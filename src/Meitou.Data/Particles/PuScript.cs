using System.Globalization;
using System.Numerics;

namespace Meitou.Data.Particles;

public sealed class PuScriptException(string message, int line) : Exception($"line {line}: {message}")
{
    public int Line { get; } = line;
}

/// <summary>One <c>name value...</c> line of a block; a value that is a <c>dyn_*</c> block carries <see cref="Dynamic"/> instead of words.</summary>
public sealed class PuProperty(string name, string[] values, PuDynamic? dynamic, int line)
{
    public string Name { get; } = name;
    public string[] Values { get; } = values;
    public PuDynamic? Dynamic { get; } = dynamic;
    public int Line { get; } = line;

    public float Float(float fallback = 0) => Dynamic is { } d ? d.A : Values.Length > 0 && TryFloat(Values[0], out float f) ? f : fallback;
    public bool Bool(bool fallback = false) => Values.Length > 0 ? Values[0] is "true" or "1" or "on" or "yes" : fallback;

    internal static bool TryFloat(string s, out float f) => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out f);

    public override string ToString() => Dynamic is not null ? $"{Name} {Dynamic.Kind}" : $"{Name} {string.Join(' ', Values)}";
}

/// <summary>A block of a script: <c>system</c>, <c>technique</c>, <c>renderer</c>, <c>emitter</c>, <c>affector</c>, <c>observer</c>, <c>handler</c>...
/// <see cref="Args"/> are the words after the class (<c>emitter Box Emitter4</c>: Box, Emitter4).</summary>
public sealed class PuNode(string kind, string[] args, int line)
{
    public string Kind { get; } = kind;
    public string[] Args { get; } = args;
    public int Line { get; } = line;
    public List<PuProperty> Properties { get; } = [];
    public List<PuNode> Children { get; } = [];

    /// <summary>The block's type (<c>Box</c> of <c>emitter Box Emitter4</c>) and name (<c>Emitter4</c>, empty when absent; <c>system Name</c> has only a name).</summary>
    public string Type => Args.Length > 0 ? Args[0] : "";
    public string Name => Kind == "system" || Kind == "technique" ? (Args.Length > 0 ? Args[0] : "") : Args.Length > 1 ? Args[1] : "";

    /// <summary>The last property of that name (later ones override earlier ones).</summary>
    public PuProperty? Find(string name)
    {
        for (int i = Properties.Count - 1; i >= 0; i--) if (Properties[i].Name == name) return Properties[i];
        return null;
    }

    public IEnumerable<PuProperty> All(string name) => Properties.Where(p => p.Name == name);
    public IEnumerable<PuNode> Blocks(string kind) => Children.Where(c => c.Kind == kind);

    public float Float(string name, float fallback = 0) => Find(name)?.Float(fallback) ?? fallback;
    public bool Bool(string name, bool fallback = false) => Find(name)?.Bool(fallback) ?? fallback;
    public string? Word(string name) => Find(name) is { Values.Length: > 0 } p ? p.Values[0] : null;

    public PuDynamic? Dynamic(string name) => Find(name) is { } p ? p.Dynamic ?? (p.Values.Length > 0 && PuProperty.TryFloat(p.Values[0], out float f) ? PuDynamic.Fixed(f) : null) : null;

    public Vector3? Vec3(string name) => Find(name) is { Values.Length: >= 3 } p && PuProperty.TryFloat(p.Values[0], out float x) && PuProperty.TryFloat(p.Values[1], out float y) && PuProperty.TryFloat(p.Values[2], out float z)
        ? new Vector3(x, y, z) : null;

    public Vector4? Vec4(string name) => Find(name) is { Values.Length: >= 4 } p && PuProperty.TryFloat(p.Values[0], out float x) && PuProperty.TryFloat(p.Values[1], out float y) && PuProperty.TryFloat(p.Values[2], out float z) && PuProperty.TryFloat(p.Values[3], out float w)
        ? new Vector4(x, y, z, w) : null;

    public override string ToString() => $"{Kind} {string.Join(' ', Args)}";
}

/// <summary>
/// Reads a ParticleUniverse <c>.pu</c> script into a tree of blocks and properties (docs/formats/particle-universe.md). The text is
/// line based: <c>//</c> starts a comment; a line followed by a line holding only <c>{</c> opens a block (words: class, then its
/// arguments), any other line is a property (name, values); <c>}</c> closes. A property whose value is <c>dyn_random</c>,
/// <c>dyn_curved_linear</c>, <c>dyn_curved_spline</c> or <c>dyn_oscillate</c> takes the block that follows as its dynamic attribute.
/// No quotes, variables or imports occur in the shipped scripts.
/// </summary>
public static class PuScriptReader
{
    /// <summary>The top-level <c>system</c> blocks of the text.</summary>
    public static List<PuNode> Parse(string text)
    {
        var lines = new List<(string Text, int Number)>();
        int n = 0;
        foreach (var raw in text.Split('\n'))
        {
            n++;
            var line = raw;
            int comment = line.IndexOf("//", StringComparison.Ordinal);
            if (comment >= 0) line = line[..comment];
            line = line.Trim();
            if (line.Length > 0) lines.Add((line, n));
        }
        var roots = new List<PuNode>();
        var stack = new Stack<PuNode>();
        for (int i = 0; i < lines.Count; i++)
        {
            var (line, number) = lines[i];
            if (line == "{") throw new PuScriptException("unexpected '{'", number);
            if (line == "}")
            {
                if (stack.Count == 0) throw new PuScriptException("unexpected '}'", number);
                stack.Pop();
                continue;
            }
            var words = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            bool opens = i + 1 < lines.Count && lines[i + 1].Text == "{";
            if (!opens)
            {
                if (stack.Count == 0) throw new PuScriptException($"property '{words[0]}' outside a block", number);
                stack.Peek().Properties.Add(new PuProperty(words[0], words[1..], null, number));
                continue;
            }
            i++;   // the '{'
            if (words.Length >= 2 && words[1].StartsWith("dyn_", StringComparison.Ordinal))
            {
                if (stack.Count == 0) throw new PuScriptException($"attribute '{words[0]}' outside a block", number);
                stack.Peek().Properties.Add(new PuProperty(words[0], words[1..2], ReadDynamic(words[1], lines, ref i, number), number));
                continue;
            }
            var node = new PuNode(words[0], words[1..], number);
            if (stack.Count == 0) roots.Add(node);
            else stack.Peek().Children.Add(node);
            stack.Push(node);
        }
        if (stack.Count != 0) throw new PuScriptException($"block '{stack.Peek().Kind}' is not closed", stack.Peek().Line);
        return roots;
    }

    /// <summary>Reads the body of a <c>dyn_*</c> block; <paramref name="i"/> is the line of its '{' and ends on its '}'.</summary>
    static PuDynamic ReadDynamic(string kind, List<(string Text, int Number)> lines, ref int i, int headerLine)
    {
        float min = 0, max = 0, frequency = 0, phase = 0, baseValue = 0, amplitude = 0;
        bool square = false;
        var points = new List<Vector2>();
        i++;
        for (; i < lines.Count && lines[i].Text != "}"; i++)
        {
            var (line, number) = lines[i];
            var w = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            float F(int k) => k < w.Length && PuProperty.TryFloat(w[k], out float f) ? f : throw new PuScriptException($"'{w[0]}' needs a number", number);
            switch (w[0])
            {
                case "min": min = F(1); break;
                case "max": max = F(1); break;
                case "control_point": points.Add(new Vector2(F(1), F(2))); break;
                case "oscillate_frequency": frequency = F(1); break;
                case "oscillate_phase": phase = F(1); break;
                case "oscillate_base": baseValue = F(1); break;
                case "oscillate_amplitude": amplitude = F(1); break;
                case "oscillate_type": square = w.Length > 1 && w[1] == "square"; break;
                default: throw new PuScriptException($"unknown property '{w[0]}' in {kind}", number);
            }
        }
        if (i >= lines.Count) throw new PuScriptException($"{kind} is not closed", headerLine);
        return kind switch
        {
            "dyn_random" => PuDynamic.Random(min, max),
            "dyn_curved_linear" => PuDynamic.Curve(points, spline: false),
            "dyn_curved_spline" => PuDynamic.Curve(points, spline: true),
            "dyn_oscillate" => PuDynamic.Oscillate(baseValue, amplitude, frequency, phase, square),
            _ => throw new PuScriptException($"unknown dynamic attribute '{kind}'", headerLine),
        };
    }
}
