using System.Numerics;
using Meitou.Game.Rendering.Overlays;

namespace Meitou.Game.Host.Photo;

/// <summary>
/// The composition grids photo mode lays over the picture (never in a saved one).
/// </summary>
public enum PhotoGrid
{
    Off, Thirds, Golden, Cross, Diagonals,
}

/// <summary>
/// The frame bars photo mode keeps over the picture, saved ones included: the picture's shape, the rest black.
/// </summary>
public enum PhotoFrame
{
    Off, Cinema, Wide, Standard, Square, Portrait,
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
        float aspect = Aspect(frame), own = width / (float)Math.Max(height, 1);
        if (aspect <= 0) return new Vector4(0, 0, width, height);
        if (aspect > own)
        {
            float shown = width / aspect, bar = MathF.Round((height - shown) / 2);
            return new Vector4(0, bar, width, height - bar);
        }
        float across = height * aspect, side = MathF.Round((width - across) / 2);
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
    /// The grid's lines inside the picture window <paramref name="w"/> (<see cref="Window"/>): white with a dark edge, so they show on sky and shade.
    /// </summary>
    public static void DrawGrid(DebugOverlay o, PhotoGrid grid, Vector4 w, float thickness)
    {
        if (grid == PhotoGrid.Off) return;
        float x0 = w.X, y0 = w.Y, x1 = w.Z, y1 = w.W;
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

    static void Cuts(DebugOverlay o, float x0, float y0, float x1, float y1, float[] at, float thickness)
    {
        foreach (float t in at)
        {
            float x = MathF.Round(x0 + (x1 - x0) * t), y = MathF.Round(y0 + (y1 - y0) * t);
            Stroke(o, x, y0, x, y1, thickness);
            Stroke(o, x0, y, x1, y, thickness);
        }
    }

    static void Stroke(DebugOverlay o, float ax, float ay, float bx, float by, float thickness)
    {
        o.Line(ax + 1, ay + 1, bx + 1, by + 1, thickness, new Vector4(0, 0, 0, 0.35f));
        o.Line(ax, ay, bx, by, thickness, new Vector4(1, 1, 1, 0.7f));
    }
}
