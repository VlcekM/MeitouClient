namespace Meitou.Rendering;

/// <summary>
/// The Meitou water's foam pattern (<see cref="WaterRenderer"/>): a tileable lace of bubble rims, baked at load. Two octaves of cellular noise
/// (one jittered point per cell; the rim is where the nearest two points are about as near, F2 − F1 small). R holds the lace (0..1), G a smooth
/// value noise for patches; the shader lets more of the lace through the more foam there is, in clumps where G is high.
/// </summary>
public static class WaterFoam
{
    public const int Size = 256;

    /// <summary>RGBA8 pixels, <see cref="Size"/>², the lace in R, the patches in G (B 0, A 255). Deterministic and tileable.</summary>
    public static byte[] Bake()
    {
        var pixels = new byte[Size * Size * 4];
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                float u = (x + 0.5f) / Size, v = (y + 0.5f) / Size;
                float lace = MathF.Max(Rim(u, v, 22, 0x3A1F), Rim(u, v, 45, 0x91C3) * 0.8f);
                float patches = (0.4f * Value(u, v, 4, 0x77u) + 0.25f * Value(u, v, 8, 0x1234u) + 0.17f * Value(u, v, 16, 0x9E37u)
                    + 0.11f * Value(u, v, 32, 0x51EDu) + 0.07f * Value(u, v, 64, 0xA3C5u));
                int o = (y * Size + x) * 4;
                (pixels[o], pixels[o + 1], pixels[o + 2], pixels[o + 3]) = (Byte(lace), Byte(patches), 0, 255);
            }
        return pixels;
    }

    /// <summary>The rim strength at (u, v) in [0, 1)² of a cellular noise with <paramref name="cells"/>² cells, wrapping.</summary>
    static float Rim(float u, float v, int cells, uint seed)
    {
        float px = u * cells, py = v * cells;
        int cx = (int)MathF.Floor(px), cy = (int)MathF.Floor(py);
        float f1 = float.MaxValue, f2 = float.MaxValue;
        for (int j = -2; j <= 2; j++)
            for (int i = -2; i <= 2; i++)
            {
                int gx = cx + i, gy = cy + j;
                int wx = ((gx % cells) + cells) % cells, wy = ((gy % cells) + cells) % cells;
                uint h = Hash((uint)wx, (uint)wy, seed);
                float jx = (h & 0xFFFF) / 65535f, jy = (h >> 16) / 65535f;
                float dx = gx + 0.1f + 0.8f * jx - px, dy = gy + 0.1f + 0.8f * jy - py;
                float d = MathF.Sqrt(dx * dx + dy * dy);
                if (d < f1) (f2, f1) = (f1, d);
                else if (d < f2) f2 = d;
            }
        float edge = f2 - f1;
        return 1 - Smooth(0.02f, 0.16f, edge);
    }

    static byte Byte(float v) => (byte)Math.Clamp((int)MathF.Round(v * 255), 0, 255);

    /// <summary>A smooth value noise at (u, v) with <paramref name="cells"/>² cells, wrapping: random values at the corners, interpolated.</summary>
    static float Value(float u, float v, int cells, uint seed)
    {
        float px = u * cells, py = v * cells;
        int x0 = (int)MathF.Floor(px), y0 = (int)MathF.Floor(py);
        float fx = Smooth(0, 1, px - x0), fy = Smooth(0, 1, py - y0);
        float C(int x, int y) => (Hash((uint)(((x % cells) + cells) % cells), (uint)(((y % cells) + cells) % cells), seed) & 0xFFFF) / 65535f;
        float top = C(x0, y0) + (C(x0 + 1, y0) - C(x0, y0)) * fx, bottom = C(x0, y0 + 1) + (C(x0 + 1, y0 + 1) - C(x0, y0 + 1)) * fx;
        return top + (bottom - top) * fy;
    }

    static float Smooth(float a, float b, float x)
    {
        float t = Math.Clamp((x - a) / (b - a), 0, 1);
        return t * t * (3 - 2 * t);
    }

    static uint Hash(uint x, uint y, uint seed)
    {
        uint h = x * 0x8DA6B343u ^ y * 0xD8163841u ^ seed * 0xCB1AB31Fu;
        h ^= h >> 13; h *= 0x5BD1E995u; h ^= h >> 15;
        return h;
    }
}
