using System.Globalization;
using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Data.Ui;
using Meitou.Game.Building;
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
/// it), QUEUE the queue (click to remove, drag to reorder), DESCRIPTION the hovered or last clicked research. The original's row widgets and colours are
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
    /// The selected building's own window while the shown window is an order's target (<see cref="OwnSuffix"/> screens; null: none), and one whose
    /// window the player closed (not reopened until the selection changes).
    /// </summary>
    internal string? OwnMachine;

    /// <summary>
    /// A selected building whose own window the player closed beside a target, and the building selected when the target opened (the order closed
    /// its window; only a selection change opens one again).
    /// </summary>
    string? _ownClosed, _targetSelection;

    /// <summary>
    /// The selected building's name when the shown window opened: its caption once the selection moves on.
    /// </summary>
    string? _shownName;

    /// <summary>
    /// The suffix of the second set of machine screens, the selected building's own window shown beside an order's target.
    /// </summary>
    internal const string OwnSuffix = "-own";

    /// <summary>
    /// Every machine screen: the shown window's three kinds and the selected building's own window's.
    /// </summary>
    internal static readonly string[] MachineScreens = ["building", "bench", "storage", "building-own", "bench-own", "storage-own"];

    /// <summary>
    /// Whether the shown building window is currently placed beside its looter's inventory.
    /// </summary>
    bool _machineLinked;

    /// <summary>
    /// The last positioned machine and its standalone offset before it was linked to a character inventory.
    /// </summary>
    string? _placedMachine;

    /// <summary>
    /// A standalone position to restore after the character inventory closes, including a position dragged by the player.
    /// </summary>
    Vector2? _standaloneMachineOffset;

    /// <summary>
    /// The machine at site <paramref name="site"/> in the snapshot; null for none.
    /// </summary>
    internal MachineSnapshot? Machine(string? site) =>
        site is null ? null : player.Session.CurrentSnapshot.Machines.FirstOrDefault(m => m.Site == site);

    /// <summary>
    /// The window a machine opens: the building window (inputs, an output or no storage), the storage window (a plain storage), else the bench
    /// window.
    /// </summary>
    internal static string MachineScreen(MachineSnapshot m) =>
        !m.HasStorage || m.Inputs.Count > 0 || m.Output is not null ? "building"
        : m.TechLevel == 0 ? "storage"
        : "bench";

    /// <summary>
    /// Whether <paramref name="screen"/> is a machine window (<see cref="MachineScreens"/>).
    /// </summary>
    internal static bool IsMachineScreen(string screen) => MachineScreens.Contains(screen);

    /// <summary>
    /// The tooltip of a machine's item at <paramref name="site"/>, naming it by its site and index for the mods' tooltip lines.
    /// </summary>
    static TooltipRuntime.ItemTip MachineTip(InventoryItemView item, string site) =>
        TooltipRuntime.ItemTip.Of(item) with { Owner = ItemOwner.AtSite(site), Index = item.Index };

    /// <summary>
    /// The kind of a machine screen (building, bench or storage), the selected building's own window's as the shown one's; any other screen as is.
    /// </summary>
    internal static string MachineKind(string screen) =>
        screen.EndsWith(OwnSuffix, StringComparison.Ordinal) && IsMachineScreen(screen) ? screen[..^OwnSuffix.Length] : screen;

    /// <summary>
    /// The site a machine screen shows: the selected building's own window's (<see cref="OwnMachine"/>), else the shown one's.
    /// </summary>
    internal string? SiteOf(string screen) => screen.EndsWith(OwnSuffix, StringComparison.Ordinal) ? OwnMachine : ShownMachine;

    /// <summary>
    /// Opens the selected machine's window and closes it when the selection moves on; beside an order's target, the selected building's own
    /// window opens as a second machine window.
    /// </summary>
    internal void SyncMachineScreens()
    {
        if (player.FaithfulScreens is null) return;

        var focus = player.BuildingSelection.BuildingFocus();

        // A rock's natural site is no registry building: its machine, once mined, is enough.
        var selected = SiteMachine(MachineSiteOf(focus));
        if (selected is { Inputs.Count: 0, Output: null, HasStorage: false, Craft: null }) selected = null;

        SyncShownMachine(selected);
        SyncOwnMachine(selected);
    }

    /// <summary>
    /// Tells the world which machines' windows are shown (<see cref="Simulation.Player.PlayerState.ShownMachines"/>): only their storages are
    /// published with their items. A new list only when they changed, so the paused world publishes again only then.
    /// </summary>
    internal void ShareShownMachines()
    {
        var state = player.Session.World.Player;
        var shown = state.ShownMachines;
        bool same = shown.Count == Count(ShownMachine) + Count(OwnMachine) && Listed(shown, ShownMachine) && Listed(shown, OwnMachine);
        if (same) return;

        state.ShownMachines = new[] { ShownMachine, OwnMachine }.OfType<string>().ToArray();

        static int Count(string? site) => site is null ? 0 : 1;
        static bool Listed(IReadOnlyList<string> list, string? site) => site is null || list.Contains(site);
    }

    /// <summary>
    /// The shown window: an order's target (an open container, a storage opened by order) or else the selected machine's own window.
    /// </summary>
    void SyncShownMachine(MachineSnapshot? selected)
    {
        // An open container takes the window; a storage opened by order is the inventory's target, not the selection's window, so a selection
        // change leaves it open (FUN_140729c80 closes only the selected building's own window, Verified).
        var m = player.Session.CurrentSnapshot.Machines.FirstOrDefault(c => c.IsContainer) ?? OrderedStorage() ?? selected;
        if (ClosedMachine is not null && ClosedMachine != m?.Site) ClosedMachine = null;

        if (ShownMachine is not null && (m is null || m.Site != ShownMachine)) HideShownMachine();

        if (ShownMachine is not null && Machine(ShownMachine) is { } shown && !player.FaithfulScreens.IsOpen(MachineScreen(shown)))
        {
            ShownMachineClosed(shown);
            return;
        }

        if (m is null || ClosedMachine == m.Site) return;

        bool alone = !m.IsContainer && player.Build.StorageWithInventory != m.Site;
        bool linked = !alone && player.FaithfulScreens.IsOpen("inventory");
        if (ShownMachine == m.Site)
        {
            // The looter's window closing closes the container or storage opened with it (FUN_1406e53d0, Verified); a storage of the player's
            // still selected goes back to its own window, as its selection shows it again (Observed, docs/game/ui-screens.md).
            if (_machineLinked && !linked && (m.IsContainer || m.Site != selected?.Site))
            {
                player.FaithfulScreens.Close(MachineScreen(m));
                ShownMachineClosed(m);
            }
            else if (_machineLinked != linked) PositionMachine(MachineScreen(m), m.Site, linked);

            return;
        }

        if (alone && OwnMachine == m.Site)
            PromoteOwnMachine(m);
        else
            OpenMachine(m, alone);
    }

    /// <summary>
    /// The target gone, the selected building's own window stays where it is and becomes the shown one (the original's own window is not touched
    /// when the target closes, <c>FUN_1406e53d0</c>).
    /// </summary>
    void PromoteOwnMachine(MachineSnapshot m)
    {
        string from = OwnScreen(m), to = MachineScreen(m);
        var offset = player.FaithfulScreens.GetOffset(from);
        CloseOwnMachine();
        player.InterfaceSounds.SoundScreens.Remove(from);

        (ShownMachine, _shownName, _targetSelection) = (m.Site, player.BuildingSelection.Selected?.Name, m.Site);
        (_placedMachine, _machineLinked, _standaloneMachineOffset) = (m.Site, false, null);
        player.FaithfulScreens.Open(to);
        player.FaithfulScreens.SetOffset(to, offset);
        player.InterfaceSounds.SoundScreens.Add(to);
    }

    /// <summary>
    /// The player's storage shown beside the looter's inventory after a storage order, whatever is selected now; null for none.
    /// </summary>
    MachineSnapshot? OrderedStorage() =>
        _machineLinked && ShownMachine is not null && player.Build.StorageWithInventory == ShownMachine ? Machine(ShownMachine) : null;

    /// <summary>
    /// Whether the shown window is an order's target (an open container, or a window linked beside the looter's inventory).
    /// </summary>
    bool ShownIsTarget() => Machine(ShownMachine) is { } m && (m.IsContainer || _machineLinked);

    /// <summary>
    /// The selected building's own window beside an order's target (<c>DAT_1421337b0</c> keeps it apart from the target, <b>Verified</b>:
    /// selecting a building opens it, <c>FUN_1406e6640</c>; a selection change closes only it, <c>FUN_140729c80</c>): opened when the selection
    /// changes to another machine while the target is shown, closed with the selection or the target; a window the player closed stays closed
    /// until the selection changes.
    /// </summary>
    void SyncOwnMachine(MachineSnapshot? selected)
    {
        if (OwnMachine is not null && Machine(OwnMachine) is { } own && !player.FaithfulScreens.IsOpen(OwnScreen(own)))
            (_ownClosed, OwnMachine) = (OwnMachine, null);

        if (_ownClosed is not null && _ownClosed != selected?.Site) _ownClosed = null;
        if (_targetSelection is not null && _targetSelection != selected?.Site) _targetSelection = null;

        // The target gone, the selected building's own window waits to become the shown one (PromoteOwnMachine).
        if (ShownMachine is null && OwnMachine is not null && OwnMachine == selected?.Site) return;

        bool wanted = ShownIsTarget() && selected is not null && selected.Site != ShownMachine && selected.Site != _targetSelection
            && selected.Site != _ownClosed;
        var want = wanted ? selected : null;
        if (OwnMachine is not null && OwnMachine != want?.Site) CloseOwnMachine();
        if (want is null || OwnMachine == want.Site) return;

        OwnMachine = want.Site;
        string screen = OwnScreen(want);
        player.ScrollBars.InventoryScroll.Remove(screen);
        player.FaithfulScreens.Open(screen);
        player.InventoryLayout.PlaceMachineWindow(screen);
    }

    /// <summary>
    /// The screen of the selected building's own window for machine <paramref name="m"/>.
    /// </summary>
    static string OwnScreen(MachineSnapshot m) => ScreenFor(m, own: true);

    /// <summary>
    /// The screen machine <paramref name="m"/> shows in: the shown window's, or with <paramref name="own"/> the selected building's own window's.
    /// </summary>
    internal static string ScreenFor(MachineSnapshot m, bool own) => MachineScreen(m) + (own ? OwnSuffix : "");

    /// <summary>
    /// Closes the selected building's own window.
    /// </summary>
    void CloseOwnMachine()
    {
        foreach (string screen in MachineScreens.Where(s => s.EndsWith(OwnSuffix, StringComparison.Ordinal))) player.FaithfulScreens.Close(screen);

        OwnMachine = null;
    }

    /// <summary>
    /// Closes the shown window once its machine is no longer selected or its container no longer open; a container's window linked beside the
    /// looter's inventory takes the inventory with it (<c>FUN_1406e53d0</c>, <b>Verified</b>: closing the target window closes the main one).
    /// </summary>
    void HideShownMachine()
    {
        // A linked window that is not the player's storage opened by order is a container.
        bool container = _machineLinked && player.Build.StorageWithInventory != ShownMachine;
        player.FaithfulScreens.Close("building");
        player.FaithfulScreens.Close("bench");
        player.FaithfulScreens.Close("storage");
        if (container) player.InventoryWindows.CloseInventory();

        ShownMachine = null;
        player.Build.StorageWithInventory = null;
    }

    /// <summary>
    /// The shown window was closed: a container closes in the world, and a window linked beside the looter's inventory closes the inventory too
    /// (<c>FUN_1406e53d0</c>, <b>Verified</b>: closing the target window closes the main one). It stays closed until the selection changes.
    /// </summary>
    void ShownMachineClosed(MachineSnapshot shown)
    {
        if (shown.IsContainer) player.Say(new CloseContainer { Tick = player.Session.World.Tick });
        if (_machineLinked) player.InventoryWindows.CloseInventory();

        ClosedMachine = ShownMachine;
        ShownMachine = null;
        player.Build.StorageWithInventory = null;
    }

    /// <summary>
    /// Shows machine <paramref name="m"/>'s window: alone, or with the character's inventory beside it, laid out as an order opens the pair.
    /// </summary>
    void OpenMachine(MachineSnapshot m, bool alone)
    {
        ShownMachine = m.Site;
        _shownName = player.BuildingSelection.Selected?.Name;
        _targetSelection = MachineSiteOf(player.BuildingSelection.BuildingFocus());
        string screen = MachineScreen(m);
        player.ScrollBars.InventoryScroll.Remove(screen);

        // The character's inventory opens with an order-driven container window (the original's order 26 opens the looter's own window and the
        // target's together) and with a machine's; selecting a selected machine, storage, bench or mined rock shows its window alone (the original's
        // windows of a selected ore storage and electrical workbench have no inventory beside them). The order first closes every other
        // inventory window (FUN_1407918a0 calls the close-all FUN_1406e5740, Verified): the pinned ones here.
        if (!alone && player.Selected().Count > 0)
        {
            player.InventoryWindows.ClosePinnedInventories();
            if (!player.FaithfulScreens.IsOpen("inventory")) player.FaithfulScreens.Open("inventory");
        }

        player.FaithfulScreens.Open(screen);
        PositionMachine(screen, m.Site, !alone && player.FaithfulScreens.IsOpen("inventory"), opening: true);
    }

    /// <summary>
    /// Links a building beside the looter (from the opening margin when <paramref name="opening"/>) or restores its prior standalone position when
    /// the character inventory closes.
    /// </summary>
    void PositionMachine(string screen, string site, bool linked, bool opening = false)
    {
        if (_placedMachine != site) _standaloneMachineOffset = null;
        else if (linked && !_machineLinked) _standaloneMachineOffset = player.FaithfulScreens.GetOffset(screen);

        // The link is recorded first: a linked window is placed in the inventory's window row, which asks for it (LinkedScreen).
        var restore = !linked && _machineLinked ? _standaloneMachineOffset : null;
        _placedMachine = site;
        _machineLinked = linked;

        if (restore is { } offset)
            player.FaithfulScreens.SetOffset(screen, offset);
        else if (linked)
            player.InventoryLayout.PlaceWindowRow(opening);
        else
            player.InventoryLayout.PlaceMachineWindow(screen);
    }

    /// <summary>
    /// The shown machine's or container's window while it is open linked beside the looter's inventory, else null.
    /// </summary>
    internal string? LinkedScreen()
    {
        if (!_machineLinked || Machine(ShownMachine) is not { } m) return null;

        string screen = MachineScreen(m);
        return player.FaithfulScreens.IsOpen(screen) ? screen : null;
    }

    /// <summary>
    /// The machine site a selected building's window shows: its own site, else the <c>townwork:</c> machine of a town building or layout object of
    /// the player's faction, as the original opens the window of any complete building the player's faction owns (<c>FUN_1405504a0</c>,
    /// docs/game/ui-input.md "Building picking and selection"); null for none.
    /// </summary>
    string? MachineSiteOf(BuildingSelection.SelectedBuilding? focus)
    {
        if (focus is null) return null;
        if (focus.SiteId is { } site) return site;

        string? own = player.Session.CurrentSnapshot.InspectedOwnMachine;
        return own == ProductionSystem.TownWorkPrefix + focus.Instance ? own : null;
    }

    /// <summary>
    /// The machine of a focused site: a natural site's (a rock's, once mined, is no registry building), a town building's or a complete building's;
    /// null for any other.
    /// </summary>
    MachineSnapshot? SiteMachine(string? site)
    {
        if (site is not { } id) return null;

        bool built = ProductionSystem.IsNatural(id) || ProductionSystem.IsTownWork(id)
            || player.Session.CurrentSnapshot.Buildings.FirstOrDefault(s => s.Id == id) is { Complete: true };
        return built ? Machine(id) : null;
    }

    /// <summary>
    /// Binds the shown machine's window: the storage window, a research bench's or the generic building window with its inputs and output.
    /// </summary>
    internal void BindMachineScreen(string id, UiBindings b)
    {
        var m = Machine(SiteOf(id));
        string kind = MachineKind(id);

        if (kind == "storage")
        {
            BindStorageScreen(id, m, b);
            return;
        }

        b.Text["Root"] = MachineCaption(id, m).ToUpperInvariant();

        if (kind == "bench")
        {
            BindBenchScreen(id, m, b);
            return;
        }

        BindCraftingRow(m, b);
        b.TemplateScale = player.UiScale;

        // The layout's OutputText is an editable EditBox; it is a status line, so read-only here: a click or Tab gave it the keys and typing went
        // into it (#383).
        b.Properties["OutputText"] = new Dictionary<string, string> { ["ReadOnly"] = "true" };
        if (m is null) return;

        BindInputs(m, b);
        BindOutput(m, b);
        BindColumnTitles(m, b);
        BindBuildingGeometry(id, m, b);
    }

    /// <summary>
    /// A machine window's caption: a container's item name, the selected building's name for its own window, the name the shown window opened
    /// with otherwise.
    /// </summary>
    string MachineCaption(string id, MachineSnapshot? m)
    {
        if (m is { IsContainer: true }) return player.Build.Services?.ItemName(m.Record) ?? "CONTAINER";

        string? name = id.EndsWith(OwnSuffix, StringComparison.Ordinal) ? player.BuildingSelection.Selected?.Name : _shownName;
        return name ?? player.BuildingSelection.Selected?.Name ?? "BUILDING";
    }

    /// <summary>
    /// The original's generic container window: caption STORAGE, the limited item as heading, and Max Capacity (red "(Full)" once the stack is
    /// full).
    /// </summary>
    void BindStorageScreen(string id, MachineSnapshot? m, UiBindings b)
    {
        b.Text["Root"] = "STORAGE";
        b.Text["TypeText"] = m?.StorageLimitName ?? "";
        if (string.IsNullOrEmpty(m?.StorageLimitName)) b.Hidden.Add("TypeText");

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
        if (m is { StorageCapacity: > 0 }) b.Hidden.Add("ArrangeButton");
        else
        {
            b.Actions["ArrangeButton"] = "ArrangeButton";
            b.Hidden.Add("TypeText");
        }

        if (m?.StorageSize is { } size)
        {
            player.InventoryLayout.BindContainerGeometry(id, b, size.Width, size.Height);
        }

        b.TemplateScale = player.UiScale;
    }

    /// <summary>
    /// A research bench's window: the RESEARCH button (hidden below tech level 1), the layout's ARRANGE under the grid and the window sized
    /// around its storage grid.
    /// </summary>
    void BindBenchScreen(string id, MachineSnapshot? m, UiBindings b)
    {
        b.Disabled.Remove("ResearchButton");
        b.Actions["ResearchButton"] = "ResearchButton";
        if (m is not { TechLevel: > 0 }) b.Hidden.Add("ResearchButton");

        b.Disabled.Remove("ArrangeButton");
        b.Actions["ArrangeButton"] = "ArrangeButton";
        if (m is not { HasStorage: true }) b.Hidden.Add("ArrangeButton");

        if (m?.StorageSize is { } size)
        {
            player.InventoryLayout.BindContainerGeometry(id, b, size.Width, size.Height);
        }

        b.TemplateScale = player.UiScale;
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

            if (k == 0 && FurnaceSize(m) is not null)
            {
                BindFurnaceInput(b);
                continue;
            }

            if (k >= m.Inputs.Count)
            {
                b.Hidden.Add($"Input{n}Panel");
                continue;
            }

            var input = m.Inputs[k];

            if (input.Idle)
            {
                BindIdleInput(n, b);
                continue;
            }

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
    /// A bench section the bound craft takes nothing from (<c>FUN_1402b6110</c>, its ingredient cleared): name "-", no status, bar 0.
    /// </summary>
    static void BindIdleInput(string n, UiBindings b)
    {
        b.Text[$"Input{n}Text"] = "-";
        b.Text[$"Input{n}StatusText"] = "";
        b.Progress[$"Input{n}ProgressBar"] = 0;
    }

    /// <summary>
    /// A furnace's one input is its 12 x 7 grid (FurnaceBuilding::setupFromData), drawn in in1 by AddMachineContent; its label, status and bar are
    /// an engine choice (left empty).
    /// </summary>
    static void BindFurnaceInput(UiBindings b)
    {
        b.Text["Input1Text"] = "";
        b.Text["Input1StatusText"] = "";
        b.Hidden.Add("Input1ProgressBar");
    }

    /// <summary>
    /// The building window's output: its stock and bar, a bench's current craft, or the column hidden. A farm's bar is the plant being harvested
    /// (ProductionBuilding slot 179 <c>FUN_140298810</c>: the output entry's progress, which <c>FUN_1400e59e0</c> works), so 0 while it grows.
    /// </summary>
    static void BindOutput(MachineSnapshot m, UiBindings b)
    {
        if (m.Output is { } output)
        {
            string count = output.Count.ToString(CultureInfo.InvariantCulture);
            string capacity = output.Capacity.ToString(CultureInfo.InvariantCulture);
            b.Text["OutputText"] = $"{output.Name}\n{count}/{capacity}";
            b.Progress["OutputProgressBar"] = m.Farm is { } farm ? farm.HarvestProgress : output.Fill;
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
        int inputCount = FurnaceSize(m) is not null ? 1 : m.Inputs.Count;
        if (inputCount > 0) b.Text["InputTitleText"] = Loc.Plural("Input", "Inputs", inputCount);

        b.Text["OutputTitleText"] = Loc.Tr("Output");
        if (m.Inputs.Count == 0 && FurnaceSize(m) is null) b.Hidden.Add("InputTitleText");
        if (m.Inputs.Count == 0 && FurnaceSize(m) is null) b.Hidden.Add("InputColumn");
    }

    /// <summary>
    /// Fits all building panels and their frame in the same binding pass, from the layout and section dimensions rather than a previous frame.
    /// </summary>
    void BindBuildingGeometry(string id, MachineSnapshot machine, UiBindings bindings)
    {
        var geometry = new InventoryGeometry(player.FaithfulScreens.Layout(id), player.Width, player.Height,
            player.InterfaceScale, player.UiScale);
        var cell = player.InventoryCellSize;
        var furnace = FurnaceSize(machine);
        Vector2[] inputs = furnace is { } grid
            ? [new Vector2(grid.Width, grid.Height) * cell]
            : machine.Inputs.Take(2).Select(s => new Vector2(s.Width, s.Height) * cell).ToArray();
        Vector2? output = machine.Output is { } slot ? new Vector2(slot.Width, slot.Height) * cell
            : machine.Craft is { } craft ? new Vector2(craft.OutputWidth, craft.OutputHeight) * cell : null;
        geometry.FitBuilding(inputs, output, machine.Craft is not null, furnace is not null);
        geometry.Bind(bindings);
    }

    /// <summary>
    /// A furnace's item grid with its items (its <c>in1</c> section, kept as the machine's storage); null for any other machine, and for a
    /// furnace until its window is shown (<see cref="ShareShownMachines"/>).
    /// </summary>
    internal static InventoryView? FurnaceGrid(MachineSnapshot? m) => m is { Furnace: not null, Inputs.Count: 0, Storage: { } grid } ? grid : null;

    /// <summary>
    /// A furnace's item grid's size, published whether or not its window is shown; null for any other machine.
    /// </summary>
    internal static (int Width, int Height)? FurnaceSize(MachineSnapshot? m) => m is { Furnace: not null, Inputs.Count: 0 } ? m.StorageSize : null;

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
        if (Machine(SiteOf(id)) is not { } m) return;

        if (MachineKind(id) is "bench" or "storage")
        {
            AddStorageGrid(id, controls, m);
            return;
        }

        for (int k = 0; k < Math.Min(2, m.Inputs.Count); k++) AddSlot(controls, m, $"in{k + 1}", m.Inputs[k], 10 + k);

        if (FurnaceSize(m) is { } size && m.Furnace is { } burn) AddFurnaceGrid(controls, m, size, burn);

        AddOutputSlot(controls, m);
    }

    /// <summary>
    /// A bench's or storage's grid and its items, placed in the window's <c>backpack_content</c>; the items once the snapshot carries them (the
    /// publish after the window was shown).
    /// </summary>
    void AddStorageGrid(string id, List<UiControl> controls, MachineSnapshot m)
    {
        if (!m.HasStorage) return;

        player.InventoryLayout.ArrangeContainerGrid(id, controls);
        if (m.Storage is not { } grid || controls.FirstOrDefault(c => c.Name == "backpack_content") is not { } slot) return;

        var area = slot.Bounds;

        foreach (var item in grid.Items)
        {
            if (player.InventoryWindows.Lifted(ItemOwner.AtSite(m.Site), 1000 + item.Index)) continue;

            player.ItemCells.AddItemCell(controls, $"machine-item-{item.Index}",
                new(area.X + item.X * area.Width / grid.Width, area.Y + item.Y * area.Height / grid.Height, item.Width * area.Width / grid.Width,
                    item.Height * area.Height / grid.Height),
                item.Record, item.Material, item.Name, item.Quantity, MachineTip(item, m.Site), "MachineItem", 1000 + item.Index,
                slot.Bounds.Intersect(slot.Clip ?? slot.Bounds), charge: item.Charge, colour: item.Colour, rotated: item.Rotated);
        }
    }

    /// <summary>
    /// One input or output section, sized to its item's footprint, with its item (or a bench's faded ghost of the expected item).
    /// </summary>
    void AddSlot(List<UiControl> controls, MachineSnapshot m, string name, MachineSlotView view, int code)
    {
        if (SizeSection(controls, name) is not var (section, clip)) return;

        // The item sits at the section's top left at its own footprint (a bench's sections can be larger).
        var b = view.ItemWidth > 0
            ? new UiRect(section.X, section.Y, view.ItemWidth * player.InventoryCell, view.ItemHeight * player.InventoryCellHeight)
            : section;

        // An idle bench section has no ghost and is disabled while empty (FUN_1402bb470, setEnabled through FUN_14014ea30).
        if (view is { Idle: true, Count: <= 0 }) DisableSection(controls, name);
        if (view.Count <= 0 && m.Craft is not null && !view.Idle)
            AddItemGhost(controls, $"machine-{name}", b, view.Item, "", clip);
        if (view.Count <= 0 || player.InventoryWindows.Lifted(ItemOwner.AtSite(m.Site), code)) return;

        // The stack's tooltip is an inventory item's (the original's sections hold real items, tipped by the inventory code).
        var tip = view.Held is { } held ? TooltipRuntime.ItemTip.Of(held) : null;
        player.ItemCells.AddItemCell(controls, $"machine-{name}", b, view.Item, "", view.Name, view.Count, tip, "MachineItem", code, clip);
    }

    /// <summary>
    /// Applies the cell tile to a section already fitted by the geometry binding; returns its bounds and inherited clip.
    /// </summary>
    (UiRect Bounds, UiRect? Clip)? SizeSection(List<UiControl> controls, string name)
    {
        int index = controls.FindIndex(c => c.Name == name);
        if (index < 0) return null;

        var slot = controls[index];
        var b = slot.Bounds;
        var clip = slot.Clip;
        controls[index] = slot with
        {
            Properties = new Dictionary<string, string>(slot.Properties)
            {
                ["TileSize"] = FormattableString.Invariant($"{player.InventoryCell} {player.InventoryCellHeight}"),
                ["State"] = "normal",
            },
        };

        return (b, clip);
    }

    /// <summary>
    /// Draws a section in the slot skin's red <c>disabled</c> state.
    /// </summary>
    static void DisableSection(List<UiControl> controls, string name)
    {
        int index = controls.FindIndex(c => c.Name == name);
        if (index < 0) return;

        var slot = controls[index];
        controls[index] = slot with { Properties = new Dictionary<string, string>(slot.Properties) { ["State"] = "disabled" } };
    }

    /// <summary>
    /// A furnace's input grid of <paramref name="size"/> and its items (none until the snapshot carries them, the publish after the window was
    /// shown).
    /// </summary>
    void AddFurnaceGrid(List<UiControl> controls, MachineSnapshot m, (int Width, int Height) size, FurnaceView burn)
    {
        // The furnace's grid in in1, its items the storage's (picked up as storage items); disabled while it burns
        // (InventorySection::setEnabled(!active)).
        AddSlot(controls, m, "in1", new MachineSlotView("", "", 0, 0, 0, size.Width, size.Height), -1);
        if (burn.Burning) DisableSection(controls, "in1");
        int gi = controls.FindIndex(c => c.Name == "in1");

        if (gi >= 0 && FurnaceGrid(m) is { } fg)
        {
            var g = controls[gi];

            foreach (var item in fg.Items)
            {
                if (player.InventoryWindows.Lifted(ItemOwner.AtSite(m.Site), 1000 + item.Index)) continue;

                player.ItemCells.AddItemCell(controls, $"machine-item-{item.Index}",
                    new(g.Bounds.X + item.X * player.InventoryCell, g.Bounds.Y + item.Y * player.InventoryCellHeight, item.Width * player.InventoryCell,
                        item.Height * player.InventoryCellHeight),
                    item.Record, item.Material, item.Name, item.Quantity, MachineTip(item, m.Site), "MachineItem", 1000 + item.Index,
                    g.Clip, charge: item.Charge, colour: item.Colour, rotated: item.Rotated);
            }
        }
    }

    /// <summary>
    /// The output section: the machine's output slot, a bench's finished craft, or the current craft's item faded in the empty output.
    /// </summary>
    void AddOutputSlot(List<UiControl> controls, MachineSnapshot m)
    {
        if (m.Output is { } output) AddSlot(controls, m, "out", output, 8);
        else if (m.Craft is { } craft) AddCraftOutput(controls, m, craft);
    }

    /// <summary>
    /// A bench's output: the section as large as the largest thing the bench makes (docs/game/buildings-production.md "Crafting"), with the first
    /// finished item at its top left, or the current craft's item faded in the empty section; both at the item's footprint.
    /// </summary>
    void AddCraftOutput(List<UiControl> controls, MachineSnapshot m, CraftView craft)
    {
        if (SizeSection(controls, "out") is not var (ob, clip)) return;

        float cell = player.InventoryCell;

        if (craft.Done is [var made, ..])
        {
            if (player.InventoryWindows.Lifted(ItemOwner.AtSite(m.Site), 8)) return;

            var area = new UiRect(ob.X, ob.Y, made.Width * cell, made.Height * player.InventoryCellHeight);
            var tip = TooltipRuntime.ItemTip.Of(made);
            player.ItemCells.AddItemCell(controls, "machine-out", area, made.Record, made.Material, made.Name, made.Quantity, tip, "MachineItem", 8,
                clip, colour: made.Colour);
            return;
        }

        if (CraftCurrent(craft) is not { } current) return;

        // The empty output shows the current craft's item, faded.
        string[] key = current.Key.Split('|');
        var ghost = new UiRect(ob.X, ob.Y, craft.FrontWidth * cell, craft.FrontHeight * player.InventoryCellHeight);
        AddItemGhost(controls, "machine-out", ghost, key[0], key.Length > 1 ? key[1] : "", clip);
    }

    /// <summary>
    /// A machine window event: the bench's RESEARCH opens the overview's research tab, its ARRANGE arranges its grid, the building's QUEUE opens
    /// its Craft tab.
    /// </summary>
    internal void MachineAction(MyGuiEvent e)
    {
        string kind = MachineKind(e.Screen);
        string? site = SiteOf(e.Screen);
        if (kind == "bench" && e.Widget == "ResearchButton")
        {
            player.FaithfulScreens.Open("overview");
            player.FaithfulScreens.SetTab("overview", "TabsMain", TutorialPanel.ResearchTab);
        }
        else if (kind is "bench" or "storage" && e.Widget == "ArrangeButton" && site is not null)
            player.Say(new ArrangeMachine(site) { Tick = player.Session.World.Tick });
        else if (kind == "building" && e.Widget == "QueueButton" && site is not null)
            OpenCraftTab(site);
    }

    /// <summary>
    /// The verb for a complete machine characters work: the original's Use (order 87, <c>FUN_14079b2d0</c>); with Shift held on the click it becomes
    /// the Operating machine job, as the original's dispatch turns a menu order into a job (<c>FUN_1407fa650</c>).
    /// </summary>
    internal void AddMachineVerb(List<(string Caption, Action Give)> verbs, Simulation.Construction.BuildingSnapshot site,
        IReadOnlyList<CharacterId> selection)
    {
        // An automatic machine: Auto-Haul (152) in place of Use; with Shift the 152 job (its companion 124 follows), else the 152 order, which leaves
        // once the output holds nothing a storage takes.
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
                stats.Add(MaxSkillLine(m.OutputValue));
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
    /// A training dummy's <c>Max Skill Level:</c> line (UseableStuff slot 40 for BF_TRAINING): int(<paramref name="outputValue"/>) − 1.
    /// </summary>
    internal static PlayerInterface.HudLine MaxSkillLine(float outputValue) =>
        new("Max Skill Level:", ((int)outputValue - 1).ToString(CultureInfo.InvariantCulture), PlayerInterface.MainColour,
            Tooltip: "This is the maximum skill a person can train up to using this equipment. For anyone higher it is a waste of time.");

    /// <summary>
    /// The stats lines of a selected machine: the farm's yield and growth rate, the output's progress, then (section 6: slots 155, 154, 153) Workers and
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
    /// The status word and the lines of a machine's product: a mine's or rock's status, a farm's yield and growth rate, or the output's stock.
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
            FarmPanel.AddStats(m, stats);
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

            stats.Add(ConsumerPowerLine(pw.Granted, pw.Demand));
        }

        // A machine in no town is on no frame, so its demand stays 0 and the Power line above is not drawn.
        if (pw.Kind == PowerKind.Consumer && pw.NoTown) stats.Add(NoOutpostLine());
    }

    /// <summary>
    /// Meitou only: a consumer's line when its building belongs to no outpost (NO_TOWN), which gets no power however many generators stand nearby
    /// (docs/game/buildings-production.md "Power"; the original gives no hint).
    /// </summary>
    static PlayerInterface.HudLine NoOutpostLine()
    {
        string tip = Loc.Tr(
            "This building stands outside every outpost's build limit, so no generator can power it. Dismantle it and build it again inside the red ring.");
        return new(Loc.Tr("Outpost:"), Loc.Tr("None"), PlayerInterface.MainColour, PlayerInterface.BadColour, tip);
    }

    /// <summary>
    /// A consumer's <c>Power:</c> line (slot 153): granted over demand, the value Bad while it gets less than it asks.
    /// </summary>
    internal static PlayerInterface.HudLine ConsumerPowerLine(float granted, float demand)
    {
        static string F(float v) => v.ToString("0.#", CultureInfo.InvariantCulture);

        return new("Power:", $"{F(granted)}/{F(demand)}", PlayerInterface.MainColour, granted < demand ? PlayerInterface.BadColour : null);
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

    #region Research tab

    /// <summary>
    /// The queue row's "X" action followed by the research id.
    /// </summary>
    const string ResearchCancelAction = "research-cancel:";

    /// <summary>
    /// The action a queue row carries: its research, for the tooltip; a click on it does nothing (the original's row only reorders by dragging).
    /// </summary>
    const string ResearchQueuedAction = "research-queued:";

    /// <summary>
    /// A pressed queue entry, its starting point and whether it has crossed the drag threshold.
    /// </summary>
    (string Id, Vector2 Start, bool Dragging)? _researchPress;

    /// <summary>
    /// The research tab's selected category, and the blueprint whose description shows.
    /// </summary>
    internal string? ResearchCategory, ResearchFocus;

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
    /// How far the blueprint rows are scrolled, their pitch and the panel they scroll in (the DatapanelGUI's own ScrollView in the original).
    /// </summary>
    float _researchScroll, _researchStep;

    /// <summary>
    /// The panel the blueprint rows scroll in.
    /// </summary>
    UiRect _researchArea;

    /// <summary>
    /// Whether a queue row owns the pointer until release.
    /// </summary>
    internal bool ResearchCapturesPointer => _researchPress is not null;

    /// <summary>
    /// Takes a left press on a queued project's row, for a drag that reorders the queue.
    /// </summary>
    internal bool ResearchPointerDown(string screen, UiControl row, Vector2 p)
    {
        if (screen != OverviewScreen || QueuedResearchId(row) is not { } id) return false;

        _researchPress = (id, p, false);
        return true;
    }

    /// <summary>
    /// Remembers that the pointer crossed the drag threshold, even if it returns to the pressed row.
    /// </summary>
    internal void ResearchPointerMove(Vector2 p)
    {
        if (_researchPress is { Dragging: false } press && Vector2.Distance(press.Start, p) >= PlayerInterface.DragThreshold)
            _researchPress = (press.Id, press.Start, true);
    }

    /// <summary>
    /// A drag onto another project changes the queue order; a click on a row does nothing, its "X" takes it off the queue.
    /// </summary>
    internal void ResearchPointerUp(Vector2 p, bool cancelled)
    {
        ResearchPointerMove(p);
        var pressed = _researchPress;
        _researchPress = null;
        if (cancelled || pressed is not { } press || !player.UiVisible) return;

        player.ArrangeUi();
        if (QueuedResearchAt(p) is not { } target) return;

        long tick = player.Session.World.Tick;
        if (press.Dragging && target != press.Id) player.Say(new MoveResearch(press.Id, target) { Tick = tick });
    }

    /// <summary>
    /// The research id of the visible queue row under the pointer.
    /// </summary>
    string? QueuedResearchAt(Vector2 p)
    {
        var screens = player.FaithfulScreens;
        if (!screens.IsOpen(OverviewScreen) || screens.GetTab(OverviewScreen, "TabsMain") != TutorialPanel.ResearchTab) return null;

        return screens.HitTest(p) is { Screen: OverviewScreen, Control: { Enabled: true } row } ? QueuedResearchId(row) : null;
    }

    /// <summary>
    /// The id a queue row carries (not its "X", whose click cancels).
    /// </summary>
    static string? QueuedResearchId(UiControl row) =>
        row.Action.StartsWith(ResearchQueuedAction, StringComparison.Ordinal) ? row.Action[ResearchQueuedAction.Length..] : null;

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
        else if (e.Action.StartsWith(ResearchCancelAction, StringComparison.Ordinal))
            player.Say(new CancelResearch(e.Action[ResearchCancelAction.Length..]) { Tick = player.Session.World.Tick });
    }

    /// <summary>
    /// One queued entry as the original's <c>ReorderableListItem</c> draws it (<c>Kenshi_ItemListItem.layout</c>, the research queue's update
    /// <c>FUN_1404bbfe0</c>, the craft queue's <c>FUN_1402cc960</c>, <b>Verified</b> by decompile): the row's frame over 0.9 of the cell, its name from the
    /// left, its <paramref name="value"/> right-aligned (a research's time left, empty for a craft), the name cut short of it, a
    /// <c>Kenshi_LifeBarSkin</c> bar of the share done times the value's width, and the red "X" (<c>RemoveButton</c>) that takes it off the queue.
    /// The row carries <paramref name="rowAction"/> (a research's, for its tooltip and drags). The bar goes under the texts, as on the blueprint
    /// lines (engine choice: the layout lists it last).
    /// </summary>
    void QueueRow(List<UiControl> controls, string key, UiRect cell, UiRect clip, string rowAction, string cancelAction, string name, string value,
        float progress)
    {
        var none = new Dictionary<string, string>();
        float h = cell.Height * 0.9375f, w = cell.Width;
        var text = new UiRect(cell.X + 0.015f * w, cell.Y, 0.86f * w, h);
        float valueWidth = value.Length == 0 ? 0
            : player.UiPainter?.MeasureText("Kenshi_StandardFont", value, player.Height, 0, player.StandardPixels).Width ?? 0;
        var nameRect = text with { Width = MathF.Max(0, text.Width - valueWidth) };

        controls.Add(new(key, "Button", new(cell.X, cell.Y, 0.9f * w, h), "", rowAction, "Kenshi_Button1Skin", true, none, Vector4.Zero,
            Pickable: true, Clip: clip));
        controls.Add(new($"{key}-bar", "Widget", new(text.X, cell.Y + 0.216667f * h, Math.Clamp(progress, 0, 1) * text.Width, 0.5f * h), "", "",
            "Kenshi_LifeBarSkin", true, none, Vector4.Zero, Pickable: false, Clip: clip));
        controls.Add(new($"{key}-name", "TextBox", nameRect, name, "", "Kenshi_TextboxStandardText", true,
            new Dictionary<string, string> { ["TextAlign"] = "Left VCenter" }, Vector4.Zero, TextColour: new(0.719298f, 0.656202f, 0.656202f, 1),
            Pickable: false, Clip: Within(clip, nameRect)));
        controls.Add(new($"{key}-x", "Button", new(cell.X + 0.9f * w, cell.Y, 0.1f * w, h), "X", cancelAction, "Kenshi_Button1Skin", true, none,
            Vector4.Zero, TextColour: new(0.8f, 0.266f, 0.266f, 1), Pickable: true, Clip: clip));
        controls.Add(new($"{key}-time", "TextBox", text, value, "", "Kenshi_TextboxStandardText", true,
            new Dictionary<string, string> { ["TextAlign"] = "Right VCenter" }, Vector4.Zero, TextColour: new(0.71345f, 0.688417f, 0.688417f, 1),
            Pickable: false, Clip: clip));
    }

    /// <summary>
    /// The part of <paramref name="a"/> inside <paramref name="b"/> (empty when they do not meet).
    /// </summary>
    static UiRect Within(UiRect a, UiRect b)
    {
        float x = MathF.Max(a.X, b.X), y = MathF.Max(a.Y, b.Y);
        float right = MathF.Min(a.X + a.Width, b.X + b.Width), bottom = MathF.Min(a.Y + a.Height, b.Y + b.Height);
        return new UiRect(x, y, MathF.Max(0, right - x), MathF.Max(0, bottom - y));
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
    /// The blueprint rows (<c>DataPanelLine_Research</c>, <c>FUN_1406f9220</c>): a <c>Kenshi_Button1</c> frame across 98 % of the panel, the name
    /// from 3 % and the time right-aligned to 97 %, in Main when it can be queued, else Bad, and a green bar over the frame for the progress kept;
    /// then the queue rows and the hovered or last clicked research's description.
    /// </summary>
    internal void AddResearchContent(List<UiControl> controls)
    {
        if (player.FaithfulScreens.GetTab("overview", "TabsMain") != TutorialPanel.ResearchTab) return;

        var research = player.Session.CurrentSnapshot.Research;
        var face = new Dictionary<string, string> { ["FontName"] = "Kenshi_StandardFont_Medium", ["TextAlign"] = "Left VCenter" };
        float line = MathF.Max(18, 24 * player.UiScale);
        var ink = new Vector4(0.86f, 0.82f, 0.71f, 1);

        if (controls.FirstOrDefault(c => c.Name == "BlueprintsAvailablePanel") is { } blueprints)
        {
            // The row unit U as the base game draws it at 1080 lines (Observed): a 29 px frame 0.15 U down a 35 px pitch.
            float unit = 30.5f * player.UiScale, pitch = 1.15f * unit, w = blueprints.Bounds.Width;
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
            var view = QueueView(controls, queue.Bounds, research.Queue.Count, line, (TutorialPanel.ResearchTab, null), out float scroll);
            for (int i = 0; i < research.Queue.Count; i++)
            {
                float top = view.Y + i * line - scroll;
                if (top + line <= view.Y || top >= view.Y + view.Height) continue;   // out of the box: not drawn
                var (qid, name, progress, hours) = research.Queue[i];
                QueueRow(controls, $"research-queue-{i}", new UiRect(view.X, top, view.Width, line), view, ResearchQueuedAction + qid,
                    ResearchCancelAction + qid, name, ResearchTime(hours, progress, research.Rate, false), progress);
            }
        }
        else ClearQueueScroll();

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
        ["TextPadding"] = $"{(8 * player.UiScale).ToString(CultureInfo.InvariantCulture)} 0 0 0",
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

    #endregion

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
    /// How far the item rows are scrolled and their pitch (0 until they are laid out).
    /// </summary>
    float _craftScroll, _craftStep;

    /// <summary>
    /// The panel the item rows scroll in.
    /// </summary>
    UiRect _craftArea;

    /// <summary>
    /// The wheel over the item rows scrolls them a row per notch; clamped when the rows are laid out.
    /// </summary>
    internal bool CraftWheel(Vector2 p, float steps)
    {
        if (!player.FaithfulScreens.IsOpen("overview") || player.FaithfulScreens.GetTab("overview", "TabsMain") != CraftTab
            || _craftStep <= 0 || !_craftArea.Contains(p.X, p.Y))
        {
            return false;
        }

        _craftScroll -= MathF.Sign(steps) * _craftStep;
        return true;
    }

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
        if (_craftStation is not null) b.Disabled.Remove("QueueRepeat");
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
            _craftScroll = 0;
            ClearQueueScroll();
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
        int tab = player.FaithfulScreens.GetTab("overview", "TabsMain");
        if (tab != CraftTab || _craftStation is null || Machine(_craftStation) is not { Craft: { } craft })
        {
            if (tab != TutorialPanel.ResearchTab) ClearQueueScroll();
            return;
        }

        var options = player.Session.CurrentSnapshot.Crafts.GetValueOrDefault(_craftStation) ?? [];
        var face = new Dictionary<string, string> { ["FontName"] = "Kenshi_StandardFont_Medium", ["TextAlign"] = "Left VCenter" };
        float line = MathF.Max(18, 24 * player.UiScale);
        var ink = new Vector4(0.86f, 0.82f, 0.71f, 1);

        if (controls.FirstOrDefault(c => c.Name == "QueueItemsPanel") is { } panel)
        {
            var rows = options.OrderBy(o => o.Kind).ThenBy(o => o.Level).ThenBy(o => o.Name, StringComparer.Ordinal).ToList();
            var area = panel.Bounds;
            _craftArea = area;
            _craftStep = line;
            _craftScroll = Math.Clamp(_craftScroll, 0, Math.Max(0, rows.Count * line - area.Height));

            for (int i = 0; i < rows.Count; i++)
            {
                // A row outside the panel is left out: the clip only cuts its drawing, and it would take clicks over the description below.
                float top = area.Y + i * line - _craftScroll, middle = top + line / 2;
                if (middle < area.Y || middle > area.Y + area.Height) continue;

                var o = rows[i];
                controls.Add(new($"craft-row-{i}", "Button", new(area.X + 6, top, area.Width - 12, line),
                    o.Name, o.Unlocked ? "craft-queue:" + o.Key : "craft-focus:" + o.Key, "", true, face, Vector4.Zero,
                    TextColour: o.Unlocked ? ink : new Vector4(0.75f, 0.35f, 0.3f, 1), HoverColour: new(0, 1, 0, 1), Pickable: true,
                    Clip: area));
            }
        }

        if (QueueBox(controls, "CraftQueue", "QueuePanel") is { } queue)
        {
            var view = QueueView(controls, queue.Bounds, craft.Queue.Count, line, (CraftTab, _craftStation), out float scroll);
            for (int i = 0; i < craft.Queue.Count; i++)
            {
                float top = view.Y + i * line - scroll;
                if (top + line <= view.Y || top >= view.Y + view.Height) continue;   // out of the box: not drawn
                var (_, name, progress, _) = craft.Queue[i];
                QueueRow(controls, $"craft-queue-{i}", new UiRect(view.X, top, view.Width, line), view, "",
                    "craft-cancel:" + i.ToString(CultureInfo.InvariantCulture), name, "", progress);
            }
        }
        else ClearQueueScroll();

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
    /// The queue the overview's scroll offset belongs to (<see cref="QueueView"/>): its tab and bench.
    /// </summary>
    (int Tab, string? Bench) _queueOwner;

    /// <summary>
    /// The overview window's screen id, the key of its queue's scroll view.
    /// </summary>
    const string OverviewScreen = "overview";

    /// <summary>
    /// The queue box's scrolling: the original's queue is an ItemBox whose skin (<c>Kenshi_ItemBoxEmpty</c>) has a vertical scroll bar, 15 wide, so
    /// a queue taller than its box scrolls (bar, arrows, track, thumb and wheel; the overview window's scroll view, <see cref="ScrollBars"/>).
    /// Returns the rows' rectangle, narrowed by the bar when the queue overflows, and the scroll offset. The offset belongs to one queue
    /// (<paramref name="owner"/>: the tab and the bench), as each box has its own: another queue starts at the top. The arrows and the wheel move it
    /// a row.
    /// </summary>
    UiRect QueueView(List<UiControl> controls, UiRect box, int count, float line, (int Tab, string? Bench) owner, out float scroll)
    {
        var bars = player.ScrollBars;
        float maximum = MathF.Max(0, count * line - box.Height);

        if (maximum <= 0 || owner != _queueOwner) ClearQueueScroll();

        if (maximum <= 0)
        {
            scroll = 0;
            return box;
        }

        _queueOwner = owner;
        bars.ViewSteps[OverviewScreen] = line;
        scroll = bars.InventoryScroll[OverviewScreen] = Math.Clamp(bars.InventoryScroll.GetValueOrDefault(OverviewScreen), 0, maximum);

        float barWidth = 15 * player.UiScale;
        var view = box with { Width = MathF.Max(0, box.Width - barWidth) };
        bars.InventoryViews[OverviewScreen] = (box, maximum);   // the wheel over the bar scrolls too
        bars.AddScrollBar(OverviewScreen, controls, new UiRect(box.X + box.Width - barWidth, box.Y, barWidth, box.Height), box.Height,
            box.Height + maximum, scroll, maximum, box);

        return view;
    }

    /// <summary>
    /// The overview window has no scrolled queue (another tab, another bench, or a queue that fits).
    /// </summary>
    void ClearQueueScroll()
    {
        player.ScrollBars.InventoryScroll.Remove(OverviewScreen);
        player.ScrollBars.InventoryViews.Remove(OverviewScreen);
        player.ScrollBars.InventoryTracks.Remove(OverviewScreen);
        player.ScrollBars.ViewSteps.Remove(OverviewScreen);
        _queueOwner = default;
    }

    /// <summary>
    /// A queued craft's row under the pointer.
    /// </summary>
    bool IsHoveredCraftQueue(UiControl c) =>
        c.Name.StartsWith("craft-queue-", StringComparison.Ordinal) && c.Bounds.Contains(player.Mouse.X, player.Mouse.Y)
        && c.Clip?.Contains(player.Mouse.X, player.Mouse.Y) != false;

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
