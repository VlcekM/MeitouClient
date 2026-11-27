using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using Meitou.Content;
using Meitou.Game.Rendering.Gpu.Shaders;

namespace Meitou.Game.Rendering.Sky;

/// <summary>
/// The code mods' hooks into the lighting every lit surface goes through, <c>kenshiLight</c> of <see cref="AtmosphereShaders.Functions"/>
/// (docs/modding.md "Lighting hooks"). A mod's <c>meitou.json</c> names a GLSL file that defines any of <c>vec3 hookAlbedo(vec3 albedo)</c>,
/// <c>vec3 hookLight(vec3 light, vec3 sun, vec3 sky, vec3 n, vec3 v)</c> and <c>vec3 hookLit(vec3 lit, vec3 albedo, vec3 n, vec3 v, vec3
/// world)</c>; the game pastes the files into the lighting text once, before any lit program is compiled, and calls them behind a switch each.
/// The switches and the parameters live in one frame-global array, <c>uModLight</c>, so they change while the game runs with no recompile.
/// Without a hook the lighting text is the game's own, character for character.
/// </summary>
static partial class LightingHooks
{
    #region Installed hooks

    /// <summary>
    /// The <c>vec4</c>s of <c>uModLight</c>: 128 numbers, a switch and the parameters of every hook.
    /// </summary>
    public const int MaxVectors = 32;

    /// <summary>
    /// The longest hook file, in characters.
    /// </summary>
    public const int MaxFileLength = 128 * 1024;

    /// <summary>
    /// One installed hook: the mod, its GLSL, which of the three functions it defines, and where its numbers sit in <c>uModLight</c> (the
    /// switch at <see cref="First"/>, then the parameters in manifest order).
    /// </summary>
    internal sealed record Hook(string ModId, string Text, IReadOnlyList<CodeModLightingParam> Params, int First, bool Albedo, bool Light, bool Lit);

    /// <summary>
    /// A hook to install: the mod, its manifest's declaration and its file's text.
    /// </summary>
    internal readonly record struct Source(string ModId, CodeModLighting Declared, string Text);

    /// <summary>
    /// What <c>uModLight</c> holds: each hook's switch (1 on) and parameters. Written by the mods' handles, read each frame by the frame globals.
    /// </summary>
    public static readonly Vector4[] Values = new Vector4[MaxVectors];

    /// <summary>
    /// The hooks in the lighting text, in load order.
    /// </summary>
    static Hook[] s_hooks = [];

    /// <summary>
    /// Whether <see cref="Apply"/> ran: the lighting text is made, so hooks can no longer be installed.
    /// </summary>
    static bool s_applied;

    /// <summary>
    /// The hooks in the lighting text.
    /// </summary>
    internal static IReadOnlyList<Hook> Installed => s_hooks;

    /// <summary>
    /// How many <c>vec4</c>s of <see cref="Values"/> the hooks use (0 without a hook).
    /// </summary>
    public static int Vectors => s_hooks.Length == 0 ? 0 : (s_hooks[^1].First + s_hooks[^1].Params.Count + 4) / 4;

    /// <summary>
    /// The hook of a mod, or null when it has none in the lighting text.
    /// </summary>
    internal static Hook? Of(string modId) => s_hooks.FirstOrDefault(h => h.ModId == modId);

    /// <summary>
    /// Installs the mods' hooks, once, before anything reads <see cref="AtmosphereShaders.Functions"/>: each file is checked (<see cref="Check"/>)
    /// and the hooks together compiled in a scratch program of each stage; one that fails is left out and its reason returned for the log. Too
    /// late (the lighting text is made), every hook is left out.
    /// </summary>
    internal static List<(string ModId, string Reason)> Install(IReadOnlyList<Source> sources)
    {
        var refused = new List<(string, string)>();
        if (s_applied)
        {
            refused.AddRange(sources.Select(s => (s.ModId, "the lighting shaders are made already")));
            return refused;
        }

        var hooks = new List<Hook>();
        int next = 0;
        foreach (var source in sources)
        {
            var hook = Make(source, next, out string? reason);
            reason ??= hook is null ? null : CompileProblem([.. hooks, hook]);
            if (hook is null || reason is not null)
            {
                refused.Add((source.ModId, reason ?? "it holds no hook"));
                continue;
            }

            hooks.Add(hook);
            next += 1 + hook.Params.Count;
        }
        s_hooks = [.. hooks];
        foreach (var hook in hooks) Reset(hook);

        return refused;
    }

    /// <summary>
    /// A hook of a checked source with its numbers from <paramref name="first"/>; null, with the reason, when the file or the room fails.
    /// </summary>
    static Hook? Make(in Source source, int first, out string? reason)
    {
        reason = Check(source.Text);
        if (reason is null && first + 1 + source.Declared.Params.Count > MaxVectors * 4)
            reason = $"the lighting hooks hold {MaxVectors * 4} numbers together, and they are taken";
        if (reason is not null) return null;

        string text = Clean(source.Text);
        bool albedo = Defines(text, "hookAlbedo"), light = Defines(text, "hookLight"), lit = Defines(text, "hookLit");
        if (!albedo && !light && !lit) reason = "it defines none of hookAlbedo, hookLight and hookLit";

        return reason is null ? new Hook(source.ModId, source.Text, source.Declared.Params, first, albedo, light, lit) : null;
    }

    /// <summary>
    /// Turns a mod's hook on or off (nothing for a mod without one): on while the mod runs, from its <c>Initialize</c>.
    /// </summary>
    internal static void Switch(string modId, bool on)
    {
        if (Of(modId) is { } hook) Write(hook.First, on ? 1 : 0);
    }

    /// <summary>
    /// Puts a hook's switch off (the mod's start turns it on) and its parameters at the values the manifest starts them at.
    /// </summary>
    static void Reset(Hook hook)
    {
        Write(hook.First, 0);
        for (int i = 0; i < hook.Params.Count; i++) Write(hook.First + 1 + i, hook.Params[i].Start);
    }

    /// <summary>
    /// One number of <c>uModLight</c> by its index.
    /// </summary>
    internal static void Write(int index, float value)
    {
        ref var vector = ref Values[index / 4];
        vector[index % 4] = value;
    }

    /// <summary>
    /// One number of <c>uModLight</c> by its index.
    /// </summary>
    internal static float Read(int index) => Values[index / 4][index % 4];

    /// <summary>
    /// The test hook: forgets every hook, so a test can install its own (the lighting text made before stays as it was).
    /// </summary>
    internal static void ForgetForTests()
    {
        s_hooks = [];
        s_applied = false;
        Array.Clear(Values);
    }

    #endregion

    #region File check

    /// <summary>
    /// The words a hook file may not hold at file scope: it declares no inputs, outputs or uniforms of its own.
    /// </summary>
    static readonly string[] Refused = ["uniform", "in", "out", "inout", "layout", "varying", "attribute", "precision"];

    /// <summary>
    /// Why a hook file cannot go into the lighting shaders, or null when it can: every name it declares at file scope (a function, a constant, a
    /// struct, a macro) starts with <c>hook</c> or <c>HOOK</c>, so it cannot clash with the game's text; no <c>#version</c>, <c>#extension</c> or
    /// <c>#include</c>, no input, output or uniform; nothing a vertex stage lacks (<c>discard</c>, derivatives), as the grass lights its vertices.
    /// </summary>
    internal static string? Check(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "the file is empty";
        if (text.Length > MaxFileLength) return $"the file is longer than {MaxFileLength} characters";

        string clean = Clean(text);
        if (VertexLacksRegex().Match(clean) is { Success: true } lacks) return $"'{lacks.Value}' is not for a vertex stage, which the grass lights in";

        return DirectiveProblem(clean) ?? ScopeProblem(clean);
    }

    /// <summary>
    /// The first preprocessor line a hook may not hold, or a macro whose name does not start with <c>HOOK</c> or <c>hook</c>.
    /// </summary>
    static string? DirectiveProblem(string clean)
    {
        foreach (Match m in DirectiveRegex().Matches(clean))
        {
            string kind = m.Groups["kind"].Value, name = m.Groups["name"].Value;
            if (kind is "version" or "extension" or "include" or "pragma" or "line") return $"#{kind} is not allowed";
            if (kind is "define" or "undef" && !IsHookName(name)) return $"the macro '{name}' does not start with HOOK";
        }

        return null;
    }

    /// <summary>
    /// The first name declared at file scope that does not start with <c>hook</c>, or a refused word there.
    /// </summary>
    static string? ScopeProblem(string clean)
    {
        string scope = FileScope(clean);
        foreach (Match m in WordRegex().Matches(scope))
        {
            if (Refused.Contains(m.Value)) return $"'{m.Value}' at file scope: a hook declares no input, output or uniform";
        }
        foreach (Match m in DeclaredRegex().Matches(scope))
        {
            string name = m.Groups["name"].Value;
            if (!IsHookName(name)) return $"'{name}' is declared at file scope: every name a hook file declares starts with hook";
        }

        return null;
    }

    /// <summary>
    /// Whether a name is the hook's own: it starts with <c>hook</c> or <c>HOOK</c>.
    /// </summary>
    static bool IsHookName(string name) => name.StartsWith("hook", StringComparison.Ordinal) || name.StartsWith("HOOK", StringComparison.Ordinal);

    /// <summary>
    /// Whether the text defines a function of that name returning <c>vec3</c>.
    /// </summary>
    static bool Defines(string clean, string function) => Regex.IsMatch(clean, $@"\bvec3\s+{function}\s*\(");

    /// <summary>
    /// The text without its comments (a comment's words are not code), a line a backslash continues joined to the next.
    /// </summary>
    static string Clean(string text) => CommentRegex().Replace(ContinuedRegex().Replace(text, ""), " ");

    /// <summary>
    /// A backslash at a line's end: the line goes on.
    /// </summary>
    [GeneratedRegex(@"\\\r?\n")]
    private static partial Regex ContinuedRegex();

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
    /// What a vertex stage cannot compile.
    /// </summary>
    [GeneratedRegex(@"\b(discard|dFdx|dFdy|fwidth|gl_FragCoord|gl_FragDepth|gl_FrontFacing)\b")]
    private static partial Regex VertexLacksRegex();

    #endregion

    #region Lighting text

    /// <summary>
    /// The head of the lighting function the hooks' text goes before.
    /// </summary>
    const string LightHead = "vec3 kenshiLight(vec3 albedo, vec3 n, vec3 v, float gloss, vec3 world, vec3 diffuseNl)";

    /// <summary>
    /// The line of the lighting function that sums the sun's and the sky's light: <c>hookAlbedo</c> and <c>hookLight</c> act before it.
    /// </summary>
    const string SumLine = "vec3 lit = albedo * (diffuse + envDiffuse) + sunSpecular + envSpecular;";

    /// <summary>
    /// The lighting function's return: <c>hookLit</c> acts before it.
    /// </summary>
    const string ReturnLine = "return lit;";

    /// <summary>
    /// What goes before <see cref="SumLine"/>: the light the albedo is multiplied by through the hooks (the sky's share stays, the difference goes
    /// to the sun's term, which only that line reads), then the albedo.
    /// </summary>
    const string BeforeSum = "{ vec3 hookedLight = meitouHookLight(diffuse + envDiffuse, ATMO_PI * sun * fresnelDiffuse, envDiffuse, n, v); "
        + "diffuse = hookedLight - envDiffuse; albedo = meitouHookAlbedo(albedo); }";

    /// <summary>
    /// What goes before <see cref="ReturnLine"/>.
    /// </summary>
    const string BeforeReturn = "lit = meitouHookLit(lit, albedo, n, v, world);";

    /// <summary>
    /// <paramref name="functions"/> (the atmosphere text) with the installed hooks in <c>kenshiLight</c>; unchanged without a hook. From here on
    /// no hook can be installed. A text that no longer holds the places the hooks go leaves them out, with a log line, rather than fail.
    /// </summary>
    public static string Apply(string functions)
    {
        s_applied = true;
        if (s_hooks.Length == 0) return functions;

        int head = functions.IndexOf(LightHead, StringComparison.Ordinal);
        int sum = head < 0 ? -1 : functions.IndexOf(SumLine, head, StringComparison.Ordinal);
        int end = sum < 0 ? -1 : functions.IndexOf(ReturnLine, sum, StringComparison.Ordinal);
        if (end < 0)
        {
            Console.Error.WriteLine("mods      lighting hooks left out: the lighting text no longer holds the lines they go by");
            s_hooks = [];
            return functions;
        }

        // Back to front, so the earlier places stay where they were found.
        string text = functions.Insert(end, BeforeReturn + " ");
        text = text.Insert(sum, BeforeSum + " ");
        return text.Insert(head, Block(s_hooks) + "\n");
    }

    /// <summary>
    /// The hooks' GLSL: <c>uModLight</c>, each mod's file with its three functions and its parameters given names of its own, then the three
    /// functions the lighting calls, which run each mod's behind its switch.
    /// </summary>
    internal static string Block(IReadOnlyList<Hook> hooks)
    {
        var sb = new StringBuilder();
        sb.Append("\nuniform vec4 uModLight[").Append(MaxVectors).Append("];\n");
        for (int i = 0; i < hooks.Count; i++) AppendHook(sb, hooks[i], i);
        AppendCalls(sb, hooks, "Albedo", "vec3 albedo", "albedo", h => h.Albedo);
        AppendCalls(sb, hooks, "Light", "vec3 light, vec3 sun, vec3 sky, vec3 n, vec3 v", "light, sun, sky, n, v", h => h.Light);
        AppendCalls(sb, hooks, "Lit", "vec3 lit, vec3 albedo, vec3 n, vec3 v, vec3 world", "lit, albedo, n, v, world", h => h.Lit);

        return sb.ToString();
    }

    /// <summary>
    /// One mod's file between the macros that give its three functions names of its own and its parameters their place in <c>uModLight</c>
    /// (<c>PARAM_name</c>).
    /// </summary>
    static void AppendHook(StringBuilder sb, Hook hook, int index)
    {
        string[] functions = ["hookAlbedo", "hookLight", "hookLit"];
        sb.Append("// ---- lighting hook of mod ").Append(hook.ModId).Append(" ----\n");
        foreach (string f in functions) sb.Append("#define ").Append(f).Append(" meitouHook").Append(index).Append(f[4..]).Append('\n');
        for (int p = 0; p < hook.Params.Count; p++)
            sb.Append("#define PARAM_").Append(hook.Params[p].Name).Append(' ').Append(Slot(hook.First + 1 + p)).Append('\n');
        sb.Append(hook.Text.ReplaceLineEndings("\n")).Append('\n');
        foreach (var p in hook.Params) sb.Append("#undef PARAM_").Append(p.Name).Append('\n');
        foreach (string f in functions) sb.Append("#undef ").Append(f).Append('\n');
    }

    /// <summary>
    /// The function the lighting calls for one of the three hooks: each mod's that defines it, in load order, behind its switch.
    /// </summary>
    static void AppendCalls(StringBuilder sb, IReadOnlyList<Hook> hooks, string name, string parameters, string arguments, Func<Hook, bool> has)
    {
        string value = arguments.Split(',')[0];
        sb.Append("vec3 meitouHook").Append(name).Append('(').Append(parameters).Append(")\n{\n");
        for (int i = 0; i < hooks.Count; i++)
        {
            if (!has(hooks[i])) continue;

            sb.Append("    if (").Append(Slot(hooks[i].First)).Append(" > 0.5) ").Append(value).Append(" = meitouHook").Append(i).Append(name)
                .Append('(').Append(arguments).Append(");\n");
        }
        sb.Append("    return ").Append(value).Append(";\n}\n");
    }

    /// <summary>
    /// The GLSL of one number of <c>uModLight</c>.
    /// </summary>
    static string Slot(int index) => string.Create(CultureInfo.InvariantCulture, $"uModLight[{index / 4}].{"xyzw"[index % 4]}");

    /// <summary>
    /// Why the hooks together do not compile, or null when they do: a scratch program whose vertex and fragment stages both hold the hooks'
    /// text and call the three functions, compiled as a lit program's is.
    /// </summary>
    static string? CompileProblem(IReadOnlyList<Hook> hooks)
    {
        const string call = "meitouHookLit(meitouHookAlbedo(vec3(0.5)) * meitouHookLight(vec3(1.0), vec3(1.0), vec3(0.2), vec3(0.0, 1.0, 0.0), "
            + "vec3(0.0, 0.0, 1.0)), vec3(0.5), vec3(0.0, 1.0, 0.0), vec3(0.0, 0.0, 1.0), vec3(0.0))";
        string head = "#version 330 core\n" + Block(hooks);
        string vertex = head + "out vec3 vLit;\nvoid main() { vLit = " + call + "; gl_Position = vec4(0.0, 0.0, 0.0, 1.0); }\n";
        string fragment = head + "in vec3 vLit;\nout vec4 fragColour;\nvoid main() { fragColour = vec4(vLit + " + call + ", 1.0); }\n";
        try
        {
            new GlslProgramCompiler().Compile(vertex, fragment);
            return null;
        }
        catch (ShaderCompileException e)
        {
            return e.Message;
        }
    }

    #endregion
}
