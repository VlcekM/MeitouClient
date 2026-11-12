using System.Diagnostics;
using System.Numerics;
using Meitou.Content;
using Meitou.Data.World;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Profiling;

namespace Meitou.Game.Rendering.Terrain;

/// <summary>
/// GL work cut into steps (a slab of a texture, a swap) that run a few at a time on the render thread, so streaming data in never costs a frame more
/// than the budget (a step is at most about 2 MB of upload). The first step of a <see cref="Run"/> always runs; after it, a step starts only if the
/// budget has not passed and what steps of its label have cost lately (<see cref="StepCosts"/>) still fits in what is left, so a step known to be
/// heavy waits for a frame of its own instead of running on top of others.
/// </summary>
public sealed class UploadQueue
{
    /// <summary>
    /// The queued steps with their labels.
    /// </summary>
    readonly Queue<(Action Step, string Label)> _steps = new();

    /// <summary>
    /// What each label's steps have cost lately.
    /// </summary>
    readonly StepCosts _costs = new();

    /// <summary>
    /// The clock in milliseconds.
    /// </summary>
    readonly Func<double> _clock;

    /// <param name="clockMs">Milliseconds on any scale that only moves forward (tests pass their own); the real time by default.</param>
    public UploadQueue(Func<double>? clockMs = null) => _clock = clockMs ?? (() => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency);

    /// <summary>
    /// The steps queued.
    /// </summary>
    public int Count => _steps.Count;

    /// <summary>
    /// Labels of the steps the last <see cref="Run"/> executed (for diagnosing slow frames).
    /// </summary>
    public string LastRun { get; private set; } = "";

    /// <summary>
    /// What steps with <paramref name="label"/> have cost lately, in milliseconds (0 before the first).
    /// </summary>
    public double ExpectedMs(string label) => _costs.Expected(label);

    /// <summary>
    /// Queues a step under <paramref name="label"/> (the label its cost is learnt by).
    /// </summary>
    public void Add(Action step, string label = "step") => _steps.Enqueue((step, label));

    /// <summary>
    /// Runs queued steps until <paramref name="budgetMs"/> has passed or the next one is expected to end more than <see cref="StepCosts.Slack"/> past
    /// it (always at least one, if any).
    /// </summary>
    public void Run(double budgetMs)
    {
        LastRun = "";
        double start = _clock();
        int ran = 0;
        while (_steps.TryPeek(out var next))
        {
            double elapsed = _clock() - start;
            if (ran > 0 && (elapsed >= budgetMs || !_costs.Fits(next.Label, elapsed, budgetMs))) break;

            var item = _steps.Dequeue();
            double began = _clock();
            item.Step();
            double ms = _clock() - began;
            _costs.Learn(item.Label, ms);
            ran++;
            LastRun += item.Label + ", ";
            if (ms > 3) SpikeLog.Note($"slow terrain step {item.Label}", ms: ms);   // with --log-spikes
        }
    }
}

/// <summary>
/// Makes the terrain's detail follow the camera. The fine height window (<see cref="HeightWindow"/>, the finest heightmap samples) is re-read from
/// <c>fullmap.tif</c> around the eye on a worker thread when the eye has left the middle of the current one, and swapped in when uploaded; the old one
/// is used until then. The biome layers and the overlay/colour windows stream the same way inside <see cref="TerrainTextures"/>.
/// </summary>
public sealed class TerrainStreamer : IDisposable
{
    /// <summary>
    /// Cells per side of a streamed height window (at least; the starting window's size if larger).
    /// </summary>
    public const int WindowCells = 1536;

    /// <summary>
    /// The eye may drift this fraction of the window's width from its centre before a new window is read.
    /// </summary>
    internal const double RecentreFraction = 0.15;

    /// <summary>
    /// The renderer whose fine window is replaced.
    /// </summary>
    readonly TerrainRenderer _terrain;

    /// <summary>
    /// The biome and overlay/colour windows (null without textures).
    /// </summary>
    readonly TerrainTextures? _textures;

    /// <summary>
    /// The upload steps of the windows and textures.
    /// </summary>
    readonly UploadQueue _uploads = new();

    /// <summary>
    /// The worker's own handle on <c>fullmap.tif</c>.
    /// </summary>
    readonly TerrainHeightmap _map;

    /// <summary>
    /// Cells per side of a window, and the heightmap's sample step it is read at (1: every sample).
    /// </summary>
    readonly int _cells, _step;

    /// <summary>
    /// The window read running on the worker, with its bounds (null: none).
    /// </summary>
    Task<(HeightWindow Window, TerrainHeightBounds.Patch Bounds)>? _job;

    /// <summary>
    /// The standby the running job (and its upload) makes, or null: the job makes the drawn window.
    /// </summary>
    bool _jobStandby;

    /// <summary>
    /// The drawn window's upload is under way (until its swap).
    /// </summary>
    bool _swapping;

    /// <summary>
    /// Uploaded windows round kept points far from the eye's window (<see cref="Kept"/>), swapped in when the eye arrives.
    /// </summary>
    readonly HeightStandbys _standbys;

    /// <summary>
    /// Opens <paramref name="install"/>'s heightmap again for the worker; windows are read every <paramref name="step"/> samples.
    /// </summary>
    public TerrainStreamer(GameInstall install, TerrainRenderer terrain, TerrainTextures? textures, int step)
    {
        this._terrain = terrain;
        this._textures = textures;
        this._step = Math.Max(step, 1);
        _cells = Math.Max(WindowCells, terrain.Fine.Columns - 1);
        _standbys = new HeightStandbys(this, terrain);

        // A second handle on the file: the worker must not share the render thread's stream.
        _map = TerrainHeightmap.Open(install);
    }

    /// <summary>
    /// Places kept loaded (<see cref="WorldTextureCache.Areas"/>): the standby counts and the terrain layers' near slots scale with it.
    /// </summary>
    public int Areas { get; set; } = 1;

    /// <summary>
    /// Distance within which the full terrain material is wanted (biomes are loaded for the cells inside it).
    /// </summary>
    public float MaterialDistance { get; set; } = 30000;

    /// <summary>
    /// Render-thread time per frame spent on uploads.
    /// </summary>
    public double BudgetMs { get; set; } = 2;

    /// <summary>
    /// Height windows swapped in so far.
    /// </summary>
    public int HeightWindows { get; private set; }

    /// <summary>
    /// Milliseconds the last window read took on the worker.
    /// </summary>
    public double LastWindowMs { get; private set; }

    /// <summary>
    /// Render-thread time of the last <see cref="Update(Vector3)"/>.
    /// </summary>
    public double LastUpdateMs { get; private set; }

    /// <summary>
    /// The labels of the upload steps the last update ran.
    /// </summary>
    public string LastSteps => _uploads.LastRun;

    /// <summary>
    /// A character flight's arrival eye: the biome textures there load during the flight (the height window still moves only with the eye).
    /// </summary>
    public Vector3? Ahead { get; set; }

    /// <summary>
    /// Points whose surrounding biome textures, colour and overlay windows and fine heights stay loaded besides the eye's ("Keep textures at
    /// characters").
    /// </summary>
    public IReadOnlyList<Vector3> Kept { get; set; } = [];

    /// <summary>
    /// Video memory pressure: while it is on no standby window is made.
    /// </summary>
    public VramGuard? Guard { get; set; }

    /// <summary>
    /// The ground's height under the eye, for which biome textures load (<see cref="TerrainTextures.GroundUnderEye"/>).
    /// </summary>
    public float GroundUnderEye { get; set; } = float.NegativeInfinity;

    /// <summary>
    /// Nothing is being read, uploaded or swapped (a standby's read is not waited for: a far squad member does not hold up the loading screen).
    /// </summary>
    public bool Idle => (_job is null || _jobStandby) && !_swapping && _uploads.Count == 0 && (_textures?.Idle ?? true);

    /// <summary>
    /// Work in flight (decodes, window reads, queued upload steps), for the window title.
    /// </summary>
    public int Pending => (_job is null ? 0 : 1) + _uploads.Count + (_textures?.Outstanding ?? 0);

    /// <summary>
    /// A line about what is loaded, for the log.
    /// </summary>
    public string Describe()
    {
        string textures = _textures is null ? "" : TextureSummary(_textures);
        return $"height windows {HeightWindows}{textures}";
    }

    /// <summary>
    /// Streams round <paramref name="eye"/> within <see cref="BudgetMs"/>, timed into <see cref="LastUpdateMs"/>.
    /// </summary>
    public void Update(Vector3 eye)
    {
        var watch = Stopwatch.StartNew();
        Update(eye, BudgetMs);
        LastUpdateMs = watch.Elapsed.TotalMilliseconds;
    }

    /// <summary>
    /// Waits for everything wanted around the eye to be loaded and uploaded (offscreen rendering).
    /// </summary>
    public void Settle(Vector3 eye, int timeoutMs = 300000)
    {
        var watch = Stopwatch.StartNew();
        int rounds = 0;
        while (watch.ElapsedMilliseconds < timeoutMs)
        {
            Update(eye, 1e9);
            if (++rounds > 1 && Idle) return;

            Thread.Sleep(2);
        }

        Console.WriteLine($"warning   streaming did not finish in {timeoutMs / 1000} s");
    }

    /// <summary>
    /// Waits up to 5 s for a window read still running, then drops the standbys and closes the heightmap.
    /// </summary>
    public void Dispose()
    {
        try
        {
            _job?.Wait(5000);
        }
        catch (AggregateException)
        {
        }

        _standbys.DropAll();
        _map.Dispose();
    }

    /// <summary>
    /// The biomes resident and the overlay/colour maps' state, for <see cref="Describe"/>.
    /// </summary>
    static string TextureSummary(TerrainTextures textures)
    {
        string resident = string.Join(", ", textures.Resident.Select(b => b.Name.Trim()));
        string maps = textures.MapState == 2 ? "loaded" : "missing";
        return $", biomes {textures.ResidentBiomes}/{textures.TotalBiomes} resident ({resident}), overlay/colour maps {maps}";
    }

    /// <summary>
    /// Takes a finished window, starts the next one the eye needs, streams the textures and runs the upload steps within <paramref name="budgetMs"/>.
    /// </summary>
    void Update(Vector3 eye, double budgetMs)
    {
        ApplyFinishedJob();
        RequestWindow(eye);
        if (_textures is not null) (_textures.GroundUnderEye, _textures.Guard, _textures.Areas) = (GroundUnderEye, Guard, Areas);

        _textures?.Update(eye, MaterialDistance, _uploads, Ahead, Kept);
        _uploads.Run(budgetMs);
    }

    /// <summary>
    /// Hands a window that the worker finished to its upload: the drawn one, or a standby.
    /// </summary>
    void ApplyFinishedJob()
    {
        if (_job is not { IsCompleted: true } done) return;

        _job = null;
        try
        {
            var (window, bounds) = done.Result;
            if (_jobStandby)
            {
                _standbys.BeginUpload(window, bounds, _uploads);
            }
            else
            {
                _swapping = true;
                _terrain.BeginFineUpload(window, bounds, _uploads, () =>
                {
                    _swapping = false;
                    HeightWindows++;
                });
            }
        }
        catch (AggregateException e)
        {
            Console.WriteLine($"warning   height window: {e.InnerException?.Message ?? e.Message}");
        }
    }

    /// <summary>
    /// Starts the window the eye needs: a standby already made for it is swapped in at once; otherwise a read, or a standby for a kept point.
    /// </summary>
    void RequestWindow(Vector3 eye)
    {
        bool idle = _job is null && !_swapping && !_standbys.Uploading;
        if (idle && NeedsWindow(_terrain.Fine, eye, out int column0, out int row0))
        {
            // A standby round where the eye now is (it jumped to a kept character): drawn at once, the window it replaces kept as a standby for where
            // the eye was.
            if (_standbys.TrySwapIn(s => !NeedsWindow(s.Window, eye, out _, out _)))
            {
                HeightWindows++;
            }
            else
            {
                Read(column0, row0, standby: false);
            }
        }
        else if (idle && _step < WorldFrame.CoarseStep && Guard is not { Pressure: true })
        {
            PrepareStandby();
        }
    }

    /// <summary>
    /// Reads the window from (<paramref name="column0"/>, <paramref name="row0"/>) on the worker, with its bounds: the drawn window, or a standby.
    /// </summary>
    void Read(int column0, int row0, bool standby)
    {
        var watch = Stopwatch.StartNew();
        _jobStandby = standby;
        _job = BackgroundWork.Run(() =>
        {
            var window = _map.ReadWindow(column0, row0, _cells + 1, _cells + 1, _step);
            var bounds = _terrain.MeasureBounds(window);
            LastWindowMs = watch.Elapsed.TotalMilliseconds;
            return (window, bounds);
        });
    }

    /// <summary>
    /// Starts reading a standby window for the first kept point that needs one (<see cref="HeightStandbys.TryPickPoint"/>).
    /// </summary>
    void PrepareStandby()
    {
        if (!_standbys.TryPickPoint(out var point)) return;

        if (NeedsWindow(_terrain.Fine, point, out int column0, out int row0)) Read(column0, row0, standby: true);
    }

    /// <summary>
    /// Whether <paramref name="p"/> lies within <paramref name="fraction"/> of <paramref name="w"/>'s width of its centre (horizontally).
    /// </summary>
    internal static bool Within(HeightWindow w, Vector3 p, double fraction)
    {
        var (x0, z0) = w.WorldOf(0, 0);
        double width = (w.Columns - 1) * (double)w.Spacing;
        double dx = Math.Abs(p.X - (x0 + width / 2));
        double dz = Math.Abs(p.Z - (z0 + (w.Rows - 1) * (double)w.Spacing / 2));
        return Math.Max(dx, dz) <= width * fraction;
    }

    /// <summary>
    /// Whether <paramref name="eye"/> is outside the middle part of <paramref name="fine"/>, and where a window centred on it would start.
    /// </summary>
    bool NeedsWindow(HeightWindow fine, Vector3 eye, out int column0, out int row0)
    {
        (column0, row0) = (0, 0);
        if (_step >= WorldFrame.CoarseStep) return false;   // as coarse as the whole-world grid: nothing to gain
        if (Within(fine, eye, RecentreFraction)) return false;

        int align = _step * 64, span = _cells * _step, max = _map.Size - 1 - span;
        var (sc, sr) = WorldLayout.ToSample(eye.X, eye.Z);
        int Axis(double s) => Math.Clamp((int)Math.Round((s - span / 2.0) / align) * align, 0, Math.Max(max, 0));
        (column0, row0) = (Axis(sc), Axis(sr));

        // Already there (the eye is past the edge of the world, or the window covers it all).
        return column0 != fine.Column0 || row0 != fine.Row0 || fine.Columns != _cells + 1;
    }
}
