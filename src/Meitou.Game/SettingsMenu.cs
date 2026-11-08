using System.Numerics;
using System.Globalization;
using System.Text.RegularExpressions;
using Meitou.Data.Ui;
using Meitou.Game.Effects;
using Meitou.Game.Host;
using Meitou.Game.Host.Input;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Post;
using Meitou.Game.Rendering.Terrain;
using Meitou.Game.Settings;
using Meitou.Game.Title;
using Meitou.Simulation.Session;
using AudioSettings = Meitou.Game.Audio.AudioSettings;

namespace Meitou.Game;

/// <summary>Shared settings layout and interaction; the current UI mode selects only its presentation.</summary>
sealed partial class SettingsMenu(PlayerInterface player, SettingsPanel values, InputBindings bindings, Action save, Slider frameLimit, UserConfig config)
{
    static readonly string[] Pages =
        ["Display", "Graphics", "Interface", "Controls", "Advanced", "Audio", "Gameplay", "Fixes", "Mods"];
    /// <summary>The MODS page (SettingsMenu.Mods.cs): the loaded mods, each with the settings its code registered (SettingsMenu.ModSettings.cs).</summary>
    const int ModsPage = 8;
    /// <summary>The names <c>ui-open settings:&lt;name&gt;</c> takes: a page, or <c>graphics:&lt;sub-page&gt;</c>; compared ignoring case (spaces and "&amp;" count).</summary>
    internal static IEnumerable<string> PageNames => Pages.Select(p => p.ToLowerInvariant()).Concat(GraphicsPages.Select(p => "graphics:" + p.ToLowerInvariant()));

    /// <summary>The page shown now, as <see cref="PageNames"/> spells it.</summary>
    internal string PageName => page == 1 ? "graphics:" + GraphicsPages[graphicsPage].ToLowerInvariant() : Pages[page].ToLowerInvariant();

    /// <summary>Switches to a page by name (see <see cref="PageNames"/>); false when there is none.</summary>
    internal bool TryOpenPage(string name)
    {
        string[] parts = name.Split(':', 2);
        int index = Array.FindIndex(Pages, p => p.Equals(parts[0], StringComparison.OrdinalIgnoreCase));
        if (index < 0) return false;
        int sub = parts.Length == 2 ? Array.FindIndex(GraphicsPages, p => p.Equals(parts[1], StringComparison.OrdinalIgnoreCase)) : graphicsPage;
        if (parts.Length == 2 && (index != 1 || sub < 0)) return false;
        Navigate(index);
        graphicsPage = sub;
        return true;
    }

    /// <summary>The Graphics tabs: the look tabs, then the quality tabs (SettingsMenu.Looks.cs).</summary>
    internal static readonly string[] GraphicsPages = ["Lighting", "Sky & weather", "Colour", "World", "Characters"];
    /// <summary>The Metal-only preset row (Native / MetalFX quality, balanced, performance / TAA), edited by the Metal settings panel.</summary>
    internal const string MetalPresetSlider = "Metal upscaling";
    readonly float[] defaults = values.Sliders.Select(s => s.Get()).ToArray();
    readonly bool[] toggleDefaults = values.Toggles.Select(t => t.Get()).ToArray();
    readonly Slider mainMenuTransitionSpeed = new("Main menu transition speed", SkyArrival.MinSpeed, SkyArrival.MaxSpeed,
        () => config.MainMenuTransitionSpeed, v => config.MainMenuTransitionSpeed = v, "0.00'x'");
    readonly Slider inGameTransitionSpeed = new("In-game transition speed", SkyArrival.MinSpeed, SkyArrival.MaxSpeed,
        () => config.InGameTransitionSpeed, v => config.InGameTransitionSpeed = v, "0.00'x'");
    readonly Slider characterFlightTime = new("Character flight time", 0.5f, 6, () => config.CharacterFlightSeconds, v => config.CharacterFlightSeconds = v, "0.0' s'");
    Slider? cameraRotateSpeed;
    Slider CameraRotateSpeed => cameraRotateSpeed ??= new("Camera rotation speed", UserConfig.MinCameraRotateSpeed, UserConfig.MaxCameraRotateSpeed,
        () => config.CameraRotateSpeed, v => { config.CameraRotateSpeed = MathF.Round(v * 2) / 2; CameraRotateSpeedChanged?.Invoke(config.CameraRotateSpeed); },
        "0.#", Faithful: global::Meitou.Data.World.KenshiCamera.DefaultRotateSpeed);
    Slider? cameraMoveSpeed, cameraZoomSpeed;
    Slider CameraMoveSpeed => cameraMoveSpeed ??= CameraSpeedSlider("Camera move speed", UserConfig.MinCameraMoveSpeed, UserConfig.MaxCameraMoveSpeed,
        () => config.CameraMoveSpeed ?? global::Meitou.Data.World.KenshiCamera.DefaultMoveSpeed, v => config.CameraMoveSpeed = v, global::Meitou.Data.World.KenshiCamera.DefaultMoveSpeed);
    Slider CameraZoomSpeed => cameraZoomSpeed ??= CameraSpeedSlider("Camera zoom speed", UserConfig.MinCameraZoomSpeed, UserConfig.MaxCameraZoomSpeed,
        () => config.CameraZoomSpeed ?? global::Meitou.Data.World.KenshiCamera.DefaultZoomSpeed, v => config.CameraZoomSpeed = v, global::Meitou.Data.World.KenshiCamera.DefaultZoomSpeed);
    Slider CameraSpeedSlider(string label, float min, float max, Func<float> get, Action<float> set, float original) =>
        new(label, min, max, get, v => { set(MathF.Round(v)); CameraChanged?.Invoke(); }, "0", Faithful: original);
    Slider[]? audioSliders;
    /// <summary>The audio sliders of the original's options (docs/formats/settings.md); each writes <see cref="UserConfig.Audio"/> and calls <see cref="AudioChanged"/>.</summary>
    Slider[] AudioSliders => audioSliders ??= [
        Volume("Music volume", a => a.Music, (a, v) => a with { Music = v }),
        Volume("Ambient volume", a => a.Ambient, (a, v) => a with { Ambient = v }),
        Volume("Footstep volume", a => a.Footstep, (a, v) => a with { Footstep = v }),
        Volume("Sfx volume", a => a.Sfx, (a, v) => a with { Sfx = v }),
        Volume("UI volume", a => a.Ui, (a, v) => a with { Ui = v }),
        Volume("VO volume", a => a.Vo, (a, v) => a with { Vo = v }),
        new("Music frequency", AudioSettings.MinFrequency, AudioSettings.MaxFrequency,
            () => (config.Audio ??= new()).MusicFrequency,
            v => { config.Audio = (config.Audio ?? new()) with { MusicFrequency = MathF.Round(v) }; AudioChanged?.Invoke(); },
            "0", Text: v => $"{v:0} min", Faithful: 5),
        // Meitou's voice cap (docs/audio.md "Voice cap"): the top of the track mixes every sound.
        new("Sound voices", AudioSettings.MinVoices, AudioSettings.MaxVoices,
            () => (config.Audio ??= new()).Voices,
            v => { config.Audio = (config.Audio ?? new()) with { Voices = (int)MathF.Round(v / 8) * 8 }; AudioChanged?.Invoke(); },
            "0", Text: v => v >= AudioSettings.MaxVoices ? "All" : $"{v:0}", Faithful: AudioSettings.MaxVoices, Meitou: AudioSettings.MeitouVoices),
        // Meitou's hearing range (docs/audio.md "Hearing range"): a factor on every distance sounds are heard from.
        new("Sound range", AudioSettings.MinRange, AudioSettings.MaxRange,
            () => (config.Audio ??= new()).Range,
            v => { config.Audio = (config.Audio ?? new()) with { Range = Math.Clamp(MathF.Round(v * 4) / 4, AudioSettings.MinRange, AudioSettings.MaxRange) }; AudioChanged?.Invoke(); },
            "0.##", Text: v => $"{v:0.##}x", Faithful: 1, Meitou: AudioSettings.MeitouRange),
    ];

    Slider[]? gameplaySliders;
    /// <summary>The four sliders of the original's Gameplay page (docs/formats/settings.md "Gameplay"); each writes <see cref="UserConfig.Gameplay"/> and calls <see cref="GameplayChanged"/>.</summary>
    Slider[] GameplaySliders => gameplaySliders ??= [
        Multiplier("Squad size multiplier", GameplaySettings.MinSquadSize, GameplaySettings.MaxSquadSize, g => g.SquadSize, (g, v) => g with { SquadSize = v }),
        Multiplier("Global population multiplier", GameplaySettings.MinPopulation, GameplaySettings.MaxPopulation, g => g.Population, (g, v) => g with { Population = v }),
        Multiplier("Town raid events: size", GameplaySettings.MinRaidSize, GameplaySettings.MaxRaidSize, g => g.RaidSize, (g, v) => g with { RaidSize = v }),
        Multiplier("Town raid events: frequency", GameplaySettings.MinRaidFrequency, GameplaySettings.MaxRaidFrequency, g => g.RaidFrequency, (g, v) => g with { RaidFrequency = v }),
    ];

    Slider Multiplier(string label, float min, float max, Func<GameplaySettings, float> get, Func<GameplaySettings, float, GameplaySettings> set) =>
        new(label, min, max, () => get(config.Gameplay ??= new()), v => SetGameplay(set(config.Gameplay ?? new(), Math.Clamp(MathF.Round(v * 20) / 20, min, max))),
            "0.##", Faithful: 1);

    Slider? autoSaveSlider;
    Slider AutoSaveSlider => autoSaveSlider ??= new("Auto save time (minutes)", GameplaySettings.MinAutoSaveMinutes, GameplaySettings.MaxAutoSaveMinutes,
        () => (config.Gameplay ??= new()).AutoSaveMinutes, v => SetGameplay((config.Gameplay ?? new()) with { AutoSaveMinutes = MathF.Round(v) }), "0", Text: v => $"{v:0} min", Faithful: 10);

    void SetGameplay(GameplaySettings value) { config.Gameplay = value; GameplayChanged?.Invoke(); }

    Slider Volume(string label, Func<AudioSettings, float> get, Func<AudioSettings, float, AudioSettings> set) =>
        new(label, 0, 1, () => get(config.Audio ??= new()), v => { config.Audio = set(config.Audio ?? new(), Math.Clamp(v, 0, 1)); AudioChanged?.Invoke(); },
            "0.00", Text: v => $"{v * 100:0}%", Faithful: 0.5f, Scale: 100);
    Slider? attackSlotsSlider;
    Slider AttackSlotsSlider => attackSlotsSlider ??= new("Attackers per target", 1, 5, () => config.AttackSlots, v =>
    {
        config.AttackSlots = Math.Clamp((int)MathF.Round(v), 1, 5);
        AttackSlotsChanged?.Invoke(config.AttackSlotsOption);
    }, "0", Faithful: UserConfig.FaithfulAttackSlots, Meitou: UserConfig.MeitouAttackSlots);
    Slider? harpoonLimitSlider;
    Slider HarpoonLimitSlider => harpoonLimitSlider ??= new("Harpoon limit", global::Meitou.Simulation.Combat.RangedTuning.MinStuckLimit, global::Meitou.Simulation.Combat.RangedTuning.MaxStuckLimit, () => (config.Gameplay ??= new()).HarpoonLimit,
        v => SetGameplay((config.Gameplay ?? new()) with { HarpoonLimit = Math.Clamp((int)MathF.Round(v), global::Meitou.Simulation.Combat.RangedTuning.MinStuckLimit, global::Meitou.Simulation.Combat.RangedTuning.MaxStuckLimit) }), "0", Logarithmic: true,
        Faithful: global::Meitou.Simulation.Combat.RangedTuning.DefaultStuckLimit);
    Slider? fastSpeedSlider, fastestSpeedSlider;
    Slider FastSpeedSlider => fastSpeedSlider ??= TimeSpeed("Fast speed", SimulationClock.MinFastSpeed, SimulationClock.MaxFastSpeed, 1, () => config.FastSpeed, v => config.FastSpeed = v);
    Slider FastestSpeedSlider => fastestSpeedSlider ??= TimeSpeed("Fastest speed", SimulationClock.MinFastestSpeed, SimulationClock.MaxFastestSpeed, 2,
        () => config.FastestSpeed, v => config.FastestSpeed = v);

    /// <summary>A speed button's game speed, in half steps, applied to the running clock at once.</summary>
    Slider TimeSpeed(string label, double min, double max, int step, Func<double> get, Action<double> set) =>
        new(label, (float)min, (float)max, () => (float)get(), v =>
        {
            set(Math.Clamp(Math.Round(v * 2) / 2, min, max));
            TimeSpeedsChanged?.Invoke();
        }, "0.#", Text: v => $"{v:0.#}x", Faithful: (float)SimulationClock.DefaultSpeeds[step]);
    Slider? groundItemsSlider;
    Slider GroundItemsSlider => groundItemsSlider ??= new("Dropped items kept", 16, global::Meitou.Simulation.Items.GroundItems.MaxLimit, () => config.GroundItemLimit,
        v => config.GroundItemLimit = Math.Clamp((int)MathF.Round(v), 1, global::Meitou.Simulation.Items.GroundItems.MaxLimit), "0", Logarithmic: true,
        Meitou: global::Meitou.Simulation.Items.GroundItems.DefaultLimit);
    readonly List<Hit> hits = [];
    readonly List<UiControl> controls = [];
    readonly SettingsMenuArt art = new();
    bool visible, resetPending;
    int page, graphicsPage, focus = -1;
    float scroll, maxScroll, scale = 1;
    Vector2 pointer;
    UiRect viewport, bounds;
    Hit? dragging;
    InputAction? rebinding;
    Choice? openChoice;
    int choiceSelection, choiceScroll;
    string notice = "";
    Slider? editingNumber;
    string numberText = "";
    int numberCaret, numberAnchor;
    UiRect numberBounds;

    sealed record Choice(Func<IReadOnlyList<string>> Options, Func<int> Current, Action<int> Choose);
    sealed record Entry(string Label, string Hint = "", Slider? Slider = null, Toggle? Toggle = null, Choice? Choice = null,
        InputAction? Binding = null, Action? Run = null, string? Picture = null, bool Segmented = false, bool Changed = false,
        string? Impact = null, bool Warn = false, string? Raw = null, int Depth = 0, bool? Open = null, string? GroupId = null, Action? Reset = null, bool Locked = false);
    sealed record Hit(UiRect Bounds, Action Activate, Slider? Slider = null, UiRect? Track = null, string Label = "");

    /// <summary>The labelled clickable widgets as the last frame drew them (agent control channel: ui-tree, ui-click); sliders are left out.</summary>
    internal IReadOnlyList<(string Label, UiRect Bounds, Action Activate)> Clickables =>
        [.. hits.Where(h => h.Label.Length > 0 && h.Slider is null).Select(h => (h.Label, h.Bounds, h.Activate))];

    /// <summary>Whether <paramref name="p"/> is over a widget a click acts on in the last frame (the enhanced Hardware mouse's hand).</summary>
    public bool OverClickable(Vector2 p) => Visible && hits.Any(h => h.Bounds.Contains(p.X, p.Y));

    /// <summary>The sliders as the last frame drew them, with the track they sit on (agent control channel: ui-tree, ui-set).</summary>
    internal IReadOnlyList<(Slider Slider, UiRect Bounds)> SliderHits => [.. hits.Where(h => h.Slider is not null).Select(h => (h.Slider!, h.Bounds))];

    public IReadOnlyList<Slider> Sliders => values.Sliders;
    public IReadOnlyList<Toggle> Toggles => values.Toggles;
    public Func<string>? UpscalerStatus { get; init; }
    /// <summary>What the Frame generation row reports as its state ("active", "off", ...); null on a host without that setting.</summary>
    public Func<string>? FrameGenerationStatus { get; init; }
    /// <summary>Applies a new attackers-per-target setting to the running combat system.</summary>
    public Action<int?>? AttackSlotsChanged { get; init; }
    /// <summary>Applies the late-dodges setting to the running combat system.</summary>
    public Action<bool>? LateDefencesChanged { get; init; }
    /// <summary>Applies the turret gunner facing fix to the running combat system.</summary>
    public Action<bool>? GunnerFacingChanged { get; init; }
    public Action<bool>? PunchReachChanged { get; init; }
    public Action<bool>? BenchFacingChanged { get; init; }
    /// <summary>Called when an off-screen switch changes (the config holds the new values).</summary>
    public Action? OffscreenChanged { get; init; }
    /// <summary>The Tutorials option changed (the config holds it).</summary>
    public Action<bool>? TutorialsChanged { get; init; }
    /// <summary>The Reset Tutorials button.</summary>
    public Action? ResetTutorials { get; init; }
    /// <summary>The door auto-close switch changed (<see cref="UserConfig.DoorsCloseBehind"/>).</summary>
    public Action<bool>? DoorsCloseChanged { get; init; }
    public Action<bool>? AnimalsPickUpChanged { get; init; }
    /// <summary>The fast speeds changed (<see cref="UserConfig.FastSpeed"/>, <see cref="UserConfig.FastestSpeed"/>).</summary>
    public Action? TimeSpeedsChanged { get; init; }
    /// <summary>Applies a new camera rotation speed (<see cref="UserConfig.CameraRotateSpeed"/>) to the camera.</summary>
    public Action<float>? CameraRotateSpeedChanged { get; init; }
    /// <summary>A camera option other than the rotation speed changed (invert X / Y, move speed, zoom speed; <see cref="UserConfig.ApplyCamera"/>).</summary>
    public Action? CameraChanged { get; init; }
    /// <summary>The person races of the loaded data with their skin default (<c>CharacterRenderer.PersonRaces</c>), listed on Graphics > Skin.</summary>
    public IReadOnlyList<(string Id, string Name, bool Default)> SkinRaceList { get; init; } = [];
    /// <summary>A race's skin was turned on or off (the config's <see cref="UserConfig.SkinRaces"/> holds it); the game hands it to the character renderer.</summary>
    public Action? SkinRacesChanged { get; init; }
    /// <summary>Called when a Gameplay setting changes (the config holds the new values); the game hands them to the running world.</summary>
    public Action? GameplayChanged { get; init; }
    /// <summary>Called when an audio slider changes (the config holds the new values); the game applies them to the running sound.</summary>
    public Action? AudioChanged { get; init; }
    public bool Visible
    {
        get => visible || menuProgress > 0;
        set
        {
            if (visible == value) return;
            visible = value;
            dragging = null; rebinding = null; openChoice = null; resetPending = false; naming = false; focus = -1; notice = "";
            editingNumber = null;
            if (value) { lastFrameAt = 0; player.SetSettingsOpen(true); }
            if (!value) save();
        }
    }

    internal static string Label(string label) => label == "Anti-aliasing" ? "Anti-aliasing/upscaling" : Regex.Replace(label.Split('(')[0].Trim(), @"\s+0-\d+.*$", "")
        .Replace(" x", " multiplier", StringComparison.Ordinal).Replace(": error px", "", StringComparison.Ordinal);

    /// <summary>The page a Tab panel slider is on: Display, Advanced, or a Graphics sub-page (<see cref="GraphicsPages"/>).</summary>
    internal static string Section(string label)
    {
        if (label is "Time of day" or "Time speed") return "Advanced";
        if (label is "VSync" or "Full screen" or MetalPresetSlider || WorldFrame.UpscalerSliders.Contains(label) || label == WorldFrame.DlssPresetSlider
            || label == WorldFrame.FilteringSlider || label == WorldFrame.TextureQualitySlider) return "Display";
        return GraphicsSection(label, false);
    }

    /// <summary>The page a Tab panel switch is on: a Graphics sub-page (<see cref="GraphicsPages"/>), or Advanced (Interior lamps).</summary>
    internal static string ToggleSection(string label) => GraphicsSection(label, true);

    /// <summary>
    /// A Graphics row, with its performance impact. A look row is marked when it differs from the game's (Faithful) or the active preset's value; a costly look switch
    /// Low holds off says so, and turning it on keeps it on at Low.
    /// </summary>
    Entry LookRow(Entry entry, string key, string label, bool toggle, HashSet<string> changes)
    {
        entry = entry with { Impact = ImpactOf(label), Raw = label };
        if (!IsLook(label, toggle)) return entry;
        if (toggle && entry.Toggle is { } cut && CutAtLow(label) && !cut.Get())
            return entry with
            {
                Warn = true, Hint = "Off at Low quality. Choose Meitou to keep it on at Low.",
                Toggle = cut with { Set = v => { if (v && !config.KeepAtLow.Contains(label)) config.KeepAtLow.Add(label); cut.Set(v); } },
            };
        if (toggle && entry.Toggle is { } kept && config.KeepAtLow.Contains(label))
            entry = entry with { Toggle = kept with { Set = v => { if (!v) config.KeepAtLow.Remove(label); kept.Set(v); } } };
        return entry with { Changed = changes.Contains(key), Reset = () => ResetLook(key) };
    }

    /// <summary>False for a ground relief slider whose effect is off: quality and depth need the parallax or the displacement, each distance its own.
    /// The displacement counts only where its switch is offered (tessellation; not on Metal).</summary>
    bool ReliefSliderShown(string label)
    {
        bool displace = GroundRelief.DisplaceActive && values.Toggles.Any(t => t.Label == "Ground displacement (tessellation)");
        return label switch
        {
            WorldFrame.GroundReliefQualitySlider or WorldFrame.GroundReliefDepthSlider => GroundRelief.ParallaxActive || displace,
            WorldFrame.GroundParallaxDistanceSlider => GroundRelief.ParallaxActive,
            WorldFrame.GroundDisplaceDistanceSlider => displace,
            _ => true,
        };
    }

    internal const string TickAheadHint = "Faithful runs the world, then draws it, as the game does. Meitou runs the next world tick while the frame is drawn "
        + "whenever the world is busy (big fights, crowds), Always on every frame: much higher frame rates with many characters, up to 33 ms more input delay.";
    const string FrameGenerationTemplate = "Shows a generated frame between each two rendered ones: nearly twice the frames shown, with a little more input delay. "
        + "FSR 3 (AMD's, any GPU) needs the FidelityFX library; DLSS (NVIDIA's, Windows, RTX 40 or later) needs Streamline's frame generation DLLs and turns Reflex on. "
        + "Both work with TAA, FSR and DLSS upscaling. A change takes effect after a restart. Now: {1}";

    internal static readonly string[] FrameGenerationChoices = ["Off", "FSR 3", "DLSS"];

    string FrameGenerationHint() => Loc.Tr(FrameGenerationTemplate, Loc.Tr(FrameGenerationStatus?.Invoke() ?? "off"));

    internal static readonly string[] TickAheadChoices = ["Faithful (in series)", "Meitou (when busy)", "Always on"];

    /// <summary>A Tab panel slider's key in the catalogue (<see cref="LauncherSchema.Find"/>): its label, or the top-level key of the display switches.</summary>
    static string CatalogueKey(string label) => label switch { "Full screen" => "fullScreen", "VSync" => "vSync", _ => label };

    /// <summary>
    /// A Tab panel slider's row, as the launcher shows it: name, range, scale direction, value text and choices come from the catalogue
    /// (<see cref="LauncherSchema.Find"/>); the panel's slider only reads and writes the value (and marks the Faithful / Meitou values).
    /// </summary>
    Entry SliderEntry(Slider slider)
    {
        string key = slider.Label;
        var d = LauncherSchema.Find(CatalogueKey(key));
        if (d is { Kind: global::Meitou.Settings.SettingKind.Slider, Labels: null })
            slider = slider with { Min = d.Min, Max = d.Max, Logarithmic = d.Logarithmic && d.Min > 0, Reversed = d.Reversed, Text = d.Text, Scale = d.Scale };
        // An on / off setting in the catalogue (full screen, VSync; colour grading's two choices) is a switch, whatever the panel's slider.
        if (d is { Kind: global::Meitou.Settings.SettingKind.Toggle } or { Labels.Length: 2 })
            return new(d?.Name ?? Label(key), SliderHint(slider), Toggle: new(key, () => slider.Get() >= 0.5f, v => slider.Set(v ? 1 : 0)), Raw: CatalogueKey(key));
        string[]? options = key == MetalPresetSlider
            ? ["Native 1.00", "MetalFX Super Quality 0.77", "MetalFX Quality 0.67", "MetalFX Balanced 0.59", "MetalFX Performance 0.50", "MetalFX Temporal 0.40", "MetalFX Spatial 0.50 (alternative)", "MetalFX Spatial 0.40 (alternative)", "TAA 0.50 (experimental)", "Custom"]
            : d?.Labels;
        string label = d?.Name ?? Label(key);
        string hint = key == MetalPresetSlider
            ? "MetalFX temporal is the default at every tier; spatial rows are explicit alternatives. The preset sets the render scale, so the separate scale slider is disabled."
            : key == WorldFrame.UpscalerSliders[0] ? Loc.Tr("Smooths edges and upscales the render. Active: {1}", Loc.Tr(UpscalerStatus?.Invoke() ?? "unavailable"))
            : SliderHint(slider);
        // The options are the slider's whole values from its left end.
        float first = d?.Min ?? slider.Min;
        return options is { } items ? new(label, hint, Choice: new(() => items, () => (int)MathF.Round(slider.Get() - first), i => slider.Set(i + first)), Raw: key)
            : new(label, hint, Slider: slider, Raw: key);
    }

    List<Entry> Entries()
    {
        string section = page == 1 ? GraphicsPages[graphicsPage] : Pages[page];
        // In the catalogue's order, the launcher's (LauncherSchema.Order).
        var result = values.Sliders.Where(s => s.Label is not ("Time of day" or "Time speed") && Section(s.Label) == section && ReliefSliderShown(s.Label))
            .OrderBy(s => LauncherSchema.Order(CatalogueKey(s.Label))).Select(SliderEntry).ToList();
        if (page == 1)
        {
            var changes = LookChanges();
            result = values.Toggles.Where(toggle => ToggleSection(toggle.Label) == section).OrderBy(toggle => LauncherSchema.Order(SwitchKey(toggle.Label)))
                .Select(toggle => LookRow(new Entry(Label(toggle.Label), ToggleHint(toggle.Label), Toggle: toggle), SwitchKey(toggle.Label), toggle.Label, true, changes))
                .Concat(values.Sliders.Where(s => s.Label is not ("Time of day" or "Time speed") && Section(s.Label) == section && ReliefSliderShown(s.Label))
                    .OrderBy(s => LauncherSchema.Order(s.Label)).Select(s => LookRow(SliderEntry(s), s.Label, s.Label, false, changes)))
                .ToList();
        }
        // Graphics > Characters, in the Skin feature: after the switch, one row per person race of the loaded data (mods too), on or off against its default.
        if (page == 1 && section == "Characters")
            foreach (var (id, name, byDefault) in SkinRaceList)
                result.Add(new(name, byDefault ? "Skin scatters light on this race (the default)." : "A robot race: no skin scattering unless turned on.",
                    Toggle: new(name, () => config.SkinRaces.TryGetValue(id, out bool on) ? on : byDefault, v =>
                    {
                        if (v == byDefault) config.SkinRaces.Remove(id); else config.SkinRaces[id] = v;
                        SkinRacesChanged?.Invoke();
                    }, OnOff: true), Raw: "race:" + id));
        if (page == 1) result = Grouped([.. result.OrderBy(e => e.Raw is { } raw ? GroupRank(raw) : FeatureGroups.Length)]);
        if (page == 0)
        {
            // The Display page in the catalogue's order, as the launcher's: full screen, VSync, frame limit, frame generation, world tick, then the display sliders.
            var fps = LauncherSchema.Find("fpsLimit");
            result.Add(new(fps?.Name ?? "Frame limit", fps?.Description ?? "", Slider: fps is null ? frameLimit
                : frameLimit with { Min = fps.Min, Max = fps.Max, Text = fps.Text }, Raw: "fpsLimit"));
            result.Add(new("World tick alongside drawing", TickAheadHint, Choice: new(() => TickAheadChoices, () => (int)config.TickAhead, i => config.TickAhead = (TickAheadMode)i),
                Raw: "tickAhead"));
            if (!OperatingSystem.IsMacOS())
                result.Add(new("Frame generation", FrameGenerationHint(), Choice: new(() => FrameGenerationChoices, () => (int)config.FrameGenerationMethod,
                    i => config.FrameGenerationMethod = (FrameGenerationMethod)i), Raw: "frameGeneration"));
            result.RemoveAll(e => !ShownNow(e));
            result = [.. result.OrderBy(e => LauncherSchema.Order(e.Raw ?? ""))];
            result = [.. result.SelectMany(ModeEntries)];
        }
        if (page == 2)
            result.Add(new("UI style", "Choose original Kenshi skins or Meitou materials.", Choice: new(() => ["Faithful", "Meitou"],
                () => (int)player.UiMode, i => player.SetUiMode((UiMode)i))));
        if (page == 2)
            result.Add(new("Speed units", "The unit of the run speed in the character's information panel (the game: mph).", Choice: new(() => ["mph", "km/h"],
                () => (int)config.SpeedUnit, i => player.SpeedUnit = config.SpeedUnit = (SpeedUnit)i)));
        if (page == 2)
            result.Add(new("Damage floaters", "Numbers that float over a character when a blow lands, with the damage armour stopped in brackets. Simple shows the total, Full one number per damage type "
                + "and the part hit.", Choice: new(() => ["Off", "Simple info", "Full info"],
                () => (int)config.Floaters, i => player.Floaters = config.Floaters = (FloaterMode)i)));
        if (page == 2)
            result.Add(new("Font size", "Points added to every interface font's default height (the game: 3).",
                Slider: new("Font size", UserConfig.MinFontSize, UserConfig.MaxFontSize, () => config.FontSize ?? global::Meitou.Data.Ui.KenshiFontSize.DefaultSetting,
                    v => { float size = Math.Clamp(MathF.Round(v), UserConfig.MinFontSize, UserConfig.MaxFontSize); config.FontSize = size; player.FontSize = size; }, "0", Faithful: global::Meitou.Data.Ui.KenshiFontSize.DefaultSetting)));
        if (page == 2)
            result.Add(new("Show names", "The name over each of your characters (the game's Show names option). Off, only the stealth and light icons stay.",
                Toggle: new("Show names", () => config.ShowNames, v => player.ShowNames = config.ShowNames = v, OnOff: true)));
        if (page == 2)
            result.Add(new("Show movement marker", "The green marker where a move order sends your characters (the game: on).",
                Toggle: new("Show movement marker", () => config.MovementMarker != false, v => player.MovementMarker = (config.MovementMarker = v) == true, OnOff: true)));
        if (page == 2)
            result.Add(new("Show rotation marker", "The green marker on the camera's pivot while you turn it (the game: on).",
                Toggle: new("Show rotation marker", () => config.RotationMarker != false, v => player.RotationMarker = (config.RotationMarker = v) == true, OnOff: true)));
        if (page == 2)
            result.Add(new("Censorship", "Swaps a few strong words in the dialogue for milder ones (the game's censorship option).",
                Toggle: new("Censorship", () => config.Censorship, v => config.Censorship = v, OnOff: true)));
        if (page == 2)
            result.Add(new("Feedback button", "The FEEDBACK button at the top right during play. Off, it still shows on the title screen and in the Esc menu.",
                Toggle: new("Feedback button", () => config.FeedbackButton, v => player.FeedbackButtonInPlay = config.FeedbackButton = v, OnOff: true)));
        if (page == 2)
            result.Add(new("Hardware mouse", "Use the Windows cursor instead of the game's drawn pointer (no hover pointers unless Enhanced hardware mouse is on).",
                Toggle: new("Hardware mouse", () => config.HardwareMouse, v => config.HardwareMouse = v)));
        if (page == 2 && config.HardwareMouse)
            result.Add(new("Enhanced hardware mouse", "The icon of what a right click would do beside the Windows cursor (none for a walk), and the hand over buttons and targets.",
                Toggle: new("Enhanced hardware mouse", () => config.EnhancedHardwareMouse, v => config.EnhancedHardwareMouse = v, OnOff: true), Depth: 1));
        if (page == 3)
            result.Add(new(CameraRotateSpeed.Label, "How fast the rotate and tilt keys and the mouse turn the camera (the game: 6).", Slider: CameraRotateSpeed));
        if (page == 3)
            result.Add(new(CameraMoveSpeed.Label, "How fast the arrow keys and the screen edge pan the camera (the game: 500; faster the higher the camera).", Slider: CameraMoveSpeed));
        if (page == 3)
            result.Add(new(CameraZoomSpeed.Label, "How fast the wheel and the zoom keys move the camera in and out (the game: 125).", Slider: CameraZoomSpeed));
        if (page == 3)
            result.Add(new("Invert mouse X", "Reverses the camera's turn, for the mouse and the keys (the game: off).",
                Toggle: new("Invert mouse X", () => config.InvertX == true, v => { config.InvertX = v; CameraChanged?.Invoke(); }, OnOff: true)));
        if (page == 3)
            result.Add(new("Invert mouse Y", "Reverses the camera's tilt, for the mouse and the keys (the game: on).",
                Toggle: new("Invert mouse Y", () => config.InvertY != false, v => { config.InvertY = v; CameraChanged?.Invoke(); }, OnOff: true)));
        if (page == 3)
            result.Add(new("Swap mouse buttons", "Swap the left and right mouse buttons (the game: off; the game view follows, as in the original).",
                Toggle: new("Swap mouse buttons", () => config.SwapMouseButtons == true, v => KeyMap.SwapButtons = (config.SwapMouseButtons = v) == true, OnOff: true)));
        if (page == 3)
            result.Add(new("Edge scrolling", "The camera moves when the pointer is at the edge of the screen.",
                Toggle: new("Edge scrolling", () => config.EdgeScroll != false, v => config.EdgeScroll = v)));
        if (page == 3)
            foreach (var action in Enum.GetValues<InputAction>().Where(a => a != InputAction.Quit && a != InputAction.ToggleFps))
                result.Add(new(ActionLabel(action), BindingHint(action), Binding: action));
        if (page == 5)
        {
            string[] hints = [
                "Volume of the biome music and the main theme.",
                "Volume of the weather and the world's ambience.",
                "Volume of footsteps.",
                "Volume of combat, building and other sound effects.",
                "Volume of the interface sounds.",
                "Volume of the voices.",
                "Average minutes of silence between pieces of biome music.",
                "The most sounds heard at once: the loudest play, quieter ones wait silently and come back when they are loud enough. Lower it if big fights stutter; All mixes every sound.",
                "How far away sounds are heard: 1x is the original, 4x hears fights, footsteps and machines four times as far. Higher values mix more sounds at once.",
            ];
            for (int i = 0; i < AudioSliders.Length; i++)
                result.Add(new(AudioSliders[i].Label, hints[i], Slider: AudioSliders[i]));
            result.Add(new("Hear from", "Camera: you hear the world where the camera is. Character: you hear it where your first selected character stands (from the camera while nobody is selected).",
                Toggle: new("Hear from", () => config.HearFromSelected, v => config.HearFromSelected = v, Text: () => config.HearFromSelected ? "Character" : "Camera")));
        }
        if (page == 6)
        {
            string[] hints = [
                "Size of the squads made from now on: townspeople, roaming squads and raids (the game: 1). Affects performance.",
                "How many squads roam and live in the wild (the game: 1). Affects performance.",
                "How many fighters a raid on your towns gathers (the game: 1).",
                "How often factions start raids on your towns (the game: 1).",
            ];
            for (int i = 0; i < GameplaySliders.Length; i++)
                result.Add(new(GameplaySliders[i].Label, hints[i], Slider: GameplaySliders[i]));
            result.Add(new("Town attacks frequency", "How long a faction waits between attacks on your towns by squads passing by (the game: Normal). Never stops them.",
                Choice: new(() => GameplaySettings.TownAttackLabels, () => (config.Gameplay ??= new()).TownAttacks,
                    i => SetGameplay((config.Gameplay ?? new()) with { TownAttacks = i }))));
            result.Add(new("Dismemberment", "When a destroyed limb is cut off: Rare only by a single heavy blow, Frequent also when a wound worsens (the game: Rare).",
                Choice: new(() => GameplaySettings.DismembermentLabels, () => (int)(config.Gameplay ??= new()).Dismemberment,
                    i => SetGameplay((config.Gameplay ?? new()) with { Dismemberment = (global::Meitou.Simulation.Bodies.Dismemberment)i }))));
            result.Add(new("Auto save", "Saves the game on its own every few minutes of unpaused play, into three slots in turn (autosave0 to autosave2).",
                Toggle: new("Auto save", () => (config.Gameplay ??= new()).AutoSave, v => SetGameplay((config.Gameplay ?? new()) with { AutoSave = v }), OnOff: true)));
            result.Add(new(AutoSaveSlider.Label, "Minutes of unpaused play between autosaves (the game: 10).", Slider: AutoSaveSlider));
            result.Add(new("Fast zone hopping", "Keeps a larger area loaded round each of your characters, so switching between distant ones skips most of the loading (the game: off; uses more memory).",
                Toggle: new("Fast zone hopping", () => (config.Gameplay ??= new()).FastZoneHopping, v => SetGameplay((config.Gameplay ?? new()) with { FastZoneHopping = v }), OnOff: true)));
            result.Add(new("Tutorials", "Shows the tutorial hints as you play (the game: on).",
                Toggle: new("Tutorials", () => config.Tutorials != false, v => { config.Tutorials = v; TutorialsChanged?.Invoke(v); }, OnOff: true)));
            result.Add(new("Reset Tutorials", "Every tutorial can show again, the ones already seen included.", Run: () => ResetTutorials?.Invoke()));
        }
        if (page == 7)
        {
            // Bugs of the game as it ships; on fixes them, off plays as the game does.
            result.Add(new("Raids as written", "Raid and campaign data that builds on a base campaign counts the base's squads and follow-up campaigns once. "
                + "The game counts them twice, so inherited squads come up more often and follow-up raids are about twice as likely.",
                Toggle: new("Raids as written", () => config.CampaignAncestorsOnce, v => { config.CampaignAncestorsOnce = v; OffscreenChanged?.Invoke(); }, Remaster: true, OnOff: true)));
            result.Add(new("Door break sound", "Doors and gates make their breaking sound when they are bashed or broken open. "
                + "The game never picks which breaking sound to play, so it plays none.",
                Toggle: new("Door break sound", () => config.FixDoorBreak, v => config.FixDoorBreak = v, Remaster: true, OnOff: true)));
            result.Add(new("Ambience after loading", "A biome whose sounds were not loaded yet starts its day or night ambience by the time of day. "
                + "The game plays the opposite one (night sounds by day) until the next biome or day change.",
                Toggle: new("Ambience after loading", () => config.FixAmbienceSuffix, v => config.FixAmbienceSuffix = v, Remaster: true, OnOff: true)));
            result.Add(new("Wind stops", "The wind's sound ends when the wind dies down. The game asks for a stop sound that does not exist, so once the wind has blown it never goes quiet.",
                Toggle: new("Wind stops", () => config.FixWindStop, v => config.FixWindStop = v, Remaster: true, OnOff: true)));
            result.Add(new("Load button", "The load window's Load button works only once a save that can be read is selected. "
                + "The game keeps it live: with nothing selected a click does nothing, and an unreadable save fails to load.",
                Toggle: new("Load button", () => config.FixLoadButton, v => player.FixLoadButton = config.FixLoadButton = v, Remaster: true, OnOff: true)));
            result.Add(new("Turret gunners turn", "A turret's gunner turns with the gun, facing where it aims. "
                + "In the game the gunner keeps facing the turret, so once the gun swings toward a target the gunner looks off to the side.",
                Toggle: new("Turret gunners turn", () => config.FixGunnerFacing, v => { config.FixGunnerFacing = v; GunnerFacingChanged?.Invoke(v); }, Remaster: true, OnOff: true)));
            result.Add(new("Weak punch reach", "Fighters with martial arts of 20 or less throw their weak punch from a normal punching distance. "
                + "The game's data gives it a negative reach, so they throw it only when nearly touching their target.",
                Toggle: new("Weak punch reach", () => config.FixPunchReach, v => { config.FixPunchReach = v; PunchReachChanged?.Invoke(v); }, Remaster: true, OnOff: true)));
            result.Add(new("Bench facing", "A character on a bench with the table behind it sits facing away from the table. "
                + "The game mirrors the bench's look point through the middle of the world map, so the sitter faces an unrelated direction.",
                Toggle: new("Bench facing", () => config.FixBenchFacing, v => { config.FixBenchFacing = v; BenchFacingChanged?.Invoke(v); }, Remaster: true, OnOff: true)));
            result.Add(new("Walls inside buildings", "Seen into a building your characters are in, parts of other buildings that run through it are left out, such as the Hub's town wall in the Shinobi Thieves tower. "
                + "The game's world places them there and draws them inside the room.",
                Toggle: new("Walls inside buildings", () => config.FixPartsInBuildings, v => config.FixPartsInBuildings = v, Remaster: true, OnOff: true)));        }
        if (page == 4)
        {
            // A graphics switch that changes play: the stealth sight counts the lamps it lights, so no look preset or Faithful sets it.
            if (values.Toggles.FirstOrDefault(t => t.Label == "Interior lamps") is { } lamps) result.Add(new("Interior lamps", ToggleHint(lamps.Label), Toggle: lamps));
            result.Add(new(AttackSlotsSlider.Label, "How many enemies may swing at one character at once (the game: 1, plus its race's extra slots); the others wait at reach or circle.", Slider: AttackSlotsSlider));
            result.Add(new(HarpoonLimitSlider.Label, "How many bolts and harpoons stay stuck in the ground and buildings, per kind of bolt; past it the one that flew longest disappears (the game: 60).", Slider: HarpoonLimitSlider));
            result.Add(new("Late dodges and blocks", "Balance change: a dodge or block that succeeds its roll but starts a moment late still works, "
                + "instead of failing as in the game. Makes skilled fighters much harder to wear down when several enemies attack at once.",
                Toggle: new("Late dodges and blocks", () => config.LateDefences, v => { config.LateDefences = v; LateDefencesChanged?.Invoke(v); }, Remaster: true, OnOff: true)));
            result.Add(new("Squads keep their people", "Roaming squads keep their characters when they leave the area around your squads, and travel on as themselves. "
                + "In the game they are removed and new ones are made later.",
                Toggle: new("Squads keep their people", () => config.KeepRoamers, v => { config.KeepRoamers = v; OffscreenChanged?.Invoke(); }, Remaster: true, OnOff: true)));
            result.Add(new("Fights far away", "Hostile squads that meet far from your squads fight it out by strength; the loser is wiped out. In the game nothing happens far away.",
                Toggle: new("Fights far away", () => config.OffscreenFights, v => { config.OffscreenFights = v; OffscreenChanged?.Invoke(); }, Remaster: true, OnOff: true)));
            result.Add(new("Healing far away", "A squad coming back into the area around your squads has healed for the time it was away. In the game it comes back as it left.",
                Toggle: new("Healing far away", () => config.OffscreenHealing, v => { config.OffscreenHealing = v; OffscreenChanged?.Invoke(); }, Remaster: true, OnOff: true)));
            result.Add(new("Wild squads stay", "Animals, bandits and other wild squads keep their characters, wounds and places when the area around them unloads, and are there again when you come back. "
                + "In the game they are removed and new ones are made.",
                Toggle: new("Wild squads stay", () => config.KeepWild, v => { config.KeepWild = v; OffscreenChanged?.Invoke(); }, Remaster: true, OnOff: true)));
            result.Add(new("Towns keep their schedule", "Townspeople you come back to after an hour or more are where their day puts them: in bed at night, at the counter, at their posts and seats. "
                + "In the game they are where they stood when you left.",
                Toggle: new("Towns keep their schedule", () => config.ResidentSchedule, v => { config.ResidentSchedule = v; OffscreenChanged?.Invoke(); }, Remaster: true, OnOff: true)));
            result.Add(new("Production far away", "Farms, mines, wells and generators away from your squads keep working. In the game they stop while their area is unloaded, and only farms catch up when you come back.",
                Toggle: new("Production far away", () => config.UnloadedProduction, v => { config.UnloadedProduction = v; OffscreenChanged?.Invoke(); }, Remaster: true, OnOff: true)));
            result.Add(new("Doors close behind", "A door someone opened by walking through it closes again a few seconds after the last person went through. In the game doors stay open.",
                Toggle: new("Doors close behind", () => config.DoorsCloseBehind, v => { config.DoorsCloseBehind = v; DoorsCloseChanged?.Invoke(v); }, Remaster: true, OnOff: true)));
            result.Add(new("Decapitation", "A blow that destroys a character's head cuts it off, by the same rule the Dismemberment setting cuts off limbs. In the game a destroyed head only kills.",
                Toggle: new("Decapitation", () => config.Decapitation, v => { config.Decapitation = v; GameplayChanged?.Invoke(); }, Remaster: true, OnOff: true)));
            result.Add(new("Advanced ragdolls", "A character knocked down or killed by a blow falls with its own momentum and is pushed away from the hit, harder the heavier the blow and most at the part that was struck, its arms and legs flung by it, and a cut-off head flies the same way. Limp bodies have joints that ease to a stop and a slight muscle tone, so they settle into natural poses. In the game a body drops where it stood, its joints stopping hard.",
                Toggle: new("Advanced ragdolls", () => config.AdvancedRagdolls, v => { config.AdvancedRagdolls = v; GameplayChanged?.Invoke(); }, Remaster: true)));
            result.Add(new(FastSpeedSlider.Label, "The game speed of the second speed button (two arrows) and its key (the game: 2x).", Slider: FastSpeedSlider));
            result.Add(new(FastestSpeedSlider.Label, "The game speed of the third speed button (three arrows) and its key (the game: 5x). "
                + "High speeds need a fast processor; when it cannot keep up the game runs slower than chosen.", Slider: FastestSpeedSlider));
            result.Add(new("Pause after loading", "A save starts paused once it has loaded, so nothing happens before you look around. In the game it runs at once.",
                Toggle: new("Pause after loading", () => config.PauseOnLoad, v => config.PauseOnLoad = v, OnOff: true)));
            result.Add(new("Animals pick up items", "Your pack animals pick up an item you point them at, into the bag they wear (or wear a bag they pick up). Off, only people pick items up.",
                Toggle: new("Animals pick up items", () => config.AnimalsPickUp, v => { config.AnimalsPickUp = v; AnimalsPickUpChanged?.Invoke(v); }, OnOff: true)));
            result.Add(new("Hover outline", "Outline and tint the character under the cursor, in the colour of what a right click would do.",
                Toggle: new("Hover outline", () => config.HoverOutline, v => config.HoverOutline = v, Remaster: true, OnOff: true)));
            result.Add(new("Building outline", "Outline and tint the selected building, and the building under the cursor in the colour of what a right click would do, like a character under the cursor, "
                + "except while its interior is shown.",
                Toggle: new("Building outline", () => config.BuildingOutline, v => config.BuildingOutline = v, Remaster: true, OnOff: true)));
            result.Add(new("Click away deselects", "A left click on the ground ends the selection of the selected building and closes its panels. In the game the building stays selected.",
                Toggle: new("Click away deselects", () => config.DeselectBuildings, v => player.DeselectBuildings = config.DeselectBuildings = v, Remaster: true, OnOff: true)));
            result.Add(new("luckyren Mode", "A character that takes a fatal hit explodes, covers everything around in blood and sends bones flying everywhere. Its gear falls to the ground around the spot.",
                Toggle: new("luckyren Mode", () => config.LuckyrenMode, v => config.LuckyrenMode = v),
                Picture: SettingsMenuArt.LuckyrenImage));
            result.Add(new(GroundItemsSlider.Label, "How many items dropped during play (severed limbs, luckyren Mode's gear) stay on the ground; past it the oldest disappears. "
                + "Items that were on the ground in the save always stay.", Slider: GroundItemsSlider));
            result.Add(new("Splash screens", "Faithful shows the game's logos at startup (Faithful UI); a key or a click skips each one. Meitou shows the menu as soon as the game has loaded.",
                Toggle: new("Splash screens", () => !config.SplashScreens, v => config.SplashScreens = !v, Remaster: true)));
            result.Add(new("Main menu scene transitions", "Sky flight to/from the main menu (Meitou UI).",
                Toggle: new("Main menu scene transitions", () => config.MainMenuTransitions, v => config.MainMenuTransitions = v)));
            result.Add(new(mainMenuTransitionSpeed.Label, "Higher values make the camera ascend and descend faster.", Slider: mainMenuTransitionSpeed));
            result.Add(new("In-game scene transitions", "Sky flight for save loads/new games during play (Meitou UI).",
                Toggle: new("In-game scene transitions", () => config.InGameTransitions, v => config.InGameTransitions = v)));
            result.Add(new(inGameTransitionSpeed.Label, "Higher values make the camera ascend and descend faster.", Slider: inGameTransitionSpeed));
            result.Add(new("Character flights", "A double click on a squad member far from the view (or a double tap of their number) flies the camera there in an arc instead of cutting.",
                Toggle: new("Character flights", () => config.CharacterFlights, v => config.CharacterFlights = v, Remaster: true, OnOff: true)));
            result.Add(new("Keep textures at characters", "Keeps the meshes and textures around every squad member loaded, not only around the camera, so a flight or jump to them lands on finished detail. Costs video memory.",
                Toggle: new("Keep textures at characters", () => config.KeepTexturesAtCharacters, v => config.KeepTexturesAtCharacters = v, Remaster: true, OnOff: true)));
            if (config.KeepTexturesAtCharacters)
                result.Add(new("Keep textures at all squads", "Also keeps the surroundings of your other squads' members loaded, not only the squad on the squad bar. Costs more video memory.",
                    Toggle: new("Keep textures at all squads", () => config.KeepTexturesAtAllSquads, v => config.KeepTexturesAtAllSquads = v, OnOff: true)));
            result.Add(new(characterFlightTime.Label, "Seconds a character flight takes, and each hop of the squad tour (numpad /).", Slider: characterFlightTime));
            foreach (var action in values.Actions)
                result.Add(new(action.Label, "Delete cached billboards; they are rebuilt when needed.", Run: () =>
                {
                    pendingAction = action.Run; confirmTitle = "Clear cached files?"; confirmation = action.Question(); focus = -1;
                }));
        }
        if (page == 4)
            result.Insert(0, PageSidesEntry(result, "All gameplay changes", "Every change on this page as the game plays, or every Meitou change.", "gameplay change"));
        if (page == 7)
            result.Insert(0, PageSidesEntry(result, "All fixes", "Every bug on this page as the game ships it, or every fix.", "fix"));
        return result;
    }

    /// <summary>False for a row the catalogue shows only while another setting is at a value (<c>ShownWhen</c>: the DLSS preset) and that one is not.</summary>
    bool ShownNow(Entry entry) => entry.Raw is not { } key || LauncherSchema.Find(key)?.ShownWhen is not { } when
        || values.Sliders.FirstOrDefault(s => s.Label == when.Key) is not { } other || when.Matches(other.Get());

    static string ActionLabel(InputAction action) => Regex.Replace(action.ToString(), "(?<=[a-z0-9])(?=[A-Z])|(?<=[a-z])(?=[0-9])", " ");
    Func<string>? pendingAction;
    string confirmation = "", confirmTitle = "";

    public bool Contains(Vector2 _) => Visible;

    public bool MouseDown(Vector2 at, MouseButton button = MouseButton.Left)
    {
        if (!Visible) return false;
        pointer = at;
        pointerDown = button == MouseButton.Left;
        scrollTarget = scroll;
        if (rebinding is { } action)
        {
            if (button == MouseButton.Left && hits.LastOrDefault(h => h.Bounds.Contains(at.X, at.Y)) is { } cancel)
            { cancel.Activate(); return true; }
            SetBinding(action, button);
            return true;
        }
        if (button != MouseButton.Left) return true;
        if (editingNumber is not null)
        {
            if (numberBounds.Contains(at.X, at.Y)) return true;
            if (!CommitNumber()) return true;
        }
        var hit = hits.LastOrDefault(h => h.Bounds.Contains(at.X, at.Y));
        if (hit is null) { openChoice = null; return true; }
        focus = hits.IndexOf(hit);
        if (hit.Slider is not null) { dragging = hit; MouseMove(at); }
        else hit.Activate();
        return true;
    }

    public bool MouseMove(Vector2 at)
    {
        pointer = at;
        if (!Visible) return false;
        if (dragging is { Slider: { } slider, Track: { } track }) slider.SetFraction((at.X - track.X) / track.Width);
        return true;
    }

    public bool MouseUp() { bool had = dragging is not null; dragging = null; pointerDown = false; return had; }

    public bool Scroll(float steps)
    {
        if (!Visible) return false;
        if (editingNumber is not null || rebinding is not null || resetPending || pendingAction is not null) return true;
        if (openChoice is { } choice) choiceScroll = Math.Clamp(choiceScroll - (int)steps, 0, Math.Max(0, choice.Options().Count - 6));
        else if (page == ModsPage && ModsDetail.Contains(pointer.X, pointer.Y)) modsDetailScroll -= steps * 40;
        else scrollTarget = Math.Clamp(scrollTarget - steps * 60 * scale, 0, maxScroll);
        return true;
    }

    public bool KeyDown(Key key, bool shift)
    {
        if (!Visible) return false;
        if (rebinding is { } action)
        {
            if (key == Key.Escape) rebinding = null;
            else if (key == Key.Delete || key == Key.Backspace) { bindings.Set(action); rebinding = null; notice = "Binding cleared."; }
            else SetBinding(action, key);
            return true;
        }
        if (key == Key.Escape)
        {
            if (pendingAction is not null || resetPending) { pendingAction = null; resetPending = false; }
            else if (openChoice is not null) openChoice = null;
            else Visible = false;
            return true;
        }
        if (openChoice is { } choice)
        {
            if (key is Key.Up or Key.Down)
            {
                choiceSelection = Math.Clamp(choiceSelection + (key == Key.Up ? -1 : 1), 0, choice.Options().Count - 1);
                choiceScroll = Math.Clamp(choiceSelection - 3, 0, Math.Max(0, choice.Options().Count - 6));
            }
            if (key == Key.Enter) { choice.Choose(choiceSelection); openChoice = null; }
            return true;
        }
        if (key is Key.PageDown or Key.PageUp) Scroll(key == Key.PageUp ? 5 : -5);
        if (hits.Count == 0) return true;
        if (key is Key.Tab or Key.Up or Key.Down)
            focus = (focus + (key == Key.Up || key == Key.Tab && shift ? -1 : 1) + hits.Count) % hits.Count;
        else if (focus >= 0 && focus < hits.Count)
        {
            var hit = hits[focus];
            if (hit.Slider is { } slider && key is Key.Left or Key.Right)
                slider.SetFraction(slider.Fraction + (key == Key.Left ? -1 : 1) * slider.KeyStep);
            else if (key is Key.Enter or Key.Space) hit.Activate();
        }
        return true;
    }

    public bool KeyDown(Key key, bool shift, bool ctrl)
    {
        if (naming && Visible) return NameKey(key);
        if (editingNumber is null) return KeyDown(key, shift);
        switch (key)
        {
            case Key.Escape: editingNumber = null; notice = ""; break;
            case Key.Enter: CommitNumber(); break;
            case Key.Tab:
                if (CommitNumber()) KeyDown(key, shift);
                break;
            case Key.A when ctrl: numberAnchor = 0; numberCaret = numberText.Length; break;
            case Key.Home: numberCaret = 0; if (!shift) numberAnchor = numberCaret; break;
            case Key.End: numberCaret = numberText.Length; if (!shift) numberAnchor = numberCaret; break;
            case Key.Left:
                numberCaret = !shift && numberCaret != numberAnchor ? Math.Min(numberCaret, numberAnchor) : Math.Max(0, numberCaret - 1);
                if (!shift) numberAnchor = numberCaret;
                break;
            case Key.Right:
                numberCaret = !shift && numberCaret != numberAnchor ? Math.Max(numberCaret, numberAnchor) : Math.Min(numberText.Length, numberCaret + 1);
                if (!shift) numberAnchor = numberCaret;
                break;
            case Key.Backspace:
            case Key.Delete:
                if (numberCaret == numberAnchor)
                {
                    if (key == Key.Backspace && numberCaret > 0) numberAnchor--;
                    if (key == Key.Delete && numberCaret < numberText.Length) numberAnchor++;
                }
                ReplaceNumberSelection("");
                break;
        }
        return true;
    }

    public void TextInput(char value)
    {
        if (naming && Visible) { NameText(value); return; }
        if (editingNumber is null || !Visible) return;
        if (value is >= '0' and <= '9' or '.' or ',' or '-' or '+' or 'e' or 'E')
            ReplaceNumberSelection(value == ',' ? "." : value.ToString());
    }

    void BeginNumber(Slider slider, UiRect at)
    {
        editingNumber = slider;
        numberText = MathF.Round(slider.Get() * slider.Scale, 4).ToString("G", CultureInfo.InvariantCulture);
        numberAnchor = 0;
        numberCaret = numberText.Length;
        numberBounds = at;
        scrollTarget = scroll;
        dragging = null;
        notice = "";
    }

    void ReplaceNumberSelection(string replacement)
    {
        int start = Math.Min(numberCaret, numberAnchor), end = Math.Max(numberCaret, numberAnchor);
        if (numberText.Length - (end - start) + replacement.Length > 24) return;
        numberText = numberText[..start] + replacement + numberText[end..];
        numberCaret = numberAnchor = start + replacement.Length;
        notice = "";
    }

    bool CommitNumber()
    {
        if (editingNumber is not { } slider) return true;
        if (!float.TryParse(numberText, NumberStyles.Float, CultureInfo.InvariantCulture, out float value) || !float.IsFinite(value))
        {
            notice = "Enter a valid number, or press Esc to cancel.";
            return false;
        }
        slider.Set(Math.Clamp(value / slider.Scale, slider.Min, slider.Max));
        editingNumber = null;
        notice = "";
        return true;
    }

    void SetBinding(InputAction action, Binding binding)
    {
        if (!binding.IsMouse && binding.Key is Key.Escape or Key.F6 or Key.LeftAlt or Key.RightAlt or Key.LeftControl or Key.RightControl or Key.LeftShift or Key.RightShift)
        {
            notice = "That key is reserved. Choose another key, or Esc to cancel.";
            return;
        }
        var conflicts = Enum.GetValues<InputAction>().Where(a => a != action && bindings.Get(a).Contains(binding)).ToArray();
        // A mouse button taken from another action is swapped: that action gets this one's old button, so the three buttons can be shuffled (the buttons form a cycle otherwise).
        if (binding.IsMouse && conflicts.Length == 1 && bindings.Get(action).Where(b => b.IsMouse).Cast<Binding?>().FirstOrDefault() is { } previous)
        {
            var other = conflicts[0];
            bindings.Set(other, [.. bindings.Get(other).Where(b => b != binding), previous]);
            bindings.Set(action, binding);
            rebinding = null;
            notice = $"{ActionLabel(action)} bound to {binding}; {ActionLabel(other)} moved to {previous}.";
            return;
        }
        if (conflicts.Length > 0)
        {
            notice = $"{binding} is assigned to {ActionLabel(conflicts[0])}. Choose another key, or Esc to cancel.";
            return;
        }
        bindings.Set(action, binding);
        rebinding = null;
        notice = $"{ActionLabel(action)} bound to {binding}.";
    }

    void Reset()
    {
        for (int i = 0; i < values.Sliders.Count; i++)
            if (values.Sliders[i].Label is not ("Time of day" or "Time speed")) values.Sliders[i].Set(defaults[i]);
        for (int i = 0; i < values.Toggles.Count; i++)
            if (values.Toggles[i].Get() != toggleDefaults[i]) values.Toggles[i].Set(toggleDefaults[i]);
        frameLimit.Set(240);
        ResetModSettings();
        config.SplashScreens = false;
        config.MainMenuTransitions = true;
        config.InGameTransitions = true;
        config.MainMenuTransitionSpeed = 1.5f;
        config.InGameTransitionSpeed = 1.5f;
        config.CharacterFlights = false;
        config.KeepTexturesAtCharacters = true;
        config.KeepTexturesAtAllSquads = false;
        config.CharacterFlightSeconds = UserConfig.DefaultFlightSeconds;
        AttackSlotsSlider.Set(UserConfig.FaithfulAttackSlots);
        config.LateDefences = false;
        LateDefencesChanged?.Invoke(false);
        config.KeepRoamers = config.OffscreenFights = config.OffscreenHealing = config.CampaignAncestorsOnce = config.KeepWild = config.ResidentSchedule = config.UnloadedProduction = true;
        config.DoorsCloseBehind = true;
        config.Decapitation = true;
        config.AdvancedRagdolls = true;
        DoorsCloseChanged?.Invoke(true);
        config.PauseOnLoad = false;
        config.AnimalsPickUp = false;
        AnimalsPickUpChanged?.Invoke(false);
        config.FixDoorBreak = config.FixAmbienceSuffix = config.FixWindStop = true;
        player.FixLoadButton = config.FixLoadButton = true;
        config.FixGunnerFacing = true;
        GunnerFacingChanged?.Invoke(true);
        config.FixPunchReach = true;
        PunchReachChanged?.Invoke(true);
        config.FixBenchFacing = true;
        BenchFacingChanged?.Invoke(true);
        OffscreenChanged?.Invoke();
        config.Audio = new();
        AudioChanged?.Invoke();
        SetGameplay(new());
        config.HardwareMouse = false;
        config.EnhancedHardwareMouse = true;
        CameraRotateSpeed.Set(global::Meitou.Data.World.KenshiCamera.DefaultRotateSpeed);
        config.EdgeScroll = true;
        config.InvertX = false;
        config.InvertY = true;
        config.CameraMoveSpeed = global::Meitou.Data.World.KenshiCamera.DefaultMoveSpeed;
        config.CameraZoomSpeed = global::Meitou.Data.World.KenshiCamera.DefaultZoomSpeed;
        CameraChanged?.Invoke();
        config.Tutorials = true;
        player.MovementMarker = player.RotationMarker = true;
        config.MovementMarker = config.RotationMarker = true;
        TutorialsChanged?.Invoke(true);
        config.HoverOutline = true;
        config.BuildingOutline = true;
        player.DeselectBuildings = config.DeselectBuildings = true;
        config.LuckyrenMode = false;
        config.GroundItemLimit = global::Meitou.Simulation.Items.GroundItems.DefaultLimit;
        (config.FastSpeed, config.FastestSpeed) = (SimulationClock.DefaultSpeeds[1], SimulationClock.DefaultSpeeds[2]);
        TimeSpeedsChanged?.Invoke();
        player.SetUiMode(UiMode.Meitou);
        player.SpeedUnit = config.SpeedUnit = SpeedUnit.Mph;
        player.Floaters = config.Floaters = FloaterMode.Simple;
        player.ShowNames = config.ShowNames = true;
        player.FeedbackButtonInPlay = config.FeedbackButton = true;
        config.TickAhead = TickAheadMode.Dynamic;
        bindings.ResetToDefaults();
        config.LookPreset = null;
        config.FaithfulLook = false;
        config.KeepAtLow.Clear();
        activeLook = null;
        customModes.Clear();
        resetPending = false;
        notice = "Default settings restored.";
    }
}
