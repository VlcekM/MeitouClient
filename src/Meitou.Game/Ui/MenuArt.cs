using System.Numerics;
using Meitou.Data.Textures;
using Meitou.Data.Ui;

namespace Meitou.Game.Ui;

/// <summary>
/// The Meitou menu's surfaces, baked on the CPU as sRGB images with straight alpha (docs/meitou-ui.md, "Surfaces").
/// </summary>
static class MenuArt
{
    /// <summary>
    /// The paper card (kit .mt-save): the paper crop stretched whole, brightened by <paramref name="bright"/>, with its edge drawn inside (2px dark
    /// line, 1px faint line, 10px wear), corners rounded to 10, over a drop shadow (6 down, ~14 soft, 55% black) in a margin of
    /// <paramref name="margin"/> pixels.
    /// </summary>
    public static RgbaImage Card(RgbaImage atlas, UiRect paper, int width, int height, float s, float margin, float bright)
    {
        int m = (int)MathF.Ceiling(margin), w = width + 2 * m, h = height + 2 * m;
        var image = new RgbaImage(w, h);
        float radius = 10 * s;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float px = x + 0.5f - m, py = y + 0.5f - m;
                float d = RoundedDistance(px, py, width, height, radius);

                // The shadow: the card's shape 6 lower, faded over ~14.
                float shadow = 0.55f * Smooth(1 - Math.Clamp((RoundedDistance(px, py - 6 * s, width, height, radius) + 3 * s) / (14 * s), 0, 1));
                var colour = Vector4.Zero;
                float cover = Math.Clamp(0.5f - d, 0, 1);
                if (cover > 0)
                {
                    var c = Sample(atlas, paper, px / width, py / height) * bright;
                    float inside = -d;
                    c = Mix(c, new(60, 35, 20), 0.35f * Math.Clamp(1 - inside / (10 * s), 0, 1));
                    if (inside < 3 * s) c = Mix(c, new(20, 8, 6), 0.25f);
                    if (inside < 2 * s) c = Mix(c, new(20, 8, 6), 0.9f);
                    colour = new(c, cover);
                }

                Put(image, x, y, Over(colour, new(0, 0, 0, shadow)));
            }

        return image;
    }

    /// <summary>
    /// A plate button (kit .mt-btn): the 9-slice skin (6-pixel slices) stretched to size at <paramref name="bright"/> brightness, corners rounded to
    /// 7.
    /// </summary>
    public static RgbaImage Plate(RgbaImage atlas, UiRect skin, int width, int height, float s, float bright)
    {
        var image = new RgbaImage(width, height);
        float slice = 6 * s, radius = 7 * s;
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                float px = x + 0.5f, py = y + 0.5f;
                float cover = Math.Clamp(0.5f - RoundedDistance(px, py, width, height, radius), 0, 1);
                float sx = skin.X + NineSliceMap(px, width, slice, skin.Width);
                float sy = skin.Y + NineSliceMap(py, height, slice, skin.Height);
                var sample = SampleAlpha(atlas, sx, sy);
                Put(image, x, y, new(new Vector3(sample.X, sample.Y, sample.Z) * bright, sample.W * cover));
            }

        return image;
    }

    /// <summary>
    /// One axis of the 9-slice: the 6-pixel edges kept, the middle stretched.
    /// </summary>
    static float NineSliceMap(float p, float size, float slice, float source)
    {
        if (p < slice) return p / slice * 6;
        if (p > size - slice) return source - 6 + (p - (size - slice)) / slice * 6;
        return 6 + (p - slice) / Math.Max(1, size - 2 * slice) * (source - 12);
    }

    /// <summary>
    /// A flat chip with rounded corners (the save card's key badge), with an optional 1px rim.
    /// </summary>
    public static RgbaImage Chip(int width, int height, float radius, Vector4 fill, Vector4? rim)
    {
        var image = new RgbaImage(width, height);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                float d = RoundedDistance(x + 0.5f, y + 0.5f, width, height, radius);
                var c = rim is { } r && d > -1 ? Over(r, fill) : fill;
                Put(image, x, y, c with { W = c.W * Math.Clamp(0.5f - d, 0, 1) });
            }

        return image;
    }

    /// <summary>
    /// The vignette behind the stack (kit: radial-gradient(ellipse 620px 470px at 4% 100%, .93 at 25%, .6 at 55%, clear at 80%) in #080605), baked
    /// for the area it covers: the ellipse's 80% extent from its centre at the image's bottom left, scaled to the frame by the caller.
    /// </summary>
    public static RgbaImage Vignette(int width, int height)
    {
        var image = new RgbaImage(width, height);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                // The image spans 0..0.8 of the ellipse's radii (its left 4% of the frame is folded into the first column).
                float t = MathF.Sqrt(MathF.Pow((x + 0.5f) / width * 0.8f, 2) + MathF.Pow((height - y - 0.5f) / height * 0.8f, 2));
                float a = t < 0.25f ? 0.93f
                    : t < 0.55f ? 0.93f + (0.6f - 0.93f) * (t - 0.25f) / 0.3f
                    : t < 0.8f ? 0.6f * (1 - (t - 0.55f) / 0.25f)
                    : 0;
                Put(image, x, y, new(8 / 255f, 6 / 255f, 5 / 255f, a));
            }

        return image;
    }

    /// <summary>
    /// Signed distance from the edge of a <paramref name="w"/>×<paramref name="h"/> rectangle at the origin with corners rounded to
    /// <paramref name="r"/> (negative inside).
    /// </summary>
    static float RoundedDistance(float x, float y, float w, float h, float r)
    {
        float qx = MathF.Abs(x - w / 2) - (w / 2 - r), qy = MathF.Abs(y - h / 2) - (h / 2 - r);
        return MathF.Sqrt(MathF.Max(qx, 0) * MathF.Max(qx, 0) + MathF.Max(qy, 0) * MathF.Max(qy, 0)) + MathF.Min(MathF.Max(qx, qy), 0) - r;
    }

    /// <summary>
    /// Smoothstep of <paramref name="t"/>.
    /// </summary>
    static float Smooth(float t) => t * t * (3 - 2 * t);

    /// <summary>
    /// <paramref name="a"/> moved <paramref name="t"/> of the way to the 0-255 colour <paramref name="b"/>.
    /// </summary>
    static Vector3 Mix(Vector3 a, Vector3 b, float t) => a + (b / 255f - a) * t;

    /// <summary>
    /// Paper colour (0..1 sRGB) at <paramref name="u"/>, <paramref name="v"/> across a crop, bilinear.
    /// </summary>
    static Vector3 Sample(RgbaImage atlas, UiRect crop, float u, float v)
    {
        var c = SampleAlpha(atlas, crop.X + Math.Clamp(u, 0, 1) * crop.Width, crop.Y + Math.Clamp(v, 0, 1) * crop.Height);
        return new(c.X, c.Y, c.Z);
    }

    /// <summary>
    /// The atlas at pixel position <paramref name="x"/>, <paramref name="y"/> (0..1 sRGB with alpha), bilinear.
    /// </summary>
    static Vector4 SampleAlpha(RgbaImage atlas, float x, float y)
    {
        x -= 0.5f;
        y -= 0.5f;
        int x0 = (int)MathF.Floor(x), y0 = (int)MathF.Floor(y);
        float fx = x - x0, fy = y - y0;
        Vector4 At(int px, int py)
        {
            px = Math.Clamp(px, 0, atlas.Width - 1);
            py = Math.Clamp(py, 0, atlas.Height - 1);
            int i = (py * atlas.Width + px) * 4;
            return new Vector4(atlas.Pixels[i], atlas.Pixels[i + 1], atlas.Pixels[i + 2], atlas.Pixels[i + 3]) / 255f;
        }

        return Vector4.Lerp(Vector4.Lerp(At(x0, y0), At(x0 + 1, y0), fx), Vector4.Lerp(At(x0, y0 + 1), At(x0 + 1, y0 + 1), fx), fy);
    }

    /// <summary>
    /// <paramref name="top"/> over <paramref name="under"/>, straight alpha.
    /// </summary>
    static Vector4 Over(Vector4 top, Vector4 under)
    {
        float a = top.W + under.W * (1 - top.W);
        if (a <= 0) return Vector4.Zero;

        var rgb = (new Vector3(top.X, top.Y, top.Z) * top.W + new Vector3(under.X, under.Y, under.Z) * under.W * (1 - top.W)) / a;
        return new(rgb, a);
    }

    /// <summary>
    /// Writes colour <paramref name="c"/> (0..1) to pixel (<paramref name="x"/>, <paramref name="y"/>).
    /// </summary>
    static void Put(RgbaImage image, int x, int y, Vector4 c)
    {
        int i = (y * image.Width + x) * 4;
        image.Pixels[i] = (byte)Math.Clamp(c.X * 255 + 0.5f, 0, 255);
        image.Pixels[i + 1] = (byte)Math.Clamp(c.Y * 255 + 0.5f, 0, 255);
        image.Pixels[i + 2] = (byte)Math.Clamp(c.Z * 255 + 0.5f, 0, 255);
        image.Pixels[i + 3] = (byte)Math.Clamp(c.W * 255 + 0.5f, 0, 255);
    }

    /// <summary>
    /// Rust under the kit's dark tint, with a rounded inner rim and a soft drop shadow.
    /// </summary>
    public static RgbaImage Window(RgbaImage atlas) => WindowSurface(atlas, 128, 128, 1);

    /// <summary>
    /// A window plate of <paramref name="width"/> by <paramref name="height"/> at scale <paramref name="s"/>: the rust sheet under the dark tint, a
    /// rounded rim and a soft drop shadow in a 16-unit margin.
    /// </summary>
    public static RgbaImage WindowSurface(RgbaImage atlas, int width, int height, float s)
    {
        int margin = (int)(16 * s);
        var image = new RgbaImage(width + margin * 2, height + margin * 2);
        for (int y = 0; y < image.Height; y++)
            for (int x = 0; x < image.Width; x++)
            {
                float px = x + 0.5f - margin, py = y + 0.5f - margin;
                float distance = RoundedDistance(px, py, width, height, 10 * s);
                float shadow = 0.55f * Smooth(1 - Math.Clamp((RoundedDistance(px, py - 6 * s, width, height, 10 * s) + 3 * s) / (14 * s), 0, 1));

                // The full rust crop contains a welded T joint. Use the uninterrupted sheet to its right.
                var texture = Sample(atlas, new(1620, 136, 200, 510), px / width, py / height);
                var colour = Vector3.Lerp(texture, new Vector3(14, 11, 9) / 255, 0.86f);
                if (distance > -s) colour = Vector3.Lerp(colour, new Vector3(175, 166, 139) / 255, 0.12f);
                Put(image, x, y, Over(new(colour, Math.Clamp(0.5f - distance, 0, 1)), new(0, 0, 0, shadow)));
            }

        return image;
    }
}