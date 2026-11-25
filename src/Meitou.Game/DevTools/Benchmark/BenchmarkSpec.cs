using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Meitou.Game.Host;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Settings;

namespace Meitou.Game.DevTools.Benchmark;

/// <summary>
/// A launcher benchmark scenario: its id, how it starts, its save (null: a new game), its segments in order with their seconds (NaN: set by
/// the world) and its groups of segments, "all" last.
/// </summary>
sealed record BenchmarkScenario(string Id, string Start, string? Save, string? SaveHash, IReadOnlyList<(string Name, double Seconds)> Timeline,
    IReadOnlyList<(string Name, string[] Segments)> Groups)
{
    /// <summary>
    /// The seconds measured (every segment but settle; one whose length the world sets is left out).
    /// </summary>
    public double MeasuredSeconds => Timeline.Where(s => s.Name != BenchmarkSpec.SettleSegment && !double.IsNaN(s.Seconds)).Sum(s => s.Seconds);
}

/// <summary>
/// The launcher benchmark's static spec (<c>--benchmark</c>, docs/bench.md "Launcher benchmark"): its version, scenarios, fixed settings and
/// metrics, and a scenario's fingerprint of all that with the default user settings and the tier tables. Nothing in it reads the machine, the
/// device or the saved settings.
/// </summary>
static class BenchmarkSpec
{
    #region Spec

    /// <summary>
    /// The benchmark's version: bumped by hand whenever the workload changes (tier tables, fixed settings, default user settings that are not
    /// BenchmarkNeutral, a scenario's start, segments or groups, the save, metric definitions); faster or slower code never bumps it. The
    /// fingerprint tells a forgotten bump.
    /// </summary>
    public const int Version = 1;

    /// <summary>
    /// The version of result.json's layout.
    /// </summary>
    public const int Schema = 1;

    /// <summary>
    /// The exit code of a tier that needs ray queries on a device without them.
    /// </summary>
    public const int NoRayQueriesExit = 5;

    /// <summary>
    /// The exit code of a save whose hash is not the scenario's.
    /// </summary>
    public const int SaveMismatchExit = 6;

    /// <summary>
    /// The exit code of an unknown scenario.
    /// </summary>
    public const int UnknownScenarioExit = 7;

    /// <summary>
    /// The scenario when <c>--benchmark-scenario</c> names none.
    /// </summary>
    public const string DefaultScenario = "bases";

    /// <summary>
    /// The segment every script starts with, never counted.
    /// </summary>
    public const string SettleSegment = "settle";

    /// <summary>
    /// <c>--benchmark-list &lt;file.json&gt;</c>, the only arguments: writes the scenarios and exits.
    /// </summary>
    public const string ListFlag = "--benchmark-list";

    /// <summary>
    /// How the group metrics are computed (BenchmarkResult), as the fingerprint reads them.
    /// </summary>
    const string Metrics = "frames.csv rows of the group's segments; percentile = sorted value at index round(p/100*(n-1)); "
        + "avgFps = 1000 / mean frame_ms, 1 decimal; over33Pct = share of frame_ms over 33 in %; over100 = frames over 100 ms; "
        + "slowWorld = sim_slow-world p99 and max; peaks of vram_mb and working_set_mb; stages = p50 and p99 of every cpu_* and gpu_* column, "
        + "finite values only";

    /// <summary>
    /// The settings every run has whatever the scenario and tier (GameOptions, WorldOptions.ApplyBenchmarkTier, QualityTiers.ApplyBenchmarkTier).
    /// </summary>
    static readonly string[] FixedSettings =
    [
        string.Create(CultureInfo.InvariantCulture,
            $"window {WorldOptions.BenchmarkBaselineSize.Width}x{WorldOptions.BenchmarkBaselineSize.Height}, windowed"),
        "frame limit off, vsync off, HDR off",
        "renderer vulkan",
        "no mods: base data and core mods only, no code mods",
        "look Meitou",
        "anti-aliasing: Low its own at its render scale; the other tiers TAA for an upscaler, render scale 1",
        "ray queries and indirect light only for a tier with traced switches",
        "texture quality: the tier's table value, before the device",
        "scene-npc " + GameOptions.DefaultSceneNpc,
        "the whole script; settings neither read nor written; no autosave",
    ];

    /// <summary>
    /// <c>bases</c>: the play bench's whole script (PlayBenchScript) on the frozen save <c>bench-bases-2026-10-27</c>.
    /// </summary>
    static readonly BenchmarkScenario Bases = new(DefaultScenario, "load the save", "bench-bases-2026-10-27",
        "ceb2fb186fcfce7d3841a947914eac89f37547b028fbf05fb760aa8613dac4ff", PlayBenchScript.Timeline,
        WithAll(PlayBenchScript.Timeline,
        [
            ("camera", ["idle", "orbit", "zoom", "pan"]),
            ("streaming", ["travel", "return"]),
            ("simulation",
                ["speed-x2", "speed-x5", "talk", "trade", "container", "doors", "pick-up", "paths", "walk", "fight", "fight-on", "loot"]),
            ("ui", ["panels", "panels-warm"]),
            ("views", ["horizon", "overview", "flights"]),
        ]));

    /// <summary>
    /// <c>shark-flyby</c>: a new game's camera flown round the town Shark (SharkFlyby).
    /// </summary>
    static readonly BenchmarkScenario SharkFlybyScenario = new(SharkFlyby.Id, SharkFlyby.Start, null, null, SharkFlyby.Timeline,
        WithAll(SharkFlyby.Timeline, [("flight", [.. SharkFlyby.Timeline.Select(s => s.Name).Where(n => n != SettleSegment)])]));

    /// <summary>
    /// The scenarios, the default first.
    /// </summary>
    public static IReadOnlyList<BenchmarkScenario> Scenarios { get; } = [Bases, SharkFlybyScenario];

    /// <summary>
    /// The scenario <paramref name="id"/>; null for an unknown one.
    /// </summary>
    public static BenchmarkScenario? Find(string id) => Scenarios.FirstOrDefault(s => s.Id == id);

    /// <summary>
    /// <paramref name="groups"/> and "all" (every segment of <paramref name="timeline"/> but settle).
    /// </summary>
    static (string Name, string[] Segments)[] WithAll(IReadOnlyList<(string Name, double Seconds)> timeline,
        (string Name, string[] Segments)[] groups) =>
        [.. groups, ("all", [.. timeline.Select(s => s.Name).Where(n => n != SettleSegment)])];

    #endregion

    #region Tiers

    /// <summary>
    /// The tier named <paramref name="name"/> (low, medium, high, ultra, extreme) as a QualityTiers preset; null for any other name.
    /// </summary>
    public static int? TierOf(string name)
    {
        for (int tier = 0; tier < QualityTiers.CustomPreset; tier++)
        {
            if (TierName(tier) == name.ToLowerInvariant()) return tier;
        }

        return null;
    }

    /// <summary>
    /// Tier <paramref name="tier"/>'s name in result.json and on the command line.
    /// </summary>
    public static string TierName(int tier) => QualityTiers.PresetNames[tier].ToLowerInvariant();

    /// <summary>
    /// Tier <paramref name="tier"/>'s texture quality, from its table row (set before the device).
    /// </summary>
    public static int TextureQuality(int tier)
    {
        var row = QualityTiers.PresetSliders.First(p => p.Label == WorldFrame.TextureQualitySlider);
        return (int)(QualityTiers.TableValue(row, tier) ?? 0);
    }

    #endregion

    #region Fingerprint and list

    /// <summary>
    /// <paramref name="scenario"/>'s fingerprint: SHA-256 hex of <see cref="CanonicalText"/>.
    /// </summary>
    public static string Fingerprint(BenchmarkScenario scenario) => Sha256Hex(CanonicalText(scenario));

    /// <summary>
    /// The applied hash: SHA-256 hex of <paramref name="settings"/> (key and value) sorted by key, one <c>key=value\n</c> each.
    /// </summary>
    public static string AppliedHash(IEnumerable<(string Key, string Value)> settings) =>
        Sha256Hex(string.Concat(settings.OrderBy(s => s.Key, StringComparer.Ordinal).Select(s => $"{s.Key}={s.Value}\n")));

    /// <summary>
    /// SHA-256 of <paramref name="text"/>'s UTF-8, lower-case hex.
    /// </summary>
    public static string Sha256Hex(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>
    /// <c>--benchmark-list</c>: writes the version and the scenarios (id, save, save hash, groups, measured seconds) to <paramref name="path"/>;
    /// the exit code (0; 1 when the file cannot be written).
    /// </summary>
    public static int WriteList(string path)
    {
        var scenarios = new JsonArray();
        foreach (var s in Scenarios)
        {
            scenarios.Add(new JsonObject
            {
                ["id"] = s.Id,
                ["save"] = s.Save,
                ["saveHash"] = s.SaveHash,
                ["groups"] = new JsonArray([.. s.Groups.Select(g => (JsonNode)g.Name)]),
                ["seconds"] = s.MeasuredSeconds,
            });
        }

        var list = new JsonObject { ["version"] = Version, ["scenarios"] = scenarios };
        try
        {
            File.WriteAllText(path, list.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"{ListFlag}: could not write {path}: {e.Message}");
            return 1;
        }
    }

    /// <summary>
    /// <paramref name="scenario"/>'s static spec as one text, lines ending in \n: version, metrics, the scenario (start, save, segments with a
    /// world-set length by name only, groups), fixed settings, the benchmark's user settings but the neutral ones and each tier's table.
    /// </summary>
    internal static string CanonicalText(BenchmarkScenario scenario)
    {
        var sb = new StringBuilder();
        Line(sb, $"meitou benchmark {Version}");
        Line(sb, "metrics " + Metrics);
        AppendScenario(sb, scenario);
        foreach (var setting in FixedSettings) Line(sb, "fixed " + setting);
        Line(sb, "config " + UserConfig.BenchmarkText());
        AppendTierTables(sb);
        return sb.ToString();
    }

    /// <summary>
    /// The scenario's id, start, save, segments and groups.
    /// </summary>
    static void AppendScenario(StringBuilder sb, BenchmarkScenario scenario)
    {
        Line(sb, $"scenario {scenario.Id}: {scenario.Start}");
        Line(sb, scenario.Save is null ? "save none" : $"save {scenario.Save} {scenario.SaveHash}");
        foreach (var (name, seconds) in scenario.Timeline)
            Line(sb, double.IsNaN(seconds) ? $"segment {name}" : $"segment {name} {Number(seconds)}");

        foreach (var (name, segments) in scenario.Groups) Line(sb, $"group {name}: {string.Join(',', segments)}");
    }

    /// <summary>
    /// Each tier's slider and switch targets, then the lists Low and the traced switches go by.
    /// </summary>
    static void AppendTierTables(StringBuilder sb)
    {
        for (int tier = 0; tier < QualityTiers.CustomPreset; tier++)
        {
            Line(sb, "tier " + TierName(tier));
            foreach (var p in QualityTiers.PresetSliders)
            {
                string value = QualityTiers.TableValue(p, tier) is { } v ? Number(v) : "start";
                Line(sb, $"slider {p.Label}: {value} scale={p.Scale} mode={p.Mode ?? "-"} lowFixed={p.LowFixed}");
            }

            foreach (var p in QualityTiers.PresetToggles)
            {
                string state = QualityTiers.TableSwitch(p, tier) switch { true => "on", false => "off", null => "start" };
                Line(sb, $"switch {p.Label}: {state}");
            }
        }

        Line(sb, "low off " + string.Join(',', QualityTiers.LowOffSwitches.Concat(QualityTiers.LowOffToggles)));
        Line(sb, "traced " + string.Join(',', QualityTiers.TracedSwitches));
    }

    /// <summary>
    /// <paramref name="text"/> and a \n (never the platform's newline).
    /// </summary>
    static void Line(StringBuilder sb, string text) => sb.Append(text).Append('\n');

    /// <summary>
    /// <paramref name="value"/> in the invariant culture, round-trip.
    /// </summary>
    static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>
    /// <paramref name="value"/> in the invariant culture, round-trip as a float (0.6, not its double's digits).
    /// </summary>
    static string Number(float value) => value.ToString("R", CultureInfo.InvariantCulture);

    #endregion
}
