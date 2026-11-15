using Meitou.Game.Rendering.Gpu;

namespace Meitou.Game.Rendering.Frame;

/// <summary>
/// The see-through materials' opacity map and dither (Meitou, issue #562; docs/render-objects.md "See-through items"). The main view blends such
/// a surface over the finished opaque scene; where there is no blended pass (the shadow casters, the water reflection, icons, portraits) a
/// fragment is kept where the pattern's value is under the opacity. The pattern is the LOD cross-fade's interleaved gradient noise and never
/// moves: one that changed each frame made every pixel's depth flip between the surface and what is behind it, which DLSS and frame generation
/// drew as ghosts (Observed, a see-through bottle indoors).
/// </summary>
static class OpacityDither
{
    /// <summary>
    /// The record field naming a see-through material's opacity map (MATERIAL_SPECS_CLOTHING; Meitou's own, the original never reads it): its red
    /// channel is the opacity, 1 solid and 0 clear.
    /// </summary>
    public const string MapField = "opacity map";

    /// <summary>
    /// The GLSL function <c>opacityDither()</c>, 0 to 1: a fragment is dropped where it is not under the opacity. Starts on a line of its own,
    /// whatever text it is appended to.
    /// </summary>
    public const string Function = """

        float opacityDither() { return fract(52.9829189 * fract(dot(gl_FragCoord.xy, vec2(0.06711056, 0.00583715)))); }

        """;

    /// <summary>
    /// The blended pass's state changes: over the scene by the fragment's alpha, the destination's alpha kept (the scene's is the character
    /// mask).
    /// </summary>
    public static readonly BlendState Blend = new(true, BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha, AlphaSrc: BlendingFactor.Zero,
        AlphaDst: BlendingFactor.One);
}
