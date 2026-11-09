using Meitou.Game.Rendering.Objects;
using Meitou.Game.Rendering.Frame;
using InternalFormat = Meitou.Game.Rendering.Gpu.InternalFormat;
using System.Numerics;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Gpu.Core;

namespace Meitou.Game.Rendering.Post;

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

    sealed class LampShowPass : FullscreenProgram
    {
        public readonly SamplerSlot Shown;
        public LampShowPass(GpuContext gpu) : base(gpu, Gi.LampShadowShaders.ShowTarget, "post lamp show") => Shown = P.Sampler("uShown");
    }

    LampShowPass? _lampShow;

    Gi.LampShadowPass? _lampShadowTrace;
    LampShadowApplyPass? _lampShadowApply;
    LampShadowFloorPass? _lampShadowFloor;
    Target2D? _lampLost, _lampTotal, _lampTotal1, _lampScratch, _lampFiltered;

    /// <summary>NDC, jittered as the scene was drawn, to the position relative to the eye (set by the world frame before the scene; the penumbra filters reconstruct with it).</summary>
    internal Matrix4x4 SceneToEye { get; set; }

    /// <summary>Takes the occluded lamps' diffuse light off the scene. <paramref name="scene"/> must hold this frame's traced scene.</summary>
    internal void RunLampShadows(Gi.GiScene? scene, WorldLamps? lamps)
    {
        var (colour, depth, surface) = (_sceneColour, _sceneDepth, _giAlbedo);
        if (!Options.LampShadows || GiResolve <= 0 || surface is null || colour is null || depth is null || !_haveScene) return;
        if (scene?.Top is null || lamps is null || lamps.Grid.W <= 0 || !SupportsStorage(InternalFormat.Rgba16F)) return;

        MarkScene();
        var lost = _lampLost ??= MakeLampLost();
        var total = _lampTotal ??= MakeLampLost();
        var total1 = _lampTotal1 ??= MakeLampLost();
        var scratch = _lampScratch ??= MakeLampLost();
        var filtered = _lampFiltered ??= MakeLampLost();
        CloseSegment();
        var trace = _lampShadowTrace ??= new Gi.LampShadowPass(Gpu, Options.LampShadowLamps);
        if (lamps.DebugReference is { } reference && lamps.RowOf(reference.Lamp) is { } debugRow)
        {
            trace.RunReference(scene, lamps, depth.Texture, surface.Texture, lost.Texture, LampShadowViewNow(), debugRow, reference.Truth);
            var show = _lampShow ??= new LampShowPass(Gpu);
            Bind(show.P, show.Shown, lost);
            Draw(show.P, colour.Attachment, colour.Format, _width, _height, _width, _height, PassState);
            Stamp("lamp shadows");
            CloseSegment();
            return;
        }
        // With the tiles the rays are for what moves alone, a lamp at a time: total1 sums their lost light, filtered ends as 1.
        bool perLamp = lamps.Tiles is not null;
        if (perLamp)
        {
            if (!trace.RunPerLamp(scene, lamps, depth.Texture, surface.Texture, lost.Texture, total.Texture, total1.Texture, scratch.Texture, filtered.Texture, LampShadowViewNow(), SceneToEye)) return;
        }
        else trace.Run(scene, lamps, depth.Texture, surface.Texture, lost.Texture, total.Texture, total1.Texture, scratch.Texture, filtered.Texture, LampShadowViewNow(), SceneToEye);

        var a = _lampShadowApply ??= new LampShadowApplyPass(Gpu);
        SetGiView(a);
        Bind(a.P, a.Lost, filtered);
        Bind(a.P, a.Total, perLamp ? total1 : total);
        Bind(a.P, a.Total1, total1);
        a.P.ApplyGlobals();   // the haze's uniforms
        Draw(a.P, colour.Attachment, colour.Format, _width, _height, _width, _height, LampShadowState);
        var floor = _lampShadowFloor ??= new LampShadowFloorPass(Gpu);
        Draw(floor.P, colour.Attachment, colour.Format, _width, _height, _width, _height, LampShadowFloorState);
        Stamp("lamp shadows");
        CloseSegment();
    }

    Target2D MakeLampLost()
    {
        using var batch = Gpu.Uploads.Begin();
        return Make(batch, _width, _height, InternalFormat.Rgba16F, TextureMinFilter.Nearest, "post lamp shadows");
    }

    void DisposeLampShadows()
    {
        _lampShadowTrace?.Dispose();
        _lampShadowApply?.P.Dispose();
        _lampShadowFloor?.P.Dispose();
        _lampShow?.P.Dispose();
    }

    Gi.LampShadowView LampShadowViewNow()
    {
        float tanY = MathF.Tan(_fovNow * 0.5f);
        var r = _viewRotation;
        return new Gi.LampShadowView(new Vector3(r.M11, r.M21, r.M31), new Vector3(r.M12, r.M22, r.M32), ViewWindow.BackFor(r, _fovNow, _aspectNow), _eyeNow,
            _planes, ViewWindow.Tan(tanY * _aspectNow, tanY), new Vector2(_width, _height), 2 * tanY / _height, Temporal ? _frameIndex % LampShadowPhases : 0);
    }
}
