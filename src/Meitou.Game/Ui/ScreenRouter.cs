using System.Globalization;
using Meitou.Data.Ui;

namespace Meitou.Game.Ui;

/// <summary>Routes the faithful screens to the features that own them: each screen's actions, bindings and added content.</summary>
sealed class ScreenRouter(PlayerInterface player)
{
    internal void FaithfulAction(MyGuiEvent e)
    {
        player.interfaceSounds.SoundForScreen(e);
        if (e.Action.StartsWith("inventory-scroll:", StringComparison.Ordinal))
        { player.scrollBars.ScrollInventory(e.Screen, float.Parse(e.Action[17..], CultureInfo.InvariantCulture)); return; }
        if (e.Action.StartsWith("inventory-scroll-page:", StringComparison.Ordinal))
        { player.scrollBars.ScrollInventoryPage(e.Screen, float.Parse(e.Action[22..], CultureInfo.InvariantCulture)); return; }
        if (e.Screen == "editor") player.characterEditor.EditorAction(e);
        else if (e.Screen == "file") player.characterEditor.FileDialogAction(e);
        else if (e.Screen == FeedbackPanel.FeedbackScreen) player.feedbackPanel.FeedbackFormAction(e);
        else if (player.frontend.HandleFrontendAction(e)) player.UpdateMenuPause();
        else if (e.Screen == "stats" && e.Action == "select") player.statsWindow.selectedStat = e.Text;
        else if (e.Screen == "stats" && e.Action.StartsWith("stat:", StringComparison.Ordinal)) player.statsWindow.selectedStat = e.Action[5..];
        else if (e.Screen == "build") player.build.BuildAction(e);
        else if (e.Screen == "leveleditor") player.levelEditorPanel.LevelEditorAction(e);
        else if (e.Screen == "dialogue") player.dialogueWindow.DialogueAction(e);
        else if (e.Screen == TradeWindows.CharacterTradeScreen) player.tradeWindows.CharacterTradeAction(e);
        else if (e.Screen == "prospect") player.prospecting.ProspectScreenAction(e);
        else if (e.Screen == "tutorials") player.tutorialWindow.TutorialsWindowAction(e);
        else if (e.Screen == TownPanel.TownScreen) player.townPanel.TownAction(e);
        else if (e.Screen == "overview") { player.worldMap.MapAction(e.Widget); player.production.ResearchAction(e); player.production.CraftAction(e); player.squadWindows.AiAction(e); player.squadWindows.SquadsAction(e); player.factionWindow.FactionAction(e); }
        else if (e.Screen is "building" or "bench" or "storage") player.production.MachineAction(e);
        else if (e.Screen == "loot" && e.Widget == "OpenBagButton") player.tradeWindows.lootBagOpen = !player.tradeWindows.lootBagOpen;
        else if (e.Screen == "loot" && e.Widget == "LimbsButton") player.inventoryWindows.ToggleLimbs(player.tradeWindows.LootBody());
        else if (e.Screen == "trader" && e.Widget == "ArrangeButton") player.Say(new Meitou.Simulation.Trading.ArrangeStock { Tick = player.session.World.Tick });
        else if (e.Screen is "inventory" or "bag" || InventoryWindows.IsPinnedInventory(e.Screen))
        {
            if (e.Widget == "OpenBagButton") player.inventoryWindows.ToggleBag();
            else if (e.Widget == "LimbsButton") player.inventoryWindows.ToggleLimbs();
            else if (e.Widget == "ArrangeButton" && player.inventoryWindows.WindowCharacter(e.Screen) is { } lead) player.Say(new Meitou.Simulation.Items.ArrangeInventory(lead.Id) { Tick = player.session.World.Tick });
        }
    }
    internal UiBindings BindFaithfulScreen(string id)
    {
        var b = new UiBindings { TextHeight = c => player.uiPainter?.TextHeight(c, player.Height, player.StandardPixels) ?? 0 };
        var layout = player.faithfulScreens.Layout(id);
        string screen = id;
        bool pinned = InventoryWindows.IsPinnedInventory(id);
        if (pinned) id = "inventory";   // a pinned window binds as the inventory, for its own character
        var lead = pinned || id == "limbs" ? player.inventoryWindows.WindowCharacter(screen) : id is "inventory" or "bag" ? player.inventoryWindows.InventoryOwner() : player.Selected().FirstOrDefault();
        foreach (var button in layout.Widgets.Where(w => w.Type == "Button" && !w.Properties.ContainsKey("TemplateEvent"))) b.Disabled.Add(button.Name);
        if (id == FeedbackPanel.FeedbackScreen) { player.feedbackPanel.BindFeedbackScreen(b); return b; }
        if (player.frontend.BindFrontend(id, b)) return b;
        if (id == "editor") { player.characterEditor.BindEditor(b); return b; }
        if (id == "file") { player.characterEditor.BindFileDialog(b); return b; }
        else if (id == "trader") player.tradeWindows.BindTraderScreen(b);
        else if (id == "dialogue") player.dialogueWindow.BindDialogueScreen(b);
        else if (id == TradeWindows.CharacterTradeScreen) player.tradeWindows.BindCharacterTradeScreen(b);
        else if (id == "prospect") player.prospecting.BindProspectScreen(b);
        else if (id == "tutorials") player.tutorialWindow.BindTutorialsWindow(b);
        else if (id == TownPanel.TownScreen) player.townPanel.BindTownScreen(b);
        else if (id == "build") player.build.BindBuildScreen(b);
        else if (id == "leveleditor") player.levelEditorPanel.BindLevelEditorScreen(b);
        else if (id is "building" or "bench" or "storage") player.production.BindMachineScreen(id, b);
        else if (TradeWindows.IsLootScreen(id)) player.tradeWindows.BindLootScreen(id, b);
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
                player.statsWindow.BindStatsGeometry(layout, b);
            }
        }
        else if (id == "overview")
        {
            // The window's caption is the selected tab's name (FUN_14049bf90); the research tab writes its level and rate over it (BindResearchTab).
            var tabs = layout.Find("TabsMain")?.Children.Where(w => w.Type == "TabItem").ToArray() ?? [];
            int shown = player.faithfulScreens.GetTab("overview", "TabsMain");
            b.Text["Root"] = shown >= 0 && shown < tabs.Length ? Loc.Tr(tabs[shown].Properties.GetValueOrDefault("Caption") ?? "") : "OVERVIEW";
            foreach (string name in new[] { "MapCenterButton", "MapZoomInButton", "MapZoomOutButton" }) { b.Disabled.Remove(name); b.Actions[name] = name; }
            foreach (var tab in layout.Widgets.Where(w => w.Type == "TabItem")) b.Properties[tab.Name] = new Dictionary<string, string> { ["Visible"] = "true" };
            player.factionWindow.BindFactionTab(b);
            b.Text["MessagesTextBox"] = player.messageRoller.MessagesText();
            b.Properties["MessagesTextBox"] = new Dictionary<string, string> { ["Static"] = "true", ["WordWrap"] = "true", ["TextAlign"] = "Left Top" };
            player.squadWindows.BindSquadsTab(b);
            player.production.BindResearchTab(b);
            player.production.BindCraftTab(b);
            player.squadWindows.BindFormation(b);
            // Editor preview markers are unrelated to the current camera and orders.
            b.Hidden.Add("CameraMarker"); b.Hidden.Add("MovemementMarker");
            b.NativeScroll.Add("MapScrollView");   // the map pans and zooms itself (WorldMap); the canvas is not the layout's 4096 px
        }
        if (id is "inventory" or "bag" or "limbs" or "trader" or "loot" or "loot-bag") player.inventoryLayout.BindInventoryGeometry(id, b);
        return b;
    }

    internal void AddFaithfulContent(string id, List<UiControl> controls)
    {
        if (id == "title") { if (player.UiMode == UiMode.Meitou) player.titleMenu.MeitouTitleMenu(controls); player.titleMenu.AddDataProblems(controls); return; }
        if (id == "stats") { player.statsWindow.AddStatsContent(controls); return; }
        if (id == "new-game") { player.frontend.AddStartLines(controls); player.advancedOptions.AddAdvancedContent(controls); return; }
        if (id == "message") { player.frontend.StyleMessageBox(controls); return; }
        if (id is "load" or "import" or "save" && player.UiMode == UiMode.Faithful) { player.frontend.AddSlotInfo(controls); if (id == "import") player.advancedOptions.AddAdvancedContent(controls, "import"); return; }
        if (id == "editor") { player.characterEditor.AddEditorContent(controls); return; }
        if (id == "build") { player.build.AddBuildContent(controls); return; }
        if (id == "dialogue") { player.dialogueWindow.AddDialogueChoices(controls); return; }
        if (id == TradeWindows.CharacterTradeScreen) { player.tradeWindows.AddCharacterTradeRows(controls); return; }
        if (id == "prospect") { player.prospecting.AddProspectContent(controls); return; }
        if (id is "building" or "bench" or "storage") { player.production.AddMachineContent(id, controls); return; }
        // The bag window has no caption buttons (the vanilla screenshot): OPEN BAG closes it.
        if (id is "bag" or "loot-bag") controls.RemoveAll(c => c.Action.StartsWith("mygui:close", StringComparison.Ordinal) || c.Action.StartsWith("mygui:pin", StringComparison.Ordinal));
        player.worldMap.AddMapContent(id, controls);
        if (id == "overview") { player.production.AddResearchContent(controls); player.production.AddCraftContent(controls); player.squadWindows.AddAiContent(controls); player.squadWindows.AddSquadsContent(controls); player.factionWindow.AddFactionContent(controls); }
        if (id == "trader") { player.tradeWindows.ArrangeTraderWindow(controls); player.tradeWindows.AddTraderContent(controls); return; }
        // The loot windows show the open loot's body as the character windows show the lead; a pinned window its own character.
        var shown = player.inventoryWindows.WindowCharacter(id);
        if (InventoryWindows.IsPinnedInventory(id)) id = "inventory";
        if (id is not ("inventory" or "bag" or "limbs" or "loot" or "loot-bag") || shown is not { ItemGrid: { } grid } lead) return;
        bool bagWindow = id is "bag" or "loot-bag";
        string gridSection = bagWindow ? "backpack_content" : "main";
        int columns = bagWindow ? grid.BagWidth : grid.Width, rows = bagWindow ? grid.BagHeight : grid.Height;
        if (bagWindow) player.inventoryLayout.ArrangeContainerGrid(id, controls, columns, rows);
        else if (id == "limbs") player.inventoryLayout.ArrangeLimbSlots(controls, lead);
        else
        {
            player.inventoryLayout.ArrangeEquipmentGrid(controls, grid);
            InventoryLayout.CloseSlots(controls, lead);
        }
        if (id is "inventory" or "loot") player.inventoryLayout.AddInventorySummary(controls, lead, animal: TradeWindows.IsAnimal(lead));
        foreach (var item in grid.Items.Where(i => bagWindow ? i.Section == "backpack_content" : id == "limbs" ? i.Section.EndsWith("_replace", StringComparison.Ordinal) : i.Section != "backpack_content"))
        {
            if (player.inventoryWindows.Lifted(Meitou.Simulation.Items.ItemOwner.Of(lead.Id), item.Index)) continue;
            if (controls.FirstOrDefault(c => c.Name == item.Section) is not { } slot) continue;
            var area = slot.Bounds;
            if (item.Section == gridSection && columns > 0 && rows > 0) area = new(area.X + item.X * area.Width / columns, area.Y + item.Y * area.Height / rows, item.Width * area.Width / columns, item.Height * area.Height / rows);
            else area = new(area.X + item.X * player.InventoryCell, area.Y + item.Y * player.InventoryCell, item.Width * player.InventoryCell, item.Height * player.InventoryCell);
            // The name carries the character: the loot window's cells would otherwise take over the same names' tooltips in the player's window.
            player.itemCells.AddItemCell(controls, $"inventory-item-{lead.Id}-{item.Index}", area, item.Record, item.Material, item.Name, item.Quantity, TooltipRuntime.ItemTip.Of(item, player.tradeWindows.SellTip(lead.Id, item.Index), player.worldMap.MapItemCaption(item.Record)), "InventoryItem", item.Index, slot.Bounds.Intersect(slot.Clip ?? slot.Bounds), charge: item.Charge, colour: item.Colour);
        }
    }
}
