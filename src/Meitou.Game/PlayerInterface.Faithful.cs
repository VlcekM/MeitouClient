using System.Globalization;
using System.Numerics;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Data.Ui;
using Meitou.Engine.Input;
using Meitou.Simulation;
using Meitou.Ui;

namespace Meitou.Game;

sealed partial class PlayerInterface
{
    string selectedStat = ((int)StatsEnumerated.Katanas).ToString(CultureInfo.InvariantCulture);
    public bool UiCapturesPointer => UiVisible && (faithfulScreens.CapturesPointer || mapDrag is not null || liftPress is not null || scrollDrag is not null);
    /// <summary>A modal screen (the Esc menu, a message box) is up: the pointer is the interface's, even in the free camera.</summary>
    public bool ModalOpen => UiVisible && faithfulScreens.HasModal;
    /// <summary>The faithful screens open, bottom first (the play benchmark says which modal one stopped the game).</summary>
    public IReadOnlyList<string> OpenScreens => faithfulScreens.OpenScreens;
    /// <summary>Keys the host keeps for itself (the sandbox's letters), so the faithful window shortcuts leave them alone.</summary>
    public HashSet<Key> ReservedKeys { get; } = [];
    void UpdateMenuPause() => session.Simulation.RequestedPause = settingsOpen || faithfulScreens.HasModal;
    public bool UiKey(Key key, bool ctrl = false, bool shift = false)
    {
        if (!UiVisible) return false;
        if (key == Meitou.Engine.Input.Key.Escape && CancelInventoryDrag()) return true;
        if (key == Meitou.Engine.Input.Key.Escape)
        {
            if (advancedOpen && faithfulScreens.OpenScreens.LastOrDefault() == "new-game") { advancedOpen = false; return true; }
            // The bare title: Esc hides the credits and quits at once, no confirmation (FUN_14082b010 sets the quit flag unless FUN_140914a90 closed a title
            // window; hiding the credits does not count as closing one). With a title window open the generic Esc below closes it.
            if (faithfulScreens.IsOpen("title") && faithfulScreens.OpenScreens.Count == 1)
            {
                showCredits = false;
                ScreenAction?.Invoke(new("menu", "ExitGameButton", "exit"));
                return true;
            }
            // The editor stays under the game menu (the original's Esc in the editor; it has no cancel: CONFIRM closes it).
            if (EditorOpen && faithfulScreens.OpenScreens[^1] == "editor") { faithfulScreens.Open("menu"); UpdateMenuPause(); return true; }
            // Esc presses the last button of the top message box (Return the first, docs/game/ui-input.md section 5).
            if (faithfulScreens.IsOpen("message")) HandleFrontendAction(new("message", CancelButton, CancelButton));
            // While looting, one Esc closes the looter's and the body's windows together (their bags follow); the loot closes with its window.
            else if (faithfulScreens.IsOpen("loot") && faithfulScreens.OpenScreens[^1] is "inventory" or "bag" or "loot" or "loot-bag") { faithfulScreens.Close("loot"); faithfulScreens.Close("inventory"); }
            // So does trading: one Esc closes the trader with the customer's windows, whichever of them was clicked last (closing the trader ends the trade).
            else if (faithfulScreens.IsOpen("trader") && faithfulScreens.OpenScreens[^1] is "inventory" or "bag" or "limbs" or "trader") { faithfulScreens.Close("trader"); faithfulScreens.Close("inventory"); }
            else if (!faithfulScreens.Escape()) faithfulScreens.Open("menu");
            UpdateMenuPause();
            return true;
        }
        if (faithfulScreens.HasKeyboardFocus && EditFocusKey(key, ctrl, shift)) return true;
        // Delete or Backspace deletes the save selected in the load window (LoadSaveWindow::keyPressed, docs/game/ui-screens.md "Deleting a save").
        if (key is Meitou.Engine.Input.Key.Delete or Meitou.Engine.Input.Key.Backspace && faithfulScreens.OpenScreens is [.., "load"] && selectedSlot is { } doomed) { AskDeleteSave(doomed); return true; }
        if (key == Meitou.Engine.Input.Key.Tab && faithfulScreens.FocusNext(shift)) return true;
        if (key == Meitou.Engine.Input.Key.Enter && faithfulScreens.Submit()) return true;
        if (key == Meitou.Engine.Input.Key.Space && faithfulScreens.ActivateFocused()) return true;
        // The list or combo box last clicked takes the MyGUI ListBox keys (docs/formats/mygui.md "Widget runtime").
        if (key switch
            {
                Meitou.Engine.Input.Key.Up => UiListKey.Up, Meitou.Engine.Input.Key.Down => UiListKey.Down, Meitou.Engine.Input.Key.PageUp => UiListKey.PageUp,
                Meitou.Engine.Input.Key.PageDown => UiListKey.PageDown, Meitou.Engine.Input.Key.Home => UiListKey.Home, Meitou.Engine.Input.Key.End => UiListKey.End,
                Meitou.Engine.Input.Key.Enter => UiListKey.Accept, _ => (UiListKey?)null,
            } is { } listKey && faithfulScreens.ListKey(listKey)) return true;
        if (DialogueKey(key)) return true;
        // A modal screen (the menu, a message box, the character editor) takes the keys, except the host's own: quicksave / quickload, the screenshot and the debug overlays.
        if (faithfulScreens.HasModal)
            return !(Bound(InputAction.QuickSave, key) || Bound(InputAction.QuickLoad, key) || Bound(InputAction.Screenshot, key) ||
                Bound(InputAction.ToggleStats, key) || Bound(InputAction.ToggleFps, key) || Bound(InputAction.CycleProfiler, key));
        // change_squad (Tab by default): the squad bar shows the next squad with members (docs/game/ui-input.md section 6).
        if (Bound(InputAction.ChangeSquad, key) && !TitleOpen && !EditorOpen) { Say(new ActiveSquadCommand(-1) { Tick = session.World.Tick }); return true; }
        if (ReservedKeys.Contains(key)) return false;
        if (Bound(InputAction.ToggleInventory, key) || Bound(InputAction.ToggleCharacterStats, key))
        { ExecuteUiAction(Bound(InputAction.ToggleInventory, key) ? "inventory" : "stats", ""); return true; }
        // toggle_help (F1, event 7): the Tutorials window (docs/game/ui-input.md section 6).
        if (Bound(InputAction.ToggleHelp, key) && !TitleOpen && !EditorOpen)
        {
            if (faithfulScreens.IsOpen("tutorials")) faithfulScreens.Close("tutorials"); else faithfulScreens.Open("tutorials");
            return true;
        }
        // toggle_map / toggle_faction / toggle_research / toggle_crafting (M / unbound / T / Y) ask for overview tabs 0 / 1 / 2 / 3 (docs/game/ui-input.md section 6).
        // Pressing the key of the tab already showing closes the window (engine choice: the original's toggle rule is not traced).
        int? tab = Bound(InputAction.ToggleMap, key) ? 0 : Bound(InputAction.ToggleFaction, key) ? 1 : Bound(InputAction.ToggleResearch, key) ? 2 : Bound(InputAction.ToggleCrafting, key) ? 3 : null;
        if (tab is { } t)
        {
            if (faithfulScreens.IsOpen("overview") && faithfulScreens.GetTab("overview", "TabsMain") == t) faithfulScreens.Close("overview");
            else { faithfulScreens.Open("overview"); faithfulScreens.SetTab("overview", "TabsMain", t); }
            return true;
        }
        return false;
    }
    public void UiText(char value) { if (UiVisible) faithfulScreens.TextInput(value); }
    public bool UiScroll(Vector2 p, float steps)
    {
        if (!UiVisible) return false;
        ArrangeUi();
        if (InventoryWheel(p, steps)) return true;
        if (MapWheel(p, steps)) return true;
        if (!faithfulScreens.Contains(p) && SquadBarWheel(p, steps)) return true;
        if (SquadsWheel(p, steps)) return true;
        if (FactionWheel(p, steps)) return true;
        if (ResearchWheel(p, steps)) return true;
        return EditorWheel(p, steps) || faithfulScreens.Wheel(p, steps);
    }
    void FaithfulAction(MyGuiEvent e)
    {
        SoundForScreen(e);
        if (e.Action.StartsWith("inventory-scroll:", StringComparison.Ordinal))
        { ScrollInventory(e.Screen, float.Parse(e.Action[17..], CultureInfo.InvariantCulture)); return; }
        if (e.Action.StartsWith("inventory-scroll-page:", StringComparison.Ordinal))
        { ScrollInventoryPage(e.Screen, float.Parse(e.Action[22..], CultureInfo.InvariantCulture)); return; }
        if (e.Screen == "editor") EditorAction(e);
        else if (e.Screen == "file") FileDialogAction(e);
        else if (e.Screen == FeedbackScreen) FeedbackFormAction(e);
        else if (HandleFrontendAction(e)) UpdateMenuPause();
        else if (e.Screen == "stats" && e.Action == "select") selectedStat = e.Text;
        else if (e.Screen == "stats" && e.Action.StartsWith("stat:", StringComparison.Ordinal)) selectedStat = e.Action[5..];
        else if (e.Screen == "build") BuildAction(e);
        else if (e.Screen == "dialogue") DialogueAction(e);
        else if (e.Screen == CharacterTradeScreen) CharacterTradeAction(e);
        else if (e.Screen == "prospect") ProspectScreenAction(e);
        else if (e.Screen == "tutorials") TutorialsWindowAction(e);
        else if (e.Screen == TownScreen) TownAction(e);
        else if (e.Screen == "overview") { MapAction(e.Widget); ResearchAction(e); CraftAction(e); AiAction(e); SquadsAction(e); FactionAction(e); }
        else if (e.Screen is "building" or "bench" or "storage") MachineAction(e);
        else if (e.Screen == "loot" && e.Widget == "OpenBagButton") lootBagOpen = !lootBagOpen;
        else if (e.Screen == "loot" && e.Widget == "LimbsButton") ToggleLimbs(LootBody());
        else if (e.Screen == "trader" && e.Widget == "ArrangeButton") Say(new Meitou.Simulation.Trading.ArrangeStock { Tick = session.World.Tick });
        else if (e.Screen is "inventory" or "bag" || IsPinnedInventory(e.Screen))
        {
            if (e.Widget == "OpenBagButton") ToggleBag();
            else if (e.Widget == "LimbsButton") ToggleLimbs();
            else if (e.Widget == "ArrangeButton" && WindowCharacter(e.Screen) is { } lead) Say(new Meitou.Simulation.Items.ArrangeInventory(lead.Id) { Tick = session.World.Tick });
        }
    }
    UiBindings BindFaithfulScreen(string id)
    {
        var b = new UiBindings { TextHeight = c => uiPainter?.TextHeight(c, Height, StandardPixels) ?? 0 };
        var layout = faithfulScreens.Layout(id);
        string screen = id;
        bool pinned = IsPinnedInventory(id);
        if (pinned) id = "inventory";   // a pinned window binds as the inventory, for its own character
        var lead = pinned || id == "limbs" ? WindowCharacter(screen) : id is "inventory" or "bag" ? InventoryOwner() : Selected().FirstOrDefault();
        foreach (var button in layout.Widgets.Where(w => w.Type == "Button" && !w.Properties.ContainsKey("TemplateEvent"))) b.Disabled.Add(button.Name);
        if (id == FeedbackScreen) { BindFeedbackScreen(b); return b; }
        if (BindFrontend(id, b)) return b;
        if (id == "editor") { BindEditor(b); return b; }
        if (id == "file") { BindFileDialog(b); return b; }
        else if (id == "trader") BindTraderScreen(b);
        else if (id == "dialogue") BindDialogueScreen(b);
        else if (id == CharacterTradeScreen) BindCharacterTradeScreen(b);
        else if (id == "prospect") BindProspectScreen(b);
        else if (id == "tutorials") BindTutorialsWindow(b);
        else if (id == TownScreen) BindTownScreen(b);
        else if (id == "build") BindBuildScreen(b);
        else if (id is "building" or "bench" or "storage") BindMachineScreen(id, b);
        else if (IsLootScreen(id)) BindLootScreen(id, b);
        else if (id is "inventory" or "stats" or "bag" or "limbs")
        {
            b.Text["Root"] = id == "stats" ? lead?.Name.ToUpperInvariant() ?? "CHARACTER STATS"
                : id == "bag" ? lead?.ItemGrid?.Items.FirstOrDefault(i => i.Section == "backpack_attach")?.Name.ToUpperInvariant() ?? "BACKPACK"
                : id == "limbs" ? "LIMBS" : lead?.Name.ToUpperInvariant() ?? "INVENTORY";
            if (id == "limbs") { b.Text["lbLeftArm"] = "-ARMS-"; b.Text["lbLeftLeg"] = "-LEGS-"; }
            if (id is "inventory" or "bag" or "limbs")
            {
                // Each window binds only what its own layout has: the character window the data panel and the limbs button, the generic (bag) window
                // the capacity box; the limb window has neither.
                if (id == "inventory")
                {
                    b.Properties["datapanel"] = new Dictionary<string, string> { ["SkipCaption"] = "true" };
                    if (!pinned && lead?.ItemGrid is { BagWidth: > 0, BagHeight: > 0 }) { b.Disabled.Remove("OpenBagButton"); b.Actions["OpenBagButton"] = "OpenBagButton"; }
                }
                if (id == "bag")
                {
                    b.Hidden.Add("CapacityText"); b.Hidden.Add("TypeText");
                }
                if (id != "limbs")
                    foreach (string name in id == "inventory" ? new[] { "ArrangeButton", "LimbsButton" } : ["ArrangeButton"])
                    { b.Disabled.Remove(name); b.Actions[name] = name; if (lead is null || name == "LimbsButton" && (pinned || !HasLimbWindow(lead))) b.Disabled.Add(name); }
            }
            else
            {
                BindStatsGeometry(layout, b);
            }
        }
        else if (id == "overview")
        {
            // The window's caption is the selected tab's name (FUN_14049bf90); the research tab writes its level and rate over it (BindResearchTab).
            var tabs = layout.Find("TabsMain")?.Children.Where(w => w.Type == "TabItem").ToArray() ?? [];
            int shown = faithfulScreens.GetTab("overview", "TabsMain");
            b.Text["Root"] = shown >= 0 && shown < tabs.Length ? Loc.Tr(tabs[shown].Properties.GetValueOrDefault("Caption") ?? "") : "OVERVIEW";
            foreach (string name in new[] { "MapCenterButton", "MapZoomInButton", "MapZoomOutButton" }) { b.Disabled.Remove(name); b.Actions[name] = name; }
            foreach (var tab in layout.Widgets.Where(w => w.Type == "TabItem")) b.Properties[tab.Name] = new Dictionary<string, string> { ["Visible"] = "true" };
            BindFactionTab(b);
            b.Text["MessagesTextBox"] = MessagesText();
            b.Properties["MessagesTextBox"] = new Dictionary<string, string> { ["Static"] = "true", ["WordWrap"] = "true", ["TextAlign"] = "Left Top" };
            BindSquadsTab(b);
            BindResearchTab(b);
            BindCraftTab(b);
            BindFormation(b);
            // Editor preview markers are unrelated to the current camera and orders.
            b.Hidden.Add("CameraMarker"); b.Hidden.Add("MovemementMarker");
            b.NativeScroll.Add("MapScrollView");   // the map pans and zooms itself (PlayerInterface.Map.cs); the canvas is not the layout's 4096 px
        }
        if (id is "inventory" or "bag" or "limbs" or "trader" or "loot" or "loot-bag") BindInventoryGeometry(id, b);
        return b;
    }
    /// <summary>
    /// The attributes and four skill columns, ordered as observed in the supplied original-game screenshot.
    /// </summary>
    internal static readonly StatsEnumerated[][] StatPanels =
    [
        [StatsEnumerated.Strength, StatsEnumerated.Toughness, StatsEnumerated.Dexterity, StatsEnumerated.Perception],
        [StatsEnumerated.Katanas, StatsEnumerated.Sabres, StatsEnumerated.Hackers, StatsEnumerated.HeavyWeapons, StatsEnumerated.Blunt, StatsEnumerated.Polearms],
        [StatsEnumerated.MeleeAttack, StatsEnumerated.MeleeDefence, StatsEnumerated.Dodge, StatsEnumerated.MartialArts,
            StatsEnumerated.Turrets, StatsEnumerated.Crossbows, StatsEnumerated.FriendlyFire],
        [StatsEnumerated.Stealth, StatsEnumerated.Lockpicking, StatsEnumerated.Thieving, StatsEnumerated.Assassination, StatsEnumerated.Athletics, StatsEnumerated.Swimming],
        [StatsEnumerated.Medic, StatsEnumerated.Engineering, StatsEnumerated.Robotics, StatsEnumerated.Science,
            StatsEnumerated.SmithingWeapon, StatsEnumerated.SmithingArmour, StatsEnumerated.SmithingBow, StatsEnumerated.Labouring, StatsEnumerated.Farming, StatsEnumerated.Cooking],
    ];

    static string StatLabel(StatsEnumerated stat) => Loc.Tr(StatLabelEnglish(stat));

    static string StatLabelEnglish(StatsEnumerated stat) => stat switch
    {
        StatsEnumerated.Thieving => "Thievery", StatsEnumerated.HeavyWeapons => "Heavy weapons",
        StatsEnumerated.MeleeAttack => "Melee attack", StatsEnumerated.MeleeDefence => "Melee defence",
        StatsEnumerated.SmithingWeapon => "Weapon smith", StatsEnumerated.SmithingArmour => "Armour smith",
        StatsEnumerated.SmithingBow => "Crossbow smith", StatsEnumerated.FriendlyFire => "Precision shooting",
        StatsEnumerated.MartialArts => "Martial arts",
        StatsEnumerated.Medic => "Field medic", StatsEnumerated.Engineering => "Engineer", _ => stat.ToString(),
    };
    void AddFaithfulContent(string id, List<UiControl> controls)
    {
        if (id == "title" && UiMode == UiMode.Meitou) { MeitouTitleMenu(controls); return; }
        if (id == "stats") { AddStatsContent(controls); return; }
        if (id == "new-game") { AddStartLines(controls); AddAdvancedContent(controls); return; }
        if (id == "message") { StyleMessageBox(controls); return; }
        if (id is "load" or "import" or "save" && UiMode == UiMode.Faithful) { AddSlotInfo(controls); if (id == "import") AddAdvancedContent(controls, "import"); return; }
        if (id == "editor") { AddEditorContent(controls); return; }
        if (id == "build") { AddBuildContent(controls); return; }
        if (id == "dialogue") { AddDialogueChoices(controls); return; }
        if (id == CharacterTradeScreen) { AddCharacterTradeRows(controls); return; }
        if (id == "prospect") { AddProspectContent(controls); return; }
        if (id is "building" or "bench" or "storage") { AddMachineContent(id, controls); return; }
        // The bag window has no caption buttons (the vanilla screenshot): OPEN BAG closes it.
        if (id is "bag" or "loot-bag") controls.RemoveAll(c => c.Action.StartsWith("mygui:close", StringComparison.Ordinal) || c.Action.StartsWith("mygui:pin", StringComparison.Ordinal));
        AddMapContent(id, controls);
        if (id == "overview") { AddResearchContent(controls); AddCraftContent(controls); AddAiContent(controls); AddSquadsContent(controls); AddFactionContent(controls); }
        if (id == "trader") { ArrangeTraderWindow(controls); AddTraderContent(controls); return; }
        // The loot windows show the open loot's body as the character windows show the lead; a pinned window its own character.
        var shown = WindowCharacter(id);
        if (IsPinnedInventory(id)) id = "inventory";
        if (id is not ("inventory" or "bag" or "limbs" or "loot" or "loot-bag") || shown is not { ItemGrid: { } grid } lead) return;
        bool bagWindow = id is "bag" or "loot-bag";
        string gridSection = bagWindow ? "backpack_content" : "main";
        int columns = bagWindow ? grid.BagWidth : grid.Width, rows = bagWindow ? grid.BagHeight : grid.Height;
        if (bagWindow) ArrangeContainerGrid(id, controls, columns, rows);
        else if (id == "limbs") ArrangeLimbSlots(controls, lead);
        else
        {
            ArrangeEquipmentGrid(controls, grid);
            CloseSlots(controls, lead);
        }
        if (id is "inventory" or "loot") AddInventorySummary(controls, lead, animal: IsAnimal(lead));
        foreach (var item in grid.Items.Where(i => bagWindow ? i.Section == "backpack_content" : id == "limbs" ? i.Section.EndsWith("_replace", StringComparison.Ordinal) : i.Section != "backpack_content"))
        {
            if (Lifted(Meitou.Simulation.Items.ItemOwner.Of(lead.Id), item.Index)) continue;
            if (controls.FirstOrDefault(c => c.Name == item.Section) is not { } slot) continue;
            var area = slot.Bounds;
            if (item.Section == gridSection && columns > 0 && rows > 0) area = new(area.X + item.X * area.Width / columns, area.Y + item.Y * area.Height / rows, item.Width * area.Width / columns, item.Height * area.Height / rows);
            else area = new(area.X + item.X * InventoryCell, area.Y + item.Y * InventoryCell, item.Width * InventoryCell, item.Height * InventoryCell);
            // The name carries the character: the loot window's cells would otherwise take over the same names' tooltips in the player's window.
            AddItemCell(controls, $"inventory-item-{lead.Id}-{item.Index}", area, item.Record, item.Material, item.Name, item.Quantity, ItemTip.Of(item, SellTip(lead.Id, item.Index), MapItemCaption(item.Record)), "InventoryItem", item.Index, slot.Bounds.Intersect(slot.Clip ?? slot.Bounds), charge: item.Charge, colour: item.Colour);
        }
    }
    readonly List<(string Text, MessageTone Tone)> uiMessages = [];
    /// <summary>
    /// The orders panel's toggles by the words in their widget names (the layout names only <c>OrdersChaseButton</c>, <c>OrdersMedicButton</c> and <c>OrdersRescueButton</c>
    /// for certain, docs/game/ui-screens.md section 4; the rest are matched by the button's word, so a differently spelt name still binds). Prospect is its own action (PlayerInterface.Prospecting.cs).
    /// </summary>
    static readonly (string Word, CharacterOrders Order)[] OrderWords =
    [
        ("Block", CharacterOrders.Block), ("Hold", CharacterOrders.Hold), ("Chase", CharacterOrders.Jobs), ("Passive", CharacterOrders.Passive),
        ("Ranged", CharacterOrders.Ranged), ("Taunt", CharacterOrders.Taunt), ("Sneak", CharacterOrders.Sneak), ("Stealth", CharacterOrders.Sneak),
        ("Jobs", CharacterOrders.Jobs),
    ];

    /// <summary>Binds the order toggles (checked while the lead selected character has the order) and the speed arrows of the HUD.</summary>
    void BindOrders(UiBindings b, Meitou.Simulation.CharacterSnapshot? lead)
    {
        foreach (var w in faithfulHud.Widgets)
        {
            if (w.Type != "Button" || !w.Name.StartsWith("Orders", StringComparison.Ordinal)) continue;
            foreach (var (word, order) in OrderWords)
            {
                if (!w.Name.Contains(word, StringComparison.Ordinal)) continue;
                b.Actions[w.Name] = $"meitou:order.{order}";
                if (lead is null) b.Disabled.Add(w.Name);
                if (lead is not null && (lead.Orders & order) != 0) b.Properties[w.Name] = new Dictionary<string, string> { ["Checked"] = "true" };
                break;
            }
        }
        b.Actions["SpeedArrowLeft"] = "meitou:runspeed.-1";
        b.Actions["SpeedArrowRight"] = "meitou:runspeed.1";
        // The icon shows the lead's mode (docs/game/ui-screens.md); with nobody selected it keeps the layout's.
        if (lead is not null) b.Properties["SpeedPanel"] = new Dictionary<string, string> { ["ImageName"] = RunSpeedIcons[Math.Min((int)lead.RunSpeed, RunSpeedIcons.Length - 1)] };
    }

    /// <summary>
    /// The jobs list (<c>JobsPanel</c>, docs/game/ui-screens.md section 4): the lead's jobs, one row each with an X that removes it from that character; with none, the
    /// layout's hint. The job list (<see cref="Meitou.Simulation.Production.PermaJobs"/>, the medic and rescue jobs included)
    /// as "&lt;row text&gt;: &lt;building&gt;" (the original's row text is <b>Unknown</b>); rows are dragged to reorder.
    /// </summary>
    /// <summary>The first Obedient Slave job of the jobs list (the tutorial highlight PanelOrdersJobObedience), -1: none.</summary>
    int obedienceJob = -1;

    void BindJobs(UiBindings b, Meitou.Simulation.CharacterSnapshot? lead)
    {
        obedienceJob = -1;
        var jobs = new List<UiListItem>();
        for (int i = 0; i < (lead?.Jobs.Count ?? 0); i++)
        {
            var job = lead!.Jobs[i];
            if (job.Type == Meitou.Data.Gameplay.Ai.TaskType._SLAVE_OBEDIENCE && obedienceJob < 0) obedienceJob = i;
            string text = Loc.Tr(Meitou.Simulation.Production.PermaJobs.Text(job.Type) ?? job.Type.ToString());
            if (job.Site is { } id && session.CurrentSnapshot.Buildings.FirstOrDefault(s => s.Id == id) is { } site) text += ": " + SiteName(site);
            jobs.Add(new("job:" + i.ToString(CultureInfo.InvariantCulture), text));
        }
        b.Lists["JobsPanel"] = jobs;
        if (jobs.Count > 0) b.Hidden.Add("JobsPanelInfo");
        else b.Text["JobsPanelInfo"] = Loc.Tr("[No jobs assigned]") + "\n" + Loc.Tr("[To add jobs, hold shift key when giving an order]");
    }

    /// <summary>The X at the right end of each jobs row (the original's remove button, its tutorial: "remove a job at any time by pressing the X button").</summary>
    static void AddJobRemoveButtons(List<UiControl> controls)
    {
        foreach (var row in controls.Where(c => c.Properties.GetValueOrDefault("ListOwner") == "JobsPanel").ToArray())
        {
            float size = row.Bounds.Height - 4;
            controls.Add(new($"{row.Name}-remove", "Button", new(row.Bounds.X + row.Bounds.Width - size - 2, row.Bounds.Y + 2, size, size), "", "meitou:job-remove",
                "Kenshi_CloseButtonSkin", true, new Dictionary<string, string>(), Vector4.Zero, Argument: row.Argument, Clip: row.Clip));
        }
    }

    /// <summary>The job row (its list index) pressed and being dragged to a new place in the list; null: none.</summary>
    int? jobDrag;

    /// <summary>The job-list index of the jobs row under a point (not its X), or null.</summary>
    static int? JobRowAt(IReadOnlyList<UiControl> controls, Vector2 p)
    {
        if (controls.Any(c => c.Action == "meitou:job-remove" && c.Bounds.Contains(p.X, p.Y))) return null;
        var row = controls.FirstOrDefault(c => c.Properties.GetValueOrDefault("ListOwner") == "JobsPanel" && c.Bounds.Contains(p.X, p.Y));
        return row is not null && row.Argument.StartsWith("job:", StringComparison.Ordinal) && int.TryParse(row.Argument.AsSpan(4), NumberStyles.None, CultureInfo.InvariantCulture, out int i) ? i : null;
    }

    /// <summary>A press on a job row starts dragging it (true: taken).</summary>
    bool JobDragStart(IReadOnlyList<UiControl> controls, Vector2 p)
    {
        jobDrag = JobRowAt(controls, p);
        return jobDrag is not null;
    }

    /// <summary>The button let go: a dragged job dropped on another row of the list moves there.</summary>
    void JobDragEnd(Vector2 p)
    {
        if (jobDrag is not { } from) return;
        jobDrag = null;
        if (JobRowAt(ArrangeUi(), p) is { } to && to != from && (Selected().FirstOrDefault() ?? Lead()) is { IsPlayer: true } lead)
            Say(new Meitou.Simulation.Production.MoveJob(lead.Id, from, to) { Tick = session.World.Tick });
    }

    /// <summary>Takes a job (the jobs list's row id) off the lead whose list shows it.</summary>
    void RemoveJob(string job)
    {
        if ((Selected().FirstOrDefault() ?? Lead()) is not { IsPlayer: true } lead) return;
        if (job.StartsWith("job:", StringComparison.Ordinal) && int.TryParse(job.AsSpan(4), NumberStyles.None, CultureInfo.InvariantCulture, out int index))
            Say(new Meitou.Simulation.Production.RemoveJob(lead.Id, index) { Tick = session.World.Tick });
    }

    /// <summary>The speed icon per mode: the <c>Kenshi_CharacterSpeedSet</c> image names, in mode order.</summary>
    static readonly string[] RunSpeedIcons = ["Walk", "Jog", "Run", "Group"];
}
