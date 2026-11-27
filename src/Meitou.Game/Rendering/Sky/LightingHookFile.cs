using System.Text;
using System.Text.RegularExpressions;

namespace Meitou.Game.Rendering.Sky;

/// <summary>
/// A code mod's lighting hook file as it goes into the lighting shaders (docs/modding.md "Lighting hooks"): checked, and every name it
/// declares at file scope (function, constant, struct, macro) given a name of its own, so it cannot clash with the game's text or another
/// mod's whatever it calls its helpers.
/// </summary>
static partial class LightingHookFile
{
    /// <summary>
    /// The longest hook file, in characters.
    /// </summary>
    public const int MaxLength = 128 * 1024;

    /// <summary>
    /// The three functions a file may define, by the names the mod writes.
    /// </summary>
    public const string Albedo = "hookAlbedo", Light = "hookLight", Lit = "hookLit";

    /// <summary>
    /// A file ready for the shaders: its text with its declared names renamed, and which of the three functions it defines.
    /// </summary>
    internal sealed record Read(string Text, bool Albedo, bool Light, bool Lit);

    /// <summary>
    /// The words a hook file may not hold at file scope: it declares no inputs, outputs or uniforms of its own.
    /// </summary>
    static readonly string[] Refused = ["uniform", "in", "out", "inout", "layout", "varying", "attribute", "precision"];

    /// <summary>
    /// GLSL's own words: found where a declared name should be, the declaration is one the check cannot read (an array type before the name).
    /// </summary>
    static readonly HashSet<string> Reserved =
    [
        "void", "bool", "int", "uint", "float", "double", "vec2", "vec3", "vec4", "ivec2", "ivec3", "ivec4", "uvec2", "uvec3", "uvec4", "bvec2",
        "bvec3", "bvec4", "mat2", "mat3", "mat4", "sampler2D", "sampler3D", "samplerCube", "usampler2D", "isampler2D", "const", "struct", "return",
        "if", "else", "for", "while", "do", "switch", "case", "default", "break", "continue", "true", "false", "highp", "mediump", "lowp", "main",
    ];

    /// <summary>
    /// The name a declared <paramref name="name"/> of the hook at <paramref name="index"/> gets in the shaders.
    /// </summary>
    public static string Renamed(int index, string name) => $"meitouHook{index}_{name}";

    /// <summary>
    /// Why a hook file cannot go into the lighting shaders, or null when it can: no <c>#version</c>, <c>#extension</c> or <c>#include</c>, no
    /// input, output or uniform, nothing a vertex stage lacks (<c>discard</c>, derivatives: the grass lights its vertices), and names the check
    /// can give names of their own.
    /// </summary>
    internal static string? Check(string text)
    {
        Parse(text, 0, out string? problem);
        return problem;
    }

    /// <summary>
    /// The file as hook <paramref name="index"/> of the shaders, its declared names renamed; null, with the reason, when it cannot go in.
    /// </summary>
    internal static Read? Parse(string text, int index, out string? problem)
    {
        problem = Problem(text, out var names);
        if (problem is not null) return null;

        string clean = Clean(text);
        var read = new Read(Rename(text, names, index), Defines(clean, Albedo), Defines(clean, Light), Defines(clean, Lit));
        if (!read.Albedo && !read.Light && !read.Lit) problem = $"it defines none of {Albedo}, {Light} and {Lit}";

        return problem is null ? read : null;
    }

    /// <summary>
    /// The first reason the file is refused, or null with the names it declares at file scope.
    /// </summary>
    static string? Problem(string text, out List<string> names)
    {
        names = [];
        if (string.IsNullOrWhiteSpace(text)) return "the file is empty";
        if (text.Length > MaxLength) return $"the file is longer than {MaxLength} characters";

        string clean = Clean(text);
        if (VertexLacksRegex().Match(clean) is { Success: true } lacks) return $"'{lacks.Value}' is not for a vertex stage, which the grass lights in";

        return DirectiveProblem(clean, names) ?? ScopeProblem(clean, names) ?? names.Select(NameProblem).FirstOrDefault(p => p is not null);
    }

    /// <summary>
    /// The first preprocessor line a hook may not hold; the macros it defines join <paramref name="names"/>.
    /// </summary>
    static string? DirectiveProblem(string clean, List<string> names)
    {
        foreach (Match m in DirectiveRegex().Matches(clean))
        {
            string kind = m.Groups["kind"].Value, name = m.Groups["name"].Value;
            if (kind is "version" or "extension" or "include" or "pragma" or "line") return $"#{kind} is not allowed";
            if (kind == "define" && name.Length > 0 && !names.Contains(name)) names.Add(name);
        }

        return null;
    }

    /// <summary>
    /// A refused word at file scope; the names declared there join <paramref name="names"/>.
    /// </summary>
    static string? ScopeProblem(string clean, List<string> names)
    {
        string scope = FileScope(clean);
        foreach (Match m in WordRegex().Matches(scope))
        {
            if (Refused.Contains(m.Value)) return $"'{m.Value}' at file scope: a hook declares no input, output or uniform";
        }
        foreach (Match m in DeclaredRegex().Matches(scope))
        {
            string name = m.Groups["name"].Value;
            if (!names.Contains(name)) names.Add(name);
        }

        return null;
    }

    /// <summary>
    /// Why a declared name cannot be renamed: GLSL's own word (the declaration was not read), a swizzle (every <c>.xy</c> would be renamed with
    /// it), or a name the game keeps for itself.
    /// </summary>
    static string? NameProblem(string name)
    {
        if (Reserved.Contains(name)) return $"the declaration at '{name}' cannot be read: put the name right after its type";
        if (SwizzleRegex().IsMatch(name)) return $"'{name}' is declared at file scope: a name made of xyzw, rgba or stpq alone would rename every swizzle";
        bool kept = name.StartsWith("gl_", StringComparison.Ordinal) || name.StartsWith("meitou", StringComparison.Ordinal)
            || name.StartsWith("PARAM_", StringComparison.Ordinal) || name.StartsWith("TEXTURE_", StringComparison.Ordinal);

        return kept ? $"'{name}' is declared at file scope: gl_, meitou, PARAM_ and TEXTURE_ names are the game's" : null;
    }

    /// <summary>
    /// The text with every declared name, wherever it stands, replaced by its own name in the shaders.
    /// </summary>
    static string Rename(string text, List<string> names, int index)
    {
        if (names.Count == 0) return text;

        var renamed = names.ToDictionary(n => n, n => Renamed(index, n), StringComparer.Ordinal);
        return WordRegex().Replace(text, m => renamed.GetValueOrDefault(m.Value, m.Value));
    }

    /// <summary>
    /// Whether the text defines a function of that name returning <c>vec3</c>.
    /// </summary>
    static bool Defines(string clean, string function) => Regex.IsMatch(clean, $@"\bvec3\s+{function}\s*\(");

    /// <summary>
    /// The text without its comments (a comment's words are not code), a line a backslash continues joined to the next.
    /// </summary>
    static string Clean(string text) => CommentRegex().Replace(ContinuedRegex().Replace(text, ""), " ");

    /// <summary>
    /// The text at file scope: the preprocessor lines, what is inside braces and parentheses (their opening character kept) and what a
    /// declaration is set to (from its <c>=</c> to its <c>,</c> or <c>;</c>) blanked, so the names left before a <c>(</c>, <c>{</c>, <c>=</c>,
    /// <c>;</c>, <c>,</c> or <c>[</c> are the declared ones.
    /// </summary>
    static string FileScope(string clean)
    {
        var scope = new StringBuilder(clean.Length);
        int depth = 0;
        bool value = false;
        foreach (char c in DirectiveRegex().Replace(clean, " "))
        {
            bool opens = c is '{' or '(';
            if (opens) depth++;
            if (depth == 0 && c is ',' or ';') value = false;

            bool kept = !value && (depth == 0 || opens && depth == 1);
            scope.Append(kept ? c : ' ');
            if (depth == 0 && c == '=') value = true;
            if (c is '}' or ')') depth = Math.Max(depth - 1, 0);
        }

        return scope.ToString();
    }

    /// <summary>
    /// A backslash at a line's end: the line goes on.
    /// </summary>
    [GeneratedRegex(@"\\\r?\n")]
    private static partial Regex ContinuedRegex();

    /// <summary>
    /// A <c>//</c> or <c>/* */</c> comment.
    /// </summary>
    [GeneratedRegex(@"//[^\n]*|/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex CommentRegex();

    /// <summary>
    /// A preprocessor line: its directive and the name after it.
    /// </summary>
    [GeneratedRegex(@"(?m)^[ \t]*#[ \t]*(?<kind>\w+)[ \t]*(?<name>\w*)[^\n]*")]
    private static partial Regex DirectiveRegex();

    /// <summary>
    /// A word.
    /// </summary>
    [GeneratedRegex(@"\b[A-Za-z_]\w*\b")]
    private static partial Regex WordRegex();

    /// <summary>
    /// A name declared at file scope: the word before a function's <c>(</c>, a struct's <c>{</c>, or a variable's <c>=</c>, <c>;</c>, <c>,</c> or
    /// <c>[</c> (in <see cref="FileScope"/>'s text, where bodies and parameters are blank).
    /// </summary>
    [GeneratedRegex(@"\b(?<name>[A-Za-z_]\w*)\s*(?=[({=;,\[])")]
    private static partial Regex DeclaredRegex();

    /// <summary>
    /// A name that reads as a swizzle.
    /// </summary>
    [GeneratedRegex("^([xyzw]{1,4}|[rgba]{1,4}|[stpq]{1,4})$")]
    private static partial Regex SwizzleRegex();

    /// <summary>
    /// What a vertex stage cannot compile.
    /// </summary>
    [GeneratedRegex(@"\b(discard|dFdx|dFdy|fwidth|gl_FragCoord|gl_FragDepth|gl_FrontFacing)\b")]
    private static partial Regex VertexLacksRegex();
}
