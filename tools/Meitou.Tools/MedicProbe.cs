using System.Numerics;
using Meitou.Simulation;
using Meitou.Simulation.Aggro;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Combat;
using Meitou.Simulation.FirstAid;
using Meitou.Simulation.Perception;

/// <summary>
/// The <c>medic</c> mode of <c>meitou-tools probe</c>: a medic's treatment and decision over time, with each downed character in its sight range
/// and how its senses and the sight test hold it (#843: a medic on Heal All standing aimless after a treatment).
/// </summary>
static class MedicProbe
{
    /// <summary>
    /// Prints <paramref name="rounds"/> rounds, <paramref name="interval"/> ticks apart, of the first character named like <paramref name="name"/>.
    /// </summary>
    public static void Run(ProbeWorld probe, string name, int rounds, int interval)
    {
        if (probe.Named(name) is not [int medic, ..])
        {
            Console.WriteLine($"nobody named {name}");
            return;
        }

        probe.World.Decisions.Tracing = true;
        for (int round = 0; round < rounds; round++)
        {
            PrintMedic(probe, medic);
            PrintPatients(probe, medic);
            probe.PrintMessages();
            probe.Tick(interval);
        }
    }

    /// <summary>
    /// The medic's task, patient and last decision.
    /// </summary>
    static void PrintMedic(ProbeWorld probe, int medic)
    {
        var c = probe.Characters.Cold(medic)!;
        var target = probe.Characters.TryResolveNext(c.TreatTarget, out int t) ? $"{t} '{probe.Characters.Cold(t)!.Name}'" : "-";
        Console.WriteLine($"tick {probe.World.Tick} {probe.Who(medic)} treat={target} kneels={c.TreatKneels} running={c.Running?.Source}");
        if (c.Decision is { } d) Console.WriteLine($"  decision {d.Tick} {d.Winner}: {string.Join("; ", d.Lines)}");
    }

    /// <summary>
    /// The downed characters within the medic's sight range, one line each (<see cref="PatientLine"/>).
    /// </summary>
    static void PrintPatients(ProbeWorld probe, int medic)
    {
        var table = probe.Characters;
        var cold = table.Cold(medic)!;
        float range = AggroSystem.RangeOf(cold, AggroSystem.OutdoorSight);
        var senses = CharacterFacts.Senses(probe.World, medic);
        Console.WriteLine($"  senses entries={senses?.Entries.Count} faded={senses?.Faded}");
        for (int s = 0; s < table.HighWater; s++)
        {
            if (s == medic || !table.Next[s].Alive || table.Cold(s) is not { Medical: { Incapacitated: true, Dead: false } }) continue;
            if (Vector3.Distance(table.Next[medic].Position, table.Next[s].Position) > range) continue;
            Console.WriteLine(PatientLine(probe, medic, s, range, senses));
        }
    }

    /// <summary>
    /// One downed character: distance, need, the finder's seen-or-heard test and the sense entry behind it, the give-up list, the place, the sight
    /// test's value and the sight ray.
    /// </summary>
    static string PatientLine(ProbeWorld probe, int medic, int s, float range, SenseBlock? senses)
    {
        var world = probe.World;
        var table = probe.Characters;
        var other = table.Cold(s)!;
        var (eye, there) = (table.Previous[medic].Position, table.Previous[s].Position);
        float d = Vector3.Distance(table.Next[medic].Position, table.Next[s].Position);
        string entry = senses is null ? "no senses" : senses.TryGet(table.IdOf(s), out var e)
            ? $"seen={e.Seen} heard={e.Heard} age={world.Tick - e.Tick} tags={e.Tags}"
            : "no entry";
        bool ignored = PursuitSystem.PathImpossible(table.Cold(medic)!, table.IdOf(s), world.Tick);
        bool sameLocation = world.Sight.LocationOf(medic) == world.Sight.LocationOf(s);
        float sees = world.Sight.Sees(medic, s, eye, there, range);
        bool ray = SightRay.Clear(world.Walkability, world.SystemOf<CombatSystem>()?.Buildings, eye, there);
        return $"    {s} '{other.Name}' {probe.Faction(other.Faction)} d={d:F0} carried={!other.CarriedBy.IsNone} "
            + $"needs={FirstAidSystem.NeedsFirstAid(world, other)} perceives={CharacterFacts.Perceives(world, medic, s, now: true)} {entry} "
            + $"ignored={ignored} sameLoc={sameLocation} sees={sees:F2} ray={ray}";
    }
}
