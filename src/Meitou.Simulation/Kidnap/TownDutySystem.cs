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
    public float CageSlack { get; init; } = 200;
    /// <summary>
    /// Walks that end short before the member gives up (an intruder is then put down where it is).
    /// </summary>
    public int Walks { get; init; } = 4;
    /// <summary>
    /// Game seconds a lift may spend walking to the body before the member gives it up as put down short (no timer of the original's is known; a body
    /// it cannot reach would hold it forever).
    /// </summary>
    public float LiftSeconds { get; init; } = 60;
    /// <summary>
    /// Game hours a body put down short of its goal is left alone.
    /// </summary>
    public double RefuseHours { get; init; } = 2;
}

public enum TownDutyKind : byte { Lift, Out, Cage, Food, Confiscate, LootFood, Shoo, Ransack, Stash, Ditch, HomeLift, HomeOut }

/// <summary>
/// One member's errand of the <see cref="TownDutySystem"/>: an intruder it lifts and carries out or to a cage, or a store it fetches food from.
/// </summary>
public sealed class TownDuty
{
    public required CharacterId Member { get; init; }
    /// <summary>
    /// The intruder (none for a food run).
    /// </summary>
    public CharacterId Body { get; init; } = CharacterId.None;
    public required int Faction { get; init; }
    public required CampaignTown Town { get; init; }
    public TownDutyKind Kind { get; set; }
    public Vector2 Goal { get; set; }
    /// <summary>
    /// The cage's fixture id, or the store's (a machine's site id, else a container's fixture id).
    /// </summary>
    public string? Target { get; set; }
    public int Tries { get; set; }
    /// <summary>
    /// A shoo's start (game hours) and its next event (game seconds of the errand); a confiscation's seconds at the cage; a lift's seconds walking to
    /// the body.
    /// </summary>
    public double Since { get; set; }
    public float Next { get; set; }
    /// <summary>
    /// A food run of a character of the player's side (243 from <c>Character::setupAI</c>): a player order ends it without being undone.
    /// </summary>
    public bool PlayerSide { get; init; }

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
        if (Kind is TownDutyKind.Confiscate or TownDutyKind.Lift or TownDutyKind.HomeLift) h.Add(Next);
    }
}

/// <summary>
/// Goals of a squad's town the AI's act-outs do not cover (docs/game/carry-loot.md 3.2, ai-tasks.md "Finders of the jobs"), for the members of the
/// squads the AI runs whose running package lists them for the member's role in the hour (<see cref="PopulationSystem.GoalDuty"/>). All serial, in
/// <c>SlowWorld</c> after the carry system.
/// <list type="bullet">
/// <item><b>273 Remove Intruder</b> (finder <c>FUN_14099c250</c>, <b>Verified</b> by decompile): only while the member stands in a town that is its
/// home town, the town its occupation holds, or a town of its faction (<c>Ownerships::isMyTown</c>, KenshiLib names); the intruder is knocked out, in
/// that town, not carried or caged, and (an engine reading of the sensed-list classes) not of the member's faction or an ally. The original's last
/// test, <c>amInsideTownWalls</c>, is read as the town's built-up area (<see cref="BuiltUp"/>). The member lifts it, then</item>
/// <item><b>112 Find and put in cage</b> (a living body) or <b>281</b> (any body), while the package lists it: the best free cage in the town (the
/// original's finder; an occupier may use the occupied town's, its use test passing for that town, <c>FUN_140621230</c>), where the intruder is
/// locked up with the put-in's usual sentence (from a bounty with the cage owner's faction, docs/game/economy.md 11.5; none: served, so the jailer
/// frees it); else</item>
/// <item><b>274 Throwing out</b> (<c>Task_TakeIntruderOutsideTown</c>): carried to the town's point outside its gates (<see cref="TownExit"/>, the
/// same for every carrier) and put down.</item>
/// <item><b>223 Remove Home Intruder</b> (finder <c>FUN_14099c110</c>): a knocked-out character in the squad's home building
/// (<see cref="HomeIntruder"/>) is lifted, then with <b>224</b> (<c>Task_TakeIntruderOutside</c>) carried past the home's first door
/// (<see cref="AiSystem.LeavePoint"/>) and put down; without 224 it goes on as 273's body does.</item>
/// <item><b>243 Getting food</b> (finder <c>FUN_1408cb650</c>, <c>BuildingFinder_LootFoodAndStuff</c>; then 244 <c>Task_grabOneFood</c>): a member
/// holding no food walks to the nearest store with food in the town and takes one item of it (the goal holds while it has food, list A
/// <c>gotAFoodItem</c>). The finder searches the blackboard's target town (<c>Blackboard.Destination</c>), else the town the member stands in, else
/// the 3 x 3 zone cells round it; it need not be the squad's own, and a player-side member takes only from its faction's stores.</item>
/// </list>
/// </summary>
public sealed class TownDutySystem(PopulationData data, PopulationSystem population, CarrySystem carry, TownDutySettings? settings = null)
        : ITickSystem, IStateHashed, IDecisionOwner
{
    readonly List<TownDuty> _duties = [];
    readonly List<int> _downed = [];
    // The slots carrying someone at the look.
    readonly List<int> _carriers = [];
    readonly HashSet<CharacterId> _reserved = [];
    // Bodies put down short of their goal: left alone until this game hour, in the order they were put down.
    readonly List<(CharacterId Body, double Until)> _refused = [];
    // Player-side characters whose last food run found no room for the item: no new run until this game hour.
    readonly List<(CharacterId Member, double Until)> _foodWaits = [];

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
    public IReadOnlyList<TownDuty> Duties => _duties;

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
        why = null;
        var table = world.Characters;
        int faction = world.Player.Faction;
        if (faction < 0 || c is not { Medical: { Dead: false, Incapacitated: false } med, Race: { } race } || !(c.IsPlayer || c.Faction == faction))
        {
            why = "not the player's";
            return 0;
        }
        bool animal = !med.HasStatsObject;
        if (animal && (world.Player.SquadAi & SquadAi.Feed) == 0)
        {
            why = "Feed Animals is off";
            return 0;
        }
        bool hungry = med.Fed <= 0 && med.Hunger <= FeedSystem.FoodOnGroundBelow && !(c.InCombat && med.Hunger >= FeedSystem.FoodOnGroundInFight);
        bool occupied = Occupations(world);
        if (!hungry && !occupied)
        {
            why = "not hungry";
            return 0;
        }
        var id = table.IdOf(s);
        if (c.Engaged || c.Fleeing || c.PickingUpItem is not null || !c.CarriedBy.IsNone || c.InSomething != HeldState.None || Busy(id))
        {
            why = "busy";
            return 0;
        }
        if (FeedSystem.CarriesFood(c, race, animal) || _foodWaits.Exists(w => w.Member == id && w.Until > world.GameHours))
        {
            why = "has food, or no room for it";
            return 0;
        }
        var at = table.Next[s].Position;
        var town = AiSystem.TownAt(data, world, at);
        if (town == CampaignTown.None)
        {
            why = "not in a town";
            return 0;
        }
        int owner = town.PlayerTown > 0 ? world.Buildings.FindTown(town.PlayerTown)?.Owner ?? -1 : town.Site < data.SiteFactions.Count
                ? data.SiteFactions[town.Site] : -1;
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
    /// Whether any squad occupies a town (the occupied branch of 243's desire), once a tick.
    /// </summary>
    bool Occupations(World world)
    {
        if (_occupationsTick != world.Tick) (_occupations,
                _occupationsTick) = (world.Squads.All.Any(q => q.Ai is { CurrentClass: PackageClass.OccupyTown }), world.Tick);
        return _occupations;
    }

    bool _occupations;
    long _occupationsTick = -1;

    void StartFood(World world, int s, CharacterCold c, (CampaignTown Town, string Id, Vector2 Spot) store)
    {
        c.Engaged = true;
        _duties.Add(new TownDuty { Member = world.Characters.IdOf(s), Faction = c.Faction, Town = store.Town, Kind = TownDutyKind.Food,
                Target = store.Id, Goal = store.Spot, PlayerSide = true });
        TaskStart.Walk(world, s, store.Spot, SpeedMode.Walk, RowOf(TownDutyKind.Food), TaskSource.Goal);
    }

    public void RegisterSources(DecisionSystem decisions) => decisions.Register(new GettingFood(this));

    /// <summary>
    /// 243 Getting food of a player character (priority 3, weight 0.5, <see cref="FoodWant"/>): it runs while its food run does.
    /// </summary>
    sealed class GettingFood(TownDutySystem owner) : IDecisionSource
    {
        public string Name => "243 Getting food";
        public DecisionStep Step => DecisionStep.Goals;
        public int Key => 243;
        public float Weight => 0.5f;

        public DecisionCheck CanRun(World world, int slot)
        {
            var id = world.Characters.IdOf(slot);
            if (owner.Busy(id))
                return owner._duties.Exists(d => d.Member == id && d.Kind == TownDutyKind.Food) ? DecisionCheck.Runs : DecisionCheck.No("busy");
            return owner.FoodWant(world, slot, world.Characters.Cold(slot)!, out _, out string? why) > 0 ? DecisionCheck.Run : DecisionCheck.No(why);
        }

        public float Desire(World world, int slot) =>
            owner.Busy(world.Characters.IdOf(slot)) ? 1 : owner.FoodWant(world, slot, world.Characters.Cold(slot)!, out _, out _);

        public void Start(World world, int slot)
        {
            var cold = world.Characters.Cold(slot)!;
            if (owner.FoodWant(world, slot, cold, out var store, out _) > 0) owner.StartFood(world, slot, cold, store);
        }
    }

    // ------------------------------------------------------------------ the look

    void Look(World world, int every)
    {
        var table = world.Characters;
        _downed.Clear();
        _carriers.Clear();
        _reserved.Clear();
        foreach (var d in _duties)
            if (!d.Body.IsNone) _reserved.Add(d.Body);
        _refused.RemoveAll(r => r.Until <= world.GameHours);
        foreach (var r in _refused) _reserved.Add(r.Body);
        for (int s = 0; s < table.HighWater; s++)
        {
            if (!table.Next[s].Alive || table.Cold(s) is not { } c) continue;
            if (table.Next[s].Task == (byte)CharacterTask.PickUp && !c.CarryTarget.IsNone) _reserved.Add(c.CarryTarget);
            if (!c.Carrying.IsNone)
            {
                _reserved.Add(c.Carrying);
                _carriers.Add(s);
            }
            if (c is { Medical: { Unconscious: true, Dead: false } } && c.InSomething != HeldState.Imprisoned && CarrySystem.CanBeLifted(c))
                _downed.Add(s);
        }
        LookCarriers(world, every);
        Span<int> intruders = stackalloc int[5], food = stackalloc int[5], confiscate = stackalloc int[5], loot = stackalloc int[5],
                shoo = stackalloc int[5], ransack = stackalloc int[5], stash = stackalloc int[5],
            home = stackalloc int[5];
        foreach (var squad in world.Squads.All)
        {
            if (squad.Ai is null || squad.Members.Count == 0) continue;
            intruders.Fill(-1);
            food.Fill(-1);
            confiscate.Fill(-1);
            loot.Fill(-1);
            shoo.Fill(-1);
            ransack.Fill(-1);
            stash.Fill(-1);
            home.Fill(-1);
            foreach (var member in squad.Members)
            {
                if (!table.TryResolveNext(member, out int slot) || !world.Turn(slot, every) || !Free(world, slot) || Busy(member))
                    continue;
                var cold = table.Cold(slot)!;
                int role = (int)AiSystem.RoleOf(squad, member, cold);
                if (intruders[role] < 0)
                    intruders[role] = population.GoalDuty(world, squad, (AiRole)role, TaskType.PickupIntrudersTown) ? 1 : 0;
                if (food[role] < 0)
                    food[role] = population.GoalDuty(world, squad, (AiRole)role, TaskType.AquireFoodAtHomebase) ? 1 : 0;
                if (confiscate[role] < 0)
                    confiscate[role] = population.GoalDuty(world, squad, (AiRole)role, TaskType.LootStolenGoods) ? 1 : 0;
                if (loot[role] < 0)
                    loot[role] = population.GoalDuty(world, squad, (AiRole)role, TaskType.LootFoodAndStuff) ? 1 : 0;
                if (shoo[role] < 0) shoo[role] = squad.HomeBuilding is not null
                        && (population.GoalDuty(world, squad, (AiRole)role, TaskType.ShooStrangersOutOfMyBuilding)
                    || squad.Ai is { HomePublic: false }
                            && population.GoalDuty(world, squad, (AiRole)role, TaskType.ShooStrangersOutOfMyBuildingIfPrivate)) ? 1 : 0;
                if (ransack[role] < 0)
                    ransack[role] = population.GoalDuty(world, squad, (AiRole)role, TaskType.RunAroundTownLookingForPeople) ? 1 : 0;
                if (stash[role] < 0)
                    stash[role] = population.GoalDuty(world, squad, (AiRole)role, TaskType.PutLootInStorage) ? 1 : 0;
                if (home[role] < 0)
                    home[role] = squad.HomeBuilding is not null && population.GoalDuty(world, squad, (AiRole)role, TaskType.PickupIntruders) ? 1 : 0;
                if (ransack[role] == 1 && RansackTarget(world, squad, slot) is var (raid, door))
                {
                    cold.Engaged = true;
                    if (door is not null) _locks?.BashForErrand(cold, door);
                    if (cold.Fighter is { } armed) cold.DrawnWeapon = Combat.CombatSystem.MeleeDrawn(armed);   // B: hasWeaponEquipped
                    var goal = Flat(raid.Position);
                    _duties.Add(new TownDuty { Member = member, Faction = cold.Faction, Town = AiSystem.TownAt(data, world, raid.Position),
                            Kind = TownDutyKind.Ransack, Target = raid.Key, Goal = goal });
                    TaskStart.Walk(world, slot, goal, SpeedMode.Run, RowOf(TownDutyKind.Ransack), TaskSource.Goal);
                    continue;
                }
                if (intruders[role] == 0 && food[role] == 0 && confiscate[role] == 0 && loot[role] == 0 && shoo[role] == 0 && stash[role] == 0
                        && home[role] == 0)
                    continue;
                if (shoo[role] == 1 && Stranger(world, slot, cold, squad) is { IsNone: false } stranger)
                {
                    cold.Engaged = true;
                    _duties.Add(new TownDuty { Member = member, Body = stranger, Faction = cold.Faction,
                            Town = AiSystem.TownAt(data, world, table.Next[slot].Position), Kind = TownDutyKind.Shoo, Since = world.GameHours });
                    continue;
                }
                var at = table.Next[slot].Position;
                var town = AiSystem.TownAt(data, world, at);

                // 131 Looting food and 243 below search the blackboard's target town (Blackboard.Destination), else the current one, else the 3 x 3
                // zone cells round the member (FUN_1408cb650).
                var foodTown = squad.Ai is { Destination.IsNone: false } target ? target.Destination : town;
                if (loot[role] == 1 && !HoldsFood(cold) && Store(world, at, foodTown, cold.Faction, asker: cold) is var (lootId, lootSpot))
                {
                    cold.Engaged = true;
                    _duties.Add(new TownDuty { Member = member, Faction = cold.Faction, Town = town, Kind = TownDutyKind.LootFood, Target = lootId,
                            Goal = lootSpot });
                    TaskStart.Walk(world, slot, lootSpot, SpeedMode.Walk, RowOf(TownDutyKind.LootFood), TaskSource.Goal);
                    continue;
                }

                // 184 Ditching loot: stolen goods it carries, in whatever town it is in (the finder reads the current town only).
                if (stash[role] == 1 && !town.IsNone && StashStore(world, squad, cold, at, town) is var (stashId, stashSpot))
                {
                    cold.Engaged = true;
                    _duties.Add(new TownDuty { Member = member, Faction = cold.Faction, Town = town, Kind = TownDutyKind.Stash, Target = stashId,
                            Goal = stashSpot });
                    TaskStart.Walk(world, slot, stashSpot, SpeedMode.Walk, RowOf(TownDutyKind.Stash), TaskSource.Goal);
                    continue;
                }
                if (home[role] == 1 && _downed.Count > 0 && HomeIntruder(world, slot, cold, squad.HomeBuilding!) is >= 0 and var guest
                    && StartLift(world, slot, cold, guest, town, TownDutyKind.HomeLift))
                    continue;
                bool mine = !town.IsNone && MyTown(world, squad, town);
                if (mine && intruders[role] == 1 && _downed.Count > 0 && Intruder(world, slot, cold, town) is >= 0 and var body
                        && StartLift(world, slot, cold, body, town, TownDutyKind.Lift))
                    continue;
                if (mine && confiscate[role] == 1 && Contraband(world, town) is var (cage, prisoner) && cage is not null)
                {
                    cold.Engaged = true;
                    var goal = Flat(cage.Position);
                    _duties.Add(new TownDuty { Member = member, Body = prisoner, Faction = cold.Faction, Town = town, Kind = TownDutyKind.Confiscate,
                            Target = cage.Id, Goal = goal });
                    TaskStart.Walk(world, slot, goal, SpeedMode.Walk, RowOf(TownDutyKind.Confiscate), TaskSource.Goal);
                    continue;
                }

                // 243 needs no own town (FUN_1408cb650); a player-side member takes only from its own faction's stores (FUN_1408dac20). 244 starts
                // only with room for the item (state 82,
                // FUN_14059bd80: the inventory, else the worn bag) and with no sensed ally fighting (state !12, AiSystem.AlliesFighting).
                if (food[role] == 1 && !HoldsFood(cold)
                        && Store(world, at, foodTown, owner: cold.Faction == world.Player.Faction ? cold.Faction : -1, asker: cold) is var (id, spot)
                    && world.SystemOf<AiSystem>()?.AlliesFighting(world, slot) != true)
                {
                    if (!FoodFits(world, id, cold))
                    {
                        // 244's B list asks state 82 (room); its provider is row 114 Ditching to make space, whose plan is 93 into the nearest
                        // storage (a2-039): the food run waits for the next look.
                        if (!town.IsNone && DitchStore(world, squad, cold, at, register: true) is var (ditchId, ditchSpot))
                            StartDitch(world, slot, cold, town, ditchId, ditchSpot);
                        continue;
                    }
                    cold.Engaged = true;
                    _duties.Add(new TownDuty { Member = member, Faction = cold.Faction, Town = town, Kind = TownDutyKind.Food, Target = id,
                            Goal = spot });
                    TaskStart.Walk(world, slot, spot, SpeedMode.Walk, RowOf(TownDutyKind.Food), TaskSource.Goal);
                }
            }
        }
    }

    /// <summary>
    /// 112 FIND_CAGE_AND_PUT_IN and 281 FIND_CAGE_AND_PUT_DEADGUY_IN for a body already on the shoulder, whoever lifted it (a victim who died on the
    /// way, an arrest's target, a carry a save restored): the desire <c>FUN_1405a1140</c> reads only the carry flag and the carried handle, never
    /// whether it is dead, and neither goal has a list B (<b>Verified</b>). A free member whose package lists 281 (any body) or 112 (a living one,
    /// its list A <c>!99</c>) takes it to the best free cage of its town; with none it keeps the body (the goal has no finder result and does not
    /// plan).
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
            if (!table.TryResolveNext(cold.Carrying, out int b) || table.Cold(b) is not { Medical: { } medical } body || body.CarriedBy != id)
                continue;
            if (!(ListsDeadCage(world, cold, id) || !medical.Dead && ListsCage(world, cold, id)) || CageDesire(world, slot, b) <= 0)
                continue;
            var at = table.Next[slot].Position;
            var duty = new TownDuty { Member = id, Body = cold.Carrying, Faction = cold.Faction, Town = AiSystem.TownAt(data, world, at),
                    Kind = TownDutyKind.Cage };
            if (Cage(world, at, duty) is not { } cage) continue;
            (duty.Target, duty.Goal) = (cage.Id, Flat(cage.Position));
            cold.Engaged = true;
            _duties.Add(duty);
            WalkToCage(world, slot, cage);
        }
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

    bool Busy(CharacterId id)
    {
        foreach (var d in _duties)
            if (d.Member == id) return true;
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
    /// 223's finder (<c>FUN_14099c110</c>, docs/game/ai-tasks.md): the nearest knocked-out character within the member's sight inside the squad's
    /// home building, not carried, caged or in a bed (special state 0), not another errand's, and not one the member regards as friendly (the sensed
    /// filter leaves out SENSE_ALLY; a dead one is tagged dead alone and fails it too). -1: none.
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

            float d2 = Vector2.DistanceSquared(at, Flat(table.Next[v].Position));
            if (d2 <= best2) (best, best2) = (v, d2);
        }
        return best;
    }

    /// <summary>
    /// The nearest downed intruder within the member's sight in its town's built-up area (-1: none).
    /// </summary>
    int Intruder(World world, int slot, CharacterCold cold, CampaignTown town)
    {
        var next = world.Characters.Next;
        var at = Flat(next[slot].Position);
        float range = AggroSystem.RangeOf(cold, Settings.Sight), best2 = range * range;
        int best = -1;
        foreach (int v in _downed)
        {
            if (v == slot || _reserved.Contains(world.Characters.IdOf(v)) || carry.Friendly(cold, world.Characters.Cold(v)!))
                continue;
            var p = next[v].Position;
            float d2 = Vector2.DistanceSquared(at, Flat(p));
            if (d2 > best2 || AiSystem.TownAt(data, world, p) != town || !InsideWalls(world, town, p)) continue;
            (best, best2) = (v, d2);
        }
        return best;
    }

    /// <summary>
    /// The original's <c>amInsideTownWalls</c> &gt; 0 (docs/game/pathfinding.md "Gate codes"): the point's gate code is a walled region. Where the
    /// codes are not known (no navmesh labelled yet, off the mesh, a headless world) the town's built-up area stands in (<see cref="BuiltUp"/>).
    /// </summary>
    bool InsideWalls(World world, CampaignTown town, Vector3 p)
    {
        _locks ??= world.Systems.OfType<LockSystem>().FirstOrDefault();
        return _locks?.Regions?.CodeAt(p) is { } code and >= 0 ? code > 0 : BuiltUp(world, town, Flat(p));
    }

    LockSystem? _locks;

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

    readonly Dictionary<string, Data.GameRecord?> _gateFunctions = new(StringComparer.Ordinal);

    /// <summary>
    /// <c>Town::hasGates</c> (slot 94, Town <c>+0x3b0</c> set; 0 for TownBase), <b>engine reading</b>: a gate
    /// (<see cref="Meitou.Data.World.TownBuildings.IsGate"/>) among the town's level buildings, or a complete one among a player town's sites.
    /// </summary>
    bool HasGates(World world, CampaignTown town)
    {
        if (town.Site >= 0)
            return data.Sites[town.Site].Buildings.Any(b => data.Db.Find(b.BuildingId) is { } r
                    && Data.World.TownBuildings.IsGate(data.Db, r, _gateFunctions));
        foreach (var site in world.Buildings.All)
            if (site.Town == town.PlayerTown && site.Complete && data.Db.Find(site.Record) is { } r
                    && Data.World.TownBuildings.IsGate(data.Db, r, _gateFunctions))
                return true;
        return false;
    }

    /// <summary>
    /// The intruder finder's stand-in for <c>amInsideTownWalls</c> where the gate codes are unknown (<b>engine reading</b>): a placed town's radius,
    /// a player town's farthest building from its centre. A body put down at the exit point is normally outside it.
    /// </summary>
    bool BuiltUp(World world, CampaignTown town, Vector2 p) => Vector2.Distance(p, Centre(world, town)) <= Reach(world, town);

    Vector2 Centre(World world, CampaignTown town) =>
        town.Site >= 0 ? Flat(data.Sites[town.Site].Position) : world.Buildings.FindTown(town.PlayerTown) is { } t ? Flat(t.Position) : default;

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
        if (town.Site >= 0) return TownExit.Of(data.Sites[town.Site], _locks?.Regions, world.Fixtures.IsDestroyed);
        return TownExit.Of(Centre(world, town), ExitCandidates(world, town));
    }

    IEnumerable<(Vector2, bool)> ExitCandidates(World world, CampaignTown town)
    {
        foreach (var site in world.Buildings.All)
        {
            if (site.Town != town.PlayerTown || !site.Complete || data.Db.Find(site.Record) is not { } r) continue;
            bool gate = Data.World.TownBuildings.IsGate(data.Db, r, _gateFunctions) && !(_locks?.Regions?.CodeAt(site.Position) > 0);
            yield return (Flat(site.Position), gate || r.GetBool("links together") && r.GetReferences("interior").Count > 0);
        }
    }

    static bool HoldsFood(CharacterCold cold) =>
        cold.Inventory is { } inv && (inv.Items.Any(i => i.IsFood) || inv.Backpack?.Contents is { } bag && bag.Items.Any(i => i.IsFood));

    /// <summary>
    /// The nearest store of the town with food in it (<c>BuildingFinder_LootFoodAndStuff</c>, its filter only partly read: buildings with an
    /// inventory holding food): a complete player storage of the player town, or a container that is not locked inside the town. Its id and where the
    /// member walks. With <paramref name="owner"/> (a player-side run) only stores of that faction; for an <paramref name="animal"/> raw food counts
    /// too and a store inside another building is skipped (the finder's animal tests).
    /// </summary>
    (string Id, Vector2 Spot)? Store(World world, Vector3 at, CampaignTown town, int looter = -1, int owner = -1, bool animal = false,
            RaceData? race = null, CharacterCold? asker = null)
    {
        var here = Flat(at);
        (string, Vector2)? best = null;
        float bestD = float.MaxValue;
        if (town.PlayerTown > 0 || town.IsNone)
            foreach (var m in world.Machines.All)
            {
                if (m.Storage is not { } s || !s.Items.Any(i => Edible(i, animal, race))
                        || world.Buildings.Find(m.Site) is not { Complete: true } site
                                || !InScope(world, town, at, CampaignTown.OfPlayer(site.Town), site.Position))
                    continue;
                if (owner >= 0 && site.Owner != owner || animal && site.Parent is not null || asker?.IsUnreachable(m.Site, world.Tick) == true)
                    continue;
                float d = Vector2.DistanceSquared(here, Flat(site.Position));
                if (d < bestD) (best, bestD) = ((m.Site, Flat(site.Position)), d);
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

    // The storage facts of a container's record, by record id: function 2 or 13 (BF_RESOURCE_STORAGE, BF_GENERAL_STORAGE), its itemtype limit and its
    // limit inventory list. Derived from the data.
    readonly Dictionary<string, (bool Storage, Data.Fcs.FcsRecordType Type, string[] Limit)> _storageRecords = new(StringComparer.Ordinal);

    (bool Storage, Data.Fcs.FcsRecordType Type, string[] Limit) StorageOf(Fixture f)
    {
        if (_storageRecords.TryGetValue(f.RecordId, out var known)) return known;
        var record = data.Db.Find(f.RecordId);
        (bool, Data.Fcs.FcsRecordType, string[]) made = (false, Data.Fcs.FcsRecordType.Item, []);
        if (record is not null
                && Data.World.TownBuildings.FunctionOf(data.Db, record,
                        _gateFunctions)?.GetInt("function") is Data.World.TownBuildings.ResourceStorageFunction
                                or Data.World.TownBuildings.GeneralStorageFunction)
            made = (true, (Data.Fcs.FcsRecordType)record.GetInt("itemtype limit", (int)Data.Fcs.FcsRecordType.Item),
                    [.. record.GetReferences("limit inventory").Select(r => r.TargetStringId)]);
        return _storageRecords[f.RecordId] = made;
    }

    /// <summary>
    /// A container takes a loose stack (<c>StorageBuilding</c> slot 0x4a0, <c>FUN_1402988c0</c>, <b>Observed</b>): the item's type is the container's
    /// <c>itemtype limit</c> and, when it has a <c>limit inventory</c> list, the item is in it, and there is room (a stack of it with space, or a
    /// free cell). Same test as <see cref="Production.ProductionSystem.Stores"/> for a machine.
    /// </summary>
    bool Takes(Fixture f, ItemInstance item)
    {
        var (storage, type, limit) = StorageOf(f);
        if (!storage || f.Contents is not { } box || item.Section is not ("main" or "backpack_content") || item.LockedOn || item.Contents is not null)
            return false;
        if (item.Type != type || limit.Length > 0 && Array.IndexOf(limit, item.Record) < 0) return false;
        int room = box.LimitOf(item);
        foreach (var other in box.Items)
            if (other.Record == item.Record && other.Quality == item.Quality && other.Mark == item.Mark && other.Section == "main"
                    && other.Quantity < room)
                return true;
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
        int townOwner = search.PlayerTown > 0 ? world.Buildings.FindTown(search.PlayerTown)?.Owner ?? -1 : search.Site < data.SiteFactions.Count
                ? data.SiteFactions[search.Site] : -1;
        bool Usable(int owner) => listed || owner == cold.Faction || townOwner < 0 || owner == townOwner;
        (string, Vector2)? best = null;
        float bestD = float.MaxValue;
        if (search.PlayerTown > 0)
            foreach (var m in world.Machines.All)
            {
                if (!m.Data.IsStorage || m.Storage is null || world.Buildings.Find(m.Site) is not { Complete: true } site
                        || site.Town != search.PlayerTown || !Usable(site.Owner))
                    continue;
                float d = Vector3.DistanceSquared(at, site.Position);
                if (d <= Production.ProductionSystem.JobRangeSq && d < bestD && loot.Any(i => Production.ProductionSystem.Stores(m, i)))
                    (best, bestD) = ((m.Site, Flat(site.Position)), d);
            }
        foreach (var f in world.Fixtures.All)
        {
            if (f.Contents is null || !Usable(f.Owner)) continue;
            float d = Vector3.DistanceSquared(at, f.Position);
            if (d <= Production.ProductionSystem.JobRangeSq && d < bestD && AiSystem.TownAt(data, world, f.Position) == search
                    && loot.Any(i => Takes(f, i)))
                (best, bestD) = ((f.Id, Flat(f.Position)), d);
        }
        return best;
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
            foreach (var item in (d.Kind == TownDutyKind.Ditch ? Production.ProductionSystem.Ditchable(inv) : LootOf(world, gc)).Where(i
                    => machine is not null ? Production.ProductionSystem.Stores(machine, i) : Takes(box!, i)).ToList())
            {
                inv.Remove(item);
                if (store.Add(item)) continue;
                if (!inv.Add(item)) world.GroundItems.Drop(item, gn.Position, 0);
                break;
            }
            if (machine is not null) world.Machines.Touch();
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

    // ------------------------------------------------------------------ one errand

    // The cage walk's row is also the kidnapper's (KidnapSystem), whose walk is started again over a fight: left out.
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

        // A body that died on the way goes on into the cage only with 281 listed (112 needs !99 isCarriedDudeDead); 224 carries out whatever it
        // holds.
        bool home = d.Kind is TownDutyKind.HomeLift or TownDutyKind.HomeOut;
        if (!table.TryResolveNext(d.Body, out int b) || table.Cold(b) is not { Medical: { } bodyMedical } body || body.IsImprisoned
            || bodyMedical.Dead && !home
                    && !(d.Kind is TownDutyKind.Lift or TownDutyKind.Cage && gc.Carrying == d.Body && ListsDeadCage(world, gc, d.Member)))
        {
            carry.LetGo(world, g);
            return End(world, d);
        }
        if (d.Kind is TownDutyKind.Lift or TownDutyKind.HomeLift)
        {
            if (gn.Task == (byte)CharacterTask.PickUp)
                return gc.LiftTime >= 0 || (d.Next += world.TickSeconds) <= Settings.LiftSeconds || PutDown(world, d, g);
            if (gc.Carrying != d.Body) return End(world, d);   // fought off or pushed away: the body lies there for the next look
            return home ? CarryOut(world, d, g, b) : Dispose(world, d, g, b);
        }
        if (gc.Carrying != d.Body) return End(world, d);
        if (d.Kind == TownDutyKind.Cage) return StepCage(world, d, g, b);

        bool walking = gn.Task == (byte)CharacterTask.GoTo;
        if (Vector2.Distance(Flat(gn.Position), d.Goal) <= Settings.Arrive)
        {
            carry.BeginPutDown(world, g, TaskSource.Goal);
            return End(world, d);
        }
        if (walking) return true;
        if (++d.Tries > Settings.Walks) return PutDown(world, d, g);
        TaskStart.Walk(world, g, d.Goal, SpeedMode.Free, RowOf(d.Kind), TaskSource.Goal);
        return true;
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
    /// What a member does with the body it has lifted: cages it (112 for a living body, 281 for any, while the carried-body desire
    /// <see cref="CageDesire"/> is above 0 and a cage is free), else throws it out (274), else puts it down. Meitou tries the cage first whatever the
    /// desires of 112/281 and 274 are (the desire of 274 is <b>Unknown</b>).
    /// </summary>
    bool Dispose(World world, TownDuty d, int g, int b, bool cageable = true)
    {
        var table = world.Characters;
        var gc = table.Cold(g)!;
        ref var gn = ref table.Next[g];
        var squad = world.Squads.Find(gc.SquadId);
        int role = squad is null ? 0 : (int)AiSystem.RoleOf(squad, d.Member, gc);

        // 112 needs !99 isCarriedDudeDead (a living body only); 281 has no such list, so it cages the living and the dead.
        if (cageable && squad is not null
                && (ListsDeadCage(world, gc, d.Member) || table.Cold(b)!.Medical is { Dead: false }
                        && population.GoalDuty(world, squad, (AiRole)role, TaskType.FindCageAndPutIn))
            && CageDesire(world, g, b) > 0 && Cage(world, gn.Position, d) is { } cage)
        {
            (d.Kind, d.Target, d.Goal, d.Tries) = (TownDutyKind.Cage, cage.Id, Flat(cage.Position), 0);
            WalkToCage(world, g, cage);
            return true;
        }
        if (squad is not null && population.GoalDuty(world, squad, (AiRole)role, TaskType.TakeIntruderOutsideTown))
        {
            (d.Kind, d.Goal, d.Tries) = (TownDutyKind.Out, Outside(world, d.Town), 0);
            TaskStart.Walk(world, g, d.Goal, SpeedMode.Free, RowOf(d.Kind), TaskSource.Goal);
            return true;
        }
        return PutDown(world, d, g);
    }

    /// <summary>
    /// After a 223 lift: with 224 listed (<c>Task_TakeIntruderOutside</c>) the body goes to the point past the home's first door
    /// (<see cref="AiSystem.LeavePoint"/>, <c>FUN_140296a40</c>) and is put down there; without 224, or with no door known, it goes on as a 273 body
    /// does (<see cref="Dispose"/>; engine choice: the original's carrier keeps it).
    /// </summary>
    bool CarryOut(World world, TownDuty d, int g, int b)
    {
        var gc = world.Characters.Cold(g)!;
        var squad = world.Squads.Find(gc.SquadId);
        if (squad?.HomeBuilding is not { } home || AiSystem.LeavePoint(world, home) is not { } door
            || !population.GoalDuty(world, squad, AiSystem.RoleOf(squad, d.Member, gc), TaskType.TakeIntruderOutside))
            return Dispose(world, d, g, b);

        (d.Kind, d.Goal, d.Tries) = (TownDutyKind.HomeOut, door, 0);
        TaskStart.Walk(world, g, d.Goal, SpeedMode.Free, RowOf(d.Kind), TaskSource.Goal);
        return true;
    }

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
    /// Whether the carrier's package lists 112 FIND_CAGE_AND_PUT_IN for its role (a living carried body).
    /// </summary>
    bool ListsCage(World world, CharacterCold carrier, CharacterId id) =>
        world.Squads.Find(carrier.SquadId) is { } s && population.GoalDuty(world, s, AiSystem.RoleOf(s, id, carrier), TaskType.FindCageAndPutIn);

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
    /// <c>AI::findPrisonerWithContraband</c> (<c>FUN_1408cf6a0</c>, goal 212): the first cage of the town, in list order (no distance weighting),
    /// whose occupant carries a stolen item and is not another errand's. Engine choice: the original's room test for the item is left to the take.
    /// </summary>
    // Plain loops, not lambdas: Contraband looks at every cage each pass, and the captures made two closures per cage.
    bool HasDutyOn(CharacterId body)
    {
        foreach (var d in _duties)
            if (d.Body == body) return true;
        return false;
    }

    bool CarriesStolen(Inventory inventory, int faction)
    {
        foreach (var item in inventory.Items)
            if (item.IsStolenFor(faction, data)) return true;
        return false;
    }

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
            carry.Confiscate(gc, table.Cold(p)!);
            return End(world, d);
        }
        if (walking) return true;
        if (++d.Tries > Settings.Walks) return End(world, d);
        TaskStart.Walk(world, g, d.Goal, SpeedMode.Walk, RowOf(d.Kind), TaskSource.Goal);
        return true;
    }

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
                if (table.Next[s].Alive && world.Sight.BuildingOf(s) is { } key)
                    (_insiders.TryGetValue(key, out var list) ? list : _insiders[key] = []).Add(s);
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

    AggroSystem? _aggro;
    // Who is inside each building, by building key, as of insidersTick (the shoo finder's pass).
    readonly Dictionary<string, List<int>> _insiders = new(StringComparer.Ordinal);
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

    readonly DoorPlan _doorPlan = new();

    const ulong RansackSalt = 0x5261_6E73_6163_6B21;

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

    bool PutDown(World world, TownDuty d, int g)
    {
        TaskStart.Stand(world.Characters, g);
        carry.BeginPutDown(world, g, TaskSource.Goal);
        _refused.Add((d.Body, world.GameHours + Settings.RefuseHours));
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

    static Vector2 Flat(Vector3 p) => new(p.X, p.Z);

    public void Hash(ref StateHasher hasher)
    {
        // only while one runs, so worlds without them hash as before
        if (_duties.Count == 0 && _refused.Count == 0 && _foodWaits.Count == 0) return;
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
    }
}