using Meitou.Data.Textures;

namespace Meitou.Tests.Textures;

public class BlockCompressionTests
{
    static byte[] Gradient(int w, int h, bool alpha)
    {
        var px = new byte[w * h * 4];

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 4;
                px[o] = (byte)(x * 255 / Math.Max(w - 1, 1));
                px[o + 1] = (byte)((x + y) * 200 / Math.Max(w + h - 2, 1) + 20);
                px[o + 2] = (byte)((x + y) * 255 / Math.Max(w + h - 2, 1) / 2 + 40);
                px[o + 3] = alpha ? (byte)(255 - x * 200 / Math.Max(w - 1, 1)) : (byte)255;
            }
        }

        return px;
    }

    static byte[] Decode(DdsFormat format, byte[] blocks, int w, int h)
    {
        var rgba = new byte[w * h * 4];
        DdsDecoder.Decode(format, null, blocks, w, h, rgba);
        return rgba;
    }

    static double MeanError(byte[] a, byte[] b, int channels)
    {
        double sum = 0;
        for (int i = 0; i < a.Length; i += 4)
            for (int c = 0; c < channels; c++) sum += Math.Abs(a[i + c] - b[i + c]);

        return sum / (a.Length / 4) / channels;
    }

    [Fact]
    public void Encoded_blocks_decode_close_to_the_source()
    {
        var src = Gradient(32, 20, alpha: true);
        var bc3 = BlockCompression.EncodeBc3(src, 32, 20);
        Assert.Equal(BlockCompression.Bc3Size(32, 20), bc3.Length);
        Assert.True(MeanError(src, Decode(DdsFormat.Bc3, bc3, 32, 20), 4) < 4);
        var bc1 = BlockCompression.EncodeBc1(src, 32, 20);
        Assert.Equal(BlockCompression.Bc1Size(32, 20), bc1.Length);
        Assert.True(MeanError(src, Decode(DdsFormat.Bc1, bc1, 32, 20), 3) < 4);
    }

    [Fact]
    public void Bc3_to_Bc1_keeps_the_colours_including_reversed_endpoints()
    {
        var src = Gradient(16, 16, alpha: true);
        var bc3 = BlockCompression.EncodeBc3(src, 16, 16);

        // reverse the endpoints of every block (and the indices with them): the same pixels in a block whose first endpoint is the smaller
        var swapped = (byte[])bc3.Clone();

        for (int b = 0; b < swapped.Length; b += 16)
        {
            (swapped[b + 8], swapped[b + 9], swapped[b + 10], swapped[b + 11]) = (bc3[b + 10], bc3[b + 11], bc3[b + 8], bc3[b + 9]);

            for (int i = 0; i < 4; i++)
            {
                int v = bc3[b + 12 + i], m = 0;
                for (int k = 0; k < 4; k++) m |= ((v >> (2 * k) & 3) ^ 1) << (2 * k);

                swapped[b + 12 + i] = (byte)m;
            }
        }

        var expected = Decode(DdsFormat.Bc3, bc3, 16, 16);
        Assert.True(MeanError(expected, Decode(DdsFormat.Bc1, BlockCompression.Bc3ToBc1(bc3), 16, 16), 3) < 0.01);
        Assert.True(MeanError(expected, Decode(DdsFormat.Bc1, BlockCompression.Bc3ToBc1(swapped), 16, 16), 3) < 0.01);
    }
}
