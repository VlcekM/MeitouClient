namespace Meitou.Game.Rendering.Sky;

/// <summary>
/// The <c>grazesun</c> switch (Meitou, optional, off): the sun's light takes a surface's normal map less and less as the sun nears the surface's
/// own plane (<c>atmoSunNormal</c> in <see cref="AtmosphereShaders"/>; docs/render-shadows.md "Soft grazing sun").
/// </summary>
public static class GrazingSun
{
    /// <summary>
    /// Whether the switch is on.
    /// </summary>
    public static bool Enabled { get; set; }
}
