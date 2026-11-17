using System.Numerics;
using Meitou.Simulation;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Saving;

/// <summary>
/// The <c>hire</c> mode of <c>meitou-tools probe</c>: a squad near the player's leader is hired as bodyguards (the <c>Bodyguard</c> contract on the
/// leader, as dialogue's <c>AI contract</c> starts it), the player's squad jumps away, and every round the hired squad's platoon and squad are
/// printed: loaded or a stand-in, where, and how far from the leader (#858: hired mercenaries that stay behind or come back only with their town).
/// Optionally the world is then saved to a folder, for a probe of that save.
/// </summary>
static class HireProbe
{
    /// <summary>
    /// The Bodyguard AI_PACKAGE (signal 12 Contract_HiredAlly, <c>unloaded func</c> 6).
    /// </summary>
    const string Bodyguard = "5090-gamedata.base";

    /// <summary>
    /// The contract's hours.
    /// </summary>
    const float ContractHours = 72;

    /// <summary>
    /// How far round the leader the squad to hire is looked for.
    /// </summary>
    const float HireRadius = 3000;

    /// <summary>
    /// Hires the squad named like <paramref name="name"/> (its name or its template's; "-" for the nearest town squad; "#id" hires no one and
    /// watches platoon id, a hired squad of a save), sends the player's squad <paramref name="distance"/> along X (<paramref name="walk"/>: ordered
    /// to walk there, else put there at once), prints <paramref name="rounds"/> rounds <paramref name="interval"/> ticks apart with the camera focus
    /// on the leader, and saves to <paramref name="saveTo"/> when given.
    /// </summary>
    public static void Run(ProbeWorld probe, string name, bool walk, float distance, int rounds, int interval, string? saveTo)
    {
        if (Watched(probe, name, interval) is not var (squadName, platoonId)) return;

        Print(probe, squadName, platoonId);
        if (distance != 0) Send(probe, walk, distance);
        for (int round = 0; round < rounds; round++)
        {
            probe.World.Commands.Enqueue(new FocusCommand(probe.LeaderPosition) { Tick = probe.World.Tick });
            probe.Tick(interval);
            Print(probe, squadName, platoonId);
        }

        if (saveTo is not null) Save(probe, saveTo);
    }

    /// <summary>
    /// The squad to watch, by name and platoon id: platoon "#id" as it is, else the squad found by <paramref name="name"/>, hired on the leader and
    /// given <paramref name="interval"/> ticks to take the contract. Null (said why) when there is none.
    /// </summary>
    static (string Name, int Platoon)? Watched(ProbeWorld probe, string name, int interval)
    {
        if (name.StartsWith('#')) return ("", int.Parse(name[1..]));

        if (Find(probe, name) is not { } hired)
        {
            Console.WriteLine($"no squad named {name} within {HireRadius} of the leader");
            return null;
        }

        var leader = probe.Characters.IdOf(probe.World.Squads.Find(probe.World.Player.Squad)!.Leader.Slot);
        probe.World.Commands.Enqueue(new StartContract(hired.Id, Bodyguard, ContractHours, leader) { Tick = probe.World.Tick });
        probe.Tick(interval);
        return (hired.Name, hired.PlatoonId);
    }

    /// <summary>
    /// The NPC squad within <see cref="HireRadius"/> of the leader named like <paramref name="name"/> (its name or template's name), the nearest
    /// first; "-": the nearest squad of a town.
    /// </summary>
    static Squad? Find(ProbeWorld probe, string name)
    {
        var t = probe.Characters;
        var at = probe.LeaderPosition;
        return probe.World.Squads.All
            .Where(s => s.Id != probe.World.Player.Squad && t.TryResolveNext(s.Leader, out _) && !t.Cold(s.Leader.Slot)!.IsPlayer)
            .Where(s => name == "-" ? s.Town >= 0 : Named(probe, s, name))
            .Where(s => Vector3.Distance(t.Next[s.Leader.Slot].Position, at) < HireRadius)
            .OrderBy(s => Vector3.Distance(t.Next[s.Leader.Slot].Position, at))
            .FirstOrDefault();
    }

    /// <summary>
    /// Whether the squad's name or its template's name holds <paramref name="name"/> (any case).
    /// </summary>
    static bool Named(ProbeWorld probe, Squad squad, string name) =>
        squad.Name.Contains(name, StringComparison.OrdinalIgnoreCase)
        || probe.Db.Find(squad.TemplateId)?.Name.Contains(name, StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// Sends the player's squad <paramref name="distance"/> along X from its leader: ordered to walk there, or put there at once with the camera focus.
    /// </summary>
    static void Send(ProbeWorld probe, bool walk, float distance)
    {
        var world = probe.World;
        var mine = world.Squads.Find(world.Player.Squad)!;
        var to = probe.LeaderPosition + new Vector3(distance, 0, 0);
        to.Y = world.Walkability.TerrainHeight(to.X, to.Z);
        Console.WriteLine($"{(walk ? "walk" : "jump")} to {to:F0}");
        if (walk)
        {
            world.Commands.Enqueue(new MoveOrder([.. mine.Members], to) { Tick = world.Tick });
            return;
        }

        world.Commands.Enqueue(new DebugTeleport([.. mine.Members], to) { Tick = world.Tick });
        world.Commands.Enqueue(new FocusCommand(to) { Tick = world.Tick });
    }

    /// <summary>
    /// The hired squad's line: its platoon's state, place and pin, its contract, and (loaded) its leader's place and gap to the player's leader. The
    /// squad is found by its platoon (<paramref name="platoonId"/>), else (a town squad that has none yet) by its name.
    /// </summary>
    static void Print(ProbeWorld probe, string squadName, int platoonId)
    {
        var world = probe.World;
        var t = probe.Characters;
        var me = probe.LeaderPosition;
        var platoon = platoonId >= 0
            ? world.Platoons.Find(platoonId)
            : world.Platoons.All.FirstOrDefault(p => p.Stored?.Name == squadName);
        var squad = platoon is { SquadId: >= 0 } ? world.Squads.Find(platoon.SquadId)
            : platoon is null ? world.Squads.All.FirstOrDefault(s => s.Name == squadName)
            : null;
        string platoonText = platoon is null
            ? "none"
            : $"{platoon.Id} {platoon.State} pinned={platoon.Pinned} job={platoon.Job} pos={platoon.Position:F0} "
                + $"gap={Vector2.Distance(platoon.Position, new Vector2(me.X, me.Z)):F0} goal={platoon.RouteGoal:F0} "
                + $"road={platoon.RouteStep}/{platoon.Route?.Count ?? 0}";
        string squadText = squad is { Leader.IsNone: false } && t.TryResolveNext(squad.Leader, out int lead)
            ? $"sq {squad.Id} contract={squad.Ai?.Contract?.Name} target={squad.Ai?.MissionTarget} town={squad.Ai?.ContractTown} "
                + $"leader={t.Next[lead].Position:F0} "
                + $"gap={Vector3.Distance(t.Next[lead].Position, me):F0} members={squad.Members.Count}"
            : "not in the world";
        Console.WriteLine($"tick {world.Tick} '{squadName}' platoon={platoonText} squad: {squadText}");
    }

    /// <summary>
    /// Saves the world to <paramref name="folder"/> as the game does (the capture over the loaded save).
    /// </summary>
    static void Save(ProbeWorld probe, string folder)
    {
        var game = SaveCapture.Capture(probe.World, probe.Data, probe.Loaded.Clock, probe.Loaded);
        game.Write(folder);
        Console.WriteLine($"saved to {folder}");
    }
}
