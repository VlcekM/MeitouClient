using System.Numerics;
using Meitou.Simulation;

namespace Meitou.Tests.Simulation;

public class CoreTests
{
    [Fact]
    public void Rolls_are_pure_functions_of_the_key()
    {
        ulong a = Rng.Hash(1, 2, RngPurpose.Wander, 3);
        Assert.Equal(a, Rng.Hash(1, 2, RngPurpose.Wander, 3));
        Assert.NotEqual(a, Rng.Hash(2, 2, RngPurpose.Wander, 3));
        Assert.NotEqual(a, Rng.Hash(1, 3, RngPurpose.Wander, 3));
        Assert.NotEqual(a, Rng.Hash(1, 2, RngPurpose.Combat, 3));
        Assert.NotEqual(a, Rng.Hash(1, 2, RngPurpose.Wander, 4));
        // A reused slot gets other rolls.
        Assert.NotEqual(Rng.Key(new CharacterId(5, 0)), Rng.Key(new CharacterId(5, 1)));
    }

    [Fact]
    public void Floats_and_ints_stay_in_range_and_spread_evenly()
    {
        var buckets = new int[10];
        for (ulong i = 0; i < 20000; i++)
        {
            ulong roll = Rng.Hash(9, 1, RngPurpose.Test, i);
            float f = Rng.Float(roll);
            Assert.InRange(f, 0f, 0.99999994f);
            buckets[Rng.Int(roll, 10)]++;
        }
        Assert.All(buckets, b => Assert.InRange(b, 1700, 2300));
    }

    [Fact]
    public void A_stream_walks_its_counter_and_At_reads_by_index()
    {
        var s = new RandomStream(4, 5, RngPurpose.Think);
        ulong first = s.NextRoll(), second = s.NextRoll();
        Assert.Equal(first, s.At(0));
        Assert.Equal(second, s.At(1));
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void The_hasher_depends_on_values_and_their_order()
    {
        StateHasher Hash(params int[] values)
        {
            var h = new StateHasher();
            foreach (int v in values) h.Add(v);
            return h;
        }
        Assert.Equal(Hash(1, 2).Value, Hash(1, 2).Value);
        Assert.NotEqual(Hash(1, 2).Value, Hash(2, 1).Value);
        Assert.NotEqual(Hash(1, 2).Value, Hash(1, 3).Value);
        var neg = new StateHasher();
        neg.Add(-0f);
        var pos = new StateHasher();
        pos.Add(0f);
        Assert.Equal(pos.Value, neg.Value);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(16)]
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
    public void A_failure_in_a_worker_comes_back_to_the_caller_and_the_pool_survives()
    {
        using var pool = new WorkerPool(4);
        Assert.Throws<InvalidOperationException>(() => pool.ForEach(20, i => { if (i == 7) throw new InvalidOperationException("boom"); }));
        int sum = 0;
        pool.ForEach(10, i => Interlocked.Add(ref sum, i));
        Assert.Equal(45, sum);
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
    public void Next_is_a_copy_of_Previous_until_a_phase_writes_it()
    {
        var t = new CharacterTable();
        var id = t.Spawn(new CharacterHot { Position = new(1, 2, 3) }, new CharacterCold(), 0);
        t.BeginTick();
        t.Next[id.Slot].Position = new(9, 9, 9);
        Assert.Equal(new Vector3(1, 2, 3), t.Previous[id.Slot].Position);
        t.EndTick();
        Assert.Equal(new Vector3(9, 9, 9), t.Previous[id.Slot].Position);
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

    [Fact]
    public void The_grid_finds_neighbours_in_a_fixed_order()
    {
        var state = new CharacterHot[5];
        var positions = new Vector3[] { new(10, 0, 10), new(250, 0, 10), new(20, 0, 20), new(15, 0, 205), new(-30, 0, -30) };
        for (int i = 0; i < state.Length; i++) state[i] = new CharacterHot { Alive = i != 3, Position = positions[i] };
        var grid = new SpatialGrid(100);
        grid.Build(state);
        var found = new List<int>();
        grid.Query(10, 10, 50, ref found, static (int slot, ref List<int> list) => list.Add(slot));
        // Cells (-1,-1), (0,0) are in reach: slot 4 comes with the cell before slot 0 and 2.
        Assert.Equal([4, 0, 2], found);
    }
}
