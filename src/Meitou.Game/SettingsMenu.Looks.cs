using System.IO.Compression;
using System.Numerics;
using System.Text;
using System.Text.Json;
using Meitou.Data.Ui;
using Meitou.Engine.Input;
using Meitou.Rendering;

namespace Meitou.Game;

/// <summary>
/// The look and its presets on the Graphics page (docs/native-ui.md "Settings"). The look tabs (Light, Sky, Colour, Water, Skin) belong to the look: Faithful (the game
/// exactly as it ships, nothing to set) or a Meitou preset (Meitou plus the settings the preset changes). The quality tabs (Distances, Detail) belong to the quality tier.
/// Presets are JSON files in a <c>presets</c> folder beside <c>meitou.user.json</c>; a share code is <c>MT1:</c> and the deflated JSON in base64.
/// </summary>
sealed partial class SettingsMenu
{
    /// <summary>The roles <see cref="Role"/> gives the look's settings (the rest are quality settings, which the tier sets).</summary>
    static readonly string[] LookRoles = ["Light", "Sky", "Colour", "Water", "Skin"];
    /// <summary>This build's release (its git tag without the <c>v</c>, as the settings menu shows it), written into the presets it makes.</summary>
    internal static string MeitouVersion => BuildVersion.Tag.TrimStart('v');

    /// <summary>
    /// Switches that save memory or time and leave the picture as it is (welded tangents, billboards past the impostor distance, generated levels): the look leaves
    /// them alone, so Faithful costs no memory for nothing; they are quality settings the player sets.
    /// </summary>
    internal static readonly string[] PerformanceSwitches = ["Mesh welding", "Far impostors", "Foliage levels", "Character levels"];

    /// <summary>A look preset: what it sets, by the keys the settings are saved under (a slider's label, "Meitou: " and a switch's label); everything else is Meitou's.</summary>
    sealed record LookPreset(string Name, string Source, string Made, Dictionary<string, float> Values, string? File = null);

    static string SwitchKey(string label) => "Meitou: " + label;

    static readonly LookPreset MeitouLook = new("Meitou", "built in", MeitouVersion, []);
    /// <summary>Example looks, each far from the others (docs/native-ui.md "Settings"); everything they do not set is Meitou's.</summary>
    static readonly LookPreset[] ExampleLooks =
    [
        new("Hot and dry", "example", MeitouVersion, new() { [SwitchKey(WorldFrame.BloomSwitch)] = 1, [WorldFrame.BloomStrengthSlider] = 0.13f, [WorldFrame.HeatHazeStrengthSlider] = 0.8f }),
        new("Vanilla colours", "example", MeitouVersion, new() { [SwitchKey("Tone map and grade")] = 0, [SwitchKey("Night grading")] = 0, [SwitchKey("Dithering")] = 0 }),
    ];

    List<LookPreset>? looks;
    LookPreset? activeLook;
    Choice? lookChoice, qualityChoice;
    Choice LookChoice => lookChoice ??= new(() => Looks().Select(l => l.Source == "built in" ? l.Name : $"{l.Name}  ·  {l.Source}").ToList(),
        () => Looks().IndexOf(ActiveLook), i => { var look = Looks()[i]; ApplyLook(look); notice = $"{look.Name} loaded."; });
    Choice QualityChoice => qualityChoice ??= PresetEntry.Choice!;

    string PresetFolder => Path.Combine(Path.GetDirectoryName(config.Path) is { Length: > 0 } dir ? dir : ".", LookFolder);

    /// <summary>The folder of the player's look presets, beside <c>meitou.user.json</c>.</summary>
    internal const string LookFolder = "presets";
    internal const string LookHint = "The game as it ships, or Meitou with a look preset.";
    /// <summary>The built-in looks, Meitou first (saved as no <see cref="UserConfig.LookPreset"/>), with what each sets; the player's are in <see cref="LookFolder"/>.</summary>
    internal static IReadOnlyList<(string Name, IReadOnlyDictionary<string, float> Values)> BuiltInLooks =>
        [.. new[] { MeitouLook }.Concat(ExampleLooks).Select(l => (l.Name, (IReadOnlyDictionary<string, float>)l.Values))];

    /// <summary>The <c>graphics</c> keys a look sets: every Faithful / Meitou graphics switch (Faithful turns them all off; the fifth cascade and the
    /// <see cref="PerformanceSwitches"/> are quality switches) and the look tabs' sliders.</summary>
    internal static IEnumerable<string> LookKeys(IEnumerable<string> switchLabels, IEnumerable<string> sliderLabels) =>
        switchLabels.Where(label => label != WorldFrame.FifthCascadeSwitch && !PerformanceSwitches.Contains(label) && GraphicsPages.Contains(GraphicsSection(label, true))).Select(SwitchKey)
            .Concat(sliderLabels.Where(label => Section(label) is not ("Display" or "Advanced") && IsLook(label, false)));

    /// <summary>The quality tiers' switches (their keys): the tier sets them under a Meitou look, so a look leaves them alone (Faithful turns them off).</summary>
    internal static IEnumerable<string> TierSwitchKeys => PresetToggles.Select(p => SwitchKey(p.Label));

    /// <summary>
    /// A switch the menus leave out, which another setting already covers: anti-aliasing (the Display page's method) and weather particles (their size).
    /// Its saved state is not read back, so it cannot undo that setting.
    /// </summary>
    internal static bool HiddenSwitch(string label) => GraphicsSection(label, true) == "Hidden";

    /// <summary>A slider under a feature's switch (a strength): the switch is its Faithful side, so Faithful leaves the slider as it is.</summary>
    internal static bool UnderSwitch(string label) => FeatureGroups.Any(g => g.Header is not null && g.Members.Contains(label));

    /// <summary>The game's value a slider's label names ("Tone map (0 = game)"), if any.</summary>
    internal static float? LabelGameValue(string label) =>
        System.Text.RegularExpressions.Regex.Match(label, @"\((-?\d+(?:\.\d+)?) = game") is { Success: true } m
            ? float.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : null;

    List<LookPreset> Looks()
    {
        if (looks is not null) return looks;
        looks = [MeitouLook, .. ExampleLooks];
        try
        {
            if (!NoDiskWrites && Directory.Exists(PresetFolder))
                foreach (var file in Directory.GetFiles(PresetFolder, "*.json").Order(StringComparer.OrdinalIgnoreCase))
                    if (ReadLook(File.ReadAllText(file), "mine") is { } look) looks.Add(look with { File = file });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return looks;
    }

    LookPreset ActiveLook => activeLook ??= Looks().FirstOrDefault(l => l.Name == config.LookPreset) ?? MeitouLook;

    // The preset files and share codes are LookPresetFile's (Meitou.Core), the launcher's too.
    static LookPreset? ReadLook(string json, string source) =>
        global::Meitou.Settings.LookPresetFile.Read(json) is { } read ? new(read.Name, source, read.Made, read.Values) : null;

    static global::Meitou.Settings.LookPresetData Data(LookPreset look) => new(look.Name, look.Made, look.Values);

    static string WriteLook(LookPreset look) => global::Meitou.Settings.LookPresetFile.Write(Data(look));

    static string ShareCode(LookPreset look) => global::Meitou.Settings.LookPresetFile.ShareCode(Data(look));

    /// <summary>
    /// The Graphics tab a setting sits on, by feature (Display and Advanced settings are sorted out before this): a feature's switch, look settings, quality
    /// settings and distances on one tab, in its group (<see cref="FeatureGroups"/>). Interior lamps is on Advanced; the anti-aliasing and weather particles switches are hidden.
    /// </summary>
    static string GraphicsSection(string label, bool toggle)
    {
        bool S(string prefix) => label.StartsWith(prefix, StringComparison.Ordinal);
        if (toggle)
            return label switch
            {
                "Interior lamps" => "Advanced",
                "Anti-aliasing" or "Weather particles" => "Hidden",
                "Shadows" or "Indirect light" or "Indoor GI" or "Ambient occlusion" or "Light shafts" or "Planetshine" or WorldFrame.FifthCascadeSwitch => "Lighting",
                "Haze" or "Night air" or "Night sky" or "Clouds" or "Cloud shadows" or "Cloud shadows in fog" or "Heat haze" or "Fog" => "Sky & weather",
                "Tone map and grade" or "Night grading" or "Dithering" or WorldFrame.BloomSwitch => "Colour",
                "Skin" or "Character levels" or "Item icons" => "Characters",
                _ => "World",
            };
        if (S("Ambient") || S("Night ambient") || S("Light shafts") || S("Planetshine") || S("Shadow")) return "Lighting";
        if (S("Haze") || S("Heat haze") || WorldFrame.ParticleSliders.Contains(label)) return "Sky & weather";
        if (S("Tone map") || S("Hybrid") || S("Grading") || S("Night grading") || S("Bloom")) return "Colour";
        return "World";
    }

    /// <summary>
    /// Whether a setting is part of the look or of the quality: the look's (<see cref="LookRoles"/>) are the effects and their strengths, the quality's the
    /// distances and the detail. As the Graphics tabs were split before they were grouped by feature, so the looks and the tiers set what they set before.
    /// </summary>
    static string Role(string label, bool toggle)
    {
        bool S(string prefix) => label.StartsWith(prefix, StringComparison.Ordinal);
        if (toggle)
            return label switch
            {
                "Interior lamps" => "Advanced",
                "Anti-aliasing" or "Weather particles" => "Hidden",
                "Shadows" or "Indirect light" or "Indoor GI" or "Ambient occlusion" or "Light shafts" or "Planetshine" => "Light",
                "Haze" or "Night air" or "Night sky" or "Clouds" or "Cloud shadows" or "Cloud shadows in fog" or "Heat haze" or "Fog" => "Sky",
                "Tone map and grade" or "Night grading" or "Dithering" or WorldFrame.BloomSwitch => "Colour",
                "Water" => "Water",
                "Skin" => "Skin",
                "Draw distances" or "Foliage ranges" or "Far impostors" => "Distances",
                _ => "Detail",
            };
        if (S("Ambient") || S("Night ambient") || S("Light shafts") || S("Planetshine")) return "Light";
        if (S("Haze") || S("Heat haze")) return "Sky";
        if (S("Tone map") || S("Hybrid") || S("Grading") || S("Night grading") || S("Bloom")) return "Colour";
        if (label.Contains("clarity", StringComparison.Ordinal) || label.Contains("see-through", StringComparison.Ordinal)) return "Water";
        if (label == WorldFrame.ObjectDistanceSlider || label == WorldFrame.ShadowDistanceSlider || S("Landmark") || S("Distant towns") || S("Object LOD") || S("Foliage")
            || S("Large ") || S("Medium foliage") || S("Small foliage") || S("Impostor") || S("Grass draw") || S("Terrain detail") || S("Reflection range"))
            return "Distances";
        return "Detail";
    }

    /// <summary>Whether a switch or slider is part of the look (presets set it); a quality tier's slider on a look tab (light shafts quality) is not.</summary>
    internal static bool IsLook(string label, bool toggle) => GraphicsSection(label, toggle) != "Hidden" && LookRoles.Contains(Role(label, toggle))
        && (toggle || !PresetSliders.Any(p => p.Label == label));

    /// <summary>A Faithful / Meitou switch of the Graphics page that Faithful turns off (not the gameplay one, Interior lamps, nor the <see cref="PerformanceSwitches"/>).</summary>
    static bool IsGraphicsSwitch(Toggle t) => t.Remaster && !PerformanceSwitches.Contains(t.Label) && GraphicsPages.Contains(GraphicsSection(t.Label, true));

    /// <summary>
    /// How much a setting costs, as a level a player can use on any machine: ranked from the measured costs in the docs (RTX 4090, 1440p; docs/render-*.md),
    /// High about 1 ms or more or a large share of the scene, Medium a few tenths, Low under a tenth or only in some views. "Faster with Meitou": a switch whose Meitou side saves work.
    /// </summary>
    internal static string? ImpactOf(string label) => label switch
    {
        "Indirect light" or "Clouds" or "Shadows" or WorldFrame.ObjectDistanceSlider or "Foliage draw distance x" or "Grass draw distance x"
            or WorldFrame.ShadowDistanceSlider or "Large foliage range (F6 Meitou)" or WorldFrame.WaterReflectionSlider => "High impact",
        "Ambient occlusion" or "Light shafts" or "Water" or "Ground parallax" or "Ground displacement (tessellation)" or "Ground displacement distance (m)"
            or "Ground parallax distance (m)" or WorldFrame.ShadowMapSlider or "Shadow filter 0-2 (Meitou; 0 low-end)" or "Shadow cascade 5 (Meitou)"
            or "Landmark distance (Meitou)" or "Distant towns (zones)" or "Object LOD distance x" or "Foliage LOD distance x (lod Meitou)" or "Medium foliage range"
            or "Terrain detail: error px (less = finer)" or WorldFrame.GrassDensitySlider or "Impostor distance (F7 Meitou)" or "Large impostor distance (F7 Meitou)"
            or "Reflection range x (game 0.6)" or "Anti-aliasing" or WorldFrame.ShaftQualitySlider => "Medium impact",
        "Cloud shadows" or "Indoor GI" or "Planetshine" or "Night air" or "Night sky" or "Skin" or "Rock smoothing (tessellation)" or "Cloud shadows in fog"
            or "Terrain outline refinement (tessellation)" or "Small foliage range" or "Bloom strength (0 = game)" or WorldFrame.BloomSwitch or "Heat haze" or "Blood cells" or "Blood on surfaces" or "Item icons" or "Fog" or "Bolt tracers"
            or "Light shafts air (0 = none)" => "Low impact",
        "Far impostors" or "Foliage levels" or "Character levels" or "Weather particles" or "Mesh welding" => "Faster with Meitou",
        _ when label == WorldFrame.ParticleSliders[0] || label == WorldFrame.ParticleSliders[1] => "Medium impact",
        _ when IsLook(label, false) || IsLook(label, true) => "No impact",
        _ => null,
    };

    IEnumerable<(string Key, Toggle Toggle, bool Default)> LookToggles() =>
        values.Toggles.Select((t, i) => (SwitchKey(t.Label), t, toggleDefaults[i])).Where(x => x.t.Remaster && IsLook(x.t.Label, true));

    IEnumerable<(string Key, Slider Slider, float Default)> LookSliders() =>
        values.Sliders.Select((s, i) => (s.Label, s, defaults[i])).Where(x => Section(x.s.Label) is not ("Display" or "Advanced") && IsLook(x.s.Label, false));

    static float LookValue(LookPreset look, string key, float meitou) => look.Values.TryGetValue(key, out float v) ? v : meitou;

    bool PresetHas(string label) => LookToggles().FirstOrDefault(x => x.Toggle.Label == label) is { Toggle: not null } found && LookValue(ActiveLook, found.Key, found.Default ? 1 : 0) >= 0.5f;

    /// <summary>Whether Low quality holds this look switch off: one of <see cref="LowOffSwitches"/> the preset has on, at Low, not kept on.</summary>
    bool CutAtLow(string label) => LowOffSwitches.Contains(label) && !config.KeepAtLow.Contains(label) && !FaithfulLook && PresetHas(label) && Tier == LowPreset;

    IEnumerable<string> LowCut() => LowOffSwitches.Where(CutAtLow);

    /// <summary>The <see cref="LowOffSwitches"/> as the preset and the tier say: off where Low cuts them, else the preset's.</summary>
    void ApplyLowCut()
    {
        foreach (var (key, toggle, meitou) in LookToggles().Where(x => LowOffSwitches.Contains(x.Toggle.Label)))
        {
            bool on = LookValue(ActiveLook, key, meitou ? 1 : 0) >= 0.5f && !CutAtLow(toggle.Label);
            if (toggle.Get() != on) toggle.Set(on);
        }
    }

    /// <summary>What a look switch is measured against: off (the game's) in Faithful, else the active preset's side.</summary>
    bool BaseToggle(string key, bool meitou) => !FaithfulLook && LookValue(ActiveLook, key, meitou ? 1 : 0) >= 0.5f;

    /// <summary>What a look slider is measured against: the game's value in Faithful (its Faithful mark, or its default where it has none), else the active preset's.</summary>
    float BaseSlider(string key, Slider slider, float meitou) => FaithfulLook ? GameValue(slider, meitou) : LookValue(ActiveLook, key, meitou);

    /// <summary>A slider's game value: its Faithful mark, else the one its label names ("Tone map (0 = game)"), else its default (a Meitou-only setting).</summary>
    static float GameValue(Slider slider, float meitou) => UnderSwitch(slider.Label) ? meitou : slider.Faithful ?? LabelGameValue(slider.Label) ?? meitou;

    /// <summary>The look settings that differ from the game's (Faithful) or the active preset's (by key); a switch Low holds off is not a change.</summary>
    HashSet<string> LookChanges()
    {
        var changed = new HashSet<string>();
        foreach (var (key, toggle, meitou) in LookToggles())
            if (toggle.Get() != BaseToggle(key, meitou) && !(CutAtLow(toggle.Label) && !toggle.Get())) changed.Add(key);
        foreach (var (key, slider, meitou) in LookSliders())
            if (MathF.Abs(slider.Get() - BaseSlider(key, slider, meitou)) > 0.002f * (slider.Max - slider.Min)) changed.Add(key);
        return changed;
    }

    /// <summary>
    /// Whether the look is Faithful (the game as it ships, every option still free to change) rather than a Meitou preset: saved (<see cref="UserConfig.FaithfulLook"/>);
    /// unknown, it is Faithful while every Faithful / Meitou effect switch is on the game's side (one that does not take, such as indirect light without ray queries, is
    /// ignored; so are the tier's switches and the optional bloom). The guess is not saved: choosing a look saves it.
    /// </summary>
    bool FaithfulLook => config.FaithfulLook ?? values.Toggles
        .Where(t => IsGraphicsSwitch(t) && !t.Optional && !PresetToggles.Any(p => p.Label == t.Label) && !stuckSwitches.Contains(t.Label)).All(t => !t.Get());

    void ApplyLook(LookPreset look)
    {
        config.FaithfulLook = false;
        activeLook = look;
        config.LookPreset = ReferenceEquals(look, MeitouLook) ? null : look.Name;
        foreach (var (key, toggle, meitou) in LookToggles())
        {
            bool on = LookValue(look, key, meitou ? 1 : 0) >= 0.5f && !CutAtLow(toggle.Label);
            if (toggle.Get() != on) toggle.Set(on);
        }
        foreach (var (key, slider, meitou) in LookSliders()) slider.Set(LookValue(look, key, meitou));
    }

    /// <summary>One look setting back to the game's value (Faithful) or the active preset's (a switch Low holds off stays off).</summary>
    void ResetLook(string key)
    {
        foreach (var (k, toggle, meitou) in LookToggles())
            if (k == key)
            {
                bool on = BaseToggle(k, meitou) && !CutAtLow(toggle.Label);
                if (toggle.Get() != on) toggle.Set(on);
            }
        foreach (var (k, slider, meitou) in LookSliders()) if (k == key) slider.Set(BaseSlider(k, slider, meitou));
        notice = FaithfulLook ? "Back to the game's value." : $"Back to {ActiveLook.Name}'s value.";
    }

    void SetFaithful(bool faithful)
    {
        // The tier stays: its sliders are measured from the switches' sides (Draw distances, Shadows), so they are set again for the new look.
        int tier = CurrentPreset();
        tierOverride = tier;
        try
        {
            config.FaithfulLook = faithful;
            if (faithful)
            {
                foreach (var toggle in values.Toggles.Where(t => IsGraphicsSwitch(t) && t.Get()))
                {
                    toggle.Set(false);
                    if (toggle.Get()) stuckSwitches.Add(toggle.Label);
                }
                foreach (var (_, slider, meitou) in LookSliders()) slider.Set(GameValue(slider, meitou));
            }
            else
            {
                // The Meitou quality switches as the tier has them, then the active preset's look.
                int switches = tier < CustomPreset ? tier : HighPreset;
                for (int i = 0; i < values.Toggles.Count; i++)
                    if (values.Toggles[i] is var t && IsGraphicsSwitch(t) && !IsLook(t.Label, true))
                    {
                        bool on = PresetToggles.FirstOrDefault(p => p.Label == t.Label) is { } tierSwitch ? SwitchTarget(tierSwitch, toggleDefaults[i], switches) : toggleDefaults[i];
                        if (t.Get() != on) t.Set(on);
                    }
                ApplyLook(ActiveLook);
            }
            if (tier < CustomPreset) foreach (var (p, slider) in PresetPairs()) slider.Set(PresetTarget(p, slider, tier));
        }
        finally { tierOverride = null; }
        lastTier = tier;
        notice = faithful ? "Faithful: every graphics feature as the game ships it." : $"Meitou look: {ActiveLook.Name}.";
    }

    /// <summary>Saves the look as the player's preset <paramref name="name"/> and makes it the active one; null (the notice says why) when its file could not be written.</summary>
    LookPreset? SaveLook(string name)
    {
        var set = new Dictionary<string, float>();
        foreach (var (key, toggle, meitou) in LookToggles())
        {
            bool on = toggle.Get() || CutAtLow(toggle.Label);   // a switch Low holds off keeps the preset's side
            if (on != meitou) set[key] = on ? 1 : 0;
        }
        foreach (var (key, slider, meitou) in LookSliders()) if (MathF.Abs(slider.Get() - meitou) > 0.002f * (slider.Max - slider.Min)) set[key] = MathF.Round(slider.Get(), 3);
        var saved = new LookPreset(name, "mine", MeitouVersion, set, Path.Combine(PresetFolder, MakeFileName(name)));
        if (!WritePreset(saved)) return null;
        var list = Looks();
        int at = list.FindIndex(l => l.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (at >= 0) list[at] = saved; else list.Add(saved);
        ApplyLook(saved);
        return saved;
    }

    void DeleteLook(LookPreset look)
    {
        if (look.File is { } file && !NoDiskWrites)
            try { File.Delete(file); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        Looks().Remove(look);
        ApplyLook(MeitouLook);
    }

    static string MakeFileName(string name) => global::Meitou.Settings.LookPresetFile.FileName(name);

    string FreeName(string stem) => global::Meitou.Settings.LookPresetFile.FreeName(stem, Looks().Select(l => l.Name));

    void PasteLook()
    {
        if (global::Meitou.Settings.LookPresetFile.FromShareCode(player.ClipboardRead?.Invoke()) is not { } read || new LookPreset(read.Name, "pasted", read.Made, read.Values) is not { } pasted)
        {
            notice = "The clipboard holds no preset code (MT1:…). Copy one with Share first.";
            return;
        }
        if (SaveLookAs(FreeName(pasted.Name), pasted.Values, pasted.Made) is { } look) notice = $"{look.Name} pasted and loaded (made on Meitou {look.Made}).";
    }

    /// <summary>Adds a preset of the player's and makes it the active one; null (the notice says why) when its file could not be written.</summary>
    LookPreset? SaveLookAs(string name, Dictionary<string, float> set, string made)
    {
        var look = new LookPreset(name, "mine", made, set, Path.Combine(PresetFolder, MakeFileName(name)));
        if (!WritePreset(look)) return null;
        Looks().Add(look);
        ApplyLook(look);
        return look;
    }

    /// <summary>Writes a preset's file (the showcase writes none); false, with the reason in the notice, when it could not be written.</summary>
    bool WritePreset(LookPreset look)
    {
        if (NoDiskWrites) return true;
        try { Directory.CreateDirectory(PresetFolder); File.WriteAllText(look.File!, WriteLook(look)); return true; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { notice = "Could not write the preset: " + e.Message; return false; }
    }

    // ---- naming a preset (Save as) ----

    bool naming;
    string nameText = "";

    void BeginNaming() { naming = true; nameText = FreeName("My look"); openChoice = null; focus = -1; }

    void FinishNaming()
    {
        string name = nameText.Trim();
        if (name.Length == 0) { notice = "Type a name, or press Esc to cancel."; return; }
        if (name.Equals(MeitouLook.Name, StringComparison.OrdinalIgnoreCase) || ExampleLooks.Any(l => l.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            notice = $"{name} is a built-in preset: choose another name.";
            return;
        }
        naming = false;
        if (SaveLook(name) is { } saved) notice = $"Saved as {saved.Name}.";
    }

    bool NameKey(Key key)
    {
        if (key == Key.Escape) naming = false;
        else if (key == Key.Enter) FinishNaming();
        else if (key == Key.Backspace && nameText.Length > 0) nameText = nameText[..^1];
        return true;
    }

    void NameText(char value)
    {
        if (!char.IsControl(value) && nameText.Length < global::Meitou.Settings.LookPresetFile.MaxNameLength && !Path.GetInvalidFileNameChars().Contains(value)) nameText += value;
    }

    void DrawNameDialog()
    {
        hits.Clear();
        Rect(bounds, new(8 / 255f, 6 / 255f, 5 / 255f, 0.8f));
        Surface(R(290, 230, 540, 236), "window");
        Text("Save as a new preset", R(316, 244, 488, 30), 20, LightInk);
        Text("Name", R(316, 282, 488, 18), 12, MainInk);
        var box = R(316, 302, 488, 32);
        Rect(box, new(0.06f, 0.05f, 0.04f, 0.95f));
        Rect(box with { Y = box.Y + box.Height - scale, Height = scale }, Brass with { W = 0.6f });
        controls.Add(new("settings-name", "EditBox", box with { X = box.X + 8 * scale, Width = box.Width - 16 * scale }, nameText, "", "", true,
            new Dictionary<string, string>
            {
                ["Focused"] = "true", ["TextAlign"] = "Left VCenter",
                ["FontPixels"] = ((int)MathF.Round(15 * scale)).ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["CaretIndex"] = nameText.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), ["SelectionStart"] = "0", ["SelectionLength"] = "0",
            }, Vector4.Zero, LightInk, Pickable: false));
        int changes = LookChanges().Count;
        Text($"Your look as it is now: {ActiveLook.Name}{(changes > 0 ? $" with {changes} change{(changes == 1 ? "" : "s")}" : "")}. What it does not set keeps following Meitou.",
            R(316, 342, 488, 40), 12, MutedInk, "Left Top", wrap: true);
        Button("Cancel", R(500, 418, 138, 32), () => naming = false);
        Button("Save preset", R(650, 418, 154, 32), FinishNaming, primary: true);
    }

    // ---- the Look and Quality rows ----

    /// <summary>The Look and Quality rows above the Graphics tabs.</summary>
    void DrawLookBar()
    {
        var keep = viewport;
        viewport = R(212, 96, 888, 72);
        SyncTier();
        bool faithful = FaithfulLook;
        Text("Look", R(212, 98, 70, 30), 15, LightInk);
        Segmented(R(286, 101, 172, 24), faithful ? 0 : 1, i => SetFaithful(i == 0));
        if (faithful)
        {
            // Faithful is a starting point, not a lock: every option stays free, and what differs from the game is counted and can be reverted.
            int changes = LookChanges().Count;
            if (changes > 0)
            {
                Text(changes == 1 ? "1 change from the game" : $"{changes} changes from the game", R(474, 98, 150, 30), 12, Brass);
                Button("Revert", R(628, 100, 66, 26), () => SetFaithful(true));
            }
            else Text("As the game ships", R(474, 98, 200, 30), 12, MutedInk);
        }
        else
        {
            var look = ActiveLook;
            var at = R(474, 99, 200, 28);
            DropDown(look.Name, at, () => OpenChoice(LookChoice, at));
            int changes = LookChanges().Count;
            bool mine = look.Source == "mine";
            if (changes > 0)
            {
                Text(changes == 1 ? "1 unsaved change" : $"{changes} unsaved changes", R(684, 98, 120, 30), 12, Brass);
                Button("Revert", R(800, 100, 66, 26), () => { ApplyLook(look); notice = $"Back to {look.Name}."; });
            }
            else Text(look.Source == "built in" ? "Built in" : look.Source == "example" ? "Example" : $"Mine · Meitou {look.Made}", R(684, 98, 120, 30), 12, MutedInk);
            const float x = 872;
            if (mine && changes > 0) Button("Save", R(x - 64, 100, 58, 26), () => { if (SaveLook(look.Name) is not null) notice = $"{look.Name} saved."; });
            else if (mine)
                Button("Delete", R(x - 70, 100, 64, 26), () =>
                {
                    pendingAction = () => { DeleteLook(look); return $"{look.Name} deleted; the look is Meitou."; };
                    confirmTitle = "Delete preset?"; confirmation = $"Delete {look.Name}? Its file goes; the look goes back to Meitou."; focus = -1;
                });
            Button("Save as", R(x, 100, 72, 26), BeginNaming);
            Button("Share", R(x + 78, 100, 70, 26), () => { player.ClipboardWrite?.Invoke(ShareCode(look)); notice = $"{look.Name}'s code is on the clipboard: paste it on Discord, or in Paste."; });
            Button("Paste", R(x + 154, 100, 74, 26), PasteLook);
        }
        Text("Quality", R(212, 134, 70, 30), 15, LightInk);
        var q = R(286, 136, 172, 28);
        DropDown(PresetNames[CurrentPreset()], q, () => OpenChoice(QualityChoice, q));
        string cut = string.Join(", ", LowCut());
        if (cut.Length > 0) Text($"Low holds off: {cut}.", R(474, 134, 626, 30), 12, Brass);
        viewport = keep;
    }

    void OpenChoice(Choice choice, UiRect at)
    {
        openChoice = choice; choiceSelection = Math.Clamp(choice.Current(), 0, Math.Max(0, choice.Options().Count - 1));
        choiceScroll = Math.Max(0, choiceSelection - 3); choiceAnchor = at; focus = -1;
    }

    // ---- the settings showcase (--settings-showcase with --screenshot): the Graphics page offscreen ----

    /// <summary>Set by the showcase: presets are neither read from nor written to disk.</summary>
    internal bool NoDiskWrites { get; init; }
    internal void ShowGraphicsTab(int tab) { Visible = true; page = 1; graphicsPage = tab; openChoice = null; naming = false; scroll = scrollTarget = 0; }
    internal void ShowcaseFaithful(bool faithful) => SetFaithful(faithful);
    internal string ShowcaseLook(int index) { ApplyLook(Looks()[index]); return Looks()[index].Name; }
    internal int ShowcaseLookCount => Looks().Count;
    internal void ShowcaseTier(int tier) => ApplyPreset(tier);
    internal void ShowcaseOpenLooks() => OpenChoice(LookChoice, R(474, 99, 200, 28));
    internal void ShowcaseNaming() => BeginNaming();
    internal void ShowcaseSwitch(string label, bool on) { if (values.Toggles.FirstOrDefault(t => t.Label == label) is { } t) t.Set(on); }
    internal void ShowcaseSet(string label, float value) { if (values.Sliders.FirstOrDefault(s => s.Label == label) is { } s) s.Set(value); }
}
