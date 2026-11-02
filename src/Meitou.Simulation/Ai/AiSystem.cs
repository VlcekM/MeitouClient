using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Ai;
using Meitou.Data.World;

namespace Meitou.Simulation.Ai;

/// <summary>Settings of the <see cref="AiSystem"/>; the defaults are the game's (engine choices unless a doc label says otherwise).</summary>
public sealed class AiSettings
{
    /// <summary>A character thinks every this many ticks (staggered by slot), and at once when its squad starts another package or its patrol moves on.</summary>
    public int ThinkEveryTicks { get; init; } = 15;
    /// <summary>Ticks between two package choices of a squad.</summary>
    public int PackageEveryTicks { get; init; } = 30;
    /// <summary>A character farther than this from its spot (node, bed, post) walks back to it.</summary>
    public float PostSlack { get; init; } = 12;
    /// <summary>The radius a character mills about its post in (<see cref="AiBehaviour.Mill"/>).</summary>
    public float MillRadius { get; init; } = 25;
    /// <summary>The score of the goal being acted out is multiplied by this (<b>Observed</b>, docs/game/ai.md "Scoring"), so a near tie does not flip.</summary>
    public float Continuity { get; init; } = 1.25f;
    /// <summary>A patrol package may start this many game hours after the last one started (uniform), and the first one within <see cref="FirstPatrolHours"/> of the squad appearing.</summary>
    public float PatrolEveryHoursMin { get; init; } = 6;
    public float PatrolEveryHoursMax { get; init; } = 12;
    public float FirstPatrolHours { get; init; } = 4;
    /// <summary>A patrol gives up after this many game hours.</summary>
    public float PatrolGiveUpHours { get; init; } = 6;
    /// <summary>A patrol's route: this many points round the home town, between these multiples of the town's radius from its centre, then home.</summary>
    public int PatrolPoints { get; init; } = 4;
    public float PatrolRadiusMin { get; init; } = 1.5f;
    public float PatrolRadiusMax { get; init; } = 3f;
    /// <summary>The patrol's leader is at a route point within this distance.</summary>
    public float RouteArrive { get; init; } = 40;
    /// <summary>
    /// The original's <c>attacks</c> (settings.cfg "Town attacks frequency": 0 Bombardment, 1 High, 2 Normal, 3 Fewer, 4 Rare, 5 Never; default 2): a faction's wait between
    /// passing-by assaults on the player's towns (docs/game/ai.md "Package classes"); Never stops them.
    /// </summary>
    public int TownAttacks { get; set; } = 2;
}

/// <summary>
/// Starts a contract package on a squad for some game hours, aimed at <see cref="Target"/> (docs/game/ai.md "Dialogue and AI": dialogue's <c>AI contract</c>, the target the
/// conversation's partner).
/// </summary>
public sealed record StartContract(int SquadId, string PackageId, float Hours, CharacterId? Target = null) : SimCommand;

/// <summary>Replaces a squad's packages with one (dialogue's <c>change AI</c>).</summary>
public sealed record ChangeAi(int SquadId, string PackageId) : SimCommand;

/// <summary>
/// The AI (docs/simulation.md "AI"; the original's in docs/game/ai.md). Squads whose template lists <c>AI packages</c> get a <see cref="Blackboard"/> that picks
/// the package by slot and package class; each member thinks on its own cadence: it scores the goals of its role list, its race's <c>AI Goals</c> and the
/// default idle goal by tier (urgent, then non-urgent, then fluff; the first tier with a positive score wins), weight, hour window, crowding and continuity,
/// acts out the winner through <see cref="TaskBehaviours"/> (in its home building's nodes and beds where it has one); its combat goals are goals of the same choice (AiSystem.Fights.cs),
/// which <see cref="AggroSystem"/>'s look asks it to weigh again when it senses a new enemy. The AI runs only characters that nothing else holds: not the player's, not in a fight it did not
/// start, fleeing, down, carrying, carried, treating, looting or trading; in its own fight it chooses but acts out nothing (<see cref="InFight"/>), and an engine duty or fight is taken over
/// only by the goals that pre-empt it (<see cref="Preempts"/>). The members of a squad it does not run get a mind that weighs only their combat goals (<see cref="AiMind.Engine"/>,
/// <see cref="EngineMinds"/>). Roaming platoons keep their travel stand-in (<see cref="PopulationSystem"/>; their off-screen mode follows
/// their package, <see cref="PlatoonJobs"/>).
/// <list type="bullet">
/// <item><c>Think</c> (parallel): the choice and the act-out write only the character's own state (its task, goal, path flags and <see cref="AiMind"/>);
/// they read the squad's blackboard, which only the serial steps change.</item>
/// <item><c>Inputs</c> (serial): contracts. <c>SlowWorld</c> (serial): blackboards, homes and minds for new squads; on the package cadence the intact test, the package
/// choice, the patrol's progress and the crowding counts.</item>
/// </list>
/// </summary>
public sealed partial class AiSystem(PopulationData data, AiSettings? settings = null) : ITickSystem, IStateHashed
{
    /// <summary>The blackboard an <see cref="AiMind.Engine"/> mind chooses under: no package, nothing claimed (its combat goals read none of it).</summary>
    static readonly Blackboard EngineBoard = new([]);

    /// <summary>Every character's default goal (docs/game/ai.md "Default tasks per character", Observed): IDLE at the fluff tier, weight 0.1.</summary>
    public static readonly AiGoal Idle = new(new AiTask("", "IDLE", TaskType.IDLE, TaskPriority.Fluff, TaskTargetType.Specific, TaskEndEvent.Nothing), 0, 100, 0.1f);

    /// <summary>
    /// Every character's other default goal (<c>Character::setupAI</c>, <b>Verified</b>; docs/game/ai-tasks.md "Self preservation"): SELF_PRESERVATION, urgent, aimed at itself, weight 1, all hours.
    /// The entries of the role and race lists (30 races list it) are the same goal and are left out of the scoring, so it counts once.
    /// </summary>
    public static readonly AiGoal SelfPreservationGoal = new(new AiTask("", "SELF_PRESERVATION", TaskType.SELF_PRESERVATION, TaskPriority.Urgent, TaskTargetType.Self, TaskEndEvent.Nothing), 0, 100, 1f);

    /// <summary>Walks to a spot in a row that may end elsewhere before it gives the spot up.</summary>
    public const int MaxFailedWalks = 3;

    /// <summary>How near a turret's gunner spot counts as arrived (units): well inside the turret's <c>use range</c>, which is 10 for the base game's turrets.</summary>
    public const float TurretSlack = 3;
    /// <summary>How near an enemy must be for <c>isNoEnemiesInVicinity</c> to fail (units; <b>engine choice</b>, about a turret's range: the original's radius is <b>Unknown</b>).</summary>
    public const float EnemyVicinity = 1000;
    FactionRelations Relations => data.Relations;

    /// <summary>The step that separates two candidates of the same score (the original's 0.0001).</summary>
    const float TieStep = 1e-4f;

    static readonly TaskPriority[] Tiers = [TaskPriority.Urgent, TaskPriority.NonUrgent, TaskPriority.Fluff];

    public AiSettings Settings { get; } = settings ?? new AiSettings();
    PopulationData Data => data;
    /// <summary>The towns' alarms the investigators answer (<see cref="AiBehaviour.Investigate"/>); null: none are raised.</summary>
    public Crime.CrimeSystem? Crimes { get; set; }
    /// <summary>
    /// The combat system, for the threats Self preservation reads (<see cref="Combat.CombatSystem.ThreatLevel"/>) and its fight's attacker (row 13); null: the world's, once the serial step has
    /// found it (none: nothing threatens anyone).
    /// </summary>
    public Combat.CombatSystem? ThreatSource { get; set; }

    /// <summary>The alarm an investigator answers: its squad's town's nearest in reach, for goal 210 only its own faction's (<see cref="Crime.CrimeSystem.AlarmFor"/>).</summary>
    internal Crime.TownAlarm? AlarmOf(World world, int slot, TaskType type, Blackboard board) =>
        Crimes is { Alarms.Count: > 0 } crimes && world.Characters.Cold(slot) is { } cold
            ? crimes.AlarmFor(board.Town, world.Characters.Previous[slot].Position, type == TaskType.INVESTIGATE_ALARMS_ALLIES_ONLY ? cold.Faction : -1, Crime.CrimeSystem.FloorOf(world, slot, world.Characters.Previous[slot].Position)) : null;

    readonly Dictionary<string, (IReadOnlyList<SlottedPackage> Packages, SquadTemplate? Template)> templates = [];
    // Squads already looked at (ids are never reused); derived from the squads, so not part of the hash.
    readonly HashSet<int> looked = [];
    // The turret indices of each town claimed in the current cadence pass (derived, not hashed).
    readonly Dictionary<int, HashSet<int>> turretTaken = [];
    // The seats of each bar claimed in the current cadence pass, by building (derived, not hashed).
    readonly Dictionary<TownBuilding, HashSet<int>> seatTaken = new(ReferenceEqualityComparer.Instance);
    readonly Rules rules = new();

    // ------------------------------------------------------------------ think (parallel)

    public void Think(World world, Partition part)
    {
        var table = world.Characters;
        var prev = table.Previous;
        int every = Math.Max(1, Settings.ThinkEveryTicks);
        int hour = world.Hour;
        for (int i = part.Start; i < part.End; i++)
        {
            if (!prev[i].Alive || table.Cold(i) is not { Ai: { Squad: var squad } mind } cold || (mind.Engine ? EngineBoard : squad.Ai) is not { } board) continue;
            // Cheapest test first: most characters are not due this tick.
            if ((world.Tick + i) % every != 0 && mind.Version == board.Version && !mind.Replan) continue;
            mind.Replan = false;
            var id = table.IdOf(i);
            var roles = mind.Engine ? mind.EngineGoals : ObeyedRoles(world, squad, cold) ?? board.Roles[(int)RoleOf(squad, id, cold)];
            ref var n = ref table.Next[i];
            if (Holds(n, cold))
            {
                if (mind.Engine)
                {
                    // Its own fight, with nothing but its combat goals and Self preservation to weigh: none wanted ends the fight, and the engine's routine takes it back.
                    if (Choose(world, i, id, mind, squad, board, roles, hour, Restrict.Combat) is { } kept) InFight(mind, kept, board);
                    else
                    {
                        mind.LeaveFight = true;
                        Interlocked.Exchange(ref leaving, 1);
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
                if (ReferenceEquals(urgent, SelfPreservationGoal) && mind.Step == TaskType.RUN_AWAY) mind.FleeRequest = FleeAsk.Threat;
                else if (!cold.InCombat) AskFight(world, i, urgent.Task.Type, mind);
            }
        }
    }

    /// <summary>
    /// The choice of a character in its own fight (<see cref="OwnsFight"/>): a combat goal or Self preservation's fight (row 13) keeps the fight running (the fight is its plan's task, already
    /// queued: no new start, whichever of them is current now; the combat system re-targets); Self preservation's run (35) or a goal 35 asks the flee step, which stops the fight; any other goal
    /// ends the fight in the serial step, and the character thinks again at the next tick to act it out (docs/game/ai-tasks.md "Combat goals"). Writes only its own mind.
    /// </summary>
    void InFight(AiMind mind, AiGoal goal, Blackboard board)
    {
        var behaviour = TaskBehaviours.Of(goal.Task.Type);
        if (behaviour is not (AiBehaviour.Fight or AiBehaviour.SelfPreserve or AiBehaviour.Flee))
        {
            mind.LeaveFight = true;
            Interlocked.Exchange(ref leaving, 1);
            return;
        }
        mind.Goal = goal;
        mind.Behaviour = behaviour;
        mind.Version = board.Version;
        if (behaviour == AiBehaviour.Flee || behaviour == AiBehaviour.SelfPreserve && mind.Step == TaskType.RUN_AWAY)
            mind.FleeRequest = behaviour == AiBehaviour.SelfPreserve ? FleeAsk.Threat : FleeAsk.Enemies;
    }

    // Set by a fighter's think that chose a goal that is no fight (AiMind.LeaveFight).
    int leaving;

    /// <summary>Which goals of a character the AI does not hold may still take it over (<see cref="Preempts"/>).</summary>
    internal enum Restrict : byte
    {
        None,
        /// <summary>Self preservation alone (its run, or its fight through row 13 against its own attacker).</summary>
        Self,
        /// <summary>
        /// The combat goals and Self preservation (priority 3): a fight they start ends an engine duty through its task's end; what an <see cref="AiMind.Engine"/> mind weighs on its routine
        /// and in its own fight.
        /// </summary>
        Combat,
    }

    /// <summary>
    /// What may take over an NPC with a mind that the AI does not hold (<see cref="Holds"/>), as the original's chooser would (its engine duties are goals and jobs of the same chooser, below a
    /// priority-3 goal, docs/game/ai-tasks.md "Precedence"): on an engine duty whose system lets a fight interrupt it, the combat goals and Self preservation; on any other (and in a fight it did
    /// not start: a dialogue's, an assault's, a duty's), Self preservation alone, whose fight or run (<see cref="SelfPreservation.Runs"/>) ends the duty's task as any fight's start does (in a
    /// fight, only its run changes anything). The duties a fight may interrupt: treating (<see cref="FirstAid.FirstAidSystem"/> lets go of the patient at the fight's start), a town errand's walk but
    /// the cage walk (<see cref="Kidnap.TownDutySystem"/> waits for the fight to end and walks on; one carrying an intruder out fights with the body on its shoulder, as the original's carrier
    /// does), a captive job's walk carrying nothing (<see cref="Kidnap.CaptiveSystem"/> gives the job up), and the routine of an <see cref="AiMind.Engine"/> mind (<see cref="EngineRoutine"/>).
    /// Nothing for one fleeing, imprisoned, carried or down, nor for a gunner or a carrier in a fight.
    /// </summary>
    internal static Restrict Preempts(in CharacterHot n, CharacterCold cold)
    {
        if (cold.Ai is not { } mind || cold.IsPlayer || cold.Fleeing || cold.IsImprisoned || !cold.CarriedBy.IsNone || cold.Medical is { Incapacitated: true }) return Restrict.None;
        if (cold.InCombat) return cold.Gunning is null && cold.Carrying.IsNone ? Restrict.Self : Restrict.None;
        if (cold.Engaged) return Restrict.Self;
        if (mind.Engine && EngineRoutine(n, cold)) return Restrict.Combat;
        if (n.Task == (byte)CharacterTask.Treat) return Restrict.Combat;
        return n.Task == (byte)CharacterTask.GoTo && cold.Running is { Source: TaskSource.Goal, Task: CharacterTask.GoTo } run
            && (Kidnap.TownDutySystem.IsErrandRow(run.Row) || cold.Carrying.IsNone && Kidnap.CaptiveSystem.IsJobRow(run.Row)) ? Restrict.Combat : Restrict.Self;
    }

    /// <summary>
    /// The engine's routine of an <see cref="AiMind.Engine"/> mind, which a fight its combat goals start interrupts: standing, wandering, following its leader, or the roaming leader's walk on its
    /// trip (row 53 of the population's travel stand-in, <see cref="PopulationSystem"/>; the trip goes on after the fight).
    /// </summary>
    static bool EngineRoutine(in CharacterHot n, CharacterCold cold) => (CharacterTask)n.Task switch
    {
        CharacterTask.Idle or CharacterTask.Wander or CharacterTask.Follow => true,
        CharacterTask.GoTo => cold.Running is { Source: TaskSource.Goal, Row: TaskType.TRAVEL_TO_TARGET_TOWN, Task: CharacterTask.GoTo },
        _ => false,
    };

    /// <summary>
    /// Whether the character's fight is the AI's own: the one the serial step started for its combat goal (<see cref="AiMind.FightFrom"/>), still running. A fight the combat system or another
    /// system started (a self-defence, a duty's, a dialogue's, an assault's) is not.
    /// </summary>
    internal static bool OwnsFight(CharacterCold cold, AiMind mind) => mind.FightFrom != 0 && cold.Running is { Task: CharacterTask.Attack } run && run.Tick == mind.FightFrom;

    /// <summary>
    /// The HUD's Goal text: the goal's name while the AI holds the character; "Running away" (35's name, <c>Task_Runaway</c>) for an NPC on the flee step, whichever of Self preservation, a goal 35 or a
    /// broken squad started it, rather than its walk's "Moving"; null for the rest (the HUD then names the movement task).
    /// </summary>
    public static string? ActivityText(in CharacterHot n, CharacterCold cold) =>
        cold is { Fleeing: true, IsPlayer: false, Medical.Incapacitated: not true } ? TaskBehaviours.Text(TaskType.RUN_AWAY)
        : cold.Ai is { Goal: { } goal } && Holds(n, cold) ? TaskBehaviours.Text(goal.Task.Type) : null;

    /// <summary>
    /// Whether the AI runs the character now: it has a mind and nothing else holds it (a fight it did not start, a flight, an order, a carry, a trade, a knock-out). In its own fight it thinks
    /// as anyone does (on the cadence and the replan triggers) and may leave it for another goal (<see cref="InFight"/>).
    /// </summary>
    public static bool Holds(in CharacterHot n, CharacterCold cold)
    {
        if (cold.Ai is not { } mind || cold.IsPlayer || cold.Fleeing || cold.Engaged || cold.IsImprisoned || !cold.CarriedBy.IsNone || !cold.Carrying.IsNone || cold.Medical is { Incapacitated: true }) return false;
        if (cold.InCombat) return n.Task == (byte)CharacterTask.Attack && OwnsFight(cold, mind);
        if (mind.Engine) return false;
        return (CharacterTask)n.Task switch
        {
            CharacterTask.Idle or CharacterTask.Wander or CharacterTask.Follow => true,
            // The walks to its spots, and lying or sitting there, are the AI's own.
            CharacterTask.GoTo => mind.Behaviour is AiBehaviour.Post or AiBehaviour.Sleep or AiBehaviour.Sit or AiBehaviour.GoHome or AiBehaviour.Patrol or AiBehaviour.Turret
                or AiBehaviour.TownNode or AiBehaviour.Shop or AiBehaviour.Relax or AiBehaviour.ToTown or AiBehaviour.Travel or AiBehaviour.Work or AiBehaviour.Investigate,
            CharacterTask.Rest => mind.Behaviour is AiBehaviour.Sleep or AiBehaviour.Sit or AiBehaviour.Relax,
            // In the bed it went to (or lying in one when loaded, before its first think).
            CharacterTask.Sleep => cold.InSomething == Locks.HeldState.Bed && (mind.Behaviour == AiBehaviour.Sleep || mind.Goal is null),
            _ => false,
        };
    }

    /// <summary>
    /// Job 187 Obedient Slave (<c>FUN_14050e5b0</c>, docs/game/ai-tasks.md "Obedient Slave"): a slave (state 1) not sneaking, owned by another squad, takes that squad's
    /// <c>Slave AI Goals</c> instead of its own lists. Null: it plans with its own squad's (a slave in its owner's squad already takes the Slave role there).
    /// </summary>
    static IReadOnlyList<AiGoal>? ObeyedRoles(World world, Squad squad, CharacterCold cold) =>
        cold.SlaveState == 1 && cold.SlaveMaster >= 0 && cold.SlaveMaster != squad.Id && (cold.Orders & CharacterOrders.Sneak) == 0
            && world.Squads.Find(cold.SlaveMaster) is { Ai: { } master } && master.Roles[(int)AiRole.Slave] is { Count: > 0 } list ? list : null;

    /// <summary>The role list a member takes (docs/game/ai.md "Goal loading"): the squad leader the leader's, else by its squad role.</summary>
    internal static AiRole RoleOf(Squad squad, CharacterId id, CharacterCold cold) =>
        squad.Leader == id || cold.Role == (int)SquadRole.Leader ? AiRole.Leader
        : cold.Role == (int)SquadRole.Squad2 ? AiRole.Squad2
        : cold.Role == (int)SquadRole.Slave ? AiRole.Slave
        : AiRole.Squad;

    /// <summary>
    /// The goal to act out, with its plan in <see cref="AiMind.Plan"/>: by tier (urgent, non-urgent, fluff), the goals with a positive score highest first,
    /// leaving out one whose list A already holds; the first whose plan succeeds wins (docs/game/ai-tasks.md "Planner": the pick). Idle when none does. With <paramref name="only"/>, only the
    /// priority-3 goals that may take over a character the AI does not hold (<see cref="Preempts"/>) are weighed, and null comes back when none plans.
    /// </summary>
    AiGoal? Choose(World world, int slot, CharacterId id, AiMind mind, Squad squad, Blackboard board, IReadOnlyList<AiGoal> roles, int hour, Restrict only = Restrict.None)
    {
        var race = mind.RaceGoals;
        var candidates = Candidates;
        var plans = PlanContext.Local.Bind(this, world, slot, mind, board);
        // While a goal it committed to is running, only a tier above that goal's priority may take over; if none plans, the goal and its plan stay
        // (FUN_140510ef0 in committed mode, Verified).
        var committed = only == Restrict.None && mind.Goal is { } running && mind.Plan.Count > 0 && TaskProfiles.Of(running.Task.Type).Commits && world.GameHours < mind.CommitUntil
            ? running : null;
        foreach (var tier in Tiers)
        {
            if (committed is not null && tier <= committed.Task.Priority || only != Restrict.None && tier != TaskPriority.Urgent) break;
            candidates.Clear();
            void Consider(AiGoal g)
            {
                if (g.Task.Priority != tier || !g.InWindow(hour)) return;
                // The list entries of Self preservation are the engine's own goal, which every character has once.
                if (g.Task.Type == TaskType.SELF_PRESERVATION && !ReferenceEquals(g, SelfPreservationGoal)) return;
                if (only switch
                    {
                        Restrict.Self => !ReferenceEquals(g, SelfPreservationGoal),
                        Restrict.Combat => TaskBehaviours.Of(g.Task.Type) != AiBehaviour.Fight && !ReferenceEquals(g, SelfPreservationGoal),
                        _ => false,
                    }) return;
                var profile = TaskProfiles.Of(g.Task.Type);
                float desire = Desire(world, TaskBehaviours.Of(g.Task.Type), g.Task.Type, profile, squad, board, mind, id, g.Task.Target);
                if (desire <= 0) return;
                int crowd = profile.Crowd == 1 ? 0 : board.CrowdOf(g.Task.Type, TaskDataTable.Of(g.Task.Type)?.Is(TaskDataFlags.NeedsTarget) ?? false, CrowdTarget(g.Task.Target, squad, board, id), id);
                float score = desire * g.Weight * (crowd > 0 ? MathF.Pow(profile.Crowd, crowd) : 1);
                if (mind.Goal is { } current && current.Task.Type == g.Task.Type && current.Task.Target == g.Task.Target)
                {
                    score *= Settings.Continuity;
                    // A goal it committed to holds at least 0.4 until the commitment runs out, then at most 0.05 (docs/game/ai.md "Packages and goals", Observed).
                    if (profile.Commits) score = world.GameHours < mind.CommitUntil ? MathF.Max(score, 0.4f) : MathF.Min(score, 0.05f);
                }
                if (score <= 0) return;
                // Highest first. The original keys its candidates by score and, while the key is taken, adds 0.0001 (FUN_14050df10, Verified): of equal
                // scores the goal listed later ranks first.
                for (int k = 0; k < candidates.Count; k++)
                    if (candidates[k].Score == score) { score += TieStep; k = -1; }
                int at = candidates.Count;
                while (at > 0 && candidates[at - 1].Score < score) at--;
                candidates.Insert(at, (g, score, desire));
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
        if (plans.TryPlan(Idle, Desire(world, TaskBehaviours.Of(Idle.Task.Type), Idle.Task.Type, TaskProfiles.Of(Idle.Task.Type), squad, board, mind, id, Idle.Task.Target))) return Idle;
        mind.Plan.Clear();
        return Idle;
    }

    [ThreadStatic] static List<(AiGoal Goal, float Score, float Desire)>? candidates;
    static List<(AiGoal Goal, float Score, float Desire)> Candidates => candidates ??= [];

    /// <summary>
    /// The character a goal is aimed at, as its <c>TaskInfo</c> entry holds it (<c>FUN_140269e90</c> copies the goal's target hand): the member itself for SELF, the leader for LEADER,
    /// the contract's mission target for SQUAD_MISSION; none for the rest (SPECIFIC and HOME_GATE are the same for every member of the squad here).
    /// </summary>
    internal static CharacterId CrowdTarget(TaskTargetType targeting, Squad squad, Blackboard board, CharacterId who) => targeting switch
    {
        TaskTargetType.Self => who,
        TaskTargetType.Leader => squad.Leader,
        TaskTargetType.SquadMission => board.MissionTarget,
        _ => CharacterId.None,
    };

    /// <summary>
    /// How much the character wants a goal: its type's desire (<see cref="TaskProfiles"/>; a type with a desire function of its own, 1), times what the goal's target
    /// finder found (docs/game/ai-tasks.md "Town goals", Observed: 0 when it found nothing). Going home is 0.5 while away from the home town and 0 in it
    /// (docs/game/perception.md 7.2, Observed); a goal the engine cannot act out is 0.
    /// </summary>
    float Desire(World world, AiBehaviour behaviour, TaskType type, TaskProfile profile, Squad squad, Blackboard board, AiMind mind, CharacterId self, TaskTargetType targeting)
    {
        if (behaviour == AiBehaviour.None) return 0;
        // Going to bed: the bed search is its target finder, whose factor is 0 when it finds no bed (FUN_1408cfe00, Verified), so the goal is not wanted.
        if (type is TaskType.GO_HOME_AND_GO_TO_BED or TaskType.GO_HOME_AND_GO_TO_BED_SECURE && !HasBed(world, board, mind, self.Slot)) return 0;
        float desire = profile.Desire switch
        {
            TaskProfiles.Random => 0.01f + 0.4f * Rng.Float(Rng.Hash(world.Seed, Rng.Key(self), RngPurpose.Think, SeedSalts.AiDesire ^ (ulong)world.Tick << 9 ^ (ulong)type)),
            TaskProfiles.Special => 1,
            var d => d,
        };
        return desire * behaviour switch
        {
            // A goal aimed at the squad's mission follows the contract's target (a bodyguard its employer), wanted only while there is one alive.
            AiBehaviour.Follow when targeting == TaskTargetType.SquadMission => board.MissionTarget.IsNone || board.MissionTarget == self || !world.Characters.IsAlive(board.MissionTarget) ? 0 : 1,
            AiBehaviour.Follow => squad.Leader.IsNone || squad.Leader == self || !world.Characters.IsAlive(squad.Leader) ? 0 : 1,
            AiBehaviour.GoHome => AtHomeTown(world, squad, board, self) ? 0 : 0.5f,
            // A turret is only wanted by a member its squad gave one (the nearest free one in <see cref="ClaimTurrets"/>).
            AiBehaviour.Turret => board.TurretClaims.ContainsKey(self.Slot) ? 1 : 0,
            AiBehaviour.TownNode => WanderIn(board.Town, world.Characters.Previous[self.Slot].Position).Count > 0 || OccupiedBuildings(world, board) > 0 ? 1 : 0,
            // Shopping is wanted while its list has something left to buy today (FUN_140999620), and there is a shop to go to.
            // The finder's factor follows its pick: 1 for a shop, 0.4 for any other public building, 0 when it finds none (FUN_1409997f0).
            AiBehaviour.Shop => !mind.Shopping.Pending(ShoppingList.DayOf(world.GameHours), world.Hour) ? 0
                : ShopFor(Places(board.Town), mind, TargetRoll(world, self, behaviour), RandomShopping(world, board.Town)) is { } picked ? ShopFactor(Places(board.Town), picked) : 0,
            // A worker needs a mine, bench or refinery in its town (the finders return 0 without one).
            AiBehaviour.Work => Places(board.Town).Workplaces.Count > 0 ? 1 : 0,
            // Relaxing needs a free seat of the package's bar, sitting around a free chair of the home (claimed in <see cref="ClaimSeats"/>); the original's finder returns 0 without one (FUN_1408caf00).
            AiBehaviour.Relax => board.BarTable >= 0 && board.SeatClaims.ContainsKey(self.Slot) && !mind.SeatUnreachable ? 1 : 0,
            AiBehaviour.Sit => board.SitClaims.ContainsKey(self.Slot) && !mind.SeatUnreachable ? 1 : 0,
            AiBehaviour.ToTown => TownAt(world.Characters.Previous[self.Slot].Position) >= 0 ? 0 : 1,
            // To the package's mission town while not in it.
            AiBehaviour.Travel => board.RefugeeTarget >= 0 ? 1 : board.Destination.IsNone ||CurrentTown(world, world.Characters.Previous[self.Slot].Position) == board.Destination ? 0 : 1,
            // Only while an alarm of its town is in reach (the row's done state !ALARMS_IN_THE_VICINITY).
            AiBehaviour.Investigate => AlarmOf(world, self.Slot, type, board) is null ? 0 : 1,
            // The assault's finder factor: 1 while a member of the mission target's squad passes its tests, else 0 (FUN_140793eb0).
            AiBehaviour.Assault => targeting == TaskTargetType.SquadMission && !AssaultTargetFor(world, self.Slot, board).IsNone ? 1 : 0,
            // Self preservation: the threat level (AI slot 17); Running away: row 35's own (AI slot 55), a goal of the packages that list it.
            AiBehaviour.SelfPreserve => SelfPreservationDesire(world, self.Slot, mind),
            AiBehaviour.Flee => world.Characters.Cold(self.Slot) is { } runner ? SelfPreservation.RunDesire(runner) : 0,
            // A combat goal: its own desire's zeros and factors, times its finder's factor (an attack goal's 1 with a target, a protect goal's fight threat; AiSystem.Fights.cs).
            AiBehaviour.Fight => FightDesire(world, self.Slot, type, mind) is var want and > 0 ? want * FightFactor(world, self.Slot, type, mind) : 0,
            _ => 1,
        };
    }

    /// <summary>The nearest town whose <see cref="TownData.Radius"/> holds a point (as the game's position lookup, <c>FUN_140928890</c>), -1 when none.</summary>
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

    /// <summary>A town's public buildings with a general node by zone, its shops, its public bars and its outdoor tables, as <see cref="RefreshPlaces"/> last found them.</summary>
    sealed record TownPlaces(IReadOnlyDictionary<ZoneCoordinate, TownBuilding[]> Wander, IReadOnlyList<TownBuilding> Shops, IReadOnlyList<TownBuilding> Bars,
        IReadOnlyList<TownBuilding> OutdoorTables, IReadOnlyList<TownBuilding> Workplaces,
        IReadOnlyDictionary<TownBuilding, ulong> Stock, IReadOnlyList<SeatClaim> Seats, IReadOnlyDictionary<TownBuilding, int> SeatOwners,
        IReadOnlyList<TownBuilding> Public);

    static readonly TownPlaces NoPlaces = new(new Dictionary<ZoneCoordinate, TownBuilding[]>(), [], [], [], [], new Dictionary<TownBuilding, ulong>(), [], new Dictionary<TownBuilding, int>(), []);
    // Rebuilt in the serial step, read by the think on every worker (derived, not hashed).
    readonly Dictionary<int, TownPlaces> places = [];

    TownPlaces Places(int site) => site >= 0 && places.TryGetValue(site, out var p) ? p : NoPlaces;

    /// <summary>The buildings wandering in <paramref name="site"/> can go to from <paramref name="at"/>: its public ones with a general node in the zone of that point.</summary>
    IReadOnlyList<TownBuilding> WanderIn(int site, Vector3 at) => Places(site).Wander.TryGetValue(WorldLayout.ZoneOf(at.X, at.Z), out var list) ? list : [];

    /// <summary>
    /// The towns' places for the AI's squads (serial, on the package cadence). A bar is a building whose residents' template has <c>building designation</c> BAR
    /// (the original's hang-around-bar package picks one of those in the home town, docs/game/ai-tasks.md "Town goals", Observed); a shop one whose residents
    /// trade (<see cref="SquadTemplate.HasShop"/>; the original's shopping looks for BF_SHOP furniture inside, which Meitou does not read: engine choice),
    /// and public, as the original's shop finders require.
    /// Residents are the squads living there, else the level data's <c>residents template</c>; the layout flags <c>is bar</c> / <c>is shop</c> count too.
    /// A building is public as its residents' template's <c>public day</c> (the original writes it over the home's flag), else as its level state's
    /// <c>is public day</c>, else public (engine choice: the original's default is Unknown); a town building the player owns as its Open to public toggle once set
    /// (the stats panel, <see cref="Locks.FixtureRegistry.PublicOf"/>).
    /// </summary>
    void RefreshPlaces(World world)
    {
        places.Clear();
        var shops = new HashSet<TownBuilding>(ReferenceEqualityComparer.Instance);
        var bars = new HashSet<TownBuilding>(ReferenceEqualityComparer.Instance);
        var publicFlag = new Dictionary<TownBuilding, bool>(ReferenceEqualityComparer.Instance);
        void Note(TownBuilding b, SquadTemplate? t)
        {
            if (t is null) return;
            if (t.HasShop) shops.Add(b);
            if (t.BuildingDesignation == DesignationBar) bars.Add(b);
            publicFlag.TryAdd(b, t.PublicDay);
        }
        var stocks = new Dictionary<TownBuilding, List<Trading.Shop>>(ReferenceEqualityComparer.Instance);
        foreach (var squad in world.Squads.All)
            if ((squad.Ai?.OwnHome ?? squad.HomeBuilding) is { } home)
            {
                Note(home, TemplateOf(squad.TemplateId).Template);
                // Only a building shop sells to shoppers: its stock is in containers, which is all FUN_14054add0 looks at (a trader's carried stock is not).
                if (squad.Shop is { Building: not null } shop) (stocks.TryGetValue(home, out var held) ? held : stocks[home] = []).Add(shop);
            }
        foreach (var squad in world.Squads.All)
            for (int pass = 0; pass < 2; pass++)
            {
                // The squad's town, and the one it is in (a bar package away from home takes that town's bar).
                int site = pass == 0 ? squad.Ai?.Town ?? -1 : squad.Ai?.InTown.Site ?? -1;
                if (site >= 0 && !places.ContainsKey(site)) AddPlaces(site);
            }

        void AddPlaces(int site)
        {
            var buildings = data.Sites[site].Buildings;
            foreach (var b in buildings)
                if (b.ResidentsTemplate.Length > 0) Note(b, TemplateOf(b.ResidentsTemplate).Template);
            bool IsPublic(TownBuilding b) => world.Fixtures.PublicOf(b.Key) ?? (publicFlag.TryGetValue(b, out bool open) ? open : b.LevelState?.GetBool("is public day", true) ?? true);
            var wander = buildings.Where(b => IsPublic(b) && b.Spots.Any(IsGeneralNode)).GroupBy(b => b.Zone).ToDictionary(g => g.Key, g => g.ToArray());
            var shopList = buildings.Where(b => (b.IsShop || shops.Contains(b)) && IsPublic(b)).ToArray();
            var stock = new Dictionary<TownBuilding, ulong>(ReferenceEqualityComparer.Instance);
            foreach (var b in shopList) stock[b] = StockOf(world, b, stocks.GetValueOrDefault(b));
            // The town's furniture to sit around at (chairs, thrones, dummies) and the faction owning each building: its property owner, else its level owner, else the town's.
            int townFaction = site < data.SiteFactions.Count ? data.SiteFactions[site] : -1;
            var seats = new List<SeatClaim>();
            var seatOwners = new Dictionary<TownBuilding, int>(ReferenceEqualityComparer.Instance);
            foreach (var b in buildings)
                for (int i = 0; i < b.SeatCount; i++)
                    if (Sittable(b.SeatAt(i).Kind, dummies: true))
                    {
                        seats.Add(new SeatClaim(b, i));
                        if (!seatOwners.ContainsKey(b))
                            seatOwners[b] = world.Fixtures.PropertyOwner(b.Key) ?? (b.OwnerFactionId is { } o && data.FactionIndex(o) is var fi and >= 0 ? fi : townFaction);
                    }
            places[site] = new(wander, shopList, [.. buildings.Where(b => (b.IsBar || bars.Contains(b)) && IsPublic(b))],
                [.. buildings.Where(b => b.IsOutdoorTable)], [.. buildings.Where(b => !b.Destroyed && b.Function is >= 0 and var fn && (WorkFunctions >> fn & 1) != 0)], stock, seats, seatOwners,
                [.. buildings.Where(b => b.HasInterior && !b.IsWall && !b.Destroyed && IsPublic(b))]);
        }
    }

    /// <summary>
    /// The item functions a shop has on sale, as a bit mask (goal 118 looks for a shop selling what it wants, <c>FUN_14054add0</c>): what its stock containers hold and what the stock of
    /// the squads living in it holds (a building shop's containers are merged into the squad's stock; a trader's carried stock is not looked at).
    /// </summary>
    ulong StockOf(World world, TownBuilding building, List<Trading.Shop>? held)
    {
        ulong mask = 0;
        void Add(IEnumerable<Items.ItemInstance> items)
        {
            foreach (var item in items)
                if (data.Items.Info(item.Record) is { } info) mask |= ShoppingList.Bit(info.Function);
        }
        foreach (var f in building.Fixtures)
            if (f.HoldsShopStock && world.Fixtures.Find(f.Id)?.Contents is { } box) Add(box.Items);
        if (held is not null)
            foreach (var shop in held) Add(shop.Stock.Items);
        return mask;
    }

    /// <summary>BuildingDesignation BD_BAR.</summary>
    const int DesignationBar = 3;

    /// <summary>The BuildingFunction bits a worker looks for (mask <c>0x41a</c> of <c>FUN_1408cac60</c> / <c>FUN_1408cadb0</c>, read as bits, <b>Observed</b>): MINE, RESEARCH, REFINERY, CRAFTING.</summary>
    const int WorkFunctions = 0x41a;

    /// <summary>How near a worker's building it stands (units; engine choice: the functionality's <c>use range</c> is not read for the town's buildings).</summary>
    const float WorkReach = 30;

    readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> workClips = new(StringComparer.Ordinal);

    /// <summary>The clip a worker plays at a building: its functionality's <c>animation</c>, else <c>stonemine</c> (<c>Task_OperateMachine</c>, docs/game/buildings-production.md).</summary>
    string WorkClipOf(TownBuilding b) => workClips.GetOrAdd(b.BuildingId, id =>
        data.Db.Find(id)?.GetReferences("functionality") is [var link, ..] && data.Db.Find(link.TargetStringId)?.GetReferences("animation") is [var anim, ..]
            && data.Db.Find(anim.TargetStringId) is { } clip ? clip.Name : "stonemine");

    readonly System.Collections.Concurrent.ConcurrentDictionary<string, float> workHungerRates = new(StringComparer.Ordinal);

    /// <summary>
    /// The hunger rate of a worker at a building: its functionality's <c>hunger rate</c> (1 without one), which <c>Task_OperateMachine</c>'s set-up (<c>FUN_14035b430</c>, <b>Verified</b>) writes
    /// to the operator's CharStats <c>+0x178</c> for the real (87) and the pretend (221) act-out alike.
    /// </summary>
    float WorkHungerRateOf(TownBuilding b) => workHungerRates.GetOrAdd(b.BuildingId, id =>
        data.Db.Find(id)?.GetReferences("functionality") is [var link, ..] && data.Db.Find(link.TargetStringId) is { } function ? function.GetFloat("hunger rate", 1) : 1);

    /// <summary>
    /// The building a worker works at (the finders of goals 189 / 190: a random building of the town with a function in <see cref="WorkFunctions"/>, slot 2 of the building finder
    /// <c>FUN_1409fb020</c>); the roll is the character's and its <paramref name="epoch"/> of picks (<see cref="AiMind.WorkEpoch"/>), so asking twice names the same one. Null: the town has none.
    /// </summary>
    TownBuilding? PickWork(World world, CharacterId self, int town, int epoch)
    {
        var list = Places(town).Workplaces;
        if (list.Count == 0) return null;
        return list[Rng.Int(Rng.Hash(world.Seed, Rng.Key(self), RngPurpose.Think, SeedSalts.AiTarget ^ (ulong)epoch << 20 ^ 0x57), list.Count)];
    }

    static bool IsGeneralNode(HomeSpot s) => s.NodeType == NodeGeneral;

    /// <summary>BUILDING <c>node type</c> NODE_GENERAL.</summary>
    const int NodeGeneral = 1;

    static ulong TargetRoll(World world, CharacterId self, AiBehaviour behaviour) =>
        Rng.Hash(world.Seed, Rng.Key(self), RngPurpose.Think, SeedSalts.AiTarget ^ (ulong)world.Tick << 8 ^ (ulong)behaviour);

    /// <summary>
    /// Where a town goal goes (docs/game/ai-tasks.md "Town goals"): wandering in town, a random general node of a random public building of the town in the walker's
    /// zone (Observed; the original draws from all of those and finds nothing in one without such a node, Meitou draws from those with one); shopping, a random shop of the town that sells what its list wants,
    /// else a random public building (<see cref="ShopFor"/>): a shop at a general node in it, else at it, any other building at its origin; the wanderer, the nearest town that is no nest marker. Null when there is none.
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
                var b = behaviour == AiBehaviour.TownNode ? PickFrom(WanderIn(town, world.Characters.Previous[self.Slot].Position), roll) : ShopFor(places, mind, roll, RandomShopping(world, town));
                if (b is null) return null;
                // A building without a counter is the target itself, its origin on any floor (FUN_1409997f0); the walk ends within WorkReach of it.
                if (behaviour == AiBehaviour.Shop && ShopFactor(places, b) < 1) return new Vector3(b.Position.X, float.NaN, b.Position.Z);
                // A general node; in a shop without one a guard post, else any other node (engine choice: where the original's Task_Shopping stands is not decoded).
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
                    // Every town type but a nest marker (the finder's type filter 10, FUN_140927f10: type 8 is taken only by a filter of exactly 8).
                    if (data.Sites[s].Town.Type == TownType.NestMarker) continue;
                    float d = Vector2.DistanceSquared(at, Flat(data.Sites[s].Position));
                    if (d < bestD) (best, bestD) = (s, d);
                }
                return best < 0 ? null : data.Sites[best].Position;
            }
        }
        return null;
    }

    // The desire and the target ask on every worker: one scratch list each.
    [ThreadStatic] static List<TownBuilding>? sellingScratch;

    static TownBuilding? PickFrom(IReadOnlyList<TownBuilding> from, ulong roll) => from.Count == 0 ? null : from[Rng.Int(roll, from.Count)];

    /// <summary>
    /// The shop goal 118 goes to (<c>FUN_1409997f0</c>, <b>Verified</b>): a random shop of the town selling an item function of the first entry on the character's list that has one,
    /// the entries taken in order and those already bought skipped (<c>FUN_1409fc070</c>); none sells one: a random public building of the town (<c>FUN_1409fc1b0</c>, <see cref="TownPlaces.Public"/>;
    /// not only a shop). The random pick is made only when <paramref name="randomAllowed"/> (<see cref="RandomShopping"/>): in a town of the player's
    /// faction only a shop selling what the list wants is found (the list finder runs before that test, <c>FUN_1409997f0</c>, <b>Verified</b>). The goal's factor is <see cref="ShopFactor"/>.
    /// </summary>
    static TownBuilding? ShopFor(TownPlaces places, AiMind mind, ulong roll, bool randomAllowed)
    {
        var shops = places.Shops;
        var selling = sellingScratch ??= [];
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

    /// <summary>The finder factor of a building the shopper picked (<c>FUN_1409997f0</c>, <b>Verified</b>): 1 for one with BF_SHOP furniture inside, 0.4 for any other public building.</summary>
    const float ShopNoCounterFactor = 0.4f;

    /// <summary>
    /// The factor of the pick: 1 for a shop (Meitou's <see cref="TownPlaces.Shops"/>: trading residents or the layout's shop flag, standing for BF_SHOP furniture, <b>Observed</b>), else
    /// <see cref="ShopNoCounterFactor"/>. It multiplies the goal's desire.
    /// </summary>
    static float ShopFactor(TownPlaces places, TownBuilding building)
    {
        foreach (var shop in places.Shops)
            if (ReferenceEquals(shop, building)) return 1;
        return ShopNoCounterFactor;
    }

    /// <summary>
    /// Whether the random pick of goal 118 runs in <paramref name="town"/>: its finder <c>FUN_1409fc1b0</c> is called only when the faction of the shopper's town has no player flag
    /// (<c>+0x250</c> clear, <c>FUN_1409997f0</c>, <b>Verified</b>), i.e. the town is not the player's faction's.
    /// </summary>
    bool RandomShopping(World world, int town) =>
        !(town >= 0 && town < data.SiteFactions.Count && data.SiteFactions[town] is >= 0 and var owner && owner == (world.Player.Faction >= 0 ? world.Player.Faction : data.PlayerFaction));

    bool AtHomeTown(World world, Squad squad, Blackboard board, CharacterId self)
    {
        if (board.Town < 0 || !world.Characters.IsAlive(self)) return true;
        var site = data.Sites[board.Town];
        var p = world.Characters.Previous[self.Slot].Position;
        float r = site.Town.Radius;
        return Vector2.DistanceSquared(new Vector2(p.X, p.Z), new Vector2(site.Position.X, site.Position.Z)) <= r * r;
    }

    /// <summary>Acts out a goal: sets the movement task it maps to, unless the character is already doing it.</summary>
    void Act(World world, int slot, AiMind mind, AiGoal goal, Blackboard board)
    {
        var table = world.Characters;
        var type = goal.Task.Type;
        var behaviour = TaskBehaviours.Of(type);
        var profile = TaskProfiles.Of(type);
        // A goal taken up anew starts over: a fresh target and, for a goal it commits to, a fresh commitment. So does a committed goal chosen again after its
        // commitment ran out (engine reading: the original's target finders roll a new target then, so the goal counts as new).
        bool fresh = mind.Goal is not { } before || before.Task.Type != type || before.Task.Target != goal.Task.Target
            || profile.Commits && world.GameHours >= mind.CommitUntil;
        if (fresh)
        {
            mind.Walking = false;
            mind.FailedWalks = 0;
            mind.Target = null;
            // A spot given up is tried again from wherever the character is now (engine choice: the walk may have failed from the old place only).
            mind.HomeUnreachable = false;
            if (type is not (TaskType.RELAX_IN_TOWN_PACKAGE or TaskType.SIT_AROUND)) mind.SeatUnreachable = false;
            if (profile.Commits)
            {
                ulong roll = Rng.Hash(world.Seed, Rng.Key(table.IdOf(slot)), RngPurpose.Think, SeedSalts.AiCommit ^ (ulong)world.Tick << 9 ^ (ulong)type);
                mind.CommitUntil = world.GameHours + profile.CommitMin + Rng.Float(roll) * profile.CommitRange;
            }
        }
        // Staying home sits on the chair its squad claimed for it (the original's 43 operates home furniture, chairs among it); without one it stands at home.
        if (type == TaskType.STAY_IN_HOME && board.SeatClaims.ContainsKey(slot) && !mind.SeatUnreachable) behaviour = AiBehaviour.Sit;
        if (behaviour == AiBehaviour.Investigate && (fresh || mind.Behaviour != AiBehaviour.Investigate)) mind.LookAbout = Crime.CrimeSystem.FirstLookAboutSeconds;
        mind.Goal = goal;
        mind.Behaviour = behaviour;
        mind.Version = board.Version;
        mind.WanderRadius = 0;
        mind.SitClip = null;   // set again below while it stays on its chair
        mind.SitHungerRate = 1;
        if (mind.WorkActive && behaviour != AiBehaviour.Work)
        {
            // It stopped working: the next pick is a new one and the work clip goes.
            mind.WorkActive = false;
            mind.WorkEpoch++;
            if (table.Cold(slot) is { } worker && mind.WorkClip is not null && worker.ActionClip == mind.WorkClip) worker.ActionClip = null;
            mind.WorkClip = null;
        }
        mind.FollowTarget = CharacterId.None;   // set again below for a follow goal aimed at the squad's mission
        ref var n = ref table.Next[slot];
        bool leader = mind.Squad.Leader == table.IdOf(slot);
        // Another goal gets it out of its bed (the get-up step's own think, or the goal's act-out where the plan has none); it stands where it lay.
        if (behaviour != AiBehaviour.Sleep && table.Cold(slot)!.InSomething == Locks.HeldState.Bed) world.Fixtures.LeaveBed(world, table.IdOf(slot));
        // Steps of the plan before the goal's own act-out: getting out of bed, sheathing (one think each); the walk and the rest are the goal's.
        switch (mind.Step)
        {
            case TaskType.GET_OUT_OF_BED or TaskType.GET_OUT_OF_BED_IF_ITS_EMERGENCY or TaskType.GET_OUT_OF_BED_ONCE_HEALED:
                Become(world, slot, CharacterTask.Idle);
                return;
            case TaskType.UNEQUIP_WEAPON:
                table.Cold(slot)!.DrawnWeapon = WeaponKinds.None;
                return;
            case TaskType.EQUIP_WEAPON or TaskType.EQUIP_CROSSBOW:
                // Task_EquipBestWeapon (6 melee only, 264 ranged only; FUN_140346c30, Verified): the matching weapon it carries comes out, else the hand is emptied.
                var armed = table.Cold(slot)!;
                if (armed.Fighter is { } fighter)
                    armed.DrawnWeapon = mind.Step == TaskType.EQUIP_WEAPON ? Combat.CombatSystem.MeleeDrawn(fighter) : fighter.Crossbow is not null ? BowHold : WeaponKinds.None;
                return;
            case TaskType.GO_HOMEBUILDING:
                // 19 Going home before the goal (43 Staying home and the like away from home): the walk home of the GoHome act-out.
                behaviour = AiBehaviour.GoHome;
                break;
            case TaskType.ENTER_BUILDING:
                // 156 Task_EnterBuilding: the walk into its home building (the GoHome walk), done once inside.
                behaviour = AiBehaviour.GoHome;
                break;
            case TaskType.LEAVE_BUILDING when mind.Squad.Ai?.Home is { } leaving && world.Sight.BuildingOf(slot) == leaving.Key:
                // 200 Task_LeaveBuilding: to 1.1 of the door's span past its outer point (A + (A - B) x 1.1) of the home's first door, until outside.
                if (LeavePoint(world, leaving) is { } outside && !GoToSpot(world, slot, mind, outside, GiveUp.Target, SpeedMode.Walk, slack: 10)) return;
                break;
            case TaskType.GET_NEAR_TO when !mind.Plan[0].Target.IsNone:
                // 40 Following to within 10 of the plan's target, then the goal's own act-out.
                if (!GoToSpot(world, slot, mind, Flat(mind.Plan[0].Target.Position), GiveUp.Target, SpeedMode.Walk, slack: 10)) return;
                break;
        }
        if (mind.Resume)
        {
            // Back from its town's time unloaded: it is already where the plan's walk ends (docs/simulation.md "Off-screen tier", residents' schedule).
            mind.Resume = false;
            if (mind.Step is TaskType.MOVE_ON_FREE_WILL or TaskType.MOVE_ON_FREE_WILL_FAST && !mind.Plan[0].Target.IsNone)
            {
                var end = mind.Plan[0].Target.Position;
                // On a ring round the spot by member (engine choice): members sharing a bed or node are not put on one point, where separation would throw them off it.
                float angle = mind.MemberIndex * 2.39996f;
                end += new Vector3(MathF.Sin(angle), 0, MathF.Cos(angle)) * ResumeRing;
                var walk = world.Walkability;
                var at = !float.IsNaN(end.Y) && walk.FloorNear(end.X, end.Z, end.Y, FloorSlack) is { } floor ? floor
                    : new Vector3(end.X, float.IsNaN(end.Y) ? walk.GroundHeight(end.X, end.Z) : end.Y, end.Z);
                // Only onto walkable ground: a spot it could not walk to either is left to the walk and its give-up rule. The serial step puts it
                // there (after the movement, which steps from the last tick's place), and it thinks again on the next tick, from the spot.
                if (walk.IsWalkable(at.X, at.Z, at.Y))
                {
                    mind.PlaceAt = at;
                    mind.Version = -1;
                    Interlocked.Exchange(ref placing, 1);
                    Become(world, slot, CharacterTask.Idle);
                    return;
                }
            }
        }
        switch (behaviour)
        {
            case AiBehaviour.Mill:
                // Round its post; at a borrowed home (the bar of a bar package) round that.
                mind.WanderCentre = board.Home is { } borrowed && borrowed != board.OwnHome ? Flat(borrowed.Position) : mind.Post;
                mind.WanderRadius = Settings.MillRadius;
                Become(world, slot, CharacterTask.Wander);
                break;
            case AiBehaviour.Follow:
                mind.FollowTarget = goal.Task.Target == TaskTargetType.SquadMission ? board.MissionTarget : CharacterId.None;
                Become(world, slot, CharacterTask.Follow);
                break;
            case AiBehaviour.Patrol:
                if (board.CurrentClass is PackageClass.Patrol or PackageClass.PatrolRunning && board.RouteIndex < board.Route.Length)
                {
                    if (!leader) Become(world, slot, CharacterTask.Follow);
                    else WalkTo(world, slot, board.Route[board.RouteIndex], board.CurrentClass == PackageClass.PatrolRunning ? SpeedMode.Run : SpeedMode.Walk);
                }
                else Become(world, slot, CharacterTask.Wander);
                break;
            case AiBehaviour.Sleep when table.Cold(slot)!.InSomething == Locks.HeldState.Bed:
                break;   // lying in its bed
            case AiBehaviour.Post:
            case AiBehaviour.Sleep:
            case AiBehaviour.GoHome:
                // Going to bed without a bed spot at home: an occupier's bed of the town it holds, else the NPC bed search.
                var spare = behaviour == AiBehaviour.Sleep && type is TaskType.GO_HOME_AND_GO_TO_BED or TaskType.GO_HOME_AND_GO_TO_BED_SECURE ? SpareBed(world, board, mind, slot) : null;
                var (target, home, floor, facing) = spare is not null ? (Flat(spare.Position), true, spare.Position.Y, (float?)null) : SpotFor(mind, board, behaviour, type);
                if (GoToSpot(world, slot, mind, target, home ? GiveUp.Home : GiveUp.Post, type is TaskType.RUN_AWAY_HOMETOWN or TaskType.TRAVEL_TO_TARGET_TOWN_FAST ? SpeedMode.Run : SpeedMode.Walk, height: floor))
                {
                    Become(world, slot, behaviour == AiBehaviour.Sleep ? CharacterTask.Rest : CharacterTask.Idle);
                    // At its home's bed: the serial step lies it in (Task_UseBed via 98 Sleeping); resting on the floor stays the fallback when no bed is free.
                    if (behaviour == AiBehaviour.Sleep && home && floor is not null && (spare?.Id ?? BedSpotOf(board, mind)) is { } bed)
                    {
                        mind.BedAt = bed;
                        Interlocked.Exchange(ref bedding, 1);
                    }
                    if (home && board.Gate is { } gate && GateSpot(board, mind, type) is not null) table.Next[slot].Yaw = MathF.Atan2(gate.Out.X, gate.Out.Y);   // looking out
                    else if (facing is { } yaw) table.Next[slot].Yaw = yaw;   // at a node, facing its way (the barman over his counter)
                }
                break;
            case AiBehaviour.TownNode:
            case AiBehaviour.Shop:
            case AiBehaviour.ToTown:
                // A shop picked is remembered, so the arrival knows what it sells (the same pick as the target's: the roll is this tick's).
                if (mind.Target is null && behaviour == AiBehaviour.Shop) mind.ShopAt = ShopFor(Places(board.Town), mind, TargetRoll(world, table.IdOf(slot), behaviour), RandomShopping(world, board.Town));
                mind.Target ??= TargetFor(world, table.IdOf(slot), behaviour, board.Town, mind) ?? (behaviour == AiBehaviour.TownNode ? OccupiedSpot(world, board, table.IdOf(slot)) : null);
                if (mind.Target is not { } goTo) Become(world, slot, CharacterTask.Idle);
                else if (GoToSpot(world, slot, mind, Flat(goTo), GiveUp.Target, SpeedMode.Walk,
                    float.IsNaN(goTo.Y) ? WorkReach : 0, behaviour == AiBehaviour.ToTown || float.IsNaN(goTo.Y) ? null : goTo.Y))   // a building without a counter: near its origin, on any floor
                {
                    Become(world, slot, CharacterTask.Idle);
                    // At the shop (Task_Shopping 119 starts): the first unit is bought a second on, then one a second (BuyForShoppers).
                    if (behaviour == AiBehaviour.Shop && mind.ShopAt is not null && mind.NextBuyTick == 0)
                    {
                        mind.NextBuyTick = world.Tick + BuyTicks(world);
                        Interlocked.Exchange(ref shopping, 1);
                    }
                }
                else if (behaviour == AiBehaviour.Shop) mind.NextBuyTick = 0;   // walking again: the task is not running
                break;
            case AiBehaviour.Work:
                if (mind.Target is null)
                {
                    if (mind.WorkActive) mind.WorkEpoch++;
                    mind.WorkActive = true;
                    if (PickWork(world, table.IdOf(slot), board.Town, mind.WorkEpoch) is { } building)
                    {
                        mind.Target = new Vector3(building.Position.X, float.NaN, building.Position.Z);
                        mind.WorkClip = WorkClipOf(building);
                        mind.WorkHungerRate = WorkHungerRateOf(building);
                    }
                }
                if (mind.Target is not { } working) Become(world, slot, CharacterTask.Idle);
                else if (GoToSpot(world, slot, mind, Flat(working), GiveUp.Target, SpeedMode.Walk, WorkReach))
                {
                    // At the building: facing it, at its work clip.
                    Become(world, slot, CharacterTask.Idle);
                    var face = working - n.Position;
                    if (face.X * face.X + face.Z * face.Z > 1e-4f) n.Yaw = MathF.Atan2(face.X, face.Z);
                    if (table.Cold(slot) is { } workerCold) workerCold.ActionClip = mind.WorkClip;
                }
                break;
            case AiBehaviour.Investigate:
            {
                // Task_InvestigateAlarms: within 100 of the alarm it stops and faces it (the serial step counts the look-about down); else it runs to 30 short of it, on its side.
                if (AlarmOf(world, slot, type, board) is not { } alarm) { Become(world, slot, CharacterTask.Idle); break; }
                var gap = n.Position - alarm.Position;
                if (gap.LengthSquared() <= Crime.CrimeSystem.AlarmLookRange * Crime.CrimeSystem.AlarmLookRange)
                {
                    if (mind.Walking) Become(world, slot, CharacterTask.Idle);
                    mind.Walking = false;
                    if (gap.X * gap.X + gap.Z * gap.Z > 1e-4f) n.Yaw = MathF.Atan2(-gap.X, -gap.Z);
                    break;
                }
                var side = Flat(gap);
                var spot = Flat(alarm.Position) + (side.LengthSquared() > 1e-4f ? Vector2.Normalize(side) : Vector2.UnitX) * Crime.CrimeSystem.AlarmApproach;
                GoToSpot(world, slot, mind, spot, GiveUp.Target, SpeedMode.Run);
                break;
            }
            case AiBehaviour.Assault:
                // Stands until the serial step sends the attack on the member picked (the combat system walks it there).
                Become(world, slot, CharacterTask.Idle);
                mind.AssaultTarget = AssaultTargetFor(world, slot, board);
                if (!mind.AssaultTarget.IsNone) Interlocked.Exchange(ref assaulting, 1);
                break;
            case AiBehaviour.Fight:
            case AiBehaviour.SelfPreserve when mind.Step != TaskType.RUN_AWAY:
                // Stands while the serial step starts the fight on the finder's pick (Self preservation's: row 13's attacker of itself; the combat system walks it there and fights); the
                // request is its own mind's.
                Become(world, slot, CharacterTask.Idle);
                AskFight(world, slot, type, mind);
                break;
            case AiBehaviour.SelfPreserve:
            case AiBehaviour.Flee:
                // Task_Runaway: the serial flee step takes the request, stops a fight and starts the first run (FleeSystem), then hops until it is safe.
                mind.FleeRequest = behaviour == AiBehaviour.SelfPreserve ? FleeAsk.Threat : FleeAsk.Enemies;
                break;
            case AiBehaviour.Travel:
                // To the mission town's centre; the goal is no longer wanted once inside the town (Desire), long before the centre.
                if (DestinationCentre(world, board) is not { } town) Become(world, slot, CharacterTask.Idle);
                else if (GoToSpot(world, slot, mind, town, GiveUp.Target, type == TaskType.TRAVEL_TO_TARGET_TOWN_FAST ? SpeedMode.Run : SpeedMode.Walk))
                    Become(world, slot, CharacterTask.Idle);
                break;
            case AiBehaviour.Relax:
            case AiBehaviour.Sit:
                // At its claimed seat (a chair or throne, else a node; a training dummy is stood at).
                HomeSpot claimed = default;
                bool claim;
                if (type == TaskType.SIT_AROUND)
                {
                    claim = board.SitClaims.TryGetValue(slot, out var sitting);
                    if (claim) claimed = sitting.Spot;
                }
                else claim = board.SeatClaims.TryGetValue(slot, out claimed);
                if (claim)
                {
                    bool chair = claimed.Kind is HomeSpotKind.Chair or HomeSpotKind.ThroneSeat;
                    // Already on its chair (moved onto it below, off the floor point the walk ended at).
                    bool seated = chair && n.Task == (byte)CharacterTask.Rest && !mind.Walking
                        && Vector2.DistanceSquared(Flat(n.Position), Flat(claimed.Position)) < 1 && MathF.Abs(n.Position.Y - claimed.Position.Y) <= FloorSlack;
                    if (seated || GoToSpot(world, slot, mind, Flat(claimed.Position), GiveUp.Seat, SpeedMode.Walk, height: claimed.Position.Y))
                    {
                        if (chair)
                        {
                            // On the seat's origin, with no offset, facing its way (docs/game/ai-tasks.md "Chairs"): the sitting clip carries the pose.
                            n.Position = claimed.Position;
                            n.Yaw = world.BenchFacingFix ? claimed.Yaw : claimed.FaithfulYaw;
                            mind.SitClip = claimed.SitClip;
                            mind.SitHungerRate = claimed.HungerRate;
                        }
                        else if (claimed.Kind == HomeSpotKind.Training) n.Yaw = claimed.Yaw;
                        Become(world, slot, claimed.Kind == HomeSpotKind.Training ? CharacterTask.Idle : CharacterTask.Rest);
                    }
                }
                break;
            case AiBehaviour.Turret:
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
                break;
        }
    }

    /// <summary>
    /// Where a goal is acted out: a node of the home building by goal (shopkeeper, guard, throne, general; any node when the home has none of that kind), a bed for
    /// sleep (the home itself when it has no bed), the home or the town for going home; else (no home, or its spots kept failing) the character's post. True in
    /// <c>Home</c> when the spot is the home's; <c>Floor</c> is the height of a node or bed (so the walk ends on the floor it is on, not on the ground below);
    /// <c>Yaw</c> the way a node faces, which the character turns to on arriving.
    /// </summary>
    (Vector2 Target, bool Home, float? Floor, float? Yaw) SpotFor(AiMind mind, Blackboard board, AiBehaviour behaviour, TaskType type)
    {
        if (behaviour == AiBehaviour.GoHome)
        {
            if (board.Home is { } h && !mind.HomeUnreachable) return (Flat(h.Position), true, null, null);
            if (board.Town >= 0) return (Flat(data.Sites[board.Town].Position), true, null, null);
            return (mind.Squad.HomeCentre, true, null, null);
        }
        if (!mind.HomeUnreachable && GateSpot(board, mind, type) is { } guard) return (Flat(guard), true, guard.Y, null);
        if (board.Home is not { } home || mind.HomeUnreachable || type is TaskType.HOLD_POSITION or TaskType.MAN_THE_GATE) return (mind.Post, false, null, null);
        HomeSpotKind kind = behaviour == AiBehaviour.Sleep ? HomeSpotKind.Bed : type switch
        {
            TaskType.STAND_AT_SHOPKEEPER_NODE => HomeSpotKind.Shopkeeper,
            TaskType.STAND_AT_GUARD_NODE_HOMEBUILDING or TaskType.STAND_AT_GUARD_NODE_HOMEBUILDING_INDOORS_ONLY or TaskType.STAND_AT_GUARD_NODE_HOMETOWN_OUTSIDE => HomeSpotKind.Guard,
            TaskType.SIT_ON_THRONE => HomeSpotKind.Throne,
            _ => HomeSpotKind.General,
        };
        if (Pick(home, kind, mind.MemberIndex) is { } spot) return (Flat(spot.Position), true, spot.Position.Y, spot.IsNode ? spot.Yaw : null);
        // Guard posts fall back to general nodes (docs/game/ai-tasks.md "Town goals"); the counter is the shopkeeper's, so only its goal falls back onto it.
        if (kind == HomeSpotKind.Guard && Pick(home, HomeSpotKind.General, mind.MemberIndex) is { } general)
            return (Flat(general.Position), true, general.Position.Y, general.Yaw);
        if (kind != HomeSpotKind.Bed && Pick(home, null, mind.MemberIndex, kind != HomeSpotKind.Shopkeeper) is { } any) return (Flat(any.Position), true, any.Position.Y, any.Yaw);
        // No bed: it sleeps in the house; a node goal without nodes stays at its own post, except staying home.
        if (kind == HomeSpotKind.Bed || type == TaskType.STAY_IN_HOME) return (Flat(home.Position), true, null, null);
        return (mind.Post, false, null, null);
    }

    /// <summary>
    /// The <paramref name="index"/>-th spot of a kind (null: any node, <see cref="HomeSpot.IsNode"/>, shopkeeper nodes left out with <paramref name="notCounter"/>) of a
    /// building, round robin; null when it has none.
    /// </summary>
    static HomeSpot? Pick(TownBuilding home, HomeSpotKind? kind, int index, bool notCounter = false)
    {
        bool Takes(HomeSpot s) => kind is { } k ? s.Kind == k : s.IsNode && !(notCounter && s.Kind == HomeSpotKind.Shopkeeper);
        int count = 0;
        foreach (var s in home.Spots) if (Takes(s)) count++;
        if (count == 0) return null;
        int want = index % count;
        foreach (var s in home.Spots)
            if (Takes(s) && want-- == 0) return s;
        return null;
    }

    /// <summary>The fixture id of the bed <see cref="SpotFor"/> sends a sleeper to (the <see cref="Pick"/> of its home's beds, numbered as <c>FixtureRegistry</c> numbers them); null without one.</summary>
    static string? BedSpotOf(Blackboard board, AiMind mind)
    {
        if (board.Home is not { } home || mind.HomeUnreachable) return null;
        int count = 0;
        foreach (var s in home.Spots) if (s.Kind == HomeSpotKind.Bed) count++;
        return count == 0 ? null : $"{home.Key}@bed{(mind.MemberIndex % count).ToString(System.Globalization.CultureInfo.InvariantCulture)}";
    }

    /// <summary>
    /// The bed a sleeper whose home has no bed spot goes to (153/239 Go home go to bed, docs/game/locks.md "Bed search, other characters"): an occupier's bed of the
    /// town it holds, else the NPC bed search (a free bed of its home building, else of its town). A squad without a town takes only its own or an ally's beds within
    /// <see cref="Locks.FixtureRegistry.BedSearchRange"/>, standing in for the original's allied BF_BED buildings in the zones around it (engine choice for the range).
    /// Null for a home with bed spots (<see cref="SpotFor"/> picks one) or when no bed is found.
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
            || f.Owner != self.Faction && !(f.Owner >= 0 && relations.Contains(f.Owner) && relations.Contains(self.Faction) && relations.IsAlly(f.Owner, self.Faction)));
    }

    /// <summary>Whether a sleeper has a bed to go to: lying in one, a bed spot of its home, or a <see cref="SpareBed"/>.</summary>
    bool HasBed(World world, Blackboard board, AiMind mind, int slot) =>
        world.Characters.Cold(slot)?.InSomething == Locks.HeldState.Bed
        || board.Home is not null && !mind.HomeUnreachable && BedSpotOf(board, mind) is not null
        || SpareBed(world, board, mind, slot) is not null;

    /// <summary>The hand hold of a drawn crossbow.</summary>
    static readonly WeaponKinds BowHold = HandHold.OfCategory((int)Meitou.Data.Gameplay.Combat.WeaponCategory.Bow).Weapon;

    static Vector2 Flat(Vector3 p) => new(p.X, p.Z);

    /// <summary>
    /// Where a turret is manned: behind its gun (the gunner stands between the gun and the walkway behind it), inside the turret's <c>use range</c>: the first of a
    /// few distances behind it whose ground is walkable at the turret's height, else beside or before it, else the turret's own place. The Y is the walkable surface
    /// there, so the path goes up to the tower's floor rather than to the ground below it. Engine choice: the game's gunner spot is not in the data.
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

    /// <summary>
    /// Walks the character to a spot; true when it is there (and not walking). A walk that ends away from the spot counts as failed; after
    /// <see cref="MaxFailedWalks"/> in a row it gives the spot up as <paramref name="giveUp"/> says. <paramref name="slack"/> overrides
    /// <see cref="AiSettings.PostSlack"/> where the spot must be exact (a turret's gun); <paramref name="height"/> is the floor the spot is on, so the path reaches a
    /// floor or walkway above the ground; a spot over a hole furniture cuts in that floor is walked to beside it, on the floor (<see cref="IWalkability.FloorNear"/>).
    /// </summary>
    /// <summary>
    /// Where 200 Task_LeaveBuilding walks (docs/game/ai-tasks.md): A + (A − B) × 1.1 of the building's first door with a known leaf, A and B its points 15 either side of the leaf
    /// (engine reading of which is outer: the side away from the building's origin). Null: no door known.
    /// </summary>
    static Vector2? LeavePoint(World world, TownBuilding building)
    {
        foreach (var door in world.Fixtures.DoorsByBuilding[building.Key])
        {
            if (door.Spot is not { } s) continue;
            var centre = new Vector2(s.Centre.X, s.Centre.Z);
            var outward = new Vector2(s.Normal.X, s.Normal.Z);
            if (Vector2.Dot(outward, centre - new Vector2(building.Position.X, building.Position.Z)) < 0) outward = -outward;
            Vector2 a = centre + outward * 15, b = centre - outward * 15;
            return a + (a - b) * 1.1f;
        }
        return null;
    }

    bool GoToSpot(World world, int slot, AiMind mind, Vector2 target, GiveUp giveUp, byte mode, float slack = 0, float? height = null)
    {
        var walk = world.Walkability;
        var table = world.Characters;
        if (height is { } spotY && walk.FloorNear(target.X, target.Y, spotY, FloorSlack) is { } onFloor) (target, height) = (Flat(onFloor), onFloor.Y);
        ref var n = ref table.Next[slot];
        var here = new Vector2(n.Position.X, n.Position.Z);
        float within = slack > 0 ? slack : Settings.PostSlack;
        // A spot on a floor counts as reached on that floor only, not on the ground below it.
        bool away = Vector2.DistanceSquared(here, target) > within * within || height is { } y && MathF.Abs(n.Position.Y - y) > FloorSlack;
        if (mind.Walking && n.Task != (byte)CharacterTask.GoTo)
        {
            // The walk is over: arrived, or it ended elsewhere (no way there, or a fight took it).
            mind.Walking = false;
            mind.FailedWalks = away ? mind.FailedWalks + 1 : 0;
            if (mind.FailedWalks >= MaxFailedWalks)
            {
                mind.FailedWalks = 0;
                switch (giveUp)
                {
                    case GiveUp.Home: mind.HomeUnreachable = true; break;
                    case GiveUp.Post: mind.Post = here; break;
                    case GiveUp.Target: mind.Target = null; break;
                    case GiveUp.Seat: mind.SeatUnreachable = true; break;
                }
                return false;   // the next think picks the new spot
            }
        }
        if (away)
        {
            WalkTo(world, slot, target, mode);
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

    // Set by a think that left a resident to be placed (<see cref="AiMind.PlaceAt"/>); the serial step looks for them only then.
    int placing;

    /// <summary>Puts the residents back from their town's time unloaded on the spots their thinks chose (serial, after the movement).</summary>
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

    // Set by a think that left a sleeper to be laid in its bed (<see cref="AiMind.BedAt"/>).
    int bedding;

    /// <summary>How far (X/Z) from where it stands a sleeper takes another free bed of its home when its own is taken (units; engine choice).</summary>
    const float BedReach = 30;

    /// <summary>
    /// Lies the sleepers that reached their bed spot in it (serial: beds are shared). A taken bed gives way to the nearest free bed of the same building within
    /// <see cref="BedReach"/>; with none free it keeps resting on the floor there (engine choice).
    /// </summary>
    static void LieDown(World world)
    {
        var table = world.Characters;
        for (int s = 0; s < table.HighWater; s++)
        {
            if (table.Cold(s) is not { Ai: { BedAt: { } id } mind } cold) continue;
            mind.BedAt = null;
            ref var n = ref table.Next[s];
            if (!n.Alive || mind.Behaviour != AiBehaviour.Sleep || n.Task != (byte)CharacterTask.Rest || world.Fixtures.Find(id) is not { } bed) continue;
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

    /// <summary>What a character gives up when its walks to a spot keep failing: its home's spots (it uses its post), its post (where it stands becomes it), the target, or relaxing at its bar seat.</summary>
    enum GiveUp : byte { Home, Post, Target, Seat }

    /// <summary>How far above or below a spot's floor a character may stand and still be at it (units).</summary>
    const float FloorSlack = 6;

    /// <summary>The radius of the ring a resident back from its town's time unloaded is put on round its spot (units; half a human's separation radius).</summary>
    const float ResumeRing = 4;

    /// <summary>
    /// Switches to a standing task (wander, follow, idle, rest), dropping a walk in progress; nothing when it is doing it already. A goal's start on the character's own slot
    /// (<see cref="TaskStart.StartOwn"/>: the think runs in parallel), as the row of the goal it acts out.
    /// </summary>
    static void Become(World world, int slot, CharacterTask task)
    {
        if (world.Characters.Next[slot].Task == (byte)task) return;
        TaskStart.StartOwn(world, slot, new TaskRequest(task, GoalRow(world, slot), TaskSource.Goal));
    }

    /// <summary>Sends it walking (or running) to a point, unless it is going there already (<see cref="TaskStart.StartOwn"/>, as <see cref="Become"/>).</summary>
    static void WalkTo(World world, int slot, Vector2 to, byte mode)
    {
        ref var n = ref world.Characters.Next[slot];
        if (n.Task == (byte)CharacterTask.GoTo && Vector2.DistanceSquared(n.Goal, to) < 1) { n.Mode = mode; return; }
        TaskStart.StartOwn(world, slot, new TaskRequest(CharacterTask.GoTo, GoalRow(world, slot), TaskSource.Goal) { Goal = to, Mode = mode });
    }

    /// <summary>The row of the goal the character acts out (14 Idleness without one).</summary>
    static TaskType GoalRow(World world, int slot) => world.Characters.Cold(slot)?.Ai?.Goal?.Task.Type ?? TaskType.IDLE;

    // ------------------------------------------------------------------ serial steps

    public void Inputs(World world, IReadOnlyList<SimCommand> commands)
    {
        foreach (var command in commands)
        {
            var (squadId, packageId) = command switch { StartContract s => (s.SquadId, s.PackageId), ChangeAi a => (a.SquadId, a.PackageId), _ => (-1, "") };
            if (world.Squads.Find(squadId) is not { } squad || data.Ai.Package(packageId) is not { } package) continue;
            if (squad.Ai is null)
            {
                if (IsPlayers(world, squad)) continue;
                Attach(world, squad);
            }
            rules.Bind(this, world, squad);
            if (command is StartContract c) squad.Ai!.StartContract(package, world.GameHours, c.Hours, rules, c.Target);
            else squad.Ai!.Replace(package, rules);
        }
    }

    public void SlowWorld(World world)
    {
        var table = world.Characters;
        if (Interlocked.Exchange(ref placing, 0) != 0) Place(table);
        if (Interlocked.Exchange(ref bedding, 0) != 0) LieDown(world);
        if (Interlocked.Exchange(ref shopping, 0) != 0) BuyForShoppers(world);
        combat ??= world.SystemOf<Combat.CombatSystem>();
        aggro ??= world.SystemOf<AggroSystem>();
        if (Interlocked.Exchange(ref assaulting, 0) != 0) SendAssaults(world);
        if (Interlocked.Exchange(ref fighting, 0) != 0) SendFights(world);
        if (Interlocked.Exchange(ref leaving, 0) != 0) LeaveFights(world);
        bool cadence = world.Tick % Math.Max(1, Settings.PackageEveryTicks) == 0;
        // Each town's turrets are claimed once per cadence, by the squads in squad order (derived, not hashed).
        if (cadence) { turretTaken.Clear(); seatTaken.Clear(); ReserveSeats(world); RefreshPlaces(world); }
        foreach (var squad in world.Squads.All)
        {
            if (squad.Ai is null)
            {
                bool first = looked.Add(squad.Id);
                if (!first || !Runs(world, squad))
                {
                    if (first || cadence) EngineMinds(world, squad);
                    continue;
                }
                Attach(world, squad);
                if (squad.Ai!.Town >= 0 && !places.ContainsKey(squad.Ai.Town)) RefreshPlaces(world);
            }
            else if (!cadence) continue;   // the rest is looked at on the package cadence only (and when the squad appears)
            var board = squad.Ai!;
            board.Intact = IsIntact(world, squad, board);
            UpdateInTown(world, squad, board);
            rules.Bind(this, world, squad);
            board.Update(rules, world.GameHours);
            StepPackage(world, squad, board);
            // A contract that ended: its leader says the package's contract end talk to the employer (event 30, FUN_14026a110).
            if (board.TakeEnded() is { Package.ContractEndTalk: { } talk, Target: var employer } && !squad.Leader.IsNone)
                world.Defer(new Dialogue.DialogueEvent(squad.Leader, employer, Dialogue.DialogueSystem.ContractJobEnded, talk) { Tick = world.Tick + 1 });
            AdvanceRoute(world, squad, board);
            board.Crowd.Clear();
            board.CrowdEntries.Clear();
            for (int k = 0; k < squad.Members.Count; k++)
            {
                if (!table.TryResolveNext(squad.Members[k], out int s)) continue;
                var cold = table.Cold(s)!;
                if (cold.Ai is null or { Engine: true })
                {
                    var p = table.Next[s].Position;
                    cold.Ai = new AiMind
                    {
                        Squad = squad, MemberIndex = k, Post = OccupierPost(world, board, k) ?? new Vector2(p.X, p.Z), RaceGoals = cold.Race is { } race ? data.Ai.RaceGoals(race.StringId) : [], Resume = squad.Resume,
                        Shopping = ShoppingList.Make(data.Db, cold.RecordId, world.Seed, Rng.Key(table.IdOf(s))),
                    };
                }
                if (cold.Ai.Goal is { } g)
                {
                    board.Crowd[g.Task.Type] = board.Crowd.GetValueOrDefault(g.Task.Type) + 1;
                    board.CrowdEntries.Add(new CrowdEntry(squad.Members[k], g.Task.Type, CrowdTarget(g.Task.Target, squad, board, squad.Members[k])));
                }
            }
            squad.Resume = false;
            ClaimTurrets(world, board, squad);
            ClaimSeats(world, board, squad);
            ClaimSitting(world, board, squad);
            FindGate(world, board, squad);
        }
        if (merges.Count > 0) ApplyMerges(world);
    }

    /// <summary>
    /// Sets <see cref="Blackboard.Gate"/>: the home when it is a gate, else, for a squad with a gate guard, the town's gate nearest the squad (the original's
    /// <c>HOME_GATE</c> chain, docs/game/ai-tasks.md "Town goals"); its first door's leaf, from the door gate (none headless). Kept while the gate stays the same.
    /// </summary>
    void FindGate(World world, Blackboard board, Squad squad)
    {
        TownBuilding? gate = board.Home is { IsGate: true } home ? home : null;
        if (gate is null && board.Crowd.ContainsKey(TaskType.MAN_THE_GATE) && board.Town >= 0)
        {
            float best = float.MaxValue;
            foreach (var b in data.Sites[board.Town].Buildings)
            {
                float d = Vector2.DistanceSquared(Flat(b.Position), Flat(squad.Position));
                if (b.IsGate && d < best) (gate, best) = (b, d);
            }
        }
        if (gate is null) { board.Gate = null; return; }
        if (board.Gate?.Building == gate) return;
        board.Gate = PostOf(world, gate, board.Town >= 0 ? Flat(data.Sites[board.Town].Position) : Flat(gate.Position));
    }

    /// <summary>The post of <paramref name="gate"/> (see <see cref="FindGate"/>) for a town centred at <paramref name="town"/>; null: no door, or its leaf's place not known.</summary>
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

    /// <summary>Where a gate's gate guard stands: <see cref="GateGuardOut"/> out from its leaf.</summary>
    internal static Vector3 FrontOf(GatePost gate) => gate.Door + new Vector3(gate.Out.X, 0, gate.Out.Y) * GateGuardOut;

    /// <summary>How far from a gate's leaf its gate guard stands (Observed, <c>Task_ManTheGate</c>: the door's point 15 along its normal).</summary>
    const float GateGuardOut = 15;

    /// <summary>
    /// Where a gate's guards stand: the gate guard 15 out from its leaf; a member whose node goal finds no node in a gate it lives in flanks the opening on that side,
    /// beyond the leaf's edges, alternately left and right in two rows (engine choice: the original's goal finds nothing there). Null for other goals.
    /// </summary>
    static Vector3? GateSpot(Blackboard board, AiMind mind, TaskType type)
    {
        if (board.Gate is not { } gate) return null;
        var front = FrontOf(gate);
        if (type == TaskType.MAN_THE_GATE) return front;
        if (type is not (TaskType.STAND_AT_GUARD_NODE_HOMEBUILDING or TaskType.STAND_AT_GUARD_NODE_HOMEBUILDING_INDOORS_ONLY or TaskType.STAND_AT_GUARD_NODE_HOMETOWN_OUTSIDE
            or TaskType.STAND_AT_GENERAL_NODE) || board.Home != gate.Building) return null;
        foreach (var s in gate.Building.Spots) if (s.IsNode) return null;
        int step = mind.MemberIndex / 2;
        float side = (mind.MemberIndex & 1) == 0 ? 1 : -1;
        var along = new Vector3(-gate.Out.Y, 0, gate.Out.X) * side * (gate.HalfWidth + 6 + 7 * (step / 2));
        return front + new Vector3(gate.Out.X, 0, gate.Out.Y) * (8 * (step % 2)) + along;
    }

    /// <summary>
    /// Gives the members' slots the town's turrets to man (docs/simulation.md "AI", turrets): in member order each takes the nearest turret no earlier member claimed,
    /// by the distance to its post; a member whose goal is the home building's turrets first takes one mounted on its home. Rebuilt on the package cadence; a town
    /// without turrets gives none.
    /// </summary>
    void ClaimTurrets(World world, Blackboard board, Squad squad)
    {
        board.Turrets = data.Turrets(board.Town);
        board.TurretClaims.Clear();
        if (board.Turrets.Count == 0) return;
        if (!turretTaken.TryGetValue(board.Town, out var taken)) turretTaken[board.Town] = taken = [];
        int hour = world.Hour, claimed = 0;
        for (int pass = 0; pass < 2 && claimed < board.Turrets.Count; pass++)
            for (int k = 0; k < squad.Members.Count && claimed < board.Turrets.Count; k++)
            {
                var id = squad.Members[k];
                if (!world.Characters.TryResolveNext(id, out int s) || world.Characters.Cold(s) is not { } cold || cold.Ai is not { } mind) continue;
                if (board.TurretClaims.ContainsKey(s) || !TurretGoal(board, squad, id, cold, hour, out bool homeOnly)) continue;
                if (pass == 0 ? !homeOnly : homeOnly) continue;
                int best = -1;
                float bestD = float.MaxValue;
                for (int t = 0; t < board.Turrets.Count; t++)
                {
                    if (taken.Contains(t) || board.Turrets[t].Turret is not { } turret) continue;
                    if (homeOnly && turret.MountedOn != board.Home?.Key) continue;
                    float d = Vector2.DistanceSquared(mind.Post, Flat(turret.Position));
                    if (d < bestD) (best, bestD) = (t, d);
                }
                if (best >= 0) { taken.Add(best); board.TurretClaims[s] = best; claimed++; }
            }
    }

    /// <summary>
    /// Gives the members whose role list has relaxing (under a bar package), sitting around or staying home open at this hour a seat of their <see cref="Blackboard.Home"/> (the
    /// package's bar under a bar package): in member order each takes the first seat no one in that building claimed in this cadence pass; members left without one
    /// do not relax or sit. Relaxing takes a chair, else a bar node, else a general node (the original operates furniture of the bar, docs/game/ai-tasks.md "Town
    /// goals", Observed; the node fallbacks are an engine choice); staying home a chair only (the original's furniture not claimed by others, Observed; its mask also holds
    /// training furniture, which Meitou leaves out). Sitting around (275) claims in <see cref="ClaimSitting"/>. A member keeps the seat it had while it still relaxes or stays (engine choice:
    /// reassigning every pass in member order moved every later sitter to another chair whenever one came or went); the seats kept, and those a character sits on by an order (the player's Use), are reserved for every squad first (<see cref="ReserveSeats"/>).
    /// </summary>
    void ClaimSeats(World world, Blackboard board, Squad squad)
    {
        kept.Clear();
        foreach (var (slot, spot) in board.SeatClaims) kept[slot] = spot;
        board.SeatClaims.Clear();
        if (board.Home is not { } home) return;
        bool bar = board.BarTable >= 0;
        // The squad's table: members sit nearest it (engine choice); an outdoor table has no seats, they stand at it.
        Vector3? near = bar ? board.BarTable < home.Tables.Count ? home.Tables[board.BarTable] : home.Position : null;
        HashSet<int>? taken = null;
        int hour = world.Hour;
        for (int k = 0; k < squad.Members.Count; k++)
        {
            var id = squad.Members[k];
            if (!world.Characters.TryResolveNext(id, out int s) || world.Characters.Cold(s) is not { } cold || cold.Ai is null) continue;
            var roles = board.Roles[(int)RoleOf(squad, id, cold)];
            bool relaxes = false, sits = false;
            for (int g = 0; g < roles.Count; g++)
            {
                if (!roles[g].InWindow(hour)) continue;
                relaxes |= bar && roles[g].Task.Type == TaskType.RELAX_IN_TOWN_PACKAGE;
                sits |= roles[g].Task.Type == TaskType.STAY_IN_HOME;
            }
            if (taken is null && !seatTaken.TryGetValue(home, out taken)) seatTaken[home] = taken = [];
            int had = kept.TryGetValue(s, out var old) ? IndexOf(home, old) : -1;
            if (had >= 0 && Operated(world, home.Spots[had].Position)) had = -1;
            if (!relaxes && !sits || cold.Ai.SeatUnreachable)   // one that gave its seat up leaves it to the next member
            {
                if (had >= 0) taken.Remove(had);
                continue;
            }
            if (had >= 0 && (relaxes || old.Kind == HomeSpotKind.Chair))
            {
                taken.Add(had);
                board.SeatClaims[s] = old;
                continue;
            }
            int seat = FreeSeat(home, taken, HomeSpotKind.Chair, near);
            if (seat < 0 && relaxes) seat = FreeSeat(home, taken, HomeSpotKind.Bar, near);
            if (seat < 0 && relaxes) seat = FreeSeat(home, taken, HomeSpotKind.General, near);
            if (seat < 0 && relaxes && home.IsOutdoorTable) board.SeatClaims[s] = new HomeSpot(HomeSpotKind.General, home.Position, home.Yaw);
            if (seat < 0) continue;
            taken.Add(seat);
            board.SeatClaims[s] = home.Spots[seat];
        }
    }

    readonly Dictionary<int, HomeSpot> kept = [];

    /// <summary>Marks taken the chairs and thrones of <paramref name="home"/> a character sits on by an order (the player's Use, <see cref="CharacterTask.Operate"/>), so no member claims them.</summary>
    static void TakeOperatedSeats(World world, TownBuilding home, HashSet<int> taken)
    {
        for (int i = 0; i < home.Spots.Count; i++)
            if (home.Spots[i].Kind is HomeSpotKind.Chair or HomeSpotKind.ThroneSeat && !taken.Contains(i) && Operated(world, home.Spots[i].Position)) taken.Add(i);
    }

    struct OperatorScan { public CharacterTable Table; public Vector3 At; public bool Found; }

    /// <summary>Whether a character working a machine (<see cref="CharacterTask.Operate"/>) is on the spot, as of the last tick.</summary>
    static bool Operated(World world, Vector3 at)
    {
        var scan = new OperatorScan { Table = world.Characters, At = at };
        world.Grid.Query(at.X, at.Z, 1, ref scan, static (int other, ref OperatorScan st) =>
        {
            var prev = st.Table.Previous;
            if (st.Found || (uint)other >= (uint)prev.Length || !prev[other].Alive || prev[other].Task != (byte)CharacterTask.Operate) return;
            st.Found = Vector2.DistanceSquared(Flat(prev[other].Position), Flat(st.At)) < 1 && MathF.Abs(prev[other].Position.Y - st.At.Y) <= FloorSlack;
        });
        return scan.Found;
    }
    readonly Dictionary<int, SeatClaim> keptSits = [];

    /// <summary>The seats of <paramref name="building"/> taken in this cadence pass (indices of <see cref="TownBuilding.SeatAt"/>).</summary>
    HashSet<int> TakenOf(TownBuilding building) => seatTaken.TryGetValue(building, out var taken) ? taken : seatTaken[building] = [];

    /// <summary>
    /// The sitting-around (275) counterpart of <see cref="ClaimSeats"/> (the finder <c>FUN_1408caf00</c>, docs/game/ai-tasks.md "Town goals"): a member whose role list has 275 open at this hour
    /// takes a piece of furniture of the mask 0x1020080 that no one claimed in this cadence pass, in the town it stands in (<see cref="TownPlaces.Seats"/>; the home need not be a part of it):
    /// a chair or throne, and a training dummy unless the character is on the player's side (the mask 0x1020000 of <c>FUN_140856bc0</c>; an AI-run squad is never the player's, so this only
    /// spares a recruit). The town must be the squad's home town or owned by the squad's faction, a player-side character's a town of the player's faction (<see cref="MaySitIn"/>), and the
    /// piece's building owned by the character's faction or the squad's (<see cref="TownPlaces.SeatOwners"/>); outside every town it finds nothing. The nearest free piece is an
    /// engine choice (the original takes the first of its zone map). A member keeps its piece while it still sits around and may sit there, as in <see cref="ClaimSeats"/>.
    /// </summary>
    void ClaimSitting(World world, Blackboard board, Squad squad)
    {
        keptSits.Clear();
        foreach (var (slot, claim) in board.SitClaims) keptSits[slot] = claim;
        board.SitClaims.Clear();
        int hour = world.Hour;
        for (int k = 0; k < squad.Members.Count; k++)
        {
            var id = squad.Members[k];
            if (!world.Characters.TryResolveNext(id, out int s) || world.Characters.Cold(s) is not { } cold || cold.Ai is null) continue;
            var roles = board.Roles[(int)RoleOf(squad, id, cold)];
            bool sits = false;
            for (int g = 0; g < roles.Count && !sits; g++)
                sits = roles[g].Task.Type == TaskType.SIT_AROUND && roles[g].InWindow(hour);
            bool had = keptSits.TryGetValue(s, out var old);
            if (!sits || cold.Ai.SeatUnreachable)   // one that gave its seat up leaves it to the next member
            {
                if (had) TakenOf(old.Building).Remove(old.Index);
                continue;
            }
            bool playerSide = PlayerSide(world, cold);
            var at = world.Characters.Next[s].Position;
            int site = TownAt(at);
            var seats = site >= 0 && MaySitIn(world, board, squad, site, playerSide) ? Places(site) : NoPlaces;
            bool Mine(TownBuilding b) => seats.SeatOwners.TryGetValue(b, out int owner) && owner >= 0 && (owner == cold.Faction || owner == squad.Faction);
            if (had && Mine(old.Building) && Sittable(old.Spot.Kind, !playerSide))
            {
                TakenOf(old.Building).Add(old.Index);
                board.SitClaims[s] = old;
                continue;
            }
            if (had) TakenOf(old.Building).Remove(old.Index);
            int best = -1;
            float bestD = float.MaxValue;
            for (int i = 0; i < seats.Seats.Count; i++)
            {
                var seat = seats.Seats[i];
                if (!Sittable(seat.Spot.Kind, !playerSide) || !Mine(seat.Building) || seatTaken.TryGetValue(seat.Building, out var taken) && taken.Contains(seat.Index)) continue;
                float d = Vector3.DistanceSquared(seat.Spot.Position, at);
                if (d < bestD) (best, bestD) = (i, d);
            }
            if (best < 0) continue;
            var found = seats.Seats[best];
            TakenOf(found.Building).Add(found.Index);
            board.SitClaims[s] = found;
        }
    }

    /// <summary>
    /// Whether a member standing in town <paramref name="site"/> may sit around there (the town gate of <c>FUN_1408caf00</c>): a non-player character needs the squad's home town or a town owned
    /// by the squad's faction, a player-side one a town owned by the player's faction (no home test).
    /// </summary>
    bool MaySitIn(World world, Blackboard board, Squad squad, int site, bool playerSide)
    {
        int owner = site < data.SiteFactions.Count ? data.SiteFactions[site] : -1;
        if (playerSide) return owner >= 0 && owner == (world.Player.Faction >= 0 ? world.Player.Faction : data.PlayerFaction);
        return site == board.Town || owner >= 0 && owner == squad.Faction;
    }

    /// <summary>Whether sitting around takes a piece of furniture of this kind (bits CHAIR and THRONE of the mask; TRAINING too unless <paramref name="dummies"/> is false).</summary>
    static bool Sittable(HomeSpotKind kind, bool dummies) => kind is HomeSpotKind.Chair or HomeSpotKind.ThroneSeat || dummies && kind == HomeSpotKind.Training;

    static bool PlayerSide(World world, CharacterCold cold) => cold.IsPlayer || world.Player.Faction >= 0 && cold.Faction == world.Player.Faction;

    /// <summary>Marks every seat a squad's member holds from the last pass as taken, before any squad claims (the cadence pass; see <see cref="ClaimSeats"/>).</summary>
    void ReserveSeats(World world)
    {
        foreach (var squad in world.Squads.All)
        {
            if (squad.Ai is not { } board) continue;
            if (board.Home is { } h) TakeOperatedSeats(world, h, TakenOf(h));
            if (board.Home is { } home && board.SeatClaims.Count > 0)
            {
                var taken = TakenOf(home);
                foreach (var spot in board.SeatClaims.Values)
                    if (IndexOf(home, spot) is >= 0 and var i) taken.Add(i);
            }
            foreach (var claim in board.SitClaims.Values) TakenOf(claim.Building).Add(claim.Index);
        }
    }

    static int IndexOf(TownBuilding building, HomeSpot spot)
    {
        for (int i = 0; i < building.Spots.Count; i++)
            if (building.Spots[i] == spot) return i;
        return -1;
    }

    /// <summary>The first free spot of <paramref name="kind"/> in <paramref name="bar"/>, or with <paramref name="near"/> the nearest free one to it; -1: none.</summary>
    static int FreeSeat(TownBuilding bar, HashSet<int> taken, HomeSpotKind kind, Vector3? near = null)
    {
        int best = -1;
        float bestD = float.MaxValue;
        for (int i = 0; i < bar.Spots.Count; i++)
        {
            if (bar.Spots[i].Kind != kind || taken.Contains(i)) continue;
            if (near is not { } p) return i;
            float d = Vector3.DistanceSquared(bar.Spots[i].Position, p);
            if (d < bestD) (best, bestD) = (i, d);
        }
        return best;
    }

    /// <summary>
    /// Whether the member's role list has a turret goal open at <paramref name="hour"/>, and whether it is only the home building's (222): that one may only man a
    /// turret mounted on its home. Its role list only (not its race's goals): a race goal without the town's turret data names no turret.
    /// </summary>
    static bool TurretGoal(Blackboard board, Squad squad, CharacterId id, CharacterCold cold, int hour, out bool homeOnly)
    {
        var roles = board.Roles[(int)RoleOf(squad, id, cold)];
        bool home = false, town = false;
        for (int k = 0; k < roles.Count; k++)
        {
            if (!roles[k].InWindow(hour)) continue;
            var type = roles[k].Task.Type;
            if (type == TaskType.MAN_A_TURRET_ON_BUILDING) home = true;
            else if (type is TaskType.MAN_A_TURRET or TaskType.MAN_A_TURRET_PLAYER_JOB or TaskType.USE_TURRET) town = true;
        }
        homeOnly = home && !town;
        return home || town;
    }

    void Attach(World world, Squad squad)
    {
        var (packages, template) = TemplateOf(squad.TemplateId);
        var home = AssignHome(world, squad, template);
        // The template's initial door state applies to the home it is given (docs/game/locks.md "Doors"); the first squad's wins, a saved building keeps its own.
        if (home is not null) world.Fixtures.ApplyInitialDoorState(home.Key, template?.InitialDoorState ?? 0);
        ulong roll = Rng.Hash(world.Seed, (ulong)squad.Id, RngPurpose.Think, SeedSalts.AiPatrol);
        squad.Ai = new Blackboard(packages)
        {
            Town = squad.Town, Size = squad.Members.Count, OwnHome = home, Home = home, HomePublic = template?.PublicDay ?? true,
            NextPatrol = world.GameHours + Rng.Float(roll) * Settings.FirstPatrolHours,
        };
        // The town it starts in, taken as already entered (engine choice: no ENTER_TOWN line for every squad a town makes).
        if (LeaderPosition(world, squad) is { } at) (squad.Ai.InTown, squad.Ai.InTownSince) = (CurrentTown(world, at), world.GameHours);
        if (squad.SavedAi is { } saved)
        {
            rules.Bind(this, world, squad);
            saved.Apply(squad.Ai, data.Ai, rules);
            squad.SavedAi = null;
        }
        // An occupation it left the world in goes on in the player town it comes back in (the original keeps the occupier's platoon and package, docs/game/ai.md); the town
        // is the one it was made in, as a squad made this tick has no positions yet.
        if (squad.LeftPackage is { } left && data.Ai.Package(left) is { } package && PackageClasses.Of(package.Signal) == PackageClass.OccupyTown && squad.Ai.Contract is null
            && CurrentTown(world, squad.Position) is { PlayerTown: > 0 } there && squad.Ai.Resume(package))
        {
            squad.Ai.InTown = there;
            OccupyResumed(world, squad, squad.Ai);
        }
        squad.LeftPackage = null;
    }

    /// <summary>
    /// Minds for the members of a squad the AI does not run (<see cref="Runs"/>; not the player's): every character has goals in the original (<c>setupAI</c> <c>FUN_140621b70</c>: 14, 32, its
    /// race's and its faction's <c>AI Goals</c>; its squad's packages add theirs, docs/game/ai-tasks.md "Precedence"), so each gets an <see cref="AiMind.Engine"/> mind whose combat goals are its
    /// platoon's package's for its role (a campaign squad's phase package, a roaming one's the package the population's stand-in follows, else the template's first choice; none for a
    /// template without packages) and its race's. Its routine stays the engine's. Serial, on the package cadence and when the squad appears; a mind made for another squad is made again.
    /// </summary>
    void EngineMinds(World world, Squad squad)
    {
        if (IsPlayers(world, squad)) return;
        var table = world.Characters;
        AiPackage? package;
        if (squad.PlatoonId >= 0 && world.Platoons.Find(squad.PlatoonId) is { } platoon)
        {
            // A campaign's squad runs its phase's package (as PopulationSystem.GoalDuty reads it); a roaming one the package its stand-in follows.
            string? id = platoon.Campaign is { } inst
                ? inst.Phase <= 2 ? world.SystemOf<PopulationSystem>()?.CampaignOf(inst.CampaignId)?.PackageOf(inst.Phase, inst.Leader == platoon.Id) : null
                : platoon.Package;
            package = id is null ? platoon.Campaign is null ? FirstPackage(squad.TemplateId) : null : data.Ai.Package(id);
        }
        else package = FirstPackage(squad.TemplateId);
        for (int k = 0; k < squad.Members.Count; k++)
        {
            if (!table.TryResolveNext(squad.Members[k], out int s) || table.Cold(s) is not { IsPlayer: false } cold || cold.Ai is { Engine: false }) continue;
            if (cold.Ai is null || !ReferenceEquals(cold.Ai.Squad, squad))
            {
                var p = table.Next[s].Position;
                cold.Ai = new AiMind
                {
                    Squad = squad, MemberIndex = k, Post = new Vector2(p.X, p.Z), Engine = true, Version = EngineBoard.Version,
                    RaceGoals = cold.Race is { } race ? data.Ai.RaceGoals(race.StringId) : [],
                };
            }
            cold.Ai.EngineGoals = package is null ? [] : CombatGoalsOf(package, RoleOf(squad, squad.Members[k], cold));
        }
    }

    /// <summary>The package a template's squad starts with (<see cref="Blackboard.Update"/> over its slots, as the population's stand-in takes it, <see cref="PlatoonJobs.For"/>); null without one.</summary>
    AiPackage? FirstPackage(string templateId)
    {
        if (firstPackages.TryGetValue(templateId, out var known)) return known;
        var packages = TemplateOf(templateId).Packages;
        AiPackage? first = null;
        if (packages.Count > 0)
        {
            var board = new Blackboard(packages);
            board.Update();
            first = board.Current;
        }
        return firstPackages[templateId] = first;
    }

    /// <summary>The combat goals (<see cref="AiBehaviour.Fight"/>) of a package's role list, kept per package and role (serial only).</summary>
    IReadOnlyList<AiGoal> CombatGoalsOf(AiPackage package, AiRole role)
    {
        if (combatGoals.TryGetValue((package, role), out var known)) return known;
        var list = package.Roles[(int)role].Where(g => TaskBehaviours.Of(g.Task.Type) == AiBehaviour.Fight).ToArray();
        return combatGoals[(package, role)] = list;
    }

    // Derived from the data, not hashed.
    readonly Dictionary<string, AiPackage?> firstPackages = [];
    readonly Dictionary<(AiPackage, AiRole), IReadOnlyList<AiGoal>> combatGoals = [];

    /// <summary>
    /// The AI runs a squad that is not the player's, not a roaming platoon's active half (a town's own saved squad, pinned, is run like the town's, a save's wild one like the wild
    /// sites' squads), and whose template lists AI packages.
    /// </summary>
    bool Runs(World world, Squad squad) => (squad.PlatoonId < 0 || squad.Pinned || squad.Wild) && !IsPlayers(world, squad) && TemplateOf(squad.TemplateId).Packages.Count > 0;

    bool IsPlayers(World world, Squad squad)
    {
        if (squad.Faction == data.PlayerFaction && data.PlayerFaction >= 0) return true;
        foreach (var m in squad.Members)
            if (world.Characters.TryResolveNext(m, out int s) && world.Characters.Cold(s)!.IsPlayer) return true;
        return false;
    }

    (IReadOnlyList<SlottedPackage> Packages, SquadTemplate? Template) TemplateOf(string templateId)
    {
        if (templates.TryGetValue(templateId, out var known)) return known;
        var template = data.Db.Find(templateId) is { } r && SquadFactory.IsTemplate(r) ? SquadTemplate.From(r) : null;
        var entry = (template is null ? (IReadOnlyList<SlottedPackage>)[] : data.Ai.Packages(template), template);
        templates[templateId] = entry;
        return entry;
    }

    /// <summary>
    /// The squad's own building: the one its town gave it (<see cref="Squad.HomeBuilding"/>, docs/game/factions-squads-towns.md "Town residents"); for a squad without
    /// one (made without building data, or a save's without a home) an <b>engine choice</b>: among its town's
    /// buildings no other squad has, the nearest of the template's <c>building</c>; else by <c>building designation</c> a shop (SHOP) or the bar (BAR, or one of the
    /// town's bar squads; shared);
    /// else the nearest with beds, else the nearest at all. None for a squad without a town or a town without building data.
    /// </summary>
    TownBuilding? AssignHome(World world, Squad squad, SquadTemplate? template)
    {
        if (squad.HomeBuilding is { } given) return given;
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
                if (!accept(b) || (!shared && taken.Contains(b))) continue;
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

    /// <summary>A squad made from one of its town's <c>bar squads</c> entries (the original gives it no home; Meitou keeps it at the bar, not in a free building with beds).</summary>
    bool IsBarSquad(Squad squad)
    {
        int f = squad.Town < data.SiteFactions.Count ? data.SiteFactions[squad.Town] : -1;
        return PopulationSystem.BarEntries(data.Sites[squad.Town].Town, f >= 0 ? data.Factions[f] : null).Any(e => e.Id == squad.TemplateId);
    }

    /// <summary>
    /// Whether the squad is intact (<see cref="SquadIntact"/>, <c>FUN_14079acf0</c>, Verified): never while it lacks a member held in a cage or carried off by a non-ally.
    /// Persistent with a home stands for a town's resident squad with its own building (engine reading of the platoon's persistent flag and home holder).
    /// </summary>
    bool IsIntact(World world, Squad squad, Blackboard board)
    {
        var table = world.Characters;
        if (SquadIntact.Held(table, squad, data.Relations)) return true;
        int able = 0;
        foreach (var m in squad.Members)
            if (table.TryResolveNext(m, out int s) && table.Cold(s)!.Medical is not { Incapacitated: true }) able++;
        return SquadIntact.Decide(able, Math.Max(board.Size, 1), !squad.Leader.IsNone, SquadIntact.Down(table, squad.Leader), SquadIntact.PersistentHome(squad));
    }

    /// <summary>A running patrol moves on when its leader reaches the route point; the members think again at once.</summary>
    void AdvanceRoute(World world, Squad squad, Blackboard board)
    {
        if (board.CurrentClass is not (PackageClass.Patrol or PackageClass.PatrolRunning) || board.RouteIndex >= board.Route.Length) return;
        if (!world.Characters.TryResolveNext(squad.Leader, out int s)) return;
        var p = world.Characters.Next[s].Position;
        if (Vector2.Distance(new Vector2(p.X, p.Z), board.Route[board.RouteIndex]) > Settings.RouteArrive) return;
        board.RouteIndex++;
        board.Touch();
    }

    /// <summary>The package-class rules of <see cref="Blackboard.Update"/>, bound to the squad being updated (serial steps only).</summary>
    sealed class Rules : IPackageRules
    {
        AiSystem system = null!;
        World world = null!;
        Squad squad = null!;

        public void Bind(AiSystem s, World w, Squad q) => (system, world, squad) = (s, w, q);

        public bool CanStart(Blackboard board, AiPackage package, PackageClass kind) => kind switch
        {
            PackageClass.Retreat => !board.Intact,
            PackageClass.Patrol or PackageClass.PatrolRunning => board.Town >= 0 && world.GameHours >= board.NextPatrol && world.Characters.IsAlive(squad.Leader)
                && !system.CannibalPatrolHeld(world, squad, board, package),
            PackageClass.Bar => BarHere(board),
            PackageClass.BarOutdoors => true,
            PackageClass.CannibalRetreat or PackageClass.CannibalsAtHome or PackageClass.PrisonerGathering or PackageClass.SlaverShipping => system.CaptiveCanStart(world, squad, board, package, kind),
            PackageClass.WanderingTrader => system.TraderCanStart(world, squad, board),
            PackageClass.PassingAssault => system.AssaultCanStart(world, squad, board),
            PackageClass.Refugee => RefugeeCanStart(world, squad),
            _ => false,
        };

        /// <summary>
        /// Where a bar package can run: for a squad that is not a town's own (engine choice: a town's squads keep their town), away from home in a placed town not hostile to it that has a
        /// public bar (the original's <c>Package_HangAroundBar</c> takes the town the squad is in, docs/game/ai.md "Package classes"), in a player town not (engine choice: their bars
        /// are not read); at home (or in no town), with building data only where the town has a bar or an outdoor table, without it the package runs and the squad keeps its spots.
        /// </summary>
        bool BarHere(Blackboard board)
        {
            var t = board.InTown;
            if (!squad.TownOwn && t.PlayerTown > 0) return false;
            if (!squad.TownOwn && t.Site >= 0 && t.Site != board.Town)
            {
                int theirs = t.Site < system.Data.SiteFactions.Count ? system.Data.SiteFactions[t.Site] : -1;
                return !(squad.Faction >= 0 && theirs >= 0 && system.Data.Relations.IsHostile(squad.Faction, theirs)) && system.Places(t.Site).Bars.Count > 0;
            }
            return board.Town < 0 || system.Data.Sites[board.Town].Buildings.Count == 0
                || system.Places(board.Town) is { } p && (p.Bars.Count > 0 || p.OutdoorTables.Count > 0);
        }

        /// <summary>The town a bar package uses: for a squad that is not a town's own, the placed town it is in; else its own town.</summary>
        int BarTown(Blackboard board) => !squad.TownOwn && board.InTown.Site >= 0 ? board.InTown.Site : board.Town;

        public bool Finished(Blackboard board, AiPackage package, PackageClass kind) => kind switch
        {
            PackageClass.Retreat => board.Intact,
            PackageClass.Patrol or PackageClass.PatrolRunning => board.RouteIndex >= board.Route.Length || world.GameHours >= board.PatrolGiveUp || !world.Characters.IsAlive(squad.Leader),
            PackageClass.CannibalRetreat or PackageClass.CannibalsAtHome or PackageClass.PrisonerGathering or PackageClass.SlaverShipping => system.CaptiveFinished(world, squad, board, package, kind),
            // Away from home a bar package ends where it could not start (the original's finished test is its start test negated).
            PackageClass.Bar => !squad.TownOwn && !board.InTown.IsNone && board.InTown != CampaignTown.OfSite(board.Town) && !BarHere(board),
            PackageClass.WanderingTrader => board.TraderState == 2,
            PackageClass.PassingAssault => system.AssaultFinished(world, squad, board, package),
            PackageClass.OccupyTown => system.OccupyFinished(world, squad, board),
            PackageClass.Refugee => system.RefugeeFinished(world, squad, board),
            _ => false,
        };

        public bool MayLead(Blackboard board, AiPackage package, PackageClass kind) => kind != PackageClass.PassingAssault || system.AssaultMayLead(world, squad, board);

        public void Ended(Blackboard board, AiPackage package, PackageClass kind)
        {
            switch (kind)
            {
                case PackageClass.PassingAssault: system.AssaultEnded(world, squad); break;
                case PackageClass.OccupyTown: system.OccupyEnded(world, squad, board); break;
            }
        }

        public void Started(Blackboard board, AiPackage package, PackageClass kind)
        {
            switch (kind)
            {
                case PackageClass.Patrol:
                case PackageClass.PatrolRunning:
                    system.LayRoute(world, squad, board);
                    break;
                case PackageClass.Bar:
                case PackageClass.BarOutdoors:
                    if (BarTown(board) is >= 0 and var town && system.PickTable(world, town, squad, outdoors: kind == PackageClass.BarOutdoors) is var (bar, table))
                        (board.Home, board.BarTable) = (bar, table);
                    else if (kind == PackageClass.BarOutdoors) board.Home = null;
                    break;
                case PackageClass.CannibalRetreat:
                case PackageClass.SlaverShipping:
                    system.CaptiveStarted(world, squad, board, package, kind);
                    break;
                case PackageClass.WanderingTrader:
                    system.TraderStart(world, squad, board);
                    break;
                case PackageClass.PassingAssault:
                    system.AssaultStarted(world, squad, board);
                    break;
                case PackageClass.OccupyTown:
                    system.OccupyStarted(world, squad, board);
                    break;
                case PackageClass.Refugee:
                    system.RefugeeStarted(world, squad, board);
                    break;
            }
        }
    }

    /// <summary>
    /// The table a squad starting a bar package reserves (the original's <c>Package_HangAroundBar</c> start, docs/game/ai-tasks.md "Town goals"): the town's public bars
    /// are tried in turn from a random one (seeded by the squad and the tick), the first table of one no other squad's bar package holds wins; a town without bars, or
    /// <paramref name="outdoors"/>, takes the first free outdoor table. Null when all are taken: the squad stays home.
    /// </summary>
    (TownBuilding Bar, int Table)? PickTable(World world, int site, Squad squad, bool outdoors)
    {
        var places = Places(site);
        var held = new HashSet<(TownBuilding, int)>();
        foreach (var other in world.Squads.All)
            if (other != squad && other.Ai is { BarTable: >= 0, Home: { } at } b && b.CurrentClass is PackageClass.Bar or PackageClass.BarOutdoors) held.Add((at, b.BarTable));
        if (!outdoors && places.Bars.Count > 0)
        {
            int start = Rng.Int(Rng.Hash(world.Seed, (ulong)squad.Id << 32 ^ (ulong)world.Tick, RngPurpose.Think, SeedSalts.BarPick), places.Bars.Count);
            for (int k = 0; k < places.Bars.Count; k++)
            {
                var bar = places.Bars[(start + k) % places.Bars.Count];
                for (int t = 0; t < bar.Tables.Count; t++)
                    if (!held.Contains((bar, t))) return (bar, t);
            }
            return null;
        }
        foreach (var table in places.OutdoorTables)
            if (!held.Contains((table, 0))) return (table, 0);
        return null;
    }

    /// <summary>
    /// A patrol's route (<b>engine choice</b>: the original's <c>GoOutOnPatrol</c> is not decoded): <see cref="AiSettings.PatrolPoints"/> points round the home town at
    /// seeded angles and distances of 1.5 to 3 town radii, each pulled in towards the town until walkable (dropped when none is), then home. The next patrol may start
    /// 6 to 12 game hours after this one started; this one gives up after 6.
    /// </summary>
    void LayRoute(World world, Squad squad, Blackboard board)
    {
        if (board.Town < 0) { board.Route = []; return; }
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
        float every = Settings.PatrolEveryHoursMin + Rng.Float(Rng.Hash(world.Seed, key, RngPurpose.Think, 0x50)) * (Settings.PatrolEveryHoursMax - Settings.PatrolEveryHoursMin);
        board.NextPatrol = world.GameHours + every;
        board.PatrolGiveUp = world.GameHours + Settings.PatrolGiveUpHours;
    }
}
