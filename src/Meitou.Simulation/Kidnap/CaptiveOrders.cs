using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Locks;
using Meitou.Simulation.Player;

namespace Meitou.Simulation.Kidnap;

/// <summary>
/// The captive jobs given as an order (docs/modding.md "Ordering characters"): 220 Eat target alive on a caged or downed character, the job the
/// cannibal goals act out (<see cref="CaptiveJobKind.Eat"/> at a cage, <see cref="CaptiveJobKind.EatBody"/> on the ground), for any character the
/// player's order queue holds, a player character included. The job runs while the order is the queue's front; another order ends it.
/// </summary>
internal sealed class CaptiveOrders(CaptiveSystem captives) : IOrderOwner, IOrderSteps
{
    /// <summary>
    /// 220's check: gone when the target is no longer one to eat (alive in a cage with nobody else's job on it, or down or dead on the ground,
    /// uncarried and in nothing, with a body); waits while the eater is down, carried, caged or carrying.
    /// </summary>
    OrderCheck IOrderOwner.CanRun(World world, int slot, OrderEntry order)
    {
        var table = world.Characters;
        var cold = table.Cold(slot)!;
        if (!table.TryResolveNext(order.Target, out int c) || c == slot || MealOf(world, table.IdOf(slot), c) is null)
            return OrderCheck.Gone;

        bool able = cold is { Medical.Incapacitated: false, IsImprisoned: false } && cold.CarriedBy.IsNone && cold.Carrying.IsNone;
        return able ? OrderCheck.Run : OrderCheck.Blocked();
    }

    /// <summary>
    /// Starts 220's job: the meal at the target's cage, or on the body where it lies; the eater sheathes (220's <c>!hasWeaponEquipped</c>). An
    /// order's job resets no town's kill timer; it starts eating within the order's <see cref="OrderEntry.Reach"/> when it has one.
    /// </summary>
    void IOrderOwner.Start(World world, int slot, OrderEntry order)
    {
        var table = world.Characters;
        var worker = table.IdOf(slot);
        if (!table.TryResolveNext(order.Target, out int c) || MealOf(world, worker, c) is not { } kind) return;

        var cold = table.Cold(slot)!;
        var cc = table.Cold(c)!;
        cold.DrawnWeapon = WeaponKinds.None;
        captives.Begin(world, new CaptiveJob
        {
            Kind = kind, Worker = worker, Captive = order.Target, Cage = kind == CaptiveJobKind.Eat ? cc.InWhat! : "", Faction = cold.Faction,
            Goal = TaskType.EatTargetAlive, Target = PlanTarget.OfCharacter(order.Target, table.Next[c].Position), Ordered = true,
            Reach = order.Reach,
        });
    }

    /// <summary>
    /// The order goes on while its job does, between the job's walk and its work.
    /// </summary>
    bool IOrderSteps.Between(World world, int slot, OrderEntry order) => OrderedJobOf(world.Characters.IdOf(slot)) is not null;

    /// <summary>
    /// The meal <paramref name="worker"/> would make of the character in slot <paramref name="c"/>: <see cref="CaptiveJobKind.Eat"/> for a live
    /// caged occupant, <see cref="CaptiveJobKind.EatBody"/> for one down or dead on the ground; null when neither, or another worker's job holds it
    /// or its cage.
    /// </summary>
    CaptiveJobKind? MealOf(World world, CharacterId worker, int c)
    {
        var table = world.Characters;
        if (table.Cold(c) is not { Medical: { } m, Race: not null } cc || !cc.CarriedBy.IsNone || OthersJob(worker, table.IdOf(c), cc.InWhat))
            return null;

        if (cc is { IsImprisoned: true, InWhat: { } cageId } && !m.Dead)
            return world.Fixtures.Find(cageId) is { } cage && CaptiveSystem.OccupantSlot(world, cage) == c ? CaptiveJobKind.Eat : null;

        return cc.InSomething == HeldState.None && m.Incapacitated ? CaptiveJobKind.EatBody : null;
    }

    /// <summary>
    /// Whether a job of a worker other than <paramref name="worker"/> holds <paramref name="captive"/> or works its cage.
    /// </summary>
    bool OthersJob(CharacterId worker, CharacterId captive, string? cage)
    {
        foreach (var j in captives.Jobs)
        {
            if (j.Worker != worker && (j.Captive == captive || j.Worker == captive || cage is not null && j.Cage == cage))
                return true;
        }

        return false;
    }

    /// <summary>
    /// The order's job of <paramref name="worker"/> (null: none).
    /// </summary>
    CaptiveJob? OrderedJobOf(CharacterId worker)
    {
        foreach (var j in captives.Jobs)
            if (j.Ordered && j.Worker == worker) return j;
        return null;
    }

    /// <summary>
    /// Whether an order's job still has its order: the worker's queue front is a started 220 of this system on the job's captive.
    /// </summary>
    public bool Holds(World world, int w, CaptiveJob j) =>
        world.Characters.Cold(w)?.OrderQueue.Front is { Started: true, Row: TaskType.EatTargetAlive } front
        && ReferenceEquals(front.Owner, this) && front.Target == j.Captive;
}
