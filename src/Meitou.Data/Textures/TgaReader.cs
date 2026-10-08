namespace Meitou.Data.Textures;

/// <summary>
/// Reads uncompressed true-colour Targa files (image type 2, 24 or 32 bits per pixel), which is what <c>areasmap.tga</c> is
/// (docs/formats/weather.md). Rows come out top to bottom whatever the file's origin bit says.
/// </summary>
public static class TgaReader
{
    public static RgbaImage Read(ReadOnlySpan<byte> file)
    {
        if (file.Length < 18) throw new FormatException("TGA header is truncated.");
        int idLength = file[0], colourMapType = file[1], imageType = file[2];
        int colourMapLength = file[5] | file[6] << 8, colourMapBits = file[7];
        int width = file[12] | file[13] << 8, height = file[14] | file[15] << 8, bits = file[16];
        bool topDown = (file[17] & 0x20) != 0;
        if (colourMapType != 0 || imageType != 2) throw new NotSupportedException($"TGA image type {imageType} (colour map {colourMapType}) is not supported.");
        if (bits is not (24 or 32)) throw new NotSupportedException($"TGA with {bits} bits per pixel is not supported.");
        if (width == 0 || height == 0) throw new FormatException("TGA has no pixels.");
        int bytesPerPixel = bits / 8;
        int start = 18 + idLength + colourMapLength * ((colourMapBits + 7) / 8);
        if (file.Length < start + (long)width * height * bytesPerPixel) throw new FormatException("TGA pixel data is truncated.");

        var pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            int sourceRow = topDown ? y : height - 1 - y;
            for (int x = 0; x < width; x++)
            {
                int s = start + (sourceRow * width + x) * bytesPerPixel, d = (y * width + x) * 4;
                pixels[d] = file[s + 2];
                pixels[d + 1] = file[s + 1];
                pixels[d + 2] = file[s];
                pixels[d + 3] = bytesPerPixel == 4 ? file[s + 3] : (byte)255;
            }
        }
        return new RgbaImage(width, height, pixels);
    }
}
