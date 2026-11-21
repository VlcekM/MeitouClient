using Meitou.Data.Gameplay.Ai;

namespace Meitou.Simulation.Ai;

/// <summary>
/// The scoring numbers of a goal type's TaskData (docs/game/ai-tasks.md "Desire and commitment", Observed): its desire, how long a character sticks
/// with it once chosen (game hours, <see cref="CommitMin"/> plus a uniform share of <see cref="CommitRange"/>, only when <see cref="Commits"/>), and
/// its crowding factor.
/// </summary>
/// <param name="Desire">The desire: a fixed value (0 or more), <see cref="TaskProfiles.Random"/> (0.01 + 0.4 x a fresh roll) or
/// <see cref="TaskProfiles.Special"/> (a TaskData function of its own; the engine's rule for the behaviour).</param>
/// <param name="EndsWithTask">TaskData <c>+0x21</c>: a committing row whose commitment ends with its task's timer (<see cref="AiMind.TaskCommitUntil"/>),
/// the running task ended and a replan asked (<c>FUN_14050f0b0</c>, <b>Verified</b>); without it only the goal's timer ends it.</param>
public readonly record struct TaskProfile(float Desire, float CommitMin = 0, float CommitRange = 0, bool Commits = false, float Crowd = 1,
    bool EndsWithTask = false);

/// <summary>
/// The <see cref="TaskProfile"/> of every goal type the engine acts out (<see cref="TaskBehaviours"/>); other types get the default (special, no
/// commitment, crowd 1).
/// </summary>
public static class TaskProfiles
{
    /// <summary>
    /// The desire stand-ins: a random desire (0.01 + 0.4 x a fresh roll) and a special one (a TaskData function of its own).
    /// </summary>
    public const float Random = -1, Special = -2;

    /// <summary>
    /// The profile of a type not in <see cref="Table"/>: special desire, no commitment, crowd 1.
    /// </summary>
    static readonly TaskProfile Default = new(Special);

    /// <summary>
    /// The profile of every goal type the engine acts out.
    /// </summary>
    static readonly Dictionary<TaskType, TaskProfile> Table = new()
    {
        [TaskType.Idle] = new(0.1f),
        [TaskType.Wanderer] = new(1),
        [TaskType.Patrol] = new(0.25f),
        [TaskType.PatrolTown] = new(0.25f),
        // The committing rows with TaskData +0x21 (FUN_140319a60, Verified: the short 0x101 at +0x20) end with their task's timer (EndsWithTask).
        [TaskType.WanderTown] = new(Random, 0.25f, 1.5f, true, EndsWithTask: true),
        [TaskType.RelaxInTownPackage] = new(1),
        [TaskType.Shopping] = new(Special, 0.25f, 1.5f, true, EndsWithTask: true),
        [TaskType.StandAtShopkeeperNode] = new(0.5f),
        [TaskType.StandAtGuardNodeHomebuilding] = new(0.5f, 0.1f, 0.5f, true, EndsWithTask: true),
        [TaskType.StandAtGuardNodeHomebuildingIndoorsOnly] = new(0.5f, 0.1f, 0.5f, true, EndsWithTask: true),
        [TaskType.StandAtGuardNodeHometownOutside] = new(Random, 0.1f, 1, true, EndsWithTask: true),
        [TaskType.StandAtGeneralNode] = new(0.5f, 0.03f, 0.1f, true, EndsWithTask: true),
        [TaskType.HoldPosition] = new(1),
        [TaskType.StayInHome] = new(Random, 0.2f, 0.8f, true, EndsWithTask: true),
        [TaskType.SitAround] = new(Random, 0.2f, 0.8f, true, EndsWithTask: true),
        [TaskType.SitOnThrone] = new(Special, 0.2f, 0.8f, true, EndsWithTask: true),
        // Worker: AI slot 19 (1); only the pretend row commits, 0.5 + r x 2 hours (docs/game/ai-tasks.md "Desire and commitment"), as 221 Operating
        // machine, a step of plans (FUN_140319a60, Verified).
        [TaskType.AutoLabouringMines] = new(1),
        [TaskType.AutoLabouringMinesPretend] = new(1, 0.5f, 2, true, EndsWithTask: true),
        [TaskType.PretendToOperateMachinery] = new(Special, 0.5f, 2, true, EndsWithTask: true),
        [TaskType.FollowSlavemaster] = new(1),
        [TaskType.Bodyguard] = new(Special, Crowd: 0.9f),
        // Medic: the desire function's 1 times the finder's score (FUN_140998410); crowd factor 0.8 (FUN_140319a60, Observed).
        [TaskType.JobMedic] = new(Special, Crowd: 0.8f),
        // 248 Attacking Squad (FUN_140319a60: commitment 0.3 + rand x 2, +0x20 and +0x21 set)
        [TaskType.DefeatSquadLimitChaseRange] = new(Special, 0.3f, 2, true, EndsWithTask: true),
        [TaskType.GoHomeAndGoToBed] = new(Special, 2, 8, true, EndsWithTask: true),
        [TaskType.GoHomeAndGoToBedSecure] = new(Special, 2, 8, true, EndsWithTask: true),
        [TaskType.RunAwayHometown] = new(Special, 0.05f, 0.15f, true),   // 127 Retreating fast (FUN_140319a60: 0.05 + 0.15 r, +0x20 set, +0x21 clear, a2-003)
        // 35 Running away, a goal of its own (FUN_140319a60: +0x18 0.05, +0x1c 0.1, +0x20 set, +0x21 clear; 280 registers the same, but is only ever an order).
        [TaskType.RunAway] = new(Special, 0.05f, 0.1f, true),
        // Home territory aggression: AI slot 41, 0.5 (docs/game/ai-tasks.md "Combat goals", Observed). 21 and 34 desire 1 (slot 19), 16 slot 52 (1
        // but its zeros); 15 / 288 / 143 slot 14 (AiSystem.Fights.cs).
        [TaskType.TerritorialAggressionButDontLeaveHome] = new(0.5f),
    };

    /// <summary>
    /// Flat by type number for the think's hot loop.
    /// </summary>
    static readonly TaskProfile[] Flat = BuildFlat();

    /// <summary>
    /// Builds <see cref="Flat"/>: <see cref="Default"/> for every type, then the table's entries.
    /// </summary>
    static TaskProfile[] BuildFlat()
    {
        var a = new TaskProfile[TaskBehaviours.TypeCount];
        Array.Fill(a, Default);
        foreach (var (type, p) in Table) a[(int)type] = p;
        return a;
    }

    /// <summary>
    /// The profile of a goal type (<see cref="Default"/> for one out of range).
    /// </summary>
    public static TaskProfile Of(TaskType type) => (uint)type < (uint)Flat.Length ? Flat[(int)type] : Default;
}

/// <summary>
/// A TaskData row's intended aggression (field +0x24, <b>Verified</b>: read from the registrations <c>FUN_140319a60</c>): what the AI's state
/// broadcast shows of the current goal (status +0xac, <c>getIntendedAggression</c>) and of the running task (+0xa0). 1 for the attack rows, 0.5 for
/// 13 Attacking attackers of, 0 for the protect goals (15, 143, 288), 34, 32 and most rows. The negative values of the medic, lockpicking and repair
/// rows are not transcribed (only 35's -0.5): the readers here test above 0.
/// </summary>
public static class TaskAggression
{
    /// <summary>
    /// The intended aggression of a row type.
    /// </summary>
    public static float Of(TaskType type) => type switch
    {
        TaskType.MeleeAttack or TaskType.MeleeAttackAnimal or TaskType.RangedAttack or TaskType.RangedAttackFocused
            or TaskType.RangedAttackFocusedUnprovoked or TaskType.StealthKnockout or TaskType.UnprovokedFocusedMeleeAttack
            or TaskType.DefeatSquad or TaskType.DefeatSquadLimitChaseRange or TaskType.AttackEnemies
            or TaskType.AttackEnemiesAndNeutrals or TaskType.TerritorialAggressionButDontLeaveHome
            or TaskType.AttackEnemiesOfMySlavemaster or TaskType.UseTurret or TaskType.ShootAtTarget or TaskType.LootFoodAndStuff
            or TaskType.GrabOneFood => 1,
        TaskType.AttackAttackersOf => 0.5f,
        TaskType.RunAway => -0.5f,
        _ => 0,
    };

    /// <summary>
    /// The intended aggression of a character's current goal (status +0xac): in a fight its row (16 for a goal's or an NPC's attack, 13 for a
    /// defence), else its AI goal, else its running row. Engine reading of "current goal" for Meitou's split of goals and engine starts.
    /// </summary>
    public static float OfGoal(CharacterCold c) =>
        c.InCombat && c.Running is { } fight ? Of(fight.Row) : c.Ai?.Goal is { } goal ? Of(goal.Task.Type) : c.Running is { } run ? Of(run.Row) : 0;

    /// <summary>
    /// The intended aggression of a character's running task (status +0xa0).
    /// </summary>
    public static float OfTask(CharacterCold c) => c.Running is { } run ? Of(run.Row) : 0;
}
