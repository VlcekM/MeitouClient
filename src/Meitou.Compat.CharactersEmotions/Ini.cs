namespace Meitou.Compat.CharactersEmotions;

/// <summary>
/// A minimal INI reader for the plugin's two configuration files: <c>[Section]</c> headers (case-insensitive; the animation file's names may hold commas and spaces),
/// <c>key=value</c> lines (keys case-insensitive), <c>;</c> or <c>#</c> comment lines. A repeated key keeps its last value.
/// </summary>
sealed class Ini
{
    readonly List<(string Name, Dictionary<string, string> Keys)> _sections = [];

    public static Ini Parse(IEnumerable<string> lines)
    {
        var ini = new Ini();
        var current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        ini._sections.Add(("", current));
        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] is ';' or '#') continue;
            if (line[0] == '[')
            {
                int end = line.IndexOf(']');
                if (end < 0) continue;
                current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                ini._sections.Add((line[1..end].Trim(), current));
                continue;
            }
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            current[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }
        return ini;
    }

    /// <summary>The sections in file order (the unnamed head of the file first).</summary>
    public IEnumerable<(string Name, IReadOnlyDictionary<string, string> Keys)> Sections =>
        _sections.Select(s => (s.Name, (IReadOnlyDictionary<string, string>)s.Keys));

    public IReadOnlyDictionary<string, string>? Section(string name) =>
        _sections.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)).Keys;
}
