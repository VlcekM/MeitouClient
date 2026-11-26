using System.Numerics;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Gi;
using Meitou.Game.Rendering.Gpu;

namespace Meitou.Game.Rendering.Metal.Gi;

/// <summary>
/// The GI resolve on Metal (docs/render-gi.md "Resolve" and "Metal"): Vulkan's two passes with the same shaders (<see cref="GiResolveShaders"/>), the
/// probes' light at 1/divisor of the render size from the scene's surface target, then the depth-aware upsample added to the scene colour. Metal's
/// targets keep row 0 at the top, so the position reconstruction turns its y round.
/// </summary>
sealed class MetalGiResolve(MetalContext ctx) : IDisposable
{
    /// <summary>
    /// The reconstruction's line that maps a pixel to NDC (rows bottom-up, as Vulkan's targets).
    /// </summary>
    const string NdcLine = "vec2 ndc = (vec2(px) + 0.5) / uFullSize * 2.0 - 1.0;";

    /// <summary>
    /// The scene colour gains the resolve: source one, destination one, red, green and blue (the alpha is the characters' mask).
    /// </summary>
    static readonly BlendState AddBlend = new(true, BlendingFactor.One, BlendingFactor.One);

    /// <summary>
    /// The resolve programs by relocation (1) and the radiance cache (2), and the apply.
    /// </summary>
    readonly Pass?[] _resolves = new Pass?[4];

    /// <summary>
    /// See <see cref="_resolves"/>.
    /// </summary>
    Pass? _apply;

    /// <summary>
    /// The low targets: the probes' light (alpha the signed view depth) and the share of the sky's ambient taken back.
    /// </summary>
    GpuTexture? _low, _lowSky;

    /// <summary>
    /// The pipelines by program and target format.
    /// </summary>
    readonly Dictionary<(GpuProgram, InternalFormat, InternalFormat, BlendState?), GraphicsPipelineDesc> _pipelines = [];

    /// <summary>
    /// The nearest clamped sampler every input is read with.
    /// </summary>
    readonly GpuSampler _point = ctx.Samplers.Get(SamplerDesc.Clamped(TextureMinFilter.Nearest, TextureMagFilter.Nearest));

    /// <summary>
    /// The camera the surfaces were drawn from: eye, view matrix, vertical field of view, aspect and the depth's planes.
    /// </summary>
    public readonly record struct View(Vector3 Eye, Matrix4x4 ViewMatrix, float FieldOfView, float Aspect, float Near, float Far);

    /// <summary>
    /// The radiance cache's view of <paramref name="view"/> over a scene of <paramref name="width"/> x <paramref name="height"/> resolved at
    /// 1/<paramref name="divisor"/> (the low target's size).
    /// </summary>
    public static GiCache.View CacheView(in View view, int width, int height, int divisor)
    {
        float tanY = MathF.Tan(view.FieldOfView * 0.5f);
        var rot = view.ViewMatrix with { M41 = 0, M42 = 0, M43 = 0 };
        var right = new Vector3(rot.M11, rot.M21, rot.M31);
        var up = new Vector3(rot.M12, rot.M22, rot.M32);
        var back = ViewWindow.BackFor(rot, view.FieldOfView, view.Aspect);
        var tan = ViewWindow.Tan(tanY * view.Aspect, tanY);
        int w = (width + divisor - 1) / divisor, h = (height + divisor - 1) / divisor;
        return new GiCache.View(view.Eye, right, up, back, tan, new Vector2(view.Near, view.Far), w, h, divisor, width, height, tanY);
    }

    /// <summary>
    /// The resolve and the apply over <paramref name="scene"/> for <paramref name="view"/>, at 1/<paramref name="divisor"/> of its size; with
    /// <paramref name="cache"/> the radiance cache's light in place of the probes' where it holds.
    /// </summary>
    public void Run(GpuTexture scene, GpuTexture depth, GpuTexture surface, in View view, int divisor, bool relocate, GpuTexture? cache)
    {
        int width = scene.Desc.Width, height = scene.Desc.Height;
        EnsureTargets(width, height, divisor);

        int variant = (relocate ? 1 : 0) | (cache is null ? 0 : 2);
        var r = _resolves[variant] ??= new Pass(ctx, Flip(GiResolveShaders.ResolveFor(relocate, cache is not null)), "metal gi resolve");
        if (cache is not null) r.P.Bind(r.Cache, new SampledTexture(_point, cache.View()));
        var a = _apply ??= new Pass(ctx, Flip(GiResolveShaders.Apply), "metal gi apply");
        foreach (var p in (ReadOnlySpan<Pass>)[r, a]) SetView(p, depth, surface, view, width, height, divisor);

        var cmd = (MetalCommandList)ctx.BeginNative("gi resolve");
        r.P.ApplyGlobals();
        FullscreenBoth(cmd, r.P, _low!, _lowSky!);

        a.P.Bind(a.Low, new SampledTexture(_point, _low!.View()));
        a.P.Bind(a.LowSky, new SampledTexture(_point, _lowSky!.View()));
        a.P.Set(a.LowSize, (float)_low.Desc.Width, (float)_low.Desc.Height);
        a.P.ApplyGlobals();
        Fullscreen(cmd, a.P, scene, blend: true);
        ctx.EndNative(cmd);
    }

    /// <summary>
    /// The last run's two low results: the probes' light and the share of the sky's ambient taken back (null before the first run).
    /// </summary>
    internal (GpuTexture Light, GpuTexture Sky)? Results => _low is { } light && _lowSky is { } sky ? (light, sky) : null;

    /// <summary>
    /// <paramref name="shader"/> with its reconstruction's y turned round for top-down targets.
    /// </summary>
    internal static string Flip(string shader)
    {
        if (!shader.Contains(NdcLine, StringComparison.Ordinal)) throw new InvalidOperationException("MetalGiResolve: no NDC line");

        return shader.Replace(NdcLine, NdcLine + "\n    ndc.y = -ndc.y;", StringComparison.Ordinal);
    }

    /// <summary>
    /// What both passes read of the depth, the surface and the camera.
    /// </summary>
    internal void SetView(Pass p, GpuTexture depth, GpuTexture surface, in View view, int width, int height, int divisor)
    {
        float tanY = MathF.Tan(view.FieldOfView * 0.5f);
        var rot = view.ViewMatrix with { M41 = 0, M42 = 0, M43 = 0 };
        p.P.Bind(p.NearDepth, new SampledTexture(_point, depth.View()));
        p.P.Bind(p.Albedo, new SampledTexture(_point, surface.View()));
        p.P.Set(p.NearPlanes, view.Near, view.Far);
        p.P.Set(p.Tan, ViewWindow.Tan(tanY * view.Aspect, tanY));
        p.P.Set(p.Right, rot.M11, rot.M21, rot.M31);
        p.P.Set(p.Up, rot.M12, rot.M22, rot.M32);
        p.P.Set(p.Back, ViewWindow.BackFor(rot, view.FieldOfView, view.Aspect));
        p.P.Set(p.Eye, view.Eye);
        p.P.Set(p.FullSize, (float)width, (float)height);
        p.P.Set(p.Div, (float)divisor);
    }

    /// <summary>
    /// The low targets at 1/<paramref name="divisor"/> of the render size (made again when it changes).
    /// </summary>
    void EnsureTargets(int width, int height, int divisor)
    {
        int w = (width + divisor - 1) / divisor, h = (height + divisor - 1) / divisor;
        if (_low is { } low && low.Desc.Width == w && low.Desc.Height == h) return;

        Release();
        _low = Target(w, h, "metal gi resolve");
        _lowSky = Target(w, h, "metal gi resolve sky");
    }

    /// <summary>
    /// A signed half-float colour target.
    /// </summary>
    GpuTexture Target(int w, int h, string name) =>
        ctx.CreateTexture(new TextureDesc(InternalFormat.Rgba16F, w, h, Use: TextureUse.ColourTarget | TextureUse.Sampled, Name: name));

    /// <summary>
    /// One fullscreen triangle of <paramref name="program"/> into <paramref name="target"/>; <paramref name="blend"/> adds it to the target's red,
    /// green and blue.
    /// </summary>
    internal void Fullscreen(MetalCommandList cmd, LegacyProgram program, GpuTexture target, bool blend, bool load = false) =>
        Fullscreen(cmd, program, target, blend ? AddBlend : null, load);

    /// <summary>
    /// One fullscreen triangle of <paramref name="program"/> into <paramref name="target"/>'s red, green and blue with <paramref name="blend"/> (none:
    /// written as it is).
    /// </summary>
    internal void Fullscreen(MetalCommandList cmd, LegacyProgram program, GpuTexture target, BlendState? blend, bool load) =>
        Draw(cmd, program, target, null, blend, load);

    /// <summary>
    /// One fullscreen triangle of <paramref name="program"/> into <paramref name="first"/> (location 0) and <paramref name="second"/> (location 1,
    /// of the same size) at once: a shader with two outputs, written as they are.
    /// </summary>
    internal void FullscreenBoth(MetalCommandList cmd, LegacyProgram program, GpuTexture first, GpuTexture second) =>
        Draw(cmd, program, first, second, null, false);

    /// <summary>
    /// One fullscreen triangle of <paramref name="program"/> into <paramref name="target"/> and, when given, <paramref name="second"/> at location 1,
    /// with <paramref name="blend"/> on their red, green and blue (none: written as they are).
    /// </summary>
    void Draw(MetalCommandList cmd, LegacyProgram program, GpuTexture target, GpuTexture? second, BlendState? blend, bool load)
    {
        int w = target.Desc.Width, h = target.Desc.Height;
        var formats = new AttachmentFormats(target.Desc.Format, InternalFormat.None, Extra: second?.Desc.Format ?? InternalFormat.None);
        var key = (program.Program, formats.Colour, formats.Extra, blend);
        if (!_pipelines.TryGetValue(key, out var desc))
        {
            var state = DrawState.For(formats, false);
            if (blend is { } b) state = state with { Blend = b, ColourMask = DrawState.Rgb };
            _pipelines[key] = desc = state.Pipeline(program.Program, program.VertexLayout([]), PrimitiveType.Triangles, formats, program.Name);
        }

        var loadOp = blend is not null || load ? LoadOp.Load : LoadOp.DontCare;
        var extra = second is null ? default : new RenderTarget(second.Attachment(), loadOp, default);
        cmd.BeginRendering(new RenderingDesc(new RenderTarget(target.Attachment(), loadOp, default), default, w, h, Extra: extra));
        cmd.SetViewport(new PixelViewport(0, 0, w, h, 0, 1));
        cmd.SetScissor(new PixelRect(0, 0, (uint)w, (uint)h));
        cmd.SetRaster(CullMode.None, FrontFaceDirection.Ccw);
        cmd.SetDepth(false, false, DepthFunction.Always);
        cmd.SetDepthBias(false, 0, 0);
        cmd.BindPipeline(ctx.Pipelines.Get(desc));
        program.Flush(cmd);
        cmd.Draw(3);
        cmd.EndRendering();
    }

    /// <summary>
    /// Releases the low targets.
    /// </summary>
    void Release()
    {
        if (_low is { } low) ctx.DeferDelete(() => ctx.ReleaseTexture(low));
        if (_lowSky is { } sky) ctx.DeferDelete(() => ctx.ReleaseTexture(sky));
        _low = _lowSky = null;
    }

    /// <summary>
    /// Releases the targets and the programs.
    /// </summary>
    public void Dispose()
    {
        Release();
        foreach (var p in _resolves) p?.P.Dispose();
        _apply?.P.Dispose();
    }

    /// <summary>
    /// A fullscreen GI pass of <paramref name="fragment"/> (its reconstruction's y turned round), for passes over the GI surfaces beside the resolve.
    /// </summary>
    internal Pass Program(string fragment, string name) => new(ctx, fragment.Contains(NdcLine, StringComparison.Ordinal) ? Flip(fragment) : fragment,
        name);

    /// <summary>
    /// The nearest clamped sampler the passes read their inputs with.
    /// </summary>
    internal GpuSampler Point => _point;

    /// <summary>
    /// A resolve program and its handles (the shared reconstruction's, the resolve's part and the apply's inputs).
    /// </summary>
    internal sealed class Pass
    {
        /// <summary>
        /// The program.
        /// </summary>
        public readonly LegacyProgram P;

        /// <summary>
        /// The samplers: the scene depth, the surface, the two low targets (the apply's).
        /// </summary>
        public readonly SamplerSlot NearDepth, Albedo, Low, LowSky, Cache;

        /// <summary>
        /// The uniforms of the reconstruction and the apply's low size.
        /// </summary>
        public readonly UniformHandle NearPlanes, Tan, Right, Up, Back, Eye, FullSize, Div, LowSize;

        /// <summary>
        /// Compiles <paramref name="fragment"/> with the post chain's fullscreen vertex shader and looks its handles up.
        /// </summary>
        public Pass(MetalContext ctx, string fragment, string name)
        {
            P = LegacyProgram.Create(ctx, Post.PostProcessShaders.Vertex, fragment, name);
            (NearDepth, Albedo, Low, LowSky) = (P.Sampler("uNearDepth"), P.Sampler("uAlbedo"), P.Sampler("uLow"), P.Sampler("uLowSky"));
            Cache = P.Sampler("uGiCache");
            (NearPlanes, Tan, Right, Up, Back) = (P.Uniform("uNearPlanes"), P.Uniform("uTan"), P.Uniform("uRight"), P.Uniform("uUp"), P.Uniform("uBack"));
            (Eye, FullSize, Div, LowSize) = (P.Uniform("uGiEye"), P.Uniform("uFullSize"), P.Uniform("uDiv"), P.Uniform("uLowSize"));
        }
    }
}
