using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace Meitou.Data.Ui;

/// <summary>
/// The UI mode (<c>--ui</c>, Tab; docs/native-ui.md): which presentation the title, scene transitions and settings use. Both modes use the installed
/// MyGUI layouts for the HUD and windows.
/// </summary>
public enum UiMode
{
    /// <summary>
    /// The original presentation of the title, scene transitions and settings, where implemented.
    /// </summary>
    Faithful,

    /// <summary>
    /// Meitou's own title, scene transitions and settings.
    /// </summary>
    Meitou
}

/// <summary>
/// Top-left coordinates, in pixels or fractions of the parent.
/// </summary>
public readonly record struct UiRect(float X, float Y, float Width, float Height)
{
    /// <summary>
    /// The overlap of this rectangle and <paramref name="other"/>; zero size when they do not overlap.
    /// </summary>
    public UiRect Intersect(UiRect other)
    {
        float x = Math.Max(X, other.X), y = Math.Max(Y, other.Y);
        return new(x, y, Math.Max(0, Math.Min(X + Width, other.X + other.Width) - x), Math.Max(0, Math.Min(Y + Height, other.Y + other.Height) - y));
    }

    /// <summary>
    /// Whether the point is inside the rectangle (left and top edges inside); an empty rectangle holds nothing.
    /// </summary>
    public bool Contains(float x, float y) => Width > 0 && Height > 0 && x >= X && y >= Y && x < X + Width && y < Y + Height;
    /// <summary>
    /// This rectangle placed in <paramref name="parent"/>: offset by its corner, and with <paramref name="real"/> (fractions) scaled by its size,
    /// each product truncated to whole pixels as MyGUI's <c>CoordConverter::convertFromRelative</c> does (<c>int(fraction * size)</c>).
    /// </summary>
    public UiRect RelativeTo(UiRect parent, bool real) => real
        ? new(parent.X + MathF.Truncate(X * parent.Width), parent.Y + MathF.Truncate(Y * parent.Height), MathF.Truncate(Width * parent.Width),
            MathF.Truncate(Height * parent.Height))
        : new(parent.X + X, parent.Y + Y, Width, Height);

    /// <summary>
    /// Four whitespace-separated numbers (x, y, width, height); the default for null. <see cref="InvalidDataException"/> for another count or a
    /// non-finite value.
    /// </summary>
    public static UiRect Parse(string? value)
    {
        if (value is null) return default;
        var parts = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 4) throw new InvalidDataException($"Expected four coordinates: {value}");
        var n = parts.Select(p => float.Parse(p, CultureInfo.InvariantCulture)).ToArray();
        if (n.Any(v => !float.IsFinite(v))) throw new InvalidDataException("Non-finite UI coordinate.");
        return new(n[0], n[1], n[2], n[3]);
    }

    /// <summary>
    /// This rectangle (laid out in a parent <paramref name="referenceWidth"/> x <paramref name="referenceHeight"/>) aligned in
    /// <paramref name="parent"/> as MyGUI 3.2.2 aligns a child when its parent is resized (<c>Widget::_setAlign</c>, <c>SubSkin::_setAlign</c>, the same
    /// rule): a stretched axis grows by the parent's growth, Right / Bottom move by it, Left / Top stay, and a centred axis (neither of its two
    /// words) is put in the middle of the parent, (parent - size) / 2 truncated toward zero, whatever its position was.
    /// </summary>
    public UiRect AlignTo(UiRect parent, float referenceWidth, float referenceHeight, string align)
    {
        var flags = Flags(align);
        var (x, width) = AlignAxis(X, Width, parent.Width, parent.Width - referenceWidth, flags & Align.HStretch, Align.Left, Align.Right);
        var (y, height) = AlignAxis(Y, Height, parent.Height, parent.Height - referenceHeight, flags & Align.VStretch, Align.Top, Align.Bottom);
        return new(parent.X + x, parent.Y + y, Math.Max(0, width), Math.Max(0, height));
    }

    /// <summary>
    /// Whether <paramref name="align"/> stretches an axis: MyGUI's <c>Widget::_setAlign</c> then sizes the widget (<c>setSize</c> / <c>setCoord</c>),
    /// which aligns its own children in turn; a widget that only moves (<c>setPosition</c>) leaves them as they are.
    /// </summary>
    public static bool Stretches(string align)
    {
        var flags = Flags(align);
        return (flags & Align.HStretch) == Align.HStretch || (flags & Align.VStretch) == Align.VStretch;
    }

    /// <summary>
    /// Whether <paramref name="align"/> stretches the horizontal axis (a piece so aligned keeps its distance to both side edges).
    /// </summary>
    public static bool StretchesWidth(string align) => (Flags(align) & Align.HStretch) == Align.HStretch;

    /// <summary>
    /// One axis of <see cref="AlignTo"/>: the start and length of a piece at <paramref name="start"/>, <paramref name="length"/> long, in a parent
    /// <paramref name="parent"/> long that grew by <paramref name="growth"/>, by the axis' bits <paramref name="flags"/> (<paramref name="near"/>,
    /// <paramref name="far"/>, both, or neither).
    /// </summary>
    static (float Start, float Length) AlignAxis(float start, float length, float parent, float growth, Align flags, Align near, Align far)
    {
        if (flags == (near | far)) return (start, length + growth);
        if (flags == far) return (start + growth, length);
        if (flags == near) return (start, length);

        return (MathF.Truncate((parent - length) / 2), length);
    }

    /// <summary>
    /// MyGUI's <c>Align</c> bits; an axis with neither of its two is centred.
    /// </summary>
    [Flags]
    enum Align
    {
        /// <summary>
        /// Keeps the piece's distance to the left edge (with <see cref="Right"/>: grows its width with the parent).
        /// </summary>
        Left = 1,

        /// <summary>
        /// Keeps the piece's distance to the right edge.
        /// </summary>
        Right = 2,

        /// <summary>
        /// Keeps the piece's distance to the top edge (with <see cref="Bottom"/>: grows its height with the parent).
        /// </summary>
        Top = 4,

        /// <summary>
        /// Keeps the piece's distance to the bottom edge.
        /// </summary>
        Bottom = 8,

        /// <summary>
        /// Both horizontal bits.
        /// </summary>
        HStretch = Left | Right,

        /// <summary>
        /// Both vertical bits.
        /// </summary>
        VStretch = Top | Bottom,
    }

    /// <summary>
    /// The bits of an align string as MyGUI's <c>Align::parse</c> reads it (the words ORed, unknown ones ignored, so "Right" alone centres
    /// vertically), read in place: splitting it allocated per control per frame (docs/bench.md "Play benchmark"). An empty string is a missing
    /// <c>align</c> attribute: <c>Default</c>, Left Top.
    /// </summary>
    static Align Flags(string align)
    {
        if (align.Length == 0) return Align.Left | Align.Top;

        Align flags = 0;
        var text = align.AsSpan();
        foreach (var range in text.SplitAny(" \t\r\n"))
            flags |= text[range] switch
            {
                "Left" or "ALIGN_LEFT" => Align.Left,
                "Right" or "ALIGN_RIGHT" => Align.Right,
                "HStretch" or "ALIGN_HSTRETCH" => Align.HStretch,
                "Top" or "ALIGN_TOP" => Align.Top,
                "Bottom" or "ALIGN_BOTTOM" => Align.Bottom,
                "VStretch" or "ALIGN_VSTRETCH" => Align.VStretch,
                "Stretch" or "ALIGN_STRETCH" => Align.HStretch | Align.VStretch,
                "Default" or "ALIGN_DEFAULT" => Align.Left | Align.Top,
                _ => 0,
            };
        return flags;
    }
}

/// <summary>
/// A <c>Widget</c> element of a layout: its type, name, skin, layer, align, position (fractions of the parent when <paramref name="RealPosition"/>),
/// its <c>Property</c> and <c>UserString</c> values and its child widgets.
/// </summary>
public sealed record MyGuiWidget(string Type, string Name, string Skin, string Layer, string Align,
    UiRect Position, bool RealPosition, IReadOnlyDictionary<string, string> Properties,
    IReadOnlyDictionary<string, string> UserStrings, IReadOnlyList<MyGuiWidget> Children)
{
    /// <summary>
    /// This widget, then every widget below it, each before its children.
    /// </summary>
    public IEnumerable<MyGuiWidget> DescendantsAndSelf() => new[] { this }.Concat(Children.SelectMany(c => c.DescendantsAndSelf()));
}

/// <summary>
/// A MyGUI layout: its version and its root widgets.
/// </summary>
public sealed record MyGuiLayout(string Version, IReadOnlyList<MyGuiWidget> Roots)
{
    /// <summary>
    /// Every widget, each before its children (depth first).
    /// </summary>
    public IReadOnlyList<MyGuiWidget> Widgets => Index().All;
    /// <summary>
    /// The first widget of <see cref="Widgets"/> named <paramref name="name"/>.
    /// </summary>
    public MyGuiWidget? Find(string name) => Index().ByName.GetValueOrDefault(name);

    /// <summary>
    /// The flattened tree, made once per tree (the HUD asks it every frame). Kept by the roots list rather than in a field, which a <c>with</c> copy
    /// would carry over to other roots and which would take part in the record's equality.
    /// </summary>
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IReadOnlyList<MyGuiWidget>, WidgetIndex> Indexes = new();
    /// <summary>
    /// The <see cref="WidgetIndex"/> of <see cref="Roots"/>, made on first use.
    /// </summary>
    WidgetIndex Index() => Indexes.GetValue(Roots, static roots => new WidgetIndex(roots));

    /// <summary>
    /// The widget tree flattened once.
    /// </summary>
    sealed class WidgetIndex
    {
        /// <summary>
        /// Every widget, each before its children.
        /// </summary>
        public readonly MyGuiWidget[] All;
        /// <summary>
        /// The first widget of each name in <see cref="All"/>.
        /// </summary>
        public readonly Dictionary<string, MyGuiWidget> ByName = new(StringComparer.Ordinal);
        /// <summary>
        /// Flattens the trees under <paramref name="roots"/>.
        /// </summary>
        public WidgetIndex(IReadOnlyList<MyGuiWidget> roots)
        {
            All = [.. roots.SelectMany(w => w.DescendantsAndSelf())];
            foreach (var w in All) ByName.TryAdd(w.Name, w);
        }
    }
}

/// <summary>
/// A <c>BasisSkin</c> of a skin: its type, offset and align, the texture rectangle of each state that has an <c>offset</c>, and every state's
/// properties (with its <c>colour</c> and <c>shift</c>).
/// </summary>
public sealed record MyGuiSkinPart(string Type, UiRect Offset, string Align, IReadOnlyDictionary<string, UiRect> States,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? StateProperties = null);
/// <summary>
/// A <c>ResourceSkin</c>: its name, its size (the reference canvas its parts align to), texture and parts.
/// </summary>
public sealed record MyGuiSkin(string Name, float Width, float Height, string Texture, IReadOnlyList<MyGuiSkinPart> Parts);

/// <summary>
/// Preserves resource XML for image sets, fonts, pointers, settings and templates as well as decoded skins/layers.
/// </summary>
public sealed class MyGuiResources
{
    /// <summary>
    /// The <c>ResourceSkin</c> resources decoded, by name.
    /// </summary>
    public Dictionary<string, MyGuiSkin> Skins { get; } = new(StringComparer.Ordinal);
    /// <summary>
    /// Every <c>Resource</c> element by name, as read.
    /// </summary>
    public Dictionary<string, XElement> Resources { get; } = new(StringComparer.Ordinal);
    /// <summary>
    /// The <c>ResourceLayout</c> resources (widget templates) by name, as read.
    /// </summary>
    public Dictionary<string, XElement> Templates { get; } = new(StringComparer.Ordinal);
    /// <summary>
    /// The <c>Layer</c> names in the order first read.
    /// </summary>
    public List<string> Layers { get; } = [];
    /// <summary>
    /// The full paths of the files read, in reading order.
    /// </summary>
    public List<string> LoadedFiles { get; } = [];
    /// <summary>
    /// Each file read, by full path: its whole <c>MyGUI</c> element.
    /// </summary>
    public Dictionary<string, XElement> Documents { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Resolve every List through the caller's game/mod resource lookup. Cycles and repeats are read once.
    /// </summary>
    public static MyGuiResources Load(string entry, Func<string, string?> resolve, params string[] additionalEntries)
    {
        var result = new MyGuiResources();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Visit(string name)
        {
            var path = resolve(name) ?? throw new FileNotFoundException($"MyGUI resource not found: {name}");
            path = Path.GetFullPath(path);
            if (!visited.Add(path)) return;
            var root = MyGuiReader.ReadFile(path);
            result.LoadedFiles.Add(path);
            result.Documents[path] = new XElement(root);
            foreach (var child in root.Elements())
            {
                if (child.Name == "List") { Visit(MyGuiReader.Attribute(child, "file")); continue; }
                string key = MyGuiReader.Attribute(child, "name");
                if (child.Name == "Layer") { if (!result.Layers.Contains(key)) result.Layers.Add(key); }
                else if (child.Name == "Resource")
                {
                    result.Resources[key] = new XElement(child);
                    if (MyGuiReader.Attribute(child, "type") == "ResourceLayout") result.Templates[key] = new XElement(child);
                    if (MyGuiReader.Attribute(child, "type") == "ResourceSkin") result.Skins[key] = MyGuiReader.Skin(child);
                }
            }
        }
        Visit(entry);
        foreach (string additional in additionalEntries) Visit(additional);
        return result;
    }
}

/// <summary>
/// Reads MyGUI XML files: layouts, layout templates and skins (docs/formats/mygui.md).
/// </summary>
public static class MyGuiReader
{
    /// <summary>
    /// The layout a <c>ResourceLayout</c> resource (a template) holds.
    /// </summary>
    public static MyGuiLayout ReadResourceLayout(XElement resource)
    {
        if (Attribute(resource, "type") != "ResourceLayout") throw new InvalidDataException("Expected ResourceLayout.");
        return new(Attribute(resource, "version"), resource.Elements("Widget").Select(Widget).ToArray());
    }

    /// <summary>
    /// The layout file at <paramref name="path"/>.
    /// </summary>
    public static MyGuiLayout ReadLayout(string path) => Layout(ReadFile(path));
    /// <summary>
    /// The layout in the text <paramref name="xml"/>.
    /// </summary>
    public static MyGuiLayout ParseLayout(string xml)
    {
        using var input = new StringReader(xml);
        return Layout(Read(input));
    }

    /// <summary>
    /// The <c>MyGUI</c> root element of the file at <paramref name="path"/>.
    /// </summary>
    internal static XElement ReadFile(string path)
    {
        using var input = File.OpenText(path);
        return Read(input);
    }

    /// <summary>
    /// The <c>MyGUI</c> root element of an XML document, DTDs refused; <see cref="InvalidDataException"/> for another root.
    /// </summary>
    internal static XElement Read(TextReader input)
    {
        using var reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        var root = XElement.Load(reader);
        if (root.Name != "MyGUI") throw new InvalidDataException("Expected a MyGUI document.");
        return root;
    }

    /// <summary>
    /// The value of attribute <paramref name="name"/>; empty when absent.
    /// </summary>
    internal static string Attribute(XElement e, string name) => (string?)e.Attribute(name) ?? "";
    /// <summary>
    /// The <c>key</c> / <c>value</c> pairs of the <paramref name="kind"/> child elements, a later key replacing an earlier one.
    /// </summary>
    static Dictionary<string, string> Values(XElement e, string kind) => e.Elements(kind)
        .Aggregate(new Dictionary<string, string>(StringComparer.Ordinal), AddValue);

    /// <summary>
    /// Adds a <c>key</c> / <c>value</c> element to <paramref name="map"/>.
    /// </summary>
    static Dictionary<string, string> AddValue(Dictionary<string, string> map, XElement p)
    {
        map[Attribute(p, "key")] = Attribute(p, "value");
        return map;
    }

    /// <summary>
    /// A skin state's <c>Property</c> values, with its <c>colour</c> and <c>shift</c> attributes.
    /// </summary>
    static IReadOnlyDictionary<string, string> SkinStateValues(XElement state)
    {
        var values = Values(state, "Property");
        foreach (string key in new[] { "colour", "shift" }) if (state.Attribute(key) is { } attribute) values[key] = attribute.Value;
        return values;
    }

    /// <summary>
    /// The layout of a <c>MyGUI</c> root of type <c>Layout</c>; <see cref="InvalidDataException"/> for another type.
    /// </summary>
    static MyGuiLayout Layout(XElement root)
    {
        if (Attribute(root, "type") != "Layout") throw new InvalidDataException("Expected MyGUI type Layout.");
        return new(Attribute(root, "version"), root.Elements("Widget").Select(Widget).ToArray());
    }

    /// <summary>
    /// A <c>Widget</c> element and its children; <c>position_real</c> (fractions) is taken over <c>position</c>.
    /// </summary>
    static MyGuiWidget Widget(XElement e) => new(Attribute(e, "type"), Attribute(e, "name"), Attribute(e, "skin"),
        Attribute(e, "layer"), Attribute(e, "align"), UiRect.Parse((string?)e.Attribute("position_real") ?? (string?)e.Attribute("position")),
        e.Attribute("position_real") is not null, Values(e, "Property"), Values(e, "UserString"), e.Elements("Widget").Select(Widget).ToArray());

    /// <summary>
    /// A <c>ResourceSkin</c> element; <see cref="InvalidDataException"/> when its size is not two numbers.
    /// </summary>
    internal static MyGuiSkin Skin(XElement e)
    {
        var size = Attribute(e, "size").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (size.Length != 2) throw new InvalidDataException("Expected two skin size coordinates.");
        return new(Attribute(e, "name"), float.Parse(size[0], CultureInfo.InvariantCulture), float.Parse(size[1], CultureInfo.InvariantCulture),
            Attribute(e, "texture"), e.Elements("BasisSkin").Select(p => new MyGuiSkinPart(Attribute(p, "type"),
                UiRect.Parse((string?)p.Attribute("offset")), Attribute(p, "align"), p.Elements("State").Where(s => s.Attribute("offset") is not null)
                    .ToDictionary(s => Attribute(s, "name"), s => UiRect.Parse(Attribute(s, "offset")), StringComparer.Ordinal),
                p.Elements("State").ToDictionary(s => Attribute(s, "name"), SkinStateValues, StringComparer.Ordinal))).ToArray());
    }
}