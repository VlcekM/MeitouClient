using System.Numerics;
using Meitou.Data;
using Meitou.Data.Physics;
using Meitou.Data.World;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Navigation;
using Meitou.Simulation;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Locks;
using Meitou.Simulation.Population;
using static Meitou.Tests.Bodies.BodyFixtures;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Simulation;

/// <summary>
/// Doors: leaf motion, closing to the lock, Meitou's close-behind, bashing and where the doors are (docs/game/locks.md "Doors", "Door motion";
/// docs/simulation.md "Doors").
/// </summary>
public class DoorTests(ITestOutputHelper output)
{
    [Fact]
    public void The_leaf_slides_and_steps_out_by_the_original_formula()
    {
        // A collision box in PhysX's Z-up frame: 20 wide (X), 4 thick (Y), 40 tall (Z), centred at (-10, 2, 20); in the part's Y-up frame its Y is -Z
        // and its Z is up.
        var box = DoorFrame.Of(Pose.Identity with { Translation = new Vector3(-10, 2, 20) }, new Vector3(10, 2, 20));

        // No step out: slide x dist x doa.
        var plain = new DoorLeaf(0, 30, 1, 0);

        Assert.Equal(new Vector3(15, 0, 0), plain.LocalOffset(0.5f, box));
        Assert.Equal(new Vector3(30, 0, 0), plain.LocalOffset(1, box));

        // Step out 10 along the thinnest other axis (box Y = part -Z, its centre at Z -2: away from the origin is -Z) until t = 10 / (0.25 x 28 + 10)
        // = 10/17, then slide 28 up (box Z).
        var stepping = new DoorLeaf(2, 28, 0.5f, 10);
        var half = stepping.LocalOffset(0.5f, box);

        Assert.Equal(-8.5f, half.Z, 4);
        Assert.Equal(0, half.Y);
        Assert.Equal(new Vector3(0, 28, -10), stepping.LocalOffset(1, box));
    }

    sealed class Gate : IDoorGate
    {
        public void SetDoorsBlocked(string buildingInstance, bool blocked) { }
        public void ResetDoors() { }
        public DoorSpot? DoorSpotOf(string buildingRecordId, int doorIndex) => new(Vector3.Zero, Vector3.UnitZ, 15);
    }

    [Fact]
    public void A_door_that_wants_its_lock_locks_when_closed_and_a_walker_opened_door_closes_after_5_s()
    {
        var walk = new OpenGroundWalkability((_, _) => 150);
        using var world = new SimWorld(new WorldSettings { Seed = 31, PublishSnapshots = false, MinPartitionSize = 1 }, walk,
                [new LockSystem(BaseGame, BodyOptions.Default)]);
        world.Fixtures.RegisterSite(world, ArrestTests.Town(), 0);
        world.Fixtures.Doors = new Gate();

        var door = world.Fixtures.Find("0.0:house#0")!;
        door.Motion = DoorMotion.Open;
        door.OpenAmount = 1;
        door.WantsLock = true;

        Assert.True(FixtureRegistry.CloseDoor(door));

        for (int i = 0; i < 11; i++) world.Fixtures.StepDoors(0.1f);

        Assert.Equal(DoorMotion.Closed, door.Motion);
        Assert.Equal(0, door.OpenAmount);
        Assert.True(door.Lock!.Locked);

        // Meitou: a walker opened it and nobody goes through, so it closes once 5 s have passed since the last pass (and not before).
        door.WantsLock = false;
        door.Lock.Locked = false;
        door.Motion = DoorMotion.Open;
        door.OpenAmount = 1;
        door.AutoOpened = true;
        door.LastPass = world.Tick;
        int five = (int)MathF.Ceiling(LockSystem.AutoCloseSeconds / world.TickSeconds);
        world.RunTicks(five - 10);

        Assert.Equal(DoorMotion.Open, door.Motion);

        world.RunTicks(10 + LockSystem.WalkerEveryTicks);

        Assert.Equal(DoorMotion.Closing, door.Motion);
        Assert.False(door.Lock!.Locked);
    }

    [Fact]
    public void A_bash_blow_does_clamp_2_blunt_plus_half_cut_20_to_60_an_animal_at_a_quarter_first()
    {
        Assert.Equal(20, LockSystem.BlowDamage(10, 2, animal: false));     // 9 -> 20
        Assert.Equal(35, LockSystem.BlowDamage(30, 10, animal: false));
        Assert.Equal(60, LockSystem.BlowDamage(100, 40, animal: false));   // 130 -> 60
        Assert.Equal(25, LockSystem.BlowDamage(0, 50, animal: true));      // 100 x 0.25, then the clamp
        Assert.Equal(20, LockSystem.BlowDamage(30, 10, animal: true));
        Assert.Equal(0.015f, LockSystem.ProgressPerDamage);
    }

    sealed class SpotGate : IDoorGate
    {
        public void SetDoorsBlocked(string buildingInstance, bool blocked) { }
        public void ResetDoors() { }
        public DoorSpot? DoorSpotOf(string buildingRecordId, int doorIndex) => new(new Vector3(0, 0, 60), Vector3.UnitZ, 15);
    }

    [Fact, Slow]
    public void A_bonedog_whose_target_hides_in_a_locked_hub_house_walks_to_its_door_bashes_it_open_and_goes_in()
    {
        Assert.SkipWhen(InstallData.Install is null, "Kenshi install not found");

        var db = InstallData.BaseGame!;
        var levels = InstallData.Levels!;
        var data = PopulationData.Create(db, levels.Towns(), buildings: TownBuildings.Build(db, levels, null, (_, _) => 0));
        int site = data.Sites.ToList().FindIndex(s => s.Town.Name == "The Hub");
        float ground = 150;
        var walk = new OpenGroundWalkability((_, _) => ground);
        var systems = StandardSystems.Build(data, walk, new StandardSystemOptions
        {
            Parts = StandardParts.Movement | StandardParts.Pursuit | StandardParts.Body | StandardParts.Combat | StandardParts.Crime,
                    SynchronousPaths = true,
            Combat = new CombatOptions { SelfApproach = false, TickMedical = false },
        });
        using var world = new SimWorld(new WorldSettings { Seed = 3, MinPartitionSize = 1, PublishSnapshots = false }, walk, systems.Systems);
        world.Fixtures.RegisterSite(world, data, site);
        world.Fixtures.Doors = new SpotGate();   // headless: a stand-in for navigation's door spots
        var b = data.Sites[site].Buildings.First(x => world.Fixtures.OfBuilding(x.Key).Any(f => f.Kind == FixtureKind.Door));
        ground = b.Position.Y;   // the house stands at its level height: the ground under the walkers too
        var doors = world.Fixtures.OfBuilding(b.Key).Where(f => f.Kind == FixtureKind.Door).ToList();
        foreach (var d in doors) { d.Lock!.Level = Math.Max(1, d.Lock.Level); world.Fixtures.SetLocked(d, true); }

        Assert.True(world.Fixtures.IsShut(b.Key));

        var door = doors[0];
        float hardness = door.Lock!.Hardness;

        Assert.True(hardness > 0);

        var population = new PopulationSystem(data, new PopulationSettings());
        string human = db.OfType(Data.Fcs.FcsRecordType.Character)
            .First(r => r.GetReferences("race").Any(x => x.Values.Value0 > 0 && db.Find(x.TargetStringId)?.Name == "Greenlander")
                    && r.GetReferences("stats").Count > 0).StringId;
        var me = population.SpawnSandbox(world, human, new Vector2(b.Position.X, b.Position.Z), player: true, faction: data.PlayerFaction);
        var spot = world.Fixtures.DoorSpotOf(door)!.Value;
        var outward = Vector2.Normalize(new Vector2(spot.Centre.X - b.Position.X, spot.Centre.Z - b.Position.Z));
        var dog = population.SpawnSandbox(world, "3979-gamedata.base", new Vector2(spot.Centre.X, spot.Centre.Z) + outward * 200, player: false);
        world.RunTicks(2);
        world.Defer(new AttackOrder([dog], me) { Tick = world.Tick + 1 });
        var table = world.Characters;

        // A bonedog's blows do the minimum (a quarter of its packet is under 20): 0.3 of the door's progress each, one every 1.5 s.
        bool bashed = false;
        Vector3 at = default;
        int ticks = (int)((MathF.Ceiling(door.DoorProgress / (20 * LockSystem.ProgressPerDamage)) + 4) * LockSystem.DefaultBashClipSeconds
            / world.TickSeconds) + 900;
        for (int t = 0; t < ticks && !door.DoorBroken; t++)
        {
            world.RunTick();
            bashed |= table.Cold(dog.Slot)!.Bashing == door.Id;
            at = table.Next[dog.Slot].Position;
        }
        output.WriteLine($"{b.Name}: hardness {hardness}, broken at tick {world.Tick}");

        Assert.True(bashed);
        Assert.True(door.DoorBroken);
        Assert.False(door.Lock.Locked);
        Assert.Equal(DoorMotion.Opening, door.Motion);
        Assert.False(world.Fixtures.IsShut(b.Key));
        Assert.True(Vector2.Distance(new Vector2(at.X, at.Z), new Vector2(spot.Centre.X, spot.Centre.Z)) < 2 * DoorPlan.DoorReach);

        // The pursuit carries on into the house.
        world.RunTicks(150);

        Assert.Null(table.Cold(dog.Slot)!.Bashing);

        var now = table.Next[dog.Slot].Position;

        Assert.True(Vector2.Distance(new Vector2(now.X, now.Z), new Vector2(b.Position.X, b.Position.Z)) < Vector2.Distance(new Vector2(at.X, at.Z),
                new Vector2(b.Position.X, b.Position.Z)));
    }

    /// <summary>
    /// Gate codes split by the line z = <see cref="SyntheticTown.Centre"/>.Z: 0 south of it, 1 north; one gate between them.
    /// </summary>
    sealed class LineRegions(string gate) : IGateRegions
    {
        public int CodeAt(Vector3 at) => at.Z < SyntheticTown.Centre.Z ? 0 : 1;
        public IReadOnlyList<GateLink> Gates { get; } = [new GateLink(gate, [0, 1])];
    }

    [Fact]
    public void A_bandit_outside_a_locked_gate_plans_to_bash_it_and_none_once_it_is_broken()
    {
        static Meitou.Data.Fcs.FcsRecord Rec(string id, Meitou.Data.Fcs.FcsRecordType type, string name) => SyntheticTown.Rec(id, type, name);
        var owners = Rec("60-g", Data.Fcs.FcsRecordType.Faction, "owners");
        var raiders = Rec("61-g", Data.Fcs.FcsRecordType.Faction, "raiders");
        var leaf = Rec("62-g", Data.Fcs.FcsRecordType.Building, "Gate door");
        var gateRecord = Rec("63-g", Data.Fcs.FcsRecordType.Building, "Gate");
        gateRecord.Bools["is wall gate"] = true;
        gateRecord.References["doors"] = [SyntheticTown.Ref("62-g")];
        var town = Rec("64-g", Data.Fcs.FcsRecordType.Town, "Walled");
        town.References["faction"] = [SyntheticTown.Ref("60-g")];
        var file = new Meitou.Data.Fcs.FcsFile();
        file.Records.AddRange([owners, raiders, leaf, gateRecord, town]);
        var db = new GameDatabase();
        db.Apply(file, "test.mod");
        var buildings = new Dictionary<string,
                List<TownBuilding>> { ["walled"] = [new("gate", "63-g", "63-g", SyntheticTown.Centre, 0, false, false, [])] };
        var data = PopulationData.Create(db, [new TownPlacement("walled", "64-g", SyntheticTown.Centre, null)], buildings: buildings);

        var locks = new LockSystem(BaseGame, BodyOptions.Default);
        using var world = new SimWorld(new WorldSettings { Seed = 5, PublishSnapshots = false, MinPartitionSize = 1 },
                new OpenGroundWalkability((_, _) => 300), [locks]);
        world.Fixtures.RegisterSite(world, data, 0);
        world.Fixtures.Doors = new Gate();
        string key = data.Sites[0].Buildings[0].Key;
        var door = Assert.Single(world.Fixtures.OfBuilding(key), f => f.Kind == FixtureKind.Door);

        Assert.True(door.IsGate);

        door.Lock!.Level = Math.Max(1, door.Lock.Level);
        world.Fixtures.SetLocked(door, true);
        locks.Inputs(world, [new GateRegionsCommand(new LineRegions(key)) { Tick = 0 }]);

        CharacterId Spawn(float dz, int faction)
        {
            var cold = new Combat.Combatant { Name = "raider" }.MakeCold();
            cold.Faction = faction;
            cold.NpcClass = Data.Gameplay.FundamentalType.Bandit;
            return world.Characters.Spawn(new CharacterHot { Position = SyntheticTown.Centre + new Vector3(0, 0, dz), Health = 100, MaxSpeed = 60,
                    WalkSpeed = 15, Task = (byte)CharacterTask.Attack }, cold, 0);
        }
        var raider = Spawn(-300, data.FactionIndex("61-g"));
        var target = Spawn(300, data.FactionIndex("60-g"));
        var spot = world.Fixtures.DoorSpotOf(door)!.Value;
        var plan = new GatePlan();

        Assert.True(plan.PlansBash(world, null, locks, raider.Slot, target.Slot, key, door, spot));

        // A ransacker (goal 216) bound for a building behind it plans the same bash.
        var from = world.Characters.Next[raider.Slot].Position;
        var to = world.Characters.Next[target.Slot].Position;

        Assert.Equal((DoorPlan.Entry.Bash, door.Id), locks.GateEntry(world, raider.Slot, from, to));

        // Broken, the gate is open (state 1): the way is not blocked, so nothing is bashed.
        door.DoorBroken = true;

        Assert.False(plan.PlansBash(world, null, locks, raider.Slot, target.Slot, key, door, spot));

        locks.Inputs(world, [new GateRegionsCommand(new LineRegions(key)) { Tick = 0 }]);   // the gate graph is rebuilt once a check

        Assert.Equal(DoorPlan.Entry.Walk, locks.GateEntry(world, raider.Slot, from, to).Entry);
    }

    /// <summary>
    /// Shut in (<c>FUN_1405a3710</c> / <c>FUN_1405a34e0</c>): a character inside town walls whose gate to the open world is locked has no retreat
    /// desire; outside the walls, or with the gate broken open, it has.
    /// </summary>
    [Fact]
    public void A_character_behind_a_locked_gate_of_town_walls_is_shut_in()
    {
        var owners = SyntheticTown.Rec("60-g", Data.Fcs.FcsRecordType.Faction, "owners");
        var leaf = SyntheticTown.Rec("62-g", Data.Fcs.FcsRecordType.Building, "Gate door");
        var gateRecord = SyntheticTown.Rec("63-g", Data.Fcs.FcsRecordType.Building, "Gate");
        gateRecord.Bools["is wall gate"] = true;
        gateRecord.References["doors"] = [SyntheticTown.Ref("62-g")];
        var town = SyntheticTown.Rec("64-g", Data.Fcs.FcsRecordType.Town, "Walled");
        town.References["faction"] = [SyntheticTown.Ref("60-g")];
        var file = new Meitou.Data.Fcs.FcsFile();
        file.Records.AddRange([owners, leaf, gateRecord, town]);
        var db = new GameDatabase();
        db.Apply(file, "test.mod");
        var buildings = new Dictionary<string,
                List<TownBuilding>> { ["walled"] = [new("gate", "63-g", "63-g", SyntheticTown.Centre, 0, false, false, [])] };
        var data = PopulationData.Create(db, [new TownPlacement("walled", "64-g", SyntheticTown.Centre, null)], buildings: buildings);
        var locks = new LockSystem(BaseGame, BodyOptions.Default);
        using var world = new SimWorld(new WorldSettings { Seed = 5, PublishSnapshots = false, MinPartitionSize = 1 },
                new OpenGroundWalkability((_, _) => 300), [locks]);
        world.Fixtures.RegisterSite(world, data, 0);
        world.Fixtures.Doors = new Gate();
        string key = data.Sites[0].Buildings[0].Key;
        var door = Assert.Single(world.Fixtures.OfBuilding(key), f => f.Kind == FixtureKind.Door);
        door.Lock!.Level = Math.Max(1, door.Lock.Level);
        world.Fixtures.SetLocked(door, true);
        locks.Inputs(world, [new GateRegionsCommand(new LineRegions(key)) { Tick = 0 }]);

        Assert.True(locks.WallsLocked(world, SyntheticTown.Centre + new Vector3(0, 0, 300)));
        Assert.False(locks.WallsLocked(world, SyntheticTown.Centre + new Vector3(0, 0, -300)));

        // DC 58 TOWN_WALLS_LOCKED_UP: a gate and no open door; an opening door still counts as locked, an open one does not.
        Assert.True(LockSystem.TownLockedUp(world, data.Sites[0]));

        door.Motion = DoorMotion.Opening;

        Assert.True(LockSystem.TownLockedUp(world, data.Sites[0]));

        door.Motion = DoorMotion.Open;

        Assert.False(LockSystem.TownLockedUp(world, data.Sites[0]));

        door.Motion = DoorMotion.Closed;
        door.DoorBroken = true;
        locks.Inputs(world, [new GateRegionsCommand(new LineRegions(key)) { Tick = 0 }]);   // the gate graph is rebuilt once a check

        Assert.False(locks.WallsLocked(world, SyntheticTown.Centre + new Vector3(0, 0, 300)));
    }

    /// <summary>
    /// docs/game/locks.md "When": an attacker's row 4 needs state 59 (<c>hasAnOpenDoor</c>, one evaluator for every goal), so a bandit bashes a
    /// closed unlocked door and a non-aggressive attacker does not.
    /// </summary>
    [Fact]
    public void An_attackers_plan_reads_state_59_exactly_so_a_bandit_bashes_a_closed_unlocked_door()
    {
        var locks = new LockSystem(BaseGame, BodyOptions.Default);
        using var world = new SimWorld(new WorldSettings { Seed = 31, PublishSnapshots = false, MinPartitionSize = 1 },
                new OpenGroundWalkability((_, _) => 150), [locks]);
        world.Fixtures.RegisterSite(world, ArrestTests.Town(), 0);
        world.Fixtures.Doors = new Gate();
        var door = world.Fixtures.Find("0.0:house#0")!;
        var spot = world.Fixtures.DoorSpotOf(door)!.Value;
        int owner = world.Fixtures.DoorOwner(door.Building);
        CharacterId Spawn(bool bandit, int faction)
        {
            var cold = new Combat.Combatant { Name = "raider" }.MakeCold();
            cold.Faction = faction;
            if (bandit) cold.NpcClass = Data.Gameplay.FundamentalType.Bandit;
            return world.Characters.Spawn(new CharacterHot { Position = spot.Centre + new Vector3(0, 0, 10), Health = 100, MaxSpeed = 60,
                    WalkSpeed = 15, Task = (byte)CharacterTask.Attack }, cold, 0);
        }
        var bandit = Spawn(true, owner + 1);
        var resident = Spawn(false, owner);
        var plan = new DoorPlan();
        world.Fixtures.SetLocked(door, false);
        door.Motion = DoorMotion.Closed;

        Assert.True(plan.PlansBash(world, null, bandit.Slot, resident.Slot, door.Building, door, spot, locks));
        Assert.False(plan.PlansBash(world, null, resident.Slot, bandit.Slot, door.Building, door, spot, locks));

        door.Motion = DoorMotion.Open;

        Assert.False(plan.PlansBash(world, null, bandit.Slot, resident.Slot, door.Building, door, spot, locks));
    }

    sealed class SplitRegions : IGateRegions
    {
        public int CodeAt(Vector3 at) => at.Z < SyntheticTown.Centre.Z ? 0 : 1;
        public IReadOnlyList<GateLink> Gates { get; } = [];
    }

    /// <summary>
    /// docs/game/locks.md (270 -> 272): with no gate between the attacker's region and its target's, the planner smashes a wall of its own region;
    /// only an attacker in reach of the wall plans it (state 46).
    /// </summary>
    [Fact]
    public void A_bandit_in_a_region_no_gate_leads_out_of_plans_to_smash_a_wall_only_once_in_its_reach()
    {
        var locks = new LockSystem(BaseGame, BodyOptions.Default);
        using var world = new SimWorld(new WorldSettings { Seed = 5, PublishSnapshots = false, MinPartitionSize = 1 },
                new OpenGroundWalkability((_, _) => 300), [locks]);
        locks.Inputs(world, [new GateRegionsCommand(new SplitRegions()) { Tick = 0 }]);
        CharacterId Spawn(float dz, int faction)
        {
            var cold = new Combat.Combatant { Name = "raider" }.MakeCold();
            cold.Faction = faction;
            cold.NpcClass = Data.Gameplay.FundamentalType.Bandit;
            return world.Characters.Spawn(new CharacterHot { Position = SyntheticTown.Centre + new Vector3(0, 0, dz), Health = 100, MaxSpeed = 60,
                    WalkSpeed = 15, Task = (byte)CharacterTask.Attack }, cold, 0);
        }
        var raider = Spawn(-300, 1);
        var target = Spawn(300, 2);
        var wall = new Meitou.Simulation.Construction.BuildSite { Id = "wall", Record = "w",
                Position = SyntheticTown.Centre + new Vector3(0, 0, -100), Materials = [] };
        var plan = new GatePlan();

        // 272's state 46 (at the wall) has only 67 Running as provider, which needs 90 again: only an attacker already in reach (60) of the wall
        // plans it.
        Assert.False(plan.PlansSmash(world, null, locks, raider.Slot, target.Slot, wall));

        world.Characters.Next[raider.Slot].Position = wall.Position + new Vector3(0, 0, -30);

        Assert.True(plan.PlansSmash(world, null, locks, raider.Slot, target.Slot, wall));
    }

    [Fact, Slow]
    public void Every_door_in_the_hub_has_a_known_position_and_its_destroyed_gates_have_none()
    {
        Assert.SkipWhen(InstallData.Install is null, "Kenshi install not found");

        var db = InstallData.BaseGame!;
        var levels = InstallData.Levels!;
        var data = PopulationData.Create(db, levels.Towns(), buildings: TownBuildings.Build(db, levels, null, (_, _) => 0));
        int site = data.Sites.ToList().FindIndex(s => s.Town.Name == "The Hub");
        var hub = data.Sites[site];
        var geometry = new NavDoorGeometry(InstallData.Install!, db);
        int doors = 0;
        var missing = new List<string>();
        foreach (var b in hub.Buildings)
        {
            if (db.Find(b.BuildingId) is not { } record) continue;
            for (int i = 0; i < record.GetReferences("doors").Count; i++, doors++)
                if (geometry.Spot(record.StringId, i) is null) missing.Add($"{b.Name} ({record.StringId}) door {i}");
        }

        Assert.True(doors > 10, $"{doors} doors");
        Assert.Empty(missing);

        var walk = new OpenGroundWalkability((_, _) => 150);
        using var world = new SimWorld(new WorldSettings { Seed = 3, PublishSnapshots = false }, walk, []);
        world.Fixtures.RegisterSite(world, data, site);

        // The level data marks both Defensive Gate IV destroyed; a ruin has no doors (docs/game/locks.md "Doors"), the intact buildings keep theirs.
        var gates = hub.Buildings.Where(b => b.IsGate).ToList();

        Assert.Equal(2, gates.Count(b => b.Destroyed));
        Assert.All(gates.Where(b => b.Destroyed), g => Assert.DoesNotContain(world.Fixtures.OfBuilding(g.Key), f => f.Kind == FixtureKind.Door));
        Assert.Contains(hub.Buildings.Where(b => !b.Destroyed), b => world.Fixtures.OfBuilding(b.Key).Any(f => f.Kind == FixtureKind.Door));
    }
}
