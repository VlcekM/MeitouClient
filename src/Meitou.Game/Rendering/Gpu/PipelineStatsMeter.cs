using Meitou.Game.Rendering.Bench;
using Meitou.Game.Rendering.Gpu.Core;
using Meitou.Game.Rendering.Profiling;
using Silk.NET.Vulkan;

namespace Meitou.Game.Rendering.Gpu;

/// <summary>
/// The bench's triangles per pass (<c>--bench-tris</c>, docs/bench.md "Triangles per pass"): pipeline statistics queries (input assembly primitives,
/// vertex shader invocations, clipping in and out, fragment shader invocations) around each part of a frame, read after the frame has been waited
/// for. Only the bench's counting frames use it; nothing else is recorded in a normal frame (<see cref="Current"/> is null).
/// </summary>
/// <remarks>
/// The parts are the <see cref="StageClock"/> boundaries (<see cref="OnClose"/>: the stage laps, the shadow cascades, the parts of the foliage pass),
/// as the pass meter takes them, and a query never crosses a rendering: <see cref="CommandList.BeginRendering"/> and
/// <see cref="CommandList.EndRendering"/> end the open query before and begin the next after (the pieces of one part are summed). That needs the
/// draws recorded straight into the frame's list, so the bench runs these frames with <see cref="Recording.Mode"/> 0 (secondaries could not hold a
/// query that started in another command buffer).
/// </remarks>
public sealed unsafe class PipelineStatsMeter : IDisposable
{
    /// <summary>
    /// The meter whose frame is being recorded (read by <see cref="CommandList"/> at the start and end of every rendering).
    /// </summary>
    internal static PipelineStatsMeter? Current;

    /// <summary>
    /// Queries one frame may take.
    /// </summary>
    public const uint Capacity = 2048;

    /// <summary>
    /// The values a query gives, in the order of the set bits: input assembly primitives, vertex invocations, clipping invocations, clipping
    /// primitives, fragment invocations.
    /// </summary>
    const int Values = 5;

    /// <summary>
    /// The statistics the queries count.
    /// </summary>
    const QueryPipelineStatisticFlags Wanted = QueryPipelineStatisticFlags.InputAssemblyPrimitivesBit
        | QueryPipelineStatisticFlags.VertexShaderInvocationsBit | QueryPipelineStatisticFlags.ClippingInvocationsBit
        | QueryPipelineStatisticFlags.ClippingPrimitivesBit | QueryPipelineStatisticFlags.FragmentShaderInvocationsBit;

    /// <summary>
    /// The context whose frame the queries are interleaved into.
    /// </summary>
    readonly GpuContext _ctx;
    /// <summary>
    /// The Vulkan API entry points.
    /// </summary>
    readonly Vk _vk;
    /// <summary>
    /// The logical device.
    /// </summary>
    readonly Device _device;
    /// <summary>
    /// The pipeline statistics query pool.
    /// </summary>
    readonly QueryPool _pool;
    /// <summary>
    /// The queries of the part open now (a part split by renderings has several).
    /// </summary>
    readonly List<int> _loose = [];
    /// <summary>
    /// The parts of the stage open now, with their queries, filed under the stage when it closes.
    /// </summary>
    readonly List<(string Label, int[] Queries)> _subs = [];
    /// <summary>
    /// The frame's rows: each stage (and its parts as stage/part) with its queries, in order.
    /// </summary>
    readonly List<(string Key, int[] Queries)> _rows = [];
    /// <summary>
    /// How many times each stage label has closed this frame (a repeat is filed as <c>label #n</c>).
    /// </summary>
    readonly Dictionary<string, int> _occurrences = [];
    /// <summary>
    /// The next free query.
    /// </summary>
    int _next;
    /// <summary>
    /// The open query; -1 when none is.
    /// </summary>
    int _open = -1;
    /// <summary>
    /// The command buffer the open query began in (it must end there).
    /// </summary>
    nint _openBuffer;
    /// <summary>
    /// Between <see cref="BeginFrame"/> and the post stage's close (or <see cref="Read"/>).
    /// </summary>
    bool _active;

    /// <summary>
    /// Queries lost to a full pool or a close in the wrong command buffer, over all frames (0 when the numbers are complete).
    /// </summary>
    public int Lost { get; private set; }

    /// <summary>
    /// Whether the device can count pipeline statistics.
    /// </summary>
    public static bool Supported(GpuContext ctx) => ctx.Device.HasPipelineStatistics;

    /// <summary>
    /// A meter with a pool of <see cref="Capacity"/> queries; throws when the device has no pipeline statistics.
    /// </summary>
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
    /// A stage or part of the frame ended (the stage clock's hook): the open query closes and is filed under <paramref name="label"/>, the next one
    /// opens.
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

    /// <summary>
    /// Files the closed stage's queries (and its parts' under <c>stage/part</c>) as a row.
    /// </summary>
    void FileStage(string label)
    {
        int n = _occurrences.GetValueOrDefault(label) + 1;
        _occurrences[label] = n;
        string key = n == 1 ? label : $"{label} #{n}";
        _rows.Add((key, Take()));
        foreach (var (sub, queries) in _subs) _rows.Add(($"{key}/{sub}", queries));

        _subs.Clear();
    }

    /// <summary>
    /// The open part's queries, handed over and cleared.
    /// </summary>
    int[] Take()
    {
        var q = _loose.ToArray();
        _loose.Clear();
        return q;
    }

    /// <summary>
    /// Begins the next query in the list's command buffer (counted as lost when the pool is full).
    /// </summary>
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

    /// <summary>
    /// Ends the open query; one opened in another command buffer is dropped and counted as lost.
    /// </summary>
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
    /// Per part of the frame, what the queries counted (call after the frame was waited for): the stages (<c>terrain</c>, <c>foliage</c>, a second
    /// slice as <c>terrain #2</c>) and their parts (<c>foliage/fol meshes</c>, <c>shadows/...</c>), in the order the frame ran them.
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

    /// <summary>
    /// Reads every query's values, waiting for them.
    /// </summary>
    void ReadResults(ulong[] data)
    {
        fixed (ulong* p = data)
        {
            var flags = QueryResultFlags.Result64Bit | QueryResultFlags.ResultWaitBit;
            VulkanException.Check(_vk.GetQueryPoolResults(_device, _pool, 0, (uint)_next, (nuint)(data.Length * 8), p, Values * 8, flags),
                "vkGetQueryPoolResults(pipeline statistics)");
        }
    }

    /// <summary>
    /// Destroys the pool and stops being the current meter.
    /// </summary>
    public void Dispose()
    {
        if (Current == this) Current = null;

        _vk.DestroyQueryPool(_device, _pool, null);
    }
}
