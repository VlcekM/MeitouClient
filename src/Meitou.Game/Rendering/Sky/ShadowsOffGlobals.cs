using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Profiling;
using Meitou.Game.Rendering.Shadows;

namespace Meitou.Game.Rendering.Sky;

/// <summary>
/// (Phase 8 stage 2.) The shadow blocks a world program reads when there is no <see cref="ShadowPass"/> (<c>--no-shadows</c>), as frame globals:
/// zeros (shadows off), what <c>ShadowShaders.Bind</c> put on the GL binding points through the unused GL program the terrain linked for it.
/// Published only where nothing is yet; a <see cref="ShadowPass"/> publishes its own over them whenever it is made. The shadow textures stay
/// unpublished: readers get the stand-in, as an empty GL unit gave. Called by the sky, which every world view has.
/// </summary>
static class ShadowsOffGlobals
{
    /// <summary>
    /// Publishes zeroed receiver, caster and Meitou shadow blocks where nothing is published yet.
    /// </summary>
    public static void Publish(IGpuContext gpu)
    {
        var g = gpu.Globals;
        foreach (var (name, bytes) in new[]
                 {
                     (ShadowShaders.ReceiverBlock, ShadowPass.ReceiverBytes), (ShadowShaders.CasterBlock, 16),
                     (MeitouShadowShaders.Block, MeitouShadowShaders.BlockBytes),
                 })
            if (g.Block(name) is null) g.Publish(name, new FrameBlock(gpu, bytes).Binding);
    }
}
