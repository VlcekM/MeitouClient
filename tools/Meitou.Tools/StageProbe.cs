using System.Numerics;
using Meitou.Data.Fcs;
using Meitou.Simulation;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Population;

/// <summary>
/// The <c>stage</c> mode of <c>meitou-tools probe</c>: two squads spawned at a point as Re_Dev's squad spawn does (each a FACTION name and a
/// SQUAD_TEMPLATE name), then each squad's size, knocked out, enslaved, carried and carrying members, its centre and its leader's goal every
/// interval: the same staged fight the harness runs in the original (#904: a slaver caravan and the vagrants it enslaves).
/// </summary>
static class StageProbe
{
    /// <summary>
    /// Spawns <paramref name="first"/> and <paramref name="second"/> (<c>faction:template</c>) within 50 of <paramref name="at"/>, then lists both
    /// squads <paramref name="rounds"/> times, <paramref name="interval"/> ticks apart.
    /// </summary>
    public static void Run(ProbeWorld probe, Vector2 at, string first, string second, int rounds, int interval)
    {
        int a = Spawn(probe, at, first), b = Spawn(probe, at + new Vector2(150, 0), second);
        if (a < 0 || b < 0) return;

        for (int round = 0; round < rounds; round++)
        {
            probe.Tick(interval);
            Console.WriteLine($"tick {probe.World.Tick} hours {probe.World.GameHours:F2}");
            Print(probe, a);
            Print(probe, b);
            PrintEvents(probe);
        }
    }

    /// <summary>
    /// The event log's fight starts and relation changes since the last call (a downing blow's change names its striker, whose goal the fight line
    /// gives: the striker's rating, <c>FactionDeeds.Rating</c>).
    /// </summary>
    static void PrintEvents(ProbeWorld probe)
    {
        foreach (string line in probe.World.Events.Take())
        {
            if (line.StartsWith("relation", StringComparison.Ordinal) || line.StartsWith("fight", StringComparison.Ordinal))
                Console.WriteLine("  " + line);
        }
    }

    /// <summary>
    /// Spawns one <c>faction:template</c> platoon; its id, or -1 (said) for an unknown name or a failed spawn.
    /// </summary>
    static int Spawn(ProbeWorld probe, Vector2 at, string spec)
    {
        var parts = spec.Split(':', 2);
        int faction = Index(probe, parts[0]);
        var template = probe.Db.OfType(FcsRecordType.SquadTemplate)
            .FirstOrDefault(r => string.Equals(r.Name, parts[^1], StringComparison.OrdinalIgnoreCase));
        var population = probe.World.SystemOf<PopulationSystem>();
        int id = faction >= 0 && template is not null && population is not null
            ? population.SpawnPlatoon(probe.World, faction, template.StringId, 0, at, 20, 150)
            : -1;

        Console.WriteLine($"spawn {spec}: platoon {id} (faction {faction}, template {template?.StringId ?? "none"})");
        return id;
    }

    /// <summary>
    /// The faction named <paramref name="name"/>, or -1.
    /// </summary>
    static int Index(ProbeWorld probe, string name)
    {
        for (int f = 0; f < probe.Data.Factions.Count; f++)
            if (string.Equals(probe.Data.Factions[f].Name, name, StringComparison.OrdinalIgnoreCase)) return f;

        return -1;
    }

    /// <summary>
    /// One platoon's squad: members, knocked out, in slave state 1, carried, carrying, the centre, the leader's goal, the running package and the
    /// town the squad is in.
    /// </summary>
    static void Print(ProbeWorld probe, int platoon)
    {
        if (probe.World.Platoons.Find(platoon) is not { SquadId: >= 0 } p || probe.World.Squads.Find(p.SquadId) is not { } squad)
        {
            Console.WriteLine($"  platoon {platoon} not loaded");
            return;
        }

        var table = probe.Characters;
        int n = 0, ko = 0, slaves = 0, carried = 0, carrying = 0;
        var centre = Vector3.Zero;
        foreach (var m in squad.Members)
        {
            if (!table.TryResolveNext(m, out int i) || table.Cold(i) is not { } c) continue;

            n++;
            centre += table.Next[i].Position;
            if (c.Medical is { Incapacitated: true }) ko++;
            if (c.SlaveState == 1) slaves++;
            if (!c.CarriedBy.IsNone) carried++;
            if (!c.Carrying.IsNone) carrying++;
        }

        var goal = table.TryResolveNext(squad.Leader, out int lead) ? table.Cold(lead)?.Ai?.Goal?.Task.Type.ToString() : "-";
        var mid = n > 0 ? centre / n : centre;
        Console.WriteLine($"  {probe.Faction(squad.Faction)} sq={squad.Id} n={n} ko={ko} slaves={slaves} carried={carried} carrying={carrying} "
            + $"at <{mid.X:F0}, {mid.Z:F0}> lead={goal} package={squad.Ai?.Current?.Name} town={SaveProbe.TownLabel(squad.Ai?.InTown)}");
    }
}
