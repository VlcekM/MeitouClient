using System.Numerics;
using Meitou.Data.World;

namespace Meitou.Tests.World;

public class ShadowTests
{
    static readonly ShadowView Camera = new(new Vector3(-51468, 1200, -14324), Vector3.Normalize(new Vector3(0.6f, -0.35f, 0.7f)), Vector3.UnitY, 50 * MathF.PI / 180, 4 / 3f, 2);
    static readonly Vector3 Sun = Vector3.Normalize(new Vector3(0.5f, 0.6f, 0.3f));

    [Fact]
    public void Splits_follow_the_games_practical_scheme_halved()
    {
        var s = KenshiShadows.Splits(1, 5000, 4);
        Assert.Equal(5, s.Length);
        Assert.Equal(1f, s[0]);
        Assert.Equal(5000f, s[4]);
        // 0.5 · (0.95 · 5000^(i/4) + 0.05 · (1 + 4999 · i/4))
        Assert.Equal(35.27f, s[1], 1);
        Assert.Equal(96.10f, s[2], 1);
        Assert.Equal(376.2f, s[3], 0);
        for (int i = 1; i < s.Length; i++) Assert.True(s[i] > s[i - 1]);
    }

    [Fact]
    public void Quality_indexes_the_map_size_table()
    {
        Assert.Equal(1024, KenshiShadows.MapSize(0));
        Assert.Equal(2048, KenshiShadows.MapSize(1));
        Assert.Equal(4096, KenshiShadows.MapSize(2));
        Assert.Equal(4096, KenshiShadows.MapSize(9));
        var settings = new ShadowSettings(2048);
        Assert.Equal(2, settings.Grid);
        Assert.Equal(1024, settings.TileSize);
    }

    [Fact]
    public void Light_view_looks_away_from_the_sun_and_switches_up_for_a_vertical_sun()
    {
        var r = ShadowCascades.LightRotation(Sun);
        var z = Vector3.Transform(Sun, r);
        Assert.Equal(-1f, z.Z, 4);   // the sun is at −z: depth grows away from it
        var up = Vector3.Transform(Vector3.UnitY, r);
        Assert.Equal(0f, up.X, 4);   // world up stays in the light view's y-z plane
        Assert.True(up.Y > 0);

        var high = ShadowCascades.LightRotation(Vector3.Normalize(new Vector3(0.05f, 1, 0.1f)));
        var zAxis = Vector3.Transform(Vector3.UnitZ, high);
        Assert.Equal(0f, zAxis.X, 4);   // world +Z is the up now
        Assert.True(zAxis.Y > 0);
        foreach (var m in new[] { r, high })
            Assert.Equal(1f, Math.Abs(m.GetDeterminant()), 4);
    }

    [Fact]
    public void Every_cascade_holds_its_frustum_from_the_near_plane_to_its_split()
    {
        var cascades = ShadowCascades.Fit(Camera, Sun, new ShadowSettings());
        Assert.Equal(4, cascades.Length);
        var right = Vector3.Normalize(Vector3.Cross(Camera.Forward, Camera.Up));
        var up = Vector3.Cross(right, Camera.Forward);
        float t = MathF.Tan(Camera.FieldOfViewY / 2);
        foreach (var c in cascades)
        {
            foreach (float depth in new[] { Camera.Near, c.NearDepth, c.FarDepth })
                for (int k = 0; k < 4; k++)
                {
                    float h = depth * t, w = h * Camera.Aspect;
                    var p = Camera.Eye + Camera.Forward * depth + right * ((k & 1) == 0 ? -w : w) + up * ((k & 2) == 0 ? -h : h);
                    var q = c.Project(p);
                    Assert.InRange(q.X, 0f, 1f);
                    Assert.InRange(q.Y, 0f, 1f);
                    Assert.InRange(q.Z, 0f, 1f);
                    // The culling planes keep it too.
                    foreach (var plane in c.CullPlanes()) Assert.True(Vector4.Dot(plane, new Vector4(p, 1)) > -1e-2f * plane.Length());
                }
            Assert.True(c.FixedBias >= KenshiShadows.MinFixedBias);
            // The frustum fits inside the stable size, so the snapping step is the box's own texel.
            Assert.Equal(ShadowCascades.StableSize(c.NearDepth, c.FarDepth, Camera.FieldOfViewY, Camera.Aspect), c.Extent.X, 3);
            Assert.False(c.Unused);
            Assert.Equal(c.Size.X / new ShadowSettings().TileSize, c.Texel, 3);
        }
        // Tiles: the game's 2 × 2 grid, cascade i in column i / 2 and row i % 2 from the top (GL's y counts from the bottom).
        Assert.Equal(new Vector4(0, 0.5f, 0.5f, 0.5f), cascades[0].Tile);
        Assert.Equal(new Vector4(0, 0, 0.5f, 0.5f), cascades[1].Tile);
        Assert.Equal(new Vector4(0.5f, 0.5f, 0.5f, 0.5f), cascades[2].Tile);
        Assert.Equal(new Vector4(0.5f, 0, 0.5f, 0.5f), cascades[3].Tile);
    }

    [Fact]
    public void Moving_the_camera_moves_the_map_by_whole_texels_only()
    {
        var settings = new ShadowSettings();
        var a = ShadowCascades.Fit(Camera, Sun, settings);
        var b = ShadowCascades.Fit(Camera with { Eye = Camera.Eye + new Vector3(3.3f, 0.7f, -1.9f) }, Sun, settings);
        for (int i = 0; i < a.Length; i++)
        {
            Assert.Equal(a[i].Size, b[i].Size);   // turning or moving does not change the size
            double dx = (b[i].Translation.X - a[i].Translation.X) / a[i].Texel, dy = (b[i].Translation.Y - a[i].Translation.Y) / a[i].Texel;
            Assert.Equal(Math.Round(dx), dx, 6);
            Assert.Equal(Math.Round(dy), dy, 6);
        }
        // A fixed world point keeps its place relative to the texel grid: its tile coordinate changes by whole texels.
        var p = Camera.Eye + Camera.Forward * 20;
        float texels = settings.TileSize;
        var pa = a[0].Project(p) * texels;
        var pb = b[0].Project(p) * texels;
        Assert.Equal(MathF.Round(pb.X - pa.X), pb.X - pa.X, 2);
        Assert.Equal(MathF.Round(pb.Y - pa.Y), pb.Y - pa.Y, 2);
    }

    [Fact]
    public void A_cascade_in_front_of_a_far_near_plane_is_unused_and_the_rest_still_fit()
    {
        var far = Camera with { Near = 60 };   // beyond the first split (35)
        var cascades = ShadowCascades.Fit(far, Sun, new ShadowSettings());
        Assert.True(cascades[0].Unused);
        Assert.False(cascades[1].Unused);
        foreach (var c in cascades.Skip(1))
            Assert.Equal(ShadowCascades.StableSize(c.NearDepth, c.FarDepth, Camera.FieldOfViewY, Camera.Aspect), c.Extent.X, 3);
    }

    [Fact]
    public void Turning_the_camera_keeps_the_cascade_size()
    {
        var settings = new ShadowSettings();
        var a = ShadowCascades.Fit(Camera, Sun, settings);
        var turned = Camera with { Forward = Vector3.Normalize(new Vector3(-0.8f, -0.2f, 0.3f)) };
        var b = ShadowCascades.Fit(turned, Sun, settings);
        for (int i = 0; i < a.Length; i++) Assert.Equal(a[i].Size.X, b[i].Size.X);
    }

    [Fact]
    public void The_receiver_matrix_agrees_with_the_double_precision_projection()
    {
        var c = ShadowCascades.Fit(Camera, Sun, new ShadowSettings())[1];
        var origin = Camera.Eye;
        var m = c.OriginToTile(origin);
        var p = Camera.Eye + Camera.Forward * 60 + new Vector3(3, -2, 5);
        var viaMatrix = Vector4.Transform(new Vector4(p - origin, 1), m);
        var direct = c.Project(p);
        Assert.Equal(direct.X, viaMatrix.X, 4);
        Assert.Equal(direct.Y, viaMatrix.Y, 4);
        Assert.Equal(direct.Z, viaMatrix.Z, 4);
        // The caster matrix: clip x, y = 2 uv − 1, z = depth.
        var clip = Vector4.Transform(new Vector4(p, 1), c.WorldToClip());
        Assert.Equal(direct.X * 2 - 1, clip.X, 2);
        Assert.Equal(direct.Z, clip.Z, 2);
    }

    [Fact]
    public void Cascades_are_picked_by_the_games_clip_z_test()
    {
        var cascades = ShadowCascades.Fit(Camera, Sun, new ShadowSettings());
        var splits = KenshiShadows.Splits(KenshiShadows.SplitNear, KenshiShadows.DefaultRange, 4);
        const double n = KenshiShadows.CameraNear, f = KenshiShadows.CameraFar;
        for (int i = 0; i < 4; i++)
        {
            // At the selection depth the game's Direct3D clip z, f (d − n) / (f − n), equals csmParams[i].x = split[i + 1] − split[0].
            double d = cascades[i].SelectDepth;
            Assert.Equal(splits[i + 1] - splits[0], f * (d - n) / (f - n), 2);
        }
        // About the split plus the near clip less the first split (5 − 1).
        Assert.Equal(splits[1] + 4, cascades[0].SelectDepth, 1);
    }

    [Fact]
    public void A_horizontal_light_keeps_world_up()
    {
        // Under the horizon the game's lighting direction has its height clamped to 0; the map is still fitted along it.
        var flat = Vector3.Normalize(new Vector3(0.8f, 0, -0.6f));
        var r = ShadowCascades.LightRotation(flat);
        Assert.Equal(1f, Vector3.Transform(Vector3.UnitY, r).Y, 5);
        Assert.Equal(-1f, Vector3.Transform(flat, r).Z, 5);
        foreach (var c in ShadowCascades.Fit(Camera, flat, new ShadowSettings()))
        {
            Assert.True(double.IsFinite(c.Translation.X) && double.IsFinite(c.Translation.Z));
            Assert.InRange(c.Project(Camera.Eye + Camera.Forward * c.FarDepth * 0.9f).Z, 0f, 1f);
        }
    }

    [Fact]
    public void Hex12_offsets_are_the_games_with_its_repeated_tap()
    {
        var o = KenshiShadows.PcfOffsets;
        Assert.Equal(KenshiShadows.PcfTaps, o.Length);
        Assert.Equal(o[1], o[2]);   // the game lists (−0.5, 0.866) twice
        Assert.DoesNotContain(new Vector2(-0.5f, -0.866025f), o);
        foreach (var p in o) Assert.InRange(p.Length(), 0.99f, MathF.Sqrt(7) + 1e-4f);
        // The repeat (in place of its opposite) pulls the mean off the centre: (0, 2 · 0.866) / 12 kernel units.
        var mean = o.Aggregate(Vector2.Zero, (a, b) => a + b) / o.Length;
        Assert.Equal(0f, mean.X, 4);
        Assert.Equal(2 * 0.866025f / 12, mean.Y, 4);
    }

    [Fact]
    public void Stable_size_uses_the_games_formula()
    {
        double t = Math.Tan(25 * Math.PI / 180), aspect = 1.5, k = aspect * t * aspect * t + 2 * t;
        double expected = Math.Max(Math.Sqrt(Math.Pow(100 + 35, 2) * k + Math.Pow(100 - 35, 2)), 2 * 100 * Math.Sqrt(k));
        Assert.Equal(expected, ShadowCascades.StableSize(35, 100, 50 * Math.PI / 180, aspect), 6);
    }

    [Fact]
    public void Landmark_box_holds_every_landmark_and_its_sunward_side_is_nearer()
    {
        Assert.Null(MeitouShadowFit.FitLandmarks(Sun, [], 4096));
        Vector4[] spheres = [new(-120000, 3000, 80000, 2500), new(90000, 6000, -100000, 9000), new(5000, 1500, 2000, 2000)];
        var box = MeitouShadowFit.FitLandmarks(Sun, spheres, 4096)!;
        foreach (var s in spheres)
            foreach (var d in new[] { Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ })
            {
                var p = box.Project(new Vector3(s.X, s.Y, s.Z) + d * s.W);
                Assert.InRange(p.X, 0f, 1f);
                Assert.InRange(p.Y, 0f, 1f);
                Assert.InRange(p.Z, 0f, 1f);
            }
        // Depth grows away from the sun: a point towards the sun is nearer, and the matrices agree with Project.
        var c = new Vector3(5000, 1500, 2000);
        Assert.True(box.Project(c + Sun * 1000).Z < box.Project(c).Z);
        var clip = Vector4.Transform(new Vector4(c, 1), box.WorldToClip());
        Assert.Equal(box.Project(c).X * 2 - 1, clip.X, 3);
        Assert.Equal(box.Project(c).Z, clip.Z, 3);
        Assert.Equal(box.Size.X / 4096, box.Texel, 3);
    }
}
