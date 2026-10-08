using Meitou.Data.Textures;

namespace Meitou.Rendering;

/// <summary>The picture comparison of the A/B mode: counts of differing pixels and a heat map. Pure, tested without a GPU.</summary>
public static class PictureDiff
{
    /// <summary>
    /// Compares two RGBA8 pictures of the same size by the largest difference over the colour channels of each pixel (alpha ignored). The heat map is
    /// black where equal, then blue, cyan, yellow, red, white from a difference of 1/255 to 32/255 and above.
    /// </summary>
    public static (PictureDiffResult Result, byte[] Heat) Compare(byte[] a, byte[] b, int width, int height)
    {
        if (a.Length != b.Length || a.Length < width * height * 4) throw new ArgumentException("the pictures differ in size");
        var heat = new byte[width * height * 4];
        long over1 = 0, over4 = 0, over12 = 0, sum = 0;
        int max = 0;
        for (int p = 0; p < width * height; p++)
        {
            int i = p * 4;
            int dr = Math.Abs(a[i] - b[i]), dg = Math.Abs(a[i + 1] - b[i + 1]), db = Math.Abs(a[i + 2] - b[i + 2]);
            int d = Math.Max(dr, Math.Max(dg, db));
            sum += dr + dg + db;
            if (d >= 1) over1++;
            if (d >= 4) over4++;
            if (d >= 12) over12++;
            if (d > max) max = d;
            var (r, g, bl) = Ramp(d);
            heat[i] = r; heat[i + 1] = g; heat[i + 2] = bl; heat[i + 3] = 255;
        }
        var result = new PictureDiffResult { Width = width, Height = height, Over1 = over1, Over4 = over4, Over12 = over12, MaxDiff = max, MeanDiff = sum / (3.0 * width * height) };
        return (result, heat);
    }

    static (byte, byte, byte) Ramp(int d)
    {
        if (d <= 0) return (0, 0, 0);
        double t = Math.Min(d / 32.0, 1);
        // Piecewise linear through blue, cyan, yellow, red, white.
        (double R, double G, double B)[] stops = [(0, 0, 0.6), (0, 0.9, 1), (1, 1, 0), (1, 0, 0), (1, 1, 1)];
        double x = t * (stops.Length - 1);
        int k = Math.Min((int)x, stops.Length - 2);
        double f = x - k;
        var (r0, g0, b0) = stops[k];
        var (r1, g1, b1) = stops[k + 1];
        return ((byte)(255 * (r0 + (r1 - r0) * f)), (byte)(255 * (g0 + (g1 - g0) * f)), (byte)(255 * (b0 + (b1 - b0) * f)));
    }

    /// <summary>Writes RGBA8 rows in the bottom-up order of <see cref="FramebufferCapture.Read"/> as a PNG (rows flipped, alpha opaque).</summary>
    public static void SavePng(string path, byte[] bottomUp, int width, int height)
    {
        var flipped = new byte[width * height * 4];
        for (int y = 0; y < height; y++) bottomUp.AsSpan((height - 1 - y) * width * 4, width * 4).CopyTo(flipped.AsSpan(y * width * 4));
        for (int i = 3; i < flipped.Length; i += 4) flipped[i] = 255;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        PngWriter.Write(path, width, height, flipped);
    }
}
