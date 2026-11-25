namespace Meitou.Simulation.Bodies;

/// <summary>
/// A character's consciousness state changing (<see cref="IKnockoutObserver"/>): the tick, the character, whether it is now unconscious (true:
/// knocked out; false: woke up), the KO timer in game minutes, and whether it is in a recovery coma.
/// </summary>
public readonly record struct KnockoutChange(long Tick, CharacterId Character, bool Unconscious, float KoTimer, bool Coma);

/// <summary>
/// A system told whenever a character in the world is knocked out or wakes up (docs/modding.md "Character observers"). Called serially in slot
/// order once per tick after the slow world, or as the character leaves the world. It may read the world and update its own hashed state, and must
/// not mutate characters or other world state (changes go through commands). One that throws is disabled for that world, and the fault enters the
/// state hash. Engine API, not in the original.
/// </summary>
public interface IKnockoutObserver : ITickSystem
{
    /// <summary>
    /// A character was knocked out or woke up.
    /// </summary>
    void KnockoutChanged(World world, in KnockoutChange change);
}
