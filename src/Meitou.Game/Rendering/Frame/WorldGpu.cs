using System.Diagnostics;
using System.Numerics;
using Meitou.Data.World;

using Meitou.Game.Rendering.Foliage;
using Meitou.Game.Rendering.Objects;
using Meitou.Game.Rendering.Overlays;
using Meitou.Game.Rendering.Particles;
using Meitou.Game.Rendering.Post;
using Meitou.Game.Rendering.Shadows;
using Meitou.Game.Rendering.Sky;
using Meitou.Game.Rendering.Terrain;
using Meitou.Game.Rendering.Water;

namespace Meitou.Game.Rendering.Frame;

/// <summary>
/// The world frame's renderers and per-frame state: what <see cref="WorldFrame.CreateGpu"/> makes and <see cref="WorldFrame.Draw"/> draws with.
/// </summary>
internal sealed class WorldGpu : IDisposable
{
    /// <summary>
    /// The terrain: heights, materials and their textures.
    /// </summary>
    public required TerrainRenderer Terrain;

    /// <summary>
    /// The sky, sun, moon, stars and clouds.
    /// </summary>
    public required SkyRenderer Sky;

    /// <summary>
    /// The lakes, rivers and sea (null when the scene has no water source).
    /// </summary>
    public WaterRenderer? Water;

    /// <summary>
    /// The weather's wetness, dust and rain for every surface shader (null without a game database).
    /// </summary>
    public WeatherSurfaces? Surfaces;

    /// <summary>
    /// The placed fog volumes of <c>fogfeatures.dat</c> (null when the install has none).
    /// </summary>
    public FogVolumes? FogVolumes;

    /// <summary>
    /// This frame's weather fog spheres (<see cref="ParticleRenderer.CollectFogVolumes"/>), reused.
    /// </summary>
    public readonly List<FogVolumes.EffectFog> EffectFogs = [];

    /// <summary>
    /// The weather's particle effects (null while there are none: a clear weather, or <c>--no-particles</c>); <see cref="WorldFrame.EnsureParticles"/> makes
    /// it.
    /// </summary>
    public ParticleRenderer? Particles;

    /// <summary>
    /// The movement marker the game interface shows after a ground move order (null in the viewer, or without <c>position.mesh</c>).
    /// </summary>
    public MarkerRenderer? Markers;

    /// <summary>
    /// The blood decals on the ground (null in the viewer, or with the game's <c>Blood</c> setting off; docs/game/blood-decals.md).
    /// </summary>
    public TerrainDecalRenderer? Decals;

    /// <summary>
    /// The host's decals <see cref="Decals"/> draws.
    /// </summary>
    public TerrainDecalStore? DecalStore;

    /// <summary>
    /// luckyren Mode's flying bones and limbs (null until the host makes it; docs/game/luckyren.md).
    /// </summary>
    public DebrisRenderer? Debris;

    /// <summary>
    /// This frame's bolts stuck in characters (<see cref="CharacterRenderer.CollectBolts"/>), drawn with the debris.
    /// </summary>
    public readonly List<DebrisPiece> BoltPieces = [];

    /// <summary>
    /// The flying pieces <see cref="Debris"/> draws this frame.
    /// </summary>
    public readonly List<DebrisPiece> DebrisPieces = [];

    /// <summary>
    /// The birds of this frame (<see cref="Meitou.Game"/>'s flocks), drawn as loose meshes after the debris.
    /// </summary>
    public readonly List<DebrisPiece> BirdPieces = [];

    /// <summary>
    /// The buildings' interior masks the terrain, foliage and map features are cut by (made with the first frame that has objects;
    /// docs/render-interior-cut.md).
    /// </summary>
    public InteriorMaskRenderer? InteriorMask;

    /// <summary>
    /// The navmesh debug view (null in the viewer; drawn while visible).
    /// </summary>
    public NavmeshOverlayRenderer? NavmeshOverlay;

    /// <summary>
    /// The pick & collision inspector's drawing (Meitou only): pick hulls and shapes, same kind of renderer as the navmesh view.
    /// </summary>
    public NavmeshOverlayRenderer? InspectorOverlay;

    /// <summary>
    /// The hover outline the game draws round the character under the mouse, and with Alt those near (null in the viewer).
    /// </summary>
    public HoverOutlineRenderer? HoverOutline;

    /// <summary>
    /// The characters <see cref="HoverOutline"/> outlines, by their draw-list keys, each with its colour (empty: none).
    /// </summary>
    public IReadOnlyList<(long Key, Vector4 Colour)> HoverKeys = [];

    /// <summary>
    /// The buildings <see cref="HoverOutline"/> outlines too (the selected and the hovered one), by placement key, above its floor height, in its
    /// colour (empty: none;
    /// nothing for one the interior view shows).
    /// </summary>
    public IReadOnlyList<(string Key, float Floor, Vector4 Colour)> OutlinedBuildings = [];

    /// <summary>
    /// A presentation cap on how far the frame reaches (the Meitou editor's bar, high above the world): the far plane and the shadow cascades end
    /// there, so the world below leaves the view; null in play.
    /// </summary>
    public float? ViewCap;

    /// <summary>
    /// The Tab panel's weather particles switch, given to <see cref="Particles"/> every frame (it is made only with the first weather).
    /// </summary>
    public bool WeatherParticles = true;

    /// <summary>
    /// The Tab panel's particle density, given to <see cref="Particles"/> every frame.
    /// </summary>
    public float ParticleDensity = 1;

    /// <summary>
    /// Makes the particle renderer on demand (null with <c>--no-particles</c>).
    /// </summary>
    public Func<ParticleRenderer>? MakeParticles;

    /// <summary>
    /// The weather regions' cells.
    /// </summary>
    public WeatherAreas? WeatherAreas;

    /// <summary>
    /// The colour of the camera's weather cell the particle area was made for (-1: none yet).
    /// </summary>
    public int ParticleAreaColour = -1;

    /// <summary>
    /// The water's reflection (null without water).
    /// </summary>
    public ReflectionPass? Reflection;

    /// <summary>
    /// The sun's shadow cascades (null when not made).
    /// </summary>
    public ShadowPass? Shadow;

    /// <summary>
    /// The shadow debug view (<c>--debug-shadows</c>; 0 off).
    /// </summary>
    public int DebugShadows;

    /// <summary>
    /// The ray-traced global illumination's scene and debug view (null without <c>--gi-debug</c> or ray queries; docs/render-gi.md).
    /// </summary>
    internal Gi.GiScene? Gi;

    /// <summary>
    /// The GI debug view's pass (with <see cref="Gi"/>).
    /// </summary>
    internal Gi.GiDebugPass? GiDebugPass;

    /// <summary>
    /// The irradiance probes lit from <see cref="Gi"/> (Vulkan).
    /// </summary>
    internal Gi.GiProbes? Probes;

    /// <summary>
    /// A backend-owned probe GI (Metal's native ray tracing); Vulkan uses <see cref="Gi"/> and <see cref="Probes"/> instead.
    /// </summary>
    internal Gi.IProbeGi? ProbeGi;

    /// <summary>
    /// The placed lamps' lights (null without a game database or heightmap).
    /// </summary>
    internal WorldLamps? Lamps;

    /// <summary>
    /// The placed buildings' effects (campfires, torches, smoke; docs/formats/lights.md "Effects on buildings"), read on a worker, handed to the
    /// particles once read.
    /// </summary>
    internal Task<List<PlacedEffect>>? BuildingEffects;

    /// <summary>
    /// The GI debug view (<c>--gi-debug</c>, 0 to 3; 0 off).
    /// </summary>
    public int GiDebug;

    /// <summary>
    /// Whether the traced scene is updated (and the debug view drawn) this frame: the bench's gi-scene switch.
    /// </summary>
    public bool GiActive = true;

    /// <summary>
    /// The GI resolve's divisor (0 the lit fragments read the probes, 1 full size, 2 half; the bench's gi-resolve switch; docs/render-gi.md
    /// "Resolve").
    /// </summary>
    public int GiResolve = 2;

    /// <summary>
    /// The Vulkan post chain (its reflection and GI also read it); null on a backend with a seam-only chain.
    /// </summary>
    public PostProcess? Post;

    /// <summary>
    /// The chain <see cref="WorldFrame.Draw"/> runs: <see cref="Post"/> on Vulkan (also when a host sets only that), the host's seam chain on
    /// Metal.
    /// </summary>
    public IWorldPostChain? PostChain { get => _postChain ?? Post; set => _postChain = value; }

    /// <summary>
    /// The host's seam chain behind <see cref="PostChain"/> (null: <see cref="Post"/>).
    /// </summary>
    IWorldPostChain? _postChain;

    /// <summary>
    /// The placed objects: buildings, rocks and the rest (null when the scene has no objects).
    /// </summary>
    public WorldObjectRenderer? Objects;

    /// <summary>
    /// The foliage and grass (null with <c>--no-foliage</c> or without a game database).
    /// </summary>
    public FoliageRenderer? Foliage;

    /// <summary>
    /// The characters (null without <c>--crowd</c> or a host that feeds them).
    /// </summary>
    public Characters.CharacterRenderer? Characters;

    /// <summary>
    /// Streams the terrain's biome textures round the camera (null when not made).
    /// </summary>
    public TerrainStreamer? Streamer;

    /// <summary>
    /// Where the camera is about to arrive (a character flight's end view's eye; null otherwise): the object and terrain texture streaming load
    /// there too.
    /// </summary>
    public Vector3? Ahead;

    /// <summary>
    /// The host flies the camera this frame (a character flight or squad tour hop under way, not its hold): the far shadow cascades are not drawn
    /// (docs/render-shadows.md "Flights").
    /// </summary>
    public bool CameraFlying;

    /// <summary>
    /// Points whose surroundings stay loaded besides the camera's (the player's characters, "Keep textures at characters"): object meshes and
    /// textures, terrain biome textures.
    /// </summary>
    public IReadOnlyList<Vector3> Kept = [];

    /// <summary>
    /// Places kept loaded: the eye's and each merged kept point's, held at its highest for <see cref="WorldFrame.AreasHoldMs"/> (the merge flickers near its
    /// distance, and the grass store and terrain arrays reallocate when it grows). The texture, mesh and grass budgets scale with it.
    /// </summary>
    public int Areas = 1;

    /// <summary>
    /// When <see cref="Areas"/> was last set (<see cref="Environment.TickCount64"/>).
    /// </summary>
    public long AreasAt;

    /// <summary>
    /// Keeps the video memory under the budget by clamping the ranges and pausing the streaming (null with <c>MEITOU_VRAM_GUARD=0</c>).
    /// </summary>
    public VramGuard? Guard;

    /// <summary>
    /// The scene's native host (wave 4), made at the first frame.
    /// </summary>
    public SceneHost? Scene;

    /// <summary>
    /// The heat haze's target this frame (the weather's <c>heat haze</c> × strength 1 × the sun factor), for the statistics.
    /// </summary>
    public float HeatHazeTarget;

    /// <summary>
    /// Game hours since the load for the heat haze's animation, when the caller runs a game clock; null: real time at game speed 1.
    /// </summary>
    public double? GameHours;

    /// <summary>
    /// The weather scheduler hooked to the frame (null without a game database). The host sets <see cref="WorldWeather.Day"/>, <see
    /// cref="WorldWeather.GameSpeed"/> and <see cref="WorldWeather.Paused"/>.
    /// </summary>
    public WorldWeather? Weather;

    /// <summary>
    /// The weather at the camera this frame (what the other renderers read; <see cref="WeatherState.Clear"/> without a scheduler).
    /// </summary>
    public WeatherState WeatherState => Weather?.State ?? WeatherState.Clear;

    /// <summary>
    /// Real time since the heat haze's last update (its step is capped at 0.25 s).
    /// </summary>
    internal readonly Stopwatch HeatHazeClock = new();

    /// <summary>
    /// The heat haze's own clock in game hours, used while <see cref="GameHours"/> is null.
    /// </summary>
    internal double HeatHazeHours;

    /// <summary>
    /// Disposes every renderer this holds; a seam-only post chain stays with its host.
    /// </summary>
    public void Dispose()
    {
        GiDebugPass?.Dispose();
        ProbeGi?.Dispose();
        Probes?.Dispose();
        Lamps?.Dispose();
        Gi?.Dispose();
        Streamer?.Dispose();
        Foliage?.Dispose();
        Characters?.Dispose();
        Objects?.Dispose();
        Particles?.Dispose();
        Markers?.Dispose();
        Decals?.Dispose();
        Debris?.Dispose();
        InteriorMask?.Dispose();
        NavmeshOverlay?.Dispose();
        InspectorOverlay?.Dispose();
        Water?.Dispose();
        Surfaces?.Dispose();
        Reflection?.Dispose();
        Shadow?.Dispose();
        Post?.Dispose();   // a seam-only chain (MetalPostChain) is owned by its host
        Sky.Dispose();
        Terrain.Dispose();
    }
}
