namespace Meitou.Data.Ui;

/// <summary>
/// Kenshi's MyGUI build scales resource pixels when it loads them (its own <c>ScaleManager</c>, not in upstream MyGUI; docs/formats/mygui.md
/// "Align and pixel rounding"): a position is floored, a size is ceiled, each the whole-pixel value times the scale in single precision.
/// </summary>
public static class MyGuiScale
{
    /// <summary>
    /// A scaled position or offset: <c>floorf(value * scale)</c>.
    /// </summary>
    public static float Position(float value, float scale) => MathF.Floor(value * scale);

    /// <summary>
    /// A scaled size: <c>ceilf(value * scale)</c>.
    /// </summary>
    public static float Size(float value, float scale) => MathF.Ceiling(value * scale);

    /// <summary>
    /// A scaled coordinate (<c>ScaleManager::apply</c> of an <c>IntCoord</c>): left and top floored, width and height ceiled.
    /// </summary>
    public static UiRect Coord(UiRect r, float scale) => new(Position(r.X, scale), Position(r.Y, scale), Size(r.Width, scale), Size(r.Height, scale));
}
