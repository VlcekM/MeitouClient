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
        TaskType.MOVE_CUS_ORDERED or TaskType.STAY_CLOSE_TO_TARGET or TaskType.BODYGUARD => SystemOf<MovementSystem>(),
        TaskType.LIFT_PERSON_PLAYER_ORDER or TaskType.KIDNAP_ORDER or TaskType.LOOT_TARGET or TaskType.STEALTH_KNOCKOUT or TaskType.PUT_DOWN_OBJECT => SystemOf<Carrying.CarrySystem>(),
        TaskType.FOCUSED_MELEE_ATTACK or TaskType.UNPROVOKED_FOCUSED_MELEE_ATTACK => SystemOf<Combat.CombatSystem>(),
        TaskType.BUILD or TaskType.REPAIR or TaskType.DISMANTLE => SystemOf<Construction.ConstructionSystem>(),
        TaskType.OPERATE_MACHINERY or TaskType.OPERATE_AUTOMATIC_MACHINERY or TaskType.OPERATE_STORAGE or TaskType.USE_TRAINING_DUMMY or TaskType.USE_TURRET or TaskType.PROSPECTING => SystemOf<Production.ProductionSystem>(),
        TaskType.FIRST_AID_ORDER or TaskType.SPLINT_ORDER or TaskType.REST => SystemOf<FirstAid.FirstAidSystem>(),
        TaskType.PICKUP => SystemOf<PlayerSystem>(),
        TaskType.USE_BED or TaskType.PUT_SOMEONE_IN_BED => SystemOf<Locks.BedSystem>(),
        TaskType.PUT_IN_CAGE => SystemOf<Crime.ArrestSystem>(),
        _ => Locks.LockSystem.OwnsRow(row) ? SystemOf<Locks.LockSystem>() : null,
    };
}
