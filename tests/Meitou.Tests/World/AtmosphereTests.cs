using System.Numerics;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Textures;
using Meitou.Data.World;

namespace Meitou.Tests.World;

public class AtmosphereTests
{
    static readonly SkyClock Clock = new(54, 5, 23);

    [Fact]
    public void Sun_colour_and_daylight_scale_follow_the_sky_controller()
    {
        // kenshi_x64.exe sky update: sunColour.rgb = getColorAt(sun + (0, 0, 0.04)) / (4 · 1.4), black under y = -0.2.
        var noon = Clock.SunDirection(14);
        var expected = SkyXModel.Colour(noon + new Vector3(0, 0, 0.04f), noon, skydome: false) / 5.6f;
        Assert.Equal(expected, KenshiLighting.SunColour(noon));
        var low = KenshiLighting.SunColour(Clock.SunDirection(22.3f));
        var high = KenshiLighting.SunColour(noon);
        Assert.True(low.Z / low.X < high.Z / high.X, "the low sun is redder");
        Assert.Equal(Vector3.Zero, KenshiLighting.SunColour(new Vector3(0.97f, -0.25f, 0)));
        // sunColour.w: 0.5 at the horizon, 1 at the zenith, gone 0.093 below.
        Assert.Equal(0.5f, KenshiLighting.Daylight(0), 5);
        Assert.Equal(1f, KenshiLighting.Daylight(1), 5);
        Assert.Equal(0.75f, KenshiLighting.Daylight(0.5f), 5);
        Assert.Equal(0, KenshiLighting.Daylight(-0.0926f), 2);
        Assert.Equal(0f, KenshiLighting.Daylight(-0.2f));
        // The lighting direction lies on the horizon at night; the environment light bottoms out at 0.2.
        var night = KenshiLighting.LightDirection(Clock.SunDirection(1));
        Assert.Equal(0, night.Y);
        Assert.Equal(1, night.Length(), 5);
        Assert.Equal(0.2f, KenshiLighting.EnvironmentFactor(night), 5);
        Assert.Equal(1f, KenshiLighting.EnvironmentFactor(Vector3.UnitY));
    }

    [Fact]
    public void Kenshi_haze_colour_grows_with_distance_to_the_skys_at_the_dome_radius()
    {
        var sun = Clock.SunDirection(13);
        var dir = Vector3.Normalize(new Vector3(-sun.X, 0, -sun.Z));
        var near = KenshiHaze.Colour(dir * 5000, sun);
        var mid = KenshiHaze.Colour(dir * 30000, sun);
        var dome = KenshiHaze.Colour(dir * KenshiHaze.DomeRadius, sun);
        var beyond = KenshiHaze.Colour(dir * 300000, sun);
        Assert.True(near.Y < mid.Y && mid.Y < dome.Y, "more in-scattered light the longer the path");
        Assert.True((dome - beyond).Length() < 0.02f * dome.Length(),
                $"{dome} {beyond}");   // the path is capped at one dome radius (the direction still shifts a little)
        // At the dome radius the haze is the sky's Rayleigh light just above the horizon (same integral, same eye).
        var (ray, _, _) = KenshiHaze.Ray(dir * KenshiHaze.DomeRadius);
        var sky = SkyXModel.InScatter(ray, 1,
                sun) * SkyAtmosphere.InverseWaveLength4 * (SkyAtmosphere.KrESun * SkyXModel.RayleighPhase(Vector3.Dot(sun,
                        ray)) * SkyAtmosphere.Exposure);
        Assert.Equal(sky.Y, dome.Y, 3);
        // Night: the integral vanishes, the haze is black.
        var night = KenshiHaze.Colour(dir * 30000, Clock.SunDirection(1));
        Assert.True(night.Length() < 1e-3f);
    }

    [Fact]
    public void Kenshi_haze_stays_bounded_from_far_above_the_games_camera_heights()
    {
        // Within the game's camera heights (at most 1840 above the pivot) the haze colour stays near the sky's own (blue at most ~1.3).
        // From far higher up, long steep rays drive the fit's optical depth strongly negative; without the viewer's floor the colour
        // reached thousands (the white arc of an overview shot). With it the colour stays finite and modest.
        float worstGame = 0, worstHigh = 0;
        foreach (float hour in new[] { 6f, 9, 13, 18, 22.6f })
        {
            var sun = Clock.SunDirection(hour);
            for (float d = 1000; d < 450000; d *= 1.3f)
            {
                worstGame = MathF.Max(worstGame, KenshiHaze.Colour(new Vector3(0, -KenshiCamera.MaxHeightAbovePivot, d), sun).Z);
                var high = KenshiHaze.Colour(new Vector3(0, -50000, d), sun);
                Assert.True(float.IsFinite(high.X) && float.IsFinite(high.Y) && float.IsFinite(high.Z));
                worstHigh = MathF.Max(worstHigh, high.Z);
            }
        }
        Assert.InRange(worstGame, 0.5f, 1.4f);
        Assert.InRange(worstHigh, 0, 40);
        Assert.Equal(1840, KenshiCamera.MaxHeightAbovePivot, 1);
    }

    [Fact]
    [Slow]
    public void Ambient_map_built_from_the_biomes_matches_the_shipped_one()
    {
        var install = GameInstall.Locate();
        Assert.SkipWhen(install is null, $"No Kenshi install configured ({GameInstall.EnvironmentVariable}).");
        var db = GameDatabase.Load(LoadOrder.FromInstall(install!));
        var shipped = TextureLoader.LoadImage(File.ReadAllBytes(Path.Combine(install!.DataDirectory, "newland/land/overlaymaps/ambientmap.png")));
        var biomes = TextureLoader.LoadImage(File.ReadAllBytes(Path.Combine(install.DataDirectory, TerrainMaps.BiomeMap)));
        var built = AmbientMap.Build(db, biomes);
        Assert.Equal(shipped.Width, built.Width);
        int differ = 0;
        for (int i = 0; i < built.Pixels.Length; i += 4)
            if (!built.Pixels.AsSpan(i, 4).SequenceEqual(shipped.Pixels.AsSpan(i, 4))) differ++;
        Assert.Equal(0, differ);
        // Sun brightness 1 -> alpha 127, 0.38 (Ashlands) -> 48.
        Assert.Contains(built.Pixels.Where((_, i) => i % 4 == 3), a => a == 127);
        Assert.Contains(built.Pixels.Where((_, i) => i % 4 == 3), a => a == 48);
    }
}
