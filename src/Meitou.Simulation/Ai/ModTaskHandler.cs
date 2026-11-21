using Meitou.Simulation.Movement;

namespace Meitou.Simulation.Ai;

/// <summary>
/// A mod system's task handler (<see cref="TaskHandlers.RegisterMod"/>), its calls caught: a throw stops the system as a throw of one of its phases
/// does (<see cref="ModSystemFaults.Fault"/>: logged once with the mod's id, the player's notice, hashed), and the task starts and ends without the
/// handler from then on, so an engine start or end that runs into it goes on. Once the system is stopped the handler is called no more.
/// </summary>
internal sealed class ModTaskHandler(ITaskHandler handler, ModSystemFaults faults, int system) : ITaskHandler
{
    /// <summary>
    /// The mod's start, unless its system is stopped.
    /// </summary>
    public void Start(World world, int slot, in TaskRequest request)
    {
        if (faults.IsStopped(system)) return;

        try
        {
            handler.Start(world, slot, request);
        }
        catch (Exception e)
        {
            faults.Fault(system, "task handler's start", e);
        }
    }

    /// <summary>
    /// The mod's end, unless its system is stopped.
    /// </summary>
    public void End(World world, int slot, CharacterTask task, TaskEnd reason)
    {
        if (faults.IsStopped(system)) return;

        try
        {
            handler.End(world, slot, task, reason);
        }
        catch (Exception e)
        {
            faults.Fault(system, "task handler's end", e);
        }
    }

    /// <summary>
    /// The mod's answer; once its system is stopped, or for a throw, done when the character is off the task (the default).
    /// </summary>
    public bool Done(World world, int slot, CharacterTask task)
    {
        if (faults.IsStopped(system)) return Off(world, slot, task);

        try
        {
            return handler.Done(world, slot, task);
        }
        catch (Exception e)
        {
            faults.Fault(system, "task handler's done", e);
            return Off(world, slot, task);
        }
    }

    /// <summary>
    /// Whether the character is off <paramref name="task"/>.
    /// </summary>
    static bool Off(World world, int slot, CharacterTask task) => world.Characters.Next[slot].Task != (byte)task;
}
