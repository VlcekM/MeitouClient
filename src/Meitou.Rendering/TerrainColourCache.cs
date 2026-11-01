using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Textures;
using Meitou.Data.World;

namespace Meitou.Rendering;

/// <summary>
/// The terrain colour tiles (<c>overlaymaps/colour.X.Z.png</c>) as BC3 mip chains for the colour window (docs/render-terrain.md, "Colour window"):
/// encoded once and kept in <c>%LOCALAPPDATA%\Meitou\terrain\colour</c>, one file per tile named by a hash of its path, length and write time, so a
/// mod's tile gets its own. BC3, not BC1: the alpha is the ground's gloss. With <c>--no-load-cache</c> a tile is encoded whenever it is needed.
/// </summary>
static class TerrainColourCache
{
    /// <summary>Levels kept per tile: the window's.</summary>
    public const int Levels = 7;

    /// <summary>A tile's side must be a multiple of this: its last level is still whole 4 × 4 blocks.</summary>
    public const int Granule = 4 << (Levels - 1);

    const string Version = "terrain colour bc3 v1";
    const int Magic = 0x3343544D;   // "MTC3"
    static readonly ConcurrentDictionary<string, object> gates = new();

    /// <summary>A tile's side and its levels' BC3 blocks, rows of blocks top first.</summary>
    public sealed record Tile(int Size, byte[][] Levels);

    static IEnumerable<string> TilePaths(GameInstall install) =>
        Enumerable.Range(0, TerrainMaps.OverlayTiles * TerrainMaps.OverlayTiles)
            .Select(t => TerrainMaps.OverlayTile(install, "colour", t % TerrainMaps.OverlayTiles, t / TerrainMaps.OverlayTiles));

    static string[] Uncached(GameInstall install) =>
        LoadCaches.Disabled ? [] : [.. TilePaths(install).Where(p => File.Exists(p) && !File.Exists(CachePath(p)))];

    /// <summary>Whether a tile has no cache file yet (the first start, or a mod's new tile).</summary>
    public static bool Missing(GameInstall install) => Uncached(install).Length > 0;

    /// <summary>Encodes and writes every tile without a cache file (worker thread); returns how many. A tile unfit for blocks is left to the window to report.</summary>
    public static int EncodeMissing(GameInstall install)
    {
        var missing = Uncached(install);
        // A few tiles at once: each is 16 MB decoded plus its levels, and the mips and the encoder are parallel themselves.
        Parallel.ForEach(missing, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 4) }, path =>
        {
            try { Get(path); }
            catch (Exception e) when (e is IOException or InvalidDataException) { }
        });
        return missing.Length;
    }

    /// <summary>A tile's levels, read from its cache file or encoded (and written); null when the tile does not exist.</summary>
    public static Tile? Get(string path)
    {
        if (!File.Exists(path)) return null;
        string? cache = LoadCaches.Disabled ? null : CachePath(path);
        lock (gates.GetOrAdd(path, _ => new object()))
        {
            if (cache is not null && Read(cache) is { } hit) return hit;
            var tile = Encode(path);
            if (cache is not null) Write(cache, tile);
            return tile;
        }
    }

    /// <summary>A tile's valid cache file and its side: a window reads the block rows it needs from it (<see cref="ReadBlocks"/>) instead of the whole tile.</summary>
    public sealed record Stored(string Path, int Size);

    /// <summary>
    /// The tile's cache file, encoded and written first when it has none; null when the tile does not exist, the caches are off or the file could not be
    /// written (the caller takes <see cref="Get"/>'s levels).
    /// </summary>
    public static Stored? Locate(string path)
    {
        if (LoadCaches.Disabled || !File.Exists(path)) return null;
        string cache = CachePath(path);
        lock (gates.GetOrAdd(path, _ => new object()))
        {
            if (Header(cache) is { } size) return new Stored(cache, size);
            var tile = Encode(path);
            Write(cache, tile);
            return Header(cache) is { } written ? new Stored(cache, written) : null;
        }
    }

    /// <summary>Reads <paramref name="into"/>'s length of level <paramref name="level"/>'s blocks from <paramref name="offset"/> bytes into that level.</summary>
    public static void ReadBlocks(Microsoft.Win32.SafeHandles.SafeFileHandle file, Stored tile, int level, long offset, Span<byte> into)
    {
        long at = 8 + LevelBytes(tile.Size, level) + offset;
        while (into.Length > 0)
        {
            int read = RandomAccess.Read(file, into, at);
            if (read <= 0) throw new IOException($"{tile.Path} ended early");
            into = into[read..];
            at += read;
        }
    }

    // The side of a valid cache file (magic, a side fit for blocks, the length of its levels), else null.
    static int? Header(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var file = File.OpenHandle(path);
            Span<byte> head = stackalloc byte[8];
            if (RandomAccess.Read(file, head, 0) != 8 || BitConverter.ToInt32(head) != Magic) return null;
            int size = BitConverter.ToInt32(head[4..]);
            return size > 0 && size % Granule == 0 && RandomAccess.GetLength(file) == 8 + LevelBytes(size, Levels) ? size : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    static Tile Encode(string path)
    {
        var img = TextureLoader.LoadImage(File.ReadAllBytes(path));
        if (img.Width != img.Height || img.Width % Granule != 0)
            throw new InvalidDataException($"{Path.GetFileName(path)} is {img.Width}x{img.Height}, not square with a side a multiple of {Granule}");
        var mips = TerrainTextures.Mips(img.Pixels, img.Width, img.Height, Levels);
        var levels = new byte[Levels][];
        for (int l = 0; l < Levels; l++) levels[l] = BlockCompression.EncodeBc3(mips[l], img.Width >> l, img.Width >> l);
        return new Tile(img.Width, levels);
    }

    static string CachePath(string tile)
    {
        var file = new FileInfo(tile);
        var key = FormattableString.Invariant($"{Version}|{tile}|{file.Length}|{file.LastWriteTimeUtc.Ticks}");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..24].ToLowerInvariant();
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Meitou", "terrain", "colour", hash + ".bc3");
    }

    // The file: magic, side (i32 each), then every level's blocks; one of any other length is encoded again.
    static Tile? Read(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length < 8 || BitConverter.ToInt32(bytes, 0) != Magic) return null;
            int size = BitConverter.ToInt32(bytes, 4);
            if (size <= 0 || size % Granule != 0 || bytes.Length != 8 + LevelBytes(size, Levels)) return null;
            var levels = new byte[Levels][];
            for (int l = 0, at = 8; l < Levels; at += levels[l].Length, l++) levels[l] = bytes.AsSpan(at, LevelBytes(size >> l, 1)).ToArray();
            return new Tile(size, levels);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    static void Write(string path, Tile tile)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + "." + Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".tmp";
            using (var stream = File.Create(temporary))
            {
                stream.Write(BitConverter.GetBytes(Magic));
                stream.Write(BitConverter.GetBytes(tile.Size));
                foreach (var level in tile.Levels) stream.Write(level);
            }
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>BC3 bytes of <paramref name="levels"/> levels from a side of <paramref name="size"/>.</summary>
    static int LevelBytes(int size, int levels)
    {
        int bytes = 0;
        for (int l = 0; l < levels; l++) bytes += BlockCompression.Bc3Size(size >> l, size >> l);
        return bytes;
    }
}
