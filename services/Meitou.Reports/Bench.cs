using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Http.Features;
using Npgsql;

namespace Meitou.Reports;

/// <summary>
/// The launcher's benchmarks: a session (one scenario at one tier on one machine and build, two runs) is submitted for a benchmark target,
/// every session is served to the site, and the old Python kit's results are imported by an admin. Senders are stored as the salted hash of the
/// launcher's install id only.
/// </summary>
sealed class Bench(Store store, Releases releases, Reporters reporters)
{
    #region Submit

    /// <summary>
    /// The quality tiers a session may be of (the game's <c>QualityTiers</c>).
    /// </summary>
    static readonly string[] Tiers = ["low", "medium", "high", "ultra", "extreme"];

    /// <summary>
    /// The platforms a session may run on: the Linux build, the Windows build under Proton, Windows, macOS.
    /// </summary>
    static readonly string[] Platforms = ["native", "proton", "windows", "macos"];

    /// <summary>
    /// The longest scenario id (lower-case letters, digits and hyphens).
    /// </summary>
    const int ScenarioMax = 40;

    /// <summary>
    /// The largest submission (bytes), and the longest summary.md kept (characters).
    /// </summary>
    const int BodyMax = 2_000_000, SummaryMax = 256_000;

    /// <summary>
    /// The result.json schema a run must have.
    /// </summary>
    const int ResultSchema = 1;

    /// <summary>
    /// Validates and stores a submitted session; returns its id, or why it was refused.
    /// </summary>
    public async Task<(long? Id, string? Error)> Submit(HttpRequest request)
    {
        var (body, error) = await ReadJson(request, ReportsJson.Default.BenchBody, BodyMax);
        if (body is null) return (null, error);

        var (row, refused) = await Validate(body);
        if (row is null) return (null, refused);

        long id;
        try
        {
            id = await store.AddBenchSession(row);
        }
        catch (PostgresException e) when (e.SqlState is PostgresErrorCodes.InvalidTextRepresentation or PostgresErrorCodes.UntranslatableCharacter)
        {
            return (null, "the runs are not JSON PostgreSQL can store");
        }

        _cache = null;
        return (id, null);
    }

    /// <summary>
    /// The row a submission is stored as, or why it is refused.
    /// </summary>
    async Task<(BenchRow? Row, string? Error)> Validate(BenchBody b)
    {
        string? installId = Feedback.Clean(b.InstallId, 100, lines: false);
        if (installId is null) return (null, "no installId");
        if (b.Platform is not { } platform || !Platforms.Contains(platform))
            return (null, $"platform must be one of {string.Join(", ", Platforms)}");
        if (CleanMachine(b.Machine) is not { } machine) return (null, "machine needs an id");
        if (b.Summary is { Length: > SummaryMax }) return (null, $"summary over {SummaryMax} characters");

        var (key, error) = SessionKey(b.Runs);
        if (key is null) return (null, error);
        if (await TargetError(b.Target, key.Game) is { } targetError) return (null, targetError);

        string handle = Feedback.Clean(b.Handle, 40, lines: false) ?? "";
        string data = $"{{\"runs\":[{b.Runs![0].GetRawText()},{b.Runs[1].GetRawText()}]}}";
        string machineJson = JsonSerializer.Serialize(machine, ReportsJson.Default.BenchMachine);
        string sender = reporters.Hash($"install:{installId}");
        string fingerprint = BenchFingerprints.Current(key.Fingerprint);
        var row = new BenchRow(null, sender, handle, machineJson, platform, key.Scenario, key.Game, key.Tier, key.Version, fingerprint,
            Legacy: false, data, b.Summary);
        return (row, null);
    }

    /// <summary>
    /// What both runs share, or what is wrong with them: there must be two, each valid, alike in scenario, tier, version, fingerprint and game
    /// version.
    /// </summary>
    static (BenchRunKey? Key, string? Error) SessionKey(List<JsonElement>? runs)
    {
        if (runs is not [var first, var second]) return (null, "runs must hold two result.json objects");

        var (key, error) = RunKey(first);
        if (key is null) return (null, $"run 1: {error}");

        var (other, otherError) = RunKey(second);
        if (other is null) return (null, $"run 2: {otherError}");
        if (other != key) return (null, "the two runs differ in scenario, tier, benchmark version, fingerprint or game version");

        return (key, null);
    }

    /// <summary>
    /// The fields of a result.json the service relies on, or what is wrong with them; the rest of the run is stored unread.
    /// </summary>
    static (BenchRunKey? Key, string? Error) RunKey(JsonElement run)
    {
        if (run.ValueKind != JsonValueKind.Object) return (null, "not an object");
        if (Int(run, "schema") != ResultSchema) return (null, $"schema must be {ResultSchema}");
        if (Text(run, "scenario") is not { } scenario || !IsScenarioId(scenario))
            return (null, $"scenario must be up to {ScenarioMax} lower-case letters, digits and hyphens");
        if (Int(run, "benchmarkVersion") is not { } version || version < 1) return (null, "benchmarkVersion must be 1 or more");
        if (Text(run, "fingerprint") is not { } fingerprint || !IsSha256(fingerprint))
            return (null, "fingerprint must be 64 lower-case hex digits");
        if (Text(run, "tier") is not { } tier || !Tiers.Contains(tier)) return (null, $"tier must be one of {string.Join(", ", Tiers)}");
        if (!run.TryGetProperty("game", out var game) || Text(game, "version") is not { Length: > 0 } gameVersion)
            return (null, "no game.version");
        if (!run.TryGetProperty("groups", out var groups) || groups.ValueKind != JsonValueKind.Object) return (null, "no groups");

        return (new BenchRunKey(scenario, tier, version, fingerprint, gameVersion), null);
    }

    /// <summary>
    /// Why <paramref name="target"/> can't be submitted for, null when it is a benchmark target that the runs' game version matches.
    /// </summary>
    async Task<string?> TargetError(string? target, string gameVersion)
    {
        if (string.IsNullOrEmpty(target)) return "no target";
        if (gameVersion != target) return $"the runs are of game {gameVersion}, not the target {target}";
        if (await releases.Targets() is not { } targets) return "the game's releases can't be read now; try again later";
        if (!targets.Any(t => t.Version == target)) return $"{target} is not a benchmark target (full releases and daily builds)";

        return null;
    }

    /// <summary>
    /// The machine record with its texts cleaned and cut; null when it has no id.
    /// </summary>
    static BenchMachine? CleanMachine(BenchMachine? m)
    {
        if (m is null || Feedback.Clean(m.Id, 64, lines: false) is not { } id) return null;

        string? Short(string? s) => Feedback.Clean(s, 200, lines: false);
        double? ram = m.RamGb is >= 0 and < 1_000_000 ? m.RamGb : null;
        string? notes = Feedback.Clean(m.Notes, 1000, lines: true);
        return new BenchMachine(id, Short(m.Name), Short(m.Cpu), Short(m.Gpu), ram, Short(m.Os), Short(m.OsFamily), Short(m.Resolution), notes);
    }

    /// <summary>
    /// The integer property <paramref name="name"/> of <paramref name="e"/>, null when missing or not an integer.
    /// </summary>
    static int? Int(JsonElement e, string name)
    {
        return e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            && v.TryGetInt32(out int i)
            ? i
            : null;
    }

    /// <summary>
    /// The string property <paramref name="name"/> of <paramref name="e"/>, null when missing or not a string.
    /// </summary>
    static string? Text(JsonElement e, string name)
    {
        return e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    /// <summary>
    /// Whether <paramref name="s"/> is a SHA-256 in lower-case hex.
    /// </summary>
    static bool IsSha256(string s) => s.Length == 64 && s.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    /// <summary>
    /// Whether <paramref name="s"/> has the form of a scenario id: 1 to <see cref="ScenarioMax"/> lower-case letters, digits and hyphens.
    /// </summary>
    static bool IsScenarioId(string s) => s.Length is > 0 and <= ScenarioMax && s.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'z' or '-');

    #endregion

    #region Data

    /// <summary>
    /// How long the serialised sessions are reused.
    /// </summary>
    static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The serialised sessions and when they were read; null when stale (a write clears it).
    /// </summary>
    BenchCache? _cache;

    /// <summary>
    /// <c>GET /bench/data</c> as JSON text: every session, newest first, read again when older than <see cref="CacheFor"/>.
    /// </summary>
    public async Task<string> Data()
    {
        if (_cache is { } cached && DateTimeOffset.UtcNow - cached.At < CacheFor) return cached.Json;

        var sessions = await store.BenchSessions();
        string json = JsonSerializer.Serialize(new BenchData(sessions), ReportsJson.Default.BenchData);
        _cache = new BenchCache(json, DateTimeOffset.UtcNow);
        return json;
    }

    #endregion

    #region Legacy

    /// <summary>
    /// The largest legacy import (bytes): Cloudflare in front of the service takes at most 100 MB a request.
    /// </summary>
    const int LegacyMax = 64_000_000;

    /// <summary>
    /// Replaces every legacy session with the posted ones (<c>{sessions: [...]}</c>, each as <c>GET /bench/data</c> lists them, the id ignored);
    /// returns how many were stored, or why the import was refused.
    /// </summary>
    public async Task<(int? Count, string? Error)> ImportLegacy(HttpRequest request)
    {
        var (body, error) = await ReadJson(request, ReportsJson.Default.LegacyBody, LegacyMax);
        if (body is null) return (null, error);
        if (body.Sessions is not { } sessions) return (null, "no sessions");

        var rows = new List<BenchRow>(sessions.Count);
        for (int i = 0; i < sessions.Count; i++)
        {
            if (LegacyRow(sessions[i]) is not { } row)
                return (null, $"session {i}: needs machine and data objects, platform, scenario, target and tier");

            rows.Add(row);
        }

        await store.ReplaceLegacyBenchSessions(rows);
        _cache = null;
        return (rows.Count, null);
    }

    /// <summary>
    /// The row a legacy session is stored as (no sender, no summary), null when a field it needs is missing.
    /// </summary>
    static BenchRow? LegacyRow(BenchSession s)
    {
        if (s.Machine.ValueKind != JsonValueKind.Object || s.Data.ValueKind != JsonValueKind.Object) return null;
        string?[] needed = [s.Platform, s.Scenario, s.Target, s.Tier];
        if (needed.Any(string.IsNullOrEmpty)) return null;

        DateTimeOffset? at = s.At == default ? null : s.At;
        return new BenchRow(at, "", s.Handle ?? "", s.Machine.GetRawText(), s.Platform, s.Scenario, s.Target, s.Tier, s.Version,
            s.Fingerprint ?? "", Legacy: true, s.Data.GetRawText(), null);
    }

    #endregion

    #region Request bodies

    /// <summary>
    /// Reads a JSON request body of at most <paramref name="max"/> bytes; the value, or why it can't be read.
    /// </summary>
    static async Task<(T? Value, string? Error)> ReadJson<T>(HttpRequest request, JsonTypeInfo<T> type, long max) where T : class
    {
        string tooLarge = $"over {max / 1_000_000} MB";
        if (request.ContentLength > max) return (null, tooLarge);
        if (request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit) limit.MaxRequestBodySize = max;

        try
        {
            var value = await JsonSerializer.DeserializeAsync(request.Body, type);
            return value is null ? (null, "empty body") : (value, null);
        }
        catch (JsonException e)
        {
            return (null, $"not valid JSON: {e.Message}");
        }
        catch (BadHttpRequestException e) when (e.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return (null, tooLarge);
        }
    }

    #endregion
}

/// <summary>
/// The machine a session ran on, as the launcher keeps it: its id and name (chosen once), hardware, system, the resolution rendered at and notes.
/// </summary>
sealed record BenchMachine(
    string? Id, string? Name, string? Cpu, string? Gpu, double? RamGb, string? Os, string? OsFamily, string? Resolution, string? Notes);

/// <summary>
/// The JSON body of <c>POST /bench</c>: who sends (the launcher's install id, hashed; a free handle), the machine, the platform, the target's
/// version, the two runs' result.json and run 2's summary.md.
/// </summary>
sealed record BenchBody(
    string? InstallId, string? Handle, BenchMachine? Machine, string? Platform, string? Target, List<JsonElement>? Runs, string? Summary);

/// <summary>
/// <c>POST /bench</c>: the stored session's id.
/// </summary>
sealed record BenchCreated(long Id);

/// <summary>
/// A stored session as <c>GET /bench/data</c> serves it (no sender, no summary); <c>data</c> is <c>{runs: [result, result]}</c>, or for a legacy
/// session the old kit's CSV rows. Posted to the legacy import without an id.
/// </summary>
sealed record BenchSession(
    long Id, DateTimeOffset At, string Handle, JsonElement Machine, string Platform, string Scenario, string Target, string Tier, int Version,
    string Fingerprint, bool Legacy, JsonElement Data);

/// <summary>
/// <c>GET /bench/data</c>: every session, newest first.
/// </summary>
sealed record BenchData(List<BenchSession> Sessions);

/// <summary>
/// The JSON body of <c>POST /admin/bench/legacy</c>: the legacy sessions that replace all stored ones.
/// </summary>
sealed record LegacyBody(List<BenchSession>? Sessions);

/// <summary>
/// <c>POST /admin/bench/legacy</c>: how many sessions were stored.
/// </summary>
sealed record LegacyImported(int Imported);

/// <summary>
/// What the two runs of a session must share: the scenario, the tier, the benchmark version, the workload's fingerprint and the game version.
/// </summary>
sealed record BenchRunKey(string Scenario, string Tier, int Version, string Fingerprint, string Game);

/// <summary>
/// The sessions as <c>GET /bench/data</c> serves them, and when they were read.
/// </summary>
sealed record BenchCache(string Json, DateTimeOffset At);
