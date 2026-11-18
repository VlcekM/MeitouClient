using System.Buffers.Binary;

namespace Meitou.Game.Ui;

/// <summary>
/// The checks a code mod's map marker image file must pass before it is decoded (docs/modding.md "Map markers"): it exists, is a PNG or DDS file of
/// at most <see cref="MaxBytes"/>, and its header gives a size of at most <see cref="MaxSide"/> pixels a side, so a mod cannot make the painter
/// decode and upload an image of any size.
/// </summary>
static class MarkerImageFile
{
    /// <summary>
    /// The largest file accepted.
    /// </summary>
    internal const long MaxBytes = 8 << 20;

    /// <summary>
    /// The longest side accepted, in pixels.
    /// </summary>
    internal const int MaxSide = 1024;

    /// <summary>
    /// The eight bytes every PNG file starts with.
    /// </summary>
    static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>
    /// Why <paramref name="path"/> (a full path) cannot be a marker image, or null when it can.
    /// </summary>
    internal static string? Problem(string path)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists) return "the file does not exist";
            if (file.Length > MaxBytes) return $"the file is larger than {MaxBytes >> 20} MB";

            Span<byte> head = stackalloc byte[24];
            using (var stream = file.OpenRead())
            {
                if (stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false) < head.Length) return "the file is too short to be an image";
            }

            return SizeProblem(head);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "the file could not be opened: " + e.Message;
        }
    }

    /// <summary>
    /// Why a file starting with <paramref name="head"/> (its first 24 bytes) cannot be a marker image: not a PNG or DDS file, or a side of 0 or
    /// over <see cref="MaxSide"/> (PNG: the IHDR width and height, big-endian at bytes 16 and 20; DDS: the header's height and width, little-endian
    /// at bytes 12 and 16).
    /// </summary>
    static string? SizeProblem(ReadOnlySpan<byte> head)
    {
        long width, height;
        if (head[..8].SequenceEqual(PngSignature))
        {
            width = BinaryPrimitives.ReadUInt32BigEndian(head[16..]);
            height = BinaryPrimitives.ReadUInt32BigEndian(head[20..]);
        }
        else if (head[..4].SequenceEqual("DDS "u8))
        {
            height = BinaryPrimitives.ReadUInt32LittleEndian(head[12..]);
            width = BinaryPrimitives.ReadUInt32LittleEndian(head[16..]);
        }
        else
        {
            return "the file is not a PNG or DDS image";
        }

        bool fits = width is > 0 and <= MaxSide && height is > 0 and <= MaxSide;
        return fits ? null : $"the image is {width} by {height} pixels; a side may be at most {MaxSide}";
    }
}
