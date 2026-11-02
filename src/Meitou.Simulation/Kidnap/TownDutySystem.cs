using System.Numerics;
using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Carrying;
using Meitou.Simulation.Items;
using Meitou.Simulation.Locks;

namespace Meitou.Simulation.Kidnap;

/// <summary>Settings of the <see cref="TownDutySystem"/>; engine choices unless a doc label says otherwise.</summary>
public sealed record TownDutySettings
{
    /// <summary>Each member looks for intruders and food once in this many ticks, on its slot's turn.</summary>
    public int LookEveryTicks { get; init; } = 15;
    /// <summary>A member looks for an intruder within its sight (the sensed list's size is <b>Unknown</b>).</summary>
    public float Sight { get; init; } = AggroSystem.OutdoorSight;
    /// <summary>An intruder is put down this far past the town's built-up area (<b>Observed</b>: the original's exit point is 200 beyond a door of one of the town's buildings).</summary>
    public float OutsideTown { get; init; } = 200;
    /// <summary>A walk counts as arrived within this of its goal (a cage: its <c>use range</c> plus <see cref="CageReach"/>; a walk that ended within <see cref="CageSlack"/> of it does too).</summary>
    public float Arrive { get; init; } = 40;
    public float CageReach { get; init; } = 15;
    public float CageSlack { get; init; } = 200;
    /// <summary>Walks that end short before the member gives up (an intruder is then put down where it is).</summary>
    public int Walks { get; init; } = 4;
    /// <summary>Game hours a body put down short of its goal is left alone.</summary>
    public double RefuseHours { get; init; } = 2;
}

public enum TownDutyKind : byte { Lift, Out, Cage, Food, Confiscate, LootFood, Shoo, Ransack, Stash }

/// <summary>One member's errand of the <see cref="TownDutySystem"/>: an intruder it lifts and carries out or to a cage, or a store it fetches food from.</summary>
public sealed class TownDuty
{
    public required CharacterId Member { get; init; }
    /// <summary>The intruder (none for a food run).</summary>
    public CharacterId Body { get; init; } = CharacterId.None;
    public required int Faction { get; init; }
    public required CampaignTown Town { get; init; }
    public TownDutyKind Kind { get; set; }
    public Vector2 Goal { get; set; }
    /// <summary>The cage's fixture id, or the store's (a machine's site id, else a container's fixture id).</summary>
    public string? Target { get; set; }
    public int Tries { get; set; }
    /// <summary>A shoo's start (game hours) and its next event (game seconds of the errand); a confiscation's seconds at the cage.</summary>
    public double Since { get; set; }
    public float Next { get; set; }
    /// <summary>A food run of a character of the player's side (243 from <c>Character::setupAI</c>): a player order ends it without being undone.</summary>
    public bool PlayerSide { get; init; }

    internal void Hash(ref StateHasher h)
    {
        h.Add(Member); h.Add(Body); h.Add(Faction); h.Add(Town.Site); h.Add(Town.PlayerTown); h.Add((int)Kind); h.Add(Goal); h.Add(Tries);
        h.Add(Target is { } t ? Rng.StableHash(t) : 0UL);
        if (Kind == TownDutyKind.Shoo) { h.Add(Since); h.Add(Next); }
        if (PlayerSide) h.Add(0x50534964);
        if (Kind == TownDutyKind.Confiscate) h.Add(Next);
    }
}

/// <summary>
/// Goals of a squad's town the AI's act-outs do not cover (docs/game/carry-loot.md 3.2, ai-tasks.md "Finders of the jobs"), for the members of the squads the AI runs whose running
/// package lists them for the member's role in the hour (<see cref="PopulationSystem.GoalDuty"/>). All serial, in <c>SlowWorld</c> after the carry system.
/// <list type="bullet">
/// <item><b>273 Remove Intruder</b> (finder <c>FUN_14099c250</c>, <b>Verified</b> by decompile): only while the member stands in a town that is its home town, the town its occupation holds,
/// or a town of its faction (<c>Ownerships::isMyTown</c>, KenshiLib names); the intruder is knocked out, in that town, not carried or caged, and (an engine reading of the sensed-list classes)
/// not of the member's faction or an ally. The original's last test, <c>amInsideTownWalls</c>, is read as the town's built-up area (<see cref="BuiltUp"/>). The member lifts it, then</item>
/// <item><b>112 Find and put in cage</b> (a living body) or <b>281</b> (any body), while the package lists it: the best free cage in the town (the original's finder; an occupier may use the occupied town's, its use test passing
/// for that town, <c>FUN_140621230</c>), where the intruder is locked up with the put-in's usual sentence (from a bounty with the cage owner's faction,
/// docs/game/economy.md 11.5; none: served, so the jailer frees it); else</item>
/// <item><b>274 Throwing out</b> (<c>Task_TakeIntruderOutsideTown</c>): carried past the town's built-up area (<see cref="TownDutySettings.OutsideTown"/>) on the member's side and put down.</item>
/// <item><b>243 Getting food</b> (finder <c>FUN_1408cb650</c>, <c>BuildingFinder_LootFoodAndStuff</c>; then 244 <c>Task_grabOneFood</c>): a member holding no food walks to the nearest store with
/// food in the town and takes one item of it (the goal holds while it has food, list A <c>gotAFoodItem</c>). The town the original's finder searches is read as the one the member stands in;
/// it need not be the squad's own, and a player-side member takes only from its faction's stores.</item>
/// </list>
/// </summary>
public sealed class TownDutySystem(PopulationData data, PopulationSystem population, CarrySystem carry, TownDutySettings? settings = null) : ITickSystem, IStateHashed, IDecisionOwner
{
    readonly List<TownDuty> duties = [];
    readonly List<int> downed = [];
    // The slots carrying someone at the look.
    readonly List<int> carriers = [];
    readonly HashSet<CharacterId> reserved = [];
    // Bodies put down short of their goal: left alone until this game hour, in the order they were put down.
    readonly List<(CharacterId Body, double Until)> refused = [];
    // Player-side characters whose last food run found no room for the item: no new run until this game hour.
    readonly List<(CharacterId Member, double Until)> foodWaits = [];

    public TownDutySettings Settings { get; } = settings ?? new TownDutySettings();
    public IReadOnlyList<TownDuty> Duties => duties;

    public void SlowWorld(World world)
    {
        for (int k = 0; k < duties.Count;)
        {
            if (Step(world, duties[k])) k++;
            else duties.RemoveAt(k);
        }
        // Each member looks once in LookEveryTicks, on its slot's turn (slot mod the period): the looks spread over the ticks (docs/simulation.md "Spread sweeps").
        int every = Math.Max(1, Settings.LookEveryTicks), phase = (int)(world.Tick % every);
        Look(world, every, phase);
        LookPlayerFood(world, every, phase);
    }

    /// <summary>
    /// 243 Getting food for the player's side (<c>Character::setupAI</c> gives it to every character of the player's faction; desire AI slots 26, 24, 25, <b>Verified</b> by decompile): an
    /// animal only while the squad option Feed Animals is on; only in a town (slot 24), and in one of another faction only while the player's faction is resident there (town slot 88:
    /// the town's owner or its <c>factionsResidentHere</c>, which takes the player's faction when a player character stands in a building of the player's in that town; Meitou: the
    /// player owns a house there, <see cref="FixtureRegistry.OwnsPropertyIn"/>). In a town with occupiers while it senses an enemy: inside the walls it wants food whatever its hunger
    /// (desire 0.2), outside them in a town with gates not at all; else the 259 desire (slot 25): stomach empty, hunger at most 2.4, no food it would bite on it, not fighting unless
    /// hunger is below 1.2. It walks to the nearest store of the town owned by its faction with food in it and takes one item, which the eating rule then eats. A player character's
    /// goal is the decision step's (<see cref="GettingFood"/>, priority 3, weight 0.5); this pass runs it for the other characters of the player's faction while idle (they hold no
    /// jobs; engine choice).
    /// </summary>
    void LookPlayerFood(World world, int every, int phase)
    {
        int faction = world.Player.Faction;
        if (faction < 0) return;
        foodWaits.RemoveAll(w => w.Until <= world.GameHours);
        var table = world.Characters;
        for (int s = phase; s < table.HighWater; s += every)
        {
            if (!table.Next[s].Alive || table.Cold(s) is not { IsPlayer: false } c || c.Faction != faction) continue;
            var task = (CharacterTask)table.Next[s].Task;
            if (task != CharacterTask.Idle) continue;
            if (FoodWant(world, s, c, out var store, out _) > 0) StartFood(world, s, c, store);
        }
    }

    /// <summary>243's desire for a player-side character, 0 with <paramref name="why"/> when it does not want food now, and the store it would walk to (a pure read).</summary>
    float FoodWant(World world, int s, CharacterCold c, out (CampaignTown Town, string Id, Vector2 Spot) store, out string? why)
    {
        store = default;
        why = null;
        var table = world.Characters;
        int faction = world.Player.Faction;
        if (faction < 0 || c is not { Medical: { Dead: false, Incapacitated: false } med, Race: { } race } || !(c.IsPlayer || c.Faction == faction)) { why = "not the player's"; return 0; }
        bool animal = !med.HasStatsObject;
        if (animal && (world.Player.SquadAi & SquadAi.Feed) == 0) { why = "Feed Animals is off"; return 0; }
        bool hungry = med.Fed <= 0 && med.Hunger <= FeedSystem.FoodOnGroundBelow && !(c.InCombat && med.Hunger >= FeedSystem.FoodOnGroundInFight);
        bool occupied = Occupations(world);
        if (!hungry && !occupied) { why = "not hungry"; return 0; }
        var id = table.IdOf(s);
        if (c.Engaged || c.Fleeing || c.PickingUpItem is not null || !c.CarriedBy.IsNone || c.InSomething != HeldState.None || Busy(id)) { why = "busy"; return 0; }
        if (FeedSystem.CarriesFood(c, race, animal) || foodWaits.Exists(w => w.Member == id && w.Until > world.GameHours)) { why = "has food, or no room for it"; return 0; }
        var at = table.Next[s].Position;
        var town = AiSystem.TownAt(data, world, at);
        if (town == CampaignTown.None) { why = "not in a town"; return 0; }
        int owner = town.PlayerTown > 0 ? world.Buildings.FindTown(town.PlayerTown)?.Owner ?? -1 : town.Site < data.SiteFactions.Count ? data.SiteFactions[town.Site] : -1;
        if (owner != c.Faction && owner != faction && !world.Fixtures.OwnsPropertyIn(town.Site, faction)) { why = "a town the player does not live in"; return 0; }
        float desire = 1;
        if (occupied && AiSystem.Occupied(world, town) && SensesEnemy(world, s, c))
        {
            if (InsideWalls(world, town, at)) (hungry, desire) = (true, 0.2f);
            else if (HasGates(world, town)) { why = "outside an occupied town's walls"; return 0; }
        }
        if (!hungry) { why = "not hungry"; return 0; }
        if (Store(world, at, town, owner: c.Faction, animal: animal) is not var (storeId, spot)) { why = "no store with food"; return 0; }
        store = (town, storeId, spot);
        return desire;
    }

    /// <summary>Whether any squad occupies a town (the occupied branch of 243's desire), once a tick.</summary>
    bool Occupations(World world)
    {
        if (occupationsTick != world.Tick) (occupations, occupationsTick) = (world.Squads.All.Any(q => q.Ai is { CurrentClass: PackageClass.OccupyTown }), world.Tick);
        return occupations;
    }

    bool occupations;
    long occupationsTick = -1;

    void StartFood(World world, int s, CharacterCold c, (CampaignTown Town, string Id, Vector2 Spot) store)
    {
        c.Engaged = true;
        duties.Add(new TownDuty { Member = world.Characters.IdOf(s), Faction = c.Faction, Town = store.Town, Kind = TownDutyKind.Food, Target = store.Id, Goal = store.Spot, PlayerSide = true });
        TaskStart.Walk(world, s, store.Spot, SpeedMode.Walk, RowOf(TownDutyKind.Food), TaskSource.Goal);
    }

    public void RegisterSources(DecisionSystem decisions) => decisions.Register(new GettingFood(this));

    /// <summary>243 Getting food of a player character (priority 3, weight 0.5, <see cref="FoodWant"/>): it runs while its food run does.</summary>
    sealed class GettingFood(TownDutySystem owner) : IDecisionSource
    {
        public string Name => "243 Getting food";
        public DecisionStep Step => DecisionStep.Goals;
        public int Key => 243;
        public float Weight => 0.5f;

        public DecisionCheck CanRun(World world, int slot)
        {
            var id = world.Characters.IdOf(slot);
            if (owner.Busy(id)) return owner.duties.Exists(d => d.Member == id && d.Kind == TownDutyKind.Food) ? DecisionCheck.Runs : DecisionCheck.No("busy");
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

    void Look(World world, int every, int phase)
    {
        var table = world.Characters;
        downed.Clear();
        carriers.Clear();
        reserved.Clear();
        foreach (var d in duties) if (!d.Body.IsNone) reserved.Add(d.Body);
        refused.RemoveAll(r => r.Until <= world.GameHours);
        foreach (var r in refused) reserved.Add(r.Body);
        for (int s = 0; s < table.HighWater; s++)
        {
            if (!table.Next[s].Alive || table.Cold(s) is not { } c) continue;
            if (table.Next[s].Task == (byte)CharacterTask.PickUp && !c.CarryTarget.IsNone) reserved.Add(c.CarryTarget);
            if (!c.Carrying.IsNone) { reserved.Add(c.Carrying); carriers.Add(s); }
            if (c is { Medical: { Unconscious: true, Dead: false } } && c.InSomething == HeldState.None && CarrySystem.CanBeLifted(c)) downed.Add(s);
        }
        LookDeadCarriers(world, every, phase);
        Span<int> intruders = stackalloc int[5], food = stackalloc int[5], confiscate = stackalloc int[5], loot = stackalloc int[5], shoo = stackalloc int[5], ransack = stackalloc int[5], stash = stackalloc int[5];
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
            foreach (var member in squad.Members)
            {
                if (!table.TryResolveNext(member, out int slot) || slot % every != phase || !Free(world, slot) || Busy(member)) continue;
                var cold = table.Cold(slot)!;
                int role = (int)AiSystem.RoleOf(squad, member, cold);
                if (intruders[role] < 0) intruders[role] = population.GoalDuty(world, squad, (AiRole)role, TaskType.PICKUP_INTRUDERS_TOWN) ? 1 : 0;
                if (food[role] < 0) food[role] = population.GoalDuty(world, squad, (AiRole)role, TaskType.AQUIRE_FOOD_AT_HOMEBASE) ? 1 : 0;
                if (confiscate[role] < 0) confiscate[role] = population.GoalDuty(world, squad, (AiRole)role, TaskType.LOOT_STOLEN_GOODS) ? 1 : 0;
                if (loot[role] < 0) loot[role] = population.GoalDuty(world, squad, (AiRole)role, TaskType.LOOT_FOOD_AND_STUFF) ? 1 : 0;
                if (shoo[role] < 0) shoo[role] = squad.HomeBuilding is not null && (population.GoalDuty(world, squad, (AiRole)role, TaskType.SHOO_STRANGERS_OUT_OF_MY_BUILDING)
                    || squad.Ai is { HomePublic: false } && population.GoalDuty(world, squad, (AiRole)role, TaskType.SHOO_STRANGERS_OUT_OF_MY_BUILDING_IF_PRIVATE)) ? 1 : 0;
                if (ransack[role] < 0) ransack[role] = population.GoalDuty(world, squad, (AiRole)role, TaskType.RUN_AROUND_TOWN_LOOKING_FOR_PEOPLE) ? 1 : 0;
                if (stash[role] < 0) stash[role] = population.GoalDuty(world, squad, (AiRole)role, TaskType.PUT_LOOT_IN_STORAGE) ? 1 : 0;
                if (ransack[role] == 1 && RansackTarget(world, squad, slot) is { } raid)
                {
                    cold.Engaged = true;
                    if (cold.Fighter is { } armed) cold.DrawnWeapon = Combat.CombatSystem.MeleeDrawn(armed);   // B: hasWeaponEquipped
                    var goal = Flat(raid.Position);
                    duties.Add(new TownDuty { Member = member, Faction = cold.Faction, Town = AiSystem.TownAt(data, world, raid.Position), Kind = TownDutyKind.Ransack, Target = raid.Key, Goal = goal });
                    TaskStart.Walk(world, slot, goal, SpeedMode.Run, RowOf(TownDutyKind.Ransack), TaskSource.Goal);
                    continue;
                }
                if (intruders[role] == 0 && food[role] == 0 && confiscate[role] == 0 && loot[role] == 0 && shoo[role] == 0 && stash[role] == 0) continue;
                if (shoo[role] == 1 && Stranger(world, slot, cold, squad) is { IsNone: false } stranger)
                {
                    cold.Engaged = true;
                    duties.Add(new TownDuty { Member = member, Body = stranger, Faction = cold.Faction, Town = AiSystem.TownAt(data, world, table.Next[slot].Position), Kind = TownDutyKind.Shoo, Since = world.GameHours });
                    continue;
                }
                var at = table.Next[slot].Position;
                var town = AiSystem.TownAt(data, world, at);
                // 131 Looting food, in whatever town it is in (the original's target town, else the current one).
                if (loot[role] == 1 && !town.IsNone && !HoldsFood(cold) && Store(world, at, town, cold.Faction) is var (lootId, lootSpot))
                {
                    cold.Engaged = true;
                    duties.Add(new TownDuty { Member = member, Faction = cold.Faction, Town = town, Kind = TownDutyKind.LootFood, Target = lootId, Goal = lootSpot });
                    TaskStart.Walk(world, slot, lootSpot, SpeedMode.Walk, RowOf(TownDutyKind.LootFood), TaskSource.Goal);
                    continue;
                }
                // 184 Ditching loot: stolen goods it carries, in whatever town it is in (the finder reads the current town only).
                if (stash[role] == 1 && !town.IsNone && StashStore(world, squad, cold, at, town) is var (stashId, stashSpot))
                {
                    cold.Engaged = true;
                    duties.Add(new TownDuty { Member = member, Faction = cold.Faction, Town = town, Kind = TownDutyKind.Stash, Target = stashId, Goal = stashSpot });
                    TaskStart.Walk(world, slot, stashSpot, SpeedMode.Walk, RowOf(TownDutyKind.Stash), TaskSource.Goal);
                    continue;
                }
                if (town.IsNone) continue;
                bool mine = MyTown(world, squad, town);
                if (mine && intruders[role] == 1 && downed.Count > 0 && Intruder(world, slot, cold, town) is >= 0 and var body
                    && carry.BeginPickUp(world, slot, table.IdOf(body), CharacterTask.Idle, source: TaskSource.Goal, row: TaskType.PICKUP_INTRUDERS_TOWN))
                {
                    cold.Engaged = true;
                    reserved.Add(table.IdOf(body));
                    duties.Add(new TownDuty { Member = member, Body = table.IdOf(body), Faction = cold.Faction, Town = town, Kind = TownDutyKind.Lift });
                    continue;
                }
                if (mine && confiscate[role] == 1 && Contraband(world, town) is var (cage, prisoner) && cage is not null)
                {
                    cold.Engaged = true;
                    var goal = Flat(cage.Position);
                    duties.Add(new TownDuty { Member = member, Body = prisoner, Faction = cold.Faction, Town = town, Kind = TownDutyKind.Confiscate, Target = cage.Id, Goal = goal });
                    TaskStart.Walk(world, slot, goal, SpeedMode.Walk, RowOf(TownDutyKind.Confiscate), TaskSource.Goal);
                    continue;
                }
                // 243 needs no own town (FUN_1408cb650); a player-side member takes only from its own faction's stores (FUN_1408dac20). 244 starts only with room for the item (state 82,
                // FUN_14059bd80: the inventory, else the worn bag); state !12 (no sensed ally fighting) is not modelled: its per-ally score FUN_14099a190 is undecoded.
                if (food[role] == 1 && !HoldsFood(cold) && Store(world, at, town, owner: cold.Faction == world.Player.Faction ? cold.Faction : -1) is var (id, spot) && FoodFits(world, id, cold))
                {
                    cold.Engaged = true;
                    duties.Add(new TownDuty { Member = member, Faction = cold.Faction, Town = town, Kind = TownDutyKind.Food, Target = id, Goal = spot });
                    TaskStart.Walk(world, slot, spot, SpeedMode.Walk, RowOf(TownDutyKind.Food), TaskSource.Goal);
                }
            }
        }
    }

    /// <summary>
    /// 281 FIND_CAGE_AND_PUT_DEADGUY_IN for a body already on the shoulder, whoever lifted it (a victim who died on the way, an arrest's target): the desire <c>FUN_1405a1140</c> reads only the carry flag and
    /// the carried handle, never whether it is dead, and the goal has no list B (<b>Verified</b>). A free member whose package lists 281 and who carries a dead body takes it to the best free cage
    /// of its town; with none it keeps the body (the goal has no finder result and does not plan).
    /// </summary>
    void LookDeadCarriers(World world, int every, int phase)
    {
        var table = world.Characters;
        foreach (int slot in carriers)
        {
            if (slot % every != phase) continue;
            var id = table.IdOf(slot);
            if (!FreeCarrier(table, slot) || Busy(id)) continue;
            var cold = table.Cold(slot)!;
            if (!table.TryResolveNext(cold.Carrying, out int b) || table.Cold(b) is not { Medical.Dead: true } body || body.CarriedBy != id || !ListsDeadCage(world, cold, id) || CageDesire(world, slot, b) <= 0) continue;
            var at = table.Next[slot].Position;
            var duty = new TownDuty { Member = id, Body = cold.Carrying, Faction = cold.Faction, Town = AiSystem.TownAt(data, world, at), Kind = TownDutyKind.Cage };
            if (Cage(world, at, duty) is not { } cage) continue;
            (duty.Target, duty.Goal) = (cage.Id, Flat(cage.Position));
            cold.Engaged = true;
            duties.Add(duty);
            TaskStart.Walk(world, slot, duty.Goal, SpeedMode.Free, RowOf(TownDutyKind.Cage), TaskSource.Goal);
        }
    }

    /// <summary>A member that already carries someone and may start the cage errand: awake, not fighting, fleeing or held, doing nothing but its routine.</summary>
    static bool FreeCarrier(CharacterTable table, int slot)
    {
        if (!table.Next[slot].Alive || table.Next[slot].Task is not ((byte)CharacterTask.Idle or (byte)CharacterTask.Wander or (byte)CharacterTask.Follow or (byte)CharacterTask.GoTo)) return false;
        return table.Cold(slot) is { IsPlayer: false, Fleeing: false, Engaged: false, InCombat: false, Medical.Incapacitated: false, CarriedBy.IsNone: true } c && c.Fighter is not { IsAnimal: true };
    }

    /// <summary>A member that may start an errand: awake, not fighting, fleeing or held, able to carry, doing nothing but its routine.</summary>
    static bool Free(World world, int slot)
    {
        var table = world.Characters;
        if (!table.Next[slot].Alive || table.Next[slot].Task is not ((byte)CharacterTask.Idle or (byte)CharacterTask.Wander or (byte)CharacterTask.Follow or (byte)CharacterTask.GoTo)) return false;
        return table.Cold(slot) is { IsPlayer: false, Fleeing: false, Engaged: false, InCombat: false } c && CarrySystem.CanCarry(world, c, TaskType.PICKUP_INTRUDERS);
    }

    bool Busy(CharacterId id)
    {
        foreach (var d in duties) if (d.Member == id) return true;
        return false;
    }

    /// <summary>
    /// <c>Ownerships::isMyTown</c> (<c>FUN_1406211e0</c>, <b>Verified</b> by decompile): the squad's home town, the town its occupation holds (<c>Ownerships +0x80</c>, which the occupation's
    /// start declares), or a town of its faction.
    /// </summary>
    bool MyTown(World world, Squad squad, CampaignTown town)
    {
        if (town.Site >= 0) return town.Site == squad.Town || town.Site < data.SiteFactions.Count && data.SiteFactions[town.Site] == squad.Faction;
        if (squad.Ai is { CurrentClass: PackageClass.OccupyTown } board && board.Destination == town) return true;
        return world.Buildings.FindTown(town.PlayerTown) is { } t && t.Owner == squad.Faction;
    }

    /// <summary>The nearest downed intruder within the member's sight in its town's built-up area (-1: none).</summary>
    int Intruder(World world, int slot, CharacterCold cold, CampaignTown town)
    {
        var next = world.Characters.Next;
        var at = Flat(next[slot].Position);
        float range = AggroSystem.RangeOf(cold, Settings.Sight), best2 = range * range;
        int best = -1;
        foreach (int v in downed)
        {
            if (v == slot || reserved.Contains(world.Characters.IdOf(v)) || carry.Friendly(cold, world.Characters.Cold(v)!)) continue;
            var p = next[v].Position;
            float d2 = Vector2.DistanceSquared(at, Flat(p));
            if (d2 > best2 || AiSystem.TownAt(data, world, p) != town || !InsideWalls(world, town, p)) continue;
            (best, best2) = (v, d2);
        }
        return best;
    }

    /// <summary>
    /// The original's <c>amInsideTownWalls</c> &gt; 0 (docs/game/pathfinding.md "Gate codes"): the point's gate code is a walled region. Where the codes are not known (no navmesh labelled yet, off
    /// the mesh, a headless world) the town's built-up area stands in (<see cref="BuiltUp"/>).
    /// </summary>
    bool InsideWalls(World world, CampaignTown town, Vector3 p)
    {
        locks ??= world.Systems.OfType<Locks.LockSystem>().FirstOrDefault();
        return locks?.Regions?.CodeAt(p) is { } code and >= 0 ? code > 0 : BuiltUp(world, town, Flat(p));
    }

    Locks.LockSystem? locks;

    /// <summary>
    /// <c>amInsideTownWalls</c> &gt; 0 at a point, whatever town it is in (the context menu's Throw out of town): the gate code where known, else the built-up area of the town the point is in
    /// (<see cref="InsideWalls"/>); outside every town without codes, no. The host asks between ticks.
    /// </summary>
    public bool AmInsideTownWalls(World world, Vector3 p)
    {
        var town = AiSystem.TownAt(data, world, p);
        locks ??= world.Systems.OfType<Locks.LockSystem>().FirstOrDefault();
        return locks?.Regions?.CodeAt(p) is { } code and >= 0 ? code > 0 : !town.IsNone && BuiltUp(world, town, Flat(p));
    }

    /// <summary>The original's <c>SensoryData::numEnemies</c> &gt; 0 (<see cref="BodySystem.Senses"/>).</summary>
    static bool SensesEnemy(World world, int slot, CharacterCold c) =>
        world.SystemOf<Combat.CombatSystem>() is { } combat && BodySystem.Senses(world, combat, slot, c).Enemies > 0;

    readonly Dictionary<string, Meitou.Data.GameRecord?> gateFunctions = new(StringComparer.Ordinal);

    /// <summary>
    /// <c>Town::hasGates</c> (slot 94, Town <c>+0x3b0</c> set; 0 for TownBase), <b>engine reading</b>: a gate (<see cref="Meitou.Data.World.TownBuildings.IsGate"/>) among the town's level buildings, or a
    /// complete one among a player town's sites.
    /// </summary>
    bool HasGates(World world, CampaignTown town)
    {
        if (town.Site >= 0)
            return data.Sites[town.Site].Buildings.Any(b => data.Db.Find(b.BuildingId) is { } r && Meitou.Data.World.TownBuildings.IsGate(data.Db, r, gateFunctions));
        foreach (var site in world.Buildings.All)
            if (site.Town == town.PlayerTown && site.Complete && data.Db.Find(site.Record) is { } r && Meitou.Data.World.TownBuildings.IsGate(data.Db, r, gateFunctions)) return true;
        return false;
    }

    /// <summary>
    /// The intruder finder's stand-in for <c>amInsideTownWalls</c> where the gate codes are unknown (<b>engine reading</b>): a placed town's radius, a player
    /// town's farthest building from its centre. A body put down past it (<see cref="TownDutySettings.OutsideTown"/>) is not found again.
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

    /// <summary>Where an intruder is put down: past the built-up area by <see cref="TownDutySettings.OutsideTown"/> on the carrier's side, farther out until the ground is walkable.</summary>
    Vector2 Outside(World world, CampaignTown town, Vector3 from)
    {
        var centre = Centre(world, town);
        var dir = Flat(from) - centre;
        dir = dir.LengthSquared() > 1e-4f ? Vector2.Normalize(dir) : Vector2.UnitX;
        float distance = Reach(world, town) + Settings.OutsideTown;
        var p = centre + dir * distance;
        for (int tries = 0; tries < 4 && !world.Walkability.IsWalkable(p.X, p.Y); tries++, distance *= 1.25f) p = centre + dir * distance;
        return p;
    }

    static bool HoldsFood(CharacterCold cold) =>
        cold.Inventory is { } inv && (inv.Items.Any(i => i.IsFood) || inv.Backpack?.Contents is { } bag && bag.Items.Any(i => i.IsFood));

    /// <summary>
    /// The nearest store of the town with food in it (<c>BuildingFinder_LootFoodAndStuff</c>, its filter only partly read: buildings with an inventory holding food): a complete player storage
    /// of the player town, or a container that is not locked inside the town. Its id and where the member walks. With <paramref name="owner"/> (a player-side run) only stores of that
    /// faction; for an <paramref name="animal"/> raw food counts too and a store inside another building is skipped (the finder's animal tests).
    /// </summary>
    (string Id, Vector2 Spot)? Store(World world, Vector3 at, CampaignTown town, int looter = -1, int owner = -1, bool animal = false)
    {
        var here = Flat(at);
        (string, Vector2)? best = null;
        float bestD = float.MaxValue;
        if (town.PlayerTown > 0)
            foreach (var m in world.Machines.All)
            {
                if (m.Storage is not { } s || !s.Items.Any(i => Edible(i, animal)) || world.Buildings.Find(m.Site) is not { Complete: true } site || site.Town != town.PlayerTown) continue;
                if (owner >= 0 && site.Owner != owner || animal && site.Parent is not null) continue;
                float d = Vector2.DistanceSquared(here, Flat(site.Position));
                if (d < bestD) (best, bestD) = ((m.Site, Flat(site.Position)), d);
            }
        foreach (var f in world.Fixtures.All)
        {
            if (looter >= 0 && f.Owner == looter || owner >= 0 && f.Owner != owner) continue;
            if (f.Contents is not { } c || f.Lock is { EffectivelyLocked: true } || !c.Items.Any(i => Edible(i, animal)) || AiSystem.TownAt(data, world, f.Position) != town) continue;
            float d = Vector2.DistanceSquared(here, Flat(f.Position));
            if (d < bestD) (best, bestD) = ((f.Id, Flat(f.Position)), d);
        }
        return best;
    }

    /// <summary>Food the finder looks for: <c>item function</c> 3, for an animal also raw food (15).</summary>
    static bool Edible(ItemInstance item, bool animal) => item.IsFood || animal && item.ItemFunction == FeedSystem.RawFoodFunction;

    /// <summary>Whether the member has room for the item <see cref="StepFood"/> would take from the store (<see cref="Inventory.HasRoomFor"/>).</summary>
    bool FoodFits(World world, string storeId, CharacterCold cold) =>
        StoreInventory(world, storeId) is { } store && cold.Inventory is { } inv && store.Items.FirstOrDefault(i => Edible(i, false)) is { } item && inv.HasRoomFor(item);

    Inventory? StoreInventory(World world, string id) => world.Machines.Find(id)?.Storage ?? world.Fixtures.Find(id)?.Contents;

    /// <summary>
    /// What state 121 CARRYING_EXCESS_LOOT counts (<c>FUN_14059b7b0</c>): the items of the character's inventory and worn backpack that are stolen for it (<c>FUN_14079e550</c>, docs/game/carry-loot.md
    /// "Stolen ownership"); none for a lawless holder (the predicate's own exemption).
    /// </summary>
    public static IEnumerable<ItemInstance> LootOf(World world, CharacterCold c)
    {
        if (c.Inventory is not { } inv || world.SystemOf<Crime.CrimeSystem>()?.Factions.Lawless(c.Faction) == true) yield break;
        foreach (var item in inv.All())
            if (item.IsStolenFor(c.Faction, null)) yield return item;
    }

    /// <summary>State 121 CARRYING_EXCESS_LOOT (<see cref="LootOf"/>).</summary>
    public static bool CarriesLoot(World world, CharacterCold c) => LootOf(world, c).Any();

    // The storage facts of a container's record, by record id: function 2 or 13 (BF_RESOURCE_STORAGE, BF_GENERAL_STORAGE), its itemtype limit and its limit inventory list. Derived from the data.
    readonly Dictionary<string, (bool Storage, Meitou.Data.Fcs.FcsRecordType Type, string[] Limit)> storageRecords = new(StringComparer.Ordinal);

    (bool Storage, Meitou.Data.Fcs.FcsRecordType Type, string[] Limit) StorageOf(Fixture f)
    {
        if (storageRecords.TryGetValue(f.RecordId, out var known)) return known;
        var record = data.Db.Find(f.RecordId);
        (bool, Meitou.Data.Fcs.FcsRecordType, string[]) made = (false, Meitou.Data.Fcs.FcsRecordType.ITEM, []);
        if (record is not null && Meitou.Data.World.TownBuildings.FunctionOf(data.Db, record, gateFunctions)?.GetInt("function") is Meitou.Data.World.TownBuildings.ResourceStorageFunction or Meitou.Data.World.TownBuildings.GeneralStorageFunction)
            made = (true, (Meitou.Data.Fcs.FcsRecordType)record.GetInt("itemtype limit", (int)Meitou.Data.Fcs.FcsRecordType.ITEM), [.. record.GetReferences("limit inventory").Select(r => r.TargetStringId)]);
        return storageRecords[f.RecordId] = made;
    }

    /// <summary>
    /// A container takes a loose stack (<c>StorageBuilding</c> slot 0x4a0, <c>FUN_1402988c0</c>, <b>Observed</b>): the item's type is the container's <c>itemtype limit</c> and, when it has a
    /// <c>limit inventory</c> list, the item is in it, and there is room (a stack of it with space, or a free cell). Same test as <see cref="Production.ProductionSystem.Stores"/> for a machine.
    /// </summary>
    bool Takes(Fixture f, ItemInstance item)
    {
        var (storage, type, limit) = StorageOf(f);
        if (!storage || f.Contents is not { } box || item.Section is not ("main" or "backpack_content") || item.LockedOn || item.Contents is not null) return false;
        if (item.Type != type || limit.Length > 0 && Array.IndexOf(limit, item.Record) < 0) return false;
        int room = box.LimitOf(item);
        foreach (var other in box.Items)
            if (other.Record == item.Record && other.Quality == item.Quality && other.Mark == item.Mark && other.Section == "main" && other.Quantity < room) return true;
        return box.FindCell("main", item.Width, item.Height, out _, out _);
    }

    /// <summary>
    /// 184 Ditching loot's finder (<c>FUN_14059b040</c>): with stolen goods carried and a current town, the storage buildings (functions 2 and 13) of that town that take one of them, the nearest
    /// by squared 3D distance within <see cref="Production.ProductionSystem.JobRangeSq"/> (the default ROAM_FAR bound); its id (a player town's machine, else a container's fixture) and where the member
    /// walks. A member with a home town standing in a TOWN-type town reads the <i>home</i> town's storages (any owner); otherwise the current town's, owned by the member's faction or the town's
    /// (the nearest-allies finder's building test, <c>FUN_1408ca5d0</c> with <c>FUN_140621230</c>, <b>Observed</b>: a storage has an inventory, so only those two owners pass).
    /// </summary>
    (string Id, Vector2 Spot)? StashStore(World world, Squad squad, CharacterCold cold, Vector3 at, CampaignTown town)
    {
        var loot = LootOf(world, cold).ToList();
        if (loot.Count == 0) return null;
        bool listed = squad.Town >= 0 && town.Site >= 0 && data.Sites[town.Site].Town.Type == Meitou.Data.Gameplay.TownType.Town;
        var search = listed ? CampaignTown.OfSite(squad.Town) : town;
        if (search.Site >= 0) world.Fixtures.RegisterSite(world, data, search.Site);
        int townOwner = search.PlayerTown > 0 ? world.Buildings.FindTown(search.PlayerTown)?.Owner ?? -1 : search.Site < data.SiteFactions.Count ? data.SiteFactions[search.Site] : -1;
        bool Usable(int owner) => listed || owner == cold.Faction || townOwner < 0 || owner == townOwner;
        (string, Vector2)? best = null;
        float bestD = float.MaxValue;
        if (search.PlayerTown > 0)
            foreach (var m in world.Machines.All)
            {
                if (!m.Data.IsStorage || m.Storage is null || world.Buildings.Find(m.Site) is not { Complete: true } site || site.Town != search.PlayerTown || !Usable(site.Owner)) continue;
                float d = Vector3.DistanceSquared(at, site.Position);
                if (d <= Production.ProductionSystem.JobRangeSq && d < bestD && loot.Any(i => Production.ProductionSystem.Stores(m, i))) (best, bestD) = ((m.Site, Flat(site.Position)), d);
            }
        foreach (var f in world.Fixtures.All)
        {
            if (f.Contents is null || !Usable(f.Owner)) continue;
            float d = Vector3.DistanceSquared(at, f.Position);
            if (d <= Production.ProductionSystem.JobRangeSq && d < bestD && AiSystem.TownAt(data, world, f.Position) == search && loot.Any(i => Takes(f, i))) (best, bestD) = ((f.Id, Flat(f.Position)), d);
        }
        return best;
    }

    /// <summary>
    /// 184 at its storage (the plan's row 93 Filling machine, whose item choice is <b>Unknown</b>; engine choice: what list A <c>!121</c> asks for): every stolen item the storage takes goes in, whole
    /// stacks while there is room; what does not fit stays carried.
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
            foreach (var item in LootOf(world, gc).Where(i => machine is not null ? Production.ProductionSystem.Stores(machine, i) : Takes(box!, i)).ToList())
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
        if (++d.Tries > Settings.Walks) return End(world, d);
        TaskStart.Walk(world, g, d.Goal, SpeedMode.Walk, RowOf(d.Kind), TaskSource.Goal);
        return true;
    }

    // ------------------------------------------------------------------ one errand

    // The cage walk's row is also the kidnapper's (KidnapSystem), whose walk is started again over a fight: left out.
    static readonly HashSet<TaskType> ErrandRows = [.. Enum.GetValues<TownDutyKind>().Where(k => k != TownDutyKind.Cage).Select(RowOf)];

    /// <summary>
    /// Whether a walk's row is an errand's (<see cref="RowOf"/>, not the cage walk's, which the kidnapper's shares): a fight may interrupt it, the errand waits for the fight's end and walks on
    /// (<see cref="Ai.AiSystem"/>'s pre-emption).
    /// </summary>
    internal static bool IsErrandRow(TaskType row) => ErrandRows.Contains(row);

    /// <summary>The row an errand's walks act out (<see cref="TaskStart.Walk(World, int, Vector2, byte, TaskType, TaskSource)"/>): the goal of the package it stands in for.</summary>
    static TaskType RowOf(TownDutyKind kind) => kind switch
    {
        TownDutyKind.Lift => TaskType.PICKUP_INTRUDERS_TOWN,
        TownDutyKind.Out => TaskType.TAKE_INTRUDER_OUTSIDE_TOWN,
        TownDutyKind.Cage => TaskType.FIND_CAGE_AND_PUT_IN,
        TownDutyKind.Food => TaskType.AQUIRE_FOOD_AT_HOMEBASE,
        TownDutyKind.Confiscate => TaskType.LOOT_STOLEN_GOODS,
        TownDutyKind.LootFood => TaskType.LOOT_FOOD_AND_STUFF,
        TownDutyKind.Shoo => TaskType.SHOO_STRANGERS_OUT_OF_MY_BUILDING,
        TownDutyKind.Ransack => TaskType.RUN_AROUND_TOWN_LOOKING_FOR_PEOPLE,
        _ => TaskType.PUT_LOOT_IN_STORAGE,
    };

    /// <summary>One tick of an errand; false when it is over.</summary>
    bool Step(World world, TownDuty d)
    {
        var table = world.Characters;
        if (!table.TryResolveNext(d.Member, out int g) || table.Cold(g) is not { Medical.Incapacitated: false } gc) return End(world, d);
        // A fight (a reaction, or what a shoo's dialogue turned into) beats the errand: its walks would end it (a new task drops the fight task), so it waits for the fight to be over.
        if (gc.InCombat) return d.PlayerSide && gc.IsPlayer ? End(world, d) : true;   // a player character's food run is a goal its fight replaced: the decision step chooses again
        ref var gn = ref table.Next[g];
        if (d.Kind is TownDutyKind.Food or TownDutyKind.LootFood) return StepFood(world, d, g, gc);
        if (d.Kind == TownDutyKind.Stash) return StepStash(world, d, g, gc);
        if (d.Kind == TownDutyKind.Confiscate) return StepConfiscate(world, d, g, gc);
        if (d.Kind == TownDutyKind.Shoo) return StepShoo(world, d, g, gc);
        if (d.Kind == TownDutyKind.Ransack) return StepRansack(world, d, g);
        // A body that died on the way goes on into the cage only with 281 listed (112 needs !99 isCarriedDudeDead).
        if (!table.TryResolveNext(d.Body, out int b) || table.Cold(b) is not { Medical: { } bodyMedical } body || body.IsImprisoned
            || bodyMedical.Dead && !(d.Kind is TownDutyKind.Lift or TownDutyKind.Cage && gc.Carrying == d.Body && ListsDeadCage(world, gc, d.Member)))
        {
            carry.LetGo(world, g);
            return End(world, d);
        }
        if (d.Kind == TownDutyKind.Lift)
        {
            if (gn.Task == (byte)CharacterTask.PickUp) return true;
            if (gc.Carrying != d.Body) return End(world, d);   // fought off or pushed away: the body lies there for the next look
            return Dispose(world, d, g, b);
        }
        if (gc.Carrying != d.Body) return End(world, d);
        bool walking = gn.Task == (byte)CharacterTask.GoTo;
        float gap = Vector2.Distance(Flat(gn.Position), d.Goal);
        if (d.Kind == TownDutyKind.Cage)
        {
            // The carried-body desire is read every update of the plan: at 0 the cage goal drops out and the body is thrown out or put down.
            if ((world.Tick + g) % Math.Max(1, Settings.LookEveryTicks) == 0 && CageDesire(world, g, b) <= 0) return Dispose(world, d, g, b, cageable: false);
            var cage = d.Target is { } id ? world.Fixtures.Find(id) : null;
            if (cage is null || !FixtureRegistry.IsFree(table, cage)) return PutDown(world, d, g);
            if (gap <= cage.UseRange + Settings.CageReach || !walking && gap <= Settings.CageSlack)
            {
                TaskStart.Stand(table, g);
                carry.LetGo(world, g);
                if (!world.Fixtures.Imprison(world, d.Body, cage, 1, d.Member)) return End(world, d);
                if (body.IsPlayer) world.Post($"{body.Name} has been locked in a cage by {data.Factions[d.Faction].Name}");
                return End(world, d);
            }
        }
        else if (gap <= Settings.Arrive)
        {
            carry.BeginPutDown(world, g, TaskSource.Goal);
            return End(world, d);
        }
        if (walking) return true;
        if (++d.Tries > Settings.Walks)
        {
            if (d.Kind == TownDutyKind.Cage && d.Target is { } unreachable && world.Fixtures.Find(unreachable) is { } c) world.Fixtures.MarkUnreachable(world, c, d.Faction);
            return PutDown(world, d, g);
        }
        TaskStart.Walk(world, g, d.Goal, SpeedMode.Free, RowOf(d.Kind), TaskSource.Goal);
        return true;
    }

    /// <summary>
    /// What a member does with the body it has lifted: cages it (112 for a living body, 281 for any, while the carried-body desire <see cref="CageDesire"/> is above 0 and a cage is free), else
    /// throws it out (274), else puts it down. Meitou tries the cage first whatever the desires of 112/281 and 274 are (the desire of 274 is <b>Unknown</b>).
    /// </summary>
    bool Dispose(World world, TownDuty d, int g, int b, bool cageable = true)
    {
        var table = world.Characters;
        var gc = table.Cold(g)!;
        ref var gn = ref table.Next[g];
        var squad = world.Squads.Find(gc.SquadId);
        int role = squad is null ? 0 : (int)AiSystem.RoleOf(squad, d.Member, gc);
        // 112 needs !99 isCarriedDudeDead (a living body only); 281 has no such list, so it cages the living and the dead.
        if (cageable && squad is not null && (ListsDeadCage(world, gc, d.Member) || table.Cold(b)!.Medical is { Dead: false } && population.GoalDuty(world, squad, (AiRole)role, TaskType.FIND_CAGE_AND_PUT_IN))
            && CageDesire(world, g, b) > 0 && Cage(world, gn.Position, d) is { } cage)
        {
            (d.Kind, d.Target, d.Goal, d.Tries) = (TownDutyKind.Cage, cage.Id, Flat(cage.Position), 0);
            TaskStart.Walk(world, g, d.Goal, SpeedMode.Free, RowOf(d.Kind), TaskSource.Goal);
            return true;
        }
        if (squad is not null && population.GoalDuty(world, squad, (AiRole)role, TaskType.TAKE_INTRUDER_OUTSIDE_TOWN))
        {
            (d.Kind, d.Goal, d.Tries) = (TownDutyKind.Out, Outside(world, d.Town, gn.Position), 0);
            TaskStart.Walk(world, g, d.Goal, SpeedMode.Free, RowOf(d.Kind), TaskSource.Goal);
            return true;
        }
        return PutDown(world, d, g);
    }

    /// <summary>The desire for a carried body outside any town (<c>DAT_14167fad0</c>, <b>Verified</b>).</summary>
    public const float OutsideTownDesire = 0.1f;
    /// <summary>The desire of a law enforcer for a carried slave it holds no bounty on (<c>DAT_14169bb48</c>), of a dead body (<c>DAT_14167e8d8</c>) and of a living one (<c>DAT_141682ad0</c>).</summary>
    public const float PoliceSlaveDesire = 1.4f, DeadDesire = 0.5f, AliveDesire = 2;

    /// <summary>
    /// The desire of goals 112 and 281 for the body the carrier in slot <paramref name="carrier"/> holds (<c>AI::scorePutCarriedDudeInCage</c> <c>FUN_1405a1140</c> with <c>FUN_1405a0f10</c>,
    /// <b>Verified</b>), tests in the original's order: the carrier stands in no town 0.1; an animal 0; a law enforcer (<see cref="Crime.CrimeSystem.IsPoliceLike"/>) carrying a slave or escapee it has
    /// no bounty on 1.4; a body the carrier regards as friendly (<see cref="CarrySystem.Friendly"/>) 0; a dead one 0.5; else 2. The original also returns 0 while the squad's mission target
    /// (a hand on its AI data, <b>Observed</b>) is set and is not the town the carrier stands in: not modelled, the Meitou package fields do not map to it one for one.
    /// </summary>
    public float CageDesire(World world, int carrier, int body)
    {
        var table = world.Characters;
        if (table.Cold(carrier) is not { } c || table.Cold(body) is not { } b) return 0;
        if (AiSystem.TownAt(data, world, table.Next[carrier].Position).IsNone) return OutsideTownDesire;
        if (b.Fighter is { IsAnimal: true }) return 0;
        if (b.SlaveState != 0 && (b.Bounties?.AmountFor(c.Faction) ?? 0) < 1 && world.SystemOf<Crime.CrimeSystem>()?.IsPoliceLike(world, c) == true) return PoliceSlaveDesire;
        if (carry.Friendly(c, b)) return 0;
        return b.Medical is { Dead: true } ? DeadDesire : AliveDesire;
    }

    /// <summary>Whether the carrier's package lists 281 FIND_CAGE_AND_PUT_DEADGUY_IN for its role (any carried body, dead included).</summary>
    bool ListsDeadCage(World world, CharacterCold carrier, CharacterId id) =>
        world.Squads.Find(carrier.SquadId) is { } s && population.GoalDuty(world, s, AiSystem.RoleOf(s, id, carrier), TaskType.FIND_CAGE_AND_PUT_DEADGUY_IN);

    /// <summary>The best free cage in the duty's town for the carrier (<see cref="FixtureRegistry.FindCage"/>'s weights, its squad's home x100), not another errand's.</summary>
    Fixture? Cage(World world, Vector3 at, TownDuty self)
    {
        if (!world.Characters.TryResolveNext(self.Member, out int slot) || world.Characters.Cold(slot) is not { } carrier) return null;
        if (self.Town.Site >= 0) world.Fixtures.RegisterSite(world, data, self.Town.Site);
        var squad = world.Squads.Find(carrier.SquadId);
        return world.Fixtures.FindCage(world, carrier, at, (squad?.Ai?.OwnHome ?? squad?.HomeBuilding)?.Key, f => AiSystem.TownAt(data, world, f.Position) != self.Town || duties.Any(o => !ReferenceEquals(o, self) && o.Target == f.Id));
    }

    bool StepFood(World world, TownDuty d, int g, CharacterCold gc)
    {
        var table = world.Characters;
        ref var gn = ref table.Next[g];
        if (d.Target is not { } id || StoreInventory(world, id) is not { } store || gc.Inventory is not { } into) return End(world, d);
        bool walking = gn.Task == (byte)CharacterTask.GoTo;
        // A player order (a walk elsewhere, any other task) wins: the run ends and leaves it be.
        if (d.PlayerSide && (walking ? gn.Goal != d.Goal : gn.Task != (byte)CharacterTask.Idle))
        {
            gc.Engaged = false;
            return false;
        }
        if (Vector2.Distance(Flat(gn.Position), d.Goal) <= Settings.Arrive || !walking && Vector2.Distance(Flat(gn.Position), d.Goal) <= Settings.CageSlack)
        {
            TaskStart.Stand(table, g);
            if (d.Kind == TownDutyKind.LootFood)
                foreach (var item in store.Items.Where(i => i.IsFood).ToList()) InventoryCommands.MoveInto(store, item, 0, into, bagFirst: true, out _);   // Task_EmptyMachine: while there is room
            else if (store.Items.FirstOrDefault(i => Edible(i, d.PlayerSide && gc.Medical is { HasStatsObject: false })) is { } item
                && InventoryCommands.MoveInto(store, item, 1, into, bagFirst: false, out _) == 0   // the grid, else the worn bag (state 82)
                && InventoryCommands.MoveInto(store, item, 1, into, bagFirst: true, out _, bagOnly: true) == 0 && d.PlayerSide)
                foodWaits.Add((d.Member, world.GameHours + Settings.RefuseHours));   // no room for it: not again for a while
            world.Machines.Touch();
            return End(world, d);
        }
        if (walking) return true;
        if (++d.Tries > Settings.Walks) return End(world, d);
        TaskStart.Walk(world, g, d.Goal, SpeedMode.Walk, RowOf(d.Kind), TaskSource.Goal);
        return true;
    }

    /// <summary>
    /// <c>AI::findPrisonerWithContraband</c> (<c>FUN_1408cf6a0</c>, goal 212): the first cage of the town, in list order (no distance weighting), whose occupant carries a stolen item
    /// and is not another errand's. Engine choice: the original's room test for the item is left to the take.
    /// </summary>
    (Fixture? Cage, CharacterId Prisoner) Contraband(World world, CampaignTown town)
    {
        var table = world.Characters;
        if (town.Site >= 0) world.Fixtures.RegisterSite(world, data, town.Site);
        foreach (var f in world.Fixtures.All)
        {
            if (f.Kind != FixtureKind.Cage || FixtureRegistry.IsFree(table, f) || !table.TryResolveNext(f.Occupant, out int s) || duties.Any(o => o.Body == f.Occupant)) continue;
            if (table.Cold(s) is not { Inventory: { } inv } c || !inv.Items.Any(i => i.IsStolenFor(c.Faction, data)) || AiSystem.TownAt(data, world, f.Position) != town) continue;
            return (f, f.Occupant);
        }
        return (null, CharacterId.None);
    }

    /// <summary>
    /// A confiscation (212; its effect is row 3 "Collecting item" acted out by <c>Task_Loot_AI</c>, whose law-enforcement branch it is, <b>Observed</b>): at the cage the guard waits
    /// <see cref="CarrySystem.ConfiscateSeconds"/>, then takes one unit of each stolen item of the prisoner (<see cref="CarrySystem.Confiscate"/>). Engine choice: every 212 runner
    /// confiscates (the base data lists 212 only in the law-enforcement add-on packages).
    /// </summary>
    bool StepConfiscate(World world, TownDuty d, int g, CharacterCold gc)
    {
        var table = world.Characters;
        ref var gn = ref table.Next[g];
        var cage = d.Target is { } id ? world.Fixtures.Find(id) : null;
        if (cage is null || cage.Occupant != d.Body || !table.TryResolveNext(d.Body, out int p) || table.Cold(p)?.Inventory is null || gc.Inventory is null) return End(world, d);
        bool walking = gn.Task == (byte)CharacterTask.GoTo;
        float gap = Vector2.Distance(Flat(gn.Position), d.Goal);
        if (gap <= cage.UseRange + Settings.CageReach || !walking && gap <= Settings.CageSlack)
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
    /// <c>AI::findStrangersInMyBuilding</c> (<c>FUN_1408ced70</c>, goals 158 / 283): in the squad's home building, the first character (slot order, the original takes its sensed-list
    /// order) neither of an allied faction nor an enemy, not down, dead or caged, not a slave of the shooer's faction and not law enforcement (AUTHORITY_FIGURE, engine reading); for an
    /// NPC squad also any of the player's characters there that is not sneaking or that the shooer has spotted. Not one another errand shoos. None: none.
    /// </summary>
    CharacterId Stranger(World world, int slot, CharacterCold cold, Squad squad)
    {
        if (squad.HomeBuilding is not { } home) return CharacterId.None;
        var table = world.Characters;
        aggro ??= world.Systems.OfType<AggroSystem>().FirstOrDefault();
        if (insidersTick != world.Tick)
        {
            insidersTick = world.Tick;
            insiders.Clear();
            for (int s = 0; s < table.HighWater; s++)
                if (table.Next[s].Alive && world.Sight.BuildingOf(s) is { } key) (insiders.TryGetValue(key, out var list) ? list : insiders[key] = []).Add(s);
        }
        if (!insiders.TryGetValue(home.Key, out var inside)) return CharacterId.None;
        foreach (int s in inside)
        {
            if (s == slot || table.Cold(s) is not { Medical.Incapacitated: false } c || c.IsImprisoned) continue;
            if (c.SlaveState == 1 && c.SlaveOwner == cold.Faction) continue;
            var id = table.IdOf(s);
            if (duties.Any(o => o.Kind == TownDutyKind.Shoo && o.Body == id)) continue;
            bool neutral = c.Faction != cold.Faction && !(data.Relations.Contains(cold.Faction) && data.Relations.Contains(c.Faction)
                && (data.Relations.IsAlly(cold.Faction, c.Faction) || AggroSystem.IsEnemy(data.Relations, cold.Faction, c.Faction)));
            if (neutral && c.NpcClass != Meitou.Data.Gameplay.FundamentalType.LawEnforcement) return id;
            if (!cold.IsPlayer && c.IsPlayer && ((c.Orders & CharacterOrders.Sneak) == 0 || aggro?.Spotted(slot, id) == true)) return id;
        }
        return CharacterId.None;
    }

    AggroSystem? aggro;
    // Who is inside each building, by building key, as of insidersTick (the shoo finder's pass).
    readonly Dictionary<string, List<int>> insiders = new(StringComparer.Ordinal);
    long insidersTick = -1;

    /// <summary>Event 9 EV_SHOO_FROM_MY_BUILDING, 17 EV_INTRUDER_FOUND (for an enemy), 60 EV_GIVE_UP_CHASE (docs/game/dialogue.md "Enums").</summary>
    const int ShooEvent = 9, IntruderFound = 17, GiveUpChase = 60;
    /// <summary>Game seconds between shoo events (engine choice: the original re-sends on every update and its lines' own timers pace them).</summary>
    const float ShooEverySeconds = 5;

    /// <summary>
    /// A shoo (161 <c>Task_TalktoPlayer</c> with the row event 9): the shooer walks at the stranger and sends it event 9 (17 when it is an enemy) every
    /// <see cref="ShooEverySeconds"/>; the dialogue data does the rest (lines that follow, time out and turn violent). After 25 game minutes, once the stranger is getting away
    /// (fleeing or walking off), it gives up with event 60. Over when the stranger is no longer in the home building, is down or caged.
    /// </summary>
    bool StepShoo(World world, TownDuty d, int g, CharacterCold gc)
    {
        var table = world.Characters;
        var squad = world.Squads.Find(gc.SquadId);
        if (squad?.HomeBuilding is not { } home || !table.TryResolveNext(d.Body, out int t) || table.Cold(t) is not { Medical.Incapacitated: false, IsImprisoned: false } tc
            || world.Sight.BuildingOf(t) != home.Key)
            return End(world, d);
        ref var gn = ref table.Next[g];
        var to = new Vector2(table.Next[t].Position.X, table.Next[t].Position.Z);
        if (Vector2.Distance(Flat(gn.Position), to) > Dialogue.DialogueSystem.TalkReach && (gn.Task != (byte)CharacterTask.GoTo || Vector2.Distance(gn.Goal, to) > Dialogue.DialogueSystem.TalkReach))
            TaskStart.Walk(world, g, to, SpeedMode.Walk, RowOf(d.Kind), TaskSource.Goal);
        d.Next -= world.TickSeconds;
        if (d.Next <= 0)
        {
            d.Next = ShooEverySeconds;
            bool enemy = data.Relations.Contains(gc.Faction) && data.Relations.Contains(tc.Faction) && AggroSystem.IsEnemy(data.Relations, gc.Faction, tc.Faction);
            world.Defer(new Dialogue.DialogueEvent(d.Member, d.Body, enemy ? IntruderFound : ShooEvent) { Tick = world.Tick + 1 });
        }
        bool away = tc.Fleeing || table.Next[t].Task == (byte)CharacterTask.GoTo && Vector2.Distance(table.Next[t].Goal, Flat(gn.Position)) > Vector2.Distance(to, Flat(gn.Position));
        if ((world.GameHours - d.Since) * 60 > 25 && away)
        {
            world.Defer(new Dialogue.DialogueEvent(d.Member, d.Body, GiveUpChase) { Tick = world.Tick + 1 });
            return End(world, d);
        }
        return true;
    }

    /// <summary>
    /// <c>AI::findRandomBuildingInTown</c> (<c>FUN_1408cb510</c>, goal 216): the squad's mission town (its blackboard's, else the campaign target it marches on), else the town the
    /// member stands in; one of its standing buildings with an interior, drawn uniformly at random. Null: none.
    /// </summary>
    Meitou.Data.World.TownBuilding? RansackTarget(World world, Squad squad, int slot)
    {
        int site = squad.Ai is { MissionTown: >= 0 } b ? b.MissionTown : squad.Ai is { Destination: { Site: >= 0 } dest } ? dest.Site : AiSystem.TownAt(data, world, world.Characters.Next[slot].Position).Site;
        if (site < 0) return null;
        var rooms = data.Sites[site].Buildings.Where(x => !x.Destroyed && (x.InteriorLayout.Length > 0 || x.InteriorLayouts.Count > 0)).ToList();
        if (rooms.Count == 0) return null;
        return rooms[Rng.Int(Rng.Hash(world.Seed, (ulong)Rng.Key(world.Characters.IdOf(slot)) ^ (ulong)world.Tick, RngPurpose.Slow, RansackSalt), rooms.Count)];
    }

    const ulong RansackSalt = 0x5261_6E73_6163_6B21;

    /// <summary>
    /// 216 Ransacking town (156 into the drawn building, then <c>Task_PatrolTownViolent</c>, which only sets running): the member runs into the building; once inside (or when the walk
    /// ends near it) the errand ends and the next look draws another, so the raiders run from building to building.
    /// </summary>
    bool StepRansack(World world, TownDuty d, int g)
    {
        var table = world.Characters;
        ref var gn = ref table.Next[g];
        bool walking = gn.Task == (byte)CharacterTask.GoTo;
        if (world.Sight.BuildingOf(g) == d.Target || !walking && Vector2.Distance(Flat(gn.Position), d.Goal) <= Settings.CageSlack) return End(world, d);
        if (walking) return true;
        if (++d.Tries > Settings.Walks) return End(world, d);
        TaskStart.Walk(world, g, d.Goal, SpeedMode.Run, RowOf(d.Kind), TaskSource.Goal);
        return true;
    }

    bool PutDown(World world, TownDuty d, int g)
    {
        TaskStart.Stand(world.Characters, g);
        carry.BeginPutDown(world, g, TaskSource.Goal);
        refused.Add((d.Body, world.GameHours + Settings.RefuseHours));
        return End(world, d);
    }

    /// <summary>Ends an errand: the member goes back to its routine (the AI takes it again).</summary>
    static bool End(World world, TownDuty d)
    {
        var table = world.Characters;
        if (table.TryResolveNext(d.Member, out int g) && table.Cold(g) is { } gc)
        {
            gc.Engaged = false;
            if (table.Next[g].Task == (byte)CharacterTask.GoTo) TaskStart.Stand(table, g);
        }
        return false;
    }

    static Vector2 Flat(Vector3 p) => new(p.X, p.Z);

    public void Hash(ref StateHasher hasher)
    {
        if (duties.Count == 0 && refused.Count == 0 && foodWaits.Count == 0) return;   // only while one runs, so worlds without them hash as before
        hasher.Add(0x546F776E);
        hasher.Add(duties.Count);
        foreach (var d in duties) d.Hash(ref hasher);
        hasher.Add(refused.Count);
        foreach (var (body, until) in refused) { hasher.Add(body); hasher.Add(until); }
        if (foodWaits.Count > 0)
        {
            hasher.Add(foodWaits.Count);
            foreach (var (member, until) in foodWaits) { hasher.Add(member); hasher.Add(until); }
        }
    }
}
