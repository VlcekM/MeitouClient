using Meitou.Rendering.Vulkan.Shaders;
using Silk.NET.Vulkan;

namespace Meitou.Rendering.Gpu;

/// <summary>
/// What a pass draws into (<see cref="GpuContext.CurrentTargets"/>): its attachments, their formats and size, and the viewport and scissor its
/// host set (the scissor clipped to the target). A guest drawing into the current pass sets these.
/// </summary>
public sealed record PassTargets(RenderTarget Colour, RenderTarget Depth, AttachmentFormats Formats, int Width, int Height, Viewport Viewport, Rect2D Scissor)
{
    public RenderingDesc Rendering => new(Colour, Depth, Width, Height);

    /// <summary>The targets of <paramref name="colour"/> and / or <paramref name="depth"/> (null: none), loaded, the whole area as viewport and scissor.</summary>
    public static PassTargets Of(Texture? colour, Texture? depth)
    {
        var any = colour ?? depth ?? throw new ArgumentException("a pass needs an attachment");
        int w = any.Desc.Width, h = any.Desc.Height;
        static RenderTarget Target(Texture? t) => t is null ? default : new RenderTarget(t.Attachment(), AttachmentLoadOp.Load, default, t.Image);
        var formats = new AttachmentFormats(colour?.Desc.Format ?? Format.Undefined, depth?.Desc.Format ?? Format.Undefined, any.Desc.Samples);
        return new PassTargets(Target(colour), Target(depth), formats, w, h, new Viewport(0, 0, w, h, 0, 1), Whole(w, h));
    }

    /// <summary>The same targets with the viewport <paramref name="viewport"/> and the scissor <paramref name="scissor"/> (clipped to the targets),
    /// or the whole viewport when the scissor is null.</summary>
    public PassTargets At(Viewport viewport, Rect2D? scissor = null)
    {
        var s = scissor ?? Whole(Width, Height);
        int x0 = Math.Max(s.Offset.X, 0), y0 = Math.Max(s.Offset.Y, 0);
        int x1 = Math.Min(s.Offset.X + (int)s.Extent.Width, Width), y1 = Math.Min(s.Offset.Y + (int)s.Extent.Height, Height);
        return this with { Viewport = viewport, Scissor = new Rect2D(new Offset2D(x0, y0), new Extent2D((uint)Math.Max(x1 - x0, 0), (uint)Math.Max(y1 - y0, 0))) };
    }

    static Rect2D Whole(int w, int h) => new(default, new Extent2D((uint)w, (uint)h));
}

/// <summary>
/// The fixed-function state a guest draws with in its host's pass (<see cref="GpuContext.CurrentState"/>): what the host hands it, made by
/// <see cref="For"/> with the rules VkGl applied to GL's state (depth test and write only with a depth attachment, write only while testing; blend
/// only with a colour attachment; alpha-to-coverage only with more than one sample; depth clamp only where the device has it).
/// </summary>
public sealed record DrawState(CullModeFlags Cull, FrontFace Front, bool DepthTest, bool DepthWrite, CompareOp Compare,
    bool BiasEnable, float BiasConstant, float BiasSlope, BlendState Blend, ColorComponentFlags ColourMask, Silk.NET.Vulkan.PolygonMode Polygon,
    bool AlphaToCoverage, bool DepthClamp)
{
    public const ColorComponentFlags Rgba = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit;

    /// <summary>
    /// The state for a draw into <paramref name="targets"/>, from the state the host means (GL's counter-clockwise front faces, which are Vulkan's
    /// clockwise with the flipped viewport, by default): VkGl's masking rules applied, so the record equals what VkGl reported for the same GL state.
    /// </summary>
    public static DrawState For(in AttachmentFormats targets, bool deviceDepthClamp, CullModeFlags cull = CullModeFlags.None, bool depthTest = false,
        bool depthWrite = true, CompareOp compare = CompareOp.Less, BlendState blend = default, ColorComponentFlags mask = Rgba, bool depthClamp = false,
        FrontFace front = FrontFace.Clockwise, bool biasEnable = false, float biasConstant = 0, float biasSlope = 0,
        Silk.NET.Vulkan.PolygonMode polygon = Silk.NET.Vulkan.PolygonMode.Fill, bool alphaToCoverage = false)
    {
        bool test = depthTest && targets.Depth != Format.Undefined;
        bool on = blend.Enable && targets.Colour != Format.Undefined;
        var b = on ? blend : BlendState.Off;
        return new DrawState(cull, front, test, test && depthWrite, compare, biasEnable, biasConstant, biasSlope, b, mask, polygon,
            alphaToCoverage && targets.Samples > 1, depthClamp && deviceDepthClamp);
    }

    /// <summary>The scene's state (the main view's and the reflection's slices): depth tested with less-or-equal and written, no culling,
    /// blending or clamp, every channel. Each guest puts in its own culling, blending and depth changes.</summary>
    public static DrawState Scene(in AttachmentFormats targets) =>
        For(targets, false, depthTest: true, depthWrite: true, compare: CompareOp.LessOrEqual);

    /// <summary>The pipeline for <paramref name="program"/> with this state.</summary>
    public GraphicsPipelineDesc Pipeline(ShaderProgram program, VertexLayout vertex, PrimitiveTopology topology, AttachmentFormats targets, string name = "") =>
        new(program, vertex, topology, targets, Blend, ColourMask, Polygon, AlphaToCoverage, DepthClamp, name);

    /// <summary>Records the dynamic state: <paramref name="t"/>'s viewport and scissor, and this cull, front face, depth and bias
    /// (<paramref name="front"/> overrides the front face, for a draw that turns the winding round).</summary>
    public void Record(CommandList cmd, PassTargets t, FrontFace? front = null)
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
    PassTargets? passTargets;
    DrawState? passState;
    CommandList? passList;

    /// <summary>A host's pass is open (<see cref="BeginHostPass"/>).</summary>
    public bool PassOpen => passList is not null;

    /// <summary>
    /// The caller has begun a rendering instance on <paramref name="cmd"/> (inside its own <c>BeginNative</c> segment) into
    /// <paramref name="targets"/>: until <see cref="EndHostPass"/> guests draw into it with <paramref name="state"/>.
    /// </summary>
    public void BeginHostPass(CommandList cmd, PassTargets targets, DrawState state)
    {
        if (passList is not null) throw new InvalidOperationException("a host pass is already open");
        (passList, passTargets, passState) = (cmd, targets, state);
    }

    /// <summary>The viewport and scissor (null: the whole viewport) the guests draw with from now on (a cascade's tile).</summary>
    public void SetPassViewport(Viewport viewport, Rect2D? scissor = null)
    {
        if (passTargets is null) throw new InvalidOperationException("no host pass is open");
        passTargets = passTargets.At(viewport, scissor);
    }

    /// <summary>The state the guests draw with from now on.</summary>
    public void SetPassState(DrawState state)
    {
        if (passList is null) throw new InvalidOperationException("no host pass is open");
        passState = state;
    }

    public void EndHostPass(CommandList cmd)
    {
        if (passList is null || !ReferenceEquals(cmd, passList)) throw new InvalidOperationException("EndHostPass without a matching BeginHostPass");
        (passList, passTargets, passState) = (null, null, null);
    }

    /// <summary>What the open pass draws into (its host's targets, viewport and scissor).</summary>
    public PassTargets CurrentTargets() => passTargets ?? throw new InvalidOperationException("no pass is open");

    /// <summary>The state the open pass's host hands its guests .</summary>
    public DrawState CurrentState() => passState ?? throw new InvalidOperationException("no pass is open");
}
