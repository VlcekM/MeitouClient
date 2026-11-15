using System.Numerics;

namespace Meitou.Game.Rendering.Frame;

/// <summary>
/// The see-through materials' dither (Meitou, issue #562; docs/render-objects.md "See-through items"): a fragment is kept where the pattern's
/// value is under the material's opacity, in the colour and shadow passes alike, so the surface keeps its depth and a temporal method averages
/// the kept and dropped pixels into glass. The pattern is the LOD cross-fade's interleaved gradient noise, moved each frame by the view's jitter:
/// a view without one (no temporal method, a shadow cascade, an icon) keeps a still pattern.
/// </summary>
static class OpacityDither
{
    /// <summary>
    /// The record field naming a see-through material's opacity map (MATERIAL_SPECS_CLOTHING; Meitou's own, the original never reads it): its red
    /// channel is the opacity, 1 solid and 0 clear.
    /// </summary>
    public const string MapField = "opacity map";

    /// <summary>
    /// The GLSL function <c>opacityDither()</c>, 0 to 1: a fragment is dropped where it is not under the opacity. <paramref name="jitterNdc"/>
    /// is the expression of the view's jitter in NDC. Starts on a line of its own, whatever text it is appended to.
    /// </summary>
    public static string Function(string jitterNdc) => "\n" + $$"""
        float opacityDither()
        {
            float cell = fract(52.9829189 * fract(dot(gl_FragCoord.xy, vec2(0.06711056, 0.00583715))));
            return fract(cell + fract(dot({{jitterNdc}}, vec2(29173.0, 47119.0))));
        }

        """;

    /// <summary>
    /// The upscaler's subpixel jitter in NDC, as the view constants carry it (<see cref="ViewConstants.JitterNdc"/>).
    /// </summary>
    public static Vector2 JitterNdc(Vector2 jitterPixels, int width, int height) =>
        new(2 * jitterPixels.X / Math.Max(width, 1), 2 * jitterPixels.Y / Math.Max(height, 1));
}
