using System.Numerics;
using Meitou.Game.Rendering.Overlays;

namespace Meitou.Game.Host.Photo;

/// <summary>
/// The composition grids photo mode lays over the picture (never in a saved one).
/// </summary>
public enum PhotoGrid
{
    /// <summary>
    /// No grid.
    /// </summary>
    Off,

    /// <summary>
    /// Lines at the thirds.
    /// </summary>
    Thirds,

    /// <summary>
    /// Lines at the golden section (0.382 and 0.618).
    /// </summary>
    Golden,

    /// <summary>
    /// A cross through the centre.
    /// </summary>
    Cross,

    /// <summary>
    /// The two diagonals.
    /// </summary>
    Diagonals,
}

/// <summary>
/// The frame bars photo mode keeps over the picture, saved ones included: the picture's shape, the rest black.
/// </summary>
public enum PhotoFrame
{
    /// <summary>
    /// No bars: the whole picture.
    /// </summary>
    Off,

    /// <summary>
    /// 2.39:1.
    /// </summary>
    Cinema,

    /// <summary>
    /// 16:9.
    /// </summary>
    Wide,

    /// <summary>
    /// 4:3.
    /// </summary>
    Standard,

    /// <summary>
    /// 1:1.
    /// </summary>
    Square,

    /// <summary>
    /// 4:5.
    /// </summary>
    Portrait,
}

/// <summary>
/// The grids and frame bars of photo mode, drawn through the overlay over the finished picture.
/// </summary>
static class PhotoGuides
{
    /// <summary>
    /// The width over the height of each frame (0 for off): 2.39:1, 16:9, 4:3, 1:1, 4:5.
    /// </summary>
    public static float Aspect(PhotoFrame frame) => frame switch
    {
        PhotoFrame.Cinema => 2.39f,
        PhotoFrame.Wide => 16f / 9,
        PhotoFrame.Standard => 4f / 3,
        PhotoFrame.Square => 1,
        PhotoFrame.Portrait => 4f / 5,
        _ => 0,
    };

    /// <summary>
    /// The part of a <paramref name="width"/> by <paramref name="height"/> picture the frame leaves: the whole picture when the frame is off or is no
    /// narrower than the picture's own shape is wide, else centred bars at top and bottom or at the sides. Pixels: left, top, right, bottom.
    /// </summary>
    public static Vector4 Window(PhotoFrame frame, int width, int height)
    {
        float aspect = Aspect(frame);
        float own = width / (float)Math.Max(height, 1);
        if (aspect <= 0) return new Vector4(0, 0, width, height);

        if (aspect > own)
        {
            float shown = width / aspect;
            float bar = MathF.Round((height - shown) / 2);
            return new Vector4(0, bar, width, height - bar);
        }

        float across = height * aspect;
        float side = MathF.Round((width - across) / 2);
        return new Vector4(side, 0, width - side, height);
    }

    /// <summary>
    /// Black over what the frame cuts off.
    /// </summary>
    public static void DrawFrame(DebugOverlay o, PhotoFrame frame, int width, int height)
    {
        if (frame == PhotoFrame.Off) return;

        var w = Window(frame, width, height);
        var black = new Vector4(0, 0, 0, 1);
        if (w.Y > 0)
        {
            o.Rect(0, 0, width, w.Y, black);
            o.Rect(0, w.W, width, height, black);
        }

        if (w.X > 0)
        {
            o.Rect(0, 0, w.X, height, black);
            o.Rect(w.Z, 0, width, height, black);
        }
    }

    /// <summary>
    /// The focus point's mark: the four corners of a square round it, white with a dark edge.
    /// </summary>
    public static void DrawReticle(DebugOverlay o, Vector2 at, float half, float thickness)
    {
        var ink = new Vector4(1, 1, 1, 0.85f);
        float arm = half * 0.45f;
        float t = thickness / 2;
        for (int corner = 0; corner < 4; corner++)
        {
            int sx = (corner & 1) == 0 ? -1 : 1;
            int sy = (corner & 2) == 0 ? -1 : 1;
            float cx = at.X + sx * half;
            float cy = at.Y + sy * half;

            // A dark edge under each stroke, so the mark shows on sand and sky alike.
            for (int pass = 0; pass < 2; pass++)
            {
                float e = pass == 0 ? t : 0;
                var colour = pass == 0 ? new Vector4(0, 0, 0, 0.55f) : ink;
                o.Rect(MathF.Min(cx, cx - sx * arm) - e, cy - t - e, MathF.Max(cx, cx - sx * arm) + e, cy + t + e, colour);
                o.Rect(cx - t - e, MathF.Min(cy, cy - sy * arm) - e, cx + t + e, MathF.Max(cy, cy - sy * arm) + e, colour);
            }
        }
    }

    /// <summary>
    /// The grid's lines inside the picture window <paramref name="w"/> (<see cref="Window"/>): white with a dark edge, so they show on sky and shade.
    /// </summary>
    public static void DrawGrid(DebugOverlay o, PhotoGrid grid, Vector4 w, float thickness)
    {
        if (grid == PhotoGrid.Off) return;

        float x0 = w.X;
        float y0 = w.Y;
        float x1 = w.Z;
        float y1 = w.W;
        switch (grid)
        {
            case PhotoGrid.Thirds:
                Cuts(o, x0, y0, x1, y1, [1f / 3, 2f / 3], thickness);
                break;
            case PhotoGrid.Golden:
                Cuts(o, x0, y0, x1, y1, [1 - 0.618034f, 0.618034f], thickness);
                break;
            case PhotoGrid.Cross:
                Cuts(o, x0, y0, x1, y1, [0.5f], thickness);
                break;
            case PhotoGrid.Diagonals:
                Stroke(o, x0, y0, x1, y1, thickness);
                Stroke(o, x1, y0, x0, y1, thickness);
                break;
        }
    }

    /// <summary>
    /// A vertical and a horizontal line at each fraction <paramref name="at"/> of the window.
    /// </summary>
    static void Cuts(DebugOverlay o, float x0, float y0, float x1, float y1, float[] at, float thickness)
    {
        foreach (float t in at)
        {
            float x = MathF.Round(x0 + (x1 - x0) * t);
            float y = MathF.Round(y0 + (y1 - y0) * t);
            Stroke(o, x, y0, x, y1, thickness);
            Stroke(o, x0, y, x1, y, thickness);
        }
    }

    /// <summary>
    /// A white line over a dark one a pixel down and right.
    /// </summary>
    static void Stroke(DebugOverlay o, float ax, float ay, float bx, float by, float thickness)
    {
        o.Line(ax + 1, ay + 1, bx + 1, by + 1, thickness, new Vector4(0, 0, 0, 0.35f));
        o.Line(ax, ay, bx, by, thickness, new Vector4(1, 1, 1, 0.7f));
    }
}
