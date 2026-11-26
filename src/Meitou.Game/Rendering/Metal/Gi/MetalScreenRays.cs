using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Game.Rendering.Gi;
using Meitou.Game.Rendering.Gpu;
using SharpMetal.Metal;

namespace Meitou.Game.Rendering.Metal.Gi;

/// <summary>
/// The rays traced from the screen's pixels on Metal, with the probe update's constants and hit inputs as Vulkan's <see cref="GiProbes"/> binds them:
/// the characters' gathered indirect light (docs/render-gi.md "Characters", "Metal"; <see cref="GiShaders.GatherPool"/>,
/// <see cref="GiShaders.CharacterGather"/>: a texel a pixel, two targets swapped each frame, a ray's hit on a character reading the last frame's, and
/// the last one pooled) and the reflections' rays (docs/render-gi.md "Reflections"; <see cref="GiShaders.Reflect"/>). Render thread only.
/// </summary>
sealed unsafe class MetalScreenRays(MetalContext ctx, MetalProbeGi probes) : IDisposable
{
    #region State

    /// <summary>
    /// The gather and the pool kernels and their pipelines (made on first use).
    /// </summary>
    (MetalProgram Program, GpuComputePipeline Pipeline) _gather, _pool, _reflect;

    /// <summary>
    /// This frame's gather, the last frame's, and the last pooled over a block, at the render size.
    /// </summary>
    GpuTexture? _now, _last, _pooled;

    /// <summary>
    /// The frame <see cref="_now"/> was gathered in (-1: never).
    /// </summary>
    long _gatheredFrame = -1;

    /// <summary>
    /// The nearest clamped sampler every input is read with.
    /// </summary>
    readonly GpuSampler _nearest = ctx.Samplers.Get(SamplerDesc.Clamped(TextureMinFilter.Nearest, TextureMagFilter.Nearest));

    /// <summary>
    /// The kernels' camera block (binding 15; <see cref="GiProbes"/>' reflection camera's layout).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    struct Camera
    {
        /// <summary>
        /// The eye and the camera's axes (back's w the sparse gather's pixel width); the tangents and the depth's planes; the size, whether the last
        /// gather is there, and the rooms' fill.
        /// </summary>
        public Vector4 Eye, Right, Up, Back, View, Sizes;
    }

    /// <summary>
    /// The camera of the surfaces. <paramref name="Up"/> is negated for Metal's top-down rows (the kernels rebuild rays from a pixel's row).
    /// </summary>
    public readonly record struct View(Vector3 Eye, Vector3 Right, Vector3 Up, Vector3 Back, Vector2 Tan, Vector2 Planes, int Width, int Height);

    #endregion

    #region Run

    /// <summary>
    /// The pool of the last gather and this frame's gather over <paramref name="depth"/> and <paramref name="surface"/>; the gathered light (rgb, alpha
    /// 1 on a character's pixel), or null without a probe update this frame.
    /// </summary>
    public GpuTexture? GatherCharacters(GpuTexture depth, GpuTexture surface, in View v)
    {
        if (probes.Scene.Top is null || !probes.UpdatedThisFrame) return null;

        Make();
        bool hadLast = EnsureTargets(v.Width, v.Height) && _gatheredFrame == ctx.Frame.Number - 1;
        (_now, _last) = (_last, _now);
        var camera = Upload(Constants(v, hadLast));
        var constants = Upload(probes.LastConstants);

        var cmd = (MetalCommandList)ctx.BeginNative("gi character gather");
        if (hadLast) Pool(cmd, surface, v);
        Gather(cmd, depth, surface, camera, constants, hadLast ? _last! : surface, v);
        cmd.EndCompute();
        ctx.EndNative(cmd);
        _gatheredFrame = ctx.Frame.Number;
        return _now;
    }

    /// <summary>
    /// The camera block: the divisor's place says whether the last gather is there to read, the back axis' w the sparse gather's pixel width, the
    /// size's w the rooms' fill.
    /// </summary>
    Camera Constants(in View v, bool hadLast)
    {
        float fill = probes.InteriorEnabled ? Math.Clamp(probes.InteriorGain - 1, 0, 1) : 0;
        return new Camera
        {
            Eye = new Vector4(v.Eye, 0),
            Right = new Vector4(v.Right, 0),
            Up = new Vector4(v.Up, 0),
            Back = new Vector4(v.Back, GiProbes.GatherSparse),
            View = new Vector4(v.Tan, v.Planes.X, v.Planes.Y),
            Sizes = new Vector4(v.Width, v.Height, hadLast ? 1 : 0, fill),
        };
    }

    /// <summary>
    /// The last frame's gather pooled once for every hit on a character (<see cref="GiShaders.GatherPool"/>).
    /// </summary>
    void Pool(MetalCommandList cmd, GpuTexture surface, in View v)
    {
        cmd.BindPipeline(_pool.Pipeline);
        var e = cmd.ComputeEncoder();
        Bind(e, _pool.Program, 0, _last!);
        Bind(e, _pool.Program, 1, surface);
        e.SetTexture(ctx.TextureAt(_pooled!), 2);
        cmd.Dispatch((uint)((v.Width + 7) / 8), (uint)((v.Height + 7) / 8));
        cmd.EndCompute();
    }

    /// <summary>
    /// The gather into <see cref="_now"/>: the scene, its records, the probes' constants and hit inputs, the surfaces and depth, the camera, the
    /// last gather (<paramref name="last"/>: a stand-in the kernel does not read without one) and its pool.
    /// </summary>
    void Gather(MetalCommandList cmd, GpuTexture depth, GpuTexture surface, (MTLBuffer Buffer, ulong Offset) camera,
        (MTLBuffer Buffer, ulong Offset) constants, GpuTexture last, in View v)
    {
        var e = Trace(cmd, _gather, _now!, depth, surface, camera, constants);
        Bind(e, _gather.Program, 19, last);
        Bind(e, _gather.Program, 20, _pooled!);
        cmd.Dispatch((uint)((v.Width + 7) / 8), (uint)((v.Height + 7) / 8));
    }

    /// <summary>
    /// The reflections' rays over the marched hits in <paramref name="hit"/> (a texel a block of <paramref name="divisor"/> pixels): a hit replaces
    /// the march's, a miss proves the sky open past it. False without a probe update this frame (the march's hits stand).
    /// </summary>
    public bool Reflect(GpuTexture hit, GpuTexture depth, GpuTexture surface, in View v, int divisor)
    {
        if (probes.Scene.Top is null || !probes.UpdatedThisFrame) return false;

        _reflect = _reflect.Program is null ? Kernel((MetalShaders)ctx.Shaders, GiShaders.Reflect, "metal gi reflect") : _reflect;
        var camera = Upload(new Camera
        {
            Eye = new Vector4(v.Eye, 0),
            Right = new Vector4(v.Right, 0),
            Up = new Vector4(v.Up, 0),
            Back = new Vector4(v.Back, 0),
            View = new Vector4(v.Tan, v.Planes.X, v.Planes.Y),
            Sizes = new Vector4(v.Width, v.Height, divisor, 0),
        });
        var constants = Upload(probes.LastConstants);

        var cmd = (MetalCommandList)ctx.BeginNative("gi reflect");
        Trace(cmd, _reflect, hit, depth, surface, camera, constants);
        cmd.Dispatch((uint)((hit.Desc.Width + 7) / 8), (uint)((hit.Desc.Height + 7) / 8));
        cmd.EndCompute();
        ctx.EndNative(cmd);
        return true;
    }

    /// <summary>
    /// What both ray kernels bind: the scene, its records, <paramref name="target"/>, the probes' constants and hit inputs, the surfaces and depth,
    /// the camera, the bindless table and the scene's resources.
    /// </summary>
    MTLComputeCommandEncoder Trace(MetalCommandList cmd, (MetalProgram Program, GpuComputePipeline Pipeline) kernel, GpuTexture target, GpuTexture depth,
        GpuTexture surface, (MTLBuffer Buffer, ulong Offset) camera, (MTLBuffer Buffer, ulong Offset) constants)
    {
        var scene = probes.Scene;
        var program = kernel.Program;
        cmd.BindPipeline(kernel.Pipeline);
        var e = cmd.ComputeEncoder();
        e.SetAccelerationStructure(scene.Top!.Value, 0);
        e.SetBuffer(scene.Records, 0, 1);
        e.SetTexture(ctx.TextureAt(target), 2);
        e.SetBuffer(constants.Buffer, constants.Offset, 3);
        probes.BindHitInputs(e, program);
        Bind(e, program, 13, surface);
        Bind(e, program, 14, depth);
        e.SetBuffer(camera.Buffer, camera.Offset, 15);
        cmd.BindBindlessTable(new GpuProgram(program.Info.Index, program.Info), Frame.NativeShaders.BindlessSet, GpuBindPoint.Compute);
        cmd.UseResources(scene.Resident, scene.Resident.Length);
        return e;
    }

    #endregion

    #region Setup

    /// <summary>
    /// The two kernels (first use).
    /// </summary>
    void Make()
    {
        if (_gather.Program is not null) return;

        var shaders = (MetalShaders)ctx.Shaders;
        string glsl = GiShaders.CharacterGather(GiProbes.GatherRays, GiProbes.GatherBehind, GiProbes.GatherBlock, GiProbes.GatherQuad);
        _gather = Kernel(shaders, glsl, "metal gi character gather");
        _pool = Kernel(shaders, GiShaders.GatherPool(GiProbes.GatherBlock), "metal gi character gather pool");
    }

    /// <summary>
    /// A kernel and its pipeline.
    /// </summary>
    (MetalProgram, GpuComputePipeline) Kernel(MetalShaders shaders, string glsl, string name)
    {
        var program = shaders.ComputeDiscrete(glsl, name);
        return (shaders.Backend(program), ctx.Pipelines.Get(new ComputePipelineDesc(program, name)));
    }

    /// <summary>
    /// The three targets at <paramref name="width"/> x <paramref name="height"/>; false when they were made again (no last gather).
    /// </summary>
    bool EnsureTargets(int width, int height)
    {
        if (_pooled is { } made && made.Desc.Width == width && made.Desc.Height == height) return true;

        Release();
        GpuTexture Target(string name) => ctx.CreateTexture(new TextureDesc(InternalFormat.Rgba16F, width, height,
            Use: TextureUse.Storage | TextureUse.Sampled, Name: name));
        (_now, _last, _pooled) = (Target("metal character gather"), Target("metal character gather"), Target("metal character gather pooled"));
        return false;
    }

    /// <summary>
    /// A sampled texture at texture index <paramref name="binding"/> and its sampler at the program's slot.
    /// </summary>
    void Bind(MTLComputeCommandEncoder e, MetalProgram program, int binding, GpuTexture texture)
    {
        var (handle, sampler) = ctx.Sampled(new SampledTexture(_nearest, texture.View()));
        e.SetTexture(handle, (ulong)binding);
        int slot = program.SamplerSlot(binding);
        if (slot >= 0) e.SetSamplerState(sampler, (ulong)slot);
    }

    /// <summary>
    /// <paramref name="value"/> in the frame's constants ring: its buffer and offset.
    /// </summary>
    (MTLBuffer Buffer, ulong Offset) Upload<T>(in T value) where T : unmanaged
    {
        var slice = ctx.Frame.AllocateConstants((ulong)sizeof(T), 256);
        *(T*)slice.Pointer = value;
        return (ctx.BufferAt(slice.Handle), slice.Offset);
    }

    /// <summary>
    /// Releases the targets (after the frames in flight).
    /// </summary>
    void Release()
    {
        foreach (var t in (ReadOnlySpan<GpuTexture?>)[_now, _last, _pooled])
            if (t is { } texture) ctx.DeferDelete(() => ctx.ReleaseTexture(texture));
        _now = _last = _pooled = null;
        _gatheredFrame = -1;
    }

    /// <summary>
    /// Releases the targets.
    /// </summary>
    public void Dispose() => Release();

    #endregion
}
