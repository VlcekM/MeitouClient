using Meitou.Simulation;
using Meitou.Simulation.FirstAid;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Player;

/// <summary>
/// The <c>treat</c> mode of <c>meitou-tools probe</c>: a character ordered to treat a patient (itself by default) and, each round, its task, the
/// patient's posture and each leg's flesh, stun, bandage and down test (#898: a crawler treating its legs stood up and the treatment broke off).
/// </summary>
static class TreatProbe
{
    /// <summary>
    /// Orders the first character named like <paramref name="medic"/> to treat the one named like <paramref name="patient"/> (null: itself) and
    /// prints <paramref name="rounds"/> rounds, <paramref name="interval"/> ticks apart.
    /// </summary>
    public static void Run(ProbeWorld probe, string medic, string? patient, int rounds, int interval)
    {
        if (probe.Named(medic) is not [int m, ..] || probe.Named(patient is null or "button" or "job" ? medic : patient) is not [int p, ..])
        {
            Console.WriteLine($"nobody named {medic} or {patient}");
            return;
        }

        var world = probe.World;
        var table = probe.Characters;
        OrderCommand order = patient switch
        {
            "button" => new MedicJobCommand([table.IdOf(m)], Job: false) { Tick = world.Tick },
            "job" => new MedicJobCommand([table.IdOf(m)]) { Tick = world.Tick },
            _ => new TreatOrder([table.IdOf(m)], table.IdOf(p)) { Tick = world.Tick },
        };
        world.Commands.Enqueue(order);
        for (int round = 0; round < rounds; round++)
        {
            probe.Tick(interval);
            PrintRound(probe, m, p);
            probe.PrintMessages();
        }
    }

    /// <summary>
    /// The medic's task and treatment fields, the patient's posture and hold, and its legs.
    /// </summary>
    static void PrintRound(ProbeWorld probe, int m, int p)
    {
        var world = probe.World;
        var table = probe.Characters;
        var medic = table.Cold(m)!;
        var cold = table.Cold(p)!;
        var med = cold.Medical!;
        Console.WriteLine($"tick {world.Tick} task={(CharacterTask)table.Next[m].Task} kneels={medic.TreatKneels} running={medic.Running?.Row} "
            + $"combat={medic.InCombat} crawling={med.Crawling} prone={med.Prone} lowered={med.StayingLow} out={med.Incapacitated} "
            + $"gettingUp={cold.GettingUpUntil - world.Tick} staggered={cold.StaggeredUntil - world.Tick} ko={med.LegKoPoint:F1}");
        foreach (var part in med.Parts)
        {
            if (!part.IsLeg) continue;
            Console.WriteLine($"  {part.Template.Name}: flesh={part.Flesh:F1} stun={part.Stun:F1} bandage={part.Bandage:F1} rig={part.Rig:F1} "
                + $"down={part.LegDown(med.LegKoPoint)}");
        }
    }
}
