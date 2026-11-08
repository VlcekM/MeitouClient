namespace Meitou.Rendering;

/// <summary>
/// A height map recovered from a tiling normal map (docs/render-terrain.md, "Ground relief"): the normal's slopes integrated in the frequency domain
/// (Frankot–Chellappa), exact for a periodic texture up to a constant. The slopes are read in the terrain shader's tangent frame (tangent along world -X,
/// binormal along +Z, texture u along +X and v along +Z: dh/du = x / z, dh/dv = -y / z), the frame the game lights them in, also where a normal map is
/// authored in another (mirrored or turned; read so, its height would rise where the lighting puts a hollow). A slope field splits into
/// a gradient part (along the frequency: what a surface makes) and a curl part (across it: what no surface makes); noise splits evenly between them, so
/// in each half-octave band (g − c) / (g + c) of their energies is the share of the band that is surface, and the band is kept by that much (the Wiener
/// gain with the noise measured from the curl). The lowest frequencies (under two cycles a tile) are dropped; a tilt weighs the broad shapes down
/// against the detail. The result is scaled to 0..255 between its 1st and 99th percentiles, 255 the top.
/// </summary>
public static class NormalHeight
{
    /// <summary>
    /// An R8 height (rows top first) from an RGBA8 normal map of <paramref name="size"/>² texels (a power of two), and its steepness: its root-mean-square
    /// slope per texel, the height range counted as 1 (<see cref="Steepness"/>). <paramref name="tilt"/>: each frequency also times (cycles a tile / 2)^tilt (0: none).
    /// </summary>
    public static byte[] FromNormals(byte[] rgba, int size, out float steepness, float tilt = 0)
    {
        int count = size * size;
        float[] pRe = new float[count], pIm = new float[count], qRe = new float[count], qIm = new float[count];
        for (int i = 0; i < count; i++)
        {
            float x = rgba[i * 4] / 127.5f - 1, y = rgba[i * 4 + 1] / 127.5f - 1;
            float z = MathF.Sqrt(MathF.Max(1 - x * x - y * y, 0.04f));
            pRe[i] = Math.Clamp(x / z, -5f, 5f);
            qRe[i] = Math.Clamp(-y / z, -5f, 5f);
        }
        Fft2(pRe, pIm, size, false);
        Fft2(qRe, qIm, size, false);

        // Each half-octave band's surface share.
        int bands = (int)Math.Ceiling(2 * Math.Log2(size)) + 1;
        double[] bandG = new double[bands], bandC = new double[bands];
        for (int row = 0; row < size; row++)
        {
            int fy = row <= size / 2 ? row : row - size;
            for (int col = 0; col < size; col++)
            {
                int fx = col <= size / 2 ? col : col - size, i = row * size + col;
                if (fx * fx + fy * fy < 4) continue;
                float r = MathF.Sqrt(fx * fx + fy * fy), ux = fx / r, uy = fy / r;
                double gRe = ux * pRe[i] + uy * qRe[i], gIm = ux * pIm[i] + uy * qIm[i], cRe = -uy * pRe[i] + ux * qRe[i], cIm = -uy * pIm[i] + ux * qIm[i];
                int j = Math.Clamp((int)Math.Round(2 * Math.Log2(r)) - 2, 0, bands - 1);
                bandG[j] += gRe * gRe + gIm * gIm;
                bandC[j] += cRe * cRe + cIm * cIm;
            }
        }
        var gains = new float[bands];
        for (int j = 0; j < bands; j++) gains[j] = bandG[j] + bandC[j] > 0 ? (float)Math.Max(0, (bandG[j] - bandC[j]) / (bandG[j] + bandC[j])) : 0;

        // H = -i G / k, G the gradient part, times the band's share (interpolated between band centres in log frequency); -i (a + ib) = b - ia.
        for (int row = 0; row < size; row++)
        {
            int fy = row <= size / 2 ? row : row - size;
            for (int col = 0; col < size; col++)
            {
                int fx = col <= size / 2 ? col : col - size, i = row * size + col;
                if (fx * fx + fy * fy < 4) { pRe[i] = pIm[i] = 0; continue; }
                float r = MathF.Sqrt(fx * fx + fy * fy), ux = fx / r, uy = fy / r;
                float gRe = ux * pRe[i] + uy * qRe[i], gIm = ux * pIm[i] + uy * qIm[i];
                float t = Math.Clamp(2 * MathF.Log2(r) - 2, 0, bands - 1);
                int j0 = (int)t, j1 = Math.Min(j0 + 1, bands - 1);
                float gain = (gains[j0] + (gains[j1] - gains[j0]) * (t - j0)) * MathF.Pow(r / 2, tilt), k = 2 * MathF.PI * r / size;
                (pRe[i], pIm[i]) = (gIm * gain / k, -gRe * gain / k);
            }
        }
        Fft2(pRe, pIm, size, true);
        var height = Scaled(pRe);
        steepness = Steepness(height, size);
        return height;
    }

    /// <summary>The root-mean-square slope per texel of an R8 height (0..255 counted as 0..1), wrapping, measured over <see cref="SteepnessStep"/> texels: the
    /// height averaged in blocks that wide, central differences between blocks, so a fine texture's texel noise does not count as its steepness.</summary>
    static float Steepness(byte[] height, int size)
    {
        int n = Math.Max(size / SteepnessStep, 2), step = size / n;
        var blocks = new double[n * n];
        for (int v = 0; v < size; v++)
            for (int u = 0; u < size; u++)
                blocks[v / step * n + u / step] += height[v * size + u] / 255.0 / (step * step);
        double sum = 0;
        for (int v = 0; v < n; v++)
            for (int u = 0; u < n; u++)
            {
                double du = (blocks[v * n + (u + 1) % n] - blocks[v * n + (u + n - 1) % n]) / (2.0 * step);
                double dv = (blocks[(v + 1) % n * n + u] - blocks[(v + n - 1) % n * n + u]) / (2.0 * step);
                sum += du * du + dv * dv;
            }
        return (float)Math.Sqrt(sum / (n * n));
    }

    /// <summary>The scale <see cref="Steepness"/> is measured at, in texels (of a 512² height: features about 1/128 of the tile).</summary>
    public const int SteepnessStep = 4;

    /// <summary>0..255 between the 1st and 99th percentiles, so a few deep pits do not flatten the rest.</summary>
    static byte[] Scaled(float[] values)
    {
        int count = values.Length;
        var sorted = (float[])values.Clone();
        Array.Sort(sorted);
        float lo = sorted[count / 100], hi = sorted[count - 1 - count / 100], scale = hi > lo ? 255 / (hi - lo) : 0;
        var bytes = new byte[count];
        for (int i = 0; i < count; i++) bytes[i] = (byte)Math.Clamp((values[i] - lo) * scale, 0, 255);
        return bytes;
    }

    /// <summary>In-place 2D FFT of a square power-of-two grid: rows, then columns (the inverse scaled by 1 / n²).</summary>
    static void Fft2(float[] re, float[] im, int n, bool inverse)
    {
        Parallel.For(0, n, row => Fft(re.AsSpan(row * n, n), im.AsSpan(row * n, n), inverse));
        Parallel.For(0, n, () => (new float[n], new float[n]), (col, _, buffers) =>
        {
            var (r, i) = buffers;
            for (int k = 0; k < n; k++) (r[k], i[k]) = (re[k * n + col], im[k * n + col]);
            Fft(r, i, inverse);
            for (int k = 0; k < n; k++) (re[k * n + col], im[k * n + col]) = (r[k], i[k]);
            return buffers;
        }, _ => { });
        if (!inverse) return;
        float s = 1f / ((float)n * n);
        for (int k = 0; k < re.Length; k++) { re[k] *= s; im[k] *= s; }
    }

    /// <summary>Iterative radix-2 FFT (unscaled).</summary>
    static void Fft(Span<float> re, Span<float> im, bool inverse)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double angle = (inverse ? 2 : -2) * Math.PI / len;
            float wRe = (float)Math.Cos(angle), wIm = (float)Math.Sin(angle);
            for (int i = 0; i < n; i += len)
            {
                float cRe = 1, cIm = 0;
                for (int k = 0; k < len / 2; k++)
                {
                    int a = i + k, b = a + len / 2;
                    float tRe = re[b] * cRe - im[b] * cIm, tIm = re[b] * cIm + im[b] * cRe;
                    (re[b], im[b]) = (re[a] - tRe, im[a] - tIm);
                    re[a] += tRe; im[a] += tIm;
                    (cRe, cIm) = (cRe * wRe - cIm * wIm, cRe * wIm + cIm * wRe);
                }
            }
        }
    }
}
