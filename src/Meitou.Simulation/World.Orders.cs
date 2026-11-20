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
        var entry = EntryOf(row, target, site, point);
        return entry is null ? (false, null) : CanOrder(entry, actor);
    }

    /// <summary>
    /// Gives <paramref name="actor"/> (a player character or an NPC) the order <paramref name="row"/> with its parts as the row's <c>CanOrder</c>
    /// takes them (no <paramref name="target"/>: none), through the one order queue the player's orders use (<see cref="PlayerOrders.Give"/>): without
    /// <paramref name="queued"/> it replaces the queue. Serial phases only (between ticks, or a system's serial step). Returns the check of the queue's
    /// front; <see cref="OrderCheck.Gone"/> for an actor not in the world or a row no system owns as an order (nothing is queued then).
    /// </summary>
    public OrderCheck GiveOrder(CharacterId actor, TaskType row, CharacterId? target = null, string? site = null, Vector3 point = default,
        bool queued = false)
    {
        if (Characters.Phase == TablePhase.Parallel) throw new InvalidOperationException("Orders are given in the serial phases only.");
        if (!Characters.TryResolveNext(actor, out int slot) || Characters.Cold(slot) is null) return OrderCheck.Gone;
        if (EntryOf(row, target ?? CharacterId.None, site, point) is not { } entry) return OrderCheck.Gone;

        return PlayerOrders.Give(this, slot, entry, queued);
    }

    /// <summary>
    /// The order entry of <paramref name="row"/> with its parts, owned by the row's order owner (<see cref="OrderOwnerOf"/>); null for a row no system
    /// owns as an order. A lock order's detail is its common case (<see cref="Locks.LockSystem.WorkDetail"/>).
    /// </summary>
    OrderEntry? EntryOf(TaskType row, CharacterId target, string? site, Vector3 point)
    {
        if (OrderOwnerOf(row, site is not null) is not { } owner) return null;

        return new OrderEntry(row, owner)
        {
            Target = target, Site = site, Point = point, Detail = owner is Locks.LockSystem locks ? locks.WorkDetail() : null,
        };
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
            or TaskType.LootAnimalsJob => SystemOf<Carrying.CarrySystem>(),
        TaskType.FocusedMeleeAttack or TaskType.UnprovokedFocusedMeleeAttack or TaskType.MeleeAttack => SystemOf<Combat.CombatSystem>(),
        TaskType.Build or TaskType.Repair or TaskType.Dismantle => SystemOf<Construction.ConstructionSystem>(),
        TaskType.OperateMachinery or TaskType.OperateAutomaticMachinery or TaskType.OperateStorage or TaskType.UseTrainingDummy
            or TaskType.UseTurret or TaskType.Prospecting => SystemOf<Production.ProductionSystem>(),
        TaskType.FirstAidOrder or TaskType.SplintOrder or TaskType.Rest or TaskType.JobMedic => SystemOf<FirstAid.FirstAidSystem>(),
        TaskType.Pickup => SystemOf<PlayerSystem>()?.PickUps,
        TaskType.UseBed or TaskType.PutSomeoneInBed => SystemOf<Locks.BedSystem>(),
        TaskType.PutInCage or TaskType.LiftPersonSnatchingAllowed or TaskType.LiftPersonSnatchingAllowedInTownOnly or TaskType.FindCageAndPutIn
            => SystemOf<Crime.ArrestSystem>(),
        TaskType.GetOutOfCageLegit => Locks.FreedPrisoner.Instance,
        TaskType.RunAway => SystemOf<Aggro.FleeSystem>(),
        TaskType.GetUp or TaskType.Aoeu or TaskType.StumbleTaskForced or TaskType.GetOutOfBed => SystemOf<Combat.CombatSystem>()?.BodyOrders,
        TaskType.SeekAndTalkAndSendSignal or TaskType.FollowWhileTalking or TaskType.TownStalker => SystemOf<Dialogue.DialogueSystem>()?.TalkOrders,
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