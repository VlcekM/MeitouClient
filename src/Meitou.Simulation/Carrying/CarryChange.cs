namespace Meitou.Simulation.Carrying;

/// <summary>
/// A character picking up or putting down another character (<see cref="ICarryObserver"/>): the tick, the carrier, the carried character, and
/// whether it was picked up (true) or put down (false).
/// </summary>
public readonly record struct CarryChange(long Tick, CharacterId Carrier, CharacterId Carried, bool PickedUp);

/// <summary>
/// A system told whenever a character picks up or puts down another character (docs/modding.md "Character observers"). Called serially in slot
/// order once per tick after the slow world, or as the carrier leaves the world. It may read the world and update its own hashed state, and must
/// not mutate characters or other world state. One that throws is disabled for that world, and the fault enters the state hash. Engine API, not in
/// the original.
/// </summary>
public interface ICarryObserver : ITickSystem
{
    /// <summary>
    /// A character picked up or put down another character.
    /// </summary>
    void CarryChanged(World world, in CarryChange change);
}
