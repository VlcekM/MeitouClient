using System.Numerics;

namespace Meitou.Game.Rendering.Post;

/// <summary>
/// The letterbox (docs/render-post.md "Letterbox"): black bars that leave the world's picture the shape the player chose, drawn as the post
/// chain's last pass (<see cref="PostProcessShaders.Letterbox"/>), after the anti-aliasing and the heat haze so their edges stay straight and
/// sharp, and under the interface. One place for both backends' chains (<see cref="PostProcess"/> and <see cref="PostComposite"/>).
/// </summary>
static class Letterbox
{
    /// <summary>
    /// The narrowest and the widest picture shape the setting takes (4:3 to Ultra Panavision), and the one it starts at (anamorphic widescreen).
    /// </summary>
    public const float MinAspect = 1.33f, MaxAspect = 2.76f, DefaultAspect = 2.39f;

    /// <summary>
    /// The bars of a <paramref name="width"/> x <paramref name="height"/> picture: the share of its height each of the top and bottom bars takes
    /// (x) and the share of its width each side bar takes (y), one of them 0: a shape wider than the picture gives bars above and below, a
    /// narrower one bars at the sides. Zero, no bar, with the switch off, a picture already of that shape, or a frame that is not
    /// <paramref name="whole"/>: a whole view of a scene (not a tiled shot's tile, not the title).
    /// </summary>
    public static Vector2 Bars(PostOptions o, bool whole, int width, int height)
    {
        if (!o.Letterbox || !whole || width <= 0 || height <= 0) return Vector2.Zero;

        float picture = width / (float)height, shape = Math.Clamp(o.LetterboxAspect, MinAspect, MaxAspect);
        float rows = MathF.Max(1 - picture / shape, 0) * 0.5f, columns = MathF.Max(1 - shape / picture, 0) * 0.5f;

        // Less than half a pixel: the picture has the shape.
        return new Vector2(rows * height < 0.5f ? 0 : rows, columns * width < 0.5f ? 0 : columns);
    }
}
