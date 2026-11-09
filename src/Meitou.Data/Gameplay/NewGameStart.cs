using System.Numerics;
using Meitou.Data.Fcs;

namespace Meitou.Data.Gameplay;

/// <summary>
/// A NEW_GAME_STARTOFF record, one entry of the new-game screen (docs/game/ui-screens.md section 3, <b>Verified</b> by probe: 13 records in the
/// install, 8 from the base data and 5 from <c>rebirth.mod</c>): the starting cash, the squad the player begins with and the towns it may begin in.
/// What the original does with each field beyond showing it is partly <b>Unknown</b> (economy.md: the hand-over of <see cref="Money"/> into the purse
/// was not traced).
/// </summary>
public sealed record NewGameStart
{
    /// <summary>
    /// The standard start of the base game (difficulty "Default", the one with the Wanderer squad).
    /// </summary>
    public const string DefaultName = "Wanderer";

    /// <summary>
    /// The data files' name when set apart from <see cref="Name"/>; null: the same.
    /// </summary>
    string? _baseName;

    /// <summary>
    /// The NEW_GAME_STARTOFF's string id.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// The start's name in the active language.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// The name in the data files (<see cref="GameRecord.BaseName"/>), so a start is found by its English name in any language.
    /// </summary>
    public string BaseName { get => _baseName ?? Name; init => _baseName = value; }

    /// <summary>
    /// <c>money</c>: "Starting cash".
    /// </summary>
    public int Money { get; init; }

    /// <summary>
    /// <c>start pos X</c> and <c>start pos Z</c>: the place the squad stands when <see cref="ForceStartPos"/> is set; otherwise unused (the town is
    /// drawn from <see cref="Towns"/>).
    /// </summary>
    public Vector2 StartPosition { get; init; }

    /// <summary>
    /// <c>force start pos</c>: the squad starts at <see cref="StartPosition"/>.
    /// </summary>
    public bool ForceStartPos { get; init; }

    /// <summary>
    /// <c>description</c>: the new-game screen's text of the start.
    /// </summary>
    public string Description { get; init; } = "";

    /// <summary>
    /// <c>difficulty</c>: shown on the new-game screen.
    /// </summary>
    public string Difficulty { get; init; } = "";

    /// <summary>
    /// <c>style</c>: shown on the new-game screen.
    /// </summary>
    public string Style { get; init; } = "";

    /// <summary>
    /// <c>squad</c>: SQUAD_TEMPLATE records (a squad each) or CHARACTER records (one character each).
    /// </summary>
    public IReadOnlyList<RecordLink> Squad { get; init; } = [];

    /// <summary>
    /// <c>town</c>: the towns the start may be in; one placement of any of them is drawn.
    /// </summary>
    public IReadOnlyList<RecordLink> Towns { get; init; } = [];

    /// <summary>
    /// <c>faction relations</c>: each sets that faction's relation with the player to v0, both ways.
    /// </summary>
    public IReadOnlyList<RecordLink> FactionRelations { get; init; } = [];

    /// <summary>
    /// <c>research</c>.
    /// </summary>
    public IReadOnlyList<RecordLink> Research { get; init; } = [];

    /// <summary>
    /// <c>force race</c>: the races the character creation is limited to.
    /// </summary>
    public IReadOnlyList<RecordLink> ForceRace { get; init; } = [];

    /// <summary>
    /// Reads a NEW_GAME_STARTOFF record.
    /// </summary>
    public static NewGameStart From(GameRecord r)
    {
        IReadOnlyList<RecordLink> Links(string list) => [.. r.GetReferences(list).Select(RecordLink.From)];

        return new NewGameStart
        {
            Id = r.StringId,
            Name = r.Name,
            BaseName = r.BaseName,
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

    /// <summary>
    /// The start-offs of the database, in its order.
    /// </summary>
    public static List<NewGameStart> LoadAll(GameDatabase db) => [.. db.OfType(FcsRecordType.NewGameStartoff).Select(From)];

    /// <summary>
    /// Finds a start by name (case-insensitive; the shown or the data-file name) or string id.
    /// </summary>
    public static NewGameStart? Find(IEnumerable<NewGameStart> starts, string nameOrId) =>
        starts.FirstOrDefault(s => s.Id == nameOrId)
        ?? starts.FirstOrDefault(s => string.Equals(s.Name, nameOrId, StringComparison.OrdinalIgnoreCase))
        ?? starts.FirstOrDefault(s => string.Equals(s.BaseName, nameOrId, StringComparison.OrdinalIgnoreCase));
}
