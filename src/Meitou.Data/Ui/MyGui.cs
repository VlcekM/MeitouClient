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
    /// This rectangle placed in <paramref name="parent"/>: offset by its corner, and with <paramref name="real"/> (fractions) scaled by its size.
    /// </summary>
    public UiRect RelativeTo(UiRect parent, bool real) => new(parent.X + X * (real ? parent.Width : 1),
        parent.Y + Y * (real ? parent.Height : 1), Width * (real ? parent.Width : 1), Height * (real ? parent.Height : 1));

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
    /// Align a skin piece when its reference canvas is resized.
    /// </summary>
    public UiRect AlignTo(UiRect parent, float referenceWidth, float referenceHeight, string align)
    {
        var flags = Flags(align);
        float dx = parent.Width - referenceWidth, dy = parent.Height - referenceHeight;
        return new(parent.X + X + ((flags & Align.Right) != 0 ? dx : (flags & Align.HCenter) != 0 ? dx / 2 : 0),
            parent.Y + Y + ((flags & Align.Bottom) != 0 ? dy : (flags & Align.VCenter) != 0 ? dy / 2 : 0),
            Math.Max(0, Width + ((flags & Align.HStretch) != 0 ? dx : 0)),
            Math.Max(0, Height + ((flags & Align.VStretch) != 0 ? dy : 0)));
    }

    /// <summary>
    /// The words of a MyGUI <c>align</c> string; none of them keeps the piece at the top left at its size.
    /// </summary>
    [Flags]
    enum Align
    {
        /// <summary>
        /// Keeps the piece's distance to the right edge.
        /// </summary>
        Right = 1,

        /// <summary>
        /// Keeps the piece centred horizontally.
        /// </summary>
        HCenter = 2,

        /// <summary>
        /// Keeps the piece's distance to the bottom edge.
        /// </summary>
        Bottom = 4,

        /// <summary>
        /// Keeps the piece centred vertically.
        /// </summary>
        VCenter = 8,

        /// <summary>
        /// Grows the piece's width with the canvas.
        /// </summary>
        HStretch = 16,

        /// <summary>
        /// Grows the piece's height with the canvas.
        /// </summary>
        VStretch = 32
    }

    /// <summary>
    /// The words of an Align string, read in place: splitting it allocated per control per frame (docs/bench.md "Play benchmark").
    /// </summary>
    static Align Flags(string align)
    {
        Align flags = 0;
        var text = align.AsSpan();
        foreach (var range in text.Split(' '))
            flags |= text[range] switch
            {
                "Right" => Align.Right,
                "HCenter" => Align.HCenter,
                "Bottom" => Align.Bottom,
                "VCenter" => Align.VCenter,
                "Center" => Align.HCenter | Align.VCenter,
                "HStretch" => Align.HStretch,
                "VStretch" => Align.VStretch,
                "Stretch" => Align.HStretch | Align.VStretch,
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