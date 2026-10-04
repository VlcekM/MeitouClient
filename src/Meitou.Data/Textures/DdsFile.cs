namespace Meitou.Data.Textures;

/// <summary>
/// A DirectDraw Surface (<c>.dds</c>) texture: header facts plus the raw surface data, unchanged. Layout:
/// docs/formats/dds.md. Decode a surface with <see cref="DdsDecoder"/>.
/// </summary>
public sealed class DdsFile
{
    /// <summary>The whole file (surfaces are slices of it).</summary>
    public required byte[] Data { get; init; }

    public int Width { get; init; }
    public int Height { get; init; }

    /// <summary>Depth of a volume texture; 1 otherwise.</summary>
    public int Depth { get; init; } = 1;

    /// <summary>Mip levels per image, including the full-size level (at least 1).</summary>
    public int MipCount { get; init; } = 1;

    /// <summary>
    /// Images in the file, each with its own mip chain: 6 for a cubemap (faces +X, -X, +Y, -Y, +Z, -Z, as present),
    /// the array size for a DX10 texture array (times 6 for a cube array), otherwise 1.
    /// </summary>
    public int ImageCount { get; init; } = 1;

    public bool IsCubemap { get; init; }
    public bool IsVolume { get; init; }

    public DdsFormat Format { get; init; }

    /// <summary>The legacy pixel-format FourCC (e.g. <c>DXT5</c>), or null for an uncompressed legacy format.</summary>
    public string? FourCC { get; init; }

    /// <summary>The DXGI format number from the DX10 header, or null without one.</summary>
    public int? DxgiFormat { get; init; }

    /// <summary>Channel layout of <see cref="DdsFormat.Uncompressed"/> data; null for other formats.</summary>
    public DdsPixelMasks? Masks { get; init; }

    /// <summary>Header flags (<c>DDSD_*</c>) as stored.</summary>
    public uint HeaderFlags { get; init; }

    /// <summary>Size of the headers: 128 (magic + header), or 148 with a DX10 header.</summary>
    public int HeaderSize { get; init; }

    /// <summary>Surfaces in file order: image by image, each image's mip levels from largest to smallest.</summary>
    public required IReadOnlyList<DdsSurface> Surfaces { get; init; }

    /// <summary>Bytes after the last surface (0 for a well-formed file).</summary>
    public int TrailingBytes => Data.Length - (Surfaces.Count == 0 ? HeaderSize : Surfaces[^1].Offset + Surfaces[^1].Length);

    public DdsSurface Surface(int image, int level) => Surfaces[image * MipCount + level];

    public ReadOnlySpan<byte> SurfaceData(DdsSurface surface) => Data.AsSpan(surface.Offset, surface.Length);

    public override string ToString() =>
        $"{Width}x{Height}{(IsVolume ? $"x{Depth}" : "")} {Format}{(IsCubemap ? " cube" : "")} mips={MipCount} images={ImageCount}";
}

/// <param name="Image">Index of the image (cube face / array slice).</param>
/// <param name="Level">Mip level, 0 = full size.</param>
/// <param name="Offset">Offset of the data in <see cref="DdsFile.Data"/>.</param>
/// <param name="Length">Size of the data in bytes.</param>
public readonly record struct DdsSurface(int Image, int Level, int Width, int Height, int Depth, int Offset, int Length);

/// <summary>Pixel layouts the reader understands.</summary>
public enum DdsFormat
{
    /// <summary>BC1 / DXT1: 8-byte blocks of 4×4 pixels, colour with optional 1-bit alpha.</summary>
    Bc1,
    /// <summary>BC2 / DXT3 (also DXT2): 16-byte blocks, explicit 4-bit alpha.</summary>
    Bc2,
    /// <summary>BC3 / DXT5 (also DXT4): 16-byte blocks, interpolated alpha.</summary>
    Bc3,
    /// <summary>BC4 / ATI1: 8-byte blocks, one interpolated channel.</summary>
    Bc4,
    /// <summary>BC5 / ATI2: 16-byte blocks, two interpolated channels (typically a normal map's X and Y).</summary>
    Bc5,
    /// <summary>Uncompressed pixels with channel bit masks (<see cref="DdsFile.Masks"/>), 8 to 32 bits per pixel.</summary>
    Uncompressed,
}

/// <summary>Channel bit masks of an uncompressed format. A zero mask means the channel is absent.</summary>
/// <param name="Luminance">True when <see cref="Red"/> is a luminance (grey) channel.</param>
public sealed record DdsPixelMasks(int BitCount, uint Red, uint Green, uint Blue, uint Alpha, bool Luminance = false)
{
    public int BytesPerPixel => BitCount / 8;
}

public sealed class DdsFormatException(string message) : Exception(message);
