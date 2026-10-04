using System.Buffers.Binary;
using System.Numerics;

namespace Meitou.Data.Textures;

/// <summary>
/// CPU decoding of DDS surfaces to RGBA8. Block formats follow the public BC1–BC5 descriptions (Microsoft,
/// "Block Compression (Direct3D 10)"); interpolated values are rounded to nearest, which is within the
/// tolerance the specification allows hardware.
/// </summary>
public static class DdsDecoder
{
    /// <summary>Decodes one surface. A volume surface's depth slices are stacked vertically.</summary>
    public static RgbaImage Decode(DdsFile file, int image = 0, int level = 0)
    {
        var surface = file.Surface(image, level);
        var data = file.SurfaceData(surface);
        int sliceHeight = surface.Height, sliceBytes = surface.Length / surface.Depth;
        var result = new RgbaImage(surface.Width, sliceHeight * surface.Depth);
        for (int z = 0; z < surface.Depth; z++)
        {
            var slice = data.Slice(z * sliceBytes, sliceBytes);
            var target = result.Pixels.AsSpan(z * surface.Width * sliceHeight * 4, surface.Width * sliceHeight * 4);
            Decode(file.Format, file.Masks, slice, surface.Width, sliceHeight, target);
        }
        return result;
    }

    /// <summary>Decodes a 2D surface of <paramref name="width"/>×<paramref name="height"/> into <paramref name="rgba"/> (4 bytes per pixel, rows top to bottom).</summary>
    public static void Decode(DdsFormat format, DdsPixelMasks? masks, ReadOnlySpan<byte> data, int width, int height, Span<byte> rgba)
    {
        if (rgba.Length < width * height * 4) throw new ArgumentException("Target too small.", nameof(rgba));
        if (format == DdsFormat.Uncompressed)
        {
            DecodeUncompressed(masks ?? throw new ArgumentNullException(nameof(masks)), data, width, height, rgba);
            return;
        }

        int blockBytes = format is DdsFormat.Bc1 or DdsFormat.Bc4 ? 8 : 16;
        int blocksX = (width + 3) / 4, blocksY = (height + 3) / 4;
        if (data.Length < blocksX * blocksY * blockBytes) throw new ArgumentException("Surface data too short.", nameof(data));
        Span<byte> block = stackalloc byte[16 * 4];
        Span<byte> channel = stackalloc byte[16];
        for (int by = 0; by < blocksY; by++)
            for (int bx = 0; bx < blocksX; bx++)
            {
                var src = data.Slice((by * blocksX + bx) * blockBytes, blockBytes);
                switch (format)
                {
                    case DdsFormat.Bc1:
                        DecodeColourBlock(src, block, allowOneBitAlpha: true);
                        break;
                    case DdsFormat.Bc2:
                        DecodeColourBlock(src[8..], block, allowOneBitAlpha: false);
                        for (int i = 0; i < 16; i++)
                        {
                            int a4 = (src[i / 2] >> (i % 2 * 4)) & 0xF;
                            block[i * 4 + 3] = (byte)(a4 * 17);
                        }
                        break;
                    case DdsFormat.Bc3:
                        DecodeColourBlock(src[8..], block, allowOneBitAlpha: false);
                        DecodeChannelBlock(src, channel);
                        for (int i = 0; i < 16; i++) block[i * 4 + 3] = channel[i];
                        break;
                    case DdsFormat.Bc4:
                        DecodeChannelBlock(src, channel);
                        for (int i = 0; i < 16; i++)
                        {
                            block[i * 4] = block[i * 4 + 1] = block[i * 4 + 2] = channel[i];
                            block[i * 4 + 3] = 255;
                        }
                        break;
                    case DdsFormat.Bc5:
                        DecodeChannelBlock(src, channel);
                        for (int i = 0; i < 16; i++) block[i * 4] = channel[i];
                        DecodeChannelBlock(src[8..], channel);
                        for (int i = 0; i < 16; i++)
                        {
                            block[i * 4 + 1] = channel[i];
                            block[i * 4 + 2] = 0;
                            block[i * 4 + 3] = 255;
                        }
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(format), format, null);
                }

                // Copy the 4x4 block, clipping at the right and bottom edges.
                for (int y = 0; y < 4; y++)
                {
                    int py = by * 4 + y;
                    if (py >= height) break;
                    int columns = Math.Min(4, width - bx * 4);
                    block.Slice(y * 16, columns * 4).CopyTo(rgba.Slice((py * width + bx * 4) * 4, columns * 4));
                }
            }
    }

    /// <summary>
    /// BC1 colour block: two RGB565 endpoints and 2-bit indices. With <paramref name="allowOneBitAlpha"/> (BC1),
    /// <c>color0 &lt;= color1</c> selects three colours plus transparent black; BC2/BC3 always use four colours.
    /// </summary>
    static void DecodeColourBlock(ReadOnlySpan<byte> src, Span<byte> block, bool allowOneBitAlpha)
    {
        ushort c0 = BinaryPrimitives.ReadUInt16LittleEndian(src);
        ushort c1 = BinaryPrimitives.ReadUInt16LittleEndian(src[2..]);
        uint indices = BinaryPrimitives.ReadUInt32LittleEndian(src[4..]);
        Span<int> palette = stackalloc int[16]; // 4 colours × RGBA
        Expand565(c0, palette[..4]);
        Expand565(c1, palette.Slice(4, 4));
        if (c0 > c1 || !allowOneBitAlpha)
            for (int ch = 0; ch < 3; ch++)
            {
                palette[8 + ch] = (2 * palette[ch] + palette[4 + ch] + 1) / 3;
                palette[12 + ch] = (palette[ch] + 2 * palette[4 + ch] + 1) / 3;
            }
        else
        {
            for (int ch = 0; ch < 3; ch++)
            {
                palette[8 + ch] = (palette[ch] + palette[4 + ch] + 1) / 2;
                palette[12 + ch] = 0;
            }
            palette[15] = 0;
        }
        palette[11] = 255;
        if (c0 > c1 || !allowOneBitAlpha) palette[15] = 255;
        for (int i = 0; i < 16; i++)
        {
            int p = (int)((indices >> (2 * i)) & 3) * 4;
            for (int ch = 0; ch < 4; ch++) block[i * 4 + ch] = (byte)palette[p + ch];
        }
    }

    static void Expand565(ushort c, Span<int> rgba)
    {
        int r = c >> 11, g = (c >> 5) & 0x3F, b = c & 0x1F;
        rgba[0] = (r << 3) | (r >> 2);
        rgba[1] = (g << 2) | (g >> 4);
        rgba[2] = (b << 3) | (b >> 2);
        rgba[3] = 255;
    }

    /// <summary>BC3 alpha / BC4 block: two 8-bit endpoints, 3-bit indices; 8 interpolated values, or 6 plus 0 and 255.</summary>
    static void DecodeChannelBlock(ReadOnlySpan<byte> src, Span<byte> values)
    {
        int a0 = src[0], a1 = src[1];
        Span<int> palette = stackalloc int[8];
        palette[0] = a0;
        palette[1] = a1;
        if (a0 > a1)
            for (int i = 1; i <= 6; i++) palette[i + 1] = ((7 - i) * a0 + i * a1 + 3) / 7;
        else
        {
            for (int i = 1; i <= 4; i++) palette[i + 1] = ((5 - i) * a0 + i * a1 + 2) / 5;
            palette[6] = 0;
            palette[7] = 255;
        }
        ulong bits = 0;
        for (int i = 0; i < 6; i++) bits |= (ulong)src[2 + i] << (8 * i);
        for (int i = 0; i < 16; i++) values[i] = (byte)palette[(int)((bits >> (3 * i)) & 7)];
    }

    static void DecodeUncompressed(DdsPixelMasks masks, ReadOnlySpan<byte> data, int width, int height, Span<byte> rgba)
    {
        int bpp = masks.BytesPerPixel;
        if (data.Length < width * height * bpp) throw new ArgumentException("Surface data too short.", nameof(data));
        var r = Channel.Of(masks.Red);
        var g = Channel.Of(masks.Green);
        var b = Channel.Of(masks.Blue);
        var a = Channel.Of(masks.Alpha);
        for (int i = 0; i < width * height; i++)
        {
            uint v = 0;
            for (int k = 0; k < bpp; k++) v |= (uint)data[i * bpp + k] << (8 * k);
            byte red = r.Extract(v, 0);
            rgba[i * 4] = red;
            rgba[i * 4 + 1] = masks.Luminance ? red : g.Extract(v, 0);
            rgba[i * 4 + 2] = masks.Luminance ? red : b.Extract(v, 0);
            rgba[i * 4 + 3] = a.Extract(v, 255);
        }
    }

    readonly record struct Channel(uint Mask, int Shift, uint Max)
    {
        public static Channel Of(uint mask) =>
            mask == 0 ? default : new Channel(mask, BitOperations.TrailingZeroCount(mask), mask >> BitOperations.TrailingZeroCount(mask));

        /// <summary>The channel scaled to 0–255 (rounded), or <paramref name="absent"/> without a mask.</summary>
        public byte Extract(uint value, byte absent) =>
            Mask == 0 ? absent : (byte)((((value & Mask) >> Shift) * 255 + Max / 2) / Max);
    }
}
