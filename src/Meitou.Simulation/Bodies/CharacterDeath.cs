namespace Meitou.Simulation.Bodies;

/// <summary>
/// A character's death (<see cref="ICharacterDeathObserver"/>): the world's tick, the dead character (none for a member of an unloaded platoon,
/// which is no character in the world), its CHARACTER or ANIMAL_CHARACTER record's string id ("" for an unloaded platoon's member that was never
/// made), its faction, its platoon (-1 none), the medical cause (<see cref="DeathCause.None"/> when <see cref="OffScreen"/>), whether it lost an
/// off-screen fight, whether a blow killed it, and the character whose act killed it with that character's faction (none and -1 when the engine
/// names nobody). <see cref="Killer"/> is the attacker of a killing blow (melee, bolt, execution), the butcher of a body that died of a death
/// item taken, or the eater of a captive eaten alive; a death of bleeding, hunger, a wound worsening or the debug kill names none, whoever hit the
/// character before. An off-screen fight names no killer but the winning platoon's faction as <see cref="KillerFaction"/>.
/// </summary>
public readonly record struct CharacterDeath(long Tick, CharacterId Victim, string Record, int Faction, int Platoon, DeathCause Cause,
    bool OffScreen, bool Blow, CharacterId Killer, int KillerFaction);

/// <summary>
/// A system told of every death (docs/modding.md "Deaths"): a loaded character's, whatever killed it (a blow, bleeding out, hunger, a wound
/// worsening, a death item taken, being eaten, the rack, the debug kill, another system's kill), and each member of an unloaded platoon an
/// off-screen fight kills. Called once per death, serially on the tick thread, in system order: at once where a blow, a butchering, a meal, the
/// rack or an off-screen fight killed, otherwise in a sweep in slot order after the parallel phases (a death in them) or after the slow world
/// (a death in the commit or the slow world that reported none itself). The dead character is still in the world during the call. It may read
/// the world and change its own state, and must not change characters or other world state in the call (gameplay changes go through commands).
/// One that throws is disabled for that world, and the fault enters the state hash. Engine API, not in the original.
/// </summary>
public interface ICharacterDeathObserver : ITickSystem
{
    /// <summary>
    /// A character died.
    /// </summary>
    void Died(World world, in CharacterDeath death);
}
