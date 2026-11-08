using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace Meitou.Data.Ui;

public enum UiMode { Faithful, Meitou }

/// <summary>Top-left coordinates, in pixels or fractions of the parent.</summary>
public readonly record struct UiRect(float X, float Y, float Width, float Height)
{
    public UiRect Intersect(UiRect other)
    {
        float x = Math.Max(X, other.X), y = Math.Max(Y, other.Y);
        return new(x, y, Math.Max(0, Math.Min(X + Width, other.X + other.Width) - x), Math.Max(0, Math.Min(Y + Height, other.Y + other.Height) - y));
    }
    public bool Contains(float x, float y) => Width > 0 && Height > 0 && x >= X && y >= Y && x < X + Width && y < Y + Height;
    public UiRect RelativeTo(UiRect parent, bool real) => new(parent.X + X * (real ? parent.Width : 1),
        parent.Y + Y * (real ? parent.Height : 1), Width * (real ? parent.Width : 1), Height * (real ? parent.Height : 1));

    public static UiRect Parse(string? value)
    {
        if (value is null) return default;
        var parts = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 4) throw new InvalidDataException($"Expected four coordinates: {value}");
        var n = parts.Select(p => float.Parse(p, CultureInfo.InvariantCulture)).ToArray();
        if (n.Any(v => !float.IsFinite(v))) throw new InvalidDataException("Non-finite UI coordinate.");
        return new(n[0], n[1], n[2], n[3]);
    }

    /// <summary>Align a skin piece when its reference canvas is resized.</summary>
    public UiRect AlignTo(UiRect parent, float referenceWidth, float referenceHeight, string align)
    {
        var flags = Flags(align);
        float dx = parent.Width - referenceWidth, dy = parent.Height - referenceHeight;
        return new(parent.X + X + ((flags & Align.Right) != 0 ? dx : (flags & Align.HCenter) != 0 ? dx / 2 : 0),
            parent.Y + Y + ((flags & Align.Bottom) != 0 ? dy : (flags & Align.VCenter) != 0 ? dy / 2 : 0),
            Math.Max(0, Width + ((flags & Align.HStretch) != 0 ? dx : 0)),
            Math.Max(0, Height + ((flags & Align.VStretch) != 0 ? dy : 0)));
    }

    [Flags]
    enum Align { Right = 1, HCenter = 2, Bottom = 4, VCenter = 8, HStretch = 16, VStretch = 32 }

    // The words of an Align string, read in place: splitting it allocated per control per frame (docs/bench.md "Play benchmark").
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

public sealed record MyGuiWidget(string Type, string Name, string Skin, string Layer, string Align,
    UiRect Position, bool RealPosition, IReadOnlyDictionary<string, string> Properties,
    IReadOnlyDictionary<string, string> UserStrings, IReadOnlyList<MyGuiWidget> Children)
{
    public IEnumerable<MyGuiWidget> DescendantsAndSelf() => new[] { this }.Concat(Children.SelectMany(c => c.DescendantsAndSelf()));
}

public sealed record MyGuiLayout(string Version, IReadOnlyList<MyGuiWidget> Roots)
{
    /// <summary>Every widget, each before its children (depth first).</summary>
    public IReadOnlyList<MyGuiWidget> Widgets => Index().All;
    /// <summary>The first widget of <see cref="Widgets"/> named <paramref name="name"/>.</summary>
    public MyGuiWidget? Find(string name) => Index().ByName.GetValueOrDefault(name);

    /// <summary>
    /// The flattened tree, made once per tree (the HUD asks it every frame). Kept by the roots list rather than in a field, which a <c>with</c> copy would carry over
    /// to other roots and which would take part in the record's equality.
    /// </summary>
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IReadOnlyList<MyGuiWidget>, WidgetIndex> Indexes = new();
    WidgetIndex Index() => Indexes.GetValue(Roots, static roots => new WidgetIndex(roots));

    sealed class WidgetIndex
    {
        public readonly MyGuiWidget[] All;
        public readonly Dictionary<string, MyGuiWidget> ByName = new(StringComparer.Ordinal);
        public WidgetIndex(IReadOnlyList<MyGuiWidget> roots)
        {
            All = [.. roots.SelectMany(w => w.DescendantsAndSelf())];
            foreach (var w in All) ByName.TryAdd(w.Name, w);
        }
    }
}

public sealed record MyGuiSkinPart(string Type, UiRect Offset, string Align, IReadOnlyDictionary<string, UiRect> States,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? StateProperties = null);
public sealed record MyGuiSkin(string Name, float Width, float Height, string Texture, IReadOnlyList<MyGuiSkinPart> Parts);

/// <summary>Preserves resource XML for image sets, fonts, pointers, settings and templates as well as decoded skins/layers.</summary>
public sealed class MyGuiResources
{
    public Dictionary<string, MyGuiSkin> Skins { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, XElement> Resources { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, XElement> Templates { get; } = new(StringComparer.Ordinal);
    public List<string> Layers { get; } = [];
    public List<string> LoadedFiles { get; } = [];
    public Dictionary<string, XElement> Documents { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Resolve every List through the caller's game/mod resource lookup. Cycles and repeats are read once.</summary>
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

public static class MyGuiReader
{
    public static MyGuiLayout ReadResourceLayout(XElement resource)
    {
        if (Attribute(resource, "type") != "ResourceLayout") throw new InvalidDataException("Expected ResourceLayout.");
        return new(Attribute(resource, "version"), resource.Elements("Widget").Select(Widget).ToArray());
    }
    public static MyGuiLayout ReadLayout(string path) => Layout(ReadFile(path));
    public static MyGuiLayout ParseLayout(string xml)
    {
        using var input = new StringReader(xml);
        return Layout(Read(input));
    }

    internal static XElement ReadFile(string path)
    {
        using var input = File.OpenText(path);
        return Read(input);
    }

    internal static XElement Read(TextReader input)
    {
        using var reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        var root = XElement.Load(reader);
        if (root.Name != "MyGUI") throw new InvalidDataException("Expected a MyGUI document.");
        return root;
    }

    internal static string Attribute(XElement e, string name) => (string?)e.Attribute(name) ?? "";
    static Dictionary<string, string> Values(XElement e, string kind) => e.Elements(kind)
        .Aggregate(new Dictionary<string, string>(StringComparer.Ordinal), (map, p) => { map[Attribute(p, "key")] = Attribute(p, "value"); return map; });
    static IReadOnlyDictionary<string, string> SkinStateValues(XElement state)
    {
        var values = Values(state, "Property");
        foreach (string key in new[] { "colour", "shift" }) if (state.Attribute(key) is { } attribute) values[key] = attribute.Value;
        return values;
    }

    static MyGuiLayout Layout(XElement root)
    {
        if (Attribute(root, "type") != "Layout") throw new InvalidDataException("Expected MyGUI type Layout.");
        return new(Attribute(root, "version"), root.Elements("Widget").Select(Widget).ToArray());
    }

    static MyGuiWidget Widget(XElement e) => new(Attribute(e, "type"), Attribute(e, "name"), Attribute(e, "skin"),
        Attribute(e, "layer"), Attribute(e, "align"), UiRect.Parse((string?)e.Attribute("position_real") ?? (string?)e.Attribute("position")),
        e.Attribute("position_real") is not null, Values(e, "Property"), Values(e, "UserString"), e.Elements("Widget").Select(Widget).ToArray());

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