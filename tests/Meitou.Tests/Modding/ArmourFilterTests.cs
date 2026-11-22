using Meitou.Simulation.Combat;
using Meitou.Tests.Combat;
using static Meitou.Tests.Combat.CombatFixtures;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Modding;

public class ArmourFilterTests
{
    sealed class Hits : ICombatHitObserver
    {
        public readonly List<CombatHit> Landed = [];

        public void HitResolved(SimWorld world, in CombatHit hit) => Landed.Add(hit);
    }

    sealed class Filter(Func<ArmourAsk, ArmourPiece> answer) : IArmourFilter
    {
        public readonly List<ArmourAsk> Asks = [];

        public ArmourPiece Piece(SimWorld world, in ArmourAsk ask)
        {
            Asks.Add(ask);
            return answer(ask);
        }
    }

    static (ulong Hash, CombatHit[] Hits) Run(int threads, Filter? filter)
    {
        using var world = DuelFixture.NewWorld(17, threads, out _);
        var hits = new Hits();
        world.AddSystem(hits);
        if (filter is not null) world.AddSystem(filter);

        DuelFixture.Pair(world, new Combatant { Armour = [BodyPlate, Helmet] }, new Combatant { Armour = [BodyPlate, Helmet] });
        world.RunTicks(600);
        return (world.StateHash(), [.. hits.Landed]);
    }

    static float Stopped(CombatHit[] hits) => hits.Sum(h => h.Absorbed.Cut + h.Absorbed.Blunt + h.Absorbed.Pierce);

    [Fact]
    public void A_filter_sees_every_worn_piece_of_each_hit_and_its_answer_is_the_armour_the_hit_goes_through()
    {
        var plain = Run(1, null);
        var bare = new Filter(ask => ask.Piece with { CutDefence = 0, BluntDefence = 0, PierceDefence = 0 });
        var stripped = Run(1, bare);
        var same = new Filter(ask => ask.Piece);
        var unchanged = Run(1, same);

        Assert.True(Stopped(plain.Hits) > 0);
        Assert.Equal(0, Stopped(stripped.Hits));
        Assert.Equal(plain.Hash, unchanged.Hash);
        Assert.Equal(plain.Hits, unchanged.Hits);
        Assert.NotEmpty(same.Asks);
        Assert.All(same.Asks, ask => Assert.InRange(ask.Index, 0, 1));
        Assert.Equal(2 * plain.Hits.Length, same.Asks.Count);
        Assert.Equal(unchanged.Hits, Run(4, new Filter(ask => ask.Piece)).Hits);
    }

    [Fact]
    public void An_answer_out_of_range_keeps_the_piece_disables_the_filter_and_enters_the_hash()
    {
        var plain = Run(1, null);
        var broken = new Filter(ask => ask.Piece with { CutDefence = 2 });
        var faulted = Run(1, broken);

        Assert.Single(broken.Asks);
        Assert.Equal(plain.Hits, faulted.Hits);
        Assert.NotEqual(plain.Hash, faulted.Hash);
    }
}
