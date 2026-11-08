using System.Numerics;
using Meitou.Data.Gameplay.Ai;
using Meitou.Data.World;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Player;
using Meitou.Simulation.Production;

namespace Meitou.Simulation.Kidnap;

/// <summary>
/// Job 187 Obedient Slave of a player character (docs/game/ai-tasks.md "Obedient Slave"; Meitou: docs/simulation.md "Captives"). The job is a perma-job the slave state setter
/// adds when a character becomes a slave (state 1; <c>FUN_1405a4940</c> -> <c>FUN_14032e0d0</c>, <b>Verified</b>) after destroying its orders and perma-jobs, and removes when it stops being
/// one. While it is a slave and not sneaking (<c>FUN_14050e5b0</c>), the job is urgent (step 3 of the decision step): the owner squad's <c>Slave AI Goals</c> are its candidates, so it
/// follows its master, works the town it is in, or stands, instead of acting on its own goals. Only a player character's list holds the job (the AI's slaves run the same list directly,
/// <see cref="Ai.AiSystem"/>).
/// </summary>
public static class ObedientSlave
{
    /// <summary>The job is in the character's list.</summary>
    public static bool HasJob(CharacterCold cold) => cold.Jobs.Exists(j => j.Type == TaskType._SLAVE_OBEDIENCE);

    /// <summary>
    /// The branch's gate in <c>FUN_14050e5b0</c> (<b>Verified</b>): the character is in slave state 1 and not sneaking (<c>Character::isStealthMode</c>, <c>FUN_1405c9750</c>, the Sneak order).
    /// </summary>
    public static bool Obeys(CharacterCold cold) => cold.SlaveState == 1 && (cold.Orders & CharacterOrders.Sneak) == 0;

    /// <summary>
    /// The slave state changed to <paramref name="state"/> (<c>FUN_1405a4940</c>, only when it differs): becoming a slave destroys the orders and the perma-jobs and adds the job toward the owner;
    /// leaving slavery removes the job only, or, with none in the list, destroys the orders and the perma-jobs (<c>FUN_14032e0d0</c>, <b>Verified</b>). A player character's list only.
    /// </summary>
    internal static void StateChanged(World world, CharacterCold cold, byte state)
    {
        if (!cold.IsPlayer) return;
        if (state == 1)
        {
            Reset(world, cold);
            return;
        }
        int at = cold.Jobs.FindIndex(j => j.Type == TaskType._SLAVE_OBEDIENCE);
        if (at < 0)
        {
            Destroy(world, cold);
            return;
        }
        if (SlotOf(world, cold) is >= 0 and var slot) ProductionSystem.RemoveJob(world, slot, at);
        else cold.Jobs.RemoveAt(at);
    }

    /// <summary>
    /// The owner's orders object reset that processing (<c>FUN_14035aa20</c>), chaining (<c>FUN_14032e590</c>) and the state setter share: the one-shot orders and the perma-jobs are destroyed and
    /// the job is added toward the owner (a character the master squad's leader stands in for). A player character's only.
    /// </summary>
    internal static void Reset(World world, CharacterCold cold)
    {
        if (!cold.IsPlayer) return;
        Destroy(world, cold);
        var owner = OwnerOf(world, cold);
        PermaJobs.Add(cold, TaskType._SLAVE_OBEDIENCE, owner is { } o ? PermaJob.OfCharacter(TaskType._SLAVE_OBEDIENCE, o) : PermaJob.Untargeted(TaskType._SLAVE_OBEDIENCE));
    }

    /// <summary>Destroys the character's perma-jobs and order queue (<c>FUN_140507280</c>, <c>FUN_140507530</c>).</summary>
    static void Destroy(World world, CharacterCold cold)
    {
        int slot = SlotOf(world, cold);
        for (int i = cold.Jobs.Count - 1; i >= 0; i--)
            if (slot >= 0) ProductionSystem.RemoveJob(world, slot, i);
            else cold.Jobs.RemoveAt(i);
        cold.OrderQueue.Clear();
        if (slot >= 0 && cold.OrderMove && world.Characters.Next[slot].Task == (byte)CharacterTask.GoTo) TaskStart.Stand(world.Characters, slot);
    }

    static int SlotOf(World world, CharacterCold cold)
    {
        var table = world.Characters;
        for (int i = 0; i < table.HighWater; i++)
            if (ReferenceEquals(table.Cold(i), cold) && table.Next[i].Alive) return i;
        return -1;
    }

    /// <summary>
    /// The character the owner handle names (<b>Meitou</b>: the leader of the master squad the slave was sold, arrested or processed into; the original's handle names a character or a
    /// squad); null when the master is not known.
    /// </summary>
    internal static CharacterId? OwnerOf(World world, CharacterCold cold)
    {
        if (cold.SlaveMaster < 0 || world.Squads.Find(cold.SlaveMaster) is not { } master) return null;
        var table = world.Characters;
        return !master.Leader.IsNone && table.IsAlive(master.Leader) ? master.Leader : null;
    }

    /// <summary>The master squad of the slave's job <paramref name="k"/>: the squad it obeys, else the squad of the owner character the job names (found again from its saved handle after a load).</summary>
    internal static Squad? MasterOf(World world, int slot, CharacterCold cold, int k)
    {
        if (cold.SlaveMaster >= 0 && world.Squads.Find(cold.SlaveMaster) is { } known) return known;
        return Owner(world, slot, cold, k) is { } owner && world.Characters.Cold(owner.Slot) is { SquadId: >= 0 and var squad } ? world.Squads.Find(squad) : null;
    }

    /// <summary>The owner character of job <paramref name="k"/>: its character, found again from the saved handle after a load (as <c>ProductionSystem</c>'s follow jobs); alive and not the slave itself.</summary>
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
/// The Obedient Slave job as the decision step's contributor and the act-out of its work (rows 189 and 190). Step 3 asks it while the slave state is 1 and the character is not sneaking; it picks
/// among the owner squad's Slave role list in their hours by desire x weight (x1.25 for the goal running, a tie to the later-listed, <c>FUN_14050df10</c>) and starts the best that Meitou
/// acts out: 171 Following master (the owner, else the master squad's leader: Task_FollowLeader follows the slave owner, docs/game/pathfinding.md), 189 / 190 Worker (a random workplace of the town the
/// squad's leader stands in, <see cref="AiSystem.WorkAt"/>; the same standing at the building at its work clip as the AI's), 14 Idleness. The other goals of the lists (172 Getting in my
/// cage, the combat goals) are not acted out for a player character.
/// </summary>
sealed class ObedientJobs : IJobSource, ITaskHandler
{
    /// <summary>How near a worker stands to its building (units; the AI's, <c>Task_OperateMachine</c>'s <c>use range</c> is not read).</summary>
    const float WorkReach = 30;
    /// <summary>The x1.25 of the goal running now (docs/game/ai-tasks.md "Desire and commitment").</summary>
    const float Continuity = 1.25f;
    /// <summary>A walk that ends short of the building this many times gives it up for another pick.</summary>
    const int MaxFails = 3;

    sealed class WorkSpot
    {
        public required TownBuilding Building;
        public required string Clip;
        public string? Functionality;
        public int Epoch;
        public int Fails;
    }

    // The building each slave works at and the picks it made (runtime only, never hashed: the pick is a roll of the character and the epoch).
    readonly Dictionary<CharacterId, WorkSpot> works = [];

    readonly record struct Choice(TaskType Type, float Score);

    public bool Urgent(World world, int slot, int k) => ObedientSlave.Obeys(world.Characters.Cold(slot)!);

    public DecisionCheck CanRun(World world, int slot, int k)
    {
        var cold = world.Characters.Cold(slot)!;
        if (!ObedientSlave.Obeys(cold)) return DecisionCheck.No(cold.SlaveState == 1 ? "sneaking" : "not a slave");
        if (Choose(world, slot, cold, k) is not { } pick) return DecisionCheck.No("no goal of its master's Slave list runs");
        return Continues(world, slot, cold, k, pick.Type) ? DecisionCheck.Runs : DecisionCheck.Run;
    }

    public void Start(World world, int slot, int k)
    {
        var table = world.Characters;
        var cold = table.Cold(slot)!;
        if (!ObedientSlave.Obeys(cold) || Choose(world, slot, cold, k) is not { } pick) return;
        switch (pick.Type)
        {
            case TaskType.FOLLOW_SLAVEMASTER when FollowTarget(world, slot, cold, k) is { } target:
                MovementSystem.Follow(world, new FollowOrder([table.IdOf(slot)], target) { Tick = world.Tick }, TaskSource.Job, k, TaskType.FOLLOW_SLAVEMASTER);
                break;
            case TaskType.AUTO_LABOURING_MINES or TaskType.AUTO_LABOURING_MINES_PRETEND:
                StartWork(world, slot, cold, k, pick.Type);
                break;
            case TaskType.IDLE:
                TaskStart.Start(world, slot, new TaskRequest(CharacterTask.Idle, TaskType.IDLE, TaskSource.Job) { Job = k });
                break;
        }
    }

    /// <summary>The slave's task is this job's and no goal of the list runs now (sneaking, or the list is gone): the task ends and the step decides the character on its own goals.</summary>
    public void Stop(World world, int slot, int k)
    {
        var cold = world.Characters.Cold(slot)!;
        if (cold.Running is { Source: TaskSource.Job, Implicit: false } run && run.Job == k && run.Row == TaskType.FOLLOW_SLAVEMASTER) cold.FollowOrder = CharacterId.None;
        TaskStart.End(world, slot, TaskEnd.Stopped);
    }

    // ------------------------------------------------------------------ the pick

    /// <summary>
    /// The best goal of the master's Slave list now (the scorer of <c>FUN_14050df10</c>: only the priorities 1 to 3 are loaded, <c>FUN_14026b790</c> mask 0xe), among those Meitou acts out; null when
    /// the slave cannot act (down, carried, carrying) or none runs.
    /// </summary>
    Choice? Choose(World world, int slot, CharacterCold cold, int k)
    {
        if (cold.Medical is { Incapacitated: true } || !cold.CarriedBy.IsNone || !cold.Carrying.IsNone) return null;
        if (ObedientSlave.MasterOf(world, slot, cold, k) is not { Ai: { } board }) return null;
        var list = board.Roles[(int)AiRole.Slave];
        int hour = world.Hour;
        Choice? best = null;
        for (int i = 0; i < list.Count; i++)
        {
            var goal = list[i];
            var type = goal.Task.Type;
            if (goal.Task.Priority is < TaskPriority.Fluff or > TaskPriority.Urgent || !goal.InWindow(hour) || !TaskStart.Eligible(world, cold, type)) continue;
            float desire = Desire(world, slot, cold, k, type);
            if (!(desire > 0)) continue;
            float score = desire * goal.Weight;
            if (cold.Running is { Source: TaskSource.Job, Implicit: false } run && run.Job == k && run.Row == type) score *= Continuity;
            if (!(score > 0)) continue;
            // The original keys candidates by score and raises a taken key by 0.0001: of equal scores the later-listed ranks first.
            if (best is null || score >= best.Value.Score) best = new Choice(type, score);
        }
        return best;
    }

    /// <summary>How much the goal is wanted: 1 when its finder finds a target, 0 when it finds none or Meitou does not act it out.</summary>
    float Desire(World world, int slot, CharacterCold cold, int k, TaskType type) => type switch
    {
        TaskType.FOLLOW_SLAVEMASTER => FollowTarget(world, slot, cold, k) is null ? 0 : 1,
        TaskType.AUTO_LABOURING_MINES or TaskType.AUTO_LABOURING_MINES_PRETEND => world.SystemOf<AiSystem>() is { } ai && ai.HasWorkplaces(TownPoint(world, slot, cold)) ? 1 : 0,
        TaskType.IDLE => 1,
        _ => 0,
    };

    /// <summary>Whom 171 follows: the owner, else the master squad's leader.</summary>
    static CharacterId? FollowTarget(World world, int slot, CharacterCold cold, int k)
    {
        if (ObedientSlave.Owner(world, slot, cold, k) is { } owner) return owner;
        var table = world.Characters;
        return ObedientSlave.MasterOf(world, slot, cold, k) is { } master && !master.Leader.IsNone && master.Leader.Slot != slot && table.IsAlive(master.Leader) ? master.Leader : null;
    }

    /// <summary>
    /// The point whose town the worker finders ask (<c>FUN_1408cac60</c> reads the platoon blackboard's current town, <c>+0x88</c>, which the AI's rule writes from the squad leader,
    /// docs/game/ai.md): where the slave's own squad's leader stands, else the slave.
    /// </summary>
    static Vector3 TownPoint(World world, int slot, CharacterCold cold)
    {
        var table = world.Characters;
        if (world.Squads.Find(cold.SquadId) is { } squad && !squad.Leader.IsNone && table.TryResolveNext(squad.Leader, out int leader)) return table.Next[leader].Position;
        return table.Next[slot].Position;
    }

    // ------------------------------------------------------------------ acting out

    /// <summary>Whether the character's task is already this goal's act-out (the plan goes on): a follow of the target, a walk to the workplace or standing at it, standing idle on 14.</summary>
    bool Continues(World world, int slot, CharacterCold cold, int k, TaskType type)
    {
        if (cold.Running is not { Source: TaskSource.Job, Implicit: false } run || run.Job != k || run.Row != type) return false;
        var task = (CharacterTask)world.Characters.Next[slot].Task;
        switch (type)
        {
            case TaskType.FOLLOW_SLAVEMASTER:
                return run.Runs(task) && FollowTarget(world, slot, cold, k) is { } target && cold.FollowOrder == target;
            case TaskType.IDLE:
                return run.Runs(task);
            default:
                return works.TryGetValue(world.Characters.IdOf(slot), out var spot) && run.Runs(task) && (task == CharacterTask.GoTo || cold.ActionClip == spot.Clip);
        }
    }

    void StartWork(World world, int slot, CharacterCold cold, int k, TaskType type)
    {
        var table = world.Characters;
        var id = table.IdOf(slot);
        if (world.SystemOf<AiSystem>() is not { } ai) return;
        var at = TownPoint(world, slot, cold);
        bool again = works.TryGetValue(id, out var spot) && cold.Running is { Source: TaskSource.Job, Implicit: false } run && run.Job == k && run.Row == type;
        if (again && table.Next[slot].Task == (byte)CharacterTask.Idle && cold.Running!.Task == CharacterTask.GoTo)
        {
            // The walk ended: at the building it works, short of it the walk is asked again, a few times.
            if (Near(table.Next[slot].Position, spot!.Building)) { Work(world, slot, k, type); return; }
            if (++spot.Fails < MaxFails) { Walk(world, slot, k, type, spot.Building); return; }
        }
        // A new pick: another roll each time it starts working anew.
        int epoch = spot is null ? 0 : spot.Epoch + 1;
        if (ai.WorkAt(world, id, at, epoch) is not { } found) return;
        works[id] = new WorkSpot { Building = found.Building, Clip = found.Clip, Functionality = found.Functionality, Epoch = epoch };
        if (Near(table.Next[slot].Position, found.Building)) Work(world, slot, k, type);
        else Walk(world, slot, k, type, found.Building);
    }

    static bool Near(Vector3 at, TownBuilding building)
    {
        float dx = building.Position.X - at.X, dz = building.Position.Z - at.Z;
        return dx * dx + dz * dz <= WorkReach * WorkReach;
    }

    static void Walk(World world, int slot, int k, TaskType type, TownBuilding building) =>
        TaskStart.Start(world, slot, new TaskRequest(CharacterTask.GoTo, type, TaskSource.Job) { Job = k, Goal = new Vector2(building.Position.X, building.Position.Z), Mode = SpeedMode.Walk });

    static void Work(World world, int slot, int k, TaskType type) =>
        TaskStart.Start(world, slot, new TaskRequest(CharacterTask.Idle, type, TaskSource.Job) { Job = k });

    /// <summary>The walk to the workplace (rows 189 and 190 start as <see cref="CharacterTask.GoTo"/>) and the standing at it (<see cref="CharacterTask.Idle"/>, facing the building at its work clip).</summary>
    void ITaskHandler.Start(World world, int slot, in TaskRequest request)
    {
        var table = world.Characters;
        var cold = table.Cold(slot)!;
        if (request.Task == CharacterTask.GoTo)
        {
            MovementSystem.GoTo(table, slot, request.Goal!.Value, request.Mode ?? cold.RunSpeed);
            cold.ArriveWithin = WorkReach;
            return;
        }
        MovementSystem.Stop(table, slot);
        if (!works.TryGetValue(table.IdOf(slot), out var spot)) return;
        ref var n = ref table.Next[slot];
        float dx = spot.Building.Position.X - n.Position.X, dz = spot.Building.Position.Z - n.Position.Z;
        if (dx * dx + dz * dz > 1e-4f) n.Yaw = MathF.Atan2(dx, dz);
        cold.ActionClip = spot.Clip;
        cold.ActionWork = AiSystem.StandingWork(spot.Functionality, spot.Clip);
    }

    /// <summary>Another task takes the worker: the work clip goes.</summary>
    void ITaskHandler.End(World world, int slot, CharacterTask task, TaskEnd reason)
    {
        var cold = world.Characters.Cold(slot)!;
        if (works.TryGetValue(world.Characters.IdOf(slot), out var spot) && cold.ActionClip == spot.Clip)
        {
            cold.ActionClip = null;
            cold.ActionWork = null;
        }
    }
}
