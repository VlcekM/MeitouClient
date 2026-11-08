using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation.Aggro;
using Meitou.Simulation.Movement;

namespace Meitou.Simulation.Ai;

/// <summary>
/// How the engine acts out a goal it chose (docs/simulation.md "AI"). The original runs a <c>Task_*</c> class and its GOAP plan per goal
/// (docs/game/ai-tasks.md); here a goal maps onto the movement tasks that exist, at the town's buildings, nodes and beds.
/// A goal whose type has no behaviour is never chosen.
/// </summary>
public enum AiBehaviour : byte
{
    /// <summary>Not built: the goal scores 0.</summary>
    None = 0,
    /// <summary>Idleness (14): stands where it is, as the original's blank task (<see cref="CharacterTask.Idle"/>).</summary>
    Mill = 1,
    /// <summary>Walks to its node in the home building (shopkeeper, guard, throne or general, by goal), else to its post, and stands there.</summary>
    Post = 3,
    /// <summary>Keeps its place beside the squad leader (<see cref="CharacterTask.Follow"/>); the leader itself cannot.</summary>
    Follow = 4,
    /// <summary>Walks to its bed in the home building (else the home, else its post) and lies down there (<see cref="CharacterTask.Rest"/>).</summary>
    Sleep = 5,
    /// <summary>Sits down (<see cref="CharacterTask.Rest"/>) at the seat it claimed (<see cref="Blackboard.SeatClaims"/>); wanted only while it has one.</summary>
    Sit = 6,
    /// <summary>While a patrol package runs, the leader walks the patrol's route and the others follow it; otherwise wanders about the town (<see cref="CharacterTask.Wander"/>).</summary>
    Patrol = 7,
    /// <summary>Goes back to its home town (its home building, else the town's centre); wanted at 0.5 while away from it and not at all at home (docs/game/perception.md 7.2, Observed).</summary>
    GoHome = 8,
    /// <summary>Walks to the turret of its town it claimed and stands at its gun (docs/simulation.md "AI"), where the combat system fires it.</summary>
    Turret = 9,
    /// <summary>Walks to a general node of a random building of its town and stands there (WANDER_TOWN, docs/game/ai-tasks.md "Town goals", Observed).</summary>
    TownNode = 10,
    /// <summary>Walks into a shop of its town and stands at a node in it (SHOPPING; buying is not modelled).</summary>
    Shop = 11,
    /// <summary>Sits at the seat of the package's bar it claimed (RELAX_IN_TOWN_PACKAGE under a bar package); wanted only while it has one.</summary>
    Relax = 12,
    /// <summary>Walks to the nearest town; not wanted while in one (WANDERER, docs/game/ai-tasks.md "Town goals", Observed).</summary>
    ToTown = 13,
    /// <summary>
    /// The leader walks (runs for the fast one) to the running package's mission town (<see cref="Blackboard.Destination"/>); wanted only while it has one and is not in it
    /// (WANDERING_TRADER and the travelling rows, whose plan is <c>Task_TravelToTargetTown</c> to <c>atDestinationTown</c>, docs/game/ai-tasks.md).
    /// </summary>
    Travel = 14,
    /// <summary>
    /// Works a building of its town: walks to a random one of the town's mines, research benches, refineries and crafting benches and stands at it at its work clip (the worker goals 189 and
    /// 190, finders <c>FUN_1408cac60</c> / <c>FUN_1408cadb0</c>, docs/game/ai-tasks.md "Slave work, shout and capture"; the machines of NPC towns produce nothing here).
    /// </summary>
    Work = 15,
    /// <summary>
    /// Answers a raised alarm of its town (INVESTIGATE_ALARMS 209, and 210 for its own faction's only; <c>Task_InvestigateAlarms</c>, docs/game/economy.md "Town alarms"): runs to it, looks
    /// about there and clears it, attacking criminals it sees on the way (<see cref="Crime.CrimeSystem"/>'s serial step). Wanted only while an alarm is in reach.
    /// </summary>
    Investigate = 16,
    /// <summary>
    /// Attacks the best-scoring standing member of the squad its running contract is aimed at (DEFEAT_SQUAD 101, the contract's mission target; finder <c>FUN_140793eb0</c>,
    /// docs/game/ai-tasks.md "Combat goals"): the serial step orders the attack, the combat system fights it. Wanted only while such a member is left.
    /// </summary>
    Assault = 17,
    /// <summary>
    /// SELF_PRESERVATION (32, every NPC's urgent goal, <see cref="SelfPreservation"/>): wanted at the threat level (<see cref="Combat.CombatSystem.ThreatLevel"/>), planned as [35, 32] when its
    /// provider is 35 Running away (a crippled NPC, or one with no working arm; acted out as <see cref="Flee"/>), else [13, 32] on its own attacker (acted out as a <see cref="Fight"/>).
    /// </summary>
    SelfPreserve = 18,
    /// <summary>
    /// RUN_AWAY (35, <c>Task_Runaway</c>): asks <see cref="FleeSystem"/> to run the character from its enemies (its first run, then hops, until it is safe); a goal of the packages that list it
    /// ("Leader flees while guards fight", the police stations' slaves), wanted at row 35's desire (<see cref="SelfPreservation.RunDesire"/>) while an enemy is near.
    /// </summary>
    Flee = 19,
    /// <summary>
    /// The combat goals that start a fight by their own finder (docs/game/ai-tasks.md "Combat goals"): 16 and 21 attack on sight, 144 in its home territory, 34 a trouble maker in its town,
    /// 143 the attacker of a squad mate in a fight, 15 and 288 the attacker of a sensed ally in a fight. Wanted at the goal's desire times its finder's factor (1 with a target, else 0;
    /// <see cref="AiSystem"/>, AiSystem.Fights.cs); the think asks for the fight (<see cref="AiMind.FightTarget"/>) and the serial step starts it, which the combat system fights.
    /// </summary>
    Fight = 20,
    /// <summary>
    /// JOB_MEDIC (58, the Medic goal of most packages, docs/game/ai-tasks.md "Medic jobs"): wanted at the patient finder's score (<see cref="FirstAid.FirstAidSystem.FindPatient"/>, run on the
    /// NPC's sweep turn and kept in <see cref="AiMind.MedicScore"/>); the think asks for the treatment (<see cref="AiMind.MedicAsk"/>) and the serial step starts it, row 58's Treat.
    /// </summary>
    Medic = 21,
    /// <summary>
    /// PATROL_TOWN (36, <c>Task_PatrolTown</c>, docs/game/ai-tasks.md "Town goals"): walks to a random point round its town's centre, and draws the next one each time a walk is over,
    /// so it keeps strolling about the town (its squad's mission town, else its home; an occupier: the player town it holds).
    /// </summary>
    PatrolTown = 22,
    /// <summary>
    /// Find and rescue leader (142, finder <c>FUN_140598540</c>, list B 101 <c>isLeaderDown</c>): wanted while the squad's leader (not the character, not dead) is down and can be lifted
    /// (<see cref="Carrying.CarrySystem.LeaderToRescue"/>); the serial carry sweep lifts it (row 213, the snatch lift) once the goal is chosen.
    /// </summary>
    RescueLeader = 23,
    /// <summary>
    /// Crouches (CROUCH 27, <c>Task_Crouch</c>) or stands up from a crouch (STAND_UP 28, <c>Task_UnCrouch</c>) where it stands: the <c>squat T</c> clip plays forwards or backwards and the animation
    /// stance's crouched byte is set once it passes 0.9 or cleared under 0.1 (<see cref="AnimationSystem"/>). The base data lists neither goal; modded packages may.
    /// </summary>
    Crouch = 24,
    /// <summary>
    /// A captive goal (slave work, gathering, processing, the prisoner's fate and the rest that <see cref="Kidnap.CaptiveSystem"/> runs as jobs;
    /// docs/simulation.md "Captives"): its sweep starts the job for a member the AI holds that stands free. Any other member awake weighs the goal at
    /// the desire its last sweep turn or job's end kept (<see cref="AiMind.CaptiveOffers"/>), as the original's chooser weighs it against its other
    /// goals; the think asks for the job (<see cref="AiMind.CaptiveAsk"/>) and the serial step starts it.
    /// </summary>
    Captive = 25,
}

/// <summary>The engine's table from <see cref="TaskType"/> to <see cref="AiBehaviour"/>, and the TaskData display text (docs/game/ai-tasks.md "TaskData registry", Verified) of the types it acts out.</summary>
public static class TaskBehaviours
{
    static readonly Dictionary<TaskType, (AiBehaviour Behaviour, string Text)> Table = new()
    {
        [TaskType.IDLE] = (AiBehaviour.Mill, "Idleness"),
        [TaskType.WANDERER] = (AiBehaviour.ToTown, "Wandering"),
        [TaskType.WANDER_TOWN] = (AiBehaviour.TownNode, "Wandering town"),
        [TaskType.PATROL_TOWN] = (AiBehaviour.PatrolTown, "Patrolling town"),
        [TaskType.PATROL] = (AiBehaviour.Patrol, "Patrolling"),
        [TaskType.RELAX_IN_TOWN_PACKAGE] = (AiBehaviour.Relax, "Relaxing"),
        [TaskType.SHOPPING] = (AiBehaviour.Shop, "Shopping"),
        [TaskType.STAND_AT_SHOPKEEPER_NODE] = (AiBehaviour.Post, "Shopkeeper"),
        [TaskType.STAND_AT_GUARD_NODE_HOMEBUILDING] = (AiBehaviour.Post, "Static guard"),
        [TaskType.STAND_AT_GUARD_NODE_HOMEBUILDING_INDOORS_ONLY] = (AiBehaviour.Post, "Static guard indoors"),
        [TaskType.STAND_AT_GUARD_NODE_HOMETOWN_OUTSIDE] = (AiBehaviour.Post, "Static town guard"),
        [TaskType.STAND_AT_GENERAL_NODE] = (AiBehaviour.Post, "general node"),
        [TaskType.HOLD_POSITION] = (AiBehaviour.Post, "Holding position"),
        [TaskType.STAY_IN_HOME] = (AiBehaviour.Post, "Staying home"),
        [TaskType.MAN_THE_GATE] = (AiBehaviour.Post, "Gate guard"),
        [TaskType.MAN_A_TURRET] = (AiBehaviour.Turret, "Turret guard"),
        [TaskType.MAN_A_TURRET_ON_BUILDING] = (AiBehaviour.Turret, "Turret home guard"),
        [TaskType.MAN_A_TURRET_PLAYER_JOB] = (AiBehaviour.Turret, "Turret guard"),
        [TaskType.USE_TURRET] = (AiBehaviour.Turret, "Turret gunner"),
        [TaskType.SIT_ON_THRONE] = (AiBehaviour.Sit, "Reigning"),
        [TaskType.FOLLOW_SQUADLEADER] = (AiBehaviour.Follow, "Following leader"),
        [TaskType.FOLLOW_SLAVEMASTER] = (AiBehaviour.Follow, "Following master"),
        // 45 Bodyguard has no behaviour of its own (FUN_14032ebd0 builds Task_Blank for it): the following of a bodyguard contract is goal 44's.
        [TaskType.FOLLOW_PLAYER_ORDER] = (AiBehaviour.Follow, "Following"),
        // 253 Following (urgent escape): a Task_Follow like 31 / 44 / 289 (FUN_14032ebd0 case 253, list B !7 60 90 59); one package lists it as a Leader goal.
        [TaskType.FOLLOW_URGENT_ESCAPE] = (AiBehaviour.Follow, "Following"),
        [TaskType.GO_HOME_AND_GO_TO_BED] = (AiBehaviour.Sleep, "Going to bed"),
        [TaskType.GO_HOME_AND_GO_TO_BED_SECURE] = (AiBehaviour.Sleep, "Going to bed"),
        [TaskType.SLEEP_ON_FLOOR] = (AiBehaviour.Sleep, "Sleeping on ground"),
        [TaskType.SIT_AROUND] = (AiBehaviour.Sit, "Sitting around"),
        [TaskType.AUTO_LABOURING_MINES] = (AiBehaviour.Work, "Worker"),
        [TaskType.AUTO_LABOURING_MINES_PRETEND] = (AiBehaviour.Work, "Worker"),
        [TaskType.RETREAT_HOMETOWN] = (AiBehaviour.GoHome, "Retreating"),
        [TaskType.RUN_AWAY_HOMETOWN] = (AiBehaviour.GoHome, "Retreating fast"),
        [TaskType.WANDERING_TRADER] = (AiBehaviour.Travel, "Wandering trader"),
        [TaskType.TRAVEL_TO_TARGET_TOWN] = (AiBehaviour.Travel, "Travelling"),
        // To the running package's destination (Blackboard.Destination): the trader's next town; under the retreat packages that list it (signals 1 and 25) the squad's home town
        // (AiSystem.Captives.cs, engine reading).
        [TaskType.TRAVEL_TO_TARGET_TOWN_FAST] = (AiBehaviour.Travel, "Travelling quickly"),
        // Row 215 of the refugee package (and of the campaigns' first phase, whose squads the campaign drives): to its mission, the town or the squad it is to join.
        [TaskType.TRAVEL_TO_TARGET_PACKAGE] = (AiBehaviour.Travel, "Travelling"),
        // Its Task_PatrolTownViolent is not decoded (engine choice): it roams the town it assaults, as patrolling does without a patrol package.
        [TaskType.RUN_AROUND_TOWN_LOOKING_FOR_PEOPLE] = (AiBehaviour.Patrol, "Ransacking town"),
        [TaskType.INVESTIGATE_ALARMS] = (AiBehaviour.Investigate, "Investigating"),
        [TaskType.INVESTIGATE_ALARMS_ALLIES_ONLY] = (AiBehaviour.Investigate, "Investigating"),
        [TaskType.DEFEAT_SQUAD] = (AiBehaviour.Assault, "Assault"),
        // 248 Attacking Squad: the same finder as 101 (FUN_140793eb0, stub LAB_140006d25), the same flags and effect; no base package lists it, a mod can.
        [TaskType.DEFEAT_SQUAD_LIMIT_CHASE_RANGE] = (AiBehaviour.Assault, "Attacking squad"),
        [TaskType.FIND_AND_RESCUE_LEADER] = (AiBehaviour.RescueLeader, "Rescuing leader"),
        [TaskType.CROUCH] = (AiBehaviour.Crouch, "Crouching"),
        [TaskType.STAND_UP] = (AiBehaviour.Crouch, "Standing"),
        [TaskType.SELF_PRESERVATION] = (AiBehaviour.SelfPreserve, "Self preservation"),
        [TaskType.RUN_AWAY] = (AiBehaviour.Flee, "Running away"),
        [TaskType.ATTACK_ENEMIES] = (AiBehaviour.Fight, "Attacking enemies"),
        [TaskType.ATTACK_ENEMIES_AND_NEUTRALS] = (AiBehaviour.Fight, "Attacking others"),
        [TaskType.TERRITORIAL_AGGRESSION_BUT_DONT_LEAVE_HOME] = (AiBehaviour.Fight, "Home territory aggression"),
        [TaskType.ATTACK_TROUBLE_MAKERS] = (AiBehaviour.Fight, "Attacking trouble makers"),
        [TaskType.ATTACK_ENEMIES_OF_MY_SLAVEMASTER] = (AiBehaviour.Fight, "Attacking enemies of my master"),
        [TaskType.PROTECT_OWN_SQUAD] = (AiBehaviour.Fight, "Protect close allies"),
        [TaskType.PROTECT_ALLIES] = (AiBehaviour.Fight, "Protect allies"),
        [TaskType.PROTECT_ALLIES_STAY_IN_TOWN] = (AiBehaviour.Fight, "Protect allies"),
        [TaskType.JOB_MEDIC] = (AiBehaviour.Medic, "Medic"),
    };

    // Flat tables by type number for the think's hot loop (a dictionary lookup per goal was a third of the think).
    internal const int TypeCount = 291;
    static readonly AiBehaviour[] Behaviours = BuildBehaviours();

    static AiBehaviour[] BuildBehaviours()
    {
        var a = new AiBehaviour[TypeCount];
        foreach (var (type, e) in Table) a[(int)type] = e.Behaviour;
        // The captive goals: acted out by their jobs, so no display text of their own here.
        foreach (var type in Kidnap.CaptiveSystem.GoalTypes) a[(int)type] = AiBehaviour.Captive;
        return a;
    }

    public static AiBehaviour Of(TaskType type) => (uint)type < TypeCount ? Behaviours[(int)type] : AiBehaviour.None;

    /// <summary>The game's display text of a type the engine acts out; the enum name for the rest.</summary>
    public static string Text(TaskType type) => Table.TryGetValue(type, out var e) ? e.Text : type == TaskType.STUMBLE_TASK_FORCED ? "Stumbling" : type.ToString();

    /// <summary>Every type the engine acts out.</summary>
    public static IEnumerable<TaskType> Built => Table.Keys;
}
