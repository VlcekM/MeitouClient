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
    public static IReadOnlyList<Enhancement> Create(PostOptions post, Func<bool> meitouHaze, Action<bool> setMeitouHaze, Func<bool> meitouShadows, Action<bool> setMeitouShadows,
        Func<bool> meitouRange, Action<bool> setMeitouRange, Func<bool> impostors, Action<bool> setImpostors, Func<bool> meitouReach, Action<bool> setMeitouReach,
        Func<bool> meitouWater, Action<bool> setMeitouWater, Func<bool> foliageLod, Action<bool> setFoliageLod, Func<bool>? gi = null, Action<bool>? setGi = null,
        Func<bool>? nightHaze = null, Action<bool>? setNightHaze = null, Func<bool>? planetshine = null, Action<bool>? setPlanetshine = null,
        Func<bool>? stars = null, Action<bool>? setStars = null, Func<bool>? clouds = null, Action<bool>? setClouds = null, Func<bool>? cloudShadows = null, Action<bool>? setCloudShadows = null,
        Func<bool>? cloudFog = null, Action<bool>? setCloudFog = null,
        Func<bool>? interiorGi = null, Action<bool>? setInteriorGi = null, Func<bool>? characterLod = null, Action<bool>? setCharacterLod = null,
        Func<bool>? skin = null, Action<bool>? setSkin = null, Func<bool>? interiorLamps = null, Action<bool>? setInteriorLamps = null,
        Func<bool>? icons = null, Action<bool>? setIcons = null, Func<bool>? blood = null, Action<bool>? setBlood = null,
        Func<bool>? bloodSurfaces = null, Action<bool>? setBloodSurfaces = null, Func<bool>? tracers = null, Action<bool>? setTracers = null) =>
    [
        new("ao", "Ambient occlusion", "off", "SSAO",
            () => post.Ssao, v => post.Ssao = v, "the game ships SSAO but has it disabled"),
        new("dither", "Dithering", "off", "on",
            () => post.Dither, v => post.Dither = v, "against banding in smooth gradients; the game has none"),
        new("haze", "Haze", "strength 1", $"strength {MeitouHazeStrength}",
            meitouHaze, setMeitouHaze, "lighter haze keeps far mountains visible"),
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
            () => post.ToneMapping, v => post.ToneMapping = v,
            "the game has no tone curve (bright skies and sunlit sand clip flat at white) and no grading; Meitou blends the clip with ACES (the Tab slider sets the share, 0.75) so highlights roll off, then grades saturation 1.06 and contrast 1.05"),
        new("shafts", "Light shafts", "flat haze", "shadowed haze",
            () => post.LightShafts, v => post.LightShafts = v,
            "the game lights its haze and weather fog the same everywhere, so shadows end at the ground; Meitou darkens the air the sun does not reach (a froxel grid of shadow samples along the view), so mountains, rock stacks and buildings cast shafts through the haze and the fog (docs/render-shafts.md)"),
        new("heathaze", "Heat haze", "strength 1", $"strength {MeitouHeatHazeStrength}",
            () => post.MeitouHeatHaze, v => post.MeitouHeatHaze = v,
            "the game's heat haze shifts distant objects by several pixels in hot weather, which reads as blur; Meitou halves it (the Tab slider sets any strength)"),
        new("night", "Night air", "black haze", "airglow",
            nightHaze ?? (() => false), setNightHaze ?? (_ => { }),
            "the game's haze takes the sky's sunlit colour, which is black at night, so everything a few thousand units away fades to black against a black sky and only the land round the camera stays lit; Meitou gives the air a faint glow of its own (airglow, and the planet's light scattered by the air, darker when the planet is a crescent): the far land fades into it, and the sky shows the same glow above the horizon, falling off with height, so ridges stand against it and the stars keep their contrast (docs/formats/sky.md, \"Night air\")"),
        new("planetshine", "Planetshine", "flat night", "planet light",
            planetshine ?? (() => false), setPlanetshine ?? (_ => { }),
            "the game lights the night land with a flat, directionless ambient; Meitou lets the big planet light it, as a sunlit sphere would (its phase, colour and size decide how much; the Tab slider scales it), from its fixed direction, with the shadows, the water's glint and the probes' light following (docs/formats/sky.md \"Planetshine\")"),
        new("stars", "Night sky", "starfield texture", "procedural",
            stars ?? (() => false), setStars ?? (_ => { }),
            "the game lays one 4096 pixel starfield texture over the dome (blobby purple stars, a smeared brown nebula, blurred when it is magnified); Meitou draws point stars (a few thousand naked-eye ones and a faint background, colours from blackbody temperatures, one or two pixels wide at any resolution), turning about the sun's axis once a game day, dimming and reddening towards the horizon, the low ones twinkling (docs/formats/sky.md, \"Meitou night sky\")"),
        new("nightgrade", "Night grading", "none", "scotopic blue",
            () => post.NightGrade, v => post.NightGrade = v,
            "the game has no grading, so night is the day's saturated browns and oranges, darkened; Meitou shifts dim colour towards the rods' blue-grey as the scene's adapted luminance falls (the Purkinje shift), keeping lamps, fires and the moon in colour (the Tab slider sets the strength)"),
        new("clouds", "Clouds", "flat", "volumetric",
            clouds ?? (() => false), setClouds ?? (_ => { }),
            "the game uses a flat cloud texture; Meitou ray marches a procedural cloud volume with rounded shapes, light absorption, sunlit edges and shaded interiors, lit by the sun and by the planet at night; coverage and wind follow the existing weather, with separate temporal histories for the sky and water reflections (docs/render-clouds.md)"),
        new("cloudshadows", "Cloud shadows", "none", "on the land",
            cloudShadows ?? (() => false), setCloudShadows ?? (_ => { }),
            "the game's clouds cast no shadows; Meitou anchors the cloud layer to the world (1.2 km up, so the clouds also drift past as the camera moves) and dims the sun on the land and the water under it, the shadows sweeping over the ground with the wind (docs/formats/clouds.md, \"Meitou cloud shadows\")"),
        new("cloudfog", "Cloud shadows in fog", "full", "faded",
            cloudFog ?? (() => false), setCloudFog ?? (_ => { }),
            "with Cloud shadows on, Meitou weakens them as the camera nears a placed fog volume and drops them inside one; otherwise the cloud shadows darken the already dim fog, and places like the swamp or the Deadlands get too dark (docs/render-clouds.md)"),
        new("interiorgi", "Indoor GI", "original ambient", "bounced light",
            interiorGi ?? (() => false), setInteriorGi ?? (_ => { }),
            "with Indirect light enabled, use bounced light indoors; turn this off to keep the original ambient on room surfaces, furniture and indoor characters while outdoor GI stays enabled"),
        new("charlod", "Character levels", "the meshes' own", "generated levels",
            characterLod ?? (() => true), setCharacterLod ?? (_ => { }),
            "the game draws a character's meshes with their files' own LOD levels (one reduced level at 200 or 400 units for some, none for most: human_female, hair cuts, weapons) by the distance_sphere rule; Meitou makes four reductions of every mesh at load and picks one by how many pixels its deviation would show, with coarser levels in the shadow maps (docs/characters.md \"LOD of character meshes\")"),
        new("skin", "Skin", "plain", "subsurface",
            skin ?? (() => false), setSkin ?? (_ => { }),
            "the game lights skin as it lights stone, so the sun's terminator on a face or arm is a hard grey edge; Meitou lets the light spread under bare skin (pre-integrated scattering from a measured skin profile, by how tightly the surface curves), so the edge softens and warms to red, in the world and on the Meitou stage and portraits; clothing, hair, animals and robots stay as they were (docs/character-renderer.md \"Skin\")"),
        new("interiorlamps", "Interior lamps", "night only", "day and night",
            interiorLamps ?? (() => false), setInteriorLamps ?? (_ => { }),
            "the game lights its ceiling lamps and other light buildings only at night, indoors too, so rooms are dim by day; Meitou keeps the lamps of buildings' interior layouts lit all day, outdoor lamps stay night-only (docs/render-lights.md)"),
        new("icons", "Item icons", "the game's", "supersampled",
            icons ?? (() => false), setIcons ?? (_ => { }),
            "the game draws an inventory item's icon from its mesh with one sample a pixel (jagged edges, shimmering fine detail) and shows the pictures it saved before; Meitou draws each one supersampled 4 x 4, as the enhanced portraits, and keeps its own pictures (docs/game/ui-screens.md \"Item icons\")"),
        new("blood", "Blood cells", "8 nearest", $"up to {TerrainDecalRenderer.MaxSlots}",
            blood ?? (() => false), setBlood ?? (_ => { }),
            $"the game draws the blood of only the 8 terrain cells (288 units square) nearest the camera, so a big battle's blood ends at a straight cell edge and the cells past it dry up unseen; Meitou draws every cell within Decal Range, up to {TerrainDecalRenderer.MaxSlots} (docs/game/blood-decals.md)"),
        new("bloodsurfaces", "Blood on surfaces", "terrain only", "walls, floors, objects",
            bloodSurfaces ?? (() => false), setBloodSurfaces ?? (_ => { }),
            "the game leaves blood only on the terrain, so a fight beside a wall or inside a building marks nothing there; Meitou also sprays splashes over walls, buildings, rocks and other static objects, and leaves pools and drips on floors, never up a wall (docs/game/blood-decals.md \"Blood on surfaces\")"),
        // The ground relief's settings are static (GroundRelief), so these two need no delegates. The labels are the saved keys of the on/off toggles they replace.
        new("parallax", "Ground parallax", "off", "relief march",
            () => GroundRelief.ParallaxActive, v => GroundRelief.ParallaxActive = v,
            "the game's ground is flat, its normal maps only shading it; Meitou marches the view ray through a height made from each layer's normal map near the eye, and toward the sun for a self-shadow (docs/render-terrain.md \"Ground relief\")"),
        new("displace", "Ground displacement (tessellation)", "off", "tessellated",
            () => GroundRelief.DisplaceActive, v => GroundRelief.DisplaceActive = v,
            "the game's ground is its height map's mesh; Meitou tessellates the ground near the eye and raises it by the same relief (needs tessellation shaders; visual only: characters walk on the undisplaced ground)"),
        new("weld", "Mesh welding", "per-face tangents", "welded, averaged tangents",
            () => MeshWeld.Enabled, v => MeshWeld.Enabled = v,
            "the game's meshes store each corner once per triangle, with that triangle's own tangent, so normal-mapped surfaces show faint facets at the triangle edges; Meitou welds the corners that differ only in their tangent and averages it, which smooths the bump lighting (characters most) and keeps about 60% fewer vertices in video memory; applies to meshes as they load (docs/render-objects.md)"),
        new("tracers", "Bolt tracers", "none", "streaks",
            tracers ?? (() => false), setTracers ?? (_ => { }),
            "the game draws a bolt in flight as its mesh alone, stretched a little with its speed, which is hard to follow at a distance; Meitou draws a faint streak behind it that grows out of the crossbow and shrinks into where the bolt stops; drawn with the particles, so none with --no-particles (docs/game/combat.md \"Bolt visuals\")"),
        new("fog", "Fog", "hard-edged", "soft",
            () => post.MeitouFog, v => post.MeitouFog = v,
            "the game's dust and smoke sprites cut straight into the ground and walls they cross (all but its mist and steam, which fade; Faithful draws those hard-edged too); Meitou fades each sprite out over the last stretch before a surface (a quarter of its size, 0.2 to 30 m), so dust clouds meet the ground softly (docs/render-post.md \"Fog\")"),
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

    /// <summary>The sweep's next switch (-1: no sweep) and when it flips (<see cref="System.Diagnostics.Stopwatch"/> ticks).</summary>
    int sweep = -1;
    long sweepNext;

    /// <summary>Starts (or stops) a sweep: every switch flips in turn (Meitou to Faithful and back the other way), one a second (the viewer's Ctrl+F1).</summary>
    public string Sweep()
    {
        if (sweep >= 0) { sweep = -1; return "meitou    sweep stopped"; }
        if (switches.Count == 0) return "meitou    no switches to sweep";
        (sweep, sweepNext) = (0, System.Diagnostics.Stopwatch.GetTimestamp());
        return $"meitou    sweep: {switches.Count} switches flip in turn, 1 s apart (Ctrl+F1 stops)";
    }

    /// <summary>Flips the sweep's next switch when its second has come; the console line, or null.</summary>
    public string? TickSweep()
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (sweep < 0 || now < sweepNext) return null;
        var e = switches[sweep];
        e.IsMeitou = !e.IsMeitou;
        string line = $"meitou    sweep {sweep + 1}/{switches.Count}: {e.Id} {(e.IsMeitou ? "Meitou" : "Faithful")}";
        sweepNext = now + System.Diagnostics.Stopwatch.Frequency;
        if (++sweep == switches.Count) sweep = -1;
        return line;
    }
}
