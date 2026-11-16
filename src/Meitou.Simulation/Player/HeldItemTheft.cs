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
        ItemHolder.Site => Goods(world, held.From.Site) is not { } goods || goods.Owner == world.Player.Faction,
        _ => false,
    };

    /// <summary>
    /// The goods a site's window shows as their owner's: a container with contents, or a town building's machine
    /// (<see cref="Production.ProductionSystem.TownGoods"/>); null for any other site.
    /// </summary>
    static Crime.StealSource? Goods(World world, string site) =>
        world.Fixtures.Find(site) is { Contents: not null } box ? Crime.StealSource.Of(box)
        : world.SystemOf<Production.ProductionSystem>()?.TownGoods(site);

    /// <summary>
    /// The stolen mark a held item gets when it goes to <paramref name="taker"/>'s faction: from a take that marks
    /// (<see cref="Crime.CrimeSystem.MarksTheft"/>) the body (its faction, itself and its squad), from another faction's container its owner
    /// (<see cref="Crime.CrimeSystem.OwnerMark"/>), else unchanged; a drag marks only an unmarked item, a give (<paramref name="overwrite"/>) any
    /// (<c>Item::notifyTheftFrom</c> call sites). Handed to its owner's faction it loses the mark (the engine clears the owner when the item goes to
    /// its owner or a member of the owner's faction). Into the container or storage machine at <paramref name="site"/>: out of a player's window as
    /// <see cref="PutMark"/> with the site's owner (<see cref="OwnerOf"/>), out of another's (a character's, a container's or a town machine's) the
    /// take's mark of an unmarked item whatever the site's owner, a marked one unchanged (<c>FUN_140715560</c>, <b>Observed</b>: the mark needs
    /// the source window not the player's, the clearing the player's).
    /// </summary>
    internal static StolenMark MarkFor(World world, HeldItem held, CharacterCold? taker, bool overwrite, string? site = null)
    {
        var item = held.Item;
        if (taker is not null)
            return item.StolenFrom >= 0 && item.StolenFrom == taker.Faction ? StolenMark.None : TheftMark(world, held, taker.Faction, overwrite);
        if (site is null) return item.Mark;

        return FromPlayerWindow(world, held) ? PutMark(item, OwnerOf(world, site)) : TheftMark(world, held, -1, overwrite);
    }

    /// <summary>
    /// The faction owning the container or machine at <paramref name="site"/>: a container's or town machine's owner, else (the player's own
    /// machine) the player's.
    /// </summary>
    internal static int OwnerOf(World world, string site) => Goods(world, site)?.Owner ?? world.Player.Faction;

    /// <summary>
    /// The mark a take out of another's window gives the held item (<see cref="MarkFor"/>): the body's from a character that marks
    /// (<see cref="Crime.CrimeSystem.MarksTheft"/>), the owner's (<see cref="Crime.CrimeSystem.OwnerMark"/>) from goods not of
    /// <paramref name="takerFaction"/> (-1: any goods), else the item's own; only an unmarked item but with <paramref name="overwrite"/>.
    /// </summary>
    static StolenMark TheftMark(World world, HeldItem held, int takerFaction, bool overwrite)
    {
        var item = held.Item;
        var table = world.Characters;
        if (!overwrite && item.Mark != StolenMark.None) return item.Mark;

        if (held.From.Holder == ItemHolder.Character && table.TryResolveNext(held.From.Character, out int v)
                && table.Cold(v) is { IsPlayer: false } victim
            && table.TryResolveNext(held.Actor, out int a) && Crime.CrimeSystem.MarksTheft(world, table.Cold(a)!,
                    victim)) return StolenMark.Of(victim, table.IdOf(v));
        if (held.From.Holder == ItemHolder.Site && Goods(world, held.From.Site) is { Owner: >= 0 } box && box.Owner != takerFaction
            && Crime.CrimeSystem.IsStealing(world, box))
            return Crime.CrimeSystem.OwnerMark(world, box.Building, box.Owner);
        return item.Mark;
    }

    /// <summary>
    /// The stolen mark <paramref name="item"/> keeps when the player puts it into a container or storage machine of <paramref name="owner"/>'s
    /// (drag <c>FUN_140715560</c>, right click <c>FUN_140713d20</c>, <b>Observed</b>): none when that is the faction it was stolen from, which so
    /// gets it back; else its own (an unmarked item stays unmarked).
    /// </summary>
    internal static StolenMark PutMark(ItemInstance item, int owner) => item.StolenFrom >= 0 && item.StolenFrom == owner ? StolenMark.None : item.Mark;

    /// <summary>
    /// The held item leaves another faction's container or character for another owner: the pending crime of the move (a container: against its
    /// owner character, Looting for a drag, <c>FUN_140715560</c>, Theft for a give, <c>FUN_140712490</c>; a character:
    /// <see cref="Crime.CrimeSystem.TakeCode"/>) and the steal roll
    /// (<see cref="Crime.CrimeSystem.Steal"/>, <see cref="Crime.CrimeSystem.Pickpocket"/>). True when the thief was caught and the item goes back.
    /// </summary>
    internal static bool Caught(World world, HeldItem held, Crime.CrimeSystem.TakeKind kind)
    {
        var table = world.Characters;
        if (world.SystemOf<Crime.CrimeSystem>() is not { } crimes || !table.TryResolveNext(held.Actor, out int thief))
            return false;
        if (held.From.Holder == ItemHolder.Site)
            return Goods(world, held.From.Site) is { } box && box.Owner >= 0
                && box.Owner != world.Player.Faction
                && crimes.Steal(world, thief, box, kind);
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
