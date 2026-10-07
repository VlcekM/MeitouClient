using Meitou.Rendering;
using Meitou.Rendering.Impostors;

namespace Meitou.Tests.Rendering;

/// <summary>The impostor atlas budget rule and the far-mip arithmetic (docs/impostors.md section 10): pure functions, no GPU.</summary>
public class ImpostorBudgetTests
{
    const long Gb = 1L << 30;

    [Theory]
    [InlineData(2, 163.84)]
    [InlineData(4, 327.68)]
    [InlineData(8, 655.36)]
    [InlineData(12, 983.04)]
    [InlineData(16, 1024)]       // 8% would be 1311 MB: the cap
    [InlineData(24, 1024)]
    public void A_discrete_card_gets_eight_percent_up_to_a_gigabyte(double gigabytes, double expectedMb) =>
        Assert.Equal(expectedMb, ImpostorBudget.DefaultMb((long)(gigabytes * Gb), integrated: false), 2);

    [Theory]
    [InlineData(2, 102.4)]
    [InlineData(4, 204.8)]
    [InlineData(8, 256)]         // 5% would be 410 MB: the integrated cap
    [InlineData(12, 256)]
    [InlineData(16, 256)]
    public void An_integrated_gpu_gets_five_percent_up_to_256_megabytes(double gigabytes, double expectedMb) =>
        Assert.Equal(expectedMb, ImpostorBudget.DefaultMb((long)(gigabytes * Gb), integrated: true), 2);

    [Fact]
    public void A_small_budget_is_never_mostly_atlases()
    {
        foreach (bool integrated in new[] { false, true })
            for (double mb = 64; mb <= 32768; mb *= 1.3)
            {
                double value = ImpostorBudget.DefaultMb((long)(mb * 1048576), integrated);
                Assert.True(value <= mb * ImpostorBudget.MaxShare + 1e-3, $"{mb} MB: {value}");
                Assert.True(value >= Math.Min(ImpostorBudget.FloorMb, mb * ImpostorBudget.MaxShare) - 1e-3, $"{mb} MB: {value}");
            }
        // The floor applies to a card that reports 512 MB, but never beyond 12% of it.
        Assert.Equal(48, ImpostorBudget.DefaultMb(512L << 20, false), 6);
        Assert.Equal(30.72, ImpostorBudget.DefaultMb(256L << 20, false), 6);
    }

    [Fact]
    public void An_unknown_budget_is_the_old_fixed_one_and_an_override_wins_with_pressure_still_applying()
    {
        Assert.Equal(192, ImpostorBudget.DefaultMb(0, false));
        Assert.Equal(192, ImpostorBudget.LimitMb(null, 0, false, false));
        Assert.Equal(2048, ImpostorBudget.LimitMb(2048, 12 * Gb, false, false));
        Assert.Equal(1536, ImpostorBudget.LimitMb(2048, 12 * Gb, false, true), 6);
        Assert.Equal(ImpostorBudget.DefaultMb(8 * Gb, false) * 0.75, ImpostorBudget.LimitMb(null, 8 * Gb, false, true), 6);
    }

    [Fact]
    public void Far_mips_drop_a_level_for_every_doubling_of_the_distance_and_keep_the_transitions_level_at_the_transition()
    {
        // BushTree01-like: radius 626, 256 pixel frames, 7 levels.
        float radius = 626;
        int atTransition = FoliageRenderer.SkipFor(256, 7, radius, 4000);
        Assert.Equal(0, atTransition);   // 361 pixels at 4000 units: the largest level
        int previous = atTransition;
        for (float distance = 4000; distance <= 1024000; distance *= 2)
        {
            int skip = FoliageRenderer.SkipFor(256, 7, radius, distance);
            Assert.True(skip >= previous && skip <= previous + 1, $"{distance}: {previous} -> {skip}");
            previous = skip;
        }
        Assert.Equal(6, previous);   // never past the smallest level (4 x 4 pixels a frame)
        Assert.Equal(1, FoliageRenderer.SkipFor(256, 7, radius, 16000));
    }

    [Theory]
    [InlineData(256, 12)]
    [InlineData(128, 12)]
    [InlineData(64, 12)]
    [InlineData(32, 12)]
    [InlineData(32, 8)]
    [InlineData(16, 12)]
    public void The_planned_bytes_are_what_the_textures_take(int frame, int grid)
    {
        int levels = System.Numerics.BitOperations.Log2((uint)frame) - 1;
        ImpostorTexture Map(ImpostorMap map, ImpostorEncoding encoding) => new()
        {
            Map = map, Encoding = encoding,
            Levels = Enumerable.Range(0, levels).Select(l => new byte[ImpostorTexture.LevelBytes(encoding, (grid * frame) >> l)]).ToArray(),
        };
        var atlas = new ImpostorAtlas
        {
            Grid = grid, FramePixels = frame, Centre = default, Radius = 1,
            Textures = [Map(ImpostorMap.Albedo, ImpostorEncoding.Bc1), Map(ImpostorMap.Normal, ImpostorEncoding.Bc5)],
        };
        for (int skip = 0; skip < levels; skip++)
            Assert.Equal(ImpostorTextures.BytesFor(atlas, skip, skip + 1), FoliageRenderer.AtlasBytes(frame, grid, skip));
    }
}
