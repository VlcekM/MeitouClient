using System.Numerics;
using Meitou.Rendering;

namespace Meitou.Tests.World;

/// <summary>
/// The volumetric clouds' fog early-out (docs/render-clouds.md "Fog early-out"): the march leaves out a half-size texel only when the fog makes the
/// clouds there invisible, so the picture cannot change.
/// </summary>
public class CloudFogSkipTests
{
    /// <summary>The view direction of a point in NDC for a camera looking along +x, pitched up, vertical field of view 50 degrees.</summary>
    static Vector3 Ray(Vector2 ndc, float pitch, float aspect)
    {
        float tanY = MathF.Tan(25 * MathF.PI / 180), tanX = tanY * aspect;
        var forward = new Vector3(MathF.Cos(pitch), MathF.Sin(pitch), 0);
        var right = new Vector3(0, 0, 1);
        var up = Vector3.Cross(right, forward);
        return Vector3.Normalize(forward + right * (ndc.X * tanX) + up * (ndc.Y * tanY));
    }

    /// <summary>What the fog volume pass leaves of the sky behind one block (the eye inside it), at the far clip as for a pixel with no geometry.</summary>
    static float Transmittance(Vector3 eye, Vector3 d) => 1f - FogCullTests.Alpha(FogCullTests.Block(), eye, d, 50000);

    /// <summary>VolumetricCloudShaders.March's fogHidesSky: the texel's own ray and the four at the corners of the reach, all with nothing left.</summary>
    static bool Hidden(Vector3 eye, Vector2 ndc, Vector2 reach, float pitch, float aspect)
    {
        Span<Vector2> offsets = [Vector2.Zero, new(1, -1), new(-1, -1), new(1, 1), new(-1, 1)];
        foreach (var o in offsets)
            if (Transmittance(eye, Ray(ndc + o * reach, pitch, aspect)) > 0) return false;
        return true;
    }

    [Theory]
    [InlineData(1121f, 3f, false)]   // the bench's eye height, looking just above the horizon: up to 28 degrees every path is opaque
    [InlineData(1121f, 20f, true)]   // looking up: the top of the picture sees out through the ceiling
    [InlineData(2600f, 8f, true)]    // near the ceiling: short paths, the sky shows
    public void A_texel_is_left_out_only_where_every_pixel_that_reads_it_sees_no_sky(float height, float pitchDegrees, bool skyShows)
    {
        const int width = 960, height2 = 540;   // the half-size target of a 1920 x 1080 render
        float aspect = width / (float)height2, pitch = pitchDegrees * MathF.PI / 180;
        var eye = new Vector3(0, height, 0);
        var reach = new Vector2(VolumetricCloudRenderer.FogSkipReach * 2f / width, VolumetricCloudRenderer.FogSkipReach * 2f / height2);
        // What a full-size pixel can read: the composite's bilinear lookup reaches one texel, the projection's jitter (against the fog pass's
        // unjittered pixel centre) a quarter texel more.
        var reads = reach * (1.25f / VolumetricCloudRenderer.FogSkipReach);
        int hidden = 0, marched = 0;
        for (int y = 0; y < height2; y += 3)
            for (int x = 0; x < width; x += 41)
            {
                var ndc = new Vector2((x + 0.5f) / width * 2 - 1, (y + 0.5f) / height2 * 2 - 1);
                if (Ray(ndc, pitch, aspect).Y <= 0) continue;   // the march's slab is above the eye: no work below the horizon
                if (!Hidden(eye, ndc, reach, pitch, aspect)) { marched++; continue; }
                hidden++;
                for (int j = 0; j <= 8; j++)
                    for (int i = 0; i <= 8; i++)
                    {
                        var p = ndc + new Vector2(i / 4f - 1, j / 4f - 1) * reads;
                        Assert.Equal(0f, Transmittance(eye, Ray(p, pitch, aspect)));
                    }
            }
        Assert.True(hidden > 0, "the long low paths inside the block are left out");
        Assert.Equal(skyShows, marched > 0);
    }

    [Fact]
    public void The_block_hides_the_horizon_exactly_and_not_the_zenith()
    {
        var eye = new Vector3(0, 1121, 0);
        Assert.Equal(0f, Transmittance(eye, Vector3.Normalize(new Vector3(1, 0.02f, 0))));   // the curve clamps: exactly nothing left
        Assert.InRange(Transmittance(eye, Vector3.UnitY), 0.05f, 0.15f);                    // 1879 units of fog straight up leave about a tenth
    }

    /// <summary>The sky shader's weather term over a pixel with no geometry: ease-in-out of the far clip over the fog distance, times the weight.</summary>
    static float SkyFog(SkyRenderer.AtmosphereUniforms u)
    {
        if (!(u.Fog.Z > 0) || !(u.Haze.W > 0)) return 0;
        float a = Math.Clamp(u.Fog.W * u.Haze.W, 0, 1);
        return (a < 0.5f ? 2 * a * a : 1 - 2 * (a - 1) * (a - 1)) * u.Fog.Z;
    }

    [Fact]
    public void The_whole_march_is_left_out_only_when_the_weather_fog_covers_the_sky()
    {
        Assert.True(SkyRenderer.WeatherFogHidesSky(FogCullTests.Atmosphere()));                         // fog complete at 3000, far clip 50000
        Assert.True(SkyRenderer.WeatherFogHidesSky(FogCullTests.Atmosphere(fogEnd: 25000)));           // a dust storm
        Assert.False(SkyRenderer.WeatherFogHidesSky(FogCullTests.Atmosphere(weight: 0)));               // fog off (the swamp's weathers)
        Assert.False(SkyRenderer.WeatherFogHidesSky(FogCullTests.Atmosphere(weight: 0.999f)));         // a fog still blending in
        Assert.False(SkyRenderer.WeatherFogHidesSky(FogCullTests.Atmosphere(fogEnd: 190000)));         // the far clip is part of the way in
        foreach (float weight in new[] { 0.5f, 0.9997f, 0.9998f, 1f })
            foreach (float fogEnd in new[] { 1000f, 20000f, 49000f, 50000f, 51000f, 60000f, 120000f })
            {
                var u = FogCullTests.Atmosphere(weight: weight, fogEnd: fogEnd);
                Assert.Equal(SkyFog(u) >= 0.9998f, SkyRenderer.WeatherFogHidesSky(u));
            }
    }
}
