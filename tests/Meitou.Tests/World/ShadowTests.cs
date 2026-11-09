using System.Numerics;
using Meitou.Data.World;

namespace Meitou.Tests.World;

public class ShadowTests
{
    static readonly ShadowView Camera = new(new Vector3(-51468, 1200, -14324), Vector3.Normalize(new Vector3(0.6f, -0.35f, 0.7f)), Vector3.UnitY,
            50 * MathF.PI / 180, 4 / 3f, 2);
    static readonly Vector3 Sun = Vector3.Normalize(new Vector3(0.5f, 0.6f, 0.3f));

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

    [Theory]
    [InlineData(6f, 45f)]     // a close-up: the focus sets the first split
    [InlineData(48f, 900f)]   // a far view: the practical scheme's own first split
    public void A_fifth_cascade_splits_the_second_slice_and_leaves_the_others_as_with_four(float near, float focus)
    {
        float q = MeitouShadowFit.QuantizedNear(near);
        var four = MeitouShadowFit.Splits(q, 10000, 4, focus);
        var five = MeitouShadowFit.Splits(q, 10000, 5, focus);
        Assert.Equal(6, five.Length);
        Assert.Equal(four[..2], five[..2]);
        Assert.Equal(four[2..], five[3..]);
        // The extra split at the second slice's geometric mean.
        Assert.Equal(MathF.Sqrt(four[1] * four[2]), five[2], 2);
        if (focus == 45)
        {
            // The close-up's numbers (docs/formats/shadows.md): 6, 87, 1016, 2982, 10000 with four; the fifth lands near 300.
            Assert.InRange(four[1], 80, 95);
            Assert.InRange(four[2], 950, 1100);
            Assert.InRange(five[2], 100, 1000);
        }
        // Each cascade's box: the far ones (and the first) the same size as with four, as the same slices with the same margins.
        var settings = new ShadowSettings(4096);
        var view = Camera with { Near = q, Focus = focus };
        for (int i = 0; i < 5; i++)
        {
            if (i is 1 or 2) continue;
            int j = i == 0 ? 0 : i - 1;
            var a = MeitouShadowFit.Fit(view, Sun, settings, four, j);
            var b = MeitouShadowFit.Fit(view, Sun, settings, five, i);
            if (i == 3) Assert.True(b.Texel <= a.Texel * 1.0001,
                    $"cascade {i}: {b.Texel} against {a.Texel}");   // its blend band starts later: no larger
            else Assert.Equal(a.Texel, b.Texel, 6);
        }
        // The new cascade 1 is finer than the four's cascade 1 (several times in the close-up, whose second slice is the deepest step).
        Assert.True(MeitouShadowFit.Fit(view, Sun, settings, five, 1).Texel < MeitouShadowFit.Fit(view, Sun, settings, four, 1).Texel / (focus == 45
                ? 2 : 1.2));
    }

    /// <summary>
    /// Where a world point falls on cascade 0's texel grid (light-space x and y in texels, from the box's translation).
    /// </summary>
    static (double X, double Y) GridPosition(ShadowCascade c, Vector3 p)
    {
        var r = c.Rotation;
        double lx = r.M11 * (double)p.X + r.M21 * (double)p.Y + r.M31 * (double)p.Z, ly = r.M12 * (double)p.X + r.M22 * (double)p.Y
                + r.M32 * (double)p.Z;
        return ((lx + c.Translation.X) / c.Texel, (ly + c.Translation.Y) / c.Texel);
    }

    /// <summary>
    /// How far, in texels, the grid slid past a point between two fits (whole-texel snaps do not count).
    /// </summary>
    static double Slide((double X, double Y) a, (double X, double Y) b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        return Math.Max(Math.Abs(dx - Math.Round(dx)), Math.Abs(dy - Math.Round(dy)));
    }

    [Fact]
    public void A_turning_sun_does_not_slide_the_meitou_grid_far_from_the_origin_when_it_is_anchored_near_the_eye()
    {
        // A sun turn of 1e-6 radians (a sixtieth of a frame of the viewer's time-lapse flight) with the camera 53000 units from the world origin;
        // the first cascade's texel here is 0.21 units, so the slides stay under a texel and are measured whole.
        var settings = new ShadowSettings(4096);
        var splits = MeitouShadowFit.Splits(MeitouShadowFit.QuantizedNear(Camera.Near), settings.Range, 4);
        float turn = 1e-6f;
        var turned = Vector3.Normalize(new Vector3(Sun.X * MathF.Cos(turn) + Sun.Z * MathF.Sin(turn), Sun.Y, Sun.Z * MathF.Cos(turn)
                - Sun.X * MathF.Sin(turn)));
        var p = Camera.Eye + Camera.Forward * 300;   // a receiver in front of the eye
        var eye = (Math.Round(Camera.Eye.X), Math.Round(Camera.Eye.Y), Math.Round(Camera.Eye.Z));

        double atOrigin = Slide(GridPosition(MeitouShadowFit.Fit(Camera, Sun, settings, splits, 0), p),
                GridPosition(MeitouShadowFit.Fit(Camera, turned, settings, splits, 0), p));
        double atEye = Slide(GridPosition(MeitouShadowFit.Fit(Camera, Sun, settings, splits, 0, eye), p),
                GridPosition(MeitouShadowFit.Fit(Camera, turned, settings, splits, 0, eye), p));
        // Through the origin the grid slides about 53000 × 1e-6 = 0.05 units (a quarter texel) under the point; through the eye, 300 × 1e-6.
        Assert.True(atOrigin > 0.1, $"through the origin {atOrigin:0.###} texel");
        Assert.True(atEye < 0.02, $"through the eye {atEye:0.###} texel");
        // With the sun still, either anchor keeps the grid where it was (the camera did not move).
        Assert.Equal(0,
                Slide(GridPosition(MeitouShadowFit.Fit(Camera, Sun, settings, splits, 0, eye), p),
                        GridPosition(MeitouShadowFit.Fit(Camera, Sun, settings, splits, 0, eye), p)), 6);
    }
}
