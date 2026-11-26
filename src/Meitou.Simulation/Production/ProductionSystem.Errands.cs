using System.Numerics;
using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Carrying;
using Meitou.Simulation.Construction;
using Meitou.Simulation.Locks;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Player;

namespace Meitou.Simulation.Production;

/// <summary>
/// The carrying jobs of the lists (docs/game/ai-tasks.md "Perma-jobs" and "Finders of the jobs"): corpse disposal (the lift 138 Find corpses and the
/// delivery 137 Corpse disposal, two jobs) and the removal of intruders (223 / 273 with their companions 224 / 274). A free character with the job
/// lifts what the job's finder names and walks it to where the delivery takes it; the lift and the put-down are the carry system's.
/// </summary>
public sealed partial class ProductionSystem
{
    #region Errands and their jobs

    /// <summary>
    /// Ticks between the steps of a carrying job's carry under way (engine choice); its start is the decision step's.
    /// </summary>
    public const int ErrandEveryTicks = 15;
    /// <summary>
    /// A body carried out is put down within this of its goal (engine choice).
    /// </summary>
    public const float ErrandArrive = 40;
    /// <summary>
    /// Game minutes an intruder carrier may stand still before it drops the body (<b>Verified</b>: 5, <c>FUN_1403356e0</c>).
    /// </summary>
    public const float CarryOutMinutes = 5;
    /// <summary>
    /// How far past a town's farthest building an intruder is carried out of the town (<b>Observed</b>: 200 beyond the town's edge).
    /// </summary>
    public const float OutsideTown = 200;
    /// <summary>
    /// The finders look this far for a body or an intruder (the sensed list's reach, an engine stand-in as the rescue job's).
    /// </summary>
    const float ErrandSight = 1600;

    /// <summary>
    /// The carry system that lifts and puts down the bodies of the carrying jobs; null: they never run.
    /// </summary>
    public CarrySystem? Carry { get; set; }

    /// <summary>
    /// What a carrying job carries a body for.
    /// </summary>
    enum ErrandKind : byte
    {
        /// <summary>
        /// Corpse disposal: the lift of 138 (or of 137's plan) and the delivery of 137.
        /// </summary>
        Corpse,
        /// <summary>
        /// A home intruder carried out of the building (223 / 224).
        /// </summary>
        OutOfBuilding,
        /// <summary>
        /// An intruder carried out of the town (273 / 274).
        /// </summary>
        OutOfTown
    }

    /// <summary>
    /// One carrying job under way: what the character carries it for, the goal of its walk and how many walks ended short.
    /// </summary>
    sealed class Errand
    {
        /// <summary>
        /// What the carrying is for.
        /// </summary>
        public ErrandKind Kind;
        /// <summary>
        /// The goal of the walk.
        /// </summary>
        public Vector2 Goal;
        /// <summary>
        /// The body carried.
        /// </summary>
        public CharacterId Body;
        /// <summary>
        /// The carrier's current GoTo is this job's own (else a GoTo task is an order of the player's).
        /// </summary>
        public bool Walking;
        /// <summary>
        /// Intruder carry-out: the game hours at the first step the carrier was not moving (cleared while it moves).
        /// </summary>
        public double? StoppedAt;
        /// <summary>
        /// Order 136 started it (<see cref="CorpseOrder"/>): it needs no job in the list, its machine is the order's (<see cref="Site"/>), and its walk
        /// ending short of it ends it.
        /// </summary>
        public bool Ordered;
        /// <summary>
        /// A corpse carried to its machine (137's delivery, or the order's); false while a corpse job's lift runs, which ends with the lift.
        /// </summary>
        public bool Delivering;
        /// <summary>
        /// The list job that started it (<see cref="TaskType.None"/>: the order).
        /// </summary>
        public TaskType Job;
        /// <summary>
        /// The corpse machine the carrier is taking the body to: claimed against its squad mates (the squad blackboard's goal list).
        /// </summary>
        public string? Site;
        /// <summary>
        /// That machine (<see cref="CorpseDisposal.Find"/>), found again on each step.
        /// </summary>
        public CorpseMachine? Machine;
    }

    /// <summary>
    /// Derived state, not saved or hashed: a lift under way is looked at again from the characters' own state when it is lost.
    /// </summary>
    readonly Dictionary<CharacterId, Errand> _errands = [];

    /// <summary>
    /// The finders' kinds a job type runs, with the companion type that must be in the list for the carrying out.
    /// </summary>
    static ErrandKind? KindOf(TaskType type) => type switch
    {
        TaskType.FindADeadGuy or TaskType.DeadGuysGoInThePot => ErrandKind.Corpse,
        TaskType.PickupIntruders => ErrandKind.OutOfBuilding,
        TaskType.PickupIntrudersTown => ErrandKind.OutOfTown,
        _ => null,
    };

    /// <summary>
    /// The delivery job that must be in the list for an intruder's carrying out.
    /// </summary>
    static TaskType CompanionOf(ErrandKind kind) => kind == ErrandKind.OutOfBuilding ? TaskType.TakeIntruderOutside : TaskType.TakeIntruderOutsideTown;

    /// <summary>
    /// Whether an intruder's carrying out can run (<b>Verified</b>: the lifts 223 / 273 do not look at the companion job; only the delivery jobs
    /// 224 / 274 do): the companion is in the list. Otherwise the carrier keeps the body and the errand waits.
    /// </summary>
    static bool DeliveryReady(CharacterCold cold, ErrandKind kind) => PermaJobs.IndexOf(cold, CompanionOf(kind)) >= 0;

    /// <summary>
    /// The carrying errands under way (serial, slot order, every <see cref="ErrandEveryTicks"/> ticks): each is stepped. Their starts are the
    /// decision step's (<see cref="ErrandJobs"/>).
    /// </summary>
    void Errands(World world)
    {
        if (Carry is null || !world.Beat(ErrandEveryTicks, TickPhases.Errands) || _errands.Count == 0) return;
        var table = world.Characters;
        foreach (var gone in _errands.Keys.Where(k => !table.IsAlive(k)).ToList()) _errands.Remove(gone);
        for (int i = 0; i < table.HighWater; i++)
        {
            if (table.Next[i].Alive && table.Cold(i) is { } cold && _errands.TryGetValue(table.IdOf(i), out var errand))
                StepErrand(world, i, cold, errand);
        }
    }

    /// <summary>
    /// The carrying jobs as the decision step's contributor (step 5): a character carrying nothing, able to carry and not fleeing, with a job whose
    /// finder finds something, lifts it (the lift does not need the delivery job nor a machine). An intruder job runs while its lift is under way and
    /// while its carrying out can run (<see cref="DeliveryReady"/>); a corpse job's lift ends with the lift, and 137 delivers a carried corpse as a
    /// job of its own.
    /// </summary>
    sealed class ErrandJobs(ProductionSystem owner) : IJobSource
    {
        /// <summary>
        /// What the last <see cref="CanRun"/> found: the tick, the slot and job, the body and the walk's goal (tick -1: nothing).
        /// </summary>
        (long Tick, int Slot, int K, CharacterId Body, Vector2 Goal) _found = (-1, -1, -1, CharacterId.None, default);

        /// <summary>
        /// Whether carrying job <paramref name="k"/> can run: a corpse job as <see cref="CorpseCanRun"/>; an intruder job under way runs on while its
        /// carrying out can (<see cref="DeliveryReady"/>); with a body carried and none under way it starts one again (<see cref="CarriedOut"/>), else
        /// its finder must find a body to lift.
        /// </summary>
        public DecisionCheck CanRun(World world, int slot, int k)
        {
            var table = world.Characters;
            var cold = table.Cold(slot)!;
            var type = cold.Jobs[k].Type;
            if (owner.Carry is null || KindOf(type) is not { } kind) return DecisionCheck.No(null);
            if (kind == ErrandKind.Corpse) return CorpseCanRun(world, slot, k, type == TaskType.DeadGuysGoInThePot);

            if (owner._errands.TryGetValue(table.IdOf(slot), out var e))
            {
                if (e.Kind != kind || !Lifting(table, slot, cold, e) && cold.Carrying != e.Body) return DecisionCheck.No("another errand");
                return cold.Carrying.IsNone || DeliveryReady(cold, kind) ? DecisionCheck.Runs : DecisionCheck.No("nothing to deliver to");
            }

            if (!cold.Carrying.IsNone) return CarriedOut(world, slot, k, cold, kind);
            if (!Free(world, cold)) return DecisionCheck.No("cannot carry");
            return Found(world, slot, k, owner.FindIntruder(world, slot, cold, Lifted(world, cold.SquadId, table.IdOf(slot)), kind));
        }

        /// <summary>
        /// The corpse jobs (<b>Verified</b>, docs/game/ai-tasks.md "Finders of the jobs"): 138 Find corpses lifts (desire <c>FUN_140599480</c>: 0 on
        /// Hold or while carrying; its finder a corpse); 137 Corpse disposal (finder <c>FUN_1408d1090</c>: a disposal) delivers a carried dead body
        /// (its list B 99), and plans 138's lift when nothing is carried. A lift or a delivery under way runs on for the job that started it.
        /// </summary>
        DecisionCheck CorpseCanRun(World world, int slot, int k, bool delivery)
        {
            var table = world.Characters;
            var cold = table.Cold(slot)!;
            var id = table.IdOf(slot);
            if (owner._errands.TryGetValue(id, out var e) && (Lifting(table, slot, cold, e) || e.Delivering && cold.Carrying == e.Body))
                return e.Kind == ErrandKind.Corpse && e.Job == cold.Jobs[k].Type ? DecisionCheck.Runs : DecisionCheck.No("another errand");

            if (delivery && owner.NearestIncinerator(world, table.Next[slot].Position, id, cold.SquadId) is null)
                return DecisionCheck.No("no disposal");
            if (!cold.Carrying.IsNone) return Deliverable(world, slot, k, cold, delivery);

            if ((cold.Orders & Player.CharacterOrders.Hold) != 0) return DecisionCheck.No("holding");
            if (!Free(world, cold)) return DecisionCheck.No("cannot carry");
            return Found(world, slot, k, owner.FindCorpse(world, slot, cold, Lifted(world, cold.SquadId, id)));
        }

        /// <summary>
        /// A corpse job of a character carrying a body: 138's desire is 0; 137 delivers a dead one (list B 99, <see cref="CarriesCorpse"/>), and with
        /// any other body its plan would need 138, whose desire is 0.
        /// </summary>
        DecisionCheck Deliverable(World world, int slot, int k, CharacterCold cold, bool delivery)
        {
            if (!delivery) return DecisionCheck.No("carrying");
            if (!CarriesCorpse(world, cold)) return DecisionCheck.No("not carrying a corpse");

            _found = (world.Tick, slot, k, cold.Carrying, default);
            return DecisionCheck.Run;
        }

        /// <summary>
        /// An intruder job of a character that carries a body with no carrying out under way (it was broken off, or the body was lifted otherwise):
        /// the desire of 224 / 274 is 2.0 for any carried character the carrier does not regard as friendly (<c>FUN_1405a13a0</c>, <b>Verified</b>),
        /// so the carrying out starts again from where the carrier stands (<see cref="CarryOutGoal"/>).
        /// </summary>
        DecisionCheck CarriedOut(World world, int slot, int k, CharacterCold cold, ErrandKind kind)
        {
            var table = world.Characters;
            if (!DeliveryReady(cold, kind)) return DecisionCheck.No("nothing to deliver to");
            if (!table.TryResolveNext(cold.Carrying, out int b) || table.Cold(b) is not { } body) return DecisionCheck.No("carrying");
            if (owner.Carry!.Friendly(cold, body)) return DecisionCheck.No("carrying a friend");

            _found = (world.Tick, slot, k, cold.Carrying, owner.CarryOutGoal(world, slot, kind));
            return DecisionCheck.Run;
        }

        /// <summary>
        /// Lifts the body <see cref="CanRun"/> found this tick and starts the errand, or, with a body carried (137's corpse, an intruder's carrying
        /// out started again), starts its delivery.
        /// </summary>
        public void Start(World world, int slot, int k)
        {
            var table = world.Characters;
            var cold = table.Cold(slot)!;
            var type = cold.Jobs[k].Type;
            if (_found.Tick != world.Tick || _found.Slot != slot || _found.K != k || KindOf(type) is not { } kind) return;

            var id = table.IdOf(slot);
            if (!cold.Carrying.IsNone)
            {
                var delivery = new Errand { Kind = kind, Goal = _found.Goal, Body = cold.Carrying, Delivering = true, Job = type };
                owner._errands[id] = delivery;
                owner.StepErrand(world, slot, cold, delivery);
                return;
            }

            if (!owner.Carry!.BeginPickUp(world, slot, _found.Body, source: TaskSource.Job, job: k, row: type)) return;
            owner._errands[id] = new Errand { Kind = kind, Goal = _found.Goal, Body = _found.Body, Job = type };
        }

        /// <summary>
        /// The finder's pick, kept for <see cref="Start"/>: the job can run when it found a body.
        /// </summary>
        DecisionCheck Found(World world, int slot, int k, (CharacterId Id, Vector2 Goal)? body)
        {
            if (body is not { } b) return DecisionCheck.No("nothing found");

            _found = (world.Tick, slot, k, b.Id, b.Goal);
            return DecisionCheck.Run;
        }

        /// <summary>
        /// Whether the lift of errand <paramref name="e"/> runs.
        /// </summary>
        static bool Lifting(CharacterTable table, int slot, CharacterCold cold, Errand e) =>
            table.Next[slot].Task == (byte)CharacterTask.PickUp && cold.CarryTarget == e.Body;

        /// <summary>
        /// Whether the character may start a lift: carrying nothing, able to carry and not fleeing.
        /// </summary>
        static bool Free(World world, CharacterCold cold) =>
            cold.Carrying.IsNone && CarrySystem.CanCarry(world, cold, TaskType.DeadGuysGoInThePot) && !cold.Fleeing;
    }

    /// <summary>
    /// State 99 isCarriedDudeDead (<c>FUN_1405a14e0</c>, <b>Verified</b>): the character carries a body whose dead byte (+0x5bc) is set.
    /// </summary>
    static bool CarriesCorpse(World world, CharacterCold cold) =>
        world.Characters.TryResolveNext(cold.Carrying, out int b) && world.Characters.Cold(b)?.Medical is { Dead: true };

    /// <summary>
    /// The bodies a finder of a character of squad <paramref name="squad"/> skips: every body carried (the carried byte, global), and the bodies its
    /// squad mates work on (their goal on the squad's blackboard, <see cref="SquadGoals"/>, <b>Verified</b>); another squad may go for the same body
    /// until the lift attaches.
    /// </summary>
    static LiftedBodies Lifted(World world, int squad, CharacterId asker)
    {
        var table = world.Characters;
        var carried = new HashSet<CharacterId>();
        for (int i = 0; i < table.HighWater; i++)
            if (table.Next[i].Alive && table.Cold(i) is { } c && !c.Carrying.IsNone) carried.Add(c.Carrying);
        return new LiftedBodies(carried, SquadGoals.Of(world), squad, asker);
    }

    /// <summary>
    /// The bodies a finder skips (<see cref="Lifted"/>).
    /// </summary>
    sealed class LiftedBodies(HashSet<CharacterId> carried, SquadGoals goals, int squad, CharacterId asker)
    {
        /// <summary>
        /// Whether a finder skips <paramref name="body"/>: someone carries it, or a squad mate's goal is on it.
        /// </summary>
        public bool Contains(CharacterId body) => carried.Contains(body) || goals.Working(squad, asker, GoalTarget.Of(body)) > 0;
    }

    /// <summary>
    /// The corpse machines the carriers of the squad are taking a body to, in the squad's goal list.
    /// </summary>
    void IGoalSource.Publish(World world, SquadGoals goals)
    {
        foreach (var (id, e) in _errands)
        {
            if (e.Site is not null && world.Characters.TryResolveNext(id, out int i) && world.Characters.Cold(i) is { } c)
                goals.Add(c.SquadId, id, GoalTarget.At(e.Site));
        }
    }

    #endregion

    #region Stepping a carrying job

    /// <summary>
    /// One step of a character's errand: lifted, it walks the body to its goal and lets it go there (or feeds it to the corpse machine); the lift
    /// lost or the walk given up ends it.
    /// </summary>
    void StepErrand(World world, int i, CharacterCold cold, Errand e)
    {
        var table = world.Characters;
        var id = table.IdOf(i);
        ref var n = ref table.Next[i];
        var task = (CharacterTask)n.Task;
        if (task is CharacterTask.PickUp or CharacterTask.PutDown) return;
        if (cold.Carrying.IsNone)
        {
            // Not lifted (the lift broke off) or put down: over.
            _errands.Remove(id);
            return;
        }

        // An order of the player took the carrier elsewhere: the job is over, the body stays with it.
        if (task == CharacterTask.GoTo && !e.Walking) { _errands.Remove(id); return; }
        if (task is not (CharacterTask.GoTo or CharacterTask.Idle or CharacterTask.Wander or CharacterTask.Follow)) { _errands.Remove(id); return; }
        bool wasWalking = e.Walking;
        if (task != CharacterTask.GoTo) e.Walking = false;
        if (e.Kind == ErrandKind.Corpse && wasWalking && task != CharacterTask.GoTo) NoteWalkEnd(world, cold, n, e);
        if (!ErrandGoal(world, i, cold, e, out var machine, out var goal) || Delivered(world, i, machine, goal)) return;

        if (task == CharacterTask.GoTo)
        {
            e.StoppedAt = null;
            return;   // on its way
        }

        if (GivenUp(world, i, e, wasWalking)) return;

        e.Goal = goal;
        e.Walking = true;
        TaskStart.StepTo(table, i, goal, cold.RunSpeed);
    }

    /// <summary>
    /// The goal of an errand's walk: a carry-out's own (while its delivery job is listed, <see cref="DeliveryReady"/>), a corpse delivery's machine
    /// (the order's, else the one <see cref="NearestIncinerator"/> finds). False when the step goes no further: a corpse lift is over once the body
    /// is held, a delivery with no machine keeps the body.
    /// </summary>
    bool ErrandGoal(World world, int i, CharacterCold cold, Errand e, out CorpseMachine? machine, out Vector2 goal)
    {
        (machine, goal) = (null, e.Goal);
        if (e.Kind != ErrandKind.Corpse) return DeliveryReady(cold, e.Kind);

        // A corpse job's lift is over once the body is held; its delivery is 137's (or the order's), decided anew.
        var id = world.Characters.IdOf(i);
        if (!e.Delivering)
        {
            _errands.Remove(id);
            return false;
        }

        machine = e.Ordered ? OrderedMachine(world, e.Site) : NearestIncinerator(world, world.Characters.Next[i].Position, id, cold.SquadId);
        if (machine is null)
        {
            // The carrier keeps the body: a job's errand waits for a machine, the order's is over with its machine.
            if (e.Ordered) _errands.Remove(id);
            return false;
        }

        (e.Site, e.Machine) = (machine.Id, machine);
        goal = DisposalGoal(world, machine, world.Characters.Next[i].Position);
        return true;
    }

    /// <summary>
    /// The carrier at its goal feeds the body to <paramref name="machine"/>, or lets it go at a carry-out's goal, and the errand is over; false when
    /// it is not there yet.
    /// </summary>
    bool Delivered(World world, int i, CorpseMachine? machine, Vector2 goal)
    {
        ref readonly var n = ref world.Characters.Next[i];
        bool arrived = machine is not null
            ? AtDisposal(world, n, machine)
            : Vector2.Distance(new Vector2(n.Position.X, n.Position.Z), goal) <= ErrandArrive;
        if (!arrived) return false;

        if (machine is not null) Incinerate(world, i, machine);
        else Carry!.LetGo(world, i);   // a carry-out at its goal: the task's own unlink, as below

        _errands.Remove(world.Characters.IdOf(i));
        return true;
    }

    /// <summary>
    /// A carrier standing short of its goal gives up: the order's walk that ended short (the path failed, which ends the order: the failed flag's
    /// pop, <c>FUN_14050d080</c>), or a carry-out whose stand is over (<see cref="CarryOutOver"/>, the body let go). False when it walks on.
    /// </summary>
    bool GivenUp(World world, int i, Errand e, bool wasWalking)
    {
        var id = world.Characters.IdOf(i);
        if (e.Ordered && wasWalking)
        {
            _errands.Remove(id);
            return true;
        }

        if (e.Kind == ErrandKind.Corpse || !CarryOutOver(world, e, wasWalking)) return false;

        Carry!.LetGo(world, i);   // the task's own unlink (FUN_1405ce1e0), not the put-down task
        _errands.Remove(id);
        return true;
    }

    /// <summary>
    /// The update of <c>Task_TakeIntruderOutside</c> / <c>...OutsideTown</c> (slot 7 <c>FUN_1403356e0</c>, <b>Verified</b>), for a carrier that is
    /// not moving: it drops the body when its walk is over (here: the walk this job started has ended, short of the goal or not) or when 5 game
    /// minutes of clock have passed since it first stood still (the timer is zeroed while it moves).
    /// </summary>
    static bool CarryOutOver(World world, Errand e, bool wasWalking)
    {
        e.StoppedAt ??= world.GameHours;
        return wasWalking || world.GameHours - e.StoppedAt.Value >= CarryOutMinutes / 60.0;
    }

    /// <summary>
    /// The walk to the machine ended: if the carrier is not at it, the path failed and the carrier skips that machine for
    /// <see cref="ConstructionSystem.UnreachableSeconds"/> in the shared ignore map, <see cref="CharacterCold.Unreachable"/> (<c>FUN_1405b0290</c> in
    /// <c>FUN_1408d0770</c>).
    /// </summary>
    void NoteWalkEnd(World world, CharacterCold cold, in CharacterHot n, Errand e)
    {
        if (e.Machine is not { } machine || AtDisposal(world, n, machine)) return;

        cold.NoteUnreachable(machine.Id, world.Tick + (long)MathF.Ceiling(ConstructionSystem.UnreachableSeconds / world.TickSeconds));
    }

    /// <summary>
    /// Whether the character's path to site <paramref name="siteId"/> is known to be impossible now.
    /// </summary>
    bool PathImpossible(World world, CharacterId id, string siteId) =>
        world.Characters.TryResolveNext(id, out int i) && world.Characters.Cold(i) is { } cold && cold.IsUnreachable(siteId, world.Tick);

    #endregion

    #region Corpse disposal (137 / 138)

    /// <summary>
    /// The corpse disposals the finders choose among, gathered once per <see cref="ErrandEveryTicks"/>.
    /// </summary>
    CorpseMachineCache? _disposals;

    /// <summary>
    /// Feeds the carried body into the machine (<see cref="CorpseDisposal.Feed"/>, <c>Task_FeedCorpseIntoMachine</c> slot 3, <b>Verified</b>): it is
    /// removed with its items, the sound plays and the machine's functionality <c>string</c> floats up. The original plays the lift clip back first;
    /// Meitou removes it at once.
    /// </summary>
    void Incinerate(World world, int i, CorpseMachine machine) =>
        CorpseDisposal.Feed(world, Carry!, i, machine.Position, Data(machine.RecordId)?.ShownLabel() ?? "");

    /// <summary>
    /// Where the carrier at <paramref name="from"/> walks to: a player machine's operator goal (<see cref="GoalOf"/>), else the floor point at or
    /// next to the disposal.
    /// </summary>
    Vector2 DisposalGoal(World world, CorpseMachine machine, Vector3 from)
    {
        if (world.Machines.Find(machine.Id) is { } m && Site(world, machine.Id) is { } site)
        {
            var spot = GoalOf(world, site, m, from);
            return new Vector2(spot.X, spot.Z);
        }

        var to = machine.Position;
        var at = world.Walkability.FloorNear(to.X, to.Z, to.Y, MovementSystem.FloorSlack) ?? to;
        return new Vector2(at.X, at.Z);
    }

    /// <summary>
    /// Whether the carrier is at the disposal: a player machine's <see cref="AtSite"/>, else within the BUILDING's use range
    /// (<c>UseableStuff::getReachRange</c>, doubled once stopped, <see cref="SiteReach.Within"/>) on its storey.
    /// </summary>
    internal bool AtDisposal(World world, in CharacterHot n, CorpseMachine machine)
    {
        if (world.Machines.Find(machine.Id) is { } m && Site(world, machine.Id) is { } site) return AtSite(world, n, site, m);

        float range = Data(machine.RecordId)?.UseRange ?? 20;
        return SiteReach.Within(n, Gap(n.Position, machine.Position), range)
            && MathF.Abs(n.Position.Y - machine.Position.Y) < SiteReach.HeightLimit(door: false, machine.Inside);
    }

    /// <summary>
    /// The finder of job 138 (<see cref="CorpseDisposal.FindCorpse"/>, <b>Verified</b>): a dead, non-robot body in a cage of the carrier's faction
    /// among its town candidates (<see cref="Locks.FixtureRegistry.TownCandidates"/>; every cage without population data), else the nearest dead body
    /// within <see cref="ErrandSight"/> (the sensed list's stand-in) that nobody has. The goal is the machine (found again on each step).
    /// </summary>
    (CharacterId Id, Vector2 Goal)? FindCorpse(World world, int slot, CharacterCold cold, LiftedBodies lifted)
    {
        var at = world.Characters.Next[slot].Position;
        var inReach = _towns is { } data ? world.Fixtures.TownCandidates(world, data, cold, at) : static _ => true;
        var body = CorpseDisposal.FindCorpse(world, slot, f => inReach(f.Position), ErrandSight, lifted.Contains);
        return body.IsNone ? null : (body, Vector2.Zero);
    }

    /// <summary>
    /// The corpse machine a body is taken to (<see cref="CorpseDisposal.Find"/>, the NPCs' finder): the player's machines and the towns' disposals
    /// alike, leaving out one a squad mate has claimed, one whose operator places are taken and one whose path failed.
    /// </summary>
    CorpseMachine? NearestIncinerator(World world, Vector3 from, CharacterId self, int squad)
    {
        if (!world.Characters.TryResolveNext(self, out int me)) return null;

        var goals = SquadGoals.Of(world);
        var machines = (_disposals ??= new CorpseMachineCache(ErrandEveryTicks)).Get(world, _towns);
        return CorpseDisposal.Find(world, _towns, machines, me, from, m => Unavailable(world, goals, self, squad, m));
    }

    /// <summary>
    /// Whether the machine is out for the carrier: claimed by a squad mate, every operator place taken, or its path failed.
    /// </summary>
    bool Unavailable(World world, SquadGoals goals, CharacterId self, int squad, CorpseMachine m) =>
        MachineClaimed(goals, self, squad, m.Id) || OperatorsOf(world, m.Id) >= (Data(m.RecordId)?.MaxOperators ?? 1)
            || PathImpossible(world, self, m.Id);

    /// <summary>
    /// Whether another member of the squad is already taking a body to the machine at <paramref name="site"/> (<c>FUN_1402690e0</c> on the squad's
    /// goal list, <b>Verified</b>): not offered, even before it operates it.
    /// </summary>
    static bool MachineClaimed(SquadGoals goals, CharacterId self, int squad, string site) => goals.Working(squad, self, GoalTarget.At(site)) > 0;

    #endregion

    #region Corpse Disposal order (136)

    /// <summary>
    /// The owner of order 136 (null until asked).
    /// </summary>
    CorpseOrder? _corpseOrder;

    /// <summary>
    /// The owner of order 136 Disposing of corpse (<see cref="World.OrderOwnerOf"/>).
    /// </summary>
    internal IOrderOwner CorpseOrders => _corpseOrder ??= new CorpseOrder(this);

    /// <summary>
    /// The Corpse Disposal order without Shift (<see cref="DisposeCorpseOrder"/>): each character listed gets order 136 on the machine through its
    /// queue (the giver <c>FUN_1407f9e20</c>), the queue cleared unless <see cref="OrderCommand.Queued"/>.
    /// </summary>
    void GiveDisposal(World world, DisposeCorpseOrder order)
    {
        var table = world.Characters;
        var at = Site(world, order.Site) is { } site ? OrderEntry.AtBuilding(world, order, site.Position) : default;
        foreach (var id in Who(world, order.Characters))
        {
            if (!table.TryResolveNext(id, out int slot)) continue;

            var entry = new OrderEntry(TaskType.FeedCorpseIntoMachine, CorpseOrders) { Site = order.Site, TaskPoint = at };
            PlayerOrders.Give(world, slot, entry, order.Queued);
        }
    }

    /// <summary>
    /// The corpse disposal of id <paramref name="site"/> (<see cref="CorpseDisposal.Machines"/>), null when there is none.
    /// </summary>
    CorpseMachine? OrderedMachine(World world, string? site)
    {
        if (site is null) return null;

        foreach (var m in (_disposals ??= new CorpseMachineCache(ErrandEveryTicks)).Get(world, _towns))
            if (m.Id == site) return m;

        return null;
    }

    /// <summary>
    /// Order 136 Disposing of corpse (docs/game/ui-input.md "Context menu verbs", <b>Verified</b>: the giver <c>FUN_1407f9e20</c> gives it to every
    /// selected character, its queue cleared): row 136's list A 100 (carrying nothing) makes it done at once, its list B 99 (a dead body carried) and
    /// <c>isAtLocation</c> take the carrier to the order's machine, where <c>Task_FeedCorpseIntoMachine</c> feeds the body in. Its act-out is a
    /// corpse errand on that machine (<see cref="Errand.Ordered"/>), whose walk is the order's task and whose feed waits for the errand step.
    /// </summary>
    sealed class CorpseOrder(ProductionSystem owner) : IOrderOwner, IOrderSteps, ITaskHandler
    {
        /// <summary>
        /// Gone when the machine is gone or nothing is carried (list A 100 holds); blocked while the body carried is not dead (99 fails, and its
        /// provider 138 has desire 0 while carrying, <c>FUN_140599480</c>) or the character is carried; else it runs.
        /// </summary>
        public OrderCheck CanRun(World world, int slot, OrderEntry order)
        {
            var cold = world.Characters.Cold(slot)!;
            if (owner.OrderedMachine(world, order.Site) is null || cold.Carrying.IsNone) return OrderCheck.Gone;

            return CarriesCorpse(world, cold) && cold.CarriedBy.IsNone ? OrderCheck.Run : OrderCheck.Blocked();
        }

        /// <summary>
        /// Starts the walk to the machine as the order's task (<see cref="ITaskHandler.Start"/> below).
        /// </summary>
        public void Start(World world, int slot, OrderEntry order) =>
            TaskStart.Start(world, slot, new TaskRequest(CharacterTask.GoTo, TaskType.FeedCorpseIntoMachine, TaskSource.Order) { Site = order.Site });

        /// <summary>
        /// The errand goes on while the walk is over and the feed waits for the errand step.
        /// </summary>
        public bool Between(World world, int slot, OrderEntry order) => Ordered(world, slot);

        /// <summary>
        /// The order's errand on its machine: the carrier stands, then walks the body there (or feeds it in at once when it stands there).
        /// </summary>
        void ITaskHandler.Start(World world, int slot, in TaskRequest request)
        {
            var table = world.Characters;
            var cold = table.Cold(slot)!;
            TaskStart.Stand(table, slot);
            if (cold.Carrying.IsNone) return;

            var errand = new Errand { Kind = ErrandKind.Corpse, Body = cold.Carrying, Ordered = true, Delivering = true, Site = request.Site };
            owner._errands[table.IdOf(slot)] = errand;
            owner.StepErrand(world, slot, cold, errand);
        }

        /// <summary>
        /// Another task or the Stop ends the order's errand; the body stays with the carrier.
        /// </summary>
        void ITaskHandler.End(World world, int slot, CharacterTask task, TaskEnd reason)
        {
            if (Ordered(world, slot)) owner._errands.Remove(world.Characters.IdOf(slot));
        }

        /// <summary>
        /// The walk is done once the order's errand is over.
        /// </summary>
        bool ITaskHandler.Done(World world, int slot, CharacterTask task) => !Ordered(world, slot);

        /// <summary>
        /// Whether the character's errand is the order's.
        /// </summary>
        bool Ordered(World world, int slot) => owner._errands.TryGetValue(world.Characters.IdOf(slot), out var e) && e.Ordered;
    }

    #endregion

    #region Intruders (223 / 273)

    /// <summary>
    /// The finders of jobs 223 (<c>FUN_14099c110</c>) and 273 (<c>FUN_14099c250</c>, whose own test is the carrier's town, docs/game/ai-tasks.md; the
    /// AI's: <see cref="Kidnap.TownDutySystem"/>) for a player character, <b>Verified</b> (<c>FUN_14099c110</c>: down by the character's isDown, not
    /// carried, special state 0, no race test, so a non-carriable one counts and a refused lift leaves the finder returning it; its sensed mask
    /// leaves a dead body out, docs/game/ai-tasks.md, which this job still takes: engine choice): a downed character in the building the carrier
    /// stands in, not carried, not caged (223: not in a bed either, special state 0). Engine choices: the building is the player's complete building
    /// with doors whose footprint holds the carrier (the original's "home" of 223 is the Ownerships' +0x38, whose value for the player faction is
    /// <b>Unknown</b>); an intruder is knocked out, alive, and neither of the carrier's faction nor an ally (the sensed list's exclude bits 0x201 are
    /// <b>Unknown</b>); 273 is a town finder (<see cref="FindTownIntruder"/>). The goal is outside the door (223 / 224,
    /// <c>Task_TakeIntruderOutside</c>) or outside the town (273 / 274, <c>Task_TakeIntruderOutsideTown</c>).
    /// </summary>
    (CharacterId Id, Vector2 Goal)? FindIntruder(World world, int slot, CharacterCold cold, LiftedBodies lifted, ErrandKind kind)
    {
        var table = world.Characters;
        var at = table.Next[slot].Position;
        if (kind == ErrandKind.OutOfTown) return FindTownIntruder(world, slot, cold, lifted);
        if (EnclosedAt(world, at) is not { } site) return null;
        CharacterId? best = null;
        float bestD = float.MaxValue;
        for (int s = 0; s < table.HighWater; s++)
        {
            if (s == slot || !table.Next[s].Alive || table.Cold(s) is not { Medical.Incapacitated: true } body) continue;
            var id = table.IdOf(s);
            if (lifted.Contains(id) || body.IsImprisoned || !body.CarriedBy.IsNone) continue;
            if (kind == ErrandKind.OutOfBuilding && body.InSomething != HeldState.None) continue;
            if (Carry!.Friendly(cold, body)) continue;
            if (EnclosedAt(world, table.Next[s].Position) != site) continue;
            float d = Gap(at, table.Next[s].Position);
            if (d <= ErrandSight && d < bestD) (best, bestD) = (id, d);
        }

        return best is { } found ? (found, OutsideGoal(world, site, at, kind)) : null;
    }

    /// <summary>
    /// The finder of job 273 (<c>FUN_14099c250</c>, <b>Observed</b>): the carrier stands in a town of the player's and a downed character, not
    /// carried or caged, stands in that same town inside its walls (<c>amInsideTownWalls</c>, <see cref="Kidnap.TownDutySystem.AmInsideTownWalls"/>;
    /// true without that system); no building is needed, so a body in a courtyard counts.
    /// </summary>
    (CharacterId Id, Vector2 Goal)? FindTownIntruder(World world, int slot, CharacterCold cold, LiftedBodies lifted)
    {
        var table = world.Characters;
        var at = table.Next[slot].Position;
        if (PlayerTownAt(world, at) is not { } town) return null;
        var walls = world.SystemOf<Kidnap.TownDutySystem>();
        CharacterId? best = null;
        float bestD = float.MaxValue;
        for (int s = 0; s < table.HighWater; s++)
        {
            if (s == slot || !table.Next[s].Alive || table.Cold(s) is not { Medical: { Unconscious: true, Dead: false } } body) continue;
            var id = table.IdOf(s);
            if (lifted.Contains(id) || body.IsImprisoned || !body.CarriedBy.IsNone || !CarrySystem.CanBeLifted(body)) continue;
            if (Carry!.Friendly(cold, body) || PlayerTownAt(world, table.Next[s].Position) != town) continue;
            if (walls is not null && !walls.AmInsideTownWalls(world, table.Next[s].Position)) continue;
            float d = Gap(at, table.Next[s].Position);
            if (d <= ErrandSight && d < bestD) (best, bestD) = (id, d);
        }

        return best is { } found ? (found, OutsideTownGoal(world, town, at)) : null;
    }

    /// <summary>
    /// The goal of a carrying out started again by a carrier already holding the body: outside the player's building or town it stands in, as the
    /// finders' goals; where it stands when it is in none, so the body is let go at once (engine choice: Meitou's home building is the one the
    /// carrier stands in).
    /// </summary>
    Vector2 CarryOutGoal(World world, int slot, ErrandKind kind)
    {
        var at = world.Characters.Next[slot].Position;
        var here = new Vector2(at.X, at.Z);
        if (kind == ErrandKind.OutOfTown) return PlayerTownAt(world, at) is { } town ? OutsideTownGoal(world, town, at) : here;
        return EnclosedAt(world, at) is { } site ? OutsideGoal(world, site, at, kind) : here;
    }

    /// <summary>
    /// The player's town (of the player's faction) a point lies in: the nearest by centre within its radius, or null.
    /// </summary>
    static PlayerTown? PlayerTownAt(World world, Vector3 at)
    {
        PlayerTown? best = null;
        float bestD = float.MaxValue;
        foreach (var town in world.Buildings.Towns)
        {
            float d = Gap(town.Position, at);
            if (town.Owner == world.Player.Faction && d <= town.Radius && d < bestD) (best, bestD) = (town, d);
        }

        return best;
    }

    /// <summary>
    /// A point lies in one of the player's complete buildings with doors (<see cref="EnclosedAt"/>): the context menu's Throw out of home.
    /// </summary>
    public bool InPlayerBuilding(World world, Vector3 at) => EnclosedAt(world, at) is not null;

    /// <summary>
    /// The player's complete building with doors whose footprint holds a point (the nearest by origin), or null.
    /// </summary>
    BuildSite? EnclosedAt(World world, Vector3 at)
    {
        BuildSite? best = null;
        float bestGap = float.MaxValue;
        var sites = world.Buildings.All;
        for (int i = 0; i < sites.Count; i++)   // indexed: asked per character in the publish, a boxed enumerator each time
        {
            var site = sites[i];
            float radius = Settings.Footprint?.Invoke(site.Record) ?? 0;
            if (!site.Complete || site.Owner != world.Player.Faction || radius <= 0) continue;
            float gap = Gap(site.Position, at);
            if (gap > radius || gap >= bestGap || !world.Fixtures.OfBuilding(site.Id).Any(f => f.Kind == FixtureKind.Door)) continue;
            (best, bestGap) = (site, gap);
        }

        return best;
    }

    /// <summary>
    /// Where an intruder is put down (<c>Task_TakeIntruderOutside</c>, <b>Verified</b>: <c>FUN_140335480</c>): the point outside the building's first
    /// door (lowest door index), A + (A - B) x 1.1 (<see cref="Ai.AiSystem.DoorExit"/>); a building without a door has the town's exit point
    /// (<see cref="OutsideTownGoal"/>, as the 274 variant does, which stands past the town's farthest building on the side of the carrier). The
    /// original does not search for walkable ground; Meitou pulls an unwalkable point in towards the door (engine safeguard).
    /// </summary>
    Vector2 OutsideGoal(World world, BuildSite site, Vector3 from, ErrandKind kind)
    {
        var town = world.Buildings.FindTown(site.Town);
        if (kind == ErrandKind.OutOfTown && town is not null) return OutsideTownGoal(world, town, from);
        Fixture? door = null;
        foreach (var f in world.Fixtures.OfBuilding(site.Id))
            if (f.Kind == FixtureKind.Door && world.Fixtures.DoorSpotOf(f) is not null && (door is null || f.DoorIndex < door.DoorIndex)) door = f;
        if (door is null || world.Fixtures.DoorSpotOf(door) is not { } leaf)
            return town is not null ? OutsideTownGoal(world, town, from) : new Vector2(site.Position.X, site.Position.Z);

        var centre = new Vector2(leaf.Centre.X, leaf.Centre.Z);
        var exit = AiSystem.DoorExit(centre, new Vector2(leaf.Normal.X, leaf.Normal.Z), new Vector2(site.Position.X, site.Position.Z));
        return world.Walkability.IsWalkable(exit.X, exit.Y) ? exit : Outward(world, centre, exit - centre, Vector2.Distance(exit, centre));
    }

    /// <summary>
    /// Past the town's farthest building by <see cref="OutsideTown"/>, on the side of <paramref name="from"/> (the carrier).
    /// </summary>
    Vector2 OutsideTownGoal(World world, PlayerTown town, Vector3 from)
    {
        var origin = new Vector2(town.Position.X, town.Position.Z);
        float far = 0;
        foreach (var other in world.Buildings.All)
            if (other.Town == town.Number) far = MathF.Max(far, Vector2.Distance(origin, new Vector2(other.Position.X, other.Position.Z)));
        return Outward(world, origin, new Vector2(from.X, from.Z) - origin, far + OutsideTown);
    }

    /// <summary>
    /// The point <paramref name="distance"/> from <paramref name="origin"/> along <paramref name="dir"/>, pulled in towards the origin until the
    /// ground is walkable.
    /// </summary>
    static Vector2 Outward(World world, Vector2 origin, Vector2 dir, float distance)
    {
        dir = dir.LengthSquared() > 1e-4f ? Vector2.Normalize(dir) : Vector2.UnitX;
        for (int tries = 0; tries < 4; tries++, distance *= 0.75f)
        {
            var p = origin + dir * distance;
            if (world.Walkability.IsWalkable(p.X, p.Y)) return p;
        }

        return origin + dir * distance;
    }

    #endregion
}