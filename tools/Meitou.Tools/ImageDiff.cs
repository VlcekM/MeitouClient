using Meitou.Data.Textures;

/// <summary>
/// Per-pixel comparison of two screenshots of the same size (the renderer parity check): mean absolute difference over
/// the RGB channels in 0..255, the share of pixels whose largest channel difference is above a threshold, the maximum,
/// and optionally a difference image (×4, so small differences show).
/// </summary>
static class ImageDiff
{
    public static int Run(string a, string b, string? diffPng, int threshold = 12)
    {
        var x = TextureLoader.LoadImage(File.ReadAllBytes(a));
        var y = TextureLoader.LoadImage(File.ReadAllBytes(b));
        if (x.Width != y.Width || x.Height != y.Height)
        {
            Console.Error.WriteLine($"size differs: {x.Width}x{x.Height} vs {y.Width}x{y.Height}");
            return 1;
        }

        long sum = 0, over = 0;
        int max = 0;
        var diff = diffPng is null ? null : new byte[x.Width * x.Height * 4];
        for (int i = 0; i < x.Pixels.Length; i += 4)
        {
            int worst = 0;
            for (int c = 0; c < 3; c++)
            {
                int d = Math.Abs(x.Pixels[i + c] - y.Pixels[i + c]);
                sum += d;
                worst = Math.Max(worst, d);
                if (diff is not null)
                    diff[i + c] = (byte)Math.Min(255, d * 4);
            }
            if (diff is not null)
                diff[i + 3] = 255;
            if (worst > threshold)
                over++;
            max = Math.Max(max, worst);
        }

        long pixels = (long)x.Width * x.Height;
        Console.WriteLine($"mean {sum / (3.0 * pixels):0.0000}  over{threshold} {100.0 * over / pixels:0.000}%  max {max}");
        if (diff is not null)
            PngWriter.Write(diffPng!, x.Width, x.Height, diff);
        return 0;
    }
}
