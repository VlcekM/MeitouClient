using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Player;
using Meitou.Simulation.Population;

namespace Meitou.Simulation.Locks;

/// <summary>
/// The original's order 258 <c>USE_BED_ORDER</c> (verb "Sleep"; a quick right click on a bed, docs/game/ui-input.md "Building hover and click"): one
/// of the characters (the selection when the list is empty), the nearest that is awake and free, walks to <see cref="Bed"/> (a bed fixture id) and
/// lies in it until another order.
/// </summary>
public sealed record UseBedOrder(IReadOnlyList<CharacterId> Characters, string Bed) : OrderCommand;

/// <summary>
/// The original's order 99 <c>PUT_SOMEONE_IN_BED</c> (verb "Put in Bed"): each of the carriers (the selected ones carrying someone when the list is
/// empty) carries its body to <see cref="Bed"/>, or with none to the bed the bed search finds for it, and lays it in.
/// </summary>
public sealed record PutInBedOrder(IReadOnlyList<CharacterId> Carriers, string? Bed = null) : OrderCommand;

/// <summary>
/// A walk to a bed (<see cref="BedSystem"/>): <see cref="Character"/> to lie in <see cref="Bed"/> itself, or to lay <see cref="Body"/> it carries in
/// it.
/// </summary>
public sealed class BedTrip
{
    public required CharacterId Character { get; init; }
    public required string Bed { get; set; }
    /// <summary>
    /// The carried character to lay in the bed; <see cref="CharacterId.None"/>: the character lies in it itself.
    /// </summary>
    public CharacterId Body { get; init; } = CharacterId.None;
    /// <summary>
    /// The bed was given (an order on that bed): no other is looked for when it is taken.
    /// </summary>
    public bool Fixed { get; init; }
    /// <summary>
    /// Walks that ended short of the bed.
    /// </summary>
    public int Tries { get; set; }

    internal void Hash(ref StateHasher h)
    {
        h.Add(Character);
        h.Add(Rng.StableHash(Bed));
        h.Add(Body);
        h.Add(Fixed);
        h.Add(Tries);
    }
}

/// <summary>
/// The numbers of <see cref="BedSystem"/>; engine choices unless marked.
/// </summary>
public sealed class BedSettings
{
    /// <summary>
    /// A walker gets in (or lays its body in) within the bed's <c>use range</c> (15, <b>Verified</b> data) plus this, on the bed's floor.
    /// </summary>
    public float Reach { get; init; } = 15;
    /// <summary>
    /// A walk that ended (blocked by the bed's furniture, the path's end snapped beside it) still counts within this.
    /// </summary>
    public float Slack { get; init; } = 60;
    /// <summary>
    /// A bed counts as on the walker's floor within this height.
    /// </summary>
    public float FloorSlack { get; init; } = 20;
    /// <summary>
    /// Walks that end short before the bed is given up (and marked unreachable for the walker's faction).
    /// </summary>
    public int Walks { get; init; } = 3;
    /// <summary>
    /// A rented bed paid for this many game hours ago is free again for the payer's faction (<b>Observed</b>, <c>FUN_1403576c0</c>: 24).
    /// </summary>
    public double RentHours { get; init; } = 24;
}

/// <summary>
/// Beds in play (docs/simulation.md "Cages, beds and locks"; the original's rules in docs/game/locks.md "Beds"). Serial phases only.
/// <list type="bullet">
/// <item><b>Sleep</b> (<see cref="UseBedOrder"/>, and the "Sleep when injured" option through <see cref="StartSleep"/>): the walker's task is
/// <see cref="CharacterTask.Sleep"/>; in reach it pays
/// the bed's rent (<see cref="Pay"/>) and lies in it (<see cref="FixtureRegistry.EnterBed"/>). It lies there until its task changes (any order), then
/// gets up (<see cref="FixtureRegistry.LeaveBed"/>), as the original's 258 never completes and an order replaces it.</item>
/// <item><b>Put in bed</b> (<see cref="PutInBedOrder"/>, and the job 106 "Find and put in bed" for a carried friend): the carrier walks there, pays
/// (the cheaper of its own and the body's rent, <c>FUN_140358170</c>), lets go and the body lies in the bed (<c>FUN_140358670</c>).</item>
/// <item><b>Rent</b> (<see cref="Cost"/>): only the player's characters pay; refused for a bed that is not theirs to use.</item>
/// </list>
/// The player's own beds become bed fixtures when their buildings register (<see cref="FixtureRegistry.RegisterPlayerBuilding"/>, by the lock
/// system).
/// </summary>
public sealed class BedSystem(PopulationData data, BedSettings? settings = null) : ITickSystem, IStateHashed, ITaskOwner, ITaskHandler, IOrderOwner,
        IDecisionOwner
{
    public BedSettings Settings { get; } = settings ?? new BedSettings();
    /// <summary>
    /// Lets a carrier put its body down (null: no carrying, so no laying in).
    /// </summary>
    public Carrying.CarrySystem? Carry { get; set; }

    readonly List<BedTrip> _trips = [];
    // Bed id -> the game hour the player's faction last paid its rent (the original's global map is keyed by a name string of the argument and is
    // never saved, Verified by references;
    // Meitou saves it as `meitou rent` keys on the CAMERA, engine choice).
    readonly SortedDictionary<string, double> _paid = new(StringComparer.Ordinal);

    public IReadOnlyList<BedTrip> Trips => _trips;

    /// <summary>
    /// The beds whose rent was paid, with the game hour of the payment, in id order (a save keeps them: <see cref="Saving.BedSave"/>).
    /// </summary>
    public IReadOnlyCollection<KeyValuePair<string, double>> RentPaid => _paid;

    /// <summary>
    /// Sets the game hour a bed's rent was last paid (a loaded save does this).
    /// </summary>
    public void SetRentPaid(string bed, double hours) => _paid[bed] = hours;

    static IReadOnlyList<CharacterId> Who(World world, IReadOnlyList<CharacterId> ids) => ids.Count > 0 ? ids : world.Player.Selection.ToArray();

    public void Inputs(World world, IReadOnlyList<SimCommand> commands)
    {
        var table = world.Characters;
        foreach (var command in commands)
        {
            switch (command)
            {
                case UseBedOrder use:
                    // Order 258 goes to every selected character (FUN_1407f9e20 has no gate for it, Verified); the bed takes one, the later arrivals
                    // are refused there.
                    if (world.Fixtures.Find(use.Bed) is not { Kind: FixtureKind.Bed } bed) break;
                    foreach (var id in Who(world, use.Characters))
                        if (table.TryResolveNext(id, out int s)) PlayerOrders.Give(world, s, new OrderEntry(TaskType.UseBed, this) { Site = bed.Id },
                                use.Queued);
                    break;
                case PutInBedOrder put:
                    foreach (var id in Who(world, put.Carriers))
                        if (table.TryResolveNext(id, out int s) && table.Cold(s) is { Carrying.IsNone: false })
                            PlayerOrders.Give(world, s,
                                    new OrderEntry(TaskType.PutSomeoneInBed, this) { Site = put.Bed, Detail = put.Bed is not null ? Named : null },
                                            put.Queued);
                    break;
            }
        }
    }

    /// <summary>
    /// The bed a put-in order takes its body to: the one it named, else the nearest free one nobody else is on the way to.
    /// </summary>
    Fixture? BedFor(World world, int slot, OrderEntry order) =>
        order.Site is { } given ? world.Fixtures.Find(given)
                : world.Fixtures.FindBed(world, slot, data.Relations, f => Reserved(f, world.Characters.IdOf(slot)));

    /// <summary>
    /// Sleeping 98 needs the bed and a character that can walk and carries no one (one lying in it already needs nothing more); putting someone in
    /// bed a carrier that can walk and a bed ("no free bed nearby", posted for the player's character).
    /// </summary>
    OrderCheck IOrderOwner.CanRun(World world, int slot, OrderEntry order)
    {
        var c = world.Characters.Cold(slot)!;
        if (order.Row == TaskType.UseBed)
        {
            if (world.Fixtures.Find(order.Site!) is not { Kind: FixtureKind.Bed }) return OrderCheck.Gone;
            if (c.InSomething == HeldState.Bed && c.InWhat == order.Site) return OrderCheck.Run;   // lies in it already
            return CanWalk(c) && c.Carrying.IsNone ? OrderCheck.Run : OrderCheck.Blocked();
        }
        if (c.Carrying.IsNone) return OrderCheck.Gone;
        if (!CanWalk(c)) return OrderCheck.Blocked();
        return BedFor(world, slot, order) is { Kind: FixtureKind.Bed }
            ? OrderCheck.Run
            : OrderCheck.Blocked(c.IsPlayer ? $"{c.Name}: no free bed nearby" : null, posted: true);
    }

    void IOrderOwner.Start(World world, int slot, OrderEntry order)
    {
        var c = world.Characters.Cold(slot)!;
        if (order.Row == TaskType.UseBed)
        {
            if (c.InSomething != HeldState.Bed || c.InWhat != order.Site) TaskStart.Start(world, slot,
                    new TaskRequest(CharacterTask.Sleep, TaskType.UseBed, TaskSource.Order) { Site = order.Site });
            return;
        }
        var bed = BedFor(world, slot, order)!;
        TaskStart.Start(world, slot,
                new TaskRequest(CharacterTask.GoTo, TaskType.PutSomeoneInBed, TaskSource.Order) { Site = bed.Id, Detail = order.Detail });
    }

    /// <summary>
    /// The <see cref="TaskRequest.Detail"/> of a put-in order that named its bed (no other is looked for when it is taken).
    /// </summary>
    static readonly object Named = new();

    // Saved: a put-in order that named its bed is kind 1.
    int IOrderOwner.SaveKind(OrderEntry order) => order.Detail is null ? 0 : order.Detail == Named ? 1 : -1;
    object? IOrderOwner.LoadDetail(World world, CharacterId self, TaskType row, int kind, CharacterId target) => kind == 1 ? Named : null;

    public void RegisterTasks(TaskHandlers tasks)
    {
        tasks.Register(CharacterTask.Sleep, this);
        tasks.Register(TaskType.PutSomeoneInBed, this);
        tasks.Register(TaskType.FindBedAndPutIn, this);
    }

    /// <summary>
    /// Lying in the bed <see cref="TaskRequest.Site"/> (row 98, the player named it; the implicit 153, one the bed search found), or laying the
    /// carried body in it (99, 106: the walk acts it out).
    /// </summary>
    void ITaskHandler.Start(World world, int slot, in TaskRequest request)
    {
        var bed = world.Fixtures.Find(request.Site!)!;
        if (request.Task == CharacterTask.Sleep) StartSleep(world, slot, bed, fixedBed: request.Row == TaskType.UseBed);
        else StartPutIn(world, slot, bed, fixedBed: request.Detail == Named);
    }

    /// <summary>
    /// A walk to a bed that another task replaces or the Stop order ends is given up; a sleeper in its bed gets up in the slow world
    /// (<see cref="GetUp"/>), as the original's 258 never completes and an order replaces it. A trip that ends by itself is dropped by its own step.
    /// </summary>
    void ITaskHandler.End(World world, int slot, CharacterTask task, TaskEnd reason)
    {
        if (reason == TaskEnd.Done) return;
        var id = world.Characters.IdOf(slot);
        _trips.RemoveAll(t => t.Character == id);
    }

    /// <summary>
    /// A character that may walk to a bed: awake, not carried, not fleeing. A cage is no gate: rows 98, 99 and 106 do not register the imprisoned or
    /// chained repertoire (<see cref="TaskStart.Eligible"/>), and a caging ends the walk (<see cref="FixtureRegistry.Imprison"/>).
    /// </summary>
    static bool CanWalk(CharacterCold c) => c is { Medical.Incapacitated: false, Fleeing: false } && c.CarriedBy.IsNone;

    /// <summary>
    /// The bed search for the character in <paramref name="slot"/> (<see cref="FixtureRegistry.FindBed"/>), leaving out the beds others are on their
    /// way to.
    /// </summary>
    public Fixture? FindFor(World world, int slot)
    {
        var id = world.Characters.IdOf(slot);
        return world.Fixtures.FindBed(world, slot, data.Relations, f => Reserved(f, id));
    }

    /// <summary>
    /// Whether another character than <paramref name="except"/> is on its way to <paramref name="bed"/>.
    /// </summary>
    public bool Reserved(Fixture bed, CharacterId except)
    {
        foreach (var t in _trips)
            if (t.Bed == bed.Id && t.Character != except) return true;
        return false;
    }

    /// <summary>
    /// Sends a character to lie in <paramref name="bed"/> (task <see cref="CharacterTask.Sleep"/>): out of another bed first; in reach it gets in at
    /// once. <paramref name="fixedBed"/>: the player named the bed, so no other is looked for. The "Sleep when injured" job starts it too.
    /// </summary>
    void StartSleep(World world, int slot, Fixture bed, bool fixedBed)
    {
        var table = world.Characters;
        var id = table.IdOf(slot);
        var c = table.Cold(slot)!;
        _trips.RemoveAll(t => t.Character == id);
        if (c.InSomething == HeldState.Bed && c.InWhat != bed.Id) world.Fixtures.LeaveBed(world, id);
        if (c.InSomething == HeldState.Bed)
        {
            TaskStart.Step(table, slot, CharacterTask.Sleep);
            return;
        }
        var trip = new BedTrip { Character = id, Bed = bed.Id, Fixed = fixedBed };
        _trips.Add(trip);
        WalkTo(world, slot, bed);
        TaskStart.Step(table, slot, CharacterTask.Sleep);
        if (!Step(world, trip)) _trips.Remove(trip);
    }

    /// <summary>
    /// Sends a carrier to lay its body in <paramref name="bed"/>.
    /// </summary>
    void StartPutIn(World world, int slot, Fixture bed, bool fixedBed)
    {
        var table = world.Characters;
        var id = table.IdOf(slot);
        var c = table.Cold(slot)!;
        _trips.RemoveAll(t => t.Character == id);
        var trip = new BedTrip { Character = id, Bed = bed.Id, Body = c.Carrying, Fixed = fixedBed };
        _trips.Add(trip);
        WalkTo(world, slot, bed);
        if (!Step(world, trip)) _trips.Remove(trip);
    }

    /// <summary>
    /// A walk onto the bed's own floor (furniture cuts a hole in the floor's navmesh; a goal without a height resolves to the storey above or the
    /// ground below).
    /// </summary>
    void WalkTo(World world, int slot, Fixture bed)
    {
        var table = world.Characters;
        var c = table.Cold(slot)!;
        var at = world.Walkability.FloorNear(bed.Position.X, bed.Position.Z, bed.Position.Y, Settings.FloorSlack) is { } onFloor ? onFloor
                : bed.Position;
        var flat = new Vector2(at.X, at.Z);
        TaskStart.StepTo(table, slot, flat, c.RunSpeed);
        (c.GoalHeight, c.GoalHeightAt) = (at.Y, flat);
    }

    public void SlowWorld(World world)
    {
        GetUp(world);
        for (int k = 0; k < _trips.Count;)
        {
            if (Step(world, _trips[k])) k++;
            else _trips.RemoveAt(k);
        }
    }

    /// <summary>
    /// A character lying in a bed whose task is no longer <see cref="CharacterTask.Sleep"/> (an order replaced it) gets up.
    /// </summary>
    void GetUp(World world)
    {
        var table = world.Characters;
        for (int i = 0; i < table.HighWater; i++)
            if (table.Next[i].Alive && table.Next[i].Task != (byte)CharacterTask.Sleep && table.Cold(i) is { InSomething: HeldState.Bed } sleeper)
                world.Fixtures.GetOutOfBed(world, table.IdOf(i));
    }

    /// <summary>
    /// One tick of a trip; false when it is over (in bed, laid in, refused, given up, or another order).
    /// </summary>
    bool Step(World world, BedTrip t)
    {
        var table = world.Characters;
        if (!table.TryResolveNext(t.Character, out int s) || table.Cold(s) is not { } c || !CanWalk(c)) return false;
        bool carrying = !t.Body.IsNone;
        if (carrying ? c.Carrying != t.Body : c.InSomething != HeldState.None) return false;
        ref var n = ref table.Next[s];
        var bed = world.Fixtures.Find(t.Bed);
        if (bed is null || !carrying && !FixtureRegistry.IsFree(table, bed))
        {
            if (t.Fixed || carrying || world.Fixtures.FindBed(world, s, data.Relations, f => Reserved(f, t.Character)) is not { } other)
            {
                if (c.IsPlayer) world.Post(carrying ? $"{c.Name}: no free bed nearby" : $"{c.Name}: the bed is taken");
                if (!carrying) TaskStart.Stand(table, s);
                return false;
            }
            t.Bed = other.Id;
            WalkTo(world, s, other);
            if (!carrying) TaskStart.Step(table, s, CharacterTask.Sleep);
            return true;
        }
        var goal = new Vector2(bed.Position.X, bed.Position.Z);
        float d = Vector2.Distance(new Vector2(n.Position.X, n.Position.Z), goal);
        bool walking = ((MoveFlags)n.Flags & MoveFlags.AnyPath) != 0;
        bool onTask = carrying ? n.Task == (byte)CharacterTask.GoTo || !walking && n.Task == (byte)CharacterTask.Idle
                : n.Task == (byte)CharacterTask.Sleep;
        if (!onTask || walking && Vector2.Distance(n.Goal, goal) > Settings.Slack) return false;   // another order
        bool floor = MathF.Abs(n.Position.Y - bed.Position.Y) <= Settings.FloorSlack;
        if (floor && (d <= bed.UseRange + Settings.Reach || !walking && d <= Settings.Slack))
        {
            if (walking) TaskStart.Stand(table, s);
            return carrying ? LayIn(world, s, c, t.Body, bed) : LieIn(world, s, c, bed);
        }
        if (!walking)
        {
            if (++t.Tries > Settings.Walks)
            {
                world.Fixtures.MarkUnreachable(world, bed, c.Faction);
                FixtureRegistry.MarkBadNode(world, bed, n.Position);
                if (c.IsPlayer) world.Post($"{c.Name} cannot get to the bed");
                if (!carrying) TaskStart.Stand(table, s);
                return false;
            }
            WalkTo(world, s, bed);
            if (!carrying) TaskStart.Step(table, s, CharacterTask.Sleep);
        }
        return true;
    }

    /// <summary>
    /// The walker at its bed: the rent, then in (the original's <c>Task_UseBed</c> start <c>FUN_140357d00</c>). A bed whose machine draws power and
    /// gets none refuses (<c>FUN_140357ad0</c> with the power test <c>FUN_140299370</c>, <b>Verified</b>; only a bed the player built has a machine,
    /// and no base-game bed draws power): the task ends and the player hears "This bed needs power to operate.". False: the trip is over either way.
    /// </summary>
    bool LieIn(World world, int s, CharacterCold c, Fixture bed)
    {
        var table = world.Characters;
        if (world.Machines.Find(bed.Building) is { Data.IsConsumer: true, HasPower: false })
        {
            if (c.IsPlayer) world.Post(Loc.Tr("This bed needs power to operate."));
            TaskStart.Stand(table, s);
            return false;
        }

        if (!Pay(world, s, bed, Cost(world, bed, c.Faction)) || !world.Fixtures.EnterBed(world, table.IdOf(s), bed))
            TaskStart.Stand(table, s);
        return false;
    }

    /// <summary>
    /// The carrier at the bed: the rent (the cheaper of the carrier's and the body's, <c>FUN_140358170</c>), then the body is let go and lies in the
    /// bed (<c>FUN_140358670</c>). False: over.
    /// </summary>
    bool LayIn(World world, int s, CharacterCold c, CharacterId body, Fixture bed)
    {
        var table = world.Characters;
        if (!table.TryResolveNext(body, out int b) || table.Cold(b) is not { } bc) return false;
        if (!FixtureRegistry.IsFree(table, bed))
        {
            if (c.IsPlayer) world.Post($"{c.Name}: no free bed nearby");
            return false;
        }

        int cost = Cost(world, bed, c.Faction);
        if (cost != 0 && Cost(world, bed, bc.Faction) is var own and >= 0 && own < cost) cost = own;
        if (!Pay(world, s, bed, cost)) return false;
        Carry!.LetGo(world, s);
        if (!world.Fixtures.EnterBed(world, body, bed) && c.IsPlayer) world.Post($"{c.Name} could not put {bc.Name} in the bed");
        return false;
    }

    // The editor's BuildingDesignation BD_BAR. The original tests the house's own designation, which the first squad to take it as home sets from its
    // template.
    const int BarDesignation = 3;

    /// <summary>
    /// What a character of <paramref name="faction"/> pays to use a bed (the original's <c>FUN_140330ab0</c>, <b>Observed</b>): 0, in this order, for
    /// a bed of no faction, of a <c>not real</c> faction, of a faction that counts it as an ally (its own included) or of a <c>fundamental type</c> 8
    /// (bandit) faction, for a bed standing on its own, and for a bed in a Nest, Ruins or Point-of-interest town (the original takes the town nearest
    /// the building; here the placed town of the bed's site, and a bed without a site, which the player built, has no town test: engine choice); else
    /// the bed's house must be public for beds (its resident squad's <c>public beds</c>, or a bar: its <c>building designation</c> is BD_BAR), or -1
    /// (not to be used); then the squad's <c>bed usage cost</c>, x4 for a skeleton bed. Before the public-beds test the bed must pass
    /// <see cref="MayUse"/> (<c>canIUseThisBuilding</c>), else -1.
    /// </summary>
    public int Cost(World world, Fixture bed, int faction)
    {
        int owner = bed.Owner;
        if (owner < 0 || owner == faction || !bed.Indoors) return 0;
        if (owner < data.Factions.Count && data.Factions[owner].NotReal) return 0;
        if (data.Relations.Contains(owner) && data.Relations.Contains(faction) && data.Relations.IsAlly(owner, faction))
            return 0;
        if (owner < data.Factions.Count && data.Factions[owner].FundamentalType == FundamentalType.Bandit) return 0;
        if (bed.Site >= 0 && bed.Site < data.Sites.Count && data.Sites[bed.Site].Town.Type is TownType.Nest or TownType.Ruins
                or TownType.PointOfInterest)
            return 0;
        if (!MayUse(world, bed, faction)) return -1;

        // The building's record: the template of the last squad assigned to it and the designation it was given first (FUN_140330ab0 reads the house,
        // not its residents); a building with no record falls back to its residents (docs/game/locks.md).
        var record = world.Fixtures.RecordOf(bed.Building);
        string? templateId = record?.Template;
        if (templateId is null)
            foreach (var squad in world.Squads.All)
                if ((squad.Ai?.OwnHome ?? squad.HomeBuilding)?.Key == bed.Building)
                {
                    templateId = squad.TemplateId;
                    break;
                }
        if (templateId is null || data.Db.Find(templateId) is not { } template || !template.GetBool("public beds")
                && (record?.Designation ?? template.GetInt("building designation")) != BarDesignation)
            return -1;
        int cost = template.GetInt("bed usage cost");
        if (bed.Bed.Function == Data.World.TownBuildings.SkeletonBedFunction) cost *= 4;
        return Math.Max(cost, 0);
    }

    /// <summary>
    /// <c>Ownerships::canIUseThisBuilding</c> for an indoor bed of a faction that is not an ally (<c>FUN_140621230</c>, <b>Verified</b>,
    /// docs/game/locks.md): false when the bed's faction is hostile to <paramref name="faction"/>; else true only when its building is public (its
    /// <c>public day</c> flag; unknown counts as public) and <paramref name="faction"/> is real. The original's occupied-town and home-town clauses
    /// read the user's squad, which the rent does not have here (engine reading: not applied).
    /// </summary>
    bool MayUse(World world, Fixture bed, int faction)
    {
        int owner = bed.Owner;
        if (faction < 0) return true;
        if (data.Relations.Contains(owner) && data.Relations.Contains(faction) && data.Relations.IsHostile(faction, owner))
            return false;
        return world.Fixtures.PublicFlagOf(bed.Building) != false && !(faction < data.Factions.Count && data.Factions[faction].NotReal);
    }

    internal BuildingDetail? DetailOf(World world, string? instance)
    {
        // Town beds only: a bed the player built is a machine site (Site -1) and keeps the machine's panel.
        if (instance is null || world.Fixtures.Find(instance) is not { Kind: FixtureKind.Bed, Site: >= 0 } bed
                || data.Db.Find(bed.RecordId) is not { } record)
            return null;
        string? town = bed.Site >= 0 && bed.Site < data.Sites.Count ? data.Sites[bed.Site].Town.Name : null;
        float healing = Data.Gameplay.Construction.MachineData.From(data.Db, record)?.OutputValue ?? 1;
        return new BuildingDetail(instance, [], bed.Owner == world.Player.Faction)
        {
            Bed = new(record.Name, town, Cost(world, bed, world.Player.Faction), healing),
        };
    }

    /// <summary>
    /// The rent paid by the character in <paramref name="slot"/> (the original's <c>FUN_1403576c0</c>, <b>Observed</b>); only the player's characters
    /// pay. -1: "This isn't mine", refused. Above 0: free when the player's faction paid for this bed less than <see cref="BedSettings.RentHours"/>
    /// ago; else "Can't afford c.{1} for bed rental." and refused, or the money is paid with "Paid c.{1} for bed rental.". True when the bed may be
    /// used.
    /// </summary>
    public bool Pay(World world, int slot, Fixture bed, int cost)
    {
        var table = world.Characters;
        if (table.Cold(slot) is not { IsPlayer: true } || cost == 0) return true;
        if (cost < 0)
        {
            world.Bark(table.IdOf(slot), Loc.Tr("This isn't mine"));
            return false;
        }
        if (_paid.TryGetValue(bed.Id, out double when) && world.GameHours - when < Settings.RentHours) return true;
        if (world.Player.Money < cost)
        {
            world.Post(Loc.Tr("Can't afford c.{1} for bed rental.", cost));
            return false;
        }
        world.Player.Money -= cost;
        _paid[bed.Id] = world.GameHours;
        world.Post(Loc.Tr("Paid c.{1} for bed rental.", cost));
        return true;
    }

    /// <summary>
    /// The job 106 "Find and put in bed" (<c>FUN_1405a0d50</c>, docs/game/carry-loot.md 3.2) as the decision step's contributor (step 5): a player
    /// character carrying a body whose faction is not hostile to its own and that is no animal lays it in the bed the bed search finds (row 106
    /// registers the bed search <c>FUN_1408cfe00</c> as its finder, <b>Verified</b>). It runs while its walk does.
    /// </summary>
    public void RegisterSources(DecisionSystem decisions) => decisions.RegisterJob(TaskType.FindBedAndPutIn, new PutInJob(this));

    sealed class PutInJob(BedSystem owner) : IJobSource
    {
        public DecisionCheck CanRun(World world, int slot, int k)
        {
            var table = world.Characters;
            var c = table.Cold(slot)!;
            if (c.Running is { Source: TaskSource.Job, Row: TaskType.FindBedAndPutIn } run && run.Job == k
                    && run.Runs((CharacterTask)table.Next[slot].Task))
                return DecisionCheck.Runs;
            return owner.BedFor(world, slot) is null ? DecisionCheck.No("no body to put in a bed, or no bed") : DecisionCheck.Run;
        }

        public void Start(World world, int slot, int k)
        {
            if (owner.BedFor(world, slot) is { } bed)
                TaskStart.Start(world, slot,
                        new TaskRequest(CharacterTask.GoTo, TaskType.FindBedAndPutIn, TaskSource.Job) { Job = k, Site = bed.Id });
        }
    }

    /// <summary>
    /// The bed the put-in job takes the character's body to (a pure read): it carries a body that is no animal and not of a faction hostile to its
    /// own, and the bed search finds one.
    /// </summary>
    Fixture? BedFor(World world, int slot)
    {
        var table = world.Characters;
        if (Carry is null || table.Cold(slot) is not { Carrying.IsNone: false } c || !CanWalk(c)
                || _trips.Exists(t => t.Character == table.IdOf(slot)))
            return null;
        if (!table.TryResolveNext(c.Carrying, out int b) || table.Cold(b) is not { } bc || bc.Fighter is { IsAnimal: true })
            return null;
        if (data.Relations.Contains(bc.Faction) && data.Relations.Contains(c.Faction) && data.Relations.IsHostile(bc.Faction, c.Faction))
            return null;
        if (!WantsToPutIn(world, slot, c, b, bc)) return null;
        return world.Fixtures.FindBed(world, slot, data.Relations, f => Reserved(f, table.IdOf(slot)));
    }

    /// <summary>
    /// The rest of the desire of goal 106 (<c>FUN_1405a0d50</c>, <b>Verified</b>): the carrier regards the body as friendly, its squad's memory of
    /// the body holds no grudge tag, and a police-like carrier is owed no bounty by the body.
    /// </summary>
    static bool WantsToPutIn(World world, int slot, CharacterCold c, int body, CharacterCold bc)
    {
        if (world.SystemOf<Dialogue.DialogueSystem>() is { } dialogue)
        {
            if (!dialogue.RegardsFriendly(world, slot, body)) return false;
            if (dialogue.Memory.Find(world, c.SquadId, world.Characters.IdOf(body)) is { } known
                && ((known.Long & Dialogue.MemoryTag.GrudgeLongMask) != 0 || (known.Short & Dialogue.MemoryTag.GrudgeShortMask) != 0)) return false;
        }
        return !(world.SystemOf<Crime.CrimeSystem>()?.IsPoliceLike(world, c) == true && bc.Bounties?.AmountFor(c.Faction) > 0);
    }

    public void Hash(ref StateHasher hasher)
    {
        if (_trips.Count == 0 && _paid.Count == 0) return;
        hasher.Add(0x42656473);
        hasher.Add(_trips.Count);
        foreach (var t in _trips) t.Hash(ref hasher);
        hasher.Add(_paid.Count);
        foreach (var (bed, when) in _paid)
        {
            hasher.Add(Rng.StableHash(bed));
            hasher.Add(when);
        }
    }
}