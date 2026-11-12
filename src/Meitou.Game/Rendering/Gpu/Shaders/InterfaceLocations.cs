using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Meitou.Game.Rendering.Frame;

namespace Meitou.Game.Rendering.Gpu.Shaders;

/// <summary>
/// GL links the vertex outputs to the fragment inputs by name; Vulkan by location. This adds an explicit <c>layout(location = N)</c>
/// to every global <c>out</c> of the vertex stage and <c>in</c> of the fragment stage that lacks one, the same N for the same name,
/// and numbers the fragment outputs 0, 1, 2... Works on the text (comments masked while scanning, line numbers kept).
/// </summary>
static partial class InterfaceLocations
{
    /// <summary>
    /// One name of an interface declaration: its slots and its location (explicit or assigned).
    /// </summary>
    sealed class Declarator
    {
        /// <summary>
        /// The variable's name.
        /// </summary>
        public required string Name;
        /// <summary>
        /// The locations it takes (a matrix's columns times its array length).
        /// </summary>
        public required int Slots;
        /// <summary>
        /// [NameStart, DeclEnd): the name and its array suffix in the source.
        /// </summary>
        public int NameStart, DeclEnd;
        /// <summary>
        /// The location: explicit, or assigned.
        /// </summary>
        public int? Location;
        /// <summary>
        /// The array dimensions after the name.
        /// </summary>
        public string Suffix = "";
    }

    /// <summary>
    /// One global <c>in</c> or <c>out</c> declaration statement and where its parts are in the source.
    /// </summary>
    sealed class Statement
    {
        /// <summary>
        /// An <c>out</c> (else an <c>in</c>).
        /// </summary>
        public required bool IsOut;
        /// <summary>
        /// [Start, End]: the statement up to and including the ';'.
        /// </summary>
        public int Start, End;
        /// <summary>
        /// The start of the first declarator.
        /// </summary>
        public int PrefixEnd;
        /// <summary>
        /// The offset just after "layout(" when a layout without a location exists.
        /// </summary>
        public int? LayoutOpen;
        /// <summary>
        /// The whole layout(...) when present.
        /// </summary>
        public int? LayoutStart, LayoutEnd;
        /// <summary>
        /// The names the statement declares.
        /// </summary>
        public List<Declarator> Declarators = [];
        /// <summary>
        /// The statement has an explicit location (the scan numbered its names from it).
        /// </summary>
        public bool HasExplicit;
    }

    #region Linking

    /// <summary>
    /// Adds the explicit locations to both sources: varyings first, then the fragment outputs.
    /// </summary>
    public static (string Vertex, string Fragment) Apply(string vertex, string fragment)
    {
        var vs = Scan(vertex, "vertex");
        var fs = Scan(fragment, "fragment");
        var vOuts = vs.Where(s => s.IsOut).ToList();
        var fIns = fs.Where(s => !s.IsOut).ToList();
        var fOuts = fs.Where(s => s.IsOut).ToList();
        AssignVaryings(vOuts, fIns);
        AssignOutputs(fOuts);
        return (Rewrite(vertex, vOuts), Rewrite(fragment, fIns.Concat(fOuts).ToList()));
    }

    /// <summary>
    /// Varyings: one location per name, shared by both stages.
    /// </summary>
    static void AssignVaryings(List<Statement> vOuts, List<Statement> fIns)
    {
        var byName = new Dictionary<string, (int Location, int Slots)>();
        var used = new HashSet<int>();
        foreach (var d in vOuts.Concat(fIns).SelectMany(s => s.Declarators))
        {
            if (d.Location is not { } loc) continue;
            if (byName.TryGetValue(d.Name, out var have))
            {
                if (have.Location != loc || have.Slots != d.Slots)
                {
                    throw new ShaderCompileException(
                        $"Varying '{d.Name}' is declared with different locations or sizes in the two stages.", "link", "");
                }
            }
            else
            {
                byName[d.Name] = (loc, d.Slots);
                for (int i = 0; i < d.Slots; i++) used.Add(loc + i);
            }
        }

        foreach (var d in vOuts.Concat(fIns).SelectMany(s => s.Declarators))
        {
            if (d.Location is not null) continue;
            if (!byName.TryGetValue(d.Name, out var have))
            {
                int loc = Free(used, d.Slots);
                for (int i = 0; i < d.Slots; i++) used.Add(loc + i);
                have = byName[d.Name] = (loc, d.Slots);
            }
            else if (have.Slots != d.Slots)
            {
                throw new ShaderCompileException($"Varying '{d.Name}' has a different size in the two stages.", "link", "");
            }

            d.Location = have.Location;
        }
    }

    /// <summary>
    /// Fragment outputs: 0, 1, 2... in order.
    /// </summary>
    static void AssignOutputs(List<Statement> fOuts)
    {
        var usedOut = new HashSet<int>();
        foreach (var d in fOuts.SelectMany(s => s.Declarators))
        {
            if (d.Location is { } l)
            {
                for (int i = 0; i < d.Slots; i++) usedOut.Add(l + i);
            }
        }

        foreach (var d in fOuts.SelectMany(s => s.Declarators))
        {
            if (d.Location is not null) continue;
            int loc = Free(usedOut, d.Slots);
            for (int i = 0; i < d.Slots; i++) usedOut.Add(loc + i);
            d.Location = loc;
        }
    }

    /// <summary>
    /// The first location from which <paramref name="slots"/> consecutive locations are all free.
    /// </summary>
    static int Free(HashSet<int> used, int slots)
    {
        for (int start = 0; ; start++)
        {
            bool ok = true;
            for (int i = 0; i < slots && ok; i++) ok = !used.Contains(start + i);
            if (ok) return start;
        }
    }

    /// <summary>
    /// The source with the assigned locations written in: into the layout or as a new one, and a multi-name statement split into one declaration per
    /// name (line count kept).
    /// </summary>
    static string Rewrite(string source, List<Statement> statements)
    {
        var edits = new List<(int Start, int Length, string Text)>();
        foreach (var s in statements)
        {
            if (s.Declarators.Count == 0) continue;
            if (s.HasExplicit) continue; // GLSL numbers the rest of an explicit declaration consecutively; the scan already did
            if (s.Declarators.Count == 1)
            {
                var d = s.Declarators[0];
                if (s.LayoutOpen is { } open) edits.Add((open, 0, $"location = {d.Location}, "));
                else edits.Add((s.Start, 0, $"layout(location = {d.Location}) "));
                continue;
            }

            // out vec2 a, b;  ->  one declaration per name
            string prefix = source[s.Start..s.PrefixEnd];
            if (s.LayoutStart is { } ls && s.LayoutEnd is { } le)
                prefix = source[s.Start..ls] + source[le..s.PrefixEnd];
            var sb = new StringBuilder();
            foreach (var d in s.Declarators)
                sb.Append($"layout(location = {d.Location}) {prefix.Trim()} {d.Name}{d.Suffix}; ");
            string original = source[s.Start..(s.End + 1)];
            sb.Append('\n', original.Count(c => c == '\n'));
            edits.Add((s.Start, s.End + 1 - s.Start, sb.ToString()));
        }

        var result = new StringBuilder(source);
        foreach (var e in edits.OrderByDescending(e => e.Start))
            result.Remove(e.Start, e.Length).Insert(e.Start, e.Text);
        return result.ToString();
    }

    #endregion

    #region Scanning

    /// <summary>
    /// The interpolation and precision qualifiers that may stand before <c>in</c>/<c>out</c>.
    /// </summary>
    static readonly HashSet<string> Qualifiers =
        ["flat", "smooth", "noperspective", "centroid", "sample", "invariant", "precise", "highp", "mediump", "lowp"];

    /// <summary>
    /// The global interface statements of a stage's source (comments masked, integer constants collected for array sizes).
    /// </summary>
    static List<Statement> Scan(string source, string stage)
    {
        string m = MaskComments(source);
        var consts = new Dictionary<string, int>();
        foreach (Match c in ConstIntRegex().Matches(m))
            consts[c.Groups[1].Value] = int.Parse(c.Groups[2].Value, CultureInfo.InvariantCulture);

        var result = new List<Statement>();
        int depth = 0, start = 0;
        bool lineStart = true;
        for (int i = 0; i < m.Length; i++)
        {
            char ch = m[i];
            if (ch == '\n')
            {
                lineStart = true;
                continue;
            }

            if (char.IsWhiteSpace(ch)) continue;
            if (ch == '#' && lineStart && depth == 0 && IsBlank(m, start, i))
            {
                // preprocessor line, with continuations
                while (i < m.Length && m[i] != '\n')
                {
                    if (m[i] == '\\' && i + 1 < m.Length && m[i + 1] == '\n') i++;
                    else if (m[i] == '\\' && i + 2 < m.Length && m[i + 1] == '\r' && m[i + 2] == '\n') i += 2;
                    i++;
                }
                start = i;
                lineStart = true;
                continue;
            }
            lineStart = false;
            switch (ch)
            {
                case '{':
                    depth++;
                    break;
                case '}':
                    depth--;
                    if (depth == 0) start = i + 1;
                    break;
                case ';':
                    if (depth == 0)
                    {
                        if (Parse(source, m, start, i, consts, stage) is { } st) result.Add(st);
                        start = i + 1;
                    }

                    break;
            }
        }
        return result;
    }

    /// <summary>
    /// Whether the masked text in [<paramref name="from"/>, <paramref name="to"/>) is only whitespace.
    /// </summary>
    static bool IsBlank(string m, int from, int to)
    {
        for (int i = from; i < to; i++) if (!char.IsWhiteSpace(m[i])) return false;
        return true;
    }

    /// <summary>
    /// One statement as an interface declaration (its layout, qualifiers, type and names); null when it is not a global <c>in</c> or <c>out</c>.
    /// </summary>
    static Statement? Parse(string source, string m, int from, int semi, Dictionary<string, int> consts, string stage)
    {
        int p = from;
        SkipWs(m, ref p, semi);
        int start = p;
        bool? isOut = null;
        int? layoutOpen = null, layoutStart = null, layoutEnd = null;
        int? explicitLoc = null;
        string? typeName = null;

        while (p < semi)
        {
            string? tok = Ident(m, ref p, semi);
            if (tok is null) return null;
            if (tok == "layout")
            {
                int ls = p - tok.Length;
                SkipWs(m, ref p, semi);
                if (p >= semi || m[p] != '(') return null;
                int close = Balanced(m, p, semi);
                if (close < 0) return null;
                string inner = m[(p + 1)..close];
                var loc = LocationRegex().Match(inner);
                if (loc.Success) explicitLoc = int.Parse(loc.Groups[1].Value, CultureInfo.InvariantCulture);
                else layoutOpen = p + 1;
                layoutStart = ls;
                layoutEnd = close + 1;
                p = close + 1;
            }
            else if (tok is "in" or "out")
            {
                if (isOut is not null) return null;
                isOut = tok == "out";
            }
            else if (Qualifiers.Contains(tok)) { }
            else
            {
                if (isOut is null) return null; // not an interface declaration
                typeName = tok;
                break;
            }
            SkipWs(m, ref p, semi);
        }

        if (isOut is null || typeName is null) return null;

        // the vertex stage's `in` and the fragment stage's `out`/`in` handled by the caller's filter; keep all here
        int slots = BaseSlots(typeName);
        if (slots == 0) return null; // interface block or a type we do not know

        SkipWs(m, ref p, semi);
        int typeDims = 1;
        while (p < semi && m[p] == '[')
        {
            typeDims *= ArraySize(m, ref p, semi, consts, stage);
            SkipWs(m, ref p, semi);
        }

        var st = new Statement
        {
            IsOut = isOut.Value,
            Start = start,
            End = semi,
            LayoutOpen = layoutOpen,
            LayoutStart = layoutStart,
            LayoutEnd = layoutEnd,
            HasExplicit = explicitLoc is not null,
        };
        st.PrefixEnd = p;
        if (!ParseDeclarators(st, m, p, semi, slots, typeDims, explicitLoc, consts, stage)) return null;
        if (st.Declarators.Count > 0) st.PrefixEnd = st.Declarators[0].NameStart;

        // `in vec2 a, b;` prefix for the rebuilt multi-declarations excludes the array dims of the type (they stay in the prefix text)
        return st;
    }

    /// <summary>
    /// The statement's declarators from <paramref name="p"/> to <paramref name="semi"/>, each with its slots and (after an explicit location)
    /// the location it takes; false when a declarator is not one an interface variable can be.
    /// </summary>
    static bool ParseDeclarators(Statement st, string m, int p, int semi, int slots, int typeDims, int? explicitLoc, Dictionary<string, int> consts,
        string stage)
    {
        while (p < semi)
        {
            int nameStart = p;
            string? name = Ident(m, ref p, semi);
            if (name is null) return false;
            int dims = typeDims;
            int suffixStart = p;
            SkipWs(m, ref p, semi);
            while (p < semi && m[p] == '[')
            {
                dims *= ArraySize(m, ref p, semi, consts, stage);
                SkipWs(m, ref p, semi);
            }

            string suffix = m[suffixStart..p].Trim();
            if (p < semi && m[p] == '=') return false; // not valid for in/out; leave alone
            var d = new Declarator { Name = name, Slots = slots * dims, NameStart = nameStart, DeclEnd = p, Suffix = suffix };
            if (explicitLoc is { } e)
            {
                d.Location = e;
                explicitLoc = e + d.Slots;
            }

            st.Declarators.Add(d);
            if (p < semi && m[p] == ',')
            {
                p++;
                SkipWs(m, ref p, semi);
                continue;
            }

            break;
        }

        return true;
    }

    /// <summary>
    /// An array size at <paramref name="p"/>: a number or an integer constant; throws for anything else.
    /// </summary>
    static int ArraySize(string m, ref int p, int end, Dictionary<string, int> consts, string stage)
    {
        int close = m.IndexOf(']', p);
        if (close < 0 || close > end) throw new ShaderCompileException($"Unterminated array size in the {stage} shader.", stage, "");
        string expr = m[(p + 1)..close].Trim();
        p = close + 1;
        if (int.TryParse(expr, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)) return n;
        if (consts.TryGetValue(expr, out n)) return n;
        throw new ShaderCompileException($"Cannot evaluate the array size '{expr}' of an interface variable in the {stage} shader.", stage, "");
    }

    /// <summary>
    /// The locations one variable of a GLSL type takes: 1 for scalars and vectors (2 for <c>dvec3</c>/<c>dvec4</c>), a matrix's columns; 0 for
    /// anything else.
    /// </summary>
    static int BaseSlots(string type)
    {
        switch (type)
        {
            case "float" or "int" or "uint" or "bool" or "double": return 1;
        }
        var v = VectorRegex().Match(type);
        if (v.Success) return type.StartsWith('d') && v.Groups[1].Value is "3" or "4" ? 2 : 1;
        var mx = MatrixRegex().Match(type);
        if (mx.Success) return int.Parse(mx.Groups[1].Value, CultureInfo.InvariantCulture);
        return 0;
    }

    /// <summary>
    /// Moves <paramref name="p"/> past whitespace.
    /// </summary>
    static void SkipWs(string m, ref int p, int end)
    {
        while (p < end && char.IsWhiteSpace(m[p])) p++;
    }

    /// <summary>
    /// The identifier at <paramref name="p"/> (moving past it); null when there is none.
    /// </summary>
    static string? Ident(string m, ref int p, int end)
    {
        int s = p;
        while (p < end && (char.IsLetterOrDigit(m[p]) || m[p] == '_')) p++;
        return p == s || char.IsDigit(m[s]) ? null : m[s..p];
    }

    /// <summary>
    /// The index of the parenthesis closing the one at <paramref name="open"/>; -1 when it is not closed before <paramref name="end"/>.
    /// </summary>
    static int Balanced(string m, int open, int end)
    {
        int depth = 0;
        for (int i = open; i < end; i++)
        {
            if (m[i] == '(') depth++;
            else if (m[i] == ')' && --depth == 0) return i;
        }
        return -1;
    }

    /// <summary>
    /// Comments replaced by spaces (newlines kept), so offsets and line numbers match the source.
    /// </summary>
    static string MaskComments(string s)
    {
        var sb = new StringBuilder(s);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '/' && i + 1 < s.Length && s[i + 1] == '/')
            {
                while (i < s.Length && s[i] != '\n') sb[i++] = ' ';
                i--;
            }
            else if (s[i] == '/' && i + 1 < s.Length && s[i + 1] == '*')
            {
                int j = i;
                sb[j++] = ' ';
                sb[j++] = ' ';
                while (j < s.Length && !(s[j] == '*' && j + 1 < s.Length && s[j + 1] == '/'))
                {
                    if (s[j] != '\n') sb[j] = ' ';
                    j++;
                }

                if (j < s.Length)
                {
                    sb[j] = ' ';
                    if (j + 1 < s.Length) sb[j + 1] = ' ';
                }

                i = j + 1;
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// <c>const int</c>/<c>const uint</c> NAME = number;
    /// </summary>
    [GeneratedRegex(@"\bconst\s+(?:int|uint)\s+(\w+)\s*=\s*(\d+)\s*;")]
    private static partial Regex ConstIntRegex();

    /// <summary>
    /// <c>location = N</c>.
    /// </summary>
    [GeneratedRegex(@"\blocation\s*=\s*(\d+)")]
    private static partial Regex LocationRegex();

    /// <summary>
    /// A GLSL vector type name, capturing its size.
    /// </summary>
    [GeneratedRegex(@"^[dbiu]?vec([234])$")]
    private static partial Regex VectorRegex();

    /// <summary>
    /// A GLSL matrix type name, capturing its column count.
    /// </summary>
    [GeneratedRegex(@"^d?mat([234])(?:x[234])?$")]
    private static partial Regex MatrixRegex();

    #endregion
}