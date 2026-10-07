using Meitou.Data.Fcs;

namespace Meitou.Data.Gameplay;

/// <summary>The editor's TownType, the TOWN <c>type</c> (docs/game/factions-squads-towns.md section 7; histogram Verified by probe).</summary>
public enum TownType
{
    Nest = 0, Outpost = 1, Town = 2, Village = 3, Ruins = 4, SlaveCamp = 5, Military = 6, Prison = 7, NestMarker = 8, PointOfInterest = 9, Null = 10,
}

/// <summary>
/// A TOWN record (docs/game/factions-squads-towns.md section 7): its kind, faction, size and the lists that fill it with people.
/// Placement in the world (where the town is) is in the level data, not here (formats/zones.md). The setup rules of
/// <c>Zones_TownSetup</c> (Observed) are applied to the two numbers they change.
/// </summary>
public sealed record TownData
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public TownType Type { get; init; }
    /// <summary><c>faction</c>, the first link; null when the town has none.</summary>
    public string? Faction { get; init; }
    public bool IsPublic { get; init; }
    public bool IsSecret { get; init; }
    public float SizeRadius { get; init; }
    /// <summary><c>town radius mult</c> after the setup rule: values below 0.1 become 1, then it is capped at 5.</summary>
    public float TownRadiusMult { get; init; } = 1;
    /// <summary><c>no-foliage range</c> after the setup rule: 0 for a NEST_MARKER.</summary>
    public float NoFoliageRange { get; init; }
    public bool DistantMesh { get; init; }
    public int NumCentrepoints { get; init; }
    public int NestResidentPopulation { get; init; }
    public bool SpawnInTownCentre { get; init; }
    /// <summary><c>residents</c> (squad templates; v0, v1 = the count range, section 6.4) and <c>residents override</c>.</summary>
    public IReadOnlyList<RecordLink> Residents { get; init; } = [];
    public bool ResidentsOverride { get; init; }
    public IReadOnlyList<RecordLink> RoamingSquads { get; init; } = [];
    public IReadOnlyList<RecordLink> BarSquads { get; init; } = [];
    public IReadOnlyList<RecordLink> DefaultResident { get; init; } = [];
    /// <summary><c>override town</c> candidates and the <c>world state</c> conditions of this town's own gate.</summary>
    public IReadOnlyList<RecordLink> OverrideTown { get; init; } = [];
    public IReadOnlyList<RecordLink> WorldState { get; init; } = [];
    /// <summary><c>debris</c> (v0, v1 = the least and most to spawn: the code, not fcs.def's text) and <c>debris building</c>.</summary>
    public IReadOnlyList<RecordLink> Debris { get; init; } = [];
    public IReadOnlyList<RecordLink> DebrisBuilding { get; init; } = [];
    public IReadOnlyList<RecordLink> LootSpawn { get; init; } = [];
    public IReadOnlyList<RecordLink> TradeCulture { get; init; } = [];
    public IReadOnlyList<RecordLink> Material { get; init; } = [];
    public string UnexploredName { get; init; } = "";
    public int GearArtifactsMinValue { get; init; }
    public int GearArtifactsMaxValue { get; init; }
    public int ItemArtifactsMinValue { get; init; }
    public int ItemArtifactsMaxValue { get; init; }

    public static TownData From(GameRecord r)
    {
        IReadOnlyList<RecordLink> Links(string list) => [.. r.GetReferences(list).Select(RecordLink.From)];
        var type = (TownType)r.GetInt("type");
        float mult = r.GetFloat("town radius mult", 1);
        mult = mult < 0.1f ? 1 : Math.Min(mult, 5);
        var faction = r.GetReferences("faction");
        return new TownData
        {
            Id = r.StringId,
            Name = r.Name,
            Type = type,
            Faction = faction.Count > 0 ? faction[0].TargetStringId : null,
            IsPublic = r.GetBool("is public"),
            IsSecret = r.GetBool("is secret"),
            SizeRadius = r.GetFloat("size radius"),
            TownRadiusMult = mult,
            NoFoliageRange = type == TownType.NestMarker ? 0 : r.GetFloat("no-foliage range"),
            DistantMesh = r.GetBool("distant mesh"),
            NumCentrepoints = r.GetInt("num centrepoints"),
            NestResidentPopulation = r.GetInt("nest resident population"),
            SpawnInTownCentre = r.GetBool("spawn in town centre"),
            Residents = Links("residents"),
            ResidentsOverride = r.GetBool("residents override"),
            RoamingSquads = Links("roaming squads"),
            BarSquads = Links("bar squads"),
            DefaultResident = Links("default resident"),
            OverrideTown = Links("override town"),
            WorldState = Links("world state"),
            Debris = Links("debris"),
            DebrisBuilding = Links("debris building"),
            LootSpawn = Links("loot spawn"),
            TradeCulture = Links("trade culture"),
            Material = Links("material"),
            UnexploredName = r.GetString("unexplored name"),
            GearArtifactsMinValue = r.GetInt("gear artifacts min value"),
            GearArtifactsMaxValue = r.GetInt("gear artifacts max value"),
            ItemArtifactsMinValue = r.GetInt("item artifacts min value"),
            ItemArtifactsMaxValue = r.GetInt("item artifacts max value"),
        };
    }

    /// <summary>Every TOWN record, in the database's order.</summary>
    public static List<TownData> LoadAll(GameDatabase db) => [.. db.OfType(FcsRecordType.TOWN).Select(From)];
}
