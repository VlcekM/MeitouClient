using System.Collections.Concurrent;
using Meitou.Simulation.Combat;
using Meitou.Tests.Combat;
using static Meitou.Tests.Combat.CombatFixtures;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Modding;

/// <summary>
/// The code mods' combat speed hook (<see cref="ICombatSpeedFilter"/>, docs/modding.md "Combat speed").
/// </summary>
public class CombatSpeedFilterTests
{
    /// <summary>
    /// A filter that records every ask and answers with <paramref name="answer"/>.
    /// </summary>
    sealed class Filter(Func<CombatSpeedAsk, float> answer) : ICombatSpeedFilter
    {
        /// <summary>
        /// Every ask, in the order the threads made them.
        /// </summary>
        public readonly ConcurrentQueue<CombatSpeedAsk> Asks = [];

        /// <summary>
        /// Records the ask and answers it.
        /// </summary>
        public float Rate(SimWorld world, in CombatSpeedAsk ask)
        {
            Asks.Enqueue(ask);
            return answer(ask);
        }
    }

    /// <summary>
    /// A duel of 600 ticks: its hash, its log and the length in ticks of the first fighter's first swing.
    /// </summary>
    static (ulong Hash, CombatLogEntry[] Log, int Swing) Run(int threads, Filter? filter)
    {
        using var world = DuelFixture.NewWorld(17, threads, out var combat);
        if (filter is not null) world.AddSystem(filter);

        var (a, b) = DuelFixture.Pair(world, new Combatant { Armour = [BodyPlate, Helmet] }, new Combatant { Armour = [BodyPlate, Helmet] });
        int swing = 0;
        for (int tick = 0; tick < 600; tick++)
        {
            world.RunTick();
            if (swing == 0 && combat.StateOf(a) is { AttackTech: >= 0, AttackTicks: > 0 } s) swing = s.AttackTicks;
        }

        return (world.StateHash(), combat.Log.ToArray(), swing);
    }

    /// <summary>
    /// An unchanged answer changes nothing on 1 or 4 threads; a doubled attack rate halves the first swing.
    /// </summary>
    [Fact]
    public void Attack_and_block_rates_are_asked_and_a_faster_answer_makes_swings_quicker()
    {
        var plain = Run(1, null);
        var same = new Filter(ask => ask.Rate);
        var unchanged = Run(1, same);
        var fast = Run(1, new Filter(ask => ask.Kind == CombatSpeedKind.Attack ? ask.Rate * 2 : ask.Rate));

        Assert.Equal(plain.Hash, unchanged.Hash);
        Assert.Equal(plain.Log, unchanged.Log);
        Assert.Contains(same.Asks, ask => ask.Kind == CombatSpeedKind.Attack);
        Assert.Contains(same.Asks, ask => ask.Kind == CombatSpeedKind.Block);
        Assert.All(same.Asks, ask => Assert.InRange(ask.Rate, 0.01f, 4f));
        Assert.True(plain.Swing > 0);
        Assert.InRange(fast.Swing, plain.Swing / 2 - 1, plain.Swing / 2 + 1);
        Assert.Equal(unchanged.Log, Run(4, new Filter(ask => ask.Rate)).Log);
    }

    /// <summary>
    /// A NaN answer keeps the rate, is asked in one tick only and enters the state hash.
    /// </summary>
    [Fact]
    public void An_answer_that_is_not_a_positive_number_keeps_the_rate_drops_the_filter_from_the_next_tick_and_enters_the_hash()
    {
        var plain = Run(1, null);
        var broken = new Filter(_ => float.NaN);
        var faulted = Run(1, broken);

        Assert.Equal(plain.Log, faulted.Log);
        Assert.NotEqual(plain.Hash, faulted.Hash);
        Assert.Single(broken.Asks.Select(ask => ask.Tick).Distinct());
    }
}
