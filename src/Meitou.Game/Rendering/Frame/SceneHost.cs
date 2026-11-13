using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Profiling;

namespace Meitou.Game.Rendering.Frame;

/// <summary>
/// The scene's passes as a native host (wave 4, docs/renderer-native.md 4.5 and 6; phase 8 stage 3, 8.9): <see cref="Open"/> begins a rendering of
/// the scene's targets whose contents are secondaries, clearing them by load ops where asked, and hands the guests the targets and the scene's state
/// (the scene depth's convention, <see cref="DepthConvention"/>); their segments are queued (<see cref="GpuContext.Record"/>) or recorded at once
/// into secondaries of their own (sky, water, the grass, timestamps); <see cref="Close"/> records the queued jobs on the job threads and executes
/// everything in order.
/// </summary>
sealed class SceneHost(IGpuContext ctx)
{
    /// <summary>
    /// The command list of the open rendering; null while the host is closed.
    /// </summary>
    IGpuCommandList? _cmd;

    /// <summary>
    /// Whether a rendering is open (between <see cref="Open"/> and <see cref="Close"/>).
    /// </summary>
    public bool IsOpen => _cmd is not null;

    /// <summary>
    /// Opens the host on <paramref name="targets"/> (nothing when it is open already); its jobs count towards <paramref name="stage"/>.
    /// <paramref name="clearColour"/> / <paramref name="clearDepth"/>: the attachments are cleared by the rendering's load ops (to the colour, to
    /// <see cref="DepthConvention.Clear"/>).
    /// Without secondaries (<see cref="IGpuContext.RecordsSecondaries"/>) the guests record straight into its rendering.
    /// </summary>
    public void Open(int stage, PassTargets targets, ClearColour? clearColour = null, bool clearDepth = false)
    {
        if (_cmd is not null)
        {
            Stage(stage);
            return;
        }

        var list = ctx.BeginNative("scene");
        bool secondaries = ctx.RecordsSecondaries;
        var colour = clearColour is { } c ? targets.Colour with { Load = LoadOp.Clear, Clear = c } : targets.Colour;
        var depth = clearDepth ? targets.Depth with { Load = LoadOp.Clear, DepthClear = DepthConvention.Clear } : targets.Depth;

        // The extra target (the GI resolve's albedo) is cleared to 0 with the colour: no surface there takes the probes' light in post.
        var extra = clearColour is not null && !targets.Extra.IsNull ? targets.Extra with { Load = LoadOp.Clear, Clear = default } : targets.Extra;
        var extra2 = clearColour is not null && !targets.Extra2.IsNull
            ? targets.Extra2 with { Load = LoadOp.Clear, Clear = default }
            : targets.Extra2;
        list.BeginRendering(new RenderingDesc(colour, depth, targets.Width, targets.Height, 0, 0, targets.ShadingRate, targets.ShadingRateTexel,
            extra, extra2), secondaries);
        ctx.BeginHostPassParallel(list, targets, DrawState.SceneDepth(targets.Formats, DrawState.Rgb),
            stage);   // alpha: the characters only (the SSAO character mask)
        _cmd = list;
    }

    /// <summary>
    /// The <see cref="StageClock"/> stage the guests' jobs count towards from now on.
    /// </summary>
    public void Stage(int stage)
    {
        if (_cmd is not null) ctx.SetPassStage(stage);
    }

    /// <summary>
    /// Ends the open rendering after its queued guests have executed into it; nothing when the host is closed.
    /// </summary>
    public void Close()
    {
        if (_cmd is not { } list) return;

        // Vulkan's queued guests execute into the rendering's secondaries; they must finish before EndRendering.
        ctx.EndHostPassParallel(list);
        list.EndRendering();
        ctx.EndHostPass(list);
        ctx.EndNative(list);
        _cmd = null;
    }
}
