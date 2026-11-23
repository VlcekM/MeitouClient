using System.Globalization;
using System.Numerics;

namespace Meitou.Game.Ui;

/// <summary>
/// How an extension's line is drawn, shared by item tooltip lines, character panel lines and town panel lines (docs/modding.md "Line style"):
/// <paramref name="Label"/> and <paramref name="Value"/> are text colours (RGB, 0 to 1; alpha is not used; null keeps the panel's own), and
/// <paramref name="Icon"/> an image file drawn in a square before the label (null: none; tooltips draw no icon).
/// </summary>
public sealed record UiLineStyle(Vector4? Label = null, Vector4? Value = null, string? Icon = null)
{
    /// <summary>
    /// The most characters an icon's path keeps; a longer one is dropped.
    /// </summary>
    public const int MaxIconPath = 1024;

    /// <summary>
    /// <paramref name="style"/> as it is drawn: colours not finite dropped and the rest clamped to 0..1, a blank or overlong icon path dropped; null
    /// when nothing is left.
    /// </summary>
    internal static UiLineStyle? Shown(UiLineStyle? style)
    {
        if (style is null) return null;

        var label = Clamp(style.Label);
        var value = Clamp(style.Value);
        string? icon = string.IsNullOrWhiteSpace(style.Icon) || style.Icon.Length > MaxIconPath ? null : style.Icon;
        return label is null && value is null && icon is null ? null : new UiLineStyle(label, value, icon);
    }

    /// <summary>
    /// A colour clamped to 0..1, or null when it is none or not finite.
    /// </summary>
    static Vector4? Clamp(Vector4? colour)
    {
        if (colour is not { } c || !float.IsFinite(c.X + c.Y + c.Z + c.W)) return null;

        return Vector4.Clamp(c, Vector4.Zero, Vector4.One);
    }

    /// <summary>
    /// Adds <paramref name="style"/>'s icon (named <paramref name="name"/>-icon) in a square at the start of <paramref name="row"/>, and returns the
    /// row after it; the row as it is without an icon.
    /// </summary>
    internal static Data.Ui.UiRect PlaceIcon(List<Data.Ui.UiControl> controls, string name, UiLineStyle? style, Data.Ui.UiRect row,
        Data.Ui.UiRect? clip)
    {
        if (style?.Icon is not { } icon || row.Height <= 0 || row.Width <= row.Height) return row;

        var properties = new Dictionary<string, string> { ["ImageTexture"] = Rendering.Ui.UiPainter.IconTexture(icon) };
        var box = row with { Width = row.Height };
        controls.Add(new(name + "-icon", "ImageBox", box, "", "", "", true, properties, Vector4.Zero, Pickable: false, Clip: clip));
        return row with { X = row.X + row.Height, Width = row.Width - row.Height };
    }

    /// <summary>
    /// A colour as MyGUI's <c>TextColour</c> text ("r g b"); null for none.
    /// </summary>
    internal static string? Ink(Vector4? colour)
    {
        if (colour is not { } c) return null;

        return string.Join(' ', new[] { c.X, c.Y, c.Z }.Select(f => f.ToString("0.###", CultureInfo.InvariantCulture)));
    }
}
