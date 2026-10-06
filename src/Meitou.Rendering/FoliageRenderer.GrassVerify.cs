using System.Runtime.InteropServices;
using Meitou.Rendering.Gpu;

namespace Meitou.Rendering;

// The verify mode of the GPU grass (MEITOU_GPU_CULL_VERIFY=1): the CPU's draw list of each view against the GPU's, a frame ring later.
public sealed unsafe partial class FoliageRenderer
{
    sealed class GrassVerifyCall
    {
        public long Frame;
        public string Kind = "";
        public ReadbackBuffer Buffer = null!;
        public int MaxDraws;
        public (uint First, uint Shown, uint Vertices, float Distance)[] Expected = [];
    }

    readonly List<GrassVerifyCall> grassVerifyPending = [];
    long grassVerifyViews, grassVerifyDraws, grassVerifySet, grassVerifyOrder, grassVerifyLines;

    void GrassVerifyNote(string line)
    {
        if (grassVerifyLines++ < 30) Console.WriteLine($"foliage gpu grass verify: {line}");
    }

    /// <summary>Keeps the CPU's draws of this view (<see cref="grassDraws"/> or <see cref="motionDraws"/>, just prepared) and copies the GPU's for reading a frame ring later.</summary>
    void QueueGrassVerify(string kind, in GrassResult r)
    {
        var expected = kind == "colour"
            ? grassDraws.Select(d => (d.Buffer.FirstBlade, (uint)d.Buffer.Shown, d.Vertices, d.Distance)).ToArray()
            : motionDraws.Select(d => (d.Buffer.FirstBlade, (uint)d.Buffer.Shown, d.Vertices, 0f)).ToArray();
        var v = new GrassVerifyCall { Frame = Gpu.Frame.Number, Kind = kind, MaxDraws = r.MaxDraws, Expected = expected, Buffer = ReadbackBuffer.Create(Gpu, FoliageGrassGpu.ReadbackBytes(r), "foliage grass verify") };
        grassStore.CopyForReadback(r, v.Buffer);
        grassVerifyPending.Add(v);
    }

    void CheckGrassVerify(bool all)
    {
        for (int i = 0; i < grassVerifyPending.Count; i++)
        {
            var v = grassVerifyPending[i];
            if (!all && !ReadbackBuffer.Completed(Gpu, v.Frame)) continue;
            CompareGrass(v);
            v.Buffer.Dispose();
            grassVerifyPending.RemoveAt(i--);
        }
    }

    void CompareGrass(GrassVerifyCall v)
    {
        grassVerifyViews++;
        var all = MemoryMarshal.Cast<byte, uint>(v.Buffer.Read(0, 16 + (ulong)v.MaxDraws * 16));
        int count = (int)all[0];
        grassVerifyDraws += count;
        if (count != v.Expected.Length || count > v.MaxDraws)
        {
            grassVerifySet++;
            GrassVerifyNote($"frame {v.Frame} {v.Kind}: {count} draws on the GPU, {v.Expected.Length} on the CPU");
            return;
        }
        var byFirst = new Dictionary<uint, (uint Shown, uint Vertices, float Distance)>();
        foreach (var e in v.Expected) byFirst[e.First] = (e.Shown, e.Vertices, e.Distance);
        float last = float.NegativeInfinity;
        var sortedCpu = v.Expected.Select(e => e.Distance).Order().ToArray();
        for (int d = 0; d < count; d++)
        {
            uint verts = all[4 + d * 4], shown = all[4 + d * 4 + 1], first = all[4 + d * 4 + 3];
            if (!byFirst.TryGetValue(first, out var e) || e.Shown != shown || e.Vertices != verts)
            {
                grassVerifySet++;
                GrassVerifyNote($"frame {v.Frame} {v.Kind}: draw {d} (first blade {first}, {shown} blades, {verts} vertices) is not the CPU's");
                continue;
            }
            if (v.Kind != "colour") continue;
            if (e.Distance < last || e.Distance != sortedCpu[d]) { grassVerifyOrder++; GrassVerifyNote($"frame {v.Frame}: draw {d} at distance {e.Distance:R} breaks the nearest-first order"); }
            last = e.Distance;
        }
    }

    void ReportGrassVerify()
    {
        if (!GpuCullVerify || !GpuGrassActive) return;
        if (!Gpu.Device.Frames.InFrame) { Gpu.Device.Frames.WaitAll(); CheckGrassVerify(all: true); }
        else CheckGrassVerify(all: false);
        Console.WriteLine($"foliage gpu grass verify: {grassVerifyViews} views compared, {grassVerifyDraws:N0} draws; set differences {grassVerifySet}, order differences {grassVerifyOrder}; {grassVerifyPending.Count} not read");
    }
}
