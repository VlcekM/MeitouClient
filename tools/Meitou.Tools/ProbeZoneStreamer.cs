using System.Collections.Concurrent;
using System.Diagnostics;
using Meitou.Data.World;
using Meitou.Simulation;
using Meitou.Simulation.Navigation;
using Meitou.Simulation.Population;

/// <summary>
/// <c>probe --stream</c>: loads navmesh zones while the world runs, as the game's host does in play (GameHost's zone streaming), instead of building
/// the 3 x 3 block before the load. Each tick it asks for the population's zones and those the walkers' long routes lead into, lets the population
/// activate only zones whose mesh is live, sends the zones gone live as one <see cref="RepathCommand"/> at most every second with fresh gate codes,
/// and paces the ticks to the game's speed, so a walker reaches a zone border while the zone past it is still building (#755). Not modelled: dropping
/// stale zones, and the game's path threads (the probe's paths are found at once).
/// </summary>
sealed class ProbeZoneStreamer
{
    /// <summary>
    /// Seconds between repaths at least, as GameHost's <c>RepathCoalesceSeconds</c>.
    /// </summary>
    const double RepathCoalesceSeconds = 1;

    /// <summary>
    /// How old a walker's route may be for its zones to be asked for, as GameHost's <c>LoadWantedZones</c>.
    /// </summary>
    static readonly TimeSpan RouteAge = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The navmesh zones are streamed into.
    /// </summary>
    readonly NavSystem _nav;

    /// <summary>
    /// The zones asked for so far.
    /// </summary>
    readonly HashSet<ZoneCoordinate> _asked = [];

    /// <summary>
    /// The zones gone live since the last repath, filled on the builder threads.
    /// </summary>
    readonly ConcurrentQueue<NavZoneReady> _ready = new();

    /// <summary>
    /// The real time since the first tick, for the pacing.
    /// </summary>
    readonly Stopwatch _clock = new();

    /// <summary>
    /// The tick the pacing counts from.
    /// </summary>
    long _firstTick = -1;

    /// <summary>
    /// When the last repath was sent (<see cref="_clock"/> seconds).
    /// </summary>
    double _lastRepath = double.NegativeInfinity;

    /// <summary>
    /// Streams into <paramref name="nav"/>, whose zones <paramref name="loaded"/> are already live.
    /// </summary>
    public ProbeZoneStreamer(NavSystem nav, IEnumerable<ZoneCoordinate> loaded)
    {
        _nav = nav;
        _asked.UnionWith(loaded);
        nav.ZoneReady += _ready.Enqueue;
    }

    /// <summary>
    /// Makes the population activate only zones whose mesh is live, as the game's host does.
    /// </summary>
    public void Attach(World world)
    {
        if (world.SystemOf<PopulationSystem>() is { } population) population.ZoneGate = zone => _nav.Walkability.World.Contains(zone);
    }

    /// <summary>
    /// Before a tick: waits for its time at the game's speed, asks for the zones wanted now and hands the world the zones gone live.
    /// </summary>
    public void BeforeTick(World world)
    {
        Pace(world);
        AskWanted(world);
        SendReady(world);
    }

    /// <summary>
    /// Waits until the tick's time, counted from the first tick, has come in real time.
    /// </summary>
    void Pace(World world)
    {
        if (_firstTick < 0)
        {
            _firstTick = world.Tick;
            _clock.Start();
        }

        double due = (world.Tick - _firstTick) * world.TickSeconds;
        double wait = due - _clock.Elapsed.TotalSeconds;
        if (wait > 0) Thread.Sleep(TimeSpan.FromSeconds(wait));
    }

    /// <summary>
    /// Asks for the population's zones and those the walkers' long routes lead into, printing each new one.
    /// </summary>
    void AskWanted(World world)
    {
        var population = world.SystemOf<PopulationSystem>();
        var wanted = (population?.ActiveZones ?? []).Concat(_nav.Walkability.RouteZones(RouteAge));
        foreach (var zone in wanted)
        {
            if (!_asked.Add(zone)) continue;

            Console.WriteLine($"stream tick {world.Tick} ask {zone}");
            _ = _nav.LoadZone(zone);
        }
    }

    /// <summary>
    /// Sends the zones gone live as one <see cref="RepathCommand"/>, at most every <see cref="RepathCoalesceSeconds"/>, with fresh gate codes.
    /// </summary>
    void SendReady(World world)
    {
        double now = _clock.Elapsed.TotalSeconds;
        if (_ready.IsEmpty || now - _lastRepath < RepathCoalesceSeconds) return;

        var zones = new HashSet<ZoneCoordinate>();
        while (_ready.TryDequeue(out var ready))
        {
            Console.WriteLine($"stream tick {world.Tick} live {ready.Zone} from {ready.Origin} in {ready.Milliseconds:0} ms, {ready.Polygons} polygons");
            zones.Add(ready.Zone);
        }

        _lastRepath = now;
        world.Commands.Enqueue(new RepathCommand { Tick = world.Tick, Zones = zones });
        world.Commands.Enqueue(new Meitou.Simulation.Locks.GateRegionsCommand(_nav.LabelGates()) { Tick = world.Tick });
    }
}
