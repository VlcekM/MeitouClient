using System.Globalization;

namespace Meitou.Rendering;

/// <summary>Curve that maps the HDR scene colour to the display.</summary>
public enum ToneMapOperator
{
    /// <summary>Clamp at 1: what Kenshi does (its tone-map function is exposure only; docs/formats/post-processing.md).</summary>
    Clamp,
    /// <summary>Identity up to a knee, then an exponential shoulder towards 1 on the brightest channel: same look, no hard clip.</summary>
    Shoulder,
    /// <summary>Narkowicz's ACES fit: filmic, but darker mid-tones and more contrast than Kenshi.</summary>
    Aces,
}

/// <summary>Which post-processing the world view runs, and its parameters. Presets: <c>kenshi</c> (default, the game's chain) and <c>off</c>; single effects go on top with their options and keys.</summary>
public sealed class PostOptions
{
    public string Preset = "kenshi";
    /// <summary>Samples of the scene framebuffer (1, 2, 4 or 8).</summary>
    public int Msaa = 4;
    public bool Ssao, Bloom, Vignette, Grade, Dither;
    public ToneMapOperator ToneMap = ToneMapOperator.Clamp;
    /// <summary>Linear scale of the scene before everything else. 1 keeps the shaders' brightness.</summary>
    public float Exposure = 1;
    /// <summary>World units: how far from a point occluders count.</summary>
    public float SsaoRadius = 12, SsaoStrength = 4;
    public float BloomThreshold = 1, BloomIntensity = 0.3f;
    public float Saturation = 1.12f, Contrast = 1.06f, VignetteStrength = 0.3f;
    /// <summary>0 none, 1 shows the occlusion, 2 the bloom.</summary>
    public int Debug;

    public static PostOptions Create(string preset)
    {
        var o = new PostOptions { Preset = preset };
        switch (preset)
        {
            case "off": break;
            case "kenshi":
                // Kenshi's chain with the shipped settings: exposure only (no curve, bloom magnitude 0, SSAO commented
                // out). The game smooths edges with FXAA and no MSAA; the viewer uses 4x MSAA instead (the default).
                break;
            default: throw new ArgumentException($"unknown post preset '{preset}' (kenshi, off)");
        }
        return o;
    }

    public void CopyFrom(PostOptions other)
    {
        Preset = other.Preset; Msaa = other.Msaa; Debug = other.Debug; Ssao = other.Ssao; Bloom = other.Bloom; Vignette = other.Vignette;
        Grade = other.Grade; Dither = other.Dither; ToneMap = other.ToneMap; Exposure = other.Exposure; SsaoRadius = other.SsaoRadius;
        SsaoStrength = other.SsaoStrength; BloomThreshold = other.BloomThreshold; BloomIntensity = other.BloomIntensity;
        Saturation = other.Saturation; Contrast = other.Contrast; VignetteStrength = other.VignetteStrength;
    }

    public const string Usage = """
          --post <kenshi|off>   post-processing preset (default kenshi); give it before the options below
          --ssao / --no-ssao, --bloom / --no-bloom, --vignette / --no-vignette, --grade / --no-grade, --dither / --no-dither
          --msaa <1|2|4|8>         samples of the HDR scene framebuffer
          --tonemap <clamp|shoulder|aces>   --exposure <x>   --bloom-intensity <x>   --bloom-threshold <x>   --ssao-radius <units>   --ssao-strength <x>
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
            case "--bloom": Bloom = true; return true;
            case "--no-bloom": Bloom = false; return true;
            case "--vignette": Vignette = true; return true;
            case "--no-vignette": Vignette = false; return true;
            case "--grade": Grade = true; return true;
            case "--no-grade": Grade = false; return true;
            case "--dither": Dither = true; return true;
            case "--no-dither": Dither = false; return true;
            case "--msaa":
                Msaa = int.Parse(next(), CultureInfo.InvariantCulture);
                if (Msaa is not (1 or 2 or 4 or 8)) throw new ArgumentException("--msaa must be 1, 2, 4 or 8");
                return true;
            case "--tonemap":
                ToneMap = Enum.TryParse<ToneMapOperator>(next(), true, out var t) ? t : throw new ArgumentException("--tonemap must be clamp, shoulder or aces");
                return true;
            case "--post-debug": Debug = next() switch { "ao" => 1, "bloom" => 2, _ => 0 }; return true;
            case "--exposure": Exposure = F(); return true;
            case "--bloom-intensity": BloomIntensity = F(); return true;
            case "--bloom-threshold": BloomThreshold = F(); return true;
            case "--ssao-radius": SsaoRadius = F(); return true;
            case "--ssao-strength": SsaoStrength = F(); return true;
            default: return false;
        }
    }

    public string Describe() =>
        $"{Preset}: msaa {Msaa}x, ssao {(Ssao ? "on" : "off")}, bloom {(Bloom ? "on" : "off")}, tonemap {ToneMap.ToString().ToLowerInvariant()} x{Exposure:0.##}, " +
        $"grade {(Grade ? "on" : "off")}, vignette {(Vignette ? "on" : "off")}";
}
