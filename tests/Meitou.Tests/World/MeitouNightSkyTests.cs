using System.Numerics;
using Meitou.Data.World;
using Meitou.Rendering;

namespace Meitou.Tests.World;

/// <summary>The Meitou <c>stars</c> switch's night sky (docs/formats/sky.md "Meitou night sky"): the turn about the sun's axis, the star model's numbers, the air, the Milky Way.</summary>
public class MeitouNightSkyTests
{
    static readonly SkyClock Game = new(54, 5, 23);   // the game's latitude, sunrise and sunset

    static float DegreesBetween(Vector3 a, Vector3 b) => MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.Normalize(a), Vector3.Normalize(b)), -1f, 1f)) * 180 / MathF.PI;

    // ---- rotation ----

    [Fact]
    public void Pole_is_the_axis_the_sun_turns_about_at_the_altitude_of_the_latitude()
    {
        var axis = CelestialSphere.SunAxis(Game);
        // (0, −sin lat, cos lat): perpendicular to every sun direction of the day.
        Assert.Equal(-MathF.Sin(54 * MathF.PI / 180), axis.Y, 4);
        Assert.Equal(MathF.Cos(54 * MathF.PI / 180), axis.Z, 4);
        for (float hour = 0; hour < 24; hour += 1.5f) Assert.Equal(0, Vector3.Dot(axis, Game.SunDirection(hour)), 4);
        var pole = CelestialSphere.Pole(Game);
        Assert.Equal(54, CelestialSphere.AltitudeDegrees(pole), 3);
        Assert.True(pole.Z < 0);   // towards −z, the way opposite to the sun at noon (+z)
        Assert.Equal(0, CelestialSphere.AltitudeDegrees(CelestialSphere.Pole(new SkyClock(0, 6, 20))), 3);
    }

    [Fact]
    public void Celestial_frame_is_orthonormal_and_right_handed()
    {
        foreach (float hour in (ReadOnlySpan<float>)[0, 3.3f, 12, 20.7f])
        {
            var f = CelestialSphere.Frame(Game, 4, hour);
            Assert.Equal(1, f.X.Length(), 4); Assert.Equal(1, f.Y.Length(), 4); Assert.Equal(1, f.Z.Length(), 4);
            Assert.Equal(0, Vector3.Dot(f.X, f.Y), 4); Assert.Equal(0, Vector3.Dot(f.Y, f.Z), 4);
            Assert.True(Vector3.Distance(Vector3.Cross(f.X, f.Y), f.Z) < 1e-4f);
            var d = Vector3.Normalize(new Vector3(0.3f, 0.5f, -0.8f));
            Assert.True(Vector3.Distance(f.ToWorld(f.ToCelestial(d)), d) < 1e-4f);
        }
    }

    [Fact]
    public void The_sky_turns_once_a_game_day_about_the_pole()
    {
        var pole = CelestialSphere.Pole(Game);
        var c = Vector3.Normalize(new Vector3(0.6f, 0.2f, 0.5f));   // a fixed star
        var at = (int day, float hour) => CelestialSphere.Frame(Game, day, hour).ToWorld(c);
        Assert.True(Vector3.Distance(at(3, 7), at(4, 7)) < 1e-3f);       // a day later: the same place
        Assert.True(Vector3.Distance(at(3, 7), at(3, 7 + 24)) < 1e-3f);
        Assert.True(Vector3.Distance(at(3, 1), at(3, 13)) > 0.5f);       // half a day later: the other side
        // Its angle from the pole never changes (it turns about it).
        var angle = DegreesBetween(at(0, 0), pole);
        for (float hour = 0; hour < 24; hour += 2) Assert.Equal(angle, DegreesBetween(at(0, hour), pole), 2);
        // The pole itself stands still.
        Assert.True(Vector3.Distance(CelestialSphere.Frame(Game, 0, 5).Y, CelestialSphere.Frame(Game, 9, 17.3f).Y) < 1e-4f);
    }

    [Fact]
    public void Stars_rise_in_the_east_and_set_in_the_west_like_the_sun()
    {
        // A star on the celestial equator at the equinox point: at the phase offset's start it is where the sun rises (+x, east), a quarter day on it is up
        // and south (+z), then it sets towards −x.
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

    [Fact]
    public void Air_mass_runs_from_1_overhead_to_about_38_at_the_horizon()
    {
        Assert.Equal(1, NightAtmosphere.AirMass(90), 2);
        Assert.Equal(2, NightAtmosphere.AirMass(30), 1);
        Assert.InRange(NightAtmosphere.AirMass(0), 35, 40);
        float last = 0;
        for (float alt = 90; alt >= 0; alt -= 5) { float x = NightAtmosphere.AirMass(alt); Assert.True(x > last); last = x; }
    }

    [Fact]
    public void Stars_dim_and_redden_towards_the_horizon_and_vanish_at_it()
    {
        var zenith = NightAtmosphere.Transmission(90);
        Assert.True(zenith.X > 0.99f && zenith.Y > 0.99f && zenith.Z > 0.99f);
        var ten = NightAtmosphere.Transmission(10);
        Assert.True(ten.X > ten.Y && ten.Y > ten.Z);              // red survives best, blue worst
        Assert.True(ten.Y < 0.7f && ten.Y > 0.1f);
        var five = NightAtmosphere.Transmission(5);
        Assert.True(five.Z / five.X < ten.Z / ten.X);              // the lower, the redder
        Assert.True(five.Y < ten.Y);
        Assert.Equal(Vector3.Zero, NightAtmosphere.Transmission(0.5f));   // gone at the horizon ramp's foot
        Assert.Equal(Vector3.Zero, NightAtmosphere.Transmission(-3));
        Assert.True(NightAtmosphere.Transmission(2).Y < 0.05f);
    }

    [Fact]
    public void Only_low_stars_twinkle()
    {
        Assert.Equal(0, NightAtmosphere.Scintillation(90));
        Assert.Equal(0, NightAtmosphere.Scintillation(45));
        Assert.Equal(0, NightAtmosphere.Scintillation(35));
        Assert.True(NightAtmosphere.Scintillation(20) > 0);
        Assert.True(NightAtmosphere.Scintillation(5) > NightAtmosphere.Scintillation(20));
        Assert.True(NightAtmosphere.Scintillation(0) <= NightAtmosphere.ScintillationMax);
    }

    // ---- the stars ----

    [Fact]
    public void Faces_and_cells_map_back_and_forth()
    {
        foreach (var d in new[] { new Vector3(1, 0.2f, -0.3f), new Vector3(-0.1f, -1, 0.4f), new Vector3(0.3f, 0.3f, 1), new Vector3(-1, -1, -1), new Vector3(0, 0, -1) })
        {
            int face = StarField.Face(d, out var uv);
            Assert.InRange(uv.X, -1.0001f, 1.0001f);
            Assert.InRange(uv.Y, -1.0001f, 1.0001f);
            Assert.True(Vector3.Distance(Vector3.Normalize(StarField.FaceDirection(face, uv)), Vector3.Normalize(d)) < 1e-4f);
        }
    }

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
    public void The_starfield_has_the_real_skys_counts()
    {
        var stars = AllStars().ToList();
        int[] perLayer = new int[StarField.Layers.Length];
        foreach (var s in stars) perLayer[s.Layer]++;
        // The bright layer has no enrichment: its count is the table's (a cell holds one star at most, the centre cells are the busiest, at 0.7).
        Assert.InRange(perLayer[0], 1800 * 0.9, 1800 * 1.1);
        // The faint layers gather towards the Milky Way, so they hold more than their share, not several times as many.
        Assert.InRange(perLayer[1], 8100 * 0.9, 8100 * 4);
        Assert.InRange(perLayer[2], 45000 * 0.9, 45000 * 4);
        // The naked-eye sky: about 4800 stars brighter than magnitude 6, 15 brighter than 1 (the bright layer's flatter slope has about 100).
        int bright6 = stars.Count(s => s.Star.Magnitude < 6), bright1 = stars.Count(s => s.Star.Magnitude < 1);
        Assert.InRange(bright6, 2500, 12000);
        Assert.InRange(bright1, 30, 250);
        Assert.True(stars.All(s => s.Star.Magnitude >= StarField.Layers[s.Layer].BrightestMagnitude - 1e-3f && s.Star.Magnitude <= StarField.Layers[s.Layer].FaintestMagnitude + 1e-3f));
        // Power law: each magnitude down has about three times as many stars as the one before (layer 1: 4.5 to 6.5).
        int n5 = stars.Count(s => s.Layer == 1 && s.Star.Magnitude < 5.5), n6 = stars.Count(s => s.Layer == 1 && s.Star.Magnitude is >= 5.5f and < 6.5f);
        Assert.InRange(n6 / (double)n5, 1.5, 4.5);
    }

    [Fact]
    public void Star_density_is_even_over_the_sphere_not_denser_at_the_centres_of_cube_faces()
    {
        // The bright layer (no Milky Way enrichment): stars within 25° of a face's centre against within 25° of a cube corner. A cell shrinks to a fifth of its
        // solid angle towards the corner, so without the chance's solid-angle weight the corner would hold about three times as many.
        var stars = AllStars().Where(s => s.Layer == 0).Select(s => Vector3.Normalize(StarField.FaceDirection(s.Face, s.Star.FaceUv))).ToList();
        int centre = stars.Count(d => DegreesBetween(d, new Vector3(0.3f, 0.2f, 1)) < 25), corner = stars.Count(d => DegreesBetween(d, new Vector3(1, 1, 1)) < 25);
        Assert.InRange(centre / (double)corner, 0.65, 1.55);
    }

    [Fact]
    public void No_star_sits_where_its_spot_would_cross_a_cube_face_edge()
    {
        foreach (var (layer, _, s) in AllStars())
        {
            float margin = StarField.FaceEdgeMargin * 2f / StarField.Layers[layer].Cells;   // in face units
            Assert.InRange(s.FaceUv.X, -1 + margin - 1e-5f, 1 - margin + 1e-5f);
            Assert.InRange(s.FaceUv.Y, -1 + margin - 1e-5f, 1 - margin + 1e-5f);
        }
    }

    [Fact]
    public void Star_hashes_are_well_mixed_and_repeatable()
    {
        Assert.Equal(StarField.Pcg3d(3, 4, 5), StarField.Pcg3d(3, 4, 5));
        Assert.NotEqual(StarField.Pcg3d(3, 4, 5), StarField.Pcg3d(4, 3, 5));
        Assert.NotEqual(StarField.Pcg1(77), StarField.Pcg1(78));
        double sum = 0, sum1 = 0;
        const int count = 20000;
        for (uint i = 0; i < count; i++) { sum += (StarField.Pcg3d(i, i / 7, 3).Y >> 16) / 65536.0; sum1 += (StarField.Pcg1(i) >> 16) / 65536.0; }
        Assert.InRange(sum / count, 0.49, 0.51);
        Assert.InRange(sum1 / count, 0.49, 0.51);
        // Cell ids are unique over layers, faces and cells.
        var ids = new HashSet<uint>();
        for (int layer = 0; layer < StarField.Layers.Length; layer++)
            for (int face = 0; face < 6; face++)
                for (int iy = 0; iy < StarField.Layers[layer].Cells; iy += 7)
                    for (int ix = 0; ix < StarField.Layers[layer].Cells; ix += 5) Assert.True(ids.Add(StarField.CellId(layer, face, ix, iy)));
    }

    [Fact]
    public void Brighter_stars_have_lower_magnitudes_and_the_range_is_compressed()
    {
        Assert.True(StarField.Brightness(0) > StarField.Brightness(1));
        Assert.Equal(1, StarField.Brightness(0), 5);
        // 5 magnitudes are 100 times in light; on this screen 10^(0.4 · 0.7 · 5) = 25.
        Assert.Equal(25, StarField.Brightness(1) / StarField.Brightness(6), 0);
        var layer = StarField.Layers[1];
        Assert.Equal(layer.BrightestMagnitude, StarField.MagnitudeAt(layer, 0), 4);
        Assert.Equal(layer.FaintestMagnitude, StarField.MagnitudeAt(layer, 1), 4);
        Assert.True(StarField.MagnitudeAt(layer, 0.5f) > StarField.MagnitudeAt(layer, 0.1f));
    }

    [Fact]
    public void Star_colours_are_blue_white_to_orange_and_mostly_pale()
    {
        Assert.Equal(StarColours.TableSize, StarColours.Table().Length);
        var t = StarColours.Table();
        foreach (var c in t) Assert.Equal(1, StarColours.Luminance(c), 3);   // colour only; the magnitude sets the brightness
        Assert.True(t[0].Z > t[0].X);                                         // the first (hottest) are bluish
        Assert.True(t[^1].X > t[^1].Z);                                       // the last are orange
        float redOverBlue = 0;
        foreach (var c in t) redOverBlue += c.X / c.Z;
        Assert.InRange(redOverBlue / t.Length, 0.8, 1.5);                     // on average near white
        Assert.True(t.All(c => c.X / c.Z is > 0.6f and < 2.2f));              // never saturated
        // Blackbodies: 3000 K orange, 6500 K white, 12000 K blue-white.
        var warm = StarColours.Blackbody(3000); var white = StarColours.Blackbody(6500); var blue = StarColours.Blackbody(12000);
        Assert.True(warm.X > 2 * warm.Z);
        Assert.InRange(white.X / white.Z, 0.85, 1.2);
        Assert.True(blue.Z > blue.X);
        Assert.InRange(StarColours.Temperature(0.65f), 5300, 6000);          // the sun's colour index
        Assert.True(StarColours.Temperature(-0.2f) > 10000);
    }

    // ---- the Milky Way ----

    [Fact]
    public void The_galactic_frame_is_tilted_against_the_celestial_equator()
    {
        Assert.Equal(0, Vector3.Dot(MilkyWay.Pole, MilkyWay.Centre), 4);
        Assert.Equal(0, Vector3.Dot(MilkyWay.Pole, MilkyWay.East), 4);
        Assert.Equal(MilkyWay.TiltDegrees, DegreesBetween(MilkyWay.Pole, Vector3.UnitY), 2);          // the plane is inclined by the tilt
        Assert.Equal(MilkyWay.CentreDeclinationDegrees, MathF.Asin(MilkyWay.Centre.Y) * 180 / MathF.PI, 2);
        var (lat, lon) = MilkyWay.Galactic(MilkyWay.Centre);
        Assert.Equal(0, lat, 4); Assert.Equal(0, lon, 4);
    }

    [Fact]
    public void The_band_is_brightest_at_the_bulge_and_fades_away_from_the_plane()
    {
        // Average a few directions (the noise is lumpy) at each place.
        float Mean(float lonDeg, float latDeg)
        {
            float sum = 0;
            for (int i = 0; i < 40; i++)
            {
                float l = (lonDeg + (i % 8 - 3.5f) * 1.5f) * MathF.PI / 180, b = (latDeg + (i / 8 - 2) * 1.5f) * MathF.PI / 180;
                var d = MilkyWay.Centre * (MathF.Cos(b) * MathF.Cos(l)) + MilkyWay.East * (MathF.Cos(b) * MathF.Sin(l)) + MilkyWay.Pole * MathF.Sin(b);
                var v = MilkyWay.Radiance(Vector3.Normalize(d));
                sum += StarColours.Luminance(v);
            }
            return sum / 40;
        }
        float bulge = Mean(0, 6), opposite = Mean(180, 4), pole = Mean(0, 80), offPlane = Mean(90, 45);
        Assert.True(bulge > 2 * opposite);          // the bulge outshines the far side of the band
        Assert.True(opposite > 4 * pole);           // the band is brighter than the sky at the pole
        Assert.True(opposite > 4 * offPlane);
        Assert.True(pole < 0.01f);
    }

    [Fact]
    public void The_bulge_is_warm_and_the_outer_band_cool()
    {
        Vector3 Mean(float lonDeg, float latDeg)
        {
            var sum = Vector3.Zero;
            for (int i = 0; i < 40; i++)
            {
                float l = (lonDeg + (i % 8 - 3.5f) * 1.5f) * MathF.PI / 180, b = (latDeg + (i / 8 - 2) * 1.5f) * MathF.PI / 180;
                sum += MilkyWay.Radiance(Vector3.Normalize(MilkyWay.Centre * (MathF.Cos(b) * MathF.Cos(l)) + MilkyWay.East * (MathF.Cos(b) * MathF.Sin(l)) + MilkyWay.Pole * MathF.Sin(b)));
            }
            return sum;
        }
        var core = Mean(0, 8); var edge = Mean(120, 6);
        Assert.True(core.X / core.Z > edge.X / edge.Z);
        Assert.True(edge.Z > edge.X);               // cool: blue over red
    }

    [Fact]
    public void Dust_darkens_without_browning_and_the_glow_is_neutral_to_cool()
    {
        // Neutral extinction: the three channels differ by under 12% even through a dense lane (the old one made 0.41 : 0.37 : 0.30 at a depth of 1, brown).
        foreach (float tau in new[] { 0.3f, 1f, 2.5f })
        {
            var t = MilkyWay.Transmit(tau);
            Assert.InRange(t.X / t.Z, 0.99f, 1.12f);
            Assert.Equal(t.X, t.Y, 5);
        }
        // The glow's own colour: blue never under red away from the bulge, and cream (red over blue by under 15%) at its warmest.
        var cool = MilkyWay.SmoothLight(Vector3.Normalize(MilkyWay.Centre * -1 + MilkyWay.Pole * 0.05f)).Light;
        Assert.True(cool.Z >= cool.X);
        var warmest = Vector3.Zero;
        for (int i = 0; i < 400; i++)
        {
            float l = (-6 + i * 0.03f) * MathF.PI / 180, b = ((i * 37 % 100) / 100f - 0.5f) * 4 * MathF.PI / 180;
            var d = MilkyWay.Centre * (MathF.Cos(b) * MathF.Cos(l)) + MilkyWay.East * (MathF.Cos(b) * MathF.Sin(l)) + MilkyWay.Pole * MathF.Sin(b);
            var v = MilkyWay.SmoothLight(Vector3.Normalize(d)).Light;
            if (v.X / v.Z > warmest.X / MathF.Max(warmest.Z, 1e-9f)) warmest = v;
        }
        Assert.InRange(warmest.X / warmest.Z, 0.95f, 1.15f);
        Assert.True(warmest.Y <= warmest.X * 1.01f);   // never green-yellow (olive)
    }

    [Fact]
    public void The_air_reddens_the_glow_less_than_the_stars_but_dims_it_the_same()
    {
        foreach (float altitude in new[] { 5f, 10f, 20f, 40f })
        {
            var stars = NightAtmosphere.Transmission(altitude);
            var band = MilkyWay.BandTransmission(stars);
            Assert.Equal(stars.Y, band.Y, 5);                                       // the green channel is the same
            Assert.True(band.X / band.Z <= stars.X / stars.Z + 1e-6f);               // less red over blue than the stars'
            Assert.True(band.X / band.Z < 1 + 0.4f * (stars.X / stars.Z - 1) + 0.2f); // and only a share of the shift
        }
    }

    [Fact]
    public void Pixel_dust_is_crisp_dense_in_a_lane_and_thin_in_haze()
    {
        // Monotone in the baked depth for a fixed noise; haze (a thin baked depth) is thinned, a lane's core deepened; the noise only matters at the rim.
        foreach (float h in new[] { 0f, 0.5f, 1f })
        {
            float prev = -1;
            for (float tau = 0; tau <= 3; tau += 0.05f)
            {
                float p = MilkyWay.PixelTau(tau, h);
                Assert.True(p >= prev - 1e-6f, $"tau {tau} h {h}");
                prev = p;
            }
        }
        Assert.True(MilkyWay.PixelTau(0.1f, 0.5f) < 0.1f * 0.5f);
        Assert.True(MilkyWay.PixelTau(1.5f, 0.5f) > 1.5f * 2f);
        Assert.True(MilkyWay.Transmit(MilkyWay.PixelTau(1.2f, 0.5f)).Y < 0.1f);          // a lane is dark
        Assert.Equal(MilkyWay.PixelTau(2f, 0.1f), MilkyWay.PixelTau(2f, 0.9f), 3);        // the core does not flicker with the noise
        Assert.NotEqual(MilkyWay.PixelTau(0.5f, 0.1f), MilkyWay.PixelTau(0.5f, 0.9f), 2);  // the rim does
        // The edge is steep: the factor goes from the haze's to the core's between baked depths 0.3 and 0.9 (the bake alone ramps over the whole lane).
        Assert.True(MilkyWay.PixelTau(0.3f, 0.5f) / 0.3f < 1.5f * MilkyWay.DustHaze);
        Assert.True(MilkyWay.PixelTau(0.9f, 0.5f) / 0.9f > 0.95f * MilkyWay.DustCore);
    }

    [Fact]
    public void The_grain_keeps_the_stars_energy_inside_its_reach()
    {
        // A grain star is a Gaussian of GrainSigma pixels cut at the core reach: nearly all of its energy lands inside, so the share of the band's light is kept.
        float inside = 1 - MathF.Exp(-SkyRenderer.StarCoreReach * SkyRenderer.StarCoreReach / (2 * MilkyWay.GrainSigma * MilkyWay.GrainSigma));
        Assert.True(inside > 0.95f);
        Assert.InRange(MilkyWay.GrainShare, 0.3f, 0.8f);
        Assert.InRange(MilkyWay.GrainChance, 0.3f, 1f);
        Assert.InRange(MilkyWay.GrainSpread, 0f, 1f);   // the brightness spread keeps every star positive
    }

    [Fact]
    public void The_grain_is_as_fine_as_the_screen_at_any_resolution()
    {
        // Pixels per radian of a 60 degree vertical view: height / 2 / tan(30 degrees).
        float Ppr(float height) => height / 2 / MathF.Tan(30 * MathF.PI / 180);
        Assert.Equal(1024, MilkyWay.GrainCells);
        Assert.Equal(MilkyWay.GrainCells, MilkyWay.GrainCellsFor(Ppr(1080)));
        Assert.Equal(512, MilkyWay.GrainCellsFor(Ppr(720)));
        Assert.Equal(1024, MilkyWay.GrainCellsFor(Ppr(1440)));
        Assert.Equal(2048, MilkyWay.GrainCellsFor(Ppr(2160)));
        // Whatever the view, a cell is between 1.4 and 2.9 pixels at the face's centre, and the count is a power of two in its range.
        for (float ppr = 200; ppr < 6000; ppr += 37)
        {
            int cells = MilkyWay.GrainCellsFor(ppr);
            Assert.Equal(0, cells & (cells - 1));
            Assert.InRange(cells, MilkyWay.GrainCellsMin, MilkyWay.GrainCellsMax);
            float pixels = 2f / cells * ppr;
            if (cells > MilkyWay.GrainCellsMin && cells < MilkyWay.GrainCellsMax) Assert.InRange(pixels, 1.4f, 2.9f);
        }
    }

    [Fact]
    public void Radiance_is_finite_and_not_negative_everywhere_and_has_dust_lanes()
    {
        var rng = new Random(5);
        float min = float.MaxValue, max = 0;
        for (int i = 0; i < 4000; i++)
        {
            var d = Vector3.Normalize(new Vector3((float)rng.NextDouble() * 2 - 1, (float)rng.NextDouble() * 2 - 1, (float)rng.NextDouble() * 2 - 1));
            var v = MilkyWay.Radiance(d);
            Assert.True(float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z) && v.X >= 0 && v.Y >= 0 && v.Z >= 0);
            max = MathF.Max(max, v.Y);
        }
        Assert.InRange(max, 0.01f, 0.2f);           // a faint glow: far under the vanilla nebula
        // Along the plane near the bulge the light has dark lanes: some places are well under the local average.
        int dark = 0, count = 0;
        float total = 0;
        var samples = new List<float>();
        for (int i = 0; i < 600; i++)
        {
            float l = (-25 + i * 0.083f) * MathF.PI / 180, b = ((i * 37 % 100) / 100f - 0.5f) * 8 * MathF.PI / 180;
            var d = MilkyWay.Centre * (MathF.Cos(b) * MathF.Cos(l)) + MilkyWay.East * (MathF.Cos(b) * MathF.Sin(l)) + MilkyWay.Pole * MathF.Sin(b);
            samples.Add(StarColours.Luminance(MilkyWay.Radiance(Vector3.Normalize(d))));
        }
        total = samples.Sum() / samples.Count;
        foreach (var s in samples) { count++; if (s < 0.4f * total) dark++; }
        min = samples.Min();
        Assert.InRange(dark / (double)count, 0.03, 0.6);
        Assert.True(min < 0.25f * total);
    }

    [Fact]
    public void The_band_comes_near_the_zenith_and_shows_during_the_night()
    {
        // Over a day the plane's highest point comes within a few degrees of the zenith at latitude 54 (its inclination of 62° is 8° off).
        float best = 0, bestAtNight = 0;
        for (float hour = 0; hour < 24; hour += 0.25f)
        {
            var frame = CelestialSphere.Frame(Game, 0, hour);
            float top = 0;
            for (int deg = 0; deg < 360; deg += 3)
            {
                float l = deg * MathF.PI / 180;
                var c = MilkyWay.Centre * MathF.Cos(l) + MilkyWay.East * MathF.Sin(l);
                top = MathF.Max(top, frame.ToWorld(c).Y);
            }
            best = MathF.Max(best, top);
            if (hour >= 23 || hour < 5) bestAtNight = MathF.Max(bestAtNight, top);   // the game's night
        }
        Assert.True(best > MathF.Cos(12 * MathF.PI / 180));                 // within 12° of the zenith some time in the day
        Assert.True(bestAtNight > MathF.Cos(25 * MathF.PI / 180));          // and high up during the night, where it is seen
    }

    [Fact]
    public void Cube_texels_cover_the_sphere_seamlessly()
    {
        const int size = 8;
        var all = new List<Vector3>();
        for (int face = 0; face < 6; face++)
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    var d = MilkyWay.TexelDirection(face, x, y, size);
                    Assert.Equal(1, d.Length(), 4);
                    all.Add(d);
                }
        // Every texel has a neighbour (another texel) within about one texel's angle, also across the edges: no gaps and no overlaps of whole faces.
        float texel = 90f / size * 1.4f;
        for (int i = 0; i < all.Count; i++)
        {
            float nearest = float.MaxValue;
            for (int j = 0; j < all.Count; j++) if (j != i) nearest = MathF.Min(nearest, DegreesBetween(all[i], all[j]));
            Assert.True(nearest < texel, $"texel {i}: nearest {nearest}");
            Assert.True(nearest > 90f / size * 0.2f, $"texel {i} overlaps another: {nearest}");
        }
        // The six faces point at ±x, ±y, ±z.
        Assert.True(MilkyWay.TexelDirection(0, size / 2, size / 2, size).X > 0.9f);
        Assert.True(MilkyWay.TexelDirection(3, size / 2, size / 2, size).Y < -0.9f);
        Assert.True(MilkyWay.TexelDirection(5, size / 2, size / 2, size).Z < -0.9f);
    }

    [Fact]
    public void Baking_gives_every_level_of_every_face()
    {
        var levels = MilkyWay.Bake(16);
        Assert.Equal(5, levels.Length);   // 16, 8, 4, 2, 1
        for (int level = 0; level < levels.Length; level++)
        {
            Assert.Equal(6, levels[level].Length);
            int s = Math.Max(16 >> level, 1);
            foreach (var face in levels[level])
            {
                Assert.Equal(s * s * 4, face.Length);
                Assert.True(face.All(h => float.IsFinite((float)h)));
            }
        }
        // Alpha is the dust's optical depth (not negative, not absurd), and the mean of a level stays the base level's mean (box filtering), for the light and the depth.
        Assert.True(levels[0].All(face => face.Where((_, i) => i % 4 == 3).All(h => (float)h >= 0 && (float)h < 4)));
        double Mean(Half[][] faces, int channel) => faces.SelectMany(f => f.Where((_, i) => i % 4 == channel)).Average(h => (double)(float)h);
        Assert.Equal(Mean(levels[0], 1), Mean(levels[2], 1), 3);
        Assert.Equal(Mean(levels[0], 3), Mean(levels[2], 3), 2);
        Assert.True(Mean(levels[0], 3) > 0);        // some dust
        // Deterministic.
        Assert.Equal(levels[1][3], MilkyWay.Bake(16)[1][3]);
    }

    // ---- the switch ----

    [Fact]
    public void The_stars_switch_is_a_Meitou_default_that_Faithful_turns_off()
    {
        var o = new WorldOptions();
        Assert.True(o.MeitouStars);
        var switches = WorldOptions.Switches(o);
        var stars = Assert.Single(switches, e => e.Id == "stars");
        Assert.True(stars.IsMeitou);
        Enhancements.Apply(switches, "stars", meitou: false);
        Assert.False(o.MeitouStars);
        Assert.StartsWith("Faithful", stars.State);
        Enhancements.Apply(switches, "all", meitou: true);
        Assert.True(o.MeitouStars);
        Assert.StartsWith("Meitou", stars.State);
    }
}
