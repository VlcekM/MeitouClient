using Meitou.Data.Gameplay.Ai;
using Meitou.Data.World;

namespace Meitou.Simulation.Ai;

/// <summary>
/// What a job candidate is (<see cref="JobScoreAsk.Kind"/>); room for more kinds.
/// </summary>
public enum JobCandidateKind : byte
{
    /// <summary>
    /// A workplace the AI's workers (189 Auto labouring, 190 its pretend) may pick in their work town: a town building or an outpost machine with a
    /// free operator slot.
    /// </summary>
    Workplace,
}

/// <summary>
/// A job candidate the AI weighs for a character (<see cref="IJobScoreFilter"/>): the tick, the character, the goal's task type, the kind, the
/// site (a workplace's <see cref="TownBuilding.Key"/>), its BUILDING record's string id and the score (1 as the game weighs every free workplace
/// alike, or a filter before's answer).
/// </summary>
public readonly record struct JobScoreAsk(long Tick, CharacterId Character, TaskType Type, JobCandidateKind Kind, string Site, string Record,
    float Score);

/// <summary>
/// A system asked to score the AI's job candidates (docs/modding.md "Job choice"): the best score wins, ties drawn as the game draws among its
/// candidates; 0 or below leaves a candidate out. A mod ranks by the stat a workplace trains (its record's data) against the character's. Filters
/// run in system order, each seeing the one before's answer, on any thread and concurrently (the think picks too): quick, thread-safe, a pure read
/// of the ask and of world state the tick has settled, the same answer for the same ask within a tick. A throw or an answer that is not a finite
/// number keeps the score; the filter is still asked for the rest of that tick, not from the next, and the fault enters the state hash. Engine
/// API, not in the original.
/// </summary>
public interface IJobScoreFilter : ITickSystem
{
    /// <summary>
    /// The score to use for the candidate <paramref name="ask"/> names.
    /// </summary>
    float Score(World world, in JobScoreAsk ask);
}

/// <summary>
/// The world's job score filters (<see cref="IJobScoreFilter"/>); any thread.
/// </summary>
sealed class JobScoreFilters
{
    /// <summary>
    /// The registered filters.
    /// </summary>
    readonly ParallelHooks<IJobScoreFilter> _hooks = new(0x4A534346, "job score filter");

    /// <summary>
    /// Whether any filter is registered; the game's choice stands without one.
    /// </summary>
    internal bool Active => _hooks.Active;

    /// <summary>
    /// Registers <paramref name="system"/> when it is a filter.
    /// </summary>
    internal void Register(ITickSystem system, int systemIndex)
    {
        if (system is IJobScoreFilter filter) _hooks.Add(filter, system, systemIndex);
    }

    /// <summary>
    /// Adds the faulted filters to the world's state hash.
    /// </summary>
    internal void HashFaults(ref StateHasher hash) => _hooks.HashFaults(ref hash);

    /// <summary>
    /// The score of a candidate after every filter not faulted before this tick, starting from <paramref name="score"/>. Any thread.
    /// </summary>
    internal float Pass(World world, CharacterId character, TaskType type, JobCandidateKind kind, string site, string record, float score)
    {
        long tick = world.Tick;
        for (int i = 0; i < _hooks.Count; i++)
        {
            if (_hooks.Asked(i, tick))
                score = Ask(world, i, new JobScoreAsk(tick, character, type, kind, site, record, score));
        }

        return score;
    }

    /// <summary>
    /// One filter's answer; the score as it came (and faulted) when the filter throws or answers a number that is not finite.
    /// </summary>
    float Ask(World world, int i, in JobScoreAsk ask)
    {
        try
        {
            float score = _hooks[i].Score(world, ask);
            if (float.IsFinite(score)) return score;

            _hooks.Fault(i, ask.Tick, $"score {score} is not a finite number");
        }
        catch (Exception e)
        {
            _hooks.Threw(i, ask.Tick, e);
        }

        return ask.Score;
    }
}
