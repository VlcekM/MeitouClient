using Meitou.Data.Fcs;

namespace Meitou.Data.Gameplay;

/// <summary>
/// A WORLD_EVENT_STATE (docs/game/factions-squads-towns.md "Relations and world states"): a condition on the unique characters and the factions'
/// stand towards the player, as the original's reader (<c>read_NPC_is_n4</c>) keeps it. Entries naming a record the data lacks are dropped, as the
/// reader drops them.
/// </summary>
public sealed record WorldEventStateData
{
    /// <summary>
    /// The WORLD_EVENT_STATE's string id.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// The WORLD_EVENT_STATE's name in the active language.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// <c>NPC is</c>: a CHARACTER and the unique state (V0: 0 dead, 1 alive, 2 imprisoned) it must have.
    /// </summary>
    public IReadOnlyList<(string Character, int State)> NpcIs { get; init; } = [];

    /// <summary>
    /// <c>NPC is NOT</c>: a CHARACTER and the unique state it must not have.
    /// </summary>
    public IReadOnlyList<(string Character, int State)> NpcIsNot { get; init; } = [];

    /// <summary>
    /// <c>player ally</c> / <c>player enemy</c>: a FACTION and whether it must (V0 = 1) or must not be the player's ally / hostile to the player.
    /// </summary>
    public IReadOnlyList<(string Faction, bool Value)> PlayerAlly { get; init; } = [];

    /// <summary>
    /// <c>player enemy</c>: a FACTION and whether it must (V0 = 1) or must not be hostile to the player.
    /// </summary>
    public IReadOnlyList<(string Faction, bool Value)> PlayerEnemy { get; init; } = [];

    /// <summary>
    /// <c>player involvement</c>: an <c>NPC is</c> other than alive, or an <c>NPC is NOT</c> alive, also needs the player to have been involved.
    /// </summary>
    public bool PlayerInvolvement { get; init; }

    /// <summary>
    /// Reads a WORLD_EVENT_STATE record.
    /// </summary>
    public static WorldEventStateData From(GameDatabase db, GameRecord r)
    {
        IReadOnlyList<(string, int)> Npcs(string list) =>
            [.. r.GetReferences(list).Where(x => db.Find(x.TargetStringId) is not null).Select(x => (x.TargetStringId, x.Values.Value0))];
        IReadOnlyList<(string, bool)> Factions(string list) =>
            [.. r.GetReferences(list)
                .Where(x => db.Find(x.TargetStringId) is { Type: FcsRecordType.Faction })
                .Select(x => (x.TargetStringId, x.Values.Value0 == 1))];

        return new WorldEventStateData
        {
            Id = r.StringId,
            Name = r.Name,
            NpcIs = Npcs("NPC is"),
            NpcIsNot = Npcs("NPC is NOT"),
            PlayerAlly = Factions("player ally"),
            PlayerEnemy = Factions("player enemy"),
            PlayerInvolvement = r.GetBool("player involvement"),
        };
    }
}
