using System.Collections.Concurrent;
using Meitou.Data.Textures;

namespace Meitou.Game.Rendering.Frame;

/// <summary>
/// Coverage-preserving alpha for a cut-out texture (docs/render-foliage.md "Far grass"; Castano, "Computing Alpha Mipmaps"): a texture's mips average
/// its alpha, so a thin mask (wiry grass) falls under a fixed cut a few levels down and what it draws thins out and vanishes with distance. Each
/// level's alpha is scaled as it is loaded so that the cut keeps the share of texels the top level keeps; the top level is not touched, and a level
/// that already keeps as much is left alone. The scale is worked out once per file from all its levels.
/// </summary>
static class AlphaCover
{
    static readonly ConcurrentDictionary<(string Path, int Cut), float[]> Tables = new();

    /// <summary>
    /// Scales the alpha of the levels <paramref name="kept"/> holds (the file's lower levels: <paramref name="whole"/> describes all of them) in
    /// place. BC2 and BC3 only (what the game's cut-out sprites are stored as); other formats are left as they are.
    /// </summary>
    public static void Apply(string path, DdsFile kept, DdsFile whole, float cut)
    {
        if (cut <= 0 || kept.Format is not (DdsFormat.Bc2 or DdsFormat.Bc3)) return;
        float[] scale = Tables.GetOrAdd((path, CutByte(cut)), key => Table(key.Path, key.Cut));
        int dropped = whole.MipCount - kept.MipCount;
        foreach (var surface in kept.Surfaces)
        {
            int level = surface.Level + dropped;
            float k = level < scale.Length ? scale[level] : 1;
            if (k > 1.001f) Scale(kept.Format, kept.Data.AsSpan(surface.Offset, surface.Length), k);
        }
    }

    static int CutByte(float cut) => Math.Clamp((int)MathF.Ceiling(cut * 255 - 1e-4f), 1, 255);

    /// <summary>
    /// The scale of each level of the file: the cut over the threshold at which the level keeps the top level's share (1 for the top level, and where
    /// the level keeps that share at the cut already).
    /// </summary>
    static float[] Table(string path, int cut)
    {
        var dds = DdsReader.ReadFile(path);
        var scale = new float[dds.MipCount];
        Array.Fill(scale, 1f);
        Span<int> histogram = stackalloc int[256];
        double share = Share(dds, 0, histogram, cut);
        if (share <= 0) return scale;
        for (int level = 1; level < dds.MipCount && level < dds.Surfaces.Count; level++)
        {
            Histogram(dds, level, histogram);
            long texels = 0, above = 0;
            for (int a = 0; a < 256; a++) texels += histogram[a];
            int threshold = 255;
            for (; threshold >= 1; threshold--)
            {
                above += histogram[threshold];
                if (above >= share * texels) break;
            }

            threshold = Math.Clamp(threshold, 1, cut);
            scale[level] = cut / (float)threshold;
        }

        Console.WriteLine($"textures  cover kept in {Path.GetFileName(path)} ({dds.Format}, {share:P1} past the cut): scale {string.Join(" ", scale.Skip(1).Take(8).Select(k => k.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)))}");
        return scale;
    }

    static double Share(DdsFile dds, int level, Span<int> histogram, int cut)
    {
        Histogram(dds, level, histogram);
        long texels = 0, above = 0;
        for (int a = 0; a < 256; a++)
        {
            texels += histogram[a];
            if (a >= cut) above += histogram[a];
        }

        return texels == 0 ? 0 : above / (double)texels;
    }

    static void Histogram(DdsFile dds, int level, Span<int> histogram)
    {
        histogram.Clear();
        byte[] pixels = DdsDecoder.Decode(dds, 0, level).Pixels;
        for (int i = 3; i < pixels.Length; i += 4) histogram[pixels[i]]++;
    }

    /// <summary>
    /// Scales a level's stored alpha by <paramref name="k"/> (above 1), capped at opaque. BC3: the two endpoints of each block's alpha (the values
    /// between follow them); a block whose eight-value endpoints meet at the cap is all opaque, and its indices are cleared, since equal endpoints
    /// would switch it to the six-value layout. BC2: each texel's four bits.
    /// </summary>
    static void Scale(DdsFormat format, Span<byte> blocks, float k)
    {
        for (int at = 0; at + 16 <= blocks.Length; at += 16)
        {
            var alpha = blocks.Slice(at, 8);
            if (format == DdsFormat.Bc3)
            {
                bool eight = alpha[0] > alpha[1];
                alpha[0] = (byte)Math.Min(255, (int)MathF.Round(alpha[0] * k));
                alpha[1] = (byte)Math.Min(255, (int)MathF.Round(alpha[1] * k));
                if (eight && alpha[0] <= alpha[1]) alpha.Slice(2, 6).Clear();
            }
            else
            {
                for (int i = 0; i < 8; i++)
                {
                    int low = Math.Min(15, (int)MathF.Round((alpha[i] & 15) * k)), high = Math.Min(15, (int)MathF.Round((alpha[i] >> 4) * k));
                    alpha[i] = (byte)(low | high << 4);
                }
            }
        }
    }
}
