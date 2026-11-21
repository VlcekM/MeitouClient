using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Player;

namespace Meitou.Simulation.Combat;

/// <summary>
/// The owner of order 235 Shoot At (docs/game/combat.md "Ranged and turrets", <b>Verified</b>): the giver <c>FUN_1407f9e20</c> gives it to every selected
/// character, the menu clearing the queue and the quick click on a character (<c>FUN_1407fb410</c>) appending it. Row 235 is a <c>Task_UseTurret</c> on
/// the character's own turret (<c>FUN_1403474f0</c>: for a target that is no building it mans the turret of Character <c>+0x258</c>), its target handed
/// to the controller's target choice (<c>FUN_140437f40</c>). List A 7 isTargetDown makes it done; list B 145 <c>using any turret</c> needs a manned turret.
/// Stateless: one instance serves every world.
/// </summary>
internal sealed class ShootAt : IOrderOwner
{
    /// <summary>
    /// The one instance (<see cref="World.OrderOwnerOf"/>).
    /// </summary>
    public static ShootAt Instance { get; } = new();

    /// <summary>
    /// The order on <paramref name="target"/>.
    /// </summary>
    public static OrderEntry Order(CharacterId target) => new(TaskType.ShootAtTarget, Instance) { Target = target };

    /// <summary>
    /// The target of the gunner's running Shoot At (its turret task, which the order took over); None while it runs none.
    /// </summary>
    public static CharacterId TargetOf(CharacterCold gunner) =>
        gunner.Running is { Row: TaskType.ShootAtTarget, Source: TaskSource.Order, Task: CharacterTask.Operate } run ? run.Target : CharacterId.None;

    /// <summary>
    /// Gone for a target that is gone or down (list A 7); blocked for a character that mans no turret now (list B 145 fails, and its provider 146 has
    /// no turret to plan on a character target: Meitou's reading of the planner); else it runs.
    /// </summary>
    OrderCheck IOrderOwner.CanRun(World world, int slot, OrderEntry order)
    {
        var table = world.Characters;
        if (!table.TryResolveNext(order.Target, out int t) || t == slot || CharacterFacts.TargetDown(table.Cold(t))) return OrderCheck.Gone;

        var cold = table.Cold(slot)!;
        bool operating = table.Next[slot].Task == (byte)CharacterTask.Operate && cold.Running is { Task: CharacterTask.Operate };
        return operating && cold.Gunning is not null ? OrderCheck.Run : OrderCheck.Blocked();
    }

    /// <summary>
    /// The order's <c>Task_UseTurret</c> takes over the turret the gunner mans: its operate task goes on as row 235 under the order, so the turret's
    /// target choice reads the order's target (<see cref="TargetOf"/>) and the task ends with the order (the controller cleared, <c>FUN_140437470</c>).
    /// </summary>
    void IOrderOwner.Start(World world, int slot, OrderEntry order)
    {
        var cold = world.Characters.Cold(slot)!;
        if (cold.Running is not { } run) return;

        cold.Running = run with
        {
            Row = TaskType.ShootAtTarget,
            Source = TaskSource.Order,
            Job = -1,
            Target = order.Target,
            Tick = world.Tick,
            Implicit = false,
        };
    }
}
