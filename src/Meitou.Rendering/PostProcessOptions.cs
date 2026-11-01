using System.Globalization;

namespace Meitou.Rendering;

/// <summary>Which post-processing the world view runs, and its parameters. Presets: <c>meitou</c> (default: the game's chain plus Meitou's switches), <c>kenshi</c> (the game's chain) and <c>off</c>; single effects go on top with their options and keys.</summary>
public sealed class PostOptions
{
    public string Preset = "meitou";
    /// <summary>Kenshi's FXAA on the final image (its <c>FXAA=1</c> setting; docs/formats/post-processing.md) when no temporal upscaler runs.</summary>
    public bool Fxaa = true;
    /// <summary>Meitou's SMAA in place of FXAA when no temporal upscaler runs (docs/render-post.md "SMAA"); off by default.</summary>
    public bool Smaa;
    /// <summary>Kenshi's heat haze after FXAA (its <c>HeatHaze=1</c> setting, on when the key is absent; docs/formats/post-processing.md).</summary>
    public bool HeatHaze = true;
    /// <summary>The Meitou <c>heathaze</c> switch: the heat haze's amount (the weather's) scaled by <see cref="HeatHazeStrength"/>; Faithful keeps the game's (1).</summary>
    public bool MeitouHeatHaze = true;
    /// <summary>The Meitou heat haze's scale on the amount (0 hides it; default <see cref="Enhancements.MeitouHeatHazeStrength"/>), kept while the switch is Faithful.</summary>
    public float HeatHazeStrength = Enhancements.MeitouHeatHazeStrength;
    /// <summary>The scale the heat haze uses: <see cref="HeatHazeStrength"/> with the Meitou switch, else the game's 1.</summary>
    public float HeatHazeScale => MeitouHeatHaze ? HeatHazeStrength : 1;
    /// <summary>Meitou switches (Enhancements), on by default; the <c>kenshi</c> preset turns them off.</summary>
    public bool Ssao = true, Dither = true;
    /// <summary>Meitou switch (<c>particles</c>): the weather's alpha and additive particles with big sprites are drawn at half or a quarter of the render size and composited over the scene; Faithful draws every one at full size.</summary>
    public bool LowResParticles = true;
    /// <summary>Meitou switch (<c>shafts</c>; docs/render-shafts.md): the haze and the weather's fog darkened where the sun is shadowed along the view ray, so shadows
    /// cast light shafts through the air; Faithful leaves the haze lit everywhere, as the game does.</summary>
    public bool LightShafts = true;
    /// <summary>Meitou switch (<c>fog</c>; docs/render-post.md "Fog"): the particle sprites fade out where they meet a surface instead of cutting into it; Faithful
    /// draws them hard-edged, as the game's particle materials are but its mist and steam (which fade; not drawn so yet).</summary>
    public bool MeitouFog = true;
    /// <summary>How much of the haze in shadow goes, before the sun's share of the light (1: all of it; the sky's ambient light keeps the rest).</summary>
    public float ShaftStrength = 0.8f;
    /// <summary>How much the sky (which no haze covers) darkens behind shadowed air, relative to a surface at the grid's end.</summary>
    public const float ShaftSky = 0.5f;
    /// <summary>
    /// The light shafts' budget: 0 low, 1 medium, 2 high (the Tab panel's slider; high by default). It sets the froxel grid and shadow samples;
    /// the apply pass stays at the render size (docs/render-shafts.md, "Quality").
    /// </summary>
    public int ShaftQuality = 2;
    /// <summary>The light shafts' grid: cells across the screen (down follows the aspect), depth slices, shadow samples per cell.</summary>
    public int ShaftCells => ShaftQuality switch { 0 => 96, 1 => 128, _ => 160 };
    public int ShaftSlices => ShaftQuality switch { 0 => 32, 1 => 48, _ => 64 };
    public int ShaftSamples => ShaftQuality switch { 0 => 1, 1 => 2, _ => 2 };
    public const float ShaftNear = 50;
    /// <summary>The air layer (docs/render-shafts.md "Air layer"): its density as a multiple of <see cref="ShaftAirDensity"/> at the ground under the eye (0: none; the Tab slider),
    /// its scale height in world units and the Henyey-Greenstein anisotropy (0 even, towards 1 all towards the sun).</summary>
    public float ShaftAir = 1;
    public const float ShaftAirHeight = 800, ShaftAirPhase = 0.6f;
    public const float ShaftAirDensity = 1.5e-5f;
    /// <summary>The air layer by the time of day (morning mist, evening haze): <see cref="ShaftAirDawn"/> times the density at sunrise and at sunset (the Tab slider), easing down to
    /// <see cref="ShaftAirDay"/> times it over <see cref="ShaftAirRamp"/> hours after sunrise and before sunset (smoothstep).</summary>
    public float ShaftAirDawn = 1;
    public const float ShaftAirDay = 0, ShaftAirRamp = 5.5f;
    /// <summary>The shafts under clouds (docs/render-shafts.md "Cloud cover"): the darkening loses <see cref="ShaftCloudShade"/> of itself as the cloud density goes
    /// from <see cref="ShaftCloudFrom"/> to 1 (smoothstep).</summary>
    public const float ShaftCloudShade = 0.85f, ShaftCloudFrom = 0.3f;
    /// <summary>0: each draw's size follows its sprites' mean screen size; 1, 2 or 4: every alpha and additive draw at that divisor (for measuring).</summary>
    public int ParticleDivisor;
    /// <summary>The Meitou <c>tonemap</c> switch: <see cref="ToneMap"/> and <see cref="Grade"/> apply; Faithful is the game's clamp without grading.</summary>
    public bool ToneMapping = true;
    /// <summary>The Meitou curve after the exposure (the hybrid by default); <see cref="ToneMapOperator.Clamp"/> is the game's (it has none, values over 1 clip).</summary>
    public ToneMapOperator ToneMap = ToneMapOperator.Hybrid;
    /// <summary>The curve and grade used: the Meitou ones with the switch, else the game's clamp and none.</summary>
    public ToneMapOperator ToneMapUsed => ToneMapping ? ToneMap : ToneMapOperator.Clamp;
    public bool GradeUsed => ToneMapping && Grade;
    /// <summary>The hybrid tone map's share of ACES (0 the game's clamp, 1 ACES; default 0.75).</summary>
    public float ToneMix = 0.75f;
    /// <summary>Our saturation / contrast grade after the tone map, on in Meitou (the <c>tonemap</c> switch; the game has no grading); 1 / 1 change nothing.</summary>
    public bool Grade = true;
    public const float Saturation = 1.06f, Contrast = 1.05f;
    /// <summary>The Meitou <c>nightgrade</c> switch (docs/render-post.md "Night grading"); Faithful is the game's: no grading.</summary>
    public bool NightGrade = true;
    /// <summary>How far the Meitou night grading moves the picture towards the rods' blue-grey at night, 0 to 1, kept while the switch is Faithful;
    /// the adaptation (the auto exposure's mean luminance) decides how much of it applies, so by day it does nothing. Default <see cref="Meitou.Data.World.NightGrade.MeitouStrength"/>.</summary>
    public float NightGradeStrength = Meitou.Data.World.NightGrade.MeitouStrength;
    public float NightGradeAmount => NightGrade ? NightGradeStrength : 0;
    /// <summary>The Meitou bloom switch (docs/render-post.md "Bloom"), off by default even in the Meitou look; Faithful is the game's, whose bloom ships at magnitude 0.</summary>
    public bool Bloom;
    /// <summary>The bloom's share of every pixel's light scattered into its neighbourhood, 0 to 0.25, kept while the switch is off.</summary>
    public float BloomStrength = 0.13f;
    public float BloomAmount => Bloom ? BloomStrength : 0;
    /// <summary>The bloom's firefly ceiling in linear scene units (a gamma-encoded 2 is 4.6, a 4 is 21): texels under a quarter of it keep their light to 0.1%, specular spikes far above scatter as if they were it.</summary>
    public const float BloomFirefly = 64;
    /// <summary>Linear scale of the scene before everything else. 1 keeps the shaders' brightness (the - and = keys).</summary>
    public float Exposure = 1;
    /// <summary>Presentation-only white balance: the scene's linear colour is multiplied by it before the tone map (the Meitou title warms its light with it); 1, 1, 1 in play.</summary>
    public System.Numerics.Vector3 Balance = System.Numerics.Vector3.One;
    /// <summary>World units: how far from a point occluders count (at least 3 % of its distance).</summary>
    public const float SsaoRadius = 6, SsaoStrength = 4;
    /// <summary>World units: the eye distances over which the occlusion fades out (a detail effect; far geometry is hazy and has little depth precision).</summary>
    public const float SsaoFadeStart = 1600, SsaoFadeEnd = 6600;
    /// <summary>How much of the occlusion stays on the characters' own pixels (Meitou): 0 none, 1 the same as everywhere else. They still darken the ground round them.</summary>
    public const float SsaoCharacterStrength = 0.25f;
    /// <summary>How much a character counts as an occluder of the ground and things round it (Meitou): 1 as anything else. At 1 a character lying down left a dark halo as wide as itself (radius 6, strength 4) with a light seam where the half-size mask met it.</summary>
    public const float SsaoCharacterOccluder = 0.3f;
    /// <summary>The upscaler (render scale, TAA / FSR / DLSS); not part of the presets.</summary>
    public readonly UpscaleOptions Upscale = new();

    public static PostOptions Create(string preset)
    {
        var o = new PostOptions { Preset = preset };
        switch (preset)
        {
            case "meitou": break;
            case "off": o.Fxaa = o.HeatHaze = false; o.Ssao = o.Dither = o.LowResParticles = o.LightShafts = o.MeitouFog = false; o.MeitouHeatHaze = o.ToneMapping = o.NightGrade = o.Bloom = false; break;
            case "kenshi":
                o.Ssao = o.Dither = o.LowResParticles = o.LightShafts = o.MeitouFog = false; o.MeitouHeatHaze = o.ToneMapping = o.NightGrade = o.Bloom = false;
                // Kenshi's chain with the shipped settings: exposure only (no curve, bloom magnitude 0, SSAO commented
                // out), and FXAA then the heat haze on the final image.
                break;
            default: throw new ArgumentException($"unknown post preset '{preset}' (meitou, kenshi, off)");
        }
        return o;
    }

    public void CopyFrom(PostOptions other)
    {
        Preset = other.Preset; Fxaa = other.Fxaa; Smaa = other.Smaa; HeatHaze = other.HeatHaze; Ssao = other.Ssao;
        Dither = other.Dither; LowResParticles = other.LowResParticles; LightShafts = other.LightShafts; MeitouFog = other.MeitouFog; ShaftQuality = other.ShaftQuality;
        ShaftStrength = other.ShaftStrength; ShaftAir = other.ShaftAir; ShaftAirDawn = other.ShaftAirDawn;
        ParticleDivisor = other.ParticleDivisor; Exposure = other.Exposure; ToneMap = other.ToneMap; ToneMix = other.ToneMix; Grade = other.Grade;
        HeatHazeStrength = other.HeatHazeStrength; NightGradeStrength = other.NightGradeStrength; BloomStrength = other.BloomStrength;
        MeitouHeatHaze = other.MeitouHeatHaze; ToneMapping = other.ToneMapping; NightGrade = other.NightGrade; Bloom = other.Bloom;
    }

    /// <summary>Handles one command-line option; false when it is not a post-processing one.</summary>
    public bool TryParse(string a, Func<string> next)
    {
        switch (a)
        {
            case "--post": CopyFrom(Create(next())); return true;
            case "--particle-divisor": ParticleDivisor = Math.Clamp(int.Parse(next(), CultureInfo.InvariantCulture), 0, 4); return true;
            case "--no-fxaa": Fxaa = false; return true;
            case "--smaa": Smaa = true; return true;
            case "--no-heat-haze": HeatHaze = false; return true;
            case "--bloom": BloomStrength = Math.Clamp(float.Parse(next(), CultureInfo.InvariantCulture), 0, 0.25f); Bloom = BloomStrength > 0; return true;
            case "--upscaler":
                Upscale.Explicit = true;
                Upscale.Kind = Enum.TryParse<UpscalerKind>(next(), true, out var k) ? k : throw new ArgumentException("--upscaler must be off, taa, fsr, dlss or metalfx");
                if (Upscale.Kind != UpscalerKind.Off) Upscale.Preferred = Upscale.Kind;
                return true;
            case "--render-scale": Upscale.Scale = UpscaleOptions.ParseScale(next()); Upscale.Explicit = true; return true;
            default: return false;
        }
    }

    public string Describe() =>
        $"{Preset}: aa {(Upscale.Temporal ? "temporal" : Smaa ? "smaa" : Fxaa ? "fxaa" : "off")}, heat haze {(HeatHaze ? "weather" + (HeatHazeScale != 1 ? $" strength {HeatHazeScale:0.##}" : "") : "off")}, " +
        $"ssao {(Ssao ? "on" : "off")}, shafts {(LightShafts ? $"{ShaftStrength:0.##} ({ShaftCells}x{ShaftSlices}x{ShaftSamples})" : "off")}, exposure x{Exposure:0.##}, tonemap {ToneMapUsed.ToString().ToLowerInvariant()}{(ToneMapUsed == ToneMapOperator.Hybrid ? $" {ToneMix:0.##}" : "")}, " +
        $"grade {(GradeUsed ? $"saturation {Saturation:0.##} contrast {Contrast:0.##}" : "off")}, night grade {(NightGradeAmount > 0 ? NightGradeAmount.ToString("0.##", CultureInfo.InvariantCulture) : "off")}, " +
        $"bloom {(BloomAmount > 0 ? BloomAmount.ToString("0.###", CultureInfo.InvariantCulture) : "off")}, " +
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
