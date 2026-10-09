using System.Diagnostics;
using System.Numerics;
using Meitou.Data.World;
using Meitou.Rendering;

namespace Meitou.Tests.Rendering;

/// <summary>The shore distance field's CPU bake on synthetic terrain (docs/render-water.md "Shore distance field"); no game and no GPU.</summary>
public class ShoreFieldTests(Xunit.ITestOutputHelper output)
{
    const int N = 512;
    const float Texel = 10f;

    static ShoreGrid Bake(Func<float, float, float> h, float cx = 0, float cz = 0) => ShoreBake.Bake(h, cx, cz, N, Texel);

    static (float X, float Z) Centre(ShoreGrid g, int i, int j) => (g.X0 + (i + 0.5f) * g.Texel, g.Z0 + (j + 0.5f) * g.Texel);

    [Theory]
    [InlineData(0.0)]
    [InlineData(30.0)]
    [InlineData(77.0)]
    public void Straight_beach_distance_is_the_perpendicular_distance_with_water_positive(double degrees)
    {
        float c = MathF.Cos((float)(degrees * Math.PI / 180)), s = MathF.Sin((float)(degrees * Math.PI / 180));
        // Slope 0.05 through 100 along (c, s): land for u > 0.
        var g = Bake((x, z) => 100f + 0.05f * (c * x + s * z) + 0.0f);
        float worst = 0;
        int checkedTexels = 0;
        for (int j = 0; j < N; j++)
            for (int i = 0; i < N; i++)
            {
                var (x, z) = Centre(g, i, j);
                float u = c * x + s * z, expected = -u;
                if (MathF.Abs(expected) > 1800) continue;
                // The nearest waterline point must lie inside the baked square (else the true nearest point is not in the data).
                if (MathF.Abs(x - u * c) > 2400 || MathF.Abs(z - u * s) > 2400) continue;
                float d = g.Distance[j * N + i];
                if (MathF.Abs(expected) > Texel) Assert.True(MathF.Sign(d) == MathF.Sign(expected), $"sign at ({x},{z}): {d} vs {expected}");
                worst = Math.Max(worst, MathF.Abs(d - expected));
                checkedTexels++;
            }
        Assert.True(checkedTexels > 10000);
        Assert.True(worst <= 3f, $"worst error {worst} units");
    }

    [Fact]
    public void Shoreline_is_placed_between_texels_not_at_their_centres()
    {
        // Shore at x = 3 (a third of a texel past a texel centre line): texel centres are at ...±5, so the field must read +/-2 and 8 beside it.
        var g = Bake((x, z) => 100f + 0.2f * (x - 3f));
        var (i, j) = ((int)MathF.Floor((0 - g.X0) / Texel), N / 2);
        float left = g.Distance[j * N + i - 1], right = g.Distance[j * N + i];
        Assert.InRange(left, 7.5f, 8.5f); // centre at x = -5, 8 from the shore
        Assert.InRange(right, -2.5f, -1.5f); // centre at x = +5... land, 2 from the shore
    }

    [Fact]
    public void Island_distance_is_the_radius_difference()
    {
        var g = Bake((x, z) => 100f + (1000f - MathF.Sqrt(x * x + z * z)) * 0.1f);
        float worst = 0;
        for (int j = 0; j < N; j++)
            for (int i = 0; i < N; i++)
            {
                var (x, z) = Centre(g, i, j);
                float r = MathF.Sqrt(x * x + z * z);
                if (r < 40 || r > 2300) continue;
                worst = Math.Max(worst, MathF.Abs(g.Distance[j * N + i] - (r - 1000f)));
            }
        Assert.True(worst <= Texel, $"worst error {worst} units");
    }

    [Fact]
    public void Open_sea_is_exposed_and_a_small_pond_is_not()
    {
        // Sea for x > 0 (shore at x = 0), a 150-unit pond at (-1500, 0), land otherwise.
        float Height(float x, float z)
        {
            float sea = 100f + (-x) * 0.1f;
            float pond = 100f + (MathF.Sqrt((x + 1500) * (x + 1500) + z * z) - 150f) * 0.1f;
            return Math.Min(sea, pond);
        }
        var g = Bake(Height);
        // Sea side, at least 50 units off the shore and well inside the grid.
        foreach (var (x, z) in new[] { (60f, 0f), (300f, 400f), (1500f, -800f), (2000f, 0f) })
            Assert.True(g.Sample(x, z).Y > 0.95f, $"sea exposure at ({x},{z}) = {g.Sample(x, z).Y}");
        Assert.True(g.Sample(-1500, 0).Y < 0.05f, $"pond exposure {g.Sample(-1500, 0).Y}");
        Assert.True(g.Sample(-1500 + 80, 20).Y < 0.05f);
    }

    [Fact]
    public void An_islet_has_no_surf_round_it_and_an_island_does()
    {
        // Open sea everywhere, a 150-unit islet at (-1200, 0) and a 700-unit island at (1200, 0).
        float Height(float x, float z)
        {
            float islet = 100f + (150f - MathF.Sqrt((x + 1200) * (x + 1200) + z * z)) * 0.1f;
            float island = 100f + (700f - MathF.Sqrt((x - 1200) * (x - 1200) + z * z)) * 0.1f;
            return Math.Max(islet, island);
        }
        var g = Bake(Height);
        foreach (var (x, z) in new[] { (-1200f + 200, 0f), (-1200f, -220f), (-1200f - 190, 60f) })
            Assert.True(g.Sample(x, z).Y < 0.05f, $"islet exposure at ({x},{z}) = {g.Sample(x, z).Y}");
        foreach (var (x, z) in new[] { (1200f + 760, 0f), (1200f, 780f), (1200f - 760, 100f) })
            Assert.True(g.Sample(x, z).Y > 0.95f, $"island exposure at ({x},{z}) = {g.Sample(x, z).Y}");
    }

    [Fact]
    public void Narrow_bay_has_low_exposure_inside_and_full_at_the_mouth_in_the_sea()
    {
        // Sea for x > 1500; a 300-wide channel |z| < 150 from x = -1800 to the sea; land elsewhere.
        float Height(float x, float z)
        {
            float sea = 100f + (1500f - x) * 0.1f;
            float channel = 100f + MathF.Max(MathF.Abs(z) - 150f, -1800f - x) * 0.1f;
            return Math.Min(sea, channel);
        }
        var g = Bake(Height);
        Assert.True(g.Distance[(N / 2) * N + N / 2] > 100, "channel centre is water");
        Assert.True(g.Sample(-1200, 0).Y < 0.05f, $"inner bay {g.Sample(-1200, 0).Y}");
        Assert.True(g.Sample(-300, 0).Y < 0.05f, $"middle of the bay {g.Sample(-300, 0).Y}");
        Assert.True(g.Sample(2800, 0).Y > 0.95f, $"open sea outside {g.Sample(2800, 0).Y}");
        // Near the mouth it is in between: some open sea within reach.
        Assert.True(g.Sample(1350, 0).Y > g.Sample(-300, 0).Y);
    }

    [Fact]
    public void Open_water_to_the_edge_is_clamped_and_fully_exposed()
    {
        var g = Bake((x, z) => 0f);
        Assert.All(g.Distance, d => Assert.Equal(g.MaxDistance, d));
        Assert.All(g.Exposure, e => Assert.Equal(1f, e));
    }

    [Fact]
    public void Land_everywhere_is_negative_and_unexposed()
    {
        var g = Bake((x, z) => 500f);
        Assert.All(g.Distance, d => Assert.Equal(-g.MaxDistance, d));
        Assert.All(g.Exposure, e => Assert.Equal(0f, e));
    }

    [Fact]
    public void Gradient_direction_has_no_jumps_near_a_straight_shore()
    {
        float c = MathF.Cos(0.5f), s = MathF.Sin(0.5f);
        var g = Bake((x, z) => 100f + 0.07f * (c * x + s * z));
        Vector2 Grad(int i, int j) => Vector2.Normalize(new(
            g.Distance[j * N + i + 1] - g.Distance[j * N + i - 1], g.Distance[(j + 1) * N + i] - g.Distance[(j - 1) * N + i]));
        float worst = 0;
        for (int j = 2; j < N - 3; j++)
            for (int i = 2; i < N - 3; i++)
            {
                if (g.Distance[j * N + i] is < 0 or > 800) continue;
                float cos1 = Vector2.Dot(Grad(i, j), Grad(i + 1, j)), cos2 = Vector2.Dot(Grad(i, j), Grad(i, j + 1));
                worst = Math.Max(worst, MathF.Acos(Math.Clamp(Math.Min(cos1, cos2), -1, 1)) * 180 / MathF.PI);
            }
        Assert.True(worst < 30, $"largest turn between neighbouring water texels {worst} degrees");
    }

    [Fact]
    public void Centre_snaps_to_whole_texels_and_the_rect_matches()
    {
        var g = ShoreBake.Bake((x, z) => 0f, 1234.4f, -77.7f, 64, 10f);
        Assert.Equal(1230f, g.X0 + 32 * 10f);
        Assert.Equal(-80f, g.Z0 + 32 * 10f);
        Assert.Equal(g.X0 + 640f, g.X1);
    }

    [Fact]
    [Bench]
    public void Full_size_bake_time()
    {
        // A diagonal coast with a few islands, 1024² at 10 units.
        float H(float x, float z) => 100f + 0.05f * (x + 0.3f * z) + 40f * MathF.Sin(x * 0.0021f) * MathF.Cos(z * 0.0017f);
        ShoreBake.Bake(H, 0, 0);
        var watch = Stopwatch.StartNew();
        for (int k = 0; k < 5; k++) ShoreBake.Bake(H, k * 10, 0);
        double ms = watch.Elapsed.TotalMilliseconds / 5;
        output.WriteLine($"shore field bake 1024^2: {ms:0.0} ms");
        Assert.True(ms < 100, $"{ms} ms");
    }

    // ------------------------------------------------------------------ HeightSnapshot

    [Fact]
    public void Snapshot_reads_the_coarse_grid_far_from_the_fine_window_and_the_fine_one_inside_it()
    {
        const int coarseSize = 17;
        var coarse = new ushort[coarseSize * coarseSize];
        for (int j = 0; j < coarseSize; j++)
            for (int i = 0; i < coarseSize; i++) coarse[j * coarseSize + i] = (ushort)(1000 * i + 300 * j);
        // A 40 x 40 window of the real heightmap at the world's centre, flat at raw 20000.
        int cols = 40;
        var fineRaw = new ushort[cols * cols];
        Array.Fill(fineRaw, (ushort)20000);
        var fine = new HeightWindow(2000, 2000, 1, cols, cols, fineRaw);
        var snap = new HeightSnapshot(coarse, coarseSize, fine, fineBand: 100);

        // Far corner of the world: coarse grid bilinear at an exact node.
        float cell = WorldLayout.WorldSize / (float)(coarseSize - 1);
        float x = -WorldLayout.HalfWorldSize + 3 * cell, z = -WorldLayout.HalfWorldSize + 2 * cell;
        Assert.Equal(WorldLayout.RawToHeight((ushort)(1000 * 3 + 300 * 2)), snap.HeightAt(x, z), 0.01f);

        // Deep inside the fine window (more than the band from its edge): its own value.
        var (wx, wz) = fine.WorldOf(cols / 2, cols / 2);
        Assert.Equal(WorldLayout.RawToHeight(20000), snap.HeightAt((float)wx, (float)wz), 0.01f);
    }
}
