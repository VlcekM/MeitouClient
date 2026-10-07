using System.Numerics;

namespace Meitou.Rendering.Impostors;

/// <summary>
/// The CPU half of a bake (docs/impostors.md, "Filtering"): takes the rendered rows of frames (each frame rendered at 2x the frame size with
/// a hard cut-out and box-filtered to the frame size on the GPU), and per frame
/// <list type="number">
/// <item>undoes the premultiplication: coverage = covered samples / 4; albedo the mean of the covered samples; the normal their normalised sum
/// (stored octahedrally encoded in the frame's basis, so normals facing away from the frame keep their direction); the gloss the coverage-weighted
/// mean over the whole atlas;</item>
/// <item>fills the empty pixels by pull-push from the covered ones, so bilinear filtering and the mips never pull in black or a wrong normal at the
/// silhouette;</item>
/// <item>builds the mip chain within the frame (coverage-weighted averages, so frames never bleed into each other), down to 4 x 4;</item>
/// <item>scales each level's coverage (level 0's too) so the share of pixels at or above the 0.5 cut equals the frame's covered area (thin
/// branches do not vanish: the alpha-test mip correction), then cuts it: a texel is covered or not (BC1's 1-bit alpha).</item>
/// </list>
/// Then <see cref="Finish"/> encodes the levels (BC1 albedo with the cut-out, BC5 normal). Frames are independent, so a row is processed in parallel.
/// </summary>
public sealed class ImpostorAssembler
{
    readonly int grid, frame, levels;
    /// <summary>RGBA8 levels per map: [map][level], each <c>(grid x frame / 2^level)^2</c> pixels, rows bottom first.</summary>
    readonly byte[][][] maps;
    double glossSum, glossWeight;

    public ImpostorAssembler(int grid, int frame, int levels)
    {
        (this.grid, this.frame, this.levels) = (grid, frame, levels);
        maps = new byte[2][][];
        for (int m = 0; m < 2; m++)
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
    /// One row of frames, three RGBA8 pictures (albedo, normal x 0.5 + 0.5, depth + gloss: only the gloss, in green, is used) of <c>grid x frame</c>
    /// by <c>frame</c> pixels, rows bottom first, each the 2 x 2 box average of a picture rendered at twice the size with alpha 1 on covered samples
    /// and 0 elsewhere (the GPU's linear half-size blit): so alpha is the coverage and the colour is premultiplied by it.
    /// </summary>
    public void AddRow(int row, byte[] albedo, byte[] normal, byte[] depth) =>
        Parallel.For(0, grid, i => Frame(i, row, albedo, normal, depth));

    void Frame(int column, int row, byte[] albedo, byte[] normal, byte[] gloss)
    {
        int f = frame, width = grid * f, n = f * f;
        var cov = new float[n];
        var alb = new float[n * 3];
        var nrm = new float[n * 3];
        double glossTotal = 0, glossCover = 0;
        for (int y = 0; y < f; y++)
            for (int x = 0; x < f; x++)
            {
                int p = y * f + x, o = (y * width + column * f + x) * 4;
                if (albedo[o + 3] == 0) continue;
                float c = albedo[o + 3] / 255f, k = 1 / (255f * c);
                var a = Vector3.Min(new Vector3(albedo[o], albedo[o + 1], albedo[o + 2]) * k, Vector3.One);
                var nn = Unit(new Vector3(normal[o], normal[o + 1], normal[o + 2]) * k * 2 - Vector3.One);
                glossTotal += c * MathF.Min(gloss[o + 1] * k, 1);
                glossCover += c;
                cov[p] = c;
                (alb[p * 3], alb[p * 3 + 1], alb[p * 3 + 2]) = (a.X, a.Y, a.Z);
                (nrm[p * 3], nrm[p * 3 + 1], nrm[p * 3 + 2]) = (nn.X, nn.Y, nn.Z);
            }
        lock (this) { glossSum += glossTotal; glossWeight += glossCover; }

        // The frame's covered area (the mean of the samples): every level's coverage, level 0's too, is scaled so that the runtime cut
        // (coverage >= 0.5) keeps that share of its pixels. Without it branches thinner than half a pixel vanish (their 2 x 2 coverage is 0.25).
        float area = 0;
        foreach (float c in cov) area += c;
        float target = area / n;

        Fill(cov, alb, 3, f);
        Fill(cov, nrm, 3, f);
        for (int i = 0; i < n; i++)
        {
            var v = Unit(new Vector3(nrm[i * 3], nrm[i * 3 + 1], nrm[i * 3 + 2]));
            (nrm[i * 3], nrm[i * 3 + 1], nrm[i * 3 + 2]) = (v.X, v.Y, v.Z);
        }

        for (int level = 0; level < levels; level++)
        {
            int size = f >> level;
            if (level > 0) (cov, alb, nrm) = Reduce(cov, alb, nrm, size * 2);
            float scale = CoverageScale(cov, target);
            Store(column, row, level, size, cov, scale, alb, nrm);
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
    static (float[] Cov, float[] Alb, float[] Nrm) Reduce(float[] cov, float[] alb, float[] nrm, int size)
    {
        int h = size / 2;
        var c2 = new float[h * h];
        var a2 = new float[h * h * 3];
        var n2 = new float[h * h * 3];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < h; x++)
            {
                int q = y * h + x;
                float total = 0;
                Vector3 a = default, an = default, n = default, nn = default;
                for (int dy = 0; dy < 2; dy++)
                    for (int dx = 0; dx < 2; dx++)
                    {
                        int p = (2 * y + dy) * size + 2 * x + dx;
                        float w = cov[p];
                        total += w;
                        var av = new Vector3(alb[p * 3], alb[p * 3 + 1], alb[p * 3 + 2]);
                        var nv = new Vector3(nrm[p * 3], nrm[p * 3 + 1], nrm[p * 3 + 2]);
                        a += av * w; an += av;
                        n += nv * w; nn += nv;
                    }
                c2[q] = total / 4;
                if (total > 0) a /= total;
                else { a = an / 4; n = nn; }
                n = Unit(n);
                (a2[q * 3], a2[q * 3 + 1], a2[q * 3 + 2]) = (a.X, a.Y, a.Z);
                (n2[q * 3], n2[q * 3 + 1], n2[q * 3 + 2]) = (n.X, n.Y, n.Z);
            }
        return (c2, a2, n2);
    }

    /// <summary>The factor that makes the share of <paramref name="cov"/> x factor >= 0.5 closest to <paramref name="target"/> (bisection; 1 when nothing is covered).</summary>
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

    void Store(int column, int row, int level, int size, float[] cov, float scale, float[] alb, float[] nrm)
    {
        int atlas = grid * size;
        var a = maps[(int)ImpostorMap.Albedo][level];
        var nm = maps[(int)ImpostorMap.Normal][level];
        static byte B(float v) => (byte)Math.Clamp(MathF.Round(v * 255), 0, 255);
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                int p = y * size + x, o = ((row * size + y) * atlas + column * size + x) * 4;
                // The cut-out is stored as it will be tested (coverage >= 0.5): BC1's alpha has one bit.
                bool covered = cov[p] * scale >= 0.5f;
                a[o] = B(alb[p * 3]); a[o + 1] = B(alb[p * 3 + 1]); a[o + 2] = B(alb[p * 3 + 2]); a[o + 3] = covered ? (byte)255 : (byte)0;
                var e = ImpostorLayout.EncodeNormal(new Vector3(nrm[p * 3], nrm[p * 3 + 1], nrm[p * 3 + 2])) * 0.5f + new Vector2(0.5f);
                nm[o] = B(e.X); nm[o + 1] = B(e.Y); nm[o + 2] = 0; nm[o + 3] = 255;
            }
    }

    /// <summary>The atlas: every level encoded (BC1 albedo with its cut-out, BC5 normal when <paramref name="compress"/>, else RGBA8).</summary>
    public ImpostorAtlas Finish(string name, Vector3 centre, float radius, bool compress = true)
    {
        var encodings = compress ? new[] { ImpostorEncoding.Bc1, ImpostorEncoding.Bc5 } : [ImpostorEncoding.Rgba8, ImpostorEncoding.Rgba8];
        var textures = new ImpostorTexture[2];
        for (int m = 0; m < 2; m++)
        {
            var data = new byte[levels][];
            for (int l = 0; l < levels; l++) data[l] = ImpostorEncoder.Encode(encodings[m], maps[m][l], grid * (frame >> l));
            textures[m] = new ImpostorTexture { Map = (ImpostorMap)m, Encoding = encodings[m], Levels = data };
        }
        float gloss = glossWeight > 0 ? (float)(glossSum / glossWeight) : 0.3f;
        return new ImpostorAtlas { Grid = grid, FramePixels = frame, Centre = centre, Radius = radius, Gloss = Math.Clamp(gloss, 0, 1), Name = name, Textures = textures };
    }
}
