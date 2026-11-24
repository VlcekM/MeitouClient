using System.Numerics;
using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Player;

namespace Meitou.Simulation;

/// <summary>
/// The world's orders: whether an order could start, giving one (<see cref="GiveOrder"/>), and the system that owns its row.
/// </summary>
public sealed partial class World
{
    /// <summary>
    /// Whether the order <paramref name="order"/> could start for <paramref name="actor"/> now, and the text its failure shows:
    /// <see cref="PlayerOrders.CanRun"/> (the row's repertoire, its owner's check, the <c>!158</c> threat rule), the same read the queue
    /// starts and barks through. A pure read, asked between ticks; false without a reason when the actor or the order's target or site
    /// is gone or no longer fits the verb (the order would leave the queue). Not a menu filter: the original offers most verbs whatever
    /// this says and lets the order fail with its text (docs/simulation.md "Order queue").
    /// </summary>
    public (bool Can, string? Why) CanOrder(OrderEntry order, CharacterId actor)
    {
        if (Characters.Phase != TablePhase.Outside || !Characters.TryResolveNext(actor, out int slot) || Characters.Cold(slot) is null)
            return (false, null);

        var check = PlayerOrders.CanRun(this, slot, order);
        return (check.CanRun, check.Why);
    }

    /// <summary>
    /// <see cref="CanOrder(OrderEntry, CharacterId)"/> for an order row given by its parts (a target character, a site or fixture id,
    /// a point) as the menu's verbs give them; false without a reason for a row no system owns as an order. The variants an owner tells apart by its
    /// detail (loot against open inventory, a lock order in place or on a walk) are read as their common case.
    /// </summary>
    public (bool Can, string? Why) CanOrder(TaskType row, CharacterId actor, CharacterId target = default, string? site = null, Vector3 point = default)
    {
        var entry = EntryOf(row, actor, target, site, point);
        return entry is null ? (false, null) : CanOrder(entry, actor);
    }

    /// <summary>
    /// Gives <paramref name="actor"/> (a player character or an NPC) the order <paramref name="row"/> with its parts as the row's <c>CanOrder</c>
    /// takes them (no <paramref name="target"/>: none), through the one order queue the player's orders use (<see cref="PlayerOrders.Give"/>): without
    /// <paramref name="queued"/> it replaces the queue. A move (<see cref="TaskType.MoveCusOrdered"/>) is the player's move command for the one
    /// character (<see cref="MovementSystem.GiveMove(World, int, Vector3, bool)"/>); any other row gets the detail a saved order of that row is loaded
    /// with and its owner's readying (<see cref="IOrderOwner.LoadDetail"/>, <see cref="IOrderOwner.Restored"/>). Serial phases only (between ticks, or
    /// a system's serial step). Returns the check of the order's start when it is alone in the queue and the character takes orders now, else
    /// <see cref="OrderCheck.Run"/> (it waits queued); <see cref="OrderCheck.Gone"/> for an actor not in the world or a row no system owns as an
    /// order (nothing is queued then).
    /// </summary>
    public OrderCheck GiveOrder(CharacterId actor, TaskType row, CharacterId? target = null, string? site = null, Vector3 point = default,
        bool queued = false)
    {
        if (Characters.Phase == TablePhase.Parallel) throw new InvalidOperationException("Orders are given in the serial phases only.");
        if (Characters.Phase != TablePhase.Outside) return Give(actor, row, target, site, point, queued);

        // Between ticks the order's start writes the state the next tick computes from, as the paused step's commands do.
        Characters.BeginTick();
        Characters.Phase = TablePhase.Inputs;
        try
        {
            return Give(actor, row, target, site, point, queued);
        }
        finally
        {
            Characters.Phase = TablePhase.Outside;
            Characters.EndTick();
        }
    }

    /// <summary>
    /// <see cref="GiveOrder"/> in a serial phase.
    /// </summary>
    OrderCheck Give(CharacterId actor, TaskType row, CharacterId? target, string? site, Vector3 point, bool queued)
    {
        if (!Characters.TryResolveNext(actor, out int slot) || Characters.Cold(slot) is null) return OrderCheck.Gone;
        if (row == TaskType.MoveCusOrdered && SystemOf<MovementSystem>() is { } movement) return movement.GiveMove(this, slot, point, queued);
        if (EntryOf(row, actor, target ?? CharacterId.None, site, point) is not { } entry) return OrderCheck.Gone;

        entry.Owner.Restored(this, entry);
        return PlayerOrders.Give(this, slot, entry, queued);
    }

    /// <summary>
    /// The order entry of <paramref name="row"/> for <paramref name="actor"/> with its parts, owned by the row's order owner
    /// (<see cref="OrderOwnerOf"/>), with the detail its owner gives a saved order of that row (<see cref="IOrderOwner.LoadDetail"/>, its plain
    /// kind: a lock order's walk with its row's work, the player's pick of an attack); null for a row no system owns as an order.
    /// </summary>
    OrderEntry? EntryOf(TaskType row, CharacterId actor, CharacterId target, string? site, Vector3 point)
    {
        if (OrderOwnerOf(row, site is not null) is not { } owner) return null;

        var detail = owner.LoadDetail(this, actor, row, 0, target);
        return new OrderEntry(row, owner) { Target = target, Site = site, Point = point, Detail = detail };
    }

    /// <summary>
    /// The system whose <see cref="IOrderOwner"/> checks and starts the order row (the one each owner gives its orders with), or null when none does;
    /// <paramref name="onSite"/>: the order names a site (26 Loot on a container is the pick-ups', on a character the carry system's).
    /// </summary>
    internal IOrderOwner? OrderOwnerOf(TaskType row, bool onSite = false) => row switch
    {
        TaskType.LootTarget when onSite => SystemOf<PlayerSystem>()?.PickUps,
        TaskType.MoveCusOrdered or TaskType.StayCloseToTarget or TaskType.Bodyguard => SystemOf<MovementSystem>(),
        TaskType.LiftPersonPlayerOrder or TaskType.KidnapOrder or TaskType.LootTarget or TaskType.StealthKnockout or TaskType.PutDownObject
            or TaskType.LootAnimalsJob or TaskType.FindWeapon => SystemOf<Carrying.CarrySystem>(),
        TaskType.FocusedMeleeAttack or TaskType.UnprovokedFocusedMeleeAttack or TaskType.MeleeAttack or TaskType.AttackEnemies
            => SystemOf<Combat.CombatSystem>(),
        TaskType.ShootAtTarget => Combat.ShootAt.Instance,
        TaskType.FeedCorpseIntoMachine => SystemOf<Production.ProductionSystem>()?.CorpseOrders,
        TaskType.Build or TaskType.Repair or TaskType.Dismantle or TaskType.JobBuilder => SystemOf<Construction.ConstructionSystem>(),
        TaskType.OperateMachinery or TaskType.OperateAutomaticMachinery or TaskType.OperateStorage or TaskType.UseTrainingDummy
            or TaskType.UseTurret or TaskType.Prospecting => SystemOf<Production.ProductionSystem>(),
        TaskType.FirstAidOrder or TaskType.SplintOrder or TaskType.Rest or TaskType.JobMedic or TaskType.JobRepairRobot or TaskType.SplintJob
            => SystemOf<FirstAid.FirstAidSystem>(),
        TaskType.Pickup => SystemOf<PlayerSystem>()?.PickUps,
        TaskType.UseBed or TaskType.PutSomeoneInBed => SystemOf<Locks.BedSystem>(),
        TaskType.PutInCage or TaskType.LiftPersonSnatchingAllowed or TaskType.LiftPersonSnatchingAllowedInTownOnly or TaskType.FindCageAndPutIn
            => SystemOf<Crime.ArrestSystem>(),
        TaskType.GetOutOfCageLegit => Locks.FreedPrisoner.Instance,
        TaskType.GetOutOfCageEscape => Locks.CageEscape.Instance,
        TaskType.RunAway => SystemOf<Aggro.FleeSystem>(),
        TaskType.EatTargetAlive => SystemOf<Kidnap.CaptiveSystem>(),
        TaskType.GetUp or TaskType.Aoeu or TaskType.StumbleTaskForced or TaskType.GetOutOfBed => SystemOf<Combat.CombatSystem>()?.BodyOrders,
        TaskType.PlayerTalkTo or TaskType.SeekAndTalkAndSendSignal or TaskType.FollowWhileTalking or TaskType.TownStalker
            => SystemOf<Dialogue.DialogueSystem>()?.TalkOrders,
        TaskType.GoHomebuilding or TaskType.Patrol or TaskType.MoveOnFreeWillFast or TaskType.RunAwayForced or TaskType.DefeatSquad
            or TaskType.DefeatSquadLimitChaseRange => SystemOf<Ai.AiSystem>()?.NpcOrders,
        _ => Locks.LockSystem.OwnsRow(row) ? SystemOf<Locks.LockSystem>() : null,
    };

    /// <summary>
    /// An order's task is about to start on the NPC in <paramref name="slot"/> (serial): every system holding it on an engine duty lets it go
    /// (<see cref="IDutyRelease.ReleaseForOrder"/>), in system order.
    /// </summary>
    internal void ReleaseDuties(int slot)
    {
        foreach (var system in _systems)
        {
            if (system is IDutyRelease holder) holder.ReleaseForOrder(this, slot);
        }
    }
}