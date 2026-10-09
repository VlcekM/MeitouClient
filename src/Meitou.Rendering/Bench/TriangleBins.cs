using System.Globalization;
using System.Text.RegularExpressions;

namespace Meitou.Rendering;

/// <summary>
/// The pure parts of the bench's triangle size histogram (docs/bench.md "Triangles per pass", <c>--bench-tris</c>): the fine area bins the
/// instrumented fragment shaders count into, folded into the five reported bins (&lt; 1, 1-4, 4-16, 16-64, &gt; 64 px of screen area), and the
/// shader source change that does the counting.
/// </summary>
/// <remarks>
/// The shaders add one to a fine bin per shaded sample, the bin picked by the area of the sample's triangle in pixels (measured in the shader
/// from the screen-space derivatives of the no-perspective barycentric coordinates, which are constant over a triangle). A triangle of area A
/// shades A samples on average over where it falls on the pixel grid, so <c>samples in a bin / the bin's mean area</c> estimates the number of
/// triangles of that size, including the sub-pixel triangles that happen to hit no sample centre (those are counted statistically, not lost).
/// </remarks>
public static partial class TriangleBins
{
    /// <summary>Fine bins per doubling of the area.</summary>
    public const int PerOctave = 4;
    /// <summary>Fine bin 0 starts at 2^MinLog2 px^2 (everything smaller lands in it).</summary>
    public const int MinLog2 = -12;
    /// <summary>Fine bins: 34 octaves, 2^-12 to 2^22 px^2 (a 1080p picture is about 2^21).</summary>
    public const int Fine = 34 * PerOctave;

    /// <summary>The reported bins by screen area.</summary>
    public static readonly string[] Coarse = ["< 1 px", "1-4 px", "4-16 px", "16-64 px", "> 64 px"];

    /// <summary>The first fine bin of each reported bin after the first (areas 1, 4, 16 and 64 px^2; the fine edges fall exactly on them).</summary>
    static readonly int[] CoarseStart = [(0 - MinLog2) * PerOctave, (2 - MinLog2) * PerOctave, (4 - MinLog2) * PerOctave, (6 - MinLog2) * PerOctave];

    /// <summary>The fine bin of a triangle of <paramref name="area"/> px^2 (as the shader's <c>clamp(int(floor(log2(area) * 4)) + 48, 0, 135)</c>).</summary>
    public static int FineOf(double area)
    {
        if (!(area > 0)) return 0;
        int bin = (int)Math.Floor(Math.Log2(area) * PerOctave) - MinLog2 * PerOctave;
        return Math.Clamp(bin, 0, Fine - 1);
    }

    /// <summary>The reported bin (0 to 4) of a fine bin.</summary>
    public static int CoarseOf(int fine)
    {
        int c = 0;
        foreach (int start in CoarseStart) if (fine >= start) c++;
        return c;
    }

    /// <summary>Lower edge of a fine bin, px^2.</summary>
    public static double Low(int fine) => Math.Pow(2, MinLog2 + fine / (double)PerOctave);

    /// <summary>Mean area of the triangles in a fine bin, taken as spread evenly over the logarithm of the area: (hi - lo) / ln(hi / lo).</summary>
    public static double MeanArea(int fine)
    {
        double lo = Low(fine), hi = Low(fine + 1);
        return (hi - lo) / Math.Log(hi / lo);
    }

    /// <summary>Folds the samples counted per fine bin into the reported bins: the estimated triangles and the samples (pixels shaded) themselves.</summary>
    public static SizeHistogram Fold(ReadOnlySpan<uint> samplesPerFine)
    {
        if (samplesPerFine.Length != Fine) throw new ArgumentException($"expected {Fine} fine bins, got {samplesPerFine.Length}");
        var h = new SizeHistogram();
        for (int i = 0; i < Fine; i++)
        {
            if (samplesPerFine[i] == 0) continue;
            int c = CoarseOf(i);
            h.Pixels[c] += samplesPerFine[i];
            h.Triangles[c] += samplesPerFine[i] / MeanArea(i);
        }
        return h;
    }

    // ---- the shader change ----

    /// <summary>The programs counted and their histogram category: by the name the renderer gave the program (the colour programs of the main view; depth-only and motion programs are not).</summary>
    public static readonly string[] Categories = ["terrain", "rocks", "objects", "foliage meshes", "grass", "impostors", "rock impostors", "characters"];

    /// <summary>The category of a program name, or -1 when the program is not counted.</summary>
    public static int CategoryOf(string programName) => programName switch
    {
        "terrain" => 0,
        "terrain meshes" => 1,   // the TERRAIN-mode rocks go through the terrain's mesh path
        "objects" => 2,
        "foliage meshes" => 3,
        "foliage grass" or "foliage grass gpu" => 4,
        "impostors" => 5,
        "impostors of rocks" => 6,
        "characters" => 7,
        _ => -1,
    };

    /// <summary>
    /// <paramref name="fragment"/> (a native fragment shader, GLSL 450) with the counting added: the original <c>main</c> runs first (a
    /// <c>discard</c> in it ends the invocation before the count), then each non-helper invocation adds one to
    /// <c>bins[category * Fine + fine bin]</c> in the buffer at <paramref name="address"/> (buffer device address, baked into the text).
    /// A shader without <c>discard</c> or a depth write (and those of the rocks and the objects, see <see cref="EarlyTests"/>) also gets <c>early_fragment_tests</c>, so occluded samples are not shaded and not counted.
    /// Null when the text has no single <c>void main()</c> or no <c>#version 450</c> line.
    /// </summary>
    public static string? Instrument(string fragment, int category, ulong address)
    {
        var version = VersionLine().Match(fragment);
        var main = MainFunction().Matches(fragment);
        if (!version.Success || main.Count != 1) return null;
        bool earlyTests = EarlyTests(fragment, category);
        string head = "#extension GL_EXT_fragment_shader_barycentric : require\n#extension GL_EXT_buffer_reference : require\n#extension GL_EXT_buffer_reference_uvec2 : require\n"
            + (earlyTests ? "layout(early_fragment_tests) in;\n" : "");
        string text = fragment.Insert(version.Index + version.Length, head);
        text = MainFunction().Replace(text, "void meitou_main()", 1);
        var c = CultureInfo.InvariantCulture;
        return text + $$"""


            layout(buffer_reference, std430) buffer MeitouBins { uint c[]; };
            void main()
            {
                // Derivatives first, while every lane of the quad is still running.
                vec3 bc = gl_BaryCoordNoPerspEXT;
                vec2 g1 = vec2(dFdxFine(bc.y), dFdyFine(bc.y));
                vec2 g2 = vec2(dFdxFine(bc.z), dFdyFine(bc.z));
                float det = abs(g1.x * g2.y - g1.y * g2.x);
                float area = det > 0.0 ? 0.5 / det : 4194304.0;
                // The original runs after the derivatives (they need the whole quad; a discard in it ends the invocation before the count).
                meitou_main();
                int bin = clamp(int(floor(log2(area) * {{PerOctave}}.0)) + {{-MinLog2 * PerOctave}}, 0, {{Fine - 1}});
                if (!gl_HelperInvocation)
                {
                    MeitouBins b = MeitouBins(uvec2({{(uint)(address & 0xFFFFFFFF)}}u, {{(uint)(address >> 32)}}u));
                    atomicAdd(b.c[{{category * Fine}} + bin], 1u);
                }
            }

            """.Replace("\r\n", "\n");
    }

    /// <summary>Whether <see cref="Instrument"/> forces the depth test before the shader: not when the text can discard or writes depth (it would then write depth for samples it discards), except for the rocks and the objects, whose discard is a rare alpha cut-out on mostly opaque meshes (their hidden samples would otherwise be counted).</summary>
    public static bool EarlyTests(string fragment, int category) =>
        category is 1 or 2 || !fragment.Contains("discard", StringComparison.Ordinal) && !fragment.Contains("gl_FragDepth", StringComparison.Ordinal);

    [GeneratedRegex(@"#version\s+450[^\n]*\n")]
    private static partial Regex VersionLine();

    [GeneratedRegex(@"\bvoid\s+main\s*\(\s*(void\s*)?\)")]
    private static partial Regex MainFunction();
}

/// <summary>One category's triangles by screen area (<see cref="TriangleBins.Coarse"/>) and the samples they shaded.</summary>
public sealed class SizeHistogram
{
    /// <summary>Estimated triangles per frame in each reported bin.</summary>
    public double[] Triangles { get; set; } = new double[TriangleBins.Coarse.Length];
    /// <summary>Samples (pixels) shaded per frame by the triangles of each bin.</summary>
    public double[] Pixels { get; set; } = new double[TriangleBins.Coarse.Length];

    public double TotalTriangles => Triangles.Sum();
    public double TotalPixels => Pixels.Sum();

    /// <summary>Share of the estimated triangles under one pixel (0 to 1; 0 with none).</summary>
    public double SubPixelShare => TotalTriangles > 0 ? Triangles[0] / TotalTriangles : 0;

    /// <summary>This histogram scaled by <paramref name="factor"/> (for the mean over frames).</summary>
    public SizeHistogram Scaled(double factor) => new() { Triangles = [.. Triangles.Select(t => t * factor)], Pixels = [.. Pixels.Select(p => p * factor)] };

    public void Add(SizeHistogram other)
    {
        for (int i = 0; i < Triangles.Length; i++) { Triangles[i] += other.Triangles[i]; Pixels[i] += other.Pixels[i]; }
    }
}

/// <summary>What the pipeline statistics queries counted for one pass of the frame, per frame (docs/bench.md "Triangles per pass").</summary>
public sealed class PassStat
{
    /// <summary>Primitives the input assembler made.</summary>
    public double Primitives { get; set; }
    /// <summary>Vertex shader invocations.</summary>
    public double VertexInvocations { get; set; }
    /// <summary>Primitives that reached the clipper.</summary>
    public double ClipIn { get; set; }
    /// <summary>Primitives the clipper passed on.</summary>
    public double ClipOut { get; set; }
    /// <summary>Fragment shader invocations (the driver may count the helper lanes of partly covered quads).</summary>
    public double FragmentInvocations { get; set; }

    /// <summary>Fragment invocations per primitive that left the clipper (0 with none).</summary>
    public double FragmentsPerTriangle => ClipOut > 0 ? FragmentInvocations / ClipOut : 0;

    public void Add(PassStat other, double weight = 1)
    {
        Primitives += other.Primitives * weight;
        VertexInvocations += other.VertexInvocations * weight;
        ClipIn += other.ClipIn * weight;
        ClipOut += other.ClipOut * weight;
        FragmentInvocations += other.FragmentInvocations * weight;
    }
}
