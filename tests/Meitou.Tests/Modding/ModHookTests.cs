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
    static (SimWorld World, PopulationSystem Population) Start(IReadOnlyList<FcsRecord>? extra = null)
    {
        var db = SyntheticTown.Database(roaming: true, extra: extra);
        var walk = new OpenGroundWalkability(SyntheticTown.Ground);
        var population = new PopulationSystem(SyntheticTown.Data(db),
                new PopulationSettings { CheckEveryTicks = 15, UnloadGraceSeconds = 600, RoamUnloadGraceSeconds = 5 });
        var world = new SimWorld(new WorldSettings { Seed = 3, Threads = 1, PublishSnapshots = false }, walk,
            [population, new MovementSystem(new PathService(walk, true))]);
        world.Commands.Enqueue(new FocusCommand(SyntheticTown.Centre) { Tick = 0 });
        return (world, population);
    }

    /// <summary>
    /// <c>SpawnPlatoon</c> adds an unloaded platoon of the template inside the ring with its purse and size, the same spot for the same seed; an
    /// unknown template, a radius that is not finite or a template that plans no one adds nothing and takes no platoon id. Catches a spawn outside the ring, a lost purse, or a
    /// refusal that still adds or shifts later spawns' ids.
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

        var (w2, population2) = Start();
        using var again = w2;
        int id2 = population2.SpawnPlatoon(w2, faction, "12-t", 0, around, 100, 200, money: 750);

        Assert.Equal(p.Position, w2.Platoons.Find(id2)!.Position);
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

        // The camera leaves, so both roamers unload and walk on as stand-ins.
        w.Commands.Enqueue(new FocusCommand(new Vector3(-2000, 0, 2000)) { Tick = w.Tick });
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

    #endregion

    #region Item use

    /// <summary>
    /// A token for a right click on <paramref name="item"/> at <paramref name="index"/> of its inventory.
    /// </summary>
    static InventoryItemToken Token(int index, ItemInstance item) =>
        new(index, item.Record, item.Section, item.X, item.Y, item.Quantity, item.Quality) { ModTag = item.ModTag };

    /// <summary>
    /// A world whose player system routes <c>note</c> clicks to a handler that records each item it gets and uses the click.
    /// </summary>
    static SimWorld NoteWorld(List<ItemInstance> used)
    {
        var extensions = new SimulationExtensions();
        extensions.ItemUses["note"] = use =>
        {
            used.Add(use.Item);
            return true;
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
    /// A click queued for one tagged note never reaches another note that took its cell with the same record, quantity and quality but another tag.
    /// Catches a stale click consuming the wrong rival's ledger (review P1).
    /// </summary>
    [Fact]
    public void A_stale_click_never_reaches_a_differently_tagged_note_in_the_same_cell()
    {
        var used = new List<ItemInstance>();
        using var world = NoteWorld(used);
        var root = new Inventory(2, 2);
        var first = new ItemInstance { Record = "note", ModTag = "rival|1" };
        root.Items.Add(first);
        var who = Reader(world, root);
        var stale = Token(0, first);
        var second = new ItemInstance { Record = "note", ModTag = "rival|2" };
        root.Items[0] = second;

        Click(world, who, stale);

        Assert.Empty(used);

        Click(world, who, Token(0, second));

        Assert.Equal([second], used);
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
    /// A saved ground item retagged in place rewrites its zone, while an untouched zone is left byte for byte. Catches the zone-save shortcut
    /// keeping the old tag (review P2, the path #653 touched).
    /// </summary>
    [Fact]
    public void A_retagged_ground_item_rewrites_its_zone_and_an_untouched_zone_stays_byte_identical()
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
        var item = Assert.Single(SaveGroundItems.Read(save.Zones[WorldLayout.ZoneOf(100, 100)]));

        Assert.Equal("rival|2", item.Item.ModTag);
    }

    #endregion
}
