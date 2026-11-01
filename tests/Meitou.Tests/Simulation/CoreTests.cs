using System.Numerics;
using Meitou.Simulation;

namespace Meitou.Tests.Simulation;

public class CoreTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void The_pool_runs_every_index_exactly_once(int threads)
    {
        using var pool = new WorkerPool(threads);
        for (int round = 0; round < 50; round++)
        {
            var hits = new int[100 + round];
            pool.ForEach(hits.Length, i => Interlocked.Increment(ref hits[i]));
            Assert.All(hits, h => Assert.Equal(1, h));
        }
        pool.ForEach(0, _ => throw new InvalidOperationException());
    }

    [Fact]
    public void Slots_are_reused_most_recent_first_with_a_higher_generation()
    {
        var t = new CharacterTable(2);
        var a = t.Spawn(new CharacterHot(), new CharacterCold(), 0);
        var b = t.Spawn(new CharacterHot(), new CharacterCold(), 0);
        var c = t.Spawn(new CharacterHot(), new CharacterCold(), 0);   // grows past the first capacity
        Assert.Equal([0, 1, 2], new[] { a.Slot, b.Slot, c.Slot });
        Assert.Equal(3, t.Count);
        Assert.True(t.Remove(a));
        Assert.True(t.Remove(c));
        Assert.False(t.Remove(a));
        Assert.False(t.IsAlive(a));
        var d = t.Spawn(new CharacterHot(), new CharacterCold(), 0);
        Assert.Equal(2, d.Slot);
        Assert.Equal(1, d.Generation);
        var e = t.Spawn(new CharacterHot(), new CharacterCold(), 0);
        Assert.Equal(0, e.Slot);
        Assert.Equal(1, e.Generation);
        // The old id names nothing, though its slot is alive again.
        Assert.False(t.IsAlive(a));
        Assert.True(t.IsAlive(e));
        Assert.Equal(3, t.HighWater);
    }

    [Fact]
    public void Effects_are_ordered_by_target_source_system_and_sequence_whatever_the_buffers()
    {
        // The same effects split over buffers in two different ways sort the same.
        var all = new List<(int Source, int Target)> { (5, 1), (2, 1), (2, 1), (9, 0), (3, 4) };
        List<Effect> Collect(int[] cut)
        {
            var buffers = new List<EffectBuffer>();
            int from = 0;
            foreach (int to in cut.Append(all.Count))
            {
                var b = new EffectBuffer();
                b.BeginSystem(0);
                for (int i = from; i < to; i++) b.Emit(all[i].Source, all[i].Target, kind: i);
                buffers.Add(b);
                from = to;
            }
            var list = buffers.SelectMany(b => b.Items).ToList();
            list.Sort(EffectBuffer.Order);
            return list;
        }
        // Sources emit together, so the cuts fall between sources: (5,1) | (2,1),(2,1) | rest, or all in one.
        var a = Collect([1, 3]);
        var b = Collect([]);
        Assert.Equal(a.Select(e => (e.Target, e.Source, e.Sequence)), b.Select(e => (e.Target, e.Source, e.Sequence)));
        Assert.Equal([(0, 9, 0), (1, 2, 0), (1, 2, 1), (1, 5, 0), (4, 3, 0)], a.Select(e => (e.Target, e.Source, e.Sequence)));
    }

    /// <summary>
    /// Character snapshots are reused, a set of three in turn (docs/simulation.md "Snapshots"). A slot's object that held a selected, knocked-out player character and is
    /// refilled for a plain NPC matches the same NPC's in a world where the slot held a plain one; a paused publish leaves the previous snapshot's characters alone.
    /// Catches a property the fill forgets to write, and the ring writing into the shown pair.
    /// </summary>
    [Fact]
    public void A_reused_character_snapshot_carries_nothing_over_and_a_paused_publish_keeps_the_previous()
    {
        (CharacterSnapshot Npc, Meitou.Simulation.World World) Npc(bool heavy)
        {
            var world = new Meitou.Simulation.World(new WorldSettings { Seed = 3 }, new OpenGroundWalkability(SyntheticTown.Ground));
            CharacterId Add(Vector3 at, Action<CharacterCold> shape)
            {
                var cold = new Meitou.Tests.Combat.Combatant().MakeCold();
                shape(cold);
                return world.Characters.Spawn(new CharacterHot { Position = at }, cold, 0);
            }
            world.Player.Faction = 1;
            Add(new(100, 300, 100), c => (c.Faction, c.IsPlayer, c.Name) = (1, true, "own"));
            var first = Add(new(140, 300, 100), c =>
            {
                if (!heavy) return;
                (c.Faction, c.IsPlayer, c.Name, c.StuckBolts) = (1, true, "gone", [default]);
                c.Medical!.KnockOut(1000);
            });
            world.Player.Selection.Add(first);
            world.RunTicks(3);   // every buffer of the set holds the first one
            world.Characters.Remove(first);
            var npc = Add(new(180, 300, 100), c => (c.Faction, c.Name) = (2, "npc"));
            Assert.Equal(first.Slot, npc.Slot);
            world.RunTicks(3);
            return (world.Snapshot.Characters.Single(c => c.Id == npc), world);
        }
        var (reused, a) = Npc(heavy: true);
        using var worldA = a;
        var (expected, b) = Npc(heavy: false);
        using var worldB = b;
        Assert.Equivalent(expected, reused, strict: true);

        // Paused publishes: the previous snapshot keeps its characters while the current one moves on.
        var previous = a.PreviousSnapshot;
        var own = previous.Characters.Single(x => x.Name == "own");
        a.Characters.Cold(own.Id.Slot)!.Name = "renamed";
        for (int k = 0; k < 2; k++)
        {
            a.Commands.Enqueue(new InspectCommand(CharacterId.None) { Tick = a.Tick });   // a paused publish needs a command due
            a.ApplyPausedInterfaceCommands();
        }
        Assert.Same(previous, a.PreviousSnapshot);
        Assert.Equal("own", own.Name);
        Assert.Equal("renamed", a.Snapshot.Characters.Single(x => x.Id == own.Id).Name);
    }
}
