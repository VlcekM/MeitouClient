using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Meitou.Data.Ui;

/// <summary>
/// XML text as the game's own XML parser (<c>MyGUI::xml::Document</c>) reads it, where .NET's parser is stricter.
/// </summary>
public static partial class MyGuiXml
{
    /// <summary>
    /// The document in the file at <paramref name="path"/>, read as <c>MyGUI::xml::Document::open</c> reads it: a tag that repeats an attribute
    /// keeps the first, as <c>Element::findAttribute</c> returns the first of that name (docs/formats/overview.md "MyGUI XML").
    /// </summary>
    public static XDocument Load(string path) => XDocument.Parse(DropRepeatedAttributes(File.ReadAllText(path)));

    /// <summary>
    /// <paramref name="xml"/> with every attribute that repeats an earlier one of its tag removed.
    /// </summary>
    internal static string DropRepeatedAttributes(string xml) => StartTag().Replace(xml, KeepFirstAttributes);

    /// <summary>
    /// A start tag with its attributes, rewritten to keep only the first of each name.
    /// </summary>
    static string KeepFirstAttributes(Match tag)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var kept = new StringBuilder();
        foreach (Match a in Attribute().Matches(tag.Groups["attributes"].Value))
            if (seen.Add(a.Groups["name"].Value)) kept.Append(a.Value);

        return "<" + tag.Groups["name"].Value + kept + tag.Groups["end"].Value;
    }

    /// <summary>
    /// A start or empty-element tag: its name, its attributes and its close (<c>&gt;</c> or <c>/&gt;</c> with the space before it).
    /// </summary>
    [GeneratedRegex(@"<(?<name>[A-Za-z_][\w:.\-]*)(?<attributes>(?:\s+[^\s=/>]+\s*=\s*(?:""[^""]*""|'[^']*'))*)(?<end>\s*/?>)")]
    private static partial Regex StartTag();

    /// <summary>
    /// One attribute with the white space before it.
    /// </summary>
    [GeneratedRegex(@"\s+(?<name>[^\s=/>]+)\s*=\s*(?:""[^""]*""|'[^']*')")]
    private static partial Regex Attribute();
}
