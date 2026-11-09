using System.Globalization;
using System.Numerics;

namespace Meitou.Data.Ui;

/// <summary>
/// A run of tagged text: from <see cref="Start"/> (an index into the plain text) on, the text takes <see cref="Colour"/> (rgb; alpha 1).
/// </summary>
public readonly record struct TextColourRun(int Start, Vector3 Colour);

/// <summary>
/// MyGUI's inline colour tags (its TextView, docs/game/ui-screens.md "Inline colour tags"): <c>#rrggbb</c> colours the text after it, <c>##</c> is
/// one <c>#</c>. A <c>#</c> before anything else is kept as written (engine choice: MyGUI reads any six characters).
/// </summary>
public static class MyGuiTextTags
{
    /// <summary>
    /// The text without its tags, and the colour runs in it (empty when it has no tag).
    /// </summary>
    public static (string Plain, IReadOnlyList<TextColourRun> Runs) Parse(string text)
    {
        if (!text.Contains('#')) return (text, []);
        var plain = new System.Text.StringBuilder(text.Length);
        var runs = new List<TextColourRun>();
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != '#') { plain.Append(text[i]); continue; }
            if (i + 1 < text.Length && text[i + 1] == '#') { plain.Append('#'); i++; continue; }
            if (i + 7 <= text.Length && int.TryParse(text.AsSpan(i + 1, 6), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int rgb))
            {
                var colour = new Vector3((rgb >> 16 & 255) / 255f, (rgb >> 8 & 255) / 255f, (rgb & 255) / 255f);
                if (runs.Count > 0 && runs[^1].Start == plain.Length) runs[^1] = new(plain.Length, colour);
                else runs.Add(new(plain.Length, colour));
                i += 6;
                continue;
            }
            plain.Append('#');
        }
        return (plain.ToString(), runs);
    }

    /// <summary>
    /// The text without its tags.
    /// </summary>
    public static string Strip(string text) => text.Contains('#') ? Parse(text).Plain : text;
}