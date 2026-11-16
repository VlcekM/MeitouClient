using System.Globalization;

namespace Meitou.Data.Ui;

/// <summary>
/// Insets from a rectangle's edges, in pixels: a control's <c>TextPadding</c>.
/// </summary>
public readonly record struct UiInsets(float Left, float Top, float Right, float Bottom)
{
    /// <summary>
    /// The same inset on all four edges.
    /// </summary>
    public static UiInsets Uniform(float value) => new(value, value, value, value);

    /// <summary>
    /// <paramref name="bounds"/> shrunk by the insets; left and top are clamped to the size, nothing goes below zero size.
    /// </summary>
    public UiRect Apply(UiRect bounds)
    {
        float left = Math.Clamp(Left, 0, bounds.Width), top = Math.Clamp(Top, 0, bounds.Height);
        return new(bounds.X + left, bounds.Y + top, Math.Max(0, bounds.Width - left - Math.Max(0, Right)),
            Math.Max(0, bounds.Height - top - Math.Max(0, Bottom)));
    }

    /// <summary>
    /// A <c>TextPadding</c> value: 1 (all edges), 2 (left/right, top/bottom) or 4 (left, top, right, bottom) finite non-negative numbers.
    /// </summary>
    public static UiInsets Parse(string value)
    {
        var values = value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(v => float.Parse(v, CultureInfo.InvariantCulture)).ToArray();
        if (values.Any(v => !float.IsFinite(v) || v < 0))
            throw new InvalidDataException("Text padding needs finite nonnegative values.");

        return values.Length switch
        {
            1 => Uniform(values[0]),
            2 => new(values[0], values[1], values[0], values[1]),
            4 => new(values[0], values[1], values[2], values[3]),
            _ => throw new InvalidDataException("Text padding needs 1, 2 or 4 values (left top right bottom).")
        };
    }

    public override string ToString() => FormattableString.Invariant($"{Left} {Top} {Right} {Bottom}");
}

/// <summary>
/// Where a control's text goes inside its bounds.
/// </summary>
public static class UiTextLayout
{
    /// <summary>
    /// The rectangle a control's text lays out in: its <see cref="TextRegion"/> (clipped to its bounds), less its <c>TextPadding</c>.
    /// </summary>
    public static UiRect ContentRect(MyGuiResources resources, UiControl control, float scale = 1)
    {
        var area = control.Bounds;
        if (TextRegion(resources, control, scale) is { } region) area = region.Intersect(area);
        if (control.Properties.TryGetValue("TextPadding", out var padding)) area = UiInsets.Parse(padding).Apply(area);

        return area;
    }

    /// <summary>
    /// The skin's first text part of a control (MyGUI's <c>SimpleText</c> or <c>EditText</c>); null when its skin has none.
    /// </summary>
    public static MyGuiSkinPart? TextPart(MyGuiResources resources, UiControl control) =>
        resources.Skins.TryGetValue(control.Skin, out var skin) ? skin.Parts.FirstOrDefault(p => p.Type is "SimpleText" or "EditText") : null;

    /// <summary>
    /// A control's text region (MyGUI's <c>TextBox::getTextRegion</c>): its skin's text part, in the skin's pixels scaled at load by
    /// <paramref name="scale"/> as Kenshi's MyGUI does (<see cref="MyGuiScale"/>), aligned from the scaled skin size to the control's bounds; null
    /// when its skin has no text part.
    /// </summary>
    public static UiRect? TextRegion(MyGuiResources resources, UiControl control, float scale = 1)
    {
        if (!resources.Skins.TryGetValue(control.Skin, out var skin) || TextPart(resources, control) is not { } text) return null;

        var offset = MyGuiScale.Coord(text.Offset, scale);
        return offset.AlignTo(control.Bounds, MyGuiScale.Size(skin.Width, scale), MyGuiScale.Size(skin.Height, scale), text.Align);
    }
}