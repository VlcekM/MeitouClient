using System.Globalization;
using System.Numerics;
using System.Xml.Linq;

namespace Meitou.Data.Characters;

/// <summary>Character editor category of a slider (editor_data.xsd <c>configType</c>).</summary>
public enum AppearanceCategory { Face, Body, Hair, Personality }

/// <summary>
/// One slider of a race's editor limits file (<c>Range</c>): an integer between <see cref="Min"/> and <see cref="Max"/>.
/// With <see cref="Target"/> it drives face poses (weight = value × 0.01; a negative value goes to
/// <see cref="TargetOpposite"/> as value × −0.01 when there is one); without, it is a body-file slider of the same name.
/// </summary>
public sealed record AppearanceRange(string Name, AppearanceCategory Category, int Min, int Max, int Mid, int Group, int Variation)
{
    public string? Target { get; init; }
    public string? TargetOpposite { get; init; }
}

/// <summary>A colour slider (<c>ColourRange</c>, e.g. <c>Skin Tone</c>): value 0..(n−1)×10 blends between the n colours.</summary>
public sealed record AppearanceColourRange(string Name, AppearanceCategory Category, IReadOnlyList<Vector3> Colours, int Min, int Max, int Mid, int Group, int Variation)
{
    /// <summary>The colour of a slider value: colour index value × 0.1, blended linearly with the next (docs/characters.md).</summary>
    public Vector3 ColourAt(int value)
    {
        if (Colours.Count == 1) return Colours[0];
        float t = value * 0.1f;
        int i = Math.Clamp((int)MathF.Floor(t), 0, Colours.Count - 1);
        int j = i == Colours.Count - 1 ? i : i + 1;
        return Vector3.Lerp(Colours[i], Colours[j], t - i);
    }
}

/// <summary>The sliders of one gender in a race's <c>editor limits</c> XML (<c>data/editor/editor_data_*.xml</c>).</summary>
public sealed class AppearanceLimits
{
    public List<AppearanceRange> Ranges { get; } = [];
    public List<AppearanceColourRange> Colours { get; } = [];

    /// <summary>
    /// Reads the config of one gender, the way <c>AppearanceManager::loadEditorXMLData</c> does (docs/characters.md,
    /// "Random appearance"): a <c>Config</c> without <c>gender</c> counts as male; <c>mid</c> defaults to the middle of
    /// the range, else is clamped into it; the random variation is the range × <c>random_variation</c>% or, without
    /// one, × <paramref name="defaultDeviation"/> (CONSTANTS <c>appearance random deviation percentage</c>).
    /// Returns null if the file has no config for that gender.
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
                        Target = NonEmpty((string?)e.Attribute("target")), TargetOpposite = NonEmpty((string?)e.Attribute("target_opposite")),
                    });
                }
                else if (e.Name.LocalName == "ColourRange")
                {
                    var colours = e.Elements("Colour").Select(c => new Vector3(Int(c, "r", 0), Int(c, "g", 0), Int(c, "b", 0)) / 255f).ToList();
                    if (colours.Count == 0) continue;
                    int max = (colours.Count - 1) * 10;
                    limits.Colours.Add(new AppearanceColourRange(name, cat, colours, 0, max, Mid(e, 0, max), group, Variation(e, 0, max, defaultDeviation)));
                }
            }
        }
        return limits;
    }

    static int Mid(XElement e, int min, int max) =>
        e.Attribute("mid") is { } mid ? Math.Clamp(Parse(mid.Value), min, max) : CharacterGenerator.Round((max - min) * 0.5f + min);

    static int Variation(XElement e, int min, int max, float defaultDeviation) =>
        CharacterGenerator.Round((max - min) * (e.Attribute("random_variation") is { } v ? Parse(v.Value) * 0.01f : defaultDeviation));

    static int Int(XElement e, string attribute, int fallback) => e.Attribute(attribute) is { } a ? Parse(a.Value) : fallback;

    // Ogre's parseInt: invalid text reads as 0.
    static int Parse(string s) => int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

    static string? NonEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;
}
