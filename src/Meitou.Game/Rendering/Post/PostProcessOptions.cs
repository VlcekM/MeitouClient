using System.Globalization;
using Meitou.Game.Rendering.Frame;

namespace Meitou.Game.Rendering.Post;

/// <summary>
/// Which post-processing the world view runs, and its parameters. Presets: <c>meitou</c> (default: the game's chain plus Meitou's switches),
/// <c>kenshi</c> (the game's chain) and <c>off</c>; single effects go on top with their options and keys.
/// </summary>
public sealed class PostOptions
{
    #region Settings

    /// <summary>
    /// The preset the settings came from; <c>meitou</c> by default.
    /// </summary>
    public string Preset = "meitou";

    /// <summary>
    /// Kenshi's FXAA on the final image (its <c>FXAA=1</c> setting; docs/formats/post-processing.md) when no temporal upscaler runs.
    /// </summary>
    public bool Fxaa = true;

    /// <summary>
    /// Meitou's SMAA in place of FXAA when no temporal upscaler runs (docs/render-post.md "SMAA"); off by default.
    /// </summary>
    public bool Smaa;

    /// <summary>
    /// Kenshi's heat haze after FXAA (its <c>HeatHaze=1</c> setting, on when the key is absent; docs/formats/post-processing.md).
    /// </summary>
    public bool HeatHaze = true;

    /// <summary>
    /// The Meitou <c>heathaze</c> switch: the heat haze's amount (the weather's) scaled by <see cref="HeatHazeStrength"/>; Faithful keeps the game's
    /// (1).
    /// </summary>
    public bool MeitouHeatHaze = true;

    /// <summary>
    /// The Meitou heat haze's scale on the amount (0 hides it; default <see cref="Enhancements.MeitouHeatHazeStrength"/>), kept while the switch is
    /// Faithful.
    /// </summary>
    public float HeatHazeStrength = Enhancements.MeitouHeatHazeStrength;

    /// <summary>
    /// The scale the heat haze uses: <see cref="HeatHazeStrength"/> with the Meitou switch, else the game's 1.
    /// </summary>
    public float HeatHazeScale => MeitouHeatHaze ? HeatHazeStrength : 1;

    /// <summary>
    /// Meitou switches (Enhancements), on by default; the <c>kenshi</c> preset turns them off.
    /// </summary>
    public bool Ssao = true;

    /// <summary>
    /// Meitou switches (Enhancements), on by default; the <c>kenshi</c> preset turns them off.
    /// </summary>
    public bool Dither = true;

    /// <summary>
    /// Meitou switch (<c>particles</c>): the weather's alpha and additive particles with big sprites are drawn at half or a quarter of the render
    /// size and composited over the scene; Faithful draws every one at full size.
    /// </summary>
    public bool LowResParticles = true;

    /// <summary>
    /// Meitou switch (<c>shafts</c>; docs/render-shafts.md): the haze and the weather's fog darkened where the sun is shadowed along the view ray, so
    /// shadows cast light shafts through the air; Faithful leaves the haze lit everywhere, as the game does.
    /// </summary>
    public bool LightShafts = true;

    /// <summary>
    /// Meitou switch (<c>traced</c>, off by default; docs/render-shadows.md "Traced sun"): near the eye the sun's shadow is traced, grass included,
    /// where the device has ray queries and the GI resolve runs; everywhere else, and off, the cascades.
    /// </summary>
    public bool TracedSun;

    /// <summary>
    /// Meitou switch (<c>fog</c>; docs/render-post.md "Fog"): the particle sprites fade out where they meet a surface instead of cutting into it;
    /// Faithful draws them hard-edged, as the game's particle materials are but its mist and steam (which fade; not drawn so yet).
    /// </summary>
    public bool MeitouFog = true;

    /// <summary>
    /// Meitou switch (<c>gihybrid</c>; docs/render-gi.md "Hybrid GI"), off by default: nothing it holds has been seen on a GPU.
    /// <c>--gi-hybrid</c> picks among its parts.
    /// </summary>
    public bool GiHybrid;

    /// <summary>
    /// A part of <see cref="GiHybrid"/> (<c>relocate</c>): probes moved out of geometry.
    /// </summary>
    public bool GiRelocate = true;

    /// <summary>
    /// A part of <see cref="GiHybrid"/> (<c>clouds</c>): the cloud shadows at the probes' hits and in the light shafts.
    /// </summary>
    public bool GiCloudShadows = true;

    /// <summary>
    /// A part of <see cref="GiHybrid"/> (<c>cache</c>): the traced world-space radiance cache (<c>Gi.GiCache</c>).
    /// </summary>
    public bool GiCache = true;

    /// <summary>
    /// Whether the probes are relocated this frame: <see cref="GiHybrid"/> with <see cref="GiRelocate"/>.
    /// </summary>
    public bool GiRelocating => GiHybrid && GiRelocate;

    /// <summary>
    /// Whether the cloud shadows reach the probes and the shafts: <see cref="GiHybrid"/> with <see cref="GiCloudShadows"/>.
    /// </summary>
    public bool GiCloudShadowing => GiHybrid && GiCloudShadows;

    /// <summary>
    /// Whether the radiance cache runs: <see cref="GiHybrid"/> with <see cref="GiCache"/>.
    /// </summary>
    public bool GiCaching => GiHybrid && GiCache;

    /// <summary>
    /// Meitou switch (<c>ssr</c>; docs/render-gi.md "Reflections"): the sky's reflection on glossy surfaces replaced by what they mirror, and
    /// occluded on all that take the GI resolve; off by default, also in the Meitou preset.
    /// </summary>
    public bool Ssr;

    /// <summary>
    /// How much of the haze in shadow goes, before the sun's share of the light (1: all of it; the sky's ambient light keeps the rest).
    /// </summary>
    public float ShaftStrength = 0.8f;

    /// <summary>
    /// How much the sky (which no haze covers) darkens behind shadowed air, relative to a surface at the grid's end.
    /// </summary>
    public const float ShaftSky = 0.5f;

    /// <summary>
    /// The light shafts' budget: 0 low, 1 medium, 2 high (the Tab panel's slider; high by default). It sets the froxel grid and shadow samples; the
    /// apply pass stays at the render size (docs/render-shafts.md, "Quality").
    /// </summary>
    public int ShaftQuality = 2;

    /// <summary>
    /// The light shafts' grid: cells across the screen (down follows the aspect), depth slices, shadow samples per cell. Each follows
    /// <see cref="ShaftQuality"/>.
    /// </summary>
    public int ShaftCells => ShaftQuality switch
    {
        0 => 96,
        1 => 128,
        _ => 160,
    };

    /// <summary>
    /// The depth slices of the light shafts' grid (see <see cref="ShaftCells"/>).
    /// </summary>
    public int ShaftSlices => ShaftQuality switch
    {
        0 => 32,
        1 => 48,
        _ => 64,
    };

    /// <summary>
    /// The shadow samples per cell of the light shafts' grid (see <see cref="ShaftCells"/>).
    /// </summary>
    public int ShaftSamples => ShaftQuality switch
    {
        0 => 1,
        1 => 2,
        _ => 2,
    };

    /// <summary>
    /// The light shafts' near plane in world units.
    /// </summary>
    public const float ShaftNear = 50;

    /// <summary>
    /// The air layer (docs/render-shafts.md "Air layer"): its density as a multiple of <see cref="ShaftAirDensity"/> at the ground under the eye (0:
    /// none; the Tab slider), its scale height in world units and the Henyey-Greenstein anisotropy (0 even, towards 1 all towards the sun).
    /// </summary>
    public float ShaftAir = 1;

    /// <summary>
    /// The air layer's scale height in world units (see <see cref="ShaftAir"/>).
    /// </summary>
    public const float ShaftAirHeight = 800;

    /// <summary>
    /// The air layer's Henyey-Greenstein anisotropy (see <see cref="ShaftAir"/>): 0 even, towards 1 all towards the sun.
    /// </summary>
    public const float ShaftAirPhase = 0.6f;

    /// <summary>
    /// The air layer's base density, which <see cref="ShaftAir"/> and the time of day scale.
    /// </summary>
    public const float ShaftAirDensity = 1.5e-5f;

    /// <summary>
    /// The air layer by the time of day (morning mist, evening haze): <see cref="ShaftAirDawn"/> times the density at sunrise and at sunset
    /// (the Tab slider), easing down to
    /// <see cref="ShaftAirDay"/> times it over <see cref="ShaftAirRamp"/> hours after sunrise and before sunset (smoothstep).
    /// </summary>
    public float ShaftAirDawn = 1;

    /// <summary>
    /// The air layer's share of the density by day (see <see cref="ShaftAirDawn"/>).
    /// </summary>
    public const float ShaftAirDay = 0;

    /// <summary>
    /// The hours over which the air layer eases from dawn to day (see <see cref="ShaftAirDawn"/>).
    /// </summary>
    public const float ShaftAirRamp = 5.5f;

    /// <summary>
    /// The shafts under clouds (docs/render-shafts.md "Cloud cover"): the darkening loses <see cref="ShaftCloudShade"/> of itself as the cloud
    /// density goes from <see cref="ShaftCloudFrom"/> to 1 (smoothstep).
    /// </summary>
    public const float ShaftCloudShade = 0.85f;

    /// <summary>
    /// The cloud density where the shafts' darkening starts to lose its share under clouds (see <see cref="ShaftCloudShade"/>).
    /// </summary>
    public const float ShaftCloudFrom = 0.3f;

    /// <summary>
    /// 0: each draw's size follows its sprites' mean screen size; 1, 2 or 4: every alpha and additive draw at that divisor (for measuring).
    /// </summary>
    public int ParticleDivisor;

    /// <summary>
    /// The Meitou <c>tonemap</c> switch: <see cref="ToneMap"/> and <see cref="Grade"/> apply; Faithful is the game's clamp without grading.
    /// </summary>
    public bool ToneMapping = true;

    /// <summary>
    /// The Meitou curve after the exposure (the hybrid by default); <see cref="ToneMapOperator.Clamp"/> is the game's (it has none, values over 1
    /// clip).
    /// </summary>
    public ToneMapOperator ToneMap = ToneMapOperator.Hybrid;

    /// <summary>
    /// The curve and grade used: the Meitou ones with the switch, else the game's clamp and none.
    /// </summary>
    public ToneMapOperator ToneMapUsed => ToneMapping ? ToneMap : ToneMapOperator.Clamp;

    /// <summary>
    /// Whether the grade applies: the <c>tonemap</c> switch and <see cref="Grade"/>.
    /// </summary>
    public bool GradeUsed => ToneMapping && Grade;

    /// <summary>
    /// The hybrid tone map's share of ACES (0 the game's clamp, 1 ACES; default 0.75).
    /// </summary>
    public float ToneMix = 0.75f;

    /// <summary>
    /// Our saturation / contrast grade after the tone map, on in Meitou (the <c>tonemap</c> switch; the game has no grading); 1 / 1 change nothing.
    /// </summary>
    public bool Grade = true;

    /// <summary>
    /// The grade's saturation (see <see cref="Grade"/>).
    /// </summary>
    public const float Saturation = 1.06f;

    /// <summary>
    /// The grade's contrast (see <see cref="Grade"/>).
    /// </summary>
    public const float Contrast = 1.05f;

    /// <summary>
    /// The Meitou <c>nightgrade</c> switch (docs/render-post.md "Night grading"); Faithful is the game's: no grading.
    /// </summary>
    public bool NightGrade = true;

    /// <summary>
    /// How far the Meitou night grading moves the picture towards the rods' blue-grey at night, 0 to 1, kept while the switch is Faithful; the
    /// adaptation (the auto exposure's mean luminance) decides how much of it applies, so by day it does nothing. Default
    /// <see cref="Meitou.Data.World.NightGrade.MeitouStrength"/>.
    /// </summary>
    public float NightGradeStrength = Data.World.NightGrade.MeitouStrength;

    /// <summary>
    /// The night grading's strength in use: <see cref="NightGradeStrength"/> with the switch, else 0.
    /// </summary>
    public float NightGradeAmount => NightGrade ? NightGradeStrength : 0;

    /// <summary>
    /// The Meitou bloom switch (docs/render-post.md "Bloom"), off by default even in the Meitou look; Faithful is the game's, whose bloom ships at
    /// magnitude 0.
    /// </summary>
    public bool Bloom;

    /// <summary>
    /// The bloom's share of every pixel's light scattered into its neighbourhood, 0 to 0.25, kept while the switch is off.
    /// </summary>
    public float BloomStrength = 0.13f;

    /// <summary>
    /// The bloom's strength in use: <see cref="BloomStrength"/> with the switch, else 0.
    /// </summary>
    public float BloomAmount => Bloom ? BloomStrength : 0;

    /// <summary>
    /// The bloom's firefly ceiling in linear scene units (a gamma-encoded 2 is 4.6, a 4 is 21): texels under a quarter of it keep their light to
    /// 0.1%, specular spikes far above scatter as if they were it.
    /// </summary>
    public const float BloomFirefly = 64;

    /// <summary>
    /// HDR output's highlight ceiling in the composite's gamma-encoded units (docs/render-post.md "HDR output"); 0 is SDR. Set each frame by the
    /// host, not a saved setting.
    /// </summary>
    public float HdrPeak;

    /// <summary>
    /// The <see cref="HdrPeak"/> for a display's peak and the picture's paper white, both in nits.
    /// </summary>
    public static float HdrPeakFor(float peakNits, float paperNits) => MathF.Pow(MathF.Max(peakNits / paperNits, 1), 1 / 2.2f);

    /// <summary>
    /// The Meitou <c>dof</c> switch (docs/render-post.md "Depth of field"), off by default even in the Meitou look.
    /// </summary>
    public bool DepthOfField;

    /// <summary>
    /// The lens's f-number where the shot does not set one (the Tab slider; <see cref="DefaultAperture"/> by default).
    /// </summary>
    public float DofAperture = DefaultAperture;

    /// <summary>
    /// The default f-number of <see cref="DofAperture"/> and <see cref="LensAperture"/>.
    /// </summary>
    public const float DefaultAperture = 1;

    /// <summary>
    /// The largest CoC as a share of the picture's height in half-size pixels (the gathers' four rings thin out past it).
    /// </summary>
    public const float DofMaxCocShare = 0.015f;

    /// <summary>
    /// The largest CoC in half-size pixels, whatever <see cref="DofMaxCocShare"/> gives.
    /// </summary>
    public const float DofMaxCocPixels = 24;

    /// <summary>
    /// The focus distance (world units) of the frame's camera, set by the world frame.
    /// </summary>
    public float LensFocus = 1000;

    /// <summary>
    /// The f-number of the frame's camera, set by the world frame.
    /// </summary>
    public float LensAperture = DefaultAperture;

    /// <summary>
    /// The lens focuses by itself on what the picture's centre shows (docs/render-post.md "Depth of field"): set per frame, while no cinema key
    /// names a focus. <see cref="LensFocus"/> is then what it falls back to on the sky.
    /// </summary>
    public bool LensAutoFocus;

    /// <summary>
    /// Half the depth of a zone round the focus that is wholly sharp, world units (photo mode's Sharp zone; 0: a thin lens, sharp at the focus
    /// alone).
    /// </summary>
    public float LensSharpZone;

    /// <summary>
    /// Where in the picture the autofocus looks (0..1 from its top left): the centre, or the point photo mode's click chose.
    /// </summary>
    public System.Numerics.Vector2 LensFocusSpot = new(0.5f);

    /// <summary>
    /// The camera effects in the strategy camera as well (the player's choice, off by default): otherwise only with <see cref="CinematicView"/>.
    /// </summary>
    public bool CameraEffectsEverywhere;

    /// <summary>
    /// Set by the game each frame: the free camera or the cinematic one holds the view. A host without a strategy camera leaves it on.
    /// </summary>
    public bool CinematicView = true;

    /// <summary>
    /// Set for a frame that is no world (the loading sky): the camera effects leave it alone.
    /// </summary>
    public bool CameraEffectsHeld;

    /// <summary>
    /// Meitou switch (<c>lampshadows</c>; docs/render-lights.md "Traced lamp shadows"), off by default even in the Meitou look; Faithful is the
    /// game's: lamps cast no shadows.
    /// </summary>
    public bool LampShadows;

    /// <summary>
    /// Linear scale of the scene before everything else. 1 keeps the shaders' brightness (the - and = keys).
    /// </summary>
    public float Exposure = 1;

    /// <summary>
    /// Photo mode's exposure compensation, a linear factor on top of <see cref="Exposure"/> (the player's saved brightness is left alone; 1 outside
    /// it).
    /// </summary>
    public float PhotoExposure = 1;

    /// <summary>
    /// Photo mode's depth of field: on or off whatever <see cref="DepthOfField"/> says; null outside it.
    /// </summary>
    public bool? PhotoDepthOfField;

    /// <summary>
    /// The exposure the passes apply: <see cref="Exposure"/> with photo mode's compensation.
    /// </summary>
    public float ExposureApplied => Exposure * PhotoExposure;

    /// <summary>
    /// Presentation-only white balance: the scene's linear colour is multiplied by it before the tone map (the Meitou title warms its light with it);
    /// 1, 1, 1 in play.
    /// </summary>
    public System.Numerics.Vector3 Balance = System.Numerics.Vector3.One;

    /// <summary>
    /// World units: how far from a point occluders count (at least 3 % of its distance).
    /// </summary>
    public const float SsaoRadius = 6;

    /// <summary>
    /// The strength the SSAO pass is set with.
    /// </summary>
    public const float SsaoStrength = 4;

    /// <summary>
    /// World units: the eye distances over which the occlusion fades out (a detail effect; far geometry is hazy and has little depth precision).
    /// </summary>
    public const float SsaoFadeStart = 1600;

    /// <summary>
    /// The eye distance where the occlusion has faded out (see <see cref="SsaoFadeStart"/>).
    /// </summary>
    public const float SsaoFadeEnd = 6600;

    /// <summary>
    /// How much of the occlusion stays on the characters' own pixels (Meitou): 0 none, 1 the same as everywhere else. They still darken the ground
    /// round them.
    /// </summary>
    public const float SsaoCharacterStrength = 0.25f;

    /// <summary>
    /// How much a character counts as an occluder of the ground and things round it (Meitou): 1 as anything else. At 1 a character lying down left a
    /// dark halo as wide as itself (radius 6, strength 4) with a light seam where the half-size mask met it.
    /// </summary>
    public const float SsaoCharacterOccluder = 0.3f;

    /// <summary>
    /// Meitou switch (<c>contact</c>; docs/render-post.md "Contact shadows"): short sun shadows marched in the scene depth, taken from the sun's
    /// light alone where the GI resolve runs. Off by default (what the screen does not show casts nothing); the traced sun does the same exactly
    /// where the device has ray queries.
    /// </summary>
    public bool ContactShadows;

    /// <summary>
    /// How much of a surface's sun a contact shadow takes.
    /// </summary>
    public const float ContactStrength = 1;

    /// <summary>
    /// A contact shadow's reach in world units.
    /// </summary>
    public const float ContactReach = 8;

    /// <summary>
    /// A contact shadow's thickness in world units.
    /// </summary>
    public const float ContactThickness = 0.6f;

    /// <summary>
    /// The eye distance where the contact shadows have faded out.
    /// </summary>
    public const float ContactEnd = 2500;

    /// <summary>
    /// The contact shadows' parameters for their pass; a strength of 0 turns them off.
    /// </summary>
    public System.Numerics.Vector4 ContactParams =>
        new(ContactShadows ? ContactStrength : 0, ContactReach, ContactThickness, ContactEnd);

    /// <summary>
    /// The traced sun's grass shadows (docs/render-shadows.md "Traced sun shadows"): off, its rays see no grass, the larger part of its cost in
    /// grassland.
    /// </summary>
    public bool TracedGrass = true;

    /// <summary>
    /// The <c>gtao</c> switch (docs/render-post.md "GTAO"): with <see cref="Ssao"/> on, the occlusion is the horizon search instead; off by default
    /// in every preset.
    /// </summary>
    public bool Gtao;

    /// <summary>
    /// GTAO's budget: 0 low, 1 medium, 2 high (the Tab panel's slider). It sets the slices, the steps and whether the gather runs at the render size.
    /// </summary>
    public int GtaoQuality = 1;

    /// <summary>
    /// GTAO's slices for <see cref="GtaoQuality"/>.
    /// </summary>
    public int GtaoSlices => GtaoQuality switch { 0 => 2, _ => 3 };

    /// <summary>
    /// GTAO's steps per slice for <see cref="GtaoQuality"/>.
    /// </summary>
    public int GtaoSteps => GtaoQuality switch { 0 => 4, _ => 6 };

    /// <summary>
    /// Whether GTAO's gather runs at the render size (high quality).
    /// </summary>
    public bool GtaoFullSize => GtaoQuality >= 2;

    /// <summary>
    /// GTAO's search radius in world units; it gives way to the bounds <see cref="GtaoMaxPixels"/> and <see cref="GtaoMinPixels"/>.
    /// </summary>
    public const float GtaoRadius = 20;

    /// <summary>
    /// The most GTAO's radius spans, as a share of the render height (the world radius gives way to it).
    /// </summary>
    public const float GtaoMaxPixels = 96f / 1440;

    /// <summary>
    /// The least GTAO's radius spans, as a share of the render height (the world radius gives way to it).
    /// </summary>
    public const float GtaoMinPixels = 12f / 1440;

    /// <summary>
    /// The share of GTAO's radius at full weight.
    /// </summary>
    public const float GtaoFalloff = 0.17f;

    /// <summary>
    /// How much of a passed horizon each further tap keeps (1: every occluder is solid behind).
    /// </summary>
    public const float GtaoThickness = 0.9f;

    /// <summary>
    /// GTAO's curve after the blur (KenshiNative's default): the visibility to this power.
    /// </summary>
    public const float GtaoPower = 1.6f;

    /// <summary>
    /// GTAO's curve after the blur (KenshiNative's default): how much of the powered visibility reaches the picture.
    /// </summary>
    public const float GtaoStrength = 0.55f;

    /// <summary>
    /// The characters' traced occlusion on (docs/render-post.md "Traced occlusion on characters"). Off: not calibrated against a reference yet, a
    /// hooded face came out too dark and blotched round the eyes.
    /// </summary>
    public bool CharacterOcclusion;

    /// <summary>
    /// How far the characters' traced occlusion's rays look (<c>Gi.CharacterOcclusionPass</c>), world units.
    /// </summary>
    public const float CharacterOcclusionRadius = 8f;

    /// <summary>
    /// How much of the blocked ambient light the characters' traced occlusion takes back.
    /// </summary>
    public const float CharacterOcclusionStrength = 1f;

    /// <summary>
    /// The albedo of GTAO's multi-bounce term where no lit fragment wrote one to the surface target.
    /// </summary>
    public const float GtaoAlbedo = 0.35f;

    /// <summary>
    /// The upscaler (render scale, TAA / FSR / DLSS); not part of the presets.
    /// </summary>
    public readonly UpscaleOptions Upscale = new();

    #endregion

    #region Presets

    /// <summary>
    /// The settings of a named preset; throws on an unknown name.
    /// </summary>
    public static PostOptions Create(string preset)
    {
        var o = new PostOptions { Preset = preset };
        switch (preset)
        {
            case "meitou":
                break;
            case "off":
                o.Fxaa = o.HeatHaze = false;
                o.ContactShadows = false;
                o.Ssao = o.Dither = o.LowResParticles = o.LightShafts = o.MeitouFog = false;
                o.MeitouHeatHaze = o.ToneMapping = o.NightGrade = o.Bloom = o.LampShadows = false;
                break;
            case "kenshi":
                o.ContactShadows = false;
                o.Ssao = o.Dither = o.LowResParticles = o.LightShafts = o.MeitouFog = false;
                o.MeitouHeatHaze = o.ToneMapping = o.NightGrade = o.Bloom = o.LampShadows = false;

                // Kenshi's chain with the shipped settings: exposure only (no curve, bloom magnitude 0, SSAO commented
                // out), and FXAA then the heat haze on the final image.
                break;
            default:
                throw new ArgumentException($"unknown post preset '{preset}' (meitou, kenshi, off)");
        }

        return o;
    }

    /// <summary>
    /// Copies other's settings into this one; the upscaler and the white balance are kept.
    /// </summary>
    public void CopyFrom(PostOptions other)
    {
        CopyChainSettings(other);
        CopyEffectSettings(other);
    }

    /// <summary>
    /// Copies the chain's switches and levels: anti-aliasing, haze, occlusion, shafts, particles, exposure, tone map, grade and bloom.
    /// </summary>
    void CopyChainSettings(PostOptions other)
    {
        Preset = other.Preset;
        Fxaa = other.Fxaa;
        Smaa = other.Smaa;
        HeatHaze = other.HeatHaze;
        Ssao = other.Ssao;
        ContactShadows = other.ContactShadows;
        Dither = other.Dither;
        LowResParticles = other.LowResParticles;
        LightShafts = other.LightShafts;
        TracedSun = other.TracedSun;
        MeitouFog = other.MeitouFog;
        ShaftQuality = other.ShaftQuality;
        ShaftStrength = other.ShaftStrength;
        ShaftAir = other.ShaftAir;
        ShaftAirDawn = other.ShaftAirDawn;
        ParticleDivisor = other.ParticleDivisor;
        Exposure = other.Exposure;
        ToneMap = other.ToneMap;
        ToneMix = other.ToneMix;
        Grade = other.Grade;
        HeatHazeStrength = other.HeatHazeStrength;
        NightGradeStrength = other.NightGradeStrength;
        BloomStrength = other.BloomStrength;
        MeitouHeatHaze = other.MeitouHeatHaze;
        ToneMapping = other.ToneMapping;
        NightGrade = other.NightGrade;
        Bloom = other.Bloom;
    }

    /// <summary>
    /// Copies the further effects' switches: reflections, hybrid GI, camera effects, depth of field, GTAO, lamp shadows and the traced extras.
    /// </summary>
    void CopyEffectSettings(PostOptions other)
    {
        Ssr = other.Ssr;
        GiHybrid = other.GiHybrid;
        GiRelocate = other.GiRelocate;
        GiCloudShadows = other.GiCloudShadows;
        GiCache = other.GiCache;
        CameraEffectsEverywhere = other.CameraEffectsEverywhere;
        DepthOfField = other.DepthOfField;
        DofAperture = other.DofAperture;
        Gtao = other.Gtao;
        GtaoQuality = other.GtaoQuality;
        LampShadows = other.LampShadows;
        TracedGrass = other.TracedGrass;
        CharacterOcclusion = other.CharacterOcclusion;
    }

    /// <summary>
    /// <c>--gi-hybrid &lt;parts&gt;</c>: the switch on with these parts only (comma list: <c>relocate</c>, <c>clouds</c>, <c>cache</c>).
    /// </summary>
    void SetGiHybrid(string parts)
    {
        GiRelocate = GiCloudShadows = GiCache = false;
        foreach (var part in parts.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part)
            {
                case "relocate": GiRelocate = true; break;
                case "clouds": GiCloudShadows = true; break;
                case "cache": GiCache = true; break;
                default: throw new ArgumentException($"unknown --gi-hybrid part '{part}' (relocate, clouds, cache)");
            }
        }

        GiHybrid = true;
    }

    #endregion

    #region Command line

    /// <summary>
    /// Handles one command-line option; false when it is not a post-processing one.
    /// </summary>
    public bool TryParse(string a, Func<string> next)
    {
        switch (a)
        {
            case "--post":
                CopyFrom(Create(next()));
                return true;
            case "--particle-divisor":
                ParticleDivisor = Math.Clamp(int.Parse(next(), CultureInfo.InvariantCulture), 0, 4);
                return true;
            case "--no-fxaa":
                Fxaa = false;
                return true;
            case "--smaa":
                Smaa = true;
                return true;
            case "--no-heat-haze":
                HeatHaze = false;
                return true;
            case "--gi-hybrid":
                SetGiHybrid(next());
                return true;
            case "--gtao-quality":
                GtaoQuality = Math.Clamp(int.Parse(next(), CultureInfo.InvariantCulture), 0, 2);
                return true;
            case "--bloom":
                BloomStrength = Math.Clamp(float.Parse(next(), CultureInfo.InvariantCulture), 0, 0.25f);
                Bloom = BloomStrength > 0;
                return true;
            case "--upscaler":
                Upscale.Explicit = true;
                Upscale.Kind = ParseUpscaler(next());
                if (Upscale.Kind != UpscalerKind.Off) Upscale.Preferred = Upscale.Kind;

                return true;
            case "--render-scale":
                Upscale.Scale = UpscaleOptions.ParseScale(next());
                Upscale.Explicit = true;
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// The upscaler named by <c>--upscaler</c>; throws on an unknown name.
    /// </summary>
    static UpscalerKind ParseUpscaler(string text) =>
        Enum.TryParse<UpscalerKind>(text, true, out var k) ? k : throw new ArgumentException("--upscaler must be off, taa, fsr, dlss or metalfx");

    #endregion

    #region Description

    /// <summary>
    /// One line naming the settings in effect, for the log.
    /// </summary>
    public string Describe()
    {
        string aa = Upscale.Temporal ? "temporal" : Smaa ? "smaa" : Fxaa ? "fxaa" : "off";
        string heatHaze = HeatHaze ? "weather" + (HeatHazeScale != 1 ? $" strength {HeatHazeScale:0.##}" : "") : "off";
        string ssao = !Ssao
            ? "off"
            : Gtao
                ? $"gtao {GtaoSlices}x{GtaoSteps}{(GtaoFullSize ? " full" : "")}"
                : "on";
        string shafts = LightShafts ? $"{ShaftStrength:0.##} ({ShaftCells}x{ShaftSlices}x{ShaftSamples})" : "off";
        string tonemap = ToneMapUsed.ToString().ToLowerInvariant();
        string mix = ToneMapUsed == ToneMapOperator.Hybrid ? $" {ToneMix:0.##}" : "";
        string grade = GradeUsed ? $"saturation {Saturation:0.##} contrast {Contrast:0.##}" : "off";
        string nightGrade = NightGradeAmount > 0 ? NightGradeAmount.ToString("0.##", CultureInfo.InvariantCulture) : "off";
        string bloom = BloomAmount > 0 ? BloomAmount.ToString("0.###", CultureInfo.InvariantCulture) : "off";
        string focus = DepthOfField ? "f/" + DofAperture.ToString("0.##", CultureInfo.InvariantCulture) : "off";
        string lamps = LampShadows ? "on" : "off";
        string contact = ContactShadows ? "on" : "off";
        string reflections = Ssr ? "on" : "off";
        string traced = TracedSun ? "on" : "off";
        return $"{Preset}: aa {aa}, heat haze {heatHaze}, ssao {ssao}, contact {contact}, reflections {reflections}, " +
            $"traced sun {traced}, shafts {shafts}, exposure x{Exposure:0.##}, tonemap {tonemap}{mix}, grade {grade}, night grade {nightGrade}, " +
            $"bloom {bloom}, depth of field {focus}, lamp shadows {lamps}, gi hybrid {(GiHybrid ? GiHybridParts : "off")}, " +
            $"upscaler {Upscale.Describe()}";
    }

    /// <summary>
    /// The <see cref="GiHybrid"/> parts that are on, space-separated, for <see cref="Describe"/>.
    /// </summary>
    string GiHybridParts
    {
        get
        {
            var parts = new[] { GiRelocate ? "relocate" : "", GiCloudShadows ? "clouds" : "", GiCache ? "cache" : "" };
            return string.Join(' ', parts.Where(p => p.Length > 0));
        }
    }

    #endregion
}

/// <summary>
/// The tone curve of the composite (docs/render-post.md "Tone map and grading").
/// </summary>
public enum ToneMapOperator
{
    /// <summary>
    /// Clamp at 1: what Kenshi does (its tone-map function is exposure only; docs/formats/post-processing.md).
    /// </summary>
    Clamp,

    /// <summary>
    /// Identity up to 0.8 on the brightest channel, then an exponential shoulder towards 1: the same look without the hard clip.
    /// </summary>
    Shoulder,

    /// <summary>
    /// Narkowicz's ACES fit: filmic, but darker mid-tones and more contrast than Kenshi.
    /// </summary>
    Aces,

    /// <summary>
    /// Between the two: the clamp and ACES mixed by <see cref="PostOptions.ToneMix"/> (0 the clamp, 1 ACES): highlights roll off instead of clipping,
    /// with half the shift in contrast and saturation at 0.5.
    /// </summary>
    Hybrid,
}
