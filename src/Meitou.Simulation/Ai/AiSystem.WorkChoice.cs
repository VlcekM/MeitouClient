using Meitou.Data.Gameplay.Ai;
using Meitou.Data.World;
using Meitou.Simulation.Factions;

namespace Meitou.Simulation.Ai;

/// <summary>
/// The code mods' say in the workers' choice of workplace (docs/modding.md "Job choice"): a squad's assigned work building
/// (<see cref="SetSquadWork"/>) and the job score filters (<see cref="IJobScoreFilter"/>).
/// </summary>
public sealed partial class AiSystem
{
    /// <summary>
    /// The work building assigned to the squad of <paramref name="self"/> (<see cref="Squad.AssignedWork"/>) as a workplace the AI looked at; null
    /// without one. Any thread (reads only).
    /// </summary>
    TownBuilding? AssignedWorkOf(World world, CharacterId self) =>
        world.Characters.Cold(self.Slot) is { SquadId: >= 0 } cold ? AssignedWorkOf(world.Squads.Find(cold.SquadId)) : null;

    /// <summary>
    /// <see cref="AssignedWorkOf(World, CharacterId)"/> of <paramref name="squad"/>.
    /// </summary>
    TownBuilding? AssignedWorkOf(Squad? squad) => squad?.AssignedWork is { } key ? _townPlaces.FindWorkplace(key) : null;

    /// <summary>
    /// Whether a worker on <paramref name="type"/> may work at <paramref name="building"/>: 189 only where it can really work (<see cref="RealWork"/>),
    /// 190 anywhere.
    /// </summary>
    static bool WorksAt(World world, TownBuilding building, TaskType type) => type != TaskType.AutoLabouringMines || RealWork(world, building);

    /// <summary>
    /// <see cref="PickWork"/> with job score filters: each workplace with a free slot is scored (1, then the filters), those at 0 or below are left
    /// out, and among the best the same roll as the game's picks one, so asking twice names the same.
    /// </summary>
    TownBuilding? PickScoredWork(World world, CharacterId self, IReadOnlyList<TownBuilding> list, int epoch, TaskType type, TownBuilding? held)
    {
        float best = 0;
        int ties = 0;
        var scores = new float[list.Count];
        for (int k = 0; k < list.Count; k++)
        {
            var b = list[k];
            scores[k] = HasFreeSlot(b, held) ? world.JobScoreFilters.Pass(world, self, type, JobCandidateKind.Workplace, b.Key, b.BuildingId, 1) : 0;
            if (scores[k] > best) (best, ties) = (scores[k], 1);
            else if (scores[k] == best && best > 0) ties++;
        }

        if (ties == 0) return null;

        int pick = Rng.Int(Rng.Hash(world.Seed, Rng.Key(self), RngPurpose.Think, SeedSalts.AiTarget ^ (ulong)epoch << 20 ^ 0x57), ties);
        for (int k = 0; k < list.Count; k++)
            if (scores[k] == best && pick-- == 0) return list[k];
        return null;
    }
}
