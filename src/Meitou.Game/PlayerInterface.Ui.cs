using System.Globalization;
using System.Numerics;
using Meitou.Data.Characters;
using Meitou.Data.Ui;
using Meitou.Game.Building;
using Meitou.Game.Editor;
using Meitou.Game.Host;
using Meitou.Game.Host.Input;
using Meitou.Game.Rendering.Overlays;
using Meitou.Game.Rendering.Ui;
using Meitou.Game.Title;
using Meitou.Game.Ui;
using Meitou.Modding;
using Meitou.Simulation;
using Meitou.Simulation.Editing;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Player;
using EngineKey = Meitou.Game.Host.Input.Key;

namespace Meitou.Game;

/// <summary>
/// The interface's MyGUI side: loading the original's layouts and screens, binding and arranging the HUD each frame, and the keys, clicks, text and
/// wheel the screens take.
/// </summary>
sealed partial class PlayerInterface
{
    #region Interface state

    /// <summary>
    /// The information panel is extended (the <c>extendedstats</c> action).
    /// </summary>
    internal bool InfoExpanded;
    /// <summary>
    /// The original's HUD, <c>Kenshi_MainPanel.layout</c> expanded.
    /// </summary>
    internal MyGuiLayout FaithfulHud = null!;
    /// <summary>
    /// The original's MyGUI resources (<c>kenshi_init.xml</c>, the fonts).
    /// </summary>
    internal MyGuiResources UiResources = new();
    /// <summary>
    /// The UI extensions' host (<see cref="RegisterUiExtension"/>).
    /// </summary>
    readonly UiHost _uiHost = new();
    /// <summary>
    /// The interface's own UI extension's registration, removed in <see cref="DisposeUi"/>.
    /// </summary>
    IDisposable? _uiRegistration;
    /// <summary>
    /// Paints the arranged controls; null before <see cref="CreateUiPainter"/> or after <see cref="DisposeUi"/>.
    /// </summary>
    internal UiPainter? UiPainter;
    /// <summary>
    /// The settings panel is open: the world pauses (<see cref="UpdateMenuPause"/>).
    /// </summary>
    internal bool SettingsOpen;
    /// <summary>
    /// The original's screens (the windows of <see cref="FaithfulScreenFiles"/>), made in <see cref="LoadUi"/>.
    /// </summary>
    internal MyGuiScreenHost FaithfulScreens = null!;
    /// <summary>
    /// An action of the screens for the host (save, load, new game, the options and exit buttons).
    /// </summary>
    public event Action<MyGuiEvent>? ScreenAction;

    /// <summary>
    /// The interface style, Faithful (the original's layouts) or Meitou.
    /// </summary>
    public UiMode UiMode { get; set; }

    /// <summary>
    /// The painter the settings panel draws with.
    /// </summary>
    internal UiPainter? SettingsPainter => UiPainter;

    /// <summary>
    /// The viewed floor of the interior view (the host's): read for the HUD's floor text, stepped by its arrows.
    /// </summary>
    public (Func<int> Get, Action<int> Step)? Floor { get; set; }

    /// <summary>
    /// The interface is drawn and takes input (F7 and the cinema's clean picture hide it).
    /// </summary>
    public bool UiVisible { get; set; } = true;
    /// <summary>The title screen is Meitou's (its menu over the staged world) rather than the original's picture; a switch of its own, not <see cref="UiMode"/>.</summary>
    public bool MeitouMainMenu { get; set; } = true;

    /// <summary>
    /// The share of the screen under fully opaque controls in the last drawn UI frame (<see cref="UiPainter.OpaqueCoverage"/>); 0 without a painter.
    /// </summary>
    public float OpaqueUiCoverage => UiPainter?.OpaqueCoverage ?? 0;

    /// <summary>
    /// The controls this frame, arranged (<see cref="ArrangeUi"/>).
    /// </summary>
    internal IReadOnlyList<UiControl> UiFrame => ArrangeUi();

    /// <summary>
    /// Opens or closes the settings panel, ending an inventory drag, and pauses the world while it is open.
    /// </summary>
    internal void SetSettingsOpen(bool value)
    {
        SettingsOpen = value;
        if (value) InventoryWindows.CancelInventoryDrag();
        UpdateMenuPause();
    }

    /// <summary>
    /// Raises <see cref="ScreenAction"/> for a feature class (an event is raised only by its own type).
    /// </summary>
    internal void RaiseScreenAction(MyGuiEvent e) => ScreenAction?.Invoke(e);

    #endregion

    #region Loading and binding

    /// <summary>
    /// The original layouts the faithful mode opens as screens: id, layout file, modal.
    /// </summary>
    internal static readonly (string Id, string File, bool Modal)[] FaithfulScreenFiles =
    [
        ("inventory", "Kenshi_InventoryCharacterWindow", false), ("stats", "Kenshi_StatsWindow", false),
        ("overview", "Kenshi_OverviewWindow", false), ("menu", "Kenshi_MainMenuPopupPanel", true),
        ("title", "Kenshi_MainMenu", true), ("new-game", "Kenshi_NewGamePanel", true),
        ("save", "Kenshi_SaveGamePanel", true),
        ("load", "Kenshi_LoadGamePanel", true), ("import", "Kenshi_ImportGamePanel", true), ("message", "Kenshi_MessageBox", true),
        ("bag", "Kenshi_InventoryGenericWindow", false), ("loot", "Kenshi_InventoryCharacterWindow", false),
        ("loot-bag", "Kenshi_InventoryGenericWindow", false), ("limbs", "Kenshi_LimbReplacementWindow", false),
        ("trader", "Kenshi_InventoryTraderWindow", false),
        ("building", "Kenshi_GenericBuildingWindow", false), ("bench", "Kenshi_InventoryResearchWindow", false),
        ("storage", "Kenshi_InventoryGenericWindow", false),
        ("build", "Kenshi_BuildWindow", false), ("editor", "Kenshi_CharacterEditor", true), ("dialogue", "Kenshi_ConversationPanel", true),
        ("file", "Kenshi_OpenSaveFileDialog", true), ("prospect", "Kenshi_ProspectingWindow", false), ("tutorials", "Kenshi_TutorialsWindow", false),
        ("leveleditor", "Kenshi_LevelEditor", false),
        (TradeWindows.CharacterTradeScreen, "Kenshi_CharacterSelectionWindow", false),
    ];

    /// <summary>
    /// Loads the original's interface: the catalog's help texts, the palette, the resources, the HUD and every screen's layout, and registers the
    /// interface's actions.
    /// </summary>
    public void LoadUi(UiMode mode, Func<string, string?> resolve)
    {
        UiMode = mode;
        UiResolve = resolve;
        var template = PotTemplate.Read(resolve("locale/en_GB/LC_MESSAGES/main.pot")
            ?? (Content.GameInstall.Locate() is { } install ? Path.Combine(install.Root, "locale/en_GB/LC_MESSAGES/main.pot") : null));
        AdvancedOptions.AdvancedHelpCatalog = NewGameHelpCatalog.From(template);
        StatsWindow.StatHelp = StatHelpCatalog.From(template);
        TutorialWindow.TutorialTexts = TutorialCatalog.From(template);
        MyGuiColours.LoadPalette(resolve("kenshi_colours.xml"));
        UiResources = MyGuiResources.Load("kenshi_init.xml", resolve, "kenshi_fonts.xml");
        var mainPanelFile = resolve("Kenshi_MainPanel.layout") ?? throw new FileNotFoundException("Kenshi_MainPanel.layout not found.");
        var mainPanel = MyGuiReader.ReadLayout(mainPanelFile);
        FaithfulHud = MyGuiTemplates.Expand(mainPanel, UiResources);
        FactionWindow.RelationsLineLayout = ExpandedLayout(resolve, "Kenshi_FactionRelationsLine.layout");
        OrdersPanel.OrdersRowLayout = ExpandedLayout(resolve, "Kenshi_OrdersPanelOrder.layout");
        SquadWindows.SquadBoxLayout = ExpandedLayout(resolve, "Kenshi_SquadBox.layout");
        MessageRoller.MessageLayout = ExpandedLayout(resolve, "Kenshi_MessagePanel.layout");
        StealthView.TaskBarLayout = ExpandedLayout(resolve, "Kenshi_ProgressBarPanel.layout");
        TutorialWindow.TutorialPanelLayout = ExpandedLayout(resolve, "Kenshi_TutorialPanel.layout");
        TutorialWindow.TutorialLineLayout = ExpandedLayout(resolve, "Kenshi_TutorialTooltipPanel.layout");
        MessageRoller.MessageClient = MessageRoller.MessageLayout?.Widgets
            .FirstOrDefault(w => w.Properties.GetValueOrDefault("CaptionOwner") == "MessageTextBox")?.Name;
        FaithfulScreens = new(UiResources)
        {
            ClipboardReader = () => ClipboardRead?.Invoke(),
            ClipboardWriter = text => ClipboardWrite?.Invoke(text),
            CaretAt = p => UiPainter?.EditCaretAt(p),
        };
        foreach (var (id, file, modal) in FaithfulScreenFiles)
        {
            var layout = MyGuiReader.ReadLayout(resolve(file + ".layout") ?? throw new FileNotFoundException(file));
            if (id is "new-game" or "import")
                layout = AdvancedOptions.WithAdvancedPanel(layout,
                    MyGuiReader.ReadLayout(resolve("Kenshi_AdvancedOptionsWindow.layout")
                    ?? throw new FileNotFoundException("Kenshi_AdvancedOptionsWindow.layout")));
            if (id == "load") layout = FrontendScreens.WithSaveFilter(layout);
            if (id == "editor") (CharacterEditor.EditorLayout, CharacterEditor.EditorRowsShape) = (layout, "");
            if (id == "inventory") TradeWindows.CharacterInventoryLayout = layout;
            if (id == "bag") InventoryWindows.BagLayout = layout;
            if (id == "leveleditor") layout = LevelEditorPanel.LevelEditorBar(layout);
            FaithfulScreens.Register(id, layout, modal, centreRoot: id is "file");
        }
        TradeWindows.AnimalInventoryLayout = resolve("Kenshi_InventoryAnimalWindow.layout") is { } animalWindow
            ? MyGuiReader.ReadLayout(animalWindow) : null;
        TradeWindows.CharacterRowLayout = ExpandedLayout(resolve, "Kenshi_CharacterSelectionBox.layout");

        // The title's start, load and import windows leave its buttons live (the original's title windows are not modal): another button replaces
        // them.
        FaithfulScreens.PassThrough = id => id is "new-game" or "load" or "import" && FaithfulScreens.IsOpen("title");
        FaithfulScreens.RegisterPanel(TownPanel.TownScreen, TownPanel.TownPanelLayout(mainPanel));
        FaithfulScreens.Register(FeedbackPanel.FeedbackScreen, FeedbackPanel.FeedbackLayout(), modal: true);
        FeedbackPanel.ButtonLayout = MyGuiTemplates.Expand(FeedbackPanel.FeedbackButtonLayout(), UiResources);
        FaithfulScreens.Follow("bag", "inventory");   // the bag window is married to the inventory, opened and closed by OPEN BAG only
        FaithfulScreens.Follow("loot-bag", "loot");
        FaithfulScreens.Activated += ScreenRouter.FaithfulAction;
        _uiHost.Diagnostic += d => Console.Error.WriteLine($"ui        {d.Extension} {d.Operation}: {d.Error.Message}");
        _uiRegistration = _uiHost.Register(new PlayerUiExtension(ExecuteUiAction));
    }

    /// <summary>
    /// The layout <paramref name="file"/> with its templates expanded; null when the install has no such file.
    /// </summary>
    MyGuiLayout? ExpandedLayout(Func<string, string?> resolve, string file) =>
        resolve(file) is { } path ? MyGuiTemplates.Expand(MyGuiReader.ReadLayout(path), UiResources) : null;

    /// <summary>
    /// The expanded layout and the current bindings of the HUD (<c>"hud"</c>) or of a faithful screen, for the layout audit test.
    /// </summary>
    internal (MyGuiLayout Layout, UiBindings Bindings) FaithfulBindings(string id) =>
        id == "hud" ? (FaithfulHud, BindUi()) : (FaithfulScreens.Layout(id), ScreenRouter.BindFaithfulScreen(id));

    /// <summary>
    /// Makes the painter on <paramref name="overlay"/> and reads the screens' pictures ahead.
    /// </summary>
    public void CreateUiPainter(DebugOverlay overlay, Func<string, string?> resolve)
    {
        UiPainter = new(overlay, UiResources, resolve) { Mode = UiMode, FontSizeSetting = _fontSize, InterfaceScale = _interfaceScale };
        CharacterEditor.FindAsset = resolve;

        // The screens' pictures are read ahead (the overview's map is 43 MB), so that opening a screen only uploads them.
        UiPainter.Prefetch(FaithfulScreenFiles.SelectMany(s => FaithfulScreens.Layout(s.Id).Widgets).Concat(FaithfulHud.Widgets)
            .Select(w => w.Properties.GetValueOrDefault("ImageTexture")).OfType<string>());
    }

    /// <summary>
    /// Frees the painter and removes the interface's UI extension.
    /// </summary>
    public void DisposeUi()
    {
        UiPainter?.Dispose();
        UiPainter = null;
        _uiRegistration?.Dispose();
        _uiRegistration = null;
    }

    /// <summary>
    /// Future code-mod loader entry point; disposal removes the extension's owned registrations.
    /// </summary>
    public IDisposable RegisterUiExtension(IUiExtension extension) => _uiHost.Register(extension);

    /// <summary>
    /// Switches the interface style; a drag or a held order under way ends.
    /// </summary>
    public void SetUiMode(UiMode mode)
    {
        UiMode = mode;
        if (UiPainter is not null) UiPainter.Mode = mode;
        _dragFrom = null;
        Dragging = false;
        Steering = false;
        WorldMap.MapDrag = null;
        InventoryWindows.CancelInventoryDrag();
        UpdateMenuPause();
    }

    /// <summary>
    /// The HUD's bindings this frame: the status texts, shortcut buttons, orders, panels, jobs, squad tabs, disabled buttons and tooltips.
    /// </summary>
    UiBindings BindUi()
    {
        // The original sizes the HUD's template parts by height / 1080 (Observed: the squad tabs' 37 px HeaderPlace at 1440 lines,
        // docs/game/ui-screens.md).
        var b = new UiBindings { TemplateScale = UiScale };
        var lead = Lead();
        BindStatusTexts(b, lead);
        BindShortcutButtons(b);
        // The orders, jobs and squad tab stay the player's while another faction's character is inspected.
        var own = Selected().FirstOrDefault() ?? lead;
        OrdersPanel.BindOrders(b, own);
        foreach (string name in new[] { "LoadingPanel", "TownPanel", "TooltipPanel", "MessagePanel" }) b.Hidden.Add(name);
        BiomeBanner.BindBiome(b);
        BindFloorAndPanels(b, lead);
        bool buildingFocus = BindBuildingFocus(b, lead);
        OrdersPanel.BindJobs(b, own);
        SquadWindows.BindSquadTabs(b);
        if (lead is null && !buildingFocus) b.Hidden.Add("InformationPanel");
        if (lead is null && !buildingFocus) b.Hidden.Add("MedicalPanel");
        BindDisabledButtons(b);
        TooltipRuntime.BindTooltips(b);
        return b;
    }

    /// <summary>
    /// The HUD's texts: the clock, the money, the lead's name and condition, the squad roster.
    /// </summary>
    void BindStatusTexts(UiBindings b, CharacterSnapshot? lead)
    {
        b.Text["clock"] = $"{Session.Clock.DayText}  {Session.Clock.TimeText}";
        b.Text["DayText"] = Session.Clock.DayText;
        b.Text["TimeText"] = Session.Clock.TimeText;

        // The exe's money format string is c.{1,num} (docs/game/ui-screens.md section 1): no space after the point.
        b.Text["MoneyAmountText"] = "c." + Session.World.Player.Money.ToString("N0", CultureInfo.InvariantCulture);

        // The name bar draws its own lines over the widget (HudPanels).
        b.Text["NameText"] = lead?.Name ?? "No character selected";
        b.Properties["NameText"] = new Dictionary<string, string> { ["SkipCaption"] = "true" };
        b.Text["hunger"] = lead?.Body is { } hungry ? $"Hunger {hungry.Hunger:0.00} / 3" : "";
        b.Text["condition"] = lead?.Body is { } status
            ? status.Dead ? "Dead" : status.Unconscious ? "Unconscious" : status.NeedsAid ? "Needs first aid" : "Conscious"
            : "No character selected";
        b.Text["blood"] = lead?.Body is { } blood ? $"Blood   {blood.Blood * 100:0}%" : "Blood --";
        b.Text["body"] = lead?.Body is { } worst ? $"Worst body part   {worst.WorstPart * 100:0}%" : "Body --";
        var roster = Session.CurrentSnapshot.Characters.Where(c => c.IsPlayer).ToArray();
        b.Text["squad.count"] = roster.Length.ToString(CultureInfo.InvariantCulture);
        for (int i = 0; i < Math.Min(8, roster.Length); i++) b.Text[$"squad.{i}.name"] = roster[i].Name;
        b.Text["members"] = $"{PlayerCharacters().Count} members | {Selected().Count} selected";
        b.Text["ExtendedStatsText"] = "";

        // The exe sets the money label without the layout's colon (docs/game/ui-screens.md section 4).
        b.Text["MoneyLabelText"] = "Money";
    }

    /// <summary>
    /// The bounty panel's caption and the actions of the HUD's speed, shortcut and order buttons.
    /// </summary>
    static void BindShortcutButtons(UiBindings b)
    {
        // The bounty panel draws its own Bounty: line over the widget (HudPanels).
        b.Properties["BountyText"] = new Dictionary<string, string> { ["SkipCaption"] = "true" };
        for (int i = 0; i < SpeedButtons; i++) b.Actions[$"TimeSpeedButton{i + 1}"] = $"meitou:speed.{i}";
        b.Actions["ShortcutInventoryButton"] = "meitou:inventory";
        b.Actions["ShortcutStatsButton"] = "meitou:stats";
        b.Actions["ShortcutHelpButton"] = "meitou:help";
        b.Text["ShortcutTechButton"] = "TEC";
        foreach (string name in new[] { "ShortcutMapButton", "ShortcutTechButton", "ShortcutSquadButton" }) b.Actions[name] = "faithful:" + name;
        b.Actions["OrdersMedicButton"] = "meitou:medic";
        b.Actions["OrdersRescueButton"] = "meitou:rescue";
        b.Actions["OrdersProspectButton"] = "meitou:prospect";
    }

    /// <summary>
    /// The floor arrows, the build button, which of the bounty, information and paused panels show and how far the information panel is out.
    /// </summary>
    void BindFloorAndPanels(UiBindings b, CharacterSnapshot? lead)
    {
        // The viewed floor (the exe's "Floor {1,num}", docs/game/ui-screens.md section 4; docs/formats/zones.md "Interior view"); the arrows step
        // it as PageUp / PageDown do.
        b.Text["FloorText"] = Loc.Tr("Floor {1,num}", Floor?.Get() ?? 0);
        b.Actions["FloorArrowUp"] = "meitou:floor-up";
        b.Actions["FloorArrowDown"] = "meitou:floor-down";
        b.Actions["BuildButton"] = "meitou:build";
        if (Build.Active) foreach (string name in BuildMode.BuildHiddenHud) b.Hidden.Add(name);

        // A bounty or a status line (Slave, Leader, Committing Crime!...) shows the bounty panel.
        if (lead is null || lead.Bounties.Count == 0 && lead.Slave is null && lead.Status is null) b.Hidden.Add("BountyPanel");

        // The information panel starts collapsed (as the original's, docs/game/ui-screens.md section 4): the extending part (name, state lines,
        // extended stats) slid down
        // until its bottom is the panel's, behind the stats panel, so only the name and the state lines show; the ^ button between them toggles it.
        if (!InfoExpanded && FaithfulHud.Find("InformationPanel") is { } info && FaithfulHud.Find("ExtendingPanel") is { } extending)
        {
            var infoRect = info.Position.RelativeTo(new(0, 0, Width, Height), true);
            var rect = extending.Position.RelativeTo(infoRect, true);
            b.Positions["ExtendingPanel"] = rect with { Y = infoRect.Y + infoRect.Height - rect.Height };
        }
        b.Actions["ExtendedStatsButton"] = "meitou:extendedstats";
        if (Session.Simulation.Speed != 0) b.Hidden.Add("PausedPanel");
    }

    /// <summary>
    /// The medical bars of the lead or the selected building, with the building's status word in the medical header; whether a building is
    /// the focus.
    /// </summary>
    bool BindBuildingFocus(UiBindings b, CharacterSnapshot? lead)
    {
        var buildingNow = BuildingSelection.BuildingFocus();
        bool buildingFocus = buildingNow is not null;
        HudPanels.BindMedicalBars(b, lead, buildingNow is { } shownBuilding ? BuildingSelection.BuildingRows(shownBuilding) : null);

        // A selected building shows its status word in the medical header and its condition rows below it.
        if (buildingNow is not null)
        {
            var (_, _, _, buildingStatus, bad, _) = BuildingSelection.BuildingLines(buildingNow);
            b.Text["HealthText"] = buildingStatus;
            b.Properties["HealthText"] = new Dictionary<string, string> { ["TextAlign"] = "Center",
                ["TextColour"] = bad ? BadColour : MyGuiColours.TemplateInk(UiResources, "Kenshi_TextboxStandardText") };
        }
        return buildingFocus;
    }

    /// <summary>
    /// Greys out the HUD buttons without a backend and the orders that need a selection.
    /// </summary>
    void BindDisabledButtons(UiBindings b)
    {
        // Buttons without a backend are disabled, except those the original never greys out (the build and floor buttons look live there in the
        // open).
        foreach (var w in FaithfulHud.Widgets.Where(w => w.Type == "Button" && !b.Actions.ContainsKey(w.Name) && !AlwaysLiveButton(w.Name)))
            b.Disabled.Add(w.Name);
        if (Selected().Count == 0 && !FaithfulScreens.IsOpen("prospect")) b.Disabled.Add("OrdersProspectButton");
        if (Selected().Count == 0)
            foreach (string name in new[] { "OrdersMedicButton", "OrdersRescueButton", "medic", "rescue", "stop" }) b.Disabled.Add(name);
    }

    /// <summary>
    /// The HUD buttons the original never greys out: the build and floor buttons.
    /// </summary>
    static bool AlwaysLiveButton(string name) => name is "BuildButton" or "FloorArrowUp" or "FloorArrowDown";

    #endregion

    #region Arranging

    // The arranged base (the HUD, the squad bar, the HUD text and the open screens) is kept across frames while nothing it is made from changed:
    // the shown snapshot, the mouse, the window, the mode, the open screens, the speed, the modifiers and any input since. What moves with the camera
    // or real time (name tags, floaters, speech, the notice, the hover label) is added every frame on top (ArrangeUi).
    /// <summary>
    /// The HUD's last arrangement and what it was made from (<see cref="MyGuiPresentation.Arrange"/> depends on nothing else): reused while the
    /// bindings stay the same, so most frames skip the layout; remade at least every <see cref="HudRearrangeCalls"/> calls in case a binding is
    /// missed by <see cref="UiBindings.ContentEquals"/>.
    /// </summary>
    UiBindings? _hudBindings;
    /// <summary>
    /// The HUD's last arranged controls.
    /// </summary>
    IReadOnlyList<UiControl> _hudControls = [];
    /// <summary>
    /// What <see cref="_hudControls"/> was arranged for: the HUD, the window's size, the layers and the language.
    /// </summary>
    (MyGuiLayout Layout, int Width, int Height, IReadOnlyList<string> Layers, GameLanguage Language) _hudKey;
    /// <summary>
    /// The arrangements reused since the last one.
    /// </summary>
    int _hudReuses;
    /// <summary>
    /// The HUD is arranged again at least every this many calls.
    /// </summary>
    const int HudRearrangeCalls = 30;
    /// <summary>
    /// The most a base is reused (real seconds), in case something it reads changed without any of the key's parts.
    /// </summary>
    const double UiReuseSeconds = 0.05;
    /// <summary>
    /// The key <see cref="_uiBase"/> was arranged for; null before the first.
    /// </summary>
    UiBaseKey? _uiBaseKey;
    /// <summary>
    /// The open screens <see cref="_uiBase"/> was arranged with.
    /// </summary>
    string[] _uiBaseScreens = [];
    /// <summary>
    /// The last arranged base.
    /// </summary>
    List<UiControl> _uiBase = [];
    /// <summary>
    /// When <see cref="_uiBase"/> was arranged (a <see cref="System.Diagnostics.Stopwatch"/> timestamp).
    /// </summary>
    long _uiBaseAt;

    /// <summary>
    /// What a base was arranged from: when it changes, the base is arranged again.
    /// </summary>
    readonly record struct UiBaseKey(WorldSnapshot Snapshot, Vector2 Mouse, int Width, int Height, UiMode Mode, GameLanguage Language,
        int Input, int Town, int? Tutorial, double Speed, bool Paused, bool Hud, bool Ctrl, bool Alt, bool ShotWithoutMenus);

    /// <summary>
    /// Arranges this frame's controls: the base (reused while nothing it is made from changed), then what moves every frame on top.
    /// </summary>
    internal IReadOnlyList<UiControl> ArrangeUi()
    {
        SyncScreens();
        // The conversation panel covers the HUD bar (engine reading of its layout).
        bool hud = !TitleOpen && !CharacterEditor.EditorOpen && !DialogueWindow.ConversationShown;
        TownPanel.SyncTownPanel(hud);
        if (!TitleOpen) TitleMenu.ReleaseTitleLogo();
        var controls = ArrangedBase(hud);
        AddFrameControls(controls, hud);
        var top = MyGuiPresentation.HitTest(controls, Mouse.X, Mouse.Y);
        PointerPanel.PointerOverUi = FaithfulScreens.Contains(Mouse) || top is not null;
        PointerPanel.PointerOverButton = top is { Enabled: true } && (top.Type == "Button" || top.Action.Length > 0);
        TooltipRuntime.AddHoverLabel(controls);
        Inspector.AddInspectorPanel(controls);
        return controls;
    }

    /// <summary>
    /// Once an arrangement: the screens that follow the world opened, closed or filled.
    /// </summary>
    void SyncScreens()
    {
        TradeWindows.SyncTradeScreens();
        Production.SyncMachineScreens();
        TradeWindows.SyncLootScreen();
        InventoryWindows.SyncPinnedInventories();
        TradeWindows.SyncInventoryLayouts();
        InventoryWindows.SyncLimbsScreen();
        DialogueWindow.SyncDialogueScreen();
        TradeWindows.SyncCharacterTradeScreen();
        InventoryWindows.ValidateInventoryDrag();
        FaithfulScreens.FocusRing = UiMode == UiMode.Meitou;
    }

    /// <summary>
    /// The interface's base (<see cref="ArrangedBase"/>): the HUD and its slots, the squad bar, the HUD text, the tutorial panel and the open
    /// screens.
    /// </summary>
    List<UiControl> ArrangeBase(bool hud)
    {
        var b = BindUi();
        var controls = hud ? ArrangeHud(b) : [];
        UiRect FaithfulSlot(string name) => controls.FirstOrDefault(c => c.Name == name)?.Bounds ?? default;
        var slots = new Dictionary<string, UiRect>
        {
            [UiSlots.Overlay] = new(0, 0, Width, Height),
            [UiSlots.Toolbar] = FaithfulSlot("TimeMoneyPanel"),
            [UiSlots.Selection] = FaithfulSlot("InformationPanel"),
            [UiSlots.Squad] = FaithfulSlot("SquadPanel"),
        };
        if (hud) controls.AddRange(_uiHost.Arrange(UiMode, Width, Height, b, slots));
        PortraitBar.SquadBarRow = 0;
        if (controls.FirstOrDefault(c => c.Name == "SquadPanel") is { } squadPanel)
            PortraitBar.AddSquadPortraits(controls, squadPanel.Bounds);
        HudPanels.AddHudText(controls);
        if (hud) OrdersPanel.AddJobScrollBar(controls);
        OrdersPanel.AddJobRowParts(controls);
        if (hud) TutorialWindow.AddTutorialPanel(controls);
        AddScreens(controls);
        return controls;
    }

    /// <summary>
    /// The open screens over <paramref name="controls"/>.
    /// </summary>
    void AddScreens(List<UiControl> controls)
    {
        FaithfulScreens.WindowScale = InterfaceScale;
        foreach (var screen in FaithfulScreens.Arrange(Width, Height, ScreenRouter.BindFaithfulScreen, ScreenRouter.AddFaithfulContent))
        {
            if (FeedbackPanel.ShotWithoutMenus && FeedbackPanel.FeedbackShotHides.Contains(screen.Id)) continue;
            if (screen.Id == "editor") controls.AddRange(CharacterEditor.EditorBackdrop());
            controls.AddRange(screen.Controls.Where(c => !MeitouMainMenu ||
                !c.Properties.TryGetValue("ImageResource", out var resource) || resource != "Kenshi_TitleScreen"));
            if (screen.Id == "title" && MeitouMainMenu && !Frontend.ShowCredits) AddTitleMark(controls);
        }
    }

    /// <summary>
    /// The Meitou title's mark, top right: the logo once it is loaded, the word until then.
    /// </summary>
    void AddTitleMark(List<UiControl> controls)
    {
        if (TitleMenu.TitleLogoReady())
        {
            float logoWidth = Math.Min(Width * 0.3f, Height * 0.6f);
            controls.Add(new("MeitouTitle", "ImageBox", new(Width * 0.975f - logoWidth, Height * 0.04f, logoWidth, logoWidth * 0.469f), "", "", "",
                true, new Dictionary<string, string> { ["ImageTexture"] = TitleMenu.TitleLogoTexture }, Vector4.Zero, Pickable: false));
        }
        else
            controls.Add(new("MeitouTitle", "TextBox", new(Width * 0.6f, Height * 0.04f, Width * 0.37f, Height * 0.14f), "KENSHI", "", "", true,
                new Dictionary<string, string> { ["FontName"] = "Meitou_TitleFont", ["TextAlign"] = "Right VCenter" },
                Vector4.Zero, new Vector4(0.89f, 0.84f, 0.71f, 1), TextScale: Height / 720f));
    }

    /// <summary>
    /// Every frame over the base: what moves with the camera or real time. The name tags, ground item labels and task bars go under everything (the
    /// original's Back layer); the floaters and speech bubbles just over them, under the HUD and every screen, the map included (its Dialog layer,
    /// below Middle where the HUD is, <c>core_layer.xml</c>).
    /// </summary>
    void AddFrameControls(List<UiControl> controls, bool hud)
    {
        if (hud) TutorialWindow.AddTutorialHighlight(controls);
        MessageRoller.AddNotice(controls);
        InventoryWindows.AddInventoryDrag(controls);

        var nameTags = new List<UiControl>();
        GroundItemPicks.AddItemLabels(nameTags);
        StealthView.AddNameTags(nameTags);
        StealthView.AddTaskBars(nameTags);
        var dialogLayer = new List<UiControl>();
        DamageFloaters.AddFloaters(dialogLayer);
        SpeechBubbles.AddSpeech(dialogLayer);
        controls.InsertRange(0, [.. nameTags, .. dialogLayer]);

        Build.AddBuildReadout(controls);
        PointerPanel.AddPointerChance(controls);
        ContextMenuWindow.AddContextMenu(controls);
        FeedbackPanel.AddFeedbackButton(controls);
    }

    /// <summary>
    /// The HUD arranged from <paramref name="b"/>, reused while the bindings and <see cref="_hudKey"/> stay the same.
    /// </summary>
    internal List<UiControl> ArrangeHud(UiBindings b)
    {
        (MyGuiLayout, int, int, IReadOnlyList<string>, GameLanguage) key = (FaithfulHud, Width, Height, UiResources.Layers, Loc.Language);
        if (_hudBindings is null || !key.Equals(_hudKey) || ++_hudReuses >= HudRearrangeCalls || !b.ContentEquals(_hudBindings))
        {
            _hudControls = MyGuiPresentation.Arrange(FaithfulHud, Width, Height, b, UiResources.Layers);
            (_hudBindings, _hudKey, _hudReuses) = (b, key, 0);
        }
        return [.. _hudControls];
    }

    /// <summary>
    /// A copy of the base for this frame (the caller adds to it), made again when its key changed,
    /// something in it animates, or it is <see cref="UiReuseSeconds"/> old.
    /// </summary>
    List<UiControl> ArrangedBase(bool hud)
    {
        var key = BaseKey(hud);
        var screens = FaithfulScreens.OpenScreens.ToArray();
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        bool fresh = _uiBaseKey is { } kept && kept == key && screens.AsSpan().SequenceEqual(_uiBaseScreens) && !UiAnimating
            && System.Diagnostics.Stopwatch.GetElapsedTime(_uiBaseAt, now).TotalSeconds < UiReuseSeconds;
        if (!fresh)
        {
            _uiBase = ArrangeBase(hud);
            (_uiBaseKey, _uiBaseScreens, _uiBaseAt) = (key, FaithfulScreens.OpenScreens.ToArray(), now);
        }

        return [.. _uiBase];
    }

    /// <summary>
    /// This frame's base key.
    /// </summary>
    UiBaseKey BaseKey(bool hud) => new(Session.CurrentSnapshot, Mouse, Width, Height, UiMode, Loc.Language, UiInput, TownPanel.ShownTown,
        TutorialWindow.TutorialShown, Session.Simulation.Speed, Session.Simulation.IsPaused, hud, CtrlHeld, AltHeld, FeedbackPanel.ShotWithoutMenus);

    /// <summary>
    /// The base shows something that moves on real time or with the camera: it is arranged every frame.
    /// </summary>
    bool UiAnimating => TitleOpen || CharacterEditor.EditorOpen || Frontend.ShowCredits || BiomeBanner.BiomeShown || WorldMap.MapShown
        || PortraitBar.PortraitFlashes.Count > 0 || TutorialWindow.TutorialFlashes.Count > 0 || FaithfulScreens.CapturesPointer;

    #endregion

    #region Clicks and actions

    /// <summary>
    /// The squad bar member last clicked, for the double click (-1 for none).
    /// </summary>
    int _lastMemberClick = -1;
    /// <summary>
    /// When <see cref="_lastMemberClick"/> was clicked (a <see cref="System.Diagnostics.Stopwatch"/> timestamp).
    /// </summary>
    long _lastMemberClickAt;

    /// <summary>
    /// Whether a point is over the interface: an open screen or an arranged control.
    /// </summary>
    public bool ContainsUi(Vector2 p)
    {
        if (!UiVisible) return false;
        var controls = ArrangeUi();
        return FaithfulScreens.Contains(p) || MyGuiPresentation.HitTest(controls, p.X, p.Y) is not null;
    }

    /// <summary>
    /// A button press on the interface: to the first that takes it (the inventory, the map, the squad windows, the screens, the HUD's controls);
    /// false when none did.
    /// </summary>
    bool UiMouseDown(Vector2 p)
    {
        if (!UiVisible) return false;
        var controls = ArrangeUi();
        if (InventoryWindows.InventoryPointerDown(p)) return true;
        if (WorldMap.MapPointerDown(p)) return true;
        if (SquadWindows.SquadPointerDown(p)) return true;
        if (FaithfulScreens.PointerDown(p))
        {
            UpdateMenuPause();
            return true;
        }
        if (OrdersPanel.JobDragStart(controls, p)) return true;
        if (MyGuiPresentation.HitTest(controls, p.X, p.Y) is not { } c) return false;
        if (SquadWindows.SquadTabClick(c)) return true;
        if (c.Action.StartsWith("faithful:", StringComparison.Ordinal))
        {
            // A second click on the open tab's button closes the overview.
            int tab = c.Name == "ShortcutTechButton" ? 2 : c.Name == "ShortcutSquadButton" ? 4 : 0;
            if (FaithfulScreens.IsOpen("overview") && FaithfulScreens.GetTab("overview", "TabsMain") == tab)
                FaithfulScreens.Close("overview");
            else
            {
                FaithfulScreens.Open("overview");
                FaithfulScreens.SetTab("overview", "TabsMain", tab);
            }
            UpdateMenuPause();
            return true;
        }

        // By name: the thumb has no action.
        if (ScrollBars.HudScrollKey(c.Name) is { } key)
        {
            if (c.Name.EndsWith("-thumb", StringComparison.Ordinal)) ScrollBars.ScrollThumbDown(key, c, p);
            else ScrollBars.HudScrollAction(key, c.Action);
            return true;
        }
        _uiHost.Dispatch(c);
        return true;
    }

    /// <summary>
    /// The F1 key and the HLP button share one handler: toggle the Tutorials window (FUN_1406e1f20; ui-screens.md).
    /// </summary>
    void ToggleHelpWindow()
    {
        if (FaithfulScreens.IsOpen("tutorials"))
            FaithfulScreens.Close("tutorials");
        else
            FaithfulScreens.Open("tutorials");
    }

    /// <summary>
    /// Runs an interface action: the speed buttons, the windows, the orders and the rest of the HUD's buttons.
    /// </summary>
    internal void ExecuteUiAction(string action, string argument)
    {
        InterfaceSounds.SoundForAction(action, argument);
        if (action.StartsWith("speed:", StringComparison.Ordinal))
            PressButton(int.Parse(action[6..], CultureInfo.InvariantCulture));
        else if (action is "inventory" or "stats")
        {
            // toggle_inventory does nothing, opening or closing, without a living lead of the player's (FUN_140724980); the selection holds only
            // player characters.
            if (action == "inventory" && (InventoryWindows.InventoryOwner() is not { } owner || owner.Body is { Dead: true }))
                return;
            if (FaithfulScreens.IsOpen(action))
            {
                FaithfulScreens.Close(action);
                if (action == "inventory")
                {
                    FaithfulScreens.Close("bag");
                    FaithfulScreens.Close("limbs");
                }
            }

            // The lead's pinned window comes to the front instead of a second window on them.
            else if (action == "inventory" && InventoryWindows.InventoryOwner() is { } lead
                && InventoryWindows.PinnedInventories.FirstOrDefault(p => p.Value == lead.Id).Key is { } slot)
                FaithfulScreens.Open(slot);
            else FaithfulScreens.Open(action);
        }
        else if (action == "help") ToggleHelpWindow();
        else if (action == "extendedstats") InfoExpanded = !InfoExpanded;
        else if (action is "floor-up" or "floor-down") Floor?.Step(action == "floor-up" ? 1 : -1);
        else if (action == "build") Build.ToggleBuildMode();
        else if (action == "context") ContextMenuWindow.ContextAction(argument);
        else if (action == "squad") Select(ActiveSquadMembers(), false);
        else if (action == "member")
        {
            // A portrait click selects (Shift: adds), a double click centres and follows (docs/game/ui-input.md section 7).
            int index = int.Parse(argument, CultureInfo.InvariantCulture);
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            bool doubleClick = index == _lastMemberClick
                && System.Diagnostics.Stopwatch.GetElapsedTime(_lastMemberClickAt, now).TotalSeconds <= DoubleTapSeconds;
            (_lastMemberClick, _lastMemberClickAt) = (doubleClick ? -1 : index, now);
            SelectMember(index, InventoryWindows.PressShift, doubleClick);
        }
        else if (action == "stop") Say(new StopCommand(SelectedIds()) { Tick = Session.World.Tick });
        else if (action == "medic") Say(new Simulation.FirstAid.MedicJobCommand(SelectedIds(), SlowModifier) { Tick = Session.World.Tick });
        else if (action == "rescue") Say(new Simulation.Carrying.RescueJobCommand(SelectedIds(), SlowModifier) { Tick = Session.World.Tick });
        else if (action == "prospect") Prospecting.ProspectAction();
        else if (action.StartsWith("building-", StringComparison.Ordinal) || action.StartsWith("machine-", StringComparison.Ordinal))
            BuildingSelection.BuildingAction(action);
        else if (action == "tutorial") TutorialWindow.TutorialAction(argument);
        else if (action == "job-remove") OrdersPanel.RemoveJob(argument);
        else if (action.StartsWith("order.", StringComparison.Ordinal) && Enum.TryParse(action[6..], out CharacterOrders order))
            Say(new OrdersCommand(SelectedIds(), order) { Tick = Session.World.Tick });
        else if (action.StartsWith("runspeed.", StringComparison.Ordinal))
        {
            int step = int.Parse(action[9..], CultureInfo.InvariantCulture);
            Say(new RunSpeedCommand(SelectedIds(), step) { Tick = Session.World.Tick });
        }
    }

    /// <summary>
    /// Whether the interface takes the pointer: a screen that captures it, the map's or a scroll bar's drag, or an item lifted.
    /// </summary>
    public bool UiCapturesPointer => UiVisible && (FaithfulScreens.CapturesPointer || WorldMap.MapDrag is not null
        || InventoryWindows.LiftPress is not null || ScrollBars.ScrollDrag is not null);

    /// <summary>
    /// A modal screen (the Esc menu, a message box) is up: the pointer is the interface's, even in the free camera.
    /// </summary>
    public bool ModalOpen => UiVisible && FaithfulScreens.HasModal;

    /// <summary>
    /// The faithful screens open, bottom first (the play benchmark says which modal one stopped the game).
    /// </summary>
    public IReadOnlyList<string> OpenScreens => FaithfulScreens.OpenScreens;

    /// <summary>
    /// The widgets a click can reach in the last arranged frame (the topmost control at its own centre): the HUD and its panels in either UI mode and
    /// the open screens, for the agent control channel (ui-tree, ui-click). Screen is the faithful screen's id, "hud" for the rest.
    /// </summary>
    internal IEnumerable<(string Screen, UiControl Control)> UiClickables()
    {
        if (!UiVisible) yield break;
        var controls = ArrangeUi();
        var owner = new Dictionary<UiControl, string>(ReferenceEqualityComparer.Instance);
        foreach (var screen in FaithfulScreens.LastFrame)
            foreach (var c in screen.Controls) owner[c] = screen.Id;
        foreach (var c in controls)
        {
            float cx = c.Bounds.X + c.Bounds.Width / 2, cy = c.Bounds.Y + c.Bounds.Height / 2;
            if (!c.Enabled || c.Bounds.Width < 2 || c.Bounds.Height < 2 || cx < 0 || cy < 0 || cx > Width || cy > Height)
                continue;
            if (c.Type != "Button" && c.Action.Length == 0) continue;
            if (MyGuiPresentation.HitTest(controls, cx, cy) is { } top && ReferenceEquals(top, c))
                yield return (owner.GetValueOrDefault(c, "hud"), c);
        }
    }

    /// <summary>
    /// A left click at <paramref name="p"/> through the game's own pointer path (press and release).
    /// </summary>
    internal void UiClickAt(Vector2 p)
    {
        MouseDown(MouseButton.Left, p, false);
        MouseUp(MouseButton.Left, p, false);
    }

    /// <summary>
    /// Opens a registered faithful screen by id (the agent control channel); false when there is none.
    /// </summary>
    internal bool OpenFaithfulScreen(string id)
    {
        if (!FaithfulScreens.IsRegistered(id)) return false;
        FaithfulScreens.Open(id);
        UpdateMenuPause();
        return true;
    }

    #endregion

    #region Drawing

    /// <summary>
    /// The rule-of-thirds camera grid (Shift+;, toggle_camera_grid, event 0x29: the widget "CameraGrid", <c>FUN_1406e5fc0</c>), under the interface.
    /// </summary>
    bool _cameraGrid;
    /// <summary>
    /// Frames left before the screenshot with the camera grid hidden is taken.
    /// </summary>
    int _gridHold;
    /// <summary>
    /// The camera grid was hidden for a screenshot and is shown again after it.
    /// </summary>
    bool _gridRestore;

    /// <summary>
    /// The original's background-loading bar, drawn over the interface in the Faithful mode (the host steps it).
    /// </summary>
    internal ResourceLoadBar? LoadBar { get; set; }

    /// <summary>
    /// Draws the interface: the camera grid, the arranged controls and the Faithful background-loading bar.
    /// </summary>
    void DrawGameUi(DebugOverlay overlay, int width, int height, DebugOverlay.OverlayPass? pass = null)
    {
        IReadOnlyList<UiControl> controls = ArrangeUi();
        if (_cameraGrid) DrawCameraGrid(overlay, width, height);
        if (UiMode == UiMode.Faithful && LoadBar is { Visible: true } bar)
            controls = [.. controls, .. bar.Controls(UiResources, width, height)];
        UiPainter?.Draw(controls, width, height, Mouse, SpeedButtonAction,
            minimumTextPixels: TitleOpen ? 0 : StandardPixels, pass: pass);
    }

    /// <summary>
    /// With the interface hidden (F7, <c>hud hide</c>) only the speech bubbles are drawn, so a filmed clean picture keeps the <c>say</c> commentary.
    /// </summary>
    public void DrawSpeechOnly(DebugOverlay overlay, int width, int height)
    {
        Width = width;
        Height = height;
        var controls = new List<UiControl>();
        SpeechBubbles.AddSpeech(controls);
        if (controls.Count == 0) return;
        UiPainter?.Draw(controls, width, height, Mouse, SpeedButtonAction, minimumTextPixels: StandardPixels);
        overlay.Flush(width, height);
    }

    /// <summary>
    /// The screenshot event with the camera grid shown (the frame-end callback <c>FUN_14082b6e0</c>, <b>Verified</b>): the "CameraGrid" widget is
    /// hidden and the picture is taken two frames later, when the widget is shown again (<see cref="EndScreenshot"/>); with the grid hidden or
    /// missing the picture is taken at once. Returns whether the picture is taken this frame.
    /// </summary>
    public bool GateScreenshot(bool requested)
    {
        if (_gridHold > 0) return --_gridHold == 0;
        if (!requested) return false;
        if (!_cameraGrid) return true;
        (_cameraGrid, _gridRestore, _gridHold) = (false, true, 2);
        return false;
    }

    /// <summary>
    /// The picture is taken: the camera grid hidden for it is shown again.
    /// </summary>
    public void EndScreenshot()
    {
        if (!_gridRestore) return;
        (_cameraGrid, _gridRestore) = (true, false);
    }

    /// <summary>
    /// The rule-of-thirds lines.
    /// </summary>
    static void DrawCameraGrid(DebugOverlay overlay, int width, int height)
    {
        var white = Vector4.One;
        for (int i = 1; i <= 2; i++)
        {
            float x = MathF.Round(width * i / 3f), y = MathF.Round(height * i / 3f);
            overlay.Line(x, 0, x, height, 1, white);
            overlay.Line(0, y, width, y, 1, white);
        }
    }

    /// <summary>
    /// For screenshot reviews: open the same panel through its registered button action.
    /// </summary>
    public void ShowUiPanel(string? panel, bool? expandInformation = null)
    {
        if (expandInformation is { } expanded) InfoExpanded = expanded;
        if (panel?.StartsWith("editor", StringComparison.Ordinal) == true)
        {
            var tab = panel == "editor-face" ? EditorTab.Face : panel == "editor-hair" ? EditorTab.Hair : EditorTab.Body;
            if (!CharacterEditor.EditorOpen)
            {
                FaithfulScreens.CloseAll();
                if (Build.Active) Build.SetBuildMode(false);
                if (!CharacterEditor.OpenEditor(EditKind.Creation, PlayerCharacters())) return;
            }
            CharacterEditor.Editor!.Tab = tab;
            return;
        }
        FaithfulScreens.CloseAll();
        UpdateMenuPause();
        if (panel is null) return;

        // The title with one of its buttons pressed (its window or the credits, placed as from the title).
        if (panel.StartsWith("title-", StringComparison.Ordinal))
        {
            Frontend.ShowTitle();
            string button = panel[6..] switch { "load" => "LoadGameButton", "import" => "ImportGameButton", "new-game" => "NewGameButton",
                "exit" => "ExitButton", _ => "CreditsButton" };
            Frontend.HandleFrontendAction(new("title", button, button));
            UpdateMenuPause();
            return;
        }
        if (panel is "overview" or "overview-faction" or "menu" or "new-game" or "save" or "load" or "title")
        {
            if (panel == "title") Frontend.ShowTitle();
            else if (panel is "save" or "load") Frontend.OpenSlots(panel);
            else if (panel == "overview-faction")
            {
                FaithfulScreens.Open("overview");
                FaithfulScreens.SetTab("overview", "TabsMain", FactionWindow.FactionTab);
            }
            else
            {
                FaithfulScreens.Open(panel);
                if (panel == "overview") FaithfulScreens.SetTab(panel, "TabsMain", 0);
            }
            UpdateMenuPause();
            return;
        }

        // The first building found on a grid of picks over the view (its HUD panels); any other panel drops it.
        if (panel != "building" && BuildingSelection.Selected is not null) BuildingSelection.SetBuilding(null);
        if (panel == "building")
        {
            for (int y = Height / 8; BuildingSelection.Selected is null && y < Height * 2 / 3; y += 24)
                for (int x = Width / 8; BuildingSelection.Selected is null && x < Width * 7 / 8; x += 24)
                    if (BuildingSelection.PickBuilding(new(x, y)) is { Record: not null } found)
                        BuildingSelection.SetBuilding(found);
            return;
        }
        if (panel == "build")
        {
            if (Build.Active) FaithfulScreens.Open("build");
            else Build.SetBuildMode(true);
            return;
        }
        if (panel is not ("inventory" or "stats" or "limbs"))
            throw new ArgumentException("Unknown UI panel.", nameof(panel));
        var control = ArrangeUi().First(c => c.Action == $"meitou:{(panel == "limbs" ? "inventory" : panel)}");
        _uiHost.Dispatch(control);
        if (panel == "limbs")
        {
            InventoryWindows.LimbsOwner = InventoryWindows.InventoryOwner()?.Id ?? CharacterId.None;
            FaithfulScreens.Open("limbs");
            InventoryLayout.PlaceLimbsWindow();
        }
    }

    #endregion

    #region Keys and text

    /// <summary>
    /// Keys the host keeps for itself (the sandbox's letters), so the faithful window shortcuts leave them alone.
    /// </summary>
    public HashSet<Key> ReservedKeys { get; } = [];

    /// <summary>
    /// Pauses the world while the settings panel or a modal screen is open.
    /// </summary>
    internal void UpdateMenuPause() => Session.Simulation.RequestedPause = SettingsOpen || FaithfulScreens.HasModal;

    /// <summary>
    /// A key to the interface: the camera grid, Esc, an edit box, the open screens, then the window hotkeys; false when it did not take it.
    /// </summary>
    public bool UiKey(Key key, bool ctrl = false, bool shift = false)
    {
        UiInput++;
        if (!UiVisible) return false;
        if (key == Key.Escape && InventoryWindows.CancelInventoryDrag()) return true;

        // toggle_camera_grid (Shift+; fixed: a binding carries no modifier, and ; alone is the free camera's).
        if (shift && key == Key.Semicolon && !FaithfulScreens.HasKeyboardFocus)
        {
            _cameraGrid = !_cameraGrid;
            return true;
        }
        if (key == Key.Escape && LevelEditorPanel.LevelEscape()) return true;
        if (key == Key.Escape) return EscapeKey();
        if (FaithfulScreens.HasKeyboardFocus && EditFocusKey(key, ctrl, shift)) return true;
        if (ScreenKey(key, shift) is { } screenTook) return screenTook;
        return WindowHotkey(key);
    }

    /// <summary>
    /// Esc in the interface: closes the top window (or the title, the loot, the trade), else opens the game menu; always taken.
    /// </summary>
    bool EscapeKey()
    {
        if (AdvancedOptions.AdvancedOpen && FaithfulScreens.OpenScreens.LastOrDefault() == "new-game")
        {
            AdvancedOptions.AdvancedOpen = false;
            return true;
        }

        // The bare title: Esc hides the credits and quits at once, no confirmation (FUN_14082b010 sets the quit flag unless FUN_140914a90 closed
        // a title window; hiding the credits does not count as closing one). With a title window open the generic Esc below closes it.
        if (FaithfulScreens.IsOpen("title") && FaithfulScreens.OpenScreens.Count == 1)
        {
            Frontend.ShowCredits = false;
            ScreenAction?.Invoke(new("menu", "ExitGameButton", "exit"));
            return true;
        }

        // The editor stays under the game menu (the original's Esc in the editor; it has no cancel: CONFIRM closes it).
        if (CharacterEditor.EditorOpen && FaithfulScreens.OpenScreens[^1] == "editor")
        {
            FaithfulScreens.Open("menu");
            UpdateMenuPause();
            return true;
        }

        // Esc presses the last button of the top message box (Return the first, docs/game/ui-input.md section 5).
        if (FaithfulScreens.IsOpen("message"))
        {
            Frontend.HandleFrontendAction(new("message", Frontend.CancelButton, Frontend.CancelButton));
        }

        // While looting, one Esc closes the looter's and the body's windows together (their bags follow); the loot closes with its window.
        else if (FaithfulScreens.IsOpen("loot") && FaithfulScreens.OpenScreens[^1] is "inventory" or "bag" or "loot" or "loot-bag")
        {
            FaithfulScreens.Close("loot");
            FaithfulScreens.Close("inventory");
        }

        // So does trading: one Esc closes the trader with the customer's windows, whichever of them was clicked last (closing the trader ends the
        // trade).
        else if (FaithfulScreens.IsOpen("trader") && FaithfulScreens.OpenScreens[^1] is "inventory" or "bag" or "limbs" or "trader")
        {
            FaithfulScreens.Close("trader");
            FaithfulScreens.Close("inventory");
        }
        else if (!FaithfulScreens.Escape())
        {
            FaithfulScreens.Open("menu");
        }
        UpdateMenuPause();
        return true;
    }

    /// <summary>
    /// The keys of the open windows: deleting a save, the focus traversal, Enter and Space, the list keys, the conversation, a modal screen's
    /// hold on the keys; null when none of them takes <paramref name="key"/>.
    /// </summary>
    bool? ScreenKey(Key key, bool shift)
    {
        // Delete or Backspace deletes the save selected in the load, save or import window (LoadSaveWindow::keyPressed, docs/game/ui-screens.md
        // "Deleting a save").
        if (key is Key.Delete or Key.Backspace && FaithfulScreens.OpenScreens is [.., "load" or "save" or "import"]
                && Frontend.SelectedSlot is { } doomed)
        {
            Frontend.AskDeleteSave(doomed);
            return true;
        }

        // Tab bound to a window key (Inventory, Character...) opens it on a window with a box; the traversal keeps modal windows (#383).
        if (key == Key.Tab && (FaithfulScreens.HasModal || !GameHotkeys.WindowKey(key)) && FaithfulScreens.FocusNext(shift))
            return true;
        if (key == Key.Enter && FaithfulScreens.Submit()) return true;
        if (key == Key.Space && FaithfulScreens.ActivateFocused()) return true;

        // The list or combo box last clicked takes the MyGUI ListBox keys (docs/formats/mygui.md "Widget runtime").
        if (key switch
        {
            Key.Up => UiListKey.Up,
            Key.Down => UiListKey.Down,
            Key.PageUp => UiListKey.PageUp,
            Key.PageDown => UiListKey.PageDown,
            Key.Home => UiListKey.Home,
            Key.End => UiListKey.End,
            Key.Enter => UiListKey.Accept,
            _ => (UiListKey?)null,
        } is { } listKey && FaithfulScreens.ListKey(listKey)) return true;
        if (DialogueWindow.DialogueKey(key)) return true;

        // A modal screen (the menu, a message box, the character editor) takes the keys, except the host's own: quicksave / quickload, the screenshot
        // and the debug overlays.
        if (FaithfulScreens.HasModal)
            return !(GameHotkeys.Bound(InputAction.QuickSave, key) || GameHotkeys.Bound(InputAction.QuickLoad, key)
                || GameHotkeys.Bound(InputAction.Screenshot, key) || GameHotkeys.Bound(InputAction.ToggleStats, key)
                || GameHotkeys.Bound(InputAction.ToggleFps, key) || GameHotkeys.Bound(InputAction.CycleProfiler, key));
        return null;
    }

    /// <summary>
    /// The window hotkeys over the world: the next squad, the inventory and stats, the help, the overview tabs; false when none is bound to
    /// <paramref name="key"/> (or the host keeps it).
    /// </summary>
    bool WindowHotkey(Key key)
    {
        // change_squad (Tab by default): the squad bar shows the next squad with members (docs/game/ui-input.md section 6).
        if (GameHotkeys.Bound(InputAction.ChangeSquad, key) && !TitleOpen && !CharacterEditor.EditorOpen)
        {
            Say(new ActiveSquadCommand(-1) { Tick = Session.World.Tick });
            return true;
        }
        if (ReservedKeys.Contains(key)) return false;
        if (GameHotkeys.Bound(InputAction.ToggleInventory, key) || GameHotkeys.Bound(InputAction.ToggleCharacterStats, key))
        {
            ExecuteUiAction(GameHotkeys.Bound(InputAction.ToggleInventory, key) ? "inventory" : "stats", "");
            return true;
        }

        // toggle_help (F1, event 7): the Tutorials window (docs/game/ui-input.md section 6).
        if (GameHotkeys.Bound(InputAction.ToggleHelp, key) && !TitleOpen && !CharacterEditor.EditorOpen)
        {
            ToggleHelpWindow();
            return true;
        }

        // toggle_map / toggle_faction / toggle_research / toggle_crafting (M / unbound / T / Y) ask for overview tabs 0 / 1 / 2 / 3
        // (docs/game/ui-input.md section 6). Pressing the key of the tab already showing closes the window (engine choice: the original's toggle rule
        // is not traced).
        int? tab = GameHotkeys.Bound(InputAction.ToggleMap, key) ? 0 : GameHotkeys.Bound(InputAction.ToggleFaction, key) ? 1
            : GameHotkeys.Bound(InputAction.ToggleResearch, key) ? 2 : GameHotkeys.Bound(InputAction.ToggleCrafting, key) ? 3 : null;
        if (tab is { } t)
        {
            if (FaithfulScreens.IsOpen("overview") && FaithfulScreens.GetTab("overview", "TabsMain") == t)
            {
                FaithfulScreens.Close("overview");
            }
            else
            {
                FaithfulScreens.Open("overview");
                FaithfulScreens.SetTab("overview", "TabsMain", t);
            }
            return true;
        }
        return false;
    }

    /// <summary>
    /// A typed character to the screens' edit box.
    /// </summary>
    public void UiText(char value)
    {
        UiInput++;
        if (UiVisible) FaithfulScreens.TextInput(value);
    }

    /// <summary>
    /// A wheel step to the first list or view under the pointer that takes it; false when none did.
    /// </summary>
    public bool UiScroll(Vector2 p, float steps)
    {
        UiInput++;
        if (!UiVisible) return false;
        ArrangeUi();
        if (ScrollBars.InventoryWheel(p, steps)) return true;
        if (WorldMap.MapWheel(p, steps)) return true;
        if (!FaithfulScreens.Contains(p) && PortraitBar.SquadBarWheel(p, steps)) return true;
        if (OrdersPanel.JobsWheel(p, steps)) return true;
        if (SquadWindows.SquadsWheel(p, steps)) return true;
        if (FactionWindow.FactionWheel(p, steps)) return true;
        if (Production.ResearchWheel(p, steps)) return true;
        return CharacterEditor.EditorWheel(p, steps) || FaithfulScreens.Wheel(p, steps);
    }


    #endregion

    #region Edit boxes and the clipboard

    // The keys in the faithful windows' edit boxes (MyGUI 3.2.2 EditBox, docs/game/ui-input.md "Edit boxes") and the clipboard.
    /// <summary>
    /// The system clipboard's text and where copied text goes (the window's keyboard); null: the boxes keep a clipboard of their own.
    /// </summary>
    public Func<string?>? ClipboardRead { get; set; }
    /// <summary>
    /// Where copied text goes (the window's keyboard); null: the boxes keep a clipboard of their own.
    /// </summary>
    public Action<string>? ClipboardWrite { get; set; }

    /// <summary>
    /// An edit box has the keyboard: Ctrl+C and Ctrl+V are its, not the free camera's.
    /// </summary>
    public bool UiEditing => UiVisible && FaithfulScreens.HasKeyboardFocus;

    /// <summary>
    /// The edit command of a key (MyGUI's key table for Windows and Linux; Meitou uses Ctrl on every system): Ctrl+C / X / V / A / Z / Y, Ctrl+Insert
    /// copy, Shift+Insert paste, Shift+Delete cut, Ctrl+Home / End to the ends of the text. Shift is not looked at for Z, so Ctrl+Shift+Z undoes as
    /// in MyGUI.
    /// </summary>
    static UiEditKey? EditKeyFor(EngineKey key, bool ctrl, bool shift) => key switch
    {
        EngineKey.Left => UiEditKey.Left,
        EngineKey.Right => UiEditKey.Right,
        EngineKey.Up => UiEditKey.Up,
        EngineKey.Down => UiEditKey.Down,
        EngineKey.PageUp => UiEditKey.PageUp,
        EngineKey.PageDown => UiEditKey.PageDown,
        EngineKey.Home => ctrl ? UiEditKey.DocumentStart : UiEditKey.Home,
        EngineKey.End => ctrl ? UiEditKey.DocumentEnd : UiEditKey.End,
        EngineKey.Backspace => UiEditKey.Backspace,
        EngineKey.Delete => shift ? UiEditKey.Cut : UiEditKey.Delete,
        EngineKey.Insert => shift ? UiEditKey.Paste : ctrl ? UiEditKey.Copy : null,
        EngineKey.A when ctrl => UiEditKey.SelectAll,
        EngineKey.C when ctrl => UiEditKey.Copy,
        EngineKey.X when ctrl => UiEditKey.Cut,
        EngineKey.V when ctrl => UiEditKey.Paste,
        EngineKey.Z when ctrl => UiEditKey.Undo,
        EngineKey.Y when ctrl => UiEditKey.Redo,
        _ => null,
    };

    /// <summary>
    /// A key with an edit box focused: it takes every key, except that a read-only box takes only the edit keys, so Tab, Enter and the hotkeys go on
    /// as without a focus.
    /// </summary>
    bool EditFocusKey(EngineKey key, bool ctrl, bool shift)
    {
        var editing = EditKeyFor(key, ctrl, shift);
        if (editing is { } editKey) FaithfulScreens.EditKey(editKey, shift);
        if (FaithfulScreens.FocusReadOnly) return editing is not null;
        if (key == EngineKey.Tab) FaithfulScreens.FocusNext(shift);
        if (key == EngineKey.Enter)
        {
            FaithfulScreens.EditEnter(ctrl);
            // Enter accepts the faction name
            if (FaithfulScreens.Focus is { Screen: "overview", Widget: FactionWindow.FactionNameBox }) FaithfulScreens.Blur();
            // and the outpost name (FUN_140722d60)
            if (FaithfulScreens.Focus is { Screen: TownPanel.TownScreen, Widget: TownPanel.TownNameBox }) FaithfulScreens.Blur();
        }
        return true;
    }


    #endregion

    #region Speed buttons

    /// <summary>
    /// The speed buttons: pause, then the clock's three speeds.
    /// </summary>
    internal const int SpeedButtons = 4;

    /// <summary>
    /// A speed button: the clock's speed at that step (the pause button sets 0).
    /// </summary>
    void PressButton(int index)
    {
        Session.Simulation.SetSpeed(ButtonSpeed(index));   // the pause button (0) never resumes; only the pause key does
    }

    /// <summary>
    /// The speed of speed button <paramref name="index"/>: 0 for the pause, else the clock's speed at that step.
    /// </summary>
    double ButtonSpeed(int index) => index == 0 ? 0 : Session.Simulation.Speeds[index - 1];

    /// <summary>
    /// The lit speed button's action: the button whose speed is the clock's (none for a console speed off the steps).
    /// </summary>
    string SpeedButtonAction
    {
        get
        {
            double speed = Session.Simulation.Speed;
            int index = speed == 0 ? 0 : Session.Simulation.StepOf(speed) is var i and >= 0 ? i + 1 : -1;
            return $"meitou:speed.{index}";
        }
    }


    #endregion
}