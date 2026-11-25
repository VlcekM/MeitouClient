using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace Meitou.Reports;

/// <summary>
/// One in-game feedback form: what the player wrote, the game's details, the attachments asked for and uploaded, the issue it became.
/// </summary>
sealed record FeedbackRow(
    long Id, DateTimeOffset At, string Token, string Title, string? Body, string Version, string? Commit, string Platform, string[] Mods,
    string[] Wants, long? ScreenshotSize, long? SaveSize, long? LogSize,
    int? Issue, string? IssueUrl, string? Discord, long? NavmeshSize, string? Location, string[]? Workshop);

/// <summary>
/// A benchmark session to store: <paramref name="Machine"/> and <paramref name="Data"/> are JSON text, <paramref name="At"/> null for now.
/// </summary>
sealed record BenchRow(
    DateTimeOffset? At, string Sender, string Handle, string Machine, string Platform, string Scenario, string Target, string Tier, int Version,
    string Fingerprint, bool Legacy, string Data, string? Summary);

/// <summary>
/// The feedback and benchmark tables in PostgreSQL. Reporters and benchmark senders are only ever stored as salted hashes.
/// </summary>
sealed class Store(NpgsqlDataSource db)
{
    #region Schema

    /// <summary>
    /// Creates the tables and indexes when missing and adds the columns later versions use.
    /// </summary>
    public async Task Migrate()
    {
        await using var cmd = db.CreateCommand("""
            create table if not exists feedback (
                id bigserial primary key,
                at timestamptz not null default now(),
                token text not null unique,
                reporter text not null,
                title text not null,
                body text,
                version text not null,
                commit text,
                platform text not null,
                mods text[] not null,
                wants text[] not null,
                screenshot_size bigint,
                save_size bigint,
                log_size bigint,
                issue integer,
                issue_url text
            );
            create index if not exists feedback_reporter_at on feedback (reporter, at);
            alter table feedback add column if not exists discord text;
            alter table feedback add column if not exists navmesh_size bigint;
            alter table feedback add column if not exists location text;
            alter table feedback add column if not exists workshop text[];
            create table if not exists bench_session (
                id bigserial primary key,
                at timestamptz not null default now(),
                sender text not null,
                handle text not null,
                machine jsonb not null,
                platform text not null,
                scenario text not null,
                target text not null,
                tier text not null,
                version integer not null,
                fingerprint text not null,
                legacy boolean not null default false,
                data jsonb not null,
                summary text
            );
            create index if not exists bench_session_key on bench_session (sender, target, scenario, tier, version, platform);
            create table if not exists schema_migration (
                name text primary key,
                applied_at timestamptz not null default now()
            );
            """);
        await cmd.ExecuteNonQueryAsync();
        await MigrateBenchMachineIds();
        await RenameRetiredFingerprints();
    }

    /// <summary>
    /// Gives the stored sessions of a retired fingerprint its replacement (BenchFingerprints), at every start: a fingerprint retired later is
    /// renamed by the next deploy.
    /// </summary>
    async Task RenameRetiredFingerprints()
    {
        await using var connection = await db.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        foreach (var (retired, current) in BenchFingerprints.Retired)
        {
            await using var write = new NpgsqlCommand("update bench_session set fingerprint = $2 where fingerprint = $1", connection, transaction);
            write.Parameters.Add(new() { Value = retired });
            write.Parameters.Add(new() { Value = current });
            await write.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    /// <summary>
    /// One-time migration (<c>bench_machine_id_v1</c>): regenerates <c>machine.id</c> on existing non-legacy sessions from their hardware
    /// (<c>gpu</c> + <c>osFamily</c>) and a 6-hex hash of <c>sender</c>, and clears <c>machine.name</c>, matching the launcher's generated id format.
    /// </summary>
    async Task MigrateBenchMachineIds()
    {
        await using var connection = await db.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var claim = new NpgsqlCommand(
            "insert into schema_migration (name) values ('bench_machine_id_v1') on conflict do nothing", connection, transaction))
        {
            if (await claim.ExecuteNonQueryAsync() == 0) return;
        }

        var rows = new List<(long Id, string Sender, string Machine)>();
        await using (var read = new NpgsqlCommand(
            "select id, sender, machine::text from bench_session where not legacy", connection, transaction))
        await using (var r = await read.ExecuteReaderAsync())
        {
            while (await r.ReadAsync()) rows.Add((r.GetInt64(0), r.GetString(1), r.GetString(2)));
        }

        foreach (var (id, sender, machineJson) in rows)
        {
            if (JsonSerializer.Deserialize(machineJson, ReportsJson.Default.BenchMachine) is not { } machine) continue;

            var updated = machine with { Id = RegeneratedMachineId(sender, machine), Name = "" };
            string json = JsonSerializer.Serialize(updated, ReportsJson.Default.BenchMachine);
            await using var write = new NpgsqlCommand("update bench_session set machine = $2::jsonb where id = $1", connection, transaction);
            write.Parameters.Add(new() { Value = id });
            write.Parameters.Add(new() { Value = json, NpgsqlDbType = NpgsqlDbType.Text });
            await write.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
    }

    /// <summary>
    /// Deterministic machine id for an existing session: <c>&lt;gpu-os-slug&gt;-&lt;6-hex&gt;</c> (max 64 chars), hashed over <paramref name="sender"/>
    /// and hardware so identical-spec machines from different senders get distinct ids while one sender's sessions stay grouped.
    /// </summary>
    static string RegeneratedMachineId(string sender, BenchMachine machine)
    {
        const int maxLength = 64, suffixHex = 6;
        string gpu = machine.Gpu ?? "", osFamily = machine.OsFamily ?? "";
        string raw = new([.. $"{gpu} {osFamily}".ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-')]);
        string slug = string.Join('-', raw.Split('-', StringSplitOptions.RemoveEmptyEntries));
        if (slug.Length == 0) slug = "pc";

        int maxSlug = maxLength - suffixHex - 1;
        if (slug.Length > maxSlug) slug = slug[..maxSlug].TrimEnd('-');

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes($"meitou-machine-v1:{sender}:{gpu}:{osFamily}"));
        string suffix = Convert.ToHexStringLower(hash.AsSpan(0, suffixHex / 2));
        return $"{slug}-{suffix}";
    }

    #endregion

    #region Feedback

    /// <summary>
    /// Stores a new feedback form under its token (not yet sent: no issue); <paramref name="workshop"/> holds each mod's workshop item id ("" none).
    /// </summary>
    public async Task AddFeedback(string token, string reporter, string title, string? body, string version, string? commit, string platform,
        string[] mods, string[] workshop, string[] wants, string? discord, string? location)
    {
        await using var cmd = db.CreateCommand("""
            insert into feedback (token, reporter, title, body, version, commit, platform, mods, wants, discord, location, workshop)
            values ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12)
            """);
        cmd.Parameters.Add(new() { Value = token });
        cmd.Parameters.Add(new() { Value = reporter });
        cmd.Parameters.Add(new() { Value = title });
        cmd.Parameters.Add(new() { Value = (object?)body ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        cmd.Parameters.Add(new() { Value = version });
        cmd.Parameters.Add(new() { Value = (object?)commit ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        cmd.Parameters.Add(new() { Value = platform });
        cmd.Parameters.Add(new() { Value = mods, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text });
        cmd.Parameters.Add(new() { Value = wants, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text });
        cmd.Parameters.Add(new() { Value = (object?)discord ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        cmd.Parameters.Add(new() { Value = (object?)location ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        cmd.Parameters.Add(new() { Value = workshop, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text });
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// How many feedback forms <paramref name="reporter"/> started since <paramref name="since"/>, sent or not.
    /// </summary>
    public async Task<int> FeedbackCountBy(string reporter, DateTimeOffset since)
    {
        await using var cmd = db.CreateCommand("select count(*) from feedback where reporter = $1 and at >= $2");
        cmd.Parameters.Add(new() { Value = reporter });
        cmd.Parameters.Add(new() { Value = since });
        return (int)(long)(await cmd.ExecuteScalarAsync())!;
    }

    /// <summary>
    /// The feedback form of <paramref name="token"/>, null when there is none.
    /// </summary>
    public async Task<FeedbackRow?> GetFeedback(string token)
    {
        await using var cmd = db.CreateCommand("""
            select id, at, title, body, version, commit, platform, mods, wants, screenshot_size, save_size, log_size, issue, issue_url, discord,
                navmesh_size, location, workshop
            from feedback where token = $1
            """);
        cmd.Parameters.Add(new() { Value = token });
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return null;
        long? Size(int i) => r.IsDBNull(i) ? null : r.GetInt64(i);
        return new(r.GetInt64(0), r.GetFieldValue<DateTimeOffset>(1), token, r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetString(4),
            r.IsDBNull(5) ? null : r.GetString(5), r.GetString(6), r.GetFieldValue<string[]>(7), r.GetFieldValue<string[]>(8),
            Size(9), Size(10), Size(11), r.IsDBNull(12) ? null : r.GetInt32(12), r.IsDBNull(13) ? null : r.GetString(13), r.IsDBNull(14) ? null
                    : r.GetString(14),
            Size(15), r.IsDBNull(16) ? null : r.GetString(16), r.IsDBNull(17) ? null : r.GetFieldValue<string[]>(17));
    }

    /// <summary>
    /// Records an attachment's size (<paramref name="kind"/> is one of <see cref="Feedback.Kinds"/>).
    /// </summary>
    public async Task SetFeedbackUpload(string token, string kind, long size)
    {
        string column = kind switch { "screenshot" => "screenshot_size", "navmesh" => "navmesh_size", "save" => "save_size", "log" => "log_size",
                _ => throw new ArgumentException(kind) };
        await using var cmd = db.CreateCommand($"update feedback set {column} = $2 where token = $1");
        cmd.Parameters.Add(new() { Value = token });
        cmd.Parameters.Add(new() { Value = size });
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Records the GitHub issue a form became.
    /// </summary>
    public async Task SetFeedbackIssue(string token, int number, string url)
    {
        await using var cmd = db.CreateCommand("update feedback set issue = $2, issue_url = $3 where token = $1");
        cmd.Parameters.Add(new() { Value = token });
        cmd.Parameters.Add(new() { Value = number });
        cmd.Parameters.Add(new() { Value = url });
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Removes the forms started before <paramref name="before"/> and never sent; returns their tokens (whose files go too).
    /// </summary>
    public async Task<List<string>> DeleteFeedbackDrafts(DateTimeOffset before)
    {
        await using var cmd = db.CreateCommand("delete from feedback where issue is null and at < $1 returning token");
        cmd.Parameters.Add(new() { Value = before });
        var tokens = new List<string>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) tokens.Add(r.GetString(0));
        return tokens;
    }

    /// <summary>
    /// Deletes a feedback form; false when there was none.
    /// </summary>
    public async Task<bool> DeleteFeedback(string token)
    {
        await using var cmd = db.CreateCommand("delete from feedback where token = $1");
        cmd.Parameters.Add(new() { Value = token });
        return await cmd.ExecuteNonQueryAsync() > 0;
    }

    #endregion

    #region Benchmarks

    /// <summary>
    /// How many sessions one sender keeps per target, scenario, tier, benchmark version and platform; a new one beyond it replaces the oldest.
    /// </summary>
    const int SessionsPerKey = 2;

    /// <summary>
    /// Stores a submitted session, first deleting the sender's oldest ones of the same target, scenario, tier, version and platform so that at most
    /// <see cref="SessionsPerKey"/> remain; returns its id.
    /// </summary>
    public async Task<long> AddBenchSession(BenchRow row)
    {
        await using var connection = await db.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await TrimBenchSessions(connection, row);
        long id = await InsertBenchSession(connection, row);
        await transaction.CommitAsync();
        return id;
    }

    /// <summary>
    /// Replaces every legacy session with <paramref name="rows"/> in one transaction, so a failed import keeps the old ones.
    /// </summary>
    public async Task ReplaceLegacyBenchSessions(IReadOnlyList<BenchRow> rows)
    {
        await using var connection = await db.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var delete = new NpgsqlCommand("delete from bench_session where legacy", connection))
        {
            await delete.ExecuteNonQueryAsync();
        }

        foreach (var row in rows) await InsertBenchSession(connection, row);
        await transaction.CommitAsync();
    }

    /// <summary>
    /// Every benchmark session without its sender and summary, newest first.
    /// </summary>
    public async Task<List<BenchSession>> BenchSessions()
    {
        await using var cmd = db.CreateCommand("""
            select id, at, handle, machine::text, platform, scenario, target, tier, version, fingerprint, legacy, data::text
            from bench_session order by at desc, id desc
            """);
        await using var r = await cmd.ExecuteReaderAsync();
        var sessions = new List<BenchSession>();
        while (await r.ReadAsync()) sessions.Add(ReadBenchSession(r));
        return sessions;
    }

    /// <summary>
    /// Deletes the non-legacy sessions of <paramref name="row"/>'s sender and key past the newest <see cref="SessionsPerKey"/> − 1.
    /// </summary>
    static async Task TrimBenchSessions(NpgsqlConnection connection, BenchRow row)
    {
        await using var cmd = new NpgsqlCommand("""
            delete from bench_session where id in (
                select id from bench_session
                where not legacy and sender = $1 and target = $2 and tier = $3 and version = $4 and platform = $5 and scenario = $6
                order by at desc, id desc offset $7)
            """, connection);
        cmd.Parameters.Add(new() { Value = row.Sender });
        cmd.Parameters.Add(new() { Value = row.Target });
        cmd.Parameters.Add(new() { Value = row.Tier });
        cmd.Parameters.Add(new() { Value = row.Version });
        cmd.Parameters.Add(new() { Value = row.Platform });
        cmd.Parameters.Add(new() { Value = row.Scenario });
        cmd.Parameters.Add(new() { Value = (long)(SessionsPerKey - 1) });
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Inserts one session (the JSON columns bound as text and cast: no JSON plugin under AOT); returns its id.
    /// </summary>
    static async Task<long> InsertBenchSession(NpgsqlConnection connection, BenchRow row)
    {
        await using var cmd = new NpgsqlCommand("""
            insert into bench_session (at, sender, handle, machine, platform, scenario, target, tier, version, fingerprint, legacy, data, summary)
            values (coalesce($1, now()), $2, $3, $4::jsonb, $5, $6, $7, $8, $9, $10, $11, $12::jsonb, $13)
            returning id
            """, connection);
        cmd.Parameters.Add(new() { Value = (object?)row.At ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.TimestampTz });
        cmd.Parameters.Add(new() { Value = row.Sender });
        cmd.Parameters.Add(new() { Value = row.Handle });
        cmd.Parameters.Add(new() { Value = row.Machine, NpgsqlDbType = NpgsqlDbType.Text });
        cmd.Parameters.Add(new() { Value = row.Platform });
        cmd.Parameters.Add(new() { Value = row.Scenario });
        cmd.Parameters.Add(new() { Value = row.Target });
        cmd.Parameters.Add(new() { Value = row.Tier });
        cmd.Parameters.Add(new() { Value = row.Version });
        cmd.Parameters.Add(new() { Value = row.Fingerprint });
        cmd.Parameters.Add(new() { Value = row.Legacy });
        cmd.Parameters.Add(new() { Value = row.Data, NpgsqlDbType = NpgsqlDbType.Text });
        cmd.Parameters.Add(new() { Value = (object?)row.Summary ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    /// <summary>
    /// The session at the reader's row, its JSON columns parsed.
    /// </summary>
    static BenchSession ReadBenchSession(NpgsqlDataReader r)
    {
        return new BenchSession(
            r.GetInt64(0),
            r.GetFieldValue<DateTimeOffset>(1),
            r.GetString(2),
            ParseJson(r.GetString(3)),
            r.GetString(4),
            r.GetString(5),
            r.GetString(6),
            r.GetString(7),
            r.GetInt32(8),
            r.GetString(9),
            r.GetBoolean(10),
            ParseJson(r.GetString(11)));
    }

    /// <summary>
    /// JSON text read from a <c>jsonb</c> column as an element that outlives its document.
    /// </summary>
    static JsonElement ParseJson(string text) => JsonSerializer.Deserialize(text, ReportsJson.Default.JsonElement);

    #endregion
}
