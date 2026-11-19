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
    /// A character thinks every this many ticks (staggered by slot), and at once when its squad starts another package or its patrol moves on.
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
/// The AI (docs/simulation.md "AI"; the original's in docs/game/ai.md). Squads whose template lists <c>AI packages</c> get a <see cref="Blackboard"/>
/// that picks the package by slot and package class; each member thinks on its own cadence: it scores the goals of its role list, its race's <c>AI
/// Goals</c> and the default idle goal by tier (urgent, then non-urgent, then fluff; the first tier with a positive score wins), weight, hour window,
/// crowding and continuity, acts out the winner through <see cref="TaskBehaviours"/> (in its home building's nodes and beds where it has one); its
/// combat goals are goals of the same choice (AiSystem.Fights.cs), which <see cref="AggroSystem"/>'s look asks it to weigh again when it senses a new
/// enemy. The AI runs only characters that nothing else holds: not the player's (but those of a squad made autonomous, below their orders and jobs,
/// <see cref="SquadAutonomy"/>), not in a fight it did not start, fleeing, down, carrying, carried,
/// treating, looting or trading; in its own fight it chooses but acts out nothing (<see cref="InFight"/>), and an engine duty or fight is taken over
/// only by the goals that pre-empt it (<see cref="Preempts"/>). The members of a squad it does not run get a mind that weighs only their combat goals
/// (<see cref="AiMind.Engine"/>, <see cref="EngineMinds"/>). Roaming platoons keep their travel stand-in (<see cref="PopulationSystem"/>; their
/// off-screen mode follows their package, <see cref="PlatoonJobs"/>).
/// <list type="bullet">
/// <item><c>Think</c> (parallel): the choice and the act-out write only the character's own state (its task, goal, path flags and
/// <see cref="AiMind"/>); they read the squad's blackboard, which only the serial steps change.</item>
/// <item><c>Inputs</c> (serial): contracts. <c>SlowWorld</c> (serial): blackboards, homes and minds for new squads; on the package cadence the
/// intact test, the package choice, the patrol's progress and the crowding counts.</item>
/// </list>
/// </summary>
public sealed partial class AiSystem(PopulationData data, AiSettings? settings = null) : ITickSystem, IStateHashed, IDecisionOwner
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
    PopulationData Data => data;

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
    /// What the code mods add (their NPC goal types, docs/modding.md "NPC goals"); null: none. Set before the world is made.
    /// </summary>
    public SimulationExtensions? Extensions { get; set; }

    /// <summary>
    /// The code mods' NPC goals in this world (<see cref="BindModGoals"/>); null without any, and then no mod path of the think or the serial step
    /// runs.
    /// </summary>
    NpcGoals? _modGoals;

    /// <summary>
    /// Think periods a character weighs a mod's goal at 0 after the mod refused to start it (<see cref="AiMind.ModGoalBackoff"/>).
    /// </summary>
    const int ModGoalBackoffThinks = 4;

    /// <summary>
    /// Asks the world's <see cref="INpcGoalOwner"/> systems for their goals, once when the world is made (<see cref="World"/>'s constructor); nothing
    /// is built when no mod claimed a goal type.
    /// </summary>
    internal void BindModGoals(IReadOnlyList<ITickSystem> systems) => _modGoals = NpcGoals.Build(Extensions, systems);

    #endregion

    #region Think

    /// <summary>
    /// The blackboard an <see cref="AiMind.Engine"/> mind chooses under: no package, nothing claimed (its combat goals read none of it).
    /// </summary>
    static readonly Blackboard EngineBoard = new([]);

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
    /// The serial schedule step: stamps every character's <c>followMe</c> before the parallel think.
    /// </summary>
    public void Schedule(World world) => _followMe.Stamp(world);

    /// <summary>
    /// The parallel think: each character the AI runs, on its turn or when asked, chooses a goal and acts it out (writes its own slot only).
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
                || (mind.Engine ? EngineBoard : squad.Ai) is not { } board)
                continue;

            // Cheapest test first: most characters are not due this tick (a far character's step covers several ticks: due when its turn fell in
            // them, as World.Turn; the think's turn is anchored at Tick + slot, the sweeps' at Tick - slot, so a slot's aggro look and its think
            // interleave).
            if ((world.Tick + i) % every >= world.StepTicks(i) && mind.Version == board.Version && !mind.Replan) continue;
            mind.Replan = false;
            var id = table.IdOf(i);
            var roles = mind.Engine ? mind.EngineGoals : ObeyedRoles(world, squad, cold) ?? board.Roles[(int)RoleOf(squad, id, cold)];
            ref var n = ref table.Next[i];
            if (cold.IsPlayer && SquadAutonomy.OnJob(n, cold, mind))
            {
                AskOffJob(world, i, id, cold, mind, squad, board, roles, hour);
                continue;
            }

            if (Holds(n, cold))
            {
                if (mind.ModGoalStarted) EndModGoal(mind);   // the mod let it go: its act-out is over
                if (mind.ModGoalBackoff is not null) mind.PruneBackoff(world.Tick);
                if (mind.Engine)
                {
                    // Its own fight, with nothing but its combat goals and Self preservation to weigh: none wanted ends the fight, and the engine's
                    // routine takes it back.
                    if (Choose(world, i, id, mind, squad, board, roles, hour, Restrict.Combat) is { } kept) InFight(mind, kept, board);
                    else
                    {
                        mind.LeaveFight = true;
                        Interlocked.Exchange(ref _leaving, 1);
                    }

                    continue;
                }

                var goal = Choose(world, i, id, mind, squad, board, roles, hour)!;
                if (mind.Player) mind.Urgent = goal.Task.Priority == TaskPriority.Urgent;
                if (cold.InCombat) InFight(mind, goal, board);
                else Act(world, i, mind, goal, board);
                continue;
            }

            // A character on an engine duty or in an engine fight: only the goals that pre-empt it (Preempts) are weighed.
            if (Preempts(n, cold) is not Restrict.None and var only && Choose(world, i, id, mind, squad, board, roles, hour, only) is { } urgent)
            {
                // Self preservation's run; or a fight (its own defence, a combat goal's), which a character already fighting does not start again.
                // On its Fixing Legs treatment: the goal itself goes on; any other urgent goal that won ends the treatment in the serial step and
                // is acted out at the next think.
                if (ReferenceEquals(urgent, SelfPreservationGoal) && mind.Step == TaskType.RunAway) mind.FleeRequest = FleeAsk.Threat;
                else if (only == Restrict.Urgent && urgent.Task.Type == TaskType.HealMyLegs) continue;
                else if (only == Restrict.Urgent && TaskBehaviours.Of(urgent.Task.Type) is not (AiBehaviour.Fight or AiBehaviour.SelfPreserve))
                    LeaveTreatment(mind);
                else if (!cold.InCombat) AskFight(world, i, urgent.Task.Type, mind);
            }
        }
    }

    /// <summary>
    /// The think of a player character on a job's task (<see cref="SquadAutonomy.OnJob"/>): only its urgent package goals are weighed; one that
    /// plans is kept as <see cref="AiMind.Urgent"/> and asks the decision step, whose source takes the character off the job
    /// (<see cref="SquadAutonomy.PackageGoals"/>). Writes only its own slot.
    /// </summary>
    void AskOffJob(World world, int slot, CharacterId id, CharacterCold cold, AiMind mind, Squad squad, Blackboard board,
        IReadOnlyList<AiGoal> roles, int hour)
    {
        bool wanted = Choose(world, slot, id, mind, squad, board, roles, hour, Restrict.Urgent) is not null;
        if (wanted && !mind.Urgent) DecisionSystem.Request(cold);

        mind.Urgent = wanted;
        mind.Version = board.Version;
    }

    /// <summary>
    /// The choice of a character in its own fight (<see cref="OwnsFight"/>): a combat goal or Self preservation's fight (row 13) keeps the fight
    /// running (the fight is its plan's task, already queued: no new start, whichever of them is current now; the combat system re-targets); Self
    /// preservation's run (35) or a goal 35 asks the flee step, which stops the fight; any other goal ends the fight in the serial step, and the
    /// character thinks again at the next tick to act it out (docs/game/ai-tasks.md "Combat goals"). Writes only its own mind.
    /// </summary>
    void InFight(AiMind mind, AiGoal goal, Blackboard board)
    {
        var behaviour = TaskBehaviours.Of(goal.Task.Type);
        if (behaviour is not (AiBehaviour.Fight or AiBehaviour.SelfPreserve or AiBehaviour.Flee))
        {
            mind.LeaveFight = true;
            Interlocked.Exchange(ref _leaving, 1);
            return;
        }

        mind.Goal = goal;
        mind.Behaviour = behaviour;
        mind.Version = board.Version;
        if (behaviour == AiBehaviour.Flee || behaviour == AiBehaviour.SelfPreserve && mind.Step == TaskType.RunAway)
            mind.FleeRequest = behaviour == AiBehaviour.SelfPreserve ? FleeAsk.Threat : FleeAsk.Enemies;
    }

    /// <summary>
    /// Which goals of a character the AI does not hold may still take it over (<see cref="Preempts"/>).
    /// </summary>
    internal enum Restrict : byte
    {
        /// <summary>
        /// No restriction: every goal may take it over.
        /// </summary>
        None,
        /// <summary>
        /// Self preservation alone (its run, or its fight through row 13 against its own attacker).
        /// </summary>
        Self,
        /// <summary>
        /// The combat goals and Self preservation (priority 3): a fight they start ends an engine duty through its task's end; what an
        /// <see cref="AiMind.Engine"/> mind weighs on its routine and in its own fight.
        /// </summary>
        Combat,
        /// <summary>
        /// Every urgent (priority 3) goal of the lists: what a player character on a job weighs (<see cref="AskOffJob"/>), and an NPC on its own
        /// Fixing Legs treatment (<see cref="TreatsOwnLegs"/>).
        /// </summary>
        Urgent,
    }

    /// <summary>
    /// What may take over an NPC with a mind that the AI does not hold (<see cref="Holds"/>), as the original's chooser would (its engine duties are
    /// goals and jobs of the same chooser, below a priority-3 goal, docs/game/ai-tasks.md "Precedence"): on an engine duty whose system lets a fight
    /// interrupt it, the combat goals and Self preservation; on any other (and in a fight it did not start: a dialogue's, an assault's, a duty's),
    /// Self preservation alone, whose fight or run (<see cref="SelfPreservation.Runs"/>) ends the duty's task as any fight's start does (in a fight,
    /// only its run changes anything). The duties a fight may interrupt: treating (<see cref="FirstAid.FirstAidSystem"/> lets go of the patient at
    /// the fight's start), a town errand's walk but the cage walk (<see cref="Kidnap.TownDutySystem"/> waits for the fight to end and walks on; one
    /// carrying an intruder out fights with the body on its shoulder, as the original's carrier does), a captive job's walk carrying nothing and its meal
    /// (<see cref="InterruptibleDuty"/>), a code mod's goal below the urgent tier (<see cref="InterruptibleModGoal"/>), and the routine of an
    /// <see cref="AiMind.Engine"/> mind (<see cref="EngineRoutine"/>).
    /// Nothing for one fleeing, imprisoned, carried or down, nor for a gunner or a carrier in a fight.
    /// </summary>
    internal static Restrict Preempts(in CharacterHot n, CharacterCold cold)
    {
        if (cold.Ai is not { } mind || cold.IsPlayer || cold.Fleeing || cold.IsImprisoned || !cold.CarriedBy.IsNone
            || cold.Medical is { Incapacitated: true })
            return Restrict.None;

        if (cold.InCombat) return cold.Gunning is null && cold.Carrying.IsNone ? Restrict.Self : Restrict.None;
        if (cold.Engaged) return InterruptibleDuty(n, cold) || InterruptibleModGoal(n, cold, mind) ? Restrict.Combat : Restrict.Self;
        if (mind.Engine && EngineRoutine(n, cold)) return Restrict.Combat;

        // Fixing Legs' own treatment: the original's chooser weighs every goal on each update, and while the urgent goal is wanted only an urgent
        // goal can outscore it; once the legs work the treatment still runs until the leg is fully bandaged (Observed in the original), and only
        // an urgent goal that plans takes over.
        if (TreatsOwnLegs(n, cold)) return Restrict.Urgent;

        return n.Task == (byte)CharacterTask.Treat || InterruptibleDuty(n, cold) ? Restrict.Combat : Restrict.Self;
    }

    /// <summary>
    /// Whether the character is on the treatment of itself that its Fixing Legs goal started (row 269, <see cref="AiBehaviour.HealLegs"/>).
    /// </summary>
    internal static bool TreatsOwnLegs(in CharacterHot n, CharacterCold cold) =>
        n.Task == (byte)CharacterTask.Treat && cold.Running is { Task: CharacterTask.Treat, Row: TaskType.HealMyLegs };

    /// <summary>
    /// An urgent goal outscored Fixing Legs while the character treats itself: the serial step of the same tick ends the treatment
    /// (<see cref="LeaveFights"/>), and the character thinks again at the next tick, which acts the goal out. Writes only its own mind.
    /// </summary>
    void LeaveTreatment(AiMind mind)
    {
        mind.LeaveFight = true;
        Interlocked.Exchange(ref _leaving, 1);
    }

    /// <summary>
    /// A duty holding the character (<see cref="CharacterCold.Engaged"/>) that a fight may interrupt: a town errand's walk but the cage walk
    /// (<see cref="Kidnap.TownDutySystem"/> waits for the fight's end and walks on; a carrier fights with the body on its shoulder), a captive job's
    /// walk carrying nothing (<see cref="Kidnap.CaptiveSystem"/> gives the job up at the fight's start), or the meal it stands at (220 Eating is a
    /// priority-2 goal's task, below the combat goals; a vampiric meal lasts minutes, #623).
    /// </summary>
    static bool InterruptibleDuty(in CharacterHot n, CharacterCold cold)
    {
        if (n.Task == (byte)CharacterTask.Idle) return cold.Carrying.IsNone && cold.ActionClip == Kidnap.CaptiveSystem.EatClip;
        if (n.Task != (byte)CharacterTask.GoTo || cold.Running is not { Source: TaskSource.Goal, Task: CharacterTask.GoTo } run) return false;

        return Kidnap.TownDutySystem.IsErrandRow(run.Row) || cold.Carrying.IsNone && Kidnap.CaptiveSystem.IsJobRow(run.Row);
    }

    /// <summary>
    /// A code mod's goal below the urgent tier walking on its act-out's own walk, carrying nothing (as a captive job's walk): the combat goals, a
    /// tier above it, may take the character over as they take over the engine's own priority-2 duties; the fight's start ends the mod's task through its
    /// handler. Not while it stands: a trade or an open conversation holds a standing character with its task and provenance as they were, so only
    /// the walk tells the mod's act-out from such a hold. (A Talk order's approach does not stand a walking NPC, so the walk may still be taken over
    /// until the conversation opens and stands it.)
    /// </summary>
    static bool InterruptibleModGoal(in CharacterHot n, CharacterCold cold, AiMind mind) =>
        mind.Goal is { Task.Priority: < TaskPriority.Urgent } && n.Task == (byte)CharacterTask.GoTo && cold.Carrying.IsNone
        && OnModTask(cold, mind) && cold.Running!.Runs(CharacterTask.GoTo);

    /// <summary>
    /// Whether the character's provenance is the act-out a mod's goal started (<see cref="AiMind.ModGoalStarted"/>): a goal's
    /// (<see cref="TaskSource.Goal"/>), of the row the start left running (<see cref="AiMind.ModGoalRow"/>, from 1000 up), started at or after that
    /// task (<see cref="AiMind.ModGoalTick"/>). The task may have moved on within it (a walk that arrived and stands).
    /// </summary>
    internal static bool OnModTask(CharacterCold cold, AiMind mind) =>
        mind.ModGoalStarted && (int)mind.ModGoalRow >= SimulationExtensions.FirstModCode
        && cold.Running is { Source: TaskSource.Goal } run && run.Row == mind.ModGoalRow && run.Tick >= mind.ModGoalTick;

    /// <summary>
    /// The mod let the character go (the AI holds it again): its act-out is over, so the goal ends with it (as a planned goal ends with its last
    /// step) and the next choice weighs it afresh, without continuity.
    /// </summary>
    internal static void EndModGoal(AiMind mind)
    {
        mind.ModGoalStarted = false;
        mind.Goal = null;
        mind.Plan.Clear();
    }

    /// <summary>
    /// The engine's routine of an <see cref="AiMind.Engine"/> mind, which a fight its combat goals start interrupts: standing, wandering, following
    /// its leader, or the roaming leader's walk on its trip (row 53 of the population's travel stand-in, <see cref="PopulationSystem"/>; the trip
    /// goes on after the fight).
    /// </summary>
    static bool EngineRoutine(in CharacterHot n, CharacterCold cold) => (CharacterTask)n.Task switch
    {
        CharacterTask.Idle or CharacterTask.Wander or CharacterTask.Follow => true,
        CharacterTask.GoTo => OnTrip(n, cold),
        _ => false,
    };

    /// <summary>
    /// Whether the character's fight is the AI's own: the one the serial step started for its combat goal (<see cref="AiMind.FightFrom"/>), still
    /// running. A fight the combat system or another system started (a self-defence, a duty's, a dialogue's, an assault's) is not.
    /// </summary>
    internal static bool OwnsFight(CharacterCold cold, AiMind mind) =>
        mind.FightFrom != 0 && cold.Running is { Task: CharacterTask.Attack } run && run.Tick == mind.FightFrom;

    /// <summary>
    /// The HUD's Goal text: the goal's name while the AI holds the character; "Running away" (35's name, <c>Task_Runaway</c>) for an NPC on the flee
    /// step, whichever of Self preservation, a goal 35 or a broken squad started it, rather than its walk's "Moving"; "Stumbling" (147) for a stagger
    /// outside a fight (<see cref="CharacterCold.StumbleTaskUntil"/>); the captive goal's name on its job (<see cref="TakeCaptiveGoal"/>: "Eat
    /// body"); "Fixing Legs" on its own Fixing Legs treatment (<see cref="TreatsOwnLegs"/>); a code mod's goal by its AI_TASK record's name
    /// while the AI holds the character and while it is engaged on the act-out the mod started (<see cref="OnModTask"/>: walking or standing
    /// on that act-out's row); null for the rest (the HUD then names the movement task).
    /// </summary>
    public static string? ActivityText(in CharacterHot n, CharacterCold cold, long tick = 0) =>
        cold.StumbleTaskUntil > tick ? TaskBehaviours.Text(TaskType.StumbleTaskForced)
        : cold is { Fleeing: true, IsPlayer: false, Medical.Incapacitated: not true } ? TaskBehaviours.Text(TaskType.RunAway)
        : cold is { Engaged: true, InCombat: false, Ai: { Behaviour: AiBehaviour.Captive, Goal: { } job } } ? TaskBehaviours.Text(job.Task.Type)
        : cold.Ai is not null && TreatsOwnLegs(n, cold) ? TaskBehaviours.Text(TaskType.HealMyLegs)
        : cold is { Engaged: true, InCombat: false, Ai: { ModGoalStarted: true, Goal: { } modGoal } modMind } && OnModTask(cold, modMind)
            ? GoalText(modGoal)
        : cold.Ai is { Goal: { } goal } && Holds(n, cold) ? GoalText(goal) : null;

    /// <summary>
    /// A goal's HUD text: the TaskData display text (<see cref="TaskBehaviours.Text"/>), or for a code mod's type (from 1000 up, which only a mod's
    /// goal makes current) its AI_TASK record's name.
    /// </summary>
    static string GoalText(AiGoal goal) =>
        (int)goal.Task.Type >= SimulationExtensions.FirstModCode && goal.Task.Name.Length > 0 ? goal.Task.Name : TaskBehaviours.Text(goal.Task.Type);

    /// <summary>
    /// Whether the AI runs the character now: it has a mind and nothing else holds it (a fight it did not start, a flight, an order, a carry, a
    /// trade, a knock-out). In its own fight it thinks as anyone does (on the cadence and the replan triggers) and may leave it for another goal
    /// (<see cref="InFight"/>).
    /// </summary>
    public static bool Holds(in CharacterHot n, CharacterCold cold)
    {
        if (cold.Ai is not { } mind || cold.Fleeing || cold.Engaged || cold.IsImprisoned || !cold.CarriedBy.IsNone
            || !cold.Carrying.IsNone || cold.Medical is { Incapacitated: true })
            return false;

        // A player character: only one of an autonomous squad, on no order's or job's task (SquadAutonomy.Free).
        if (cold.IsPlayer && !SquadAutonomy.Free(n, cold, mind)) return false;

        if (cold.InCombat) return n.Task == (byte)CharacterTask.Attack && OwnsFight(cold, mind);
        if (mind.Engine) return false;
        return (CharacterTask)n.Task switch
        {
            CharacterTask.Idle or CharacterTask.Wander or CharacterTask.Follow => true,
            // The walks to its spots, and lying or sitting there, are the AI's own.
            CharacterTask.GoTo => mind.Behaviour is AiBehaviour.Post or AiBehaviour.Sleep or AiBehaviour.Sit or AiBehaviour.GoHome
                or AiBehaviour.Patrol or AiBehaviour.Turret or AiBehaviour.TownNode or AiBehaviour.Shop or AiBehaviour.Relax
                or AiBehaviour.ToTown or AiBehaviour.Travel or AiBehaviour.Work or AiBehaviour.Investigate
                or AiBehaviour.PatrolTown,
            CharacterTask.Rest => mind.Behaviour is AiBehaviour.Sleep or AiBehaviour.Sit or AiBehaviour.Relax,
            // In the bed it went to (or lying in one when loaded, before its first think, or laid in by a carrier while it was down: no task of
            // its own keeps it there, and its goal of before then is stale), its think lets it lie on or gets it out (Act).
            CharacterTask.Sleep => cold.InSomething == Locks.HeldState.Bed
                && (mind.Behaviour == AiBehaviour.Sleep || mind.Goal is null || cold.Running?.Runs(CharacterTask.Sleep) != true),
            _ => false,
        };
    }

    /// <summary>
    /// Job 187 Obedient Slave (<c>FUN_14050e5b0</c>, docs/game/ai-tasks.md "Obedient Slave"): a slave (state 1) not sneaking takes its owner
    /// squad's <c>Slave AI Goals</c> instead of its own lists, whatever its squad role: a guard enslaved by its own squad stops guarding (#991).
    /// Null: it plans with its own squad's (no master squad, an empty Slave list, or a job a split took,
    /// <see cref="CharacterCold.ObedienceLost"/>).
    /// </summary>
    internal static IReadOnlyList<AiGoal>? ObeyedRoles(World world, Squad squad, CharacterCold cold) =>
        cold.SlaveState == 1 && !cold.ObedienceLost && cold.SlaveMaster >= 0 && (cold.Orders & CharacterOrders.Sneak) == 0
            && world.Squads.Find(cold.SlaveMaster) is { Ai: { } master } && master.Roles[(int)AiRole.Slave] is { Count: > 0 } list ? list : null;

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
    /// mind or a squad blackboard, for a player character's mind (an autonomous squad's: its mod goals are weighed at 0), and for an engine mind
    /// (a squad the population moves) but for a combat goal of its combat or race lists. For code mods (docs/modding.md "NPC goals"): whether a goal
    /// of theirs is still weighed for a character they act it out for (unlike <see cref="ListsGoal"/>, the hour's window counts). Serial phases, or
    /// the character's own partition.
    /// </summary>
    public static bool WeighsGoal(World world, int slot, TaskType type)
    {
        if (world.Characters.Cold(slot) is not { Ai: { Squad: var squad, Player: false } mind } cold) return false;

        int hour = world.Hour;
        if (mind.Engine)
            return TaskBehaviours.Of(type) == AiBehaviour.Fight && (Lists(mind.EngineGoals, type, hour) || Lists(mind.RaceGoals, type, hour));
        if (squad.Ai is not { } board) return false;

        var roles = ObeyedRoles(world, squad, cold) ?? board.Roles[(int)RoleOf(squad, world.Characters.IdOf(slot), cold)];
        return Lists(roles, type, hour) || Lists(mind.RaceGoals, type, hour);
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
    /// The step that separates two candidates of the same score (the original's 0.0001).
    /// </summary>
    const float TieStep = 1e-4f;

    /// <summary>
    /// The factor on the followed character's <c>followMe</c> in the following desire (<c>DAT_14167e8d8</c>, <see cref="FollowDesire"/>).
    /// </summary>
    const float FollowShare = 0.5f;

    /// <summary>
    /// The tiers the chooser weighs in turn (<see cref="Choose"/>).
    /// </summary>
    static readonly TaskPriority[] Tiers = [TaskPriority.Urgent, TaskPriority.NonUrgent, TaskPriority.Fluff];

    /// <summary>
    /// The chooser's candidates of one tier, a scratch list per worker thread (<see cref="Candidates"/>).
    /// </summary>
    [ThreadStatic] static List<(AiGoal Goal, float Score, float Desire)>? s_candidates;
    /// <summary>
    /// The calling worker thread's candidate list (<see cref="s_candidates"/>), made on its first use.
    /// </summary>
    static List<(AiGoal Goal, float Score, float Desire)> Candidates => s_candidates ??= [];

    /// <summary>
    /// The goal to act out, with its plan in <see cref="AiMind.Plan"/>: first the current goal's running step is tested (<see cref="EndStep"/>); then
    /// by tier (urgent, non-urgent, fluff), the goals with a positive score highest first, leaving out one whose list A already holds; the current goal
    /// with a step running is kept as it is (<see cref="Kept"/>), else the first whose plan succeeds wins (docs/game/ai-tasks.md "Planner": the pick).
    /// Idle when none does. With <paramref name="only"/>, only the priority-3 goals that may take over a character the AI does not hold
    /// (<see cref="Preempts"/>) are weighed, and null comes back when none plans.
    /// </summary>
    AiGoal? Choose(World world, int slot, CharacterId id, AiMind mind, Squad squad, Blackboard board, IReadOnlyList<AiGoal> roles,
        int hour, Restrict only = Restrict.None)
    {
        var race = mind.RaceGoals;
        var candidates = Candidates;
        var plans = PlanContext.Local.Bind(this, world, slot, mind, board);
        if (only == Restrict.None) EndStep(world, slot, id, mind, squad, board, plans, hour);

        // While a goal it committed to is running, only a tier above that goal's priority may take over; if none plans, the goal and its plan stay
        // (FUN_140510ef0 in committed mode, Verified). Not past its hours: the plan step ends a goal's plan then, and the update clears a goal
        // left with no plan however long its commitment runs (FUN_14050d970, FUN_140511130, Verified; docs/game/ai-tasks.md "Precedence").
        var committed = only == Restrict.None && mind.Goal is { } running && mind.Plan.Count > 0 && TaskProfiles.Of(running.Task.Type).Commits
            && world.GameHours < mind.CommitUntil && running.InWindow(hour) ? running : null;
        foreach (var tier in Tiers)
        {
            if (committed is not null && tier <= committed.Task.Priority || only != Restrict.None && tier != TaskPriority.Urgent) break;
            candidates.Clear();
            void Consider(AiGoal g)
            {
                if (g.Task.Priority != tier || !g.InWindow(hour)) return;

                // The list entries of Self preservation are the engine's own goal, which every character has once.
                if (g.Task.Type == TaskType.SelfPreservation && !ReferenceEquals(g, SelfPreservationGoal)) return;
                if (only switch
                {
                    Restrict.Self => !ReferenceEquals(g, SelfPreservationGoal),
                    Restrict.Combat => TaskBehaviours.Of(g.Task.Type) != AiBehaviour.Fight && !ReferenceEquals(g, SelfPreservationGoal),
                    _ => false,
                })
                {
                    return;
                }

                var profile = TaskProfiles.Of(g.Task.Type);
                float desire = Desire(world, TaskBehaviours.Of(g.Task.Type), g.Task.Type, profile, squad, board, mind, id, g.Task.Target);
                if (desire <= 0) return;

                float score = Score(world, g, profile, desire, squad, board, mind, id);
                if (score <= 0) return;

                Rank(candidates, g, score, desire);
            }

            // Index loops: a foreach over the interface would box an enumerator per list and pass.
            for (int k = 0; k < roles.Count; k++) Consider(roles[k]);
            for (int k = 0; k < race.Count; k++) Consider(race[k]);
            if (tier == TaskPriority.Urgent && !mind.Player) Consider(SelfPreservationGoal);   // a player character's is the decision step's
            for (int k = 0; k < candidates.Count; k++)
            {
                var g = candidates[k].Goal;
                if (Kept(mind, g)) return g;
                if (plans.TryPlan(g, candidates[k].Desire)) return g;
            }
        }

        if (only != Restrict.None) return null;
        if (committed is not null) return committed;

        // The default task (IDLE, weight 0.1) is no candidate of a tier: the chooser tries it only when every tier failed (FUN_140510ef0, Observed).
        if (plans.TryPlan(Idle, Desire(world, TaskBehaviours.Of(Idle.Task.Type), Idle.Task.Type, TaskProfiles.Of(Idle.Task.Type), squad, board,
            mind, id, Idle.Task.Target)))
            return Idle;

        mind.Plan.Clear();
        return Idle;
    }

    /// <summary>
    /// The plan step before the choice (<see cref="PlanSteps"/>): the current goal's running step ends by its own tests and the next starts; with no
    /// step left the goal goes, committed or not (the original keeps a committed goal over the next update only for a choice limited to the tiers
    /// above it, then clears it, <c>FUN_14050d7d0</c>, <c>FUN_140511130</c>; one think here does both), so the choice that follows plans afresh. A
    /// walk or a stand is first aimed where the act-out goes (<see cref="FollowsWalk"/>).
    /// </summary>
    void EndStep(World world, int slot, CharacterId id, AiMind mind, Squad squad, Blackboard board, PlanContext plans, int hour)
    {
        if (mind.Goal is not { } current || !PlanContext.Stepped(current)) return;
        if (Displaced(world, slot, mind))
        {
            mind.Plan.Clear();
            mind.Goal = null;
            return;
        }

        if (FollowsWalk(mind))
        {
            var type = current.Task.Type;
            float desire = Desire(world, TaskBehaviours.Of(type), type, TaskProfiles.Of(type), squad, board, mind, id, current.Task.Target);
            plans.TryPlan(current, desire, current: true);
        }

        if (!PlanSteps.Run(world, slot, mind, plans, current, hour)) mind.Goal = null;
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
    static bool Kept(AiMind mind, AiGoal candidate) =>
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
    float Score(World world, AiGoal g, TaskProfile profile, float desire, Squad squad, Blackboard board, AiMind mind, CharacterId id)
    {
        int crowd = profile.Crowd == 1 ? 0
            : board.CrowdOf(g.Task.Type, TaskDataTable.Of(g.Task.Type)?.Is(TaskDataFlags.NeedsTarget) ?? false,
                CrowdTarget(g.Task.Target, squad, board, id), id);
        float score = desire * g.Weight * (crowd > 0 ? MathF.Pow(profile.Crowd, crowd) : 1);

        if (mind.Goal is { } current && current.Task.Type == g.Task.Type && current.Task.Target == g.Task.Target
            && !Displaced(world, id.Slot, mind))
        {
            score *= Settings.Continuity;

            // A goal it committed to holds at least 0.4 until the commitment runs out, then at most 0.05 (docs/game/ai.md "Packages and
            // goals", Observed).
            if (profile.Commits) score = world.GameHours < mind.CommitUntil ? MathF.Max(score, 0.4f) : MathF.Min(score, 0.05f);

            // While the running task's class says it is not finished (its slot 4, TaskFinished), a score below 1 is raised once more
            // (FUN_14050df10, Verified).
            if (score < 1 && TaskFinished.Unfinished(world, id.Slot, mind.Step)) score *= Settings.Continuity;
        }

        return score;
    }

    /// <summary>
    /// Puts a candidate in the list, highest score first.
    /// </summary>
    static void Rank(List<(AiGoal Goal, float Score, float Desire)> candidates, AiGoal g, float score, float desire)
    {
        // Highest first. The original keys its candidates by score and, while the key is taken, adds 0.0001 (FUN_14050df10, Verified): of
        // equal scores the goal listed later ranks first.
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
        candidates.Insert(at, (g, score, desire));
    }

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
    /// home town and 0 in it (docs/game/perception.md 7.2, Observed); a goal the engine cannot act out is 0, but a code mod's
    /// (<see cref="ModDesire"/>).
    /// </summary>
    float Desire(World world, AiBehaviour behaviour, TaskType type, TaskProfile profile, Squad squad, Blackboard board, AiMind mind,
        CharacterId self, TaskTargetType targeting)
    {
        if (behaviour == AiBehaviour.None) return _modGoals is null ? 0 : ModDesire(_modGoals, world, type, squad, mind, self, targeting);

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
    /// A code mod's goal's desire (<see cref="INpcGoal.Desire"/>, the whole desire: its type has the default profile, no commitment and no crowding);
    /// 0 for a type no mod registered, for a player character's mind (an autonomous squad's: its goals stay the engine's), and for a type whose
    /// start the mod refused, until its back-off ends (<see cref="AiMind.ModGoalBackoff"/>).
    /// </summary>
    static float ModDesire(NpcGoals goals, World world, TaskType type, Squad squad, AiMind mind, CharacterId self, TaskTargetType targeting) =>
        mind.Player || mind.BackedOff(type, world.Tick)
            ? 0
            : goals.Desire(world, new NpcGoalAsk(self.Slot, self, squad, type, targeting));

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
            // Sleeping on the ground has row 98's desire (AI slot 30): the registry gives rows 98, 153, 175, 239 and 261 the same desire
            // (FUN_140319a60, Verified).
            AiBehaviour.Sleep when type == TaskType.SleepOnFloor => SleepingDesire(world, board, self.Slot),
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
            AiBehaviour.TownNode =>
                _townPlaces.WanderIn(board.Town, world.Characters.Previous[self.Slot].Position).Count > 0 || OccupiedBuildings(world, board) > 0
                    ? 1 : 0,
            // Row 36 has no finder: its update strolls round the squad's town, or where the character stands without one (Task_PatrolTown,
            // docs/game/ai-tasks.md).
            AiBehaviour.PatrolTown => 1,
            // Shopping is wanted while its list has something left to buy today (FUN_140999620), and there is a shop to go to.
            // The finder's factor follows its pick: 1 for a shop, 0.4 for any other public building, 0 when it finds none (FUN_1409997f0).
            AiBehaviour.Shop => ShopDesire(world, self, mind, board.Town),
            // A worker needs a mine, bench or refinery in its town (the finders return 0 without one).
            AiBehaviour.Work => WorkAllowed(world, self.Slot, board.HomeTown)
                && (HoldsWork(mind, board.HomeTown, type) || PickWork(world, self, board.HomeTown, mind.WorkEpoch, type) is not null) ? 1 : 0,
            // Relaxing needs a free seat of the package's bar, sitting around a free chair of the home (claimed in
            // <see cref="TownPlaceClaims.ClaimSeats"/>); the original's finder returns 0 without one (FUN_1408caf00).
            AiBehaviour.Relax => board.BarTable >= 0 && board.SeatClaims.ContainsKey(self.Slot) && !mind.SeatUnreachable ? 1 : 0,
            AiBehaviour.Sit => board.SitClaims.ContainsKey(self.Slot) && !mind.SeatUnreachable ? 1 : 0,
            AiBehaviour.ToTown => TownAt(world.Characters.Previous[self.Slot].Position) >= 0 ? 0 : 1,
            // To the package's mission town while not in it.
            AiBehaviour.Travel => board.RefugeeTarget >= 0 ? 1
                : board.Destination.IsNone || CurrentTown(world, world.Characters.Previous[self.Slot].Position) == board.Destination ? 0 : 1,
            // Only while an alarm of its town is in reach (the row's done state !ALARMS_IN_THE_VICINITY).
            AiBehaviour.Investigate => AlarmOf(world, self.Slot, type, board) is null ? 0 : 1,
            // The assault's finder factor: 1 while a member of the mission target's squad passes its tests, else 0 (FUN_140793eb0).
            AiBehaviour.RescueLeader => mind.RescueWant,
            // Find and rescue if there's beds: its desire and its finder's factor, both kept by the carry system's serial sweep.
            AiBehaviour.RescueToBed => mind.BedRescueWant,
            // Crouching is wanted always (it holds the pose while it runs); standing up (its effects !24 !25 !26) only while crouched.
            AiBehaviour.Crouch => type == TaskType.Crouch || world.Characters.Cold(self.Slot) is { Crouched: true } ? 1 : 0,
            AiBehaviour.Assault => targeting == TaskTargetType.SquadMission && !AssaultTargetFor(world, self.Slot, board).IsNone ? 1 : 0,
            // Self preservation: the threat level (AI slot 17); Running away: row 35's own (AI slot 55), a goal of the packages that list it.
            AiBehaviour.SelfPreserve => SelfPreservationDesire(world, self.Slot, mind),
            AiBehaviour.Flee => world.Characters.Cold(self.Slot) is { } runner
                ? SelfPreservation.RunDesire(runner, SelfPreservation.Walled(world, self.Slot)) : 0,
            // A combat goal: its own desire's zeros and factors, times its finder's factor (an attack goal's 1 with a target, a protect goal's fight
            // threat; AiSystem.Fights.cs).
            AiBehaviour.Fight => FightDesire(world, self.Slot, type, mind.Squad) is var want and > 0
                ? want * FightFactor(world, self.Slot, type, mind)
                : 0,
            // The Medic goal: its desire times the finder's score from the serial sweep (the desire's carrying case is a gate of the finder).
            AiBehaviour.Medic => world.Characters.IsAlive(mind.MedicPatient) ? mind.MedicScore * MedicDesire(world, self.Slot) : 0,
            // Fixing Legs (269) has no finder: its own desire (FUN_1409983b0); the planner tests its list B (kit, arm, water, threat, weapon).
            AiBehaviour.HealLegs => HealLegsDesire(world, self.Slot),
            // A captive goal: the desire its job start kept on the sweep turn of a seated character (0 for the rest, whose jobs the sweep starts
            // itself; Kidnap/CaptiveSystem.Offers.cs).
            AiBehaviour.Captive => mind.CaptiveDesire(type),
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

        return FollowDesire(type, target);
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
    /// in the walker's zone (<b>Verified</b>, <c>FUN_1409fc6c0</c>, <c>FUN_140999a60</c>: one without such a node gives no target); shopping, a
    /// random shop of the town that sells what its list wants, else a random public building (<see cref="ShopFor"/>): a shop at a general node in it,
    /// else at it, any other building at its origin; the wanderer, the nearest town that is no nest marker. Null when there is none.
    /// </summary>
    Vector3? TargetFor(World world, CharacterId self, AiBehaviour behaviour, int town, AiMind mind)
    {
        ulong roll = TargetRoll(world, self, behaviour);
        switch (behaviour)
        {
            case AiBehaviour.TownNode:
                {
                    // Wandering: a random general node of the drawn building, none: no target and no redraw (FUN_140999a60, FUN_14038c2f0).
                    var b = PickFrom(_townPlaces.WanderIn(town, world.Characters.Previous[self.Slot].Position), roll);
                    return b is null ? null : RandomNode(b, IsGeneralNode, roll);
                }
            case AiBehaviour.Shop:
                return ShopTarget(world, self, town, mind, roll);
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
    /// <b>Verified</b>): the squad's town hand (<c>FUN_140269cd0</c>: a running contract's mission town, even none: the contract classes'
    /// <see cref="Blackboard.ContractTown"/>, a retreat's or a shipping's <see cref="Blackboard.MissionTown"/> when a campaign gives it as its phase
    /// package, so a retreating raider patrols its home town and walks there; else its mission town, else its home town) at a scatter of half the
    /// town's radius,
    /// at least 500; a nest squad's town is its nest (the original's <c>Nest</c> is a town: #771, Meitou had its members random-walk kilometres
    /// away); with none, the character's own position at 1250. The centre's height is the town's (a nest's, which Meitou keeps flat, the
    /// character's).
    /// </summary>
    (Vector3 Centre, float Scatter) PatrolArea(World world, Squad? squad, Blackboard board, Vector3 from)
    {
        var town = board.Contract is not null
            ? board.ContractMissionTown
            : board.Destination.IsNone
                ? board.HomeTown
                : board.Destination;
        if (town.Site >= 0)
        {
            var site = data.Sites[town.Site];
            return (site.Position, MathF.Max(PatrolScatterMin, site.Town.Radius * 0.5f));
        }

        if (town.PlayerTown > 0 && world.Buildings.FindTown(town.PlayerTown) is { } player)
        {
            return (player.Position, MathF.Max(PatrolScatterMin, player.Radius * 0.5f));
        }

        if (squad is not null && world.SystemOf<PopulationSystem>()?.NestOf(squad.Id) is { } nest)
            return (new Vector3(squad.HomeCentre.X, from.Y, squad.HomeCentre.Y), MathF.Max(PatrolScatterMin, nest.Radius * 0.5f));

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
    /// A worker is at its building within <paramref name="reach"/> of the building's position, or twice that once it has stopped (the original's
    /// arrival doubles the reach of a character that is not moving, docs/game/buildings-production.md "Work distance"), so one held off by the
    /// building's collision short of the reach has arrived there.
    /// </summary>
    internal static bool AtWork(in CharacterHot n, Vector3 building, float reach)
    {
        float dx = n.Position.X - building.X, dz = n.Position.Z - building.Z;
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
    /// slot go. Serial, on the package cadence, after every squad's kept slots are reserved (<see cref="ReserveWork"/>).
    /// </summary>
    void ClaimWork(World world, Blackboard board, Squad squad)
    {
        foreach (var id in squad.Members)
        {
            if (!world.Characters.TryResolveNext(id, out int s) || world.Characters.Cold(s) is not { Ai: { } mind } cold)
                continue;
            var type = mind.Goal?.Task.Type;

            // An autonomous member on a player job holds no AI work slot (its job's machine counts it).
            bool onJob = mind.Player && SquadAutonomy.OnJob(world.Characters.Next[s], cold, mind);
            bool works = type is TaskType.AutoLabouringMines or TaskType.AutoLabouringMinesPretend && !onJob;
            if (works && HoldsWork(mind, board.HomeTown, type!.Value)) continue;
            if (mind.WorkPost is { } left) _workTaken[left] = _workTaken.GetValueOrDefault(left) - 1;
            mind.WorkPost = works ? PickWork(world, id, board.HomeTown, mind.WorkEpoch, type!.Value) : null;
            if (mind.WorkPost is { } got) _workTaken[got] = _workTaken.GetValueOrDefault(got) + 1;
        }
    }

    /// <summary>
    /// The workplaces the members hold, reserved for every squad before any claims (as the seats, <see cref="TownPlaceClaims.Reserve"/>), those
    /// obedient player slaves operate (<see cref="ReserveObedientWork"/>), and the player's operators on their jobs at an outpost's machines.
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

        ReserveObedientWork(world);
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
    /// At its work building (<see cref="AtWork"/>): the walk there is over.
    /// </summary>
    static bool ArrivedAtWork(in CharacterHot n, AiMind mind, Vector3 building)
    {
        if (!AtWork(in n, building, mind.WorkRange)) return false;
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
        // clears a goal left with no plan (FUN_140511130, Verified), and its sleepers whose hours ended lie on with no goal or order
        // (docs/game/ai-tasks.md "Precedence", Observed; why its Idleness does not take over is Unknown).
        if (ReferenceEquals(goal, Idle) && table.Cold(slot)!.InSomething == Locks.HeldState.Bed)
        {
            LieOnWithNoGoal(mind, board);
            return;
        }

        var type = goal.Task.Type;
        var behaviour = TaskBehaviours.Of(type);
        if (behaviour == AiBehaviour.None && _modGoals is not null && _modGoals.Has(type)) behaviour = AiBehaviour.Mod;

        var profile = TaskProfiles.Of(type);
        bool fresh = TakeUp(world, slot, mind, goal, type, profile);
        behaviour = Adopt(world, slot, mind, goal, board, type, behaviour, fresh);
        if (fresh) PlanAnew(world, slot, mind, goal, board, profile);

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

        if (!PlanStep(world, slot, mind, ref behaviour)) return;
        if (mind.Resume && Resume(world, slot, mind)) return;

        ActOut(world, slot, mind, goal, board, type, behaviour);
    }

    /// <summary>
    /// One lying in a bed lies on with no goal: its plan, goal and behaviour cleared, as of the board's version.
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
        // plan is chosen again at every think: one it committed to starts over once its commitment ran out (engine reading: the original's target
        // finders roll a new target then, so the goal counts as new).
        bool fresh = mind.Goal is not { } before || before.Task.Type != type || before.Task.Target != goal.Task.Target
            || profile.Commits && world.GameHours >= mind.CommitUntil && !PlanContext.Stepped(goal);
        if (!fresh) return false;

        mind.GoalTick = world.Tick;
        mind.Walking = false;
        mind.FailedWalks = 0;
        mind.Target = null;

        // a renewed goal draws its node anew
        mind.NodeRoll = Rng.Hash(world.Seed, Rng.Key(table.IdOf(slot)), RngPurpose.Think, SeedSalts.NodePick ^ (ulong)++mind.NodeEpoch);

        // A spot given up is tried again from wherever the character is now (engine choice: the walk may have failed from the old place only).
        mind.HomeUnreachable = false;
        if (type is not (TaskType.RelaxInTownPackage or TaskType.SitAround or TaskType.SitOnThrone)) mind.SeatUnreachable = false;

        if (profile.Commits)
        {
            ulong roll = Rng.Hash(world.Seed, Rng.Key(table.IdOf(slot)), RngPurpose.Think,
                SeedSalts.AiCommit ^ (ulong)world.Tick << 9 ^ (ulong)type);
            mind.CommitUntil = world.GameHours + profile.CommitMin + Rng.Float(roll) * profile.CommitRange;
        }

        return true;
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

        if (mind.WorkActive && behaviour != AiBehaviour.Work && !PretendsAtHome(board, slot, behaviour, type))
        {
            // It stopped working: the next pick is a new one and the work clip goes.
            mind.WorkActive = false;
            mind.WorkEpoch++;

            if (table.Cold(slot) is { } worker && mind.WorkClip is not null && worker.ActionClip == mind.WorkClip)
            {
                worker.ActionClip = null;
                worker.ActionWork = null;
            }

            mind.WorkClip = null;
            mind.WorkFunctionality = null;
        }

        mind.FollowTarget = CharacterId.None;   // set again below for a follow goal aimed at the squad's mission
        return behaviour;
    }

    /// <summary>
    /// The steps of the plan before the goal's own act-out: getting out of bed, sheathing and drawing (one think each), the walks home, out of the
    /// home and near the target, the ditch request, and a way through a locked door (the lock system's, <see cref="TakeDoorStep"/>). False when the
    /// step took the think (the act-out waits); <paramref name="behaviour"/> may become the walk home.
    /// </summary>
    bool PlanStep(World world, int slot, AiMind mind, ref AiBehaviour behaviour)
    {
        var table = world.Characters;

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
            case TaskType.GoHomebuilding:
                // 19 Going home before the goal (43 Staying home and the like away from home): the walk home of the GoHome act-out.
                behaviour = AiBehaviour.GoHome;
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
                // in, then the AI plans again.
                mind.DitchRequest = true;
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
            case AiBehaviour.Captive:
                AskCaptiveJob(world, slot, mind);
                break;
            case AiBehaviour.Mod:
                // A code mod's goal: it stands while the serial step asks the mod to start the act-out (NpcGoals.SendStarts).
                StandForModGoal(world, slot);
                _modGoals!.Ask(mind);
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
            case AiBehaviour.Relax:
            case AiBehaviour.Sit:
                ActSeated(world, slot, mind, board, type);
                break;
            case AiBehaviour.RescueLeader:
            case AiBehaviour.RescueToBed:
            case AiBehaviour.Crouch:
                // RescueLeader and RescueToBed: the carry system's sweep lifts the leader or the body (CarrySystem.Rescue); until then it stands.
                // Crouch: it stands where it is; the animation system plays the clip and sets the stance.
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
    /// A trip to the mission town's centre.
    /// </summary>
    void ActTravel(World world, int slot, AiMind mind, Blackboard board, TaskType type)
    {
        // To the mission town's centre; the goal is no longer wanted once inside the town (Desire), long before the centre.
        if (DestinationCentre(world, board) is not { } town) Become(world, slot, CharacterTask.Idle);
        else if (GoToSpot(world, slot, mind, town, GiveUp.Target, type == TaskType.TravelToTargetTownFast ? SpeedMode.Run : SpeedMode.Walk))
            Become(world, slot, CharacterTask.Idle);
    }

    /// <summary>
    /// A patrol (<c>Task_Patrol</c>'s move, docs/game/factions-squads-towns.md "Patrol task"): the leader walks to the patrol point exactly, which
    /// the population's patrol step keeps in <see cref="Squad.HomeCentre"/> and redraws once it is reached; the others follow. Under a running
    /// patrol package the leader runs (<c>GoOutOnPatrol_Running</c>'s speed mode 2, <c>FUN_140289cb0</c>; the other packages' 4 changes nothing).
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

        WalkTo(world, slot, mind.Squad.HomeCentre, board.CurrentClass == PackageClass.PatrolRunning ? SpeedMode.Run : SpeedMode.Walk);
    }

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

        mind.Target ??= TargetFor(world, table.IdOf(slot), behaviour, board.Town, mind)
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

        var stepAt = StepBuilding(mind, board.HomeTown);
        Vector3? working = stepAt is not null && stepAt != mind.WorkBuilding
            ? new Vector3(stepAt.Position.X, float.NaN, stepAt.Position.Z) : mind.Target;
        var aimed = stepAt is not null && stepAt != mind.WorkBuilding ? stepAt : mind.WorkBuilding;
        mind.WorkRange = aimed is null ? WorkReach : WorkRangeOf(aimed);

        if (working is not { } workAt)
        {
            Become(world, slot, CharacterTask.Idle);
            return;
        }

        if (!ArrivedAtWork(in n, mind, workAt) && !GoToSpot(world, slot, mind, Flat(workAt), GiveUp.Target, SpeedMode.Walk, mind.WorkRange))
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
        if ((mind.WorkPost ?? PickWork(world, table.IdOf(slot), board.HomeTown, mind.WorkEpoch, type)) is not { } building)
            return;

        mind.Target = new Vector3(building.Position.X, float.NaN, building.Position.Z);
        mind.WorkClip = _work.ClipOf(building);
        mind.WorkFunctionality = _work.FunctionalityOf(building);
        mind.WorkHungerRate = _work.HungerRateOf(building);
        mind.WorkBuilding = building;
        mind.WorkOwner = board.HomeTown.Owner(world, data);
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
        if (at is not { } home || mind.HomeUnreachable || type is TaskType.HoldPosition or TaskType.ManTheGate)
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
    /// Whether a node goal of a character with a home finds no node: the home has none of the goal's type (a guard post retries with general nodes,
    /// nothing else does), so the finder's hand is empty (<c>FUN_14099e920</c>: kind 2 retries with kind 1, <c>FUN_14038c2f0</c> matches the type
    /// alone). A character without a home or whose home is out of reach keeps its post. 157 Static town guard is left out: it draws a post of any
    /// building of its town (<see cref="TownPlaceClaims.ClaimTownPosts"/>), not of its home.
    /// </summary>
    bool NodeGoalFindsNothing(AiMind mind, Blackboard board, TaskType type)
    {
        if (!IsNodeGoal(type) || type == TaskType.StandAtGuardNodeHometownOutside || board.Home is not { } home
            || mind.HomeUnreachable || GateSpot(board, mind, type) is not null)
            return false;

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
    /// up (<see cref="WalkEnd.Failed"/>: no way there, blocked, a locked door) or a fight or flight took counts toward <see cref="MaxFailedWalks"/>;
    /// one another system stopped (a captive job's or lock round's stand) is neither, so the next act-out walks again.
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
    }

    /// <summary>
    /// The stand while a mod's goal waits for its start: idle on row 14 Idleness, not on the goal's row, so the mod's task handler for a row of that
    /// number is never ended for a stand it did not start. Kept when it already stands so.
    /// </summary>
    static void StandForModGoal(World world, int slot)
    {
        if (world.Characters.Next[slot].Task == (byte)CharacterTask.Idle && world.Characters.Cold(slot)!.Running is { Row: TaskType.Idle }) return;

        TaskStart.StartOwn(world, slot, new TaskRequest(CharacterTask.Idle, TaskType.Idle, TaskSource.Goal));
    }

    /// <summary>
    /// Switches to a standing task (wander, follow, idle, rest), dropping a walk in progress; nothing when it is doing it already. A goal's start on
    /// the character's own slot (<see cref="TaskStart.StartOwn"/>: the think runs in parallel), as the row of the goal it acts out.
    /// </summary>
    static void Become(World world, int slot, CharacterTask task)
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
    static void WalkTo(World world, int slot, Vector2 to, byte mode)
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
    /// Registers the decision step's source for the urgent package goals of the player's autonomous squads.
    /// </summary>
    public void RegisterSources(DecisionSystem decisions) => decisions.Register(new SquadAutonomy.PackageGoals());

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
                if (!AttachNew(world, squad, cadence)) continue;
            }
            else if (!cadence) continue;   // the rest is looked at on the package cadence only (and when the squad appears)

            UpdateSquad(world, squad);
        }

        if (_merges.Count > 0) ApplyMerges(world);
    }

    /// <summary>
    /// The requests the thinks left with a flag (placing, beds, shopping, assaults, fights, treatments, leaving fights), the captive jobs and the
    /// code mods' goals, in that order; the systems they need are looked up once.
    /// </summary>
    void SendRequests(World world)
    {
        var table = world.Characters;
        if (Interlocked.Exchange(ref _placing, 0) != 0) Place(table);
        if (Interlocked.Exchange(ref _bedding, 0) != 0) LieDown(world);
        if (Interlocked.Exchange(ref _shopping, 0) != 0) BuyForShoppers(world);

        _combat ??= world.SystemOf<Combat.CombatSystem>();
        _aggro ??= world.SystemOf<AggroSystem>();
        _locks ??= world.SystemOf<Locks.LockSystem>();

        if (Interlocked.Exchange(ref _assaulting, 0) != 0 | Volatile.Read(ref _forcedAssaults) != 0) SendAssaults(world);
        if (Interlocked.Exchange(ref _fighting, 0) != 0) SendFights(world);
        if (Interlocked.Exchange(ref _treating, 0) != 0) (_firstAid ??= world.SystemOf<FirstAid.FirstAidSystem>())?.SendGoalTreatments(world);

        SendCaptiveJobs(world);
        _modGoals?.Step(world, world.Every(Settings.ThinkEveryTicks) * ModGoalBackoffThinks);
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
    /// A squad without a board: the first time it is seen, one the AI runs gets its board (and its town's places); else its minds are the
    /// engine's (again on the cadence). True when it was attached now.
    /// </summary>
    bool AttachNew(World world, Squad squad, bool cadence)
    {
        bool first = _looked.Add(squad.Id);

        if (!first || !Runs(world, squad))
        {
            if (first || cadence) EngineMinds(world, squad);

            return false;
        }

        Attach(world, squad);
        if (squad.Ai!.Town >= 0 && !_townPlaces.Has(squad.Ai.Town)) _townPlaces.Refresh(world, this);

        return true;
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
        if (platoon?.Campaign is null) StepPackage(world, squad, board);   // a campaign's squad has its war targets from the campaign
        if (board.BarWaits) _rules.RetryTable(board);

        // A contract that ended: its leader says the package's contract end talk to the employer (event 30, FUN_14026a110).
        if (board.TakeEnded() is { Package.ContractEndTalk: { } talk, Target: var employer } && !squad.Leader.IsNone)
        {
            world.Defer(new Dialogue.DialogueEvent(squad.Leader, employer, Dialogue.DialogueSystem.ContractJobEnded, talk)
                { Tick = world.Tick + 1 });
        }

        board.Crowd.Clear();
        board.CrowdEntries.Clear();

        // Every loaded platoon acts out its package's goals in the original, so its members get full minds. Two kinds stay on the engine's
        // routine while their package does not travel: a campaign's squad (its march stands for the war packages that are not built) and one a
        // code mod holds (the mod moves it).
        bool populationMoves = platoon is not null && PopulationRuns(platoon) && !BoardMoves(board);
        if (populationMoves) EngineMinds(world, squad, board);

        for (int k = 0; k < squad.Members.Count; k++) MindAndCrowd(world, squad, board, k, populationMoves);

        squad.Resume = false;
        _claims.ClaimTurrets(world, board, squad);
        _claims.ClaimSeats(world, board, squad);
        _claims.ClaimSitting(world, board, squad, _townPlaces);
        ClaimWork(world, board, squad);
        _claims.ClaimTownPosts(world, board, squad, _townPlaces);
        FindGate(world, board, squad);
    }

    /// <summary>
    /// Member <paramref name="k"/> of a squad the AI moves gets its mind (an engine mind is replaced), and its goal counts in the squad's crowd.
    /// </summary>
    void MindAndCrowd(World world, Squad squad, Blackboard board, int k, bool populationMoves)
    {
        var table = world.Characters;
        if (!table.TryResolveNext(squad.Members[k], out int s)) return;

        var cold = table.Cold(s)!;

        // An autonomous squad: minds for the player's characters only (not a prisoner kept in it), made again for one that came from another squad.
        if (squad.Autonomous && !cold.IsPlayer) return;

        bool own = squad.Autonomous && cold.IsPlayer;
        bool moved = own && cold.Ai is { } brought && !ReferenceEquals(brought.Squad, squad);

        // Every character has goals in the original, and a slave's are its master's Slave list (ObeyedRoles), whoever moves its own squad: a
        // captive of a squad the population moves (a campaign's, a held one) would otherwise stand where it was enslaved, on an engine mind (#904).
        bool obeys = populationMoves && ObeyedRoles(world, squad, cold) is not null;
        if ((!populationMoves || obeys) && (moved || cold.Ai is null or { Engine: true })) GiveMind(world, squad, board, k, s, own);

        if (cold.Ai?.Goal is { } g)
        {
            board.Crowd[g.Task.Type] = board.Crowd.GetValueOrDefault(g.Task.Type) + 1;
            board.CrowdEntries.Add(new CrowdEntry(squad.Members[k], g.Task.Type, CrowdTarget(g.Task.Target, squad, board, squad.Members[k])));
        }
    }

    /// <summary>
    /// Member <paramref name="k"/> (slot <paramref name="s"/>) gets a mind of the squad's package. A leader the population had walking its trip
    /// (an engine mind's routine, <see cref="EngineRoutine"/>, or the walk a platoon is given as it comes into the world, before it has a mind) is
    /// stopped first: the trip is the engine's stand-in for the package that moved it, and in the original the package that takes over (a
    /// contract started on the way, or simply the package of a platoon that loaded) gives the leader its own goals at once.
    /// </summary>
    void GiveMind(World world, Squad squad, Blackboard board, int k, int s, bool own)
    {
        var table = world.Characters;
        var cold = table.Cold(s)!;
        if ((cold.Ai is { Engine: true } || cold.Ai is null && squad.PlatoonId >= 0) && OnTrip(table.Next[s], cold))
            TaskStart.End(world, s, TaskEnd.Stopped);

        var p = table.Next[s].Position;
        cold.Ai = new AiMind
        {
            Squad = squad,
            MemberIndex = k,
            Post = OccupierPost(world, board, k) ?? new Vector2(p.X, p.Z),
            Player = own,
            RaceGoals = cold.Race is { } race && !own ? data.Ai.RaceGoals(race.StringId) : [],
            Resume = squad.Resume,
            Shopping = ShoppingList.Make(data.Db, cold.RecordId, world.Seed, Rng.Key(table.IdOf(s))),
        };
    }

    /// <summary>
    /// Whether the character walks the population's trip (row 53 of its travel stand-in, <see cref="EngineRoutine"/>'s walk).
    /// </summary>
    static bool OnTrip(in CharacterHot n, CharacterCold cold) =>
        n.Task == (byte)CharacterTask.GoTo && cold.Running is { Source: TaskSource.Goal, Row: TaskType.TravelToTargetTown, Task: CharacterTask.GoTo };

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
        TownBuilding? gate = board.GateHome is { IsGate: true } home ? home : null;
        if (gate is null && board.Crowd.ContainsKey(TaskType.ManTheGate) && board.Town >= 0)
        {
            float best = float.MaxValue;
            foreach (var b in data.Sites[board.Town].Buildings)
            {
                float d = Vector2.DistanceSquared(Flat(b.Position), Flat(squad.Position));
                if (b.IsGate && d < best) (gate, best) = (b, d);
            }
        }

        if (gate is null)
        {
            board.Gate = null;
            return;
        }

        if (board.Gate?.Building == gate) return;
        board.Gate = PostOf(world, gate, board.Town >= 0 ? Flat(data.Sites[board.Town].Position) : Flat(gate.Position));
    }

    /// <summary>
    /// The post of <paramref name="gate"/> (see <see cref="FindGate"/>) for a town centred at <paramref name="town"/>; null: no door, or its leaf's
    /// place not known.
    /// </summary>
    internal static GatePost? PostOf(World world, TownBuilding gate, Vector2 town)
    {
        Locks.Fixture? door = null;
        foreach (var f in world.Fixtures.OfBuilding(gate.Key))
            if (f.Kind == Locks.FixtureKind.Door && (door is null || f.DoorIndex < door.DoorIndex)) door = f;
        if (door is null || world.Fixtures.DoorSpotOf(door) is not { } leaf) return null;
        var way = Vector2.Normalize(new Vector2(leaf.Normal.X, leaf.Normal.Z));

        // Out of the town: the original's side is not decoded (engine choice).
        if (Vector2.Dot(way, Flat(leaf.Centre) - town) < 0) way = -way;
        return new GatePost(gate, leaf.Centre with { Y = leaf.Foot }, way, leaf.HalfWidth);
    }

    /// <summary>
    /// Where a gate's gate guard stands: <see cref="GateGuardOut"/> out from its leaf.
    /// </summary>
    internal static Vector3 FrontOf(GatePost gate) => gate.Door + new Vector3(gate.Out.X, 0, gate.Out.Y) * GateGuardOut;

    /// <summary>
    /// Where a gate's guards stand: the gate guard 15 out from its leaf; a member whose node goal finds no node in a gate it lives in flanks the
    /// opening on that side, beyond the leaf's edges, alternately left and right in two rows (engine choice: it stands in for the original's gate
    /// posts, which no layout holds). Null for other goals, and for 157 while it holds a town post.
    /// </summary>
    static Vector3? GateSpot(Blackboard board, AiMind mind, TaskType type)
    {
        if (board.Gate is not { } gate) return null;
        if (type == TaskType.StandAtGuardNodeHometownOutside && mind.TownPost is not null) return null;
        var front = FrontOf(gate);
        if (type == TaskType.ManTheGate) return front;
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
    /// The desire of the turret goals (AI slot 35 <c>FUN_140598080</c>): 0 when carrying; 2.0 when enemies are sensed (it has a turret as target);
    /// else 1.0. Not modelled: the 0 for a combat-flagged character outside melee mode, the cut at the squared distance clamp(<c>AI+0x28</c> x 1.2,
    /// 10000, 1e6), the faction-owned test (stat flag <c>+0x129</c>) and the 0.4 of a hurt character with none sensed (their operands are
    /// <b>Unknown</b>).
    /// </summary>
    float TurretDesire(World world, int slot) =>
        world.Characters.Cold(slot) is not { } cold || !cold.Carrying.IsNone ? 0 : _aggro?.SensesOf(slot) is { Enemies: > 0 } ? 2 : 1;

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
    /// Derived from the data, not hashed.
    /// </summary>
    readonly Dictionary<string, AiPackage?> _firstPackages = [];

    /// <summary>
    /// The combat goals of each package and role (<see cref="CombatGoalsOf"/>); derived from the data, not hashed.
    /// </summary>
    readonly Dictionary<(AiPackage, AiRole), IReadOnlyList<AiGoal>> _combatGoals = [];

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
    /// its template's packages, taken as already in the town it starts in; a saved squad's AI state is applied to it.
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
        squad.Ai = new Blackboard(packages)
        {
            Town = squad.Town,
            Size = squad.Members.Count,
            OwnHome = home,
            Home = home,
            HomePublic = template?.PublicDay ?? true,

            // A patrol package a save restores as running counts its 12 hours from the load (engine choice: Meitou does not save the stamp).
            PatrolStart = world.GameHours,
        };

        // The town it starts in (a nest too), taken as already entered (engine choice: no ENTER_TOWN line for every squad a town or nest makes).
        if (TownPosition(world, squad) is { } at) (squad.Ai.InTown, squad.Ai.InTownSince) = (StartTown(world, at), world.GameHours);
        if (squad.SavedAi is { } saved)
        {
            _rules.Bind(this, world, squad);
            saved.Apply(squad.Ai, data.Ai, _rules, world.Saved is { } population ? population.CharacterOf : null,
                world.Saved is { } towns ? h => Saving.WarSave.Town(world, towns, h) : null);
            squad.SavedAi = null;
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

        squad.LeftPackage = null;
        if (squad.Autonomous) _autonomy.Settle(world, squad, squad.Ai);
    }

    /// <summary>
    /// Minds for the members of a squad the AI does not run (<see cref="Runs"/>; not the player's): every character has goals in the original
    /// (<c>setupAI</c> <c>FUN_140621b70</c>: 14, 32, its race's and its faction's <c>AI Goals</c>; its squad's packages add theirs,
    /// docs/game/ai-tasks.md "Precedence"), so each gets an <see cref="AiMind.Engine"/> mind whose combat goals are its platoon's package's for its
    /// role (a campaign squad's phase package, a roaming one's the package the population's stand-in follows, else the template's first choice; none
    /// for a template without packages) and its race's. Its routine stays the engine's. Serial, on the package cadence and when the squad appears; a
    /// mind made for another squad is made again.
    /// </summary>
    void EngineMinds(World world, Squad squad, Blackboard? board = null)
    {
        if (IsPlayers(world, squad))
        {
            SquadAutonomy.DropMinds(world, squad);   // none for a player's squad; what an autonomous one left goes
            return;
        }

        var table = world.Characters;
        AiPackage? package;
        if (board is not null) package = board.Current;   // a platoon's squad running its blackboard: the package that runs
        else if (squad.PlatoonId >= 0 && world.Platoons.Find(squad.PlatoonId) is { } platoon)
        {
            // A campaign's squad runs its phase's package (as PopulationSystem.GoalDuty reads it); a roaming one the package its stand-in follows.
            AiPackage? phase = platoon.Campaign is { } inst ? PhasePackage(world, platoon, inst) : null;
            package = phase ?? (platoon.Package is { } id ? data.Ai.Package(id) : platoon.Campaign is null ? FirstPackage(squad.TemplateId) : null);
        }
        else package = FirstPackage(squad.TemplateId);
        for (int k = 0; k < squad.Members.Count; k++)
        {
            // A full mind gives way to an engine one only under a blackboard that says the population moves the squad.
            if (!table.TryResolveNext(squad.Members[k], out int s) || table.Cold(s) is not { IsPlayer: false } cold
                || cold.Ai is { Engine: false } && board is null)
                continue;

            // A slave obeying its master takes the master's Slave list with a full mind (MindAndCrowd), not its own squad's routine.
            if (board is not null && ObeyedRoles(world, squad, cold) is not null) continue;

            if (cold.Ai is { Engine: false })
            {
                ToRoutine(world, squad, s, cold);
                cold.Ai = null;
            }

            if (cold.Ai is null || !ReferenceEquals(cold.Ai.Squad, squad))
            {
                var p = table.Next[s].Position;
                cold.Ai = new AiMind
                {
                    Squad = squad,
                    MemberIndex = k,
                    Post = new Vector2(p.X, p.Z),
                    Engine = true,
                    Version = EngineBoard.Version,
                    RaceGoals = cold.Race is { } race ? data.Ai.RaceGoals(race.StringId) : [],
                };
            }

            cold.Ai.EngineGoals = package is null ? [] : CombatGoalsOf(package, RoleOf(squad, squad.Members[k], cold));
            if (squad.DebugAggro && AttackEnemiesGoal() is { } attack) cold.Ai.EngineGoals = [.. cold.Ai.EngineGoals, attack];

            // A follower of a squad the population moves that stands idle (it came round, its fight ended, the swap found it held by something
            // else: ToRoutine) follows its leader again, as the original's followers walk to their leader (goal 215).
            if (board is not null && Rejoins(world, squad, s, cold))
                TaskStart.Become(world, s, CharacterTask.Follow, TaskType.FollowSquadleader, TaskSource.Goal);
        }
    }

    /// <summary>
    /// A member's full mind gives way to an engine one (a campaign drafted its loaded platoon, a code mod holds it): what the full mind had it
    /// doing ends, so a sleeper gets up, one sitting at a table stands and a walk to a shop stops, and a member other than the leader follows
    /// its leader again, as one made under the population does; the leader stands for the population to move. Else each would stay where its
    /// package left it while the population marches the leader off (engine choice: the original has one kind of mind, and the campaign's
    /// package gives every member its goals at once). One the mind does not hold now (in a fight, a flight, a carry, a treatment, a talk, a
    /// knock-out) is left alone and rejoins on a later package step, once it stands idle (<see cref="Rejoins"/>).
    /// </summary>
    static void ToRoutine(World world, Squad squad, int s, CharacterCold cold)
    {
        var table = world.Characters;
        var id = table.IdOf(s);
        if (!Holds(table.Next[s], cold) || cold.InCombat) return;

        if (cold.InSomething == Locks.HeldState.Bed) world.Fixtures.LeaveBed(world, id);
        if (id != squad.Leader && table.TryResolveNext(squad.Leader, out _))
            TaskStart.Become(world, s, CharacterTask.Follow, TaskType.FollowSquadleader, TaskSource.Goal);
        else TaskStart.End(world, s, TaskEnd.Stopped);   // also one standing: its task record named the goal it stood for
    }

    /// <summary>
    /// Whether a member of a squad the population moves should take up following its leader again: not the leader, whose own task the
    /// population gives; standing idle, alive and awake, and held by nothing (no fight, duty, flight, carry, bed or cage).
    /// </summary>
    static bool Rejoins(World world, Squad squad, int s, CharacterCold cold)
    {
        var table = world.Characters;
        ref readonly var n = ref table.Next[s];
        return n.Alive && n.Task == (byte)CharacterTask.Idle && table.IdOf(s) != squad.Leader
            && table.TryResolveNext(squad.Leader, out int led) && table.Next[led].Alive
            && cold is { InCombat: false, Engaged: false, Fleeing: false, IsImprisoned: false, CarriedBy.IsNone: true, Carrying.IsNone: true }
            && cold.Medical is not { Incapacitated: true } && cold.InSomething == Locks.HeldState.None;
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
    /// The package a template's squad starts with (<see cref="Blackboard.Update"/> over its slots, as the population's stand-in takes it,
    /// <see cref="PlatoonJobs.For"/>); null without one.
    /// </summary>
    AiPackage? FirstPackage(string templateId)
    {
        if (_firstPackages.TryGetValue(templateId, out var known)) return known;
        var packages = TemplateOf(templateId).Packages;
        AiPackage? first = null;
        if (packages.Count > 0)
        {
            var board = new Blackboard(packages);
            board.Update();
            first = board.Current;
        }

        return _firstPackages[templateId] = first;
    }

    /// <summary>
    /// The combat goals (<see cref="AiBehaviour.Fight"/>) of a package's role list, kept per package and role (serial only).
    /// </summary>
    IReadOnlyList<AiGoal> CombatGoalsOf(AiPackage package, AiRole role)
    {
        if (_combatGoals.TryGetValue((package, role), out var known)) return known;
        var list = package.Roles[(int)role].Where(g => TaskBehaviours.Of(g.Task.Type) == AiBehaviour.Fight).ToArray();
        return _combatGoals[(package, role)] = list;
    }

    /// <summary>
    /// The AI runs a squad that is not the player's, not a roaming platoon's active half (a town's own saved squad, pinned, is run like the town's, a
    /// save's wild one like the wild sites' squads), and whose template lists AI packages. A player's squad only when it is autonomous and a save
    /// held its AI (<see cref="Squad.SavedAi"/>): else it is attached when it is given a package (<see cref="TakePackage"/>). A squad of slaves
    /// whose template lists none (Garret's slave, #938) is run too: its members plan with their master's Slave AI Goals (<see cref="ObeyedRoles"/>),
    /// as the original's AI slaves run their master's list.
    /// </summary>
    bool Runs(World world, Squad squad) =>
        IsPlayers(world, squad) ? squad is { Autonomous: true, SavedAi: not null }
            : TemplateOf(squad.TemplateId).Packages.Count > 0 || AllSlaves(world, squad);

    /// <summary>
    /// Every living member of the squad is a slave (state 1); false for an empty squad.
    /// </summary>
    static bool AllSlaves(World world, Squad squad)
    {
        var table = world.Characters;
        bool any = false;
        foreach (var m in squad.Members)
        {
            if (!table.TryResolveNext(m, out int s) || table.Cold(s) is not { } cold) continue;
            if (cold.SlaveState != 1) return false;
            any = true;
        }

        return any;
    }

    /// <summary>
    /// The platoon whose squad this is when it is a loaded roaming or campaign platoon's (not a town's own saved squad, nor a wild one); null for
    /// any other squad. Such a squad claims no building of its own, and the population moves it only when <see cref="PopulationRuns"/>. Every loaded platoon runs its blackboard in the original (<c>FUN_1402732b0</c> from the platoon's slot 27,
    /// docs/game/ai.md "Blackboard update"), whatever its kind.
    /// </summary>
    Platoon? PlatoonOf(World world, Squad squad) =>
        squad.PlatoonId >= 0 && !squad.Pinned && !squad.Wild ? world.Platoons.Find(squad.PlatoonId) : null;

    /// <summary>
    /// Whether the population, not the squad's blackboard, moves a loaded platoon's squad: a campaign's (<see cref="Platoon.Campaign"/>: the
    /// campaign march stands for the war package classes, which are not built) and one a code mod holds (<see cref="Platoon.Held"/>). Every other
    /// loaded platoon acts out its package's goals with full minds, as in the original (<see cref="PlatoonOf"/>). The test does not depend on
    /// where the squad stands, so a free platoon walking into or out of a town never changes its kind of mind; a campaign drafting or releasing
    /// a loaded platoon and a mod's hold or release do, and the minds are made again then (<see cref="EngineMinds"/>, <see cref="ToRoutine"/>,
    /// <see cref="GiveMind"/>). A campaign's or a held squad whose own package travels still takes full minds while it does
    /// (<see cref="BoardMoves"/>).
    /// </summary>
    internal static bool PopulationRuns(Platoon platoon) => platoon.Campaign is not null || platoon.Held;

    /// <summary>
    /// Whether the running package moves the squad itself (the trader, the refugee, the shipping and retreat classes): one of its goals travels and a
    /// mission town is set. For a squad the population runs (<see cref="PopulationRuns"/>) the campaign march or the mod moves it otherwise, and
    /// its members keep the engine's routine (<see cref="AiMind.Engine"/>). It also names the squads whose mission town is the stand-in's target
    /// at unload and is saved.
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
    /// job). Under the population's trip the slavers had engine minds, so the leader walked the trip alone while the rest neither followed nor
    /// carried its knocked-out captives, which then escaped far from it (#904).
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
    /// The packages and the template of a squad template id (none for an id that is no squad template), cached.
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
    /// use, and has its members think again.
    /// </summary>
    void Rehome(World world, Squad squad)
    {
        var home = HouseForSale(world, squad);
        squad.HomeBuilding = home;
        squad.MovedOut = home is null;
        if (squad.Ai is not { } board) return;
        var old = board.OwnHome;
        board.OwnHome = home;
        if (board.Home == old) board.Home = home;
        board.Touch();
        foreach (var id in squad.Members)
        {
            if (world.Characters.TryResolveNext(id, out int slot) && world.Characters.Cold(slot)?.Ai is { } mind)
            {
                mind.HomeUnreachable = false;
                mind.Replan = true;
            }
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
        int BarTown(Blackboard board) => !_squad.TownOwn && board.InTown.Site >= 0 ? board.InTown.Site : board.Town;

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
                    _system.RefugeeStarted(_world, _squad, board);
                    break;
                case PackageClass.Contract when package.Signal is HiredAllySignal or DefendBaseSignal:
                    _system.ContractStart(_world, _squad, board, package);
                    break;
                case PackageClass.CampaignDefend:
                    _system.CampaignDefendStart(_world, _squad, board);
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
