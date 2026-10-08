using System.Numerics;

namespace Meitou.Data.World;

/// <summary>
/// SkyX's planar cloud layer as Kenshi runs it (docs/formats/clouds.md): the layer's options, the numbers the weather feeds it
/// (<c>DensityOffset</c>, <c>Darkness</c>), the light the shader uses and the wind offset. The per-pixel pass is in <c>SkyRenderer</c>'s
/// shader; these are the parts that don't need a GPU, so a test can pin them. Verified: <c>SkyX_Clouds.hlsl</c>, <c>CloudLayer::CloudLayer</c>
/// and the sky controller (<c>kenshi_x64.exe</c> FUN_14066e380 / FUN_14066f190).
/// </summary>
public static class CloudLayer
{
    /// <summary>Plane height (<c>uHeight</c>) and texture scale (<c>uScale</c>): the cloud point is <c>d · 100 / d.y</c>, the texture coordinate its xz × 0.001.</summary>
    public const float Height = 100, Scale = 0.001f;
    /// <summary><c>uDistanceAttenuation</c>: the horizon band fades in over 0.1 of <c>d.y</c> above this (<c>h = saturate(10 · saturate(d.y − 0.05))</c>).</summary>
    public const float DistanceAttenuation = 0.05f;
    /// <summary><c>uCloudLayerHeightVolume</c> and <c>uCloudLayerVolumetricDisplacement</c>: the fake volume of the second lookup.</summary>
    public const float HeightVolume = 0.25f, VolumetricDisplacement = 0.01f;
    /// <summary><c>uDensityMultiplier</c>.</summary>
    public const float DensityMultiplier = 3;
    /// <summary>The wind offset's texture scale (the shader's <c>uWindDirection × 0.00005</c>).</summary>
    public const float WindScale = 0.00005f;
    /// <summary>The second lookup of <c>Clouds.dds</c> is shifted by this in texture space; the tile texture adds <c>0.1 ×</c> its red to the density.</summary>
    public static readonly Vector2 SecondLookupShift = new(0.2f, 0.6f);
    public const float TileWeight = 0.1f;
    /// <summary>The floor of <c>getColorAt(+Z) · skyMult</c> in <c>zenithLight</c>, the sky controller's offset 0xb8, set when the sky is created.</summary>
    public static readonly Vector3 ZenithFloor = new(0.001f, 0.001f, 0.0015f);

    /// <summary><c>DensityOffset = 1.4 c − 0.8</c>, with c (the weather's cloud density) clamped to 0..1 as the sky controller does.</summary>
    public static float DensityOffset(float c) => 1.4f * Math.Clamp(c, 0, 1) - 0.8f;

    /// <summary><c>Darkness = pow(clamp(c − 0.5, 0, 1), 0.3)</c>.</summary>
    public static float Darkness(float c) => MathF.Pow(Math.Clamp(Math.Clamp(c, 0, 1) - 0.5f, 0, 1), 0.3f);

    /// <summary>The alpha near the horizon (and below it), <c>saturate(DensityOffset + 0.5)</c>; also <c>horizonClouds.a</c>.</summary>
    public static float HorizonAlpha(float c) => Math.Clamp(DensityOffset(c) + 0.5f, 0, 1);

    /// <summary>The horizon band's weight <c>h</c> at the dome direction's height <paramref name="y"/>: 0 up to 0.05, 1 from 0.15.</summary>
    public static float HorizonBand(float y) => Math.Clamp(10 * Math.Clamp(y - DistanceAttenuation, 0, 1), 0, 1);

    /// <summary>
    /// <c>zenithLight = max(getColorAt(+Z) · skyMult, (0.001, 0.001, 0.0015)) · sunColour.g</c> (FUN_14066f190: the direction is Ogre's
    /// <c>UNIT_Z</c>). <paramref name="sun"/>: unit direction towards the sun; <paramref name="skyMultiplier"/>: the weather's (faded) sky colour multiplier.
    /// </summary>
    public static Vector3 ZenithLight(Vector3 sun, Vector3 skyMultiplier) =>
        Vector3.Max(SkyXModel.Colour(Vector3.UnitZ, sun, skydome: false) * skyMultiplier, ZenithFloor) * KenshiLighting.SunColour(sun).Y;

    /// <summary>
    /// <c>horizonClouds.rgb</c>, the colour the distance haze is pulled to: <c>saturate(sun (1 − 0.1 (offset + 0.2) · 3) + zenithLight) ·
    /// (1 − darkness) · √exposure</c>, the same terms as the cloud pass (docs/formats/sky.md).
    /// </summary>
    public static Vector3 HorizonColour(Vector3 sunColour, Vector3 zenithLight, float c) =>
        Vector3.Clamp(sunColour * (1 - 0.1f * (DensityOffset(c) + 0.2f) * DensityMultiplier) + zenithLight, Vector3.Zero, Vector3.One)
        * ((1 - Darkness(c)) * MathF.Sqrt(SkyAtmosphere.Exposure));

    /// <summary>
    /// The pixel's alpha straight overhead (<c>h = 1</c>) with the second lookup, the tile and no volume displacement, from the red of
    /// <c>Clouds.dds</c> at the shifted coordinate (<paramref name="cloud"/>) and of <c>CloudsTile.dds</c> (<paramref name="tile"/>), both 0..1:
    /// <c>D = (cloud + o) · 3 + 0.1 tile + 1</c>, <c>alpha = saturate(D · saturate(1 − tile + o))</c>.
    /// </summary>
    public static float OverheadAlpha(float cloud, float tile, float c)
    {
        float o = DensityOffset(c);
        float density = (cloud + o) * DensityMultiplier + tile * TileWeight + 1;
        return Math.Clamp(density * Math.Clamp(1 - tile + o, 0, 1), 0, 1);
    }

    /// <summary>
    /// Coverage of the overhead alpha over a whole square texture pair (<paramref name="size"/>², red channels as 0..255 bytes, row-major):
    /// the mean alpha and the shares of texels above 0.05 and 0.5. The second lookup reads <c>Clouds</c> shifted by (0.2, 0.6) of the texture.
    /// </summary>
    public static (double Mean, double Above005, double Above05) Coverage(ReadOnlySpan<byte> cloudRed, ReadOnlySpan<byte> tileRed, int size, float c)
    {
        int shiftX = (int)MathF.Round(SecondLookupShift.X * size), shiftY = (int)MathF.Round(SecondLookupShift.Y * size);
        double sum = 0; int above005 = 0, above05 = 0;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float cloud = cloudRed[((y + shiftY) % size) * size + (x + shiftX) % size] / 255f;
                float a = OverheadAlpha(cloud, tileRed[y * size + x] / 255f, c);
                sum += a;
                if (a > 0.05f) above005++;
                if (a > 0.5f) above05++;
            }
        double n = (double)size * size;
        return (sum / n, above005 / n, above05 / n);
    }

    /// <summary>
    /// One frame of the drift: the sky controller adds <c>velocity × dt</c> (world units; dt in seconds, 0 when paused) to an offset, the
    /// shader shifts the textures by <c>offset × 0.00005</c>. The offset is kept in doubles (it grows without bound). Returns the new offset.
    /// </summary>
    public static (double X, double Z) Advance((double X, double Z) offset, Vector2 velocity, float dt) =>
        (offset.X + (double)velocity.X * dt, offset.Z + (double)velocity.Y * dt);

    /// <summary>The offset's texture shift, wrapped to 0..1 (the textures repeat with period 1, so the shader gets the same picture with float precision kept).</summary>
    public static Vector2 TextureShift((double X, double Z) offset)
    {
        static float Wrap(double v) { double w = v - Math.Floor(v); return (float)w; }
        return new Vector2(Wrap(offset.X * WindScale), Wrap(offset.Z * WindScale));
    }
}
