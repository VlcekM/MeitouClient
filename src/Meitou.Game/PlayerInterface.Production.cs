using System.Globalization;
using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Data.Ui;
using Meitou.Rendering.Ui;
using Meitou.Simulation;
using Meitou.Simulation.Items;
using Meitou.Simulation.Production;
using Meitou.Ui;

namespace Meitou.Game;

/// <summary>
/// Production in the UI (docs/simulation.md "Production", docs/game/ui-screens.md "Selected building"):
/// <list type="bullet">
/// <item>Selecting a complete machine of the player's opens its window at the left edge, vertically centred: a well or farm the original's <c>Kenshi_GenericBuildingWindow</c> (inputs in <c>in1</c> / <c>in2</c>,
/// the output in <c>out</c>, their bars and texts), a research bench <c>Kenshi_InventoryResearchWindow</c> (its storage grid; the RESEARCH button opens the overview's research tab).
/// Items are held with the mouse between the windows and the selected character's inventory (PlayerInterface.Inventory.cs), or moved with a right click (<see cref="PutInMachine"/>,
/// <see cref="TakeFromMachine"/>).</item>
/// <item>The site menu has Operate for a machine characters work; a quick right click on one gives it.</item>
/// <item>The overview's research tab: TYPES lists the categories, BLUEPRINTS the research of the selected category that can be queued (a click queues it), QUEUE the queue (a click
/// takes one off), DESCRIPTION the hovered or last clicked research. The original's row widgets and colours are <b>Unknown</b>; Meitou draws plain text rows.</item>
/// </list>
/// </summary>
sealed partial class PlayerInterface
{
    /// <summary>The site whose machine window is shown (null: none), and a site whose window the player closed (not reopened until the selection changes).</summary>
    string? shownMachine, closedMachine;

    string? researchCategory, researchFocus;

    MachineSnapshot? Machine(string? site) => site is null ? null : session.CurrentSnapshot.Machines.FirstOrDefault(m => m.Site == site);

    /// <summary>The storage window's layout pixels above its grid (caption and item heading) and in all around it, with the two-line Max Capacity text below (measured on the original's window).</summary>
    const float StorageAbove = 86, StorageChrome = 150;

    static string MachineScreen(MachineSnapshot m) => m.Storage is null || m.Inputs.Count > 0 || m.Output is not null ? "building" : m.IsStorage && !m.IsContainer && m.TechLevel == 0 ? "storage" : "bench";

    /// <summary>Opens the selected machine's window and closes it when the selection moves on.</summary>
    void SyncMachineScreens()
    {
        if (faithfulScreens is null) return;
        var focus = BuildingFocus();
        // A rock's natural site is no registry building: its machine, once mined, is enough.
        var m = focus?.SiteId is { } id && (ProductionSystem.IsNatural(id) || session.CurrentSnapshot.Buildings.FirstOrDefault(s => s.Id == id) is { Complete: true }) ? Machine(id) : null;
        if (m is not null && m.Inputs.Count == 0 && m.Output is null && m.Storage is null && m.Craft is null) m = null;
        // An open container takes the window.
        m = session.CurrentSnapshot.Machines.FirstOrDefault(c => c.IsContainer) ?? m;
        if (closedMachine is not null && closedMachine != m?.Site) closedMachine = null;
        if (shownMachine is not null && (m is null || m.Site != shownMachine))
        {
            faithfulScreens.Close("building");
            faithfulScreens.Close("bench");
            faithfulScreens.Close("storage");
            shownMachine = null;
            storageWithInventory = null;
        }
        if (shownMachine is not null && Machine(shownMachine) is { } shown && !faithfulScreens.IsOpen(MachineScreen(shown)))
        {
            if (shown.IsContainer) Say(new Meitou.Simulation.Items.CloseContainer { Tick = session.World.Tick });
            closedMachine = shownMachine;
            shownMachine = null;
            storageWithInventory = null;
            return;
        }
        if (m is null || shownMachine == m.Site || closedMachine == m.Site) return;
        shownMachine = m.Site;
        string screen = MachineScreen(m);
        inventoryScroll.Remove(screen);
        // The character's inventory opens with an order-driven container window (the original's order 26 opens the looter's own window and the target's together) and with a machine's; selecting
        // a selected machine, storage, bench or mined rock shows its window alone (the original's windows of a selected ore storage and electrical workbench have no inventory beside them).
        bool alone = !m.IsContainer && storageWithInventory != m.Site;
        if (!alone && Selected().Count > 0 && !faithfulScreens.IsOpen("inventory")) faithfulScreens.Open("inventory");
        faithfulScreens.Open(screen);
        PlaceMachineWindow(screen);
    }

    void BindMachineScreen(string id, UiBindings b)
    {
        var m = Machine(shownMachine);
        if (id == "storage")
        {
            // The original's generic container window: caption STORAGE, the limited item as heading, and Max Capacity (red "(Full)" once the stack is full).
            b.Text["Root"] = "STORAGE";
            b.Text["TypeText"] = m?.StorageLimitName ?? "";
            if (m is { StorageCapacity: > 0 } cap)
            {
                bool full = m.Storage?.Items.FirstOrDefault()?.Quantity >= cap.StorageCapacity;
                b.Text["CapacityText"] = $"Max Capacity: {cap.StorageCapacity.ToString("N0", CultureInfo.InvariantCulture)}" + (full ? " (Full)" : "");
                // The original paints the whole line red once the stack is full (the exact red is Unknown; read off its screenshot).
                if (full) b.Properties["CapacityText"] = new Dictionary<string, string> { ["TextColour"] = "0.91 0.25 0.25" };
            }
            else b.Hidden.Add("CapacityText");
            b.Disabled.Remove("ArrangeButton");
            b.Hidden.Add("ArrangeButton");
            if (m?.Storage is { } sg)
                b.Positions["Root"] = new(0, 0, sg.Width * InventoryCell + 40 * InventoryScale, Math.Min(Height * 0.8f, sg.Height * InventoryCell + StorageChrome * InventoryScale));
            b.TemplateScale = InventoryScale;
            return;
        }
        b.Text["Root"] = (m is { IsContainer: true } ? buildHost?.ItemName(m.Record) ?? "CONTAINER" : selectedBuilding?.Name ?? "BUILDING").ToUpperInvariant();
        if (id == "bench")
        {
            b.Disabled.Remove("ResearchButton");
            b.Actions["ResearchButton"] = "ResearchButton";
            if (m is not { TechLevel: > 0 }) b.Hidden.Add("ResearchButton");
            b.Disabled.Remove("ArrangeButton");
            b.Hidden.Add("ArrangeButton");
            if (m?.Storage is { } grid)
                b.Positions["Root"] = new(0, 0, grid.Width * InventoryCell + 40 * InventoryScale, Math.Min(Height * 0.8f, grid.Height * InventoryCell + 108 * InventoryScale));
            b.TemplateScale = InventoryScale;
            return;
        }
        if (m?.Craft is { } craft)
        {
            // CraftingBuilding::updateInventoryWindow: the current entry's name in #afa68b (an empty name with nothing current).
            b.Text["CraftingName"] = "#afa68b" + (CraftCurrent(craft)?.Name ?? "");
            b.Disabled.Remove("QueueButton");
            b.Actions["QueueButton"] = "QueueButton";
        }
        else b.Hidden.Add("CraftingPanel");
        b.TemplateScale = InventoryScale;
        if (m is null) return;
        for (int k = 0; k < 2; k++)
        {
            string n = (k + 1).ToString(CultureInfo.InvariantCulture);
            if (k == 0 && FurnaceGrid(m) is { } fg)
            {
                // A furnace's one input is its 12 x 7 grid (FurnaceBuilding::setupFromData), drawn in in1 by AddMachineContent; its label, status and bar are an engine choice (left empty).
                b.Text["Input1Text"] = "";
                b.Text["Input1StatusText"] = "";
                b.Hidden.Add("Input1ProgressBar");
                // Widen and deepen the window to hold the grid, measured off the last frame's slot (the original's sizing is Unknown).
                if (faithfulScreens.LastFrame.FirstOrDefault(f => f.Id == "building")?.Controls is { } last && last.FirstOrDefault(c => c.Type == "Window") is { } win
                    && last.FirstOrDefault(c => c.Name == "in1") is { } in1)
                    b.Positions["Root"] = new(0, 0, Math.Max(win.Bounds.Width, in1.Bounds.X - win.Bounds.X + fg.Width * InventoryCell + 20 * InventoryScale),
                        Math.Max(win.Bounds.Height, in1.Bounds.Y - win.Bounds.Y + fg.Height * InventoryCell + 60 * InventoryScale));
                continue;
            }
            if (k >= m.Inputs.Count) { b.Hidden.Add($"Input{n}Panel"); continue; }
            var input = m.Inputs[k];
            b.Text[$"Input{n}Text"] = input.Name;
            if (m.Craft is not null)
            {
                // A crafting bench's input: "Empty!" for an empty section with nothing taken in, else "OK" (a section holds only its ingredient here, so never "Invalid item!").
                bool empty = input.Count <= 0 && input.Fill <= 0;
                b.Text[$"Input{n}StatusText"] = empty ? "#afa68b" + Loc.Tr("Empty!") : TipGood + Loc.Tr("OK");
            }
            else b.Text[$"Input{n}StatusText"] = $"{input.Count.ToString(CultureInfo.InvariantCulture)}/{input.Capacity.ToString(CultureInfo.InvariantCulture)}";
            b.Progress[$"Input{n}ProgressBar"] = input.Fill;
        }
        if (m.Output is { } output)
        {
            b.Text["OutputText"] = $"{output.Name}\n{output.Count.ToString(CultureInfo.InvariantCulture)}/{output.Capacity.ToString(CultureInfo.InvariantCulture)}";
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
        if (m.Inputs.Count == 0 && FurnaceGrid(m) is null) b.Hidden.Add("InputTitleText");
        if (m is { Inputs.Count: 0, Craft: null, Output: not null } && FurnaceGrid(m) is null) OutputOnlyGeometry(b);
    }

    /// <summary>
    /// Engine choice for a machine with only an output (a mined rock): the layout's empty input column and crafting row are dropped, the output column takes the
    /// window's width and the window shrinks around it, its output panel kept near the layout's size (the original's window is the full layout, mostly empty).
    /// </summary>
    void OutputOnlyGeometry(UiBindings b)
    {
        const float WidthPart = 0.52f, HeightPart = 0.6f;
        var layout = faithfulScreens.Layout("building");
        b.Hidden.Add("CraftingPanel");
        b.Hidden.Add("InputColumn");
        b.Positions["Root"] = new(0, 0, 0.208854f * Width * WidthPart, 0.398148f * Height * HeightPart);
        float x = 0.0126582f / WidthPart, w = 1 - 2 * x, titles = 0.0909091f / HeightPart;
        if (layout.Find("TitlesPanel") is { } t) b.RelativePositions[t] = new(x, 0, w, titles);
        if (layout.Find("OutputTitleText") is { } ot) b.RelativePositions[ot] = new(0, 0, 1, 1);
        if (layout.Find("InputOutputPanel") is { } io) b.RelativePositions[io] = new(x, titles, w, 1 - titles - 0.03f);
        if (layout.Find("OutputColumn") is { } oc) b.RelativePositions[oc] = new(0, 0, 1, 1);
        if (layout.Find("OutputPanel") is { } op) b.RelativePositions[op] = new(0, 0, 1, 1);
    }

    /// <summary>A furnace's item grid (its <c>in1</c> section, kept as the machine's storage); null for any other machine.</summary>
    static Meitou.Simulation.InventoryView? FurnaceGrid(MachineSnapshot? m) => m is { Furnace: not null, Inputs.Count: 0, Storage: { } grid } ? grid : null;

    /// <summary>A bench's current craft: the queue's front while its progress is below 1 (docs/game/ui-screens.md "Selected building", crafting window); null: none.</summary>
    static (string Key, string Name, float Progress, float Hours)? CraftCurrent(Meitou.Simulation.Production.CraftView craft) =>
        craft.Queue is [var front, ..] && front.Progress < 1 ? front : null;

    /// <summary>The faded picture of the item a slot expects (alpha 0.35), shown while the slot is empty.</summary>
    void AddItemGhost(List<UiControl> controls, string name, UiRect area, string record, string material, UiRect? clip)
    {
        if (itemIcons?.Choose(record, material) is not { } choice || uiPainter is not { } painter) return;
        Dictionary<string, string>? image = null;
        if (choice is { Source: ItemIconSource.File, File: { } file } && painter.IconReady(file)) image = new() { ["ImageTexture"] = UiPainter.IconTexture(file) };
        else if (choice.Source == ItemIconSource.Render && UiPainter.RenderedIconTexture(record, material, choice.Colour) is { } rendered && painter.RenderedIconReady(rendered))
            image = new() { ["ImageTexture"] = rendered, ["ImageFlipV"] = "true" };
        if (image is null) return;
        image["Alpha"] = "0.35";
        controls.Add(new($"{name}-ghost", "ImageBox", area, "", "", "", true, image, Vector4.Zero, Pickable: false, Clip: clip));
    }

    void AddMachineContent(string id, List<UiControl> controls)
    {
        if (Machine(shownMachine) is not { } m) return;
        if (id is "bench" or "storage")
        {
            if (m.Storage is not { } grid) return;
            if (id == "storage") ArrangeContainerGrid(id, controls, grid.Width, grid.Height, StorageAbove, StorageChrome - StorageAbove - 6);
            else ArrangeContainerGrid(id, controls, grid.Width, grid.Height);
            if (controls.FirstOrDefault(c => c.Name == "backpack_content") is not { } slot) return;
            var area = slot.Bounds;
            foreach (var item in grid.Items)
            {
                if (Lifted(ItemOwner.AtSite(m.Site), 1000 + item.Index)) continue;
                AddItemCell(controls, $"machine-item-{item.Index}",
                    new(area.X + item.X * area.Width / grid.Width, area.Y + item.Y * area.Height / grid.Height, item.Width * area.Width / grid.Width, item.Height * area.Height / grid.Height),
                    item.Record, item.Material, item.Name, item.Quantity, ItemTip.Of(item), "MachineItem", 1000 + item.Index,
                    slot.Bounds.Intersect(slot.Clip ?? slot.Bounds), charge: item.Charge, colour: item.Colour);
            }
            return;
        }
        void Slot(string name, string bar, MachineSlotView view, int code)
        {
            int index = controls.FindIndex(c => c.Name == name);
            if (index < 0) return;
            // The original makes the section the recipe item's footprint (a box of building material is 6 x 3 cells); the grid keeps the layout's top and takes the progress bar's horizontal centre (the layout slot is not centred in its column), so it stays under its label and bar.
            // A grid wider than the bar keeps the slot's left edge, so it overflows only to the right as before rather than into the column on its left.
            var slot = controls[index];
            var barRoot = controls.FirstOrDefault(c => c.Name == bar);
            float width = view.Width * InventoryCell;
            var column = barRoot?.Bounds ?? slot.Bounds;
            float x = width <= column.Width ? column.X + (column.Width - width) / 2 : slot.Bounds.X;
            var b = slot.Bounds with { X = x, Width = width, Height = view.Height * InventoryCell };
            // The grid and a moved bar may leave their content panel, so they clip to the window instead.
            var clip = controls.FirstOrDefault(c => c.Type == "Window")?.Bounds ?? slot.Clip;
            controls[index] = slot with
            {
                Bounds = b,
                Clip = clip,
                Properties = new Dictionary<string, string>(slot.Properties) { ["TileSize"] = FormattableString.Invariant($"{InventoryCell} {InventoryCell}"), ["State"] = "normal" },
            };
            // Engine choice: a grid taller than the room above the progress bar pushes the bar down under it.
            if (barRoot is { } root && b.Y + b.Height + 4 * InventoryScale - root.Bounds.Y is var dy and > 0)
                for (int i = 0; i < controls.Count; i++)
                    if (controls[i].Name == bar || controls[i].Name == bar + "@track" || controls[i].Properties.GetValueOrDefault("TemplateOwner") == bar)
                        controls[i] = controls[i] with { Bounds = controls[i].Bounds with { Y = controls[i].Bounds.Y + dy }, Clip = clip };
            if (view.Count <= 0 && m.Craft is not null) AddItemGhost(controls, $"machine-{name}", b, view.Item, "", clip);
            if (view.Count <= 0 || Lifted(ItemOwner.AtSite(m.Site), code)) return;
            AddItemCell(controls, $"machine-{name}", b, view.Item, "", view.Name, view.Count, null, "MachineItem", code, clip);
        }
        for (int k = 0; k < Math.Min(2, m.Inputs.Count); k++) Slot($"in{k + 1}", $"Input{k + 1}ProgressBar", m.Inputs[k], 10 + k);
        if (FurnaceGrid(m) is { } fg && m.Furnace is { } burn)
        {
            // The furnace's grid in in1, its items the storage's (picked up as storage items); disabled while it burns (InventorySection::setEnabled(!active)).
            Slot("in1", "Input1ProgressBar", new Meitou.Simulation.Production.MachineSlotView("", "", 0, 0, 0, fg.Width, fg.Height), -1);
            int gi = controls.FindIndex(c => c.Name == "in1");
            if (gi >= 0)
            {
                var g = controls[gi];
                if (burn.Burning) controls[gi] = g with { Properties = new Dictionary<string, string>(g.Properties) { ["State"] = "disabled" } };
                foreach (var item in fg.Items)
                {
                    if (Lifted(ItemOwner.AtSite(m.Site), 1000 + item.Index)) continue;
                    AddItemCell(controls, $"machine-item-{item.Index}",
                        new(g.Bounds.X + item.X * InventoryCell, g.Bounds.Y + item.Y * InventoryCell, item.Width * InventoryCell, item.Height * InventoryCell),
                        item.Record, item.Material, item.Name, item.Quantity, ItemTip.Of(item), "MachineItem", 1000 + item.Index, g.Clip, charge: item.Charge, colour: item.Colour);
                }
            }
        }
        if (m.Output is { } output) Slot("out", "OutputProgressBar", output, 8);
        else if (m.Craft is { Done: [var made, ..] } && controls.FirstOrDefault(c => c.Name == "out") is { } outSlot && !Lifted(ItemOwner.AtSite(m.Site), 8))
        {
            var ob = outSlot.Bounds;
            float side = MathF.Min(ob.Width, ob.Height);
            AddItemCell(controls, "machine-out", new(ob.X, ob.Y, side, side), made.Record, made.Material, made.Name, made.Quantity, null, "MachineItem", 8, outSlot.Clip, colour: made.Colour);
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

    void MachineAction(MyGuiEvent e)
    {
        if (e.Screen == "bench" && e.Widget == "ResearchButton") { faithfulScreens.Open("overview"); faithfulScreens.SetTab("overview", "TabsMain", ResearchTab); }
        else if (e.Screen == "building" && e.Widget == "QueueButton" && shownMachine is not null) OpenCraftTab(shownMachine);
    }

    /// <summary>
    /// The verb for a complete machine characters work: the original's Use (order 87, <c>FUN_14079b2d0</c>); with Shift held on the click it becomes the Operating machine job, as the
    /// original's dispatch turns a menu order into a job (<c>FUN_1407fa650</c>).
    /// </summary>
    void AddMachineVerb(List<(string Caption, Action Give)> verbs, Meitou.Simulation.Construction.BuildingSnapshot site, IReadOnlyList<CharacterId> selection)
    {
        // An automatic machine: Auto-Haul (152) in place of Use; with Shift the 152 job (its companion 124 follows), else a one-shot haul round (the operator only hauls there).
        if (site.Complete && Machine(site.Id) is { Operable: true, IsAutomatic: true })
            verbs.Add(("Auto-Haul", () => Say(SlowModifier
                ? (Meitou.Simulation.SimCommand)new AddJob(selection, Meitou.Data.Gameplay.Ai.TaskType.OPERATE_AUTOMATIC_MACHINERY, site.Id) { Tick = session.World.Tick }
                : new OperateOrder(selection, site.Id) { Tick = session.World.Tick })));
        else if (site.Complete && Machine(site.Id) is { Operable: true } or { Function: Meitou.Data.World.TownBuildings.ChairFunction or Meitou.Data.World.TownBuildings.ThroneFunction })
            verbs.Add(("Use", () => Say(SlowModifier
                ? (Meitou.Simulation.SimCommand)new AddJob(selection, Meitou.Data.Gameplay.Ai.TaskType.OPERATE_MACHINERY, site.Id) { Tick = session.World.Tick }
                : new OperateOrder(selection, site.Id) { Tick = session.World.Tick })));
        // A training dummy: Training (97 USE_TRAINING_DUMMY, UseableStuff slot 131 for function 7), which Shift turns into the Training job.
        else if (site.Complete && Machine(site.Id) is { Function: Meitou.Data.Gameplay.Construction.MachineData.TrainingFunction })
            verbs.Add(("Training", () => Say(SlowModifier
                ? (Meitou.Simulation.SimCommand)new AddJob(selection, Meitou.Data.Gameplay.Ai.TaskType.USE_TRAINING_DUMMY, site.Id) { Tick = session.World.Tick }
                : new OperateOrder(selection, site.Id) { Tick = session.World.Tick })));
    }

    /// <summary>
    /// The first stats line of a mine, farm, well or rock (<c>ProductionBuilding::getGUIFertility</c>, docs/game/ui-screens.md "Selected building"): the resource's label, the yield as
    /// a whole percent without the sign, red to green (r = 0.8 - y, g = 0.8 y, b = 0.1), and the tooltip naming the resource in lower case.
    /// </summary>
    static void AddRichnessLine(Meitou.Simulation.Production.RichnessView rich, List<HudLine> stats)
    {
        (string Label, string Name) = rich.Resource switch
        {
            1 => ("Iron Quality:", "Iron"), 2 => ("Stone Richness:", "Stone"), 3 => ("Copper Quality:", "Copper"), 4 => ("Carbon Quality:", "Carbon"),
            5 => ("Water Available:", "Water"), _ => ("Ground Fertility:", "Fertility"),
        };
        static int Byte(float v) => (int)(Math.Clamp(v, 0, 1) * 255 + 0.5f);
        string colour = $"{Byte(0.8f - rich.Yield):x2}{Byte(0.8f * rich.Yield):x2}{Byte(0.1f):x2}";
        stats.Add(new(Label, ((int)(rich.Yield * 100)).ToString(CultureInfo.InvariantCulture), MainColour, colour,
            Loc.Tr("The richness of natural {1} present in this location. Anything over 50% is considered good.", Loc.Tr(Name).ToLower(CultureInfo.CurrentCulture))));
    }

    /// <summary>Miles per hour per world unit per second (the original's 0.22369362: a unit is a metre).</summary>
    const float MphPerUnit = 0.22369362f;

    // More of the exe's static colour tags (with MainTag and TitleTag): tooltip texts carry them as text, so they are the tags, not the palette's colours.
    const string InkTag = "#140806", BadTag = "#59231a", GoodTag = "#a8b774";

    /// <summary>
    /// The status word of a production machine (<c>ProductionBuilding</c> slot 114, <c>FUN_140300090</c>): Output full (status 1), Out of resource (status 0 with an empty input), then No workers
    /// while nobody works a machine that takes workers, else Working. Its other words (Switched off, No power) come from the power lines.
    /// </summary>
    static (string Status, bool Bad) ProductionStatus(byte machineStatus, Meitou.Simulation.Production.EfficiencyView e, bool inputsEmpty = false) =>
        machineStatus == 1 ? ("Output full", true) : machineStatus == 0 && inputsEmpty ? ("Out of resource", true)
            : e.MaxOperators > 0 && e.Operators == 0 ? ("No workers", true) : ("Working", false);

    /// <summary>
    /// The Workers line (<c>FUN_1403075b0</c>): <c>-</c> for an automatic machine (raw <c>max operators</c> 0), else operators / max; the value in ink, Bad while nobody works it; the tooltip
    /// "fully staffed" when they match, "under-staffed" (Title) below.
    /// </summary>
    static HudLine WorkersLine(int operators, int max)
    {
        if (max <= 0) return new("Workers:", "-", MainColour, Tooltip: "This machine is fully automatic.");
        string? tip = operators == max ? Loc.Tr("The machine is fully staffed.") : operators < max ? TitleTag + Loc.Tr("The machine is under-staffed.") : null;
        return new("Workers:", $"{operators.ToString(CultureInfo.InvariantCulture)}/{max.ToString(CultureInfo.InvariantCulture)}", MainColour, operators == 0 ? BadColour : null, tip);
    }

    /// <summary>
    /// The Efficiency and Max efficiency lines (slot 154 <c>FUN_140306050</c>, docs/game/ui-screens.md "Selected building", Production building): with share = operators / raw max
    /// (1 when 0), work = health x power x share x 100 and max = yield x rate, Efficiency is int(max x work)% (ink from work 90 up, else Bad) and Max efficiency int(max x 100)% (ink).
    /// Their tooltips are the slot 156 rows (Max possible efficiency; for a mine or rock, <paramref name="resource"/> under ground) and the losses, then Total.
    /// <paramref name="active"/> false (slot 164: a light by day) zeroes work; the Damage and Power rows read their own values and stay as they are.
    /// </summary>
    static void AddEfficiencyLines(Meitou.Simulation.Production.EfficiencyView e, int resource, List<HudLine> stats, bool active = true)
    {
        float share = e.MaxOperators > 0 ? 1f / e.MaxOperators * e.Operators : 1;
        // Slot 109: a rain collector's is x the rain strength (FUN_140298470).
        float work = (active ? e.Health * e.Power * (e.Rain ?? 1) : 0) * share * 100;
        float max = e.Yield * e.Rate;
        int total = (int)(max * work);
        // Slot 156: a rain collector's rain strength (FUN_14030b460, Bad at 0); else (FUN_14030a130) the rate, then the resource's loss or gain, in Bad when the yield is under 1.
        var head = e.Rain is { } rain ? [(Loc.Tr("Rain strength:"), (rain > 0 ? MainTag : BadTag) + Pc((int)(rain * 100)))]
            : new List<(string, string)> { (Loc.Tr("Max possible efficiency:"), MainTag + Pc((int)(e.Rate * 100))) };
        string? under = resource switch { 1 => "Iron under ground:", 2 => "Stone under ground:", 3 => "Copper under ground:", 4 => "Carbon under ground:", 5 => "Water under ground:", 6 => "Ground Fertility:", _ => null };
        if (under is not null)
        {
            int loss = (int)((1 - e.Yield) * e.Rate * 100);
            head.Add((Loc.Tr(under), (e.Yield < 1 ? BadTag : InkTag) + (loss < 0 ? "+" : "") + Pc(-loss)));
        }
        string Loss(float kept) => kept >= 1 ? GoodTag + Loc.Tr("OK") : BadTag + "-" + Pc((int)((1 - kept) * e.Rate * 100));
        var rows = new List<(string, string)>(head) { (Loc.Tr("Damage:"), Loss(e.Health)) };
        if (e.Consumer) rows.Add((Loc.Tr("Power:"), Loss(e.Power)));
        rows.Add((Loc.Tr("Num workers:"), e.Operators == e.MaxOperators ? GoodTag + Loc.Tr("OK") : Loss(share)));
        rows.Add((Loc.Tr("Total:"), MainTag + Pc(total)));
        stats.Add(new("Efficiency:", Pc(total), MainColour, work >= 90 ? null : BadColour, TooltipRows: rows));
        stats.Add(new("Max efficiency:", Pc((int)(max * 100)), MainColour, TooltipRows: [.. head, (Loc.Tr("Total:"), MainTag + Pc((int)(max * 100)))]));
    }

    static string Pc(int v) => v.ToString(CultureInfo.InvariantCulture) + "%";

    /// <summary>
    /// A farm's Workers line (FarmBuilding slot 155, <c>FUN_1400e0020</c>): until the crop has grown, <c>-</c> with "No workers required until the crop is ready to harvest.", then the
    /// common line.
    /// </summary>
    static HudLine FarmWorkersLine(MachineSnapshot m, Meitou.Simulation.Production.EfficiencyView e) => m.Farm is { Growth: < 1 }
        ? new("Workers:", "-", MainColour, Tooltip: "No workers required until the crop is ready to harvest.") : WorkersLine(e.Operators, e.MaxOperators);

    /// <summary>
    /// A hydroponic farm's one Efficiency line (FarmBuilding slot 154, <c>FUN_1400e2d50</c>; other farms draw none): int(max x health x power x 100), x the <c>drought multiplier</c> while
    /// out of water, in ink when health x power reaches 0.9, else Bad. No operator share and no Max efficiency line; the tooltip has no slot 156 rows, only Damage, Power (a consumer),
    /// <c>No Water:</c> (drought - 1, Bad) while out of water, and Total.
    /// </summary>
    static void AddFarmEfficiencyLine(Meitou.Simulation.Production.EfficiencyView e, List<HudLine> stats)
    {
        float work = e.Health * e.Power, max = e.Yield * e.Rate;
        int total = (int)(work * max * 100 * (e.Starved ? e.Drought : 1));
        string Loss(float kept) => kept >= 1 ? GoodTag + Loc.Tr("OK") : BadTag + "-" + Pc((int)((1 - kept) * max * 100));
        var rows = new List<(string, string)> { (Loc.Tr("Damage:"), Loss(e.Health)) };
        if (e.Consumer) rows.Add((Loc.Tr("Power:"), Loss(e.Power)));
        if (e.Starved) rows.Add((Loc.Tr("No Water:"), BadTag + Pc((int)((e.Drought - 1) * 100))));
        rows.Add((Loc.Tr("Total:"), MainTag + Pc(total)));
        stats.Add(new("Efficiency:", Pc(total), MainColour, work * 100 >= 90 ? null : BadColour, TooltipRows: rows));
    }

    /// <summary>
    /// A turret's gun lines (<c>GunClassTurret</c> slot 14 <c>FUN_14030d620</c>, section 6; docs/game/ui-screens.md "Production building"): Damage, Deviation, Range and Aim speed, labels in Main,
    /// each with its breakdown. P = 0.8 e + 0.2 (e = health x power), M the mount factor, h the height factor to the last aim point, D = output value x research. The suffixes are the exe's
    /// literal <c>x</c> and <c>*</c>; "Wall bonus" is int((M - 0.99) x 100), so +1% with no wall (kept); the deviation's skill term is 50 x the Turrets level x 0.01 + 50 x perception, so any
    /// gunner with perception shows 0 (kept); "Total speed" leaves out M while the line has it (kept).
    /// </summary>
    static void AddGunLines(MachineSnapshot m, Meitou.Simulation.Production.EfficiencyView e, Meitou.Simulation.Combat.TurretPanelView g, List<HudLine> stats)
    {
        static string I(float v) => ((int)v).ToString(CultureInfo.InvariantCulture);
        static int Round(float v) => (int)MathF.Round(v, MidpointRounding.AwayFromZero);
        static string R(float v) => Round(v).ToString(CultureInfo.InvariantCulture);
        float condition = MathF.Min(e.Health, 1) * 100, supply = MathF.Min(e.Power, 1);
        float p = Meitou.Simulation.Combat.TurretFormulas.PowerFactor(e.Health * e.Power), h = g.Height, mount = g.Mount, d = m.OutputValue * m.Research;
        float damage = d * p * h * mount, range = p * g.Range * h * mount;
        float deviation = MathF.Max(0, g.Deviation * (1 - (g.UserSkill * 50 + (g.Perception ?? 0) * 50)));
        // The rows the three breakdowns share: Power supply below 1 (Bad), Condition below 99 (Good from 95), the wall bonus (Good above 1) and the height advantage.
        var shared = new List<(string, string)>();
        if (supply < 1) shared.Add((Loc.Tr("Power supply:"), BadTag + Pc((int)(supply * 100))));
        if (condition < 99) shared.Add((Loc.Tr("Condition:"), (condition >= 95 ? GoodTag : BadTag) + Pc((int)condition)));
        var wall = (Loc.Tr("Wall bonus:"), (mount > 1 ? GoodTag : BadTag) + "+" + Pc((int)((mount - 0.99f) * 100)));
        var height = (Loc.Tr("Height advantage:"), "+" + Pc((int)((h - 1) * 100)));
        static string Metres(int v) => Loc.Tr("{1,num} m", v);
        stats.Add(new("Damage:", I(damage), MainColour,
            TooltipRows: [(Loc.Tr("Default damage:"), I(d)), .. shared, wall, height, (Loc.Tr("Total damage:"), I(damage))]));
        stats.Add(new("Deviation:", R(deviation) + "*", MainColour,
            TooltipRows: [(Loc.Tr("Base Accuracy Deviation:"), g.Deviation.ToString("F1", CultureInfo.InvariantCulture) + "*"), (Loc.Tr("User Skill:"), I(g.UserSkill * 100)),
                (Loc.Tr("User Accuracy Deviation:"), deviation.ToString("F1", CultureInfo.InvariantCulture) + "*"),
                (Loc.Tr("The amount of accuracy error, in degrees. Higher numbers are poor, 0 is perfect accuracy."), "")]));
        stats.Add(new("Range:", Metres(Round(range * 0.1f)), MainColour,
            TooltipRows: [(Loc.Tr("Default range:"), Metres((int)(g.Range * 0.1f))), .. shared, wall, height, (Loc.Tr("Total range:"), Metres((int)(range * 0.1f)))]));
        stats.Add(new("Aim speed:", R(p * g.AimSpeed * mount) + "x", MainColour,
            TooltipRows: [(Loc.Tr("Default aim speed:"), I(g.AimSpeed) + "x"), .. shared, wall, (Loc.Tr("Total speed:"), (p * g.AimSpeed).ToString("F2", CultureInfo.InvariantCulture) + "x")]));
    }

    /// <summary>
    /// The Workers and Efficiency lines of a machine's class (slot 40, docs/game/ui-screens.md "Selected building"): Stats 1 (section 6) gets them in <paramref name="stats"/>, Stats 2
    /// (section 7, only a turret's) in <paramref name="extended"/>. <paramref name="active"/> is slot 164.
    /// </summary>
    static void AddClassLines(MachineSnapshot m, Meitou.Simulation.Production.EfficiencyView e, int resource, List<HudLine> stats, List<HudLine> extended, bool active,
        Meitou.Simulation.Combat.TurretPanelView? gun = null)
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
                stats.Add(new("Healing bonus:", Loc.Tr("x{1,num}", m.OutputValue), MainColour));
                break;
            case MachinePanel.Training:
                stats.Add(WorkersLine(e.Operators, e.MaxOperators));
                stats.Add(new("Max Skill Level:", ((int)m.OutputValue - 1).ToString(CultureInfo.InvariantCulture), MainColour,
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
    /// The stats lines of a selected machine: the farm's crop and growth, the output's progress, then (section 6: slots 155, 154, 153) Workers and Efficiency per
    /// <see cref="AddEfficiencyLines"/>, the power lines, and the bench's tech level.
    /// </summary>
    void AddMachineLines(MachineSnapshot m, List<HudLine> stats, List<HudLine> extended, ref string status, ref bool bad, Meitou.Simulation.Production.RichnessView? rich = null,
        Meitou.Simulation.Combat.TurretPanelView? gun = null, bool own = false)
    {
        string N(int v) => v.ToString(CultureInfo.InvariantCulture);
        string P(float v) => ((int)(Math.Clamp(v, 0, 1) * 100)).ToString(CultureInfo.InvariantCulture) + "%";
        // A mine or rock: the ProductionBuilding status word, and no Output line (the original has none).
        if (m.Farm is null && rich is not null && m.Efficiency is { } mine) (status, bad) = ProductionStatus(m.Status, mine, m.InputsEmpty);
        else if (m.Farm is { } f)
        {
            status = f.Status switch { FarmStatus.NoWater => "No water", FarmStatus.OutputFull => "Output full", FarmStatus.Harvest => "Ready to harvest", _ => "Growing" };
            bad = f.Status is FarmStatus.NoWater or FarmStatus.OutputFull;
            stats.Add(new("Growth:", P(MathF.Min(f.Growth, 1)), MainColour));
            if (f.Growth >= 1) stats.Add(new("Ripeness:", P(f.Ripeness), MainColour, f.Growth >= 2 ? BadColour : GoodColour));
            if (f.Death > 0) stats.Add(new("Dying:", P(f.Death), MainColour, BadColour));
        }
        else if (m.Output is { } o)
        {
            // A production machine (refinery, well) reads FUN_140300090's chain; a bench or furnace keeps Meitou's own words for its status.
            if (m.Craft is null && m.Furnace is null && m.Efficiency is { } producer) (status, bad) = ProductionStatus(m.Status, producer, m.InputsEmpty);
            else
            {
                status = m.Status switch { 2 => "Working", 1 => "Output full", _ => "Idle" };
                bad = m.Status == 1;
            }
            stats.Add(new("Output:", $"{o.Name} {N(o.Count)}/{N(o.Capacity)}", MainColour));
        }
        // RainCollectorBuilding slot 114 (FUN_140301410): unless the output is full, Collecting while it rains, else Not Raining.
        if (m.Efficiency is { Rain: { } rain } && m.Status != 1) (status, bad) = rain > 0 ? ("Collecting", false) : ("Not Raining", true);
        if (m.Efficiency is { } e) AddClassLines(m, e, rich?.Resource ?? 0, stats, extended, e.Active, gun);
        // Power, in the original's words where it has them (GeneratorBuilding vfunc_114, vfunc_153); "No power" and "Low power" are Meitou's.
        if (m.Power is { } pw)
        {
            string F(float v) => v.ToString("0.#", CultureInfo.InvariantCulture);
            if (m.Wind is not null || pw.Kind is PowerKind.Generator or PowerKind.Battery)
            {
                // The production chain (FUN_140300090) as far as a finished generator reaches it: Switched off (power on clear), No workers, else Working; then the class's own word.
                bool on = m.Wind?.PowerOn ?? pw.PowerOn;
                (status, bad) = GeneratorWord(m) ?? (!on ? ("Switched off", true) : m.Efficiency is { MaxOperators: > 0, Operators: 0 } ? ("No workers", true) : ("Working", false));
            }
            if (pw.Kind == PowerKind.Battery) stats.Add(new("Charge:", P(pw.Store > 0 ? pw.Charge / pw.Store : 0), MainColour));
            // getGUIPower section 6: the wind speed at the building.
            else if (m.Wind is { } wind) stats.Add(new("Wind Speed", Loc.Tr("{1,num=fix,p=1} mph", wind.Speed * MphPerUnit), MainColour, Tooltip: "The current wind speed"));
            else if (pw.Kind == PowerKind.Generator)
            {
                stats.Add(new("Power:", F(pw.Demand), MainColour));
                if (m.Inputs.Count > 0) stats.Add(new("Fuel Consumption", P(pw.Usage), MainColour));
            }
            else if (pw.Demand > 0)
            {
                if (pw.Granted < pw.Demand) { status = pw.Granted <= 0 ? "No power" : "Low power"; bad = true; }
                stats.Add(new("Power:", $"{F(pw.Granted)}/{F(pw.Demand)}", MainColour, pw.Granted < pw.Demand ? BadColour : null));
            }
        }
        // getGUIPower section 6 on the player's machine with a power figure: a consumer's Battery toggle, then Power (FUN_140307bf0).
        if (own && m.Power is { } toggled)
        {
            if (toggled.Kind == PowerKind.Consumer)
                stats.Add(new("Battery", toggled.GetsBattery ? "On" : "Off", MainColour, toggled.GetsBattery ? GoodColour : BadBrightColour, Loc.Tr(toggled.GetsBattery
                    ? "This machine will run off of battery power if the main generators should run low or break down."
                    : "This machine will NOT run off of battery power if the main generators should run low or break down."), "meitou:machine-battery", ButtonWidth: 0.3f));
            bool on = m.Wind?.PowerOn ?? toggled.PowerOn;
            stats.Add(new("Power", on ? "On" : "Off", MainColour, on ? GoodColour : BadBrightColour, Loc.Tr(on ? "This machine is switched On" : "This machine is switched Off"),
                "meitou:machine-power", ButtonWidth: 0.3f));
        }
        if (m.TechLevel > 0) stats.Add(new("Tech level:", N(m.TechLevel), MainColour));
        // FurnaceBuilding::getGUIData section 0xf (FUN_14030b750): Total Metals (one decimal, Good from 1), then the Active button with its warning.
        if (m.Furnace is { } furnace)
        {
            stats.Add(new("Total Metals:", ((int)(furnace.Metal * 10) / 10f).ToString("0.#", CultureInfo.InvariantCulture), MainColour, furnace.Metal >= 1 ? GoodColour : BadColour));
            if (own) stats.Add(new("Active:", furnace.Burning ? "ON" : "OFF", MainColour, furnace.Burning ? GoodColour : MainColour,
                Loc.Tr("Warning:") + " " + Loc.Tr("Switching on will incinerate the entire inventory!"), "meitou:machine-incinerate", ButtonWidth: 0.4f));
        }
    }

    /// <summary>
    /// The word a generator class sets over the common chain whatever that gave (finished or not, broken or not): GeneratorBuilding (<c>FUN_140300b40</c>) <c>Out of fuel</c> (slot 149),
    /// then for a battery switched on and on a town grid: Fully Charged at its store (health x capacity), Empty at 0, else Charging / Draining by the grid's flows, else Stored;
    /// WindGeneratorBuilding (<c>FUN_1403011e0</c>) <c>No Wind</c> while the blades do not turn. Null: the chain's word stands.
    /// </summary>
    static (string, bool)? GeneratorWord(MachineSnapshot m)
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

    void BindResearchTab(UiBindings b)
    {
        var research = session.CurrentSnapshot.Research;
        var categories = research.Available.Select(r => r.Category).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        if (researchCategory is null || !categories.Contains(researchCategory)) researchCategory = categories.FirstOrDefault();
        b.Lists["ResearchTypeList"] = [.. categories.Select(c => new UiListItem(c, c))];
        b.SelectedItems["ResearchTypeList"] = researchCategory is null ? -1 : categories.IndexOf(researchCategory);
        if (research.BenchLevel <= 0) b.Text["ResearchBenchMessage"] = Loc.Tr("You currently have no research facilities. Build a research bench and assign someone to work there.");
        else b.Hidden.Add("ResearchBenchMessage");
        IndentLabel(b, "lbResearchDescription");
        if (faithfulScreens.GetTab("overview", "TabsMain") == ResearchTab)
            b.Text["Root"] = Loc.Tr("RESEARCH - LEVEL {1,num} (CURRENT RATE: {2,num=fix,p=2}x)", research.BenchLevel, research.Rate);
    }

    void ResearchAction(MyGuiEvent e)
    {
        if (e.Widget == "ResearchTypeList" && e.Action == "select") { if (e.Text != researchCategory) researchScroll = 0; researchCategory = e.Text; }
        else if (e.Action.StartsWith("research-queue:", StringComparison.Ordinal))
        {
            researchFocus = e.Action[15..];
            Say(new QueueResearch(researchFocus) { Tick = session.World.Tick });
        }
        else if (e.Action.StartsWith("research-cancel:", StringComparison.Ordinal)) Say(new CancelResearch(e.Action[16..]) { Tick = session.World.Tick });
    }

    /// <summary>
    /// A research's time left as <c>FUN_140831550</c> writes it: its hours over the current rate (when there is one) times the share still to do, rounded up to at least 1, "&lt;" before it
    /// under an hour, "{n} hr" / "{n} hrs"; with <paramref name="rateNote"/> and no rate " (at 1x rate)" after it.
    /// </summary>
    static string ResearchTime(float hours, float progress, float rate, bool rateNote)
    {
        float left = (rate > 0 ? hours / rate : hours) * (1 - progress);
        int whole = Math.Max(1, (int)MathF.Ceiling(left));
        string text = (left < 1 ? "<" : "") + Loc.Tr(whole == 1 ? "{1,num} hr" : "{1,num} hrs", whole);
        return rateNote && rate == 0 ? text + " " + Loc.Tr("(at 1x rate)") : text;
    }

    /// <summary>The description's caption for each <see cref="Meitou.Data.Gameplay.Construction.ResearchData.UnlockFields"/> field (<c>FUN_140495e00</c>).</summary>
    static readonly Dictionary<string, string> UnlockCaptions = new(StringComparer.Ordinal)
    {
        ["enable buildings"] = "New Building:", ["enable weapon type"] = "New Weapon Class:", ["enable weapon model"] = "New Weapons Grade:", ["enable armour"] = "New Armour Type:",
        ["enable item"] = "New Items:", ["enable crossbow"] = "New Crossbows:", ["enable backpack"] = "New Backpacks:", ["enable robotics"] = "New Robot Limbs:",
    };

    /// <summary>
    /// The blueprint rows (<c>DataPanelLine_Research</c>, <c>FUN_1406f9220</c>): a <c>Kenshi_Button1</c> frame across 98 % of the panel, the name from 3 % and the time right-aligned to 97 %,
    /// in Main when it can be queued, else Bad, and a green bar over the frame for the progress kept; then the queue rows and the hovered or last clicked research's description.
    /// </summary>
    void AddResearchContent(List<UiControl> controls)
    {
        if (faithfulScreens.GetTab("overview", "TabsMain") != ResearchTab) return;
        var research = session.CurrentSnapshot.Research;
        var face = new Dictionary<string, string> { ["FontName"] = "Kenshi_StandardFont_Medium", ["TextAlign"] = "Left VCenter" };
        float line = MathF.Max(18, 24 * InventoryScale);
        var ink = new Vector4(0.86f, 0.82f, 0.71f, 1);
        if (controls.FirstOrDefault(c => c.Name == "BlueprintsAvailablePanel") is { } blueprints)
        {
            // The row unit U as the base game draws it at 1080 lines (Observed): a 29 px frame 0.15 U down a 35 px pitch.
            float unit = 30.5f * Height / 1080f, pitch = 1.15f * unit, w = blueprints.Bounds.Width;
            var area = blueprints.Bounds;
            var clip = area.Intersect(blueprints.Clip ?? area);
            var rows = research.Available.Where(r => r.Category == researchCategory).OrderBy(r => r.Name, StringComparer.Ordinal).ToList();
            researchArea = clip;
            researchScroll = Math.Clamp(researchScroll, 0, Math.Max(0, rows.Count * pitch + 0.15f * unit - area.Height));
            researchStep = pitch;
            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                float top = area.Y + i * pitch + 0.15f * unit - researchScroll, height = 0.97f * unit;
                var colour = MyGuiColours.Parse(r.BenchTooLow || !r.Affordable ? BadColour : MainColour, ink);
                controls.Add(new($"research-row-{i}", "Button", new(area.X + 0.01f * w, top, 0.98f * w, height), "", "research-queue:" + r.Id, "Kenshi_Button1Skin", true,
                    new Dictionary<string, string>(), Vector4.Zero, Pickable: true, Clip: clip));
                if (r.Progress > 0)
                    controls.Add(new($"research-row-{i}-bar", "Widget", new(area.X + 0.02f * w, top + 0.2f * height, Math.Clamp(r.Progress, 0, 1) * 0.88f * w, 0.6f * height), "", "",
                        "Kenshi_LifeBarSkin", true, new Dictionary<string, string>(), Vector4.Zero, Pickable: false, Clip: clip));
                var label = new UiRect(area.X + 0.03f * w, top, 0.94f * w, height);
                controls.Add(new($"research-row-{i}-name", "TextBox", label, r.Name, "", "", true, face, Vector4.Zero, TextColour: colour, Pickable: false, Clip: clip));
                controls.Add(new($"research-row-{i}-time", "TextBox", label, ResearchTime(r.Hours, r.Progress, research.Rate, false), "", "", true,
                    new Dictionary<string, string>(face) { ["TextAlign"] = "Right VCenter" }, Vector4.Zero, TextColour: colour, Pickable: false, Clip: clip));
            }
        }
        if (controls.FirstOrDefault(c => c.Name == "ResearchQueueList") is { } queue)
            for (int i = 0; i < research.Queue.Count; i++)
            {
                var (qid, name, progress) = research.Queue[i];
                controls.Add(new($"research-queue-{i}", "Button", new(queue.Bounds.X + 6, queue.Bounds.Y + i * line, queue.Bounds.Width - 12, line),
                    $"{name}  {(int)(progress * 100)}%", "research-cancel:" + qid, "", true, face, Vector4.Zero, TextColour: i == 0 ? new(0.66f, 0.72f, 0.45f, 1) : ink,
                    HoverColour: new(1, 0.4f, 0.3f, 1), Pickable: true, Clip: queue.Bounds));
            }
        var hovered = controls.LastOrDefault(c => c.Name.StartsWith("research-row-", StringComparison.Ordinal) && c.Action.Length > 0 && c.Bounds.Contains(mouse.X, mouse.Y)
            && c.Clip?.Contains(mouse.X, mouse.Y) != false)?.Action;
        string? focus = hovered is { Length: > 15 } h ? h[15..] : researchFocus;
        if (focus is null || research.Available.FirstOrDefault(r => r.Id == focus) is not { Id: not null } option) return;
        DatapanelLines(controls, "ResearchDescriptionPanel1", ResearchDescription(option, research.Rate));
        if (option.Description.Length > 0 && controls.FirstOrDefault(c => c.Name == "ResearchDescriptionPanel2") is { } text)
            controls.Add(new("research-description", "EditBox", text.Bounds, option.Description, "", "", true,
                new Dictionary<string, string> { ["FontName"] = "Kenshi_StandardFont_Medium", ["WordWrap"] = "true", ["MultiLine"] = "true", ["Static"] = "true", ["TextAlign"] = "Left Top" },
                Vector4.Zero, TextColour: ink, Pickable: false, Clip: text.Bounds));
    }

    /// <summary>How far the blueprint rows are scrolled, their pitch and the panel they scroll in (the DatapanelGUI's own ScrollView in the original).</summary>
    float researchScroll, researchStep;
    UiRect researchArea;

    /// <summary>The wheel over the blueprint rows scrolls them a row per notch; clamped when the rows are laid out.</summary>
    bool ResearchWheel(Vector2 p, float steps)
    {
        if (!faithfulScreens.IsOpen("overview") || faithfulScreens.GetTab("overview", "TabsMain") != ResearchTab || researchStep <= 0 || !researchArea.Contains(p.X, p.Y)) return false;
        researchScroll -= MathF.Sign(steps) * researchStep;
        return true;
    }

    /// <summary>A description DatapanelGUI's lines (<see cref="AddDatapanel"/>'s geometry): rows of 0.94 / (int)(55 x its height / the screen's) of its height from 0.03 of it down.</summary>
    void DatapanelLines(List<UiControl> controls, string panel, IReadOnlyList<HudLine> lines)
    {
        if (controls.FirstOrDefault(c => c.Name == panel) is not { } host) return;
        float view = host.Bounds.Height;
        AddLines(controls, panel, lines, 0.94f / Math.Max(1, (int)(55 * view / Math.Max(1, Height))) * view, top: 0.03f * view);
    }

    /// <summary>
    /// The DESCRIPTION captions over the research and crafting panels sit at the tab's left edge in the layout; Meitou insets them 8 px at 1080 lines to line up with the panel's
    /// text (engine choice: the base game draws them flush with the frame).
    /// </summary>
    void IndentLabel(UiBindings b, string name) =>
        b.Properties[name] = new Dictionary<string, string> { ["TextAlign"] = "Left VCenter", ["TextPadding"] = $"{(8 * Height / 1080f).ToString(CultureInfo.InvariantCulture)} 0 0 0" };

    /// <summary>
    /// The description's lines (<c>FUN_140495e00</c>): the name in Title with "lvl {n}"; "ETA:" in Title with the time left; "Needs research bench level {n}" in Bad when the bench is too low;
    /// a "Cost:" line per item ("{item} x{n}", Good when the benches hold it, else Bad), or "[Paid]" in Good; "Requirements:" with the first required research and a line below with the
    /// rest (Greyed finished, Good queued, else Bad; "-" for none); one line per unlocked record under its field's caption, in Good.
    /// </summary>
    static List<HudLine> ResearchDescription(ResearchOption option, float rate)
    {
        var lines = new List<HudLine>
        {
            new(option.Name, Loc.Tr("lvl {1,num}", option.Level), TitleColour),
            new(Loc.Tr("ETA:"), ResearchTime(option.Hours, option.Progress, rate, true), TitleColour),
        };
        if (option.BenchTooLow) lines.Add(new(Loc.Tr("Needs research bench level {1,num}", option.Level), "", BadColour));
        if (option.Paid) lines.Add(new(Loc.Tr("Cost:"), "[" + Loc.Tr("Paid") + "]", null, GoodColour));
        else foreach (var (item, count, held) in option.Cost) lines.Add(new(Loc.Tr("Cost:"), $"{item} x{count.ToString(CultureInfo.InvariantCulture)}", null, held ? GoodColour : BadColour));
        static string Requirement((string Name, bool Finished, bool Queued) r) => Tag(r.Finished ? GreyColour : r.Queued ? GoodColour : BadColour) + r.Name.Replace("#", "##", StringComparison.Ordinal);
        var required = option.Requirements;
        lines.Add(new(Loc.Tr("Requirements:"), required.Count == 0 ? "-" : Requirement(required[0]) + (required.Count > 1 ? ", " : "")));
        if (required.Count > 1) lines.Add(new("", string.Join(", ", required.Skip(1).Select(Requirement))));
        foreach (var (field, name) in option.Unlocks) lines.Add(new(Loc.Tr(UnlockCaptions.GetValueOrDefault(field, field)), name, null, GoodColour));
        return lines;
    }
}
