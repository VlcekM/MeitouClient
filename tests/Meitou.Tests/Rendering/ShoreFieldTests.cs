using System.Numerics;
using Meitou.Data.World;
using Meitou.Game.Rendering.Water;

namespace Meitou.Tests.Rendering;

/// <summary>
/// The shore distance field's CPU bake on synthetic terrain (docs/render-water.md "Shore distance field"); no game and no GPU.
/// </summary>
public class ShoreFieldTests
{
    const int N = 512;
    const float Texel = 10f;

    static ShoreGrid Bake(Func<float, float, float> h, float cx = 0, float cz = 0) => ShoreBake.Bake(h, cx, cz, N, Texel);

    static (float X, float Z) Centre(ShoreGrid g, int i, int j) => (g.X0 + (i + 0.5f) * g.Texel, g.Z0 + (j + 0.5f) * g.Texel);

    [Theory]
    [InlineData(30.0)]
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
                if (MathF.Abs(expected) > Texel)

                    Assert.True(MathF.Sign(d) == MathF.Sign(expected), $"sign at ({x},{z}): {d} vs {expected}");

                worst = Math.Max(worst, MathF.Abs(d - expected));
                checkedTexels++;
            }

        Assert.True(checkedTexels > 10000);
        Assert.True(worst <= 3f, $"worst error {worst} units");
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
    public void Open_sea_is_exposed_and_a_small_pond_is_not_and_an_islet_has_no_surf_and_an_island_does()
    {
        // Sea for x > 0 (shore at x = 0), a 150-unit pond at (-1500, 0), land otherwise; in the sea a 150-unit islet at (2300, -2300)
        // and a 700-unit island at (1500, 1500).
        float Height(float x, float z)
        {
            float sea = 100f + (-x) * 0.1f;
            float pond = 100f + (MathF.Sqrt((x + 1500) * (x + 1500) + z * z) - 150f) * 0.1f;
            float isletR = MathF.Sqrt((x - 2300) * (x - 2300) + (z + 2300) * (z + 2300)),
                    islandR = MathF.Sqrt((x - 1500) * (x - 1500) + (z - 1500) * (z - 1500));
            float islet = isletR < 200 ? 100f + (150f - isletR) * 0.1f : float.MinValue;   // only near its own shore, not as a far field over the sea
            float island = islandR < 750 ? 100f + (700f - islandR) * 0.1f : float.MinValue;
            return Math.Max(Math.Min(sea, pond), Math.Max(islet, island));
        }
        var g = Bake(Height);

        // Sea side, at least 50 units off the shore and well inside the grid.
        foreach (var (x, z) in new[] { (60f, 0f), (300f, 400f), (1500f, -300f), (2000f, 0f) })

            Assert.True(g.Sample(x, z).Y > 0.95f, $"sea exposure at ({x},{z}) = {g.Sample(x, z).Y}");
        Assert.True(g.Sample(-1500, 0).Y < 0.05f, $"pond exposure {g.Sample(-1500, 0).Y}");
        Assert.True(g.Sample(-1500 + 80, 20).Y < 0.05f);

        foreach (var (x, z) in new[] { (2300f - 200, -2300f), (2300f, -2300f + 220), (2300f - 190, -2300f + 60) })

            Assert.True(g.Sample(x, z).Y < 0.05f, $"islet exposure at ({x},{z}) = {g.Sample(x, z).Y}");

        foreach (var (x, z) in new[] { (1500f + 760, 1500f), (1500f, 1500f - 780), (1500f - 760, 1500f + 100) })

            Assert.True(g.Sample(x, z).Y > 0.95f, $"island exposure at ({x},{z}) = {g.Sample(x, z).Y}");
    }
}
