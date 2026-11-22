using Meitou.Simulation.Factions;

namespace Meitou.Simulation.Population;

/// <summary>
/// A rule's answer about two hostile stand-ins about to fight off screen (<see cref="IOffscreenFightRule"/>).
/// </summary>
public enum OffscreenFightVerdict : byte
{
    /// <summary>
    /// No say: they fight as the engine decides.
    /// </summary>
    Fight,

    /// <summary>
    /// They do not fight: the first looks for another enemy within reach, and the two may meet again at a later check.
    /// </summary>
    Skip,

    /// <summary>
    /// They fight and the first wins; the engine settles the losses as for a win it drew.
    /// </summary>
    FirstWins,

    /// <summary>
    /// They fight and the second wins, as <see cref="FirstWins"/>.
    /// </summary>
    SecondWins,
}

/// <summary>
/// A system asked about each meeting of hostile stand-ins before the off-screen fight settles it (docs/modding.md "Off-screen fight rules";
/// <see cref="PopulationSystem.OffscreenFights"/>, docs/simulation.md "Fights far away"): <paramref name="first"/> is the platoon looking for a
/// fight, <paramref name="second"/> the enemy it met. Called serially on the tick thread in system order, only while the switch is on and only for
/// pairs the engine would make fight: a stand-in's enemies within reach are asked about in id order until one is not skipped, and that one it fights.
/// Every rule is asked: one <see cref="OffscreenFightVerdict.Skip"/> skips the fight; otherwise the winner the rules name wins when they agree, and
/// the engine draws it when they name different ones. A rule reads the world and changes nothing in it (its own state only); one that throws, answers
/// no verdict, or adds or removes a platoon is disabled for that world, and the fault enters the state hash. Engine API, not in the original.
/// </summary>
public interface IOffscreenFightRule : ITickSystem
{
    /// <summary>
    /// The rule's answer about two stand-ins about to fight.
    /// </summary>
    OffscreenFightVerdict Judge(World world, Platoon first, Platoon second);
}
