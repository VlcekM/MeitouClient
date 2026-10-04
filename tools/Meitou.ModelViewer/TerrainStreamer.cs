using System.Diagnostics;
using System.Numerics;
using Meitou.Content;
using Meitou.Data.World;

namespace Meitou.ModelViewer;

/// <summary>
/// GL work cut into steps (a slab of a texture, a swap) that run a few at a time on the render thread, so streaming
/// data in never costs a frame more than the budget (a step is at most about 2 MB of upload).
/// </summary>
public sealed class UploadQueue
{
    readonly Queue<(Action Step, string Label)> steps = new();

    /// <summary>Set MEITOU_STREAM_LOG=1 to print every step that takes more than 3 ms.</summary>
    static readonly bool Log = Environment.GetEnvironmentVariable("MEITOU_STREAM_LOG") == "1";

    public int Count => steps.Count;

    /// <summary>Labels of the steps the last <see cref="Run"/> executed (for diagnosing slow frames).</summary>
    public string LastRun { get; private set; } = "";

    public void Add(Action step, string label = "step") => steps.Enqueue((step, label));

    /// <summary>Runs queued steps until <paramref name="budgetMs"/> has passed (always at least one, if any).</summary>
    public void Run(double budgetMs)
    {
        LastRun = "";
        var watch = Stopwatch.StartNew();
        while (steps.TryDequeue(out var item))
        {
            var one = Stopwatch.StartNew();
            item.Step();
            LastRun += item.Label + ", ";
            if (Log && one.Elapsed.TotalMilliseconds > 3) Console.WriteLine($"slow step {item.Label}: {one.Elapsed.TotalMilliseconds:0.0} ms");
            if (watch.Elapsed.TotalMilliseconds >= budgetMs) break;
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
    bool swapping;

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

    public bool Idle => job is null && !swapping && uploads.Count == 0 && (textures?.Idle ?? true);

    /// <summary>Work in flight (decodes, window reads, queued upload steps), for the window title.</summary>
    public int Pending => (job is null ? 0 : 1) + uploads.Count + (textures?.Outstanding ?? 0);

    /// <summary>Render-thread time of the last <see cref="Update(Vector3)"/>.</summary>
    public double LastUpdateMs { get; private set; }
    public string LastSteps => uploads.LastRun;

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
                swapping = true;
                terrain.BeginFineUpload(window, bounds, uploads, () => { swapping = false; HeightWindows++; });
            }
            catch (AggregateException e)
            {
                Console.WriteLine($"warning   height window: {e.InnerException?.Message ?? e.Message}");
            }
        }
        if (job is null && !swapping && NeedsWindow(eye, out int column0, out int row0))
        {
            var watch = Stopwatch.StartNew();
            job = Task.Run(() =>
            {
                var window = map.ReadWindow(column0, row0, cells + 1, cells + 1, step);
                var bounds = terrain.MeasureBounds(window);
                LastWindowMs = watch.Elapsed.TotalMilliseconds;
                return (window, bounds);
            });
        }
        textures?.Update(eye, MaterialDistance, uploads);
        uploads.Run(budgetMs);
    }

    /// <summary>Whether the eye is outside the middle part of the current window, and where a window centred on it would start.</summary>
    bool NeedsWindow(Vector3 eye, out int column0, out int row0)
    {
        (column0, row0) = (0, 0);
        if (step >= WorldApp.CoarseStep) return false;   // as coarse as the whole-world grid: nothing to gain
        var fine = terrain.Fine;
        var (x0, z0) = fine.WorldOf(0, 0);
        double width = (fine.Columns - 1) * (double)fine.Spacing;
        double dx = Math.Abs(eye.X - (x0 + width / 2)), dz = Math.Abs(eye.Z - (z0 + (fine.Rows - 1) * (double)fine.Spacing / 2));
        (column0, row0) = (0, 0);
        if (Math.Max(dx, dz) <= width * RecentreFraction) return false;
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
        map.Dispose();
    }
}
