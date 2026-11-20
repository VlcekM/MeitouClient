namespace Meitou.Simulation.Locks;

/// <summary>
/// What holds a walker on its way through a locked door (<see cref="LockSystem.StartDoorWay"/>; docs/game/locks.md "Locked doors on the way"): the
/// original's planner puts the door rows in front of the walk as steps of the plan the walk belongs to, so the way lasts only while that plan runs.
/// </summary>
internal enum DoorWayHolder : byte
{
    /// <summary>
    /// The walk of whatever task ran it (an order's, a source's, an engine system's or a mod's): held while that task's provenance is the one the way
    /// began on (<see cref="CharacterCold.Running"/>).
    /// </summary>
    Walk,
    /// <summary>
    /// The NPC's kept plan, the way being its front step (<see cref="Ai.AiSystem.TakeDoorStep"/>): held while that step runs.
    /// </summary>
    Plan,
    /// <summary>
    /// The engine goal whose act-out walks (<see cref="Ai.AiMind.Duty"/>): held while the goal holds the character.
    /// </summary>
    Goal,
}
