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

    internal void BindInventoryGeometry(string id, UiBindings b)
    {
        b.TemplateScale = player.InventoryScale;
        if (id is "bag" or "loot-bag")
        {
            var owner = id == "bag" ? player.InventoryWindows.InventoryOwner() : player.TradeWindows.LootBody();
            if (owner?.ItemGrid is { } bag)
            {
                b.Positions["Root"] = new(0, 0, bag.BagWidth * player.InventoryCell + 40 * player.InventoryScale,
                    Math.Min(player.Height * 0.8f, bag.BagHeight * player.InventoryCell + 108 * player.InventoryScale));
            }
        }

        if (id == "trader") player.TradeWindows.BindTraderGeometry(b);
    }

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
        foreach (var f in player.FaithfulScreens.Arrange(player.Width, player.Height, player.ScreenRouter.BindFaithfulScreen, player.ScreenRouter.AddFaithfulContent))
        {
            if (f.Controls.FirstOrDefault(c => c.Type == "Window") is { } window) roots[f.Id] = window.Bounds;
        }

        return roots;
    }

    /// <summary>
    /// Lays the open character, loot and bag windows out in one row, left to right: the limbs window, the character's bag, the character, the looted or traded
    /// character, its bag. The row is centred with the character's top (the limbs window keeps its margin top), so no window slides over another when the row
    /// is wider than the pair that placed them. A row wider than the screen spans it, its windows overlapping evenly rather than the host stacking the last
    /// ones on the right edge.
    /// </summary>
    internal void PlaceWindowRow()
    {
        var roots = WindowRoots();
        var row = new[] { "limbs", "bag", "inventory", "loot", "loot-bag" }.Where(roots.ContainsKey).ToArray();
        if (row.Length < 2) return;

        float gap = 6 * player.InventoryScale;
        float total = row.Sum(s => roots[s].Width) + gap * (row.Length - 1);
        if (total > player.Width)
        {
            gap -= (total - player.Width) / (row.Length - 1);
            total = player.Width;
        }

        float x = (player.Width - total) / 2;
        float y = (roots.TryGetValue("inventory", out var lead) ? lead : roots[row[0]]).Y;
        float margin = 0.05f * Math.Min(player.Width, player.Height);
        foreach (string screen in row)
        {
            var r = roots[screen];
            player.FaithfulScreens.SetOffset(screen, player.FaithfulScreens.GetOffset(screen) + new Vector2(x - r.X, (screen == "limbs" ? margin : y) - r.Y));
            x += r.Width + gap;
        }
    }

    /// <summary>
    /// The limbs window's place (<c>FUN_1406e4960</c>, <b>Verified</b>): its right edge on the left edge of the inventory (of its bag window when that is open),
    /// its top at the margin (5% of the smaller screen side); when that is off the screen, at the left edge with the inventory and its bag pushed right of it.
    /// Not moved with the inventory afterwards. With a loot window open it joins the window row instead.
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
                if (player.FaithfulScreens.IsOpen(screen)) player.FaithfulScreens.SetOffset(screen, player.FaithfulScreens.GetOffset(screen) + new Vector2(-x, 0));
            }

            x = 0;
        }

        player.FaithfulScreens.SetOffset("limbs", player.FaithfulScreens.GetOffset("limbs") + new Vector2(x - limbs.X, margin - limbs.Y));
    }

    /// <summary>
    /// A machine or container window's place (<b>Observed</b> on the original's 2026-10-26 screenshot of a mined iron rock): at the screen's left edge,
    /// vertically centred; an open inventory (with its bag) under it moves right of it.
    /// </summary>
    internal void PlaceMachineWindow(string id)
    {
        var roots = WindowRoots();
        if (!roots.TryGetValue(id, out var window)) return;

        player.FaithfulScreens.SetOffset(id, player.FaithfulScreens.GetOffset(id) + new Vector2(-window.X, (player.Height - window.Height) / 2 - window.Y));
        float left = new[] { "bag", "inventory" }.Where(roots.ContainsKey).Select(s => roots[s].X).DefaultIfEmpty(float.MaxValue).Min();
        float push = window.Width + 6 * player.InventoryScale - left;
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

        float gap = 6 * player.InventoryScale;
        float x = left ? own.X - other.Width - gap : own.X + own.Width + gap;

        // Make room for the pair before the screen host clamps either window to the viewport.
        if (x < 0 || x + other.Width > player.Width)
        {
            float ownX = left
                ? Math.Min(player.Width - own.Width, other.Width + gap)
                : Math.Max(0, player.Width - other.Width - gap - own.Width);
            player.FaithfulScreens.SetOffset(anchor, player.FaithfulScreens.GetOffset(anchor) + new Vector2(ownX - own.X, 0));
            x = left ? ownX - other.Width - gap : ownX + own.Width + gap;
        }

        player.FaithfulScreens.SetOffset(id, player.FaithfulScreens.GetOffset(id) + new Vector2(x - other.X, own.Y - other.Y));
    }

    #endregion

    #region Equipment

    /// <summary>
    /// The four prosthetic slots (<c>FUN_1400d04d0</c>, <c>FUN_1401534f0</c>, <b>Verified</b>): arms 2 x 6 cells, legs 2 x 7; a slot is enabled only for a stump
    /// or a fitted prosthetic, else drawn with the skin's disabled tile and refusing drops (the simulation's check).
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
                Bounds = slot.Bounds with { Width = 2 * player.InventoryCell, Height = (k < 2 ? 6 : 7) * player.InventoryCell },
                Properties = new Dictionary<string, string>(slot.Properties)
                {
                    ["TileSize"] = FormattableString.Invariant($"{player.InventoryCell} {player.InventoryCell}"),
                    ["State"] = open ? "normal" : "disabled",
                },
            };
        }
    }

    /// <summary>
    /// The equipment slots disabled for the character (<see cref="CharacterSnapshot.ClosedSlots"/>): the skin's disabled (red) tile, and drops find no section
    /// there.
    /// </summary>
    internal static void CloseSlots(List<UiControl> controls, CharacterSnapshot lead)
    {
        foreach (string section in lead.ClosedSlots)
        {
            int index = controls.FindIndex(c => c.Name == section);
            if (index < 0) continue;

            controls[index] = controls[index] with { Properties = new Dictionary<string, string>(controls[index].Properties) { ["State"] = "disabled" } };
        }
    }

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
            var bounds = slot.Bounds with { Width = columns * player.InventoryCell, Height = rows * player.InventoryCell };
            controls[index] = slot with
            {
                Bounds = bounds,
                Properties = new Dictionary<string, string>(slot.Properties)
                {
                    ["TileSize"] = FormattableString.Invariant($"{player.InventoryCell} {player.InventoryCell}"),
                    ["State"] = "normal",
                },
            };
        }
    }

    #endregion

    #region Grids

    /// <summary>
    /// The grid is laid in the window with <paramref name="above"/> (default 50) layout pixels over it and <paramref name="below"/> (default 58) under it.
    /// </summary>
    internal void ArrangeContainerGrid(string id, List<UiControl> controls, int columns, int rows, float above = 50, float below = 58)
    {
        int index = controls.FindIndex(c => c.Name == "backpack_content");
        var root = controls.FirstOrDefault(c => c.Type == "Window");
        if (index < 0 || root is null) return;

        float scale = player.InventoryScale;
        float cell = player.InventoryCell;
        var r = root.Bounds;
        float top = above * scale;
        var view = new UiRect(r.X + 18 * scale, r.Y + top, Math.Max(0, r.Width - 40 * scale), Math.Max(0, r.Height - top - below * scale));
        float maximum = Math.Max(0, rows * cell - view.Height);
        if (maximum < 0.01f) maximum = 0;
        float scroll = Math.Clamp(player.ScrollBars.InventoryScroll.GetValueOrDefault(id), 0, maximum);
        player.ScrollBars.InventoryScroll[id] = scroll;
        player.ScrollBars.InventoryViews[id] = (view, maximum);

        var slot = controls[index];
        controls[index] = slot with
        {
            Bounds = new(view.X, view.Y - scroll, columns * cell, rows * cell),
            Clip = view.Intersect(slot.Clip ?? view),
            Properties = new Dictionary<string, string>(slot.Properties) { ["TileSize"] = FormattableString.Invariant($"{cell} {cell}") },
        };
        int arrange = controls.FindIndex(c => c.Name == "ArrangeButton");
        if (arrange >= 0)
        {
            controls[arrange] = controls[arrange] with { Bounds = new(r.X + (r.Width - 110 * scale) / 2, r.Y + r.Height - 44 * scale, 110 * scale, 32 * scale) };
        }

        if (maximum <= 0)
        {
            player.ScrollBars.InventoryTracks.Remove(id);
            return;
        }

        var track = new UiRect(r.X + r.Width - 22 * scale, view.Y, 17 * scale, view.Height);
        player.ScrollBars.AddScrollBar(id, controls, track, view.Height, rows * cell, scroll, maximum, root.Clip);
    }

    #endregion

    #region Data panel

    /// <summary>
    /// The data panel's lines; an animal's window shows only the weight and burden rows (the original's animal inventory, observed).
    /// </summary>
    internal void AddInventorySummary(List<UiControl> controls, CharacterSnapshot lead, bool animal = false)
    {
        string weight = ((int)(lead.ItemGrid?.TotalWeight ?? 0)).ToString(CultureInfo.InvariantCulture);   // truncated, as the original prints it
        if (lead.ItemGrid?.CarryCapacity is { } capacity) weight += "/" + ((int)capacity).ToString(CultureInfo.InvariantCulture);

        var (burdenWord, burdenColour) = BurdenText(lead.Hud?.Encumbrance ?? lead.ItemGrid?.Burden ?? 1);
        player.AddLines(controls, "datapanel", InventorySummaryLines(lead, weight, burdenWord, burdenColour, animal), 20 * player.InventoryScale);
    }

    /// <summary>
    /// The data panel's lines (<c>FUN_1405d4260</c> mask 0xf, called through Character slot 40 for the window's owner, <b>Verified</b> by disassembly,
    /// docs/game/ui-screens.md): Total Weight and the burden row for every character; for a character of the player's faction also Disguise (no disguise:
    /// "None" and a "-" value on a blank-labelled row; the disguise percent is not simulated), Slave of (free: "-" greyed; a slave or escaped slave: its owner
    /// faction's name in Bad, no line without an owner) and Looks like slave (free: "-" greyed; else the level in percent, Bad above 10 %). A character of
    /// another faction has only the weight rows (and, for a trader, its money lines, not built).
    /// </summary>
    List<PlayerInterface.HudLine> InventorySummaryLines(CharacterSnapshot lead, string weight, string burdenWord, string? burdenColour, bool animal)
    {
        var lines = new List<PlayerInterface.HudLine> { new(Loc.Tr("Total Weight:"), weight), new("", burdenWord, ValueColour: burdenColour) };
        if (animal || !lead.IsPlayer) return lines;

        lines.AddRange(DisguiseLines(lead.Disguise));
        if (lead.Slave is not { } slave)
        {
            lines.Add(new(Loc.Tr("Slave of:"), "-", PlayerInterface.GreyColour, PlayerInterface.GreyColour, Loc.Tr("You are a free person.")));
            lines.Add(new(Loc.Tr("Looks like slave:"), "-", PlayerInterface.GreyColour, PlayerInterface.GreyColour, Loc.Tr("You are no slave.")));
            return lines;
        }

        if (slave.Owner.Length > 0)
            lines.Add(new(Loc.Tr("Slave of:"), slave.Owner, ValueColour: PlayerInterface.BadColour, Tooltip: Loc.Tr("The faction that owns you.")));

        lines.Add(new(
            Loc.Tr("Looks like slave:"),
            Loc.Tr("{1,num}%", (int)(slave.Looks * 100)),
            ValueColour: slave.Looks > 0.1f ? PlayerInterface.BadColour : null,
            Tooltip: Loc.Tr("How much you look like an escaped slave. Based on how you are dressed/undressed. Wearing shackles and rags is a big giveaway. "
                + "Try to avoid hanging around too long near people who may recognise you.")));
        return lines;
    }

    /// <summary>
    /// The Disguise row and the blank-labelled row under it (<c>FUN_1405d4260</c>, <b>Verified</b> strings, docs/game/ui-screens.md "Which lines"): the uniform
    /// faction's name (Main), else "None"; the tooltip "Not currently in disguise." at level 0, else the rows Clothing disguise (level %, Good), In secure
    /// location -25%, Same faction -40%, Slave owner recognition -50%, Someone knows me personally -80% (Bad, each multiplying the total), Disguise blown
    /// (seconds, Bad, total 0) and Total (Main). The second row: "-" at level 0, else the level in percent (Main) or Blown! (Bad) while blown.
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
    /// The burden row under Total Weight (<c>FUN_1405d4260</c>, <b>Verified</b> by decompile): by the carry speed factor (1 unburdened), at least 0.95
    /// "-Weightless", 0.75 "-Lightweight", 0.5 "-Moderate" (Main colour), 0.1 "-Heavy", below "-Overloaded" (both Bad colour).
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