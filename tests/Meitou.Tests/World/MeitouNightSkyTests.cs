using System.Numerics;
using Meitou.Data.World;

namespace Meitou.Tests.World;

/// <summary>
/// The Meitou <c>stars</c> switch's night sky (docs/formats/sky.md "Meitou night sky"): the turn about the sun's axis, the star model's numbers, the
/// air.
/// </summary>
public class MeitouNightSkyTests
{
    static readonly SkyClock Game = new(54, 5, 23);   // the game's latitude, sunrise and sunset

    static float DegreesBetween(Vector3 a,
            Vector3 b) => MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.Normalize(a), Vector3.Normalize(b)), -1f, 1f)) * 180 / MathF.PI;

    // ---- rotation ----

    [Fact]
    public void Stars_rise_in_the_east_and_set_in_the_west_like_the_sun()
    {
        // A star on the celestial equator at the equinox point: at the phase offset's start it is where the sun rises (+x, east), a quarter day on it
        // is up and south (+z), then it sets towards −x.
        var c = Vector3.UnitX;
        double offset = CelestialSphere.PhaseOffset;
        float hourOfOffset = (float)(-offset / (2 * Math.PI) * 24);          // the hour at which the turn is 0
        hourOfOffset = (hourOfOffset % 24 + 24) % 24;
        var rise = CelestialSphere.Frame(Game, 0, hourOfOffset).ToWorld(c);
        var later = CelestialSphere.Frame(Game, 0, hourOfOffset + 3).ToWorld(c);
        var noon = CelestialSphere.Frame(Game, 0, hourOfOffset + 6).ToWorld(c);
        Assert.True(Vector3.Distance(rise, Vector3.UnitX) < 1e-3f);
        Assert.True(later.Y > 0.2f && later.X > 0 && later.Z > 0);           // rising, still on the east side, towards the south
        Assert.True(noon.Y > 0.5f && MathF.Abs(noon.X) < 1e-3f && noon.Z > 0);   // culminating due south at 90 − 54 = 36° up
        Assert.Equal(36, CelestialSphere.AltitudeDegrees(noon), 2);
        // Turning the same way as the sun: the sense of the cross product of consecutive positions about the axis is the sun's.
        var axis = CelestialSphere.SunAxis(Game);
        float starSense = Vector3.Dot(Vector3.Cross(rise, later), axis);
        float sunSense = Vector3.Dot(Vector3.Cross(Game.SunDirection(6), Game.SunDirection(9)), axis);
        Assert.True(starSense > 0 && sunSense > 0);
    }

    // ---- the air ----

    // ---- the stars ----

    static IEnumerable<(int Layer, int Face, StarCandidate Star)> AllStars()
    {
        for (int layer = 0; layer < StarField.Layers.Length; layer++)
        {
            int n = StarField.Layers[layer].Cells;
            for (int face = 0; face < 6; face++)
                for (int iy = 0; iy < n; iy++)
                    for (int ix = 0; ix < n; ix++)
                    {
                        var s = StarField.Candidate(layer, face, ix, iy);
                        if (s.Present) yield return (layer, face, s);
                    }
        }
    }

    [Fact]
    public void Star_density_is_even_over_the_sphere_not_denser_at_the_centres_of_cube_faces()
    {
        // The bright layer: stars within 25° of a face's centre against within 25° of a cube corner. A cell shrinks to a fifth of its
        // solid angle towards the corner, so without the chance's solid-angle weight the corner would hold about three times as many.
        var stars = AllStars().Where(s => s.Layer == 0).Select(s => Vector3.Normalize(StarField.FaceDirection(s.Face, s.Star.FaceUv))).ToList();
        int centre = stars.Count(d => DegreesBetween(d, new Vector3(0.3f, 0.2f, 1)) < 25), corner = stars.Count(d => DegreesBetween(d,
                new Vector3(1, 1, 1)) < 25);
        Assert.InRange(centre / (double)corner, 0.65, 1.55);
    }

    // ---- the switch ----
}
