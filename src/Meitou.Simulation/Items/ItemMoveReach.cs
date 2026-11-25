using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Population;

namespace Meitou.Simulation.Items;

/// <summary>
/// The reach of an item move between two inventories of different owners (<c>FUN_14070d290</c>, <b>Verified</b>; docs/game/economy.md "Trading
/// range"): unflagged, the owners' positions within <see cref="Range"/> on the X/Z plane; flagged (an imprisoned owner, a source standing in a town
/// that restricts, <see cref="Restricts"/>) a much shorter reach.
/// </summary>
static class ItemMoveReach
{
    /// <summary>
    /// The X/Z reach of an item move between two inventories that is not flagged (<c>FUN_14070d290</c>, <b>Verified</b>: 1000, stored squared).
    /// </summary>
    internal const float Range = 1000;

    /// <summary>
    /// The relation at or below which a public town counts as hostile for <see cref="Restricts"/> (<c>FUN_1406b26d0</c>: -30).
    /// </summary>
    const float HostileRelation = -30;

    /// <summary>
    /// Whether an item may move from the player character in slot <paramref name="source"/> to the one in <paramref name="taker"/>, else posts "Out
    /// of trading range." (<c>FUN_14070d290</c> for the right click's one-unit move <c>FUN_140713d20</c> and the placement <c>FUN_140715560</c>):
    /// within <see cref="Range"/>, as a move between the player's own windows is not stealing; flagged for an imprisoned one or a source standing in
    /// a town that restricts, within <see cref="ContainerAccess.TradingRange"/>.
    /// </summary>
    internal static bool CharactersNear(PopulationData? towns, World world, int source, int taker)
    {
        var table = world.Characters;
        Vector3 from = table.Next[source].Position, to = table.Next[taker].Position;
        bool flagged = Imprisoned(table.Cold(source)) || Imprisoned(table.Cold(taker)) || Restricts(towns, world, from);
        float reach = flagged ? ContainerAccess.TradingRange : Range;
        if (Vector2.Distance(new(from.X, from.Z), new(to.X, to.Z)) <= reach) return true;

        world.Post("Out of trading range.");
        return false;
    }

    /// <summary>
    /// Whether the town at <paramref name="at"/> flags an item move whose source stands there (Town slot 0x260 <c>FUN_140926fc0</c>, <b>Verified</b>):
    /// none when its faction is the player's or an ally (relation 50 or more); else a public town (<c>is public</c>) not hostile (relation above -30)
    /// only when it is a prison, any other town always. No town: none.
    /// </summary>
    internal static bool Restricts(PopulationData? towns, World world, Vector3 at)
    {
        if (towns is not { } data || AiSystem.TownAt(data, world, at) is not { Site: >= 0 } town || town.Site >= data.SiteFactions.Count)
            return false;

        int player = world.Player.Faction;
        int faction = data.SiteFactions[town.Site];
        if (faction < 0 || player < 0) return true;
        if (faction == player) return false;

        float relation = data.Relations.Get(faction, player);
        if (relation >= FactionRelations.AllyThreshold) return false;

        var info = data.Sites[town.Site].Town;
        return !info.IsPublic || relation <= HostileRelation || info.Type == TownType.Prison;
    }

    /// <summary>
    /// Whether the owner is imprisoned, which flags a move to or from it (the owner's <c>+0x2f8</c> 2).
    /// </summary>
    static bool Imprisoned(CharacterCold? owner) => owner?.InSomething == Locks.HeldState.Imprisoned;
}
