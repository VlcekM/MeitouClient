using System.Numerics;
using Meitou.Data.World;

namespace Meitou.Game.Rendering.Sky;

/// <summary>
/// The cloud layer's values as the sky pass reads them (<c>uCloudLight</c>, <c>uCloudSun</c>, <c>uCloudWind</c>, <c>uCloudMeitou</c>, <c>uCloudKey</c>,
/// <c>uCloudKeyDir</c>, <c>uCloudAmbient</c>; <see cref="CloudLayerShaders"/>), and as the frame globals give them to a puddle's reflection
/// (<c>uAtmoLayer*</c>), so the two draw the same layer.
/// </summary>
readonly record struct CloudPassValues(Vector4 Light, Vector4 Sun, Vector4 Wind, Vector4 Meitou, Vector4 Key, Vector4 KeyDir, Vector4 Ambient)
{
    /// <summary>
    /// The cloud pass's light and the wind's texture <paramref name="shift"/> from <paramref name="s"/> and the <paramref name="coverage"/>, and the
    /// Meitou clouds' (<paramref name="lit"/>; <paramref name="volume"/>: a prepared volume image) key, ambient and parallax round <paramref
    /// name="eye"/>. <paramref name="pixelAngle"/>: the angle a pixel of the main view spans (radians), in <see cref="Wind"/>'s z, which the sky pass
    /// does not read.
    /// </summary>
    public static CloudPassValues Of(in SkyRenderer.State s, float coverage, Vector2 shift, bool lit, bool volume, Vector3 eye, float pixelAngle)
    {
        // The cloud pass: zenithLight and Darkness, sunColour.rgb and DensityOffset, the wind offset's texture shift.
        var zenith = s.CloudZenith;
        var light = new Vector4(zenith, CloudLayer.Darkness(coverage));
        var sun = new Vector4(s.CloudSun.X, s.CloudSun.Y, s.CloudSun.Z, CloudLayer.DensityOffset(coverage));

        // The Meitou clouds are anchored to the world (the eye's parallax, MeitouClouds.Parallax), so the cloud overhead is the one whose shadow
        // falls here; the game's follow the eye.
        var (parallaxScale, parallaxOffset) = lit ? MeitouClouds.Parallax(eye.X, eye.Y, eye.Z) : (1f, Vector2.Zero);
        var meitou = new Vector4(lit ? (volume ? 2 : 1) : 0, parallaxScale, parallaxOffset.X, parallaxOffset.Y);

        var ambient = new Vector4(s.CloudAmbient, MeitouClouds.AmbientGreyAt(coverage));
        return new CloudPassValues(light, sun, new Vector4(shift.X, shift.Y, pixelAngle, 0), meitou, new Vector4(s.CloudKey, 0),
            new Vector4(s.CloudKeyDirection, 0), ambient);
    }
}
