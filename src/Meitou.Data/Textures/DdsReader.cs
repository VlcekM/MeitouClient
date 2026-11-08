using System.Buffers.Binary;
using System.Text;

namespace Meitou.Data.Textures;

/// <summary>
/// Reads <c>.dds</c> files (legacy header and DX10 extension). Layout: docs/formats/dds.md. Written from the
/// public DDS documentation (Microsoft, "DDS_HEADER" / "DDS_PIXELFORMAT" / "DDS_HEADER_DXT10").
/// </summary>
public static class DdsReader
{
    public const uint Magic = 0x20534444; // "DDS "
    const int HeaderSize = 124, Dx10HeaderSize = 20;

    // DDS_HEADER.flags
    public const uint FlagMipMapCount = 0x20000;
    public const uint FlagDepth = 0x800000;

    // DDS_PIXELFORMAT.flags
    const uint PfAlphaPixels = 0x1, PfAlpha = 0x2, PfFourCc = 0x4, PfRgb = 0x40, PfYuv = 0x200, PfLuminance = 0x20000;

    // caps2
    const uint Caps2Cubemap = 0x200, Caps2Volume = 0x200000;
    static readonly uint[] CubeFaces = [0x400, 0x800, 0x1000, 0x2000, 0x4000, 0x8000];

    const uint Dx10MiscTextureCube = 0x4;
    const int Dx10DimensionTexture3D = 4;

    public static DdsFile ReadFile(string path) => Read(File.ReadAllBytes(Content.GameFiles.Resolve(path)));

    /// <summary>Parses the headers and lays out every surface. Throws <see cref="DdsFormatException"/> on bad or unsupported data.</summary>
    public static DdsFile Read(byte[] data) => Parse(data.AsSpan(), data.Length, data);

    /// <summary>
    /// Reads a DDS file from an open handle keeping only what <paramref name="keep"/> chooses. The headers are read first (<paramref name="whole"/>: every surface laid out, no
    /// data), <paramref name="keep"/> returns the file as it is to be held (its top mips dropped by <see cref="TextureQuality.DropTopMips"/>, the surfaces re-sliced), and only the
    /// bytes from its first surface on are read. A dropped top level is three quarters of a mip chain's bytes, which were read, held and thrown away with the file.
    /// The result's <see cref="DdsFile.Data"/> starts at its first surface (the offsets are rebased); when <paramref name="keep"/> returns <paramref name="whole"/> itself (nothing dropped)
    /// the result is the whole file, as <see cref="Read"/> returns it, and <paramref name="whole"/> is that file.
    /// With <paramref name="pool"/> the result's <see cref="DdsFile.Data"/> is rented from it and may be longer than the bytes read (go by the surfaces, not its length;
    /// <see cref="DdsFile.TrailingBytes"/> means nothing): the caller returns it once nothing reads the file any more (docs/engine.md, pooled buffers).
    /// </summary>
    public static DdsFile ReadKept(Microsoft.Win32.SafeHandles.SafeFileHandle file, Func<DdsFile, DdsFile> keep, out DdsFile whole, System.Buffers.ArrayPool<byte>? pool = null)
    {
        long length = RandomAccess.GetLength(file);
        if (length > int.MaxValue) throw new DdsFormatException($"File of {length} bytes is too large.");
        Span<byte> head = stackalloc byte[4 + HeaderSize + Dx10HeaderSize];
        head = head[..ReadFully(file, head, 0)];
        var headers = Parse(head, length, []);
        var kept = keep(headers);
        bool all = ReferenceEquals(kept, headers) || kept.Surfaces.Count == 0;
        int first = all ? 0 : kept.Surfaces[0].Offset, count = (int)(length - first);
        var data = pool?.Rent(count) ?? new byte[count];
        try
        {
            if (ReadFully(file, data.AsSpan(0, count), first) != count) throw new DdsFormatException("File ended early.");
            if (all) return whole = Parse(data, length, data);
            whole = headers;
            return kept.WithData(data, kept.Surfaces.Select(s => s with { Offset = s.Offset - first }).ToList());
        }
        catch
        {
            pool?.Return(data);
            throw;
        }
    }

    /// <summary>
    /// Reads only the first surface (the full-size level of the first image) of a DDS file from an open handle: the headers, then that surface's bytes. The result's
    /// <see cref="DdsFile.Data"/> holds just that surface (its offset rebased to 0), so a 14-level 8192 x 8192 BC1 file costs 32 MB instead of 43 MB read and held.
    /// </summary>
    public static DdsFile ReadTopSurface(Microsoft.Win32.SafeHandles.SafeFileHandle file)
    {
        long length = RandomAccess.GetLength(file);
        if (length > int.MaxValue) throw new DdsFormatException($"File of {length} bytes is too large.");
        Span<byte> head = stackalloc byte[4 + HeaderSize + Dx10HeaderSize];
        head = head[..ReadFully(file, head, 0)];
        var headers = Parse(head, length, []);
        if (headers.Surfaces.Count == 0) throw new DdsFormatException("No surfaces.");
        var top = headers.Surfaces[0];
        var data = new byte[top.Length];
        if (ReadFully(file, data, top.Offset) != top.Length) throw new DdsFormatException("File ended early.");
        return headers.WithData(data, [top with { Offset = 0 }]);
    }

    static int ReadFully(Microsoft.Win32.SafeHandles.SafeFileHandle file, Span<byte> buffer, long offset)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = RandomAccess.Read(file, buffer[read..], offset + read);
            if (n == 0) break;
            read += n;
        }
        return read;
    }

    /// <summary>The headers in <paramref name="span"/> (at least the headers) of a file of <paramref name="fileLength"/> bytes, whose bytes are <paramref name="data"/> (empty when only the headers were read).</summary>
    static DdsFile Parse(ReadOnlySpan<byte> span, long fileLength, byte[] data)
    {
        if (span.Length < 4 + HeaderSize) throw new DdsFormatException($"File too short for a DDS header ({span.Length} bytes).");
        if (U32(span, 0) != Magic) throw new DdsFormatException("Missing 'DDS ' magic.");
        if (U32(span, 4) != HeaderSize) throw new DdsFormatException($"Header size {U32(span, 4)}, expected {HeaderSize}.");

        uint flags = U32(span, 8);
        int height = checked((int)U32(span, 12));
        int width = checked((int)U32(span, 16));
        int depth = (int)U32(span, 24);
        int mipCount = (flags & FlagMipMapCount) != 0 && U32(span, 28) > 0 ? checked((int)U32(span, 28)) : 1;
        // DDS_PIXELFORMAT at 76: size, flags, fourCC, bitCount, r, g, b, a masks
        uint pfFlags = U32(span, 80);
        uint fourCc = U32(span, 84);
        int bitCount = (int)U32(span, 88);
        uint rMask = U32(span, 92), gMask = U32(span, 96), bMask = U32(span, 100), aMask = U32(span, 104);
        uint caps2 = U32(span, 112);
        if (width <= 0 || height <= 0) throw new DdsFormatException($"Bad size {width}x{height}.");

        int headerEnd = 4 + HeaderSize;
        bool volume = (caps2 & Caps2Volume) != 0 && (flags & FlagDepth) != 0 && depth > 1;
        bool cube = (caps2 & Caps2Cubemap) != 0;
        int images = cube ? CubeFaces.Count(f => (caps2 & f) != 0) : 1;
        if (cube && images == 0) images = 6; // some writers set only DDSCAPS2_CUBEMAP
        DdsFormat format;
        DdsPixelMasks? masks = null;
        string? fourCcText = null;
        int? dxgi = null;

        if ((pfFlags & PfFourCc) != 0)
        {
            fourCcText = Encoding.ASCII.GetString(span.Slice(84, 4));
            if (fourCcText == "DX10")
            {
                if (span.Length < headerEnd + Dx10HeaderSize) throw new DdsFormatException("File too short for the DX10 header.");
                dxgi = (int)U32(span, headerEnd);
                int dimension = (int)U32(span, headerEnd + 4);
                uint misc = U32(span, headerEnd + 8);
                int arraySize = Math.Max(1, (int)U32(span, headerEnd + 12));
                headerEnd += Dx10HeaderSize;
                (format, masks) = FromDxgi(dxgi.Value);
                cube = (misc & Dx10MiscTextureCube) != 0;
                volume = dimension == Dx10DimensionTexture3D && depth > 1;
                images = arraySize * (cube ? 6 : 1);
            }
            else
            {
                format = fourCcText switch
                {
                    "DXT1" => DdsFormat.Bc1,
                    "DXT2" or "DXT3" => DdsFormat.Bc2,
                    "DXT4" or "DXT5" => DdsFormat.Bc3,
                    "ATI1" or "BC4U" => DdsFormat.Bc4,
                    "ATI2" or "BC5U" => DdsFormat.Bc5,
                    "RXGB" => DdsFormat.Rxgb,
                    _ => throw new DdsFormatException($"Unsupported FourCC '{Printable(fourCcText, fourCc)}'."),
                };
            }
        }
        else if ((pfFlags & (PfRgb | PfLuminance | PfAlpha)) != 0 && (pfFlags & PfYuv) == 0)
        {
            if (bitCount is not (8 or 16 or 24 or 32)) throw new DdsFormatException($"Unsupported bit count {bitCount}.");
            format = DdsFormat.Uncompressed;
            bool hasAlpha = (pfFlags & (PfAlphaPixels | PfAlpha)) != 0;
            masks = (pfFlags & PfAlpha) != 0 && (pfFlags & (PfRgb | PfLuminance)) == 0
                ? new DdsPixelMasks(bitCount, 0, 0, 0, aMask)
                : new DdsPixelMasks(bitCount, rMask, (pfFlags & PfLuminance) != 0 ? 0 : gMask, (pfFlags & PfLuminance) != 0 ? 0 : bMask,
                    hasAlpha ? aMask : 0, (pfFlags & PfLuminance) != 0);
        }
        else throw new DdsFormatException($"Unsupported pixel format flags 0x{pfFlags:X}.");

        if (!volume) depth = 1;
        var surfaces = new List<DdsSurface>(images * mipCount);
        long offset = headerEnd;
        for (int image = 0; image < images; image++)
            for (int level = 0; level < mipCount; level++)
            {
                int w = Math.Max(1, width >> level), h = Math.Max(1, height >> level), d = Math.Max(1, depth >> level);
                long length = SurfaceSize(format, masks, w, h) * d;
                if (offset + length > fileLength)
                    throw new DdsFormatException($"Surface {image}/{level} ({w}x{h}) ends at {offset + length}, past the end of the file ({fileLength}).");
                surfaces.Add(new DdsSurface(image, level, w, h, d, (int)offset, (int)length));
                offset += length;
            }

        return new DdsFile
        {
            Data = data,
            Width = width,
            Height = height,
            Depth = depth,
            MipCount = mipCount,
            ImageCount = images,
            IsCubemap = cube,
            IsVolume = volume,
            Format = format,
            FourCc = fourCcText,
            DxgiFormat = dxgi,
            Masks = masks,
            HeaderFlags = flags,
            HeaderSize = headerEnd,
            Surfaces = surfaces,
        };
    }

    /// <summary>Bytes of one 2D surface (one depth slice) of <paramref name="width"/>×<paramref name="height"/>.</summary>
    public static long SurfaceSize(DdsFormat format, DdsPixelMasks? masks, int width, int height)
    {
        long blocks = (long)((width + 3) / 4) * ((height + 3) / 4);
        return format switch
        {
            DdsFormat.Bc1 or DdsFormat.Bc4 => blocks * 8,
            DdsFormat.Bc2 or DdsFormat.Bc3 or DdsFormat.Bc5 or DdsFormat.Bc7 or DdsFormat.Rxgb => blocks * 16,
            DdsFormat.Uncompressed => (long)width * height * masks!.BytesPerPixel,
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, null),
        };
    }

    static (DdsFormat, DdsPixelMasks?) FromDxgi(int dxgi) => dxgi switch
    {
        70 or 71 or 72 => (DdsFormat.Bc1, null),      // BC1_TYPELESS / UNORM / UNORM_SRGB
        73 or 74 or 75 => (DdsFormat.Bc2, null),
        76 or 77 or 78 => (DdsFormat.Bc3, null),
        79 or 80 => (DdsFormat.Bc4, null),            // BC4_TYPELESS / UNORM (SNORM 81 not supported)
        82 or 83 => (DdsFormat.Bc5, null),            // BC5_TYPELESS / UNORM (SNORM 84 not supported)
        97 or 98 or 99 => (DdsFormat.Bc7, null),      // BC7_TYPELESS / UNORM / UNORM_SRGB (sRGB is not applied: like BC1-BC3 the data is sampled as stored)
        27 or 28 or 29 => (DdsFormat.Uncompressed, new DdsPixelMasks(32, 0x000000FF, 0x0000FF00, 0x00FF0000, 0xFF000000)), // R8G8B8A8
        87 or 90 or 91 => (DdsFormat.Uncompressed, new DdsPixelMasks(32, 0x00FF0000, 0x0000FF00, 0x000000FF, 0xFF000000)), // B8G8R8A8
        88 or 92 or 93 => (DdsFormat.Uncompressed, new DdsPixelMasks(32, 0x00FF0000, 0x0000FF00, 0x000000FF, 0)),          // B8G8R8X8
        85 => (DdsFormat.Uncompressed, new DdsPixelMasks(16, 0xF800, 0x07E0, 0x001F, 0)),       // B5G6R5
        86 => (DdsFormat.Uncompressed, new DdsPixelMasks(16, 0x7C00, 0x03E0, 0x001F, 0x8000)),  // B5G5R5A1
        115 => (DdsFormat.Uncompressed, new DdsPixelMasks(16, 0x0F00, 0x00F0, 0x000F, 0xF000)), // B4G4R4A4
        61 => (DdsFormat.Uncompressed, new DdsPixelMasks(8, 0xFF, 0, 0, 0, Luminance: true)),   // R8_UNORM, shown as grey
        65 => (DdsFormat.Uncompressed, new DdsPixelMasks(8, 0, 0, 0, 0xFF)),                    // A8_UNORM
        _ => throw new DdsFormatException($"Unsupported DXGI format {dxgi}."),
    };

    static uint U32(ReadOnlySpan<byte> s, int at) => BinaryPrimitives.ReadUInt32LittleEndian(s[at..]);

    static string Printable(string text, uint value) => text.All(c => c is >= ' ' and <= '~') ? text : $"0x{value:X8}";
}