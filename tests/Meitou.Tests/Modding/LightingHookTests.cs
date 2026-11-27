using Meitou.Content;
using Meitou.Game.Rendering.Gpu.Shaders;
using Meitou.Game.Rendering.Post;
using Meitou.Game.Rendering.Sky;

namespace Meitou.Tests.Modding;

/// <summary>
/// The code mods' lighting hooks (docs/modding.md "Lighting hooks"): which files go into the lighting shaders, the names they get there, and
/// that the lighting text with hooks in it compiles in both stages.
/// </summary>
public class LightingHookTests
{
    /// <summary>
    /// A hook with plain helper names, all three functions, parameters and the game's inputs.
    /// </summary>
    const string Toon = """
        // the light in bands
        #define BANDS 3.0
        const vec3 LUMA = vec3(0.2126, 0.7152, 0.0722);
        struct Band { float level; float edge; };
        float luma(vec3 c) { return dot(c, LUMA); }
        vec3 hookLight(vec3 light, vec3 sun, vec3 sky, vec3 n, vec3 v)
        {
            float t = clamp(luma(light) / max(luma(sun + sky), 1e-4), 0.0, 1.0);
            return mix(light, (sun + sky) * ceil(t * BANDS) / BANDS, PARAM_strength);
        }
        vec3 hookAlbedo(vec3 albedo) { return mix(vec3(luma(albedo)), albedo, PARAM_saturation); }
        vec3 hookLit(vec3 lit, vec3 albedo, vec3 n, vec3 v, vec3 world)
        {
            return lit + hookSunColour * max(dot(n, hookSunDirection), 0.0) * fract(hookWorld.y + hookSeconds) * 0.0;
        }
        """;

    /// <summary>
    /// A hook of one function, with the first one's helper name under another meaning.
    /// </summary>
    const string Tint = """
        float luma(vec3 c) { return c.g; }
        vec3 hookLit(vec3 lit, vec3 albedo, vec3 n, vec3 v, vec3 world) { return lit * vec3(PARAM_r, PARAM_g, PARAM_b) * luma(vec3(1.0)); }
        """;

    /// <summary>
    /// The two hooks as two mods declare them.
    /// </summary>
    static LightingHooks.Source[] Sources() =>
    [
        new("sample.toon", new CodeModLighting("toon.glsl", [new("strength", 1), new("saturation", 1.2f)]), Toon),
        new("sample.tint", new CodeModLighting("tint.glsl", [new("r", 1), new("g", 0.9f), new("b", 0.8f)]), Tint),
    ];

    /// <summary>
    /// Clears the hooks with the game's own lighting text already made, so no other test's first use of it takes this test's hooks in.
    /// </summary>
    static void Reset()
    {
        _ = AtmosphereShaders.Functions;
        LightingHooks.ForgetForTests();
    }

    [Theory]
    [InlineData("vec3 hookLit(vec3 lit, vec3 albedo, vec3 n, vec3 v, vec3 world) { return lit; }", null)]
    [InlineData("float luma(vec3 c) { return c.r; }\nvec3 hookLit(vec3 l, vec3 a, vec3 n, vec3 v, vec3 w) { return l * luma(a); }", null)]
    [InlineData("#define SCALE 2.0\nconst vec3 tint = vec3(1.0);\nvec3 hookAlbedo(vec3 a) { return a * SCALE * tint; }", null)]
    [InlineData("const vec3 T = vec3(1.0, 0.5, max(0.1, 0.2));\nvec3 hookAlbedo(vec3 a) { return a * T * hookSunColour; }", null)]
    [InlineData("// uniform in a comment, luma( too\nvec3 hookAlbedo(vec3 a) { float luma = a.r; return vec3(luma); }", null)]
    [InlineData("uniform float uMine;\nvec3 hookAlbedo(vec3 a) { return a * uMine; }", "'uniform'")]
    [InlineData("#version 330 core\nvec3 hookAlbedo(vec3 a) { return a; }", "#version")]
    [InlineData("vec3 hookAlbedo(vec3 a) { if (a.r < 0.1) discard; return a; }", "discard")]
    [InlineData("float xy = 1.0;\nvec3 hookAlbedo(vec3 a) { return a * xy; }", "swizzle")]
    [InlineData("float[2] w = float[2](1.0, 2.0);\nvec3 hookAlbedo(vec3 a) { return a * w[0]; }", "'float'")]
    [InlineData("float meitouMine = 1.0;\nvec3 hookAlbedo(vec3 a) { return a; }", "the game's")]
    [InlineData("float helper(float x) { return x; }", "defines none")]
    [InlineData("", "empty")]
    public void A_hook_file_goes_in_or_is_refused_with_its_reason(string text, string? problem)
    {
        string? found = LightingHookFile.Check(text);
        if (problem is null) Assert.Null(found);
        else Assert.Contains(problem, found);
    }

    [Fact]
    public void Hooks_install_with_names_of_their_own_and_those_that_do_not_compile_are_refused()
    {
        try
        {
            Reset();
            var broken = new LightingHooks.Source("sample.broken", new CodeModLighting("b.glsl", []),
                "vec3 hookLit(vec3 l, vec3 a, vec3 n, vec3 v, vec3 w) { return nothing; }");
            var engine = new LightingHooks.Source("sample.engine", new CodeModLighting("e.glsl", []),
                "vec3 hookLit(vec3 l, vec3 a, vec3 n, vec3 v, vec3 w) { return l * atmoIrradiance(n); }");
            var refused = LightingHooks.Install([.. Sources(), broken, engine]);

            Assert.Equal(["sample.toon", "sample.tint"], LightingHooks.Installed.Select(h => h.ModId));
            Assert.Equal(["sample.broken", "sample.engine"], refused.Select(r => r.ModId));
            Assert.Equal(0, LightingHooks.Of("sample.toon")!.First);
            Assert.Equal(3, LightingHooks.Of("sample.tint")!.First);
            Assert.Equal(2, LightingHooks.Vectors);   // 3 + 4 numbers
            Assert.Equal(0, LightingHooks.Read(0));   // off until the mod starts
            Assert.Equal(1.2f, LightingHooks.Read(2));
            LightingHooks.Switch("sample.tint", true);
            Assert.Equal(1, LightingHooks.Read(3));
            Assert.Equal(0.8f, LightingHooks.Read(6));

            string text = LightingHooks.Block(LightingHooks.Installed);
            Assert.Contains("#define PARAM_saturation uModLight[0].z", text);
            Assert.Contains("float meitouHook0_luma(vec3 c)", text);
            Assert.Contains("float meitouHook1_luma(vec3 c)", text);
            Assert.Contains("#define meitouHook0_BANDS 3.0", text);
            Assert.Contains("if (uModLight[0].w > 0.5) lit = meitouHook1_hookLit(lit, albedo, n, v, world);", text);
            Assert.Contains("bool meitouHookOn() { return uModLight[0].x > 0.5 || uModLight[0].w > 0.5; }", text);

            // Too late once the lighting text is made with them.
            LightingHooks.Apply(AtmosphereShaders.Functions);
            Assert.Equal(["sample.toon"], LightingHooks.Install([Sources()[0]]).Select(r => r.ModId));
        }
        finally
        {
            LightingHooks.ForgetForTests();
        }
    }

    [Fact]
    public void A_manifest_names_the_hooks_textures_as_a_file_or_with_repeat_and_refuses_one_outside_the_folder()
    {
        string folder = Path.Combine(Path.GetTempPath(), "meitou-hook-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string Manifest(string textures) => """
                { "id": "sample", "apiVersion": 2, "assemblies": ["Sample.dll"], "entry": "Sample.Mod",
                  "lighting": { "file": "toon.glsl", "params": { "strength": 1.0 }, "textures": TEXTURES } }
                """.Replace("TEXTURES", textures);
            string path = Path.Combine(folder, "meitou.json");
            File.WriteAllText(path, Manifest("""{ "ramp": "ramp.png", "hatch": { "file": "art/hatch.png", "repeat": true } }"""));
            var lighting = CodeModManifest.Read(path).Lighting!;
            Assert.Equal([new("ramp", "ramp.png", false), new("hatch", "art/hatch.png", true)], lighting.Textures);
            Assert.Equal([new CodeModLightingParam("strength", 1)], lighting.Params);

            File.WriteAllText(path, Manifest("""{ "ramp": "../ramp.png" }"""));
            Assert.Throws<InvalidDataException>(() => CodeModManifest.Read(path));
            File.WriteAllText(path, Manifest("""{ "a": "a.png", "b": "b.png", "c": "c.png", "d": "d.png", "e": "e.png" }"""));
            Assert.Throws<InvalidDataException>(() => CodeModManifest.Read(path));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void A_hooks_textures_get_samplers_in_order_and_a_hook_past_the_fourth_texture_is_refused()
    {
        try
        {
            Reset();
            const string reads = "vec3 hookLight(vec3 l, vec3 s, vec3 k, vec3 n, vec3 v) { return l * texture(TEXTURE_ramp, vec2(0.5)).rgb"
                + " * texture(TEXTURE_hatch, hookWorld.xz).r; }";
            var two = new CodeModLighting("a.glsl", []) { Textures = [new("ramp", "ramp.png", false), new("hatch", "hatch.png", true)] };
            var three = new CodeModLighting("c.glsl", [])
                { Textures = [new("ramp", "r.png", false), new("hatch", "h.png", false), new("more", "m.png", false)] };
            var refused = LightingHooks.Install(
            [
                new("sample.first", two, reads, "mods/first"),
                new("sample.second", two, reads, "mods/second"),
                new("sample.third", three, reads, "mods/third"),
            ]);

            Assert.Equal(["sample.first", "sample.second"], LightingHooks.Installed.Select(h => h.ModId));
            Assert.Contains("4 textures", Assert.Single(refused).Reason);
            Assert.Equal([0, 1], LightingHooks.Of("sample.first")!.Textures.Select(t => t.Slot));
            Assert.Equal([2, 3], LightingHooks.Of("sample.second")!.Textures.Select(t => t.Slot));
            Assert.Equal(Path.Combine("mods/second", "hatch.png"), LightingHooks.Of("sample.second")!.Textures[1].Path);
            Assert.True(LightingHooks.Of("sample.second")!.Textures[1].Repeat);

            string text = LightingHooks.Block(LightingHooks.Installed);
            Assert.Contains("uniform sampler2D uModTexture3;", text);
            Assert.DoesNotContain("uModTexture4", text);
            Assert.Contains("#define TEXTURE_hatch uModTexture3", text);
            Assert.Contains("#undef TEXTURE_hatch", text);

            // A file may not take the game's prefix for a name of its own.
            Assert.Contains("the game's", LightingHookFile.Check("float TEXTURE_mine = 1.0;\nvec3 hookAlbedo(vec3 a) { return a; }"));
        }
        finally
        {
            LightingHooks.ForgetForTests();
        }
    }

    [Fact]
    public void The_lighting_text_with_hooks_in_it_compiles_in_both_stages_and_is_the_games_own_without_any()
    {
        try
        {
            Reset();
            string own = AtmosphereShaders.Functions;
            Assert.Same(own, LightingHooks.Apply(own));

            LightingHooks.ForgetForTests();
            Assert.Empty(LightingHooks.Install(Sources()));
            string hooked = LightingHooks.Apply(own);
            Assert.Contains("meitouHookOn()", hooked);
            Assert.Contains("mhLamp = lampDiffuse;", hooked);

            // A lit vertex stage (as the grass's) and a lit fragment stage, each with the whole lighting text.
            const string call = "kenshiLight(vec3(0.5), vec3(0.0, 1.0, 0.0), vec3(0.0, 0.0, 1.0), 0.5, vec3(1.0, 2.0, 3.0))";
            string vertex = "#version 330 core\n" + hooked + "out vec3 vLit;\nvoid main() { vLit = " + call + "; gl_Position = vec4(0.0, 0.0, 0.0, 1.0); }\n";
            string fragment = "#version 330 core\n" + hooked + "in vec3 vLit;\nout vec4 fragColour;\nvoid main() { fragColour = vec4(vLit + " + call
                + ", 1.0); }\n";
            new GlslProgramCompiler().Compile(vertex, fragment);
            new GlslProgramCompiler().Compile(PostProcessShaders.Vertex, fragment.Replace("in vec3 vLit;", "const vec3 vLit = vec3(0.0);"));
        }
        finally
        {
            LightingHooks.ForgetForTests();
        }
    }
}
