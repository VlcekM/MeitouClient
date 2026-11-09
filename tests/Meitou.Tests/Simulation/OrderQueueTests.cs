using System.Numerics;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Data.World;
using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation;
using Meitou.Simulation.Aggro;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Construction;
using Meitou.Simulation.FirstAid;
using Meitou.Simulation.Items;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Player;
using Meitou.Simulation.Production;
using Meitou.Tests.Combat;
using static Meitou.Tests.Bodies.BodyFixtures;
using static Meitou.Tests.Combat.CombatFixtures;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Simulation;

/// <summary>
/// The order queue (docs/simulation.md "Order queue"; the original's <c>checkOrders</c> and <c>Character::addOrder</c>, docs/game/ai-tasks.md
/// "Precedence").
/// </summary>
public class OrderQueueTests
{
    static TaskType[] Rows(TaskStartTests.Rig rig, CharacterId id) => [.. rig.Cold(id).OrderQueue.Select(e => e.Row)];

    /// <summary>
    /// <c>addOrder</c>: an order without Shift clears the queue first, one with Shift waits behind the order in hand (a move, then a non-move order).
    /// Catches Shift queueing only moves, and an order that starts without replacing the queue.
    /// </summary>
    [Fact]
    public void An_order_without_Shift_clears_the_queue_and_one_with_Shift_waits_behind()
    {
        using var rig = new TaskStartTests.Rig(carrying: false);
        rig.World.RunTicks(3);
        var f = rig.Fighter;
        long t = rig.World.Tick;
        rig.Say(new MoveOrder([f], new Vector3(900, 0, 1900)) { Tick = t });
        rig.Say(new MoveOrder([f], new Vector3(900, 0, 2100)) { Tick = t, Queued = true });
        rig.Say(new RestOrder([f]) { Tick = t, Queued = true });
        rig.World.RunTick();

        Assert.Equal([TaskType.MoveCusOrdered, TaskType.MoveCusOrdered, TaskType.Rest], Rows(rig, f));
        Assert.Equal([true, false, false], rig.Cold(f).OrderQueue.Select(e => e.Started));
        Assert.Equal(CharacterTask.GoTo, rig.Task(f));

        rig.Say(new TreatOrder([f], rig.Mate) { Tick = rig.World.Tick });
        rig.World.RunTick();

        Assert.Equal([TaskType.FirstAidOrder], Rows(rig, f));
        Assert.True(rig.Cold(f).OrderQueue[0].Started);
        Assert.Equal(CharacterTask.Treat, rig.Task(f));
    }

    /// <summary>
    /// A player order that fails to plan stays queued and the chooser goes on (docs/game/ai-tasks.md "Precedence"): a First Aid order with no medkit
    /// waits while the character's job (operating the well) runs, and starts once a kit is back. Catches a refused order being dropped, or one that
    /// blocks the character's jobs.
    /// </summary>
    [Fact]
    public void An_order_that_cannot_run_stays_queued_while_a_job_runs_and_starts_once_it_can()
    {
        using var rig = new TaskStartTests.Rig(carrying: false);
        rig.World.RunTicks(3);
        var f = rig.Fighter;
        var inventory = rig.Cold(f).Inventory!;
        var kit = inventory.All().First(i => i.ItemFunction == ItemInfo.FirstAidKitFunction);

        Assert.True(inventory.Remove(kit));

        rig.Say(new StopCommand([f]) { Tick = rig.World.Tick });
        rig.Say(new AddJob([f], TaskType.OperateMachinery, rig.WellSite) { Tick = rig.World.Tick });
        rig.Say(new TreatOrder([f], rig.Mate) { Tick = rig.World.Tick });
        rig.World.RunTick();

        Assert.Equal([TaskType.FirstAidOrder], Rows(rig, f));
        Assert.False(rig.Cold(f).OrderQueue[0].Started);

        rig.World.RunTicks(ProductionSystem.JobCheckTicks * 2);

        Assert.Equal(CharacterTask.Operate, rig.Task(f));
        Assert.Equal([TaskType.FirstAidOrder], Rows(rig, f));

        inventory.Add(kit);
        rig.World.RunTicks(ProductionSystem.JobCheckTicks + 1);

        Assert.Equal(CharacterTask.Treat, rig.Task(f));
        Assert.True(rig.Cold(f).OrderQueue[0].Started);
    }

    /// <summary>
    /// S3: Turret gunner 146 needs <c>!158</c> (no melee threat within 40), so an ordered gunner attacked in melee cannot run its order: it falls
    /// through to Self preservation and fights back, and mans the turret again once the attacker is down. Catches the order's task staying (no
    /// defence) and the order being dropped (no return).
    /// </summary>
    [Fact]
    public void An_ordered_turret_gunner_attacked_in_melee_defends_itself_and_mans_the_turret_again()
    {
        using var rig = new TurretRig();
        var (gunner, enemy) = (rig.Gunner, rig.Enemy);
        rig.Say(new UseTurretOrder([gunner], rig.Turret) { Tick = 0 });
        rig.RunUntil(() => rig.Task(gunner) == CharacterTask.Operate && rig.Cold(gunner).OperateSite == rig.Turret, 600, "manning the turret");
        rig.Say(new AttackOrder([enemy], gunner) { Tick = rig.World.Tick });
        rig.RunUntil(() => rig.Task(gunner) == CharacterTask.Attack && rig.Cold(gunner).InCombat, 600, "the gunner fighting back");

        Assert.Equal([TaskType.UseTurret], Rows(rig.World, gunner));
        Assert.False(rig.Cold(gunner).OrderQueue[0].Started);

        rig.Cold(enemy).Medical!.KnockOut(100000);
        rig.RunUntil(() => rig.Task(gunner) == CharacterTask.Operate, 900, "the gunner back on the turret");

        Assert.True(rig.Cold(gunner).OrderQueue[0].Started);
    }

    static TaskType[] Rows(SimWorld world, CharacterId id) => [.. world.Characters.Cold(id.Slot)!.OrderQueue.Select(e => e.Row)];

    /// <summary>
    /// A player's turret (function 12) with a gunner of the player's next to it and an enemy fighter 120 away; movement, pursuit, body, production,
    /// combat and retaliation; with <paramref name="aggro"/> also the attack-on-sight look, the two factions hostile (the urgent turret job's sensed
    /// enemies).
    /// </summary>
    internal sealed class TurretRig : IDisposable
    {
        public SimWorld World { get; }
        public CharacterId Gunner, Enemy;
        public string Turret;
        readonly OpenGroundWalkability _walk = new(SyntheticTown.Ground);

        public TurretRig(bool aggro = false)
        {
            var db = FirstAidTests.KitDatabase();
            var fn = new FcsRecord { StringId = "92-t", Flags = 0x10, Name = "92-t", RecordType = FcsRecordType.BuildingFunctionality };
            fn.Ints["function"] = ObjectRanges.TurretFunction;
            fn.Floats["use range"] = 10;
            var turret = new FcsRecord { StringId = "64-t", Flags = 0x10, Name = "Test Turret", RecordType = FcsRecordType.Building };
            turret.References["functionality"] = [new FcsReference("92-t", 0, 0, 0)];
            var file = new FcsFile();
            file.Records.AddRange([fn, turret]);
            db.Apply(file, "turret.mod");
            var items = new ItemFactory(db, BaseGame);
            var combat = new CombatSystem(KatanaTechniques(), Constants, BaseGame, BodyOptions.Default, DuelFixture.Lengths,
                    new CombatOptions { SelfApproach = false, TickMedical = false });
            World = new SimWorld(new WorldSettings { Seed = 7, PublishSnapshots = false }, _walk,
            [
                new PlayerSystem(), new PursuitSystem(combat), new MovementSystem(new PathService(_walk, true)),
                        new BodySystem(BaseGame, BodyOptions.Default),
                new ProductionSystem(db, BaseGame, BodyOptions.Default, items, null), combat, new RetaliationSystem(combat),
                        aggro ? new AggroSystem(Hostile()) { Combat = combat } : Senses(combat),
            ]);
            World.Player.Faction = 1;
            Turret = World.Buildings.NewId(WorldLayout.ZoneOf(1000, 2000));
            World.Buildings.Add(new BuildSite { Id = Turret, Record = "64-t", Position = new Vector3(1000, 300, 2000), Owner = 1, Materials = [],
                    Complete = true });
            Gunner = Add(1000, 2030, player: true);
            Enemy = Add(1000, 2150, player: false);
        }

        /// <summary>
        /// Factions 0 (none), 1 (the player's) and 2, the last two hostile to each other.
        /// </summary>
        static FactionRelations Hostile()
        {
            static FactionData F(string id, params (string, int)[] rel) => new() { Id = id, Name = id,
                    Relations = rel.ToDictionary(r => r.Item1, r => r.Item2) };
            var table = FactionRelations.Build([F("none"), F("player", ("raiders", -80)), F("raiders", ("player", -80))]);
            table.PlayerFaction = 1;
            return table;
        }

        CharacterId Add(float x, float z, bool player)
        {
            var cold = new Combatant { Name = player ? "gunner" : "enemy" }.MakeCold();
            cold.Race = new RaceData { StringId = "17-c", Name = "Greenlander", StatMultipliers = Human.StatMultipliers, Anatomy = Human.Anatomy,
                    Carriable = true };
            cold.IsPlayer = player;
            cold.Faction = player ? 1 : 2;
            return World.Characters.Spawn(new CharacterHot { Position = new Vector3(x, 300, z), MaxSpeed = 40, WalkSpeed = 15 }, cold, 0);
        }

        public CharacterCold Cold(CharacterId id) => World.Characters.Cold(id.Slot)!;
        public CharacterTask Task(CharacterId id) => (CharacterTask)World.Characters.Previous[id.Slot].Task;
        public void Say(SimCommand c) => World.Commands.Enqueue(c);

        public void RunUntil(Func<bool> done, int max, string what)
        {
            for (int t = 0; t < max && !done(); t++) World.RunTick();

            Assert.True(done(), $"{what} within {max} ticks");
        }

        public void Dispose() => World.Dispose();
    }

    /// <summary>
    /// Every order goes through the one order path while the game is paused too: each order type sets its act-out between ticks. Catches an order the
    /// paused interface drops (it applied a hand-kept list of commands, which left out pick up, loot, operate, build, beds and others).
    /// </summary>
    [Fact]
    public void Every_order_applies_while_paused() => Assert.All(TaskStartTests.Orders, AppliesWhilePaused);

    static void AppliesWhilePaused(string name)
    {
        using var rig = new TaskStartTests.Rig(carrying: name is "put down" or "put in bed" or "put in cage");
        rig.World.RunTicks(3);
        var (order, task) = TaskStartTests.Order(rig, name, rig.World.Tick);
        rig.Say(order);
        rig.World.ApplyPausedInterfaceCommands();

        Assert.Equal(task, rig.Task(rig.Fighter));
    }
}
