using System.Diagnostics;
using System.Numerics;
using Meitou.Game.Rendering.Gpu;

namespace Meitou.Game.Rendering.Post;

/// <summary>
/// Meitou depth of field (the <c>dof</c> switch; docs/render-post.md "Depth of field"), the one implementation for both backends: a thin lens's circle of
/// confusion from the camera's field of view, f-number and focus (<see cref="ThinLens"/>), then a half-size scatter-as-gather with a near and a far
/// field, in linear light (<see cref="CameraEffectShaders"/>). Recorded through <see cref="IGpuContext"/>, so <see cref="PostProcess"/> (Vulkan) and
/// Metal's <c>MetalPostChain</c> both run it. The picture after the upscaler goes in and the picture through the lens comes out, at the same size, with
/// no history. Port of KenshiNative's <c>docs/features/dof-rewrite.md</c> without its sparkle; KenshiNative's lens is a slider's radius, this one's is
/// the optics.
/// </summary>
/// <remarks>
/// The passes draw with <c>vUv</c> running from row 0 as Vulkan's do (<see cref="PostProcessShaders.VertexRowsDown"/> where the targets are top-down),
/// so the shaders are the Vulkan ones unchanged. Two things follow from the row order: the jitter's y is negated on top-down targets (the jitter is
/// along clip y, which runs against the rows there), and the autofocus spot, given from the picture's top, is its row on top-down targets and one
/// minus it on bottom-up ones (Vulkan's), whose row 0 is the bottom.
/// </remarks>
sealed class DepthOfField : IDisposable
{
    #region Pass programs

    /// <summary>
    /// One full-screen program: the vertex shader given with a fragment shader.
    /// </summary>
    abstract class Pass(IGpuContext ctx, string vertex, string fragment, string name)
    {
        /// <summary>
        /// The program.
        /// </summary>
        public readonly LegacyProgram P = LegacyProgram.Create(ctx, vertex, fragment, name);
    }

    /// <summary>
    /// What the two passes that compute a CoC share: the picture, its depth and the autofocus texel, their planes and jitter, the lens.
    /// </summary>
    abstract class LensPass : Pass
    {
        /// <summary>
        /// The picture, its depth and the autofocus texel.
        /// </summary>
        public readonly SamplerSlot Scene, Depth, Focus;

        /// <summary>
        /// The depth's planes, the jitter in picture units, the lens (CoC at infinity, focus, largest CoC) and the autofocus's lens.
        /// </summary>
        public readonly UniformHandle Planes, JitterUv, Lens, Auto;

        /// <summary>
        /// Compiles <paramref name="fragment"/> and looks up the lens's slots.
        /// </summary>
        protected LensPass(IGpuContext ctx, string vertex, string fragment, string name) : base(ctx, vertex, fragment, name)
        {
            (Scene, Depth, Focus) = (P.Sampler("uScene"), P.Sampler("uDepth"), P.Sampler("uFocus"));
            (Planes, JitterUv, Lens, Auto) = (P.Uniform("uPlanes"), P.Uniform("uJitterUv"), P.Uniform("uLens"), P.Uniform("uAuto"));
        }
    }

    /// <summary>
    /// The autofocus's one-texel pass (<see cref="CameraEffectShaders.DofFocus"/>).
    /// </summary>
    sealed class FocusPass : Pass
    {
        /// <summary>
        /// The scene depth and the last frame's focus texel.
        /// </summary>
        public readonly SamplerSlot Depth, Previous;

        /// <summary>
        /// The depth's planes, the jitter in picture units, the spot (ease, fallback focus, size, aspect) and its centre.
        /// </summary>
        public readonly UniformHandle Planes, JitterUv, Spot, Centre;

        /// <summary>
        /// Compiles the autofocus and looks up its slots.
        /// </summary>
        public FocusPass(IGpuContext ctx, string vertex) : base(ctx, vertex, CameraEffectShaders.DofFocus, "post dof focus")
        {
            (Depth, Previous) = (P.Sampler("uDepth"), P.Sampler("uPrevious"));
            (Planes, JitterUv, Spot, Centre) = (P.Uniform("uPlanes"), P.Uniform("uJitterUv"), P.Uniform("uSpot"), P.Uniform("uCentre"));
        }
    }

    /// <summary>
    /// Pass 1 (<see cref="CameraEffectShaders.DofPrepare"/>): the half-size light and CoC.
    /// </summary>
    sealed class PreparePass(IGpuContext ctx, string vertex) : LensPass(ctx, vertex, CameraEffectShaders.DofPrepare, "post dof prepare");

    /// <summary>
    /// Pass 6 (<see cref="CameraEffectShaders.DofComposite"/>): the two fields over the sharp picture.
    /// </summary>
    sealed class CompositePass : LensPass
    {
        /// <summary>
        /// The filtered far field and the near field.
        /// </summary>
        public readonly SamplerSlot FarField, NearField;

        /// <summary>
        /// Compiles the composite and looks up its fields' slots.
        /// </summary>
        public CompositePass(IGpuContext ctx, string vertex) : base(ctx, vertex, CameraEffectShaders.DofComposite, "post dof") =>
            (FarField, NearField) = (P.Sampler("uFarField"), P.Sampler("uNearField"));
    }

    /// <summary>
    /// Pass 2 (<see cref="CameraEffectShaders.DofTile"/>): the largest near CoC per tile.
    /// </summary>
    sealed class TilePass : Pass
    {
        /// <summary>
        /// Pass 1's output.
        /// </summary>
        public readonly SamplerSlot Prepared;

        /// <summary>
        /// The tiles' count across and down.
        /// </summary>
        public readonly UniformHandle Tiles;

        /// <summary>
        /// Compiles the tile pass and looks up its slots.
        /// </summary>
        public TilePass(IGpuContext ctx, string vertex) : base(ctx, vertex, CameraEffectShaders.DofTile, "post dof tile") =>
            (Prepared, Tiles) = (P.Sampler("uPrepared"), P.Uniform("uTiles"));
    }

    /// <summary>
    /// Pass 3 (<see cref="CameraEffectShaders.DofDilate"/>): the largest near CoC that can reach each tile.
    /// </summary>
    sealed class DilatePass : Pass
    {
        /// <summary>
        /// Pass 2's tiles.
        /// </summary>
        public readonly SamplerSlot Tile;

        /// <summary>
        /// The largest CoC and a tile's span in half-size pixels.
        /// </summary>
        public readonly UniformHandle Reach;

        /// <summary>
        /// Compiles the dilation and looks up its slots.
        /// </summary>
        public DilatePass(IGpuContext ctx, string vertex) : base(ctx, vertex, CameraEffectShaders.DofDilate, "post dof dilate") =>
            (Tile, Reach) = (P.Sampler("uTile"), P.Uniform("uReach"));
    }

    /// <summary>
    /// Pass 4 (<see cref="CameraEffectShaders.DofFar"/>): the far field's gather.
    /// </summary>
    sealed class FarPass : Pass
    {
        /// <summary>
        /// Pass 1's output.
        /// </summary>
        public readonly SamplerSlot Prepared;

        /// <summary>
        /// Compiles the far gather and looks up its slot.
        /// </summary>
        public FarPass(IGpuContext ctx, string vertex) : base(ctx, vertex, CameraEffectShaders.DofFar, "post dof far") =>
            Prepared = P.Sampler("uPrepared");
    }

    /// <summary>
    /// The far field's filter (<see cref="CameraEffectShaders.DofFarFilter"/>).
    /// </summary>
    sealed class FarFilterPass : Pass
    {
        /// <summary>
        /// The gathered far field.
        /// </summary>
        public readonly SamplerSlot Far;

        /// <summary>
        /// Compiles the far filter and looks up its slot.
        /// </summary>
        public FarFilterPass(IGpuContext ctx, string vertex) : base(ctx, vertex, CameraEffectShaders.DofFarFilter, "post dof far filter") =>
            Far = P.Sampler("uFar");
    }

    /// <summary>
    /// Pass 5 (<see cref="CameraEffectShaders.DofNear"/>): the near field's gather.
    /// </summary>
    sealed class NearPass : Pass
    {
        /// <summary>
        /// Pass 1's output and pass 3's dilated tiles.
        /// </summary>
        public readonly SamplerSlot Prepared, Dilated;

        /// <summary>
        /// Compiles the near gather and looks up its slots.
        /// </summary>
        public NearPass(IGpuContext ctx, string vertex) : base(ctx, vertex, CameraEffectShaders.DofNear, "post dof near") =>
            (Prepared, Dilated) = (P.Sampler("uPrepared"), P.Sampler("uDilated"));
    }

    #endregion

    #region Setup

    /// <summary>
    /// The autofocus's spot (its half height as a share of the picture's) and the time its focus takes to cover most of a change.
    /// </summary>
    const float FocusSpot = 0.03f, FocusSeconds = 0.15f;

    /// <summary>
    /// Half-size pixels to a tile.
    /// </summary>
    const int TileSize = 8;

    /// <summary>
    /// The camera of the frame: the vertical field of view, the planes of the scene depth, and the jitter its projection was moved by in render pixels.
    /// </summary>
    public readonly record struct Camera(float FieldOfView, float Near, float Far, Vector2 JitterPixels);

    /// <summary>
    /// What one run reads: the picture and its depth, the options, the camera, the largest CoC and the jitter in the depth's own units.
    /// </summary>
    readonly record struct Shot(GpuTexture Scene, GpuTexture Depth, PostOptions Options, Camera Camera, float MaxCoc, Vector2 JitterUv);

    /// <summary>
    /// The device the passes run on.
    /// </summary>
    readonly IGpuContext _ctx;

    /// <summary>
    /// Whether the targets' rows run up with clip y (Vulkan's); false on Metal, whose rows run down.
    /// </summary>
    readonly bool _rowsUp;

    /// <summary>
    /// The clamped nearest and linear samplers the passes read with.
    /// </summary>
    readonly SampledTexture _point, _linear;

    /// <summary>
    /// The pass pipelines made so far, by program and target format.
    /// </summary>
    readonly Dictionary<(GpuProgram Program, InternalFormat Format), GraphicsPipelineDesc> _pipelines = [];

    /// <summary>
    /// Compiles the passes and makes the samplers; the targets follow the first picture.
    /// </summary>
    public DepthOfField(IGpuContext ctx)
    {
        _ctx = ctx;
        _rowsUp = ctx.RenderTargetsBottomUp;
        string vertex = _rowsUp ? PostProcessShaders.Vertex : PostProcessShaders.VertexRowsDown;
        (_point, _linear) = (Sample(TextureMinFilter.Nearest, TextureMagFilter.Nearest), Sample(TextureMinFilter.Linear, TextureMagFilter.Linear));
        (_focusPass, _preparePass, _tilePass, _dilatePass) = (new FocusPass(ctx, vertex), new PreparePass(ctx, vertex), new TilePass(ctx, vertex),
            new DilatePass(ctx, vertex));
        (_farPass, _farFilterPass, _nearPass, _compositePass) = (new FarPass(ctx, vertex), new FarFilterPass(ctx, vertex), new NearPass(ctx, vertex),
            new CompositePass(ctx, vertex));
    }

    /// <summary>
    /// A clamped sampler with the GL filters <paramref name="min"/> and <paramref name="mag"/>, no LOD bias.
    /// </summary>
    SampledTexture Sample(TextureMinFilter min, TextureMagFilter mag) =>
        new(_ctx.Samplers.Get(SamplerDesc.FromGl(min, mag, TextureWrapMode.ClampToEdge, TextureWrapMode.ClampToEdge, TextureWrapMode.Repeat,
            false, DepthFunction.Lequal, false, 1, false, 0)), default);

    /// <summary>
    /// Releases the targets and the programs.
    /// </summary>
    public void Dispose()
    {
        Free();
        var passes = new Pass[] { _focusPass, _preparePass, _tilePass, _dilatePass, _farPass, _farFilterPass, _nearPass, _compositePass };
        foreach (var pass in passes) pass.P.Dispose();
    }

    #endregion

    #region Run

    /// <summary>
    /// The picture through the lens: the autofocus, the CoC at half size, the near tiles, the two fields and the composite at the picture's size.
    /// The caller has the segment open: <paramref name="cmd"/> records every pass. <paramref name="scene"/> is left as it was.
    /// </summary>
    public GpuTexture Run(IGpuCommandList cmd, GpuTexture scene, GpuTexture depth, PostOptions options, in Camera camera)
    {
        EnsureTargets(scene.Desc.Width, scene.Desc.Height);

        float jitterY = _rowsUp ? camera.JitterPixels.Y : -camera.JitterPixels.Y;
        var jitterUv = new Vector2(camera.JitterPixels.X / depth.Desc.Width, jitterY / depth.Desc.Height);
        var shot = new Shot(scene, depth, options, camera, ThinLens.MaxCoc(options, scene.Desc.Height), jitterUv);

        var focus = FocusNow(cmd, shot);
        DrawPrepare(cmd, shot, focus);
        DrawNearTiles(cmd, shot);
        DrawFields(cmd);
        DrawComposite(cmd, shot, focus);

        return _output!;
    }

    /// <summary>
    /// Pass 1: the half-size light and CoC.
    /// </summary>
    void DrawPrepare(IGpuCommandList cmd, in Shot shot, GpuTexture focus)
    {
        var prepare = _preparePass;
        SetLens(prepare, shot, focus);
        Fullscreen(cmd, prepare.P, _prepared!);
    }

    /// <summary>
    /// Pass 6: the far field by the full-size CoC, then the near field over it.
    /// </summary>
    void DrawComposite(IGpuCommandList cmd, in Shot shot, GpuTexture focus)
    {
        var composite = _compositePass;
        SetLens(composite, shot, focus);
        composite.P.Bind(composite.FarField, _point with { View = _farSmooth!.View() });
        composite.P.Bind(composite.NearField, _linear with { View = _nearField!.View() });
        Fullscreen(cmd, composite.P, _output!);
    }

    /// <summary>
    /// Binds a CoC pass's inputs: the autofocus texel, the autofocus's lens, the picture and its depth, and the lens.
    /// </summary>
    void SetLens(LensPass p, in Shot shot, GpuTexture focus)
    {
        p.P.Bind(p.Focus, _point with { View = focus.View() });
        var (auto, lens) = ThinLens.Uniforms(shot.Options, shot.Camera.FieldOfView, shot.Scene.Desc.Height, shot.MaxCoc);
        p.P.Set(p.Auto, auto);

        p.P.Bind(p.Scene, _point with { View = shot.Scene.View() });
        p.P.Bind(p.Depth, _point with { View = shot.Depth.View() });
        p.P.Set(p.Planes, shot.Camera.Near, shot.Camera.Far);
        p.P.Set(p.JitterUv, shot.JitterUv);
        p.P.Set(p.Lens, lens);
    }

    /// <summary>
    /// The largest near CoC per tile, then the largest that can reach each tile.
    /// </summary>
    void DrawNearTiles(IGpuCommandList cmd, in Shot shot)
    {
        var tile = _tilePass;
        tile.P.Bind(tile.Prepared, _point with { View = _prepared!.View() });
        tile.P.Set(tile.Tiles, (float)_tiles!.Desc.Width, (float)_tiles.Desc.Height);
        Fullscreen(cmd, tile.P, _tiles);

        var dilate = _dilatePass;
        dilate.P.Bind(dilate.Tile, _point with { View = _tiles.View() });
        float tileSpan = MathF.Min(_prepared.Desc.Width / (float)_tiles.Desc.Width, _prepared.Desc.Height / (float)_tiles.Desc.Height);
        dilate.P.Set(dilate.Reach, shot.MaxCoc, tileSpan);
        Fullscreen(cmd, dilate.P, _dilated!);
    }

    /// <summary>
    /// The far field's gather and filter, then the near field's gather.
    /// </summary>
    void DrawFields(IGpuCommandList cmd)
    {
        var far = _farPass;
        far.P.Bind(far.Prepared, _point with { View = _prepared!.View() });
        Fullscreen(cmd, far.P, _farField!);

        var smooth = _farFilterPass;
        smooth.P.Bind(smooth.Far, _linear with { View = _farField!.View() });
        Fullscreen(cmd, smooth.P, _farSmooth!);

        var near = _nearPass;
        near.P.Bind(near.Prepared, _point with { View = _prepared.View() });
        near.P.Bind(near.Dilated, _point with { View = _dilated!.View() });
        Fullscreen(cmd, near.P, _nearField!);
    }

    #endregion

    #region Autofocus

    /// <summary>
    /// The pass, made with the others.
    /// </summary>
    readonly FocusPass _focusPass;

    /// <summary>
    /// The autofocus's two texels: this frame's and the last's, by turns.
    /// </summary>
    readonly GpuTexture?[] _focus = new GpuTexture?[2];

    /// <summary>
    /// The time since the last autofocus, which the ease is by.
    /// </summary>
    readonly Stopwatch _focusClock = new();

    /// <summary>
    /// Which of <see cref="_focus"/> is this frame's.
    /// </summary>
    int _focusTurn;

    /// <summary>
    /// Whether the last texel of <see cref="_focus"/> holds a focus.
    /// </summary>
    bool _focusHeld;

    /// <summary>
    /// This frame's autofocus texel: the nearest surface at the picture's centre, eased from the last frame's (snapped when the last is older than
    /// half a second: the effect was off, or the view was another).
    /// </summary>
    GpuTexture FocusNow(IGpuCommandList cmd, in Shot shot)
    {
        float seconds = _focusClock.IsRunning ? (float)_focusClock.Elapsed.TotalSeconds : float.MaxValue;
        _focusClock.Restart();
        bool snap = !_focusHeld || seconds > 0.5f;

        var previous = _focus[_focusTurn]!;
        _focusTurn ^= 1;
        var now = _focus[_focusTurn]!;

        var pass = _focusPass;
        pass.P.Bind(pass.Depth, _point with { View = shot.Depth.View() });
        pass.P.Bind(pass.Previous, _point with { View = previous.View() });
        pass.P.Set(pass.Planes, shot.Camera.Near, shot.Camera.Far);
        pass.P.Set(pass.JitterUv, shot.JitterUv);
        var spot = shot.Options.LensFocusSpot;
        pass.P.Set(pass.Centre, spot.X, _rowsUp ? 1 - spot.Y : spot.Y);
        float ease = snap ? 1f : 1 - MathF.Exp(-seconds / FocusSeconds);
        float aspect = shot.Scene.Desc.Width / (float)Math.Max(shot.Scene.Desc.Height, 1);
        pass.P.Set(pass.Spot, ease, shot.Options.LensFocus, FocusSpot, aspect);
        Fullscreen(cmd, pass.P, now);
        _focusHeld = true;

        return now;
    }

    #endregion

    #region Targets and passes

    /// <summary>
    /// Pass 1 to 6's programs, made with the others.
    /// </summary>
    readonly PreparePass _preparePass;
    /// <summary>
    /// See <see cref="_preparePass"/>.
    /// </summary>
    readonly TilePass _tilePass;
    /// <summary>
    /// See <see cref="_preparePass"/>.
    /// </summary>
    readonly DilatePass _dilatePass;
    /// <summary>
    /// See <see cref="_preparePass"/>.
    /// </summary>
    readonly FarPass _farPass;
    /// <summary>
    /// See <see cref="_preparePass"/>.
    /// </summary>
    readonly FarFilterPass _farFilterPass;
    /// <summary>
    /// See <see cref="_preparePass"/>.
    /// </summary>
    readonly NearPass _nearPass;
    /// <summary>
    /// See <see cref="_preparePass"/>.
    /// </summary>
    readonly CompositePass _compositePass;

    /// <summary>
    /// The passes' targets: half size RGBA16F for the prepared light and the fields, the near CoC's tiles and their dilation, the output at the
    /// picture's size.
    /// </summary>
    GpuTexture? _prepared, _tiles, _dilated, _farField, _farSmooth, _nearField, _output;

    /// <summary>
    /// The targets for a picture of this size, kept while it stays.
    /// </summary>
    void EnsureTargets(int width, int height)
    {
        if (_output is not null && _output.Desc.Width == width && _output.Desc.Height == height) return;

        Free();
        int halfWidth = Math.Max((width + 1) / 2, 1);
        int halfHeight = Math.Max((height + 1) / 2, 1);
        int tilesAcross = Math.Max((halfWidth + TileSize - 1) / TileSize, 1);
        int tilesDown = Math.Max((halfHeight + TileSize - 1) / TileSize, 1);

        using var batch = _ctx.Uploads.Begin();
        _prepared = Make(batch, InternalFormat.Rgba16F, halfWidth, halfHeight, "post dof prepared");
        _tiles = Make(batch, InternalFormat.Rg16F, tilesAcross, tilesDown, "post dof tiles");
        _dilated = Make(batch, InternalFormat.Rg16F, tilesAcross, tilesDown, "post dof dilated");
        _farField = Make(batch, InternalFormat.Rgba16F, halfWidth, halfHeight, "post dof far");
        _farSmooth = Make(batch, InternalFormat.Rgba16F, halfWidth, halfHeight, "post dof far smooth");
        _nearField = Make(batch, InternalFormat.Rgba16F, halfWidth, halfHeight, "post dof near");
        _output = Make(batch, InternalFormat.Rgba16F, width, height, "post dof");
        for (int i = 0; i < _focus.Length; i++) _focus[i] = Make(batch, InternalFormat.R32F, 1, 1, "post dof focus");
    }

    /// <summary>
    /// A target a pass draws into and a later one samples, made through the upload batch (the transition into its layout is recorded there, not
    /// submitted at once).
    /// </summary>
    static GpuTexture Make(IGpuUploadBatch batch, InternalFormat format, int width, int height, string name) =>
        batch.Create(new TextureDesc(format, width, height, Use: TextureUse.ColourTarget | TextureUse.Sampled, Name: name));

    /// <summary>
    /// Releases the targets and the autofocus texels (the textures go after the frames in flight); the next run starts the focus afresh.
    /// </summary>
    public void Free()
    {
        foreach (var texture in (ReadOnlySpan<GpuTexture?>)[_prepared, _tiles, _dilated, _farField, _farSmooth, _nearField, _output, _focus[0], _focus[1]])
            texture?.Dispose();

        _prepared = _tiles = _dilated = _farField = _farSmooth = _nearField = _output = null;
        (_focus[0], _focus[1], _focusHeld) = (null, null, false);
    }

    /// <summary>
    /// One fullscreen triangle of <paramref name="program"/> into <paramref name="target"/> (its uniforms and samplers set before).
    /// </summary>
    void Fullscreen(IGpuCommandList cmd, LegacyProgram program, GpuTexture target)
    {
        var formats = new AttachmentFormats(target.Desc.Format, InternalFormat.None);
        if (!_pipelines.TryGetValue((program.Program, formats.Colour), out var desc))
        {
            var state = DrawState.For(formats, false);
            _pipelines[(program.Program, formats.Colour)] = desc = state.Pipeline(program.Program, program.VertexLayout([]), PrimitiveType.Triangles,
                formats, program.Name);
        }

        int width = target.Desc.Width, height = target.Desc.Height;
        cmd.BeginLabel(program.Name);
        cmd.BeginRendering(new RenderingDesc(new RenderTarget(target.Attachment(), LoadOp.DontCare, default), default, width, height));
        cmd.SetViewport(new PixelViewport(0, 0, width, height, 0, 1));
        cmd.SetScissor(new PixelRect(0, 0, (uint)width, (uint)height));
        cmd.SetRaster(CullMode.None, FrontFaceDirection.Ccw);
        cmd.SetDepth(false, false, DepthFunction.Always);
        cmd.SetDepthBias(false, 0, 0);
        cmd.BindPipeline(_ctx.Pipelines.Get(desc));
        program.Flush(cmd);
        cmd.Draw(3);
        cmd.EndRendering();
        cmd.EndLabel();
        cmd.Order();
    }

    #endregion
}
