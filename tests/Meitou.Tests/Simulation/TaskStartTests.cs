using System.Numerics;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Ai;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Data.Gameplay.Construction;
using Meitou.Data.World;
using Meitou.Simulation;
using Meitou.Simulation.Aggro;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Carrying;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Construction;
using Meitou.Simulation.Crime;
using Meitou.Simulation.FirstAid;
using Meitou.Simulation.Items;
using Meitou.Simulation.Kidnap;
using Meitou.Simulation.Locks;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Player;
using Meitou.Simulation.Population;
using Meitou.Simulation.Production;
using Meitou.Tests.Combat;
using static Meitou.Tests.Bodies.BodyFixtures;
using static Meitou.Tests.Combat.CombatFixtures;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Simulation;

/// <summary>
/// The one task start path (docs/simulation.md "Task starts"): what a start does to the character's fight.
/// </summary>
public class TaskStartTests
{
    /// <summary>
    /// The fixture's Greenlander, carriable like every base race but the Leviathan.
    /// </summary>
    static readonly RaceData Person = new() { StringId = "17-c", Name = "Greenlander", StatMultipliers = Human.StatMultipliers,
            Anatomy = Human.Anatomy, Carriable = true };

    const string Well = "60-t", Shed = "61-t", Bed = "62-t", Cage = "63-t", Store = "64-t";

    /// <summary>
    /// The first aid kits, a well (an operable mine), a shed (4 Building Materials), a bed and a cage.
    /// </summary>
    static GameDatabase Database()
    {
        var db = FirstAidTests.KitDatabase();
        static FcsRecord Rec(string id, FcsRecordType type, string name) => new() { StringId = id, Flags = 0x10, Name = name, RecordType = type };
        var water = Rec("50-t", FcsRecordType.Item, "Water");
        water.Floats["weight kg"] = 1;
        var wellFn = Rec("80-t", FcsRecordType.BuildingFunctionality, "80-t");
        wellFn.Ints["function"] = MachineData.MineFunction;
        wellFn.Ints["stat used"] = (int)StatsEnumerated.Labouring;
        wellFn.References["produces"] = [new FcsReference("50-t", 10, 0, 0)];
        var well = Rec(Well, FcsRecordType.Building, "Test Well");
        well.References["functionality"] = [new FcsReference("80-t", 0, 0, 0)];
        var shed = Rec(Shed, FcsRecordType.Building, "Test Shed");
        shed.Ints["build materials"] = 4;
        var bedFn = Rec("90-t", FcsRecordType.BuildingFunctionality, "90-t");
        bedFn.Ints["function"] = TownBuildings.BedFunction;
        var bed = Rec(Bed, FcsRecordType.Building, "Test Bed");
        bed.References["functionality"] = [new FcsReference("90-t", 0, 0, 0)];
        var cageFn = Rec("91-t", FcsRecordType.BuildingFunctionality, "91-t");
        cageFn.Ints["function"] = TownBuildings.CageFunction;
        cageFn.Floats["use range"] = 25;
        var cage = Rec(Cage, FcsRecordType.Building, "Test Cage");
        cage.References["functionality"] = [new FcsReference("91-t", 0, 0, 0)];
        var storeFn = Rec("92-t", FcsRecordType.BuildingFunctionality, "92-t");
        storeFn.Ints["function"] = MachineData.GeneralStorageFunction;
        var store = Rec(Store, FcsRecordType.Building, "Test Store");
        store.Bools["has inventory"] = true;
        store.References["functionality"] = [new FcsReference("92-t", 0, 0, 0)];
        var file = new FcsFile();
        file.Records.AddRange([water, wellFn, well, shed, bedFn, bed, cageFn, cage, storeFn, store]);
        db.Apply(file, "buildings.mod");
        return db;
    }

    /// <summary>
    /// A player's fighter (attacking a far enemy, so no blow lands) with a medkit and a splint kit, sneaking, and with <paramref name="carrying"/> a
    /// downed mate on its shoulder; a hurt mate, a downed NPC, an awake NPC, a second enemy and a prisoner; the player's well, an unfinished shed, a
    /// bed, an empty cage and the prisoner's locked one, and an item on the ground; the movement, first aid, body, carry, bed, construction,
    /// production, combat, crime, arrest and lock systems in the game's order; <paramref name="decide"/> adds feeding and retaliation after combat
    /// and town duties after carrying, with an outpost and a store round the rig (the decision step's self-defence and food goals);
    /// <paramref name="senses"/> adds the aggro system's looks (attack on sight off) after retaliation, over <paramref name="relations"/> (the combat
    /// system reads them too) when given.
    /// </summary>
    internal sealed class Rig : IDisposable
    {
        public SimWorld World { get; }
        public CombatSystem Combat { get; }
        public CharacterId Fighter, Enemy, OtherEnemy, Mate, Carried, Downed, Awake, Prisoner;
        public string WellSite = "", ShedSite = "", BedFixture = "", EmptyCage = "", LockedCage = "", StoreSite = "";
        public int Item;
        readonly OpenGroundWalkability _walk = new(SyntheticTown.Ground);
        internal readonly ItemFactory Items;

        public Rig(bool carrying, bool decide = false, bool slaves = false, bool senses = false, FactionRelations? relations = null)
        {
            var db = Database();
            var data = PopulationData.Create(db, []);
            Items = new ItemFactory(db, BaseGame);
            Combat = new CombatSystem(KatanaTechniques(), Constants, BaseGame, BodyOptions.Default, DuelFixture.Lengths,
                    new CombatOptions { SelfApproach = false, TickMedical = false });
            var carry = new CarrySystem(BaseGame, BodyOptions.Default);
            var crime = new CrimeSystem(data.Relations);
            var systems = new List<ITickSystem>
            {
                new PlayerSystem(), new MovementSystem(new PathService(_walk, true)), new FirstAidSystem(BaseGame, BodyOptions.Default, Items,
                        new FirstAidSettings { AutoNpcs = false }),
                new BodySystem(BaseGame, BodyOptions.Default), carry, new BedSystem(data) { Carry = carry },
                new ConstructionSystem(db, BaseGame, BodyOptions.Default, Items, [], ""), new ProductionSystem(db, BaseGame, BodyOptions.Default,
                        Items, null), Combat,
                crime, new ArrestSystem(crime, BaseGame) { Carry = carry }, new LockSystem(BaseGame, BodyOptions.Default) { Crimes = crime },
            };
            // The decision step's goals (docs/simulation.md "Decision step"): food on the ground and self-defence, in the game's order after combat.
            if (decide)
            {
                systems.Insert(systems.IndexOf(carry) + 1, new TownDutySystem(data, new PopulationSystem(data), carry));
                systems.InsertRange(systems.IndexOf(Combat) + 1, [new FeedSystem(Items), new RetaliationSystem(Combat)]);
                // The senses the protect-allies finder reads (15, docs/game/ai-tasks.md "Combat goals"); attack on sight stays off so the other
                // scenarios are not changed.
                if (senses) systems.Insert(systems.IndexOf(Combat) + 3,
                        new AggroSystem(relations ?? data.Relations, new AggroSettings { AttackOnSight = false }));
            }
            // The slaves' Obedient Slave job (187) is the captive system's.
            if (slaves) systems.Add(new CaptiveSystem(data, Combat));
            World = new SimWorld(new WorldSettings { Seed = 7, PublishSnapshots = false }, _walk, systems);
            if (relations is not null) Combat.Relations = relations;
            World.Player.Faction = 1;
            string Site(string record, float x, float z, bool complete)
            {
                var p = new Vector3(x, 300, z);
                string id = World.Buildings.NewId(WorldLayout.ZoneOf(p.X, p.Z));
                World.Buildings.Add(new BuildSite { Id = id, Record = record, Position = p, Owner = 1, Materials = [], Complete = complete });
                return id;
            }
            WellSite = Site(Well, 700, 2000, complete: true);
            ShedSite = Site(Shed, 700, 2300, complete: false);
            string Fixture(string record, float x, float z)
            {
                var site = World.Buildings.Find(Site(record, x, z, complete: true))!;
                World.Fixtures.RegisterPlayerBuilding(World, data, site);
                return World.Fixtures.OfBuilding(site.Id).Single().Id;
            }
            BedFixture = Fixture(Bed, 900, 1600);
            EmptyCage = Fixture(Cage, 1000, 1600);
            LockedCage = Fixture(Cage, 1100, 1600);
            if (decide)
            {
                // The player's outpost round the rig with a store (Getting food's, 243).
                int town = World.Buildings.AddTown(new Vector3(1000, 300, 2000), 1).Number;
                StoreSite = Site(Store, 1100, 2000, complete: true);
                World.Buildings.Find(StoreSite)!.Town = town;
            }
            World.GroundItems.Drop(Items.Create("72-t")!, new Vector3(1000, 300, 1800), 0);
            Item = World.GroundItems.All[^1].Id;
            Fighter = Add(1000, 2000, player: true, "72-t", "74-t");
            Mate = Add(1040, 2000, player: true);
            Carried = Add(1000, 2000, player: true);
            Downed = Add(1060, 2040, player: false);
            Awake = Add(1080, 2060, player: false);
            Enemy = Add(1500, 2000, player: false);
            OtherEnemy = Add(1500, 2100, player: false);
            Prisoner = Add(1100, 1600, player: false);
            World.Fixtures.Imprison(World, Prisoner, World.Fixtures.Find(LockedCage)!);
            World.Fixtures.Find(LockedCage)!.Lock!.Locked = true;
            Cold(Mate).Medical!.Parts[RLeg].Flesh = Cold(Mate).Medical!.Parts[RLeg].MaxHp * 0.2f;
            Cold(Carried).Medical!.KnockOut(1000);
            Cold(Downed).Medical!.KnockOut(1000);
            if (carrying) (Cold(Fighter).Carrying, Cold(Carried).CarriedBy) = (Carried, Fighter);
            Cold(Fighter).Orders |= CharacterOrders.Sneak;
            Say(new AttackOrder([Fighter], Enemy) { Tick = 0, Picked = true });
        }

        internal CharacterId Add(float x, float z, bool player, params string[] kits)
        {
            var cold = new Combatant { Name = "C" + World.Characters.HighWater }.MakeCold();
            cold.Race = Person;
            cold.Stats![StatsEnumerated.Medic] = 50;
            cold.Inventory = new Inventory();
            foreach (var k in kits) cold.Inventory.Add(Items.Create(k)!);
            cold.IsPlayer = player;
            cold.Faction = player ? 1 : 2;
            return World.Characters.Spawn(new CharacterHot { Position = new Vector3(x, 300, z), MaxSpeed = 40, WalkSpeed = 15 }, cold, 0);
        }

        public CharacterCold Cold(CharacterId id) => World.Characters.Cold(id.Slot)!;
        public CharacterTask Task(CharacterId id) => (CharacterTask)World.Characters.Previous[id.Slot].Task;
        public void Say(SimCommand c) => World.Commands.Enqueue(c);
        public void Dispose() => World.Dispose();
    }

    /// <summary>
    /// The converted order starts given to a fighting character, by name, with the act-out each starts (M3b and M3c add theirs).
    /// </summary>
    public static string[] Orders => ["move", "follow", "bodyguard", "stop", "first aid", "splint", "rest", "medic", "pick up", "kidnap", "loot",
        "open inventory", "stealth ko", "put down", "operate", "prospect", "build", "dismantle", "use bed", "put in bed", "pick lock", "set free",
        "get in cage", "force lock", "pick up item", "put in cage", "attack"];

    internal static (SimCommand Order, CharacterTask Task) Order(Rig rig, string name, long tick)
    {
        var f = rig.Fighter;
        SimCommand order = name switch
        {
            "move" => new MoveOrder([f], new Vector3(900, 0, 1900)) { Tick = tick },
            "follow" => new FollowOrder([f], rig.Mate) { Tick = tick },
            "bodyguard" => new FollowOrder([f], rig.Mate) { Tick = tick, Guard = true },
            "stop" => new StopCommand([f]) { Tick = tick },
            "first aid" => new TreatOrder([f], rig.Mate) { Tick = tick },
            "splint" => new SplintOrder([f], rig.Mate) { Tick = tick },
            "rest" => new RestOrder([f]) { Tick = tick },
            "medic" => new MedicJobCommand([f], Job: false) { Tick = tick },
            "pick up" => new PickUpOrder([f], rig.Downed) { Tick = tick },
            "kidnap" => new PickUpOrder([f], rig.Downed) { Tick = tick, Kidnap = true },
            "loot" => new LootOrder([f], rig.Downed) { Tick = tick },
            "open inventory" => new OpenLootOrder([f], rig.Downed) { Tick = tick },
            "stealth ko" => new StealthKoOrder([f], rig.Awake) { Tick = tick },
            "put down" => new PutDownOrder([f]) { Tick = tick },
            "operate" => new OperateOrder([f], rig.WellSite) { Tick = tick },
            "prospect" => new ProspectOrder(f) { Tick = tick },
            "build" => new BuildOrder([f], rig.ShedSite) { Tick = tick },
            "dismantle" => new DismantleOrder([f], rig.WellSite) { Tick = tick },
            "use bed" => new UseBedOrder([f], rig.BedFixture) { Tick = tick },
            "put in bed" => new PutInBedOrder([f], rig.BedFixture) { Tick = tick },
            "pick lock" => new PickLockOrder([f], CharacterId.None) { Tick = tick, Cage = rig.EmptyCage },
            "set free" => new SetFreeOrder([f], rig.Prisoner) { Tick = tick },
            "get in cage" => new GetInCageOrder([f], rig.EmptyCage) { Tick = tick },
            "force lock" => new ForceLockOrder([f], rig.LockedCage, LockForce.Strength) { Tick = tick },
            "pick up item" => new PickUpItemOrder([f], rig.Item) { Tick = tick },
            "put in cage" => new PutInCageOrder([f]) { Tick = tick, Cage = rig.EmptyCage },
            "attack" => new AttackOrder([f], rig.OtherEnemy) { Tick = tick, Picked = true },
            _ => throw new ArgumentException(name),
        };
        var task = name switch
        {
            "move" => CharacterTask.GoTo,
            "follow" or "bodyguard" => CharacterTask.Follow,
            "stop" => CharacterTask.Idle,
            "first aid" or "splint" or "medic" => CharacterTask.Treat,
            "rest" => CharacterTask.Rest,
            "pick up" or "kidnap" => CharacterTask.PickUp,
            "loot" or "open inventory" => CharacterTask.Loot,
            "stealth ko" => CharacterTask.StealthKo,
            "put down" => CharacterTask.PutDown,
            "operate" => CharacterTask.Operate,
            "prospect" => CharacterTask.Prospect,
            "build" => CharacterTask.Build,
            "dismantle" => CharacterTask.Dismantle,
            "use bed" => CharacterTask.Sleep,
            "put in bed" or "pick lock" or "set free" or "get in cage" or "force lock" or "pick up item" or "put in cage" => CharacterTask.GoTo,
            _ => CharacterTask.Attack,
        };
        return (order, task);
    }

    /// <summary>
    /// Another faction's ground item without Alt: the hint and a walk to it, nothing taken (<c>FUN_1407fc440</c>); with Alt it is picked up as any
    /// item.
    /// </summary>
    [Theory, InlineData(false), InlineData(true)]
    public void A_stolen_ground_item_needs_alt_to_be_picked_up(bool alt)
    {
        using var rig = new Rig(carrying: false);
        int stolen = rig.World.GroundItems.Place(rig.Items.Create("72-t")!, new Vector3(1000, 300, 1800), 0, null, owner: 2);
        rig.World.RunTicks(3);
        rig.World.TakeMessages();
        rig.Say(new PickUpItemOrder([rig.Fighter], stolen) { Tick = rig.World.Tick, Alt = alt });
        rig.World.RunTick();
        Assert.Equal(!alt, rig.World.TakeMessages().Contains("Hold down the ALT key to steal items"));
        Assert.Equal(CharacterTask.GoTo, rig.Task(rig.Fighter));
        Assert.Equal(alt, rig.Cold(rig.Fighter).PickingUpItem == stolen);
    }

    /// <summary>
    /// A new task ends the fight task (docs/game/combat.md "Fight lifecycle and orders"): every order given mid-fight starts its act-out and leaves
    /// the character with no fight flag and no fight target, unless the order is a fight, which keeps it fighting the new target. Catches an order
    /// refused mid-fight, or one that overwrites the task and leaves the fight running under it (docs/simulation.md "Task starts", a fight under
    /// another task).
    /// </summary>
    [Fact]
    public void An_order_given_mid_fight_ends_the_fight_unless_it_is_one() => Assert.All(Orders, EndsTheFightUnlessItIsOne);

    static void EndsTheFightUnlessItIsOne(string name)
    {
        using var rig = new Rig(carrying: name is "put down" or "put in bed" or "put in cage");
        rig.World.RunTicks(3);
        var f = rig.Fighter;
        Assert.True(rig.Cold(f).InCombat && rig.Combat.StateOf(f).TargetSlot == rig.Enemy.Slot, "fighting before the order");
        var (order, task) = Order(rig, name, rig.World.Tick);
        rig.Say(order);
        rig.World.RunTick();
        Assert.Equal(task, rig.Task(f));
        if (task == CharacterTask.Attack)
        {
            Assert.True(rig.Cold(f).InCombat);
            Assert.Equal(rig.OtherEnemy.Slot, rig.Combat.StateOf(f).TargetSlot);
            return;
        }
        Assert.False(rig.Cold(f).InCombat, "the fight flag is off");
        Assert.True(rig.Combat.StateOf(f).TargetSlot < 0, "no fight target");
    }

    /// <summary>
    /// Set Free through the Cages path is Task_ReleasePrisoner's start (<c>FUN_1403570a0</c>): the prisoner is marked freed and a releaser of the
    /// player's faction is thanked (event 13, deferred). Catches the walk-and-release path skipping the mark or the thanks that the rounds path gave.
    /// </summary>
    [Fact]
    public void Set_free_marks_the_prisoner_freed_and_thanks_a_player_releaser()
    {
        using var rig = new Rig(carrying: false);
        rig.Say(new SetFreeOrder([rig.Fighter], rig.Prisoner) { Tick = 0 });
        for (int i = 0; i < 3000 && !rig.Cold(rig.Prisoner).Freed; i++) rig.World.RunTick();
        Assert.True(rig.Cold(rig.Prisoner).Freed, "marked freed");
        Assert.False(rig.Cold(rig.Prisoner).IsImprisoned);
        Assert.True(rig.World.DeferredCount > 0, "the thanks (event 13) is deferred");
    }

    const int H = Repertoire.Human, I = Repertoire.Imprisoned, Ch = Repertoire.Chained, A = Repertoire.Animal, Cr = Repertoire.Crippled;

    /// <summary>
    /// The row of each start (docs/simulation.md "Task eligibility") and the repertoires the original registers it for (docs/game/ai-tasks.md
    /// "TaskData registry").
    /// </summary>
    public static Dictionary<TaskType, int> StartRows => new()
    {
        { TaskType.MoveCusOrdered, H | A | Cr }, { TaskType.StayCloseToTarget, H | A | Cr }, { TaskType.Bodyguard, H | A | Cr },
                { TaskType.Unnamed289, A },
        { TaskType.FocusedMeleeAttack, H | A | Cr }, { TaskType.AttackAttackersOf, H | A | Cr }, { TaskType.AttackEnemies, H | A | Cr },
        { TaskType.FirstAidOrder, H | I | Cr }, { TaskType.SplintOrder, H | I | Cr }, { TaskType.SplintJob, H | I },
                { TaskType.JobMedic, H | I | Cr }, { TaskType.JobRepairRobot, H | I | Cr },
        { TaskType.LootTarget, H | I | Cr }, { TaskType.LootTheDead, H | I | Cr }, { TaskType.NotBeUnarmed, H | I | Cr },
        { TaskType.LiftPersonPlayerOrder, H }, { TaskType.KidnapOrder, H }, { TaskType.FindAndRescueIfTheresBeds, H }, { TaskType.LootAnimalsJob, H },
        { TaskType.DeadGuysGoInThePot, H }, { TaskType.PickupIntruders, H }, { TaskType.PickupIntrudersTown, H }, { TaskType.PutDownObject, H },
                { TaskType.StealthKnockout, H },
        { TaskType.OperateMachinery, H }, { TaskType.UseTrainingDummy, H }, { TaskType.OperateStorage, H }, { TaskType.UseTurret, H },
                { TaskType.Prospecting, H | Cr },
        { TaskType.Build, H }, { TaskType.Repair, H }, { TaskType.Dismantle, H }, { TaskType.UseBed, H | Cr }, { TaskType.PutSomeoneInBed, H },
                { TaskType.FindBedAndPutIn, H },
        { TaskType.OperateAutomaticMachinery, H }, { TaskType.ManATurretPlayerJob, H }, { TaskType.JobBuilder, H },
        { TaskType.DitchAllResources, H }, { TaskType.GoHomeAndGoToBed, H | Cr }, { TaskType.SitAround, H },
        { TaskType.PickLock, H | I | Ch | Cr }, { TaskType.PickLockOnShackles, H | I | Ch | Cr }, { TaskType.CutShackles, H | I | Ch | Cr },
                { TaskType.BruteForceShackles, H | I | Ch | Cr },
        { TaskType.ReleasePrisoner, H }, { TaskType.UseCage, H | I | Ch | Cr }, { TaskType.OpenDoor, H | Cr }, { TaskType.CloseDoor, H | Cr },
                { TaskType.LockDoor, H | Cr },
        { TaskType.UnlockDoor, H | I | Cr }, { TaskType.UnlockDoorPlayerOrder, H }, { TaskType.CutLock, H | I | Ch | Cr },
                { TaskType.BruteForceLock, H | I | Ch | Cr },
        { TaskType.BashDoorPlayerOrder, H | A }, { TaskType.Pickup, H | I | A | Cr }, { TaskType.PutInCage, H },
        // The engine's starts: arrest, kidnap, captive jobs, town duties, smell, food, dialogue acts, flight, population, the standing tasks.
        { TaskType.ArrestTarget, H }, { TaskType.FindCageAndPutIn, H }, { TaskType.FindAndKidnap, H }, { TaskType.CarryWoundedSlaves, H },
                { TaskType.CaptureNewSlaves, H },
        { TaskType.KillCageOccupant, H }, { TaskType.EatTargetAlive, H | A | Cr }, { TaskType.ProcessAndStripNewSlave, H }, { TaskType.GetSlave, H },
                { TaskType.GiveAllMySlavesTo, H },
        { TaskType.SlaveGetInMyMastersCage, H | I | Ch | Cr }, { TaskType.WorkTheSlaves, H }, { TaskType.EatCrops, H | A | Cr },
                { TaskType.WorshipTarget, H | Cr },
        { TaskType.PlayBecauseIHaveALimbInMouth, A }, { TaskType.ChaseAllyDogsWithMouthLimbs, A }, { TaskType.AnimalFetchALimb, A },
        { TaskType.TakeIntruderOutsideTown, H | Cr }, { TaskType.AquireFoodAtHomebase, H | A | Cr }, { TaskType.LootStolenGoods, H | I | Cr },
                { TaskType.LootFoodAndStuff, H },
        { TaskType.PutLootInStorage, H }, { TaskType.HuntingBloodsmell, H | A | Cr }, { TaskType.EatFoodOnGround, H | Ch | A | Cr },
        { TaskType.RunAwayForced, H | A | Cr }, { TaskType.GoHomebuilding, H | A | Cr }, { TaskType.MoveOnFreeWillFast, H | A | Cr },
                { TaskType.FollowWhileTalking, H | Cr },
        { TaskType.TownStalker, H }, { TaskType.MoveOnFreeWill, H | A | Cr }, { TaskType.RunAway, H | A | Cr },
                { TaskType.TravelToTargetTown, H | A | Cr },
        { TaskType.TravelToTargetPackage, H | A | Cr }, { TaskType.FollowSquadleader, H | A | Cr }, { TaskType.Wanderer, H | A },
                { TaskType.Idle, H | I | Ch | A | Cr },
    };

    /// <summary>
    /// Each start's row admits exactly the repertoires the original registers it for (<c>FUN_14060e840</c>, <c>Category &amp; repertoire</c>):
    /// catches a start moved to a row of another registration or a row's flags changed in the table. The platoon repertoire (2) is not modelled.
    /// </summary>
    [Fact]
    public void Each_start_row_admits_the_repertoires_the_original_registers_it_for() => Assert.All(StartRows,
            row => AdmitsItsRepertoires(row.Key, row.Value));

    static void AdmitsItsRepertoires(TaskType row, int admitted)
    {
        var data = TaskDataTable.Of(row);
        Assert.NotNull(data);
        foreach (int repertoire in new[] { H, I, Ch, A, Cr })
            Assert.True(((data.Category & repertoire) != 0) == ((admitted & repertoire) != 0), $"{row} and repertoire 0x{repertoire:X}");
    }

    /// <summary>
    /// <see cref="Repertoire.Of"/> outside a cage: human 1, an animal 0x20, a crawler or one staying low 0x40 (the posture setter
    /// <c>FUN_1405c7e20</c>), before the animal test; a type with no row (Rest, 54) is open to all. Caged and chained characters are covered by the
    /// cage tests' starts.
    /// </summary>
    [Fact]
    public void The_repertoire_is_crippled_for_a_crawler_or_one_staying_low_else_human_or_animal()
    {
        using var world = new SimWorld(new WorldSettings { PublishSnapshots = false }, new OpenGroundWalkability(SyntheticTown.Ground));
        CharacterCold Make(bool animal = false, bool low = false, bool stump = false)
        {
            var cold = new Combatant().MakeCold();
            if (animal) cold.Fighter = new Fighter(null) { IsAnimal = true };
            cold.Medical!.StayingLow = low;
            if (stump)
            {
                cold.Medical.Parts[RLeg].Limb = LimbState.Stump;
                cold.Medical.Tick(1e-4f, Context());
            }
            return cold;
        }
        Assert.Equal(H, Repertoire.Of(world, Make()));
        Assert.Equal(A, Repertoire.Of(world, Make(animal: true)));
        Assert.Equal(Cr, Repertoire.Of(world, Make(stump: true)));
        Assert.Equal(Cr, Repertoire.Of(world, Make(low: true)));
        Assert.Equal(Cr, Repertoire.Of(world, Make(animal: true, low: true)));
        Assert.False(TaskStart.Eligible(world, Make(stump: true), TaskType.Build));
        Assert.False(TaskStart.Eligible(world, Make(animal: true), TaskType.LiftPersonPlayerOrder));
        Assert.True(TaskStart.Eligible(world, Make(animal: true), TaskType.Rest));
    }

    /// <summary>
    /// A system that sets slot 0's task in the slow world: directly, or through <see cref="TaskStart.Step"/>.
    /// </summary>
    sealed class Writer : ITickSystem
    {
        public CharacterTask? Direct, Stepped;
        public void SlowWorld(SimWorld world)
        {
            if (Direct is { } d) world.Characters.Next[0].Task = (byte)d;
            if (Stepped is { } s) TaskStart.Step(world.Characters, 0, s);
            Direct = Stepped = null;
        }
    }

#if DEBUG
    const bool Debug = true;
#else
    const bool Debug = false;
#endif

    /// <summary>
    /// Debug builds: a task written outside the start path fails the tick's check (<see cref="TaskStart.CheckStarts"/>), one set through it passes.
    /// Catches the check weakened (a task left out of it, a stamp that passes any write), which no other test would notice.
    /// </summary>
    [Fact]
    public void A_task_written_outside_the_start_path_fails_the_debug_check()
    {
        Assert.SkipUnless(Debug, "the check runs in debug builds only");
        var writer = new Writer();
        using var world = new SimWorld(new WorldSettings { PublishSnapshots = false }, new OpenGroundWalkability(SyntheticTown.Ground), [writer]);
        world.Characters.Spawn(new CharacterHot { Position = new Vector3(1000, 300, 2000) }, new Combatant().MakeCold(), 0);
        world.RunTick();
        writer.Stepped = CharacterTask.Wander;
        world.RunTick();
        Assert.Equal((byte)CharacterTask.Wander, world.Characters.Previous[0].Task);
        writer.Direct = CharacterTask.Idle;
        Assert.Throws<InvalidOperationException>(() => world.RunTick());
    }
}
