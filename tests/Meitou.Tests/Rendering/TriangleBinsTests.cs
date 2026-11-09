using Meitou.Game.Rendering.Bench;


namespace Meitou.Tests.Rendering;

/// <summary>
/// The pure parts of the bench's triangle measurements (docs/bench.md "Triangles per pass"): the area bins, the folding of counted samples into
/// triangles, the shader change and the result file.
/// </summary>
public class TriangleBinsTests
{
    [Fact]
    public void FoldEstimatesTrianglesFromTheSamplesTheyShaded()
    {
        // 1000 triangles of 2 px^2 shade about 2000 samples; 5000 sub-pixel ones of 0.25 px^2 hit about 1250; 10 of 400 px^2 shade 4000.
        var counts = new uint[TriangleBins.Fine];
        counts[TriangleBins.FineOf(2.0)] = 2000;
        counts[TriangleBins.FineOf(0.25)] = 1250;
        counts[TriangleBins.FineOf(400)] = 4000;
        var h = TriangleBins.Fold(counts);

        Assert.InRange(h.Triangles[0], 5000 * 0.85, 5000 * 1.15);
        Assert.InRange(h.Triangles[1], 1000 * 0.85, 1000 * 1.15);
        Assert.InRange(h.Triangles[4], 10 * 0.85, 10 * 1.15);
        Assert.InRange(h.SubPixelShare, 0.8, 0.9);
        Assert.Equal(1250, h.Pixels[0]);
        Assert.Equal(2000, h.Pixels[1]);
        Assert.Equal(4000, h.Pixels[4]);
        Assert.Throws<ArgumentException>(() => TriangleBins.Fold(new uint[3]));
    }
}
