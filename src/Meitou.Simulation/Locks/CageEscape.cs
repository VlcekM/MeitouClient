using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Crime;
using Meitou.Simulation.Player;

namespace Meitou.Simulation.Locks;

/// <summary>
/// The owner of order 207 Escape cage, the player's Escape (docs/game/ai-tasks.md "Order givers", <b>Verified</b>): the caged character's menu verb
/// and the Alt quick click on a cage holding one of the player's (<c>FUN_1407fc6e0</c>) give it through <c>FUN_1407fa650</c> → <c>FUN_1407f9e20</c>
/// to every selected character, its queue cleared. Its act-out is <c>Task_GetOutOfCage</c>'s leave with the escape flag
/// (<see cref="FixtureRegistry.GetOut"/>), which ends at once. Row 207's list A <c>!79</c> makes it done for a character in no cage, and its list B
/// <c>!54</c> holds it while the cage is locked. Stateless: one instance serves every world.
/// </summary>
internal sealed class CageEscape : IOrderOwner
{
    /// <summary>
    /// The one instance (<see cref="World.OrderOwnerOf"/>).
    /// </summary>
    public static CageEscape Instance { get; } = new();

    /// <summary>
    /// The order on the character in <paramref name="slot"/> itself.
    /// </summary>
    public static OrderEntry Order(World world, int slot) => new(TaskType.GetOutOfCageEscape, Instance) { Target = world.Characters.IdOf(slot) };

    /// <summary>
    /// Gone for a character in no cage (list A <c>!79</c> holds); blocked while its cage is locked (list B <c>!54</c>); else it runs.
    /// </summary>
    OrderCheck IOrderOwner.CanRun(World world, int slot, OrderEntry order)
    {
        var cold = world.Characters.Cold(slot)!;
        if (cold is not { InSomething: HeldState.Imprisoned, InWhat: { } cage }) return OrderCheck.Gone;
        if (world.Fixtures.Find(cage)?.Lock is { EffectivelyLocked: true }) return OrderCheck.Blocked();

        return OrderCheck.Run;
    }

    /// <summary>
    /// The leave: out of the cage at its exit spot with the escape flag, a pick of its lock dropped; the task the character was on ends, as the
    /// order's new task replaces it and ends at once. Nothing runs after it, so the order leaves the queue.
    /// </summary>
    void IOrderOwner.Start(World world, int slot, OrderEntry order)
    {
        if (world.Fixtures.GetOut(world, world.Characters.IdOf(slot), world.SystemOf<CrimeSystem>())) world.Characters.Cold(slot)!.PickingLock = null;
        TaskStart.End(world, slot, TaskEnd.Stopped);
    }
}
