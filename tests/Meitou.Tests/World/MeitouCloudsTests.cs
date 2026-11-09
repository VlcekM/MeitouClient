using System.Numerics;
using Meitou.Data.World;
using Meitou.Game.Rendering.Frame;

namespace Meitou.Tests.World;

/// <summary>
/// The Meitou clouds (docs/formats/clouds.md "Meitou clouds"): the game's cloud layer lit by a key light through its depth, with a phase and the
/// sky's ambient.
/// </summary>
public class MeitouCloudsTests
{
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

    /// <summary>
    /// Every switch is wired in both lists: the options' (<c>--meitou</c> / <c>--faithful</c>, which must read back what is set) and the live one the
    /// viewer's Tab panel and F1 use. A switch left out of a list falls back to <see cref="Enhancements.Create"/>'s stand-in, which always reads
    /// Faithful and ignores a set (the clouds and cloud shadows were missing from the live list on 2026-10-11: their checkboxes could not be turned
    /// on).
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
