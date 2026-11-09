using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Ai;
using Meitou.Data.World;
using Meitou.Simulation.Aggro;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Movement;
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
    /// A patrol package may start this many game hours after the last one started (uniform), and the first one within <see cref="FirstPatrolHours"/>
    /// of the squad appearing.
    /// </summary>
    public float PatrolEveryHoursMin { get; init; } = 6;
    /// <summary>
    /// The most game hours between two patrols (<see cref="PatrolEveryHoursMin"/>).
    /// </summary>
    public float PatrolEveryHoursMax { get; init; } = 12;
    /// <summary>
    /// Game hours from the squad appearing to its first patrol.
    /// </summary>
    public float FirstPatrolHours { get; init; } = 4;

    /// <summary>
    /// A patrol gives up after this many game hours.
    /// </summary>
    public float PatrolGiveUpHours { get; init; } = 6;

    /// <summary>
    /// A patrol's route: this many points round the home town, between these multiples of the town's radius from its centre, then home.
    /// </summary>
    public int PatrolPoints { get; init; } = 4;
    /// <summary>
    /// The nearest a patrol point lies from the town's centre, in town radii (<see cref="PatrolPoints"/>).
    /// </summary>
    public float PatrolRadiusMin { get; init; } = 1.5f;
    /// <summary>
    /// The farthest a patrol point lies from the town's centre, in town radii (<see cref="PatrolPoints"/>).
    /// </summary>
    public float PatrolRadiusMax { get; init; } = 3f;

    /// <summary>
    /// The patrol's leader is at a route point within this distance.
    /// </summary>
    public float RouteArrive { get; init; } = 40;

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
public sealed record StartContract(int SquadId, string PackageId, float Hours, CharacterId? Target = null) : SimCommand;

/// <summary>
/// Replaces a squad's packages with one (dialogue's <c>change AI</c>).
/// </summary>
public sealed record ChangeAi(int SquadId, string PackageId) : SimCommand;

/// <summary>
/// The AI (docs/simulation.md "AI"; the original's in docs/game/ai.md). Squads whose template lists <c>AI packages</c> get a <see cref="Blackboard"/>
/// that picks the package by slot and package class; each member thinks on its own cadence: it scores the goals of its role list, its race's <c>AI
/// Goals</c> and the default idle goal by tier (urgent, then non-urgent, then fluff; the first tier with a positive score wins), weight, hour window,
/// crowding and continuity, acts out the winner through <see cref="TaskBehaviours"/> (in its home building's nodes and beds where it has one); its
/// combat goals are goals of the same choice (AiSystem.Fights.cs), which <see cref="AggroSystem"/>'s look asks it to weigh again when it senses a new
/// enemy. The AI runs only characters that nothing else holds: not the player's, not in a fight it did not start, fleeing, down, carrying, carried,
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
public sealed partial class AiSystem(PopulationData data, AiSettings? settings = null) : ITickSystem, IStateHashed
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
    /// How near an enemy must be for <c>isNoEnemiesInVicinity</c> to fail (units; <b>engine choice</b>, about a turret's range: the original's radius
    /// is <b>Unknown</b>).
    /// </summary>
    public const float EnemyVicinity = 1000;

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
            if (Holds(n, cold))
            {
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
                if (cold.InCombat) InFight(mind, goal, board);
                else Act(world, i, mind, goal, board);
                continue;
            }

            // A character on an engine duty or in an engine fight: only the goals that pre-empt it (Preempts) are weighed.
            if (Preempts(n, cold) is not Restrict.None and var only && Choose(world, i, id, mind, squad, board, roles, hour, only) is { } urgent)
            {
                // Self preservation's run; or a fight (its own defence, a combat goal's), which a character already fighting does not start again.
                if (ReferenceEquals(urgent, SelfPreservationGoal) && mind.Step == TaskType.RunAway) mind.FleeRequest = FleeAsk.Threat;
                else if (!cold.InCombat) AskFight(world, i, urgent.Task.Type, mind);
            }
        }
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
    }

    /// <summary>
    /// What may take over an NPC with a mind that the AI does not hold (<see cref="Holds"/>), as the original's chooser would (its engine duties are
    /// goals and jobs of the same chooser, below a priority-3 goal, docs/game/ai-tasks.md "Precedence"): on an engine duty whose system lets a fight
    /// interrupt it, the combat goals and Self preservation; on any other (and in a fight it did not start: a dialogue's, an assault's, a duty's),
    /// Self preservation alone, whose fight or run (<see cref="SelfPreservation.Runs"/>) ends the duty's task as any fight's start does (in a fight,
    /// only its run changes anything). The duties a fight may interrupt: treating (<see cref="FirstAid.FirstAidSystem"/> lets go of the patient at
    /// the fight's start), a town errand's walk but the cage walk (<see cref="Kidnap.TownDutySystem"/> waits for the fight to end and walks on; one
    /// carrying an intruder out fights with the body on its shoulder, as the original's carrier does), a captive job's walk carrying nothing
    /// (<see cref="Kidnap.CaptiveSystem"/> gives the job up), and the routine of an <see cref="AiMind.Engine"/> mind (<see cref="EngineRoutine"/>).
    /// Nothing for one fleeing, imprisoned, carried or down, nor for a gunner or a carrier in a fight.
    /// </summary>
    internal static Restrict Preempts(in CharacterHot n, CharacterCold cold)
    {
        if (cold.Ai is not { } mind || cold.IsPlayer || cold.Fleeing || cold.IsImprisoned || !cold.CarriedBy.IsNone
            || cold.Medical is { Incapacitated: true })
            return Restrict.None;

        if (cold.InCombat) return cold.Gunning is null && cold.Carrying.IsNone ? Restrict.Self : Restrict.None;
        if (cold.Engaged) return Restrict.Self;
        if (mind.Engine && EngineRoutine(n, cold)) return Restrict.Combat;
        if (n.Task == (byte)CharacterTask.Treat) return Restrict.Combat;
        return n.Task == (byte)CharacterTask.GoTo && cold.Running is { Source: TaskSource.Goal, Task: CharacterTask.GoTo } run
            && (Kidnap.TownDutySystem.IsErrandRow(run.Row) || cold.Carrying.IsNone && Kidnap.CaptiveSystem.IsJobRow(run.Row))
            ? Restrict.Combat : Restrict.Self;
    }

    /// <summary>
    /// The engine's routine of an <see cref="AiMind.Engine"/> mind, which a fight its combat goals start interrupts: standing, wandering, following
    /// its leader, or the roaming leader's walk on its trip (row 53 of the population's travel stand-in, <see cref="PopulationSystem"/>; the trip
    /// goes on after the fight).
    /// </summary>
    static bool EngineRoutine(in CharacterHot n, CharacterCold cold) => (CharacterTask)n.Task switch
    {
        CharacterTask.Idle or CharacterTask.Wander or CharacterTask.Follow => true,
        CharacterTask.GoTo => cold.Running is { Source: TaskSource.Goal, Row: TaskType.TravelToTargetTown, Task: CharacterTask.GoTo },
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
    /// outside a fight (<see cref="CharacterCold.StumbleTaskUntil"/>); null for the rest (the HUD then names the movement task).
    /// </summary>
    public static string? ActivityText(in CharacterHot n, CharacterCold cold, long tick = 0) =>
        cold.StumbleTaskUntil > tick ? TaskBehaviours.Text(TaskType.StumbleTaskForced)
        : cold is { Fleeing: true, IsPlayer: false, Medical.Incapacitated: not true } ? TaskBehaviours.Text(TaskType.RunAway)
        : cold.Ai is { Goal: { } goal } && Holds(n, cold) ? TaskBehaviours.Text(goal.Task.Type) : null;

    /// <summary>
    /// Whether the AI runs the character now: it has a mind and nothing else holds it (a fight it did not start, a flight, an order, a carry, a
    /// trade, a knock-out). In its own fight it thinks as anyone does (on the cadence and the replan triggers) and may leave it for another goal
    /// (<see cref="InFight"/>).
    /// </summary>
    public static bool Holds(in CharacterHot n, CharacterCold cold)
    {
        if (cold.Ai is not { } mind || cold.IsPlayer || cold.Fleeing || cold.Engaged || cold.IsImprisoned || !cold.CarriedBy.IsNone
            || !cold.Carrying.IsNone || cold.Medical is { Incapacitated: true })
            return false;

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
            // In the bed it went to (or lying in one when loaded, before its first think).
            CharacterTask.Sleep => cold.InSomething == Locks.HeldState.Bed && (mind.Behaviour == AiBehaviour.Sleep || mind.Goal is null),
            _ => false,
        };
    }

    /// <summary>
    /// Job 187 Obedient Slave (<c>FUN_14050e5b0</c>, docs/game/ai-tasks.md "Obedient Slave"): a slave (state 1) not sneaking, owned by another squad,
    /// takes that squad's <c>Slave AI Goals</c> instead of its own lists. Null: it plans with its own squad's (a slave in its owner's squad already
    /// takes the Slave role there).
    /// </summary>
    internal static IReadOnlyList<AiGoal>? ObeyedRoles(World world, Squad squad, CharacterCold cold) =>
        cold.SlaveState == 1 && cold.SlaveMaster >= 0 && cold.SlaveMaster != squad.Id && (cold.Orders & CharacterOrders.Sneak) == 0
            && world.Squads.Find(cold.SlaveMaster) is { Ai: { } master } && master.Roles[(int)AiRole.Slave] is { Count: > 0 } list ? list : null;

    /// <summary>
    /// The role list a member takes (docs/game/ai.md "Goal loading"): the squad leader the leader's, else by its squad role.
    /// </summary>
    internal static AiRole RoleOf(Squad squad, CharacterId id, CharacterCold cold) =>
        squad.Leader == id || cold.Role == (int)SquadRole.Leader ? AiRole.Leader
        : cold.Role == (int)SquadRole.Squad2 ? AiRole.Squad2
        : cold.Role == (int)SquadRole.Slave ? AiRole.Slave
        : AiRole.Squad;

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
    /// The goal to act out, with its plan in <see cref="AiMind.Plan"/>: by tier (urgent, non-urgent, fluff), the goals with a positive score highest
    /// first, leaving out one whose list A already holds; the first whose plan succeeds wins (docs/game/ai-tasks.md "Planner": the pick). Idle when
    /// none does. With <paramref name="only"/>, only the priority-3 goals that may take over a character the AI does not hold
    /// (<see cref="Preempts"/>) are weighed, and null comes back when none plans.
    /// </summary>
    AiGoal? Choose(World world, int slot, CharacterId id, AiMind mind, Squad squad, Blackboard board, IReadOnlyList<AiGoal> roles,
        int hour, Restrict only = Restrict.None)
    {
        var race = mind.RaceGoals;
        var candidates = Candidates;
        var plans = PlanContext.Local.Bind(this, world, slot, mind, board);

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
            if (tier == TaskPriority.Urgent) Consider(SelfPreservationGoal);
            for (int k = 0; k < candidates.Count; k++)
                if (plans.TryPlan(candidates[k].Goal, candidates[k].Desire)) return candidates[k].Goal;
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
    /// A candidate goal's score: its desire times its weight and its crowd factor (<see cref="TaskProfile.Crowd"/> per member already on it), the
    /// running goal's continuity, and a committed goal's floor or ceiling.
    /// </summary>
    float Score(World world, AiGoal g, TaskProfile profile, float desire, Squad squad, Blackboard board, AiMind mind, CharacterId id)
    {
        int crowd = profile.Crowd == 1 ? 0
            : board.CrowdOf(g.Task.Type, TaskDataTable.Of(g.Task.Type)?.Is(TaskDataFlags.NeedsTarget) ?? false,
                CrowdTarget(g.Task.Target, squad, board, id), id);
        float score = desire * g.Weight * (crowd > 0 ? MathF.Pow(profile.Crowd, crowd) : 1);

        if (mind.Goal is { } current && current.Task.Type == g.Task.Type && current.Task.Target == g.Task.Target)
        {
            score *= Settings.Continuity;

            // A goal it committed to holds at least 0.4 until the commitment runs out, then at most 0.05 (docs/game/ai.md "Packages and
            // goals", Observed).
            if (profile.Commits) score = world.GameHours < mind.CommitUntil ? MathF.Max(score, 0.4f) : MathF.Min(score, 0.05f);
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
    /// home town and 0 in it (docs/game/perception.md 7.2, Observed); a goal the engine cannot act out is 0.
    /// </summary>
    float Desire(World world, AiBehaviour behaviour, TaskType type, TaskProfile profile, Squad squad, Blackboard board, AiMind mind,
        CharacterId self, TaskTargetType targeting)
    {
        if (behaviour == AiBehaviour.None) return 0;

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
    /// What a goal's target finder found, by its behaviour (0 when it found nothing, or when the goal is not wanted now): the factor on its type's
    /// desire (<see cref="Desire"/>).
    /// </summary>
    float FinderFactor(World world, AiBehaviour behaviour, TaskType type, Squad squad, Blackboard board, AiMind mind, CharacterId self,
        TaskTargetType targeting)
    {
        return behaviour switch
        {
            // A goal aimed at the squad's mission follows the contract's target (a bodyguard its employer), wanted only while there is one alive.
            AiBehaviour.Follow when targeting == TaskTargetType.SquadMission =>
                board.MissionTarget.IsNone || board.MissionTarget == self || !world.Characters.IsAlive(board.MissionTarget) ? 0 : FollowDesire(type),
            AiBehaviour.Follow => squad.Leader.IsNone || squad.Leader == self || !world.Characters.IsAlive(squad.Leader) ? 0 : FollowDesire(type),
            // Sleeping on the ground has row 98's desire (AI slot 30): the registry gives rows 98, 153, 175, 239 and 261 the same desire
            // (FUN_140319a60, Verified).
            AiBehaviour.Sleep when type == TaskType.SleepOnFloor => SleepingDesire(world, board, self.Slot),
            AiBehaviour.GoHome => AtHomeTown(world, squad, board, self) ? 0 : GoHomeDesire(world, self),
            // Static guard's finder (FUN_14099edd0, Verified; 38 and 268 both register it, 268 with the indoors flag; 157 has its own, FUN_14099f1a0)
            // returns 0.5 on its normal exit; only its visitor branch (a node beside a neutral stranger, not modelled) returns 1. Staying home needs
            // a piece of the home's furniture to operate (the finder of 43 returns 0 without one).
            AiBehaviour.Post when type == TaskType.StayInHome => TownPlaceClaims.HasStayingFurniture(board.Home) ? 1 : 0,
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
            AiBehaviour.Shop => !mind.Shopping.Pending(ShoppingList.DayOf(world.GameHours), world.Hour, board.Town) ? 0
                : ShopFor(Places(board.Town), mind, TargetRoll(world, self, behaviour), RandomShopping(world, board.Town)) is { } picked
                    ? ShopFactor(Places(board.Town), picked) : 0,
            // A worker needs a mine, bench or refinery in its town (the finders return 0 without one).
            AiBehaviour.Work => WorkAllowed(world, self.Slot, board.Town)
                && (HoldsWork(mind, board.Town, type) || PickWork(world, self, board.Town, mind.WorkEpoch, type) is not null) ? 1 : 0,
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
            // Crouching is wanted always (it holds the pose while it runs); standing up (its effects !24 !25 !26) only while crouched.
            AiBehaviour.Crouch => type == TaskType.Crouch || world.Characters.Cold(self.Slot) is { Crouched: true } ? 1 : 0,
            AiBehaviour.Assault => targeting == TaskTargetType.SquadMission && !AssaultTargetFor(world, self.Slot, board).IsNone ? 1 : 0,
            // Self preservation: the threat level (AI slot 17); Running away: row 35's own (AI slot 55), a goal of the packages that list it.
            AiBehaviour.SelfPreserve => SelfPreservationDesire(world, self.Slot, mind),
            AiBehaviour.Flee => world.Characters.Cold(self.Slot) is { } runner
                ? SelfPreservation.RunDesire(runner, SelfPreservation.Walled(world, self.Slot)) : 0,
            // A combat goal: its own desire's zeros and factors, times its finder's factor (an attack goal's 1 with a target, a protect goal's fight
            // threat; AiSystem.Fights.cs).
            AiBehaviour.Fight => FightDesire(world, self.Slot, type, mind) is var want and > 0 ? want * FightFactor(world, self.Slot, type, mind) : 0,
            // The Medic goal: the finder's score from the serial sweep (its desire's fight and carrying cases are gates of the finder).
            AiBehaviour.Medic => world.Characters.IsAlive(mind.MedicPatient) ? mind.MedicScore : 0,
            // A captive goal: the desire its job start kept on the sweep turn of a seated character (0 for the rest, whose jobs the sweep starts
            // itself; Kidnap/CaptiveSystem.Offers.cs).
            AiBehaviour.Captive => mind.CaptiveDesire(type),
            _ => 1,
        };
    }

    /// <summary>
    /// Following's desire (AI slot 15, <c>FUN_1405a33f0</c>, <b>Verified</b>; rows 31, 40, 44, 141 and 289 register it): 0.5 times the followed
    /// character's <c>followMe</c> (StateBroadcastData <c>+0xa8</c>), 1 when the target does not resolve. What sets <c>followMe</c> is
    /// <b>Unknown</b>; Meitou reads 1, so a member weighs following its leader at 0.5.
    /// </summary>
    static float FollowDesire(TaskType type) => type is TaskType.FollowSquadleader or TaskType.FollowPlayerOrder ? FollowShare : 1;

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
    /// Whether a character is within its squad's home town's radius; true when the squad has no town or the character is not alive.
    /// </summary>
    bool AtHomeTown(World world, Squad squad, Blackboard board, CharacterId self)
    {
        if (board.Town < 0 || !world.Characters.IsAlive(self)) return true;
        var site = data.Sites[board.Town];
        var p = world.Characters.Previous[self.Slot].Position;
        float r = site.Town.Radius;
        return Vector2.DistanceSquared(new Vector2(p.X, p.Z), new Vector2(site.Position.X, site.Position.Z)) <= r * r;
    }

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
        var places = Places(town);
        switch (behaviour)
        {
            case AiBehaviour.TownNode:
            case AiBehaviour.Shop:
                {
                    var b = behaviour == AiBehaviour.TownNode
                        ? PickFrom(_townPlaces.WanderIn(town, world.Characters.Previous[self.Slot].Position), roll)
                        : ShopFor(places, mind, roll, RandomShopping(world, town));
                    if (b is null) return null;

                    // A building without a counter is the target itself, its origin on any floor (FUN_1409997f0); the walk ends within WorkReach of
                    // it.
                    if (behaviour == AiBehaviour.Shop && ShopFactor(places, b) < 1) return new Vector3(b.Position.X, float.NaN, b.Position.Z);

                    // Wandering: a random general node of the drawn building, none: no target and no redraw (FUN_140999a60, FUN_14038c2f0).
                    if (behaviour == AiBehaviour.TownNode) return RandomNode(b, IsGeneralNode, roll);

                    // A general node; in a shop without one a guard post, else any other node (engine choice: where the original's Task_Shopping
                    // stands is not decoded).
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
    /// Acts out 36 Patrolling town (<c>Task_PatrolTown</c>, docs/game/ai-tasks.md "Town goals"): strolls to a point drawn round its squad's town
    /// (<see cref="PatrolArea"/>, <see cref="PatrolPoint"/>) and, once the walk is over, forgets it, so the next think draws another, as the
    /// original's update sends a new move each time the last one ended.
    /// </summary>
    void Patrol(World world, int slot, AiMind mind, Blackboard board)
    {
        var self = world.Characters.IdOf(slot);
        if (mind.Target is null)
        {
            var (centre, scatter) = PatrolArea(world, board, world.Characters.Previous[slot].Position);
            mind.Target = PatrolPoint(world, self, centre, scatter);
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
    /// <b>Verified</b>): the squad's town hand (<c>FUN_140269cd0</c>: its mission town, else its home town) at a scatter of half the town's radius,
    /// at least 500; with neither, the character's own position at 1250.
    /// </summary>
    (Vector2 Centre, float Scatter) PatrolArea(World world, Blackboard board, Vector3 from)
    {
        var town = board.Destination.IsNone && board.Town >= 0 ? CampaignTown.OfSite(board.Town) : board.Destination;
        if (town.Site >= 0)
        {
            var site = data.Sites[town.Site];
            return (Flat(site.Position), MathF.Max(PatrolScatterMin, site.Town.Radius * 0.5f));
        }

        if (town.PlayerTown > 0 && world.Buildings.FindTown(town.PlayerTown) is { } player)
        {
            return (Flat(player.Position), MathF.Max(PatrolScatterMin, player.Radius * 0.5f));
        }

        return (Flat(from), PatrolScatter);
    }

    /// <summary>
    /// Where 36 Patrolling town walks next (<c>setPatrolInput</c>, docs/game/ai-tasks.md "Town goals"): the area's centre moved by up to half the
    /// scatter in X and Z; of up to 10 draws the first on walkable ground (the original redraws a point near a building that is not public, allied or
    /// the player's, or under water for a race that avoids it; Meitou tests the ground only). Null without a walkable draw.
    /// </summary>
    Vector3? PatrolPoint(World world, CharacterId self, Vector2 centre, float scatter)
    {
        ulong roll = TargetRoll(world, self, AiBehaviour.PatrolTown);
        for (int i = 0; i < 10; i++)
        {
            roll = Rng.Mix(roll);
            float x = centre.X + (Rng.Float(roll) - 0.5f) * scatter;
            roll = Rng.Mix(roll);
            float z = centre.Y + (Rng.Float(roll) - 0.5f) * scatter;
            if (world.Walkability.IsWalkable(x, z)) return new Vector3(x, float.NaN, z);
        }

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
    /// The machine events of standing work at a functionality's clip (<see cref="CharacterCold.ActionWork"/>); null without a functionality or clip.
    /// </summary>
    internal static WorkEvents? StandingWork(string? functionality, string? clip) =>
        functionality is not null && clip is not null ? new WorkEvents(functionality, clip) : null;

    /// <summary>
    /// The building a worker works at (the finders of goals 189 / 190: a random building of the town with a function in
    /// <see cref="TownPlaceIndex.WorkFunctions"/>, slot 2 of the building finder <c>FUN_1409fb020</c>); the roll is the character's and its
    /// <paramref name="epoch"/> of picks (<see cref="AiMind.WorkEpoch"/>), so asking twice names the same one. Null: the town has none. 189
    /// (<paramref name="type"/>, real work) takes only the workplaces it can run (<see cref="RealWork"/>); 190 (pretend) any.
    /// </summary>
    TownBuilding? PickWork(World world, CharacterId self, int town, int epoch, TaskType type = TaskType.AutoLabouringMinesPretend)
    {
        if (!WorkAllowed(world, self.Slot, town)) return null;
        var list = WorkplacesFor(town, type);
        int free = 0;
        foreach (var b in list) if (HasFreeSlot(b)) free++;
        if (free == 0) return null;
        int pick = Rng.Int(Rng.Hash(world.Seed, Rng.Key(self), RngPurpose.Think, SeedSalts.AiTarget ^ (ulong)epoch << 20 ^ 0x57), free);
        foreach (var b in list)
            if (HasFreeSlot(b) && pick-- == 0) return b;
        return null;
    }

    /// <summary>
    /// Whether a mind holds a workplace that is still one its goal <paramref name="type"/> may work at.
    /// </summary>
    bool HoldsWork(AiMind mind, int town, TaskType type) => mind.WorkPost is { } post && WorkplacesFor(town, type).Contains(post);

    /// <summary>
    /// Whether a workplace has an operator slot no one holds in this cadence pass (73 <c>machineHasFreeOperatorSlot</c>; the finder's
    /// <c>couldIOperate</c>, docs/game/ai-tasks.md "Worker"): fewer holders than its functionality's <c>max operators</c>.
    /// </summary>
    bool HasFreeSlot(TownBuilding building) => _workTaken.GetValueOrDefault(building) < _work.MaxOperatorsOf(building);

    /// <summary>
    /// The operator slots of 189 / 190 (docs/game/ai-tasks.md "Worker"): a member whose goal is one of them keeps the workplace it holds while that
    /// is still one its goal may work at, else holds one with a free slot drawn as <see cref="PickWork"/> draws; a member on another goal lets its
    /// slot go. Serial, on the package cadence, after every squad's kept slots are reserved (<see cref="ReserveWork"/>).
    /// </summary>
    void ClaimWork(World world, Blackboard board, Squad squad)
    {
        foreach (var id in squad.Members)
        {
            if (!world.Characters.TryResolveNext(id, out int s) || world.Characters.Cold(s)?.Ai is not { } mind) continue;
            var type = mind.Goal?.Task.Type;
            bool works = type is TaskType.AutoLabouringMines or TaskType.AutoLabouringMinesPretend;
            if (works && HoldsWork(mind, board.Town, type!.Value)) continue;
            if (mind.WorkPost is { } left) _workTaken[left] = _workTaken.GetValueOrDefault(left) - 1;
            mind.WorkPost = works ? PickWork(world, id, board.Town, mind.WorkEpoch, type!.Value) : null;
            if (mind.WorkPost is { } got) _workTaken[got] = _workTaken.GetValueOrDefault(got) + 1;
        }
    }

    /// <summary>
    /// The workplaces the members hold, reserved for every squad before any claims (as the seats, <see cref="TownPlaceClaims.Reserve"/>).
    /// </summary>
    void ReserveWork(World world)
    {
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
    IReadOnlyList<TownBuilding> WorkplacesFor(int town, TaskType type)
    {
        if (type != TaskType.AutoLabouringMines) return Places(town).Workplaces;
        return _townPlaces.RealWorkOf(town);
    }

    /// <summary>
    /// Whether 189 can really work at a workplace (docs/game/ai-tasks.md "Worker"). Its act-out, 87 Operating machine, needs !62
    /// <c>machineHasEmptyInputs</c>, and with an empty input the plan must fill the machine from a source of its input, so the original's workers
    /// keep to the workplaces they can run. A mine has no input; a refinery, research or crafting bench counts only with a machine running there with
    /// its inputs (engine choice: the pick does not ask whether 94 / 93 could fill an empty one).
    /// </summary>
    internal static bool RealWork(World world, TownBuilding building)
    {
        if (building.Function == MineFunction) return true;
        foreach (var m in world.Machines.All)
            if (m.Position.X == building.Position.X && m.Position.Z == building.Position.Z) return !m.HasEmptyInputs;
        return false;
    }

    /// <summary>
    /// The player-flag gate of the building finders of goals 189 / 190 (<c>FUN_1408cac60</c> / <c>FUN_1408cadb0</c>, <b>Verified</b>): the town's
    /// owner and the character must agree on being the player's faction; a player-faction character in a town that is not the player's works only as
    /// an owned slave (slave state 1).
    /// </summary>
    bool WorkAllowed(World world, int slot, int town)
    {
        int player = world.Player.Faction >= 0 ? world.Player.Faction : data.PlayerFaction;
        if (player < 0 || world.Characters.Cold(slot) is not { } cold) return true;
        bool townIsPlayer = town >= 0 && town < data.SiteFactions.Count && data.SiteFactions[town] == player;
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
    /// Staying home at a workstation of the home (the pretend operating of 221, <see cref="PretendOperate"/>).
    /// </summary>
    static bool PretendsAtHome(Blackboard board, int slot, AiBehaviour behaviour, TaskType type) =>
        behaviour == AiBehaviour.Sit && type == TaskType.StayInHome && board.SeatClaims.TryGetValue(slot, out var claim)
            && claim.Kind == HomeSpotKind.Workstation;

    /// <summary>
    /// At a workstation, pretending to operate it (effect 136 IS_WORKING_PRETEND, row 221): the machine's work clip and hunger rate, as the real
    /// operating.
    /// </summary>
    void PretendOperate(World world, int slot, AiMind mind, HomeSpot piece)
    {
        var clip = _work.PieceClipOf(piece);
        mind.WorkActive = true;
        mind.WorkClip = clip;
        mind.WorkHungerRate = piece.HungerRate;
        if (world.Characters.Cold(slot) is { } worker) worker.ActionClip = clip;
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
            mind.Plan.Clear();
            mind.Goal = null;
            mind.Behaviour = AiBehaviour.None;
            mind.Version = board.Version;
            return;
        }

        var type = goal.Task.Type;
        var behaviour = TaskBehaviours.Of(type);
        var profile = TaskProfiles.Of(type);
        bool fresh = TakeUp(world, slot, mind, goal, type, profile);
        behaviour = Adopt(world, slot, mind, goal, board, type, behaviour, fresh);

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
    /// Whether the goal is taken up anew, and then its start over: a fresh target and node roll, the spots given up tried again, and for a goal it
    /// commits to a fresh commitment.
    /// </summary>
    static bool TakeUp(World world, int slot, AiMind mind, AiGoal goal, TaskType type, TaskProfile profile)
    {
        var table = world.Characters;

        // A goal taken up anew starts over: a fresh target and, for a goal it commits to, a fresh commitment. So does a committed goal chosen again
        // after its commitment ran out (engine reading: the original's target finders roll a new target then, so the goal counts as new).
        bool fresh = mind.Goal is not { } before || before.Task.Type != type || before.Task.Target != goal.Task.Target
            || profile.Commits && world.GameHours >= mind.CommitUntil;
        if (!fresh) return false;

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
    /// The mind takes the goal and its behaviour (staying home on a claimed chair sits); what the last goal left is cleared: the wander radius, the
    /// sitting clip, the work it stopped and the follow target. Returns the behaviour.
    /// </summary>
    AiBehaviour Adopt(World world, int slot, AiMind mind, AiGoal goal, Blackboard board, TaskType type, AiBehaviour behaviour, bool fresh)
    {
        var table = world.Characters;

        // Staying home sits on the chair its squad claimed for it (the original's 43 operates home furniture, chairs among it); without one it stands
        // at home.
        if (type == TaskType.StayInHome && board.SeatClaims.ContainsKey(slot) && !mind.SeatUnreachable) behaviour = AiBehaviour.Sit;
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
    /// home and near the target, and the ditch request. False when the step took the think (the act-out waits); <paramref name="behaviour"/> may
    /// become the walk home.
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
            case AiBehaviour.Captive:
                AskCaptiveJob(world, slot, mind);
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
            case AiBehaviour.Crouch:
                // RescueLeader: the carry system's sweep lifts the leader (CarrySystem.Rescue); until then it stands. Crouch: it stands where it is;
                // the animation system plays the clip and sets the stance.
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
            TaskStart.StartOwn(world, slot, new TaskRequest(CharacterTask.Sleep, type, TaskSource.Goal));
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
    /// A patrol package's route: the leader walks (or runs) to the route's next point, the others follow it; without a route it wanders.
    /// </summary>
    static void ActPatrol(World world, int slot, AiMind mind, Blackboard board)
    {
        var table = world.Characters;
        bool leader = mind.Squad.Leader == table.IdOf(slot);

        if (board.CurrentClass is PackageClass.Patrol or PackageClass.PatrolRunning && board.RouteIndex < board.Route.Length)
        {
            if (!leader) Become(world, slot, CharacterTask.Follow);
            else WalkTo(world, slot, board.Route[board.RouteIndex], board.CurrentClass == PackageClass.PatrolRunning ? SpeedMode.Run : SpeedMode.Walk);
        }
        else Become(world, slot, CharacterTask.Wander);
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
        var (target, home, floor, facing) = spare is not null
            ? (Flat(spare.Position), true, spare.Position.Y, (float?)null)
            : SpotFor(mind, board, behaviour, type);
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
        else if (facing is { } yaw) table.Next[slot].Yaw = yaw;   // at a node, facing its way (the barman over his counter)
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
            mind.ShopAt = ShopFor(Places(board.Town), mind, TargetRoll(world, table.IdOf(slot), behaviour),
                RandomShopping(world, board.Town));
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

        var stepAt = StepBuilding(mind, board.Town);
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
            workerCold.ActionWork = StandingWork(mind.WorkFunctionality, mind.WorkClip);
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
        if ((mind.WorkPost ?? PickWork(world, table.IdOf(slot), board.Town, mind.WorkEpoch, type)) is not { } building) return;

        mind.Target = new Vector3(building.Position.X, float.NaN, building.Position.Z);
        mind.WorkClip = _work.ClipOf(building);
        mind.WorkFunctionality = _work.FunctionalityOf(building);
        mind.WorkHungerRate = _work.HungerRateOf(building);
        mind.WorkBuilding = building;
        mind.WorkOwner = WorkOwnerOf(board.Town);
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
                Crime.CrimeSystem.FloorOf(world, slot, world.Characters.Previous[slot].Position), world.Sight.PlaceOf(slot), world)
            : null;

    /// <summary>
    /// Relaxing and sitting: at its claimed seat (a chair or throne, else a node; a training dummy is stood at), on the chair's origin facing its
    /// way, a workstation pretend-operated.
    /// </summary>
    void ActSeated(World world, int slot, AiMind mind, Blackboard board, TaskType type)
    {
        ref var n = ref world.Characters.Next[slot];

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

        if (claimed.Kind == HomeSpotKind.Workstation) PretendOperate(world, slot, mind, claimed);
        Become(world, slot, claimed.Kind is HomeSpotKind.Training or HomeSpotKind.Workstation ? CharacterTask.Idle : CharacterTask.Rest);
    }

    /// <summary>
    /// A turret guard: to the gun of the turret its squad claimed for it, facing its way; without one it stands.
    /// </summary>
    void ActTurret(World world, int slot, AiMind mind, Blackboard board)
    {
        var table = world.Characters;

        if (board.TurretClaims.TryGetValue(slot, out int t) && (uint)t < (uint)board.Turrets.Count && board.Turrets[t].Turret is { } turret)
        {
            var spot = TurretSpot(world, turret);

            if (GoToSpot(world, slot, mind, Flat(spot), GiveUp.Post, SpeedMode.Walk, TurretSlack, spot.Y))
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
    /// floor it is on, not on the ground below); <c>Yaw</c> the way a node faces, which the character turns to on arriving.
    /// </summary>
    (Vector2 Target, bool Home, float? Floor, float? Yaw) SpotFor(AiMind mind, Blackboard board, AiBehaviour behaviour, TaskType type)
    {
        if (behaviour == AiBehaviour.GoHome)
        {
            // Retreating (128) and Retreating fast (127) walk to the home town's waypoint outside its gates (FUN_14033dd70 -> Town vfunc 0x38), never
            // to a building.
            if (type is TaskType.RetreatHometown or TaskType.RunAwayHometown && RetreatPoint(mind.Squad) is { } waypoint)
                return (waypoint, true, null, null);

            if (board.Home is { } h && !mind.HomeUnreachable) return (Flat(h.Position), true, null, null);
            if (board.Town >= 0) return (Flat(data.Sites[board.Town].Position), true, null, null);
            return (mind.Squad.HomeCentre, true, null, null);
        }

        // 157 Static town guard: the post of its town it holds (ClaimTownPosts).
        if (type == TaskType.StandAtGuardNodeHometownOutside && !mind.HomeUnreachable && mind.TownPost is { Spot: var post })
            return (Flat(post.Position), true, post.Position.Y, post.Yaw);

        if (!mind.HomeUnreachable && GateSpot(board, mind, type) is { } guard) return (Flat(guard), true, guard.Y, null);
        if (board.Home is not { } home || mind.HomeUnreachable || type is TaskType.HoldPosition or TaskType.ManTheGate)
            return (mind.Post, false, null, null);

        HomeSpotKind kind = behaviour == AiBehaviour.Sleep ? HomeSpotKind.Bed : type switch
        {
            TaskType.StandAtShopkeeperNode => HomeSpotKind.Shopkeeper,
            TaskType.StandAtGuardNodeHomebuilding or TaskType.StandAtGuardNodeHomebuildingIndoorsOnly
                or TaskType.StandAtGuardNodeHometownOutside => HomeSpotKind.Guard,
            _ => HomeSpotKind.General,
        };
        ulong roll = kind == HomeSpotKind.Bed ? 0 : mind.NodeRoll;   // beds stay round robin (the bed search is not the node finders')
        if (kind == HomeSpotKind.Guard && mind.GuardVisitorAt is { } visitor && Nearest(home, kind, visitor) is { } beside)
            return (Flat(beside.Position), true, beside.Position.Y, beside.Yaw);

        if (Pick(home, kind, mind.MemberIndex, roll) is { } spot) return (Flat(spot.Position), true, spot.Position.Y, spot.IsNode ? spot.Yaw : null);

        // Guard posts fall back to general nodes (docs/game/ai-tasks.md "Town goals"); the counter is the shopkeeper's, so only its goal falls back
        // onto it.
        if (kind == HomeSpotKind.Guard && Pick(home, HomeSpotKind.General, mind.MemberIndex, roll) is { } general)
            return (Flat(general.Position), true, general.Position.Y, general.Yaw);

        // The node goals (shopkeeper, guard, general) find a node of their own type and nothing else (FUN_14099ec70, FUN_14099f280: an empty hand,
        // see NodeGoalFindsNothing); the others stand at any node.
        if (kind != HomeSpotKind.Bed && !IsNodeGoal(type)
            && Pick(home, null, mind.MemberIndex, roll, kind != HomeSpotKind.Shopkeeper) is { } any)
            return (Flat(any.Position), true, any.Position.Y, any.Yaw);

        // No bed: it sleeps in the house; a node goal without nodes stays at its own post, except staying home.
        if (kind == HomeSpotKind.Bed || type == TaskType.StayInHome) return (Flat(home.Position), true, null, null);
        return (mind.Post, false, null, null);
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
    /// bed is found.
    /// </summary>
    Locks.Fixture? SpareBed(World world, Blackboard board, AiMind mind, int slot)
    {
        if (board.Home is not null && !mind.HomeUnreachable && BedSpotOf(board, mind) is not null) return null;
        if (board.Home is null && !mind.HomeUnreachable && OccupiedBed(world, board, mind, slot) is { } occupied) return occupied;
        if (world.Characters.Cold(slot) is not { } self) return null;
        if (mind.Squad.Town >= 0) return world.Fixtures.FindBed(world, slot, data.Relations);
        var at = Flat(world.Characters.Next[slot].Position);
        float r2 = Locks.FixtureRegistry.BedSearchRange * Locks.FixtureRegistry.BedSearchRange;
        var relations = data.Relations;
        return world.Fixtures.FindBed(world, slot, relations, f =>
            Vector2.DistanceSquared(at, Flat(f.Position)) > r2
            || f.Owner != self.Faction
                && !(f.Owner >= 0 && relations.Contains(f.Owner) && relations.Contains(self.Faction)
                    && relations.IsAlly(f.Owner, self.Faction)));
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
    internal static Vector2? LeavePoint(World world, TownBuilding building)
    {
        foreach (var door in world.Fixtures.DoorsByBuilding[building.Key])
        {
            if (world.Fixtures.DoorSpotOf(door) is not { } s) continue;
            return DoorExit(new Vector2(s.Centre.X, s.Centre.Z), new Vector2(s.Normal.X, s.Normal.Z),
                new Vector2(building.Position.X, building.Position.Z));
        }

        return null;
    }

    /// <summary>
    /// The point outside a door that <c>FUN_140296a40</c> yields (<b>Verified</b>): A + (A - B) x 1.1 with A and B the points 15 either side of the
    /// leaf along its normal; the outer side is the one away from the building's origin (engine reading). Shared by the leave-building task and the
    /// intruder carry-out.
    /// </summary>
    internal static Vector2 DoorExit(Vector2 leafCentre, Vector2 normal, Vector2 building)
    {
        if (Vector2.Dot(normal, leafCentre - building) < 0) normal = -normal;
        Vector2 a = leafCentre + normal * 15, b = leafCentre - normal * 15;
        return a + (a - b) * 1.1f;
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
    /// Switches to a standing task (wander, follow, idle, rest), dropping a walk in progress; nothing when it is doing it already. A goal's start on
    /// the character's own slot (<see cref="TaskStart.StartOwn"/>: the think runs in parallel), as the row of the goal it acts out.
    /// </summary>
    static void Become(World world, int slot, CharacterTask task)
    {
        if (world.Characters.Next[slot].Task == (byte)task) return;
        TaskStart.StartOwn(world, slot, new TaskRequest(task, GoalRow(world, slot), TaskSource.Goal));
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
    /// Sends it walking (or running) to a point, unless it is going there already (<see cref="TaskStart.StartOwn"/>, as <see cref="Become"/>).
    /// </summary>
    static void WalkTo(World world, int slot, Vector2 to, byte mode)
    {
        ref var n = ref world.Characters.Next[slot];
        if (n.Task == (byte)CharacterTask.GoTo && Vector2.DistanceSquared(n.Goal, to) < 1)
        {
            n.Mode = mode;
            return;
        }

        TaskStart.StartOwn(world, slot, new TaskRequest(CharacterTask.GoTo, GoalRow(world, slot), TaskSource.Goal) { Goal = to, Mode = mode });
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

            if (cold.InSomething == Locks.HeldState.None) world.Fixtures.EnterBed(world, table.IdOf(s), bed);
        }
    }

    #endregion

    #region Serial step

    /// <summary>
    /// The turrets, seats, sitting-around pieces and town guard posts the members claim on the package cadence.
    /// </summary>
    readonly TownPlaceClaims _claims = new(data);

    /// <summary>
    /// The contract and AI change commands: the squad (attached first when the AI does not run it yet) takes the package.
    /// </summary>
    public void Inputs(World world, IReadOnlyList<SimCommand> commands)
    {
        foreach (var command in commands)
        {
            var (squadId, packageId) = command switch
            {
                StartContract s => (s.SquadId, s.PackageId),
                ChangeAi a => (a.SquadId, a.PackageId),
                _ => (-1, ""),
            };
            if (world.Squads.Find(squadId) is not { } squad || data.Ai.Package(packageId) is not { } package) continue;
            if (squad.Ai is null)
            {
                if (IsPlayers(world, squad)) continue;
                Attach(world, squad);
            }

            _rules.Bind(this, world, squad);
            if (command is StartContract c) squad.Ai!.StartContract(package, world.GameHours, c.Hours, _rules, c.Target);
            else squad.Ai!.Replace(package, _rules);
        }
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
    /// The requests the thinks left with a flag (placing, beds, shopping, assaults, fights, treatments, leaving fights) and the captive jobs, in that
    /// order; the systems they need are looked up once.
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
    /// One squad's turn on the package cadence: its board's state and package (a campaign's phase package), the contract end talk, its route, the
    /// minds and crowd of its members, and the claims (turrets, seats, sitting, work, town posts) and gate.
    /// </summary>
    void UpdateSquad(World world, Squad squad)
    {
        var board = squad.Ai!;
        board.Intact = IsIntact(world, squad, board);
        UpdateInTown(world, squad, board);
        _rules.Bind(this, world, squad);

        var platoon = PlatoonOf(world, squad);
        if (platoon?.Campaign is { } campaign && PhasePackage(world, platoon, campaign) is { } phase) InstallPhasePackage(world, board, phase);

        board.Update(_rules, world.GameHours);
        if (platoon?.Campaign is null) StepPackage(world, squad, board);   // a campaign's squad has its war targets from the campaign

        // A contract that ended: its leader says the package's contract end talk to the employer (event 30, FUN_14026a110).
        if (board.TakeEnded() is { Package.ContractEndTalk: { } talk, Target: var employer } && !squad.Leader.IsNone)
        {
            world.Defer(new Dialogue.DialogueEvent(squad.Leader, employer, Dialogue.DialogueSystem.ContractJobEnded, talk)
                { Tick = world.Tick + 1 });
        }

        AdvanceRoute(world, squad, board);
        board.Crowd.Clear();
        board.CrowdEntries.Clear();

        bool populationMoves = platoon is not null && !BoardMoves(board);
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

        if (!populationMoves && cold.Ai is null or { Engine: true })
        {
            var p = table.Next[s].Position;
            cold.Ai = new AiMind
            {
                Squad = squad,
                MemberIndex = k,
                Post = OccupierPost(world, board, k) ?? new Vector2(p.X, p.Z),
                RaceGoals = cold.Race is { } race ? data.Ai.RaceGoals(race.StringId) : [],
                Resume = squad.Resume,
                Shopping = ShoppingList.Make(data.Db, cold.RecordId, world.Seed, Rng.Key(table.IdOf(s))),
            };
        }

        if (cold.Ai?.Goal is { } g)
        {
            board.Crowd[g.Task.Type] = board.Crowd.GetValueOrDefault(g.Task.Type) + 1;
            board.CrowdEntries.Add(new CrowdEntry(squad.Members[k], g.Task.Type, CrowdTarget(g.Task.Target, squad, board, squad.Members[k])));
        }
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
    /// How long the war manager's phase package runs as the platoon's override before it is installed afresh (<c>FUN_140272240</c> called with 24
    /// game hours, <b>Verified</b>).
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
    /// The BuildingFunction bits the finder of a new home looks through: 0x13, BF_ANY, MINE and REFINERY (the house function's number 19 passed as a
    /// bit mask, as the worker finder's 0x41a is read, <b>Observed</b>); a building with no functionality counts as BF_ANY.
    /// </summary>
    const int RehomeFunctions = 0x13;

    /// <summary>
    /// Gives a squad its AI: its home (assigned, or the one its town gave it), whose template and initial door state apply, and a new blackboard of
    /// its template's packages, taken as already in the town it starts in; a saved squad's AI state is applied to it.
    /// </summary>
    void Attach(World world, Squad squad)
    {
        var (packages, template) = TemplateOf(squad.TemplateId);

        // A platoon's squad on the road has no building of its own to claim (only one its town gave it).
        bool given = squad.HomeBuilding is not null;
        var home = PlatoonOf(world, squad) is null ? AssignHome(world, squad, template) : squad.HomeBuilding;

        // A building the squad claims itself takes its template (FUN_140553210); one its town gave it was noted when it was made, a saved squad's
        // (from a save with no building record) now.
        if (home is not null && template is not null && (!given || world.Fixtures.RecordOf(home.Key) is null))
            world.Fixtures.NoteAssigned(home.Key, template);

        // The template's initial door state applies to the home it is given (docs/game/locks.md "Doors"); the first squad's wins, a saved building
        // keeps its own.
        if (home is not null) world.Fixtures.ApplyInitialDoorState(home.Key, template?.InitialDoorState ?? 0);
        ulong roll = Rng.Hash(world.Seed, (ulong)squad.Id, RngPurpose.Think, SeedSalts.AiPatrol);
        squad.Ai = new Blackboard(packages)
        {
            Town = squad.Town,
            Size = squad.Members.Count,
            OwnHome = home,
            Home = home,
            HomePublic = template?.PublicDay ?? true,
            NextPatrol = world.GameHours + Rng.Float(roll) * Settings.FirstPatrolHours,
        };

        // The town it starts in, taken as already entered (engine choice: no ENTER_TOWN line for every squad a town makes).
        if (LeaderPosition(world, squad) is { } at) (squad.Ai.InTown, squad.Ai.InTownSince) = (CurrentTown(world, at), world.GameHours);
        if (squad.SavedAi is { } saved)
        {
            _rules.Bind(this, world, squad);
            saved.Apply(squad.Ai, data.Ai, _rules);
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
        if (IsPlayers(world, squad)) return;
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

            if (cold.Ai is { Engine: false }) cold.Ai = null;
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
        }
    }

    /// <summary>
    /// The package the war manager's record gives a campaign platoon for its phase (<c>FUN_140272570</c>: pt1, pt2 or retreat, the leader platoon's
    /// or the others'); null after the retreat or when the campaign lists none.
    /// </summary>
    AiPackage? PhasePackage(World world, Platoon platoon, CampaignInstance inst) =>
        inst.Phase <= 2 && world.SystemOf<PopulationSystem>()?.CampaignOf(inst.CampaignId)?.PackageOf(inst.Phase, inst.Leader == platoon.Id)
            is { } id ? data.Ai.Package(id) : null;

    /// <summary>
    /// Installs the phase package as the blackboard's 24 h timed override when it holds none or another (the war manager's check on every visit,
    /// <c>FUN_140272570</c>, <b>Verified</b>); the override running out ends it (<see cref="Blackboard.Update"/>), the slots choose that visit and
    /// the same record installs it afresh on the next.
    /// </summary>
    void InstallPhasePackage(World world, Blackboard board, AiPackage package)
    {
        if (board.Contract?.Id == package.Id) return;
        board.StartContract(package, world.GameHours, PhaseHours, _rules);
    }

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
    /// save's wild one like the wild sites' squads), and whose template lists AI packages.
    /// </summary>
    bool Runs(World world, Squad squad) => !IsPlayers(world, squad) && TemplateOf(squad.TemplateId).Packages.Count > 0;

    /// <summary>
    /// The platoon whose squad this is when the population moves it (a loaded roaming or campaign platoon: not a town's own saved squad, nor a wild
    /// one); null for any other squad. Every loaded platoon runs its blackboard in the original (<c>FUN_1402732b0</c> from the platoon's slot 27,
    /// docs/game/ai.md "Blackboard update"), whatever its kind.
    /// </summary>
    Platoon? PlatoonOf(World world, Squad squad) =>
        squad.PlatoonId >= 0 && !squad.Pinned && !squad.Wild ? world.Platoons.Find(squad.PlatoonId) : null;

    /// <summary>
    /// Whether the running package moves the squad itself (the trader, the refugee, the shipping and retreat classes): one of its goals travels and a
    /// mission town is set. Else the population's trip or campaign march moves a platoon's squad and its members keep the engine's routine
    /// (<see cref="AiMind.Engine"/>), as before the platoons ran a blackboard.
    /// </summary>
    internal static bool BoardMoves(Blackboard board)
    {
        if (board.Destination.IsNone && board.MissionTown < 0 && board.RefugeeTarget < 0) return false;
        foreach (var role in board.Roles)
        {
            foreach (var goal in role)
                if (TaskBehaviours.Of(goal.Task.Type) == AiBehaviour.Travel) return true;
        }

        return false;
    }

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
            if (squad.HomeBuilding?.Key == building || squad.Ai?.OwnHome?.Key == building) Rehome(world, squad);
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
    /// A running patrol moves on when its leader reaches the route point; the members think again at once.
    /// </summary>
    void AdvanceRoute(World world, Squad squad, Blackboard board)
    {
        if (board.CurrentClass is not (PackageClass.Patrol or PackageClass.PatrolRunning) || board.RouteIndex >= board.Route.Length) return;
        if (!world.Characters.TryResolveNext(squad.Leader, out int s)) return;
        var p = world.Characters.Next[s].Position;
        if (Vector2.Distance(new Vector2(p.X, p.Z), board.Route[board.RouteIndex]) > Settings.RouteArrive) return;
        board.RouteIndex++;
        board.Touch();
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
            PackageClass.Patrol or PackageClass.PatrolRunning =>
                board.Town >= 0 && _world.GameHours >= board.NextPatrol && _world.Characters.IsAlive(_squad.Leader)
                && !_system.CannibalPatrolHeld(_world, _squad, board, package),
            PackageClass.Bar => BarHere(board),
            PackageClass.BarOutdoors => true,
            PackageClass.CannibalRetreat or PackageClass.CannibalsAtHome or PackageClass.PrisonerGathering or PackageClass.SlaverShipping =>
                _system.CaptiveCanStart(_world, _squad, board, package, kind),
            PackageClass.WanderingTrader => _system.TraderCanStart(_world, _squad, board),
            PackageClass.PassingAssault => _system.AssaultCanStart(_world, _squad, board),
            PackageClass.Refugee => RefugeeCanStart(_world, _squad),
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
            PackageClass.Patrol or PackageClass.PatrolRunning =>
                board.RouteIndex >= board.Route.Length || _world.GameHours >= board.PatrolGiveUp || !_world.Characters.IsAlive(_squad.Leader),
            PackageClass.CannibalRetreat or PackageClass.CannibalsAtHome or PackageClass.PrisonerGathering or PackageClass.SlaverShipping =>
                _system.CaptiveFinished(_world, _squad, board, package, kind),
            // Away from home a bar package ends where it could not start (the original's finished test is its start test negated).
            PackageClass.Bar => !_squad.TownOwn && !board.InTown.IsNone && board.InTown != CampaignTown.OfSite(board.Town) && !BarHere(board),
            PackageClass.WanderingTrader => board.TraderState == 2,
            PackageClass.PassingAssault => _system.AssaultFinished(_world, _squad, board, package),
            PackageClass.OccupyTown => _system.OccupyFinished(_world, _squad, board),
            PackageClass.Refugee => _system.RefugeeFinished(_world, _squad, board),
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
        /// A package of class <paramref name="kind"/> just started for the bound squad: a patrol lays its route, a bar package takes a table, and the
        /// other classes set up what they need.
        /// </summary>
        public void Started(Blackboard board, AiPackage package, PackageClass kind)
        {
            switch (kind)
            {
                case PackageClass.Patrol:
                case PackageClass.PatrolRunning:
                    _system.LayRoute(_world, _squad, board);
                    break;
                case PackageClass.Bar:
                case PackageClass.BarOutdoors:
                    if (BarTown(board) is >= 0 and var town
                        && _system.PickTable(_world, town, _squad, outdoors: kind == PackageClass.BarOutdoors) is var (bar, table))
                        (board.Home, board.BarTable) = (bar, table);
                    else if (kind == PackageClass.BarOutdoors) board.Home = null;
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
            }
        }
    }

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

    /// <summary>
    /// A patrol's route (<b>engine choice</b>: the original's <c>GoOutOnPatrol</c> is not decoded): <see cref="AiSettings.PatrolPoints"/> points
    /// round the home town at seeded angles and distances of 1.5 to 3 town radii, each pulled in towards the town until walkable (dropped when none
    /// is), then home. The next patrol may start 6 to 12 game hours after this one started; this one gives up after 6.
    /// </summary>
    void LayRoute(World world, Squad squad, Blackboard board)
    {
        if (board.Town < 0)
        {
            board.Route = [];
            return;
        }

        var site = data.Sites[board.Town];
        var centre = Flat(site.Position);
        float r = MathF.Max(site.Town.SizeRadius * site.Town.TownRadiusMult, 100);
        ulong key = Rng.Mix((ulong)squad.Id << 20 ^ (ulong)board.Version);
        float start = Rng.Float(Rng.Hash(world.Seed, key, RngPurpose.Think, SeedSalts.AiPatrol)) * MathF.Tau;
        var route = new List<Vector2>();
        for (int k = 0; k < Settings.PatrolPoints; k++)
        {
            float angle = start + k * MathF.Tau / Settings.PatrolPoints;
            float f = Rng.Float(Rng.Hash(world.Seed, key, RngPurpose.Think, (ulong)k + 1));
            float d = r * (Settings.PatrolRadiusMin + f * (Settings.PatrolRadiusMax - Settings.PatrolRadiusMin));
            for (int tries = 0; tries < 4; tries++, d *= 0.75f)
            {
                var p = centre + new Vector2(MathF.Sin(angle), MathF.Cos(angle)) * d;
                if (!world.Walkability.IsWalkable(p.X, p.Y)) continue;
                route.Add(p);
                break;
            }
        }

        route.Add(board.OwnHome is { } home ? Flat(home.Position) : centre);
        board.Route = [.. route];
        board.RouteIndex = 0;
        float every = Settings.PatrolEveryHoursMin
            + Rng.Float(Rng.Hash(world.Seed, key, RngPurpose.Think, 0x50)) * (Settings.PatrolEveryHoursMax - Settings.PatrolEveryHoursMin);
        board.NextPatrol = world.GameHours + every;
        board.PatrolGiveUp = world.GameHours + Settings.PatrolGiveUpHours;
    }

    #endregion
}