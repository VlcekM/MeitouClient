using System.Numerics;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Gpu;

namespace Meitou.Game.Rendering.Post;

/// <summary>
/// GTAO in the shared composite (docs/render-post.md "GTAO"): Vulkan's programs (<see cref="GtaoShaders"/>) for the backend
/// <see cref="PostComposite"/> serves. The depth packed with the characters' mark and its coarser levels, the horizon search into the occlusion's
/// first target, and the apply that multiplies the scene by the blurred occlusion's coloured term. The blur between them is the composite's SSAO
/// blur.
/// </summary>
sealed class CompositeGtao : IDisposable
{
    #region Setup

    /// <summary>
    /// The coarsest level of the gather's depth.
    /// </summary>
    const int Levels = 4;

    /// <summary>
    /// What a tap's reach in pixels (as a power of two) gives up before its level rises (XeGTAO's 3.3).
    /// </summary>
    const float LevelOffset = 3.3f;

    /// <summary>
    /// The device the passes run on.
    /// </summary>
    readonly IGpuContext _ctx;

    /// <summary>
    /// The full-screen vertex shader the composite's passes share (its <c>vUv</c> flipped for top-down targets).
    /// </summary>
    readonly string _vertex;

    /// <summary>
    /// The nearest sampler every input is read with (the passes fetch texels).
    /// </summary>
    readonly SampledTexture _point;

    /// <summary>
    /// The pack and the depth filter, made on first use.
    /// </summary>
    Program? _pack, _depthLevel;

    /// <summary>
    /// The gathers and the applies, each made on first use: [0] without the surface target, [1] with it.
    /// </summary>
    readonly Program?[] _gather = new Program?[2], _apply = new Program?[2];

    /// <summary>
    /// The gather's depth at the render size with its coarser levels (<see cref="GtaoShaders.Pack"/>).
    /// </summary>
    GpuTexture? _packed;

    /// <summary>
    /// One program and its slots by name.
    /// </summary>
    sealed class Program(IGpuContext ctx, string vertex, string fragment, string name)
    {
        /// <summary>
        /// The program.
        /// </summary>
        public readonly LegacyProgram P = LegacyProgram.Create(ctx, vertex, fragment, name);

        /// <summary>
        /// Sets a float uniform by its GLSL name (none where the program lacks it).
        /// </summary>
        public void Set(string name, float value)
        {
            if (P.Uniform(name) is { Index: >= 0 } u) P.Set(u, value);
        }

        /// <summary>
        /// Sets a vec2 uniform by its GLSL name.
        /// </summary>
        public void Set(string name, float x, float y)
        {
            if (P.Uniform(name) is { Index: >= 0 } u) P.Set(u, x, y);
        }

        /// <summary>
        /// Sets a vec3 uniform by its GLSL name.
        /// </summary>
        public void Set(string name, Vector3 value)
        {
            if (P.Uniform(name) is { Index: >= 0 } u) P.Set(u, value);
        }

        /// <summary>
        /// Sets a vec4 uniform by its GLSL name.
        /// </summary>
        public void Set(string name, Vector4 value)
        {
            if (P.Uniform(name) is { Index: >= 0 } u) P.Set(u, value);
        }
    }

    /// <summary>
    /// The gather's view and march this frame.
    /// </summary>
    /// <param name="Slices">The slices per pixel.</param>
    /// <param name="Steps">The taps along each side of a slice.</param>
    /// <param name="FullSize">The occlusion's targets are at the render size (else half).</param>
    /// <param name="Noise">The noise's offset (moving over the frames with a temporal resolve to average them, still without).</param>
    /// <param name="Surface">
    /// The GI resolve's surface target, with this frame's normals and albedos (null: the depth's normals, a constant albedo).
    /// </param>
    public readonly record struct Frame(int Slices, int Steps, bool FullSize, float Noise, GpuTexture? Surface);

    /// <summary>
    /// The programs are made on first use.
    /// </summary>
    public CompositeGtao(IGpuContext ctx, string vertex)
    {
        _ctx = ctx;
        _vertex = vertex;
        var sampler = ctx.Samplers.Get(SamplerDesc.Clamped(TextureMinFilter.Nearest, TextureMagFilter.Nearest));
        _point = new SampledTexture(sampler, default);
    }

    #endregion

    #region Passes

    /// <summary>
    /// The packed depth, its levels and the horizon search into <paramref name="target"/> (R the visibility, G the view depth, as the SSAO writes).
    /// <paramref name="up"/>: the camera's up axis as the composite's passes rebuild rays (negated for top-down targets).
    /// </summary>
    public void Gather(PostComposite composite, IGpuCommandList cmd, GpuTexture scene, GpuTexture depth, GpuTexture target,
        in PostComposite.SceneInputs i, in Frame f, Vector3 up)
    {
        int w = scene.Desc.Width;
        int h = scene.Desc.Height;
        EnsurePacked(w, h);

        Pack(composite, cmd, scene, depth, i, w, h);
        BuildLevels(composite, cmd, i, w, h);
        Search(composite, cmd, target, i, f, up, w, h);
    }

    /// <summary>
    /// The blurred occlusion <paramref name="ao"/> multiplied onto <paramref name="scene"/>, upsampled by depth, with the multi-bounce colour.
    /// </summary>
    public void Apply(PostComposite composite, IGpuCommandList cmd, GpuTexture scene, GpuTexture depth, GpuTexture ao, in PostComposite.SceneInputs i,
        GpuTexture? surface)
    {
        bool withSurface = surface is not null;
        var p = _apply[withSurface ? 1 : 0] ??= new Program(_ctx, _vertex, GtaoShaders.Apply(withSurface), "post gtao apply");
        p.P.Bind(p.P.Sampler("uAo"), _point with { View = ao.View() });
        p.P.Bind(p.P.Sampler("uDepth"), _point with { View = depth.View() });
        if (surface is not null) p.P.Bind(p.P.Sampler("uSurface"), _point with { View = surface.View() });

        int w = scene.Desc.Width;
        int h = scene.Desc.Height;
        p.Set("uSize", w, h);
        p.Set("uAoSize", ao.Desc.Width, ao.Desc.Height);
        p.Set("uNearFar", i.Near, i.Far);

        // A half-size texel's gather read the depth texel past its centre (GtaoShaders' floor): a quarter of that texel.
        float shift = ao.Desc.Width < w ? 0.25f : 0f;
        p.Set("uCurve", new Vector4(PostOptions.GtaoPower, PostOptions.GtaoStrength, PostOptions.GtaoAlbedo, shift));

        var state = DrawState.For(new AttachmentFormats(scene.Desc.Format, InternalFormat.None), false) with
        {
            Blend = new BlendState(true, BlendingFactor.Zero, BlendingFactor.SrcColor),
            ColourMask = DrawState.Rgb,
        };
        composite.Fullscreen(cmd, p.P, scene.Attachment(), scene.Desc.Format, w, h, state, LoadOp.Load);
    }

    /// <summary>
    /// Each pixel's view depth, 0 on the sky, negative on a character's.
    /// </summary>
    void Pack(PostComposite composite, IGpuCommandList cmd, GpuTexture scene, GpuTexture depth, in PostComposite.SceneInputs i, int w, int h)
    {
        var p = _pack ??= new Program(_ctx, _vertex, GtaoShaders.Pack, "post gtao pack");
        p.P.Bind(p.P.Sampler("uDepth"), _point with { View = depth.View() });
        p.P.Bind(p.P.Sampler("uScene"), _point with { View = scene.View() });
        p.Set("uNearFar", i.Near, i.Far);
        composite.Fullscreen(cmd, p.P, _packed!.Attachment(), _packed.Desc.Format, w, h);
    }

    /// <summary>
    /// The packed depth's coarser levels, each from the one before (read through a view of that level alone, so no pass samples the level it writes).
    /// </summary>
    void BuildLevels(PostComposite composite, IGpuCommandList cmd, in PostComposite.SceneInputs i, int w, int h)
    {
        var p = _depthLevel ??= new Program(_ctx, _vertex, GtaoShaders.DepthLevel, "post gtao depth level");
        p.Set("uReach", Reach(h));
        for (int m = 1; m <= Levels; m++)
        {
            int lw = Math.Max(w >> m, 1);
            int lh = Math.Max(h >> m, 1);
            p.P.Bind(p.P.Sampler("uPacked"), _point with { View = _packed!.View(m - 1, 1) });
            p.Set("uFrom", 0, 2 * MathF.Tan(i.FieldOfView * 0.5f) / h);
            composite.Fullscreen(cmd, p.P, _packed.Attachment(m), _packed.Desc.Format, lw, lh);
        }
    }

    /// <summary>
    /// The horizon search into <paramref name="target"/>.
    /// </summary>
    void Search(PostComposite composite, IGpuCommandList cmd, GpuTexture target, in PostComposite.SceneInputs i, in Frame f, Vector3 up, int w,
        int h)
    {
        bool withSurface = f.Surface is not null;
        var p = _gather[withSurface ? 1 : 0] ??= new Program(_ctx, _vertex, GtaoShaders.Gather(withSurface), "post gtao");
        p.P.Bind(p.P.Sampler("uPacked"), _point with { View = _packed!.View() });
        if (f.Surface is { } surface) p.P.Bind(p.P.Sampler("uSurface"), _point with { View = surface.View() });

        p.Set("uLevels", Levels, LevelOffset);
        p.Set("uCharacterOccluder", i.CharactersDrawn ? PostOptions.SsaoCharacterOccluder : 1f);
        SetView(p, i, up, w, h);
        p.Set("uReach", Reach(h));
        p.Set("uMarch", new Vector4(f.Slices, f.Steps, PostOptions.GtaoThickness, f.Noise));
        p.Set("uFadeStart", PostOptions.SsaoFadeStart);
        p.Set("uFadeEnd", PostOptions.SsaoFadeEnd);
        p.P.ApplyGlobals();   // the atmosphere's and the fog volumes' uniforms, through the frame globals
        composite.Fullscreen(cmd, p.P, target.Attachment(), target.Desc.Format, target.Desc.Width, target.Desc.Height);
    }

    /// <summary>
    /// The gather's view: the frustum's tangents, the depth's planes, the render size, the camera's axes and eye, the water's height.
    /// </summary>
    static void SetView(Program p, in PostComposite.SceneInputs i, Vector3 up, int w, int h)
    {
        float tanY = MathF.Tan(i.FieldOfView * 0.5f);
        var r = i.View with { M41 = 0, M42 = 0, M43 = 0 };
        var tan = ViewWindow.Tan(tanY * i.Aspect, tanY);
        p.Set("uTan", tan.X, tan.Y);
        p.Set("uNearFar", i.Near, i.Far);
        p.Set("uSize", w, h);
        p.Set("uRight", new Vector3(r.M11, r.M21, r.M31));
        p.Set("uUp", up);
        p.Set("uBack", ViewWindow.BackFor(r, i.FieldOfView, i.Aspect));
        p.Set("uGtaoEye", i.Eye);
        p.Set("uWaterY", i.WaterHeight ?? float.MinValue);
    }

    /// <summary>
    /// The gather's reach as the gather and the depth filter read it: the radius in world units, the most and the least pixels, the full-weight
    /// share.
    /// </summary>
    static Vector4 Reach(int height) =>
        new(PostOptions.GtaoRadius, PostOptions.GtaoMaxPixels * height, PostOptions.GtaoMinPixels * height, PostOptions.GtaoFalloff);

    #endregion

    #region Targets

    /// <summary>
    /// The packed depth at <paramref name="width"/> x <paramref name="height"/> with its levels (made again at a new size).
    /// </summary>
    void EnsurePacked(int width, int height)
    {
        if (_packed is { } made && made.Desc.Width == width && made.Desc.Height == height) return;

        _packed?.Dispose();   // released after the frames in flight
        _packed = _ctx.CreateTexture(new TextureDesc(InternalFormat.R32F, width, height, Levels + 1,
            Use: TextureUse.ColourTarget | TextureUse.Sampled, Name: "post gtao depth"));
    }

    /// <summary>
    /// Releases the programs and the packed depth.
    /// </summary>
    public void Dispose()
    {
        foreach (var p in (ReadOnlySpan<Program?>)[_pack, _depthLevel, .. _gather, .. _apply]) p?.P.Dispose();
        _packed?.Dispose();
    }

    #endregion
}
