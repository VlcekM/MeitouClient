using System.Globalization;
using System.Numerics;
using System.Text;
using Meitou.Content;
using Meitou.Game.Rendering.Gpu.Shaders;

namespace Meitou.Game.Rendering.Sky;

/// <summary>
/// The code mods' hooks into the lighting every lit surface goes through, <c>kenshiLight</c> of <see cref="AtmosphereShaders.Functions"/>
/// (docs/modding.md "Lighting hooks"). A mod's <c>meitou.json</c> names a GLSL file that defines any of <c>vec3 hookAlbedo(vec3 albedo)</c>,
/// <c>vec3 hookLight(vec3 light, vec3 sun, vec3 sky, vec3 n, vec3 v)</c> and <c>vec3 hookLit(vec3 lit, vec3 albedo, vec3 n, vec3 v, vec3
/// world)</c>; the game pastes the files into the lighting text once, before any lit program is compiled, and calls them behind a switch each.
/// The switches and the parameters live in one frame-global array, <c>uModLight</c>, so they change while the game runs with no recompile.
/// Without a hook the lighting text is the game's own, character for character; with every hook switched off the lit colour is the game's own
/// too (the hooks' part is skipped whole).
/// </summary>
static class LightingHooks
{
    #region Installed hooks

    /// <summary>
    /// The <c>vec4</c>s of <c>uModLight</c>: 128 numbers, a switch and the parameters of every hook.
    /// </summary>
    public const int MaxVectors = 32;

    /// <summary>
    /// The lighting hooks' textures together: <c>uModTexture0</c> to <c>uModTexture3</c>.
    /// </summary>
    public const int MaxTextures = CodeModLighting.MaxTextures;

    /// <summary>
    /// One installed hook: the mod, its GLSL with its names made its own (<see cref="LightingHookFile"/>), which of the three functions it
    /// defines, and where its numbers sit in <c>uModLight</c> (the switch at <see cref="First"/>, then the parameters in manifest order).
    /// </summary>
    internal sealed record Hook(string ModId, string Text, IReadOnlyList<CodeModLightingParam> Params, int First, bool Albedo, bool Light, bool Lit)
    {
        /// <summary>
        /// The images its GLSL reads, each with the sampler it has among <c>uModTexture0</c>..; empty when it declares none.
        /// </summary>
        public IReadOnlyList<HookTexture> Textures { get; init; } = [];
    }

    /// <summary>
    /// One texture of an installed hook: the name its GLSL reads it by, its image's full path, whether it tiles, and which of the samplers it is.
    /// </summary>
    internal readonly record struct HookTexture(string Name, string Path, bool Repeat, int Slot);

    /// <summary>
    /// A hook to install: the mod, its manifest's declaration, its file's text and the mod's folder (where its textures are).
    /// </summary>
    internal readonly record struct Source(string ModId, CodeModLighting Declared, string Text, string Folder = "");

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
    /// Installs the mods' hooks, once, before anything reads <see cref="AtmosphereShaders.Functions"/>: each file is checked and its names made
    /// its own (<see cref="LightingHookFile"/>), and the hooks together compiled in a scratch program of each stage; one that fails is left out
    /// and its reason returned for the log. Too late (the lighting text is made), every hook is left out.
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
            var hook = Make(source, hooks.Count, next, hooks.Sum(h => h.Textures.Count), out string? reason);
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
    /// Hook <paramref name="index"/> of a checked source with its numbers from <paramref name="first"/> and its textures from sampler
    /// <paramref name="firstTexture"/>; null, with the reason, when the file or the room fails.
    /// </summary>
    static Hook? Make(in Source source, int index, int first, int firstTexture, out string? reason)
    {
        reason = first + 1 + source.Declared.Params.Count > MaxVectors * 4
            ? $"the lighting hooks hold {MaxVectors * 4} numbers together, and they are taken"
            : firstTexture + source.Declared.Textures.Count > MaxTextures ? $"the lighting hooks hold {MaxTextures} textures together, and they are taken"
            : null;
        if (reason is not null) return null;

        var read = LightingHookFile.Parse(source.Text, index, out reason);
        if (read is null) return null;

        string folder = source.Folder;
        return new Hook(source.ModId, read.Text, source.Declared.Params, first, read.Albedo, read.Light, read.Lit)
        {
            Textures = [.. source.Declared.Textures.Select((t, i) => new HookTexture(t.Name, Path.Combine(folder, t.File), t.Repeat, firstTexture + i))],
        };
    }

    /// <summary>
    /// The sampler of a texture slot, as the lighting text and the frame globals name it.
    /// </summary>
    internal static string TextureUniform(int slot) => string.Create(CultureInfo.InvariantCulture, $"uModTexture{slot}");

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

    #region Lighting text

    /// <summary>
    /// The head of the lighting function the hooks' text goes before.
    /// </summary>
    const string LightHead = "vec3 kenshiLight(vec3 albedo, vec3 n, vec3 v, float gloss, vec3 world, vec3 diffuseNl)";

    /// <summary>
    /// The line of the lighting function that sums the sun's and the sky's light: the lamps' light is declared before it for the hooks.
    /// </summary>
    const string SumLine = "vec3 lit = albedo * (diffuse + envDiffuse) + sunSpecular + envSpecular;";

    /// <summary>
    /// The line that adds the lamps' light: the hooks keep its diffuse share after it.
    /// </summary>
    const string LampLine = "lit += albedo * lampDiffuse + lampSpecular;";

    /// <summary>
    /// The lighting function's return: the hooks act before it.
    /// </summary>
    const string ReturnLine = "return lit;";

    /// <summary>
    /// What goes before <see cref="SumLine"/> and after <see cref="LampLine"/>: the lamps' diffuse light, which the lamps' block keeps to itself.
    /// </summary>
    const string BeforeSum = "vec3 mhLamp = vec3(0.0); ", AfterLamp = " mhLamp = lampDiffuse;";

    /// <summary>
    /// What goes before <see cref="ReturnLine"/>, run only while a hook is on. The lit colour is taken apart into the albedo times its diffuse
    /// light (sun, sky or probes, lamps) and the rest (speculars, a character surface's own terms); the albedo and the light go through the
    /// hooks, then the whole through <c>hookLit</c>. <c>hookLight</c>'s <c>sun</c> is the sun as it reaches the point (shadowed, a surface
    /// facing it), its <c>sky</c> the light with no sun in it (the sky's or the probes', and the lamps'), so the light never exceeds their sum. What later passes add is brought along: the traced sun's term becomes the hooked colour
    /// with the sun less the hooked colour without it (exact where the rays' mask is 0 or 1, a straight line between), and the albedo stored for
    /// the indirect light's resolve, the reflections and the lamps' traced shadows becomes the hooked albedo times the gain the hooks gave the
    /// light and the lit colour, so what those passes add or take back follows the hooks to the first order.
    /// </summary>
    const string BeforeReturn = """
        if (meitouHookOn())
        {
            hookSunDirection = uAtmoSun.xyz; hookSunColour = uAtmoSunLight; hookWorld = world; hookSeconds = uWetClock;
            vec3 mhDirect = diffuse + envDiffuse + mhLamp;
            vec3 mhRest = lit - albedo * mhDirect;
            vec3 mhSun = ATMO_PI * sun * fresnelDiffuse;
            vec3 mhAmbient = envDiffuse + mhLamp;
            vec3 mhAlbedo = meitouHookAlbedo(albedo);
            vec3 mhLight = meitouHookLight(mhDirect, mhSun, mhAmbient, n, v);
            vec3 mhBefore = mhAlbedo * mhLight + mhRest;
            vec3 mhAfter = meitouHookLit(mhBefore, mhAlbedo, n, v, world);
            if (tracedShare > 0.0)
            {
                vec3 mhTraced = ATMO_PI * diffuseNl * tracedSun * fresnelDiffuse;
                vec3 mhSunWith = mhSun + ATMO_PI * tracedSun * fresnelDiffuse;
                vec3 mhWith = mhAlbedo * meitouHookLight(mhDirect + mhTraced, mhSunWith, mhAmbient, n, v) + mhRest + tracedSunOut - albedo * mhTraced;
                tracedSunOut = meitouHookLit(mhWith, mhAlbedo, n, v, world) - mhAfter;
            }
            vec3 mhGain = meitouHookRatio(mhAfter, mhBefore);
            vec3 mhEffective = mhAlbedo * meitouHookRatio(mhLight, mhDirect) * mhGain;
            if ((giAlbedoOut.z & 65535u) != 0u || (giAlbedoOut.w & 255u) != 0u)
            {
                uvec3 mhStored = uvec3(sqrt(clamp(mhEffective, 0.0, 1.0)) * 255.0 + 0.5);
                giAlbedoOut.z = (giAlbedoOut.z & 4294901760u) | mhStored.r | (mhStored.g << 8);
                giAlbedoOut.w = (giAlbedoOut.w & 4294967040u) | mhStored.b;
            }
            float mhLuma = dot(mhGain, vec3(0.2126, 0.7152, 0.0722));
            lampDiffuseOut = dot(mhEffective * mhLamp, vec3(1.0 / 3.0));
            lampSpecularOut *= mhLuma;
            ambientGlossOut *= mhLuma;
            lit = mhAfter;
        }

        """;

    /// <summary>
    /// What a hook reads beside its arguments, set before the hooks run: the direction to the sun, the sun's colour, the lit point in the world
    /// and the game clock in seconds (wrapping at 4096). And the gain of one colour over another, per channel, 1 where there is nothing to divide.
    /// </summary>
    const string Inputs = """
        vec3 hookSunDirection = vec3(0.0, 1.0, 0.0);
        vec3 hookSunColour = vec3(0.0);
        vec3 hookWorld = vec3(0.0);
        float hookSeconds = 0.0;
        vec3 meitouHookRatio(vec3 a, vec3 b) { return clamp(mix(vec3(1.0), a / max(b, vec3(1e-5)), step(vec3(1e-5), b)), 0.0, 8.0); }

        """;

    /// <summary>
    /// <paramref name="functions"/> (the atmosphere text) with the installed hooks in <c>kenshiLight</c>; unchanged without a hook. From here on
    /// no hook can be installed. A text that no longer holds the lines the hooks go by leaves them out, with a log line, rather than fail.
    /// </summary>
    public static string Apply(string functions)
    {
        s_applied = true;
        if (s_hooks.Length == 0) return functions;

        int head = functions.IndexOf(LightHead, StringComparison.Ordinal);
        int sum = head < 0 ? -1 : functions.IndexOf(SumLine, head, StringComparison.Ordinal);
        int lamp = sum < 0 ? -1 : functions.IndexOf(LampLine, sum, StringComparison.Ordinal);
        int end = lamp < 0 ? -1 : functions.IndexOf(ReturnLine, lamp, StringComparison.Ordinal);
        if (end < 0)
        {
            Console.Error.WriteLine("mods      lighting hooks left out: the lighting text no longer holds the lines they go by");
            s_hooks = [];
            return functions;
        }

        // Back to front, so the earlier places stay where they were found.
        string text = functions.Insert(end, BeforeReturn.ReplaceLineEndings("\n"));
        text = text.Insert(lamp + LampLine.Length, AfterLamp);
        text = text.Insert(sum, BeforeSum);
        return text.Insert(head, Block(s_hooks) + "\n");
    }

    /// <summary>
    /// The hooks' GLSL: <c>uModLight</c>, the textures' samplers, what a hook reads (<see cref="Inputs"/>), each mod's file with its parameters
    /// given their place in <c>uModLight</c> and its textures their sampler, then the three functions the lighting calls, which run each mod's behind its switch, and whether any switch is on.
    /// </summary>
    internal static string Block(IReadOnlyList<Hook> hooks)
    {
        var sb = new StringBuilder();
        sb.Append("\nuniform vec4 uModLight[").Append(MaxVectors).Append("];\n");
        foreach (var texture in hooks.SelectMany(h => h.Textures)) sb.Append("uniform sampler2D ").Append(TextureUniform(texture.Slot)).Append(";\n");
        sb.Append(Inputs.ReplaceLineEndings("\n"));
        foreach (var hook in hooks) AppendHook(sb, hook);
        AppendCalls(sb, hooks, LightingHookFile.Albedo, "vec3 albedo", "albedo", h => h.Albedo);
        AppendCalls(sb, hooks, LightingHookFile.Light, "vec3 light, vec3 sun, vec3 sky, vec3 n, vec3 v", "light, sun, sky, n, v", h => h.Light);
        AppendCalls(sb, hooks, LightingHookFile.Lit, "vec3 lit, vec3 albedo, vec3 n, vec3 v, vec3 world", "lit, albedo, n, v, world", h => h.Lit);
        sb.Append("bool meitouHookOn() { return ").AppendJoin(" || ", hooks.Select(h => Slot(h.First) + " > 0.5")).Append("; }\n");

        return sb.ToString();
    }

    /// <summary>
    /// One mod's file between the macros that give its parameters their place in <c>uModLight</c> (<c>PARAM_name</c>) and its textures their
    /// sampler (<c>TEXTURE_name</c>).
    /// </summary>
    static void AppendHook(StringBuilder sb, Hook hook)
    {
        sb.Append("// ---- lighting hook of mod ").Append(hook.ModId).Append(" ----\n");
        for (int p = 0; p < hook.Params.Count; p++)
            sb.Append("#define PARAM_").Append(hook.Params[p].Name).Append(' ').Append(Slot(hook.First + 1 + p)).Append('\n');
        foreach (var t in hook.Textures) sb.Append("#define TEXTURE_").Append(t.Name).Append(' ').Append(TextureUniform(t.Slot)).Append('\n');
        sb.Append(hook.Text.ReplaceLineEndings("\n")).Append('\n');
        foreach (var p in hook.Params) sb.Append("#undef PARAM_").Append(p.Name).Append('\n');
        foreach (var t in hook.Textures) sb.Append("#undef TEXTURE_").Append(t.Name).Append('\n');
    }

    /// <summary>
    /// The function the lighting calls for one of the three hooks (<c>meitouHookAlbedo</c>, <c>Light</c>, <c>Lit</c>): each mod's that defines
    /// it, in load order, behind its switch.
    /// </summary>
    static void AppendCalls(StringBuilder sb, IReadOnlyList<Hook> hooks, string function, string parameters, string arguments, Func<Hook, bool> has)
    {
        string value = arguments.Split(',')[0];
        sb.Append("vec3 meitouHook").Append(function["hook".Length..]).Append('(').Append(parameters).Append(")\n{\n");
        for (int i = 0; i < hooks.Count; i++)
        {
            if (!has(hooks[i])) continue;

            sb.Append("    if (").Append(Slot(hooks[i].First)).Append(" > 0.5) ").Append(value).Append(" = ")
                .Append(LightingHookFile.Renamed(i, function)).Append('(').Append(arguments).Append(");\n");
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
        string vertex = head + "out vec3 vLit;\nvoid main() { vLit = meitouHookOn() ? " + call + " : vec3(0.0); gl_Position = vec4(0.0, 0.0, 0.0, 1.0); }\n";
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
