using Npgsql;
using NpgsqlTypes;

namespace Meitou.Reports;

/// <summary>
/// One play report: a roadmap row seen working or broken on a build.
/// </summary>
sealed record Report(
    long Id, DateTimeOffset At, string Row, string Verdict, string Build, string Platform, bool? Modded,
    string? Note, string Source, string Reporter, bool Trusted, bool HasLog);

/// <summary>
/// Counts for one row since a cutoff: distinct reporters per verdict, and the newest report.
/// </summary>
sealed record RowSummary(string Row, int Works, int Broken, bool TrustedWorks, string LastVerdict, string LastBuild, DateTimeOffset LastAt);

/// <summary>
/// One in-game feedback form: what the player wrote, the game's details, the attachments asked for and uploaded, the issue it became.
/// </summary>
sealed record FeedbackRow(
    long Id, DateTimeOffset At, string Token, string Title, string? Body, string Version, string? Commit, string Platform, string[] Mods,
    string[] Wants, long? ScreenshotSize, long? SaveSize, long? LogSize,
    int? Issue, string? IssueUrl, string? Discord, long? NavmeshSize, string? Location);

/// <summary>
/// The reports and feedback tables in PostgreSQL. Reporters are only ever stored as salted hashes.
/// </summary>
sealed class Store(NpgsqlDataSource db)
{
    public async Task Migrate()
    {
        await using var cmd = db.CreateCommand("""
            create table if not exists reports (
                id bigserial primary key,
                at timestamptz not null default now(),
                row_id text not null,
                verdict text not null check (verdict in ('works', 'broken')),
                build text not null,
                platform text not null,
                modded boolean,
                note text,
                source text not null,
                reporter text not null,
                trusted boolean not null default false,
                log bytea
            );
            create index if not exists reports_row_at on reports (row_id, at);
            create index if not exists reports_reporter_at on reports (reporter, at);
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
            """);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<long> Add(string row, string verdict, string build, string platform, bool? modded, string? note, string source,
        string reporter, bool trusted, byte[]? gzippedLog)
    {
        await using var cmd = db.CreateCommand("""
            insert into reports (row_id, verdict, build, platform, modded, note, source, reporter, trusted, log)
            values ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10) returning id
            """);
        cmd.Parameters.Add(new() { Value = row });
        cmd.Parameters.Add(new() { Value = verdict });
        cmd.Parameters.Add(new() { Value = build });
        cmd.Parameters.Add(new() { Value = platform });
        cmd.Parameters.Add(new() { Value = (object?)modded ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Boolean });
        cmd.Parameters.Add(new() { Value = (object?)note ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        cmd.Parameters.Add(new() { Value = source });
        cmd.Parameters.Add(new() { Value = reporter });
        cmd.Parameters.Add(new() { Value = trusted });
        cmd.Parameters.Add(new() { Value = (object?)gzippedLog ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bytea });
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    /// <summary>
    /// How many reports <paramref name="reporter"/> made since <paramref name="since"/> (a per-person limit on top of the per-IP one).
    /// </summary>
    public async Task<int> CountBy(string reporter, DateTimeOffset since)
    {
        await using var cmd = db.CreateCommand("select count(*) from reports where reporter = $1 and at >= $2");
        cmd.Parameters.Add(new() { Value = reporter });
        cmd.Parameters.Add(new() { Value = since });
        return (int)(long)(await cmd.ExecuteScalarAsync())!;
    }

    /// <summary>
    /// The counts per row over the reports <paramref name="w"/> lets count (a Broken one dated by its build's release, else its own time).
    /// </summary>
    public async Task<List<RowSummary>> Summary(Window w)
    {
        await using var cmd = db.CreateCommand("""
            with releases as (select * from unnest($2::text[], $3::timestamptz[]) as t(build, published)),
            counted as (
                select r.* from reports r left join releases using (build)
                where r.at >= $1 and (r.verdict = 'works' or $4::timestamptz is null or coalesce(published, r.at) >= $4))
            select row_id,
                   count(distinct reporter) filter (where verdict = 'works'),
                   count(distinct reporter) filter (where verdict = 'broken'),
                   coalesce(bool_or(trusted and verdict = 'works'), false),
                   (array_agg(verdict order by at desc))[1],
                   (array_agg(build order by at desc))[1],
                   max(at)
            from counted group by row_id order by row_id
            """);
        cmd.Parameters.Add(new() { Value = w.Since });
        cmd.Parameters.Add(new() { Value = w.Builds, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text });
        cmd.Parameters.Add(new() { Value = w.Published, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.TimestampTz });
        cmd.Parameters.Add(new() { Value = (object?)w.NewestFull ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.TimestampTz });
        var list = new List<RowSummary>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            list.Add(new(r.GetString(0), (int)r.GetInt64(1), (int)r.GetInt64(2), r.GetBoolean(3), r.GetString(4), r.GetString(5),
                    r.GetFieldValue<DateTimeOffset>(6)));
        return list;
    }

    public async Task<List<Report>> List(DateTimeOffset since, string? row, int limit)
    {
        await using var cmd = db.CreateCommand("""
            select id, at, row_id, verdict, build, platform, modded, note, source, reporter, trusted, log is not null
            from reports where at >= $1 and ($2::text is null or row_id = $2) order by at desc limit $3
            """);
        cmd.Parameters.Add(new() { Value = since });
        cmd.Parameters.Add(new() { Value = (object?)row ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        cmd.Parameters.Add(new() { Value = limit });
        var list = new List<Report>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            list.Add(new(r.GetInt64(0), r.GetFieldValue<DateTimeOffset>(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5),
                r.IsDBNull(6) ? null : r.GetBoolean(6), r.IsDBNull(7) ? null
                        : r.GetString(7), r.GetString(8), r.GetString(9), r.GetBoolean(10), r.GetBoolean(11)));
        return list;
    }

    public async Task<byte[]?> Log(long id)
    {
        await using var cmd = db.CreateCommand("select log from reports where id = $1");
        cmd.Parameters.Add(new() { Value = id });
        return await cmd.ExecuteScalarAsync() as byte[];
    }

    public async Task<bool> Delete(long id)
    {
        await using var cmd = db.CreateCommand("delete from reports where id = $1");
        cmd.Parameters.Add(new() { Value = id });
        return await cmd.ExecuteNonQueryAsync() > 0;
    }

    public async Task AddFeedback(string token, string reporter, string title, string? body, string version, string? commit, string platform,
        string[] mods, string[] wants, string? discord, string? location)
    {
        await using var cmd = db.CreateCommand("""
            insert into feedback (token, reporter, title, body, version, commit, platform, mods, wants, discord, location)
            values ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11)
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

    public async Task<FeedbackRow?> GetFeedback(string token)
    {
        await using var cmd = db.CreateCommand("""
            select id, at, title, body, version, commit, platform, mods, wants, screenshot_size, save_size, log_size, issue, issue_url, discord, navmesh_size, location
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
            Size(15), r.IsDBNull(16) ? null : r.GetString(16));
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

    public async Task<bool> DeleteFeedback(string token)
    {
        await using var cmd = db.CreateCommand("delete from feedback where token = $1");
        cmd.Parameters.Add(new() { Value = token });
        return await cmd.ExecuteNonQueryAsync() > 0;
    }
}
