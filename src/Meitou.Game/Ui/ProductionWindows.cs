using System.Globalization;
using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Data.Ui;
using Meitou.Game.Rendering.Ui;
using Meitou.Game.Tutorials;
using Meitou.Simulation;
using Meitou.Simulation.Items;
using Meitou.Simulation.Production;

namespace Meitou.Game.Ui;

/// <summary>
/// Production in the UI (docs/simulation.md "Production", docs/game/ui-screens.md "Selected building"):
/// <list type="bullet">
/// <item>Selecting a complete machine of the player's opens its window at the left edge, vertically centred: a well or farm the original's
/// <c>Kenshi_GenericBuildingWindow</c> (inputs in <c>in1</c> / <c>in2</c>, the output in <c>out</c>, their bars and texts), a research bench
/// <c>Kenshi_InventoryResearchWindow</c> (its storage grid; the RESEARCH button opens the overview's research tab). Items are held with the mouse
/// between the windows and the selected character's inventory (<see cref="InventoryWindows"/>), or moved with a right click
/// (<see cref="PutInMachine"/>, <see cref="TakeFromMachine"/>).</item>
/// <item>The site menu has Operate for a machine characters work; a quick right click on one gives it.</item>
/// <item>The overview's research tab: TYPES lists the categories, BLUEPRINTS the research of the selected category that can be queued (a click queues
/// it), QUEUE the queue (a click takes one off), DESCRIPTION the hovered or last clicked research. The original's row widgets and colours are
/// <b>Unknown</b>; Meitou draws plain text rows.</item>
/// </list>
/// </summary>
sealed class ProductionWindows(PlayerInterface player)
{
    /// <summary>
    /// The site whose machine window is shown (null: none), and a site whose window the player closed (not reopened until the selection changes).
    /// </summary>
    internal string? ShownMachine, ClosedMachine;

    /// <summary>
    /// The research tab's selected category, and the blueprint whose description shows.
    /// </summary>
    internal string? ResearchCategory, ResearchFocus;

    /// <summary>
    /// The machine at site <paramref name="site"/> in the snapshot; null for none.
    /// </summary>
    internal MachineSnapshot? Machine(string? site) =>
        site is null ? null : player.Session.CurrentSnapshot.Machines.FirstOrDefault(m => m.Site == site);

    /// <summary>
    /// The storage window's layout pixels above its grid (caption and item heading) and in all around it, with the two-line Max Capacity text below
    /// (measured on the original's window).
    /// </summary>
    const float StorageAbove = 86, StorageChrome = 150;

    /// <summary>
    /// The window a machine opens: the building window (inputs, an output or no storage), the storage window (a plain storage), else the bench
    /// window.
    /// </summary>
    internal static string MachineScreen(MachineSnapshot m) =>
        m.Storage is null || m.Inputs.Count > 0 || m.Output is not null ? "building"
        : m.IsStorage && !m.IsContainer && m.TechLevel == 0 ? "storage"
        : "bench";

    /// <summary>
    /// Opens the selected machine's window and closes it when the selection moves on.
    /// </summary>
    internal void SyncMachineScreens()
    {
        if (player.FaithfulScreens is null) return;

        var focus = player.BuildingSelection.BuildingFocus();

        // A rock's natural site is no registry building: its machine, once mined, is enough.
        var m = SiteMachine(focus?.SiteId);
        if (m is not null && m.Inputs.Count == 0 && m.Output is null && m.Storage is null && m.Craft is null) m = null;

        // An open container takes the window.
        m = player.Session.CurrentSnapshot.Machines.FirstOrDefault(c => c.IsContainer) ?? m;
        if (ClosedMachine is not null && ClosedMachine != m?.Site) ClosedMachine = null;

        if (ShownMachine is not null && (m is null || m.Site != ShownMachine))
        {
            player.FaithfulScreens.Close("building");
            player.FaithfulScreens.Close("bench");
            player.FaithfulScreens.Close("storage");
            ShownMachine = null;
            player.Build.StorageWithInventory = null;
        }

        if (ShownMachine is not null && Machine(ShownMachine) is { } shown && !player.FaithfulScreens.IsOpen(MachineScreen(shown)))
        {
            if (shown.IsContainer) player.Say(new CloseContainer { Tick = player.Session.World.Tick });

            ClosedMachine = ShownMachine;
            ShownMachine = null;
            player.Build.StorageWithInventory = null;
            return;
        }

        if (m is null || ShownMachine == m.Site || ClosedMachine == m.Site) return;

        ShownMachine = m.Site;
        string screen = MachineScreen(m);
        player.ScrollBars.InventoryScroll.Remove(screen);

        // The character's inventory opens with an order-driven container window (the original's order 26 opens the looter's own window and the
        // target's together) and with a machine's; selecting a selected machine, storage, bench or mined rock shows its window alone (the original's
        // windows of a selected ore storage and electrical workbench have no inventory beside them).
        bool alone = !m.IsContainer && player.Build.StorageWithInventory != m.Site;
        if (!alone && player.Selected().Count > 0 && !player.FaithfulScreens.IsOpen("inventory"))
            player.FaithfulScreens.Open("inventory");

        player.FaithfulScreens.Open(screen);
        player.InventoryLayout.PlaceMachineWindow(screen);
    }

    /// <summary>
    /// The machine of a focused site: a natural site's (a rock's, once mined, is no registry building) or a complete building's; null for any other.
    /// </summary>
    MachineSnapshot? SiteMachine(string? site)
    {
        if (site is not { } id) return null;

        bool built = ProductionSystem.IsNatural(id) || player.Session.CurrentSnapshot.Buildings.FirstOrDefault(s => s.Id == id) is { Complete: true };
        return built ? Machine(id) : null;
    }

    /// <summary>
    /// Binds the shown machine's window: the storage window, a research bench's or the generic building window with its inputs and output.
    /// </summary>
    internal void BindMachineScreen(string id, UiBindings b)
    {
        var m = Machine(ShownMachine);

        if (id == "storage")
        {
            BindStorageScreen(m, b);
            return;
        }

        string caption = m is { IsContainer: true }
            ? player.Build.Services?.ItemName(m.Record) ?? "CONTAINER"
            : player.BuildingSelection.Selected?.Name ?? "BUILDING";
        b.Text["Root"] = caption.ToUpperInvariant();

        if (id == "bench")
        {
            BindBenchScreen(m, b);
            return;
        }

        BindCraftingRow(m, b);
        b.TemplateScale = player.InventoryScale;

        // The layout's OutputText is an editable EditBox; it is a status line, so read-only here: a click or Tab gave it the keys and typing went
        // into it (#383).
        b.Properties["OutputText"] = new Dictionary<string, string> { ["ReadOnly"] = "true" };
        if (m is null) return;

        BindInputs(m, b);
        BindOutput(m, b);
        BindColumnTitles(m, b);
    }

    /// <summary>
    /// The original's generic container window: caption STORAGE, the limited item as heading, and Max Capacity (red "(Full)" once the stack is
    /// full).
    /// </summary>
    void BindStorageScreen(MachineSnapshot? m, UiBindings b)
    {
        b.Text["Root"] = "STORAGE";
        b.Text["TypeText"] = m?.StorageLimitName ?? "";

        if (m is { StorageCapacity: > 0 } cap)
        {
            bool full = m.Storage?.Items.FirstOrDefault()?.Quantity >= cap.StorageCapacity;
            string capacity = cap.StorageCapacity.ToString("N0", CultureInfo.InvariantCulture);
            b.Text["CapacityText"] = $"Max Capacity: {capacity}" + (full ? " (Full)" : "");

            // The original paints the whole line red once the stack is full (the exact red is Unknown; read off its screenshot).
            if (full)
                b.Properties["CapacityText"] = new Dictionary<string, string> { ["TextColour"] = "0.91 0.25 0.25" };
        }
        else b.Hidden.Add("CapacityText");

        b.Disabled.Remove("ArrangeButton");
        b.Hidden.Add("ArrangeButton");

        if (m?.Storage is { } sg)
        {
            b.Positions["Root"] = new(0, 0, sg.Width * player.InventoryCell + 40 * player.InventoryScale,
                Math.Min(player.Height * 0.8f, sg.Height * player.InventoryCell + StorageChrome * player.InventoryScale));
        }

        b.TemplateScale = player.InventoryScale;
    }

    /// <summary>
    /// A research bench's window: the RESEARCH button (hidden below tech level 1) and the window sized around its storage grid.
    /// </summary>
    void BindBenchScreen(MachineSnapshot? m, UiBindings b)
    {
        b.Disabled.Remove("ResearchButton");
        b.Actions["ResearchButton"] = "ResearchButton";
        if (m is not { TechLevel: > 0 }) b.Hidden.Add("ResearchButton");

        b.Disabled.Remove("ArrangeButton");
        b.Hidden.Add("ArrangeButton");

        if (m?.Storage is { } grid)
        {
            b.Positions["Root"] = new(0, 0, grid.Width * player.InventoryCell + 40 * player.InventoryScale,
                Math.Min(player.Height * 0.8f, grid.Height * player.InventoryCell + StorageChrome * player.InventoryScale));
        }

        b.TemplateScale = player.InventoryScale;
    }

    /// <summary>
    /// A crafting bench's row (the current entry's name and the queue button); hidden for any other machine.
    /// </summary>
    static void BindCraftingRow(MachineSnapshot? m, UiBindings b)
    {
        if (m?.Craft is { } craft)
        {
            // CraftingBuilding::updateInventoryWindow: the current entry's name in #afa68b (an empty name with nothing current).
            b.Text["CraftingName"] = "#afa68b" + (CraftCurrent(craft)?.Name ?? "");
            b.Disabled.Remove("QueueButton");
            b.Actions["QueueButton"] = "QueueButton";
        }
        else b.Hidden.Add("CraftingPanel");
    }

    /// <summary>
    /// The building window's two input panels: a furnace's grid, an input's name, status and bar, or the panel hidden.
    /// </summary>
    void BindInputs(MachineSnapshot m, UiBindings b)
    {
        for (int k = 0; k < 2; k++)
        {
            string n = (k + 1).ToString(CultureInfo.InvariantCulture);

            if (k == 0 && FurnaceGrid(m) is { } fg)
            {
                BindFurnaceInput(fg, b);
                continue;
            }

            if (k >= m.Inputs.Count)
            {
                b.Hidden.Add($"Input{n}Panel");
                continue;
            }

            var input = m.Inputs[k];
            b.Text[$"Input{n}Text"] = input.Name;

            if (m.Craft is not null)
            {
                // A crafting bench's input: "Empty!" for an empty section with nothing taken in, else "OK" (a section holds only its ingredient here,
                // so never "Invalid item!").
                bool empty = input.Count <= 0 && input.Fill <= 0;
                b.Text[$"Input{n}StatusText"] = empty ? "#afa68b" + Loc.Tr("Empty!") : HudPanels.TipGood + Loc.Tr("OK");
            }
            else
            {
                b.Text[$"Input{n}StatusText"] =
                    $"{input.Count.ToString(CultureInfo.InvariantCulture)}/{input.Capacity.ToString(CultureInfo.InvariantCulture)}";
            }

            b.Progress[$"Input{n}ProgressBar"] = input.Fill;
        }
    }

    /// <summary>
    /// A furnace's one input is its 12 x 7 grid (FurnaceBuilding::setupFromData), drawn in in1 by AddMachineContent; its label, status and bar are
    /// an engine choice (left empty).
    /// </summary>
    void BindFurnaceInput(InventoryView fg, UiBindings b)
    {
        b.Text["Input1Text"] = "";
        b.Text["Input1StatusText"] = "";
        b.Hidden.Add("Input1ProgressBar");

        // Widen and deepen the window to hold the grid, measured off the last frame's slot (the original's sizing is Unknown).
        if (player.FaithfulScreens.LastFrame.FirstOrDefault(f => f.Id == "building")?.Controls is { } last
            && last.FirstOrDefault(c => c.Type == "Window") is { } win
            && last.FirstOrDefault(c => c.Name == "in1") is { } in1)
        {
            b.Positions["Root"] = new(0, 0, Math.Max(win.Bounds.Width,
                in1.Bounds.X - win.Bounds.X + fg.Width * player.InventoryCell + 20 * player.InventoryScale),
                Math.Max(win.Bounds.Height, in1.Bounds.Y - win.Bounds.Y + fg.Height * player.InventoryCell + 60 * player.InventoryScale));
        }
    }

    /// <summary>
    /// The building window's output: its stock and bar (a farm's ripeness), a bench's current craft, or the column hidden.
    /// </summary>
    static void BindOutput(MachineSnapshot m, UiBindings b)
    {
        if (m.Output is { } output)
        {
            string count = output.Count.ToString(CultureInfo.InvariantCulture);
            string capacity = output.Capacity.ToString(CultureInfo.InvariantCulture);
            b.Text["OutputText"] = $"{output.Name}\n{count}/{capacity}";
            b.Progress["OutputProgressBar"] = m.Farm is { } farm ? farm.Ripeness : output.Fill;
        }
        else if (m.Craft is { } done)
        {
            // The current entry's item name and progress, else "-" and 0.
            var current = CraftCurrent(done);
            b.Text["OutputText"] = current?.Name ?? "-";
            b.Progress["OutputProgressBar"] = current?.Progress ?? 0;
        }
        else b.Hidden.Add("OutputColumn");
    }

    /// <summary>
    /// BuildInventoryLayout's constructor (FUN_140155f90) sets both column titles: "Input" / "Inputs" by the number of inputs (hidden at 0) and
    /// "Output", over the layout's INPUTS / OUTPUT.
    /// </summary>
    void BindColumnTitles(MachineSnapshot m, UiBindings b)
    {
        int inputCount = FurnaceGrid(m) is not null ? 1 : m.Inputs.Count;
        if (inputCount > 0) b.Text["InputTitleText"] = Loc.Plural("Input", "Inputs", inputCount);

        b.Text["OutputTitleText"] = Loc.Tr("Output");
        if (m.Inputs.Count == 0 && FurnaceGrid(m) is null) b.Hidden.Add("InputTitleText");
        if (m is { Inputs.Count: 0, Craft: null, Output: not null } && FurnaceGrid(m) is null) OutputOnlyGeometry(b);
    }

    /// <summary>
    /// Engine choice for a machine with only an output (a mined rock): the layout's empty input column and crafting row are dropped, the output
    /// column takes the window's width and the window shrinks around it, its output panel kept near the layout's size (the original's window is the
    /// full layout, mostly empty).
    /// </summary>
    void OutputOnlyGeometry(UiBindings b)
    {
        const float widthPart = 0.52f, heightPart = 0.6f;
        var layout = player.FaithfulScreens.Layout("building");
        b.Hidden.Add("CraftingPanel");
        b.Hidden.Add("InputColumn");
        b.Positions["Root"] = new(0, 0, 0.208854f * player.Width * widthPart, 0.398148f * player.Height * heightPart);
        float x = 0.0126582f / widthPart, w = 1 - 2 * x, titles = 0.0909091f / heightPart;
        if (layout.Find("TitlesPanel") is { } t) b.RelativePositions[t] = new(x, 0, w, titles);
        if (layout.Find("OutputTitleText") is { } ot) b.RelativePositions[ot] = new(0, 0, 1, 1);
        if (layout.Find("InputOutputPanel") is { } io) b.RelativePositions[io] = new(x, titles, w, 1 - titles - 0.03f);
        if (layout.Find("OutputColumn") is { } oc) b.RelativePositions[oc] = new(0, 0, 1, 1);
        if (layout.Find("OutputPanel") is { } op) b.RelativePositions[op] = new(0, 0, 1, 1);
    }

    /// <summary>
    /// A furnace's item grid (its <c>in1</c> section, kept as the machine's storage); null for any other machine.
    /// </summary>
    internal static InventoryView? FurnaceGrid(MachineSnapshot? m) => m is { Furnace: not null, Inputs.Count: 0, Storage: { } grid } ? grid : null;

    /// <summary>
    /// A bench's current craft: the queue's front while its progress is below 1 (docs/game/ui-screens.md "Selected building", crafting window); null:
    /// none.
    /// </summary>
    static (string Key, string Name, float Progress, float Hours)? CraftCurrent(CraftView craft) =>
        craft.Queue is [var front, ..] && front.Progress < 1 ? front : null;

    /// <summary>
    /// The faded picture of the item a slot expects (alpha 0.35), shown while the slot is empty.
    /// </summary>
    void AddItemGhost(List<UiControl> controls, string name, UiRect area, string record, string material, UiRect? clip)
    {
        if (player.ItemCells.ItemIcons?.Choose(record, material) is not { } choice || player.UiPainter is not { } painter)
            return;

        Dictionary<string, string>? image = null;

        if (choice is { Source: ItemIconSource.File, File: { } file } && painter.IconReady(file))
            image = new() { ["ImageTexture"] = UiPainter.IconTexture(file) };
        else if (choice.Source == ItemIconSource.Render && UiPainter.RenderedIconTexture(record, material, choice.Colour) is { } rendered
            && painter.RenderedIconReady(rendered))
        {
            image = new() { ["ImageTexture"] = rendered, ["ImageFlipV"] = "true" };
        }

        if (image is null) return;

        image["Alpha"] = "0.35";
        controls.Add(new($"{name}-ghost", "ImageBox", area, "", "", "", true, image, Vector4.Zero, Pickable: false, Clip: clip));
    }

    /// <summary>
    /// Adds the shown machine's item cells: a bench's or storage's grid, or the building window's input, furnace and output slots.
    /// </summary>
    internal void AddMachineContent(string id, List<UiControl> controls)
    {
        if (Machine(ShownMachine) is not { } m) return;

        if (id is "bench" or "storage")
        {
            AddStorageGrid(id, controls, m);
            return;
        }

        for (int k = 0; k < Math.Min(2, m.Inputs.Count); k++) AddSlot(controls, m, $"in{k + 1}", $"Input{k + 1}ProgressBar", m.Inputs[k], 10 + k);

        if (FurnaceGrid(m) is { } fg && m.Furnace is { } burn) AddFurnaceGrid(controls, m, fg, burn);

        AddOutputSlot(controls, m);
    }

    /// <summary>
    /// A bench's or storage's grid and its items, placed in the window's <c>backpack_content</c>.
    /// </summary>
    void AddStorageGrid(string id, List<UiControl> controls, MachineSnapshot m)
    {
        if (m.Storage is not { } grid) return;

        player.InventoryLayout.ArrangeContainerGrid(id, controls, grid.Width, grid.Height, StorageAbove, StorageChrome - StorageAbove - 6);
        if (id == "bench") PlaceResearchButton(controls);
        if (controls.FirstOrDefault(c => c.Name == "backpack_content") is not { } slot) return;

        var area = slot.Bounds;

        foreach (var item in grid.Items)
        {
            if (player.InventoryWindows.Lifted(ItemOwner.AtSite(m.Site), 1000 + item.Index)) continue;

            player.ItemCells.AddItemCell(controls, $"machine-item-{item.Index}",
                new(area.X + item.X * area.Width / grid.Width, area.Y + item.Y * area.Height / grid.Height, item.Width * area.Width / grid.Width,
                    item.Height * area.Height / grid.Height),
                item.Record, item.Material, item.Name, item.Quantity, TooltipRuntime.ItemTip.Of(item), "MachineItem", 1000 + item.Index,
                slot.Bounds.Intersect(slot.Clip ?? slot.Bounds), charge: item.Charge, colour: item.Colour);
        }
    }

    /// <summary>
    /// The research button keeps its height under HCenter Top alignment when the bench grows to fit its grid (#434).
    /// </summary>
    void PlaceResearchButton(List<UiControl> controls)
    {
        int index = controls.FindIndex(c => c.Name == "ResearchButton");
        var root = controls.FirstOrDefault(c => c.Type == "Window");
        if (index < 0 || root is null) return;

        float scale = player.InventoryScale;
        var bounds = root.Bounds;
        controls[index] = controls[index] with
        {
            Bounds = new(bounds.X + 18 * scale, bounds.Y + 50 * scale, Math.Max(0, bounds.Width - 40 * scale), 32 * scale),
        };
    }

    /// <summary>
    /// One input or output section, sized to its item's footprint, with its item (or a bench's faded ghost of the expected item).
    /// </summary>
    void AddSlot(List<UiControl> controls, MachineSnapshot m, string name, string bar, MachineSlotView view, int code)
    {
        int index = controls.FindIndex(c => c.Name == name);
        if (index < 0) return;

        // The original makes the section the recipe item's footprint (a box of building material is 6 x 3 cells); the grid keeps the layout's top
        // and takes the progress bar's horizontal centre (the layout slot is not centred in its column), so it stays under its label and bar. A
        // grid wider than the bar keeps the slot's left edge, so it overflows only to the right as before rather than into the column on its
        // left.
        var slot = controls[index];
        var barRoot = controls.FirstOrDefault(c => c.Name == bar);
        float width = view.Width * player.InventoryCell;
        var column = barRoot?.Bounds ?? slot.Bounds;
        float x = width <= column.Width ? column.X + (column.Width - width) / 2 : slot.Bounds.X;
        var b = slot.Bounds with { X = x, Width = width, Height = view.Height * player.InventoryCell };

        // The grid and a moved bar may leave their content panel, so they clip to the window instead.
        var clip = controls.FirstOrDefault(c => c.Type == "Window")?.Bounds ?? slot.Clip;
        controls[index] = slot with
        {
            Bounds = b,
            Clip = clip,
            Properties = new Dictionary<string, string>(slot.Properties)
            {
                ["TileSize"] = FormattableString.Invariant($"{player.InventoryCell} {player.InventoryCell}"),
                ["State"] = "normal",
            },
        };

        // Engine choice: a grid taller than the room above the progress bar pushes the bar down under it.
        if (barRoot is { } root && b.Y + b.Height + 4 * player.InventoryScale - root.Bounds.Y is var dy and > 0)
        {
            for (int i = 0; i < controls.Count; i++)
            {
                if (controls[i].Name == bar || controls[i].Name == bar + "@track"
                    || controls[i].Properties.GetValueOrDefault("TemplateOwner") == bar)
                {
                    controls[i] = controls[i] with { Bounds = controls[i].Bounds with { Y = controls[i].Bounds.Y + dy }, Clip = clip };
                }
            }
        }

        if (view.Count <= 0 && m.Craft is not null)
            AddItemGhost(controls, $"machine-{name}", b, view.Item, "", clip);
        if (view.Count <= 0 || player.InventoryWindows.Lifted(ItemOwner.AtSite(m.Site), code)) return;

        player.ItemCells.AddItemCell(controls, $"machine-{name}", b, view.Item, "", view.Name, view.Count, null, "MachineItem", code, clip);
    }

    /// <summary>
    /// A furnace's input grid and its items.
    /// </summary>
    void AddFurnaceGrid(List<UiControl> controls, MachineSnapshot m, InventoryView fg, FurnaceView burn)
    {
        // The furnace's grid in in1, its items the storage's (picked up as storage items); disabled while it burns
        // (InventorySection::setEnabled(!active)).
        AddSlot(controls, m, "in1", "Input1ProgressBar", new MachineSlotView("", "", 0, 0, 0, fg.Width, fg.Height), -1);
        int gi = controls.FindIndex(c => c.Name == "in1");

        if (gi >= 0)
        {
            var g = controls[gi];
            if (burn.Burning)
                controls[gi] = g with { Properties = new Dictionary<string, string>(g.Properties) { ["State"] = "disabled" } };

            foreach (var item in fg.Items)
            {
                if (player.InventoryWindows.Lifted(ItemOwner.AtSite(m.Site), 1000 + item.Index)) continue;

                player.ItemCells.AddItemCell(controls, $"machine-item-{item.Index}",
                    new(g.Bounds.X + item.X * player.InventoryCell, g.Bounds.Y + item.Y * player.InventoryCell, item.Width * player.InventoryCell,
                        item.Height * player.InventoryCell),
                    item.Record, item.Material, item.Name, item.Quantity, TooltipRuntime.ItemTip.Of(item), "MachineItem", 1000 + item.Index,
                    g.Clip, charge: item.Charge, colour: item.Colour);
            }
        }
    }

    /// <summary>
    /// The output section: the machine's output slot, a bench's finished craft, or the current craft's item faded in the empty output.
    /// </summary>
    void AddOutputSlot(List<UiControl> controls, MachineSnapshot m)
    {
        if (m.Output is { } output) AddSlot(controls, m, "out", "OutputProgressBar", output, 8);
        else if (m.Craft is { Done: [var made, ..] } && controls.FirstOrDefault(c => c.Name == "out") is { } outSlot
            && !player.InventoryWindows.Lifted(ItemOwner.AtSite(m.Site), 8))
        {
            var ob = outSlot.Bounds;
            float side = MathF.Min(ob.Width, ob.Height);
            player.ItemCells.AddItemCell(controls, "machine-out", new(ob.X, ob.Y, side, side), made.Record, made.Material, made.Name, made.Quantity,
                null, "MachineItem", 8, outSlot.Clip, colour: made.Colour);
        }
        else if (m.Craft is { Done: [] } craft && CraftCurrent(craft) is { } current && controls.FirstOrDefault(c => c.Name == "out") is { } emptyOut)
        {
            // The empty output shows the current craft's item, faded.
            var ob = emptyOut.Bounds;
            float side = MathF.Min(ob.Width, ob.Height);
            string[] key = current.Key.Split('|');
            AddItemGhost(controls, "machine-out", new(ob.X, ob.Y, side, side), key[0], key.Length > 1 ? key[1] : "", emptyOut.Clip);
        }
    }

    /// <summary>
    /// A machine window event: the bench's RESEARCH opens the overview's research tab, the building's QUEUE its Craft tab.
    /// </summary>
    internal void MachineAction(MyGuiEvent e)
    {
        if (e.Screen == "bench" && e.Widget == "ResearchButton")
        {
            player.FaithfulScreens.Open("overview");
            player.FaithfulScreens.SetTab("overview", "TabsMain", TutorialPanel.ResearchTab);
        }
        else if (e.Screen == "building" && e.Widget == "QueueButton" && ShownMachine is not null)
            OpenCraftTab(ShownMachine);
    }

    /// <summary>
    /// The verb for a complete machine characters work: the original's Use (order 87, <c>FUN_14079b2d0</c>); with Shift held on the click it becomes
    /// the Operating machine job, as the original's dispatch turns a menu order into a job (<c>FUN_1407fa650</c>).
    /// </summary>
    internal void AddMachineVerb(List<(string Caption, Action Give)> verbs, Simulation.Construction.BuildingSnapshot site,
        IReadOnlyList<CharacterId> selection)
    {
        // An automatic machine: Auto-Haul (152) in place of Use; with Shift the 152 job (its companion 124 follows), else a one-shot haul round (the
        // operator only hauls there).
        if (site.Complete && Machine(site.Id) is { Operable: true, IsAutomatic: true })
        {
            verbs.Add(("Auto-Haul", () => player.Say(player.SlowModifier
                ? (SimCommand)new AddJob(selection, Data.Gameplay.Ai.TaskType.OperateAutomaticMachinery, site.Id) { Tick = player.Session.World.Tick }
                : new OperateOrder(selection, site.Id) { Tick = player.Session.World.Tick })));
        }
        else if (site.Complete
            && Machine(site.Id) is { Operable: true }
                or { Function: Data.World.TownBuildings.ChairFunction or Data.World.TownBuildings.ThroneFunction })
        {
            verbs.Add(("Use", () => player.Say(player.SlowModifier
                ? (SimCommand)new AddJob(selection, Data.Gameplay.Ai.TaskType.OperateMachinery, site.Id) { Tick = player.Session.World.Tick }
                : new OperateOrder(selection, site.Id) { Tick = player.Session.World.Tick })));
        }
        // A training dummy: Training (97 USE_TRAINING_DUMMY, UseableStuff slot 131 for function 7), which Shift turns into the Training job.
        else if (site.Complete && Machine(site.Id) is { Function: Data.Gameplay.Construction.MachineData.TrainingFunction })
        {
            verbs.Add(("Training", () => player.Say(player.SlowModifier
                ? (SimCommand)new AddJob(selection, Data.Gameplay.Ai.TaskType.UseTrainingDummy, site.Id) { Tick = player.Session.World.Tick }
                : new OperateOrder(selection, site.Id) { Tick = player.Session.World.Tick })));
        }
    }

    /// <summary>
    /// The first stats line of a mine, farm, well or rock (<c>ProductionBuilding::getGUIFertility</c>, docs/game/ui-screens.md "Selected building"):
    /// the resource's label, the yield as a whole percent without the sign, red to green (r = 0.8 - y, g = 0.8 y, b = 0.1), and the tooltip naming
    /// the resource in lower case.
    /// </summary>
    internal static void AddRichnessLine(RichnessView rich, List<PlayerInterface.HudLine> stats)
    {
        (string label, string name) = rich.Resource switch
        {
            1 => ("Iron Quality:", "Iron"),
            2 => ("Stone Richness:", "Stone"),
            3 => ("Copper Quality:", "Copper"),
            4 => ("Carbon Quality:", "Carbon"),
            5 => ("Water Available:", "Water"),
            _ => ("Ground Fertility:", "Fertility"),
        };

        static int Byte(float v) => (int)(Math.Clamp(v, 0, 1) * 255 + 0.5f);

        string colour = $"{Byte(0.8f - rich.Yield):x2}{Byte(0.8f * rich.Yield):x2}{Byte(0.1f):x2}";
        stats.Add(new(label, ((int)(rich.Yield * 100)).ToString(CultureInfo.InvariantCulture), PlayerInterface.MainColour, colour,
            Loc.Tr("The richness of natural {1} present in this location. Anything over 50% is considered good.",
                Loc.Tr(name).ToLower(CultureInfo.CurrentCulture))));
    }

    /// <summary>
    /// Miles per hour per world unit per second (the original's 0.22369362: a unit is a metre).
    /// </summary>
    internal const float MphPerUnit = 0.22369362f;

    /// <summary>
    /// More of the exe's static colour tags (with MainTag and TitleTag): tooltip texts carry them as text, so they are the tags, not the palette's
    /// colours.
    /// </summary>
    internal const string InkTag = "#140806", BadTag = "#59231a", GoodTag = "#a8b774";

    /// <summary>
    /// The status word of a production machine (<c>ProductionBuilding</c> slot 114, <c>FUN_140300090</c>): Output full (status 1), Out of resource
    /// (status 0 with an empty input), then No workers while nobody works a machine that takes workers, else Working. Its other words (Switched off,
    /// No power) come from the power lines.
    /// </summary>
    internal static (string Status, bool Bad) ProductionStatus(byte machineStatus, EfficiencyView e, bool inputsEmpty = false) =>
        machineStatus == 1 ? ("Output full", true) : machineStatus == 0 && inputsEmpty ? ("Out of resource", true)
            : e.MaxOperators > 0 && e.Operators == 0 ? ("No workers", true) : ("Working", false);

    /// <summary>
    /// The Workers line (<c>FUN_1403075b0</c>): <c>-</c> for an automatic machine (raw <c>max operators</c> 0), else operators / max; the value in
    /// ink, Bad while nobody works it; the tooltip "fully staffed" when they match, "under-staffed" (Title) below.
    /// </summary>
    internal static PlayerInterface.HudLine WorkersLine(int operators, int max)
    {
        if (max <= 0)
            return new("Workers:", "-", PlayerInterface.MainColour, Tooltip: "This machine is fully automatic.");

        string? tip = operators == max ? Loc.Tr("The machine is fully staffed.")
            : operators < max ? TooltipRuntime.TitleTag + Loc.Tr("The machine is under-staffed.")
            : null;
        return new("Workers:", $"{operators.ToString(CultureInfo.InvariantCulture)}/{max.ToString(CultureInfo.InvariantCulture)}",
            PlayerInterface.MainColour, operators == 0 ? PlayerInterface.BadColour : null, tip);
    }

    /// <summary>
    /// The Efficiency and Max efficiency lines (slot 154 <c>FUN_140306050</c>, docs/game/ui-screens.md "Selected building", Production building):
    /// with share = operators / raw max (1 when 0), work = health x power x share x 100 and max = yield x rate, Efficiency is int(max x work)% (ink
    /// from work 90 up, else Bad) and Max efficiency int(max x 100)% (ink). Their tooltips are the slot 156 rows (Max possible efficiency; for a mine
    /// or rock, <paramref name="resource"/> under ground) and the losses, then Total. <paramref name="active"/> false (slot 164: a light by day)
    /// zeroes work; the Damage and Power rows read their own values and stay as they are.
    /// </summary>
    internal static void AddEfficiencyLines(EfficiencyView e, int resource, List<PlayerInterface.HudLine> stats, bool active = true)
    {
        float share = e.MaxOperators > 0 ? 1f / e.MaxOperators * e.Operators : 1;

        // Slot 109: a rain collector's is x the rain strength (FUN_140298470).
        float work = (active ? e.Health * e.Power * (e.Rain ?? 1) : 0) * share * 100;
        float max = e.Yield * e.Rate;
        int total = (int)(max * work);

        // Slot 156: a rain collector's rain strength (FUN_14030b460, Bad at 0); else (FUN_14030a130) the rate, then the resource's loss or gain, in
        // Bad when the yield is under 1.
        var head = e.Rain is { } rain ? [(Loc.Tr("Rain strength:"), (rain > 0 ? TooltipRuntime.MainTag : BadTag) + Pc((int)(rain * 100)))]
            : new List<(string, string)> { (Loc.Tr("Max possible efficiency:"), TooltipRuntime.MainTag + Pc((int)(e.Rate * 100))) };
        string? under = resource switch
        {
            1 => "Iron under ground:",
            2 => "Stone under ground:",
            3 => "Copper under ground:",
            4 => "Carbon under ground:",
            5 => "Water under ground:",
            6 => "Ground Fertility:",
            _ => null,
        };

        if (under is not null)
        {
            int loss = (int)((1 - e.Yield) * e.Rate * 100);
            head.Add((Loc.Tr(under), (e.Yield < 1 ? BadTag : InkTag) + (loss < 0 ? "+" : "") + Pc(-loss)));
        }

        string Loss(float kept) => kept >= 1 ? GoodTag + Loc.Tr("OK") : BadTag + "-" + Pc((int)((1 - kept) * e.Rate * 100));

        var rows = new List<(string, string)>(head) { (Loc.Tr("Damage:"), Loss(e.Health)) };
        if (e.Consumer) rows.Add((Loc.Tr("Power:"), Loss(e.Power)));

        rows.Add((Loc.Tr("Num workers:"), e.Operators == e.MaxOperators ? GoodTag + Loc.Tr("OK") : Loss(share)));
        rows.Add((Loc.Tr("Total:"), TooltipRuntime.MainTag + Pc(total)));
        stats.Add(new("Efficiency:", Pc(total), PlayerInterface.MainColour, work >= 90 ? null : PlayerInterface.BadColour, TooltipRows: rows));
        stats.Add(new("Max efficiency:", Pc((int)(max * 100)), PlayerInterface.MainColour,
            TooltipRows: [.. head, (Loc.Tr("Total:"), TooltipRuntime.MainTag + Pc((int)(max * 100)))]));
    }

    /// <summary>
    /// <paramref name="v"/> as a percentage text.
    /// </summary>
    static string Pc(int v) => v.ToString(CultureInfo.InvariantCulture) + "%";

    /// <summary>
    /// A farm's Workers line (FarmBuilding slot 155, <c>FUN_1400e0020</c>): until the crop has grown, <c>-</c> with "No workers required until the
    /// crop is ready to harvest.", then the common line.
    /// </summary>
    static PlayerInterface.HudLine FarmWorkersLine(MachineSnapshot m, EfficiencyView e) => m.Farm is { Growth: < 1 }
        ? new("Workers:", "-", PlayerInterface.MainColour, Tooltip: "No workers required until the crop is ready to harvest.")
        : WorkersLine(e.Operators, e.MaxOperators);

    /// <summary>
    /// A hydroponic farm's one Efficiency line (FarmBuilding slot 154, <c>FUN_1400e2d50</c>; other farms draw none): int(max x health x power x 100),
    /// x the <c>drought multiplier</c> while out of water, in ink when health x power reaches 0.9, else Bad. No operator share and no Max efficiency
    /// line; the tooltip has no slot 156 rows, only Damage, Power (a consumer), <c>No Water:</c> (drought - 1, Bad) while out of water, and Total.
    /// </summary>
    static void AddFarmEfficiencyLine(EfficiencyView e, List<PlayerInterface.HudLine> stats)
    {
        float work = e.Health * e.Power, max = e.Yield * e.Rate;
        int total = (int)(work * max * 100 * (e.Starved ? e.Drought : 1));

        string Loss(float kept) => kept >= 1 ? GoodTag + Loc.Tr("OK") : BadTag + "-" + Pc((int)((1 - kept) * max * 100));

        var rows = new List<(string, string)> { (Loc.Tr("Damage:"), Loss(e.Health)) };
        if (e.Consumer) rows.Add((Loc.Tr("Power:"), Loss(e.Power)));
        if (e.Starved) rows.Add((Loc.Tr("No Water:"), BadTag + Pc((int)((e.Drought - 1) * 100))));

        rows.Add((Loc.Tr("Total:"), TooltipRuntime.MainTag + Pc(total)));
        stats.Add(new("Efficiency:", Pc(total), PlayerInterface.MainColour, work * 100 >= 90 ? null : PlayerInterface.BadColour, TooltipRows: rows));
    }

    /// <summary>
    /// A turret's gun lines (<c>GunClassTurret</c> slot 14 <c>FUN_14030d620</c>, section 6; docs/game/ui-screens.md "Production building"): Damage,
    /// Deviation, Range and Aim speed, labels in Main, each with its breakdown. P = 0.8 e + 0.2 (e = health x power), M the mount factor, h the
    /// height factor to the last aim point, D = output value x research. The suffixes are the exe's literal <c>x</c> and <c>*</c>; "Wall bonus" is
    /// int((M - 0.99) x 100), so +1% with no wall (kept); the deviation's skill term is 50 x the Turrets level x 0.01 + 50 x perception, so any
    /// gunner with perception shows 0 (kept); "Total speed" leaves out M while the line has it (kept).
    /// </summary>
    static void AddGunLines(MachineSnapshot m, EfficiencyView e, Simulation.Combat.TurretPanelView g, List<PlayerInterface.HudLine> stats)
    {
        static string I(float v) => ((int)v).ToString(CultureInfo.InvariantCulture);

        static int Round(float v) => (int)MathF.Round(v, MidpointRounding.AwayFromZero);

        static string R(float v) => Round(v).ToString(CultureInfo.InvariantCulture);

        float condition = MathF.Min(e.Health, 1) * 100, supply = MathF.Min(e.Power, 1);
        float p = Simulation.Combat.TurretFormulas.PowerFactor(e.Health * e.Power), h = g.Height, mount = g.Mount, d = m.OutputValue * m.Research;
        float damage = d * p * h * mount, range = p * g.Range * h * mount;
        float deviation = MathF.Max(0, g.Deviation * (1 - (g.UserSkill * 50 + (g.Perception ?? 0) * 50)));

        // The rows the three breakdowns share: Power supply below 1 (Bad), Condition below 99 (Good from 95), the wall bonus (Good above 1) and the
        // height advantage.
        var shared = new List<(string, string)>();
        if (supply < 1) shared.Add((Loc.Tr("Power supply:"), BadTag + Pc((int)(supply * 100))));
        if (condition < 99)
            shared.Add((Loc.Tr("Condition:"), (condition >= 95 ? GoodTag : BadTag) + Pc((int)condition)));

        var wall = (Loc.Tr("Wall bonus:"), (mount > 1 ? GoodTag : BadTag) + "+" + Pc((int)((mount - 0.99f) * 100)));
        var height = (Loc.Tr("Height advantage:"), "+" + Pc((int)((h - 1) * 100)));

        static string Metres(int v) => Loc.Tr("{1,num} m", v);

        stats.Add(new("Damage:", I(damage), PlayerInterface.MainColour,
            TooltipRows: [(Loc.Tr("Default damage:"), I(d)), .. shared, wall, height, (Loc.Tr("Total damage:"), I(damage))]));
        stats.Add(new("Deviation:", R(deviation) + "*", PlayerInterface.MainColour,
            TooltipRows: [(Loc.Tr("Base Accuracy Deviation:"), g.Deviation.ToString("F1", CultureInfo.InvariantCulture) + "*"),
                (Loc.Tr("User Skill:"), I(g.UserSkill * 100)),
                (Loc.Tr("User Accuracy Deviation:"), deviation.ToString("F1", CultureInfo.InvariantCulture) + "*"),
                (Loc.Tr("The amount of accuracy error, in degrees. Higher numbers are poor, 0 is perfect accuracy."), "")]));
        stats.Add(new("Range:", Metres(Round(range * 0.1f)), PlayerInterface.MainColour,
            TooltipRows: [(Loc.Tr("Default range:"), Metres((int)(g.Range * 0.1f))), .. shared, wall, height,
                (Loc.Tr("Total range:"), Metres((int)(range * 0.1f)))]));
        stats.Add(new("Aim speed:", R(p * g.AimSpeed * mount) + "x", PlayerInterface.MainColour,
            TooltipRows: [(Loc.Tr("Default aim speed:"), I(g.AimSpeed) + "x"), .. shared, wall,
                (Loc.Tr("Total speed:"), (p * g.AimSpeed).ToString("F2", CultureInfo.InvariantCulture) + "x")]));
    }

    /// <summary>
    /// The Workers and Efficiency lines of a machine's class (slot 40, docs/game/ui-screens.md "Selected building"): Stats 1 (section 6) gets them in
    /// <paramref name="stats"/>, Stats 2 (section 7, only a turret's) in <paramref name="extended"/>. <paramref name="active"/> is slot 164.
    /// </summary>
    static void AddClassLines(MachineSnapshot m, EfficiencyView e, int resource, List<PlayerInterface.HudLine> stats,
        List<PlayerInterface.HudLine> extended, bool active, Simulation.Combat.TurretPanelView? gun = null)
    {
        switch (m.Panel)
        {
            case MachinePanel.Production:
                stats.Add(WorkersLine(e.Operators, e.MaxOperators));
                AddEfficiencyLines(e, resource, stats, active);
                break;
            case MachinePanel.Farm:
                stats.Add(FarmWorkersLine(m, e));
                if (m.Hydroponic) AddFarmEfficiencyLine(e, stats);
                break;
            case MachinePanel.Bed:
                // UseableStuff slot 40 (FUN_140304e90) for BF_BED: the output value in place of both lines.
                stats.Add(new("Healing bonus:", Loc.Tr("x{1,num}", m.OutputValue), PlayerInterface.MainColour));
                break;
            case MachinePanel.Training:
                stats.Add(WorkersLine(e.Operators, e.MaxOperators));
                stats.Add(new("Max Skill Level:", ((int)m.OutputValue - 1).ToString(CultureInfo.InvariantCulture), PlayerInterface.MainColour,
                    Tooltip: "This is the maximum skill a person can train up to using this equipment. For anyone higher it is a waste of time."));
                break;
            case MachinePanel.Turret:
                // TurretBuilding slot 40 (FUN_14030c2a0): section 6 the gun's lines and Efficiency, section 7 Workers and Efficiency.
                if (gun is { } g) AddGunLines(m, e, g, stats);

                AddEfficiencyLines(e, resource, stats, active);
                extended.Add(WorkersLine(e.Operators, e.MaxOperators));
                AddEfficiencyLines(e, resource, extended, active);
                break;
        }
    }

    /// <summary>
    /// The stats lines of a selected machine: the farm's crop and growth, the output's progress, then (section 6: slots 155, 154, 153) Workers and
    /// Efficiency per <see cref="AddEfficiencyLines"/>, the power lines, and the bench's tech level.
    /// </summary>
    internal void AddMachineLines(MachineSnapshot m, List<PlayerInterface.HudLine> stats, List<PlayerInterface.HudLine> extended, ref string status,
        ref bool bad, RichnessView? rich = null, Simulation.Combat.TurretPanelView? gun = null, bool own = false)
    {
        AddOutputLines(m, stats, ref status, ref bad, rich);

        // RainCollectorBuilding slot 114 (FUN_140301410): unless the output is full, Collecting while it rains, else Not Raining.
        if (m.Efficiency is { Rain: { } rain } && m.Status != 1)
            (status, bad) = rain > 0 ? ("Collecting", false) : ("Not Raining", true);
        if (m.Efficiency is { } e) AddClassLines(m, e, rich?.Resource ?? 0, stats, extended, e.Active, gun);

        // Power, in the original's words where it has them (GeneratorBuilding vfunc_114, vfunc_153); "No power" and "Low power" are Meitou's.
        if (m.Power is { } pw) AddPowerLines(m, pw, stats, ref status, ref bad);

        // getGUIPower section 6 on the player's machine with a power figure: a consumer's Battery toggle, then Power (FUN_140307bf0).
        if (own && m.Power is { } toggled) AddPowerToggles(m, toggled, stats);

        if (m.TechLevel > 0) stats.Add(new("Tech level:", Whole(m.TechLevel), PlayerInterface.MainColour));

        // FurnaceBuilding::getGUIData section 0xf (FUN_14030b750): Total Metals (one decimal, Good from 1), then the Active button with its warning.
        if (m.Furnace is { } furnace) AddFurnaceLines(furnace, stats, own);
    }

    /// <summary>
    /// A whole number as the stats lines print it (invariant).
    /// </summary>
    static string Whole(int v) => v.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// A fraction as a whole percentage, clamped to 0..1 first.
    /// </summary>
    static string Percent(float v) => ((int)(Math.Clamp(v, 0, 1) * 100)).ToString(CultureInfo.InvariantCulture) + "%";

    /// <summary>
    /// The status word and the lines of a machine's product: a mine's or rock's status, a farm's growth, or the output's stock.
    /// </summary>
    static void AddOutputLines(MachineSnapshot m, List<PlayerInterface.HudLine> stats, ref string status, ref bool bad, RichnessView? rich)
    {
        // A mine or rock: the ProductionBuilding status word, and no Output line (the original has none).
        if (m.Farm is null && rich is not null && m.Efficiency is { } mine)
            (status, bad) = ProductionStatus(m.Status, mine, m.InputsEmpty);
        else if (m.Farm is { } f)
        {
            status = f.Status switch
            {
                FarmStatus.NoWater => "No water",
                FarmStatus.OutputFull => "Output full",
                FarmStatus.Harvest => "Ready to harvest",
                _ => "Growing",
            };
            bad = f.Status is FarmStatus.NoWater or FarmStatus.OutputFull;
            stats.Add(new("Growth:", Percent(MathF.Min(f.Growth, 1)), PlayerInterface.MainColour));

            if (f.Growth >= 1)
            {
                stats.Add(new("Ripeness:", Percent(f.Ripeness), PlayerInterface.MainColour,
                    f.Growth >= 2 ? PlayerInterface.BadColour : PlayerInterface.GoodColour));
            }

            if (f.Death > 0)
                stats.Add(new("Dying:", Percent(f.Death), PlayerInterface.MainColour, PlayerInterface.BadColour));
        }
        else if (m.Output is { } o)
        {
            // A production machine (refinery, well) reads FUN_140300090's chain; a bench or furnace keeps Meitou's own words for its status.
            if (m.Craft is null && m.Furnace is null && m.Efficiency is { } producer)
                (status, bad) = ProductionStatus(m.Status, producer, m.InputsEmpty);
            else
            {
                status = m.Status switch { 2 => "Working", 1 => "Output full", _ => "Idle" };
                bad = m.Status == 1;
            }

            stats.Add(new("Output:", $"{o.Name} {Whole(o.Count)}/{Whole(o.Capacity)}", PlayerInterface.MainColour));
        }
    }

    /// <summary>
    /// A powered machine's status word (a generator's or battery's) and its power line: a battery's charge, the wind speed, a generator's output
    /// and fuel use, or a consumer's granted power.
    /// </summary>
    static void AddPowerLines(MachineSnapshot m, PowerView pw, List<PlayerInterface.HudLine> stats, ref string status, ref bool bad)
    {
        static string F(float v) => v.ToString("0.#", CultureInfo.InvariantCulture);

        if (m.Wind is not null || pw.Kind is PowerKind.Generator or PowerKind.Battery)
        {
            // The production chain (FUN_140300090) as far as a finished generator reaches it: Switched off (power on clear), No workers, else
            // Working; then the class's own word.
            bool on = m.Wind?.PowerOn ?? pw.PowerOn;
            (status, bad) = GeneratorWord(m) ?? (!on
                ? ("Switched off", true)
                : m.Efficiency is { MaxOperators: > 0, Operators: 0 } ? ("No workers", true) : ("Working", false));
        }

        if (pw.Kind == PowerKind.Battery)
            stats.Add(new("Charge:", Percent(pw.Store > 0 ? pw.Charge / pw.Store : 0), PlayerInterface.MainColour));
        // getGUIPower section 6: the wind speed at the building.
        else if (m.Wind is { } wind)
        {
            stats.Add(new("Wind Speed", Loc.Tr("{1,num=fix,p=1} mph", wind.Speed * MphPerUnit), PlayerInterface.MainColour,
                Tooltip: "The current wind speed"));
        }
        else if (pw.Kind == PowerKind.Generator)
        {
            stats.Add(new("Power:", F(pw.Demand), PlayerInterface.MainColour));
            if (m.Inputs.Count > 0) stats.Add(new("Fuel Consumption", Percent(pw.Usage), PlayerInterface.MainColour));
        }
        else if (pw.Demand > 0)
        {
            if (pw.Granted < pw.Demand)
            {
                status = pw.Granted <= 0 ? "No power" : "Low power";
                bad = true;
            }

            stats.Add(new("Power:", $"{F(pw.Granted)}/{F(pw.Demand)}", PlayerInterface.MainColour,
                pw.Granted < pw.Demand ? PlayerInterface.BadColour : null));
        }
    }

    /// <summary>
    /// The player's machine's power buttons: a consumer's Battery toggle, then Power.
    /// </summary>
    static void AddPowerToggles(MachineSnapshot m, PowerView toggled, List<PlayerInterface.HudLine> stats)
    {
        if (toggled.Kind == PowerKind.Consumer)
        {
            stats.Add(new("Battery", toggled.GetsBattery ? "On" : "Off", PlayerInterface.MainColour,
                toggled.GetsBattery ? PlayerInterface.GoodColour : PlayerInterface.BadBrightColour,
                Loc.Tr(toggled.GetsBattery
                    ? "This machine will run off of battery power if the main generators should run low or break down."
                    : "This machine will NOT run off of battery power if the main generators should run low or break down."),
                "meitou:machine-battery", ButtonWidth: 0.3f));
        }

        bool on = m.Wind?.PowerOn ?? toggled.PowerOn;
        stats.Add(new("Power", on ? "On" : "Off", PlayerInterface.MainColour, on ? PlayerInterface.GoodColour : PlayerInterface.BadBrightColour,
            Loc.Tr(on ? "This machine is switched On" : "This machine is switched Off"), "meitou:machine-power", ButtonWidth: 0.3f));
    }

    /// <summary>
    /// A furnace's Total Metals line and, on the player's furnace, its Active button.
    /// </summary>
    static void AddFurnaceLines(FurnaceView furnace, List<PlayerInterface.HudLine> stats, bool own)
    {
        stats.Add(new("Total Metals:", ((int)(furnace.Metal * 10) / 10f).ToString("0.#", CultureInfo.InvariantCulture),
            PlayerInterface.MainColour, furnace.Metal >= 1 ? PlayerInterface.GoodColour : PlayerInterface.BadColour));

        if (own)
        {
            stats.Add(new("Active:", furnace.Burning ? "ON" : "OFF", PlayerInterface.MainColour,
                furnace.Burning ? PlayerInterface.GoodColour : PlayerInterface.MainColour,
                Loc.Tr("Warning:") + " " + Loc.Tr("Switching on will incinerate the entire inventory!"), "meitou:machine-incinerate",
                ButtonWidth: 0.4f));
        }
    }

    /// <summary>
    /// The word a generator class sets over the common chain whatever that gave (finished or not, broken or not): GeneratorBuilding
    /// (<c>FUN_140300b40</c>) <c>Out of fuel</c> (slot 149), then for a battery switched on and on a town grid: Fully Charged at its store (health x
    /// capacity), Empty at 0, else Charging / Draining by the grid's flows, else Stored; WindGeneratorBuilding (<c>FUN_1403011e0</c>) <c>No Wind</c>
    /// while the blades do not turn. Null: the chain's word stands.
    /// </summary>
    internal static (string, bool)? GeneratorWord(MachineSnapshot m)
    {
        if (m.Power is not { } pw) return null;
        if (m.Wind is { } wind) return wind.Share <= 0 ? ("No Wind", true) : null;

        if (pw.Kind == PowerKind.Battery)
        {
            if (!pw.PowerOn || !pw.OnGrid) return null;

            float full = pw.Store;
            return pw.Charge >= full ? ("Fully Charged", false) : pw.Charge <= 0 ? ("Empty", true)
                : pw.GridCharging ? ("Charging", false) : pw.GridDraining ? ("Draining", false) : ("Stored", false);
        }

        return pw.Kind == PowerKind.Generator && !pw.Fueled ? ("Out of fuel", true) : null;
    }

    // ---- the overview's research tab (docs/game/ui-screens.md "Research tab") ----

    /// <summary>
    /// The overview's research tab bindings: the categories, the no-bench message and the title with the bench level and rate.
    /// </summary>
    internal void BindResearchTab(UiBindings b)
    {
        var research = player.Session.CurrentSnapshot.Research;
        var categories = research.Available.Select(r => r.Category).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        if (ResearchCategory is null || !categories.Contains(ResearchCategory))
            ResearchCategory = categories.FirstOrDefault();

        b.Lists["ResearchTypeList"] = [.. categories.Select(c => new UiListItem(c, c))];
        b.SelectedItems["ResearchTypeList"] = ResearchCategory is null ? -1 : categories.IndexOf(ResearchCategory);
        if (research.BenchLevel <= 0)
        {
            b.Text["ResearchBenchMessage"] =
                Loc.Tr("You currently have no research facilities. Build a research bench and assign someone to work there.");
        }
        else b.Hidden.Add("ResearchBenchMessage");

        IndentLabel(b, "lbResearchDescription");
        if (player.FaithfulScreens.GetTab("overview", "TabsMain") == TutorialPanel.ResearchTab)
            b.Text["Root"] = Loc.Tr("RESEARCH - LEVEL {1,num} (CURRENT RATE: {2,num=fix,p=2}x)", research.BenchLevel, research.Rate);
    }

    /// <summary>
    /// A research tab event: a category picked, a blueprint queued or one cancelled.
    /// </summary>
    internal void ResearchAction(MyGuiEvent e)
    {
        if (e.Widget == "ResearchTypeList" && e.Action == "select")
        {
            if (e.Text != ResearchCategory) _researchScroll = 0;

            ResearchCategory = e.Text;
        }
        else if (e.Action.StartsWith("research-queue:", StringComparison.Ordinal))
        {
            ResearchFocus = e.Action[15..];
            player.Say(new QueueResearch(ResearchFocus) { Tick = player.Session.World.Tick });
        }
        else if (e.Action.StartsWith("research-cancel:", StringComparison.Ordinal))
            player.Say(new CancelResearch(e.Action[16..]) { Tick = player.Session.World.Tick });
    }

    /// <summary>
    /// A research's time left as <c>FUN_140831550</c> writes it: its hours over the current rate (when there is one) times the share still to do,
    /// rounded up to at least 1, "&lt;" before it under an hour, "{n} hr" / "{n} hrs"; with <paramref name="rateNote"/> and no rate " (at 1x rate)"
    /// after it.
    /// </summary>
    static string ResearchTime(float hours, float progress, float rate, bool rateNote)
    {
        float left = (rate > 0 ? hours / rate : hours) * (1 - progress);
        int whole = Math.Max(1, (int)MathF.Ceiling(left));
        string text = (left < 1 ? "<" : "") + Loc.Tr(whole == 1 ? "{1,num} hr" : "{1,num} hrs", whole);
        return rateNote && rate == 0 ? text + " " + Loc.Tr("(at 1x rate)") : text;
    }

    /// <summary>
    /// The description's caption for each <see cref="Meitou.Data.Gameplay.Construction.ResearchData.UnlockFields"/> field (<c>FUN_140495e00</c>).
    /// </summary>
    static readonly Dictionary<string, string> UnlockCaptions = new(StringComparer.Ordinal)
    {
        ["enable buildings"] = "New Building:",
        ["enable weapon type"] = "New Weapon Class:",
        ["enable weapon model"] = "New Weapons Grade:",
        ["enable armour"] = "New Armour Type:",
        ["enable item"] = "New Items:",
        ["enable crossbow"] = "New Crossbows:",
        ["enable backpack"] = "New Backpacks:",
        ["enable robotics"] = "New Robot Limbs:",
    };

    /// <summary>
    /// The blueprint rows (<c>DataPanelLine_Research</c>, <c>FUN_1406f9220</c>): a <c>Kenshi_Button1</c> frame across 98 % of the panel, the name
    /// from 3 % and the time right-aligned to 97 %, in Main when it can be queued, else Bad, and a green bar over the frame for the progress kept;
    /// then the queue rows and the hovered or last clicked research's description.
    /// </summary>
    internal void AddResearchContent(List<UiControl> controls)
    {
        if (player.FaithfulScreens.GetTab("overview", "TabsMain") != TutorialPanel.ResearchTab) return;

        var research = player.Session.CurrentSnapshot.Research;
        var face = new Dictionary<string, string> { ["FontName"] = "Kenshi_StandardFont_Medium", ["TextAlign"] = "Left VCenter" };
        float line = MathF.Max(18, 24 * player.InventoryScale);
        var ink = new Vector4(0.86f, 0.82f, 0.71f, 1);

        if (controls.FirstOrDefault(c => c.Name == "BlueprintsAvailablePanel") is { } blueprints)
        {
            // The row unit U as the base game draws it at 1080 lines (Observed): a 29 px frame 0.15 U down a 35 px pitch.
            float unit = 30.5f * player.Height / 1080f, pitch = 1.15f * unit, w = blueprints.Bounds.Width;
            var area = blueprints.Bounds;
            var clip = area.Intersect(blueprints.Clip ?? area);
            var rows = research.Available.Where(r => r.Category == ResearchCategory).OrderBy(r => r.Name, StringComparer.Ordinal).ToList();
            _researchArea = clip;
            _researchScroll = Math.Clamp(_researchScroll, 0, Math.Max(0, rows.Count * pitch + 0.15f * unit - area.Height));
            _researchStep = pitch;

            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                float top = area.Y + i * pitch + 0.15f * unit - _researchScroll, height = 0.97f * unit;
                var colour = MyGuiColours.Parse(r.BenchTooLow || !r.Affordable ? PlayerInterface.BadColour : PlayerInterface.MainColour, ink);
                controls.Add(new($"research-row-{i}", "Button", new(area.X + 0.01f * w, top, 0.98f * w, height), "", "research-queue:" + r.Id,
                    "Kenshi_Button1Skin", true, new Dictionary<string, string>(), Vector4.Zero, Pickable: true, Clip: clip));

                if (r.Progress > 0)
                {
                    controls.Add(new($"research-row-{i}-bar", "Widget", new(area.X + 0.02f * w, top + 0.2f * height,
                        Math.Clamp(r.Progress, 0, 1) * 0.88f * w, 0.6f * height), "", "",
                        "Kenshi_LifeBarSkin", true, new Dictionary<string, string>(), Vector4.Zero, Pickable: false, Clip: clip));
                }

                var label = new UiRect(area.X + 0.03f * w, top, 0.94f * w, height);
                controls.Add(new($"research-row-{i}-name", "TextBox", label, r.Name, "", "", true, face, Vector4.Zero, TextColour: colour,
                    Pickable: false, Clip: clip));
                string time = ResearchTime(r.Hours, r.Progress, research.Rate, false);
                controls.Add(new($"research-row-{i}-time", "TextBox", label, time, "", "", true,
                    new Dictionary<string, string>(face) { ["TextAlign"] = "Right VCenter" }, Vector4.Zero, TextColour: colour, Pickable: false,
                    Clip: clip));
            }
        }

        if (QueueBox(controls, "ResearchQueueList", "ResearchQueuePanel") is { } queue)
        {
            for (int i = 0; i < research.Queue.Count; i++)
            {
                var (qid, name, progress) = research.Queue[i];
                controls.Add(new($"research-queue-{i}", "Button", new(queue.Bounds.X + 6, queue.Bounds.Y + i * line, queue.Bounds.Width - 12, line),
                    $"{name}  {(int)(progress * 100)}%", "research-cancel:" + qid, "", true, face, Vector4.Zero,
                    TextColour: i == 0 ? new(0.66f, 0.72f, 0.45f, 1) : ink, HoverColour: new(1, 0.4f, 0.3f, 1), Pickable: true, Clip: queue.Bounds));
            }
        }

        var hovered = controls.LastOrDefault(IsHoveredResearchRow)?.Action;
        string? focus = hovered is { Length: > 15 } h ? h[15..] : ResearchFocus;
        if (focus is null || research.Available.FirstOrDefault(r => r.Id == focus) is not { Id: not null } option)
            return;

        DatapanelLines(controls, "ResearchDescriptionPanel1", ResearchDescription(option, research.Rate));

        if (option.Description.Length > 0 && controls.FirstOrDefault(c => c.Name == "ResearchDescriptionPanel2") is { } text)
        {
            controls.Add(new("research-description", "EditBox", text.Bounds, option.Description, "", "", true,
                new Dictionary<string, string>
                {
                    ["FontName"] = "Kenshi_StandardFont_Medium",
                    ["WordWrap"] = "true",
                    ["MultiLine"] = "true",
                    ["Static"] = "true",
                    ["TextAlign"] = "Left Top",
                },
                Vector4.Zero, TextColour: ink, Pickable: false, Clip: text.Bounds));
        }
    }

    /// <summary>
    /// A blueprint row with an action under the pointer, inside its clip.
    /// </summary>
    bool IsHoveredResearchRow(UiControl c) => c.Name.StartsWith("research-row-", StringComparison.Ordinal) && c.Action.Length > 0
        && c.Bounds.Contains(player.Mouse.X, player.Mouse.Y) && c.Clip?.Contains(player.Mouse.X, player.Mouse.Y) != false;

    /// <summary>
    /// How far the blueprint rows are scrolled, their pitch and the panel they scroll in (the DatapanelGUI's own ScrollView in the original).
    /// </summary>
    float _researchScroll, _researchStep;

    /// <summary>
    /// The panel the blueprint rows scroll in.
    /// </summary>
    UiRect _researchArea;

    /// <summary>
    /// The wheel over the blueprint rows scrolls them a row per notch; clamped when the rows are laid out.
    /// </summary>
    internal bool ResearchWheel(Vector2 p, float steps)
    {
        if (!player.FaithfulScreens.IsOpen("overview") || player.FaithfulScreens.GetTab("overview", "TabsMain") != TutorialPanel.ResearchTab
            || _researchStep <= 0 || !_researchArea.Contains(p.X, p.Y))
        {
            return false;
        }

        _researchScroll -= MathF.Sign(steps) * _researchStep;
        return true;
    }

    /// <summary>
    /// A description DatapanelGUI's lines (<see cref="Title.FrontendScreens.AddDatapanel"/>'s geometry): rows of 0.94 / (int)(55 x its height / the
    /// screen's) of its height from 0.03 of it down.
    /// </summary>
    void DatapanelLines(List<UiControl> controls, string panel, IReadOnlyList<PlayerInterface.HudLine> lines)
    {
        if (controls.FirstOrDefault(c => c.Name == panel) is not { } host) return;

        float view = host.Bounds.Height;
        player.AddLines(controls, panel, lines, 0.94f / Math.Max(1, (int)(55 * view / Math.Max(1, player.Height))) * view, top: 0.03f * view);
    }

    /// <summary>
    /// The DESCRIPTION captions over the research and crafting panels sit at the tab's left edge in the layout; Meitou insets them 8 px at 1080 lines
    /// to line up with the panel's text (engine choice: the base game draws them flush with the frame).
    /// </summary>
    void IndentLabel(UiBindings b, string name) => b.Properties[name] = new Dictionary<string, string>
    {
        ["TextAlign"] = "Left VCenter",
        ["TextPadding"] = $"{(8 * player.Height / 1080f).ToString(CultureInfo.InvariantCulture)} 0 0 0",
    };

    /// <summary>
    /// The description's lines (<c>FUN_140495e00</c>): the name in Title with "lvl {n}"; "ETA:" in Title with the time left; "Needs research bench
    /// level {n}" in Bad when the bench is too low; a "Cost:" line per item ("{item} x{n}", Good when the benches hold it, else Bad), or "[Paid]" in
    /// Good; "Requirements:" with the first required research and a line below with the rest (Greyed finished, Good queued, else Bad; "-" for none);
    /// one line per unlocked record under its field's caption, in Good.
    /// </summary>
    static List<PlayerInterface.HudLine> ResearchDescription(ResearchOption option, float rate)
    {
        var lines = new List<PlayerInterface.HudLine>
        {
            new(option.Name, Loc.Tr("lvl {1,num}", option.Level), PlayerInterface.TitleColour),
            new(Loc.Tr("ETA:"), ResearchTime(option.Hours, option.Progress, rate, true), PlayerInterface.TitleColour),
        };
        if (option.BenchTooLow)
            lines.Add(new(Loc.Tr("Needs research bench level {1,num}", option.Level), "", PlayerInterface.BadColour));
        if (option.Paid) lines.Add(new(Loc.Tr("Cost:"), "[" + Loc.Tr("Paid") + "]", null, PlayerInterface.GoodColour));
        else foreach (var (item, count, held) in option.Cost) lines.Add(new(Loc.Tr("Cost:"),
            $"{item} x{count.ToString(CultureInfo.InvariantCulture)}", null, held ? PlayerInterface.GoodColour : PlayerInterface.BadColour));

        static string Requirement((string Name, bool Finished, bool Queued) r) =>
            PlayerInterface.Tag(r.Finished ? PlayerInterface.GreyColour : r.Queued ? PlayerInterface.GoodColour : PlayerInterface.BadColour)
            + r.Name.Replace("#", "##", StringComparison.Ordinal);

        var required = option.Requirements;
        lines.Add(new(Loc.Tr("Requirements:"), required.Count == 0 ? "-" : Requirement(required[0]) + (required.Count > 1 ? ", " : "")));
        if (required.Count > 1) lines.Add(new("", string.Join(", ", required.Skip(1).Select(Requirement))));
        foreach (var (field, name) in option.Unlocks) lines.Add(new(Loc.Tr(UnlockCaptions.GetValueOrDefault(field, field)), name, null,
            PlayerInterface.GoodColour));

        return lines;
    }

    // The overview's Craft tab (docs/game/ui-screens.md "Selected building", docs/game/buildings-production.md "Crafting"): STATIONS lists the
    // player's crafting benches, ITEMS what the selected one can craft (a click queues one; locked or too advanced rows are red and do nothing),
    // QUEUE its queue (a click takes one off), Repeat Queue its repeat toggle, DESCRIPTION the hovered or last clicked craft's time and inputs. The
    // original's row widgets and colours are <b>Unknown</b>; Meitou draws plain text rows like the research tab.
    /// <summary>
    /// The Craft tab's index in the overview's tabs.
    /// </summary>
    const int CraftTab = 3;

    /// <summary>
    /// The Craft tab's selected station's site, and the craft whose description shows.
    /// </summary>
    string? _craftStation, _craftFocus;

    /// <summary>
    /// The player's crafting benches.
    /// </summary>
    List<MachineSnapshot> CraftStations() => [.. player.Session.CurrentSnapshot.Machines.Where(m => m.Craft is not null)];

    /// <summary>
    /// A station's name: its site's, else its record.
    /// </summary>
    string StationName(MachineSnapshot m) =>
        player.Session.CurrentSnapshot.Buildings.FirstOrDefault(s => s.Id == m.Site) is { } site ? player.Build.SiteName(site) : m.Record;

    /// <summary>
    /// Opens the Craft tab on a bench (the building window's QUEUE... button).
    /// </summary>
    void OpenCraftTab(string site)
    {
        _craftStation = site;
        player.FaithfulScreens.Open("overview");
        player.FaithfulScreens.SetTab("overview", "TabsMain", CraftTab);
    }

    /// <summary>
    /// The Craft tab's bindings: the stations, the selected one, and its repeat toggle.
    /// </summary>
    internal void BindCraftTab(UiBindings b)
    {
        var stations = CraftStations();
        if (_craftStation is null || stations.All(s => s.Site != _craftStation))
            _craftStation = stations.FirstOrDefault()?.Site;

        b.Lists["CraftingStationsList"] = [.. stations.Select(s => new UiListItem(s.Site, StationName(s)))];
        b.SelectedItems["CraftingStationsList"] = stations.FindIndex(s => s.Site == _craftStation);
        bool repeat = stations.FirstOrDefault(s => s.Site == _craftStation)?.Craft?.Repeat == true;
        b.Actions["QueueRepeat"] = "QueueRepeat";
        b.Properties["QueueRepeat"] = new Dictionary<string, string>
        {
            ["Checked"] = repeat ? "true" : "false",
            ["StateSelected"] = repeat ? "true" : "false",
        };
        IndentLabel(b, "lbCraftingDescription");
    }

    /// <summary>
    /// A Craft tab event: a station picked, the repeat toggled, a craft queued or one taken off the queue.
    /// </summary>
    internal void CraftAction(MyGuiEvent e)
    {
        if (e.Widget == "CraftingStationsList" && e.Action == "select")
        {
            var stations = CraftStations();
            _craftStation = stations.FirstOrDefault(s => s.Site == e.Text)?.Site
                ?? stations.FirstOrDefault(s => StationName(s) == e.Text)?.Site
                ?? _craftStation;
        }
        else if (e.Widget == "QueueRepeat" && _craftStation is not null && Machine(_craftStation)?.Craft is { } craft)
            player.Say(new RepeatCrafts(_craftStation, !craft.Repeat) { Tick = player.Session.World.Tick });
        else if (e.Action.StartsWith("craft-queue:", StringComparison.Ordinal) && _craftStation is not null)
        {
            _craftFocus = e.Action[12..];
            player.Say(new QueueCraft(_craftStation, _craftFocus) { Tick = player.Session.World.Tick });
        }
        else if (e.Action.StartsWith("craft-focus:", StringComparison.Ordinal)) _craftFocus = e.Action[12..];
        else if (e.Action.StartsWith("craft-cancel:", StringComparison.Ordinal) && _craftStation is not null)
        {
            int cancelled = int.Parse(e.Action.AsSpan(13), CultureInfo.InvariantCulture);
            player.Say(new CancelCraft(_craftStation, cancelled) { Tick = player.Session.World.Tick });
        }
    }

    /// <summary>
    /// The item rows, the queue rows and the description, drawn into the Craft tab's panels.
    /// </summary>
    internal void AddCraftContent(List<UiControl> controls)
    {
        if (player.FaithfulScreens.GetTab("overview", "TabsMain") != CraftTab || _craftStation is null
            || Machine(_craftStation) is not { Craft: { } craft })
        {
            return;
        }

        var options = player.Session.CurrentSnapshot.Crafts.GetValueOrDefault(_craftStation) ?? [];
        var face = new Dictionary<string, string> { ["FontName"] = "Kenshi_StandardFont_Medium", ["TextAlign"] = "Left VCenter" };
        float line = MathF.Max(18, 24 * player.InventoryScale);
        var ink = new Vector4(0.86f, 0.82f, 0.71f, 1);

        if (controls.FirstOrDefault(c => c.Name == "QueueItemsPanel") is { } panel)
        {
            var rows = options.OrderBy(o => o.Kind).ThenBy(o => o.Level).ThenBy(o => o.Name, StringComparer.Ordinal).ToList();

            for (int i = 0; i < rows.Count; i++)
            {
                var o = rows[i];
                controls.Add(new($"craft-row-{i}", "Button", new(panel.Bounds.X + 6, panel.Bounds.Y + i * line, panel.Bounds.Width - 12, line),
                    o.Name, o.Unlocked ? "craft-queue:" + o.Key : "craft-focus:" + o.Key, "", true, face, Vector4.Zero,
                    TextColour: o.Unlocked ? ink : new Vector4(0.75f, 0.35f, 0.3f, 1), HoverColour: new(0, 1, 0, 1), Pickable: true,
                    Clip: panel.Bounds));
            }
        }

        if (QueueBox(controls, "CraftQueue", "QueuePanel") is { } queue)
        {
            for (int i = 0; i < craft.Queue.Count; i++)
            {
                var (_, name, progress, _) = craft.Queue[i];
                controls.Add(new($"craft-queue-{i}", "Button", new(queue.Bounds.X + 6, queue.Bounds.Y + i * line, queue.Bounds.Width - 12, line),
                    $"{name}  {(int)(progress * 100)}%", "craft-cancel:" + i.ToString(CultureInfo.InvariantCulture), "", true, face, Vector4.Zero,
                    TextColour: i == 0 ? new(0.66f, 0.72f, 0.45f, 1) : ink, HoverColour: new(1, 0.4f, 0.3f, 1), Pickable: true, Clip: queue.Bounds));
            }
        }

        // A queued craft under the pointer has its own description (the CraftQueue box's tooltip, FUN_1402be650 -> FUN_1402bcf60).
        if (controls.LastOrDefault(IsHoveredCraftQueue) is { } over
            && int.TryParse(over.Name.AsSpan(12), NumberStyles.None, CultureInfo.InvariantCulture, out int hoveredIndex)
            && hoveredIndex < craft.Queue.Count)
        {
            QueueDescription(controls, _craftStation, craft, hoveredIndex, options);
            return;
        }

        string? hovered = controls.LastOrDefault(IsHoveredCraftRow)?.Action;
        string? focus = hovered is { } h ? h[(h.IndexOf(':') + 1)..] : _craftFocus;
        if (focus is null || options.FirstOrDefault(o => o.Key == focus) is not { } option) return;

        var lines = new List<PlayerInterface.HudLine>
        {
            new(option.Name, ""),
            new("Time:", $"{option.Hours.ToString("0.#", CultureInfo.InvariantCulture)} h", PlayerInterface.MainColour),
        };
        if (option.Level > 0)
            lines.Add(new("Level:", option.Level.ToString(CultureInfo.InvariantCulture), PlayerInterface.MainColour));
        foreach (var (item, per) in option.Inputs)
            lines.Add(new(item, per.ToString("0.##", CultureInfo.InvariantCulture), PlayerInterface.MainColour));
        if (!option.Unlocked) lines.Add(new("Needs research", "", PlayerInterface.BadColour));

        DatapanelLines(controls, "QueueDescriptionPanel1", lines);
    }

    /// <summary>
    /// A queue box of the overview's Craft or Research tab: the layout's own <paramref name="name"/>, else the panel an older layout has in its place
    /// (Dark UI, #463), which the original fills with an item box of that name stretched over it (<c>FUN_1402bf0f0</c> for <c>QueuePanel</c>,
    /// <c>FUN_14049f5b0</c> for <c>ResearchQueuePanel</c>, <b>Verified</b>).
    /// </summary>
    static UiControl? QueueBox(List<UiControl> controls, string name, string panel) =>
        controls.FirstOrDefault(c => c.Name == name) ?? controls.FirstOrDefault(c => c.Name == panel);

    /// <summary>
    /// A queued craft's row under the pointer.
    /// </summary>
    bool IsHoveredCraftQueue(UiControl c) =>
        c.Name.StartsWith("craft-queue-", StringComparison.Ordinal) && c.Bounds.Contains(player.Mouse.X, player.Mouse.Y);

    /// <summary>
    /// A craft option's row under the pointer.
    /// </summary>
    bool IsHoveredCraftRow(UiControl c) =>
        c.Name.StartsWith("craft-row-", StringComparison.Ordinal) && c.Bounds.Contains(player.Mouse.X, player.Mouse.Y);

    /// <summary>
    /// The description of a queued craft (<c>FUN_1402bcf60</c>, into <c>QueueDescriptionPanel2</c>; <b>Verified</b>): <c>State:</c> "In
    /// production..." and, by the bench's status, a second <c>State:</c> row in Bad (0 Out of resources, 1 Output full, 3 Upgrade required);
    /// <c>ETA:</c> the hours left, truncated to a tenth, "&lt;" before 0; and, only while the bench has had a smith, <c>-Current Smith:</c> with his
    /// level of the bench's stat and the multiplier's penalty in Bad, then by the item's class <c>-Material Costs:</c> (weapons), <c>-Predicted
    /// Result:</c> (the quality's name for armour and crossbows, in the colour of its grade; the model's for weapons, Bad when it is not the queued
    /// one) and <c>-Critical Success Chance:</c>. The ingredients go to Panel 1 (<c>FUN_1402b6b40</c>; Meitou lists the recipe as the item rows do).
    /// Not shown: "Not assigned to a craft bench" (the queue entry's bench hand is never null here) and the ETA's bar.
    /// </summary>
    void QueueDescription(List<UiControl> controls, string site, CraftView craft, int index, IReadOnlyList<CraftOptionView> options)
    {
        var (key, name, progress, hours) = craft.Queue[index];
        var ingredients = new List<PlayerInterface.HudLine> { new(name, "") };

        if (options.FirstOrDefault(o => o.Key == key) is { } option)
        {
            foreach (var (item, per) in option.Inputs) ingredients.Add(new(item, per.ToString("0.##", CultureInfo.InvariantCulture),
                PlayerInterface.MainColour));
        }

        DatapanelLines(controls, "QueueDescriptionPanel1", ingredients);
        var rows = new List<PlayerInterface.HudLine> { new(Loc.Tr("State:"), Loc.Tr("In production...")) };
        string? status = Machine(site)?.Status switch { 0 => "Out of resources", 1 => "Output full", 3 => "Upgrade required", _ => null };
        if (status is not null) rows.Add(new(Loc.Tr("State:"), Loc.Tr(status), ValueColour: PlayerInterface.BadColour));

        // FUN_1402bcf60: (int)(hours x (1 - progress) x 10) tenths, "{1,num} hr" for a whole 1 and "hrs" else, "<" in front at 0 (the entry's +0x30
        // divisor is Unknown: 0 unless the bench worked this tick).
        float left = (int)(hours * (1 - progress) * 10) * 0.1f;
        rows.Add(new(Loc.Tr("ETA:"), (left == 0 ? "<" : "") + Loc.Plural("{1,num} hr", "{1,num} hrs", (int)left, left)));

        if (player.Session.CurrentSnapshot.Smiths.GetValueOrDefault(site) is { } smith)
        {
            string penalty = smith.Penalty == 0
                ? ""
                : PlayerInterface.Tag(PlayerInterface.BadColour) + " (" + Loc.Format("{1,num=fix,p=0}", -smith.Penalty) + ")"
                    + PlayerInterface.Tag(MyGuiColours.InkText);
            rows.Add(new(Loc.Tr("-Current Smith:"), smith.Name.Replace("#", "##", StringComparison.Ordinal) + Loc.Tr(" (skill: {1,num}{2})",
                smith.Skill, penalty)));

            if (index < smith.Entries.Count)
            {
                var guess = smith.Entries[index];

                if (guess.Model is not null)
                {
                    rows.Add(new(Loc.Tr("-Material Costs:"), Loc.Format("{1,num}", guess.MaterialCost)));
                    rows.Add(new(Loc.Tr("-Predicted Result:"),
                        PlayerInterface.Tag(guess.ModelChanged ? PlayerInterface.BadColour : PlayerInterface.MainColour) + guess.Model.Replace("#",
                            "##", StringComparison.Ordinal)));
                }
                else if (guess.Quality >= 0)
                {
                    int grade = Simulation.Combat.ArmourTooltip.GradeOf(guess.Quality);
                    rows.Add(new(Loc.Tr("-Predicted Result:"),
                        PlayerInterface.Tag(TooltipRuntime.TipColour(TooltipRuntime.GradeTone(grade), false))
                            + Loc.Tr(Simulation.Combat.ArmourTooltip.GradeNames[grade])));
                }

                if (guess.Critical >= 0)
                    rows.Add(new(Loc.Tr("-Critical Success Chance:"), Loc.Tr("{1,num}%", guess.Critical)));
            }
        }

        DatapanelLines(controls, "QueueDescriptionPanel2", rows);
    }
}
