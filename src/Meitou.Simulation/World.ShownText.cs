using Meitou.Data;
using Meitou.Data.Gameplay;
using Meitou.Simulation.Construction;
using Meitou.Simulation.Factions;

namespace Meitou.Simulation;

/// <summary>
/// Names as the mods' text sources show them (<see cref="ShownText"/>, docs/modding.md "Shown text"), for the text the simulation builds for the
/// screen: notices, floaters, the dialogue log and the views only the interface reads. Display only: the names the game decides with, compares,
/// saves and hashes stay its own. Each returns the name unchanged at the cost of one check when no mod adds a text source.
/// </summary>
public sealed partial class World
{
    /// <summary>
    /// The game data the population system was made from (null in a world without one, such as a bare test world).
    /// </summary>
    GameDatabase? ShownData => SystemOf<Population.PopulationSystem>()?.Data.Db;

    /// <summary>
    /// The name <paramref name="name"/> of a <paramref name="what"/> (<c>character</c>, <c>squad</c>, <c>outpost</c>, <c>faction</c>,
    /// <c>place</c>) made from record <paramref name="record"/> ("" none), as shown (<see cref="GameDatabase.ShownName"/>).
    /// </summary>
    internal string ShownName(string what, string record, string name) =>
        !ShownText.Any ? name
            : ShownData is { } db ? db.ShownName(what, record, name) : ShownText.Name(what, record, "", name);

    /// <summary>
    /// Character <paramref name="c"/>'s name as shown.
    /// </summary>
    internal string ShownName(CharacterCold c) => !ShownText.Any ? c.Name : ShownName("character", c.RecordId, c.Name);

    /// <summary>
    /// The name of the character <paramref name="id"/> as shown; empty when the id no longer resolves.
    /// </summary>
    internal string ShownName(CharacterId id) =>
        Characters.TryResolveNext(id, out int slot) && Characters.Cold(slot) is { } c ? ShownName(c) : "";

    /// <summary>
    /// The name of an item's owner <paramref name="owner"/> as shown (<see cref="CharacterTable.OwnerNameOf"/>): null for none, empty when the id no
    /// longer resolves.
    /// </summary>
    internal string? ShownOwnerNameOf(CharacterId owner) => owner.IsNone ? null : ShownName(owner);

    /// <summary>
    /// Squad <paramref name="squad"/>'s name as shown (made from its SQUAD_TEMPLATE, or the player's own).
    /// </summary>
    internal string ShownName(Squad squad) => !ShownText.Any ? squad.Name : ShownName("squad", squad.TemplateId, squad.Name);

    /// <summary>
    /// Outpost <paramref name="town"/>'s name as shown (the player's own, made from <see cref="PlayerTown.Template"/>).
    /// </summary>
    internal string ShownName(PlayerTown town) => !ShownText.Any ? town.Name : ShownName("outpost", PlayerTown.Template, town.Name);

    /// <summary>
    /// Faction <paramref name="faction"/>'s record name as shown (<see cref="FactionData.Name"/>, the data's name, which the game's own text
    /// uses even after the player renames his faction).
    /// </summary>
    internal static string ShownName(FactionData faction) => ShownText.Record(faction.Id, faction.Name);

    /// <summary>
    /// <paramref name="name"/>, a name the game gave faction <paramref name="faction"/> (a position in the faction list), as shown: the faction's
    /// record name asked as that record's, the player's rename as a name of its own; any other text (a fallback, an unknown index) as it is.
    /// </summary>
    internal string ShownFaction(int faction, string name)
    {
        if (!ShownText.Any || EventLog.Relations(this) is not { } relations || !relations.Contains(faction)) return name;
        var f = relations.Factions[faction];
        if (name == f.Name) return ShownText.Record(f.Id, name);
        return faction == Player.Faction && name == Player.FactionName ? ShownText.Name("faction", f.Id, f.Name, name) : name;
    }
}
