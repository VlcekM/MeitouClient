using System.Numerics;
using Meitou.Data.Ui;

namespace Meitou.Game.Ui;

/// <summary>
/// Map pan/zoom in normalized texture coordinates. Projection is supplied by the game's adapter.
/// </summary>
public sealed class UiMapView
{
    /// <summary>
    /// The view's centre on the image, 0..1 each way.
    /// </summary>
    public Vector2 Centre { get; private set; } = new(0.5f);

    /// <summary>
    /// The zoom: 1 covers the viewport (or with <see cref="FitWhole"/> fits it).
    /// </summary>
    public float Zoom { get; private set; } = 1;

    /// <summary>
    /// Zoom 1 fits the whole image in the viewport (the longer side shows margins) instead of covering it.
    /// </summary>
    public bool FitWhole { get; init; }

    /// <summary>
    /// The greatest zoom.
    /// </summary>
    public float MaxZoom { get; set; } = 64;

    /// <summary>
    /// Centres the view on image point <paramref name="normalized"/>.
    /// </summary>
    public void CentreOn(Vector2 normalized) => Centre = normalized;

    /// <summary>
    /// The image's on-screen width and height in pixels.
    /// </summary>
    public float Size(UiRect viewport) => (FitWhole ? Math.Min(viewport.Width, viewport.Height) : Math.Max(viewport.Width, viewport.Height)) * Zoom;

    /// <summary>
    /// <paramref name="value"/> as a centre that keeps the image over the viewport (an axis narrower than it centred, with <see cref="FitWhole"/>).
    /// </summary>
    Vector2 Clamp(Vector2 value, UiRect viewport)
    {
        float size = Size(viewport);
        if (size <= 0) return value;
        if (!FitWhole) return Vector2.Clamp(value, new(0.5f / Zoom), new(1 - 0.5f / Zoom));

        // On an axis where the image is narrower than the viewport it stays centred.
        var half = new Vector2(viewport.Width, viewport.Height) / (2 * size);
        float x = half.X >= 0.5f ? 0.5f : Math.Clamp(value.X, half.X, 1 - half.X);
        float y = half.Y >= 0.5f ? 0.5f : Math.Clamp(value.Y, half.Y, 1 - half.Y);
        return new(x, y);
    }

    /// <summary>
    /// The image's on-screen rectangle.
    /// </summary>
    public UiRect ImageBounds(UiRect viewport)
    {
        var centre = Clamp(Centre, viewport);
        float size = Size(viewport);
        return new(viewport.X + viewport.Width / 2 - centre.X * size, viewport.Y + viewport.Height / 2 - centre.Y * size, size, size);
    }

    /// <summary>
    /// The image point (0..1 each way) under <paramref name="pixel"/>.
    /// </summary>
    public Vector2 At(UiRect viewport, Vector2 pixel)
    {
        var image = ImageBounds(viewport);
        return new((pixel.X - image.X) / image.Width, (pixel.Y - image.Y) / image.Height);
    }

    /// <summary>
    /// Zooms by <paramref name="factor"/> (within 1 and <see cref="MaxZoom"/>) keeping the image point under <paramref name="pixel"/> in place.
    /// </summary>
    public void ZoomAt(UiRect viewport, Vector2 pixel, float factor)
    {
        if (viewport.Width <= 0 || viewport.Height <= 0 || !float.IsFinite(factor) || factor <= 0) return;

        var before = At(viewport, pixel);
        Zoom = Math.Clamp(Zoom * factor, 1, Math.Max(1, MaxZoom));
        float size = Size(viewport);
        var delta = pixel - new Vector2(viewport.X + viewport.Width / 2, viewport.Y + viewport.Height / 2);
        Centre = Clamp(before - delta / size, viewport);
    }

    /// <summary>
    /// Pans the view by <paramref name="delta"/> pixels.
    /// </summary>
    public void Pan(UiRect viewport, Vector2 delta)
    {
        if (viewport.Width > 0 && viewport.Height > 0)
            Centre = Clamp(Clamp(Centre, viewport) - delta / Size(viewport), viewport);
    }
}