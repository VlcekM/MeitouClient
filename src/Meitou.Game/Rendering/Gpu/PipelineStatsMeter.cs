using Meitou.Game.Rendering.Bench;
using Meitou.Game.Rendering.Gpu.Core;
using Meitou.Game.Rendering.Profiling;
using Silk.NET.Vulkan;

namespace Meitou.Game.Rendering.Gpu;

/// <summary>
/// The bench's triangles per pass (<c>--bench-tris</c>, docs/bench.md "Triangles per pass"): pipeline statistics queries (input assembly primitives,
/// vertex shader invocations, clipping in and out, fragment shader invocations) around each part of a frame, read after the frame has been waited for.
/// Only the bench's counting frames use it; nothing else is recorded in a normal frame (<see cref="Current"/> is null).
/// </summary>
/// <remarks>
/// The parts are the <see cref="StageClock"/> boundaries (<see cref="OnClose"/>: the stage laps, the shadow cascades, the parts of the foliage pass), as
/// the pass meter takes them, and a query never crosses a rendering: <see cref="CommandList.BeginRendering"/> and <see cref="CommandList.EndRendering"/>
/// end the open query before and begin the next after (the pieces of one part are summed). That needs the draws recorded straight into the frame's list,
/// so the bench runs these frames with <see cref="Recording.Mode"/> 0 (secondaries could not hold a query that started in another command buffer).
/// </remarks>
public sealed unsafe class PipelineStatsMeter : IDisposable
{
    /// <summary>
    /// The meter whose frame is being recorded (read by <see cref="CommandList"/> at the start and end of every rendering).
    /// </summary>
    internal static PipelineStatsMeter? Current;

    public const uint Capacity = 2048;

    // In the order of the set bits: input assembly primitives, vertex invocations, clipping invocations, clipping primitives, fragment invocations.
    const int Values = 5;

    const QueryPipelineStatisticFlags Wanted = QueryPipelineStatisticFlags.InputAssemblyPrimitivesBit | QueryPipelineStatisticFlags.VertexShaderInvocationsBit |
        QueryPipelineStatisticFlags.ClippingInvocationsBit | QueryPipelineStatisticFlags.ClippingPrimitivesBit | QueryPipelineStatisticFlags.FragmentShaderInvocationsBit;

    readonly GpuContext _ctx;
    readonly Vk _vk;
    readonly Device _device;
    readonly QueryPool _pool;
    readonly List<int> _loose = [];
    readonly List<(string Label, int[] Queries)> _subs = [];
    readonly List<(string Key, int[] Queries)> _rows = [];
    readonly Dictionary<string, int> _occurrences = [];
    int _next;
    int _open = -1;
    nint _openBuffer;
    bool _active;

    /// <summary>
    /// Queries lost to a full pool or a close in the wrong command buffer, over all frames (0 when the numbers are complete).
    /// </summary>
    public int Lost { get; private set; }

    public static bool Supported(GpuContext ctx) => ctx.Device.HasPipelineStatistics;

    public PipelineStatsMeter(GpuContext ctx)
    {
        if (!ctx.Device.HasPipelineStatistics) throw new NotSupportedException("the device has no pipeline statistics queries");

        this._ctx = ctx;
        _vk = ctx.Device.Vk;
        _device = ctx.Device.Device;
        var info = new QueryPoolCreateInfo
        {
            SType = StructureType.QueryPoolCreateInfo,
            QueryType = QueryType.PipelineStatistics,
            QueryCount = Capacity,
            PipelineStatistics = Wanted,
        };
        VulkanException.Check(_vk.CreateQueryPool(_device, &info, null, out _pool), "vkCreateQueryPool(pipeline statistics)");
    }

    /// <summary>
    /// Starts recording the parts of one frame: call at the frame's start (the stage clock's start), after the previous frame was waited for.
    /// </summary>
    public void BeginFrame()
    {
        _vk.ResetQueryPool(_device, _pool, 0, Capacity);
        _next = 0;
        _open = -1;
        _loose.Clear();
        _subs.Clear();
        _rows.Clear();
        _occurrences.Clear();
        _active = true;
        Current = this;
        _ctx.Interleave(Open);
    }

    /// <summary>
    /// A stage or part of the frame ended (the stage clock's hook): the open query closes and is filed under <paramref name="label"/>, the next one opens.
    /// </summary>
    public void OnClose(string label, bool isSub)
    {
        if (!_active) return;

        _ctx.Interleave(End);
        if (isSub)
        {
            _subs.Add((label, Take()));
        }
        else
        {
            FileStage(label);

            // The post stage is the last of the world frame: only the overlay follows.
            if (label == "post")
            {
                _active = false;
                return;
            }
        }

        _ctx.Interleave(Open);
    }

    void FileStage(string label)
    {
        int n = _occurrences.GetValueOrDefault(label) + 1;
        _occurrences[label] = n;
        string key = n == 1 ? label : $"{label} #{n}";
        _rows.Add((key, Take()));
        foreach (var (sub, queries) in _subs) _rows.Add(($"{key}/{sub}", queries));

        _subs.Clear();
    }

    int[] Take()
    {
        var q = _loose.ToArray();
        _loose.Clear();
        return q;
    }

    void Open(IGpuCommandList cmd)
    {
        if (!_active || _open >= 0) return;

        if (_next >= Capacity)
        {
            Lost++;
            return;
        }

        _open = _next++;
        _openBuffer = ((CommandList)cmd).Handle.Handle;
        _vk.CmdBeginQuery(((CommandList)cmd).Handle, _pool, (uint)_open, 0);
        _loose.Add(_open);
    }

    void End(IGpuCommandList cmd)
    {
        if (_open < 0) return;

        var vkList = (CommandList)cmd;
        if (vkList.Handle.Handle != _openBuffer)
        {
            Lost++;
            _open = -1;
            return;
        }

        _vk.CmdEndQuery(vkList.Handle, _pool, (uint)_open);
        _open = -1;
    }

    /// <summary>
    /// Called by <see cref="CommandList"/> around a rendering: <paramref name="before"/> ends the open query, after it a new one opens.
    /// </summary>
    internal void Rendering(CommandList cmd, bool before)
    {
        if (before)
        {
            if (_open >= 0 && cmd.Handle.Handle == _openBuffer) End(cmd);
        }
        else if (_active && _open < 0)
        {
            Open(cmd);
        }
    }

    /// <summary>
    /// Per part of the frame, what the queries counted (call after the frame was waited for): the stages (<c>terrain</c>, <c>foliage</c>, a second slice as
    /// <c>terrain #2</c>) and their parts (<c>foliage/fol meshes</c>, <c>shadows/...</c>), in the order the frame ran them.
    /// </summary>
    public List<(string Key, PassStat Stat)> Read()
    {
        Current = null;
        _active = false;
        var data = new ulong[Math.Max(_next, 1) * Values];
        if (_next > 0) ReadResults(data);

        var result = new List<(string, PassStat)>(_rows.Count);
        foreach (var (key, queries) in _rows)
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

    void ReadResults(ulong[] data)
    {
        fixed (ulong* p = data)
        {
            var flags = QueryResultFlags.Result64Bit | QueryResultFlags.ResultWaitBit;
            VulkanException.Check(_vk.GetQueryPoolResults(_device, _pool, 0, (uint)_next, (nuint)(data.Length * 8), p, Values * 8, flags),
                "vkGetQueryPoolResults(pipeline statistics)");
        }
    }

    public void Dispose()
    {
        if (Current == this) Current = null;

        _vk.DestroyQueryPool(_device, _pool, null);
    }
}
