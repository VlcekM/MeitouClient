using Meitou.Data.Gameplay.Ai;

namespace Meitou.Simulation.Ai;

/// <summary>
/// The scoring numbers of a goal type's TaskData (docs/game/ai-tasks.md "Desire and commitment", Observed): its desire, how long a character sticks with it once
/// chosen (game hours, <see cref="CommitMin"/> plus a uniform share of <see cref="CommitRange"/>, only when <see cref="Commits"/>), and its crowding factor.
/// </summary>
/// <param name="Desire">The desire: a fixed value (0 or more), <see cref="TaskProfiles.Random"/> (0.01 + 0.4 x a fresh roll) or <see cref="TaskProfiles.Special"/>
/// (a TaskData function of its own; the engine's rule for the behaviour).</param>
public readonly record struct TaskProfile(float Desire, float CommitMin = 0, float CommitRange = 0, bool Commits = false, float Crowd = 1);

/// <summary>The <see cref="TaskProfile"/> of every goal type the engine acts out (<see cref="TaskBehaviours"/>); other types get the default (special, no commitment, crowd 1).</summary>
public static class TaskProfiles
{
    public const float Random = -1, Special = -2;

    static readonly TaskProfile Default = new(Special);

    static readonly Dictionary<TaskType, TaskProfile> Table = new()
    {
        [TaskType.IDLE] = new(0.1f),
        [TaskType.WANDERER] = new(1),
        [TaskType.PATROL] = new(0.25f),
        [TaskType.PATROL_TOWN] = new(0.25f),
        [TaskType.WANDER_TOWN] = new(Random, 0.25f, 1.5f, true),
        [TaskType.RELAX_IN_TOWN_PACKAGE] = new(1),
        [TaskType.SHOPPING] = new(Special, 0.25f, 1.5f, true),
        [TaskType.STAND_AT_SHOPKEEPER_NODE] = new(0.5f),
        [TaskType.STAND_AT_GUARD_NODE_HOMEBUILDING] = new(0.5f, 0.1f, 0.5f, true),
        [TaskType.STAND_AT_GUARD_NODE_HOMEBUILDING_INDOORS_ONLY] = new(0.5f, 0.1f, 0.5f, true),
        [TaskType.STAND_AT_GUARD_NODE_HOMETOWN_OUTSIDE] = new(Random, 0.1f, 1, true),
        [TaskType.STAND_AT_GENERAL_NODE] = new(0.5f, 0.03f, 0.1f, true),
        [TaskType.HOLD_POSITION] = new(1),
        [TaskType.STAY_IN_HOME] = new(Random, 0.2f, 0.8f, true),
        [TaskType.SIT_AROUND] = new(Random, 0.2f, 0.8f, true),
        [TaskType.SIT_ON_THRONE] = new(Special, 0.2f, 0.8f, true),
        // Worker: AI slot 19 (1); only the pretend row commits, 0.5 + r x 2 hours (docs/game/ai-tasks.md "Desire and commitment").
        [TaskType.AUTO_LABOURING_MINES] = new(1),
        [TaskType.AUTO_LABOURING_MINES_PRETEND] = new(1, 0.5f, 2, true),
        [TaskType.MAN_THE_GATE] = new(1),
        [TaskType.FOLLOW_SLAVEMASTER] = new(1),
        [TaskType.BODYGUARD] = new(Special, Crowd: 0.9f),
        // Medic: the desire function's 1 times the finder's score (FUN_140998410); crowd factor 0.8 (FUN_140319a60, Observed).
        [TaskType.JOB_MEDIC] = new(Special, Crowd: 0.8f),
        [TaskType.DEFEAT_SQUAD_LIMIT_CHASE_RANGE] = new(Special, 0.3f, 2, true),   // 248 Attacking Squad (FUN_140319a60: commitment 0.3 + rand x 2, +0x20 set)
        [TaskType.GO_HOME_AND_GO_TO_BED] = new(Special, 2, 8, true),
        [TaskType.GO_HOME_AND_GO_TO_BED_SECURE] = new(Special, 2, 8, true),
        [TaskType.RUN_AWAY_HOMETOWN] = new(Special, 0.05f, 0.15f, true),   // 127 Retreating fast (FUN_140319a60: 0.05 + 0.15 r, +0x20 set, a2-003)
        // Home territory aggression: AI slot 41, 0.5 (docs/game/ai-tasks.md "Combat goals", Observed). 16, 21, 34, 143 desire 1 (slot 19, 16's own zeros apart); 15 / 288 their own (AiSystem.Fights.cs).
        [TaskType.TERRITORIAL_AGGRESSION_BUT_DONT_LEAVE_HOME] = new(0.5f),
    };

    // Flat by type number for the think's hot loop.
    static readonly TaskProfile[] Flat = BuildFlat();

    static TaskProfile[] BuildFlat()
    {
        var a = new TaskProfile[TaskBehaviours.TypeCount];
        Array.Fill(a, Default);
        foreach (var (type, p) in Table) a[(int)type] = p;
        return a;
    }

    public static TaskProfile Of(TaskType type) => (uint)type < (uint)Flat.Length ? Flat[(int)type] : Default;
}

/// <summary>
/// A TaskData row's intended aggression (field +0x24, <b>Verified</b>: read from the registrations <c>FUN_140319a60</c>): what the AI's state broadcast shows of the current goal (status
/// +0xac, <c>getIntendedAggression</c>) and of the running task (+0xa0). 1 for the attack rows, 0.5 for 13 Attacking attackers of, 0 for the protect goals (15, 143, 288), 34, 32 and most
/// rows. The negative values of the medic, lockpicking and repair rows are not transcribed (only 35's -0.5): the readers here test above 0.
/// </summary>
public static class TaskAggression
{
    public static float Of(TaskType type) => type switch
    {
        TaskType.MELEE_ATTACK or TaskType.MELEE_ATTACK_ANIMAL or TaskType.RANGED_ATTACK or TaskType.RANGED_ATTACK_FOCUSED or TaskType.RANGED_ATTACK_FOCUSED_UNPROVOKED
            or TaskType.STEALTH_KNOCKOUT or TaskType.UNPROVOKED_FOCUSED_MELEE_ATTACK or TaskType.DEFEAT_SQUAD or TaskType.DEFEAT_SQUAD_LIMIT_CHASE_RANGE or TaskType.ATTACK_ENEMIES
            or TaskType.ATTACK_ENEMIES_AND_NEUTRALS or TaskType.TERRITORIAL_AGGRESSION_BUT_DONT_LEAVE_HOME or TaskType.ATTACK_ENEMIES_OF_MY_SLAVEMASTER or TaskType.USE_TURRET
            or TaskType.SHOOT_AT_TARGET or TaskType.LOOT_FOOD_AND_STUFF or TaskType.GRAB_ONE_FOOD => 1,
        TaskType.ATTACK_ATTACKERS_OF => 0.5f,
        TaskType.RUN_AWAY => -0.5f,
        _ => 0,
    };

    /// <summary>
    /// The intended aggression of a character's current goal (status +0xac): in a fight its row (16 for a goal's or an NPC's attack, 13 for a defence), else its AI goal, else its running row.
    /// Engine reading of "current goal" for Meitou's split of goals and engine starts.
    /// </summary>
    public static float OfGoal(CharacterCold c) =>
        c.InCombat && c.Running is { } fight ? Of(fight.Row) : c.Ai?.Goal is { } goal ? Of(goal.Task.Type) : c.Running is { } run ? Of(run.Row) : 0;

    /// <summary>The intended aggression of a character's running task (status +0xa0).</summary>
    public static float OfTask(CharacterCold c) => c.Running is { } run ? Of(run.Row) : 0;
}
