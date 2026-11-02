using Meitou.Data.Gameplay.Ai;
using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Data.World;
using Meitou.Simulation.Construction;

namespace Meitou.Simulation.Production;

// Job lists (Perma-Jobs; docs/simulation.md "Production", the original's rules in docs/game/ai-tasks.md "Perma-jobs"): the decision step takes the first job of a player character's
// list that can run (ListJobs here is the production pick's contributor), so a higher job takes over as soon as it can run and a blocked one passes to the next. Meitou runs Operating machine (87, the
// operator below), Operating automatic machine (152: an operator that only hauls for it), Hauling to (124: a machine's output carried to the job's storage, CharacterTask.Haul; on an automatic machine, carried inputs into it), Engineer (125: ConstructionSystem builds the site its finder picks) and Turret guard (234: manning the job's turret as an operator). Blocked
// uses one test for starting and for going on.
public sealed partial class ProductionSystem
{
    /// <summary>
    /// How often (ticks) the decision step asks again for a player character idle or on a job's task, staggered by slot (<see cref="DecisionSystem.ReAsked"/>; engine choice: the original
    /// re-plans when its task ends or its commitment runs out).
    /// </summary>
    public const int JobCheckTicks = 10;

    void AddJobs(World world, AddJob add)
    {
        if (Site(world, add.Site) is null) return;
        var table = world.Characters;
        foreach (var id in Who(world, add.Characters))
            if (table.TryResolveNext(id, out int slot) && table.Cold(slot) is { IsPlayer: true } cold) PermaJobs.Add(cold, add.Type, new PermaJob(add.Type, add.Site));
    }

    void AddOrderJobs(World world, AddOrderJob add)
    {
        var table = world.Characters;
        var ordered = add.Ordered;
        // A first aid on a robot is the original's robot repair order (60), whose job is Robotics.
        if (ordered == TaskType.FIRST_AID_ORDER && table.TryResolveNext(add.Target, out int t) && table.Cold(t)?.Race is { IsRobot: true }) ordered = TaskType.FIRST_AID_ROBOT;
        // Follow (31) and Bodyguard (45) on a character become the jobs that follow it (docs/game/ai-tasks.md "Finders of the jobs": they act on the job's own target).
        bool onCharacter = ordered is TaskType.BODYGUARD or TaskType.STAY_CLOSE_TO_TARGET;
        foreach (var id in Who(world, add.Characters))
        {
            if (!table.TryResolveNext(id, out int slot) || table.Cold(slot) is not { IsPlayer: true } cold) continue;
            if (!onCharacter) PermaJobs.Add(cold, ordered, PermaJob.Untargeted(ordered));
            else if (id != add.Target && table.IsAlive(add.Target))
            {
                PermaJobs.Add(cold, ordered, PermaJob.OfCharacter(ordered, add.Target));
                // The order giver then drops what would close a circle back to the ordered character (FUN_1405c8c10), whether or not the job was new.
                MovementSystem.BreakCircle(world, id);
            }
        }
    }

    void RemoveJobAt(World world, RemoveJob remove)
    {
        var table = world.Characters;
        if (table.TryResolveNext(remove.Character, out int slot)) RemoveJob(world, slot, remove.Index);
    }

    /// <summary>Takes the job at <paramref name="index"/> out of a character's list (the list's X, and the circle break of <see cref="MovementSystem"/>): the task of the job it is on ends, the running index follows (serial phases only).</summary>
    internal static void RemoveJob(World world, int slot, int index)
    {
        var table = world.Characters;
        if (table.Cold(slot) is not { } cold || (uint)index >= (uint)cold.Jobs.Count) return;
        if (PermaJobs.ListJob(cold, (CharacterTask)table.Next[slot].Task) is { Implicit: false } on && on.Job == index) End(world, slot);
        cold.Jobs.RemoveAt(index);
        DecisionSystem.Request(cold);
        // Any job's provenance keeps pointing at its job; a treatment, lift or loot whose job left the list runs on as a job pass's (no index).
        if (cold.Running is { Source: TaskSource.Job, Implicit: false, Job: >= 0 } job && job.Job >= index) cold.Running = job with { Job = job.Job == index ? -1 : job.Job - 1 };
    }

    void MoveJobTo(World world, MoveJob move)
    {
        if (!world.Characters.TryResolveNext(move.Character, out int slot) || world.Characters.Cold(slot) is not { } cold) return;
        int count = cold.Jobs.Count;
        if ((uint)move.From >= (uint)count || (uint)move.To >= (uint)count || move.From == move.To) return;
        var job = cold.Jobs[move.From];
        cold.Jobs.RemoveAt(move.From);
        cold.Jobs.Insert(move.To, job);
        DecisionSystem.Request(cold);
        // The job's provenance keeps pointing at it.
        if (cold.Running is not { Source: TaskSource.Job, Implicit: false, Job: >= 0 } run) return;
        int k = run.Job;
        if (k == move.From) k = move.To;
        else if (move.From < k && k <= move.To) k--;
        else if (move.To <= k && k < move.From) k++;
        if (k != run.Job) cold.Running = run with { Job = k };
    }

    /// <summary>
    /// The production pick's list jobs as the decision step's contributor (<see cref="IJobSource"/>, step 5; 234 also step 3): Operating machine 87, Operating automatic machine 152, Hauling
    /// to 124, Training 97, Turret guard 234, Engineer 125 and the follow jobs 31, 45, 289; and the carrying jobs 138, 223, 273 (<see cref="ErrandJobs"/>). A job runs while
    /// <see cref="Runnable"/> holds; one that no longer does and that nothing replaces ends (<see cref="End"/>).
    /// </summary>
    public void RegisterSources(DecisionSystem decisions)
    {
        var jobs = new ListJobs(this);
        foreach (var type in (ReadOnlySpan<TaskType>)[TaskType.OPERATE_MACHINERY, TaskType.OPERATE_AUTOMATIC_MACHINERY, TaskType.OPERATE_STORAGE, TaskType.USE_TRAINING_DUMMY,
            TaskType.MAN_A_TURRET_PLAYER_JOB, TaskType.JOB_BUILDER, TaskType.BODYGUARD, TaskType.STAY_CLOSE_TO_TARGET, TaskType.Unnamed289])
            decisions.RegisterJob(type, jobs);
        var errands = new ErrandJobs(this);
        foreach (var type in (ReadOnlySpan<TaskType>)[TaskType.FIND_A_DEAD_GUY, TaskType.PICKUP_INTRUDERS, TaskType.PICKUP_INTRUDERS_TOWN]) decisions.RegisterJob(type, errands);
        decisions.Register(new ImplicitJobs(this));
    }

    sealed class ListJobs(ProductionSystem owner) : IJobSource
    {
        public DecisionCheck CanRun(World world, int slot, int k)
        {
            var table = world.Characters;
            var cold = table.Cold(slot)!;
            var task = (CharacterTask)table.Next[slot].Task;
            var type = cold.Jobs[k].Type;
            bool runs = PermaJobs.ListJob(cold, task) is { Implicit: false } on && on.Job == k;
            if (PermaJobs.Follows(type) && cold.Running is { Source: TaskSource.Job, Implicit: false } follow && follow.Job == k && PermaJobs.Follows(follow.Row))
            {
                // Its fight is a step of the row (31 and 45 need state 16, which 13 gives): it runs; once over, the follow is taken up again.
                if (task == CharacterTask.Attack && follow.Runs(task)) return DecisionCheck.Runs;
                if (MovementSystem.FightStepOver(table, slot, cold)) return FollowedBy(world, slot, cold, k) is not null ? DecisionCheck.Run : DecisionCheck.No("its character is gone");
            }
            // An operator carrying or carried looks at no job (and keeps the one it is on).
            if (!Able(cold)) return runs ? DecisionCheck.Runs : DecisionCheck.No("carrying, carried or down");
            // 234's effect (using the turret) is planned through 146, whose list B has !158: not while a melee attacker is within 40 (docs/game/ai-tasks.md "Precedence").
            if (type == TaskType.MAN_A_TURRET_PLAYER_JOB && world.SystemOf<Combat.CombatSystem>() is { } combat && CharacterFacts.ImmediateThreat(combat, world, slot))
                return DecisionCheck.No("a melee threat within 40 (146 needs !158)");
            if (!owner.Runnable(world, slot, cold, k)) return DecisionCheck.No("blocked");
            return runs ? DecisionCheck.Runs : DecisionCheck.Run;
        }

        public void Start(World world, int slot, int k)
        {
            var table = world.Characters;
            var cold = table.Cold(slot)!;
            if (MovementSystem.FightStepOver(table, slot, cold) && cold.Running is { Source: TaskSource.Job } run && run.Job == k && MovementSystem.FollowAgain(world, slot, cold)) return;
            owner.StartJob(world, slot, k);
        }

        /// <summary>
        /// A Turret guard (234, kind 2) is urgent while enemies are sensed (AI <c>+0xbc</c>, the senses' <c>numEnemies</c>, <see cref="SenseBlock.Enemies"/>; <c>FUN_14050e5b0</c>).
        /// </summary>
        public bool Urgent(World world, int slot, int k) =>
            world.Characters.Cold(slot)!.Jobs[k].Type == TaskType.MAN_A_TURRET_PLAYER_JOB && CharacterFacts.Senses(world, slot) is { Enemies: > 0 };

        public void Stop(World world, int slot, int k) => End(world, slot);
    }

    /// <summary>
    /// The implicit jobs (squad AI options) as step 5's source after the list (the original's lowest keys, <c>FUN_14050e5b0</c>): a character idle, on a standing task or on a job's task
    /// (a list job's that could not run on, or an implicit one) takes the one <see cref="ImplicitPick"/> gives; it runs while that is the one it is on (the same type and site). One on an
    /// implicit job that no longer holds ends it (<see cref="Passed"/>: the original rebuilds the entry on every pass, so nothing carries over).
    /// </summary>
    sealed class ImplicitJobs(ProductionSystem owner) : IDecisionSource
    {
        (long Tick, int Slot, TaskType Type, string Site) picked = (-1, -1, TaskType.None, "");

        public string Name => "Implicit jobs";
        public DecisionStep Step => DecisionStep.Jobs;
        /// <summary>After the list (its key 0).</summary>
        public int Key => 1;

        public DecisionCheck CanRun(World world, int slot)
        {
            var table = world.Characters;
            var cold = table.Cold(slot)!;
            var task = (CharacterTask)table.Next[slot].Task;
            var job = PermaJobs.ListJob(cold, task);
            if (task == CharacterTask.Attack || cold.InCombat) return DecisionCheck.No("fighting");
            if (job is null && task is not (CharacterTask.Idle or CharacterTask.Wander or CharacterTask.Follow)) return DecisionCheck.No("busy");
            if (!Able(cold)) return job is { Implicit: true } ? DecisionCheck.Runs : DecisionCheck.No("carrying, carried or down");
            if (owner.ImplicitPick(world, slot) is not { } chosen) return DecisionCheck.No("none holds");
            string? current = chosen.Type == TaskType.DITCH_ALL_RESOURCES ? cold.HaulTo : cold.OperateSite;
            if (job is { Implicit: true } && chosen.Type == job.Row && chosen.Site == current) return DecisionCheck.Runs;
            picked = (world.Tick, slot, chosen.Type, chosen.Site);
            return DecisionCheck.Run;
        }

        public void Start(World world, int slot)
        {
            if (picked.Tick == world.Tick && picked.Slot == slot) owner.StartImplicit(world, slot, picked.Type, picked.Site);
        }

        public void Passed(World world, int slot, in DecisionCheck check)
        {
            var table = world.Characters;
            var cold = table.Cold(slot)!;
            if (Able(cold) && PermaJobs.ListJob(cold, (CharacterTask)table.Next[slot].Task) is { Implicit: true }) End(world, slot);
        }
    }

    /// <summary>How far (X/Z) from where it idles a character sits down on a chair (engine choice: the original's sitter looks in its home town only).</summary>
    public const float SeatRange = 600;

    /// <summary>
    /// The implicit job of a character whose list has none to run (docs/game/ai-tasks.md "Perma-jobs": the original's lowest keys, in this order), with the jobs switch on, not
    /// sneaking and not a slave; Sleep and Sit not while it holds position. <see cref="SquadAi.Ditch"/>: carried items a storage takes go there (242; only with no queued orders). <see cref="SquadAi.Sleep"/>: an injured character lies in the bed
    /// the bed search finds (153, whose target finder it is, <b>Verified</b>; <see cref="Locks.FixtureRegistry.FindBed"/>) until healed (then 257 gets it up). <see cref="SquadAi.Sit"/>: a free
    /// chair (275). The one it is on is kept while it holds (a bed within <see cref="Locks.FixtureRegistry.BedSearchRange"/>). Distances are from the character.
    /// </summary>
    (TaskType Type, string Site)? ImplicitPick(World world, int slot)
    {
        var table = world.Characters;
        var cold = table.Cold(slot)!;
        var options = world.Player.SquadAi;
        // None while sneaking (FUN_1405c9750; a fighting character is not Able anyway).
        if ((cold.Orders & CharacterOrders.Jobs) == 0 || (cold.Orders & CharacterOrders.Sneak) != 0 || cold.SlaveState != 0 || options == SquadAi.None) return null;
        var at = table.Next[slot].Position;
        var on = PermaJobs.ListJob(cold, (CharacterTask)table.Next[slot].Task) is { Implicit: true } running ? running.Row : TaskType.None;
        if ((options & SquadAi.Ditch) != 0)
        {
            // The 242 entry is rebuilt every pass (FUN_14050e5b0): the trip goes on only while no order is queued and its storage still takes something carried.
            if (on == TaskType.DITCH_ALL_RESOURCES && cold.HaulTo is { } keep && !PlayerOrders.Holds(world, slot) && cold.Inventory is { } held
                && Site(world, keep) is { Complete: true } keptSite && keptSite.Owner == world.Player.Faction
                && Ensure(world, keptSite) is { } keptStore && keptStore.Data.IsStorage && keptStore.Storage is not null && Ditchable(held).Any(item => Stores(keptStore, item)))
                return (TaskType.DITCH_ALL_RESOURCES, keep);
            if (!PlayerOrders.Holds(world, slot) && cold.Inventory is { } inv
                && NearestSite(world, at, Settings.HaulRange, (_, m) => m.Data.IsStorage && m.Storage is not null && Ditchable(inv).Any(item => Stores(m, item))) is { } store)
                return (TaskType.DITCH_ALL_RESOURCES, store);
        }
        // Sleep and Sit not while holding position (FUN_1402b2b90).
        if (Orders.Holding(cold)) return null;
        if ((options & SquadAi.Sleep) != 0 && Injured(cold) && Beds is not null)
        {
            if (on == TaskType.GO_HOME_AND_GO_TO_BED && cold.OperateSite is { } bed && world.Fixtures.Find(bed) is { } kept
                && Gap(kept.Position, at) <= Locks.FixtureRegistry.BedSearchRange)
                return (TaskType.GO_HOME_AND_GO_TO_BED, bed);
            if (Beds.FindFor(world, slot) is { } free) return (TaskType.GO_HOME_AND_GO_TO_BED, free.Id);
        }
        if ((options & SquadAi.Sit) != 0)
        {
            if (on == TaskType.SIT_AROUND && cold.OperateSite is { } seat && Site(world, seat) is { } kept && Gap(kept.Position, at) <= SeatRange)
                return (TaskType.SIT_AROUND, seat);
            if (NearestSite(world, at, SeatRange, (s, m) => IsSeat(m) && Free(world, cold, s, m)) is { } free) return (TaskType.SIT_AROUND, free);
        }
        return null;
    }

    /// <summary>The training dummy's gain per second of the continuous entry (<b>Verified</b>, <c>UseableStuff</c> vfunc 0x4b0: <c>FUN_1408c64f0</c> with 2.0).</summary>
    public const float TrainingRate = 2;

    /// <summary>
    /// A user at a training dummy (<c>Task_UseTrainingDummy</c> slot 2 and <c>UseableStuff</c> vfunc 0x4b0, <b>Verified</b> by decompile): facing it, the building's <c>stat used</c> rises every
    /// tick through <see cref="XpService.Capped"/> at <see cref="TrainingRate"/>, up to the building's cap (<c>output rate</c>: MkI 6, MkII 11, MkIII 16, whose descriptions promise levels 5, 10 and 15,
    /// <b>Observed</b>; the field read as the cap is +0x1a4, not traced to the record). The gain also depends on the raw stat, not the injured one.
    /// </summary>
    void Train(ref CharacterHot n, CharacterCold cold, BuildSite site, Machine m, float dt)
    {
        if (n.Velocity.X == 0 && n.Velocity.Z == 0) n.Yaw = MathF.Atan2(site.Position.X - n.Position.X, site.Position.Z - n.Position.Z);
        m.Operators++;
        cold.Operating = true;
        if (cold.Stats is { } stats && cold.Race is { } race) xp.Capped(stats, race, (Meitou.Data.Gameplay.Bodies.StatsEnumerated)m.Data.Stat, dt, TrainingRate, m.Data.OutputRate);
    }

    /// <summary>BF_CHAIR and BF_THRONE (17, 24; docs/game/ai-tasks.md "Town goals").</summary>
    static bool IsSeat(Machine m) => m.Data.Function is TownBuildings.ChairFunction or ThroneFunction;

    const int ThroneFunction = 24;

    /// <summary>Nobody else lies or sits on it.</summary>
    static bool Free(World world, CharacterCold cold, BuildSite site, Machine m) => OperatorsOf(world, site.Id) - (cold.OperateSite == site.Id ? 1 : 0) < m.Data.MaxOperators;

    /// <summary>A body part below full health (<b>engine choice</b>: the original's injury test is Unknown; its 257 is "get out of bed once healed").</summary>
    static bool Injured(CharacterCold cold) => cold.Medical is { Dead: false } medical && medical.Parts.Any(p => p.Fraction is >= 0 and < 1);

    /// <summary>The player's complete building nearest <paramref name="at"/> within <paramref name="range"/> whose machine passes <paramref name="ok"/> (ties: the lower site id).</summary>
    string? NearestSite(World world, Vector3 at, float range, Func<BuildSite, Machine, bool> ok)
    {
        string? best = null;
        float bestGap = float.MaxValue;
        foreach (var m in world.Machines.All)
        {
            if (Site(world, m.Site) is not { Complete: true } site || site.Owner != world.Player.Faction) continue;
            float gap = Gap(site.Position, at);
            if (gap > range || gap > bestGap || (gap == bestGap && string.CompareOrdinal(m.Site, best) > 0) || !ok(site, m)) continue;
            best = m.Site;
            bestGap = gap;
        }
        return best;
    }

    void StartImplicit(World world, int slot, TaskType type, string siteId)
    {
        var table = world.Characters;
        var cold = table.Cold(slot)!;
        var task = type switch { TaskType.GO_HOME_AND_GO_TO_BED => CharacterTask.Sleep, TaskType.DITCH_ALL_RESOURCES => CharacterTask.Haul, _ => CharacterTask.Operate };
        // A bed fixture: the bed system walks it there and lays it in (task Sleep); the job keeps the bed's id.
        if (task == CharacterTask.Sleep && (world.Fixtures.Find(siteId) is null || Beds is null)) return;
        if (TaskStart.Start(world, slot, new TaskRequest(task, type, TaskSource.Job) { Site = siteId, Implicit = true }) != StartResult.Started) return;
        if (task == CharacterTask.Sleep && table.Next[slot].Task == (byte)CharacterTask.Sleep) cold.OperateSite = siteId;
    }

    /// <summary>A sitter or sleeper at its chair or bed: on the seat's origin facing its way, as the original's <c>Task_OperateMachine</c> (docs/game/ai-tasks.md "Town goals").</summary>
    void Settle(World world, ref CharacterHot n, CharacterCold cold, BuildSite site, Machine m)
    {
        n.Position = site.Position;
        n.Yaw = SeatFacing(world, site, m);
        n.Velocity = default;
        m.Operators++;
        cold.Operating = true;
    }

    /// <summary>
    /// The square of how far (3D) from the character a job's target may be (<b>Verified</b>, <c>AI::isTargetInRangeForOrders</c> <c>FUN_140597790</c> in the default ROAM_FAR mode: the walk a job
    /// plans needs StateType 27 <c>isMovementAllowed</c>; about 6517 units). Farther, the job is skipped, so a character away from its base does not walk back for a job there.
    /// </summary>
    public const float JobRangeSq = 4.246733e7f;

    /// <summary>
    /// A job of the list that may run now: Meitou runs its type, the jobs switch is on (or the type ignores it), the character is not getting up, its target is within
    /// <see cref="JobRangeSq"/>, and it is not blocked.
    /// </summary>
    bool Runnable(World world, int slot, CharacterCold cold, int k)
    {
        var job = cold.Jobs[k];
        if (!TaskStart.Eligible(world, cold, job.Type)) return false;
        if (PermaJobs.Follows(job.Type)) return ((cold.Orders & CharacterOrders.Jobs) != 0 || job.Type != TaskType.Unnamed289) && FollowedBy(world, slot, cold, k) is not null;
        if (!PermaJobs.Runs(job.Type) || ((cold.Orders & CharacterOrders.Jobs) == 0 && !PermaJobs.IgnoresSwitch(job.Type))) return false;
        // The walk to a job needs state 27, false while the character gets up (FUN_1405a3370).
        if (CharacterFacts.GettingUp(cold, world.Tick)) return false;
        var at = world.Characters.Next[slot].Position;
        // The engineer job ignores its target: its finder picks any site (the original's row 125 finder, docs/game/buildings-production.md "Builders and dismantle"); a pick out of range fails the job, and so does a character with no working arm (row 2's state 38).
        if (job.Type == TaskType.JOB_BUILDER) return ConstructionSystem.HasWorkingArm(cold) && Construction?.EngineerSite(world, slot) is { } pick && Vector3.DistanceSquared(at, pick.Position) < JobRangeSq;
        if (job.Site is { } id && Site(world, id) is { } site && Vector3.DistanceSquared(at, site.Position) >= JobRangeSq) return false;
        return !Blocked(world, cold, job);
    }

    /// <summary>
    /// The character a Bodyguard (45), Staying close (31) or animal Staying close (289) job follows (the row has no finder: it acts on the job's own target, docs/game/ai-tasks.md "Finders of the jobs"): the job's character,
    /// found again from its saved handle after a load, alive and not the follower itself; for 31 and 289 also standing (the rows' state 7, <c>!isTargetDown</c>). Null: the job is blocked.
    /// </summary>
    static CharacterId? FollowedBy(World world, int slot, CharacterCold cold, int k)
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
        if (target.IsNone || target.Slot == slot || !table.TryResolveNext(target, out int t)) return null;
        return job.Type != TaskType.BODYGUARD && CharacterFacts.TargetDown(table.Cold(t)) ? null : target;
    }

    /// <summary>
    /// Whether a job cannot run (the original skips such a job: an unmet start condition the planner cannot meet, or the target finder found nothing; docs/game/ai-tasks.md "Perma-jobs").
    /// Operating: the building is gone or not an operable machine, its operator places are taken by others, or it has no work (<see cref="HasWork"/>). Hauling to: the storage is gone, and
    /// the character carries nothing it takes and no machine within <see cref="ProductionSettings.HaulRange"/> has an output it takes. Automatic machines (152, and 124 on one): <see cref="AutoWork"/>.
    /// </summary>
    bool Blocked(World world, CharacterCold cold, PermaJob job)
    {
        if (job.Site is not { } id || Site(world, id) is not { Complete: true } site || Ensure(world, site) is not { } m) return true;
        switch (job.Type)
        {
            case TaskType.OPERATE_MACHINERY:
                if (IsSeat(m)) return SeatOccupied(world, site) || OperatorsOf(world, id) - (cold.OperateSite == id ? 1 : 0) >= m.Data.MaxOperators;
                if (!m.Data.Operable) return true;
                if (OperatorsOf(world, id) - (cold.OperateSite == id ? 1 : 0) >= m.Data.MaxOperators || !HasWorkingArm(cold)) return true;
                return !HasWork(world, site, m, cold);
            case TaskType.MAN_A_TURRET_PLAYER_JOB:
                // Turret guard (234, the job a Shift order to man a turret becomes): the building is a turret no one else mans.
                if (m.Data.Function != Meitou.Data.World.ObjectRanges.TurretFunction || !HasWorkingArm(cold)) return true;
                return OperatorsOf(world, id) - (cold.OperateSite == id ? 1 : 0) >= m.Data.MaxOperators;
            case TaskType.USE_TRAINING_DUMMY:
                // Training (97): a dummy with a place free for a character with an arm that works (the row's state 38; the rest of its conditions, 73, is Unknown). It goes on at the cap, as the original's has no end.
                if (!m.Data.IsTraining || OperatorsOf(world, id) - (cold.OperateSite == id ? 1 : 0) >= m.Data.MaxOperators) return true;
                return !HasWorkingArm(cold);
            case TaskType.OPERATE_AUTOMATIC_MACHINERY:
                return !m.Data.IsAutomatic || !m.Data.IsProducer || !AutoWork(world, site, m, cold, fetch: true);
            case TaskType.OPERATE_STORAGE:
                // On an automatic machine (152's companion, the original's Task_FillMachine into it): what the character carries for its inputs.
                if (m.Data.IsAutomatic && m.Data.IsProducer) return !AutoWork(world, site, m, cold, fetch: false);
                if (!m.Data.IsStorage || m.Storage is null) return true;
                if (cold.Inventory is { } inv && inv.All().Any(i => Takes(world, m, i))) return false;
                return HaulSource(world, site, m) is null;
            default:
                return true;
        }
    }

    /// <summary>State 38 <c>hasOneWorkingArm</c>, which rows 87, 97, 146 and 234 need and no row provides, so the job cannot run without it.</summary>
    static bool HasWorkingArm(CharacterCold cold) => cold.Medical is { } m && (m.ArmWorks(left: true) || m.ArmWorks(left: false));

    /// <summary>
    /// A machine an operator can work (the original's start conditions of task 87: output not full, inputs not empty, power; docs/game/ai-tasks.md): it has power, and a bench a queued
    /// research or craft; a farm a dead crop to clear, a ripe one to harvest or a growing one with water (tended); inputs that hold something or that the operator can fill
    /// (it carries the item or, with <see cref="ProductionSettings.AutoHaul"/>, <see cref="NearestSource"/> finds it); and room in the output or, with AutoHaul, a storage that takes it.
    /// </summary>
    bool HasWork(World world, BuildSite site, Machine m, CharacterCold cold)
    {
        if (WorkFactor(world, m) <= 0) return false;
        if (m.Data.IsResearch) return world.Research.Queue.Count > 0;
        if (m.Farm is { } f)
        {
            if (f.Death >= m.Data.Farm!.DeathThreshold) return true;
            if (f.Growth >= 1) return f.Harvested < f.Plants.Length && Room(world, site, m);
            return Supplied(world, site, m, cold);
        }
        if (m.Craft is { Queue.Count: 0 }) return false;
        return Supplied(world, site, m, cold) && (m.Craft is not null || Room(world, site, m));
    }

    /// <summary>Every input holds something or the operator can fill it: it carries the item or, with AutoHaul, <see cref="NearestSource"/> finds it.</summary>
    bool Supplied(World world, BuildSite site, Machine m, CharacterCold cold)
    {
        foreach (var input in m.Inputs)
        {
            if (input.Rate <= 0 || input.Level + input.Count > 0) continue;
            if (cold.Inventory is { } inv && Carried(inv, input.Item).Any()) continue;
            if (Settings.AutoHaul && NearestSource(world, site, input.Item, cold) is not null) continue;
            return false;
        }
        return true;
    }

    /// <summary>
    /// Haul work at an automatic machine (engine reading of 152, whose goal is "no empty inputs", and of 124 on it): the character is on a trip for it, or carries an item an input has room for;
    /// with <paramref name="fetch"/> also an empty input a storage holds the item of, a full output a storage takes, or the machine's output carried that a storage takes.
    /// </summary>
    bool AutoWork(World world, BuildSite site, Machine m, CharacterCold cold, bool fetch)
    {
        if (cold.Inventory is not { } inv) return false;
        if (fetch && cold.HaulSite is not null && cold.OperateSite == site.Id) return true;
        foreach (var input in m.Inputs)
        {
            if (input.Rate <= 0) continue;
            if (input.Count < input.Recipe.Capacity && Carried(inv, input.Item).Any()) return true;
            if (fetch && input.Count == 0 && NearestSource(world, site, input.Item, cold) is not null) return true;
        }
        if (!fetch || m.Output is not { } output || items.Create(output.Recipe.Item, 1) is not { } one) return false;
        if (!output.Full && !Carried(inv, one.Record).Any()) return false;
        return NearestStorage(world, site, s => s.Data.Accepts(one.Record, one.Type) && Fits(s.Storage!, one)) is not null;
    }

    /// <summary>The output has room, or with AutoHaul a storage takes it.</summary>
    bool Room(World world, BuildSite site, Machine m) =>
        m.Output is { } o && (!o.Full || Settings.AutoHaul && items.Create(o.Recipe.Item, 1) is { } one && NearestStorage(world, site, s => s.Data.Accepts(one.Record, one.Type) && Fits(s.Storage!, one)) is not null);

    /// <summary>A carried item a "Hauling to" job takes to its storage: a loose stack of something a player machine makes (not the character's own kit), that the storage takes and has room for.</summary>
    static bool Takes(World world, Machine store, Items.ItemInstance item) =>
        Stores(store, item) && world.Machines.All.Any(s => !s.Data.IsStorage && s.Output?.Recipe.Item == item.Record);

    /// <summary>A loose stack (not the character's own kit) the storage takes and has room for.</summary>
    internal static bool Stores(Machine store, Items.ItemInstance item) =>
        item.Section is "main" or "backpack_content" && !item.LockedOn && item.Contents is null && store.Data.Accepts(item.Record, item.Type) && Fits(store.Storage!, item);

    /// <summary>
    /// What 242 Ditch all resources carries off (finder <c>FUN_1405a59a0</c>, its item collection <c>FUN_140749490</c>, <b>Verified</b>): ITEMs with <c>item function</c> 0 or 15, and food
    /// (function 3) but the first, in inventory order.
    /// </summary>
    static IEnumerable<Items.ItemInstance> Ditchable(Items.Inventory inv)
    {
        bool fed = false;
        foreach (var item in inv.All())
        {
            if (item.Type != Meitou.Data.Fcs.FcsRecordType.ITEM) continue;
            if (item.ItemFunction == Meitou.Simulation.Items.FeedSystem.FoodFunction)
            {
                if (fed) yield return item;
                fed = true;
            }
            else if (item.ItemFunction is 0 or Meitou.Simulation.Items.FeedSystem.RawFoodFunction) yield return item;
        }
    }

    /// <summary>What a hauler puts into the storage: for a ditch run what <see cref="Ditchable"/> lists and the storage takes, else what <see cref="Takes"/> allows.</summary>
    static IEnumerable<Items.ItemInstance> Delivered(World world, Items.Inventory inv, Machine store, bool ditch) =>
        ditch ? Ditchable(inv).Where(i => Stores(store, i)) : inv.All().Where(i => Takes(world, store, i));

    /// <summary>The player's complete machine (not a storage) nearest a storage within <see cref="ProductionSettings.HaulRange"/> whose output holds items the storage takes (ties: the lower site id).</summary>
    string? HaulSource(World world, BuildSite storage, Machine store)
    {
        string? best = null;
        float bestGap = float.MaxValue;
        foreach (var s in world.Machines.All)
        {
            if (s.Data.IsStorage || s.Output is not { Count: > 0 } output || Site(world, s.Site) is not { Complete: true } site || site.Owner != world.Player.Faction) continue;
            float gap = Gap(site.Position, storage.Position);
            if (gap > Settings.HaulRange || gap > bestGap || (gap == bestGap && string.CompareOrdinal(s.Site, best) > 0)) continue;
            if (items.Create(output.Recipe.Item, 1) is not { } one || !store.Data.Accepts(one.Record, one.Type) || !Fits(store.Storage!, one)) continue;
            best = s.Site;
            bestGap = gap;
        }
        return best;
    }

    void StartJob(World world, int slot, int k)
    {
        var table = world.Characters;
        var cold = table.Cold(slot)!;
        var job = cold.Jobs[k];
        if (job.Type == TaskType.JOB_BUILDER)
        {
            End(world, slot);
            Construction!.StartBuilding(world, slot, Construction.EngineerSite(world, slot)!, k);
            return;
        }
        if (PermaJobs.Follows(job.Type))
        {
            if (FollowedBy(world, slot, cold, k) is not { } target) return;
            End(world, slot);
            // The Bodyguard job guards (row 45, RetaliationSystem.Guards); the verb always gives the job. The circle was broken when the job was added.
            MovementSystem.Follow(world, new FollowOrder([table.IdOf(slot)], target) { Tick = world.Tick }, TaskSource.Job, k, job.Type);
            return;
        }
        var site = Site(world, job.Site!)!;
        var m = Ensure(world, site)!;
        var request = new TaskRequest(CharacterTask.Haul, job.Type, TaskSource.Job) { Job = k, Site = site.Id };
        if (job.Type is TaskType.OPERATE_MACHINERY or TaskType.MAN_A_TURRET_PLAYER_JOB or TaskType.USE_TRAINING_DUMMY or TaskType.OPERATE_AUTOMATIC_MACHINERY || !m.Data.IsStorage)
        {
            if (cold.OperateSite != site.Id) TaskStart.Start(world, slot, request with { Task = CharacterTask.Operate });
            else if (cold.Running is { } running && running.Runs(CharacterTask.Operate)) cold.Running = running with { Source = TaskSource.Job, Row = job.Type, Job = k, Implicit = false, Resume = CharacterTask.Idle };
        }
        else TaskStart.Start(world, slot, request);
    }

    /// <summary>A <see cref="CharacterTask.Haul"/> trip to a storage: what the character already carries for it goes there first; else it fetches a machine's output.</summary>
    void StartHaulTrip(World world, int slot, BuildSite storage, Machine store, bool ditch = false)
    {
        var table = world.Characters;
        var cold = table.Cold(slot)!;
        cold.OperateSite = null;
        cold.HaulFetch = null;
        cold.Operating = false;
        ref var n = ref table.Next[slot];
        TaskStart.Stand(table, slot);
        n.Task = (byte)CharacterTask.Haul;
        n.Mode = SpeedMode.Free;
        n.TaskTime = 0;
        bool carries = cold.Inventory is { } inv && Delivered(world, inv, store, ditch).Any();
        cold.HaulSite = carries ? storage.Id : HaulSource(world, storage, store);
        cold.HaulTo = storage.Id;
        sweep = true;
    }

    /// <summary>The haul trip is the implicit ditching's (242).</summary>
    static bool Ditching(CharacterCold cold) => cold.Running is { Implicit: true, Row: TaskType.DITCH_ALL_RESOURCES } run && run.Runs(CharacterTask.Haul);

    /// <summary>A hauler at the end of a leg (serial): at the source it takes the output, then makes for the storage; at the storage it puts in what the storage takes and the job's trip ends.</summary>
    void HaulStep(World world, int slot)
    {
        var table = world.Characters;
        var cold = table.Cold(slot)!;
        ref var n = ref table.Next[slot];
        if (cold.HaulTo is not { } storeId || cold.HaulSite is not { } at
            || Site(world, storeId) is not { Complete: true } || world.Machines.Find(storeId) is not { Storage: not null } store
            || Site(world, at) is not { Complete: true } atSite || world.Machines.Find(at) is not { } there || !Able(cold) || cold.Inventory is not { } inv)
        {
            End(world, slot);
            return;
        }
        if (!AtSite(n, atSite, there)) return;
        if (at != storeId)
        {
            if (there.Output is { Count: > 0 } output && items.Create(output.Recipe.Item, output.Count) is { } made && store.Data.Accepts(made.Record, made.Type))
            {
                int moved = Placed(inv, made);
                output.Count -= moved;
                world.Machines.Touch();
            }
            if (Delivered(world, inv, store, Ditching(cold)).Any()) cold.HaulSite = storeId;
            else End(world, slot);
            return;
        }
        foreach (var item in Delivered(world, inv, store, Ditching(cold)).ToList())
        {
            inv.Remove(item);
            if (store.Storage!.Add(item)) continue;
            if (!inv.Add(item)) world.GroundItems.Drop(item, store.Position, 0);
            break;
        }
        world.Machines.Touch();
        End(world, slot);
    }
}
