using Meitou.Data.Fcs;

namespace Meitou.Data.Gameplay;

/// <summary>
/// A SQUAD_TEMPLATE (or UNIQUE_SQUAD_TEMPLATE, which has the same squad fields) as docs/game/factions-squads-towns.md section 4
/// describes what the game reads. Only the fields the squad factory (stage 2) and the town residents need are typed; the shop
/// and building-choice fields stay in the record. How the counts of <see cref="Squad"/>, <see cref="Squad2"/> and the random
/// picks combine into a squad is section 5.1, a later stage.
/// </summary>
public sealed record SquadTemplate
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    /// <summary>True for a UNIQUE_SQUAD_TEMPLATE.</summary>
    public bool IsUnique { get; init; }
    /// <summary><c>leader</c>: only the first entry is used by the game (<c>FUN_140583a10</c>); null when the list is empty. V1 = 100 marks a guaranteed leader in 24 templates.</summary>
    public RecordLink? Leader { get; init; }
    /// <summary><c>squad</c> (CHARACTER; v0, v1 = the count range).</summary>
    public IReadOnlyList<RecordLink> Squad { get; init; } = [];
    /// <summary><c>squad2</c>, the second half: member role 1.</summary>
    public IReadOnlyList<RecordLink> Squad2 { get; init; } = [];
    /// <summary><c>choosefrom list</c>: characters picked at random, weighted by v0 (0 counts as 100).</summary>
    public IReadOnlyList<RecordLink> ChooseFrom { get; init; } = [];
    public int NumRandomChars { get; init; }
    public int NumRandomCharsMax { get; init; }
    /// <summary><c>animals</c> / <c>animals2</c> (ANIMAL_CHARACTER; v2 = age 0 to 100).</summary>
    public IReadOnlyList<RecordLink> Animals { get; init; } = [];
    public IReadOnlyList<RecordLink> Animals2 { get; init; } = [];
    /// <summary><c>slaves</c> and <c>prisoners</c>: whole squads created recursively.</summary>
    public IReadOnlyList<RecordLink> Slaves { get; init; } = [];
    public IReadOnlyList<RecordLink> Prisoners { get; init; } = [];
    /// <summary><c>housemates</c>: further squads spawned with this one (v0, v1 = min, max).</summary>
    public IReadOnlyList<RecordLink> Housemates { get; init; } = [];
    /// <summary><c>faction</c>: the owner; absent, the spawn code asks the town or searches the factions (section 4).</summary>
    public string? Faction { get; init; }
    /// <summary><c>world state</c>: AND gate for the spawn (WORLD_EVENT_STATE links).</summary>
    public IReadOnlyList<RecordLink> WorldState { get; init; } = [];
    /// <summary><c>roaming military</c>: forces squad mode 2 even with a home town.</summary>
    public bool RoamingMilitary { get; init; }
    /// <summary><c>dont multiply</c>: exempt from the Squad size multiplier setting.</summary>
    public bool DontMultiply { get; init; }
    public IReadOnlyList<RecordLink> AiPackages { get; init; } = [];
    public float BloodSmellMult { get; init; }
    public bool PatrolApproachesTowns { get; init; }
    public int ForceSpeed { get; init; }
    /// <summary><c>building</c>: the home building a resident squad uses (first entry); <c>building designation</c> is the editor's BuildingDesignation.</summary>
    public RecordLink? Building { get; init; }
    public int BuildingDesignation { get; init; }
    /// <summary><c>nest</c> links.</summary>
    public IReadOnlyList<RecordLink> Nest { get; init; } = [];

    public static SquadTemplate From(GameRecord r)
    {
        IReadOnlyList<RecordLink> Links(string list) => [.. r.GetReferences(list).Select(RecordLink.From)];
        var leader = r.GetReferences("leader");
        var building = r.GetReferences("building");
        var faction = r.GetReferences("faction");
        return new SquadTemplate
        {
            Id = r.StringId,
            Name = r.Name,
            IsUnique = r.Type == FcsRecordType.UNIQUE_SQUAD_TEMPLATE,
            Leader = leader.Count > 0 ? RecordLink.From(leader[0]) : null,
            Squad = Links("squad"),
            Squad2 = Links("squad2"),
            ChooseFrom = Links("choosefrom list"),
            NumRandomChars = r.GetInt("num random chars"),
            NumRandomCharsMax = r.GetInt("num random chars max"),
            Animals = Links("animals"),
            Animals2 = Links("animals2"),
            Slaves = Links("slaves"),
            Prisoners = Links("prisoners"),
            Housemates = Links("housemates"),
            Faction = faction.Count > 0 ? faction[0].TargetStringId : null,
            WorldState = Links("world state"),
            RoamingMilitary = r.GetBool("roaming military"),
            DontMultiply = r.GetBool("dont multiply"),
            AiPackages = Links("AI packages"),
            BloodSmellMult = r.GetFloat("blood smell mult"),
            PatrolApproachesTowns = r.GetBool("patrol approaches towns"),
            ForceSpeed = r.GetInt("force speed"),
            Building = building.Count > 0 ? RecordLink.From(building[0]) : null,
            BuildingDesignation = r.GetInt("building designation"),
            Nest = Links("nest"),
        };
    }

    /// <summary>Every SQUAD_TEMPLATE record, then every UNIQUE_SQUAD_TEMPLATE, in the database's order.</summary>
    public static List<SquadTemplate> LoadAll(GameDatabase db) =>
        [.. db.OfType(FcsRecordType.SQUAD_TEMPLATE).Select(From), .. db.OfType(FcsRecordType.UNIQUE_SQUAD_TEMPLATE).Select(From)];
}
