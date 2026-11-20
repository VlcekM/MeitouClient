using System.Numerics;
using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Carrying;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Locks;
using Meitou.Simulation.Player;
using Meitou.Simulation.Population;
using Meitou.Simulation.Production;

namespace Meitou.Simulation.Kidnap;

/// <summary>
/// The town duties as goals of the one chooser (docs/simulation.md "Town duties", "Engine goals"): the intruder and corpse lifts 273, 223 and 138,
/// the carried body's goals 112, 281, 274, 224, 106 and 137, the store runs 243, 131 and 184, the confiscation 212, the shoos 158 and 283 and the
/// ransack 216. The finders' picks are kept in a serial look for the think to read (<see cref="Look"/>); a goal the chooser starts is acted out by
/// <see cref="TownDutySystem"/>, which ends it with <see cref="DutyGoals.Done"/> at the original's own end.
/// </summary>
internal sealed class TownGoals(TownDutySystem owner)
{
    #region Picks

    /// <summary>
    /// A body a lift finder found (273, 223, 138), or none, and whether 273's desire lets the asker's town have it lifted (<see cref="TownLets"/>).
    /// </summary>
    readonly record struct BodyPick(CharacterId Id, CharacterId Body, Vector3 At, bool TownLets);

    /// <summary>
    /// A carrier's body as its goals weigh it: dead, friendly, the desires of the cage (112 / 281), throw-out (224 / 274) and bed (106) goals and
    /// the cage and bed their finders found.
    /// </summary>
    readonly record struct LoadPick(CharacterId Id, CharacterId Body, bool Dead, bool Friendly, float Cage, float ThrowOut, float Bed, Fixture? CageAt,
        Fixture? BedAt);

    /// <summary>
    /// A store a store finder found (243, 131, 184): its id (a machine's site or a container), where the member walks, 243's desire, and the room
    /// and allies-fighting states of the take (82, 12).
    /// </summary>
    readonly record struct StorePick(CharacterId Id, string Store, Vector3 At, float Want, bool Room, bool AlliesFighting);

    /// <summary>
    /// A character or building a watch finder found (212 a prisoner and its cage, 158 / 283 a stranger, 216 a building and the door it bashes).
    /// </summary>
    readonly record struct WatchPick(CharacterId Id, CharacterId Who, Vector3 At, string? Key, string? Door);

    /// <summary>
    /// 273's pick by slot (hashed).
    /// </summary>
    readonly SortedDictionary<int, BodyPick> _intruders = [];

    /// <summary>
    /// 223's pick by slot (hashed).
    /// </summary>
    readonly SortedDictionary<int, BodyPick> _guests = [];

    /// <summary>
    /// 138's pick (also 137's row 138) by slot (hashed).
    /// </summary>
    readonly SortedDictionary<int, BodyPick> _corpses = [];

    /// <summary>
    /// 137's disposal by slot (hashed).
    /// </summary>
    readonly SortedDictionary<int, (CharacterId Id, CorpseMachine Machine)> _disposals = [];

    /// <summary>
    /// The carriers' bodies by slot (hashed).
    /// </summary>
    readonly SortedDictionary<int, LoadPick> _loads = [];

    /// <summary>
    /// 243's store by slot (hashed).
    /// </summary>
    readonly SortedDictionary<int, StorePick> _food = [];

    /// <summary>
    /// 131's store by slot (hashed).
    /// </summary>
    readonly SortedDictionary<int, StorePick> _loot = [];

    /// <summary>
    /// 184's storage by slot (hashed).
    /// </summary>
    readonly SortedDictionary<int, StorePick> _stash = [];

    /// <summary>
    /// 212's prisoner by slot (hashed).
    /// </summary>
    readonly SortedDictionary<int, WatchPick> _prisoners = [];

    /// <summary>
    /// 158 / 283's stranger by slot (hashed).
    /// </summary>
    readonly SortedDictionary<int, WatchPick> _strangers = [];

    /// <summary>
    /// 216's building by slot (hashed).
    /// </summary>
    readonly SortedDictionary<int, WatchPick> _raids = [];

    /// <summary>
    /// The finders' look (serial, the town duty step), for each NPC with a full mind of its own in a squad on its slot's turn every
    /// <paramref name="every"/> ticks, and every tick for a carrier no goal holds (it chooses its body's goal at once): the finders of the goals its
    /// role, slave or race list holds in the hour (<see cref="AiSystem.WeighsGoal"/>) keep what they find; the think's desire, finder and states read
    /// the picks (engine: the original's finders run at each score, so a pick is at most a look period old).
    /// </summary>
    internal void Look(World world, int every)
    {
        var table = world.Characters;
        for (int s = 0; s < table.HighWater; s++)
        {
            var cold = Minded(world, s);
            if (cold is null)
            {
                Forget(s);
                continue;
            }

            bool choosing = !cold.Carrying.IsNone && cold.Ai!.Duty is null;
            if (choosing || world.Turn(s, every)) LookAt(world, s, cold);
        }

        PruneAll(table);
    }

    /// <summary>
    /// The look of one slot again after its act-out ended (serial), so its next choice reads what it finds now; nothing for a slot the look skips.
    /// </summary>
    internal void LookAgain(World world, int slot)
    {
        if (Minded(world, slot) is { } cold) LookAt(world, slot, cold);
    }

    /// <summary>
    /// The NPC at <paramref name="slot"/> when the look serves it: alive, not dormant, a full mind of its own in a squad with a blackboard; else null.
    /// </summary>
    static CharacterCold? Minded(World world, int slot)
    {
        var table = world.Characters;
        if (!table.Next[slot].Alive || table.Next[slot].Dormant) return null;

        return table.Cold(slot) is { IsPlayer: false, Ai: { Engine: false, Player: false, Squad.Ai: not null } } cold ? cold : null;
    }

    /// <summary>
    /// Every finder of the slot's goals: a carrier's body goals, else the lifts; then the disposal, the stores and the watches.
    /// </summary>
    void LookAt(World world, int slot, CharacterCold cold)
    {
        var squad = cold.Ai!.Squad;
        var id = world.Characters.IdOf(slot);
        var at = world.Characters.Next[slot].Position;
        var town = AiSystem.TownAt(owner.PopulationData, world, at);
        if (cold.Carrying.IsNone) LookForBodies(world, slot, id, cold, squad, town);
        else LookAtLoad(world, slot, id, cold);

        LookForDisposal(world, slot, id);
        LookForStores(world, slot, id, cold, squad, at, town);
        LookForWatches(world, slot, id, cold, squad, town);
    }

    /// <summary>
    /// The lift finders of a member carrying nothing: 273's intruder in its own town (with the town test of 273's desire), 223's in its home, 138's
    /// corpse (also 137's provider); a carrier's body picks go.
    /// </summary>
    void LookForBodies(World world, int slot, CharacterId id, CharacterCold cold, Squad squad, CampaignTown town)
    {
        _loads.Remove(slot);
        int intruder = -1;
        if (AiSystem.WeighsGoal(world, slot, TaskType.PickupIntrudersTown) && !town.IsNone && owner.MyTown(world, squad, town))
            intruder = owner.Intruder(world, slot, cold, town);

        // 273's pick is kept while its goal holds the member too, for the town test of its desire.
        bool lifting = owner.ActOf(id, TaskType.PickupIntrudersTown) is not null;
        bool lets = (intruder >= 0 || lifting) && owner.TownLets(world, cold, town);
        Keep(world, _intruders, slot, id, intruder, lifting, lets);

        int guest = squad.HomeBuilding is { } home && AiSystem.WeighsGoal(world, slot, TaskType.PickupIntruders)
            ? owner.HomeIntruder(world, slot, cold, home) : -1;
        Keep(world, _guests, slot, id, guest, false, true);

        bool corpses = AiSystem.WeighsGoal(world, slot, TaskType.FindADeadGuy) || AiSystem.WeighsGoal(world, slot, TaskType.DeadGuysGoInThePot);
        int corpse = corpses && (cold.Orders & CharacterOrders.Hold) == 0 ? owner.Corpse(world, slot, cold) : -1;
        Keep(world, _corpses, slot, id, corpse, false, true);
    }

    /// <summary>
    /// Keeps a lift finder's pick of the body in <paramref name="body"/> (-1: none, the pick goes unless <paramref name="always"/>).
    /// </summary>
    static void Keep(World world, SortedDictionary<int, BodyPick> picks, int slot, CharacterId id, int body, bool always, bool lets)
    {
        if (body >= 0) picks[slot] = new BodyPick(id, world.Characters.IdOf(body), world.Characters.Next[body].Position, lets);
        else if (always) picks[slot] = new BodyPick(id, CharacterId.None, default, lets);
        else picks.Remove(slot);
    }

    /// <summary>
    /// A carrier's body as its goals weigh it (<see cref="LoadPick"/>); the lift picks go. Nothing for a body it no longer holds.
    /// </summary>
    void LookAtLoad(World world, int slot, CharacterId id, CharacterCold cold)
    {
        var table = world.Characters;
        _intruders.Remove(slot);
        _guests.Remove(slot);
        _corpses.Remove(slot);
        if (!table.TryResolveNext(cold.Carrying, out int b) || table.Cold(b) is not { Medical: { } medical } body || body.CarriedBy != id)
        {
            _loads.Remove(slot);
            return;
        }

        bool cages = AiSystem.WeighsGoal(world, slot, TaskType.FindCageAndPutIn) || AiSystem.WeighsGoal(world, slot, TaskType.FindCageAndPutDeadguyIn);
        float cage = cages ? owner.CageDesire(world, slot, b) : 0;
        bool outs = AiSystem.WeighsGoal(world, slot, TaskType.TakeIntruderOutside)
            || AiSystem.WeighsGoal(world, slot, TaskType.TakeIntruderOutsideTown);
        float throwOut = outs ? owner.ThrowOutDesire(world, slot, b) : 0;
        var beds = AiSystem.WeighsGoal(world, slot, TaskType.FindBedAndPutIn) ? owner.Beds(world) : null;
        float bed = beds?.PutInDesire(world, slot) ?? 0;

        var cageAt = cage > 0 ? owner.Cage(world, slot, cold, table.Next[slot].Position) : null;
        var bedAt = bed > 0 ? beds!.FindForCarrier(world, slot) : null;
        _loads[slot] = new LoadPick(id, cold.Carrying, medical.Dead, owner.Carry.Friendly(cold, body), cage, throwOut, bed, cageAt, bedAt);
    }

    /// <summary>
    /// 137's disposal (<see cref="TownDutySystem.DisposalFor"/>) for a member weighing 137.
    /// </summary>
    void LookForDisposal(World world, int slot, CharacterId id)
    {
        if (AiSystem.WeighsGoal(world, slot, TaskType.DeadGuysGoInThePot) && owner.DisposalFor(world, slot) is { } machine)
            _disposals[slot] = (id, machine);
        else _disposals.Remove(slot);
    }

    /// <summary>
    /// The store finders: 243's food store (an NPC of the player's faction runs the player-side food run instead), 131's store to loot and 184's
    /// storage for stolen goods.
    /// </summary>
    void LookForStores(World world, int slot, CharacterId id, CharacterCold cold, Squad squad, Vector3 at, CampaignTown town)
    {
        // 243 and 131 search the blackboard's target town (Blackboard.Destination), else the current one, else the 3 x 3 zone cells round the member
        // (FUN_1408cb650).
        var foodTown = squad.Ai is { Destination.IsNone: false } target ? target.Destination : town;
        bool food = cold.Faction != world.Player.Faction && AiSystem.WeighsGoal(world, slot, TaskType.AquireFoodAtHomebase);
        Keep(_food, slot, food ? owner.FoodPick(world, slot, cold, at, foodTown) : null, id);

        bool loot = AiSystem.WeighsGoal(world, slot, TaskType.LootFoodAndStuff);
        Keep(_loot, slot, loot ? owner.LootPick(world, cold, at, foodTown) : null, id);

        bool stash = !town.IsNone && AiSystem.WeighsGoal(world, slot, TaskType.PutLootInStorage);
        Keep(_stash, slot, stash ? owner.StashStore(world, squad, cold, at, town) : null, id);
    }

    /// <summary>
    /// Keeps a store finder's pick for the slot (none: the pick goes).
    /// </summary>
    static void Keep(SortedDictionary<int, StorePick> picks, int slot, (string Id, Vector2 Spot, float Want, bool Room, bool Fighting)? found,
        CharacterId id)
    {
        if (found is not var (store, spot, want, room, fighting))
        {
            picks.Remove(slot);
            return;
        }

        picks[slot] = new StorePick(id, store, Spot(spot), want, room, fighting);
    }

    /// <summary>
    /// Keeps a storage the stash finder found (its room and desire are the goal's own).
    /// </summary>
    static void Keep(SortedDictionary<int, StorePick> picks, int slot, (string Id, Vector2 Spot)? found, CharacterId id) =>
        Keep(picks, slot, found is var (store, spot) ? (store, spot, 0, true, false) : null, id);

    /// <summary>
    /// A flat spot as a plan target's place (any floor).
    /// </summary>
    static Vector3 Spot(Vector2 spot) => new(spot.X, float.NaN, spot.Y);

    /// <summary>
    /// The watch finders: 212's prisoner with stolen goods in a cage of the member's own town, 158 / 283's stranger in its home, 216's building.
    /// </summary>
    void LookForWatches(World world, int slot, CharacterId id, CharacterCold cold, Squad squad, CampaignTown town)
    {
        bool mine = !town.IsNone && owner.MyTown(world, squad, town);
        if (mine && AiSystem.WeighsGoal(world, slot, TaskType.LootStolenGoods) && owner.Contraband(world, town) is ({ } cage, var prisoner))
            _prisoners[slot] = new WatchPick(id, prisoner, cage.Position, cage.Id, null);
        else _prisoners.Remove(slot);

        bool shoos = AiSystem.WeighsGoal(world, slot, TaskType.ShooStrangersOutOfMyBuilding)
            || AiSystem.WeighsGoal(world, slot, TaskType.ShooStrangersOutOfMyBuildingIfPrivate);
        var stranger = shoos ? owner.Stranger(world, slot, cold, squad) : CharacterId.None;
        if (!stranger.IsNone) _strangers[slot] = new WatchPick(id, stranger, world.Characters.Next[stranger.Slot].Position, null, null);
        else _strangers.Remove(slot);

        if (AiSystem.WeighsGoal(world, slot, TaskType.RunAroundTownLookingForPeople) && owner.RansackTarget(world, squad, slot) is var (raid, door))
            _raids[slot] = new WatchPick(id, CharacterId.None, raid.Position, raid.Key, door);
        else _raids.Remove(slot);
    }

    /// <summary>
    /// Drops every pick of the slot.
    /// </summary>
    void Forget(int slot)
    {
        _intruders.Remove(slot);
        _guests.Remove(slot);
        _corpses.Remove(slot);
        _disposals.Remove(slot);
        _loads.Remove(slot);
        _food.Remove(slot);
        _loot.Remove(slot);
        _stash.Remove(slot);
        _prisoners.Remove(slot);
        _strangers.Remove(slot);
        _raids.Remove(slot);
    }

    /// <summary>
    /// Drops the picks of slots that no longer hold the character that looked.
    /// </summary>
    void PruneAll(CharacterTable table)
    {
        Prune(table, _intruders, p => p.Id);
        Prune(table, _guests, p => p.Id);
        Prune(table, _corpses, p => p.Id);
        Prune(table, _disposals, p => p.Id);
        Prune(table, _loads, p => p.Id);
        Prune(table, _food, p => p.Id);
        Prune(table, _loot, p => p.Id);
        Prune(table, _stash, p => p.Id);
        Prune(table, _prisoners, p => p.Id);
        Prune(table, _strangers, p => p.Id);
        Prune(table, _raids, p => p.Id);
    }

    /// <summary>
    /// Drops the picks in <paramref name="picks"/> whose looker no longer stands in their slot.
    /// </summary>
    static void Prune<T>(CharacterTable table, SortedDictionary<int, T> picks, Func<T, CharacterId> looker)
    {
        if (picks.Count == 0) return;

        List<int>? stale = null;
        foreach (var (slot, pick) in picks)
        {
            if (!table.TryResolveNext(looker(pick), out int at) || at != slot) (stale ??= []).Add(slot);
        }

        if (stale is not null) foreach (int slot in stale) picks.Remove(slot);
    }

    /// <summary>
    /// The asker's pick in <paramref name="picks"/>; null for none or another character's.
    /// </summary>
    static T? PickOf<T>(SortedDictionary<int, T> picks, in DutyAsk ask, Func<T, CharacterId> looker) where T : struct =>
        picks.TryGetValue(ask.Slot, out var pick) && looker(pick) == ask.Id ? pick : null;

    /// <summary>
    /// Adds the picks to the state hash, only while there are any.
    /// </summary>
    internal void Hash(ref StateHasher hasher)
    {
        if (_intruders.Count + _guests.Count + _corpses.Count + _disposals.Count + _loads.Count + _food.Count + _loot.Count + _stash.Count
            + _prisoners.Count + _strangers.Count + _raids.Count == 0)
        {
            return;
        }

        hasher.Add(0x546F476C);   // "ToGl"
        HashBodies(ref hasher, _intruders);
        HashBodies(ref hasher, _guests);
        HashBodies(ref hasher, _corpses);
        HashDisposals(ref hasher);
        HashLoads(ref hasher);
        HashStores(ref hasher, _food);
        HashStores(ref hasher, _loot);
        HashStores(ref hasher, _stash);
        HashWatches(ref hasher, _prisoners);
        HashWatches(ref hasher, _strangers);
        HashWatches(ref hasher, _raids);
    }

    /// <summary>
    /// Hashes body picks.
    /// </summary>
    static void HashBodies(ref StateHasher hasher, SortedDictionary<int, BodyPick> picks)
    {
        hasher.Add(picks.Count);
        foreach (var (slot, p) in picks)
        {
            hasher.Add(slot);
            hasher.Add(p.Id);
            hasher.Add(p.Body);
            hasher.Add(p.At);
            hasher.Add(p.TownLets);
        }
    }

    /// <summary>
    /// Hashes the disposal picks.
    /// </summary>
    void HashDisposals(ref StateHasher hasher)
    {
        hasher.Add(_disposals.Count);
        foreach (var (slot, p) in _disposals)
        {
            hasher.Add(slot);
            hasher.Add(p.Id);
            hasher.Add(Rng.StableHash(p.Machine.Id));
        }
    }

    /// <summary>
    /// Hashes the carriers' body picks.
    /// </summary>
    void HashLoads(ref StateHasher hasher)
    {
        hasher.Add(_loads.Count);
        foreach (var (slot, p) in _loads)
        {
            hasher.Add(slot);
            hasher.Add(p.Id);
            hasher.Add(p.Body);
            hasher.Add(p.Dead);
            hasher.Add(p.Friendly);
            hasher.Add(p.Cage);
            hasher.Add(p.ThrowOut);
            hasher.Add(p.Bed);
            hasher.Add(p.CageAt is { } cage ? Rng.StableHash(cage.Id) : 0UL);
            hasher.Add(p.BedAt is { } bed ? Rng.StableHash(bed.Id) : 0UL);
        }
    }

    /// <summary>
    /// Hashes store picks.
    /// </summary>
    static void HashStores(ref StateHasher hasher, SortedDictionary<int, StorePick> picks)
    {
        hasher.Add(picks.Count);
        foreach (var (slot, p) in picks)
        {
            hasher.Add(slot);
            hasher.Add(p.Id);
            hasher.Add(Rng.StableHash(p.Store));
            hasher.Add(p.Want);
            hasher.Add(p.Room);
            hasher.Add(p.AlliesFighting);
        }
    }

    /// <summary>
    /// Hashes watch picks.
    /// </summary>
    static void HashWatches(ref StateHasher hasher, SortedDictionary<int, WatchPick> picks)
    {
        hasher.Add(picks.Count);
        foreach (var (slot, p) in picks)
        {
            hasher.Add(slot);
            hasher.Add(p.Id);
            hasher.Add(p.Who);
            hasher.Add(p.At);
            hasher.Add(p.Key is { } key ? Rng.StableHash(key) : 0UL);
            hasher.Add(p.Door is { } door ? Rng.StableHash(door) : 0UL);
        }
    }

    #endregion

    #region Asking

    /// <summary>
    /// The desire of row 68 Lifting and of 223 (AI slot 59 <c>FUN_14059e1a0</c>, <b>Verified</b>) for a target that is no character
    /// (<c>DAT_14167e8d8</c>): the scorer clears a fresh candidate's target before it asks, so a new 273 or 223 scores this.
    /// </summary>
    const float LiftOther = 0.5f;

    /// <summary>
    /// The desire of 184 Ditching loot (<b>Verified</b>, its registration in <c>FUN_140319a60</c>).
    /// </summary>
    const float StashWant = 2;

    /// <summary>
    /// The town duty system that acts the goals out and owns the finders.
    /// </summary>
    TownDutySystem Owner => owner;

    /// <summary>
    /// The act-out record of the asker's goal, or null while the goal does not hold it.
    /// </summary>
    TownDuty? Held(in DutyAsk ask) => owner.ActOf(ask.Id, ask.Type);

    /// <summary>
    /// Row 68's desire (AI slot 59, <b>Verified</b>): 0 while the asker carries something, 1.0 for a character target, else <see cref="LiftOther"/>.
    /// The goal's own ask has the target the goal holds while it holds the asker (<paramref name="held"/>), none for a fresh candidate.
    /// </summary>
    static float LiftDesire(World world, in DutyAsk ask, bool held)
    {
        if (world.Characters.Cold(ask.Slot) is not { Carrying.IsNone: true }) return 0;

        return held || ask.Target.Character is not null ? 1 : LiftOther;
    }

    /// <summary>
    /// A finder's answer: the act-out's planned target while the goal holds the asker (the original keeps the current goal's finder hand), else
    /// <paramref name="picked"/>; factor 1 with a target, else 0.
    /// </summary>
    float Find(in DutyAsk ask, PlanTarget picked, out PlanTarget target)
    {
        target = Held(ask) is { } held ? held.Planned : picked;
        return target.IsNone ? 0 : 1;
    }

    /// <summary>
    /// The states of a found character target (parallel: the last tick's places, cold state only serial steps write): 1 and 46 at it (the lift's
    /// reach), 7 down, 48 carried by the asker; null for the rest.
    /// </summary>
    bool? BodyState(World world, in DutyAsk ask, CharacterId body, StateType state)
    {
        var table = world.Characters;
        var cold = table.IsAlive(body) ? table.Cold(body.Slot) : null;
        return state switch
        {
            StateType.IsAtLocation or StateType.IsAtLocation46 => cold is not null && owner.Carry.AtBody(world, ask.Slot, body.Slot),
            StateType.IsTargetDown => CharacterFacts.TargetDown(cold),
            StateType.IsCarryingTarget => table.Cold(ask.Slot)?.Carrying == body,
            _ => null,
        };
    }

    /// <summary>
    /// The carried body's states, for any target: 99 the carried body is dead; 77, 81 and 100 (in a bed, a cage, a disposal) read as the asker
    /// carrying nothing any more (the put-in, lock-up or feed let it go); null for the rest.
    /// </summary>
    static bool? LoadState(World world, in DutyAsk ask, StateType state)
    {
        var table = world.Characters;
        var cold = table.Cold(ask.Slot);
        return state switch
        {
            StateType.IsCarriedDudeDead => cold is { Carrying.IsNone: false } c && table.IsAlive(c.Carrying)
                && table.Cold(c.Carrying.Slot)?.Medical is { Dead: true },
            StateType.IsCarriedDudeInBed or StateType.CarriedDudeNowInCage or StateType.CarriedDudeNowInDisposalMachine =>
                cold?.Carrying.IsNone ?? true,
            _ => null,
        };
    }

    /// <summary>
    /// Whether the asker stands at a found place (the last tick's place): within <see cref="TownDutySettings.Arrive"/> flat, any floor.
    /// </summary>
    bool AtPlace(World world, in DutyAsk ask, Vector3 at)
    {
        var me = world.Characters.Previous[ask.Slot].Position;
        return Vector2.Distance(new Vector2(me.X, me.Z), new Vector2(at.X, at.Z)) <= owner.Settings.Arrive;
    }

    /// <summary>
    /// The asker's carried body pick while it still carries that body, or null.
    /// </summary>
    LoadPick? Load(World world, in DutyAsk ask) =>
        PickOf(_loads, ask, p => p.Id) is { } load && world.Characters.Cold(ask.Slot)?.Carrying == load.Body ? load : null;

    /// <summary>
    /// The target of a found character pick (none without a body).
    /// </summary>
    static PlanTarget TargetOf(BodyPick? pick) => pick is { Body.IsNone: false } p ? PlanTarget.OfCharacter(p.Body, p.At) : default;

    /// <summary>
    /// The states of a found body target or, for any target, the carried body's (<see cref="BodyState"/>, <see cref="LoadState"/>).
    /// </summary>
    bool? LiftState(World world, in DutyAsk ask, StateType state) =>
        ask.Target is { Kind: PlanTarget.Found, Character: { } body } && BodyState(world, ask, body, state) is { } own
            ? own
            : LoadState(world, ask, state);

    #endregion

    #region Registration

    /// <summary>
    /// Registers the town goals with the rows of their plans the act-outs carry out (<see cref="DutyGoals.Register"/>).
    /// </summary>
    internal void Register(DutyGoals goals)
    {
        goals.Register(TaskType.PickupIntrudersTown, new Lifting(this, _intruders, TownDutyKind.Lift), TaskType.LiftPerson);
        goals.Register(TaskType.PickupIntruders, new Lifting(this, _guests, TownDutyKind.HomeLift), TaskType.LiftPerson);
        goals.Register(TaskType.FindADeadGuy, new Lifting(this, _corpses, TownDutyKind.CorpseLift), TaskType.LiftPerson);
        goals.Register(TaskType.DeadGuysGoInThePot, new Disposing(this), TaskType.FeedCorpseIntoMachine, TaskType.FindADeadGuy, TaskType.LiftPerson);
        goals.Register(TaskType.FindCageAndPutIn, new Caging(this), TaskType.PutInCage);
        goals.Register(TaskType.FindCageAndPutDeadguyIn, new Caging(this), TaskType.PutInCage);
        goals.Register(TaskType.TakeIntruderOutside, new CarryingOut(this));
        goals.Register(TaskType.TakeIntruderOutsideTown, new CarryingOut(this));
        goals.Register(TaskType.FindBedAndPutIn, new Bedding(this), TaskType.PutSomeoneInBed);
        goals.Register(TaskType.AquireFoodAtHomebase, new FoodRun(this), TaskType.GrabOneFood);
        goals.Register(TaskType.LootFoodAndStuff, new FoodLooting(this), TaskType.LootContainer);
        goals.Register(TaskType.PutLootInStorage, new Stashing(this), TaskType.FillMachine);
        goals.Register(TaskType.LootStolenGoods, new Confiscating(this), TaskType.Pickup);
        goals.Register(TaskType.ShooStrangersOutOfMyBuilding, new Shooing(this), TaskType.SendDialogueSignalWithoutMoving);
        goals.Register(TaskType.ShooStrangersOutOfMyBuildingIfPrivate, new Shooing(this), TaskType.SendDialogueSignalWithoutMoving);
        goals.Register(TaskType.RunAroundTownLookingForPeople, new Ransacking(this), TaskType.EnterBuilding);
    }

    #endregion

    #region Lifts and the carried body

    /// <summary>
    /// 273 Remove Intruder, 223 Remove Home Intruder and 138 Find corpses (plan <c>[67, 68, goal]</c>, the finder on the goal's row): the member
    /// lifts the body its finder gives; the goal is done once the body is on its shoulder (the goal row's list A is empty, so the plan ends with
    /// row 68), and the carried body's goals take it on.
    /// </summary>
    sealed class Lifting(TownGoals goals, SortedDictionary<int, BodyPick> picks, TownDutyKind kind) : IDutyGoal
    {
        /// <summary>
        /// Row 68's desire (<see cref="LiftDesire"/>); 223's is the same (slot 59); 273's (AI slot 60 <c>FUN_140597f20</c>, <b>Verified</b>) is
        /// that, 0 where its town test fails (<see cref="TownDutySystem.TownLets"/>); 138's (<b>Verified</b>) 1.0, 0 on Hold or while carrying.
        /// </summary>
        public float Desire(World world, in DutyAsk ask)
        {
            if (ask.Row == TaskType.LiftPerson) return LiftDesire(world, ask, false);
            if (ask.Type == TaskType.FindADeadGuy) return CorpseDesire(world, ask);

            float lift = LiftDesire(world, ask, goals.Held(ask) is not null);
            if (ask.Type == TaskType.PickupIntruders || lift <= 0) return lift;

            return PickOf(picks, ask, p => p.Id) is { TownLets: true } ? lift : 0;
        }

        /// <summary>
        /// The body the goal's finder gives (273 <c>FUN_14099c250</c>, 223 <c>FUN_14099c110</c>, 138 <see cref="CorpseDisposal.FindCorpse"/>).
        /// </summary>
        public float Find(World world, in DutyAsk ask, out PlanTarget target) => goals.Find(ask, TargetOf(PickOf(picks, ask, p => p.Id)), out target);

        /// <summary>
        /// The body's states (<see cref="LiftState"/>).
        /// </summary>
        public bool? State(World world, in DutyAsk ask, StateType state) => goals.LiftState(world, ask, state);

        /// <summary>
        /// Lifts the plan's body (<see cref="TownDutySystem.StartLift"/>).
        /// </summary>
        public bool Start(World world, in DutyAsk ask, IReadOnlyList<PlanStep> plan) => goals.Owner.StartLift(world, ask, plan, kind);

        /// <summary>
        /// The member is no longer held.
        /// </summary>
        public void End(World world, int slot, CharacterId id, DutyEnd why) => goals.Owner.EndGoal(world, slot, id, why);
    }

    /// <summary>
    /// 138's desire (AI slot <c>FUN_140599480</c>, <b>Verified</b>), also as row 138 of 137's plan: 1.0, 0 on Hold or while carrying.
    /// </summary>
    static float CorpseDesire(World world, in DutyAsk ask) =>
        world.Characters.Cold(ask.Slot) is { Carrying.IsNone: true } cold && (cold.Orders & CharacterOrders.Hold) == 0 ? 1 : 0;

    /// <summary>
    /// 137 Corpse disposal (list B 99 a dead body carried; plan <c>[1, 136, 137]</c> while carrying one, else <c>[67, 68, 138, 137]</c>): a carrier
    /// walks the corpse to the disposal its finder gives and feeds it in; a member carrying none lifts the corpse row 138's finder gives first
    /// (the plan ends with the lift, as row 138 is skipped once its list A holds) and weighs 137 again with it on its shoulder.
    /// </summary>
    sealed class Disposing(TownGoals goals) : IDutyGoal
    {
        /// <summary>
        /// 137's desire (AI slot 43 <c>FUN_1406415a0</c>, <b>Verified</b>) and row 136's (slot 19) 1.0; row 138's <see cref="CorpseDesire"/>; row
        /// 68's <see cref="LiftDesire"/>.
        /// </summary>
        public float Desire(World world, in DutyAsk ask) => ask.Row switch
        {
            TaskType.FindADeadGuy => CorpseDesire(world, ask),
            TaskType.LiftPerson => LiftDesire(world, ask, false),
            _ => 1,
        };

        /// <summary>
        /// Row 137's disposal (<c>FUN_1408d1090</c> over <see cref="CorpseDisposal.Find"/>), or row 138's corpse.
        /// </summary>
        public float Find(World world, in DutyAsk ask, out PlanTarget target)
        {
            if (ask.Row == TaskType.FindADeadGuy) return goals.Find(ask, TargetOf(PickOf(goals._corpses, ask, p => p.Id)), out target);

            var picked = PickOf(goals._disposals, ask, p => p.Id) is { } d ? PlanTarget.OfPlace(d.Machine.Position) : default;
            return goals.Find(ask, picked, out target);
        }

        /// <summary>
        /// The corpse's states (<see cref="LiftState"/>); at the disposal (state 1) for its place.
        /// </summary>
        public bool? State(World world, in DutyAsk ask, StateType state)
        {
            if (ask.Target is { Kind: PlanTarget.Found, Character: null } place && state is StateType.IsAtLocation)
                return goals.AtPlace(world, ask, place.Position);

            return goals.LiftState(world, ask, state);
        }

        /// <summary>
        /// Walks the carried corpse to the disposal, or lifts the plan's corpse (<see cref="TownDutySystem.StartDisposal"/>).
        /// </summary>
        public bool Start(World world, in DutyAsk ask, IReadOnlyList<PlanStep> plan) =>
            goals.Owner.StartDisposal(world, ask, plan, PickOf(goals._disposals, ask, p => p.Id)?.Machine);

        /// <summary>
        /// The member is no longer held.
        /// </summary>
        public void End(World world, int slot, CharacterId id, DutyEnd why) => goals.Owner.EndGoal(world, slot, id, why);
    }

    /// <summary>
    /// 112 FIND_CAGE_AND_PUT_IN (a living body, list B <c>!99</c>) and 281 FIND_CAGE_AND_PUT_DEADGUY_IN (plan <c>[1, 108, goal]</c>): the carrier
    /// walks the body to the cage its finder gives and locks it up there.
    /// </summary>
    sealed class Caging(TownGoals goals) : IDutyGoal
    {
        /// <summary>
        /// The goal's desire, <see cref="TownDutySystem.CageDesire"/> (<b>Verified</b>), as of the look; row 108's (slot 19) 1.0.
        /// </summary>
        public float Desire(World world, in DutyAsk ask) => ask.Row == TaskType.PutInCage ? 1 : goals.Load(world, ask)?.Cage ?? 0;

        /// <summary>
        /// The cage finder's pick (<c>FUN_1408d0d30</c>: 1.0 with a free cage, <see cref="TownDutySystem.Cage"/>).
        /// </summary>
        public float Find(World world, in DutyAsk ask, out PlanTarget target)
        {
            var picked = goals.Load(world, ask) is { CageAt: { } cage } ? PlanTarget.OfFixture(cage.Id, cage.Position) : default;
            return goals.Find(ask, picked, out target);
        }

        /// <summary>
        /// At the cage (state 1, <see cref="FixtureRegistry.AtCage"/>), the cage free (73), and the carried body's states.
        /// </summary>
        public bool? State(World world, in DutyAsk ask, StateType state)
        {
            if (ask.Target is { Kind: PlanTarget.Found, Fixture: { } id } && world.Fixtures.Find(id) is { } cage)
            {
                if (state == StateType.IsAtLocation) return FixtureRegistry.AtCage(cage, world.Characters.Previous[ask.Slot].Position, false);
                if (state == StateType.MachineHasFreeOperatorSlot) return FixtureRegistry.IsFree(world.Characters, cage);
            }

            return LoadState(world, ask, state);
        }

        /// <summary>
        /// Walks the body to the plan's cage (<see cref="TownDutySystem.StartCage"/>).
        /// </summary>
        public bool Start(World world, in DutyAsk ask, IReadOnlyList<PlanStep> plan) => goals.Owner.StartCage(world, ask, plan);

        /// <summary>
        /// The member is no longer held.
        /// </summary>
        public void End(World world, int slot, CharacterId id, DutyEnd why) => goals.Owner.EndGoal(world, slot, id, why);
    }

    /// <summary>
    /// 224 Intruder out and 274 Throwing out (the goal row acts; list A 137 / 164, list B 47): the carrier walks the body past its home's first
    /// door (224) or to its town's point outside the gates (274) and lets it go there.
    /// </summary>
    sealed class CarryingOut(TownGoals goals) : IDutyGoal
    {
        /// <summary>
        /// <see cref="TownDutySystem.ThrowOutDesire"/> (<b>Verified</b>), as of the look.
        /// </summary>
        public float Desire(World world, in DutyAsk ask) => goals.Load(world, ask)?.ThrowOut ?? 0;

        /// <summary>
        /// 137 INTRUDER_IS_OUTSIDE (<c>FUN_140598ef0</c>) and 164 INTRUDER_IS_OUTSIDE_GATES (<c>FUN_140598fa0</c>, both <b>Verified</b>), of the
        /// carried body when the target is none: true with no intruder or a friendly one, false while the asker carries it (both need it no longer
        /// carried by the asker); the carried body's other states.
        /// </summary>
        public bool? State(World world, in DutyAsk ask, StateType state)
        {
            if (state is not (StateType.IntruderIsOutside or StateType.IntruderIsOutsideGates)) return LoadState(world, ask, state);
            if (world.Characters.Cold(ask.Slot) is not { Carrying.IsNone: false }) return true;

            return goals.Load(world, ask) is { Friendly: true };
        }

        /// <summary>
        /// Walks the body out (<see cref="TownDutySystem.StartCarryOut"/>).
        /// </summary>
        public bool Start(World world, in DutyAsk ask, IReadOnlyList<PlanStep> plan) => goals.Owner.StartCarryOut(world, ask);

        /// <summary>
        /// The member is no longer held.
        /// </summary>
        public void End(World world, int slot, CharacterId id, DutyEnd why) => goals.Owner.EndGoal(world, slot, id, why);
    }

    /// <summary>
    /// 106 Find and put in bed (plan <c>[1, 99, 106]</c>): the carrier walks the body to the bed its finder gives and lays it in
    /// (<see cref="BedSystem.PutIn"/>).
    /// </summary>
    sealed class Bedding(TownGoals goals) : IDutyGoal
    {
        /// <summary>
        /// The goal's desire, <see cref="BedSystem.PutInDesire"/> (<c>FUN_1405a0d50</c>, <b>Verified</b>), as of the look; row 99's (slot 19) 1.0.
        /// </summary>
        public float Desire(World world, in DutyAsk ask) => ask.Row == TaskType.PutSomeoneInBed ? 1 : goals.Load(world, ask)?.Bed ?? 0;

        /// <summary>
        /// The bed search's pick (<c>FUN_1408cfe00</c>, <see cref="BedSystem.FindForCarrier"/>).
        /// </summary>
        public float Find(World world, in DutyAsk ask, out PlanTarget target)
        {
            var picked = goals.Load(world, ask) is { BedAt: { } bed } ? PlanTarget.OfFixture(bed.Id, bed.Position) : default;
            return goals.Find(ask, picked, out target);
        }

        /// <summary>
        /// At the bed (state 1) and the carried body's states.
        /// </summary>
        public bool? State(World world, in DutyAsk ask, StateType state)
        {
            if (state == StateType.IsAtLocation && ask.Target.Kind == PlanTarget.Found) return goals.AtPlace(world, ask, ask.Target.Position);

            return LoadState(world, ask, state);
        }

        /// <summary>
        /// Walks the body to a bed (<see cref="TownDutySystem.StartBed"/>).
        /// </summary>
        public bool Start(World world, in DutyAsk ask, IReadOnlyList<PlanStep> plan) => goals.Owner.StartBed(world, ask, plan);

        /// <summary>
        /// The member is no longer held.
        /// </summary>
        public void End(World world, int slot, CharacterId id, DutyEnd why) => goals.Owner.EndGoal(world, slot, id, why);
    }

    #endregion

    #region Stores

    /// <summary>
    /// 243 Getting food (plan <c>[1, 244, 243]</c>; a ditch first when 244's room test fails, the planner's 114 and 93): the member walks to the
    /// store its finder gives and takes one food item (244).
    /// </summary>
    sealed class FoodRun(TownGoals goals) : IDutyGoal
    {
        /// <summary>
        /// 243's desire (AI slots 26, 24, 25, <b>Verified</b>, docs/game/character-stats.md "Food from storage"), as of the look; row 244's 1.0.
        /// </summary>
        public float Desire(World world, in DutyAsk ask) => ask.Row == TaskType.GrabOneFood ? 1 : PickOf(goals._food, ask, p => p.Id)?.Want ?? 0;

        /// <summary>
        /// The food store finder's pick (<c>FUN_1408cb650</c>, <see cref="TownDutySystem.Store"/>).
        /// </summary>
        public float Find(World world, in DutyAsk ask, out PlanTarget target) => goals.FindStore(goals._food, ask, out target);

        /// <summary>
        /// At the store (1), food carried (149, <c>gotAFoodItem</c>), room for the item (82) and allies fighting (12) as of the look.
        /// </summary>
        public bool? State(World world, in DutyAsk ask, StateType state) => state switch
        {
            StateType.GotAFoodItem => world.Characters.Cold(ask.Slot) is { } cold && TownDutySystem.HoldsFood(cold),
            StateType.GotInventoryRoomForResources => PickOf(goals._food, ask, p => p.Id)?.Room ?? true,
            StateType.IsAlliesFighting => PickOf(goals._food, ask, p => p.Id)?.AlliesFighting ?? false,
            _ => goals.StoreState(world, ask, state),
        };

        /// <summary>
        /// Walks to the plan's store (<see cref="TownDutySystem.StartStoreRun"/>).
        /// </summary>
        public bool Start(World world, in DutyAsk ask, IReadOnlyList<PlanStep> plan) =>
            goals.Owner.StartStoreRun(world, ask, plan, TownDutyKind.Food, goals.StoreOf(goals._food, ask));

        /// <summary>
        /// The member is no longer held.
        /// </summary>
        public void End(World world, int slot, CharacterId id, DutyEnd why) => goals.Owner.EndGoal(world, slot, id, why);
    }

    /// <summary>
    /// 131 Looting food (plan <c>[1, 284, 131]</c>): the member walks to the store its finder gives and loots its first record (284).
    /// </summary>
    sealed class FoodLooting(TownGoals goals) : IDutyGoal
    {
        /// <summary>
        /// 131's desire and row 284's (slot 19) 1.0.
        /// </summary>
        public float Desire(World world, in DutyAsk ask) => 1;

        /// <summary>
        /// The food store finder's pick (<c>FUN_1408cb650</c>, a looter's: the looter's own stores left out).
        /// </summary>
        public float Find(World world, in DutyAsk ask, out PlanTarget target) => goals.FindStore(goals._loot, ask, out target);

        /// <summary>
        /// At the store (1), loot taken (68, <see cref="LootFrom"/>) and room (82) as of the look.
        /// </summary>
        public bool? State(World world, in DutyAsk ask, StateType state) => state switch
        {
            StateType.HaveSomeLootFrom => LootFrom(world, ask),
            StateType.GotInventoryRoomForResources => PickOf(goals._loot, ask, p => p.Id)?.Room ?? true,
            _ => goals.StoreState(world, ask, state),
        };

        /// <summary>
        /// Walks to the plan's store (<see cref="TownDutySystem.StartStoreRun"/>).
        /// </summary>
        public bool Start(World world, in DutyAsk ask, IReadOnlyList<PlanStep> plan) =>
            goals.Owner.StartStoreRun(world, ask, plan, TownDutyKind.LootFood, goals.StoreOf(goals._loot, ask));

        /// <summary>
        /// State 68 <c>HAVE_SOME_LOOT_FROM</c>: the evaluator of state 66 (<c>FUN_140597530</c>, the same function in the state table
        /// <c>FUN_140610250</c>, <b>Verified</b>), the looter carries the store's output item
        /// (<see cref="Production.ProductionSystem.CarriesOutputOf"/>); a container, with no storage cast, has none, so false.
        /// </summary>
        bool LootFrom(World world, in DutyAsk ask) =>
            goals.StoreOf(goals._loot, ask) is { } store && world.Machines.Find(store.Id) is { } machine
            && world.Characters.Cold(ask.Slot) is { } cold && Production.ProductionSystem.CarriesOutputOf(cold, machine);

        /// <summary>
        /// The member is no longer held.
        /// </summary>
        public void End(World world, int slot, CharacterId id, DutyEnd why) => goals.Owner.EndGoal(world, slot, id, why);
    }

    /// <summary>
    /// 184 Ditching loot (list A <c>!121</c>, plan <c>[1, 93, 184]</c>): the member walks to the storage its finder gives and puts its stolen goods in
    /// (93).
    /// </summary>
    sealed class Stashing(TownGoals goals) : IDutyGoal
    {
        /// <summary>
        /// 184's desire <see cref="StashWant"/>; row 93's 1.0.
        /// </summary>
        public float Desire(World world, in DutyAsk ask) => ask.Row == TaskType.FillMachine ? 1 : StashWant;

        /// <summary>
        /// The storage finder's pick (<c>FUN_14059b040</c>, <see cref="TownDutySystem.StashStore"/>).
        /// </summary>
        public float Find(World world, in DutyAsk ask, out PlanTarget target) => goals.FindStore(goals._stash, ask, out target);

        /// <summary>
        /// At the storage (1); 62 and 67 of the storage read as stolen goods still carried (the finder found it taking one), 63 never.
        /// </summary>
        public bool? State(World world, in DutyAsk ask, StateType state) => state switch
        {
            StateType.MachineHasEmptyInputs or StateType.HaveSomeResourcesFor when ask.Target.Kind == PlanTarget.Found =>
                world.Characters.Cold(ask.Slot) is { } cold && TownDutySystem.CarriesLoot(world, cold),
            StateType.IsMachineOutputFull when ask.Target.Kind == PlanTarget.Found => false,
            _ => goals.StoreState(world, ask, state),
        };

        /// <summary>
        /// Walks to the plan's storage (<see cref="TownDutySystem.StartStoreRun"/>).
        /// </summary>
        public bool Start(World world, in DutyAsk ask, IReadOnlyList<PlanStep> plan) =>
            goals.Owner.StartStoreRun(world, ask, plan, TownDutyKind.Stash, goals.StoreOf(goals._stash, ask));

        /// <summary>
        /// The member is no longer held.
        /// </summary>
        public void End(World world, int slot, CharacterId id, DutyEnd why) => goals.Owner.EndGoal(world, slot, id, why);
    }

    /// <summary>
    /// A store finder's answer (<see cref="Find(in DutyAsk, PlanTarget, out PlanTarget)"/>) from <paramref name="picks"/>.
    /// </summary>
    float FindStore(SortedDictionary<int, StorePick> picks, in DutyAsk ask, out PlanTarget target)
    {
        var picked = PickOf(picks, ask, p => p.Id) is { } pick ? PlanTarget.OfPlace(pick.At) : default;
        return Find(ask, picked, out target);
    }

    /// <summary>
    /// The store of the asker's pick in <paramref name="picks"/> and where it walks, or null.
    /// </summary>
    (string Id, Vector3 At)? StoreOf(SortedDictionary<int, StorePick> picks, in DutyAsk ask) =>
        PickOf(picks, ask, p => p.Id) is { } pick ? (pick.Store, pick.At) : null;

    /// <summary>
    /// At a found store's spot (state 1); null for the rest.
    /// </summary>
    bool? StoreState(World world, in DutyAsk ask, StateType state) =>
        state == StateType.IsAtLocation && ask.Target.Kind == PlanTarget.Found ? AtPlace(world, ask, ask.Target.Position) : null;

    #endregion

    #region Watches

    /// <summary>
    /// 212 Confiscating goods (plan <c>[1, 3, 212]</c>): the guard walks to the cage of the prisoner its finder gives and takes the stolen goods.
    /// </summary>
    sealed class Confiscating(TownGoals goals) : IDutyGoal
    {
        /// <summary>
        /// 212's desire (slot 19) 1.0; row 3's (AI slot 72 <c>FUN_140792650</c>, <b>Verified</b>) 0 while the guard is down, else 1.0.
        /// </summary>
        public float Desire(World world, in DutyAsk ask)
        {
            if (ask.Row != TaskType.Pickup) return 1;

            return world.Characters.Cold(ask.Slot) is { } cold && !CharacterFacts.Down(cold) ? 1 : 0;
        }

        /// <summary>
        /// <c>AI::findPrisonerWithContraband</c>'s pick (<c>FUN_1408cf6a0</c>, <see cref="TownDutySystem.Contraband"/>): the prisoner at its cage.
        /// </summary>
        public float Find(World world, in DutyAsk ask, out PlanTarget target)
        {
            var picked = PickOf(goals._prisoners, ask, p => p.Id) is { } p ? PlanTarget.OfCharacter(p.Who, p.At) : default;
            return goals.Find(ask, picked, out target);
        }

        /// <summary>
        /// At the prisoner's cage (1), its stolen goods still there (15) or taken (3).
        /// </summary>
        public bool? State(World world, in DutyAsk ask, StateType state)
        {
            if (ask.Target is not { Kind: PlanTarget.Found, Character: { } prisoner }) return null;

            var table = world.Characters;
            var held = table.IsAlive(prisoner) ? table.Cold(prisoner.Slot) : null;
            var cage = held?.InWhat is { } id ? world.Fixtures.Find(id) : null;
            bool stolen = held is { Inventory: { } inv } && goals.Owner.CarriesStolen(inv, held.Faction);
            return state switch
            {
                StateType.IsAtLocation => cage is not null && FixtureRegistry.AtCage(cage, table.Previous[ask.Slot].Position, false),
                StateType.IsItemLootable => stolen,
                StateType.IsSubjectInInventory => !stolen,
                _ => null,
            };
        }

        /// <summary>
        /// Walks to the prisoner's cage (<see cref="TownDutySystem.StartConfiscate"/>).
        /// </summary>
        public bool Start(World world, in DutyAsk ask, IReadOnlyList<PlanStep> plan) => goals.Owner.StartConfiscate(world, ask, plan);

        /// <summary>
        /// The member is no longer held.
        /// </summary>
        public void End(World world, int slot, CharacterId id, DutyEnd why) => goals.Owner.EndGoal(world, slot, id, why);
    }

    /// <summary>
    /// 158 / 283 Shooing (list A <c>!108</c>, plan <c>[161, goal]</c>): the member walks at the stranger its finder gives and sends it the shoo event
    /// until it leaves.
    /// </summary>
    sealed class Shooing(TownGoals goals) : IDutyGoal
    {
        /// <summary>
        /// 158's desire and row 161's (slot 19) 1.0; 283's (AI slot 71, <b>Verified</b>) 1.0 while the squad's home resolves and is not public.
        /// </summary>
        public float Desire(World world, in DutyAsk ask)
        {
            if (ask.Row != TaskType.ShooStrangersOutOfMyBuildingIfPrivate) return 1;

            return ask.Squad.HomeBuilding is not null && ask.Squad.Ai is { HomePublic: false } ? 1 : 0;
        }

        /// <summary>
        /// <c>AI::findStrangersInMyBuilding</c>'s pick (<c>FUN_1408ced70</c>, <see cref="TownDutySystem.Stranger"/>).
        /// </summary>
        public float Find(World world, in DutyAsk ask, out PlanTarget target)
        {
            var picked = PickOf(goals._strangers, ask, p => p.Id) is { } p ? PlanTarget.OfCharacter(p.Who, p.At) : default;
            return goals.Find(ask, picked, out target);
        }

        /// <summary>
        /// 110 (the dialogue delivered) false while the shoo runs: the act-out ends the goal.
        /// </summary>
        public bool? State(World world, in DutyAsk ask, StateType state) =>
            state == StateType.IsDialogDelivered110 && ask.Target.Kind == PlanTarget.Found ? false : null;

        /// <summary>
        /// Starts the shoo of the plan's stranger (<see cref="TownDutySystem.StartShoo"/>).
        /// </summary>
        public bool Start(World world, in DutyAsk ask, IReadOnlyList<PlanStep> plan) => goals.Owner.StartShoo(world, ask, plan);

        /// <summary>
        /// The member is no longer held.
        /// </summary>
        public void End(World world, int slot, CharacterId id, DutyEnd why) => goals.Owner.EndGoal(world, slot, id, why);
    }

    /// <summary>
    /// 216 Ransacking town (list B 5, plan <c>[156, 216]</c>, <c>[6, 156, 216]</c> with no weapon drawn: the AI's step 6 draws it): the member runs
    /// into the building its finder draws; the goal is done once inside.
    /// </summary>
    sealed class Ransacking(TownGoals goals) : IDutyGoal
    {
        /// <summary>
        /// 216's desire (slot 19) and row 156's (slot 57) 1.0.
        /// </summary>
        public float Desire(World world, in DutyAsk ask) => 1;

        /// <summary>
        /// <c>AI::findRandomBuildingInTown</c>'s draw (<c>FUN_1408cb510</c>, <see cref="TownDutySystem.RansackTarget"/>), one per look.
        /// </summary>
        public float Find(World world, in DutyAsk ask, out PlanTarget target)
        {
            var picked = PickOf(goals._raids, ask, p => p.Id) is { } p ? PlanTarget.OfPlace(p.At) : default;
            return goals.Find(ask, picked, out target);
        }

        /// <summary>
        /// Inside the building (107); its way in (59, 90) as the finder found it (a building without one is not drawn).
        /// </summary>
        public bool? State(World world, in DutyAsk ask, StateType state)
        {
            if (ask.Target.Kind != PlanTarget.Found) return null;

            return state switch
            {
                StateType.AmInsideTargetBuilding => world.Sight.BuildingOf(ask.Slot) is { } inside && inside == goals.RaidKey(ask),
                StateType.DestinationIsAccessible or StateType.DestinationNotBlockedByFortifications => true,
                _ => null,
            };
        }

        /// <summary>
        /// Runs to the plan's building (<see cref="TownDutySystem.StartRansack"/>).
        /// </summary>
        public bool Start(World world, in DutyAsk ask, IReadOnlyList<PlanStep> plan) =>
            goals.Owner.StartRansack(world, ask, plan, PickOf(goals._raids, ask, p => p.Id) is { } p ? (p.Key!, p.Door, p.At) : null);

        /// <summary>
        /// The member is no longer held.
        /// </summary>
        public void End(World world, int slot, CharacterId id, DutyEnd why) => goals.Owner.EndGoal(world, slot, id, why);
    }

    /// <summary>
    /// The building of the asker's ransack: its act-out's while the goal holds it, else its pick's.
    /// </summary>
    string? RaidKey(in DutyAsk ask) => Held(ask)?.Target ?? PickOf(_raids, ask, p => p.Id)?.Key;

    #endregion
}
