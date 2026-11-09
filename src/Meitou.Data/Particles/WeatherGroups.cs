using System.Diagnostics;

namespace Meitou.Data.Particles;

/// <summary>
/// The weather's effect groups and when they change (docs/formats/particle-universe.md "Weather changes"). The game removes a region's groups and
/// makes the new weather's the moment its weather changes; the viewer shows only the camera's region and cannot start a group with no particles, so a
/// change costs a warm-up of up to 40 s of simulation. Two choices keep that off the frame (Observed, measured 2026-10-10; the sim need not be 1:1):
/// <list type="bullet">
/// <item>A change is made on the side: the new groups are created on the render thread (microseconds, with the terrain's heights frozen for the
/// worker), scheduled and simulated on a worker (<see cref="EffectSet"/>, <see cref="EffectWarmer"/>), and swapped for the old ones when they are
/// ready; the old weather's particles are drawn meanwhile.</item>
/// <item>The camera crossing into another region waits for <see cref="RegionDwell"/> seconds before anything is made, and is forgotten if the weather
/// returns to the shown one before: the checkerboard of small regions at a coast flipped the weather several times in a flight, and each flip
/// would have made (and thrown away) a warm-up. A change inside the region the groups were made for (the scheduler's next weather, a forced
/// or rerolled one) waits for nothing.</item>
/// </list>
/// The first groups (the start of a view) are made and warmed at once on the calling thread, so the first picture has its particles.
/// Not thread-safe: the render thread calls <see cref="Want"/> and <see cref="Advance"/>, with the frame's own simulation not running.
/// </summary>
public sealed class WeatherGroups : IDisposable
{
    /// <summary>
    /// The longest warm-up a group gets of its own accord (seconds): <see cref="EffectSet.MaxAutoPrewarm"/>.
    /// </summary>
    public const float MaxAutoPrewarm = EffectSet.MaxAutoPrewarm;

    /// <summary>
    /// The particle systems the groups are made from.
    /// </summary>
    readonly ParticleLibrary _library;
    /// <summary>
    /// The ground and wind the groups live in.
    /// </summary>
    readonly EffectWorld _world;
    /// <summary>
    /// The groups shown; replaced in place by <see cref="Swap"/>.
    /// </summary>
    readonly List<EffectGroup> _groups = [];
    /// <summary>
    /// The entries of the shown weather that have no group.
    /// </summary>
    readonly List<WeatherEffectEntry> _skipped = [];
    /// <summary>
    /// Whether a weather's groups have been swapped in yet: the first change is made at once, without a dwell.
    /// </summary>
    bool _applied;
    /// <summary>
    /// The weather to change to (null: none) and the one whose warm-up last threw, not tried again until the weather changes.
    /// </summary>
    WeatherEffectInput? _wanted, _failed;
    /// <summary>
    /// When <see cref="_wanted"/> was first wanted, and the seconds it must hold before it is made.
    /// </summary>
    double _wantedSince, _wantedDwell;
    /// <summary>
    /// The set being warmed in the background; null when none is.
    /// </summary>
    EffectSet? _building;
    /// <summary>
    /// The clock of the last <see cref="Advance"/>, for <see cref="SwapLatency"/>.
    /// </summary>
    double _lastNow;

    /// <summary>
    /// Seconds from the weather being wanted to its groups being shown, for the last change (the dwell and the warm-up; for the log).
    /// </summary>
    public double SwapLatency { get; private set; }

    /// <summary>
    /// The thread that warms background sets, made with the groups and not at the first change: starting one in a frame can stall that frame.
    /// </summary>
    readonly EffectWarmer _warmer = new();

    /// <summary>
    /// Shows no groups until the first weather is wanted and advanced.
    /// </summary>
    public WeatherGroups(ParticleLibrary library, EffectWorld world) => (this._library, this._world) = (library, world);

    /// <summary>
    /// The seed of the groups' random numbers (a group takes the seed plus its place in the list).
    /// </summary>
    public int Seed { get; set; } = 1;
    /// <summary>
    /// Seconds simulated before the first picture of a new group: null is each system's longest particle life, capped at
    /// <see cref="MaxAutoPrewarm"/>; 0 starts empty.
    /// </summary>
    public float? PrewarmSeconds { get; set; }
    /// <summary>
    /// Seconds a change of region must hold before the groups are rebuilt (0: none).
    /// </summary>
    public float RegionDwell { get; set; } = 2;
    /// <summary>
    /// Whether a change is warmed on a worker while the old groups run on (the default). False: it is made and warmed at once on the calling thread.
    /// </summary>
    public bool Background { get; set; } = true;

    /// <summary>
    /// The groups shown (the same list object for ever: it is replaced in place by <see cref="Advance"/>).
    /// </summary>
    public IReadOnlyList<EffectGroup> Groups => _groups;
    /// <summary>
    /// The entries of the shown weather that have no group.
    /// </summary>
    public IReadOnlyList<WeatherEffectEntry> Skipped => _skipped;
    /// <summary>
    /// The weather the groups were made for, with the latest strength and wind.
    /// </summary>
    public WeatherEffectInput Input { get; private set; } = WeatherEffectInput.None;
    /// <summary>
    /// A change is waiting out its dwell or being made.
    /// </summary>
    public bool Changing => _wanted is not null;
    /// <summary>
    /// The groups of a change are being simulated on the worker.
    /// </summary>
    public bool Building => _building is not null;
    /// <summary>
    /// The groups being warmed (empty when none are): for whoever wants to prepare what they draw with.
    /// </summary>
    public IReadOnlyList<EffectGroup> BuildingGroups => _building?.Groups ?? [];

    /// <summary>
    /// What the weather is now, every frame (<paramref name="now"/> in seconds on any steady clock). The weather of the groups shown only updates
    /// their strength and wind; another one is noted to be made by <see cref="Advance"/>, after its dwell if the camera is in another region than the
    /// groups were made for.
    /// </summary>
    public void Want(WeatherEffectInput weather, double now)
    {
        if (_applied && weather.SameEffects(Input)) { Input = weather; _wanted = null; return; }   // back to the shown weather: the change is off
        if (_wanted is not null && weather.SameEffects(_wanted))
        {
            _wanted = weather;
            return;
        }
        if (_failed is not null && weather.SameEffects(_failed)) return;   // its warm-up threw: not tried again until the weather changes
        _failed = null;
        _wanted = weather;
        _wantedSince = now;
        _wantedDwell = _applied && Background && RegionDwell > 0 && weather.Region.Length > 0 && Input.Region.Length > 0
            && weather.Region != Input.Region ? RegionDwell : 0;
    }

    /// <summary>
    /// Carries a wanted change on: makes the groups once the dwell is over, swaps them in when their warm-up is done. Called once per frame with the
    /// frame's simulation finished and before the groups update; <paramref name="camera"/> is where the warm-up is made. Returns whether the groups
    /// were replaced.
    /// </summary>
    public bool Advance(in EffectCamera camera, double now)
    {
        _lastNow = now;
        bool changed = false;
        if (_building is { } set)
        {
            // the weather moved on, or came back to the shown one
            if (_wanted is null || !set.Source.SameEffects(_wanted))
            {
                set.Cancel();
                _building = null;
            }
            else if (set.Ready)
            {
                _building = null;
                if (set.Failure is { } failure)
                {
                    Console.WriteLine($"warning   weather particles: the warm-up failed ({failure.Message}); keeping the groups shown");
                    (_failed, _wanted) = (_wanted, null);
                }
                else
                {
                    Swap(set, _wanted);
                    _wanted = null;
                    changed = true;
                }
            }
        }
        if (_building is null && _wanted is { } next && (!_applied || !Background || now - _wantedSince >= _wantedDwell))
        {
            if (!_applied || !Background)
            {
                var made = EffectSet.Begin(next, _library, Seed, _world, PrewarmSeconds, camera);
                made.SimulateNow();
                Swap(made, next);
                _wanted = null;
                changed = true;
            }
            else
            {
                var made = EffectSet.BeginInBackground(next, _library, Seed, _world, PrewarmSeconds, camera, _warmer);
                LastStartTicks = made.StartTicks;
                if (made.Ready) { Swap(made, next); _wanted = null; changed = true; }   // no groups at all (a clear weather)
                else _building = made;
            }
        }
        return changed;
    }

    /// <summary>
    /// Shows the groups of <paramref name="set"/> for <paramref name="weather"/> and notes the change's latency and cost.
    /// </summary>
    void Swap(EffectSet set, WeatherEffectInput weather)
    {
        long start = Stopwatch.GetTimestamp();
        SwapLatency = _lastNow - _wantedSince;
        _groups.Clear();
        _groups.AddRange(set.Groups);
        _skipped.Clear();
        _skipped.AddRange(set.Skipped);
        Input = weather;
        _applied = true;
        LastSwapTicks = Stopwatch.GetTimestamp() - start;
    }

    /// <summary>
    /// For the log: what starting the last background build cost the calling thread (<see cref="EffectSet.StartTicks"/>), and the Stopwatch ticks the
    /// last swap took.
    /// </summary>
    public (long Freeze, long Make, long Queue) LastStartTicks { get; private set; }
    /// <summary>
    /// Stopwatch ticks the last <see cref="Swap"/> took.
    /// </summary>
    public long LastSwapTicks { get; private set; }

    /// <summary>
    /// Cancels a change being warmed and ends the warming thread.
    /// </summary>
    public void Dispose()
    {
        _building?.Cancel();
        _building = null;
        _warmer.Dispose();
    }
}