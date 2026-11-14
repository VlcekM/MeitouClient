using System.Globalization;
using System.Numerics;
using Meitou.Data.Ui;
using Meitou.Simulation;
using Meitou.Simulation.Items;

namespace Meitou.Game.Ui;

/// <summary>
/// The inventory windows' geometry: where they open beside each other, their grids and the data panel's summary lines.
/// </summary>
sealed class InventoryLayout(PlayerInterface player)
{
    #region Window placement

    /// <summary>
    /// The main inventory has an opening position to retain when closed and reopened.
    /// </summary>
    bool _inventoryPlaced;

    /// <summary>
    /// A window's geometry at the inventory scale: a bag window sized to its bag, the trader's own.
    /// </summary>
    internal void BindInventoryGeometry(string id, UiBindings b)
    {
        b.TemplateScale = player.UiScale;
        b.WindowPosition = new Vector2(InventoryPlacement.Margin(player.Width, player.Height));
        if (InventoryWindows.ScreenKind(id) is "bag" or "loot-bag")
        {
            var owner = player.InventoryWindows.WindowCharacter(id);
            if (owner?.ItemGrid is { } bag)
            {
                BindContainerGeometry(id, b, bag.BagWidth, bag.BagHeight, fixedTop: true);
            }
        }

        if (InventoryWindows.ScreenKind(id) is "inventory" or "loot"
            && player.InventoryWindows.WindowCharacter(id)?.ItemGrid is { } grid)
        {
            var geometry = new InventoryGeometry(player.FaithfulScreens.Layout(id), player.Width, player.Height,
                player.InterfaceScale, player.UiScale);
            if (!TradeWindows.IsAnimal(player.InventoryWindows.WindowCharacter(id)!))
                geometry.FitCharacter(new(grid.Width * player.InventoryCell, grid.Height * player.InventoryCellHeight));
            geometry.Bind(b);
        }

        if (id == "trader") player.TradeWindows.BindTraderGeometry(b);
    }

    /// <summary>
    /// Places the bag window: in the window row while a loot window is open, else beside the inventory on its left.
    /// </summary>
    internal void PlaceBagWindow()
    {
        if (player.FaithfulScreens.IsOpen("loot"))
            PlaceWindowRow();
        else
            PlaceInventoryBeside("bag", left: true);
    }

    /// <summary>
    /// The open windows' bounds by screen, as laid out now.
    /// </summary>
    Dictionary<string, UiRect> WindowRoots()
    {
        var roots = new Dictionary<string, UiRect>(StringComparer.Ordinal);
        var frames = player.FaithfulScreens.Arrange(player.Width, player.Height, player.ScreenRouter.BindFaithfulScreen,
            player.ScreenRouter.AddFaithfulContent);
        foreach (var f in frames)
        {
            if (f.Controls.FirstOrDefault(c => c.Type == "Window") is { } window) roots[f.Id] = window.Bounds;
        }

        return roots;
    }

    /// <summary>
    /// Places a newly opened independent character inventory after the other inventory windows, using another row when necessary.
    /// </summary>
    internal void PlaceCharacterWindow(bool newCharacter = false)
    {
        if (_inventoryPlaced && !newCharacter) return;

        var roots = WindowRoots();
        if (!roots.TryGetValue("inventory", out var own)) return;

        _inventoryPlaced = true;

        var occupied = roots.Where(p => p.Key != "inventory" && InventoryWindows.ScreenKind(p.Key) is "inventory" or "bag")
            .Select(p => p.Value).ToArray();
        var position = InventoryPlacement.FindSpace(player.Width, player.Height, own.Width, occupied);
        foreach (string machine in new[] { "building", "bench", "storage" })
        {
            if (roots.TryGetValue(machine, out var r)) position.X = Math.Max(position.X, r.X + r.Width);
        }

        player.FaithfulScreens.SetOffset("inventory", player.FaithfulScreens.GetOffset("inventory") + position - new Vector2(own.X, own.Y));
    }

    /// <summary>
    /// Places linked loot windows edge to edge at the opening margin: limbs, own bag, own inventory, looted bag, looted character.
    /// </summary>
    internal void PlaceWindowRow()
    {
        var roots = WindowRoots();
        var row = new[] { "limbs", "bag", "inventory", "loot-bag", "loot" }.Where(roots.ContainsKey).ToArray();
        float margin = InventoryPlacement.Margin(player.Width, player.Height);
        float before = row.TakeWhile(s => s != "inventory").Sum(s => roots[s].Width);
        var own = roots.GetValueOrDefault("inventory", new(margin, margin, 0, 0));
        float x = Math.Max(margin, own.X - before), y = own.Y;
        foreach (string screen in row)
        {
            var r = roots[screen];
            var move = new Vector2(x - r.X, (screen == "limbs" ? margin : y) - r.Y);
            player.FaithfulScreens.SetOffset(screen, player.FaithfulScreens.GetOffset(screen) + move);
            x += r.Width;
        }
    }

    /// <summary>
    /// The limbs window's place (<c>FUN_1406e4960</c>, <b>Verified</b>): its right edge on the left edge of the inventory (of its bag window when
    /// that is open), its top at the margin (5% of the smaller screen side); when that is off the screen, at the left edge with the inventory and its
    /// bag pushed right of it. Not moved with the inventory afterwards. With a loot window open it joins the window row instead.
    /// </summary>
    internal void PlaceLimbsWindow()
    {
        if (player.FaithfulScreens.IsOpen("loot"))
        {
            PlaceWindowRow();
            return;
        }

        var roots = WindowRoots();
        UiRect? Root(string screen) => roots.TryGetValue(screen, out var r) ? r : null;
        if (Root("limbs") is not { } limbs || (Root("bag") ?? Root("inventory")) is not { } anchor) return;

        float margin = 0.05f * Math.Min(player.Width, player.Height);
        float x = anchor.X - limbs.Width;
        if (x < 0)
        {
            foreach (string screen in new[] { "inventory", "bag" })
            {
                if (player.FaithfulScreens.IsOpen(screen))
                    player.FaithfulScreens.SetOffset(screen, player.FaithfulScreens.GetOffset(screen) + new Vector2(-x, 0));
            }

            x = 0;
        }

        player.FaithfulScreens.SetOffset("limbs", player.FaithfulScreens.GetOffset("limbs") + new Vector2(x - limbs.X, margin - limbs.Y));
    }

    /// <summary>
    /// A machine or container window's place (<b>Observed</b> on the original's 2026-10-26 screenshot of a mined iron rock): at the screen's left
    /// edge, vertically centred; an open inventory (with its bag) under it moves right of it.
    /// </summary>
    internal void PlaceMachineWindow(string id, bool linked = false)
    {
        if (linked)
        {
            PlaceInventoryBeside(id, left: false);
            return;
        }

        var roots = WindowRoots();
        if (!roots.TryGetValue(id, out var window)) return;

        var move = new Vector2(-window.X, (player.Height - window.Height) / 2 - window.Y);
        player.FaithfulScreens.SetOffset(id, player.FaithfulScreens.GetOffset(id) + move);
        float left = new[] { "bag", "inventory" }.Where(roots.ContainsKey).Select(s => roots[s].X).DefaultIfEmpty(float.MaxValue).Min();
        float push = window.Width - left;
        if (push <= 0) return;

        foreach (string screen in new[] { "inventory", "bag" })
        {
            if (roots.ContainsKey(screen)) player.FaithfulScreens.SetOffset(screen, player.FaithfulScreens.GetOffset(screen) + new Vector2(push, 0));
        }
    }

    /// <summary>
    /// Puts window <paramref name="id"/> beside <paramref name="anchor"/>'s, moving the anchor in when the pair would not fit the screen.
    /// </summary>
    internal void PlaceInventoryBeside(string id, bool left, string anchor = "inventory")
    {
        var roots = WindowRoots();
        if (!roots.TryGetValue(id, out var other) || !roots.TryGetValue(anchor, out var own)) return;

        float x = left ? own.X - other.Width : own.X + own.Width;

        // Make room for the pair without changing either frame's size.
        if (x < 0 || x + other.Width > player.Width)
        {
            float ownX = left
                ? Math.Min(player.Width - own.Width, other.Width)
                : Math.Max(0, player.Width - other.Width - own.Width);
            player.FaithfulScreens.SetOffset(anchor, player.FaithfulScreens.GetOffset(anchor) + new Vector2(ownX - own.X, 0));
            x = left ? ownX - other.Width : ownX + own.Width;
        }

        player.FaithfulScreens.SetOffset(id, player.FaithfulScreens.GetOffset(id) + new Vector2(x - other.X, own.Y - other.Y));
    }

    #endregion

    #region Equipment

    /// <summary>
    /// The four prosthetic slots (<c>FUN_1400d04d0</c>, <c>FUN_1401534f0</c>, <b>Verified</b>): arms 2 x 6 cells, legs 2 x 7; a slot is enabled only
    /// for a stump or a fitted prosthetic, else drawn with the skin's disabled tile and refusing drops (the simulation's check).
    /// </summary>
    internal void ArrangeLimbSlots(List<UiControl> controls, CharacterSnapshot lead)
    {
        for (int k = 0; k < 4; k++)
        {
            string section = k switch { 0 => "left_arm_replace", 1 => "right_arm_replace", 2 => "left_leg_replace", _ => "right_leg_replace" };
            int index = controls.FindIndex(c => c.Name == section);
            if (index < 0) continue;

            var slot = controls[index];
            int state = (lead.LimbBits >> (2 * k)) & 3;
            bool open = state is (int)Simulation.Bodies.LimbState.Stump or (int)Simulation.Bodies.LimbState.Replaced;
            controls[index] = slot with
            {
                Bounds = slot.Bounds with { Width = 2 * player.InventoryCell, Height = (k < 2 ? 6 : 7) * player.InventoryCellHeight },
                Properties = new Dictionary<string, string>(slot.Properties)
                {
                    ["TileSize"] = FormattableString.Invariant($"{player.InventoryCell} {player.InventoryCellHeight}"),
                    ["State"] = open ? "normal" : "disabled",
                },
            };
        }
    }

    /// <summary>
    /// The equipment slots disabled for the character (<see cref="CharacterSnapshot.ClosedSlots"/>): the skin's disabled (red) tile, and drops find
    /// no section there.
    /// </summary>
    internal static void CloseSlots(List<UiControl> controls, CharacterSnapshot lead)
    {
        foreach (string section in lead.ClosedSlots)
        {
            int index = controls.FindIndex(c => c.Name == section);
            if (index < 0) continue;

            var closed = new Dictionary<string, string>(controls[index].Properties) { ["State"] = "disabled" };
            controls[index] = controls[index] with { Properties = closed };
        }
    }

    /// <summary>
    /// Sizes the equipment slots and the main grid in cells, with their tile size.
    /// </summary>
    internal void ArrangeEquipmentGrid(List<UiControl> controls, InventoryView grid)
    {
        foreach (string section in new[] { "main", "backpack_attach", "back", "hip", "shirt", "head", "legs", "boots", "armour", "belt" })
        {
            int index = controls.FindIndex(c => c.Name == section);
            if (index < 0) continue;

            var slot = controls[index];
            var size = InventoryCommands.SectionSize(section);
            int columns = section == "main" ? grid.Width : size.Width;
            int rows = section == "main" ? grid.Height : size.Height;
            var bounds = slot.Bounds with { Width = columns * player.InventoryCell, Height = rows * player.InventoryCellHeight };
            controls[index] = slot with
            {
                Bounds = bounds,
                Properties = new Dictionary<string, string>(slot.Properties)
                {
                    ["TileSize"] = FormattableString.Invariant($"{player.InventoryCell} {player.InventoryCellHeight}"),
                    ["State"] = "normal",
                },
            };
        }
    }

    /// <summary>
    /// Adds the mods' equipment slots (<see cref="ModSlots"/>, docs/modding.md "Equipment slots") to a character window that has the belt slot: each
    /// a copy of the belt slot's widget, named by its section, sized in cells and placed at its offset from the window's top-left corner, clipped
    /// to the window.
    /// </summary>
    internal void AddModSlots(List<UiControl> controls)
    {
        if (ModSlots.All.Count == 0) return;
        int belt = controls.FindIndex(c => c.Name == "belt");
        if (belt < 0 || controls.FirstOrDefault(c => c.Type == "Window") is not { } window) return;

        var template = controls[belt];
        float cell = player.InventoryCell;
        string tile = FormattableString.Invariant($"{cell} {player.InventoryCellHeight}");

        // The belt's clip is its own panel's; a mod slot may lie anywhere in the window.
        var clip = window.Clip is { } c ? window.Bounds.Intersect(c) : window.Bounds;
        foreach (var slot in ModSlots.All)
        {
            var bounds = new UiRect(window.Bounds.X + slot.X * player.UiScale, window.Bounds.Y + slot.Y * player.UiScale, slot.Width * cell,
                slot.Height * player.InventoryCellHeight);
            var properties = new Dictionary<string, string>(template.Properties) { ["TileSize"] = tile, ["State"] = "normal" };
            controls.Insert(++belt, template with { Name = slot.Section, Bounds = bounds, Clip = clip, Properties = properties });
        }
    }

    /// <summary>
    /// The player's own animal's inventory window ends where its "-INVENTORY-" label would start, with no main grid, so its death items stay out of
    /// sight (<b>Observed</b>, original: a bought Garru with meat and skins in its save, and a dog). The loot window keeps the grid.
    /// </summary>
    internal static void TrimAnimalWindow(List<UiControl> controls)
    {
        var window = controls.FirstOrDefault(c => c.Type == "Window");
        var label = controls.FirstOrDefault(c => c.Name == "MainSectionName");
        if (window is null || label is null) return;

        // The frame's pieces under the cut move up with the bottom edge; those crossing it (the sides, the panel, the window) shorten.
        float cut = label.Bounds.Y, shrink = window.Bounds.Y + window.Bounds.Height - cut;
        controls.RemoveAll(c => c.Name is "main" or "MainSectionName");
        for (int i = 0; i < controls.Count; i++)
        {
            var r = controls[i].Bounds;
            if (r.Y >= cut)
                controls[i] = controls[i] with { Bounds = r with { Y = r.Y - shrink } };
            else if (r.Y + r.Height > cut)
                controls[i] = controls[i] with { Bounds = r with { Height = Math.Max(0, r.Height - shrink) } };
        }
    }

    #endregion

    #region Grids

    /// <summary>
    /// Sizes a storage, research or backpack window from its original layout and the inventory's dimensions.
    /// </summary>
    internal void BindContainerGeometry(string id, UiBindings bindings, int columns, int rows, bool fixedTop = false)
    {
        var geometry = new InventoryGeometry(player.FaithfulScreens.Layout(id), player.Width, player.Height,
            player.InterfaceScale, player.UiScale);
        geometry.FitContainer(new(columns * player.InventoryCell, rows * player.InventoryCellHeight), fixedTop);
        geometry.Bind(bindings);
    }

    /// <summary>
    /// Applies the inventory cell tile to the grid already positioned inside its frame. Generic inventories have no artificial height cap or scroll.
    /// </summary>
    internal void ArrangeContainerGrid(string id, List<UiControl> controls)
    {
        player.ScrollBars.InventoryScroll.Remove(id);
        player.ScrollBars.InventoryViews.Remove(id);
        player.ScrollBars.InventoryTracks.Remove(id);
        int index = controls.FindIndex(c => c.Name == "backpack_content");
        if (index < 0) return;

        var slot = controls[index];
        controls[index] = slot with
        {
            Properties = new Dictionary<string, string>(slot.Properties)
            {
                ["TileSize"] = FormattableString.Invariant($"{player.InventoryCell} {player.InventoryCellHeight}"),
            },
        };
    }

    #endregion

    #region Data panel

    /// <summary>
    /// The data panel's lines (<see cref="InventorySummaryLines"/>): the player's own animals show the Disguise and slave rows too
    /// (<b>Observed</b>, original: a Garru, a Pack Beast, an Iron Spider and a Bonedog of the player's squad).
    /// </summary>
    internal void AddInventorySummary(List<UiControl> controls, CharacterSnapshot lead)
    {
        string weight = ((int)(lead.ItemGrid?.TotalWeight ?? 0)).ToString(CultureInfo.InvariantCulture);   // truncated, as the original prints it
        if (lead.ItemGrid?.CarryCapacity is { } capacity) weight += "/" + ((int)capacity).ToString(CultureInfo.InvariantCulture);

        var (burdenWord, burdenColour) = BurdenText(lead.Hud?.Encumbrance ?? lead.ItemGrid?.Burden ?? 1);
        player.AddLines(controls, "datapanel", InventorySummaryLines(lead, weight, burdenWord, burdenColour), 20 * player.UiScale);
    }

    /// <summary>
    /// The data panel's lines (<c>FUN_1405d4260</c> mask 0xf, called through Character slot 40 for the window's owner, <b>Verified</b> by
    /// disassembly, docs/game/ui-screens.md): Total Weight and the burden row for every character; for a character of the player's faction also
    /// Disguise (no disguise: "None" and a "-" value on a blank-labelled row; the disguise percent is not simulated), Slave of (free: "-" greyed; a
    /// slave or escaped slave: its owner faction's name in Bad, no line without an owner) and Looks like slave (free: "-" greyed; else the level in
    /// percent, Bad above 10 %). A character of another faction has only the weight rows (and, for a trader, its money lines, not built).
    /// </summary>
    List<PlayerInterface.HudLine> InventorySummaryLines(CharacterSnapshot lead, string weight, string burdenWord, string? burdenColour)
    {
        var lines = new List<PlayerInterface.HudLine> { new(Loc.Tr("Total Weight:"), weight), new("", burdenWord, ValueColour: burdenColour) };
        if (!lead.IsPlayer) return lines;

        lines.AddRange(DisguiseLines(lead.Disguise));
        if (lead.Slave is not { } slave)
        {
            lines.Add(new(Loc.Tr("Slave of:"), "-", PlayerInterface.GreyColour, PlayerInterface.GreyColour, Loc.Tr("You are a free person.")));
            lines.Add(new(Loc.Tr("Looks like slave:"), "-", PlayerInterface.GreyColour, PlayerInterface.GreyColour, Loc.Tr("You are no slave.")));
            return lines;
        }

        if (slave.Owner.Length > 0)
            lines.Add(new(Loc.Tr("Slave of:"), slave.Owner, ValueColour: PlayerInterface.BadColour, Tooltip: Loc.Tr("The faction that owns you.")));

        string? looksColour = slave.Looks > 0.1f ? PlayerInterface.BadColour : null;
        string looksTip = Loc.Tr("How much you look like an escaped slave. Based on how you are dressed/undressed. "
            + "Wearing shackles and rags is a big giveaway. Try to avoid hanging around too long near people who may recognise you.");
        lines.Add(new(Loc.Tr("Looks like slave:"), Loc.Tr("{1,num}%", (int)(slave.Looks * 100)), ValueColour: looksColour, Tooltip: looksTip));
        return lines;
    }

    /// <summary>
    /// The Disguise row and the blank-labelled row under it (<c>FUN_1405d4260</c>, <b>Verified</b> strings, docs/game/ui-screens.md "Which lines"):
    /// the uniform faction's name (Main), else "None"; the tooltip "Not currently in disguise." at level 0, else the rows Clothing disguise (level %,
    /// Good), In secure location -25%, Same faction -40%, Slave owner recognition -50%, Someone knows me personally -80% (Bad, each multiplying the
    /// total), Disguise blown (seconds, Bad, total 0) and Total (Main). The second row: "-" at level 0, else the level in percent (Main) or Blown!
    /// (Bad) while blown.
    /// </summary>
    static IEnumerable<PlayerInterface.HudLine> DisguiseLines(DisguiseView? disguise)
    {
        if (disguise is not { Level: > 0 } d)
        {
            yield return new(Loc.Tr("Disguise:"), Loc.Tr("None"), Tooltip: Loc.Tr("Not currently in disguise."));
            yield return new("    ", "-");
            yield break;
        }

        float total = d.Level * 100;
        string clothing = ProductionWindows.GoodTag + Loc.Tr("{1,num}", (int)total) + "%";
        var rows = new List<(string, string)> { (Loc.Tr("Clothing disguise:"), clothing) };
        void Factor(bool on, string label, string text, float mult)
        {
            if (!on) return;

            total *= mult;
            rows.Add((Loc.Tr(label), ProductionWindows.BadTag + text));
        }

        Factor(d.Secure, "In secure location:", "-25%", 0.75f);
        Factor(d.SameFaction, "Same faction:", "-40%", 0.6f);
        Factor(d.SlaveOwner, "Slave owner recognition:", "-50%", 0.5f);
        Factor(d.KnowsMe, "Someone knows me personally:", "-80%", 0.2f);
        if (d.Blown > 0)
        {
            total = 0;
            int secs = (int)d.Blown;
            rows.Add((Loc.Tr("Disguise blown:"), ProductionWindows.BadTag + Loc.Tr(secs == 1 ? "{1,num} sec" : "{1,num} secs", secs)));
        }

        rows.Add((Loc.Tr("Total:"), $"{TooltipRuntime.MainTag}{Loc.Tr("{1,num}", (int)total)}%"));
        yield return new(Loc.Tr("Disguise:"), d.Faction.Length > 0 ? d.Faction : Loc.Tr("None"),
            ValueColour: d.Faction.Length > 0 ? PlayerInterface.MainColour : null, TooltipRows: rows);
        yield return d.Blown > 0
            ? new("    ", Loc.Tr("Blown!"), ValueColour: PlayerInterface.BadColour)
            : new("    ", Loc.Tr("{1,num}%", (int)(d.Level * 100)), ValueColour: PlayerInterface.MainColour);
    }

    /// <summary>
    /// The burden row under Total Weight (<c>FUN_1405d4260</c>, <b>Verified</b> by decompile): by the carry speed factor (1 unburdened), at least
    /// 0.95 "-Weightless", 0.75 "-Lightweight", 0.5 "-Moderate" (Main colour), 0.1 "-Heavy", below "-Overloaded" (both Bad colour).
    /// </summary>
    internal static (string Text, string? Colour) BurdenText(float factor) => factor switch
    {
        >= 0.95f => (Burden("Weightless"), null),
        >= 0.75f => (Burden("Lightweight"), null),
        >= 0.5f => (Burden("Moderate"), PlayerInterface.MainColour),
        >= 0.1f => (Burden("Heavy"), PlayerInterface.BadColour),
        _ => (Burden("Overloaded"), PlayerInterface.BadColour),
    };

    /// <summary>
    /// A burden word with the original's leading dash, the dash being the panel's and the word the catalog's ("Weightless" is a msgid).
    /// </summary>
    static string Burden(string word) => "-" + Loc.Tr(word);

    #endregion
}
