using Meitou.Game.Host;
using Meitou.Game.Rendering.Frame;

namespace Meitou.Game.Settings;

/// <summary>
/// The look and its presets on the Graphics page (docs/native-ui.md "Settings"). The look tabs (Light, Sky, Colour, Water, Skin) belong to the look:
/// Faithful (the game exactly as it ships, nothing to set) or a Meitou preset (Meitou plus the settings the preset changes). The quality tabs
/// (Distances, Detail) belong to the quality tier. Presets are JSON files in a <c>presets</c> folder beside <c>meitou.user.json</c>; a share code is
/// <c>MT1:</c> and the deflated JSON in base64.
/// </summary>
sealed class LookPresets(SettingsMenu menu)
{
    #region Look data

    /// <summary>
    /// The roles <see cref="Role"/> gives the look's settings (the rest are quality settings, which the tier sets).
    /// </summary>
    static readonly string[] LookRoles = ["Light", "Sky", "Colour", "Water", "Skin"];

    /// <summary>
    /// Switches that save memory or time and leave the picture as it is (welded tangents, billboards past the impostor distance, generated levels):
    /// the look leaves them alone, so Faithful costs no memory for nothing; they are quality settings the player sets.
    /// </summary>
    internal static readonly string[] PerformanceSwitches = ["Mesh welding", "Far impostors", "Foliage levels", "Character levels"];

    /// <summary>
    /// A look preset: what it sets, by the keys the settings are saved under (a slider's label, "Meitou: " and a switch's label); everything else is
    /// Meitou's.
    /// </summary>
    internal sealed record LookPreset(string Name, string Source, string Made, Dictionary<string, float> Values, string? File = null);

    /// <summary>
    /// The built-in Meitou look: it sets nothing.
    /// </summary>
    internal static readonly LookPreset MeitouLook = new("Meitou", "built in", MeitouVersion, []);

    /// <summary>
    /// Example looks, each far from the others (docs/native-ui.md "Settings"); everything they do not set is Meitou's.
    /// </summary>
    internal static readonly LookPreset[] ExampleLooks =
    [
        new("Hot and dry", "example", MeitouVersion, new()
        {
            [SettingsMenu.SwitchKey(WorldFrame.BloomSwitch)] = 1,
            [WorldFrame.BloomStrengthSlider] = 0.13f,
            [WorldFrame.HeatHazeStrengthSlider] = 0.8f,
        }),
        new("Vanilla colours", "example", MeitouVersion, new()
        {
            [SettingsMenu.SwitchKey("Tone map and grade")] = 0,
            [SettingsMenu.SwitchKey("Night grading")] = 0,
            [SettingsMenu.SwitchKey("Dithering")] = 0,
        }),
    ];

    /// <summary>
    /// The folder of the player's look presets, beside <c>meitou.user.json</c>.
    /// </summary>
    internal const string LookFolder = "presets";

    /// <summary>
    /// The look choice's hint.
    /// </summary>
    internal const string LookHint = "The game as it ships, or Meitou with a look preset.";

    /// <summary>
    /// The Faithful / Meitou switches whose reading stays put when set (Indirect light without ray queries); the all switch ignores them.
    /// </summary>
    internal readonly HashSet<string> StuckSwitches = [];

    /// <summary>
    /// The looks: Meitou, the examples and the player's presets; null until listed.
    /// </summary>
    List<LookPreset>? _looks;

    /// <summary>
    /// The look chosen this session; null for the saved one.
    /// </summary>
    internal LookPreset? ChosenLook;

    /// <summary>
    /// The look and quality choices, made on first use.
    /// </summary>
    SettingsMenu.Choice? _lookChoice, _qualityChoice;

    #endregion

    #region Choices and paths

    /// <summary>
    /// This build's release (its git tag without the <c>v</c>, as the settings menu shows it), written into the presets it makes.
    /// </summary>
    internal static string MeitouVersion => BuildVersion.Tag.TrimStart('v');

    /// <summary>
    /// The look choice: the looks by name (with their source but the built-in ones); choosing one applies it.
    /// </summary>
    internal SettingsMenu.Choice LookChoice => _lookChoice ??= new(
        () => Looks().Select(l => l.Source == "built in" ? l.Name : $"{l.Name}  ·  {l.Source}").ToList(),
        () => Looks().IndexOf(ActiveLook),
        i =>
        {
            var look = Looks()[i];
            ApplyLook(look);
            menu.Notice = $"{look.Name} loaded.";
        });

    /// <summary>
    /// The quality choice (<see cref="QualityTiers.PresetEntry"/>'s).
    /// </summary>
    internal SettingsMenu.Choice QualityChoice => _qualityChoice ??= menu.Tiers.PresetEntry.Choice!;

    /// <summary>
    /// The presets folder beside the user config.
    /// </summary>
    string PresetFolder => Path.Combine(Path.GetDirectoryName(menu.Config.Path) is { Length: > 0 } dir ? dir : ".", LookFolder);

    /// <summary>
    /// The built-in looks, Meitou first (saved as no <see cref="UserConfig.LookPreset"/>), with what each sets; the player's are in <see
    /// cref="LookFolder"/>.
    /// </summary>
    internal static IReadOnlyList<(string Name, IReadOnlyDictionary<string, float> Values)> BuiltInLooks =>
        [.. new[] { MeitouLook }.Concat(ExampleLooks).Select(l => (l.Name, (IReadOnlyDictionary<string, float>)l.Values))];

    #endregion

    #region Setting keys

    /// <summary>
    /// The <c>graphics</c> keys a look sets: every Faithful / Meitou graphics switch (Faithful turns them all off; the fifth cascade and the
    /// <see cref="PerformanceSwitches"/> are quality switches) and the look tabs' sliders.
    /// </summary>
    internal static IEnumerable<string> LookKeys(IEnumerable<string> switchLabels, IEnumerable<string> sliderLabels)
    {
        var switchKeys = switchLabels
            .Where(label => label != WorldFrame.FifthCascadeSwitch && !PerformanceSwitches.Contains(label))
            .Where(label => SettingsMenu.GraphicsPages.Contains(SettingsMenu.GraphicsSection(label, true)))
            .Select(SettingsMenu.SwitchKey);
        var sliderKeys = sliderLabels.Where(label => SettingsMenu.Section(label) is not ("Display" or "Advanced") && IsLook(label, false));
        return switchKeys.Concat(sliderKeys);
    }

    /// <summary>
    /// The quality tiers' switches (their keys): the tier sets them under a Meitou look, so a look leaves them alone (Faithful turns them off).
    /// </summary>
    internal static IEnumerable<string> TierSwitchKeys => QualityTiers.PresetToggles.Select(p => SettingsMenu.SwitchKey(p.Label));

    /// <summary>
    /// A switch the menus leave out, which another setting already covers: anti-aliasing (the Display page's method) and weather particles (their
    /// size). Its saved state is not read back, so it cannot undo that setting.
    /// </summary>
    internal static bool HiddenSwitch(string label) => SettingsMenu.GraphicsSection(label, true) == "Hidden";

    /// <summary>
    /// A slider under a feature's switch (a strength): the switch is its Faithful side, so Faithful leaves the slider as it is.
    /// </summary>
    internal static bool UnderSwitch(string label) => FeatureGrouping.FeatureGroups.Any(g => g.Header is not null && g.Members.Contains(label));

    /// <summary>
    /// The game's value a slider's label names ("Tone map (0 = game)"), if any.
    /// </summary>
    internal static float? LabelGameValue(string label) =>
        System.Text.RegularExpressions.Regex.Match(label, @"\((-?\d+(?:\.\d+)?) = game") is { Success: true } m
            ? float.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)
            : null;

    #endregion

    #region Preset files

    /// <summary>
    /// The looks, listed once: Meitou, the examples and the player's presets (an unreadable folder adds none).
    /// </summary>
    List<LookPreset> Looks()
    {
        if (_looks is not null) return _looks;

        _looks = [MeitouLook, .. ExampleLooks];
        try
        {
            AddDiskLooks();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }

        return _looks;
    }

    /// <summary>
    /// Adds the presets the player saved in the presets folder, by file name.
    /// </summary>
    void AddDiskLooks()
    {
        if (menu.NoDiskWrites || !Directory.Exists(PresetFolder)) return;

        foreach (var file in Directory.GetFiles(PresetFolder, "*.json").Order(StringComparer.OrdinalIgnoreCase))
        {
            if (ReadLook(File.ReadAllText(file), "mine") is { } look) _looks!.Add(look with { File = file });
        }
    }

    /// <summary>
    /// The active look: the chosen one, else the saved one by name, else Meitou.
    /// </summary>
    internal LookPreset ActiveLook => ChosenLook ??= Looks().FirstOrDefault(l => l.Name == menu.Config.LookPreset) ?? MeitouLook;

    /// <summary>
    /// The preset files and share codes are LookPresetFile's (Meitou.Core), the launcher's too. A look read from preset JSON, marked as from
    /// <paramref name="source"/>; null when it is not one.
    /// </summary>
    static LookPreset? ReadLook(string json, string source) =>
        global::Meitou.Settings.LookPresetFile.Read(json) is { } read ? new(read.Name, source, read.Made, read.Values) : null;

    /// <summary>
    /// The preset file data of <paramref name="look"/>.
    /// </summary>
    static global::Meitou.Settings.LookPresetData Data(LookPreset look) => new(look.Name, look.Made, look.Values);

    /// <summary>
    /// <paramref name="look"/> as preset file JSON.
    /// </summary>
    static string WriteLook(LookPreset look) => global::Meitou.Settings.LookPresetFile.Write(Data(look));

    /// <summary>
    /// <paramref name="look"/>'s share code.
    /// </summary>
    internal static string ShareCode(LookPreset look) => global::Meitou.Settings.LookPresetFile.ShareCode(Data(look));

    #endregion

    #region Roles

    /// <summary>
    /// Whether a setting is part of the look or of the quality: the look's (<see cref="LookRoles"/>) are the effects and their strengths, the
    /// quality's the distances and the detail. As the Graphics tabs were split before they were grouped by feature, so the looks and the tiers set
    /// what they set before.
    /// </summary>
    static string Role(string label, bool toggle) => toggle ? ToggleRole(label) : SliderRole(label);

    /// <summary>
    /// A look switch's role (its group) by its label.
    /// </summary>
    static string ToggleRole(string label) => label switch
    {
        "Interior lamps" => "Advanced",
        "Anti-aliasing" or "Weather particles" => "Hidden",
        "Shadows" or "Indirect light" or "Indoor GI" or "Ambient occlusion" or "Light shafts" or "Planetshine" or "Lamp shadows" => "Light",
        "Haze" or "Night air" or "Night sky" or "Clouds" or "Cloud shadows" or "Cloud shadows in fog" or "Heat haze" or "Fog" => "Sky",
        "Tone map and grade" or "Night grading" or "Dithering" or WorldFrame.BloomSwitch
            or WorldFrame.DepthOfFieldSwitch or WorldFrame.CameraEffectsSwitch => "Colour",
        "Water" => "Water",
        "Skin" => "Skin",
        "Draw distances" or "Foliage ranges" or "Far impostors" => "Distances",
        _ => "Detail",
    };

    /// <summary>
    /// A look slider's role (its group) by its label prefix.
    /// </summary>
    static string SliderRole(string label)
    {
        // The technique choices stand for switches the looks and tiers already set: not a look value of their own.
        if (WorldFrame.ChoiceSliders.Contains(label)) return "Choice";

        bool S(string prefix) => label.StartsWith(prefix, StringComparison.Ordinal);
        if (S("Ambient") || S("Night ambient") || S("Light shafts") || S("Planetshine")) return "Light";

        if (S("Haze") || S("Heat haze")) return "Sky";

        if (S("Tone map") || S("Hybrid") || S("Grading") || S("Night grading") || S("Bloom") || S("Depth of field")) return "Colour";

        if (label.Contains("clarity", StringComparison.Ordinal) || label.Contains("see-through", StringComparison.Ordinal))
            return "Water";

        return IsDistanceSlider(label) ? "Distances" : "Detail";
    }

    /// <summary>
    /// Whether a slider sets a draw or detail distance.
    /// </summary>
    static bool IsDistanceSlider(string label)
    {
        bool S(string prefix) => label.StartsWith(prefix, StringComparison.Ordinal);
        return label == WorldFrame.ObjectDistanceSlider || label == WorldFrame.ShadowDistanceSlider || S("Landmark") || S("Distant towns")
            || S("Object LOD") || S("Foliage") || S("Large ") || S("Medium foliage") || S("Small foliage") || S("Impostor") || S("Grass draw")
            || S("Terrain detail") || S("Reflection range");
    }

    /// <summary>
    /// Whether a switch or slider is part of the look (presets set it); a quality tier's slider on a look tab (light shafts quality) is not.
    /// </summary>
    internal static bool IsLook(string label, bool toggle) => SettingsMenu.GraphicsSection(label, toggle) != "Hidden"
        && LookRoles.Contains(Role(label, toggle))
        && (toggle || !QualityTiers.PresetSliders.Any(p => p.Label == label));

    /// <summary>
    /// A Faithful / Meitou switch of the Graphics page that Faithful turns off (not the gameplay one, Interior lamps, nor the <see
    /// cref="PerformanceSwitches"/>).
    /// </summary>
    static bool IsGraphicsSwitch(Toggle t) =>
        t.Remaster && !PerformanceSwitches.Contains(t.Label) && SettingsMenu.GraphicsPages.Contains(SettingsMenu.GraphicsSection(t.Label, true));

    /// <summary>
    /// How much a setting costs, as a level a player can use on any machine: ranked from the measured costs in the docs (RTX 4090, 1440p;
    /// docs/render-*.md), High about 1 ms or more or a large share of the scene, Medium a few tenths, Low under a tenth or only in some views.
    /// "Faster with Meitou": a switch whose Meitou side saves work.
    /// </summary>
    internal static string? ImpactOf(string label) => label switch
    {
        "Indirect light" or "Hybrid GI" or "Clouds" or "Shadows" or WorldFrame.TracedGrassSwitch or WorldFrame.ObjectDistanceSlider
            or "Foliage draw distance x" or "Grass draw distance x" or WorldFrame.ShadowDistanceSlider or "Large foliage range (F6 Meitou)"
            or WorldFrame.WaterReflectionSlider => "High impact",
        "Ambient occlusion" or "Ground-truth occlusion" or WorldFrame.GtaoQualitySlider or "Lamp shadows" or "Light shafts" or "Water"
            or "Ground parallax" or "Ground displacement (tessellation)" or "Ground displacement distance (m)" or "Ground parallax distance (m)"
            or WorldFrame.ShadowMapSlider
            or "Shadow filter 0-2 (Meitou; 0 low-end)" or "Shadow cascade 5 (Meitou)" or "Landmark distance (Meitou)" or "Distant towns (zones)"
            or "Object LOD distance x" or "Foliage LOD distance x (lod Meitou)" or "Medium foliage range" or "Terrain detail: error px (less = finer)"
            or WorldFrame.GrassDensitySlider or "Impostor distance (F7 Meitou)" or "Large impostor distance (F7 Meitou)"
            or "Reflection range x (game 0.6)" or "Anti-aliasing" or WorldFrame.ShaftQualitySlider or "Ground trails"
            or WorldFrame.DepthOfFieldSwitch => "Medium impact",
        "Cloud shadows" or "Indoor GI" or "Planetshine" or "Night air" or "Night sky" or "Skin" or "Rock smoothing (tessellation)"
            or "Cloud shadows in fog" or "Terrain outline refinement (tessellation)" or "Small foliage range" or "Bloom strength (0 = game)"
            or WorldFrame.BloomSwitch or "Heat haze" or "Blood cells" or "Blood on surfaces" or "Bloody footprints" or WorldFrame.BloodDrawsSlider or "Item icons" or "Fog" or "Bolt tracers"
            or "Water contact foam, wakes and ripples" or "Light shafts air (0 = none)" => "Low impact",
        "Far impostors" or "Foliage levels" or "Character levels" or "Weather particles" or "Mesh welding" => "Faster with Meitou",
        _ when label == WorldFrame.ParticleSliders[0] || label == WorldFrame.ParticleSliders[1] => "Medium impact",
        _ when IsLook(label, false) || IsLook(label, true) => "No impact",
        _ => null,
    };

    #endregion

    #region Look toggles and sliders

    /// <summary>
    /// The look switches (Meitou features with a look key) with their keys and Meitou defaults.
    /// </summary>
    IEnumerable<(string Key, Toggle Toggle, bool Default)> LookToggles() =>
        menu.Values.Toggles.Select((t, i) => (SettingsMenu.SwitchKey(t.Label), t, menu.ToggleDefaults[i]))
            .Where(x => x.t.Remaster && IsLook(x.t.Label, true));

    /// <summary>
    /// The look sliders (outside Display and Advanced) with their keys and Meitou defaults.
    /// </summary>
    IEnumerable<(string Key, Slider Slider, float Default)> LookSliders() =>
        menu.Values.Sliders
            .Select((s, i) => (s.Label, s, menu.Defaults[i]))
            .Where(x => SettingsMenu.Section(x.s.Label) is not ("Display" or "Advanced") && IsLook(x.s.Label, false));

    /// <summary>
    /// <paramref name="key"/>'s value in <paramref name="look"/>, else <paramref name="meitou"/>.
    /// </summary>
    static float LookValue(LookPreset look, string key, float meitou) => look.Values.TryGetValue(key, out float v) ? v : meitou;

    /// <summary>
    /// Whether the active look has switch <paramref name="label"/> on.
    /// </summary>
    bool PresetHas(string label)
    {
        var found = LookToggles().FirstOrDefault(x => x.Toggle.Label == label);
        return found.Toggle is not null && LookValue(ActiveLook, found.Key, found.Default ? 1 : 0) >= 0.5f;
    }

    /// <summary>
    /// Whether Low quality holds this look switch off: one of <see cref="QualityTiers.LowOffSwitches"/> the preset has on, at Low, not kept on.
    /// </summary>
    bool CutAtLow(string label) =>
        QualityTiers.LowOffSwitches.Contains(label) && !menu.Config.KeepAtLow.Contains(label) && !FaithfulLook && PresetHas(label)
        && menu.Tiers.Tier == QualityTiers.LowPreset;

    /// <summary>
    /// The switches Low holds off now.
    /// </summary>
    internal IEnumerable<string> LowCut() => QualityTiers.LowOffSwitches.Where(CutAtLow);

    /// <summary>
    /// The switches Low holds off, translated, as one list for a hint or notice.
    /// </summary>
    internal string LowCutList() => string.Join(", ", LowCut().Select(s => Loc.Tr(s)));

    /// <summary>
    /// The <see cref="QualityTiers.LowOffSwitches"/> as the preset and the tier say: off where Low cuts them, else the preset's.
    /// </summary>
    internal void ApplyLowCut()
    {
        foreach (var (key, toggle, meitou) in LookToggles().Where(x => QualityTiers.LowOffSwitches.Contains(x.Toggle.Label)))
        {
            bool on = LookValue(ActiveLook, key, meitou ? 1 : 0) >= 0.5f && !CutAtLow(toggle.Label);
            if (toggle.Get() != on) toggle.Set(on);
        }
    }

    /// <summary>
    /// What a look switch is measured against: off (the game's) in Faithful, else the active preset's side.
    /// </summary>
    bool BaseToggle(string key, bool meitou) => !FaithfulLook && LookValue(ActiveLook, key, meitou ? 1 : 0) >= 0.5f;

    /// <summary>
    /// What a look slider is measured against: the game's value in Faithful (its Faithful mark, or its default where it has none), else the active
    /// preset's.
    /// </summary>
    float BaseSlider(string key, Slider slider, float meitou) => FaithfulLook ? GameValue(slider, meitou) : LookValue(ActiveLook, key, meitou);

    /// <summary>
    /// A slider's game value: its Faithful mark, else the one its label names ("Tone map (0 = game)"), else its default (a Meitou-only setting).
    /// </summary>
    static float GameValue(Slider slider, float meitou) =>
        UnderSwitch(slider.Label) ? meitou : slider.Faithful ?? LabelGameValue(slider.Label) ?? meitou;

    /// <summary>
    /// The look settings that differ from the game's (Faithful) or the active preset's (by key); a switch Low holds off is not a change.
    /// </summary>
    internal HashSet<string> LookChanges()
    {
        var changed = new HashSet<string>();
        foreach (var (key, toggle, meitou) in LookToggles())
        {
            if (toggle.Get() != BaseToggle(key, meitou) && !(CutAtLow(toggle.Label) && !toggle.Get())) changed.Add(key);
        }

        foreach (var (key, slider, meitou) in LookSliders())
        {
            if (MathF.Abs(slider.Get() - BaseSlider(key, slider, meitou)) > 0.002f * (slider.Max - slider.Min))
                changed.Add(key);
        }

        return changed;
    }

    /// <summary>
    /// Whether the look is Faithful (the game as it ships, every option still free to change) rather than a Meitou preset: saved (<see
    /// cref="UserConfig.FaithfulLook"/>); unknown, it is Faithful while every Faithful / Meitou effect switch is on the game's side (one that does
    /// not take, such as indirect light without ray queries, is ignored; so are the tier's switches and the optional bloom). The guess is not saved:
    /// choosing a look saves it.
    /// </summary>
    internal bool FaithfulLook => menu.Config.FaithfulLook ?? menu.Values.Toggles
        .Where(t => IsGraphicsSwitch(t) && !t.Optional && !QualityTiers.PresetToggles.Any(p => p.Label == t.Label)
            && !StuckSwitches.Contains(t.Label))
        .All(t => !t.Get());

    /// <summary>
    /// Applies <paramref name="look"/> with the Meitou look on and saves it as the choice (none for Meitou): its switches (one Low holds off stays
    /// off) and sliders.
    /// </summary>
    internal void ApplyLook(LookPreset look)
    {
        menu.Config.FaithfulLook = false;
        ChosenLook = look;
        menu.Config.LookPreset = ReferenceEquals(look, MeitouLook) ? null : look.Name;
        foreach (var (key, toggle, meitou) in LookToggles())
        {
            bool on = LookValue(look, key, meitou ? 1 : 0) >= 0.5f && !CutAtLow(toggle.Label);
            if (toggle.Get() != on) toggle.Set(on);
        }

        foreach (var (key, slider, meitou) in LookSliders())
        {
            slider.Set(LookValue(look, key, meitou));
        }
    }

    /// <summary>
    /// One look setting back to the game's value (Faithful) or the active preset's (a switch Low holds off stays off).
    /// </summary>
    void ResetLook(string key)
    {
        foreach (var (k, toggle, meitou) in LookToggles())
        {
            if (k == key)
            {
                bool on = BaseToggle(k, meitou) && !CutAtLow(toggle.Label);
                if (toggle.Get() != on) toggle.Set(on);
            }
        }

        foreach (var (k, slider, meitou) in LookSliders())
        {
            if (k == key) slider.Set(BaseSlider(k, slider, meitou));
        }

        menu.Notice = FaithfulLook ? "Back to the game's value." : $"Back to {ActiveLook.Name}'s value.";
    }

    /// <summary>
    /// Switches the look to Faithful or Meitou (the active preset), keeping the quality tier: its sliders are set again for the new look.
    /// </summary>
    internal void SetFaithful(bool faithful)
    {
        // The tier stays: its sliders are measured from the switches' sides (Draw distances, Shadows), so they are set again for the new look.
        int tier = menu.Tiers.CurrentPreset();
        menu.Tiers.TierOverride = tier;
        try
        {
            menu.Config.FaithfulLook = faithful;
            if (faithful)
            {
                SetFaithfulSwitchesAndSliders();
            }
            else
            {
                // The Meitou quality switches as the tier has them, then the active preset's look.
                SetMeitouSwitches(tier);
                ApplyLook(ActiveLook);
                // The traced techniques follow the indirect light, which the look has just set.
                menu.Tiers.ApplyTracedSwitches(tier < QualityTiers.CustomPreset ? tier : QualityTiers.HighPreset);
            }

            if (tier < QualityTiers.CustomPreset)
            {
                foreach (var (p, slider) in menu.Tiers.PresetPairs())
                {
                    slider.Set(menu.Tiers.PresetTarget(p, slider, tier));
                }
            }
        }
        finally
        {
            menu.Tiers.TierOverride = null;
        }

        menu.Tiers.LastTier = tier;
        menu.Notice = faithful ? "Faithful: every graphics feature as the game ships it." : $"Meitou look: {ActiveLook.Name}.";
    }

    /// <summary>
    /// Turns off the Faithful / Meitou switches that are on and the look sliders to the game's values.
    /// </summary>
    void SetFaithfulSwitchesAndSliders()
    {
        foreach (var toggle in menu.Values.Toggles.Where(t => IsGraphicsSwitch(t) && t.Get()))
        {
            toggle.Set(false);
            if (toggle.Get()) StuckSwitches.Add(toggle.Label);
        }

        foreach (var (_, slider, meitou) in LookSliders())
        {
            slider.Set(GameValue(slider, meitou));
        }
    }

    /// <summary>
    /// Sets the Meitou quality switches as the tier has them; the active preset's look is applied after.
    /// </summary>
    void SetMeitouSwitches(int tier)
    {
        int switches = tier < QualityTiers.CustomPreset ? tier : QualityTiers.HighPreset;
        for (int i = 0; i < menu.Values.Toggles.Count; i++)
        {
            if (menu.Values.Toggles[i] is var t && IsGraphicsSwitch(t) && !IsLook(t.Label, true))
            {
                bool on = QualityTiers.PresetToggles.FirstOrDefault(p => p.Label == t.Label) is { } tierSwitch
                    ? QualityTiers.SwitchTarget(tierSwitch, menu.ToggleDefaults[i], switches)
                    : menu.ToggleDefaults[i];
                if (t.Get() != on) t.Set(on);
            }
        }
    }

    #endregion

    #region Saving presets

    /// <summary>
    /// Saves the look as the player's preset <paramref name="name"/> and makes it the active one; null (the notice says why) when its file could not
    /// be written.
    /// </summary>
    internal LookPreset? SaveLook(string name)
    {
        var set = new Dictionary<string, float>();
        foreach (var (key, toggle, meitou) in LookToggles())
        {
            bool on = toggle.Get() || CutAtLow(toggle.Label);   // a switch Low holds off keeps the preset's side
            if (on != meitou) set[key] = on ? 1 : 0;
        }

        foreach (var (key, slider, meitou) in LookSliders())
        {
            if (MathF.Abs(slider.Get() - meitou) > 0.002f * (slider.Max - slider.Min))
                set[key] = MathF.Round(slider.Get(), 3);
        }

        var saved = new LookPreset(name, "mine", MeitouVersion, set, Path.Combine(PresetFolder, MakeFileName(name)));
        if (!WritePreset(saved)) return null;

        var list = Looks();
        int at = list.FindIndex(l => l.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (at >= 0)
        {
            list[at] = saved;
        }
        else
        {
            list.Add(saved);
        }

        ApplyLook(saved);
        return saved;
    }

    /// <summary>
    /// Deletes the player's preset <paramref name="look"/> (its file too) and applies Meitou.
    /// </summary>
    internal void DeleteLook(LookPreset look)
    {
        if (look.File is { } file && !menu.NoDiskWrites)
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }

        Looks().Remove(look);
        ApplyLook(MeitouLook);
    }

    /// <summary>
    /// The file name of a preset named <paramref name="name"/>.
    /// </summary>
    static string MakeFileName(string name) => global::Meitou.Settings.LookPresetFile.FileName(name);

    /// <summary>
    /// <paramref name="stem"/>, or it with a number, so no look has the name.
    /// </summary>
    internal string FreeName(string stem) => global::Meitou.Settings.LookPresetFile.FreeName(stem, Looks().Select(l => l.Name));

    /// <summary>
    /// Adds the preset of the clipboard's share code under a free name and applies it; the notice says when the clipboard holds none.
    /// </summary>
    internal void PasteLook()
    {
        if (global::Meitou.Settings.LookPresetFile.FromShareCode(menu.Player.ClipboardRead?.Invoke()) is not { } read
            || new LookPreset(read.Name, "pasted", read.Made, read.Values) is not { } pasted)
        {
            menu.Notice = "The clipboard holds no preset code (MT1:…). Copy one with Share first.";
            return;
        }

        if (SaveLookAs(FreeName(pasted.Name), pasted.Values, pasted.Made) is { } look)
            menu.Notice = $"{look.Name} pasted and loaded (made on Meitou {look.Made}).";
    }

    /// <summary>
    /// Adds a preset of the player's and makes it the active one; null (the notice says why) when its file could not be written.
    /// </summary>
    LookPreset? SaveLookAs(string name, Dictionary<string, float> set, string made)
    {
        var look = new LookPreset(name, "mine", made, set, Path.Combine(PresetFolder, MakeFileName(name)));
        if (!WritePreset(look)) return null;

        Looks().Add(look);
        ApplyLook(look);
        return look;
    }

    /// <summary>
    /// Writes a preset's file (the showcase writes none); false, with the reason in the notice, when it could not be written.
    /// </summary>
    bool WritePreset(LookPreset look)
    {
        if (menu.NoDiskWrites) return true;
        try
        {
            Directory.CreateDirectory(PresetFolder);
            File.WriteAllText(look.File!, WriteLook(look));
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            menu.Notice = "Could not write the preset: " + e.Message;
            return false;
        }
    }

    #endregion

    #region Showcase and rows

    /// <summary>
    /// Switches the look to Faithful or Meitou (the settings showcase: <c>--settings-showcase</c> with <c>--screenshot</c>, the Graphics page
    /// offscreen).
    /// </summary>
    internal void ShowcaseFaithful(bool faithful) => SetFaithful(faithful);

    /// <summary>
    /// Applies look <paramref name="index"/> and returns its name (the settings showcase).
    /// </summary>
    internal string ShowcaseLook(int index)
    {
        ApplyLook(Looks()[index]);
        return Looks()[index].Name;
    }

    /// <summary>
    /// How many looks there are (the settings showcase).
    /// </summary>
    internal int ShowcaseLookCount => Looks().Count;

    /// <summary>
    /// A Graphics row, with its performance impact. A look row is marked when it differs from the game's (Faithful) or the active preset's value; a
    /// costly look switch Low holds off says so, and turning it on keeps it on at Low.
    /// </summary>
    internal SettingsMenu.Entry LookRow(SettingsMenu.Entry entry, string key, string label, bool toggle, HashSet<string> changes)
    {
        entry = entry with { Impact = ImpactOf(label), Raw = label };
        if (!IsLook(label, toggle)) return entry;

        if (toggle && entry.Toggle is { } cut && CutAtLow(label) && !cut.Get())
        {
            return entry with
            {
                Warn = true,
                Hint = "Off at Low quality. Choose Meitou to keep it on at Low.",
                Toggle = cut with
                {
                    Set = v =>
                    {
                        if (v && !menu.Config.KeepAtLow.Contains(label)) menu.Config.KeepAtLow.Add(label);
                        cut.Set(v);
                    },
                },
            };
        }

        if (toggle && entry.Toggle is { } kept && menu.Config.KeepAtLow.Contains(label))
        {
            entry = entry with
            {
                Toggle = kept with
                {
                    Set = v =>
                    {
                        if (!v) menu.Config.KeepAtLow.Remove(label);
                        kept.Set(v);
                    },
                },
            };
        }

        return entry with { Changed = changes.Contains(key), Reset = () => ResetLook(key) };
    }

    #endregion
}