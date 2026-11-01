namespace Meitou.Rendering;

/// <summary>
/// Relief on the ground (docs/render-terrain.md, "Ground relief"), a Meitou look the game does not have: each terrain layer gets a height map
/// integrated from its normal map (<see cref="NormalHeight"/>). Near the eye the terrain is tessellated and moved by it (the displacement); the terrain
/// shader marches the view ray through it (the parallax), and toward the sun for a self-shadow. The Tab panel has the essentials (the quality, the
/// toggles, the depth and the two distances); the rest are tuned values, settable for a run with the viewer's <c>--relief Name=value</c>.
/// </summary>
public static class GroundRelief
{
    /// <summary>Read when the terrain textures are created (the height array is made then or not at all).</summary>
    public static bool Enabled { get; set; } = true;

    /// <summary>
    /// The march's budget: 0 low, 1 medium, 2 high (the Tab panel's slider; high by default). The quality changes the view and sun step counts and
    /// how many of the heaviest layers the height keeps, not the material's layers or the relief's depth.
    /// </summary>
    public static int Quality { get; set; } = 2;

    /// <summary>The name of a <see cref="Quality"/> level, for the settings panels.</summary>
    public static string QualityName(int quality) => quality switch { 0 => "Low", 1 => "Medium", _ => "High" };

    /// <summary>Whether the patches march the height (the Tab panel's toggle; the viewer's K).</summary>
    public static bool ParallaxActive { get; set; } = true;

    /// <summary>Whether the ground near the eye is moved for real (tessellated patches, where the device has tessellation shaders; the Tab panel's toggle).</summary>
    public static bool DisplaceActive { get; set; } = true;

    /// <summary>The depth scale in world units (1 unit = 10 cm; the Tab panel's slider, default 30 cm): each layer's depth (<see cref="LayerDepthCm"/>) times
    /// this over <see cref="ReferenceDepth"/>.</summary>
    public static float Depth { get; set; } = 3;

    /// <summary>The depth the tuned values are expressed at (20 cm): <see cref="Depth"/> scales every layer's depth, and <see cref="MaxDepth"/>, by itself over this.</summary>
    public const float ReferenceDepth = 2;

    /// <summary>Where the displacement ends (world units from the eye; in full to 60% of it; the Tab panel's slider).</summary>
    public static float DisplaceDistance { get; set; } = 1000;

    /// <summary>Where the parallax ends (world units from the eye; in full to half of it; the Tab panel's slider).</summary>
    public static float ParallaxDistance { get; set; } = 600;

    /// <summary>
    /// The terrain's outline refinement (docs/render-terrain.md, "Refinement"): every main-view patch is tessellated, an edge split where the terrain
    /// leaves it by more than this many pixels and the new points put on the terrain, so crests stay smooth over a coarse mesh; 0 off (the Tab
    /// panel's toggle). Needs tessellation.
    /// </summary>
    public static float RefinePixels { get; set; } = DefaultRefinePixels;

    /// <summary><see cref="RefinePixels"/> when the refinement is on.</summary>
    public const float DefaultRefinePixels = 0.5f;

    /// <summary>The root-mean-square slope every layer's relief is given at its tile's size (rise over run).</summary>
    public static float TargetSlope { get; set; } = 0.2f;

    /// <summary>
    /// How far a layer's depth follows its own scale: the depth <see cref="TargetSlope"/> gives it (as a multiple of <see cref="ReferenceDepth"/>) raised to
    /// this power. 0: every layer as deep; 1: fine textures shallow, coarse ones deep, in proportion; 0.5 the geometric middle.
    /// </summary>
    public static float ScaleFollow { get; set; } = 0.5f;

    /// <summary>The deepest a layer gets, in world units at <see cref="ReferenceDepth"/> (8: 80 cm, 120 cm at the default <see cref="Depth"/>; the characters walk
    /// on the undisplaced ground, so deeper relief buries their feet).</summary>
    public static float MaxDepth { get; set; } = 8;

    /// <summary>The displacement leaves out shapes wider than this (world units: 20, 2 m): the characters walk on the undisplaced surface, so broad swells
    /// swallow their feet; the parallax keeps them beyond the displacement. 0 keeps all.</summary>
    public static float BroadCut { get; set; } = 20;

    /// <summary>The cliff layer's depth (and its cap) as a multiple of the ground's.</summary>
    public static float CliffScale { get; set; } = 1;

    /// <summary>
    /// Biomes whose relief is scaled (BIOMES string id, so a translation mod keeps it; the rest 1): ground and cliff depth times this. ForestLand, all
    /// of Shark's ground: its layers come out 37 to 85 cm deep, the swamp round it about 26 (docs/render-terrain.md, "Depth").
    /// </summary>
    public static readonly IReadOnlyDictionary<string, float> BiomeDepth = new Dictionary<string, float>
    {
        ["4442-Newwworld.mod"] = 1 / 3f,   // ForestLand
    };

    /// <summary>A biome's <see cref="BiomeDepth"/> factor, 1 when it has none.</summary>
    public static float BiomeFactor(string stringId) => BiomeDepth.TryGetValue(stringId, out float f) ? f : 1;

    /// <summary>The share of relief the grass, slope, dirt and road layers get, each from its own height map, by how much of the pixel it shows (0: the
    /// base layer alone).</summary>
    public static float OverlayRelief { get; set; } = 1;

    /// <summary>How dark the relief's self-shadow gets on the sun (1: the sun fully hidden where it is deepest; 0 skips the sun's march).</summary>
    public static float SelfShadow { get; set; } = 0.6f;

    /// <summary>The tessellation's density as a multiple of its base (the ground's segments and the rocks' curve error both divided by it).</summary>
    public static float TessDetail { get; set; } = 0.5f;

    /// <summary>How much a layer's broad shapes are weighed down against its detail when its height is made (<see cref="NormalHeight.FromNormals"/>'s tilt).</summary>
    public const float Tilt = 0.5f;

    /// <summary>Patches within this horizontal distance of the eye are drawn tessellated: just beyond the end of the displacement (all with the refinement).</summary>
    public static float TessellatedRadius => RefinePixels > 0 ? float.MaxValue : DisplaceDistance + 10;

    /// <summary>
    /// The texture field of the terrain sandbox (the game's and the viewer's <c>--terrain-sandbox</c>): the centre of a square of tiles, one per terrain
    /// texture (<see cref="TerrainTextures.SetField"/>), drawn through the whole relief pipeline; null: none. Read once the terrain textures exist.
    /// </summary>
    public static System.Numerics.Vector2? FieldCentre { get; set; }

    /// <summary>The texture field holds the cliff layers (on a steep face; their vertical projections) instead of the ground ones.</summary>
    public static bool FieldCliffs { get; set; }

    /// <summary>A texture field tile's side in world units (the cliff field's: half of it).</summary>
    public const float FieldTile = 200;

    /// <summary>
    /// A layer's full relief depth in cm at the current settings (the terrain shaders' <c>reliefDepth</c> times <see cref="Depth"/>), from its steepness
    /// factor (1 / (side × its height's steepness), <see cref="NormalHeight.FromNormals"/>; 0 flat) and its tile's side in world units; for labels.
    /// </summary>
    public static float LayerDepthCm(float steep, float tile) =>
        steep <= 0 ? 0 : MathF.Min(MathF.Pow(TargetSlope * steep * tile / ReferenceDepth, ScaleFollow), MaxDepth / ReferenceDepth) * Depth * 10;
}
