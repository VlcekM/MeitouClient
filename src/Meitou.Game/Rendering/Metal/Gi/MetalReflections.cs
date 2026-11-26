using Meitou.Game.Rendering.Gi;
using Meitou.Game.Rendering.Gpu;

namespace Meitou.Game.Rendering.Metal.Gi;

/// <summary>
/// The indirect specular's reflections on Metal (the <c>ssr</c> switch, docs/render-gi.md "Reflections" and "Metal"): Vulkan's march, shade and apply
/// (<see cref="SsrShaders"/>) as the GI resolve's programs (<see cref="MetalGiResolve.Program"/>: their reconstruction turned round for top-down
/// targets), the rays between march and shade where the probes traced this frame (<see cref="MetalScreenRays.Reflect"/>). Render thread only.
/// </summary>
sealed class MetalReflections(MetalContext ctx) : IDisposable
{
    #region State

    /// <summary>
    /// The march's projection of a screen position to its pixel (rows bottom-up, as Vulkan's targets).
    /// </summary>
    const string PixelLine = "ivec2 ssrPixel(vec2 s) { return ivec2((s * 0.5 + 0.5) * uFullSize); }";

    /// <summary>
    /// <see cref="PixelLine"/> for top-down targets: the clip y turned round.
    /// </summary>
    const string PixelLineTopDown = "ivec2 ssrPixel(vec2 s) { return ivec2(vec2(s.x * 0.5 + 0.5, 0.5 - s.y * 0.5) * uFullSize); }";

    /// <summary>
    /// The march, the shade and the apply (made on first use).
    /// </summary>
    MetalGiResolve.Pass? _march, _shade, _apply;

    /// <summary>
    /// What the march and the rays found, and the shade's two parts (the light found, the sky's kept share), at the resolve's size.
    /// </summary>
    GpuTexture? _hit, _low, _lowSky;

    /// <summary>
    /// The nearest clamped sampler every input is read with.
    /// </summary>
    readonly GpuSampler _point = ctx.Samplers.Get(SamplerDesc.Clamped(TextureMinFilter.Nearest, TextureMagFilter.Nearest));

    /// <summary>
    /// What one run reads: the scene and its targets, the camera as the passes and as the rays take it, the block's size, and the resolve's two low
    /// results this frame (null without the GI resolve).
    /// </summary>
    public readonly record struct Frame(GpuTexture Scene, GpuTexture Depth, GpuTexture Surface, MetalGiResolve.View View, MetalScreenRays.View Rays,
        int Divisor, (GpuTexture Light, GpuTexture Sky)? Resolved);

    #endregion

    #region Run

    /// <summary>
    /// The march into the hits, the rays over them where <paramref name="rays"/> can, the shade's two parts and the apply added to the scene.
    /// </summary>
    public void Run(MetalGiResolve resolve, MetalScreenRays? rays, in Frame f)
    {
        int width = f.Scene.Desc.Width, height = f.Scene.Desc.Height;
        EnsureTargets(width, height, f.Divisor);
        March(resolve, f, width, height);
        rays?.Reflect(_hit!, f.Depth, f.Surface, f.Rays, f.Divisor);
        Shade(resolve, f, width, height);
        Apply(resolve, f, width, height);
    }

    /// <summary>
    /// The screen-space march into <see cref="_hit"/>.
    /// </summary>
    void March(MetalGiResolve resolve, in Frame f, int width, int height)
    {
        var m = _march ??= resolve.Program(TopDown(SsrShaders.March), "metal ssr march");
        resolve.SetView(m, f.Depth, f.Surface, f.View, width, height, f.Divisor);
        m.P.Bind(m.P.Sampler("uScene"), new SampledTexture(_point, f.Scene.View()));

        var cmd = (MetalCommandList)ctx.BeginNative("ssr march");
        resolve.Fullscreen(cmd, m.P, _hit!, blend: false);
        ctx.EndNative(cmd);
    }

    /// <summary>
    /// <paramref name="march"/> with its projection to a pixel turned round for top-down targets.
    /// </summary>
    static string TopDown(string march)
    {
        if (!march.Contains(PixelLine, StringComparison.Ordinal))
            throw new InvalidOperationException("MetalReflections: no pixel line in the march");

        return march.Replace(PixelLine, PixelLineTopDown, StringComparison.Ordinal);
    }

    /// <summary>
    /// The hits shaded into the two low targets at once (the resolve's results give the specular occlusion; stand-ins without it).
    /// </summary>
    void Shade(MetalGiResolve resolve, in Frame f, int width, int height)
    {
        var s = _shade ??= resolve.Program(SsrShaders.Shade, "metal ssr shade");
        resolve.SetView(s, f.Depth, f.Surface, f.View, width, height, f.Divisor);
        s.P.Bind(s.P.Sampler("uHit"), new SampledTexture(_point, _hit!.View()));
        BindOrStandIn(s, "uGiLow", f.Resolved?.Light);
        BindOrStandIn(s, "uGiLowSky", f.Resolved?.Sky);
        s.P.Set(s.P.Uniform("uHasGi"), f.Resolved is null ? 0f : 1f);
        s.P.ApplyGlobals();   // the atmosphere's uniforms, through the frame globals

        var cmd = (MetalCommandList)ctx.BeginNative("ssr shade");
        resolve.FullscreenBoth(cmd, s.P, _low!, _lowSky!);
        ctx.EndNative(cmd);
    }

    /// <summary>
    /// The shade's two parts upsampled by depth and added to the scene colour (each pixel's own reflection of the sky, through the frame globals).
    /// </summary>
    void Apply(MetalGiResolve resolve, in Frame f, int width, int height)
    {
        var a = _apply ??= resolve.Program(SsrShaders.Apply, "metal ssr apply");
        resolve.SetView(a, f.Depth, f.Surface, f.View, width, height, f.Divisor);
        a.P.Bind(a.P.Sampler("uLow"), new SampledTexture(_point, _low!.View()));
        a.P.Bind(a.P.Sampler("uLowSky"), new SampledTexture(_point, _lowSky!.View()));
        a.P.Set(a.P.Uniform("uLowSize"), _low.Desc.Width, _low.Desc.Height);
        a.P.ApplyGlobals();

        var cmd = (MetalCommandList)ctx.BeginNative("ssr apply");
        resolve.Fullscreen(cmd, a.P, f.Scene, blend: true);
        ctx.EndNative(cmd);
    }

    /// <summary>
    /// <paramref name="texture"/> at the program's <paramref name="name"/>, or the backend's stand-in where it is null.
    /// </summary>
    void BindOrStandIn(MetalGiResolve.Pass p, string name, GpuTexture? texture)
    {
        var slot = p.P.Sampler(name);
        if (!slot.IsValid) return;

        p.P.Bind(slot, texture is null ? ctx.Dummy(p.P.SamplerInfo(slot)) : new SampledTexture(_point, texture.View()));
    }

    #endregion

    #region Targets

    /// <summary>
    /// The three targets at 1/<paramref name="divisor"/> of the render size (made again when it changes); the hits also written by the rays.
    /// </summary>
    void EnsureTargets(int width, int height, int divisor)
    {
        int w = (width + divisor - 1) / divisor, h = (height + divisor - 1) / divisor;
        if (_hit is { } made && made.Desc.Width == w && made.Desc.Height == h) return;

        Release();
        _hit = Target(w, h, "metal ssr hit", TextureUse.Storage);
        _low = Target(w, h, "metal ssr low", 0);
        _lowSky = Target(w, h, "metal ssr low sky", 0);
    }

    /// <summary>
    /// A signed half-float colour target, with <paramref name="extra"/> uses.
    /// </summary>
    GpuTexture Target(int w, int h, string name, TextureUse extra) => ctx.CreateTexture(new TextureDesc(InternalFormat.Rgba16F, w, h,
        Use: TextureUse.ColourTarget | TextureUse.Sampled | extra, Name: name));

    /// <summary>
    /// Releases the targets (after the frames in flight).
    /// </summary>
    void Release()
    {
        foreach (var t in (ReadOnlySpan<GpuTexture?>)[_hit, _low, _lowSky])
            if (t is { } texture) ctx.DeferDelete(() => ctx.ReleaseTexture(texture));
        _hit = _low = _lowSky = null;
    }

    /// <summary>
    /// Releases the targets.
    /// </summary>
    public void Dispose() => Release();

    #endregion
}
