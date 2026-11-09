using System.Globalization;
using System.Numerics;

using Meitou.Game.Rendering.Overlays;

namespace Meitou.Game.Rendering.Profiling;

/// <summary>
/// What <see cref="ProfilerLegend"/> reads of the <see cref="FrameProfiler"/>'s timings this frame: both sides' histories, upd-chars's parts and
/// the simulation's rows (the profiler's own arrays and lists, not copies).
/// </summary>
readonly struct LegendTimings
{
    /// <summary>
    /// The render thread's history per series.
    /// </summary>
    public required float[][] Cpu { get; init; }

    /// <summary>
    /// The index after the newest entry of <see cref="Cpu"/>.
    /// </summary>
    public required int CpuHead { get; init; }

    /// <summary>
    /// How many frames <see cref="Cpu"/> holds.
    /// </summary>
    public required int CpuCount { get; init; }

    /// <summary>
    /// The GPU's history per series.
    /// </summary>
    public required float[][] Gpu { get; init; }

    /// <summary>
    /// The index after the newest entry of <see cref="Gpu"/>.
    /// </summary>
    public required int GpuHead { get; init; }

    /// <summary>
    /// How many frames <see cref="Gpu"/> holds (0 before the first GPU times are read).
    /// </summary>
    public required int GpuCount { get; init; }

    /// <summary>
    /// The render thread's ms per part of upd-chars (<see cref="StageClock.PartNames"/>), kept with <see cref="Cpu"/>.
    /// </summary>
    public required float[][] Parts { get; init; }

    /// <summary>
    /// The world ticks per frame, kept with <see cref="Cpu"/>.
    /// </summary>
    public required float[] SimTicks { get; init; }

    /// <summary>
    /// The sim rows to list (the phases first, then the costliest systems and the systems' parts), each with its mean ms.
    /// </summary>
    public required List<(string Name, double Ms)> SimRows { get; init; }

    /// <summary>
    /// How many of <see cref="SimRows"/> lead as phases (the last of them the time outside the world's ticks).
    /// </summary>
    public required int SimPhases { get; init; }

    /// <summary>
    /// The paths waiting in the simulation.
    /// </summary>
    public required int SimPaths { get; init; }

    /// <summary>
    /// The simulation reported its parts, so the sim row opens.
    /// </summary>
    public required bool SimExpands { get; init; }
}

/// <summary>
/// The profiler chart's legend at the right: swatch, row, mean of the last frames on the CPU and the GPU. A row marked + opens (a click), - closes; a
/// closed group shows its stages' sum and is plotted as one line, an open one plots each stage.
/// </summary>
sealed class ProfilerLegend
{
    /// <summary>
    /// What a click on a row opens besides a group (its index) or nothing (-1): upd-chars's parts, the simulation's rows.
    /// </summary>
    const int PartsRow = -2, SimRow = -3;

    /// <summary>
    /// A row's colour swatch, in pixels.
    /// </summary>
    const float Swatch = 10;

    /// <summary>
    /// The groups (<see cref="FrameProfiler.Groups"/>) open in the legend.
    /// </summary>
    readonly bool[] _groupOpen = new bool[FrameProfiler.Groups.Length];

    /// <summary>
    /// upd-chars's parts and the simulation's rows are listed.
    /// </summary>
    bool _partsOpen, _simOpen;

    /// <summary>
    /// Each row drawn last: its top and what a click on it opens (a group, <see cref="PartsRow"/>, <see cref="SimRow"/> or nothing, -1).
    /// </summary>
    readonly List<(float Top, int Opens)> _rows = [];

    /// <summary>
    /// The legend's box as drawn last, and its line height.
    /// </summary>
    (float X0, float Y0, float X1, float Y1, float LineHeight) _box;

    /// <summary>
    /// A closed group's summed series on the side shown.
    /// </summary>
    readonly float[][] _groupSeries = FrameProfiler.Groups.Select(_ => new float[FrameProfiler.History]).ToArray();

    /// <summary>
    /// The overlay the legend draws with now.
    /// </summary>
    DebugOverlay _overlay = null!;

    /// <summary>
    /// The timings it reads now.
    /// </summary>
    LegendTimings _data;

    /// <summary>
    /// The series to plot, filled as the rows are drawn.
    /// </summary>
    List<(float[] Series, Vector4 Colour, float Thickness, int Head, int Count)> _plotted = null!;

    /// <summary>
    /// The chart shows the GPU side.
    /// </summary>
    bool _showGpu;

    /// <summary>
    /// The side shown's history.
    /// </summary>
    float[][] _hist = [];

    /// <summary>
    /// The index after the newest entry of the side shown, and how many frames it holds.
    /// </summary>
    int _head, _count;

    /// <summary>
    /// The frames the means are over.
    /// </summary>
    int _meanFrames;

    /// <summary>
    /// The legend's left edge, its column header's top and the overlay's line height.
    /// </summary>
    float _lx, _ly, _lh;

    /// <summary>
    /// The simulation's rows are listed (the profiler then works them out before drawing).
    /// </summary>
    public bool SimOpen => _simOpen;

    /// <summary>
    /// The legend's left edge as drawn last (the plot ends short of it).
    /// </summary>
    public float Left => _box.X0;

    /// <summary>
    /// The rows the legend lists with upd-chars's <paramref name="partCount"/> parts and <paramref name="simRowCount"/> sim rows (for the panel's
    /// height).
    /// </summary>
    public int RowCount(int partCount, int simRowCount)
    {
        int rowCount = 0;
        for (int gi = 0; gi < FrameProfiler.Groups.Length; gi++)
        {
            var stages = FrameProfiler.Groups[gi].Stages;
            if (stages.Length > 1) rowCount++;
            if (stages.Length == 1 || _groupOpen[gi])
            {
                foreach (int s in stages)
                {
                    rowCount += 1;
                    if (s == StageClock.UpdChars && _partsOpen) rowCount += partCount;
                    if (s == StageClock.Sim && _simOpen) rowCount += simRowCount + 2;
                }
            }
        }

        return rowCount;
    }

    /// <summary>
    /// A left click at <paramref name="at"/> (framebuffer pixels): on a row marked + or - it opens or closes that row. True when it was on the
    /// legend.
    /// </summary>
    public bool Click(Vector2 at)
    {
        var (bx0, by0, bx1, by1, lh) = _box;
        if (at.X < bx0 || at.X > bx1 || at.Y < by0 || at.Y > by1) return false;

        foreach (var (top, opens) in _rows)
        {
            if (at.Y < top || at.Y >= top + lh) continue;

            if (opens >= 0) _groupOpen[opens] = !_groupOpen[opens];
            else if (opens == PartsRow) _partsOpen = !_partsOpen;
            else if (opens == SimRow) _simOpen = !_simOpen;
            break;
        }

        return true;
    }

    /// <summary>
    /// Draws the legend left of <paramref name="x1"/> below the panel's top <paramref name="y0"/>, and fills <paramref name="plotted"/> with the
    /// series its rows plot.
    /// </summary>
    public void Draw(DebugOverlay overlay, bool showGpu, float x1, float y0, int meanFrames, in LegendTimings data,
        List<(float[] Series, Vector4 Colour, float Thickness, int Head, int Count)> plotted)
    {
        Begin(overlay, showGpu, x1, y0, meanFrames, in data, plotted);
        for (int gi = 0; gi < FrameProfiler.Groups.Length; gi++) DrawGroup(gi);

        _box = (_lx, _ly, x1 - FrameProfiler.Pad, _ly + (_rows.Count + 1) * _lh, _lh);
    }

    /// <summary>
    /// Takes this draw's inputs, places the legend, draws its column header and starts the rows and the plotted series afresh.
    /// </summary>
    void Begin(DebugOverlay overlay, bool showGpu, float x1, float y0, int meanFrames, in LegendTimings data,
        List<(float[] Series, Vector4 Colour, float Thickness, int Head, int Count)> plotted)
    {
        (_overlay, _data, _plotted, _showGpu, _meanFrames) = (overlay, data, plotted, showGpu, meanFrames);
        float lh = overlay.LineHeight, cw = overlay.CharWidth;
        _lh = lh;
        _hist = showGpu ? data.Gpu : data.Cpu;
        (_head, _count) = (showGpu ? data.GpuHead : data.CpuHead, showGpu ? data.GpuCount : data.CpuCount);
        float legendW = Swatch + 6 + (16 + 10 + 10) * cw;
        (_lx, _ly) = (x1 - FrameProfiler.Pad - legendW, y0 + FrameProfiler.Pad + 1.5f * lh);
        overlay.Text($"{"",-16}{"cpu",10}{"gpu",10}", _lx + Swatch + 6, _ly, FrameProfiler.DimColour);
        _rows.Clear();
        _plotted.Clear();
    }

    /// <summary>
    /// A group's rows: a stage of its own as one row; else the group's row (its stages' sums, plotted as one line while closed), then its stages'
    /// rows while open.
    /// </summary>
    void DrawGroup(int gi)
    {
        var (name, stages) = FrameProfiler.Groups[gi];
        if (stages.Length == 1)
        {
            StageRow(stages[0], "");
            return;
        }

        var (c, g, anyCpu, anyGpu) = GroupMeans(stages);
        float top = NextRow(gi);
        bool open = _groupOpen[gi], shown = _showGpu ? anyGpu : anyCpu;
        var sum = SumGroupSeries(gi, stages);

        if (!open)
        {
            _overlay.Rect(_lx, top + 4, _lx + Swatch, top + 4 + Swatch, FrameProfiler.Colours[stages[0]]);
            if (shown) _plotted.Add((sum, FrameProfiler.Colours[stages[0]], 1.5f, _head, _count));
        }

        _overlay.Text($"{(open ? "- " : "+ ") + name,-16}{Ms(anyCpu, c),10}{Ms(_data.GpuCount > 0 && anyGpu, g),10}",
            _lx + Swatch + 6, top, DebugOverlay.TextColour);
        if (open)
        {
            foreach (int s in stages) StageRow(s, "  ");
        }
    }

    /// <summary>
    /// The sums of a group's stages' means on the CPU and the GPU, and whether any of them runs on each.
    /// </summary>
    (double Cpu, double Gpu, bool AnyCpu, bool AnyGpu) GroupMeans(int[] stages)
    {
        double c = 0, g = 0;
        bool anyCpu = false, anyGpu = false;
        foreach (int s in stages)
        {
            if (FrameProfiler.OnCpu(s))
            {
                c += FrameProfiler.Mean(_data.Cpu[s], _data.CpuHead, _data.CpuCount, _meanFrames);
                anyCpu = true;
            }

            if (FrameProfiler.OnGpu(s))
            {
                g += FrameProfiler.Mean(_data.Gpu[s], _data.GpuHead, _data.GpuCount, _meanFrames);
                anyGpu = true;
            }
        }

        return (c, g, anyCpu, anyGpu);
    }

    /// <summary>
    /// Group <paramref name="gi"/>'s summed series on the side shown (its stages that side plots), reused.
    /// </summary>
    float[] SumGroupSeries(int gi, int[] stages)
    {
        var sum = _groupSeries[gi];
        Array.Clear(sum);
        foreach (int s in stages)
        {
            if (Plots(s))
            {
                for (int i = 0; i < FrameProfiler.History; i++) sum[i] += _hist[s][i];
            }
        }

        return sum;
    }

    /// <summary>
    /// A stage's row: swatch, label and both sides' means, plotted when the side shown has it; then, while open, upd-chars's parts or the
    /// simulation's rows.
    /// </summary>
    void StageRow(int s, string indent)
    {
        bool expands = s == StageClock.UpdChars || s == StageClock.Sim && _data.SimExpands;
        bool open = s == StageClock.Sim ? _simOpen : _partsOpen;
        float top = NextRow(!expands ? -1 : s == StageClock.Sim ? SimRow : PartsRow);
        _overlay.Rect(_lx, top + 4, _lx + Swatch, top + 4 + Swatch, FrameProfiler.Colours[s]);
        if (Plots(s)) _plotted.Add((_hist[s], FrameProfiler.Colours[s], s == FrameProfiler.Total ? 2f : 1.5f, _head, _count));
        string label = indent + (expands ? open ? "- " : "+ " : "  ") + FrameProfiler.Label(s);
        string cpu = Ms(FrameProfiler.OnCpu(s), FrameProfiler.Mean(_data.Cpu[s], _data.CpuHead, _data.CpuCount, _meanFrames));
        string gpu = Ms(_data.GpuCount > 0 && FrameProfiler.OnGpu(s), FrameProfiler.Mean(_data.Gpu[s], _data.GpuHead, _data.GpuCount, _meanFrames));
        _overlay.Text($"{label,-16}{cpu,10}{gpu,10}", _lx + Swatch + 6, top, DebugOverlay.TextColour);
        if (!expands || !open) return;

        if (s == StageClock.Sim)
        {
            SimDetail(indent);
            return;
        }

        PartRows(indent);
    }

    /// <summary>
    /// Under sim: the world ticks a frame ran and the mean tick, the paths waiting, then the phases, the time outside the ticks and the costliest
    /// systems.
    /// </summary>
    void SimDetail(string indent)
    {
        var dim = FrameProfiler.DimColour;
        double ticks = FrameProfiler.Mean(_data.SimTicks, _data.CpuHead, _data.CpuCount, _meanFrames), inTicks = 0;
        for (int p = 0; p < _data.SimPhases - 1; p++) inTicks += _data.SimRows[p].Ms;
        string ticksText = string.Create(CultureInfo.InvariantCulture,
            $"{indent}    {ticks:0.0} ticks, {(ticks > 0 ? inTicks / ticks : 0):0.00} ms each");
        _overlay.Text(ticksText, _lx + Swatch + 6, NextRow(-1), dim);
        _overlay.Text($"{indent}    {_data.SimPaths} paths waiting", _lx + Swatch + 6, NextRow(-1), dim);
        foreach (var (name, ms) in _data.SimRows)
        {
            string row = indent + "    " + name;
            _overlay.Text($"{(row.Length > 16 ? row[..16] : row),-16}{Ms(true, ms),10}{"",10}", _lx + Swatch + 6, NextRow(-1), dim);
        }
    }

    /// <summary>
    /// Under upd-chars: its parts, render thread only (the GPU work they record is in the passes that run it); not plotted.
    /// </summary>
    void PartRows(string indent)
    {
        for (int p = 0; p < _data.Parts.Length; p++)
        {
            string partName = indent + "    " + StageClock.PartNames[p];
            string partMs = Ms(true, FrameProfiler.Mean(_data.Parts[p], _data.CpuHead, _data.CpuCount, _meanFrames));
            _overlay.Text($"{partName,-16}{partMs,10}{"",10}", _lx + Swatch + 6, NextRow(-1), FrameProfiler.DimColour);
        }
    }

    /// <summary>
    /// The next row's top, noted with what a click on it opens.
    /// </summary>
    float NextRow(int opens)
    {
        float top = _ly + (_rows.Count + 1) * _lh;
        _rows.Add((top, opens));
        return top;
    }

    /// <summary>
    /// Whether the side shown plots series <paramref name="s"/>.
    /// </summary>
    bool Plots(int s) => _showGpu ? FrameProfiler.OnGpu(s) : FrameProfiler.OnCpu(s);

    /// <summary>
    /// A mean as shown in a column: two decimals, or - where the side does not have it.
    /// </summary>
    static string Ms(bool shown, double ms) => shown ? ms.ToString("0.00", CultureInfo.InvariantCulture) : "-";
}
