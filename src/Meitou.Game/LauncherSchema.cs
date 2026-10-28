using System.Text.Json;
using Meitou.Data.World;
using Meitou.Rendering;
using Meitou.Settings;

namespace Meitou.Game;

/// <summary>
/// The settings the launcher's Graphics page shows (docs/launcher.md "Graphics"), written by <c>--settings-schema &lt;file&gt;</c> into each
/// release (tools/scripts/package-release.ps1). The keys are the ones this release saves under, so a renamed setting renames its launcher row.
/// Every Tab panel slider and switch is listed, on the page the game's settings menu puts it (<see cref="SettingsMenu.Section"/>).
/// It is also the catalogue the game's own Display and Graphics pages draw their rows from (<see cref="Find"/>, <see cref="Order"/>: name, range,
/// scale direction, value text, choices, impact and order), so the launcher and the game show the same settings the same way.
/// </summary>
static class LauncherSchema
{
    static string Key(string property) => JsonNamingPolicy.CamelCase.ConvertName(property);

    static SettingDescription Graphics(string label, float min, float max, float step, float shown) => new()
    {
        Key = label, Graphics = true, Kind = SettingKind.Slider, Name = SettingsMenu.Label(label), Description = SettingsMenu.SliderHint(label),
        Min = min, Max = max, Step = step, Shown = shown, Impact = SettingsMenu.ImpactOf(label),
    };

    static readonly string[] LowMediumHigh = ["Low", "Medium", "High"];

    /// <summary>The Tab panel sliders of the Vulkan renderer (WorldFrame.CreateSettingsPanel) with their ranges; the game clamps a saved value to its slider.</summary>
    static IEnumerable<SettingDescription> GraphicsSliders() =>
    [
        Graphics(WorldFrame.TextureQualitySlider, 0, Meitou.Data.Textures.TextureQuality.Maximum, 1, 0) with
            { Name = "Texture quality", Labels = [.. Meitou.Data.Textures.TextureQuality.Labels], Reversed = true },
        Graphics(WorldFrame.FilteringSlider, 0, 4, 1, 4) with { Name = "Texture filtering", Labels = ["1x", "2x", "4x", "8x", "16x"] },
        Graphics(WorldFrame.UpscalerSliders[0], 0, 5, 1, 3) with { Name = "Anti-aliasing/upscaling", Labels = ["Off", "FXAA", "SMAA", "TAA", "FSR", "DLSS"] },
        Graphics(WorldFrame.UpscalerSliders[1], 0.33f, 1, 0.01f, 1) with { Scale = 100, Suffix = " %" },
        Graphics(WorldFrame.UpscalerSliders[2], 0, 1, 0.01f, 0.5f) with { Format = "0.00" },
        // World
        Graphics(WorldFrame.ObjectDistanceSlider, 1000, 400000, 100, Enhancements.MeitouObjectDistance) with { Logarithmic = true },
        Graphics(WorldFrame.LandmarkDistanceSlider, 1000, 400000, 100, Enhancements.MeitouLandmarkDistance) with { Logarithmic = true },
        Graphics(WorldFrame.DistantTownsSlider, 0, ObjectRanges.MaxDistantTownRangeZones * 10, 1, ObjectRanges.MaxDistantTownRangeZones) with { Suffix = " zones" },
        Multiplier(WorldFrame.ObjectLodSlider, 0.25f, 4, 1),
        // Fewer pixels of error are finer: drawn mirrored, so right is more detail.
        Graphics(WorldFrame.TerrainErrorSlider, 4, 32, 0.1f, Enhancements.MeitouTerrainPixelError) with { Logarithmic = true, Format = "0.0", Suffix = " px", Reversed = true },
        Multiplier(WorldFrame.FoliageDistanceSlider, 0.25f, 80, 1),
        Multiplier(WorldFrame.FoliageLodSlider, 0.25f, 8, 1),
        Graphics(WorldFrame.LargeFoliageRangeSlider, 1000, 120000, 50, 20000) with { Logarithmic = true },
        Graphics(WorldFrame.MediumFoliageRangeSlider, 400, 80000, 50, 10000) with { Logarithmic = true },
        Graphics(WorldFrame.SmallFoliageRangeSlider, 200, 40000, 50, 4000) with { Logarithmic = true },
        Graphics(WorldFrame.ImpostorDistanceSlider, 500, 40000, 50, 5000) with { Logarithmic = true },
        Graphics(WorldFrame.LargeImpostorDistanceSlider, 500, 40000, 50, 10000) with { Logarithmic = true },
        Multiplier(WorldFrame.GrassDistanceSlider, 0.25f, 80, 1),
        Graphics(WorldFrame.GrassDensitySlider, 0.1f, 2, 0.05f, 1) with { Format = "0.00", Suffix = "x" },
        Graphics(WorldFrame.GroundReliefQualitySlider, 0, 2, 1, 2) with { Labels = LowMediumHigh },
        Graphics(WorldFrame.GroundReliefDepthSlider, 0, 80, 0.5f, 20) with { Format = "0.0", Suffix = " cm" },
        Graphics(WorldFrame.GroundDisplaceDistanceSlider, 10, 200, 1, 50) with { Suffix = " m" },
        Graphics(WorldFrame.GroundParallaxDistanceSlider, 10, 150, 1, 50) with { Suffix = " m" },
        // Water
        Graphics(WorldFrame.WaterReflectionSlider, 0, 4, 1, 2) with { Labels = ["Off", "Landscape", "Characters", "Buildings", "Everything"] },
        Multiplier(WorldFrame.ReflectionRangeSlider, 0.1f, 50, 0.6f),
        Multiplier(WorldFrame.WaterClaritySlider, 0.25f, 8, 0.5f),
        Graphics(WorldFrame.FarWaterClaritySlider, 0, 1, 0.01f, 0.25f) with { Format = "0.00" },
        Graphics(WorldFrame.WaterClearDistanceSlider, 1000, 100000, 100, 10000) with { Logarithmic = true },
        // Lighting
        Graphics(WorldFrame.ShadowDistanceSlider, KenshiShadows.MinRange * 0.9f, 200000, 100, Enhancements.MeitouShadowRange) with
            { Logarithmic = true, OffBelow = KenshiShadows.MinRange },
        Graphics(WorldFrame.ShadowMapSlider, 0, 2, 1, 2) with { Labels = [.. KenshiShadows.QualitySizes.Select((s, i) => $"{LowMediumHigh[i]} · {s}")] },
        Graphics(WorldFrame.ShadowFilterSlider, 0, 2, 1, 2) with { Labels = LowMediumHigh },
        Graphics(WorldFrame.HazeStrengthSlider, 0, 3, 0.01f, Enhancements.MeitouHazeStrength) with { Format = "0.00" },
        Graphics(WorldFrame.PlanetshineStrengthSlider, 0, 200, 1, Enhancements.MeitouPlanetshineStrength),
        Graphics(WorldFrame.AmbientStrengthSlider, 0, 3, 0.05f, 1) with { Format = "0.00" },
        Graphics(WorldFrame.NightAmbientStrengthSlider, 0, 5, 0.05f, 1) with { Format = "0.00" },
        Graphics(WorldFrame.ShaftStrengthSlider, 0, 1, 0.05f, new PostOptions().ShaftStrength) with { Format = "0.00" },
        Graphics(WorldFrame.ShaftAirSlider, 0, 4, 0.05f, 1) with { Format = "0.00" },
        Multiplier(WorldFrame.ShaftAirDawnSlider, 0, 6, 1) with { Step = 0.1f, Format = "0.0" },
        Graphics(WorldFrame.ShaftQualitySlider, 0, 2, 1, 2) with { Labels = LowMediumHigh },
        // Effects
        Graphics(WorldFrame.ParticleSliders[0], 0, 4, 1, 3) with { Name = "Weather particles", Labels = ["Off", "Quarter resolution", "Half resolution", "Automatic", "Full resolution"] },
        Graphics(WorldFrame.ParticleSliders[1], 0.1f, 1, 0.05f, 1) with { Format = "0.00", Suffix = "x" },
        Graphics(WorldFrame.ToneMapSlider, 1, 3, 1, 3) with { Labels = ["Shoulder", "ACES", "Hybrid"] },
        Graphics(WorldFrame.ToneMixSlider, 0, 1, 0.05f, 0.75f) with { Format = "0.00" },
        Graphics(WorldFrame.GradingSlider, 0, 1, 1, 1) with { Name = "Colour grading", Labels = ["Off", "On"] },
        Graphics(WorldFrame.HeatHazeStrengthSlider, 0, 3, 0.05f, Enhancements.MeitouHeatHazeStrength) with { Format = "0.00" },
        Graphics(WorldFrame.NightGradeStrengthSlider, 0, 1, 0.05f, NightGrade.MeitouStrength) with { Format = "0.00" },
        Graphics(WorldFrame.BloomStrengthSlider, 0, 0.25f, 0.005f, new PostOptions().BloomStrength) with { Format = "0.000" },
    ];

    static SettingDescription Multiplier(string label, float min, float max, float shown) =>
        Graphics(label, min, max, 0.05f, shown) with { Logarithmic = min > 0, Format = "0.00", Suffix = "x" };

    /// <summary>The Tab panel switches: the Faithful / Meitou ones (their state as a fresh <see cref="WorldOptions"/> has it) and the plain on/off ones.</summary>
    static IEnumerable<SettingDescription> Switches()
    {
        static SettingDescription Switch(string label, string off, string on, bool shown) => new()
        {
            Key = SettingsMenu.SwitchKeyPrefix + label, Graphics = true, Kind = SettingKind.Toggle, Name = SettingsMenu.Label(label),
            Description = SettingsMenu.ToggleHint(label), Off = off, On = on, Shown = shown ? 1 : 0, Impact = SettingsMenu.ImpactOf(label),
        };
        foreach (var e in WorldFrame.LiveSwitches(new WorldOptions(), () => null, () => null, () => null).Where(e => !SettingsMenu.HiddenSwitch(e.Name)))
            yield return Switch(e.Name, e.Faithful is "off" or "none" ? "Off" : $"Faithful: {e.Faithful}", $"Meitou: {e.Meitou}", e.IsMeitou);
        yield return Switch(WorldFrame.FifthCascadeSwitch, "Off", "On", true);
        // An optional effect, in neither look: plain Off / On (Toggle.Optional).
        yield return Switch(WorldFrame.BloomSwitch, "Off", "On", false);
        foreach (var label in new[] { WorldFrame.RockSmoothingSwitch, WorldFrame.OutlineRefineSwitch })
            yield return Switch(label, "Off", "Meitou: on", true);   // Faithful / Meitou switches whose game side is off
    }

    public static SettingsSchema Create()
    {
        var defaults = new UserConfig();
        var sliders = GraphicsSliders().ToList();
        var switches = Switches().ToList();
        var display = new SettingsSection
        {
            Name = "Display",
            Settings =
            [
                new() { Key = Key(nameof(UserConfig.FullScreen)), Kind = SettingKind.Toggle, Name = "Full screen", Description = SettingsMenu.SliderHint("Full screen"),
                    Default = defaults.FullScreen ? 1 : 0, On = "Borderless", Off = "Window" },
                new() { Key = Key(nameof(UserConfig.VSync)), Kind = SettingKind.Toggle, Name = "VSync", Description = SettingsMenu.SliderHint("VSync"),
                    Default = defaults.VSync ? 1 : 0 },
                new() { Key = Key(nameof(UserConfig.FpsLimit)), Kind = SettingKind.Slider, Name = "Frame limit", Description = SettingsMenu.SliderHint("Frame limit"),
                    Default = defaults.FpsLimit, Shown = defaults.FpsLimit, Min = 0, Max = 360, Step = 5, Suffix = " fps", OffBelow = 1, OffText = "Unlimited" },
                .. (!OperatingSystem.IsMacOS() ? (SettingDescription[])[new() { Key = Key(nameof(UserConfig.FrameGeneration)), Kind = SettingKind.Toggle, Name = "Frame generation (FSR 3)",
                    Description = "Shows a generated frame between each two rendered ones through AMD's FSR 3 frame-interpolation layer: nearly twice the frames shown, with a little more input delay. Works with TAA, FSR and DLSS; needs the FidelityFX library (amd_fidelityfx_vk.dll, libamd_fidelityfx_vk.so). Takes effect at start-up.",
                    Default = defaults.FrameGeneration ? 1 : 0, On = "On", Off = "Off" }] : []),
                new() { Key = Key(nameof(UserConfig.TickAhead)), Kind = SettingKind.Slider, Name = "World tick alongside drawing", Description = SettingsMenu.TickAheadHint,
                    Default = (int)defaults.TickAhead, Shown = (int)defaults.TickAhead, Min = 0, Max = 2, Step = 1, Labels = [.. SettingsMenu.TickAheadChoices] },
                // Null defaults below: the game's value depends on the Faithful / Meitou mode and the computer until the setting is saved.
                .. sliders.Where(s => SettingsMenu.Section(s.Key) == "Display"),
            ],
        };
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
                        .OrderBy(s => SettingsMenu.GroupRank(s.Key.StartsWith(SettingsMenu.SwitchKeyPrefix, StringComparison.Ordinal) ? s.Key[SettingsMenu.SwitchKeyPrefix.Length..] : s.Key))],
                }).Where(section => section.Settings.Count > 0)
                .Select(section => { section.Groups = SettingsMenu.LauncherGroups([.. section.Settings.Select(s => s.Key)]); return section; }),
            ],
            Presets = new SettingsPresets
            {
                Key = Key(nameof(UserConfig.GraphicsPreset)), Name = "Quality preset", Description = SettingsMenu.PresetHint,
                Names = [.. SettingsMenu.PresetChoices], Custom = SettingsMenu.PresetChoices[^1], Default = SettingsMenu.DefaultPreset,
                Keys = [.. SettingsMenu.PresetKeys()],
                Detect = Program.HardwareProbeFlag,
            },
            Looks = Looks(switches, sliders),
        };
    }

    /// <summary>
    /// The looks as <c>graphics</c> values, as the settings menu applies them (SettingsMenu.SetFaithful, ApplyLook): Faithful turns every look switch
    /// off and puts the look sliders at the game's value (the one their label names; a strength under a switch stays); Meitou leaves them at their
    /// start value, the tier's switches to the quality preset.
    /// </summary>
    static SettingsLooks Looks(List<SettingDescription> switches, List<SettingDescription> sliders)
    {
        var shown = switches.Concat(sliders).ToDictionary(s => s.Key, s => s.Shown);
        var keys = SettingsMenu.LookKeys(switches.Select(s => s.Key[SettingsMenu.SwitchKeyPrefix.Length..]), sliders.Select(s => s.Key)).ToList();
        var tier = SettingsMenu.TierSwitchKeys.ToHashSet();
        bool IsSwitch(string key) => key.StartsWith(SettingsMenu.SwitchKeyPrefix, StringComparison.Ordinal);
        return new SettingsLooks
        {
            Name = "Look", Description = SettingsMenu.LookHint,
            FaithfulKey = Key(nameof(UserConfig.FaithfulLook)), PresetKey = Key(nameof(UserConfig.LookPreset)),
            Faithful = keys.ToDictionary(k => k, k => IsSwitch(k) ? 0 : SettingsMenu.UnderSwitch(k) ? shown[k] : SettingsMenu.LabelGameValue(k) ?? shown[k]),
            Meitou = keys.Where(k => !tier.Contains(k)).ToDictionary(k => k, k => shown[k]),
            Presets = [.. SettingsMenu.BuiltInLooks.Select(l => new SettingsLook { Name = l.Name, Values = new(l.Values) })],
            Folder = SettingsMenu.LookFolder,
            Keys = [.. keys.Where(k => IsSwitch(k) ? SettingsMenu.IsLook(k[SettingsMenu.SwitchKeyPrefix.Length..], true) : SettingsMenu.IsLook(k, false))],
            Made = SettingsMenu.MeitouVersion,
        };
    }

    static readonly Lazy<(Dictionary<string, SettingDescription> Find, Dictionary<string, int> Order)> catalogue = new(() =>
    {
        var all = Create().Sections.SelectMany(section => section.Settings).ToList();
        return (all.ToDictionary(s => s.Key), all.Select((s, i) => (s.Key, i)).ToDictionary(x => x.Key, x => x.i));
    });

    /// <summary>A setting's description by its key (a slider's label, <c>Meitou: </c> and a switch's label, a top-level key such as <c>fpsLimit</c>); null when not listed.</summary>
    internal static SettingDescription? Find(string key) => catalogue.Value.Find.GetValueOrDefault(key);

    /// <summary>Where a setting comes on its page (the launcher's order, the game's too); unlisted ones go last, in their own order.</summary>
    internal static int Order(string key) => catalogue.Value.Order.GetValueOrDefault(key, int.MaxValue);

    /// <summary><c>--settings-schema &lt;file&gt;</c>: writes the schema and exits, before anything else starts.</summary>
    public static int Write(string path)
    {
        File.WriteAllText(path, Create().ToJson());
        Console.WriteLine($"schema    {path}");
        return 0;
    }
}
