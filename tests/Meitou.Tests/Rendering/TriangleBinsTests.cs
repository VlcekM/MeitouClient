using Meitou.Rendering;
using Meitou.Rendering.Gpu.Shaders;

namespace Meitou.Tests.Rendering;

/// <summary>The pure parts of the bench's triangle measurements (docs/bench.md "Triangles per pass"): the area bins, the folding of counted samples into triangles, the shader change and the result file.</summary>
public class TriangleBinsTests
{
    [Theory]
    [InlineData(0.0005, 4)]
    [InlineData(1e-9, 0)]
    [InlineData(0.999, 47)]
    [InlineData(1.0, 48)]
    [InlineData(1.18, 48)]
    [InlineData(3.99, 55)]
    [InlineData(4.0, 56)]
    [InlineData(16.0, 64)]
    [InlineData(63.9, 71)]
    [InlineData(64.0, 72)]
    [InlineData(1e12, TriangleBins.Fine - 1)]
    public void FineBinsFollowTheAreaInQuarterOctaves(double area, int fine) => Assert.Equal(fine, TriangleBins.FineOf(area));

    [Fact]
    public void ReportedBinsBreakAtOneFourSixteenAndSixtyFourPixels()
    {
        Assert.Equal(0, TriangleBins.CoarseOf(TriangleBins.FineOf(0.5)));
        Assert.Equal(0, TriangleBins.CoarseOf(TriangleBins.FineOf(0.999)));
        Assert.Equal(1, TriangleBins.CoarseOf(TriangleBins.FineOf(1.0)));
        Assert.Equal(1, TriangleBins.CoarseOf(TriangleBins.FineOf(3.9)));
        Assert.Equal(2, TriangleBins.CoarseOf(TriangleBins.FineOf(4.0)));
        Assert.Equal(3, TriangleBins.CoarseOf(TriangleBins.FineOf(16)));
        Assert.Equal(4, TriangleBins.CoarseOf(TriangleBins.FineOf(64)));
        Assert.Equal(4, TriangleBins.CoarseOf(TriangleBins.FineOf(1e7)));
        Assert.Equal(1.0, TriangleBins.Low(48), 12);
        Assert.Equal(64.0, TriangleBins.Low(72), 9);
    }

    [Fact]
    public void MeanAreaLiesInsideItsBin()
    {
        for (int i = 0; i < TriangleBins.Fine; i++)
            Assert.InRange(TriangleBins.MeanArea(i), TriangleBins.Low(i), TriangleBins.Low(i + 1));
    }

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

    [Fact]
    public void ScaledAndAddedHistogramsKeepTheirBins()
    {
        var a = new SizeHistogram { Triangles = [4, 3, 2, 1, 0], Pixels = [1, 2, 3, 4, 5] };
        var b = a.Scaled(0.5);
        Assert.Equal([2, 1.5, 1, 0.5, 0], b.Triangles);
        b.Add(a);
        Assert.Equal([6, 4.5, 3, 1.5, 0], b.Triangles);
        Assert.Equal(15, a.TotalPixels);
        Assert.Equal(0.4, a.SubPixelShare, 12);
    }

    [Theory]
    [InlineData("terrain", 0)]
    [InlineData("terrain meshes", 1)]
    [InlineData("objects", 2)]
    [InlineData("foliage meshes", 3)]
    [InlineData("foliage grass gpu", 4)]
    [InlineData("impostors", 5)]
    [InlineData("characters", 7)]
    [InlineData("terrain depth", -1)]
    [InlineData("objects depth", -1)]
    [InlineData("foliage grass motion gpu", -1)]
    public void ProgramsAreCountedByName(string name, int category) => Assert.Equal(category, TriangleBins.CategoryOf(name));

    const string Plain = "#version 450\nlayout(location = 0) out vec4 colour;\nvoid main()\n{\n    colour = vec4(1.0);\n}\n";

    [Fact]
    public void InstrumentRunsTheOriginalMainFirstAndCountsAfterIt()
    {
        string text = TriangleBins.Instrument(Plain, 3, 0x0000_1234_ABCD_0000UL)!;
        Assert.StartsWith("#version 450\n#extension GL_EXT_fragment_shader_barycentric : require", text);
        Assert.Contains("layout(early_fragment_tests) in;", text);
        Assert.Contains("void meitou_main()", text);
    }

    [Fact]
    public void InstrumentedTextHasOneMainThatCallsTheOriginal()
    {
        string text = TriangleBins.Instrument(Plain, 3, 0x0000_1234_ABCD_0000UL)!;
        Assert.Equal(2, text.Split("void main()").Length);
        Assert.True(text.IndexOf("meitou_main();", StringComparison.Ordinal) > text.IndexOf("void main()", StringComparison.Ordinal));
        Assert.Contains("uvec2(2882338816u, 4660u)", text);
        Assert.Contains($"b.c[{3 * TriangleBins.Fine} + bin]", text);
    }

    [Fact]
    public void ShadersThatDiscardOrWriteDepthKeepTheirLateDepthTest()
    {
        string discards = Plain.Replace("colour = vec4(1.0);", "if (colour.a < 0.5) discard;");
        Assert.DoesNotContain("early_fragment_tests", TriangleBins.Instrument(discards, 0, 1)!);
        string depth = Plain.Replace("colour = vec4(1.0);", "gl_FragDepth = 0.5;");
        Assert.DoesNotContain("early_fragment_tests", TriangleBins.Instrument(depth, 0, 1)!);
        // The objects and the rocks (categories 1 and 2) take the early test whatever their text says.
        Assert.Contains("early_fragment_tests", TriangleBins.Instrument(discards, 2, 1)!);
        Assert.Contains("early_fragment_tests", TriangleBins.Instrument(discards, 1, 1)!);
        Assert.False(TriangleBins.EarlyTests(discards, 3));
    }

    [Fact]
    public void InstrumentRefusesTextsItCannotChange()
    {
        Assert.Null(TriangleBins.Instrument("#version 450\nvoid helper() {}\n", 0, 1));
        Assert.Null(TriangleBins.Instrument(Plain + "void main() {}\n", 0, 1));
        Assert.Null(TriangleBins.Instrument("#version 330 core\nvoid main() {}\n", 0, 1));
    }

    [Fact]
    public void InstrumentedTextCompilesToSpirv()
    {
        var compiler = new GlslProgramCompiler(new ShaderCompileOptions { UseDiskCache = false, UseMemoryCache = false });
        byte[] spirv = compiler.CompileNative(TriangleBins.Instrument(Plain, 2, 0x1_0000_2000UL)!, fragment: true);
        Assert.True(spirv.Length > 200);
        Assert.Equal(0x07230203u, BitConverter.ToUInt32(spirv, 0));
    }

    [Fact]
    public void TriangleResultsSurviveTheJsonFile()
    {
        var result = new BenchResult();
        var config = new BenchConfig { Label = "A", Frames = 5 };
        config.Passes["terrain"] = new PassStat { Primitives = 1000, ClipOut = 600, FragmentInvocations = 3000, VertexInvocations = 3000, ClipIn = 1000 };
        config.Passes["foliage/fol meshes"] = new PassStat { Primitives = 10, FragmentInvocations = 50, ClipOut = 5 };
        config.Sizes["terrain"] = new SizeHistogram { Triangles = [9, 8, 7, 6, 5], Pixels = [1, 2, 3, 4, 5] };
        result.Configs["A"] = config;
        string path = Path.Combine(Path.GetTempPath(), $"meitou-tris-{Guid.NewGuid():N}.json");
        try
        {
            result.Save(path);
            var back = BenchResult.Load(path).Configs["A"];
            Assert.Equal(["terrain", "foliage/fol meshes"], back.Passes.Keys);
            Assert.Equal(5.0, back.Passes["terrain"].FragmentsPerTriangle);
            Assert.Equal([9, 8, 7, 6, 5], back.Sizes["terrain"].Triangles);
            BenchResult.Compare(path, path, new StringWriter());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ThePassAndSizeTablesPrint()
    {
        var result = new BenchResult();
        result.Meta["renderPixels"] = "2073600";
        result.Meta["passFrames"] = "8";
        var config = new BenchConfig { Label = "A", Counts = new() { ["terrain tris"] = 1 } };
        config.Passes["terrain"] = new PassStat { Primitives = 1_500_000, ClipOut = 1_000_000, FragmentInvocations = 2_073_600 };
        config.Passes["foliage/fol meshes"] = new PassStat { Primitives = 20_000, ClipOut = 10_000, FragmentInvocations = 5000 };
        config.Sizes["terrain"] = new SizeHistogram { Triangles = [900_000, 80_000, 15_000, 4000, 1000], Pixels = [400_000, 200_000, 300_000, 400_000, 773_600] };
        result.Configs["A"] = config;
        var text = new StringWriter();
        result.Print(text);
        string s = text.ToString();
        Assert.Contains("triangles per pass", s);
        Assert.Contains("1.50M", s);
        Assert.Contains("foliage / fol meshes", s);
        Assert.Contains("main view triangles by screen area", s);
        Assert.Contains("90.0", s);   // 900k of 1.0M under a pixel
    }
}
