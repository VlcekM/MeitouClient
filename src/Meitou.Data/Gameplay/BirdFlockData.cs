using Meitou.Data.Fcs;

namespace Meitou.Data.Gameplay;

/// <summary>
/// A WILDLIFE_BIRDS record (docs/game/birds.md; the loader is the <c>BirdManager</c>'s <c>FUN_140261090</c>, <b>Verified</b>): one kind of flock. The
/// BIOME_GROUP <c>birds</c> references say where it flies, their first value the group's amount of it (<see cref="AmountIn"/>).
/// </summary>
public sealed record BirdFlockData
{
    /// <summary>
    /// The WILDLIFE_BIRDS's string id.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// The mesh (a static one, drawn as a hardware-instanced <c>HWBirds</c> material) and its texture.
    /// </summary>
    public string Mesh { get; init; } = "";

    /// <summary>
    /// <c>texture</c>: the mesh's texture.
    /// </summary>
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

    /// <summary>
    /// <c>ground height</c> (docs/game/birds.md).
    /// </summary>
    public float GroundHeight { get; init; }

    /// <summary>
    /// <c>ground strength</c> (docs/game/birds.md).
    /// </summary>
    public float GroundStrength { get; init; }

    /// <summary>
    /// <c>contour strength</c> (docs/game/birds.md).
    /// </summary>
    public float ContourStrength { get; init; }

    /// <summary>
    /// <c>distance</c>: read as a squared radius round the camera (<b>Unknown</b>: the unit).
    /// </summary>
    public float Distance { get; init; }

    /// <summary>
    /// <c>flocking</c>.
    /// </summary>
    public bool Flocking { get; init; }

    /// <summary>
    /// <c>circling</c>.
    /// </summary>
    public bool Circling { get; init; }

    /// <summary>
    /// <c>spawn height min</c>.
    /// </summary>
    public float SpawnHeightMin { get; init; }

    /// <summary>
    /// <c>spawn height max</c>.
    /// </summary>
    public float SpawnHeightMax { get; init; }

    /// <summary>
    /// <c>scale min</c> (default 1).
    /// </summary>
    public float ScaleMin { get; init; } = 1;

    /// <summary>
    /// <c>scale max</c> (default 1).
    /// </summary>
    public float ScaleMax { get; init; } = 1;

    /// <summary>
    /// BIOME_GROUP string id to the flocks it holds (the reference's first value).
    /// </summary>
    public IReadOnlyDictionary<string, int> Amounts { get; init; } = new Dictionary<string, int>();

    /// <summary>
    /// The flocks of this kind BIOME_GROUP <paramref name="group"/> holds; 0 when it does not list the kind.
    /// </summary>
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
