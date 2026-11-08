using System.Diagnostics;
using System.Numerics;
using Meitou.Content;
using Meitou.Data.World;

namespace Meitou.Rendering;

/// <summary>
/// GL work cut into steps (a slab of a texture, a swap) that run a few at a time on the render thread, so streaming
/// data in never costs a frame more than the budget (a step is at most about 2 MB of upload). The first step of a
/// <see cref="Run"/> always runs; after it, a step starts only if the budget has not passed and what steps of its label have cost lately
/// (<see cref="StepCosts"/>) still fits in what is left, so a step known to be heavy waits for a frame of its own instead of
/// running on top of others.
/// </summary>
public sealed class UploadQueue
{
    readonly Queue<(Action Step, string Label)> steps = new();
    readonly StepCosts costs = new();
    readonly Func<double> clock;

    /// <param name="clockMs">Milliseconds on any scale that only moves forward (tests pass their own); the real time by default.</param>
    public UploadQueue(Func<double>? clockMs = null) => clock = clockMs ?? (() => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency);

    public int Count => steps.Count;

    /// <summary>Labels of the steps the last <see cref="Run"/> executed (for diagnosing slow frames).</summary>
    public string LastRun { get; private set; } = "";

    /// <summary>What steps with <paramref name="label"/> have cost lately, in milliseconds (0 before the first).</summary>
    public double ExpectedMs(string label) => costs.Expected(label);

    public void Add(Action step, string label = "step") => steps.Enqueue((step, label));

    /// <summary>Runs queued steps until <paramref name="budgetMs"/> has passed or the next one is expected to end more than <see cref="StepCosts.Slack"/> past it (always at least one, if any).</summary>
    public void Run(double budgetMs)
    {
        LastRun = "";
        double start = clock();
        int ran = 0;
        while (steps.TryPeek(out var next))
        {
            double elapsed = clock() - start;
            if (ran > 0 && (elapsed >= budgetMs || !costs.Fits(next.Label, elapsed, budgetMs))) break;
            var item = steps.Dequeue();
            double began = clock();
            item.Step();
            double ms = clock() - began;
            costs.Learn(item.Label, ms);
            ran++;
            LastRun += item.Label + ", ";
            if (ms > 3) SpikeLog.Note($"slow terrain step {item.Label}", ms: ms);   // with --log-spikes
        }
    }
}

/// <summary>
/// Makes the terrain's detail follow the camera. The fine height window (<see cref="HeightWindow"/>, the finest
/// heightmap samples) is re-read from <c>fullmap.tif</c> around the eye on a worker thread when the eye has left the
/// middle of the current one, and swapped in when uploaded; the old one is used until then. The biome layers and the
/// overlay/colour windows stream the same way inside <see cref="TerrainTextures"/>.
/// </summary>
public sealed class TerrainStreamer : IDisposable
{
    /// <summary>Cells per side of a streamed height window (at least; the starting window's size if larger).</summary>
    public const int WindowCells = 1536;

    /// <summary>The eye may drift this fraction of the window's width from its centre before a new window is read.</summary>
    const double RecentreFraction = 0.15;

    readonly TerrainRenderer terrain;
    readonly TerrainTextures? textures;
    readonly UploadQueue uploads = new();
    readonly TerrainHeightmap map;
    readonly int cells, step;
    Task<(HeightWindow Window, TerrainHeightBounds.Patch Bounds)>? job;
    /// <summary>The standby the running job (and its upload) makes, or null: the job makes the drawn window.</summary>
    bool jobStandby;
    bool swapping, standbyUploading;
    /// <summary>Uploaded windows round kept points far from the eye's window (<see cref="Kept"/>), swapped in when the eye arrives.</summary>
    readonly List<TerrainRenderer.FineStandby> standbys = [];

    /// <summary>At most this many standby windows (about 5 MB of video memory and 5 MB of memory each at step 1): one per kept place
    /// (<see cref="Areas"/> less the eye's), at least 3, at most <see cref="StandbyCeiling"/>.</summary>
    int MaxStandbys => Math.Clamp(Areas - 1, 3, StandbyCeiling);
    const int StandbyCeiling = 8;

    /// <summary>Places kept loaded (<see cref="WorldTextureCache.Areas"/>): the standby counts and the terrain layers' near slots scale with it.</summary>
    public int Areas { get; set; } = 1;

    /// <summary>A kept point this near the drawn window's centre (a fraction of its width) needs no standby: a jump there keeps the
    /// window's fine heights in view while the window moves.</summary>
    const double CoverFraction = 0.3;

    /// <summary>Distance within which the full terrain material is wanted (biomes are loaded for the cells inside it).</summary>
    public float MaterialDistance { get; set; } = 30000;
    /// <summary>Render-thread time per frame spent on uploads.</summary>
    public double BudgetMs { get; set; } = 2;
    public int HeightWindows { get; private set; }
    public double LastWindowMs { get; private set; }

    public TerrainStreamer(GameInstall install, TerrainRenderer terrain, TerrainTextures? textures, int step)
    {
        this.terrain = terrain;
        this.textures = textures;
        this.step = Math.Max(step, 1);
        cells = Math.Max(WindowCells, terrain.Fine.Columns - 1);
        // A second handle on the file: the worker must not share the render thread's stream.
        map = TerrainHeightmap.Open(install);
    }

    /// <summary>A line about what is loaded, for the log.</summary>
    public string Describe() =>
        $"height windows {HeightWindows}" + (textures is null ? "" :
            $", biomes {textures.ResidentBiomes}/{textures.TotalBiomes} resident ({string.Join(", ", textures.Resident.Select(b => b.Name.Trim()))}), overlay/colour maps {(textures.MapState == 2 ? "loaded" : "missing")}");

    // A standby's read is not waited for (a far squad member does not hold up the loading screen).
    public bool Idle => (job is null || jobStandby) && !swapping && uploads.Count == 0 && (textures?.Idle ?? true);

    /// <summary>Work in flight (decodes, window reads, queued upload steps), for the window title.</summary>
    public int Pending => (job is null ? 0 : 1) + uploads.Count + (textures?.Outstanding ?? 0);

    /// <summary>Render-thread time of the last <see cref="Update(Vector3)"/>.</summary>
    public double LastUpdateMs { get; private set; }
    public string LastSteps => uploads.LastRun;

    /// <summary>A character flight's arrival eye: the biome textures there load during the flight (the height window still moves only with the eye).</summary>
    public Vector3? Ahead { get; set; }

    /// <summary>Points whose surrounding biome textures, colour and overlay windows and fine heights stay loaded besides the eye's ("Keep textures at characters").</summary>
    public IReadOnlyList<Vector3> Kept { get; set; } = [];
    /// <summary>Video memory pressure: while it is on no standby window is made.</summary>
    public VramGuard? Guard { get; set; }
    /// <summary>The ground's height under the eye, for which biome textures load (<see cref="TerrainTextures.GroundUnderEye"/>).</summary>
    public float GroundUnderEye { get; set; } = float.NegativeInfinity;

    public void Update(Vector3 eye)
    {
        var watch = Stopwatch.StartNew();
        Update(eye, BudgetMs);
        LastUpdateMs = watch.Elapsed.TotalMilliseconds;
    }

    void Update(Vector3 eye, double budgetMs)
    {
        if (job is { IsCompleted: true } done)
        {
            job = null;
            try
            {
                var (window, bounds) = done.Result;
                if (jobStandby)
                {
                    standbyUploading = true;
                    terrain.BeginFineStandby(window, bounds, uploads, s => { standbys.Add(s); standbyUploading = false; });
                }
                else
                {
                    swapping = true;
                    terrain.BeginFineUpload(window, bounds, uploads, () => { swapping = false; HeightWindows++; });
                }
            }
            catch (AggregateException e)
            {
                Console.WriteLine($"warning   height window: {e.InnerException?.Message ?? e.Message}");
            }
        }
        if (job is null && !swapping && !standbyUploading && NeedsWindow(terrain.Fine, eye, out int column0, out int row0))
        {
            // A standby round where the eye now is (it jumped to a kept character): drawn at once, the window it replaces kept as a standby for where the eye was.
            int ready = standbys.FindIndex(s => !NeedsWindow(s.Window, eye, out _, out _));
            if (ready >= 0) { standbys[ready] = terrain.SwapFine(standbys[ready]); HeightWindows++; }
            else Read(column0, row0, standby: false);
        }
        else if (job is null && !swapping && !standbyUploading && step < WorldFrame.CoarseStep && Guard is not { Pressure: true }) PrepareStandby();
        if (textures is not null) (textures.GroundUnderEye, textures.Guard, textures.Areas) = (GroundUnderEye, Guard, Areas);
        textures?.Update(eye, MaterialDistance, uploads, Ahead, Kept);
        uploads.Run(budgetMs);
    }

    void Read(int column0, int row0, bool standby)
    {
        var watch = Stopwatch.StartNew();
        jobStandby = standby;
        job = BackgroundWork.Run(() =>
        {
            var window = map.ReadWindow(column0, row0, cells + 1, cells + 1, step);
            var bounds = terrain.MeasureBounds(window);
            LastWindowMs = watch.Elapsed.TotalMilliseconds;
            return (window, bounds);
        });
    }

    /// <summary>
    /// Starts reading a standby window for the first kept point that is neither near the drawn window's centre nor inside a standby's middle, in place of
    /// a standby no kept point is in the middle of (or a new one, up to <see cref="MaxStandbys"/>).
    /// </summary>
    void PrepareStandby()
    {
        foreach (var point in Kept)
        {
            if (Within(terrain.Fine, point, CoverFraction) || standbys.Any(s => Within(s.Window, point, RecentreFraction))) continue;
            if (standbys.Count >= MaxStandbys)
            {
                int idle = standbys.FindIndex(s => !Kept.Any(k => Within(s.Window, k, RecentreFraction)));
                if (idle < 0) return;
                terrain.DropFine(standbys[idle]);
                standbys.RemoveAt(idle);
            }
            if (NeedsWindow(terrain.Fine, point, out int column0, out int row0)) Read(column0, row0, standby: true);
            return;
        }
    }

    static bool Within(HeightWindow w, Vector3 p, double fraction)
    {
        var (x0, z0) = w.WorldOf(0, 0);
        double width = (w.Columns - 1) * (double)w.Spacing;
        double dx = Math.Abs(p.X - (x0 + width / 2)), dz = Math.Abs(p.Z - (z0 + (w.Rows - 1) * (double)w.Spacing / 2));
        return Math.Max(dx, dz) <= width * fraction;
    }

    /// <summary>Whether <paramref name="eye"/> is outside the middle part of <paramref name="fine"/>, and where a window centred on it would start.</summary>
    bool NeedsWindow(HeightWindow fine, Vector3 eye, out int column0, out int row0)
    {
        (column0, row0) = (0, 0);
        if (step >= WorldFrame.CoarseStep) return false;   // as coarse as the whole-world grid: nothing to gain
        if (Within(fine, eye, RecentreFraction)) return false;
        int align = step * 64, span = cells * step, max = map.Size - 1 - span;
        var (sc, sr) = WorldLayout.ToSample(eye.X, eye.Z);
        int Axis(double s) => Math.Clamp((int)Math.Round((s - span / 2.0) / align) * align, 0, Math.Max(max, 0));
        (column0, row0) = (Axis(sc), Axis(sr));
        // Already there (the eye is past the edge of the world, or the window covers it all).
        return column0 != fine.Column0 || row0 != fine.Row0 || fine.Columns != cells + 1;
    }

    /// <summary>Waits for everything wanted around the eye to be loaded and uploaded (offscreen rendering).</summary>
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

    public void Dispose()
    {
        try { job?.Wait(5000); } catch (AggregateException) { }
        foreach (var s in standbys) terrain.DropFine(s);
        standbys.Clear();
        map.Dispose();
    }
}
