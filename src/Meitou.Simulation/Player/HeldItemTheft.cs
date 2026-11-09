using Meitou.Simulation.Items;
using Meitou.Simulation.Population;

namespace Meitou.Simulation.Player;

/// <summary>
/// The crime side of moving the held item (<see cref="HeldItems"/>) out of another owner's window: whether it is the player's own window, the
/// stolen mark it gets, the steal roll of the move and the loot it leaves.
/// </summary>
static class HeldItemTheft
{
    /// <summary>
    /// Whether the held item came out of one of the player's own windows (theirs or a player-faction character's), so leaving it is not a theft and
    /// it may be dropped.
    /// </summary>
    internal static bool FromPlayerWindow(World world, HeldItem held) => held.From.Holder switch
    {
        ItemHolder.Character => world.Characters.TryResolveNext(held.From.Character, out int s) && world.Characters.Cold(s) is { } c && (c.IsPlayer
                || c.Faction == world.Player.Faction),
        ItemHolder.Site => world.Fixtures.Find(held.From.Site) is not { Contents: not null } box || box.Owner == world.Player.Faction,
        _ => false,
    };

    /// <summary>
    /// The stolen mark a held item gets when it goes to <paramref name="taker"/>'s faction: from a take that marks
    /// (<see cref="Crime.CrimeSystem.MarksTheft"/>) the body (its faction, itself and its squad), from another faction's container that faction
    /// (engine choice, as the container's take), else unchanged; a drag marks only an unmarked item, a give (<paramref name="overwrite"/>) any
    /// (<c>Item::notifyTheftFrom</c> call sites). Handed to its owner's faction it loses the mark (the engine clears the owner when the item goes to
    /// its owner or a member of the owner's faction).
    /// </summary>
    internal static StolenMark MarkFor(World world, HeldItem held, CharacterCold? taker, bool overwrite)
    {
        var item = held.Item;
        var table = world.Characters;
        if (taker is null) return item.Mark;
        if (item.StolenFrom >= 0 && item.StolenFrom == taker.Faction) return StolenMark.None;
        bool open = overwrite || item.Mark == StolenMark.None;
        if (held.From.Holder == ItemHolder.Character && table.TryResolveNext(held.From.Character, out int v)
                && table.Cold(v) is { IsPlayer: false } victim
            && table.TryResolveNext(held.Actor, out int a) && Crime.CrimeSystem.MarksTheft(world, table.Cold(a)!,
                    victim) && open) return StolenMark.Of(victim, table.IdOf(v));
        if (held.From.Holder == ItemHolder.Site && world.Fixtures.Find(held.From.Site) is { Owner: >= 0 } box && box.Owner != taker.Faction
            && Crime.CrimeSystem.IsStealing(world, box) && open) return StolenMark.OfFaction(box.Owner);
        return item.Mark;
    }

    /// <summary>
    /// The held item leaves another faction's container or character for another owner: the pending crime of the move (a container: Looting for a
    /// drag, <c>FUN_140715560</c>, Theft for a give, <c>FUN_140712490</c>; a character: <see cref="Crime.CrimeSystem.TakeCode"/>) and the steal roll
    /// (<see cref="Crime.CrimeSystem.Steal"/>, <see cref="Crime.CrimeSystem.Pickpocket"/>). True when the thief was caught and the item goes back.
    /// </summary>
    internal static bool Caught(World world, HeldItem held, Crime.CrimeSystem.TakeKind kind)
    {
        var table = world.Characters;
        if (world.SystemOf<Crime.CrimeSystem>() is not { } crimes || !table.TryResolveNext(held.Actor, out int thief))
            return false;
        if (held.From.Holder == ItemHolder.Site)
            return world.Fixtures.Find(held.From.Site) is { Contents: not null } box && box.Owner >= 0
                && box.Owner != world.Player.Faction
                && crimes.Steal(world, thief, box, kind == Crime.CrimeSystem.TakeKind.Drag ? Crime.CrimeCode.Looting : Crime.CrimeCode.Theft);
        return held.From.Holder == ItemHolder.Character && table.TryResolveNext(held.From.Character,
                out int owner) && table.Cold(owner) is { IsPlayer: false }
            && crimes.Pickpocket(world, thief, owner, held.Item, kind);
    }

    /// <summary>
    /// The held item went to another owner out of the open loot: the body may be butchered (a death item) and the looting seen.
    /// </summary>
    internal static void LeftLoot(World world, HeldItem held, bool death)
    {
        if (held.From.Holder == ItemHolder.Character && world.SystemOf<Carrying.CarrySystem>() is { } carry && carry.OpenLoot(world) is { } open
                && open.Body == held.From.Character)
            carry.TookHeld(world, death);
    }
}
