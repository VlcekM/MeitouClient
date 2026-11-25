using System.Globalization;
using System.Text.RegularExpressions;
using Meitou.Game.Settings;

namespace Meitou.Game.Host;

/// <summary>
/// The quality tier an Apple-silicon GPU suits (an engine choice, docs/launcher.md "Graphics"): its Geekbench 7 Metal score against the frame
/// rates an M6 measured in the launcher benchmark's bases scenario, where every tier is GPU-bound; the highest tier predicted at
/// <see cref="TargetFps"/> or more, High at most (Ultra and Extreme, which need hardware ray tracing, have no M6 measurements yet).
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
    /// The anchor: the M6's Metal score and its average frames per second in the bases scenario at each tier it can run (two runs each, the
    /// launcher benchmark at 1920x1080 without an upscaler; GPU time was 93-103% of the frame time in every group). Docs/launcher.md "Graphics".
    /// </summary>
    const int AnchorScore = 89357;

    /// <summary>
    /// <see cref="AnchorScore"/>'s frames per second by tier, lowest tier first.
    /// </summary>
    static readonly (int Tier, double Fps)[] AnchorFps =
        [(QualityTiers.LowPreset, 164.0), (QualityTiers.MediumPreset, 136.1), (QualityTiers.HighPreset, 112.7)];

    /// <summary>
    /// The highest tier a GPU of Metal score <paramref name="score"/> is predicted to run at <see cref="TargetFps"/> (frame rate in proportion
    /// to the score); Low when none.
    /// </summary>
    public static int Tier(int score)
    {
        int tier = QualityTiers.LowPreset;
        foreach (var (t, fps) in AnchorFps)
        {
            if (fps * score / AnchorScore >= TargetFps) tier = t;
        }
        return tier;
    }

    #endregion
}
