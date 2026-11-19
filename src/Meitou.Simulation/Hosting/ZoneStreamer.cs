using System.Diagnostics;
using Meitou.Data.World;
using Meitou.Simulation.Locks;
using Meitou.Simulation.Navigation;
using Meitou.Simulation.Population;

namespace Meitou.Simulation.Hosting;

/// <summary>
/// Keeps a navmesh on the zones a running world needs (docs/game/pathfinding.md "Zone streaming"): loads the zones its population has active and
/// those its walkers' long routes lead into, drops the ones left too long, tells the world which went live (<see cref="RepathCommand"/>) and keeps
/// its gate codes current. One per <see cref="NavSystem"/>; whoever runs the world calls <see cref="Update"/> between its ticks, on one thread.
/// </summary>
public sealed class ZoneStreamer
{
    /// <summary>
    /// The navmesh zones are streamed into.
    /// </summary>
    readonly NavSystem _nav;

    /// <summary>
    /// Streams into <paramref name="nav"/>: the zones that go live from here on are told to the world.
    /// </summary>
    public ZoneStreamer(NavSystem nav)
    {
        _nav = nav;
        nav.ZoneReady += NoteReady;
    }

    #region Zones

    /// <summary>
    /// How old a walker's route may be for its zones to be asked for.
    /// </summary>
    static readonly TimeSpan RouteAge = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The loaded zones and when each was last wanted (a <see cref="Stopwatch"/> timestamp).
    /// </summary>
    readonly Dictionary<ZoneCoordinate, long> _wanted = [];

    /// <summary>
    /// Zones whose mesh went live since the last <see cref="RepathCommand"/>, with what the builder reported (filled on the nav thread, under its
    /// own lock).
    /// </summary>
    readonly Dictionary<ZoneCoordinate, NavZoneReady> _ready = [];
    /// <summary>
    /// When the last <see cref="RepathCommand"/> was sent (a <see cref="Stopwatch"/> timestamp).
    /// </summary>
    long _lastRepath;

    // The batch of zones being dropped on the pool, and its task.
    /// <summary>
    /// The task.
    /// </summary>
    Task _unload = Task.CompletedTask;
    /// <summary>
    /// The zones being dropped.
    /// </summary>
    HashSet<ZoneCoordinate> _unloading = [];

    /// <summary>
    /// Seconds between <see cref="RepathCommand"/>s at least: zones go live in runs along a long walk, and are sent together (engine choice).
    /// </summary>
    public double RepathSeconds { get; init; } = 1;

    // Meitou's asset cache (engine choice): a left zone's mesh stays CacheSeconds, at most CacheZones zones, the oldest dropped first, so a return
    // within two minutes rebuilds nothing.
    /// <summary>
    /// The seconds a left zone's mesh stays.
    /// </summary>
    public double CacheSeconds { get; init; } = 120;
    /// <summary>
    /// At most this many zones' meshes stay loaded; the ones wanted longest ago are dropped past it.
    /// </summary>
    public int CacheZones { get; init; } = 60;

    /// <summary>
    /// A zone not loaded is asked for (raised in <see cref="Update"/>).
    /// </summary>
    public event Action<ZoneCoordinate>? Asked;

    /// <summary>
    /// The zones gone live were sent to the world as one <see cref="RepathCommand"/> (raised in <see cref="Update"/>).
    /// </summary>
    public event Action<IReadOnlyCollection<NavZoneReady>>? Sent;

    /// <summary>
    /// Makes <paramref name="population"/> activate only zones whose mesh is live.
    /// </summary>
    public void Gate(PopulationSystem population) => population.ZoneGate = IsLive;

    /// <summary>
    /// Counts <paramref name="zones"/>, loaded by the caller, as wanted now: they are kept and dropped like the ones asked for here.
    /// </summary>
    public void MarkWanted(IEnumerable<ZoneCoordinate> zones)
    {
        long now = Stopwatch.GetTimestamp();
        foreach (var z in zones)
            _wanted[z] = now;
    }

    /// <summary>
    /// Asks for the <paramref name="zones"/> not loaded and counts them all as wanted now; done when their meshes are live. Zones being dropped
    /// are waited for first (a load right after must not be undone by it).
    /// </summary>
    public Task Load(IEnumerable<ZoneCoordinate> zones)
    {
        FinishUnload();
        long now = Stopwatch.GetTimestamp();
        var pending = new List<Task>();
        foreach (var z in zones)
        {
            if (!_wanted.ContainsKey(z)) pending.Add(_nav.LoadZone(z));
            _wanted[z] = now;
        }

        return Task.WhenAll(pending);
    }

    /// <summary>
    /// Keeps the navmesh on the zones <paramref name="population"/> has active (built on the nav thread in the order the player's characters reach
    /// them, cached), drops the ones idle too long, and has the characters of <paramref name="world"/> whose way leads through a zone whose mesh
    /// arrived ask for their paths again (their first path may have crossed a building), at most every <see cref="RepathSeconds"/>.
    /// <paramref name="wait"/> waits for the zones asked for and sends them at once.
    /// </summary>
    public void Update(World world, PopulationSystem? population, bool wait = false)
    {
        UpdateGateRegions(world);
        long now = Stopwatch.GetTimestamp();

        // Dropping zones republishes the whole mesh (tens of ms): it runs on the pool, one batch at a time, and a zone being dropped is loaded again
        // once that is done.
        if (_unloading.Count > 0 && _unload.IsCompleted)
        {
            _unloading = [];
            _relabelGates = true;
        }
        if (wait) FinishUnload();

        _nav.Favour(world);
        _nav.Walkability.GameSpeed = world.PhysicsSpeed;
        var pending = LoadWantedZones(population, now);
        DropStaleZones(now);
        if (wait) Task.WaitAll([.. pending]);

        SendRepath(world, pending, wait, now);
    }

    /// <summary>
    /// Whether <paramref name="zone"/>'s mesh is live.
    /// </summary>
    bool IsLive(ZoneCoordinate zone) => _nav.Walkability.World.Contains(zone);

    /// <summary>
    /// Keeps a zone gone live for the next <see cref="RepathCommand"/> (on the nav thread).
    /// </summary>
    void NoteReady(NavZoneReady ready)
    {
        lock (_ready)
            _ready[ready.Zone] = ready;
    }

    /// <summary>
    /// Waits for the zones being dropped.
    /// </summary>
    void FinishUnload()
    {
        if (_unloading.Count == 0) return;

        _unload.Wait();
        _unloading = [];
        _relabelGates = true;
    }

    /// <summary>
    /// Starts loading the population's zones and those the walkers' long routes lead into (NavmeshWalkability.RouteZones), and marks them wanted now.
    /// </summary>
    List<Task<NavZoneReady>> LoadWantedZones(PopulationSystem? population, long now)
    {
        var pending = new List<Task<NavZoneReady>>();
        IEnumerable<ZoneCoordinate> active = population?.ActiveZones ?? [];
        foreach (var z in active.Concat(_nav.Walkability.RouteZones(RouteAge)))
        {
            if (_unloading.Contains(z)) continue;
            if (!_wanted.ContainsKey(z)) pending.Add(Ask(z));

            _wanted[z] = now;
        }

        return pending;
    }

    /// <summary>
    /// Asks the builder for <paramref name="zone"/>.
    /// </summary>
    Task<NavZoneReady> Ask(ZoneCoordinate zone)
    {
        Asked?.Invoke(zone);
        return _nav.LoadZone(zone);
    }

    /// <summary>
    /// Drops the zones unwanted for <see cref="CacheSeconds"/>, and the oldest past <see cref="CacheZones"/>, once no other batch is being dropped.
    /// </summary>
    void DropStaleZones(long now)
    {
        var stale = _wanted.Where(e => Stopwatch.GetElapsedTime(e.Value, now).TotalSeconds > CacheSeconds).Select(e => e.Key).ToList();
        int overCache = _wanted.Count - stale.Count - CacheZones;
        if (overCache > 0)
            stale.AddRange(_wanted.Where(e => !stale.Contains(e.Key)).OrderBy(e => e.Value).Take(overCache).Select(e => e.Key));

        if (stale.Count == 0 || _unloading.Count > 0) return;

        foreach (var z in stale)
            _wanted.Remove(z);
        _unloading = [.. stale];
        _unload = Task.Run(() => _nav.UnloadZones(stale));
    }

    /// <summary>
    /// Sends the zones gone live as one <see cref="RepathCommand"/>, at most every <see cref="RepathSeconds"/> unless waited for.
    /// </summary>
    void SendRepath(World world, List<Task<NavZoneReady>> pending, bool wait, long now)
    {
        NavZoneReady[] live;
        lock (_ready)
        {
            // The event follows the task's completion, so zones waited for are added here.
            if (wait)
            {
                foreach (var task in pending)
                    _ready.TryAdd(task.Result.Zone, task.Result);
            }

            bool coalescing = !wait && Stopwatch.GetElapsedTime(_lastRepath, now).TotalSeconds < RepathSeconds;
            if (_ready.Count == 0 || coalescing) return;

            live = [.. _ready.Values];
            _ready.Clear();
        }

        _lastRepath = now;
        _relabelGates = true;
        world.Commands.Enqueue(new RepathCommand { Tick = world.Tick, Zones = live.Select(r => r.Zone).ToHashSet() });
        Sent?.Invoke(live);
    }

    #endregion

    #region Gate codes

    /// <summary>
    /// The navmesh changed since the gate codes were made (a zone went live or was dropped).
    /// </summary>
    bool _relabelGates;
    /// <summary>
    /// The labelling running on the pool; null when none.
    /// </summary>
    Task<NavGateRegions>? _gateLabelling;
    /// <summary>
    /// The last gate codes made; null before the first.
    /// </summary>
    NavGateRegions? _gateRegions;
    /// <summary>
    /// The world <see cref="_gateRegions"/> was last handed to: a new world is handed them again.
    /// </summary>
    World? _gateRegionsFor;

    /// <summary>
    /// Keeps <paramref name="world"/>'s gate codes (docs/game/pathfinding.md "Gate codes") up to date: labels the loaded meshes on the pool after
    /// they changed, one labelling at a time, and hands each result to the world (again to a new world) as a <see cref="GateRegionsCommand"/>.
    /// </summary>
    void UpdateGateRegions(World world)
    {
        if (_gateLabelling is { IsCompleted: true } done)
        {
            if (done.Status == TaskStatus.RanToCompletion)
            {
                _gateRegions = done.Result;
                _gateRegionsFor = null;
            }
            else
            {
                Console.Error.WriteLine($"nav       gate codes failed: {done.Exception?.GetBaseException().Message}");
            }

            _gateLabelling = null;
        }

        if (_relabelGates && _gateLabelling is null)
        {
            _relabelGates = false;
            _gateLabelling = Task.Run(_nav.LabelGates);
        }

        if (_gateRegions is { } regions && !ReferenceEquals(_gateRegionsFor, world))
        {
            _gateRegionsFor = world;
            world.Commands.Enqueue(new GateRegionsCommand(regions) { Tick = world.Tick });
        }
    }

    #endregion
}
