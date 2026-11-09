using Meitou.Game.Rendering.Foliage;
using Meitou.Game.Rendering.Impostors;

namespace Meitou.Tests.Rendering;

/// <summary>
/// The impostor atlas budget rule and the far-mip arithmetic (docs/impostors.md section 10): pure functions, no GPU.
/// </summary>
public class ImpostorBudgetTests
{
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
    [InlineData(16, 12)]
    public void The_planned_bytes_are_what_the_textures_take(int frame, int grid)
    {
        int levels = System.Numerics.BitOperations.Log2((uint)frame) - 1;
        ImpostorTexture Map(ImpostorMap map, ImpostorEncoding encoding) => new()
        {
            Map = map, Encoding = encoding,
            Levels = Enumerable.Range(0,
                    levels).Select(l => (ReadOnlyMemory<byte>)new byte[ImpostorTexture.LevelBytes(encoding, (grid * frame) >> l)]).ToArray(),
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
