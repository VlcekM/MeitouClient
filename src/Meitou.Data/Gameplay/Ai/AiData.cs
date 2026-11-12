using Meitou.Data.Fcs;

namespace Meitou.Data.Gameplay.Ai;

/// <summary>
/// The editor's <c>taskPriority</c>, an AI_TASK's <c>classification</c> (docs/game/ai.md "Enums", Verified): the tier the chooser scores a goal in.
/// The data uses 1 to 3.
/// </summary>
public enum TaskPriority
{
    /// <summary>
    /// <c>TP_JUST_ACTION</c>; no record uses it.
    /// </summary>
    JustAction = 0,
    /// <summary>
    /// <c>TP_FLUFF</c> (5 records of the base game).
    /// </summary>
    Fluff = 1,
    /// <summary>
    /// <c>TP_NON_URGENT</c> (82 records).
    /// </summary>
    NonUrgent = 2,
    /// <summary>
    /// <c>TP_URGENT</c> (37 records).
    /// </summary>
    Urgent = 3,
    /// <summary>
    /// <c>TP_OBEDIENCE</c>; no record uses it.
    /// </summary>
    Obedience = 4
}

/// <summary>
/// <c>TaskTargetType</c>, an AI_TASK's <c>targeting</c>, numbered in the order <c>fcs_enums.def</c> lists the names (docs/game/ai.md "AI_TASK"; the
/// numbering is Observed).
/// </summary>
public enum TaskTargetType
{
    /// <summary>
    /// <c>SPECIFIC</c> (99 records): no hand set at load.
    /// </summary>
    Specific = 0,
    /// <summary>
    /// <c>SELF</c> (11 records): no hand set at load; the character's own hand is filled in where the task is created.
    /// </summary>
    Self = 1,
    /// <summary>
    /// <c>LEADER</c> (4 records): the squad leader's hand (Observed).
    /// </summary>
    Leader = 2,
    /// <summary>
    /// <c>SQUAD_MISSION</c> (9 records): the Blackboard's mission target (Observed).
    /// </summary>
    SquadMission = 3,
    /// <summary>
    /// <c>HOME_GATE</c> (1 record): a Blackboard lookup (Observed).
    /// </summary>
    HomeGate = 4
}

/// <summary>
/// <c>TaskEndEvent</c>, an AI_TASK's <c>ending</c> (behaviour Unknown).
/// </summary>
public enum TaskEndEvent
{
    /// <summary>
    /// <c>NOTHING</c> (115 records).
    /// </summary>
    Nothing = 0,
    /// <summary>
    /// <c>REMOVE_MINE_ONLY</c> (8 records).
    /// </summary>
    RemoveMineOnly = 1,
    /// <summary>
    /// <c>REMOVE_WHOLE_SQUADS</c> (1 record).
    /// </summary>
    RemoveWholeSquads = 2
}

/// <summary>
/// The Blackboard's role lists, by index (docs/game/ai.md "Goal loading", Verified): a member takes the list of its squad role.
/// </summary>
public enum AiRole
{
    /// <summary>
    /// Filled from <c>Squad AI Goals</c>, else <c>Leader AI Goals</c> (Blackboard +0x1b0).
    /// </summary>
    Squad = 0,
    /// <summary>
    /// Filled from <c>Squad2 AI Goals</c>, else as Squad (Blackboard +0x1c8).
    /// </summary>
    Squad2 = 1,
    /// <summary>
    /// Filled from <c>Leader AI Goals</c> (Blackboard +0x198).
    /// </summary>
    Leader = 2,
    /// <summary>
    /// Never filled by the goal loader, nor cleared when a package activates (Blackboard +0x1e0).
    /// </summary>
    Unused = 3,
    /// <summary>
    /// Filled from <c>Slave AI Goals</c> only (Blackboard +0x1f8).
    /// </summary>
    Slave = 4
}

/// <summary>
/// An AI_TASK record: one <see cref="TaskType"/> with its tier, target and ending (docs/game/ai.md "AI_TASK"). Only int fields exist.
/// </summary>
public sealed record AiTask(string Id, string Name, TaskType Type, TaskPriority Priority, TaskTargetType Target, TaskEndEvent Ending)
{
    /// <summary>
    /// Reads an AI_TASK record's four int fields (<c>enum</c>, <c>classification</c>, <c>targeting</c>, <c>ending</c>; absent ones read 0).
    /// </summary>
    public static AiTask From(GameRecord r) => new(r.StringId, r.Name, (TaskType)r.GetInt("enum"), (TaskPriority)r.GetInt("classification"),
        (TaskTargetType)r.GetInt("targeting"), (TaskEndEvent)r.GetInt("ending"));
}

/// <summary>
/// One goal of a role list: the task, its hour window and its weight (docs/game/ai.md "AI_PACKAGE", Verified). The window is the game's integer hour:
/// start &lt; end means start &lt;= h &lt; end, start &gt;= end wraps midnight (so (0, 0) is always open); 100 as the end means no limit.
/// </summary>
public sealed record AiGoal(AiTask Task, int Start, int End, float Weight)
{
    /// <summary>
    /// Whether the integer game <paramref name="hour"/> is inside the goal's window (wrapping midnight when Start &gt;= End).
    /// </summary>
    public bool InWindow(int hour) => Start < End ? hour >= Start && hour < End : hour >= Start || hour < End;
}

/// <summary>
/// An AI_PACKAGE with its <c>inherits from</c> chain resolved (docs/game/ai.md "Goal loading"): the five role lists, the signal that picks the
/// package class, the unloaded job and the <c>Leads to</c> link.
/// </summary>
public sealed class AiPackage
{
    /// <summary>
    /// The AI_PACKAGE's string id.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// The AI_PACKAGE's name in the active language.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// <c>signal func</c>: the editor's <c>BlackboardSignalFunctions</c>; the package class it builds is in docs/game/ai.md "Signal functions".
    /// </summary>
    public int Signal { get; init; }

    /// <summary>
    /// <c>unloaded func</c>: the editor's <c>UnloadedPlatoonJob</c>.
    /// </summary>
    public int Unloaded { get; init; }

    /// <summary>
    /// <c>Leads to</c>: the package started when this one finishes, resolved among the squad's own packages; null when none.
    /// </summary>
    public string? LeadsTo { get; init; }

    /// <summary>
    /// <c>contract end talk passive</c> (own, else inherited): the DIALOGUE the leader says to the employer when the package ends as a contract
    /// (event 30).
    /// </summary>
    public string? ContractEndTalk { get; init; }

    /// <summary>
    /// The goals per <see cref="AiRole"/> (index), the parent's first.
    /// </summary>
    public required IReadOnlyList<AiGoal>[] Roles { get; init; }

    /// <summary>
    /// The goals of <paramref name="role"/>, the parent's first.
    /// </summary>
    public IReadOnlyList<AiGoal> Goals(AiRole role) => Roles[(int)role];

    /// <summary>
    /// Signals the factory builds the plain <c>AIPackage</c> for (docs/game/ai.md "Signal functions", Verified): its <c>canActivate</c> is always
    /// true and it never finishes.
    /// </summary>
    public bool IsPlain => Signal is 0 or 2 or 3 or 7 or 8 or 9 or 18 or 21;
}

/// <summary>
/// A package a squad template lists in <c>AI packages</c>: the slot (v0, a priority, highest first) and the package.
/// </summary>
public readonly record struct SlottedPackage(int Slot, AiPackage Package);

/// <summary>
/// The AI records of a database, read on demand and cached (thread-safe: the population builds squads on its own thread). Problems (a goal whose task
/// is missing, an <c>inherits from</c> cycle) are skipped and counted in <see cref="Problems"/>.
/// </summary>
public sealed class AiDatabase(GameDatabase db)
{
    /// <summary>
    /// AI_TASK records by id; null for an id that is no AI_TASK (cached too).
    /// </summary>
    readonly Dictionary<string, AiTask?> _tasks = [];

    /// <summary>
    /// Resolved AI_PACKAGEs by id; null for an id that is no AI_PACKAGE (cached too).
    /// </summary>
    readonly Dictionary<string, AiPackage?> _packages = [];

    /// <summary>
    /// Each squad template's slotted packages, by template id.
    /// </summary>
    readonly Dictionary<string, IReadOnlyList<SlottedPackage>> _templates = [];

    /// <summary>
    /// Each race's <c>AI Goals</c>, by race id.
    /// </summary>
    readonly Dictionary<string, IReadOnlyList<AiGoal>> _races = [];

    /// <summary>
    /// The lock every cache read and fill takes.
    /// </summary>
    readonly object _gate = new();

    /// <summary>
    /// How many problems the loads so far found (missing tasks and packages, <c>inherits from</c> cycles).
    /// </summary>
    int _problems;

    /// <summary>
    /// How many goals and package links were skipped as problems so far (a missing task or package, an <c>inherits from</c> cycle).
    /// </summary>
    public int Problems { get { lock (_gate) return _problems; } }

    /// <summary>
    /// The AI_TASK <paramref name="id"/>, or null when it is not one.
    /// </summary>
    public AiTask? Task(string id)
    {
        lock (_gate) return TaskLocked(id);
    }

    /// <summary>
    /// <see cref="Task"/> with <see cref="_gate"/> held.
    /// </summary>
    AiTask? TaskLocked(string id)
    {
        if (_tasks.TryGetValue(id, out var known)) return known;
        var r = db.Find(id);
        var task = r is { Type: FcsRecordType.AiTask } ? AiTask.From(r) : null;
        _tasks[id] = task;
        return task;
    }

    /// <summary>
    /// The AI_PACKAGE <paramref name="id"/> with its <c>inherits from</c> chain resolved, or null when it is not one.
    /// </summary>
    public AiPackage? Package(string id)
    {
        lock (_gate) return PackageLocked(id);
    }

    /// <summary>
    /// <see cref="Package"/> with <see cref="_gate"/> held: builds the five role lists, the signal and unloaded funcs, the first <c>Leads to</c> and
    /// the end talk.
    /// </summary>
    AiPackage? PackageLocked(string id)
    {
        if (_packages.TryGetValue(id, out var known)) return known;
        var r = db.Find(id);
        AiPackage? package = null;
        if (r is { Type: FcsRecordType.AiPackage })
        {
            var roles = new List<AiGoal>[5];
            for (int i = 0; i < roles.Length; i++) roles[i] = [];
            Load(r, roles, []);
            var leads = r.GetReferences("Leads to");
            package = new AiPackage
            {
                Id = r.StringId,
                Name = r.Name,
                Signal = r.GetInt("signal func"),
                Unloaded = r.GetInt("unloaded func"),
                LeadsTo = leads.Count > 0 ? leads[0].TargetStringId : null,
                ContractEndTalk = EndTalk(r),
                Roles = [.. roles],
            };
        }
        _packages[id] = package;
        return package;
    }

    /// <summary>
    /// The package's <c>contract end talk passive</c> target, else the first one found up its <c>inherits from</c> chain (at most 8 deep); null when
    /// none has one.
    /// </summary>
    string? EndTalk(GameRecord r, int depth = 0)
    {
        if (r.GetReferences("contract end talk passive") is [var own, ..]) return own.TargetStringId;
        foreach (var parent in r.GetReferences("inherits from"))
            if (depth < 8 && db.Find(parent.TargetStringId) is { Type: FcsRecordType.AiPackage } p && EndTalk(p, depth + 1) is { } inherited)
                return inherited;
        return null;
    }

    /// <summary>
    /// Adds a package record's goals to the role lists, its <c>inherits from</c> parent's first; an entry whose task type and target equal one
    /// already listed is skipped, so the parent's wins (docs/game/ai.md "Goal loading", Verified). Each record fills Squad from <c>Squad AI Goals</c>
    /// else <c>Leader AI Goals</c>, Squad2 from <c>Squad2</c> else Squad else Leader, Leader from Leader, Slave from <c>Slave AI Goals</c> only; the
    /// legacy <c>AI Goals</c> (no record of the base game has it) goes into the first three.
    /// </summary>
    void Load(GameRecord r, List<AiGoal>[] roles, HashSet<string> visiting)
    {
        if (!visiting.Add(r.StringId))
        {
            _problems++;
            return;
        }
        // visiting is the chain being loaded, not every record seen: two parents may share an ancestor (warrior guild), whose second load adds
        // nothing.
        foreach (var parent in r.GetReferences("inherits from"))
            if (db.Find(parent.TargetStringId) is { Type: FcsRecordType.AiPackage } p) Load(p, roles, visiting);
        visiting.Remove(r.StringId);
        var leader = r.GetReferences("Leader AI Goals");
        var squad = r.GetReferences("Squad AI Goals");
        var squad2 = r.GetReferences("Squad2 AI Goals");
        var squadOrLeader = squad.Count > 0 ? squad : leader;
        Add(roles[(int)AiRole.Squad], squadOrLeader);
        Add(roles[(int)AiRole.Squad2], squad2.Count > 0 ? squad2 : squadOrLeader);
        Add(roles[(int)AiRole.Leader], leader);
        Add(roles[(int)AiRole.Slave], r.GetReferences("Slave AI Goals"));
        var legacy = r.GetReferences("AI Goals");
        for (int i = 0; i < 3; i++) Add(roles[i], legacy);
    }

    /// <summary>
    /// The goal lists' duplicate test (<c>FUN_14027b3a0</c>, <b>Verified</b>): same task type and equal hand (<c>FUN_1400cd060</c>: two null hands
    /// are equal). At load only LEADER, SQUAD_MISSION and HOME_GATE fill the hand, so SPECIFIC and SELF both stay null and are equal; the others are
    /// told apart by their kind (a hand that resolves to nothing later is not seen here).
    /// </summary>
    static bool SameHand(TaskTargetType a, TaskTargetType b) =>
        a == b || a is TaskTargetType.Specific or TaskTargetType.Self && b is TaskTargetType.Specific or TaskTargetType.Self;

    /// <summary>
    /// Adds the goals of <paramref name="entries"/> to a role list: an entry whose task is missing counts as a problem, one whose task type and hand
    /// equal a listed goal's is skipped (<see cref="SameHand"/>); the window is (v0, v1), the weight v2 x 0.01 with 0 meaning 1.
    /// </summary>
    void Add(List<AiGoal> list, IReadOnlyList<GameReference> entries)
    {
        foreach (var e in entries)
        {
            if (TaskLocked(e.TargetStringId) is not { } task)
            {
                _problems++;
                continue;
            }
            if (list.Exists(g => g.Task.Type == task.Type && SameHand(g.Task.Target, task.Target))) continue;
            // val2 is the weight x 100, 0 meaning 1.
            float weight = e.Values.Value2 == 0 ? 1 : e.Values.Value2 * 0.01f;
            list.Add(new AiGoal(task, e.Values.Value0, e.Values.Value1, weight));
        }
    }

    /// <summary>
    /// A template's <c>AI packages</c> with their slots (v0), in the listed order; links that are not AI_PACKAGE records are skipped.
    /// </summary>
    public IReadOnlyList<SlottedPackage> Packages(SquadTemplate template)
    {
        lock (_gate)
        {
            if (_templates.TryGetValue(template.Id, out var known)) return known;
            var list = new List<SlottedPackage>();
            foreach (var link in template.AiPackages)
            {
                if (PackageLocked(link.Id) is { } package) list.Add(new SlottedPackage(Math.Max(0, link.V0), package));
                else _problems++;
            }
            _templates[template.Id] = list;
            return list;
        }
    }

    /// <summary>
    /// A RACE's <c>AI Goals</c> (docs/game/ai.md "Other records that touch the AI", Verified): the weight is val1 x 0.01 with no zero default, the
    /// window is always open (0, 100); val0 is not read. The goal never has a task target (the task's <c>targeting</c> is replaced by Specific:
    /// FUN_1405094f0 reads only three legacy bools no record has).
    /// </summary>
    public IReadOnlyList<AiGoal> RaceGoals(string raceId)
    {
        lock (_gate)
        {
            if (_races.TryGetValue(raceId, out var known)) return known;
            var list = new List<AiGoal>();
            if (db.Find(raceId) is { } race)
                foreach (var e in race.GetReferences("AI Goals"))
                {
                    if (TaskLocked(e.TargetStringId) is { } task)
                        list.Add(new AiGoal(task with { Target = TaskTargetType.Specific }, 0, 100, e.Values.Value1 * 0.01f));
                    else _problems++;
                }
            _races[raceId] = list;
            return list;
        }
    }
}