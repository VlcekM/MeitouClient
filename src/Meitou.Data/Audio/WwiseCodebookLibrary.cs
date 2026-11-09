using System.Buffers.Binary;
using System.Text;

namespace Meitou.Data.Audio;

/// <summary>
/// The Vorbis codebooks Wwise Vorbis setup headers refer to by a 10-bit index (docs/formats/wwise-bnk.md#codebook-library). The game's Wwise decoder
/// carries them in <c>kenshi_x64.exe</c>: a table of pointers (in <c>.data</c> in the Steam exe) to the packed codebooks in <c>.rdata</c>, one more
/// pointer than codebooks (the end). Nothing is shipped: the table is found in the install's exe by its shape and checked by parsing every codebook,
/// so any build of the exe works.
/// </summary>
public sealed class WwiseCodebookLibrary
{
    /// <summary>
    /// The layout of <c>codebooks.bin</c>; a cache of another version is read again from the exe.
    /// </summary>
    const int CacheVersion = 1;

    /// <summary>
    /// The packed codebooks by index.
    /// </summary>
    readonly byte[][] _codebooks;

    /// <summary>
    /// A library of <paramref name="codebooks"/>, kept as given.
    /// </summary>
    WwiseCodebookLibrary(byte[][] codebooks) => this._codebooks = codebooks;

    /// <summary>
    /// How many codebooks the library holds (598 in Kenshi's exe).
    /// </summary>
    public int Count => _codebooks.Length;

    /// <summary>
    /// The packed codebook <paramref name="index"/>.
    /// </summary>
    public ReadOnlySpan<byte> Packed(int index) =>
        (uint)index < (uint)_codebooks.Length ? _codebooks[index]
                : throw new InvalidDataException($"Codebook {index} is not in the library ({_codebooks.Length})");

    /// <summary>
    /// A library of the given packed codebooks in order (tests and tools).
    /// </summary>
    public static WwiseCodebookLibrary FromCodebooks(IEnumerable<byte[]> codebooks) => new([.. codebooks]);

    /// <summary>
    /// Finds the library in a Wwise-linked 64-bit Windows executable; null when none is found.
    /// </summary>
    public static WwiseCodebookLibrary? FromExecutable(string exePath) => FromExecutable(File.ReadAllBytes(exePath));

    /// <summary>
    /// The default cache folder: <c>%LOCALAPPDATA%\Meitou\audio</c>.
    /// </summary>
    public static string DefaultCacheDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Meitou", "audio");

    /// <summary>
    /// The first four bytes of <c>codebooks.bin</c>.
    /// </summary>
    static ReadOnlySpan<byte> CacheMagic => "MWCB"u8;

    /// <summary>
    /// The library of <paramref name="exePath"/>, from <c>codebooks.bin</c> in <paramref name="cacheDirectory"/> when that was made from an exe of
    /// the same size and modification time; otherwise read from the exe (36 MB, about 50 ms) and the cache rewritten. Null when the exe holds no
    /// library. A cache that cannot be written is skipped.
    /// </summary>
    public static WwiseCodebookLibrary? Load(string exePath, string? cacheDirectory = null)
    {
        var exe = new FileInfo(exePath);
        if (!exe.Exists) return null;
        var cacheFile = Path.Combine(cacheDirectory ?? DefaultCacheDirectory, "codebooks.bin");
        long stamp = exe.LastWriteTimeUtc.Ticks;
        if (TryReadCache(cacheFile, exe.Length, stamp) is { } cached) return cached;
        var library = FromExecutable(exePath);
        if (library is not null)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(cacheFile)!);
                using var w = new BinaryWriter(File.Create(cacheFile));
                w.Write(CacheMagic);
                w.Write(CacheVersion);
                w.Write(exe.Length);
                w.Write(stamp);
                w.Write(library._codebooks.Length);
                foreach (var book in library._codebooks)
                {
                    w.Write((ushort)book.Length);
                    w.Write(book);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return library;
    }

    /// <summary>
    /// The library cached in <paramref name="file"/> when it was made from an exe of <paramref name="exeLength"/> bytes and write time
    /// <paramref name="stamp"/>; null when missing, stale or unreadable.
    /// </summary>
    static WwiseCodebookLibrary? TryReadCache(string file, long exeLength, long stamp)
    {
        try
        {
            if (!File.Exists(file)) return null;
            using var r = new BinaryReader(File.OpenRead(file));
            if (!r.ReadBytes(4).AsSpan().SequenceEqual(CacheMagic) || r.ReadInt32() != CacheVersion || r.ReadInt64() != exeLength
                    || r.ReadInt64() != stamp)
                return null;
            var books = new byte[r.ReadInt32()][];
            for (int i = 0; i < books.Length; i++)
            {
                books[i] = r.ReadBytes(r.ReadUInt16());
                if (WwiseVorbis.PackedCodebookBits(books[i]) / 8 + 1 != books[i].Length) return null;
            }
            return new WwiseCodebookLibrary(books);
        }
        catch (Exception e)
            when (e is IOException or EndOfStreamException or InvalidDataException or UnauthorizedAccessException or OverflowException)
        {
            return null;
        }
    }

    /// <summary>
    /// Finds the library in the bytes of a Wwise-linked 64-bit Windows executable; null when none is found.
    /// </summary>
    public static WwiseCodebookLibrary? FromExecutable(byte[] exe) => FromExecutable(exe, out _);

    /// <summary>
    /// Why <paramref name="exePath"/> has no library, for the log: missing, not PE32+, or its size, sections and the longest pointer run found
    /// (with the codebook a candidate table failed at). Reads the exe again; call it only after a failed <see cref="Load"/>.
    /// </summary>
    public static string Diagnose(string exePath)
    {
        if (!File.Exists(exePath)) return "the exe is missing";
        var exe = File.ReadAllBytes(exePath);
        FromExecutable(exe, out var why);
        return $"{exe.Length:N0} bytes, {why}";
    }

    /// <summary>
    /// Scans every section that is not code for a run of at least 200 increasing pointers into the image no more than 8000 bytes apart, and takes the
    /// first whose codebooks all parse; <paramref name="why"/> says why none did.
    /// </summary>
    static WwiseCodebookLibrary? FromExecutable(byte[] exe, out string why)
    {
        var sections = Sections(exe, out ulong imageBase);
        if (sections is null)
        {
            why = "not a 64-bit Windows executable";
            return null;
        }
        long ToFile(ulong va)
        {
            if (va < imageBase) return -1;
            ulong rva = va - imageBase;
            foreach (var s in sections)
                if (rva >= s.Va && rva < s.Va + (ulong)s.RawSize) return s.Raw + (long)(rva - s.Va);
            return -1;
        }

        int longest = 0, failedAt = -1;
        string longestIn = "";

        // Kenshi's Steam exe keeps the table in .data; any section that is not code is scanned, as another build may link it elsewhere.
        foreach (var data in sections.Where(s => (s.Flags & 0x20000000) == 0))
        {
            long end = data.Raw + data.RawSize - 8;
            for (long p = data.Raw; p < end; p += 8)
            {
                // A run of increasing pointers no more than 8000 bytes apart, long enough to be the library (Kenshi's: 598 codebooks).
                ulong first = BinaryPrimitives.ReadUInt64LittleEndian(exe.AsSpan((int)p));
                if (first < imageBase || ToFile(first) < 0) continue;
                int n = 1;
                ulong prev = first;
                while (p + 8 * (n + 1) <= data.Raw + data.RawSize)
                {
                    ulong v = BinaryPrimitives.ReadUInt64LittleEndian(exe.AsSpan((int)(p + 8 * n)));
                    if (v <= prev || v - prev > 8000) break;
                    prev = v;
                    n++;
                }
                if (n >= 200)
                {
                    if (TryRead(exe, p, n, ToFile, out int failed) is { } library)
                    {
                        why = "";
                        return library;
                    }
                    failedAt = failed;
                }
                if (n > longest) (longest, longestIn) = (n, data.Name);
                p += 8 * (n - 1);
            }
        }
        why = $"sections {string.Join(' ', sections.Select(s => s.Name))}, the longest pointer run {longest} in {longestIn}"
            + (failedAt >= 0 ? $", the last table of 200+ failed at codebook {failedAt}" : "");
        return null;
    }

    /// <summary>
    /// The codebooks between the <paramref name="pointers"/> pointers at file offset <paramref name="table"/>, each of which must parse and fill its
    /// span exactly; null otherwise, with the failing index in <paramref name="failedAt"/>.
    /// </summary>
    static WwiseCodebookLibrary? TryRead(byte[] exe, long table, int pointers, Func<ulong, long> toFile, out int failedAt)
    {
        var books = new byte[pointers - 1][];
        for (failedAt = 0; failedAt < books.Length; failedAt++)
        {
            int i = failedAt;
            ulong a = BinaryPrimitives.ReadUInt64LittleEndian(exe.AsSpan((int)(table + 8 * i)));
            ulong b = BinaryPrimitives.ReadUInt64LittleEndian(exe.AsSpan((int)(table + 8 * i + 8)));
            long from = toFile(a);
            if (from < 0 || toFile(b - 1) != from + (long)(b - a) - 1) return null;
            books[i] = exe.AsSpan((int)from, (int)(b - a)).ToArray();
            try
            {
                // Every codebook must parse and fill its span: bits / 8 + 1 bytes (a whole extra byte when the bits end on a byte boundary).
                int bits = WwiseVorbis.PackedCodebookBits(books[i]);
                if (bits / 8 + 1 != books[i].Length) return null;
            }
            catch (InvalidDataException)
            {
                return null;
            }
        }
        return new WwiseCodebookLibrary(books);
    }

    /// <summary>
    /// A PE section header: name, virtual address (relative to the image base), file offset and size of its raw data, characteristics flags.
    /// </summary>
    sealed record Section(string Name, ulong Va, long Raw, long RawSize, uint Flags);

    /// <summary>
    /// The section headers of a PE32+ executable and its image base; null when <paramref name="exe"/> is not one.
    /// </summary>
    static List<Section>? Sections(byte[] exe, out ulong imageBase)
    {
        imageBase = 0;
        if (exe.Length < 0x40 || exe[0] != 'M' || exe[1] != 'Z') return null;
        int pe = BinaryPrimitives.ReadInt32LittleEndian(exe.AsSpan(0x3C));
        if (pe < 0 || pe + 24 > exe.Length || BinaryPrimitives.ReadUInt32LittleEndian(exe.AsSpan(pe)) != 0x00004550)
            return null;
        int count = BinaryPrimitives.ReadUInt16LittleEndian(exe.AsSpan(pe + 6));
        int optional = BinaryPrimitives.ReadUInt16LittleEndian(exe.AsSpan(pe + 20));
        if (BinaryPrimitives.ReadUInt16LittleEndian(exe.AsSpan(pe + 24)) != 0x20B) return null; // PE32+ only
        imageBase = BinaryPrimitives.ReadUInt64LittleEndian(exe.AsSpan(pe + 24 + 24));
        var list = new List<Section>(count);
        for (int i = 0; i < count; i++)
        {
            int o = pe + 24 + optional + i * 40;
            if (o + 40 > exe.Length) return null;
            list.Add(new Section(Encoding.ASCII.GetString(exe, o, 8).TrimEnd('\0'), BinaryPrimitives.ReadUInt32LittleEndian(exe.AsSpan(o + 12)),
                BinaryPrimitives.ReadUInt32LittleEndian(exe.AsSpan(o + 20)), BinaryPrimitives.ReadUInt32LittleEndian(exe.AsSpan(o + 16)),
                BinaryPrimitives.ReadUInt32LittleEndian(exe.AsSpan(o + 36))));
        }
        return list;
    }
}