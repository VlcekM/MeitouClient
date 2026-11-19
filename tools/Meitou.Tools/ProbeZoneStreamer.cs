using System.Diagnostics;
using System.Numerics;
using Meitou.Data.World;
using Meitou.Simulation;
using Meitou.Simulation.Hosting;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Navigation;
using Meitou.Simulation.Population;

/// <summary>
/// <c>probe --stream</c>: loads navmesh zones while the world runs, with the game's own zone streaming (<see cref="ZoneStreamer"/>), instead of
/// building the 3 x 3 block before the load. Each tick the streamer asks for the population's zones and those the walkers' long routes lead into,
/// lets the population activate only zones whose mesh is live, sends the zones gone live as one <see cref="RepathCommand"/> at most every second
/// and has the gate codes labelled again on the pool; the ticks are paced to the game's speed (<c>--speed</c>), so a walker reaches a zone border
/// while the zone past it is still building (#755). Once the world runs, the builder works on the game's share of the cores in play
/// (<see cref="NavSystem.Covered"/> off). Each zone asked for and gone live is printed, and each player character with a path that stops, with
/// whether its zone is loaded, and again when it moves on (#824). Not modelled: dropping stale zones (the streamer keeps every zone here), and the
/// game's path threads (the probe's paths are found at once).
/// </summary>
sealed class ProbeZoneStreamer
{
    /// <summary>
    /// The navmesh zones are streamed into.
    /// </summary>
    readonly NavSystem _nav;

    /// <summary>
    /// The game's streaming, keeping every zone it loaded.
    /// </summary>
    readonly ZoneStreamer _zones;

    /// <summary>
    /// The real time since the first tick, for the pacing.
    /// </summary>
    readonly Stopwatch _clock = new();

    /// <summary>
    /// The tick the pacing counts from.
    /// </summary>
    long _firstTick = -1;

    /// <summary>
    /// The tick being prepared, for the printed lines.
    /// </summary>
    long _tick;

    /// <summary>
    /// The real time (s) after which a tick runs late enough to drop the backlog instead of catching up (the game's frames do not run a stall's
    /// ticks at once either; a first long-range path's terrain grid holds the first tick for tens of seconds).
    /// </summary>
    const double MaxBehindSeconds = 0.25;

    /// <summary>
    /// The tick each player character with a path stopped at (stood still since the tick before), by slot.
    /// </summary>
    readonly Dictionary<int, long> _stopped = [];

    /// <summary>
    /// Each player character's position before the last tick, by slot.
    /// </summary>
    readonly Dictionary<int, Vector3> _was = [];

    /// <summary>
    /// Streams into <paramref name="nav"/>, whose zones <paramref name="loaded"/> are already live.
    /// </summary>
    public ProbeZoneStreamer(NavSystem nav, IEnumerable<ZoneCoordinate> loaded)
    {
        _nav = nav;
        _zones = new ZoneStreamer(nav) { CacheSeconds = double.PositiveInfinity, CacheZones = int.MaxValue };
        _zones.MarkWanted(loaded);
        _zones.Asked += PrintAsked;
        _zones.Sent += PrintLive;
    }

    /// <summary>
    /// Makes the population activate only zones whose mesh is live, as the game's host does.
    /// </summary>
    public void Attach(World world)
    {
        if (world.SystemOf<PopulationSystem>() is { } population) _zones.Gate(population);
    }

    /// <summary>
    /// Before a tick: waits for its time at the game's speed, then the streamer asks for the zones wanted now and hands the world the zones gone
    /// live.
    /// </summary>
    public void BeforeTick(World world)
    {
        Pace(world);
        _tick = world.Tick;
        _zones.Update(world, world.SystemOf<PopulationSystem>());
        PrintStopped(world);
    }

    /// <summary>
    /// Waits until the tick's time, counted from the first tick, has come in real time at the game's speed (<see cref="World.PhysicsSpeed"/>).
    /// </summary>
    void Pace(World world)
    {
        if (!_clock.IsRunning)
        {
            _firstTick = world.Tick;
            _clock.Start();
            _nav.Covered = false;
        }

        double due = (world.Tick - _firstTick) * world.TickSeconds / Math.Max(world.PhysicsSpeed, 1);
        double wait = due - _clock.Elapsed.TotalSeconds;
        if (wait > 0) Thread.Sleep(TimeSpan.FromSeconds(wait));
        else if (wait < -MaxBehindSeconds)
            _firstTick = world.Tick - (long)(_clock.Elapsed.TotalSeconds * Math.Max(world.PhysicsSpeed, 1) / world.TickSeconds);
    }

    /// <summary>
    /// Prints a zone the streamer asks for.
    /// </summary>
    void PrintAsked(ZoneCoordinate zone) => Console.WriteLine($"stream {Seconds} s tick {_tick} ask {zone}");

    /// <summary>
    /// Prints the zones gone live the streamer sent the world, with what the builder reported.
    /// </summary>
    void PrintLive(IReadOnlyCollection<NavZoneReady> live)
    {
        foreach (var ready in live)
        {
            string built = $"from {ready.Origin} in {ready.Milliseconds:0} ms, {ready.Polygons} polygons";
            Console.WriteLine($"stream {Seconds} s tick {_tick} live {ready.Zone} {built}");
        }
    }

    /// <summary>
    /// The real time since the first tick, for the printed lines.
    /// </summary>
    string Seconds => _clock.Elapsed.TotalSeconds.ToString("0.0");

    /// <summary>
    /// Prints each player character with a path that stopped since the tick before, with whether its zone is loaded (a character in a zone not
    /// loaded is frozen, <see cref="World"/>'s dormant marks), and each that moves on with the ticks it stood.
    /// </summary>
    void PrintStopped(World world)
    {
        var table = world.Characters;
        for (int i = 0; i < table.HighWater; i++)
        {
            var at = table.Previous[i].Position;
            bool player = table.Previous[i].Alive && table.Cold(i) is { IsPlayer: true };
            bool moving = ((MoveFlags)table.Previous[i].Flags & MoveFlags.HasPath) != 0;
            bool stood = player && moving && _was.TryGetValue(i, out var was) && was == at;
            if (player) _was[i] = at;
            if (stood == _stopped.ContainsKey(i)) continue;

            PrintStop(world, i, at, stood);
        }
    }

    /// <summary>
    /// One line of <see cref="PrintStopped"/>.
    /// </summary>
    void PrintStop(World world, int slot, Vector3 at, bool stood)
    {
        string name = world.Characters.Cold(slot)?.Name ?? $"slot {slot}";
        if (!stood)
        {
            Console.WriteLine($"stream {Seconds} s tick {world.Tick} moves {name} after {world.Tick - _stopped[slot]} ticks");
            _stopped.Remove(slot);
            return;
        }

        var zone = WorldLayout.ZoneOf(at.X, at.Z);
        string loaded = world.LoadedZones?.Contains(zone) != false ? "loaded" : "not loaded";
        _stopped[slot] = world.Tick;
        Console.WriteLine($"stream {Seconds} s tick {world.Tick} stops {name} at {at.X:0}, {at.Z:0} in {zone} ({loaded})");
    }
}
