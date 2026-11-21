using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Crime;
using Meitou.Simulation.Player;

namespace Meitou.Simulation.Locks;

/// <summary>
/// The owner of order 133 Get out of cage (legit), the order a freed prisoner gets with its queue cleared (docs/game/ai-tasks.md "Order givers",
/// <b>Verified</b>): a bailed prisoner (<c>FUN_1406ae170</c>), a slave bought free (<c>FUN_1406add20</c>) and a prisoner a jailer of a faction that
/// books bounties or of the player's faction releases (<c>FUN_1403570a0</c>). Its act-out is <c>Task_GetOutOfCage</c>'s leave
/// (<see cref="FixtureRegistry.GetOutLegit"/>), which ends at once: out of the cage of a bailed prisoner (its lock left as it was), nothing for one whose hold the release or the
/// purchase already cleared. Stateless: one instance serves every world.
/// </summary>
internal sealed class FreedPrisoner : IOrderOwner
{
    /// <summary>
    /// The one instance (<see cref="World.OrderOwnerOf"/>).
    /// </summary>
    public static FreedPrisoner Instance { get; } = new();

    /// <summary>
    /// Gives the character in <paramref name="slot"/> order 133, clearing its queue (serial phases; never from inside a duty holder's own loop over
    /// that character).
    /// </summary>
    public static void Give(World world, int slot) =>
        PlayerOrders.Give(world, slot, new OrderEntry(TaskType.GetOutOfCageLegit, Instance), queued: false);

    /// <summary>
    /// The order has nothing to wait for: it runs whenever the character's chooser does.
    /// </summary>
    OrderCheck IOrderOwner.CanRun(World world, int slot, OrderEntry order) => OrderCheck.Run;

    /// <summary>
    /// The leave: out of the cage at its exit spot when still in one; the task the character was on ends either way, as the order's new task replaces
    /// it and ends at once. Nothing runs after it, so the order leaves the queue.
    /// </summary>
    void IOrderOwner.Start(World world, int slot, OrderEntry order)
    {
        world.Fixtures.GetOutLegit(world, world.Characters.IdOf(slot), world.SystemOf<CrimeSystem>());
        TaskStart.End(world, slot, TaskEnd.Stopped);
    }
}
