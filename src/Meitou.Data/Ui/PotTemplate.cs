using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Meitou.Content;

namespace Meitou.Data.Ui;

/// <summary>
/// A msgid of an installed gettext template with its source references (file, line), e.g. ("kenshi/statswindow.cpp", 937).
/// </summary>
public sealed record PotEntry(IReadOnlyList<(string File, int Line)> References, string Id);

/// <summary>
/// Reads the user's English gettext template; the help catalogs take their structure from its source references, never from embedded game text.
/// </summary>
public static class PotTemplate
{
    /// <summary>
    /// The entries that have a source reference and a msgid; none when <paramref name="path"/> is null or missing.
    /// </summary>
    public static IReadOnlyList<PotEntry> Read(string? path)
    {
        var result = new List<PotEntry>();
        if (path is null) return result;
        path = GameFiles.Resolve(path);
        if (!File.Exists(path)) return result;
        var references = new List<(string, int)>();
        var text = new StringBuilder();
        bool reading = false;
        void Finish()
        {
            if (references.Count > 0 && text.Length > 0) result.Add(new([.. references], text.ToString()));
            references.Clear(); text.Clear(); reading = false;
        }
        foreach (string raw in File.ReadLines(path))
        {
            string line = raw.Trim();
            if (line.Length == 0) { Finish(); continue; }
            if (line.StartsWith("#:", StringComparison.Ordinal))
                foreach (Match match in Regex.Matches(line, @"(\S+):(\d+)"))
                    references.Add((match.Groups[1].Value, int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture)));
            if (line.StartsWith("msgid ", StringComparison.Ordinal))
            { reading = true; text.Append(JsonSerializer.Deserialize<string>(line[6..])); }
            else if (line.StartsWith("msgstr", StringComparison.Ordinal) || line.StartsWith("msgctxt", StringComparison.Ordinal)) reading = false;
            else if (reading && line.StartsWith('"')) text.Append(JsonSerializer.Deserialize<string>(line));
        }
        Finish();
        return result;
    }
}