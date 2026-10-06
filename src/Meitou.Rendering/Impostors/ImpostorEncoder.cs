using System.Numerics;
using Meitou.Data.Textures;

namespace Meitou.Rendering.Impostors;

/// <summary>
/// Block compression of the atlas levels: BC3 for the albedo and coverage (<see cref="EncodeBc3"/>), BC5 for the two-channel
/// maps (two BC4 blocks, red then green: the same eight-value encoding as BC3's alpha block). Width and height are multiples of 4.
/// </summary>
public static class ImpostorEncoder
{
    public static byte[] Encode(ImpostorEncoding encoding, byte[] rgba, int size) => encoding switch
    {
        ImpostorEncoding.Rgba8 => rgba,
        ImpostorEncoding.Bc3 => EncodeBc3(rgba, size),
        ImpostorEncoding.Bc5 => EncodeBc5(rgba, size),
        _ => throw new ArgumentOutOfRangeException(nameof(encoding)),
    };

    /// <summary>
    /// RGBA8 pixels as BC3 blocks: the alpha block as BC4; the colour endpoints along the principal axis of the block's covered pixels (all
    /// pixels when none is covered). <see cref="BlockCompression.EncodeBc3"/>'s bounding-box endpoints turn a block of orange leaves and
    /// blue-grey twigs into olive and pink greys (its box corners are neither colour); the impostors' frames are full of such blocks.
    /// </summary>
    public static byte[] EncodeBc3(byte[] rgba, int size)
    {
        int blocks = size / 4;
        var result = new byte[blocks * blocks * 16];
        Parallel.For(0, blocks, by =>
        {
            Span<byte> alpha = stackalloc byte[16];
            Span<Vector3> colours = stackalloc Vector3[16];
            for (int bx = 0; bx < blocks; bx++)
            {
                for (int i = 0; i < 16; i++)
                {
                    int o = ((by * 4 + i / 4) * size + bx * 4 + i % 4) * 4;
                    colours[i] = new Vector3(rgba[o], rgba[o + 1], rgba[o + 2]);
                    alpha[i] = rgba[o + 3];
                }
                var dst = result.AsSpan((by * blocks + bx) * 16, 16);
                EncodeBc4Block(alpha, dst[..8]);
                EncodeColourBlock(colours, alpha, dst[8..]);
            }
        });
        return result;
    }

    /// <summary>A BC1 colour block (four-colour mode), endpoints from the principal axis of the pixels with alpha above 0 (or all of them).</summary>
    static void EncodeColourBlock(ReadOnlySpan<Vector3> colours, ReadOnlySpan<byte> alpha, Span<byte> dst)
    {
        bool any = false;
        foreach (byte a in alpha) any |= a > 0;
        var mean = Vector3.Zero;
        int n = 0;
        for (int i = 0; i < 16; i++) if (!any || alpha[i] > 0) { mean += colours[i]; n++; }
        mean /= n;
        // Covariance and its principal axis (power iteration).
        float xx = 0, xy = 0, xz = 0, yy = 0, yz = 0, zz = 0;
        for (int i = 0; i < 16; i++)
        {
            if (any && alpha[i] == 0) continue;
            var d = colours[i] - mean;
            xx += d.X * d.X; xy += d.X * d.Y; xz += d.X * d.Z; yy += d.Y * d.Y; yz += d.Y * d.Z; zz += d.Z * d.Z;
        }
        var axis = new Vector3(xx + xy + xz, xy + yy + yz, xz + yz + zz);
        if (axis.LengthSquared() < 1e-6f) axis = new Vector3(0.577f);
        for (int k = 0; k < 8; k++)
        {
            axis = new Vector3(xx * axis.X + xy * axis.Y + xz * axis.Z, xy * axis.X + yy * axis.Y + yz * axis.Z, xz * axis.X + yz * axis.Y + zz * axis.Z);
            float length = axis.Length();
            if (length < 1e-12f) { axis = new Vector3(0.577f); break; }
            axis /= length;
        }
        float lo = float.MaxValue, hi = float.MinValue;
        for (int i = 0; i < 16; i++)
        {
            if (any && alpha[i] == 0) continue;
            float t = Vector3.Dot(colours[i] - mean, axis);
            lo = MathF.Min(lo, t);
            hi = MathF.Max(hi, t);
        }
        var c0 = Vector3.Clamp(mean + axis * hi, Vector3.Zero, new Vector3(255));
        var c1 = Vector3.Clamp(mean + axis * lo, Vector3.Zero, new Vector3(255));
        ushort e0 = Pack565(c0), e1 = Pack565(c1);
        if (e0 < e1) (e0, e1) = (e1, e0);
        Span<Vector3> palette = stackalloc Vector3[4];
        palette[0] = Expand565(e0);
        palette[1] = Expand565(e1);
        palette[2] = (2 * palette[0] + palette[1]) / 3;
        palette[3] = (palette[0] + 2 * palette[1]) / 3;
        uint indices = 0;
        if (e0 != e1)
            for (int i = 0; i < 16; i++)
            {
                int best = 0;
                float bestDistance = float.MaxValue;
                for (int p = 0; p < 4; p++)
                {
                    float d = Vector3.DistanceSquared(colours[i], palette[p]);
                    if (d < bestDistance) (best, bestDistance) = (p, d);
                }
                indices |= (uint)best << (2 * i);
            }
        dst[0] = (byte)e0; dst[1] = (byte)(e0 >> 8); dst[2] = (byte)e1; dst[3] = (byte)(e1 >> 8);
        for (int i = 0; i < 4; i++) dst[4 + i] = (byte)(indices >> (8 * i));
    }

    static ushort Pack565(Vector3 c) =>
        (ushort)((int)MathF.Round(c.X * 31 / 255) << 11 | (int)MathF.Round(c.Y * 63 / 255) << 5 | (int)MathF.Round(c.Z * 31 / 255));

    static Vector3 Expand565(ushort c)
    {
        int r = c >> 11, g = c >> 5 & 63, b = c & 31;
        return new Vector3(r << 3 | r >> 2, g << 2 | g >> 4, b << 3 | b >> 2);
    }

    /// <summary>The red and green channels of RGBA8 pixels as BC5 blocks.</summary>
    public static byte[] EncodeBc5(byte[] rgba, int size)
    {
        int blocks = size / 4;
        var result = new byte[blocks * blocks * 16];
        Parallel.For(0, blocks, by =>
        {
            Span<byte> values = stackalloc byte[16];
            for (int bx = 0; bx < blocks; bx++)
                for (int channel = 0; channel < 2; channel++)
                {
                    for (int y = 0; y < 4; y++)
                        for (int x = 0; x < 4; x++)
                            values[y * 4 + x] = rgba[((by * 4 + y) * size + bx * 4 + x) * 4 + channel];
                    EncodeBc4Block(values, result.AsSpan((by * blocks + bx) * 16 + channel * 8, 8));
                }
        });
        return result;
    }

    /// <summary>One BC4 block (eight-value mode: endpoints the block's maximum and minimum), indices to the nearest value.</summary>
    public static void EncodeBc4Block(ReadOnlySpan<byte> values, Span<byte> dst)
    {
        int lo = 255, hi = 0;
        foreach (byte v in values) { lo = Math.Min(lo, v); hi = Math.Max(hi, v); }
        dst.Clear();
        dst[0] = (byte)hi;
        dst[1] = (byte)lo;
        if (hi == lo) return;
        Span<int> palette = stackalloc int[8];
        palette[0] = hi;
        palette[1] = lo;
        for (int k = 1; k <= 6; k++) palette[1 + k] = ((7 - k) * hi + k * lo + 3) / 7;
        ulong indices = 0;
        for (int i = 0; i < 16; i++)
        {
            int best = 0, bestDistance = int.MaxValue;
            for (int p = 0; p < 8; p++)
            {
                int d = Math.Abs(values[i] - palette[p]);
                if (d < bestDistance) (best, bestDistance) = (p, d);
            }
            indices |= (ulong)best << (3 * i);
        }
        for (int i = 0; i < 6; i++) dst[2 + i] = (byte)(indices >> (8 * i));
    }

    /// <summary>Decodes one BC4 block to 16 values (tests, the preview's atlas pictures).</summary>
    public static void DecodeBc4Block(ReadOnlySpan<byte> src, Span<byte> values)
    {
        int a0 = src[0], a1 = src[1];
        Span<int> palette = stackalloc int[8];
        palette[0] = a0;
        palette[1] = a1;
        if (a0 > a1)
            for (int k = 1; k <= 6; k++) palette[1 + k] = ((7 - k) * a0 + k * a1 + 3) / 7;
        else
        {
            for (int k = 1; k <= 4; k++) palette[1 + k] = ((5 - k) * a0 + k * a1 + 2) / 5;
            palette[6] = 0;
            palette[7] = 255;
        }
        ulong indices = 0;
        for (int i = 0; i < 6; i++) indices |= (ulong)src[2 + i] << (8 * i);
        for (int i = 0; i < 16; i++) values[i] = (byte)palette[(int)(indices >> (3 * i) & 7)];
    }

    /// <summary>A level back to RGBA8 (BC3 colour decoded with the 4-colour rule BC3 always uses; BC5 into R and G, B 0, A 255).</summary>
    public static byte[] Decode(ImpostorEncoding encoding, byte[] data, int size)
    {
        if (encoding == ImpostorEncoding.Rgba8) return data;
        var rgba = new byte[size * size * 4];
        int blocks = size / 4;
        Span<byte> a = stackalloc byte[16], b = stackalloc byte[16];
        Span<int> colours = stackalloc int[12];
        for (int by = 0; by < blocks; by++)
            for (int bx = 0; bx < blocks; bx++)
            {
                var block = data.AsSpan((by * blocks + bx) * 16, 16);
                DecodeBc4Block(block[..8], a);
                if (encoding == ImpostorEncoding.Bc5) DecodeBc4Block(block[8..], b);
                else
                {
                    var c = block[8..];
                    ushort c0 = (ushort)(c[0] | c[1] << 8), c1 = (ushort)(c[2] | c[3] << 8);
                    Expand(c0, colours[..3]);
                    Expand(c1, colours.Slice(3, 3));
                    for (int k = 0; k < 3; k++)
                    {
                        colours[6 + k] = (2 * colours[k] + colours[3 + k]) / 3;
                        colours[9 + k] = (colours[k] + 2 * colours[3 + k]) / 3;
                    }
                    uint idx = (uint)(c[4] | c[5] << 8 | c[6] << 16 | c[7] << 24);
                    for (int i = 0; i < 16; i++)
                    {
                        int p = (int)(idx >> (2 * i) & 3);
                        int o = (((by * 4 + i / 4) * size) + bx * 4 + i % 4) * 4;
                        rgba[o] = (byte)colours[p * 3];
                        rgba[o + 1] = (byte)colours[p * 3 + 1];
                        rgba[o + 2] = (byte)colours[p * 3 + 2];
                    }
                }
                for (int i = 0; i < 16; i++)
                {
                    int o = (((by * 4 + i / 4) * size) + bx * 4 + i % 4) * 4;
                    if (encoding == ImpostorEncoding.Bc5) { rgba[o] = a[i]; rgba[o + 1] = b[i]; rgba[o + 2] = 0; rgba[o + 3] = 255; }
                    else rgba[o + 3] = a[i];
                }
            }
        return rgba;
    }

    static void Expand(ushort c, Span<int> rgb)
    {
        int r = c >> 11, g = c >> 5 & 63, b = c & 31;
        rgb[0] = r << 3 | r >> 2;
        rgb[1] = g << 2 | g >> 4;
        rgb[2] = b << 3 | b >> 2;
    }
}
