using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// Compares two draw logs (<c>MEITOU_DRAW_LOG</c>, docs/renderer-native.md 7.6) draw by draw. Vulkan handles differ between runs, so each
/// log's handles (attachments, sampler and view, buffers) are renamed by first appearance (H1, H2, ...) before comparing; program hashes
/// and the hashes of host-memory bytes are stable and compared as they are. Pass labels and comment lines (';') are ignored. Prints the
/// first differing draws field by field; exit code 0 when the logs match.
/// </summary>
static partial class DrawLogDiff
{
    public static int Run(string a, string b, bool keepHandles = false, int show = 10)
    {
        var x = Load(a, keepHandles);
        var y = Load(b, keepHandles);
        Console.WriteLine($"{a}: {x.Count} draws");
        Console.WriteLine($"{b}: {y.Count} draws");
        int differing = 0, n = Math.Min(x.Count, y.Count);
        var kinds = new SortedDictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < n; i++)
        {
            if (x[i].Text == y[i].Text) continue;
            if (++differing <= show) Report(i, x[i], y[i]);
            var f = Fields(x[i].Text);
            string kind = $"prog {f.GetValueOrDefault("1.prog")} fmt {f.GetValueOrDefault("1.fmt")}";
            kinds[kind] = kinds.GetValueOrDefault(kind) + 1;
        }
        foreach (var (kind, count) in kinds) Console.WriteLine($"  differing: {count} x {kind}");
        if (x.Count != y.Count) Console.WriteLine($"draw counts differ: {x.Count} vs {y.Count} (first {n} compared)");
        Console.WriteLine(differing == 0 && x.Count == y.Count ? "identical" : $"{differing} of {n} draws differ");
        return differing == 0 && x.Count == y.Count ? 0 : 1;
    }

    sealed record Draw(int Number, string Label, string Text);

    static void Report(int i, Draw a, Draw b)
    {
        Console.WriteLine($"draw {i + 1} (#{a.Number} [{a.Label}] / #{b.Number} [{b.Label}]):");
        var fa = Fields(a.Text);
        var fb = Fields(b.Text);
        foreach (var key in fa.Keys.Union(fb.Keys))
        {
            fa.TryGetValue(key, out var va);
            fb.TryGetValue(key, out var vb);
            if (va != vb) Console.WriteLine($"  {key}: {va ?? "(none)"}  <>  {vb ?? "(none)"}");
        }
    }

    /// <summary>The record's space-separated name=value pieces, keyed by section and name (for the field report).</summary>
    static Dictionary<string, string> Fields(string text)
    {
        var d = new Dictionary<string, string>();
        var sections = text.Split(" | ");
        for (int s = 0; s < sections.Length; s++)
        {
            int k = 0;
            foreach (var piece in sections[s].Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = piece.IndexOf('=');
                string name = eq > 0 ? piece[..eq] : $"#{k}";
                d[$"{s}.{name}"] = eq > 0 ? piece[(eq + 1)..] : piece;
                k++;
            }
        }
        return d;
    }

    static List<Draw> Load(string path, bool keepHandles)
    {
        var handles = new Dictionary<string, string>();
        string H(string hex) => keepHandles || hex is "0" or "null" ? hex : handles.TryGetValue(hex, out var h) ? h : handles[hex] = $"H{handles.Count + 1}";
        var list = new List<Draw>();
        foreach (var line in File.ReadLines(path))
        {
            if (line.Length == 0 || line[0] == ';') continue;
            var m = Header().Match(line);
            if (!m.Success) continue;
            var sections = line[m.Length..].Split(" | ");
            for (int s = 0; s < sections.Length; s++)
            {
                var sec = sections[s];
                if (sec.StartsWith("tgt ", StringComparison.Ordinal))
                    sections[s] = Target().Replace(sec, x => $"{x.Groups[1].Value}={H(x.Groups[2].Value)}");
                else if (sec.StartsWith("s0:", StringComparison.Ordinal) || sec.StartsWith("vb:", StringComparison.Ordinal))
                {
                    var sb = new StringBuilder(sec[..3]);
                    foreach (var entry in sec[3..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    {
                        int eq = entry.IndexOf('=');
                        sb.Append(' ').Append(entry[..(eq + 1)]).Append(Value(entry[(eq + 1)..], H));
                    }
                    sections[s] = sb.ToString();
                }
                else if (sec.StartsWith("ib=", StringComparison.Ordinal))
                    sections[s] = "ib=" + Value(sec[3..], H);
            }
            list.Add(new Draw(int.Parse(m.Groups[1].Value), m.Groups[2].Value, string.Join(" | ", sections)));
        }
        return list;
    }

    /// <summary>One logged resource: "const", "-", "host/16", "h&lt;hash&gt;/size" stay; otherwise the first ':'-separated part is a handle,
    /// and for an image (sampler:view, two parts) both are.</summary>
    static string Value(string v, Func<string, string> h)
    {
        if (v is "const" or "-" or "null" || v.StartsWith('h')) return v;
        var parts = v.Split(':');
        if (parts.Length == 2 && !parts[1].Contains('/')) return $"{h(parts[0])}:{h(parts[1])}";   // sampler:view
        parts[0] = h(parts[0]);
        return string.Join(':', parts);
    }

    [GeneratedRegex(@"^#(\d+) \[([^\]]*)\] ")]
    private static partial Regex Header();

    [GeneratedRegex(@"\b([cd])=([0-9a-f]+)")]
    private static partial Regex Target();
}
