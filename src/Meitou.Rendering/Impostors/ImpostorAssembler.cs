using System.Numerics;

namespace Meitou.Rendering.Impostors;

/// <summary>
/// The CPU half of a bake (docs/impostors.md, "Filtering"): takes the rendered rows of frames (each frame rendered at 2× the frame size with
/// a hard cut-out), and per frame
/// <list type="number">
/// <item>reduces 2 × 2 samples to a pixel: coverage = covered samples / 4; albedo, depth and gloss the mean of the covered samples; the
/// normal their normalised sum (stored octahedrally encoded in the frame's basis, so normals facing away from the frame keep their direction);</item>
/// <item>fills the empty pixels by pull-push from the covered ones, so bilinear filtering and the mips never pull in black or a wrong normal at the
/// silhouette;</item>
/// <item>builds the mip chain within the frame (coverage-weighted averages, so frames never bleed into each other), down to 4 × 4;</item>
/// <item>scales each mip's coverage so the share of pixels at or above the 0.5 cut equals level 0's (thin branches do not vanish in the
/// distance, the alpha-to-coverage mip correction).</item>
/// </list>
/// Then <see cref="Finish"/> encodes the levels (BC3 albedo, BC5 normal and depth). Frames are independent, so a row is processed in parallel.
/// </summary>
public sealed class ImpostorAssembler
{
    readonly int grid, frame, levels;
    /// <summary>RGBA8 levels per map: [map][level], each <c>(grid × frame / 2^level)²</c> pixels, rows bottom first.</summary>
    readonly byte[][][] maps;

    public ImpostorAssembler(int grid, int frame, int levels)
    {
        (this.grid, this.frame, this.levels) = (grid, frame, levels);
        maps = new byte[3][][];
        for (int m = 0; m < 3; m++)
        {
            maps[m] = new byte[levels][];
            for (int l = 0; l < levels; l++)
            {
                int size = grid * (frame >> l);
                maps[m][l] = new byte[size * size * 4];
            }
        }
    }

    /// <summary>
    /// One row of frames as rendered: three RGBA8 pictures (albedo + coverage, normal, depth + gloss) of <c>grid × 2·frame</c> by <c>2·frame</c>
    /// pixels, rows bottom first; alpha 255 where the mesh covers a sample.
    /// </summary>
    public void AddRow(int row, byte[] albedo, byte[] normal, byte[] depth) =>
        Parallel.For(0, grid, i => Frame(i, row, albedo, normal, depth));

    void Frame(int column, int row, byte[] albedo, byte[] normal, byte[] depth)
    {
        int f = frame, s = 2 * frame, width = grid * s, n = f * f;
        var cov = new float[n];
        var alb = new float[n * 3];
        var nrm = new float[n * 3];
        var dep = new float[n * 2];
        for (int y = 0; y < f; y++)
            for (int x = 0; x < f; x++)
            {
                int p = y * f + x, count = 0;
                Vector3 a = default, nn = default;
                Vector2 d = default;
                for (int dy = 0; dy < 2; dy++)
                    for (int dx = 0; dx < 2; dx++)
                    {
                        int o = ((2 * y + dy) * width + column * s + 2 * x + dx) * 4;
                        if (albedo[o + 3] < 128) continue;
                        count++;
                        a += new Vector3(albedo[o], albedo[o + 1], albedo[o + 2]) / 255f;
                        nn += new Vector3(normal[o], normal[o + 1], normal[o + 2]) / 127.5f - Vector3.One;
                        d += new Vector2(depth[o], depth[o + 1]) / 255f;
                    }
                if (count == 0) continue;
                cov[p] = count / 4f;
                a /= count;
                d /= count;
                nn = Unit(nn);
                (alb[p * 3], alb[p * 3 + 1], alb[p * 3 + 2]) = (a.X, a.Y, a.Z);
                (nrm[p * 3], nrm[p * 3 + 1], nrm[p * 3 + 2]) = (nn.X, nn.Y, nn.Z);
                (dep[p * 2], dep[p * 2 + 1]) = (d.X, d.Y);
            }

        // Level 0's share of pixels the runtime cut (coverage ≥ 0.5) keeps: each mip's coverage is scaled to keep it.
        int kept = 0;
        foreach (float c in cov) if (c >= 0.5f) kept++;
        float target = kept / (float)n;

        Fill(cov, alb, 3, f);
        Fill(cov, nrm, 3, f);
        Fill(cov, dep, 2, f);
        for (int i = 0; i < n; i++)
        {
            var v = Unit(new Vector3(nrm[i * 3], nrm[i * 3 + 1], nrm[i * 3 + 2]));
            (nrm[i * 3], nrm[i * 3 + 1], nrm[i * 3 + 2]) = (v.X, v.Y, v.Z);
        }

        for (int level = 0; level < levels; level++)
        {
            int size = f >> level;
            if (level > 0)
            {
                (cov, alb, nrm, dep) = Reduce(cov, alb, nrm, dep, size * 2);
            }
            float scale = level == 0 ? 1 : CoverageScale(cov, target);
            Store(column, row, level, size, cov, scale, alb, nrm, dep);
        }
    }

    /// <summary>A unit normal (straight at the viewer when the sum is degenerate).</summary>
    static Vector3 Unit(Vector3 v)
    {
        float length = v.Length();
        return length < 1e-6f ? Vector3.UnitZ : v / length;
    }

    /// <summary>Pull-push: empty pixels (coverage 0) take the coverage-weighted average of the covered pixels around them, from the coarsest level that has some.</summary>
    static void Fill(float[] cov, float[] values, int components, int size)
    {
        var levelValues = new List<float[]> { values };
        var levelWeights = new List<float[]> { cov.Select(c => c > 0 ? c : 0f).ToArray() };
        for (int s = size; s > 1; s /= 2)
        {
            var v = levelValues[^1];
            var w = levelWeights[^1];
            int h = s / 2;
            var nv = new float[h * h * components];
            var nw = new float[h * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < h; x++)
                {
                    float total = 0;
                    for (int dy = 0; dy < 2; dy++)
                        for (int dx = 0; dx < 2; dx++)
                        {
                            int p = (2 * y + dy) * s + 2 * x + dx;
                            float wt = w[p];
                            if (wt <= 0) continue;
                            total += wt;
                            for (int c = 0; c < components; c++) nv[(y * h + x) * components + c] += v[p * components + c] * wt;
                        }
                    if (total > 0)
                        for (int c = 0; c < components; c++) nv[(y * h + x) * components + c] /= total;
                    nw[y * h + x] = total;
                }
            levelValues.Add(nv);
            levelWeights.Add(nw);
        }
        // Push: from the coarsest level down, every empty pixel takes its parent's (by then filled) value.
        for (int k = levelValues.Count - 2; k >= 0; k--)
        {
            var v = levelValues[k];
            var w = levelWeights[k];
            var parent = levelValues[k + 1];
            int s = size >> k, half = s / 2;
            for (int y = 0; y < s; y++)
                for (int x = 0; x < s; x++)
                {
                    int p = y * s + x;
                    if (w[p] > 0) continue;
                    int q = (y / 2) * half + x / 2;
                    for (int c = 0; c < components; c++) v[p * components + c] = parent[q * components + c];
                }
        }
    }

    /// <summary>The next mip of a frame: coverage the mean of 4, the rest coverage-weighted (a plain mean where nothing is covered).</summary>
    static (float[] Cov, float[] Alb, float[] Nrm, float[] Dep) Reduce(float[] cov, float[] alb, float[] nrm, float[] dep, int size)
    {
        int h = size / 2;
        var c2 = new float[h * h];
        var a2 = new float[h * h * 3];
        var n2 = new float[h * h * 3];
        var d2 = new float[h * h * 2];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < h; x++)
            {
                int q = y * h + x;
                float total = 0;
                Vector3 a = default, an = default, n = default, nn = default;
                Vector2 d = default, dn = default;
                for (int dy = 0; dy < 2; dy++)
                    for (int dx = 0; dx < 2; dx++)
                    {
                        int p = (2 * y + dy) * size + 2 * x + dx;
                        float w = cov[p];
                        total += w;
                        var av = new Vector3(alb[p * 3], alb[p * 3 + 1], alb[p * 3 + 2]);
                        var nv = new Vector3(nrm[p * 3], nrm[p * 3 + 1], nrm[p * 3 + 2]);
                        var dv = new Vector2(dep[p * 2], dep[p * 2 + 1]);
                        a += av * w; an += av;
                        n += nv * w; nn += nv;
                        d += dv * w; dn += dv;
                    }
                c2[q] = total / 4;
                if (total > 0) { a /= total; d /= total; }
                else { a = an / 4; n = nn; d = dn / 4; }
                n = Unit(n);
                (a2[q * 3], a2[q * 3 + 1], a2[q * 3 + 2]) = (a.X, a.Y, a.Z);
                (n2[q * 3], n2[q * 3 + 1], n2[q * 3 + 2]) = (n.X, n.Y, n.Z);
                (d2[q * 2], d2[q * 2 + 1]) = (d.X, d.Y);
            }
        return (c2, a2, n2, d2);
    }

    /// <summary>The factor that makes the share of <paramref name="cov"/> × factor ≥ 0.5 closest to <paramref name="target"/> (bisection; 1 when nothing is covered).</summary>
    internal static float CoverageScale(float[] cov, float target)
    {
        if (target <= 0) return 1;
        float Share(float k)
        {
            int count = 0;
            foreach (float c in cov) if (c * k >= 0.5f) count++;
            return count / (float)cov.Length;
        }
        float lo = 0.25f, hi = 16f;
        for (int i = 0; i < 20; i++)
        {
            float mid = MathF.Sqrt(lo * hi);
            if (Share(mid) < target) lo = mid; else hi = mid;
        }
        return hi;
    }

    void Store(int column, int row, int level, int size, float[] cov, float scale, float[] alb, float[] nrm, float[] dep)
    {
        int atlas = grid * size;
        var a = maps[(int)ImpostorMap.Albedo][level];
        var nm = maps[(int)ImpostorMap.Normal][level];
        var dm = maps[(int)ImpostorMap.Depth][level];
        static byte B(float v) => (byte)Math.Clamp(MathF.Round(v * 255), 0, 255);
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                int p = y * size + x, o = ((row * size + y) * atlas + column * size + x) * 4;
                a[o] = B(alb[p * 3]); a[o + 1] = B(alb[p * 3 + 1]); a[o + 2] = B(alb[p * 3 + 2]); a[o + 3] = B(MathF.Min(cov[p] * scale, 1));
                var e = ImpostorLayout.EncodeNormal(new Vector3(nrm[p * 3], nrm[p * 3 + 1], nrm[p * 3 + 2])) * 0.5f + new Vector2(0.5f);
                nm[o] = B(e.X); nm[o + 1] = B(e.Y); nm[o + 2] = 0; nm[o + 3] = 255;
                dm[o] = B(dep[p * 2]); dm[o + 1] = B(dep[p * 2 + 1]); dm[o + 2] = 0; dm[o + 3] = 255;
            }
    }

    /// <summary>The atlas: every level encoded (BC3 albedo, BC5 normal and depth when <paramref name="compress"/>, else RGBA8).</summary>
    public ImpostorAtlas Finish(string name, Vector3 centre, float radius, bool compress = true)
    {
        var encodings = compress ? new[] { ImpostorEncoding.Bc3, ImpostorEncoding.Bc5, ImpostorEncoding.Bc5 } : [ImpostorEncoding.Rgba8, ImpostorEncoding.Rgba8, ImpostorEncoding.Rgba8];
        var textures = new ImpostorTexture[3];
        for (int m = 0; m < 3; m++)
        {
            var data = new byte[levels][];
            for (int l = 0; l < levels; l++) data[l] = ImpostorEncoder.Encode(encodings[m], maps[m][l], grid * (frame >> l));
            textures[m] = new ImpostorTexture { Map = (ImpostorMap)m, Encoding = encodings[m], Levels = data };
        }
        return new ImpostorAtlas { Grid = grid, FramePixels = frame, Centre = centre, Radius = radius, Name = name, Textures = textures };
    }
}
