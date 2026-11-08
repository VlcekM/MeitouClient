using System.Numerics;
using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Player;

namespace Meitou.Simulation;

public sealed partial class World
{
    /// <summary>
    /// Whether the order <paramref name="order"/> could start for <paramref name="actor"/> now, and the text its failure shows: <see cref="PlayerOrders.CanRun"/> (the row's repertoire, its owner's
    /// check, the <c>!158</c> threat rule), the same read the queue starts and barks through. A pure read, asked between ticks; false without a reason when the actor or the order's target or site
    /// is gone or no longer fits the verb (the order would leave the queue). Not a menu filter: the original offers most verbs whatever this says and lets the order fail with its text (docs/simulation.md "Order queue").
    /// </summary>
    public (bool Can, string? Why) CanOrder(OrderEntry order, CharacterId actor)
    {
        if (Characters.Phase != TablePhase.Outside || !Characters.TryResolveNext(actor, out int slot) || Characters.Cold(slot) is null) return (false, null);
        var check = PlayerOrders.CanRun(this, slot, order);
        return (check.CanRun, check.Why);
    }

    /// <summary>
    /// <see cref="CanOrder(OrderEntry, CharacterId)"/> for an order row given by its parts (a target character, a site or fixture id, a point) as the menu's verbs give them; false without a reason for a row no
    /// system owns as an order. The variants an owner tells apart by its detail (loot against open inventory, a lock order in place or on a walk) are read as their common case.
    /// </summary>
    public (bool Can, string? Why) CanOrder(TaskType row, CharacterId actor, CharacterId target = default, string? site = null, Vector3 point = default)
    {
        var entry = OrderOwnerOf(row) is { } owner ? new OrderEntry(row, owner) { Target = target, Site = site, Point = point, Detail = owner is Locks.LockSystem locks ? locks.WorkDetail() : null } : null;
        return entry is null ? (false, null) : CanOrder(entry, actor);
    }

    /// <summary>The system whose <see cref="IOrderOwner"/> checks and starts the order row (the one each owner gives its orders with), or null when none does.</summary>
    internal IOrderOwner? OrderOwnerOf(TaskType row) => row switch
    {
        TaskType.MoveCusOrdered or TaskType.StayCloseToTarget or TaskType.Bodyguard => SystemOf<MovementSystem>(),
        TaskType.LiftPersonPlayerOrder or TaskType.KidnapOrder or TaskType.LootTarget or TaskType.StealthKnockout or TaskType.PutDownObject => SystemOf<Carrying.CarrySystem>(),
        TaskType.FocusedMeleeAttack or TaskType.UnprovokedFocusedMeleeAttack => SystemOf<Combat.CombatSystem>(),
        TaskType.Build or TaskType.Repair or TaskType.Dismantle => SystemOf<Construction.ConstructionSystem>(),
        TaskType.OperateMachinery or TaskType.OperateAutomaticMachinery or TaskType.OperateStorage or TaskType.UseTrainingDummy or TaskType.UseTurret or TaskType.Prospecting => SystemOf<Production.ProductionSystem>(),
        TaskType.FirstAidOrder or TaskType.SplintOrder or TaskType.Rest => SystemOf<FirstAid.FirstAidSystem>(),
        TaskType.Pickup => SystemOf<PlayerSystem>(),
        TaskType.UseBed or TaskType.PutSomeoneInBed => SystemOf<Locks.BedSystem>(),
        TaskType.PutInCage => SystemOf<Crime.ArrestSystem>(),
        _ => Locks.LockSystem.OwnsRow(row) ? SystemOf<Locks.LockSystem>() : null,
    };
}