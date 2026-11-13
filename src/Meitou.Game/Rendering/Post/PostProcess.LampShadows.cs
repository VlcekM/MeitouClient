using Meitou.Game.Rendering.Objects;
using Meitou.Game.Rendering.Frame;
using InternalFormat = Meitou.Game.Rendering.Gpu.InternalFormat;
using System.Numerics;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Gpu.Core;

namespace Meitou.Game.Rendering.Post;

/// <summary>
/// The lamps' traced shadows (docs/render-lights.md "Traced lamp shadows"): after the GI resolve, what occluded lamps gave a surface is taken back
/// off the scene. Needs the surface target, the traced scene and a storage target; without one of them nothing runs.
/// </summary>
public sealed unsafe partial class PostProcess
{
    #region Lamp shadow pass programs

    /// <summary>
    /// The occluded lamps' light off the scene (<see cref="Gi.LampShadowShaders.Apply"/>).
    /// </summary>
    sealed class LampShadowApplyPass : GiPass
    {
        /// <summary>
        /// The filtered lost light and the two totals.
        /// </summary>
        public readonly SamplerSlot Lost, Total, Total1;

        /// <summary>
        /// Compiles the apply and looks up its slots.
        /// </summary>
        public LampShadowApplyPass(GpuContext gpu) : base(gpu, Gi.LampShadowShaders.Apply, "post lamp shadows") =>
            (Lost, Total, Total1) = (P.Sampler("uLost"), P.Sampler("uTotal"), P.Sampler("uTotal1"));
    }

    /// <summary>
    /// The floor under the lamp shadows (<see cref="Gi.LampShadowShaders.Floor"/>).
    /// </summary>
    sealed class LampShadowFloorPass(GpuContext gpu) : FullscreenProgram(gpu, Gi.LampShadowShaders.Floor, "post lamp shadow floor");

    /// <summary>
    /// Debug: a target shown over the scene (<see cref="Gi.LampShadowShaders.ShowTarget"/>).
    /// </summary>
    sealed class LampShowPass : FullscreenProgram
    {
        /// <summary>
        /// The target shown.
        /// </summary>
        public readonly SamplerSlot Shown;

        /// <summary>
        /// Compiles the show pass and looks up its slot.
        /// </summary>
        public LampShowPass(GpuContext gpu) : base(gpu, Gi.LampShadowShaders.ShowTarget, "post lamp show") => Shown = P.Sampler("uShown");
    }

    #endregion

    #region Lamp shadows

    /// <summary>
    /// How many frames the sample pattern moves through under a temporal resolve; without one it holds still.
    /// </summary>
    const int LampShadowPhases = 16;

    /// <summary>
    /// The debug show pass, made when first used.
    /// </summary>
    LampShowPass? _lampShow;

    /// <summary>
    /// The trace, made when first used.
    /// </summary>
    Gi.LampShadowPass? _lampShadowTrace;

    /// <summary>
    /// The apply, made when first used.
    /// </summary>
    LampShadowApplyPass? _lampShadowApply;

    /// <summary>
    /// The floor pass, made when first used.
    /// </summary>
    LampShadowFloorPass? _lampShadowFloor;

    /// <summary>
    /// The trace's targets at the render size: the lost light, the totals, the filter's scratch and its result.
    /// </summary>
    Target2D? _lampLost, _lampTotal, _lampTotal1, _lampScratch, _lampFiltered;

    /// <summary>
    /// The last frame's per-lamp shadow areas and the pixels of their rectangles summed (0 before the pass ran).
    /// </summary>
    public (int Areas, long Pixels, long CellPixels, int Movers) LampShadowAreas =>
        _lampShadowTrace is { } t ? (t.AreasLast, t.AreaPixelsLast, t.CellPixelsLast, t.MoversLast) : default;

    /// <summary>
    /// NDC, jittered as the scene was drawn, to the position relative to the eye (set by the world frame before the scene; the penumbra filters
    /// reconstruct with it).
    /// </summary>
    internal Matrix4x4 SceneToEye { get; set; }

    /// <summary>
    /// Takes the occluded lamps' diffuse light off the scene. <paramref name="scene"/> must hold this frame's traced scene.
    /// </summary>
    internal void RunLampShadows(Gi.GiScene? scene, WorldLamps? lamps)
    {
        var (colour, depth, surface) = (_sceneColour, _sceneDepth, _giAlbedo);
        if (!Options.LampShadows || GiResolve <= 0 || surface is null || colour is null || depth is null || !_haveScene) return;
        if (scene?.Top is null || lamps is null || lamps.Grid.W <= 0 || !SupportsStorage(InternalFormat.Rgba16F))
            return;

        MarkScene();
        var lost = _lampLost ??= MakeLampLost();
        _lampTotal ??= MakeLampLost();
        var total1 = _lampTotal1 ??= MakeLampLost();
        _lampScratch ??= MakeLampLost();
        var filtered = _lampFiltered ??= MakeLampLost();
        CloseSegment();

        var trace = _lampShadowTrace ??= new Gi.LampShadowPass(Gpu);
        if (lamps.DebugReference is { } reference && lamps.RowOf(reference.Lamp) is { } debugRow)
        {
            trace.RunReference(scene, lamps, depth.Texture, surface.Texture, lost.Texture, LampShadowViewNow(), debugRow, reference.Truth);
            ShowLampShadowReference(colour, lost);
            return;
        }

        // With the tiles the rays are for what moves alone, a lamp at a time: total1 sums their lost light, filtered ends as 1.
        bool perLamp = lamps.Tiles is not null;
        if (!TraceLampShadows(trace, scene, lamps, depth, surface, perLamp)) return;

        ApplyLampShadows(colour, filtered, perLamp ? total1 : _lampTotal!, total1);
    }

    /// <summary>
    /// Releases the trace and the pass programs.
    /// </summary>
    void DisposeLampShadows()
    {
        _lampShadowTrace?.Dispose();
        _lampShadowApply?.P.Dispose();
        _lampShadowFloor?.P.Dispose();
        _lampShow?.P.Dispose();
    }

    /// <summary>
    /// One of the trace's targets at the render size.
    /// </summary>
    Target2D MakeLampLost()
    {
        using var batch = Gpu.Uploads.Begin();
        return Make(batch, _width, _height, InternalFormat.Rgba16F, TextureMinFilter.Nearest, "post lamp shadows");
    }

    /// <summary>
    /// Debug (console <c>lamps refview</c>): the reference trace's <paramref name="shown"/> drawn over the scene.
    /// </summary>
    void ShowLampShadowReference(Target2D colour, Target2D shown)
    {
        var show = _lampShow ??= new LampShowPass(Gpu);
        Bind(show.P, show.Shown, shown);
        Draw(show.P, colour.Attachment, colour.Format, _width, _height, _width, _height, PassState);
        Stamp("lamp shadows");
        CloseSegment();
    }

    /// <summary>
    /// The trace into the targets: a lamp at a time over the cells the depth pyramid allows when <paramref name="perLamp"/>, else all lamps at
    /// once. False: nothing to do.
    /// </summary>
    bool TraceLampShadows(Gi.LampShadowPass trace, Gi.GiScene scene, WorldLamps lamps, Target2D depth, Target2D surface, bool perLamp)
    {
        var (lost, total, total1) = (_lampLost!.Texture, _lampTotal!.Texture, _lampTotal1!.Texture);
        var (scratch, filtered) = (_lampScratch!.Texture, _lampFiltered!.Texture);
        if (!perLamp)
        {
            trace.Run(scene, lamps, depth.Texture, surface.Texture, lost, total, total1, scratch, filtered, LampShadowViewNow(), SceneToEye);
            return true;
        }

        // This frame's depth pyramid tells each lamp's pass which cells of the screen can hold a mover's shadow.
        var pyramid = BuildHiz() ? Hiz!.ThisFrame() : default;
        return trace.RunPerLamp(scene, lamps, depth.Texture, surface.Texture, lost, total, total1, scratch, filtered, LampShadowViewNow(),
            SceneToEye, pyramid);
    }

    /// <summary>
    /// The filtered lost light off the scene, then the floor pass.
    /// </summary>
    void ApplyLampShadows(Target2D colour, Target2D filtered, Target2D total, Target2D total1)
    {
        var a = _lampShadowApply ??= new LampShadowApplyPass(Gpu);
        SetGiView(a);
        Bind(a.P, a.Lost, filtered);
        Bind(a.P, a.Total, total);
        Bind(a.P, a.Total1, total1);
        a.P.ApplyGlobals();   // the haze's uniforms
        Draw(a.P, colour.Attachment, colour.Format, _width, _height, _width, _height, LampShadowState);

        var floor = _lampShadowFloor ??= new LampShadowFloorPass(Gpu);
        Draw(floor.P, colour.Attachment, colour.Format, _width, _height, _width, _height, LampShadowFloorState);
        Stamp("lamp shadows");
        CloseSegment();
    }

    /// <summary>
    /// The camera as the trace reads it: its axes, eye, depth planes, frustum tangents, render size, a pixel's width per unit of depth and the
    /// sample pattern's phase.
    /// </summary>
    Gi.LampShadowView LampShadowViewNow()
    {
        float tanY = MathF.Tan(_fovNow * 0.5f);
        var r = _viewRotation;
        var right = new Vector3(r.M11, r.M21, r.M31);
        var up = new Vector3(r.M12, r.M22, r.M32);
        var phase = Temporal ? _frameIndex % LampShadowPhases : 0;

        return new Gi.LampShadowView(right, up, ViewWindow.BackFor(r, _fovNow, _aspectNow), _eyeNow, _planes, ViewWindow.Tan(tanY * _aspectNow, tanY),
            new Vector2(_width, _height), 2 * tanY / _height, phase);
    }

    #endregion
}
