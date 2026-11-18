using System.Numerics;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation;
using Meitou.Simulation.Factions;

/// <summary>
/// The <c>turrets</c> mode of <c>meitou-tools probe</c>: hostile characters spawned at a point as the debug menu's "Spawn NPC (hostile)" does, then
/// the squads round it whose package has a turret goal, each member's goal, turret claim and sensed enemies, every interval (#788: town guards who
/// never manned their turrets against spawned cannibals).
/// </summary>
static class TurretProbe
{
    /// <summary>
    /// The turret goal types (149, 222, 234, 235).
    /// </summary>
    static readonly HashSet<TaskType> TurretGoals = [TaskType.ManATurret, TaskType.ManATurretOnBuilding, TaskType.ManATurretPlayerJob, TaskType.UseTurret];

    /// <summary>
    /// Spawns <paramref name="count"/> characters of the CHARACTER named <paramref name="record"/> at <paramref name="at"/>, then lists the turret
    /// squads within 3000 of it <paramref name="rounds"/> times, <paramref name="interval"/> ticks apart.
    /// </summary>
    public static void Run(ProbeWorld probe, string record, int count, Vector2 at, int rounds, int interval)
    {
        if (!Spawn(probe, record, count, at)) return;

        for (int round = 0; round < rounds; round++)
        {
            probe.Tick(interval);
            Console.WriteLine($"tick {probe.World.Tick}");
            foreach (var squad in probe.World.Squads.All)
                if (IsTurretSquad(probe, squad, at)) PrintSquad(probe, squad);

            foreach (int i in probe.Near(new Vector3(at.X, probe.LeaderPosition.Y, at.Y), 1500))
                if (probe.Characters.Cold(i) is { Name: var name } c && name.Equals(record, StringComparison.OrdinalIgnoreCase))
                    Console.WriteLine($"  spawn {probe.Who(i)} goal={c.Ai?.Goal?.Task.Type} fight={c.Ai?.FightGoal} down={c.Medical?.Incapacitated}");

            probe.PrintMessages();
        }
    }

    /// <summary>
    /// Queues the spawns; false (said) when no CHARACTER has that name.
    /// </summary>
    static bool Spawn(ProbeWorld probe, string record, int count, Vector2 at)
    {
        var npc = probe.Db.OfType(FcsRecordType.Character).FirstOrDefault(r => string.Equals(r.Name, record, StringComparison.OrdinalIgnoreCase));
        if (npc is null)
        {
            Console.WriteLine($"no CHARACTER named '{record}'");
            return false;
        }

        for (int k = 0; k < count; k++)
        {
            var spot = at + new Vector2(k % 5 * 15, k / 5 * 15);
            probe.World.Commands.Enqueue(new DebugSpawn(npc.StringId, spot, DebugSpawn.HostileFaction) { Tick = probe.World.Tick, Aggro = true });
        }

        return true;
    }

    /// <summary>
    /// Whether the squad's leader is within 3000 of <paramref name="at"/> and its current package lists a turret goal.
    /// </summary>
    static bool IsTurretSquad(ProbeWorld probe, Squad squad, Vector2 at)
    {
        if (!probe.Characters.TryResolveNext(squad.Leader, out int lead) || squad.Ai?.Current is not { } package) return false;

        var p = probe.Characters.Next[lead].Position;
        if (Vector2.Distance(new Vector2(p.X, p.Z), at) > 3000) return false;

        return package.Roles.Any(role => role.Any(g => TurretGoals.Contains(g.Task.Type)));
    }

    /// <summary>
    /// The squad's package and turret count, then each member's goal, claimed turret, factor, sensed enemies, gunned turret and distance to its
    /// claimed turret.
    /// </summary>
    static void PrintSquad(ProbeWorld probe, Squad squad)
    {
        var board = squad.Ai!;
        var t = probe.Characters;
        Console.WriteLine($"  sq {squad.Id} '{squad.Name}' package={board.Current?.Name} town={board.Town} turrets={board.Turrets.Count} "
            + $"claims={board.TurretClaims.Count}");
        foreach (var m in squad.Members)
        {
            if (!t.TryResolveNext(m, out int s)) continue;

            var c = t.Cold(s)!;
            string claim = board.TurretClaims.TryGetValue(s, out int k) && board.Turrets[k].Turret is { } turret
                ? $"{board.Turrets[k].Key}@{Vector3.Distance(turret.Position, t.Next[s].Position):F0} f={board.TurretFactors.GetValueOrDefault(s):F2} "
                    + $"turret={turret.Position:F0} spot={Meitou.Simulation.Ai.AiSystem.TurretSpot(probe.World, turret):F0} walk-goal={t.Next[s].Goal:F0} "
                    + $"flags={(Meitou.Simulation.Movement.MoveFlags)t.Next[s].Flags} speed={t.Next[s].Velocity.Length():F1} path={PathText(c.Path, t.Next[s].PathCursor)}"
                : "-";
            int enemies = Meitou.Simulation.Bodies.CharacterFacts.Senses(probe.World, s)?.Enemies ?? -1;
            Console.WriteLine($"    {probe.Who(s)} behaviour={c.Ai?.Behaviour} goal={c.Ai?.Goal?.Task.Type} fight={c.Ai?.FightGoal} "
                + $"combat={c.InCombat} enemies={enemies} slave={c.SlaveState} master={c.SlaveMaster} down={c.Medical?.Incapacitated} "
                + $"claim={claim} gunning={c.Gunning ?? "-"}");
        }
    }

    /// <summary>
    /// A path's point count, cursor and last point.
    /// </summary>
    static string PathText(Vector3[] path, int cursor) =>
        path.Length == 0 ? "none" : $"{cursor}/{path.Length} [{string.Join(" ", path.Select(p => $"{p.X:F0},{p.Y:F0},{p.Z:F0}"))}]";
}
