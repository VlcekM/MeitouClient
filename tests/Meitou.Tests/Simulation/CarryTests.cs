using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Simulation;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Carrying;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Items;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Player;
using SimWorld = Meitou.Simulation.World;
using static Meitou.Tests.Bodies.BodyFixtures;

namespace Meitou.Tests.Simulation;

/// <summary>
/// Carrying downed characters, rescue and looting (docs/game/carry-loot.md, docs/simulation.md "Carrying, rescue and looting").
/// </summary>
public class CarryTests
{
    /// <summary>
    /// The fixture's Greenlander, carriable like every base race but the Leviathan.
    /// </summary>
    static readonly RaceData Person = new() { StringId = "17-c", Name = "Greenlander", StatMultipliers = Human.StatMultipliers,
            Anatomy = Human.Anatomy, Carriable = true };

    /// <summary>
    /// Hand-made characters on open ground with the game's movement, body and carry systems (in the game's order).
    /// </summary>
    sealed class Rig : IDisposable
    {
        public SimWorld World { get; }
        public CarrySystem Carry { get; }
        readonly OpenGroundWalkability _walk = new(SyntheticTown.Ground);

        public Rig(int threads = 1, int minPartition = 16, FactionRelations? relations = null, ulong seed = 5, CarrySettings? settings = null)
        {
            Carry = new CarrySystem(BaseGame, BodyOptions.Default, 1, settings) { Relations = relations };
            var systems = new List<ITickSystem> { new PlayerSystem(), new MovementSystem(new PathService(_walk, true)),
                    new BodySystem(BaseGame, BodyOptions.Default), Carry };
            World = new SimWorld(new WorldSettings { Seed = seed, Threads = threads, PublishSnapshots = false, MinPartitionSize = minPartition },
                    _walk, systems);
        }

        public CharacterId Add(float x, float z, bool player = true, int squad = 0, int faction = 0, float strength = 50, RaceData? race = null,
                CharacterTask task = CharacterTask.Idle, float yaw = 0, bool leader = false)
        {
            var stats = new CharacterStats();
            stats[StatsEnumerated.Strength] = strength;
            var cold = new CharacterCold
            {
                Name = "C" + World.Characters.HighWater, Race = race ?? Person, Stats = stats,
                        Medical = MedicalState.Create(race ?? Person, strength), Inventory = new Inventory(),
                IsPlayer = player, SquadId = squad, Faction = faction,
            };
            var hot = new CharacterHot { Position = new Vector3(x, 300, z), MaxSpeed = 40, WalkSpeed = 15, Task = (byte)task, Yaw = yaw };
            var id = World.Characters.Spawn(hot, cold, 0);
            var sq = World.Squads.Find(squad);
            if (sq is null)
                World.Squads.Add(sq = new Squad { Id = squad, TemplateId = "t", Faction = faction, HomeCentre = new Vector2(x, z),
                        HomeRadius = 100 });
            sq.Members.Add(id);
            if (leader || sq.Leader.IsNone) sq.Leader = id;
            return id;
        }

        public CharacterCold Cold(CharacterId id) => World.Characters.Cold(id.Slot)!;
        public MedicalState Body(CharacterId id) => Cold(id).Medical!;
        public ref readonly CharacterHot Hot(CharacterId id) => ref World.Characters.Previous[id.Slot];
        public CharacterTask Task(CharacterId id) => (CharacterTask)Hot(id).Task;
        public void Say(SimCommand c) => World.Commands.Enqueue(c);

        /// <summary>
        /// Orders a pick-up and runs until the lift is over; returns the ticks it took.
        /// </summary>
        public int PickUp(CharacterId carrier, CharacterId body, int max = 1500)
        {
            Say(new PickUpOrder([carrier], body) { Tick = World.Tick });
            for (int t = 1; t <= max; t++)
            {
                World.RunTick();
                if (Cold(carrier).Carrying == body && Task(carrier) != CharacterTask.PickUp) return t;
            }
            throw new Xunit.Sdk.XunitException("the pick-up did not finish");
        }

        public void Dispose() => World.Dispose();
    }

    static float Flat(Vector3 a, Vector3 b) => Vector2.Distance(new Vector2(a.X, a.Z), new Vector2(b.X, b.Z));

    // ------------------------------------------------------------------ carrying

    [Fact]
    public void A_pick_up_walks_lifts_at_the_documented_clip_point_and_adds_the_carry_person_weight()
    {
        using var rig = new Rig();
        var carrier = rig.Add(1000, 2000, strength: 5);
        var body = rig.Add(1100, 2000, player: false, squad: 1, faction: 1);
        rig.Body(body).KnockOut(10000);
        rig.World.RunTicks(2);
        float unladen = rig.Hot(carrier).MaxSpeed;
        rig.Say(new PickUpOrder([carrier], body) { Tick = rig.World.Tick });
        long liftStart = -1, attached = -1;
        for (int t = 0; t < 1500 && rig.Task(carrier) == CharacterTask.PickUp || t == 0; t++)
        {
            rig.World.RunTick();
            if (liftStart < 0 && rig.Cold(carrier).LiftTime >= 0) liftStart = rig.World.Tick;
            if (attached < 0 && rig.Cold(carrier).Carrying == body) attached = rig.World.Tick;
        }

        Assert.True(liftStart > 0 && attached > 0, "it walked up and lifted");
        Assert.True(Flat(rig.Hot(carrier).Position, new Vector3(1000, 0, 2000)) > 70, "it walked to the body");

        // The clip is 1 s here: the body is attached once more than 0.4 of it has played (12 or 13 ticks of 1/30 s, by float rounding), the lift ends
        // at 0.95.
        Assert.InRange(attached - liftStart, 11, 13);
        Assert.Equal(CharacterTask.Idle, rig.Task(carrier));
        Assert.Equal(carrier, rig.Cold(body).CarriedBy);

        rig.World.RunTicks(2);

        Assert.Equal(rig.Hot(carrier).Position, rig.Hot(body).Position);

        // The load is carry person weight (30 in the base game) on top of an empty inventory: strength 5 carries 1.2 x 5 + 15 = 21 freely, so the
        // factor drops.
        var c = rig.Cold(carrier);
        float factor = Encumbrance.Factor(BaseGame, 0, true, 5, c.Medical!.StatMultiplier(StatsEnumerated.Strength));

        Assert.Equal((21 - 0.05f * 30) / (0.95f * 30), Encumbrance.Factor(BaseGame, 0, true, 5), 4);
        Assert.True(factor < 1);
        Assert.Equal(Speed.Run(Person, c.Stats!, c.Medical!, factor), rig.Hot(carrier).MaxSpeed, 3);
        Assert.True(rig.Hot(carrier).MaxSpeed < unladen);
    }

    [Fact]
    public void The_body_is_dropped_when_the_carrier_is_knocked_out_or_loses_its_left_arm()
    {
        using var rig = new Rig();
        var a = rig.Add(1000, 2000);
        var b = rig.Add(1400, 2000, squad: 2);
        var c = rig.Add(1800, 2000, squad: 3);
        var bodies = new[] { rig.Add(1010, 2000, false, 4, 1), rig.Add(1410, 2000, false, 5, 1), rig.Add(1810, 2000, false, 6, 1) };
        foreach (var body in bodies) rig.Body(body).KnockOut(10000);
        rig.World.RunTick();
        rig.PickUp(a, bodies[0]);
        rig.PickUp(b, bodies[1]);
        rig.PickUp(c, bodies[2]);
        rig.Body(a).KnockOut(100);                       // knocked out
        rig.Body(b).Parts[LArm].Limb = LimbState.Stump;   // the left arm cut off
        rig.Body(c).Parts[LArm].Flesh = -30;              // the left arm limp
        rig.World.RunTick();
        foreach (var (carrier, body) in new[] { (a, bodies[0]), (b, bodies[1]), (c, bodies[2]) })
        {
            Assert.True(rig.Cold(carrier).Carrying.IsNone);
            Assert.True(rig.Cold(body).CarriedBy.IsNone);
            Assert.True(Flat(rig.Hot(carrier).Position, rig.Hot(body).Position) < 0.01f, "dropped where the carrier stands");
        }
    }

    [Fact]
    public void A_squad_mate_lifted_awake_stays_carried_and_one_lifted_down_is_set_down_when_it_wakes()
    {
        using var rig = new Rig();
        var a = rig.Add(1000, 2000);
        var awake = rig.Add(1050, 2000);
        var b = rig.Add(1400, 2000, squad: 2);
        var down = rig.Add(1450, 2000, squad: 2);
        rig.Body(down).KnockOut(10);
        rig.World.RunTick();
        rig.PickUp(a, awake);
        rig.PickUp(b, down);

        Assert.True(rig.Cold(awake).CarriedAwake);
        Assert.True(rig.Body(down).Incapacitated && !rig.Cold(down).CarriedAwake);

        for (int t = 0; t < 1500 && !rig.Cold(down).CarriedBy.IsNone; t++) rig.World.RunTick();

        Assert.False(rig.Body(down).Incapacitated);
        Assert.True(rig.Cold(down).CarriedBy.IsNone, "set down once it came round");
        Assert.Equal(a, rig.Cold(awake).CarriedBy);

        rig.Say(new PutDownOrder([a]) { Tick = rig.World.Tick });
        for (int t = 0; t < 100 && !rig.Cold(awake).CarriedBy.IsNone; t++) rig.World.RunTick();

        Assert.True(rig.Cold(awake).CarriedBy.IsNone);
        Assert.False(rig.Cold(awake).CarriedAwake);
        Assert.False(rig.Body(awake).Incapacitated, "a friendly put-down knocks no one out");
    }

    // ------------------------------------------------------------------ looting

    static ItemInstance Item(string record, int w, int h, int quantity = 1, bool stack = false) =>
        new() { Record = record, Name = record, Width = w, Height = h, Quantity = quantity, StackLimit = stack ? 99 : 1, UnitWeight = 1 };

    // Factions: 0 townsfolk (neutral), 1 the player (Nameless).
    static FactionRelations TwoFactions()
    {
        var table = FactionRelations.Build([new FactionData { Id = "townsfolk", Name = "townsfolk", DefaultRelation = 10 },
                new FactionData { Id = "Nameless", Name = "Nameless", DefaultRelation = 10 }]);
        table.PlayerFaction = 1;
        return table;
    }

    /// <summary>
    /// A player looter, a knocked-out townsman with three items, and a townsman 300 units away facing the looter or away from it. Returns the
    /// relation after the loot.
    /// </summary>
    static (float Relation, LootEntry Loot) LootScene(bool facing)
    {
        var relations = TwoFactions();
        using var rig = new Rig(relations: relations);
        var looter = rig.Add(1000, 2000, faction: 1);
        var victim = rig.Add(1100, 2000, player: false, squad: 1, faction: 0);

        // The witness stands at x 1400 and looks along -X (yaw -90 degrees) at the scene, or along +X away from it.
        rig.Add(1400, 2000, player: false, squad: 2, faction: 0, yaw: facing ? -MathF.PI / 2 : MathF.PI / 2);
        foreach (var r in new[] { "a", "b", "c" }) rig.Cold(victim).Inventory!.Add(Item(r, 1, 1));
        rig.Body(victim).KnockOut(10000);
        rig.World.RunTick();
        float before = relations.Get(0, 1);
        rig.Say(new LootOrder([looter], victim) { Tick = rig.World.Tick });
        for (int t = 0; t < 1000 && rig.Carry.Loots.Count == 0; t++) rig.World.RunTick();

        Assert.Equal(10, before);
        Assert.Equal(3, rig.Cold(looter).Inventory!.Items.Count);
        Assert.Empty(rig.Cold(victim).Inventory!.Items);
        Assert.Equal(CharacterTask.Idle, rig.Task(looter));

        return (relations.Get(0, 1), rig.Carry.Loots.Single());
    }

    [Fact]
    public void Looting_seen_by_a_member_of_the_victims_faction_is_a_theft_and_unseen_it_is_not()
    {
        var (seen, loot) = LootScene(facing: true);

        Assert.True(loot.Witnessed);
        Assert.Equal(3, loot.Taken);
        Assert.Equal(10, seen);   // looting moves no relation (the game has no such relation event)

        var (unseen, quiet) = LootScene(facing: false);

        Assert.False(quiet.Witnessed);
        Assert.Equal(10, unseen);
    }

    [Fact]
    public void Taking_a_death_item_from_a_knocked_out_body_butchers_it_and_the_items_lose_the_flag()
    {
        using var rig = new Rig();
        var looter = rig.Add(1000, 2000);
        var animal = rig.Add(1100, 2000, player: false, squad: 1, faction: 2);
        var meat = Item("meat", 1, 1, 3, stack: true);
        meat.Death = true;
        rig.Cold(animal).Inventory!.Add(meat);
        rig.Cold(looter).Inventory!.Add(new ItemInstance { Record = "meat", Name = "meat", Quantity = 2, StackLimit = 99, UnitWeight = 1 });
        rig.Body(animal).KnockOut(10000);
        rig.World.RunTick();
        rig.Say(new LootOrder([looter], animal) { Tick = rig.World.Tick });
        for (int t = 0; t < 1000 && rig.Carry.Loots.Count == 0; t++) rig.World.RunTick();

        Assert.Equal((true, DeathCause.Butchered), (rig.Body(animal).Dead, rig.Body(animal).Cause));
        Assert.Contains($"{rig.Cold(animal).Name} has been butchered", rig.World.TakeMessages());

        // The taken units are the looter's now, no longer death items.
        var held = rig.Cold(looter).Inventory!.Items;

        Assert.Equal((5, false), (held.Sum(i => i.Quantity), held.Any(i => i.Death)));
        Assert.Empty(rig.Cold(animal).Inventory!.Items);
    }

    // ------------------------------------------------------------------ determinism

    /// <summary>
    /// A scripted game: player characters pick up, carry, put down and loot; one has the rescue job; an NPC squad rescues its leader; a carrier is
    /// knocked out mid-carry. Cut into one-slot partitions; pinned in <see cref="GoldenHashTests"/>.
    /// </summary>
    internal static List<ulong> Scripted(int threads)
    {
        var relations = TwoFactions();
        using var rig = new Rig(threads, minPartition: 1, relations: relations, seed: 21);
        var p1 = rig.Add(1000, 2000, faction: 1, strength: 20);
        var p2 = rig.Add(1050, 2000, faction: 1);
        var p3 = rig.Add(1000, 2100, faction: 1);
        var down1 = rig.Add(1200, 2050, player: false, squad: 1, faction: 0);
        var down2 = rig.Add(1150, 1900, squad: 2, faction: 1);
        var down3 = rig.Add(900, 2200, squad: 3, faction: 1);
        var leader = rig.Add(1600, 2000, player: false, squad: 4, faction: 0, leader: true);
        var rescuer = rig.Add(1650, 2000, player: false, squad: 4, faction: 0, task: CharacterTask.Wander);

        // Its AI chose goal 142 (Find and rescue leader, listed by a few retreat packages);
        // a squad whose package does not list it leaves its leader lying.
        rig.Cold(rescuer).Ai = new Meitou.Simulation.Ai.AiMind
        {
            Squad = rig.World.Squads.Find(4)!,
            Goal = new Meitou.Data.Gameplay.Ai.AiGoal(new Meitou.Data.Gameplay.Ai.AiTask("142", "Find and rescue leader",
                Data.Gameplay.Ai.TaskType.FindAndRescueLeader, Data.Gameplay.Ai.TaskPriority.NonUrgent, Data.Gameplay.Ai.TaskTargetType.Specific,
                Data.Gameplay.Ai.TaskEndEvent.Nothing), 0, 100, 1)
        };
        rig.Add(1700, 2050, player: false, squad: 4, faction: 0, task: CharacterTask.Idle, yaw: -MathF.PI / 2);   // the witness, facing the looting
        foreach (var r in new[] { "a", "b", "c", "d" }) rig.Cold(down1).Inventory!.Add(Item(r, 2, 2));
        rig.Body(down1).KnockOut(10000);
        rig.Body(down2).KnockOut(3);
        rig.Body(down3).KnockOut(10000);
        rig.Body(leader).KnockOut(40);
        void Say(SimCommand c) => rig.Say(c);
        Say(new PickUpOrder([p1], down1) { Tick = 3 });
        Say(new RescueJobCommand([p3]) { Tick = 5 });
        Say(new LootOrder([p2], down1) { Tick = 10 });
        Say(new MoveOrder([p1], new Vector3(1300, 0, 2300)) { Tick = 200 });
        Say(new PutDownOrder([p1]) { Tick = 500 });
        Say(new LootOrder([p2], down1) { Tick = 600 });
        Say(new PickUpOrder([p2], down3) { Tick = 720 });
        var hashes = new List<ulong>();
        int ran = 0;
        var carried = new SortedSet<string>(StringComparer.Ordinal);
        foreach (int target in new[] { 4, 60, 150, 400, 700, 1000, 1500 })
        {
            for (; ran < target; ran++)
            {
                if (ran == 700) rig.Body(p3).KnockOut(5);   // a carrier goes down mid-carry
                rig.World.RunTick();
                foreach (var body in new[] { down1, down2, down3, leader })
                    if (!rig.Cold(body).CarriedBy.IsNone) carried.Add($"{body}<-{rig.Cold(body).CarriedBy}");
            }
            hashes.Add(rig.World.StateHash());
        }
        string happened = $"carried {string.Join(", ", carried)}; "
            + $"loots {string.Join(", ", rig.Carry.Loots.Select(l => $"{l.Taken}/{l.Left}{(l.Witnessed ? " seen" : "")}"))}; "
            + $"relation {relations.Get(0, 1)}";

        // The script lifts (p1 a townsman, p2 a downed mate, an NPC its leader; p3's rescue job finds no bed in this bedless world, so it lifts no
        // one), loots in sight of the townsfolk (the relation stays 10) and loots an emptied body.
        Assert.Equal("carried 3.0<-0.0, 5.0<-1.0, 6.0<-7.0; loots 4/0 seen, 0/0; relation 10", happened);

        return hashes;
    }

    [Fact]
    public void A_carried_body_that_came_round_is_put_down_unless_it_is_chained_and_the_carriers_enemy()
    {
        // Goal 169's desire (FUN_1405a0710): 1 unless the body is chained and the carrier's enemy (FUN_14079c950).
        var table = FactionRelations.Build([new FactionData { Id = "a", Name = "a", DefaultRelation = 50 },
                new FactionData { Id = "b", Name = "b", DefaultRelation = -50 }, new FactionData { Id = "c", Name = "c", DefaultRelation = 50 }]);
        var carry = new CarrySystem(BaseGame, BodyOptions.Default, 1) { Relations = table };
        CharacterCold Make(int faction, bool chained)
        {
            var c = new CharacterCold { Faction = faction, Inventory = new Inventory() };
            if (!chained) return c;
            var shackles = new ItemInstance { Record = "shackles", Name = "shackles",
                    Lock = new Meitou.Simulation.Locks.LockState { Locked = true, Level = 10, Hardness = 5 } };
            c.Inventory.Add(shackles);
            shackles.Section = "legs";
            return c;
        }

        Assert.True(carry.WakesIntoPutDown(Make(0, false), Make(1, false)));
        Assert.False(carry.WakesIntoPutDown(Make(0, false), Make(1, true)));
        Assert.True(carry.WakesIntoPutDown(Make(0, false), Make(2, true)));
        Assert.True(carry.WakesIntoPutDown(Make(0, false), Make(0, true)));
    }

    [Fact]
    public void Goal_169_puts_a_woken_body_down_only_for_a_carrier_whose_goal_lists_hold_it()
    {
        // The AI scores only the goals of its lists: a carrier whose race goals (or package) lack 169 keeps the awake body on its shoulder.
        using var rig = new Rig(1, minPartition: 1, seed: 21);
        var carrier = rig.Add(1000, 2000, player: false, squad: 1, faction: 0);
        var goal = new Meitou.Data.Gameplay.Ai.AiGoal(new Meitou.Data.Gameplay.Ai.AiTask("169", "Putting down",
                Data.Gameplay.Ai.TaskType.PutDownCarriedDudeIfTheyCanWalk, Data.Gameplay.Ai.TaskPriority.Urgent, Data.Gameplay.Ai.TaskTargetType.Self,
                Data.Gameplay.Ai.TaskEndEvent.Nothing), 0, 24, 1);
        var squad = rig.World.Squads.Find(1)!;
        rig.Cold(carrier).Ai = new Meitou.Simulation.Ai.AiMind { Squad = squad };

        Assert.False(rig.Carry.ListsPutDown(rig.World, carrier.Slot, rig.Cold(carrier)));

        rig.Cold(carrier).Ai = new Meitou.Simulation.Ai.AiMind { Squad = squad, RaceGoals = [goal] };

        Assert.True(rig.Carry.ListsPutDown(rig.World, carrier.Slot, rig.Cold(carrier)));
    }
}
