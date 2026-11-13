namespace Meitou.Game.Rendering.Objects;

/// <summary>
/// Debug switches for the interior view's lighting (agent control command <c>interior-debug</c>, docs/agent-playtest.md); all off = as shipped.
/// </summary>
public static class InteriorDebug
{
    /// <summary>
    /// The room-grid focus test (<c>InteriorVolume.Covered</c>) ignores floor, stairs and roof surfaces above the viewed floor, which the view hides.
    /// </summary>
    public static bool CoverIgnoresHiddenFloors;

    /// <summary>
    /// The shadow passes leave out what the view hides (instead of keeping it so a lower floor stays shaded under its ceiling).
    /// </summary>
    public static bool ShadowsLeaveOutHidden;

    /// <summary>
    /// Parts whose mesh path contains this text are treated as hidden by the floor view (to find which part draws a surface), or null.
    /// </summary>
    public static string? HideMesh;

    /// <summary>
    /// Colour batches are drawn in the order they were met, not nearest first (to find out whether coplanar parts depend on the draw order).
    /// </summary>
    public static bool NoObjectSort;
}
