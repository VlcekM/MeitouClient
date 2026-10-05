namespace Meitou.Rendering;

/// <summary>
/// One feature that can look as the game ships it (<b>Faithful</b>) or with Meitou's improvement (<b>Meitou</b>, the
/// default), chosen per feature: the remaster switches. Separate from the game's own graphics settings (draw distances,
/// shadow quality...), which keep their meaning in both. Reads and writes the live options through its delegates.
/// </summary>
public sealed class Enhancement(string id, string name, string faithful, string meitou, Func<bool> get, Action<bool> set, string note, Func<string>? liveMeitou = null)
{
    /// <summary>The command-line name (<c>--meitou ao,haze</c>).</summary>
    public string Id { get; } = id;
    public string Name { get; } = name;
    /// <summary>What each choice amounts to, in a word or two (shown next to it: "Faithful (off)").</summary>
    public string Faithful { get; } = faithful;
    public string Meitou => liveMeitou?.Invoke() ?? meitou;

    public bool IsMeitou { get => get(); set => set(value); }

    /// <summary>A sentence on the difference (what the game does, what Meitou changes).</summary>
    public string Note { get; } = note;

    /// <summary>"Faithful (off)" or "Meitou (SSAO)".</summary>
    public string State => IsMeitou ? $"Meitou ({Meitou})" : $"Faithful ({Faithful})";
}

public static class Enhancements
{
    public const float MeitouHazeStrength = 0.85f;

    /// <summary>
    /// The switches, in key order (F1 upwards in the viewer), over the post-processing options and the haze strength
    /// (docs/formats/post-processing.md and sky.md for what the game does).
    /// </summary>
    public static IReadOnlyList<Enhancement> Create(PostOptions post, Func<float> hazeStrength, Action<float> setHazeStrength, Func<bool> meitouShadows, Action<bool> setMeitouShadows) =>
    [
        new("ao", "Ambient occlusion", "off", "SSAO",
            () => post.Ssao, v => post.Ssao = v, "the game ships SSAO but has it disabled"),
        new("dither", "Dithering", "off", "on",
            () => post.Dither, v => post.Dither = v, "against banding in smooth gradients; the game has none"),
        new("haze", "Haze", "strength 1", $"strength {MeitouHazeStrength}",
            () => MathF.Abs(hazeStrength() - 1) > 1e-3f, v => setHazeStrength(v ? MeitouHazeStrength : 1), "lighter haze keeps far mountains visible"),
        new("aa", "Anti-aliasing", "FXAA", "temporal",
            () => post.Upscale.Kind != UpscalerKind.Off, v => post.Upscale.Kind = v ? post.Upscale.Preferred : UpscalerKind.Off,
            "the game uses FXAA; Meitou uses a temporal method (TAA, FSR or DLSS: the Tab panel chooses, FSR and DLSS need their libraries)",
            () => post.Upscale.Preferred.ToString().ToUpperInvariant()),
        new("shadows", "Shadows", "CSM", "soft, far terrain",
            meitouShadows, setMeitouShadows,
            "the game draws hard-edged cascades that end abruptly at the shadow range; Meitou fits them to the view, filters them softly with contact-hardening penumbrae, blends the cascades, and lets mountains shadow the land out to the horizon"),
    ];

    /// <summary><c>--meitou</c> / <c>--faithful &lt;all|id,id...&gt;</c>: turns those switches to Meitou or to Faithful.</summary>
    public static void Apply(IReadOnlyList<Enhancement> switches, string list, bool meitou)
    {
        foreach (var id in list.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (id == "all") { foreach (var e in switches) e.IsMeitou = meitou; continue; }
            var match = switches.FirstOrDefault(e => e.Id == id)
                ?? throw new ArgumentException($"unknown feature '{id}' ({string.Join(", ", switches.Select(e => e.Id))} or all)");
            match.IsMeitou = meitou;
        }
    }
}
