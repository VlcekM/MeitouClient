using System.Numerics;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Sky;

namespace Meitou.Game.Rendering.Terrain;

/// <summary>
/// Sole masks for the blood decal pass. The white masks take the colour of the blood source.
/// </summary>
static class FootprintTexture
{
    /// <summary>
    /// Source dimensions. The rendered size is independent of texture resolution.
    /// </summary>
    internal const int Width = 128, Height = 256;

    /// <summary>
    /// Half the footprint's width and length in world units.
    /// </summary>
    internal const float HalfWidth = 1.125f, HalfLength = 2.25f;

    /// <summary>
    /// Uploads the boot and barefoot masks, left before right in each pair.
    /// </summary>
    internal static SampledImage?[] Create(IGpuContext gpu) =>
    [
        CreateMask(gpu, true, false), CreateMask(gpu, true, true),
        CreateMask(gpu, false, false), CreateMask(gpu, false, true),
    ];

    /// <summary>
    /// Makes an antialiased print with varying blood coverage, mirrored for the right foot.
    /// </summary>
    static SampledImage CreateMask(IGpuContext gpu, bool boot, bool right)
    {
        var rgba = Bake(boot, right, [], flat: false);
        return SampledImage.Rgba8(gpu, Width, Height, rgba, repeat: false, mipmaps: true, name: "blood footprint");
    }

    /// <summary>
    /// Bakes a boot mask from the equipped model's normalized sole outline.
    /// </summary>
    internal static byte[] BakeBoot(Vector2[] outline, bool flat) => Bake(true, false, outline, flat);

    /// <summary>
    /// Bakes a bare foot's mask from the body's normalized sole triangles (<see cref="FootprintSoles"/>): their union, softened at its rim. A union
    /// covering under <see cref="MinBareFill"/> of its convex <paramref name="hull"/> (a robot's foot frame, open between its struts) prints the
    /// hull, flat, instead.
    /// </summary>
    internal static byte[] BakeBare((Vector2 A, Vector2 B, Vector2 C)[] triangles, Vector2[] hull)
    {
        float[] ink = new float[Width * Height];
        foreach (var t in triangles) Fill(ink, t.A, t.B, t.C);
        if (hull.Length >= 3 && Fill(ink) < MinBareFill * HullTexels(hull)) return Bake(true, false, hull, flat: true);

        byte[] rgba = new byte[Width * Height * 4];
        for (int y = 0; y < Height; y++)
        for (int x = 0; x < Width; x++)
        {
            float u = (x + .5f) / Width * 2 - 1, v = (y + .5f) / Height;
            int i = (y * Width + x) * 4;
            rgba[i] = rgba[i + 1] = rgba[i + 2] = 255;
            rgba[i + 3] = (byte)(255 * Soft(ink, x, y) * Coverage(u, v));
        }
        return rgba;
    }

    /// <summary>
    /// Marks the texels whose centre lies in a triangle given in the mask's normalized space (-1 to 1 across and along).
    /// </summary>
    static void Fill(float[] ink, Vector2 a, Vector2 b, Vector2 c)
    {
        (a, b, c) = (Texel(a), Texel(b), Texel(c));
        float area = Cross(b - a, c - a);
        if (MathF.Abs(area) < 1e-6f) return;

        int x0 = Math.Max((int)MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X))), 0);
        int x1 = Math.Min((int)MathF.Ceiling(MathF.Max(a.X, MathF.Max(b.X, c.X))), Width - 1);
        int y0 = Math.Max((int)MathF.Floor(MathF.Min(a.Y, MathF.Min(b.Y, c.Y))), 0);
        int y1 = Math.Min((int)MathF.Ceiling(MathF.Max(a.Y, MathF.Max(b.Y, c.Y))), Height - 1);
        for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++)
        {
            var p = new Vector2(x + .5f, y + .5f);
            float w0 = Cross(b - a, p - a) / area, w1 = Cross(c - b, p - b) / area, w2 = Cross(a - c, p - c) / area;
            if (w0 >= 0 && w1 >= 0 && w2 >= 0) ink[y * Width + x] = 1;
        }
    }

    /// <summary>
    /// The share of its convex hull a bare sole's triangles must cover to print as modelled.
    /// </summary>
    const float MinBareFill = .75f;

    /// <summary>
    /// The texels filled.
    /// </summary>
    static int Fill(float[] ink) => ink.Count(static i => i > 0);

    /// <summary>
    /// The texels inside a convex outline in the mask's normalized space.
    /// </summary>
    static int HullTexels(Vector2[] hull)
    {
        int count = 0;
        for (int y = 0; y < Height; y++)
        for (int x = 0; x < Width; x++)
            if (Inside(hull, new Vector2((x + .5f) / Width * 2 - 1, (y + .5f) / Height * 2 - 1)) > 0) count++;
        return count;
    }

    /// <summary>
    /// A normalized mask point in texels.
    /// </summary>
    static Vector2 Texel(Vector2 p) => new((p.X + 1) / 2 * Width, (p.Y + 1) / 2 * Height);

    /// <summary>
    /// The filled texels averaged over a small disc: an antialiased rim instead of the triangles' stairs.
    /// </summary>
    static float Soft(float[] ink, int x, int y)
    {
        const int r = 2;
        float sum = 0;
        int count = 0;
        for (int dy = -r; dy <= r; dy++)
        for (int dx = -r; dx <= r; dx++)
        {
            if (dx * dx + dy * dy > r * r) continue;

            int sx = x + dx, sy = y + dy;
            if ((uint)sx < Width && (uint)sy < Height) sum += ink[sy * Width + sx];
            count++;
        }
        return sum / count;
    }

    /// <summary>
    /// The 2D cross product.
    /// </summary>
    static float Cross(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;

    /// <summary>
    /// The blood's uneven transfer over the print.
    /// </summary>
    static float Coverage(float u, float v) =>
        .9f + .06f * MathF.Sin(u * 14 + v * 31) * MathF.Sin(u * 9 - v * 17) + .04f * MathF.Sin(u * 36 + v * 77) * MathF.Cos(v * 47 - u * 19);

    /// <summary>
    /// Bakes coverage in CPU memory so model-specific masks can be prepared on a worker.
    /// </summary>
    static byte[] Bake(bool boot, bool right, Vector2[] outline, bool flat)
    {
        byte[] rgba = new byte[Width * Height * 4];
        for (int y = 0; y < Height; y++)
        for (int x = 0; x < Width; x++)
        {
            float u = (x + .5f) / Width * 2 - 1, v = (y + .5f) / Height;
            if (right) u = -u;

            float ink = outline.Length != 0 ? ModelSole(u, v, outline, flat) : boot ? Boot(u, v) : Bare(u, v);
            float coverage = Coverage(u, v);
            int i = (y * Width + x) * 4;
            rgba[i] = rgba[i + 1] = rgba[i + 2] = 255;
            rgba[i + 3] = (byte)(255 * ink * coverage);
        }
        return rgba;
    }

    /// <summary>
    /// A fallback boot outline and shallow tread when the original sole is unavailable.
    /// </summary>
    static float Boot(float u, float v)
    {
        float fore = Contact(u - .06f, v - .29f, .83f, .26f, squared: true);
        float heel = Contact(u + .10f, v - .785f, .64f, .16f, squared: true);
        float mid = Contact(u + .15f, v - .55f, .52f, .22f);
        float shape = MathF.Max(MathF.Max(fore, heel), mid * .7f);
        float lugs = Rib(v - .035f * MathF.Abs(u + .06f) + .018f * MathF.Sin(u * 5 + v * 14), .14f, .047f);
        return shape * (.68f + .32f * lugs);
    }

    /// <summary>
    /// Coverage inside the model's sole, lighter at the instep on taller boots. Flat soles have continuous contact.
    /// </summary>
    static float ModelSole(float u, float v, Vector2[] outline, bool flat)
    {
        float shape = Inside(outline, new Vector2(u, v * 2 - 1));
        if (flat) return shape;

        float waist = (v - .57f) * 8;
        float arch = 1 - .48f * MathF.Exp(-waist * waist);
        return shape * arch;
    }

    /// <summary>
    /// Soft coverage at the perimeter of the projected model's convex sole.
    /// </summary>
    static float Inside(Vector2[] hull, Vector2 at)
    {
        float coverage = 1;
        for (int i = 0; i < hull.Length; i++)
        {
            var edge = hull[(i + 1) % hull.Length] - hull[i];
            var delta = at - hull[i];
            float cross = edge.X * delta.Y - edge.Y * delta.X;
            if (cross <= 0) return 0;
            coverage = MathF.Min(coverage, cross * 37 / edge.Length());
        }
        return coverage;
    }

    /// <summary>
    /// The ball, toes and heel, with lighter contact along the outside of the arch.
    /// </summary>
    static float Bare(float u, float v)
    {
        float heel = Contact(u + .12f, v - .77f, .48f, .17f);
        float ball = Contact(u - .08f, v - .375f, .68f, .18f);
        float arch = Contact(u + .34f, v - .55f, .28f, .21f) * .53f;
        float toes = Contact(u - .51f, v - .13f, .22f, .082f);
        toes = MathF.Max(toes, Contact(u - .18f, v - .151f, .13f, .064f));
        toes = MathF.Max(toes, Contact(u + .08f, v - .18f, .115f, .055f));
        toes = MathF.Max(toes, Contact(u + .31f, v - .211f, .103f, .048f));
        toes = MathF.Max(toes, Contact(u + .49f, v - .247f, .090f, .042f));
        return MathF.Max(MathF.Max(heel, ball), MathF.Max(arch, toes));
    }

    /// <summary>
    /// Coverage of a rounded contact patch. Squared patches describe boot soles, elliptical ones describe skin.
    /// </summary>
    static float Contact(float u, float v, float rx, float ry, bool squared = false)
    {
        float x = u / rx, y = v / ry;
        float xx = x * x, yy = y * y;
        float shape = squared ? xx * xx + yy * yy : xx + yy;
        return Math.Clamp((1 - shape) * 10, 0, 1);
    }

    /// <summary>
    /// Coverage of each raised tread band, separated by the sole's grooves.
    /// </summary>
    static float Rib(float position, float spacing, float halfWidth)
    {
        float phase = position / spacing;
        float distance = MathF.Abs(phase - MathF.Round(phase)) * spacing;
        return Math.Clamp((halfWidth - distance) * 170, 0, 1);
    }
}
