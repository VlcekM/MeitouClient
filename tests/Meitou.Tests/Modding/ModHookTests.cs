using System.Numerics;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Save;
using Meitou.Data.World;
using Meitou.Simulation;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Items;
using Meitou.Simulation.Locks;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Player;
using Meitou.Simulation.Population;
using Meitou.Simulation.Saving;
using Meitou.Tests.Simulation;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Modding;

/// <summary>
/// The engine hooks for code mods (docs/modding.md "World hooks", "Driving NPCs", "Item use") on synthetic worlds.
/// </summary>
public class ModHookTests
{
    #region World hooks

    /// <summary>
    /// The synthetic town's faction (<c>20-t</c>, Testfolk).
    /// </summary>
    const string Faction = "20-t";

    /// <summary>
    /// A world of the roaming synthetic towns with population and movement, the camera on the first town.
    /// </summary>
    static (SimWorld World, PopulationSystem Population) Start(IReadOnlyList<FcsRecord>? extra = null, bool keepRoamers = true)
    {
        var db = SyntheticTown.Database(roaming: true, extra: extra);
        var walk = new OpenGroundWalkability(SyntheticTown.Ground);
        var settings = new PopulationSettings
        {
            CheckEveryTicks = 15, UnloadGraceSeconds = 600, RoamUnloadGraceSeconds = 5, KeepRoamers = keepRoamers,
        };
        var population = new PopulationSystem(SyntheticTown.Data(db), settings);
        var world = new SimWorld(new WorldSettings { Seed = 3, Threads = 1, PublishSnapshots = false }, walk,
            [population, new MovementSystem(new PathService(walk, true))]);
        world.Commands.Enqueue(new FocusCommand(SyntheticTown.Centre) { Tick = 0 });
        return (world, population);
    }

    /// <summary>
    /// <c>SpawnPlatoon</c> adds an unloaded platoon of the template inside the ring with its purse and size, the same spot for the same seed; an
    /// unknown template, a radius that is not finite or a template that plans no one adds nothing and takes no platoon id. Catches a spawn
    /// outside the ring, a lost purse, or a refusal that still adds or shifts later spawns' ids.
    /// </summary>
    [Fact]
    public void A_spawned_platoon_lies_in_its_ring_unloaded_with_its_purse_and_bad_input_adds_nothing()
    {
        var (w, population) = Start();
        using var world = w;
        int faction = population.Data.FactionIndex(Faction);
        var around = new Vector2(20000, 20000);
        int before = w.Platoons.Count;

        int id = population.SpawnPlatoon(w, faction, "12-t", 0, around, 100, 200, money: 750);
        var p = w.Platoons.Find(id)!;
        float distance = Vector2.Distance(around, p.Position);

        Assert.Equal((PlatoonState.Unloaded, "12-t", faction, 3, 750L), (p.State, p.TemplateId, p.Faction, p.Size, p.Money));
        Assert.InRange(distance, 100, 200);

        int next = w.Platoons.PeekId;

        Assert.Equal(-1, population.SpawnPlatoon(w, faction, "no-such-template", 0, around, 100, 200));
        Assert.Equal(-1, population.SpawnPlatoon(w, faction, "12-t", 0, around, 100, float.NaN));
        Assert.Equal(-1, population.SpawnPlatoon(w, faction, "11-t", 0, around, 100, 200));   // gated behind a state that does not hold: no one
        Assert.Equal(before + 1, w.Platoons.Count);
        Assert.Equal(next, w.Platoons.PeekId);
        Assert.Equal(next, population.SpawnPlatoon(w, faction, "12-t", 0, around, 100, 200));

        var (w2, population2) = Start();
        using var again = w2;
        int id2 = population2.SpawnPlatoon(w2, faction, "12-t", 0, around, 100, 200, money: 750);

        Assert.Equal(p.Position, w2.Platoons.Find(id2)!.Position);
        Assert.Equal(id + 1, population2.SpawnPlatoon(w2, faction, "12-t", 0, around, 100, 200));   // the same id as after the refusals above
    }

    /// <summary>
    /// A held roaming platoon's stand-in stays put while the other one travels, and an unknown platoon cannot be held. Catches a stand-in that
    /// still moves (the mod and the population fight over it).
    /// </summary>
    [Fact]
    public void A_held_stand_in_stays_while_the_other_travels()
    {
        var (w, _) = Start();
        using var world = w;
        w.RunTicks(700);
        var travellers = w.Platoons.All.ToList();

        Assert.Equal(2, travellers.Count);
        Assert.False(PopulationSystem.HoldPlatoon(w, -1, true));

        // The camera leaves and the roamers walk out of its block, so both unload and walk on as stand-ins.
        w.Commands.Enqueue(new FocusCommand(new Vector3(-2000, 0, 2000)) { Tick = w.Tick });
        RoamingTests.WalkOut(w, travellers);
        for (int i = 0; i < 40 && travellers.Any(p => p.State != PlatoonState.Unloaded); i++) w.RunTicks(150);

        Assert.True(PopulationSystem.HoldPlatoon(w, travellers[0].Id, true));

        var before = travellers.Select(p => p.Position).ToList();
        w.RunTicks(300);

        Assert.Equal(before[0], travellers[0].Position);
        Assert.True(Vector2.Distance(before[1], travellers[1].Position) > 100);
    }

    /// <summary>
    /// <c>RequestCampaign</c> queues the campaign on the faction's war manager once; a second identical request, an unknown campaign and a delay
    /// that is not finite are refused. Catches a request that never reaches the queue or a queue that takes duplicates.
    /// </summary>
    [Fact]
    public void A_campaign_request_is_queued_once_and_bad_requests_are_refused()
    {
        var campaign = SyntheticTown.Rec("50-t", FcsRecordType.FactionCampaign, "Test raid");
        var (w, population) = Start([campaign]);
        using var world = w;
        int faction = population.Data.FactionIndex(Faction);
        var target = CampaignTown.OfSite(1);

        Assert.True(population.RequestCampaign(w, faction, "50-t", target, 2));
        Assert.Single(population.WarOf(faction)!.Requests, r => r.CampaignId == "50-t" && r.Target == target);
        Assert.False(population.RequestCampaign(w, faction, "50-t", target, 2));
        Assert.False(population.RequestCampaign(w, faction, "no-such-campaign", target));
        Assert.False(population.RequestCampaign(w, faction, "50-t", CampaignTown.OfSite(0), float.PositiveInfinity));
    }

    /// <summary>
    /// <c>CharacterCold.FedTo</c> comes before the game's floors, capped at the most hunger, and a level that is not finite falls back to the game's
    /// rule. Catches a mod's level the player's-faction rule overrides (a carried captive of the player's faction would starve).
    /// </summary>
    [Fact]
    public void A_mods_fed_level_comes_first_and_is_capped()
    {
        using var world = new SimWorld(new WorldSettings { Seed = 3, PublishSnapshots = false }, new OpenGroundWalkability(SyntheticTown.Ground), []);
        var bodies = new BodySystem(Bodies.BodyFixtures.BaseGame);
        var medical = Bodies.BodyFixtures.Healthy();
        var cold = new CharacterCold { Name = "captive", IsPlayer = true };

        Assert.Equal(MedicalContext.NoHungerFloor, bodies.HungerFloor(world, cold, medical));

        cold.FedTo = 2;

        Assert.Equal(2, bodies.HungerFloor(world, cold, medical));

        cold.FedTo = 10;

        Assert.Equal(MedicalState.MaxHunger, bodies.HungerFloor(world, cold, medical));

        cold.FedTo = float.NaN;

        Assert.Equal(MedicalContext.NoHungerFloor, bodies.HungerFloor(world, cold, medical));
    }

    /// <summary>
    /// A spawned platoon that never loads is kept off screen with the keep switch off, and a save writes it, with its mod marks and no one else's;
    /// loaded again it is the same roaming platoon with its tag, though its template is a bar squad's (which a save's platoon would be pinned by).
    /// Catches the save dropping a spawn that never loaded, the orphan timer despawning
    /// it, or the tag lost (a mod could not find its squad after a load, when platoon ids are given anew).
    /// </summary>
    [Fact]
    public void A_spawned_platoon_that_never_loads_is_kept_and_reloads_with_its_tag()
    {
        var (w, population) = Start(keepRoamers: false);
        using var world = w;
        int faction = population.Data.FactionIndex(Faction);
        int id = population.SpawnPlatoon(w, faction, "10-t", 0, new Vector2(20000, 20000), 100, 200, money: 750);   // a bar squad's template
        var p = w.Platoons.Find(id)!;
        p.ModTag = "crew|7";
        w.RunTicks(2400);   // past the 60 s orphan limit that drops a stand-in nobody keeps

        Assert.Same(p, w.Platoons.Find(id));
        Assert.Equal(PlatoonState.Unloaded, p.State);

        var game = SaveCapture.Capture(w, population.Data, new SaveClock(1, 13, 0));
        var record = game.Platoons.Single(x => x.Record.Strings.GetValueOrDefault(ModPlatoonSave.TagKey) == "crew|7").Record;

        Assert.True(record.Bools[ModPlatoonSave.SpawnKey]);
        Assert.All(game.Platoons.Where(x => x.Record != record), x => Assert.False(x.Record.Bools.ContainsKey(ModPlatoonSave.SpawnKey)));

        var (w2, population2) = Start();
        using var back = w2;
        SaveLoader.Load(w2, game, population2.Data, new SaveLoadOptions { ApplyRelations = false });
        var again = Assert.Single(w2.Platoons.All, x => x.ModTag == "crew|7");

        var reloaded = (again.ModSpawned, again.TemplateId, again.Faction, again.Size, again.Money, again.Pinned);
        Assert.Equal((true, "10-t", faction, p.Size, 750L, false), reloaded);
        Assert.Equal(p.Key, again.Key);   // planned again as the same squad
        Assert.InRange(Vector2.Distance(p.Position, again.Position), 0, 1);
    }

    /// <summary>
    /// <c>SendPlatoon</c> is refused for a platoon not held, a point that is not finite and a character not alive; a held one's stand-in then
    /// travels to the point and stops there, and giving the platoon back clears the goal. Catches a held stand-in that never moves for its mod, one
    /// that overshoots, or a goal that outlives the hold.
    /// </summary>
    [Fact]
    public void A_held_platoon_sent_to_a_point_travels_there_and_stops()
    {
        var (w, population) = Start();
        using var world = w;
        int faction = population.Data.FactionIndex(Faction);
        int id = population.SpawnPlatoon(w, faction, "12-t", 0, new Vector2(20000, 20000), 100, 200);
        var p = w.Platoons.Find(id)!;
        var goal = p.Position + new Vector2(1500, 0);

        Assert.False(PopulationSystem.SendPlatoon(w, id, goal));
        Assert.True(PopulationSystem.HoldPlatoon(w, id, true));
        Assert.False(PopulationSystem.SendPlatoon(w, id, new Vector2(float.NaN, 0)));
        Assert.False(PopulationSystem.SendPlatoon(w, id, CharacterId.None));
        Assert.True(PopulationSystem.SendPlatoon(w, id, goal));

        for (int i = 0; i < 40 && Vector2.Distance(p.Position, goal) > 60; i++) w.RunTicks(300);
        var arrived = p.Position;
        w.RunTicks(150);

        Assert.InRange(Vector2.Distance(arrived, goal), 0, 60.01f);
        Assert.Equal(arrived, p.Position);
        Assert.True(PopulationSystem.HoldPlatoon(w, id, false));
        Assert.Null(p.ModGoal);
    }

    /// <summary>
    /// <c>EndContract</c> ends the running contract (its end talk kept for its target), so the same contract starts afresh, which the game's own
    /// start ignores while it runs; an any-range contract is saved with its flag and a plain one writes no key. Catches a contract a mod can give
    /// only once, or an any-range assault lost on a save.
    /// </summary>
    [Fact]
    public void EndContract_lets_the_same_contract_start_again_and_any_range_is_saved_with_it()
    {
        var guard = new Meitou.Data.Gameplay.Ai.AiPackage { Id = "guard", Name = "guard", Roles = [[], [], [], [], []] };
        var board = new Meitou.Simulation.Ai.Blackboard([]);
        var target = new CharacterId(5, 1);
        var record = new FcsRecord { StringId = "p", Name = "p", RecordType = FcsRecordType.Platoon };

        Assert.False(board.EndContract());

        board.StartContract(guard, 0, 8, target: target, anyRange: true);
        board.StartContract(guard, 4, 8, target: target);
        AiSave.Write(record, AiSave.Of(board));

        Assert.Equal((8.0, true), (board.ContractEnd, board.MissionAnyRange));
        Assert.True(AiSave.Read(record)!.AnyRange);
        Assert.True(board.EndContract());
        Assert.Equal((null, CharacterId.None, false), (board.Contract, board.MissionTarget, board.MissionAnyRange));
        Assert.Equal((guard, target), board.TakeEnded());

        board.StartContract(guard, 10, 8, target: target);
        AiSave.Write(record, AiSave.Of(board));

        Assert.Equal((18.0, false), (board.ContractEnd, board.MissionAnyRange));
        Assert.False(record.Bools.ContainsKey(AiSave.AnyRangeKey));
    }

    /// <summary>
    /// A contract's assault takes its target beyond the ROAM_FAR reach only with <c>AnyRange</c>; <c>EndContract</c> then lets the same contract
    /// start again with the flag (the game ignores a start of the contract already running). Catches a mod's assault that never reaches its
    /// named target, or a flag that leaks to targets the mod did not name.
    /// </summary>
    [Fact]
    public void An_any_range_contract_assaults_its_named_target_beyond_the_roam_reach()
    {
        var db = SyntheticTown.Database(anatomy: true, extra: Simulation.AiTests.Records(),
            changePatrol: p => p.References["AI packages"] = [SyntheticTown.Ref("111-t", 0), SyntheticTown.Ref("112-t", 1)]);
        var data = SyntheticTown.Data(db);
        var walk = new OpenGroundWalkability(SyntheticTown.Ground);
        var systems = StandardSystems.Build(data, walk, new StandardSystemOptions
        {
            Parts = StandardParts.Population | StandardParts.Movement | StandardParts.Ai | StandardParts.Body, SynchronousPaths = true,
        }).Systems;
        using var world = new SimWorld(new WorldSettings { Seed = 5, Threads = 1, StartHours = 24 + 13, PublishSnapshots = false }, walk, systems);
        world.Commands.Enqueue(new FocusCommand(SyntheticTown.Centre) { Tick = 0 });
        world.RunTicks(50);
        var ai = world.Systems.OfType<Meitou.Simulation.Ai.AiSystem>().Single();
        var squads = world.Squads.All.Where(s => s.Ai is not null).Take(2).ToList();
        var (me, target) = (squads[0].Members[0], squads[1].Members[0]);

        // The target, of another faction, stands well past the 6517-unit reach.
        world.Characters.Cold(target.Slot)!.Faction = data.PlayerFaction;
        var here = world.Characters.Previous[me.Slot].Position;
        world.Characters.Place(target.Slot, here + new Vector3(8000, 0, 0), 0);
        world.Commands.Enqueue(new Meitou.Simulation.Ai.StartContract(squads[0].Id, "115-t", 8, target) { Tick = world.Tick });
        world.RunTick();

        Assert.Equal(target, squads[0].Ai!.MissionTarget);
        Assert.True(ai.AssaultTargetFor(world, me.Slot, squads[0].Ai).IsNone);

        world.Commands.Enqueue(new Meitou.Simulation.Ai.EndContract(squads[0].Id) { Tick = world.Tick });
        world.Commands.Enqueue(new Meitou.Simulation.Ai.StartContract(squads[0].Id, "115-t", 8, target) { AnyRange = true, Tick = world.Tick });
        world.RunTick();

        Assert.True(squads[0].Ai!.MissionAnyRange);
        Assert.Equal(target, ai.AssaultTargetFor(world, me.Slot, squads[0].Ai));
    }

    #endregion

    #region Item use

    /// <summary>
    /// A token for a right click on <paramref name="item"/> at <paramref name="index"/> of its inventory.
    /// </summary>
    static InventoryItemToken Token(int index, ItemInstance item) =>
        new(index, item.Record, item.Section, item.X, item.Y, item.Quantity, item.Quality) { ModTag = item.ModTag };

    /// <summary>
    /// A world whose player system routes <c>note</c> clicks to a handler that records each item it gets and uses the click when
    /// <paramref name="take"/> (else lets it fall back to a quick move).
    /// </summary>
    static SimWorld NoteWorld(List<ItemInstance> used, bool take = true)
    {
        var extensions = new SimulationExtensions();
        extensions.ItemUses["note"] = use =>
        {
            used.Add(use.Item);
            return take;
        };
        return new SimWorld(new WorldSettings { Seed = 3, PublishSnapshots = false }, new OpenGroundWalkability(SyntheticTown.Ground),
                [new PlayerSystem { Extensions = extensions }]);
    }

    /// <summary>
    /// A player character holding <paramref name="root"/>.
    /// </summary>
    static CharacterId Reader(SimWorld world, Inventory root) =>
        world.Characters.Spawn(new CharacterHot { Position = new Vector3(1000, 150, 1000) },
            new CharacterCold { Name = "reader", Inventory = root, IsPlayer = true }, 0);

    /// <summary>
    /// A right click with <paramref name="token"/> by <paramref name="who"/>, run.
    /// </summary>
    static void Click(SimWorld world, CharacterId who, InventoryItemToken token)
    {
        world.Commands.Enqueue(new QuickMoveItem(who, token, false, true) { Tick = world.Tick });
        world.RunTick();
    }

    /// <summary>
    /// A root of a 2 x 2 main grid holding <paramref name="note"/> and an open-able 2 x 2 bag (empty), as the backpack section holds it.
    /// </summary>
    static (Inventory Root, Inventory Bag) WithBag(ItemInstance note)
    {
        var root = new Inventory(2, 2);
        var bag = new Inventory(2, 2);
        root.Items.Add(note);
        root.Items.Add(new ItemInstance { Record = "bag", Section = "backpack_attach", Contents = bag });
        return (root, bag);
    }

    /// <summary>
    /// A click queued for one tagged note, with the bag open, reaches neither the handler nor the quick move once another note with the same record,
    /// cell, quantity and quality but another tag took its place: the replacement stays in the main grid. Catches a stale click consuming the
    /// wrong ledger, or the fallback moving it into the bag (review P1, and the follow-up's merge blocker).
    /// </summary>
    [Fact]
    public void A_stale_click_with_the_bag_open_touches_neither_handler_nor_fallback_on_a_differently_tagged_note()
    {
        var used = new List<ItemInstance>();
        using var world = NoteWorld(used);
        var first = new ItemInstance { Record = "note", ModTag = "rival|1" };
        var (root, bag) = WithBag(first);
        var who = Reader(world, root);
        var stale = Token(0, first);
        var second = new ItemInstance { Record = "note", ModTag = "rival|2" };
        root.Items[0] = second;

        Click(world, who, stale);

        Assert.Empty(used);
        Assert.Equal(("main", true), (second.Section, root.Items.Contains(second)));
        Assert.Empty(bag.Items);

        Click(world, who, Token(0, second));

        Assert.Equal([second], used);
    }

    /// <summary>
    /// A valid click whose handler returns false falls back to the quick move: with the bag open, the note goes into the bag. Catches a guard that
    /// also blocks a handler's deliberate fallback.
    /// </summary>
    [Fact]
    public void A_valid_click_whose_handler_declines_falls_back_to_the_quick_move()
    {
        var used = new List<ItemInstance>();
        using var world = NoteWorld(used, take: false);
        var note = new ItemInstance { Record = "note", ModTag = "rival|1" };
        var (root, bag) = WithBag(note);
        var who = Reader(world, root);

        Click(world, who, Token(0, note));

        Assert.Equal([note], used);
        Assert.Contains(note, bag.Items);
        Assert.DoesNotContain(note, root.Items);
    }

    /// <summary>
    /// A locked-on note (worn and locked, as shackles) is refused as a quick move refuses it; unlocked, the handler gets it. Catches a mod's use
    /// that bypasses the quick move's guards (review P2).
    /// </summary>
    [Fact]
    public void A_locked_on_item_never_reaches_the_handler()
    {
        var used = new List<ItemInstance>();
        using var world = NoteWorld(used);
        var root = new Inventory(2, 2);
        var note = new ItemInstance { Record = "note", Section = "shirt", Lock = new LockState { Locked = true, Level = 10, Hardness = 5 } };
        root.Items.Add(note);
        var who = Reader(world, root);

        Assert.True(note.LockedOn);

        Click(world, who, Token(0, note));

        Assert.Empty(used);

        note.Lock = null;
        Click(world, who, Token(0, note));

        Assert.Equal([note], used);
    }

    /// <summary>
    /// A right click on a mod's item in a player's own window runs its handler: true keeps the click (the item stays in the bag), false lets it
    /// quick-move as usual; another record never reaches the handler. Catches a handler that does not run, or a used click that still moves it.
    /// </summary>
    [Fact]
    public void A_mods_item_use_takes_the_click_only_when_it_returns_true()
    {
        var extensions = new SimulationExtensions();
        var used = new List<string>();
        bool take = true;
        extensions.ItemUses["note"] = use =>
        {
            used.Add(use.Item.Record);
            return take;
        };
        using var world = new SimWorld(new WorldSettings { Seed = 3, PublishSnapshots = false }, new OpenGroundWalkability(SyntheticTown.Ground),
                [new PlayerSystem { Extensions = extensions }]);
        var root = new Inventory(2, 2);
        var bag = new Inventory(2, 2);
        root.Items.Add(new ItemInstance { Record = "bag", Section = "backpack_attach", Contents = bag });
        var note = new ItemInstance { Record = "note", Section = "backpack_content" };
        var food = new ItemInstance { Record = "food", Section = "backpack_content", X = 1 };
        bag.Items.AddRange([note, food]);
        var who = world.Characters.Spawn(new CharacterHot { Position = new Vector3(1000, 150, 1000) },
            new CharacterCold { Name = "reader", Inventory = root, IsPlayer = true }, 0);

        void RightClick(ItemInstance item)
        {
            int index = root.All().ToList().IndexOf(item);
            world.Commands.Enqueue(new QuickMoveItem(who, Token(index, item), false, true) { Tick = world.Tick });
            world.RunTick();
        }

        RightClick(note);

        Assert.Equal(["note"], used);
        Assert.Contains(note, bag.Items);

        RightClick(food);

        Assert.Equal(["note"], used);
        Assert.Contains(food, root.Items);

        take = false;
        RightClick(note);

        Assert.Equal(["note", "note"], used);
        Assert.Contains(note, root.Items);
    }

    /// <summary>
    /// Items differing only in their mod tag never share a stack, through <c>Inventory.Add</c> or <c>InventoryCommands.Stackable</c>. Catches two
    /// ledgers naming different rivals merged into one.
    /// </summary>
    [Fact]
    public void Differently_tagged_items_never_stack()
    {
        static ItemInstance Note(string tag) => new() { Record = "note", Name = "note", StackLimit = 10, ModTag = tag };
        var inventory = new Inventory(3, 1);

        Assert.True(inventory.Add(Note("rival|1")));
        Assert.True(inventory.Add(Note("rival|2")));
        Assert.True(inventory.Add(Note("rival|1")));
        Assert.Equal(2, inventory.Items.Count);
        Assert.False(InventoryCommands.Stackable(Note("rival|1"), Note("rival|2")));
        Assert.True(InventoryCommands.Stackable(Note("rival|1"), Note("rival|1")));
    }

    /// <summary>
    /// A mod tag survives a save's inventory round trip as <c>meitou mod tag</c>, and an untagged item writes no such key (vanilla items stay as
    /// they were). Catches a tag lost on save or a key written for every item.
    /// </summary>
    [Fact]
    public void A_mod_tag_round_trips_through_a_save_and_an_untagged_item_writes_no_key()
    {
        var file = new SaveFile();
        var state = new FcsRecord { Type = (int)FcsRecordType.InventoryState, StringId = "1-test-INGAME" };
        file.Records.Add(state);
        var tagged = new SaveItem { BaseId = "note", ModTag = "rival|7" };
        var plain = new SaveItem { BaseId = "food" };

        SaveInventory.Replace(file, state, [tagged, plain], n => $"{n}-test-INGAME");
        var back = SaveInventory.Read(SaveFile.Read(file.ToBytes()), SaveFile.Read(file.ToBytes()).Records[0]);
        var plainRecord = file.Records.Single(r => r.Strings.GetValueOrDefault("base data sid") == "food");

        Assert.Equal(["rival|7", ""], back.Select(i => i.ModTag));
        Assert.False(plainRecord.Strings.ContainsKey(SaveItem.ModTagKey));
    }

    /// <summary>
    /// Inventories differing only in a mod tag hash apart, and an untagged inventory hashes as it did before tags existed. Catches worlds that
    /// diverge through tags (stacking, handlers) while their hashes agree (review P2).
    /// </summary>
    [Fact]
    public void A_mod_tag_changes_the_inventory_hash_only_when_set()
    {
        static ulong HashOf(string tag)
        {
            var inventory = new Inventory(2, 2);
            inventory.Items.Add(new ItemInstance { Record = "note", ModTag = tag });
            var h = new StateHasher();
            inventory.Hash(ref h);
            return h.Value;
        }

        static ulong Untagged()
        {
            var h = new StateHasher();
            h.Add(1);
            h.Add(Rng.StableHash("note"));
            h.Add(Rng.StableHash("main"));
            h.Add(0);
            h.Add(0);
            h.Add(1);
            h.Add(0);
            h.Add(0f);
            h.Add(1f);
            return h.Value;
        }

        Assert.NotEqual(HashOf("rival|1"), HashOf("rival|2"));
        Assert.NotEqual(HashOf(""), HashOf("rival|1"));
        Assert.Equal(Untagged(), HashOf(""));
    }

    /// <summary>
    /// A saved ground item retagged in place is saved and reloaded with its new tag, while an untouched zone is left byte for byte. Catches the
    /// zone-save shortcut keeping the old tag (review P2, the path #653 touched).
    /// </summary>
    [Fact]
    public void A_retagged_ground_item_reloads_with_its_new_tag_and_an_untouched_zone_stays_byte_identical()
    {
        var file = new FcsFile();
        file.Records.Add(SyntheticTown.Rec("note", FcsRecordType.Item, "Note"));
        var db = new GameDatabase();
        db.Apply(file, "test.mod");
        var factory = new ItemFactory(db, Bodies.BodyFixtures.BaseGame);
        using var world = new SimWorld(new WorldSettings { Seed = 3, PublishSnapshots = false }, new OpenGroundWalkability(SyntheticTown.Ground), []);
        var note = factory.Create("note")!;
        note.ModTag = "rival|1";
        world.GroundItems.Drop(note, new Vector3(100, 300, 100), 0);
        var save = new SaveBuilder(1).Game;
        GroundItemSave.Write(save, world, factory);
        var zone = save.Zones[WorldLayout.ZoneOf(100, 100)];
        note.Saved = zone.File.Records.Single(r => r.Strings.GetValueOrDefault("base data sid") == "note");
        var written = zone.File.ToBytes();

        GroundItemSave.Write(save, world, factory);

        Assert.Equal(written, zone.File.ToBytes());

        note.ModTag = "rival|2";
        GroundItemSave.Write(save, world, factory);
        using var loaded = new SimWorld(new WorldSettings { Seed = 3, PublishSnapshots = false }, new OpenGroundWalkability(SyntheticTown.Ground), []);
        GroundItemSave.Load(save, loaded, factory);

        Assert.Equal("rival|2", Assert.Single(loaded.GroundItems.All).Item.ModTag);
    }

    #endregion
}
