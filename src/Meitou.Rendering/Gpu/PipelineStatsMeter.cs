using Meitou.Rendering.Gpu.Core;
using Silk.NET.Vulkan;

namespace Meitou.Rendering.Gpu;

/// <summary>
/// The bench's triangles per pass (<c>--bench-tris</c>, docs/bench.md "Triangles per pass"): pipeline statistics queries (input assembly
/// primitives, vertex shader invocations, clipping in and out, fragment shader invocations) around each part of a frame, read after the frame
/// has been waited for. Only the bench's counting frames use it; nothing else is recorded in a normal frame (<see cref="Current"/> is null).
/// </summary>
/// <remarks>
/// The parts are the <see cref="StageClock"/> boundaries (<see cref="OnClose"/>: the stage laps, the shadow cascades, the parts of the foliage
/// pass), as the pass meter takes them, and a query never crosses a rendering: <see cref="CommandList.BeginRendering"/> and
/// <see cref="CommandList.EndRendering"/> end the open query before and begin the next after (the pieces of one part are summed). That needs
/// the draws recorded straight into the frame's list, so the bench runs these frames with <see cref="Recording.Mode"/> 0 (secondaries could not
/// hold a query that started in another command buffer).
/// </remarks>
public sealed unsafe class PipelineStatsMeter : IDisposable
{
    /// <summary>The meter whose frame is being recorded (read by <see cref="CommandList"/> at the start and end of every rendering).</summary>
    internal static PipelineStatsMeter? Current;

    public const uint Capacity = 2048;
    const int Values = 5;   // in the order of the set bits: input assembly primitives, vertex invocations, clipping invocations, clipping primitives, fragment invocations
    const QueryPipelineStatisticFlags Wanted = QueryPipelineStatisticFlags.InputAssemblyPrimitivesBit | QueryPipelineStatisticFlags.VertexShaderInvocationsBit |
        QueryPipelineStatisticFlags.ClippingInvocationsBit | QueryPipelineStatisticFlags.ClippingPrimitivesBit | QueryPipelineStatisticFlags.FragmentShaderInvocationsBit;

    readonly GpuContext ctx;
    readonly Vk vk;
    readonly Device device;
    readonly QueryPool pool;
    int next;
    int open = -1;
    nint openBuffer;
    bool active;
    readonly List<int> loose = [];
    readonly List<(string Label, int[] Queries)> subs = [];
    readonly List<(string Key, int[] Queries)> rows = [];
    readonly Dictionary<string, int> occurrences = [];

    /// <summary>Queries lost to a full pool or a close in the wrong command buffer, over all frames (0 when the numbers are complete).</summary>
    public int Lost { get; private set; }

    public static bool Supported(GpuContext ctx) => ctx.Device.HasPipelineStatistics;

    public PipelineStatsMeter(GpuContext ctx)
    {
        if (!ctx.Device.HasPipelineStatistics) throw new NotSupportedException("the device has no pipeline statistics queries");
        this.ctx = ctx;
        vk = ctx.Device.Vk;
        device = ctx.Device.Device;
        var info = new QueryPoolCreateInfo { SType = StructureType.QueryPoolCreateInfo, QueryType = QueryType.PipelineStatistics, QueryCount = Capacity, PipelineStatistics = Wanted };
        VulkanException.Check(vk.CreateQueryPool(device, &info, null, out pool), "vkCreateQueryPool(pipeline statistics)");
    }

    /// <summary>Starts recording the parts of one frame: call at the frame's start (the stage clock's start), after the previous frame was waited for.</summary>
    public void BeginFrame()
    {
        vk.ResetQueryPool(device, pool, 0, Capacity);
        next = 0;
        open = -1;
        loose.Clear();
        subs.Clear();
        rows.Clear();
        occurrences.Clear();
        active = true;
        Current = this;
        ctx.Interleave(Open);
    }

    /// <summary>A stage or part of the frame ended (the stage clock's hook): the open query closes and is filed under <paramref name="label"/>, the next one opens.</summary>
    public void OnClose(string label, bool isSub)
    {
        if (!active) return;
        ctx.Interleave(End);
        if (isSub) subs.Add((label, Take()));
        else
        {
            int n = occurrences.GetValueOrDefault(label) + 1;
            occurrences[label] = n;
            string key = n == 1 ? label : $"{label} #{n}";
            rows.Add((key, Take()));
            foreach (var (sub, queries) in subs) rows.Add(($"{key}/{sub}", queries));
            subs.Clear();
            // The post stage is the last of the world frame: only the overlay follows.
            if (label == "post") { active = false; return; }
        }
        ctx.Interleave(Open);
    }

    int[] Take()
    {
        var q = loose.ToArray();
        loose.Clear();
        return q;
    }

    void Open(CommandList cmd)
    {
        if (!active || open >= 0) return;
        if (next >= Capacity) { Lost++; return; }
        open = next++;
        openBuffer = cmd.Handle.Handle;
        vk.CmdBeginQuery(cmd.Handle, pool, (uint)open, 0);
        loose.Add(open);
    }

    void End(CommandList cmd)
    {
        if (open < 0) return;
        if (cmd.Handle.Handle != openBuffer) { Lost++; open = -1; return; }
        vk.CmdEndQuery(cmd.Handle, pool, (uint)open);
        open = -1;
    }

    /// <summary>Called by <see cref="CommandList"/> around a rendering: <paramref name="before"/> ends the open query, after it a new one opens.</summary>
    internal void Rendering(CommandList cmd, bool before)
    {
        if (before) { if (open >= 0 && cmd.Handle.Handle == openBuffer) End(cmd); }
        else if (active && open < 0) Open(cmd);
    }

    /// <summary>Per part of the frame, what the queries counted (call after the frame was waited for): the stages (<c>terrain</c>, <c>foliage</c>, a second
    /// slice as <c>terrain #2</c>) and their parts (<c>foliage/fol meshes</c>, <c>shadows/...</c>), in the order the frame ran them.</summary>
    public List<(string Key, PassStat Stat)> Read()
    {
        Current = null;
        active = false;
        var data = new ulong[Math.Max(next, 1) * Values];
        if (next > 0)
            fixed (ulong* p = data)
                VulkanException.Check(vk.GetQueryPoolResults(device, pool, 0, (uint)next, (nuint)(data.Length * 8), p, Values * 8, QueryResultFlags.Result64Bit | QueryResultFlags.ResultWaitBit),
                    "vkGetQueryPoolResults(pipeline statistics)");
        var result = new List<(string, PassStat)>(rows.Count);
        foreach (var (key, queries) in rows)
        {
            var s = new PassStat();
            foreach (int q in queries)
            {
                s.Primitives += data[q * Values];
                s.VertexInvocations += data[q * Values + 1];
                s.ClipIn += data[q * Values + 2];
                s.ClipOut += data[q * Values + 3];
                s.FragmentInvocations += data[q * Values + 4];
            }
            result.Add((key, s));
        }
        return result;
    }

    public void Dispose()
    {
        if (Current == this) Current = null;
        vk.DestroyQueryPool(device, pool, null);
    }
}
