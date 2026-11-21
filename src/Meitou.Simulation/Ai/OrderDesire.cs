using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Combat;
using Meitou.Simulation.FirstAid;
using Meitou.Simulation.Player;

namespace Meitou.Simulation.Ai;

/// <summary>
/// The desire half of the order step's pop (<c>FUN_140510920</c> → <c>FUN_14060fd80</c>, docs/game/ai-tasks.md "Finished tests", <b>Verified</b>):
/// the row's desire function (TaskData <c>+0x108</c>) of the order's target and point, times its finder's score only for a row with an effect
/// (TaskData <c>+0x128</c>, as 112's 81: its cage finder is asked by its owner's check, <c>ArrestSystem</c>'s <c>CanRun</c>). The functions, each resolved from the registration in <c>FUN_140319a60</c> through its stub to the AI vtable slot: 1 for
/// the rows of slots 19 and 43, 0.1 for those of slot 39 (76, 186, 201, 286), and their own for the rest below. Pure reads (serial phases).
/// </summary>
public static class OrderDesire
{
    /// <summary>
    /// AI slot 39's constant (<c>FUN_140641560</c>, <c>DAT_14167fad0</c>).
    /// </summary>
    const float Low = 0.1f;

    /// <summary>
    /// The lift orders' desire for a target that is no character (<c>FUN_14059e1a0</c>, <c>DAT_14167e8d8</c>).
    /// </summary>
    const float NoCharacter = 0.5f;

    /// <summary>
    /// Following's factor on the followed character's <c>followMe</c> (AI slot 15 <c>FUN_1405a33f0</c>, <c>DAT_14167e8d8</c>).
    /// </summary>
    const float FollowShare = 0.5f;

    /// <summary>
    /// The order's desire for the character at <paramref name="slot"/>; null for a row with no TaskData row (54 Rest), which is not asked.
    /// </summary>
    public static float? Of(World world, int slot, OrderEntry order)
    {
        var cold = world.Characters.Cold(slot)!;
        return order.Row switch
        {
            _ when TaskDataTable.Of(order.Row) is null => null,

            // AI slot 15 (FUN_1405a33f0): 0.5 x the followed character's followMe (at least 0.1); 1 for a target that does not resolve. Never 0,
            // so a follow order never leaves by it.
            TaskType.StayCloseToTarget or TaskType.Unnamed289 => FollowDesire(world, order.Target),
            TaskType.PickLock or TaskType.PickLockOnShackles or TaskType.BruteForceShackles or TaskType.BruteForceLock => Low,

            // AI slot 72 (FUN_140792650): 0 while the character itself is down (Character slot 6 FUN_1405c9e10).
            TaskType.Pickup => CharacterFacts.Down(cold) ? 0 : 1,

            // AI slot 44 (FUN_1405991c0): 0 for the character itself as target, which its own senses never list.
            TaskType.Bodyguard => order.Target == world.Characters.IdOf(slot) ? 0 : 1,
            TaskType.LiftPersonSnatchingAllowed or TaskType.LiftPersonSnatchingAllowedInTownOnly when order.Owner is Crime.ArrestSystem arrests
                => ArrestLiftDesire(world, slot, cold, order, arrests),
            TaskType.LiftPersonPlayerOrder or TaskType.KidnapOrder or TaskType.LiftPersonSnatchingAllowed
                or TaskType.LiftPersonSnatchingAllowedInTownOnly => LiftDesire(world, slot, cold, order.Target),
            TaskType.UseTurret => TurretDesire(world, slot, cold),
            TaskType.FirstAidOrder => world.SystemOf<FirstAidSystem>()?.TreatOrderDesire(world, slot, order.Target, robot: false),
            TaskType.FirstAidRobot => world.SystemOf<FirstAidSystem>()?.TreatOrderDesire(world, slot, order.Target, robot: true),
            TaskType.SplintOrder => world.SystemOf<FirstAidSystem>()?.SplintOrderDesire(world, slot, order.Target),

            // A loaded order its job remap turned into a job row (OrderSave) is scored as that row: desire x finder.
            TaskType.JobMedic or TaskType.JobRepairRobot or TaskType.SplintJob
                => world.SystemOf<FirstAidSystem>()?.JobEntryScore(world, slot, order.Row),
            TaskType.AttackEnemies => world.SystemOf<Aggro.AggroSystem>()?.AttackEntryScore(world, slot),
            TaskType.JobBuilder => world.SystemOf<Construction.ConstructionSystem>()?.EngineerEntryScore(world, slot),
            TaskType.LootAnimalsJob => world.SystemOf<Carrying.CarrySystem>()?.ForageEntryScore(world, slot),
            _ => 1,
        };
    }

    /// <summary>
    /// Following's desire of a follow order (AI slot 15): 0.5 x the followed character's <see cref="FollowMe"/>; 1 when it does not resolve.
    /// </summary>
    static float FollowDesire(World world, CharacterId target)
    {
        var table = world.Characters;
        if (!table.TryResolveNext(target, out int t) || table.Cold(t) is not { } followed) return 1;

        return FollowShare * FollowMe.Current(table, t, followed);
    }

    /// <summary>
    /// 225 / 246, and the arrest's 213 / 240 (AI slot 59, <c>FUN_14059e1a0</c>, <b>Verified</b>): 0 while the character carries someone
    /// (<c>+0x348</c>); 0.5 for a target that is no character; 0 for one a member of its own squad carries (the carrier hand's <c>+0xc</c> /
    /// <c>+0x10</c> against its own hand's: the container id and serial, as the hand maker <c>FUN_1400d1c20</c> writes them, and a character's
    /// container is its platoon, docs/formats/save.md "Object handles"); else 1, so a body another squad carries, of its own faction too, is
    /// lifted (snatched by 213 / 225 / 240).
    /// </summary>
    static float LiftDesire(World world, int slot, CharacterCold cold, CharacterId target)
    {
        if (!cold.Carrying.IsNone) return 0;
        if (!world.Characters.TryResolveNext(target, out int t)) return NoCharacter;

        return CarriedBySquad(world, slot, cold, world.Characters.Cold(t)!) ? 0 : 1;
    }

    /// <summary>
    /// The arrest's 213 / 240: <see cref="LiftDesire"/>, and 0 while the Fixes option holds the lift back (<see cref="Crime.ArrestSystem.LiftHeld"/>:
    /// a downed target an ally carries or with no cage free, Meitou fix), so the order leaves the queue as for a squad mate's carry.
    /// </summary>
    static float ArrestLiftDesire(World world, int slot, CharacterCold cold, OrderEntry order, Crime.ArrestSystem arrests)
    {
        float desire = LiftDesire(world, slot, cold, order.Target);

        return desire > 0 && arrests.LiftHeld(world, slot, order) ? 0 : desire;
    }

    /// <summary>
    /// Whether <paramref name="target"/> is carried by the character in <paramref name="slot"/> or a member of its squad.
    /// </summary>
    static bool CarriedBySquad(World world, int slot, CharacterCold cold, CharacterCold target)
    {
        if (!world.Characters.TryResolveNext(target.CarriedBy, out int carrier)) return false;
        if (carrier == slot) return true;

        return cold.SquadId >= 0 && world.Characters.Cold(carrier)?.SquadId == cold.SquadId;
    }

    /// <summary>
    /// 146 Turret gunner for a player character (AI slot 35, <c>FUN_140598080</c>): 0 while it carries someone, or is Passive (standing order 13,
    /// the <c>+0x450</c> object's <c>+0x12c</c>) and not in melee combat mode (<c>FUN_140436610</c>); else 1, as its orders hold a 146
    /// (<c>FUN_140507c10</c>).
    /// </summary>
    static float TurretDesire(World world, int slot, CharacterCold cold)
    {
        if (!cold.Carrying.IsNone) return 0;
        if ((cold.Orders & CharacterOrders.Passive) == 0) return 1;

        var combat = world.SystemOf<CombatSystem>();
        bool melee = combat is not null && combat.IsInCombatMode(world.Characters, world.Characters.IdOf(slot), cold, melee: true, ranged: false);
        return melee ? 1 : 0;
    }
}
