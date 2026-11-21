using Meitou.Data.Gameplay.Ai;

namespace Meitou.Simulation.Ai;

/// <summary>
/// What the chooser picked for one character (<see cref="Ladder.Run"/>): a source of the serial look (<see cref="Source"/>, started in the serial
/// pass), or a goal the AI acts out (<see cref="Goal"/>), or an entry of the second list (<see cref="Second"/>, started in the serial pass), or
/// none. <see cref="Through"/> is the end of the sources the ladder went past: those below it that could not run are passed
/// (<see cref="IDecisionSource.Passed"/>) in the serial pass, as the decision step passed them.
/// </summary>
internal readonly record struct LadderPick(int Source, AiGoal? Goal, int Through, int Second = -1)
{
    /// <summary>
    /// The candidate code of second-list entry <paramref name="k"/> among a pick's candidates, whose look sources are 0 up and goals -1.
    /// </summary>
    public static int SecondCode(int k) => -2 - k;

    /// <summary>
    /// The pick a candidate code stands for once it wins: a look source, a second-list entry, else the goal.
    /// </summary>
    public static LadderPick Of(int code, AiGoal? goal, int through) => code switch
    {
        >= 0 => new(code, null, through),
        < -1 => new(-1, null, through, -2 - code),
        _ => new(-1, goal, through),
    };

    /// <summary>
    /// Nothing picked, every source passed (the mover reset).
    /// </summary>
    public static LadderPick None(int through) => new(-1, null, through);
}

/// <summary>
/// The AI's rungs of the ladder for one bound character (<see cref="TierRungs"/>): the plan step and committed mode before the choice, the second
/// list's scores, the tier passes (requests, role list, own lists; the sources of the priority-3 goals scored with them) and the default task.
/// Every call runs in the character's own partition of the think.
/// </summary>
internal interface ITierRungs
{
    /// <summary>
    /// The update's start (<c>FUN_140511130</c>): without a replan request the plan step (the current goal's running step ends by its tests), with
    /// one the uncommitted goal cleared; returns the priority of the goal the character is committed to (<see cref="TaskPriority.JustAction"/>:
    /// none).
    /// </summary>
    TaskPriority Begin();

    /// <summary>
    /// The pick of a committed stop that ends the choice: the goal the character is committed to, kept with its plan; after a replan request, which
    /// stopped its task, the mover reset (null: none).
    /// </summary>
    AiGoal? Committed { get; }

    /// <summary>
    /// Takes the second list's scores of the look (<c>FUN_14050df10</c> over <c>+0x70</c>) into the candidates of the next pick.
    /// </summary>
    void ScoreSecondList(ChooserRow? row);

    /// <summary>
    /// Whether the character holds job 187 in effect (an NPC that obeys its master's Slave goals): its job passes score them, and it counts as
    /// having a job list.
    /// </summary>
    bool Obeys { get; }

    /// <summary>
    /// A job pass's pick over the AI's candidates: the 187 branch's goals (the master's Slave goals) after the candidates still kept, which in the
    /// urgent pass are the second list's (the list jobs come first by their keys). Either way the candidates are cleared, as a failed pick clears
    /// the map. The goal planned, or null; a second-list entry that won in <paramref name="code"/> (<see cref="LadderPick.SecondCode"/>, else -1).
    /// </summary>
    AiGoal? PickJobPass(out int code);

    /// <summary>
    /// One tier's pick (<c>FUN_140510d60</c>): its goals scored (the second list's candidates still kept join the priority-3 pick), with the
    /// look's sources of <paramref name="from"/> to <paramref name="to"/> scored among them; the goal planned (or kept), or a source in
    /// <paramref name="source"/> (the goal null), or nothing.
    /// </summary>
    AiGoal? Tier(TaskPriority tier, ChooserRow? row, int from, int to, out int source);

    /// <summary>
    /// The default task (<c>+0x190</c>, at key 1.0, picked without the scorer): Meitou's IDLE plan, or null when it does not plan.
    /// </summary>
    AiGoal? DefaultTask();

    /// <summary>
    /// The mover reset: no goal, no plan; the IDLE goal stands for it.
    /// </summary>
    AiGoal Mover();
}

/// <summary>
/// The original's single chooser <c>AITaskSytem::chooseGoal</c> (<c>FUN_140510ef0</c>, <b>Verified</b>; docs/game/ai-tasks.md "Precedence"), the
/// one ladder every character with a squad runs, player or NPC (docs/simulation.md "Decision step"). Its rungs in order: the order step, the
/// committed stop above priority 2, the second list, the urgent job pass, the priority-3 tier, the committed stop above 1, the full job pass (the
/// player faction's implicit jobs last), the priority-2 tier, the committed stop above 0, the priority-1 tier, the default task, the mover reset.
/// The sources' rungs read the serial look (<see cref="ChooserRow"/>); the tiers and the default task are the AI's (<see cref="ITierRungs"/>,
/// null for a character with no mind). Runs in the parallel think; it reads only its own row and its own mind.
/// </summary>
internal static class Ladder
{
    /// <summary>
    /// The goal types of the own lists of a player-faction mind (<see cref="AiMind.Player"/>) that are on the ladder's rungs: their goals are
    /// weighed by the tiers and acted out as an NPC's. Every other type of those lists is still decided by its source (an adapter,
    /// <see cref="DecisionSystem"/>) and weighed 0 by the tiers, so nothing is chosen twice. A fan-out step that moves a goal onto its rung adds its
    /// type here and deletes the adapter. Never written at run time.
    /// </summary>
    internal static readonly HashSet<TaskType> OnRungs =
    [
        TaskType.AquireFoodAtHomebase, TaskType.FindCropsToEat, TaskType.EatARandomKoBody, TaskType.AnimalFetchALimb,
        TaskType.PlayBecauseIHaveALimbInMouth, TaskType.ChaseAllyDogsWithMouthLimbs, TaskType.ProtectAllies, TaskType.EatFoodOnGround,
        TaskType.FindAndRescueIfTheresBeds, TaskType.FindAndRescueLeader,
    ];

    /// <summary>
    /// The first key of the priority-1 sources of <see cref="DecisionStep.Fallback"/> (keys 1000 + type are priority-2 goals, 2000 + type
    /// priority-1; the original's tier order).
    /// </summary>
    internal const int FluffKeys = 2000;

    /// <summary>
    /// The first key of the default task's sources of <see cref="DecisionStep.Fallback"/>.
    /// </summary>
    internal const int DefaultKeys = 3000;

    /// <summary>
    /// The ladder for one character: <paramref name="steps"/> the sources' ranges, <paramref name="row"/> its look (null: no source was asked),
    /// <paramref name="tiers"/> the AI's rungs bound to it (null: no mind), <paramref name="jobs"/> whether it has a job list (<c>+0x90</c>),
    /// the second list scored by the look (<see cref="ChooserRow.SecondScores"/>).
    /// </summary>
    public static LadderPick Run(in SourceSteps steps, ChooserRow? row, ITierRungs? tiers, bool jobs)
    {

        // 1. The order step (FUN_140510920): its pick ends the choice.
        if (First(row, steps.Orders, steps.Commitment) is { } order) return order;

        // 2. The update's start (the plan step, or a replan request's clear), then committed above priority 2: the goal keeps its plan.
        var committed = tiers?.Begin() ?? TaskPriority.JustAction;
        if (committed > TaskPriority.NonUrgent) return new(-1, tiers!.Committed, steps.Commitment);
        if (First(row, steps.Commitment, steps.UrgentJobs) is { } commitment) return commitment;

        // 3 and 4. The second list scored, then the urgent job pass (with jobs: an NPC's job 187 counts), whose pick takes the second list's
        // candidates and the 187 branch's.
        tiers?.ScoreSecondList(row);
        if (First(row, steps.UrgentJobs, steps.Goals) is { } urgentJob) return urgentJob;
        if ((jobs || tiers?.Obeys == true) && JobPass(tiers, steps.Goals) is { } second) return second;

        return AfterUrgentJobs(steps, row, tiers, committed);
    }

    /// <summary>
    /// The ladder from the priority-3 tier on (<see cref="Run"/>).
    /// </summary>
    static LadderPick AfterUrgentJobs(in SourceSteps steps, ChooserRow? row, ITierRungs? tiers, TaskPriority committed)
    {
        // 5. The priority-3 tier with the goal sources.
        if (Tier(row, tiers, TaskPriority.Urgent, steps.Goals, steps.Jobs, steps.Jobs) is { } urgent) return urgent;

        // 6. Committed above priority 1, then 7. every job, the implicit jobs, then the 187 branch's goals (scores rank below the jobs' keys).
        if (committed > TaskPriority.Fluff) return new(-1, tiers!.Committed, steps.Jobs);
        if (First(row, steps.Jobs, steps.Fallback) is { } job) return job;
        if (tiers is { Obeys: true } && JobPass(tiers, steps.Fallback) is { } obeyed) return obeyed;

        // 8. The priority-2 tier, then its sources.
        if (Tier(row, tiers, TaskPriority.NonUrgent, 0, 0, steps.Fallback) is { } nonUrgent) return nonUrgent;
        if (First(row, steps.Fallback, steps.FallbackFluff) is { } nonUrgentSource) return nonUrgentSource;

        // 9. Committed above priority 0, then 10. the priority-1 tier and its sources.
        if (committed > TaskPriority.JustAction) return new(-1, tiers!.Committed, steps.FallbackFluff);
        if (Tier(row, tiers, TaskPriority.Fluff, 0, 0, steps.FallbackFluff) is { } fluff) return fluff;
        if (First(row, steps.FallbackFluff, steps.FallbackDefault) is { } fluffSource) return fluffSource;

        return DefaultTask(steps, row, tiers);
    }

    /// <summary>
    /// 11. The default task (its sources, then the AI's IDLE plan), else 12. the mover reset.
    /// </summary>
    static LadderPick DefaultTask(in SourceSteps steps, ChooserRow? row, ITierRungs? tiers)
    {
        if (First(row, steps.FallbackDefault, steps.End) is { } source) return source;
        if (tiers?.DefaultTask() is { } idle) return new(-1, idle, steps.End);

        return new(-1, tiers?.Mover(), steps.End);
    }

    /// <summary>
    /// A job pass's pick (<see cref="ITierRungs.PickJobPass"/>): a goal or a second-list entry; null when none plans.
    /// </summary>
    static LadderPick? JobPass(ITierRungs? tiers, int through)
    {
        if (tiers is null) return null;

        var goal = tiers.PickJobPass(out int code);
        return goal is null && code == -1 ? null : LadderPick.Of(code, goal, through);
    }

    /// <summary>
    /// The order step alone (rung 1): the first source of the orders step the look found can run or runs; null when none can. The think asks it
    /// before anything else, so an order that runs or starts beats every goal, held or not.
    /// </summary>
    public static LadderPick? Orders(in SourceSteps steps, ChooserRow? row) => First(row, steps.Orders, steps.Commitment);

    /// <summary>
    /// A tier's pick: the AI's (with the look's sources of <paramref name="from"/> to <paramref name="to"/> scored among its goals), or without a
    /// mind the sources alone (<see cref="BestSource"/>). <paramref name="through"/>: where the passed sources end when it picks.
    /// </summary>
    static LadderPick? Tier(ChooserRow? row, ITierRungs? tiers, TaskPriority tier, int from, int to, int through)
    {
        if (tiers is null)
        {
            int best = tier == TaskPriority.Urgent ? BestOfTier(row, from, to) : BestSource(row, from, to);
            return best == -1 ? null : LadderPick.Of(best, null, through);
        }

        var goal = tiers.Tier(tier, row, from, to, out int source);
        return goal is null && source == -1 ? null : LadderPick.Of(source, goal, through);
    }

    /// <summary>
    /// The first source of <paramref name="from"/> to <paramref name="to"/> the look found can run (or runs already), as the decision step's other
    /// steps take it; null when none can.
    /// </summary>
    static LadderPick? First(ChooserRow? row, int from, int to)
    {
        if (row is null) return null;

        for (int k = from; k < to; k++)
            if (row.Asked[k] && row.Checks[k].Can) return new(k, null, k);
        return null;
    }

    /// <summary>
    /// Without a mind, the priority-3 pick: the best of the second list's entries and the goal sources (a source wins a tie, as the tier's goals
    /// rank above the second list's leftovers); its candidate code, -1 for none.
    /// </summary>
    static int BestOfTier(ChooserRow? row, int from, int to)
    {
        int best = BestSource(row, from, to);
        if (row is null) return best;

        float bestScore = best >= 0 ? row.Scores[best] : 0;
        for (int k = 0; k < row.SecondCount; k++)
        {
            if (row.SecondScores[k] > bestScore) (best, bestScore) = (LadderPick.SecondCode(k), row.SecondScores[k]);
        }

        return best;
    }

    /// <summary>
    /// The best-scoring source of a goal step that can run, scored as the decision step's step 4 does (<see cref="ChooserRow.Scores"/>; a tie goes
    /// to the later-listed); -1 for none.
    /// </summary>
    internal static int BestSource(ChooserRow? row, int from, int to)
    {
        if (row is null) return -1;

        int best = -1;
        float bestScore = 0;
        for (int k = from; k < to; k++)
        {
            if (!row.Asked[k] || !row.Checks[k].Can) continue;
            if (row.Scores[k] > 0 && row.Scores[k] >= bestScore) (best, bestScore) = (k, row.Scores[k]);
        }

        return best;
    }
}

/// <summary>
/// The index ranges of the sources by step (they are sorted by step, then key).
/// </summary>
internal readonly struct SourceSteps
{
    /// <summary>
    /// The first index of each step's sources, and of the priority-1 and default-task keys of the fallback step; <see cref="End"/> the count.
    /// </summary>
    public readonly int Orders, Commitment, UrgentJobs, Goals, Jobs, Fallback, FallbackFluff, FallbackDefault, End;

    /// <summary>
    /// Finds the ranges in <paramref name="sources"/>.
    /// </summary>
    public SourceSteps(IReadOnlyList<IDecisionSource> sources)
    {
        End = sources.Count;
        Orders = 0;
        Commitment = StartOf(sources, DecisionStep.Commitment, int.MinValue);
        UrgentJobs = StartOf(sources, DecisionStep.UrgentJobs, int.MinValue);
        Goals = StartOf(sources, DecisionStep.Goals, int.MinValue);
        Jobs = StartOf(sources, DecisionStep.Jobs, int.MinValue);
        Fallback = StartOf(sources, DecisionStep.Fallback, int.MinValue);
        FallbackFluff = StartOf(sources, DecisionStep.Fallback, Ladder.FluffKeys);
        FallbackDefault = StartOf(sources, DecisionStep.Fallback, Ladder.DefaultKeys);
    }

    /// <summary>
    /// The index of the first source of <paramref name="step"/> with a key of at least <paramref name="key"/>, or of a later step.
    /// </summary>
    static int StartOf(IReadOnlyList<IDecisionSource> sources, DecisionStep step, int key)
    {
        for (int k = 0; k < sources.Count; k++)
            if (sources[k].Step > step || sources[k].Step == step && sources[k].Key >= key) return k;
        return sources.Count;
    }
}
