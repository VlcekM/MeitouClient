namespace Meitou.Data.Textures;

/// <summary>
/// An 8-bit RGBA image, rows from top to bottom, 4 bytes per pixel.
/// </summary>
public sealed class RgbaImage
{
    /// <summary>
    /// An image of <paramref name="width"/> x <paramref name="height"/> over <paramref name="pixels"/> (width x height x 4 bytes, not copied), or a
    /// new transparent black one.
    /// </summary>
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

    /// <summary>
    /// Width in pixels.
    /// </summary>
    public int Width { get; }
    /// <summary>
    /// Height in pixels.
    /// </summary>
    public int Height { get; }
    /// <summary>
    /// The pixel bytes, R, G, B, A per pixel, rows top to bottom.
    /// </summary>
    public byte[] Pixels { get; }

    /// <summary>
    /// The pixel at column <paramref name="x"/>, row <paramref name="y"/> (0 the top).
    /// </summary>
    public (byte R, byte G, byte B, byte A) this[int x, int y]
    {
        get
        {
            int i = (y * Width + x) * 4;
            return (Pixels[i], Pixels[i + 1], Pixels[i + 2], Pixels[i + 3]);
        }
    }
}