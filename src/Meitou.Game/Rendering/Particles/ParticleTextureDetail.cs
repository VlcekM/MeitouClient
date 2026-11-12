using Meitou.Data.Textures;

namespace Meitou.Game.Rendering.Particles;

/// <summary>
/// How many pixels across a particle texture needs on screen to keep its detail (<see cref="ParticleRenderer"/>'s low-resolution targets): its side
/// divided by the largest reduction (2 to 32) whose premultiplied brightness, box-reduced and scaled back up bilinearly, stays within
/// <see cref="Tolerance"/> of the texture's. A soft dust blob (<c>Sand-Wisp.png</c>) keeps it at a 32nd of its side, 16 pixels; a crisp twig
/// (<c>Ticker01.png</c>) at none (docs/render-post.md).
/// </summary>
static class ParticleTextureDetail
{
    /// <summary>
    /// The share of the texture's premultiplied brightness a reduction may change.
    /// </summary>
    const float Tolerance = 0.1f;
    /// <summary>
    /// A bigger texture is box-reduced to this side first (the measure costs a few passes over it, on a decode worker).
    /// </summary>
    const int MaxSide = 512;

    /// <summary>
    /// The pixels across <paramref name="image"/> needs on screen: its side over the largest reduction that keeps its detail (1 when it has no
    /// brightness).
    /// </summary>
    public static float PixelsNeeded(RgbaImage image)
    {
        var (v, w, h) = Coverage(image);
        double total = 0;
        foreach (float x in v) total += x;
        if (total <= 0) return 1;

        int kept = 1;
        for (int f = 2; f <= 32 && w / f >= 1 && h / f >= 1; f *= 2)
        {
            if (Error(v, w, h, f) / total > Tolerance) break;
            kept = f;
        }
        return Math.Max(w, h) / (float)kept;
    }

    /// <summary>
    /// Each texel's alpha times its brightest channel (what an alpha sprite covers, and what an additive one, whose alpha is often 1, adds), at most
    /// <see cref="MaxSide"/> across.
    /// </summary>
    static (float[] V, int W, int H) Coverage(RgbaImage image)
    {
        var v = new float[image.Width * image.Height];
        var p = image.Pixels;
        for (int i = 0; i < v.Length; i++)
        {
            int max = Math.Max(p[4 * i], Math.Max(p[4 * i + 1], p[4 * i + 2]));
            v[i] = max * p[4 * i + 3] / (255f * 255f);
        }

        int step = 1;
        while (Math.Max(image.Width, image.Height) / step > MaxSide) step *= 2;
        return step == 1 ? (v, image.Width, image.Height) : Box(v, image.Width, image.Height, step);
    }

    /// <summary>
    /// The summed difference between <paramref name="v"/> and itself reduced by <paramref name="f"/> and scaled back up.
    /// </summary>
    static double Error(float[] v, int w, int h, int f)
    {
        var (small, sw, sh) = Box(v, w, h, f);
        double error = 0;
        for (int y = 0; y < h; y++)
        {
            float sy = Math.Clamp((y + 0.5f) / f - 0.5f, 0, sh - 1);
            int y0 = (int)sy, y1 = Math.Min(y0 + 1, sh - 1);
            float ty = sy - y0;
            for (int x = 0; x < w; x++)
            {
                float sx = Math.Clamp((x + 0.5f) / f - 0.5f, 0, sw - 1);
                int x0 = (int)sx, x1 = Math.Min(x0 + 1, sw - 1);
                float tx = sx - x0;
                float top = small[y0 * sw + x0] + (small[y0 * sw + x1] - small[y0 * sw + x0]) * tx;
                float bottom = small[y1 * sw + x0] + (small[y1 * sw + x1] - small[y1 * sw + x0]) * tx;
                error += Math.Abs(v[y * w + x] - (top + (bottom - top) * ty));
            }
        }
        return error;
    }

    /// <summary>
    /// The mean of each <paramref name="f"/> × <paramref name="f"/> block (the last row and column of blocks take the remainder).
    /// </summary>
    static (float[] V, int W, int H) Box(float[] v, int w, int h, int f)
    {
        int sw = Math.Max(1, w / f), sh = Math.Max(1, h / f);
        var sum = new float[sw * sh];
        var count = new int[sw * sh];
        for (int y = 0; y < h; y++)
        {
            int row = Math.Min(y / f, sh - 1) * sw;
            for (int x = 0; x < w; x++)
            {
                int i = row + Math.Min(x / f, sw - 1);
                sum[i] += v[y * w + x];
                count[i]++;
            }
        }
        for (int i = 0; i < sum.Length; i++) sum[i] /= count[i];
        return (sum, sw, sh);
    }
}