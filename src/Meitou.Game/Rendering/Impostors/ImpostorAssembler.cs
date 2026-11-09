using System.Collections.Concurrent;
using System.Numerics;

namespace Meitou.Game.Rendering.Impostors;

/// <summary>
/// The CPU half of a bake (docs/impostors.md, "Filtering"): takes the rendered rows of frames (each frame rendered at 2x the frame size with a hard
/// cut-out and box-filtered to the frame size on the GPU), and per frame
/// <list type="number">
/// <item>undoes the premultiplication: coverage = covered samples / 4; albedo the mean of the covered samples; the normal their normalised sum
/// (stored octahedrally encoded in the frame's basis, so normals facing away from the frame keep their direction); the gloss the coverage-weighted
/// mean over the whole atlas;</item>
/// <item>fills the empty pixels by pull-push from the covered ones, so bilinear filtering and the mips never pull in black or a wrong normal at the
/// silhouette;</item>
/// <item>builds the mip chain within the frame (coverage-weighted averages, so frames never bleed into each other), down to 4 x 4;</item>
/// <item>scales each level's coverage (level 0's too) so the share of pixels at or above the 0.5 cut equals the frame's covered area (thin branches
/// do not vanish: the alpha-test mip correction), then cuts it: a texel is covered or not (BC1's 1-bit alpha).</item>
/// </list>
/// Then <see cref="Finish"/> encodes the levels (BC1 albedo with the cut-out, BC5 normal). Frames are independent, so a row is processed in parallel.
/// The per-frame arrays come from a shared pool (<see cref="Scratch"/>) and the atlas levels are not cleared before they are written: a large atlas
/// used to allocate about 900 MB of short-lived large arrays, and the collections they caused stalled the render thread's frames.
/// </summary>
public sealed class ImpostorAssembler
{
    /// <summary>
    /// The RGBA8 levels of finished bakes, kept for the next bake of the same size (a large atlas is two maps of about 50 MB: allocating them anew
    /// for every bake made the collector stall the render thread).
    /// </summary>
    static readonly ConcurrentDictionary<(int Grid, int Frame, int Levels), ConcurrentBag<byte[][][]>> MapPools = new();

    /// <summary>
    /// The per-frame filtering arrays (<see cref="Scratch"/>) by frame size and level count, shared by the bakes.
    /// </summary>
    static readonly ConcurrentDictionary<(int Frame, int Levels), ConcurrentBag<Scratch>> Pools = new();

    /// <summary>
    /// Frames per atlas side.
    /// </summary>
    readonly int _grid;
    /// <summary>
    /// The frame size in pixels at level 0.
    /// </summary>
    readonly int _frame;
    /// <summary>
    /// The mip levels per frame.
    /// </summary>
    readonly int _levels;

    /// <summary>
    /// RGBA8 levels per map: [map][level], each <c>(grid x frame / 2^level)^2</c> pixels, rows bottom first.
    /// </summary>
    readonly byte[][][] _maps;

    /// <summary>
    /// The coverage-weighted gloss summed over the atlas's frames (see <see cref="_glossWeight"/>).
    /// </summary>
    double _glossSum;
    /// <summary>
    /// The coverage the gloss sum is weighted by; the atlas's gloss is their ratio (0.3 when nothing is covered).
    /// </summary>
    double _glossWeight;

    /// <summary>
    /// An assembler for a <paramref name="grid"/> × <paramref name="grid"/> atlas of <paramref name="frame"/>-pixel frames with
    /// <paramref name="levels"/> levels, its maps taken from the pool when a finished bake of the same size left them.
    /// </summary>
    public ImpostorAssembler(int grid, int frame, int levels)
    {
        (this._grid, this._frame, this._levels) = (grid, frame, levels);
        if (MapPools.GetOrAdd((grid, frame, levels), _ => new()).TryTake(out var pooled))
        {
            _maps = pooled;
            return;
        }

        _maps = new byte[2][][];
        for (int m = 0; m < 2; m++)
        {
            _maps[m] = new byte[levels][];
            for (int l = 0; l < levels; l++)
            {
                int size = grid * (frame >> l);

                // Every texel of every level is written by Store, so the memory need not be cleared (it is up to 100 MB for a large atlas).
                _maps[m][l] = GC.AllocateUninitializedArray<byte>(size * size * 4);
            }
        }
    }

    /// <summary>
    /// Drops the pooled scratch (a few MB per worker for each frame size seen).
    /// </summary>
    public static void ReleasePool()
    {
        Pools.Clear();
        MapPools.Clear();
    }

    /// <summary>
    /// One row of frames, three RGBA8 pictures (albedo, normal x 0.5 + 0.5, depth + gloss: only the gloss, in green, is used) of <c>grid x frame</c>
    /// by <c>frame</c> pixels, rows bottom first, each the 2 x 2 box average of a picture rendered at twice the size with alpha 1 on covered samples
    /// and 0 elsewhere (the GPU's linear half-size blit): so alpha is the coverage and the colour is premultiplied by it.
    /// </summary>
    public unsafe void AddRow(int row, byte[] albedo, byte[] normal, byte[] depth)
    {
        fixed (byte* a = albedo, n = normal, d = depth) AddRow(row, (nint)a, (nint)n, (nint)d);
    }

    /// <summary>
    /// As <see cref="AddRow(int, byte[], byte[], byte[])"/>, reading the pictures from memory (the readback buffer's mapping: no copy).
    /// </summary>
    public unsafe void AddRow(int row, nint albedo, nint normal, nint depth) =>
        Parallel.For(0, _grid, i => Frame(i, row, (byte*)albedo, (byte*)normal, (byte*)depth));

    /// <summary>
    /// The atlas: every level encoded (BC1 albedo with its cut-out, BC5 normal when <paramref name="compress"/>, else RGBA8).
    /// </summary>
    public ImpostorAtlas Finish(string name, Vector3 centre, float radius, bool compress = true)
    {
        var encodings = compress ? new[] { ImpostorEncoding.Bc1, ImpostorEncoding.Bc5 } : [ImpostorEncoding.Rgba8, ImpostorEncoding.Rgba8];
        var textures = new ImpostorTexture[2];
        for (int m = 0; m < 2; m++)
        {
            var data = new ReadOnlyMemory<byte>[_levels];
            for (int l = 0; l < _levels; l++) data[l] = ImpostorEncoder.Encode(encodings[m], _maps[m][l], _grid * (_frame >> l));
            textures[m] = new ImpostorTexture { Map = (ImpostorMap)m, Encoding = encodings[m], Levels = data };
        }

        // The encoded levels are new arrays (the RGBA8 option keeps the maps themselves): the maps go back to the pool.
        if (compress && MapPools.GetOrAdd((_grid, _frame, _levels), _ => new()) is { Count: < 2 } pool) pool.Add(_maps);

        float gloss = _glossWeight > 0 ? (float)(_glossSum / _glossWeight) : 0.3f;
        return new ImpostorAtlas
        {
            Grid = _grid,
            FramePixels = _frame,
            Centre = centre,
            Radius = radius,
            Gloss = Math.Clamp(gloss, 0, 1),
            Name = name,
            Textures = textures,
        };
    }

    /// <summary>
    /// A unit normal (straight at the viewer when the sum is degenerate).
    /// </summary>
    static Vector3 Unit(Vector3 v)
    {
        float length = v.Length();
        return length < 1e-6f ? Vector3.UnitZ : v / length;
    }

    /// <summary>
    /// The factor that makes the share of <paramref name="cov"/> x factor &gt;= 0.5 closest to <paramref name="target"/> (bisection; 1 when nothing
    /// is covered).
    /// </summary>
    internal static float CoverageScale(float[] cov, float target)
    {
        if (target <= 0) return 1;

        float Share(float k)
        {
            int count = 0;
            foreach (float c in cov)
                if (c * k >= 0.5f) count++;

            return count / (float)cov.Length;
        }

        float lo = 0.25f, hi = 16f;
        for (int i = 0; i < 20; i++)
        {
            float mid = MathF.Sqrt(lo * hi);
            if (Share(mid) < target) lo = mid;
            else hi = mid;
        }

        return hi;
    }

    /// <summary>
    /// Pull-push: empty pixels (coverage 0) take the coverage-weighted average of the covered pixels around them, from the coarsest level that has
    /// some.
    /// </summary>
    static void Fill(Scratch scratch, float[] cov, float[] values, int size)
    {
        const int components = 3;
        var w0 = scratch.FillWeight0;
        for (int i = 0; i < cov.Length; i++) w0[i] = cov[i] > 0 ? cov[i] : 0f;

        int count = PullLevels(scratch, size, w0, values);
        PushLevels(scratch, size, count, w0, values, components);
    }

    /// <summary>
    /// Pull: level k (size &gt;&gt; k) from level k - 1; returns how many levels were pulled.
    /// </summary>
    static int PullLevels(Scratch scratch, int size, float[] w0, float[] values)
    {
        const int components = 3;
        int count = 0;
        for (int s = size; s > 1; s /= 2)
        {
            var v = count == 0 ? values : scratch.FillValue[count];
            var w = count == 0 ? w0 : scratch.FillWeight[count];
            int h = s / 2;
            var nv = scratch.FillValue[count + 1];
            var nw = scratch.FillWeight[count + 1];
            Array.Clear(nv, 0, h * h * components);
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < h; x++)
                {
                    float total = 0;
                    for (int dy = 0; dy < 2; dy++)
                    {
                        for (int dx = 0; dx < 2; dx++)
                        {
                            int p = (2 * y + dy) * s + 2 * x + dx;
                            float wt = w[p];
                            if (wt <= 0) continue;

                            total += wt;
                            for (int c = 0; c < components; c++) nv[(y * h + x) * components + c] += v[p * components + c] * wt;
                        }
                    }

                    if (total > 0)
                    {
                        for (int c = 0; c < components; c++) nv[(y * h + x) * components + c] /= total;
                    }

                    nw[y * h + x] = total;
                }
            }

            count++;
        }

        return count;
    }

    /// <summary>
    /// Push: from the coarsest level down, every empty pixel takes its parent's (by then filled) value.
    /// </summary>
    static void PushLevels(Scratch scratch, int size, int count, float[] w0, float[] values, int components)
    {
        for (int k = count - 1; k >= 0; k--)
        {
            var v = k == 0 ? values : scratch.FillValue[k];
            var w = k == 0 ? w0 : scratch.FillWeight[k];
            var parent = scratch.FillValue[k + 1];
            int s = size >> k, half = s / 2;
            for (int y = 0; y < s; y++)
            {
                for (int x = 0; x < s; x++)
                {
                    int p = y * s + x;
                    if (w[p] > 0) continue;

                    int q = (y / 2) * half + x / 2;
                    for (int c = 0; c < components; c++) v[p * components + c] = parent[q * components + c];
                }
            }
        }
    }

    /// <summary>
    /// The next mip of a frame, written to <paramref name="c2"/> / <paramref name="a2"/> / <paramref name="n2"/>: coverage the mean of 4, the rest
    /// coverage-weighted (a plain mean where nothing is covered).
    /// </summary>
    static void Reduce(float[] cov, float[] alb, float[] nrm, int size, float[] c2, float[] a2, float[] n2)
    {
        int h = size / 2;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < h; x++)
            {
                int q = y * h + x;
                float total = 0;
                Vector3 a = default, an = default, n = default, nn = default;
                for (int dy = 0; dy < 2; dy++)
                {
                    for (int dx = 0; dx < 2; dx++)
                    {
                        int p = (2 * y + dy) * size + 2 * x + dx;
                        float w = cov[p];
                        total += w;
                        var av = new Vector3(alb[p * 3], alb[p * 3 + 1], alb[p * 3 + 2]);
                        var nv = new Vector3(nrm[p * 3], nrm[p * 3 + 1], nrm[p * 3 + 2]);
                        a += av * w;
                        an += av;
                        n += nv * w;
                        nn += nv;
                    }
                }

                c2[q] = total / 4;
                if (total > 0)
                {
                    a /= total;
                }
                else
                {
                    a = an / 4;
                    n = nn;
                }

                n = Unit(n);
                (a2[q * 3], a2[q * 3 + 1], a2[q * 3 + 2]) = (a.X, a.Y, a.Z);
                (n2[q * 3], n2[q * 3 + 1], n2[q * 3 + 2]) = (n.X, n.Y, n.Z);
            }
        }
    }

    /// <summary>
    /// Writes one frame's level <paramref name="level"/> (<paramref name="size"/> pixels a side) into the atlas maps at its cell: the albedo with the
    /// cut-out (coverage times <paramref name="scale"/> at least 0.5) in alpha, and the octahedral normal in red and green.
    /// </summary>
    void Store(int column, int row, int level, int size, float[] cov, float scale, float[] alb, float[] nrm)
    {
        int atlas = _grid * size;
        var a = _maps[(int)ImpostorMap.Albedo][level];
        var nm = _maps[(int)ImpostorMap.Normal][level];
        static byte B(float v) => (byte)Math.Clamp(MathF.Round(v * 255), 0, 255);
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int p = y * size + x, o = ((row * size + y) * atlas + column * size + x) * 4;

                // The cut-out is stored as it will be tested (coverage >= 0.5): BC1's alpha has one bit.
                bool covered = cov[p] * scale >= 0.5f;
                a[o] = B(alb[p * 3]);
                a[o + 1] = B(alb[p * 3 + 1]);
                a[o + 2] = B(alb[p * 3 + 2]);
                a[o + 3] = covered ? (byte)255 : (byte)0;
                var e = ImpostorLayout.EncodeNormal(new Vector3(nrm[p * 3], nrm[p * 3 + 1], nrm[p * 3 + 2])) * 0.5f + new Vector2(0.5f);
                nm[o] = B(e.X);
                nm[o + 1] = B(e.Y);
                nm[o + 2] = 0;
                nm[o + 3] = 255;
            }
        }
    }

    /// <summary>
    /// Filters the frame at <paramref name="column"/>, <paramref name="row"/> with arrays from the pool (returned afterwards).
    /// </summary>
    unsafe void Frame(int column, int row, byte* albedo, byte* normal, byte* gloss)
    {
        var pool = Pools.GetOrAdd((_frame, _levels), _ => new());
        if (!pool.TryTake(out var scratch)) scratch = new Scratch(_frame, _levels);
        try
        {
            Frame(scratch, column, row, albedo, normal, gloss);
        }
        finally
        {
            if (pool.Count < Environment.ProcessorCount) pool.Add(scratch);
        }
    }

    /// <summary>
    /// Filters one frame from the rendered row's albedo, normal and gloss bytes: undoes the premultiplication, fills the empty pixels, builds the
    /// levels and stores them.
    /// </summary>
    unsafe void Frame(Scratch scratch, int column, int row, byte* albedo, byte* normal, byte* gloss)
    {
        int f = _frame, width = _grid * f, n = f * f;
        var cov = scratch.Cov;
        var alb = scratch.Alb;
        var nrm = scratch.Nrm;
        Array.Clear(cov);
        Array.Clear(alb);
        Array.Clear(nrm);
        double glossTotal = 0, glossCover = 0;
        for (int y = 0; y < f; y++)
        {
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
        }

        lock (this)
        {
            _glossSum += glossTotal;
            _glossWeight += glossCover;
        }

        // The frame's covered area (the mean of the samples): every level's coverage, level 0's too, is scaled so that the runtime cut (coverage >=
        // 0.5) keeps that share of its pixels. Without it branches thinner than half a pixel vanish (their 2 x 2 coverage is 0.25).
        float area = 0;
        foreach (float c in cov) area += c;

        float target = area / n;
        Fill(scratch, cov, alb, f);
        Fill(scratch, cov, nrm, f);
        for (int i = 0; i < n; i++)
        {
            var v = Unit(new Vector3(nrm[i * 3], nrm[i * 3 + 1], nrm[i * 3 + 2]));
            (nrm[i * 3], nrm[i * 3 + 1], nrm[i * 3 + 2]) = (v.X, v.Y, v.Z);
        }

        for (int level = 0; level < _levels; level++)
        {
            int size = f >> level;
            if (level > 0)
            {
                Reduce(cov, alb, nrm, size * 2, scratch.LevelCov[level], scratch.LevelAlb[level], scratch.LevelNrm[level]);
                (cov, alb, nrm) = (scratch.LevelCov[level], scratch.LevelAlb[level], scratch.LevelNrm[level]);
            }

            float scale = CoverageScale(cov, target);
            Store(column, row, level, size, cov, scale, alb, nrm);
        }
    }

    /// <summary>
    /// The arrays of one frame's filtering, reused from frame to frame (and from bake to bake).
    /// </summary>
    sealed class Scratch
    {
        /// <summary>
        /// Level 0's coverage, albedo (3 a pixel), normal (3 a pixel) and the fill's level-0 weights.
        /// </summary>
        public readonly float[] Cov, Alb, Nrm, FillWeight0;

        /// <summary>
        /// The mip levels 1 and up: coverage, albedo, normal.
        /// </summary>
        public readonly float[][] LevelCov, LevelAlb, LevelNrm;

        /// <summary>
        /// The pull pyramid of <see cref="Fill"/> (levels 1 and up): values (3 components) and weights.
        /// </summary>
        public readonly float[][] FillValue, FillWeight;

        /// <summary>
        /// The arrays for <paramref name="frame"/>-pixel frames with <paramref name="levels"/> levels.
        /// </summary>
        public Scratch(int frame, int levels)
        {
            int n = frame * frame;
            (Cov, Alb, Nrm, FillWeight0) = (new float[n], new float[n * 3], new float[n * 3], new float[n]);
            LevelCov = new float[levels][];
            LevelAlb = new float[levels][];
            LevelNrm = new float[levels][];
            for (int l = 1; l < levels; l++)
            {
                int s = frame >> l;
                (LevelCov[l], LevelAlb[l], LevelNrm[l]) = (new float[s * s], new float[s * s * 3], new float[s * s * 3]);
            }

            int count = int.Log2(frame);
            FillValue = new float[count + 1][];
            FillWeight = new float[count + 1][];
            for (int k = 1; k <= count; k++)
            {
                int s = frame >> k;
                (FillValue[k], FillWeight[k]) = (new float[s * s * 3], new float[s * s]);
            }
        }
    }
}
