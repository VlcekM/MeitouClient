using System.Globalization;
using System.Text.RegularExpressions;
using Meitou.Game.Settings;

namespace Meitou.Game.Host;

/// <summary>
/// The quality tier an Apple-silicon GPU suits (an engine choice, docs/launcher.md "Graphics"): its Geekbench 7 Metal score against the frame
/// rates an M6 measured in the launcher benchmark's bases scenario, where every tier is GPU-bound; the highest tier predicted at
/// <see cref="TargetFps"/> or more, Ultra at most and High at most without hardware ray tracing.
/// </summary>
static partial class AppleGpuTiers
{
    #region Scores

    /// <summary>
    /// The Geekbench 7 Metal score of each chip, by generation (M1 first) and variant (<see cref="Variants"/>); 0 where none is listed.
    /// </summary>
    static readonly int[][] Scores =
    [
        [28496, 65572, 109071, 146942],
        [43540, 82826, 145172, 221104],
        [48180, 77083, 167187, 247973],
        [54230, 115619, 207103, 0],
        [72805, 137290, 240329, 343652],
        [89357, 0, 0, 0],
    ];

    /// <summary>
    /// The variants' names after the generation, in <see cref="Scores"/>' order: the base chip has none.
    /// </summary>
    static readonly string[] Variants = ["", "Pro", "Max", "Ultra"];

    /// <summary>
    /// The Metal score of the GPU Metal names <paramref name="gpu"/> ("Apple M3 Pro"); null for another name. A chip not listed is estimated: a
    /// generation past the table's counts as its newest, and a variant not listed for its generation is that variant's newest score scaled by
    /// the two generations' base chips (an M6 Pro: the M5 Pro's x M6 / M5).
    /// </summary>
    public static int? Score(string gpu)
    {
        var m = ChipName().Match(gpu);
        if (!m.Success) return null;

        int generation = Math.Min(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), Scores.Length) - 1;
        int variant = Array.IndexOf(Variants, m.Groups[2].Value);
        if (generation < 0) return null;
        if (Scores[generation][variant] > 0) return Scores[generation][variant];

        int known = Array.FindLastIndex(Scores, s => s[variant] > 0);
        return (int)((long)Scores[known][variant] * Scores[generation][0] / Scores[known][0]);
    }

    /// <summary>
    /// Whether the GPU Metal names <paramref name="gpu"/> traces rays in hardware: an M3 or later (Metal's Apple9 family, which the
    /// renderer checks on the device); false for another name.
    /// </summary>
    public static bool TracesInHardware(string gpu) =>
        ChipName().Match(gpu) is { Success: true } m && int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) >= 3;

    /// <summary>
    /// "Apple M&lt;generation&gt;" and an optional variant, as Metal names the GPU.
    /// </summary>
    [GeneratedRegex(@"^Apple M(\d+)(?: (Pro|Max|Ultra))?$")]
    private static partial Regex ChipName();

    #endregion

    #region Tiers

    /// <summary>
    /// The frame rate a recommended tier is predicted to reach in the bases scenario.
    /// </summary>
    const double TargetFps = 60;

    /// <summary>
    /// The anchor: the M6's Metal score and its average frames per second in the bases scenario by tier (two runs each, the launcher benchmark
    /// at 1920x1080 without an upscaler; GPU-bound). Extreme is not listed, so never recommended: on Metal it adds the GI radiance cache, 64 probe
    /// rays and full-size GTAO, costing about 28% of the frame rate (35 fps) for a picture close to Ultra's. Docs/launcher.md "Graphics".
    /// </summary>
    const int AnchorScore = 89357;

    /// <summary>
    /// <see cref="AnchorScore"/>'s frames per second by tier, lowest tier first.
    /// </summary>
    static readonly (int Tier, double Fps)[] AnchorFps =
    [
        (QualityTiers.LowPreset, 203.5),
        (QualityTiers.MediumPreset, 131.2),
        (QualityTiers.HighPreset, 95.6),
        (QualityTiers.UltraPreset, 48.9),
    ];

    /// <summary>
    /// The highest tier a GPU of Metal score <paramref name="score"/> is predicted to run at <see cref="TargetFps"/> (frame rate in proportion
    /// to the score), a tier with ray-traced switches only where it <paramref name="tracesInHardware"/>; Low when none.
    /// </summary>
    public static int Tier(int score, bool tracesInHardware)
    {
        int tier = QualityTiers.LowPreset;
        foreach (var (t, fps) in AnchorFps)
        {
            if (!tracesInHardware && QualityTiers.NeedsRayQueries(t)) continue;
            if (fps * score / AnchorScore >= TargetFps) tier = t;
        }

        return tier;
    }

    #endregion
}
