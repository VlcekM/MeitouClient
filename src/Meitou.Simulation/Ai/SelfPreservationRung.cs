using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation.Factions;

namespace Meitou.Simulation.Ai;

/// <summary>
/// The AI's rungs of the chooser (<see cref="ITierRungs"/>) for an autonomous squad's member the AI does not hold (<see cref="SquadAutonomy"/>):
/// 32 Self preservation alone, the goal <c>Character::setupAI</c> gives every character (<c>FUN_140621b70</c>, <b>Verified</b>), scored at priority 3
/// among the look's goal sources as <see cref="TierRungs"/> scores it. No plan step, no committed stop, no second list, no default task and no
/// mover reset, and its plan is tested without being kept: the member's package goal and plan stay for when the AI holds it again. One per worker
/// thread (<see cref="Local"/>), bound to the member before each choice; it writes nothing.
/// </summary>
internal sealed class SelfPreservationRung : ITierRungs
{
    #region Binding

    /// <summary>
    /// The worker thread's rung (<see cref="Local"/>).
    /// </summary>
    [ThreadStatic] static SelfPreservationRung? s_local;

    /// <summary>
    /// The calling worker thread's rung, made on its first use.
    /// </summary>
    public static SelfPreservationRung Local => s_local ??= new SelfPreservationRung();

    /// <summary>
    /// The bound AI system.
    /// </summary>
    AiSystem _ai = null!;
    /// <summary>
    /// The bound world.
    /// </summary>
    World _world = null!;
    /// <summary>
    /// The bound member's slot.
    /// </summary>
    int _slot;
    /// <summary>
    /// The bound member's id.
    /// </summary>
    CharacterId _id;
    /// <summary>
    /// The bound member's mind.
    /// </summary>
    AiMind _mind = null!;
    /// <summary>
    /// The bound member's squad.
    /// </summary>
    Squad _squad = null!;
    /// <summary>
    /// The blackboard the bound member chooses under.
    /// </summary>
    Blackboard _board = null!;

    /// <summary>
    /// Whether the last pick's plan runs (35 Running away) rather than fights its attacker (13), as <c>SelfPreservation.Runs</c> chooses between the
    /// two providers.
    /// </summary>
    public bool Runs { get; private set; }

    /// <summary>
    /// Binds the rung to one member's choice.
    /// </summary>
    public SelfPreservationRung Bind(AiSystem ai, World world, int slot, CharacterId id, AiMind mind, Squad squad, Blackboard board)
    {
        (_ai, _world, _slot, _id, _mind, _squad, _board) = (ai, world, slot, id, mind, squad, board);
        Runs = false;
        return this;
    }

    #endregion

    #region Ladder rungs

    /// <summary>
    /// No plan step and no committed stop (the package mind is left as it is).
    /// </summary>
    public TaskPriority Begin() => TaskPriority.JustAction;

    /// <summary>
    /// Never committed.
    /// </summary>
    public AiGoal? Committed => null;

    /// <summary>
    /// The second list is no goal of this rung.
    /// </summary>
    public void ScoreSecondList(IReadOnlyList<AiGoal> list) { }

    /// <summary>
    /// No 187 branch (its job is the member's list's).
    /// </summary>
    public bool Obeys => false;

    /// <summary>
    /// No goal of a job pass.
    /// </summary>
    public AiGoal? PickJobPass() => null;

    /// <summary>
    /// The priority-3 tier: Self preservation against the look's goal sources of <paramref name="from"/> to <paramref name="to"/>, the higher score
    /// first and a tie to the source (listed after the own goals); Self preservation only when its plan has a provider (a run, or an attacker of its
    /// own to fight). Nothing at the other tiers.
    /// </summary>
    public AiGoal? Tier(TaskPriority tier, ChooserRow? row, int from, int to, out int source)
    {
        source = -1;
        if (tier != TaskPriority.Urgent) return null;

        bool wanted = Wanted(out float score);
        int best = Ladder.BestSource(row, from, to);
        if (best >= 0 && (!wanted || row!.Scores[best] >= score))
        {
            source = best;
            return null;
        }

        return wanted ? AiSystem.SelfPreservationGoal : null;
    }

    /// <summary>
    /// No default task of the AI's: the look's Idleness source stands for it.
    /// </summary>
    public AiGoal? DefaultTask() => null;

    /// <summary>
    /// No mover reset (the package plan stays): IDLE stands for no goal.
    /// </summary>
    public AiGoal Mover() => AiSystem.Idle;

    #endregion

    #region Self preservation

    /// <summary>
    /// Whether Self preservation is wanted and plans: its desire (the threat level), its score (continuity included), and one of its providers
    /// (<see cref="AiSystem.SelfPreservationPlans"/>).
    /// </summary>
    bool Wanted(out float score)
    {
        score = 0;
        var goal = AiSystem.SelfPreservationGoal;
        var type = goal.Task.Type;
        var profile = TaskProfiles.Of(type);
        float desire = _ai.Desire(_world, TaskBehaviours.Of(type), type, profile, _squad, _board, _mind, _id, goal.Task.Target);
        if (desire <= 0) return false;

        score = _ai.Score(_world, goal, profile, desire, _squad, _board, _mind, _id);
        if (score <= 0 || !_ai.SelfPreservationPlans(_world, _slot, desire, out bool runs)) return false;

        Runs = runs;
        return true;
    }

    #endregion
}
