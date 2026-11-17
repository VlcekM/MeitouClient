using System.Numerics;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Saving;

namespace Meitou.Simulation.Population;

/// <summary>
/// Where an unloaded hired squad's stand-in heads: <see cref="At"/> (null: it stands still), stopping within <see cref="Reach"/> of it, at the
/// speed of <see cref="Job"/>.
/// </summary>
readonly record struct ContractGoal(Vector2? At, float Reach, PlatoonJob Job);

/// <summary>
/// A platoon's contract (dialogue's <c>AI contract</c>: hired bodyguards, a base defence) as the population sees it, loaded or not
/// (docs/game/factions-squads-towns.md "Off-screen world").
/// </summary>
static class PlatoonContracts
{
    /// <summary>
    /// The signals of <c>Contract_HiredAlly</c>, the bodyguard contract, and <c>Contract_DefendPlayerBase</c>, the base defence (docs/game/ai.md
    /// "Package classes").
    /// </summary>
    const int HiredAllySignal = 12, DefendBaseSignal = 15;

    /// <summary>
    /// The AI_PACKAGE id of the contract the platoon's squad runs, null for none: the loaded squad's (its blackboard's, else the AI it has still to
    /// take), else the one kept with its characters off screen (<see cref="OffscreenAi"/>). Any thread.
    /// </summary>
    public static string? ContractOf(World world, Platoon p) =>
        p.SquadId >= 0 && world.Squads.Find(p.SquadId) is { } squad
            ? squad.Ai is { } board ? board.Contract?.Id : squad.SavedAi?.Contract
            : OffscreenAi(p)?.Contract;

    /// <summary>
    /// Where an unloaded hired squad's stand-in heads and at which job (<c>FUN_1407ee790</c>, <b>Verified</b>): the job is the blackboard's current
    /// package's <c>unloaded func</c>, the contract's while one runs (<c>FUN_1402721a0</c> makes it current), and for TRAVEL_TARGET / _FAST its target
    /// is the contract's (<c>FUN_140269b60</c> -> slot 1 <c>FUN_1402859e0</c>: the package's <c>mission target</c> hand), which
    /// <c>Contract_HiredAlly</c>'s start (<c>FUN_140289490</c>) sets to the employer, a character the resolver finds wherever it is, alive or dead
    /// (<c>FUN_140284510</c>). With the employer gone from the world it is the contract's mission town (<c>FUN_140269cd0</c>,
    /// <see cref="Blackboard.ContractTown"/>), reached once inside its radius; with none it stands still. A base defence's target
    /// (<c>Contract_DefendPlayerBase</c>'s slot 1, <c>FUN_14028fa10</c>) is its mission town itself. Null when the platoon runs neither contract with
    /// such a job. Any thread: reads the characters and towns only.
    /// </summary>
    public static ContractGoal? EmployerGoal(World world, Platoon p, PopulationData data, float arrival)
    {
        if (OffscreenAi(p) is not { Contract: { } id } contract) return null;
        if (data.Ai.Package(id) is not { Signal: HiredAllySignal or DefendBaseSignal } package) return null;

        var job = PlatoonJobs.JobOf(package.Unloaded);
        if (job is not (PlatoonJob.TravelTarget or PlatoonJob.TravelTargetFast)) return null;
        if (package.Signal == DefendBaseSignal) return TownGoal(world, data, MissionTownOf(world, contract), job);

        var employer = contract.Target.IsNone ? world.Saved?.CharacterOf(contract.TargetHand) ?? CharacterId.None : contract.Target;
        if (!employer.IsNone && world.Characters.TryResolveNext(employer, out int slot))
        {
            var at = world.Characters.Previous[slot].Position;
            return new ContractGoal(new Vector2(at.X, at.Z), arrival, job);
        }

        return TownGoal(world, data, MissionTownOf(world, contract), job);
    }

    /// <summary>
    /// The contract's mission town: the one the squad was hired near, else the one its save names; none without either.
    /// </summary>
    static CampaignTown MissionTownOf(World world, AiSave contract) =>
        !contract.MissionTown.IsNone ? contract.MissionTown
        : world.Saved is { } saved ? WarSave.Town(world, saved, contract.MissionTownHand)
        : CampaignTown.None;

    /// <summary>
    /// The goal of a town: its centre and radius (a placed town's, or a player town still standing); standing still for none.
    /// </summary>
    static ContractGoal TownGoal(World world, PopulationData data, CampaignTown town, PlatoonJob job)
    {
        if (town.Site >= 0 && town.Site < data.Sites.Count)
        {
            var site = data.Sites[town.Site];
            return new ContractGoal(new Vector2(site.Position.X, site.Position.Z), site.Town.Radius, job);
        }

        if (town.PlayerTown > 0 && world.Buildings.FindTown(town.PlayerTown) is { } built)
            return new ContractGoal(new Vector2(built.Position.X, built.Position.Z), built.Radius, job);

        return new ContractGoal(null, 0, job);
    }

    /// <summary>
    /// The contract and <c>change AI</c> kept with an unloaded platoon's characters: a stored squad's, else the save's platoon's (the save's record until
    /// its squad first left the world).
    /// </summary>
    static AiSave? OffscreenAi(Platoon p) => p.Stored is { } stored ? stored.Ai : p.Saved?.Ai;
}
