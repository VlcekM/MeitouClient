using Meitou.Data.Fcs;

namespace Meitou.Data.Gameplay;

/// <summary>
/// The editor's TownType, the TOWN <c>type</c> (docs/game/factions-squads-towns.md section 7; histogram Verified by probe).
/// </summary>
public enum TownType
{
    /// <summary>
    /// 0: a nest; nests are TOWN records the game spawns. Takes no artifacts.
    /// </summary>
    Nest = 0,
    /// <summary>
    /// 1: an outpost (the player's <c>player_town_template</c> is one).
    /// </summary>
    Outpost = 1,
    /// <summary>
    /// 2: a town (radius 2500 x the mult).
    /// </summary>
    Town = 2,
    /// <summary>
    /// 3: a village (radius 937.5 x the mult).
    /// </summary>
    Village = 3,
    /// <summary>
    /// 4: ruins (radius 875 x the mult).
    /// </summary>
    Ruins = 4,
    /// <summary>
    /// 5: a slave camp.
    /// </summary>
    SlaveCamp = 5,
    /// <summary>
    /// 6: a military base.
    /// </summary>
    Military = 6,
    /// <summary>
    /// 7: a prison.
    /// </summary>
    Prison = 7,
    /// <summary>
    /// 8: a placed marker nests can stand on; radius 0, it never blocks building and takes no artifacts.
    /// </summary>
    NestMarker = 8,
    /// <summary>
    /// 9: a point of interest (radius 437.5 x the mult).
    /// </summary>
    PointOfInterest = 9,
    /// <summary>
    /// 10: radius 0; takes no artifacts.
    /// </summary>
    Null = 10,
}

/// <summary>
/// A TOWN record (docs/game/factions-squads-towns.md section 7): its kind, faction, size and the lists that fill it with people. Placement in the
/// world (where the town is) is in the level data, not here (formats/zones.md). The setup rules of <c>Zones_TownSetup</c> (Observed) are applied to
/// the two numbers they change.
/// </summary>
public sealed record TownData
{
    /// <summary>
    /// The TOWN's string id.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// The TOWN's name in the active language.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// <c>type</c>.
    /// </summary>
    public TownType Type { get; init; }

    /// <summary>
    /// <c>faction</c>, the first link; null when the town has none.
    /// </summary>
    public string? Faction { get; init; }

    /// <summary>
    /// <c>is public</c>.
    /// </summary>
    public bool IsPublic { get; init; }

    /// <summary>
    /// <c>is secret</c>.
    /// </summary>
    public bool IsSecret { get; init; }

    /// <summary>
    /// <c>size radius</c>: read by the game only for the nest debris scatter (<c>FUN_140938440</c>), not the town's radius (that is
    /// <see cref="Radius"/>).
    /// </summary>
    public float SizeRadius { get; init; }

    /// <summary>
    /// <c>town radius mult</c> after the setup rule: values below 0.1 become 1, then it is capped at 5.
    /// </summary>
    public float TownRadiusMult { get; init; } = 1;

    /// <summary>
    /// The town's radius as the game uses it for a non-player town (<c>Town::vfunc_84</c>, docs/game/buildings-production.md "Towns"):
    /// <see cref="TownRadiusMult"/> x 2500 for a Town, 937.5 a Village, 875 Ruins, 437.5 a PointOfInterest, 0 a NestMarker or Null, 1250 the rest.
    /// </summary>
    public float Radius => TownRadiusMult * Type switch
    {
        TownType.Town => 2500,
        TownType.Village => 937.5f,
        TownType.Ruins => 875,
        TownType.PointOfInterest => 437.5f,
        TownType.NestMarker or TownType.Null => 0,
        _ => 1250,
    };

    /// <summary>
    /// <c>no-foliage range</c> after the setup rule: 0 for a NEST_MARKER.
    /// </summary>
    public float NoFoliageRange { get; init; }

    /// <summary>
    /// <c>distant mesh</c>.
    /// </summary>
    public bool DistantMesh { get; init; }

    /// <summary>
    /// <c>num centrepoints</c>.
    /// </summary>
    public int NumCentrepoints { get; init; }

    /// <summary>
    /// <c>nest resident population</c>.
    /// </summary>
    public int NestResidentPopulation { get; init; }

    /// <summary>
    /// <c>spawn in town centre</c>.
    /// </summary>
    public bool SpawnInTownCentre { get; init; }

    /// <summary>
    /// <c>residents</c> (squad templates; v0, v1 = the count range, section 6.4) and <c>residents override</c>.
    /// </summary>
    public IReadOnlyList<RecordLink> Residents { get; init; } = [];

    /// <summary>
    /// <c>residents override</c> (see <see cref="Residents"/>).
    /// </summary>
    public bool ResidentsOverride { get; init; }

    /// <summary>
    /// <c>roaming squads</c>.
    /// </summary>
    public IReadOnlyList<RecordLink> RoamingSquads { get; init; } = [];

    /// <summary>
    /// <c>bar squads</c>.
    /// </summary>
    public IReadOnlyList<RecordLink> BarSquads { get; init; } = [];

    /// <summary>
    /// <c>default resident</c>.
    /// </summary>
    public IReadOnlyList<RecordLink> DefaultResident { get; init; } = [];

    /// <summary>
    /// <c>override town</c> candidates and the <c>world state</c> conditions of this town's own gate.
    /// </summary>
    public IReadOnlyList<RecordLink> OverrideTown { get; init; } = [];

    /// <summary>
    /// <c>world state</c>: the WORLD_EVENT_STATE conditions of the town's own gate.
    /// </summary>
    public IReadOnlyList<RecordLink> WorldState { get; init; } = [];

    /// <summary>
    /// <c>debris</c> (v0, v1 = the least and most to spawn: the code, not fcs.def's text) and <c>debris building</c>.
    /// </summary>
    public IReadOnlyList<RecordLink> Debris { get; init; } = [];

    /// <summary>
    /// <c>debris building</c> (see <see cref="Debris"/>).
    /// </summary>
    public IReadOnlyList<RecordLink> DebrisBuilding { get; init; } = [];

    /// <summary>
    /// <c>loot spawn</c>.
    /// </summary>
    public IReadOnlyList<RecordLink> LootSpawn { get; init; } = [];

    /// <summary>
    /// <c>trade culture</c>: ITEMS_CULTURE records (<see cref="Economy.TradeCulture"/>).
    /// </summary>
    public IReadOnlyList<RecordLink> TradeCulture { get; init; } = [];

    /// <summary>
    /// <c>material</c>.
    /// </summary>
    public IReadOnlyList<RecordLink> Material { get; init; } = [];

    /// <summary>
    /// <c>unexplored name</c>.
    /// </summary>
    public string UnexploredName { get; init; } = "";

    /// <summary>
    /// <c>gear artifacts min value</c>: the lowest value of a gear artifact the town takes (docs/game/economy.md "Artifacts").
    /// </summary>
    public int GearArtifactsMinValue { get; init; }

    /// <summary>
    /// <c>gear artifacts max value</c>: the highest value of a gear artifact it takes; 0: it takes none.
    /// </summary>
    public int GearArtifactsMaxValue { get; init; }

    /// <summary>
    /// <c>item artifacts min value</c>: the lowest value of an item artifact the town takes.
    /// </summary>
    public int ItemArtifactsMinValue { get; init; }

    /// <summary>
    /// <c>item artifacts max value</c>: the highest value of an item artifact it takes; 0: it takes none.
    /// </summary>
    public int ItemArtifactsMaxValue { get; init; }

    /// <summary>
    /// Reads a TOWN record with the setup rules applied (<see cref="TownRadiusMult"/>, <see cref="NoFoliageRange"/>).
    /// </summary>
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

    /// <summary>
    /// Every TOWN record, in the database's order.
    /// </summary>
    public static List<TownData> LoadAll(GameDatabase db) => [.. db.OfType(FcsRecordType.Town).Select(From)];
}