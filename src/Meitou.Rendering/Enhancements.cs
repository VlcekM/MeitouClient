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

    /// <summary>
    /// The Meitou <c>planetshine</c> switch's strength: a multiple of the physical value (<see cref="Meitou.Data.World.Planetshine"/>; the Tab slider moves it).
    /// The physical light is a hundredth of a percent to a fifth of a percent of the game's noon sun, against a game night ambient that is a quarter of
    /// the noon sun's: 60 brings the planet's light on a face turned to it to about the ambient's level at the brightest part of the night (docs/formats/sky.md).
    /// </summary>
    public const float MeitouPlanetshineStrength = 60f;

    /// <summary>The Meitou <c>heathaze</c> switch's scale on the heat haze (Faithful: 1, the game's): far objects shimmer but stay readable.</summary>
    public const float MeitouHeatHazeStrength = 0.5f;

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
        Func<bool> meitouWater, Action<bool> setMeitouWater, Func<bool> foliageLod, Action<bool> setFoliageLod, Func<bool>? gi = null, Action<bool>? setGi = null,
        Func<bool>? nightHaze = null, Action<bool>? setNightHaze = null, Func<bool>? planetshine = null, Action<bool>? setPlanetshine = null,
        Func<bool>? stars = null, Action<bool>? setStars = null) =>
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
            "the game lights every surface's shadow side with one flat sky ambient per biome; Meitou traces rays from a grid of probes around the camera (needs a GPU with ray tracing; --no-gi leaves it out), so corners, interiors and the ground under overhangs darken and sunlit ground lights what faces it (docs/render-gi.md)"),
        new("tonemap", "Tone map and grade", "clip", "hybrid, graded",
            () => post.ToneMap != ToneMapOperator.Clamp || post.Grade, v => { post.ToneMap = v ? ToneMapOperator.Hybrid : ToneMapOperator.Clamp; post.Grade = v; },
            "the game has no tone curve (bright skies and sunlit sand clip flat at white) and no grading; Meitou blends the clip with ACES (the Tab slider sets the share, 0.75) so highlights roll off, then grades saturation 1.06 and contrast 1.05"),
        new("shafts", "Light shafts", "flat haze", "shadowed haze",
            () => post.LightShafts, v => post.LightShafts = v,
            "the game lights its haze and weather fog the same everywhere, so shadows end at the ground; Meitou darkens the air the sun does not reach (a froxel grid of shadow samples along the view), so mountains, rock stacks and buildings cast shafts through the haze and the fog (docs/render-shafts.md)"),
        new("heathaze", "Heat haze", "strength 1", $"strength {MeitouHeatHazeStrength}",
            () => MathF.Abs(post.HeatHazeStrength - 1) > 1e-3f, v => post.HeatHazeStrength = v ? MeitouHeatHazeStrength : 1,
            "the game's heat haze shifts distant objects by several pixels in hot weather, which reads as blur; Meitou halves it (the Tab slider sets any strength)"),
        new("night", "Night air", "black haze", "airglow",
            nightHaze ?? (() => false), setNightHaze ?? (_ => { }),
            "the game's haze takes the sky's sunlit colour, which is black at night, so everything a few thousand units away fades to black against a black sky and only the land round the camera stays lit; Meitou gives the air a faint glow of its own (airglow, and the planet's light scattered by the air, darker when the planet is a crescent): the far land fades into it, and the sky shows the same glow above the horizon, falling off with height, so ridges stand against it and the stars keep their contrast (docs/formats/sky.md, \"Night air\")"),
        new("planetshine", "Planetshine", "flat night", "planet light",
            planetshine ?? (() => false), setPlanetshine ?? (_ => { }),
            "the game lights the night land with a flat, directionless ambient; Meitou lets the big planet light it, as a sunlit sphere would (its phase, colour and size decide how much; the Tab slider scales it), from its fixed direction, with the shadows, the water's glint and the probes' light following (docs/formats/sky.md \"Planetshine\")"),
        new("stars", "Night sky", "starfield texture", "procedural",
            stars ?? (() => false), setStars ?? (_ => { }),
            "the game lays one 4096 pixel starfield texture over the dome (blobby purple stars, a smeared brown nebula, blurred when it is magnified); Meitou draws point stars (a few thousand naked-eye ones and a faint background, colours from blackbody temperatures, one or two pixels wide at any resolution) and a Milky Way with dust lanes, turning about the sun's axis once a game day, dimming and reddening towards the horizon, the low ones twinkling (docs/formats/sky.md, \"Meitou night sky\")"),
        new("nightgrade", "Night grading", "none", "scotopic blue",
            () => post.NightGradeStrength > 0, v => post.NightGradeStrength = v ? Meitou.Data.World.NightGrade.MeitouStrength : 0,
            "the game has no grading, so night is the day's saturated browns and oranges, darkened; Meitou shifts dim colour towards the rods' blue-grey as the scene's adapted luminance falls (the Purkinje shift), keeping lamps, fires and the moon in colour (the Tab slider sets the strength)"),
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
