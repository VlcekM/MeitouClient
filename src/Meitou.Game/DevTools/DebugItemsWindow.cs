using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Meitou.Data;
using Meitou.Data.Ui;
using Meitou.Game.Ui;
using Meitou.Simulation;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Population;

namespace Meitou.Game.DevTools;

/// <summary>
/// One cell of the Items window's stock: the item record at one quality, and the item as a window lists it (its footprint, icon and tooltip).
/// </summary>
sealed record StockCell(DebugItemEntry Entry, DebugItemQuality? Quality, InventoryItemView View);

/// <summary>
/// The debug menu's Items window (docs/game/ui-input.md "Items window"): a window of the game's own window system (<see cref="MyGuiScreenHost"/>), not
/// modal and standing just above the squad bar, so the portraits stay free to drop on. A search box on top; under it the item categories and the items
/// of the picked one as two lists, and a fake trader's stock grid with what was picked (an item, at every quality it has, the worst first). A search
/// (two characters or more, 300 ms after the last key) hides the lists and fills the stock with up to 50 matching items. A stock item is dragged by the
/// mouse: let go over a squad bar portrait it is given to that character (what does not fit lands at the character's feet), over the world it is
/// placed on the ground there, over any other part of the interface it goes nowhere. The stock never runs out; Shift gives five. Layout:
/// <see cref="DebugItemsLayout"/>.
/// </summary>
sealed class DebugItemsWindow(PlayerInterface player)
{
    #region State

    /// <summary>
    /// How many characters a search needs, how long after the last key it runs, and how many cells its results fill at most.
    /// </summary>
    const int SearchMinLength = 1, SearchLimit = 100;

    /// <summary>
    /// The seconds a search waits after the last key.
    /// </summary>
    const double SearchDelay = 0.3;

    /// <summary>
    /// How many units a drag with Shift held gives.
    /// </summary>
    const int ShiftQuantity = 5;

    /// <summary>
    /// The room the window leaves between itself and the squad bar, and above itself, in layout pixels.
    /// </summary>
    const float Gap = 12, TopMargin = 24;

    /// <summary>
    /// The least height the window keeps in layout pixels when the squad bar leaves less room.
    /// </summary>
    const float MinHeight = 360;

    /// <summary>
    /// The item catalog of the game data.
    /// </summary>
    readonly DebugItemCatalog _catalog = new();

    /// <summary>
    /// The window's top left corner and size in pixels, made when it opens.
    /// </summary>
    Vector2 _origin, _size;

    /// <summary>
    /// The picked category: its group, and its sub-category (null: the whole group). Null without a pick.
    /// </summary>
    string? _group, _sub;

    /// <summary>
    /// The first row of the items list, which shows the whole category: its id and its caption.
    /// </summary>
    const string AllItemsId = "*", AllItemsCaption = "--- ALL ITEMS ---";

    /// <summary>
    /// The quality dropdown's "Any" row: no quality filter.
    /// </summary>
    const string AnyQuality = "-1";

    /// <summary>
    /// A quality level the dropdown offers: its label and the quality (0 to 100) it picks.
    /// </summary>
    readonly record struct QualityLevel(string Label, int Quality);

    /// <summary>
    /// The game's own quality levels: the six grades, Prototype to Masterwork.
    /// </summary>
    static List<QualityLevel> Grades() =>
        [.. ArmourTooltip.GradeNames.Select((name, grade) => new QualityLevel(name, ArmourTooltip.GradeQualities[grade]))];

    /// <summary>
    /// The rows of the picked category's items, as the second list shows them: <see cref="AllItemsCaption"/> first, then the items by name.
    /// </summary>
    List<UiListItem> _listed = [];

    /// <summary>
    /// The rows of the categories list, and the game data they were made from.
    /// </summary>
    List<UiListItem> _categories = [];

    /// <summary>
    /// The game data <see cref="_categories"/> was made for.
    /// </summary>
    GameDatabase? _categoriesFor;

    /// <summary>
    /// The picked item; null without a pick.
    /// </summary>
    DebugItemEntry? _item;

    /// <summary>
    /// The quality filter: -1 for any, else the quality (0 to 100) the stock is limited to, each item at the best quality it has at or below it.
    /// </summary>
    int _quality = -1;

    /// <summary>
    /// The quality levels the dropdown offers: the six grades, then the model levels a mod adds above them.
    /// </summary>
    List<QualityLevel> _levels = Grades();

    /// <summary>
    /// The game data <see cref="_levels"/> was made for.
    /// </summary>
    GameDatabase? _levelsFor;

    /// <summary>
    /// The search box's text as last typed and when (a <see cref="Stopwatch"/> timestamp), and the text the stock was last made for.
    /// </summary>
    string _typed = "", _query = "";

    /// <summary>
    /// When the search box was last typed in.
    /// </summary>
    long _typedAt;

    /// <summary>
    /// The stock: the cells shown, by their number.
    /// </summary>
    List<StockCell> _stock = [];

    /// <summary>
    /// The cell each stock item sits in, by the stock's order, as <see cref="Pack"/> last placed them.
    /// </summary>
    readonly List<(int X, int Y)> _slots = [];

    /// <summary>
    /// What <see cref="_slots"/> was made for: the stock's version and the grid's columns.
    /// </summary>
    (int Version, int Columns) _packKey = (-1, -1);

    /// <summary>
    /// The number of the stock as it stands, so the packing is made again when it changes.
    /// </summary>
    int _version;

    /// <summary>
    /// The rows <see cref="Pack"/> used.
    /// </summary>
    int _packedRows;

    /// <summary>
    /// The grid's size in cells as the last binding made it.
    /// </summary>
    int _columns, _rows;

    /// <summary>
    /// The stock cell being dragged (-1: none), and where the press was.
    /// </summary>
    int _dragCell = -1;

    /// <summary>
    /// Where the dragging press was.
    /// </summary>
    Vector2 _pressAt;

    /// <summary>
    /// The window is open.
    /// </summary>
    internal bool Shown => player.FaithfulScreens.IsRegistered(DebugItemsLayout.Screen) && player.FaithfulScreens.IsOpen(DebugItemsLayout.Screen);

    /// <summary>
    /// The search is on: its text is long enough, so the lists give way to its results.
    /// </summary>
    bool Searching => _query.Length >= SearchMinLength;

    /// <summary>
    /// The game data's population (items and records); null before a world is loaded.
    /// </summary>
    PopulationData? Data => player.PopulationOf?.Invoke();

    /// <summary>
    /// A stock item is being dragged past the drag threshold.
    /// </summary>
    bool Dragging => _dragCell >= 0 && Vector2.Distance(_pressAt, player.Mouse) >= PlayerInterface.DragThreshold;

    #endregion

    #region Lifetime

    /// <summary>
    /// Opens the window (the debug menu's Items entry) above the squad bar, with the search box emptied and nothing picked.
    /// </summary>
    internal void Open()
    {
        var screens = player.FaithfulScreens;
        if (!screens.IsRegistered(DebugItemsLayout.Screen)) screens.Register(DebugItemsLayout.Screen, DebugItemsLayout.Build());

        (_group, _sub, _item, _typed, _query, _dragCell, _quality) = (null, null, null, "", "", -1, -1);
        _listed = [];
        screens.SetText(DebugItemsLayout.Screen, DebugItemsLayout.SearchBox, "");
        screens.ClearSelection(DebugItemsLayout.Screen, DebugItemsLayout.CategoriesList);
        screens.ClearSelection(DebugItemsLayout.Screen, DebugItemsLayout.ItemsList);
        screens.ClearSelection(DebugItemsLayout.Screen, DebugItemsLayout.QualityCombo);
        Rebuild();
        Place();
        screens.Open(DebugItemsLayout.Screen);
        screens.SetOffset(DebugItemsLayout.Screen, Vector2.Zero);
    }

    /// <summary>
    /// The window's size and corner: as wide as the layout says (less on a small screen), as tall as it says or as the room above the squad bar leaves,
    /// centred across the screen with its bottom edge a gap above the bar.
    /// </summary>
    void Place()
    {
        float scale = player.UiScale;
        var squads = player.ArrangeUi().FirstOrDefault(c => c.Name == "SquadPanel");
        float floor = squads is null ? player.Height : squads.Bounds.Y;
        float width = Math.Min(DebugItemsLayout.WindowWidth * scale, player.Width - 24);
        float room = floor - (Gap + TopMargin) * scale;
        float height = Math.Max(MinHeight * scale, Math.Min(DebugItemsLayout.WindowHeight * scale, room));

        _size = new(width, height);
        _origin = new((player.Width - width) / 2, Math.Max(TopMargin * scale, floor - Gap * scale - height));
    }

    /// <summary>
    /// Every frame before the interface is arranged: drops a drag of a closed window, and runs the search once its box has been quiet for
    /// <see cref="SearchDelay"/> seconds.
    /// </summary>
    internal void Sync()
    {
        if (!Shown)
        {
            _dragCell = -1;
            return;
        }

        if (_typed != _query && Stopwatch.GetElapsedTime(_typedAt).TotalSeconds >= SearchDelay)
        {
            _query = _typed.Trim();
            Rebuild();
        }
    }

    #endregion

    #region Stock

    /// <summary>
    /// Makes the stock again for what is shown: a search's matches, the whole picked category (its "all items" row) or the picked item, up to
    /// <see cref="SearchLimit"/> cells.
    /// </summary>
    void Rebuild()
    {
        _stock = [];
        _version++;
        if (Data is not { } data) return;

        if (Searching)
        {
            AddEntries(data, _catalog.Matching(data.Db, _query));
            return;
        }

        if (_item is { } picked) AddCells(data, picked, int.MaxValue);
        else if (_group is { } group) AddEntries(data, _catalog.InCategory(data.Db, group, _sub));
    }

    /// <summary>
    /// Adds a cell for each of <paramref name="entries"/> at every quality it has, until the stock holds <see cref="SearchLimit"/> cells.
    /// </summary>
    void AddEntries(PopulationData data, IEnumerable<DebugItemEntry> entries)
    {
        foreach (var entry in entries)
        {
            if (_stock.Count >= SearchLimit) return;

            AddCells(data, entry, SearchLimit);
        }
    }

    /// <summary>
    /// Adds a cell for <paramref name="entry"/> at each of its qualities, the worst first (one for an item without any), until the stock holds
    /// <paramref name="limit"/> cells.
    /// </summary>
    void AddCells(PopulationData data, DebugItemEntry entry, int limit)
    {
        List<DebugItemQuality?> qualities = [.. _catalog.Qualities(entry, data.Db)];
        if (qualities.Count == 0) qualities.Add(null);

        if (_quality >= 0) qualities = [Best(qualities)];

        foreach (var quality in qualities)
        {
            if (_stock.Count >= limit) return;
            if (DebugGiveItem.Make(data, entry.Id, 1, quality?.Quality ?? 0, quality?.Exact) is not { } item) continue;

            _stock.Add(new(entry, quality, InventoryItemView.Of(_stock.Count, item)));
        }
    }

    /// <summary>
    /// The quality of <paramref name="qualities"/> at or below the picked one, the highest such: an item without that quality takes the highest it has
    /// below it (armour takes Masterwork for Meitou); the lowest when none is below.
    /// </summary>
    DebugItemQuality? Best(List<DebugItemQuality?> qualities)
    {
        var below = qualities.Where(q => (q?.Quality ?? 0) <= _quality).ToList();
        return below.Count > 0 ? below.MaxBy(q => q?.Quality ?? 0) : qualities.MinBy(q => q?.Quality ?? 0);
    }

    /// <summary>
    /// Places the stock's items in a grid of <paramref name="columns"/> columns as a trader's stock is laid out: each in the first free spot reading
    /// row by row from the top left. Returns the rows used.
    /// </summary>
    int Pack(int columns)
    {
        if (_packKey == (_version, columns)) return _packedRows;

        _slots.Clear();
        var taken = new List<bool[]>();
        foreach (var cell in _stock)
        {
            int width = Math.Clamp(cell.View.Width, 1, columns), height = Math.Max(1, cell.View.Height);
            _slots.Add(FirstFree(taken, columns, width, height));
        }

        _packKey = (_version, columns);
        return _packedRows = taken.Count;
    }

    /// <summary>
    /// The first spot of the grid <paramref name="taken"/> (a row per entry, grown as needed) where a footprint of
    /// <paramref name="width"/> x <paramref name="height"/> is free; marks it taken.
    /// </summary>
    static (int X, int Y) FirstFree(List<bool[]> taken, int columns, int width, int height)
    {
        for (int y = 0; ; y++)
        {
            while (taken.Count < y + height) taken.Add(new bool[columns]);

            for (int x = 0; x + width <= columns; x++)
            {
                if (!IsFree(taken, x, y, width, height)) continue;

                for (int row = y; row < y + height; row++)
                {
                    for (int column = x; column < x + width; column++) taken[row][column] = true;
                }

                return (x, y);
            }
        }
    }

    /// <summary>
    /// Whether the cells of a footprint at (<paramref name="x"/>, <paramref name="y"/>) are all free.
    /// </summary>
    static bool IsFree(List<bool[]> taken, int x, int y, int width, int height)
    {
        for (int row = y; row < y + height; row++)
        {
            for (int column = x; column < x + width; column++)
            {
                if (taken[row][column]) return false;
            }
        }

        return true;
    }

    #endregion

    #region Bindings

    /// <summary>
    /// The window's bindings: its place and size, the search label and its clear button, the help line, the two lists with their empty states, and the
    /// stock's grid.
    /// </summary>
    internal void Bind(UiBindings b)
    {
        b.TemplateScale = player.UiScale;
        b.WindowPosition = _origin;
        b.Positions[DebugItemsLayout.Root] = new(0, 0, _size.X, _size.Y);
        b.Text[DebugItemsLayout.SearchLabel] = "Search by item name";
        b.Text[DebugItemsLayout.HelpText] = "Drag an item onto a character's portrait or to the world, hold [shift] for x5. Note: items inventory displays only up to 100 items at once.";

        BindClear(b);
        BindQuality(b);
        BindLists(b);
        BindStock(b);
    }

    /// <summary>
    /// The quality dropdown: "Any", then the quality levels in their grade's colour (<see cref="QualityLevels"/>; the last grades share one).
    /// </summary>
    void BindQuality(UiBindings b)
    {
        b.Text[DebugItemsLayout.QualityLabel] = "Preferred quality";
        var levels = QualityLevels(Data);
        b.Lists[DebugItemsLayout.QualityCombo] =
        [
            new(AnyQuality, "Any"),
            .. levels.Select(l => new UiListItem(l.Quality.ToString(CultureInfo.InvariantCulture), l.Label,
                Colour: TooltipRuntime.TipColour(TooltipRuntime.GradeTone(ArmourTooltip.GradeOf(l.Quality)), plate: false))),
        ];
        b.SelectedItems[DebugItemsLayout.QualityCombo] = _quality < 0 ? 0 : levels.FindIndex(l => l.Quality == _quality) + 1;
    }

    /// <summary>
    /// The dropdown's levels: the six grades, then every weapon model level above the last grade that the data has (Rebirth's Meitou at 100), named as
    /// its model is. Made once per game data.
    /// </summary>
    List<QualityLevel> QualityLevels(PopulationData? data)
    {
        if (data is null || ReferenceEquals(_levelsFor, data.Db)) return _levels;

        _levels =
        [
            .. Grades(),
            .. _catalog.ModelsAbove(data.Db, ArmourTooltip.GradeQualities[^1]).Select(m => new QualityLevel(m.Name, m.Level)),
        ];
        _levelsFor = data.Db;
        return _levels;
    }

    /// <summary>
    /// The clear button: it empties the search box, and is only there to press while the box holds something.
    /// </summary>
    void BindClear(UiBindings b)
    {
        b.Text[DebugItemsLayout.ClearButton] = "CLEAR";
        if (player.FaithfulScreens.GetText(DebugItemsLayout.Screen, DebugItemsLayout.SearchBox).Length == 0)
        {
            b.Disabled.Add(DebugItemsLayout.ClearButton);
            return;
        }

        b.Disabled.Remove(DebugItemsLayout.ClearButton);
        b.Actions[DebugItemsLayout.ClearButton] = DebugItemsLayout.ClearAction;
    }

    /// <summary>
    /// The categories and the picked category's items, each with the text it shows while it is empty; all hidden while a search shows its results.
    /// </summary>
    void BindLists(UiBindings b)
    {
        if (Searching)
        {
            foreach (string name in new[] { DebugItemsLayout.CategoriesHead, DebugItemsLayout.ItemsHead, DebugItemsLayout.CategoriesList,
                DebugItemsLayout.ItemsList, DebugItemsLayout.CategoriesEmpty, DebugItemsLayout.ItemsEmpty })
            {
                b.Hidden.Add(name);
            }

            return;
        }

        b.Text[DebugItemsLayout.CategoriesHead] = "CATEGORIES";
        b.Text[DebugItemsLayout.ItemsHead] = "ITEMS";

        var categories = Data is { } data ? CategoryRows(data) : [];
        b.Lists[DebugItemsLayout.CategoriesList] = categories;
        b.Lists[DebugItemsLayout.ItemsList] = _listed;
        if (_group is not null) b.SelectedItems[DebugItemsLayout.ItemsList] = _item is null ? 0 : _listed.FindIndex(r => r.Id == _item.Id);
        BindEmpty(b, DebugItemsLayout.CategoriesEmpty, categories.Count == 0 ? "No items" : "");
        BindEmpty(b, DebugItemsLayout.ItemsEmpty, _group is null ? "Pick a category" : "");
    }

    /// <summary>
    /// The text <paramref name="name"/> shows over an empty list, in the list's own ink; hidden for no text.
    /// </summary>
    void BindEmpty(UiBindings b, string name, string text)
    {
        if (text.Length == 0)
        {
            b.Hidden.Add(name);
            return;
        }

        b.Text[name] = text;
        b.Properties[name] = new Dictionary<string, string>
        {
            ["TextColour"] = MyGuiColours.TemplateInk(player.UiResources, "Kenshi_GenericTextBoxSkin"),
            ["TextAlign"] = "Center",
            ["WordWrap"] = "true",
        };
    }

    /// <summary>
    /// The categories' rows: each group, in capitals, picks all its items; the sub-categories of a group stand indented under it. Made once per
    /// game data.
    /// </summary>
    List<UiListItem> CategoryRows(PopulationData data)
    {
        if (ReferenceEquals(_categoriesFor, data.Db)) return _categories;

        var rows = new List<UiListItem>();
        foreach (string group in DebugItemCatalog.GroupNames)
        {
            if (!_catalog.InCategory(data.Db, group, null).Any()) continue;

            rows.Add(new(CategoryId(group, null), group.ToUpperInvariant()));
            foreach (string sub in _catalog.Subs(data.Db, group)) rows.Add(new(CategoryId(group, sub), "    " + sub));
        }

        (_categoriesFor, _categories) = (data.Db, rows);
        return rows;
    }

    /// <summary>
    /// A category row's id: its group, and after a bar its sub-category.
    /// </summary>
    static string CategoryId(string group, string? sub) => sub is null ? group : $"{group}|{sub}";

    /// <summary>
    /// The stock: its grid sized to the columns the view holds and the rows its items need (at least the view's), or the text asking for a pick or
    /// saying nothing was found while there are no items.
    /// </summary>
    void BindStock(UiBindings b)
    {
        var layout = player.FaithfulScreens.Layout(DebugItemsLayout.Screen);
        var area = Searching ? DebugItemsLayout.StockWide : DebugItemsLayout.StockBeside;
        foreach (string name in new[] { DebugItemsLayout.StockView, DebugItemsLayout.StockGrid, DebugItemsLayout.StockEmpty })
        {
            if (layout.Find(name) is { } widget) b.RelativePositions[widget] = area;
        }

        if (_stock.Count == 0)
        {
            b.Hidden.Add(DebugItemsLayout.StockView);
            b.Hidden.Add(DebugItemsLayout.StockGrid);
            b.Text[DebugItemsLayout.StockEmpty] = Searching || _group is not null ? "Nothing found" : "Pick a category";
            return;
        }

        b.Hidden.Add(DebugItemsLayout.StockEmpty);
        float cell = player.InventoryCell, cellHeight = player.InventoryCellHeight;
        _columns = Math.Max(4, (int)((_size.X * area.Width - 28 * player.UiScale) / cell));
        int visible = Math.Max(1, (int)(_size.Y * area.Height / cellHeight) - 2);
        _rows = Math.Max(Pack(_columns), visible);
        b.Canvas[DebugItemsLayout.StockView] = (_columns * cell, _rows * cellHeight);
    }

    #endregion

    #region Content

    /// <summary>
    /// What the window draws over its widgets: the slot grid as the stock view's canvas and the stock's items on it.
    /// </summary>
    internal void AddContent(List<UiControl> controls)
    {
        if (_stock.Count == 0 || !ArrangeGrid(controls, out var grid)) return;

        float cell = player.InventoryCell, cellHeight = player.InventoryCellHeight;
        bool lifted = Dragging;
        for (int i = 0; i < _stock.Count && i < _slots.Count; i++)
        {
            if (lifted && i == _dragCell) continue;

            var view = _stock[i].View;
            var at = new UiRect(grid.Bounds.X + _slots[i].X * cell, grid.Bounds.Y + _slots[i].Y * cellHeight, view.Width * cell, view.Height * cellHeight);
            player.ItemCells.AddItemCell(controls, $"debug-stock-{i}", at, view.Record, view.Material, view.Name, view.Quantity,
                TooltipRuntime.ItemTip.Of(view), DebugItemsLayout.CellProperty, i, grid.Clip, charge: view.Charge, colour: view.Colour,
                rotated: view.Rotated, tipAtPointer: true);
        }
    }

    /// <summary>
    /// Moves the slot grid to where the stock view's canvas is (as the trader window's is), as big as the columns and rows the binding made, clipped
    /// to the view's client, and drawn over the view's frame and under its bars. False without a view or a grid.
    /// </summary>
    bool ArrangeGrid(List<UiControl> controls, out UiControl grid)
    {
        grid = null!;
        int viewIndex = controls.FindIndex(c => c.Name == DebugItemsLayout.StockView), gridIndex = controls.FindIndex(c => c.Name == DebugItemsLayout.StockGrid);
        if (viewIndex < 0 || gridIndex < 0) return false;

        var view = controls[viewIndex];
        var client = controls.FirstOrDefault(c => TradeWindows.IsTemplateClient(c, DebugItemsLayout.StockView))?.Bounds ?? view.Bounds;
        var slot = controls[gridIndex];
        controls.RemoveAt(gridIndex);
        viewIndex = controls.FindIndex(c => c.Name == DebugItemsLayout.StockView);

        float cell = player.InventoryCell, cellHeight = player.InventoryCellHeight;
        grid = slot with
        {
            Bounds = new(MyGuiPresentation.Num(view.Properties, "CanvasX", client.X), MyGuiPresentation.Num(view.Properties, "CanvasY", client.Y),
                _columns * cell, _rows * cellHeight),
            Clip = client.Intersect(slot.Clip ?? client),
            Properties = new Dictionary<string, string>(slot.Properties) { ["TileSize"] = FormattableString.Invariant($"{cell} {cellHeight}") },
        };
        controls.Insert(viewIndex + 1, grid);
        return true;
    }

    /// <summary>
    /// The dragged item under the pointer, drawn like a held item, once the drag has gone past the threshold.
    /// </summary>
    internal void AddDrag(List<UiControl> controls)
    {
        if (!Shown || !Dragging || _dragCell >= _stock.Count) return;

        var view = _stock[_dragCell].View;
        float width = view.Width * player.InventoryCell, height = view.Height * player.InventoryCellHeight;
        var at = new UiRect(player.Mouse.X - width / 2, player.Mouse.Y - height / 2, width, height);
        player.ItemCells.AddItemCell(controls, "debug-drag-item", at, view.Record, view.Material, view.Name, view.Quantity, null, "DebugDragItem", 0,
            null, pickable: false, charge: view.Charge, colour: view.Colour, rotated: view.Rotated);
    }

    #endregion

    #region Events

    /// <summary>
    /// A widget's event: the search box typed in (the search runs once it is quiet), a category or an item picked.
    /// </summary>
    internal void Action(MyGuiEvent e)
    {
        switch (e.Widget, e.Action)
        {
            case (DebugItemsLayout.SearchBox, "text"):
                (_typed, _typedAt) = (e.Text, Stopwatch.GetTimestamp());
                break;
            case (DebugItemsLayout.ClearButton, DebugItemsLayout.ClearAction):
                ClearSearch();
                break;
            case (DebugItemsLayout.QualityCombo, "select"):
                PickQuality(e.Text);
                break;
            case (DebugItemsLayout.CategoriesList, "select"):
                PickCategory(e.Text);
                break;
            case (DebugItemsLayout.ItemsList, "select"):
                PickItem(e.Text);
                break;
        }
    }

    /// <summary>
    /// Empties the search box: the lists come back at once, whatever was picked in them.
    /// </summary>
    void ClearSearch()
    {
        player.FaithfulScreens.SetText(DebugItemsLayout.Screen, DebugItemsLayout.SearchBox, "");
        (_typed, _query) = ("", "");
        Rebuild();
    }

    /// <summary>
    /// Picks the quality <paramref name="id"/> of the dropdown (<see cref="AnyQuality"/> for any): the stock shows each item at the best quality it has
    /// at or below it.
    /// </summary>
    void PickQuality(string id)
    {
        if (!int.TryParse(id, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int quality)) return;

        _quality = quality;
        Rebuild();
    }

    /// <summary>
    /// Picks the category <paramref name="id"/> (<see cref="CategoryId"/>): its items fill the second list under <see cref="AllItemsCaption"/>, which
    /// is picked, so the stock shows the whole category.
    /// </summary>
    void PickCategory(string id)
    {
        if (Data is not { } data) return;

        string[] parts = id.Split('|');
        (_group, _sub, _item) = (parts[0], parts.Length > 1 ? parts[1] : null, null);
        _listed =
        [
            new(AllItemsId, AllItemsCaption),
            .. _catalog.InCategory(data.Db, _group, _sub).Select(e => new UiListItem(e.Id, e.Name)),
        ];
        player.FaithfulScreens.ClearSelection(DebugItemsLayout.Screen, DebugItemsLayout.ItemsList);
        Rebuild();
    }

    /// <summary>
    /// Picks the item <paramref name="id"/> of the second list: the stock shows it at every quality, or the whole category for
    /// <see cref="AllItemsId"/>.
    /// </summary>
    void PickItem(string id)
    {
        _item = id == AllItemsId ? null : _catalog.Find(id);
        Rebuild();
    }

    #endregion

    #region Dragging

    /// <summary>
    /// A left press at <paramref name="p"/>: on a stock item it starts a drag. False for anything else, which the window system takes.
    /// </summary>
    internal bool PointerDown(Vector2 p)
    {
        if (!Shown || player.FaithfulScreens.HitTest(p) is not { Screen: DebugItemsLayout.Screen } hit) return false;
        if (!hit.Control.Properties.TryGetValue(DebugItemsLayout.CellProperty, out string? raw)
            || !int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out int cell) || cell >= _stock.Count)
        {
            return false;
        }

        player.FaithfulScreens.Blur();
        (_dragCell, _pressAt) = (cell, p);
        return true;
    }

    /// <summary>
    /// The left button let go at <paramref name="p"/>: a drag that went past the threshold drops its item there, with Shift held
    /// <see cref="ShiftQuantity"/> of it. The stock is untouched, so it is whole again at once.
    /// </summary>
    internal void PointerUp(Vector2 p, bool shift)
    {
        if (_dragCell < 0) return;

        int cell = _dragCell;
        bool dragged = Dragging;
        _dragCell = -1;
        if (dragged && cell < _stock.Count) Drop(_stock[cell], p, shift ? ShiftQuantity : 1);
    }

    /// <summary>
    /// Escape or a right click: the drag ends without dropping anything. True when there was one.
    /// </summary>
    internal bool CancelDrag()
    {
        if (_dragCell < 0) return false;

        _dragCell = -1;
        return true;
    }

    /// <summary>
    /// Lets go of <paramref name="quantity"/> units of <paramref name="cell"/> at <paramref name="p"/>: over a squad bar portrait they are given to
    /// that character; over the rest of the interface nothing happens; over the world they are placed on the ground at the camera's target (as the
    /// menu's other world actions use it), not under the pointer.
    /// </summary>
    void Drop(StockCell cell, Vector2 p, int quantity)
    {
        if (player.PortraitBar.SquadBarMemberAt(p) is { } member)
        {
            Give(cell, member, quantity);
            return;
        }

        if (player.OverHud(p)) return;

        var target = player.Session.Camera.Current.Target;
        var world = player.Session.World;
        PlaceOnGround(cell, new Vector3(target.X, world.Walkability.GroundHeight(target.X, target.Z, target.Y), target.Z), quantity);
    }

    /// <summary>
    /// Gives the character <paramref name="to"/> the item, a unit at a time (a stack of a weapon is no item).
    /// </summary>
    void Give(StockCell cell, CharacterId to, int quantity)
    {
        var world = player.Session.World;
        for (int i = 0; i < quantity; i++)
        {
            world.Commands.Enqueue(new DebugGiveItem(to, cell.Entry.Id, 1, cell.Quality?.Quality ?? 0) { Tick = world.Tick, Exact = cell.Quality?.Exact });
        }

        ApplyWhilePaused();
    }

    /// <summary>
    /// Lays the item on the ground at <paramref name="at"/>, a unit at a time, the extra ones a little apart.
    /// </summary>
    void PlaceOnGround(StockCell cell, Vector3 at, int quantity)
    {
        var world = player.Session.World;
        for (int i = 0; i < quantity; i++)
        {
            float turn = i * MathF.Tau / quantity;
            var spot = i == 0 ? at : at + new Vector3(MathF.Cos(turn), 0, MathF.Sin(turn)) * 0.6f;
            world.Commands.Enqueue(new DebugPlaceItem(cell.Entry.Id, 1, spot, 0)
            {
                Tick = world.Tick,
                Quality = cell.Quality?.Quality,
                Exact = cell.Quality?.Exact,
            });
        }

        ApplyWhilePaused();
    }

    /// <summary>
    /// Runs one simulation tick while the game is paused, so the command just queued takes effect at once.
    /// </summary>
    void ApplyWhilePaused()
    {
        if (player.Session.Simulation.IsPaused) player.Session.Step(1);
    }

    #endregion
}
