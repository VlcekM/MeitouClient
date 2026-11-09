using System.Globalization;
using System.Numerics;
using System.Xml.Linq;

namespace Meitou.Data.Characters;

/// <summary>
/// Character editor category of a slider (editor_data.xsd <c>configType</c>).
/// </summary>
public enum AppearanceCategory
{
    /// <summary>
    /// The <c>Face</c> category.
    /// </summary>
    Face,
    /// <summary>
    /// The <c>Body</c> category.
    /// </summary>
    Body,
    /// <summary>
    /// The <c>Hair</c> category.
    /// </summary>
    Hair,
    /// <summary>
    /// The <c>Personality</c> category.
    /// </summary>
    Personality
}

/// <summary>
/// One slider of a race's editor limits file (<c>Range</c>): an integer between <see cref="Min"/> and <see cref="Max"/>.
/// With <see cref="Target"/> it drives face poses (weight = value × 0.01; a negative value goes to
/// <see cref="TargetOpposite"/> as value × −0.01 when there is one); without, it is a body-file slider of the same name.
/// </summary>
public sealed record AppearanceRange(string Name, AppearanceCategory Category, int Min, int Max, int Mid, int Group, int Variation)
{
    /// <summary>
    /// The <c>target</c> pose the slider drives; null for a body-file slider.
    /// </summary>
    public string? Target { get; init; }

    /// <summary>
    /// The <c>target_opposite</c> pose a negative value drives; null when none.
    /// </summary>
    public string? TargetOpposite { get; init; }
}

/// <summary>
/// A colour slider (<c>ColourRange</c>, e.g. <c>Skin Tone</c>): value 0..(n−1)×10 blends between the n colours.
/// </summary>
public sealed record AppearanceColourRange(string Name, AppearanceCategory Category, IReadOnlyList<Vector3> Colours, int Min, int Max, int Mid,
        int Group, int Variation)
{
    /// <summary>
    /// The colour of a slider value: colour index value × 0.1, blended linearly with the next (docs/characters.md).
    /// </summary>
    public Vector3 ColourAt(int value)
    {
        if (Colours.Count == 1) return Colours[0];
        float t = value * 0.1f;
        int i = Math.Clamp((int)MathF.Floor(t), 0, Colours.Count - 1);
        int j = i == Colours.Count - 1 ? i : i + 1;
        return Vector3.Lerp(Colours[i], Colours[j], t - i);
    }
}

/// <summary>
/// The sliders of one gender in a race's <c>editor limits</c> XML (<c>data/editor/editor_data_*.xml</c>).
/// </summary>
public sealed class AppearanceLimits
{
    /// <summary>
    /// The <c>Range</c> sliders, in file order.
    /// </summary>
    public List<AppearanceRange> Ranges { get; } = [];

    /// <summary>
    /// The <c>ColourRange</c> sliders with at least one colour, in file order.
    /// </summary>
    public List<AppearanceColourRange> Colours { get; } = [];

    /// <summary>
    /// Reads the config of one gender, the way <c>AppearanceManager::loadEditorXMLData</c> does (docs/characters.md, "Random appearance"): a
    /// <c>Config</c> without <c>gender</c> counts as male; <c>mid</c> defaults to the middle of the range, else is clamped into it; the random
    /// variation is the range × <c>random_variation</c>% or, without one, × <paramref name="defaultDeviation"/> (runtime CONSTANTS <c>appearance
    /// random deviation percentage</c>, after its ×0.5 conversion). Returns null if the file has no config for that gender.
    /// </summary>
    public static AppearanceLimits? Read(string path, bool female, float defaultDeviation)
    {
        var doc = XDocument.Load(path);
        var config = doc.Root?.Elements("Config")
            .FirstOrDefault(c => string.Equals((string?)c.Attribute("gender") ?? "Male", "Male", StringComparison.OrdinalIgnoreCase) != female);
        if (config is null) return null;
        var limits = new AppearanceLimits();
        foreach (var category in config.Elements("Category"))
        {
            if (!Enum.TryParse<AppearanceCategory>((string?)category.Attribute("name"), out var cat)) continue;
            foreach (var e in category.Elements())
            {
                string name = (string?)e.Attribute("name") ?? "";
                int group = Int(e, "random_group", -1);
                if (e.Name.LocalName == "Range")
                {
                    int min = Int(e, "min", 0), max = Int(e, "max", 0);
                    limits.Ranges.Add(new AppearanceRange(name, cat, min, max, Mid(e, min, max), group, Variation(e, min, max, defaultDeviation))
                    {
                        Target = NonEmpty((string?)e.Attribute("target")),
                        TargetOpposite = NonEmpty((string?)e.Attribute("target_opposite")),
                    });
                }
                else if (e.Name.LocalName == "ColourRange")
                {
                    var colours = e.Elements("Colour").Select(c => new Vector3(Int(c, "r", 0), Int(c, "g", 0), Int(c, "b", 0)) / 255f).ToList();
                    if (colours.Count == 0) continue;
                    int max = (colours.Count - 1) * 10;
                    limits.Colours.Add(new AppearanceColourRange(name, cat, colours, 0, max, Mid(e, 0, max), group, Variation(e, 0, max,
                            defaultDeviation)));
                }
            }
        }
        return limits;
    }

    /// <summary>
    /// The slider's <c>mid</c>, clamped into [<paramref name="min"/>, <paramref name="max"/>]; without one, the middle of the range.
    /// </summary>
    static int Mid(XElement e, int min, int max) =>
        e.Attribute("mid") is { } mid ? Math.Clamp(Parse(mid.Value), min, max) : CharacterGenerator.Round((max - min) * 0.5f + min);

    /// <summary>
    /// The random variation: the range × <c>random_variation</c>%, else × <paramref name="defaultDeviation"/>.
    /// </summary>
    static int Variation(XElement e, int min, int max, float defaultDeviation) =>
        CharacterGenerator.Round((max - min) * (e.Attribute("random_variation") is { } v ? Parse(v.Value) * 0.01f : defaultDeviation));

    /// <summary>
    /// The integer <paramref name="attribute"/> of <paramref name="e"/>, or <paramref name="fallback"/> when it is absent.
    /// </summary>
    static int Int(XElement e, string attribute, int fallback) => e.Attribute(attribute) is { } a ? Parse(a.Value) : fallback;

    /// <summary>
    /// Ogre's parseInt: invalid text reads as 0.
    /// </summary>
    static int Parse(string s) => int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

    /// <summary>
    /// Null for an empty or missing attribute value.
    /// </summary>
    static string? NonEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;
}