using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Locks;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Player;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Simulation;

/// <summary>
/// M6: <see cref="SimWorld.CanOrder(TaskType, CharacterId, CharacterId, string?, System.Numerics.Vector3)"/> and the snapshot facts the menu reads
/// (docs/simulation.md "Order queue", "Shared predicates").
/// </summary>
public class CanOrderTests
{
    static string[] States => ["standing", "down", "carried", "caged", "asleep", "animal", "crawling"];

    /// <summary>
    /// The order rows the menu gives, by the name <see cref="TaskStartTests.Order"/> uses, with their target and site.
    /// </summary>
    static (TaskType Row, CharacterId Target, string? Site) Row(TaskStartTests.Rig rig, string name) => name switch
    {
        "move" => (TaskType.MoveCusOrdered, CharacterId.None, null),
        "follow" => (TaskType.StayCloseToTarget, rig.Mate, null),
        "first aid" => (TaskType.FirstAidOrder, rig.Mate, null),
        "rest" => (TaskType.Rest, CharacterId.None, null),
        "pick up" => (TaskType.LiftPersonPlayerOrder, rig.Downed, null),
        "loot" => (TaskType.LootTarget, rig.Downed, null),
        "stealth ko" => (TaskType.StealthKnockout, rig.Awake, null),
        "operate" => (TaskType.OperateMachinery, CharacterId.None, rig.WellSite),
        "build" => (TaskType.Build, CharacterId.None, rig.ShedSite),
        "dismantle" => (TaskType.Dismantle, CharacterId.None, rig.WellSite),
        "use bed" => (TaskType.UseBed, CharacterId.None, rig.BedFixture),
        "attack" => (TaskType.FocusedMeleeAttack, rig.OtherEnemy, null),
        _ => throw new ArgumentException(name),
    };

    static IEnumerable<(string State, string Order)> StatesAndOrders()
    {
        foreach (string state in States)
            foreach (string order in new[] { "move", "follow", "first aid", "rest", "pick up", "loot", "stealth ko", "operate", "build", "dismantle",
                    "use bed", "attack" })
                yield return (state, order);
    }

    /// <summary>
    /// <c>CanOrder</c> is the read the queue starts and barks through: for a character that is standing, down, carried, caged, asleep, an animal or
    /// crawling, an order that <c>CanOrder</c> accepts starts its act-out when given and one it refuses does not (it stays queued with its text, or
    /// leaves the queue). Catches a read that drifts from the start path (a handler testing its own precondition, a row's repertoire left out) so
    /// that a menu or the multiplayer gate would offer or refuse what the order then does otherwise.
    /// </summary>
    [Fact]
    public void CanOrder_agrees_with_what_giving_the_order_does() => Assert.All(StatesAndOrders(), row => AgreesWithTheOrder(row.State, row.Order));

    static void AgreesWithTheOrder(string state, string name)
    {
        using var rig = new TaskStartTests.Rig(carrying: false);
        var f = rig.Fighter;
        rig.Say(new StopCommand([f]) { Tick = 0 });
        rig.World.RunTicks(3);
        var cold = rig.Cold(f);
        switch (state)
        {
            case "down":
                cold.Medical!.KnockOut(1000);
                break;
            case "carried":
                (cold.CarriedBy, rig.Cold(rig.Mate).Carrying) = (rig.Mate, f);
                break;
            case "caged":
                rig.World.Fixtures.Imprison(rig.World, f, rig.World.Fixtures.Find(rig.EmptyCage)!);
                break;
            case "animal":
                cold.Fighter = new Fighter(null) { IsAnimal = true };
                break;
            case "crawling":
                cold.Medical!.StayingLow = true;
                break;
            case "asleep":
                rig.Say(new UseBedOrder([f], rig.BedFixture) { Tick = rig.World.Tick });
                for (int i = 0; i < 600 && rig.Task(f) != CharacterTask.Sleep; i++) rig.World.RunTick();

                Assert.Equal(CharacterTask.Sleep, rig.Task(f));

                rig.Say(new StopCommand([f]) { Tick = rig.World.Tick });
                break;
        }
        rig.World.RunTick();
        var (row, target, site) = Row(rig, name);
        var (can, why) = rig.World.CanOrder(row, f, target, site);
        if (can) Assert.Null(why);
        var (order, task) = TaskStartTests.Order(rig, name, rig.World.Tick);
        var before = rig.Task(f);
        rig.Say(order);
        rig.World.RunTick();
        bool started = rig.Task(f) == task && before != task || rig.Cold(f).OrderQueue.Front is { Started: true };

        Assert.True(can == started, $"{state}, {name}: CanOrder {can} ({why}), started {started}, task {before} -> {rig.Task(f)}");
    }

    /// <summary>
    /// A world without the order's owning system or a gone actor has no answer (false, no text), and an order row nobody owns is refused. Catches a
    /// null owner reaching the queue.
    /// </summary>
    [Fact]
    public void CanOrder_answers_false_without_text_for_a_gone_actor_or_an_unowned_row()
    {
        using var rig = new TaskStartTests.Rig(carrying: false);
        rig.World.RunTicks(3);

        Assert.Equal((false, null), rig.World.CanOrder(TaskType.Rest, CharacterId.None));
        Assert.Equal((false, null), rig.World.CanOrder(TaskType.None, rig.Fighter));
    }

    /// <summary>
    /// The snapshot facts the menu reads, from the simulation: Conscious (not dead, out cold or carried), Standing (not dead or out cold), Animal,
    /// and HostileToPlayer (another faction without relations, or one that fights the player). Catches the UI's own copies drifting from them, and a
    /// replica client getting none.
    /// </summary>
    [Fact]
    public void The_snapshot_publishes_the_facts_the_menu_reads()
    {
        using var world = new SimWorld(new WorldSettings { Seed = 3 }, new OpenGroundWalkability(SyntheticTown.Ground));
        CharacterId Add(int faction, Action<CharacterCold>? shape = null)
        {
            var cold = new Combat.Combatant().MakeCold();
            cold.Faction = faction;
            shape?.Invoke(cold);
            return world.Characters.Spawn(new CharacterHot { Position = new(100 + world.Characters.HighWater * 20, 300, 100) }, cold, 0);
        }
        world.Player.Faction = 1;
        var own = Add(1);
        var other = Add(2);
        var ko = Add(2, c => c.Medical!.KnockOut(1000));
        var carried = Add(2, c => c.CarriedBy = own);
        var animal = Add(2, c => c.Fighter = new Fighter(null) { IsAnimal = true });
        world.RunTick();
        CharacterSnapshot Of(CharacterId id) => world.Snapshot.Characters.Single(c => c.Id == id);

        Assert.True(Of(other).Conscious && Of(other).Standing && !Of(other).Animal);
        Assert.True(!Of(ko).Conscious && !Of(ko).Standing);
        Assert.True(!Of(carried).Conscious && Of(carried).Standing);
        Assert.True(Of(animal).Animal);
        Assert.True(Of(other).HostileToPlayer);
        Assert.True(CharacterFacts.HostileToPlayer(null, 1, 2, fightsPlayer: false));
        Assert.True(CharacterFacts.HostileToPlayer(null, 1, 1, fightsPlayer: true));
    }

    /// <summary>
    /// An animal holding a limb in its mouth publishes the limb's record and the skin tone of the character it was cut from, one with an empty mouth
    /// none (the renderer draws the limb from them, docs/game/character-stats.md "Eating"). Catches the snapshot dropping them.
    /// </summary>
    [Fact]
    public void The_snapshot_publishes_the_limb_in_an_animals_mouth()
    {
        using var world = new SimWorld(new WorldSettings { Seed = 3 }, new OpenGroundWalkability(SyntheticTown.Ground));
        var tone = new System.Numerics.Vector3(0.2f, 0.4f, 0.6f);
        CharacterId Add(Action<CharacterCold>? shape = null)
        {
            var cold = new Combat.Combatant().MakeCold();
            shape?.Invoke(cold);
            return world.Characters.Spawn(new CharacterHot { Position = new(100 + world.Characters.HighWater * 20, 300, 100) }, cold, 0);
        }
        var holder = Add(c => (c.Mouth, c.MouthTone) = (new Meitou.Simulation.Items.ItemInstance { Record = "limb-t" }, tone));
        var empty = Add();
        world.RunTick();
        CharacterSnapshot Of(CharacterId id) => world.Snapshot.Characters.Single(c => c.Id == id);

        Assert.Equal(("limb-t", tone), (Of(holder).Mouth, Of(holder).MouthTone));
        Assert.Null(Of(empty).Mouth);
    }
}
