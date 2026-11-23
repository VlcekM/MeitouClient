namespace Meitou.Simulation.Bodies;

/// <summary>
/// Where a death was reported from (<see cref="CharacterDeath.Site"/>): the step that killed, or <see cref="Other"/> for a death no step names.
/// </summary>
public enum DeathSite
{
    /// <summary>
    /// A death no step names (bleeding out, hunger, a wound worsening, the debug kill, another system's kill), found by the world's sweep or as the
    /// character left the world.
    /// </summary>
    Other,
    /// <summary>
    /// A melee blow.
    /// </summary>
    Melee,
    /// <summary>
    /// A bolt or harpoon.
    /// </summary>
    Bolt,
    /// <summary>
    /// An execution blow on a captive.
    /// </summary>
    Execution,
    /// <summary>
    /// One of its death items taken while it was down.
    /// </summary>
    Butchered,
    /// <summary>
    /// Eaten alive by a captor.
    /// </summary>
    Eaten,
    /// <summary>
    /// Stretched on a rack.
    /// </summary>
    Rack,
    /// <summary>
    /// Its unloaded platoon lost an off-screen fight, or it was the winner's loss.
    /// </summary>
    OffScreenFight,
}

/// <summary>
/// A character's death (<see cref="ICharacterDeathObserver"/>): the world's tick, the dead character (none for a member of an unloaded platoon,
/// which is no character in the world), its CHARACTER or ANIMAL_CHARACTER record's string id ("" for an unloaded platoon's member that was never
/// made), its faction (the platoon's off screen), its platoon (-1 none), the medical cause (<see cref="DeathCause.None"/> off screen), the step
/// that killed it, and the character whose act killed it with that character's faction (none and -1 when the engine names nobody).
/// <see cref="Killer"/> is the attacker of a killing blow (melee, bolt, execution), the butcher of a body that died of a death item taken, or the
/// eater of a captive eaten alive; it may have left the world since (a bolt's shooter), and its faction is then -1. A death of bleeding, hunger, a
/// wound worsening, the rack or the debug kill names none, whoever hit the character before. An off-screen fight names no killer but the winning
/// platoon's faction as <see cref="KillerFaction"/>.
/// </summary>
public readonly record struct CharacterDeath(long Tick, CharacterId Victim, string Record, int Faction, int Platoon, DeathCause Cause,
    DeathSite Site, CharacterId Killer, int KillerFaction)
{
    /// <summary>
    /// A blow killed it (<see cref="DeathSite.Melee"/>, <see cref="DeathSite.Bolt"/> or <see cref="DeathSite.Execution"/>).
    /// </summary>
    public bool Blow => Site is DeathSite.Melee or DeathSite.Bolt or DeathSite.Execution;
    /// <summary>
    /// It died in an off-screen fight (<see cref="DeathSite.OffScreenFight"/>).
    /// </summary>
    public bool OffScreen => Site == DeathSite.OffScreenFight;
}

/// <summary>
/// A system told of every death (docs/modding.md "Deaths"): a loaded character's, whatever killed it (a blow, bleeding out, hunger, a wound
/// worsening, a death item taken, being eaten, the rack, the debug kill, another system's kill), and each member of an unloaded platoon an
/// off-screen fight kills. Called once per death, serially and never during a parallel phase, in system order: at once where a blow, a
/// butchering, a meal, the rack or an off-screen fight killed; else in a sweep in slot order after the parallel phases (a death in them or in the
/// inputs) and after the slow world, or as the dead character leaves the world, whichever comes first. During a tick it runs on the thread running
/// the tick; a death the paused interface's commands make between ticks (a death item taken by hand) is told on the host's thread, with
/// <see cref="CharacterDeath.Tick"/> the coming tick. The dead character is still in the world's table during the call. It may read the world and
/// change its own hashed state, and must not change characters or other world state in the call (gameplay changes go through commands). One that
/// throws, or that removes the dead character itself, is disabled for that world, and the fault enters the state hash. Engine API, not in the
/// original.
/// </summary>
public interface ICharacterDeathObserver : ITickSystem
{
    /// <summary>
    /// A character died.
    /// </summary>
    void Died(World world, in CharacterDeath death);
}

/// <summary>
/// A code mod's own report of a death it caused (docs/modding.md "Deaths").
/// </summary>
public static class Deaths
{
    /// <summary>
    /// Reports the death of <paramref name="victim"/> to the death observers at once, naming <paramref name="killer"/> (none: nobody) and
    /// <paramref name="site"/>, when the character is in the world, dead and its death not reported yet; otherwise nothing (a death is told once,
    /// and the world's sweep tells an unreported one with no killer). Serial phases or between ticks; throws in a parallel phase.
    /// </summary>
    public static void Report(World world, CharacterId victim, CharacterId killer, DeathSite site)
    {
        if (world.Characters.Phase == TablePhase.Parallel) throw new InvalidOperationException("Deaths are reported in the serial phases only.");
        if (!world.Characters.TryResolveNext(victim, out int slot)) return;

        DeathObservers.Report(world, slot, killer, site);
    }
}
