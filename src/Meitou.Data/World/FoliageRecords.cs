using Meitou.Data.Fcs;

namespace Meitou.Data.World;

/// <summary>
/// FOLIAGE_LAYER <c>visibility range</c> (fcs_enums.def FoliageVisibilityRange). The game's distance is
/// <see cref="FoliageLayer.Range"/>.
/// </summary>
public enum FoliageVisibility
{
    /// <summary>
    /// CLOSE (0).
    /// </summary>
    Close,

    /// <summary>
    /// MEDIUM (1), the default.
    /// </summary>
    Medium,

    /// <summary>
    /// FAR (2), the farthest a grass or wind layer goes.
    /// </summary>
    Far,

    /// <summary>
    /// FEATURE (3).
    /// </summary>
    Feature
}

/// <summary>
/// A FOLIAGE_MESH record as the foliage placer reads it (docs/formats/foliage.md, "Records"). Fields keep their FCS names;
/// defaults are fcs.def's. <see cref="MinScale"/> / <see cref="MaxScale"/> are the game's scale draw bounds: the record's
/// <c>min height</c> / <c>max height</c> times 2, clamped to [0.01, 10].
/// </summary>
public sealed class FoliageMesh
{
    /// <summary>
    /// The FOLIAGE_MESH record.
    /// </summary>
    public required GameRecord Record { get; init; }
    /// <summary>
    /// The record's string id.
    /// </summary>
    public string StringId => Record.StringId;
    /// <summary>
    /// The record's name.
    /// </summary>
    public string Name => Record.Name;
    /// <summary>
    /// <c>mesh</c>: the mesh file.
    /// </summary>
    public required string MeshPath { get; init; }
    /// <summary>
    /// <c>leaves mesh</c>; null when empty.
    /// </summary>
    public string? LeavesMesh { get; init; }
    /// <summary>
    /// <c>texture map</c>; null when empty.
    /// </summary>
    public string? Texture { get; init; }
    /// <summary>
    /// <c>normal map</c>; null when empty.
    /// </summary>
    public string? Normal { get; init; }
    /// <summary>
    /// <c>texture map 2</c>; null when empty.
    /// </summary>
    public string? Texture2 { get; init; }
    /// <summary>
    /// <c>normal map 2</c>; null when empty.
    /// </summary>
    public string? Normal2 { get; init; }
    /// <summary>
    /// <c>leaves texture</c>; null when empty.
    /// </summary>
    public string? LeavesTexture { get; init; }
    /// <summary>
    /// <c>leaves normal</c>; null when empty.
    /// </summary>
    public string? LeavesNormal { get; init; }
    /// <summary>
    /// MapFeatureMode: 0 UV_MAPPED, 1 TRIPLANAR, 2 TERRAIN, 3 DUAL_TEXTURE, 4 FOLIAGE, 5 DUAL_TRIPLANAR, 6 EMISSIVE.
    /// </summary>
    public int MaterialType { get; init; }
    /// <summary>
    /// <c>alpha threshold</c> (0..255) of the mesh's cutout.
    /// </summary>
    public int AlphaThreshold { get; init; } = 128;
    /// <summary>
    /// <c>leaves alpha threshold</c> (0..255) of the leaves' cutout.
    /// </summary>
    public int LeavesAlphaThreshold { get; init; } = 80;
    /// <summary>
    /// <c>tile X</c>: the texture's tiling in x.
    /// </summary>
    public float TileX { get; init; } = 1;
    /// <summary>
    /// <c>tile Y</c>: the texture's tiling in y.
    /// </summary>
    public float TileY { get; init; } = 1;
    /// <summary>
    /// <c>specular mult</c>.
    /// </summary>
    public float SpecularMult { get; init; }
    /// <summary>
    /// <c>min height</c>: half the lower bound of the scale draw (<see cref="MinScale"/>).
    /// </summary>
    public float MinHeight { get; init; } = 1;
    /// <summary>
    /// <c>max height</c>: half the upper bound of the scale draw (<see cref="MaxScale"/>).
    /// </summary>
    public float MaxHeight { get; init; } = 1;
    /// <summary>
    /// <c>min slope</c>: the lowest ground slope a copy is placed on (height difference per 36 units).
    /// </summary>
    public float MinSlope { get; init; }
    /// <summary>
    /// <c>max slope</c>: the highest ground slope a copy is placed on (height difference per 36 units).
    /// </summary>
    public float MaxSlope { get; init; } = 10;
    /// <summary>
    /// <c>min altitude</c>: the lowest ground a copy is placed on.
    /// </summary>
    public float MinAltitude { get; init; }
    /// <summary>
    /// <c>max altitude</c>: the highest ground a copy is placed on.
    /// </summary>
    public float MaxAltitude { get; init; } = 20000;
    /// <summary>
    /// <c>slope align</c>: whether a copy's up follows the ground after its yaw.
    /// </summary>
    public bool SlopeAlign { get; init; } = true;
    /// <summary>
    /// <c>keep upright</c>: whether a copy that is not slope-aligned only turns about y.
    /// </summary>
    public bool KeepUpright { get; init; } = true;
    /// <summary>
    /// <c>floating</c>: whether a copy is raised to the water height.
    /// </summary>
    public bool Floating { get; init; }
    /// <summary>
    /// <c>avoid towns</c>: whether copies are rejected within a town's <c>no-foliage range</c>.
    /// </summary>
    public bool AvoidTowns { get; init; } = true;
    /// <summary>
    /// <c>limit to grass areas</c>: whether a place is drawn again (up to 21 times) while the overlay's R + G is at most 149.
    /// </summary>
    public bool LimitToGrassAreas { get; init; }
    /// <summary>
    /// <c>clustered</c>: whether copies are placed in clusters.
    /// </summary>
    public bool Clustered { get; init; }
    /// <summary>
    /// <c>cluster num min</c>: the fewest copies in a cluster.
    /// </summary>
    public int ClusterNumMin { get; init; } = 3;
    /// <summary>
    /// <c>cluster num max</c>: the most copies in a cluster.
    /// </summary>
    public int ClusterNumMax { get; init; } = 10;
    /// <summary>
    /// <c>cluster radius min</c>: the low end of a cluster's radius draw.
    /// </summary>
    public float ClusterRadiusMin { get; init; } = 100;
    /// <summary>
    /// <c>cluster radius max</c>: the high end of a cluster's radius draw.
    /// </summary>
    public float ClusterRadiusMax { get; init; } = 400;
    /// <summary>
    /// <c>child cluster radius</c>: added to the distance of the children placed round a copy.
    /// </summary>
    public float ChildClusterRadius { get; init; }
    /// <summary>
    /// <c>vertical offset min</c>: the low end of the height offset draw, times the copy's scale.
    /// </summary>
    public float VerticalOffsetMin { get; init; }
    /// <summary>
    /// <c>vertical offset max</c>: the high end of the height offset draw, times the copy's scale.
    /// </summary>
    public float VerticalOffsetMax { get; init; }
    /// <summary>
    /// <c>road avoidance</c>: a copy is rejected within this times its scale of a road (0: never).
    /// </summary>
    public float RoadAvoidance { get; init; } = 10;
    /// <summary>
    /// <c>grass spot</c>: the radius, times the copy's scale, of the grass-spot disc stamped into the overlay's G (0: none).
    /// </summary>
    public float GrassSpot { get; init; }
    /// <summary>
    /// <c>wind factor</c>: the material's sway (<c>windFactorX/Z = wind factor × 0.25</c>).
    /// </summary>
    public float WindFactor { get; init; } = 1;
    /// <summary>
    /// The BUILDING of <c>building type</c> (mineable resources), null for plain foliage.
    /// </summary>
    public GameRecord? BuildingType { get; init; }
    /// <summary>
    /// Child meshes (<c>meshes</c>) placed around every placed copy of this one.
    /// </summary>
    public List<FoliageMesh> Children { get; } = [];

    /// <summary>
    /// Lower bound of the scale draw: <c>clamp(2 × min height, 0.01, 10)</c>.
    /// </summary>
    public float MinScale => Math.Max(Math.Min(MinHeight * 2, FoliageLayout.MaxScale), FoliageLayout.MinScale);
    /// <summary>
    /// Upper bound of the scale draw: <c>clamp(2 × max height, 0.01, 10)</c>.
    /// </summary>
    public float MaxScale => Math.Max(Math.Min(MaxHeight * 2, FoliageLayout.MaxScale), FoliageLayout.MinScale);

    /// <summary>
    /// A mesh's material looks: FOLIAGE clips on the normal map's alpha and is double-sided (runtime-materials.md).
    /// </summary>
    public bool IsFoliageMaterial => MaterialType == 4;

    /// <summary>
    /// The mesh of FOLIAGE_MESH <paramref name="r"/> with its children, each record read once through <paramref name="cache"/>.
    /// </summary>
    public static FoliageMesh FromRecord(GameRecord r, GameDatabase db, Dictionary<string, FoliageMesh> cache)
    {
        if (cache.TryGetValue(r.StringId, out var known)) return known;
        static string? P(GameRecord r, string f) => r.GetPath(f) is { Length: > 0 } p ? p : null;
        var m = new FoliageMesh
        {
            Record = r,
            MeshPath = r.GetPath("mesh"),
            LeavesMesh = P(r, "leaves mesh"),
            Texture = P(r, "texture map"),
            Normal = P(r, "normal map"),
            Texture2 = P(r, "texture map 2"),
            Normal2 = P(r, "normal map 2"),
            LeavesTexture = P(r, "leaves texture"),
            LeavesNormal = P(r, "leaves normal"),
            MaterialType = r.GetInt("material type"),
            AlphaThreshold = r.GetInt("alpha threshold", 128),
            LeavesAlphaThreshold = r.GetInt("leaves alpha threshold", 80),
            TileX = r.GetFloat("tile X", 1),
            TileY = r.GetFloat("tile Y", 1),
            SpecularMult = r.GetFloat("specular mult"),
            MinHeight = r.GetFloat("min height", 1),
            MaxHeight = r.GetFloat("max height", 1),
            MinSlope = r.GetFloat("min slope"),
            MaxSlope = r.GetFloat("max slope", 10),
            MinAltitude = r.GetFloat("min altitude"),
            MaxAltitude = r.GetFloat("max altitude", 20000),
            SlopeAlign = r.GetBool("slope align", true),
            KeepUpright = r.GetBool("keep upright", true),
            Floating = r.GetBool("floating"),
            AvoidTowns = r.GetBool("avoid towns", true),
            LimitToGrassAreas = r.GetBool("limit to grass areas"),
            Clustered = r.GetBool("clustered"),
            ClusterNumMin = r.GetInt("cluster num min", 3),
            ClusterNumMax = r.GetInt("cluster num max", 10),
            ClusterRadiusMin = r.GetFloat("cluster radius min", 100),
            ClusterRadiusMax = r.GetFloat("cluster radius max", 400),
            ChildClusterRadius = r.GetFloat("child cluster radius"),
            VerticalOffsetMin = r.GetFloat("vertical offset min"),
            VerticalOffsetMax = r.GetFloat("vertical offset max"),
            RoadAvoidance = r.GetFloat("road avoidance", 10),
            GrassSpot = r.GetFloat("grass spot"),
            WindFactor = r.GetFloat("wind factor", 1),
            BuildingType = r.GetReferences("building type").Select(x => db.Find(x.TargetStringId)).FirstOrDefault(b => b is not null),
        };
        cache[r.StringId] = m;
        foreach (var c in r.GetReferences("meshes"))
            if (db.Find(c.TargetStringId) is { Type: FcsRecordType.FoliageMesh } child)
                m.Children.Add(FromRecord(child, db, cache));
        return m;
    }
}

/// <summary>
/// A GRASS record (docs/formats/foliage.md, "Grass").
/// </summary>
public sealed class FoliageGrass
{
    /// <summary>
    /// The GRASS record; null for one made by hand.
    /// </summary>
    public GameRecord? Record { get; init; }
    /// <summary>
    /// The record's string id.
    /// </summary>
    public string StringId { get; init; } = "";
    /// <summary>
    /// The record's name.
    /// </summary>
    public string Name { get; init; } = "";
    /// <summary>
    /// <c>grass sprite</c>; null when empty.
    /// </summary>
    public string? Sprite { get; init; }
    /// <summary>
    /// <c>color map</c>; null when empty.
    /// </summary>
    public string? ColourMap { get; init; }
    /// <summary>
    /// <c>cross quads</c>: whether a blade is two crossed quads.
    /// </summary>
    public bool CrossQuads { get; init; } = true;
    /// <summary>
    /// <c>density</c> (<see cref="PerSquareUnit"/>).
    /// </summary>
    public float Density { get; init; } = 1;
    /// <summary>
    /// <c>min width</c> (<see cref="QuadMinWidth"/>).
    /// </summary>
    public float MinWidth { get; init; } = 1;
    /// <summary>
    /// <c>max width</c> (<see cref="QuadMaxWidth"/>).
    /// </summary>
    public float MaxWidth { get; init; } = 1;
    /// <summary>
    /// <c>min height</c> (<see cref="QuadMinHeight"/>).
    /// </summary>
    public float MinHeight { get; init; } = 1;
    /// <summary>
    /// <c>max height</c> (<see cref="QuadMaxHeight"/>).
    /// </summary>
    public float MaxHeight { get; init; } = 1;
    /// <summary>
    /// <c>min altitude</c>: the lowest ground the grass grows on.
    /// </summary>
    public float MinAltitude { get; init; }
    /// <summary>
    /// <c>max altitude</c>: the highest ground the grass grows on.
    /// </summary>
    public float MaxAltitude { get; init; } = 20000;
    /// <summary>
    /// <c>max slope</c>: the steepest ground the grass grows on.
    /// </summary>
    public float MaxSlope { get; init; } = 10;
    /// <summary>
    /// <c>wind factor</c> (<see cref="SwayLength"/>).
    /// </summary>
    public float WindFactor { get; init; } = 1;
    /// <summary>
    /// <c>noise scale</c> of the coverage noise (five octaves at <c>noise scale × 72</c>).
    /// </summary>
    public float NoiseScale { get; init; } = 1.5f;
    /// <summary>
    /// <c>zero cutoff</c> of the coverage noise.
    /// </summary>
    public float ZeroCutoff { get; init; } = 0.5f;
    /// <summary>
    /// <c>brightness boost</c> of the coverage noise.
    /// </summary>
    public float BrightnessBoost { get; init; } = 3;
    /// <summary>
    /// <c>cap</c> of the coverage noise.
    /// </summary>
    public float Cap { get; init; } = 1;
    /// <summary>
    /// <c>blackout</c>: whether the coverage is multiplied by a second, shifted noise.
    /// </summary>
    public bool Blackout { get; init; } = true;
    /// <summary>
    /// <c>blackout noise scale</c> of the blackout noise.
    /// </summary>
    public float BlackoutNoiseScale { get; init; } = 10;
    /// <summary>
    /// <c>blackout zero cutoff</c> of the blackout noise.
    /// </summary>
    public float BlackoutZeroCutoff { get; init; } = 0.57f;

    /// <summary>
    /// Quad size bounds the game gives the grass loader: min width/height × 9, max × 17.
    /// </summary>
    public float QuadMinWidth => MinWidth * 9;
    /// <summary>
    /// See <see cref="QuadMinWidth"/>.
    /// </summary>
    public float QuadMinHeight => MinHeight * 9;
    /// <summary>
    /// See <see cref="QuadMinWidth"/>.
    /// </summary>
    public float QuadMaxWidth => MaxWidth * 17;
    /// <summary>
    /// See <see cref="QuadMinWidth"/>.
    /// </summary>
    public float QuadMaxHeight => MaxHeight * 17;
    /// <summary>
    /// Grass per square unit at full coverage (grass density setting 1): <c>density × 0.005</c>.
    /// </summary>
    public float PerSquareUnit => Density * 0.005f;
    /// <summary>
    /// Sway amplitude in units: <c>wind factor × 3</c>.
    /// </summary>
    public float SwayLength => WindFactor * 3;

    /// <summary>
    /// The grass of GRASS record <paramref name="r"/>.
    /// </summary>
    public static FoliageGrass FromRecord(GameRecord r)
    {
        static string? P(GameRecord r, string f) => r.GetPath(f) is { Length: > 0 } p ? p : null;
        return new FoliageGrass
        {
            Record = r,
            StringId = r.StringId,
            Name = r.Name,
            Sprite = P(r, "grass sprite"),
            ColourMap = P(r, "color map"),
            CrossQuads = r.GetBool("cross quads", true),
            Density = r.GetFloat("density", 1),
            MinWidth = r.GetFloat("min width", 1),
            MaxWidth = r.GetFloat("max width", 1),
            MinHeight = r.GetFloat("min height", 1),
            MaxHeight = r.GetFloat("max height", 1),
            MinAltitude = r.GetFloat("min altitude"),
            MaxAltitude = r.GetFloat("max altitude", 20000),
            MaxSlope = r.GetFloat("max slope", 10),
            WindFactor = r.GetFloat("wind factor", 1),
            NoiseScale = r.GetFloat("noise scale", 1.5f),
            ZeroCutoff = r.GetFloat("zero cutoff", 0.5f),
            BrightnessBoost = r.GetFloat("brightness boost", 3),
            Cap = r.GetFloat("cap", 1),
            Blackout = r.GetBool("blackout", true),
            BlackoutNoiseScale = r.GetFloat("blackout noise scale", 10),
            BlackoutZeroCutoff = r.GetFloat("blackout zero cutoff", 0.57f),
        };
    }
}

/// <summary>
/// A FOLIAGE_LAYER record: meshes with their count per zone, or grass types with their coverage channel.
/// </summary>
public sealed class FoliageLayer
{
    /// <summary>
    /// The FOLIAGE_LAYER record; null for one made by hand.
    /// </summary>
    public GameRecord? Record { get; init; }
    /// <summary>
    /// The record's string id.
    /// </summary>
    public string StringId { get; init; } = "";
    /// <summary>
    /// The record's name.
    /// </summary>
    public string Name { get; init; } = "";
    /// <summary>
    /// <c>meshes</c>: each FOLIAGE_MESH with the number placed per zone.
    /// </summary>
    public List<(FoliageMesh Mesh, int Count)> Meshes { get; } = [];
    /// <summary>
    /// <c>grass</c>: each GRASS with its coverage channel (0 reads the overlay's R, anything else G).
    /// </summary>
    public List<(FoliageGrass Grass, int Channel)> Grass { get; } = [];
    /// <summary>
    /// <c>wind</c>: whether the layer is a wind page.
    /// </summary>
    public bool Wind { get; init; }
    /// <summary>
    /// <c>visibility range</c>, clamped to the enum.
    /// </summary>
    public FoliageVisibility Visibility { get; init; } = FoliageVisibility.Medium;
    /// <summary>
    /// <c>lod range</c> × 10 (0 when unset).
    /// </summary>
    public float LodRange { get; init; }

    /// <summary>
    /// Whether the layer has grass.
    /// </summary>
    public bool IsGrass => Grass.Count > 0;

    /// <summary>
    /// Distance at which the layer's pages are dropped, at range setting 1: {50, 100, 800, 4000}[visibility] × 10. Grass and
    /// wind layers use at most FAR.
    /// </summary>
    public float Range => FoliageLayout.VisibilityRange(IsGrass || Wind ? (FoliageVisibility)Math.Min((int)Visibility, 2) : Visibility);

    /// <summary>
    /// Length of the fade between detail levels: 100 units for wind layers, 10 for the others.
    /// </summary>
    public float Transition => Wind ? 100 : 10;

    /// <summary>
    /// The layer of FOLIAGE_LAYER <paramref name="r"/>: its known grass and meshes in list order, the meshes read once through
    /// <paramref name="meshCache"/>.
    /// </summary>
    public static FoliageLayer FromRecord(GameRecord r, GameDatabase db, Dictionary<string, FoliageMesh> meshCache)
    {
        var layer = new FoliageLayer
        {
            Record = r,
            StringId = r.StringId,
            Name = r.Name,
            Wind = r.GetBool("wind"),
            Visibility = (FoliageVisibility)Math.Clamp(r.GetInt("visibility range", 1), 0, 3),
            LodRange = r.GetInt("lod range") * 10f,
        };
        foreach (var g in r.GetReferences("grass"))
            if (db.Find(g.TargetStringId) is { Type: FcsRecordType.Grass } grass)
                layer.Grass.Add((FoliageGrass.FromRecord(grass), g.Values.Value0));
        foreach (var m in r.GetReferences("meshes"))
            if (db.Find(m.TargetStringId) is { Type: FcsRecordType.FoliageMesh } mesh)
                layer.Meshes.Add((FoliageMesh.FromRecord(mesh, db, meshCache), m.Values.Value0));
        return layer;
    }
}

/// <summary>
/// The foliage layers of every biome, by the biome's <c>index</c> colour (BIOMES <c>foliage</c>, in list order).
/// </summary>
public sealed class FoliageCatalog
{
    /// <summary>
    /// Each biome's layers in list order, by the biome's <c>index</c> colour (rgb).
    /// </summary>
    public Dictionary<uint, List<FoliageLayer>> ByBiome { get; } = [];
    /// <summary>
    /// The layers by string id.
    /// </summary>
    public Dictionary<string, FoliageLayer> Layers { get; } = new(StringComparer.Ordinal);
    /// <summary>
    /// The meshes by string id.
    /// </summary>
    public Dictionary<string, FoliageMesh> Meshes { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// The layers of the biome coloured <paramref name="biomeIndex"/>; empty when there is none.
    /// </summary>
    public IReadOnlyList<FoliageLayer> LayersOf(uint biomeIndex) => ByBiome.TryGetValue(biomeIndex, out var l) ? l : [];

    /// <summary>
    /// Reads every BIOMES record's layers, the first record of a colour (by string id) winning.
    /// </summary>
    public static FoliageCatalog Load(GameDatabase db)
    {
        var c = new FoliageCatalog();
        foreach (var b in db.OfType(FcsRecordType.Biomes).OrderBy(r => r.StringId, StringComparer.Ordinal))
        {
            uint index = (uint)b.GetInt("index") & 0xFFFFFF;
            if (c.ByBiome.ContainsKey(index)) continue; // the first record of a colour wins, as BiomeTerrain.ByIndex
            var list = new List<FoliageLayer>();
            foreach (var f in b.GetReferences("foliage"))
            {
                if (db.Find(f.TargetStringId) is not { Type: FcsRecordType.FoliageLayer } lr) continue;
                if (!c.Layers.TryGetValue(lr.StringId, out var layer)) c.Layers[lr.StringId] = layer = FoliageLayer.FromRecord(lr, db, c.Meshes);
                list.Add(layer);
            }
            c.ByBiome[index] = list;
        }
        return c;
    }
}