namespace Meitou.Launcher.Benchmarks;

/// <summary>
/// The quality tiers a session benchmarks (the game's presets, lower case as <c>--benchmark</c> takes them) and the names of the scenario
/// groups a <c>result.json</c> reports.
/// </summary>
static class BenchmarkTiers
{
    /// <summary>
    /// Every tier, cheapest first.
    /// </summary>
    public static readonly string[] All = ["low", "medium", "high", "ultra", "extreme"];

    /// <summary>
    /// The tier ticked by default besides the recommended one.
    /// </summary>
    public const string Default = "medium";

    /// <summary>
    /// The launches a session makes of one tier.
    /// </summary>
    public const int RunsPerTier = 2;

    /// <summary>
    /// About how long one launch of a scenario takes when the build's list is not known yet (the bases play bench, with loading).
    /// </summary>
    public const int MinutesPerRun = 8;

    /// <summary>
    /// Whether <paramref name="tier"/> uses traced switches, so the game refuses it (exit code 5) on a device without ray queries.
    /// </summary>
    public static bool NeedsRayQueries(string tier) => tier is "ultra" or "extreme";

    /// <summary>
    /// The tier of a <c>--recommend-preset</c> preset name (<c>High</c>), null for one that is no tier (Custom).
    /// </summary>
    public static string? FromPreset(string? preset) =>
        preset?.ToLowerInvariant() is { } tier && All.Contains(tier) ? tier : null;

    /// <summary>
    /// <paramref name="tier"/>'s name in the launcher's language.
    /// </summary>
    public static string Label(string tier) => tier switch
    {
        "low" => LauncherText.Translate("Low"),
        "medium" => LauncherText.Translate("Medium"),
        "high" => LauncherText.Translate("High"),
        "ultra" => LauncherText.Translate("Ultra"),
        "extreme" => LauncherText.Translate("Extreme"),
        _ => tier,
    };

    /// <summary>
    /// <paramref name="group"/>'s name in the launcher's language; a group this launcher does not know (a newer scenario's) by its id.
    /// </summary>
    public static string GroupLabel(string group) => group switch
    {
        "all" => LauncherText.Translate("Whole run"),
        "camera" => LauncherText.Translate("Camera"),
        "streaming" => LauncherText.Translate("Streaming"),
        "simulation" => LauncherText.Translate("Simulation"),
        "ui" => LauncherText.Translate("Interface"),
        "views" => LauncherText.Translate("Views"),
        "flight" => LauncherText.Translate("Flight"),
        _ => group,
    };
}
