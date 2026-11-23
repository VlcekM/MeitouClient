using System.Globalization;
using System.Numerics;
using Meitou.Game.Host.Cameras;
using Meitou.Simulation;

namespace Meitou.Game.DevTools.Benchmark;

/// <summary>
/// The <c>shark-flyby</c> benchmark's script (docs/bench.md "Launcher benchmark"): a new game's squad and camera taken to the town Shark, an
/// uncounted settle that waits for the town's residents, then the strategy camera flown round the town on a fixed path: a high circle, a low
/// circle and a low pass through it.
/// </summary>
sealed class SharkFlyby
{
    #region Path

    /// <summary>
    /// The scenario's id (<c>--benchmark-scenario</c>).
    /// </summary>
    internal const string Id = "shark-flyby";

    /// <summary>
    /// The town the flight circles (a population site whose town name is this, else the shortest that contains it).
    /// </summary>
    const string Town = "Shark";

    /// <summary>
    /// The high circle's and the low circle's boom and pitch (radians): one turn each round the town's centre.
    /// </summary>
    const float HighBoom = 2000, HighPitch = 0.7f, LowBoom = 900, LowPitch = 0.3f;

    /// <summary>
    /// The pass: its length along +x through the centre, its boom and pitch (radians).
    /// </summary>
    const float PassLength = 4000, PassBoom = 300, PassPitch = 0.15f;

    /// <summary>
    /// The settle holds while fewer residents than this (characters not the player's within <see cref="ResidentRange"/> of the centre) are
    /// there, or their count rose by <see cref="ResidentGrowth"/> in the last <see cref="ResidentQuiet"/> seconds: the town still filling (on a
    /// cold navmesh cache it fills a minute or more after the teleport; BenchSegment.MaxHoldSeconds at most).
    /// </summary>
    const int MinResidents = 50, ResidentGrowth = 5;

    /// <summary>
    /// The settle's resident range (units) and quiet time (seconds).
    /// </summary>
    const float ResidentRange = 5000, ResidentQuiet = 5;

    /// <summary>
    /// The segments in order with their lengths in seconds (the settle's at least: it waits for the town).
    /// </summary>
    internal static readonly (string Name, double Seconds)[] Timeline =
        [(BenchmarkSpec.SettleSegment, 15), ("circle-high", 30), ("circle-low", 25), ("pass", 20)];

    /// <summary>
    /// The scenario's start and path, as the fingerprint reads them.
    /// </summary>
    internal static readonly string Start = string.Create(CultureInfo.InvariantCulture,
        $"new game, default start, seed 0; squad and camera taken to the town {Town}; settle holds for {MinResidents} residents "
        + $"within {ResidentRange}, none rising by {ResidentGrowth} in {ResidentQuiet} s; high circle boom {HighBoom} pitch {HighPitch}, "
        + $"low circle boom {LowBoom} pitch {LowPitch}, a turn each; pass {PassLength} along +x at boom {PassBoom} pitch {PassPitch}");

    /// <summary>
    /// The game the script drives.
    /// </summary>
    readonly GameHost _host;

    /// <summary>
    /// The strategy camera it flies.
    /// </summary>
    readonly StrategyCamera _strategy;

    /// <summary>
    /// The camera's yaw when the script was built (radians): the circles start there and the pass keeps it.
    /// </summary>
    readonly float _yaw0;

    /// <summary>
    /// The town's centre (x, z); the camera's start when the town is not found.
    /// </summary>
    Vector2 _centre;

    /// <summary>
    /// Reads the camera's start.
    /// </summary>
    public SharkFlyby(GameHost host)
    {
        _host = host;
        _strategy = host.Session.Camera.Strategy;
        _yaw0 = _strategy.Yaw;
        _centre = _strategy.PivotXz;
    }

    /// <summary>
    /// The script: each segment of <see cref="Timeline"/> with what it does.
    /// </summary>
    public BenchSegment[] Segments() =>
    [
        new(BenchmarkSpec.SettleSegment, Timeline[0].Seconds, SettleFrame, Begin: GoToTown, Hold: TownFilling),
        new("circle-high", Timeline[1].Seconds, (t, dt) => Circle(t, Timeline[1].Seconds, HighBoom, HighPitch)),
        new("circle-low", Timeline[2].Seconds, (t, dt) => Circle(t, Timeline[2].Seconds, LowBoom, LowPitch)),
        new("pass", Timeline[3].Seconds, (t, dt) => Pass(t / Timeline[3].Seconds)),
    ];

    /// <summary>
    /// Settle: the squad teleported to the town (the world streams round it) and the camera over it, following nobody.
    /// </summary>
    void GoToTown()
    {
        _host.Player.Follow = null;
        var site = _host.PopData?.Sites.Where(s => s.Town.Name.Contains(Town, StringComparison.OrdinalIgnoreCase))
            .OrderBy(s => s.Town.Name.Length)
            .FirstOrDefault();
        if (site is null)
        {
            Console.WriteLine($"bench     no town matches '{Town}': the flight circles the start");
        }
        else
        {
            _centre = new Vector2(site.Position.X, site.Position.Z);
            var squad = _host.Session.CurrentSnapshot.PlayerSquads.SelectMany(s => s.Members).ToList();
            _host.Session.World.Commands.Enqueue(new DebugTeleport(squad, site.Position) { Tick = _host.Session.World.Tick });
            Console.WriteLine($"bench     {site.Town.Name} at {_centre.X:0}, {_centre.Y:0}");
        }

        _strategy.Place(_centre, _yaw0, HighPitch, HighBoom);
    }

    /// <summary>
    /// One turn round the town in <paramref name="seconds"/> at boom <paramref name="distance"/> and <paramref name="pitch"/>.
    /// </summary>
    void Circle(double t, double seconds, float distance, float pitch) =>
        _strategy.Place(_centre, _yaw0 + (float)(t / seconds * 2 * Math.PI), pitch, distance);

    /// <summary>
    /// The low pass at <paramref name="along"/> (0 to 1): the pivot on the line through the town's centre along +x.
    /// </summary>
    void Pass(double along) => _strategy.Place(_centre + new Vector2((float)((along - 0.5) * PassLength), 0), _yaw0, PassPitch, PassBoom);

    #endregion

    #region Settle

    /// <summary>
    /// The settle's seconds so far, and when the residents last rose by <see cref="ResidentGrowth"/>.
    /// </summary>
    double _settleSeconds, _grewAt;

    /// <summary>
    /// The resident count a rise is measured from: the last rise's, or lower where it fell since.
    /// </summary>
    int _residents;

    /// <summary>
    /// Each settle frame: the residents now (not the player's characters, within <see cref="ResidentRange"/> of the centre), and when their
    /// count last rose.
    /// </summary>
    void SettleFrame(double t, double dt)
    {
        _settleSeconds = t;
        float range2 = ResidentRange * ResidentRange;
        int now = _host.Session.CurrentSnapshot.Characters
            .Count(c => !c.IsPlayer && Vector2.DistanceSquared(new Vector2(c.Position.X, c.Position.Z), _centre) < range2);

        // Characters walk out of range: the count follows them down, and only a rise from there counts as filling.
        if (now < _residents) _residents = now;
        if (now < _residents + ResidentGrowth) return;

        _residents = now;
        _grewAt = t;
    }

    /// <summary>
    /// The settle holds while the town is still filling (<see cref="MinResidents"/>, <see cref="ResidentGrowth"/>).
    /// </summary>
    bool TownFilling()
    {
        bool filling = _residents < MinResidents || _settleSeconds - _grewAt < ResidentQuiet;
        if (!filling) Console.WriteLine($"bench     {_residents} residents near {Town} after {_settleSeconds:0} s");
        return filling;
    }

    #endregion
}
