using System.Numerics;
using Meitou.Data;
using Meitou.Data.Gameplay;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay.Ai;
using Meitou.Data.Save;
using Meitou.Simulation;
using Meitou.Simulation.Factions;
using Meitou.Simulation.FirstAid;
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

    static Game NewGame(bool start = true)
    {
        var db = SyntheticTown.Database(anatomy: true);
        var data = SyntheticTown.Data(db);
        var walk = new OpenGroundWalkability(SyntheticTown.Ground);
        var systems = SyntheticTown.Standard(data, walk, Parts, new PopulationSettings { CheckEveryTicks = 1 });
        var world = new SimWorld(new WorldSettings { Seed = 11, PublishSnapshots = false }, walk, systems.Systems);
        var squad = start ? systems.Population!.StartPlayer(world, NewGameStart.From(db.Find("40-t")!)) : null!;
        return new Game(world, data, squad);
    }

    static TaskType[] Rows(SimWorld world, CharacterId id) => [.. world.Characters.Cold(id.Slot)!.OrderQueue.Select(e => e.Row)];

    static FcsRecord AiOf(SaveGame save, int member) => save.Platoons.Single(p => p.FactionId == save.PlayerFaction!.Id).Characters[member].Ai!;

    static IEnumerable<string> OrderKeys(FcsRecord ai) =>
        ai.Ints.Where(k => k.Key.StartsWith("tjob", StringComparison.Ordinal) || k.Key.StartsWith("meitou tjob",
                StringComparison.Ordinal)).Select(k => $"{k.Key}={k.Value}")
            .Concat(ai.Vector3S.Where(k => k.Key.StartsWith("tjob", StringComparison.Ordinal)).Select(k => $"{k.Key}={k.Value}"))
            .Concat(ai.Strings.Where(k => k.Key.StartsWith("meitou tjob",
                    StringComparison.Ordinal)).Select(k => $"{k.Key}={k.Value}")).Order(StringComparer.Ordinal);

    /// <summary>
    /// A Shift-queued move, Rest and First Aid are written as the original's <c>tjob&lt;i&gt;</c> / <c>tjobP&lt;i&gt;</c> / <c>tjobT&lt;i&gt;</c>
    /// (front first, the started move too), read back as an unstarted queue the decision step starts through the normal path, and written again
    /// unchanged; a target that is no longer in the world drops its order. Catches orders lost by a save, a front restored as running without a task,
    /// a character target found by the wrong handle, and a queue that does not round-trip.
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
            var (ai, patientHand) = AssertTheSaveHoldsTheQueue(save);

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
            Assert.Equal([TaskType.MoveCusOrdered, TaskType.MoveCusOrdered, TaskType.Rest, TaskType.FirstAidOrder], Rows(b.World, medicB));
            Assert.Equal([true, false, false, false], cold.OrderQueue.Select(e => e.Started));
            Assert.Equal([First, Second], cold.OrderQueue.Take(2).Select(e => e.Point));
            Assert.Equal(squad.Members[1], cold.OrderQueue[3].Target);
            Assert.Equal(CharacterTask.GoTo, (CharacterTask)b.World.Characters.Next[medicB.Slot].Task);

            // Written again: the same order keys.
            var second = SaveCapture.Capture(b.World, b.Data, loaded.Clock, loaded);
            var again = AiOf(second, 0);

            Assert.Equal(OrderKeys(ai), OrderKeys(again));
            Assert.Equal(patientHand, Hand.Read(again, "tjobT3"));

            // A load that is captured before any tick keeps the orders it read.
            using var c = NewGame(start: false);
            var loadedC = SaveLoader.Load(c.World, save, c.Data);

            Assert.Equal(OrderKeys(ai), OrderKeys(AiOf(SaveCapture.Capture(c.World, c.Data, loadedC.Clock, loadedC), 0)));

            // The patient is not in the world any more: its order is left out and the rest stay.
            var other = SaveGame.Load(folder);
            Hand.Null.Write(AiOf(other, 0), "tjobT3");
            AiOf(other, 0).Ints["tjobT3I"] = 999;
            AiOf(other, 0).Ints["tjobT3TYPE"] = (int)FcsRecordType.Character;
            using var d = NewGame(start: false);
            SaveLoader.Load(d.World, other, d.Data);
            d.World.RunTicks(2);

            Assert.Equal([TaskType.MoveCusOrdered, TaskType.MoveCusOrdered, TaskType.Rest],
                    Rows(d.World, d.World.Squads.Find(d.World.Player.Squad)!.Members[0]));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(folder)!, recursive: true);
        }
    }

    /// <summary>
    /// The medic's queue in the save: its four jobs, the two points and the patient's handle as the last one's target; the patient holds no
    /// order. Returns the medic's AI record and the patient's handle.
    /// </summary>
    static (FcsRecord Ai, Hand PatientHand) AssertTheSaveHoldsTheQueue(SaveGame save)
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

        return (ai, patientHand);
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

        string[] roots = [InstallData.Install!.SaveDirectory,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "kenshi", "save")];
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
