using System.Numerics;
using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Kidnap;
using Meitou.Simulation.Locks;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Player;

namespace Meitou.Simulation.Crime;

/// <summary>
/// The escaping-slave test of the sight pass (<c>FUN_140859680</c> -> <c>FUN_140856b50</c> -> <c>FUN_140855a80</c>, <b>Verified</b>,
/// docs/game/ai-tasks.md "Escaping slave spotted"): whether a looker of the owner's faction that sees a slave (state 1) takes it for escaping. A
/// sneaker escapes; so does a slave without shackles; a shackled one only while it walks a move order away from its owner, or fights an ally of
/// the looker. The job 187 exemption of <c>FUN_140852700</c> is read on the looker, which never holds it, so it never applies.
/// </summary>
public static class EscapingSlave
{
    /// <summary>
    /// The looker's view factor must be above this (<c>DAT_141696c74</c>, <b>Verified</b>), as returned by the spot value <c>FUN_140853620</c>.
    /// </summary>
    public const float SpotValue = 0.05f;

    /// <summary>
    /// The owner reach of the walk test (<c>DAT_141fedd00</c>, 250, <b>Verified</b>; no writer).
    /// </summary>
    public const float OwnerReach = 250;

    /// <summary>
    /// The squared distance from the owner past which a walking slave escapes indoors too: three times <see cref="OwnerReach"/> squared
    /// (<c>DAT_14168a254</c>, <b>Verified</b>), about 433.
    /// </summary>
    public const float FarFromOwnerSquared = 3 * OwnerReach * OwnerReach;

    /// <summary>
    /// <c>FUN_140855a80</c> for a slave in state 1 seen by the NPC in slot <paramref name="looker"/>: sneaking out of a fight escapes; no worn
    /// shackles escapes (the boots item, else the armour item, is lockable armour: <c>FUN_1405c8a10</c>, whose slot 94 returns the item itself,
    /// locked or not); then, with its owner known, walking away from it (<see cref="WalksAway"/>) escapes, and in a fight it escapes when it fights
    /// an ally of the looker (<c>FUN_140998790</c>). An owner Meitou does not know (no master squad, or one still to load) tests nothing more
    /// here (<b>engine</b>; slave catching and the far-escape rule of <see cref="ArrestSystem"/> handle such a slave).
    /// </summary>
    public static bool Test(World world, int looker, int slave, CharacterCold c, CaptiveSystem? captives, CrimeSystem crimes)
    {
        if ((c.Orders & CharacterOrders.Sneak) != 0 && !c.InCombat) return true;
        if (!WearsShackles(c)) return true;
        if (ObedientSlave.OwnerOf(world, c) is not { } owner) return false;

        var table = world.Characters;
        if (WalksAway(world, looker, slave, c, table.Next[owner.Slot].Position, captives)) return true;

        return c.InCombat && FightsAllyOf(world, slave, table.Cold(looker)!.Faction, crimes);
    }

    /// <summary>
    /// The shackle slot of <c>FUN_1405c8a10</c>: the item worn in the boots section, else in the armour section, has a lock.
    /// </summary>
    static bool WearsShackles(CharacterCold c)
    {
        var items = c.Inventory?.Items;
        var worn = items?.Find(i => i.Section == "boots") ?? items?.Find(i => i.Section == "armour");

        return LockSystem.IsShackles(worn);
    }

    /// <summary>
    /// <c>FUN_140852750</c> with an owner at <paramref name="owner"/>: running away (rows 35, 127) escapes; else the current task's position
    /// (Tasker <c>+0x58</c>, here a move order's point) is farther than <see cref="OwnerReach"/> from the owner and farther from it than the slave
    /// stands, the slave's heading does not point at the owner (<c>FUN_1409b1960</c>: dot above 0), and it is outdoors, farther than
    /// <see cref="FarFromOwnerSquared"/> from the owner or does not look like a slave to the looker (<c>FUN_1405a6bb0</c>).
    /// </summary>
    static bool WalksAway(World world, int looker, int slave, CharacterCold c, Vector3 owner, CaptiveSystem? captives)
    {
        if (c.Running is { Row: TaskType.RunAway or TaskType.RunAwayHometown }) return true;
        if (OrderPoint(c) is not { } point) return false;

        ref readonly var n = ref world.Characters.Next[slave];
        float pointToOwner = Vector3.DistanceSquared(point, owner);
        float slaveToOwner = Vector3.DistanceSquared(n.Position, owner);
        if (pointToOwner <= OwnerReach * OwnerReach || slaveToOwner >= pointToOwner) return false;

        var toOwner = owner - n.Position;
        if (MathF.Sin(n.Yaw) * toOwner.X + MathF.Cos(n.Yaw) * toOwner.Z > 0) return false;

        bool looksLikeSlave = captives?.LooksLikeASlave(world.Characters.Cold(looker)!, c) ?? true;
        return world.Sight.Outdoors(slave) || slaveToOwner > FarFromOwnerSquared || !looksLikeSlave;
    }

    /// <summary>
    /// The point of the slave's task (Tasker <c>+0x58</c>), null for the zero point. Every planned step has its goal's point (<b>Verified</b>: the
    /// goal tasker's <c>+0x58</c> is the plan's root node point, <c>FUN_14050fe10</c>; each child node gets its parent's, <c>FUN_14051bae0</c>; each
    /// step's tasker is made at its node's), so the walks of the slave's own job goals have none (the Slave list's goals are made at zero,
    /// <c>FUN_14026b790</c> -> <c>FUN_140330530</c>) and an order's steps have its <see cref="OrderEntry.TaskPoint"/>.
    /// </summary>
    static Vector3? OrderPoint(CharacterCold c)
    {
        if (c.Running is not { Source: TaskSource.Order } || c.OrderQueue.Front is not { Started: true } order) return null;

        return order.TaskPoint == default ? null : order.TaskPoint;
    }

    /// <summary>
    /// <c>FUN_140998790</c>: the slave's fight target is a character of the looker's faction or an ally of it (<c>FUN_1406b2630</c>).
    /// </summary>
    static bool FightsAllyOf(World world, int slave, int faction, CrimeSystem crimes)
    {
        if (world.SystemOf<CombatSystem>() is not { } combat) return false;

        var fight = combat.StateOf(world.Characters.IdOf(slave));
        if (!fight.Fighting || world.Characters.Cold(fight.TargetSlot) is not { } target) return false;

        return crimes.Friendly(faction, target.Faction);
    }
}
