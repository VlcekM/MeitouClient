namespace Meitou.Data.Textures;

/// <summary>
/// Reads true-colour Targa files, uncompressed (image type 2, which is what the base game's <c>areasmap.tga</c> is, docs/formats/weather.md) or
/// run-length encoded (type 10, a mod's, e.g. Soulbody's), 24 or 32 bits per pixel. Rows come out top to bottom whatever the file's origin bit says.
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
        if (colourMapType != 0 || imageType is not (2 or 10)) throw new NotSupportedException($"TGA image type {imageType} (colour map {colourMapType}) is not supported.");
        if (bits is not (24 or 32)) throw new NotSupportedException($"TGA with {bits} bits per pixel is not supported.");
        if (width == 0 || height == 0) throw new FormatException("TGA has no pixels.");
        int bytesPerPixel = bits / 8;
        int start = 18 + idLength + colourMapLength * ((colourMapBits + 7) / 8);
        if (imageType == 10)
        {
            file = Unpack(file[start..], width * height, bytesPerPixel);
            start = 0;
        }
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

    /// <summary>The <paramref name="count"/> pixels of run-length packets: a header byte whose top bit marks a run of one pixel repeated, or else
    /// raw pixels, (low 7 bits + 1) of them.</summary>
    static byte[] Unpack(ReadOnlySpan<byte> packed, int count, int bytesPerPixel)
    {
        var pixels = new byte[count * bytesPerPixel];
        int read = 0, written = 0;
        while (written < pixels.Length)
        {
            if (read >= packed.Length) throw new FormatException("TGA run-length data is truncated.");
            int header = packed[read++], n = (header & 0x7F) + 1;
            int bytes = Math.Min(n * bytesPerPixel, pixels.Length - written);
            if ((header & 0x80) != 0)
            {
                if (read + bytesPerPixel > packed.Length) throw new FormatException("TGA run-length data is truncated.");
                for (int i = 0; i < bytes; i++) pixels[written + i] = packed[read + i % bytesPerPixel];
                read += bytesPerPixel;
            }
            else
            {
                if (read + bytes > packed.Length) throw new FormatException("TGA run-length data is truncated.");
                packed.Slice(read, bytes).CopyTo(pixels.AsSpan(written));
                read += n * bytesPerPixel;
            }
            written += bytes;
        }
        return pixels;
    }
}
