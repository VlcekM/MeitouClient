using System.Numerics;
using Meitou.Data.Gameplay.Ai;
using Meitou.Data.World;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Locks;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Player;
using Meitou.Simulation.Production;

namespace Meitou.Simulation.Kidnap;

/// <summary>
/// Job 187 Obedient Slave of a player character (docs/game/ai-tasks.md "Obedient Slave"; Meitou: docs/simulation.md "Captives"). The job is a
/// perma-job the slave state setter adds when a character becomes a slave (state 1; <c>FUN_1405a4940</c> -> <c>FUN_14032e0d0</c>, <b>Verified</b>)
/// after destroying its orders and perma-jobs, and removes when it stops being one. While it is a slave and not sneaking (<c>FUN_14050e5b0</c>), the
/// job is urgent (step 3 of the decision step): the owner squad's <c>Slave AI Goals</c> are its candidates, so it follows its master, works the town
/// it is in, gets into its cage, fights for its master or its squad, runs, or stands, instead of acting on its own goals. Only a player character's
/// list holds the job; an NPC slave holds it in effect (<see cref="Ai.AiSystem.ObeyedRoles"/>), and the chooser's job passes score its master's
/// Slave goals for it with the AI's scorer (<see cref="Ai.TierRungs.PickJobPass"/>), on the same rungs as this job's.
/// </summary>
public static class ObedientSlave
{
    /// <summary>
    /// The job is in the character's list.
    /// </summary>
    public static bool HasJob(CharacterCold cold) => cold.Jobs.Exists(j => j.Type == TaskType.SlaveObedience);

    /// <summary>
    /// The branch's gate in <c>FUN_14050e5b0</c> (<b>Verified</b>): the character is in slave state 1 and not sneaking
    /// (<c>Character::isStealthMode</c>, <c>FUN_1405c9750</c>, the Sneak order).
    /// </summary>
    public static bool Obeys(CharacterCold cold) => cold.SlaveState == 1 && (cold.Orders & CharacterOrders.Sneak) == 0;

    /// <summary>
    /// The goals the job pass's 187 branch syncs from the master squad (<c>FUN_14026b790</c>, role 4): its blackboard's <c>Slave AI Goals</c>; null
    /// without a master, a blackboard or any goal. The chooser scores those of priorities 1 to 3. A pure read.
    /// </summary>
    internal static IReadOnlyList<AiGoal>? SlaveList(Squad? master) =>
        master is { Ai: { } board } && board.Roles[(int)AiRole.Slave] is { Count: > 0 } list ? list : null;

    /// <summary>
    /// The slave state changed to <paramref name="state"/> (<c>FUN_1405a4940</c>, only when it differs): becoming a slave destroys the orders and the
    /// perma-jobs and adds the job toward the owner; leaving slavery removes the job only, or, with none in the list, destroys the orders and the
    /// perma-jobs (<c>FUN_14032e0d0</c>, <b>Verified</b>). A player character's list only; an NPC slave obeys again
    /// (<see cref="CharacterCold.ObedienceLost"/>).
    /// </summary>
    internal static void StateChanged(World world, CharacterCold cold, byte state)
    {
        cold.ObedienceLost = false;
        if (!cold.IsPlayer) return;
        if (state == 1)
        {
            Reset(world, cold);
            return;
        }

        int at = cold.Jobs.FindIndex(j => j.Type == TaskType.SlaveObedience);
        if (at < 0)
        {
            Destroy(world, cold);
            return;
        }

        if (SlotOf(world, cold) is >= 0 and var slot) ProductionSystem.RemoveJob(world, slot, at);
        else cold.Jobs.RemoveAt(at);
    }

    /// <summary>
    /// The owner's orders object reset that processing (<c>FUN_14035aa20</c>), chaining (<c>FUN_14032e590</c>) and the state setter share: the
    /// one-shot orders and the perma-jobs are destroyed and the job is added toward the owner (a character the master squad's leader stands in for).
    /// A player character's only; an NPC slave obeys again (<see cref="CharacterCold.ObedienceLost"/>).
    /// </summary>
    internal static void Reset(World world, CharacterCold cold)
    {
        cold.ObedienceLost = false;
        if (!cold.IsPlayer) return;
        Destroy(world, cold);
        var owner = OwnerOf(world, cold);
        PermaJobs.Add(cold, TaskType.SlaveObedience, owner is { } o ? PermaJob.OfCharacter(TaskType.SlaveObedience, o)
                : PermaJob.Untargeted(TaskType.SlaveObedience));
    }

    /// <summary>
    /// DA 28's split (<c>FUN_1405ce4e0</c>, <b>Verified</b>): the perma-jobs alone are destroyed (<c>FUN_140507280</c>), job 187 with them, so a
    /// slave keeps its state and owner but no longer obeys (an NPC's <see cref="CharacterCold.ObedienceLost"/>).
    /// </summary>
    internal static void Disobey(World world, CharacterCold cold)
    {
        DestroyJobs(world, cold, SlotOf(world, cold));
        if (cold.SlaveState == 1) cold.ObedienceLost = true;
    }

    /// <summary>
    /// Destroys the character's perma-jobs and second list (<c>FUN_140507280</c>, <c>FUN_140507530</c>: a loaded queue); the order queue is kept
    /// (docs/game/ai-tasks.md "What clears the queue").
    /// </summary>
    static void Destroy(World world, CharacterCold cold)
    {
        DestroyJobs(world, cold, SlotOf(world, cold));
        PlayerOrders.ClearSecond(cold);
    }

    /// <summary>
    /// Destroys the character's perma-jobs (<c>FUN_140507280</c>); <paramref name="slot"/> -1: it is not in the table.
    /// </summary>
    static void DestroyJobs(World world, CharacterCold cold, int slot)
    {
        for (int i = cold.Jobs.Count - 1; i >= 0; i--)
        {
            if (slot >= 0) ProductionSystem.RemoveJob(world, slot, i);
            else cold.Jobs.RemoveAt(i);
        }
    }

    /// <summary>
    /// The slot of a living character (-1: not in the table).
    /// </summary>
    static int SlotOf(World world, CharacterCold cold)
    {
        var table = world.Characters;
        for (int i = 0; i < table.HighWater; i++)
            if (ReferenceEquals(table.Cold(i), cold) && table.Next[i].Alive) return i;
        return -1;
    }

    /// <summary>
    /// The character the owner handle names (<b>Meitou</b>: the leader of the master squad the slave was sold, arrested or processed into; the
    /// original's handle names a character or a squad); null when the master is not known.
    /// </summary>
    internal static CharacterId? OwnerOf(World world, CharacterCold cold)
    {
        if (cold.SlaveMaster < 0 || world.Squads.Find(cold.SlaveMaster) is not { } master) return null;
        var table = world.Characters;
        return !master.Leader.IsNone && table.IsAlive(master.Leader) ? master.Leader : null;
    }

    /// <summary>
    /// The master squad of the slave's job <paramref name="k"/>: the squad it obeys, else the squad of the owner character the job names (found again
    /// from its saved handle after a load).
    /// </summary>
    internal static Squad? MasterOf(World world, int slot, CharacterCold cold, int k)
    {
        if (cold.SlaveMaster >= 0 && world.Squads.Find(cold.SlaveMaster) is { } known) return known;
        return Owner(world, slot, cold, k) is { } owner && world.Characters.Cold(owner.Slot) is { SquadId: >= 0 and var squad }
                ? world.Squads.Find(squad) : null;
    }

    /// <summary>
    /// The job in the list of a slave with no master squad whose owner is still only its saved handle (a load), else -1: the owner the original
    /// reads through the owner handle (<c>+0x328</c>), which the job's target copies (<c>FUN_14032e0d0</c>). A job whose owner <see cref="Owner"/>
    /// found already stays pending until the master is adopted from it: the decision step can find it between the owner's spawn and the restore
    /// pass, and the far-escape rule then took the slave for one without an owner (#813's save: an escaped slave 150 ticks after the load).
    /// </summary>
    internal static int PendingOwnerJob(CharacterCold cold)
    {
        if (cold.SlaveState != 1 || cold.SlaveMaster >= 0) return -1;
        return cold.Jobs.FindIndex(j => j.Type == TaskType.SlaveObedience && !j.Saved.IsNull);
    }

    /// <summary>
    /// The owner character of job <paramref name="k"/>: its character, found again from the saved handle after a load (as <c>ProductionSystem</c>'s
    /// follow jobs); alive and not the slave itself.
    /// </summary>
    internal static CharacterId? Owner(World world, int slot, CharacterCold cold, int k)
    {
        var table = world.Characters;
        var job = cold.Jobs[k];
        var target = job.Character;
        if (target.IsNone && !job.Saved.IsNull)
        {
            for (int s = 0; s < table.HighWater && target.IsNone; s++)
                if (table.Next[s].Alive && table.Cold(s)?.Save?.Source.Handle == job.Saved) target = table.IdOf(s);
            if (!target.IsNone) cold.Jobs[k] = job with { Character = target };
        }

        return !target.IsNone && target.Slot != slot && table.IsAlive(target) ? target : null;
    }
}

/// <summary>
/// The Obedient Slave job as the decision step's contributor and the act-out of its goals. Step 3 asks it while the slave state is 1 and the
/// character is not sneaking; it picks among the owner squad's Slave role list in their hours by desire x finder x weight (x1.25 for the goal
/// running, a tie to the later-listed, <c>FUN_14050df10</c>) and starts the best: 171 Following master (the owner, else the master squad's leader:
/// Task_FollowLeader follows the slave owner, docs/game/pathfinding.md), 189 / 190 Worker (a random workplace of the town the squad's leader stands
/// in, <see cref="AiSystem.WorkAt"/>, stood at at its work clip as the AI's workers; 189 works its machine, <see cref="CharacterCold.SlaveWork"/>),
/// 172 Getting in my cage (<see cref="CaptiveSystem.OwnCage"/>, walked to and got into), 41 Attack enemies of my slavemaster and 143 Protect own
/// squad (the AI's finders, <see cref="AiSystem.ObedientFight"/>: a fight of the job), 35 Running away (<c>Task_Runaway</c>'s run from the enemies
/// it senses, <see cref="Aggro.FleeSystem.EnemyRunPoint"/>), 14 Idleness.
/// </summary>
sealed class ObedientJobs(CaptiveSystem captives) : IJobSource, ITaskHandler
{
    #region The job

    /// <summary>
    /// The x1.25 of the goal running now (docs/game/ai-tasks.md "Desire and commitment").
    /// </summary>
    const float Continuity = 1.25f;
    /// <summary>
    /// A walk that ends short of the building this many times gives it up for another pick.
    /// </summary>
    const int MaxFails = 3;

    /// <summary>
    /// The building a slave works at: the pick, its epoch and the walks that failed.
    /// </summary>
    sealed class WorkSpot
    {
        /// <summary>
        /// The workplace, its clip, functionality, hunger rate, reach and owner.
        /// </summary>
        public required ObedientWorkplace Place;
        /// <summary>
        /// The epoch of the pick (<see cref="AiMind.WorkEpoch"/> numbering).
        /// </summary>
        public int Epoch;
        /// <summary>
        /// The walks to it that ended short (<see cref="MaxFails"/> give it up).
        /// </summary>
        public int Fails;
        /// <summary>
        /// The walk 189's plan makes away from the workplace now (<see cref="ErrandFor"/>), null while the work is at the workplace.
        /// </summary>
        public Errand? Away;
    }

    /// <summary>
    /// A walk of 189's plan away from the workplace: to the machine row 94 found, whose output row 92 takes there (<paramref name="Source"/>), or to
    /// the storage row 93 fills there (<paramref name="Store"/>); <paramref name="Goal"/> is where it walks.
    /// </summary>
    sealed record Errand(Vector3 Goal, TownBuilding? Source, string? Store);

    /// <summary>
    /// What 189's plan does at a workplace (<see cref="ErrandFor"/>).
    /// </summary>
    enum Plan
    {
        /// <summary>
        /// The work is at the workplace (rows 92, 93 or 87 there).
        /// </summary>
        Here,
        /// <summary>
        /// A walk away comes first (<see cref="WorkSpot.Away"/>).
        /// </summary>
        Away,
        /// <summary>
        /// 189 has no plan at this workplace.
        /// </summary>
        None,
    }

    /// <summary>
    /// The building each slave works at and the picks it made (runtime only, never hashed: the pick is a roll of the character and the epoch).
    /// </summary>
    readonly Dictionary<CharacterId, WorkSpot> _works = [];

    /// <summary>
    /// The cage each slave's 172 heads for, kept while it stays free, and the walks to it that ended short of it (runtime only, never hashed).
    /// </summary>
    readonly Dictionary<CharacterId, (string Cage, int Walks)> _cages = [];

    /// <summary>
    /// A goal of the master's Slave list and its score.
    /// </summary>
    readonly record struct Choice(TaskType Type, float Score);

    /// <summary>
    /// Whether job <paramref name="k"/> is urgent: always while the character obeys as a slave.
    /// </summary>
    public bool Urgent(World world, int slot, int k) => ObedientSlave.Obeys(world.Characters.Cold(slot)!);

    /// <summary>
    /// Whether the Obedient Slave job can run for the character: a slave that obeys with a goal of its master's Slave list to act out.
    /// </summary>
    public DecisionCheck CanRun(World world, int slot, int k)
    {
        var cold = world.Characters.Cold(slot)!;
        if (!ObedientSlave.Obeys(cold)) return DecisionCheck.No(cold.SlaveState == 1 ? "sneaking" : "not a slave");
        if (Choose(world, slot, cold, k) is not { } pick)
            return DecisionCheck.No("no goal of its master's Slave list runs");

        return Continues(world, slot, cold, k, pick.Type) ? DecisionCheck.Runs : DecisionCheck.Run;
    }

    /// <summary>
    /// Starts the goal the job chose for the character.
    /// </summary>
    public void Start(World world, int slot, int k)
    {
        var table = world.Characters;
        var cold = table.Cold(slot)!;
        if (!ObedientSlave.Obeys(cold) || Choose(world, slot, cold, k) is not { } pick) return;

        switch (pick.Type)
        {
            case TaskType.FollowSlavemaster when FollowTarget(world, slot, cold, k) is { } target:
                MovementSystem.Follow(world, new FollowOrder([table.IdOf(slot)], target) { Tick = world.Tick }, TaskSource.Job, k,
                        TaskType.FollowSlavemaster);
                break;
            case TaskType.AutoLabouringMines or TaskType.AutoLabouringMinesPretend:
                StartWork(world, slot, cold, k, pick.Type);
                break;
            case TaskType.SlaveGetInMyMastersCage:
                StartCage(world, slot, cold, k);
                break;
            case TaskType.AttackEnemiesOfMySlavemaster or TaskType.ProtectOwnSquad:
                StartFight(world, slot, cold, k, pick.Type);
                break;
            case TaskType.RunAway:
                StartRun(world, slot, k);
                break;
            case TaskType.Idle:
                TaskStart.Start(world, slot, new TaskRequest(CharacterTask.Idle, TaskType.Idle, TaskSource.Job) { Job = k });
                break;
        }
    }

    /// <summary>
    /// The slave's task is this job's and no goal of the list runs now (sneaking, or the list is gone): the task ends and the step decides the
    /// character on its own goals.
    /// </summary>
    public void Stop(World world, int slot, int k)
    {
        var cold = world.Characters.Cold(slot)!;
        if (cold.Running is { Source: TaskSource.Job, Implicit: false } run && run.Job == k && run.Row == TaskType.FollowSlavemaster)
            cold.FollowOrder = CharacterId.None;

        TaskStart.End(world, slot, TaskEnd.Stopped);
    }

    #endregion

    #region The pick

    /// <summary>
    /// The best goal of the master's Slave list now (the scorer of <c>FUN_14050df10</c>: only the priorities 1 to 3 are loaded, <c>FUN_14026b790</c>
    /// mask 0xe), among those Meitou acts out; null when the slave cannot act (down, carried, carrying) or none runs.
    /// </summary>
    Choice? Choose(World world, int slot, CharacterCold cold, int k)
    {
        if (cold.Medical is { Incapacitated: true } || !cold.CarriedBy.IsNone || !cold.Carrying.IsNone) return null;
        if (ObedientSlave.SlaveList(ObedientSlave.MasterOf(world, slot, cold, k)) is not { } list) return null;

        int hour = world.Hour;
        Choice? best = null;
        for (int i = 0; i < list.Count; i++)
        {
            var goal = list[i];
            var type = goal.Task.Type;
            if (goal.Task.Priority is < TaskPriority.Fluff or > TaskPriority.Urgent || !goal.InWindow(hour) || !TaskStart.Eligible(world, cold, type))
                continue;

            float score = Desire(world, slot, cold, k, type) * goal.Weight * Crowding(world, slot, cold, goal);
            if (Runs(cold, k, type)) score *= Continuity;
            if (!(score > 0)) continue;

            // The original keys candidates by score and raises a taken key by 0.0001: of equal scores the later-listed ranks first.
            if (best is null || score >= best.Value.Score) best = new Choice(type, score);
        }

        return best;
    }

    /// <summary>
    /// The scorer's crowd factor (<c>FUN_14050df10</c>, <b>Verified</b>): the row's TaskData <c>+0x34</c> (<see cref="TaskProfile.Crowd"/>) to
    /// the power of the entries of the slave's own squad's blackboard on the same goal (<c>FUN_140268f30</c>, <see cref="Blackboard.CrowdOf"/>).
    /// Every goal the Slave lists hold keeps the default 1, so it changes no score; a squad without a board has no entries.
    /// </summary>
    static float Crowding(World world, int slot, CharacterCold cold, AiGoal goal)
    {
        var type = goal.Task.Type;
        float crowd = TaskProfiles.Of(type).Crowd;
        if (crowd == 1 || world.Squads.Find(cold.SquadId) is not { Ai: { } board } squad) return 1;

        var id = world.Characters.IdOf(slot);
        bool needsTarget = TaskDataTable.Of(type)?.Is(TaskDataFlags.NeedsTarget) ?? false;
        int n = board.CrowdOf(type, needsTarget, AiSystem.CrowdTarget(goal.Task.Target, squad, board, id), id);
        return MathF.Pow(crowd, n);
    }

    /// <summary>
    /// Whether goal <paramref name="type"/> is the job's running goal (its act-out's row, <see cref="RowOf"/>).
    /// </summary>
    static bool Runs(CharacterCold cold, int k, TaskType type) =>
        cold.Running is { Source: TaskSource.Job, Implicit: false } run && run.Job == k && run.Row == RowOf(type);

    /// <summary>
    /// The row a goal's act-out runs under: 41's fight is a goal's attack (row 16), 143's a defence (row 13, as the AI's protect goals); the others
    /// their own.
    /// </summary>
    static TaskType RowOf(TaskType type) => type switch
    {
        TaskType.AttackEnemiesOfMySlavemaster => TaskType.AttackEnemies,
        TaskType.ProtectOwnSquad => TaskType.AttackAttackersOf,
        _ => type,
    };

    /// <summary>
    /// How much the goal is wanted, its finder's factor included: 171, 189 / 190 and 172 1 when their finder finds a target, the combat goals and 35
    /// as the AI weighs them, 14 1; 0 for a goal Meitou does not act out.
    /// </summary>
    float Desire(World world, int slot, CharacterCold cold, int k, TaskType type)
    {
        var ai = world.SystemOf<AiSystem>();
        return type switch
        {
            TaskType.FollowSlavemaster => FollowTarget(world, slot, cold, k) is null ? 0 : 1,
            TaskType.AutoLabouringMines or TaskType.AutoLabouringMinesPretend => ai is not null && WorkPlans(world, slot, cold, type, ai) ? 1 : 0,
            // Its effect 79 (imprisoned) holding, the goal is done: the finder wants the slave held in nothing.
            TaskType.SlaveGetInMyMastersCage => captives.OwnCage(world, slot, cold) is null ? 0 : 1,
            TaskType.AttackEnemiesOfMySlavemaster or TaskType.ProtectOwnSquad => FightScore(world, slot, cold, k, type, ai),
            TaskType.RunAway => ai?.ObedientRunDesire(world, slot) ?? 0,
            TaskType.Idle => 1,
            _ => 0,
        };
    }

    /// <summary>
    /// Whether a worker goal finds a workplace and plans there: the finder needs one (<see cref="AiSystem.HasWorkplaces"/>); for 189 the workplace it
    /// holds must have a plan (<see cref="ErrandFor"/>), else the next pick, which its next start makes, must (a goal whose plan fails is not run, and
    /// the next goal of the list is tried).
    /// </summary>
    bool WorkPlans(World world, int slot, CharacterCold cold, TaskType type, AiSystem ai)
    {
        var id = world.Characters.IdOf(slot);
        var town = TownPoint(world, slot, cold);
        if (!ai.HasWorkplaces(world, id, town, type)) return false;
        if (type != TaskType.AutoLabouringMines || !_works.TryGetValue(id, out var spot)) return true;
        if (ErrandFor(world, slot, cold, spot.Place).Plan != Plan.None) return true;

        return ai.WorkAt(world, id, town, spot.Epoch + 1, type) is { } next && ErrandFor(world, slot, cold, next).Plan != Plan.None;
    }

    /// <summary>
    /// A combat goal's desire x finder factor (<see cref="AiSystem.ObedientFight"/>), its fight kept while it runs.
    /// </summary>
    static float FightScore(World world, int slot, CharacterCold cold, int k, TaskType type, AiSystem? ai)
    {
        if (ai is null || world.Squads.Find(cold.SquadId) is not { } squad) return 0;

        bool running = Runs(cold, k, type) && cold.InCombat;
        return ai.ObedientFight(world, slot, squad, type, running).Score;
    }

    /// <summary>
    /// Whom 171 follows: the owner, else the master squad's leader.
    /// </summary>
    static CharacterId? FollowTarget(World world, int slot, CharacterCold cold, int k)
    {
        if (ObedientSlave.Owner(world, slot, cold, k) is { } owner) return owner;
        var table = world.Characters;
        return ObedientSlave.MasterOf(world, slot, cold, k) is { } master && !master.Leader.IsNone && master.Leader.Slot != slot
                && table.IsAlive(master.Leader) ? master.Leader : null;
    }

    /// <summary>
    /// The point whose town the worker finders ask (<c>FUN_1408cac60</c> reads the platoon blackboard's current town, <c>+0x88</c>, which the AI's
    /// rule writes from the squad leader, docs/game/ai.md): where the slave's own squad's leader stands, else the slave.
    /// </summary>
    static Vector3 TownPoint(World world, int slot, CharacterCold cold)
    {
        var table = world.Characters;
        if (world.Squads.Find(cold.SquadId) is { } squad && !squad.Leader.IsNone && table.TryResolveNext(squad.Leader, out int leader))
            return table.Next[leader].Position;

        return table.Next[slot].Position;
    }

    #endregion

    #region Acting out

    /// <summary>
    /// Whether the character's task is already this goal's act-out (the plan goes on): a follow of the target, a walk to the workplace or standing at
    /// it, the job's fight, the walk into the cage, the run, standing idle on 14.
    /// </summary>
    bool Continues(World world, int slot, CharacterCold cold, int k, TaskType type)
    {
        if (!Runs(cold, k, type)) return false;

        var task = (CharacterTask)world.Characters.Next[slot].Task;
        var run = cold.Running!;
        switch (type)
        {
            case TaskType.FollowSlavemaster:
                return run.Runs(task) && FollowTarget(world, slot, cold, k) is { } target && cold.FollowOrder == target;
            case TaskType.AutoLabouringMines or TaskType.AutoLabouringMinesPretend:
                return ContinuesWork(world, slot, cold, task);
            case TaskType.SlaveGetInMyMastersCage:
                return run.Runs(task) && task == CharacterTask.GoTo && _cages.TryGetValue(world.Characters.IdOf(slot), out var cage)
                    && world.Fixtures.Find(cage.Cage) is { } fixture && FixtureRegistry.IsFree(world.Characters, fixture)
                    && !AtCage(world, slot, fixture, walked: false);
            default:
                return run.Runs(task);
        }
    }

    /// <summary>
    /// The work goes on: the walk to the workplace, or standing at it at its clip. A walk held off by the building short of it is over once it stands
    /// within twice the reach (AiSystem.AtWork): StartWork then sets it working.
    /// </summary>
    bool ContinuesWork(World world, int slot, CharacterCold cold, CharacterTask task)
    {
        if (!_works.TryGetValue(world.Characters.IdOf(slot), out var spot) || !cold.Running!.Runs(task)) return false;

        ref var n = ref world.Characters.Next[slot];
        if (spot.Away is { } away) return task == CharacterTask.GoTo && !AiSystem.AtWork(in n, away.Goal, AiSystem.WorkReach);

        var place = spot.Place;
        if (task == CharacterTask.GoTo) return !AtPlace(world, slot, place);

        // Standing at it: 189's plan may now walk away first (an input to fetch, output to put away).
        bool real = cold.Running.Row == TaskType.AutoLabouringMines;
        return cold.ActionClip == place.Clip && (!real || ErrandFor(world, slot, cold, place).Plan == Plan.Here);
    }

    /// <summary>
    /// Starts work goal <paramref name="type"/> for a slave: picks its workplace (kept while the pick stands) and walks there.
    /// </summary>
    void StartWork(World world, int slot, CharacterCold cold, int k, TaskType type)
    {
        var table = world.Characters;
        var id = table.IdOf(slot);
        if (world.SystemOf<AiSystem>() is not { } ai) return;

        bool again = _works.TryGetValue(id, out var spot) && Runs(cold, k, type);
        if (again && type == TaskType.AutoLabouringMines)
        {
            var plan = RunErrand(world, slot, cold, k, spot!);
            if (plan == Plan.Away) return;
            if (plan == Plan.None) again = false;
        }

        if (again && AtPlace(world, slot, spot!.Place))
        {
            Work(world, slot, k, type);
            return;
        }

        // The walk ended short of the building: it is asked again, a few times.
        if (again && table.Next[slot].Task == (byte)CharacterTask.Idle && cold.Running!.Task == CharacterTask.GoTo && ++spot!.Fails < MaxFails)
        {
            Walk(world, slot, k, type, spot.Place);
            return;
        }

        // A new pick: another roll each time it starts working anew.
        int epoch = spot is null ? 0 : spot.Epoch + 1;
        if (ai.WorkAt(world, id, TownPoint(world, slot, cold), epoch, type) is not { } found) return;

        var picked = _works[id] = new WorkSpot { Place = found, Epoch = epoch };
        if (type == TaskType.AutoLabouringMines && RunErrand(world, slot, cold, k, picked) != Plan.Here) return;

        if (AtPlace(world, slot, found)) Work(world, slot, k, type);
        else Walk(world, slot, k, type, found);
    }

    #endregion

    #region 189's plan

    /// <summary>
    /// 189's walks away from its workplace: at the walk's end it acts (row 92 at the source, row 93 at the storage) and the plan is asked again;
    /// a walk that keeps ending short gives the workplace up (<see cref="MaxFails"/>). <see cref="Plan.Away"/> while a walk runs,
    /// <see cref="Plan.None"/> when 189 has no plan at the workplace (the next start makes a new pick: engine choice, the original's finder rolls a
    /// fresh target each think).
    /// </summary>
    Plan RunErrand(World world, int slot, CharacterCold cold, int k, WorkSpot spot)
    {
        ref var n = ref world.Characters.Next[slot];
        if (spot.Away is { } away)
        {
            if (!AiSystem.AtWork(in n, away.Goal, AiSystem.WorkReach))
            {
                if (n.Task == (byte)CharacterTask.GoTo) return Plan.Away;
                if (++spot.Fails >= MaxFails) return Plan.None;

                GoTo(world, slot, k, away.Goal);
                return Plan.Away;
            }

            Act(world, slot, cold, spot.Place, away);
            spot.Away = null;
            spot.Fails = 0;
        }

        var (plan, errand) = ErrandFor(world, slot, cold, spot.Place);
        if (plan != Plan.Away) return plan;

        spot.Away = errand;
        GoTo(world, slot, k, errand!.Goal);
        return Plan.Away;
    }

    /// <summary>
    /// At the walk's end: row 92 empties the source's output into the inventory, row 93 puts the carried items into the storage.
    /// </summary>
    static void Act(World world, int slot, CharacterCold cold, ObedientWorkplace place, Errand away)
    {
        var at = world.Characters.Next[slot].Position;
        if (away.Source is { } source) world.SystemOf<ProductionSystem>()?.TakeTownOutput(world, cold, source, place.Owner);
        else if (away.Store is { } store) world.SystemOf<TownDutySystem>()?.PutAway(world, cold, store, at);
    }

    /// <summary>
    /// The plan of 189 at its workplace, as the planner makes it from 87's list B in order (docs/game/buildings-production.md "NPC town machines"):
    /// with the output full (<c>!63</c>) and no room for its item (92's <c>82</c>), the ditch first (114, then 93 at the storage); with an empty
    /// input (<c>!62</c>) and none of its items carried (93's <c>67</c>), the machine row 94 finds (its effect <c>66</c>: 92 there; no room for that
    /// machine's item: the ditch first); with the machine's output carried and a storage taking it (<c>!89</c>, evaluator <c>FUN_1405a2c90</c>,
    /// <b>Observed</b>), putting it away (113, then 93 at that storage). With no source, or no storage to make room in, 189 has no plan there.
    /// </summary>
    static (Plan Plan, Errand? Errand) ErrandFor(World world, int slot, CharacterCold cold, ObedientWorkplace place)
    {
        if (world.SystemOf<ProductionSystem>() is not { } production || ProductionSystem.TownWorkOf(world, place.Building) is not { } m)
            return (Plan.Here, null);

        var at = world.Characters.Next[slot].Position;
        if (m.OutputBlocked) return production.RoomForOutput(cold, m) ? (Plan.Here, null) : Ditch(world, cold, at);
        if (m.HasEmptyInputs) return ProductionSystem.CarriesInputOf(cold, m) ? (Plan.Here, null) : Fetch(world, cold, m, at, production);
        if (!ProductionSystem.CarriesOutputOf(cold, m) || Store(world, cold, at, OutputCarried(cold, m)) is not var (id, spot))
            return (Plan.Here, null);

        return (Plan.Away, new Errand(new Vector3(spot.X, 0, spot.Y), null, id));
    }

    /// <summary>
    /// Row 94's walk: the town machine whose output holds an item the empty input takes (<see cref="ProductionSystem.InputSource"/>), the ditch first
    /// when there is no room for that item; no plan without one.
    /// </summary>
    static (Plan, Errand?) Fetch(World world, CharacterCold cold, Machine m, Vector3 at, ProductionSystem production)
    {
        if (world.SystemOf<AiSystem>() is not { } ai || ProductionSystem.InputSource(world, m, ai.TownWorkplaces(at), at) is not { } source
            || ProductionSystem.TownWorkOf(world, source) is not { } from)
        {
            return (Plan.None, null);
        }

        if (!production.RoomForOutput(cold, from)) return Ditch(world, cold, at);
        return (Plan.Away, new Errand(source.Position with { Y = 0 }, source, null));
    }

    /// <summary>
    /// Row 114's walk: the nearest storage taking a carried item (<see cref="TownDutySystem.DitchStore"/>); no plan without one.
    /// </summary>
    static (Plan, Errand?) Ditch(World world, CharacterCold cold, Vector3 at)
    {
        if (world.SystemOf<TownDutySystem>() is not { } duties || world.Squads.Find(cold.SquadId) is not { } squad
            || duties.DitchStore(world, squad, cold, at, register: false) is not var (id, spot))
        {
            return (Plan.None, null);
        }

        return (Plan.Away, new Errand(new Vector3(spot.X, 0, spot.Y), null, id));
    }

    /// <summary>
    /// The storage the ditch finder finds for <paramref name="items"/> (<see cref="TownDutySystem.StoreTaking"/>), null for none.
    /// </summary>
    static (string Id, Vector2 Spot)? Store(World world, CharacterCold cold, Vector3 at, IEnumerable<Items.ItemInstance> items) =>
        world.SystemOf<TownDutySystem>() is { } duties && world.Squads.Find(cold.SquadId) is { } squad
            ? duties.StoreTaking(world, squad, cold, at, items) : null;

    /// <summary>
    /// The carried units of the machine's output item (inventory and worn backpack).
    /// </summary>
    static IEnumerable<Items.ItemInstance> OutputCarried(CharacterCold cold, Machine m)
    {
        if (m.Output is not { } output || cold.Inventory is not { } inv) return [];

        var carried = inv.All().Concat(inv.Backpack?.Contents?.All() ?? []);
        return carried.Where(i => i.Record == output.Recipe.Item);
    }

    /// <summary>
    /// Starts a walk of 189 to <paramref name="goal"/> (arriving within <see cref="AiSystem.WorkReach"/>, <see cref="ITaskHandler.Start"/>).
    /// </summary>
    static void GoTo(World world, int slot, int k, Vector3 goal)
    {
        var request = new TaskRequest(CharacterTask.GoTo, TaskType.AutoLabouringMines, TaskSource.Job)
        {
            Job = k,
            Goal = new Vector2(goal.X, goal.Z),
            Mode = SpeedMode.Walk,
        };
        TaskStart.Start(world, slot, request);
    }

    #endregion

    #region Acting out the rest

    /// <summary>
    /// Whether the slave is at its workplace, as the AI's workers (<see cref="AiSystem.AtWorkplace"/>).
    /// </summary>
    static bool AtPlace(World world, int slot, ObedientWorkplace place) =>
        world.SystemOf<AiSystem>() is { } ai && ai.AtWorkplace(world, in world.Characters.Next[slot], place.Building);

    /// <summary>
    /// Starts the walk to the workplace: to its use node nearest the slave, its position without one (<see cref="AiSystem.WorkMarker"/>).
    /// </summary>
    static void Walk(World world, int slot, int k, TaskType type, ObedientWorkplace place)
    {
        var from = world.Characters.Next[slot].Position;
        var marker = world.SystemOf<AiSystem>()?.WorkMarker(world, place.Building, from) ?? place.Building.Position;
        var goal = new Vector2(marker.X, marker.Z);
        TaskStart.Start(world, slot, new TaskRequest(CharacterTask.GoTo, type, TaskSource.Job) { Job = k, Goal = goal, Mode = SpeedMode.Walk });
    }

    /// <summary>
    /// Starts the standing at the workplace.
    /// </summary>
    static void Work(World world, int slot, int k, TaskType type) =>
        TaskStart.Start(world, slot, new TaskRequest(CharacterTask.Idle, type, TaskSource.Job) { Job = k });

    /// <summary>
    /// 172: at the cage the slave gets in (<see cref="FixtureRegistry.Imprison"/>, as the AI slave's own walk into its cage), else it walks there.
    /// The cage it heads for is kept while it stays free; after <see cref="CaptiveSettings.Walks"/> walks that ended short of it, it is marked
    /// unreachable for the slave's faction (<see cref="FixtureRegistry.MarkUnreachable"/>) and given up, as the AI slave's walk.
    /// </summary>
    void StartCage(World world, int slot, CharacterCold cold, int k)
    {
        var table = world.Characters;
        var id = table.IdOf(slot);
        bool kept = _cages.TryGetValue(id, out var held) && world.Fixtures.Find(held.Cage) is { } known && FixtureRegistry.IsFree(table, known);
        var cage = kept ? world.Fixtures.Find(held.Cage) : captives.OwnCage(world, slot, cold);
        if (cage is null) return;

        int walks = kept ? held.Walks : 0;
        bool walked = table.Next[slot].Task == (byte)CharacterTask.Idle && Runs(cold, k, TaskType.SlaveGetInMyMastersCage);
        if (AtCage(world, slot, cage, walked && walks > 0))
        {
            _cages.Remove(id);
            TaskStart.Stand(table, slot);
            world.Fixtures.Imprison(world, id, cage);
            return;
        }

        if (++walks > captives.Settings.Walks)
        {
            _cages.Remove(id);
            world.Fixtures.MarkUnreachable(world, cage, cold.Faction);
            return;
        }

        _cages[id] = (cage.Id, walks);
        var goal = new Vector2(cage.Position.X, cage.Position.Z);
        TaskStart.Start(world, slot,
            new TaskRequest(CharacterTask.GoTo, TaskType.SlaveGetInMyMastersCage, TaskSource.Job) { Job = k, Goal = goal, Mode = SpeedMode.Walk });
    }

    /// <summary>
    /// At the cage: within its <c>use range</c> + <see cref="CaptiveSettings.Reach"/>, or within <see cref="CaptiveSettings.Slack"/> once its walk
    /// there ended (<paramref name="walked"/>), as the AI slaves' walk (<see cref="CaptiveSystem"/>).
    /// </summary>
    bool AtCage(World world, int slot, Fixture cage, bool walked)
    {
        var at = world.Characters.Next[slot].Position;
        float d = Vector2.Distance(new Vector2(at.X, at.Z), new Vector2(cage.Position.X, cage.Position.Z));
        return d <= cage.UseRange + captives.Settings.Reach || walked && d <= captives.Settings.Slack;
    }

    /// <summary>
    /// 41 or 143: the job's fight on the finder's foe for the next tick, an attack (row 16, with the fighter's alert voice) for 41, a defence (row
    /// 13) for 143, as the AI's goal fights (<see cref="AiSystem"/>'s serial step).
    /// </summary>
    static void StartFight(World world, int slot, CharacterCold cold, int k, TaskType type)
    {
        var table = world.Characters;
        if (world.SystemOf<AiSystem>() is not { } ai || world.Squads.Find(cold.SquadId) is not { } squad) return;

        var foe = ai.ObedientFight(world, slot, squad, type, running: false).Target;
        if (foe.IsNone || !table.TryResolveNext(foe, out int f)) return;

        bool protects = type == TaskType.ProtectOwnSquad;
        world.Defer(new AttackOrder([table.IdOf(slot)], foe)
        {
            Tick = world.Tick + 1, Source = TaskSource.Job, Job = k, Defensive = protects, Cause = "an obedient slave's job of its list",
        });
        world.SystemOf<Aggro.AggroSystem>()?.GoalFight(world, slot, f, voice: !protects);
    }

    /// <summary>
    /// 35: <c>Task_Runaway</c>'s run at full speed from the enemies it senses (<see cref="Aggro.FleeSystem.EnemyRunPoint"/>); the start ends a fight.
    /// </summary>
    static void StartRun(World world, int slot, int k)
    {
        if (world.SystemOf<Aggro.FleeSystem>() is not { } flee) return;

        var run = new TaskRequest(CharacterTask.GoTo, TaskType.RunAway, TaskSource.Job) { Job = k, Mode = SpeedMode.Free };
        TaskStart.Start(world, slot, run with { Goal = flee.EnemyRunPoint(world, slot) });
    }

    /// <summary>
    /// The walk to the workplace (rows 189 and 190 start as <see cref="CharacterTask.GoTo"/>) and the standing at it
    /// (<see cref="CharacterTask.Idle"/>, facing the building at its work clip, <see cref="CharacterCold.SlaveWork"/> set).
    /// </summary>
    void ITaskHandler.Start(World world, int slot, in TaskRequest request)
    {
        var table = world.Characters;
        var cold = table.Cold(slot)!;
        var known = _works.TryGetValue(table.IdOf(slot), out var spot);
        if (request.Task == CharacterTask.GoTo)
        {
            MovementSystem.GoTo(table, slot, request.Goal!.Value, request.Mode ?? cold.RunSpeed);
            cold.ArriveWithin = known && spot!.Away is null ? spot.Place.Range : AiSystem.WorkReach;
            return;
        }

        MovementSystem.Stop(table, slot);
        if (!known) return;

        var place = spot!.Place;
        ref var n = ref table.Next[slot];
        float dx = place.Building.Position.X - n.Position.X, dz = place.Building.Position.Z - n.Position.Z;
        if (dx * dx + dz * dz > 1e-4f) n.Yaw = MathF.Atan2(dx, dz);

        cold.ActionClip = place.Clip;
        cold.ActionWork = AiSystem.StandingWork(place.Functionality, place.Clip, place.Tool);
        cold.SlaveWork = new ObedientWork(place, request.Row == TaskType.AutoLabouringMines);
    }

    /// <summary>
    /// Another task takes the worker: the work clip, the workplace and its operating go.
    /// </summary>
    void ITaskHandler.End(World world, int slot, CharacterTask task, TaskEnd reason)
    {
        var cold = world.Characters.Cold(slot)!;
        if (cold.SlaveWork is not null) cold.Operating = false;   // ProductionSystem.ObedientWorkers' operating of the machine
        cold.SlaveWork = null;
        if (_works.TryGetValue(world.Characters.IdOf(slot), out var spot) && cold.ActionClip == spot.Place.Clip)
        {
            cold.ActionClip = null;
            cold.ActionWork = null;
        }
    }

    #endregion
}
