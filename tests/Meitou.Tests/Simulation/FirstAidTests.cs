using System.Numerics;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Ai;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Simulation;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Factions;
using Meitou.Simulation.FirstAid;
using Meitou.Simulation.Items;
using Meitou.Simulation.Locks;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Player;
using SimWorld = Meitou.Simulation.World;
using static Meitou.Tests.Bodies.BodyFixtures;

namespace Meitou.Tests.Simulation;

/// <summary>
/// First aid and resting in play: the treatment rate and kit drain against the doc, a medic walking up and treating, and determinism.
/// </summary>
public class FirstAidTests
{
    const float KitQuality = 40, KitCharges = 400, SplintQuality = 20;

    // ------------------------------------------------------------------ the formulas (docs/game/character-stats.md "First aid, medkits and robot
    // repair")

    [Fact]
    public void The_treatment_rate_the_kit_drain_and_splinting_follow_the_documented_formulas()
    {
        var c = BaseGame;   // medic speed mult 3, robot medic speed mult 0.33, medkit drain 1 / 99 = 2 / 0.1 after the loader's scaling

        // lerp(1, 15, min(skill, quality) x 0.01) x medic speed mult: skill 50 with a quality 40 kit is limited by the kit (t = 0.4).
        Assert.Equal((1 + 14 * 0.4f) * 3, MedicalState.TreatmentRate(c, 50, 40, false), 4);
        Assert.Equal((1 + 14 * 0.4f) * 3, MedicalState.TreatmentRate(c, 90, 40, false), 4);     // a better medic gains nothing from a poor kit
        Assert.Equal((1 + 14 * 0.4f) * 3, MedicalState.TreatmentRate(c, 40, 100, false), 4);    // nor a better kit from a poor medic
        Assert.Equal(3f, MedicalState.TreatmentRate(c, 0, 100, false), 4);                      // t = 0: 1 x 3
        Assert.Equal(45f, MedicalState.TreatmentRate(c, 100, 100, false), 4);                   // t = 1: 15 x 3
        Assert.Equal(45f * 0.33f, MedicalState.TreatmentRate(c, 100, 100, true), 4);            // a robot kit is slower

        // lerp(medkit drain 99, medkit drain 1, (100 - skill) / 100) x dt: an expert uses a kit 20 times slower than a beginner.
        Assert.Equal(1.05f * 2, MedicalState.KitDrain(c, 50, 2, false), 4);
        Assert.Equal(2f, MedicalState.KitDrain(c, 0, 1, false), 4);
        Assert.Equal(0.1f, MedicalState.KitDrain(c, 100, 1, false), 4);
        Assert.Equal(20f, MedicalState.KitDrain(c, 0, 1, false) / MedicalState.KitDrain(c, 100, 1, false), 3);
        Assert.Equal(2 * 0.33f, MedicalState.KitDrain(c, 0, 1, true), 4);

        // docs/game/character-stats.md "Splints": a broken right leg, h = -20 with 60 bandaged.
        var m = Healthy();
        var leg = m.Parts[RLeg];
        leg.Flesh = -20;
        leg.Bandage = 60;

        Assert.True(m.NeedsSplint(Human, 50));                // rig 0 < b, (h - s + rig) x 1.05 < 50, rig + 1 < trunc(50)
        Assert.False(m.NeedsSplint(Human, 1));                // rig + 1 < trunc(X) fails

        // Splint Kit quality 20, medic 50: t = 20 x 0.01f, just under 0.2, so the cap 1 - trunc(t x -100) is 20; rate lerp(1, 15, t) x medic speed
        // mult 3 = 11.4 per unit of dt.
        var ctx = Context();

        Assert.Equal(11.4f, m.Splint(1, 50, 20, ctx), 3);
        Assert.Equal(11.4f, leg.Rig, 3);
        Assert.Equal(20 - 11.4f, m.Splint(1, 50, 20, ctx), 3); // clamped to the cap
        Assert.Equal(20f, leg.Rig, 3);
        Assert.Equal(0f, m.Splint(1, 50, 20, ctx));           // nothing else to splint
        Assert.False(leg.IsDown);                             // h - s + rig = 0: the leg holds again

        // rig never passes s + b: a part with only 5 bandaged takes 5.
        var arm = m.Parts[LArm];
        arm.Flesh = 0;
        arm.Bandage = 5;

        Assert.Equal(5f, m.Splint(1, 50, 20, ctx), 3);

        // A vital part is never splinted.
        var vital = Healthy();
        vital.Parts[HeadIx].Flesh = 10;
        vital.Parts[HeadIx].Bandage = 60;

        Assert.False(vital.NeedsSplint(Human, 50));
        Assert.Equal(0f, vital.Splint(1, 50, 20, ctx));
    }

    // ------------------------------------------------------------------ in the world

    internal static GameDatabase KitDatabase()
    {
        FcsRecord Kit(string id, string name, int function, float quality, float charges)
        {
            var r = new FcsRecord { StringId = id, Flags = 0x10, Name = name, RecordType = FcsRecordType.Item };
            r.Ints["item function"] = function;
            r.Floats["quality"] = quality;
            r.Floats["charges"] = charges;
            r.Floats["weight kg"] = 2;
            return r;
        }
        var file = new FcsFile();
        file.Records.AddRange([Kit("72-t", "Test kit", 1, KitQuality, KitCharges), Kit("73-t", "Test robot kit", 12, 50, 500),
                Kit("74-t", "Test splint kit", 2, SplintQuality, 100)]);
        var db = new GameDatabase();
        db.Apply(file, "test.mod");
        return db;
    }

    /// <summary>
    /// A small world of hand-made characters on open ground, with the movement, first aid and body systems (and combat when asked).
    /// </summary>
    sealed class Rig : IDisposable
    {
        public SimWorld World { get; }
        public ItemFactory Items { get; }
        readonly OpenGroundWalkability _walk = new(SyntheticTown.Ground);
        public List<CharacterId> Ids { get; } = [];

        public Rig(int threads = 1, int minPartition = 16, FirstAidSettings? settings = null, ulong seed = 5)
        {
            Items = new ItemFactory(KitDatabase(), BaseGame);
            var movement = new MovementSystem(new PathService(_walk, true));
            var systems = new List<ITickSystem> { new PlayerSystem(), movement, new FirstAidSystem(BaseGame, BodyOptions.Default, Items, settings),
                    new BodySystem(BaseGame, BodyOptions.Default) };
            World = new SimWorld(new WorldSettings { Seed = seed, Threads = threads, PublishSnapshots = false, MinPartitionSize = minPartition },
                    _walk, systems);
        }

        /// <summary>
        /// Adds a character at a spot; <paramref name="kits"/> are record ids put in its inventory. All join squad <paramref name="squad"/> (created
        /// when new).
        /// </summary>
        public CharacterId Add(float x, float z, bool player = true, int squad = 0, float medic = 50, params string[] kits)
        {
            var inventory = new Inventory();
            foreach (var k in kits) inventory.Add(Items.Create(k)!);
            var stats = new CharacterStats();
            stats[StatsEnumerated.Medic] = medic;
            stats[StatsEnumerated.Strength] = 50;
            var cold = new CharacterCold
            {
                Name = "C" + Ids.Count, Race = Human, Stats = stats, Medical = MedicalState.Create(Human, 50), Inventory = inventory,
                        IsPlayer = player,
                SquadId = squad,
            };
            var hot = new CharacterHot { Position = new Vector3(x, 300, z), MaxSpeed = 40, WalkSpeed = 15, Task = (byte)CharacterTask.Idle };
            var id = World.Characters.Spawn(hot, cold, 0);
            Ids.Add(id);
            var sq = World.Squads.Find(squad);
            if (sq is null) World.Squads.Add(sq = new Squad { Id = squad, TemplateId = "t" });
            sq.Members.Add(id);
            if (player) World.Player.Selection.Add(id);
            return id;
        }

        public CharacterCold Cold(CharacterId id) => World.Characters.Cold(id.Slot)!;
        public MedicalState Body(CharacterId id) => Cold(id).Medical!;
        public CharacterTask Task(CharacterId id) => (CharacterTask)World.Characters.Previous[id.Slot].Task;
        public Vector3 Where(CharacterId id) => World.Characters.Previous[id.Slot].Position;
        public void Say(SimCommand c) => World.Commands.Enqueue(c);
        public ItemInstance Kit(CharacterId id) => Cold(id).Inventory!.All().First(i => i.ItemFunction is 1 or 12);

        /// <summary>
        /// Wounds a part so first aid has something to do: its flesh is set to <paramref name="fraction"/> of the maximum.
        /// </summary>
        public void Hurt(CharacterId id, int part, float fraction) => Body(id).Parts[part].Flesh = Body(id).Parts[part].MaxHp * fraction;

        public void Dispose() => World.Dispose();
    }

    static float Bandage(MedicalState m) => m.Parts.Sum(p => p.Bandage);

    [Fact]
    public void A_medic_walks_to_the_patient_and_bandages_it_with_the_kit_at_the_documented_rate()
    {
        using var rig = new Rig(settings: new FirstAidSettings { AutoNpcs = false });
        var medic = rig.Add(1000, 2000, medic: 50, kits: "72-t");
        var patient = rig.Add(1130, 2000, squad: 1);
        rig.Hurt(patient, RLeg, 0.3f);                 // 70 to bandage
        rig.Hurt(patient, LArm, 0.6f);                 // 40
        rig.World.RunTicks(2);
        float startX = rig.Where(medic).X;
        rig.Say(new TreatOrder([medic], patient) { Tick = rig.World.Tick });

        // The medic walks (the task is Treat), the patient is untouched until it arrives.
        rig.World.RunTicks(10);

        Assert.Equal(CharacterTask.Treat, rig.Task(medic));
        Assert.Equal(0, Bandage(rig.Body(patient)));

        long arrived = -1;
        float before = 0;
        for (int t = 0; t < 1500 && arrived < 0; t++)
        {
            before = Bandage(rig.Body(patient));
            rig.World.RunTick();
            if (Bandage(rig.Body(patient)) > 0) arrived = rig.World.Tick;
        }

        Assert.True(arrived > 0, "the medic reached the patient");
        Assert.True(rig.Where(medic).X > startX + 50);

        // One tick of treatment: skill 50 (injury multiplier 1), kit quality 40: lerp(1, 15, 0.4) x 3 per second, a tick is 1/30 s.
        float expected = MedicalState.TreatmentRate(BaseGame, 50, KitQuality, false) / 30;

        Assert.Equal(expected, Bandage(rig.Body(patient)) - before, 1e-3);   // only the first wounded part in part order takes the rate

        var kit = rig.Kit(medic);
        float charges = kit.Charges;
        float medicBefore = rig.Cold(medic).Stats![StatsEnumerated.Medic];

        // Runs on until nothing is left to bandage.
        for (int t = 0; t < 3000 && rig.Task(medic) == CharacterTask.Treat; t++) rig.World.RunTick();

        Assert.Equal(CharacterTask.Idle, rig.Task(medic));
        Assert.False(rig.Body(patient).NeedsAid(Human, false));

        foreach (var p in rig.Body(patient).Parts) Assert.True(p.Flesh + p.Bandage >= p.MaxHp - 0.01f,
                $"{p.Template.Name} is bandaged up to its maximum");

        Assert.True(kit.Charges < charges && kit.Charges > 0, "the kit was used, not used up");
        Assert.True(rig.Cold(medic).Stats![StatsEnumerated.Medic] > medicBefore, "treating trains the medic skill");
    }

    [Fact]
    public void A_splint_order_rigs_the_broken_leg_with_the_kit_and_a_medic_without_one_refuses()
    {
        using var rig = new Rig(settings: new FirstAidSettings { AutoNpcs = false });
        var medic = rig.Add(1000, 2000, medic: 50, kits: "74-t");
        var bare = rig.Add(1000, 2200, medic: 50);           // beyond the kit reach of its squad mate's kit
        var patient = rig.Add(1060, 2000, squad: 1);
        var leg = rig.Body(patient).Parts[RLeg];
        leg.Flesh = -10;
        leg.Bandage = 60;
        rig.World.RunTicks(2);

        Assert.True(leg.IsDown);

        rig.Say(new SplintOrder([bare], patient) { Tick = rig.World.Tick });
        rig.World.RunTicks(2);

        Assert.Equal(CharacterTask.Idle, rig.Task(bare));   // "I don't have a splint kit."

        var kit = rig.Cold(medic).Inventory!.All().First(i => i.ItemFunction == 2);
        float charges = kit.Charges;
        rig.Say(new SplintOrder([medic], patient) { Tick = rig.World.Tick });
        rig.World.RunTicks(2);

        Assert.Equal(CharacterTask.Treat, rig.Task(medic));

        for (int t = 0; t < 3000 && rig.Task(medic) == CharacterTask.Treat; t++) rig.World.RunTick();

        Assert.Equal(CharacterTask.Idle, rig.Task(medic));
        Assert.Equal(SplintQuality, leg.Rig, 2);              // up to the cap, 20 for quality 20 (see the formula test)
        Assert.False(leg.IsDown, $"h {leg.Flesh} s {leg.Stun} rig {leg.Rig}");
        Assert.True(kit.Charges < charges, "the kit drained");
    }

    // ------------------------------------------------------------------ determinism

    /// <summary>
    /// The scripted scenario of treating, resting and the medic job, pinned in <see cref="GoldenHashTests"/>.
    /// </summary>
    internal static List<ulong> Scripted(int threads)
    {
        using var rig = new Rig(threads, minPartition: 1, seed: 12);
        var a = rig.Add(1000, 2000, player: true, medic: 60, kits: "72-t");
        var b = rig.Add(1060, 2000, player: true);
        var c = rig.Add(1100, 2060, player: true);
        var d = rig.Add(1500, 2000, player: false, squad: 1, medic: 30, kits: "72-t");
        var e = rig.Add(1530, 2000, player: false, squad: 1);
        var f = rig.Add(1560, 2030, player: false, squad: 1);
        rig.Hurt(b, RLeg, 0.3f);
        rig.Hurt(c, LArm, 0.5f);
        rig.Hurt(e, RArm, 0.1f);
        rig.Hurt(f, RLeg, 0.6f);
        void Say(SimCommand cmd) => rig.Say(cmd);
        Say(new TreatOrder([a], b) { Tick = 5 });
        Say(new RestOrder([c]) { Tick = 8 });
        Say(new MedicJobCommand([a]) { Tick = 200 });
        Say(new MoveOrder([c], new Vector3(1300, 0, 2200)) { Tick = 260 });
        Say(new TreatOrder([a], a) { Tick = 400 });
        var hashes = new List<ulong>();
        int ran = 0;
        foreach (int target in new[] { 4, 60, 150, 400, 900, 1500 })
        {
            rig.World.RunTicks(target - ran);
            ran = target;
            hashes.Add(rig.World.StateHash());
        }
        var done = (Players: Bandage(rig.Body(b)) + Bandage(rig.Body(c)) + Bandage(rig.Body(a)), Npcs: Bandage(rig.Body(e)) + Bandage(rig.Body(f)));

        Assert.True(done.Players > 100 && done.Npcs > 100, $"the script treats: {done}");

        return hashes;
    }

    /// <summary>
    /// docs/game/ai-tasks.md "Medic jobs" (second pass) and character-stats.md "First aid update" (kitless): a guard with no kit walks to its town's
    /// caged prisoner and bandages it at 1 HP a second.
    /// </summary>
    [Fact]
    public void An_npc_guard_without_a_kit_treats_a_hurt_prisoner_in_its_factions_cage()
    {
        var data = ArrestTests.Town();
        int city = data.FactionIndex("50-a"), stranger = data.FactionIndex("204-gamedata.base");
        var walk = new OpenGroundWalkability((_, _) => 150);
        var items = new ItemFactory(KitDatabase(), BaseGame);
        var systems = new List<ITickSystem>
        {
            new PlayerSystem(), new MovementSystem(new PathService(walk, true)),
                    new FirstAidSystem(BaseGame, BodyOptions.Default, items) { Relations = data.Relations },
            new BodySystem(BaseGame, BodyOptions.Default),
        };
        using var world = new SimWorld(new WorldSettings { Seed = 7, PublishSnapshots = false, MinPartitionSize = 1 }, walk, systems);
        world.Fixtures.RegisterSite(world, data, 0);
        var cage = world.Fixtures.Find("0.0:cageA")!;

        Assert.Equal(city, cage.Owner);

        CharacterId Spawn(Vector3 at, int faction, int squad)
        {
            var stats = new CharacterStats();
            stats[StatsEnumerated.Medic] = 50;
            stats[StatsEnumerated.Strength] = 50;
            var cold = new CharacterCold { Name = "C" + squad, Race = Human, Stats = stats, Medical = MedicalState.Create(Human, 50),
                    Inventory = new Inventory(), Faction = faction, SquadId = squad };
            var id = world.Characters.Spawn(new CharacterHot { Position = at, MaxSpeed = 40, WalkSpeed = 15, Task = (byte)CharacterTask.Idle }, cold,
                    0);
            world.Squads.Add(new Squad { Id = squad, TemplateId = "t", Faction = faction, Members = { id } });
            return id;
        }
        var guard = Spawn(cage.Position + new Vector3(0, 0, 300), city, 1);
        var prisoner = Spawn(cage.Position, stranger, 2);

        Assert.True(world.Fixtures.Imprison(world, prisoner, cage));

        var body = world.Characters.Cold(prisoner.Slot)!.Medical!;
        body.Parts[RLeg].Flesh = body.Parts[RLeg].MaxHp * 0.4f;   // 60% to bandage
        long started = -1;
        for (int t = 0; t < 600 && started < 0; t++)
        {
            world.RunTick();
            if (Bandage(body) > 0) started = world.Tick;
        }

        Assert.True(started > 0, "the guard reached the prisoner and began");
        Assert.Equal(CharacterTask.Treat, (CharacterTask)world.Characters.Previous[guard.Slot].Task);

        // Without a kit: 1 HP a game second, 1/30 s a tick.
        float before = Bandage(body);
        world.RunTicks(30);

        Assert.Equal(1f, Bandage(body) - before, 0.05);

        for (int t = 0; t < 3000 && (CharacterTask)world.Characters.Previous[guard.Slot].Task == CharacterTask.Treat; t++) world.RunTick();

        Assert.Equal(CharacterTask.Idle, (CharacterTask)world.Characters.Previous[guard.Slot].Task);
        Assert.False(body.NeedsAid(Human, false));
        Assert.True(body.Parts[RLeg].Flesh + body.Parts[RLeg].Bandage >= body.Parts[RLeg].MaxHp - 0.01f, "the leg is bandaged up to its maximum");
        Assert.True(world.Characters.Cold(prisoner.Slot)!.IsImprisoned, "the prisoner stays in its cage");
    }

    /// <summary>
    /// docs/game/character-stats.md "First aid update" (kitless): once a prisoner hurt in several parts is bandaged up, the guard without a kit
    /// stops, though healing goes on turning bandage into flesh (the parts of a Rebirth Slave Shop prisoner seen treated for ever).
    /// </summary>
    [Fact]
    public void A_guard_without_a_kit_stops_once_a_prisoner_hurt_in_several_parts_is_bandaged_up()
    {
        var data = ArrestTests.Town();
        int city = data.FactionIndex("50-a"), stranger = data.FactionIndex("204-gamedata.base");
        var walk = new OpenGroundWalkability((_, _) => 150);
        var items = new ItemFactory(KitDatabase(), BaseGame);
        var systems = new List<ITickSystem>
        {
            new PlayerSystem(), new MovementSystem(new PathService(walk, true)),
                    new FirstAidSystem(BaseGame, BodyOptions.Default, items) { Relations = data.Relations },
            new BodySystem(BaseGame, BodyOptions.Default),
        };
        using var world = new SimWorld(new WorldSettings { Seed = 7, PublishSnapshots = false, MinPartitionSize = 1 }, walk, systems);
        world.Fixtures.RegisterSite(world, data, 0);
        var cage = world.Fixtures.Find("0.0:cageA")!;
        CharacterId Spawn(Vector3 at, int faction, int squad)
        {
            var stats = new CharacterStats();
            stats[StatsEnumerated.Medic] = 50;
            stats[StatsEnumerated.Strength] = 50;
            var cold = new CharacterCold { Name = "C" + squad, Race = Human, Stats = stats, Medical = MedicalState.Create(Human, 50),
                    Inventory = new Inventory(), Faction = faction, SquadId = squad };
            var id = world.Characters.Spawn(new CharacterHot { Position = at, MaxSpeed = 40, WalkSpeed = 15, Task = (byte)CharacterTask.Idle }, cold,
                    0);
            world.Squads.Add(new Squad { Id = squad, TemplateId = "t", Faction = faction, Members = { id } });
            return id;
        }
        var guard = Spawn(cage.Position + new Vector3(0, 0, 300), city, 1);
        var prisoner = Spawn(cage.Position, stranger, 2);

        Assert.True(world.Fixtures.Imprison(world, prisoner, cage));

        var body = world.Characters.Cold(prisoner.Slot)!.Medical!;

        // The Rebirth prisoner's three hurt parts (41.3, 70.8 and 70.5 of 100).
        foreach (var (part, fraction) in new[] { (HeadIx, 0.413f), (LArm, 0.708f),
                (RLeg, 0.705f) }) body.Parts[part].Flesh = body.Parts[part].MaxHp * fraction;
        CharacterTask Task() => (CharacterTask)world.Characters.Previous[guard.Slot].Task;
        long full = -1;
        for (int t = 0; t < 12000 && (full < 0 || world.Tick < full + 300); t++)
        {
            world.RunTick();
            if (full < 0 && body.Parts.Sum(p => p.Bandage) > 0 && !body.NeedsAid(Human, false)) full = world.Tick;
            if (full >= 0 && Task() != CharacterTask.Treat) break;
        }

        Assert.True(full > 0, "the guard bandaged the prisoner up");
        Assert.NotEqual(CharacterTask.Treat, Task());
    }

    sealed record CagedWorld(SimWorld World, CharacterId Guard, CharacterId Prisoner, MedicalState Body, Vector3 Door) : IDisposable
    {
        public void Dispose() => World.Dispose();
    }

    /// <summary>
    /// A city guard 300 from a cage holding a stranger hurt in the right leg; the cage has a use node 30 to its side (local +X), raised by
    /// <paramref name="rise"/>. With <paramref name="goals"/> the AI runs the guard's squad by them (all its roles), else the guard is an NPC the AI
    /// does not run.
    /// </summary>
    static CagedWorld CagedPrisoner(float rise, AiGoal[]? goals = null)
    {
        // The cage record with a use node 30 to its side (local +X), raised by `rise`.
        var node = SyntheticTown.Rec("56-a", FcsRecordType.Building, "Use node");
        node.Bools["is node"] = true;
        node.Ints["node type"] = 4;
        var data = ArrestTests.Town([node]);
        var withDoor = SyntheticTown.Rec("54-a", FcsRecordType.Building, "Prisoner Cage");   // a later file adds the node to the cage, as a mod would
        withDoor.Instances.Add(new FcsInstance { Id = "door", Target = "56-a", Position = new Vector3(30, rise, 0) });
        var edit = new FcsFile();
        edit.Records.Add(withDoor);
        data.Db.Apply(edit, "door.mod");
        int city = data.FactionIndex("50-a"), stranger = data.FactionIndex("204-gamedata.base");
        var walk = new OpenGroundWalkability((_, _) => 150);
        var items = new ItemFactory(KitDatabase(), BaseGame);
        ITickSystem[] systems =
        [
            .. goals is null ? Array.Empty<ITickSystem>() : [new AiSystem(data)],
            new PlayerSystem(), new MovementSystem(new PathService(walk, true)),
                    new FirstAidSystem(BaseGame, BodyOptions.Default, items) { Relations = data.Relations },
            new BodySystem(BaseGame, BodyOptions.Default),
        ];
        var world = new SimWorld(new WorldSettings { Seed = 7, PublishSnapshots = false, MinPartitionSize = 1 }, walk, systems);
        world.Fixtures.RegisterSite(world, data, 0);
        var cage = world.Fixtures.Find("0.0:cageA")!;
        var door = FixtureRegistry.ExitSpot(cage)!.Value;
        CharacterId Spawn(Vector3 at, int faction, int squad)
        {
            var stats = new CharacterStats();
            stats[StatsEnumerated.Medic] = 50;
            stats[StatsEnumerated.Strength] = 50;
            var cold = new CharacterCold { Name = "C" + squad, Race = Human, Stats = stats, Medical = MedicalState.Create(Human, 50),
                    Inventory = new Inventory(), Faction = faction, SquadId = squad };
            var id = world.Characters.Spawn(new CharacterHot { Position = at, MaxSpeed = 40, WalkSpeed = 15, Task = (byte)CharacterTask.Idle }, cold,
                    0);
            world.Squads.Add(new Squad { Id = squad, TemplateId = "t", Faction = faction, Members = { id } });
            return id;
        }
        var guard = Spawn(cage.Position + new Vector3(0, 0, -300), city, 1);
        var prisoner = Spawn(cage.Position, stranger, 2);

        Assert.True(world.Fixtures.Imprison(world, prisoner, cage));

        var body = world.Characters.Cold(prisoner.Slot)!.Medical!;
        body.Parts[RLeg].Flesh = body.Parts[RLeg].MaxHp * 0.4f;
        if (goals is not null) GiveGoals(world, 1, goals);
        return new CagedWorld(world, guard, prisoner, body, door);
    }

    /// <summary>
    /// Gives a squad one AI package with <paramref name="goals"/> in every role, and the AI's first serial step its minds.
    /// </summary>
    static void GiveGoals(SimWorld world, int squad, AiGoal[] goals)
    {
        var board = new Blackboard([new(0, new AiPackage { Id = "p", Name = "p", Roles = [goals, goals, goals, [], []] })]);
        board.Update();
        world.Squads.Find(squad)!.Ai = board;
        world.RunTick();
    }

    static AiGoal Goal(TaskType type, TaskPriority priority) =>
        new(new AiTask("t", type.ToString(), type, priority, TaskTargetType.Specific, TaskEndEvent.Nothing), 0, 100, 1);

    /// <summary>
    /// docs/game/ai-tasks.md "Medic jobs": an NPC the AI runs treats as its Medic goal (58, Urgent in the guard packages), scored at the patient
    /// finder's kept score, so a guard holding its post leaves it for a caged prisoner; one whose lists hold no 58 never treats. Catches the old
    /// rule, under which only an idle NPC looked for a patient (issue #163).
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_guard_on_its_post_treats_a_caged_prisoner_by_its_medic_goal(bool listsMedic)
    {
        var post = Goal(TaskType.HoldPosition, TaskPriority.NonUrgent);
        AiGoal[] goals = listsMedic ? [post, Goal(TaskType.JobMedic, TaskPriority.Urgent)] : [post];
        using var c = CagedPrisoner(0, goals);

        Assert.NotNull(c.World.Characters.Cold(c.Guard.Slot)!.Ai);

        for (int t = 0; t < 3000 && Bandage(c.Body) == 0; t++) c.World.RunTick();

        Assert.Equal(listsMedic, Bandage(c.Body) > 0);
    }
}

/// <summary>
/// The kits of the installed game: a CHARACTER that lists one is given it by the usual loadout rules.
/// </summary>
[Slow]
public class FirstAidInstallTests
{
    [Fact]
    public void A_character_that_lists_a_kit_carries_it()
    {
        Assert.SkipWhen(InstallData.BaseGame is null, "Kenshi install not found");

        var db = InstallData.BaseGame!;
        var items = new ItemFactory(db, GameConstants.FromDatabase(db));
        var citizen = db.OfType(FcsRecordType.Character).First(r => r.Name == "Citizen"
                && r.GetReferences("inventory").Any(i => i.TargetStringId == "515-gamedata.base"));
        var inventory = items.Build(citizen, null);

        Assert.Contains(inventory.All(), i => i.Record == "515-gamedata.base" && i.ItemFunction == 1);
    }
}
