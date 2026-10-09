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
    public const float MeitouHazeStrength = 0.93f;

    /// <summary>The Meitou shadows' default shadow distance (the game's is 5000, its slider ends at 9000), and the most the <c>--shadow-range</c> option takes with them.</summary>
    public const float MeitouShadowRange = 10000, MeitouShadowRangeMax = 15000;

    /// <summary>The Meitou <c>reach</c> switch's defaults (docs/render-objects.md, "Draw distance"): objects at full detail out to 20000, huge landmarks out to 150000,
    /// and the terrain LOD's pixel error 16 (Faithful: 12000 and <see cref="WorldRenderOptions.DefaultTerrainPixelError"/>, 10, what the viewer drew before the switch).</summary>
    public const float MeitouObjectDistance = 20000, MeitouLandmarkDistance = 150000, MeitouTerrainPixelError = 16;
    public const float FaithfulObjectDistance = 12000;

    /// <summary>
    /// The switches, in key order (Shift+F1 upwards in the game, the Tab panel's checkboxes in the viewer), over the post-processing options and the haze strength
    /// (docs/formats/post-processing.md and sky.md for what the game does).
    /// </summary>
    public static IReadOnlyList<Enhancement> Create(PostOptions post, Func<float> hazeStrength, Action<float> setHazeStrength, Func<bool> meitouShadows, Action<bool> setMeitouShadows,
        Func<bool> meitouRange, Action<bool> setMeitouRange, Func<bool> impostors, Action<bool> setImpostors, Func<bool> meitouReach, Action<bool> setMeitouReach,
        Func<bool> meitouWater, Action<bool> setMeitouWater, Func<bool> foliageLod, Action<bool> setFoliageLod, Func<bool>? gi = null, Action<bool>? setGi = null) =>
    [
        new("ao", "Ambient occlusion", "off", "SSAO",
            () => post.Ssao, v => post.Ssao = v, "the game ships SSAO but has it disabled"),
        new("dither", "Dithering", "off", "on",
            () => post.Dither, v => post.Dither = v, "against banding in smooth gradients; the game has none"),
        new("haze", "Haze", "strength 1", $"strength {MeitouHazeStrength}",
            () => MathF.Abs(hazeStrength() - 1) > 1e-3f, v => setHazeStrength(v ? MeitouHazeStrength : 1), "lighter haze keeps far mountains visible"),
        new("aa", "Anti-aliasing", "FXAA", "temporal",
            () => post.Upscale.Kind != UpscalerKind.Off, v => { post.Upscale.Kind = v ? post.Upscale.Preferred : UpscalerKind.Off; if (!v) post.Fxaa = true; },
            "the game uses FXAA; Meitou uses a temporal method (TAA, FSR or DLSS: the Tab panel chooses, FSR and DLSS need their libraries)",
            () => post.Upscale.Preferred.ToString().ToUpperInvariant()),
        new("shadows", "Shadows", "CSM", "soft, far terrain",
            meitouShadows, setMeitouShadows,
            "the game draws hard-edged cascades that end abruptly at the shadow range; Meitou fits them to the view, filters them softly with contact-hardening penumbrae, blends the cascades, and lets mountains shadow the land out to the horizon"),
        new("range", "Foliage ranges", "per layer", "by size",
            meitouRange, setMeitouRange,
            "the game ends all of a layer's meshes at one range (1000 x foliage range for most: trees, ruins, junk and litter alike); Meitou draws large meshes (trees, ruins, wrecks, rock stacks) far, medium ones (junk, boulders, bushes) midway and small ones (litter, small plants) near, each class with its own Tab slider"),
        new("impostors", "Far impostors", "off", "billboards",
            impostors, setImpostors,
            "the game draws every foliage mesh in full out to its range; Meitou draws large and medium meshes beyond the impostor distance (Tab slider) as baked billboards, crossfaded, which also stand in as shadow casters far out"),
        new("reach", "Draw distances", "game-like", "farther",
            meitouReach, setMeitouReach,
            $"Faithful keeps the viewer's old defaults (objects at full detail to {FaithfulObjectDistance:0}, terrain error {WorldRenderOptions.DefaultTerrainPixelError:0} px); Meitou draws objects to {MeitouObjectDistance:0}, lets the terrain be coarser ({MeitouTerrainPixelError:0} px) and draws huge landmarks (giant wrecks, skeletons, towers) out to {MeitouLandmarkDistance:0} (Tab sliders; the landmarks need Meitou at start)"),
        new("water", "Water", "flat", "waves and surf",
            meitouWater, setMeitouWater,
            "the game's water is a flat plane with scrolled normal maps; Meitou adds wind-driven waves, breakers that roll in along the depth, break into foam and run up the beach, and foam on the crests (visual only: the water stays at its height for the game)"),
        new("particles", "Weather particles", "full size", "low resolution",
            () => post.LowResParticles, v => post.LowResParticles = v,
            "the game draws every dust, ash and rain sprite at the full picture size, which is a fill-rate cost when big sprites pile up (a dust storm); Meitou draws the alpha and additive ones into a quarter-area (half per axis) target and blends it back, depth-aware, for a fraction of the cost"),
        new("lod", "Foliage levels", "full detail", "generated levels",
            foliageLod, setFoliageLod,
            "the game draws every foliage mesh at full detail, and TERRAIN-mode rocks and plants (some thousands of triangles each) have no LOD levels; Meitou makes coarser levels at load (quadric edge collapse, kept in a disk cache) and picks one per instance by how many pixels (shadow texels) its deviation would show"),
        new("gi", "Indirect light", "flat ambient", "ray-traced probes",
            gi ?? (() => false), setGi ?? (_ => { }),
            "the game lights every surface's shadow side with one flat sky ambient per biome; Meitou traces rays from a grid of probes around the camera (needs --gi and a GPU with ray tracing), so corners, interiors and the ground under overhangs darken and sunlit ground lights what faces it (docs/render-gi.md)"),
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

/// <summary>The one key that turns Meitou off and on (F1 in the viewer, Shift+F1 in the game): every switch Faithful, then back to the
/// ones that were Meitou before (all of them when they started Faithful). The Tab panel's checkboxes set them one by one.</summary>
public sealed class MeitouToggle(IReadOnlyList<Enhancement> switches)
{
    bool[]? before;

    /// <summary>Any switch is Meitou.</summary>
    public bool On => switches.Any(e => e.IsMeitou);

    /// <summary>Flips Meitou off or back on; returns the console line saying which.</summary>
    public string Toggle()
    {
        if (On)
        {
            before = [.. switches.Select(e => e.IsMeitou)];
            foreach (var e in switches) e.IsMeitou = false;
        }
        else
        {
            for (int i = 0; i < switches.Count; i++) switches[i].IsMeitou = before?[i] ?? true;
        }
        return On ? $"meitou    on: {string.Join(", ", switches.Where(e => e.IsMeitou).Select(e => e.Id))}" : "meitou    off, every switch Faithful (the game as it ships)";
    }
}
