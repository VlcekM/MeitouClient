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
/// Goals of a squad's town the AI's act-outs do not cover (docs/game/carry-loot.md 3.2, ai-tasks.md "Finders of the jobs"), for the members of the
/// squads the AI runs whose running package lists them for the member's role in the hour (<see cref="PopulationSystem.GoalDuty"/>). All serial, in
/// <c>SlowWorld</c> after the carry system.
/// <list type="bullet">
/// <item><b>273 Remove Intruder</b> (finder <c>FUN_14099c250</c>, <b>Verified</b> by decompile): only while the member stands in a town that is its
/// home town, the town its occupation holds, or a town of its faction (<c>Ownerships::isMyTown</c>, KenshiLib names); the intruder is knocked out, in
/// that town, not carried or caged (Meitou: nor in a bed), and (an engine reading of the sensed-list classes) not of the member's faction or an ally. The original's last
/// test, <c>amInsideTownWalls</c>, is read as the town's built-up area (<see cref="BuiltUp"/>). The member lifts it, then</item>
/// <item><b>112 Find and put in cage</b> (a living body) or <b>281</b> (any body), while the package lists it: the best free cage in the town (the
/// original's finder; an occupier may use the occupied town's, its use test passing for that town, <c>FUN_140621230</c>), where the intruder is
/// locked up with the put-in's usual sentence (from a bounty with the cage owner's faction, docs/game/economy.md 11.5; none: served, so the jailer
/// frees it); else</item>
/// <item><b>274 Throwing out</b> (<c>Task_TakeIntruderOutsideTown</c>): carried to the town's point outside its gates (<see cref="TownExit"/>, the
/// same for every carrier) and put down.</item>
/// <item><b>223 Remove Home Intruder</b> (finder <c>FUN_14099c110</c>): a knocked-out character in the squad's home building
/// (<see cref="HomeIntruder"/>) is lifted, then with <b>224</b> (<c>Task_TakeIntruderOutside</c>) carried past the home's first door
/// (<see cref="AiSystem.LeavePoint"/>; a home with no door: the town's exit point) and put down; without 224 it goes on as 273's body does.</item>
/// <item><b>138 Find corpses</b> and <b>137 Corpse disposal</b> (<see cref="LiftsCorpse"/>): a member listing 138, or 137 with a disposal found
/// (<see cref="CorpseDisposal.Find"/>), lifts the corpse 138's finder gives; then 137, ranked with the cage and throw-out goals
/// (<see cref="RankDisposals"/>), carries it to the disposal, which takes it in; without a disposal goal it keeps the body.</item>
/// <item><b>106 Find and put in bed</b>, ranked with those goals for any carried body (<see cref="RankDisposals"/>): a body the carrier would lay
/// in a bed (<see cref="BedSystem.PutInDesire"/>, <c>FUN_1405a0d50</c>) is walked to the bed the bed search finds and laid in
/// (<see cref="BedSystem.PutIn"/>).</item>
/// <item><b>243 Getting food</b> (finder <c>FUN_1408cb650</c>, <c>BuildingFinder_LootFoodAndStuff</c>; then 244 <c>Task_grabOneFood</c>): a member
/// holding no food walks to the nearest store with food in the town and takes one item of it (the goal holds while it has food, list A
/// <c>gotAFoodItem</c>). The finder searches the blackboard's target town (<c>Blackboard.Destination</c>), else the town the member stands in, else
/// the 3 x 3 zone cells round it; it need not be the squad's own, and a player-side member takes only from its faction's stores.</item>
/// </list>
/// </summary>
public sealed class TownDutySystem(PopulationData data, PopulationSystem population, CarrySystem carry, TownDutySettings? settings = null)
        : ITickSystem, IStateHashed, IDecisionOwner
{
    #region State and wiring

    /// <summary>
    /// The errands under way, in the order they were taken up.
    /// </summary>
    readonly List<TownDuty> _duties = [];

    /// <summary>
    /// The downed characters that may be lifted, as of the look (<see cref="CountBodies"/>).
    /// </summary>
    readonly List<int> _downed = [];

    /// <summary>
    /// The slots carrying someone at the look.
    /// </summary>
    readonly List<int> _carriers = [];

    /// <summary>
    /// The bodies an errand, a refusal or a carrier holds, as of the look: no other errand takes them.
    /// </summary>
    readonly HashSet<CharacterId> _reserved = [];

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
    /// The serial step: every errand steps on (an ended one is dropped), then the look on each member's turn, the player's food runs and the ditches
    /// asked for.
    /// </summary>
    public void SlowWorld(World world)
    {
        for (int k = 0; k < _duties.Count;)
        {
            if (Step(world, _duties[k])) k++;
            else _duties.RemoveAt(k);
        }

        // Each member looks once in LookEveryTicks, on its slot's turn (slot mod the period): the looks spread over the ticks (docs/simulation.md
        // "Spread sweeps").
        int every = world.Every(Settings.LookEveryTicks);
        Look(world, every);
        LookPlayerFood(world, every);
        StartRequestedDitches(world);
    }

    /// <summary>
    /// A point on the ground plane (X, Z).
    /// </summary>
    static Vector2 Flat(Vector3 p) => new(p.X, p.Z);

    /// <summary>
    /// Hashes the errands, the refusals, the food waits and the lifts given up (only while there are any).
    /// </summary>
    public void Hash(ref StateHasher hasher)
    {
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
            if (c.Engaged || c.InCombat || c.Fleeing || Busy(id)) continue;
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
    /// Starts a ditch errand: the member walks to the store spot to empty its inventory there.
    /// </summary>
    void StartDitch(World world, int slot, CharacterCold cold, CampaignTown town, string storeId, Vector2 spot)
    {
        cold.Engaged = true;
        _duties.Add(new TownDuty { Member = world.Characters.IdOf(slot), Faction = cold.Faction, Town = town, Kind = TownDutyKind.Ditch,
                Target = storeId, Goal = spot });
        TaskStart.Walk(world, slot, spot, SpeedMode.Walk, RowOf(TownDutyKind.Ditch), TaskSource.Goal);
    }

    /// <summary>
    /// 243 Getting food for the player's side (<c>Character::setupAI</c> gives it to every character of the player's faction; desire AI slots 26, 24,
    /// 25, <b>Verified</b> by decompile): an animal only while the squad option Feed Animals is on; only in a town (slot 24), and in one of another
    /// faction only while the player's faction is resident there (town slot 88: the town's owner or its <c>factionsResidentHere</c>, which takes the
    /// player's faction when a player character stands in a building of the player's in that town; Meitou: the player owns a house there,
    /// <see cref="FixtureRegistry.OwnsPropertyIn"/>). In a town with occupiers while it senses an enemy: inside the walls it wants food whatever its
    /// hunger (desire 0.2), outside them in a town with gates not at all; else the 259 desire (slot 25): stomach empty, hunger at most 2.4, no food
    /// it would bite on it, not fighting unless hunger is below 1.2. It walks to the nearest store of the town owned by its faction with food in it
    /// and takes one item, which the eating rule then eats. A player character's goal is the decision step's (<see cref="GettingFood"/>, priority 3,
    /// weight 0.5); this pass runs it for the other characters of the player's faction while idle (they hold no jobs; engine choice).
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

            var task = (CharacterTask)table.Next[s].Task;
            if (task != CharacterTask.Idle) continue;
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
        TaskStart.Walk(world, s, store.Spot, SpeedMode.Walk, RowOf(TownDutyKind.Food), TaskSource.Goal);
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

    #region The look

    /// <summary>
    /// The look of the town duties: the downed, the carriers and the bodies taken are counted, the carriers looked at, then every free member of
    /// an AI squad on its turn takes the first duty its role has and finds work for (ransacking, shooing, looting, stashing, lifting intruders,
    /// confiscating, fetching food).
    /// </summary>
    void Look(World world, int every)
    {
        CountBodies(world);
        LookCarriers(world, every);

        var duties = new RoleDuties
        {
            Intruders = stackalloc int[5],
            Food = stackalloc int[5],
            Confiscate = stackalloc int[5],
            Loot = stackalloc int[5],
            Shoo = stackalloc int[5],
            Ransack = stackalloc int[5],
            Stash = stackalloc int[5],
            Home = stackalloc int[5],
            Corpse = stackalloc int[5],
            FindCorpse = stackalloc int[5],
        };

        foreach (var squad in world.Squads.All)
        {
            if (squad.Ai is null || squad.Members.Count == 0) continue;

            duties.Clear();
            foreach (var member in squad.Members) LookMember(world, squad, member, every, ref duties);
        }
    }

    /// <summary>
    /// Which duties each role of the squad looked at has (by <see cref="AiRole"/>; -1: not asked yet, 0: no, 1: yes).
    /// </summary>
    ref struct RoleDuties
    {
        /// <summary>
        /// 273 Throw out of town (lifting downed intruders out of its town).
        /// </summary>
        public Span<int> Intruders;

        /// <summary>
        /// 244 Fetching food from the home base's stores.
        /// </summary>
        public Span<int> Food;

        /// <summary>
        /// Confiscating stolen goods from the town's cages.
        /// </summary>
        public Span<int> Confiscate;

        /// <summary>
        /// 131 Looting food and stuff.
        /// </summary>
        public Span<int> Loot;

        /// <summary>
        /// Shooing strangers out of its building (or out of a private one).
        /// </summary>
        public Span<int> Shoo;

        /// <summary>
        /// Running round the town looking for people (the ransack).
        /// </summary>
        public Span<int> Ransack;

        /// <summary>
        /// 184 Putting loot in storage.
        /// </summary>
        public Span<int> Stash;

        /// <summary>
        /// Picking intruders up out of its home.
        /// </summary>
        public Span<int> Home;

        /// <summary>
        /// 137 Corpse disposal.
        /// </summary>
        public Span<int> Corpse;

        /// <summary>
        /// 138 Find corpses.
        /// </summary>
        public Span<int> FindCorpse;

        /// <summary>
        /// Forgets every answer (a new squad).
        /// </summary>
        public readonly void Clear()
        {
            Intruders.Fill(-1);
            Food.Fill(-1);
            Confiscate.Fill(-1);
            Loot.Fill(-1);
            Shoo.Fill(-1);
            Ransack.Fill(-1);
            Stash.Fill(-1);
            Home.Fill(-1);
            Corpse.Fill(-1);
            FindCorpse.Fill(-1);
        }
    }

    /// <summary>
    /// The look's census: the bodies a duty or a refusal holds or a carrier carries or goes to pick up are taken (not an intruder its lifter still
    /// walks to, <see cref="WalksToIntruder(CharacterTable, TownDuty)"/>), the carriers and the downed that may be lifted are listed.
    /// </summary>
    void CountBodies(World world)
    {
        var table = world.Characters;
        _downed.Clear();
        _carriers.Clear();
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

            if (!c.Carrying.IsNone)
            {
                _reserved.Add(c.Carrying);
                _carriers.Add(s);
            }

            if (c is { Medical: { Unconscious: true, Dead: false } } && c.InSomething != HeldState.Imprisoned && CarrySystem.CanBeLifted(c))
                _downed.Add(s);
        }
    }

    /// <summary>
    /// One member on its turn, free and not busy: its role's duties are asked once per squad, then it ransacks or shoos when it can, else takes up
    /// an errand (<see cref="LookErrands"/>).
    /// </summary>
    void LookMember(World world, Squad squad, CharacterId member, int every, ref RoleDuties duties)
    {
        var table = world.Characters;
        if (!table.TryResolveNext(member, out int slot) || !world.Turn(slot, every) || !Free(world, slot) || Busy(member)) return;

        var cold = table.Cold(slot)!;
        int role = (int)AiSystem.RoleOf(squad, member, cold);
        AskDuties(world, squad, role, ref duties);
        var intruders = duties.Intruders;
        var food = duties.Food;
        var confiscate = duties.Confiscate;
        var loot = duties.Loot;
        var shoo = duties.Shoo;
        var ransack = duties.Ransack;
        var stash = duties.Stash;
        var home = duties.Home;

        if (ransack[role] == 1 && RansackTarget(world, squad, slot) is var (raid, door))
        {
            cold.Engaged = true;
            if (door is not null) _locks?.BashForErrand(cold, door);
            if (cold.Fighter is { } armed) cold.DrawnWeapon = Combat.CombatSystem.MeleeDrawn(armed);   // B: hasWeaponEquipped

            var goal = Flat(raid.Position);
            _duties.Add(new TownDuty
            {
                Member = member, Faction = cold.Faction, Town = AiSystem.TownAt(data, world, raid.Position), Kind = TownDutyKind.Ransack,
                Target = raid.Key, Goal = goal,
            });
            TaskStart.Walk(world, slot, goal, SpeedMode.Run, RowOf(TownDutyKind.Ransack), TaskSource.Goal);
            return;
        }

        if (intruders[role] == 0 && food[role] == 0 && confiscate[role] == 0 && loot[role] == 0 && shoo[role] == 0 && stash[role] == 0
            && home[role] == 0 && duties.Corpse[role] == 0 && duties.FindCorpse[role] == 0)
        {
            return;
        }

        if (shoo[role] == 1 && Stranger(world, slot, cold, squad) is { IsNone: false } stranger)
        {
            cold.Engaged = true;
            _duties.Add(new TownDuty
            {
                Member = member, Body = stranger, Faction = cold.Faction, Town = AiSystem.TownAt(data, world, table.Next[slot].Position),
                Kind = TownDutyKind.Shoo, Since = world.GameHours,
            });
            return;
        }

        LookErrands(world, squad, member, slot, cold, role, in duties);
    }

    /// <summary>
    /// The duties of the member's role (<paramref name="role"/>) not asked yet in this squad's look are asked of its goals.
    /// </summary>
    void AskDuties(World world, Squad squad, int role, ref RoleDuties duties)
    {
        var intruders = duties.Intruders;
        var food = duties.Food;
        var confiscate = duties.Confiscate;
        var loot = duties.Loot;
        var shoo = duties.Shoo;
        var ransack = duties.Ransack;
        var stash = duties.Stash;
        var home = duties.Home;

        if (intruders[role] < 0)
            intruders[role] = population.GoalDuty(world, squad, (AiRole)role, TaskType.PickupIntrudersTown) ? 1 : 0;

        if (food[role] < 0)
            food[role] = population.GoalDuty(world, squad, (AiRole)role, TaskType.AquireFoodAtHomebase) ? 1 : 0;

        if (confiscate[role] < 0)
            confiscate[role] = population.GoalDuty(world, squad, (AiRole)role, TaskType.LootStolenGoods) ? 1 : 0;

        if (loot[role] < 0)
            loot[role] = population.GoalDuty(world, squad, (AiRole)role, TaskType.LootFoodAndStuff) ? 1 : 0;

        if (shoo[role] < 0)
        {
            shoo[role] = squad.HomeBuilding is not null
                && (population.GoalDuty(world, squad, (AiRole)role, TaskType.ShooStrangersOutOfMyBuilding)
                    || squad.Ai is { HomePublic: false }
                    && population.GoalDuty(world, squad, (AiRole)role, TaskType.ShooStrangersOutOfMyBuildingIfPrivate)) ? 1 : 0;
        }

        if (ransack[role] < 0)
            ransack[role] = population.GoalDuty(world, squad, (AiRole)role, TaskType.RunAroundTownLookingForPeople) ? 1 : 0;

        if (stash[role] < 0)
            stash[role] = population.GoalDuty(world, squad, (AiRole)role, TaskType.PutLootInStorage) ? 1 : 0;

        if (home[role] < 0)
            home[role] = squad.HomeBuilding is not null && population.GoalDuty(world, squad, (AiRole)role, TaskType.PickupIntruders) ? 1 : 0;

        if (duties.Corpse[role] < 0)
            duties.Corpse[role] = population.GoalDuty(world, squad, (AiRole)role, TaskType.DeadGuysGoInThePot) ? 1 : 0;

        if (duties.FindCorpse[role] < 0)
            duties.FindCorpse[role] = population.GoalDuty(world, squad, (AiRole)role, TaskType.FindADeadGuy) ? 1 : 0;
    }

    /// <summary>
    /// The errands a member takes up where it stands, the first it finds: looting food, ditching stolen goods, lifting a home or town intruder,
    /// confiscating from a cage, fetching food (a ditch first when the food does not fit).
    /// </summary>
    void LookErrands(World world, Squad squad, CharacterId member, int slot, CharacterCold cold, int role, in RoleDuties duties)
    {
        var table = world.Characters;
        var intruders = duties.Intruders;
        var food = duties.Food;
        var confiscate = duties.Confiscate;
        var loot = duties.Loot;
        var stash = duties.Stash;
        var home = duties.Home;
        var at = table.Next[slot].Position;
        var town = AiSystem.TownAt(data, world, at);

        // 131 Looting food and 243 below search the blackboard's target town (Blackboard.Destination), else the current one, else the 3 x 3
        // zone cells round the member (FUN_1408cb650).
        var foodTown = squad.Ai is { Destination.IsNone: false } target ? target.Destination : town;

        if (loot[role] == 1 && !HoldsFood(cold) && Store(world, at, foodTown, cold.Faction, asker: cold) is var (lootId, lootSpot))
        {
            cold.Engaged = true;
            _duties.Add(new TownDuty
            {
                Member = member, Faction = cold.Faction, Town = town, Kind = TownDutyKind.LootFood, Target = lootId, Goal = lootSpot,
            });
            TaskStart.Walk(world, slot, lootSpot, SpeedMode.Walk, RowOf(TownDutyKind.LootFood), TaskSource.Goal);
            return;
        }

        // 184 Ditching loot: stolen goods it carries, in whatever town it is in (the finder reads the current town only).
        if (stash[role] == 1 && !town.IsNone && StashStore(world, squad, cold, at, town) is var (stashId, stashSpot))
        {
            cold.Engaged = true;
            _duties.Add(new TownDuty
            {
                Member = member, Faction = cold.Faction, Town = town, Kind = TownDutyKind.Stash, Target = stashId, Goal = stashSpot,
            });
            TaskStart.Walk(world, slot, stashSpot, SpeedMode.Walk, RowOf(TownDutyKind.Stash), TaskSource.Goal);
            return;
        }

        if (home[role] == 1 && _downed.Count > 0 && HomeIntruder(world, slot, cold, squad.HomeBuilding!) is >= 0 and var guest
            && StartLift(world, slot, cold, guest, town, TownDutyKind.HomeLift))
        {
            return;
        }

        bool mine = !town.IsNone && MyTown(world, squad, town);

        if (mine && intruders[role] == 1 && _downed.Count > 0 && Intruder(world, slot, cold, town) is >= 0 and var body
            && StartLift(world, slot, cold, body, town, TownDutyKind.Lift))
        {
            return;
        }

        if (LiftsCorpse(world, slot, cold, duties.Corpse[role] == 1, duties.FindCorpse[role] == 1, town)) return;

        if (mine && confiscate[role] == 1 && Contraband(world, town) is var (cage, prisoner) && cage is not null)
        {
            cold.Engaged = true;
            var goal = Flat(cage.Position);
            _duties.Add(new TownDuty
            {
                Member = member, Body = prisoner, Faction = cold.Faction, Town = town, Kind = TownDutyKind.Confiscate, Target = cage.Id, Goal = goal,
            });
            TaskStart.Walk(world, slot, goal, SpeedMode.Walk, RowOf(TownDutyKind.Confiscate), TaskSource.Goal);
            return;
        }

        if (food[role] == 1) FetchFood(world, squad, member, slot, cold, at, town, foodTown);
    }

    /// <summary>
    /// 244 Fetching food: from a store of the target or current town (a player-side member only its own faction's), when no sensed ally fights; a
    /// ditch first when the food does not fit.
    /// </summary>
    void FetchFood(World world, Squad squad, CharacterId member, int slot, CharacterCold cold, Vector3 at, CampaignTown town, CampaignTown foodTown)
    {
        // 243 needs no own town (FUN_1408cb650); a player-side member takes only from its own faction's stores (FUN_1408dac20). 244 starts
        // only with room for the item (state 82,
        // FUN_14059bd80: the inventory, else the worn bag) and with no sensed ally fighting (state !12, AiSystem.AlliesFighting).
        if (HoldsFood(cold)
            || Store(world, at, foodTown, owner: cold.Faction == world.Player.Faction ? cold.Faction : -1, asker: cold) is not var (id, spot)
            || world.SystemOf<AiSystem>()?.AlliesFighting(world, slot) == true)
        {
            return;
        }

        if (!FoodFits(world, id, cold))
        {
            // 244's B list asks state 82 (room); its provider is row 114 Ditching to make space, whose plan is 93 into the nearest
            // storage (a2-039): the food run waits for the next look.
            if (!town.IsNone && DitchStore(world, squad, cold, at, register: true) is var (ditchId, ditchSpot))
                StartDitch(world, slot, cold, town, ditchId, ditchSpot);

            return;
        }

        cold.Engaged = true;
        _duties.Add(new TownDuty { Member = member, Faction = cold.Faction, Town = town, Kind = TownDutyKind.Food, Target = id, Goal = spot });
        TaskStart.Walk(world, slot, spot, SpeedMode.Walk, RowOf(TownDutyKind.Food), TaskSource.Goal);
    }

    /// <summary>
    /// The goals for a body already on the shoulder, whoever lifted it (a victim who died on the way, an arrest's target, a carry a save restored,
    /// which no errand is saved with): a free member takes up the first of its disposal goals that starts (<see cref="DisposeCarried"/>, with 106
    /// weighed too, as no goal's plan holds this body); with none it keeps the body, but a body that goal 148 lifted is put down there and left to
    /// that rescuer for a while (<see cref="CarrySystem.RefuseRescue"/>; engine choice, as the town duties' short put-down), so a rescuer that
    /// finds no bed after the lift does not stand holding it.
    /// </summary>
    void LookCarriers(World world, int every)
    {
        var table = world.Characters;
        foreach (int slot in _carriers)
        {
            if (!world.Turn(slot, every)) continue;
            var id = table.IdOf(slot);
            if (!FreeCarrier(table, slot) || Busy(id)) continue;
            var cold = table.Cold(slot)!;
            if (!table.TryResolveNext(cold.Carrying, out int b) || table.Cold(b) is not { Medical: not null } body || body.CarriedBy != id)
                continue;

            var at = table.Next[slot].Position;
            var duty = new TownDuty { Member = id, Body = cold.Carrying, Faction = cold.Faction, Town = AiSystem.TownAt(data, world, at),
                    Kind = TownDutyKind.Cage };
            if (!DisposeCarried(world, duty, slot, b, bed: true))
            {
                if (carry.RescuedBy148(world, slot) && carry.BeginPutDown(world, slot, TaskSource.Goal)) carry.RefuseRescue(world, id, cold.Carrying);
                continue;
            }

            cold.Engaged = true;
            _duties.Add(duty);
        }
    }

    /// <summary>
    /// The goal chooser over a carried body's disposal goals (<c>FUN_140510ef0</c> with the scorer <c>FUN_14050df10</c>, <b>Verified</b>): the
    /// candidates ranked by <see cref="RankDisposals"/>, the first that starts wins (the cage goals need a free cage). Without
    /// <paramref name="cageable"/> the cage goals are left out. 106 is weighed only with <paramref name="bed"/>: for a carrier whose body no goal's
    /// plan holds (<see cref="LookCarriers"/>). After an errand's own lift (273, 223, 138 / 137) the goal that lifted goes on with its plan, as the
    /// original runs a goal's plan task by task and chooses again only when it ends (docs/game/ai-tasks.md "The plan's lifecycle", Verified), so a
    /// corpse lifted for the disposal is not taken to a bed. False when none starts.
    /// </summary>
    bool DisposeCarried(World world, TownDuty d, int g, int b, bool cageable = true, bool bed = false)
    {
        var gc = world.Characters.Cold(g)!;
        if (world.Squads.Find(gc.SquadId) is not { } squad || population.GoalsOf(world, squad, AiSystem.RoleOf(squad, d.Member, gc)) is not { } goals)
            return false;

        foreach (var goal in RankDisposals(world, g, b, goals, cageable, bed))
        {
            bool started = goal.Task.Type switch
            {
                TaskType.TakeIntruderOutsideTown => ThrowOutCarried(world, g, d),
                TaskType.DeadGuysGoInThePot => DisposeOfCarried(world, g, d),
                TaskType.FindBedAndPutIn => BedCarried(world, g, d),
                _ => CageCarried(world, g, d),
            };
            if (started) return true;
        }

        return false;
    }

    /// <summary>
    /// The role's disposal goals for the body in slot <paramref name="b"/> in the chooser's order, each in its hours with a score above 0: 137 Corpse
    /// disposal for a dead body (list B 99) at <see cref="DisposalDesire"/>, 112 FIND_CAGE_AND_PUT_IN (a living body, its list B <c>!99</c>) and 281
    /// FIND_CAGE_AND_PUT_DEADGUY_IN (any body) at <see cref="CageDesire"/> times the cage finder's factor (<c>FUN_1408d0d30</c>: 1.0,
    /// <c>DAT_14167c308</c>, with a free cage, else 0; tested when the goal starts), 274 at <see cref="ThrowOutDesire"/> (no effect, so no finder
    /// factor, <c>FUN_14060fd80</c>), with <paramref name="bed"/> 106 FIND_BED_AND_PUT_IN at <see cref="BedSystem.PutInDesire"/>
    /// (<c>FUN_1405a0d50</c>: no death or power test, and its friendly test passes a body of the carrier's faction at once) times the bed search's
    /// factor (its finder, <c>FUN_1408cfe00</c>: tested when the goal starts), each times its package weight. A higher
    /// tier first (an urgent cage before 274), then the higher score; of equal scores the goal listed later (<see cref="AiSystem"/>'s rank).
    /// </summary>
    List<AiGoal> RankDisposals(World world, int g, int b, IReadOnlyList<AiGoal> goals, bool cageable, bool bed)
    {
        bool dead = world.Characters.Cold(b)!.Medical!.Dead;
        float cage = cageable ? CageDesire(world, g, b) : 0;
        float throwOut = ThrowOutDesire(world, g, b);
        float? disposal = null, putIn = null;
        int hour = world.Hour;
        var ranked = new List<(AiGoal Goal, float Score)>();
        for (int k = 0; k < goals.Count; k++)
        {
            var goal = goals[k];
            float desire = goal.Task.Type switch
            {
                TaskType.FindCageAndPutDeadguyIn => cage,
                TaskType.FindCageAndPutIn when !dead => cage,
                TaskType.TakeIntruderOutsideTown => throwOut,
                TaskType.DeadGuysGoInThePot when dead => disposal ??= DisposalDesire(world, g),
                TaskType.FindBedAndPutIn when bed => putIn ??= Beds(world)?.PutInDesire(world, g) ?? 0,
                _ => 0,
            };
            float score = desire * goal.Weight;
            if (score <= 0 || !goal.InWindow(hour)) continue;

            int at = ranked.Count;
            while (at > 0 && RanksBefore(goal, score, ranked[at - 1])) at--;
            ranked.Insert(at, (goal, score));
        }

        return ranked.ConvertAll(r => r.Goal);
    }

    /// <summary>
    /// Whether a goal listed later ranks before an earlier candidate: a higher tier, or the same tier and a score at least as high.
    /// </summary>
    static bool RanksBefore(AiGoal goal, float score, (AiGoal Goal, float Score) earlier) =>
        goal.Task.Priority > earlier.Goal.Task.Priority || goal.Task.Priority == earlier.Goal.Task.Priority && score >= earlier.Score;

    /// <summary>
    /// 112 / 281 for the carried body of errand <paramref name="d"/>: the member walks it to the best free cage of its town; false with none free.
    /// </summary>
    bool CageCarried(World world, int g, TownDuty d)
    {
        if (Cage(world, world.Characters.Next[g].Position, d) is not { } cage) return false;

        (d.Kind, d.Target, d.Goal, d.Tries) = (TownDutyKind.Cage, cage.Id, Flat(cage.Position), 0);
        WalkToCage(world, g, cage);
        return true;
    }

    /// <summary>
    /// 106 for the carried body of errand <paramref name="d"/>: the member walks it to the bed the bed search finds, where the bed system lays it in
    /// (<see cref="BedSystem.PutIn"/>); false with no bed found.
    /// </summary>
    bool BedCarried(World world, int g, TownDuty d)
    {
        if (Beds(world)?.PutIn(world, g) is not { } bed) return false;

        (d.Kind, d.Target, d.Goal, d.Tries) = (TownDutyKind.Bed, bed.Id, Flat(bed.Position), 0);
        return true;
    }

    /// <summary>
    /// A corpse lift (138 / 137) whose disposal was busy at the lift's end tries it again only on the member's look turn (<see cref="Look"/>'s
    /// cadence); true while it waits for that turn.
    /// </summary>
    bool WaitingTurn(World world, TownDuty d, int g) => d.Tries > 0 && !world.Turn(g, world.Every(Settings.LookEveryTicks));

    /// <summary>
    /// 137's corpse lift whose disposal is only busy (each one in reach has its operator places held by other errands): the member keeps the body
    /// under this errand and tries again on its look turn until a place frees; the original's 137 plan
    /// holds its machine from the choice on, so a second carrier is not handed to another goal. False, and the errand ends, when the role lists no
    /// 137 in the hour, or no disposal it can get to is there at all.
    /// </summary>
    bool WaitsForDisposal(World world, TownDuty d, int g)
    {
        if (!ListsInHour(world, g, d.Member, TaskType.DeadGuysGoInThePot)) return false;
        var cold = world.Characters.Cold(g)!;
        var at = world.Characters.Next[g].Position;
        if (CorpseDisposal.Find(world, data, Disposals(world), g, at, m => cold.IsUnreachable(m.Id, world.Tick)) is null) return false;

        d.Tries = 1;
        return true;
    }

    /// <summary>
    /// Whether the member's role list holds goal <paramref name="type"/> in this hour.
    /// </summary>
    bool ListsInHour(World world, int g, CharacterId member, TaskType type)
    {
        var gc = world.Characters.Cold(g)!;
        if (world.Squads.Find(gc.SquadId) is not { } squad || population.GoalsOf(world, squad, AiSystem.RoleOf(squad, member, gc)) is not { } goals)
            return false;

        foreach (var goal in goals)
            if (goal.Task.Type == type && goal.InWindow(world.Hour)) return true;
        return false;
    }

    /// <summary>
    /// The bed system, resolved on first use (null in a world without one).
    /// </summary>
    BedSystem? Beds(World world) => _beds ??= world.SystemOf<BedSystem>();

    /// <summary>
    /// 274 Throwing out for the carried body of errand <paramref name="d"/> (list B 47 IS_CARRYING_SOMETHING): the member walks it to its town's
    /// exit point (<see cref="Outside"/>) and puts it down there. False when the carrier stands in no town (Meitou: the exit point is a town's).
    /// </summary>
    bool ThrowOutCarried(World world, int g, TownDuty d)
    {
        if (d.Town.IsNone) return false;

        (d.Kind, d.Goal, d.Tries) = (TownDutyKind.Out, Outside(world, d.Town), 0);
        TaskStart.Walk(world, g, d.Goal, SpeedMode.Free, RowOf(d.Kind), TaskSource.Goal);
        return true;
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
    /// A member that already carries someone and may start the cage errand: awake, not fighting, fleeing or held, doing nothing but its routine.
    /// </summary>
    static bool FreeCarrier(CharacterTable table, int slot)
    {
        if (!table.Next[slot].Alive
                || table.Next[slot].Task is not ((byte)CharacterTask.Idle or (byte)CharacterTask.Wander or (byte)CharacterTask.Follow
                        or (byte)CharacterTask.GoTo))
            return false;

        return table.Cold(slot) is { IsPlayer: false, Fleeing: false, Engaged: false, InCombat: false, Medical.Incapacitated: false,
                CarriedBy.IsNone: true } c && c.Fighter is not { IsAnimal: true };
    }

    /// <summary>
    /// A member that may start an errand: awake, not fighting, fleeing or held, able to carry, doing nothing but its routine.
    /// </summary>
    static bool Free(World world, int slot)
    {
        var table = world.Characters;
        if (!table.Next[slot].Alive
                || table.Next[slot].Task is not ((byte)CharacterTask.Idle or (byte)CharacterTask.Wander or (byte)CharacterTask.Follow
                        or (byte)CharacterTask.GoTo))
            return false;

        return table.Cold(slot) is { IsPlayer: false, Fleeing: false, Engaged: false, InCombat: false } c
                && CarrySystem.CanCarry(world, c, TaskType.PickupIntruders) && !HoldsHigherGoal(c);
    }

    /// <summary>
    /// A duty is a priority-2 goal: the goal chooser (<c>FUN_140510ef0</c>) tries player orders, jobs, urgent perma-jobs and the priority-3 goals
    /// first (Observed), so a member whose AI holds an urgent goal does not take one. Engine stand-in: jobs and perma-jobs show as a task other than
    /// idle, wander, follow or go-to, which <see cref="Free"/> already turns away.
    /// </summary>
    static bool HoldsHigherGoal(CharacterCold c) => c.Ai?.Goal?.Task.Priority is TaskPriority.Urgent or TaskPriority.Obedience;

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
    bool MyTown(World world, Squad squad, CampaignTown town)
    {
        if (town.Site >= 0)
            return town.Site == squad.Town || town.Site < data.SiteFactions.Count && data.SiteFactions[town.Site] == squad.Faction;

        if (squad.Ai is { CurrentClass: PackageClass.OccupyTown } board && board.Destination == town) return true;
        return world.Buildings.FindTown(town.PlayerTown) is { } t && t.Owner == squad.Faction;
    }

    /// <summary>
    /// Lifts the body in slot <paramref name="body"/> for an intruder errand of <paramref name="kind"/> (<see cref="TownDutyKind.Lift"/> or
    /// <see cref="TownDutyKind.HomeLift"/>); false when the lift does not start.
    /// </summary>
    bool StartLift(World world, int slot, CharacterCold cold, int body, CampaignTown town, TownDutyKind kind)
    {
        var table = world.Characters;
        var id = table.IdOf(body);
        if (!carry.BeginPickUp(world, slot, id, CharacterTask.Idle, source: TaskSource.Goal, row: RowOf(kind)))
            return false;

        cold.Engaged = true;
        _reserved.Add(id);
        _duties.Add(new TownDuty { Member = table.IdOf(slot), Body = id, Faction = cold.Faction, Town = town, Kind = kind });
        return true;
    }

    /// <summary>
    /// 223's finder (<c>FUN_14099c110</c>, docs/game/ai-tasks.md): the nearest knocked-out character the member senses
    /// (<see cref="CharacterFacts.Perceives"/>) within its sight inside the squad's home building, not carried, caged or in a bed (special state
    /// 0), not one another errand lifts or carries or the member gave up on (<see cref="GaveUp"/>), and not one the member regards as friendly (the
    /// sensed filter leaves out SENSE_ALLY; a dead one is tagged dead alone and fails it too). -1: none.
    /// </summary>
    int HomeIntruder(World world, int slot, CharacterCold cold, Meitou.Data.World.TownBuilding home)
    {
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
    /// The nearest downed intruder the member senses (<see cref="CharacterFacts.Perceives"/>, 273's finder walks the sensed list) within its sight
    /// in its town's built-up area, not one it gave up on (<see cref="GaveUp"/>); -1: none. Meitou: one in a bed is left alone (the original skips
    /// only the caged, so guards threw patients out of rented beds, issue 751).
    /// </summary>
    int Intruder(World world, int slot, CharacterCold cold, CampaignTown town)
    {
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
    static bool HoldsFood(CharacterCold cold) =>
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
    (string Id, Vector2 Spot)? StashStore(World world, Squad squad, CharacterCold cold, Vector3 at, CampaignTown town)
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
    /// The cage walk's row is also the kidnapper's (KidnapSystem), whose walk is started again over a fight: left out.
    /// </summary>
    static readonly HashSet<TaskType> ErrandRows = [.. Enum.GetValues<TownDutyKind>().Where(k => k != TownDutyKind.Cage).Select(RowOf)];

    /// <summary>
    /// Whether a walk's row is an errand's (<see cref="RowOf"/>, not the cage walk's, which the kidnapper's shares): a fight may interrupt it, the
    /// errand waits for the fight's end and walks on (<see cref="Ai.AiSystem"/>'s pre-emption).
    /// </summary>
    internal static bool IsErrandRow(TaskType row) => ErrandRows.Contains(row);

    /// <summary>
    /// The row an errand's walks act out (<see cref="TaskStart.Walk(World, int, Vector2, byte, TaskType, TaskSource)"/>): the goal of the package it
    /// stands in for.
    /// </summary>
    static TaskType RowOf(TownDutyKind kind) => kind switch
    {
        TownDutyKind.Lift => TaskType.PickupIntrudersTown,
        TownDutyKind.Out => TaskType.TakeIntruderOutsideTown,
        TownDutyKind.Cage => TaskType.FindCageAndPutIn,
        TownDutyKind.Food => TaskType.AquireFoodAtHomebase,
        TownDutyKind.Confiscate => TaskType.LootStolenGoods,
        TownDutyKind.LootFood => TaskType.LootFoodAndStuff,
        TownDutyKind.Shoo => TaskType.ShooStrangersOutOfMyBuilding,
        TownDutyKind.Ransack => TaskType.RunAroundTownLookingForPeople,
        TownDutyKind.Ditch => TaskType.GetRidOfResourcesInMyInventory,
        TownDutyKind.HomeLift => TaskType.PickupIntruders,
        TownDutyKind.HomeOut => TaskType.TakeIntruderOutside,
        TownDutyKind.CorpseLift => TaskType.FindADeadGuy,
        TownDutyKind.Corpse => TaskType.DeadGuysGoInThePot,
        TownDutyKind.Bed => TaskType.FindBedAndPutIn,
        _ => TaskType.PutLootInStorage,
    };

    /// <summary>
    /// One tick of an errand; false when it is over.
    /// </summary>
    bool Step(World world, TownDuty d)
    {
        var table = world.Characters;
        if (!table.TryResolveNext(d.Member, out int g) || table.Cold(g) is not { Medical.Incapacitated: false } gc)
            return End(world, d);
        // A fight (a reaction, or what a shoo's dialogue turned into) beats the errand: its walks would end it (a new task drops the fight task), so
        // it waits for the fight to be over. a player character's food run is a goal its fight replaced: the decision step chooses again
        if (gc.InCombat) return d.PlayerSide && gc.IsPlayer ? End(world, d) : true;
        ref var gn = ref table.Next[g];
        if (d.Kind is TownDutyKind.Food or TownDutyKind.LootFood) return StepFood(world, d, g, gc);
        if (d.Kind is TownDutyKind.Stash or TownDutyKind.Ditch) return StepStash(world, d, g, gc);
        if (d.Kind == TownDutyKind.Confiscate) return StepConfiscate(world, d, g, gc);
        if (d.Kind == TownDutyKind.Shoo) return StepShoo(world, d, g, gc);
        if (d.Kind == TownDutyKind.Ransack) return StepRansack(world, d, g);

        // A body that died on the way goes on into the cage only with 281 listed (112 needs !99 isCarriedDudeDead); a lifted one goes on to the
        // disposal ranking; 224 and 274 carry out whatever they hold (their update, slot 7 FUN_1403356e0, has no death test).
        if (!table.TryResolveNext(d.Body, out int b) || table.Cold(b) is not { Medical: { } bodyMedical } body || body.IsImprisoned
            || bodyMedical.Dead && !KeepsDead(world, d, gc))
        {
            carry.LetGo(world, g);
            return End(world, d);
        }

        if (d.Kind is TownDutyKind.Lift or TownDutyKind.HomeLift or TownDutyKind.CorpseLift)
        {
            if (gn.Task == (byte)CharacterTask.PickUp)
                return gc.LiftTime >= 0 || (d.Next += world.TickSeconds) <= Settings.LiftSeconds || GiveUpLift(world, d, g);

            if (gc.Carrying != d.Body) return End(world, d);   // fought off or pushed away: the body lies there for the next look
            return d.Kind switch
            {
                TownDutyKind.HomeLift => CarryOut(world, d, g, b),
                // 138's lift needs no disposal: with no goal to take the body on, the carrier keeps it (docs/game/ai-tasks.md "Perma-jobs"); 137's waits
                // for a busy disposal.
                TownDutyKind.CorpseLift => WaitingTurn(world, d, g) || DisposeCarried(world, d, g, b) || WaitsForDisposal(world, d, g)
                    || End(world, d),
                _ => Dispose(world, d, g, b),
            };
        }

        if (gc.Carrying != d.Body) return End(world, d);
        if (d.Kind == TownDutyKind.Bed) return StepBed(world, d, g);
        if (d.Kind == TownDutyKind.Cage) return StepCage(world, d, g, b);
        if (d.Kind == TownDutyKind.Corpse) return StepCorpse(world, d, g);

        bool walking = gn.Task == (byte)CharacterTask.GoTo;
        if (Vector2.Distance(Flat(gn.Position), d.Goal) <= Settings.Arrive) return LetGo(world, d, g, refuse: false);
        if (walking) return true;
        if (++d.Tries > Settings.Walks) return LetGo(world, d, g, refuse: true);
        TaskStart.Walk(world, g, d.Goal, SpeedMode.Free, RowOf(d.Kind), TaskSource.Goal);
        return true;
    }

    /// <summary>
    /// Whether errand <paramref name="d"/> goes on with a body that has died: one carried out (224, 274) or to a bed (106: its desire and row 99's
    /// list B have no death test), one lifted (the disposal goals are ranked again, <see cref="Dispose"/>), or one on its way to a cage with 281
    /// listed.
    /// </summary>
    bool KeepsDead(World world, TownDuty d, CharacterCold gc)
    {
        if (d.Kind is TownDutyKind.HomeLift or TownDutyKind.HomeOut or TownDutyKind.Out or TownDutyKind.CorpseLift or TownDutyKind.Corpse
                or TownDutyKind.Bed)
            return true;

        return gc.Carrying == d.Body && (d.Kind == TownDutyKind.Lift || d.Kind == TownDutyKind.Cage && ListsDeadCage(world, gc, d.Member));
    }

    /// <summary>
    /// 106's errand while the body is on the shoulder: the bed system walks the member to the bed and lays the body in (which ends the errand: it
    /// carries nothing then). A walk that is over without that (another task or a fight ended it, the bed was taken or could not be reached) starts
    /// again with a fresh bed search, at most <see cref="TownDutySettings.Walks"/> times; then, or with no bed found, the errand ends and the member
    /// keeps the body for the next look.
    /// </summary>
    bool StepBed(World world, TownDuty d, int g)
    {
        if (Beds(world) is not { } beds) return End(world, d);
        if (beds.PuttingIn(d.Member)) return true;
        if (++d.Tries > Settings.Walks || beds.PutIn(world, g) is not { } bed) return End(world, d);

        (d.Target, d.Goal) = (bed.Id, Flat(bed.Position));
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

        TaskStart.Walk(world, g, d.Goal, SpeedMode.Walk, RowOf(d.Kind), TaskSource.Goal);
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
    /// there; walks that keep ending short mark the cage unreachable for its faction and the body is put down.
    /// </summary>
    bool StepCage(World world, TownDuty d, int g, int b)
    {
        var table = world.Characters;
        ref var gn = ref table.Next[g];

        // The carried-body desire is read every update of the plan: at 0 the cage goal drops out and the body is thrown out or put down.
        if ((world.Tick + g) % world.Every(Settings.LookEveryTicks) == 0 && CageDesire(world, g, b) <= 0)
            return Dispose(world, d, g, b, cageable: false);

        var cage = d.Target is { } id ? world.Fixtures.Find(id) : null;
        if (cage is null || !FixtureRegistry.IsFree(table, cage)) return PutDown(world, d, g);

        bool walking = gn.Task == (byte)CharacterTask.GoTo;
        if (FixtureRegistry.AtCage(cage, gn.Position, walking)) return LockUp(world, d, g, b, cage);
        if (walking) return true;
        if (++d.Tries > Settings.Walks)
        {
            world.Fixtures.MarkUnreachable(world, cage, d.Faction);
            return PutDown(world, d, g);
        }

        WalkToCage(world, g, cage);
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
    static void WalkToCage(World world, int g, Fixture cage) =>
        TaskStart.Walk(world, g, cage.Position, SpeedMode.Free, RowOf(TownDutyKind.Cage), TaskSource.Goal);

    /// <summary>
    /// What a member does with the body it has lifted: the first of its disposal goals that starts (<see cref="DisposeCarried"/>: a cage, 112 / 281,
    /// or the town's exit point, 274), else it puts the body down.
    /// </summary>
    bool Dispose(World world, TownDuty d, int g, int b, bool cageable = true) => DisposeCarried(world, d, g, b, cageable) || PutDown(world, d, g);

    /// <summary>
    /// After a 223 lift: with 224 listed (<c>Task_TakeIntruderOutside</c>) the body goes to the point past the home's first door
    /// (<see cref="AiSystem.LeavePoint"/>, <c>FUN_140296a40</c>) and is put down there; a home with no door has its town's exit point
    /// (<see cref="Outside"/>, <b>Verified</b> <c>FUN_140335480</c>). Without 224, or with neither door nor town, it goes on as a 273 body does
    /// (<see cref="Dispose"/>; engine choice: the original's carrier keeps it).
    /// </summary>
    bool CarryOut(World world, TownDuty d, int g, int b)
    {
        var gc = world.Characters.Cold(g)!;
        var squad = world.Squads.Find(gc.SquadId);
        if (squad?.HomeBuilding is not { } home
            || !population.GoalDuty(world, squad, AiSystem.RoleOf(squad, d.Member, gc), TaskType.TakeIntruderOutside))
            return Dispose(world, d, g, b);

        // A home with no door: its town's exit point (FUN_140335480).
        if (AiSystem.LeavePoint(world, home) is not { } door)
        {
            if (d.Town.IsNone) return Dispose(world, d, g, b);

            door = Outside(world, d.Town);
        }

        (d.Kind, d.Goal, d.Tries) = (TownDutyKind.HomeOut, door, 0);
        TaskStart.Walk(world, g, d.Goal, SpeedMode.Free, RowOf(d.Kind), TaskSource.Goal);
        return true;
    }

    /// <summary>
    /// The member puts the body down short of its goal; the body is left alone for <see cref="TownDutySettings.RefuseHours"/>.
    /// </summary>
    bool PutDown(World world, TownDuty d, int g)
    {
        TaskStart.Stand(world.Characters, g);
        carry.BeginPutDown(world, g, TaskSource.Goal);
        _refused.Add((d.Body, world.GameHours + Settings.RefuseHours));
        return End(world, d);
    }

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
    /// The member gives up a lift whose body it never reached (<see cref="TownDutySettings.LiftSeconds"/>): it leaves that body alone for
    /// <see cref="TownDutySettings.RefuseHours"/>, and the body stays free for the others.
    /// </summary>
    bool GiveUpLift(World world, TownDuty d, int g)
    {
        TaskStart.Stand(world.Characters, g);
        _gaveUp.Add((d.Member, d.Body, world.GameHours + Settings.RefuseHours));
        return End(world, d);
    }

    /// <summary>
    /// Ends an errand: the member goes back to its routine (the AI takes it again).
    /// </summary>
    static bool End(World world, TownDuty d)
    {
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
    /// The desire of 137 Corpse disposal (AI slot 43 <c>FUN_1406415a0</c>: 1.0, <c>DAT_14167c308</c>) times its finder's (<c>FUN_1408d1090</c>: 1.0
    /// when <see cref="CorpseDisposal.Find"/> finds a disposal, else 0; <b>Verified</b>, the registration in <c>FUN_140319a60</c> disassembled).
    /// </summary>
    float DisposalDesire(World world, int g) => DisposalFor(world, g) is null ? 0 : 1;

    /// <summary>
    /// 138 Find corpses as a goal or as 137's provider (its list A 99 <c>isCarriedDudeDead</c>): a member whose role lists 138 (desire
    /// <c>FUN_140599480</c>: 1.0, 0 on Hold or while carrying, <b>Verified</b>), or 137 with a disposal found, lifts the corpse
    /// <see cref="CorpseDisposal.FindCorpse"/> finds. False when it lifts none.
    /// </summary>
    bool LiftsCorpse(World world, int slot, CharacterCold cold, bool dispose, bool find, CampaignTown town)
    {
        if (!dispose && !find || (cold.Orders & CharacterOrders.Hold) != 0) return false;
        if (!find && DisposalFor(world, slot) is null) return false;

        var at = world.Characters.Next[slot].Position;
        var inReach = world.Fixtures.TownCandidates(world, data, cold, at);
        float sight = AggroSystem.RangeOf(cold, Settings.Sight);
        var body = CorpseDisposal.FindCorpse(world, slot, f => inReach(f.Position), sight, id => _reserved.Contains(id) || Refused(id));
        return !body.IsNone && StartLift(world, slot, cold, body.Slot, town, TownDutyKind.CorpseLift);
    }

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
    /// The disposal the carrier in slot <paramref name="g"/> takes a body to (<see cref="CorpseDisposal.Find"/>), leaving out one it cannot get to
    /// (its path-failure list, 20 s) and one whose operator places other errands hold (the squad blackboard's claims, <c>FUN_1402690e0</c>).
    /// </summary>
    CorpseMachine? DisposalFor(World world, int g, TownDuty? self = null)
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
    /// 137 for the carried corpse of errand <paramref name="d"/>: the member walks it onto the disposal's floor; false with none found.
    /// </summary>
    bool DisposeOfCarried(World world, int g, TownDuty d)
    {
        if (DisposalFor(world, g, d) is not { } machine) return false;

        (d.Kind, d.Target, d.Machine, d.Goal, d.Tries) = (TownDutyKind.Corpse, machine.Id, machine, Flat(machine.Position), 0);
        WalkToDisposal(world, g, machine);
        return true;
    }

    /// <summary>
    /// The walk to a disposal, onto its floor (one in a lab's upper storey).
    /// </summary>
    static void WalkToDisposal(World world, int g, CorpseMachine machine) =>
        TaskStart.Walk(world, g, machine.Position, SpeedMode.Free, RowOf(TownDutyKind.Corpse), TaskSource.Goal);

    /// <summary>
    /// One tick of 137's walk (136 at the machine, its list B <c>isAtLocation</c>): at the disposal the body is fed in
    /// (<see cref="CorpseDisposal.Feed"/>); walks that keep ending short put the machine on the carrier's path-failure list for 20 s (the original's
    /// ignore map, <c>FUN_1405b0290</c>) and the carrier keeps the body for the next look.
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

        WalkToDisposal(world, g, machine);
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
    /// Whether the carrier's package lists 281 FIND_CAGE_AND_PUT_DEADGUY_IN for its role (any carried body, dead included).
    /// </summary>
    bool ListsDeadCage(World world, CharacterCold carrier, CharacterId id) =>
        world.Squads.Find(carrier.SquadId) is { } s
                && population.GoalDuty(world, s, AiSystem.RoleOf(s, id, carrier), TaskType.FindCageAndPutDeadguyIn);

    /// <summary>
    /// The best free cage among the carrier's town candidates (<see cref="FixtureRegistry.CageCandidates"/>) by
    /// <see cref="FixtureRegistry.FindCage"/>'s weights (its squad's home x100), not another errand's.
    /// </summary>
    Fixture? Cage(World world, Vector3 at, TownDuty self)
    {
        if (!world.Characters.TryResolveNext(self.Member, out int slot) || world.Characters.Cold(slot) is not { } carrier)
            return null;

        if (self.Town.Site >= 0) world.Fixtures.RegisterSite(world, data, self.Town.Site);
        var candidate = world.Fixtures.CageCandidates(world, data, carrier, at);
        return world.Fixtures.FindCage(world, carrier, at, FixtureRegistry.HomeOf(world, carrier), f => !candidate(f)
                || _duties.Any(o => !ReferenceEquals(o, self) && o.Target == f.Id));
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
    static int GrabFood(Inventory store, CharacterCold c, Inventory into)
    {
        bool animal = c.Medical is { HasStatsObject: false };
        var item = store.Items.FirstOrDefault(i => i.ItemFunction == FeedSystem.RawFoodFunction && Edible(i, animal, c.Race))
            ?? store.Items.FirstOrDefault(i => i.IsFood);
        return item is null ? 1 : InventoryCommands.MoveInto(store, item, 1, into, bagFirst: true, out _);   // nothing listed is no failure of room
    }

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
                _foodWaits.Add((d.Member, world.GameHours + Settings.RefuseHours));   // no room for it: not again for a while
            world.Machines.Touch();
            return End(world, d);
        }

        if (walking) return true;
        if (++d.Tries > Settings.Walks) return End(world, d);
        TaskStart.Walk(world, g, d.Goal, SpeedMode.Walk, RowOf(d.Kind), TaskSource.Goal);
        return true;
    }

    /// <summary>
    /// Whether an errand holds <paramref name="body"/>. Plain loops, not lambdas: Contraband looks at every cage each pass, and the captures made two
    /// closures per cage.
    /// </summary>
    bool HasDutyOn(CharacterId body)
    {
        foreach (var d in _duties)
            if (d.Body == body) return true;
        return false;
    }

    /// <summary>
    /// Whether the inventory holds an item stolen from <paramref name="faction"/>.
    /// </summary>
    bool CarriesStolen(Inventory inventory, int faction)
    {
        foreach (var item in inventory.Items)
            if (item.IsStolenFor(faction, data)) return true;
        return false;
    }

    /// <summary>
    /// <c>AI::findPrisonerWithContraband</c> (<c>FUN_1408cf6a0</c>, goal 212): the first cage of the town, in list order (no distance weighting),
    /// whose occupant carries a stolen item and is not another errand's. Engine choice: the original's room test for the item is left to the take.
    /// </summary>
    (Fixture? Cage, CharacterId Prisoner) Contraband(World world, CampaignTown town)
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
        TaskStart.Walk(world, g, d.Goal, SpeedMode.Walk, RowOf(d.Kind), TaskSource.Goal);
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
    CharacterId Stranger(World world, int slot, CharacterCold cold, Squad squad)
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
            TaskStart.Walk(world, g, to, SpeedMode.Walk, RowOf(d.Kind), TaskSource.Goal);

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
    /// <c>AI::findRandomBuildingInTown</c> (<c>FUN_1408cb510</c>, goal 216): the squad's mission town (its blackboard's, else the campaign target it
    /// marches on), else the town the member stands in; one of its standing buildings with an interior, drawn uniformly at random among those the
    /// planner would reach: row 156 Entering building (the only provider of effect 107, which goal 216 wants) needs DESTINATION_IS_ACCESSIBLE (59:
    /// <c>hasAnOpenDoor</c>) and DESTINATION_NOT_BLOCKED_BY_FORTIFICATIONS (90: no closed gate between), <b>Observed</b>. The door sub-steps are
    /// planned (<see cref="EntryTo"/>: open the door, row 72, or bash it, row 81); a building with no way in is left out of the draw. A closed gate
    /// on the route is planned as for an attacker (<see cref="Locks.LockSystem.GateEntry"/>: bash it, row 123 / 290, or pass an unlocked one, row
    /// 122). Null: none.
    /// </summary>
    (Meitou.Data.World.TownBuilding Building, string? Door)? RansackTarget(World world, Squad squad, int slot)
    {
        int site = squad.Ai is { MissionTown: >= 0 } b ? b.MissionTown : squad.Ai is { Destination: { Site: >= 0 } dest } ? dest.Site
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
        TaskStart.Walk(world, g, d.Goal, SpeedMode.Run, RowOf(d.Kind), TaskSource.Goal);
        return true;
    }

    #endregion
}