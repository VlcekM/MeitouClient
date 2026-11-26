using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Game.Rendering.Gi;
using Meitou.Game.Rendering.Gpu;
using SharpMetal.Foundation;
using SharpMetal.Metal;

namespace Meitou.Game.Rendering.Metal.Gi;

/// <summary>
/// The hybrid GI's radiance cache on Metal (docs/render-gi.md "Hybrid GI", "Metal"): <see cref="GiCache"/>'s passes with the same shaders
/// (<see cref="GiCacheShaders"/>) over the Metal traced scene, the trace shading its hits with the probe update's inputs. Runs after the scene, before
/// the resolve. Metal's targets keep row 0 at the top, so the passes' position reconstruction turns its y round. Render thread only.
/// </summary>
sealed unsafe class MetalGiCache(MetalContext ctx, MetalProbeGi probes) : IDisposable
{
    #region State

    /// <summary>
    /// The reconstruction's line that maps a pixel to NDC (rows bottom-up, as Vulkan's targets).
    /// </summary>
    const string NdcLine = "vec2 ndc = (vec2(px) + 0.5) / vSize.zw * 2.0 - 1.0;";

    /// <summary>
    /// Seconds a cell takes to trace its whole ring (<see cref="GiCache"/>'s rule for the frames between a cell's traces).
    /// </summary>
    const float RingSeconds = 0.5f;

    /// <summary>
    /// The trace programs by relocated probes and cloud shadows, made on first use.
    /// </summary>
    readonly Dictionary<(bool Relocate, bool Clouds), (MetalProgram Program, GpuComputePipeline Pipeline)> _traces = [];

    /// <summary>
    /// The evict, touch, arguments and gather passes (made with the buffers on first use).
    /// </summary>
    (MetalProgram Program, GpuComputePipeline Pipeline) _evict, _touch, _arguments, _gather;

    /// <summary>
    /// The table's keys and touch stamps, the cells' frame sums, the cells, their ring of subsets, the control words (this frame's cells, the trace's
    /// dispatch, the cells held) and this frame's list of cells.
    /// </summary>
    MTLBuffer _keys, _stamps, _sums, _cells, _ring, _control, _active;

    /// <summary>
    /// Whether the passes and buffers were made.
    /// </summary>
    bool _made;

    /// <summary>
    /// The gathered image (rgb the light, a how much to take), at the low target's size.
    /// </summary>
    GpuTexture? _output;

    /// <summary>
    /// The nearest clamped sampler the surface target and the depth are read with.
    /// </summary>
    readonly GpuSampler _nearest = ctx.Samplers.Get(SamplerDesc.Clamped(TextureMinFilter.Nearest, TextureMagFilter.Nearest));

    /// <summary>
    /// The frames run so far (the first is 1), the frames between a cell's traces, when the last run started and the smoothed seconds between runs.
    /// </summary>
    uint _frame;

    /// <summary>
    /// See <see cref="_frame"/>.
    /// </summary>
    int _stride = 1;

    /// <summary>
    /// See <see cref="_frame"/>.
    /// </summary>
    long _lastRun;

    /// <summary>
    /// See <see cref="_frame"/>.
    /// </summary>
    double _frameSeconds = 1.0 / 30;

    /// <summary>
    /// The trace's Params block: the probe trace's constants, the rays' reach and clamp, then the view (<see cref="GiCache"/>'s).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    struct TraceConstants
    {
        /// <summary>
        /// The probe update's constants.
        /// </summary>
        public GiProbes.Constants Probes;

        /// <summary>
        /// x the rays' reach, y the clamp.
        /// </summary>
        public Vector4 Reach;

        /// <summary>
        /// The view (<see cref="ViewConstants"/>).
        /// </summary>
        public ViewConstants View;
    }

    /// <summary>
    /// The per-frame view block (<see cref="GiCacheShaders"/>' <c>View</c>).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    struct ViewConstants
    {
        /// <summary>
        /// The eye (w the divisor), the camera's axes, the planes and tangents, the sizes, the cells' sizes and range.
        /// </summary>
        public Vector4 Eye, Right, Up, Back, Planes, Size, Cells;

        /// <summary>
        /// The frame, the eviction age, this frame's subset, the frames between a cell's traces.
        /// </summary>
        public uint Frame, EvictAge, Subset, Stride;
    }

    #endregion

    #region Run

    /// <summary>
    /// The passes over <paramref name="depth"/> and <paramref name="surface"/>; the gathered image, or null without a probe update this frame.
    /// </summary>
    public GpuTexture? Run(GpuTexture depth, GpuTexture surface, in GiCache.View v, bool relocate, bool clouds)
    {
        if (probes.Scene.Top is null || !probes.UpdatedThisFrame) return null;

        MakePasses();
        EnsureOutput(v.Width, v.Height);
        var trace = Trace(relocate, clouds);
        NextFrame();
        var view = Constants(v);
        var viewBuffer = Upload(view);
        var traceBuffer = Upload(new TraceConstants
        {
            Probes = probes.LastConstants, Reach = new Vector4(GiCache.Reach, GiCache.SampleClamp, 0, 0), View = view,
        });

        var cmd = (MetalCommandList)ctx.BeginNative("gi cache");
        Evict(cmd, viewBuffer);
        Touch(cmd, viewBuffer, depth, surface, v);
        Arguments(cmd);
        cmd.EndCompute();
        TraceCells(cmd, trace, traceBuffer);
        cmd.EndCompute();
        Gather(cmd, viewBuffer, depth, surface, v);
        cmd.EndCompute();
        ctx.EndNative(cmd);
        return _output;
    }

    /// <summary>
    /// Counts the frame and picks the frames between a cell's traces from the smoothed frame time (<see cref="GiCache"/>'s rule, with play so a frame
    /// rate at a boundary does not switch back and forth).
    /// </summary>
    void NextFrame()
    {
        _frame++;
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_lastRun != 0)
        {
            double seconds = Math.Min((now - _lastRun) / (double)System.Diagnostics.Stopwatch.Frequency, 0.1);
            _frameSeconds += (seconds - _frameSeconds) * 0.05;
        }

        _lastRun = now;
        double wanted = RingSeconds / (GiCache.Ring * _frameSeconds);
        if (wanted < _stride - 0.25 || wanted > _stride + 1.25) _stride = Math.Clamp((int)wanted, 1, 4);
    }

    /// <summary>
    /// This frame's view block.
    /// </summary>
    ViewConstants Constants(in GiCache.View v) => new()
    {
        Eye = new Vector4(v.Eye, v.Divisor), Right = new Vector4(v.Right, 0), Up = new Vector4(v.Up, 0), Back = new Vector4(v.Back, 0),
        Planes = new Vector4(v.Planes, v.Tan.X, v.Tan.Y),
        Size = new Vector4(v.Width, v.Height, v.FullWidth, v.FullHeight),
        Cells = new Vector4(GiCache.FinestCell, 2 * v.TanY / GiCache.CellsPerHeight, GiCache.Range, GiCache.LevelBlend),
        Frame = _frame, EvictAge = GiCache.EvictAge, Subset = _frame % GiCache.Ring, Stride = (uint)_stride,
    };

    /// <summary>
    /// <paramref name="value"/> in the frame's constants ring: its buffer and offset.
    /// </summary>
    (MTLBuffer Buffer, ulong Offset) Upload<T>(in T value) where T : unmanaged
    {
        var slice = ctx.Frame.AllocateConstants((ulong)sizeof(T), 256);
        *(T*)slice.Pointer = value;
        return (ctx.BufferAt(slice.Handle), slice.Offset);
    }

    #endregion

    #region Passes

    /// <summary>
    /// The evict pass, a thread per slot.
    /// </summary>
    void Evict(MetalCommandList cmd, (MTLBuffer Buffer, ulong Offset) view)
    {
        cmd.BindPipeline(_evict.Pipeline);
        var e = cmd.ComputeEncoder();
        e.SetBuffer(_keys, 0, 0);
        e.SetBuffer(_stamps, 0, 1);
        e.SetBuffer(_control, 0, 2);
        e.SetBuffer(view.Buffer, view.Offset, 8);
        cmd.Dispatch(GiCache.Capacity / 64);
    }

    /// <summary>
    /// The touch pass, a thread per low texel.
    /// </summary>
    void Touch(MetalCommandList cmd, (MTLBuffer Buffer, ulong Offset) view, GpuTexture depth, GpuTexture surface, in GiCache.View v)
    {
        cmd.BindPipeline(_touch.Pipeline);
        var e = cmd.ComputeEncoder();
        BindSurface(e, _touch.Program, depth, surface);
        e.SetBuffer(_keys, 0, 2);
        e.SetBuffer(_stamps, 0, 3);
        e.SetBuffer(_sums, 0, 4);
        e.SetBuffer(_cells, 0, 5);
        e.SetBuffer(_control, 0, 6);
        e.SetBuffer(_active, 0, 7);
        e.SetBuffer(view.Buffer, view.Offset, 8);
        cmd.Dispatch((uint)((v.Width + 7) / 8), (uint)((v.Height + 7) / 8));
    }

    /// <summary>
    /// The trace's indirect dispatch from this frame's count of cells.
    /// </summary>
    void Arguments(MetalCommandList cmd)
    {
        cmd.BindPipeline(_arguments.Pipeline);
        cmd.ComputeEncoder().SetBuffer(_control, 0, 0);
        cmd.Dispatch(1);
    }

    /// <summary>
    /// The trace of this frame's listed cells, a threadgroup a cell through the indirect arguments, its hits shaded as the probe trace's.
    /// </summary>
    void TraceCells(MetalCommandList cmd, (MetalProgram Program, GpuComputePipeline Pipeline) trace, (MTLBuffer Buffer, ulong Offset) constants)
    {
        var scene = probes.Scene;
        cmd.BindPipeline(trace.Pipeline);
        var e = cmd.ComputeEncoder();
        e.SetAccelerationStructure(scene.Top!.Value, 0);
        e.SetBuffer(scene.Records, 0, 1);
        e.SetBuffer(_cells, 0, 2);
        e.SetBuffer(constants.Buffer, constants.Offset, 3);
        e.SetBuffer(_sums, 0, 13);
        e.SetBuffer(_active, 0, 14);
        e.SetBuffer(_ring, 0, 17);
        probes.BindHitInputs(e, trace.Program);
        cmd.BindBindlessTable(new GpuProgram(trace.Program.Info.Index, trace.Program.Info), Frame.NativeShaders.BindlessSet, GpuBindPoint.Compute);
        cmd.UseResources(scene.Resident, scene.Resident.Length);

        var threads = new MTLSize { width = GiCache.RaysPerCell, height = 1, depth = 1 };
        e.DispatchThreadgroups(_control, 4, threads);
    }

    /// <summary>
    /// The gather pass into <see cref="_output"/>, a thread per low texel.
    /// </summary>
    void Gather(MetalCommandList cmd, (MTLBuffer Buffer, ulong Offset) view, GpuTexture depth, GpuTexture surface, in GiCache.View v)
    {
        cmd.BindPipeline(_gather.Pipeline);
        var e = cmd.ComputeEncoder();
        BindSurface(e, _gather.Program, depth, surface);
        e.SetBuffer(_keys, 0, 2);
        e.SetBuffer(_cells, 0, 5);
        e.SetTexture(ctx.TextureAt(_output!), 6);
        e.SetBuffer(view.Buffer, view.Offset, 8);
        cmd.Dispatch((uint)((v.Width + 7) / 8), (uint)((v.Height + 7) / 8));
    }

    /// <summary>
    /// The surface target and the scene depth at bindings 0 and 1.
    /// </summary>
    void BindSurface(MTLComputeCommandEncoder e, MetalProgram program, GpuTexture depth, GpuTexture surface)
    {
        foreach (var (binding, texture) in (ReadOnlySpan<(int, GpuTexture)>)[(0, surface), (1, depth)])
        {
            var (image, sampler) = ctx.Sampled(new SampledTexture(_nearest, texture.View()));
            e.SetTexture(image, (ulong)binding);
            int slot = program.SamplerSlot(binding);
            if (slot >= 0) e.SetSamplerState(sampler, (ulong)slot);
        }
    }

    #endregion

    #region Making

    /// <summary>
    /// The trace for the frame's options, made on first use.
    /// </summary>
    (MetalProgram, GpuComputePipeline) Trace(bool relocate, bool clouds)
    {
        if (_traces.TryGetValue((relocate, clouds), out var made)) return made;

        return _traces[(relocate, clouds)] = Pass(GiCacheShaders.Trace(relocate, clouds), "metal gi cache trace");
    }

    /// <summary>
    /// The passes and the table's buffers, the table empty (no keys, no sums, never touched: the frames count from 1).
    /// </summary>
    void MakePasses()
    {
        if (_made) return;

        _made = true;
        _evict = Pass(GiCacheShaders.Evict, "metal gi cache evict");
        _touch = Pass(GiCacheShaders.Touch, "metal gi cache touch");
        _arguments = Pass(GiCacheShaders.Arguments, "metal gi cache arguments");
        _gather = Pass(GiCacheShaders.Gather, "metal gi cache gather");
        _keys = Buffer(GiCache.Capacity * 4, "metal gi cache keys");
        _stamps = Buffer(GiCache.Capacity * 4, "metal gi cache stamps");
        _sums = Buffer(GiCache.Capacity * 32, "metal gi cache sums");
        _cells = Buffer(GiCache.Capacity * 64, "metal gi cache cells");
        _ring = Buffer((ulong)GiCache.Capacity * GiCache.Ring * 4, "metal gi cache ring");
        _active = Buffer(GiCache.Capacity * 4, "metal gi cache list");
        _control = Buffer(32, "metal gi cache control");
    }

    /// <summary>
    /// A discrete-set compute program (its reconstruction's y turned round) and its pipeline.
    /// </summary>
    (MetalProgram, GpuComputePipeline) Pass(string glsl, string name)
    {
        if (glsl.Contains(NdcLine, StringComparison.Ordinal))
            glsl = glsl.Replace(NdcLine, NdcLine + "\n    ndc.y = -ndc.y;", StringComparison.Ordinal);

        var shaders = (MetalShaders)ctx.Shaders;
        var program = shaders.ComputeDiscrete(glsl, name);
        return (shaders.Backend(program), ctx.Pipelines.Get(new ComputePipelineDesc(program, name)));
    }

    /// <summary>
    /// A zeroed shared buffer of <paramref name="size"/> bytes.
    /// </summary>
    MTLBuffer Buffer(ulong size, string label)
    {
        var buffer = ctx.Device.NewBuffer(size, MTLResourceOptions.ResourceStorageModeShared);
        buffer.Label = (NSString)label;
        new Span<byte>((void*)buffer.Contents, checked((int)size)).Clear();
        return buffer;
    }

    /// <summary>
    /// <see cref="_output"/> made again when its size is not <paramref name="width"/> x <paramref name="height"/>.
    /// </summary>
    void EnsureOutput(int width, int height)
    {
        if (_output is { } made && made.Desc.Width == width && made.Desc.Height == height) return;

        if (_output is { } old) ctx.DeferDelete(() => ctx.ReleaseTexture(old));
        _output = ctx.CreateTexture(new TextureDesc(InternalFormat.Rgba16F, width, height, Use: TextureUse.Storage | TextureUse.Sampled,
            Name: "metal gi cache"));
    }

    /// <summary>
    /// Frees the output and the buffers.
    /// </summary>
    public void Dispose()
    {
        if (_output is { } output) ctx.ReleaseTexture(output);
        if (!_made) return;

        foreach (var b in (ReadOnlySpan<MTLBuffer>)[_keys, _stamps, _sums, _cells, _ring, _control, _active]) b.Dispose();
    }

    #endregion
}
