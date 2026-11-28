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
    /// One attribute: a name without white space, quotes, <c>=</c>, <c>/</c> or <c>&gt;</c>, then <c>=</c> and a quoted value.
    /// </summary>
    const string AttributePattern = @"[^\s=/>""']+\s*=\s*(?:""[^""]*""|'[^']*')";

    /// <summary>
    /// The document in the file at <paramref name="path"/>, read as <c>MyGUI::xml::Document::open</c> reads it: a tag that repeats an attribute
    /// keeps the first, as <c>Element::findAttribute</c> returns the first of that name, and attributes need no white space between them
    /// (docs/formats/overview.md "MyGUI XML").
    /// </summary>
    public static XDocument Load(string path) => XDocument.Parse(DropRepeatedAttributes(File.ReadAllText(path)));

    /// <summary>
    /// <paramref name="xml"/> with every attribute that repeats an earlier one of its tag removed, and one space before each attribute.
    /// </summary>
    internal static string DropRepeatedAttributes(string xml) => StartTag().Replace(xml, KeepFirstAttributes);

    /// <summary>
    /// A start tag with its attributes, rewritten to keep only the first of each name, each after one space.
    /// </summary>
    static string KeepFirstAttributes(Match tag)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var kept = new StringBuilder();
        foreach (Match a in Attribute().Matches(tag.Groups["attributes"].Value))
            if (seen.Add(a.Groups["name"].Value)) kept.Append(' ').Append(a.Groups["name"].Value).Append('=').Append(a.Groups["value"].Value);

        return "<" + tag.Groups["name"].Value + kept + tag.Groups["end"].Value;
    }

    /// <summary>
    /// A start or empty-element tag: its name, its attributes (white space after the name, none needed after a value) and its close (<c>&gt;</c>
    /// or <c>/&gt;</c> with the space before it).
    /// </summary>
    [GeneratedRegex(@"<(?<name>[A-Za-z_][\w:.\-]*)(?<attributes>(?:\s+" + AttributePattern + @"(?:\s*" + AttributePattern + @")*)?)(?<end>\s*/?>)")]
    private static partial Regex StartTag();

    /// <summary>
    /// One attribute with the white space before it, if any.
    /// </summary>
    [GeneratedRegex(@"\s*(?<name>[^\s=/>""']+)\s*=\s*(?<value>""[^""]*""|'[^']*')")]
    private static partial Regex Attribute();
}
