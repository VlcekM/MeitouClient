using Npgsql;
using NpgsqlTypes;

namespace Meitou.Reports;

/// <summary>
/// One in-game feedback form: what the player wrote, the game's details, the attachments asked for and uploaded, the issue it became.
/// </summary>
sealed record FeedbackRow(
    long Id, DateTimeOffset At, string Token, string Title, string? Body, string Version, string? Commit, string Platform, string[] Mods,
    string[] Wants, long? ScreenshotSize, long? SaveSize, long? LogSize,
    int? Issue, string? IssueUrl, string? Discord, long? NavmeshSize, string? Location);

/// <summary>
/// The feedback table in PostgreSQL. Reporters are only ever stored as salted hashes.
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
            """);
        await cmd.ExecuteNonQueryAsync();
    }

    #endregion

    #region Feedback

    /// <summary>
    /// Stores a new feedback form under its token (not yet sent: no issue).
    /// </summary>
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

    /// <summary>
    /// The feedback form of <paramref name="token"/>, null when there is none.
    /// </summary>
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
}
