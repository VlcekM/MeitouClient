using System.Numerics;

namespace Meitou.Rendering;

/// <summary>
/// The sun's own motion in the upscalers' motion vectors (docs/render-post.md "Sun motion"). The sky's motion is the camera's alone, so while the clock runs
/// and the camera stands still the upscalers read the sun disc and its glow as unmoving and blend last frame's sun into this frame's (DLSS left rows of streaks
/// beside it). With <see cref="SunMotion"/> the sky pixels round the sun also get the sun's motion across the screen since the last frame.
/// </summary>
public sealed partial class PostProcess
{
    /// <summary>Meitou: the sky round the sun moves with the sun in the motion vectors (default on; <c>--ab sun-motion</c>).</summary>
    public bool SunMotion { get; set; } = true;

    readonly SunMotionTracker sunTracker = new();

    /// <summary>This frame's: xyz the direction to the sun, w cos of the inner angle; and xy the motion to add (the motion target's units), z cos of the
    /// outer angle, w 1 when on (<see cref="SunMotionTracker"/>).</summary>
    (Vector4 Sun, Vector4 Motion) sunMotion;

    /// <summary>The sun's motion for this frame's velocity pass, from the frame globals' sun direction and the last frame's.</summary>
    void PrepareSunMotion() =>
        sunMotion = sunTracker.Prepare(Gpu.Globals, SunMotion, previousValid, viewRotation, fovNow, aspectNow);
}
