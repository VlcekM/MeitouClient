namespace Meitou.Game.Rendering.Water;

/// <summary>
/// The Meitou water's foam pattern (<see cref="WaterRenderer"/>), tileable, baked at load. R: a lace of rims from two octaves of cellular noise
/// (F2 − F1 small), for the caustics on the floor only (a single-scale network: as foam it read as a honeycomb). G: a smooth value noise for
/// patches. A: the froth itself, organic and not cellular: five octaves of domain-warped value noise less round holes of widely varying size (four
/// scales, most small, few large, about half the cells empty), so a rising threshold leaves a sheet with holes, then irregular clusters, then dots.
/// B: bubble walls up close, rings of varying size at three scales. The shader lets more of the froth through the more foam there is, in clumps
/// where G is high.
/// </summary>
public static class WaterFoam
{
    /// <summary>
    /// Texels along the pattern's side.
    /// </summary>
    public const int Size = 256;

    /// <summary>
    /// RGBA8 pixels, <see cref="Size"/>², the caustics' lace in R, the patches in G, the bubbles in B, the froth in A. Deterministic and tileable.
    /// </summary>
    public static byte[] Bake()
    {
        var pixels = new byte[Size * Size * 4];
        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                BakePixel(pixels, x, y);
            }
        }

        return pixels;
    }

    /// <summary>
    /// Writes pixel (<paramref name="x"/>, <paramref name="y"/>): the lace, the patches, the bubbles and the froth.
    /// </summary>
    static void BakePixel(byte[] pixels, int x, int y)
    {
        float u = (x + 0.5f) / Size;
        float v = (y + 0.5f) / Size;
        float lace = MathF.Max(Rim(u, v, 22, 0x3A1F), Rim(u, v, 45, 0x91C3) * 0.8f);
        float patches = (0.4f * Value(u, v, 4, 0x77u) + 0.25f * Value(u, v, 8, 0x1234u) + 0.17f * Value(u, v, 16, 0x9E37u)
            + 0.11f * Value(u, v, 32, 0x51EDu) + 0.07f * Value(u, v, 64, 0xA3C5u));
        // The froth's frame wanders (a domain warp), so neither its noise nor its holes line up.
        float wu = u + 0.06f * (Fbm(u, v, 4, 3, 101) - 0.5f);
        float wv = v + 0.06f * (Fbm(u, v, 4, 3, 202) - 0.5f);
        float holes = MathF.Max(MathF.Max(Discs(wu, wv, 12, 11, 0.12f, 0.5f, 0.5f), Discs(wu, wv, 24, 12, 0.12f, 0.5f, 0.55f)),
            MathF.Max(Discs(u, v, 48, 13, 0.12f, 0.48f, 0.6f), Discs(u, v, 96, 14, 0.15f, 0.45f, 0.6f)));
        float froth = Math.Clamp((Fbm(wu, wv, 8, 5, 303) - 0.5f) * 1.8f + 0.55f - 0.55f * holes, 0, 1);
        float bubbles = MathF.Max(Discs(u, v, 40, 21, 0.15f, 0.45f, 0.7f, ring: true),
            MathF.Max(Discs(u, v, 72, 22, 0.15f, 0.45f, 0.8f, ring: true), Discs(u, v, 128, 23, 0.2f, 0.45f, 0.85f, ring: true)));
        int o = (y * Size + x) * 4;
        (pixels[o], pixels[o + 1], pixels[o + 2], pixels[o + 3]) = (Byte(lace), Byte(patches), Byte(bubbles), Byte(froth));
    }

    /// <summary>
    /// The rim strength at (u, v) in [0, 1)² of a cellular noise with <paramref name="cells"/>² cells, wrapping.
    /// </summary>
    static float Rim(float u, float v, int cells, uint seed)
    {
        float px = u * cells;
        float py = v * cells;
        int cx = (int)MathF.Floor(px);
        int cy = (int)MathF.Floor(py);
        float f1 = float.MaxValue;
        float f2 = float.MaxValue;
        for (int j = -2; j <= 2; j++)
        {
            for (int i = -2; i <= 2; i++)
            {
                int gx = cx + i;
                int gy = cy + j;
                int wx = ((gx % cells) + cells) % cells;
                int wy = ((gy % cells) + cells) % cells;
                uint h = Hash((uint)wx, (uint)wy, seed);
                float jx = (h & 0xFFFF) / 65535f;
                float jy = (h >> 16) / 65535f;
                float dx = gx + 0.1f + 0.8f * jx - px;
                float dy = gy + 0.1f + 0.8f * jy - py;
                float d = MathF.Sqrt(dx * dx + dy * dy);
                if (d < f1) (f2, f1) = (f1, d);
                else if (d < f2) f2 = d;
            }
        }

        float edge = f2 - f1;
        return 1 - Smooth(0.02f, 0.16f, edge);
    }

    /// <summary>
    /// Value noise at <paramref name="octaves"/> octaves from <paramref name="cells"/>² cells, each twice as fine and 0.55 times as strong,
    /// normalised to 0..1.
    /// </summary>
    static float Fbm(float u, float v, int cells, int octaves, uint seed)
    {
        float sum = 0, weight = 1, total = 0;
        for (int k = 0; k < octaves; k++)
        {
            sum += weight * Value(u, v, cells << k, seed + 977u * (uint)k);
            total += weight;
            weight *= 0.55f;
        }

        return sum / total;
    }

    /// <summary>
    /// Round spots at (u, v) from <paramref name="cells"/>² cells, wrapping: in a share <paramref name="keep"/> of the cells one spot at a random
    /// place, its radius between <paramref name="min"/> and <paramref name="max"/> cells (squared, so most are small). 1 inside a disc with a soft
    /// edge, or with <paramref name="ring"/> its wall.
    /// </summary>
    static float Discs(float u, float v, int cells, uint seed, float min, float max, float keep, bool ring = false)
    {
        float px = u * cells, py = v * cells;
        int cx = (int)MathF.Floor(px), cy = (int)MathF.Floor(py);
        float result = 0;
        for (int j = -1; j <= 1; j++)
            for (int i = -1; i <= 1; i++)
            {
                int gx = cx + i, gy = cy + j;
                uint wx = (uint)(((gx % cells) + cells) % cells), wy = (uint)(((gy % cells) + cells) % cells);
                uint h = Hash(wx, wy, seed), h2 = Hash(wx, wy, seed + 31);
                if ((h2 >> 16) / 65535f >= keep) continue;

                float size = (h2 & 0xFFFF) / 65535f;
                float r = min + (max - min) * size * size;
                float d = MathF.Sqrt(MathF.Pow(gx + (h & 0xFFFF) / 65535f - px, 2) + MathF.Pow(gy + (h >> 16) / 65535f - py, 2));
                float value = ring
                    ? MathF.Exp(-MathF.Pow((d - r) / (0.08f + 0.15f * r), 2))
                    : Math.Clamp((r - d) / (0.25f * r + 0.04f) + 0.5f, 0, 1);
                result = MathF.Max(result, value);
            }

        return result;
    }

    /// <summary>
    /// A 0..1 value as a byte (rounded, clamped).
    /// </summary>
    static byte Byte(float v) => (byte)Math.Clamp((int)MathF.Round(v * 255), 0, 255);

    /// <summary>
    /// A smooth value noise at (u, v) with <paramref name="cells"/>² cells, wrapping: random values at the corners, interpolated.
    /// </summary>
    static float Value(float u, float v, int cells, uint seed)
    {
        float px = u * cells;
        float py = v * cells;
        int x0 = (int)MathF.Floor(px);
        int y0 = (int)MathF.Floor(py);
        float fx = Smooth(0, 1, px - x0);
        float fy = Smooth(0, 1, py - y0);

        float C(int x, int y) => (Hash((uint)(((x % cells) + cells) % cells), (uint)(((y % cells) + cells) % cells), seed) & 0xFFFF) / 65535f;

        float top = C(x0, y0) + (C(x0 + 1, y0) - C(x0, y0)) * fx;
        float bottom = C(x0, y0 + 1) + (C(x0 + 1, y0 + 1) - C(x0, y0 + 1)) * fx;
        return top + (bottom - top) * fy;
    }

    /// <summary>
    /// Hermite smoothstep of <paramref name="x"/> between <paramref name="a"/> and <paramref name="b"/> (0 to 1).
    /// </summary>
    static float Smooth(float a, float b, float x)
    {
        float t = Math.Clamp((x - a) / (b - a), 0, 1);
        return t * t * (3 - 2 * t);
    }

    /// <summary>
    /// A 32-bit hash of a cell and a seed.
    /// </summary>
    static uint Hash(uint x, uint y, uint seed)
    {
        uint h = x * 0x8DA6B343u ^ y * 0xD8163841u ^ seed * 0xCB1AB31Fu;
        h ^= h >> 13;
        h *= 0x5BD1E995u;
        h ^= h >> 15;
        return h;
    }
}
