namespace Meitou.Data.Textures;

/// <summary>
/// Block-compressed (BC1 / BC3) texture data without decoding it: conversions between the two colour layouts and a small
/// encoder. Used where one GPU array must hold textures that were stored in different formats (the terrain layer arrays,
/// docs/render-terrain.md). Block layouts are in docs/formats/dds.md.
/// </summary>
public static class BlockCompression
{
    public static int BlockBytes(DdsFormat format) => format is DdsFormat.Bc1 or DdsFormat.Bc4 ? 8 : 16;

    public static int Bc1Size(int width, int height) => (width + 3) / 4 * ((height + 3) / 4) * 8;
    public static int Bc3Size(int width, int height) => (width + 3) / 4 * ((height + 3) / 4) * 16;

    /// <summary>
    /// BC1 blocks as BC3 blocks with the same pixels. A BC1 block in its 3-colour mode (first endpoint not above the second;
    /// the fourth colour is transparent black) keeps its alpha exactly (alpha 0 where it was transparent) but has no exact
    /// BC3 colour for the midpoint, which gets the nearer of BC3's two interpolated colours.
    /// </summary>
    public static byte[] Bc1ToBc3(ReadOnlySpan<byte> bc1)
    {
        int blocks = bc1.Length / 8;
        var result = new byte[blocks * 16];
        for (int b = 0; b < blocks; b++)
        {
            var src = bc1.Slice(b * 8, 8);
            var dst = result.AsSpan(b * 16, 16);
            ushort c0 = (ushort)(src[0] | src[1] << 8), c1 = (ushort)(src[2] | src[3] << 8);
            dst[0] = 255;   // alpha endpoints 255 and 0: index 0 opaque, index 1 transparent
            dst[1] = c0 > c1 ? (byte)255 : (byte)0;
            if (c0 > c1)
            {
                src.CopyTo(dst[8..]);
                continue;
            }
            uint indices = (uint)(src[4] | src[5] << 8 | src[6] << 16 | src[7] << 24), colour = 0;
            ulong alpha = 0;
            for (int i = 0; i < 16; i++)
            {
                uint v = indices >> (2 * i) & 3, o = v switch { 0 => 0u, 1 => 1u, 2 => 2u, _ => 0u };
                if (v == 3) alpha |= 1ul << (3 * i);
                colour |= o << (2 * i);
            }
            src[..4].CopyTo(dst[8..]);
            for (int i = 0; i < 4; i++) dst[12 + i] = (byte)(colour >> (8 * i));
            for (int i = 0; i < 6; i++) dst[2 + i] = (byte)(alpha >> (8 * i));
        }
        return result;
    }

    /// <summary>
    /// BC3 blocks reduced to their colour half as BC1 blocks (alpha dropped). BC3 always has four colours; a block whose
    /// first endpoint is not above the second would be read in BC1's 3-colour mode, so its endpoints are swapped (and the
    /// indices with them) to keep the colours.
    /// </summary>
    public static byte[] Bc3ToBc1(ReadOnlySpan<byte> bc3)
    {
        int blocks = bc3.Length / 16;
        var result = new byte[blocks * 8];
        for (int b = 0; b < blocks; b++)
        {
            var src = bc3.Slice(b * 16 + 8, 8);
            var dst = result.AsSpan(b * 8, 8);
            ushort c0 = (ushort)(src[0] | src[1] << 8), c1 = (ushort)(src[2] | src[3] << 8);
            if (c0 > c1) { src.CopyTo(dst); continue; }
            uint indices = (uint)(src[4] | src[5] << 8 | src[6] << 16 | src[7] << 24), mapped = 0;
            for (int i = 0; i < 16; i++)
            {
                uint v = indices >> (2 * i) & 3;
                mapped |= (c0 == c1 ? 0u : v ^ 1u) << (2 * i);   // 0<->1 and 2<->3 with the endpoints swapped
            }
            (dst[0], dst[1], dst[2], dst[3]) = (src[2], src[3], src[0], src[1]);
            for (int i = 0; i < 4; i++) dst[4 + i] = (byte)(mapped >> (8 * i));
        }
        return result;
    }

    /// <summary>Encodes RGBA8 pixels as BC1 (alpha ignored, four-colour blocks).</summary>
    public static byte[] EncodeBc1(ReadOnlySpan<byte> rgba, int width, int height) => Encode(rgba, width, height, alpha: false);

    /// <summary>Encodes RGBA8 pixels as BC3.</summary>
    public static byte[] EncodeBc3(ReadOnlySpan<byte> rgba, int width, int height) => Encode(rgba, width, height, alpha: true);

    static byte[] Encode(ReadOnlySpan<byte> rgbaSpan, int width, int height, bool alpha)
    {
        int bw = (width + 3) / 4, bh = (height + 3) / 4, stride = alpha ? 16 : 8;
        var result = new byte[bw * bh * stride];
        var rgba = rgbaSpan.ToArray();
        Parallel.For(0, bh, by =>
        {
            Span<byte> px = stackalloc byte[64];
            for (int bx = 0; bx < bw; bx++)
            {
                for (int y = 0; y < 4; y++)
                    for (int x = 0; x < 4; x++)
                    {
                        int sx = Math.Min(bx * 4 + x, width - 1), sy = Math.Min(by * 4 + y, height - 1);
                        rgba.AsSpan((sy * width + sx) * 4, 4).CopyTo(px.Slice((y * 4 + x) * 4, 4));
                    }
                var dst = result.AsSpan((by * bw + bx) * stride, stride);
                if (alpha) EncodeAlpha(px, dst[..8]);
                EncodeColour(px, dst[(stride - 8)..]);
            }
        });
        return result;
    }

    /// <summary>Endpoints from the colour bounding box (pulled in by 1/16 of its extent), indices to the nearest of the four palette colours.</summary>
    static void EncodeColour(ReadOnlySpan<byte> px, Span<byte> dst)
    {
        Span<int> lo = [255, 255, 255], hi = [0, 0, 0];
        for (int i = 0; i < 16; i++)
            for (int c = 0; c < 3; c++)
            {
                lo[c] = Math.Min(lo[c], px[i * 4 + c]);
                hi[c] = Math.Max(hi[c], px[i * 4 + c]);
            }
        for (int c = 0; c < 3; c++)
        {
            int inset = (hi[c] - lo[c]) / 16;
            hi[c] -= inset;
            lo[c] += inset;
        }
        ushort e0 = Pack565(hi[0], hi[1], hi[2]), e1 = Pack565(lo[0], lo[1], lo[2]);
        if (e0 < e1) (e0, e1) = (e1, e0);
        Span<int> palette = stackalloc int[12];
        Expand565(e0, palette[..3]);
        Expand565(e1, palette.Slice(3, 3));
        for (int c = 0; c < 3; c++)
        {
            palette[6 + c] = (2 * palette[c] + palette[3 + c]) / 3;
            palette[9 + c] = (palette[c] + 2 * palette[3 + c]) / 3;
        }
        uint indices = 0;
        if (e0 != e1)
            for (int i = 0; i < 16; i++)
            {
                int best = 0, bestDist = int.MaxValue;
                for (int p = 0; p < 4; p++)
                {
                    int d = 0;
                    for (int c = 0; c < 3; c++) { int e = px[i * 4 + c] - palette[p * 3 + c]; d += e * e; }
                    if (d < bestDist) (best, bestDist) = (p, d);
                }
                indices |= (uint)best << (2 * i);
            }
        dst[0] = (byte)e0; dst[1] = (byte)(e0 >> 8); dst[2] = (byte)e1; dst[3] = (byte)(e1 >> 8);
        for (int i = 0; i < 4; i++) dst[4 + i] = (byte)(indices >> (8 * i));
    }

    /// <summary>Alpha from the block's minimum and maximum (eight-value mode), indices to the nearest of the eight values.</summary>
    static void EncodeAlpha(ReadOnlySpan<byte> px, Span<byte> dst)
    {
        int lo = 255, hi = 0;
        for (int i = 0; i < 16; i++) { lo = Math.Min(lo, px[i * 4 + 3]); hi = Math.Max(hi, px[i * 4 + 3]); }
        dst.Clear();
        dst[0] = (byte)hi;
        dst[1] = (byte)lo;
        if (hi == lo) return;
        Span<int> values = stackalloc int[8];
        values[0] = hi; values[1] = lo;
        for (int k = 1; k <= 6; k++) values[1 + k] = ((7 - k) * hi + k * lo + 3) / 7;
        ulong indices = 0;
        for (int i = 0; i < 16; i++)
        {
            int best = 0, bestDist = int.MaxValue;
            for (int v = 0; v < 8; v++)
            {
                int d = Math.Abs(px[i * 4 + 3] - values[v]);
                if (d < bestDist) (best, bestDist) = (v, d);
            }
            indices |= (ulong)best << (3 * i);
        }
        for (int i = 0; i < 6; i++) dst[2 + i] = (byte)(indices >> (8 * i));
    }

    static ushort Pack565(int r, int g, int b) => (ushort)((r * 31 + 127) / 255 << 11 | (g * 63 + 127) / 255 << 5 | (b * 31 + 127) / 255);

    static void Expand565(ushort c, Span<int> rgb)
    {
        int r = c >> 11, g = c >> 5 & 63, b = c & 31;
        rgb[0] = r << 3 | r >> 2;
        rgb[1] = g << 2 | g >> 4;
        rgb[2] = b << 3 | b >> 2;
    }
}
