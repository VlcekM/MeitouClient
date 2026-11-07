using Meitou.Data.Textures;

namespace Meitou.Rendering;

/// <summary>
/// How many pixels across a particle texture needs on screen to keep its look (Meitou; docs/render-post.md "Colour, night and particles"): the smallest
/// size, halving from the texture's own, whose box-filtered copy brought back up bilinearly stays within <see cref="Tolerance"/> of it (the mean
/// premultiplied difference over the covered texels). A soft cloud (dust, mist, smoke) keeps its look at 8 to 64 pixels, a sharp-edged splat or a leaf
/// needs hundreds: drawn into a quarter-size target it came back a blurry blotch.
/// </summary>
public static class ParticleTextureDetail
{
    /// <summary>The mean premultiplied difference (0 to 1) a reduced copy may have. The game's soft and sharp textures are a factor of ten or more apart around it.</summary>
    public const float Tolerance = 0.04f;

    /// <summary>The comparison reads at most this many points along each side (the full-size image's texels, evenly spaced).</summary>
    const int Probes = 128;

    /// <summary>The pixels along the longer side <paramref name="image"/> needs (1 to its own size).</summary>
    public static int Pixels(RgbaImage image)
    {
        int w = image.Width, h = image.Height;
        var bytes = image.Pixels;
        float[]? level = null;   // the reduced copy (null: the image itself, read from its bytes)
        int lw = w, lh = h, needed = Math.Max(w, h);
        int stepX = Math.Max(1, w / Probes), stepY = Math.Max(1, h / Probes);
        while (lw > 1 || lh > 1)
        {
            int nw = Math.Max(1, lw / 2), nh = Math.Max(1, lh / 2);
            level = level is null ? HalveBytes(bytes, w, h, nw, nh) : Halve(level, lw, lh, nw, nh);
            (lw, lh) = (nw, nh);
            double sum = 0;
            int covered = 0;
            for (int y = stepY / 2; y < h; y += stepY)
                for (int x = stepX / 2; x < w; x += stepX)
                {
                    int i = (y * w + x) * 4;
                    float a = bytes[i + 3] / 255f;
                    if (a <= 0.02f) continue;
                    float u = (x + 0.5f) / w * lw - 0.5f, v = (y + 0.5f) / h * lh - 0.5f;
                    float worst = 0;
                    for (int c = 0; c < 4; c++) worst = Math.Max(worst, Math.Abs(Bilinear(level, lw, lh, u, v, c) - (c == 3 ? a : bytes[i + c] / 255f * a)));
                    sum += worst;
                    covered++;
                }
            if (covered > 0 && sum / covered > Tolerance) break;
            needed = Math.Max(lw, lh);
        }
        return needed;
    }

    /// <summary>The image's first 2×2 box reduction, premultiplied.</summary>
    static float[] HalveBytes(byte[] p, int w, int h, int nw, int nh)
    {
        var dst = new float[nw * nh * 4];
        for (int y = 0; y < nh; y++)
            for (int x = 0; x < nw; x++)
                for (int dy = 0; dy < 2; dy++)
                    for (int dx = 0; dx < 2; dx++)
                    {
                        int i = (Math.Min(2 * y + dy, h - 1) * w + Math.Min(2 * x + dx, w - 1)) * 4, o = (y * nw + x) * 4;
                        float a = p[i + 3] / 255f * 0.25f;
                        dst[o] += p[i] / 255f * a;
                        dst[o + 1] += p[i + 1] / 255f * a;
                        dst[o + 2] += p[i + 2] / 255f * a;
                        dst[o + 3] += a;
                    }
        return dst;
    }

    /// <summary>A 2×2 box reduction (an odd last row or column folded into its neighbour's box by clamping).</summary>
    static float[] Halve(float[] src, int w, int h, int nw, int nh)
    {
        var dst = new float[nw * nh * 4];
        for (int y = 0; y < nh; y++)
        {
            int y0 = Math.Min(2 * y, h - 1), y1 = Math.Min(2 * y + 1, h - 1);
            for (int x = 0; x < nw; x++)
            {
                int x0 = Math.Min(2 * x, w - 1), x1 = Math.Min(2 * x + 1, w - 1);
                for (int c = 0; c < 4; c++)
                    dst[(y * nw + x) * 4 + c] = 0.25f * (src[(y0 * w + x0) * 4 + c] + src[(y0 * w + x1) * 4 + c] + src[(y1 * w + x0) * 4 + c] + src[(y1 * w + x1) * 4 + c]);
            }
        }
        return dst;
    }

    static float Bilinear(float[] img, int w, int h, float u, float v, int c)
    {
        u = Math.Clamp(u, 0, w - 1);
        v = Math.Clamp(v, 0, h - 1);
        int x0 = (int)u, y0 = (int)v, x1 = Math.Min(x0 + 1, w - 1), y1 = Math.Min(y0 + 1, h - 1);
        float fx = u - x0, fy = v - y0;
        float top = img[(y0 * w + x0) * 4 + c] * (1 - fx) + img[(y0 * w + x1) * 4 + c] * fx;
        float bottom = img[(y1 * w + x0) * 4 + c] * (1 - fx) + img[(y1 * w + x1) * 4 + c] * fx;
        return top * (1 - fy) + bottom * fy;
    }
}
