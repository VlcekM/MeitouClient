using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Ai;
using Meitou.Data.World;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Construction;
using Meitou.Simulation.Items;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Perception;
using Meitou.Simulation.Player;
using StorageUse = Meitou.Simulation.Items.StorageUse;

namespace Meitou.Simulation.Production;

/// <summary>
/// Job lists (Perma-Jobs; docs/simulation.md "Production", the original's rules in docs/game/ai-tasks.md "Perma-jobs"): the decision step takes the
/// first job of a player character's list that can run (ListJobs here is the production pick's contributor), so a higher job takes over as soon as it
/// can run and a blocked one passes to the next. Meitou runs Operating machine (87, the operator below), Operating automatic machine (152: an
/// operator that only hauls for it), Hauling to (124: a machine's output carried to the job's storage, CharacterTask.Haul; on an automatic machine,
/// carried inputs into it), Engineer (125: ConstructionSystem builds the site its finder picks) and Turret guard (234: manning the job's turret as an
/// operator). Blocked uses one test for starting and for going on.
/// </summary>
public sealed partial class ProductionSystem
{
    /// <summary>
    /// A job's look-again period (ticks) that callers wait out; the chooser itself runs on the think's turn (<see cref="DecisionSystem.Look"/>,
    /// <c>AiSettings.ThinkEveryTicks</c>) or when asked.
    /// </summary>
    public const int JobCheckTicks = 10;

    /// <summary>
    /// Gives each player character named a job of the order's type on its site (when the site exists); a type a code mod offers on buildings goes
    /// by its offer (<see cref="GiveOffered"/>).
    /// </summary>
    void AddJobs(World world, AddJob add)
    {
        if (world.SystemOf<PlayerSystem>()?.Extensions?.JobOffers.Find(o => o.Type == add.Type) is { } offer)
        {
            GiveOffered(world, add, offer);
            return;
        }

        if ((Site(world, add.Site) ?? TownMachineSite(world, add.Site)) is null) return;
        var table = world.Characters;
        foreach (var id in Who(world, add.Characters))
        {
            if (table.TryResolveNext(id, out int slot) && table.Cold(slot) is { } cold)
                PermaJobs.Give(cold, add.Type, new PermaJob(add.Type, add.Site));
        }
    }

    /// <summary>
    /// A code mod's offered job (docs/modding.md "Mod jobs"): when the site is a finished building of the player's faction and of one of the offer's
    /// records, each living player character named that the offer is for takes the job there (<see cref="PermaJobs.GiveOffered"/>).
    /// </summary>
    void GiveOffered(World world, AddJob add, ModJobOffer offer)
    {
        if (world.Buildings.Find(add.Site) is not { Complete: true } site) return;
        if (site.Owner != world.Player.Faction || !offer.On(site.Record)) return;

        var table = world.Characters;
        foreach (var id in Who(world, add.Characters))
        {
            if (!table.TryResolveNext(id, out int slot) || table.Cold(slot) is not { } cold) continue;
            if (cold is { IsPlayer: true, DiedTick: < 0 } && offer.Takes(CharacterFacts.Animal(cold)))
                PermaJobs.GiveOffered(cold, offer.Type, site.Id);
        }
    }

    /// <summary>
    /// Gives a character the job an order makes (a first aid on a robot makes the Robotics job).
    /// </summary>
    void AddOrderJobs(World world, AddOrderJob add)
    {
        var table = world.Characters;
        var ordered = add.Ordered;

        // A first aid on a robot is the original's robot repair order (60), whose job is Robotics.
        if (ordered == TaskType.FirstAidOrder && table.TryResolveNext(add.Target, out int t) && table.Cold(t)?.Race is { IsRobot: true })
            ordered = TaskType.FirstAidRobot;

        // Follow (31) and Bodyguard (45) on a character become the jobs that follow it (docs/game/ai-tasks.md "Finders of the jobs": they act on the
        // job's own target).
        bool onCharacter = ordered is TaskType.Bodyguard or TaskType.StayCloseToTarget;
        foreach (var id in Who(world, add.Characters))
        {
            if (!table.TryResolveNext(id, out int slot) || table.Cold(slot) is not { } cold) continue;
            if (!onCharacter) PermaJobs.Give(cold, ordered, PermaJob.Untargeted(ordered));
            else if (id != add.Target && table.IsAlive(add.Target))
            {
                PermaJobs.Give(cold, ordered, PermaJob.OfCharacter(ordered, add.Target));

                // The order giver then drops what would close a circle back to the ordered character (FUN_1405c8c10), whether or not the job was new.
                MovementSystem.BreakCircle(world, id);
            }
        }
    }

    /// <summary>
    /// Removes a job from a character's list.
    /// </summary>
    void RemoveJobAt(World world, RemoveJob remove)
    {
        var table = world.Characters;
        if (table.TryResolveNext(remove.Character, out int slot)) RemoveJob(world, slot, remove.Index);
    }

    /// <summary>
    /// Takes the job at <paramref name="index"/> out of a character's list (the list's X, and the circle break of <see cref="MovementSystem"/>): the
    /// task of the job it is on ends, the running index follows (serial phases only).
    /// </summary>
    internal static void RemoveJob(World world, int slot, int index)
    {
        var table = world.Characters;
        if (table.Cold(slot) is not { } cold || (uint)index >= (uint)cold.Jobs.Count) return;
        if (PermaJobs.ListJob(cold, (CharacterTask)table.Next[slot].Task) is { Implicit: false } on && on.Job == index) End(world, slot);
        cold.Jobs.RemoveAt(index);
        DecisionSystem.Request(cold);

        // Any job's provenance keeps pointing at its job; a treatment, lift or loot whose job left the list runs on as a job pass's (no index).
        if (cold.Running is { Source: TaskSource.Job, Implicit: false, Job: >= 0 } job && job.Job >= index)
            cold.Running = job with { Job = job.Job == index ? -1 : job.Job - 1 };
    }

    /// <summary>
    /// Moves a job within a character's list.
    /// </summary>
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
    /// The production pick's list jobs as the decision step's contributor (<see cref="IJobSource"/>, step 5; 234 also step 3): Operating machine 87,
    /// Operating automatic machine 152, Hauling to 124, Training 97, Turret guard 234, Engineer 125 and the follow jobs 31, 45, 289; and the carrying
    /// jobs 138, 137, 223, 273 (<see cref="ErrandJobs"/>). A job runs while <see cref="Runnable"/> holds; one that no longer does and that nothing
    /// replaces ends (<see cref="End"/>).
    /// </summary>
    void IDecisionOwner.RegisterSources(DecisionSystem decisions)
    {
        var jobs = new ListJobs(this);
        foreach (var type in (ReadOnlySpan<TaskType>)[TaskType.OperateMachinery, TaskType.OperateAutomaticMachinery, TaskType.OperateStorage,
            TaskType.UseTrainingDummy, TaskType.ManATurretPlayerJob, TaskType.JobBuilder, TaskType.Bodyguard, TaskType.StayCloseToTarget,
            TaskType.Unnamed289])
            decisions.RegisterJob(type, jobs);

        var errands = new ErrandJobs(this);
        foreach (var type in (ReadOnlySpan<TaskType>)[TaskType.FindADeadGuy, TaskType.DeadGuysGoInThePot, TaskType.PickupIntruders,
            TaskType.PickupIntrudersTown])
            decisions.RegisterJob(type, errands);

        decisions.Register(new BedCommitment(this));
        decisions.Register(new ImplicitJobs(this));
    }

    /// <summary>
    /// The production's job contributor (<see cref="IJobSource"/>) of the list's job types.
    /// </summary>
    sealed class ListJobs(ProductionSystem owner) : IJobSource
    {
        /// <summary>
        /// Whether list job <paramref name="k"/> can run (or runs already) for the character.
        /// </summary>
        public DecisionCheck CanRun(World world, int slot, int k)
        {
            var table = world.Characters;
            var cold = table.Cold(slot)!;
            var task = (CharacterTask)table.Next[slot].Task;
            var type = cold.Jobs[k].Type;
            bool runs = PermaJobs.ListJob(cold, task) is { Implicit: false } on && on.Job == k;
            if (PermaJobs.Follows(type) && cold.Running is { Source: TaskSource.Job, Implicit: false } follow && follow.Job == k
                && PermaJobs.Follows(follow.Row))
            {
                // Its fight is a step of the row (31 and 45 need state 16, which 13 gives): it runs; once over, the follow is taken up again.
                if (task == CharacterTask.Attack && follow.Runs(task)) return DecisionCheck.Runs;
                if (MovementSystem.FightStepOver(table, slot, cold))
                    return FollowedBy(world, slot, cold, k) is not null ? DecisionCheck.Run : DecisionCheck.No("its character is gone");
            }

            // One carried or down looks at no job (and keeps the one it is on); a carrier looks at most (AbleFor).
            bool onErrand = owner._errands.ContainsKey(table.IdOf(slot));
            if (!AbleFor(cold, type, task, onErrand)) return runs ? DecisionCheck.Runs : DecisionCheck.No("carried, down, or carrying for it");

            // 234's effect (using the turret) is planned through 146, whose list B has !158: not while a melee attacker is within 40
            // (docs/game/ai-tasks.md "Precedence").
            if (type == TaskType.ManATurretPlayerJob && world.SystemOf<CombatSystem>() is { } combat
                && CharacterFacts.ImmediateThreat(combat, world, slot))
                return DecisionCheck.No("a melee threat within 40 (146 needs !158)");

            if (!owner.Runnable(world, slot, cold, k)) return DecisionCheck.No("blocked");
            if (PermaJobs.Follows(type) && Defends(world, slot, cold, k) is { } foe)
                return foe.IsNone ? DecisionCheck.No("its plan's 13 has no attacker it can fight") : DecisionCheck.Run;

            return runs ? DecisionCheck.Runs : DecisionCheck.Run;
        }

        /// <summary>
        /// A follow job whose row's plan starts with 13 (<see cref="FollowDefence"/>: its character threatened within 40, for 45 also itself): the
        /// attacker it fights, <see cref="CharacterId.None"/> when the plan fails; null when the row plans no fight.
        /// </summary>
        static CharacterId? Defends(World world, int slot, CharacterCold cold, int k)
        {
            if (FollowedBy(world, slot, cold, k) is not { } charge) return null;

            var type = cold.Jobs[k].Type;
            if (!FollowDefence.Wanted(world, slot, type, charge)) return null;

            return FollowDefence.Foe(world, slot, type, charge);
        }

        /// <summary>
        /// Starts list job <paramref name="k"/> for the character: a follow job whose fight is over follows again, one whose plan starts with 13
        /// follows (unless it does already) and fights the attacker from the next tick (<see cref="FollowDefence.Start"/>).
        /// </summary>
        public void Start(World world, int slot, int k)
        {
            var table = world.Characters;
            var cold = table.Cold(slot)!;
            if (MovementSystem.FightStepOver(table, slot, cold) && cold.Running is { Source: TaskSource.Job } run && run.Job == k
                && MovementSystem.FollowAgain(world, slot, cold))
                return;

            var type = cold.Jobs[k].Type;
            if (!PermaJobs.Follows(type) || Defends(world, slot, cold, k) is not { IsNone: false } foe)
            {
                owner.StartJob(world, slot, k);
                return;
            }

            bool follows = cold.Running is { Source: TaskSource.Job, Task: CharacterTask.Follow } on && on.Job == k;
            if (!follows) owner.StartJob(world, slot, k);
            FollowDefence.Start(world, slot, foe, type, TaskSource.Job, k);
        }

        /// <summary>
        /// A Turret guard (234, kind 2) is urgent while enemies are sensed (AI <c>+0xbc</c>, the senses' <c>numEnemies</c>,
        /// <see cref="SenseBlock.Enemies"/>; <c>FUN_14050e5b0</c>).
        /// </summary>
        public bool Urgent(World world, int slot, int k) =>
            world.Characters.Cold(slot)!.Jobs[k].Type == TaskType.ManATurretPlayerJob && CharacterFacts.Senses(world, slot) is { Enemies: > 0 };

        /// <summary>
        /// Ends the character's list job.
        /// </summary>
        public void Stop(World world, int slot, int k) => End(world, slot);
    }

    /// <summary>
    /// The chooser's committed stop before step 5 (<c>FUN_140510ef0</c>, <b>Verified</b>; docs/game/ai-tasks.md "Perma-jobs"): the implicit 153
    /// "Sleep when injured" job is a priority-2 goal with the commit flag, so while its commitment runs the job list and the implicit jobs are not
    /// asked, and a list job that turns runnable does not get the sleeper up (#1034). Orders, urgent jobs and the priority-3 goals are asked before it.
    /// </summary>
    sealed class BedCommitment(ProductionSystem owner) : IDecisionSource
    {
        /// <summary>
        /// The trace name.
        /// </summary>
        public string Name => "153 Going to bed (committed)";
        /// <summary>
        /// Step 5, the jobs.
        /// </summary>
        public DecisionStep Step => DecisionStep.Jobs;
        /// <summary>
        /// Before the list (its key 0).
        /// </summary>
        public int Key => -1;

        /// <summary>
        /// Runs while the character's bed trip or sleep is committed (<see cref="BedCommitted"/>).
        /// </summary>
        public DecisionCheck CanRun(World world, int slot) =>
            owner.BedCommitted(world, slot) ? DecisionCheck.Runs : DecisionCheck.No("not committed to a bed");

        /// <summary>
        /// Nothing to start: it only keeps the running job.
        /// </summary>
        public void Start(World world, int slot) { }
    }

    /// <summary>
    /// The implicit jobs (squad AI options) as step 5's source after the list (the original's lowest keys, <c>FUN_14050e5b0</c>): a character idle,
    /// on a standing task or on a job's task (a list job's that could not run on, or an implicit one) takes the one <see cref="ImplicitPick"/> gives;
    /// it runs while that is the one it is on (the same type and site). One on an implicit job that no longer holds ends it (<see cref="Passed"/>:
    /// the original rebuilds the entry on every pass, so nothing carries over).
    /// </summary>
    sealed class ImplicitJobs(ProductionSystem owner) : IDecisionSource
    {
        /// <summary>
        /// What the last <see cref="CanRun"/> picked: the tick, the slot, the job type and its site (tick -1: nothing).
        /// </summary>
        (long Tick, int Slot, TaskType Type, string Site) _picked = (-1, -1, TaskType.None, "");

        /// <summary>
        /// The trace name.
        /// </summary>
        public string Name => "Implicit jobs";
        /// <summary>
        /// Step 5, the jobs.
        /// </summary>
        public DecisionStep Step => DecisionStep.Jobs;
        /// <summary>
        /// After the list (its key 0).
        /// </summary>
        public int Key => 1;

        /// <summary>
        /// Whether an implicit job can run for the character: one out of a fight, idle, on a standing task or on a job's task, that
        /// <see cref="ImplicitPick"/> gives a job.
        /// </summary>
        public DecisionCheck CanRun(World world, int slot)
        {
            var table = world.Characters;
            var cold = table.Cold(slot)!;
            var task = (CharacterTask)table.Next[slot].Task;
            var job = PermaJobs.ListJob(cold, task);
            if (task == CharacterTask.Attack || cold.InCombat) return DecisionCheck.No("fighting");
            if (job is null && task is not (CharacterTask.Idle or CharacterTask.Wander or CharacterTask.Follow)) return DecisionCheck.No("busy");
            if (!Able(cold)) return job is { Implicit: true } ? DecisionCheck.Runs : DecisionCheck.No("carried or down");
            if (owner.ImplicitPick(world, slot) is not { } chosen) return DecisionCheck.No("none holds");
            string? current = chosen.Type == TaskType.DitchAllResources ? cold.HaulTo : cold.OperateSite;
            if (job is { Implicit: true } && chosen.Type == job.Row && chosen.Site == current) return DecisionCheck.Runs;
            _picked = (world.Tick, slot, chosen.Type, chosen.Site);
            return DecisionCheck.Run;
        }

        /// <summary>
        /// Starts the implicit job <see cref="CanRun"/> picked this tick.
        /// </summary>
        public void Start(World world, int slot)
        {
            if (_picked.Tick == world.Tick && _picked.Slot == slot) owner.StartImplicit(world, slot, _picked.Type, _picked.Site);
        }

        /// <summary>
        /// The step passed over it: a character on an implicit job that no longer holds ends it, and a healed one asleep in a bed for no job
        /// (one loaded so) gets up (<see cref="GetsUpHealed"/>).
        /// </summary>
        public void Passed(World world, int slot, in DecisionCheck check)
        {
            var table = world.Characters;
            var cold = table.Cold(slot)!;
            if (!Able(cold)) return;

            var job = PermaJobs.ListJob(cold, (CharacterTask)table.Next[slot].Task);
            if (job is { Implicit: true } || job is null && owner.GetsUpHealed(world, slot, cold)) End(world, slot);
        }
    }

    /// <summary>
    /// How far (X/Z) from where it idles a character sits down on a chair (engine choice: the original's sitter looks in its home town only).
    /// </summary>
    public const float SeatRange = 600;

    /// <summary>
    /// The implicit job of a character whose list has none to run (docs/game/ai-tasks.md "Perma-jobs": the original's lowest keys, in this order),
    /// with the jobs switch on and not sneaking (the tail has no slave test); Sleep and Sit not while it holds position, carries someone or is selected. <see cref="SquadAi.Ditch"/>: carried items a
    /// storage takes go there (242; only with no queued orders). <see cref="SquadAi.Sleep"/>: an injured character lies in the bed the bed search
    /// finds (153, whose target finder it is, <b>Verified</b>; <see cref="Locks.FixtureRegistry.FindBed"/>) until healed (then 257 gets it up).
    /// <see cref="SquadAi.Sit"/>: a free chair (275). The one it is on is kept while it holds (a bed within
    /// <see cref="Locks.FixtureRegistry.BedSearchRange"/>). Distances are from the character.
    /// </summary>
    (TaskType Type, string Site)? ImplicitPick(World world, int slot)
    {
        var table = world.Characters;
        var cold = table.Cold(slot)!;
        var options = world.Player.SquadAi;

        // None while sneaking (FUN_1405c9750; a fighting character is not Able anyway).
        if ((cold.Orders & CharacterOrders.Jobs) == 0 || (cold.Orders & CharacterOrders.Sneak) != 0 || options == SquadAi.None) return null;

        var at = table.Next[slot].Position;
        var on = PermaJobs.ListJob(cold, (CharacterTask)table.Next[slot].Task) is { Implicit: true } running ? running.Row : TaskType.None;
        if ((options & SquadAi.Ditch) != 0)
        {
            // The 242 entry is rebuilt every pass (FUN_14050e5b0): the trip goes on only while no order is queued and its storage still takes
            // something carried.
            if (on == TaskType.DitchAllResources && cold.HaulTo is { } keep && !PlayerOrders.Holds(world, slot) && cold.Inventory is { } held
                && Site(world, keep) is { Complete: true } keptSite && keptSite.Owner == world.Player.Faction
                && Ensure(world, keptSite) is { } keptStore && keptStore.Data.IsStorage && keptStore.Storage is not null
                && Ditchable(held).Any(item => Stores(keptStore, item)) && world.StorageAllows(cold, keep, StorageUse.Put, DitchRecord(world, held)))
                return (TaskType.DitchAllResources, keep);

            bool ditching = !PlayerOrders.Holds(world, slot) && cold.Inventory is { } carried && Ditchable(carried).Any();
            if (ditching && NearestSite(world, at, float.MaxValue, IsStore, Ditches) is { } store)
                return (TaskType.DitchAllResources, store);

            bool Ditches(BuildSite s, Machine m) => InFinderRange(s.Position, at) && TakesDitched(m, cold.Inventory!)
                && world.StorageAllows(cold, s.Id, StorageUse.Put, DitchRecord(world, cold.Inventory!));
        }

        // Sleep and Sit not while holding position (FUN_1402b2b90), carrying someone (+0x348) or selected by the player (FUN_1407f4d20, the
        // interface's selection set; FUN_14050e5b0, Verified).
        if (Orders.Holding(cold) || !cold.Carrying.IsNone || world.Player.Selection.Contains(table.IdOf(slot))) return null;
        if ((options & SquadAi.Sleep) != 0 && WantsBed(world, _towns, slot, cold) && Beds is not null)
        {
            if (on == TaskType.GoHomeAndGoToBed && cold.OperateSite is { } bed && world.Fixtures.Find(bed) is { } kept
                && Gap(kept.Position, at) <= Locks.FixtureRegistry.BedSearchRange)
                return (TaskType.GoHomeAndGoToBed, bed);

            if (Beds.FindFor(world, slot) is { } free) return (TaskType.GoHomeAndGoToBed, free.Id);
        }

        if ((options & SquadAi.Sit) != 0)
        {
            if (on == TaskType.SitAround && cold.OperateSite is { } seat && Site(world, seat) is { } kept && Gap(kept.Position, at) <= SeatRange)
                return (TaskType.SitAround, seat);

            if (NearestSite(world, at, SeatRange, IsSeat, (s, m) => Free(world, cold, s, m)) is { } free) return (TaskType.SitAround, free);
        }

        return null;
    }

    /// <summary>
    /// The training dummy's gain per second of the continuous entry (<b>Verified</b>, <c>UseableStuff</c> vfunc 0x4b0: <c>FUN_1408c64f0</c> with
    /// 2.0).
    /// </summary>
    public const float TrainingRate = 2;

    /// <summary>
    /// A user at a training dummy (<c>Task_UseTrainingDummy</c> slot 2 and <c>UseableStuff</c> vfunc 0x4b0, <b>Verified</b> by decompile): facing it,
    /// the building's <c>stat used</c> rises every tick through <see cref="XpService.Capped"/> at <see cref="TrainingRate"/>, up to the building's
    /// cap (<c>output rate</c>: MkI 6, MkII 11, MkIII 16, whose descriptions promise levels 5, 10 and 15, <b>Observed</b>; the field read as the cap
    /// is +0x1a4, not traced to the record). The gain also depends on the raw stat, not the injured one.
    /// </summary>
    void Train(ref CharacterHot n, CharacterCold cold, BuildSite site, Machine m, float dt)
    {
        if (n.Velocity.X == 0 && n.Velocity.Z == 0) n.Yaw = MathF.Atan2(site.Position.X - n.Position.X, site.Position.Z - n.Position.Z);
        m.Operators++;
        cold.Operating = true;
        TrainingXp(cold, m.Data, dt);
    }

    /// <summary>
    /// A training dummy's work step (<c>UseableStuff</c> vfunc 0x4b0, <see cref="Train"/>): <paramref name="data"/>'s <c>stat used</c> rises at
    /// <see cref="TrainingRate"/> up to its cap.
    /// </summary>
    void TrainingXp(CharacterCold cold, Meitou.Data.Gameplay.Construction.MachineData data, float dt)
    {
        if (cold.Stats is { } stats && cold.Race is { } race)
            _xp.Capped(stats, race, (Meitou.Data.Gameplay.Bodies.StatsEnumerated)data.Stat, dt, TrainingRate, data.OutputValue);
    }

    /// <summary>
    /// BF_CHAIR and BF_THRONE (17, 24; docs/game/ai-tasks.md "Town goals").
    /// </summary>
    static bool IsSeat(Machine m) => m.Data.Function is TownBuildings.ChairFunction or ThroneFunction;

    /// <summary>
    /// BF_THRONE.
    /// </summary>
    const int ThroneFunction = 24;

    /// <summary>
    /// Whether machine <paramref name="m"/> takes the character as an operator (<c>FUN_1400f7c90</c>, <b>Verified</b>): it operates it already, or
    /// fewer than its <c>max operators</c> do.
    /// </summary>
    internal static bool TakesOperator(World world, CharacterCold cold, Machine m) =>
        cold.OperateSite == m.Site || OperatorsOf(world, m.Site) < m.Data.MaxOperators;

    /// <summary>
    /// Nobody else lies or sits on it.
    /// </summary>
    static bool Free(World world, CharacterCold cold, BuildSite site, Machine m) =>
        OperatorsOf(world, site.Id) - (cold.OperateSite == site.Id ? 1 : 0) < m.Data.MaxOperators;

    /// <summary>
    /// The player branch of row 98 Sleeping's desire (<c>FUN_1405a0880</c>, <b>Verified</b>; 153 plans into 98, and a node whose desire is 0 is
    /// dropped): it stands in a player place (<see cref="InPlayerPlace"/>), its organic first aid need is 0 (everything bandaged) and it is
    /// <see cref="Unrested"/>. <paramref name="towns"/>: the world's town data (null: none built).
    /// </summary>
    internal static bool WantsBed(World world, Population.PopulationData? towns, int slot, CharacterCold cold) =>
        Unrested(cold) && FirstAid.FirstAidSystem.OrganicAidNeed(world, cold) <= 0 && InPlayerPlace(world, towns, slot, cold);

    /// <summary>
    /// The player branch's place test (<c>FUN_1405a0880</c>, <b>Verified</b> decompile): the character's status town (status <c>+0x88</c>) is set,
    /// and that town's faction is the player's, or the building it stands in (its <c>isIndoors</c> handle, vfunc <c>0x1d8</c>) is the player's.
    /// So a character out in the wild, or outdoors in another faction's town, is not sent to bed. A world built without town data has no towns to
    /// test, so there it is not asked.
    /// </summary>
    static bool InPlayerPlace(World world, Population.PopulationData? data, int slot, CharacterCold cold)
    {
        if (data is not { } towns) return true;

        int player = world.Player.Faction;
        var town = Ai.AiSystem.StatusTownAt(towns, world, world.Characters.Next[slot].Position, cold.StatusTown);
        if (town.IsNone) return false;
        if (town.Owner(world, towns) == player) return true;

        return world.Sight.BuildingOf(slot) is { } building && world.Fixtures.OwnerOf(building) == player;
    }

    /// <summary>
    /// A living character whose rested level (<see cref="MedicalState.RestedLevel"/>, medical <c>+0xb0</c>) is at most
    /// <see cref="RowStates.RestedPast"/> (0.9, which <c>FUN_1405a0880</c> and state 72's <c>FUN_140643cd0</c> both read): the sleeping desire's
    /// injury test, and the opposite of state 72 <c>isFullyRested</c>, row 98's done test. A robot's is 1, so it never is. <b>Engine choice</b>: a
    /// part within <see cref="HealedSlack"/> of its maximum counts as full.
    /// </summary>
    static bool Unrested(CharacterCold cold) =>
        cold is { Medical: { Dead: false } medical, Race: { } race } && medical.RestedLevel(race, HealedSlack) <= RowStates.RestedPast;

    /// <summary>
    /// How far below its maximum a part may stay and count as full: the HUD shows it full, and healing from the treated pool can stop a hundredth
    /// short, which nothing heals (#500: a healed sleeper kept going back to bed). The rested level stores a part's raw health, so without it a
    /// part whose maximum is 90 or less would never read as rested with that residue.
    /// </summary>
    const float HealedSlack = 0.5f;

    /// <summary>
    /// The original's 257 "Getting out of bed once healed", above 153 in the implicit list (docs/game/ai-tasks.md "Perma-jobs"): a player character
    /// of the player's faction asleep in a bed with no order, under the <see cref="SquadAi.Sleep"/> option and the jobs switch, not sneaking, not
    /// holding position, and rested (the tail has no slave test). Engine reading: it applies to any such sleeper, not only one the 153 job put there.
    /// </summary>
    bool GetsUpHealed(World world, int slot, CharacterCold cold) =>
        DecisionSystem.Decides(cold) && cold.InSomething == Locks.HeldState.Bed && world.Characters.Next[slot].Task == (byte)CharacterTask.Sleep
        && (world.Player.SquadAi & SquadAi.Sleep) != 0 && (cold.Orders & CharacterOrders.Jobs) != 0 && (cold.Orders & CharacterOrders.Sneak) == 0
        && !Orders.Holding(cold) && !PlayerOrders.Holds(world, slot) && !Unrested(cold);

    /// <summary>
    /// Whether the character is on the implicit 153 job (walking to its bed or lying in it) and committed to it: its commitment
    /// (<see cref="CharacterCold.BedCommitUntil"/>) has not passed, the bed is still there, and it is still <see cref="Unrested"/> (row 98 is done
    /// by state 72, which ends the plan and with it the goal). The switches the implicit pass tests are not asked again, as the original's
    /// committed chooser returns before that pass.
    /// </summary>
    bool BedCommitted(World world, int slot)
    {
        var table = world.Characters;
        var cold = table.Cold(slot)!;
        var job = PermaJobs.ListJob(cold, (CharacterTask)table.Next[slot].Task);
        if (job is not { Implicit: true, Row: TaskType.GoHomeAndGoToBed }) return false;
        if (world.GameHours >= cold.BedCommitUntil || !Able(cold) || !Unrested(cold)) return false;

        return cold.OperateSite is { } bed && world.Fixtures.Find(bed) is not null;
    }

    /// <summary>
    /// Starts the commitment of the implicit 153 job (the goal setter <c>FUN_14050caf0</c>, <b>Verified</b>: a new committing goal's expiry is now
    /// plus the row's 2 + r x 8 game hours, <see cref="TaskProfiles"/>).
    /// </summary>
    static void CommitToBed(World world, int slot, CharacterCold cold)
    {
        var profile = TaskProfiles.Of(TaskType.GoHomeAndGoToBed);
        ulong salt = SeedSalts.AiCommit ^ (ulong)world.Tick << 9 ^ (ulong)TaskType.GoHomeAndGoToBed;
        ulong roll = Rng.Hash(world.Seed, Rng.Key(world.Characters.IdOf(slot)), RngPurpose.Think, salt);
        cold.BedCommitUntil = world.GameHours + profile.CommitMin + Rng.Float(roll) * profile.CommitRange;
    }

    /// <summary>
    /// The player's complete building nearest <paramref name="at"/> within <paramref name="range"/> whose machine passes <paramref name="ok"/> (ties:
    /// the lower site id). <paramref name="kind"/> is a test of the machine alone that <paramref name="ok"/> implies, asked first so the other
    /// machines' sites are not looked up.
    /// </summary>
    string? NearestSite(World world, Vector3 at, float range, Func<Machine, bool> kind, Func<BuildSite, Machine, bool> ok)
    {
        string? best = null;
        float bestGap = float.MaxValue;
        var all = world.Machines.All;
        for (int k = 0; k < all.Count; k++)
        {
            var m = all[k];
            if (!kind(m) || Site(world, m.Site) is not { Complete: true } site || site.Owner != world.Player.Faction) continue;
            float gap = Gap(site.Position, at);
            if (gap > range || gap > bestGap || (gap == bestGap && string.CompareOrdinal(m.Site, best) > 0) || !ok(site, m)) continue;
            best = m.Site;
            bestGap = gap;
        }

        return best;
    }

    /// <summary>
    /// Starts an implicit job: a bed's sleep, a ditch's haul, else operating the site.
    /// </summary>
    void StartImplicit(World world, int slot, TaskType type, string siteId)
    {
        var table = world.Characters;
        var cold = table.Cold(slot)!;
        var task = type switch { TaskType.GoHomeAndGoToBed => CharacterTask.Sleep, TaskType.DitchAllResources => CharacterTask.Haul,
            _ => CharacterTask.Operate };

        // A bed fixture: the bed system walks it there and lays it in (task Sleep); the job keeps the bed's id.
        if (task == CharacterTask.Sleep && (world.Fixtures.Find(siteId) is null || Beds is null)) return;
        if (TaskStart.Start(world, slot, new TaskRequest(task, type, TaskSource.Job) { Site = siteId, Implicit = true }) != StartResult.Started) return;
        if (task != CharacterTask.Sleep || table.Next[slot].Task != (byte)CharacterTask.Sleep) return;

        cold.OperateSite = siteId;
        CommitToBed(world, slot, cold);
    }

    /// <summary>
    /// A sitter or sleeper at its chair or bed: on the seat's origin facing its way, as the original's <c>Task_OperateMachine</c>
    /// (docs/game/ai-tasks.md "Town goals"); a seat playing a bed's clip lies it at its nearest use node with <see cref="World.SeatBedFix"/>.
    /// </summary>
    void Settle(World world, ref CharacterHot n, CharacterCold cold, BuildSite site, Machine m)
    {
        (n.Position, n.Yaw) = world.SeatBedFix && _seatBeds.LieSpot(site, m.Data.Animation, n.Position) is { } lie ? lie
                : (site.Position, SeatFacing(world, site, m));
        n.Velocity = default;
        m.Operators++;
        cold.Operating = true;
    }

    /// <summary>
    /// The square of how far (3D) from the character a job's target may be (<b>Verified</b>, <c>AI::isTargetInRangeForOrders</c> <c>FUN_140597790</c>
    /// in the default ROAM_FAR mode: the walk a job plans needs StateType 27 <c>isMovementAllowed</c>; about 6517 units). Farther, the job is
    /// skipped, so a character away from its base does not walk back for a job there.
    /// </summary>
    public const float JobRangeSq = 4.246733e7f;

    /// <summary>
    /// A job of the list that may run now: Meitou runs its type, the jobs switch is on (or the type ignores it), the character is not getting up, its
    /// target is within <see cref="JobRangeSq"/>, and it is not blocked.
    /// </summary>
    bool Runnable(World world, int slot, CharacterCold cold, int k)
    {
        var job = cold.Jobs[k];
        if (!TaskStart.Eligible(world, cold, job.Type)) return false;
        if (PermaJobs.Follows(job.Type))
            return ((cold.Orders & CharacterOrders.Jobs) != 0 || job.Type != TaskType.Unnamed289) && FollowedBy(world, slot, cold, k) is not null;

        if (!PermaJobs.Runs(job.Type) || ((cold.Orders & CharacterOrders.Jobs) == 0 && !PermaJobs.IgnoresSwitch(job.Type))) return false;

        // The walk to a job needs state 27, false while the character gets up (FUN_1405a3370).
        if (CharacterFacts.GettingUp(cold, world.Tick)) return false;
        var at = world.Characters.Next[slot].Position;

        // The engineer job ignores its target: its finder picks any site (the original's row 125 finder, docs/game/buildings-production.md "Builders
        // and dismantle"); a pick out of range fails the job, and so does a character with no working arm (row 2's state 38).
        if (job.Type == TaskType.JobBuilder)
        {
            return ConstructionSystem.HasWorkingArm(cold) && Construction?.EngineerSite(world, slot) is { } pick
                && Vector3.DistanceSquared(at, pick.Position) < JobRangeSq;
        }

        if (job.Site is { } id && Site(world, id) is { } site && Vector3.DistanceSquared(at, site.Position) >= JobRangeSq) return false;
        return !Blocked(world, cold, job, at);
    }

    /// <summary>
    /// The character a Bodyguard (45), Staying close (31) or animal Staying close (289) job follows (the row has no finder: it acts on the job's own
    /// target, docs/game/ai-tasks.md "Finders of the jobs"): the job's character, found again from its saved handle after a load, alive and not the
    /// follower itself; for 31 and 289 also standing (the rows' state 7, <c>!isTargetDown</c>). Null: the job is blocked.
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
        return job.Type != TaskType.Bodyguard && CharacterFacts.TargetDown(table.Cold(t)) ? null : target;
    }

    /// <summary>
    /// Whether a job cannot run (the original skips such a job: an unmet start condition the planner cannot meet, or the target finder found nothing;
    /// docs/game/ai-tasks.md "Perma-jobs"). Operating: the building is gone or not an operable machine, its operator places are taken by others, or
    /// it has no work (<see cref="HasWork"/>). Hauling to: the storage is gone, and the character at <paramref name="at"/> carries nothing it takes,
    /// fetches no loose item for it (<see cref="FetchesLoose"/>), no loose item it wants lies near (<see cref="LooseFor"/>) and no machine within
    /// <see cref="InFinderRange"/> has an output it takes. Automatic
    /// machines (152, and 124 on one): <see cref="AutoWork"/>.
    /// </summary>
    bool Blocked(World world, CharacterCold cold, PermaJob job, Vector3 at)
    {
        if (job.Site is not { } id || Site(world, id) is not { Complete: true } site || Ensure(world, site) is not { } m) return true;
        switch (job.Type)
        {
            case TaskType.OperateMachinery:
                if (IsSeat(m)) return SeatTaken(world, site, id, -1, cold.OperateSite == id ? 1 : 0, m.Data.MaxOperators);
                if (!m.Data.Operable) return true;
                if (OperatorsOf(world, id) - (cold.OperateSite == id ? 1 : 0) >= m.Data.MaxOperators || !HasWorkingArm(cold)) return true;
                return !HasWork(world, site, m, cold);
            case TaskType.ManATurretPlayerJob:
                // Turret guard (234, the job a Shift order to man a turret becomes): the building is a turret no one else mans.
                if (m.Data.Function != ObjectRanges.TurretFunction || !HasWorkingArm(cold)) return true;
                return OperatorsOf(world, id) - (cold.OperateSite == id ? 1 : 0) >= m.Data.MaxOperators;
            case TaskType.UseTrainingDummy:
                // Training (97): a dummy with a place free for a character with an arm that works (the row's state 38; the rest of its conditions,
                // 73, is Unknown). It goes on at the cap, as the original's has no end.
                if (!m.Data.IsTraining || OperatorsOf(world, id) - (cold.OperateSite == id ? 1 : 0) >= m.Data.MaxOperators) return true;
                return !HasWorkingArm(cold);
            case TaskType.OperateAutomaticMachinery:
                // Done as soon as no input of the machine is empty (the row's done list !62, machineHasEmptyInputs false): the 124 companion goes on
                // from there.
                return !m.Data.IsAutomatic || !m.Data.IsProducer || !m.HasEmptyInputs || !AutoWork(world, site, m, cold, fetch: true);
            case TaskType.OperateStorage:
                // On an automatic machine (152's companion, the original's Task_FillMachine into it): what the character carries for its inputs.
                if (m.Data.IsAutomatic && m.Data.IsProducer) return !AutoWork(world, site, m, cold, fetch: false);
                if (!m.Data.IsStorage || m.Storage is null) return true;
                if (cold.Inventory is { } inv && inv.All().Any(i => Takes(world, m, i))) return false;
                if (FetchesLoose(world, cold, id)) return false;
                return LooseFor(world, cold, at, m) is null && HaulSource(world, site, m) is null;
            default:
                return true;
        }
    }

    /// <summary>
    /// The character is on its way to the loose item it took for this storage (<see cref="CharacterCold.HaulItem"/>) and the item still lies there:
    /// its trip goes on whatever else lies within <see cref="LooseSource.Radius"/> of where it now stands (engine: a detour taking it out of that
    /// reach of every wanted item blocked the job and ended the trip halfway).
    /// </summary>
    static bool FetchesLoose(World world, CharacterCold cold, string storage) =>
        cold.HaulTo == storage && cold.HaulItem is { } item && world.GroundItems.Find(item) is not null;

    /// <summary>
    /// State 38 <c>hasOneWorkingArm</c>, which rows 87, 97, 146 and 234 need and no row provides, so the job cannot run without it.
    /// </summary>
    static bool HasWorkingArm(CharacterCold cold) => cold.Medical is { } m && (m.ArmWorks(left: true) || m.ArmWorks(left: false));

    /// <summary>
    /// A machine an operator can work (the original's start conditions of task 87: output not full, inputs not empty, power; docs/game/ai-tasks.md):
    /// it has power, and a bench a queued research or craft; a farm a dead crop to clear, a ripe one to harvest or a growing one with an empty input
    /// to fill; inputs that hold something or that the operator can fill (it carries the item or, with <see cref="ProductionSettings.AutoHaul"/>,
    /// <see cref="NearestSource"/> finds it); and room in the output or, with AutoHaul, a storage that takes it.
    /// </summary>
    bool HasWork(World world, BuildSite site, Machine m, CharacterCold cold)
    {
        // Power is state 93 (the town's supply), not the granted share: a machine nobody works draws nothing and reads full power, so the share
        // started the job and ended it as the operator arrived, over and over (#546).
        if (!Powered(world, site, m) || m.Health * m.ProductionMult <= 0) return false;
        if (m.Data.IsResearch) return world.Research.Queue.Count > 0;
        if (m.Farm is { } f)
        {
            if (f.Death >= m.Data.Farm!.DeathThreshold) return true;
            if (f.Growth >= 1) return f.Harvested < f.Plants.Length && Room(world, site, m, cold);

            // A growing crop is no work while it holds water: the original's operate row needs a free operator place, false while the farm's
            // status is 3 (growing), so a farmer goes on to his next job (#913). Its status is 0 once an input runs empty (state 62), and he
            // fills it.
            return m.HasEmptyInputs && Supplied(world, site, m, cold);
        }

        if (m.Craft is not { } craft) return Supplied(world, site, m, cold) && Room(world, site, m, cold);
        if (craft.Queue.Count == 0 || !Supplied(world, site, m, cold)) return false;

        // A bench held by its first craft's store works again once its finished items can go to a storage (#762).
        return !CraftFull(craft) || Settings.AutoHaul && craft.Done.Any(i => StorageFor(world, site, i, cold) is not null);
    }

    /// <summary>
    /// Every input holds something or the operator can fill it: it carries the item or, with AutoHaul, <see cref="NearestSource"/> finds it.
    /// </summary>
    bool Supplied(World world, BuildSite site, Machine m, CharacterCold cold)
    {
        foreach (var input in m.Inputs)
        {
            if (input.Rate <= 0 || input.Level + input.Count > 0) continue;
            if (cold.Inventory is { } inv && Carried(inv, input.Item).Any()) continue;
            if (Settings.AutoHaul && NearestSource(world, site, input.Item, cold, operating: cold) is not null) continue;
            return false;
        }

        return true;
    }

    /// <summary>
    /// Haul work at an automatic machine (engine reading of 152, whose goal is "no empty inputs", and of 124 on it): the character is on a trip for
    /// it, or carries an item an input has room for; with <paramref name="fetch"/> also an empty input a storage holds the item of, a full output a
    /// storage takes, or the machine's output carried that a storage takes.
    /// </summary>
    bool AutoWork(World world, BuildSite site, Machine m, CharacterCold cold, bool fetch)
    {
        if (cold.Inventory is not { } inv) return false;
        if (fetch && cold.HaulSite is not null && cold.OperateSite == site.Id) return true;
        foreach (var input in m.Inputs)
        {
            if (input.Rate <= 0) continue;
            if (input.Count < input.Recipe.Capacity && Carried(inv, input.Item).Any()) return true;
            if (fetch && input.Count == 0 && NearestSource(world, site, input.Item, cold, operating: cold) is not null) return true;
        }

        if (!fetch || m.Output is not { } output || _items.Create(output.Recipe.Item, 1) is not { } one) return false;
        if (!output.Full && !Carried(inv, one.Record).Any()) return false;
        return NearestStorage(world, site, cold, one.Record, s => s.Data.Accepts(one.Record, one.Type) && Fits(s.Storage!, one)) is not null;
    }

    /// <summary>
    /// The output has room, or with AutoHaul a storage <paramref name="cold"/> uses takes it.
    /// </summary>
    bool Room(World world, BuildSite site, Machine m, CharacterCold cold) =>
        m.Output is { } o && (!o.Full || Settings.AutoHaul && _items.Create(o.Recipe.Item, 1) is { } one
            && NearestStorage(world, site, cold, one.Record, s => s.Data.Accepts(one.Record, one.Type) && Fits(s.Storage!, one)) is not null);

    /// <summary>
    /// A carried item a "Hauling to" job takes to its storage: a loose stack (not the character's own kit), that the storage takes and has room for,
    /// of a record its <c>limit inventory</c> lists (what <see cref="LooseFor"/> picks up) or that a player machine makes or crafts.
    /// </summary>
    static bool Takes(World world, Machine store, Items.ItemInstance item) =>
        Stores(store, item) && (store.Data.StorageLimit.Contains(item.Record)
            || world.Machines.All.Any(s => !s.Data.IsStorage && !IsTownWork(s.Site) && Makes(s, item.Record)));

    /// <summary>
    /// The loose item a "Hauling to" trip picks up for <paramref name="store"/> (<b>Verified</b>, decompile: job 124 needs state 67 at its storage,
    /// which row 94 Try filling machine achieves; 94's finder <c>FUN_14059d9b0</c> asks the storage for the records it wants, <c>StorageBuilding</c>
    /// slot <c>0x560</c> <c>FUN_140299c30</c>: its item list while it has room, and passes them to <c>FUN_14059d000</c>, whose first pass is the
    /// loose one; docs/game/buildings-production.md "Hauling"): for each record of the storage's <c>limit inventory</c> it has room for, the
    /// nearest loose item of it (<see cref="LooseSource.Nearest"/> from <paramref name="at"/>), while a unit fits the hauler's main grid (row 92
    /// Collecting resource needs state 82, room for it). Engine choices: an item another faction owns is never picked up, so a hauler steals
    /// nothing (the ownership test alone lets one in a player town take such an item, which marks it stolen); a storage with no
    /// <c>limit inventory</c> wants nothing from the ground.
    /// </summary>
    GroundItem? LooseFor(World world, CharacterCold cold, Vector3 at, Machine store)
    {
        if (store.Storage is not { } grid || cold.Inventory is not { } inv) return null;
        foreach (string record in store.Data.StorageLimit)
        {
            if (_items.Create(record, 1) is not { } one || !store.Data.Accepts(record, one.Type) || !Fits(grid, one) || !Fits(inv, one)) continue;
            if (LooseSource.Nearest(world, _towns, cold, at, record, ownOnly: true) is { } loose) return loose;
        }

        return null;
    }

    /// <summary>
    /// A loose stack (not the character's own kit) the storage takes and has room for.
    /// </summary>
    internal static bool Stores(Machine store, Items.ItemInstance item) =>
        item.Section is "main" or "backpack_content" && !item.LockedOn && item.Contents is null && store.Data.Accepts(item.Record, item.Type)
            && Fits(store.Storage!, item);

    /// <summary>
    /// The record the storage filters are asked about for a ditch trip: the one every ditchable item shares, else null; none without a filter.
    /// </summary>
    static string? DitchRecord(World world, Items.Inventory inv) =>
        world.StorageFilters.Active ? Meitou.Simulation.Items.StorageFilters.OneRecord(Ditchable(inv)) : null;

    /// <summary>
    /// A storage machine with its grid (what <see cref="TakesDitched"/> asks first).
    /// </summary>
    static bool IsStore(Machine m) => m.Data.IsStorage && m.Storage is not null;

    /// <summary>
    /// A machine that is not a storage.
    /// </summary>
    static bool IsMaker(Machine m) => !m.Data.IsStorage;

    /// <summary>
    /// A storage machine that takes something 242 Ditch all resources carries off from <paramref name="inv"/>.
    /// </summary>
    static bool TakesDitched(Machine m, Items.Inventory inv) =>
        m.Data.IsStorage && m.Storage is not null && Ditchable(inv).Any(item => Stores(m, item));

    /// <summary>
    /// What 242 Ditch all resources carries off (finder <c>FUN_1405a59a0</c>, its item collection <c>FUN_140749490</c>, <b>Verified</b>): ITEMs with
    /// <c>item function</c> 0 or 15, and food (function 3) but the first, in inventory order.
    /// </summary>
    internal static IEnumerable<Items.ItemInstance> Ditchable(Items.Inventory inv)
    {
        bool fed = false;
        foreach (var item in inv.All())
        {
            if (item.Type != Meitou.Data.Fcs.FcsRecordType.Item) continue;
            if (item.ItemFunction == Meitou.Simulation.Items.FeedSystem.FoodFunction)
            {
                if (fed) yield return item;
                fed = true;
            }
            else if (item.ItemFunction is 0 or Meitou.Simulation.Items.FeedSystem.RawFoodFunction) yield return item;
        }
    }

    /// <summary>
    /// What a hauler puts into the storage: for a ditch run what <see cref="Ditchable"/> lists and the storage takes, else what <see cref="Takes"/>
    /// allows.
    /// </summary>
    static IEnumerable<Items.ItemInstance> Delivered(World world, Items.Inventory inv, Machine store, bool ditch) =>
        ditch ? Ditchable(inv).Where(i => Stores(store, i)) : inv.All().Where(i => Takes(world, store, i));

    /// <summary>
    /// The player's complete machine (not a storage) nearest a storage within <see cref="InFinderRange"/> whose output (a bench's:
    /// its finished items) holds items the storage takes (ties: the lower site id).
    /// </summary>
    string? HaulSource(World world, BuildSite storage, Machine store)
    {
        string? best = null;
        float bestGap = float.MaxValue;
        foreach (var s in world.Machines.All)
        {
            bool holds = s.Output is { Count: > 0 } || s.Craft is { Done.Count: > 0 };
            if (s.Data.IsStorage || !holds || Site(world, s.Site) is not { Complete: true } site || site.Owner != world.Player.Faction)
                continue;

            float gap = Gap(site.Position, storage.Position);
            if (gap > bestGap || (gap == bestGap && string.CompareOrdinal(s.Site, best) > 0)) continue;
            if (!InFinderRange(site.Position, storage.Position) || !Offers(s, store)) continue;

            best = s.Site;
            bestGap = gap;
        }

        return best;
    }

    /// <summary>
    /// Starts list job <paramref name="k"/> by its type: building, operating, hauling or a turret.
    /// </summary>
    void StartJob(World world, int slot, int k)
    {
        var table = world.Characters;
        var cold = table.Cold(slot)!;
        var job = cold.Jobs[k];
        if (job.Type == TaskType.JobBuilder)
        {
            End(world, slot);
            Construction!.StartBuilding(world, slot, Construction.EngineerSite(world, slot)!, k);
            return;
        }

        if (PermaJobs.Follows(job.Type))
        {
            if (FollowedBy(world, slot, cold, k) is not { } target) return;
            End(world, slot);

            // The Bodyguard job's follow runs row 45, whose fights the contributor plans (FollowDefence); the verb always gives the job. The circle was
            // broken when the job was added.
            MovementSystem.Follow(world, new FollowOrder([table.IdOf(slot)], target) { Tick = world.Tick }, TaskSource.Job, k, job.Type);
            return;
        }

        var site = Site(world, job.Site!)!;
        var m = Ensure(world, site)!;
        var request = new TaskRequest(CharacterTask.Haul, job.Type, TaskSource.Job) { Job = k, Site = site.Id };
        if (job.Type is TaskType.OperateMachinery or TaskType.ManATurretPlayerJob or TaskType.UseTrainingDummy or TaskType.OperateAutomaticMachinery
            || !m.Data.IsStorage)
        {
            if (cold.OperateSite != site.Id) TaskStart.Start(world, slot, request with { Task = CharacterTask.Operate });
            else if (cold.Running is { } running && running.Runs(CharacterTask.Operate))
                cold.Running = running with { Source = TaskSource.Job, Row = job.Type, Job = k, Implicit = false, Resume = CharacterTask.Idle };
        }
        else TaskStart.Start(world, slot, request);
    }

    /// <summary>
    /// A <see cref="CharacterTask.Haul"/> trip to a storage: what the character already carries for it goes there first; else a "Hauling to" job
    /// picks up a loose item (<see cref="LooseFor"/>, the finder's first pass); else it fetches a machine's output.
    /// </summary>
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
        cold.HaulTo = storage.Id;
        cold.HaulItem = null;
        if (!carries && !ditch && LooseFor(world, cold, n.Position, store) is { } loose)
        {
            cold.HaulItem = loose.Id;
            cold.HaulSite = storage.Id;
            _sweep = true;
            return;
        }

        cold.HaulSite = carries ? storage.Id : HaulSource(world, storage, store);
        _sweep = true;
    }

    /// <summary>
    /// The haul trip is the implicit ditching's (242).
    /// </summary>
    static bool Ditching(CharacterCold cold) => cold.Running is { Implicit: true, Row: TaskType.DitchAllResources } run && run.Runs(CharacterTask.Haul);

    /// <summary>
    /// A hauler at the end of a leg (serial): at the source it takes the output, then makes for the storage; at the storage it puts in what the
    /// storage takes and the job's trip ends.
    /// </summary>
    void HaulStep(World world, int slot)
    {
        var table = world.Characters;
        var cold = table.Cold(slot)!;
        ref var n = ref table.Next[slot];
        if (Lingers(cold))
        {
            Linger(world, slot, ref n);
            return;
        }

        if (cold.HaulTo is not { } storeId || cold.HaulSite is not { } at
            || Site(world, storeId) is not { Complete: true } || world.Machines.Find(storeId) is not { Storage: not null } store
            || Site(world, at) is not { Complete: true } atSite || world.Machines.Find(at) is not { } there || !Able(cold)
            || cold.Inventory is not { } inv)
        {
            End(world, slot);
            return;
        }

        if (cold.HaulItem is { } loose)
        {
            PickUpLoose(world, slot, cold, inv, store, loose);
            return;
        }

        if (!AtSite(world, n, atSite, there)) return;
        if (at != storeId)
        {
            if (there.Output is { Count: > 0 } output && _items.Create(output.Recipe.Item, output.Count) is { } made
                && store.Data.Accepts(made.Record, made.Type))
            {
                int moved = Placed(inv, made);
                output.Count -= moved;
                world.Machines.Touch();
            }

            if (there.Craft is { } craft && TakeDone(craft, inv, i => store.Data.Accepts(i.Record, i.Type)))
                world.Machines.Touch();

            // The walk stops here, as at a loose item (PickUpLoose), so the leg to the storage starts after the beat.
            StopWalk(ref n, cold);
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

        // The hauler stands the beat at the storage (Linger) before the trip ends.
        world.Machines.Touch();
        StopWalk(ref n, cold);
        cold.HaulSite = null;
    }

    /// <summary>
    /// A hauler standing at its storage after the drop-off (<see cref="HaulStep"/>): the trip has a storage but no leg or loose item left.
    /// </summary>
    static bool Lingers(CharacterCold cold) => cold.HaulTo is not null && cold.HaulSite is null && cold.HaulItem is null;

    /// <summary>
    /// The beat after a drop-off (<see cref="StepPauseSeconds"/>, set by <see cref="StopWalk"/>): the trip ends once it has passed, so the next job's
    /// walk starts after it.
    /// </summary>
    static void Linger(World world, int slot, ref CharacterHot n)
    {
        n.TaskTime -= world.TickSeconds;
        if (n.TaskTime <= 0) End(world, slot);
    }

    /// <summary>
    /// A hauler at the loose item it fetches (<c>Task_Pickup</c>, docs/game/buildings-production.md "Hauling"): the stack goes into the inventory
    /// (what does not fit stays on the ground), then it makes for the storage with what the storage takes; with nothing it can take the trip ends.
    /// The walk to the item stops at the pick-up, so the leg to the storage starts after <see cref="StepPauseSeconds"/>: a walk left to end by
    /// itself set the idle wait of a finished path, and the hauler stood up to 10 s with its load. Not there yet: nothing happens (the walk is <see cref="ApproachLoose"/>'s).
    /// </summary>
    void PickUpLoose(World world, int slot, CharacterCold cold, Items.Inventory inv, Machine store, int item)
    {
        ref var n = ref world.Characters.Next[slot];
        if (world.GroundItems.Find(item) is not { } loose)
        {
            End(world, slot);
            return;
        }

        if (!PickUps.InReach(n.Position, loose.Position)) return;

        int before = loose.Item.Quantity;
        bool whole = inv.Add(loose.Item);
        if (whole) world.GroundItems.Take(item);
        if (whole || loose.Item.Quantity < before) world.EmitSound(SoundKind.PickedUp, slot, n.Position);

        cold.HaulItem = null;
        StopWalk(ref n, cold);
        if (Delivered(world, inv, store, ditch: false).Any()) cold.HaulSite = store.Site;
        else End(world, slot);
    }

    /// <summary>
    /// The walk of a hauler to the loose item it fetches: it stands once in reach (<see cref="PickUps.InReach"/>); a path that ended short leaves the
    /// item out for <see cref="ConstructionSystem.UnreachableSeconds"/> (<see cref="LooseSource.Key"/>) and ends the trip, so the next pick is
    /// another source.
    /// </summary>
    void ApproachLoose(World world, int slot, ref CharacterHot n, CharacterCold cold, int item, float dt)
    {
        const ushort path = (ushort)MoveFlags.AnyPath;
        if (world.GroundItems.Find(item) is not { } loose || !Able(cold))
        {
            End(world, slot);
            return;
        }

        if (PickUps.InReach(n.Position, loose.Position))
        {
            StopWalk(ref n, cold);
            return;
        }

        if ((n.Flags & path) != 0) return;
        if (n.TaskTime > 0)
        {
            cold.NoteUnreachable(LooseSource.Key(item), world.Tick + (long)MathF.Ceiling(ConstructionSystem.UnreachableSeconds / world.TickSeconds));
            End(world, slot);
            return;
        }

        n.Goal = new Vector2(loose.Position.X, loose.Position.Z);
        (cold.GoalHeight, cold.GoalHeightAt) = (loose.Position.Y, n.Goal);
        n.Mode = SpeedMode.Free;
        n.Flags |= (ushort)MoveFlags.NeedPath;
        n.TaskTime = PursuitSystem.RepathSeconds;
    }
}