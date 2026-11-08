using System.Globalization;
using System.Numerics;
using Meitou.Data.Ui;
using Meitou.Game.Host;
using Meitou.Game.Rendering.Overlays;
using Meitou.Game.Rendering.Ui;
using Meitou.Modding;
using Meitou.Simulation;
using Meitou.Simulation.Editing;
using Meitou.Data.Characters;
using Meitou.Game.Ui;
using Meitou.Simulation.Player;
using Meitou.Game.Editor;
using Meitou.Game.Title;

namespace Meitou.Game;

sealed partial class PlayerInterface
{
    public UiMode UiMode { get; set; }
    int lastMemberClick = -1;
    bool infoExpanded;
    long lastMemberClickAt;
    MyGuiLayout faithfulHud = null!;
    internal MyGuiResources uiResources = new();
    readonly UiHost uiHost = new();
    IDisposable? uiRegistration;
    internal UiPainter? uiPainter;
    internal UiPainter? SettingsPainter => uiPainter;
    bool settingsOpen;
    internal void SetSettingsOpen(bool value) { settingsOpen = value; if (value) CancelInventoryDrag(); UpdateMenuPause(); }
    internal MyGuiScreenHost faithfulScreens = null!;
    public event Action<MyGuiEvent>? ScreenAction;

    /// <summary>Raises <see cref="ScreenAction"/> for a feature class (an event is raised only by its own type).</summary>
    internal void RaiseScreenAction(MyGuiEvent e) => ScreenAction?.Invoke(e);

    /// <summary>The viewed floor of the interior view (the host's): read for the HUD's floor text, stepped by its arrows.</summary>
    public (Func<int> Get, Action<int> Step)? Floor { get; set; }
    public bool UiVisible { get; set; } = true;
    /// <summary>The share of the screen under fully opaque controls in the last drawn UI frame (<see cref="UiPainter.OpaqueCoverage"/>); 0 without a painter.</summary>
    public float OpaqueUiCoverage => uiPainter?.OpaqueCoverage ?? 0;
    internal IReadOnlyList<UiControl> UiFrame => ArrangeUi();

    public void LoadUi(UiMode mode, Func<string, string?> resolve)
    {
        UiMode = mode;
        uiResolve = resolve;
        var template = PotTemplate.Read(resolve("locale/en_GB/LC_MESSAGES/main.pot")
            ?? (Meitou.Content.GameInstall.Locate() is { } install ? Path.Combine(install.Root, "locale/en_GB/LC_MESSAGES/main.pot") : null));
        advancedHelpCatalog = NewGameHelpCatalog.From(template);
        statHelp = StatHelpCatalog.From(template);
        tutorialWindow.tutorialTexts = TutorialCatalog.From(template);
        MyGuiColours.LoadPalette(resolve("kenshi_colours.xml"));
        uiResources = MyGuiResources.Load("kenshi_init.xml", resolve, "kenshi_fonts.xml");
        var mainPanel = MyGuiReader.ReadLayout(resolve("Kenshi_MainPanel.layout") ?? throw new FileNotFoundException("Kenshi_MainPanel.layout not found."));
        faithfulHud = MyGuiTemplates.Expand(mainPanel, uiResources);
        relationsLineLayout = resolve("Kenshi_FactionRelationsLine.layout") is { } relationsLine ? MyGuiTemplates.Expand(MyGuiReader.ReadLayout(relationsLine), uiResources) : null;
        ordersRowLayout = resolve("Kenshi_OrdersPanelOrder.layout") is { } ordersRow ? MyGuiTemplates.Expand(MyGuiReader.ReadLayout(ordersRow), uiResources) : null;
        squadBoxLayout = resolve("Kenshi_SquadBox.layout") is { } squadBox ? MyGuiTemplates.Expand(MyGuiReader.ReadLayout(squadBox), uiResources) : null;
        messageLayout = resolve("Kenshi_MessagePanel.layout") is { } messages ? MyGuiTemplates.Expand(MyGuiReader.ReadLayout(messages), uiResources) : null;
        stealthView.taskBarLayout = resolve("Kenshi_ProgressBarPanel.layout") is { } taskBar ? MyGuiTemplates.Expand(MyGuiReader.ReadLayout(taskBar), uiResources) : null;
        tutorialWindow.tutorialPanelLayout = resolve("Kenshi_TutorialPanel.layout") is { } tutorialPanel ? MyGuiTemplates.Expand(MyGuiReader.ReadLayout(tutorialPanel), uiResources) : null;
        tutorialWindow.tutorialLineLayout = resolve("Kenshi_TutorialTooltipPanel.layout") is { } tutorialLine ? MyGuiTemplates.Expand(MyGuiReader.ReadLayout(tutorialLine), uiResources) : null;
        messageClient = messageLayout?.Widgets.FirstOrDefault(w => w.Properties.GetValueOrDefault("CaptionOwner") == "MessageTextBox")?.Name;
        faithfulScreens = new(uiResources) { ClipboardReader = () => ClipboardRead?.Invoke(), ClipboardWriter = text => ClipboardWrite?.Invoke(text), CaretAt = p => uiPainter?.EditCaretAt(p) };
        foreach (var (id, file, modal) in FaithfulScreenFiles)
        {
            var layout = MyGuiReader.ReadLayout(resolve(file + ".layout") ?? throw new FileNotFoundException(file));
            if (id is "new-game" or "import") layout = WithAdvancedPanel(layout, MyGuiReader.ReadLayout(resolve("Kenshi_AdvancedOptionsWindow.layout") ?? throw new FileNotFoundException("Kenshi_AdvancedOptionsWindow.layout")));
            if (id == "load") layout = WithSaveFilter(layout);
            if (id == "editor") (editorLayout, editorRowsShape) = (layout, "");
            if (id == "inventory") tradeWindows.characterInventoryLayout = layout;
            if (id == "leveleditor") layout = LevelEditorPanel.LevelEditorBar(layout);
            faithfulScreens.Register(id, layout, modal, centreRoot: id is "file");
        }
        tradeWindows.animalInventoryLayout = resolve("Kenshi_InventoryAnimalWindow.layout") is { } animalWindow ? MyGuiReader.ReadLayout(animalWindow) : null;
        tradeWindows.characterRowLayout = resolve("Kenshi_CharacterSelectionBox.layout") is { } characterRow ? MyGuiTemplates.Expand(MyGuiReader.ReadLayout(characterRow), uiResources) : null;
        // The title's start, load and import windows leave its buttons live (the original's title windows are not modal): another button replaces them.
        faithfulScreens.PassThrough = id => id is "new-game" or "load" or "import" && faithfulScreens.IsOpen("title");
        faithfulScreens.RegisterPanel(TownScreen, TownPanelLayout(mainPanel));
        faithfulScreens.Register(FeedbackPanel.FeedbackScreen, FeedbackPanel.FeedbackLayout(), modal: true);
        feedbackPanel.feedbackButtonLayout = MyGuiTemplates.Expand(FeedbackPanel.FeedbackButtonLayout(), uiResources);
        faithfulScreens.Follow("bag", "inventory");   // the bag window is married to the inventory, opened and closed by OPEN BAG only
        faithfulScreens.Follow("loot-bag", "loot");
        faithfulScreens.Activated += FaithfulAction;
        uiHost.Diagnostic += d => Console.Error.WriteLine($"ui        {d.Extension} {d.Operation}: {d.Error.Message}");
        uiRegistration = uiHost.Register(new PlayerUiExtension(ExecuteUiAction));
    }

    /// <summary>The original layouts the faithful mode opens as screens: id, layout file, modal.</summary>
    internal static readonly (string Id, string File, bool Modal)[] FaithfulScreenFiles =
    [
        ("inventory", "Kenshi_InventoryCharacterWindow", false), ("stats", "Kenshi_StatsWindow", false),
        ("overview", "Kenshi_OverviewWindow", false), ("menu", "Kenshi_MainMenuPopupPanel", true),
        ("title", "Kenshi_MainMenu", true), ("new-game", "Kenshi_NewGamePanel", true),
        ("save", "Kenshi_SaveGamePanel", true),
        ("load", "Kenshi_LoadGamePanel", true), ("import", "Kenshi_ImportGamePanel", true), ("message", "Kenshi_MessageBox", true),
        ("bag", "Kenshi_InventoryGenericWindow", false), ("loot", "Kenshi_InventoryCharacterWindow", false), ("loot-bag", "Kenshi_InventoryGenericWindow", false), ("limbs", "Kenshi_LimbReplacementWindow", false), ("trader", "Kenshi_InventoryTraderWindow", false),
        ("building", "Kenshi_GenericBuildingWindow", false), ("bench", "Kenshi_InventoryResearchWindow", false), ("storage", "Kenshi_InventoryGenericWindow", false),
        ("build", "Kenshi_BuildWindow", false), ("editor", "Kenshi_CharacterEditor", true), ("dialogue", "Kenshi_ConversationPanel", true),
        ("file", "Kenshi_OpenSaveFileDialog", true), ("prospect", "Kenshi_ProspectingWindow", false), ("tutorials", "Kenshi_TutorialsWindow", false), ("leveleditor", "Kenshi_LevelEditor", false),
        (TradeWindows.CharacterTradeScreen, "Kenshi_CharacterSelectionWindow", false),
    ];

    /// <summary>The expanded layout and the current bindings of the HUD (<c>"hud"</c>) or of a faithful screen, for the layout audit test.</summary>
    internal (MyGuiLayout Layout, UiBindings Bindings) FaithfulBindings(string id) =>
        id == "hud" ? (faithfulHud, BindUi()) : (faithfulScreens.Layout(id), BindFaithfulScreen(id));

    public void CreateUiPainter(DebugOverlay overlay, Func<string, string?> resolve)
    {
        uiPainter = new(overlay, uiResources, resolve) { Mode = UiMode, FontSizeSetting = fontSize };
        findAsset = resolve;
        // The screens' pictures are read ahead (the overview's map is 43 MB), so that opening a screen only uploads them.
        uiPainter.Prefetch(FaithfulScreenFiles.SelectMany(s => faithfulScreens.Layout(s.Id).Widgets).Concat(faithfulHud.Widgets)
            .Select(w => w.Properties.GetValueOrDefault("ImageTexture")).OfType<string>());
    }
    public void DisposeUi() { uiPainter?.Dispose(); uiPainter = null; uiRegistration?.Dispose(); uiRegistration = null; }
    /// <summary>Future code-mod loader entry point; disposal removes the extension's owned registrations.</summary>
    public IDisposable RegisterUiExtension(IUiExtension extension) => uiHost.Register(extension);
    public bool ContainsUi(Vector2 p)
    {
        if (!UiVisible) return false;
        var controls = ArrangeUi();
        return faithfulScreens.Contains(p) || MyGuiPresentation.HitTest(controls, p.X, p.Y) is not null;
    }

    public void SetUiMode(UiMode mode)
    {
        UiMode = mode;
        if (uiPainter is not null) uiPainter.Mode = mode;
        dragFrom = null;
        dragging = false;
        steering = false;
        worldMap.mapDrag = null;
        CancelInventoryDrag();
        UpdateMenuPause();
    }

    UiBindings BindUi()
    {
        // The original sizes the HUD's template parts by height / 1080 (Observed: the squad tabs' 37 px HeaderPlace at 1440 lines, docs/game/ui-screens.md).
        var b = new UiBindings { TemplateScale = Height / 1080f };
        var lead = Lead();
        b.Text["clock"] = $"{session.Clock.DayText}  {session.Clock.TimeText}";
        b.Text["DayText"] = session.Clock.DayText;
        b.Text["TimeText"] = session.Clock.TimeText;
        // The exe's money format string is c.{1,num} (docs/game/ui-screens.md section 1): no space after the point.
        b.Text["MoneyAmountText"] = "c." + session.World.Player.Money.ToString("N0", CultureInfo.InvariantCulture);
        // The name bar draws its own lines over the widget (PlayerInterface.HudText.cs).
        b.Text["NameText"] = lead?.Name ?? "No character selected";
        b.Properties["NameText"] = new Dictionary<string, string> { ["SkipCaption"] = "true" };
        b.Text["hunger"] = lead?.Body is { } hungry ? $"Hunger {hungry.Hunger:0.00} / 3" : "";
        b.Text["condition"] = lead?.Body is { } status ? status.Dead ? "Dead" : status.Unconscious ? "Unconscious" : status.NeedsAid ? "Needs first aid" : "Conscious" : "No character selected";
        b.Text["blood"] = lead?.Body is { } blood ? $"Blood   {blood.Blood * 100:0}%" : "Blood --";
        b.Text["body"] = lead?.Body is { } worst ? $"Worst body part   {worst.WorstPart * 100:0}%" : "Body --";
        var roster = session.CurrentSnapshot.Characters.Where(c => c.IsPlayer).ToArray();
        b.Text["squad.count"] = roster.Length.ToString(CultureInfo.InvariantCulture);
        for (int i = 0; i < Math.Min(8, roster.Length); i++) b.Text[$"squad.{i}.name"] = roster[i].Name;
        b.Text["members"] = $"{PlayerCharacters().Count} members | {Selected().Count} selected";
        b.Text["ExtendedStatsText"] = "";
        // The exe sets the money label without the layout's colon (docs/game/ui-screens.md section 4).
        b.Text["MoneyLabelText"] = "Money";
        // The bounty panel draws its own Bounty: line over the widget (PlayerInterface.HudText.cs).
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
        var own = Selected().FirstOrDefault() ?? lead;   // the orders, jobs and squad tab stay the player's while another faction's character is inspected
        BindOrders(b, own);
        foreach (string name in new[] { "LoadingPanel", "TownPanel", "TooltipPanel", "MessagePanel" }) b.Hidden.Add(name);
        BindBiome(b);
        // The viewed floor (the exe's "Floor {1,num}", docs/game/ui-screens.md section 4; docs/formats/zones.md "Interior view"); the arrows step it as PageUp / PageDown do.
        b.Text["FloorText"] = Loc.Tr("Floor {1,num}", Floor?.Get() ?? 0);
        b.Actions["FloorArrowUp"] = "meitou:floor-up";
        b.Actions["FloorArrowDown"] = "meitou:floor-down";
        b.Actions["BuildButton"] = "meitou:build";
        if (buildMode) foreach (string name in BuildHiddenHud) b.Hidden.Add(name);
        if (lead is null || lead.Bounties.Count == 0 && lead.Slave is null && lead.Status is null) b.Hidden.Add("BountyPanel");   // a bounty or a status line (Slave, Leader, Committing Crime!...) shows it
        // The information panel starts collapsed (as the original's, docs/game/ui-screens.md section 4): the extending part (name, state lines, extended stats) slid down
        // until its bottom is the panel's, behind the stats panel, so only the name and the state lines show; the ^ button between them toggles it.
        if (!infoExpanded && faithfulHud.Find("InformationPanel") is { } info && faithfulHud.Find("ExtendingPanel") is { } extending)
        {
            var infoRect = info.Position.RelativeTo(new(0, 0, Width, Height), true);
            var rect = extending.Position.RelativeTo(infoRect, true);
            b.Positions["ExtendingPanel"] = rect with { Y = infoRect.Y + infoRect.Height - rect.Height };
        }
        b.Actions["ExtendedStatsButton"] = "meitou:extendedstats";
        if (session.Simulation.Speed != 0) b.Hidden.Add("PausedPanel");
        var buildingNow = BuildingFocus();
        bool buildingFocus = buildingNow is not null;
        BindMedicalBars(b, lead, buildingNow is { } shownBuilding ? BuildingRows(shownBuilding) : null);
        // A selected building shows its status word in the medical header and its condition rows below it.
        if (buildingNow is not null)
        {
            var (_, _, _, buildingStatus, bad, _) = BuildingLines(buildingNow);
            b.Text["HealthText"] = buildingStatus;
            b.Properties["HealthText"] = new Dictionary<string, string> { ["TextAlign"] = "Center", ["TextColour"] = bad ? BadColour : MyGuiColours.TemplateInk(uiResources, "Kenshi_TextboxStandardText") };
        }
        BindJobs(b, own);
        BindSquadTabs(b);
        if (lead is null && !buildingFocus) b.Hidden.Add("InformationPanel");
        if (lead is null && !buildingFocus) b.Hidden.Add("MedicalPanel");
        // Buttons without a backend are disabled, except those the original never greys out (the build and floor buttons look live there in the open).
        foreach (var w in faithfulHud.Widgets.Where(w => w.Type == "Button" && !b.Actions.ContainsKey(w.Name) && w.Name is not ("BuildButton" or "FloorArrowUp" or "FloorArrowDown"))) b.Disabled.Add(w.Name);
        if (Selected().Count == 0 && !faithfulScreens.IsOpen("prospect")) b.Disabled.Add("OrdersProspectButton");
        if (Selected().Count == 0)
            foreach (string name in new[] { "OrdersMedicButton", "OrdersRescueButton", "medic", "rescue", "stop" }) b.Disabled.Add(name);
        BindTooltips(b);
        return b;
    }

    IReadOnlyList<UiControl> ArrangeUi()
    {
        SyncScreens();
        bool hud = !TitleOpen && !EditorOpen && !conversationShown;   // the conversation panel covers the HUD bar (engine reading of its layout)
        SyncTownPanel(hud);
        if (!TitleOpen) titleMenu.ReleaseTitleLogo();
        var controls = ArrangedBase(hud);
        AddFrameControls(controls, hud);
        var top = MyGuiPresentation.HitTest(controls, mouse.X, mouse.Y);
        pointerOverUi = faithfulScreens.Contains(mouse) || top is not null;
        pointerOverButton = top is { Enabled: true } && (top.Type == "Button" || top.Action.Length > 0);
        AddHoverLabel(controls);
        inspector.AddInspectorPanel(controls);
        return controls;
    }

    /// <summary>Once an arrangement: the screens that follow the world opened, closed or filled.</summary>
    void SyncScreens()
    {
        tradeWindows.SyncTradeScreens();
        SyncMachineScreens();
        tradeWindows.SyncLootScreen();
        SyncPinnedInventories();
        tradeWindows.SyncInventoryLayouts();
        SyncLimbsScreen();
        SyncDialogueScreen();
        tradeWindows.SyncCharacterTradeScreen();
        ValidateInventoryDrag();
        faithfulScreens.FocusRing = UiMode == UiMode.Meitou;
    }

    /// <summary>
    /// The interface's base (<see cref="ArrangedBase"/>): the HUD and its slots, the squad bar, the HUD text, the tutorial panel and the open screens.
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
        if (hud) controls.AddRange(uiHost.Arrange(UiMode, Width, Height, b, slots));
        squadBarRow = 0;
        if (controls.FirstOrDefault(c => c.Name == "SquadPanel") is { } squadPanel) AddSquadPortraits(controls, squadPanel.Bounds);
        AddHudText(controls);
        if (hud) AddJobScrollBar(controls);
        AddJobRowParts(controls);
        if (hud) tutorialWindow.AddTutorialPanel(controls);
        AddScreens(controls);
        return controls;
    }

    /// <summary>The open screens over <paramref name="controls"/>.</summary>
    void AddScreens(List<UiControl> controls)
    {
        foreach (var screen in faithfulScreens.Arrange(Width, Height, BindFaithfulScreen, AddFaithfulContent))
        {
            if (feedbackPanel.ShotWithoutMenus && FeedbackPanel.FeedbackShotHides.Contains(screen.Id)) continue;
            if (screen.Id == "editor") controls.AddRange(EditorBackdrop());
            controls.AddRange(screen.Controls.Where(c => UiMode == UiMode.Faithful ||
                !c.Properties.TryGetValue("ImageResource", out var resource) || resource != "Kenshi_TitleScreen"));
            if (screen.Id == "title" && UiMode == UiMode.Meitou && !showCredits) AddTitleMark(controls);
        }
    }

    /// <summary>The Meitou title's mark, top right: the logo once it is loaded, the word until then.</summary>
    void AddTitleMark(List<UiControl> controls)
    {
        if (titleMenu.TitleLogoReady())
        {
            float logoWidth = Math.Min(Width * 0.3f, Height * 0.6f);
            controls.Add(new("MeitouTitle", "ImageBox", new(Width * 0.975f - logoWidth, Height * 0.04f, logoWidth, logoWidth * 0.469f), "", "", "", true,
                new Dictionary<string, string> { ["ImageTexture"] = TitleMenu.TitleLogoTexture }, Vector4.Zero, Pickable: false));
        }
        else
            controls.Add(new("MeitouTitle", "TextBox", new(Width * 0.6f, Height * 0.04f, Width * 0.37f, Height * 0.14f), "KENSHI", "", "", true,
                new Dictionary<string, string> { ["FontName"] = "Meitou_TitleFont", ["TextAlign"] = "Right VCenter" },
                Vector4.Zero, new Vector4(0.89f, 0.84f, 0.71f, 1), TextScale: Height / 720f));
    }

    /// <summary>
    /// Every frame over the base: what moves with the camera or real time. The name tags, ground item labels and task bars go under everything (the original's
    /// Back layer); the floaters and speech bubbles just over them, under the HUD and every screen, the map included (its Dialog layer, below Middle where the HUD is,
    /// <c>core_layer.xml</c>).
    /// </summary>
    void AddFrameControls(List<UiControl> controls, bool hud)
    {
        if (hud) tutorialWindow.AddTutorialHighlight(controls);
        AddNotice(controls);
        AddInventoryDrag(controls);

        var nameTags = new List<UiControl>();
        AddItemLabels(nameTags);
        stealthView.AddNameTags(nameTags);
        stealthView.AddTaskBars(nameTags);
        var dialogLayer = new List<UiControl>();
        damageFloaters.AddFloaters(dialogLayer);
        speechBubbles.AddSpeech(dialogLayer);
        controls.InsertRange(0, [.. nameTags, .. dialogLayer]);

        AddBuildReadout(controls);
        AddPointerChance(controls);
        AddContextMenu(controls);
        feedbackPanel.AddFeedbackButton(controls);
    }

    /// <summary>
    /// The HUD's last arrangement and what it was made from (<see cref="MyGuiPresentation.Arrange"/> depends on nothing else): reused while the bindings stay the same, so most
    /// frames skip the layout; remade at least every <see cref="HudRearrangeCalls"/> calls in case a binding is missed by <see cref="UiBindings.ContentEquals"/>.
    /// </summary>
    UiBindings? hudBindings;
    IReadOnlyList<UiControl> hudControls = [];
    (MyGuiLayout Layout, int Width, int Height, IReadOnlyList<string> Layers, Meitou.Localisation.GameLanguage Language) hudKey;
    int hudReuses;
    const int HudRearrangeCalls = 30;

    List<UiControl> ArrangeHud(UiBindings b)
    {
        (MyGuiLayout, int, int, IReadOnlyList<string>, Meitou.Localisation.GameLanguage) key = (faithfulHud, Width, Height, uiResources.Layers, Meitou.Localisation.Loc.Language);
        if (hudBindings is null || !key.Equals(hudKey) || ++hudReuses >= HudRearrangeCalls || !b.ContentEquals(hudBindings))
        {
            hudControls = MyGuiPresentation.Arrange(faithfulHud, Width, Height, b, uiResources.Layers);
            (hudBindings, hudKey, hudReuses) = (b, key, 0);
        }
        return [.. hudControls];
    }

    bool UiMouseDown(Vector2 p)
    {
        if (!UiVisible) return false;
        var controls = ArrangeUi();
        if (InventoryPointerDown(p)) return true;
        if (worldMap.MapPointerDown(p)) return true;
        if (SquadPointerDown(p)) return true;
        if (faithfulScreens.PointerDown(p)) { UpdateMenuPause(); return true; }
        if (JobDragStart(controls, p)) return true;
        if (MyGuiPresentation.HitTest(controls, p.X, p.Y) is not { } c) return false;
        if (SquadTabClick(c)) return true;
        if (c.Action.StartsWith("faithful:", StringComparison.Ordinal))
        {
            // A second click on the open tab's button closes the overview.
            int tab = c.Name == "ShortcutTechButton" ? 2 : c.Name == "ShortcutSquadButton" ? 4 : 0;
            if (faithfulScreens.IsOpen("overview") && faithfulScreens.GetTab("overview", "TabsMain") == tab)
                faithfulScreens.Close("overview");
            else
            {
                faithfulScreens.Open("overview");
                faithfulScreens.SetTab("overview", "TabsMain", tab);
            }
            UpdateMenuPause();
            return true;
        }
        // By name: the thumb has no action.
        if (HudScrollKey(c.Name) is { } key)
        {
            if (c.Name.EndsWith("-thumb", StringComparison.Ordinal)) ScrollThumbDown(key, c, p);
            else HudScrollAction(key, c.Action);
            return true;
        }
        uiHost.Dispatch(c);
        return true;
    }

    /// <summary>The F1 key and the HLP button share one handler: toggle the Tutorials window (FUN_1406e1f20; ui-screens.md).</summary>
    void ToggleHelpWindow()
    {
        if (faithfulScreens.IsOpen("tutorials")) faithfulScreens.Close("tutorials"); else faithfulScreens.Open("tutorials");
    }

    void ExecuteUiAction(string action, string argument)
    {
        SoundForAction(action, argument);
        if (action.StartsWith("speed:", StringComparison.Ordinal)) PressButton(int.Parse(action[6..], CultureInfo.InvariantCulture));
        else if (action is "inventory" or "stats")
        {
            // toggle_inventory does nothing, opening or closing, without a living lead of the player's (FUN_140724980); the selection holds only player characters.
            if (action == "inventory" && (InventoryOwner() is not { } owner || owner.Body is { Dead: true })) return;
            if (faithfulScreens.IsOpen(action))
            {
                faithfulScreens.Close(action);
                if (action == "inventory") { faithfulScreens.Close("bag"); faithfulScreens.Close("limbs"); }
            }
            // The lead's pinned window comes to the front instead of a second window on them.
            else if (action == "inventory" && InventoryOwner() is { } lead && pinnedInventories.FirstOrDefault(p => p.Value == lead.Id).Key is { } slot) faithfulScreens.Open(slot);
            else faithfulScreens.Open(action);
        }
        else if (action == "help") ToggleHelpWindow();
        else if (action == "extendedstats") infoExpanded = !infoExpanded;
        else if (action is "floor-up" or "floor-down") Floor?.Step(action == "floor-up" ? 1 : -1);
        else if (action == "build") ToggleBuildMode();
        else if (action == "context") ContextAction(argument);
        else if (action == "squad") Select(ActiveSquadMembers(), false);
        else if (action == "member")
        {
            // A portrait click selects (Shift: adds), a double click centres and follows (docs/game/ui-input.md section 7).
            int index = int.Parse(argument, CultureInfo.InvariantCulture);
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            bool doubleClick = index == lastMemberClick && System.Diagnostics.Stopwatch.GetElapsedTime(lastMemberClickAt, now).TotalSeconds <= DoubleTapSeconds;
            (lastMemberClick, lastMemberClickAt) = (doubleClick ? -1 : index, now);
            SelectMember(index, pressShift, doubleClick);
        }
        else if (action == "stop") Say(new StopCommand(SelectedIds()) { Tick = session.World.Tick });
        else if (action == "medic") Say(new Meitou.Simulation.FirstAid.MedicJobCommand(SelectedIds(), SlowModifier) { Tick = session.World.Tick });
        else if (action == "rescue") Say(new Meitou.Simulation.Carrying.RescueJobCommand(SelectedIds(), SlowModifier) { Tick = session.World.Tick });
        else if (action == "prospect") prospecting.ProspectAction();
        else if (action.StartsWith("building-", StringComparison.Ordinal) || action.StartsWith("machine-", StringComparison.Ordinal)) BuildingAction(action);
        else if (action == "tutorial") tutorialWindow.TutorialAction(argument);
        else if (action == "job-remove") RemoveJob(argument);
        else if (action.StartsWith("order.", StringComparison.Ordinal) && Enum.TryParse(action[6..], out CharacterOrders order))
            Say(new OrdersCommand(SelectedIds(), order) { Tick = session.World.Tick });
        else if (action.StartsWith("runspeed.", StringComparison.Ordinal))
        {
            int step = int.Parse(action[9..], CultureInfo.InvariantCulture);
            Say(new RunSpeedCommand(SelectedIds(), step) { Tick = session.World.Tick });
        }
    }

    /// <summary>The original's background-loading bar, drawn over the interface in the Faithful mode (the host steps it).</summary>
    internal ResourceLoadBar? LoadBar { get; set; }

    void DrawGameUi(DebugOverlay overlay, int width, int height, DebugOverlay.OverlayPass? pass = null)
    {
        IReadOnlyList<UiControl> controls = ArrangeUi();
        if (cameraGrid) DrawCameraGrid(overlay, width, height);
        if (UiMode == UiMode.Faithful && LoadBar is { Visible: true } bar) controls = [.. controls, .. bar.Controls(uiResources, width, height)];
        uiPainter?.Draw(controls, width, height, mouse, SpeedButtonAction,
            minimumTextPixels: TitleOpen ? 0 : StandardPixels, pass: pass);
    }

    /// <summary>With the interface hidden (F7, <c>hud hide</c>) only the speech bubbles are drawn, so a filmed clean picture keeps the <c>say</c> commentary.</summary>
    public void DrawSpeechOnly(DebugOverlay overlay, int width, int height)
    {
        Width = width;
        Height = height;
        var controls = new List<UiControl>();
        speechBubbles.AddSpeech(controls);
        if (controls.Count == 0) return;
        uiPainter?.Draw(controls, width, height, mouse, SpeedButtonAction, minimumTextPixels: StandardPixels);
        overlay.Flush(width, height);
    }

    /// <summary>The rule-of-thirds camera grid (Shift+;, toggle_camera_grid, event 0x29: the widget "CameraGrid", <c>FUN_1406e5fc0</c>), under the interface.</summary>
    bool cameraGrid;
    int gridHold;
    bool gridRestore;

    /// <summary>
    /// The screenshot event with the camera grid shown (the frame-end callback <c>FUN_14082b6e0</c>, <b>Verified</b>): the "CameraGrid" widget is hidden and the picture is taken two frames later,
    /// when the widget is shown again (<see cref="EndScreenshot"/>); with the grid hidden or missing the picture is taken at once. Returns whether the picture is taken this frame.
    /// </summary>
    public bool GateScreenshot(bool requested)
    {
        if (gridHold > 0) return --gridHold == 0;
        if (!requested) return false;
        if (!cameraGrid) return true;
        (cameraGrid, gridRestore, gridHold) = (false, true, 2);
        return false;
    }

    /// <summary>The picture is taken: the camera grid hidden for it is shown again.</summary>
    public void EndScreenshot()
    {
        if (!gridRestore) return;
        (cameraGrid, gridRestore) = (true, false);
    }


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

    /// <summary>For screenshot reviews: open the same panel through its registered button action.</summary>
    public void ShowUiPanel(string? panel, bool? expandInformation = null)
    {
        if (expandInformation is { } expanded) infoExpanded = expanded;
        if (panel?.StartsWith("editor", StringComparison.Ordinal) == true)
        {
            var tab = panel == "editor-face" ? EditorTab.Face : panel == "editor-hair" ? EditorTab.Hair : EditorTab.Body;
            if (!EditorOpen) { faithfulScreens.CloseAll(); if (buildMode) SetBuildMode(false); if (!OpenEditor(EditKind.Creation, PlayerCharacters())) return; }
            editor!.Tab = tab;
            return;
        }
        faithfulScreens.CloseAll();
        UpdateMenuPause();
        if (panel is null) return;
        // The title with one of its buttons pressed (its window or the credits, placed as from the title).
        if (panel.StartsWith("title-", StringComparison.Ordinal))
        {
            ShowTitle();
            string button = panel[6..] switch { "load" => "LoadGameButton", "import" => "ImportGameButton", "new-game" => "NewGameButton", "exit" => "ExitButton", _ => "CreditsButton" };
            HandleFrontendAction(new("title", button, button));
            UpdateMenuPause(); return;
        }
        if (panel is "overview" or "overview-faction" or "menu" or "new-game" or "save" or "load" or "title")
        {
            if (panel == "title") ShowTitle();
            else if (panel is "save" or "load") OpenSlots(panel);
            else if (panel == "overview-faction") { faithfulScreens.Open("overview"); faithfulScreens.SetTab("overview", "TabsMain", FactionTab); }
            else { faithfulScreens.Open(panel); if (panel == "overview") faithfulScreens.SetTab(panel, "TabsMain", 0); }
            UpdateMenuPause(); return;
        }
        // The first building found on a grid of picks over the view (its HUD panels); any other panel drops it.
        if (panel != "building" && selectedBuilding is not null) SetBuilding(null);
        if (panel == "building")
        {
            for (int y = Height / 8; selectedBuilding is null && y < Height * 2 / 3; y += 24)
                for (int x = Width / 8; selectedBuilding is null && x < Width * 7 / 8; x += 24)
                    if (PickBuilding(new(x, y)) is { Record: not null } found) SetBuilding(found);
            return;
        }
        if (panel == "build") { if (buildMode) faithfulScreens.Open("build"); else SetBuildMode(true); return; }
        if (panel is not ("inventory" or "stats" or "limbs")) throw new ArgumentException("Unknown UI panel.", nameof(panel));
        var control = ArrangeUi().First(c => c.Action == $"meitou:{(panel == "limbs" ? "inventory" : panel)}");
        uiHost.Dispatch(control);
        if (panel == "limbs") { limbsOwner = InventoryOwner()?.Id ?? CharacterId.None; faithfulScreens.Open("limbs"); PlaceLimbsWindow(); }
    }
}
