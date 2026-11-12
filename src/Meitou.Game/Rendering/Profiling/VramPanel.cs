using System.Numerics;

using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Overlays;

namespace Meitou.Game.Rendering.Profiling;

/// <summary>
/// The profiler's video memory view: the VRAM pie at the top right (Shift+F12 in the game), the VRAM and RAM figures of the chart's header, and
/// the VRAM dump (Ctrl+Shift+F12). The driver's figures are read every 30 frames, only while something shows them.
/// </summary>
sealed class VramPanel(GpuContext native)
{
    /// <summary>
    /// The slices' colours in order, the last for every slice past it.
    /// </summary>
    static readonly Vector4[] PieColours =
    [
        new(0.35f, 0.60f, 1.00f, 1), new(0.95f, 0.55f, 0.25f, 1), new(0.40f, 0.85f, 0.40f, 1), new(0.95f, 0.35f, 0.35f, 1),
        new(0.65f, 0.45f, 0.95f, 1), new(0.95f, 0.85f, 0.35f, 1), new(0.25f, 0.80f, 0.85f, 1), new(0.95f, 0.55f, 0.85f, 1),
        new(0.55f, 0.55f, 0.55f, 1), new(0.35f, 0.35f, 0.40f, 1), new(0.20f, 0.20f, 0.22f, 1),
    ];

    /// <summary>
    /// The pie's slices, by owner (<see cref="VramSlices"/>), as last read.
    /// </summary>
    List<(string Name, ulong Bytes)> _vramSlices = [];

    /// <summary>
    /// The driver's VRAM use and budget, as last read.
    /// </summary>
    (ulong Used, ulong Budget) _vram;

    /// <summary>
    /// Frames until the figures are read again.
    /// </summary>
    int _vramAge;

    /// <summary>
    /// The process's working set and managed heap, as last read.
    /// </summary>
    (long WorkingSet, long Managed) _ram;

    /// <summary>
    /// The VRAM pie at the top right is shown (Shift+F12 in the game), with the profiler chart or without it.
    /// </summary>
    public bool Show { get; set; }

    /// <summary>
    /// The driver's VRAM use and budget, as last read (<see cref="Refresh"/>).
    /// </summary>
    public (ulong Used, ulong Budget) Vram => _vram;

    /// <summary>
    /// The process's working set and managed heap, as last read (<see cref="Refresh"/>).
    /// </summary>
    public (long WorkingSet, long Managed) Ram => _ram;

    /// <summary>
    /// Writes what is in video memory now to <paramref name="path"/> (Ctrl+Shift+F12 in the game): the driver's usage and budget, the room in our
    /// blocks, the owners, then every resource and block (<see cref="Gpu.Core.GpuAllocator.WriteReport"/>). What the driver counts beyond our blocks
    /// (swapchain, DLSS, its own) is only a total here: a RenderDoc or Nsight capture lists it.
    /// </summary>
    public void Dump(string path, Action<TextWriter>? more = null)
    {
        var alloc = native.Device.Allocator;
        var (used, budget) = native.Device.VideoMemory();
        var (empty, spare, scattered, partBlocks) = alloc.Slack();
        ulong blocks = alloc.TotalAllocatedBytes;
        using var w = new StreamWriter(path);
        w.WriteLine($"VRAM dump {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        w.WriteLine($"driver: used {used / 1048576.0:0.0} MB of budget {budget / 1048576.0:0.0} MB " +
            $"(memory budget extension: {native.Device.HasMemoryBudget})");
        var heaps = native.Device.Heaps();
        for (int i = 0; i < heaps.Count; i++)
        {
            w.WriteLine($"  heap {i}: {heaps[i].Size / 1048576.0:0} MB, ours {heaps[i].Usage / 1048576.0:0.0} MB " +
                $"of budget {heaps[i].Budget / 1048576.0:0.0} MB ({heaps[i].Flags})");
        }

        ulong local = (ulong)alloc.Breakdown().Sum(o => (double)o.DeviceLocal) + empty + spare + scattered;
        string ours = $"ours: {blocks / 1048576.0:0.0} MB in {alloc.BlockCount} blocks ({local / 1048576.0:0.0} MB device-local), " +
            $"{alloc.TotalUsedBytes / 1048576.0:0.0} MB used by {alloc.AllocationCount} allocations; ";
        w.WriteLine(ours + $"driver and others {(used > local ? used - local : 0) / 1048576.0:0.0} MB");
        w.WriteLine($"room in device-local blocks: empty {empty / 1048576.0:0.0} MB, spare {spare / 1048576.0:0.0} MB, " +
            $"scattered {scattered / 1048576.0:0.0} MB in {partBlocks} blocks");
        w.WriteLine();
        w.WriteLine("owners (MB, device-local MB, count):");
        foreach (var o in alloc.Breakdown()) w.WriteLine($"{o.Bytes / 1048576.0,10:0.0}  {o.DeviceLocal / 1048576.0,10:0.0}  {o.Count,6}  {o.Name}");
        w.WriteLine();
        more?.Invoke(w);
        alloc.WriteReport(w);
    }

    /// <summary>
    /// The VRAM pie at the top right, while <see cref="Show"/>.
    /// </summary>
    public void Draw(DebugOverlay overlay, int width, int height)
    {
        if (!Show || width <= 0 || height <= 0) return;

        Refresh();
        DrawVramPie(overlay, width);
        overlay.Flush(width, height);
    }

    /// <summary>
    /// VRAM (a driver call) and RAM are read every 30 frames, for the chart's header and the pie; only while either is shown.
    /// </summary>
    public void Refresh()
    {
        if (_vramAge-- <= 0)
        {
            _vram = native.Device.VideoMemory();
            _vramSlices = VramSlices();
            _ram = (Environment.WorkingSet, GC.GetTotalMemory(false));
            _vramAge = 30;
        }
    }

    /// <summary>
    /// The VRAM by owner: the eight largest device-local owners of <see cref="Gpu.Core.GpuAllocator.Breakdown"/>, the rest of ours, the unused room
    /// in our blocks, and what the driver counts beyond our blocks (its own allocations, swapchain, other APIs' resources such as DLSS's).
    /// </summary>
    List<(string Name, ulong Bytes)> VramSlices()
    {
        var alloc = native.Device.Allocator;
        var owners = alloc.Breakdown().Where(o => o.DeviceLocal > 0).OrderByDescending(o => o.DeviceLocal).ToList();
        var slices = owners.Take(8).Select(o => (o.Name, o.DeviceLocal)).ToList();
        ulong ours = (ulong)owners.Sum(o => (double)o.DeviceLocal), shown = (ulong)slices.Sum(s => (double)s.DeviceLocal);
        if (ours > shown) slices.Add(("other of ours", ours - shown));

        // Device-local blocks only: the host-visible ones (staging, constants) are system memory, not in the driver's VRAM figure.
        var (empty, spare, scattered, _) = alloc.Slack();
        ulong free = empty + spare + scattered, blocks = ours + free;
        if (free > 0) slices.Add(("free in our blocks", free));
        if (_vram.Used > blocks) slices.Add(("driver and others", _vram.Used - blocks));
        return slices;
    }

    /// <summary>
    /// The pie and its legend: a slice per owner, the figures beside it.
    /// </summary>
    void DrawVramPie(DebugOverlay overlay, int width)
    {
        if (_vramSlices.Count == 0) return;

        double total = _vramSlices.Sum(s => (double)s.Bytes);
        if (total <= 0) return;

        const float margin = 16, pad = 12, radius = 70;
        float lh = overlay.LineHeight, cw = overlay.CharWidth;
        float legendW = 2 * cw + 30 * cw;
        float panelW = pad * 3 + 2 * radius + legendW, panelH = Math.Max(2 * radius, (_vramSlices.Count + 1.5f) * lh) + 2 * pad;
        float x1 = width - margin, x0 = x1 - panelW, y0 = margin;
        overlay.Rect(x0, y0, x1, y0 + panelH, DebugOverlay.PanelColour);
        var centre = new Vector2(x0 + pad + radius, y0 + pad + Math.Max(radius, (panelH - 2 * pad) / 2));
        double angle = -Math.PI / 2;
        float lx = x0 + pad * 2 + 2 * radius, ly = y0 + pad;
        overlay.Text($"VRAM {_vram.Used / 1073741824.0:0.00} of {_vram.Budget / 1073741824.0:0.0} GB", lx, ly, DebugOverlay.TextColour);
        for (int i = 0; i < _vramSlices.Count; i++)
        {
            var (name, bytes) = _vramSlices[i];
            var colour = PieColours[Math.Min(i, PieColours.Length - 1)];
            double sweep = bytes / total * Math.Tau;
            int steps = Math.Max(1, (int)Math.Ceiling(sweep / (Math.Tau / 96)));
            for (int k = 0; k < steps; k++)
            {
                double a0 = angle + sweep * k / steps, a1 = angle + sweep * (k + 1) / steps;
                overlay.Triangle(centre, centre + radius * new Vector2((float)Math.Cos(a0), (float)Math.Sin(a0)),
                    centre + radius * new Vector2((float)Math.Cos(a1), (float)Math.Sin(a1)), colour);
            }

            angle += sweep;
            float top = ly + (i + 1.5f) * lh;
            overlay.Rect(lx, top + 4, lx + 10, top + 14, colour);
            string label = name.Length > 20 ? name[..20] : name;
            overlay.Text($"{label,-20}{bytes / 1048576.0,7:0} MB", lx + 2 * cw, top, DebugOverlay.TextColour);
        }
    }
}
