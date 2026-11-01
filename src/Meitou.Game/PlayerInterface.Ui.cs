using System.Globalization;
using System.Numerics;
using Meitou.Data.Ui;
using Meitou.Rendering;
using Meitou.Rendering.Ui;
using Meitou.Simulation;
using Meitou.Simulation.Editing;
using Meitou.Data.Characters;
using Meitou.Ui;

namespace Meitou.Game;

sealed partial class PlayerInterface
{
    public UiMode UiMode { get; set; }
    int lastMemberClick = -1;
    bool infoExpanded;
    long lastMemberClickAt;
    MyGuiLayout faithfulHud = null!;
    MyGuiResources uiResources = new();
    readonly UiHost uiHost = new();
    IDisposable? uiRegistration;
    UiPainter? uiPainter;
    internal UiPainter? SettingsPainter => uiPainter;
    bool settingsOpen;
    internal void SetSettingsOpen(bool value) { settingsOpen = value; if (value) CancelInventoryDrag(); UpdateMenuPause(); }
    MyGuiScreenHost faithfulScreens = null!;
    public event Action<MyGuiEvent>? ScreenAction;

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
        tutorialTexts = TutorialCatalog.From(template);
        MyGuiColours.LoadPalette(resolve("kenshi_colours.xml"));
        uiResources = MyGuiResources.Load("kenshi_init.xml", resolve, "kenshi_fonts.xml");
        var mainPanel = MyGuiReader.ReadLayout(resolve("Kenshi_MainPanel.layout") ?? throw new FileNotFoundException("Kenshi_MainPanel.layout not found."));
        faithfulHud = MyGuiTemplates.Expand(mainPanel, uiResources);
        relationsLineLayout = resolve("Kenshi_FactionRelationsLine.layout") is { } relationsLine ? MyGuiTemplates.Expand(MyGuiReader.ReadLayout(relationsLine), uiResources) : null;
        squadBoxLayout = resolve("Kenshi_SquadBox.layout") is { } squadBox ? MyGuiTemplates.Expand(MyGuiReader.ReadLayout(squadBox), uiResources) : null;
        messageLayout = resolve("Kenshi_MessagePanel.layout") is { } messages ? MyGuiTemplates.Expand(MyGuiReader.ReadLayout(messages), uiResources) : null;
        taskBarLayout = resolve("Kenshi_ProgressBarPanel.layout") is { } taskBar ? MyGuiTemplates.Expand(MyGuiReader.ReadLayout(taskBar), uiResources) : null;
        tutorialPanelLayout = resolve("Kenshi_TutorialPanel.layout") is { } tutorialPanel ? MyGuiTemplates.Expand(MyGuiReader.ReadLayout(tutorialPanel), uiResources) : null;
        tutorialLineLayout = resolve("Kenshi_TutorialTooltipPanel.layout") is { } tutorialLine ? MyGuiTemplates.Expand(MyGuiReader.ReadLayout(tutorialLine), uiResources) : null;
        messageClient = messageLayout?.Widgets.FirstOrDefault(w => w.Properties.GetValueOrDefault("CaptionOwner") == "MessageTextBox")?.Name;
        faithfulScreens = new(uiResources) { ClipboardReader = () => ClipboardRead?.Invoke(), ClipboardWriter = text => ClipboardWrite?.Invoke(text), CaretAt = p => uiPainter?.EditCaretAt(p) };
        foreach (var (id, file, modal) in FaithfulScreenFiles)
        {
            var layout = MyGuiReader.ReadLayout(resolve(file + ".layout") ?? throw new FileNotFoundException(file));
            if (id is "new-game" or "import") layout = WithAdvancedPanel(layout, MyGuiReader.ReadLayout(resolve("Kenshi_AdvancedOptionsWindow.layout") ?? throw new FileNotFoundException("Kenshi_AdvancedOptionsWindow.layout")));
            if (id == "load") layout = WithSaveFilter(layout);
            if (id == "editor") (editorLayout, editorRowsShape) = (layout, "");
            if (id == "inventory") characterInventoryLayout = layout;
            faithfulScreens.Register(id, layout, modal, centreRoot: id is "file");
        }
        animalInventoryLayout = resolve("Kenshi_InventoryAnimalWindow.layout") is { } animalWindow ? MyGuiReader.ReadLayout(animalWindow) : null;
        characterRowLayout = resolve("Kenshi_CharacterSelectionBox.layout") is { } characterRow ? MyGuiTemplates.Expand(MyGuiReader.ReadLayout(characterRow), uiResources) : null;
        // The title's start, load and import windows leave its buttons live (the original's title windows are not modal): another button replaces them.
        faithfulScreens.PassThrough = id => id is "new-game" or "load" or "import" && faithfulScreens.IsOpen("title");
        faithfulScreens.RegisterPanel(TownScreen, TownPanelLayout(mainPanel));
        faithfulScreens.Register(FeedbackScreen, FeedbackLayout(), modal: true);
        feedbackButtonLayout = MyGuiTemplates.Expand(FeedbackButtonLayout(), uiResources);
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
        ("file", "Kenshi_OpenSaveFileDialog", true), ("prospect", "Kenshi_ProspectingWindow", false), ("tutorials", "Kenshi_TutorialsWindow", false),
        (CharacterTradeScreen, "Kenshi_CharacterSelectionWindow", false),
    ];

    /// <summary>The expanded layout and the current bindings of the HUD (<c>"hud"</c>) or of a faithful screen, for the layout audit test.</summary>
    internal (MyGuiLayout Layout, UiBindings Bindings) FaithfulBindings(string id) =>
        id == "hud" ? (faithfulHud, BindUi()) : (faithfulScreens.Layout(id), BindFaithfulScreen(id));

    public void CreateUiPainter(DebugOverlay overlay, Func<string, string?> resolve)
    {
        uiPainter = new(overlay, uiResources, resolve) { Mode = UiMode };
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
        mapDrag = null;
        CancelInventoryDrag();
        UpdateMenuPause();
    }

    UiBindings BindUi()
    {
        var b = new UiBindings();
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
        b.Text["bloodFraction"] = (lead?.Body?.Blood ?? 0).ToString(CultureInfo.InvariantCulture);
        b.Text["bodyFraction"] = (lead?.Body?.WorstPart ?? 0).ToString(CultureInfo.InvariantCulture);
        b.Text["hungerFraction"] = ((lead?.Body?.Hunger ?? 0) / 3).ToString(CultureInfo.InvariantCulture);
        var roster = session.CurrentSnapshot.Characters.Where(c => c.IsPlayer).ToArray();
        b.Text["squad.count"] = roster.Length.ToString(CultureInfo.InvariantCulture);
        for (int i = 0; i < Math.Min(8, roster.Length); i++) b.Text[$"squad.{i}.name"] = roster[i].Name;
        b.Text["members"] = $"{PlayerCharacters().Count} members | {Selected().Count} selected";
        b.Text["ExtendedStatsText"] = "";
        // The exe sets the money label without the layout's colon (docs/game/ui-screens.md section 4).
        b.Text["MoneyLabelText"] = "Money";
        // The bounty panel draws its own Bounty: line over the widget (PlayerInterface.HudText.cs).
        b.Properties["BountyText"] = new Dictionary<string, string> { ["SkipCaption"] = "true" };
        for (int i = 0; i < ButtonSpeeds.Length; i++) b.Actions[$"TimeSpeedButton{i + 1}"] = $"meitou:speed.{i}";
        b.Actions["ShortcutInventoryButton"] = "meitou:inventory";
        b.Actions["ShortcutStatsButton"] = "meitou:stats";
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
        SyncTradeScreens();
        SyncMachineScreens();
        SyncLootScreen();
        SyncPinnedInventories();
        SyncInventoryLayouts();
        SyncLimbsScreen();
        SyncDialogueScreen();
        SyncCharacterTradeScreen();
        ValidateInventoryDrag();
        var b = BindUi();
        faithfulScreens.FocusRing = UiMode == UiMode.Meitou;
        bool hud =!TitleOpen && !EditorOpen && !conversationShown;   // the conversation panel covers the HUD bar (engine reading of its layout)
        SyncTownPanel(hud);
        var controls = hud ? MyGuiPresentation.Arrange(faithfulHud, Width, Height, b, uiResources.Layers).ToList() : [];
        UiRect FaithfulSlot(string name) => controls.FirstOrDefault(c => c.Name == name)?.Bounds ?? default;
        var slots = new Dictionary<string, UiRect>
        {
            [UiSlots.Overlay] = new(0, 0, Width, Height),
            [UiSlots.Toolbar] = FaithfulSlot("TimeMoneyPanel"),
            [UiSlots.Selection] = FaithfulSlot("InformationPanel"),
            [UiSlots.Squad] = FaithfulSlot("SquadPanel"),
        };
        if (hud) controls.AddRange(uiHost.Arrange(UiMode, Width, Height, b, slots));
        squadBarRow = 0;   // no bar, no scrolling
        if (controls.FirstOrDefault(c => c.Name == "SquadPanel") is { } squadPanel) AddSquadPortraits(controls, squadPanel.Bounds);
        AddHudText(controls);
        AddJobRemoveButtons(controls);
        if (hud) AddTutorialPanel(controls);
        int modalFrom = -1;   // where the first modal screen (the Esc menu, save and load, message boxes) starts: the floaters go under it
        if (!TitleOpen) ReleaseTitleLogo();
        foreach (var screen in faithfulScreens.Arrange(Width, Height, BindFaithfulScreen, AddFaithfulContent))
        {
            if (ShotWithoutMenus && FeedbackShotHides.Contains(screen.Id)) continue;
            if (modalFrom < 0 && faithfulScreens.IsModal(screen.Id)) modalFrom = controls.Count;
            if (screen.Id == "editor") controls.AddRange(EditorBackdrop());
            controls.AddRange(screen.Controls.Where(c => UiMode == UiMode.Faithful ||
                !c.Properties.TryGetValue("ImageResource", out var resource) || resource != "Kenshi_TitleScreen"));
            if (screen.Id == "title" && UiMode == UiMode.Meitou && !showCredits && TitleLogoReady())
            {
                float logoWidth = Math.Min(Width * 0.3f, Height * 0.6f);
                controls.Add(new("MeitouTitle", "ImageBox", new(Width * 0.975f - logoWidth, Height * 0.04f, logoWidth, logoWidth * 0.469f), "", "", "", true,
                    new Dictionary<string, string> { ["ImageTexture"] = TitleLogoTexture }, Vector4.Zero, Pickable: false));
            }
            else if (screen.Id == "title" && UiMode == UiMode.Meitou && !showCredits)
                controls.Add(new("MeitouTitle", "TextBox", new(Width * 0.6f, Height * 0.04f, Width * 0.37f, Height * 0.14f), "KENSHI", "", "", true,
                    new Dictionary<string, string> { ["FontName"] = "Meitou_TitleFont", ["TextAlign"] = "Right VCenter" },
                    Vector4.Zero, new Vector4(0.89f, 0.84f, 0.71f, 1), TextScale: Height / 720f));
        }
        if (hud) AddTutorialHighlight(controls);
        AddNotice(controls);
        AddInventoryDrag(controls);
        var nameTags = new List<UiControl>();   // the tags are on the original's Back layer: under every panel
        AddItemLabels(nameTags);
        AddNameTags(nameTags);
        AddTaskBars(nameTags);
        controls.InsertRange(0, nameTags);
        var floaterControls = new List<UiControl>();   // the original's Dialog layer: over the panels, under the modal screens
        AddFloaters(floaterControls);
        controls.InsertRange(modalFrom < 0 ? controls.Count : modalFrom + nameTags.Count, floaterControls);
        AddSpeech(controls);
        AddBuildReadout(controls);
        AddContextMenu(controls);
        AddFeedbackButton(controls);
        pointerOverUi = faithfulScreens.Contains(mouse) || MyGuiPresentation.HitTest(controls, mouse.X, mouse.Y) is not null;
        AddHoverLabel(controls);
        return controls;
    }

    bool UiMouseDown(Vector2 p)
    {
        if (!UiVisible) return false;
        var controls = ArrangeUi();
        if (InventoryPointerDown(p)) return true;
        if (MapPointerDown(p)) return true;
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
        uiHost.Dispatch(c);
        return true;
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
        else if (action == "prospect") ProspectAction();
        else if (action.StartsWith("building-", StringComparison.Ordinal) || action.StartsWith("machine-", StringComparison.Ordinal)) BuildingAction(action);
        else if (action == "tutorial") TutorialAction(argument);
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
        if (UiMode == UiMode.Faithful && LoadBar is { Visible: true } bar) controls = [.. controls, .. bar.Controls(uiResources, width, height)];
        uiPainter?.Draw(controls, width, height, mouse, $"meitou:speed.{Array.IndexOf(ButtonSpeeds, session.Simulation.Speed)}",
            minimumTextPixels: TitleOpen ? 0 : StandardPixels, pass: pass);
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
