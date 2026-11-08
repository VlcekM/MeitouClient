using System.Numerics;

namespace Meitou.Rendering;

/// <summary>
/// How loading measures distance from a free camera high above the land (engine choice, docs/renderer-streaming.md "Eye height"): the eye's height over
/// the ground beyond the game's highest camera (<see cref="Meitou.Data.World.KenshiCamera.MaxHeightAbovePivot"/>) is added as a third side to a
/// distance across the ground, so the game's own camera heights load as before and a camera flying high loads only what can be seen from there.
/// Drawing still measures as it did.
/// </summary>
static class EyeLift
{
    /// <summary>The eye's height over <paramref name="ground"/> beyond the game's camera heights; 0 with the ground unknown (not finite).</summary>
    public static float Of(Vector3 eye, float ground) =>
        float.IsFinite(ground) ? Math.Max(0, eye.Y - ground - Meitou.Data.World.KenshiCamera.MaxHeightAbovePivot) : 0;

    /// <summary><paramref name="groundDistance"/> with <paramref name="lift"/> added as a third side.</summary>
    public static float Apply(float groundDistance, float lift) => lift == 0 ? groundDistance : MathF.Sqrt(groundDistance * groundDistance + lift * lift);
}
