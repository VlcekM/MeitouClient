using Meitou.Game.Rendering.Frame;
using Silk.NET.Vulkan;

namespace Meitou.Game.Rendering.Gpu;

/// <summary>
/// What a pass draws into (<see cref="GpuContext.CurrentTargets"/>): its attachments, their formats and size, and the viewport and scissor its host
/// set (the scissor clipped to the target). A guest drawing into the current pass sets these.
/// </summary>
public sealed record PassTargets(RenderTarget Colour, RenderTarget Depth, AttachmentFormats Formats, int Width, int Height, PixelViewport Viewport,
    PixelRect Scissor, RenderTarget Extra = default)
{
    /// <summary>
    /// The rendering these targets describe: their attachments and size, the shading rate attachment and the extra attachments.
    /// </summary>
    public RenderingDesc Rendering => new(Colour, Depth, Width, Height, 0, 0, ShadingRate, ShadingRateTexel, Extra, Extra2);

    /// <summary>
    /// A third colour attachment (location 2, after <see cref="Extra"/>; the traced sun's term, docs/render-shadows.md).
    /// </summary>
    public RenderTarget Extra2 { get; init; }

    /// <summary>
    /// The fragment shading rate attachment of the rendering (an R8_UINT view; null for none) and its texel size in pixels. Set by
    /// <see cref="WithShadingRate"/>, which also marks <see cref="Formats"/>.
    /// </summary>
    public GpuView ShadingRate { get; init; }

    /// <summary>
    /// The shading rate attachment's texel size in pixels (see <see cref="ShadingRate"/>).
    /// </summary>
    public int ShadingRateTexel { get; init; }

    /// <summary>
    /// These targets drawn with the shading rate image <paramref name="view"/> (texels of <paramref name="texel"/> pixels): the pipelines made for
    /// them are the ones for a rendering with that attachment.
    /// </summary>
    public PassTargets WithShadingRate(GpuView view, int texel) =>
        this with { ShadingRate = view, ShadingRateTexel = texel, Formats = Formats with { ShadingRate = true } };

    /// <summary>
    /// The targets of <paramref name="colour"/> and / or <paramref name="depth"/> (null: none), loaded, the whole area as viewport and scissor.
    /// </summary>
    public static PassTargets Of(GpuTexture? colour, GpuTexture? depth, GpuTexture? extra = null, GpuTexture? extra2 = null)
    {
        var any = colour ?? depth ?? throw new ArgumentException("a pass needs an attachment");
        int w = any.Desc.Width, h = any.Desc.Height;
        static RenderTarget Target(GpuTexture? t) => t is null ? default : new RenderTarget(t.Attachment(), LoadOp.Load, default);
        var formats = new AttachmentFormats(colour?.Desc.Format ?? InternalFormat.None, depth?.Desc.Format ?? InternalFormat.None, any.Desc.Samples,
            Extra: extra?.Desc.Format ?? InternalFormat.None, Extra2: extra2?.Desc.Format ?? InternalFormat.None);

        return new PassTargets(Target(colour), Target(depth), formats, w, h, new PixelViewport(0, 0, w, h, 0, 1), Whole(w, h), Target(extra))
        {
            Extra2 = Target(extra2),
        };
    }

    /// <summary>
    /// The same targets with the viewport <paramref name="viewport"/> and the scissor <paramref name="scissor"/> (clipped to the targets), or the
    /// whole viewport when the scissor is null.
    /// </summary>
    public PassTargets At(PixelViewport viewport, PixelRect? scissor = null)
    {
        var s = scissor ?? Whole(Width, Height);
        int x0 = Math.Max(s.X, 0), y0 = Math.Max(s.Y, 0);
        int x1 = Math.Min(s.X + (int)s.Width, Width), y1 = Math.Min(s.Y + (int)s.Height, Height);
        return this with { Viewport = viewport, Scissor = new PixelRect(x0, y0, (uint)Math.Max(x1 - x0, 0), (uint)Math.Max(y1 - y0, 0)) };
    }

    /// <summary>
    /// The rectangle of a whole <paramref name="w"/> x <paramref name="h"/> target.
    /// </summary>
    static PixelRect Whole(int w, int h) => new(0, 0, (uint)w, (uint)h);
}

/// <summary>
/// The fixed-function state a guest draws with in its host's pass (<see cref="GpuContext.CurrentState"/>): what the host hands it, made by
/// <see cref="For"/> with the rules VkGl applied to GL's state (depth test and write only with a depth attachment, write only while testing; blend
/// only with a colour attachment; alpha-to-coverage only with more than one sample; depth clamp only where the device has it).
/// </summary>
public sealed record DrawState(CullMode Cull, FrontFaceDirection Front, bool DepthTest, bool DepthWrite, DepthFunction Compare,
    bool BiasEnable, float BiasConstant, float BiasSlope, BlendState Blend, ColorMask ColourMask, PolygonKind Polygon,
    bool AlphaToCoverage, bool DepthClamp)
{
    /// <summary>
    /// Every colour channel written.
    /// </summary>
    public const ColorMask Rgba = ColorMask.All;

    /// <summary>
    /// Colour without alpha: the main scene's guests leave the alpha to the characters (the SSAO character mask, <c>PostProcess</c>).
    /// </summary>
    public const ColorMask Rgb = ColorMask.Rgb;

    /// <summary>
    /// The state for a draw into <paramref name="targets"/>, from the state the host means (GL's counter-clockwise front faces, which are Vulkan's
    /// clockwise with the flipped viewport, by default): VkGl's masking rules applied, so the record equals what VkGl reported for the same GL state.
    /// </summary>
    public static DrawState For(in AttachmentFormats targets, bool deviceDepthClamp, CullMode cull = CullMode.None, bool depthTest = false,
        bool depthWrite = true, DepthFunction compare = DepthFunction.Less, BlendState blend = default, ColorMask mask = Rgba, bool depthClamp = false,
        FrontFaceDirection front = FrontFaceDirection.Ccw, bool biasEnable = false, float biasConstant = 0, float biasSlope = 0,
        PolygonKind polygon = PolygonKind.Fill, bool alphaToCoverage = false)
    {
        bool test = depthTest && targets.Depth != InternalFormat.None;
        bool on = blend.Enable && targets.Colour != InternalFormat.None;
        var b = on ? blend : BlendState.Off;
        return new DrawState(cull, front, test, test && depthWrite, compare, biasEnable, biasConstant, biasSlope, b, mask, polygon,
            alphaToCoverage && targets.Samples > 1, depthClamp && deviceDepthClamp);
    }

    /// <summary>
    /// The scene's state (the main view's and the reflection's slices): depth tested with less-or-equal and written, no culling, blending or clamp,
    /// the channels in <paramref name="mask"/> (default every one). Each guest puts in its own culling, blending and depth changes.
    /// </summary>
    public static DrawState Scene(in AttachmentFormats targets, ColorMask mask = Rgba) =>
        For(targets, false, depthTest: true, depthWrite: true, compare: DepthFunction.Lequal, mask: mask);

    /// <summary>
    /// The scene's state on a target in the scene depth convention (the main view, the reflection, the hover mask): as <see cref="Scene"/> but
    /// compared with <see cref="DepthConvention.SceneCompare"/> (GEQUAL when reversed). Conventional targets (previews, icons, the object stage) keep
    /// <see cref="Scene"/>.
    /// </summary>
    public static DrawState SceneDepth(in AttachmentFormats targets, ColorMask mask = Rgba) =>
        For(targets, false, depthTest: true, depthWrite: true, compare: DepthConvention.SceneCompare, mask: mask);

    /// <summary>
    /// The pipeline for <paramref name="program"/> with this state.
    /// </summary>
    public GraphicsPipelineDesc Pipeline(GpuProgram program, VertexLayout vertex, PrimitiveType topology, AttachmentFormats targets,
        string name = "") =>
        new(program, vertex, topology, targets, Blend, ColourMask, Polygon, AlphaToCoverage, DepthClamp, name);

    /// <summary>
    /// Records the dynamic state: <paramref name="t"/>'s viewport and scissor, and this cull, front face, depth and bias (<paramref name="front"/>
    /// overrides the front face, for a draw that turns the winding round).
    /// </summary>
    public void Record(IGpuCommandList cmd, PassTargets t, FrontFaceDirection? front = null)
    {
        cmd.SetViewport(t.Viewport);
        cmd.SetScissor(t.Scissor);
        cmd.SetRaster(Cull, front ?? Front);
        cmd.SetDepth(DepthTest, DepthWrite, Compare);
        cmd.SetDepthBias(BiasEnable, BiasConstant, BiasSlope);
    }
}

/// <summary>
/// Passes (docs/renderer-native.md 8.9): a host opens a rendering instance on its command list and announces it with its targets and state
/// (<see cref="BeginHostPass"/>); its guests draw into it through <c>BeginNativeInPass</c>, reading <see cref="CurrentTargets"/> and
/// <see cref="CurrentState"/>, which answer what the host handed over, never GL state.
/// </summary>
public sealed unsafe partial class GpuContext
{
    /// <summary>
    /// The open host pass's targets, viewport and scissor.
    /// </summary>
    PassTargets? _passTargets;
    /// <summary>
    /// The state the open host pass hands its guests.
    /// </summary>
    DrawState? _passState;
    /// <summary>
    /// The open host pass's command list; null when no host pass is open.
    /// </summary>
    CommandList? _passList;

    /// <summary>
    /// A host's pass is open (<see cref="BeginHostPass"/>).
    /// </summary>
    public bool PassOpen => _passList is not null;

    /// <summary>
    /// The caller has begun a rendering instance on <paramref name="cmd"/> (inside its own <c>BeginNative</c> segment) into
    /// <paramref name="targets"/>: until <see cref="EndHostPass"/> guests draw into it with <paramref name="state"/>.
    /// </summary>
    public void BeginHostPass(CommandList cmd, PassTargets targets, DrawState state) => BeginHostPass((IGpuCommandList)cmd, targets, state);

    /// <inheritdoc/>
    public void BeginHostPass(IGpuCommandList cmd, PassTargets targets, DrawState state)
    {
        if (_passList is not null) throw new InvalidOperationException("a host pass is already open");

        (_passList, _passTargets, _passState) = ((CommandList)cmd, targets, state);
    }

    /// <inheritdoc/>
    public void BeginHostPassParallel(IGpuCommandList cmd, PassTargets targets, DrawState state, int stage)
    {
        BeginHostPass(cmd, targets, state);
        if (RecordsSecondaries) Frame.Parallel.Begin((CommandList)cmd, targets.Formats, stage);
    }

    /// <inheritdoc/>
    public void SetPassStage(int stage)
    {
        if (Frame.Parallel.Open) Frame.Parallel.Stage = stage;
    }

    /// <inheritdoc/>
    public void EndHostPassParallel(IGpuCommandList cmd)
    {
        if (Frame.Parallel.Open) Frame.Parallel.End();
    }

    /// <inheritdoc/>
    public bool DepthClamp => Device.DepthClamp;

    /// <inheritdoc/>
    public bool RecordsSecondaries => Recording.Secondaries;

    /// <summary>
    /// The viewport and scissor (null: the whole viewport) the guests draw with from now on (a cascade's tile).
    /// </summary>
    public void SetPassViewport(PixelViewport viewport, PixelRect? scissor = null)
    {
        if (_passTargets is null) throw new InvalidOperationException("no host pass is open");

        _passTargets = _passTargets.At(viewport, scissor);
    }

    /// <summary>
    /// The state the guests draw with from now on.
    /// </summary>
    public void SetPassState(DrawState state)
    {
        if (_passList is null) throw new InvalidOperationException("no host pass is open");

        _passState = state;
    }

    /// <summary>
    /// Closes the host pass on <paramref name="cmd"/> (it must be the open one).
    /// </summary>
    public void EndHostPass(IGpuCommandList cmd)
    {
        if (_passList is null || !ReferenceEquals(cmd, _passList))
            throw new InvalidOperationException("EndHostPass without a matching BeginHostPass");

        (_passList, _passTargets, _passState) = (null, null, null);
    }

    /// <summary>
    /// What the open pass draws into (its host's targets, viewport and scissor).
    /// </summary>
    public PassTargets CurrentTargets() => _passTargets ?? throw new InvalidOperationException("no pass is open");

    /// <summary>
    /// The state the open pass's host hands its guests.
    /// </summary>
    public DrawState CurrentState() => _passState ?? throw new InvalidOperationException("no pass is open");
}
