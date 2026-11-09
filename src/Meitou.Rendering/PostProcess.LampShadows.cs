using InternalFormat = Meitou.Rendering.Gpu.InternalFormat;
using System.Numerics;
using Meitou.Rendering.Gpu;
using Meitou.Rendering.Gpu.Core;

namespace Meitou.Rendering;

/// <summary>
/// The lamps' traced shadows (docs/render-lights.md "Traced lamp shadows"): after the GI resolve, what occluded lamps gave a surface is taken back off the scene.
/// Needs the surface target, the traced scene and a storage target; without one of them nothing runs.
/// </summary>
public sealed unsafe partial class PostProcess
{
    /// <summary>How many frames the sample pattern moves through under a temporal resolve; without one it holds still.</summary>
    const int LampShadowPhases = 16;

    sealed class LampShadowApplyPass : GiPass
    {
        public readonly SamplerSlot Lost, Total, Total1;
        public LampShadowApplyPass(GpuContext gpu) : base(gpu, Gi.LampShadowShaders.Apply, "post lamp shadows") => (Lost, Total, Total1) = (P.Sampler("uLost"), P.Sampler("uTotal"), P.Sampler("uTotal1"));
    }

    sealed class LampShadowFloorPass(GpuContext gpu) : FullscreenProgram(gpu, Gi.LampShadowShaders.Floor, "post lamp shadow floor");

    Gi.LampShadowPass? lampShadowTrace;
    LampShadowApplyPass? lampShadowApply;
    LampShadowFloorPass? lampShadowFloor;
    Target2D? lampLost, lampTotal, lampTotal1, lampScratch, lampFiltered;

    /// <summary>NDC, jittered as the scene was drawn, to the position relative to the eye (set by the world frame before the scene; the penumbra filters reconstruct with it).</summary>
    internal Matrix4x4 SceneToEye { get; set; }

    /// <summary>Takes the occluded lamps' diffuse light off the scene. <paramref name="scene"/> must hold this frame's traced scene.</summary>
    internal void RunLampShadows(Gi.GiScene? scene, WorldLamps? lamps)
    {
        var (colour, depth, surface) = (sceneColour, sceneDepth, giAlbedo);
        if (!Options.LampShadows || GiResolve <= 0 || surface is null || colour is null || depth is null || !haveScene) return;
        if (scene?.Top is null || lamps is null || lamps.Grid.W <= 0 || !SupportsStorage(InternalFormat.Rgba16f)) return;

        MarkScene();
        var lost = lampLost ??= MakeLampLost();
        var total = lampTotal ??= MakeLampLost();
        var total1 = lampTotal1 ??= MakeLampLost();
        var scratch = lampScratch ??= MakeLampLost();
        var filtered = lampFiltered ??= MakeLampLost();
        CloseSegment();
        var trace = lampShadowTrace ??= new Gi.LampShadowPass(Gpu, Options.LampShadowLamps);
        // With the tiles the rays are for what moves alone, a lamp at a time: total1 sums their lost light, filtered ends as 1.
        bool perLamp = lamps.Tiles is not null;
        if (perLamp)
        {
            if (!trace.RunPerLamp(scene, lamps, depth.Texture, surface.Texture, lost.Texture, total.Texture, total1.Texture, scratch.Texture, filtered.Texture, LampShadowViewNow(), SceneToEye)) return;
        }
        else trace.Run(scene, lamps, depth.Texture, surface.Texture, lost.Texture, total.Texture, total1.Texture, scratch.Texture, filtered.Texture, LampShadowViewNow(), SceneToEye);

        var a = lampShadowApply ??= new LampShadowApplyPass(Gpu);
        SetGiView(a);
        Bind(a.P, a.Lost, filtered);
        Bind(a.P, a.Total, perLamp ? total1 : total);
        Bind(a.P, a.Total1, total1);
        a.P.ApplyGlobals();   // the haze's uniforms
        Draw(a.P, colour.Attachment, colour.Format, width, height, width, height, LampShadowState);
        var floor = lampShadowFloor ??= new LampShadowFloorPass(Gpu);
        Draw(floor.P, colour.Attachment, colour.Format, width, height, width, height, LampShadowFloorState);
        Stamp("lamp shadows");
        CloseSegment();
    }

    Target2D MakeLampLost()
    {
        using var batch = Gpu.Uploads.Begin();
        return Make(batch, width, height, InternalFormat.Rgba16f, TextureMinFilter.Nearest, "post lamp shadows");
    }

    void DisposeLampShadows()
    {
        lampShadowTrace?.Dispose();
        lampShadowApply?.P.Dispose();
        lampShadowFloor?.P.Dispose();
    }

    Gi.LampShadowView LampShadowViewNow()
    {
        float tanY = MathF.Tan(fovNow * 0.5f);
        var r = viewRotation;
        return new Gi.LampShadowView(new Vector3(r.M11, r.M21, r.M31), new Vector3(r.M12, r.M22, r.M32), ViewWindow.BackFor(r, fovNow, aspectNow), eyeNow,
            planes, ViewWindow.Tan(tanY * aspectNow, tanY), new Vector2(width, height), 2 * tanY / height, Temporal ? frameIndex % LampShadowPhases : 0);
    }
}
