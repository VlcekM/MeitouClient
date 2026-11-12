using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using Meitou.Data.Ui;
using Meitou.Game.Effects;
using Meitou.Game.Host;
using Meitou.Game.Host.Input;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Overlays;
using Meitou.Game.Rendering.Post;
using Meitou.Game.Rendering.Terrain;
using Meitou.Game.Settings;
using Meitou.Modding;
using Meitou.Simulation.Session;

namespace Meitou.Game;

/// <summary>
/// Shared settings layout and interaction; the current UI mode selects only its presentation.
/// </summary>
sealed class SettingsMenu(PlayerInterface player, SettingsPanel values, InputBindings bindings, Action save, Slider frameLimit, UserConfig config)
{
    #region State

    /// <summary>
    /// The player's interface (its painter and the menu pause).
    /// </summary>
    internal readonly PlayerInterface Player = player;
    /// <summary>
    /// The panel's sliders and switches the rows edit.
    /// </summary>
    internal readonly SettingsPanel Values = values;
    /// <summary>
    /// The key bindings the Controls page edits.
    /// </summary>
    internal readonly InputBindings Bindings = bindings;
    /// <summary>
    /// The player's settings the pages read and write.
    /// </summary>
    internal readonly UserConfig Config = config;
    /// <summary>
    /// The pages, in their buttons' order (the indices of <see cref="Page"/>).
    /// </summary>
    static readonly string[] Pages =
        ["Display", "Graphics", "Interface", "Controls", "Advanced", "Audio", "Gameplay", "Fixes", "Mods"];
    /// <summary>
    /// The MODS page (<see cref="Settings.ModsPage"/>): the loaded mods, each with the settings its code registered.
    /// </summary>
    internal const int ModsPageIndex = 8;
    /// <summary>
    /// The Graphics tabs: the look tabs, then the quality tabs (<see cref="GraphicsSection"/>).
    /// </summary>
    internal static readonly string[] GraphicsPages = ["Lighting", "Sky & weather", "Colour", "World", "Characters"];
    /// <summary>
    /// The Metal-only preset row (Native / MetalFX quality, balanced, performance / TAA), edited by the Metal settings panel.
    /// </summary>
    internal const string MetalPresetSlider = "Metal upscaling";
    /// <summary>
    /// The Metal-only free render scale under the preset's upscaler, shown with the scene's size in pixels.
    /// </summary>
    internal const string MetalRenderScaleSlider = "Metal render scale";
    /// <summary>
    /// The sliders' values when the menu was made: what Restore defaults puts back.
    /// </summary>
    internal readonly float[] Defaults = values.Sliders.Select(s => s.Get()).ToArray();
    /// <summary>
    /// The switches' values when the menu was made: what Restore defaults puts back.
    /// </summary>
    internal readonly bool[] ToggleDefaults = values.Toggles.Select(t => t.Get()).ToArray();
    /// <summary>
    /// The clickable widgets as the last frame drew them.
    /// </summary>
    internal readonly List<Hit> Hits = [];
    /// <summary>
    /// The controls the last frame drew.
    /// </summary>
    internal readonly List<UiControl> Controls = [];
    /// <summary>
    /// The menu's pictures.
    /// </summary>
    internal readonly SettingsMenuArt Art = new();
    /// <summary>
    /// The menu is open (it may still be sliding in or out), and Restore defaults waits for its confirmation.
    /// </summary>
    internal bool MenuOpen, ResetPending;
    /// <summary>
    /// The page shown (<see cref="Pages"/>), the Graphics tab, and the row the keys focus (-1: none).
    /// </summary>
    internal int Page, GraphicsPage, Focus = -1;
    /// <summary>
    /// The page's scroll (shown and aimed at), its limit, and the window's scale (1 at 1120 × 720).
    /// </summary>
    internal float ScrollOffset, ScrollTarget, MaxScroll, Scale = 1;
    /// <summary>
    /// The left button is down.
    /// </summary>
    internal bool PointerDown;
    /// <summary>
    /// The pointer, pixels.
    /// </summary>
    internal Vector2 Pointer;
    /// <summary>
    /// The page's scrolled area and the window, pixels.
    /// </summary>
    internal UiRect Viewport, Bounds;
    /// <summary>
    /// The slider being dragged; null when none.
    /// </summary>
    Hit? _dragging;
    /// <summary>
    /// While the scroll bar's thumb is dragged, the grab point's distance below the thumb's top, pixels; null when it is not.
    /// </summary>
    float? _thumbGrab;
    /// <summary>
    /// The action waiting for a key or button to bind; null when none.
    /// </summary>
    internal InputAction? Rebinding;
    /// <summary>
    /// The open drop-down; null when none.
    /// </summary>
    internal Choice? ActiveChoice;
    /// <summary>
    /// The drop-down's highlighted option and its scroll.
    /// </summary>
    internal int ChoiceSelection, ChoiceScroll;
    /// <summary>
    /// The footer's notice; empty for none.
    /// </summary>
    internal string Notice = "";
    /// <summary>
    /// The slider whose value is being typed; null when none.
    /// </summary>
    internal Slider? EditingNumber;
    /// <summary>
    /// The typed value's text.
    /// </summary>
    internal string NumberText = "";
    /// <summary>
    /// The typed value's caret and the other end of its selection.
    /// </summary>
    internal int NumberCaret, NumberAnchor;
    /// <summary>
    /// Where the typed value's box is.
    /// </summary>
    internal UiRect NumberBounds;

    /// <summary>
    /// Groups the look switches into features.
    /// </summary>
    internal FeatureGrouping Grouping => field ??= new(this);
    /// <summary>
    /// The Faithful / Meitou sides of the settings.
    /// </summary>
    internal SettingModes Modes => field ??= new();
    /// <summary>
    /// The menu's opening, page and row motion.
    /// </summary>
    internal MenuMotion Motion => field ??= new(this);
    /// <summary>
    /// The Interface, Controls, Advanced, Audio, Gameplay and Fixes pages' rows.
    /// </summary>
    internal GamePages GamePages => field ??= new(this);
    /// <summary>
    /// The Graphics quality presets.
    /// </summary>
    internal QualityTiers Tiers => field ??= new(this);
    /// <summary>
    /// The look presets.
    /// </summary>
    internal LookPresets LookPresets => field ??= new(this);
    /// <summary>
    /// The Graphics page's look and quality rows.
    /// </summary>
    internal LookBar LookBar => field ??= new(this);
    /// <summary>
    /// The dialog that names a look preset.
    /// </summary>
    internal PresetNamingDialog PresetNaming => field ??= new(this);
    /// <summary>
    /// The MODS page.
    /// </summary>
    internal ModsPage ModsPage => field ??= new(this);
    /// <summary>
    /// Draws the menu's controls.
    /// </summary>
    internal SettingsCanvas Canvas => field ??= new(this);

    /// <summary>
    /// A drop-down: its options, the chosen one, and what choosing does.
    /// </summary>
    internal sealed record Choice(Func<IReadOnlyList<string>> Options, Func<int> Current, Action<int> Choose);

    /// <summary>
    /// A row of a page: its label and hint, and the slider, switch, drop-down, binding or action it edits, with how it is drawn.
    /// </summary>
    internal sealed record Entry(string Label, string Hint = "", Slider? Slider = null, Toggle? Toggle = null, Choice? Choice = null,
        InputAction? Binding = null, Action? Run = null, string? Picture = null, bool Segmented = false, bool Changed = false,
        string? Impact = null, bool Warn = false, string? Raw = null, int Depth = 0, bool? Open = null, string? GroupId = null, Action? Reset = null,
        bool Locked = false);

    /// <summary>
    /// A clickable widget as the last frame drew it: its bounds, what a click does, and a slider's track.
    /// </summary>
    internal sealed record Hit(UiRect Bounds, Action Activate, Slider? Slider = null, UiRect? Track = null, string Label = "");

    /// <summary>
    /// The names <c>ui-open settings:&lt;name&gt;</c> takes: a page, or <c>graphics:&lt;sub-page&gt;</c>; compared ignoring case (spaces and "&amp;"
    /// count).
    /// </summary>
    internal static IEnumerable<string> PageNames =>
        Pages.Select(p => p.ToLowerInvariant()).Concat(GraphicsPages.Select(p => "graphics:" + p.ToLowerInvariant()));

    /// <summary>
    /// The page shown now, as <see cref="PageNames"/> spells it.
    /// </summary>
    internal string PageName => Page == 1 ? "graphics:" + GraphicsPages[GraphicsPage].ToLowerInvariant() : Pages[Page].ToLowerInvariant();

    /// <summary>
    /// Switches to a page by name (see <see cref="PageNames"/>); false when there is none.
    /// </summary>
    internal bool TryOpenPage(string name)
    {
        string[] parts = name.Split(':', 2);
        int index = Array.FindIndex(Pages, p => p.Equals(parts[0], StringComparison.OrdinalIgnoreCase));
        if (index < 0) return false;

        int sub = parts.Length == 2 ? Array.FindIndex(GraphicsPages, p => p.Equals(parts[1], StringComparison.OrdinalIgnoreCase)) : GraphicsPage;
        if (parts.Length == 2 && (index != 1 || sub < 0)) return false;

        Navigate(index);
        GraphicsPage = sub;
        return true;
    }

    /// <summary>
    /// The labelled clickable widgets as the last frame drew them (agent control channel: ui-tree, ui-click); sliders are left out.
    /// </summary>
    internal IReadOnlyList<(string Label, UiRect Bounds, Action Activate)> Clickables =>
        [.. Hits.Where(h => h.Label.Length > 0 && h.Slider is null).Select(h => (h.Label, h.Bounds, h.Activate))];

    /// <summary>
    /// Whether <paramref name="p"/> is over a widget a click acts on in the last frame (the enhanced Hardware mouse's hand).
    /// </summary>
    public bool OverClickable(Vector2 p) => Visible && Hits.Any(h => h.Bounds.Contains(p.X, p.Y));

    /// <summary>
    /// The sliders as the last frame drew them, with the track they sit on (agent control channel: ui-tree, ui-set).
    /// </summary>
    internal IReadOnlyList<(Slider Slider, UiRect Bounds)> SliderHits => [.. Hits.Where(h => h.Slider is not null).Select(h => (h.Slider!, h.Bounds))];

    /// <summary>
    /// The panel's sliders.
    /// </summary>
    public IReadOnlyList<Slider> Sliders => Values.Sliders;

    /// <summary>
    /// The panel's switches.
    /// </summary>
    public IReadOnlyList<Toggle> Toggles => Values.Toggles;

    /// <summary>
    /// Whether the menu shows (open, or still sliding out); setting it opens or closes it, dropping what was under way, and closing saves.
    /// </summary>
    public bool Visible
    {
        get => MenuOpen || Motion.MenuProgress > 0;
        set
        {
            if (MenuOpen == value) return;

            MenuOpen = value;
            _dragging = null;
            _thumbGrab = null;
            Rebinding = null;
            ActiveChoice = null;
            ResetPending = false;
            PresetNaming.Open = false;
            Focus = -1;
            Notice = "";
            EditingNumber = null;

            if (value)
            {
                Motion.LastFrameAt = 0;
                Player.SetSettingsOpen(true);
            }

            if (!value) save();
        }
    }

    /// <summary>
    /// Set by the showcase: presets are neither read from nor written to disk.
    /// </summary>
    internal bool NoDiskWrites { get; init; }

    /// <summary>
    /// Opens the menu on the Graphics page's tab <paramref name="tab"/>.
    /// </summary>
    internal void ShowGraphicsTab(int tab)
    {
        Visible = true;
        Page = 1;
        GraphicsPage = tab;
        ActiveChoice = null;
        PresetNaming.Open = false;
        ScrollOffset = ScrollTarget = 0;
    }

    /// <summary>
    /// Opens a drop-down at <paramref name="at"/>, its chosen option highlighted.
    /// </summary>
    internal void OpenChoice(Choice choice, UiRect at)
    {
        ActiveChoice = choice;
        ChoiceSelection = Math.Clamp(choice.Current(), 0, Math.Max(0, choice.Options().Count - 1));
        ChoiceScroll = Math.Max(0, ChoiceSelection - 3);
        Canvas.ChoiceAnchor = at;
        Focus = -1;
    }

    /// <summary>
    /// Shows page <paramref name="next"/> from its top.
    /// </summary>
    internal void Navigate(int next)
    {
        Page = next;
        ScrollOffset = ScrollTarget = 0;
        Motion.ContentProgress = 0;
        Focus = -1;
        ActiveChoice = null;
        Rebinding = null;
        Notice = "";
    }

    /// <summary>
    /// Sets the look switch <paramref name="label"/> (the showcase).
    /// </summary>
    internal void ShowcaseSwitch(string label, bool on)
    {
        if (Values.Toggles.FirstOrDefault(t => t.Label == label) is { } t) t.Set(on);
    }

    /// <summary>
    /// Sets the slider <paramref name="label"/> (the showcase).
    /// </summary>
    internal void ShowcaseSet(string label, float value)
    {
        if (Values.Sliders.FirstOrDefault(s => s.Label == label) is { } s) s.Set(value);
    }

    #endregion

    #region Host hooks

    /// <summary>
    /// What the upscaler rows report as their state; null on a host without them.
    /// </summary>
    public Func<string>? UpscalerStatus { get; init; }

    /// <summary>
    /// What the Frame generation row reports as its state ("active", "off", ...); null on a host without that setting.
    /// </summary>
    public Func<string>? FrameGenerationStatus { get; init; }

    /// <summary>
    /// Applies a new attackers-per-target setting to the running combat system.
    /// </summary>
    public Action<int?>? AttackSlotsChanged { get; init; }

    /// <summary>
    /// Applies the late-dodges setting to the running combat system.
    /// </summary>
    public Action<bool>? LateDefencesChanged { get; init; }

    /// <summary>
    /// Applies the turret gunner facing fix to the running combat system.
    /// </summary>
    public Action<bool>? GunnerFacingChanged { get; init; }

    /// <summary>
    /// Applies the punch reach fix (<see cref="UserConfig.FixPunchReach"/>) to the running combat system.
    /// </summary>
    public Action<bool>? PunchReachChanged { get; init; }

    /// <summary>
    /// Applies the bench facing fix (<see cref="UserConfig.FixBenchFacing"/>).
    /// </summary>
    public Action<bool>? BenchFacingChanged { get; init; }

    /// <summary>
    /// Called when an off-screen switch changes (the config holds the new values).
    /// </summary>
    public Action? OffscreenChanged { get; init; }

    /// <summary>
    /// The Tutorials option changed (the config holds it).
    /// </summary>
    public Action<bool>? TutorialsChanged { get; init; }

    /// <summary>
    /// The Reset Tutorials button.
    /// </summary>
    public Action? ResetTutorials { get; init; }

    /// <summary>
    /// The door auto-close switch changed (<see cref="UserConfig.DoorsCloseBehind"/>).
    /// </summary>
    public Action<bool>? DoorsCloseChanged { get; init; }

    /// <summary>
    /// The animals' pick-up switch changed (<see cref="UserConfig.AnimalsPickUp"/>).
    /// </summary>
    public Action<bool>? AnimalsPickUpChanged { get; init; }

    /// <summary>
    /// The fast speeds changed (<see cref="UserConfig.FastSpeed"/>, <see cref="UserConfig.FastestSpeed"/>).
    /// </summary>
    public Action? TimeSpeedsChanged { get; init; }

    /// <summary>
    /// Applies a new camera rotation speed (<see cref="UserConfig.CameraRotateSpeed"/>) to the camera.
    /// </summary>
    public Action<float>? CameraRotateSpeedChanged { get; init; }

    /// <summary>
    /// A camera option other than the rotation speed changed (invert X / Y, smoothing, move speed, zoom speed; <see cref="UserConfig.ApplyCamera"/>).
    /// </summary>
    public Action? CameraChanged { get; init; }

    /// <summary>
    /// The person races of the loaded data with their skin default (<c>CharacterRenderer.PersonRaces</c>), listed on Graphics > Skin.
    /// </summary>
    public IReadOnlyList<(string Id, string Name, bool Default)> SkinRaceList { get; init; } = [];

    /// <summary>
    /// A race's skin was turned on or off (the config's <see cref="UserConfig.SkinRaces"/> holds it); the game hands it to the character renderer.
    /// </summary>
    public Action? SkinRacesChanged { get; init; }

    /// <summary>
    /// Called when a Gameplay setting changes (the config holds the new values); the game hands them to the running world.
    /// </summary>
    public Action? GameplayChanged { get; init; }

    /// <summary>
    /// Called when an audio slider changes (the config holds the new values); the game applies them to the running sound.
    /// </summary>
    public Action? AudioChanged { get; init; }

    /// <summary>
    /// The registered settings; read each time the page is drawn, since mods load after the menu may be made.
    /// </summary>
    public Func<IReadOnlyList<ModSettingsGroup>> ModSettings { get; init; } = () => [];

    #endregion

    #region Rows

    /// <summary>
    /// The prefix of a switch's key in <c>meitou.user.json</c>'s <c>graphics</c> (the Faithful / Meitou ones and the plain on/off ones alike).
    /// </summary>
    internal const string SwitchKeyPrefix = "Meitou: ";
    /// <summary>
    /// The Tick ahead row's hint.
    /// </summary>
    internal const string TickAheadHint =
        "Faithful runs the world, then draws it, as the game does. Meitou runs the next world tick while the frame is drawn "
        + "whenever the world is busy (big fights, crowds), Always on every frame: much higher frame rates with many characters, "
        + "up to 33 ms more input delay.";
    /// <summary>
    /// The Frame generation row's hint, its state in place of {1}.
    /// </summary>
    const string FrameGenerationTemplate =
        "Shows a generated frame between each two rendered ones: nearly twice the frames shown, with a little more input delay. "
        + "FSR 3 (AMD's, any GPU) needs the FidelityFX library; DLSS (NVIDIA's, Windows, RTX 40 or later) needs Streamline's "
        + "frame generation DLLs and turns Reflex on. "
        + "Both work with TAA, FSR and DLSS upscaling. A change takes effect after a restart. Now: {1}";
    /// <summary>
    /// The Frame generation row's choices.
    /// </summary>
    internal static readonly string[] FrameGenerationChoices = ["Off", "FSR 3", "DLSS"];
    /// <summary>
    /// The Tick ahead row's choices.
    /// </summary>
    internal static readonly string[] TickAheadChoices = ["Faithful (in series)", "Meitou (when busy)", "Always on"];

    /// <summary>
    /// A row's label without its range or its numbering; Anti-aliasing reads Anti-aliasing/upscaling.
    /// </summary>
    internal static string Label(string label) =>
        label == "Anti-aliasing" ? "Anti-aliasing/upscaling" : Regex.Replace(label.Split('(')[0].Trim(), @"\s+0-\d+.*$", "")
        .Replace(" x", " multiplier", StringComparison.Ordinal).Replace(": error px", "", StringComparison.Ordinal);

    /// <summary>
    /// The page a Tab panel slider is on: Display, Advanced, or a Graphics sub-page (<see cref="GraphicsPages"/>).
    /// </summary>
    internal static string Section(string label)
    {
        if (label is "Time of day" or "Time speed") return "Advanced";
        if (label is "VSync" or "Full screen" or MetalPresetSlider or MetalRenderScaleSlider || WorldFrame.UpscalerSliders.Contains(label) || label == WorldFrame.DlssPresetSlider
            || label == WorldFrame.FilteringSlider || label == WorldFrame.TextureQualitySlider) return "Display";

        return GraphicsSection(label, false);
    }

    /// <summary>
    /// The page a Tab panel switch is on: a Graphics sub-page (<see cref="GraphicsPages"/>), or Advanced (Interior lamps).
    /// </summary>
    internal static string ToggleSection(string label) => GraphicsSection(label, true);

    /// <summary>
    /// The Graphics tab a setting sits on, by feature (Display and Advanced settings are sorted out before this): a feature's switch, look settings,
    /// quality settings and distances on one tab, in its group (<see cref="FeatureGrouping.FeatureGroups"/>). Interior lamps is on Advanced; the
    /// anti-aliasing and weather particles switches are hidden.
    /// </summary>
    internal static string GraphicsSection(string label, bool toggle)
    {
        bool S(string prefix) => label.StartsWith(prefix, StringComparison.Ordinal);

        if (toggle)
        {
            return label switch
            {
                "Interior lamps" => "Advanced",
                "Anti-aliasing" or "Weather particles" => "Hidden",
                "Shadows" or "Indirect light" or "Indoor GI" or "Ambient occlusion" or "Light shafts" or "Planetshine"
                    or WorldFrame.FifthCascadeSwitch => "Lighting",
                "Haze" or "Night air" or "Night sky" or "Clouds" or "Cloud shadows" or "Cloud shadows in fog" or "Heat haze"
                    or "Fog" => "Sky & weather",
                "Tone map and grade" or "Night grading" or "Dithering" or WorldFrame.BloomSwitch => "Colour",
                "Skin" or "Character levels" or "Item icons" => "Characters",
                _ => "World",
            };
        }

        if (S("Ambient") || S("Night ambient") || S("Light shafts") || S("Planetshine") || S("Shadow"))
            return "Lighting";
        if (S("Haze") || S("Heat haze") || WorldFrame.ParticleSliders.Contains(label)) return "Sky & weather";
        if (S("Tone map") || S("Hybrid") || S("Grading") || S("Night grading") || S("Bloom")) return "Colour";
        if (label == WorldFrame.CharacterDistanceSlider) return "Characters";

        return "World";
    }

    /// <summary>
    /// The config key of a look switch's label.
    /// </summary>
    internal static string SwitchKey(string label) => SwitchKeyPrefix + label;

    /// <summary>
    /// False for a ground relief slider whose effect is off: quality and depth need the parallax or the displacement, each distance its own. The
    /// displacement counts only where its switch is offered (tessellation; not on Metal).
    /// </summary>
    bool ReliefSliderShown(string label)
    {
        bool displace = GroundRelief.DisplaceActive && Values.Toggles.Any(t => t.Label == "Ground displacement (tessellation)");
        return label switch
        {
            WorldFrame.GroundReliefQualitySlider or WorldFrame.GroundReliefDepthSlider => GroundRelief.ParallaxActive || displace,
            WorldFrame.GroundParallaxDistanceSlider => GroundRelief.ParallaxActive,
            WorldFrame.GroundDisplaceDistanceSlider => displace,
            _ => true,
        };
    }

    /// <summary>
    /// The Frame generation row's hint with its state now.
    /// </summary>
    string FrameGenerationHint() => Loc.Tr(FrameGenerationTemplate, Loc.Tr(FrameGenerationStatus?.Invoke() ?? "off"));

    /// <summary>
    /// A Tab panel slider's key in the catalogue (<see cref="LauncherSchema.Find"/>): its label, or the top-level key of the display switches.
    /// </summary>
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
        {
            slider = slider with { Min = d.Min, Max = d.Max, Logarithmic = d.Logarithmic
                && d.Min > 0, Reversed = d.Reversed, Text = d.Text, Scale = d.Scale };
        }

        // An on / off setting in the catalogue (full screen, VSync; colour grading's two choices) is a switch, whatever the panel's slider.
        if (d is { Kind: global::Meitou.Settings.SettingKind.Toggle } or { Labels.Length: 2 })
        {
            return new(d?.Name ?? Label(key), SettingHints.SliderHint(slider),
                Toggle: new(key, () => slider.Get() >= 0.5f, v => slider.Set(v ? 1 : 0)), Raw: CatalogueKey(key));
        }

        string[]? options = key == MetalPresetSlider
            ? ["Native 1.00", "MetalFX Super Quality 0.77", "MetalFX Quality 0.67", "MetalFX Balanced 0.59", "MetalFX Performance 0.50",
                "MetalFX Temporal 0.40", "MetalFX Spatial 0.50 (alternative)", "MetalFX Spatial 0.40 (alternative)", "TAA 0.50 (experimental)",
                "Custom"]
            : d?.Labels;
        string label = d?.Name ?? Label(key);
        string hint = key == MetalPresetSlider
            ? "MetalFX temporal is the default at every tier; spatial rows are explicit alternatives. The preset sets the render scale, "
                + "and the render scale slider fine-tunes it (Custom)."
            : key == WorldFrame.UpscalerSliders[0]
                ? Loc.Tr("Smooths edges and upscales the render. Active: {1}", Loc.Tr(UpscalerStatus?.Invoke() ?? "unavailable"))
            : SettingHints.SliderHint(slider);

        // The options are the slider's whole values from its left end.
        float first = d?.Min ?? slider.Min;
        return options is { } items
            ? new(label, hint, Choice: new(() => items, () => (int)MathF.Round(slider.Get() - first), i => slider.Set(i + first)), Raw: key)
            : new(label, hint, Slider: slider, Raw: key);
    }

    /// <summary>
    /// The rows of the page shown.
    /// </summary>
    internal List<Entry> Entries()
    {
        if (Page == 1) return GraphicsRows(GraphicsPages[GraphicsPage]);

        // In the catalogue's order, the launcher's (LauncherSchema.Order).
        var result = Values.Sliders
            .Where(s => s.Label is not ("Time of day" or "Time speed") && Section(s.Label) == Pages[Page] && ReliefSliderShown(s.Label))
            .OrderBy(s => LauncherSchema.Order(CatalogueKey(s.Label)))
            .Select(SliderEntry)
            .ToList();

        switch (Page)
        {
            case 0:
                return DisplayRows(result);
            case 2:
                GamePages.InterfaceRows(result);
                break;
            case 3:
                GamePages.ControlsRows(result);
                break;
            case 4:
                GamePages.AdvancedRows(result);
                break;
            case 5:
                GamePages.AudioRows(result);
                break;
            case 6:
                GamePages.GameplayRows(result);
                break;
            case 7:
                GamePages.FixesRows(result);
                break;
        }

        return result;
    }

    /// <summary>
    /// A Graphics tab's rows: its switches, then its sliders (look rows marked against the look), grouped by feature.
    /// </summary>
    List<Entry> GraphicsRows(string section)
    {
        var changes = LookPresets.LookChanges();
        var result = Values.Toggles
            .Where(toggle => ToggleSection(toggle.Label) == section)
            .OrderBy(toggle => LauncherSchema.Order(SwitchKey(toggle.Label)))
            .Select(toggle => LookPresets.LookRow(new Entry(Label(toggle.Label), SettingHints.ToggleHint(toggle.Label), Toggle: toggle),
                SwitchKey(toggle.Label), toggle.Label, true, changes))
            .Concat(Values.Sliders
                .Where(s => s.Label is not ("Time of day" or "Time speed") && Section(s.Label) == section && ReliefSliderShown(s.Label))
                .OrderBy(s => LauncherSchema.Order(s.Label))
                .Select(s => LookPresets.LookRow(SliderEntry(s), s.Label, s.Label, false, changes)))
            .ToList();

        // Graphics > Characters, in the Skin feature: after the switch, one row per person race of the loaded data (mods too), on or off against its
        // default.
        if (section == "Characters")
        {
            foreach (var (id, name, byDefault) in SkinRaceList)
            {
                string hint = byDefault ? "Skin scatters light on this race (the default)." : "A robot race: no skin scattering unless turned on.";
                result.Add(new(name, hint,
                    Toggle: new(name, () => Config.SkinRaces.TryGetValue(id, out bool on) ? on : byDefault, v =>
                    {
                        if (v == byDefault)
                            Config.SkinRaces.Remove(id);
                        else
                            Config.SkinRaces[id] = v;

                        SkinRacesChanged?.Invoke();
                    }, OnOff: true), Raw: "race:" + id));
            }
        }

        return Grouping.Grouped([.. result.OrderBy(e => e.Raw is { } raw ? FeatureGrouping.GroupRank(raw) : FeatureGrouping.FeatureGroups.Length)]);
    }

    /// <summary>
    /// The Display page: its sliders, the frame limit, world tick and frame generation, in the catalogue's order; modes replace their sliders.
    /// </summary>
    List<Entry> DisplayRows(List<Entry> result)
    {
        // The Display page in the catalogue's order, as the launcher's: full screen, VSync, frame limit, frame generation, world tick, then the
        // display sliders.
        var fps = LauncherSchema.Find("fpsLimit");
        result.Add(new(fps?.Name ?? "Frame limit", fps?.Description ?? "", Slider: fps is null ? frameLimit
            : frameLimit with { Min = fps.Min, Max = fps.Max, Text = fps.Text }, Raw: "fpsLimit"));
        result.Add(new("World tick alongside drawing", TickAheadHint, Choice: new(() => TickAheadChoices, () => (int)Config.TickAhead,
            i => Config.TickAhead = (TickAheadMode)i),
            Raw: "tickAhead"));

        if (!OperatingSystem.IsMacOS())
        {
            result.Add(new("Frame generation", FrameGenerationHint(), Choice: new(() => FrameGenerationChoices, () => (int)Config.FrameGenerationMethod,
                i => Config.FrameGenerationMethod = (FrameGenerationMethod)i), Raw: "frameGeneration"));
        }

        result.RemoveAll(e => !ShownNow(e));
        result = [.. result.OrderBy(e => LauncherSchema.Order(e.Raw ?? ""))];
        result = [.. result.SelectMany(Modes.ModeEntries)];
        return result;
    }

    /// <summary>
    /// False for a row the catalogue shows only while another setting is at a value (<c>ShownWhen</c>: the DLSS preset) and that one is not.
    /// </summary>
    bool ShownNow(Entry entry) => entry.Raw is not { } key || LauncherSchema.Find(key)?.ShownWhen is not { } when
        || Values.Sliders.FirstOrDefault(s => s.Label == when.Key) is not { } other || when.Matches(other.Get());

    /// <summary>
    /// An action's name for its row: its words spaced.
    /// </summary>
    internal static string ActionLabel(InputAction action) => Regex.Replace(action.ToString(), "(?<=[a-z0-9])(?=[A-Z])|(?<=[a-z])(?=[0-9])", " ");

    /// <summary>
    /// A page's all-Faithful / all-Meitou switch over its Faithful / Meitou <paramref name="toggles"/> and the <paramref name="sliders"/> with both
    /// marks; it reads 1 when all are Meitou, 0 when all are Faithful, -1 when they are mixed.
    /// </summary>
    Entry SidesEntry(string label, string hint, string what, IReadOnlyList<Toggle> toggles, IReadOnlyList<Slider> sliders) =>
        new(label, hint, Segmented: true,
        Choice: new(() => ["Faithful", "Meitou"], () =>
        {
            var live = toggles.Where(t => !LookPresets.StuckSwitches.Contains(t.Label)).ToList();

            static bool AtMark(Slider s, float? mark) => mark is { } m && MathF.Abs(s.Get() - m) <= 0.002f * (s.Max - s.Min);

            return live.All(t => t.Get()) && sliders.All(s => AtMark(s, s.Meitou)) ? 1
                : live.All(t => !t.Get()) && sliders.All(s => AtMark(s, s.Faithful)) ? 0
                : -1;
        }, side =>
        {
            bool meitou = side == 1;

            foreach (var toggle in toggles.Where(t => t.Get() != meitou))
            {
                toggle.Set(meitou);
                if (toggle.Get() != meitou) LookPresets.StuckSwitches.Add(toggle.Label);
            }

            foreach (var slider in sliders) slider.Set((meitou ? slider.Meitou : slider.Faithful)!.Value);

            Notice = meitou ? $"Every {what} is Meitou." : $"Every {what} is as the game ships it.";
        }));

    /// <summary>
    /// The Advanced or Fixes page's all switch, over that page's Faithful / Meitou switches (not the plain on/off ones such as luckyren Mode) and
    /// sliders with both marks.
    /// </summary>
    internal Entry PageSidesEntry(List<Entry> entries, string label, string hint, string what) => SidesEntry(label, hint, what,
        entries.Select(e => e.Toggle).OfType<Toggle>().Where(t => t.Remaster).ToList(),
        entries.Select(e => e.Slider).OfType<Slider>().Where(s => s.Faithful is not null && s.Meitou is not null).ToList());

    #endregion

    #region Input

    /// <summary>
    /// The menu takes every point while it shows.
    /// </summary>
    public bool Contains(Vector2 _) => Visible;

    /// <summary>
    /// A button press: a binding being set takes it, a typed value is committed first, then the hit widget acts (a slider starts its drag); true
    /// while the menu shows.
    /// </summary>
    public bool MouseDown(Vector2 at, MouseButton button = MouseButton.Left)
    {
        if (!Visible) return false;

        Pointer = at;
        PointerDown = button == MouseButton.Left;
        ScrollTarget = ScrollOffset;

        if (Rebinding is { } action)
        {
            if (button == MouseButton.Left && Hits.LastOrDefault(h => h.Bounds.Contains(at.X, at.Y)) is { } cancel)
            {
                cancel.Activate();
                return true;
            }

            SetBinding(action, button);
            return true;
        }

        if (button != MouseButton.Left) return true;

        if (EditingNumber is not null)
        {
            if (NumberBounds.Contains(at.X, at.Y)) return true;
            if (!CommitNumber()) return true;
        }

        if (GrabScrollBar(at)) return true;

        var hit = Hits.LastOrDefault(h => h.Bounds.Contains(at.X, at.Y));

        if (hit is null)
        {
            ActiveChoice = null;
            return true;
        }

        Focus = Hits.IndexOf(hit);

        if (hit.Slider is not null)
        {
            _dragging = hit;
            MouseMove(at);
        }
        else hit.Activate();

        return true;
    }

    /// <summary>
    /// A pointer move: a dragged slider follows it.
    /// </summary>
    public bool MouseMove(Vector2 at)
    {
        Pointer = at;
        if (!Visible) return false;
        if (_dragging is { Slider: { } slider, Track: { } track }) slider.SetFraction((at.X - track.X) / track.Width);
        else if (_thumbGrab is not null) DragScrollBar(at);

        return true;
    }

    /// <summary>
    /// The button's release ends a slider's or the scroll bar's drag; true when one was dragged.
    /// </summary>
    public bool MouseUp()
    {
        bool had = _dragging is not null || _thumbGrab is not null;
        _dragging = null;
        _thumbGrab = null;
        PointerDown = false;
        return had;
    }

    /// <summary>
    /// A press on the page's scroll bar starts dragging its thumb (one beside the thumb first moves it there, centred on the pointer); false when
    /// the press is elsewhere, the page does not scroll or a drop-down or dialog is open.
    /// </summary>
    bool GrabScrollBar(Vector2 at)
    {
        if (MaxScroll <= 0 || ActiveChoice is not null || ResetPending || PendingAction is not null || PresetNaming.Open) return false;

        var track = ScrollTrack();
        var zone = new UiRect(track.X - 8 * Scale, track.Y, track.Width + 16 * Scale, track.Height);
        if (!zone.Contains(at.X, at.Y)) return false;

        var thumb = ScrollThumb();
        _thumbGrab = at.Y >= thumb.Y && at.Y < thumb.Y + thumb.Height ? at.Y - thumb.Y : thumb.Height / 2;
        DragScrollBar(at);
        return true;
    }

    /// <summary>
    /// Scrolls the page so the dragged thumb's grab point is under the pointer.
    /// </summary>
    void DragScrollBar(Vector2 at)
    {
        var track = ScrollTrack();
        float room = track.Height - ScrollThumb().Height;
        if (room <= 0 || _thumbGrab is not { } grab) return;

        ScrollOffset = ScrollTarget = Math.Clamp((at.Y - grab - track.Y) / room * MaxScroll, 0, MaxScroll);
    }

    /// <summary>
    /// A wheel step: the drop-down, the mod details or the page scroll; true while the menu shows.
    /// </summary>
    public bool Scroll(float steps)
    {
        if (!Visible) return false;
        if (EditingNumber is not null || Rebinding is not null || ResetPending || PendingAction is not null)
            return true;
        if (ActiveChoice is { } choice)
            ChoiceScroll = Math.Clamp(ChoiceScroll - (int)steps, 0, Math.Max(0, choice.Options().Count - 6));
        else if (Page == ModsPageIndex && ModsPage.ModsDetail.Contains(Pointer.X, Pointer.Y))
            ModsPage.ModsDetailScroll -= steps * 40;
        else ScrollTarget = Math.Clamp(ScrollTarget - steps * 60 * Scale, 0, MaxScroll);

        return true;
    }

    /// <summary>
    /// A key with no typed value: a binding being set takes it, Esc closes the dialog, the drop-down or the menu, a drop-down takes its keys, then
    /// Page Up / Down scroll and the focus keys move and act; true while the menu shows.
    /// </summary>
    public bool KeyDown(Key key, bool shift)
    {
        if (!Visible) return false;

        if (Rebinding is { } action)
        {
            if (key == Key.Escape) Rebinding = null;
            else if (key == Key.Delete || key == Key.Backspace)
            {
                Bindings.Set(action);
                Rebinding = null;
                Notice = "Binding cleared.";
            }
            else SetBinding(action, key);

            return true;
        }

        if (key == Key.Escape)
        {
            if (PendingAction is not null || ResetPending)
            {
                PendingAction = null;
                ResetPending = false;
            }
            else if (ActiveChoice is not null) ActiveChoice = null;
            else Visible = false;

            return true;
        }

        if (ActiveChoice is { } choice)
        {
            if (key is Key.Up or Key.Down)
            {
                ChoiceSelection = Math.Clamp(ChoiceSelection + (key == Key.Up ? -1 : 1), 0, choice.Options().Count - 1);
                ChoiceScroll = Math.Clamp(ChoiceSelection - 3, 0, Math.Max(0, choice.Options().Count - 6));
            }

            if (key == Key.Enter)
            {
                choice.Choose(ChoiceSelection);
                ActiveChoice = null;
            }

            return true;
        }

        if (key is Key.PageDown or Key.PageUp) Scroll(key == Key.PageUp ? 5 : -5);
        if (Hits.Count == 0) return true;

        if (key is Key.Tab or Key.Up or Key.Down)
            Focus = (Focus + (key == Key.Up || key == Key.Tab && shift ? -1 : 1) + Hits.Count) % Hits.Count;
        else if (Focus >= 0 && Focus < Hits.Count)
        {
            var hit = Hits[Focus];
            if (hit.Slider is { } slider && key is Key.Left or Key.Right)
                slider.SetFraction(slider.Fraction + (key == Key.Left ? -1 : 1) * slider.KeyStep);
            else if (key is Key.Enter or Key.Space) hit.Activate();
        }

        return true;
    }

    /// <summary>
    /// A key: the preset name dialog, else the typed value's keys (Esc, Enter, Tab, the caret and selection), else as without Ctrl.
    /// </summary>
    public bool KeyDown(Key key, bool shift, bool ctrl)
    {
        if (PresetNaming.Open && Visible) return PresetNaming.OnKey(key);
        if (EditingNumber is null) return KeyDown(key, shift);

        switch (key)
        {
            case Key.Escape:
                EditingNumber = null;
                Notice = "";
                break;
            case Key.Enter:
                CommitNumber();
                break;
            case Key.Tab:
                if (CommitNumber()) KeyDown(key, shift);
                break;
            case Key.A when ctrl:
                NumberAnchor = 0;
                NumberCaret = NumberText.Length;
                break;
            case Key.Home:
                NumberCaret = 0;
                if (!shift) NumberAnchor = NumberCaret;
                break;
            case Key.End:
                NumberCaret = NumberText.Length;
                if (!shift) NumberAnchor = NumberCaret;
                break;
            case Key.Left:
                NumberCaret = !shift && NumberCaret != NumberAnchor ? Math.Min(NumberCaret, NumberAnchor) : Math.Max(0, NumberCaret - 1);
                if (!shift) NumberAnchor = NumberCaret;
                break;
            case Key.Right:
                NumberCaret = !shift && NumberCaret != NumberAnchor
                    ? Math.Max(NumberCaret, NumberAnchor)
                    : Math.Min(NumberText.Length, NumberCaret + 1);
                if (!shift) NumberAnchor = NumberCaret;
                break;
            case Key.Backspace:
            case Key.Delete:
                if (NumberCaret == NumberAnchor)
                {
                    if (key == Key.Backspace && NumberCaret > 0) NumberAnchor--;
                    if (key == Key.Delete && NumberCaret < NumberText.Length) NumberAnchor++;
                }

                ReplaceNumberSelection("");
                break;
        }

        return true;
    }

    /// <summary>
    /// A typed character: to the preset name dialog, else to a typed value (digits and the number's signs).
    /// </summary>
    public void TextInput(char value)
    {
        if (PresetNaming.Open && Visible)
        {
            PresetNaming.OnText(value);
            return;
        }

        if (EditingNumber is null || !Visible) return;
        if (value is >= '0' and <= '9' or '.' or ',' or '-' or '+' or 'e' or 'E')
            ReplaceNumberSelection(value == ',' ? "." : value.ToString());
    }

    /// <summary>
    /// Starts typing a slider's value in the box at <paramref name="at"/>, all of it selected.
    /// </summary>
    internal void BeginNumber(Slider slider, UiRect at)
    {
        EditingNumber = slider;
        NumberText = MathF.Round(slider.Get() * slider.Scale, 4).ToString("G", CultureInfo.InvariantCulture);
        NumberAnchor = 0;
        NumberCaret = NumberText.Length;
        NumberBounds = at;
        ScrollTarget = ScrollOffset;
        _dragging = null;
        _thumbGrab = null;
        Notice = "";
    }

    /// <summary>
    /// Replaces the typed value's selection (at most 24 characters).
    /// </summary>
    void ReplaceNumberSelection(string replacement)
    {
        int start = Math.Min(NumberCaret, NumberAnchor), end = Math.Max(NumberCaret, NumberAnchor);
        if (NumberText.Length - (end - start) + replacement.Length > 24) return;

        NumberText = NumberText[..start] + replacement + NumberText[end..];
        NumberCaret = NumberAnchor = start + replacement.Length;
        Notice = "";
    }

    /// <summary>
    /// Sets the slider to the typed value; false, with a notice, when it is not a number.
    /// </summary>
    bool CommitNumber()
    {
        if (EditingNumber is not { } slider) return true;

        if (!float.TryParse(NumberText, NumberStyles.Float, CultureInfo.InvariantCulture, out float value) || !float.IsFinite(value))
        {
            Notice = "Enter a valid number, or press Esc to cancel.";
            return false;
        }

        slider.Set(Math.Clamp(value / slider.Scale, slider.Min, slider.Max));
        EditingNumber = null;
        Notice = "";
        return true;
    }

    /// <summary>
    /// Binds <paramref name="action"/> to <paramref name="binding"/>, taking it from the actions that have it; a reserved key is refused with a
    /// notice.
    /// </summary>
    void SetBinding(InputAction action, Binding binding)
    {
        if (!binding.IsMouse
            && binding.Key is Key.Escape or Key.F6 or Key.LeftAlt or Key.RightAlt or Key.LeftControl or Key.RightControl or Key.LeftShift
                or Key.RightShift)
        {
            Notice = "That key is reserved. Choose another key, or Esc to cancel.";
            return;
        }

        var conflicts = Enum.GetValues<InputAction>().Where(a => a != action && Bindings.Get(a).Contains(binding)).ToArray();

        // A mouse button taken from another action is swapped: that action gets this one's old button, so the three buttons can be shuffled (the
        // buttons form a cycle otherwise).
        if (binding.IsMouse && conflicts.Length == 1 && Bindings.Get(action).Where(b => b.IsMouse).Cast<Binding?>().FirstOrDefault() is { } previous)
        {
            var other = conflicts[0];
            Bindings.Set(other, [.. Bindings.Get(other).Where(b => b != binding), previous]);
            Bindings.Set(action, binding);
            Rebinding = null;
            Notice = $"{ActionLabel(action)} bound to {binding}; {ActionLabel(other)} moved to {previous}.";
            return;
        }

        // A key taken from other actions leaves them: the original's binding map holds one action per key (docs/game/ui-input.md, Action table).
        foreach (var other in conflicts) Bindings.Set(other, [.. Bindings.Get(other).Where(b => b != binding)]);

        Bindings.Set(action, binding);
        Rebinding = null;
        Notice = conflicts.Length == 0
            ? $"{ActionLabel(action)} bound to {binding}."
            : $"{ActionLabel(action)} bound to {binding}, taken from {string.Join(", ", conflicts.Select(ActionLabel))}.";
    }

    /// <summary>
    /// Restore defaults: every slider (but the time of day and speed) and switch to its value at start, the frame limit 240, the mods' settings and
    /// the other settings to their defaults.
    /// </summary>
    void Reset()
    {
        for (int i = 0; i < Values.Sliders.Count; i++)
            if (Values.Sliders[i].Label is not ("Time of day" or "Time speed")) Values.Sliders[i].Set(Defaults[i]);
        for (int i = 0; i < Values.Toggles.Count; i++)
            if (Values.Toggles[i].Get() != ToggleDefaults[i]) Values.Toggles[i].Set(ToggleDefaults[i]);

        frameLimit.Set(240);
        ModsPage.Settings.ResetModSettings();
        Config.SplashScreens = false;
        Config.MainMenuTransitions = true;
        Config.InGameTransitions = true;
        Config.MainMenuTransitionSpeed = 1.5f;
        Config.InGameTransitionSpeed = 1.5f;
        Config.CharacterFlights = false;
        Config.KeepTexturesAtCharacters = true;
        Config.KeepTexturesAtAllSquads = false;
        Config.CharacterFlightSeconds = UserConfig.DefaultFlightSeconds;
        GamePages.AttackSlotsSlider.Set(UserConfig.FaithfulAttackSlots);
        Config.LateDefences = false;
        LateDefencesChanged?.Invoke(false);
        Config.KeepRoamers = Config.OffscreenFights = Config.OffscreenHealing = Config.CampaignAncestorsOnce = Config.KeepWild
            = Config.ResidentSchedule = Config.UnloadedProduction = true;
        Config.DoorsCloseBehind = true;
        Config.Decapitation = true;
        Config.AdvancedRagdolls = true;
        DoorsCloseChanged?.Invoke(true);
        Config.PauseOnLoad = true;
        Config.AnimalsPickUp = false;
        AnimalsPickUpChanged?.Invoke(false);
        Config.FixDoorBreak = Config.FixAmbienceSuffix = Config.FixWindStop = true;
        Player.Frontend.FixLoadButton = Config.FixLoadButton = true;
        Config.FixGunnerFacing = true;
        GunnerFacingChanged?.Invoke(true);
        Config.FixPunchReach = true;
        PunchReachChanged?.Invoke(true);
        Config.FixBenchFacing = true;
        BenchFacingChanged?.Invoke(true);
        OffscreenChanged?.Invoke();
        Config.Audio = new();
        AudioChanged?.Invoke();
        GamePages.SetGameplay(new());
        Config.HardwareMouse = false;
        Config.EnhancedHardwareMouse = true;
        GamePages.CameraRotateSpeed.Set(Data.World.KenshiCamera.DefaultRotateSpeed);
        Config.EdgeScroll = true;
        Config.CameraSmoothing = true;
        Config.InvertX = false;
        Config.InvertY = true;
        Config.CameraMoveSpeed = Data.World.KenshiCamera.DefaultMoveSpeed;
        Config.CameraZoomSpeed = Data.World.KenshiCamera.DefaultZoomSpeed;
        CameraChanged?.Invoke();
        Config.Tutorials = true;
        Player.MovementMarker = Player.RotationMarker = true;
        Config.MovementMarker = Config.RotationMarker = true;
        TutorialsChanged?.Invoke(true);
        Config.HoverOutline = true;
        Config.BuildingOutline = true;
        Player.BuildingSelection.DeselectBuildings = Config.DeselectBuildings = true;
        Config.LuckyrenMode = false;
        Config.GroundItemLimit = Simulation.Items.GroundItems.DefaultLimit;
        (Config.FastSpeed, Config.FastestSpeed) = (SimulationClock.DefaultSpeeds[1], SimulationClock.DefaultSpeeds[2]);
        TimeSpeedsChanged?.Invoke();
        Player.SetUiMode(UiMode.Meitou);
        Player.MeitouMainMenu = Config.MeitouMainMenu = true;
        Player.SpeedUnit = Config.SpeedUnit = SpeedUnit.Mph;
        Player.Floaters = Config.Floaters = FloaterMode.Simple;
        Player.InterfaceScale = Config.InterfaceScale = 1;
        Player.ShowNames = Config.ShowNames = true;
        Player.FeedbackButtonInPlay = Config.FeedbackButton = true;
        Config.TickAhead = TickAheadMode.Dynamic;
        Bindings.ResetToDefaults();
        Config.LookPreset = null;
        Config.FaithfulLook = false;
        Config.KeepAtLow.Clear();
        LookPresets.ChosenLook = null;
        Modes.CustomModes.Clear();
        ResetPending = false;
        Notice = "Default settings restored.";
    }

    #endregion

    #region Drawing

    /// <summary>
    /// Draws the menu: its motion, the window, the page, the footer and the dialogs.
    /// </summary>
    public void Draw(int width, int height, DebugOverlay.OverlayPass? pass = null)
    {
        if (!Visible || Player.SettingsPainter is not { } painter) return;

        Motion.AdvanceMotion();
        Art.Ensure(painter, Canvas.Meitou, Player.UiAtlas);
        PlaceWindow(width, height);
        Controls.Clear();
        Hits.Clear();
        Motion.AnimationScope = "chrome";
        DrawFrame(width, height);
        int contentStart = Controls.Count;
        DrawPage();
        Motion.Opacity(contentStart, MenuMotion.Ease(Motion.ContentProgress));
        DrawFooter();
        DrawDialogs();

        Motion.Opacity(0, MenuMotion.Ease(Motion.MenuProgress));
        painter.Draw(Controls, width, height, Pointer, null, pass: pass);
    }

    /// <summary>
    /// The window's scale and bounds for a <paramref name="width"/> x <paramref name="height"/> screen (physical pixels, so a high-DPI display's is
    /// larger than its points: Retina's twice), slid by the opening motion. The scale has no upper limit: the window keeps its share of the screen
    /// at any resolution.
    /// </summary>
    void PlaceWindow(int width, int height)
    {
        Scale = Math.Max(0.35f, Math.Min(height / 900f, width / 1280f));
        Canvas.TextViewportHeight = height;
        Bounds = new(MathF.Round((width - 1120 * Scale) / 2), MathF.Round((height - 720 * Scale) / 2), MathF.Round(1120 * Scale),
            MathF.Round(720 * Scale));
        Bounds = Bounds with { Y = Bounds.Y + (1 - MenuMotion.Ease(Motion.MenuProgress)) * 8 * Scale };
    }

    /// <summary>
    /// The window's chrome: the dimmed screen, the surface, the header with its close button, the page buttons, the mode and version and the
    /// mods summary.
    /// </summary>
    void DrawFrame(int width, int height)
    {
        Canvas.Rect(new(0, 0, width, height), new(8 / 255f, 6 / 255f, 5 / 255f, 0.64f));
        Canvas.Surface(Bounds, "window");

        if (!Canvas.Meitou)
        {
            Controls.Add(new("settings-header", "Panel", Canvas.R(6, 4, 1108, 36), "", "", "Kenshi_GenericWindowHeaderSkin", true,
                new Dictionary<string, string>(), Vector4.Zero, Pickable: false));
        }

        Canvas.Text("Settings", Canvas.R(20, 4, 650, 32), 22, SettingsCanvas.LightInk);
        var close = Canvas.R(1069, 6, 31, 32);
        Controls.Add(new("settings-close", "Button", close, "", "settings:close", "Kenshi_CloseButtonSkin", true,
            new Dictionary<string, string>(), Vector4.Zero));
        Hits.Add(new(close, () => Visible = false, Label: "Close"));
        Canvas.Rect(Canvas.R(20, 44, 1080, 1), SettingsCanvas.MainInk with { W = 0.12f });
        Canvas.Rect(Canvas.R(192, 56, 1, 610), SettingsCanvas.MainInk with { W = 0.12f });

        for (int i = 0; i < Pages.Length; i++)
        {
            int index = i;
            Canvas.Button(Pages[i], Canvas.R(20, 58 + i * 38, 156, 32), () => Navigate(index), Page == i);
        }

        Canvas.Text(Canvas.Meitou ? "MEITOU" : "FAITHFUL", Canvas.R(28, 592, 148, 18), 11, SettingsCanvas.MutedInk);
        Canvas.Text(BuildVersion.Tag, Canvas.R(28, 612, 148, 16), 11, SettingsCanvas.MutedInk);
        Canvas.Text(BuildVersion.Commit, Canvas.R(28, 628, 148, 16), 11, SettingsCanvas.MutedInk);
        ModsPage.DrawModsSummary();
    }

    /// <summary>
    /// The page's title, the Graphics page's look bar and tabs, its entries (or the mods) and the scroll bar.
    /// </summary>
    void DrawPage()
    {
        Canvas.Surface(Canvas.R(212, 56, 888, 34), "paper");
        Canvas.Text(Pages[Page], Canvas.R(224, 56, 864, 34), 18, new(20 / 255f, 8 / 255f, 6 / 255f, 1));
        float top = 98;

        if (Page == 1)
        {
            // The Look and Quality rows (LookBar), then the tabs, one per subject, across the page.
            LookBar.DrawLookBar();
            const float tabGap = 6;
            float tabWidth = (888 - tabGap * (GraphicsPages.Length - 1)) / GraphicsPages.Length;

            for (int i = 0; i < GraphicsPages.Length; i++)
            {
                int index = i;
                Canvas.Button(GraphicsPages[i], Canvas.R(212 + i * (tabWidth + tabGap), 174, tabWidth, 30), () =>
                {
                    GraphicsPage = index;
                    ScrollOffset = ScrollTarget = 0;
                    Motion.ContentProgress = 0;
                    Focus = -1;
                }, GraphicsPage == i);
            }

            top = 212;
        }

        Viewport = Canvas.R(212, top, 888, 666 - top);
        if (Page == ModsPageIndex) ModsPage.DrawMods();
        else Canvas.DrawEntries(top);

        Motion.AnimationScope = "chrome";

        if (MaxScroll > 0)
        {
            Canvas.Rect(ScrollTrack(), SettingsCanvas.MainInk with { W = 0.1f });
            Canvas.Rect(ScrollThumb(), SettingsCanvas.Brass with { W = 0.75f });
        }
    }

    /// <summary>
    /// The page's scroll bar, right of the scrolled area, pixels.
    /// </summary>
    UiRect ScrollTrack() => new(Viewport.X + Viewport.Width + 12 * Scale, Viewport.Y, 3 * Scale, Viewport.Height);

    /// <summary>
    /// The scroll bar's thumb: as tall as the shown share of the page (24 at least), placed by the scroll.
    /// </summary>
    UiRect ScrollThumb()
    {
        var track = ScrollTrack();
        float thumb = Math.Max(24 * Scale, track.Height * track.Height / (track.Height + MaxScroll));
        float y = MaxScroll > 0 ? track.Y + (track.Height - thumb) * ScrollOffset / MaxScroll : track.Y;
        return track with { Y = y, Height = thumb };
    }

    /// <summary>
    /// The footer: Restore defaults, the notice and Save.
    /// </summary>
    void DrawFooter()
    {
        Canvas.Rect(Canvas.R(20, 676, 1080, 1), SettingsCanvas.MainInk with { W = 0.12f });
        Canvas.Button("Restore defaults", Canvas.R(20, 686, 150, 28), () =>
        {
            ResetPending = true;
            ActiveChoice = null;
            Focus = -1;
        });
        if (Notice.Length > 0) Canvas.Text(Notice, Canvas.R(188, 686, 744, 28), 11, SettingsCanvas.Brass);

        Canvas.Button("Save", Canvas.R(960, 684, 140, 32), () => Visible = false, primary: true);
    }

    #endregion

    #region Dialogs

    /// <summary>
    /// The confirmed action waiting for its dialog; it returns the notice. Null when none.
    /// </summary>
    internal Func<string>? PendingAction;
    /// <summary>
    /// The confirmation dialog's question and title.
    /// </summary>
    internal string Confirmation = "", ConfirmTitle = "";

    /// <summary>
    /// What opens over the page: the open choice's list, the preset naming box, the rebinding prompt and the confirmations.
    /// </summary>
    void DrawDialogs()
    {
        if (ActiveChoice is { } choice) Canvas.DrawChoice(choice);
        else Canvas.AnimatedChoice = null;
        if (PresetNaming.Open) PresetNaming.Draw();
        if (Rebinding is { } binding)
        {
            string prompt = Notice.Length > 0
                ? Notice
                : $"{ActionLabel(binding)}: press a key or mouse button.\nDelete clears the binding. Esc cancels.";
            Canvas.DrawMessage("Assign a control", prompt, () => Rebinding = null);
        }

        if (ResetPending || PendingAction is not null)
        {
            string title = ResetPending ? "Restore defaults?" : ConfirmTitle;
            string question = ResetPending ? "Restore all preferences, controls and the look to their defaults?" : Confirmation;
            Canvas.DrawMessage(title, question, CancelConfirmation, Confirm);
        }
    }

    /// <summary>
    /// The confirmation's cancel: drops the pending reset or action.
    /// </summary>
    void CancelConfirmation()
    {
        ResetPending = false;
        PendingAction = null;
    }

    /// <summary>
    /// The confirmation's OK: restores the defaults, or runs the pending action and shows its notice.
    /// </summary>
    void Confirm()
    {
        if (ResetPending) Reset();
        else if (PendingAction is { } action)
        {
            Notice = action();
            PendingAction = null;
        }
    }

    #endregion
}