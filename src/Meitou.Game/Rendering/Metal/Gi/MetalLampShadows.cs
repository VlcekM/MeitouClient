using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Game.Rendering.Gi;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Objects;

namespace Meitou.Game.Rendering.Metal.Gi;

/// <summary>
/// The lamps' traced shadows on Metal (docs/render-lights.md "Traced lamp shadows", docs/render-gi.md "Metal"): Vulkan's full-screen trace
/// (<see cref="LampShadowShaders.Trace"/>, every lamp of a pixel's cell at once; Metal has no tile cache, so the per-lamp passes do not run) and the
/// shared penumbra filter, into the lost light and its two totals the post chain's apply takes off the scene. Render thread only.
/// </summary>
sealed unsafe class MetalLampShadows(MetalContext ctx, MetalTracedSun penumbra) : IDisposable
{
    /// <summary>
    /// The trace kernel and its pipeline (made on first use).
    /// </summary>
    (MetalProgram Program, GpuComputePipeline Pipeline) _trace;

    /// <summary>
    /// The rays' lost light, the two totals, the filter's scratch and its result, at the render size.
    /// </summary>
    GpuTexture? _lost, _total, _total1, _scratch, _filtered;

    /// <summary>
    /// The nearest clamped sampler every input is read with.
    /// </summary>
    readonly GpuSampler _nearest = ctx.Samplers.Get(SamplerDesc.Clamped(TextureMinFilter.Nearest, TextureMagFilter.Nearest));

    /// <summary>
    /// The filtered lost light and the totals of the last <see cref="Run"/> (null before the first).
    /// </summary>
    public (GpuTexture Filtered, GpuTexture Total, GpuTexture Total1)? Result =>
        _filtered is { } f ? (f, _total!, _total1!) : null;

    /// <summary>
    /// The trace's constants (<see cref="LampShadowShaders.Trace"/>'s <c>Params</c>).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    struct Params
    {
        /// <summary>
        /// The lamps' grid, the camera's axes (w the footprint, the phase), the eye, the traced origin (w the skin table), the planes and tangents, the
        /// size and the rays' mask.
        /// </summary>
        public Vector4 LightGrid, Right, Up, Back, Eye, Origin, PlanesTan, Size;
    }

    /// <summary>
    /// The camera of the surfaces: its axes, eye, depth planes, tangents, render size and a pixel's width at depth 1.
    /// </summary>
    public readonly record struct View(Vector3 Right, Vector3 Up, Vector3 Back, Vector3 Eye, Vector2 Planes, Vector2 Tan, int Width, int Height,
        float Footprint);

    /// <summary>
    /// The trace over the scene's depth and surface (every lamp of each pixel's cell), then the penumbra filter. <paramref name="inverse"/>: NDC (y up
    /// from row 0, as the kernels compute it) to the position relative to the eye.
    /// </summary>
    public void Run(MetalGiScene scene, WorldLamps lamps, GpuTexture depth, GpuTexture surface, in View view, in Matrix4x4 inverse, uint skinTable)
    {
        if (scene.Top is null) return;

        EnsureTargets(view.Width, view.Height);
        _trace = _trace.Program is null ? MakeTrace() : _trace;
        var p = new Params
        {
            LightGrid = lamps.Grid,
            Right = new Vector4(view.Right, view.Footprint),
            Up = new Vector4(view.Up, 0),
            Back = new Vector4(view.Back, 0),
            Eye = new Vector4(view.Eye, 0),
            Origin = new Vector4(scene.Origin, skinTable),
            PlanesTan = new Vector4(view.Planes, view.Tan.X, view.Tan.Y),
            Size = new Vector4(view.Width, view.Height, TracedSunShaders.LampMask, 0),
        };

        var cmd = (MetalCommandList)ctx.BeginNative("lamp shadows");
        Trace(cmd, scene, lamps, depth, surface, p, view);
        penumbra.Penumbra(cmd, depth, surface, _lost!, _scratch!, _filtered!, inverse, view.Footprint, named: true);
        cmd.EndCompute();
        ctx.EndNative(cmd);
    }

    /// <summary>
    /// The trace kernel: the depth, the surface, the scene and its records, the lamps' textures and tiles, the three outputs.
    /// </summary>
    void Trace(MetalCommandList cmd, MetalGiScene scene, WorldLamps lamps, GpuTexture depth, GpuTexture surface, in Params p, in View view)
    {
        var (cells, index, data) = lamps.Current;
        var program = _trace.Program;
        cmd.BindPipeline(_trace.Pipeline);
        var e = cmd.ComputeEncoder();
        Bind(e, program, 0, new SampledTexture(_nearest, depth.View()));
        Bind(e, program, 1, new SampledTexture(_nearest, surface.View()));
        e.SetAccelerationStructure(scene.Top!.Value, 2);
        e.SetBuffer(scene.Records, 0, 3);
        fixed (Params* constants = &p) e.SetBytes((nint)constants, (ulong)sizeof(Params), 4);
        e.SetTexture(ctx.TextureAt(_lost!), 5);
        Bind(e, program, 6, new SampledTexture(lamps.NearestSampler, cells.View()));
        Bind(e, program, 7, new SampledTexture(lamps.NearestSampler, index.View()));
        Bind(e, program, 8, new SampledTexture(lamps.NearestSampler, data.View()));
        e.SetTexture(ctx.TextureAt(_total!), 9);
        e.SetTexture(ctx.TextureAt(_total1!), 10);
        Bind(e, program, 11, new SampledTexture(lamps.NearestSampler, lamps.TilesTexture.View()));
        cmd.BindBindlessTable(new GpuProgram(program.Info.Index, program.Info), Frame.NativeShaders.BindlessSet, GpuBindPoint.Compute);
        cmd.UseResources(scene.Resident, scene.Resident.Length);
        cmd.Dispatch((uint)((view.Width + 7) / 8), (uint)((view.Height + 7) / 8));
    }

    /// <summary>
    /// The trace kernel (its reconstruction's y turned round for top-down targets) and its pipeline.
    /// </summary>
    (MetalProgram, GpuComputePipeline) MakeTrace()
    {
        var shaders = (MetalShaders)ctx.Shaders;
        string glsl = MetalGiResolve.Flip(LampShadowShaders.Trace(LampShadowShaders.MaxLamps));
        var program = shaders.ComputeDiscrete(glsl, "metal lamp shadow trace");
        return (shaders.Backend(program), ctx.Pipelines.Get(new ComputePipelineDesc(program, "metal lamp shadow trace")));
    }

    /// <summary>
    /// A sampled texture at texture index <paramref name="binding"/> and its sampler at the program's slot.
    /// </summary>
    void Bind(SharpMetal.Metal.MTLComputeCommandEncoder e, MetalProgram program, int binding, SampledTexture sampled)
    {
        var (texture, sampler) = ctx.Sampled(sampled);
        e.SetTexture(texture, (ulong)binding);
        int slot = program.SamplerSlot(binding);
        if (slot >= 0) e.SetSamplerState(sampler, (ulong)slot);
    }

    /// <summary>
    /// The five targets at <paramref name="width"/> x <paramref name="height"/> (made again when it changes).
    /// </summary>
    void EnsureTargets(int width, int height)
    {
        if (_lost is { } made && made.Desc.Width == width && made.Desc.Height == height) return;

        Release();
        GpuTexture Target(string name) => ctx.CreateTexture(new TextureDesc(InternalFormat.Rgba16F, width, height,
            Use: TextureUse.Storage | TextureUse.Sampled, Name: name));
        (_lost, _total, _total1) = (Target("metal lamp shadows lost"), Target("metal lamp shadows total"), Target("metal lamp shadows total1"));
        (_scratch, _filtered) = (Target("metal lamp shadows penumbra"), Target("metal lamp shadows filtered"));
    }

    /// <summary>
    /// Releases the targets (after the frames in flight).
    /// </summary>
    void Release()
    {
        foreach (var t in (ReadOnlySpan<GpuTexture?>)[_lost, _total, _total1, _scratch, _filtered])
            if (t is { } texture) ctx.DeferDelete(() => ctx.ReleaseTexture(texture));
        _lost = _total = _total1 = _scratch = _filtered = null;
    }

    /// <summary>
    /// Releases the targets.
    /// </summary>
    public void Dispose() => Release();
}
