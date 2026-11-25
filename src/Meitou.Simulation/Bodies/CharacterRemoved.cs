namespace Meitou.Simulation.Bodies;

/// <summary>
/// A character leaving the world (<see cref="ICharacterRemovedObserver"/>): the world's tick, the character (still in the table during the call),
/// its CHARACTER or ANIMAL_CHARACTER record's string id, its faction, its platoon (-1 for none), and whether it is dead.
/// </summary>
public readonly record struct CharacterRemoved(long Tick, CharacterId Character, string Record, int Faction, int Platoon, bool Dead);

/// <summary>
/// A system told whenever a character leaves the world's table (docs/modding.md "Character observers"): unloaded, despawned, decayed or removed by
/// a command. Called once per removal, serially and never during a parallel phase, in system order, right after any pending death or health notices
/// for that character. The character is still in the table during the call for reading; the observer must not mutate characters or other world
/// state (changes go through commands). One that throws is disabled for that world, and the fault enters the state hash. Engine API, not in the
/// original.
/// </summary>
public interface ICharacterRemovedObserver : ITickSystem
{
    /// <summary>
    /// A character is leaving the world.
    /// </summary>
    void Removed(World world, in CharacterRemoved removed);
}
