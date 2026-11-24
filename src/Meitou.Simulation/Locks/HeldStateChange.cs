namespace Meitou.Simulation.Locks;

/// <summary>
/// A character entering or leaving a bed, cage or restraint (<see cref="IHeldStateObserver"/>): the tick, the character, its
/// <see cref="HeldState"/> before and after, and the fixture id (null when none or unknown).
/// </summary>
public readonly record struct HeldStateChange(long Tick, CharacterId Character, HeldState Before, HeldState After, string? Fixture);

/// <summary>
/// A system told whenever a character enters or leaves a bed, cage or restraint (docs/modding.md "Character observers"). Called serially in slot
/// order once per tick after the slow world, or as the character leaves the world. It may read the world and update its own hashed state, and must
/// not mutate characters or other world state. One that throws is disabled for that world, and the fault enters the state hash. Engine API, not in
/// the original.
/// </summary>
public interface IHeldStateObserver : ITickSystem
{
    /// <summary>
    /// A character's held state changed.
    /// </summary>
    void HeldStateChanged(World world, in HeldStateChange change);
}
