using Meitou.Data.Gameplay.Ai;

namespace Meitou.Simulation.Ai;

/// <summary>
/// Which of the chooser's desires a goal desire filter is asked about (<see cref="GoalDesireAsk.Kind"/>).
/// </summary>
public enum GoalDesireKind : byte
{
    /// <summary>
    /// A goal of the character's goal lists as the goal scorer weighs it (its desire before the list's weight, crowding and continuity), an engine
    /// goal or a code mod's.
    /// </summary>
    Goal,

    /// <summary>
    /// A TaskData row on the way of a plan, as the planner weighs it (0 or below: a dead end), such as getting out of bed once healed.
    /// </summary>
    PlanStep,

    /// <summary>
    /// An order (the order queue's front, whose desire 0 or below ends it, and the second list's entries) as the order step weighs it.
    /// </summary>
    Order,

    /// <summary>
    /// A job of the character's job list that could run (<see cref="GoalDesireAsk.Job"/> its index), asked with desire 1; 0 or below passes over it
    /// as a job that cannot run, and the next job is tried.
    /// </summary>
    Job,

    /// <summary>
    /// The current goal's whole score as the goal scorer holds it this think (after the list's weight, crowding, the continuity and the
    /// commitment), asked for the current goal only, with <see cref="GoalDesireAsk.HeldScore"/> the score it was given at its last think; the
    /// answer is the score it competes with and is held for the next think (the original's AI keeps its current goal's score, which a
    /// <c>Tasker::score</c> hook may rewrite: the SquadAutonomy patrol rule's max(0.0001, held x 0.95)). 0 or below drops it this think.
    /// </summary>
    HeldScore,
}

/// <summary>
/// A desire the chooser is about to use (<see cref="IGoalDesireFilter"/>): the world's tick, the character, the task type (the goal's, the row's,
/// the order's or the job's), the kind, the desire as the game computed it and, for a job, its index in the character's job list (-1 otherwise).
/// </summary>
public readonly record struct GoalDesireAsk(long Tick, CharacterId Character, TaskType Type, GoalDesireKind Kind, float Desire, int Job = -1)
{
    /// <summary>
    /// For <see cref="GoalDesireKind.HeldScore"/>: the score the current goal was held at after its last think's filters (NaN for its first think as
    /// the current goal, after a load, or for another kind).
    /// </summary>
    public float HeldScore { get; init; } = float.NaN;
}

/// <summary>
/// A filter on the chooser's desires (docs/modding.md "Chooser goals and jobs", desire filters), registered with
/// <see cref="ChooserRegistry.DesireFilter"/>: asked for every desire of every task type the chooser weighs, the engine's and the code mods'. It
/// answers the desire to use. The goal scorer and the planner run in the parallel think, so it runs on any thread, concurrently: it must be quick,
/// thread-safe and a pure read of the ask and of world state the tick has settled. A throw or an answer that is not a finite number leaves the
/// desire as it came; the filter is still asked for the rest of that tick, not from the next, and the fault enters the state hash. Engine API, not
/// in the original.
/// </summary>
public interface IGoalDesireFilter
{
    /// <summary>
    /// The desire to use for <paramref name="ask"/>.
    /// </summary>
    float Desire(World world, in GoalDesireAsk ask);
}

/// <summary>
/// The world's goal desire filters (<see cref="IGoalDesireFilter"/>), kept by the chooser's host; asked from the parallel think and the serial look.
/// </summary>
sealed class GoalDesireFilters
{
    /// <summary>
    /// The registered filters.
    /// </summary>
    internal readonly ParallelHooks<IGoalDesireFilter> Hooks = new(0x47445346, "goal desire filter");

    /// <summary>
    /// Whether any filter is registered; the desire is used as it is without one.
    /// </summary>
    internal bool Active => Hooks.Active;

    /// <summary>
    /// <paramref name="desire"/> after every filter not faulted before this tick, each seeing the one before's answer. Any thread.
    /// </summary>
    internal float Pass(World world, CharacterId character, TaskType type, GoalDesireKind kind, float desire, int job = -1,
        float held = float.NaN)
    {
        long tick = world.Tick;
        for (int i = 0; i < Hooks.Count; i++)
        {
            if (!Hooks.Asked(i, tick)) continue;

            desire = Ask(world, i, new GoalDesireAsk(tick, character, type, kind, desire, job) { HeldScore = held });
        }

        return desire;
    }

    /// <summary>
    /// One filter's answer; the desire as it came when the filter throws or answers a number that is not finite.
    /// </summary>
    float Ask(World world, int i, in GoalDesireAsk ask)
    {
        try
        {
            float desire = Hooks[i].Desire(world, ask);
            if (float.IsFinite(desire)) return desire;

            Hooks.Fault(i, ask.Tick, $"desire {desire} is not a finite number");
        }
        catch (Exception e)
        {
            Hooks.Threw(i, ask.Tick, e);
        }

        return ask.Desire;
    }
}
