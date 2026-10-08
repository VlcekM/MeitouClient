using Meitou.Rendering;

namespace Meitou.Tests.Rendering;

/// <summary>The river map's CPU bake on synthetic heights (docs/viewer.md "Meitou water"); no game and no GPU.</summary>
public class RiverFlowTests
{
    const int N = 160;
    const ushort Level = 30000;

    /// <summary>Corners: wet (0) where <paramref name="wet"/> says so, land elsewhere at <paramref name="land"/>; a texel is wet when all four corners are.</summary>
    static byte[] Bake(Func<int, int, bool> wet, Func<int, int, int> land)
    {
        var h = new ushort[(N + 1) * (N + 1)];
        for (int y = 0; y <= N; y++)
            for (int x = 0; x <= N; x++)
                h[y * (N + 1) + x] = wet(x, y) ? (ushort)0 : (ushort)land(x, y);
        return RiverFlowBake.Bake(h, N, Level);
    }

    static (float X, float Z, float Width, float Weight) At(byte[] map, int x, int y)
    {
        int o = (y * N + x) * 4;
        return (map[o] / 255f * 2 - 1, map[o + 1] / 255f * 2 - 1, map[o + 2] / 255f * 8, map[o + 3] / 255f);
    }

    static int FallsEast(int x, int y) => 55000 - x * 60;
    static int FallsWest(int x, int y) => 40000 + x * 60;

    // A river along y = 80..82 (two texels wide) from x = 10 to x = 150, a blob pond, and land elsewhere.
    static bool World(int x, int y) => (y >= 80 && y <= 82 && x >= 10 && x <= 150) || (x >= 40 && x <= 52 && y >= 20 && y <= 32);

    [Fact]
    public void River_runs_down_the_valley()
    {
        var map = Bake(World, FallsEast);
        for (int x = 40; x <= 120; x += 20)
        {
            var (fx, fz, width, weight) = At(map, x, 80);
            Assert.True(fx > 0.9f && Math.Abs(fz) < 0.3f, $"flow at x={x}: {fx}, {fz}");
            Assert.True(weight > 0.8f, $"weight at x={x}: {weight}");
            Assert.InRange(width, 0.9f, 3.1f);
        }
    }

    [Fact]
    public void Flow_reverses_with_the_valley()
    {
        var (fx, _, _, weight) = At(Bake(World, FallsWest), 80, 80);
        Assert.True(fx < -0.9f && weight > 0.8f, $"{fx} {weight}");
    }

    [Fact]
    public void A_diagonal_river_runs_along_its_axis()
    {
        var map = Bake((x, y) => Math.Abs((x - 20) - (y - 20)) <= 1 && x >= 20 && x < 140, (x, y) => 55000 - (x + y) * 40);
        var (fx, fz, _, weight) = At(map, 80, 80);
        Assert.True(weight > 0.5f && fx > 0.6f && fz > 0.6f, $"{fx} {fz} {weight}");
    }

    [Fact]
    public void Level_ground_ponds_and_open_water_have_no_flow()
    {
        Assert.Equal(0f, At(Bake(World, (x, y) => 45000), 80, 80).Weight);   // no fall: still water
        var map = Bake(World, FallsEast);
        Assert.Equal(0f, At(map, 46, 26).Weight);   // the blob pond: not elongated
        Assert.Equal(0f, At(map, 5, 5).Weight);     // land away from any river
        // A lake 30 texels wide.
        var lake = Bake((x, y) => x >= 60 && x <= 100 && y >= 60 && y <= 100, FallsEast);
        Assert.Equal(0f, At(lake, 80, 80).Weight);
    }

    [Fact]
    public void Directions_turn_smoothly_and_a_crossing_is_still()
    {
        // Two rivers crossing at (80, 80): at the crossing the fitted axes fan out every way; the water there is still (a pinwheel before).
        var map = Bake((x, y) => (y >= 79 && y <= 81 && x >= 10 && x <= 150) || (x >= 79 && x <= 81 && y >= 10 && y <= 150), (x, y) => 55000 - (x + y) * 40);
        Assert.True(At(map, 80, 80).Weight < 0.5f * At(map, 30, 80).Weight, $"crossing {At(map, 80, 80).Weight} vs arm {At(map, 30, 80).Weight}");
        // A river widening into a small pond (an ellipse 20 x 10 texels): where it has weight, neighbouring texels point the same way.
        bool Bulge(int x, int y) => (y >= 79 && y <= 81 && x >= 10 && x <= 150) || ((x - 80) * (x - 80) / 100.0 + (y - 80) * (y - 80) / 25.0 <= 1);
        var pond = Bake(Bulge, FallsEast);
        int bad = 0;
        for (int y = 70; y <= 90; y++)
            for (int x = 65; x <= 95; x++)
                foreach (var (ox, oy) in new[] { (1, 0), (0, 1) })
                {
                    var a = At(pond, x, y);
                    var b = At(pond, x + ox, y + oy);
                    if (a.Weight > 0.3f && b.Weight > 0.3f && a.X * b.X + a.Z * b.Z < 0.8f) bad++;
                }
        Assert.True(bad == 0, $"{bad} neighbouring pairs in the pond turn by more than 37 degrees");
        // Along an arm the direction changes little from texel to texel (no seams).
        var straight = Bake(World, FallsEast);
        for (int x = 20; x < 140; x++)
        {
            var (ax, az, _, _) = At(straight, x, 81);
            var (bx, bz, _, _) = At(straight, x + 1, 81);
            Assert.True(ax * bx + az * bz > 0.95f, $"step at x={x}: ({ax}, {az}) to ({bx}, {bz})");
        }
    }

    [Fact]
    public void Weight_fades_with_the_width()
    {
        // A channel 8 texels wide (half-width 4) is part way to a lake.
        var map = Bake((x, y) => y >= 76 && y <= 84 && x >= 10 && x <= 150, FallsEast);
        float w = At(map, 80, 80).Weight;
        Assert.InRange(w, 0f, 0.9f);
        Assert.True(w < At(Bake(World, FallsEast), 80, 80).Weight);
    }
}

/// <summary>The river map baked from the game's own heights: its directions have no seams or pinwheels (neighbouring texels with weight point
/// about the same way). Needs the install.</summary>
[Slow]
public class RiverFlowWorldTests(Xunit.ITestOutputHelper output)
{
    [Fact]
    public void Real_rivers_turn_smoothly()
    {
        Assert.SkipWhen(InstallData.Install is null, "no Kenshi install");
        var install = InstallData.Install!;
        var flow = Meitou.Data.Textures.TextureLoader.LoadImage(File.ReadAllBytes(Path.Combine(install.DataDirectory, Meitou.Data.World.WorldWater.FlowMap)));
        int n = flow.Width;
        using var heights = Meitou.Data.World.TerrainHeightmap.Open(install);
        var raw = heights.Downsample((heights.Size - 1) / n, out _);
        var map = RiverFlowBake.Bake(raw, n, (ushort)Math.Ceiling(Meitou.Data.World.WorldWater.Height * ushort.MaxValue / Meitou.Data.World.WorldLayout.MaxHeight));
        int pairs = 0, breaks = 0;
        for (int y = 0; y + 1 < n; y++)
            for (int x = 0; x + 1 < n; x++)
            {
                int a = (y * n + x) * 4;
                foreach (int b in new[] { a + 4, a + n * 4 })
                {
                    if (map[a + 3] < 80 || map[b + 3] < 80) continue;   // weight 0.3
                    pairs++;
                    float dot = (map[a] / 127.5f - 1) * (map[b] / 127.5f - 1) + (map[a + 1] / 127.5f - 1) * (map[b + 1] / 127.5f - 1);
                    if (dot < 0.8f) breaks++;
                }
            }
        output.WriteLine($"{pairs} neighbouring pairs with weight, {breaks} turning by more than 37 degrees");
        Assert.True(pairs > 1000, "the world has rivers");
        Assert.True(breaks < pairs / 200, $"{breaks} of {pairs} pairs break");
    }
}
