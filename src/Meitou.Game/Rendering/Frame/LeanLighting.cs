namespace Meitou.Game.Rendering.Frame;

/// <summary>
/// Whether this frame lights its world with no sun shadows (the shadow pass off, its receivers' term 1) and no probe GI (no probes, so no traced sun
/// or contact sun either): the lighting's lookups for them are dead, and the objects, the terrain and the characters draw with programs built with
/// <c>LIGHTING_LEAN</c> (<c>AtmosphereShaders.kenshiLight</c>), which leave them out. The picture is the same; the programs are smaller
/// (docs/render-objects.md "Lean lighting"). Set by the frame before it draws (<c>WorldFrame.PrepareTracedSun</c>).
/// </summary>
public static class LeanLighting
{
    /// <summary>
    /// The frame's lighting has no sun shadows and no probe GI.
    /// </summary>
    public static bool Active { get; internal set; }

    /// <summary>
    /// The lean programs may be used (the A/B <c>lean-lighting</c>; false: always the full ones).
    /// </summary>
    public static bool Enabled { get; set; } = true;

    /// <summary>
    /// <paramref name="glsl"/> with <c>LIGHTING_LEAN</c> defined after its version line.
    /// </summary>
    public static string Define(string glsl) => glsl.Insert(glsl.IndexOf('\n') + 1, "#define LIGHTING_LEAN\n");
}
