using System.Globalization;
using System.Numerics;
using Meitou.Data.Ui;

namespace Meitou.Ui;

/// <summary>The colour a tooltip text takes: the palette the original's inline colour tags name (<c>data/gui/colours/kenshi_colours.xml</c>); the host maps each to its UI mode's colour.</summary>
public enum TooltipTone { Default, Main, Ink, Title, Bad, BadBright, Good, Grey, GreyBright, Cyan, Grade0, Grade1, Grade2, Grade3, Grade4 }

/// <summary>One tooltip row: a key (left) and an optional value (right), each with the tone of its leading <c>#rrggbb</c> tag.</summary>
public readonly record struct TooltipLine(string Key, string Value = "", TooltipTone KeyTone = TooltipTone.Default, TooltipTone ValueTone = TooltipTone.Default,
    string Suffix = "", TooltipTone SuffixTone = TooltipTone.Default, bool Paragraph = false)
{
    // A paragraph (Paragraph true) is wrapped at the width the other rows give the tooltip: it does not widen it.
    /// <summary>The value and the suffix (the price mark-up after a price) as one text.</summary>
    public string Whole => Value + Suffix;
}

/// <summary>Where a tooltip sits (docs/game/ui-screens.md "Tooltips"): <c>ToolTip</c>/<c>ToolTipDynamic</c> at the pointer, <c>ToolTipFixed</c> on an anchor panel, <c>ToolTipInventory</c> beside the window holding the item.</summary>
public enum TooltipKind { Floating, Fixed, Inventory, Static }

/// <summary>The rectangles of a laid out row, inside <see cref="TooltipLayout.Panel"/>: the key (left aligned) and the value (right aligned) share one area.</summary>
public readonly record struct TooltipRow(UiRect Area);

public sealed record TooltipLayout(UiRect Panel, IReadOnlyList<TooltipRow> Rows, float Offset);

/// <summary>
/// The tooltip runtime's data and layout (docs/game/ui-screens.md "Tooltips"). A widget carries the original's MyGUI user strings as control properties (<c>tooltip_type</c> 0 text, 1 rows,
/// 2 data record, 4 map place; <c>tooltip_text</c>, <c>tooltip_size</c>, <c>tooltip_key_N</c>, <c>tooltip_val_N</c>, <c>tooltip_data</c>) plus Meitou's <c>tooltip_class</c> (<c>fixed</c> / <c>inventory</c>).
/// </summary>
public static class UiTooltips
{
    public const string Type = "tooltip_type", Text = "tooltip_text", Size = "tooltip_size", KeyPrefix = "tooltip_key_", ValuePrefix = "tooltip_val_", Data = "tooltip_data", Class = "tooltip_class";

    /// <summary>MyGUI's <c>ToolTipManager</c> delay before a tooltip shows over a still pointer (<b>Verified</b>, MyGUI 3.2 source).</summary>
    public const float ShowDelaySeconds = 0.5f;

    /// <summary>The HUD <c>TooltipPanel</c>'s box (<c>Kenshi_MainPanel.layout</c>: <c>position_real</c> 0 0 1 0.972222) as a fraction of the panel's height.</summary>
    public const float FixedBoxFraction = 0.972222f;

    /// <summary>The tone of an inline colour tag <c>#rrggbb</c> (the exe's static colour strings), else <see cref="TooltipTone.Default"/>.</summary>
    public static TooltipTone ToneOf(string hex) => hex.ToLowerInvariant() switch
    {
        "#afa68b" => TooltipTone.Main, "#140806" => TooltipTone.Ink, "#492620" => TooltipTone.Title, "#59231a" => TooltipTone.Bad, "#bf4834" => TooltipTone.BadBright,
        "#a8b774" => TooltipTone.Good, "#444444" => TooltipTone.Grey, "#777777" => TooltipTone.GreyBright, "#14ffdc" => TooltipTone.Cyan, _ => TooltipTone.Default,
    };

    /// <summary>Splits a leading <c>#rrggbb</c> colour tag off <paramref name="text"/>.</summary>
    public static (string Text, TooltipTone Tone) SplitTag(string text) =>
        text.Length >= 7 && text[0] == '#' && text.AsSpan(1, 6).IndexOfAnyExcept("0123456789abcdefABCDEF") < 0 ? (text[7..], ToneOf(text[..7])) : (text, TooltipTone.Default);

    /// <summary>The rows a widget's own user strings give (type 0: one row of <c>tooltip_text</c>; type 1: <c>tooltip_size</c> key/value pairs); empty for the other types and for empty text.</summary>
    public static IReadOnlyList<TooltipLine> Lines(IReadOnlyDictionary<string, string> properties)
    {
        string Get(string key) => properties.GetValueOrDefault(key) ?? "";
        switch (Get(Type))
        {
            case "0":
                var (text, tone) = SplitTag(Get(Text));
                return text.Length == 0 ? [] : [new(text, "", tone)];
            case "1":
                if (!int.TryParse(Get(Size), NumberStyles.None, CultureInfo.InvariantCulture, out int count)) return [];
                var lines = new List<TooltipLine>(Math.Min(count, 64));
                for (int i = 0; i < count && i < 64; i++)
                {
                    var (key, keyTone) = SplitTag(Get(KeyPrefix + i.ToString(CultureInfo.InvariantCulture)));
                    var (value, valueTone) = SplitTag(Get(ValuePrefix + i.ToString(CultureInfo.InvariantCulture)));
                    lines.Add(new(key, value, keyTone, valueTone));
                }
                return lines;
            default: return [];
        }
    }

    /// <summary>Sets the user strings of a type 0 tooltip (the original's setter <c>FUN_140920fe0</c>; empty text shows none).</summary>
    public static Dictionary<string, string> TextProperties(string text, string? tooltipClass = null)
    {
        var properties = new Dictionary<string, string> { [Type] = "0", [Text] = text };
        if (tooltipClass is not null) properties[Class] = tooltipClass;
        return properties;
    }

    /// <summary>Sets the user strings of a type 1 tooltip from <paramref name="lines"/> (a tone is written back as its colour tag).</summary>
    public static Dictionary<string, string> RowProperties(IReadOnlyList<(string Key, string Value)> lines, string? tooltipClass = null)
    {
        var properties = new Dictionary<string, string> { [Type] = "1", [Size] = lines.Count.ToString(CultureInfo.InvariantCulture) };
        for (int i = 0; i < lines.Count; i++)
        {
            properties[KeyPrefix + i.ToString(CultureInfo.InvariantCulture)] = lines[i].Key;
            properties[ValuePrefix + i.ToString(CultureInfo.InvariantCulture)] = lines[i].Value;
        }
        if (tooltipClass is not null) properties[Class] = tooltipClass;
        return properties;
    }

    /// <summary>
    /// Lays out a tooltip. The original's metrics (<b>Observed</b>, <c>FUN_140920d10</c>, <c>FUN_1409208d0</c>, <c>FUN_140922680</c>, the <c>vfunc_9</c>s): the widget starts 0.25 x 0.2 of the screen; each row sits
    /// <c>0.025</c> (<c>0.04</c> for the inventory's) x UI scale of that width in from the left, as wide as its key text plus its value text plus two such offsets, as high as its tallest text;
    /// the first row is round(0.1 x widget height) from the top, the next ones round(0.01 x widget height) under the one before, and the panel ends round(0.1 x widget height) under the last row.
    /// <paramref name="measure"/> gives a text's size wrapped to a width (0: unwrapped).
    /// </summary>
    public static TooltipLayout Arrange(IReadOnlyList<TooltipLine> lines, TooltipKind kind, int screenWidth, int screenHeight, Vector2 pointer, UiRect? anchor,
        Func<string, float, Vector2> measure)
    {
        float scale = screenHeight / 1080f, widgetWidth = 0.25f * screenWidth, widgetHeight = 0.2f * screenHeight;
        // A tooltip given a widget of its own (the overview's AI panel) measures its margins from that widget.
        if (kind == TooltipKind.Static && anchor is { } given) (widgetWidth, widgetHeight) = (given.Width, given.Height);
        // The HUD's fixed one is built on the anchor panel's first child, the parchment box of 97.2 % of the panel's height (FUN_14072d3b0 passes it to FUN_140921440).
        if (kind == TooltipKind.Fixed && anchor is { } box) (widgetWidth, widgetHeight) = (box.Width, box.Height * FixedBoxFraction);
        float offset = MathF.Round((kind == TooltipKind.Inventory ? 0.04f : 0.025f) * scale * widgetWidth);
        float margin = MathF.Round(0.1f * widgetHeight), gap = MathF.Round(0.01f * widgetHeight);
        float wrap = Math.Max(1, widgetWidth - 2 * offset);
        var sizes = new (Vector2 Key, Vector2 Value)[lines.Count];
        float widest = 0;
        for (int i = 0; i < lines.Count; i++)
        {
            sizes[i] = lines[i].Paragraph ? default : (measure(lines[i].Key, wrap), lines[i].Whole.Length > 0 ? measure(lines[i].Whole, wrap) : default);
            widest = Math.Max(widest, sizes[i].Key.X + sizes[i].Value.X + 2 * offset);
        }
        float width = kind switch
        {
            TooltipKind.Inventory => Math.Max(0.17f * screenWidth, widest),
            TooltipKind.Fixed => Math.Max(anchor?.Width ?? 0, widest),
            TooltipKind.Static => anchor?.Width ?? widest,
            _ => widest,
        };
        // The text is drawn in the final inner width, which wraps it again: measure the rows once more at that width so the heights match the drawing.
        float inner = Math.Max(1, width - 2 * offset);
        var rows = new List<TooltipRow>(lines.Count);
        float y = margin;
        for (int i = 0; i < lines.Count; i++)
        {
            float keyHeight = measure(lines[i].Key, inner).Y, valueHeight = lines[i].Whole.Length > 0 ? measure(lines[i].Whole, inner).Y : 0;
            float height = Math.Max(keyHeight, valueHeight);
            rows.Add(new(new(offset, y, inner, height)));
            y += height + gap;
        }
        float bottom = lines.Count == 0 ? margin : y - gap;
        float panelHeight = bottom + margin;
        // Fixed: the anchor keeps its height unless the rows (plus the anchor's part outside the box) need more (ToolTipFixed::vfunc_9).
        if (kind == TooltipKind.Fixed && anchor is { } fixedAnchor) panelHeight = Math.Max(panelHeight + fixedAnchor.Height - widgetHeight, fixedAnchor.Height);
        if (kind == TooltipKind.Static && anchor is { } staticAnchor) panelHeight = staticAnchor.Height;
        // Placement: bottom right corner at the pointer, an axis that would leave the screen taking the pointer itself instead (ToolTip / ToolTipDynamic); fixed: the anchor's left, its bottom edge
        // (grown upwards); inventory: from the pointer. Kept on the screen (engine choice).
        float x, top;
        switch (kind)
        {
            case TooltipKind.Fixed when anchor is { } a: (x, top) = (a.X, a.Y + a.Height - panelHeight); break;
            case TooltipKind.Static when anchor is { } s: (x, top) = (s.X, s.Y); break;
            // ToolTipInventory::vfunc_6 (Verified): top left at the item window's top right, or its right edge at the window's left when it would pass the screen's right edge.
            case TooltipKind.Inventory when anchor is { } w: (x, top) = (w.X + w.Width + width > screenWidth ? w.X - width : w.X + w.Width, w.Y); break;
            case TooltipKind.Inventory: (x, top) = (pointer.X, pointer.Y); break;
            default:
                x = pointer.X - width < 0 ? pointer.X : pointer.X - width;
                top = pointer.Y - panelHeight < 0 ? pointer.Y : pointer.Y - panelHeight;
                break;
        }
        x = Math.Clamp(x, 0, Math.Max(0, screenWidth - width));
        top = Math.Clamp(top, 0, Math.Max(0, screenHeight - panelHeight));
        return new(new(x, top, width, panelHeight), rows.Select(r => new TooltipRow(new(x + r.Area.X, top + r.Area.Y, r.Area.Width, r.Area.Height))).ToArray(), offset);
    }
}
