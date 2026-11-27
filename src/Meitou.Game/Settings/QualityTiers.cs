using Meitou.Game.Host;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Post;

namespace Meitou.Game.Settings;

/// <summary>
/// The Graphics page's quality tiers (docs/native-ui.md "Settings"); the look and its presets are <see cref="LookPresets"/>.
/// </summary>
sealed class QualityTiers(SettingsMenu menu)
{
    /// <summary>
    /// The quality preset choice's names.
    /// </summary>
    internal static readonly string[] PresetNames = ["Low", "Medium", "High", "Ultra", "Extreme", "Custom"];

    /// <summary>
    /// The indexes of the tiers and Custom in <see cref="PresetNames"/>. Each tier is meant for a class of GPU (<see cref="HardwareProbe"/>):
    /// Low a GTX 1050 to an RTX 3050, Medium an RTX 3060, High an RTX 3070 to 4070, Ultra an RTX 4070 Ti to 4080, Extreme above.
    /// </summary>
    internal const int LowPreset = 0, MediumPreset = 1, HighPreset = 2, UltraPreset = 3, ExtremePreset = 4, CustomPreset = 5;

    /// <summary>
    /// A preset's slider: its value at Low, Medium and Ultra (High is the start default; NaN: the start default too), either fixed or a multiple of
    /// the start default. <see cref="Mode"/> names the switch whose side picks the base instead (the slider's Meitou or Faithful mark), so High reads
    /// High in both looks. <see cref="LowFixed"/>: Low's value is fixed even where the others are multiples. Low is <c>--low-end</c>
    /// (WorldOptions.ApplyLowEnd): a slider it leaves alone stays at High. <see cref="Extreme"/>: its value at Extreme, Ultra's when null (the
    /// distances: Extreme adds effects, not range). <see cref="MetalUltra"/>: its value at Ultra on the Metal renderer (<see cref="MetalTiers"/>),
    /// Ultra's when null; Extreme still falls back to <see cref="Ultra"/>.
    /// </summary>
    internal sealed record PresetSlider(string Label, float Low, float Medium, float Ultra, bool Scale = false, string? Mode = null, float? High = null,
        bool LowFixed = false, float? Extreme = null, float? MetalUltra = null);

    /// <summary>
    /// --low-end's large foliage range, as a multiple of the default; the medium and small ranges follow it.
    /// </summary>
    const float LowFoliageRange = WorldOptions.LowEndLargeRange / Data.World.FoliageSizes.DefaultLargeRange;

    /// <summary>
    /// The sliders the presets set.
    /// </summary>
    internal static readonly PresetSlider[] PresetSliders =
    [
        new(WorldFrame.ObjectDistanceSlider, WorldOptions.LowEndObjectDistance, 0.6f, 2, Mode: "Draw distances", LowFixed: true),

        // --low-end's landmarks follow its object distance, at the Meitou defaults' ratio; Faithful has none.
        new(WorldFrame.LandmarkDistanceSlider, WorldOptions.LowEndObjectDistance / Enhancements.MeitouObjectDistance, 0.6f, 1.5f,
            Mode: "Draw distances"),
        new(WorldFrame.DistantTownsSlider, float.NaN, 6, float.NaN),
        new(WorldFrame.ObjectLodSlider, WorldOptions.LowEndObjectLod, 0.6f, 2),
        new(WorldFrame.TerrainErrorSlider, float.NaN, 1.5f, 0.6f, Mode: "Draw distances"),
        new(WorldFrame.GroundReliefQualitySlider, 0, 1, 2, High: 2),
        new(WorldFrame.GroundReliefDepthSlider, float.NaN, float.NaN, float.NaN, High: 20),
        new(WorldFrame.FoliageDistanceSlider, float.NaN, 0.5f, 2, Scale: true),
        new(WorldFrame.FoliageLodSlider, float.NaN, 0.75f, 2),
        new(WorldFrame.LargeFoliageRangeSlider, LowFoliageRange, 0.5f, 1.5f, Scale: true),
        new(WorldFrame.MediumFoliageRangeSlider, LowFoliageRange, 0.5f, 1.5f, Scale: true),
        new(WorldFrame.SmallFoliageRangeSlider, LowFoliageRange, 0.5f, 1.5f, Scale: true),
        new(WorldFrame.ImpostorDistanceSlider, WorldOptions.LowEndImpostorDistance, 0.6f, 1.5f, Scale: true, LowFixed: true),
        new(WorldFrame.LargeImpostorDistanceSlider, WorldOptions.LowEndLargeImpostorDistance, 0.5f, 1.5f, Scale: true, LowFixed: true),
        new(WorldFrame.GrassDistanceSlider, float.NaN, 0.5f, 2, Scale: true),
        new(WorldFrame.GrassDensitySlider, WorldOptions.LowEndGrassDensity, 0.5f, float.NaN),
        new(WorldFrame.WaterReflectionSlider, 0, 1, 4),
        new(WorldFrame.ReflectionRangeSlider, float.NaN, 1, 6),

        // The samples are 4x by default (docs/render-water.md). Low draws no reflection, but a player may turn it on: 2x, as Medium.
        new(WorldFrame.ReflectionSamplesSlider, 1, 1, float.NaN, High: 2),

        // Low's shadow distance is the slider's left end: shadows off.
        new(WorldFrame.ShadowDistanceSlider, float.NegativeInfinity, 0.6f, 1.5f, Mode: "Shadows"),
        new(WorldFrame.ShadowMapSlider, float.NaN, 1, 2),
        new(WorldFrame.ShadowFilterSlider, 0, 1, 2),

        // The count is Meitou's five at High and Ultra; Low draws no shadows and keeps the game's four.
        new(WorldFrame.ShadowCascadesSlider, 4, 4, 5, High: 5, Extreme: 6),
        new(WorldFrame.ShaftQualitySlider, 0, 1, 2, High: 2, Extreme: 3),

        // Medium keeps the SSAO's twelve taps (its GTAO is off); High the GTAO's medium quality. Metal's Ultra too: the high quality's full size
        // cost an M6 2.4 ms (bench save, 1280x960) for a picture that differed from Medium's as two frames of High do.
        new(WorldFrame.GtaoQualitySlider, 0, 1, 2, High: 1, MetalUltra: 1),
        new(WorldFrame.ParticleSliders[1], WorldOptions.LowEndParticleDensity, 0.5f, float.NaN),
        new(WorldFrame.FilteringSlider, 0, 2, 4, High: 4),

        // The start default is the saved level (applied before the menu exists), so High names the full size.
        // TextureQualitySlider stays for BenchmarkSpec.TextureQuality; the menu's sliders are the three per-category ones below.
        new(WorldFrame.TextureQualitySlider, WorldOptions.LowEndTextureQuality, 1, 0, High: 0),
        new(WorldFrame.CharacterTextureQualitySlider, WorldOptions.LowEndTextureQuality, 1, 0, High: 0),
        new(WorldFrame.TerrainTextureQualitySlider, WorldOptions.LowEndTextureQuality, 1, 0, High: 0),
        new(WorldFrame.ObjectTextureQualitySlider, WorldOptions.LowEndTextureQuality, 1, 0, High: 0),
        new(WorldFrame.GroundDisplaceDistanceSlider, 0.5f, 0.75f, 1.5f, Scale: true),
        new(WorldFrame.GroundParallaxDistanceSlider, 0.5f, 0.75f, 1.5f, Scale: true),

        // The game's 30000 at High; Low is --low-end's. Only the textured shading and its streaming depend on it: the far arrays are made once.
        new(WorldFrame.MaterialDistanceSlider, WorldOptions.LowEndMaterialDistance, 20000, 45000, High: 30000),
    ];

    /// <summary>
    /// A quality switch: on or off at Low, Medium and Ultra; at High its start default unless <see cref="High"/> says, at Extreme as Ultra unless
    /// <see cref="Extreme"/> says. Meitou features only: Faithful keeps them off whatever the tier. <see cref="MetalOnly"/>: the presets set it on
    /// the Metal renderer only (<see cref="MetalTiers"/>); on Vulkan they leave it as it is.
    /// </summary>
    internal sealed record PresetToggle(string Label, bool Low, bool Medium, bool Ultra, bool? High = null, bool? Extreme = null,
        bool MetalOnly = false);

    /// <summary>
    /// The quality switches the presets set: the relief's (docs/render-terrain.md "Ground relief", RTX 4090: refinement +0.38 ms; parallax and
    /// displacement are effects, not tiers), then Meitou's effects by what they cost (RTX 4090 at 1440p native, GPU time, the settings matrix of
    /// <see cref="LookPresets.ImpactOf"/>: the materials, trails, foam and far grass up to 0.35 ms; reflections 0.65 to 1.4, GTAO 0.7 to 0.8 and
    /// 0.47 more at its high quality; the hybrid GI up to 1.3; lamp shadows 1.1 to 4.6; the GI traced on characters 2.7 to 3.6; the traced grass
    /// shadows +2.6 to +4.8 in grassland). The techniques (traced sun, GTAO, hybrid GI) are the switches their choices stand for (<see
    /// cref="WorldFrame.ChoiceDrivenSwitches"/>).
    /// </summary>
    internal static readonly PresetToggle[] AllPresetToggles =
    [
        new(WorldFrame.RockSmoothingSwitch, false, false, true),
        new(WorldFrame.OutlineRefineSwitch, false, false, true),

        // The simple terrain material: Low only (docs/render-terrain.md "Simple material": 1.3-2.7 ms of terrain on an Iris Xe at --low-end).
        new(WorldFrame.SimpleTerrainSwitch, true, false, false, High: false),
        new("Advanced materials", false, true, true, High: true),
        new("Ground trails", false, true, true, High: true),
        new("Water contact foam, wakes and ripples", false, true, true, High: true),
        new(WorldFrame.GrassCoverSwitch, false, true, true, High: true),

        // Effects whose cost shows only in their content (Radeon 8060S, 1080p, High; Observed with fresh blood, in forced rain): the blood on walls
        // and objects 2.5 ms, the water beads up to 0.6 ms close up, the rain's drops and puddles 0.6-0.7 ms. On from High, as they start.
        new("Blood on surfaces", false, false, true, High: true),
        new("Water beads", false, false, true, High: true),
        new("Rain drops and puddles", false, false, true, High: true),

        // The contact shadows: 0.4-0.8 ms at High (same machine). From Ultra, where they act only without the traced sun (it replaces them).
        new("Contact shadows", false, false, true, High: false),
        new("Reflections", false, false, true, High: true),
        new("Ground-truth occlusion", false, false, true, High: true),

        // The indirect light and its share indoors: from Ultra, the classes that trace (the tiers below run on GPUs without ray tracing). Before the
        // traced rows, which follow the indirect light (SwitchTargetHere). Off where the device has no GI (GiSwitches).
        new("Indirect light", false, false, true, High: false),
        new("Indoor GI", false, false, true, High: false),
        new("Hybrid GI", false, false, true, High: false),
        new("Traced sun shadows", false, false, true, High: false),
        // Traced lamp shadows: 0.9 to 6.3 ms on an RTX 4090 at 1440p (a lit hall at night the most), so only from the classes that trace the sun.
        new("Lamp shadows", false, false, true, High: false),
        // The characters' traced indirect light: 2.7 ms on a close-up, 3.6 in a hall full of characters; High keeps GTAO without it.
        new(WorldFrame.GiCharactersSwitch, false, false, true, High: false),
        // Off in every tier: with the rays on, they take the sun at every distance. With the cascades past 2000 the shadows of trees changed
        // (sharp to soft) as the camera moved the band over them.
        new(WorldFrame.TracedFarCascadesSwitch, false, false, false, High: false),
        new(WorldFrame.TracedGrassSwitch, false, false, false, High: false, Extreme: true),

        // Metal: the hybrid GI's radiance cache and the probes' 64 rays at Extreme only (docs/metal-performance-memory.md "Metal profiler": an M6 at
        // Ultra on the bench save, 1280x960, the cache 7 ms of a 23.5 ms GPU frame, the 64 rays 1.2 ms). Vulkan's tiers leave both as they are.
        new(WorldFrame.GiCacheSwitch, false, false, false, Extreme: true, MetalOnly: true),
        new(WorldFrame.GiRaysSwitch, false, false, false, Extreme: true, MetalOnly: true),
    ];

    /// <summary>
    /// The renderer is Metal (set by the Metal loop before its settings exist): the <see cref="PresetToggle.MetalOnly"/> rows apply.
    /// </summary>
    internal static bool MetalTiers;

    /// <summary>
    /// <see cref="AllPresetToggles"/> without the Metal rows.
    /// </summary>
    static readonly PresetToggle[] VulkanPresetToggles = [.. AllPresetToggles.Where(p => !p.MetalOnly)];

    /// <summary>
    /// The quality switches the presets set on this renderer (<see cref="AllPresetToggles"/>, the Metal rows only on Metal).
    /// </summary>
    internal static IReadOnlyList<PresetToggle> PresetToggles => MetalTiers ? AllPresetToggles : VulkanPresetToggles;

    /// <summary>
    /// The tier switches that need the indirect light's traced scene: off, whatever the tier, while it is off (no ray tracing on the device).
    /// </summary>
    internal static readonly string[] TracedSwitches =
        ["Hybrid GI", "Traced sun shadows", WorldFrame.TracedFarCascadesSwitch, "Lamp shadows", WorldFrame.GiCharactersSwitch];

    /// <summary>
    /// The tier switches of the indirect light itself: off, whatever the tier, on a device without the GI (no ray queries, <c>--no-gi</c>; <see
    /// cref="GiHere"/>), where the switch reads off whatever it is set to.
    /// </summary>
    internal static readonly string[] GiSwitches = ["Indirect light", "Indoor GI"];

    /// <summary>
    /// The device has the GI: on Metal its switches exist only then (MetalGameLoop), on Vulkan the device was made with ray queries.
    /// </summary>
    static bool GiHere => MetalTiers || WorldFrame.RayTracingAvailable;

    /// <summary>
    /// The graphics settings no tier sets, by their launcher key, each group with why (docs/native-ui.md "Settings"): every other graphics setting
    /// is a tier row (<see cref="PresetKeys"/>) or the look's (LauncherSchemaTests checks both ways).
    /// </summary>
    internal static readonly IReadOnlySet<string> OutsideTiers = new HashSet<string>(
    [
        // The anti-aliasing's: the player's, by hardware and taste (the launcher benchmark pins TAA).
        WorldFrame.DlssPresetSlider, WorldFrame.SmaaQualitySlider, WorldFrame.UpscalerSliders[2],

        // Follows the hardware by itself.
        WorldFrame.ImpostorBudgetSlider,

        // A matter of taste or content, not of cost.
        .. SwitchKeys("Gear sway", "Bolt tracers", "Glass bottles", "Polished armour", WorldFrame.NearTreeFadeSwitch, "Soft grazing sun",
            "Item icons", "Portrait and editor sun", "Character editor bar"),

        // The sky's or the ground's art, not cost.
        SettingsMenu.SwitchKey("High Altitude Clouds"), WorldFrame.HazeDistanceSlider, WorldFrame.CloudHeightSlider,
        WorldFrame.GroundParallaxDepthSlider, .. WorldFrame.HighCloudSliders,

        // The Faithful / Meitou base the tier's distances scale from.
        .. SwitchKeys("Draw distances", "Foliage ranges"),

        // Saves memory for the same picture: on at every tier.
        SettingsMenu.SwitchKey("Mesh welding"),

        // Save GPU time for the same picture, on at every tier (the look keeps them too, LookPresets.PerformanceSwitches). Observed (Radeon 8060S,
        // 1080p, Ultra, GPU-bound; the cost of turning each off): character levels up to +1.1 ms, foliage levels +6.0, far impostors +8.8.
        .. SwitchKeys("Character levels", "Foliage levels", "Far impostors"),

        // No measurable cost (same machine and tier, the noise about 0.3 ms, in close and wide views, the sea's for the water): left at their start
        // values. The water at 128 segments or the smallest wave grid, the GI parts (with the hybrid GI on) and the characters' and buildings'
        // detail effects off all stayed within the noise.
        // With fresh blood about the decal cap (64 to 2048), the blood cells, the bloody footprints and the blood on weapons and gear too.
        WorldFrame.WaterGridSlider, WorldFrame.WaterOceanSlider, WorldFrame.BloodDrawsSlider,
        .. SwitchKeys("Blood cells", "Bloody footprints", "Blood on weapons and gear", WorldFrame.GiRelocateSwitch, WorldFrame.GiCloudShadowsSwitch, "Hair highlights", "Cloth sheen", "Skin sheen and glow",
            "Eye catchlights", "Metal on characters", "Metal on buildings"),

        // The game's NPC View Range: the simulation's on-screen test reads it too (docs/formats/settings.md), so a tier would change play. Observed:
        // 8000 to 2000 saved 0.5 ms at High and 2.0 at Ultra in a wide low view, nothing near.
        WorldFrame.CharacterDistanceSlider,
    ]);

    /// <summary>
    /// The launcher keys of the switches <paramref name="labels"/>.
    /// </summary>
    static IEnumerable<string> SwitchKeys(params string[] labels) => labels.Select(SettingsMenu.SwitchKey);

    /// <summary>
    /// The Meitou effects Low sets Faithful, as <c>--low-end</c> does. The look's ones (docs/native-ui.md "Settings") are held off without changing
    /// the preset, unless kept on (<see cref="UserConfig.KeepAtLow"/>).
    /// </summary>
    internal static readonly string[] LowOffSwitches =
        ["Ambient occlusion", "Light shafts", "Water", "Shadows", "Ground parallax", "Ground displacement (tessellation)"];

    /// <summary>
    /// The on/off effects Low turns off too (the tessellation's outline refinement and rocks; they are not Faithful / Meitou switches).
    /// </summary>
    internal static readonly string[] LowOffToggles = [WorldFrame.OutlineRefineSwitch, WorldFrame.RockSmoothingSwitch];


    /// <summary>
    /// The quality preset choice's hint.
    /// </summary>
    internal const string PresetHint =
        "Distances, shadows, detail and Meitou's effects together, by class of graphics card. Low is the same as --low-end.";

    /// <summary>
    /// The tier being applied (<see cref="ApplyPreset"/>, <see cref="LookPresets.SetFaithful"/>), so the Low cut follows it before the sliders reach
    /// it.
    /// </summary>
    internal int? TierOverride;

    /// <summary>
    /// The tier the settings sat at when last looked (<see cref="SyncTier"/>); -1 not yet.
    /// </summary>
    internal int LastTier = -1;

    /// <summary>
    /// The quality preset row: its choice applies the preset.
    /// </summary>
    internal SettingsMenu.Entry PresetEntry => new("Quality preset", PresetHint, Choice: new(() => PresetNames, CurrentPreset, ApplyPreset));

    /// <summary>
    /// The quality tier: the one the settings sit at (<see cref="CurrentPreset"/>), so a slider moved off it makes it Custom.
    /// </summary>
    internal int Tier => TierOverride ?? CurrentPreset();

    /// <summary>
    /// The preset names a saved <see cref="UserConfig.GraphicsPreset"/> takes, the last one (Custom) applying nothing.
    /// </summary>
    internal static IReadOnlyList<string> PresetChoices => PresetNames;

    /// <summary>
    /// The default preset's name (High).
    /// </summary>
    internal static string DefaultPreset => PresetNames[HighPreset];

    /// <summary>
    /// The value of <paramref name="slider"/> at <paramref name="preset"/>: High's, else the factor (fixed or times the start default, the base by
    /// the mode switch's side), clamped; a Faithful shadow distance stays within the game's slider.
    /// </summary>
    internal float PresetTarget(PresetSlider p, Slider slider, int preset)
    {
        float start = 0;
        for (int i = 0; i < menu.Values.Sliders.Count; i++)
        {
            if (ReferenceEquals(menu.Values.Sliders[i], slider)) start = menu.Defaults[i];
        }

        if (p.Mode is { } mode && menu.Values.Toggles.FirstOrDefault(t => t.Label == mode) is { } toggle
            && (toggle.Get() ? slider.Meitou : slider.Faithful) is { } mark)
        {
            start = mark;
        }

        float factor = preset switch
        {
            LowPreset => p.Low,
            MediumPreset => p.Medium,
            UltraPreset => UltraOf(p),
            ExtremePreset => p.Extreme ?? p.Ultra,
            _ => float.NaN,
        };
        float value = preset == HighPreset ? p.High ?? start
            : float.IsNaN(factor) ? p.High ?? start
            : float.IsNegativeInfinity(factor) ? slider.Min
            : (p.Scale || p.Mode is not null) && !(preset == 0 && p.LowFixed) ? start * factor
            : factor;

        // A Faithful shadow distance stays within the game's slider.
        if (p.Mode == "Shadows" && !menu.Values.Toggles.Any(t => t.Label == "Shadows" && t.Get()) && value > slider.Min)
            value = Math.Min(value, Data.World.KenshiShadows.MaxRange);

        return Math.Clamp(value, slider.Min, slider.Max);
    }

    /// <summary>
    /// <paramref name="p"/>'s value at Ultra on this renderer (<see cref="PresetSlider.MetalUltra"/> on Metal).
    /// </summary>
    static float UltraOf(PresetSlider p) => MetalTiers && p.MetalUltra is { } metal ? metal : p.Ultra;

    /// <summary>
    /// The preset sliders with the menu's sliders of their labels.
    /// </summary>
    internal IEnumerable<(PresetSlider Preset, Slider Slider)> PresetPairs() =>
        PresetSliders
            .Select(p => (p, menu.Values.Sliders.FirstOrDefault(s => s.Label == p.Label)))
            .Where(x => x.Item2 is not null)!;

    /// <summary>
    /// The state of switch <paramref name="p"/> at <paramref name="preset"/> (High: <paramref name="start"/>).
    /// </summary>
    internal static bool SwitchTarget(PresetToggle p, bool start, int preset) => preset switch
    {
        LowPreset => p.Low,
        MediumPreset => p.Medium,
        UltraPreset => p.Ultra,
        ExtremePreset => p.Extreme ?? p.Ultra,
        _ => p.High ?? start,
    };

    /// <summary>
    /// <see cref="SwitchTarget"/> on this device: the GI's own switches (<see cref="GiSwitches"/>) are off without the GI, and a switch that needs
    /// the traced scene (<see cref="TracedSwitches"/>) is off while the indirect light is.
    /// </summary>
    internal bool SwitchTargetHere(PresetToggle p, bool start, int preset) =>
        SwitchTarget(p, start, preset)
        && (!GiSwitches.Contains(p.Label) || GiHere)
        && (!TracedSwitches.Contains(p.Label) || menu.Values.Toggles.Any(t => t.Label == "Indirect light" && t.Get()));

    /// <summary>
    /// Sets the tier switches that need the traced scene, once the look has set the indirect light (<see cref="LookPresets.SetFaithful"/>).
    /// </summary>
    internal void ApplyTracedSwitches(int preset)
    {
        foreach (var (p, toggle, start) in PresetSwitches().Where(x => TracedSwitches.Contains(x.Preset.Label)))
        {
            if (toggle.Get() != SwitchTargetHere(p, start, preset)) toggle.Set(SwitchTargetHere(p, start, preset));
        }
    }

    /// <summary>
    /// The tier every tier slider (and, with the Meitou look, every tier switch) sits at, else <see cref="CustomPreset"/>; the look switches do not
    /// count.
    /// </summary>
    internal int CurrentPreset()
    {
        bool faithful = menu.LookPresets.FaithfulLook;
        for (int preset = 0; preset < CustomPreset; preset++)
        {
            if (PresetPairs().All(x => SameSliderPlace(x.Preset, x.Slider, preset))
                && (faithful || PresetSwitches().All(x => x.Toggle.Get() == SwitchTargetHere(x.Preset, x.Default, preset))))
            {
                return preset;
            }
        }

        return CustomPreset;
    }

    /// <summary>
    /// The graphics keys a preset may set: a launcher that changes one of them no longer asks for a preset. The technique choices count, as each
    /// stands for tier switches and the launcher writes the choice alone.
    /// </summary>
    internal static IEnumerable<string> PresetKeys()
    {
        var switches = LowOffSwitches.Concat(LowOffToggles).Concat(AllPresetToggles.Select(p => p.Label));
        return PresetSliders.Select(p => p.Label)
            .Append(WorldFrame.ParticleSliders[0])
            .Concat(WorldFrame.UpscalerSliders.Take(2))
            .Concat(WorldFrame.ChoiceSliders)
            .Concat(switches.Select(label => SettingsMenu.SwitchKeyPrefix + label))
            .Distinct();
    }

    /// <summary>
    /// At start, after the saved sliders and switches: applies the saved preset when the settings do not sit at it, so a preset the launcher chose
    /// (docs/launcher.md "Graphics") takes effect; the launcher saves Custom once it changes a setting the preset sets.
    /// </summary>
    internal void ApplySavedPreset()
    {
        int preset = Array.IndexOf(PresetNames, menu.Config.GraphicsPreset);
        if (preset is >= 0 and < CustomPreset && preset != CurrentPreset())
        {
            ApplyPreset(preset);
            menu.Notice = "";
        }

        // At Low the look switches it holds off stay off, also where the launcher's look turned them on (unless kept on).
        foreach (var label in menu.LookPresets.LowCut())
        {
            if (menu.Values.Toggles.FirstOrDefault(t => t.Label == label && t.Get()) is { } toggle) toggle.Set(false);
        }

        LastTier = CurrentPreset();
        ApplyReflectionScale(LastTier);
    }

    /// <summary>
    /// The settings the start log line names besides the tier: the technique choices, then the anti-aliasing and render scale (Vulkan's, Metal's).
    /// </summary>
    static readonly string[] StartLineSliders =
    [
        .. WorldFrame.ChoiceSliders, WorldFrame.UpscalerSliders[0], WorldFrame.UpscalerSliders[1], SettingsMenu.MetalUseUpscalingSlider,
        SettingsMenu.MetalRenderScaleSlider,
    ];

    /// <summary>
    /// The start's log line of what the run draws with (docs/bench.md "Play benchmark"), once the saved settings, the preset and a benchmark tier
    /// are applied: the tier the settings sit at and the saved preset's name, the technique choices, the anti-aliasing and render scale, and
    /// whether the device has the GI. The quality preset leaves the anti-aliasing to the player.
    /// </summary>
    internal string StartLine()
    {
        var values = StartLineSliders
            .Select(label => menu.Values.Sliders.FirstOrDefault(s => s.Label == label))
            .OfType<Slider>()
            .Select(s => $"{s.Label} = {s.Get():0.##}");
        bool gi = GiHere && menu.Values.Toggles.Any(t => t.Label == "Indirect light");
        string saved = menu.Config.GraphicsPreset ?? "none";
        string tier = PresetNames[CurrentPreset()];

        return $"settings  preset {tier} (saved {saved}); {string.Join("; ", values)}; GI on the device: {(gi ? "yes" : "no")}";
    }

    /// <summary>
    /// <c>--benchmark-baseline</c>, at start instead of the saved settings: the Medium tier, then no GI, ground parallax or tessellation (Medium's
    /// rock smoothing too), no upscaler and SMAA at full size. Nothing is saved (GameHost leaves the saved graphics as they are).
    /// </summary>
    internal void ApplyBenchmarkBaseline()
    {
        ApplyPreset(MediumPreset);
        string[] off =
        [
            "Indirect light", "Indoor GI", "Hybrid GI", WorldFrame.GroundParallaxSwitch, WorldFrame.GroundDisplaceSwitch,
            WorldFrame.RockSmoothingSwitch, WorldFrame.OutlineRefineSwitch,
        ];
        foreach (var toggle in menu.Values.Toggles.Where(t => off.Contains(t.Label) && t.Get())) toggle.Set(false);

        if (menu.Values.Sliders.FirstOrDefault(s => s.Label == WorldFrame.UpscalerSliders[0]) is { } antiAliasing) antiAliasing.Set(2);   // SMAA
        if (menu.Values.Sliders.FirstOrDefault(s => s.Label == WorldFrame.UpscalerSliders[1]) is { } scale) scale.Set(1);

        menu.Notice = "";
        LastTier = CurrentPreset();
    }

    /// <summary>
    /// The launcher benchmark's tier (<c>--benchmark</c>, docs/bench.md "Launcher benchmark"), at start instead of the saved settings: the Meitou
    /// look, the tier, its traced switches, then the anti-aliasing: an upscaler (FSR, DLSS) becomes TAA and every tier but Low draws at render
    /// scale 1 (Low keeps its own anti-aliasing and scale). The indirect light is the tier's, as in the game (a tier without it has no ray queries
    /// on the device either, WorldOptions.ApplyBenchmarkTier). Nothing is saved.
    /// </summary>
    internal void ApplyBenchmarkTier(int tier)
    {
        menu.LookPresets.ApplyLook(LookPresets.MeitouLook);
        ApplyPreset(tier);
        ApplyTracedSwitches(tier);
        if (tier != LowPreset) SetBenchmarkAntiAliasing();
        menu.Notice = "";
        LastTier = CurrentPreset();
    }

    /// <summary>
    /// Whether tier <paramref name="tier"/> turns on a switch that needs ray queries (<see cref="GiSwitches"/>, <see cref="TracedSwitches"/>), from
    /// the tables alone.
    /// </summary>
    internal static bool NeedsRayQueries(int tier) =>
        PresetToggles.Any(p => (GiSwitches.Contains(p.Label) || TracedSwitches.Contains(p.Label)) && TableSwitch(p, tier) == true);

    /// <summary>
    /// The table's value of <paramref name="p"/> at <paramref name="tier"/> (High's own, else the tier's factor or fixed value); null where the
    /// tier keeps the start default. How it applies (fixed, a multiple, the mode's base) is <see cref="PresetTarget"/>'s.
    /// </summary>
    internal static float? TableValue(PresetSlider p, int tier)
    {
        float factor = tier switch
        {
            LowPreset => p.Low,
            MediumPreset => p.Medium,
            UltraPreset => UltraOf(p),
            ExtremePreset => p.Extreme ?? p.Ultra,
            _ => float.NaN,
        };
        return float.IsNaN(factor) ? p.High : factor;
    }

    /// <summary>
    /// The table's state of switch <paramref name="p"/> at <paramref name="tier"/>; null where the tier keeps the start default (High).
    /// </summary>
    internal static bool? TableSwitch(PresetToggle p, int tier) => tier == HighPreset && p.High is null ? null : SwitchTarget(p, false, tier);

    /// <summary>
    /// The benchmark's anti-aliasing past Low: an upscaler (FSR, DLSS) becomes TAA, at render scale 1.
    /// </summary>
    void SetBenchmarkAntiAliasing()
    {
        if (menu.Values.Sliders.FirstOrDefault(s => s.Label == WorldFrame.UpscalerSliders[0]) is { } antiAliasing && antiAliasing.Get() > 3)
            antiAliasing.Set(3);   // TAA

        if (menu.Values.Sliders.FirstOrDefault(s => s.Label == WorldFrame.UpscalerSliders[1]) is { } scale) scale.Set(1);
    }

    /// <summary>
    /// Once the settings leave Low (a slider moved), the look switches Low held off come back.
    /// </summary>
    internal void SyncTier()
    {
        int now = CurrentPreset();
        if (now == LastTier) return;

        bool leftLow = LastTier == LowPreset;
        LastTier = now;
        ApplyReflectionScale(now);
        if (leftLow && !menu.LookPresets.FaithfulLook) menu.LookPresets.ApplyLowCut();
    }

    /// <summary>
    /// Saves the preset the settings sit at (Custom when none), for the launcher to show.
    /// </summary>
    internal void SavePreset() => menu.Config.GraphicsPreset = PresetNames[CurrentPreset()];

    /// <summary>
    /// Applies tier <paramref name="tier"/> (the settings showcase).
    /// </summary>
    internal void ShowcaseTier(int tier) => ApplyPreset(tier);

    /// <summary>
    /// Whether the slider already sits at the preset's place (within half a percent of its range).
    /// </summary>
    bool SameSliderPlace(PresetSlider preset, Slider slider, int tier) =>
        MathF.Abs(slider.FractionOf(slider.Get()) - slider.FractionOf(PresetTarget(preset, slider, tier))) <= 0.005f;

    /// <summary>
    /// The preset switches with the menu's toggles of their labels and their start defaults.
    /// </summary>
    IEnumerable<(PresetToggle Preset, Toggle Toggle, bool Default)> PresetSwitches() =>
        PresetToggles
            .Select(p => (p, Index: menu.Values.Toggles.ToList().FindIndex(t => t.Label == p.Label)))
            .Where(x => x.Index >= 0)
            .Select(x => (x.p, menu.Values.Toggles[x.Index], menu.ToggleDefaults[x.Index]));


    /// <summary>
    /// Applies <paramref name="preset"/> (nothing for Custom) with the Low cut following it, and says so in the notice.
    /// </summary>
    void ApplyPreset(int preset)
    {
        if (preset is < 0 or >= CustomPreset) return;

        TierOverride = preset;
        try
        {
            ApplyTier(preset);
        }
        finally
        {
            TierOverride = null;
        }

        LastTier = CurrentPreset();
        menu.Notice = preset == LowPreset && menu.LookPresets.LowCut().Any()
            ? Loc.Tr("Low quality applied: {1} off until you keep them on.", menu.LookPresets.LowCutList())
            : Loc.Tr("{1} quality applied.", Loc.Tr(PresetNames[preset]));
    }

    /// <summary>
    /// Sets a tier's switches (Meitou look), Low's other cuts and render scale, High's render scale and the preset sliders.
    /// </summary>
    void ApplyTier(int preset)
    {
        if (preset == LowPreset && menu.Values.Sliders.FirstOrDefault(s => s.Label == WorldFrame.ParticleSliders[0]) is { } particles)
        {
            particles.Set(1);   // a quarter of the render size
        }

        // The switches first: they move the distances that sit at their mode's default, and the mode decides the distances' base.
        if (!menu.LookPresets.FaithfulLook)
        {
            foreach (var (p, toggle, start) in PresetSwitches())
            {
                if (toggle.Get() != SwitchTargetHere(p, start, preset)) toggle.Set(SwitchTargetHere(p, start, preset));
            }

            menu.LookPresets.ApplyLowCut();
            if (preset == LowPreset)
            {
                foreach (var label in LowOffToggles)
                {
                    if (menu.Values.Toggles.FirstOrDefault(t => t.Label == label && t.Get()) is { } relief) relief.Set(false);
                }
            }
        }

        if (preset == LowPreset)
        {
            // --low-end's switches outside the look (anti-aliasing), then no anti-aliasing at all (not even FXAA) at its render scale.
            var lowOff = menu.Values.Toggles
                .Where(t => t.Remaster && LowOffSwitches.Contains(t.Label) && !LookPresets.IsLook(t.Label, true) && t.Get());
            foreach (var toggle in lowOff) toggle.Set(false);

            if (menu.Values.Sliders.FirstOrDefault(s => s.Label == WorldFrame.UpscalerSliders[0]) is { } antiAliasing) antiAliasing.Set(0);
            if (menu.Values.Sliders.FirstOrDefault(s => s.Label == WorldFrame.UpscalerSliders[1]) is { } scale)
                scale.Set(WorldOptions.LowEndRenderScale);
        }

        if (preset == HighPreset) SetHighRenderScale();
        ApplyReflectionScale(preset);
        foreach (var (p, slider) in PresetPairs()) slider.Set(PresetTarget(p, slider, preset));
    }

    /// <summary>
    /// Sets the size of the planar reflection targets for the selected quality tier.
    /// </summary>
    void ApplyReflectionScale(int preset) => menu.Values.SetReflectionScale?.Invoke(preset switch
        {
            LowPreset or MediumPreset => 0.5f,
            HighPreset => 0.75f,
            _ => ReflectionPass.Scale,
        });

    /// <summary>
    /// High's render scale: Quality with an upscaler (TAA, FSR, DLSS), native without one (no plain scaling).
    /// </summary>
    void SetHighRenderScale()
    {
        var antiAliasing = menu.Values.Sliders.FirstOrDefault(s => s.Label == WorldFrame.UpscalerSliders[0]);
        if (menu.Values.Sliders.FirstOrDefault(s => s.Label == WorldFrame.UpscalerSliders[1]) is not { } scale) return;

        bool upscaler = antiAliasing is not null && antiAliasing.Get() >= 3;
        scale.Set(upscaler ? UpscaleOptions.DefaultScale : 1);
    }
}
