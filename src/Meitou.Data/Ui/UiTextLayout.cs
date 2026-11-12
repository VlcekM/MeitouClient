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
    /// The rectangle a control's text lays out in: its skin's first text part aligned in its bounds (clipped to them), less its <c>TextPadding</c>.
    /// </summary>
    public static UiRect ContentRect(MyGuiResources resources, UiControl control)
    {
        var area = control.Bounds;
        if (resources.Skins.TryGetValue(control.Skin, out var skin) && skin.Parts.FirstOrDefault(p => p.Type is "SimpleText" or "EditText") is { } text)
            area = text.Offset.AlignTo(area, skin.Width, skin.Height, text.Align).Intersect(area);
        if (control.Properties.TryGetValue("TextPadding", out var padding)) area = UiInsets.Parse(padding).Apply(area);

        return area;
    }
}