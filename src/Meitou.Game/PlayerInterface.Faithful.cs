using System.Globalization;
using System.Numerics;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Data.Ui;
using Meitou.Game.Host.Input;
using Meitou.Game.Ui;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Player;

namespace Meitou.Game;

sealed partial class PlayerInterface
{
    internal string selectedStat = ((int)StatsEnumerated.Katanas).ToString(CultureInfo.InvariantCulture);
    public bool UiCapturesPointer => UiVisible && (faithfulScreens.CapturesPointer || worldMap.mapDrag is not null || inventoryWindows.liftPress is not null || scrollDrag is not null);
    /// <summary>A modal screen (the Esc menu, a message box) is up: the pointer is the interface's, even in the free camera.</summary>
    public bool ModalOpen => UiVisible && faithfulScreens.HasModal;
    /// <summary>The faithful screens open, bottom first (the play benchmark says which modal one stopped the game).</summary>
    public IReadOnlyList<string> OpenScreens => faithfulScreens.OpenScreens;
    /// <summary>
    /// The widgets a click can reach in the last arranged frame (the topmost control at its own centre): the HUD and its panels in either UI mode and the open screens,
    /// for the agent control channel (ui-tree, ui-click). Screen is the faithful screen's id, "hud" for the rest.
    /// </summary>
    internal IEnumerable<(string Screen, UiControl Control)> UiClickables()
    {
        if (!UiVisible) yield break;
        var controls = ArrangeUi();
        var owner = new Dictionary<UiControl, string>(ReferenceEqualityComparer.Instance);
        foreach (var screen in faithfulScreens.LastFrame)
            foreach (var c in screen.Controls) owner[c] = screen.Id;
        foreach (var c in controls)
        {
            float cx = c.Bounds.X + c.Bounds.Width / 2, cy = c.Bounds.Y + c.Bounds.Height / 2;
            if (!c.Enabled || c.Bounds.Width < 2 || c.Bounds.Height < 2 || cx < 0 || cy < 0 || cx > Width || cy > Height) continue;
            if (c.Type != "Button" && c.Action.Length == 0) continue;
            if (MyGuiPresentation.HitTest(controls, cx, cy) is { } top && ReferenceEquals(top, c)) yield return (owner.GetValueOrDefault(c, "hud"), c);
        }
    }

    /// <summary>A left click at <paramref name="p"/> through the game's own pointer path (press and release).</summary>
    internal void UiClickAt(Vector2 p)
    {
        MouseDown(MouseButton.Left, p, false);
        MouseUp(MouseButton.Left, p, false);
    }

    /// <summary>Opens a registered faithful screen by id (the agent control channel); false when there is none.</summary>
    internal bool OpenFaithfulScreen(string id)
    {
        if (!faithfulScreens.IsRegistered(id)) return false;
        faithfulScreens.Open(id);
        UpdateMenuPause();
        return true;
    }

    /// <summary>Keys the host keeps for itself (the sandbox's letters), so the faithful window shortcuts leave them alone.</summary>
    public HashSet<Key> ReservedKeys { get; } = [];
    internal void UpdateMenuPause() => session.Simulation.RequestedPause = settingsOpen || faithfulScreens.HasModal;
    public bool UiKey(Key key, bool ctrl = false, bool shift = false)
    {
        uiInput++;
        if (!UiVisible) return false;
        if (key == Meitou.Game.Host.Input.Key.Escape && inventoryWindows.CancelInventoryDrag()) return true;
        // toggle_camera_grid (Shift+; fixed: a binding carries no modifier, and ; alone is the free camera's).
        if (shift && key == Meitou.Game.Host.Input.Key.Semicolon && !faithfulScreens.HasKeyboardFocus) { cameraGrid = !cameraGrid; return true; }
        if (key == Meitou.Game.Host.Input.Key.Escape && levelEditorPanel.LevelEscape()) return true;
        if (key == Meitou.Game.Host.Input.Key.Escape)
        {
            if (advancedOptions.advancedOpen && faithfulScreens.OpenScreens.LastOrDefault() == "new-game") { advancedOptions.advancedOpen = false; return true; }
            // The bare title: Esc hides the credits and quits at once, no confirmation (FUN_14082b010 sets the quit flag unless FUN_140914a90 closed a title
            // window; hiding the credits does not count as closing one). With a title window open the generic Esc below closes it.
            if (faithfulScreens.IsOpen("title") && faithfulScreens.OpenScreens.Count == 1)
            {
                frontend.showCredits = false;
                ScreenAction?.Invoke(new("menu", "ExitGameButton", "exit"));
                return true;
            }
            // The editor stays under the game menu (the original's Esc in the editor; it has no cancel: CONFIRM closes it).
            if (characterEditor.EditorOpen && faithfulScreens.OpenScreens[^1] == "editor") { faithfulScreens.Open("menu"); UpdateMenuPause(); return true; }
            // Esc presses the last button of the top message box (Return the first, docs/game/ui-input.md section 5).
            if (faithfulScreens.IsOpen("message")) frontend.HandleFrontendAction(new("message", frontend.CancelButton, frontend.CancelButton));
            // While looting, one Esc closes the looter's and the body's windows together (their bags follow); the loot closes with its window.
            else if (faithfulScreens.IsOpen("loot") && faithfulScreens.OpenScreens[^1] is "inventory" or "bag" or "loot" or "loot-bag") { faithfulScreens.Close("loot"); faithfulScreens.Close("inventory"); }
            // So does trading: one Esc closes the trader with the customer's windows, whichever of them was clicked last (closing the trader ends the trade).
            else if (faithfulScreens.IsOpen("trader") && faithfulScreens.OpenScreens[^1] is "inventory" or "bag" or "limbs" or "trader") { faithfulScreens.Close("trader"); faithfulScreens.Close("inventory"); }
            else if (!faithfulScreens.Escape()) faithfulScreens.Open("menu");
            UpdateMenuPause();
            return true;
        }
        if (faithfulScreens.HasKeyboardFocus && EditFocusKey(key, ctrl, shift)) return true;
        // Delete or Backspace deletes the save selected in the load, save or import window (LoadSaveWindow::keyPressed, docs/game/ui-screens.md "Deleting a save").
        if (key is Meitou.Game.Host.Input.Key.Delete or Meitou.Game.Host.Input.Key.Backspace && faithfulScreens.OpenScreens is [.., "load" or "save" or "import"] && frontend.selectedSlot is { } doomed) { frontend.AskDeleteSave(doomed); return true; }
        // Tab bound to a window key (Inventory, Character...) opens it on a window with a box; the traversal keeps modal windows (#383).
        if (key == Meitou.Game.Host.Input.Key.Tab && (faithfulScreens.HasModal || !gameHotkeys.WindowKey(key)) && faithfulScreens.FocusNext(shift)) return true;
        if (key == Meitou.Game.Host.Input.Key.Enter && faithfulScreens.Submit()) return true;
        if (key == Meitou.Game.Host.Input.Key.Space && faithfulScreens.ActivateFocused()) return true;
        // The list or combo box last clicked takes the MyGUI ListBox keys (docs/formats/mygui.md "Widget runtime").
        if (key switch
            {
                Meitou.Game.Host.Input.Key.Up => UiListKey.Up, Meitou.Game.Host.Input.Key.Down => UiListKey.Down, Meitou.Game.Host.Input.Key.PageUp => UiListKey.PageUp,
                Meitou.Game.Host.Input.Key.PageDown => UiListKey.PageDown, Meitou.Game.Host.Input.Key.Home => UiListKey.Home, Meitou.Game.Host.Input.Key.End => UiListKey.End,
                Meitou.Game.Host.Input.Key.Enter => UiListKey.Accept, _ => (UiListKey?)null,
            } is { } listKey && faithfulScreens.ListKey(listKey)) return true;
        if (dialogueWindow.DialogueKey(key)) return true;
        // A modal screen (the menu, a message box, the character editor) takes the keys, except the host's own: quicksave / quickload, the screenshot and the debug overlays.
        if (faithfulScreens.HasModal)
            return !(gameHotkeys.Bound(InputAction.QuickSave, key) || gameHotkeys.Bound(InputAction.QuickLoad, key) || gameHotkeys.Bound(InputAction.Screenshot, key) ||
                gameHotkeys.Bound(InputAction.ToggleStats, key) || gameHotkeys.Bound(InputAction.ToggleFps, key) || gameHotkeys.Bound(InputAction.CycleProfiler, key));
        // change_squad (Tab by default): the squad bar shows the next squad with members (docs/game/ui-input.md section 6).
        if (gameHotkeys.Bound(InputAction.ChangeSquad, key) && !TitleOpen && !characterEditor.EditorOpen) { Say(new ActiveSquadCommand(-1) { Tick = session.World.Tick }); return true; }
        if (ReservedKeys.Contains(key)) return false;
        if (gameHotkeys.Bound(InputAction.ToggleInventory, key) || gameHotkeys.Bound(InputAction.ToggleCharacterStats, key))
        { ExecuteUiAction(gameHotkeys.Bound(InputAction.ToggleInventory, key) ? "inventory" : "stats", ""); return true; }
        // toggle_help (F1, event 7): the Tutorials window (docs/game/ui-input.md section 6).
        if (gameHotkeys.Bound(InputAction.ToggleHelp, key) && !TitleOpen && !characterEditor.EditorOpen)
        {
            ToggleHelpWindow();
            return true;
        }
        // toggle_map / toggle_faction / toggle_research / toggle_crafting (M / unbound / T / Y) ask for overview tabs 0 / 1 / 2 / 3 (docs/game/ui-input.md section 6).
        // Pressing the key of the tab already showing closes the window (engine choice: the original's toggle rule is not traced).
        int? tab = gameHotkeys.Bound(InputAction.ToggleMap, key) ? 0 : gameHotkeys.Bound(InputAction.ToggleFaction, key) ? 1 : gameHotkeys.Bound(InputAction.ToggleResearch, key) ? 2 : gameHotkeys.Bound(InputAction.ToggleCrafting, key) ? 3 : null;
        if (tab is { } t)
        {
            if (faithfulScreens.IsOpen("overview") && faithfulScreens.GetTab("overview", "TabsMain") == t) faithfulScreens.Close("overview");
            else { faithfulScreens.Open("overview"); faithfulScreens.SetTab("overview", "TabsMain", t); }
            return true;
        }
        return false;
    }
    public void UiText(char value)
    {
        uiInput++;
        if (UiVisible) faithfulScreens.TextInput(value);
    }

    public bool UiScroll(Vector2 p, float steps)
    {
        uiInput++;
        if (!UiVisible) return false;
        ArrangeUi();
        if (InventoryWheel(p, steps)) return true;
        if (worldMap.MapWheel(p, steps)) return true;
        if (!faithfulScreens.Contains(p) && portraitBar.SquadBarWheel(p, steps)) return true;
        if (JobsWheel(p, steps)) return true;
        if (squadWindows.SquadsWheel(p, steps)) return true;
        if (factionWindow.FactionWheel(p, steps)) return true;
        if (production.ResearchWheel(p, steps)) return true;
        return characterEditor.EditorWheel(p, steps) || faithfulScreens.Wheel(p, steps);
    }
    internal void FaithfulAction(MyGuiEvent e)
    {
        interfaceSounds.SoundForScreen(e);
        if (e.Action.StartsWith("inventory-scroll:", StringComparison.Ordinal))
        { ScrollInventory(e.Screen, float.Parse(e.Action[17..], CultureInfo.InvariantCulture)); return; }
        if (e.Action.StartsWith("inventory-scroll-page:", StringComparison.Ordinal))
        { ScrollInventoryPage(e.Screen, float.Parse(e.Action[22..], CultureInfo.InvariantCulture)); return; }
        if (e.Screen == "editor") characterEditor.EditorAction(e);
        else if (e.Screen == "file") characterEditor.FileDialogAction(e);
        else if (e.Screen == FeedbackPanel.FeedbackScreen) feedbackPanel.FeedbackFormAction(e);
        else if (frontend.HandleFrontendAction(e)) UpdateMenuPause();
        else if (e.Screen == "stats" && e.Action == "select") selectedStat = e.Text;
        else if (e.Screen == "stats" && e.Action.StartsWith("stat:", StringComparison.Ordinal)) selectedStat = e.Action[5..];
        else if (e.Screen == "build") build.BuildAction(e);
        else if (e.Screen == "leveleditor") levelEditorPanel.LevelEditorAction(e);
        else if (e.Screen == "dialogue") dialogueWindow.DialogueAction(e);
        else if (e.Screen == TradeWindows.CharacterTradeScreen) tradeWindows.CharacterTradeAction(e);
        else if (e.Screen == "prospect") prospecting.ProspectScreenAction(e);
        else if (e.Screen == "tutorials") tutorialWindow.TutorialsWindowAction(e);
        else if (e.Screen == TownPanel.TownScreen) townPanel.TownAction(e);
        else if (e.Screen == "overview") { worldMap.MapAction(e.Widget); production.ResearchAction(e); production.CraftAction(e); squadWindows.AiAction(e); squadWindows.SquadsAction(e); factionWindow.FactionAction(e); }
        else if (e.Screen is "building" or "bench" or "storage") production.MachineAction(e);
        else if (e.Screen == "loot" && e.Widget == "OpenBagButton") tradeWindows.lootBagOpen = !tradeWindows.lootBagOpen;
        else if (e.Screen == "loot" && e.Widget == "LimbsButton") inventoryWindows.ToggleLimbs(tradeWindows.LootBody());
        else if (e.Screen == "trader" && e.Widget == "ArrangeButton") Say(new Meitou.Simulation.Trading.ArrangeStock { Tick = session.World.Tick });
        else if (e.Screen is "inventory" or "bag" || InventoryWindows.IsPinnedInventory(e.Screen))
        {
            if (e.Widget == "OpenBagButton") inventoryWindows.ToggleBag();
            else if (e.Widget == "LimbsButton") inventoryWindows.ToggleLimbs();
            else if (e.Widget == "ArrangeButton" && inventoryWindows.WindowCharacter(e.Screen) is { } lead) Say(new Meitou.Simulation.Items.ArrangeInventory(lead.Id) { Tick = session.World.Tick });
        }
    }
    internal UiBindings BindFaithfulScreen(string id)
    {
        var b = new UiBindings { TextHeight = c => uiPainter?.TextHeight(c, Height, StandardPixels) ?? 0 };
        var layout = faithfulScreens.Layout(id);
        string screen = id;
        bool pinned = InventoryWindows.IsPinnedInventory(id);
        if (pinned) id = "inventory";   // a pinned window binds as the inventory, for its own character
        var lead = pinned || id == "limbs" ? inventoryWindows.WindowCharacter(screen) : id is "inventory" or "bag" ? inventoryWindows.InventoryOwner() : Selected().FirstOrDefault();
        foreach (var button in layout.Widgets.Where(w => w.Type == "Button" && !w.Properties.ContainsKey("TemplateEvent"))) b.Disabled.Add(button.Name);
        if (id == FeedbackPanel.FeedbackScreen) { feedbackPanel.BindFeedbackScreen(b); return b; }
        if (frontend.BindFrontend(id, b)) return b;
        if (id == "editor") { characterEditor.BindEditor(b); return b; }
        if (id == "file") { characterEditor.BindFileDialog(b); return b; }
        else if (id == "trader") tradeWindows.BindTraderScreen(b);
        else if (id == "dialogue") dialogueWindow.BindDialogueScreen(b);
        else if (id == TradeWindows.CharacterTradeScreen) tradeWindows.BindCharacterTradeScreen(b);
        else if (id == "prospect") prospecting.BindProspectScreen(b);
        else if (id == "tutorials") tutorialWindow.BindTutorialsWindow(b);
        else if (id == TownPanel.TownScreen) townPanel.BindTownScreen(b);
        else if (id == "build") build.BindBuildScreen(b);
        else if (id == "leveleditor") levelEditorPanel.BindLevelEditorScreen(b);
        else if (id is "building" or "bench" or "storage") production.BindMachineScreen(id, b);
        else if (TradeWindows.IsLootScreen(id)) tradeWindows.BindLootScreen(id, b);
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
                    { b.Disabled.Remove(name); b.Actions[name] = name; if (lead is null || name == "LimbsButton" && (pinned || !InventoryWindows.HasLimbWindow(lead))) b.Disabled.Add(name); }
            }
            else
            {
                statsWindow.BindStatsGeometry(layout, b);
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
            factionWindow.BindFactionTab(b);
            b.Text["MessagesTextBox"] = MessagesText();
            b.Properties["MessagesTextBox"] = new Dictionary<string, string> { ["Static"] = "true", ["WordWrap"] = "true", ["TextAlign"] = "Left Top" };
            squadWindows.BindSquadsTab(b);
            production.BindResearchTab(b);
            production.BindCraftTab(b);
            squadWindows.BindFormation(b);
            // Editor preview markers are unrelated to the current camera and orders.
            b.Hidden.Add("CameraMarker"); b.Hidden.Add("MovemementMarker");
            b.NativeScroll.Add("MapScrollView");   // the map pans and zooms itself (WorldMap); the canvas is not the layout's 4096 px
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

    internal static string StatLabel(StatsEnumerated stat) => Loc.Tr(StatLabelEnglish(stat));

    static string StatLabelEnglish(StatsEnumerated stat) => stat switch
    {
        StatsEnumerated.Thieving => "Thievery", StatsEnumerated.HeavyWeapons => "Heavy weapons",
        StatsEnumerated.MeleeAttack => "Melee attack", StatsEnumerated.MeleeDefence => "Melee defence",
        StatsEnumerated.SmithingWeapon => "Weapon smith", StatsEnumerated.SmithingArmour => "Armour smith",
        StatsEnumerated.SmithingBow => "Crossbow smith", StatsEnumerated.FriendlyFire => "Precision shooting",
        StatsEnumerated.MartialArts => "Martial arts",
        StatsEnumerated.Medic => "Field medic", StatsEnumerated.Engineering => "Engineer", _ => stat.ToString(),
    };
    internal void AddFaithfulContent(string id, List<UiControl> controls)
    {
        if (id == "title") { if (UiMode == UiMode.Meitou) titleMenu.MeitouTitleMenu(controls); titleMenu.AddDataProblems(controls); return; }
        if (id == "stats") { statsWindow.AddStatsContent(controls); return; }
        if (id == "new-game") { frontend.AddStartLines(controls); advancedOptions.AddAdvancedContent(controls); return; }
        if (id == "message") { frontend.StyleMessageBox(controls); return; }
        if (id is "load" or "import" or "save" && UiMode == UiMode.Faithful) { frontend.AddSlotInfo(controls); if (id == "import") advancedOptions.AddAdvancedContent(controls, "import"); return; }
        if (id == "editor") { characterEditor.AddEditorContent(controls); return; }
        if (id == "build") { build.AddBuildContent(controls); return; }
        if (id == "dialogue") { dialogueWindow.AddDialogueChoices(controls); return; }
        if (id == TradeWindows.CharacterTradeScreen) { tradeWindows.AddCharacterTradeRows(controls); return; }
        if (id == "prospect") { prospecting.AddProspectContent(controls); return; }
        if (id is "building" or "bench" or "storage") { production.AddMachineContent(id, controls); return; }
        // The bag window has no caption buttons (the vanilla screenshot): OPEN BAG closes it.
        if (id is "bag" or "loot-bag") controls.RemoveAll(c => c.Action.StartsWith("mygui:close", StringComparison.Ordinal) || c.Action.StartsWith("mygui:pin", StringComparison.Ordinal));
        worldMap.AddMapContent(id, controls);
        if (id == "overview") { production.AddResearchContent(controls); production.AddCraftContent(controls); squadWindows.AddAiContent(controls); squadWindows.AddSquadsContent(controls); factionWindow.AddFactionContent(controls); }
        if (id == "trader") { tradeWindows.ArrangeTraderWindow(controls); tradeWindows.AddTraderContent(controls); return; }
        // The loot windows show the open loot's body as the character windows show the lead; a pinned window its own character.
        var shown = inventoryWindows.WindowCharacter(id);
        if (InventoryWindows.IsPinnedInventory(id)) id = "inventory";
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
        if (id is "inventory" or "loot") AddInventorySummary(controls, lead, animal: TradeWindows.IsAnimal(lead));
        foreach (var item in grid.Items.Where(i => bagWindow ? i.Section == "backpack_content" : id == "limbs" ? i.Section.EndsWith("_replace", StringComparison.Ordinal) : i.Section != "backpack_content"))
        {
            if (inventoryWindows.Lifted(Meitou.Simulation.Items.ItemOwner.Of(lead.Id), item.Index)) continue;
            if (controls.FirstOrDefault(c => c.Name == item.Section) is not { } slot) continue;
            var area = slot.Bounds;
            if (item.Section == gridSection && columns > 0 && rows > 0) area = new(area.X + item.X * area.Width / columns, area.Y + item.Y * area.Height / rows, item.Width * area.Width / columns, item.Height * area.Height / rows);
            else area = new(area.X + item.X * InventoryCell, area.Y + item.Y * InventoryCell, item.Width * InventoryCell, item.Height * InventoryCell);
            // The name carries the character: the loot window's cells would otherwise take over the same names' tooltips in the player's window.
            AddItemCell(controls, $"inventory-item-{lead.Id}-{item.Index}", area, item.Record, item.Material, item.Name, item.Quantity, TooltipRuntime.ItemTip.Of(item, tradeWindows.SellTip(lead.Id, item.Index), worldMap.MapItemCaption(item.Record)), "InventoryItem", item.Index, slot.Bounds.Intersect(slot.Clip ?? slot.Bounds), charge: item.Charge, colour: item.Colour);
        }
    }
    readonly List<(string Text, MessageTone Tone)> uiMessages = [];
    /// <summary>
    /// The orders panel's toggles by the words in their widget names (the layout names only <c>OrdersChaseButton</c>, <c>OrdersMedicButton</c> and <c>OrdersRescueButton</c>
    /// for certain, docs/game/ui-screens.md section 4; the rest are matched by the button's word, so a differently spelt name still binds). Prospect is its own action (ProspectingPanel).
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

    /// <summary>The jobs list's row layout (<c>Kenshi_OrdersPanelOrder.layout</c>: a <c>Kenshi_ConnectBarSkin</c> bar, <c>OrderText</c> and <c>RemoveButton</c>).</summary>
    MyGuiLayout? ordersRowLayout;

    /// <summary>The first Obedient Slave job of the jobs list (the tutorial highlight PanelOrdersJobObedience), -1: none.</summary>
    internal int obedienceJob = -1;

    /// <summary>The jobs rows drawn at half alpha: the jobs switch is off and the job does not run without it (<see cref="BindJobs"/>).</summary>
    readonly HashSet<string> dimmedJobs = [];

    /// <summary>
    /// The jobs list (<c>JobsPanel</c>, docs/game/ui-screens.md section 4): the lead's jobs, one <c>Kenshi_OrdersPanelOrder</c> row each, int(24 x height / 1080) tall
    /// (<see cref="AddJobRowParts"/>), numbered from 1, with an X that removes it from that character; with none, the layout's hint. Rows are dragged to reorder.
    /// </summary>
    void BindJobs(UiBindings b, Meitou.Simulation.CharacterSnapshot? lead)
    {
        obedienceJob = -1;
        dimmedJobs.Clear();
        var jobs = new List<UiListItem>();
        bool switchedOn = lead is not null && (lead.Orders & CharacterOrders.Jobs) != 0;
        for (int i = 0; i < (lead?.Jobs.Count ?? 0); i++)
        {
            var job = lead!.Jobs[i];
            if (job.Type == Meitou.Data.Gameplay.Ai.TaskType._SLAVE_OBEDIENCE && obedienceJob < 0) obedienceJob = i;
            string id = "job:" + i.ToString(CultureInfo.InvariantCulture);
            if (!switchedOn && !Meitou.Simulation.Production.PermaJobs.IgnoresSwitch(job.Type)) dimmedJobs.Add(id);
            jobs.Add(new(id, (i + 1).ToString(CultureInfo.InvariantCulture) + ": " + JobRowText(job)));
        }
        b.Lists["JobsPanel"] = jobs;
        b.Properties["JobsPanel"] = new Dictionary<string, string>
        {
            ["LineSkin"] = "Kenshi_ConnectBarSkin", ["LineHeight"] = ((int)(24 * Height / 1080f)).ToString(CultureInfo.InvariantCulture),
        };
        b.ScrollOffsets["JobsPanel"] = inventoryScroll.GetValueOrDefault("JobsPanel");
        if (jobs.Count > 0) b.Hidden.Add("JobsPanelInfo");
        // The brackets are the panel's, not the catalog's (its keys have none: "No jobs assigned", "To add jobs, hold shift key when giving an order").
        else b.Text["JobsPanelInfo"] = "[" + Loc.Tr("No jobs assigned") + "]\n[" + Loc.Tr("To add jobs, hold shift key when giving an order") + "]";
    }

    bool JobsWheel(Vector2 p, float steps)
    {
        if (!inventoryViews.TryGetValue("JobsPanel", out var view) || !view.View.Contains(p.X, p.Y)) return false;
        float line = 24 * Height / 1080f;
        inventoryScroll["JobsPanel"] = Math.Clamp(inventoryScroll.GetValueOrDefault("JobsPanel") - steps * line, 0, view.Maximum);
        return true;
    }

    void AddJobScrollBar(List<UiControl> controls)
    {
        int panelIndex = controls.FindIndex(c => c.Name == "JobsPanel");
        if (panelIndex < 0) return;
        var panel = controls[panelIndex];
        float line = MyGuiPresentation.Num(panel.Properties, "ListLine", 24 * Height / 1080f);
        float maximum = MyGuiPresentation.Num(panel.Properties, "ScrollMax");
        if (maximum <= 0) { inventoryViews.Remove("JobsPanel"); inventoryTracks.Remove("JobsPanel"); return; }
        float scroll = Math.Clamp(inventoryScroll.GetValueOrDefault("JobsPanel"), 0, maximum);
        inventoryScroll["JobsPanel"] = scroll;
        float barWidth = 17 * InventoryScale, gap = 3 * InventoryScale;
        var view = panel.Bounds with { Width = Math.Max(0, panel.Bounds.Width - barWidth - gap) };
        inventoryViews["JobsPanel"] = (view, maximum);
        for (int i = 0; i < controls.Count; i++)
            if (controls[i].Properties.GetValueOrDefault("ListOwner") == "JobsPanel" || controls[i].Name.StartsWith("JobsPanel@row", StringComparison.Ordinal))
                controls[i] = controls[i] with { Bounds = controls[i].Bounds with { Width = Math.Max(0, controls[i].Bounds.Width - barWidth - gap) }, Clip = controls[i].Clip?.Intersect(view) ?? view };
        AddScrollBar("JobsPanel", controls, new(panel.Bounds.X + panel.Bounds.Width - barWidth, panel.Bounds.Y, barWidth, panel.Bounds.Height),
            view.Height, view.Height + maximum, scroll, maximum, panel.Clip, "hud-jobs");
    }

    /// <summary>The list a HUD scrollbar part scrolls, by the part's name (AddScrollBar's prefix), or null.</summary>
    static string? HudScrollKey(string name) =>
        name.StartsWith("hud-jobs", StringComparison.Ordinal) ? "JobsPanel" : name.StartsWith("hud-squad", StringComparison.Ordinal) ? "SquadPanel" : null;

    /// <summary>A HUD scrollbar's arrow (<c>prefix:direction</c>, a line) or track (<c>prefix-page:direction</c>, a page).</summary>
    void HudScrollAction(string key, string action)
    {
        int colon = action.LastIndexOf(':');
        if (colon < 0 || !float.TryParse(action.AsSpan(colon + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out float direction)) return;
        if (!inventoryViews.TryGetValue(key, out var view)) return;
        float delta = action[..colon].EndsWith("-page", StringComparison.Ordinal) ? view.View.Height : 24 * Height / 1080f;
        inventoryScroll[key] = Math.Clamp(inventoryScroll.GetValueOrDefault(key) - direction * delta, 0, view.Maximum);
        if (key == "SquadPanel") portraitBar.squadBarScroll = inventoryScroll[key];
    }

    /// <summary>
    /// A jobs row: the type's text and the building it targets. The engineer works every site, not the one it was ordered on, so its row is plain
    /// "Engineer" (<b>Observed</b> in the original by players, #348/#353).
    /// </summary>
    string JobRowText(Meitou.Simulation.Production.PermaJob job)
    {
        string text = Loc.Tr(Meitou.Simulation.Production.PermaJobs.Text(job.Type) ?? job.Type.ToString());
        if (job.Type == Meitou.Data.Gameplay.Ai.TaskType.JOB_BUILDER) return text;

        if (job.Site is { } id && session.CurrentSnapshot.Buildings.FirstOrDefault(s => s.Id == id) is { } site) return text + ": " + build.SiteName(site);
        if (job.Site is { } dummyId && session.CurrentSnapshot.Dummies.FirstOrDefault(d => d.Id == dummyId) is { } dummy)
            return text + ": " + (build.buildHost?.ItemName(dummy.Record) ?? dummy.Record);
        return text;
    }

    /// <summary>
    /// Each jobs row's text and X over its bar (the original's cell layout <c>Kenshi_OrdersPanelOrder</c>; the X's tutorial: "remove a job at any time by pressing the X button").
    /// The row keeps the list's picking (selection, drag); only the X takes a click of its own.
    /// </summary>
    void AddJobRowParts(List<UiControl> controls)
    {
        if (ordersRowLayout is null) return;
        for (int i = controls.Count - 1; i >= 0; i--)
        {
            var row = controls[i];
            if (row.Properties.GetValueOrDefault("ListOwner") != "JobsPanel") continue;

            bool dimmed = dimmedJobs.Contains(row.Argument);
            controls[i] = row with { Caption = "", Properties = Dimmed(row.Properties, dimmed) };
            controls.InsertRange(i + 1, JobRowParts(row, dimmed));
        }
    }

    /// <summary>
    /// The row layout's text and X at <paramref name="row"/>: the layout is 300 x 20 px and sized to the row, so the text keeps its 10 px left and 35 px right margins and
    /// the X (Right VStretch) stays 30 px wide at the right end, a pixel short of the row's height. The text is the Main colour (the original's <c>#afa68b</c> prefix).
    /// </summary>
    List<UiControl> JobRowParts(UiControl row, bool dimmed)
    {
        var box = row.Bounds;
        var b = new UiBindings();
        var root = ordersRowLayout!.Roots.FirstOrDefault();
        if (root is not null) b.Positions[root.Name] = new(0, 0, box.Width, box.Height);
        b.Positions["OrderText"] = new(10, 0, Math.Max(0, box.Width - 45), box.Height);
        b.Positions["RemoveButton"] = new(box.Width - 30, 0, 30, Math.Max(0, box.Height - 1));
        b.Text["OrderText"] = row.Caption;
        b.Properties["OrderText"] = new Dictionary<string, string> { ["TextColour"] = MainColour };

        var parts = new List<UiControl>();
        foreach (var c in MyGuiPresentation.Arrange(ordersRowLayout, (int)box.Width, (int)box.Height, b, uiResources.Layers))
        {
            if (c.Name == root?.Name) continue;   // the row draws the bar

            var clip = c.Clip is { } inner ? (inner with { X = inner.X + box.X, Y = inner.Y + box.Y }).Intersect(row.Clip ?? box) : row.Clip;
            bool remove = c.Name == "RemoveButton";
            parts.Add(c with
            {
                Name = $"{row.Name}-{c.Name}", Bounds = c.Bounds with { X = c.Bounds.X + box.X, Y = c.Bounds.Y + box.Y }, Clip = clip,
                Action = remove ? "meitou:job-remove" : c.Action, Argument = row.Argument, Pickable = remove, Properties = Dimmed(c.Properties, dimmed),
            });
        }
        return parts;
    }

    /// <summary>A jobs row's properties at half alpha when <paramref name="dimmed"/> (the original's cell alpha 0.5).</summary>
    static IReadOnlyDictionary<string, string> Dimmed(IReadOnlyDictionary<string, string> properties, bool dimmed) =>
        dimmed ? new Dictionary<string, string>(properties, StringComparer.Ordinal) { ["Alpha"] = "0.5" } : properties;

    /// <summary>The job row (its list index) pressed and being dragged to a new place in the list; null: none.</summary>
    internal int? jobDrag;

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
