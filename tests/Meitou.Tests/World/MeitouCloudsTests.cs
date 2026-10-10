using System.Numerics;
using Meitou.Data.World;
using Meitou.Rendering;

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
        Assert.Equal(MeitouClouds.DayKey(KenshiLighting.SunColour(sun), sun.Y), colour);
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

    [Fact]
    public void The_shadow_plane_is_above_every_mountain_and_sweeps_at_a_wind_aloft()
    {
        Assert.True(MeitouClouds.PlaneHeight > WorldLayout.MaxHeight);
        // The wind's texture shift (CloudLayer.WindScale per unit of wind per second) over the plane's texture scale (0.1 / height) is the ground speed.
        float sweep = CloudLayer.WindScale / (0.1f / MeitouClouds.PlaneHeight);
        Assert.InRange(sweep, 3f, 8f);   // a few times the surface wind, as clouds aloft move
    }

    [Fact]
    public void The_shadows_fade_out_with_a_low_sun()
    {
        Assert.Equal(0, MeitouClouds.ShadowAt(MeitouClouds.ShadowFadeLow));
        Assert.Equal(0, MeitouClouds.ShadowAt(-0.3f));
        Assert.Equal(MeitouClouds.ShadowStrength, MeitouClouds.ShadowAt(MeitouClouds.ShadowFadeHigh), 5);
        Assert.Equal(MeitouClouds.ShadowStrength, MeitouClouds.ShadowAt(1), 5);
        float mid = MeitouClouds.ShadowAt((MeitouClouds.ShadowFadeLow + MeitouClouds.ShadowFadeHigh) / 2);
        Assert.Equal(MeitouClouds.ShadowStrength / 2, mid, 4);
    }

    [Fact]
    public void The_sky_sees_the_same_plane_point_the_shadow_reads()
    {
        // An eye at (x, y, z) looking along d sees the plane point eye + d · (height − y) / d.y; the shadow pass reads 0.1 · point.xz / height.
        double ex = 51234.5, ey = 1800, ez = -73210.25;
        var d = Vector3.Normalize(new Vector3(0.3f, 0.5f, -0.2f));
        var (scale, offset) = MeitouClouds.Parallax(ex, ey, ez);
        var sky = new Vector2(d.X, d.Z) / d.Y * 0.1f * scale + offset;
        double t = (MeitouClouds.PlaneHeight - ey) / d.Y;
        double px = 0.1 * (ex + d.X * t) / MeitouClouds.PlaneHeight, pz = 0.1 * (ez + d.Z * t) / MeitouClouds.PlaneHeight;
        static double Wrap(double v) => v - Math.Floor(v);
        Assert.Equal(Wrap(px), Wrap(sky.X), 4);
        Assert.Equal(Wrap(pz), Wrap(sky.Y), 4);
        // Above the plane the layer keeps a quarter of its distance rather than flipping.
        Assert.Equal(0.25f, MeitouClouds.Parallax(0, 2 * MeitouClouds.PlaneHeight, 0).Scale);
    }

    [Fact]
    public void Smoothing_keeps_a_flat_field_wraps_at_the_edges_and_softens_a_step()
    {
        const int n = 16;
        var flat = new byte[n * n * 4];
        for (int i = 0; i < flat.Length; i += 4) (flat[i], flat[i + 1], flat[i + 2], flat[i + 3]) = (100, 7, 200, 0);
        var smooth = MeitouClouds.SmoothRed(flat, n, n);
        for (int i = 0; i < smooth.Length; i += 4) Assert.Equal((100, 100, 100, 255), (smooth[i], smooth[i + 1], smooth[i + 2], smooth[i + 3]));   // red only, to all three
        // A bright column at x = 0 spreads to both sides, the left one wrapping round to x = n − 1 (the texture tiles).
        var column = new byte[n * n * 4];
        for (int y = 0; y < n; y++) column[y * n * 4] = 255;
        var spread = MeitouClouds.SmoothRed(column, n, n);
        int At(int x) => spread[(5 * n + x) * 4];
        Assert.True(At(0) < 255 && At(0) > At(1));
        Assert.Equal(At(1), At(n - 1));
        Assert.Equal(0, At(n / 2));
    }

    [Fact]
    public void The_key_light_is_greyer_by_day_and_keeps_its_colour_at_sunset()
    {
        var warm = new Vector3(1.4f, 1.2f, 0.9f);
        var noon = MeitouClouds.DayKey(warm, 0.8f);
        var sunset = MeitouClouds.DayKey(warm, 0.05f);
        Assert.Equal(warm, sunset);
        Assert.Equal(Planetshine.Luminance(warm), Planetshine.Luminance(noon), 4);   // the brightness stays
        Assert.True(noon.X - noon.Z < warm.X - warm.Z);
        Assert.Equal((warm.X - warm.Z) * MeitouClouds.DayKeySaturation, noon.X - noon.Z, 4);
    }

    /// <summary>
    /// Every switch is wired in both lists: the options' (<c>--meitou</c> / <c>--faithful</c>, which must read back what is set) and the live one the viewer's Tab panel
    /// and F1 use. A switch left out of a list falls back to <see cref="Enhancements.Create"/>'s stand-in, which always reads Faithful and ignores a set
    /// (the clouds and cloud shadows were missing from the live list on 2026-10-11: their checkboxes could not be turned on).
    /// </summary>
    [Fact]
    public void Every_switch_is_wired_in_the_options_and_the_live_list()
    {
        var o = new WorldOptions();
        var options = WorldOptions.Switches(o);
        var live = WorldFrame.LiveSwitches(o, () => null, () => null, () => null);   // no GPU: some live setters act only on the renderer
        Assert.Equal(options.Select(e => e.Id), live.Select(e => e.Id));
        foreach (var e in options)
        {
            e.IsMeitou = false;
            Assert.False(e.IsMeitou, e.Id);
            e.IsMeitou = true;
            Assert.True(e.IsMeitou, e.Id);
        }
        // On fresh options every switch is Meitou; a live switch left on the stand-in reads Faithful.
        foreach (var e in live)
            if (e.Id != "gi") Assert.True(e.IsMeitou, e.Id);   // gi reads off without probes (no GPU here), by design
        Assert.Contains(live, e => e.Id == "clouds");
        Assert.Contains(live, e => e.Id == "cloudshadows");
    }
}
