using System.Globalization;
using System.Numerics;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Data.Ui;
using Meitou.Simulation;
using Meitou.Simulation.Items;

namespace Meitou.Game;

sealed partial class PlayerInterface
{
    float InventoryScale => Height / 1080f;
    float InventoryCell => 25 * InventoryScale;
    readonly Dictionary<string, float> inventoryScroll = new(StringComparer.Ordinal);
    readonly Dictionary<string, (UiRect View, float Maximum)> inventoryViews = new(StringComparer.Ordinal);
    /// <summary>Per scrolled window, where its scrollbar's thumb runs and how tall it is (for dragging it).</summary>
    readonly Dictionary<string, (UiRect Track, float Thumb)> inventoryTracks = new(StringComparer.Ordinal);
    /// <summary>A scrollbar thumb being dragged: its window and where on the thumb it was taken.</summary>
    (string Screen, float Grab)? scrollDrag;

    void BindInventoryGeometry(string id, UiBindings b)
    {
        b.TemplateScale = InventoryScale;
        if (id is "bag" or "loot-bag" && (id == "bag" ? InventoryOwner() : LootBody())?.ItemGrid is { } bag)
            b.Positions["Root"] = new(0, 0, bag.BagWidth * InventoryCell + 40 * InventoryScale,
                Math.Min(Height * 0.8f, bag.BagHeight * InventoryCell + 108 * InventoryScale));
        if (id == "trader") BindTraderGeometry(b);
    }

    void PlaceBagWindow()
    {
        if (faithfulScreens.IsOpen("loot")) PlaceWindowRow(); else PlaceInventoryBeside("bag", left: true);
    }

    /// <summary>The open windows' bounds by screen, as laid out now.</summary>
    Dictionary<string, UiRect> WindowRoots()
    {
        var roots = new Dictionary<string, UiRect>(StringComparer.Ordinal);
        foreach (var f in faithfulScreens.Arrange(Width, Height, BindFaithfulScreen, AddFaithfulContent))
            if (f.Controls.FirstOrDefault(c => c.Type == "Window") is { } window) roots[f.Id] = window.Bounds;
        return roots;
    }

    /// <summary>
    /// Lays the open character, loot and bag windows out in one row, left to right: the limbs window, the character's bag, the character, the looted or traded character, its bag. The row
    /// is centred with the character's top (the limbs window keeps its margin top), so no window slides over another when the row is wider than the pair that placed them. A row wider
    /// than the screen spans it, its windows overlapping evenly rather than the host stacking the last ones on the right edge.
    /// </summary>
    void PlaceWindowRow()
    {
        var roots = WindowRoots();
        var row = new[] { "limbs", "bag", "inventory", "loot", "loot-bag" }.Where(roots.ContainsKey).ToArray();
        if (row.Length < 2) return;
        float gap = 6 * InventoryScale;
        float total = row.Sum(s => roots[s].Width) + gap * (row.Length - 1);
        if (total > Width) { gap -= (total - Width) / (row.Length - 1); total = Width; }
        float x = (Width - total) / 2, y = (roots.TryGetValue("inventory", out var lead) ? lead : roots[row[0]]).Y, margin = 0.05f * Math.Min(Width, Height);
        foreach (string screen in row)
        {
            var r = roots[screen];
            faithfulScreens.SetOffset(screen, faithfulScreens.GetOffset(screen) + new Vector2(x - r.X, (screen == "limbs" ? margin : y) - r.Y));
            x += r.Width + gap;
        }
    }

    /// <summary>
    /// The limbs window's place (<c>FUN_1406e4960</c>, <b>Verified</b>): its right edge on the left edge of the inventory (of its bag window when that is open), its top at the margin
    /// (5% of the smaller screen side); when that is off the screen, at the left edge with the inventory and its bag pushed right of it. Not moved with the inventory afterwards.
    /// With a loot window open it joins the window row instead.
    /// </summary>
    void PlaceLimbsWindow()
    {
        if (faithfulScreens.IsOpen("loot")) { PlaceWindowRow(); return; }
        var roots = WindowRoots();
        UiRect? Root(string screen) => roots.TryGetValue(screen, out var r) ? r : null;
        if (Root("limbs") is not { } limbs || (Root("bag") ?? Root("inventory")) is not { } anchor) return;
        float margin = 0.05f * Math.Min(Width, Height);
        float x = anchor.X - limbs.Width;
        if (x < 0)
        {
            foreach (string screen in new[] { "inventory", "bag" })
                if (faithfulScreens.IsOpen(screen)) faithfulScreens.SetOffset(screen, faithfulScreens.GetOffset(screen) + new Vector2(-x, 0));
            x = 0;
        }
        faithfulScreens.SetOffset("limbs", faithfulScreens.GetOffset("limbs") + new Vector2(x - limbs.X, margin - limbs.Y));
    }

    /// <summary>
    /// The four prosthetic slots (<c>FUN_1400d04d0</c>, <c>FUN_1401534f0</c>, <b>Verified</b>): arms 2 x 6 cells, legs 2 x 7; a slot is enabled only for a stump or a fitted
    /// prosthetic, else drawn with the skin's disabled tile and refusing drops (the simulation's check).
    /// </summary>
    void ArrangeLimbSlots(List<UiControl> controls, CharacterSnapshot lead)
    {
        for (int k = 0; k < 4; k++)
        {
            string section = k switch { 0 => "left_arm_replace", 1 => "right_arm_replace", 2 => "left_leg_replace", _ => "right_leg_replace" };
            int index = controls.FindIndex(c => c.Name == section);
            if (index < 0) continue;
            var slot = controls[index];
            int state = (lead.LimbBits >> (2 * k)) & 3;
            bool open = state is (int)Meitou.Simulation.Bodies.LimbState.Stump or (int)Meitou.Simulation.Bodies.LimbState.Replaced;
            controls[index] = slot with
            {
                Bounds = slot.Bounds with { Width = 2 * InventoryCell, Height = (k < 2 ? 6 : 7) * InventoryCell },
                Properties = new Dictionary<string, string>(slot.Properties)
                {
                    ["TileSize"] = FormattableString.Invariant($"{InventoryCell} {InventoryCell}"),
                    ["State"] = open ? "normal" : "disabled",
                },
            };
        }
    }

    /// <summary>
    /// A machine or container window's place (<b>Observed</b> on the original's 2026-10-26 screenshot of a mined iron rock): at the screen's left edge, vertically centred; an open
    /// inventory (with its bag) under it moves right of it.
    /// </summary>
    void PlaceMachineWindow(string id)
    {
        var roots = WindowRoots();
        if (!roots.TryGetValue(id, out var window)) return;
        faithfulScreens.SetOffset(id, faithfulScreens.GetOffset(id) + new Vector2(-window.X, (Height - window.Height) / 2 - window.Y));
        float left = new[] { "bag", "inventory" }.Where(roots.ContainsKey).Select(s => roots[s].X).DefaultIfEmpty(float.MaxValue).Min();
        float push = window.Width + 6 * InventoryScale - left;
        if (push <= 0) return;
        foreach (string screen in new[] { "inventory", "bag" })
            if (roots.ContainsKey(screen)) faithfulScreens.SetOffset(screen, faithfulScreens.GetOffset(screen) + new Vector2(push, 0));
    }

    /// <summary>Puts window <paramref name="id"/> beside <paramref name="anchor"/>'s, moving the anchor in when the pair would not fit the screen.</summary>
    void PlaceInventoryBeside(string id, bool left, string anchor = "inventory")
    {
        var roots = WindowRoots();
        if (!roots.TryGetValue(id, out var other) || !roots.TryGetValue(anchor, out var own)) return;
        float gap = 6 * InventoryScale;
        float x = left ? own.X - other.Width - gap : own.X + own.Width + gap;
        // Make room for the pair before the screen host clamps either window to the viewport.
        if (x < 0 || x + other.Width > Width)
        {
            float ownX = left ? Math.Min(Width - own.Width, other.Width + gap) : Math.Max(0, Width - other.Width - gap - own.Width);
            faithfulScreens.SetOffset(anchor, faithfulScreens.GetOffset(anchor) + new Vector2(ownX - own.X, 0));
            x = left ? ownX - other.Width - gap : ownX + own.Width + gap;
        }
        faithfulScreens.SetOffset(id, faithfulScreens.GetOffset(id) + new Vector2(x - other.X, own.Y - other.Y));
    }

    void ArrangeEquipmentGrid(List<UiControl> controls, InventoryView grid, Meitou.Data.GameRecord? race)
    {
        foreach (string section in new[] { "main", "backpack_attach", "back", "hip", "shirt", "head", "legs", "boots", "armour", "belt" })
        {
            int index = controls.FindIndex(c => c.Name == section);
            if (index < 0) continue;
            var slot = controls[index];
            var size = InventoryCommands.SectionSize(section);
            int columns = section == "main" ? grid.Width : size.Width;
            int rows = section == "main" ? grid.Height : size.Height;
            var bounds = slot.Bounds with { Width = columns * InventoryCell, Height = rows * InventoryCell };
            bool blocked = race is not null && RaceData.BlocksSection(race, section);
            controls[index] = slot with
            {
                Bounds = bounds,
                Properties = new Dictionary<string, string>(slot.Properties)
                {
                    ["TileSize"] = FormattableString.Invariant($"{InventoryCell} {InventoryCell}"),
                    // A section the race's no hats / no shirts / no shoes flag blocks is drawn with the skin's disabled tile (as a prosthetic slot without a stump) but a drop still finds it and is refused.
                    ["State"] = blocked ? "disabled" : "normal",
                    ["Blocked"] = blocked ? "1" : "0",
                },
            };
        }
    }

    /// <summary>The grid is laid in the window with <paramref name="above"/> (default 50) layout pixels over it and <paramref name="below"/> (default 58) under it.</summary>
    void ArrangeContainerGrid(string id, List<UiControl> controls, int columns, int rows, float above = 50, float below = 58)
    {
        int index = controls.FindIndex(c => c.Name == "backpack_content");
        var root = controls.FirstOrDefault(c => c.Type == "Window");
        if (index < 0 || root is null) return;
        float scale = InventoryScale, cell = InventoryCell;
        var r = root.Bounds;
        float top = above * scale;
        var view = new UiRect(r.X + 18 * scale, r.Y + top, Math.Max(0, r.Width - 40 * scale), Math.Max(0, r.Height - top - below * scale));
        float maximum = Math.Max(0, rows * cell - view.Height);
        if (maximum < 0.01f) maximum = 0;
        float scroll = Math.Clamp(inventoryScroll.GetValueOrDefault(id), 0, maximum);
        inventoryScroll[id] = scroll;
        inventoryViews[id] = (view, maximum);
        var slot = controls[index];
        controls[index] = slot with
        {
            Bounds = new(view.X, view.Y - scroll, columns * cell, rows * cell), Clip = view.Intersect(slot.Clip ?? view),
            Properties = new Dictionary<string, string>(slot.Properties) { ["TileSize"] = FormattableString.Invariant($"{cell} {cell}") },
        };
        int arrange = controls.FindIndex(c => c.Name == "ArrangeButton");
        if (arrange >= 0) controls[arrange] = controls[arrange] with { Bounds = new(r.X + (r.Width - 110 * scale) / 2, r.Y + r.Height - 44 * scale, 110 * scale, 32 * scale) };
        if (maximum <= 0) { inventoryTracks.Remove(id); return; }
        AddScrollBar(id, controls, new(r.X + r.Width - 22 * scale, view.Y, 17 * scale, view.Height), view.Height, rows * cell, scroll, maximum, root.Clip);
    }

    /// <summary>
    /// A vertical scrollbar as the original's <c>Kenshi_ScrollBarV</c> template draws it in <paramref name="bar"/>: arrows at both ends, the track between them and a thumb
    /// <c>MinTrackSize</c> (10) or more tall, sized to the visible share of the canvas. The arrows scroll by steps, a click on the track beside the thumb by a page, the thumb drags.
    /// </summary>
    void AddScrollBar(string id, List<UiControl> controls, UiRect bar, float visible, float canvas, float scroll, float maximum, UiRect? clip)
    {
        float scale = InventoryScale;
        void Part(string name, UiRect bounds, string skin, string action = "", bool pickable = false) => controls.Add(new(name, action.Length > 0 || pickable ? "Button" : "Widget", bounds, "", action, skin, true,
            new Dictionary<string, string>(), Vector4.Zero, Pickable: action.Length > 0 || pickable, Clip: clip));
        Part("inventory-scroll", bar, "Kenshi_TextBoxEmptySkin");
        Part("inventory-scroll-up", new(bar.X, bar.Y + 2 * scale, bar.Width, 12 * scale), "Kenshi_SmallArrowUpSkin", "inventory-scroll:1");
        Part("inventory-scroll-down", new(bar.X, bar.Y + bar.Height - 14 * scale, bar.Width, 12 * scale), "Kenshi_SmallArrowDownSkin", "inventory-scroll:-1");
        var track = new UiRect(bar.X, bar.Y + 14 * scale, bar.Width, Math.Max(0, bar.Height - 28 * scale));
        float thumb = Math.Clamp(track.Height * visible / Math.Max(1, canvas), Math.Min(10 * scale, track.Height), track.Height);
        float top = track.Y + (track.Height - thumb) * scroll / maximum;
        Part("inventory-scroll-page-up", new(track.X, track.Y, track.Width, top - track.Y), "", "inventory-scroll-page:1");
        Part("inventory-scroll-page-down", new(track.X, top + thumb, track.Width, track.Y + track.Height - top - thumb), "", "inventory-scroll-page:-1");
        Part("inventory-scroll-thumb", new(track.X, top, track.Width, thumb), "Kenshi_SmallBarSkin", pickable: true);
        inventoryTracks[id] = (track, thumb);
    }

    /// <summary>Takes the thumb of a window's scrollbar under the pointer (true: taken).</summary>
    bool ScrollThumbDown(string screen, UiControl picked, Vector2 p)
    {
        if (picked.Name != "inventory-scroll-thumb" || !inventoryTracks.ContainsKey(screen)) return false;
        scrollDrag = (screen, p.Y - picked.Bounds.Y);
        return true;
    }

    /// <summary>Moves the dragged thumb's canvas so the thumb follows the pointer.</summary>
    bool ScrollThumbMove(Vector2 p)
    {
        if (scrollDrag is not { } drag) return false;
        if (!inventoryTracks.TryGetValue(drag.Screen, out var t) || !inventoryViews.TryGetValue(drag.Screen, out var view) || t.Track.Height <= t.Thumb) return true;
        float share = Math.Clamp((p.Y - drag.Grab - t.Track.Y) / (t.Track.Height - t.Thumb), 0, 1);
        inventoryScroll[drag.Screen] = share * view.Maximum;
        return true;
    }

    bool InventoryWheel(Vector2 p, float steps)
    {
        if (faithfulScreens.HitTest(p) is not { } hit || !inventoryViews.TryGetValue(hit.Screen, out var view) || !view.View.Contains(p.X, p.Y)) return false;
        ScrollInventory(hit.Screen, steps);
        return true;
    }

    void ScrollInventory(string id, float steps)
    {
        if (inventoryViews.TryGetValue(id, out var view))
            inventoryScroll[id] = Math.Clamp(inventoryScroll.GetValueOrDefault(id) - steps * InventoryCell * 3, 0, view.Maximum);
    }

    /// <summary>A click on the scrollbar track beside the thumb: a page (the visible height) up (1) or down (-1).</summary>
    void ScrollInventoryPage(string id, float direction)
    {
        if (inventoryViews.TryGetValue(id, out var view))
            inventoryScroll[id] = Math.Clamp(inventoryScroll.GetValueOrDefault(id) - direction * view.View.Height, 0, view.Maximum);
    }

    /// <summary>The data panel's lines; an animal's window shows only the weight and burden rows (the original's animal inventory, observed).</summary>
    void AddInventorySummary(List<UiControl> controls, CharacterSnapshot lead, bool animal = false)
    {
        string weight = ((int)(lead.ItemGrid?.TotalWeight ?? 0)).ToString(CultureInfo.InvariantCulture);   // truncated, as the original prints it
        if (lead.ItemGrid?.CarryCapacity is { } capacity) weight += "/" + ((int)capacity).ToString(CultureInfo.InvariantCulture);
        var (burdenWord, burdenColour) = BurdenText(lead.Hud?.Encumbrance ?? lead.ItemGrid?.Burden ?? 1);
        AddLines(controls, "datapanel", InventorySummaryLines(lead, weight, burdenWord, burdenColour, animal), 20 * InventoryScale);
    }

    /// <summary>
    /// The data panel's lines (<c>FUN_1405d4260</c> mask 0xf, called through Character slot 40 for the window's owner, <b>Verified</b> by disassembly, docs/game/ui-screens.md): Total Weight and the burden row
    /// for every character; for a character of the player's faction also Disguise (no disguise: "None" and a "-" value on a blank-labelled row; the disguise percent is not simulated), Slave of
    /// (free: "-" greyed; a slave or escaped slave: its owner faction's name in Bad, no line without an owner) and Looks like slave (free: "-" greyed; else the level in percent, Bad above 10 %).
    /// A character of another faction has only the weight rows (and, for a trader, its money lines, not built).
    /// </summary>
    List<HudLine> InventorySummaryLines(CharacterSnapshot lead, string weight, string burdenWord, string? burdenColour, bool animal)
    {
        var lines = new List<HudLine> { new(Loc.Tr("Total Weight:"), weight), new("", burdenWord, ValueColour: burdenColour) };
        if (animal || !lead.IsPlayer) return lines;
        lines.Add(new(Loc.Tr("Disguise:"), Loc.Tr("None"), Tooltip: Loc.Tr("Not currently in disguise.")));
        lines.Add(new("    ", "-"));
        if (lead.Slave is not { } slave)
        {
            lines.Add(new(Loc.Tr("Slave of:"), "-", GreyColour, GreyColour, Loc.Tr("You are a free person.")));
            lines.Add(new(Loc.Tr("Looks like slave:"), "-", GreyColour, GreyColour, Loc.Tr("You are no slave.")));
            return lines;
        }
        if (slave.Owner.Length > 0) lines.Add(new(Loc.Tr("Slave of:"), slave.Owner, ValueColour: BadColour, Tooltip: Loc.Tr("The faction that owns you.")));
        lines.Add(new(Loc.Tr("Looks like slave:"), Loc.Tr("{1,num}%", (int)(slave.Looks * 100)), ValueColour: slave.Looks > 0.1f ? BadColour : null,
            Tooltip: Loc.Tr("How much you look like an escaped slave. Based on how you are dressed/undressed. Wearing shackles and rags is a big giveaway. Try to avoid hanging around too long near people who may recognise you.")));
        return lines;
    }

    /// <summary>
    /// The burden row under Total Weight (<c>FUN_1405d4260</c>, <b>Verified</b> by decompile): by the carry speed factor (1 unburdened), at least 0.95 "-Weightless",
    /// 0.75 "-Lightweight", 0.5 "-Moderate" (Main colour), 0.1 "-Heavy", below "-Overloaded" (both Bad colour).
    /// </summary>
    internal static (string Text, string? Colour) BurdenText(float factor) => factor switch
    {
        >= 0.95f => ("-Weightless", null),
        >= 0.75f => ("-Lightweight", null),
        >= 0.5f => ("-Moderate", MainColour),
        >= 0.1f => ("-Heavy", BadColour),
        _ => ("-Overloaded", BadColour),
    };
}
