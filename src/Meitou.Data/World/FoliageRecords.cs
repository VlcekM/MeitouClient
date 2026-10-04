using Meitou.Data.Fcs;

namespace Meitou.Data.World;

/// <summary>
/// FOLIAGE_LAYER <c>visibility range</c> (fcs_enums.def FoliageVisibilityRange). The game's distance is
/// <see cref="FoliageLayer.Range"/>.
/// </summary>
public enum FoliageVisibility { Close, Medium, Far, Feature }

/// <summary>
/// A FOLIAGE_MESH record as the foliage placer reads it (docs/formats/foliage.md, "Records"). Fields keep their FCS names;
/// defaults are fcs.def's. <see cref="MinScale"/> / <see cref="MaxScale"/> are the game's scale draw bounds: the record's
/// <c>min height</c> / <c>max height</c> times 2, clamped to [0.01, 10].
/// </summary>
public sealed class FoliageMesh
{
    public required GameRecord Record { get; init; }
    public string StringId => Record.StringId;
    public string Name => Record.Name;
    public required string MeshPath { get; init; }
    public string? LeavesMesh { get; init; }
    public string? Texture { get; init; }
    public string? Normal { get; init; }
    public string? Texture2 { get; init; }
    public string? Normal2 { get; init; }
    public string? LeavesTexture { get; init; }
    public string? LeavesNormal { get; init; }
    /// <summary>MapFeatureMode: 0 UV_MAPPED, 1 TRIPLANAR, 2 TERRAIN, 3 DUAL_TEXTURE, 4 FOLIAGE, 5 DUAL_TRIPLANAR, 6 EMISSIVE.</summary>
    public int MaterialType { get; init; }
    public int AlphaThreshold { get; init; } = 128;
    public int LeavesAlphaThreshold { get; init; } = 80;
    public float TileX { get; init; } = 1;
    public float TileY { get; init; } = 1;
    public float SpecularMult { get; init; }
    public float MinHeight { get; init; } = 1;
    public float MaxHeight { get; init; } = 1;
    public float MinSlope { get; init; }
    public float MaxSlope { get; init; } = 10;
    public float MinAltitude { get; init; }
    public float MaxAltitude { get; init; } = 20000;
    public bool SlopeAlign { get; init; } = true;
    public bool KeepUpright { get; init; } = true;
    public bool Floating { get; init; }
    public bool AvoidTowns { get; init; } = true;
    public bool LimitToGrassAreas { get; init; }
    public bool Clustered { get; init; }
    public int ClusterNumMin { get; init; } = 3;
    public int ClusterNumMax { get; init; } = 10;
    public float ClusterRadiusMin { get; init; } = 100;
    public float ClusterRadiusMax { get; init; } = 400;
    public float ChildClusterRadius { get; init; }
    public float VerticalOffsetMin { get; init; }
    public float VerticalOffsetMax { get; init; }
    public float RoadAvoidance { get; init; } = 10;
    public float GrassSpot { get; init; }
    public float WindFactor { get; init; } = 1;
    /// <summary>The BUILDING of <c>building type</c> (mineable resources), null for plain foliage.</summary>
    public GameRecord? BuildingType { get; init; }
    /// <summary>Child meshes (<c>meshes</c>) placed around every placed copy of this one.</summary>
    public List<FoliageMesh> Children { get; } = [];

    /// <summary>Lower bound of the scale draw: <c>clamp(2 × min height, 0.01, 10)</c>.</summary>
    public float MinScale => Math.Max(Math.Min(MinHeight * 2, FoliageLayout.MaxScale), FoliageLayout.MinScale);
    /// <summary>Upper bound of the scale draw: <c>clamp(2 × max height, 0.01, 10)</c>.</summary>
    public float MaxScale => Math.Max(Math.Min(MaxHeight * 2, FoliageLayout.MaxScale), FoliageLayout.MinScale);

    /// <summary>A mesh's material looks: FOLIAGE clips on the normal map's alpha and is double-sided (runtime-materials.md).</summary>
    public bool IsFoliageMaterial => MaterialType == 4;

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
            if (db.Find(c.TargetStringId) is { Type: FcsRecordType.FOLIAGE_MESH } child)
                m.Children.Add(FromRecord(child, db, cache));
        return m;
    }
}

/// <summary>A GRASS record (docs/formats/foliage.md, "Grass").</summary>
public sealed class FoliageGrass
{
    public GameRecord? Record { get; init; }
    public string StringId { get; init; } = "";
    public string Name { get; init; } = "";
    public string? Sprite { get; init; }
    public string? ColourMap { get; init; }
    public bool CrossQuads { get; init; } = true;
    public float Density { get; init; } = 1;
    public float MinWidth { get; init; } = 1;
    public float MaxWidth { get; init; } = 1;
    public float MinHeight { get; init; } = 1;
    public float MaxHeight { get; init; } = 1;
    public float MinAltitude { get; init; }
    public float MaxAltitude { get; init; } = 20000;
    public float MaxSlope { get; init; } = 10;
    public float WindFactor { get; init; } = 1;
    public float NoiseScale { get; init; } = 1.5f;
    public float ZeroCutoff { get; init; } = 0.5f;
    public float BrightnessBoost { get; init; } = 3;
    public float Cap { get; init; } = 1;
    public bool Blackout { get; init; } = true;
    public float BlackoutNoiseScale { get; init; } = 10;
    public float BlackoutZeroCutoff { get; init; } = 0.57f;

    /// <summary>Quad size bounds the game gives the grass loader: min width/height × 9, max × 17.</summary>
    public float QuadMinWidth => MinWidth * 9;
    public float QuadMinHeight => MinHeight * 9;
    public float QuadMaxWidth => MaxWidth * 17;
    public float QuadMaxHeight => MaxHeight * 17;
    /// <summary>Grass per square unit at full coverage (grass density setting 1): <c>density × 0.005</c>.</summary>
    public float PerSquareUnit => Density * 0.005f;
    /// <summary>Sway amplitude in units: <c>wind factor × 3</c>.</summary>
    public float SwayLength => WindFactor * 3;

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

/// <summary>A FOLIAGE_LAYER record: meshes with their count per zone, or grass types with their coverage channel.</summary>
public sealed class FoliageLayer
{
    public GameRecord? Record { get; init; }
    public string StringId { get; init; } = "";
    public string Name { get; init; } = "";
    public List<(FoliageMesh Mesh, int Count)> Meshes { get; } = [];
    public List<(FoliageGrass Grass, int Channel)> Grass { get; } = [];
    public bool Wind { get; init; }
    public FoliageVisibility Visibility { get; init; } = FoliageVisibility.Medium;
    /// <summary><c>lod range</c> × 10 (0 when unset).</summary>
    public float LodRange { get; init; }

    public bool IsGrass => Grass.Count > 0;

    /// <summary>
    /// Distance at which the layer's pages are dropped, at range setting 1: {50, 100, 800, 4000}[visibility] × 10. Grass and
    /// wind layers use at most FAR.
    /// </summary>
    public float Range => FoliageLayout.VisibilityRange(IsGrass || Wind ? (FoliageVisibility)Math.Min((int)Visibility, 2) : Visibility);

    /// <summary>Length of the fade between detail levels: 100 units for wind layers, 10 for the others.</summary>
    public float Transition => Wind ? 100 : 10;

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
            if (db.Find(g.TargetStringId) is { Type: FcsRecordType.GRASS } grass)
                layer.Grass.Add((FoliageGrass.FromRecord(grass), g.Values.Value0));
        foreach (var m in r.GetReferences("meshes"))
            if (db.Find(m.TargetStringId) is { Type: FcsRecordType.FOLIAGE_MESH } mesh)
                layer.Meshes.Add((FoliageMesh.FromRecord(mesh, db, meshCache), m.Values.Value0));
        return layer;
    }
}

/// <summary>The foliage layers of every biome, by the biome's <c>index</c> colour (BIOMES <c>foliage</c>, in list order).</summary>
public sealed class FoliageCatalog
{
    public Dictionary<uint, List<FoliageLayer>> ByBiome { get; } = [];
    public Dictionary<string, FoliageLayer> Layers { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, FoliageMesh> Meshes { get; } = new(StringComparer.Ordinal);

    public IReadOnlyList<FoliageLayer> LayersOf(uint biomeIndex) => ByBiome.TryGetValue(biomeIndex, out var l) ? l : [];

    public static FoliageCatalog Load(GameDatabase db)
    {
        var c = new FoliageCatalog();
        foreach (var b in db.OfType(FcsRecordType.BIOMES).OrderBy(r => r.StringId, StringComparer.Ordinal))
        {
            uint index = (uint)b.GetInt("index") & 0xFFFFFF;
            if (c.ByBiome.ContainsKey(index)) continue; // the first record of a colour wins, as BiomeTerrain.ByIndex
            var list = new List<FoliageLayer>();
            foreach (var f in b.GetReferences("foliage"))
            {
                if (db.Find(f.TargetStringId) is not { Type: FcsRecordType.FOLIAGE_LAYER } lr) continue;
                if (!c.Layers.TryGetValue(lr.StringId, out var layer)) c.Layers[lr.StringId] = layer = FoliageLayer.FromRecord(lr, db, c.Meshes);
                list.Add(layer);
            }
            c.ByBiome[index] = list;
        }
        return c;
    }
}
