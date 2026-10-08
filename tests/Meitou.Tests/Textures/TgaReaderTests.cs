using Meitou.Data.Textures;

namespace Meitou.Tests.Textures;

public class TgaReaderTests
{
    static byte[] Tga(byte descriptor, int bits, params byte[] pixelBytes)
    {
        var header = new byte[18];
        header[2] = 2;
        header[12] = 2; header[14] = 2;
        header[16] = (byte)bits;
        header[17] = descriptor;
        return [.. header, .. pixelBytes];
    }

    [Fact]
    public void Bottom_left_origin_rows_are_flipped_and_colours_are_swapped_from_BGR()
    {
        // File order is the bottom row first: (red, green) then (blue, white); stored as B, G, R.
        var file = Tga(0, 24, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255, 255);
        var image = TgaReader.Read(file);
        Assert.Equal((2, 2), (image.Width, image.Height));
        Assert.Equal((255, 0, 0, 255), image[0, 1]);   // red is the bottom-left pixel
        Assert.Equal((0, 255, 0, 255), image[1, 1]);
        Assert.Equal((0, 0, 255, 255), image[0, 0]);
        Assert.Equal((255, 255, 255, 255), image[1, 0]);
    }

    [Fact]
    public void Top_left_origin_keeps_the_file_order_and_32_bit_carries_alpha()
    {
        var file = Tga(0x28, 32, 0, 0, 255, 10, 0, 255, 0, 20, 255, 0, 0, 30, 1, 2, 3, 40);
        var image = TgaReader.Read(file);
        Assert.Equal((255, 0, 0, 10), image[0, 0]);
        Assert.Equal((0, 255, 0, 20), image[1, 0]);
        Assert.Equal((0, 0, 255, 30), image[0, 1]);
        Assert.Equal((3, 2, 1, 40), image[1, 1]);
    }

    [Fact]
    public void Truncated_and_compressed_files_are_refused()
    {
        Assert.Throws<FormatException>(() => TgaReader.Read(Tga(0, 24, 1, 2, 3)));
        var compressed = Tga(0, 24, new byte[12]);
        compressed[2] = 10;
        Assert.Throws<NotSupportedException>(() => TgaReader.Read(compressed));
    }
}
