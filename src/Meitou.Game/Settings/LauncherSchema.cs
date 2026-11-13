using System.Text.Json;
using Meitou.Data.World;
using Meitou.Game.Host;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Post;
using Meitou.Game.Rendering.Water;
using Meitou.Settings;

namespace Meitou.Game.Settings;

/// <summary>
/// The settings the launcher's Graphics page shows (docs/launcher.md "Graphics"), written by <c>--settings-schema &lt;file&gt;</c> into each
/// release (tools/scripts/package-release.ps1). The keys are the ones this release saves under, so a renamed setting renames its launcher row.
/// Every Tab panel slider and switch is listed, on the page the game's settings menu puts it (<see cref="SettingsMenu.Section"/>).
/// It is also the catalogue the game's own Display and Graphics pages draw their rows from (<see cref="Find"/>, <see cref="Order"/>: name, range,
/// scale direction, value text, choices, impact and order), so the launcher and the game show the same settings the same way.
/// </summary>
static class LauncherSchema
{
    /// <summary>
    /// A three-level choice's names.
    /// </summary>
    static readonly string[] LowMediumHigh = ["Low", "Medium", "High"];

    /// <summary>
    /// The schema key of a <see cref="UserConfig"/> property: its name in camel case.
    /// </summary>
    static string Key(string property) => JsonNamingPolicy.CamelCase.ConvertName(property);

    /// <summary>
    /// A Graphics slider's description: the label as key, its name, hint, range, step, shown default and impact.
    /// </summary>
    static SettingDescription Graphics(string label, float min, float max, float step, float shown) => new()
    {
        Key = label,
        Graphics = true,
        Kind = SettingKind.Slider,
        Name = SettingsMenu.Label(label),
        Description = SettingHints.SliderHint(label),
        Min = min,
        Max = max,
        Step = step,
        Shown = shown,
        Impact = LookPresets.ImpactOf(label),
    };

    /// <summary>
    /// The Tab panel sliders of the Vulkan renderer (WorldFrame.CreateSettingsPanel) with their ranges; the game clamps a saved value to its slider.
    /// </summary>
    static IEnumerable<SettingDescription> GraphicsSliders() =>
    [
        Graphics(WorldFrame.TextureQualitySlider, 0, Data.Textures.TextureQuality.Maximum, 1, 0) with
            { Name = "Texture quality", Labels = [.. Data.Textures.TextureQuality.Labels], Reversed = true },
        Graphics(WorldFrame.FilteringSlider, 0, 4, 1, 4) with { Name = "Texture filtering", Labels = ["1x", "2x", "4x", "8x", "16x"] },
        Graphics(WorldFrame.UpscalerSliders[0], 0, 5, 1, 3) with
            { Name = "Anti-aliasing/upscaling", Labels = ["Off", "FXAA", "SMAA", "TAA", "FSR", "DLSS"] },
        Graphics(WorldFrame.DlssPresetSlider, 0, UpscaleOptions.DlssPresets.Length - 1, 1, 0) with
        {
            Labels = [.. UpscaleOptions.DlssPresets.Select(p => p.Name)],
            ShownWhen = new() { Key = WorldFrame.UpscalerSliders[0], Value = 5 },   // anti-aliasing at DLSS
        },
        Graphics(WorldFrame.UpscalerSliders[1], 0.33f, 1, 0.01f, Percent(UpscaleOptions.DefaultScale)) with
            { Scale = 100, Suffix = " %", Modes = RenderScaleModes() },
        Graphics(WorldFrame.UpscalerSliders[2], 0, 1, 0.01f, 0.5f) with { Format = "0.00" },
        // World
        Graphics(WorldFrame.ObjectDistanceSlider, 1000, 400000, 100, Enhancements.MeitouObjectDistance) with { Logarithmic = true },
        Graphics(WorldFrame.LandmarkDistanceSlider, 1000, 400000, 100, Enhancements.MeitouLandmarkDistance) with { Logarithmic = true },
        Graphics(WorldFrame.DistantTownsSlider, 0, ObjectRanges.MaxDistantTownRangeZones * 10, 1, ObjectRanges.MaxDistantTownRangeZones) with
            { Suffix = " zones" },
        Graphics(WorldFrame.CharacterDistanceSlider, Rendering.Characters.CharacterRenderer.MinDrawDistance,
            Rendering.Characters.CharacterRenderer.MaxDrawDistance, 50, GameplaySettings.DefaultNpcRange),
        Multiplier(WorldFrame.ObjectLodSlider, 0.25f, 4, 1),
        // Fewer pixels of error are finer: drawn mirrored, so right is more detail.
        Graphics(WorldFrame.TerrainErrorSlider, 4, 32, 0.1f, Enhancements.MeitouTerrainPixelError) with
            { Logarithmic = true, Format = "0.0", Suffix = " px", Reversed = true },
        Multiplier(WorldFrame.FoliageDistanceSlider, 0.25f, 80, 1),
        Multiplier(WorldFrame.FoliageLodSlider, 0.25f, 8, 1),
        Graphics(WorldFrame.LargeFoliageRangeSlider, 1000, 120000, 50, 20000) with { Logarithmic = true },
        Graphics(WorldFrame.MediumFoliageRangeSlider, 400, 80000, 50, 10000) with { Logarithmic = true },
        Graphics(WorldFrame.SmallFoliageRangeSlider, 200, 40000, 50, 4000) with { Logarithmic = true },
        Graphics(WorldFrame.ImpostorDistanceSlider, 500, 40000, 50, 5000) with { Logarithmic = true },
        Graphics(WorldFrame.LargeImpostorDistanceSlider, 500, 40000, 50, 10000) with { Logarithmic = true },
        Graphics(WorldFrame.ImpostorBudgetSlider, 0, 2048, 16, 0) with
            { Suffix = " MB", OffBelow = WorldFrame.ImpostorBudgetFloor, OffText = "Automatic" },
        Multiplier(WorldFrame.GrassDistanceSlider, 0.25f, 80, 1),
        Graphics(WorldFrame.GrassDensitySlider, 0.1f, 2, 0.05f, 1) with { Format = "0.00", Suffix = "x" },
        Graphics(WorldFrame.MaterialDistanceSlider, 2000, 100000, 500, 30000) with { Logarithmic = true },
        Graphics(WorldFrame.GroundReliefQualitySlider, 0, 2, 1, 2) with { Labels = LowMediumHigh },
        Graphics(WorldFrame.GroundReliefDepthSlider, 0, 80, 0.5f, 20) with { Format = "0.0", Suffix = " cm" },
        Graphics(WorldFrame.GroundDisplaceDistanceSlider, 10, 200, 1, 50) with { Suffix = " m" },
        Graphics(WorldFrame.GroundParallaxDepthSlider, 10, 200, 5, 100) with { Suffix = " %" },
        Graphics(WorldFrame.GroundParallaxDistanceSlider, 10, 150, 1, 50) with { Suffix = " m" },
        Graphics(WorldFrame.BloodDrawsSlider, Rendering.Terrain.TerrainDecalRenderer.MinSurfaceDraws,
            Rendering.Terrain.TerrainDecalRenderer.MaxSurfaceDraws, 64, Rendering.Terrain.TerrainDecalRenderer.DefaultSurfaceDraws),
        // Water
        Graphics(WorldFrame.WaterReflectionSlider, 0, 4, 1, 2) with { Labels = ["Off", "Landscape", "Characters", "Buildings", "Everything"] },
        Multiplier(WorldFrame.ReflectionRangeSlider, 0.1f, 50, 0.6f),
        Graphics(WorldFrame.ReflectionSamplesSlider, 0, 3, 1, 2) with { Labels = ["Off", "2x", "4x", "8x"] },
        Graphics(WorldFrame.WaterGridSlider, 64, 1024, 64, WaterRenderer.DefaultGridSegments) with { Logarithmic = true },
        Graphics(WorldFrame.WaterOceanSlider, 0, 2, 1, 2) with { Name = "Water wave resolution", Labels = ["64", "128", "256"] },
        Multiplier(WorldFrame.WaterClaritySlider, 0.25f, 8, 0.5f),
        Graphics(WorldFrame.FarWaterClaritySlider, 0, 1, 0.01f, 0.25f) with { Format = "0.00" },
        Graphics(WorldFrame.WaterClearDistanceSlider, 1000, 100000, 100, 10000) with { Logarithmic = true },
        // Lighting: the technique choices first (WorldFrame.AddChoiceSliders), their defaults as a fresh WorldOptions' switches have them
        Graphics(WorldFrame.SunShadowsChoice, 0, 3, 1, ChoiceDefault(WorldFrame.SunShadowsChoice)) with
            { Labels = ["Off", "Faithful cascades", "Meitou cascades", "Traced (ray tracing)"] },
        Graphics(WorldFrame.OcclusionChoice, 0, 2, 1, ChoiceDefault(WorldFrame.OcclusionChoice)) with
            { Name = "Ambient occlusion", Labels = ["Off", "SSAO", "GTAO"] },
        Graphics(WorldFrame.GiChoice, 0, 2, 1, ChoiceDefault(WorldFrame.GiChoice)) with
            { Labels = ["Off (flat ambient)", "Probes (ray tracing)", "Probes + hybrid cache"] },
        Graphics(WorldFrame.ShadowDistanceSlider, KenshiShadows.MinRange * 0.9f, 200000, 100, Enhancements.MeitouShadowRange) with
            { Logarithmic = true, OffBelow = KenshiShadows.MinRange },
        Graphics(WorldFrame.ShadowMapSlider, 0, 2, 1, 2) with
            { Labels = [.. KenshiShadows.QualitySizes.Select((s, i) => $"{LowMediumHigh[i]} · {s}")] },
        Graphics(WorldFrame.ShadowFilterSlider, 0, 2, 1, 2) with { Labels = LowMediumHigh },
        Graphics(WorldFrame.HazeStrengthSlider, 0, 3, 0.01f, Enhancements.MeitouHazeStrength) with { Format = "0.00" },
        Graphics(WorldFrame.HazeDistanceSlider, 10000, 1000000, 1000, 250000) with { Logarithmic = true },
        Graphics(WorldFrame.PlanetshineStrengthSlider, 0, 200, 1, Enhancements.MeitouPlanetshineStrength),
        Graphics(WorldFrame.CloudHeightSlider, VolumetricClouds.MinBottom, VolumetricClouds.MaxBottom, 100, VolumetricClouds.Bottom) with
            { Logarithmic = true },
        Graphics(WorldFrame.AmbientStrengthSlider, 0, 3, 0.05f, 1) with { Format = "0.00" },
        Graphics(WorldFrame.NightAmbientStrengthSlider, 0, 5, 0.05f, 1) with { Format = "0.00" },
        Graphics(WorldFrame.ShaftStrengthSlider, 0, 1, 0.05f, new PostOptions().ShaftStrength) with { Format = "0.00" },
        Graphics(WorldFrame.ShaftAirSlider, 0, 4, 0.05f, 1) with { Format = "0.00" },
        Multiplier(WorldFrame.ShaftAirDawnSlider, 0, 6, 1) with { Step = 0.1f, Format = "0.0" },
        Graphics(WorldFrame.ShaftQualitySlider, 0, 2, 1, 2) with { Labels = LowMediumHigh },
        Graphics(WorldFrame.GtaoQualitySlider, 0, 2, 1, 1) with { Labels = LowMediumHigh },
        // Effects
        Graphics(WorldFrame.ParticleSliders[0], 0, 4, 1, 3) with
        {
            Name = "Weather particles",
            Labels = ["Off", "Quarter resolution", "Half resolution", "Automatic", "Full resolution"],
        },
        Graphics(WorldFrame.ParticleSliders[1], 0.1f, 1, 0.05f, 1) with { Format = "0.00", Suffix = "x" },
        Graphics(WorldFrame.ToneMapSlider, 1, 3, 1, 3) with { Labels = ["Shoulder", "ACES", "Hybrid"] },
        Graphics(WorldFrame.ToneMixSlider, 0, 1, 0.05f, 0.75f) with { Format = "0.00" },
        Graphics(WorldFrame.GradingSlider, 0, 1, 1, 1) with { Name = "Colour grading", Labels = ["Off", "On"] },
        Graphics(WorldFrame.HeatHazeStrengthSlider, 0, 3, 0.05f, Enhancements.MeitouHeatHazeStrength) with { Format = "0.00" },
        Graphics(WorldFrame.NightGradeStrengthSlider, 0, 1, 0.05f, NightGrade.MeitouStrength) with { Format = "0.00" },
        Graphics(WorldFrame.BloomStrengthSlider, 0, 0.25f, 0.005f, new PostOptions().BloomStrength) with { Format = "0.000" },
        Graphics(WorldFrame.DofApertureSlider, 0.25f, 22, 0.05f, new PostOptions().DofAperture) with { Logarithmic = true, Format = "0.00" },
    ];

    /// <summary>
    /// The render scale's quality modes, lowest first: FSR's and DLSS's ratios, as the slider stores them (whole percent).
    /// </summary>
    static List<SettingMode> RenderScaleModes() =>
    [
        new() { Name = "Ultra performance", Value = Percent(1 / 3f) },
        new() { Name = "Performance", Value = Percent(0.5f) },
        new() { Name = "Normal", Value = Percent(1 / 1.7f) },
        new() { Name = "Quality", Value = Percent(1 / 1.5f) },
        new() { Name = "Ultra quality", Value = Percent(UpscaleOptions.UltraQualityScale) },
        new() { Name = "DLAA", Value = 1 },
    ];

    /// <summary>
    /// <paramref name="scale"/> rounded to two decimals.
    /// </summary>
    static float Percent(float scale) => MathF.Round(scale * 100) / 100;

    /// <summary>
    /// A multiplier slider's description: steps of 0.05, logarithmic when its minimum is above 0, shown with an x.
    /// </summary>
    static SettingDescription Multiplier(string label, float min, float max, float shown) =>
        Graphics(label, min, max, 0.05f, shown) with { Logarithmic = min > 0, Format = "0.00", Suffix = "x" };

    /// <summary>
    /// A technique choice's value for a fresh <see cref="WorldOptions"/>'s switches (Meitou cascades, SSAO or GTAO, probes or probes + hybrid).
    /// </summary>
    static float ChoiceDefault(string choice)
    {
        var on = WorldFrame.LiveSwitches(new WorldOptions(), () => null, () => null, () => null).ToDictionary(e => e.Name, e => e.IsMeitou);
        bool On(string name) => on.TryGetValue(name, out bool v) && v;

        return choice switch
        {
            WorldFrame.SunShadowsChoice => On("Traced sun shadows") ? 3
                : On("Shadows") ? 2
                : 1,
            WorldFrame.OcclusionChoice => !On("Ambient occlusion") ? 0
                : On("Ground-truth occlusion") ? 2
                : 1,
            _ => !On("Indirect light") ? 0
                : On("Hybrid GI") ? 2
                : 1,
        };
    }

    /// <summary>
    /// The Tab panel switches: the Faithful / Meitou ones (their state as a fresh <see cref="WorldOptions"/> has it) and the plain on/off ones.
    /// </summary>
    static IEnumerable<SettingDescription> Switches()
    {
        static SettingDescription Switch(string label, string off, string on, bool shown) => new()
        {
            Key = SettingsMenu.SwitchKeyPrefix + label,
            Graphics = true,
            Kind = SettingKind.Toggle,
            Name = SettingsMenu.Label(label),
            Description = SettingHints.ToggleHint(label),
            Off = off,
            On = on,
            Shown = shown ? 1 : 0,
            Impact = LookPresets.ImpactOf(label),
        };
        // The switches a technique choice stands for are saved but not shown: the choice is their row (the launcher writes it, the game applies it
        // last).
        foreach (var e in WorldFrame.LiveSwitches(new WorldOptions(), () => null, () => null, () => null)
                     .Where(e => !LookPresets.HiddenSwitch(e.Name) && !WorldFrame.ChoiceDrivenSwitches.Contains(e.Name)))
            yield return Switch(e.Name, e.Faithful is "off" or "none" ? "Off" : $"Faithful: {e.Faithful}", $"Meitou: {e.Meitou}", e.IsMeitou);
        yield return Switch(WorldFrame.FifthCascadeSwitch, "Off", "On", true);
        // The hybrid GI's parts (--gi-hybrid), on at start.
        yield return Switch(WorldFrame.GiRelocateSwitch, "Off", "On", true);
        yield return Switch(WorldFrame.GiCloudShadowsSwitch, "Off", "On", true);
        yield return Switch(WorldFrame.GiCacheSwitch, "Off", "On", true);
        // The Advanced materials' parts, on at start.
        yield return Switch(WorldFrame.MaterialHairSwitch, "Off", "On", true);
        yield return Switch(WorldFrame.MaterialClothSwitch, "Off", "On", true);
        yield return Switch(WorldFrame.MaterialSkinSwitch, "Off", "On", true);
        yield return Switch(WorldFrame.MaterialEyesSwitch, "Off", "On", true);
        yield return Switch(WorldFrame.MaterialMetalSwitch, "Off", "On", true);
        yield return Switch(WorldFrame.MaterialBeadsSwitch, "Off", "On", true);
        yield return Switch(WorldFrame.TracedGrassSwitch, "Off", "On", true) with
            { ShownWhen = new() { Key = WorldFrame.SunShadowsChoice, Value = 3 } };
        yield return Switch(WorldFrame.GrassCoverSwitch, "Off", "Meitou: on", true);
        yield return Switch(WorldFrame.WetWorldSwitch, "Off", "On", true);
        // An optional effect, in neither look: plain Off / On (Toggle.Optional).
        yield return Switch(WorldFrame.BloomSwitch, "Off", "On", false);
        yield return Switch(WorldFrame.CameraEffectsSwitch, "Off", "On", false);
        // Faithful / Meitou switches whose game side is off; both start off (Ultra turns them on).
        yield return Switch(WorldFrame.RockSmoothingSwitch, "Off", "Meitou: on", false);
        yield return Switch(WorldFrame.OutlineRefineSwitch, "Off", "Meitou: on", false);
    }

    /// <summary>
    /// Frame generation: the switch, and whose (FSR 3 or DLSS) as a two-step slider of its own key.
    /// </summary>
    static SettingDescription[] FrameGenerationSettings(UserConfig defaults) =>
    [
        new()
        {
            Key = Key(nameof(UserConfig.FrameGeneration)), Kind = SettingKind.Toggle, Name = "Frame generation",
            Description = "Adds a generated frame between rendered ones: nearly twice the frame rate, a little more input delay.",
            Default = defaults.FrameGeneration ? 1 : 0, On = "On", Off = "Off",
        },
        new()
        {
            Key = Key(nameof(UserConfig.FrameGenerationVendor)), Kind = SettingKind.Slider, Name = "Frame generation by",
            Description = "FSR 3: any GPU, needs the FidelityFX library. "
                + "DLSS: Windows with an RTX 40 or later, needs Streamline's frame generation DLLs.",
            Default = (int)defaults.FrameGenerationVendor, Shown = (int)defaults.FrameGenerationVendor, Min = 0, Max = 1, Step = 1,
            Labels = ["FSR 3", "DLSS"],
        },
    ];

    /// <summary>
    /// HDR output (docs/render-post.md "HDR output"): the switch, read at start-up, and the picture's white and the display's peak in nits.
    /// </summary>
    static SettingDescription[] HdrSettings(UserConfig defaults) =>
    [
        new()
        {
            Key = Key(nameof(UserConfig.Hdr)), Kind = SettingKind.Toggle, Name = SettingsMenu.HdrName, Description = SettingsMenu.HdrHint,
            Default = defaults.Hdr ? 1 : 0, On = "On", Off = "Off",
        },
        new()
        {
            Key = Key(nameof(UserConfig.HdrPaperNits)), Kind = SettingKind.Slider, Name = SettingsMenu.HdrPaperName,
            Description = SettingsMenu.HdrPaperHint,
            Default = UserConfig.HdrPaperAuto - 10, Shown = UserConfig.HdrPaperAuto - 10, Min = UserConfig.HdrPaperAuto - 10, Max = 500, Step = 10,
            Suffix = " nits",
            OffBelow = UserConfig.HdrPaperAuto, OffText = "Automatic",
        },
        new()
        {
            Key = Key(nameof(UserConfig.HdrPeakNits)), Kind = SettingKind.Slider, Name = SettingsMenu.HdrPeakName,
            Description = SettingsMenu.HdrPeakHint,
            Default = defaults.HdrPeakNits, Shown = defaults.HdrPeakNits, Min = 200, Max = 4000, Step = 50, Suffix = " nits",
        },
    ];

    /// <summary>
    /// The launcher's Display section: full screen, VSync, the frame limit, frame generation, HDR output, the world tick and the Display ones of
    /// the given sliders.
    /// </summary>
    static SettingsSection DisplaySection(UserConfig defaults, List<SettingDescription> sliders) => new()
    {
        Name = "Display",
        Settings =
        [
            new()
            {
                Key = Key(nameof(UserConfig.FullScreen)), Kind = SettingKind.Toggle, Name = "Full screen",
                Description = SettingHints.SliderHint("Full screen"), Default = defaults.FullScreen ? 1 : 0, On = "Borderless", Off = "Window",
            },
            new()
            {
                Key = Key(nameof(UserConfig.VSync)), Kind = SettingKind.Toggle, Name = "VSync", Description = SettingHints.SliderHint("VSync"),
                Default = defaults.VSync ? 1 : 0,
            },
            new()
            {
                Key = Key(nameof(UserConfig.FpsLimit)), Kind = SettingKind.Slider, Name = "Frame limit",
                Description = SettingHints.SliderHint("Frame limit"), Default = defaults.FpsLimit, Shown = defaults.FpsLimit, Min = 0, Max = 360,
                Step = 5, Suffix = " fps", OffBelow = 1, OffText = "Unlimited",
            },
            .. (!OperatingSystem.IsMacOS() ? FrameGenerationSettings(defaults) : []),
            .. (!OperatingSystem.IsMacOS() ? HdrSettings(defaults) : []),
            new()
            {
                Key = Key(nameof(UserConfig.TickAhead)), Kind = SettingKind.Slider, Name = "World tick alongside drawing",
                Description = SettingsMenu.TickAheadHint, Default = (int)defaults.TickAhead, Shown = (int)defaults.TickAhead, Min = 0, Max = 2,
                Step = 1, Labels = [.. SettingsMenu.TickAheadChoices],
            },
            // Null defaults below: the game's value depends on the Faithful / Meitou mode and the computer until the setting is saved.
            .. sliders.Where(s => SettingsMenu.Section(s.Key) == "Display"),
        ],
    };

    /// <summary>
    /// The schema: the settings version, the renderers, the Display section and the Graphics pages' sections with their groups, and the presets.
    /// </summary>
    public static SettingsSchema Create()
    {
        var defaults = new UserConfig();
        var sliders = GraphicsSliders().ToList();
        var switches = Switches().ToList();
        var display = DisplaySection(defaults, sliders);
        display.Groups = FeatureGrouping.LauncherGroups([.. display.Settings.Select(s => s.Key)]);
        return new SettingsSchema
        {
            SettingsVersion = UserConfig.CurrentVersion,
            Renderers = OperatingSystem.IsMacOS() ? ["vulkan", "metal"] : ["vulkan"],
            // The game's Graphics sub-pages (the look tabs, then the quality tabs), each with its switches first, as in the settings menu.
            Sections =
            [
                display,
                // Interior lamps is left out: the stealth sight counts its light, so the game keeps it with the gameplay changes (Advanced).
                .. SettingsMenu.GraphicsPages.Select(page => new SettingsSection
                {
                    Name = page,
                    // Feature by feature, as the menu's tab (SettingsMenu.GroupRank); within, switches first, in the catalogue's order.
                    Settings = [.. switches.Where(s => SettingsMenu.ToggleSection(s.Key[SettingsMenu.SwitchKeyPrefix.Length..]) == page)
                        .Concat(sliders.Where(s => SettingsMenu.Section(s.Key) == page))
                        .OrderBy(s => FeatureGrouping.GroupRank(RankKey(s.Key)))],
                }).Where(section => section.Settings.Count > 0)
                .Select(section =>
                {
                    section.Groups = FeatureGrouping.LauncherGroups([.. section.Settings.Select(s => s.Key)]);
                    return section;
                }),
            ],
            Presets = new SettingsPresets
            {
                Key = Key(nameof(UserConfig.GraphicsPreset)),
                Name = "Quality preset",
                Description = QualityTiers.PresetHint,
                Names = [.. QualityTiers.PresetChoices],
                Custom = QualityTiers.PresetChoices[^1],
                Default = QualityTiers.DefaultPreset,
                Keys = [.. QualityTiers.PresetKeys()],
                Detect = Program.HardwareProbeFlag,
            },
            Looks = Looks(switches, sliders),
        };
    }

    /// <summary>
    /// The key a rank is looked up by: a switch's label, without its prefix.
    /// </summary>
    static string RankKey(string key) => key.StartsWith(SettingsMenu.SwitchKeyPrefix, StringComparison.Ordinal)
        ? key[SettingsMenu.SwitchKeyPrefix.Length..]
        : key;

    /// <summary>
    /// The looks as <c>graphics</c> values, as the settings menu applies them (SettingsMenu.SetFaithful, ApplyLook): Faithful turns every look switch
    /// off and puts the look sliders at the game's value (the one their label names; a strength under a switch stays); Meitou leaves them at their
    /// start value, the tier's switches to the quality preset.
    /// </summary>
    static SettingsLooks Looks(List<SettingDescription> switches, List<SettingDescription> sliders)
    {
        var shown = switches.Concat(sliders).ToDictionary(s => s.Key, s => s.Shown);
        var keys = LookPresets.LookKeys(switches.Select(s => s.Key[SettingsMenu.SwitchKeyPrefix.Length..]), sliders.Select(s => s.Key)).ToList();
        var tier = LookPresets.TierSwitchKeys.ToHashSet();
        static bool IsSwitch(string key) => key.StartsWith(SettingsMenu.SwitchKeyPrefix, StringComparison.Ordinal);
        return new SettingsLooks
        {
            Name = "Look",
            Description = LookPresets.LookHint,
            FaithfulKey = Key(nameof(UserConfig.FaithfulLook)),
            PresetKey = Key(nameof(UserConfig.LookPreset)),
            Faithful = keys.ToDictionary(k => k,
                k => IsSwitch(k) ? 0 : LookPresets.UnderSwitch(k) ? shown[k] : LookPresets.LabelGameValue(k) ?? shown[k]),
            Meitou = keys.Where(k => !tier.Contains(k)).ToDictionary(k => k, k => shown[k]),
            Presets = [.. LookPresets.BuiltInLooks.Select(l => new SettingsLook { Name = l.Name, Values = new(l.Values) })],
            Folder = LookPresets.LookFolder,
            Keys = [.. keys.Where(k => IsSwitch(k)
                ? LookPresets.IsLook(k[SettingsMenu.SwitchKeyPrefix.Length..], true)
                : LookPresets.IsLook(k, false))],
            Made = LookPresets.MeitouVersion,
        };
    }

    /// <summary>
    /// Every listed setting's description by key, and its place in the schema's order.
    /// </summary>
    static readonly Lazy<(Dictionary<string, SettingDescription> Find, Dictionary<string, int> Order)> Catalogue = new(() =>
    {
        var all = Create().Sections.SelectMany(section => section.Settings).ToList();
        return (all.ToDictionary(s => s.Key), all.Select((s, i) => (s.Key, i)).ToDictionary(x => x.Key, x => x.i));
    });

    /// <summary>
    /// A setting's description by its key (a slider's label, <c>Meitou: </c> and a switch's label, a top-level key such as <c>fpsLimit</c>); null
    /// when not listed.
    /// </summary>
    internal static SettingDescription? Find(string key) => Catalogue.Value.Find.GetValueOrDefault(key);

    /// <summary>
    /// Where a setting comes on its page (the launcher's order, the game's too); unlisted ones go last, in their own order.
    /// </summary>
    internal static int Order(string key) => Catalogue.Value.Order.GetValueOrDefault(key, int.MaxValue);

    /// <summary>
    /// <c>--settings-schema &lt;file&gt;</c>: writes the schema and exits, before anything else starts.
    /// </summary>
    public static int Write(string path)
    {
        File.WriteAllText(path, Create().ToJson());
        Console.WriteLine($"schema    {path}");
        return 0;
    }
}