using System.Text.Json;
using System.Text.Json.Nodes;

namespace Meitou.Launcher.Benchmarks;

/// <summary>
/// A named benchmark a build offers (<c>--benchmark-list</c>): its id, the save it loads (null for one that starts a new game), its scenario
/// groups and about how many seconds it measures.
/// </summary>
sealed record BenchmarkScenario(string Id, string? Save, IReadOnlyList<string> Groups, double Seconds)
{
    /// <summary>
    /// The scenario ticked by default, the whole play bench on the bases save.
    /// </summary>
    public const string Default = "bases";

    /// <summary>
    /// The scenarios this launcher knows, offered before a build has listed its own; a build without one skips it with a notice.
    /// </summary>
    public static readonly IReadOnlyList<string> Known = [Default, "shark-flyby"];

    /// <summary>
    /// The file a build's list is kept in, in its folder, so it is asked for once.
    /// </summary>
    const string ListFile = "benchmark-scenarios.json";

    /// <summary>
    /// What a run costs besides the measured seconds: starting, loading and the uncounted settle.
    /// </summary>
    const double OverheadSeconds = 90;

    /// <summary>
    /// About how many minutes one run takes.
    /// </summary>
    public double RunMinutes => (Seconds + OverheadSeconds) / 60;

    /// <summary>
    /// The short name shown on tabs and checkboxes for scenario <paramref name="id"/>; an unknown one by its id.
    /// </summary>
    public static string ShortLabel(string id) => id switch
    {
        "bases" => LauncherText.Translate("Bases"),
        "shark-flyby" => LauncherText.Translate("Shark fly-by"),
        _ => id,
    };

    /// <summary>
    /// A short description of scenario <paramref name="id"/>; empty for an unknown one.
    /// </summary>
    public static string Description(string id) => id switch
    {
        "bases" => LauncherText.Translate("The play bench on a save"),
        "shark-flyby" => LauncherText.Translate("A new game, flying round Shark"),
        _ => "",
    };

    /// <summary>
    /// The name shown for scenario <paramref name="id"/>; one this launcher does not know (a newer build's) by its id.
    /// </summary>
    public static string Label(string id) => id switch
    {
        "bases" => LauncherText.Translate("Bases: the play bench on a save"),
        "shark-flyby" => LauncherText.Translate("Shark fly-by: a new game, flying round Shark"),
        _ => id,
    };

    /// <summary>
    /// The list kept in <paramref name="buildFolder"/>, null when it was not asked for yet or cannot be read.
    /// </summary>
    public static List<BenchmarkScenario>? ReadList(string buildFolder) => Parse(ListPath(buildFolder));

    /// <summary>
    /// Where <see cref="ReadList"/> looks in <paramref name="buildFolder"/>.
    /// </summary>
    public static string ListPath(string buildFolder) => Path.Combine(buildFolder, ListFile);

    /// <summary>
    /// The scenarios of a <c>--benchmark-list</c> file; null when it is missing or lists none.
    /// </summary>
    static List<BenchmarkScenario>? Parse(string file)
    {
        try
        {
            if (!File.Exists(file) || JsonNode.Parse(File.ReadAllText(file)) is not JsonObject list) return null;
            var scenarios = (list["scenarios"] as JsonArray ?? []).OfType<JsonObject>().Select(FromEntry).OfType<BenchmarkScenario>().ToList();
            return scenarios.Count > 0 ? scenarios : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// One entry of the list; null without an id.
    /// </summary>
    static BenchmarkScenario? FromEntry(JsonObject entry)
    {
        if (BenchmarkResult.Text(entry, "id") is not { } id) return null;

        var groups = (entry["groups"] as JsonArray ?? []).OfType<JsonValue>()
            .Select(value => value.TryGetValue(out string? group) ? group : null)
            .OfType<string>()
            .ToList();
        double seconds = entry["seconds"] is JsonValue s && s.TryGetValue(out double n) ? n : 0;
        return new BenchmarkScenario(id, BenchmarkResult.Text(entry, "save"), groups, seconds);
    }
}
