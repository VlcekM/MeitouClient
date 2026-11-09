using Meitou.Data.Fcs;

namespace Meitou.Data.Gameplay;

/// <summary>
/// A WILDLIFE_BIRDS record (docs/game/birds.md; the loader is the <c>BirdManager</c>'s <c>FUN_140261090</c>, <b>Verified</b>): one kind of flock. The
/// BIOME_GROUP <c>birds</c> references say where it flies, their first value the group's amount of it (<see cref="AmountIn"/>).
/// </summary>
public sealed record BirdFlockData
{
    public required string Id { get; init; }

    /// <summary>
    /// The mesh (a static one, drawn as a hardware-instanced <c>HWBirds</c> material) and its texture.
    /// </summary>
    public string Mesh { get; init; } = "";
    public string Texture { get; init; } = "";

    /// <summary>
    /// <c>spawn count</c>: the birds of a flock.
    /// </summary>
    public int SpawnCount { get; init; }

    /// <summary>
    /// <c>speed</c>: units per second.
    /// </summary>
    public float Speed { get; init; }

    /// <summary>
    /// <c>height</c>: the cruising height over the ground.
    /// </summary>
    public float Height { get; init; }
    public float GroundHeight { get; init; }
    public float GroundStrength { get; init; }
    public float ContourStrength { get; init; }

    /// <summary>
    /// <c>distance</c>: a radius in world units round the camera controller (<b>Verified</b>, <c>FUN_140260620</c>).
    /// </summary>
    public float Distance { get; init; }
    public bool Flocking { get; init; }
    public bool Circling { get; init; }
    public float SpawnHeightMin { get; init; }
    public float SpawnHeightMax { get; init; }
    public float ScaleMin { get; init; } = 1;
    public float ScaleMax { get; init; } = 1;

    /// <summary>
    /// BIOME_GROUP string id to the flocks it holds (the reference's first value).
    /// </summary>
    public IReadOnlyDictionary<string, int> Amounts { get; init; } = new Dictionary<string, int>();

    public int AmountIn(string group) => Amounts.GetValueOrDefault(group);

    /// <summary>
    /// Whether the BIOME_GROUP lists this kind (even with an amount of 0).
    /// </summary>
    public bool Lists(string group) => Amounts.ContainsKey(group);

    /// <summary>
    /// Every WILDLIFE_BIRDS record with the groups that list it.
    /// </summary>
    public static List<BirdFlockData> LoadAll(GameDatabase db)
    {
        var groups = db.OfType(FcsRecordType.BiomeGroup).ToList();
        var list = new List<BirdFlockData>();
        foreach (var r in db.OfType(FcsRecordType.WildlifeBirds))
        {
            var amounts = new Dictionary<string, int>();
            foreach (var g in groups)
                foreach (var link in g.GetReferences("birds"))
                    if (link.TargetStringId == r.StringId) amounts[g.StringId] = link.Values.Value0;

            list.Add(new BirdFlockData
            {
                Id = r.StringId,
                Mesh = r.GetPath("mesh"),
                Texture = r.GetPath("texture"),
                SpawnCount = r.GetInt("spawn count"),
                Speed = r.GetFloat("speed"),
                Height = r.GetFloat("height"),
                GroundHeight = r.GetFloat("ground height"),
                GroundStrength = r.GetFloat("ground strength"),
                ContourStrength = r.GetFloat("contour strength"),
                Distance = r.GetFloat("distance"),
                Flocking = r.GetBool("flocking"),
                Circling = r.GetBool("circling"),
                SpawnHeightMin = r.GetFloat("spawn height min"),
                SpawnHeightMax = r.GetFloat("spawn height max"),
                ScaleMin = r.GetFloat("scale min", 1),
                ScaleMax = r.GetFloat("scale max", 1),
                Amounts = amounts,
            });
        }

        return list;
    }
}
