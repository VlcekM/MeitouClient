using System.Globalization;
using System.Xml.Linq;

namespace Meitou.Data.Ui;

/// <summary>
/// The FreeType hinting modes of a <c>ResourceTrueTypeFont</c> (<c>Hinting</c> property; any other value is <see cref="UseNative"/>).
/// </summary>
public enum MyGuiHinting
{
    /// <summary>
    /// <c>use_native</c>: FreeType's default loading, the font's bytecode hinting (<c>FT_LOAD_DEFAULT</c>).
    /// </summary>
    UseNative,

    /// <summary>
    /// <c>force_auto</c>: FreeType's auto-hinter (<c>FT_LOAD_FORCE_AUTOHINT</c>).
    /// </summary>
    ForceAuto,

    /// <summary>
    /// <c>disable_auto</c>: no auto-hinter (<c>FT_LOAD_NO_AUTOHINT</c>).
    /// </summary>
    DisableAuto,

    /// <summary>
    /// <c>disable_all</c>: no hinting (<c>FT_LOAD_NO_HINTING</c>).
    /// </summary>
    DisableAll
}

/// <summary>
/// Inclusive code point ranges (sorted, merged): the set a <c>ResourceTrueTypeFont</c> generates glyphs for.
/// </summary>
public sealed class MyGuiCodeRanges
{
    /// <summary>
    /// The ranges, sorted by first code point and not overlapping.
    /// </summary>
    readonly (int First, int Last)[] _ranges;

    /// <summary>
    /// The Basic Multilingual Plane: the set of a font whose <c>Codes</c> list no <c>range</c>.
    /// </summary>
    public static MyGuiCodeRanges Bmp { get; } = new([(0, 0xFFFF)], []);

    /// <summary>
    /// <paramref name="include"/> merged, then every <paramref name="hide"/> range removed (MyGUI applies exclusions last).
    /// </summary>
    public MyGuiCodeRanges(IEnumerable<(int First, int Last)> include, IEnumerable<(int First, int Last)> hide)
    {
        var merged = new List<(int First, int Last)>();
        foreach (var (first, last) in include.Where(r => r.First <= r.Last).OrderBy(r => r.First))
        {
            if (merged.Count > 0 && first <= merged[^1].Last + 1) merged[^1] = (merged[^1].First, Math.Max(merged[^1].Last, last));
            else merged.Add((first, last));
        }
        foreach (var (hideFirst, hideLast) in hide)
        {
            var kept = new List<(int First, int Last)>(merged.Count + 1);
            foreach (var (first, last) in merged)
            {
                if (hideLast < first || hideFirst > last) { kept.Add((first, last)); continue; }
                if (first < hideFirst) kept.Add((first, hideFirst - 1));
                if (last > hideLast) kept.Add((hideLast + 1, last));
            }
            merged = kept;
        }
        _ranges = [.. merged];
    }

    /// <summary>
    /// The merged ranges, ascending.
    /// </summary>
    public IReadOnlyList<(int First, int Last)> Ranges => _ranges;

    /// <summary>
    /// How many code points the set holds.
    /// </summary>
    public long Count => _ranges.Sum(r => (long)r.Last - r.First + 1);

    /// <summary>
    /// Whether <paramref name="codePoint"/> is in one of the ranges (binary search).
    /// </summary>
    public bool Contains(int codePoint)
    {
        int low = 0, high = _ranges.Length - 1;
        while (low <= high)
        {
            int mid = (low + high) >> 1;
            if (codePoint < _ranges[mid].First) high = mid - 1;
            else if (codePoint > _ranges[mid].Last) low = mid + 1;
            else return true;
        }
        return false;
    }

    /// <summary>
    /// Every code point of the set, ascending.
    /// </summary>
    public IEnumerable<int> CodePoints()
    {
        foreach (var (first, last) in _ranges)
            for (int c = first; c <= last; c++) yield return c;
    }
}

/// <summary>
/// A <c>ResourceTrueTypeFont</c> as MyGUI 3.2 reads it (docs/formats/mygui.md): the properties of <c>kenshi_fonts.xml</c> and its locale overrides.
/// Size, resolution and the other numbers keep MyGUI's defaults when absent or unparsable.
/// </summary>
/// <param name="Source">The font file name.</param>
/// <param name="Size">Points (<see cref="Resolution"/> dots per inch).</param>
/// <param name="Antialias">False: glyph colour is white and only the alpha carries the coverage.</param>
/// <param name="TabWidth">The advance of a tab in pixels; 0: eight spaces.</param>
/// <param name="OffsetHeight">Pixels glyphs are moved up.</param>
/// <param name="SubstituteCode">The code point drawn for one the font does not produce; negative: the face's undefined glyph.</param>
/// <param name="Distance">Glyph spacing in MyGUI's own texture; -1: its default of 1. No effect on the layout.</param>
/// <param name="Codes">The code points the font produces glyphs for.</param>
public sealed record MyGuiTrueTypeFont(string Source, float Size, float Resolution, bool Antialias, MyGuiHinting Hinting, float TabWidth,
    int OffsetHeight, int SubstituteCode, int Distance, MyGuiCodeRanges Codes)
{
    /// <summary>
    /// MyGUI's default resolution when the property is absent.
    /// </summary>
    public const float DefaultResolution = 96;

    /// <summary>
    /// The font <paramref name="resource"/> describes; null when it is not a <c>ResourceTrueTypeFont</c> or names no source.
    /// </summary>
    public static MyGuiTrueTypeFont? Read(XElement resource)
    {
        if ((string?)resource.Attribute("type") != "ResourceTrueTypeFont") return null;
        string? Property(string key) =>
            resource.Elements("Property").FirstOrDefault(p => (string?)p.Attribute("key") == key)?.Attribute("value")?.Value;
        float Number(string key, float fallback) =>
            float.TryParse(Property(key), NumberStyles.Float, CultureInfo.InvariantCulture, out float n) && float.IsFinite(n) ? n : fallback;
        int Integer(string key, int fallback) =>
            int.TryParse(Property(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : fallback;
        if (Property("Source") is not { Length: > 0 } source) return null;

        var include = new List<(int First, int Last)>();
        var hide = new List<(int First, int Last)>();
        foreach (var codes in resource.Elements("Codes"))
        {
            foreach (var code in codes.Elements("Code"))
            {
                if (Range((string?)code.Attribute("range")) is { } add) include.Add(add);
                if (Range((string?)code.Attribute("hide")) is { } remove) hide.Add(remove);
            }
        }
        // No range at all: the Basic Multilingual Plane, before the exclusions.
        if (include.Count == 0) include.Add((0, 0xFFFF));

        string? hinting = Property("Hinting");
        return new MyGuiTrueTypeFont(source, Number("Size", 0), Number("Resolution", DefaultResolution),
            Property("Antialias") is { } aa && (aa.Equals("true", StringComparison.OrdinalIgnoreCase) || aa == "1"),
            hinting switch
            {
                "force_auto" => MyGuiHinting.ForceAuto,
                "disable_auto" => MyGuiHinting.DisableAuto,
                "disable_all" => MyGuiHinting.DisableAll,
                _ => MyGuiHinting.UseNative
            },
            Number("TabWidth", 0), Integer("OffsetHeight", 0), Integer("SubstituteCode", -1), Integer("Distance", -1),
            new MyGuiCodeRanges(include, hide));
    }

    /// <summary>
    /// "first" or "first last" (decimal code points).
    /// </summary>
    static (int First, int Last)? Range(string? value)
    {
        if (value is null) return null;
        var parts = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int first) || first < 0) return null;
        int last = parts.Length > 1 && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int second) ? second : first;
        return (first, Math.Max(first, last));
    }
}