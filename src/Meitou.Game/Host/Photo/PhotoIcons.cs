using System.Globalization;
using System.Numerics;
using Meitou.Data.Textures;
using Meitou.Game.Rendering.Ui;

namespace Meitou.Game.Host.Photo;

/// <summary>
/// The icons over the light marks (<see cref="PhotoLights"/>): a bulb for a point light, a spotlight for a spot. Each is two images made once on the CPU
/// from vector outlines (curves flattened to polygons, filled with 4 x 4 supersampling) at two sizes: the body (neutral grey, on a soft dark outline
/// that reads on sand and on sky) and the glow (white, tinted by the light's colour when drawn). The outlines are Google's Material Symbols "lightbulb"
/// and "highlight" (filled, 24 px, viewBox 0 -960 960 960), https://github.com/google/material-design-icons, Apache License 2.0 (the licence text is
/// in that repository); only their path strings are kept here. No file of the game is used.
/// </summary>
static class PhotoIcons
{
    #region State

    /// <summary>
    /// The icon's kinds.
    /// </summary>
    public enum Kind
    {
        /// <summary>
        /// A point light.
        /// </summary>
        Bulb,

        /// <summary>
        /// A spot.
        /// </summary>
        Spot,
    }

    /// <summary>
    /// The side of the picture in icon units: the 24 unit drawing with a margin of 2 each side for the outline.
    /// </summary>
    public const float Extent = 28;

    /// <summary>
    /// The texture sizes in pixels, the small one for icons drawn up to <see cref="SmallestLargeSide"/> pixels.
    /// </summary>
    static readonly int[] Sizes = [64, 128];

    /// <summary>
    /// An icon drawn larger than this many pixels takes the larger texture.
    /// </summary>
    const float SmallestLargeSide = 48;

    /// <summary>
    /// Material Symbols "lightbulb" (filled): the cap, the bar, then the glass.
    /// </summary>
    const string BulbPath = "M480-80q-33 0-56.5-23.5T400-160h160q0 33-23.5 56.5T480-80ZM320-200v-80h320v80H320Zm10-120q-69-41-109.5-110T180-580q0-125 "
        + "87.5-212.5T480-880q125 0 212.5 87.5T780-580q0 81-40.5 150T630-320H330Z";

    /// <summary>
    /// Material Symbols "highlight" (filled): the three rays (left, middle, right), then the head.
    /// </summary>
    const string SpotPath = "m226-640-86-84 56-58 86 86-56 56Zm214-120v-120h80v120h-80Zm296 120-58-56 86-84 56 56-84 84ZM360-80v-200L240-400v-200h480v200"
        + "L600-280v200H360Z";

    #endregion

    #region Textures

    /// <summary>
    /// The texture of <paramref name="kind"/>'s <paramref name="glow"/> (else body) layer for an icon drawn <paramref name="side"/> pixels wide.
    /// </summary>
    public static string Name(Kind kind, bool glow, float side) =>
        $"photo-icon:{kind}:{(glow ? "glow" : "body")}:{(side > SmallestLargeSide ? Sizes[1] : Sizes[0])}";

    /// <summary>
    /// Makes and uploads every icon texture not on the GPU yet.
    /// </summary>
    public static void Ensure(UiPainter painter)
    {
        foreach (var kind in Enum.GetValues<Kind>())
        {
            foreach (int size in Sizes)
            {
                string body = $"photo-icon:{kind}:body:{size}";
                if (painter.HasTexture(body)) continue;

                var (bodyImage, glowImage) = Bake(kind, size);
                painter.SetImage(body, bodyImage, srgb: false);
                painter.SetImage($"photo-icon:{kind}:glow:{size}", glowImage, srgb: false);
            }
        }
    }

    #endregion

    #region Shapes

    /// <summary>
    /// A filled shape of an icon in icon units (the 24 unit drawing): its polygons (non-zero winding) and whether it takes the light's colour.
    /// </summary>
    sealed record Shape(List<Vector2[]> Polygons, bool Glow);

    /// <summary>
    /// The shapes of <paramref name="kind"/>: the bulb's glass takes the light's colour, its cap and bar stay grey; the spotlight's rays take it, its
    /// head stays grey; the spotlight is turned upside down so its beam points down.
    /// </summary>
    static List<Shape> Shapes(Kind kind)
    {
        if (kind == Kind.Bulb)
        {
            var parts = Parse(BulbPath);
            return [new Shape([parts[0]], false), new Shape([parts[1]], false), new Shape([parts[2]], true)];
        }

        // Upside down: the icon shines up, a lamp's spot mostly shines down (a mirror keeps the non-zero fill).
        var spot = Parse(SpotPath).Select(polygon => polygon.Select(p => new Vector2(p.X, 24 - p.Y)).ToArray()).ToList();
        return [new Shape([spot[0]], true), new Shape([spot[1]], true), new Shape([spot[2]], true), new Shape([spot[3]], false)];
    }

    /// <summary>
    /// The subpaths of a Material Symbols path (viewBox 0 -960 960 960: scaled to 24 units, y down from the top) as polygons: M, L, H, V, Q, T and Z,
    /// absolute and relative; a curve is flattened to 8 segments.
    /// </summary>
    static List<Vector2[]> Parse(string path)
    {
        const float unit = 1 / 40f;
        var result = new List<Vector2[]>();
        var current = new List<Vector2>();
        Vector2 at = default, start = default, control = default;
        bool smooth = false;
        int i = 0;
        char command = ' ';
        var numbers = new List<float>();

        while (i < path.Length)
        {
            if (char.IsLetter(path[i]))
            {
                command = path[i++];
                if (char.ToLowerInvariant(command) != 'z') continue;

                if (current.Count > 2) result.Add([.. current]);
                current = [];
                (at, smooth) = (start, false);
                continue;
            }

            if (!Number(path, ref i, out float first)) { i++; continue; }

            char kind = char.ToLowerInvariant(command);
            int need = kind switch { 'q' => 4, 'm' or 'l' or 't' => 2, _ => 1 };
            numbers.Clear();
            numbers.Add(first);
            while (numbers.Count < need && Number(path, ref i, out float next)) numbers.Add(next);

            bool relative = char.IsLower(command);
            var origin = relative ? at : Vector2.Zero;
            switch (kind)
            {
                case 'm':
                    at = start = origin + new Vector2(numbers[0], numbers[1]);
                    current = [at];
                    command = relative ? 'l' : 'L';
                    smooth = false;
                    break;
                case 'l':
                    at = origin + new Vector2(numbers[0], numbers[1]);
                    current.Add(at);
                    smooth = false;
                    break;
                case 'h':
                    at = new Vector2(relative ? at.X + numbers[0] : numbers[0], at.Y);
                    current.Add(at);
                    smooth = false;
                    break;
                case 'v':
                    at = new Vector2(at.X, relative ? at.Y + numbers[0] : numbers[0]);
                    current.Add(at);
                    smooth = false;
                    break;
                case 'q' or 't':
                    // T reflects the last curve's control point about the current point (the point itself after anything else).
                    var c = kind == 'q' ? origin + new Vector2(numbers[0], numbers[1]) : smooth ? 2 * at - control : at;
                    var end = kind == 'q' ? origin + new Vector2(numbers[2], numbers[3]) : origin + new Vector2(numbers[0], numbers[1]);
                    for (int k = 1; k <= 8; k++) current.Add(Quadratic(at, c, end, k / 8f));
                    (control, at, smooth) = (c, end, true);
                    break;
            }
        }

        if (current.Count > 2) result.Add([.. current]);
        return [.. result.Select(polygon => polygon.Select(point => new Vector2(point.X * unit, point.Y * unit + 24)).ToArray())];
    }

    /// <summary>
    /// The point at <paramref name="t"/> of a quadratic Bezier curve.
    /// </summary>
    static Vector2 Quadratic(Vector2 a, Vector2 b, Vector2 c, float t)
    {
        float s = 1 - t;
        return s * s * a + 2 * s * t * b + t * t * c;
    }

    /// <summary>
    /// Reads the number at <paramref name="i"/> (an SVG one: a sign, digits, a point, which may start right after another number), skipping separators.
    /// </summary>
    static bool Number(string text, ref int i, out float value)
    {
        value = 0;
        while (i < text.Length && (text[i] == ' ' || text[i] == ',')) i++;
        int begin = i;
        if (i < text.Length && (text[i] == '-' || text[i] == '+')) i++;

        bool point = false;
        while (i < text.Length && (char.IsAsciiDigit(text[i]) || text[i] == '.' && !point))
        {
            point |= text[i] == '.';
            i++;
        }

        return i > begin && float.TryParse(text.AsSpan(begin, i - begin), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    #endregion

    #region Baking

    /// <summary>
    /// Fills <paramref name="kind"/>'s shapes into two <paramref name="size"/> x <paramref name="size"/> images: the body (neutral shapes over a dark
    /// outline round every shape) and the glow (white, its alpha the coverage).
    /// </summary>
    static (RgbaImage Body, RgbaImage Glow) Bake(Kind kind, int size)
    {
        var shapes = Shapes(kind);
        float scale = size / Extent;
        var neutral = new float[size * size];
        var glow = new float[size * size];
        const int samples = 4;

        foreach (var shape in shapes)
        {
            var target = shape.Glow ? glow : neutral;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float covered = 0;
                    for (int sy = 0; sy < samples; sy++)
                    {
                        for (int sx = 0; sx < samples; sx++)
                        {
                            var p = new Vector2((x + (sx + 0.5f) / samples) / scale - 2, (y + (sy + 0.5f) / samples) / scale - 2);
                            if (Inside(shape.Polygons, p)) covered++;
                        }
                    }

                    target[y * size + x] = Math.Max(target[y * size + x], covered / (samples * samples));
                }
            }
        }

        var outline = Outline(neutral, glow, size, 1.5f * scale);
        var body = new byte[size * size * 4];
        var light = new byte[size * size * 4];
        for (int i = 0; i < size * size; i++)
        {
            // The grey shapes over the dark outline.
            float back = outline[i] * 0.8f, front = neutral[i];
            float alpha = front + back * (1 - front);
            float grey = alpha > 1e-4f ? (0.82f * front + 0.04f * back * (1 - front)) / alpha : 0;
            body[i * 4] = body[i * 4 + 1] = (byte)MathF.Round(grey * 255);
            body[i * 4 + 2] = (byte)MathF.Round(Math.Min(1, grey * 1.03f) * 255);
            body[i * 4 + 3] = (byte)MathF.Round(alpha * 255);
            light[i * 4] = light[i * 4 + 1] = light[i * 4 + 2] = 255;
            light[i * 4 + 3] = (byte)MathF.Round(glow[i] * 255);
        }

        return (new RgbaImage(size, size, body), new RgbaImage(size, size, light));
    }

    /// <summary>
    /// Whether <paramref name="p"/> is inside the polygons by the non-zero winding rule.
    /// </summary>
    static bool Inside(List<Vector2[]> polygons, Vector2 p)
    {
        int winding = 0;
        foreach (var polygon in polygons)
        {
            for (int i = 0; i < polygon.Length; i++)
            {
                var a = polygon[i];
                var b = polygon[(i + 1) % polygon.Length];
                if (a.Y <= p.Y == b.Y <= p.Y) continue;

                float cross = (b.X - a.X) * (p.Y - a.Y) - (p.X - a.X) * (b.Y - a.Y);
                winding += b.Y > a.Y ? (cross > 0 ? 1 : 0) : (cross < 0 ? -1 : 0);
            }
        }

        return winding != 0;
    }

    /// <summary>
    /// The soft outline of the two coverages: their union grown by <paramref name="radius"/> pixels, blurred a pixel.
    /// </summary>
    static float[] Outline(float[] neutral, float[] glow, int size, float radius)
    {
        var union = new float[size * size];
        for (int i = 0; i < union.Length; i++) union[i] = Math.Max(neutral[i], glow[i]);

        int reach = (int)MathF.Ceiling(radius);
        var grown = new float[size * size];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float best = 0;
                for (int dy = -reach; dy <= reach; dy++)
                {
                    for (int dx = -reach; dx <= reach; dx++)
                    {
                        int nx = x + dx, ny = y + dy;
                        if (nx < 0 || ny < 0 || nx >= size || ny >= size) continue;

                        float distance = MathF.Sqrt(dx * dx + dy * dy);
                        if (distance <= radius + 0.5f) best = Math.Max(best, union[ny * size + nx] * Math.Clamp(radius + 0.5f - distance, 0, 1));
                    }
                }

                grown[y * size + x] = best;
            }
        }

        var soft = new float[size * size];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float sum = 0;
                int count = 0;
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx, ny = y + dy;
                        if (nx < 0 || ny < 0 || nx >= size || ny >= size) continue;

                        sum += grown[ny * size + nx];
                        count++;
                    }
                }

                soft[y * size + x] = sum / count;
            }
        }

        return soft;
    }

    #endregion
}
