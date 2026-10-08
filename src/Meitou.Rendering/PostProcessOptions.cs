using System.Globalization;

namespace Meitou.Rendering;

/// <summary>Which post-processing the world view runs, and its parameters. Presets: <c>meitou</c> (default: the game's chain plus Meitou's switches), <c>kenshi</c> (the game's chain) and <c>off</c>; single effects go on top with their options and keys.</summary>
public sealed class PostOptions
{
    public string Preset = "meitou";
    /// <summary>Kenshi's FXAA on the final image (its <c>FXAA=1</c> setting; docs/formats/post-processing.md) when no temporal upscaler runs.</summary>
    public bool Fxaa = true;
    /// <summary>Kenshi's heat haze after FXAA (its <c>HeatHaze=1</c> setting, on when the key is absent; docs/formats/post-processing.md).</summary>
    public bool HeatHaze = true;
    /// <summary>Replaces the weather's <c>heat haze</c> field (the sun factor still applies), for testing in a weather without it; null: the weather's.</summary>
    public float? HeatHazeOverride;
    /// <summary>The game's <c>view distance</c> setting as the haze pass sees it: its depth scale is D = 10 × this (<see cref="Meitou.Data.World.HeatHaze.ViewDistanceSetting"/>).</summary>
    public float HeatHazeViewDistance = Meitou.Data.World.HeatHaze.ViewDistanceSetting;
    /// <summary>Meitou switches (Enhancements), on by default; the <c>kenshi</c> preset turns them off.</summary>
    public bool Ssao = true, Dither = true;
    /// <summary>Meitou switch (<c>particles</c>): the weather's alpha and additive particles with big sprites are drawn at half or a quarter of the render size and composited over the scene; Faithful draws every one at full size.</summary>
    public bool LowResParticles = true;
    /// <summary>0: each draw's size follows its sprites' mean screen size; 1, 2 or 4: every alpha and additive draw at that divisor (for measuring).</summary>
    public int ParticleDivisor;
    /// <summary>Linear scale of the scene before everything else. 1 keeps the shaders' brightness.</summary>
    public float Exposure = 1;
    /// <summary>World units: how far from a point occluders count.</summary>
    public float SsaoRadius = 12, SsaoStrength = 4;
    /// <summary>How much of the occlusion stays on the characters' own pixels (Meitou): 0 none, 1 the same as everywhere else. They still darken the ground round them.</summary>
    public float SsaoCharacterStrength = 0.25f;
    /// <summary>The upscaler (render scale, TAA / FSR / DLSS); not part of the presets.</summary>
    public readonly UpscaleOptions Upscale = new();
    /// <summary>0 none, 1 shows the occlusion.</summary>
    public int Debug;

    public static PostOptions Create(string preset)
    {
        var o = new PostOptions { Preset = preset };
        switch (preset)
        {
            case "meitou": break;
            case "off": o.Fxaa = o.HeatHaze = false; o.Ssao = o.Dither = o.LowResParticles = false; break;
            case "kenshi":
                o.Ssao = o.Dither = o.LowResParticles = false;
                // Kenshi's chain with the shipped settings: exposure only (no curve, bloom magnitude 0, SSAO commented
                // out), and FXAA then the heat haze on the final image.
                break;
            default: throw new ArgumentException($"unknown post preset '{preset}' (meitou, kenshi, off)");
        }
        return o;
    }

    public void CopyFrom(PostOptions other)
    {
        Preset = other.Preset; Fxaa = other.Fxaa; HeatHaze = other.HeatHaze; Debug = other.Debug; Ssao = other.Ssao;
        Dither = other.Dither; LowResParticles = other.LowResParticles; ParticleDivisor = other.ParticleDivisor; Exposure = other.Exposure; SsaoRadius = other.SsaoRadius;
        SsaoStrength = other.SsaoStrength; SsaoCharacterStrength = other.SsaoCharacterStrength;
    }

    public const string Usage = """
          --post <meitou|kenshi|off>   post-processing preset (default meitou; kenshi = the game's chain); give it before the options below
          --ssao / --no-ssao, --dither / --no-dither
          --particles-low / --no-particles-low   the weather's alpha and additive particles at a fraction of the render size (default on in Meitou; the `particles` switch)  --particle-divisor <0|1|2|4> (0, the default: by sprite size)
          --fxaa / --no-fxaa       the game's FXAA when no upscaler runs (default on)
          --heat-haze <x> / --no-heat-haze   the game's heat haze (default on; strength from the weather's `heat haze`); x replaces that field
          --heat-haze-view-distance <u>   the game's `view distance` setting for the haze's depth falloff (default 12000, the install's; amplitude is full from 1.67 x this units)
          --upscaler <off|taa|fsr|dlss>   temporal upscaling (off: full size with FXAA; FSR and DLSS need the vendor library, else TAA)
          --render-scale <0.25..1|native|quality|balanced|performance|ultra>   render size per axis with an upscaler (default 1)  --sharpness <0..1>
          --exposure <x>   --ssao-radius <units>   --ssao-strength <x>
          --ssao-character-strength <0..1>   occlusion kept on characters' own pixels (default 0.25; 1 = as the rest)
        """;

    /// <summary>Handles one command-line option; false when it is not a post-processing one.</summary>
    public bool TryParse(string a, Func<string> next)
    {
        float F() => float.Parse(next(), CultureInfo.InvariantCulture);
        switch (a)
        {
            case "--post": CopyFrom(Create(next())); return true;
            case "--ssao": Ssao = true; return true;
            case "--no-ssao": Ssao = false; return true;
            case "--dither": Dither = true; return true;
            case "--no-dither": Dither = false; return true;
            case "--particles-low": LowResParticles = true; return true;
            case "--no-particles-low": LowResParticles = false; return true;
            case "--particle-divisor": ParticleDivisor = Math.Clamp(int.Parse(next(), CultureInfo.InvariantCulture), 0, 4); return true;
            case "--fxaa": Fxaa = true; return true;
            case "--no-fxaa": Fxaa = false; return true;
            case "--heat-haze": HeatHaze = true; HeatHazeOverride = Math.Max(F(), 0); return true;
            case "--no-heat-haze": HeatHaze = false; return true;
            case "--heat-haze-view-distance": HeatHazeViewDistance = Math.Max(F(), 1); return true;
            case "--post-debug": Debug = next() switch { "ao" => 1, _ => 0 }; return true;
            case "--upscaler":
                Upscale.Explicit = true;
                Upscale.Kind = Enum.TryParse<UpscalerKind>(next(), true, out var k) ? k : throw new ArgumentException("--upscaler must be off, taa, fsr or dlss");
                if (Upscale.Kind != UpscalerKind.Off) Upscale.Preferred = Upscale.Kind;
                return true;
            case "--render-scale": Upscale.Scale = UpscaleOptions.ParseScale(next()); Upscale.Explicit = true; return true;
            case "--sharpness": Upscale.Sharpness = Math.Clamp(F(), 0, 1); return true;
            case "--exposure": Exposure = F(); return true;
            case "--ssao-radius": SsaoRadius = F(); return true;
            case "--ssao-strength": SsaoStrength = F(); return true;
            case "--ssao-character-strength": SsaoCharacterStrength = Math.Clamp(F(), 0f, 1f); return true;
            default: return false;
        }
    }

    public string Describe() =>
        $"{Preset}: fxaa {(Fxaa && !Upscale.Temporal ? "on" : "off")}, heat haze {(HeatHaze ? HeatHazeOverride is { } h ? $"x{h:0.##}" : "weather" : "off")}, ssao {(Ssao ? "on" : "off")}, exposure x{Exposure:0.##}, " +
        $"upscaler {Upscale.Describe()}";
}
