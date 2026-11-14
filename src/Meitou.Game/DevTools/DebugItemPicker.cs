using System.Numerics;
using Meitou.Game.Rendering.Overlays;
using Meitou.Simulation;
using EngineKey = Meitou.Game.Host.Input.Key;

namespace Meitou.Game.DevTools;

/// <summary>
/// The debug menu's "Drop item" sub-view (docs/game/ui-input.md "Debug menu"): the item records by category (record type, then the record's own
/// category), then a quality where the item has one. Typing anything searches every item at once: a single list of the names that hold the text,
/// whatever their category. Enter on the last step (the item, or its quality) drops one of it at the camera's target
/// (<see cref="DebugPlaceItem"/>) and stays there, so each further Enter drops another. Keys only: Up and Down pick, Left and Right page, Enter
/// chooses or drops, Esc steps back.
/// </summary>
sealed class DebugItemPicker(GameHost host)
{
    #region State

    /// <summary>
    /// The steps of the sub-view, in the order they are walked.
    /// </summary>
    enum Step
    {
        /// <summary>
        /// The categories (and "All items").
        /// </summary>
        Categories,

        /// <summary>
        /// A category's own sub-categories.
        /// </summary>
        Sub,

        /// <summary>
        /// The items of the chosen category.
        /// </summary>
        Items,

        /// <summary>
        /// The qualities of the chosen item.
        /// </summary>
        Quality,

        /// <summary>
        /// Every item whose name holds the typed text, whatever its category.
        /// </summary>
        Search,
    }

    /// <summary>
    /// One line of the current list: what is shown and what it stands for (null: nothing to choose).
    /// </summary>
    readonly record struct Line(string Text, string? Key);

    /// <summary>
    /// The longest search text the keys can type.
    /// </summary>
    const int MaxSearch = 40;

    /// <summary>
    /// The most lines the list shows at once; a longer list scrolls.
    /// </summary>
    const int MaxRows = 18;

    /// <summary>
    /// The share of the screen's height the panel may reach, so it stays clear of the game's own interface below.
    /// </summary>
    const float MaxPanelHeight = 0.7f;

    /// <summary>
    /// How far Left and Right move in a list.
    /// </summary>
    const int Page = 10;

    /// <summary>
    /// The quality the list of qualities starts on (the standard grade).
    /// </summary>
    const int StandardQuality = 40;

    /// <summary>
    /// The key of the lines that stand for "everything" (all items, all of a category).
    /// </summary>
    const string AllKey = "*all";

    /// <summary>
    /// The item records, grouped.
    /// </summary>
    readonly DebugItemCatalog _catalog = new();

    /// <summary>
    /// The current step's lines.
    /// </summary>
    List<Line> _lines = [];

    /// <summary>
    /// The highlighted line of each step, kept when a step is left so that coming back finds the place.
    /// </summary>
    readonly int[] _cursor = new int[5];

    /// <summary>
    /// The first line shown of each step's list.
    /// </summary>
    readonly int[] _scroll = new int[5];

    /// <summary>
    /// The step shown.
    /// </summary>
    Step _step;

    /// <summary>
    /// The text typed so far: every item whose name holds it (anywhere, ignoring case) is listed, in the search step.
    /// </summary>
    string _search = "";

    /// <summary>
    /// The step the search was typed in, which Esc returns to.
    /// </summary>
    Step _browse;

    /// <summary>
    /// The step the qualities were opened from (the items or the search results), which Esc returns to.
    /// </summary>
    Step _from;

    /// <summary>
    /// The category and sub-category being browsed; null: all.
    /// </summary>
    string? _group, _sub;

    /// <summary>
    /// The item whose quality is being picked.
    /// </summary>
    DebugItemEntry? _chosen;

    /// <summary>
    /// The qualities offered for <see cref="_chosen"/>.
    /// </summary>
    IReadOnlyList<DebugItemQuality> _qualities = [];

    /// <summary>
    /// What the last drop was (item and quality), and how many times in a row.
    /// </summary>
    string _lastDrop = "";

    /// <summary>
    /// How many times <see cref="_lastDrop"/> was dropped in a row.
    /// </summary>
    int _dropCount;

    /// <summary>
    /// The sub-view is shown (the debug menu gives it every key).
    /// </summary>
    internal bool Open { get; private set; }

    /// <summary>
    /// The last drop's result, shown in the sub-view; empty before one.
    /// </summary>
    internal string Status { get; private set; } = "";

    #endregion

    #region Keys

    /// <summary>
    /// Opens the sub-view at the categories with nothing searched for.
    /// </summary>
    internal void Begin()
    {
        Open = true;
        _step = Step.Categories;
        (_search, _group, _sub, _chosen, Status, _lastDrop) = ("", null, null, null, "", "");
        Array.Clear(_cursor);
        Array.Clear(_scroll);
        Refresh();
    }

    /// <summary>
    /// A key in the sub-view: Up and Down pick, Left and Right page, Enter chooses or drops, Backspace edits the search, Esc steps back.
    /// </summary>
    internal void Key(EngineKey key)
    {
        switch (key)
        {
            case EngineKey.Up:
                MoveCursor(-1);
                break;
            case EngineKey.Down:
                MoveCursor(1);
                break;
            case EngineKey.Left:
                MoveCursor(-Page, wrap: false);
                break;
            case EngineKey.Right:
                MoveCursor(Page, wrap: false);
                break;
            case EngineKey.Backspace when _step == Step.Search:
                SetSearch(_search[..^1]);
                break;
            case EngineKey.Enter:
                Choose();
                break;
            case EngineKey.Escape:
                StepBack();
                break;
        }
    }

    /// <summary>
    /// A typed character: added to the search text, which lists every matching item at once (not while a quality is being picked).
    /// </summary>
    internal void Type(char c)
    {
        if (_step == Step.Quality || char.IsControl(c) || _search.Length >= MaxSearch) return;

        if (_step != Step.Search) _browse = _step;
        SetSearch(_search + c);
    }

    /// <summary>
    /// Sets the search text and lists its matches from the top; empty text leaves the search for the step it was typed in.
    /// </summary>
    void SetSearch(string text)
    {
        _search = text;
        _step = text.Length > 0 ? Step.Search : _browse;
        if (text.Length > 0) (_cursor[(int)Step.Search], _scroll[(int)Step.Search]) = (0, 0);
        Refresh();
    }

    /// <summary>
    /// Moves the highlight by <paramref name="delta"/> lines, round the ends or stopping at them.
    /// </summary>
    void MoveCursor(int delta, bool wrap = true)
    {
        int count = _lines.Count;
        if (count == 0) return;

        int at = _cursor[(int)_step] + delta;
        _cursor[(int)_step] = wrap ? (at % count + count) % count : Math.Clamp(at, 0, count - 1);
    }

    /// <summary>
    /// Enter: into the highlighted category, item or quality; on a quality, or an item without one, the item drops.
    /// </summary>
    void Choose()
    {
        if (_lines.Count == 0 || _lines[_cursor[(int)_step]].Key is not { } key) return;

        switch (_step)
        {
            case Step.Categories:
                (_group, _sub) = (key == AllKey ? null : key, null);
                Enter(_group is not null && HasSubs(_group) ? Step.Sub : Step.Items);
                break;
            case Step.Sub:
                _sub = key == AllKey ? null : key;
                Enter(Step.Items);
                break;
            case Step.Items or Step.Search:
                ChooseItem(key);
                break;
            default:
                Drop(_chosen!, _qualities[_cursor[(int)Step.Quality]]);
                break;
        }
    }

    /// <summary>
    /// An item line: an item without a quality drops at once, else its qualities are listed, the first of quality 40 or more (the standard grade)
    /// highlighted.
    /// </summary>
    void ChooseItem(string id)
    {
        if (host.Scene.Database is not { } db || _catalog.Find(id) is not { } item) return;

        var qualities = _catalog.Qualities(item, db);
        if (qualities.Count == 0)
        {
            Drop(item, null);
            return;
        }

        (_chosen, _qualities, _from) = (item, qualities, _step);
        Enter(Step.Quality);
        int standard = qualities.ToList().FindIndex(q => q.Quality >= StandardQuality);
        _cursor[(int)Step.Quality] = standard >= 0 ? standard : qualities.Count - 1;
    }

    /// <summary>
    /// Shows <paramref name="step"/> from its top.
    /// </summary>
    void Enter(Step step)
    {
        _step = step;
        (_cursor[(int)step], _scroll[(int)step]) = (0, 0);
        Refresh();
    }

    /// <summary>
    /// Esc: the step before this one (the search results give way to the step the search was typed in); from the categories the sub-view closes.
    /// </summary>
    void StepBack()
    {
        if (_step == Step.Categories)
        {
            Open = false;
            return;
        }

        if (_step == Step.Search)
        {
            SetSearch("");
            return;
        }

        _step = _step switch
        {
            Step.Quality => _from,
            Step.Items when _group is not null && HasSubs(_group) => Step.Sub,
            _ => Step.Categories,
        };
        Refresh();
    }

    #endregion

    #region Dropping

    /// <summary>
    /// Drops one <paramref name="item"/> at <paramref name="quality"/> (null: the record's own) on the ground at the camera's target, as high as the
    /// ground or floor there.
    /// </summary>
    void Drop(DebugItemEntry item, DebugItemQuality? quality)
    {
        var world = host.Session.World;
        var target = host.Session.Camera.Current.Target;
        float height = world.Walkability.GroundHeight(target.X, target.Z, target.Y);
        world.Commands.Enqueue(new DebugPlaceItem(item.Id, 1, new Vector3(target.X, height, target.Z), 0)
        {
            Tick = world.Tick,
            Quality = quality?.Quality,
            Exact = quality?.Exact,
        });

        string label = quality is null ? item.Name : $"{item.Name} ({quality.Label.Split("  ")[0]})";
        string drop = $"{item.Id}/{quality?.Quality}/{quality?.Exact?.Model}";
        _dropCount = drop == _lastDrop ? _dropCount + 1 : 1;
        _lastDrop = drop;
        Status = $"dropped {_dropCount} x {label} at {target.X:0}, {target.Z:0}";
    }

    #endregion

    #region Lists

    /// <summary>
    /// The items of the category being browsed (all items for none), and with <paramref name="inSub"/> only those of the sub-category too.
    /// </summary>
    IEnumerable<DebugItemEntry> Browsed(bool inSub = true)
    {
        if (host.Scene.Database is not { } db) return [];

        return _catalog.Entries(db).Where(e => (_group is null || e.Group == _group) && (!inSub || _sub is null || e.Sub == _sub));
    }

    /// <summary>
    /// Whether the items of a category are told apart by a sub-category.
    /// </summary>
    bool HasSubs(string group) => host.Scene.Database is { } db && _catalog.Entries(db).Any(e => e.Group == group && e.Sub.Length > 0);

    /// <summary>
    /// Makes the lines of the current step again.
    /// </summary>
    void Refresh()
    {
        _lines = _step switch
        {
            Step.Categories => CategoryLines(),
            Step.Sub => SubLines(),
            Step.Items => ItemLines(),
            Step.Search => SearchLines(),
            _ => [.. _qualities.Select(q => new Line(q.Label, q.Label))],
        };
        _cursor[(int)_step] = Math.Clamp(_cursor[(int)_step], 0, Math.Max(_lines.Count - 1, 0));
    }

    /// <summary>
    /// All items, then each category.
    /// </summary>
    List<Line> CategoryLines()
    {
        var found = host.Scene.Database is { } db ? _catalog.Entries(db) : [];
        var lines = new List<Line> { new($"All items ({found.Count})", AllKey) };
        foreach (string group in DebugItemCatalog.GroupNames)
        {
            int count = found.Count(e => e.Group == group);
            if (count > 0) lines.Add(new($"{group} ({count})", group));
        }

        return lines;
    }

    /// <summary>
    /// The chosen category as a whole, then its sub-categories (the unnamed "Other" last).
    /// </summary>
    List<Line> SubLines()
    {
        var found = Browsed(inSub: false).ToList();
        var lines = new List<Line> { new($"All {_group} ({found.Count})", AllKey) };
        var subs = found.GroupBy(e => e.Sub).OrderBy(g => g.Key == "Other").ThenBy(g => g.Key, StringComparer.Ordinal);
        lines.AddRange(subs.Select(g => new Line($"{g.Key} ({g.Count()})", g.Key)));
        return lines;
    }

    /// <summary>
    /// The items of the browsed category by name; with every category shown, each one's category after its name.
    /// </summary>
    List<Line> ItemLines() => [.. Browsed().Select(e => new Line(_group is null ? $"{e.Name}   [{e.Group}]" : e.Name, e.Id))];

    /// <summary>
    /// Every item whose name holds the search text, by name, each with its category and sub-category after it.
    /// </summary>
    List<Line> SearchLines()
    {
        if (host.Scene.Database is not { } db) return [];

        var lines = _catalog.Entries(db)
            .Where(e => e.Name.Contains(_search, StringComparison.OrdinalIgnoreCase))
            .Select(e => new Line($"{e.Name}   [{(e.Sub.Length > 0 ? $"{e.Group} / {e.Sub}" : e.Group)}]", e.Id))
            .ToList();
        return lines.Count > 0 ? lines : [new("(no item matches)", null)];
    }

    /// <summary>
    /// Where the sub-view is, for its heading: "Categories > Weapons > Katanas".
    /// </summary>
    string Breadcrumb()
    {
        if (_step == Step.Search) return "Search results";

        var parts = new List<string> { _from == Step.Search && _step == Step.Quality ? "Search results" : "Categories" };
        if (_step == Step.Quality && _from == Step.Search) return string.Join(" > ", parts.Append(_chosen?.Name ?? ""));

        if (_step >= Step.Sub) parts.Add(_group ?? "All items");
        if (_step >= Step.Items && _sub is not null) parts.Add(_sub);
        if (_step == Step.Quality && _chosen is not null) parts.Add(_chosen.Name);
        return string.Join(" > ", parts);
    }

    #endregion

    #region Drawing

    /// <summary>
    /// Draws the sub-view in the debug menu's place: the heading, the search text, the list in a window of at most <see cref="MaxRows"/> lines that
    /// scrolls with the highlight (and a scroll bar when the list is longer), the keys and the last drop. All sizes are multiplied by
    /// <paramref name="scale"/> (the framebuffer's pixels per point).
    /// </summary>
    internal void Draw(DebugOverlay o, int width, int height, float top, float scale, string close, bool atRight)
    {
        float charWidth = o.CharWidth * scale, lineHeight = o.LineHeight * scale, pad = 10 * scale, y0 = top + 16 * scale;
        string title = $"Drop item   ({close} closes)";
        string search = _step == Step.Quality ? $"Quality of {_chosen?.Name}" : $"Search: {_search}_";
        string hint = _step == Step.Quality
            ? "Up/Down pick   Enter drop at the camera target   Esc back"
            : "Up/Down pick   Left/Right page   type to search all items   Enter choose / drop   Esc back";
        int room = Math.Clamp((int)((height * MaxPanelHeight - y0 - 2 * pad) / lineHeight) - 6, 5, MaxRows);
        int first = ScrollTo(room), count = Math.Min(room, _lines.Count - first);
        string crumb = $"{Breadcrumb()}   ({(_lines.Count > room ? $"{first + 1}-{first + count} of " : "")}{_lines.Count})";

        int chars = Math.Max(Math.Max(title.Length, hint.Length), Math.Max(crumb.Length, 56));
        for (int i = first; i < first + count; i++) chars = Math.Max(chars, _lines[i].Text.Length + 2);
        chars = Math.Max(chars, Math.Max(search.Length, Status.Length));

        float bar = 8 * scale, w = chars * charWidth + 2 * pad + bar, h = (count + (Status.Length > 0 ? 7 : 6)) * lineHeight + 2 * pad;
        float x0 = atRight ? Math.Max(8 * scale, width - w - 16 * scale) : 16 * scale;
        o.Rect(x0, y0, x0 + w, y0 + h, DebugOverlay.PanelColour);

        var dim = new Vector4(0.7f, 0.75f, 0.8f, 1);
        float y = y0 + pad;
        o.TextScaled(title, x0 + pad, y, DebugOverlay.TextColour, scale);
        y += 1.5f * lineHeight;
        o.TextScaled(crumb, x0 + pad, y, dim, scale);
        y += lineHeight;
        o.TextScaled(search, x0 + pad, y, DebugOverlay.TextColour, scale);
        y += 1.5f * lineHeight;
        DrawLines(o, x0, y, w - bar, first, count, scale);
        DrawScrollBar(o, x0 + w - pad / 2 - 4 * scale, y, count * lineHeight, first, count, scale);
        y += (count + 0.5f) * lineHeight;
        o.TextScaled(hint, x0 + pad, y, dim, scale);
        if (Status.Length > 0) o.TextScaled(Status, x0 + pad, y + lineHeight, new Vector4(1f, 0.9f, 0.4f, 1), scale);
        o.Flush(width, height);
    }

    /// <summary>
    /// The first line to show of a window of <paramref name="room"/> lines: the one kept from before, moved just far enough to hold the highlight.
    /// </summary>
    int ScrollTo(int room)
    {
        int step = (int)_step, cursor = _cursor[step];
        int first = Math.Clamp(_scroll[step], 0, Math.Max(_lines.Count - room, 0));
        if (cursor < first) first = cursor;
        else if (cursor >= first + room) first = cursor - room + 1;
        return _scroll[step] = first;
    }

    /// <summary>
    /// The <paramref name="count"/> lines from <paramref name="first"/>, the highlighted one marked.
    /// </summary>
    void DrawLines(DebugOverlay o, float x0, float y, float w, int first, int count, float scale)
    {
        float lineHeight = o.LineHeight * scale, pad = 10 * scale;
        for (int i = first; i < first + count; i++, y += lineHeight)
        {
            bool current = i == _cursor[(int)_step];
            if (current) o.Rect(x0 + 4 * scale, y, x0 + w - 4 * scale, y + lineHeight, new Vector4(0.25f, 0.4f, 0.65f, 0.6f));
            o.TextScaled((current ? "> " : "  ") + _lines[i].Text, x0 + pad, y, DebugOverlay.TextColour, scale);
        }
    }

    /// <summary>
    /// A bar at <paramref name="x"/> beside the window (top <paramref name="y"/>, <paramref name="length"/> high): a track, and a thumb as long as the
    /// window is of the list, as far down as the window is; nothing when the whole list shows.
    /// </summary>
    void DrawScrollBar(DebugOverlay o, float x, float y, float length, int first, int count, float scale)
    {
        int total = _lines.Count;
        if (total <= count) return;

        float width = 4 * scale, thumb = Math.Max(length * count / total, 12 * scale);
        float at = y + (length - thumb) * first / (total - count);
        o.Rect(x, y, x + width, y + length, new Vector4(1, 1, 1, 0.12f));
        o.Rect(x, at, x + width, at + thumb, new Vector4(0.55f, 0.7f, 0.95f, 0.9f));
    }

    #endregion
}
