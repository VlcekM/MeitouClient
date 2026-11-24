using Meitou.Data.Gameplay.Ai;
using Meitou.Data.World;

namespace Meitou.Simulation.Ai;

/// <summary>
/// The job score filters' say in the workers' choice of workplace (docs/modding.md "Job choice", <see cref="IJobScoreFilter"/>).
/// </summary>
internal static class ScoredWork
{
    /// <summary>
    /// <see cref="AiSystem.PickWork"/> with job score filters: each workplace with a free slot is scored (1, then the filters), those at 0 or below
    /// are left out, and among the best the same roll as the game's picks one, so asking twice names the same.
    /// </summary>
    public static TownBuilding? Pick(AiSystem ai, World world, CharacterId self, IReadOnlyList<TownBuilding> list, int epoch, TaskType type,
        TownBuilding? held)
    {
        var (scores, best, ties) = Score(ai, world, self, list, type, held);
        if (ties == 0) return null;

        ulong salt = SeedSalts.AiTarget ^ (ulong)epoch << 20 ^ 0x57;
        int pick = Rng.Int(Rng.Hash(world.Seed, Rng.Key(self), RngPurpose.Think, salt), ties);
        for (int k = 0; k < list.Count; k++)
            if (scores[k] == best && pick-- == 0) return list[k];
        return null;
    }

    /// <summary>
    /// Each workplace's score (0 without a free slot), the best of them and how many reach it.
    /// </summary>
    static (float[] Scores, float Best, int Ties) Score(AiSystem ai, World world, CharacterId self, IReadOnlyList<TownBuilding> list, TaskType type,
        TownBuilding? held)
    {
        float best = 0;
        int ties = 0;
        var scores = new float[list.Count];
        for (int k = 0; k < list.Count; k++)
        {
            var b = list[k];
            bool free = ai.HasFreeSlot(b, held);
            scores[k] = free ? world.JobScoreFilters.Pass(world, self, type, JobCandidateKind.Workplace, b.Key, b.BuildingId, 1) : 0;
            if (scores[k] > best) (best, ties) = (scores[k], 1);
            else if (scores[k] == best && best > 0) ties++;
        }

        return (scores, best, ties);
    }
}
