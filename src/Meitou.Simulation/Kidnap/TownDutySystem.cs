using System.Numerics;
using Meitou.Data.Gameplay.Ai;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Simulation.Aggro;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Carrying;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Items;
using Meitou.Simulation.Locks;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Player;
using Meitou.Simulation.Population;
using Meitou.Simulation.Production;

namespace Meitou.Simulation.Kidnap;

/// <summary>
/// Settings of the <see cref="TownDutySystem"/>; engine choices unless a doc label says otherwise.
/// </summary>
public sealed record TownDutySettings
{
    /// <summary>
    /// Each member looks for intruders and food once in this many ticks, on its slot's turn.
    /// </summary>
    public int LookEveryTicks { get; init; } = 15;
    /// <summary>
    /// A member looks for an intruder within its sight (the sensed list's size is <b>Unknown</b>).
    /// </summary>
    public float Sight { get; init; } = AggroSystem.OutdoorSight;
    /// <summary>
    /// A walk counts as arrived within this of its goal; a walk that ended within <see cref="CageSlack"/> of it does too (a cage's reach is
    /// <see cref="FixtureRegistry.AtCage"/>).
    /// </summary>
    public float Arrive { get; init; } = 40;
    /// <summary>
    /// A walk that ended within this of its goal counts as arrived (see <see cref="Arrive"/>).
    /// </summary>
    public float CageSlack { get; init; } = 200;
    /// <summary>
    /// Walks that end short before the member gives up (an intruder is then put down where it is).
    /// </summary>
    public int Walks { get; init; } = 4;
    /// <summary>
    /// Game seconds a lift may spend walking to the body before the member gives it up for itself (<see cref="TownDutySystem.GiveUpLift"/>; no
    /// timer of the original's is known; a body it cannot reach would hold it forever).
    /// </summary>
    public float LiftSeconds { get; init; } = 60;
    /// <summary>
    /// Game hours a body put down short of its goal is left alone.
    /// </summary>
    public double RefuseHours { get; init; } = 2;
}

/// <summary>
/// What a town errand is: its rows, by <see cref="TownDutySystem.RowOf"/>.
/// </summary>
public enum TownDutyKind : byte
{
    /// <summary>
    /// 273 Remove Intruder: lifting an intruder.
    /// </summary>
    Lift,
    /// <summary>
    /// 274 Throwing out: carrying it outside the town.
    /// </summary>
    Out,
    /// <summary>
    /// 112 / 281: carrying it to a cage.
    /// </summary>
    Cage,
    /// <summary>
    /// 243 Getting food: fetching food from a store.
    /// </summary>
    Food,
    /// <summary>
    /// Looting the stolen goods off a caged prisoner.
    /// </summary>
    Confiscate,
    /// <summary>
    /// Looting food and stuff from a store.
    /// </summary>
    LootFood,
    /// <summary>
    /// Shooing strangers out of the member's building.
    /// </summary>
    Shoo,
    /// <summary>
    /// Running round the town looking for people.
    /// </summary>
    Ransack,
    /// <summary>
    /// Putting loot in storage.
    /// </summary>
    Stash,
    /// <summary>
    /// Getting rid of the resources it carries.
    /// </summary>
    Ditch,
    /// <summary>
    /// 223 Remove Home Intruder: lifting a home intruder.
    /// </summary>
    HomeLift,
    /// <summary>
    /// 224: carrying it past the home's first door.
    /// </summary>
    HomeOut,
    /// <summary>
    /// 138 Find corpses: lifting a dead body.
    /// </summary>
    CorpseLift,
    /// <summary>
    /// 137 Corpse disposal: carrying a dead body to a disposal and feeding it in (136).
    /// </summary>
    Corpse,
    /// <summary>
    /// 106 Find and put in bed: carrying a body to a bed and laying it in (the bed system's put-in walk, <see cref="BedSystem.PutIn"/>).
    /// </summary>
    Bed
}

/// <summary>
/// One member's errand of the <see cref="TownDutySystem"/>: an intruder it lifts and carries out or to a cage, or a store it fetches food from.
/// </summary>
public sealed class TownDuty
{
    /// <summary>
    /// The member running the errand.
    /// </summary>
    public required CharacterId Member { get; init; }
    /// <summary>
    /// The intruder (none for a food run).
    /// </summary>
    public CharacterId Body { get; init; } = CharacterId.None;
    /// <summary>
    /// The member's faction.
    /// </summary>
    public required int Faction { get; init; }
    /// <summary>
    /// The town of the errand.
    /// </summary>
    public required CampaignTown Town { get; init; }
    /// <summary>
    /// What the errand is.
    /// </summary>
    public TownDutyKind Kind { get; set; }
    /// <summary>
    /// Where the member walks.
    /// </summary>
    public Vector2 Goal { get; set; }
    /// <summary>
    /// The cage's fixture id, or the store's (a machine's site id, else a container's fixture id).
    /// </summary>
    public string? Target { get; set; }
    /// <summary>
    /// Walks that ended short so far (<see cref="TownDutySettings.Walks"/> give up).
    /// </summary>
    public int Tries { get; set; }
    /// <summary>
    /// A shoo's start (game hours) and its next event (game seconds of the errand); a confiscation's seconds at the cage; a lift's seconds walking to
    /// the body.
    /// </summary>
    public double Since { get; set; }
    /// <summary>
    /// A shoo's next event (game seconds of the errand), a confiscation's seconds at the cage or a lift's seconds walking to the body
    /// (see <see cref="Since"/>).
    /// </summary>
    public float Next { get; set; }
    /// <summary>
    /// A food run of a character of the player's side (243 from <c>Character::setupAI</c>): a player order ends it without being undone.
    /// </summary>
    public bool PlayerSide { get; init; }
    /// <summary>
    /// A corpse disposal errand's machine (its id is <see cref="Target"/>).
    /// </summary>
    public CorpseMachine? Machine { get; set; }

    /// <summary>
    /// The chooser's goal the errand acts out (<see cref="TownGoals"/>); <see cref="TaskType.None"/> for an errand no goal holds (a ditch the plan's
    /// step 93 asked for, a player-side food run), which holds its member with <see cref="CharacterCold.Engaged"/>.
    /// </summary>
    public TaskType Chosen { get; init; } = TaskType.None;

    /// <summary>
    /// The target the goal's plan carried at the start: its finder gives it again while the goal holds the member.
    /// </summary>
    internal PlanTarget Planned { get; init; }

    /// <summary>
    /// Adds the errand to the world's state hash.
    /// </summary>
    internal void Hash(ref StateHasher h)
    {
        h.Add(Member);
        h.Add(Body);
        h.Add(Faction);
        h.Add(Town.Site);
        h.Add(Town.PlayerTown);
        h.Add((int)Kind);
        h.Add(Goal);
        h.Add(Tries);
        h.Add(Target is { } t ? Rng.StableHash(t) : 0UL);
        if (Chosen != TaskType.None)
        {
            h.Add((int)Chosen);
            h.Add((int)Planned.Kind);
            h.Add(Planned.Position);
            h.Add(Planned.Character ?? CharacterId.None);
        }
        if (Kind == TownDutyKind.Shoo)
        {
            h.Add(Since);
            h.Add(Next);
        }

        if (PlayerSide) h.Add(0x50534964);
        if (Kind is TownDutyKind.Confiscate or TownDutyKind.Lift or TownDutyKind.HomeLift or TownDutyKind.CorpseLift) h.Add(Next);
    }
}

/// <summary>
/// The town duties (docs/simulation.md "Town duties", docs/game/carry-loot.md 3.2, ai-tasks.md "Finders of the jobs"): the act-outs of the town goals
/// the chooser weighs for NPCs with a mind of their own (<see cref="TownGoals"/>: the intruder and corpse lifts, the carried body's cage, throw-out,
/// bed and disposal goals, the store runs, the confiscation, the shoos and the ransack), the finders those goals' picks come from, the ditch the
/// plan's step 93 asks for and the player side's food runs. All serial, in <c>SlowWorld</c> after the carry system.
/// </summary>
public sealed class TownDutySystem(PopulationData data, PopulationSystem population, CarrySystem carry, TownDutySettings? settings = null)
        : ITickSystem, IStateHashed, IDecisionOwner, IDutyRelease, IDutyGoalOwner
{
    #region State and wiring

    /// <summary>
    /// The errands under way, in the order they were taken up.
    /// </summary>
    readonly List<TownDuty> _duties = [];

    /// <summary>
    /// The town goals' picks and goals (the chooser's side).
    /// </summary>
    TownGoals? _goals;

    /// <summary>
    /// The engine goals of the world (the AI's), resolved at the first step.
    /// </summary>
    DutyGoals? _goalDuties;

    /// <summary>
    /// The downed characters that may be lifted, as of <see cref="_countedTick"/> (<see cref="CountBodies"/>).
    /// </summary>
    readonly List<int> _downed = [];

    /// <summary>
    /// The bodies an errand, a refusal or a carrier holds, as of <see cref="_countedTick"/>: no lift finder offers them.
    /// </summary>
    readonly HashSet<CharacterId> _reserved = [];

    /// <summary>
    /// The tick the bodies were last counted (-1: never).
    /// </summary>
    long _countedTick = -1;

    /// <summary>
    /// Bodies put down short of their goal: left alone until this game hour, in the order they were put down.
    /// </summary>
    readonly List<(CharacterId Body, double Until)> _refused = [];

    /// <summary>
    /// Lifts given up on the walk (<see cref="TownDutySettings.LiftSeconds"/>): the member leaves that body alone until this game hour, while the
    /// others may still lift it.
    /// </summary>
    readonly List<(CharacterId Member, CharacterId Body, double Until)> _gaveUp = [];

    /// <summary>
    /// Player-side characters whose last food run found no room for the item: no new run until this game hour.
    /// </summary>
    readonly List<(CharacterId Member, double Until)> _foodWaits = [];

    /// <summary>
    /// The lock system (resolved in the serial step), for the ransack's door bashing and the town walls.
    /// </summary>
    LockSystem? _locks;

    /// <summary>
    /// The bed system (resolved in the serial step), for goal 106's desire and its walk to a bed.
    /// </summary>
    BedSystem? _beds;

    /// <summary>
    /// The settings (the defaults when none were given).
    /// </summary>
    public TownDutySettings Settings { get; } = settings ?? new TownDutySettings();

    /// <summary>
    /// The relation event of a caging (<see cref="FactionDeeds.Caged"/>, <c>FUN_140358670</c>); null: none.
    /// </summary>
    public FactionDeeds? Deeds { get; set; }

    /// <summary>
    /// The bounty claim after a caging (<see cref="Crime.ArrestSystem.Claim"/>, <c>FUN_140330d50</c>); it pays only a carrier of the player's
    /// faction. Null: none.
    /// </summary>
    public Crime.ArrestSystem? Arrests { get; set; }

    /// <summary>
    /// The errands under way.
    /// </summary>
    public IReadOnlyList<TownDuty> Duties => _duties;

    /// <summary>
    /// The population data.
    /// </summary>
    internal PopulationData PopulationData => data;

    /// <summary>
    /// The carry system.
    /// </summary>
    internal CarrySystem Carry => carry;

    /// <summary>
    /// The population system the world was made with.
    /// </summary>
    internal PopulationSystem Population => population;

    /// <summary>
    /// The town goals, made on first use.
    /// </summary>
    TownGoals Goals => _goals ??= new TownGoals(this);

    /// <summary>
    /// Registers the town goals (<see cref="TownGoals.Register"/>).
    /// </summary>
    void IDutyGoalOwner.RegisterGoals(DutyGoals goals) => Goals.Register(goals);

    /// <summary>
    /// The serial step: every errand steps on (an ended one is dropped, a goal's done), then the town goals' look, the player's food runs and the
    /// ditches asked for.
    /// </summary>
    public void SlowWorld(World world)
    {
        _goalDuties ??= world.SystemOf<AiSystem>()?.Duties;
        for (int k = 0; k < _duties.Count;)
        {
            var d = _duties[k];
            if (Step(world, d)) k++;
            else
            {
                _duties.RemoveAt(k);
                Finish(world, d);
            }
        }

        // Each member looks once in LookEveryTicks, on its slot's turn (slot mod the period): the looks spread over the ticks (docs/simulation.md
        // "Spread sweeps").
        int every = world.Every(Settings.LookEveryTicks);
        Goals.Look(world, every);
        LookPlayerFood(world, every);
        StartRequestedDitches(world);
    }

    /// <summary>
    /// An errand is over: a goal's is done (<see cref="DutyGoals.Done"/>), and its member's picks are looked at again for its next choice.
    /// </summary>
    void Finish(World world, TownDuty d)
    {
        if (d.Chosen == TaskType.None || !world.Characters.TryResolveNext(d.Member, out int g)) return;

        _goalDuties?.Done(world, g, d.Chosen);
        Goals.LookAgain(world, g);
    }

    /// <summary>
    /// A point on the ground plane (X, Z).
    /// </summary>
    static Vector2 Flat(Vector3 p) => new(p.X, p.Z);

    /// <summary>
    /// Hashes the errands, the refusals, the food waits, the lifts given up and the town goals' picks (only while there are any).
    /// </summary>
    public void Hash(ref StateHasher hasher)
    {
        _goals?.Hash(ref hasher);

        // only while one runs, so worlds without them hash as before
        if (_duties.Count == 0 && _refused.Count == 0 && _foodWaits.Count == 0 && _gaveUp.Count == 0) return;
        hasher.Add(0x546F776E);
        hasher.Add(_duties.Count);
        foreach (var d in _duties) d.Hash(ref hasher);
        hasher.Add(_refused.Count);
        foreach (var (body, until) in _refused)
        {
            hasher.Add(body);
            hasher.Add(until);
        }

        if (_foodWaits.Count > 0)
        {
            hasher.Add(_foodWaits.Count);
            foreach (var (member, until) in _foodWaits)
            {
                hasher.Add(member);
                hasher.Add(until);
            }
        }

        if (_gaveUp.Count == 0) return;

        hasher.Add(_gaveUp.Count);
        foreach (var (member, body, until) in _gaveUp)
        {
            hasher.Add(member);
            hasher.Add(body);
            hasher.Add(until);
        }
    }

    #endregion

    #region The player's food runs

    /// <summary>
    /// Whether a squad runs an occupation, as found on <see cref="_occupationsTick"/>.
    /// </summary>
    bool _occupations;

    /// <summary>
    /// The tick <see cref="_occupations"/> was found on (-1: never).
    /// </summary>
    long _occupationsTick = -1;

    /// <summary>
    /// Row 114's finder (<c>FUN_1405a70a0</c>, which is row 242's <c>FUN_1405a59a0</c>, <b>Verified</b>): the nearest storage in reach that takes one
    /// of the character's carried items (<see cref="Production.ProductionSystem.Ditchable"/>). Engine choice: only storages of its own faction or the
    /// town's, in the town it stands in (<see cref="StoreFor"/>; the finder's own owner test is <b>Unknown</b>). <paramref name="register"/> false is
    /// a read-only look (the planner's, on a worker).
    /// </summary>
    public (string Id, Vector2 Spot)? DitchStore(World world, Squad squad, CharacterCold cold, Vector3 at, bool register)
    {
        var town = AiSystem.TownAt(data, world, at);
        if (town.IsNone || cold.Inventory is not { } inv) return null;
        return StoreFor(world, squad, cold, at, town, [.. Production.ProductionSystem.Ditchable(inv)], register);
    }

    /// <summary>
    /// The plan's step 93 Filling machine at a storage (<see cref="AiMind.DitchRequest"/>, set by the think when the plan's first step walks to or
    /// fills the ditch target): a <see cref="TownDutyKind.Ditch"/> errand walks there and puts the carried items in; the AI plans again when it ends.
    /// </summary>
    void StartRequestedDitches(World world)
    {
        var table = world.Characters;
        for (int s = 0; s < table.HighWater; s++)
        {
            if (!table.Next[s].Alive || table.Cold(s) is not { Ai: { DitchRequest: true } mind } c) continue;
            mind.DitchRequest = false;
            var id = table.IdOf(s);
            if (c.Engaged || c.InCombat || c.Fleeing || mind.Duty is not null || Busy(id) || DecisionSystem.Ordered(c)) continue;
            var at = table.Next[s].Position;
            if (DitchStore(world, mind.Squad, c, at, register: true) is not var (storeId, spot))
            {
                mind.Replan = true;
                continue;
            }

            StartDitch(world, s, c, AiSystem.TownAt(data, world, at), storeId, spot);
        }
    }

    /// <summary>
    /// The speed mode of a food, loot, stash or ditch run's walk and its retries: a character of the player's faction keeps its own mode (the
    /// HUD's speed selector), as the original's move-task start changes the mover's mode only for a faction without the player flag
    /// (docs/game/pathfinding.md "Force speed"); anyone else walks.
    /// </summary>
    static byte ErrandMode(World world, CharacterCold cold) => cold.Faction == world.Player.Faction ? cold.RunSpeed : SpeedMode.Walk;

    /// <summary>
    /// Starts a ditch errand: the member walks to the store spot to empty its inventory there.
    /// </summary>
    void StartDitch(World world, int slot, CharacterCold cold, CampaignTown town, string storeId, Vector2 spot)
    {
        cold.Engaged = true;
        _duties.Add(new TownDuty { Member = world.Characters.IdOf(slot), Faction = cold.Faction, Town = town, Kind = TownDutyKind.Ditch,
                Target = storeId, Goal = spot });
        TaskStart.Walk(world, slot, spot, ErrandMode(world, cold), RowOf(TownDutyKind.Ditch), TaskSource.Goal);
    }

    /// <summary>
    /// 243 Getting food for the player's side (<c>Character::setupAI</c> gives it to every character of the player's faction; desire AI slots 26, 24,
    /// 25, <b>Verified</b> by decompile): an animal only while the squad option Feed Animals is on; only in a town (slot 24), and in one of another
    /// faction only while the player's faction is resident there (town slot 88: the town's owner or its <c>factionsResidentHere</c>, which takes the
    /// player's faction when a player character stands in a building of the player's in that town; Meitou: the player owns a house there,
    /// <see cref="FixtureRegistry.OwnsPropertyIn"/>). In a town with occupiers while it senses an enemy: inside the walls it wants food whatever its
    /// hunger (desire 0.2), outside them in a town with gates not at all; else the 259 desire (slot 25): stomach empty, hunger at most 2.4, no food
    /// it would bite on it, not fighting unless hunger is below 1.2. It walks to the nearest store of the town owned by its faction with food in it
    /// and takes one item, which the eating rule then eats; without room for that item (state 82, <see cref="TakeFits"/>) it starts no run. A player
    /// character's goal is the decision step's (<see cref="GettingFood"/>, priority 3, weight 0.5); this pass runs it for the other characters of
    /// the player's faction while idle (they hold no jobs; engine choice).
    /// </summary>
    void LookPlayerFood(World world, int every)
    {
        int faction = world.Player.Faction;
        if (faction < 0) return;
        _foodWaits.RemoveAll(w => w.Until <= world.GameHours);
        var table = world.Characters;
        for (int s = 0; s < table.HighWater; s++)
        {
            if (!table.Next[s].Alive || !world.Turn(s, every) || table.Cold(s) is not { IsPlayer: false } c || c.Faction != faction)
                continue;

            // An order beats the goal: one its order holds (an order in place runs on Idle) starts no run.
            var task = (CharacterTask)table.Next[s].Task;
            if (task != CharacterTask.Idle || DecisionSystem.Ordered(c)) continue;
            if (FoodWant(world, s, c, out var store, out _) > 0) StartFood(world, s, c, store);
        }
    }

    /// <summary>
    /// 243's desire for a player-side character, 0 with <paramref name="why"/> when it does not want food now, and the store it would walk to (a pure
    /// read).
    /// </summary>
    float FoodWant(World world, int s, CharacterCold c, out (CampaignTown Town, string Id, Vector2 Spot) store, out string? why)
    {
        store = default;
        why = WhyNotFed(world, s, c, out bool animal, out bool hungry, out bool occupied);
        if (why is not null) return 0;

        var table = world.Characters;
        int faction = world.Player.Faction;
        var race = c.Race!;
        var at = table.Next[s].Position;
        var town = AiSystem.TownAt(data, world, at);
        if (town == CampaignTown.None)
        {
            why = "not in a town";
            return 0;
        }

        int owner = town.Owner(world, data);
        if (owner != c.Faction && owner != faction && !world.Fixtures.OwnsPropertyIn(town.Site, faction))
        {
            why = "a town the player does not live in";
            return 0;
        }

        float desire = 1;
        if (occupied && AiSystem.Occupied(world, town) && SensesEnemy(world, s, c))
        {
            if (InsideWalls(world, town, at)) (hungry, desire) = (true, 0.2f);
            else if (HasGates(world, town))
            {
                why = "outside an occupied town's walls";
                return 0;
            }
        }

        if (!hungry)
        {
            why = "not hungry";
            return 0;
        }

        if (Store(world, at, town, owner: c.Faction, animal: animal, race: race, asker: c) is not var (storeId, spot))
        {
            why = "no store with food";
            return 0;
        }

        // 244 needs state 82, room for the food it takes: a full inventory starts no run, and the character keeps its job
        if (!TakeFits(world, storeId, c))
        {
            why = "no room for the food";
            return 0;
        }

        store = (town, storeId, spot);
        return desire;
    }

    /// <summary>
    /// The character's own gates of the food run (<see cref="FoodWant"/>): why it fetches no food (not the player's, Feed Animals off, neither
    /// hungry nor in an occupied town, busy, already carrying food or waiting for room), null when it may; and whether it is an animal, hungry, and
    /// an occupation is on.
    /// </summary>
    string? WhyNotFed(World world, int s, CharacterCold c, out bool animal, out bool hungry, out bool occupied)
    {
        animal = hungry = occupied = false;
        var table = world.Characters;
        int faction = world.Player.Faction;
        if (faction < 0 || c is not { Medical: { Dead: false, Incapacitated: false } med, Race: { } race } || !(c.IsPlayer || c.Faction == faction))
            return "not the player's";

        animal = !med.HasStatsObject;
        if (animal && (world.Player.SquadAi & SquadAi.Feed) == 0) return "Feed Animals is off";

        hungry = med.Fed <= 0 && med.Hunger <= FeedSystem.FoodOnGroundBelow && !(c.InCombat && med.Hunger >= FeedSystem.FoodOnGroundInFight);
        occupied = Occupations(world);
        if (!hungry && !occupied) return "not hungry";

        var id = table.IdOf(s);
        if (c.Engaged || c.Fleeing || c.PickingUpItem is not null || !c.CarriedBy.IsNone || c.InSomething != HeldState.None || Busy(id)) return "busy";

        if (FeedSystem.CarriesFood(c, race, animal) || _foodWaits.Exists(w => w.Member == id && w.Until > world.GameHours))
            return "has food, or no room for it";

        return null;
    }

    /// <summary>
    /// Whether any squad occupies a town (the occupied branch of 243's desire), once a tick.
    /// </summary>
    bool Occupations(World world)
    {
        if (_occupationsTick != world.Tick)
            (_occupations, _occupationsTick) = (world.Squads.All.Any(q => q.Ai is { CurrentClass: PackageClass.OccupyTown }), world.Tick);

        return _occupations;
    }

    /// <summary>
    /// Starts a food run of a player-side character to a store.
    /// </summary>
    void StartFood(World world, int s, CharacterCold c, (CampaignTown Town, string Id, Vector2 Spot) store)
    {
        c.Engaged = true;
        _duties.Add(new TownDuty { Member = world.Characters.IdOf(s), Faction = c.Faction, Town = store.Town, Kind = TownDutyKind.Food,
                Target = store.Id, Goal = store.Spot, PlayerSide = true });
        TaskStart.Walk(world, s, store.Spot, ErrandMode(world, c), RowOf(TownDutyKind.Food), TaskSource.Goal);
    }

    /// <summary>
    /// Registers 243 Getting food with the decision step.
    /// </summary>
    public void RegisterSources(DecisionSystem decisions) => decisions.Register(new GettingFood(this));

    /// <summary>
    /// 243 Getting food of a player character (priority 3, weight 0.5, <see cref="FoodWant"/>): it runs while its food run does.
    /// </summary>
    sealed class GettingFood(TownDutySystem owner) : IDecisionSource
    {
        /// <summary>
        /// The source's name in the decision log.
        /// </summary>
        public string Name => "243 Getting food";
        /// <summary>
        /// A goal.
        /// </summary>
        public DecisionStep Step => DecisionStep.Goals;
        /// <summary>
        /// Its row.
        /// </summary>
        public int Key => 243;
        /// <summary>
        /// Its weight.
        /// </summary>
        public float Weight => 0.5f;

        /// <summary>
        /// Runs while its food run is under way; else may run when the character wants food (<see cref="FoodWant"/>).
        /// </summary>
        public DecisionCheck CanRun(World world, int slot)
        {
            var id = world.Characters.IdOf(slot);
            if (owner.Busy(id))
                return owner._duties.Exists(d => d.Member == id && d.Kind == TownDutyKind.Food) ? DecisionCheck.Runs : DecisionCheck.No("busy");

            return owner.FoodWant(world, slot, world.Characters.Cold(slot)!, out _, out string? why) > 0 ? DecisionCheck.Run : DecisionCheck.No(why);
        }

        /// <summary>
        /// 1 while its food run is under way, else the food run's desire (<see cref="FoodWant"/>).
        /// </summary>
        public float Desire(World world, int slot) =>
            owner.Busy(world.Characters.IdOf(slot)) ? 1 : owner.FoodWant(world, slot, world.Characters.Cold(slot)!, out _, out _);

        /// <summary>
        /// Starts the food run to the store found.
        /// </summary>
        public void Start(World world, int slot)
        {
            var cold = world.Characters.Cold(slot)!;
            if (owner.FoodWant(world, slot, cold, out var store, out _) > 0) owner.StartFood(world, slot, cold, store);
        }
    }

    #endregion

    #region Finders

    /// <summary>
    /// The census of the lift finders, once a tick when one first needs it (<see cref="CountBodies"/>).
    /// </summary>
    void EnsureCounted(World world)
    {
        if (_countedTick == world.Tick) return;

        _countedTick = world.Tick;
        CountBodies(world);
    }

    /// <summary>
    /// The lift finders' census: the bodies an errand or a refusal holds or a carrier carries or goes to pick up are taken (not an intruder its
    /// lifter still walks to, <see cref="WalksToIntruder(CharacterTable, TownDuty)"/>), and the downed that may be lifted are listed.
    /// </summary>
    void CountBodies(World world)
    {
        var table = world.Characters;
        _downed.Clear();
        _reserved.Clear();

        // An intruder a member still walks to is nobody's yet: the original's finders (223, 273) leave out only a carried body, so every member
        // that senses it goes for it and the first in reach lifts it (Observed, original 1.0.65, #1024: three thralls converged on one body).
        foreach (var d in _duties)
            if (!d.Body.IsNone && !WalksToIntruder(table, d)) _reserved.Add(d.Body);

        _refused.RemoveAll(r => r.Until <= world.GameHours);
        _gaveUp.RemoveAll(r => r.Until <= world.GameHours);
        foreach (var r in _refused) _reserved.Add(r.Body);

        for (int s = 0; s < table.HighWater; s++)
        {
            if (!table.Next[s].Alive || table.Cold(s) is not { } c) continue;

            bool lifts = table.Next[s].Task == (byte)CharacterTask.PickUp && !c.CarryTarget.IsNone;
            if (lifts && !WalksToIntruder(table, s, c)) _reserved.Add(c.CarryTarget);
            if (!c.Carrying.IsNone) _reserved.Add(c.Carrying);

            if (c is { Medical: { Unconscious: true, Dead: false } } && c.InSomething != HeldState.Imprisoned && CarrySystem.CanBeLifted(c))
                _downed.Add(s);
        }
    }

    /// <summary>
    /// Whether <paramref name="id"/> runs an errand.
    /// </summary>
    bool Busy(CharacterId id)
    {
        foreach (var d in _duties)
            if (d.Member == id) return true;
        return false;
    }

    /// <summary>
    /// Whether errand <paramref name="d"/> is an intruder's lift whose member still walks to the body (its lift clip has not started).
    /// </summary>
    static bool WalksToIntruder(CharacterTable table, TownDuty d) =>
        d.Kind is TownDutyKind.Lift or TownDutyKind.HomeLift
        && table.TryResolveNext(d.Member, out int g)
        && table.Next[g].Task == (byte)CharacterTask.PickUp
        && table.Cold(g)!.LiftTime < 0;

    /// <summary>
    /// Whether the character in <paramref name="slot"/> still walks to an intruder it lifts for an errand
    /// (<see cref="WalksToIntruder(CharacterTable, TownDuty)"/>).
    /// </summary>
    bool WalksToIntruder(CharacterTable table, int slot, CharacterCold cold)
    {
        if (cold.LiftTime >= 0) return false;

        var id = table.IdOf(slot);
        foreach (var d in _duties)
            if (d.Member == id) return WalksToIntruder(table, d);
        return false;
    }

    /// <summary>
    /// Whether <paramref name="member"/> gave up walking to <paramref name="body"/> and still leaves it alone (<see cref="_gaveUp"/>).
    /// </summary>
    bool GaveUp(CharacterId member, CharacterId body)
    {
        foreach (var r in _gaveUp)
            if (r.Member == member && r.Body == body) return true;
        return false;
    }

    /// <summary>
    /// <c>Ownerships::isMyTown</c> (<c>FUN_1406211e0</c>, <b>Verified</b> by decompile): the squad's home town, the town its occupation holds
    /// (<c>Ownerships +0x80</c>, which the occupation's start declares), or a town of its faction.
    /// </summary>
    internal bool MyTown(World world, Squad squad, CampaignTown town)
    {
        if (town.Site >= 0)
            return town.Site == squad.Town || town.Site < data.SiteFactions.Count && data.SiteFactions[town.Site] == squad.Faction;

        if (squad.Ai is { CurrentClass: PackageClass.OccupyTown } board && board.Destination == town) return true;
        return world.Buildings.FindTown(town.PlayerTown) is { } t && t.Owner == squad.Faction;
    }

    /// <summary>
    /// The town test of 273's desire (AI slot 60 <c>FUN_140597f20</c>, <b>Verified</b> by decompile): in a current town (status <c>+0x88</c>) the
    /// desire is 0 when the town is no Town (vfunc <c>0x268</c>, null for a TownBase), has no gates (<c>Town::hasGates</c>, <see cref="HasGates"/>),
    /// or has its walls locked up (Town slot 96, <see cref="LockSystem.TownLockedUp"/>) and the member is of the player's faction; outside a town the
    /// lift desire stands. Engine reading: a placed town of another type than Town is a TownBase, and a player town a Town whose walls are not read.
    /// </summary>
    internal bool TownLets(World world, CharacterCold cold, CampaignTown town)
    {
        if (town.IsNone) return true;
        if (town.Site >= 0 && data.Sites[town.Site].Town.Type != Data.Gameplay.TownType.Town) return false;
        if (!HasGates(world, town)) return false;

        bool locked = town.Site >= 0 && LockSystem.TownLockedUp(world, data.Sites[town.Site]);
        return !(locked && cold.Faction == world.Player.Faction);
    }

    /// <summary>
    /// 223's finder (<c>FUN_14099c110</c>, docs/game/ai-tasks.md): the nearest knocked-out character the member senses
    /// (<see cref="CharacterFacts.Perceives"/>) within its sight inside the squad's home building, not carried, caged or in a bed (special state
    /// 0), not one another errand lifts or carries or the member gave up on (<see cref="GaveUp"/>), and not one the member regards as friendly (the
    /// sensed filter leaves out SENSE_ALLY; a dead one is tagged dead alone and fails it too). -1: none.
    /// </summary>
    internal int HomeIntruder(World world, int slot, CharacterCold cold, Meitou.Data.World.TownBuilding home)
    {
        EnsureCounted(world);
        var table = world.Characters;
        var at = Flat(table.Next[slot].Position);
        float range = AggroSystem.RangeOf(cold, Settings.Sight), best2 = range * range;
        int best = -1;
        foreach (int v in _downed)
        {
            var body = table.Cold(v)!;
            if (v == slot || body.InSomething != HeldState.None || _reserved.Contains(table.IdOf(v))) continue;
            if (world.Sight.BuildingOf(v) != home.Key || carry.Friendly(cold, body)) continue;
            if (!CharacterFacts.Perceives(world, slot, v) || GaveUp(table.IdOf(slot), table.IdOf(v))) continue;

            float d2 = Vector2.DistanceSquared(at, Flat(table.Next[v].Position));
            if (d2 <= best2) (best, best2) = (v, d2);
        }

        return best;
    }

    /// <summary>
    /// 273's finder (<c>FUN_14099c250</c>, <b>Verified</b> by decompile): the nearest downed intruder the member senses
    /// (<see cref="CharacterFacts.Perceives"/>, the finder walks the sensed list) within its sight in its town inside the walls
    /// (<see cref="InsideWalls"/>), not carried or caged, not of its faction or an ally (an engine reading of the sensed-list classes), not one it
    /// gave up on (<see cref="GaveUp"/>); -1: none. Meitou: one in a bed is left alone (the original skips only the caged, so guards threw patients
    /// out of rented beds, issue 751).
    /// </summary>
    internal int Intruder(World world, int slot, CharacterCold cold, CampaignTown town)
    {
        EnsureCounted(world);
        var table = world.Characters;
        var next = table.Next;
        var at = Flat(next[slot].Position);
        float range = AggroSystem.RangeOf(cold, Settings.Sight), best2 = range * range;
        int best = -1;
        foreach (int v in _downed)
        {
            var body = table.Cold(v)!;
            if (v == slot || body.InSomething == HeldState.Bed || _reserved.Contains(table.IdOf(v))) continue;
            if (carry.Friendly(cold, body) || !CharacterFacts.Perceives(world, slot, v)) continue;
            if (GaveUp(table.IdOf(slot), table.IdOf(v))) continue;

            var p = next[v].Position;
            float d2 = Vector2.DistanceSquared(at, Flat(p));
            if (d2 > best2 || AiSystem.TownAt(data, world, p) != town || !InsideWalls(world, town, p)) continue;
            (best, best2) = (v, d2);
        }

        return best;
    }

    /// <summary>
    /// 138's finder (<see cref="CorpseDisposal.FindCorpse"/>): a corpse among the member's town candidates within its sight, not one an errand, a
    /// refusal or a carrier holds; -1: none.
    /// </summary>
    internal int Corpse(World world, int slot, CharacterCold cold)
    {
        EnsureCounted(world);
        var at = world.Characters.Next[slot].Position;
        var inReach = world.Fixtures.TownCandidates(world, data, cold, at);
        float sight = AggroSystem.RangeOf(cold, Settings.Sight);
        var body = CorpseDisposal.FindCorpse(world, slot, f => inReach(f.Position), sight, id => _reserved.Contains(id) || Refused(id));
        return body.IsNone ? -1 : body.Slot;
    }

    /// <summary>
    /// 243's pick for an NPC: its desire (<see cref="NpcFoodDesire"/>), the nearest store with food of the target or current town
    /// (<see cref="Store"/>), room for the item the take moves (state 82, <see cref="FoodFits"/>) and a sensed ally fighting (state 12,
    /// <see cref="AiSystem.AlliesFighting"/>); null when it wants no food or finds no store.
    /// </summary>
    internal (string Id, Vector2 Spot, float Want, bool Room, bool Fighting)? FoodPick(World world, int slot, CharacterCold cold, Vector3 at,
        CampaignTown foodTown)
    {
        float want = NpcFoodDesire(world, slot, cold, at);
        if (want <= 0 || Store(world, at, foodTown, asker: cold) is not var (id, spot)) return null;

        bool fighting = world.SystemOf<AiSystem>()?.AlliesFighting(world, slot) == true;
        return (id, spot, want, FoodFits(world, id, cold), fighting);
    }

    /// <summary>
    /// 131's pick: the nearest store with food of the target or current town not of the looter's faction (<see cref="Store"/>), and room for a
    /// unit of its first record (state 82, the take's pick, <see cref="LootFirstFood"/>); null with none.
    /// </summary>
    internal (string Id, Vector2 Spot, float Want, bool Room, bool Fighting)? LootPick(World world, CharacterCold cold, Vector3 at,
        CampaignTown foodTown)
    {
        if (Store(world, at, foodTown, looter: cold.Faction, asker: cold) is not var (id, spot)) return null;

        bool room = StoreInventory(world, id)?.Items.FirstOrDefault() is { } first && cold.Inventory?.HasRoomFor(first) == true;
        return (id, spot, 1, room, false);
    }

    /// <summary>
    /// 243's desire for an NPC (AI slots 26, 24, 25, <b>Verified</b>, docs/game/character-stats.md "Food from storage"): 0 for an animal while Feed
    /// Animals is off, outside a town, in a town neither of its faction nor the player's faction's nor one where the player lives, and while it
    /// carries food; in a town with occupiers while it senses an enemy 0.2 inside the walls whatever its hunger, 0 outside them when the town has
    /// gates; else 259's desire: 1.0 with its stomach empty and its hunger at most 2.4 (below 1.2 in a fight), else 0.
    /// </summary>
    float NpcFoodDesire(World world, int slot, CharacterCold c, Vector3 at)
    {
        if (c is not { Medical: { Dead: false, Incapacitated: false } med, Race: { } race }) return 0;

        bool animal = !med.HasStatsObject;
        if (animal && (world.Player.SquadAi & SquadAi.Feed) == 0) return 0;
        if (FeedSystem.CarriesFood(c, race, animal)) return 0;

        var town = AiSystem.TownAt(data, world, at);
        if (town.IsNone) return 0;

        int owner = town.Owner(world, data), player = world.Player.Faction;
        if (owner != c.Faction && owner != player && !world.Fixtures.OwnsPropertyIn(town.Site, player)) return 0;

        if (AiSystem.Occupied(world, town) && SensesEnemy(world, slot, c))
        {
            if (InsideWalls(world, town, at)) return 0.2f;
            if (HasGates(world, town)) return 0;
        }

        bool hungry = med.Fed <= 0 && med.Hunger <= FeedSystem.FoodOnGroundBelow && !(c.InCombat && med.Hunger >= FeedSystem.FoodOnGroundInFight);
        return hungry ? 1 : 0;
    }

    #endregion

    #region Goal act-outs

    /// <summary>
    /// The errand of <paramref name="member"/>'s goal <paramref name="goal"/>, or null (the think reads it: only serial steps change the errands).
    /// </summary>
    internal TownDuty? ActOf(CharacterId member, TaskType goal)
    {
        foreach (var d in _duties)
            if (d.Member == member && d.Chosen == goal) return d;
        return null;
    }

    /// <summary>
    /// The first finder's target the plan carries (null: none).
    /// </summary>
    static PlanTarget? PlanTargetOf(IReadOnlyList<PlanStep> plan)
    {
        foreach (var step in plan)
            if (step.Target.Kind == PlanTarget.Found) return step.Target;
        return null;
    }

    /// <summary>
    /// The town the member in <paramref name="slot"/> stands in.
    /// </summary>
    CampaignTown TownOf(World world, int slot) => AiSystem.TownAt(data, world, world.Characters.Next[slot].Position);

    /// <summary>
    /// 273, 223 and 138's act-out (and 137's while it carries no corpse): the lift of the plan's body as a goal's task on the goal's row; false when
    /// the body is carried or caged already, another member's lift has reached it, or the lift does not start (<see cref="CarrySystem.BeginPickUp"/>).
    /// </summary>
    internal bool StartLift(World world, in DutyAsk ask, IReadOnlyList<PlanStep> plan, TownDutyKind kind)
    {
        var table = world.Characters;
        if (PlanTargetOf(plan) is not { Character: { } body } target || !table.TryResolveNext(body, out int b)) return false;
        if (table.Cold(b) is not { CarriedBy.IsNone: true, IsImprisoned: false } || LiftStarted(table, body)) return false;

        var cold = table.Cold(ask.Slot)!;
        if (!CarrySystem.CanCarry(world, cold, TaskType.PickupIntruders)) return false;
        if (!carry.BeginPickUp(world, ask.Slot, body, CharacterTask.Idle, source: TaskSource.Goal, row: ask.Type)) return false;

        _duties.Add(new TownDuty
        {
            Member = ask.Id, Body = body, Faction = cold.Faction, Town = TownOf(world, ask.Slot), Kind = kind, Chosen = ask.Type, Planned = target,
        });
        return true;
    }

    /// <summary>
    /// Whether another member's lift of <paramref name="body"/> is past its walk (its clip started).
    /// </summary>
    bool LiftStarted(CharacterTable table, CharacterId body)
    {
        foreach (var d in _duties)
            if (d.Body == body && d.Kind is TownDutyKind.Lift or TownDutyKind.HomeLift or TownDutyKind.CorpseLift && !WalksToIntruder(table, d))
                return true;
        return false;
    }

    /// <summary>
    /// 137's act-out: a carrier of a corpse walks it onto the floor of the disposal at the plan's place (the one its finder found,
    /// <paramref name="picked"/>); one carrying none lifts the plan's corpse (<see cref="StartLift"/>; the plan ends with the lift). False when the
    /// disposal is no longer the plan's or its operator places are taken.
    /// </summary>
    internal bool StartDisposal(World world, in DutyAsk ask, IReadOnlyList<PlanStep> plan, CorpseMachine? picked)
    {
        var cold = world.Characters.Cold(ask.Slot)!;
        if (cold.Carrying.IsNone) return StartLift(world, ask, plan, TownDutyKind.CorpseLift);
        if (PlanTargetOf(plan) is not { } target || picked is not { } machine || Flat(machine.Position) != Flat(target.Position)) return false;
        if (Operators(world, machine, null) >= MaxOperators(world, machine)) return false;

        _duties.Add(new TownDuty
        {
            Member = ask.Id, Body = cold.Carrying, Faction = cold.Faction, Town = TownOf(world, ask.Slot), Kind = TownDutyKind.Corpse,
            Target = machine.Id, Machine = machine, Goal = Flat(machine.Position), Chosen = ask.Type, Planned = target,
        });
        WalkToDisposal(world, ask.Slot, machine, ask.Type);
        return true;
    }

    /// <summary>
    /// 112 / 281's act-out: the carrier walks its body onto the floor of the plan's cage (<see cref="WalkToCage"/>); false when the cage is gone,
    /// taken, or another errand's.
    /// </summary>
    internal bool StartCage(World world, in DutyAsk ask, IReadOnlyList<PlanStep> plan)
    {
        var table = world.Characters;
        var cold = table.Cold(ask.Slot)!;
        if (cold.Carrying.IsNone || PlanTargetOf(plan) is not { Fixture: { } id } target || world.Fixtures.Find(id) is not { } cage) return false;
        if (!FixtureRegistry.IsFree(table, cage) || CageTaken(cage.Id, ask.Id)) return false;

        _duties.Add(new TownDuty
        {
            Member = ask.Id, Body = cold.Carrying, Faction = cold.Faction, Town = TownOf(world, ask.Slot), Kind = TownDutyKind.Cage, Target = cage.Id,
            Goal = Flat(cage.Position), Chosen = ask.Type, Planned = target,
        });
        WalkToCage(world, ask.Slot, cage, ask.Type);
        return true;
    }

    /// <summary>
    /// Whether an errand of another member than <paramref name="member"/> takes a body to the cage <paramref name="cage"/>.
    /// </summary>
    bool CageTaken(string cage, CharacterId member)
    {
        foreach (var d in _duties)
            if (d.Member != member && d.Target == cage) return true;
        return false;
    }

    /// <summary>
    /// 224 and 274's act-out (<c>Task_TakeIntruderOutside</c>, <b>Verified</b>, slots 2 / 5 / 7 <c>FUN_1403355e0</c> / <c>FUN_14033da80</c> /
    /// <c>FUN_1403356e0</c>): 224 walks the body past its home's first door (<see cref="HomeDoor"/>), 274 to its town's point outside the gates
    /// (<see cref="Outside"/>, the same for every carrier), at <see cref="CarryOutMode"/>; false with no such point (no home, no town).
    /// </summary>
    internal bool StartCarryOut(World world, in DutyAsk ask)
    {
        var cold = world.Characters.Cold(ask.Slot)!;
        if (cold.Carrying.IsNone) return false;

        var town = TownOf(world, ask.Slot);
        bool home = ask.Type == TaskType.TakeIntruderOutside;
        Vector2? goal = home ? HomeDoor(world, cold, town) : town.IsNone ? null : Outside(world, town);
        if (goal is not { } spot) return false;

        _duties.Add(new TownDuty
        {
            Member = ask.Id, Body = cold.Carrying, Faction = cold.Faction, Town = town, Kind = home ? TownDutyKind.HomeOut : TownDutyKind.Out,
            Goal = spot, Chosen = ask.Type,
        });
        TaskStart.Walk(world, ask.Slot, spot, CarryOutMode, ask.Type, TaskSource.Goal);
        return true;
    }

    /// <summary>
    /// Where 224 takes the body: the point past the home's first door (<see cref="AiSystem.LeavePoint"/>, <c>FUN_140296a40</c>); a home with no door
    /// has its town's exit point (<see cref="Outside"/>, <b>Verified</b> <c>FUN_140335480</c>). Null without a home, or with neither door nor town.
    /// </summary>
    Vector2? HomeDoor(World world, CharacterCold cold, CampaignTown town)
    {
        if (world.Squads.Find(cold.SquadId)?.HomeBuilding is not { } home) return null;
        if (AiSystem.LeavePoint(world, home) is { } door) return door;

        return town.IsNone ? null : Outside(world, town);
    }

    /// <summary>
    /// 106's act-out: the bed system walks the carrier to the bed its search finds and lays the body in (<see cref="BedSystem.PutIn"/>, row 106, a
    /// goal's task); false with no bed.
    /// </summary>
    internal bool StartBed(World world, in DutyAsk ask, IReadOnlyList<PlanStep> plan)
    {
        var cold = world.Characters.Cold(ask.Slot)!;
        if (cold.Carrying.IsNone || Beds(world)?.PutIn(world, ask.Slot) is not { } bed) return false;

        _duties.Add(new TownDuty
        {
            Member = ask.Id, Body = cold.Carrying, Faction = cold.Faction, Town = TownOf(world, ask.Slot), Kind = TownDutyKind.Bed, Target = bed.Id,
            Goal = Flat(bed.Position), Chosen = ask.Type, Planned = PlanTarget.OfFixture(bed.Id, bed.Position),
        });
        return true;
    }

    /// <summary>
    /// 243, 131 and 184's act-out: the walk to the plan's store (<paramref name="picked"/>, the one the finder found there), a food run at
    /// <see cref="ErrandMode"/>, a loot or stash run at a walk; false when the store is no longer the plan's.
    /// </summary>
    internal bool StartStoreRun(World world, in DutyAsk ask, IReadOnlyList<PlanStep> plan, TownDutyKind kind, (string Id, Vector3 At)? picked)
    {
        if (PlanTargetOf(plan) is not { } target || picked is not var (store, at) || Flat(at) != Flat(target.Position)) return false;

        var cold = world.Characters.Cold(ask.Slot)!;
        var spot = Flat(at);
        _duties.Add(new TownDuty
        {
            Member = ask.Id, Faction = cold.Faction, Town = TownOf(world, ask.Slot), Kind = kind, Target = store, Goal = spot, Chosen = ask.Type,
            Planned = target,
        });
        byte mode = kind == TownDutyKind.Food ? ErrandMode(world, cold) : SpeedMode.Walk;
        TaskStart.Walk(world, ask.Slot, spot, mode, ask.Type, TaskSource.Goal);
        return true;
    }

    /// <summary>
    /// 212's act-out: the walk to the cage of the plan's prisoner; false when it is no longer caged there or another errand works it.
    /// </summary>
    internal bool StartConfiscate(World world, in DutyAsk ask, IReadOnlyList<PlanStep> plan)
    {
        var table = world.Characters;
        if (PlanTargetOf(plan) is not { Character: { } prisoner } target || !table.TryResolveNext(prisoner, out int p) || HasDutyOn(prisoner))
            return false;
        if (table.Cold(p)?.InWhat is not { } cageId || world.Fixtures.Find(cageId) is not { Kind: FixtureKind.Cage } cage || cage.Occupant != prisoner)
            return false;

        var goal = Flat(cage.Position);
        _duties.Add(new TownDuty
        {
            Member = ask.Id, Body = prisoner, Faction = table.Cold(ask.Slot)!.Faction, Town = TownOf(world, ask.Slot), Kind = TownDutyKind.Confiscate,
            Target = cage.Id, Goal = goal, Chosen = ask.Type, Planned = target,
        });
        TaskStart.Walk(world, ask.Slot, goal, SpeedMode.Walk, ask.Type, TaskSource.Goal);
        return true;
    }

    /// <summary>
    /// 158 / 283's act-out: the shoo of the plan's stranger (<see cref="StepShoo"/> walks at it); false when it is gone or another errand shoos it.
    /// </summary>
    internal bool StartShoo(World world, in DutyAsk ask, IReadOnlyList<PlanStep> plan)
    {
        if (PlanTargetOf(plan) is not { Character: { } stranger } target || !world.Characters.IsAlive(stranger) || HasDutyOn(stranger)) return false;

        _duties.Add(new TownDuty
        {
            Member = ask.Id, Body = stranger, Faction = world.Characters.Cold(ask.Slot)!.Faction, Town = TownOf(world, ask.Slot),
            Kind = TownDutyKind.Shoo, Since = world.GameHours, Chosen = ask.Type, Planned = target,
        });
        return true;
    }

    /// <summary>
    /// 216's act-out (156 into the drawn building): the run to the plan's building, its door bashed first when its way in needs it
    /// (<see cref="LockSystem.BashForErrand"/>); false when the building is no longer the plan's.
    /// </summary>
    internal bool StartRansack(World world, in DutyAsk ask, IReadOnlyList<PlanStep> plan, (string Key, string? Door, Vector3 At)? picked)
    {
        if (PlanTargetOf(plan) is not { } target || picked is not var (key, door, at) || Flat(at) != Flat(target.Position)) return false;

        var cold = world.Characters.Cold(ask.Slot)!;
        _locks ??= world.Systems.OfType<LockSystem>().FirstOrDefault();
        if (door is not null) _locks?.BashForErrand(cold, door);

        var goal = Flat(at);
        _duties.Add(new TownDuty
        {
            Member = ask.Id, Faction = cold.Faction, Town = AiSystem.TownAt(data, world, at), Kind = TownDutyKind.Ransack, Target = key, Goal = goal,
            Chosen = ask.Type, Planned = target,
        });
        TaskStart.Walk(world, ask.Slot, goal, SpeedMode.Run, ask.Type, TaskSource.Goal);
        return true;
    }

    /// <summary>
    /// A town goal ended (serial, <see cref="IDutyGoal.End"/>): its errand goes. An order lets a carried body go where the carrier stands, as at a
    /// walk's end (engine choice, as the errands' release did); any other end leaves the body on the shoulder (the end of a goal does not unlink it,
    /// and the carrier's next choice takes it on). A ransacker's door bash stops.
    /// </summary>
    internal void EndGoal(World world, int slot, CharacterId id, DutyEnd why)
    {
        int k = _duties.FindIndex(d => d.Member == id && d.Chosen != TaskType.None);
        if (k < 0) return;

        var d = _duties[k];
        _duties.RemoveAt(k);
        if (why == DutyEnd.Gone || world.Characters.Cold(slot) is not { } cold) return;

        if (why == DutyEnd.Order && !d.Body.IsNone && cold.Carrying == d.Body) carry.LetGo(world, slot);
        if (d.Kind == TownDutyKind.Ransack && cold.BashErrand && !cold.BashOrdered && cold.DoorWay is null)
            (cold.Bashing, cold.BashErrand, cold.BashTime) = (null, false, 0);
    }

    #endregion

    #region Town walls and exits

    /// <summary>
    /// The gate test's cache of records, by id (<see cref="Data.World.TownBuildings.IsGate"/>); derived from the data.
    /// </summary>
    readonly Dictionary<string, Data.GameRecord?> _gateFunctions = new(StringComparer.Ordinal);

    /// <summary>
    /// The original's <c>amInsideTownWalls</c> &gt; 0 (docs/game/pathfinding.md "Gate codes"): the point's gate code is a walled region. Where the
    /// codes are not known (no navmesh labelled yet, off the mesh, a headless world) the town's built-up area stands in (<see cref="BuiltUp"/>).
    /// </summary>
    bool InsideWalls(World world, CampaignTown town, Vector3 p)
    {
        _locks ??= world.Systems.OfType<LockSystem>().FirstOrDefault();
        return _locks?.Regions?.CodeAt(p) is { } code and >= 0 ? code > 0 : BuiltUp(world, town, Flat(p));
    }

    /// <summary>
    /// <c>amInsideTownWalls</c> &gt; 0 at a point, whatever town it is in (the context menu's Throw out of town): the gate code where known, else the
    /// built-up area of the town the point is in (<see cref="InsideWalls"/>); outside every town without codes, no. The host asks between ticks.
    /// </summary>
    public bool AmInsideTownWalls(World world, Vector3 p)
    {
        var town = AiSystem.TownAt(data, world, p);
        _locks ??= world.Systems.OfType<LockSystem>().FirstOrDefault();
        return _locks?.Regions?.CodeAt(p) is { } code and >= 0 ? code > 0 : !town.IsNone && BuiltUp(world, town, Flat(p));
    }

    /// <summary>
    /// The original's <c>SensoryData::numEnemies</c> &gt; 0 (<see cref="BodySystem.Senses"/>).
    /// </summary>
    static bool SensesEnemy(World world, int slot, CharacterCold c) =>
        world.SystemOf<Combat.CombatSystem>() is { } combat && BodySystem.Senses(world, combat, slot, c).Enemies > 0;

    /// <summary>
    /// <c>Town::hasGates</c> (<see cref="TownExit.HasGates"/>).
    /// </summary>
    bool HasGates(World world, CampaignTown town) => TownExit.HasGates(world, data, town, _gateFunctions);

    /// <summary>
    /// The intruder finder's stand-in for <c>amInsideTownWalls</c> where the gate codes are unknown (<b>engine reading</b>): a placed town's radius,
    /// a player town's farthest building from its centre. A body put down at the exit point is normally outside it.
    /// </summary>
    bool BuiltUp(World world, CampaignTown town, Vector2 p) => Vector2.Distance(p, Centre(world, town)) <= Reach(world, town);

    /// <summary>
    /// The centre of a town (its site's, else its player town's).
    /// </summary>
    Vector2 Centre(World world, CampaignTown town) => TownExit.Centre(world, data, town);

    /// <summary>
    /// The reach of a town: a placed town's radius, a player town's farthest building from its centre.
    /// </summary>
    float Reach(World world, CampaignTown town)
    {
        if (town.Site >= 0) return data.Sites[town.Site].Town.Radius;
        var centre = Centre(world, town);
        float far = 0;
        foreach (var site in world.Buildings.All)
            if (site.Town == town.PlayerTown) far = MathF.Max(far, Vector2.Distance(centre, Flat(site.Position)));
        return far;
    }

    /// <summary>
    /// Where an intruder is put down: the town's point outside its gates (<see cref="TownExit"/>, <c>Town::getPositionOutsideTownGates(200)</c>,
    /// <b>Verified</b>), the same for every carrier. A player town's is found over its complete sites: a gate outside the walls, or a wall with an
    /// interior.
    /// </summary>
    Vector2 Outside(World world, CampaignTown town)
    {
        _locks ??= world.Systems.OfType<LockSystem>().FirstOrDefault();
        return TownExit.Of(world, data, _locks?.Regions, town, _gateFunctions);
    }

    #endregion

    #region Stores

    /// <summary>
    /// The storage facts of a container's record, by record id: function 2 or 13 (BF_RESOURCE_STORAGE, BF_GENERAL_STORAGE), its itemtype limit and
    /// its limit inventory list. Derived from the data.
    /// </summary>
    readonly Dictionary<string, (bool Storage, Data.Fcs.FcsRecordType Type, string[] Limit)> _storageRecords = new(StringComparer.Ordinal);

    /// <summary>
    /// Whether the character carries something edible.
    /// </summary>
    internal static bool HoldsFood(CharacterCold cold) =>
        cold.Inventory is { } inv && (inv.Items.Any(i => i.IsFood) || inv.Backpack?.Contents is { } bag && bag.Items.Any(i => i.IsFood));

    /// <summary>
    /// The nearest store of the town with food in it (<c>BuildingFinder_LootFoodAndStuff</c>, its filter only partly read: buildings with an
    /// inventory holding food): a complete player storage of the town (for a player-side run also one in a placed town's bought house), or a
    /// container that is not locked inside the town. Its id and where the
    /// member walks. With <paramref name="owner"/> (a player-side run) only stores of that faction; for an <paramref name="animal"/> raw food counts
    /// too and a store inside another building is skipped (the finder's animal tests).
    /// </summary>
    (string Id, Vector2 Spot)? Store(World world, Vector3 at, CampaignTown town, int looter = -1, int owner = -1, bool animal = false,
            RaceData? race = null, CharacterCold? asker = null)
    {
        var here = Flat(at);
        (string, Vector2)? best = null;
        float bestD = float.MaxValue;
        // A player-side run also takes the player's storages in a placed town, standing in a house the player owns there (#762).
        if (town.PlayerTown > 0 || town.IsNone || owner >= 0)
        {
            foreach (var m in world.Machines.All)
            {
                if (m.Storage is not { } s || !s.Items.Any(i => Edible(i, animal, race))
                        || world.Buildings.Find(m.Site) is not { Complete: true } site
                                || !InScope(world, town, at, SiteTown(world, site), site.Position))
                    continue;

                if (owner >= 0 && site.Owner != owner || animal && site.Parent is not null || asker?.IsUnreachable(m.Site, world.Tick) == true)
                    continue;

                float d = Vector2.DistanceSquared(here, Flat(site.Position));
                if (d < bestD) (best, bestD) = ((m.Site, Flat(site.Position)), d);
            }
        }

        foreach (var f in world.Fixtures.All)
        {
            if (looter >= 0 && f.Owner == looter || owner >= 0 && f.Owner != owner || asker?.IsUnreachable(f.Id, world.Tick) == true)
                continue;

            if (f.Contents is not { } c || f.Lock is { EffectivelyLocked: true } || !c.Items.Any(i => Edible(i, animal, race))
                    || !InScope(world, town, at, AiSystem.TownAt(data, world, f.Position), f.Position))
                continue;

            float d = Vector2.DistanceSquared(here, Flat(f.Position));
            if (d < bestD) (best, bestD) = ((f.Id, Flat(f.Position)), d);
        }

        return best;
    }

    /// <summary>
    /// Whether a store belongs to the food finder's search (<c>FUN_1408cb650</c> over <c>FUN_1409fc540</c>, <b>Verified</b>): in the given town, or,
    /// with no town, within the 3 x 3 zone cells round the looter (<paramref name="storeTown"/> is the store's town for a placed one and the player
    /// town's number for a player's site; a machine is in the town when it is that player town).
    /// </summary>
    static bool InScope(World world, CampaignTown town, Vector3 at, CampaignTown storeTown, Vector3 storePosition)
    {
        if (!town.IsNone) return storeTown == town;
        return Data.World.WorldLayout.WithinOneZone(at.X, at.Z, storePosition.X, storePosition.Z);
    }

    /// <summary>
    /// The town of a player site: its player town, else (furniture in a bought house) the placed town it stands in.
    /// </summary>
    CampaignTown SiteTown(World world, Construction.BuildSite site) =>
        site.Town > 0 ? CampaignTown.OfPlayer(site.Town) : AiSystem.TownAt(data, world, site.Position);

    /// <summary>
    /// Food the finder looks for (<c>FUN_14042ee50</c>): <c>item function</c> 3, and raw food (15) for an animal or when the race's special food
    /// lists it.
    /// </summary>
    static bool Edible(ItemInstance item, bool animal, RaceData? race = null) =>
        item.IsFood || item.ItemFunction == FeedSystem.RawFoodFunction && (animal || race?.SpecialFood.Contains(item.Record) == true);

    /// <summary>
    /// Whether the member has room for the item <see cref="StepFood"/> would take from the store (<see cref="Inventory.HasRoomFor"/>).
    /// </summary>
    bool FoodFits(World world, string storeId, CharacterCold cold) =>
        StoreInventory(world, storeId) is { } store && cold.Inventory is { } inv
                && store.Items.FirstOrDefault(i => Edible(i, false, cold.Race)) is { } item && inv.HasRoomFor(item);

    /// <summary>
    /// The inventory of a store: a machine's storage, else a fixture's contents.
    /// </summary>
    Inventory? StoreInventory(World world, string id) => world.Machines.Find(id)?.Storage ?? world.Fixtures.Find(id)?.Contents;

    /// <summary>
    /// What state 121 CARRYING_EXCESS_LOOT counts (<c>FUN_14059b7b0</c>): the items of the character's inventory and worn backpack that are stolen
    /// for it (<c>FUN_14079e550</c>, docs/game/carry-loot.md "Stolen ownership"); none for a lawless holder (the predicate's own exemption).
    /// </summary>
    public static IEnumerable<ItemInstance> LootOf(World world, CharacterCold c)
    {
        if (c.Inventory is not { } inv || world.SystemOf<Crime.CrimeSystem>()?.Factions.Lawless(c.Faction) == true)
            yield break;

        foreach (var item in inv.All())
            if (item.IsStolenFor(c.Faction, null)) yield return item;
    }

    /// <summary>
    /// State 121 CARRYING_EXCESS_LOOT (<see cref="LootOf"/>).
    /// </summary>
    public static bool CarriesLoot(World world, CharacterCold c) => LootOf(world, c).Any();

    /// <summary>
    /// Whether a fixture is a store, with the item type and the items it is limited to (cached by record).
    /// </summary>
    (bool Storage, Data.Fcs.FcsRecordType Type, string[] Limit) StorageOf(Fixture f)
    {
        if (_storageRecords.TryGetValue(f.RecordId, out var known)) return known;

        var record = data.Db.Find(f.RecordId);
        (bool, Data.Fcs.FcsRecordType, string[]) made = (false, Data.Fcs.FcsRecordType.Item, []);
        if (record is not null
                && Data.World.TownBuildings.FunctionOf(data.Db, record, _gateFunctions)?.GetInt("function")
                    is Data.World.TownBuildings.ResourceStorageFunction or Data.World.TownBuildings.GeneralStorageFunction)
        {
            made = (true, (Data.Fcs.FcsRecordType)record.GetInt("itemtype limit", (int)Data.Fcs.FcsRecordType.Item),
                    [.. Data.Gameplay.Construction.MachineData.Limits(data.Db, record).Select(r => r.TargetStringId)]);
        }

        return _storageRecords[f.RecordId] = made;
    }

    /// <summary>
    /// Whether a container's record takes <paramref name="item"/> at all: it is a store, the item lies loose in a carrier's grid, and its type and
    /// the store's list allow it.
    /// </summary>
    bool Accepts(Fixture f, ItemInstance item)
    {
        var (storage, type, limit) = StorageOf(f);
        if (!storage || f.Contents is null || item.Section is not ("main" or "backpack_content") || item.LockedOn || item.Contents is not null)
            return false;

        return item.Type == type && (limit.Length == 0 || Array.IndexOf(limit, item.Record) >= 0);
    }

    /// <summary>
    /// A container takes a loose stack (<c>StorageBuilding</c> slot 0x4a0, <c>FUN_1402988c0</c>, <b>Observed</b>): the item's type is the container's
    /// <c>itemtype limit</c> and, when it has a <c>limit inventory</c> list, the item is in it, and there is room (a stack of it with space, or a
    /// free cell). Same test as <see cref="Production.ProductionSystem.Stores"/> for a machine; a building shop's stock containers hold its goods, so
    /// what goes into one is for sale.
    /// </summary>
    bool Takes(Fixture f, ItemInstance item)
    {
        if (!Accepts(f, item)) return false;

        var box = f.Contents!;
        int room = box.LimitOf(item);
        foreach (var other in box.Items)
        {
            if (other.Record == item.Record && other.Quality == item.Quality && other.Mark == item.Mark && other.Section == "main"
                    && other.Quantity < room)
                return true;
        }

        return box.FindCell("main", item.Width, item.Height, out _, out _);
    }

    /// <summary>
    /// 184 Ditching loot's finder (<c>FUN_14059b040</c>): with stolen goods carried and a current town, the storage buildings (functions 2 and 13) of
    /// that town that take one of them, the nearest by squared 3D distance within <see cref="Production.ProductionSystem.JobRangeSq"/> (the default
    /// ROAM_FAR bound); its id (a player town's machine, else a container's fixture) and where the member walks. A member with a home town standing
    /// in a TOWN-type town reads the <i>home</i> town's storages (any owner); otherwise the current town's, owned by the member's faction or the
    /// town's (the nearest-allies finder's building test, <c>FUN_1408ca5d0</c> with <c>FUN_140621230</c>, <b>Observed</b>: a storage has an
    /// inventory, so only those two owners pass).
    /// </summary>
    internal (string Id, Vector2 Spot)? StashStore(World world, Squad squad, CharacterCold cold, Vector3 at, CampaignTown town)
            => StoreFor(world, squad, cold, at, town, [.. LootOf(world, cold)], register: true);

    /// <summary>
    /// The storage <see cref="StashStore"/>'s search finds for <paramref name="loot"/> (any carried items); <paramref name="register"/> false is a
    /// read-only look (the planner's finder, run on a worker).
    /// </summary>
    (string Id, Vector2 Spot)? StoreFor(World world, Squad squad, CharacterCold cold, Vector3 at, CampaignTown town, List<ItemInstance> loot,
            bool register)
    {
        if (loot.Count == 0) return null;
        bool listed = squad.Town >= 0 && town.Site >= 0 && data.Sites[town.Site].Town.Type == Data.Gameplay.TownType.Town;
        var search = listed ? CampaignTown.OfSite(squad.Town) : town;
        if (register && search.Site >= 0) world.Fixtures.RegisterSite(world, data, search.Site);
        int townOwner = search.Owner(world, data);
        bool Usable(int owner) => listed || owner == cold.Faction || townOwner < 0 || owner == townOwner;
        (string, Vector2)? best = null;
        float bestD = float.MaxValue;
        if (search.PlayerTown > 0)
        {
            foreach (var m in world.Machines.All)
            {
                if (!m.Data.IsStorage || m.Storage is null || world.Buildings.Find(m.Site) is not { Complete: true } site
                        || site.Town != search.PlayerTown || !Usable(site.Owner))
                    continue;

                float d = Vector3.DistanceSquared(at, site.Position);
                if (d <= Production.ProductionSystem.JobRangeSq && d < bestD && loot.Any(i => Production.ProductionSystem.Stores(m, i)))
                    (best, bestD) = ((m.Site, Flat(site.Position)), d);
            }
        }

        foreach (var f in world.Fixtures.All)
        {
            if (f.Contents is null || !Usable(f.Owner)) continue;
            float d = Vector3.DistanceSquared(at, f.Position);
            if (d > Production.ProductionSystem.JobRangeSq || d >= bestD || AiSystem.TownAt(data, world, f.Position) != search) continue;

            if (loot.Any(i => Takes(f, i))) (best, bestD) = ((f.Id, Flat(f.Position)), d);
        }

        return best;
    }

    #endregion

    #region Errands

    /// <summary>
    /// The rows of the errands no goal holds (the ditch, a player-side food run), which hold their member with <see cref="CharacterCold.Engaged"/>:
    /// a fight may interrupt their walk, the errand waits for the fight's end and walks on (<see cref="Ai.AiSystem"/>'s pre-emption).
    /// </summary>
    static readonly HashSet<TaskType> ErrandRows = [RowOf(TownDutyKind.Ditch), RowOf(TownDutyKind.Food)];

    /// <summary>
    /// The mode 224 and 274 carry a body out in: their start sets the mover of a character of no player faction to walk (<c>FUN_1403355e0</c>,
    /// <b>Verified</b>), and <see cref="MovementSystem.CarriesOutAtWalk"/> keeps it from the carrier's raise to a jog.
    /// </summary>
    const byte CarryOutMode = SpeedMode.Walk;

    /// <summary>
    /// Whether a walk's row is an errand's that no goal holds (<see cref="ErrandRows"/>): a fight may interrupt it.
    /// </summary>
    internal static bool IsErrandRow(TaskType row) => ErrandRows.Contains(row);

    /// <summary>
    /// The row an errand no goal holds walks on (<see cref="TaskStart.Walk(World, int, Vector2, byte, TaskType, TaskSource)"/>): 114's for a ditch,
    /// 243 for a player-side food run.
    /// </summary>
    static TaskType RowOf(TownDutyKind kind) => kind == TownDutyKind.Ditch ? TaskType.GetRidOfResourcesInMyInventory : TaskType.AquireFoodAtHomebase;

    /// <summary>
    /// The row an errand's walks act out: its goal's (the goal's row is its act-out's, <see cref="TaskSource.Goal"/>), else <see cref="RowOf"/>.
    /// </summary>
    static TaskType RowFor(TownDuty d) => d.Chosen != TaskType.None ? d.Chosen : RowOf(d.Kind);

    /// <summary>
    /// One tick of an errand; false when it is over. A goal's act-out goes on only while its goal holds the member (<see cref="DutyGoals.Runs"/>):
    /// a fight, an order on its body, an order or another goal ends the goal, whose end drops the errand (<see cref="EndGoal"/>).
    /// </summary>
    bool Step(World world, TownDuty d)
    {
        var table = world.Characters;
        if (!table.TryResolveNext(d.Member, out int g) || table.Cold(g) is not { Medical.Incapacitated: false } gc)
            return End(world, d);

        if (d.Chosen != TaskType.None)
        {
            if (_goalDuties?.Runs(world, g, d.Chosen) != true) return true;
        }
        else if (EngagedWaits(d, gc) is { } waits)
            return waits || End(world, d);

        if (d.Kind is TownDutyKind.Food or TownDutyKind.LootFood) return StepFood(world, d, g, gc);
        if (d.Kind is TownDutyKind.Stash or TownDutyKind.Ditch) return StepStash(world, d, g, gc);
        if (d.Kind == TownDutyKind.Confiscate) return StepConfiscate(world, d, g, gc);
        if (d.Kind == TownDutyKind.Shoo) return StepShoo(world, d, g, gc);
        if (d.Kind == TownDutyKind.Ransack) return StepRansack(world, d, g);

        return StepBody(world, d, g, gc);
    }

    /// <summary>
    /// An errand no goal holds over a fight or an order on its body: true while it waits for a fight to be over (a reaction, or what a shoo's
    /// dialogue turned into; its walks would end it, as a new task drops the fight task), false when it ends (a player character's food run is a
    /// goal its fight replaced, the decision step chooses again; a stumble or a get-up plans the AI anew); null when it steps on.
    /// </summary>
    static bool? EngagedWaits(TownDuty d, CharacterCold gc)
    {
        if (gc.InCombat) return !(d.PlayerSide && gc.IsPlayer);

        return PlayerOrders.BodyHolds(gc) ? false : null;
    }

    /// <summary>
    /// One tick of a body's errand: a lift (<see cref="StepLift"/>), else the carried body's walk (to a bed, a cage, a disposal, or out); over once
    /// the body is gone or caged, or the member no longer carries it (fought off, pushed away).
    /// </summary>
    bool StepBody(World world, TownDuty d, int g, CharacterCold gc)
    {
        var table = world.Characters;
        if (!table.TryResolveNext(d.Body, out int b) || table.Cold(b) is not { Medical: not null } body || body.IsImprisoned)
        {
            carry.LetGo(world, g);
            return End(world, d);
        }

        if (d.Kind is TownDutyKind.Lift or TownDutyKind.HomeLift or TownDutyKind.CorpseLift) return StepLift(world, d, g, gc);
        if (gc.Carrying != d.Body) return End(world, d);

        return d.Kind switch
        {
            TownDutyKind.Bed => Beds(world)?.PuttingIn(d.Member) == true,
            TownDutyKind.Cage => StepCage(world, d, g, b),
            TownDutyKind.Corpse => StepCorpse(world, d, g),
            _ => StepOut(world, d, g),
        };
    }

    /// <summary>
    /// One tick of a lift (273, 223, 138, 137's corpse): over once the pick-up is (the body on the shoulder, or fought off or pushed away: the
    /// carried body's goals take it on); a lift whose body no path reaches (<see cref="CarrySystem.LiftWalkFails"/>) or that has walked
    /// <see cref="TownDutySettings.LiftSeconds"/> is given up (<see cref="GiveUpLift"/>).
    /// </summary>
    bool StepLift(World world, TownDuty d, int g, CharacterCold gc)
    {
        if (world.Characters.Next[g].Task != (byte)CharacterTask.PickUp) return End(world, d);

        bool reaches = !carry.LiftWalkFails(world, g) && (d.Next += world.TickSeconds) <= Settings.LiftSeconds;
        return gc.LiftTime >= 0 || reaches || GiveUpLift(world, d, g);
    }

    /// <summary>
    /// One tick of 224 or 274's walk: at its point the body is let go (<see cref="LetGo"/>); walks that keep ending short let it go there and leave
    /// it alone for a while.
    /// </summary>
    bool StepOut(World world, TownDuty d, int g)
    {
        ref var gn = ref world.Characters.Next[g];
        bool walking = gn.Task == (byte)CharacterTask.GoTo;
        if (Vector2.Distance(Flat(gn.Position), d.Goal) <= Settings.Arrive) return LetGo(world, d, g, refuse: false);
        if (walking) return true;
        if (++d.Tries > Settings.Walks) return LetGo(world, d, g, refuse: true);

        TaskStart.Walk(world, g, d.Goal, CarryOutMode, RowFor(d), TaskSource.Goal);
        return true;
    }

    /// <summary>
    /// 184 and 114 at their storage (row 93 Filling machine, <c>FUN_140344510</c>, <b>Verified</b>: a target with no production component is a
    /// storage that takes the carried items): for 184 every stolen item the storage takes (engine choice: what list A <c>!121</c> asks for), for 114
    /// what the finder's collection lists (<see cref="Production.ProductionSystem.Ditchable"/>), whole stacks while there is room; what does not fit
    /// stays carried.
    /// </summary>
    bool StepStash(World world, TownDuty d, int g, CharacterCold gc)
    {
        var table = world.Characters;
        ref var gn = ref table.Next[g];
        var machine = d.Target is { } id ? world.Machines.Find(id) : null;
        var box = d.Target is { } fid && machine is null ? world.Fixtures.Find(fid) : null;
        var store = machine?.Storage ?? box?.Contents;
        if (store is null || gc.Inventory is not { } inv) return End(world, d);
        bool walking = gn.Task == (byte)CharacterTask.GoTo;
        float gap = Vector2.Distance(Flat(gn.Position), d.Goal);
        if (gap <= Settings.Arrive || !walking && gap <= Settings.CageSlack)
        {
            TaskStart.Stand(table, g);
            StashHere(world, d.Kind == TownDutyKind.Ditch, gc, inv, machine, box, store, gn.Position);
            return End(world, d);
        }

        if (walking) return true;
        if (++d.Tries > Settings.Walks)
        {
            // The walk failed for good: the store goes on the AI's give-up list (pathFailures, 20 s) the finders skip (FUN_1405b0290 in
            // FUN_1408dac20).
            if (d.Target is { } failed)
                gc.NoteUnreachable(failed, world.Tick + (long)MathF.Ceiling(Construction.ConstructionSystem.UnreachableSeconds / world.TickSeconds));

            return End(world, d);
        }

        TaskStart.Walk(world, g, d.Goal, ErrandMode(world, gc), RowFor(d), TaskSource.Goal);
        return true;
    }

    /// <summary>
    /// Row 93 at a storage for an obedient player slave's errand (<see cref="StashHere"/>, the ditch's collection): the carried items the storage
    /// <paramref name="storeId"/> takes go in. Serial phases only.
    /// </summary>
    internal void PutAway(World world, CharacterCold cold, string storeId, Vector3 at)
    {
        var machine = world.Machines.Find(storeId);
        var box = machine is null ? world.Fixtures.Find(storeId) : null;
        if ((machine?.Storage ?? box?.Contents) is { } store && cold.Inventory is { } inv) StashHere(world, true, cold, inv, machine, box, store, at);
    }

    /// <summary>
    /// The storage the ditch finder (<see cref="DitchStore"/>'s search, <see cref="StoreFor"/>) finds for <paramref name="items"/> in the town at
    /// <paramref name="at"/>: the nearest that takes one of them; null outside a town or with none. A read-only look.
    /// </summary>
    internal (string Id, Vector2 Spot)? StoreTaking(World world, Squad squad, CharacterCold cold, Vector3 at, IEnumerable<ItemInstance> items)
    {
        var town = AiSystem.TownAt(data, world, at);
        return town.IsNone ? null : StoreFor(world, squad, cold, at, town, [.. items], register: false);
    }

    /// <summary>
    /// The errand's member at its store puts the items in, whole stacks while there is room: for a ditch (<paramref name="ditch"/>) what the
    /// ditch's collection lists, else its loot. What goes into a building shop's stock container is for sale: the shop's stock is what the home's
    /// containers hold (docs/game/economy.md "Building shops"), and the ditch finder takes a shop's storages as any other.
    /// </summary>
    void StashHere(World world, bool ditch, CharacterCold gc, Inventory inv, Production.Machine? machine, Fixture? box, Inventory store, Vector3 at)
    {
        var carried = ditch ? Production.ProductionSystem.Ditchable(inv) : LootOf(world, gc);
        foreach (var item in carried.Where(i => machine is not null ? Production.ProductionSystem.Stores(machine, i) : Takes(box!, i)).ToList())
        {
            inv.Remove(item);
            if (store.Add(item)) continue;
            if (!inv.Add(item)) world.GroundItems.Drop(item, at, 0);
            break;
        }

        if (machine is not null) world.Machines.Touch();
    }

    /// <summary>
    /// One tick of the cage errand (112 / 281, then 108): the carrier walks onto the cage's floor (<see cref="WalkToCage"/>) and locks the body up
    /// there. A cage gone or taken, or walks that keep ending short (they mark the cage unreachable for its faction), end it with the body on the
    /// shoulder: the carrier's next choice takes it on. The goal's desire drop is the chooser's (another goal displaces it).
    /// </summary>
    bool StepCage(World world, TownDuty d, int g, int b)
    {
        var table = world.Characters;
        ref var gn = ref table.Next[g];
        var cage = d.Target is { } id ? world.Fixtures.Find(id) : null;
        if (cage is null || !FixtureRegistry.IsFree(table, cage)) return End(world, d);

        bool walking = gn.Task == (byte)CharacterTask.GoTo;
        if (FixtureRegistry.AtCage(cage, gn.Position, walking)) return LockUp(world, d, g, b, cage);
        if (walking) return true;
        if (++d.Tries > Settings.Walks)
        {
            world.Fixtures.MarkUnreachable(world, cage, d.Faction);
            return End(world, d);
        }

        WalkToCage(world, g, cage, RowFor(d));
        return true;
    }

    /// <summary>
    /// At the cage: the body goes in with the put-in's sentence; every put-in by this task makes the captive's squad remember the putter as captor,
    /// sends the relation event and then the bounty claim (<c>FUN_140358670</c>).
    /// </summary>
    bool LockUp(World world, TownDuty d, int g, int b, Fixture cage)
    {
        var table = world.Characters;
        TaskStart.Stand(table, g);
        carry.LetGo(world, g);
        if (!world.Fixtures.Imprison(world, d.Body, cage, data.Bodies.Constants.PrisonTime, d.Member))
            return End(world, d);

        Deeds?.Caged(world, g, b);
        Arrests?.Claim(world, g, d.Body, cage);
        if (table.Cold(b) is { IsPlayer: true } body) world.Post($"{body.Name} has been locked in a cage by {data.Factions[d.Faction].Name}");
        return End(world, d);
    }

    /// <summary>
    /// The walk to a cage, onto its floor (<see cref="MovementSystem.GoToFloor"/>): a flat goal would end under a cage on an upper storey, out of
    /// <see cref="FixtureRegistry.AtCage"/>'s height.
    /// </summary>
    static void WalkToCage(World world, int g, Fixture cage, TaskType row) =>
        TaskStart.Walk(world, g, cage.Position, SpeedMode.Free, row, TaskSource.Goal);

    /// <summary>
    /// 224 and 274 at the end of their walk: the body is let go where the carrier stands, without the put-down task and its knock-out (their update
    /// <c>FUN_1403356e0</c> calls the unlink <c>FUN_1405ce1e0</c>, <b>Verified</b>; <b>Observed</b>, original 1.0.65, #1024: a body that came round
    /// on the shoulder stayed conscious where it was dropped at the exit). <paramref name="refuse"/>: the walk ended short of its goal, and the body
    /// is left alone for <see cref="TownDutySettings.RefuseHours"/>.
    /// </summary>
    bool LetGo(World world, TownDuty d, int g, bool refuse)
    {
        carry.LetGo(world, g);
        if (refuse) _refused.Add((d.Body, world.GameHours + Settings.RefuseHours));

        return End(world, d);
    }

    /// <summary>
    /// The member gives up a lift whose body it cannot reach (no path gets there, <see cref="CarrySystem.LiftWalkFails"/>) or never reached
    /// (<see cref="TownDutySettings.LiftSeconds"/>): it leaves that body alone for <see cref="TownDutySettings.RefuseHours"/>, and the body stays
    /// free for the others.
    /// </summary>
    bool GiveUpLift(World world, TownDuty d, int g)
    {
        TaskStart.Stand(world.Characters, g);
        _gaveUp.Add((d.Member, d.Body, world.GameHours + Settings.RefuseHours));
        return End(world, d);
    }

    /// <summary>
    /// An order takes the member (<see cref="IDutyRelease"/>): each errand no goal holds that it runs ends (a goal's errand ends with its goal,
    /// <see cref="EndGoal"/>).
    /// </summary>
    void IDutyRelease.ReleaseForOrder(World world, int slot)
    {
        var id = world.Characters.IdOf(slot);
        for (int k = _duties.Count - 1; k >= 0; k--)
        {
            var d = _duties[k];
            if (d.Member != id || d.Chosen != TaskType.None) continue;

            End(world, d);
            _duties.RemoveAt(k);
        }
    }

    /// <summary>
    /// Ends an errand (false, for the step): the member of one no goal holds goes back to its routine (the AI takes it again); a goal's is done by
    /// <see cref="Finish"/>, which stops what is left of its act-out.
    /// </summary>
    static bool End(World world, TownDuty d)
    {
        if (d.Chosen != TaskType.None) return false;

        var table = world.Characters;
        if (table.TryResolveNext(d.Member, out int g) && table.Cold(g) is { } gc)
        {
            gc.Engaged = false;
            if (table.Next[g].Task == (byte)CharacterTask.GoTo) TaskStart.Stand(table, g);

            // the plan's step 93 is done: the planner asks again with the inventory as it is now
            if (d.Kind == TownDutyKind.Ditch && gc.Ai is { } mind) mind.Replan = true;
        }

        return false;
    }

    #endregion

    #region Corpse disposal

    /// <summary>
    /// The disposals the finder offers, gathered once per look period.
    /// </summary>
    CorpseMachineCache? _disposals;

    /// <summary>
    /// Whether a body was put down short of its goal and is left alone for now.
    /// </summary>
    bool Refused(CharacterId body)
    {
        foreach (var r in _refused)
            if (r.Body == body) return true;
        return false;
    }

    /// <summary>
    /// 137's finder (<c>FUN_1408d1090</c>, <b>Verified</b>: 1.0 when <see cref="CorpseDisposal.Find"/> finds a disposal, else 0): the disposal the
    /// member in slot <paramref name="g"/> takes a body to, leaving out one it cannot get to (its path-failure list, 20 s) and one whose operator
    /// places other errands hold (the squad blackboard's claims, <c>FUN_1402690e0</c>).
    /// </summary>
    internal CorpseMachine? DisposalFor(World world, int g, TownDuty? self = null)
    {
        var cold = world.Characters.Cold(g)!;
        var at = world.Characters.Next[g].Position;
        return CorpseDisposal.Find(world, data, Disposals(world), g, at,
            m => cold.IsUnreachable(m.Id, world.Tick) || Operators(world, m, self) >= MaxOperators(world, m));
    }

    /// <summary>
    /// The disposals there are (<see cref="CorpseMachineCache"/>).
    /// </summary>
    IReadOnlyList<CorpseMachine> Disposals(World world) => (_disposals ??= new CorpseMachineCache(Settings.LookEveryTicks)).Get(world, data);

    /// <summary>
    /// The errands other than <paramref name="self"/> taking a body to the disposal.
    /// </summary>
    int Operators(World world, CorpseMachine m, TownDuty? self)
    {
        int n = 0;
        foreach (var d in _duties)
            if (!ReferenceEquals(d, self) && d.Kind == TownDutyKind.Corpse && d.Target == m.Id) n++;
        return n;
    }

    /// <summary>
    /// The disposal's operator places (its BUILDING's <c>max operators</c>, 1 when unknown).
    /// </summary>
    static int MaxOperators(World world, CorpseMachine m) =>
        world.SystemOf<Production.ProductionSystem>()?.Data(m.RecordId)?.MaxOperators ?? 1;

    /// <summary>
    /// The walk to a disposal, onto its floor (one in a lab's upper storey), on the goal's row.
    /// </summary>
    static void WalkToDisposal(World world, int g, CorpseMachine machine, TaskType row) =>
        TaskStart.Walk(world, g, machine.Position, SpeedMode.Free, row, TaskSource.Goal);

    /// <summary>
    /// One tick of 137's walk (136 at the machine, its list B <c>isAtLocation</c>): at the disposal the body is fed in
    /// (<see cref="CorpseDisposal.Feed"/>); walks that keep ending short put the machine on the carrier's path-failure list for 20 s (the original's
    /// ignore map, <c>FUN_1405b0290</c>) and the carrier keeps the body for its next choice.
    /// </summary>
    bool StepCorpse(World world, TownDuty d, int g)
    {
        var table = world.Characters;
        ref var gn = ref table.Next[g];
        if (d.Machine is not { } machine) return End(world, d);

        bool walking = gn.Task == (byte)CharacterTask.GoTo;
        if (AtDisposal(gn.Position, machine.Position, walking))
        {
            TaskStart.Stand(table, g);
            string label = world.SystemOf<Production.ProductionSystem>()?.Data(machine.RecordId)?.Label ?? "";
            CorpseDisposal.Feed(world, carry, g, machine.Position, label);
            return End(world, d);
        }

        if (walking) return true;
        if (++d.Tries > Settings.Walks)
        {
            long until = world.Tick + (long)MathF.Ceiling(Construction.ConstructionSystem.UnreachableSeconds / world.TickSeconds);
            table.Cold(g)!.NoteUnreachable(machine.Id, until);
            return End(world, d);
        }

        WalkToDisposal(world, g, machine, RowFor(d));
        return true;
    }

    /// <summary>
    /// Whether a carrier at <paramref name="at"/> is at the disposal: within <see cref="TownDutySettings.Arrive"/> on the ground, or within
    /// <see cref="TownDutySettings.CageSlack"/> once its walk has ended, on the machine's storey (<see cref="FixtureRegistry.WorkHeight"/>).
    /// </summary>
    bool AtDisposal(Vector3 at, Vector3 machine, bool walking)
    {
        float gap = Vector2.Distance(Flat(at), Flat(machine));
        return (gap <= Settings.Arrive || !walking && gap <= Settings.CageSlack) && MathF.Abs(at.Y - machine.Y) < FixtureRegistry.WorkHeight;
    }

    #endregion

    #region Cages

    /// <summary>
    /// The desire for a carried body outside any town (<c>DAT_14167fad0</c>, <b>Verified</b>).
    /// </summary>
    public const float OutsideTownDesire = 0.1f;

    /// <summary>
    /// The desire of a law enforcer for a carried slave it holds no bounty on (<c>DAT_14169bb48</c>), of a dead body (<c>DAT_14167e8d8</c>) and of a
    /// living one (<c>DAT_141682ad0</c>).
    /// </summary>
    public const float PoliceSlaveDesire = 1.4f, DeadDesire = 0.5f, AliveDesire = 2;

    /// <summary>
    /// The desire of goals 112 and 281 for the body the carrier in slot <paramref name="carrier"/> holds (<c>AI::scorePutCarriedDudeInCage</c>
    /// <c>FUN_1405a1140</c> with <c>FUN_1405a0f10</c>, <b>Verified</b>), tests in the original's order: the carrier stands in no town 0.1; an animal
    /// 0; a law enforcer (<see cref="Crime.CrimeSystem.IsPoliceLike"/>) carrying a slave or escapee it has no bounty on 1.4; a body the carrier
    /// regards as friendly (<see cref="CarrySystem.Friendly"/>) 0; a dead one 0.5; else 2. Before the animal test, 0 while the squad's mission town
    /// (the board's <see cref="Blackboard.Destination"/>, the hand of <b>Observed</b> <c>FUN_1405a0f10</c>) is set and is not the town the carrier
    /// stands in.
    /// </summary>
    public float CageDesire(World world, int carrier, int body)
    {
        var table = world.Characters;
        if (table.Cold(carrier) is not { } c || table.Cold(body) is not { } b) return 0;
        var here = AiSystem.TownAt(data, world, table.Next[carrier].Position);
        if (here.IsNone) return OutsideTownDesire;

        // The squad's mission town (the hand Task_TravelToTargetTown sets, FUN_1405a0f10) set and not the town it stands in: 0.
        if (world.Squads.Find(c.SquadId)?.Ai is { Destination.IsNone: false } board && board.Destination != here)
            return 0;

        if (b.Fighter is { IsAnimal: true }) return 0;
        if (b.SlaveState != 0 && (b.Bounties?.BookedAmount(data.Relations, c.Faction) ?? 0) < 1
                && world.SystemOf<Crime.CrimeSystem>()?.IsPoliceLike(world, c) == true)
            return PoliceSlaveDesire;

        if (carry.Friendly(c, b)) return 0;
        return b.Medical is { Dead: true } ? DeadDesire : AliveDesire;
    }

    /// <summary>
    /// The desire of 224 and 274 (both rows' desire stub jumps to <c>FUN_1405a13a0</c>, <b>Verified</b> by decompile and disassembly): 0 unless the
    /// carrier carries a resolvable character, 0 when it regards that one as friendly (vfunc <c>0x3f0</c>, <see cref="CarrySystem.Friendly"/>),
    /// else <see cref="AliveDesire"/> (<c>DAT_141682ad0</c>, 2). No town, animal or death test.
    /// </summary>
    public float ThrowOutDesire(World world, int carrier, int body)
    {
        var table = world.Characters;
        if (table.Cold(carrier) is not { } c || table.Cold(body) is not { } b || c.Carrying != table.IdOf(body)) return 0;

        return carry.Friendly(c, b) ? 0 : AliveDesire;
    }

    /// <summary>
    /// The bed system, resolved on first use (null in a world without one).
    /// </summary>
    internal BedSystem? Beds(World world) => _beds ??= world.SystemOf<BedSystem>();

    /// <summary>
    /// The cage finder of 112 and 281 (<c>FUN_1408d0d30</c>, 1.0 with a free cage, <b>Verified</b>): the best free cage among the carrier's town
    /// candidates (<see cref="FixtureRegistry.CageCandidates"/>; an occupier may use the occupied town's, its use test passing for that town,
    /// <c>FUN_140621230</c>) by <see cref="FixtureRegistry.FindCage"/>'s weights (its squad's home x100), not another member's errand's.
    /// </summary>
    internal Fixture? Cage(World world, int slot, CharacterCold carrier, Vector3 at)
    {
        var town = TownOf(world, slot);
        if (town.Site >= 0) world.Fixtures.RegisterSite(world, data, town.Site);

        var id = world.Characters.IdOf(slot);
        var candidate = world.Fixtures.CageCandidates(world, data, carrier, at);
        return world.Fixtures.FindCage(world, carrier, at, FixtureRegistry.HomeOf(world, carrier), f => !candidate(f) || CageTaken(f.Id, id));
    }

    #endregion

    #region Food and confiscation

    /// <summary>
    /// Units of one record the looting task (Task_EmptyMachine 284, <c>FUN_14033cd10</c>) takes per run.
    /// </summary>
    public const int LootUnits = 10;

    /// <summary>
    /// Looting food from a store (284, <c>FUN_14033cd10</c>, <b>Verified</b>): the first record the store lists, whatever it is (the storage's list,
    /// vfunc <c>0x548</c> <c>FUN_140299990</c>, is the distinct records of its inventory in item order, with no food filter once the task's looting
    /// flag is set; the finder only checked that some food is held), one unit at a time while the store still holds it and there is room, at most
    /// <see cref="LootUnits"/> in all.
    /// </summary>
    static void LootFirstFood(Inventory store, Inventory into)
    {
        if (store.Items.FirstOrDefault()?.Record is not { } record) return;
        int budget = LootUnits;
        foreach (var item in store.Items.Where(i => i.Record == record).ToList())
        {
            int moved = InventoryCommands.MoveInto(store, item, budget, into, bagFirst: true, out _);
            budget -= moved;
            if (moved == 0 || budget <= 0) break;
        }
    }

    /// <summary>
    /// Grabbing one food (244, <c>FUN_140340850</c>, <b>Verified</b>): the first raw-food item (function 15) the character accepts (an animal, or the
    /// race's special food), else the first food (function 3); one unit goes into the worn backpack when it takes it, else the character's inventory.
    /// Returns the units moved (0: nothing to take, or no room).
    /// </summary>
    static int GrabFood(Inventory store, CharacterCold c, Inventory into) =>
        FoodToTake(store, c) is { } item
            ? InventoryCommands.MoveInto(store, item, 1, into, bagFirst: true, out _)
            : 1;   // nothing listed is no failure of room

    /// <summary>
    /// The item <see cref="GrabFood"/> takes: the first raw-food item the character accepts, else the first food; null when the store lists none.
    /// </summary>
    static ItemInstance? FoodToTake(Inventory store, CharacterCold c)
    {
        bool animal = c.Medical is { HasStatsObject: false };
        return store.Items.FirstOrDefault(i => i.ItemFunction == FeedSystem.RawFoodFunction && Edible(i, animal, c.Race))
            ?? store.Items.FirstOrDefault(i => i.IsFood);
    }

    /// <summary>
    /// State 82 for a player-side run (<see cref="FoodWant"/>): one unit of the item the take would move has room in the character's inventory or
    /// worn bag (<see cref="Inventory.HasRoomFor"/>). Read on the take's own pick, not <see cref="FoodFits"/>'s, so a player animal's raw food and a
    /// race's special food are tested as they would be taken.
    /// </summary>
    bool TakeFits(World world, string storeId, CharacterCold c) =>
        StoreInventory(world, storeId) is { } store && c.Inventory is { } inv && FoodToTake(store, c) is { } item && inv.HasRoomFor(item);

    /// <summary>
    /// One tick of a food run: walking to the store, then taking one food item; false when it is over.
    /// </summary>
    bool StepFood(World world, TownDuty d, int g, CharacterCold gc)
    {
        var table = world.Characters;
        ref var gn = ref table.Next[g];
        if (d.Target is not { } id || StoreInventory(world, id) is not { } store || gc.Inventory is not { } into)
            return End(world, d);

        bool walking = gn.Task == (byte)CharacterTask.GoTo;

        // A player order (a walk elsewhere, any other task) wins: the run ends and leaves it be.
        if (d.PlayerSide && (walking ? gn.Goal != d.Goal : gn.Task != (byte)CharacterTask.Idle))
        {
            gc.Engaged = false;
            return false;
        }

        if (Vector2.Distance(Flat(gn.Position), d.Goal) <= Settings.Arrive || !walking
                && Vector2.Distance(Flat(gn.Position), d.Goal) <= Settings.CageSlack)
        {
            TaskStart.Stand(table, g);
            if (d.Kind == TownDutyKind.LootFood)
                LootFirstFood(store, into);
            else if (GrabFood(store, gc, into) == 0 && d.PlayerSide)
            {
                // No room on arrival although the start found some: room lost on the walk, or a room test that said yes where the move refuses
                // (a food stack carrying a trade receipt, which a plain unit does not join). Not again for a while, so the run cannot loop.
                _foodWaits.Add((d.Member, world.GameHours + Settings.RefuseHours));
            }
            world.Machines.Touch();
            return End(world, d);
        }

        if (walking) return true;
        if (++d.Tries > Settings.Walks) return End(world, d);
        TaskStart.Walk(world, g, d.Goal, ErrandMode(world, gc), RowFor(d), TaskSource.Goal);
        return true;
    }

    /// <summary>
    /// Whether an errand holds <paramref name="body"/>. Plain loops, not lambdas: Contraband looks at every cage each pass, and the captures made two
    /// closures per cage.
    /// </summary>
    internal bool HasDutyOn(CharacterId body)
    {
        foreach (var d in _duties)
            if (d.Body == body) return true;
        return false;
    }

    /// <summary>
    /// Whether the inventory holds an item stolen from <paramref name="faction"/>.
    /// </summary>
    internal bool CarriesStolen(Inventory inventory, int faction)
    {
        foreach (var item in inventory.Items)
            if (item.IsStolenFor(faction, data)) return true;
        return false;
    }

    /// <summary>
    /// <c>AI::findPrisonerWithContraband</c> (<c>FUN_1408cf6a0</c>, goal 212): the first cage of the town, in list order (no distance weighting),
    /// whose occupant carries a stolen item and is not another errand's. Engine choice: the original's room test for the item is left to the take.
    /// </summary>
    internal (Fixture? Cage, CharacterId Prisoner) Contraband(World world, CampaignTown town)
    {
        var table = world.Characters;
        if (town.Site >= 0) world.Fixtures.RegisterSite(world, data, town.Site);
        foreach (var f in world.Fixtures.All)
        {
            if (f.Kind != FixtureKind.Cage || FixtureRegistry.IsFree(table, f) || !table.TryResolveNext(f.Occupant, out int s)
                    || HasDutyOn(f.Occupant))
                continue;

            if (table.Cold(s) is not { Inventory: { } inv } c || !CarriesStolen(inv, c.Faction) || AiSystem.TownAt(data, world, f.Position) != town)
                continue;

            return (f, f.Occupant);
        }

        return (null, CharacterId.None);
    }

    /// <summary>
    /// A confiscation (212; its effect is row 3 "Collecting item" acted out by <c>Task_Loot_AI</c>, whose law-enforcement branch it is,
    /// <b>Observed</b>): at the cage the guard waits <see cref="CarrySystem.ConfiscateSeconds"/>, then takes one unit of each stolen item of the
    /// prisoner (<see cref="CarrySystem.Confiscate"/>). Engine choice: every 212 runner confiscates (the base data lists 212 only in the
    /// law-enforcement add-on packages).
    /// </summary>
    bool StepConfiscate(World world, TownDuty d, int g, CharacterCold gc)
    {
        var table = world.Characters;
        ref var gn = ref table.Next[g];
        var cage = d.Target is { } id ? world.Fixtures.Find(id) : null;
        if (cage is null || cage.Occupant != d.Body || !table.TryResolveNext(d.Body, out int p) || table.Cold(p)?.Inventory is null
                || gc.Inventory is null)
            return End(world, d);

        bool walking = gn.Task == (byte)CharacterTask.GoTo;
        float gap = Vector2.Distance(Flat(gn.Position), d.Goal);
        if (FixtureRegistry.AtCage(cage, gn.Position, walking))
        {
            TaskStart.Stand(table, g);
            if ((d.Next += world.TickSeconds) <= CarrySystem.ConfiscateSeconds) return true;
            carry.Confiscate(gc, table.Cold(p)!, world.BagSearchFix);
            return End(world, d);
        }

        if (walking) return true;
        if (++d.Tries > Settings.Walks) return End(world, d);
        TaskStart.Walk(world, g, d.Goal, SpeedMode.Walk, RowFor(d), TaskSource.Goal);
        return true;
    }

    #endregion

    #region Shooing and ransacking

    /// <summary>
    /// The senses (resolved on first use), for the shoo finder.
    /// </summary>
    AggroSystem? _aggro;

    /// <summary>
    /// Who is inside each building, by building key, as of <see cref="_insidersTick"/> (the shoo finder's pass).
    /// </summary>
    readonly Dictionary<string, List<int>> _insiders = new(StringComparer.Ordinal);

    /// <summary>
    /// The tick <see cref="_insiders"/> was taken (-1: never).
    /// </summary>
    long _insidersTick = -1;

    /// <summary>
    /// Event 9 EV_SHOO_FROM_MY_BUILDING, 17 EV_INTRUDER_FOUND (for an enemy), 60 EV_GIVE_UP_CHASE (docs/game/dialogue.md "Enums").
    /// </summary>
    const int ShooEvent = 9, IntruderFound = 17, GiveUpChase = 60;

    /// <summary>
    /// Game seconds between shoo events (engine choice: the original re-sends on every update and its lines' own timers pace them).
    /// </summary>
    const float ShooEverySeconds = 5;

    /// <summary>
    /// The door planner of the ransack's way in (<see cref="EntryTo"/>).
    /// </summary>
    readonly DoorPlan _doorPlan = new();

    /// <summary>
    /// The salt of the ransack's target roll.
    /// </summary>
    const ulong RansackSalt = 0x5261_6E73_6163_6B21;

    /// <summary>
    /// <c>AI::findStrangersInMyBuilding</c> (<c>FUN_1408ced70</c>, goals 158 / 283): in the squad's home building, the first character (slot order,
    /// the original takes its sensed-list order) neither of an allied faction nor an enemy, not down, dead or caged, not a slave of the shooer's
    /// faction and not law enforcement (AUTHORITY_FIGURE, engine reading); for an NPC squad also any of the player's characters there that is not
    /// sneaking or that the shooer has spotted. Not one another errand shoos. None: none.
    /// </summary>
    internal CharacterId Stranger(World world, int slot, CharacterCold cold, Squad squad)
    {
        if (squad.HomeBuilding is not { } home) return CharacterId.None;
        var table = world.Characters;
        _aggro ??= world.Systems.OfType<AggroSystem>().FirstOrDefault();
        if (_insidersTick != world.Tick)
        {
            _insidersTick = world.Tick;
            _insiders.Clear();
            for (int s = 0; s < table.HighWater; s++)
            {
                if (table.Next[s].Alive && world.Sight.BuildingOf(s) is { } key)
                    (_insiders.TryGetValue(key, out var list) ? list : _insiders[key] = []).Add(s);
            }
        }

        if (!_insiders.TryGetValue(home.Key, out var inside)) return CharacterId.None;
        foreach (int s in inside)
        {
            if (s == slot || table.Cold(s) is not { Medical.Incapacitated: false } c || c.IsImprisoned) continue;
            if (c.SlaveState == 1 && c.SlaveOwner == cold.Faction) continue;
            var id = table.IdOf(s);
            if (_duties.Any(o => o.Kind == TownDutyKind.Shoo && o.Body == id)) continue;
            bool neutral = c.Faction != cold.Faction && !(data.Relations.Contains(cold.Faction) && data.Relations.Contains(c.Faction)
                && (data.Relations.IsAlly(cold.Faction, c.Faction) || AggroSystem.IsEnemy(data.Relations, cold.Faction, c.Faction)));
            if (neutral && c.NpcClass != Data.Gameplay.FundamentalType.LawEnforcement) return id;
            if (!cold.IsPlayer && c.IsPlayer && ((c.Orders & CharacterOrders.Sneak) == 0 || _aggro?.Spotted(slot, id) == true))
                return id;
        }

        return CharacterId.None;
    }

    /// <summary>
    /// A shoo (161 <c>Task_TalktoPlayer</c> with the row event 9): the shooer walks at the stranger and sends it event 9 (17 when it is an enemy)
    /// every <see cref="ShooEverySeconds"/>; the dialogue data does the rest (lines that follow, time out and turn violent). After 25 game minutes,
    /// once the stranger is getting away (fleeing or walking off), it gives up with event 60. Over when the stranger is no longer in the home
    /// building, is down or caged.
    /// </summary>
    bool StepShoo(World world, TownDuty d, int g, CharacterCold gc)
    {
        var table = world.Characters;
        var squad = world.Squads.Find(gc.SquadId);
        if (squad?.HomeBuilding is not { } home || !table.TryResolveNext(d.Body, out int t)
                || table.Cold(t) is not { Medical.Incapacitated: false, IsImprisoned: false } tc
            || world.Sight.BuildingOf(t) != home.Key)
            return End(world, d);

        ref var gn = ref table.Next[g];
        var to = new Vector2(table.Next[t].Position.X, table.Next[t].Position.Z);
        if (Vector2.Distance(Flat(gn.Position), to) > Dialogue.DialogueSystem.TalkReach
                && (gn.Task != (byte)CharacterTask.GoTo || Vector2.Distance(gn.Goal, to) > Dialogue.DialogueSystem.TalkReach))
            TaskStart.Walk(world, g, to, SpeedMode.Walk, RowFor(d), TaskSource.Goal);

        d.Next -= world.TickSeconds;
        if (d.Next <= 0)
        {
            d.Next = ShooEverySeconds;
            bool enemy = data.Relations.Contains(gc.Faction) && data.Relations.Contains(tc.Faction)
                    && AggroSystem.IsEnemy(data.Relations, gc.Faction, tc.Faction);
            world.Defer(new Dialogue.DialogueEvent(d.Member, d.Body, enemy ? IntruderFound : ShooEvent) { Tick = world.Tick + 1 });
        }

        bool away = tc.Fleeing || table.Next[t].Task == (byte)CharacterTask.GoTo
                && Vector2.Distance(table.Next[t].Goal, Flat(gn.Position)) > Vector2.Distance(to, Flat(gn.Position));
        if ((world.GameHours - d.Since) * 60 > 25 && away)
        {
            world.Defer(new Dialogue.DialogueEvent(d.Member, d.Body, GiveUpChase) { Tick = world.Tick + 1 });
            return End(world, d);
        }

        return true;
    }

    /// <summary>
    /// <c>AI::findRandomBuildingInTown</c> (<c>FUN_1408cb510</c>, goal 216): the squad's town as <c>FUN_140269cd0</c> gives it (the override's mission
    /// town, a campaign's war package's the campaign target, <see cref="Blackboard.ContractMissionTown"/>; else the running package's mission town),
    /// else the town the member stands in; a player town is not drawn from; one of its standing buildings with an interior, drawn uniformly at random among those the
    /// planner would reach: row 156 Entering building (the only provider of effect 107, which goal 216 wants) needs DESTINATION_IS_ACCESSIBLE (59:
    /// <c>hasAnOpenDoor</c>) and DESTINATION_NOT_BLOCKED_BY_FORTIFICATIONS (90: no closed gate between), <b>Observed</b>. The door sub-steps are
    /// planned (<see cref="EntryTo"/>: open the door, row 72, or bash it, row 81); a building with no way in is left out of the draw. A closed gate
    /// on the route is planned as for an attacker (<see cref="Locks.LockSystem.GateEntry"/>: bash it, row 123 / 290, or pass an unlocked one, row
    /// 122). Null: none.
    /// </summary>
    internal (Meitou.Data.World.TownBuilding Building, string? Door)? RansackTarget(World world, Squad squad, int slot)
    {
        int site = squad.Ai is { ContractMissionTown.Site: >= 0 } b ? b.ContractMissionTown.Site
            : squad.Ai is { Destination: { Site: >= 0 } dest } ? dest.Site
            : AiSystem.TownAt(data, world, world.Characters.Next[slot].Position).Site;
        if (site < 0) return null;
        _locks ??= world.Systems.OfType<LockSystem>().FirstOrDefault();
        var from = world.Characters.Next[slot].Position;
        var rooms = new List<(Meitou.Data.World.TownBuilding, string?)>();
        foreach (var x in data.Sites[site].Buildings)
        {
            if (world.Fixtures.IsDestroyed(x) || x.InteriorLayout.Length == 0 && x.InteriorLayouts.Count == 0) continue;
            var gate = _locks?.GateEntry(world, slot, from, x.Position) ?? (DoorPlan.Entry.Walk, null);
            if (gate.Entry == DoorPlan.Entry.Blocked) continue;
            if (gate.Door is not null) rooms.Add((x, gate.Door));   // the gate is bashed first (rows 123 / 290)
            else if (EntryTo(world, slot, x) is { } door) rooms.Add((x, door.Length == 0 ? null : door));
        }

        if (rooms.Count == 0) return null;
        return rooms[Rng.Int(Rng.Hash(world.Seed, (ulong)Rng.Key(world.Characters.IdOf(slot)) ^ (ulong)world.Tick, RngPurpose.Slow, RansackSalt),
                rooms.Count)];
    }

    /// <summary>
    /// How the member gets into a building, by the planner (row 156's state 59 <c>hasAnOpenDoor</c>, <see cref="Locks.DoorPlan.PlansEntry"/>): "" to
    /// walk in (a door open, none, or a closed one the character opens as it passes, row 72), the id of the door it bashes first (row 81, a locked
    /// door of a building it is aggressive towards), null when the planner finds no way in.
    /// </summary>
    string? EntryTo(World world, int slot, Meitou.Data.World.TownBuilding building)
    {
        if (world.Fixtures.HasOpenDoor(building.Key)) return "";
        if (LockSystem.MainGateDoor(world, building.Key) is not { } main) return "";
        var entry = _doorPlan.PlansEntry(world, world.SystemOf<Combat.CombatSystem>(), slot, building.Key, main.Door, main.Spot, _locks);
        return entry switch { DoorPlan.Entry.Walk => "", DoorPlan.Entry.Bash => main.Door.Id, _ => null };
    }

    /// <summary>
    /// 216 Ransacking town (156 into the drawn building, then <c>Task_PatrolTownViolent</c>, which only sets running): the member runs into the
    /// building; once inside (or when the walk ends near it) the errand ends and the next look draws another, so the raiders run from building to
    /// building.
    /// </summary>
    bool StepRansack(World world, TownDuty d, int g)
    {
        var table = world.Characters;
        ref var gn = ref table.Next[g];
        bool walking = gn.Task == (byte)CharacterTask.GoTo;
        if (table.Cold(g)?.Bashing is not null) return true;   // bashing the door first (the lock system walks and swings)
        if (world.Sight.BuildingOf(g) == d.Target || !walking && Vector2.Distance(Flat(gn.Position), d.Goal) <= Settings.CageSlack)
            return End(world, d);

        if (walking) return true;
        if (++d.Tries > Settings.Walks) return End(world, d);
        TaskStart.Walk(world, g, d.Goal, SpeedMode.Run, RowFor(d), TaskSource.Goal);
        return true;
    }

    #endregion
}
