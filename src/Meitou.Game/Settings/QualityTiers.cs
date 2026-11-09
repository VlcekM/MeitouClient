using Meitou.Game.Host;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Post;

namespace Meitou.Game.Settings;

/// <summary>
/// The Graphics page's quality tiers (docs/native-ui.md "Settings"); the look and its presets are <see cref="LookPresets"/>.
/// </summary>
sealed class QualityTiers(SettingsMenu menu)
{
    internal static readonly string[] PresetNames = ["Low", "Medium", "High", "Ultra", "Custom"];
    internal const int LowPreset = 0, MediumPreset = 1, HighPreset = 2, CustomPreset = 4;

    /// <summary>
    /// A preset's slider: its value at Low, Medium and Ultra (High is the start default; NaN: the start default too), either fixed or a multiple of
    /// the start default. <see cref="Mode"/> names the switch whose side picks the base instead (the slider's Meitou or Faithful mark), so High reads
    /// High in both looks. <see cref="LowFixed"/>: Low's value is fixed even where the others are multiples. Low is <c>--low-end</c>
    /// (WorldOptions.ApplyLowEnd): a slider it leaves alone stays at High.
    /// </summary>
    internal sealed record PresetSlider(string Label, float Low, float Medium, float Ultra, bool Scale = false, string? Mode = null, float? High = null,
        bool LowFixed = false);

    // --low-end's large foliage range, as a multiple of the default; the medium and small ranges follow it.
    const float LowFoliageRange = WorldOptions.LowEndLargeRange / Data.World.FoliageSizes.DefaultLargeRange;

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

        // Low's shadow distance is the slider's left end: shadows off.
        new(WorldFrame.ShadowDistanceSlider, float.NegativeInfinity, 0.6f, 1.5f, Mode: "Shadows"),
        new(WorldFrame.ShadowMapSlider, float.NaN, 1, 2),
        new(WorldFrame.ShadowFilterSlider, 0, 1, 2),
        new(WorldFrame.ShaftQualitySlider, 0, 1, 2, High: 2),
        new(WorldFrame.ParticleSliders[1], WorldOptions.LowEndParticleDensity, 0.5f, float.NaN),
        new(WorldFrame.FilteringSlider, 0, 2, 4, High: 4),

        // The start default is the saved level (applied before the menu exists), so High names the full size.
        new(WorldFrame.TextureQualitySlider, WorldOptions.LowEndTextureQuality, 1, 0, High: 0),
        new(WorldFrame.GroundDisplaceDistanceSlider, 0.5f, 0.75f, 1.5f, Scale: true),
        new(WorldFrame.GroundParallaxDistanceSlider, 0.5f, 0.75f, 1.5f, Scale: true),
    ];

    /// <summary>
    /// A quality switch: on or off at Low, Medium and Ultra (High is its start default). Meitou features only: Faithful keeps them off whatever the
    /// tier.
    /// </summary>
    internal sealed record PresetToggle(string Label, bool Low, bool Medium, bool Ultra);

    // Costs (docs/render-terrain.md "Ground relief", RTX 4090): refinement +0.38 ms. Parallax and displacement are effects (Faithful / Meitou
    // switches), not tiers.
    internal static readonly PresetToggle[] PresetToggles =
    [
        new(WorldFrame.FifthCascadeSwitch, false, false, true),
        new(WorldFrame.RockSmoothingSwitch, false, false, true),
        new(WorldFrame.OutlineRefineSwitch, false, false, true),
    ];

    /// <summary>
    /// The Meitou effects Low sets Faithful, as <c>--low-end</c> does. The look's ones (docs/native-ui.md "Settings") are held off without changing
    /// the preset, unless kept on (<see cref="UserConfig.KeepAtLow"/>).
    /// </summary>
    internal static readonly string[] LowOffSwitches =
        ["Ambient occlusion", "Light shafts", "Water", "Shadows", "Ground parallax", "Ground displacement (tessellation)"];

    /// <summary>
    /// The on/off effects Low turns off too (the tessellation's outline refinement and rocks; they are not Faithful / Meitou switches).
    /// </summary>
    static readonly string[] LowOffToggles = [WorldFrame.OutlineRefineSwitch, WorldFrame.RockSmoothingSwitch];

    const string FifthCascade = WorldFrame.FifthCascadeSwitch;
    internal const string PresetHint = "Distances, shadows and detail together. Low is the same as --low-end.";

    /// <summary>
    /// The tier being applied (<see cref="ApplyPreset"/>, <see cref="LookPresets.SetFaithful"/>), so the Low cut follows it before the sliders reach
    /// it.
    /// </summary>
    internal int? TierOverride;

    /// <summary>
    /// The tier the settings sat at when last looked (<see cref="SyncTier"/>); -1 not yet.
    /// </summary>
    internal int LastTier = -1;

    internal SettingsMenu.Entry PresetEntry => new("Quality preset", PresetHint, Choice: new(() => PresetNames, CurrentPreset, ApplyPreset));

    /// <summary>
    /// The quality tier: the one the settings sit at (<see cref="CurrentPreset"/>), so a slider moved off it makes it Custom.
    /// </summary>
    internal int Tier => TierOverride ?? CurrentPreset();

    /// <summary>
    /// The preset names a saved <see cref="UserConfig.GraphicsPreset"/> takes, the last one (Custom) applying nothing.
    /// </summary>
    internal static IReadOnlyList<string> PresetChoices => PresetNames;

    internal static string DefaultPreset => PresetNames[HighPreset];

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

        float factor = preset switch { 0 => p.Low, 1 => p.Medium, 3 => p.Ultra, _ => float.NaN };
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

    internal IEnumerable<(PresetSlider Preset, Slider Slider)> PresetPairs() =>
        PresetSliders
            .Select(p => (p, menu.Values.Sliders.FirstOrDefault(s => s.Label == p.Label)))
            .Where(x => x.Item2 is not null)!;

    internal static bool SwitchTarget(PresetToggle p, bool start, int preset) => preset switch { 0 => p.Low, 1 => p.Medium, 3 => p.Ultra, _ => start };

    internal int CurrentPreset()
    {
        bool faithful = menu.LookPresets.FaithfulLook;
        var cascade = menu.Values.Toggles.FirstOrDefault(t => t.Label == FifthCascade);
        for (int preset = 0; preset < CustomPreset; preset++)
        {
            if (PresetPairs().All(x => SameSliderPlace(x.Preset, x.Slider, preset))
                && (faithful || PresetSwitches().All(x => x.Toggle.Get() == SwitchTarget(x.Preset, x.Default, preset)))
                && (cascade is null || cascade.Get() == CascadeFor(preset)))
            {
                return preset;
            }
        }

        return CustomPreset;
    }

    /// <summary>
    /// The graphics keys a preset may set: a launcher that changes one of them no longer asks for a preset.
    /// </summary>
    internal static IEnumerable<string> PresetKeys()
    {
        var switches = LowOffSwitches.Concat(LowOffToggles).Concat(PresetToggles.Select(p => p.Label));
        return PresetSliders.Select(p => p.Label)
            .Append(WorldFrame.ParticleSliders[0])
            .Concat(WorldFrame.UpscalerSliders.Take(2))
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
            "Indirect light", "Indoor GI", WorldFrame.GroundParallaxSwitch, WorldFrame.GroundDisplaceSwitch, WorldFrame.RockSmoothingSwitch,
            WorldFrame.OutlineRefineSwitch,
        ];
        foreach (var toggle in menu.Values.Toggles.Where(t => off.Contains(t.Label) && t.Get())) toggle.Set(false);

        if (menu.Values.Sliders.FirstOrDefault(s => s.Label == WorldFrame.UpscalerSliders[0]) is { } antiAliasing) antiAliasing.Set(2);   // SMAA
        if (menu.Values.Sliders.FirstOrDefault(s => s.Label == WorldFrame.UpscalerSliders[1]) is { } scale) scale.Set(1);

        menu.Notice = "";
        LastTier = CurrentPreset();
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
        if (leftLow && !menu.LookPresets.FaithfulLook) menu.LookPresets.ApplyLowCut();
    }

    /// <summary>
    /// Saves the preset the settings sit at (Custom when none), for the launcher to show.
    /// </summary>
    internal void SavePreset() => menu.Config.GraphicsPreset = PresetNames[CurrentPreset()];

    internal void ShowcaseTier(int tier) => ApplyPreset(tier);

    /// <summary>
    /// Whether the slider already sits at the preset's place (within half a percent of its range).
    /// </summary>
    bool SameSliderPlace(PresetSlider preset, Slider slider, int tier) =>
        MathF.Abs(slider.FractionOf(slider.Get()) - slider.FractionOf(PresetTarget(preset, slider, tier))) <= 0.005f;

    IEnumerable<(PresetToggle Preset, Toggle Toggle, bool Default)> PresetSwitches() =>
        PresetToggles
            .Select(p => (p, Index: menu.Values.Toggles.ToList().FindIndex(t => t.Label == p.Label)))
            .Where(x => x.Index >= 0)
            .Select(x => (x.p, menu.Values.Toggles[x.Index], menu.ToggleDefaults[x.Index]));

    /// <summary>
    /// The tier every tier slider (and, with the Meitou look, every tier switch) sits at, else <see cref="CustomPreset"/>; the look switches do not
    /// count.
    /// </summary>
    bool CascadeFor(int preset) => preset switch
    {
        0 or 1 => false,
        3 => true,
        _ => menu.ToggleDefaults[menu.Values.Toggles.Select(t => t.Label).ToList().IndexOf(FifthCascade)],
    };

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
                if (toggle.Get() != SwitchTarget(p, start, preset)) toggle.Set(SwitchTarget(p, start, preset));
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
        foreach (var (p, slider) in PresetPairs()) slider.Set(PresetTarget(p, slider, preset));
        if (menu.Values.Toggles.FirstOrDefault(t => t.Label == FifthCascade) is { } cascade && cascade.Get() != CascadeFor(preset))
            cascade.Set(CascadeFor(preset));
    }

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
