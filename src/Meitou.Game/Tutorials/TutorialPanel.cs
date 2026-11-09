using System.Globalization;
using System.Numerics;
using Meitou.Data.Ui;
using Meitou.Game.Ui;
using Meitou.Simulation;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Movement;

namespace Meitou.Game.Tutorials;

/// <summary>
/// The tutorial panel and window (<c>TutorialGUI</c>, docs/game/ui-screens.md "Tutorials"): a line per active tutorial stacked up from the bottom of
/// the top-left list panel (25 px x the UI scale each, the newest lowest), a new line flashing white for a second; its name opens the window on it,
/// its X dismisses it. The window shows the item's current page with PREVIOUS, SKIP / CONTINUE / FINISH and DISMISS, and runs the page's own advance
/// while it is open.
/// </summary>
sealed class TutorialPanel(PlayerInterface player)
{
    /// <summary>
    /// The list panel's share of the screen and a line's height in 1080p pixels (<c>Kenshi_TutorialPanel.layout</c>, <c>arrangeList</c>).
    /// </summary>
    const float TutorialListWidth = 0.130208f, TutorialListHeight = 0.462963f, TutorialLineHeight = 25;

    /// <summary>
    /// The advance timer of the camera, map, signs, first aid and lockpicking pages (real seconds).
    /// </summary>
    const float TutorialWait = 5;

    /// <summary>
    /// The tutorials' triggers (docs/game/ui-screens.md "Tutorials"): the management tabs of the crafting and research triggers (overview tabs 3
    /// and 2).
    /// </summary>
    internal const int CraftingTab = 3, ResearchTab = 2;

    /// <summary>
    /// The yellow highlight box's colour (<c>TutorialGUI</c>), its alpha swinging between the low and high values.
    /// </summary>
    static readonly Vector4 TutorialHighlightColour = new(0.909f, 0.913f, 0.125f, 1);

    /// <summary>
    /// The highlight alpha's low and high ends and its change per second.
    /// </summary>
    const float HighlightLow = 0.1f, HighlightHigh = 0.4f, HighlightRate = 0.4f;

    /// <summary>
    /// The host's tutorial manager; null without tutorials.
    /// </summary>
    TutorialManager? _tutorials;

    /// <summary>
    /// The tutorial texts of the active language (the pages' lines).
    /// </summary>
    internal TutorialCatalog TutorialTexts = TutorialCatalog.From([]);

    /// <summary>
    /// The tutorial panel's layout and its line's (<c>Kenshi_TutorialPanel</c>, <c>Kenshi_TutorialTooltipPanel</c>); null before the UI is read.
    /// </summary>
    internal MyGuiLayout? TutorialPanelLayout, TutorialLineLayout;

    /// <summary>
    /// Item id to how long its line still flashes, seconds (a newly started item).
    /// </summary>
    internal readonly Dictionary<int, float> TutorialFlashes = [];

    /// <summary>
    /// The item whose page the panel shows, null when none.
    /// </summary>
    internal int? TutorialShown;

    /// <summary>
    /// The <see cref="System.Diagnostics.Stopwatch"/> timestamp of the last poll (0 before the first), for its frame time.
    /// </summary>
    long _tutorialClock;

    /// <summary>
    /// The page's advance state: the 5 s timer, whether its condition was seen, and what its start remembered. The timer, seconds.
    /// </summary>
    float _tutorialTimer;

    /// <summary>
    /// The page's condition was seen; the camera was zoomed (the camera page).
    /// </summary>
    bool _tutorialSeen, _tutorialZoomed;

    /// <summary>
    /// The camera's target when the page started.
    /// </summary>
    Vector3 _tutorialCameraTarget;

    /// <summary>
    /// The camera's distance when the page started.
    /// </summary>
    float _tutorialCameraDistance;

    /// <summary>
    /// The PCs imprisoned when the lockpicking page first looked.
    /// </summary>
    readonly HashSet<CharacterId> _tutorialPrisoners = [];

    /// <summary>
    /// Each PC's last position, for the movement page.
    /// </summary>
    readonly Dictionary<CharacterId, Vector3> _tutorialLastPositions = [];

    /// <summary>
    /// The Tutorials window (F1, <c>Kenshi_TutorialsWindow</c>): every tutorial by title; a row's pages paged read-only; ACTIVATE puts it in the
    /// panel from its first page. Its selected row and page.
    /// </summary>
    int _tutorialsRow, _tutorialsPage;

    /// <summary>
    /// The tutorial highlight (<c>TutorialGUI</c>'s yellow box, <c>_setHightlightCoords</c>, docs/game/ui-screens.md "Tutorials"): the shown page's
    /// HUD part under a box of (0.909, 0.913, 0.125) whose alpha swings between 0.1 and 0.4 at 0.4 a second; a run page's box shows only while its
    /// condition holds.
    /// </summary>
    float _highlightAlpha, _highlightDirection = 1;

    /// <summary>
    /// The highlighted HUD part.
    /// </summary>
    TutorialHighlight _highlightShown;

    /// <summary>
    /// The <see cref="System.Diagnostics.Stopwatch"/> timestamp of the last highlight frame (0 before the first).
    /// </summary>
    long _highlightClock;

    /// <summary>
    /// The host's tutorial manager (null: no tutorials).
    /// </summary>
    public void ConfigureTutorials(TutorialManager? manager) => _tutorials = manager;

    /// <summary>
    /// Once a frame from the HUD: the manager's poll (new lines flash, <c>Thing_Complete</c>), the flashes fading and the shown page's advance.
    /// </summary>
    internal void PollTutorials()
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        float dt = _tutorialClock == 0 ? 0 : MathF.Min((float)System.Diagnostics.Stopwatch.GetElapsedTime(_tutorialClock, now).TotalSeconds, 0.25f);
        _tutorialClock = now;
        if (_tutorials is not { } manager || player.TitleOpen || !player.Session.World.Player.Exists) return;
        if (!manager.Enabled)
        {
            TutorialShown = null;
            TutorialFlashes.Clear();
            return;
        }

        if (manager.Update(dt, () => TutorialPoll(false)) is { Count: > 0 } started)
        {
            foreach (int id in started) TutorialFlashes[id] = 1;
            player.InterfaceSounds.Sound("Thing_Complete");
        }

        foreach (int id in TutorialFlashes.Keys.ToArray())
            if ((TutorialFlashes[id] -= dt) <= 0) TutorialFlashes.Remove(id);
        if (TutorialShown is { } shown && !manager.Active.Contains(shown)) TutorialShown = null;
        if (TutorialShown is { } open && TutorialAdvanced(open, dt)) NextTutorialPage(open);
    }

    /// <summary>
    /// The page item <paramref name="id"/> is on (clamped to its pages).
    /// </summary>
    TutorialPage PageOf(int id) =>
        TutorialCatalog.Items[id].Pages[Math.Clamp(_tutorials!.Pages[id], 0, TutorialCatalog.Items[id].Pages.Length - 1)];

    /// <summary>
    /// A page's start: its advance state cleared, and what the original's start does (the selection dropped, the follow stopped, the windows closed).
    /// </summary>
    void StartTutorialPage(int id)
    {
        (_tutorialTimer, _tutorialSeen, _tutorialZoomed) = (0, false, false);
        var view = player.Session.Camera.Current;
        (_tutorialCameraTarget, _tutorialCameraDistance) = (view.Target, view.Distance);
        _tutorialLastPositions.Clear();
        switch (PageOf(id).Advance)
        {
            case TutorialAdvance.CharSelection or TutorialAdvance.RecruitSelect: player.Select([], false); break;
            case TutorialAdvance.CenterChar: player.Follow = null; break;
            case TutorialAdvance.Map:
                foreach (string screen in player.FaithfulScreens.OpenScreens.ToArray())
                    player.FaithfulScreens.Close(screen);
                break;
            case TutorialAdvance.RecruitStats: player.FaithfulScreens.Close("stats"); break;
            case TutorialAdvance.Lockpicking: _tutorialPrisoners.Clear(); break;
        }
    }

    /// <summary>
    /// The shown page's advance test (docs/game/ui-screens.md "Tutorials", advance tests); the waits count real time while the window is open.
    /// </summary>
    bool TutorialAdvanced(int id, float dt)
    {
        var snap = player.Session.CurrentSnapshot;
        var lead = player.Selected().FirstOrDefault();
        bool Wait() { _tutorialTimer += dt; return _tutorialTimer >= TutorialWait; }
        bool Tab(int tab) => player.FaithfulScreens.IsOpen("overview") && player.FaithfulScreens.GetTab("overview", "TabsMain") == tab;
        switch (PageOf(id).Advance)
        {
            case TutorialAdvance.CameraControls:
                {
                    var view = player.Session.Camera.Current;
                    _tutorialSeen |= view.Target.X != _tutorialCameraTarget.X || view.Target.Z != _tutorialCameraTarget.Z;
                    _tutorialZoomed |= view.Distance != _tutorialCameraDistance;
                    return _tutorialSeen && _tutorialZoomed && Wait();
                }
            case TutorialAdvance.CharSelection or TutorialAdvance.SelectCharacter: return lead is { IsPlayer: true };
            case TutorialAdvance.CharMovement:
                {
                    if (lead is null) return false;
                    bool moved = _tutorialLastPositions.TryGetValue(lead.Id, out var before) && before != lead.Position;
                    _tutorialLastPositions[lead.Id] = lead.Position;
                    return moved;
                }
            case TutorialAdvance.CenterChar: return player.Follow is not null;
            case TutorialAdvance.Map:
                _tutorialSeen |= Tab(0);
                return _tutorialSeen && Wait();
            case TutorialAdvance.Signs:
                _tutorialSeen |= player.AltHeld;
                return _tutorialSeen && Wait();
            case TutorialAdvance.Medkiting:
                _tutorialSeen |= snap.Characters.Any(c => c.IsPlayer && c.Task == CharacterTask.Treat);
                return _tutorialSeen && Wait();
            case TutorialAdvance.RecruitSelect: return _tutorials!.Recruit is not { } recruit || lead?.Id == recruit;
            case TutorialAdvance.RecruitStats: return player.FaithfulScreens.IsOpen("stats");
            case TutorialAdvance.SquadArrange: return Tab(4);
            case TutorialAdvance.SelectUnconscious: return lead is { IsPlayer: true, Body: { Unconscious: true, PlayingDead: false } };
            case TutorialAdvance.Lockpicking:
                if (!_tutorialSeen && snap.Characters.Any(c => c.IsPlayer && c.PickingLock))
                {
                    _tutorialSeen = true;
                    if (_tutorialPrisoners.Count == 0)
                        foreach (var c in snap.Characters)
                            if (c.IsPlayer && c.Imprisoned) _tutorialPrisoners.Add(c.Id);
                }
                return _tutorialSeen && Wait();
            // A prisoner of the set still in their cage, its lock open.
            case TutorialAdvance.LockpickingSuccessful: return snap.Characters.Any(c => _tutorialPrisoners.Contains(c.Id) && c.IsPlayer && c.CageOpen);
            case TutorialAdvance.CraftButton: return Tab(2) || Tab(3);
            case TutorialAdvance.CraftTab: return Tab(3);
            case TutorialAdvance.ProductionSelection:
                return player.BuildingSelection.BuildingFocus() is { SiteId: { } site }
                    && snap.Buildings.FirstOrDefault(b => b.Id == site) is { Complete: true }
                    && player.Production.Machine(site) is { Output: not null };
            case TutorialAdvance.ConstructionMaterials:
                return player.BuildingSelection.BuildingFocus() is { SiteId: { } built }
                    && snap.Buildings.FirstOrDefault(b => b.Id == built) is { Complete: true };
            default: return false;
        }
    }

    /// <summary>
    /// The next page of item <paramref name="id"/>, or its dismissal after the last.
    /// </summary>
    void NextTutorialPage(int id)
    {
        if (_tutorials!.Pages[id] >= TutorialCatalog.Items[id].Pages.Length - 1)
        {
            DismissTutorial(id);
            return;
        }

        _tutorials.Pages[id]++;
        StartTutorialPage(id);
    }

    /// <summary>
    /// Dismisses item <paramref name="id"/>: it ends and leaves the panel.
    /// </summary>
    void DismissTutorial(int id)
    {
        if (TutorialShown == id) TutorialShown = null;
        TutorialFlashes.Remove(id);
        _tutorials?.Dismiss(id);
    }

    /// <summary>
    /// The panel's buttons: <c>open:id</c> and <c>close:id</c> on a line, <c>prev</c> / <c>next</c> / <c>dismiss</c> / <c>hide</c> in the window.
    /// </summary>
    internal void TutorialAction(string argument)
    {
        if (_tutorials is null) return;

        player.InterfaceSounds.Sound("General_Click");
        int colon = argument.IndexOf(':');
        int id = colon > 0 && int.TryParse(argument.AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int n)
            && n < TutorialManager.Names.Length ? n : -1;
        if (argument.StartsWith("open:", StringComparison.Ordinal) && id >= 0)
        {
            TutorialShown = id;
            StartTutorialPage(id);
        }
        else if (argument.StartsWith("close:", StringComparison.Ordinal) && id >= 0) DismissTutorial(id);
        else if (TutorialShown is not { } shown) return;
        else if (argument == "next") NextTutorialPage(shown);
        else if (argument == "prev" && _tutorials.Pages[shown] > 0)
        {
            _tutorials.Pages[shown]--;
            StartTutorialPage(shown);
        }
        else if (argument == "dismiss") DismissTutorial(shown);
        else if (argument == "hide") TutorialShown = null;
    }

    /// <summary>
    /// The list's lines and, when one is open, the window, over the HUD.
    /// </summary>
    internal void AddTutorialPanel(List<UiControl> controls)
    {
        if (_tutorials is not { Enabled: true } manager || TutorialLineLayout is null || TutorialPanelLayout is null
            || !player.Session.World.Player.Exists || player.CharacterEditor.EditorOpen)
            return;

        float scale = player.Height / 1080f, lineHeight = MathF.Round(TutorialLineHeight * scale);
        float width = MathF.Round(player.Width * TutorialListWidth);
        float y = player.Height * TutorialListHeight;
        var active = manager.Active;
        for (int k = active.Count - 1; k >= 0; k--)
        {
            int id = active[k];
            y -= lineHeight;
            var b = new UiBindings { TemplateScale = scale };
            // The layout's root is 250 x 25 in pixels; the list sizes it to the panel's width and the scaled line height (arrangeList).
            // Its children keep their pixel margins: NameButton stretches, CloseButton (Right VStretch) stays 27 px at the right end.
            if (TutorialLineLayout.Roots.FirstOrDefault() is { } root) b.Positions[root.Name] = new(0, 0, width, lineHeight);
            b.Positions["NameButton"] = new(0, 0, width - 25, lineHeight);
            b.Positions["CloseButton"] = new(width - 27, 0, 27, lineHeight);
            b.Text["NameButton"] = TutorialTexts.Title(id);
            string prefix = $"tutorial-{id}-";
            foreach (var c in MyGuiPresentation.Arrange(TutorialLineLayout, (int)width, (int)lineHeight, b, player.UiResources.Layers))
            {
                // Arranged at the top of the screen: the clip moves down with the bounds, else it hides the whole line.
                var control = c with
                {
                    Name = c.Name.Length > 0 ? prefix + c.Name : c.Name,
                    Bounds = c.Bounds with { Y = c.Bounds.Y + y },
                    Clip = c.Clip is { } clip ? clip with { Y = clip.Y + y } : null,
                };
                if (c.Name == "NameButton") control = control with { Action = "meitou:tutorial", Argument = $"open:{id}" };
                else if (c.Name == "CloseButton") control = control with { Action = "meitou:tutorial", Argument = $"close:{id}" };
                controls.Add(control);
            }

            if (TutorialFlashes.TryGetValue(id, out float alpha))
            {
                var flashBounds = new UiRect(0, y, width, lineHeight);
                controls.Add(new(prefix + "flash", "Widget", flashBounds, "", "", "", true, new Dictionary<string, string>(),
                    new Vector4(1, 1, 1, alpha), Pickable: false));
            }
        }

        if (TutorialShown is not { } shown) return;
        var (title, pages) = TutorialCatalog.Items[shown];
        int page = Math.Clamp(manager.Pages[shown], 0, pages.Length - 1);
        bool last = page == pages.Length - 1;
        var w = new UiBindings { TextHeight = c => player.UiPainter?.TextHeight(c, player.Height, player.StandardPixels) ?? 0 };
        w.Hidden.Add("TooltipsPanel");
        w.Text["TutorialWindow"] = TutorialTexts.TextAt(title);
        w.Text["TutorialDescription"] = TutorialTexts.TextAt(pages[page].Line);
        w.Text["PagingText"] = $"{page + 1} / {pages.Length}";
        w.Text["NextTutorialButton"] = last ? "FINISH" : pages[page].Continue ? "CONTINUE" : "SKIP";
        w.Text["DismissButton"] = "DISMISS";
        if (last) w.Hidden.Add("DismissButton");
        if (page == 0) w.Disabled.Add("PrevTutorialButton");
        foreach (var c in MyGuiPresentation.Arrange(TutorialPanelLayout, player.Width, player.Height, w, player.UiResources.Layers))
        {
            string? argument = c.Name switch
            {
                "NextTutorialButton" => "next",
                "PrevTutorialButton" => "prev",
                "DismissButton" => "dismiss",
                _ => null,
            };
            var control = c with { Name = c.Name.Length > 0 ? "tutorial-window-" + c.Name : c.Name };
            if (argument is not null) control = control with { Action = "meitou:tutorial", Argument = argument };
            else if (c.Action.StartsWith("mygui:close", StringComparison.Ordinal))
                control = control with { Action = "meitou:tutorial", Argument = "hide" };
            else if (c.Name == "Root") control = control with { Pickable = false };
            controls.Add(control);
        }
    }

    /// <summary>
    /// The Tutorials window's bindings: the titles, the selected row's page with its paging, ACTIVATE and the page buttons.
    /// </summary>
    internal void BindTutorialsWindow(UiBindings b)
    {
        b.Text["Root"] = "TUTORIALS";
        b.Lists["TutorialList"] =
        [
            .. TutorialCatalog.Items.Select((item, i) => new UiListItem(i.ToString(CultureInfo.InvariantCulture), TutorialTexts.TextAt(item.Title))),
        ];
        b.SelectedItems["TutorialList"] = _tutorialsRow;
        var pages = TutorialCatalog.Items[_tutorialsRow].Pages;
        _tutorialsPage = Math.Clamp(_tutorialsPage, 0, pages.Length - 1);
        b.Text["TutorialDescription"] = TutorialTexts.TextAt(pages[_tutorialsPage].Line);
        b.Text["PagingText"] = $"{_tutorialsPage + 1} / {pages.Length}";
        b.Text["ActivateButton"] = "ACTIVATE";
        b.Disabled.Remove("ActivateButton");
        if (_tutorialsPage > 0) b.Disabled.Remove("PrevTutorialButton");
        if (_tutorialsPage < pages.Length - 1) b.Disabled.Remove("NextTutorialButton");
    }

    /// <summary>
    /// A Tutorials window event: a row picked, the previous or next page, or ACTIVATE (the item goes into the panel at its first page).
    /// </summary>
    internal void TutorialsWindowAction(MyGuiEvent e)
    {
        if (e.Widget == "TutorialList" && e.Action == "select"
            && int.TryParse(e.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int row) && row < TutorialCatalog.Items.Length)
        {
            player.InterfaceSounds.Sound("General_Click");
            (_tutorialsRow, _tutorialsPage) = (row, 0);
        }
        else if (e.Widget == "PrevTutorialButton")
        {
            player.InterfaceSounds.Sound("General_Click");
            _tutorialsPage--;
        }
        else if (e.Widget == "NextTutorialButton")
        {
            player.InterfaceSounds.Sound("General_Click");
            _tutorialsPage++;
        }
        else if (e.Widget == "ActivateButton")
        {
            player.FaithfulScreens.Close("tutorials");
            if (_tutorials?.Activate(_tutorialsRow) == true)
            {
                TutorialFlashes[_tutorialsRow] = 1;
                player.InterfaceSounds.Sound("Thing_Complete");
            }
        }
    }

    /// <summary>
    /// The highlight the shown page wants now, or none.
    /// </summary>
    TutorialHighlight WantedHighlight()
    {
        if (_tutorials is not { Enabled: true } || TutorialShown is not { } id) return TutorialHighlight.None;

        var page = PageOf(id);
        if (!page.Run) return page.Highlight;

        var snap = player.Session.CurrentSnapshot;
        switch (page.Highlight)
        {
            case TutorialHighlight.BuildModePanelBuildings:
                return player.Build.BuildModeOn && player.Build.CurrentBuilding() is not null ? page.Highlight : TutorialHighlight.None;
            case TutorialHighlight.PanelMedicalBarMaterials or TutorialHighlight.PanelMedicalBarCondition:
                {
                    if (player.BuildingSelection.BuildingFocus() is not { } focus
                        || snap.Buildings.FirstOrDefault(b => b.Id == focus.SiteId) is not { Complete: false })
                        return TutorialHighlight.None;

                    bool door = player.BuildingSelection.FirstDoor(focus) is not null;
                    return page.Highlight == TutorialHighlight.PanelMedicalBarMaterials
                        ? door ? TutorialHighlight.PanelMedicalBarMaterialsBuilding : TutorialHighlight.PanelMedicalBarMaterials
                        : door ? TutorialHighlight.PanelMedicalBarConditionBuilding : TutorialHighlight.PanelMedicalBarCondition;
                }
            default:
                // The production building pages: the focus is a complete production building.
                return player.BuildingSelection.BuildingFocus() is { SiteId: { } site }
                    && snap.Buildings.FirstOrDefault(b => b.Id == site) is { Complete: true }
                    && player.Production.Machine(site) is { Output: not null }
                    ? page.Highlight : TutorialHighlight.None;
        }
    }

    /// <summary>
    /// Where a highlight goes among this frame's controls (null: its part is not shown, so neither is the box).
    /// </summary>
    static UiRect? HighlightBounds(TutorialHighlight h, IReadOnlyList<UiControl> controls, int obedienceJob)
    {
        UiRect? Named(string name) => controls.LastOrDefault(c => c.Name == name && c.Bounds.Width > 0)?.Bounds;
        UiRect? Where(Func<UiControl, bool> test) => controls.LastOrDefault(c => test(c) && c.Bounds.Width > 0)?.Bounds;

        // The medical panel's bars by index (FUN_1407229c0): the n-th bar is LifeBar{n + 1}.
        UiRect? Bar(int index) => Named($"LifeBar{index + 1}");
        UiRect? Line(string caption) => Named("StatsPanel") is { } panel
            ? Where(c => c.Caption.TrimEnd(':') == caption && panel.Contains(c.Bounds.X + 1, c.Bounds.Y + 1)) : null;
        return h switch
        {
            TutorialHighlight.Portraits => Named("SquadPanel"),
            TutorialHighlight.PanelStats => Named("NamePanel"),
            TutorialHighlight.PanelStatsBottom => Named("StatsPanel"),
            TutorialHighlight.PanelStatsBottomPowerButton => Line("Power"),
            TutorialHighlight.PanelStatsBottomWorkers => Line("Workers"),
            TutorialHighlight.PanelMedical => Named("MedicalPanel"),
            TutorialHighlight.PanelMedicalStatus => Named("StatusPanel"),
            TutorialHighlight.PanelMedicalBarBody or TutorialHighlight.PanelMedicalBarPower or TutorialHighlight.PanelMedicalBarMaterials
                or TutorialHighlight.PanelMedicalBarConditionBuilding => Bar(1),
            TutorialHighlight.PanelMedicalBarLimbs => Bar(4),
            TutorialHighlight.PanelMedicalBarBlood or TutorialHighlight.PanelMedicalBarCondition => Bar(0),
            TutorialHighlight.PanelMedicalBarMaterialsBuilding => Bar(2),
            TutorialHighlight.PanelGameSpeed => Named("SpeedButtonsPanel"),
            TutorialHighlight.PanelTimeMoney => Named("TimeMoneyPanel"),
            TutorialHighlight.ButtonBuild => Named("BuildPanel"),
            TutorialHighlight.PanelBounty => Named("BountyPanel"),
            TutorialHighlight.PanelMenuBar => Named("ShortcutsPanel"),
            TutorialHighlight.PanelMenuBarSquad => Named("ShortcutSquadButton"),
            TutorialHighlight.PanelMenuBarStats => Named("ShortcutStatsButton"),
            TutorialHighlight.PanelMenuBarTech => Named("ShortcutTechButton"),
            TutorialHighlight.PanelOrders => Named("OrdersPanel"),
            TutorialHighlight.PanelOrdersCommands => Union(
            [
                "OrdersBlockButton", "OrdersHoldButton", "OrdersPassiveButton", "OrdersChaseButton", "OrdersTauntButton", "OrdersRangedButton",
                "OrdersSneakButton",
            ]),
            TutorialHighlight.PanelOrdersJobs => Named("JobsPanel"),
            TutorialHighlight.PanelOrdersJobRemove => controls.FirstOrDefault(c => c.Action == "meitou:job-remove")?.Bounds,
            TutorialHighlight.PanelOrdersMedicButton => Named("OrdersMedicButton"),
            TutorialHighlight.PanelOrdersRescueButton => Named("OrdersRescueButton"),
            TutorialHighlight.MapscreenCraftTab => Where(c => c.Action == "mygui:tab:TabsMain:3"),
            TutorialHighlight.MapscreenCraftTabItems => Named("CraftTab"),
            TutorialHighlight.BuildingInventoryInput => Named("InputColumn"),
            TutorialHighlight.BuildModePanelBuildings => Named("BuildingsList"),
            // The first Obedient Slave job's whole row (FUN_140972a50 case 0x1c); none: no box.
            TutorialHighlight.PanelOrdersJobObedience => obedienceJob < 0 ? null
                : Where(c => c.Properties.GetValueOrDefault("ListOwner") == "JobsPanel"
                    && c.Argument == "job:" + obedienceJob.ToString(CultureInfo.InvariantCulture)),
            _ => null,
        };

        UiRect? Union(string[] names)
        {
            UiRect? all = null;
            foreach (string name in names)
            {
                if (Named(name) is not { } r) continue;
                if (all is not { } a)
                {
                    all = r;
                    continue;
                }

                float x = MathF.Min(a.X, r.X), y = MathF.Min(a.Y, r.Y);
                all = new UiRect(x, y, MathF.Max(a.X + a.Width, r.X + r.Width) - x, MathF.Max(a.Y + a.Height, r.Y + r.Height) - y);
            }

            return all;
        }
    }

    /// <summary>
    /// Once a frame, over everything else: the box over the wanted part, its alpha swinging.
    /// </summary>
    internal void AddTutorialHighlight(List<UiControl> controls)
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        float dt = _highlightClock == 0 ? 0 : MathF.Min((float)System.Diagnostics.Stopwatch.GetElapsedTime(_highlightClock, now).TotalSeconds, 0.25f);
        _highlightClock = now;
        var wanted = WantedHighlight();

        // startHighlight: a new item starts at alpha 0, rising; endHighlight resets.
        if (wanted != _highlightShown) (_highlightShown, _highlightAlpha, _highlightDirection) = (wanted, 0, 1);
        if (wanted == TutorialHighlight.None || HighlightBounds(wanted, controls, player.OrdersPanel.ObedienceJob) is not { } box) return;

        _highlightAlpha += _highlightDirection * dt * HighlightRate;
        if (_highlightAlpha >= HighlightHigh) (_highlightAlpha, _highlightDirection) = (HighlightHigh, -1);
        else if (_highlightAlpha <= HighlightLow && _highlightDirection < 0) (_highlightAlpha, _highlightDirection) = (HighlightLow, 1);
        var colour = TutorialHighlightColour with { W = _highlightAlpha };
        controls.Add(new("tutorial-highlight", "Widget", box, "", "", "", true, new Dictionary<string, string>(), colour, Pickable: false));
    }

    /// <summary>
    /// The player's characters, buildings, selection and windows as the triggers ask for them.
    /// </summary>
    internal TutorialPoll TutorialPoll(bool loading)
    {
        var snap = player.Session.CurrentSnapshot;
        int faction = player.Session.World.Player.Faction;
        bool unsettled = false, aid = false, attack = false, ko = false, awake = false;
        bool sneak = false, hungry = false, playingDead = false, slave = false;
        int pcs = 0;
        var pcIds = new List<CharacterId>();
        foreach (var c in snap.Characters)
        {
            if (!c.IsPlayer || c.Body is { Dead: true }) continue;
            pcs++;
            pcIds.Add(c.Id);
            bool down = c.Body is { Unconscious: true };
            unsettled |= c.CombatMode || down || c.InBed;
            aid |= c.Body is { NeedsAid: true };
            attack |= c.InCombat;
            ko |= down && c.Body is not { PlayingDead: true };
            awake |= !down;
            sneak |= c.Stealth is not null;
            hungry |= c.Body is { Hunger: < 2 } && c.Hud is not { Robot: true };
            playingDead |= c.Body is { PlayingDead: true };
            slave |= c.Slave is not null;
        }
        var inspected = snap.Characters.FirstOrDefault(c => c.Inspected);
        bool production = false, full = false, built = false, crafting = false, research = false, unstarted = false;
        var sites = snap.Buildings.Where(b => b.Owner == faction && faction >= 0).ToDictionary(b => b.Id, StringComparer.Ordinal);
        foreach (var m in snap.Machines)
        {
            if (!sites.TryGetValue(m.Site, out var site) || m.Output is null && m.Farm is null && m.Craft is null) continue;
            production = true;
            full |= m.Status == 1 || m.Farm is { Status: Simulation.Production.FarmStatus.OutputFull };
            built |= site.Complete && m.Output is not null;
            crafting |= site.Complete && m.Craft is not null;
        }

        foreach (var m in snap.Machines)
            if (m.TechLevel > 0 && sites.TryGetValue(m.Site, out var bench) && bench.Complete) research = true;
        foreach (var site in sites.Values) unstarted |= !site.Complete && site.Progress == 0;
        var facts = snap.TutorialFacts;
        return new TutorialPoll
        {
            EditorOrLoading = player.CharacterEditor.EditorOpen || loading,
            Unsettled = unsettled || facts.InPrison || facts.InCannibalCage,
            InTown = facts.InTown,
            NeedsFirstAid = aid,
            UnderAttack = attack,
            PcCount = pcs,
            Pcs = pcIds,
            Stolen = facts.Stolen,
            AnyUnconscious = ko,
            AnyConscious = awake,
            InPrison = facts.InPrison,
            InCannibalCage = facts.InCannibalCage,
            Sneaking = sneak,
            // The steal pointer (cursor 0x15): a selection over another faction's ground item.
            StealCursor = player.SelectedIds().Count > 0 && !player.PointerPanel.PointerOverUi
                && player.GroundItemPicks.StealsGroundItem(player.Mouse),
            SelectedHasBounty = inspected is { IsPlayer: false, Bounties.Count: > 0 },
            Hungry = hungry,
            PlayingDead = playingDead,
            HasProductionBuilding = production,
            Slave = slave,
            Uniform = facts.Uniform,
            MachineFull = full,
            SelectedForSale = snap.InspectedBuilding?.SalePrice is not null,
            ProductionComplete = built,
            Placing = player.Build.BuildModeOn && player.Build.CurrentBuilding() is not null,
            CraftingComplete = crafting,
            ResearchComplete = research,
            ManagementTab = player.FaithfulScreens.IsOpen("overview") ? player.FaithfulScreens.GetTab("overview", "TabsMain") : -1,
            Blueprint = facts.Blueprint,
            UnstartedConstruction = unstarted,
            Prospecting = player.FaithfulScreens.IsOpen("prospect"),
            SelectedStump = (player.Selected().FirstOrDefault() ?? inspected) is { } who
                && Enumerable.Range(0, 4).Any(k => ((who.LimbBits >> (2 * k)) & 3) == (int)LimbState.Stump),
        };
    }
}