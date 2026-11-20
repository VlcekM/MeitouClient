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
    /// <summary>
    /// Not built: the goal scores 0.
    /// </summary>
    None = 0,
    /// <summary>
    /// Idleness (14): stands where it is, as the original's blank task (<see cref="CharacterTask.Idle"/>).
    /// </summary>
    Mill = 1,
    /// <summary>
    /// Walks to its node in the home building (shopkeeper, guard, throne or general, by goal), else to its post, and stands there.
    /// </summary>
    Post = 3,
    /// <summary>
    /// Keeps its place beside the squad leader (<see cref="CharacterTask.Follow"/>); the leader itself cannot.
    /// </summary>
    Follow = 4,
    /// <summary>
    /// Walks to its bed in the home building (else the home, else its post) and lies down there (<see cref="CharacterTask.Rest"/>).
    /// </summary>
    Sleep = 5,
    /// <summary>
    /// Sits down (<see cref="CharacterTask.Rest"/>) at the seat it claimed (<see cref="Blackboard.SeatClaims"/>); wanted only while it has one.
    /// </summary>
    Sit = 6,
    /// <summary>
    /// PATROL (22, <c>Task_Patrol</c>): the leader walks to the squad's patrol point, which the population's patrol step draws sector by sector
    /// (<see cref="Factions.Squad.PatrolPoint"/>), and the others follow it.
    /// </summary>
    Patrol = 7,
    /// <summary>
    /// Goes back to its home town (its home building, else the town's centre); wanted at 0.5 while away from it and not at all at home
    /// (docs/game/perception.md 7.2, Observed).
    /// </summary>
    GoHome = 8,
    /// <summary>
    /// Walks to the turret of its town it claimed and stands at its gun (docs/simulation.md "AI"), where the combat system fires it.
    /// </summary>
    Turret = 9,
    /// <summary>
    /// Walks to a general node of a random building of its town and stands there (WANDER_TOWN, docs/game/ai-tasks.md "Town goals", Observed).
    /// </summary>
    TownNode = 10,
    /// <summary>
    /// Walks into a shop of its town and stands at a node in it (SHOPPING; buying is not modelled).
    /// </summary>
    Shop = 11,
    /// <summary>
    /// Sits at the seat of the package's bar it claimed (RELAX_IN_TOWN_PACKAGE under a bar package); wanted only while it has one.
    /// </summary>
    Relax = 12,
    /// <summary>
    /// Walks to the nearest town; not wanted while in one (WANDERER, docs/game/ai-tasks.md "Town goals", Observed).
    /// </summary>
    ToTown = 13,
    /// <summary>
    /// WANDERING_TRADER 39 and the travelling rows 53 and 130 (desire 1): their plan's <c>Task_TravelToTargetTown</c> step walks to the squad's town
    /// (<see cref="TownTravel"/>) until <c>atDestinationTown</c> holds; 39 then stands. 215 Mission formation: the refugee's walk to its mission, a
    /// war package's formation (docs/game/ai-tasks.md "Town goals").
    /// </summary>
    Travel = 14,
    /// <summary>
    /// Works a building of its town: walks to a random one of the town's mines, research benches, refineries and crafting benches and stands at it at
    /// its work clip (the worker goals 189 and 190, finders <c>FUN_1408cac60</c> / <c>FUN_1408cadb0</c>, docs/game/ai-tasks.md "Slave work, shout and
    /// capture"; the machines of NPC towns produce nothing here).
    /// </summary>
    Work = 15,
    /// <summary>
    /// Answers a raised alarm of its town (INVESTIGATE_ALARMS 209, and 210 for its own faction's only; <c>Task_InvestigateAlarms</c>,
    /// docs/game/economy.md "Town alarms"): runs to it, looks about there and clears it, attacking criminals it sees on the way
    /// (<see cref="Crime.CrimeSystem"/>'s serial step). Wanted only while an alarm is in reach.
    /// </summary>
    Investigate = 16,
    /// <summary>
    /// Attacks the best-scoring standing member of the squad its running contract is aimed at (DEFEAT_SQUAD 101, the contract's mission target;
    /// finder <c>FUN_140793eb0</c>, docs/game/ai-tasks.md "Combat goals"): the serial step orders the attack, the combat system fights it. Wanted
    /// only while such a member is left.
    /// </summary>
    Assault = 17,
    /// <summary>
    /// SELF_PRESERVATION (32, every NPC's urgent goal, <see cref="SelfPreservation"/>): wanted at the threat level
    /// (<see cref="Combat.CombatSystem.ThreatLevel"/>), planned as [35, 32] when its provider is 35 Running away (a crippled NPC, or one with no
    /// working arm; acted out as <see cref="Flee"/>), else [13, 32] on its own attacker (acted out as a <see cref="Fight"/>).
    /// </summary>
    SelfPreserve = 18,
    /// <summary>
    /// RUN_AWAY (35, <c>Task_Runaway</c>): asks <see cref="FleeSystem"/> to run the character from its enemies (its first run, then hops, until it is
    /// safe); a goal of the packages that list it ("Leader flees while guards fight", the police stations' slaves), wanted at row 35's desire
    /// (<see cref="SelfPreservation.RunDesire"/>) while an enemy is near.
    /// </summary>
    Flee = 19,
    /// <summary>
    /// The combat goals that start a fight by their own finder (docs/game/ai-tasks.md "Combat goals"): 16 and 21 attack on sight, 144 in its home
    /// territory, 34 a trouble maker in its town, 143 the attacker of a squad mate in a fight, 15 and 288 the attacker of a sensed ally in a fight.
    /// Wanted at the goal's desire times its finder's factor (1 with a target, else 0; <see cref="AiSystem"/>, AiSystem.Fights.cs); the think asks
    /// for the fight (<see cref="AiMind.FightTarget"/>) and the serial step starts it, which the combat system fights.
    /// </summary>
    Fight = 20,
    /// <summary>
    /// JOB_MEDIC (58, the Medic goal of most packages, docs/game/ai-tasks.md "Medic jobs"): wanted at the patient finder's score
    /// (<see cref="FirstAid.FirstAidSystem.FindPatient"/>, run on the NPC's sweep turn and kept in <see cref="AiMind.MedicScore"/>); the think asks
    /// for the treatment (<see cref="AiMind.MedicAsk"/>) and the serial step starts it, row 58's Treat.
    /// </summary>
    Medic = 21,
    /// <summary>
    /// PATROL_TOWN (36, <c>Task_PatrolTown</c>, docs/game/ai-tasks.md "Town goals"): away from the squad's town (<see cref="SquadTown"/>) its plan's
    /// 130 walks there (<see cref="TownTravel"/>); in it, it walks to a random point round the town's centre and draws the next one each time a walk
    /// is over, so it keeps strolling about the town.
    /// </summary>
    PatrolTown = 22,
    /// <summary>
    /// Crouches (CROUCH 27, <c>Task_Crouch</c>) or stands up from a crouch (STAND_UP 28, <c>Task_UnCrouch</c>) where it stands: the <c>squat T</c>
    /// clip plays forwards or backwards and the animation stance's crouched byte is set once it passes 0.9 or cleared under 0.1
    /// (<see cref="AnimationSystem"/>). The base data lists neither goal; modded packages may.
    /// </summary>
    Crouch = 24,
    /// <summary>
    /// HEAL_MY_LEGS (269 Fixing Legs, a race goal; <c>Task_FirstAid</c> on the character itself, docs/game/ai-tasks.md "Medic jobs"): wanted while its
    /// legs are messed up (<see cref="AiSystem.HealLegsDesire"/>); planned by the planner from its list B (an arm, a first aid kit, not swimming, no
    /// immediate threat, the weapon sheathed first), then the think asks for the treatment (<see cref="AiMind.HealLegsAsk"/>) and the serial step
    /// starts it on the character itself.
    /// </summary>
    HealLegs = 27,
    /// <summary>
    /// A goal an engine system or a code mod acts out (a type no entry of this table acts out, registered by that system or mod,
    /// <see cref="DutyGoals"/>): it stands on the goal's row (a code mod's type on row 14 Idleness) while the serial step asks for the act-out's start
    /// (<see cref="AiMind.DutyAsk"/>, <see cref="IDutyGoal.Start"/>), and the goal then holds the character (<see cref="AiMind.Duty"/>).
    /// </summary>
    Duty = 29,
    /// <summary>
    /// 129 Approaching fast (<c>Task_MakeAnnouncement</c>, a campaign leader's goal at its package target): it stands while the serial step has
    /// the campaign's announcement said (<see cref="AiMind.AnnounceAsk"/>, <see cref="Population.PopulationSystem.Announce"/>).
    /// </summary>
    Announce = 30,
}

/// <summary>
/// The engine's table from <see cref="TaskType"/> to <see cref="AiBehaviour"/>, and the TaskData display text (docs/game/ai-tasks.md "TaskData
/// registry", Verified) of the types it acts out.
/// </summary>
public static class TaskBehaviours
{
    /// <summary>
    /// The behaviour and the game's display text of every goal type the engine acts out.
    /// </summary>
    static readonly Dictionary<TaskType, (AiBehaviour Behaviour, string Text)> Table = new()
    {
        [TaskType.Idle] = (AiBehaviour.Mill, "Idleness"),
        // 62 Standing still (Task_StandStill): a request first aid files on the patient's blackboard (BoardRequests); it stands where it is.
        [TaskType.StandStill] = (AiBehaviour.Mill, "Standing still"),
        [TaskType.Wanderer] = (AiBehaviour.ToTown, "Wandering"),
        [TaskType.WanderTown] = (AiBehaviour.TownNode, "Wandering town"),
        [TaskType.PatrolTown] = (AiBehaviour.PatrolTown, "Patrolling town"),
        [TaskType.Patrol] = (AiBehaviour.Patrol, "Patrolling"),
        [TaskType.RelaxInTownPackage] = (AiBehaviour.Relax, "Relaxing"),
        [TaskType.Shopping] = (AiBehaviour.Shop, "Shopping"),
        [TaskType.StandAtShopkeeperNode] = (AiBehaviour.Post, "Shopkeeper"),
        [TaskType.StandAtGuardNodeHomebuilding] = (AiBehaviour.Post, "Static guard"),
        [TaskType.StandAtGuardNodeHomebuildingIndoorsOnly] = (AiBehaviour.Post, "Static guard indoors"),
        [TaskType.StandAtGuardNodeHometownOutside] = (AiBehaviour.Post, "Static town guard"),
        [TaskType.StandAtGeneralNode] = (AiBehaviour.Post, "general node"),
        [TaskType.HoldPosition] = (AiBehaviour.Post, "Holding position"),
        [TaskType.StayInHome] = (AiBehaviour.Post, "Staying home"),
        [TaskType.ManATurret] = (AiBehaviour.Turret, "Turret guard"),
        [TaskType.ManATurretOnBuilding] = (AiBehaviour.Turret, "Turret home guard"),
        [TaskType.ManATurretPlayerJob] = (AiBehaviour.Turret, "Turret guard"),
        [TaskType.UseTurret] = (AiBehaviour.Turret, "Turret gunner"),
        [TaskType.SitOnThrone] = (AiBehaviour.Sit, "Reigning"),
        [TaskType.FollowSquadleader] = (AiBehaviour.Follow, "Following leader"),
        [TaskType.FollowSlavemaster] = (AiBehaviour.Follow, "Following master"),
        // 45 Bodyguard is Task_Blank (FUN_14032ebd0): with nobody threatening its target or itself it neither acts nor has an effect and is dropped,
        // else it plans row 13 Attacking attackers of its target, as the protect goals' fight (AiSystem.Fights.cs). The following of a bodyguard
        // contract is goal 44's.
        [TaskType.Bodyguard] = (AiBehaviour.Fight, "Bodyguard"),
        [TaskType.FollowPlayerOrder] = (AiBehaviour.Follow, "Following"),
        // 253 Following (urgent escape): a Task_Follow like 31 / 44 / 289 (FUN_14032ebd0 case 253, list B !7 60 90 59); one package lists it as a
        // Leader goal.
        [TaskType.FollowUrgentEscape] = (AiBehaviour.Follow, "Following"),
        [TaskType.GoHomeAndGoToBed] = (AiBehaviour.Sleep, "Going to bed"),
        [TaskType.GoHomeAndGoToBedSecure] = (AiBehaviour.Sleep, "Going to bed"),
        [TaskType.SleepOnFloor] = (AiBehaviour.Sleep, "Sleeping on ground"),
        [TaskType.SitAround] = (AiBehaviour.Sit, "Sitting around"),
        [TaskType.AutoLabouringMines] = (AiBehaviour.Work, "Worker"),
        [TaskType.AutoLabouringMinesPretend] = (AiBehaviour.Work, "Worker"),
        [TaskType.RetreatHometown] = (AiBehaviour.GoHome, "Retreating"),
        [TaskType.RunAwayHometown] = (AiBehaviour.GoHome, "Retreating fast"),
        [TaskType.WanderingTrader] = (AiBehaviour.Travel, "Wandering trader"),
        [TaskType.TravelToTargetTown] = (AiBehaviour.Travel, "Travelling"),
        [TaskType.TravelToTargetTownFast] = (AiBehaviour.Travel, "Travelling quickly"),
        // Row 215: the refugee package's walk to its mission, the town or the squad it is to join; under a campaign's war package the formation's
        // walk to the package target (MissionFormation).
        [TaskType.TravelToTargetPackage] = (AiBehaviour.Travel, "Mission formation"),
        [TaskType.MakeAnnouncementFast] = (AiBehaviour.Announce, "Approaching fast"),
        [TaskType.InvestigateAlarms] = (AiBehaviour.Investigate, "Investigating"),
        [TaskType.InvestigateAlarmsAlliesOnly] = (AiBehaviour.Investigate, "Investigating"),
        [TaskType.DefeatSquad] = (AiBehaviour.Assault, "Assault"),
        // 248 Attacking Squad: the same finder as 101 (FUN_140793eb0, stub LAB_140006d25), the same flags and effect; no base package lists it, a mod
        // can.
        [TaskType.DefeatSquadLimitChaseRange] = (AiBehaviour.Assault, "Attacking squad"),
        [TaskType.Crouch] = (AiBehaviour.Crouch, "Crouching"),
        [TaskType.StandUp] = (AiBehaviour.Crouch, "Standing"),
        [TaskType.SelfPreservation] = (AiBehaviour.SelfPreserve, "Self preservation"),
        [TaskType.RunAway] = (AiBehaviour.Flee, "Running away"),
        [TaskType.AttackEnemies] = (AiBehaviour.Fight, "Attacking enemies"),
        [TaskType.AttackEnemiesAndNeutrals] = (AiBehaviour.Fight, "Attacking others"),
        [TaskType.TerritorialAggressionButDontLeaveHome] = (AiBehaviour.Fight, "Home territory aggression"),
        [TaskType.AttackTroubleMakers] = (AiBehaviour.Fight, "Attacking trouble makers"),
        [TaskType.AttackEnemiesOfMySlavemaster] = (AiBehaviour.Fight, "Attacking enemies of my master"),
        [TaskType.ProtectOwnSquad] = (AiBehaviour.Fight, "Protect close allies"),
        [TaskType.ProtectAllies] = (AiBehaviour.Fight, "Protect allies"),
        [TaskType.ProtectAlliesStayInTown] = (AiBehaviour.Fight, "Protect allies"),
        [TaskType.JobMedic] = (AiBehaviour.Medic, "Medic"),
        [TaskType.HealMyLegs] = (AiBehaviour.HealLegs, "Fixing Legs"),
    };

    /// <summary>
    /// The number of task types the flat tables hold (type numbers below it). Flat tables by type number for the think's hot loop (a dictionary
    /// lookup per goal was a third of the think).
    /// </summary>
    internal const int TypeCount = 291;
    /// <summary>
    /// The behaviour of each type by type number (<see cref="AiBehaviour.None"/> for types the engine does not act out).
    /// </summary>
    static readonly AiBehaviour[] Behaviours = BuildBehaviours();

    /// <summary>
    /// Builds <see cref="Behaviours"/> from <see cref="Table"/>.
    /// </summary>
    static AiBehaviour[] BuildBehaviours()
    {
        var a = new AiBehaviour[TypeCount];
        foreach (var (type, e) in Table) a[(int)type] = e.Behaviour;
        return a;
    }

    /// <summary>
    /// The behaviour the engine acts a goal type out with (<see cref="AiBehaviour.None"/>: not acted out).
    /// </summary>
    public static AiBehaviour Of(TaskType type) => (uint)type < TypeCount ? Behaviours[(int)type] : AiBehaviour.None;

    /// <summary>
    /// The game's display text of a type the engine acts out; for the rest its TaskData row's (an engine-acted goal's,
    /// <see cref="DutyGoals"/>), else the enum name.
    /// </summary>
    public static string Text(TaskType type)
    {
        if (Table.TryGetValue(type, out var e)) return e.Text;
        if (type == TaskType.StumbleTaskForced) return "Stumbling";

        return TaskDataTable.Of(type)?.Text ?? type.ToString();
    }

    /// <summary>
    /// Every type the engine acts out.
    /// </summary>
    public static IEnumerable<TaskType> Built => Table.Keys;
}