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
    /// <summary>Scales the heat haze's amount (the weather's, or <c>--heat-haze</c>'s): 1 is the game's (Faithful <c>heathaze</c>), 0 hides it; Meitou's default is
    /// <see cref="Enhancements.MeitouHeatHazeStrength"/>.</summary>
    public float HeatHazeStrength = Enhancements.MeitouHeatHazeStrength;
    /// <summary>The game's <c>view distance</c> setting as the haze pass sees it: its depth scale is D = 10 × this (<see cref="Meitou.Data.World.HeatHaze.ViewDistanceSetting"/>).</summary>
    public float HeatHazeViewDistance = Meitou.Data.World.HeatHaze.ViewDistanceSetting;
    /// <summary>Meitou switches (Enhancements), on by default; the <c>kenshi</c> preset turns them off.</summary>
    public bool Ssao = true, Dither = true;
    /// <summary>Meitou switch (<c>particles</c>): the weather's alpha and additive particles with big sprites are drawn at half or a quarter of the render size and composited over the scene; Faithful draws every one at full size.</summary>
    public bool LowResParticles = true;
    /// <summary>Meitou switch (<c>shafts</c>; docs/render-shafts.md): the haze and the weather's fog darkened where the sun is shadowed along the view ray, so shadows
    /// cast light shafts through the air; Faithful leaves the haze lit everywhere, as the game does.</summary>
    public bool LightShafts = true;
    /// <summary>How much of the haze in shadow goes, before the sun's share of the light (1: all of it; the sky's ambient light keeps the rest).</summary>
    public float ShaftStrength = 0.8f;
    /// <summary>How much the sky (which no haze covers) darkens behind shadowed air, relative to a surface at the grid's end.</summary>
    public float ShaftSky = 0.5f;
    /// <summary>The light shafts' grid: cells across the screen (down follows the aspect), depth slices, shadow samples per cell, where the first slice starts.</summary>
    public int ShaftCells = 160, ShaftSlices = 64, ShaftSamples = 2;
    public float ShaftNear = 50;
    /// <summary>The air layer (docs/render-shafts.md "Air layer"): its density as a multiple of <see cref="ShaftAirDensity"/> at the ground under the eye (0: none), its scale
    /// height in world units and the Henyey-Greenstein anisotropy (0 even, towards 1 all towards the sun).</summary>
    public float ShaftAir = 1, ShaftAirHeight = 800, ShaftAirPhase = 0.6f;
    public const float ShaftAirDensity = 1.5e-5f;
    /// <summary>The air layer by the time of day (morning mist, evening haze): <see cref="ShaftAirDawn"/> times the density at sunrise and at sunset, easing down to
    /// <see cref="ShaftAirDay"/> times it over <see cref="ShaftAirRamp"/> hours after sunrise and before sunset (smoothstep).</summary>
    public float ShaftAirDawn = 1, ShaftAirDay = 0, ShaftAirRamp = 5.5f;
    /// <summary>The shafts under clouds (docs/render-shafts.md "Cloud cover"): the darkening loses <see cref="ShaftCloudShade"/> of itself as the cloud density goes
    /// from <see cref="ShaftCloudFrom"/> to 1 (smoothstep).</summary>
    public float ShaftCloudShade = 0.85f, ShaftCloudFrom = 0.3f;
    /// <summary><c>--shafts-debug</c>: the picture is the share of the haze the sun reaches (white lit, black shadowed).</summary>
    public bool ShaftDebug;
    /// <summary>0: each draw's size follows its sprites' mean screen size; 1, 2 or 4: every alpha and additive draw at that divisor (for measuring).</summary>
    public int ParticleDivisor;
    /// <summary>The curve after the exposure: <see cref="ToneMapOperator.Clamp"/> is the game's (it has none, values over 1 clip); Meitou's default is the hybrid (the <c>tonemap</c> switch).</summary>
    public ToneMapOperator ToneMap = ToneMapOperator.Hybrid;
    /// <summary>The hybrid tone map's share of ACES (0 the game's clamp, 1 ACES; default 0.75).</summary>
    public float ToneMix = 0.75f;
    /// <summary>Our saturation / contrast grade after the tone map, on in Meitou (the <c>tonemap</c> switch; the game has no grading); 1 / 1 change nothing.</summary>
    public bool Grade = true;
    public float Saturation = 1.06f, Contrast = 1.05f;
    /// <summary>Meitou night grading (the <c>nightgrade</c> switch; docs/render-post.md "Night grading"): how far the picture moves towards the rods' blue-grey at night, 0 (Faithful, the game's: no grading) to 1;
    /// the adaptation (the auto exposure's mean luminance) decides how much of it applies, so by day it does nothing. Default <see cref="Meitou.Data.World.NightGrade.MeitouStrength"/>.</summary>
    public float NightGradeStrength = Meitou.Data.World.NightGrade.MeitouStrength;
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
            case "off": o.Fxaa = o.HeatHaze = false; o.Ssao = o.Dither = o.LowResParticles = o.LightShafts = false; o.HeatHazeStrength = 1; o.ToneMap = ToneMapOperator.Clamp; o.Grade = false; o.NightGradeStrength = 0; break;
            case "kenshi":
                o.Ssao = o.Dither = o.LowResParticles = o.LightShafts = false; o.HeatHazeStrength = 1; o.ToneMap = ToneMapOperator.Clamp; o.Grade = false; o.NightGradeStrength = 0;
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
        Dither = other.Dither; LowResParticles = other.LowResParticles; LightShafts = other.LightShafts; ShaftStrength = other.ShaftStrength; ShaftSky = other.ShaftSky; ShaftAir = other.ShaftAir; ShaftAirDawn = other.ShaftAirDawn; ShaftAirDay = other.ShaftAirDay; ShaftAirRamp = other.ShaftAirRamp; ShaftCloudShade = other.ShaftCloudShade; ShaftCloudFrom = other.ShaftCloudFrom; ParticleDivisor = other.ParticleDivisor; Exposure = other.Exposure; SsaoRadius = other.SsaoRadius;
        SsaoStrength = other.SsaoStrength; SsaoCharacterStrength = other.SsaoCharacterStrength; ToneMap = other.ToneMap; ToneMix = other.ToneMix; Grade = other.Grade;
        Saturation = other.Saturation; Contrast = other.Contrast; HeatHazeStrength = other.HeatHazeStrength; NightGradeStrength = other.NightGradeStrength;
    }

    public const string Usage = """
          --post <meitou|kenshi|off>   post-processing preset (default meitou; kenshi = the game's chain); give it before the options below
          --ssao / --no-ssao, --dither / --no-dither
          --particles-low / --no-particles-low   the weather's alpha and additive particles at a fraction of the render size (default on in Meitou; the `particles` switch)  --particle-divisor <0|1|2|4> (0, the default: by sprite size)
          --fxaa / --no-fxaa       the game's FXAA when no upscaler runs (default on)
          --shafts / --no-shafts   light shafts: the haze darkened where the sun is shadowed along the view (default on in Meitou; the `shafts` switch)
          --shafts-strength <0..1> (0.8)  --shafts-sky <0..1> (0.5)  --shafts-grid <cells across>,<slices>[,<samples>] (160,64,2)  --shafts-near <u> (50)  --shafts-debug (the sun's share of the haze as the picture)
          --shafts-air <x>   the lit air layer near the ground, x its density (default 1; 0 none)  --shafts-air-height <u> (800)  --shafts-air-phase <0..0.95> (0.6, how much it glows towards the sun)
          --shafts-clouds <0..1> (0.85)  --shafts-clouds-from <c> (0.3)   how much of the shafts a full cloud cover takes away, fading in from cloud density c
          --shafts-air-dawn <x> (1)  --shafts-air-day <x> (0)  --shafts-air-ramp <h> (5.5)   the air by the time of day: x dawn at sunrise and sunset, easing to x day over h hours after sunrise and before sunset
          --heat-haze <x> / --no-heat-haze   the game's heat haze (default on; strength from the weather's `heat haze`); x replaces that field
          --heat-haze-strength <x>   scales the heat haze (default 0.5, the Meitou `heathaze` switch; 1 is the game's, 0 hides it)
          --heat-haze-view-distance <u>   the game's `view distance` setting for the haze's depth falloff (default 12000, the install's; amplitude is full from 1.67 x this units)
          --upscaler <off|taa|fsr|dlss>   temporal upscaling (off: full size with FXAA; FSR and DLSS need the vendor library, else TAA)
          --render-scale <0.25..1|native|ultra-quality|quality|balanced|performance|ultra>   render size per axis (Meitou default 0.83, off default 1)  --sharpness <0..1>
          --tonemap <clamp|shoulder|aces|hybrid>   (default hybrid in Meitou, the `tonemap` switch) clamp: the game's (no curve); shoulder: identity to 0.8, then rolls off to 1; aces: Narkowicz's ACES fit; hybrid: clamp and ACES mixed by --tonemap-mix <0..1> (0.75)
          --grade / --no-grade   saturation / contrast grade (default on in Meitou, the `tonemap` switch; the game has none)  --saturation <x> (1.06)  --contrast <x> (1.05)
          --night-grade <0..1> / --no-night-grade   scotopic night grading: at night the picture shifts to a blue-grey, lamps and the moon keep their colour (default 0.7 in Meitou, the `nightgrade` switch; 0 is the game's, no grading)
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
            case "--shafts": LightShafts = true; return true;
            case "--no-shafts": LightShafts = false; return true;
            case "--shafts-strength": ShaftStrength = Math.Clamp(F(), 0, 1); return true;
            case "--shafts-sky": ShaftSky = Math.Clamp(F(), 0, 1); return true;
            case "--shafts-near": ShaftNear = Math.Max(F(), 1); return true;
            case "--shafts-debug": ShaftDebug = true; return true;
            case "--shafts-air": ShaftAir = Math.Max(F(), 0); return true;
            case "--shafts-air-height": ShaftAirHeight = Math.Max(F(), 1); return true;
            case "--shafts-air-phase": ShaftAirPhase = Math.Clamp(F(), 0, 0.95f); return true;
            case "--shafts-air-dawn": ShaftAirDawn = Math.Max(F(), 0); return true;
            case "--shafts-air-day": ShaftAirDay = Math.Max(F(), 0); return true;
            case "--shafts-clouds": ShaftCloudShade = Math.Clamp(F(), 0, 1); return true;
            case "--shafts-clouds-from": ShaftCloudFrom = Math.Clamp(F(), 0, 0.99f); return true;
            case "--shafts-air-ramp": ShaftAirRamp = Math.Max(F(), 0.1f); return true;
            case "--shafts-grid":
                {
                    var parts = next().Split(',');
                    ShaftCells = int.Parse(parts[0], CultureInfo.InvariantCulture);
                    if (parts.Length > 1) ShaftSlices = int.Parse(parts[1], CultureInfo.InvariantCulture);
                    if (parts.Length > 2) ShaftSamples = int.Parse(parts[2], CultureInfo.InvariantCulture);
                    return true;
                }
            case "--fxaa": Fxaa = true; return true;
            case "--no-fxaa": Fxaa = false; return true;
            case "--heat-haze": HeatHaze = true; HeatHazeOverride = Math.Max(F(), 0); return true;
            case "--no-heat-haze": HeatHaze = false; return true;
            case "--heat-haze-strength": HeatHazeStrength = Math.Max(F(), 0); return true;
            case "--tonemap":
                ToneMap = Enum.TryParse<ToneMapOperator>(next(), true, out var t) && Enum.IsDefined(t) ? t : throw new ArgumentException("--tonemap must be clamp, shoulder, aces or hybrid");
                return true;
            case "--tonemap-mix": ToneMix = Math.Clamp(F(), 0, 1); return true;
            case "--grade": Grade = true; return true;
            case "--no-grade": Grade = false; return true;
            case "--saturation": Saturation = Math.Max(F(), 0); return true;
            case "--contrast": Contrast = Math.Max(F(), 0); return true;
            case "--night-grade": NightGradeStrength = Math.Clamp(F(), 0, 1); return true;
            case "--no-night-grade": NightGradeStrength = 0; return true;
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
        $"{Preset}: fxaa {(Fxaa && !Upscale.Temporal ? "on" : "off")}, heat haze {(HeatHaze ? (HeatHazeOverride is { } h ? $"x{h:0.##}" : "weather") + (HeatHazeStrength != 1 ? $" strength {HeatHazeStrength:0.##}" : "") : "off")}, " +
        $"ssao {(Ssao ? "on" : "off")}, shafts {(LightShafts ? $"{ShaftStrength:0.##} ({ShaftCells}x{ShaftSlices}x{ShaftSamples})" : "off")}, exposure x{Exposure:0.##}, tonemap {ToneMap.ToString().ToLowerInvariant()}{(ToneMap == ToneMapOperator.Hybrid ? $" {ToneMix:0.##}" : "")}, " +
        $"grade {(Grade ? $"saturation {Saturation:0.##} contrast {Contrast:0.##}" : "off")}, night grade {(NightGradeStrength > 0 ? NightGradeStrength.ToString("0.##", CultureInfo.InvariantCulture) : "off")}, " +
        $"upscaler {Upscale.Describe()}";
}

/// <summary>The tone curve of the composite (docs/render-post.md "Tone map and grading").</summary>
public enum ToneMapOperator
{
    /// <summary>Clamp at 1: what Kenshi does (its tone-map function is exposure only; docs/formats/post-processing.md).</summary>
    Clamp,
    /// <summary>Identity up to 0.8 on the brightest channel, then an exponential shoulder towards 1: the same look without the hard clip.</summary>
    Shoulder,
    /// <summary>Narkowicz's ACES fit: filmic, but darker mid-tones and more contrast than Kenshi.</summary>
    Aces,
    /// <summary>Between the two: the clamp and ACES mixed by <see cref="PostOptions.ToneMix"/> (0 the clamp, 1 ACES): highlights roll off instead of clipping, with half the shift in contrast and saturation at 0.5.</summary>
    Hybrid,
}
