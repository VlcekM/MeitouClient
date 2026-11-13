using Meitou.Game.Rendering.Post;
namespace Meitou.Game.Rendering.Shadows;

/// <summary>
/// The sun's shadows as one setting (docs/render-shadows.md "Modes and resources"), for a settings control to bind to: <see cref="Off"/> draws no shadow maps and traces no rays
/// (the sun is shaded by the clouds and the ground's own relief alone; the light shafts and the lit dust then see no shadow), <see cref="Cascades"/> the shadow maps, the game's or
/// Meitou's by the <c>shadows</c> switch, <see cref="Traced"/> rays to the sun (needs ray queries and Indirect light; without them the frame falls back to the cascades).
/// </summary>
public enum SunShadowMode { Off, Cascades, Traced }

/// <summary>Reads and sets <see cref="SunShadowMode"/> over the switches it stands for: the shadow pass's <see cref="ShadowPass.Enabled"/>, and <see cref="PostOptions.TracedSun"/>.</summary>
public static class SunShadows
{
    public static SunShadowMode Get(ShadowPass? shadow, PostOptions post) =>
        shadow is not { Enabled: true } ? SunShadowMode.Off : post.TracedSun ? SunShadowMode.Traced : SunShadowMode.Cascades;

    /// <summary>Sets <paramref name="mode"/>. Traced runs on the Meitou shadows' uniforms, so it turns the <c>shadows</c> switch to Meitou.</summary>
    public static void Set(ShadowPass? shadow, PostOptions post, SunShadowMode mode)
    {
        post.TracedSun = mode == SunShadowMode.Traced;
        if (shadow is null) return;
        shadow.Enabled = mode != SunShadowMode.Off;
        if (mode == SunShadowMode.Traced) shadow.Meitou = true;
    }
}
