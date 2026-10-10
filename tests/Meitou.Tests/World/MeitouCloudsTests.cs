using System.Numerics;
using Meitou.Data.World;

namespace Meitou.Tests.World;

/// <summary>The Meitou clouds (docs/formats/clouds.md "Meitou clouds"): the game's cloud layer lit by a key light through its depth, with a phase and the sky's ambient.</summary>
public class MeitouCloudsTests
{
    [Fact]
    public void The_phase_averages_to_one_over_the_sphere_and_peaks_towards_the_light()
    {
        // The mean over the sphere is the integral over cos θ from −1 to 1, halved (the azimuth is uniform).
        const int n = 20000;
        double sum = 0;
        for (int i = 0; i < n; i++) sum += MeitouClouds.Phase(-1 + 2 * (i + 0.5f) / n);
        Assert.InRange(sum / n, 0.99, 1.01);
        Assert.True(MeitouClouds.Phase(1) > 5 * MeitouClouds.Phase(0));          // the silver lining looking into the light
        Assert.True(MeitouClouds.Phase(-1) > MeitouClouds.Phase(0));             // the weak backward lobe: the far side brightens a little
        Assert.Equal(1, MeitouClouds.HenyeyGreenstein(0.3f, 0), 5);              // g = 0 is isotropic, 1 everywhere
    }

    [Fact]
    public void Light_falls_through_depth_but_multiple_scattering_keeps_thick_cloud_grey()
    {
        Assert.Equal(1, MeitouClouds.Transmittance(0), 5);
        float thin = MeitouClouds.Transmittance(0.5f), thick = MeitouClouds.Transmittance(3), core = MeitouClouds.Transmittance(10);
        Assert.True(thin > thick && thick > core);
        // A thick core (depth 3) keeps more than a third of the light: the multiple-scattered share falls slowly.
        Assert.InRange(thick, 0.35f, 0.5f);
        Assert.True(core > 0.1f);
    }

    [Fact]
    public void By_day_the_key_is_the_suns_colour_from_the_sun()
    {
        var sun = Vector3.Normalize(new Vector3(0.3f, 0.6f, 0.7f));
        var (direction, colour) = MeitouClouds.Key(sun, SkyPlanet.All[0].Towards, Vector3.Zero);
        Assert.Equal(sun, direction);
        Assert.Equal(KenshiLighting.SunColour(sun), colour);
    }

    [Fact]
    public void At_night_the_planet_lights_the_clouds()
    {
        var sun = new Vector3(0.69f, -0.21f, -0.69f);   // 1:00, the sun well below the horizon (SunColour is black)
        Assert.Equal(Vector3.Zero, KenshiLighting.SunColour(sun));
        var planet = SkyPlanet.All[0].Towards;
        var (direction, colour) = MeitouClouds.Key(sun, planet, new Vector3(0.05f));
        Assert.True(Vector3.Distance(planet, direction) < 1e-5f);
        Assert.True(colour.Y > 0);
        // At a share of the land's boosted light, converted to the sun colour's unit.
        float unit = KenshiLighting.SunColour(Vector3.UnitY).Y / KenshiLighting.SunLight(Vector3.UnitY).Y;
        Assert.Equal(0.05f * unit * MeitouClouds.PlanetShare, colour.Y, 4);
    }

    [Fact]
    public void The_ambient_greys_as_the_sky_clouds_over()
    {
        Assert.Equal(MeitouClouds.AmbientGrey, MeitouClouds.AmbientGreyAt(0));
        Assert.Equal(1, MeitouClouds.AmbientGreyAt(1));
        Assert.Equal(1, MeitouClouds.AmbientGreyAt(20));   // the weather's c above 1 is clamped, as the sky controller does
        // By day the ambient is the sky's blue; the night air adds to it only by night.
        var noon = MeitouClouds.Ambient(Vector3.Normalize(new Vector3(0, 0.6f, 0.8f)), Vector3.One, Vector4.Zero);
        Assert.True(noon.Z > noon.X);
        var night = new Vector3(0.69f, -0.21f, -0.69f);
        var dark = MeitouClouds.Ambient(night, Vector3.One, Vector4.Zero);
        var glow = MeitouClouds.Ambient(night, Vector3.One, new Vector4(NightAir.Airglow, 1));
        Assert.True(glow.Y > dark.Y);
    }
}
