using Meitou.Rendering;

namespace Meitou.Game;

/// <summary>The Graphics page's quality tiers (docs/native-ui.md "Settings"); the look and its presets are in SettingsMenu.Looks.cs.</summary>
sealed partial class SettingsMenu
{
    static readonly string[] PresetNames = ["Low", "Medium", "High", "Ultra", "Custom"];
    const int LowPreset = 0, HighPreset = 2, CustomPreset = 4;

    /// <summary>
    /// A preset's slider: its value at Low, Medium and Ultra (High is the start default; NaN: the start default too), either fixed or a multiple of the start default.
    /// <see cref="Mode"/> names the switch whose side picks the base instead (the slider's Meitou or Faithful mark), so High reads High in both looks.
    /// <see cref="LowFixed"/>: Low's value is fixed even where the others are multiples. Low is <c>--low-end</c> (WorldOptions.ApplyLowEnd): a slider it leaves alone stays at High.
    /// </summary>
    sealed record PresetSlider(string Label, float Low, float Medium, float Ultra, bool Scale = false, string? Mode = null, float? High = null, bool LowFixed = false);

    // --low-end's large foliage range, as a multiple of the default; the medium and small ranges follow it.
    const float LowFoliageRange = WorldOptions.LowEndLargeRange / global::Meitou.Data.World.FoliageSizes.DefaultLargeRange;

    static readonly PresetSlider[] PresetSliders =
    [
        new(WorldFrame.ObjectDistanceSlider, WorldOptions.LowEndObjectDistance, 0.6f, 2, Mode: "Draw distances", LowFixed: true),
        // --low-end's landmarks follow its object distance, at the Meitou defaults' ratio; Faithful has none.
        new(WorldFrame.LandmarkDistanceSlider, WorldOptions.LowEndObjectDistance / Enhancements.MeitouObjectDistance, 0.6f, 1.5f, Mode: "Draw distances"),
        new(WorldFrame.DistantTownsSlider, float.NaN, 6, float.NaN),
        new(WorldFrame.ObjectLodSlider, WorldOptions.LowEndObjectLod, 0.6f, 2),
        new(WorldFrame.TerrainErrorSlider, float.NaN, 1.5f, 0.6f, Mode: "Draw distances"),
        new(WorldFrame.GroundReliefQualitySlider, 0, 1, 2, High: 2),
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

    /// <summary>A quality switch: on or off at Low, Medium and Ultra (High is its start default). Meitou features only: Faithful keeps them off whatever the tier.</summary>
    sealed record PresetToggle(string Label, bool Low, bool Medium, bool Ultra);

    // Costs (docs/render-terrain.md "Ground relief", RTX 4090): refinement +0.38 ms. Parallax and displacement are effects (Faithful / Meitou switches), not tiers.
    static readonly PresetToggle[] PresetToggles =
    [
        new(WorldFrame.FifthCascadeSwitch, false, false, true),
        new(WorldFrame.RockSmoothingSwitch, false, true, true),
        new(WorldFrame.OutlineRefineSwitch, false, false, true),
    ];

    /// <summary>
    /// The Meitou effects Low sets Faithful, as <c>--low-end</c> does. The look's ones (docs/native-ui.md "Settings") are held off without changing the preset,
    /// unless kept on (<see cref="UserConfig.KeepAtLow"/>).
    /// </summary>
    static readonly string[] LowOffSwitches = ["Ambient occlusion", "Light shafts", "Water", "Shadows", "Ground parallax", "Ground displacement (tessellation)"];
    /// <summary>The on/off effects Low turns off too (the tessellation's outline refinement and rocks; they are not Faithful / Meitou switches).</summary>
    static readonly string[] LowOffToggles = [WorldFrame.OutlineRefineSwitch, WorldFrame.RockSmoothingSwitch];
    const string FifthCascade = WorldFrame.FifthCascadeSwitch;

    /// <summary>The Faithful / Meitou switches whose reading stays put when set (Indirect light without ray queries); the all switch ignores them.</summary>
    readonly HashSet<string> stuckSwitches = [];

    internal const string PresetHint = "Distances, shadows and detail together. Low is the same as --low-end.";

    Entry PresetEntry => new("Quality preset", PresetHint, Choice: new(() => PresetNames, CurrentPreset, ApplyPreset));

    float PresetTarget(PresetSlider p, Slider slider, int preset)
    {
        float start = 0;
        for (int i = 0; i < values.Sliders.Count; i++) if (ReferenceEquals(values.Sliders[i], slider)) start = defaults[i];
        if (p.Mode is { } mode && values.Toggles.FirstOrDefault(t => t.Label == mode) is { } toggle && (toggle.Get() ? slider.Meitou : slider.Faithful) is { } mark)
            start = mark;
        float factor = preset switch { 0 => p.Low, 1 => p.Medium, 3 => p.Ultra, _ => float.NaN };
        float value = preset == HighPreset ? p.High ?? start
            : float.IsNaN(factor) ? p.High ?? start
            : float.IsNegativeInfinity(factor) ? slider.Min
            : (p.Scale || p.Mode is not null) && !(preset == 0 && p.LowFixed) ? start * factor : factor;
        // A Faithful shadow distance stays within the game's slider.
        if (p.Mode == "Shadows" && !values.Toggles.Any(t => t.Label == "Shadows" && t.Get()) && value > slider.Min)
            value = Math.Min(value, global::Meitou.Data.World.KenshiShadows.MaxRange);
        return Math.Clamp(value, slider.Min, slider.Max);
    }

    IEnumerable<(PresetSlider Preset, Slider Slider)> PresetPairs() =>
        PresetSliders.Select(p => (p, values.Sliders.FirstOrDefault(s => s.Label == p.Label))).Where(x => x.Item2 is not null)!;

    IEnumerable<(PresetToggle Preset, Toggle Toggle, bool Default)> PresetSwitches() =>
        PresetToggles.Select(p => (p, Index: values.Toggles.ToList().FindIndex(t => t.Label == p.Label))).Where(x => x.Index >= 0)
            .Select(x => (x.p, values.Toggles[x.Index], toggleDefaults[x.Index]));

    static bool SwitchTarget(PresetToggle p, bool start, int preset) => preset switch { 0 => p.Low, 1 => p.Medium, 3 => p.Ultra, _ => start };

    /// <summary>The tier every tier slider (and, with the Meitou look, every tier switch) sits at, else <see cref="CustomPreset"/>; the look switches do not count.</summary>
    bool CascadeFor(int preset) => preset switch { 0 or 1 => false, 3 => true, _ => toggleDefaults[values.Toggles.Select(t => t.Label).ToList().IndexOf(FifthCascade)] };

    int CurrentPreset()
    {
        bool faithful = FaithfulLook;
        var cascade = values.Toggles.FirstOrDefault(t => t.Label == FifthCascade);
        for (int preset = 0; preset < CustomPreset; preset++)
            if (PresetPairs().All(x => MathF.Abs(x.Slider.FractionOf(x.Slider.Get()) - x.Slider.FractionOf(PresetTarget(x.Preset, x.Slider, preset))) <= 0.005f)
                && (faithful || PresetSwitches().All(x => x.Toggle.Get() == SwitchTarget(x.Preset, x.Default, preset)))
                && (cascade is null || cascade.Get() == CascadeFor(preset)))
                return preset;
        return CustomPreset;
    }

    /// <summary>The prefix of a switch's key in <c>meitou.user.json</c>'s <c>graphics</c> (the Faithful / Meitou ones and the plain on/off ones alike).</summary>
    internal const string SwitchKeyPrefix = "Meitou: ";

    /// <summary>The preset names a saved <see cref="UserConfig.GraphicsPreset"/> takes, the last one (Custom) applying nothing.</summary>
    internal static IReadOnlyList<string> PresetChoices => PresetNames;
    internal static string DefaultPreset => PresetNames[HighPreset];

    /// <summary>The <c>graphics</c> keys a preset may set: a launcher that changes one of them no longer asks for a preset.</summary>
    internal static IEnumerable<string> PresetKeys() => PresetSliders.Select(p => p.Label).Append(WorldFrame.ParticleSliders[0]).Concat(WorldFrame.UpscalerSliders.Take(2))
        .Concat(LowOffSwitches.Concat(LowOffToggles).Concat(PresetToggles.Select(p => p.Label)).Select(label => SwitchKeyPrefix + label)).Distinct();

    /// <summary>
    /// At start, after the saved sliders and switches: applies the saved preset when the settings do not sit at it, so a preset the launcher
    /// chose (docs/launcher.md "Graphics") takes effect; the launcher saves Custom once it changes a setting the preset sets.
    /// </summary>
    internal void ApplySavedPreset()
    {
        int preset = Array.IndexOf(PresetNames, config.GraphicsPreset);
        if (preset is >= 0 and < CustomPreset && preset != CurrentPreset())
        {
            ApplyPreset(preset);
            notice = "";
        }
        // At Low the look switches it holds off stay off, also where the launcher's look turned them on (unless kept on).
        foreach (var label in LowCut())
            if (values.Toggles.FirstOrDefault(t => t.Label == label && t.Get()) is { } toggle) toggle.Set(false);
        lastTier = CurrentPreset();
    }

    /// <summary>The tier being applied (<see cref="ApplyPreset"/>, <see cref="SetFaithful"/>), so the Low cut follows it before the sliders reach it.</summary>
    int? tierOverride;
    /// <summary>The tier the settings sat at when last looked (<see cref="SyncTier"/>); -1 not yet.</summary>
    int lastTier = -1;

    /// <summary>The quality tier: the one the settings sit at (<see cref="CurrentPreset"/>), so a slider moved off it makes it Custom.</summary>
    int Tier => tierOverride ?? CurrentPreset();

    /// <summary>Once the settings leave Low (a slider moved), the look switches Low held off come back.</summary>
    void SyncTier()
    {
        int now = CurrentPreset();
        if (now == lastTier) return;
        bool leftLow = lastTier == LowPreset;
        lastTier = now;
        if (leftLow && !FaithfulLook) ApplyLowCut();
    }

    /// <summary>Saves the preset the settings sit at (Custom when none), for the launcher to show.</summary>
    internal void SavePreset() => config.GraphicsPreset = PresetNames[CurrentPreset()];

    void ApplyPreset(int preset)
    {
        if (preset is < 0 or >= CustomPreset) return;
        tierOverride = preset;
        try { ApplyTier(preset); }
        finally { tierOverride = null; }
        lastTier = CurrentPreset();
        notice = preset == LowPreset && LowCut().Any() ? $"Low quality applied: {string.Join(", ", LowCut())} off until you keep them on." : $"{PresetNames[preset]} quality applied.";
    }

    void ApplyTier(int preset)
    {
        if (preset == LowPreset && values.Sliders.FirstOrDefault(s => s.Label == WorldFrame.ParticleSliders[0]) is { } particles) particles.Set(1);   // a quarter of the render size
        // The switches first: they move the distances that sit at their mode's default, and the mode decides the distances' base.
        if (!FaithfulLook)
        {
            foreach (var (p, toggle, start) in PresetSwitches())
                if (toggle.Get() != SwitchTarget(p, start, preset)) toggle.Set(SwitchTarget(p, start, preset));
            ApplyLowCut();
            if (preset == LowPreset)
                foreach (var label in LowOffToggles)
                    if (values.Toggles.FirstOrDefault(t => t.Label == label && t.Get()) is { } relief) relief.Set(false);
        }
        if (preset == LowPreset)
        {
            // --low-end's switches outside the look (anti-aliasing), then no anti-aliasing at all (not even FXAA) at its render scale.
            foreach (var toggle in values.Toggles.Where(t => t.Remaster && LowOffSwitches.Contains(t.Label) && !IsLook(t.Label, true) && t.Get())) toggle.Set(false);
            if (values.Sliders.FirstOrDefault(s => s.Label == WorldFrame.UpscalerSliders[0]) is { } antiAliasing) antiAliasing.Set(0);
            if (values.Sliders.FirstOrDefault(s => s.Label == WorldFrame.UpscalerSliders[1]) is { } scale) scale.Set(WorldOptions.LowEndRenderScale);
        }
        foreach (var (p, slider) in PresetPairs()) slider.Set(PresetTarget(p, slider, preset));
        if (values.Toggles.FirstOrDefault(t => t.Label == FifthCascade) is { } cascade && cascade.Get() != CascadeFor(preset)) cascade.Set(CascadeFor(preset));
    }

    /// <summary>
    /// A page's all-Faithful / all-Meitou switch over its Faithful / Meitou <paramref name="toggles"/> and the <paramref name="sliders"/> with both marks;
    /// it reads 1 when all are Meitou, 0 when all are Faithful, -1 when they are mixed.
    /// </summary>
    Entry SidesEntry(string label, string hint, string what, IReadOnlyList<Toggle> toggles, IReadOnlyList<Slider> sliders) => new(label, hint, Segmented: true,
        Choice: new(() => ["Faithful", "Meitou"], () =>
        {
            var live = toggles.Where(t => !stuckSwitches.Contains(t.Label)).ToList();
            static bool AtMark(Slider s, float? mark) => mark is { } m && MathF.Abs(s.Get() - m) <= 0.002f * (s.Max - s.Min);
            return live.All(t => t.Get()) && sliders.All(s => AtMark(s, s.Meitou)) ? 1 : live.All(t => !t.Get()) && sliders.All(s => AtMark(s, s.Faithful)) ? 0 : -1;
        }, side =>
        {
            bool meitou = side == 1;
            foreach (var toggle in toggles.Where(t => t.Get() != meitou))
            {
                toggle.Set(meitou);
                if (toggle.Get() != meitou) stuckSwitches.Add(toggle.Label);
            }
            foreach (var slider in sliders) slider.Set((meitou ? slider.Meitou : slider.Faithful)!.Value);
            notice = meitou ? $"Every {what} is Meitou." : $"Every {what} is as the game ships it.";
        }));

    /// <summary>The Advanced or Fixes page's all switch, over that page's Faithful / Meitou switches (not the plain on/off ones such as luckyren Mode) and sliders with both marks.</summary>
    Entry PageSidesEntry(List<Entry> entries, string label, string hint, string what) => SidesEntry(label, hint, what,
        entries.Select(e => e.Toggle).OfType<Toggle>().Where(t => t.Remaster).ToList(),
        entries.Select(e => e.Slider).OfType<Slider>().Where(s => s.Faithful is not null && s.Meitou is not null).ToList());
}
