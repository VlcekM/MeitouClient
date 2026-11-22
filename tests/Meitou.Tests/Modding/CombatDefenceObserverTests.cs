using Meitou.Simulation.Combat;
using Meitou.Tests.Combat;
using static Meitou.Tests.Combat.CombatFixtures;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Modding;

public class CombatDefenceObserverTests
{
    sealed class Recorder(bool fail = false) : ICombatDefenceObserver
    {
        public readonly List<CombatDefence> Defences = [];

        public void Defended(SimWorld world, in CombatDefence defence)
        {
            Defences.Add(defence);
            if (fail) throw new InvalidOperationException("mod fault");
        }
    }

    static (ulong Hash, CombatLogEntry[] Log, CombatDefence[] Defences) Run(int threads, Recorder? observer)
    {
        using var world = DuelFixture.NewWorld(17, threads, out var combat);
        if (observer is not null) world.AddSystem(observer);

        DuelFixture.Pair(world, new Combatant { Armour = [BodyPlate, Helmet] }, new Combatant());
        world.RunTicks(600);
        return (world.StateHash(), combat.Log.ToArray(), observer?.Defences.ToArray() ?? []);
    }

    [Fact]
    public void Every_blocked_or_dodged_blow_is_reported_once_as_logged_without_changing_the_run()
    {
        var plain = Run(1, null);
        var observed = Run(1, new Recorder());

        Assert.Equal(plain.Hash, observed.Hash);
        Assert.Equal(plain.Log, observed.Log);
        var stopped = observed.Log.Where(e => e.Outcome is BlowOutcome.Blocked or BlowOutcome.Dodged).ToArray();
        Assert.NotEmpty(stopped);
        Assert.Equal(stopped.Length, observed.Defences.Length);
        for (int i = 0; i < stopped.Length; i++)
        {
            var (entry, defence) = (stopped[i], observed.Defences[i]);
            Assert.Equal(entry.Tick, defence.Tick);
            Assert.Equal(entry.Attacker, defence.Attacker.Slot);
            Assert.Equal(entry.Defender, defence.Defender.Slot);
            Assert.Equal(entry.Blow, defence.Blow);
            Assert.Equal(entry.Outcome == BlowOutcome.Blocked ? CombatDefenceKind.Blocked : CombatDefenceKind.Dodged, defence.Kind);
            if (defence.Kind == CombatDefenceKind.Dodged) Assert.False(defence.Parry);
        }

        Assert.Equal(observed.Defences, Run(4, new Recorder()).Defences);
    }

    [Fact]
    public void A_throwing_observer_is_disabled_after_its_first_call_and_the_fault_enters_the_hash()
    {
        var healthy = Run(1, new Recorder());
        var failing = new Recorder(fail: true);
        var faulted = Run(1, failing);

        Assert.True(healthy.Defences.Length > 1);
        Assert.Single(failing.Defences);
        Assert.Equal(healthy.Log, faulted.Log);
        Assert.NotEqual(healthy.Hash, faulted.Hash);
    }
}
