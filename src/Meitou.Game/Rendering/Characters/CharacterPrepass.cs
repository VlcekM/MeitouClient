namespace Meitou.Game.Rendering.Characters;

/// <summary>
/// The characters' depth prepass switch (docs/character-renderer.md "Depth prepass"): where the backend writes the colour pass's depth itself
/// (<c>IGpuServices.DiscardNeedsLateDepth</c>, Metal), the main view draws the characters' depth first and shades only what lies at it.
/// <c>MEITOU_CHARACTER_PREPASS=0</c> draws without it, for comparing.
/// </summary>
public static class CharacterPrepass
{
    /// <summary>
    /// The prepass is drawn where it applies (true, the default).
    /// </summary>
    public static bool Enabled { get; set; } = Environment.GetEnvironmentVariable("MEITOU_CHARACTER_PREPASS") != "0";
}
