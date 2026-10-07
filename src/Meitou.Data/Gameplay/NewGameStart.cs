using System.Numerics;
using Meitou.Data.Fcs;

namespace Meitou.Data.Gameplay;

/// <summary>
/// A NEW_GAME_STARTOFF record, one entry of the new-game screen (docs/game/ui-screens.md section 3, <b>Verified</b> by probe: 13 records
/// in the install, 8 from the base data and 5 from <c>rebirth.mod</c>): the starting cash, the squad the player begins with and the
/// towns it may begin in. What the original does with each field beyond showing it is partly <b>Unknown</b> (economy.md: the hand-over of
/// <see cref="Money"/> into the purse was not traced).
/// </summary>
public sealed record NewGameStart
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    /// <summary><c>money</c>: "Starting cash".</summary>
    public int Money { get; init; }
    /// <summary><c>start pos X</c> and <c>start pos Z</c>: where the new-game map opens; the player picks a start town from <see cref="Towns"/>. Only <see cref="ForceStartPos"/> makes it the place the squad stands.</summary>
    public Vector2 StartPosition { get; init; }
    public bool ForceStartPos { get; init; }
    public string Description { get; init; } = "";
    public string Difficulty { get; init; } = "";
    public string Style { get; init; } = "";
    /// <summary><c>squad</c>: SQUAD_TEMPLATE records (a squad each) or CHARACTER records (one character each).</summary>
    public IReadOnlyList<RecordLink> Squad { get; init; } = [];
    /// <summary><c>town</c>: the towns the start may be in; the first is the default.</summary>
    public IReadOnlyList<RecordLink> Towns { get; init; } = [];
    /// <summary><c>faction relations</c>: overrides of the player's relations at the start (v0 = the value; the way they are applied is <b>Unknown</b>, factions-squads-towns.md section 3.2).</summary>
    public IReadOnlyList<RecordLink> FactionRelations { get; init; } = [];
    public IReadOnlyList<RecordLink> Research { get; init; } = [];
    /// <summary><c>force race</c>: the races the character creation is limited to.</summary>
    public IReadOnlyList<RecordLink> ForceRace { get; init; } = [];

    public static NewGameStart From(GameRecord r)
    {
        IReadOnlyList<RecordLink> Links(string list) => [.. r.GetReferences(list).Select(RecordLink.From)];
        return new NewGameStart
        {
            Id = r.StringId,
            Name = r.Name,
            Money = r.GetInt("money"),
            StartPosition = new Vector2(r.GetInt("start pos X"), r.GetInt("start pos Z")),
            ForceStartPos = r.GetBool("force start pos"),
            Description = r.GetString("description"),
            Difficulty = r.GetString("difficulty"),
            Style = r.GetString("style"),
            Squad = Links("squad"),
            Towns = Links("town"),
            FactionRelations = Links("faction relations"),
            Research = Links("research"),
            ForceRace = Links("force race"),
        };
    }

    /// <summary>The start-offs of the database, in its order.</summary>
    public static List<NewGameStart> LoadAll(GameDatabase db) => [.. db.OfType(FcsRecordType.NEW_GAME_STARTOFF).Select(From)];

    /// <summary>The standard start of the base game (difficulty "Default", the one with the Wanderer squad).</summary>
    public const string DefaultName = "Wanderer";

    /// <summary>Finds a start by name (case-insensitive) or string id.</summary>
    public static NewGameStart? Find(IEnumerable<NewGameStart> starts, string nameOrId) =>
        starts.FirstOrDefault(s => s.Id == nameOrId) ?? starts.FirstOrDefault(s => string.Equals(s.Name, nameOrId, StringComparison.OrdinalIgnoreCase));
}
