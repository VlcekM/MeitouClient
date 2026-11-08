using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Meitou.Data.Ui;
using Meitou.Simulation;
using Meitou.Simulation.Items;

namespace Meitou.Game.Ui;

/// <summary>The inventory windows (docs/game/ui-screens.md section 6): the characters' and containers' windows, and the item held with the mouse between them.</summary>
sealed class InventoryWindows(PlayerInterface player)
{
    // Holding an item with the mouse (docs/game/ui-screens.md section 6 "Holding an item"; the original's <c>MouseInventory</c>, <c>FUN_14070e640</c>): a left press on an item lifts it
    // (LiftItem: one unit of a stack, the whole stack with Shift or Ctrl); the release places it where the pointer is when the pointer moved since the press, otherwise it stays on
    // the mouse and the next press places it. The item lives in the world (WorldSnapshot.Held); the interface draws what it expects until the world has applied its command.
    /// <summary>The held item as drawn: the world's, or what the interface expects while its last held command is on its way (<see cref="heldSerial"/>).</summary>
    internal HeldItemView? held;
    /// <summary>Where the pointer holds the item: its offset from the item's top-left corner, in cells (at least half a cell inside the footprint, <c>FUN_140713560</c>).</summary>
    Vector2 heldGrab;
    /// <summary>The serial of the held command the world has not applied yet (0: none), when it was sent, and the last serial used.</summary>
    internal long heldSerial, heldSentAt, heldCounter;
    /// <summary>Where the press that lifted the held item was, while the button is down (null otherwise).</summary>
    internal Vector2? liftPress;
    /// <summary>The item cell a whole-stack lift emptied, hidden until the world's answer (its window and the cell's index or machine code).</summary>
    internal (ItemOwner Window, int Code)? lifting;
    /// <summary>Shift and Ctrl at the last press (a lift takes the whole stack with either).</summary>
    internal bool pressShift, pressCtrl;
    /// <summary>The pinned inventory windows (<see cref="PinnedPrefix"/> and a number) and the character each stays on (docs/game/ui-screens.md section 6).</summary>
    internal readonly Dictionary<string, CharacterId> pinnedInventories = new(StringComparer.Ordinal);
    /// <summary>The pinned characters last told to the simulation (<see cref="ShowInventoryCommand"/>), which publishes their grids while they are not selected.</summary>
    CharacterId[] pinnedSent = [];
    /// <summary>The character whose window was pinned last: selecting someone else opens the inventory again for them, beside it.</summary>
    CharacterId pinnedFrom = CharacterId.None;
    const string PinnedPrefix = "inventory-pin-";
    internal static bool IsPinnedInventory(string screen) => screen.StartsWith(PinnedPrefix, StringComparison.Ordinal);

    /// <summary>How long the interface waits for the world to apply a held command before it shows the world's held item again (engine choice).</summary>
    const double HeldWaitSeconds = 1;

    /// <summary>The screens that show items: the held item goes back when none of them is open.</summary>
    static readonly string[] ItemScreens = ["inventory", "bag", "limbs", "loot", "loot-bag", "trader", "building", "bench", "storage"];

    /// <summary>
    /// The character the inventory window (and its bag and limbs) shows: the lead selected character, also while trading, as the original's (a trade passes to the window's character,
    /// <see cref="Meitou.Simulation.Trading.TradeSystem.TradeWith"/>; a pinned window is its own screen, <see cref="SyncPinnedInventories"/>).
    /// </summary>
    internal CharacterSnapshot? InventoryOwner() => player.Selected().FirstOrDefault();

    /// <summary>The character a character window shows: a pinned window's, the loot's body, else the inventory owner.</summary>
    internal CharacterSnapshot? WindowCharacter(string screen) =>
        TradeWindows.IsLootScreen(screen) ? player.tradeWindows.LootBody()
        : screen == "limbs" ? LimbsCharacter()
        : pinnedInventories.TryGetValue(screen, out var pinned) ? player.session.CurrentSnapshot.Characters.FirstOrDefault(c => c.Id == pinned)
        : InventoryOwner();

    /// <summary>
    /// Once a frame: the inventory window's pin (its caption's "O") moves it to a pinned window of its own that stays on its character, and the inventory opens again beside it for
    /// the next character selected, so several inventories show at once (docs/game/ui-screens.md section 6). Unpinning a pinned window gives it back to the selection when the inventory is
    /// closed, else closes it. A pinned window whose character is gone closes. Pinned windows have no bag or limbs window.
    /// </summary>
    internal void SyncPinnedInventories()
    {
        if (player.faithfulScreens is null || player.tradeWindows.characterInventoryLayout is null) return;
        var lead = InventoryOwner();
        if (player.faithfulScreens.IsPinned("inventory") && lead is not null)
        {
            string slot = pinnedInventories.FirstOrDefault(p => p.Value == lead.Id).Key ?? FreePinnedSlot();
            pinnedInventories[slot] = lead.Id;
            player.faithfulScreens.SetOffset(slot, player.faithfulScreens.GetOffset("inventory"));
            player.faithfulScreens.Close("inventory");
            player.faithfulScreens.Open(slot);
            player.faithfulScreens.SetPinned(slot, true);
            // The same window, not a new one: no closing and opening sounds.
            player.interfaceSounds.soundScreens.Remove("inventory"); player.interfaceSounds.soundScreens.Add(slot);
            pinnedFrom = lead.Id;
        }
        foreach (var (slot, character) in pinnedInventories.ToArray())
        {
            if (!player.faithfulScreens.IsOpen(slot) || player.session.CurrentSnapshot.Characters.All(c => c.Id != character)) { player.faithfulScreens.Close(slot); pinnedInventories.Remove(slot); continue; }
            if (player.faithfulScreens.IsPinned(slot)) continue;
            pinnedInventories.Remove(slot);
            if (!player.faithfulScreens.IsOpen("inventory"))
            {
                player.faithfulScreens.SetOffset("inventory", player.faithfulScreens.GetOffset(slot));
                player.faithfulScreens.Open("inventory");
                player.interfaceSounds.soundScreens.Add("inventory");
            }
            player.interfaceSounds.soundScreens.Remove(slot);
            player.faithfulScreens.Close(slot);
        }
        if (pinnedInventories.Count == 0) pinnedFrom = CharacterId.None;
        else if (lead is not null && lead.Id != pinnedFrom && !pinnedInventories.ContainsValue(lead.Id) && !player.faithfulScreens.IsOpen("inventory"))
        {
            player.faithfulScreens.Open("inventory");
            if (pinnedInventories.FirstOrDefault(p => p.Value == pinnedFrom).Key is { } anchor) player.PlaceInventoryBeside("inventory", left: true, anchor: anchor);
            pinnedFrom = CharacterId.None;
        }
        var shown = pinnedInventories.Values.ToArray();
        if (!shown.SequenceEqual(pinnedSent)) player.Say(new ShowInventoryCommand(pinnedSent = shown) { Tick = player.session.World.Tick });
    }

    /// <summary>Closes every inventory window: the character's (bag, limbs, pinned ones), loot and the shown building's. Trade stays (it is the trader's to end).</summary>
    internal void CloseInventoryWindows()
    {
        foreach (string screen in ItemScreens.Where(s => s != "trader").Concat(pinnedInventories.Keys)) player.faithfulScreens.Close(screen);
    }

    /// <summary>The first pinned window id not in use, registered with the character window's layout the first time.</summary>
    string FreePinnedSlot()
    {
        int n = 1;
        while (pinnedInventories.ContainsKey(PinnedPrefix + n)) n++;
        string slot = PinnedPrefix + n;
        if (!player.faithfulScreens.IsRegistered(slot)) player.faithfulScreens.Register(slot, player.tradeWindows.characterInventoryLayout!);
        player.tradeWindows.animalWindow.Remove(slot);
        player.faithfulScreens.Replace(slot, player.tradeWindows.characterInventoryLayout!);
        return slot;
    }

    /// <summary>The item window a screen shows: the inventory owner's, the loot's body, the stock, the shown machine or container; null for other screens.</summary>
    ItemOwner? WindowOf(string screen) => screen switch
    {
        _ when IsPinnedInventory(screen) => WindowCharacter(screen) is { } pinned ? ItemOwner.Of(pinned.Id) : null,
        "inventory" or "bag" => InventoryOwner() is { } owner ? ItemOwner.Of(owner.Id) : null,
        "limbs" => LimbsCharacter() is { } limbed ? ItemOwner.Of(limbed.Id) : null,
        "loot" or "loot-bag" => player.tradeWindows.LootBody() is { } body ? ItemOwner.Of(body.Id) : null,
        "trader" => player.session.CurrentSnapshot.Trade is not null ? ItemOwner.Stock : null,
        "building" or "bench" or "storage" => player.production.shownMachine is { } site ? ItemOwner.AtSite(site) : null,
        _ => null,
    };

    /// <summary>Whether an item cell (its window and index or machine code) was just lifted whole and waits for the world: it is not drawn.</summary>
    internal bool Lifted(ItemOwner window, int code) => lifting is { } l && l.Window == window && l.Code == code;

    /// <summary>Sends a held-item command with a new serial; the world's published serial says when it was applied.</summary>
    void SayHeld(HeldCommand command)
    {
        heldCounter = Math.Max(heldCounter + 1, Stopwatch.GetTimestamp());
        heldSerial = heldCounter;
        heldSentAt = Stopwatch.GetTimestamp();
        player.Say(command with { Serial = heldSerial });
    }

    /// <summary>Puts the held item back where it came from (<see cref="ReturnHeld"/>).</summary>
    void ReturnHeldItem()
    {
        if (held is null) return;
        SayHeld(new ReturnHeld { Tick = player.session.World.Tick });
        held = null;
    }

    internal bool InventoryPointerDown(Vector2 p)
    {
        if (!player.UiVisible || player.faithfulScreens.HasModal) return false;
        // A press while an item is held places it (the original's MouseInventory press with an item on the mouse).
        if (held is not null)
        {
            PlaceHeldAt(p);
            return true;
        }
        if (player.faithfulScreens.HitTest(p) is not { } picked || !picked.Control.Enabled) return false;
        if (player.ScrollThumbDown(picked.Screen, picked.Control, p)) return true;
        return LiftAt(picked.Screen, picked.Control, p);
    }

    internal void InventoryPointerUp(Vector2 p)
    {
        player.scrollDrag = null;
        if (liftPress is not { } from) return;
        liftPress = null;
        // The release places the item it lifted once the pointer has moved (a drag); a click leaves it on the mouse for the next press.
        if (held is not null && player.UiVisible && Vector2.Distance(from, p) >= PlayerInterface.DragThreshold) PlaceHeldAt(p);
    }

    /// <summary>
    /// A press on an item cell lifts it (<c>FUN_1407139a0</c>): a character's item, the stock's, a machine's or container's. Nothing for the other controls. The cell's footprint gives the
    /// grab offset.
    /// </summary>
    bool LiftAt(string screen, UiControl control, Vector2 p)
    {
        var snapshot = player.session.CurrentSnapshot;
        int code;
        ItemOwner window;
        CharacterId actor;
        InventoryItemToken token;
        HeldItemView view;
        bool whole;
        HeldItemView View(string record, string name, string material, int quantity, int width, int height, float? charge, ItemOwner from) =>
            new(record, name, material, default, "", whole ? quantity : 1, width, height, charge, from, -1);
        if (control.Properties.TryGetValue("TradeItem", out var stockIndex))
        {
            code = int.Parse(stockIndex, CultureInfo.InvariantCulture);
            if (snapshot.Trade is not { } trade || trade.Stock.FirstOrDefault(i => i.Index == code) is not { } stock) return false;
            (window, actor, token, whole) = (ItemOwner.Stock, trade.Customer, stock.Token, pressShift || pressCtrl || stock.Quantity <= 1);
            view = View(stock.Record, stock.Name, stock.Material, stock.Quantity, stock.Width, stock.Height, stock.Charge, window);
        }
        else if (control.Properties.TryGetValue("MachineItem", out var raw))
        {
            code = int.Parse(raw, CultureInfo.InvariantCulture);
            if (player.production.Machine(player.production.shownMachine) is not { } m || (InventoryOwner() ?? player.Selected().FirstOrDefault()) is not { } who) return false;
            (window, actor) = (ItemOwner.AtSite(m.Site), who.Id);
            int k = code - 10;
            if (code >= 1000 && m.Storage?.Items.FirstOrDefault(i => i.Index == code - 1000) is { } stored)
            {
                (token, whole) = (new(stored.Index, stored.Record, "main", stored.X, stored.Y, stored.Quantity, stored.Quality), pressShift || pressCtrl || stored.Quantity <= 1);
                view = View(stored.Record, stored.Name, stored.Material, stored.Quantity, stored.Width, stored.Height, stored.Charge, window);
            }
            else if (code == 8 && m.Craft is { Done: [var made, ..] })
            {
                (token, whole) = (new(0, made.Record, "out", 0, 0, made.Quantity, made.Quality), pressShift || pressCtrl || made.Quantity <= 1);
                view = View(made.Record, made.Name, made.Material, made.Quantity, made.Width, made.Height, made.Charge, window);
            }
            else if ((code == 8 ? m.Output : k >= 0 && k < m.Inputs.Count ? m.Inputs[k] : null) is { Count: > 0 } slot)
            {
                (token, whole) = (new(0, slot.Item, code == 8 ? "out" : $"in{k + 1}", 0, 0, slot.Count, 0), pressShift || pressCtrl || slot.Count <= 1);
                view = View(slot.Item, slot.Name, "", slot.Count, slot.Width, slot.Height, null, window);
            }
            else return false;
        }
        else if (control.Properties.TryGetValue("InventoryItem", out var index))
        {
            code = int.Parse(index, CultureInfo.InvariantCulture);
            bool loot = TradeWindows.IsLootScreen(screen);
            if (WindowCharacter(screen) is not { ItemGrid: { } grid } owner || grid.Items.FirstOrDefault(i => i.Index == code) is not { } item
                || !(InventoryCommands.IsMovableSection(item.Section) || InventoryCommands.IsLimbSection(item.Section))) return false;
            (window, actor) = (ItemOwner.Of(owner.Id), loot ? snapshot.LootOpener : owner.Id);
            (token, whole) = (new(item.Index, item.Record, item.Section, item.X, item.Y, item.Quantity, item.Quality), pressShift || pressCtrl || item.Quantity <= 1);
            view = View(item.Record, item.Name, item.Material, item.Quantity, item.Width, item.Height, item.Charge, window);
        }
        else return false;
        SayHeld(new LiftItem(actor, window, token, whole) { Tick = player.session.World.Tick });
        held = view;
        lifting = whole ? (window, code) : null;
        var b = control.Bounds;
        float cell = view.Width > 0 ? b.Width / view.Width : player.InventoryCell;
        heldGrab = ClampGrab((p - new Vector2(b.X, b.Y)) / MathF.Max(cell, 1), view);
        liftPress = p;
        player.interfaceSounds.Sound("Item_Lift", item: view.Record);
        return true;
    }

    /// <summary>The grab offset kept at least half a cell inside the item's footprint (<c>FUN_140713560</c>).</summary>
    static Vector2 ClampGrab(Vector2 grab, HeldItemView item) =>
        new(Math.Clamp(grab.X, 0.5f, MathF.Max(0.5f, item.Width - 0.5f)), Math.Clamp(grab.Y, 0.5f, MathF.Max(0.5f, item.Height - 0.5f)));

    enum DropKind { World, Window, Section }

    /// <summary>Where the held item would go at a point: nowhere (the world), a window without a section under it, or a window's section with the snapped cell and its area on screen.</summary>
    readonly record struct DropSpot(DropKind Kind, ItemOwner Window, string Section, int X, int Y, UiRect Area, UiRect? Clip, bool Shows);

    /// <summary>The section widgets of an item screen and the sections they are (the trader, storage and bench grids are their window's <c>main</c>).</summary>
    static (string Widget, string Section)[] SectionsOf(string screen) => (IsPinnedInventory(screen) ? "inventory" : screen) switch
    {
        "inventory" or "loot" => [("backpack_attach", "backpack_attach"), ("back", "back"), ("hip", "hip"), ("shirt", "shirt"), ("head", "head"), ("legs", "legs"), ("boots", "boots"),
            ("armour", "armour"), ("belt", "belt"), ("main", "main")],
        "bag" or "loot-bag" => [("backpack_content", "backpack_content")],
        "limbs" => [("left_arm_replace", "left_arm_replace"), ("right_arm_replace", "right_arm_replace"), ("left_leg_replace", "left_leg_replace"), ("right_leg_replace", "right_leg_replace")],
        "trader" or "bench" or "storage" => [("backpack_content", "main")],
        "building" => [("in1", "in1"), ("Input1Panel", "in1"), ("in2", "in2"), ("Input2Panel", "in2"), ("out", "out"), ("OutputColumn", "out")],
        _ => [],
    };

    /// <summary>
    /// Where the held item goes when let go at <paramref name="p"/> (<c>FUN_14070e640</c>): the window under the pointer, else under the item's centre; the section under the pointer (else
    /// under the centre); the cell its top-left corner is nearest, moved inside the section (<c>FUN_14074bec0</c>). <see cref="DropSpot.Shows"/>: the section takes the item and its footprint
    /// fits (what is under it does not matter: a swap is legal), when the placement highlight shows.
    /// </summary>
    DropSpot DropAt(Vector2 p)
    {
        var none = new DropSpot(DropKind.World, default, "", 0, 0, default, null, false);
        if (held is not { } h) return none;
        float cell = player.InventoryCell;
        var topLeft = p - heldGrab * cell;
        var centre = topLeft + new Vector2(h.Width, h.Height) * cell / 2;
        if ((player.faithfulScreens.HitTest(p) ?? player.faithfulScreens.HitTest(centre)) is not { } over) return none;
        if (WindowOf(over.Screen) is not { } window) return none with { Kind = DropKind.Window };
        var controls = player.faithfulScreens.LastFrame.FirstOrDefault(f => f.Id == over.Screen)?.Controls ?? [];
        // A disabled slot (a limb that takes no prosthetic) has no mouse focus in the original: the drop finds no section there.
        foreach (var point in new[] { p, centre })
            foreach (var (name, section) in SectionsOf(over.Screen))
                if (controls.LastOrDefault(c => c.Name == name && c.Bounds.Contains(point.X, point.Y) && (c.Clip?.Contains(point.X, point.Y) ?? true)
                        && c.Properties.GetValueOrDefault("State") != "disabled") is { } slot)
                    // A furnace's in1 grid is its storage, the sim's "main".
                    return Spot(window, name == "in1" && window.Holder == ItemHolder.Site && ProductionWindows.FurnaceGrid(player.production.Machine(window.Site)) is not null ? "main" : section, slot, topLeft, h);
        return none with { Kind = DropKind.Window, Window = window };
    }

    DropSpot Spot(ItemOwner window, string section, UiControl slot, Vector2 topLeft, HeldItemView h)
    {
        var b = slot.Bounds;
        var (columns, rows) = GridOf(window, section);
        var look = window.Holder == ItemHolder.Character ? player.session.CurrentSnapshot.Characters.FirstOrDefault(c => c.Id == window.Character)?.Appearance : null;
        bool takes = InventoryCommands.SlotRefusal(section, h.Type, h.EquipmentSection, h.BagItems, h.WearableBy(look?.Race.StringId, look?.Animal is not null)) is null;
        if (section is "in1" or "in2" or "out")
            takes = section != "out" && player.production.Machine(window.Site) is { } m && int.Parse(section.AsSpan(2), CultureInfo.InvariantCulture) - 1 is var k && k < m.Inputs.Count && m.Inputs[k].Item == h.Record;
        if (columns == 0) return new(DropKind.Section, window, section, 0, 0, b, slot.Clip, takes);
        float cw = InventoryCommands.IsEquipmentSection(section) ? player.InventoryCell : b.Width / columns, ch = InventoryCommands.IsEquipmentSection(section) ? player.InventoryCell : b.Height / rows;
        bool fits = h.Width <= columns && h.Height <= rows;
        int x = Math.Clamp((int)MathF.Round((topLeft.X - b.X) / cw), 0, Math.Max(0, columns - h.Width)), y = Math.Clamp((int)MathF.Round((topLeft.Y - b.Y) / ch), 0, Math.Max(0, rows - h.Height));
        return new(DropKind.Section, window, section, x, y, new(b.X + x * cw, b.Y + y * ch, h.Width * cw, h.Height * ch), slot.Clip, takes && fits);
    }

    /// <summary>A section's size in cells; 0 x 0 for one that holds one item whatever its footprint (clothing, the bag slot, prosthetics, machine slots).</summary>
    (int Columns, int Rows) GridOf(ItemOwner window, string section)
    {
        var snapshot = player.session.CurrentSnapshot;
        switch (window.Holder)
        {
            case ItemHolder.Stock:
                return snapshot.Trade is { } trade ? (trade.StockWidth, trade.StockHeight) : (0, 0);
            case ItemHolder.Site:
                return section == "main" && player.production.Machine(window.Site)?.Storage is { } storage ? (storage.Width, storage.Height) : (0, 0);
            default:
                if (InventoryCommands.IsSingleSlot(section)) return (0, 0);
                if (section is "back" or "hip") return InventoryCommands.SectionSize(section);
                var grid = snapshot.Characters.FirstOrDefault(c => c.Id == window.Character)?.ItemGrid;
                return section == "backpack_content" ? (grid?.BagWidth ?? 0, grid?.BagHeight ?? 0) : (grid?.Width ?? 0, grid?.Height ?? 0);
        }
    }

    /// <summary>
    /// The held item let go (a release after a drag, or a press while holding it): over a window's section it is placed (a sale onto the stock of a stolen item a shop may catch first asks,
    /// as the right click's); over a window elsewhere it goes back; over the world it is given to the character under the pointer, else dropped on the ground (<c>FUN_1407136a0</c>).
    /// </summary>
    void PlaceHeldAt(Vector2 p)
    {
        if (held is not { } h) return;
        player.ArrangeUi();
        var spot = DropAt(p);
        // Meitou: let go over a squad bar portrait, it is given to that character as one in the world would be.
        if (spot.Kind == DropKind.World && player.portraitBar.SquadBarMemberAt(p) is { } member)
        {
            if (h.From == ItemOwner.Of(member)) ReturnHeldItem();
            else { SayHeld(new GiveHeld(member) { Tick = player.session.World.Tick }); held = null; }
            return;
        }
        if (spot.Kind == DropKind.World && player.OverHud(p)) spot = spot with { Kind = DropKind.Window };
        switch (spot.Kind)
        {
            case DropKind.Section:
            {
                var place = new PlaceHeld(spot.Window, spot.Section, spot.X, spot.Y) { Tick = player.session.World.Tick };
                bool sale = h.From.Holder != ItemHolder.Stock && spot.Window.Holder == ItemHolder.Stock, exchange = sale || h.From.Holder == ItemHolder.Stock && spot.Window.Holder != ItemHolder.Stock;
                void Send()
                {
                    // A bag put on its slot opens its window again (FUN_14070e120).
                    if (spot.Section == "backpack_attach" && spot.Window.Holder == ItemHolder.Character && h.BagItems >= 0) bagOpen.Add(spot.Window.Character);
                    SayHeld(place with { Tick = player.session.World.Tick });
                    player.interfaceSounds.Sound(exchange ? "Sell_Item" : "Item_Drop", item: h.Record);
                    held = null;
                }
                if (sale && player.session.CurrentSnapshot.Trade?.Held is { FenceOdds: { } odds })
                {
                    player.Confirm("FENCING ITEM", TradeWindows.FenceQuestion(odds), Send);
                    player.UpdateMenuPause();
                    return;
                }
                Send();
                return;
            }
            case DropKind.Window:
                ReturnHeldItem();
                return;
            default:
                // Given to a character: no sound; dropped on the ground: Character_Drop_Ground (FUN_1407136a0).
                if (player.PickWhere(p, _ => true) is { } who && h.From != ItemOwner.Of(who)) SayHeld(new GiveHeld(who) { Tick = player.session.World.Tick });
                else
                {
                    SayHeld(new DropHeld { Tick = player.session.World.Tick });
                    player.interfaceSounds.Sound("Character_Drop_Ground", item: h.Record);
                }
                held = null;
                return;
        }
    }

    /// <summary>The held item under the pointer with the placement highlight under it (a half-transparent white rectangle on the snapped cells, shown only where it may go), and the fencing prompt.</summary>
    internal void AddInventoryDrag(List<UiControl> controls)
    {
        if (held is { } h && player.UiVisible)
        {
            if (DropAt(player.mouse) is { Kind: DropKind.Section, Shows: true } spot)
                controls.Add(new("held-target", "Widget", spot.Area, "", "", "", true, new Dictionary<string, string>(), new Vector4(1, 1, 1, 0.5f), Pickable: false, Clip: spot.Clip));
            float cell = player.InventoryCell;
            player.AddItemCell(controls, "held-item", new(player.mouse.X - heldGrab.X * cell, player.mouse.Y - heldGrab.Y * cell, h.Width * cell, h.Height * cell),
                h.Record, h.Material, h.Name, h.Quantity, null, "HeldItem", 0, null, pickable: false, charge: h.Charge, colour: h.Colour);
        }
        player.tradeWindows.AddFencePrompt(controls);
    }

    /// <summary>Right click or Escape: the held item goes back (and a scrollbar drag ends); true when there was one.</summary>
    internal bool CancelInventoryDrag()
    {
        if (player.faithfulScreens.HasModal) return false;   // the open question (the fencing prompt) takes the key first
        bool captured = held is not null || player.scrollDrag is not null || liftPress is not null;
        player.scrollDrag = null;
        liftPress = null;
        ReturnHeldItem();
        return captured;
    }

    /// <summary>The characters whose bag was left open with OPEN BAG: their inventory shows it again when it opens or they are selected.</summary>
    internal readonly HashSet<CharacterId> bagOpen = [];

    /// <summary>OPEN BAG: opens or closes the lead character's bag and remembers it for that character.</summary>
    internal void ToggleBag()
    {
        if (InventoryOwner() is not { } lead) return;
        if (!bagOpen.Remove(lead.Id)) bagOpen.Add(lead.Id);
        if (player.tradeWindows.lootOpenedBag == lead.Id) player.tradeWindows.lootOpenedBag = CharacterId.None;
        SyncBagScreen();
    }

    /// <summary>The character the limbs window was opened for (it closes when the inventory shows another, as the original's goes with its character's inventory).</summary>
    internal CharacterId limbsOwner = CharacterId.None;

    /// <summary>
    /// Whether the character has the original's limb holder, which enables the LIMBS button (<c>FUN_140645690</c>: made the first time a limb changes). Meitou: a limb that is not
    /// the original now (a stump, a prosthetic or a crushed limb), which no limb ever goes back from.
    /// </summary>
    internal static bool HasLimbWindow(CharacterSnapshot c) => c.LimbBits != 0;

    /// <summary>The character the limbs window shows: the one it was opened for (the inventory's or the open loot's), else the inventory owner.</summary>
    CharacterSnapshot? LimbsCharacter() =>
        (!limbsOwner.IsNone ? player.session.CurrentSnapshot.Characters.FirstOrDefault(c => c.Id == limbsOwner) : null) ?? InventoryOwner();

    /// <summary>
    /// LIMBS (<c>FUN_14070d1e0</c>), on the inventory window or on the open loot's (<paramref name="who"/>, the looted body, as the original's loot window has the button too): opens
    /// the limbs window for that character, or closes it when it is open for them.
    /// </summary>
    internal void ToggleLimbs(CharacterSnapshot? who = null)
    {
        who ??= InventoryOwner();
        bool open = player.faithfulScreens.IsOpen("limbs");
        if (open) player.faithfulScreens.Close("limbs");
        if (open && who?.Id == limbsOwner || who is null || !HasLimbWindow(who)) return;
        limbsOwner = who.Id;
        player.faithfulScreens.Open("limbs");
        player.PlaceLimbsWindow();
    }

    /// <summary>Once a frame: the limbs window closes with the window of the character it shows (the inventory, or the open loot), or when that window shows another character.</summary>
    internal void SyncLimbsScreen()
    {
        if (!player.faithfulScreens.IsOpen("limbs")) return;
        bool shown = player.faithfulScreens.IsOpen("inventory") && InventoryOwner()?.Id == limbsOwner || player.faithfulScreens.IsOpen("loot") && player.tradeWindows.LootBody()?.Id == limbsOwner;
        if (!shown) player.faithfulScreens.Close("limbs");
    }

    /// <summary>Once a frame: the bag window is open while the inventory is, the lead character wears a bag and left it open (<see cref="bagOpen"/>).</summary>
    void SyncBagScreen()
    {
        var lead = InventoryOwner();
        bool want = player.faithfulScreens.IsOpen("inventory") && lead is { ItemGrid: { BagWidth: > 0, BagHeight: > 0 } } && bagOpen.Contains(lead.Id);
        if (want == player.faithfulScreens.IsOpen("bag")) return;
        if (!want) { player.faithfulScreens.Close("bag"); if (player.tradeWindows.shownTrader.IsNone && player.faithfulScreens.IsOpen("loot")) player.PlaceWindowRow(); return; }
        player.faithfulScreens.Open("bag");
        if (player.tradeWindows.shownTrader.IsNone) player.PlaceBagWindow(); else player.tradeWindows.PlaceTradeWindows();
    }

    /// <summary>
    /// Once a frame: the held item is the world's again once its last command was applied (or after <see cref="HeldWaitSeconds"/>), and goes back when no item window is open.
    /// </summary>
    internal void ValidateInventoryDrag()
    {
        SyncBagScreen();
        var snapshot = player.session.CurrentSnapshot;
        if (heldSerial != 0 && snapshot.HeldSerial != heldSerial && Stopwatch.GetElapsedTime(heldSentAt).TotalSeconds < HeldWaitSeconds) return;
        heldSerial = 0;
        lifting = null;
        var was = held;
        held = snapshot.Held;
        if (held is { } h && (was is null || was.Width != h.Width || was.Height != h.Height)) heldGrab = ClampGrab(heldGrab, h);
        if (held is not null && !player.faithfulScreens.HasModal && !ItemScreens.Any(player.faithfulScreens.IsOpen) && pinnedInventories.Count == 0) ReturnHeldItem();
    }
}
