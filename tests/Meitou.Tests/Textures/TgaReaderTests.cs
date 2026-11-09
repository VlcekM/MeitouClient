using Meitou.Data.Textures;

namespace Meitou.Tests.Textures;

public class TgaReaderTests
{
    static byte[] Tga(byte descriptor, int bits, params byte[] pixelBytes)
    {
        var header = new byte[18];
        header[2] = 2;
        header[12] = 2;
        header[14] = 2;
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
}
