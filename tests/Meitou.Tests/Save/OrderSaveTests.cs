using System.Numerics;
using Meitou.Data;
using Meitou.Data.Gameplay;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay.Ai;
using Meitou.Data.Save;
using Meitou.Simulation;
using Meitou.Simulation.Dialogue;
using Meitou.Simulation.Factions;
using Meitou.Simulation.FirstAid;
using Meitou.Simulation.Locks;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Player;
using Meitou.Simulation.Population;
using Meitou.Simulation.Saving;
using Meitou.Tests.Simulation;
using static Meitou.Tests.Save.SaveSamples;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Save;

/// <summary>
/// A player character's order queue in a save (docs/simulation.md "Order queue", docs/formats/save.md "Order queue"): the original's
/// <c>tjob&lt;i&gt;</c> keys of the GAMESTATE_AI record.
/// </summary>
public class OrderSaveTests
{
    const StandardParts Parts = StandardParts.Population | StandardParts.Player | StandardParts.Movement
        | StandardParts.Body | StandardParts.FirstAid;
    static readonly Vector3 First = SyntheticTown.Centre + new Vector3(300, 0, 0), Second = SyntheticTown.Centre + new Vector3(300, 0, 200);

    sealed record Game(SimWorld World, PopulationData Data, Squad Squad) : IDisposable
    {
        public void Dispose() => World.Dispose();
    }

    /// <summary>
    /// A new game of the synthetic town with the rig's systems and <paramref name="extra"/>; with <paramref name="start"/> the player's squad starts.
    /// </summary>
    static Game NewGame(bool start = true, StandardParts extra = StandardParts.None)
    {
        var db = SyntheticTown.Database(anatomy: true);
        var data = SyntheticTown.Data(db);
        var walk = new OpenGroundWalkability(SyntheticTown.Ground);
        var systems = SyntheticTown.Standard(data, walk, Parts | extra, new PopulationSettings { CheckEveryTicks = 1 });
        var world = new SimWorld(new WorldSettings { Seed = 11, PublishSnapshots = false }, walk, systems.Systems);
        var squad = start ? systems.Population!.StartPlayer(world, NewGameStart.From(db.Find("40-t")!)) : null!;
        return new Game(world, data, squad);
    }

    static TaskType[] Rows(SimWorld world, CharacterId id) => [.. world.Characters.Cold(id.Slot)!.OrderQueue.Select(e => e.Row)];

    /// <summary>
    /// The rows of the character's second list (<see cref="CharacterCold.SecondList"/>), where a load puts its orders.
    /// </summary>
    static TaskType[] SecondRows(SimWorld world, CharacterId id) => [.. world.Characters.Cold(id.Slot)!.SecondList.Select(e => e.Row)];

    static FcsRecord AiOf(SaveGame save, int member) => save.Platoons.Single(p => p.FactionId == save.PlayerFaction!.Id).Characters[member].Ai!;

    static IEnumerable<string> OrderKeys(FcsRecord ai) =>
        ai.Ints.Where(k => k.Key.StartsWith("tjob", StringComparison.Ordinal) || k.Key.StartsWith("meitou tjob",
                StringComparison.Ordinal)).Select(k => $"{k.Key}={k.Value}")
            .Concat(ai.Vector3S.Where(k => k.Key.StartsWith("tjob", StringComparison.Ordinal)).Select(k => $"{k.Key}={k.Value}"))
            .Concat(ai.Strings.Where(k => k.Key.StartsWith("meitou tjob",
                    StringComparison.Ordinal)).Select(k => $"{k.Key}={k.Value}")).Order(StringComparer.Ordinal);

    /// <summary>
    /// A Shift-queued move, Rest and First Aid are written as the original's <c>tjob&lt;i&gt;</c> / <c>tjobP&lt;i&gt;</c> / <c>tjobT&lt;i&gt;</c>
    /// (front first, the started move too) and read back into the second list as the original's loader adds them (<c>FUN_1405092d0</c> through
    /// <c>FUN_1405086d0</c>: First Aid 25 as its job form 58 Medic), the first started by the chooser; a later save writes the deque alone
    /// (<c>FUN_1405090b0</c>), so none of them; the Medic entry stays when its patient is gone (its finder picks). Catches orders lost by a load,
    /// a front restored as running without a task, a character target found by the wrong handle, and loaded orders written again.
    /// </summary>
    [Fact]
    public void A_queued_move_rest_and_first_aid_survive_a_save_and_a_load_and_are_started_from_the_front()
    {
        using var a = NewGame();
        var (medic, patient) = (a.Squad.Members[0], a.Squad.Members[1]);
        long t = a.World.Tick;
        a.World.Commands.Enqueue(new MoveOrder([medic], First) { Tick = t });
        a.World.Commands.Enqueue(new MoveOrder([medic], Second) { Tick = t, Queued = true });
        a.World.Commands.Enqueue(new RestOrder([medic]) { Tick = t, Queued = true });
        a.World.Commands.Enqueue(new TreatOrder([medic], patient) { Tick = t, Queued = true });
        a.World.RunTicks(3);

        Assert.Equal([TaskType.MoveCusOrdered, TaskType.MoveCusOrdered, TaskType.Rest, TaskType.FirstAidOrder], Rows(a.World, medic));

        var first = SaveCapture.Capture(a.World, a.Data, new SaveClock(1, 12, 0));
        string folder = Path.Combine(Temp("orders"), QuickSave.Name);
        try
        {
            first.Write(folder);
            var save = SaveGame.Load(folder);
            AssertTheSaveHoldsTheQueue(save);

            using var b = NewGame(start: false);
            var loaded = SaveLoader.Load(b.World, save, b.Data);
            var squad = b.World.Squads.Find(b.World.Player.Squad)!;
            var medicB = squad.Members[0];
            var cold = b.World.Characters.Cold(medicB.Slot)!;

            // Read, not started: the queue is made, and its front started, by the decision step.
            Assert.Equal(4, cold.SavedOrders!.Count);
            Assert.Empty(cold.OrderQueue);

            b.World.RunTicks(2);

            Assert.Null(cold.SavedOrders);
            Assert.Empty(cold.OrderQueue);
            Assert.Equal([TaskType.MoveCusOrdered, TaskType.MoveCusOrdered, TaskType.Rest, TaskType.JobMedic], SecondRows(b.World, medicB));
            Assert.Equal([true, false, false, false], cold.SecondList.Select(e => e.Started));
            Assert.Equal([First, Second], cold.SecondList.Take(2).Select(e => e.Point));
            Assert.Equal(squad.Members[1], cold.SecondList[3].Target);
            Assert.Equal(CharacterTask.GoTo, (CharacterTask)b.World.Characters.Next[medicB.Slot].Task);

            // Written again: the deque alone, which the load left empty.
            var second = SaveCapture.Capture(b.World, b.Data, loaded.Clock, loaded);

            Assert.Empty(OrderKeys(AiOf(second, 0)));

            // A load that is captured before any tick writes none of the orders it read either.
            using var c = NewGame(start: false);
            var loadedC = SaveLoader.Load(c.World, save, c.Data);

            Assert.Empty(OrderKeys(AiOf(SaveCapture.Capture(c.World, c.Data, loadedC.Clock, loadedC), 0)));

            // The patient is not in the world any more: the Medic entry stays all the same, as its saved hand only feeds its finder.
            var other = SaveGame.Load(folder);
            Hand.Null.Write(AiOf(other, 0), "tjobT3");
            AiOf(other, 0).Ints["tjobT3I"] = 999;
            AiOf(other, 0).Ints["tjobT3TYPE"] = (int)FcsRecordType.Character;
            using var d = NewGame(start: false);
            SaveLoader.Load(d.World, other, d.Data);
            d.World.RunTicks(2);

            Assert.Equal([TaskType.MoveCusOrdered, TaskType.MoveCusOrdered, TaskType.Rest, TaskType.JobMedic],
                    SecondRows(d.World, d.World.Squads.Find(d.World.Player.Squad)!.Members[0]));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(folder)!, recursive: true);
        }
    }

    /// <summary>
    /// The medic's queue in the save: its four jobs, the two points and the patient's handle as the last one's target; the patient holds no
    /// order.
    /// </summary>
    static void AssertTheSaveHoldsTheQueue(SaveGame save)
    {
        var ai = AiOf(save, 0);

        Assert.Equal([(int)TaskType.MoveCusOrdered, (int)TaskType.MoveCusOrdered, (int)TaskType.Rest, (int)TaskType.FirstAidOrder],
                Enumerable.Range(0, 4).Select(k => ai.Ints[$"tjob{k}"]));
        Assert.False(ai.Ints.ContainsKey("tjob4"));
        Assert.Equal(First, ai.Vector3S["tjobP0"]);
        Assert.Equal(Second, ai.Vector3S["tjobP1"]);

        var patientHand = save.Platoons.Single(p => p.FactionId == save.PlayerFaction!.Id).Characters[1].Handle;

        Assert.Equal(patientHand, Hand.Read(ai, "tjobT3"));
        Assert.True(Hand.Read(ai, "tjobT0").IsNull);
        Assert.DoesNotContain(AiOf(save, 1).Ints.Keys, k => k.StartsWith("tjob", StringComparison.Ordinal));   // the patient holds no order
    }

    /// <summary>
    /// A loaded order takes its row's job remap (the loader's add <c>FUN_1405086d0</c>, TaskData <c>+4</c> / <c>+0xc</c>, commit <c>43d188020</c>): a
    /// queued 60 First aid robot loads as 57 Robotics and a 26 Loot on a container (a building handle) as 238 Foraging animals. Both are finder rows,
    /// so each entry stays in the second list though its hand names nothing the save holds (a gone character, a building no record is). Catches a
    /// load that keeps the raw rows (60 was dropped with no owner, a 26 on a container loaded as the container order) or drops a finder row's entry
    /// over its hand.
    /// </summary>
    [Fact]
    public void A_saved_60_loads_as_57_and_a_26_on_a_container_as_238_and_both_stay_when_their_hands_name_nothing()
    {
        using var a = NewGame();
        var medic = a.Squad.Members[0];
        long t = a.World.Tick;
        a.World.Commands.Enqueue(new MoveOrder([medic], First) { Tick = t });
        a.World.Commands.Enqueue(new MoveOrder([medic], Second) { Tick = t, Queued = true });
        a.World.RunTicks(3);
        var save = SaveCapture.Capture(a.World, a.Data, new SaveClock(1, 12, 0));

        // The two saved moves become a 60 on a character and a 26 on a building, neither of which the save holds.
        var ai = AiOf(save, 0);
        ai.Ints["tjob0"] = (int)TaskType.FirstAidRobot;
        new Hand((int)FcsRecordType.Character, 0, 0, 999, 0).Write(ai, "tjobT0");
        ai.Ints["tjob1"] = (int)TaskType.LootTarget;
        new Hand((int)FcsRecordType.Building, 0, 0, 999, 0).Write(ai, "tjobT1");

        using var b = NewGame(start: false, extra: StandardParts.Carry);
        SaveLoader.Load(b.World, save, b.Data);
        b.World.RunTicks(2);

        Assert.Equal([TaskType.JobRepairRobot, TaskType.LootAnimalsJob], SecondRows(b.World, b.World.Squads.Find(b.World.Player.Squad)!.Members[0]));
    }

    /// <summary>
    /// A Talk order (12, commit <c>9991b0e0f</c>) given over a queued move replaces it, is saved as <c>tjob0</c> on the NPC's handle, and after a load
    /// waits in the second list until that NPC's platoon is in the world, then is made on it: the loader's add makes the entry whatever its hand
    /// names (<c>FUN_1405086d0</c>). Catches a Talk that does not clear the queue, and a loaded order dropped because its target's platoon is made after
    /// the first look.
    /// </summary>
    [Fact]
    public void A_talk_order_replaces_a_queued_move_and_survives_a_save_and_a_load()
    {
        using var a = NewGame(extra: StandardParts.Dialogue);
        for (int i = 0; i < 600 && NpcLeader(a) is null; i++) a.World.RunTick();
        var hero = a.Squad.Members[0];
        var npc = NpcLeader(a)!.Value;
        long t = a.World.Tick;
        a.World.Commands.Enqueue(new MoveOrder([hero], First) { Tick = t });
        a.World.Commands.Enqueue(new MoveOrder([hero], Second) { Tick = t, Queued = true });
        a.World.RunTicks(2);
        a.World.Commands.Enqueue(new TalkOrder(hero, npc) { Tick = a.World.Tick });
        a.World.RunTicks(2);

        Assert.Equal([TaskType.PlayerTalkTo], Rows(a.World, hero));

        var save = SaveCapture.Capture(a.World, a.Data, new SaveClock(1, 12, 0));
        var ai = AiOf(save, 0);

        Assert.Equal((int)TaskType.PlayerTalkTo, ai.Ints["tjob0"]);
        Assert.False(ai.Ints.ContainsKey("tjob1"));
        Assert.Equal((int)FcsRecordType.Character, Hand.Read(ai, "tjobT0").Type);

        using var b = NewGame(start: false, extra: StandardParts.Dialogue);
        SaveLoader.Load(b.World, save, b.Data);
        var heroB = b.World.Squads.Find(b.World.Player.Squad)!.Members[0];
        var cold = b.World.Characters.Cold(heroB.Slot)!;
        b.World.RunTicks(2);

        // The NPC's platoon is not made yet: the order waits in the second list as no candidate (FUN_14050df10 scores each entry alone).
        Assert.NotNull(Assert.Single(cold.SecondList).Awaits);
        Assert.Null(cold.SavedOrders);

        for (int i = 0; i < 600 && cold.SecondList[0].Awaits is not null; i++) b.World.RunTick();
        var entry = Assert.Single(cold.SecondList);
        var target = b.World.Characters.Cold(entry.Target.Slot);

        Assert.Equal(TaskType.PlayerTalkTo, entry.Row);
        Assert.Equal(Hand.Read(ai, "tjobT0"), target?.Save?.Source.Handle);
        Assert.Null(cold.SavedOrders);
    }

    /// <summary>
    /// The leader of an NPC squad in the world; null while there is none.
    /// </summary>
    static CharacterId? NpcLeader(Game g) =>
        g.World.Squads.All.FirstOrDefault(s => s.Id != g.World.Player.Squad && !s.Leader.IsNone && g.World.Characters.IsAlive(s.Leader))?.Leader;

    /// <summary>
    /// A saved Sleep order is 258 <c>USE_BED_ORDER</c> on its bed, which has no job form: it loads as a second-list entry, Meitou's sleep order 98
    /// (commit <c>9672b163c</c>, docs/game/ai-tasks.md "Precedence", loaded entries). With its bed taken, 258's list B state 73
    /// <c>machineHasFreeOperatorSlot</c> (<c>FUN_140598de0</c>) fails, so the entry waits unstarted and is not spent; once the bed is free the character
    /// goes to it and lies down (commit <c>28d6cbdbc</c>). Catches a loaded 258 dropped or remapped, and a start on a taken bed that stands and so
    /// spends the entry.
    /// </summary>
    [Fact]
    public void A_saved_sleep_order_loads_into_the_second_list_and_waits_for_its_taken_bed()
    {
        using var rig = new TaskStartTests.Rig(carrying: false, decide: true, ai: true);
        rig.World.RunTicks(3);
        rig.Say(new StopCommand([rig.Fighter]) { Tick = rig.World.Tick });
        rig.World.RunTick();
        var mate = rig.Cold(rig.Mate);

        // The mate takes the bed on a Sleep order of its own.
        rig.Say(new UseBedOrder([rig.Mate], rig.BedFixture) { Tick = rig.World.Tick });
        for (int i = 0; i < 1200 && mate.InSomething != HeldState.Bed; i++) rig.World.RunTick();

        Assert.Equal(HeldState.Bed, mate.InSomething);

        // The loader's read of tjob0 258 with its point and Meitou's site key (OrderSave.Read).
        var cold = rig.Cold(rig.Fighter);
        var bed = rig.World.Fixtures.Find(rig.BedFixture)!;
        cold.SavedOrders = [new SavedOrder(TaskType.UseBedOrder, bed.Position, Hand.Null, rig.BedFixture, null, 0, false)];
        rig.World.RunTicks(300);
        var entry = Assert.Single(cold.SecondList);

        Assert.Equal(TaskType.UseBed, entry.Row);
        Assert.Equal(rig.BedFixture, entry.Site);
        Assert.False(entry.Started);
        Assert.False(entry.Spent);
        Assert.Equal(HeldState.None, cold.InSomething);

        // The mate gets up: the bed is free.
        rig.Say(new StopCommand([rig.Mate]) { Tick = rig.World.Tick });

        for (int i = 0; i < 1200 && cold.InSomething != HeldState.Bed; i++) rig.World.RunTick();

        Assert.Equal((HeldState.Bed, rig.BedFixture), (cold.InSomething, cold.InWhat));
    }

    /// <summary>
    /// The queued orders of a save the original wrote (the newest ones in the game's save folders that hold a <c>tjob</c> key in a platoon file) are
    /// read: the row, the point and the null or character handle as the original wrote them. Skips without the install or such a save. Catches a
    /// reader that misses the keys' spelling or type.
    /// </summary>
    [Fact, Slow]
    public void The_queued_orders_of_an_original_save_are_read()
    {
        Assert.SkipWhen(InstallData.Install is null, "Kenshi install not found");

        string[] roots = [InstallData.Install!.SaveDirectory, .. InstallData.Install.UserSaveDirectories];
        var marker = "tjobP"u8.ToArray();
        var found = roots.Where(Directory.Exists).SelectMany(Directory.EnumerateDirectories).OrderByDescending(Directory.GetLastWriteTimeUtc).Take(40)
            .FirstOrDefault(dir => Directory.Exists(Path.Combine(dir, "platoon")) && Directory.EnumerateFiles(Path.Combine(dir,
                    "platoon")).Any(f => File.ReadAllBytes(f).AsSpan().IndexOf(marker) >= 0));

        Assert.SkipWhen(found is null, "no save with queued orders");

        var save = SaveGame.Load(found!);
        var handles = save.BuildHandles();
        var read = 0;
        foreach (var c in save.Platoons.SelectMany(p => p.Characters))
        {
            if (c.Ai is not { } ai || !ai.Ints.ContainsKey("tjob0")) continue;
            var cold = new CharacterCold { IsPlayer = true };
            OrderSave.Read(ai, cold, handles, new Dictionary<FcsRecord, string>());
            var orders = cold.SavedOrders!;

            Assert.NotEmpty(orders);

            for (int i = 0; i < orders.Count; i++)
            {
                Assert.Equal((TaskType)ai.Ints[$"tjob{i}"], orders[i].Row);
                Assert.Equal(ai.Vector3S[$"tjobP{i}"], orders[i].Point);
                Assert.Equal(Hand.Read(ai, $"tjobT{i}"), orders[i].Hand);
            }
            read++;
        }

        Assert.True(read > 0);
    }
}
