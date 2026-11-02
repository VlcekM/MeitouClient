using System.Numerics;
using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation.Carrying;
using Meitou.Simulation.Construction;
using Meitou.Simulation.Locks;

namespace Meitou.Simulation.Production;

// The carrying jobs of the lists (docs/game/ai-tasks.md "Perma-jobs" and "Finders of the jobs"): corpse disposal (137 with its finder 138) and the removal of intruders
// (223 / 273 with their companions 224 / 274). A pass every ErrandEveryTicks ticks lets a free character with the job lift what the job's finder names and walks it to where the companion
// task takes it; the lift and the put-down are the carry system's.
public sealed partial class ProductionSystem
{
    /// <summary>The carry system that lifts and puts down the bodies of the carrying jobs; null: they never run.</summary>
    public CarrySystem? Carry { get; set; }

    /// <summary>Ticks between the steps of a carrying job's carry under way (engine choice); its start is the decision step's.</summary>
    public const int ErrandEveryTicks = 15;
    /// <summary>A body carried out is put down within this of its goal, or after <see cref="ErrandWalks"/> walks that ended short (engine choices).</summary>
    public const float ErrandArrive = 40;
    public const int ErrandWalks = 4;
    /// <summary>Game minutes an intruder carrier may stand still before it drops the body (<b>Verified</b>: 5, <c>FUN_1403356e0</c>).</summary>
    public const float CarryOutMinutes = 5;
    /// <summary>How far past a town's farthest building an intruder is carried out of the town (<b>Observed</b>: 200 beyond the town's edge).</summary>
    public const float OutsideTown = 200;
    /// <summary>The finders look this far for a body or an intruder (the sensed list's reach, an engine stand-in as the rescue job's).</summary>
    const float ErrandSight = 1600;

    enum ErrandKind : byte { Corpse, OutOfBuilding, OutOfTown }

    /// <summary>One carrying job under way: what the character carries it for, the goal of its walk and how many walks ended short.</summary>
    sealed class Errand
    {
        public ErrandKind Kind;
        public Vector2 Goal;
        public int Tries;
        public CharacterId Body;
        /// <summary>The carrier's current GoTo is this job's own (else a GoTo task is an order of the player's).</summary>
        public bool Walking;
        /// <summary>Intruder carry-out: the game hours at the first step the carrier was not moving (cleared while it moves).</summary>
        public double? StoppedAt;
        /// <summary>The player's Corpse Disposal order started it: the delivery needs no job in the list.</summary>
        public bool Ordered;
        /// <summary>The corpse machine the carrier is taking the body to: claimed against its squad mates (the squad blackboard's goal list).</summary>
        public string? Site;
    }

    // Derived state, not saved or hashed: a lift under way is looked at again from the characters' own state when it is lost.
    readonly Dictionary<CharacterId, Errand> errands = [];
    // Bodies a carrier gave up on (walks that kept ending short): the original's give-up list of the squad.
    readonly HashSet<CharacterId> givenUp = [];

    /// <summary>The finders' kinds a job type runs, with the companion type that must be in the list for the carrying out.</summary>
    static ErrandKind? KindOf(TaskType type) => type switch
    {
        TaskType.FIND_A_DEAD_GUY => ErrandKind.Corpse,
        TaskType.PICKUP_INTRUDERS => ErrandKind.OutOfBuilding,
        TaskType.PICKUP_INTRUDERS_TOWN => ErrandKind.OutOfTown,
        _ => null,
    };

    /// <summary>The delivery job that must be in the list for the carrying out.</summary>
    static TaskType CompanionOf(ErrandKind kind) => kind switch
    {
        ErrandKind.Corpse => TaskType.DEAD_GUYS_GO_IN_THE_POT,
        ErrandKind.OutOfBuilding => TaskType.TAKE_INTRUDER_OUTSIDE,
        _ => TaskType.TAKE_INTRUDER_OUTSIDE_TOWN,
    };

    /// <summary>
    /// Whether the delivery can run (<b>Verified</b>: the lifts 138 / 223 / 273 do not look at the companion job nor at a machine; only the delivery jobs 137 / 224 / 274 do): the companion is in
    /// the list (or the player ordered the delivery) and, for a corpse, a machine exists. Otherwise the carrier keeps the body and the errand waits.
    /// </summary>
    bool DeliveryReady(World world, CharacterId self, CharacterCold cold, ErrandKind kind, Vector3 at, bool ordered = false) =>
        (ordered || PermaJobs.IndexOf(cold, CompanionOf(kind)) >= 0) && (kind != ErrandKind.Corpse || NearestIncinerator(world, at, self, cold.SquadId) is not null);

    /// <summary>The carrying errands under way (serial, slot order, every <see cref="ErrandEveryTicks"/> ticks): each is stepped. Their starts are the decision step's (<see cref="ErrandJobs"/>).</summary>
    void Errands(World world)
    {
        if (Carry is null || world.Tick % ErrandEveryTicks != 0 || errands.Count == 0) return;
        var table = world.Characters;
        foreach (var gone in errands.Keys.Where(k => !table.IsAlive(k)).ToList()) errands.Remove(gone);
        for (int i = 0; i < table.HighWater; i++)
            if (table.Next[i].Alive && table.Cold(i) is { IsPlayer: true } cold && errands.TryGetValue(table.IdOf(i), out var errand)) StepErrand(world, i, cold, errand);
    }

    /// <summary>
    /// The carrying jobs as the decision step's contributor (step 5): a character carrying nothing, able to carry and not fleeing, with a job whose finder finds something, lifts it (the lift
    /// does not need the delivery job nor a machine). It runs while its lift is under way and while its delivery can run (<see cref="DeliveryReady"/>).
    /// </summary>
    sealed class ErrandJobs(ProductionSystem owner) : IJobSource
    {
        (long Tick, int Slot, int K, CharacterId Body, Vector2 Goal) found = (-1, -1, -1, CharacterId.None, default);

        public DecisionCheck CanRun(World world, int slot, int k)
        {
            var table = world.Characters;
            var cold = table.Cold(slot)!;
            if (owner.Carry is null || KindOf(cold.Jobs[k].Type) is not { } kind) return DecisionCheck.No(null);
            if (owner.errands.TryGetValue(table.IdOf(slot), out var e))
            {
                bool mine = e.Kind == kind && (cold.Carrying == e.Body || table.Next[slot].Task == (byte)CharacterTask.PickUp && cold.CarryTarget == e.Body);
                if (!mine) return DecisionCheck.No("another errand");
                return cold.Carrying.IsNone || owner.DeliveryReady(world, table.IdOf(slot), cold, kind, table.Next[slot].Position, e.Ordered) ? DecisionCheck.Runs : DecisionCheck.No("nothing to deliver to");
            }
            if (!cold.Carrying.IsNone || !CarrySystem.CanCarry(world, cold, TaskType.DEAD_GUYS_GO_IN_THE_POT) || cold.Fleeing) return DecisionCheck.No("cannot carry");
            var lifted = Lifted(table, cold.SquadId);
            var body = kind == ErrandKind.Corpse ? owner.FindCorpse(world, slot, cold, lifted) : owner.FindIntruder(world, slot, cold, lifted, kind);
            if (body is not { } b) return DecisionCheck.No("nothing found");
            found = (world.Tick, slot, k, b.Id, b.Goal);
            return DecisionCheck.Run;
        }

        public void Start(World world, int slot, int k)
        {
            var table = world.Characters;
            var cold = table.Cold(slot)!;
            if (found.Tick != world.Tick || found.Slot != slot || found.K != k || KindOf(cold.Jobs[k].Type) is not { } kind) return;
            if (!owner.Carry!.BeginPickUp(world, slot, found.Body, source: TaskSource.Job, job: k, row: cold.Jobs[k].Type)) return;
            owner.errands[table.IdOf(slot)] = new Errand { Kind = kind, Goal = found.Goal, Body = found.Body };
        }
    }

    /// <summary>The Corpse Disposal order (<see cref="DisposeCorpseOrder"/>): a corpse errand for each player character that carries a dead body.</summary>
    void DisposeCarried(World world, DisposeCorpseOrder order)
    {
        var table = world.Characters;
        foreach (var id in order.Characters)
        {
            if (!table.TryResolveNext(id, out int i) || table.Cold(i) is not { IsPlayer: true } cold || cold.Carrying.IsNone) continue;
            if (!table.TryResolveNext(cold.Carrying, out int b) || table.Cold(b)?.Medical is not { Dead: true }) continue;
            errands[id] = new Errand { Kind = ErrandKind.Corpse, Body = cold.Carrying, Ordered = true };
        }
    }

    /// <summary>
    /// The bodies a finder of a character of squad <paramref name="squad"/> skips: every body carried (the carried byte, global), and the bodies its squad mates are lifting (their goal on the squad's
    /// blackboard, <c>FUN_1402690e0</c>, <b>Verified</b>); another squad may go for the same body until the lift attaches.
    /// </summary>
    static HashSet<CharacterId> Lifted(CharacterTable table, int squad)
    {
        var set = new HashSet<CharacterId>();
        for (int i = 0; i < table.HighWater; i++)
            if (table.Next[i].Alive && table.Cold(i) is { } c)
            {
                if (!c.Carrying.IsNone) set.Add(c.Carrying);
                if (table.Next[i].Task == (byte)CharacterTask.PickUp && !c.CarryTarget.IsNone && squad >= 0 && c.SquadId == squad) set.Add(c.CarryTarget);
            }
        return set;
    }

    // ---- stepping a carrying job ----

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
            errands.Remove(id);
            return;
        }
        // An order of the player took the carrier elsewhere: the job is over, the body stays with it.
        if (task == CharacterTask.GoTo && !e.Walking) { errands.Remove(id); return; }
        if (task is not (CharacterTask.GoTo or CharacterTask.Idle or CharacterTask.Wander or CharacterTask.Follow)) { errands.Remove(id); return; }
        bool wasWalking = e.Walking;
        if (task != CharacterTask.GoTo) e.Walking = false;
        Vector2 goal = e.Goal;
        BuildSite? site = null;
        Machine? machine = null;
        if (e.Kind != ErrandKind.Corpse && !DeliveryReady(world, id, cold, e.Kind, n.Position, e.Ordered)) return;
        if (e.Kind == ErrandKind.Corpse)
        {
            if (!DeliveryReady(world, id, cold, e.Kind, n.Position, e.Ordered)) return;   // the carrier keeps the body, the errand waits
            (site, machine) = NearestIncinerator(world, n.Position, id, cold.SquadId)!.Value;
            e.Site = site.Id;
            goal = GoalOf(world, site, machine);
        }
        bool arrived = site is not null ? AtSite(n, site, machine!) : Vector2.Distance(new Vector2(n.Position.X, n.Position.Z), goal) <= ErrandArrive;
        if (arrived)
        {
            if (e.Kind == ErrandKind.Corpse) Incinerate(world, i, cold, site!, machine!);
            else Carry!.BeginPutDown(world, i, TaskSource.Job);
            errands.Remove(id);
            return;
        }
        if (task == CharacterTask.GoTo)
        {
            e.StoppedAt = null;
            return;   // on its way
        }
        if (e.Kind != ErrandKind.Corpse && CarryOutOver(world, e, wasWalking))
        {
            Carry!.BeginPutDown(world, i, TaskSource.Job);
            errands.Remove(id);
            return;
        }
        if (++e.Tries > ErrandWalks)
        {
            if (e.Kind == ErrandKind.Corpse) GiveUp(world, i, e);
            else
            {
                Carry!.BeginPutDown(world, i, TaskSource.Job);
                errands.Remove(id);
            }
            return;
        }
        e.Goal = goal;
        e.Walking = true;
        TaskStart.StepTo(table, i, goal, cold.RunSpeed);
    }

    /// <summary>
    /// The update of <c>Task_TakeIntruderOutside</c> / <c>...OutsideTown</c> (slot 7 <c>FUN_1403356e0</c>, <b>Verified</b>), for a carrier that is not moving: it drops the body when its walk is over
    /// (here: the walk this job started has ended, short of the goal or not) or when 5 game minutes of clock have passed since it first stood still (the timer is zeroed while it moves).
    /// </summary>
    static bool CarryOutOver(World world, Errand e, bool wasWalking)
    {
        e.StoppedAt ??= world.GameHours;
        return wasWalking || world.GameHours - e.StoppedAt.Value >= CarryOutMinutes / 60.0;
    }

    /// <summary>The carrier drops the body where it stands and the job leaves it alone from now on (the squad's give-up list).</summary>
    void GiveUp(World world, int i, Errand e)
    {
        givenUp.Add(e.Body);
        Carry!.LetGo(world, i);
        errands.Remove(world.Characters.IdOf(i));
    }

    // ---- corpse disposal (137 / 138) ----

    /// <summary>
    /// Feeds the carried body into the machine (<c>Task_FeedCorpseIntoMachine</c> slot 3, <b>Verified</b> by decompile): the carrier lets go of it and the character is removed from the world
    /// ("incinerated or cooked"), its items with it. The original plays the lift clip back first and the sound <c>Character_Drop_Person</c>; Meitou removes it at once.
    /// The machine's functionality <c>string</c> floats up from 5 units over it (<c>FUN_1403567a0</c>).
    /// </summary>
    void Incinerate(World world, int i, CharacterCold cold, BuildSite site, Machine machine)
    {
        var table = world.Characters;
        var body = cold.Carrying;
        if (!Carry!.LetGo(world, i) || !table.IsAlive(body)) return;
        if (machine.Data.Label.Length > 0) world.Floaters?.Enqueue(Floater.Label(site.Position + new Vector3(0, 5, 0), machine.Data.Label));
        givenUp.Remove(body);
        world.Uniques.Left(body);
        table.Remove(body);
    }

    /// <summary>
    /// The finder of job 138 (<c>FUN_1408cfb00</c>, <b>Verified</b> by decompile): a dead, non-robot body in a cage of the character's town first (<c>FUN_1408cbea0</c> with the cage mask), else
    /// the nearest dead body in sight that nobody carries (<c>FUN_1408ce170</c>), neither in the give-up list. Meitou's cages are the player's own, the town is the haul range, and sight is
    /// <see cref="ErrandSight"/> (engine stand-ins). The goal is the machine (found again on each step).
    /// </summary>
    (CharacterId Id, Vector2 Goal)? FindCorpse(World world, int slot, CharacterCold cold, HashSet<CharacterId> lifted)
    {
        var table = world.Characters;
        var at = table.Next[slot].Position;
        CharacterId? best = null;
        float bestD = float.MaxValue;
        foreach (var f in world.Fixtures.All)
        {
            if (f.Kind != FixtureKind.Cage || f.Owner != world.Player.Faction || f.Occupant.IsNone || !table.TryResolveNext(f.Occupant, out int s)) continue;
            if (!Liftable(world, table, s, f.Occupant, lifted, cageStage: true)) continue;
            float d = Gap(at, f.Position);
            if (d <= Settings.HaulRange && d < bestD) (best, bestD) = (f.Occupant, d);
        }
        if (best is null)
        {
            for (int s = 0; s < table.HighWater; s++)
            {
                if (s == slot || !table.Next[s].Alive) continue;
                var id = table.IdOf(s);
                if (!Liftable(world, table, s, id, lifted, cageStage: false)) continue;
                float d = Gap(at, table.Next[s].Position);
                if (d <= ErrandSight && d < bestD) (best, bestD) = (id, d);
            }
        }
        return best is { } body ? (body, Vector2.Zero) : null;
    }

    /// <summary>
    /// A dead body that can be lifted, that nobody has and that is not in the give-up list. Robots are skipped only in the cage stage (<b>Verified</b>: <c>FUN_1408cfb00</c> tests the race's robot
    /// byte on the cage's occupant, the nearest-body finder <c>FUN_1408ce170</c> tests it only for an animal finder), so a human carrier lifts a dead robot lying outside.
    /// </summary>
    bool Liftable(World world, CharacterTable table, int s, CharacterId id, HashSet<CharacterId> lifted, bool cageStage) =>
        table.Cold(s) is { Medical.Dead: true } body && (!cageStage || body.Race is { IsRobot: false }) && !lifted.Contains(id) && !givenUp.Contains(id)
        && CarrySystem.CanBeLifted(world, body);

    /// <summary>The weight of a distance in the disposal finder: clamp(1 - d^2 / 4608^2, 1e-5, 1) (<c>FUN_1409b2380</c> with <c>DAT_14172e248</c> = 2.1233664e7, <b>Verified</b>).</summary>
    static float DisposalWeight(float distanceSquared) => Math.Clamp(1 - distanceSquared / 21233664f, 1e-5f, 1f);

    /// <summary>
    /// The corpse machine a body is taken to (<c>FUN_1408d0770</c> for function 11, <b>Verified</b> by decompile): among the complete corpse-disposal buildings of the player and of allied factions (<see cref="MayUseDisposal"/>) with an operator place free
    /// that lie within 1.1 x the radius of the carrier's town (<c>FUN_1408cbea0</c> / <c>FUN_140a00690</c>), the one of the greatest <see cref="DisposalWeight"/> of the 3D distance to the carrier (x100 for the player's own); the
    /// first listed wins a tie (the weights of everything beyond 4608 are equal). The town is the one the carrier stands in, else the nearest within 1.5625e7 squared (<c>DAT_14172dd88</c>); with
    /// none, every town is searched. Stand-ins: the town is a player's town, and the squad's home town (the Ownerships' +0x38, <b>Unknown</b> for the player) and the home-building x100 are not read.
    /// </summary>
    (BuildSite Site, Machine Machine)? NearestIncinerator(World world, Vector3 from, CharacterId self, int squad)
    {
        var claimed = MachinesClaimed(world, self, squad);
        var town = PlayerTownAt(world, from) ?? NearestPlayerTown(world, from, DisposalTownReach);
        BuildSite? bestSite = null;
        Machine? best = null;
        float bestWeight = -1;
        foreach (var m in world.Machines.All)
        {
            if (m.Data.Function != Meitou.Data.World.TownBuildings.CorpseDisposalFunction || Site(world, m.Site) is not { Complete: true } site || !MayUseDisposal(world, site)) continue;
            if (town is not null && Gap(site.Position, town.Position) > town.Radius * 1.1f) continue;
            if (OperatorsOf(world, site.Id) >= m.Data.MaxOperators || claimed.Contains(site.Id)) continue;
            float weight = DisposalWeight(Vector3.DistanceSquared(site.Position, from)) * (site.Owner == world.Player.Faction ? OwnFactionWeight : 1);
            if (weight <= bestWeight) continue;
            (bestSite, best, bestWeight) = (site, m, weight);
        }
        return best is null ? null : (bestSite!, best);
    }

    /// <summary>The machines another member of the squad is already taking a body to (<c>FUN_1402690e0</c> on the squad's goal list, <b>Verified</b>): not offered, even before it operates them.</summary>
    HashSet<string> MachinesClaimed(World world, CharacterId self, int squad)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (squad < 0) return set;
        foreach (var (id, e) in errands)
            if (e.Site is not null && id != self && world.Characters.TryResolveNext(id, out int i) && world.Characters.Cold(i)?.SquadId == squad) set.Add(e.Site);
        return set;
    }

    /// <summary>The weight factor of a building of the character's own faction (<c>DAT_141683250</c>, <b>Verified</b> 100).</summary>
    const float OwnFactionWeight = 100;

    /// <summary>
    /// Whether the player's character may use a corpse machine of a faction (the ally test <c>FUN_140621230</c>, <b>Verified</b> for a standalone building): its own faction's; else the
    /// building's faction is not hostile to the character's (relation of the building's faction at -30 or lower, <c>FUN_1406b26d0</c>) and is its ally (the same faction, relation 50 or more,
    /// <c>FUN_1406b2630</c>). Stand-ins: no designation record, the occupied town of the Ownerships (+0x80) and the nested-building branches are not read, nor the allied flag.
    /// </summary>
    bool MayUseDisposal(World world, BuildSite site)
    {
        int mine = world.Player.Faction;
        if (site.Owner == mine) return true;
        return Carry?.Relations is { } relations && relations.Contains(site.Owner) && !relations.IsHostile(site.Owner, mine) && relations.IsAlly(site.Owner, mine);
    }

    /// <summary>The squared distance within which a carrier outside every town still takes the nearest town (<c>DAT_14172dd88</c>, <b>Verified</b> 1.5625e7).</summary>
    const float DisposalTownReach = 15625000f;

    /// <summary>The player's town nearest to a point by centre, if its squared distance is within <paramref name="reachSquared"/>.</summary>
    static PlayerTown? NearestPlayerTown(World world, Vector3 at, float reachSquared)
    {
        PlayerTown? best = null;
        float bestD = reachSquared;
        foreach (var town in world.Buildings.Towns)
        {
            float d = Vector3.DistanceSquared(town.Position, at);
            if (town.Owner == world.Player.Faction && d <= bestD) (best, bestD) = (town, d);
        }
        return best;
    }

    // ---- intruders (223 / 273) ----

    /// <summary>
    /// The finders of jobs 223 (<c>FUN_14099c110</c>) and 273 (<c>FUN_14099c250</c>, whose own test is the carrier's town, docs/game/ai-tasks.md; the AI's: <see cref="Kidnap.TownDutySystem"/>) for a
    /// player character, <b>Verified</b> (<c>FUN_14099c110</c>: down by the character's isDown, not carried, special state 0, no race test, so a dead body or a non-carriable one counts and a refused lift leaves the finder returning it): a downed character in the building the carrier stands in, not carried,
    /// not caged (223: not in a bed either, special state 0). Engine choices: the building is the player's complete building with doors whose footprint holds the carrier (the original's
    /// "home" of 223 is the Ownerships' +0x38, whose value for the player faction is <b>Unknown</b>); an intruder is knocked out, alive, and neither of the carrier's faction nor an ally (the
    /// sensed list's exclude bits 0x201 are <b>Unknown</b>); 273 is a town finder (<see cref="FindTownIntruder"/>). The goal is outside the door (223 / 224,
    /// <c>Task_TakeIntruderOutside</c>) or outside the town (273 / 274, <c>Task_TakeIntruderOutsideTown</c>).
    /// </summary>
    (CharacterId Id, Vector2 Goal)? FindIntruder(World world, int slot, CharacterCold cold, HashSet<CharacterId> lifted, ErrandKind kind)
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
            if (lifted.Contains(id) || givenUp.Contains(id) || body.IsImprisoned || !body.CarriedBy.IsNone) continue;
            if (kind == ErrandKind.OutOfBuilding && body.InSomething != HeldState.None) continue;
            if (Carry!.Friendly(cold, body)) continue;
            if (EnclosedAt(world, table.Next[s].Position) != site) continue;
            float d = Gap(at, table.Next[s].Position);
            if (d <= ErrandSight && d < bestD) (best, bestD) = (id, d);
        }
        return best is { } found ? (found, OutsideGoal(world, site, at, kind)) : null;
    }

    /// <summary>
    /// The finder of job 273 (<c>FUN_14099c250</c>, <b>Observed</b>): the carrier stands in a town of the player's and a downed character, not carried or caged, stands in that same town inside its
    /// walls (<c>amInsideTownWalls</c>, <see cref="Kidnap.TownDutySystem.AmInsideTownWalls"/>; true without that system); no building is needed, so a body in a courtyard counts.
    /// </summary>
    (CharacterId Id, Vector2 Goal)? FindTownIntruder(World world, int slot, CharacterCold cold, HashSet<CharacterId> lifted)
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
            if (lifted.Contains(id) || givenUp.Contains(id) || body.IsImprisoned || !body.CarriedBy.IsNone || !CarrySystem.CanBeLifted(body)) continue;
            if (Carry!.Friendly(cold, body) || PlayerTownAt(world, table.Next[s].Position) != town) continue;
            if (walls is not null && !walls.AmInsideTownWalls(world, table.Next[s].Position)) continue;
            float d = Gap(at, table.Next[s].Position);
            if (d <= ErrandSight && d < bestD) (best, bestD) = (id, d);
        }
        return best is { } found ? (found, OutsideTownGoal(world, town, at)) : null;
    }

    /// <summary>The player's town (of the player's faction) a point lies in: the nearest by centre within its radius, or null.</summary>
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

    /// <summary>A point lies in one of the player's complete buildings with doors (<see cref="EnclosedAt"/>): the context menu's Throw out of home.</summary>
    public bool InPlayerBuilding(World world, Vector3 at) => EnclosedAt(world, at) is not null;

    /// <summary>The player's complete building with doors whose footprint holds a point (the nearest by origin), or null.</summary>
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
    /// Where an intruder is put down (<c>Task_TakeIntruderOutside</c>, <b>Verified</b>: <c>FUN_140335480</c>): the point outside the building's first door (lowest door index), A + (A - B) x 1.1
    /// (<see cref="Ai.AiSystem.DoorExit"/>); a building without a door has the town's exit point (<see cref="OutsideTownGoal"/>, as the 274 variant does, which stands past the town's farthest
    /// building on the side of the carrier). The original does not search for walkable ground; Meitou pulls an unwalkable point in towards the door (engine safeguard).
    /// </summary>
    Vector2 OutsideGoal(World world, BuildSite site, Vector3 from, ErrandKind kind)
    {
        var town = world.Buildings.FindTown(site.Town);
        if (kind == ErrandKind.OutOfTown && town is not null) return OutsideTownGoal(world, town, from);
        Locks.Fixture? door = null;
        foreach (var f in world.Fixtures.OfBuilding(site.Id))
            if (f.Kind == FixtureKind.Door && world.Fixtures.DoorSpotOf(f) is not null && (door is null || f.DoorIndex < door.DoorIndex)) door = f;
        if (door is null || world.Fixtures.DoorSpotOf(door) is not { } leaf)
            return town is not null ? OutsideTownGoal(world, town, from) : new Vector2(site.Position.X, site.Position.Z);
        var centre = new Vector2(leaf.Centre.X, leaf.Centre.Z);
        var exit = Ai.AiSystem.DoorExit(centre, new Vector2(leaf.Normal.X, leaf.Normal.Z), new Vector2(site.Position.X, site.Position.Z));
        return world.Walkability.IsWalkable(exit.X, exit.Y) ? exit : Outward(world, centre, exit - centre, Vector2.Distance(exit, centre));
    }

    /// <summary>Past the town's farthest building by <see cref="OutsideTown"/>, on the side of <paramref name="from"/> (the carrier).</summary>
    Vector2 OutsideTownGoal(World world, PlayerTown town, Vector3 from)
    {
        var origin = new Vector2(town.Position.X, town.Position.Z);
        float far = 0;
        foreach (var other in world.Buildings.All)
            if (other.Town == town.Number) far = MathF.Max(far, Vector2.Distance(origin, new Vector2(other.Position.X, other.Position.Z)));
        return Outward(world, origin, new Vector2(from.X, from.Z) - origin, far + OutsideTown);
    }

    /// <summary>The point <paramref name="distance"/> from <paramref name="origin"/> along <paramref name="dir"/>, pulled in towards the origin until the ground is walkable.</summary>
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
}
