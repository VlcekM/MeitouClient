using System.Globalization;
using System.Security;
using System.Text;

namespace ProvingGrounds.Core.Glue;

/// <summary>
/// Builds a MyGUI layout as XML (task 4.4), one call per original <c>createWidget</c>, so each window of the C++ translates call by call:
/// <c>parent-&gt;createWidget&lt;MyGUI::Button&gt;("Kenshi_Button1", MyGUI::IntCoord(x, y, w, h), align, name)</c> becomes
/// <c>b.Open("Button", "Kenshi_Button1", x, y, w, h, name)</c>, the widget's setters become <see cref="Prop"/> / <see cref="UserString"/> and the
/// children follow before <see cref="Close"/>. The result is read by Meitou's <c>MyGuiReader.ParseLayout</c> (src/Meitou.Data/Ui/MyGui.cs:294;
/// attributes type, name, skin, layer, align, position / position_real, Property and UserString children, :361-363) and registered on the
/// faithful screen host (spike S6, VERIFIED live: a layout from an XML string in code works).
/// <para>Alignment: MyGUI's <c>Align::Default</c> (left top) is written as no attribute; any other <c>MyGUI::Align</c> as its words
/// ("Center", "Left Top", "HStretch VCenter").</para>
/// </summary>
public sealed class LayoutBuilder
{
    readonly StringBuilder sb = new("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<MyGUI type=\"Layout\" version=\"3.2.0\">\n");
    int depth = 1;

    // task 4.6a (found live): an XML parser normalises a raw line break, tab or CR inside an attribute value to a space (XML 1.0 §3.3.3), so a
    // caption with "\n" lost its line breaks in the window; they are written as character references, which the parser keeps
    static string E(string s) => (SecurityElement.Escape(s) ?? "").Replace("\r", "&#13;").Replace("\n", "&#10;").Replace("\t", "&#9;");
    static string F(float v) => v.ToString("0.#####", CultureInfo.InvariantCulture);
    static string I(int v) => v.ToString(CultureInfo.InvariantCulture);

    /// <summary><c>createWidget&lt;T&gt;(skin, IntCoord(x, y, w, h), align, name)</c>: a widget at a pixel position in its parent.</summary>
    public LayoutBuilder Open(string type, string skin, int x, int y, int w, int h, string name, string align = "", string layer = "")
        => Start(type, skin, $"position=\"{I(x)} {I(y)} {I(w)} {I(h)}\"", name, align, layer);

    /// <summary><c>createWidgetReal&lt;T&gt;(skin, x, y, w, h, align, layer, name)</c>: a widget at fractions of its parent (a window of the screen).</summary>
    public LayoutBuilder OpenReal(string type, string skin, float x, float y, float w, float h, string name, string align = "", string layer = "")
        => Start(type, skin, $"position_real=\"{F(x)} {F(y)} {F(w)} {F(h)}\"", name, align, layer);

    LayoutBuilder Start(string type, string skin, string position, string name, string align, string layer)
    {
        Indent();
        sb.Append("<Widget type=\"").Append(E(type)).Append("\" skin=\"").Append(E(skin)).Append("\" ").Append(position)
            .Append(" name=\"").Append(E(name)).Append('"');
        if (align.Length > 0) sb.Append(" align=\"").Append(E(align)).Append('"');
        if (layer.Length > 0) sb.Append(" layer=\"").Append(E(layer)).Append('"');
        sb.Append(">\n");
        depth++;
        return this;
    }

    /// <summary>A setter of the open widget as a MyGUI <c>Property</c> (Caption, TextAlign, ImageTexture, ...; PgCheck ui refuses a key Meitou
    /// does not read, <see cref="MyGuiPropertyKeys"/>).</summary>
    public LayoutBuilder Prop(string key, string value) => Pair("Property", key, value);

    /// <summary><c>setUserString(key, value)</c> of the open widget.</summary>
    public LayoutBuilder UserString(string key, string value) => Pair("UserString", key, value);

    LayoutBuilder Pair(string element, string key, string value)
    {
        if (depth == 1) throw new InvalidOperationException($"{element} '{key}' outside a widget");
        Indent();
        sb.Append('<').Append(element).Append(" key=\"").Append(E(key)).Append("\" value=\"").Append(E(value)).Append("\"/>\n");
        return this;
    }

    /// <summary>Ends the open widget.</summary>
    public LayoutBuilder Close()
    {
        if (depth == 1) throw new InvalidOperationException("Close without an open widget");
        depth--;
        Indent();
        sb.Append("</Widget>\n");
        return this;
    }

    void Indent() => sb.Append(' ', 2 * depth);

    /// <summary>The finished layout; throws while a widget is still open.</summary>
    public string Xml()
    {
        if (depth != 1) throw new InvalidOperationException($"{depth - 1} widget(s) not closed");
        return sb + "</MyGUI>\n";
    }
}

/// <summary>
/// The MyGUI <c>Property</c> keys Meitou 0.17.3 reads from a layout (so a key outside this list does nothing in game). Collected from the
/// readers: the painter and arrange (src/Meitou.Data/Ui/UiDocument.cs, UiDocument.Widgets.cs, MyGuiImages.cs:16-22), the screen host
/// (src/Meitou.Game/Ui/MyGuiScreenHost*.cs: Static, ReadOnly, MultiLine, WordWrap, Password, MaxTextLength, Numeric, DefaultAction, Movable,
/// Snap, MinSize, MaxSize) and the tooltips (src/Meitou.Game/Ui/UiTooltips.cs:141,151, CAP 7.7). PgCheck ui fails a layout using another key.
/// A later task that needs a key Meitou reads adds it here with its citation.
/// </summary>
public static class MyGuiPropertyKeys
{
    static readonly HashSet<string> Known = new(StringComparer.Ordinal)
    {
        // text: UiDocument.cs WidgetCaption (Caption), the painter (FontName, TextColour, TextAlign, TextShadow, FontPixels), UiTextLayout
        "Caption", "FontName", "FontPixels", "TextColour", "TextAlign", "TextShadow", "TextScroll", "WordWrap",
        // edit boxes: MyGuiScreenHost.Keyboard.cs:30, :82-85
        "Static", "ReadOnly", "MultiLine", "Password", "MaxTextLength", "Numeric",
        // state: UiDocument.cs:117 (Visible), :132 (Enabled), WidgetAlpha (Alpha, InheritsAlpha), NeedMouse, Checked / State
        "Visible", "Enabled", "Alpha", "InheritsAlpha", "NeedMouse", "Checked", "StateSelected", "State", "Clipped", "DefaultAction",
        // images: MyGuiImages.cs:16-22 (ImageTexture with ImageRect; ImageGroup/ImageName/ImageFrame), ImageFlipV / ImageBorder / ImageAngle (CAP 7.3)
        "ImageTexture", "ImageRect", "ImageFlipV", "ImageBorder", "ImageBorderScale", "ImageAngle", "ImageGroup", "ImageName", "ImageFrame",
        "Colour", "TileSize", "TileH", "TileV",
        // scroll views: UiDocument.Widgets.cs:502-508
        "VisibleVScroll", "VisibleHScroll", "CanvasAlign",
        // windows: MyGuiScreenHost.cs BeginWindowDrag (Movable), MyGuiScreenHost.Widgets.cs (Snap, MinSize, MaxSize, WindowAction)
        "Movable", "Snap", "MinSize", "MaxSize",
        // buttons / bars: UiDocument.Widgets.cs (ButtonAutoWidth, ButtonWidth, Repeat, MoveToClick)
        "ButtonAutoWidth", "ButtonWidth", "Repeat", "RepeatEnabled", "MoveToClick",
        // tooltips: UiTooltips.cs:141,151 (CAP 7.7)
        "tooltip_type", "tooltip_text", "tooltip_size", "tooltip_data", "tooltip_class",
    };

    /// <summary>Whether Meitou reads <paramref name="key"/> (tooltip_key_N / tooltip_val_N count too).</summary>
    public static bool IsKnown(string key) => Known.Contains(key) || key.StartsWith("tooltip_key_", StringComparison.Ordinal)
        || key.StartsWith("tooltip_val_", StringComparison.Ordinal);

    /// <summary>The keys of <paramref name="keys"/> Meitou does not read, in their order, each once.</summary>
    public static IReadOnlyList<string> Unknown(IEnumerable<string> keys) => [.. keys.Where(k => !IsKnown(k)).Distinct()];
}
