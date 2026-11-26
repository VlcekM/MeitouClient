using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Game.Rendering.Gi;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Shadows;
using SharpMetal.Metal;

namespace Meitou.Game.Rendering.Metal.Gi;

/// <summary>
/// The traced sun on Metal (docs/render-shadows.md "Traced sun", docs/render-gi.md "Metal"): Vulkan's kernels (<see cref="TracedSunShaders.Mask"/>,
/// <see cref="PenumbraFilter"/>'s planes and filter, <see cref="TracedSunVolume"/>'s sun map) over the Metal traced scene. The kernels map pixels to
/// NDC with Vulkan's bottom-up rows, so the caller's inverse takes NDC with y turned round. Render thread only.
/// </summary>
sealed unsafe class MetalTracedSun(MetalContext ctx) : IDisposable
{
    #region State

    /// <summary>
    /// The longest ray (<see cref="TracedSunPass"/>'s), within what the traced scene holds round the eye.
    /// </summary>
    const float RayLength = 8000;

    /// <summary>
    /// The rays' mask, the filter's scratch, its result and the depth's planes, at the render size.
    /// </summary>
    GpuTexture? _mask, _scratch, _filtered, _planes;

    /// <summary>
    /// The mask, planes, filter and sun-map kernels (made on first use).
    /// </summary>
    (MetalProgram Program, GpuComputePipeline Pipeline) _maskPass, _planesPass, _filterPass, _volumePass;

    /// <summary>
    /// Whether the kernels were made.
    /// </summary>
    bool _made;

    /// <summary>
    /// The filter's empty list of cells (it filters the whole picture).
    /// </summary>
    MTLBuffer _noLists;

    /// <summary>
    /// The nearest clamped sampler every input is read with.
    /// </summary>
    readonly GpuSampler _nearest = ctx.Samplers.Get(SamplerDesc.Clamped(TextureMinFilter.Nearest, TextureMagFilter.Nearest));

    /// <summary>
    /// The filtered mask of the last <see cref="Run"/> (red: what of the sun reaches the pixel).
    /// </summary>
    public GpuTexture? Filtered => _filtered;

    /// <summary>
    /// The rays' raw mask of the last <see cref="Run"/>.
    /// </summary>
    public GpuTexture? Mask => _mask;

    /// <summary>
    /// The mask kernel's constants (<see cref="TracedSunShaders.Mask"/>'s <c>Params</c>).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    struct MaskParams
    {
        /// <summary>
        /// NDC, jittered as the frame was, to the position relative to the eye.
        /// </summary>
        public Matrix4x4 Inverse;

        /// <summary>
        /// The eye relative to the traced origin (w the sun's tangent), towards the sun (w the ray's length), the size (w a pixel's width per unit of
        /// depth), the grass's ray constants.
        /// </summary>
        public Vector4 Eye, Sun, Size, Grass;
    }

    /// <summary>
    /// The penumbra kernels' constants (<see cref="PenumbraFilter"/>'s <c>Params</c>).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    struct FilterParams
    {
        /// <summary>
        /// NDC to the position relative to the eye.
        /// </summary>
        public Matrix4x4 Inverse;

        /// <summary>
        /// The size (z a pixel's width per unit of depth, w the pass), the options, the area filtered.
        /// </summary>
        public Vector4 Size, Options, Area;

        /// <summary>
        /// The cells worked on (unused over the whole picture).
        /// </summary>
        public fixed uint Cells[64];
    }

    /// <summary>
    /// The sun map kernel's constants (<see cref="TracedSunVolume"/>'s <c>Params</c>).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    struct VolumeParams
    {
        /// <summary>
        /// The eye relative to the traced origin, towards the sun, the plane's axes, the three layers, the texels and the layers traced.
        /// </summary>
        public Vector4 Eye, Sun, Right, Up, Layer0, Layer1, Layer2, Size;
    }

    #endregion

    #region Rays

    /// <summary>
    /// One ray a pixel towards the sun into the mask, then the penumbra filter into <see cref="Filtered"/>. <paramref name="inverse"/>: NDC (y up from
    /// row 0, as the kernels compute it) to the position relative to the eye; <paramref name="pixelSlope"/>: a pixel's width per unit of depth.
    /// </summary>
    public void Run(MetalGiScene scene, GpuTexture depth, GpuTexture surface, GpuTexture term, in Matrix4x4 inverse, Vector3 eye, Vector3 toSun,
        float pixelSlope)
    {
        if (scene.Top is null) return;

        int width = term.Desc.Width, height = term.Desc.Height;
        Make();
        EnsureTargets(width, height);
        var cmd = (MetalCommandList)ctx.BeginNative("traced sun");
        Trace(cmd, scene, depth, surface, term, inverse, eye, toSun, pixelSlope, width, height);
        Penumbra(cmd, depth, surface, _mask!, _scratch!, _filtered!, inverse, pixelSlope, named: false);
        cmd.EndCompute();
        ctx.EndNative(cmd);
    }

    /// <summary>
    /// The penumbra filter (<see cref="PenumbraFilter"/>) from <paramref name="raw"/> through <paramref name="scratch"/> into
    /// <paramref name="filtered"/>, the depth's planes made first; <paramref name="named"/>: the blue channel names what the others hold (the lamps').
    /// </summary>
    internal void Penumbra(MetalCommandList cmd, GpuTexture depth, GpuTexture surface, GpuTexture raw, GpuTexture scratch, GpuTexture filtered,
        in Matrix4x4 inverse, float pixelSlope, bool named)
    {
        int width = raw.Desc.Width, height = raw.Desc.Height;
        Make();
        EnsurePlanes(width, height);
        Planes(cmd, depth, inverse, pixelSlope, width, height);
        Filter(cmd, depth, surface, raw, scratch, inverse, pixelSlope, 0, named, width, height);
        Filter(cmd, depth, surface, scratch, filtered, inverse, pixelSlope, 1, named, width, height);
    }

    /// <summary>
    /// The mask kernel: depth, surface, term, the scene and its records, the cut-out maps through the bindless table.
    /// </summary>
    void Trace(MetalCommandList cmd, MetalGiScene scene, GpuTexture depth, GpuTexture surface, GpuTexture term, in Matrix4x4 inverse, Vector3 eye,
        Vector3 toSun, float pixelSlope, int width, int height)
    {
        var p = new MaskParams
        {
            Inverse = inverse,
            Eye = new Vector4(eye - scene.Origin, TracedSunPass.DiscTangent),
            Sun = new Vector4(Vector3.Normalize(toSun), RayLength),
            Size = new Vector4(width, height, 0, pixelSlope),
        };
        cmd.BindPipeline(_maskPass.Pipeline);
        var e = cmd.ComputeEncoder();
        var program = _maskPass.Program;
        BindSampled(e, program, 0, depth);
        BindSampled(e, program, 1, surface);
        BindSampled(e, program, 2, term);
        e.SetTexture(ctx.TextureAt(_mask!), 3);
        e.SetAccelerationStructure(scene.Top!.Value, 4);
        e.SetBuffer(scene.Records, 0, 5);
        SetConstants(e, p, 6);
        cmd.BindBindlessTable(new GpuProgram(program.Info.Index, program.Info), Frame.NativeShaders.BindlessSet, GpuBindPoint.Compute);
        cmd.UseResources(scene.Resident, scene.Resident.Length);
        cmd.Dispatch((uint)((width + 7) / 8), (uint)((height + 7) / 8));
    }

    /// <summary>
    /// Every pixel's plane from the depth (the filter's taps keep to the pixel's own surface).
    /// </summary>
    void Planes(MetalCommandList cmd, GpuTexture depth, in Matrix4x4 inverse, float pixelSlope, int width, int height)
    {
        cmd.BindPipeline(_planesPass.Pipeline);
        var e = cmd.ComputeEncoder();
        BindSampled(e, _planesPass.Program, 0, depth);
        e.SetTexture(ctx.TextureAt(_planes!), 4);
        SetConstants(e, new FilterParams { Inverse = inverse, Size = new Vector4(width, height, pixelSlope, 0) }, 5);
        cmd.Dispatch((uint)((width + 7) / 8), (uint)((height + 7) / 8));
    }

    /// <summary>
    /// One filter pass from <paramref name="input"/> into <paramref name="output"/>: along rows (<paramref name="pass"/> 0) or columns (1).
    /// </summary>
    void Filter(MetalCommandList cmd, GpuTexture depth, GpuTexture surface, GpuTexture input, GpuTexture output, in Matrix4x4 inverse,
        float pixelSlope, int pass, bool named, int width, int height)
    {
        var p = new FilterParams
        {
            Inverse = inverse, Size = new Vector4(width, height, pixelSlope, pass), Options = new Vector4(named ? 1 : 0, 0, 0, 0),
            Area = new Vector4(0, 0, width, height),
        };
        cmd.BindPipeline(_filterPass.Pipeline);
        var e = cmd.ComputeEncoder();
        var program = _filterPass.Program;
        BindSampled(e, program, 0, depth);
        BindSampled(e, program, 1, surface);
        BindSampled(e, program, 2, input);
        BindSampled(e, program, 3, _planes!);
        e.SetTexture(ctx.TextureAt(output), 4);
        SetConstants(e, p, 5);
        e.SetBuffer(_noLists, 0, 6);
        cmd.Dispatch((uint)((width + 7) / 8), (uint)((height + 7) / 8));
    }

    #endregion

    #region Sun map

    /// <summary>
    /// The shadow pass's sun map for what draws no traced pixel: rays from a plane towards the sun, one per texel of the nested squares, the distance
    /// to the first caster (<see cref="TracedSunVolume"/>).
    /// </summary>
    public void RunVolume(MetalGiScene scene, ShadowPass shadow)
    {
        if (scene.Top is null || shadow.TracedVolume is not { } volume) return;

        Make();
        var m = shadow.SunMap;
        var p = new VolumeParams
        {
            Eye = new Vector4(shadow.VolumeEye - scene.Origin, 0),
            Sun = new Vector4(m.ToSun, ShadowPass.SunMapRayLength),
            Right = new Vector4(m.Right, ShadowPass.SunMapHeight),
            Up = new Vector4(m.Up, 0),
            Layer0 = m.Layers[0],
            Layer1 = m.Layers[1],
            Layer2 = m.Layers[2],
            Size = new Vector4(ShadowPass.SunMapTexels, shadow.SunMapMask, 0, 0),
        };

        var cmd = (MetalCommandList)ctx.BeginNative("traced sun volume");
        cmd.BindPipeline(_volumePass.Pipeline);
        var e = cmd.ComputeEncoder();
        e.SetAccelerationStructure(scene.Top!.Value, 0);
        e.SetBuffer(scene.Records, 0, 1);
        SetConstants(e, p, 2);
        e.SetTexture(ctx.TextureAt(volume), 3);
        var program = _volumePass.Program;
        cmd.BindBindlessTable(new GpuProgram(program.Info.Index, program.Info), Frame.NativeShaders.BindlessSet, GpuBindPoint.Compute);
        cmd.UseResources(scene.Resident, scene.Resident.Length);
        const uint groups = (ShadowPass.SunMapTexels + 7) / 8;
        cmd.Dispatch(groups, groups, ShadowPass.SunMapLayers);
        cmd.EndCompute();
        ctx.EndNative(cmd);
    }

    #endregion

    #region Making

    /// <summary>
    /// The kernels and the empty list, on first use.
    /// </summary>
    void Make()
    {
        if (_made) return;

        _made = true;
        _maskPass = Compute(TracedSunShaders.Mask, "metal traced sun");
        _planesPass = Compute(PenumbraFilter.PlanesSource, "metal penumbra planes");
        _filterPass = Compute(PenumbraFilter.Source, "metal penumbra filter");
        _volumePass = Compute(TracedSunVolume.Source, "metal traced sun volume");
        _noLists = ctx.Device.NewBuffer(16, MTLResourceOptions.ResourceStorageModePrivate);
    }

    /// <summary>
    /// A discrete-set compute program and its pipeline.
    /// </summary>
    (MetalProgram, GpuComputePipeline) Compute(string glsl, string name)
    {
        var shaders = (MetalShaders)ctx.Shaders;
        var program = shaders.ComputeDiscrete(glsl, name);
        return (shaders.Backend(program), ctx.Pipelines.Get(new ComputePipelineDesc(program, name)));
    }

    /// <summary>
    /// The mask, scratch, filtered and planes targets at <paramref name="width"/> x <paramref name="height"/> (made again when it changes).
    /// </summary>
    void EnsureTargets(int width, int height)
    {
        if (_mask is { } made && made.Desc.Width == width && made.Desc.Height == height) return;

        Release();
        _mask = Target(width, height, "metal traced sun mask");
        _scratch = Target(width, height, "metal traced sun penumbra");
        _filtered = Target(width, height, "metal traced sun filtered");
    }

    /// <summary>
    /// The depth's planes at <paramref name="width"/> x <paramref name="height"/> (made again when it changes).
    /// </summary>
    void EnsurePlanes(int width, int height)
    {
        if (_planes is { } made && made.Desc.Width == width && made.Desc.Height == height) return;

        if (_planes is { } old) ctx.DeferDelete(() => ctx.ReleaseTexture(old));
        _planes = Target(width, height, "metal penumbra planes");
    }

    /// <summary>
    /// A half-float storage target.
    /// </summary>
    GpuTexture Target(int w, int h, string name) =>
        ctx.CreateTexture(new TextureDesc(InternalFormat.Rgba16F, w, h, Use: TextureUse.Storage | TextureUse.Sampled, Name: name));

    /// <summary>
    /// <paramref name="value"/> at buffer index <paramref name="binding"/> (copied into the encoder).
    /// </summary>
    static void SetConstants<T>(MTLComputeCommandEncoder e, in T value, ulong binding) where T : unmanaged
    {
        fixed (T* p = &value) e.SetBytes((nint)p, (ulong)sizeof(T), binding);
    }

    /// <summary>
    /// <paramref name="texture"/> through the nearest sampler at <paramref name="binding"/>.
    /// </summary>
    void BindSampled(MTLComputeCommandEncoder e, MetalProgram program, int binding, GpuTexture texture)
    {
        var (image, sampler) = ctx.Sampled(new SampledTexture(_nearest, texture.View()));
        e.SetTexture(image, (ulong)binding);
        int slot = program.SamplerSlot(binding);
        if (slot >= 0) e.SetSamplerState(sampler, (ulong)slot);
    }

    /// <summary>
    /// Releases the targets (after the frames in flight).
    /// </summary>
    void Release()
    {
        foreach (var t in (ReadOnlySpan<GpuTexture?>)[_mask, _scratch, _filtered])
            if (t is { } texture) ctx.DeferDelete(() => ctx.ReleaseTexture(texture));
        _mask = _scratch = _filtered = null;
    }

    /// <summary>
    /// Releases the targets and the list.
    /// </summary>
    public void Dispose()
    {
        Release();
        if (_planes is { } planes) ctx.ReleaseTexture(planes);
        if (_made) _noLists.Dispose();
    }

    #endregion
}
