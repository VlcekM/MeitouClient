namespace Meitou.Simulation.Factions;

/// <summary>
/// A character's faction changing (<see cref="IFactionChangeObserver"/>): the tick, the character, and its faction id before and after.
/// </summary>
public readonly record struct FactionChange(long Tick, CharacterId Character, int Before, int After);

/// <summary>
/// A system told whenever a character changes faction (docs/modding.md "Character observers"). Called serially in slot order once per tick after
/// the slow world, or as the character leaves the world. It may read the world and update its own hashed state, and must not mutate characters or
/// other world state. One that throws is disabled for that world, and the fault enters the state hash. Engine API, not in the original.
/// </summary>
public interface IFactionChangeObserver : ITickSystem
{
    /// <summary>
    /// A character changed faction.
    /// </summary>
    void FactionChanged(World world, in FactionChange change);
}

/// <summary>
/// A character joining or leaving the player's faction (<see cref="IRecruitObserver"/>): the tick, the character, the non-player faction id
/// (previous faction when recruited; new faction when dismissed), and whether it joined (true) or left (false).
/// </summary>
public readonly record struct RecruitChange(long Tick, CharacterId Character, int OtherFaction, bool Joined);

/// <summary>
/// A system told whenever a character joins or leaves the player's faction (docs/modding.md "Character observers"): dialogue recruitment, slave
/// joins, dismissal or mod changes. Called serially in slot order once per tick after the slow world, or as the character leaves the world. It may
/// read the world and update its own hashed state, and must not mutate characters or other world state. One that throws is disabled for that world,
/// and the fault enters the state hash. Engine API, not in the original.
/// </summary>
public interface IRecruitObserver : ITickSystem
{
    /// <summary>
    /// A character joined or left the player's faction.
    /// </summary>
    void RecruitChanged(World world, in RecruitChange change);
}
