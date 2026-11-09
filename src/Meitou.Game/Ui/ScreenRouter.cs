using System.Globalization;
using Meitou.Data.Ui;
using Meitou.Simulation;

namespace Meitou.Game.Ui;

/// <summary>
/// Routes the faithful screens to the features that own them: each screen's actions, bindings and added content.
/// </summary>
sealed class ScreenRouter(PlayerInterface player)
{
    internal void FaithfulAction(MyGuiEvent e)
    {
        player.InterfaceSounds.SoundForScreen(e);
        if (e.Action.StartsWith("inventory-scroll:", StringComparison.Ordinal))
        {
            player.ScrollBars.ScrollInventory(e.Screen, float.Parse(e.Action[17..], CultureInfo.InvariantCulture));
            return;
        }

        if (e.Action.StartsWith("inventory-scroll-page:", StringComparison.Ordinal))
        {
            player.ScrollBars.ScrollInventoryPage(e.Screen, float.Parse(e.Action[22..], CultureInfo.InvariantCulture));
            return;
        }

        if (e.Screen == "editor") player.CharacterEditor.EditorAction(e);
        else if (e.Screen == "file") player.CharacterEditor.FileDialogAction(e);
        else if (e.Screen == FeedbackPanel.FeedbackScreen) player.FeedbackPanel.FeedbackFormAction(e);
        else if (player.Frontend.HandleFrontendAction(e)) player.UpdateMenuPause();
        else if (e.Screen == "stats" && e.Action == "select") player.StatsWindow.SelectedStat = e.Text;
        else if (e.Screen == "stats" && e.Action.StartsWith("stat:", StringComparison.Ordinal)) player.StatsWindow.SelectedStat = e.Action[5..];
        else if (e.Screen == "build") player.Build.BuildAction(e);
        else if (e.Screen == "leveleditor") player.LevelEditorPanel.LevelEditorAction(e);
        else if (e.Screen == "dialogue") player.DialogueWindow.DialogueAction(e);
        else if (e.Screen == TradeWindows.CharacterTradeScreen) player.TradeWindows.CharacterTradeAction(e);
        else if (e.Screen == "prospect") player.Prospecting.ProspectScreenAction(e);
        else if (e.Screen == "tutorials") player.TutorialWindow.TutorialsWindowAction(e);
        else if (e.Screen == TownPanel.TownScreen) player.TownPanel.TownAction(e);
        else if (e.Screen == "overview")
        {
            player.WorldMap.MapAction(e.Widget);
            player.Production.ResearchAction(e);
            player.Production.CraftAction(e);
            player.SquadWindows.AiAction(e);
            player.SquadWindows.SquadsAction(e);
            player.FactionWindow.FactionAction(e);
        }
        else if (e.Screen is "building" or "bench" or "storage") player.Production.MachineAction(e);
        else if (e.Screen == "loot" && e.Widget == "OpenBagButton") player.TradeWindows.LootBagOpen = !player.TradeWindows.LootBagOpen;
        else if (e.Screen == "loot" && e.Widget == "LimbsButton") player.InventoryWindows.ToggleLimbs(player.TradeWindows.LootBody());
        else if (e.Screen == "trader" && e.Widget == "ArrangeButton")
            player.Say(new Simulation.Trading.ArrangeStock { Tick = player.Session.World.Tick });
        else if (e.Screen is "inventory" or "bag" || InventoryWindows.IsPinnedInventory(e.Screen))
        {
            if (e.Widget == "OpenBagButton") player.InventoryWindows.ToggleBag();
            else if (e.Widget == "LimbsButton") player.InventoryWindows.ToggleLimbs();
            else if (e.Widget == "ArrangeButton" && player.InventoryWindows.WindowCharacter(e.Screen) is { } lead)
                player.Say(new Simulation.Items.ArrangeInventory(lead.Id) { Tick = player.Session.World.Tick });
        }
    }

    internal UiBindings BindFaithfulScreen(string id)
    {
        var b = new UiBindings { TextHeight = c => player.UiPainter?.TextHeight(c, player.Height, player.StandardPixels) ?? 0 };
        var layout = player.FaithfulScreens.Layout(id);
        string screen = id;
        bool pinned = InventoryWindows.IsPinnedInventory(id);
        if (pinned) id = "inventory";   // a pinned window binds as the inventory, for its own character
        var lead = pinned || id == "limbs" ? player.InventoryWindows.WindowCharacter(screen)
            : id is "inventory" or "bag" ? player.InventoryWindows.InventoryOwner()
            : player.Selected().FirstOrDefault();
        foreach (var button in layout.Widgets.Where(w => w.Type == "Button" && !w.Properties.ContainsKey("TemplateEvent")))
            b.Disabled.Add(button.Name);

        if (id == FeedbackPanel.FeedbackScreen)
        {
            player.FeedbackPanel.BindFeedbackScreen(b);
            return b;
        }

        if (player.Frontend.BindFrontend(id, b)) return b;
        if (id == "editor")
        {
            player.CharacterEditor.BindEditor(b);
            return b;
        }

        if (id == "file")
        {
            player.CharacterEditor.BindFileDialog(b);
            return b;
        }
        else if (id == "trader") player.TradeWindows.BindTraderScreen(b);
        else if (id == "dialogue") player.DialogueWindow.BindDialogueScreen(b);
        else if (id == TradeWindows.CharacterTradeScreen) player.TradeWindows.BindCharacterTradeScreen(b);
        else if (id == "prospect") player.Prospecting.BindProspectScreen(b);
        else if (id == "tutorials") player.TutorialWindow.BindTutorialsWindow(b);
        else if (id == TownPanel.TownScreen) player.TownPanel.BindTownScreen(b);
        else if (id == "build") player.Build.BindBuildScreen(b);
        else if (id == "leveleditor") player.LevelEditorPanel.BindLevelEditorScreen(b);
        else if (id is "building" or "bench" or "storage") player.Production.BindMachineScreen(id, b);
        else if (TradeWindows.IsLootScreen(id)) player.TradeWindows.BindLootScreen(id, b);
        else if (id is "inventory" or "stats" or "bag" or "limbs")
        {
            b.Text["Root"] = id == "stats" ? lead?.Name.ToUpperInvariant() ?? "CHARACTER STATS"
                : id == "bag" ? lead?.ItemGrid?.Items.FirstOrDefault(i => i.Section == "backpack_attach")?.Name.ToUpperInvariant() ?? "BACKPACK"
                : id == "limbs" ? "LIMBS" : lead?.Name.ToUpperInvariant() ?? "INVENTORY";
            if (id == "limbs")
            {
                b.Text["lbLeftArm"] = "-ARMS-";
                b.Text["lbLeftLeg"] = "-LEGS-";
            }

            if (id is "inventory" or "bag" or "limbs")
            {
                // Each window binds only what its own layout has: the character window the data panel and the limbs button, the generic (bag) window
                // the capacity box; the limb window has neither.
                if (id == "inventory")
                {
                    b.Properties["datapanel"] = new Dictionary<string, string> { ["SkipCaption"] = "true" };
                    if (!pinned && lead?.ItemGrid is { BagWidth: > 0, BagHeight: > 0 })
                    {
                        b.Disabled.Remove("OpenBagButton");
                        b.Actions["OpenBagButton"] = "OpenBagButton";
                    }
                }

                if (id == "bag")
                {
                    b.Hidden.Add("CapacityText");
                    b.Hidden.Add("TypeText");
                }

                if (id != "limbs")
                    foreach (string name in id == "inventory" ? new[] { "ArrangeButton", "LimbsButton" } : ["ArrangeButton"])
                        BindCaptionButton(b, name, lead, pinned);
            }
            else
            {
                player.StatsWindow.BindStatsGeometry(layout, b);
            }
        }
        else if (id == "overview")
        {
            // The window's caption is the selected tab's name (FUN_14049bf90); the research tab writes its level and rate over it (BindResearchTab).
            var tabs = layout.Find("TabsMain")?.Children.Where(w => w.Type == "TabItem").ToArray() ?? [];
            int shown = player.FaithfulScreens.GetTab("overview", "TabsMain");
            b.Text["Root"] = shown >= 0 && shown < tabs.Length ? Loc.Tr(tabs[shown].Properties.GetValueOrDefault("Caption") ?? "") : "OVERVIEW";
            foreach (string name in new[] { "MapCenterButton", "MapZoomInButton", "MapZoomOutButton" })
            {
                b.Disabled.Remove(name);
                b.Actions[name] = name;
            }

            foreach (var tab in layout.Widgets.Where(w => w.Type == "TabItem"))
                b.Properties[tab.Name] = new Dictionary<string, string> { ["Visible"] = "true" };
            player.FactionWindow.BindFactionTab(b);
            b.Text["MessagesTextBox"] = player.MessageRoller.MessagesText();
            b.Properties["MessagesTextBox"] = new Dictionary<string, string> { ["Static"] = "true", ["WordWrap"] = "true", ["TextAlign"] = "Left Top" };
            player.SquadWindows.BindSquadsTab(b);
            player.Production.BindResearchTab(b);
            player.Production.BindCraftTab(b);
            player.SquadWindows.BindFormation(b);

            // Editor preview markers are unrelated to the current camera and orders.
            b.Hidden.Add("CameraMarker");
            b.Hidden.Add("MovemementMarker");
            b.NativeScroll.Add("MapScrollView");   // the map pans and zooms itself (WorldMap); the canvas is not the layout's 4096 px
        }

        if (id is "inventory" or "bag" or "limbs" or "trader" or "loot" or "loot-bag") player.InventoryLayout.BindInventoryGeometry(id, b);
        return b;
    }

    /// <summary>
    /// The inventory's arrange button (and the limbs button) bound as enabled actions, disabled without a character or when the limb window is open.
    /// </summary>
    void BindCaptionButton(UiBindings b, string name, CharacterSnapshot? lead, bool pinned)
    {
        b.Disabled.Remove(name);
        b.Actions[name] = name;
        if (lead is null || name == "LimbsButton" && (pinned || !InventoryWindows.HasLimbWindow(lead))) b.Disabled.Add(name);
    }

    internal void AddFaithfulContent(string id, List<UiControl> controls)
    {
        if (id == "title")
        {
            if (player.UiMode == UiMode.Meitou) player.TitleMenu.MeitouTitleMenu(controls);
            player.TitleMenu.AddDataProblems(controls);
            return;
        }

        if (id == "stats")
        {
            player.StatsWindow.AddStatsContent(controls);
            return;
        }

        if (id == "new-game")
        {
            player.Frontend.AddStartLines(controls);
            player.AdvancedOptions.AddAdvancedContent(controls);
            return;
        }

        if (id == "message")
        {
            player.Frontend.StyleMessageBox(controls);
            return;
        }

        if (id is "load" or "import" or "save" && player.UiMode == UiMode.Faithful)
        {
            player.Frontend.AddSlotInfo(controls);
            if (id == "import") player.AdvancedOptions.AddAdvancedContent(controls, "import");
            return;
        }

        if (id == "editor")
        {
            player.CharacterEditor.AddEditorContent(controls);
            return;
        }

        if (id == "build")
        {
            player.Build.AddBuildContent(controls);
            return;
        }

        if (id == "dialogue")
        {
            player.DialogueWindow.AddDialogueChoices(controls);
            return;
        }

        if (id == TradeWindows.CharacterTradeScreen)
        {
            player.TradeWindows.AddCharacterTradeRows(controls);
            return;
        }

        if (id == "prospect")
        {
            player.Prospecting.AddProspectContent(controls);
            return;
        }

        if (id is "building" or "bench" or "storage")
        {
            player.Production.AddMachineContent(id, controls);
            return;
        }

        // The bag window has no caption buttons (the vanilla screenshot): OPEN BAG closes it.
        if (id is "bag" or "loot-bag")
            controls.RemoveAll(c => c.Action.StartsWith("mygui:close", StringComparison.Ordinal) || c.Action.StartsWith("mygui:pin", StringComparison.Ordinal));
        player.WorldMap.AddMapContent(id, controls);
        if (id == "overview")
        {
            player.Production.AddResearchContent(controls);
            player.Production.AddCraftContent(controls);
            player.SquadWindows.AddAiContent(controls);
            player.SquadWindows.AddSquadsContent(controls);
            player.FactionWindow.AddFactionContent(controls);
        }

        if (id == "trader")
        {
            player.TradeWindows.ArrangeTraderWindow(controls);
            player.TradeWindows.AddTraderContent(controls);
            return;
        }

        // The loot windows show the open loot's body as the character windows show the lead; a pinned window its own character.
        var shown = player.InventoryWindows.WindowCharacter(id);
        if (InventoryWindows.IsPinnedInventory(id)) id = "inventory";
        if (id is not ("inventory" or "bag" or "limbs" or "loot" or "loot-bag") || shown is not { ItemGrid: { } grid } lead) return;

        bool bagWindow = id is "bag" or "loot-bag";
        string gridSection = bagWindow ? "backpack_content" : "main";
        int columns = bagWindow ? grid.BagWidth : grid.Width, rows = bagWindow ? grid.BagHeight : grid.Height;
        if (bagWindow) player.InventoryLayout.ArrangeContainerGrid(id, controls, columns, rows);
        else if (id == "limbs") player.InventoryLayout.ArrangeLimbSlots(controls, lead);
        else
        {
            player.InventoryLayout.ArrangeEquipmentGrid(controls, grid);
            InventoryLayout.CloseSlots(controls, lead);
        }

        if (id is "inventory" or "loot") player.InventoryLayout.AddInventorySummary(controls, lead, animal: TradeWindows.IsAnimal(lead));
        foreach (var item in grid.Items.Where(i => ShownInWindow(i, bagWindow, id)))
        {
            if (player.InventoryWindows.Lifted(Simulation.Items.ItemOwner.Of(lead.Id), item.Index)) continue;
            if (controls.FirstOrDefault(c => c.Name == item.Section) is not { } slot) continue;

            var area = slot.Bounds;
            if (item.Section == gridSection && columns > 0 && rows > 0)
                area = new(area.X + item.X * area.Width / columns, area.Y + item.Y * area.Height / rows,
                    item.Width * area.Width / columns, item.Height * area.Height / rows);
            else
                area = new(area.X + item.X * player.InventoryCell, area.Y + item.Y * player.InventoryCell,
                    item.Width * player.InventoryCell, item.Height * player.InventoryCell);

            // The name carries the character: the loot window's cells would otherwise take over the same names' tooltips in the player's window.
            string name = $"inventory-item-{lead.Id}-{item.Index}";
            var tip = TooltipRuntime.ItemTip.Of(item, player.TradeWindows.SellTip(lead.Id, item.Index), player.WorldMap.MapItemCaption(item.Record));
            var clip = slot.Bounds.Intersect(slot.Clip ?? slot.Bounds);
            player.ItemCells.AddItemCell(controls, name, area, item.Record, item.Material, item.Name, item.Quantity, tip, "InventoryItem",
                item.Index, clip, charge: item.Charge, colour: item.Colour);
        }
    }

    /// <summary>
    /// Whether an item shows in a window: a bag window its backpack's contents, the limbs window the limb slots, the others the rest.
    /// </summary>
    static bool ShownInWindow(InventoryItemView i, bool bagWindow, string id) =>
        bagWindow ? i.Section == "backpack_content"
        : id == "limbs" ? i.Section.EndsWith("_replace", StringComparison.Ordinal)
        : i.Section != "backpack_content";
}