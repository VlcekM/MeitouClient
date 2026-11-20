using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Population;

namespace Meitou.Simulation.Ai;

/// <summary>
/// The AI's rungs of the chooser for one character (<see cref="ITierRungs"/>, <see cref="Ladder"/>): the plan step before the choice, committed
/// mode, the scorer (<c>FUN_14050df10</c>) over the second list, the role list and the own lists, the tier picks (<c>FUN_140510d60</c>,
/// <c>FUN_140510650</c>) and the IDLE default. One per worker thread (<see cref="Local"/>), bound to the character before each choice; it writes
/// only that character's mind.
/// </summary>
internal sealed class TierRungs : ITierRungs
{
    #region Binding

    /// <summary>
    /// The worker thread's rungs (<see cref="Local"/>).
    /// </summary>
    [ThreadStatic] static TierRungs? s_local;

    /// <summary>
    /// The calling worker thread's rungs, made on its first use.
    /// </summary>
    public static TierRungs Local => s_local ??= new TierRungs();

    /// <summary>
    /// The step that separates two candidates of the same score (the original's 0.0001).
    /// </summary>
    const float TieStep = 1e-4f;

    /// <summary>
    /// The bound AI system.
    /// </summary>
    AiSystem _ai = null!;
    /// <summary>
    /// The bound world.
    /// </summary>
    World _world = null!;
    /// <summary>
    /// The bound character's slot.
    /// </summary>
    int _slot;
    /// <summary>
    /// The bound character's id.
    /// </summary>
    CharacterId _id;
    /// <summary>
    /// The bound character's mind.
    /// </summary>
    AiMind _mind = null!;
    /// <summary>
    /// The bound character's squad.
    /// </summary>
    Squad _squad = null!;
    /// <summary>
    /// The blackboard the bound character chooses under.
    /// </summary>
    Blackboard _board = null!;
    /// <summary>
    /// The role list the bound character takes.
    /// </summary>
    IReadOnlyList<AiGoal> _roles = [];
    /// <summary>
    /// The game hour of the choice.
    /// </summary>
    int _hour;
    /// <summary>
    /// Which goals may be weighed (<see cref="AiSystem.Restrict"/>).
    /// </summary>
    AiSystem.Restrict _only;
    /// <summary>
    /// The planner bound to the character.
    /// </summary>
    AiSystem.PlanContext _plans = null!;
    /// <summary>
    /// The goal the character is committed to for this choice (null: none).
    /// </summary>
    AiGoal? _committed;
    /// <summary>
    /// One tier's candidates, highest score first: a goal, or a source of the look (<c>Source</c> at least 0, the goal null).
    /// </summary>
    readonly List<(AiGoal? Goal, float Score, float Desire, int Source)> _candidates = [];
    /// <summary>
    /// The second list's candidates, kept for the urgent pass's pick or the priority-3 pick.
    /// </summary>
    readonly List<(AiGoal? Goal, float Score, float Desire, int Source)> _second = [];

    /// <summary>
    /// Binds the rungs to one character's choice; <paramref name="only"/> limits the goals weighed.
    /// </summary>
    public TierRungs Bind(AiSystem ai, World world, int slot, CharacterId id, AiMind mind, Squad squad, Blackboard board,
        IReadOnlyList<AiGoal> roles, int hour, AiSystem.Restrict only)
    {
        (_ai, _world, _slot, _id, _mind, _squad, _board, _roles, _hour, _only) = (ai, world, slot, id, mind, squad, board, roles, hour, only);
        _plans = AiSystem.PlanContext.Local.Bind(ai, world, slot, mind, board);
        _committed = null;
        _candidates.Clear();
        _second.Clear();
        return this;
    }

    #endregion

    #region Ladder rungs

    /// <summary>
    /// The plan step (<see cref="AiSystem.EndStep"/>) and committed mode: while a goal it committed to is running, only a tier above that goal's
    /// priority may take over; if none plans, the goal and its plan stay (<c>FUN_140510ef0</c> in committed mode, <b>Verified</b>). Not past its
    /// hours: the plan step ends a goal's plan then, and the update clears a goal left with no plan however long its commitment runs
    /// (<c>FUN_14050d970</c>, <c>FUN_140511130</c>, <b>Verified</b>; docs/game/ai-tasks.md "Precedence").
    /// </summary>
    public TaskPriority Begin()
    {
        _ai.EndStep(_world, _slot, _id, _mind, _squad, _board, _plans, _hour);
        _committed = _mind.Goal is { } running && _mind.Plan.Count > 0 && TaskProfiles.Of(running.Task.Type).Commits
            && _world.GameHours < _mind.CommitUntil && running.InWindow(_hour) ? running : null;
        return _committed?.Task.Priority ?? TaskPriority.JustAction;
    }

    /// <summary>
    /// The goal the character is committed to (null: none).
    /// </summary>
    public AiGoal? Committed => _committed;

    /// <summary>
    /// Scores the second list's goals into their own candidates (any priority: they join the urgent pass's pick, or the priority-3 one).
    /// </summary>
    public void ScoreSecondList(IReadOnlyList<AiGoal> list)
    {
        _second.Clear();
        for (int k = 0; k < list.Count; k++) Consider(_second, list[k], tier: null);
    }

    /// <summary>
    /// The urgent pass's pick over the second list's candidates (a character with jobs); its failure clears them.
    /// </summary>
    public AiGoal? PickSecondList()
    {
        var goal = PickGoal(_second, out _);
        _second.Clear();
        return goal;
    }

    /// <summary>
    /// One tier's pick: in the original's order, which settles ties (<see cref="Rank"/>): at priority 3 the second list's candidates still kept
    /// (scored into the map before the tier, <c>FUN_140510ef0</c>), then the role list, then the character's own list, where setupAI puts Self
    /// preservation before the faction's and race's goals (<c>FUN_140510d60</c>, <c>FUN_140621b70</c>, <b>Verified</b>), then the look's sources of
    /// the priority-3 goals (the decision step's step 4).
    /// </summary>
    public AiGoal? Tier(TaskPriority tier, ChooserRow? row, int from, int to, out int source)
    {
        var candidates = _candidates;
        candidates.Clear();
        if (tier == TaskPriority.Urgent) AddSecondList(candidates);
        for (int k = 0; k < _roles.Count; k++) Consider(candidates, _roles[k], tier);
        if (tier == TaskPriority.Urgent) Consider(candidates, AiSystem.SelfPreservationGoal, tier);
        AddOwnLists(candidates, tier);
        AddSources(candidates, row, from, to);

        return PickGoal(candidates, out source);
    }

    /// <summary>
    /// The default task: the IDLE plan (weight 0.1), which is no candidate of a tier (<c>FUN_140510ef0</c>, <b>Observed</b>); null when it does not
    /// plan.
    /// </summary>
    public AiGoal? DefaultTask()
    {
        var idle = AiSystem.Idle;
        float desire = _ai.Desire(_world, TaskBehaviours.Of(idle.Task.Type), idle.Task.Type, TaskProfiles.Of(idle.Task.Type), _squad, _board, _mind,
            _id, idle.Task.Target);
        return _plans.TryPlan(idle, desire) ? idle : null;
    }

    /// <summary>
    /// The mover reset: the plan cleared, IDLE standing for no goal.
    /// </summary>
    public AiGoal Mover()
    {
        _mind.Plan.Clear();
        return AiSystem.Idle;
    }

    #endregion

    #region Restricted choice

    /// <summary>
    /// The choice of a character the AI does not hold (<see cref="AiSystem.Preempts"/>) or that thinks in its cage (<see cref="AiSystem.Restrict.Caged"/>):
    /// by tier (only the urgent one unless caged), the goals <see cref="_only"/> allows; null when none plans. No sources, no committed stop.
    /// </summary>
    public AiGoal? Restricted()
    {
        bool allTiers = _only == AiSystem.Restrict.Caged;
        if (allTiers) _ai.EndStep(_world, _slot, _id, _mind, _squad, _board, _plans, _hour);

        foreach (var tier in (ReadOnlySpan<TaskPriority>)[TaskPriority.Urgent, TaskPriority.NonUrgent, TaskPriority.Fluff])
        {
            if (!allTiers && tier != TaskPriority.Urgent) break;
            if (Tier(tier, null, 0, 0, out _) is { } goal) return goal;
        }

        return null;
    }

    #endregion

    #region Scoring

    /// <summary>
    /// The own lists' goals of the tier: for a player-faction mind only the types on the ladder's rungs (<see cref="Ladder.OnRungs"/>); the rest are
    /// their sources'.
    /// </summary>
    void AddOwnLists(List<(AiGoal? Goal, float Score, float Desire, int Source)> candidates, TaskPriority tier)
    {
        var own = _mind.RaceGoals;
        for (int k = 0; k < own.Count; k++)
        {
            if (_mind.Player && !Ladder.OnRungs.Contains(own[k].Task.Type)) continue;
            Consider(candidates, own[k], tier);
        }
    }

    /// <summary>
    /// The look's goal sources that can run, at their step-4 scores (<see cref="ChooserRow.Scores"/>).
    /// </summary>
    static void AddSources(List<(AiGoal? Goal, float Score, float Desire, int Source)> candidates, ChooserRow? row, int from, int to)
    {
        if (row is null) return;

        for (int k = from; k < to; k++)
        {
            if (row.Asked[k] && row.Checks[k].Can && row.Scores[k] > 0) Rank(candidates, null, row.Scores[k], 0, k);
        }
    }

    /// <summary>
    /// The second list's candidates still kept join the priority-3 pick first (a character with no jobs: no urgent pick took them), so a goal of the
    /// tier with the same score ranks above them.
    /// </summary>
    void AddSecondList(List<(AiGoal? Goal, float Score, float Desire, int Source)> candidates)
    {
        foreach (var (goal, score, desire, _) in _second) Rank(candidates, goal, score, desire, -1);
        _second.Clear();
    }

    /// <summary>
    /// Scores goal <paramref name="g"/> into <paramref name="candidates"/> when it is of <paramref name="tier"/> (null: any) and in its hours, may be
    /// weighed (<see cref="_only"/>), is wanted and scores above 0, and is not dropped for having nothing to do.
    /// </summary>
    void Consider(List<(AiGoal? Goal, float Score, float Desire, int Source)> candidates, AiGoal g, TaskPriority? tier)
    {
        if (tier is { } t && g.Task.Priority != t || !g.InWindow(_hour)) return;

        // The list entries of Self preservation are the engine's own goal, which every character has once.
        if (g.Task.Type == TaskType.SelfPreservation && !ReferenceEquals(g, AiSystem.SelfPreservationGoal)) return;
        if (Excluded(g)) return;

        var profile = TaskProfiles.Of(g.Task.Type);
        float desire = _ai.Desire(_world, TaskBehaviours.Of(g.Task.Type), g.Task.Type, profile, _squad, _board, _mind, _id, g.Task.Target);
        if (desire <= 0) return;

        float score = _ai.Score(_world, g, profile, desire, _squad, _board, _mind, _id);
        if (score <= 0 || _plans.GivesNothingNow(g)) return;

        Rank(candidates, g, score, desire, -1);
    }

    /// <summary>
    /// Whether the restriction leaves goal <paramref name="g"/> out.
    /// </summary>
    bool Excluded(AiGoal g) => _only switch
    {
        AiSystem.Restrict.Self => !ReferenceEquals(g, AiSystem.SelfPreservationGoal),
        AiSystem.Restrict.Combat => TaskBehaviours.Of(g.Task.Type) != AiBehaviour.Fight && !ReferenceEquals(g, AiSystem.SelfPreservationGoal),
        AiSystem.Restrict.Caged => AiSystem.PlanContext.OwnPlan(g),
        AiSystem.Restrict.Urgent => g.Task.Priority != TaskPriority.Urgent,
        _ => false,
    };

    /// <summary>
    /// The pick (<c>FUN_140510650</c>): the candidates highest first; a source wins as it is (the look found it can run); the current goal with a
    /// step running is kept as it is (<see cref="AiSystem.Kept"/>), else the first whose plan succeeds wins.
    /// </summary>
    AiGoal? PickGoal(List<(AiGoal? Goal, float Score, float Desire, int Source)> candidates, out int source)
    {
        source = -1;
        for (int k = 0; k < candidates.Count; k++)
        {
            var (g, _, desire, from) = candidates[k];
            if (from >= 0)
            {
                source = from;
                return null;
            }

            if (AiSystem.Kept(_mind, g!) || _plans.TryPlan(g!, desire)) return g;
        }

        return null;
    }

    /// <summary>
    /// Puts a candidate in the list, highest score first. The original keys its candidates by score and, while the key is taken, adds 0.0001
    /// (<c>FUN_14050df10</c>, <b>Verified</b>): of equal scores the one listed later ranks first.
    /// </summary>
    static void Rank(List<(AiGoal? Goal, float Score, float Desire, int Source)> candidates, AiGoal? g, float score, float desire, int source)
    {
        for (int k = 0; k < candidates.Count; k++)
        {
            if (candidates[k].Score == score)
            {
                score += TieStep;
                k = -1;
            }
        }

        int at = candidates.Count;
        while (at > 0 && candidates[at - 1].Score < score) at--;
        candidates.Insert(at, (g, score, desire, source));
    }

    #endregion
}
