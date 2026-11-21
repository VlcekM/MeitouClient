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
    /// The planner bound to the character.
    /// </summary>
    AiSystem.PlanContext _plans = null!;
    /// <summary>
    /// The goal the character is committed to for this choice (null: none).
    /// </summary>
    AiGoal? _committed;
    /// <summary>
    /// The original's replan request is pending for this choice (<see cref="AiMind.ReplanAsked"/>): no plan step, the uncommitted goal cleared.
    /// </summary>
    bool _replan;
    /// <summary>
    /// One tier's candidates, highest score first: a goal, or a source of the look (<c>Source</c> at least 0, the goal null).
    /// </summary>
    readonly List<(AiGoal? Goal, float Score, float Desire, int Source)> _candidates = [];
    /// <summary>
    /// The second list's candidates, kept for the urgent pass's pick or the priority-3 pick; the job passes add the 187 branch's to them.
    /// </summary>
    readonly List<(AiGoal? Goal, float Score, float Desire, int Source)> _second = [];
    /// <summary>
    /// The tier's requests copied from the squad's blackboard for one pick (<c>+0x2e8</c>, emptied at the choice's start and after a tier that
    /// fails, <c>FUN_140514100</c>).
    /// </summary>
    readonly List<AiGoal> _requests = [];
    /// <summary>
    /// The master squad's Slave goals the job passes' 187 branch scores for the bound character (<see cref="AiSystem.ObeyedRoles"/>: an NPC, or a
    /// player character whose list holds job 187); null when it does not obey.
    /// </summary>
    IReadOnlyList<AiGoal>? _slaveGoals;

    /// <summary>
    /// Binds the rungs to one character's choice; <paramref name="replan"/>: the original's replan request is pending (<see cref="Begin"/>).
    /// </summary>
    public TierRungs Bind(AiSystem ai, World world, int slot, CharacterId id, AiMind mind, Squad squad, Blackboard board,
        IReadOnlyList<AiGoal> roles, int hour, bool replan)
    {
        (_ai, _world, _slot, _id, _mind, _squad, _board, _roles, _hour) = (ai, world, slot, id, mind, squad, board, roles, hour);
        _replan = replan;
        _plans = AiSystem.PlanContext.Local.Bind(ai, world, slot, mind, board);
        _slaveGoals = world.Characters.Cold(slot) is { } cold ? AiSystem.ObeyedRoles(world, squad, cold) : null;
        _committed = null;
        _candidates.Clear();
        _second.Clear();
        return this;
    }

    #endregion

    #region Ladder rungs

    /// <summary>
    /// The update's start (<c>FUN_140511130</c>, <b>Verified</b>; docs/game/ai-tasks.md "Precedence"). Without a replan request: the plan step
    /// (<see cref="AiSystem.EndStep"/>), then committed mode: while a goal it committed to is running, only a tier above that goal's priority may take
    /// over; if none plans, the goal and its plan stay (<c>FUN_140510ef0</c> in committed mode). Not past its hours: the plan step ends a goal's plan
    /// then, and the update clears a goal left with no plan however long its commitment runs (<c>FUN_14050d970</c>). With one: no plan step, and
    /// the goal and its plan are cleared unless it commits and its commitment has not run out (<see cref="Replanned"/>). First the frame step's
    /// commitment end (<see cref="EndCommittedTask"/>).
    /// </summary>
    public TaskPriority Begin()
    {
        _mind.CommitEnded = false;
        EndCommittedTask();
        if (_replan) return Replanned();

        _ai.EndStep(_world, _slot, _id, _mind, _squad, _board, _plans, _hour);
        _committed = CommittedTo(_world, _mind, _hour);
        return _committed?.Task.Priority ?? TaskPriority.JustAction;
    }

    /// <summary>
    /// The frame step's end of a task whose commitment ran out (<c>FUN_14050f0b0</c>, <b>Verified</b>; <see cref="AiSystem.TaskCommitOver"/>): the
    /// running task ends and a replan is asked, so this choice clears the goal unless the goal's own timer still runs (<see cref="Replanned"/>), and
    /// then the stop of committed mode leaves it with no goal until its next think chooses freely. The serial looks look at it again first
    /// (<see cref="AiMind.CommitEnded"/>). For players and NPCs alike.
    /// </summary>
    void EndCommittedTask()
    {
        if (!AiSystem.TaskCommitOver(_world, _mind)) return;

        _replan = true;
        _mind.CommitEnded = true;
    }

    /// <summary>
    /// The goal committed mode keeps (<c>FUN_140510ef0</c>, <b>Verified</b>: with it, only the tiers above its priority are scored, so no lower
    /// goal's desire or finder is asked): the running goal that commits, has a plan, is in its hours and whose commitment has not run out; null
    /// for none.
    /// </summary>
    internal static AiGoal? CommittedTo(World world, AiMind mind, int hour) =>
        mind.Goal is { } running && mind.Plan.Count > 0 && TaskProfiles.Of(running.Task.Type).Commits
            && world.GameHours < mind.CommitUntil && running.InWindow(hour) ? running : null;

    /// <summary>
    /// The update's start with a replan request (<c>FUN_140511130</c>, <b>Verified</b>): the running task is stopped and the current goal cleared,
    /// unless it commits and its commitment has not run out (the goal's <c>+0x20</c> and its timer, the same test as the request's own clear,
    /// <c>FUN_14050cdc0</c>). So the choice that follows has no current goal: no x1.25 continuity, no unfinished x1.25, no plan kept, and the winner
    /// is planned afresh (a fresh target and node roll). The task is not stopped here: the winner's act-out or start replaces it, and a stop the
    /// committed goal makes picks the mover reset (<see cref="Committed"/>). The behaviour stays for the hold tests until the act-out sets it.
    /// </summary>
    TaskPriority Replanned()
    {
        var goal = _mind.Goal;
        bool kept = goal is not null && TaskProfiles.Of(goal.Task.Type).Commits && _world.GameHours < _mind.CommitUntil;
        if (!kept)
        {
            _mind.Goal = null;
            _mind.Plan.Clear();
        }

        _committed = kept ? goal : null;
        return _committed?.Task.Priority ?? TaskPriority.JustAction;
    }

    /// <summary>
    /// The pick of a committed stop: the goal the character is committed to, with its plan (null: none). After a replan request the stop picks the
    /// mover reset instead: the request stopped the committed goal's task, and the update clears a goal that runs no task after the choice
    /// (<c>FUN_140511130</c>), so the character is left with no goal until its next update chooses freely.
    /// </summary>
    public AiGoal? Committed => _replan && _committed is not null ? Mover() : _committed;

    /// <summary>
    /// Takes the look's second-list scores into their own candidates (they join the urgent pass's pick, or the priority-3 one), each by its code
    /// (<see cref="LadderPick.SecondCode"/>): the serial pass starts the one that wins.
    /// </summary>
    public void ScoreSecondList(ChooserRow? row)
    {
        _second.Clear();
        if (row is null) return;

        for (int k = 0; k < row.SecondCount; k++)
        {
            if (row.SecondScores[k] > 0) Rank(_second, null, row.SecondScores[k], 0, LadderPick.SecondCode(k));
        }
    }

    /// <summary>
    /// Whether the bound character holds job 187 in effect: it obeys its master's Slave goals (<see cref="_slaveGoals"/>).
    /// </summary>
    public bool Obeys => _slaveGoals is not null;

    /// <summary>
    /// A job pass's pick (<c>FUN_14050e5b0</c> then <c>FUN_140510650</c>): the 187 branch's goals, the master's Slave goals of priorities 1 to 3
    /// (<c>FUN_14026b790</c> mask 0xe, <b>Verified</b>), scored after the candidates still kept (the second list's, in the urgent pass); the goal
    /// planned, or null. Either way the candidates are cleared, as a failed pick clears the map.
    /// </summary>
    public AiGoal? PickJobPass(out int code)
    {
        if (_slaveGoals is { } list)
        {
            for (int k = 0; k < list.Count; k++)
            {
                if (list[k].Task.Priority is >= TaskPriority.Fluff and <= TaskPriority.Urgent) Consider(_second, list[k], tier: null);
            }
        }

        var goal = PickGoal(_second, out code);
        _second.Clear();
        return goal;
    }

    /// <summary>
    /// One tier's pick: in the original's order, which settles ties (<see cref="Rank"/>): at priority 3 the second list's candidates still kept
    /// (scored into the map before the tier, <c>FUN_140510ef0</c>), then the blackboard's requests of the tier (<see cref="AddRequests"/>), the role
    /// list, then the character's own list, where setupAI puts Self preservation before the faction's and race's goals (<c>FUN_140510d60</c>,
    /// <c>FUN_140621b70</c>, <b>Verified</b>), then the look's sources of the priority-3 goals (the decision step's step 4).
    /// </summary>
    public AiGoal? Tier(TaskPriority tier, ChooserRow? row, int from, int to, out int source)
    {
        var candidates = _candidates;
        candidates.Clear();
        if (tier == TaskPriority.Urgent) AddSecondList(candidates);
        AddRequests(candidates, tier);
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

    #region Scoring

    /// <summary>
    /// The own lists' goals of the tier: everyone's 14 Idleness first (<c>Character::setupAI</c>, priority 1, weight 0.1, <b>Verified</b>; the scorer
    /// drops it unless its list B <c>!162 !157</c> fails, so it is weighed only prone or armed; it plans only armed and standing, sheathing: prone
    /// the crippled repertoire has no 65), then for a
    /// player-faction mind only the types on the ladder's rungs or of a code mod (<see cref="AiSystem.OnRungs"/>; the rest are their sources'),
    /// then the Hold entry last (<see cref="AiSystem.HoldGoalOf"/>).
    /// </summary>
    void AddOwnLists(List<(AiGoal? Goal, float Score, float Desire, int Source)> candidates, TaskPriority tier)
    {
        Consider(candidates, AiSystem.Idle, tier);

        var own = _mind.RaceGoals;
        for (int k = 0; k < own.Count; k++)
        {
            if (_mind.Player && !_ai.OnRungs(own[k].Task.Type)) continue;
            Consider(candidates, own[k], tier);
        }

        if (_world.Characters.Cold(_slot) is { } cold && AiSystem.HoldGoalOf(cold, _mind) is { } hold) Consider(candidates, hold, tier);
    }

    /// <summary>
    /// The requests of the tier filed on the squad's blackboard for the character (<see cref="BoardRequests.For"/>: subject none or itself,
    /// <c>FUN_140269240</c>, <b>Verified</b>), scored as every goal (<c>FUN_14050df10</c> with its request flag, whose done-request delete
    /// <c>FUN_140268860</c> is empty in 1.0.68). A player-faction mind weighs them too: they are no own-list goals.
    /// </summary>
    void AddRequests(List<(AiGoal? Goal, float Score, float Desire, int Source)> candidates, TaskPriority tier)
    {
        if (_squad.Requests is not { Count: > 0 } requests) return;

        _requests.Clear();
        requests.For(tier, _id, _world.Tick, _requests);
        foreach (var goal in _requests) Consider(candidates, goal, tier);
        _requests.Clear();
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
        foreach (var (goal, score, desire, source) in _second) Rank(candidates, goal, score, desire, source);
        _second.Clear();
    }

    /// <summary>
    /// Scores goal <paramref name="g"/> into <paramref name="candidates"/> when it is of <paramref name="tier"/> (null: any) and in its hours, is
    /// wanted (or held by its commitment, <see cref="AiSystem.HeldByCommitment"/>: the scorer multiplies first and floors the current goal after,
    /// <c>FUN_14050df10</c>) and scores above 0, and is not dropped for having nothing to do.
    /// </summary>
    void Consider(List<(AiGoal? Goal, float Score, float Desire, int Source)> candidates, AiGoal g, TaskPriority? tier)
    {
        if (tier is { } t && g.Task.Priority != t || !g.InWindow(_hour)) return;

        // The list entries of Self preservation are the engine's own goal, which every character has once.
        if (g.Task.Type == TaskType.SelfPreservation && !ReferenceEquals(g, AiSystem.SelfPreservationGoal)) return;

        var profile = TaskProfiles.Of(g.Task.Type);
        float desire = _ai.Desire(_world, TaskBehaviours.Of(g.Task.Type), g.Task.Type, profile, _squad, _board, _mind, _id, g.Task.Target);
        if (desire <= 0 && !AiSystem.HeldByCommitment(_world, g, profile, _mind, _slot)) return;

        float score = _ai.Score(_world, g, profile, desire, _squad, _board, _mind, _id);
        if (score <= 0 || _plans.GivesNothingNow(g)) return;

        Rank(candidates, g, score, desire, -1);
    }

    /// <summary>
    /// The pick (<c>FUN_140510650</c>): the candidates highest first; a source or a second-list entry wins as it is (the look found it can run, its
    /// code in <paramref name="source"/>); the current goal with a step running is kept as it is (<see cref="AiSystem.Kept"/>), else the first whose
    /// plan succeeds wins.
    /// </summary>
    AiGoal? PickGoal(List<(AiGoal? Goal, float Score, float Desire, int Source)> candidates, out int source)
    {
        source = -1;
        for (int k = 0; k < candidates.Count; k++)
        {
            var (g, _, desire, from) = candidates[k];
            if (from != -1)
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
