namespace Meitou.Rendering;

/// <summary>
/// How far from the eye a swaying blade's own motion is worth drawing for the upscalers (<c>grass-velocity</c>, docs/render-foliage.md "Grass motion vectors"). A blade's top
/// edge moves by <c>sway * (sin(phase) - sin(previous phase))</c> world units a frame, at most <c>sway * |phase step|</c>; at a distance <c>d</c> that is that many times
/// <c>focal / d</c> pixels (<c>focal</c> = pixels per unit at distance 1, the render height over twice the tangent of half the field of view). Beyond the distance where the bound
/// falls under <c>threshold</c> pixels the camera reprojection of the depth buffer is the motion within what the upscaler can tell apart, so those blades are not redrawn.
/// </summary>
public static class GrassMotionReach
{
    /// <summary>Pixels of own motion a frame under which a blade is left to the camera's reprojection.</summary>
    public const float DefaultThresholdPixels = 0.5f;

    /// <summary>The smallest turn from one sway phase to the other, in radians (the phase wraps at 2 pi).</summary>
    public static float PhaseStep(float phase, float previousPhase)
    {
        float d = MathF.Abs(phase - previousPhase) % (2 * MathF.PI);
        return MathF.Min(d, 2 * MathF.PI - d);
    }

    /// <summary>
    /// The factor a patch's sway length is multiplied by to get its reach in world units (the cull's <c>motionScale</c>); 0 when nothing moves (no step) or the focal length is unknown,
    /// the caller then draws nothing or everything respectively.
    /// </summary>
    public static float Scale(float phaseStep, float focalPixels, float thresholdPixels) =>
        phaseStep > 0 && focalPixels > 0 && thresholdPixels > 0 ? phaseStep * focalPixels / thresholdPixels : 0;

    /// <summary>The reach of a patch (the CPU path's <c>min(range, sway * scale)</c>, the same single-precision operations as the cull kernel).</summary>
    public static float Reach(float range, float sway, float scale) => scale > 0 ? MathF.Min(range, sway * scale) : range;
}
