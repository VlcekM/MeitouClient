using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using Meitou.Data.Textures;

namespace Meitou.Rendering;

/// <summary>
/// Where the alpha-tested part of a texture is (docs/render-foliage.md, "Card trimming"): grids of cells, one cell per texel of the decoded level (at most 512 on a side).
/// Level <c>l</c> of the mask has a cell set when some texel the cut-out can let through lies in it, taking the texture's mips 0 to <c>l</c> into account: a texel of such a
/// mip with alpha at or above the cut-off, together with the texel around it (the bilinear footprint) and every cell of the grid it overlaps. So level 0 is the opaque part
/// of the finest mip, and each further level also holds what the next coarser mip lets through, which is what a card sees from further away. A card trimmed to the cells of a
/// level loses no pixel the sampler can keep from mips up to that level; <see cref="FoliageCardTrimmer"/> picks the level from how big the card is on screen at the smallest.
/// </summary>
public sealed class FoliageCardMask
{
    /// <summary>Bumped when the mask's rule changes: it is part of the disk cache's key.</summary>
    public const int Version = 3;

    /// <summary>The grid's largest side in cells.</summary>
    public const int MaxCells = 512;

    /// <summary>The mip chain stops at this many pixels on the longer side (<c>MEITOU_TRIM_COARSEST</c>): the mask has a level for each mip down to it.</summary>
    public static int CoarsestSize { get; } = int.TryParse(Environment.GetEnvironmentVariable("MEITOU_TRIM_COARSEST"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int c) && c >= 1 ? c : 16;

    /// <summary>How far below the threshold (0-1) a texel still counts: the alpha-to-coverage ramp of the multisampled targets lets pixels a little under it through (<c>MEITOU_TRIM_SLACK</c>).</summary>
    public static float Slack { get; } = float.TryParse(Environment.GetEnvironmentVariable("MEITOU_TRIM_SLACK"), NumberStyles.Float, CultureInfo.InvariantCulture, out float s) && s >= 0 ? s : 0.1f;

    /// <param name="levels">Per level the cells, row-major, row 0 at v = 0 (the top row of the decoded image); each level contains the one before.</param>
    public FoliageCardMask(int width, int height, bool[][] levels)
    {
        if (levels.Length == 0 || levels.Any(l => l.Length != width * height)) throw new ArgumentException("Wrong cell count.", nameof(levels));
        Width = width;
        Height = height;
        Levels = levels;
    }

    public int Width { get; }
    public int Height { get; }
    public bool[][] Levels { get; }
    public int LevelCount => Levels.Length;

    /// <summary>The share of cells set at <paramref name="level"/> (clamped).</summary>
    public double Coverage(int level = 0)
    {
        var cells = Levels[Math.Clamp(level, 0, Levels.Length - 1)];
        return cells.Count(c => c) / (double)cells.Length;
    }

    /// <summary>The cut-off a threshold (0-1) becomes: the threshold less <see cref="Slack"/> (at least 8/255, so a threshold near zero does not set every cell).</summary>
    public static byte CutoffOf(float threshold) => (byte)Math.Clamp((int)MathF.Floor((threshold - Slack) * 255f), 8, 255);

    /// <summary>
    /// The mask of an alpha plane (<paramref name="alpha"/>, <paramref name="width"/> x <paramref name="height"/>, rows top to bottom): mips of 2 x 2 averages down to
    /// <paramref name="coarsest"/> pixels, each thresholded at <paramref name="cutoff"/> and widened by one texel, folded onto the cells of the plane (at most <see cref="MaxCells"/> a side).
    /// </summary>
    public static FoliageCardMask FromAlpha(byte[] alpha, int width, int height, byte cutoff, int coarsest)
    {
        int gw = Math.Min(width, MaxCells), gh = Math.Min(height, MaxCells);
        var levels = new List<bool[]>();
        var cells = new bool[gw * gh];
        var plane = alpha;
        int w = width, h = height;
        while (true)
        {
            // This mip's mask, widened by one texel (bilinear footprint), folded into the cells it overlaps.
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    if (plane[y * w + x] < cutoff) continue;
                    int x0 = Math.Max(x - 1, 0), x1 = Math.Min(x + 1, w - 1), y0 = Math.Max(y - 1, 0), y1 = Math.Min(y + 1, h - 1);
                    // The cells a texel run [x0, x1] covers on a grid gw wide: floor(x0 * gw / w) .. ceil((x1 + 1) * gw / w) - 1.
                    int cx0 = (int)((long)x0 * gw / w), cx1 = Math.Max(cx0, (int)(((long)(x1 + 1) * gw + w - 1) / w) - 1);
                    int cy0 = (int)((long)y0 * gh / h), cy1 = Math.Max(cy0, (int)(((long)(y1 + 1) * gh + h - 1) / h) - 1);
                    for (int cy = cy0; cy <= cy1; cy++)
                        for (int cx = cx0; cx <= cx1; cx++) cells[cy * gw + cx] = true;
                }
            levels.Add((bool[])cells.Clone());   // the cells so far: this mip and the finer ones
            if (Math.Max(w, h) <= Math.Max(coarsest, 1) || (w == 1 && h == 1)) break;
            int nw = Math.Max(w / 2, 1), nh = Math.Max(h / 2, 1);
            var next = new byte[nw * nh];
            for (int y = 0; y < nh; y++)
                for (int x = 0; x < nw; x++)
                {
                    int sx = Math.Min(x * 2, w - 1), sy = Math.Min(y * 2, h - 1), tx = Math.Min(sx + 1, w - 1), ty = Math.Min(sy + 1, h - 1);
                    next[y * nw + x] = (byte)((plane[sy * w + sx] + plane[sy * w + tx] + plane[ty * w + sx] + plane[ty * w + tx] + 2) >> 2);
                }
            plane = next;
            (w, h) = (nw, nh);
        }
        return new FoliageCardMask(gw, gh, [.. levels]);
    }

    /// <summary>The alpha plane of a texture file, from the finest DDS level no larger than 512 pixels (or the image, halved until it is), with its size.</summary>
    public static (byte[] Alpha, int Width, int Height) DecodeAlpha(byte[] file)
    {
        RgbaImage image;
        if (file.Length >= 4 && BitConverter.ToUInt32(file, 0) == DdsReader.Magic)
        {
            var dds = DdsReader.Read(file);
            int level = Array.FindIndex(dds.Surfaces.Take(dds.MipCount).ToArray(), s => Math.Max(s.Width, s.Height) <= MaxCells);
            image = DdsDecoder.Decode(dds, 0, level < 0 ? dds.MipCount - 1 : level);
        }
        else image = TextureLoader.LoadImage(file);
        var alpha = new byte[image.Width * image.Height];
        for (int i = 0; i < alpha.Length; i++) alpha[i] = image.Pixels[i * 4 + 3];
        int w = image.Width, h = image.Height;
        while (Math.Max(w, h) > MaxCells)
        {
            int nw = Math.Max(w / 2, 1), nh = Math.Max(h / 2, 1);
            var next = new byte[nw * nh];
            for (int y = 0; y < nh; y++)
                for (int x = 0; x < nw; x++)
                {
                    int sx = Math.Min(x * 2, w - 1), sy = Math.Min(y * 2, h - 1), tx = Math.Min(sx + 1, w - 1), ty = Math.Min(sy + 1, h - 1);
                    next[y * nw + x] = (byte)((alpha[sy * w + sx] + alpha[sy * w + tx] + alpha[ty * w + sx] + alpha[ty * w + tx] + 2) >> 2);
                }
            alpha = next;
            (w, h) = (nw, nh);
        }
        return (alpha, w, h);
    }

    // ---- the disk cache and the in-process memo ----

    /// <summary>Where the masks are kept (<c>MEITOU_TRIM_CACHE</c>).</summary>
    public static string CacheRoot { get; set; } = Environment.GetEnvironmentVariable("MEITOU_TRIM_CACHE")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Meitou", "cardtrim");

    const uint Magic = 0x4D52544D;   // "MTRM"
    static readonly ConcurrentDictionary<(string Path, byte Cutoff), Lazy<FoliageCardMask?>> Memo = new();
    static long built, hits, bytesWritten;
    static double buildMs, loadMs;

    /// <summary>Masks built, read from the cache, and the cache bytes written (for the log).</summary>
    public static string Summary => $"{Interlocked.Read(ref built)} built ({buildMs:0} ms), {Interlocked.Read(ref hits)} from the cache ({loadMs:0} ms), {Interlocked.Read(ref bytesWritten) / 1024} KB written";

    /// <summary>The mask of the texture file at <paramref name="path"/> for a cut-out at <paramref name="threshold"/> (0-1), from the cache or built and cached; null when the file cannot be decoded. Thread-safe.</summary>
    public static FoliageCardMask? Obtain(string path, float threshold)
    {
        byte cutoff = CutoffOf(threshold);
        return Memo.GetOrAdd((path.ToLowerInvariant(), cutoff), _ => new Lazy<FoliageCardMask?>(() => Load(path, cutoff))).Value;
    }

    static FoliageCardMask? Load(string path, byte cutoff)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var file = File.ReadAllBytes(path);
            string cache = PathFor(Path.GetFileNameWithoutExtension(path), file, cutoff);
            if (TryRead(cache) is { } cached)
            {
                lock (Memo) { hits++; loadMs += watch.Elapsed.TotalMilliseconds; }
                return cached;
            }
            var (alpha, w, h) = DecodeAlpha(file);
            var mask = FromAlpha(alpha, w, h, cutoff, CoarsestSize);
            long bytes = Write(cache, mask);
            lock (Memo) { built++; buildMs += watch.Elapsed.TotalMilliseconds; bytesWritten += bytes; }
            return mask;
        }
        catch (Exception e) when (e is DdsFormatException or InvalidOperationException or IOException or ArgumentException or UnauthorizedAccessException or OutOfMemoryException)
        {
            return null;
        }
    }

    public static string PathFor(string name, byte[] fileBytes, byte cutoff)
    {
        var hash = Convert.ToHexString(SHA256.HashData(fileBytes))[..24].ToLowerInvariant();
        var safe = new string(name.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray());
        if (safe.Length > 40) safe = safe[..40];
        return Path.Combine(CacheRoot, $"{safe}_{hash}_c{cutoff}_s{CoarsestSize}_v{Version}.mtrm");
    }

    /// <summary>The cached mask, or null when there is no readable file of this version.</summary>
    public static FoliageCardMask? TryRead(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var reader = new BinaryReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
            if (reader.ReadUInt32() != Magic || reader.ReadInt32() != Version) return null;
            int w = reader.ReadInt32(), h = reader.ReadInt32(), count = reader.ReadInt32();
            if (w < 1 || h < 1 || w > MaxCells || h > MaxCells || count < 1 || count > 16) return null;
            var levels = new bool[count][];
            int length = (w * h + 7) / 8;
            for (int l = 0; l < count; l++)
            {
                var bits = reader.ReadBytes(length);
                if (bits.Length != length) return null;
                var cells = levels[l] = new bool[w * h];
                for (int i = 0; i < cells.Length; i++) cells[i] = (bits[i >> 3] & (1 << (i & 7))) != 0;
            }
            return new FoliageCardMask(w, h, levels);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or EndOfStreamException) { return null; }
    }

    /// <summary>Writes a mask (to a temporary file, then renamed); the bytes written, 0 when it could not be.</summary>
    public static long Write(string path, FoliageCardMask mask)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + "." + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + ".tmp";
            long bytes = 20;
            using (var writer = new BinaryWriter(new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None)))
            {
                writer.Write(Magic);
                writer.Write(Version);
                writer.Write(mask.Width);
                writer.Write(mask.Height);
                writer.Write(mask.LevelCount);
                foreach (var cells in mask.Levels)
                {
                    var bits = new byte[(cells.Length + 7) / 8];
                    for (int i = 0; i < cells.Length; i++) if (cells[i]) bits[i >> 3] |= (byte)(1 << (i & 7));
                    writer.Write(bits);
                    bytes += bits.Length;
                }
            }
            File.Move(temporary, path, true);
            return bytes;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return 0; }
    }
}
