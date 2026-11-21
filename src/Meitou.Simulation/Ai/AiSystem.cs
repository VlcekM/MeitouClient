using System.Numerics;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Ai;
using Meitou.Data.World;
using Meitou.Simulation.Aggro;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Perception;
using Meitou.Simulation.Player;
using Meitou.Simulation.Population;

namespace Meitou.Simulation.Ai;

/// <summary>
/// Settings of the <see cref="AiSystem"/>; the defaults are the game's (engine choices unless a doc label says otherwise).
/// </summary>
public sealed class AiSettings
{
    /// <summary>
    /// A character thinks every this many ticks (staggered by slot), and at once when its squad starts another package.
    /// </summary>
    public int ThinkEveryTicks { get; init; } = 15;

    /// <summary>
    /// Ticks between two package choices of a squad.
    /// </summary>
    public int PackageEveryTicks { get; init; } = 30;

    /// <summary>
    /// A character farther than this from its spot (node, bed, post) walks back to it.
    /// </summary>
    public float PostSlack { get; init; } = 12;

    /// <summary>
    /// The score of the goal being acted out is multiplied by this (<b>Observed</b>, docs/game/ai.md "Scoring"), so a near tie does not flip.
    /// </summary>
    public float Continuity { get; init; } = 1.25f;

    /// <summary>
    /// The original's <c>attacks</c> (settings.cfg "Town attacks frequency": 0 Bombardment, 1 High, 2 Normal, 3 Fewer, 4 Rare, 5 Never; default 2): a
    /// faction's wait between passing-by assaults on the player's towns (docs/game/ai.md "Package classes"); Never stops them.
    /// </summary>
    public int TownAttacks { get; set; } = 2;
}

/// <summary>
/// Starts a contract package on a squad for some game hours, aimed at <see cref="Target"/> (docs/game/ai.md "Dialogue and AI": dialogue's <c>AI
/// contract</c>, the target the conversation's partner).
/// </summary>
public sealed record StartContract(int SquadId, string PackageId, float Hours, CharacterId? Target = null) : SimCommand
{
    /// <summary>
    /// The contract's assault takes <see cref="Target"/> wherever it is, not only within the manoeuvre mode's reach (engine API for code mods, not
    /// in the original; <see cref="Blackboard.MissionAnyRange"/>).
    /// </summary>
    public bool AnyRange { get; init; }
}

/// <summary>
/// Ends a squad's running contract now, as its hours running out would (engine API for code mods, not in the original;
/// <see cref="Blackboard.EndContract"/>): the slots choose again, and a <see cref="StartContract"/> after it in the same tick starts the same
/// contract afresh. Ignored for a squad without one.
/// </summary>
public sealed record EndContract(int SquadId) : SimCommand;

/// <summary>
/// Replaces a squad's packages with one (dialogue's <c>change AI</c>).
/// </summary>
public sealed record ChangeAi(int SquadId, string PackageId) : SimCommand;

/// <summary>
/// Sends a wandering trader squad to the player town <see cref="PlayerTown"/> for a stay of <see cref="Hours"/> game hours (engine API for code
/// mods, not in the original): its next town and stay as the trader's start would pick them; the stay counts from its first step in that town
/// after the command, also when it is there already. Ignored unless the squad runs a wandering trader package, or for hours that are not finite;
/// it leaves a town that is not public at once, as any trader does.
/// </summary>
public sealed record VisitTown(int SquadId, int PlayerTown, float Hours) : SimCommand;

/// <summary>
/// The AI (docs/simulation.md "AI"; the original's in docs/game/ai.md). Every squad that is not the player's gets a <see cref="Blackboard"/> that
/// picks the package of its template's <c>AI packages</c> by slot and package class (none: empty role lists); each member thinks on its own cadence:
/// it scores the goals of its role list, its own <c>AI Goals</c> (race and faction) and the default idle goal by tier (urgent, then non-urgent, then fluff; the first tier with a positive score wins), weight, hour window,
/// crowding and continuity, acts out the winner through <see cref="TaskBehaviours"/> (in its home building's nodes and beds where it has one); its
/// combat goals are goals of the same choice (AiSystem.Fights.cs), which <see cref="AggroSystem"/>'s look asks it to weigh again when it senses a new
/// enemy. Every NPC with a mind runs the whole chooser on its turn, whatever it is doing, but fleeing, carried, down or held by its order
/// (<see cref="ThinkNpc"/>); in its own fight it chooses but acts out nothing (<see cref="InFight"/>), and over a task an engine system runs the
/// winner takes it over by that task's rules (<see cref="ActOverEngineTask"/>). Off screen a platoon's stand-in follows its package's job
/// (<see cref="PopulationSystem"/>, <see cref="PlatoonJobs"/>).
/// <list type="bullet">
/// <item><c>Think</c> (parallel): the choice and the act-out write only the character's own state (its task, goal, path flags and
/// <see cref="AiMind"/>); they read the squad's blackboard, which only the serial steps change.</item>
/// <item><c>Inputs</c> (serial): contracts. <c>SlowWorld</c> (serial): blackboards, homes and minds for new squads; on the package cadence the
/// intact test, the package choice, the patrol's progress and the crowding counts.</item>
/// </list>
/// </summary>
public sealed partial class AiSystem(PopulationData data, AiSettings? settings = null) : ITickSystem, IStateHashed, IDutyRelease
{
    #region Settings and wiring

    /// <summary>
    /// Walks to a spot in a row that may end elsewhere before it gives the spot up.
    /// </summary>
    public const int MaxFailedWalks = 3;

    /// <summary>
    /// How near a turret's gunner spot counts as arrived (units): well inside the turret's <c>use range</c>, which is 10 for the base game's turrets.
    /// </summary>
    public const float TurretSlack = 3;

    /// <summary>
    /// How near a ditch target counts as at it for the planner (units; the errand walks the rest, <see cref="Kidnap.TownDutySystem"/>).
    /// </summary>
    const float StoreSlack = 20;

    /// <summary>
    /// The live relation table (the population data's).
    /// </summary>
    FactionRelations Relations => data.Relations;

    /// <summary>
    /// The settings (the defaults when none were given).
    /// </summary>
    public AiSettings Settings { get; } = settings ?? new AiSettings();

    /// <summary>
    /// The population data the squads, towns and packages come from.
    /// </summary>
    internal PopulationData Data => data;

    /// <summary>
    /// The gate codes of the lock system (<see cref="Locks.LockSystem.Regions"/>), for the towns' waypoints; null before the serial step found it.
    /// </summary>
    internal Locks.IGateRegions? GateRegions => _locks?.Regions;

    /// <summary>
    /// The travel rows' act-out (53, 130, <see cref="TownTravel"/>).
    /// </summary>
    readonly TownTravel _travel = new(data);

    /// <summary>
    /// The owner of the NPC orders the dialogue and a building purchase give (19, 22, 67, 101, 248, 280).
    /// </summary>
    EngineOrders? _npcOrders;

    /// <summary>
    /// The owner of the NPC orders the dialogue and a building purchase give (<see cref="EngineOrders"/>).
    /// </summary>
    internal IOrderOwner NpcOrders => _npcOrders ??= new EngineOrders(this, data);

    /// <summary>
    /// The towns' alarms the investigators answer (<see cref="AiBehaviour.Investigate"/>); null: none are raised.
    /// </summary>
    public Crime.CrimeSystem? Crimes { get; set; }

    /// <summary>
    /// The squads' memory (the dialogue system's): the contract packages' update tags their mission target in it (<see cref="TagMissionTarget"/>).
    /// </summary>
    public Dialogue.SquadMemory? Memory { get; set; }

    /// <summary>
    /// The combat system, for the threats Self preservation reads (<see cref="Combat.CombatSystem.ThreatLevel"/>) and its fight's attacker (row 13);
    /// null: the world's, once the serial step has found it (none: nothing threatens anyone).
    /// </summary>
    public Combat.CombatSystem? ThreatSource { get; set; }

    /// <summary>
    /// The package-class rules, bound to the squad being updated (<see cref="Rules"/>).
    /// </summary>
    readonly Rules _rules = new();

    /// <summary>
    /// The lock system, for the gate codes (resolved in the serial step).
    /// </summary>
    Locks.LockSystem? _locks;

    /// <summary>
    /// What the code mods add (their goal and job types, docs/modding.md "Chooser goals and jobs"); null: none. Set before the world is made.
    /// </summary>
    public SimulationExtensions? Extensions { get; set; }

    /// <summary>
    /// Think periods a character weighs an engine or mod goal at 0 after its start was refused (<see cref="AiMind.Backoffs"/>).
    /// </summary>
    const int BackoffThinks = 4;

    /// <summary>
    /// The goals the engine's systems and the code mods act out in this world (<see cref="BindGoals"/>); null without any, and then no engine-goal
    /// path of the think or the serial step runs.
    /// </summary>
    DutyGoals? _duties;

    /// <summary>
    /// The goals the engine's systems and the code mods act out (<see cref="DutyGoals"/>), for those systems' serial steps; null without any.
    /// </summary>
    internal DutyGoals? Duties => _duties;

    /// <summary>
    /// The travel task of rows 53 and 130 (<see cref="TownTravel"/>), for an engine goal's act-out that carries one (192's 53).
    /// </summary>
    internal TownTravel Travel => _travel;

    /// <summary>
    /// Takes up the goals the world's systems act out (<see cref="IDutyGoalOwner"/>) and the code mods' goals their systems registered
    /// (<paramref name="mods"/>, <see cref="IChooserOwner"/>), once when the world is made (<see cref="World"/>'s constructor); nothing is built when
    /// there is none.
    /// </summary>
    internal void BindGoals(IReadOnlyList<ITickSystem> systems, IReadOnlyList<ModGoalClaim> mods)
    {
        _duties = DutyGoals.Build(systems, mods, data);
        _dialogue = systems.OfType<Dialogue.DialogueSystem>().FirstOrDefault();
    }

    /// <summary>
    /// The world's dialogue (<see cref="BindGoals"/>), whose delivered announcements state 76 reads (<see cref="MessageDelivered"/>); null without
    /// one.
    /// </summary>
    Dialogue.DialogueSystem? _dialogue;

    /// <summary>
    /// <c>isMessageDelivered</c> (state 76) of the character at <paramref name="slot"/> (<see cref="Dialogue.DialogueSystem.MessageDelivered"/>):
    /// false without a dialogue. Any phase: the dialogue's serial steps write what it reads.
    /// </summary>
    internal bool MessageDelivered(World world, int slot) => _dialogue?.MessageDelivered(world, world.Characters.IdOf(slot)) == true;

    /// <summary>
    /// An order takes the character (<see cref="IDutyRelease"/>): an engine or mod goal holding it ends (<see cref="DutyEnd.Order"/>).
    /// </summary>
    void IDutyRelease.ReleaseForOrder(World world, int slot) => _duties?.Release(world, slot, DutyEnd.Order);

    /// <summary>
    /// Whether a player-faction mind weighs goal <paramref name="type"/> of its own lists: its type is on the chooser's rungs
    /// (<see cref="Ladder.OnRungs"/>) or a code mod's registered goal type. Any phase: neither changes while the world runs.
    /// </summary>
    internal bool OnRungs(TaskType type) => Ladder.OnRungs.Contains(type) || _duties?.IsMod(type) == true;

    /// <summary>
    /// An order on the character's body (a stumble, a get-up) starts (serial, <see cref="PlayerOrders"/>): the original's AI update after it plans
    /// anew, so an engine goal holding the character ends (<see cref="DutyEnd.Replanned"/>; no duty goal commits, docs/game/ai-tasks.md "Desire and
    /// commitment").
    /// </summary>
    internal void ReplanDuty(World world, int slot) => _duties?.Release(world, slot, DutyEnd.Replanned);

    /// <summary>
    /// An order starts on a character the sources decide (serial, <see cref="OrderStep.TakeOver"/>): the engine goal its chooser picked ends, for the
    /// order (<see cref="DutyEnd.Order"/>) or, for an order on its body (<paramref name="interrupts"/>), for a replan; the order's pick beats every
    /// goal at the order step.
    /// </summary>
    internal void ReleaseChosen(World world, int slot, bool interrupts) =>
        _duties?.Release(world, slot, interrupts ? DutyEnd.Replanned : DutyEnd.Order);

    #endregion

    #region Think

    /// <summary>
    /// The blackboard a player-faction mind of a squad with none of its own chooses under: no package, no town, nothing claimed.
    /// </summary>
    internal static readonly Blackboard EmptyBoard = new([]);

    /// <summary>
    /// Every character's default goal (docs/game/ai.md "Default tasks per character", Observed): IDLE at the fluff tier, weight 0.1.
    /// </summary>
    public static readonly AiGoal Idle =
        new(new AiTask("", "IDLE", TaskType.Idle, TaskPriority.Fluff, TaskTargetType.Specific, TaskEndEvent.Nothing), 0, 100, 0.1f);

    /// <summary>
    /// Every character's other default goal (<c>Character::setupAI</c>, <b>Verified</b>; docs/game/ai-tasks.md "Self preservation"):
    /// SELF_PRESERVATION, urgent, aimed at itself, weight 1, all hours. The entries of the role and race lists (30 races list it) are the same goal
    /// and are left out of the scoring, so it counts once.
    /// </summary>
    public static readonly AiGoal SelfPreservationGoal =
        new(new AiTask("", "SELF_PRESERVATION", TaskType.SelfPreservation, TaskPriority.Urgent, TaskTargetType.Self, TaskEndEvent.Nothing),
            0, 100, 1f);

    /// <summary>
    /// Set by a fighter's think that chose a goal that is no fight (AiMind.LeaveFight).
    /// </summary>
    int _leaving;

    /// <summary>
    /// Every character's <c>followMe</c>, stamped in the schedule step for the think to read (<see cref="FollowDesire"/>).
    /// </summary>
    readonly FollowMe _followMe = new();

    /// <summary>
    /// The serial schedule step: drops the blackboards' lapsed requests (<see cref="BoardRequests"/>), stamps every character's <c>followMe</c>
    /// before the parallel think, gives every member of a player's squad that lacks one its player-faction mind (<see cref="PlayerSideMinds"/>) and
    /// every new member of a squad with a board its mind (<see cref="NewMinds"/>).
    /// </summary>
    public void Schedule(World world)
    {
        BoardRequests.LapseAll(world);
        _followMe.Stamp(world);
        PlayerSideMinds(world);
        NewMinds(world);
    }

    /// <summary>
    /// The parallel think: each character with a mind, on its turn or when asked (its look's <see cref="ChooserRow.Due"/>, its replan bit, an order
    /// that held it until now), runs the chooser (<see cref="ThinkOne"/>; writes its own slot only), told whether the original's replan request is
    /// pending (<see cref="AiMind.ReplanAsked"/>, used up here as the original's update uses <c>+0x1b0</c>, but not while no update runs).
    /// </summary>
    public void Think(World world, Partition part)
    {
        var table = world.Characters;
        var prev = table.Previous;
        int every = world.Every(Settings.ThinkEveryTicks);
        int hour = world.Hour;
        for (int i = part.Start; i < part.End; i++)
        {
            if (!prev[i].Alive || prev[i].Dormant || !world.Steps(i) || table.Cold(i) is not { Ai: { Squad: var squad } mind } cold
                || BoardOf(mind, squad) is not { } board)
                continue;

            // Cheapest test first: most characters are not due this tick (a far character's step covers several ticks: due when its turn fell in
            // them, as World.Turn; the think's turn is anchored at Tick + slot, the sweeps' at Tick - slot, so a slot's aggro look and its think
            // interleave). One whose order held it until now thinks at once (DecisionSystem.Ordered: its queue may have emptied since).
            var row = world.Decisions.RowOf(world, i);
            if (mind.PlayerSide && row is not { Due: true }) continue;   // its chooser runs with its look only

            bool asked = mind.Replan || cold.OrderWins || row is { Due: true };
            if ((world.Tick + i) % every >= world.StepTicks(i) && mind.Version == board.Version && !asked) continue;

            // The original's update skips the AI while the character is carried or down (FUN_1405d00b0), so its replan request waits for the first
            // update after it.
            bool down = DecisionSystem.Down(cold);
            bool replan = mind.ReplanAsked && !down;
            mind.Replan = false;
            if (!down) mind.ReplanAsked = false;
            if (row is not null) row.Thought = true;
            ThinkOne(world, i, cold, mind, squad, board, row, hour, replan);
        }
    }

    /// <summary>
    /// The blackboard a mind chooses under: a player-faction mind's its squad's while that squad is autonomous and has one
    /// (<see cref="PlayerSideBoard"/>), any other its squad's (null before it has one).
    /// </summary>
    static Blackboard? BoardOf(AiMind mind, Squad squad) => mind.PlayerSide ? PlayerSideBoard(squad) : squad.Ai;

    /// <summary>
    /// The blackboard a player-faction mind chooses under: an autonomous squad's own (its package, home and town; Meitou only,
    /// <see cref="SquadAutonomy"/>), else the empty one (the player's platoon runs no package).
    /// </summary>
    internal static Blackboard PlayerSideBoard(Squad squad) => squad is { Autonomous: true, Ai: { } board } ? board : EmptyBoard;

    /// <summary>
    /// The role list a player-faction mind's tiers score: its autonomous squad's package list for its role (<see cref="PlayerSideBoard"/>; none for
    /// any other player squad). An obeying slave's master's Slave list is the job passes' (<see cref="ObeyedRoles"/>).
    /// </summary>
    internal static IReadOnlyList<AiGoal> PlayerSideRoles(Squad squad, CharacterId id, CharacterCold cold) =>
        PlayerSideBoard(squad).Roles[(int)RoleOf(squad, id, cold)];

    /// <summary>
    /// One character's chooser (<see cref="Ladder"/>), one ladder per think: first the order step for everyone, whose pick (an order that runs or can
    /// start) ends it; then a player-faction mind's ladder with the sources (<see cref="ThinkPlayer"/>; an autonomous squad's member too, its roles
    /// its package's), or an NPC's (<see cref="ThinkNpc"/>).
    /// </summary>
    void ThinkOne(World world, int i, CharacterCold cold, AiMind mind, Squad squad, Blackboard board, ChooserRow? row, int hour, bool replan)
    {
        if (world.Decisions.OrderStep(row) is { } order)
        {
            row!.Pick = order;

            // No continuity for its goal at the next choice; an engine goal holding it ends at the order's start (OrderStep.TakeOver).
            if (mind.PlayerSide && mind.Duty is null) LeaveGoal(cold, mind);
            return;
        }

        var id = world.Characters.IdOf(i);
        if (mind.PlayerSide)
        {
            ThinkPlayer(world, i, id, cold, mind, squad, board, row, hour, replan);
            return;
        }

        var roles = board.Roles[(int)RoleOf(squad, id, cold)];
        ThinkNpc(world, i, id, cold, mind, squad, board, roles, hour, row, replan);
    }

    /// <summary>
    /// The ladder of a player-faction character (<see cref="Ladder.Run"/> with its look's sources and the AI's rungs: its autonomous squad's package
    /// roles, else none, <see cref="PlayerSideRoles"/>; the own lists' types on the rungs or of a code mod, 32): a source's pick is started in the serial pass and
    /// ends the AI's goal (<see cref="LeaveChoice"/>); a goal's is acted out as an NPC's is (<see cref="ActChosen"/>) while nothing else holds the
    /// character (<see cref="PlayerSideHold.Free"/>), else only Self preservation acts (<see cref="ActSelfPreservation"/>). The default task and
    /// the mover reset leave its task as it is (the Idleness source stands for them); own-list 14 with a plan (sheathing) is acted out. None while it
    /// is down (no chooser runs) or without this tick's look.
    /// </summary>
    void ThinkPlayer(World world, int i, CharacterId id, CharacterCold cold, AiMind mind, Squad squad, Blackboard board, ChooserRow? row, int hour,
        bool replan)
    {
        if (DecisionSystem.Down(cold) || row is null) return;

        var roles = PlayerSideRoles(squad, id, cold);
        var tiers = TierRungs.Local.Bind(this, world, i, id, mind, squad, board, roles, hour, replan);
        var pick = Ladder.Run(world.Decisions.Steps, row, tiers, cold.Jobs.Count > 0);
        row.Pick = pick;
        if (pick.Goal is not { } goal || ReferenceEquals(goal, Idle) && !StepsBeforeIdle(mind)) LeaveChoice(cold, mind);
        else if (PlayerSideHold.Free(cold)) ActChosen(world, i, cold, mind, goal, board, row);
        else ActSelfPreservation(world, i, cold, mind, goal, board);
    }

    /// <summary>
    /// The mind's plan for 14 Idleness runs a step before its own row (7 Sheathing weapon): the default task and the mover reset plan none.
    /// </summary>
    static bool StepsBeforeIdle(AiMind mind) => mind.Plan.Count > 0 && mind.Plan[0].Type != TaskType.Idle;

    /// <summary>
    /// A source of the look won over a player-faction mind's goal, or nothing did: the engine goal holding it ends in the serial step of the same
    /// tick (<see cref="DutyGoals.Leave"/>: its act-out stops, before the serial pass starts the source), and the goal is no longer the current one
    /// (no continuity at the next choice).
    /// </summary>
    void LeaveChoice(CharacterCold cold, AiMind mind)
    {
        if (mind.Duty is not null) _duties!.Leave(mind);
        LeaveGoal(cold, mind);
    }

    /// <summary>
    /// The goal is no longer the mind's current one: its work stops (<see cref="StopWork"/>), goal, plan and behaviour cleared.
    /// </summary>
    static void LeaveGoal(CharacterCold cold, AiMind mind)
    {
        if (mind.WorkActive) StopWork(cold, mind);
        if (mind.Goal is null) return;

        mind.Goal = null;
        mind.Plan.Clear();
        mind.Behaviour = AiBehaviour.None;
    }

    /// <summary>
    /// The goal the ladder picked for a character the AI holds or a player-faction character, acted out on the one path (as the original's chooser
    /// installs a goal whoever the character belongs to): a winner over the engine goal holding it ends that goal in the serial step and is acted
    /// out at the next tick; in a fight the fight goes on or ends (<see cref="InFight"/>); else its act-out (<see cref="Act"/>: the behaviour's task,
    /// an engine goal's start asked of its system, a fight or flight asked of the serial steps). Writes only its own slot.
    /// </summary>
    void ActChosen(World world, int i, CharacterCold cold, AiMind mind, AiGoal goal, Blackboard board, ChooserRow? row)
    {
        if (row is not null && world.Decisions.Tracing) row.Winner = GoalName(goal);

        if (mind.Duty is { } duty && !DutyGoals.Same(duty, goal)) _duties!.Leave(mind);
        else if (cold.InCombat) InFight(world, i, mind, goal, board);
        else Act(world, i, mind, goal, board);
    }

    /// <summary>
    /// A player-faction character something else holds (a flight, a conversation, a cage, <see cref="PlayerSideHold.Free"/>): only 32 Self preservation
    /// acts, its fight on its attacker (row 13, <see cref="AskFight"/>; the fight's start replaces the task) or its run; in a fight the fight goes
    /// on. Any other goal is left (an engine goal holding it ends by the sweep: no goal may hold it).
    /// </summary>
    void ActSelfPreservation(World world, int i, CharacterCold cold, AiMind mind, AiGoal goal, Blackboard board)
    {
        if (!ReferenceEquals(goal, SelfPreservationGoal))
        {
            LeaveGoal(cold, mind);
            return;
        }

        mind.Goal = goal;
        mind.Behaviour = AiBehaviour.SelfPreserve;
        mind.Version = board.Version;
        if (cold.InCombat) return;   // its fight goes on

        if (mind.Step == TaskType.RunAway) mind.FleeRequest = FleeAsk.Threat;
        else if (mind.Step != TaskType.UseTurret) AskFight(world, i, TaskType.SelfPreservation, mind);
    }

    /// <summary>
    /// A goal's trace name: its number and TaskData text ("32 Self preservation").
    /// </summary>
    internal static string GoalName(AiGoal goal) => $"{(int)goal.Task.Type} {TaskDataTable.Of(goal.Task.Type)?.Text ?? goal.Task.Name}";

    /// <summary>
    /// An NPC's think after the order step: the full ladder (<see cref="ChooseOnLadder"/>) whatever task it is on, as the original's chooser runs on
    /// every AI update of a character with a platoon (<c>FUN_1405112b0</c>, <b>Verified</b>), but while no chooser runs (<see cref="ChooserRuns"/>).
    /// The winner is acted out as the AI's (<see cref="ActChosen"/>) on a task the AI runs, from the cage (<see cref="ActCaged"/>), or over a task an
    /// engine system runs (<see cref="ActOverEngineTask"/>).
    /// </summary>
    void ThinkNpc(World world, int i, CharacterId id, CharacterCold cold, AiMind mind, Squad squad, Blackboard board, IReadOnlyList<AiGoal> roles,
        int hour, ChooserRow? row, bool replan)
    {
        ref var n = ref world.Characters.Next[i];
        bool caged = ThinksCaged(cold);
        if (!caged && !ChooserRuns(cold)) return;

        // Whether the AI runs its task, asked before the ladder: a replan request's clear changes the goal the hold reads.
        bool held = !caged && (Holds(n, cold) || RunsListJob(n, cold));
        if (!held) KeepPlan(mind);

        var pick = ChooseOnLadder(world, i, id, cold, mind, squad, board, roles, hour, row, replan);
        if (pick.Goal is not { } goal)
        {
            if (pick.Source >= 0 || pick.Second >= 0) TakeSource(cold, mind, row, pick, !caged && (held || TakenOver(n, cold)));
            if (!held) RestorePlan(mind);
            return;
        }

        if (held) ActChosen(world, i, cold, mind, goal, board, row);
        else if (caged ? !ActCaged(world, i, mind, goal, board) : !ActOverEngineTask(world, i, cold, mind, goal, board, row)) RestorePlan(mind);
    }

    /// <summary>
    /// Keeps the plan of the current goal before the ladder of a character the AI does not hold: the ladder plans its winner into the mind
    /// (<see cref="PlanContext.TryPlan"/>), and a winner not acted out (<see cref="ActOverEngineTask"/>, <see cref="ActCaged"/>) must not leave its
    /// plan under the goal that goes on.
    /// </summary>
    static void KeepPlan(AiMind mind)
    {
        var kept = PlanScratch;
        kept.Clear();
        kept.AddRange(mind.Plan);
    }

    /// <summary>
    /// Puts back the plan <see cref="KeepPlan"/> kept: the winner was not taken up. A goal a replan request cleared (<see cref="TierRungs.Begin"/>)
    /// keeps no plan.
    /// </summary>
    static void RestorePlan(AiMind mind)
    {
        mind.Plan.Clear();
        if (mind.Goal is not null) mind.Plan.AddRange(PlanScratch);
    }

    /// <summary>
    /// Whether an NPC's chooser runs out of a cage: not while carried, knocked out or dead (<c>FUN_1405d00b0</c>, <b>Verified</b>); Meitou: not while
    /// its order holds it (the order step picked it). A runner's chooser runs: its run is its goal's task (<see cref="FleeSystem"/>).
    /// </summary>
    static bool ChooserRuns(CharacterCold cold) =>
        !cold.IsImprisoned && cold.CarriedBy.IsNone && cold.Medical is not { Incapacitated: true } && !DecisionSystem.Ordered(cold);

    /// <summary>
    /// The full ladder for a mind (<see cref="Ladder.Run"/> with its look's sources and the AI's rungs), after the back-offs that ran out are pruned; the
    /// pick is the row's for the serial pass.
    /// </summary>
    LadderPick ChooseOnLadder(World world, int i, CharacterId id, CharacterCold cold, AiMind mind, Squad squad, Blackboard board,
        IReadOnlyList<AiGoal> roles, int hour, ChooserRow? row, bool replan)
    {
        if (mind.Backoffs is not null) mind.PruneBackoff(world.Tick);

        var tiers = TierRungs.Local.Bind(this, world, i, id, mind, squad, board, roles, hour, replan);
        var pick = Ladder.Run(world.Decisions.Steps, row, tiers, cold.Jobs.Count > 0);
        if (row is not null) row.Pick = pick;
        return pick;
    }

    /// <summary>
    /// A source's or a second-list entry's pick: with <paramref name="takes"/> the serial pass starts it, and after a source's the goal is no longer
    /// the current one (the original's update clears a goal that runs no task after the choice, <c>FUN_140511130</c>); else it starts nothing and the
    /// task goes on: an engine's task (<see cref="TakenOver"/>), or the cage, where the start would bypass the cage's repertoire.
    /// </summary>
    void TakeSource(CharacterCold cold, AiMind mind, ChooserRow? row, LadderPick pick, bool takes)
    {
        if (!takes)
        {
            if (row is not null) row.Pick = LadderPick.None(0);
            return;
        }

        if (pick.Source >= 0) LeaveChoice(cold, mind);
    }

    /// <summary>
    /// Whether any winner of the ladder may take over the task an engine system runs for the NPC: a treatment out of a fight (a medic job's at its
    /// rung, or the act-out of its Medic, splint or Fixing Legs goal). The engine's other tasks (a fight it did not start, a crime's, a lift, a
    /// dialogue's, an errand's walk) stand for an order or a goal Meitou does not put on a rung, so only a combat goal or Self preservation takes them
    /// over (<see cref="ActOverEngineTask"/>).
    /// </summary>
    static bool TakenOver(in CharacterHot n, CharacterCold cold) => !cold.InCombat && n.Task == (byte)CharacterTask.Treat;

    /// <summary>
    /// The winner over a task an engine system runs (the AI does not hold the NPC, <see cref="Holds"/>). In a fight it did not start: a combat goal
    /// or Self preservation becomes the fight's goal (<see cref="InFight"/>: the fight goes on, its run asks the flee step); any other winner leaves
    /// the fight to its starter. Elsewhere: a combat goal's fight or a run is asked of the serial steps, whose start replaces the task
    /// (<see cref="ActOverTask"/>), a run's goal taken up first (<see cref="Install"/>); the goal whose act-out runs goes on; on its own Fixing Legs treatment a goal below the urgent tier leaves it (the
    /// treatment runs until the leg is bandaged, <b>Observed</b>); another winner over a treatment ends it in the serial step and acts at the next
    /// tick (<see cref="LeaveTask"/>); over the engine's other tasks it waits (<see cref="TakenOver"/>). Whether the winner was taken up as the
    /// current goal (its plan stays).
    /// </summary>
    bool ActOverEngineTask(World world, int i, CharacterCold cold, AiMind mind, AiGoal goal, Blackboard board, ChooserRow? row)
    {
        if (row is not null && world.Decisions.Tracing) row.Winner = GoalName(goal);

        var type = goal.Task.Type;
        var behaviour = TaskBehaviours.Of(type);
        bool fights = behaviour is AiBehaviour.Fight or AiBehaviour.SelfPreserve or AiBehaviour.Flee;
        if (cold.InCombat)
        {
            if (fights) InFight(world, i, mind, goal, board);
            return fights;
        }

        // A run is its goal's task from its first tick (FleeSystem.FlightGoes), so the goal becomes current first, as the chooser installs it with its
        // plan's task; the run's start replaces the engine's task as any start does.
        if (fights)
        {
            bool runs = AsksRun(mind, behaviour);
            if (runs) behaviour = Install(world, i, mind, goal, board, type, behaviour);
            ActOverTask(world, i, mind, type, behaviour);
            return runs;
        }

        if (mind.Goal is { } current && DutyGoals.Same(current, goal)) return true;

        ref var n = ref world.Characters.Next[i];
        if (TakenOver(n, cold) && !(TreatsOwnLegs(n, cold) && goal.Task.Priority < TaskPriority.Urgent)) LeaveTask(mind);
        return false;
    }

    /// <summary>
    /// The winner of an NPC in its cage (<see cref="CagedMind"/>; the cage's repertoire limits what plans, <see cref="PlanContext.TryPlan"/>): acted
    /// out only when a system acts it out from the cage (an engine goal); with any other winner it stays held with no goal, as the cage leaves it
    /// nothing to do. A winner over the engine goal holding it ends that goal in the serial step. Writes only its own slot. Whether the winner was
    /// taken up (or the goal cleared).
    /// </summary>
    bool ActCaged(World world, int slot, AiMind mind, AiGoal goal, Blackboard board)
    {
        bool duty = _duties!.Has(goal.Task.Type);
        if (mind.Duty is { } held && (!duty || !DutyGoals.Same(held, goal)))
        {
            _duties.Leave(mind);
            return false;
        }

        if (duty) Act(world, slot, mind, goal, board);
        else LieOnWithNoGoal(mind, board);
        return true;
    }

    /// <summary>
    /// Whether the NPC runs its list job's task (<see cref="TaskSource.Job"/>) out of a fight and free (<see cref="PlayerSideHold.Free"/>, no order
    /// holding it): the chooser runs over it as over any task (<c>FUN_140510ef0</c> has no faction or task test), so a priority-3 goal or a higher
    /// job takes over, and a goal that wins ends the job's task first (<see cref="ActOverTask"/>). A pure read of its own state.
    /// </summary>
    internal static bool RunsListJob(in CharacterHot n, CharacterCold cold) => FreeForJobs(cold) && OnJobTask(n, cold);

    /// <summary>
    /// Out of a fight, free (<see cref="PlayerSideHold.Free"/>) and held by no order.
    /// </summary>
    static bool FreeForJobs(CharacterCold cold) => !cold.InCombat && PlayerSideHold.Free(cold) && !DecisionSystem.Ordered(cold);

    /// <summary>
    /// Whether the character's task is a job's (list or implicit) that its provenance still runs.
    /// </summary>
    static bool OnJobTask(in CharacterHot n, CharacterCold cold) => cold.Running is { Source: TaskSource.Job } run && run.Runs((CharacterTask)n.Task);

    /// <summary>
    /// Whether the character's chooser runs in its cage (<see cref="CagedMind"/>) while systems act engine goals out (<see cref="DutyGoals"/>).
    /// </summary>
    bool ThinksCaged(CharacterCold cold) => _duties is not null && CagedMind(cold);

    /// <summary>
    /// An NPC held in a cage whose chooser runs (docs/game/ai-tasks.md "Leaving the night cage", <b>Verified</b>: the cage sets the imprisoned or
    /// chained repertoire, so only the rows registered for it plan): a full mind of its own, awake, not carried and held by no order.
    /// </summary>
    internal static bool CagedMind(CharacterCold cold) =>
        cold is { IsImprisoned: true, IsPlayer: false, Ai.Player: false }
        && cold.CarriedBy.IsNone && cold.Medical is not { Incapacitated: true } && !DecisionSystem.Ordered(cold);

    /// <summary>
    /// Whether an engine goal may hold the character (<see cref="DutyGoals"/>): the AI runs it (<see cref="Holds"/>), or it thinks in its cage
    /// (<see cref="CagedMind"/>) on no task but the act-out's (<see cref="OnDuty"/>), so a goal acted out from the cage (getting out, its shackles)
    /// starts and holds there.
    /// </summary>
    internal static bool MayHoldDuty(in CharacterHot n, CharacterCold cold) =>
        Holds(n, cold) || CagedMind(cold) && (cold.Ai!.Duty is null || OnDuty(cold, cold.Ai));

    /// <summary>
    /// The choice of a character in its own fight (<see cref="OwnsFight"/>): a combat goal or Self preservation's fight (row 13) keeps the fight
    /// running (the fight is its plan's task, already queued: no new start, whichever of them is current now; the combat system re-targets); Self
    /// preservation's run (35) or a goal 35 asks the flee step, which stops the fight (a new goal 35 rolls its commitment); any other goal, Self
    /// preservation's turret (146) among them, ends the fight in the serial step, and the character thinks again at the next tick to act it out (docs/game/ai-tasks.md "Combat goals"). Writes only
    /// its own mind.
    /// </summary>
    void InFight(World world, int slot, AiMind mind, AiGoal goal, Blackboard board)
    {
        var behaviour = TaskBehaviours.Of(goal.Task.Type);
        bool guards = behaviour == AiBehaviour.SelfPreserve && mind.Step == TaskType.UseTurret;
        if (guards || behaviour is not (AiBehaviour.Fight or AiBehaviour.SelfPreserve or AiBehaviour.Flee))
        {
            mind.LeaveFight = true;
            Interlocked.Exchange(ref _leaving, 1);
            return;
        }

        CommitInFight(world, slot, mind, goal);
        mind.Goal = goal;
        mind.Behaviour = behaviour;
        mind.Version = board.Version;
        if (AsksRun(mind, behaviour))
            mind.FleeRequest = behaviour == AiBehaviour.SelfPreserve ? FleeAsk.Threat : FleeAsk.Enemies;
    }

    /// <summary>
    /// Whether the character is on the treatment of itself that its Fixing Legs goal started (row 269, <see cref="AiBehaviour.HealLegs"/>).
    /// </summary>
    internal static bool TreatsOwnLegs(in CharacterHot n, CharacterCold cold) =>
        n.Task == (byte)CharacterTask.Treat && cold.Running is { Task: CharacterTask.Treat, Row: TaskType.HealMyLegs };

    /// <summary>
    /// Whether the character's provenance is the act-out of the engine goal holding it (<see cref="AiMind.Duty"/>): no task, the task its start left
    /// running (<see cref="AiMind.DutyTick"/>), or a goal's task (<see cref="TaskSource.Goal"/>) started since, which only the act-out starts while the
    /// goal holds the character. Any other start since (an engine reaction, an NPC engine start, an order) took the character over.
    /// </summary>
    internal static bool OnDuty(CharacterCold cold, AiMind mind) =>
        cold.Running is not { } run || run.Tick == mind.DutyTick || run.Tick > mind.DutyTick && run.Source == TaskSource.Goal;

    /// <summary>
    /// Whether the character's fight is the AI's own: the one the serial step started for its combat goal (<see cref="AiMind.FightFrom"/>), still
    /// running. A fight the combat system or another system started (a self-defence, a duty's, a dialogue's, an assault's) is not.
    /// </summary>
    internal static bool OwnsFight(CharacterCold cold, AiMind mind) =>
        mind.FightFrom != 0 && cold.Running is { Task: CharacterTask.Attack } run && run.Tick == mind.FightFrom;

    /// <summary>
    /// The HUD's Goal text: the goal's name while the AI holds the character; "Running away" (35's name, <c>Task_Runaway</c>) for a character on the
    /// flee step, whichever of Self preservation, a goal 35 or a broken squad started it, rather than its walk's "Moving"; "Stumbling" while a stagger's
    /// order 147 runs its stumble (<see cref="Combat.BodyOrders"/>); "Fixing Legs" on its own Fixing Legs treatment (<see cref="TreatsOwnLegs"/>);
    /// null for the rest (the HUD then names the movement task).
    /// </summary>
    public static string? ActivityText(in CharacterHot n, CharacterCold cold, long tick = 0) =>
        Combat.BodyOrders.Stumbles(cold, tick) ? TaskBehaviours.Text(TaskType.StumbleTaskForced)
        : cold is { Fleeing: true, Medical.Incapacitated: not true } ? TaskBehaviours.Text(TaskType.RunAway)
        : cold.Ai is not null && TreatsOwnLegs(n, cold) ? TaskBehaviours.Text(TaskType.HealMyLegs)
        : cold.Ai is { Goal: { } goal } && Holds(n, cold) ? GoalText(goal) : null;

    /// <summary>
    /// A goal's HUD text: the TaskData display text (<see cref="TaskBehaviours.Text"/>), or for a code mod's type (from 1000 up) its AI_TASK
    /// record's name.
    /// </summary>
    static string GoalText(AiGoal goal) =>
        (int)goal.Task.Type >= SimulationExtensions.FirstModCode && goal.Task.Name.Length > 0 ? goal.Task.Name : TaskBehaviours.Text(goal.Task.Type);

    /// <summary>
    /// Whether the AI runs the character now: it has a mind and nothing else holds it (a fight it did not start, a flight its goal did not start, an
    /// order, being carried, a trade, a knock-out). In its own fight it thinks as anyone does (on the cadence and the replan triggers) and may leave
    /// it for another goal (<see cref="InFight"/>); on its own flight too (<see cref="RunsOwnFlight"/>). An NPC's order that runs holds it
    /// (<see cref="DecisionSystem.Ordered"/>); one that is blocked does not. An NPC carrying a body is held as anyone outside a fight, and an engine
    /// goal's act-out (<see cref="AiMind.Duty"/>) is the AI's own: it runs the character while that act-out runs. A player-faction mind
    /// (<see cref="AiMind.PlayerSide"/>) is held only by the goal its chooser picked, on a task that is the AI's own (<see cref="PlayerSideHold.Holds"/>):
    /// a source's pick, an order's task or a job's is not the AI's.
    /// </summary>
    public static bool Holds(in CharacterHot n, CharacterCold cold)
    {
        if (cold.Ai is not { } mind || !PlayerSideHold.Free(cold) || DecisionSystem.Ordered(cold)) return false;
        if (cold.Fleeing && !RunsOwnFlight(cold, mind)) return false;   // a broken squad's run: the engine's
        if (mind.PlayerSide) return PlayerSideHold.Holds(n, cold, mind);

        // An engine goal holds it (DutyGoals): the AI runs it while its act-out runs, carrying included (the act-out's own body), not in a fight.
        if (mind.Duty is not null) return !cold.InCombat && OnDuty(cold, mind);

        // An NPC carrying a body is the AI's as anyone is (only the carried one loses its chooser, docs/game/ai-tasks.md "Precedence"): a goal
        // that needs empty hands (list B !47) puts the body down first (plan step 69); not in a fight.
        if (cold.InCombat) return cold.Carrying.IsNone && n.Task == (byte)CharacterTask.Attack && OwnsFight(cold, mind);
        return (CharacterTask)n.Task switch
        {
            CharacterTask.Idle or CharacterTask.Wander or CharacterTask.Follow => true,
            // The walks to its spots, and lying or sitting there, are the AI's own, and so is the walk of its plan's ditch step.
            CharacterTask.GoTo => mind.Behaviour is AiBehaviour.Post or AiBehaviour.Sleep or AiBehaviour.Sit or AiBehaviour.GoHome
                or AiBehaviour.Patrol or AiBehaviour.Turret or AiBehaviour.TownNode or AiBehaviour.Shop or AiBehaviour.Relax
                or AiBehaviour.ToTown or AiBehaviour.Travel or AiBehaviour.Work or AiBehaviour.Investigate
                or AiBehaviour.PatrolTown || RunsDitchStep(cold) || RunsOwnFlight(cold, mind),
            CharacterTask.Rest => mind.Behaviour is AiBehaviour.Sleep or AiBehaviour.Sit or AiBehaviour.Relax,
            // In the bed it went to (or lying in one when loaded, before its first think, or laid in by a carrier while it was down: no task of
            // its own keeps it there, and its goal of before then is stale), its think lets it lie on or gets it out (Act).
            CharacterTask.Sleep => cold.InSomething == Locks.HeldState.Bed
                && (mind.Behaviour == AiBehaviour.Sleep || mind.Goal is null || cold.Running?.Runs(CharacterTask.Sleep) != true),
            _ => false,
        };
    }

    /// <summary>
    /// Whether the character runs the flight its goal started (<see cref="FleeSystem"/>: row 35 of a goal's provenance, under 32 or a goal 35).
    /// </summary>
    internal static bool RunsOwnFlight(CharacterCold cold, AiMind mind) =>
        cold is { Fleeing: true, Running: { Source: TaskSource.Goal, Row: TaskType.RunAway } }
        && mind is { Goal: not null, Behaviour: AiBehaviour.SelfPreserve or AiBehaviour.Flee };

    /// <summary>
    /// Job 187 Obedient Slave (<c>FUN_14050e5b0</c>, docs/game/ai-tasks.md "Obedient Slave"): a slave (state 1) not sneaking, the jobs switch on,
    /// that holds the job (a player character's list, <see cref="Kidnap.ObedientSlave.HasJob"/>; an NPC holds it in effect, Meitou keeping no
    /// perma-job list for it, <see cref="CharacterCold.ObedienceLost"/> standing for its removal by a split) has its master squad's
    /// <c>Slave AI Goals</c> as the 187 branch's goals, which the chooser's job passes score before its own tiers (<see cref="TierRungs.PickJobPass"/>),
    /// whatever its squad role: a guard enslaved by its own squad stops guarding while a Slave goal plans (#991). The engine goals' looks and the
    /// seat claims take it as the list the slave plans with. Null: no master squad, an empty Slave list, or no job.
    /// </summary>
    internal static IReadOnlyList<AiGoal>? ObeyedRoles(World world, Squad squad, CharacterCold cold) =>
        HoldsObedience(cold) && Kidnap.ObedientSlave.Obeys(cold) && (cold.Orders & CharacterOrders.Jobs) != 0 && cold.SlaveMaster >= 0
            ? Kidnap.ObedientSlave.SlaveList(world.Squads.Find(cold.SlaveMaster)) : null;

    /// <summary>
    /// Whether the character holds job 187: a player character's list has it, an NPC's was not split off (<see cref="ObeyedRoles"/>).
    /// </summary>
    static bool HoldsObedience(CharacterCold cold) => cold.IsPlayer ? Kidnap.ObedientSlave.HasJob(cold) : !cold.ObedienceLost;

    /// <summary>
    /// The role list a member takes (docs/game/ai.md "Goal loading"): the squad leader the leader's, else by its squad role. Public for code mods; a
    /// pure read of the squad's leader and the character's role, safe in any phase.
    /// </summary>
    public static AiRole RoleOf(Squad squad, CharacterId id, CharacterCold cold) =>
        squad.Leader == id || cold.Role == (int)SquadRole.Leader ? AiRole.Leader
        : cold.Role == (int)SquadRole.Squad2 ? AiRole.Squad2
        : cold.Role == (int)SquadRole.Slave ? AiRole.Slave
        : AiRole.Squad;

    /// <summary>
    /// Whether the chooser weighs goal <paramref name="type"/> for the character at <paramref name="slot"/> now: its role's list of its squad's
    /// running package, or the slave list of the squad it obeys, or its race's <c>AI Goals</c> hold the type in the hour's window. False without a
    /// mind or a squad blackboard. A player-faction mind (<see cref="AiMind.PlayerSide"/>) weighs a type of its own lists that is on the chooser's
    /// rungs or of a code mod's type (<see cref="OnRungs"/>), its autonomous squad's package list (<see cref="PlayerSideRoles"/>) and, obeying, its
    /// master's Slave list (<see cref="ObeyedRoles"/>). For code mods (docs/modding.md "Chooser goals and jobs"): whether a goal of theirs is still
    /// weighed for a character they act it out for (unlike <see cref="ListsGoal"/>, the hour's window counts). Serial phases, or the character's own
    /// partition.
    /// </summary>
    public static bool WeighsGoal(World world, int slot, TaskType type)
    {
        if (world.Characters.Cold(slot) is { Ai: { PlayerSide: true } side } player) return PlayerSideWeighs(world, slot, player, side, type);
        if (world.Characters.Cold(slot) is not { Ai: { Squad: var squad, Player: false } mind } cold) return false;

        if (squad.Ai is not { } board) return false;

        int hour = world.Hour;

        var roles = ObeyedRoles(world, squad, cold) ?? board.Roles[(int)RoleOf(squad, world.Characters.IdOf(slot), cold)];
        return Lists(roles, type, hour) || Lists(mind.RaceGoals, type, hour);
    }

    /// <summary>
    /// <see cref="WeighsGoal"/> for a player-faction mind: a type its master's Slave list holds while it obeys, its autonomous squad's package list
    /// for its role, or one on the chooser's rungs or of a code mod's type that its own lists hold, in the hour's window.
    /// </summary>
    static bool PlayerSideWeighs(World world, int slot, CharacterCold cold, AiMind mind, TaskType type)
    {
        int hour = world.Hour;
        if (Lists(ObeyedRoles(world, mind.Squad, cold), type, hour)) return true;
        if (Lists(PlayerSideRoles(mind.Squad, world.Characters.IdOf(slot), cold), type, hour)) return true;

        return world.SystemOf<AiSystem>()?.OnRungs(type) == true && Lists(mind.RaceGoals, type, hour);
    }

    /// <summary>
    /// Whether <paramref name="goals"/> hold <paramref name="type"/> in <paramref name="hour"/>'s window.
    /// </summary>
    static bool Lists(IReadOnlyList<AiGoal>? goals, TaskType type, int hour)
    {
        if (goals is null) return false;

        for (int k = 0; k < goals.Count; k++)
        {
            if (goals[k].Task.Type == type && goals[k].InWindow(hour)) return true;
        }

        return false;
    }

    #endregion

    #region Choosing a goal

    /// <summary>
    /// The factor on the followed character's <c>followMe</c> in the following desire (<c>DAT_14167e8d8</c>, <see cref="FollowDesire"/>).
    /// </summary>
    const float FollowShare = 0.5f;

    /// <summary>
    /// The current committed goal's floor while its commitment lasts and its cap after (<c>DAT_141696c90</c>, <c>DAT_141696c74</c>).
    /// </summary>
    const float CommitFloor = 0.4f, CommitCap = 0.05f;

    /// <summary>
    /// 253's desire ramp (<c>FUN_140597900</c>, <b>Verified</b>): 0 within <see cref="EscapeNear"/> x f of the followed character, 1 from
    /// <see cref="EscapeFar"/> x f (<c>DAT_1416b3ac8</c>, <c>DAT_1416abee4</c>).
    /// </summary>
    const float EscapeNear = 70, EscapeFar = 200;

    /// <summary>
    /// The followed mover runs (<c>FUN_14065ee70</c>, <b>Verified</b>) above this share of its top speed (<c>DAT_141696c84</c>), heading for a
    /// destination over <see cref="EscapeAhead"/> from the follower (squared 2500, <c>DAT_1416aef20</c>) and over <see cref="EscapeAway"/> times
    /// farther (squared, <c>DAT_1416b3bf0</c>) than the mover itself is.
    /// </summary>
    const float EscapeRunShare = 0.85f, EscapeAhead = 50, EscapeAway = 1.2f;

    /// <summary>
    /// The plan step before the choice (<see cref="PlanSteps"/>): the current goal's running step ends by its own tests and the next starts; with no
    /// step left the goal goes, committed or not (the original keeps a committed goal over the next update only for a choice limited to the tiers
    /// above it, then clears it, <c>FUN_14050d7d0</c>, <c>FUN_140511130</c>; one think here does both), so the choice that follows plans afresh. A
    /// walk or a stand is first aimed where the act-out goes (<see cref="FollowsWalk"/>).
    /// </summary>
    internal void EndStep(World world, int slot, CharacterId id, AiMind mind, Squad squad, Blackboard board, PlanContext plans, int hour)
    {
        if (mind.Goal is not { } current || !PlanContext.Stepped(current)) return;
        if (Displaced(world, slot, mind))
        {
            mind.Plan.Clear();
            mind.Goal = null;
            return;
        }

        if (FollowsWalk(mind)) FollowWalk(world, slot, id, mind, squad, board, plans, current);
        if (!PlanSteps.Run(world, slot, mind, plans, current, hour)) mind.Goal = null;
    }

    /// <summary>
    /// The kept plan of a walk made again (<see cref="FollowsWalk"/>): a plan whose running step is now another (the walk no longer needed) starts
    /// that step, rolling the task's timer as the original's next task start does (<see cref="StartTask"/>).
    /// </summary>
    void FollowWalk(World world, int slot, CharacterId id, AiMind mind, Squad squad, Blackboard board, PlanContext plans, AiGoal current)
    {
        var type = current.Task.Type;
        var before = mind.Step;
        float desire = Desire(world, TaskBehaviours.Of(type), type, TaskProfiles.Of(type), squad, board, mind, id, current.Task.Target);
        plans.TryPlan(current, desire, current: true);
        if (mind.Step != before) StartTask(world, slot, mind);
    }

    /// <summary>
    /// Whether the task running now is an engine reaction's or an NPC engine start's (<see cref="TaskFinished.EngineRow"/>) and not a step of the
    /// current planned goal's plan, started since that goal was taken up (<see cref="AiMind.GoalTick"/>: an engine stand the act-out keeps, as its
    /// task is the same, is not news): in the original that task is its own goal's or job's, so the goal Meitou keeps is no longer the current one
    /// (no continuity) and its plan is over.
    /// </summary>
    static bool Displaced(World world, int slot, AiMind mind)
    {
        if (mind.Goal is not { } current || !PlanContext.Stepped(current)) return false;
        if (world.Characters.Cold(slot)?.Running is not { Tick: var since } || since < mind.GoalTick) return false;
        if (TaskFinished.EngineRow(world, slot) is not { } row || row == current.Task.Type) return false;

        foreach (var step in mind.Plan)
            if (step.Type == row) return false;
        return true;
    }

    /// <summary>
    /// The pick's keep (<c>FUN_140510650</c>, <b>Verified</b>): a candidate that is the current goal (<c>FUN_140506c00</c>: same type and target)
    /// while a step of its plan runs is taken as it is, its plan not made again.
    /// </summary>
    internal static bool Kept(AiMind mind, AiGoal candidate) =>
        mind.Goal is { } current && current.Task.Type == candidate.Task.Type && current.Task.Target == candidate.Task.Target
        && mind.Plan.Count > 0 && PlanContext.Stepped(current);

    /// <summary>
    /// Whether the kept plan runs a walk, or the stand at a node it walked to (51): such a plan is made again (engine choice): Meitou has no walk or stand
    /// task of the plan's own, the goal's act-out walks to its spot and stands there (<see cref="ActOut"/>), and the spots it picks move with the
    /// package cadence's claims (seats, turrets, town posts) and the guard's visitor spot, so the walk follows where the act-out goes; a failed plan
    /// keeps the one it has. Once another step runs, the plan is kept as it is.
    /// </summary>
    static bool FollowsWalk(AiMind mind) => mind.Step is TaskType.MoveOnFreeWill or TaskType.MoveOnFreeWillFast or TaskType.StandAtNode;

    /// <summary>
    /// A candidate goal's score: its desire times its weight and its crowd factor (<see cref="TaskProfile.Crowd"/> per member already on it), the
    /// running goal's continuity, a committed goal's floor or ceiling, and the continuity again below 1 while its running task is not finished.
    /// </summary>
    internal float Score(World world, AiGoal g, TaskProfile profile, float desire, Squad squad, Blackboard board, AiMind mind, CharacterId id)
    {
        int crowd = profile.Crowd == 1 ? 0
            : board.CrowdOf(g.Task.Type, TaskDataTable.Of(g.Task.Type)?.Is(TaskDataFlags.NeedsTarget) ?? false,
                CrowdTarget(g.Task.Target, squad, board, id), id);
        float score = desire * g.Weight * (crowd > 0 ? MathF.Pow(profile.Crowd, crowd) : 1);

        if (IsCurrent(world, g, mind, id.Slot))
        {
            score *= Settings.Continuity;

            // A goal it committed to holds at least 0.4 until the commitment runs out, whatever its desire, then at most 0.05 (FUN_14050df10,
            // Verified: DAT_141696c90, DAT_141696c74).
            if (profile.Commits) score = world.GameHours < mind.CommitUntil ? MathF.Max(score, CommitFloor) : MathF.Min(score, CommitCap);

            // While the running task's class says it is not finished (its slot 4, TaskFinished), a score below 1 is raised once more
            // (FUN_14050df10, Verified).
            if (score < 1 && TaskFinished.Unfinished(world, id.Slot, mind.Step)) score *= Settings.Continuity;
        }

        return score;
    }

    /// <summary>
    /// Whether <paramref name="g"/> is the current goal for the scorer: the mind's goal, the same type and target, not displaced by an engine task
    /// (<see cref="Displaced"/>).
    /// </summary>
    static bool IsCurrent(World world, AiGoal g, AiMind mind, int slot) =>
        mind.Goal is { } current && current.Task.Type == g.Task.Type && current.Task.Target == g.Task.Target && !Displaced(world, slot, mind);

    /// <summary>
    /// Whether the scorer's commitment floor holds <paramref name="g"/> above 0 (<see cref="Score"/>): it is the current goal, commits and its
    /// commitment has not run out, so a desire of 0 still scores <see cref="CommitFloor"/> (<c>FUN_14050df10</c>, <b>Verified</b>).
    /// </summary>
    internal static bool HeldByCommitment(World world, AiGoal g, TaskProfile profile, AiMind mind, int slot) =>
        profile.Commits && world.GameHours < mind.CommitUntil && IsCurrent(world, g, mind, slot);

    /// <summary>
    /// The character a goal is aimed at, as its <c>TaskInfo</c> entry holds it (<c>FUN_140269e90</c> copies the goal's target hand): the member
    /// itself for SELF, the leader for LEADER, the contract's mission target for SQUAD_MISSION; none for the rest (SPECIFIC and HOME_GATE are the
    /// same for every member of the squad here).
    /// </summary>
    internal static CharacterId CrowdTarget(TaskTargetType targeting, Squad squad, Blackboard board, CharacterId who) => targeting switch
    {
        TaskTargetType.Self => who,
        TaskTargetType.Leader => squad.Leader,
        TaskTargetType.SquadMission => board.MissionTarget,
        _ => CharacterId.None,
    };

    /// <summary>
    /// How much the character wants a goal: its type's desire (<see cref="TaskProfiles"/>; a type with a desire function of its own, 1), times what
    /// the goal's target finder found (docs/game/ai-tasks.md "Town goals", Observed: 0 when it found nothing). Going home is 0.5 while away from the
    /// home town and 0 in it (docs/game/perception.md 7.2, Observed); a goal the AI cannot act out is 0, but an engine-acted
    /// one's, a code mod's among them (<see cref="DutyDesire"/>).
    /// </summary>
    internal float Desire(World world, AiBehaviour behaviour, TaskType type, TaskProfile profile, Squad squad, Blackboard board, AiMind mind,
        CharacterId self, TaskTargetType targeting)
    {
        if (behaviour == AiBehaviour.None)
        {
            return _duties is not null && _duties.Has(type) ? DutyDesire(_duties, world, type, squad, mind, self, targeting) : 0;
        }

        // Going to bed: the bed search is its target finder, whose factor is 0 when it finds no bed (FUN_1408cfe00, Verified), so the goal is not
        // wanted.
        if (type is TaskType.GoHomeAndGoToBed or TaskType.GoHomeAndGoToBedSecure && !HasBed(world, board, mind, self.Slot)) return 0;

        float desire = profile.Desire switch
        {
            TaskProfiles.Random => 0.01f + 0.4f * Rng.Float(Rng.Hash(world.Seed, Rng.Key(self), RngPurpose.Think,
                SeedSalts.AiDesire ^ (ulong)world.Tick << 9 ^ (ulong)type)),
            TaskProfiles.Special => 1,
            var d => d,
        };
        return desire * FinderFactor(world, behaviour, type, squad, board, mind, self, targeting);
    }

    /// <summary>
    /// An engine-acted goal's desire (<see cref="DutyGoals.Desire"/>: the registering system's desire times its finder's factor, or a
    /// code mod's; the type's default profile adds no commitment and no crowding); 0 for a type whose start the system refused, until its
    /// back-off ends. A player-faction mind (<see cref="AiMind.PlayerSide"/>) weighs the goals its
    /// tiers score: its autonomous squad's package roles, its own lists' only on the rungs or of a code mod's type (<see cref="OnRungs"/>).
    /// </summary>
    static float DutyDesire(DutyGoals goals, World world, TaskType type, Squad squad, AiMind mind, CharacterId self, TaskTargetType targeting) =>
        mind.BackedOff(type, world.Tick)
            ? 0
            : goals.Desire(world, new DutyAsk(self.Slot, self, squad, type, targeting, type, default));

    /// <summary>
    /// What a goal's target finder found, by its behaviour (0 when it found nothing, or when the goal is not wanted now): the factor on its type's
    /// desire (<see cref="Desire"/>).
    /// </summary>
    float FinderFactor(World world, AiBehaviour behaviour, TaskType type, Squad squad, Blackboard board, AiMind mind, CharacterId self,
        TaskTargetType targeting)
    {
        return behaviour switch
        {
            AiBehaviour.Follow => FollowFactor(world, squad, board, self, targeting, type),
            // Going to bed and sleeping on the ground have row 98's desire (AI slot 30): the registry gives rows 98, 153, 175, 239 and 261 the same
            // desire (FUN_140319a60, Verified); 153's and 239's bed finder (FUN_1408cfe00) gives 1 with a bed, the gate above 0 without.
            AiBehaviour.Sleep => SleepingDesire(world, board, self.Slot),
            AiBehaviour.GoHome => AtHomeTown(world, squad, board, self) ? 0 : GoHomeDesire(world, self),
            // Static guard's finder (FUN_14099edd0, Verified; 38 and 268 both register it, 268 with the indoors flag; 157 has its own, FUN_14099f1a0)
            // returns 0.5 on its normal exit; only its visitor branch (a node beside a neutral stranger, not modelled) returns 1. Staying home needs
            // a piece of the home's furniture to operate (the finder of 43 returns 0 without one).
            AiBehaviour.Post when type == TaskType.StayInHome => TownPlaceClaims.HasStayingFurniture(world, board.OwnHome) ? 1 : 0,
            AiBehaviour.Post when type is TaskType.StandAtGuardNodeHomebuilding or TaskType.StandAtGuardNodeHomebuildingIndoorsOnly =>
                GuardFactor(world, self, board, mind),
            // A turret is only wanted by a member that found one (the nearest unused one to it in <see cref="TownPlaceClaims.ClaimTurrets"/>):
            // the finder's factor times the desire of AI slot 35.
            AiBehaviour.Turret => board.TurretClaims.ContainsKey(self.Slot)
                ? board.TurretFactors.GetValueOrDefault(self.Slot, 1) * TurretDesire(world, self.Slot) : 0,
            AiBehaviour.TownNode => WanderDesire(world, board, world.Characters.Previous[self.Slot].Position),
            // Row 36 has no finder: its update strolls round the squad's town, or where the character stands without one (Task_PatrolTown,
            // docs/game/ai-tasks.md).
            AiBehaviour.PatrolTown => 1,
            // Shopping is wanted while its list has something left to buy today (FUN_140999620), and there is a shop to go to.
            // The finder's factor follows its pick: 1 for a shop, 0.4 for any other public building, 0 when it finds none (FUN_1409997f0).
            AiBehaviour.Shop => ShopDesire(world, self, mind, board.Town),
            // A worker needs a mine, bench or refinery in its town (the finders return 0 without one).
            AiBehaviour.Work => WorkWanted(world, self, mind, WorkTownOf(mind), type) ? 1 : 0,
            // Relaxing needs a free seat of the package's bar, sitting around a free chair of the home (claimed in
            // <see cref="TownPlaceClaims.ClaimSeats"/>); the original's finder returns 0 without one (FUN_1408caf00).
            AiBehaviour.Relax => board.BarTable >= 0 && board.SeatClaims.ContainsKey(self.Slot) && !mind.SeatUnreachable ? 1 : 0,
            // Reigning (255): AI slot 67 (FUN_140597c20, Verified), 1 only for the leader of the character's squad, else 0.
            AiBehaviour.Sit when type == TaskType.SitOnThrone && squad.Leader != self => 0,
            AiBehaviour.Sit => board.SitClaims.ContainsKey(self.Slot) && !mind.SeatUnreachable ? 1 : 0,
            AiBehaviour.ToTown => TownAt(world.Characters.Previous[self.Slot].Position) >= 0 ? 0 : 1,
            // 215 under a war package: AI slot 57, the constant 1 (FUN_140319a60, Verified); its list A ends it (the planner's 131).
            AiBehaviour.Travel when type == TaskType.TravelToTargetPackage && MissionFormation.Runs(board, PlatoonOf(world, squad)) => 1,
            // 39 (AI slot 19), 53 and 130 (slot 57): the constant 1 and no finder (FUN_140319a60, Verified); their plans decide (states 32, 36, 94).
            AiBehaviour.Travel when type != TaskType.TravelToTargetPackage => 1,
            // 215 elsewhere: to the package's mission town while not in it.
            AiBehaviour.Travel => board.RefugeeTarget >= 0 ? 1
                : board.Destination.IsNone || CurrentTown(world, world.Characters.Previous[self.Slot].Position) == board.Destination ? 0 : 1,
            // Only while an alarm of its town is in reach (the row's done state !ALARMS_IN_THE_VICINITY).
            AiBehaviour.Investigate => AlarmOf(world, self.Slot, type, board) is null ? 0 : 1,
            // The assault's finder factor: 1 while a member of the mission target's squad passes its tests, else 0 (FUN_140793eb0).
            // Crouching is wanted always (it holds the pose while it runs); standing up (its effects !24 !25 !26) only while crouched.
            AiBehaviour.Crouch => type == TaskType.Crouch || world.Characters.Cold(self.Slot) is { Crouched: true } ? 1 : 0,
            AiBehaviour.Assault => targeting == TaskTargetType.SquadMission && !AssaultTargetFor(world, self.Slot, board).IsNone ? 1 : 0,
            // Self preservation: the threat level (AI slot 17); Running away: row 35's own (AI slot 55), a goal of the packages that list it.
            AiBehaviour.SelfPreserve => SelfPreservationDesire(world, self.Slot, mind),
            AiBehaviour.Flee => mind.PlayerSide ? PlayerSideRunDesire(world, self.Slot)
                : world.Characters.Cold(self.Slot) is { } runner ? SelfPreservation.RunDesire(runner, SelfPreservation.Walled(world, self.Slot)) : 0,
            // A combat goal: its own desire's zeros and factors, times its finder's factor (an attack goal's 1 with a target, a protect goal's fight
            // threat; AiSystem.Fights.cs).
            AiBehaviour.Fight => FightDesire(world, self.Slot, type, mind.Squad) is var want and > 0
                ? want * FightFactor(world, self.Slot, type, mind)
                : 0,
            // The Medic goal: its desire times the finder's score from the serial sweep (the desire's carrying case is a gate of the finder).
            AiBehaviour.Medic => world.Characters.IsAlive(mind.MedicPatient) ? mind.MedicScore * MedicDesire(world, self.Slot) : 0,
            // Fixing Legs (269) has no finder: its own desire (FUN_1409983b0); the planner tests its list B (kit, arm, water, threat, weapon).
            AiBehaviour.HealLegs => HealLegsDesire(world, self.Slot),
            _ => 1,
        };
    }

    /// <summary>
    /// The Medic goal's desire (<c>FUN_140998410</c>, <b>Verified</b> by disassembly): 0.01 while the medic's senses hold a threat towards it (AI
    /// <c>+0xb0</c>), else 0.05 while they count an enemy (<c>+0xbc</c>, <c>numEnemies</c>), else 1. Its own senses, not its squad's fights.
    /// </summary>
    internal static float MedicDesire(World world, int slot)
    {
        if (Bodies.CharacterFacts.Senses(world, slot) is not { } senses) return 1;
        if (senses.ThreatToMe > 0) return 0.01f;
        return senses.Enemies > 0 ? 0.05f : 1;
    }

    /// <summary>
    /// Following's desire (AI slot 15, <c>FUN_1405a33f0</c>, <b>Verified</b>; rows 31, 40, 44, 141 and 289 register it): 0.5 times the followed
    /// character's <c>followMe</c> (<see cref="FollowMe"/>, as stamped this tick), so a member follows a patrolling or travelling leader (1) at 0.5
    /// and one standing about or asleep (0.1) at 0.05. Other follow rows want 1.
    /// </summary>
    float FollowDesire(TaskType type, CharacterId target) => type is TaskType.StayCloseToTarget or TaskType.GetNearTo or TaskType.FollowPlayerOrder
        or TaskType.FollowSquadleader or TaskType.Unnamed289 ? FollowShare * _followMe.Of(target.Slot) : 1;

    /// <summary>
    /// Following needs a living target other than self: the contract's mission target, else the slave owner or squad leader, as movement resolves
    /// it. A slave that leads its own squad can therefore follow its master.
    /// </summary>
    float FollowFactor(World world, Squad squad, Blackboard board, CharacterId self, TaskTargetType targeting, TaskType type)
    {
        var cold = world.Characters.Cold(self.Slot)!;
        var target = targeting == TaskTargetType.SquadMission
            ? board.MissionTarget
            : Kidnap.ObedientSlave.OwnerOf(world, cold) ?? squad.Leader;
        if (target.IsNone || target == self || !world.Characters.IsAlive(target)) return 0;

        return type == TaskType.FollowUrgentEscape ? EscapeDesire(world, self.Slot, target.Slot) : FollowDesire(type, target);
    }

    /// <summary>
    /// 253 Following (urgent escape)'s desire (AI slot 16, <c>FUN_140597900</c>, <b>Verified</b>): the follower's distance d to the followed
    /// character on the ramp (d - 70 f) / (130 f), clamped to [0, 1], where f is 1 while the followed character runs away from the follower
    /// (<see cref="RunsAway"/>) and 2 otherwise: a runner is kept within 70 to 200, anyone else within 140 to 400. Reads the last tick's state.
    /// </summary>
    static float EscapeDesire(World world, int slot, int followed)
    {
        var prev = world.Characters.Previous;
        float f = RunsAway(prev[followed], Flat(prev[slot].Position)) ? 1 : 2;
        float d = Vector3.Distance(prev[slot].Position, prev[followed].Position);
        return Math.Clamp((d - EscapeNear * f) / ((EscapeFar - EscapeNear) * f), 0, 1);
    }

    /// <summary>
    /// The followed mover's escape test (<c>FUN_14065ee70</c>, <b>Verified</b>): its current speed (CharMovement <c>+0xb8</c>, the velocity) above
    /// 0.85 of its top speed (<c>+0xb4</c>, <see cref="CharacterHot.MaxSpeed"/>), its destination (<c>+0xdc</c>, <see cref="CharacterHot.Goal"/>)
    /// over 50 from the follower and, squared, over 1.2 times the mover's own squared distance to it. Engine reading: flat distances, the
    /// destination having no height here.
    /// </summary>
    static bool RunsAway(in CharacterHot mover, Vector2 from)
    {
        float speed = new Vector2(mover.Velocity.X, mover.Velocity.Z).Length();
        if (speed <= mover.MaxSpeed * EscapeRunShare) return false;

        float toGoal = Vector2.DistanceSquared(from, mover.Goal);
        float toMover = Vector2.DistanceSquared(from, Flat(mover.Position));
        return toGoal > EscapeAhead * EscapeAhead && toGoal > EscapeAway * toMover;
    }

    /// <summary>
    /// Going home while away (AI slot 56, <c>FUN_1405a3710</c>, Verified): 0.5, but 0.01 for a slave (state 1) that is not with the player
    /// (<c>FUN_140856bc0</c>: its faction or its owner's is the player's).
    /// </summary>
    internal static float GoHomeDesire(World world, CharacterId self)
    {
        // 0 shut in behind a locked gate of town walls (FUN_1402eb070 to code 0 is 3, Verified).
        if (world.SystemOf<Locks.LockSystem>() is { } walls && walls.WallsLocked(world, world.Characters.Previous[self.Slot].Position)) return 0;
        if (world.Characters.Cold(self.Slot) is not { SlaveState: 1 } c) return 0.5f;
        int player = world.Player.Faction;
        bool withPlayer = c.IsPlayer || player >= 0 && (c.Faction == player || c.SlaveOwner == player);
        return withPlayer ? 0.5f : 0.01f;
    }

    /// <summary>
    /// Whether a character is within its squad's home town's radius (a site's or an outpost's, <see cref="HomePlace"/>); true when the squad has no
    /// town or the character is not alive.
    /// </summary>
    bool AtHomeTown(World world, Squad squad, Blackboard board, CharacterId self)
    {
        if (HomePlace(world, board) is not { } town || !world.Characters.IsAlive(self)) return true;

        var p = Flat(world.Characters.Previous[self.Slot].Position);
        return Vector2.DistanceSquared(p, town.Centre) <= town.Radius * town.Radius;
    }

    /// <summary>
    /// Where the squad's home town (<see cref="Blackboard.HomeTown"/>: its site, or the outpost an autonomous squad's player-built home stands in)
    /// stands; null without one.
    /// </summary>
    PlacedTown? HomePlace(World world, Blackboard board) => TownPlace(data, world, board.HomeTown);

    #endregion

    #region Town places and targets

    /// <summary>
    /// The towns' places for the AI's squads (<see cref="TownPlaces"/>), by town site; rebuilt on the package cadence.
    /// </summary>
    readonly TownPlaceIndex _townPlaces = new(data);

    /// <summary>
    /// The desire and the target ask on every worker: one scratch list each.
    /// </summary>
    [ThreadStatic] static List<TownBuilding>? s_sellingScratch;

    /// <summary>
    /// BuildingDesignation BD_BAR.
    /// </summary>
    internal const int DesignationBar = 3;

    /// <summary>
    /// BUILDING <c>node type</c> NODE_GENERAL.
    /// </summary>
    const int NodeGeneral = 1;

    /// <summary>
    /// The finder factor of a building the shopper picked (<c>FUN_1409997f0</c>, <b>Verified</b>): 1 for one with BF_SHOP furniture inside, 0.4 for
    /// any other public building.
    /// </summary>
    const float ShopNoCounterFactor = 0.4f;

    /// <summary>
    /// The nearest town whose <see cref="TownData.Radius"/> holds a point (as the game's position lookup, <c>FUN_140928890</c>), -1 when none.
    /// </summary>
    internal int TownAt(Vector3 p)
    {
        var at = Flat(p);
        int best = -1;
        float bestD = float.MaxValue;
        for (int s = 0; s < data.Sites.Count; s++)
        {
            var site = data.Sites[s];
            float r = site.Town.Radius, d = Vector2.DistanceSquared(at, Flat(site.Position));
            if (d <= r * r && d < bestD) (best, bestD) = (s, d);
        }

        return best;
    }

    /// <summary>
    /// The places of town <paramref name="site"/> (<see cref="TownPlaceIndex.Of"/>).
    /// </summary>
    TownPlaces Places(int site) => _townPlaces.Of(site);

    /// <summary>
    /// Whether a home spot is a general node (<see cref="NodeGeneral"/>).
    /// </summary>
    static bool IsGeneralNode(HomeSpot s) => s.NodeType == NodeGeneral;

    /// <summary>
    /// The character's roll of this tick for the target of a town goal of <paramref name="behaviour"/>.
    /// </summary>
    static ulong TargetRoll(World world, CharacterId self, AiBehaviour behaviour) =>
        Rng.Hash(world.Seed, Rng.Key(self), RngPurpose.Think, SeedSalts.AiTarget ^ (ulong)world.Tick << 8 ^ (ulong)behaviour);

    /// <summary>
    /// Where a town goal goes (docs/game/ai-tasks.md "Town goals"): wandering in town, a random general node of a random public building of the town
    /// in the walker's zone (<b>Verified</b>, <c>FUN_1409fc6c0</c>, <c>FUN_140999a60</c>: one without such a node gives no target), in an outpost
    /// the squad has as its town a public building there itself (<see cref="TownPlaceIndex.Of(CampaignTown)"/>); shopping, a
    /// random shop of the town that sells what its list wants, else a random public building (<see cref="ShopFor"/>): a shop at a general node in it,
    /// else at it, any other building at its origin; the wanderer, the nearest town that is no nest marker. Null when there is none.
    /// </summary>
    Vector3? TargetFor(World world, CharacterId self, AiBehaviour behaviour, CampaignTown town, AiMind mind)
    {
        ulong roll = TargetRoll(world, self, behaviour);
        switch (behaviour)
        {
            case AiBehaviour.TownNode:
                {
                    // Wandering: a random general node of the drawn building, none: no target and no redraw (FUN_140999a60, FUN_14038c2f0).
                    // A building the player built (an outpost's) has no nodes: the building itself, its origin on any floor, and not one the
                    // player has locked since the places were built (engine choice).
                    var b = PickFrom(Unlocked(world, _townPlaces.WanderIn(town, world.Characters.Previous[self.Slot].Position)), roll);
                    if (b is { RuntimeKey: not null }) return new Vector3(b.Position.X, float.NaN, b.Position.Z);

                    return b is null ? null : RandomNode(b, IsGeneralNode, roll);
                }
            case AiBehaviour.Shop:
                return ShopTarget(world, self, town.Site, mind, roll);
            case AiBehaviour.ToTown:
                {
                    var at = Flat(world.Characters.Previous[self.Slot].Position);
                    int best = -1;
                    float bestD = float.MaxValue;
                    for (int s = 0; s < data.Sites.Count; s++)
                    {
                        // Every town type but a nest marker (the finder's type filter 10, FUN_140927f10: type 8 is taken only by a filter of exactly
                        // 8).
                        if (data.Sites[s].Town.Type == TownType.NestMarker) continue;
                        float d = Vector2.DistanceSquared(at, Flat(data.Sites[s].Position));
                        if (d < bestD) (best, bestD) = (s, d);
                    }

                    return best < 0 ? null : data.Sites[best].Position;
                }
        }

        return null;
    }

    /// <summary>
    /// Where goal 118 goes: the shop it picks (<see cref="ShopFor"/>) in the shopper's town (<see cref="ShopTown"/>), at a general node in it; null
    /// when it picks none.
    /// </summary>
    Vector3? ShopTarget(World world, CharacterId self, int town, AiMind mind, ulong roll)
    {
        var (places, _, random) = ShopTown(world, self.Slot, town);
        if (ShopFor(places, mind, roll, random) is not { } b) return null;

        // A building without a counter is the target itself, its origin on any floor (FUN_1409997f0); the walk ends within WorkReach of it. So is
        // a building the player built (an outpost's shop), which has no nodes.
        if (ShopFactor(places, b) < 1 || b.RuntimeKey is not null) return new Vector3(b.Position.X, float.NaN, b.Position.Z);

        // A general node; in a shop without one a guard post, else any other node (engine choice: where the original's Task_Shopping stands is not
        // decoded).
        foreach (var accept in (ReadOnlySpan<Func<HomeSpot, bool>>)[IsGeneralNode, s => s.Kind == HomeSpotKind.Guard, s => s.IsNode])
        {
            int nodes = 0;
            foreach (var s in b.Spots) if (accept(s)) nodes++;
            if (nodes == 0) continue;
            int pick = Rng.Int(Rng.Mix(roll), nodes);
            foreach (var s in b.Spots)
                if (accept(s) && pick-- == 0) return s.Position;
        }

        return b.Position;
    }

    /// <summary>
    /// A random spot of <paramref name="building"/> that <paramref name="accept"/>s, its position; null when it has none.
    /// </summary>
    static Vector3? RandomNode(TownBuilding building, Func<HomeSpot, bool> accept, ulong roll)
    {
        int nodes = 0;
        foreach (var s in building.Spots) if (accept(s)) nodes++;
        if (nodes == 0) return null;
        int pick = Rng.Int(Rng.Mix(roll), nodes);
        foreach (var s in building.Spots)
            if (accept(s) && pick-- == 0) return s.Position;
        return null;
    }

    /// <summary>
    /// A building of <paramref name="from"/> picked by <paramref name="roll"/> (uniform); null when the list is empty.
    /// </summary>
    static TownBuilding? PickFrom(IReadOnlyList<TownBuilding> from, ulong roll) => from.Count == 0 ? null : from[Rng.Int(roll, from.Count)];

    /// <summary>
    /// 37 Wandering town's desire: 1 while the squad's town has a building to wander to from <paramref name="at"/> (<see cref="Unlocked"/>) or
    /// one occupied, else 0.
    /// </summary>
    float WanderDesire(World world, Blackboard board, Vector3 at)
    {
        bool any = Unlocked(world, _townPlaces.WanderIn(board.HomeTown, at)).Count > 0 || OccupiedBuildings(world, board) > 0;
        return any ? 1 : 0;
    }

    /// <summary>
    /// The buildings of <paramref name="from"/> a wanderer may still go to: a placed town's list as it is; from an outpost's (stand-ins of buildings
    /// the player built, <see cref="TownBuilding.RuntimeKey"/>) those whose doors the player has not all locked since the places were built
    /// (<see cref="TownPlaceIndex"/>; the AI-run squad keeps to the player's locks, <see cref="SquadAutonomy.KeepsPlayerLocks"/>).
    /// </summary>
    static IReadOnlyList<TownBuilding> Unlocked(World world, IReadOnlyList<TownBuilding> from)
    {
        if (from.Count == 0 || from[0].RuntimeKey is null) return from;

        return from.Where(b => !world.Fixtures.AllDoorsLocked(b.RuntimeKey!)).ToArray();
    }

    /// <summary>
    /// Whether <paramref name="target"/>, a wanderer's target in an outpost (a building the player built, walked to at its origin on any floor),
    /// is no longer offered: its building has left <paramref name="town"/>'s places (made private, locked or pulled down by the last refresh,
    /// <see cref="TownPlaceIndex"/>) or the player has locked all its doors since. The target is given up and drawn again, so no walk goes on to
    /// a locked door (engine choice; the squad keeps to the player's locks, <see cref="Unlocked"/>).
    /// </summary>
    bool NoLongerOffered(World world, CampaignTown town, Vector3 target)
    {
        if (town.PlayerTown <= 0 || !float.IsNaN(target.Y)) return false;

        foreach (var zone in _townPlaces.Of(town).Wander.Values)
            foreach (var b in zone)
            {
                if (b.Position.X == target.X && b.Position.Z == target.Z)
                    return world.Fixtures.AllDoorsLocked(b.RuntimeKey!);
            }

        return true;
    }

    /// <summary>
    /// The shop goal 118 goes to (<c>FUN_1409997f0</c>, <b>Verified</b>): a random shop of the town selling an item function of the first entry on
    /// the character's list that has one, the entries taken in order and those already bought skipped (<c>FUN_1409fc070</c>); none sells one: a
    /// random public building of the town (<c>FUN_1409fc1b0</c>, <see cref="TownPlaces.Public"/>; not only a shop). The random pick is made only when
    /// <paramref name="randomAllowed"/> (<see cref="RandomShopping"/>): in a town of the player's faction only a shop selling what the list wants is
    /// found (the list finder runs before that test, <c>FUN_1409997f0</c>, <b>Verified</b>). The goal's factor is <see cref="ShopFactor"/>.
    /// </summary>
    static TownBuilding? ShopFor(TownPlaces places, AiMind mind, ulong roll, bool randomAllowed)
    {
        var shops = places.Shops;
        var selling = s_sellingScratch ??= [];
        for (int k = 0; shops.Count > 0 && k < mind.Shopping.Count; k++)
        {
            int function = mind.Shopping.OpenFunction(k);
            if (function < 0) continue;
            selling.Clear();
            foreach (var shop in shops)
                if (places.Stock.TryGetValue(shop, out ulong mask) && ShoppingList.Stocks(mask, function)) selling.Add(shop);
            if (selling.Count > 0) return selling[Rng.Int(roll, selling.Count)];
        }

        return randomAllowed && places.Public.Count > 0 ? places.Public[Rng.Int(roll, places.Public.Count)] : null;
    }

    /// <summary>
    /// The factor of the pick: 1 for a shop (Meitou's <see cref="TownPlaces.Shops"/>: trading residents or the layout's shop flag, standing for
    /// BF_SHOP furniture, <b>Observed</b>), else <see cref="ShopNoCounterFactor"/>. It multiplies the goal's desire.
    /// </summary>
    static float ShopFactor(TownPlaces places, TownBuilding building)
    {
        foreach (var shop in places.Shops)
            if (ReferenceEquals(shop, building)) return 1;
        return ShopNoCounterFactor;
    }

    /// <summary>
    /// Goal 118's desire (<c>FUN_140999620</c>): 0 once nothing is left on the list today in the shopper's town (<see cref="ShopTown"/>), else the
    /// factor of the shop it would go to (<see cref="ShopFactor"/>; 0 with none).
    /// </summary>
    float ShopDesire(World world, CharacterId self, AiMind mind, int town)
    {
        var (places, key, random) = ShopTown(world, self.Slot, town);
        if (!mind.Shopping.Pending(ShoppingList.DayOf(world.GameHours), world.Hour, key)) return 0;

        return ShopFor(places, mind, TargetRoll(world, self, AiBehaviour.Shop), random) is { } picked ? ShopFactor(places, picked) : 0;
    }

    /// <summary>
    /// The town goal 118 shops in, with its places, its key in the shopping list (<see cref="ShoppingList.KeyOf"/>) and whether its random pick
    /// runs (<see cref="RandomShopping"/>). The original's goal takes the shopper's status town and finds nothing without one
    /// (<c>FUN_1409997f0</c> reads status <c>+0x88</c> and returns 0 when it is empty, <b>Verified</b>, disassembly;
    /// <see cref="CharacterCold.StatusTown"/>; a character that holds none has it worked out from where it stands, as the status pass does on
    /// entering a town, so one the pass has not reached yet shops too). So a shopper shops where it
    /// stands, never in a town it is away from: at an outpost's shops (<see cref="TownPlaceIndex.OutpostShops"/>) with no random pick (a town of
    /// the player's faction), in a placed town at that town's. A shopper of the player's faction (an autonomous squad) is left out: the original
    /// plans no goals for the player's squads, and its buying in an outpost would only move the player's goods between the player's own counter
    /// and character; Meitou keeps the squad's town <paramref name="town"/> for it.
    /// </summary>
    (TownPlaces Places, int Key, bool Random) ShopTown(World world, int slot, int town)
    {
        var cold = world.Characters.Cold(slot);
        if (cold is null || cold.Faction == PlayerFactionOf(world)) return (Places(town), town, RandomShopping(world, town));

        var status = cold.StatusTown.IsNone
            ? StatusTownAt(data, world, world.Characters.Previous[slot].Position, CampaignTown.None) : cold.StatusTown;
        if (status.PlayerTown > 0) return (_townPlaces.OutpostShops(status.PlayerTown), ShoppingList.KeyOf(status), false);
        if (status.Site >= 0) return (Places(status.Site), status.Site, RandomShopping(world, status.Site));

        return (TownPlaceIndex.None, -1, false);
    }

    /// <summary>
    /// The player's faction: the world's, else the data's (-1: none).
    /// </summary>
    int PlayerFactionOf(World world) => world.Player.Faction >= 0 ? world.Player.Faction : data.PlayerFaction;

    /// <summary>
    /// Whether the random pick of goal 118 runs in <paramref name="town"/>: its finder <c>FUN_1409fc1b0</c> is called only when the faction of the
    /// shopper's town has no player flag (<c>+0x250</c> clear, <c>FUN_1409997f0</c>, <b>Verified</b>), i.e. the town is not the player's faction's.
    /// </summary>
    bool RandomShopping(World world, int town) =>
        !(town >= 0 && town < data.SiteFactions.Count && data.SiteFactions[town] is >= 0 and var owner
            && owner == (world.Player.Faction >= 0 ? world.Player.Faction : data.PlayerFaction));

    #endregion

    #region Patrol

    /// <summary>
    /// The scatter of a patrol order with no town (<c>FUN_1405c6b30</c>, <c>DAT_1416f190c</c>) and the least scatter of any (<c>DAT_14168dcf4</c>).
    /// </summary>
    const float PatrolScatter = 1250;

    /// <summary>
    /// The least scatter of a town patrol's points (<see cref="PatrolScatter"/>).
    /// </summary>
    const float PatrolScatterMin = 500;

    /// <summary>
    /// How far round a drawn patrol point the navmesh is searched for the face whose building is tested (<c>FUN_14065eaa0</c>, <c>DAT_14168dc94</c>,
    /// <b>Verified</b>).
    /// </summary>
    const float PatrolFaceReach = 300;

    /// <summary>
    /// Acts out 36 Patrolling town (<c>Task_PatrolTown</c>, docs/game/ai-tasks.md "Town goals"): strolls to a point drawn round its squad's town
    /// (<see cref="PatrolArea"/>, <see cref="PatrolPoint"/>) and, once the walk is over, forgets it, so the next think draws another, as the
    /// original's update sends a new move each time the last one ended.
    /// </summary>
    void Patrol(World world, int slot, AiMind mind, Blackboard board)
    {
        if (mind.Target is null)
        {
            var (centre, scatter) = PatrolArea(world, mind.Squad, board, world.Characters.Previous[slot].Position);
            mind.Target = PatrolPoint(world, slot, centre, scatter);
        }

        if (mind.Target is not { } point)
        {
            Become(world, slot, CharacterTask.Idle);
            return;
        }

        if (!GoToSpot(world, slot, mind, Flat(point), GiveUp.Target, SpeedMode.Walk, height: float.IsNaN(point.Y) ? null : point.Y)) return;
        Become(world, slot, CharacterTask.Idle);
        mind.Target = null;
    }

    /// <summary>
    /// The area order of 36 Patrolling town (<c>Task_PatrolTown</c> through <c>FUN_1405c6b30</c>, docs/game/ai-tasks.md "Town goals",
    /// <b>Verified</b>): the squad's town hand (<see cref="SquadTown.Of"/> with the home fallback) at a scatter of half the town's radius, at least
    /// 500; a nest squad's home town is its nest (the original's <c>Nest</c> is a town: #771, Meitou had its members random-walk kilometres away);
    /// with none, the character's own position at 1250. The centre's height is the town's (a nest's, which Meitou keeps flat, the character's).
    /// </summary>
    (Vector3 Centre, float Scatter) PatrolArea(World world, Squad squad, Blackboard board, Vector3 from)
    {
        var town = SquadTown.Of(world, squad, board, homeFallback: true);
        if (town.Site >= 0)
        {
            var site = data.Sites[town.Site];
            return (site.Position, MathF.Max(PatrolScatterMin, site.Town.Radius * 0.5f));
        }

        if (town.PlayerTown > 0 && world.Buildings.FindTown(town.PlayerTown) is { } player)
        {
            return (player.Position, MathF.Max(PatrolScatterMin, player.Radius * 0.5f));
        }

        if (town.Nest > 0 && world.SystemOf<PopulationSystem>() is { } population && population.WildCentreOf(town.Nest - 1) is { } centre
            && population.WildSites[town.Nest - 1].Nest is { } nest)
        {
            return (new Vector3(centre.X, from.Y, centre.Y), MathF.Max(PatrolScatterMin, nest.Radius * 0.5f));
        }

        return (from, PatrolScatter);
    }

    /// <summary>
    /// Where 36 Patrolling town walks next (<c>setPatrolInput</c>, docs/game/factions-squads-towns.md "Patrol draws"): the area's centre moved by
    /// up to half the scatter in X and Z; of up to 10 draws the first on walkable ground whose nearest navmesh face is outdoors or in a building the
    /// walker may stroll into (<see cref="MayPatrolInto"/>). Null when every draw fails: no walk this think.
    /// </summary>
    Vector3? PatrolPoint(World world, int slot, Vector3 centre, float scatter)
    {
        var self = world.Characters.IdOf(slot);
        ulong roll = TargetRoll(world, self, AiBehaviour.PatrolTown);
        for (int i = 0; i < 10; i++)
        {
            roll = Rng.Mix(roll);
            float x = centre.X + (Rng.Float(roll) - 0.5f) * scatter;
            roll = Rng.Mix(roll);
            float z = centre.Z + (Rng.Float(roll) - 0.5f) * scatter;
            if (!world.Walkability.IsWalkable(x, z)) continue;

            var face = new Vector3(x, centre.Y, z);
            if (world.Walkability.InteriorNear(face, PatrolFaceReach) is { } building && !MayPatrolInto(world, slot, building, face))
                continue;

            return new Vector3(x, float.NaN, z);
        }

        return null;
    }

    /// <summary>
    /// Whether a patrol draw on <paramref name="building"/>'s interior navmesh at <paramref name="at"/> stands (<c>FUN_14065eaa0</c>,
    /// <b>Verified</b>): the building has an open door (<see cref="Locks.FixtureRegistry.HasOpenDoor"/>, the original's <c>FUN_1405499a0</c>), is
    /// open to the walker (<see cref="OpenToPatrol"/>), and an animal walker is let in as well (<see cref="AdmitsAnimal"/>).
    /// </summary>
    bool MayPatrolInto(World world, int slot, string building, Vector3 at)
    {
        if (!world.Fixtures.HasOpenDoor(building) || !OpenToPatrol(world, slot, building)) return false;

        return world.Characters.Cold(slot) is not { } c || !SquadRegistry.IsAnimal(c) || AdmitsAnimal(world, c, building, at);
    }

    /// <summary>
    /// The building is public, or its owner faction is the walker's own or an ally, or the player's (<c>FUN_14065eaa0</c>).
    /// </summary>
    bool OpenToPatrol(World world, int slot, string building)
    {
        if (world.Fixtures.IsPublic(building)) return true;

        int owner = world.Fixtures.OwnerOf(building);
        if (owner < 0) return false;

        int player = world.Player.Faction >= 0 ? world.Player.Faction : data.PlayerFaction;
        int faction = world.Characters.Cold(slot)?.Faction ?? -1;
        return owner == player || faction >= 0 && Relations.Contains(faction) && Relations.Contains(owner) && Relations.IsAlly(faction, owner);
    }

    /// <summary>
    /// The animal terms of a patrol draw indoors (<c>FUN_14065eaa0</c>, <b>Verified</b>): both CharacterAnimal slot 101
    /// (<see cref="Locks.AnimalEntry.MayEnter"/>) and <c>FUN_140547500</c> (<see cref="Locks.AnimalEntry.Allows"/>) let it in.
    /// </summary>
    bool AdmitsAnimal(World world, CharacterCold c, string building, Vector3 at) =>
        BuildingRecordOf(world, building, at) is { } record && Locks.AnimalEntry.Allows(record)
        && Locks.AnimalEntry.MayEnter(c, record.GetBool("cavernous"));

    /// <summary>
    /// The BUILDING record of the building of key <paramref name="building"/>: a player building's, else the town building's (the town at
    /// <paramref name="at"/> looked in first); null when none is known.
    /// </summary>
    internal GameRecord? BuildingRecordOf(World world, string building, Vector3 at)
    {
        if (world.Buildings.Find(building) is { } site) return data.Db.Find(site.Record);

        int town = TownAt(at);
        if (town >= 0 && TownBuildingRecord(data.Sites[town], building) is { } near) return near;
        foreach (var other in data.Sites)
            if (TownBuildingRecord(other, building) is { } found) return found;

        return null;
    }

    /// <summary>
    /// The record of the town building of key <paramref name="building"/> in <paramref name="site"/>; null when the town has none.
    /// </summary>
    GameRecord? TownBuildingRecord(TownSite site, string building)
    {
        foreach (var b in site.Buildings)
            if (b.Key == building) return data.Db.Find(b.BuildingId);

        return null;
    }

    #endregion

    #region Work

    /// <summary>
    /// The operator slots of each workplace held in the current cadence pass (derived, not hashed).
    /// </summary>
    readonly Dictionary<TownBuilding, int> _workTaken = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// The work data of the buildings and home pieces (reach, clip, functionality, hunger rate, operator slots), cached by string id.
    /// </summary>
    readonly WorkFunctionality _work = new(data);

    /// <summary>
    /// How near a worker stands to a building without a functionality <c>use range</c> (units, engine choice).
    /// </summary>
    internal const float WorkReach = 30;

    /// <summary>
    /// BuildingFunction BF_MINE.
    /// </summary>
    const int MineFunction = 1;

    /// <summary>
    /// How near a worker must come to its building's position: the functionality's <c>use range</c> (the original's <c>UseableStuff</c> reach,
    /// <c>maxUseRange</c>, docs/game/buildings-production.md "Work distance"), <see cref="WorkReach"/> for a building without one.
    /// </summary>
    internal float WorkRangeOf(TownBuilding b) => _work.RangeOf(b);

    /// <summary>
    /// Where a worker walks to at a workplace and what its reach is measured from: the building's use node nearest <paramref name="from"/>, its
    /// position without one (<see cref="WorkFunctionality.MarkerOf"/>).
    /// </summary>
    internal Vector3 WorkMarker(World world, TownBuilding b, Vector3 from) => _work.MarkerOf(b, from, world.Walkability);

    /// <summary>
    /// A worker is at its workplace: within the building's work range of its marker from where the worker stands (<see cref="WorkMarker"/>,
    /// <see cref="AtWork"/>).
    /// </summary>
    internal bool AtWorkplace(World world, in CharacterHot n, TownBuilding b) =>
        AtWork(in n, WorkMarker(world, b, n.Position), WorkRangeOf(b));

    /// <summary>
    /// A worker is within <paramref name="reach"/> of <paramref name="point"/>, or twice that once it has stopped (the original's arrival doubles
    /// the reach of a character that is not moving, docs/game/buildings-production.md "Work distance"), so one held off by the building's
    /// collision short of the reach has arrived there.
    /// </summary>
    internal static bool AtWork(in CharacterHot n, Vector3 point, float reach)
    {
        float dx = n.Position.X - point.X, dz = n.Position.Z - point.Z;
        bool stopped = n.Velocity.X * n.Velocity.X + n.Velocity.Z * n.Velocity.Z <= 1;
        float r = stopped ? 2 * reach : reach;
        return dx * dx + dz * dz <= r * r;
    }

    /// <summary>
    /// The machine events and held tool of standing work at a functionality's clip (<see cref="CharacterCold.ActionWork"/>); null without a
    /// functionality or clip.
    /// </summary>
    internal static WorkEvents? StandingWork(string? functionality, string? clip, string? tool) =>
        functionality is not null && clip is not null ? new WorkEvents(functionality, clip, tool) : null;

    /// <summary>
    /// The building a worker works at (the finders of goals 189 / 190: a random building of the town with a function in
    /// <see cref="TownPlaceIndex.WorkFunctions"/>, slot 2 of the building finder <c>FUN_1409fb020</c>); the roll is the character's and its
    /// <paramref name="epoch"/> of picks (<see cref="AiMind.WorkEpoch"/>), so asking twice names the same one. Null: the town has none. 189
    /// (<paramref name="type"/>, real work) takes only the workplaces it can run (<see cref="RealWork"/>); 190 (pretend) any. The workplace whose
    /// operator slot the character itself holds (<paramref name="held"/>) counts as free for it (<c>couldIOperate</c> passes for a current operator).
    /// The town is a site, or an outpost an autonomous squad lives in (<see cref="Blackboard.HomeTown"/>).
    /// </summary>
    TownBuilding? PickWork(World world, CharacterId self, CampaignTown town, int epoch, TaskType type = TaskType.AutoLabouringMinesPretend,
        TownBuilding? held = null)
    {
        if (!WorkAllowed(world, self.Slot, town)) return null;
        var list = WorkplacesFor(town, type);
        int free = 0;
        foreach (var b in list) if (HasFreeSlot(b, held)) free++;
        if (free == 0) return null;
        int pick = Rng.Int(Rng.Hash(world.Seed, Rng.Key(self), RngPurpose.Think, SeedSalts.AiTarget ^ (ulong)epoch << 20 ^ 0x57), free);
        foreach (var b in list)
            if (HasFreeSlot(b, held) && pick-- == 0) return b;
        return null;
    }

    /// <summary>
    /// Whether a mind holds a workplace that is still one its goal <paramref name="type"/> may work at.
    /// </summary>
    bool HoldsWork(AiMind mind, CampaignTown town, TaskType type) => mind.WorkPost is { } post && WorkplacesFor(town, type).Contains(post);

    /// <summary>
    /// Whether a workplace has an operator slot no one holds in this cadence pass (73 <c>machineHasFreeOperatorSlot</c>; the finder's
    /// <c>couldIOperate</c>, docs/game/ai-tasks.md "Worker"): fewer holders than its functionality's <c>max operators</c>.
    /// </summary>
    bool HasFreeSlot(TownBuilding building, TownBuilding? held = null) =>
        _workTaken.GetValueOrDefault(building) - (ReferenceEquals(building, held) ? 1 : 0) < _work.MaxOperatorsOf(building);

    /// <summary>
    /// The operator slots of 189 / 190 (docs/game/ai-tasks.md "Worker"): a member whose goal is one of them keeps the workplace it holds while that
    /// is still one its goal may work at, else holds one with a free slot drawn as <see cref="PickWork"/> draws; a member on another goal lets its
    /// slot go. Serial, on the package cadence, after every squad's kept slots are reserved (<see cref="ReserveWork"/>); an NPC mind's work town is
    /// stamped first (<see cref="WorkTownAt"/>; a player-faction mind's every tick, <see cref="StampWorkTown"/>).
    /// </summary>
    void ClaimWork(World world, Squad squad)
    {
        foreach (var id in squad.Members)
        {
            if (!world.Characters.TryResolveNext(id, out int s) || world.Characters.Cold(s) is not { Ai: { } mind } cold)
                continue;
            if (!mind.PlayerSide) mind.WorkTown = WorkTownAt(world, s, cold);
            var type = mind.Goal?.Task.Type;

            // A player-faction member on a player job holds no AI work slot (its job's machine counts it).
            bool onJob = mind.Player && OnJobTask(world.Characters.Next[s], cold);
            bool works = type is TaskType.AutoLabouringMines or TaskType.AutoLabouringMinesPretend && !onJob;
            var town = WorkTownOf(mind);
            if (works && HoldsWork(mind, town, type!.Value)) continue;
            if (mind.WorkPost is { } left) _workTaken[left] = _workTaken.GetValueOrDefault(left) - 1;
            mind.WorkPost = works ? PickWork(world, id, town, mind.WorkEpoch, type!.Value) : null;
            if (mind.WorkPost is { } got) _workTaken[got] = _workTaken.GetValueOrDefault(got) + 1;
        }
    }

    /// <summary>
    /// The workplaces the members hold, reserved for every squad before any claims (as the seats, <see cref="TownPlaceClaims.Reserve"/>; a player's
    /// squad's slaves too, <see cref="ClaimSlaveWork"/>), and the player's operators on their jobs at an outpost's machines.
    /// </summary>
    void ReserveWork(World world)
    {
        foreach (var b in _townPlaces.OutpostStandIns())
        {
            int jobs = Production.ProductionSystem.JobOperatorsOf(world, b.RuntimeKey!);
            if (jobs > 0) _workTaken[b] = _workTaken.GetValueOrDefault(b) + jobs;
        }

        foreach (var squad in world.Squads.All)
        {
            foreach (var id in squad.Members)
            {
                if (world.Characters.TryResolveNext(id, out int s) && world.Characters.Cold(s)?.Ai?.WorkPost is { } post)
                    _workTaken[post] = _workTaken.GetValueOrDefault(post) + 1;
            }
        }
    }

    /// <summary>
    /// The workplaces of <paramref name="town"/> a worker on <paramref name="type"/> may pick: <see cref="RealWork"/> for 189, all of them for 190.
    /// </summary>
    IReadOnlyList<TownBuilding> WorkplacesFor(CampaignTown town, TaskType type)
    {
        bool real = type == TaskType.AutoLabouringMines;
        if (town.Site < 0) return _townPlaces.OutpostWorkplaces(town.PlayerTown, real);

        return real ? _townPlaces.RealWorkOf(town.Site) : Places(town.Site).Workplaces;
    }

    /// <summary>
    /// The workplaces of <paramref name="town"/>: a site's, or an outpost's player-built ones (<see cref="TownPlaceIndex.OutpostWorkplaces"/>).
    /// </summary>
    internal IReadOnlyList<TownBuilding> WorkplacesOf(CampaignTown town) => WorkplacesFor(town, TaskType.AutoLabouringMinesPretend);

    /// <summary>
    /// The BuildingFunction of a building record (<see cref="WorkFunctionality.FunctionOf"/>).
    /// </summary>
    internal int FunctionOf(string record) => _work.FunctionOf(record);

    /// <summary>
    /// Whether 189 can really work at a workplace (docs/game/ai-tasks.md "Worker"). Its act-out, 87 Operating machine, needs !62
    /// <c>machineHasEmptyInputs</c>, and with an empty input the plan must fill the machine from a source of its input, so the original's workers
    /// keep to the workplaces they can run. A mine has no input; a refinery counts only with a machine running there with its inputs, as the
    /// original's workers keep off unfed ones (<b>Observed</b>, docs/game/ai-tasks.md "Worker": Rebirth's slaves on 189 work the quarries, never
    /// the four unfed Manual Stone Processors). A research bench has no empty input (no StorageBuilding cast,
    /// <c>FUN_1402adb10</c>, so state 62 is false) and a crafting bench none while no queued craft binds its inputs (<see cref="BenchRuns"/>). An
    /// outpost's workplace (the player's own machine, <see cref="TownBuilding.RuntimeKey"/>) and a town's machine also need power (state 93).
    /// </summary>
    internal static bool RealWork(World world, TownBuilding building)
    {
        if (building.RuntimeKey is { } site && world.SystemOf<Production.ProductionSystem>()?.Powered(world, site) != true)
            return false;

        if (building.Function == MineFunction) return true;
        foreach (var m in world.Machines.All)
            if (m.Position.X == building.Position.X && m.Position.Z == building.Position.Z) return !m.HasEmptyInputs && Powered(world, m);
        return BenchRuns(world, building);
    }

    /// <summary>
    /// State 93 <c>BUILDING_HAS_POWER</c> of the machine (<see cref="Production.ProductionSystem.MachinePowered"/>), a start condition of 87 and 221.
    /// </summary>
    static bool Powered(World world, Production.Machine m) => world.SystemOf<Production.ProductionSystem>()?.MachinePowered(world, m) != false;

    /// <summary>
    /// A research or crafting bench with no machine made yet whose machine would start with no input to fill: none of its recipe's inputs draws
    /// anything (a research bench has none; a crafting bench's sections draw nothing until a craft is queued, ProductionSystem.Bind), so 87 can
    /// run there.
    /// </summary>
    static bool BenchRuns(World world, TownBuilding building) =>
        building.Function is TownBuildings.ResearchFunction or TownBuildings.CraftingFunction
        && world.SystemOf<Production.ProductionSystem>()?.Data(building.BuildingId) is { } data
        && data.Inputs.All(i => i.Rate <= 0);

    /// <summary>
    /// The player-flag gate of the building finders of goals 189 / 190 (<c>FUN_1408cac60</c> / <c>FUN_1408cadb0</c>, <b>Verified</b>): the town's
    /// owner and the character must agree on being the player's faction; a player-faction character in a town that is not the player's works only as
    /// an owned slave (slave state 1).
    /// </summary>
    bool WorkAllowed(World world, int slot, CampaignTown town)
    {
        int player = world.Player.Faction >= 0 ? world.Player.Faction : data.PlayerFaction;
        if (player < 0 || world.Characters.Cold(slot) is not { } cold) return true;
        bool townIsPlayer = town.Owner(world, data) == player;
        bool charIsPlayer = cold.Faction == player;
        return townIsPlayer == charIsPlayer || charIsPlayer && cold.SlaveState == 1;
    }

    /// <summary>
    /// At its work building's <paramref name="marker"/> (<see cref="AtWork"/>): the walk there is over.
    /// </summary>
    static bool ArrivedAtWork(in CharacterHot n, AiMind mind, Vector3 marker)
    {
        if (!AtWork(in n, marker, mind.WorkRange)) return false;
        mind.Walking = false;
        mind.FailedWalks = 0;
        return true;
    }

    /// <summary>
    /// At a piece it pretend-operates (221, <see cref="PretendOperate"/>): staying home at a workstation or training dummy of the home, or sitting
    /// around at a training dummy (275 leads to 221 too).
    /// </summary>
    static bool PretendsAtHome(Blackboard board, int slot, AiBehaviour behaviour, TaskType type)
    {
        if (behaviour != AiBehaviour.Sit) return false;
        if (type == TaskType.StayInHome) return board.SeatClaims.TryGetValue(slot, out var claim) && Pretended(claim.Kind);

        return type == TaskType.SitAround && board.SitClaims.TryGetValue(slot, out var sitting) && sitting.Spot.Kind == HomeSpotKind.Training;
    }

    /// <summary>
    /// A piece operated in pretence (<c>Task_OperateMachine</c>, 221): a workstation or a training dummy.
    /// </summary>
    static bool Pretended(HomeSpotKind kind) => kind is HomeSpotKind.Workstation or HomeSpotKind.Training;

    /// <summary>
    /// At a workstation or training dummy, pretending to operate it (effect 136 IS_WORKING_PRETEND, row 221; <c>Task_OperateMachine</c>
    /// <c>FUN_14035bc90</c> treats a dummy as any machine): the piece's work clip and hunger rate, as the real operating, with its functionality's
    /// <c>animation events</c> on the clip (docs/audio.md "Machine work"); the production pass gives its XP and work step
    /// (<see cref="Production.ProductionSystem"/> <c>HomePretenders</c>).
    /// </summary>
    void PretendOperate(World world, int slot, AiMind mind, HomeSpot piece)
    {
        var clip = _work.PieceClipOf(piece);
        mind.WorkActive = true;
        mind.WorkClip = clip;
        mind.WorkFunctionality = _work.PieceFunctionalityOf(piece);
        mind.WorkHungerRate = piece.HungerRate;
        mind.PretendAt = piece;
        if (world.Characters.Cold(slot) is not { } worker) return;

        worker.ActionClip = clip;
        worker.ActionWork = StandingWork(mind.WorkFunctionality, clip, _work.ToolOf(mind.WorkFunctionality));
    }

    #endregion

    #region Acting out a goal

    /// <summary>
    /// The hand hold of a drawn crossbow.
    /// </summary>
    static readonly WeaponKinds BowHold = HandHold.OfCategory((int)Meitou.Data.Gameplay.Combat.WeaponCategory.Bow).Weapon;

    /// <summary>
    /// The radius of the ring a resident back from its town's time unloaded is put on round its spot (units; half a human's separation radius).
    /// </summary>
    const float ResumeRing = 4;

    /// <summary>
    /// A scratch copy of a plan, one per worker thread (<see cref="PlanAnew"/>).
    /// </summary>
    [ThreadStatic] static List<PlanStep>? s_planScratch;
    /// <summary>
    /// The calling worker thread's plan scratch, made on its first use.
    /// </summary>
    static List<PlanStep> PlanScratch => s_planScratch ??= [];

    /// <summary>
    /// Acts out a goal: sets the movement task it maps to, unless the character is already doing it.
    /// </summary>
    void Act(World world, int slot, AiMind mind, AiGoal goal, Blackboard board)
    {
        var table = world.Characters;

        // Nothing but the default task for one lying in its bed: it lies on with no goal, as one loaded there (Holds). The original's update
        // clears a goal left with no plan (FUN_140511130), and its default 14 plans no task in a bed (its list B !162 !157 holds and the row
        // neither acts nor has an effect, so no row needs !83 and Getting out of bed is never planned; then the mover reset, which leaves the bed
        // alone): its sleepers whose hours ended lie on with no goal or order (docs/game/ai-tasks.md "Precedence", Verified). A first aid's request
        // to stand still (62) plans no getting up either (its list B, !swim and !39, holds no bed state, TaskDataTable): the patient lies on.
        bool staysPut = ReferenceEquals(goal, Idle) || goal.Task.Type == TaskType.StandStill;
        if (staysPut && table.Cold(slot)!.InSomething == Locks.HeldState.Bed)
        {
            LieOnWithNoGoal(mind, board);
            return;
        }

        var type = goal.Task.Type;
        var behaviour = TaskBehaviours.Of(type);
        if (behaviour == AiBehaviour.None && _duties is not null && _duties.Has(type)) behaviour = AiBehaviour.Duty;

        behaviour = Install(world, slot, mind, goal, board, type, behaviour);

        // A player-faction character on a task that is not the AI's (a job's, the engine's), an NPC on its job's: the think starts nothing over it
        // on its own slot.
        var self = table.Cold(slot)!;
        bool foreign = mind.PlayerSide || OnJobTask(table.Next[slot], self);
        if (foreign && !PlayerSideHold.OwnsTask(table.Next[slot], self, mind))
        {
            ActOverTask(world, slot, mind, type, behaviour);
            return;
        }

        // An animal in a bed has no row that gets it out (116, 208, 257 are not registered for its repertoire): every goal that moves or fights fails
        // to plan, so it lies on with no goal (docs/game/ai-tasks.md "Animals in beds", Verified).
        if (behaviour != AiBehaviour.Sleep && Repertoire.StuckInBed(world, table.Cold(slot)!))
        {
            LieOnWithNoGoal(mind, board);
            return;
        }

        // Another goal gets it out of its bed (the get-up step's own think, or the goal's act-out where the plan has none); it stands where it lay.
        if (behaviour != AiBehaviour.Sleep && table.Cold(slot)!.InSomething == Locks.HeldState.Bed
            && !world.Fixtures.GetOutOfBed(world, table.IdOf(slot)))
        {
            Become(world, slot, CharacterTask.Idle);   // Task_GetOutOfBed plays the bed clip backwards first
            mind.Replan = true;
            return;
        }

        if (!PlanStep(world, slot, mind, type, ref behaviour)) return;
        if (mind.Resume && Resume(world, slot, mind)) return;

        ActOut(world, slot, mind, goal, board, type, behaviour);
    }

    /// <summary>
    /// The goal becomes the mind's current one (<see cref="TakeUp"/>, <see cref="Adopt"/>), planned again when taken up anew
    /// (<see cref="PlanAnew"/>). Returns the behaviour.
    /// </summary>
    AiBehaviour Install(World world, int slot, AiMind mind, AiGoal goal, Blackboard board, TaskType type, AiBehaviour behaviour)
    {
        var profile = TaskProfiles.Of(type);
        bool fresh = TakeUp(world, slot, mind, goal, type, profile);
        behaviour = Adopt(world, slot, mind, goal, board, type, behaviour, fresh);
        if (!fresh) return behaviour;

        PlanAnew(world, slot, mind, goal, board, profile);
        StartTask(world, slot, mind);
        return behaviour;
    }

    /// <summary>
    /// Whether the goal's act-out is a run (<see cref="FleeSystem"/>): a goal 35, or Self preservation on its step 35.
    /// </summary>
    static bool AsksRun(AiMind mind, AiBehaviour behaviour) =>
        behaviour == AiBehaviour.Flee || behaviour == AiBehaviour.SelfPreserve && mind.Step == TaskType.RunAway;

    /// <summary>
    /// A player-faction character's goal over a task that is not the AI's, an NPC's over its job's (<see cref="PlayerSideHold.OwnsTask"/>; the
    /// think's own-slot starts end no handler, <see cref="TaskStart.StartOwn"/>): a fight or a run is asked of the serial steps, whose start replaces the task as any start does;
    /// any other goal (Self preservation's turret among them) has the serial step end the task (<see cref="LeaveFights"/>: the character idle, the task's handler ended) and is acted out at
    /// the next tick, from its own stand.
    /// </summary>
    void ActOverTask(World world, int slot, AiMind mind, TaskType type, AiBehaviour behaviour)
    {
        if (behaviour == AiBehaviour.SelfPreserve && GuardsTurret(mind)) LeaveTask(mind);
        else if (behaviour == AiBehaviour.Fight || behaviour == AiBehaviour.SelfPreserve && mind.Step != TaskType.RunAway)
            AskFight(world, slot, type, mind);
        else if (behaviour is AiBehaviour.SelfPreserve or AiBehaviour.Flee)
            mind.FleeRequest = behaviour == AiBehaviour.SelfPreserve ? FleeAsk.Threat : FleeAsk.Enemies;
        else LeaveTask(mind);
    }

    /// <summary>
    /// A player-faction character's goal waits for the task it is on to end: the serial step of the same tick ends it (<see cref="LeaveFights"/>),
    /// and the character thinks again at the next tick. Writes only its own mind.
    /// </summary>
    void LeaveTask(AiMind mind)
    {
        mind.LeaveFight = true;
        Interlocked.Exchange(ref _leaving, 1);
    }

    /// <summary>
    /// The character goes on with no goal (one lying in a bed, one in a cage whose winner the AI does not act out): its plan, goal and behaviour
    /// cleared, as of the board's version.
    /// </summary>
    static void LieOnWithNoGoal(AiMind mind, Blackboard board)
    {
        mind.Plan.Clear();
        mind.Goal = null;
        mind.Behaviour = AiBehaviour.None;
        mind.Version = board.Version;
    }

    /// <summary>
    /// Whether the goal is taken up anew, and then its start over: a fresh target and node roll, the spots given up tried again, and for a goal it
    /// commits to a fresh commitment.
    /// </summary>
    static bool TakeUp(World world, int slot, AiMind mind, AiGoal goal, TaskType type, TaskProfile profile)
    {
        var table = world.Characters;

        // A goal taken up anew starts over: a fresh target and, for a goal it commits to, a fresh commitment. A planned goal stays the current one only
        // while its plan runs (Kept); once the plan is over it is no longer current (EndStep), so choosing it again is anew. A goal that is its own
        // plan and is chosen again stays current: the pick keeps a current goal whose task runs without setting it again (FUN_140510650, Verified),
        // so its commitment ends by its timers (TierRungs.Begin).
        bool fresh = mind.Goal is not { } before || before.Task.Type != type || before.Task.Target != goal.Task.Target;
        if (!fresh) return false;

        mind.GoalTick = world.Tick;
        mind.Walking = false;
        mind.FailedWalks = 0;
        mind.Target = null;
        TownTravel.Leave(mind);
        mind.Announced = false;   // a new Task_MakeAnnouncement: its said flag (+0x78) and arrival line flag (+0x7a) start clear
        mind.ArrivalSaid = false;

        // a renewed goal draws its node anew
        mind.NodeRoll = Rng.Hash(world.Seed, Rng.Key(table.IdOf(slot)), RngPurpose.Think, SeedSalts.NodePick ^ (ulong)++mind.NodeEpoch);

        // A spot given up is tried again from wherever the character is now (engine choice: the walk may have failed from the old place only).
        mind.HomeUnreachable = false;
        if (type is not (TaskType.RelaxInTownPackage or TaskType.SitAround or TaskType.SitOnThrone)) mind.SeatUnreachable = false;

        if (profile.Commits) Commit(world, slot, mind, type, profile);
        return true;
    }

    /// <summary>
    /// A committing goal becomes current: its commitment ends <see cref="TaskProfile.CommitMin"/> plus a uniform share of
    /// <see cref="TaskProfile.CommitRange"/> game hours from now (docs/game/ai-tasks.md "Desire and commitment").
    /// </summary>
    static void Commit(World world, int slot, AiMind mind, TaskType type, TaskProfile profile)
    {
        ulong roll = Rng.Hash(world.Seed, Rng.Key(world.Characters.IdOf(slot)), RngPurpose.Think,
            SeedSalts.AiCommit ^ (ulong)world.Tick << 9 ^ (ulong)type);
        mind.CommitUntil = world.GameHours + profile.CommitMin + Rng.Float(roll) * profile.CommitRange;
    }

    /// <summary>
    /// A step of the current goal starts (the task start <c>FUN_14050d540</c> -> <c>FUN_14050d130</c>, <b>Verified</b>): when its commit step
    /// (<see cref="CommitStep"/>) commits, the task's timer (<see cref="AiMind.TaskCommitUntil"/>) runs from now for that row's
    /// <see cref="TaskProfile.CommitMin"/> plus a uniform share of <see cref="TaskProfile.CommitRange"/>; else it is left as it is.
    /// </summary>
    internal static void StartTask(World world, int slot, AiMind mind)
    {
        if (CommitStep(mind) is { } type) RollTaskTimer(world, slot, mind, type);
    }

    /// <summary>
    /// Rolls the task's timer (<see cref="AiMind.TaskCommitUntil"/>) from now for <paramref name="type"/>'s commitment when that row commits
    /// (<c>FUN_14050d130</c>, <b>Verified</b>); else leaves it as it is.
    /// </summary>
    static void RollTaskTimer(World world, int slot, AiMind mind, TaskType type)
    {
        if (TaskProfiles.Of(type) is not { Commits: true } profile) return;

        ulong roll = Rng.Hash(world.Seed, Rng.Key(world.Characters.IdOf(slot)), RngPurpose.Think,
            SeedSalts.AiTaskCommit ^ (ulong)world.Tick << 9 ^ (ulong)type);
        mind.TaskCommitUntil = world.GameHours + profile.CommitMin + Rng.Float(roll) * profile.CommitRange;
    }

    /// <summary>
    /// The row whose commitment the running task carries (<c>FUN_14050cff0</c>, <b>Verified</b>): the running step, or the next queued one when the
    /// running row does not commit and the next has an effect (a goal row: the walk to a goal's place carries the goal's commitment). A goal that is
    /// its own plan is its own task. None without a goal or a step.
    /// </summary>
    internal static TaskType? CommitStep(AiMind mind)
    {
        if (mind.Goal is not { } goal) return null;
        if (!PlanContext.Stepped(goal)) return goal.Task.Type;
        if (mind.Plan.Count == 0) return null;

        var running = mind.Plan[0].Type;
        if (mind.Plan.Count == 1 || TaskProfiles.Of(running).Commits) return running;

        var next = mind.Plan[1].Type;
        return TaskDataTable.Of(next)?.Effect is not null ? next : running;
    }

    /// <summary>
    /// Whether the running task's commitment ends it now (<c>FUN_14050f0b0</c>, <b>Verified</b>): its commit step commits with TaskData
    /// <c>+0x21</c> (<see cref="TaskProfile.EndsWithTask"/>) and the task's timer has passed.
    /// </summary>
    internal static bool TaskCommitOver(World world, AiMind mind) =>
        CommitStep(mind) is { } type && TaskProfiles.Of(type) is { Commits: true, EndsWithTask: true }
        && world.GameHours >= mind.TaskCommitUntil;

    /// <summary>
    /// A goal installed in a fight (<see cref="InFight"/>) that commits and was not the current one rolls its commitment, as one acted out does
    /// (<see cref="TakeUp"/>): the original sets it whenever a committing goal becomes current. The fight is the goal's own task, so the task's
    /// timer is rolled for the goal's row (<see cref="RollTaskTimer"/>), as its start would.
    /// </summary>
    static void CommitInFight(World world, int slot, AiMind mind, AiGoal goal)
    {
        var type = goal.Task.Type;
        var profile = TaskProfiles.Of(type);
        if (!profile.Commits) return;
        if (mind.Goal is { } before && before.Task.Type == type && before.Task.Target == goal.Task.Target) return;

        Commit(world, slot, mind, type, profile);
        RollTaskTimer(world, slot, mind, type);
    }

    /// <summary>
    /// A planned goal taken up anew is planned again once its start is made (a fresh target and node roll, the spots given up tried again), so the plan
    /// it keeps (<see cref="Kept"/>) walks to where its act-out goes: the scorer clears a candidate's target before it is chosen and its finder draws
    /// it anew (<c>FUN_14050df10</c>). The choice's plan stays if this one fails.
    /// </summary>
    void PlanAnew(World world, int slot, AiMind mind, AiGoal goal, Blackboard board, TaskProfile profile)
    {
        if (!PlanContext.Stepped(goal)) return;

        var type = goal.Task.Type;
        float desire = Desire(world, TaskBehaviours.Of(type), type, profile, mind.Squad, board, mind, world.Characters.IdOf(slot), goal.Task.Target);
        var kept = PlanScratch;
        kept.Clear();
        kept.AddRange(mind.Plan);
        if (PlanContext.Local.Bind(this, world, slot, mind, board).TryPlan(goal, desire)) return;

        mind.Plan.Clear();
        mind.Plan.AddRange(kept);
    }

    /// <summary>
    /// The mind takes the goal and its behaviour (staying home on a claimed chair sits); what the last goal left is cleared: the wander radius, the
    /// sitting clip, the work it stopped and the follow target. Returns the behaviour.
    /// </summary>
    AiBehaviour Adopt(World world, int slot, AiMind mind, AiGoal goal, Blackboard board, TaskType type, AiBehaviour behaviour, bool fresh)
    {
        var table = world.Characters;

        // Staying home sits on the chair its squad claimed for it (the original's 43 operates home furniture, chairs among it); without one, or at a
        // workstation without power (221 has no plan there once home: state 93), it stands at home.
        if (type == TaskType.StayInHome && board.SeatClaims.ContainsKey(slot) && !mind.SeatUnreachable && !board.UnpoweredSeats.Contains(slot))
            behaviour = AiBehaviour.Sit;
        if (behaviour == AiBehaviour.Investigate && (fresh || mind.Behaviour != AiBehaviour.Investigate))
            mind.LookAbout = Crime.CrimeSystem.FirstLookAboutSeconds;

        mind.Goal = goal;
        mind.Behaviour = behaviour;
        mind.Version = board.Version;
        mind.WanderRadius = 0;
        mind.SitClip = null;   // set again below while it stays on its chair
        mind.SitHungerRate = 1;

        if (mind.WorkActive && behaviour != AiBehaviour.Work && !PretendsAtHome(board, slot, behaviour, type)) StopWork(table.Cold(slot), mind);

        mind.FollowTarget = CharacterId.None;   // set again below for a follow goal aimed at the squad's mission
        return behaviour;
    }

    /// <summary>
    /// It stopped working: the next pick is a new one, and the work clip (with its work events and the body's work hunger rate) goes.
    /// </summary>
    static void StopWork(CharacterCold? worker, AiMind mind)
    {
        mind.WorkActive = false;
        mind.WorkEpoch++;

        if (worker is not null && mind.WorkClip is not null && worker.ActionClip == mind.WorkClip)
        {
            worker.ActionClip = null;
            worker.ActionWork = null;
        }

        mind.WorkClip = null;
        mind.WorkFunctionality = null;
    }

    /// <summary>
    /// The steps of the plan before the goal's own act-out: getting out of bed, sheathing and drawing (one think each), putting a carried body
    /// down, the walks home, out of the home and near the target, the ditch request, and a way through a locked door (the lock system's, <see cref="TakeDoorStep"/>). False when the
    /// step took the think (the act-out waits); <paramref name="behaviour"/> may become the walk home. A row the engine goal <paramref name="type"/>'s
    /// system registered (<see cref="DutyGoals.Register"/>: 78, 156, 200, 93 in some goals' plans) is that act-out's, never one of these, but a way
    /// through a door the lock system put in front of the plan (<see cref="IsDoorWay"/>).
    /// </summary>
    bool PlanStep(World world, int slot, AiMind mind, TaskType type, ref AiBehaviour behaviour)
    {
        var table = world.Characters;
        if (behaviour == AiBehaviour.Duty && _duties!.EntryOf(type)?.Acts(mind.Step) == true && !IsDoorWay(mind.Plan[0])) return true;

        // Steps of the plan before the goal's own act-out: getting out of bed, sheathing (one think each); the walk and the rest are the goal's.
        switch (mind.Step)
        {
            case TaskType.GetOutOfBed or TaskType.GetOutOfBedIfItsEmergency or TaskType.GetOutOfBedOnceHealed:
                Become(world, slot, CharacterTask.Idle);
                StepDone(mind);
                return false;
            case TaskType.UnequipWeapon:
                table.Cold(slot)!.DrawnWeapon = WeaponKinds.None;
                StepDone(mind);
                return false;
            case TaskType.EquipWeapon or TaskType.EquipCrossbow:
                EquipStep(world, slot, mind);
                return false;
            case TaskType.PutDownObject:
                // 69 Putting down before a goal that needs empty hands (list B !47): the serial step has the carry system put the body down
                // (PutDownForSteps); the step ends by its list A (!48 !47) once the hands are empty.
                Become(world, slot, CharacterTask.Idle);
                mind.PutDownRequest = true;
                Interlocked.Exchange(ref _puttingDown, 1);
                return false;
            case TaskType.GoHomebuilding:
                // 19 Going home before the goal (43 Staying home and the like away from home): the walk home of the GoHome act-out.
                behaviour = AiBehaviour.GoHome;
                break;
            case TaskType.TravelToTargetTown or TaskType.TravelToTargetTownFast:
                // 53 / 130 Task_TravelToTargetTown before the goal (39's and 36's plans): the travel walk (TownTravel), until its list A holds.
                behaviour = AiBehaviour.Travel;
                break;
            case TaskType.EnterBuilding:
                // 156 Task_EnterBuilding: the walk into its home building (the GoHome walk), done once inside.
                behaviour = AiBehaviour.GoHome;
                break;
            case TaskType.LeaveBuilding when mind.Squad.Ai?.Home is { } leaving && world.Sight.BuildingOf(slot) == leaving.Key:
                // 200 Task_LeaveBuilding: to 1.1 of the door's span past its outer point (A + (A - B) x 1.1) of the home's first door, until outside.
                if (LeavePoint(world, leaving) is { } outside
                    && !GoToSpot(world, slot, mind, outside, GiveUp.Target, SpeedMode.Walk, slack: 10))
                {
                    return false;
                }

                break;
            case TaskType.MoveOnFreeWill or TaskType.MoveOnFreeWillFast or TaskType.FillMachine when mind.Plan[0].Target.Kind == Targets.Store:
                // Step 93 into the ditch target (rows 113, 114, 242), and the walk to it: the town duty system walks there and puts the carried items
                // in while the step runs (RunsDitchStep), then the AI plans again. Its walk under way goes on.
                if (Kidnap.TownDutySystem.Ditches(table.Next[slot], table.Cold(slot)!)) return false;

                mind.DitchRequest = true;
                Become(world, slot, CharacterTask.Idle);
                return false;
            case TaskType.MoveOnFreeWill or TaskType.MoveOnFreeWillFast or TaskType.CollectOutputResource when IsFetchStep(mind.Plan[0]):
                // Step 92 from row 94's loose item or storage, and the walk to it: the town duty system walks there and takes the item while the step
                // runs (RunsFetchStep), then the AI plans again. Its walk or pick-up under way goes on.
                if (Kidnap.TownDutySystem.Fetches(table.Next[slot], table.Cold(slot)!)) return false;

                mind.FetchRequest = true;
                Become(world, slot, CharacterTask.Idle);
                return false;
            case TaskType.UnlockDoor or TaskType.UnlockDoorHere or TaskType.BashDoor or TaskType.BashDoorHere or TaskType.PickLock
                or TaskType.CutLock:
                // A way through a locked door (TakeDoorStep): the lock system works it while the step runs; one it dropped or failed is over.
                if (table.Cold(slot)!.DoorWay is null) mind.Plan.RemoveAt(0);
                return false;
            case TaskType.GetNearTo when !mind.Plan[0].Target.IsNone:
                // 40 Following to within 10 of the plan's target, then the goal's own act-out.
                if (!GoToSpot(world, slot, mind, Flat(mind.Plan[0].Target.Position), GiveUp.Target, SpeedMode.Walk, slack: 10)) return false;

                break;
        }

        return true;
    }

    /// <summary>
    /// Task_EquipBestWeapon (6 melee only, 264 ranged only; <c>FUN_140346c30</c>, Verified): the matching weapon it carries comes out, else the
    /// hand is emptied; the step is done.
    /// </summary>
    void EquipStep(World world, int slot, AiMind mind)
    {
        var table = world.Characters;
        var armed = table.Cold(slot)!;
        bool ranged = mind.Step == TaskType.EquipCrossbow;
        if (armed.Fighter is { } fighter)
            armed.DrawnWeapon = !ranged ? Combat.CombatSystem.MeleeDrawn(fighter) : fighter.Crossbow is not null ? BowHold : WeaponKinds.None;

        // Neither the back nor the hip gave one: an NPC of a non-player faction takes the first matching weapon of its inventory (s08-f09).
        if (armed.DrawnWeapon == WeaponKinds.None && DrawFromInventory(world, armed, ranged) && armed.Fighter is { } rebuilt)
            armed.DrawnWeapon = !ranged ? Combat.CombatSystem.MeleeDrawn(rebuilt) : rebuilt.Crossbow is not null ? BowHold : WeaponKinds.None;

        StepDone(mind);
    }

    /// <summary>
    /// Back from its town's time unloaded (<see cref="AiMind.Resume"/>, cleared here): it is already where the plan's walk ends, put there by the
    /// serial step when that is walkable. True when it is to be placed (the act-out waits).
    /// </summary>
    bool Resume(World world, int slot, AiMind mind)
    {
        // Back from its town's time unloaded: it is already where the plan's walk ends (docs/simulation.md "Off-screen tier", residents'
        // schedule).
        mind.Resume = false;
        if (!(mind.Step is TaskType.MoveOnFreeWill or TaskType.MoveOnFreeWillFast && !mind.Plan[0].Target.IsNone)) return false;

        var end = mind.Plan[0].Target.Position;

        // On a ring round the spot by member (engine choice): members sharing a bed or node are not put on one point, where separation would
        // throw them off it.
        float angle = mind.MemberIndex * 2.39996f;
        end += new Vector3(MathF.Sin(angle), 0, MathF.Cos(angle)) * ResumeRing;
        var walk = world.Walkability;
        var at = !float.IsNaN(end.Y) && walk.FloorNear(end.X, end.Z, end.Y, FloorSlack) is { } floor ? floor
            : new Vector3(end.X, float.IsNaN(end.Y) ? walk.GroundHeight(end.X, end.Z) : end.Y, end.Z);

        // Only onto walkable ground: a spot it could not walk to either is left to the walk and its give-up rule. The serial step puts it
        // there (after the movement, which steps from the last tick's place), and it thinks again on the next tick, from the spot.
        if (!walk.IsWalkable(at.X, at.Z, at.Y)) return false;

        mind.PlaceAt = at;
        mind.Version = -1;
        Interlocked.Exchange(ref _placing, 1);
        Become(world, slot, CharacterTask.Idle);
        return true;
    }

    /// <summary>
    /// The goal's own act-out by its behaviour (docs/game/ai-tasks.md): the task it maps to, the walk to its spot and what it does there.
    /// </summary>
    void ActOut(World world, int slot, AiMind mind, AiGoal goal, Blackboard board, TaskType type, AiBehaviour behaviour)
    {
        var table = world.Characters;
        // Only 215's formation and the travel steps cap the pace; a travel step's detour goes with the step.
        if (behaviour != AiBehaviour.Travel) table.Cold(slot)!.PaceCap = null;
        if (behaviour != AiBehaviour.Travel) TownTravel.Leave(mind);

        switch (behaviour)
        {
            case AiBehaviour.Mill:
                // Idleness is Task_Blank in the original: it stands where it is (the mover reset, CharMovement vslot 0x98 FUN_14065fc70, Verified);
                // no stroll.
                Become(world, slot, CharacterTask.Idle);
                break;
            case AiBehaviour.Follow:
                mind.FollowTarget = goal.Task.Target == TaskTargetType.SquadMission ? board.MissionTarget : CharacterId.None;
                Become(world, slot, CharacterTask.Follow);
                break;
            case AiBehaviour.Patrol:
                ActPatrol(world, slot, mind, board);
                break;
            case AiBehaviour.Sleep when table.Cold(slot)!.InSomething == Locks.HeldState.Bed:
                SleepInBed(world, slot, type);
                break;
            case AiBehaviour.Post:
            case AiBehaviour.Sleep:
            case AiBehaviour.GoHome:
                ActAtSpot(world, slot, mind, board, type, behaviour);
                break;
            case AiBehaviour.TownNode:
            case AiBehaviour.Shop:
            case AiBehaviour.ToTown:
                ActInTown(world, slot, mind, board, behaviour);
                break;
            case AiBehaviour.PatrolTown:
                Patrol(world, slot, mind, board);
                break;
            case AiBehaviour.Work:
                ActWork(world, slot, mind, board, type);
                break;
            case AiBehaviour.Investigate:
                Investigate(world, slot, mind, board, type);
                break;
            case AiBehaviour.Assault:
                ActAssault(world, slot, mind, board);
                break;
            case AiBehaviour.Medic:
                ActMedic(world, slot, mind);
                break;
            case AiBehaviour.HealLegs:
                ActHealLegs(world, slot, mind);
                break;
            case AiBehaviour.Duty:
                ActDuty(world, slot, mind, goal);
                break;
            case AiBehaviour.SelfPreserve when GuardsTurret(mind):
                ActGuardTurret(world, slot, mind);
                break;
            case AiBehaviour.Fight:
            case AiBehaviour.SelfPreserve when mind.Step != TaskType.RunAway:
                // Stands while the serial step starts the fight on the finder's pick (Self preservation's: row 13's attacker of itself; the combat
                // system walks it there and fights); the request is its own mind's.
                Become(world, slot, CharacterTask.Idle);
                AskFight(world, slot, type, mind);
                break;
            case AiBehaviour.SelfPreserve:
            case AiBehaviour.Flee:
                // Task_Runaway: the serial flee step takes the request, stops a fight and starts the first run (FleeSystem), then hops until it is
                // safe.
                mind.FleeRequest = behaviour == AiBehaviour.SelfPreserve ? FleeAsk.Threat : FleeAsk.Enemies;
                break;
            case AiBehaviour.Travel:
                ActTravel(world, slot, mind, board, type);
                break;
            case AiBehaviour.Announce:
                ActAnnounce(world, slot, mind);
                break;
            case AiBehaviour.Relax:
            case AiBehaviour.Sit:
                ActSeated(world, slot, mind, board, type);
                break;
            case AiBehaviour.Crouch:
                // It stands where it is; the animation system plays the clip and sets the stance.
                Become(world, slot, CharacterTask.Idle);
                break;
            case AiBehaviour.Turret:
                ActTurret(world, slot, mind, board);
                break;
        }
    }

    /// <summary>
    /// A sleeper already in its bed: the goal takes the sleep as its own.
    /// </summary>
    static void SleepInBed(World world, int slot, TaskType type)
    {
        var table = world.Characters;

        // Lying in its bed. One loaded there, or laid in by the walk's end, sleeps by no start of this goal: the goal takes the sleep as its
        // own (the original's sleeper runs its plan's 98 Task_UseBed).
        if (table.Cold(slot)!.Running is not { Source: TaskSource.Goal } own || !own.Runs(CharacterTask.Sleep))
        {
            TaskStart.StartOwn(world, slot, new TaskRequest(CharacterTask.Sleep, type, TaskSource.Goal));
            SquadAutonomy.Stamp(world, slot);
        }
    }

    /// <summary>
    /// An assault: it stands, the member to attack picked for the serial step.
    /// </summary>
    void ActAssault(World world, int slot, AiMind mind, Blackboard board)
    {
        // Stands until the serial step sends the attack on the member picked (the combat system walks it there).
        Become(world, slot, CharacterTask.Idle);
        mind.AssaultTarget = AssaultTargetFor(world, slot, board);
        if (!mind.AssaultTarget.IsNone) Interlocked.Exchange(ref _assaulting, 1);
    }

    /// <summary>
    /// A medic: it stands, asking the serial step for a patient.
    /// </summary>
    void ActMedic(World world, int slot, AiMind mind)
    {
        // Stands while the serial step starts the treatment of the patient its finder gives then (FirstAidSystem.SendGoalTreatments).
        Become(world, slot, CharacterTask.Idle);
        mind.MedicAsk = true;
        Interlocked.Exchange(ref _treating, 1);
    }

    /// <summary>
    /// An engine-acted goal (<see cref="AiBehaviour.Duty"/>): the one holding the character goes on as its system acts it out; a goal chosen anew
    /// stands on its row while the serial step asks the system to start it (<see cref="DutyGoals"/>); a code mod's on row 14
    /// (<see cref="StandOnIdleRow"/>).
    /// </summary>
    void ActDuty(World world, int slot, AiMind mind, AiGoal goal)
    {
        if (mind.Duty is { } running && DutyGoals.Same(running, goal)) return;

        if (_duties!.IsMod(goal.Task.Type)) StandOnIdleRow(world, slot);
        else Become(world, slot, CharacterTask.Idle);

        _duties.Ask(mind);
    }

    /// <summary>
    /// The travel goals: a travel step (53, 130: 39's, 36's and the travel goals' own plans) is <see cref="TownTravel"/>'s walk; 39 in its town
    /// stands; 215 under a campaign's war package is the formation's walk (<see cref="ActFormation"/>), elsewhere a trip to the mission town's
    /// centre.
    /// </summary>
    void ActTravel(World world, int slot, AiMind mind, Blackboard board, TaskType type)
    {
        if (type == TaskType.TravelToTargetPackage && PlatoonOf(world, mind.Squad) is { } platoon && MissionFormation.Runs(board, platoon))
        {
            ActFormation(world, slot, mind, board, platoon);
            return;
        }

        if (mind.Step is TaskType.TravelToTargetTown or TaskType.TravelToTargetTownFast)
        {
            _travel.Act(this, world, slot, mind, board, mind.Step);
            return;
        }

        // 39 in its town: its plan is the row alone, which neither acts nor has an effect, so no task starts (FUN_14050d540) and it stands.
        if (type == TaskType.WanderingTrader)
        {
            world.Characters.Cold(slot)!.PaceCap = null;
            TownTravel.Leave(mind);
            Become(world, slot, CharacterTask.Idle);
            return;
        }

        // 215 outside a war package: to the mission town's centre; the goal is no longer wanted once inside the town (Desire).
        world.Characters.Cold(slot)!.PaceCap = null;
        TownTravel.Leave(mind);
        if (DestinationCentre(world, board) is not { } town) Become(world, slot, CharacterTask.Idle);
        else if (GoToSpot(world, slot, mind, town, GiveUp.Target, SpeedMode.Walk)) Become(world, slot, CharacterTask.Idle);
    }

    /// <summary>
    /// A package's speed mode (vtable slot 3, <b>Verified</b> per class): 4 (no change) for the base package, patrols, bars, cannibals at home,
    /// prisoner gathering, the trader, occupation, the war base and <c>Contract_Timed</c>; 2 for <c>RetreatAndRefresh</c>,
    /// <c>GoOutOnPatrol_Running</c>, <c>Contract_UntilExSlave</c>, <c>Contract_UntilOutOfDanger</c>, the passing assault and the war assaults; 1
    /// for <c>Contract_HiredAlly</c>, both base defences, the cannibal retreats and the refugee; 0 for the slaver shipping. A class Meitou does not
    /// build reads 4 (engine choice).
    /// </summary>
    internal static int PackageSpeed(AiPackage package) => PackageClasses.Of(package.Signal) switch
    {
        PackageClass.Retreat or PackageClass.PatrolRunning or PackageClass.PassingAssault => 2,
        PackageClass.WarAssault or PackageClass.WarAssaultCannibal => 2,
        PackageClass.CannibalRetreat or PackageClass.Refugee or PackageClass.CampaignDefend => 1,
        PackageClass.SlaverShipping => 0,
        PackageClass.Contract => package.Signal switch
        {
            12 or 15 => 1,
            13 or 14 => 2,
            _ => 4,
        },
        _ => 4,
    };

    /// <summary>
    /// The running package starts again (its vtable <c>+0x30</c>, as a travel step with no town calls it): its class's start
    /// (<see cref="Rules.Started"/>; the base class's sets nothing Meitou keeps, its mission town being the home town).
    /// </summary>
    internal void RestartPackage(World world, Squad squad)
    {
        if (squad.Ai is not { Current: { } running } board) return;

        var kind = board.Contract is not null ? PackageClasses.OfOverride(running.Signal) : PackageClasses.Of(running.Signal);
        _rules.Bind(this, world, squad);
        _rules.Started(board, running, kind);
    }

    /// <summary>
    /// The running package's path-impossible call (vtable <c>+0x10</c>, <b>Verified</b> per class): the trader starts again (<c>FUN_140286730</c>, a
    /// new town); the bar classes' (<c>FUN_140295990</c>) only move a hand between their own maps, and every other class's does nothing
    /// (<c>FUN_140285870</c>).
    /// </summary>
    internal void PathImpossible(World world, Squad squad)
    {
        if (squad.Ai is { CurrentClass: PackageClass.WanderingTrader }) RestartPackage(world, squad);
    }

    /// <summary>
    /// A patrol (<c>Task_Patrol</c>'s move, docs/game/factions-squads-towns.md "Patrol task"): the leader walks to the patrol point exactly, which
    /// the population's patrol step keeps in <see cref="Squad.PatrolPoint"/> and redraws once it is reached; until the first is drawn it stands.
    /// The others follow. Under a running patrol package the leader runs (<c>GoOutOnPatrol_Running</c>'s speed mode 2, <c>FUN_140289cb0</c>; the
    /// other packages' 4 changes nothing).
    /// </summary>
    static void ActPatrol(World world, int slot, AiMind mind, Blackboard board)
    {
        var table = world.Characters;
        bool leader = mind.Squad.Leader == table.IdOf(slot);
        if (!leader)
        {
            Become(world, slot, CharacterTask.Follow);
            return;
        }

        // Until the first point is drawn, and once the move to this one is over (arrived, given up or stopped: Task_Patrol's update draws the next
        // when its mover is idle, reached or failed, FUN_1405c6b30 mode 1), it stands for the population's patrol step to draw again.
        if (mind.Squad.PatrolPoint is not { } point || PatrolMoveOver(table.Next[slot], point))
        {
            Become(world, slot, CharacterTask.Idle);
            return;
        }

        WalkTo(world, slot, point, board.CurrentClass == PackageClass.PatrolRunning ? SpeedMode.Run : SpeedMode.Walk);
    }

    /// <summary>
    /// Whether a patrol leader's move to <paramref name="point"/> is over: its walk went there (the goal it keeps) and it walks no more, so it
    /// arrived, gave the walk up (no way there, blocked) or was stopped (<c>FUN_1405c6b30</c> mode 1 goes on when <c>CharMovement</c> slot
    /// <c>0x50</c>, <c>0x60</c> or <c>0xd8</c> holds, <b>Verified</b>; read as a failed path, an arrival and no path, <b>Observed</b>).
    /// </summary>
    internal static bool PatrolMoveOver(in CharacterHot leader, Vector2 point) =>
        leader.Task != (byte)CharacterTask.GoTo && Vector2.DistanceSquared(leader.Goal, point) < 1;

    /// <summary>
    /// The walk to its spot (a post, a bed, home; going to bed without a bed spot at home an occupier's bed of the town it holds), then resting or
    /// standing there, laid in its bed by the serial step, facing out of the gate or its node's way.
    /// </summary>
    void ActAtSpot(World world, int slot, AiMind mind, Blackboard board, TaskType type, AiBehaviour behaviour)
    {
        var table = world.Characters;

        // Going to bed without a bed spot at home: an occupier's bed of the town it holds, else the NPC bed search.
        var spare = behaviour == AiBehaviour.Sleep && type is TaskType.GoHomeAndGoToBed or TaskType.GoHomeAndGoToBedSecure
            ? SpareBed(world, board, mind, slot) : null;
        var (target, home, floor, facing, watch) = spare is not null
            ? (Flat(spare.Position), true, spare.Position.Y, (float?)null, false)
            : SpotFor(world, mind, board, behaviour, type);
        var speed = type is TaskType.RunAwayHometown or TaskType.TravelToTargetTownFast ? SpeedMode.Run : SpeedMode.Walk;
        if (!GoToSpot(world, slot, mind, target, home ? GiveUp.Home : GiveUp.Post, speed, height: floor)) return;

        Become(world, slot, behaviour == AiBehaviour.Sleep ? CharacterTask.Rest : CharacterTask.Idle);

        // At its home's bed: the serial step lies it in (Task_UseBed via 98 Sleeping); resting on the floor stays the fallback when no
        // bed is free.
        if (behaviour == AiBehaviour.Sleep && home && floor is not null && (spare?.Id ?? BedSpotOf(board, mind)) is { } bed)
        {
            mind.BedAt = bed;
            Interlocked.Exchange(ref _bedding, 1);
        }

        // looking out
        if (home && board.Gate is { } gate && GateSpot(board, mind, type) is not null)
            table.Next[slot].Yaw = MathF.Atan2(gate.Out.X, gate.Out.Y);
        else if (watch && mind.Step == TaskType.StandAtNode) Watch(world, slot, facing);
        else if (facing is { } yaw) table.Next[slot].Yaw = yaw;   // at a node, facing its way
    }

    /// <summary>
    /// Whether a character standing at the spot watches who it senses: a shopkeeper node or a guard post (node types 0 and 2, <c>FUN_14034d7c0</c>).
    /// </summary>
    static bool Watches(HomeSpot spot) => spot.Kind is HomeSpotKind.Shopkeeper or HomeSpotKind.Guard;

    /// <summary>
    /// <c>Task_StandAtNode</c>'s update at a shopkeeper node or a guard post (<c>FUN_140354b90</c> -> <c>FUN_14034d7c0</c>, <b>Verified</b>): it looks
    /// at the first sensed character it watches (<see cref="Watched"/>), else faces the node's way (the barman over his counter). The look
    /// (Character vfunc <c>0x3b0</c>, <c>FUN_1405c9e70</c>) turns a human's head while the place is within 90 degrees of its heading
    /// (<c>AnimationClassHuman</c> vfunc <c>0xb8</c>, <c>FUN_1405b8430</c>, the <c>head turning</c> clip), else its body (<c>CharMovement</c> vfunc
    /// <c>0x38</c>), and an animal's not at all (the base <c>AnimationClass</c> slot returns 1). The head turn is not simulated: the body keeps its
    /// heading.
    /// </summary>
    static void Watch(World world, int slot, float? nodeYaw)
    {
        var table = world.Characters;
        ref var self = ref table.Next[slot];

        if (Watched(world, slot) is not { } at)
        {
            if (nodeYaw is { } yaw) self.Yaw = yaw;
            return;
        }
        if (SquadRegistry.IsAnimal(table.Cold(slot)!)) return;

        var gap = at - self.Position;
        if (gap.X * gap.X + gap.Z * gap.Z < 1e-4f) return;

        float toward = MathF.Atan2(gap.X, gap.Z);
        if (MathF.Abs(Session.Interp.WrapAngle(toward - self.Yaw)) > MathF.PI / 2) self.Yaw = toward;
    }

    /// <summary>
    /// Where the first sensed character a node watcher looks at stands (<c>FUN_140853960</c> with masks 6 and 0x688, <b>Verified</b>): tagged neutral
    /// or enemy, not down (bit 3), not of NPC class law enforcement or military (bit 7), not imprisoned (bit 9) and not of the watcher's faction
    /// (bit 10); its place now when heard at the last look (entry <c>+0x39</c>), else where it was last sensed. The entries are taken in slot order
    /// (the original's list is a hash container's); null for none.
    /// </summary>
    static Vector3? Watched(World world, int slot)
    {
        if (world.SystemOf<AggroSystem>()?.SensesOf(slot) is not { } senses) return null;

        var table = world.Characters;
        int faction = table.Cold(slot)!.Faction;
        foreach (var e in senses.Entries)
        {
            if ((e.Tags & (SenseTag.Neutral | SenseTag.Enemy)) == 0 || (e.Tags & (SenseTag.Ko | SenseTag.Imprisoned)) != 0) continue;
            if (!table.TryResolveNext(new CharacterId(e.Target, e.Generation), out int t) || table.Cold(t) is not { } tc) continue;
            if (tc.Faction == faction || tc.NpcClass is FundamentalType.LawEnforcement or FundamentalType.Military) continue;

            return e.Heard ? table.Previous[t].Position : e.Place;
        }

        return null;
    }

    /// <summary>
    /// The town goals' walk to their target (a node, a shop, the town): the target is found once per goal, and at a shop its buying starts.
    /// </summary>
    void ActInTown(World world, int slot, AiMind mind, Blackboard board, AiBehaviour behaviour)
    {
        var table = world.Characters;

        // A shop picked is remembered, so the arrival knows what it sells (the same pick as the target's: the roll is this tick's).
        if (mind.Target is null && behaviour == AiBehaviour.Shop)
        {
            var (places, _, random) = ShopTown(world, slot, board.Town);
            mind.ShopAt = ShopFor(places, mind, TargetRoll(world, table.IdOf(slot), behaviour), random);
        }

        // An outpost's building no longer offered since the walker drew it (locked, private, gone): drawn again (engine choice; the squad keeps
        // to the player's locks).
        bool withdrawn = behaviour == AiBehaviour.TownNode && mind.Target is { } drawn && NoLongerOffered(world, board.HomeTown, drawn);
        if (withdrawn) mind.Target = null;

        mind.Target ??= TargetFor(world, table.IdOf(slot), behaviour, board.HomeTown, mind)
            ?? (behaviour == AiBehaviour.TownNode ? OccupiedSpot(world, board, table.IdOf(slot)) : null);

        if (mind.Target is not { } goTo) Become(world, slot, CharacterTask.Idle);
        // a building without a counter: near its origin, on any floor
        else if (GoToSpot(world, slot, mind, Flat(goTo), GiveUp.Target, SpeedMode.Walk,
            float.IsNaN(goTo.Y) ? WorkReach : 0, behaviour == AiBehaviour.ToTown || float.IsNaN(goTo.Y) ? null : goTo.Y))
        {
            Become(world, slot, CharacterTask.Idle);

            // At the shop (Task_Shopping 119 starts): the first unit is bought a second on, then one a second (BuyForShoppers).
            if (behaviour == AiBehaviour.Shop && mind.ShopAt is not null && mind.NextBuyTick == 0)
            {
                mind.NextBuyTick = world.Tick + BuyTicks(world);
                Interlocked.Exchange(ref _shopping, 1);
            }
        }
        else if (behaviour == AiBehaviour.Shop) mind.NextBuyTick = 0;   // walking again: the task is not running
    }

    /// <summary>
    /// The workers' goals (189, 190): the workplace whose slot it holds (else a pick) and its clip, functionality and hunger rate; at the building
    /// (or the plan step's), facing it at its work clip, working the machine.
    /// </summary>
    void ActWork(World world, int slot, AiMind mind, Blackboard board, TaskType type)
    {
        var table = world.Characters;
        ref var n = ref table.Next[slot];
        mind.WorkOperate = null;
        mind.CollectFrom = null;
        mind.FillTo = null;

        // The workplace whose slot it holds wins over the pick it walks to (ClaimWork).
        if (mind.WorkPost is { } post && mind.Target is { } aim && (aim.X != post.Position.X || aim.Z != post.Position.Z)) mind.Target = null;
        if (mind.Target is null) PickWorkplace(world, slot, mind, board, type);

        var stepAt = StepBuilding(mind, WorkTownOf(mind));
        Vector3? working = stepAt is not null && stepAt != mind.WorkBuilding
            ? new Vector3(stepAt.Position.X, float.NaN, stepAt.Position.Z) : mind.Target;
        var aimed = stepAt is not null && stepAt != mind.WorkBuilding ? stepAt : mind.WorkBuilding;
        mind.WorkRange = aimed is null ? WorkReach : WorkRangeOf(aimed);

        if (working is not { } workAt)
        {
            Become(world, slot, CharacterTask.Idle);
            return;
        }

        // It walks to the building's use node nearest it, and its reach is measured from there (getPositionMarker).
        var marker = aimed is null ? workAt : WorkMarker(world, aimed, n.Position);
        if (!ArrivedAtWork(in n, mind, marker) && !GoToSpot(world, slot, mind, Flat(marker), GiveUp.Target, SpeedMode.Walk, mind.WorkRange))
            return;

        // At the building: facing it, at its work clip.
        Become(world, slot, CharacterTask.Idle);
        var face = workAt - n.Position;
        if (face.X * face.X + face.Z * face.Z > 1e-4f) n.Yaw = MathF.Atan2(face.X, face.Z);

        if (table.Cold(slot) is { } workerCold)
        {
            workerCold.ActionClip = mind.WorkClip;
            workerCold.ActionWork = StandingWork(mind.WorkFunctionality, mind.WorkClip, _work.ToolOf(mind.WorkFunctionality));
        }

        WorkAtMachine(mind, type, stepAt);
    }

    /// <summary>
    /// A worker without a target takes up work again (a new pick when it was working): the workplace whose slot it holds, else one picked, with its
    /// clip, functionality, hunger rate and owner.
    /// </summary>
    void PickWorkplace(World world, int slot, AiMind mind, Blackboard board, TaskType type)
    {
        var table = world.Characters;
        if (mind.WorkActive) mind.WorkEpoch++;

        mind.WorkActive = true;
        var town = WorkTownOf(mind);
        if ((mind.WorkPost ?? PickWork(world, table.IdOf(slot), town, mind.WorkEpoch, type)) is not { } building) return;

        mind.Target = new Vector3(building.Position.X, float.NaN, building.Position.Z);
        mind.WorkClip = _work.ClipOf(building);
        mind.WorkFunctionality = _work.FunctionalityOf(building);
        mind.WorkHungerRate = _work.HungerRateOf(building);
        mind.WorkBuilding = building;
        mind.WorkOwner = town.Owner(world, data);
    }

    /// <summary>
    /// Task_InvestigateAlarms: within 100 of the alarm it stops and faces it (the serial step counts the look-about down); else it runs to 30 short
    /// of it, on its side.
    /// </summary>
    void Investigate(World world, int slot, AiMind mind, Blackboard board, TaskType type)
    {
        ref var n = ref world.Characters.Next[slot];

        if (AlarmOf(world, slot, type, board) is not { } alarm)
        {
            Become(world, slot, CharacterTask.Idle);
            return;
        }

        var gap = n.Position - alarm.Position;

        if (gap.LengthSquared() <= Crime.CrimeSystem.AlarmLookRange * Crime.CrimeSystem.AlarmLookRange)
        {
            if (mind.Walking) Become(world, slot, CharacterTask.Idle);

            mind.Walking = false;
            if (gap.X * gap.X + gap.Z * gap.Z > 1e-4f) n.Yaw = MathF.Atan2(-gap.X, -gap.Z);

            return;
        }

        var side = Flat(gap);
        var spot = Flat(alarm.Position)
            + (side.LengthSquared() > 1e-4f ? Vector2.Normalize(side) : Vector2.UnitX) * Crime.CrimeSystem.AlarmApproach;
        GoToSpot(world, slot, mind, spot, GiveUp.Target, SpeedMode.Run);
    }

    /// <summary>
    /// The alarm an investigator answers: its squad's town's nearest in reach, for goal 210 only its own faction's
    /// (<see cref="Crime.CrimeSystem.AlarmFor"/>).
    /// </summary>
    internal Crime.TownAlarm? AlarmOf(World world, int slot, TaskType type, Blackboard board) =>
        Crimes is { Alarms.Count: > 0 } crimes && world.Characters.Cold(slot) is { } cold
            ? crimes.AlarmFor(board.Town, world.Characters.Previous[slot].Position,
                type == TaskType.InvestigateAlarmsAlliesOnly ? cold.Faction : -1,
                Crime.CrimeSystem.FloorOf(world, slot, world.Characters.Previous[slot].Position), world.Sight.LocationOf(slot), world)
            : null;

    /// <summary>
    /// Relaxing and sitting: at its claimed seat (a chair or throne, else a node; a training dummy is stood at), on the chair's origin facing its
    /// way, a workstation pretend-operated.
    /// </summary>
    void ActSeated(World world, int slot, AiMind mind, Blackboard board, TaskType type)
    {
        ref var n = ref world.Characters.Next[slot];
        mind.PretendAt = null;

        // At its claimed seat (a chair or throne, else a node; a training dummy is stood at).
        HomeSpot claimed = default;
        bool claim;

        if (type is TaskType.SitAround or TaskType.SitOnThrone)
        {
            claim = board.SitClaims.TryGetValue(slot, out var sitting);
            if (claim) claimed = sitting.Spot;

            // A piece the player built that a character took by an order or job since the claim: given up at once, so two never share it.
            if (claim && TownPlaceClaims.TakenByOrder(world, sitting))
            {
                mind.SeatUnreachable = true;
                Become(world, slot, CharacterTask.Idle);
                return;
            }
        }
        else claim = board.SeatClaims.TryGetValue(slot, out claimed);

        if (!claim) return;

        bool chair = claimed.Kind is HomeSpotKind.Chair or HomeSpotKind.ThroneSeat;

        // Already on its chair (moved onto it below, off the floor point the walk ended at).
        bool seated = chair && n.Task == (byte)CharacterTask.Rest && !mind.Walking
            && Vector2.DistanceSquared(Flat(n.Position), Flat(claimed.Position)) < 1
            && MathF.Abs(n.Position.Y - claimed.Position.Y) <= FloorSlack;
        if (!seated && !GoToSpot(world, slot, mind, Flat(claimed.Position), GiveUp.Seat, SpeedMode.Walk, height: claimed.Position.Y)) return;

        if (chair)
        {
            // On the seat's origin, with no offset, facing its way (docs/game/ai-tasks.md "Chairs"): the sitting clip carries the pose.
            n.Position = claimed.Position;
            n.Yaw = world.BenchFacingFix ? claimed.Yaw : claimed.FaithfulYaw;
            mind.SitClip = claimed.SitClip;
            mind.SitHungerRate = claimed.HungerRate;
        }
        else if (claimed.Kind is HomeSpotKind.Training or HomeSpotKind.Workstation) n.Yaw = claimed.Yaw;

        if (Pretended(claimed.Kind)) PretendOperate(world, slot, mind, claimed);
        Become(world, slot, claimed.Kind is HomeSpotKind.Training or HomeSpotKind.Workstation ? CharacterTask.Idle : CharacterTask.Rest);
    }

    /// <summary>
    /// A turret guard: runs to the gun of the turret its squad claimed for it, facing its way; without one it stands. The move-task start
    /// (<c>FUN_140332390</c>, <b>Verified</b>) sets an NPC's move to run (mode 2) when its target is a building whose use task (vfunc <c>0x418</c>) is
    /// 146, which <c>TurretBuilding</c> returns (<c>FUN_140440550</c>).
    /// </summary>
    void ActTurret(World world, int slot, AiMind mind, Blackboard board)
    {
        var table = world.Characters;

        if (board.TurretClaims.TryGetValue(slot, out int t) && (uint)t < (uint)board.Turrets.Count && board.Turrets[t].Turret is { } turret)
        {
            var spot = TurretSpot(world, turret);

            if (GoToSpot(world, slot, mind, Flat(spot), GiveUp.Post, SpeedMode.Free, TurretSlack, spot.Y))
            {
                table.Next[slot].Yaw = turret.Yaw;   // at the gun, facing its way
                Become(world, slot, CharacterTask.Idle);
            }
        }
        else Become(world, slot, CharacterTask.Idle);
    }

    /// <summary>
    /// Where a goal is acted out: a node of the home building by goal (shopkeeper, guard, throne, general; any node when the home has none of that
    /// kind), a bed for sleep (the home itself when it has no bed), the home or the town for going home; else (no home, or its spots kept failing)
    /// the character's post. True in <c>Home</c> when the spot is the home's; <c>Floor</c> is the height of a node or bed (so the walk ends on the
    /// floor it is on, not on the ground below); <c>Yaw</c> the way a node faces, which the character turns to on arriving; <c>Watch</c> when the
    /// spot is a shopkeeper node or a guard post, where the character watches who it senses (<see cref="Watch"/>).
    /// </summary>
    (Vector2 Target, bool Home, float? Floor, float? Yaw, bool Watch) SpotFor(World world, AiMind mind, Blackboard board, AiBehaviour behaviour, TaskType type)
    {
        if (behaviour == AiBehaviour.GoHome)
        {
            // Retreating (128) and Retreating fast (127) walk to the home town's waypoint outside its gates (FUN_14033dd70 -> Town vfunc 0x38), never
            // to a building.
            if (type is TaskType.RetreatHometown or TaskType.RunAwayHometown && RetreatPoint(mind.Squad) is { } waypoint)
                return (waypoint, true, null, null, false);

            if (board.Home is { } h && !mind.HomeUnreachable) return (Flat(h.Position), true, null, null, false);
            if (HomePlace(world, board) is { } town) return (town.Centre, true, null, null, false);
            return (mind.Squad.HomeCentre, true, null, null, false);
        }

        // 157 Static town guard: the post of its town it holds (ClaimTownPosts).
        if (type == TaskType.StandAtGuardNodeHometownOutside && !mind.HomeUnreachable && mind.TownPost is { Spot: var post })
            return (Flat(post.Position), true, post.Position.Y, post.Yaw, true);

        if (!mind.HomeUnreachable && GateSpot(board, mind, type) is { } guard) return (Flat(guard), true, guard.Y, null, false);

        // Staying home is in the squad's own home, whatever building a bar package made its home (FUN_1408cbc00 lists the home building's furniture).
        var at = type == TaskType.StayInHome ? board.OwnHome : board.Home;
        if (at is not { } home || mind.HomeUnreachable || type == TaskType.HoldPosition)
            return (mind.Post, false, null, null, false);

        HomeSpotKind kind = behaviour == AiBehaviour.Sleep ? HomeSpotKind.Bed : type switch
        {
            TaskType.StandAtShopkeeperNode => HomeSpotKind.Shopkeeper,
            TaskType.StandAtGuardNodeHomebuilding or TaskType.StandAtGuardNodeHomebuildingIndoorsOnly
                or TaskType.StandAtGuardNodeHometownOutside => HomeSpotKind.Guard,
            _ => HomeSpotKind.General,
        };
        ulong roll = kind == HomeSpotKind.Bed ? 0 : mind.NodeRoll;   // beds stay round robin (the bed search is not the node finders')
        if (kind == HomeSpotKind.Guard && mind.GuardVisitorAt is { } visitor && Nearest(home, kind, visitor) is { } beside)
            return (Flat(beside.Position), true, beside.Position.Y, beside.Yaw, true);

        if (Pick(home, kind, mind.MemberIndex, roll) is { } spot)
            return (Flat(spot.Position), true, spot.Position.Y, spot.IsNode ? spot.Yaw : null, Watches(spot));

        // Guard posts fall back to general nodes (docs/game/ai-tasks.md "Town goals"); the counter is the shopkeeper's, so only its goal falls back
        // onto it.
        if (kind == HomeSpotKind.Guard && Pick(home, HomeSpotKind.General, mind.MemberIndex, roll) is { } general)
            return (Flat(general.Position), true, general.Position.Y, general.Yaw, false);

        // The node goals (shopkeeper, guard, general) find a node of their own type and nothing else (FUN_14099ec70, FUN_14099f280: an empty hand,
        // see NodeGoalFindsNothing); the others stand at any node.
        if (kind != HomeSpotKind.Bed && !IsNodeGoal(type)
            && Pick(home, null, mind.MemberIndex, roll, kind != HomeSpotKind.Shopkeeper) is { } any)
            return (Flat(any.Position), true, any.Position.Y, any.Yaw, Watches(any));

        // No bed: it sleeps in the house; a node goal without nodes stays at its own post, except staying home.
        if (kind == HomeSpotKind.Bed || type == TaskType.StayInHome) return (Flat(home.Position), true, null, null, false);
        return (mind.Post, false, null, null, false);
    }

    /// <summary>
    /// Where Task_GoHomeTown / Task_GoHomeTownFast walk (<c>FUN_14033dd70</c>, <b>Verified</b>): the platoon's object hand (<c>+0xb8</c> of its
    /// platoon, meaning <b>Unknown</b>, never set here) if it resolves, else the point outside the gates of the ownerships' home town
    /// (<see cref="TownExit"/>, Town vfunc <c>0x38</c>), else (no home town) the character's own spot; null for that last case.
    /// </summary>
    Vector2? RetreatPoint(Squad squad) =>
        squad.Town >= 0 && squad.Town < data.Sites.Count ? TownExit.Of(data.Sites[squad.Town], _locks?.Regions) : null;

    /// <summary>
    /// The goals that stand at a node of the home building by its type: 20 shopkeeper, 47 general and the guard posts (<c>FUN_14099ec70</c>,
    /// <c>FUN_14099f280</c>).
    /// </summary>
    static bool IsNodeGoal(TaskType type) => type is TaskType.StandAtShopkeeperNode or TaskType.StandAtGeneralNode
        or TaskType.StandAtGuardNodeHomebuilding or TaskType.StandAtGuardNodeHomebuildingIndoorsOnly or TaskType.StandAtGuardNodeHometownOutside;

    /// <summary>
    /// Whether a node goal finds no node: the character has no home building, or its home has none of the goal's type (a guard post retries with
    /// general nodes, nothing else does), so the finder's hand is empty and the plan cannot run (<c>FUN_14099e920</c> searches the home's own node
    /// list, which a character with no home does not have: kind 2 retries with kind 1, <c>FUN_14038c2f0</c> matches the type alone). A character
    /// whose home is out of reach keeps its post. 157 Static town guard is left out: it draws a post of any building of its town
    /// (<see cref="TownPlaceClaims.ClaimTownPosts"/>), not of its home.
    /// </summary>
    bool NodeGoalFindsNothing(AiMind mind, Blackboard board, TaskType type)
    {
        if (!IsNodeGoal(type) || type == TaskType.StandAtGuardNodeHometownOutside || mind.HomeUnreachable) return false;
        if (board.Home is not { } home) return true;
        if (GateSpot(board, mind, type) is not null) return false;

        var kind = type switch
        {
            TaskType.StandAtShopkeeperNode => HomeSpotKind.Shopkeeper,
            TaskType.StandAtGeneralNode => HomeSpotKind.General,
            _ => HomeSpotKind.Guard,
        };
        return Pick(home, kind, 0) is null && !(kind == HomeSpotKind.Guard && Pick(home, HomeSpotKind.General, 0) is not null);
    }

    /// <summary>
    /// The <paramref name="index"/>-th spot of a kind (null: any node, <see cref="HomeSpot.IsNode"/>, shopkeeper nodes left out with
    /// <paramref name="notCounter"/>) of a building, uniformly at random by the goal's <paramref name="roll"/> (<see cref="AiMind.NodeRoll"/>; the
    /// node finders draw uniformly and keep the pick until the goal is renewed), else round robin; null when it has none.
    /// </summary>
    internal static HomeSpot? Pick(TownBuilding home, HomeSpotKind? kind, int index, ulong roll = 0, bool notCounter = false)
    {
        bool Takes(HomeSpot s) => kind is { } k ? s.Kind == k : s.IsNode && !(notCounter && s.Kind == HomeSpotKind.Shopkeeper);
        int count = 0;
        foreach (var s in home.Spots) if (Takes(s)) count++;
        if (count == 0) return null;
        int want = roll != 0 ? Rng.Int(roll, count) : index % count;
        foreach (var s in home.Spots)
            if (Takes(s) && want-- == 0) return s;
        return null;
    }

    /// <summary>
    /// The fixture id of the bed <see cref="SpotFor"/> sends a sleeper to (the <see cref="Pick"/> of its home's beds, numbered as
    /// <c>FixtureRegistry</c> numbers them); null without one.
    /// </summary>
    static string? BedSpotOf(Blackboard board, AiMind mind)
    {
        if (board.Home is not { } home || mind.HomeUnreachable) return null;
        int count = 0;
        foreach (var s in home.Spots) if (s.Kind == HomeSpotKind.Bed) count++;
        return count == 0 ? null : $"{home.Key}@bed{(mind.MemberIndex % count).ToString(System.Globalization.CultureInfo.InvariantCulture)}";
    }

    /// <summary>
    /// The bed a sleeper whose home has no bed spot goes to (153/239 Go home go to bed, docs/game/locks.md "Bed search, other characters"): an
    /// occupier's bed of the town it holds, else the NPC bed search (a free bed of its home building, else of its town). A squad without a town takes
    /// only its own or an ally's beds within <see cref="Locks.FixtureRegistry.BedSearchRange"/>, standing in for the original's allied BF_BED
    /// buildings in the zones around it (engine choice for the range). Null for a home with bed spots (<see cref="SpotFor"/> picks one) or when no
    /// bed is found. A visitor in a player town takes a bed it can rent there first (<see cref="Locks.BedSystem.RentableBed"/>; only while a code mod
    /// prices the player's beds).
    /// </summary>
    Locks.Fixture? SpareBed(World world, Blackboard board, AiMind mind, int slot)
    {
        if (board.InTown.PlayerTown > 0 && world.SystemOf<Locks.BedSystem>()?.RentableBed(world, slot) is { } rented) return rented;
        if (board.Home is not null && !mind.HomeUnreachable && BedSpotOf(board, mind) is not null) return null;
        if (board.Home is { } built && !mind.HomeUnreachable && SquadAutonomy.HomeBed(world, built, mind) is { } own) return own;
        if (board.Home is null && !mind.HomeUnreachable && OccupiedBed(world, board, mind, slot) is { } occupied) return occupied;
        if (world.Characters.Cold(slot) is not { } self) return null;
        if (mind.Squad.Town >= 0) return world.Fixtures.FindBed(world, slot, data.Relations);
        var at = Flat(world.Characters.Next[slot].Position);
        var relations = data.Relations;
        return world.Fixtures.FindBed(world, slot, relations, f => Locks.FixtureRegistry.OutOfTownlessReach(at, self.Faction, relations, f));
    }

    /// <summary>
    /// Whether a sleeper has a bed to go to: lying in one, a bed spot of its home, or a <see cref="SpareBed"/>.
    /// </summary>
    bool HasBed(World world, Blackboard board, AiMind mind, int slot) =>
        world.Characters.Cold(slot)?.InSomething == Locks.HeldState.Bed
        || board.Home is not null && !mind.HomeUnreachable && BedSpotOf(board, mind) is not null
        || SpareBed(world, board, mind, slot) is not null;

    #endregion

    #region Walking

    /// <summary>
    /// What a character gives up when its walks to a spot keep failing: its home's spots (it uses its post), its post (where it stands becomes it),
    /// the target, or relaxing at its bar seat.
    /// </summary>
    enum GiveUp : byte
    {
        /// <summary>
        /// Its home: its spots are given up (<see cref="AiMind.HomeUnreachable"/>), so it uses its post.
        /// </summary>
        Home,
        /// <summary>
        /// Its post: where it stands becomes its post.
        /// </summary>
        Post,
        /// <summary>
        /// The target (<see cref="AiMind.Target"/> is cleared).
        /// </summary>
        Target,
        /// <summary>
        /// Relaxing at its bar seat (<see cref="AiMind.SeatUnreachable"/>).
        /// </summary>
        Seat
    }

    /// <summary>
    /// How far above or below a spot's floor a character may stand and still be at it (units).
    /// </summary>
    internal const float FloorSlack = 6;

    /// <summary>
    /// A point on the ground plane (X, Z).
    /// </summary>
    static Vector2 Flat(Vector3 p) => new(p.X, p.Z);

    /// <summary>
    /// Where 200 Task_LeaveBuilding walks (docs/game/ai-tasks.md): A + (A − B) × 1.1 of the building's first door with a known leaf, A and B its
    /// points 15 either side of the leaf (engine reading of which is outer: the side away from the building's origin). Null: no door known.
    /// </summary>
    internal static Vector2? LeavePoint(World world, TownBuilding building) =>
        DoorFront(world, building.Key, new Vector2(building.Position.X, building.Position.Z), 1.1f);

    /// <summary>
    /// A + (A − B) × <paramref name="beyond"/> of the first door of building <paramref name="building"/> with a known leaf (<see cref="DoorExit"/>),
    /// the outer side the one away from <paramref name="inside"/>; null: no door known.
    /// </summary>
    internal static Vector2? DoorFront(World world, string building, Vector2 inside, float beyond)
    {
        foreach (var door in world.Fixtures.DoorsByBuilding[building])
        {
            if (world.Fixtures.DoorSpotOf(door) is not { } s) continue;
            return DoorExit(new Vector2(s.Centre.X, s.Centre.Z), new Vector2(s.Normal.X, s.Normal.Z), inside, beyond);
        }

        return null;
    }

    /// <summary>
    /// The point outside a door that <c>FUN_140296a40</c> yields (<b>Verified</b>): A + (A - B) x 1.1 with A and B the points 15 either side of the
    /// leaf along its normal; the outer side is the one away from the building's origin (engine reading). Shared by the leave-building task and the
    /// intruder carry-out; state 131 takes the same points 3 out (<paramref name="beyond"/>).
    /// </summary>
    internal static Vector2 DoorExit(Vector2 leafCentre, Vector2 normal, Vector2 building, float beyond = 1.1f)
    {
        if (Vector2.Dot(normal, leafCentre - building) < 0) normal = -normal;
        Vector2 a = leafCentre + normal * 15, b = leafCentre - normal * 15;
        return a + (a - b) * beyond;
    }

    /// <summary>
    /// Walks the character to a spot; true when it is there (and not walking). A walk that ends away from the spot counts as failed; after
    /// <see cref="MaxFailedWalks"/> in a row it gives the spot up as <paramref name="giveUp"/> says. <paramref name="slack"/> overrides
    /// <see cref="AiSettings.PostSlack"/> where the spot must be exact (a turret's gun); <paramref name="height"/> is the floor the spot is on, so
    /// the path reaches a floor or walkway above the ground; a spot over a hole furniture cuts in that floor is walked to beside it, on the floor
    /// (<see cref="IWalkability.FloorNear"/>).
    /// </summary>
    bool GoToSpot(World world, int slot, AiMind mind, Vector2 target, GiveUp giveUp, byte mode, float slack = 0, float? height = null)
    {
        var walk = world.Walkability;
        var table = world.Characters;
        if (height is { } spotY && walk.FloorNear(target.X, target.Y, spotY, FloorSlack) is { } onFloor)
            (target, height) = (Flat(onFloor), onFloor.Y);

        ref var n = ref table.Next[slot];
        var here = new Vector2(n.Position.X, n.Position.Z);
        float within = slack > 0 ? slack : Settings.PostSlack;

        // A spot on a floor counts as reached on that floor only, not on the ground below it.
        bool away = Vector2.DistanceSquared(here, target) > within * within || height is { } y && MathF.Abs(n.Position.Y - y) > FloorSlack;
        if (n.Task != (byte)CharacterTask.Idle) mind.ArrivedAt = null;   // it moved on: the finished path no longer says where it is

        // A player character whose walk an order or job took: the walk that ended since is not this one's (neither arrival nor failure).
        if (mind.Walking && SquadAutonomy.WalkTaken(table.Cold(slot)!, mind)) mind.Walking = false;

        if (mind.Walking && n.Task != (byte)CharacterTask.GoTo)
        {
            // The walk is over: arrived, or it ended elsewhere (no way there, or a fight took it).
            mind.Walking = false;
            WalkOver(table, slot, mind, target, here, away);
            if (mind.FailedWalks >= MaxFailedWalks)
            {
                mind.FailedWalks = 0;
                switch (giveUp)
                {
                    case GiveUp.Home:
                        mind.HomeUnreachable = true;
                        break;
                    case GiveUp.Post:
                        mind.Post = here;
                        break;
                    case GiveUp.Target:
                        mind.Target = null;
                        break;
                    case GiveUp.Seat:
                        mind.SeatUnreachable = true;
                        break;
                }

                return false;   // the next think picks the new spot
            }
        }

        if (away && mind.ArrivedThere(target, here)) return true;   // its path ended there and it has not been moved off since: arrived
        if (away)
        {
            WalkTo(world, slot, target, mode);
            mind.ArrivedAt = null;
            if (height is { } floor && n.Task == (byte)CharacterTask.GoTo)
            {
                // A spot on a floor (a node, seat or gun) is walked onto, not to a stride off it.
                var cold = table.Cold(slot)!;
                (cold.GoalHeight, cold.GoalHeightAt, cold.ArriveWithin) = (floor, target, MovementSystem.SpotArrive);
            }

            mind.Walking = true;
            return false;
        }

        return n.Task != (byte)CharacterTask.GoTo;   // arriving: the movement system ends the walk
    }

    /// <summary>
    /// Books a walk to <paramref name="target"/> that is over (<c>isAtLocation</c> <c>FUN_1405a2450</c>: a finished path is arrival whatever the
    /// distance). One the movement followed to its last point (<see cref="WalkEnd.Finished"/>) with no fight or flight taking it arrives; one it gave
    /// up (<see cref="WalkEnd.Failed"/>: no way there, blocked, a locked door) or a fight or flight took counts toward <see cref="MaxFailedWalks"/>,
    /// and one it gave up ends the goal's commitment; one another system stopped (a captive job's or lock round's stand) is neither, so the next
    /// act-out walks again.
    /// </summary>
    static void WalkOver(CharacterTable table, int slot, AiMind mind, Vector2 target, Vector2 here, bool away)
    {
        if (!away)
        {
            mind.FailedWalks = 0;
            return;
        }

        var cold = table.Cold(slot)!;
        bool fought = cold.InCombat || cold.Fleeing;
        if (!fought && cold.WalkEnd == WalkEnd.None) return;
        if (!fought && cold.WalkEnd == WalkEnd.Finished && table.Next[slot].Task == (byte)CharacterTask.Idle)
        {
            mind.ArrivedAt = target;
            mind.ArrivedFrom = here;
            mind.FailedWalks = 0;
            return;
        }

        mind.FailedWalks++;

        // A walk the movement gave up is the original's plan step finding the mover idle away from its place: the failed flag, whose handler zeroes
        // the goal's timer (FUN_14050d970, FUN_14050d080, Verified), so the commitment is over. Its replan stays the give-up rule's retries.
        if (!fought && cold.WalkEnd == WalkEnd.Failed) mind.CommitUntil = 0;
    }

    /// <summary>
    /// The stand while a mod's goal waits for its start: idle on row 14 Idleness, not on the goal's row, so the mod's task handler for a row of that
    /// number is never ended for a stand it did not start. Kept when it already stands so.
    /// </summary>
    static void StandOnIdleRow(World world, int slot)
    {
        if (world.Characters.Next[slot].Task == (byte)CharacterTask.Idle && world.Characters.Cold(slot)!.Running is { Row: TaskType.Idle }) return;

        TaskStart.StartOwn(world, slot, new TaskRequest(CharacterTask.Idle, TaskType.Idle, TaskSource.Goal));
    }

    /// <summary>
    /// Switches to a standing task (wander, follow, idle, rest), dropping a walk in progress; nothing when it is doing it already. A goal's start on
    /// the character's own slot (<see cref="TaskStart.StartOwn"/>: the think runs in parallel), as the row of the goal it acts out.
    /// </summary>
    internal static void Become(World world, int slot, CharacterTask task)
    {
        if (world.Characters.Next[slot].Task == (byte)task) return;
        TaskStart.StartOwn(world, slot, new TaskRequest(task, GoalRow(world, slot), TaskSource.Goal));
        SquadAutonomy.Stamp(world, slot);
    }

    /// <summary>
    /// A one-think step carried out leaves the plan, as the original's queue moves on when a task ends: a plan its committed goal keeps
    /// (<see cref="Choose"/>) goes on to the next step instead of doing this one again at every think. A plan made anew no longer has the step,
    /// whose state now holds.
    /// </summary>
    static void StepDone(AiMind mind)
    {
        if (mind.Plan.Count > 1) mind.Plan.RemoveAt(0);
    }

    /// <summary>
    /// Sends it walking (or running) to a point, unless it is going there already (<see cref="TaskStart.StartOwn"/>, as <see cref="Become"/>). A
    /// walk out of a building shut by locked doors it has no way through is not planned (state 60, <see cref="Locks.LockSystem.CanLeave"/>): it
    /// stands, the walk neither arrived nor failed, so the next act-out asks again.
    /// </summary>
    internal static void WalkTo(World world, int slot, Vector2 to, byte mode)
    {
        if (world.SystemOf<Locks.LockSystem>() is { } locks && !locks.CanLeave(world, slot, to))
        {
            Become(world, slot, CharacterTask.Idle);
            return;
        }

        ref var n = ref world.Characters.Next[slot];
        if (n.Task == (byte)CharacterTask.GoTo && Vector2.DistanceSquared(n.Goal, to) < 1)
        {
            n.Mode = mode;
            return;
        }

        TaskStart.StartOwn(world, slot, new TaskRequest(CharacterTask.GoTo, GoalRow(world, slot), TaskSource.Goal) { Goal = to, Mode = mode });
        SquadAutonomy.Stamp(world, slot);
    }

    /// <summary>
    /// The row of the goal the character acts out (14 Idleness without one).
    /// </summary>
    static TaskType GoalRow(World world, int slot) => world.Characters.Cold(slot)?.Ai?.Goal?.Task.Type ?? TaskType.Idle;

    #endregion

    #region Placing and bedding

    /// <summary>
    /// Set by a think that left a resident to be placed (<see cref="AiMind.PlaceAt"/>); the serial step looks for them only then.
    /// </summary>
    int _placing;

    /// <summary>
    /// Set by a think that left a sleeper to be laid in its bed (<see cref="AiMind.BedAt"/>).
    /// </summary>
    int _bedding;

    /// <summary>
    /// Set by a think whose plan's step 69 Putting down is due (<see cref="AiMind.PutDownRequest"/>).
    /// </summary>
    int _puttingDown;

    /// <summary>
    /// How far (X/Z) from where it stands a sleeper takes another free bed of its home when its own is taken (units; engine choice).
    /// </summary>
    const float BedReach = 30;

    /// <summary>
    /// Puts the residents back from their town's time unloaded on the spots their thinks chose (serial, after the movement).
    /// </summary>
    static void Place(CharacterTable table)
    {
        for (int s = 0; s < table.HighWater; s++)
        {
            if (table.Cold(s)?.Ai is not { PlaceAt: { } at } mind) continue;
            mind.PlaceAt = null;
            TaskStart.Stand(table, s);
            ref var n = ref table.Next[s];
            n.Position = at;
            n.Velocity = Vector3.Zero;
        }
    }

    /// <summary>
    /// The plan steps 69 Putting down the thinks asked (<see cref="AiMind.PutDownRequest"/>; serial, slot order): the carry system puts the body
    /// down (<c>Task_PutDownObject</c>, the put-down order's task) for a carrier the AI still holds with that step at its plan's front.
    /// </summary>
    static void PutDownForSteps(World world)
    {
        var table = world.Characters;
        var carry = world.SystemOf<Carrying.CarrySystem>();
        for (int s = 0; s < table.HighWater; s++)
        {
            if (table.Cold(s) is not { Ai: { PutDownRequest: true } mind } cold) continue;
            mind.PutDownRequest = false;
            if (carry is null || !table.Next[s].Alive || !Holds(table.Next[s], cold)) continue;
            if (mind.Plan is not [{ Type: TaskType.PutDownObject }, ..]) continue;

            carry.BeginPutDown(world, s, TaskSource.Goal);
        }
    }

    /// <summary>
    /// Lies the sleepers that reached their bed spot in it (serial: beds are shared). A taken bed gives way to the nearest free bed of the same
    /// building within <see cref="BedReach"/>; with none free it keeps resting on the floor there (engine choice).
    /// </summary>
    static void LieDown(World world)
    {
        var table = world.Characters;
        for (int s = 0; s < table.HighWater; s++)
        {
            if (table.Cold(s) is not { Ai: { BedAt: { } id } mind } cold) continue;
            mind.BedAt = null;
            ref var n = ref table.Next[s];
            if (!n.Alive || mind.Behaviour != AiBehaviour.Sleep || n.Task != (byte)CharacterTask.Rest
                || world.Fixtures.Find(id) is not { } bed)
                continue;

            var here = new Vector2(n.Position.X, n.Position.Z);
            bool Near(Locks.Fixture f) => Vector2.DistanceSquared(here, Flat(f.Position)) <= BedReach * BedReach;
            if (!Locks.FixtureRegistry.IsFree(table, bed) || !Near(bed))
            {
                Locks.Fixture? best = null;
                float bestD = float.MaxValue;
                foreach (var f in world.Fixtures.OfBuilding(bed.Building))
                {
                    if (f.Kind != Locks.FixtureKind.Bed || !Locks.FixtureRegistry.IsFree(table, f) || !Near(f)) continue;
                    float d = Vector2.DistanceSquared(here, Flat(f.Position));
                    if (d < bestD) (best, bestD) = (f, d);
                }

                if (best is null) continue;
                bed = best;
            }

            if (cold.InSomething == Locks.HeldState.None) EnterRentedBed(world, s, cold, bed);
        }
    }

    /// <summary>
    /// Lies an NPC in a bed for its rent: free unless a code mod set <see cref="Locks.BedSystem.PlayerBedRent"/> and the bed is the player's; then
    /// it must afford the rent (<see cref="Locks.BedSystem.Pay"/>) and pays only once it lies there (<see cref="Locks.BedSystem.SettleNpcRent"/>).
    /// </summary>
    static void EnterRentedBed(World world, int slot, CharacterCold cold, Locks.Fixture bed)
    {
        var beds = world.SystemOf<Locks.BedSystem>();
        int cost = beds?.Cost(world, bed, cold.Faction) ?? 0;
        if (beds is not null && !beds.Pay(world, slot, bed, cost)) return;
        if (!world.Fixtures.EnterBed(world, world.Characters.IdOf(slot), bed)) return;

        beds?.SettleNpcRent(world, slot, bed, cost);
    }

    #endregion

    #region Serial step

    /// <summary>
    /// The turrets, seats, sitting-around pieces and town guard posts the members claim on the package cadence.
    /// </summary>
    readonly TownPlaceClaims _claims = new(data);

    /// <summary>
    /// The player's squads the AI runs: their switch and home commands and their homes.
    /// </summary>
    readonly SquadAutonomy _autonomy = new(data);

    /// <summary>
    /// The commands: a trader's visit and a contract's end, an autonomous squad's switch and home (<see cref="SquadAutonomy.Apply"/>; a home in a
    /// town not looked at yet has its places found), then the contract and AI change commands (<see cref="TakePackage"/>).
    /// </summary>
    public void Inputs(World world, IReadOnlyList<SimCommand> commands)
    {
        foreach (var command in commands)
        {
            if (command is VisitTown visit)
            {
                Visit(world, visit);
                continue;
            }

            if (command is EndContract end)
            {
                world.Squads.Find(end.SquadId)?.Ai?.EndContract();
                continue;
            }

            if (_autonomy.Apply(world, command, out var named))
            {
                if (named?.Ai is { Town: >= 0 } settled && !_townPlaces.Has(settled.Town)) _townPlaces.Refresh(world, this);
            }
            else TakePackage(world, command);
        }
    }

    /// <summary>
    /// A contract or AI change command: the squad takes the package, attached first when the AI does not run it yet. A player's squad takes one
    /// only while it is autonomous (<see cref="Squad.Autonomous"/>).
    /// </summary>
    void TakePackage(World world, SimCommand command)
    {
        var (squadId, packageId) = command switch
        {
            StartContract s => (s.SquadId, s.PackageId),
            ChangeAi a => (a.SquadId, a.PackageId),
            _ => (-1, ""),
        };
        if (world.Squads.Find(squadId) is not { } squad || data.Ai.Package(packageId) is not { } package) return;
        if (squad.Ai is null)
        {
            if (IsPlayers(world, squad) && !squad.Autonomous) return;
            Attach(world, squad);
        }

        _rules.Bind(this, world, squad);
        if (command is StartContract c) squad.Ai!.StartContract(package, world.GameHours, c.Hours, _rules, c.Target, c.AnyRange);
        else squad.Ai!.Replace(package, _rules);
    }

    /// <summary>
    /// A <see cref="VisitTown"/>: the trader's next town is the player town and its stay the given hours, from state 0 (on the way), the stay's clock
    /// waiting for its arrival. The town it last left is forgotten, so a visit back to it is a stop too.
    /// </summary>
    static void Visit(World world, VisitTown visit)
    {
        if (!float.IsFinite(visit.Hours) || world.Buildings.FindTown(visit.PlayerTown) is null) return;
        if (world.Squads.Find(visit.SquadId) is not { Ai: { CurrentClass: PackageClass.WanderingTrader } board }) return;

        board.Destination = CampaignTown.OfPlayer(visit.PlayerTown);
        board.TraderWait = Math.Max(0, visit.Hours);
        board.TraderState = 0;
        board.TraderLeft = CampaignTown.None;
        board.VisitArrivalPending = true;
    }

    /// <summary>
    /// The serial step: the requests the thinks left (placing, beds, shopping, assaults, fights, treatments, captive jobs, leaving fights), then on
    /// the package cadence each squad's package, crowd and claims (a squad that appears at once), then the squad merges.
    /// </summary>
    public void SlowWorld(World world)
    {
        SendRequests(world);
        bool cadence = world.Tick % world.Every(Settings.PackageEveryTicks) == 0;

        // Each town's turrets are claimed once per cadence, by the squads in squad order (derived, not hashed).
        if (cadence) ReserveClaims(world);

        foreach (var squad in world.Squads.All)
        {
            if (squad.Ai is null)
            {
                if (cadence) ClaimSlaveWork(world, squad);
                if (AttachNew(world, squad, cadence)) UpdateSquad(world, squad);
                else StepStay(world, squad, cadence);

                continue;
            }
            else if (!cadence) continue;   // the rest is looked at on the package cadence only (and when the squad appears)

            UpdateSquad(world, squad);
        }

        if (_merges.Count > 0) ApplyMerges(world);
    }

    /// <summary>
    /// The requests the thinks left with a flag (placing, beds, shopping, assaults, fights, treatments, leaving fights), and the captive jobs, in
    /// that order; the systems they need are looked up once.
    /// </summary>
    void SendRequests(World world)
    {
        var table = world.Characters;
        if (Interlocked.Exchange(ref _placing, 0) != 0) Place(table);
        if (Interlocked.Exchange(ref _bedding, 0) != 0) LieDown(world);
        if (Interlocked.Exchange(ref _puttingDown, 0) != 0) PutDownForSteps(world);
        if (Interlocked.Exchange(ref _shopping, 0) != 0) BuyForShoppers(world);

        _combat ??= world.SystemOf<Combat.CombatSystem>();
        _aggro ??= world.SystemOf<AggroSystem>();
        _locks ??= world.SystemOf<Locks.LockSystem>();

        if (Interlocked.Exchange(ref _assaulting, 0) != 0) SendAssaults(world);
        if (Interlocked.Exchange(ref _fighting, 0) != 0) SendFights(world);
        ClaimGuardsIfAny(world);
        if (Interlocked.Exchange(ref _treating, 0) != 0) (_firstAid ??= world.SystemOf<FirstAid.FirstAidSystem>())?.SendGoalTreatments(world);
        if (Interlocked.Exchange(ref _announcing, 0) != 0) SendAnnouncements(world);

        _travel.Step(this, world);
        _duties?.Step(world, world.Every(Settings.ThinkEveryTicks) * BackoffThinks);
        if (Interlocked.Exchange(ref _leaving, 0) != 0) LeaveFights(world);
    }

    /// <summary>
    /// The cadence pass's claims start again: the seats, posts and workplaces the members hold are reserved for every squad first, and the towns'
    /// places are found again.
    /// </summary>
    void ReserveClaims(World world)
    {
        _claims.Reserve(world);
        _workTaken.Clear();
        _townPlaces.Refresh(world, this);
        ReserveWork(world);
    }

    /// <summary>
    /// A squad without a board: the first time it is seen, and again on the cadence, one the AI runs gets its board (and its town's places). True
    /// when it was attached now.
    /// </summary>
    bool AttachNew(World world, Squad squad, bool cadence)
    {
        bool first = _looked.Add(squad.Id);
        if (!first && !cadence) return false;

        if (!Runs(world, squad)) return false;

        Attach(world, squad);
        if (squad.Ai!.Town >= 0 && !_townPlaces.Has(squad.Ai.Town)) _townPlaces.Refresh(world, this);

        return true;
    }

    /// <summary>
    /// A squad the AI does not run (a player's): the town and hour its save held, the first time it is looked at since the load (as
    /// <c>FUN_140272c20</c> reads them into every platoon's blackboard), then its town step on the cadence.
    /// </summary>
    void StepStay(World world, Squad squad, bool cadence)
    {
        if (squad.SavedTown is { } saved)
        {
            if (world.Saved is { } placed)
                (squad.StayTown, squad.StaySince) = saved.Applied(squad.StayTown, h => Saving.CurrentTownSave.TownOf(world, placed, h));
            squad.SavedTown = null;
        }

        if (cadence) UpdateStay(world, squad);
    }

    /// <summary>
    /// One squad's turn on the package cadence: its board's state and package (a campaign's phase package), the contract end talk, the
    /// minds and crowd of its members, and the claims (turrets, seats, sitting, work, town posts) and gate.
    /// </summary>
    void UpdateSquad(World world, Squad squad)
    {
        var board = squad.Ai!;
        if (squad.Autonomous) _autonomy.Refresh(world, squad, board);

        board.Intact = IsIntact(world, squad, board);
        UpdateInTown(world, squad, board);
        _rules.Bind(this, world, squad);

        var platoon = PlatoonOf(world, squad);
        if (platoon?.Campaign is { } campaign && PhasePackage(world, platoon, campaign) is { } phase) InstallPhasePackage(world, board, phase);

        board.Update(_rules, world.GameHours);
        StepWar(world, squad, board);
        StepPackage(world, squad, board, campaign: platoon?.Campaign is not null);
        if (board.BarWaits) _rules.RetryTable(board);

        // A contract that ended: its leader says the package's contract end talk to the employer (event 30, FUN_14026a110).
        if (board.TakeEnded() is { Package.ContractEndTalk: { } talk, Target: var employer } && !squad.Leader.IsNone)
        {
            world.Defer(new Dialogue.DialogueEvent(squad.Leader, employer, Dialogue.DialogueSystem.ContractJobEnded, talk)
                { Tick = world.Tick + 1 });
        }

        board.Crowd.Clear();
        board.CrowdEntries.Clear();

        // Every loaded platoon acts out its package's goals in the original, so its members get full minds, a campaign's and a held one's too.
        for (int k = 0; k < squad.Members.Count; k++) MindAndCrowd(world, squad, board, k);

        squad.Resume = false;
        _claims.ClaimTurrets(world, board, squad);
        _claims.ClaimSeats(world, board, squad);
        _claims.ClaimSitting(world, board, squad, _townPlaces);
        ClaimWork(world, squad);
        _claims.ClaimTownPosts(world, board, squad, _townPlaces);
        FindGate(world, board, squad);
    }

    /// <summary>
    /// Member <paramref name="k"/> of a squad the AI moves gets its mind, and its goal counts in the squad's crowd.
    /// </summary>
    void MindAndCrowd(World world, Squad squad, Blackboard board, int k)
    {
        var table = world.Characters;
        if (!table.TryResolveNext(squad.Members[k], out int s)) return;

        var cold = table.Cold(s)!;

        // An autonomous squad's members keep their player-faction minds (PlayerSideMinds), whose goals count in the crowd; a prisoner it keeps has
        // none.
        if (squad.Autonomous)
        {
            Crowd(squad, board, k, cold);
            return;
        }

        if (cold.Ai is null or { PlayerSide: true }) GiveMind(world, squad, board, k, s);

        Crowd(squad, board, k, cold);
    }

    /// <summary>
    /// Member <paramref name="k"/>'s goal counts in the squad's crowd.
    /// </summary>
    void Crowd(Squad squad, Blackboard board, int k, CharacterCold cold)
    {
        if (cold.Ai?.Goal is not { } g) return;

        board.Crowd[g.Task.Type] = board.Crowd.GetValueOrDefault(g.Task.Type) + 1;
        board.CrowdEntries.Add(new CrowdEntry(squad.Members[k], g.Task.Type, CrowdTarget(g.Task.Target, squad, board, squad.Members[k])));
    }

    /// <summary>
    /// Member <paramref name="k"/> (slot <paramref name="s"/>) gets a mind of the squad's package and its own lists (<see cref="OwnGoals"/>).
    /// </summary>
    void GiveMind(World world, Squad squad, Blackboard board, int k, int s)
    {
        var table = world.Characters;
        var cold = table.Cold(s)!;
        var p = table.Next[s].Position;
        cold.Ai = new AiMind
        {
            Squad = squad,
            MemberIndex = k,
            Post = OccupierPost(world, board, k) ?? new Vector2(p.X, p.Z),
            RaceGoals = OwnGoals(squad, board, cold),
            Resume = squad.Resume,
            Shopping = ShoppingList.Make(data.Db, cold.RecordId, world.Seed, Rng.Key(table.IdOf(s))),
        };
    }

    /// <summary>
    /// A character that joined a squad with a board since that squad's last cadence (spawned, split off, recruited) gets the mind the cadence would
    /// give it (<see cref="MindAndCrowd"/>) at once: the original builds every character's AI at its creation and sets it
    /// up at its spawn (<c>FUN_14062af50</c>; <c>setupAI</c> <c>FUN_140621b70</c> from <c>FUN_140581770</c>, <b>Verified</b>), so it reacts to a
    /// hit from its first update. Serial, in the schedule step before the look and the think, squads and members in order; its goal counts in the
    /// crowd from the next cadence.
    /// </summary>
    void NewMinds(World world)
    {
        var table = world.Characters;
        foreach (var squad in world.Squads.All)
        {
            if (squad.Ai is not { } board) continue;

            for (int k = 0; k < squad.Members.Count; k++)
            {
                if (squad.Autonomous) break;   // its members' minds are the player-faction ones (PlayerSideMinds)
                if (!table.TryResolveNext(squad.Members[k], out int s) || !table.Next[s].Alive || table.Cold(s) is not { Ai: null }) continue;

                GiveMind(world, squad, board, k, s);
            }
        }
    }

    #endregion

    #region Player-faction minds

    /// <summary>
    /// <c>Character::setupAI</c>'s 243 Getting food of a player-faction character, an animal too (priority 3, weight 0.5, all hours;
    /// <c>FUN_140621b70</c>, <b>Verified</b>).
    /// </summary>
    static readonly AiGoal GettingFood =
        new(new AiTask("", "AQUIRE_FOOD_AT_HOMEBASE", TaskType.AquireFoodAtHomebase, TaskPriority.Urgent, TaskTargetType.Specific, TaskEndEvent.Nothing),
            0, 100, 0.5f);

    /// <summary>
    /// <c>Character::setupAI</c>'s 259 Picking up food of a player-faction animal (priority 3, weight 0.5, all hours; <c>FUN_140621b70</c>,
    /// <b>Verified</b>).
    /// </summary>
    static readonly AiGoal PickingUpFood =
        new(new AiTask("", "EAT_FOOD_ON_GROUND", TaskType.EatFoodOnGround, TaskPriority.Urgent, TaskTargetType.Specific, TaskEndEvent.Nothing),
            0, 100, 0.5f);

    /// <summary>
    /// Every member of a player's squad (<see cref="IsPlayers"/>) gets a player-faction mind (<see cref="AiMind.PlayerSide"/>) when it has none of
    /// that squad, an autonomous squad's members too (their roles are its package's, <see cref="PlayerSideRoles"/>); its work town is stamped
    /// (<see cref="StampWorkTown"/>). Serial, before the look and the think, so every player-faction character with a squad runs the chooser.
    /// </summary>
    void PlayerSideMinds(World world)
    {
        foreach (var squad in world.Squads.All)
        {
            if (!IsPlayers(world, squad)) continue;

            for (int k = 0; k < squad.Members.Count; k++)
            {
                GivePlayerSideMind(world, squad, k);
                StampWorkTown(world, squad, k);
            }
        }
    }

    /// <summary>
    /// Member <paramref name="k"/> gets its player-faction mind when it is of the player's faction (<c>+0x250</c>; not a prisoner the squad keeps),
    /// unless it holds one of this squad; an engine goal of the mind it had lets go first. Its shopping list serves an autonomous squad's package
    /// goal 118.
    /// </summary>
    void GivePlayerSideMind(World world, Squad squad, int k)
    {
        var table = world.Characters;
        if (!table.TryResolveNext(squad.Members[k], out int s) || !table.Next[s].Alive || table.Cold(s) is not { } cold) return;
        if (!cold.IsPlayer && (PlayerFactionOf(world) is var faction && (faction < 0 || cold.Faction != faction))) return;   // setupAI's branch: the faction's
        if (cold.Ai is { PlayerSide: true } held && ReferenceEquals(held.Squad, squad)) return;

        if (cold.Ai?.Duty is not null) _duties?.Release(world, s, DutyEnd.Replanned);
        var p = table.Next[s].Position;
        cold.Ai = new AiMind
        {
            Squad = squad,
            MemberIndex = k,
            Post = new Vector2(p.X, p.Z),
            Player = true,
            PlayerSide = true,
            RaceGoals = PlayerOwnGoals(cold),
            Shopping = ShoppingList.Make(data.Db, cold.RecordId, world.Seed, Rng.Key(table.IdOf(s))),
        };
    }

    /// <summary>
    /// A player-faction character's own lists (<c>setupAI</c>, <b>Verified</b>): its faction's <c>AI Goals</c> (the player platoon runs no contract),
    /// then 243 Getting food unless held; an animal then its race's <c>AI Goals</c> and 259 Picking up food unless held, without 230.
    /// <c>setupAI</c> first puts 14 Idleness (priority 1, weight 0.1) and 32 Self preservation (priority 3, weight 1.0, itself) in every
    /// character's own list; Meitou weighs them outside it: 32 as every mind's urgent candidate (<see cref="SelfPreservationGoal"/>), 14 as the
    /// default task and the mover's IDLE (<see cref="Idle"/>), not as a priority-1 candidate. The scored 14 is dropped while its list B
    /// <c>!162 !157</c> holds; it survives while the character is prone or holds a weapon, and plans only the sheathing of a standing one.
    /// </summary>
    IReadOnlyList<AiGoal> PlayerOwnGoals(CharacterCold cold)
    {
        string? faction = data.FactionId(cold.Faction);
        var factionGoals = data.Ai.OwnGoals(null, faction, withFaction: true);
        bool animal = Factions.SquadRegistry.IsAnimal(cold);
        var own = new List<AiGoal>(animal ? data.Ai.OwnGoals(cold.Race?.StringId, faction, withFaction: true) : factionGoals);
        if (!own.Exists(g => g.Task.Type == GettingFood.Task.Type)) own.Insert(factionGoals.Count, GettingFood);
        if (!animal) return own;

        own.RemoveAll(g => g.Task.Type == TaskType.EatARandomDeadBody);
        if (!own.Exists(g => g.Task.Type == PickingUpFood.Task.Type)) own.Add(PickingUpFood);

        return own;
    }

    #endregion

    #region Gates

    /// <summary>
    /// How far from a gate's leaf its gate guard stands (Observed, <c>Task_ManTheGate</c>: the door's point 15 along its normal).
    /// </summary>
    const float GateGuardOut = 15;

    /// <summary>
    /// Sets <see cref="Blackboard.Gate"/>: the cached home (<see cref="Blackboard.GateHome"/>) when it is a gate, else, for a squad with a gate
    /// guard, the town's gate nearest the squad (the original's <c>HOME_GATE</c> chain, docs/game/ai-tasks.md "Town goals"); its first door's leaf,
    /// from the door gate (none headless). Kept while the gate stays the same.
    /// </summary>
    void FindGate(World world, Blackboard board, Squad squad)
    {
        board.GateHome ??= board.OwnHome;   // the HOME_GATE hand is cached on its first resolve and never follows a later change of the home
        var town = board.HomeTown;
        TownBuilding? gate = board.GateHome is { IsGate: true } home ? home : null;
        if (gate is null && board.Crowd.ContainsKey(TaskType.ManTheGate))
            gate = NearestGate(town, Flat(squad.Position));

        if (gate is null)
        {
            board.Gate = null;
            return;
        }

        if (board.Gate?.Building == gate) return;
        var centre = town.IsNone ? Flat(gate.Position) : TownExit.Centre(world, data, town);
        board.Gate = PostOf(world, gate, centre, inside: gate.RuntimeKey is not null);
    }

    /// <summary>
    /// The gate of <paramref name="town"/> nearest <paramref name="at"/> (the fallback of <see cref="FindGate"/>): a placed town's building with
    /// <c>is wall gate</c>, or an outpost's complete wall gate the player built (<see cref="TownPlaceIndex.OutpostGates"/>, an outpost a squad has
    /// as its town; Meitou only, the original plans no goals for the player's squads); null for no town or a town without one.
    /// </summary>
    TownBuilding? NearestGate(CampaignTown town, Vector2 at)
    {
        if (town.IsNone) return null;

        TownBuilding? gate = null;
        float best = float.MaxValue;
        var buildings = town.Site >= 0 ? data.Sites[town.Site].Buildings : _townPlaces.OutpostGates(town.PlayerTown);
        foreach (var b in buildings)
        {
            float d = Vector2.DistanceSquared(Flat(b.Position), at);
            if (b.IsGate && d < best) (gate, best) = (b, d);
        }

        return gate;
    }

    /// <summary>
    /// The post of <paramref name="gate"/> (see <see cref="FindGate"/>) for a town centred at <paramref name="town"/>; null: no door, or its leaf's
    /// place not known. <paramref name="inside"/>: its guards stand on the town's side (<b>engine choice</b> for a gate the player built, an
    /// outpost's: a guard posted outside would pass through it and so unlock and open the player's gate on its way).
    /// </summary>
    internal static GatePost? PostOf(World world, TownBuilding gate, Vector2 town, bool inside = false)
    {
        Locks.Fixture? door = null;
        foreach (var f in world.Fixtures.OfBuilding(gate.Key))
            if (f.Kind == Locks.FixtureKind.Door && (door is null || f.DoorIndex < door.DoorIndex)) door = f;
        if (door is null || world.Fixtures.DoorSpotOf(door) is not { } leaf) return null;
        var way = Vector2.Normalize(new Vector2(leaf.Normal.X, leaf.Normal.Z));

        // Out of the town: the original's side is not decoded (engine choice).
        if (Vector2.Dot(way, Flat(leaf.Centre) - town) < 0) way = -way;
        return new GatePost(gate, leaf.Centre with { Y = leaf.Foot }, way, leaf.HalfWidth, inside);
    }

    /// <summary>
    /// Where a gate's gate guard stands: <see cref="GateGuardOut"/> out from its leaf, or in from it for a guard posted inside
    /// (<see cref="GatePost.Inside"/>; it still faces out).
    /// </summary>
    internal static Vector3 FrontOf(GatePost gate)
    {
        float side = gate.Inside ? -GateGuardOut : GateGuardOut;
        return gate.Door + new Vector3(gate.Out.X, 0, gate.Out.Y) * side;
    }

    /// <summary>
    /// Where a gate's other guards stand (the gate guard stands at <see cref="FrontOf"/>, <see cref="Locks.GateGoals"/>): a member whose node goal
    /// finds no node in a gate it lives in flanks the opening on that side, beyond the leaf's edges, alternately left and right in two rows (engine
    /// choice: it stands in for the original's gate posts, which no layout holds). Null for other goals, and for 157 while it holds a town post.
    /// </summary>
    static Vector3? GateSpot(Blackboard board, AiMind mind, TaskType type)
    {
        if (board.Gate is not { } gate) return null;
        if (type == TaskType.StandAtGuardNodeHometownOutside && mind.TownPost is not null) return null;
        var front = FrontOf(gate);
        if (type is not (TaskType.StandAtGuardNodeHomebuilding or TaskType.StandAtGuardNodeHomebuildingIndoorsOnly
            or TaskType.StandAtGuardNodeHometownOutside or TaskType.StandAtGeneralNode) || board.Home != gate.Building)
            return null;

        foreach (var s in gate.Building.Spots) if (s.IsNode) return null;
        int step = mind.MemberIndex / 2;
        float side = (mind.MemberIndex & 1) == 0 ? 1 : -1;
        var along = new Vector3(-gate.Out.Y, 0, gate.Out.X) * side * (gate.HalfWidth + 6 + 7 * (step / 2));
        return front + new Vector3(gate.Out.X, 0, gate.Out.Y) * (8 * (step % 2)) + along;
    }

    #endregion

    #region Turrets

    /// <summary>
    /// Within this (squared 2.25e6) of the character the turret finder's pick has factor 1; within 600 in melee mode it has 0.8.
    /// </summary>
    const float TurretNear = 1500, TurretNearMelee = 600;

    /// <summary>
    /// The turret goals' (149, 222, 234) finder factor for the turret found at distance <paramref name="d2"/> squared (<c>FUN_1408ccc70</c>,
    /// <b>Verified</b> from the registry and decompile): 1.0 within 1500, else 0.3; in melee combat mode 0.8 within 600, else 0.05.
    /// </summary>
    internal static float TurretFactor(float d2, bool melee) =>
        melee ? d2 <= TurretNearMelee * TurretNearMelee ? 0.8f : 0.05f : d2 <= TurretNear * TurretNear ? 1 : 0.3f;

    /// <summary>
    /// The hunger level below which a turret goal's desire drops to <see cref="TurretHungry"/> with an empty stomach and no enemy sensed
    /// (<c>DAT_1416efdd4</c>).
    /// </summary>
    const float TurretHungerBelow = 2.1f;

    /// <summary>
    /// A turret goal's desire for a hungry character (<c>DAT_141696c90</c>).
    /// </summary>
    const float TurretHungry = 0.4f;

    /// <summary>
    /// The least and largest squared distance an NPC's turret cut allows (<c>DAT_14168dae4</c>, <c>DAT_1416888b4</c>), and its factor on the
    /// nearest enemy's squared distance (<c>DAT_1416852f0</c>).
    /// </summary>
    const float TurretCutMin = 10000, TurretCutMax = 1e6f, TurretCutFactor = 1.2f;

    /// <summary>
    /// A player-faction character's turret cut with a 234 job or a 146 queued (<c>DAT_1416888b0</c>), and with Ranged alone (<c>DAT_1416e3460</c>).
    /// </summary>
    const float TurretCutJob = 1e8f, TurretCutRanged = 62500;

    /// <summary>
    /// AI slot 35 (<c>FUN_140598080</c>, <b>Verified</b>, decompile and constants), the desire of the turret rows 149, 222, 234 and 146: 0 when
    /// carrying, or Passive outside melee combat mode; for a character of the player's faction 1 with a 146 in its order queue, else 0 with neither
    /// a 234 job nor Ranged; 0 when its target is a building (<paramref name="building"/>, the turret of a 146; <c>FUN_140792910</c> resolves
    /// building hands only, so 149 and 222 with <c>targeting</c> 0 and 234 on the character itself have no cut) farther in squared distance than the
    /// cut: an NPC's clamp(the nearest enemy's x 1.2, 1e4, 1e6) (<see cref="SenseBlock.NearestEnemy"/>), a player-faction one's 1e8 with the job,
    /// 62500 on Ranged alone; then 2 with an enemy sensed while it mans a turret (Character <c>+0x260</c>, the turret hand <c>TurretBuilding</c>
    /// slot 150 <c>FUN_140434d20</c> writes from <c>Task_UseTurret</c>'s update; Meitou: <see cref="CharacterCold.Gunning"/>), 0.4 hungry (hunger
    /// below 2.1, stomach empty) with none sensed, else 1.
    /// </summary>
    float TurretDesire(World world, int slot, Vector3? building = null)
    {
        if (world.Characters.Cold(slot) is not { } cold || !cold.Carrying.IsNone) return 0;
        if (PassiveOffMelee(world, slot, cold)) return 0;

        var senses = _aggro?.SensesOf(slot);
        float cut = TurretCut(senses);
        if (cold.Faction == PlayerFactionOf(world))
        {
            if (!PlayerSideTurretWanted(cold, out bool queued, out bool job)) return 0;
            if (queued) return 1;

            cut = job ? TurretCutJob : TurretCutRanged;
        }

        if (building is { } at && Vector3.DistanceSquared(world.Characters.Previous[slot].Position, at) > cut) return 0;

        int enemies = senses?.Enemies ?? 0;
        if (enemies > 0 && cold.Gunning is not null) return 2;

        bool hungry = cold.Medical is { Hunger: < TurretHungerBelow, Fed: <= 0 };
        return hungry && enemies == 0 ? TurretHungry : 1;
    }

    /// <summary>
    /// An NPC's turret cut: clamp(the nearest enemy's squared distance x 1.2, 1e4, 1e6); no enemy (the float maximum) gives 1e6.
    /// </summary>
    static float TurretCut(SenseBlock? senses) =>
        Math.Clamp((senses?.NearestEnemy ?? float.MaxValue) * TurretCutFactor, TurretCutMin, TurretCutMax);

    /// <summary>
    /// Passive (standing order 13, the <c>+0x450</c> object's <c>+0x12c</c>) and not in melee combat mode (<c>FUN_140436610</c>).
    /// </summary>
    static bool PassiveOffMelee(World world, int slot, CharacterCold cold)
    {
        if ((cold.Orders & CharacterOrders.Passive) == 0) return false;

        var table = world.Characters;
        return world.SystemOf<Combat.CombatSystem>() is not { } combat || !combat.IsInCombatMode(table, table.IdOf(slot), cold, melee: true, ranged: false);
    }

    /// <summary>
    /// Slot 35's player-faction gate: a 146 in the order queue (<c>FUN_140507c10</c>, <paramref name="queued"/>: the desire is 1 at once), else a
    /// 234 job (<c>FUN_140507200</c>; <paramref name="job"/>, either of the two) or Ranged (<see cref="Orders.RangedMode"/>); without any the desire is 0.
    /// </summary>
    static bool PlayerSideTurretWanted(CharacterCold cold, out bool queued, out bool job)
    {
        queued = cold.OrderQueue.Any(o => o.Row == TaskType.UseTurret);
        job = queued || cold.Jobs.Exists(j => j.Type == TaskType.ManATurretPlayerJob);

        return job || Orders.RangedMode(cold);
    }

    /// <summary>
    /// Where a turret is manned: behind its gun (the gunner stands between the gun and the walkway behind it), inside the turret's <c>use range</c>:
    /// the first of a few distances behind it whose ground is walkable at the turret's height, else beside or before it, else the turret's own place.
    /// The Y is the walkable surface there, so the path goes up to the tower's floor rather than to the ground below it. Engine choice: the game's
    /// gunner spot is not in the data.
    /// </summary>
    internal static Vector3 TurretSpot(World world, in TurretPost turret)
    {
        var at = Flat(turret.Position);
        var forward = new Vector2(MathF.Sin(turret.Yaw), MathF.Cos(turret.Yaw));
        var side = new Vector2(forward.Y, -forward.X);
        float nearY = turret.Position.Y;
        bool Walkable(Vector2 p) => world.Walkability.IsWalkable(p.X, p.Y, nearY);
        Vector3 On(Vector2 p) => new(p.X, world.Walkability.GroundHeight(p.X, p.Y, nearY), p.Y);
        float reach = MathF.Max(MathF.Min(turret.UseRange, 10), 2);
        for (float d = reach * 0.6f; d >= 2; d -= 2)
            if (Walkable(at - forward * d)) return On(at - forward * d);
        for (int k = 0; k < 2; k++)
            if (Walkable(at + side * (k == 0 ? 4 : -4))) return On(at + side * (k == 0 ? 4 : -4));
        return Walkable(at + forward * 4) ? On(at + forward * 4) : On(at);
    }

    #endregion

    #region Squads and packages

    /// <summary>
    /// The packages and template of each squad template id (<see cref="TemplateOf"/>); derived from the data, not hashed.
    /// </summary>
    readonly Dictionary<string, (IReadOnlyList<SlottedPackage> Packages, SquadTemplate? Template)> _templates = [];

    /// <summary>
    /// Squads already looked at (ids are never reused); derived from the squads, so not part of the hash.
    /// </summary>
    readonly HashSet<int> _looked = [];

    /// <summary>
    /// The contract time the war manager's phase package is installed with (<c>FUN_140272240</c> called with 24 game hours, <b>Verified</b>); only
    /// the classes whose finished test reads it end there (<see cref="Blackboard.PhaseContract"/>).
    /// </summary>
    const double PhaseHours = 24;

    /// <summary>
    /// The "Attack enemies" goal (16) for a debug spawn that must aggro (<see cref="Squad.DebugAggro"/>), made on first use; null when the data has
    /// none.
    /// </summary>
    AiGoal? _attackEnemiesGoal;

    /// <summary>
    /// The BuildingFunction bits the finder of a new home looks through: 0x13, BF_ANY, MINE and REFINERY (the house function's number 19 passed as a
    /// bit mask, as the worker finder's 0x41a is read, <b>Observed</b>); a building with no functionality counts as BF_ANY.
    /// </summary>
    const int RehomeFunctions = 0x13;

    /// <summary>
    /// The town a squad made at <paramref name="at"/> starts in: the one whose radius holds it, else the nest it stands in.
    /// </summary>
    CampaignTown StartTown(World world, Vector3 at) =>
        CurrentTown(world, at) is { IsNone: false } town ? town : StatusTownAt(data, world, at, CampaignTown.None);

    /// <summary>
    /// Gives a squad its AI: its home (assigned, or the one its town gave it), whose template and initial door state apply, and a new blackboard of
    /// its template's packages and its faction's <c>AI fallback</c> (<see cref="AiDatabase.Packages(SquadTemplate, string, bool)"/>), taken as
    /// already in the town it starts in; a saved squad's AI state is applied to it.
    /// </summary>
    void Attach(World world, Squad squad)
    {
        var (packages, template) = TemplateOf(squad.TemplateId);

        // A platoon's squad on the road has no building of its own to claim (only one its town gave it), nor has a squad without packages (slaves
        // obeying another squad, Runs).
        bool given = squad.HomeBuilding is not null;
        var home = PlatoonOf(world, squad) is null && packages.Count > 0 ? AssignHome(world, squad, template) : squad.HomeBuilding;

        // A building the squad claims itself takes its template (FUN_140553210); one its town gave it was noted when it was made, a saved squad's
        // (from a save with no building record) now.
        if (home is not null && template is not null && (!given || world.Fixtures.RecordOf(home.Key) is null))
            world.Fixtures.NoteAssigned(home.Key, template);

        // The template's initial door state applies to the home it is given (docs/game/locks.md "Doors"); the first squad's wins, a saved building
        // keeps its own.
        if (home is not null) world.Fixtures.ApplyInitialDoorState(home.Key, template?.InitialDoorState ?? 0);
        string? faction = data.FactionId(squad.Faction);
        squad.Ai = new Blackboard(template is null ? packages : data.Ai.Packages(template, faction, squad.Restored))
        {
            Town = squad.Town,
            Size = squad.Members.Count,
            OwnHome = home,
            Home = home,
            HomePublic = template?.PublicDay ?? true,

            // A patrol package a save restores as running counts its 12 hours from the load (engine choice: Meitou does not save the stamp).
            PatrolStart = world.GameHours,
        };

        // The town it starts in (a nest too), taken as already entered (engine choice: no ENTER_TOWN line for every squad a town or nest makes);
        // a saved squad then takes its saved town.
        if (TownPosition(world, squad) is { } at) (squad.Ai.InTown, squad.Ai.InTownSince) = (StartTown(world, at), world.GameHours);

        // A player's squad the AI starts to run keeps the town it was in and its hour (the original's platoon has one blackboard throughout).
        if (IsPlayers(world, squad))
        {
            StepStay(world, squad, false);
            (squad.Ai.InTown, squad.Ai.InTownSince) = (squad.StayTown, squad.StaySince);
        }

        // The town and arrival hour its platoon's save record held, for the first squad in it since the load, made from the save's characters or
        // from its template (FUN_140272c20 reads currenttown and towntime into the platoon's blackboard).
        if (squad.PlatoonId >= 0 && world.Platoons.Find(squad.PlatoonId) is { SavedTown: { } stay } platoon)
        {
            if (world.Saved is { } placed) stay.Apply(squad.Ai, h => Saving.CurrentTownSave.TownOf(world, placed, h));
            platoon.SavedTown = null;
        }
        if (squad.SavedAi is { } saved)
        {
            _rules.Bind(this, world, squad);
            saved.Apply(squad.Ai, data.Ai, _rules, world.Saved is { } population ? population.CharacterOf : null,
                world.Saved is { } towns ? h => Saving.WarSave.Town(world, towns, h) : null);
            squad.SavedAi = null;

            // A saved replacementAI clears the template's packages; the constructor then adds the faction's fallback (FUN_1407ee2d0).
            if (squad.Ai.Replacement is not null && template is not null && faction is not null) squad.Ai.AddAtSlotZero(data.Ai.Fallback(faction));
        }

        // An occupation it left the world in goes on in the player town it comes back in (the original keeps the occupier's platoon and package,
        // docs/game/ai.md); the town is the one it was made in, as a squad made this tick has no positions yet.
        if (squad.LeftPackage is { } left && data.Ai.Package(left) is { } package
            && PackageClasses.Of(package.Signal) == PackageClass.OccupyTown && squad.Ai.Contract is null
            && CurrentTown(world, squad.Position) is { PlayerTown: > 0 } there && squad.Ai.Resume(package))
        {
            squad.Ai.InTown = there;
            OccupyResumed(world, squad, squad.Ai);
        }

        ResumeAssault(world, squad);
        squad.LeftPackage = null;
        if (squad.Autonomous) _autonomy.Settle(world, squad, squad.Ai);
    }

    /// <summary>
    /// A non-player member's own goal lists (<see cref="AiMind.RaceGoals"/>): its faction's <c>AI Goals</c> while no contract runs on the
    /// squad's blackboard, then its race's (<c>setupAI</c> reads the faction's only then, <b>Verified</b>), and for a debug spawn that must aggro
    /// (<see cref="Squad.DebugAggro"/>, engine API) Attack enemies.
    /// </summary>
    IReadOnlyList<AiGoal> OwnGoals(Squad squad, Blackboard board, CharacterCold cold)
    {
        string? faction = data.FactionId(cold.Faction);
        var own = data.Ai.OwnGoals(cold.Race?.StringId, faction, board.Contract is null);
        return squad.DebugAggro && AttackEnemiesGoal() is { } attack ? [.. own, attack] : own;
    }

    /// <summary>
    /// The "Attack enemies" goal (16) of the AI_TASK records, for a debug spawn that must aggro; null when the data has none.
    /// </summary>
    AiGoal? AttackEnemiesGoal()
    {
        if (_attackEnemiesGoal is { } known) return known;
        var task = data.Db.OfType(FcsRecordType.AiTask).Select(AiTask.From).FirstOrDefault(t => t.Type == TaskType.AttackEnemies);
        return _attackEnemiesGoal = task is null ? null : new AiGoal(task, 0, 100, 1f);
    }

    /// <summary>
    /// The package the war manager's record gives a campaign platoon for its phase (<c>FUN_140272570</c>: pt1, pt2 or retreat, the leader platoon's
    /// or the others'); null after the retreat or when the campaign lists none.
    /// </summary>
    AiPackage? PhasePackage(World world, Platoon platoon, CampaignInstance inst) =>
        inst.Phase <= 2 && world.SystemOf<PopulationSystem>()?.CampaignOf(inst.CampaignId)?.PackageOf(inst.Phase, inst.Leader == platoon.Id)
            is { } id ? data.Ai.Package(id) : null;

    /// <summary>
    /// Installs the phase package as the blackboard's override when it holds none or another (the war manager's check on every visit,
    /// <c>FUN_140272570</c>, <b>Verified</b>), with the 24 h contract time the original sets (+0x218). It ends by its class's finished test
    /// (<see cref="Blackboard.PhaseContract"/>), never by the campaign: one that ends is installed afresh on the next visit while the campaign
    /// still gives it (Retreat and refresh on an intact platoon is installed and ended on every visit, its template's packages running between,
    /// as in the original), and one the campaign no longer gives (removed, or the platoon released) stays until its test passes. An override that
    /// is already the package (a saved one) is marked as the phase's.
    /// </summary>
    void InstallPhasePackage(World world, Blackboard board, AiPackage package) =>
        board.StartContract(package, world.GameHours, PhaseHours, _rules, phase: true);

    /// <summary>
    /// The AI runs every squad that is not the player's: every platoon has a blackboard in the original and every member of one runs the chooser
    /// (<c>FUN_1405112b0</c> needs only the platoon, <b>Verified</b>). One whose template lists no AI packages has empty role lists, so its members
    /// weigh only 14, 32 and their own lists (<see cref="OwnGoals"/>) and stand idle otherwise; a slave of it plans with its master's Slave list
    /// (<see cref="ObeyedRoles"/>), and a campaign's platoon runs its phase package as the blackboard's override (<c>FUN_140272570</c>). A player's
    /// squad only when it is autonomous and a save held its AI (<see cref="Squad.SavedAi"/>): else it is attached when it is given a package
    /// (<see cref="TakePackage"/>).
    /// </summary>
    bool Runs(World world, Squad squad) => !IsPlayers(world, squad) || squad is { Autonomous: true, SavedAi: not null };

    /// <summary>
    /// The platoon whose squad this is when it is a loaded roaming or campaign platoon's (not a town's own saved squad, nor a wild one); null for
    /// any other squad. Such a squad claims no building of its own. Every loaded
    /// platoon runs its blackboard in the original (<c>FUN_1402732b0</c> from the platoon's slot 27,
    /// docs/game/ai.md "Blackboard update"), whatever its kind.
    /// </summary>
    Platoon? PlatoonOf(World world, Squad squad) =>
        squad.PlatoonId >= 0 && !squad.Pinned && !squad.Wild ? world.Platoons.Find(squad.PlatoonId) : null;

    /// <summary>
    /// Whether the running package moves the squad itself (the trader, the refugee, the shipping and retreat classes): one of its goals travels and a
    /// mission town is set. It names the squads whose mission town is the stand-in's target at unload and is saved.
    /// </summary>
    internal static bool BoardMoves(Blackboard board)
    {
        if (board.Destination.IsNone && board.MissionTown < 0 && board.RefugeeTarget < 0) return false;
        foreach (var role in board.Roles)
        {
            foreach (var goal in role)
                if (Travels(goal.Task.Type)) return true;
        }

        return false;
    }

    /// <summary>
    /// Whether a goal walks its squad to the mission town: a travel goal, or 192 Delivering slaves, the shipping package's leader goal (a captive
    /// job).
    /// </summary>
    static bool Travels(TaskType type) =>
        TaskBehaviours.Of(type) == AiBehaviour.Travel || type == TaskType.GoToSomewhereForDeliveringSlaves;

    /// <summary>
    /// Whether the squad is the player's: of the player's faction, or with a player character among its members.
    /// </summary>
    bool IsPlayers(World world, Squad squad)
    {
        if (squad.Faction == data.PlayerFaction && data.PlayerFaction >= 0) return true;
        foreach (var m in squad.Members)
            if (world.Characters.TryResolveNext(m, out int s) && world.Characters.Cold(s)!.IsPlayer) return true;
        return false;
    }

    /// <summary>
    /// The template's own packages (without its faction's <c>AI fallback</c>, which <see cref="Attach"/> adds) and the template of a squad template
    /// id (none for an id that is no squad template), cached.
    /// </summary>
    internal (IReadOnlyList<SlottedPackage> Packages, SquadTemplate? Template) TemplateOf(string templateId)
    {
        if (_templates.TryGetValue(templateId, out var known)) return known;
        var template = data.Db.Find(templateId) is { } r && SquadFactory.IsTemplate(r) ? SquadTemplate.From(r) : null;
        var entry = (template is null ? (IReadOnlyList<SlottedPackage>)[] : data.Ai.Packages(template), template);
        _templates[templateId] = entry;
        return entry;
    }

    /// <summary>
    /// The squad's own building: the one its town gave it (<see cref="Squad.HomeBuilding"/>, docs/game/factions-squads-towns.md "Town residents");
    /// for a squad without one (made without building data, or a save's without a home) an <b>engine choice</b>: among its town's buildings no other
    /// squad has, the nearest of the template's <c>building</c>; else by <c>building designation</c> a shop (SHOP) or the bar (BAR, or one of the
    /// town's bar squads; shared); else the nearest with beds, else the nearest at all. None for a squad without a town or a town without building
    /// data. A squad not of the player's faction is never given a building the player owns (the original's housing leaves player-owned buildings out,
    /// docs/game/factions-squads-towns.md "Town residents").
    /// </summary>
    TownBuilding? AssignHome(World world, Squad squad, SquadTemplate? template)
    {
        int player = world.Player.Faction;
        bool players = squad.Faction == player;
        if (squad.HomeBuilding is { } given)
        {
            if (players || !world.Fixtures.OwnsBuilding(given.Key, player)) return given;
            return squad.HomeBuilding = HouseForSale(world, squad);   // a save from before a bought house's squads moved out (Evict)
        }

        if (squad.Town < 0 || data.Sites[squad.Town].Buildings is not { Count: > 0 } buildings) return null;
        var taken = new HashSet<TownBuilding>(ReferenceEqualityComparer.Instance);
        foreach (var other in world.Squads.All)
            if (other.Ai is { OwnHome: { } h } && other.Town == squad.Town) taken.Add(h);
        var at = new Vector2(squad.Position.X, squad.Position.Z);
        TownBuilding? Nearest(Func<TownBuilding, bool> accept, bool shared = false)
        {
            TownBuilding? best = null;
            float bestD = float.MaxValue;
            foreach (var b in buildings)
            {
                if (!accept(b) || (!shared && taken.Contains(b)) || !players && world.Fixtures.OwnsBuilding(b.Key, player)) continue;
                float d = Vector2.DistanceSquared(at, Flat(b.Position));
                if (d < bestD) (best, bestD) = (b, d);
            }

            return best;
        }

        if (template?.Building is { } wanted && Nearest(b => b.BuildingId == wanted.Id) is { } own) return own;
        if (template?.BuildingDesignation == 1 && Nearest(b => b.IsShop) is { } shop) return shop;
        if ((template?.BuildingDesignation == 3 || IsBarSquad(squad)) && Nearest(b => b.IsBar, shared: true) is { } bar) return bar;
        return Nearest(b => b.Beds > 0) ?? Nearest(_ => true);
    }

    /// <summary>
    /// The squads living in a town house the player has bought move out (the original's purchase callback <c>FUN_1407ad6c0</c>, docs/simulation.md
    /// "Town house purchases"): each is given another house of its town that is for sale (<see cref="HouseForSale"/>), else none, and its members
    /// plan again for it. A platoon away from the world (<see cref="StoredSquad"/>) only loses it.
    /// </summary>
    public void Evict(World world, string building)
    {
        foreach (var squad in world.Squads.All)
            if (!squad.Autonomous && (squad.HomeBuilding?.Key == building || squad.Ai?.OwnHome?.Key == building)) Rehome(world, squad);
        foreach (var platoon in world.Platoons.All)
            if (platoon.Stored is { } stored && stored.HomeBuilding?.Key == building) stored.HomeBuilding = null;
    }

    /// <summary>
    /// Gives a squad moving out another house of its town that is for sale (<see cref="HouseForSale"/>, else none) as its own home and its home in
    /// use, and its members their orders (<see cref="OrderMovedOut"/>).
    /// </summary>
    void Rehome(World world, Squad squad)
    {
        var home = HouseForSale(world, squad);
        squad.HomeBuilding = home;
        squad.MovedOut = home is null;
        if (squad.Ai is { } board)
        {
            var old = board.OwnHome;
            board.OwnHome = home;
            if (board.Home == old) board.Home = home;
            board.Touch();
        }

        OrderMovedOut(world, squad, home);
    }

    /// <summary>
    /// The purchase's orders (<c>FUN_1407ad6c0</c>, <b>Verified</b>): each member of the squad moving out gets 19 Going home to its new home, or 22
    /// Patrolling without one, clearing its queue; its mind thinks again once the order is over.
    /// </summary>
    void OrderMovedOut(World world, Squad squad, TownBuilding? home)
    {
        var table = world.Characters;
        var row = home is not null ? TaskType.GoHomebuilding : TaskType.Patrol;
        foreach (var id in squad.Members.ToList())
        {
            if (!table.TryResolveNext(id, out int slot) || table.Cold(slot) is not { } cold) continue;
            if (cold.Ai is { } mind) mind.HomeUnreachable = false;

            PlayerOrders.Give(world, slot, new OrderEntry(row, NpcOrders) { Site = home?.Key }, queued: false);
        }
    }

    /// <summary>
    /// The home a squad moving out is given (<c>FUN_14099e6e0</c> through <c>FUN_1409fc2e0</c>, <b>Observed</b>): one of its town's buildings in
    /// <see cref="RehomeFunctions"/> that is for sale (the panel's test, slot 88 <c>FUN_14029c500</c>:
    /// <see cref="Locks.FixtureRegistry.SalePrice"/>), at random; null when there is none. The original also tests the squad against each
    /// (<c>FUN_140621230</c>, meaning <b>Unknown</b>, not modelled).
    /// </summary>
    TownBuilding? HouseForSale(World world, Squad squad)
    {
        if ((uint)squad.Town >= (uint)data.Sites.Count) return null;
        static bool Searched(int function) => function < 0 || function < 32 && (RehomeFunctions >> function & 1) != 0;
        var houses = data.Sites[squad.Town].Buildings
            .Where(b => Searched(b.Function) && world.Fixtures.SalePrice(b.Key, world.Player.Faction) is not null).ToList();
        if (houses.Count == 0) return null;
        ulong roll = Rng.Hash(world.Seed, (ulong)squad.Id, RngPurpose.Think, SeedSalts.AiRehome ^ (ulong)world.Tick << 8);
        return houses[Rng.Int(roll, houses.Count)];
    }

    /// <summary>
    /// A squad made from one of its town's <c>bar squads</c> entries (the original gives it no home; Meitou keeps it at the bar, not in a free
    /// building with beds).
    /// </summary>
    bool IsBarSquad(Squad squad)
    {
        int f = squad.Town < data.SiteFactions.Count ? data.SiteFactions[squad.Town] : -1;
        return PopulationSystem.BarEntries(data.Sites[squad.Town].Town, f >= 0 ? data.Factions[f] : null).Any(e => e.Id == squad.TemplateId);
    }

    /// <summary>
    /// Whether the squad is intact (<see cref="SquadIntact"/>, <c>FUN_14079acf0</c>, Verified): never while it lacks a member held in a cage or
    /// carried off by a non-ally. Persistent with a home stands for a town's resident squad with its own building (engine reading of the platoon's
    /// persistent flag and home holder).
    /// </summary>
    bool IsIntact(World world, Squad squad, Blackboard board)
    {
        var table = world.Characters;
        if (SquadIntact.Held(table, squad, data.Relations)) return true;
        bool home = SquadIntact.PersistentHome(squad);
        int able = 0;
        foreach (var m in squad.Members)
            if (table.TryResolveNext(m, out int s) && SquadIntact.Able(world, table.Cold(s)!, home)) able++;
        return SquadIntact.Decide(able, Math.Max(board.Size, 1), !squad.Leader.IsNone, SquadIntact.Down(table, squad.Leader), home);
    }

    /// <summary>
    /// The package-class rules of <see cref="Blackboard.Update"/>, bound to the squad being updated (serial steps only).
    /// </summary>
    sealed class Rules : IPackageRules
    {
        /// <summary>
        /// The AI system it is bound to.
        /// </summary>
        AiSystem _system = null!;
        /// <summary>
        /// The world it is bound to.
        /// </summary>
        World _world = null!;
        /// <summary>
        /// The squad being updated.
        /// </summary>
        Squad _squad = null!;

        /// <summary>
        /// Binds the rules to the squad being updated.
        /// </summary>
        public void Bind(AiSystem s, World w, Squad q) => (_system, _world, _squad) = (s, w, q);

        /// <summary>
        /// Whether a package of class <paramref name="kind"/> may start for the bound squad (the class's start test; a plain package always may).
        /// </summary>
        public bool CanStart(Blackboard board, AiPackage package, PackageClass kind) => kind switch
        {
            PackageClass.Retreat => !board.Intact,
            PackageClass.Patrol or PackageClass.PatrolRunning => _system.PatrolCanStart(_world, _squad, board, package),
            PackageClass.Bar => BarHere(board),
            PackageClass.BarOutdoors => true,
            PackageClass.CannibalRetreat or PackageClass.CannibalsAtHome or PackageClass.PrisonerGathering or PackageClass.SlaverShipping =>
                _system.CaptiveCanStart(_world, _squad, board, package, kind),
            PackageClass.WanderingTrader => _system.TraderCanStart(_world, _squad, board),
            PackageClass.PassingAssault => _system.AssaultCanStart(_world, _squad, board),
            PackageClass.Refugee => RefugeeCanStart(_world, _squad),

            // The war classes and the campaign's defence start only as a campaign's override (engine choice, Meitou's rule before they were built;
            // the original's can-start is always true, FUN_140287690 and FUN_140286e00, which matters only to a template slot listing one: none in
            // the data).
            _ => false,
        };

        /// <summary>
        /// Where a bar package can run: for a squad that is not a town's own (engine choice: a town's squads keep their town), away from home in a
        /// placed town not hostile to it that has a public bar (the original's <c>Package_HangAroundBar</c> takes the town the squad is in,
        /// docs/game/ai.md "Package classes"), in a player town not (engine choice: their bars are not read); at home (or in no town), with building
        /// data only where the town has a bar or an outdoor table, without it the package runs and the squad keeps its spots.
        /// </summary>
        bool BarHere(Blackboard board)
        {
            var t = board.InTown;
            if (!_squad.TownOwn && t.PlayerTown > 0) return false;
            if (!_squad.TownOwn && t.Site >= 0 && t.Site != board.Town)
            {
                int theirs = t.Site < _system.Data.SiteFactions.Count ? _system.Data.SiteFactions[t.Site] : -1;
                return !(_squad.Faction >= 0 && theirs >= 0 && _system.Data.Relations.IsHostile(_squad.Faction, theirs))
                    && _system.Places(t.Site).Bars.Count > 0;
            }

            return board.Town < 0 || _system.Data.Sites[board.Town].Buildings.Count == 0
                || _system.Places(board.Town) is { } p && (p.Bars.Count > 0 || p.OutdoorTables.Count > 0);
        }

        /// <summary>
        /// The town a bar package uses: for a squad that is not a town's own, the placed town it is in; else its own town.
        /// </summary>
        int BarTown(Blackboard board) => SquadTown.BarSite(_squad, board);

        /// <summary>
        /// Whether the running package of class <paramref name="kind"/> is finished for the bound squad (the class's finished test).
        /// </summary>
        public bool Finished(Blackboard board, AiPackage package, PackageClass kind) => kind switch
        {
            PackageClass.Retreat => board.Intact,
            PackageClass.Patrol or PackageClass.PatrolRunning => PatrolFinished(_world, board),
            PackageClass.CannibalRetreat or PackageClass.CannibalsAtHome or PackageClass.PrisonerGathering or PackageClass.SlaverShipping =>
                _system.CaptiveFinished(_world, _squad, board, package, kind),
            // Away from home a bar package ends where it could not start (the original's finished test is its start test negated).
            PackageClass.Bar => !_squad.TownOwn && !board.InTown.IsNone && board.InTown != CampaignTown.OfSite(board.Town) && !BarHere(board),
            PackageClass.WanderingTrader => board.TraderState == 2,
            PackageClass.PassingAssault => _system.AssaultFinished(_world, _squad, board, package),
            PackageClass.OccupyTown => _system.OccupyFinished(_world, _squad, board),
            PackageClass.Refugee => _system.RefugeeFinished(_world, _squad, board),
            PackageClass.WarBase or PackageClass.WarAssault or PackageClass.WarAssaultCannibal => _system.OutOfCampaign(_world, _squad),
            PackageClass.CampaignDefend => _system.CampaignDefendFinished(_world, board),
            _ => false,
        };

        /// <summary>
        /// Whether a finished package may hand over to its <c>Leads to</c>: a passing-by assault only by its own rule, any other class always.
        /// </summary>
        public bool MayLead(Blackboard board, AiPackage package, PackageClass kind) =>
            kind != PackageClass.PassingAssault || _system.AssaultMayLead(_world, _squad, board);

        /// <summary>
        /// The running package of class <paramref name="kind"/> gives way to another: a passing-by assault or an occupation ends.
        /// </summary>
        public void Ended(Blackboard board, AiPackage package, PackageClass kind)
        {
            switch (kind)
            {
                case PackageClass.PassingAssault:
                    _system.AssaultEnded(_world, _squad);
                    break;
                case PackageClass.OccupyTown:
                    _system.OccupyEnded(_world, _squad, board);
                    break;
            }
        }

        /// <summary>
        /// A package of class <paramref name="kind"/> just started for the bound squad: a patrol stamps its hour, a bar package that relaxes
        /// takes a table (<see cref="AsksForTable"/>), and the other classes set up what they need.
        /// </summary>
        public void Started(Blackboard board, AiPackage package, PackageClass kind)
        {
            switch (kind)
            {
                case PackageClass.Patrol:
                case PackageClass.PatrolRunning:
                    board.PatrolStart = _world.GameHours;
                    break;
                case PackageClass.Bar:
                case PackageClass.BarOutdoors:
                    if (!AsksForTable(package)) break;

                    // A squad away from its town whose bar town is not loaded takes no table yet: the original's start needs the town's zone
                    // loaded and is run again by the target query until it is (docs/game/ai-tasks.md "Hang-around-bar package", Verified).
                    if (!_squad.TownOwn && BarTown(board) is >= 0 and var far && !_system.SiteLoaded(_world, far)) board.BarWaits = true;
                    else TakeTable(board, kind);
                    break;
                case PackageClass.CannibalRetreat:
                case PackageClass.SlaverShipping:
                    _system.CaptiveStarted(_world, _squad, board, package, kind);
                    break;
                case PackageClass.WanderingTrader:
                    _system.TraderStart(_world, _squad, board);
                    break;
                case PackageClass.PassingAssault:
                    _system.AssaultStarted(_world, _squad, board);
                    break;
                case PackageClass.OccupyTown:
                    _system.OccupyStarted(_world, _squad, board);
                    break;
                case PackageClass.Refugee:
                    RefugeeStarted(_world, _squad, board);
                    break;
                case PackageClass.Contract when package.Signal is HiredAllySignal or DefendBaseSignal:
                    _system.ContractStart(_world, _squad, board, package);
                    break;
                case PackageClass.CampaignDefend:
                    _system.CampaignDefendStart(_world, _squad, board);
                    break;
                case PackageClass.WarBase:
                case PackageClass.WarAssault:
                case PackageClass.WarAssaultCannibal:
                    _system.WarStart(_world, _squad, board);
                    break;
            }
        }

        /// <summary>
        /// The table of a bar package that starts: one of the bar town's (<see cref="BarTown"/>, <see cref="PickTable"/>); the outdoor kind
        /// without one has no bar.
        /// </summary>
        void TakeTable(Blackboard board, PackageClass kind)
        {
            if (BarTown(board) is >= 0 and var town
                && _system.PickTable(_world, town, _squad, outdoors: kind == PackageClass.BarOutdoors) is var (bar, table))
                (board.Home, board.BarTable) = (bar, table);
            else if (kind == PackageClass.BarOutdoors) board.Home = null;
        }

        /// <summary>
        /// A bar package that started while its town was not loaded (<see cref="Blackboard.BarWaits"/>) takes its table once the town is, on the
        /// package cadence.
        /// </summary>
        public void RetryTable(Blackboard board)
        {
            if (board.Current is not { } package || PackageClasses.Of(package.Signal) is not (PackageClass.Bar or PackageClass.BarOutdoors))
            {
                board.BarWaits = false;
                return;
            }

            if (BarTown(board) is >= 0 and var town && !_system.SiteLoaded(_world, town)) return;
            board.BarWaits = false;
            TakeTable(board, PackageClasses.Of(package.Signal));
        }

        /// <summary>
        /// Whether a goal of <paramref name="package"/> asks for the package's target (214 Relaxing). The original's bar package takes its table when
        /// the target is first asked for (docs/game/ai-tasks.md "Hang-around-bar package"), so one without such a goal, the Hub's Squatters, holds none.
        /// </summary>
        static bool AsksForTable(AiPackage package)
        {
            foreach (var goals in package.Roles)
            {
                foreach (var goal in goals)
                    if (goal.Task.Type == TaskType.RelaxInTownPackage) return true;
            }

            return false;
        }
    }

    /// <summary>
    /// Whether the zone of placed town <paramref name="site"/> is loaded (true where the world tracks no zones, as in a synthetic world).
    /// </summary>
    bool SiteLoaded(World world, int site) => world.ActiveZones is not { } active || active.Contains(data.Sites[site].Zone);

    /// <summary>
    /// The table a squad starting a bar package reserves (the original's <c>Package_HangAroundBar</c> start, docs/game/ai-tasks.md "Town goals"): the
    /// town's public bars are tried in turn from a random one (seeded by the squad and the tick), the first table of one no other squad's bar package
    /// holds wins; the <paramref name="outdoors"/> kind takes the first free outdoor table instead. A town without bars gives the normal kind none,
    /// as the original's filter keeps only indoor furniture for it. Null when there is none: the squad stays home.
    /// </summary>
    (TownBuilding Bar, int Table)? PickTable(World world, int site, Squad squad, bool outdoors)
    {
        var places = Places(site);
        int townFaction = site < data.SiteFactions.Count ? data.SiteFactions[site] : -1;

        // A table of a building whose faction is hostile to the squad's is skipped (the table finder's filter, FUN_1408ca670 / FUN_1406b26d0:
        // relation at most -30).
        bool Hostile(TownBuilding b) => data.Relations.Contains(squad.Faction) && BuildingOwner(world, b, townFaction) is >= 0 and var owner
            && data.Relations.Contains(owner) && data.Relations.IsHostile(squad.Faction, owner);
        var held = new HashSet<(TownBuilding, int)>();
        foreach (var other in world.Squads.All)
        {
            if (other != squad && other.Ai is { BarTable: >= 0, Home: { } at } b
                && b.CurrentClass is PackageClass.Bar or PackageClass.BarOutdoors)
                held.Add((at, b.BarTable));
        }

        if (outdoors)
        {
            foreach (var table in places.OutdoorTables)
                if (!held.Contains((table, 0)) && !Hostile(table)) return (table, 0);
            return null;
        }

        if (places.Bars.Count == 0) return null;
        int start = Rng.Int(Rng.Hash(world.Seed, (ulong)squad.Id << 32 ^ (ulong)world.Tick, RngPurpose.Think, SeedSalts.BarPick), places.Bars.Count);
        for (int k = 0; k < places.Bars.Count; k++)
        {
            var bar = places.Bars[(start + k) % places.Bars.Count];
            for (int t = 0; t < bar.Tables.Count; t++)
                if (!held.Contains((bar, t)) && !Hostile(bar)) return (bar, t);
        }

        return null;
    }

    /// <summary>
    /// The faction owning a town building: its property owner, else its level owner, else the town's (<paramref name="townFaction"/>).
    /// </summary>
    internal int BuildingOwner(World world, TownBuilding b, int townFaction) =>
        world.Fixtures.PropertyOwner(b.Key) ?? data.LevelOwnerOf(b, townFaction);

    #endregion
}
