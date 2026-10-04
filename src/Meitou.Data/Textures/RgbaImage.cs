namespace Meitou.Data.Textures;

/// <summary>An 8-bit RGBA image, rows from top to bottom, 4 bytes per pixel.</summary>
public sealed class RgbaImage
{
    public RgbaImage(int width, int height, byte[]? pixels = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (pixels is not null && pixels.Length != width * height * 4)
            throw new ArgumentException($"Expected {width * height * 4} bytes, got {pixels.Length}.", nameof(pixels));
        Width = width;
        Height = height;
        Pixels = pixels ?? new byte[width * height * 4];
    }

    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }

    public (byte R, byte G, byte B, byte A) this[int x, int y]
    {
        get
        {
            int i = (y * Width + x) * 4;
            return (Pixels[i], Pixels[i + 1], Pixels[i + 2], Pixels[i + 3]);
        }
    }
}
