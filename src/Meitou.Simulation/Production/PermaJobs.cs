using Meitou.Data.Gameplay.Ai;
using Meitou.Data.Save;
using Meitou.Simulation.Aggro;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Player;

namespace Meitou.Simulation.Production;

/// <summary>
/// One entry of a character's job list (Perma-Jobs, docs/game/ai-tasks.md "Perma-jobs"): a task type and its target, a player building
/// (<see cref="Construction.BuildSite.Id"/>) or, for a job whose target is a character (Bodyguard 45, Staying close 31: <see cref="Character"/>), the
/// saved handle, kept so the job is written back as it was and the character found again after a load.
/// </summary>
public sealed record PermaJob(TaskType Type, string? Site, Hand Saved)
{
    /// <summary>
    /// A job on player building <paramref name="site"/>, with no saved handle.
    /// </summary>
    public PermaJob(TaskType type, string site) : this(type, site, Hand.Null) { }

    /// <summary>
    /// The character a Bodyguard or Staying close job follows (<see cref="CharacterId.None"/>: none, or a loaded one not matched to its saved handle
    /// yet).
    /// </summary>
    public CharacterId Character { get; init; } = CharacterId.None;

    /// <summary>
    /// A job on a character.
    /// </summary>
    public static PermaJob OfCharacter(TaskType type, CharacterId target, Hand? saved = null) =>
        new(type, null, saved ?? Hand.Null) { Character = target };

    /// <summary>
    /// A job without a target (the medic and rescue jobs).
    /// </summary>
    public static PermaJob Untargeted(TaskType type) => new(type, null, Hand.Null);
}

/// <summary>
/// The rules of adding a perma-job (<b>Verified</b>, the add function <c>FUN_1405086d0</c> and the TaskData rows of <c>FUN_140319a60</c>;
/// docs/game/ai-tasks.md "Perma-jobs"): the ordered type becomes its job type, a second job of a type is refused (or only for the same target when
/// the type allows several), a companion job follows, and the jobs switch turns on.
/// </summary>
public static class PermaJobs
{
    /// <summary>
    /// A TaskData row's job fields: +4 <paramref name="Kind"/> (2: also runs before urgent tasks), +8 <paramref name="PerTarget"/>, +0xc
    /// <paramref name="Job"/>, +0x10 <paramref name="Companion"/> (0: none).
    /// </summary>
    readonly record struct Rule(int Kind, bool PerTarget, TaskType Job, TaskType Companion);

    /// <summary>
    /// The job fields of each TaskData row that makes a job.
    /// </summary>
    static readonly Dictionary<TaskType, Rule> Rules = new()
    {
        [TaskType.OperateMachinery] = new(1, true, 0, 0),
        [TaskType.PretendToOperateMachinery] = new(1, true, 0, 0),
        [TaskType.OperateAutomaticMachinery] = new(1, true, TaskType.OperateAutomaticMachinery, TaskType.OperateStorage),
        [TaskType.OperateStorage] = new(1, true, 0, 0),
        [TaskType.Repair] = new(1, false, TaskType.JobBuilder, 0),
        [TaskType.JobBuilder] = new(1, false, 0, 0),
        [TaskType.Build] = new(1, false, TaskType.JobBuilder, 0),
        [TaskType.FirstAidOrder] = new(1, false, TaskType.JobMedic, 0),
        [TaskType.SplintOrder] = new(1, false, TaskType.SplintJob, 0),
        [TaskType.FirstAidRobot] = new(1, false, TaskType.JobRepairRobot, 0),
        [TaskType.JobMedic] = new(1, false, 0, 0),
        [TaskType.SplintJob] = new(1, false, 0, 0),
        [TaskType.JobRepairRobot] = new(1, false, 0, 0),
        [TaskType.LootTarget] = new(1, false, TaskType.LootAnimalsJob, 0),
        [TaskType.LootAnimalsJob] = new(1, false, 0, 0),
        [TaskType.MeleeAttack] = new(1, false, TaskType.AttackEnemies, 0),
        [TaskType.UseTurret] = new(2, false, TaskType.ManATurretPlayerJob, 0),
        [TaskType.ManATurretPlayerJob] = new(2, true, 0, 0),
        [TaskType.AttackEnemies] = new(1, false, 0, 0),
        [TaskType.Bodyguard] = new(1, true, TaskType.Bodyguard, TaskType.StayCloseToTarget),
        [TaskType.StayCloseToTarget] = new(1, true, 0, 0),
        [TaskType.Unnamed289] = new(1, true, 0, 0),
        [TaskType.SlaveObedience] = new(1, false, 0, 0),
        [TaskType.FocusedMeleeAttack] = new(1, false, TaskType.AttackEnemies, 0),
        [TaskType.UnprovokedFocusedMeleeAttack] = new(1, false, TaskType.AttackEnemies, 0),
        [TaskType.UseTrainingDummy] = new(1, true, 0, 0),
        [TaskType.UseBed] = new(1, false, TaskType.GoHomeAndGoToBed, 0),
        [TaskType.SleepOnFloor] = new(1, false, TaskType.GoHomeAndGoToBed, 0),
        [TaskType.SleepOnFloorFakeAmbush] = new(1, false, TaskType.GoHomeAndGoToBed, 0),
        [TaskType.FindAndRescueIfTheresBeds] = new(1, false, TaskType.FindAndRescueIfTheresBeds, TaskType.FindBedAndPutIn),
        [TaskType.FindBedAndPutIn] = new(1, false, 0, 0),
        [TaskType.FeedCorpseIntoMachine] = new(1, true, TaskType.DeadGuysGoInThePot, TaskType.FindADeadGuy),
        [TaskType.FindADeadGuy] = new(1, false, 0, 0),
        [TaskType.DeadGuysGoInThePot] = new(1, true, TaskType.DeadGuysGoInThePot, TaskType.FindADeadGuy),
        [TaskType.PickupIntruders] = new(1, false, TaskType.PickupIntruders, TaskType.TakeIntruderOutside),
        [TaskType.PickupIntrudersTown] = new(1, false, TaskType.PickupIntrudersTown, TaskType.TakeIntruderOutsideTown),
    };

    /// <summary>
    /// The types the production pick carries out from a list (<see cref="ProductionSystem"/>; the engineer job through the construction system).
    /// Every job runs through the decision step (<see cref="DecisionSystem.RegisterJob"/>), whose contributors are: these and Bodyguard 45 and
    /// Staying close 31 / 289 (a follow of the job's character) and the carrying jobs (corpse disposal 137 / 138, intruders 223 / 273 with 224 / 274)
    /// production, medic 58, robotics 57 and splinting 250 <see cref="FirstAid.FirstAidSystem"/>, rescue 148 and foraging 238
    /// <see cref="Carrying.CarrySystem"/>, put in bed 106 <see cref="Locks.BedSystem"/>, attacking enemies 16 <see cref="AggroSystem"/>, Obedient
    /// Slave 187 <see cref="Kidnap.CaptiveSystem"/>. The rest are kept, shown and saved but never started.
    /// </summary>
    public static bool Runs(TaskType type) =>
        type is TaskType.OperateMachinery or TaskType.OperateAutomaticMachinery or TaskType.OperateStorage or TaskType.JobBuilder
            or TaskType.ManATurretPlayerJob or TaskType.UseTrainingDummy;

    /// <summary>
    /// The provenance of the production pick's job the character's <paramref name="task"/> runs: a list job it carries out (<see cref="Runs"/>, the
    /// follow jobs <see cref="Follows"/>; its index <see cref="RunningTask.Job"/>) or an implicit job (<see cref="RunningTask.Implicit"/>), not a
    /// fight that is a step of a follow row; null for any other task.
    /// </summary>
    public static RunningTask? ListJob(CharacterCold cold, CharacterTask task) =>
        task != CharacterTask.Attack && cold.Running is { Source: TaskSource.Job } run && run.Runs(task)
            && (run.Implicit || run.Job >= 0 && (Runs(run.Row) || Follows(run.Row)))
                ? run
                : null;

    /// <summary>
    /// The index of the first job of <paramref name="type"/> in the list, -1 when none.
    /// </summary>
    public static int IndexOf(CharacterCold cold, TaskType type) => cold.Jobs.FindIndex(j => j.Type == type);

    /// <summary>
    /// The job an order of <paramref name="ordered"/> becomes (<b>Verified</b>: the row's +0xc when it has one), or null when it cannot be a job and
    /// the order runs once: the type has no row, or the type it becomes has none (beds become 153, which is no job).
    /// </summary>
    public static TaskType? JobOf(TaskType ordered)
    {
        if (!Rules.TryGetValue(ordered, out var rule)) return null;
        var type = rule.Job != 0 ? rule.Job : ordered;
        return Rules.ContainsKey(type) ? type : null;
    }

    /// <summary>
    /// Adds the job an order of <paramref name="ordered"/> on <paramref name="target"/> becomes to the end of <paramref name="jobs"/>, then its
    /// companion (unchecked, as the original), and turns the jobs switch on. False when the type is no job or the list refuses it: a job of the type
    /// is there and the type allows one (+8 clear), or the same job on the same target is.
    /// </summary>
    public static bool Add(CharacterCold cold, TaskType ordered, PermaJob target)
    {
        if (JobOf(ordered) is not { } type) return false;
        var rule = Rules[type];
        foreach (var job in cold.Jobs)
            if (job.Type == type && (!rule.PerTarget || SameTarget(job, target))) return false;
        cold.Jobs.Add(target with { Type = type });

        // The companion's own row decides nothing here; 31 becomes 289 for an animal receiver (vfunc +0x248, asAnimal: FUN_1405e1f50 returns the
        // animal itself, FUN_1405e5950 of a person 0).
        if (rule.Companion != 0)
        {
            cold.Jobs.Add(target with { Type = rule.Companion == TaskType.StayCloseToTarget && SquadRegistry.IsAnimal(cold)
                ? TaskType.Unnamed289
                : rule.Companion });
        }

        cold.Orders |= CharacterOrders.Jobs;
        DecisionSystem.Request(cold);   // the job list changed: decided again
        return true;
    }

    /// <summary>
    /// A job the player gives (a button, Shift with an order): <see cref="Add"/>, except that an animal takes only the jobs of its own orders
    /// (<c>FUN_1407f23b0</c>'s set: attacking enemies, bodyguard, staying close), so a mixed selection's animals get no Medic, Rescue or machine jobs
    /// they never work. <b>Engine rule</b>: the original's adder (<c>FUN_1407f5a90</c>) shows no such test (<b>Observed</b>).
    /// </summary>
    public static bool Give(CharacterCold cold, TaskType ordered, PermaJob target) =>
        (!SquadRegistry.IsAnimal(cold) || JobOf(ordered) is TaskType.AttackEnemies or TaskType.Bodyguard or TaskType.StayCloseToTarget)
            && Add(cold, ordered, target);

    /// <summary>
    /// Whether two jobs have the same target: the same character when either follows one, else the same site, else the same saved handle.
    /// </summary>
    static bool SameTarget(PermaJob a, PermaJob b) =>
        !a.Character.IsNone || !b.Character.IsNone
            ? a.Character == b.Character
            : a.Site is not null || b.Site is not null ? a.Site == b.Site : a.Saved == b.Saved;

    /// <summary>
    /// The jobs that follow their character: Bodyguard 45, Staying close 31 and its animal form 289 (<c>FUN_14032ebd0</c> builds the same
    /// <c>Task_Follow</c> for 31 and 289).
    /// </summary>
    public static bool Follows(TaskType type) => type is TaskType.Bodyguard or TaskType.StayCloseToTarget or TaskType.Unnamed289;

    /// <summary>
    /// Whether the type's TaskData row has +8 set (a job per target): the jobs list then names the target after its text.
    /// </summary>
    public static bool PerTarget(TaskType type) => Rules.TryGetValue(type, out var rule) && rule.PerTarget;

    /// <summary>
    /// The types that run with the jobs switch off (<b>Verified</b>, <c>FUN_14050c1c0</c>): attacking enemies, staying close, bodyguard, robot
    /// repair, medic, splinting. Not 289, the animals' staying close.
    /// </summary>
    public static bool IgnoresSwitch(TaskType type) =>
        type is TaskType.AttackEnemies or TaskType.StayCloseToTarget or TaskType.Bodyguard or TaskType.JobRepairRobot or TaskType.JobMedic
            or TaskType.SplintJob;

    /// <summary>
    /// The row's display text (TaskData +0x138, <c>FUN_140319a60</c>) for the jobs list; null when Meitou does not know it.
    /// </summary>
    public static string? Text(TaskType type) => type switch
    {
        TaskType.OperateMachinery or TaskType.PretendToOperateMachinery => "Operating machine",
        TaskType.OperateAutomaticMachinery => "Operating automatic machine",
        TaskType.OperateStorage => "Hauling to",
        TaskType.JobBuilder => "Engineer",
        TaskType.JobMedic => "Medic",
        TaskType.SplintJob => "Splint Rigging",
        TaskType.JobRepairRobot => "Robotics",
        TaskType.LootAnimalsJob => "Foraging animals",
        TaskType.ManATurretPlayerJob => "Turret guard",
        TaskType.AttackEnemies => "Attacking enemies",
        TaskType.Bodyguard => "Bodyguard",
        TaskType.StayCloseToTarget or TaskType.Unnamed289 => "Staying close",
        TaskType.SlaveObedience => "Obedient Slave",
        TaskType.UseTrainingDummy => "Training",
        TaskType.FindAndRescueIfTheresBeds => "Find and rescue",
        TaskType.FindBedAndPutIn => "Find and put in bed",
        TaskType.FindADeadGuy => "Find corpses",
        TaskType.DeadGuysGoInThePot => "Corpse disposal",
        TaskType.PickupIntruders => "Remove Home Intruder",
        TaskType.PickupIntrudersTown => "Remove Intruder",
        _ => null,
    };
}