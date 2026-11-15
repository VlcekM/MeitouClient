using System.Globalization;
using System.Numerics;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Game.Rendering.Overlays;
using Meitou.Game.Ui;
using Meitou.Simulation;
using Meitou.Simulation.Editing;
using EngineKey = Meitou.Game.Host.Input.Key;

namespace Meitou.Game.DevTools;

/// <summary>
/// The debug menu's "Edit character" sub-view (docs/game/ui-input.md "Debug menu"): quick actions (max out everything, the attributes, the skills),
/// the character editor's appearance page, and every stat by the stats window's category. A stat is set by typing its value in its row, which starts
/// with the character's current value; Enter sets it. Keys only: Up and Down pick, Left and Right page, Enter chooses or sets, Backspace edits, Esc
/// steps back.
/// </summary>
sealed class DebugCharacterEditor(GameHost host)
{
    #region State

    /// <summary>
    /// The steps of the sub-view, in the order they are walked.
    /// </summary>
    enum Step
    {
        /// <summary>
        /// The quick actions, Appearance and the categories.
        /// </summary>
        Root,

        /// <summary>
        /// One category's stats, each with its value.
        /// </summary>
        Category,
    }

    /// <summary>
    /// What a root line does.
    /// </summary>
    enum RootAction
    {
        /// <summary>
        /// A separator.
        /// </summary>
        None,

        /// <summary>
        /// Sets every stat to 100.
        /// </summary>
        MaxEverything,

        /// <summary>
        /// Sets the attributes to 100.
        /// </summary>
        MaxAttributes,

        /// <summary>
        /// Sets every skill to 100.
        /// </summary>
        MaxSkills,

        /// <summary>
        /// Opens the character editor on the first target.
        /// </summary>
        Appearance,

        /// <summary>
        /// Opens a category's stats.
        /// </summary>
        Category,
    }

    /// <summary>
    /// A line of the root list: a separator, a quick action, Appearance, or a category.
    /// </summary>
    readonly record struct RootLine(string Text, bool Title, RootAction Action, int Category = -1);

    /// <summary>
    /// The stats by the stats window's panels, with the titles the debug menu gives them.
    /// </summary>
    static readonly (string Title, StatsEnumerated[] Stats)[] Categories =
    [
        ("Attributes", StatsWindow.StatPanels[0]),
        ("Weapons", StatsWindow.StatPanels[1]),
        ("Combat", StatsWindow.StatPanels[2]),
        ("Thievery", StatsWindow.StatPanels[3]),
        ("Sciences", StatsWindow.StatPanels[4]),
    ];

    /// <summary>
    /// The root list: the quick actions, Appearance, then every category.
    /// </summary>
    static readonly RootLine[] RootLines = BuildRootLines();

    /// <summary>
    /// The highest value a stat can be set to.
    /// </summary>
    const float MaxValue = 1000;

    /// <summary>
    /// The longest value the keys can type.
    /// </summary>
    const int MaxField = 8;

    /// <summary>
    /// The most lines the list shows at once; a longer list scrolls.
    /// </summary>
    const int MaxRows = 18;

    /// <summary>
    /// The share of the screen's height the panel may reach, so it stays clear of the game's own interface below.
    /// </summary>
    const float MaxPanelHeight = 0.7f;

    /// <summary>
    /// How far Left and Right move in a category.
    /// </summary>
    const int Page = 10;

    /// <summary>
    /// The colour of the heading's crumb, the hint and the root's separators.
    /// </summary>
    static readonly Vector4 Dim = new(0.7f, 0.75f, 0.8f, 1);

    /// <summary>
    /// The step shown.
    /// </summary>
    Step _step;

    /// <summary>
    /// The highlighted line of the root list.
    /// </summary>
    int _rootCursor;

    /// <summary>
    /// The category shown.
    /// </summary>
    int _category;

    /// <summary>
    /// The highlighted stat in the category shown.
    /// </summary>
    int _categoryCursor;

    /// <summary>
    /// The first line shown, kept while the highlight moves.
    /// </summary>
    int _scroll;

    /// <summary>
    /// The category's values as typed, one per stat.
    /// </summary>
    string[] _fields = [];

    /// <summary>
    /// Whether each value was typed in since the category opened; the first keystroke replaces the pre-filled value.
    /// </summary>
    bool[] _edited = [];

    /// <summary>
    /// The last action's result, shown in the sub-view; empty before one.
    /// </summary>
    string _status = "";

    /// <summary>
    /// The list's lines as last drawn, with their rectangles; a click hit-tests them.
    /// </summary>
    readonly List<(int Index, Vector4 Rect)> _drawnLines = [];

    /// <summary>
    /// The "back" button's rectangle as last drawn (a click on it steps back, as Esc does); null before the first draw.
    /// </summary>
    Vector4? _backButton;

    /// <summary>
    /// The panel's rectangle as last drawn (x0, y0, x1, y1); a click outside it is not the sub-view's.
    /// </summary>
    Vector4? _panelRect;

    /// <summary>
    /// The sub-view is shown (the debug menu gives it the keys).
    /// </summary>
    internal bool Open { get; private set; }

    /// <summary>
    /// Whether the sub-view takes typed text: a category's stat values (the root list only picks, so the rest of the keys are the game's).
    /// </summary>
    internal bool TakesText => _step == Step.Category;

    /// <summary>
    /// Makes the root list: the quick actions, Appearance, then every category.
    /// </summary>
    static RootLine[] BuildRootLines()
    {
        var lines = new List<RootLine>
        {
            new("Quick actions", true, RootAction.None),
            new("Max out everything", false, RootAction.MaxEverything),
            new("Max out attributes", false, RootAction.MaxAttributes),
            new("Max out skills", false, RootAction.MaxSkills),
            new("Edit", true, RootAction.None),
            new("Appearance", false, RootAction.Appearance),
        };
        for (int i = 0; i < Categories.Length; i++) lines.Add(new(Categories[i].Title, false, RootAction.Category, i));
        return [.. lines];
    }

    #endregion

    #region Keys

    /// <summary>
    /// Opens the sub-view at the quick actions.
    /// </summary>
    internal void Begin()
    {
        Open = true;
        _step = Step.Root;
        (_category, _categoryCursor, _scroll, _status) = (0, 0, 0, "");
        _rootCursor = Math.Max(0, Array.FindIndex(RootLines, l => !l.Title));
        _fields = [];
        _edited = [];
    }

    /// <summary>
    /// A key in the sub-view: Up and Down pick, Left and Right page a category, Enter chooses or sets, Backspace edits, Esc steps back.
    /// </summary>
    internal void Key(EngineKey key)
    {
        switch (key)
        {
            case EngineKey.Up:
                Move(-1);
                break;
            case EngineKey.Down:
                Move(1);
                break;
            case EngineKey.Left when _step == Step.Category:
                Move(-Page, wrap: false);
                break;
            case EngineKey.Right when _step == Step.Category:
                Move(Page, wrap: false);
                break;
            case EngineKey.Backspace when _step == Step.Category:
                Backspace();
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
    /// Whether <paramref name="at"/> lies on the panel as last drawn.
    /// </summary>
    internal bool Contains(Vector2 at) => _panelRect is { } rect && DebugMenu.Inside(rect, at);

    /// <summary>
    /// A left click on the sub-view: the "back" button steps back as Esc does; a root line is picked and chosen as Enter does; a category row is only
    /// picked (Enter sets it).
    /// </summary>
    internal void Click(Vector2 at)
    {
        if (_backButton is { } back && at.X >= back.X && at.X <= back.Z && at.Y >= back.Y && at.Y <= back.W)
        {
            StepBack();
            return;
        }

        foreach (var (index, rect) in _drawnLines)
        {
            if (at.X < rect.X || at.X > rect.Z || at.Y < rect.Y || at.Y > rect.W) continue;
            if (_step == Step.Root)
            {
                if (RootLines[index].Title) return;
                _rootCursor = index;
                Choose();
                return;
            }

            _categoryCursor = index;
            return;
        }
    }

    /// <summary>
    /// A typed character: the highlighted stat's value, replacing the pre-filled one on the first keystroke.
    /// </summary>
    internal void Type(char c)
    {
        if (_step != Step.Category || char.IsControl(c)) return;

        if (!_edited[_categoryCursor])
        {
            _fields[_categoryCursor] = "";
            _edited[_categoryCursor] = true;
        }

        if (_fields[_categoryCursor].Length < MaxField) _fields[_categoryCursor] += c;
    }

    /// <summary>
    /// Backspace: the highlighted stat's value, clearing the pre-filled one on the first press.
    /// </summary>
    void Backspace()
    {
        if (!_edited[_categoryCursor])
        {
            _fields[_categoryCursor] = "";
            _edited[_categoryCursor] = true;
            return;
        }

        if (_fields[_categoryCursor].Length > 0) _fields[_categoryCursor] = _fields[_categoryCursor][..^1];
    }

    /// <summary>
    /// Moves the highlight by <paramref name="delta"/> lines, round the ends or stopping at them; the root skips its separators.
    /// </summary>
    void Move(int delta, bool wrap = true)
    {
        if (_step == Step.Root)
        {
            MoveRoot(delta, wrap);
            return;
        }

        int count = Categories[_category].Stats.Length;
        if (count == 0) return;

        int at = _categoryCursor + delta;
        _categoryCursor = wrap ? (at % count + count) % count : Math.Clamp(at, 0, count - 1);
    }

    /// <summary>
    /// The mouse wheel: moves the highlight by <paramref name="lines"/> rows (stopping at the ends), so the list scrolls with it.
    /// </summary>
    internal void Scroll(int lines) => Move(lines, wrap: false);

    /// <summary>
    /// Moves the root's highlight by <paramref name="delta"/> rows, skipping the separators; round the ends or stopping at them.
    /// </summary>
    void MoveRoot(int delta, bool wrap = true)
    {
        int n = RootLines.Length, step = Math.Sign(delta), left = Math.Abs(delta), at = _rootCursor;
        while (left > 0)
        {
            int next = at + step;
            if (next < 0 || next >= n)
            {
                if (!wrap) break;
                next = (next + n) % n;
            }

            at = next;
            if (!RootLines[at].Title) left--;
        }

        _rootCursor = at;
    }

    /// <summary>
    /// Enter: a root line's action, or the highlighted stat set to its typed value.
    /// </summary>
    void Choose()
    {
        if (_step == Step.Root) ChooseRoot();
        else ApplyStat();
    }

    /// <summary>
    /// A root line: a quick action, Appearance, or a category.
    /// </summary>
    void ChooseRoot()
    {
        var line = RootLines[_rootCursor];
        switch (line.Action)
        {
            case RootAction.MaxEverything:
                MaxStats(AllStats(), "every stat");
                break;
            case RootAction.MaxAttributes:
                MaxStats(Categories[0].Stats, "every attribute");
                break;
            case RootAction.MaxSkills:
                MaxStats(SkillStats(), "every skill");
                break;
            case RootAction.Appearance:
                OpenAppearance();
                break;
            case RootAction.Category:
                EnterCategory(line.Category);
                break;
        }
    }

    /// <summary>
    /// Esc: from a category back to the root, from the root out of the sub-view.
    /// </summary>
    void StepBack()
    {
        if (_step == Step.Category)
        {
            _step = Step.Root;
            _scroll = 0;
            _status = "";
            return;
        }

        Open = false;
    }

    #endregion

    #region Actions

    /// <summary>
    /// Opens a category with each stat's current value in its row.
    /// </summary>
    void EnterCategory(int index)
    {
        _category = index;
        _step = Step.Category;
        (_categoryCursor, _scroll, _status) = (0, 0, "");
        _fields = [.. Categories[index].Stats.Select(s => Format(Current(s)))];
        _edited = new bool[_fields.Length];
    }

    /// <summary>
    /// Sets the highlighted stat to its typed value on every target.
    /// </summary>
    void ApplyStat()
    {
        var stat = Categories[_category].Stats[_categoryCursor];
        if (!float.TryParse(_fields[_categoryCursor], NumberStyles.Float, CultureInfo.InvariantCulture, out float value)
            || !float.IsFinite(value) || value < 0)
        {
            _status = $"{StatsWindow.StatLabel(stat)}: a number from 0 to {MaxValue:0}";
            return;
        }

        value = Math.Min(value, MaxValue);
        if (DebugMenu.DebugTargets(host) is not [_, ..] targets)
        {
            _status = "select or inspect a character first";
            return;
        }

        host.Session.World.Commands.Enqueue(new DebugSetStats(targets, [stat], value) { Tick = host.Session.World.Tick });
        _fields[_categoryCursor] = Format(value);
        _edited[_categoryCursor] = false;
        _status = $"{StatsWindow.StatLabel(stat)} {Format(value)} for {NameOf(targets[0])}";
        host.DebugMenu.ApplyWhilePaused();
    }

    /// <summary>
    /// Sets <paramref name="stats"/> to 100 on every target.
    /// </summary>
    void MaxStats(IReadOnlyList<StatsEnumerated> stats, string what)
    {
        if (DebugMenu.DebugTargets(host) is not [_, ..] targets)
        {
            _status = "select or inspect a character first";
            return;
        }

        host.Session.World.Commands.Enqueue(new DebugSetStats(targets, stats, 100) { Tick = host.Session.World.Tick });
        _status = $"{what} 100 for {NameOf(targets[0])}";
        host.DebugMenu.ApplyWhilePaused();
    }

    /// <summary>
    /// Closes the debug menu and opens the character editor on the first target, as a recruit's would open.
    /// </summary>
    void OpenAppearance()
    {
        if (DebugMenu.DebugTargets(host) is not [var first, ..])
        {
            _status = "select or inspect a character first";
            return;
        }

        if (!host.Player.CharacterEditor.OpenEditor(EditKind.Recruit, [first]))
        {
            _status = "the character editor did not open";
            return;
        }

        Open = false;
        host.DebugMenu.DebugMenuOpen = false;
    }

    /// <summary>
    /// Every stat with storage.
    /// </summary>
    static StatsEnumerated[] AllStats() =>
        [.. Enumerable.Range(1, StatsInfo.Count - 1).Select(i => (StatsEnumerated)i).Where(s => StatsInfo.StorageOf(s) == s)];

    /// <summary>
    /// Every stat with storage that is not one of the attributes.
    /// </summary>
    static StatsEnumerated[] SkillStats()
    {
        var attributes = Categories[0].Stats.ToHashSet();
        return [.. AllStats().Where(s => !attributes.Contains(s))];
    }

    /// <summary>
    /// A character's name; empty when the snapshot does not have it.
    /// </summary>
    string NameOf(CharacterId id) => host.Session.CurrentSnapshot.Characters.FirstOrDefault(c => c.Id == id)?.Name ?? "";

    /// <summary>
    /// The first target's value of <paramref name="stat"/>; 0 without one.
    /// </summary>
    float Current(StatsEnumerated stat)
    {
        var table = host.Session.World.Characters;
        if (DebugMenu.DebugTargets(host) is not [var first, ..] || !table.TryResolveNext(first, out int slot)) return 0;
        return table.Cold(slot)?.Stats is { } stats ? stats.Stored(stat) : 0;
    }

    /// <summary>
    /// A value as its row shows it.
    /// </summary>
    static string Format(float value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    #endregion

    #region Drawing

    /// <summary>
    /// Draws the sub-view in the debug menu's place: the heading, the list in a window of at most <see cref="MaxRows"/> lines that scrolls with the
    /// highlight (and a scroll bar when the list is longer), the keys and the last action. All sizes are multiplied by <paramref name="scale"/> (the
    /// framebuffer's pixels per point).
    /// </summary>
    internal void Draw(DebugOverlay o, int width, int height, float top, float scale, string close, bool atRight)
    {
        float lineHeight = o.LineHeight * scale;
        float pad = 10 * scale;
        float y0 = top + 16 * scale;
        string title = $"Edit character   ({close} closes)";
        string hint = _step == Step.Root
            ? "Up/Down or click pick   Enter choose   Esc back"
            : "Up/Down or click pick   type a value   Enter set   Esc back";

        var lines = DisplayLines();
        int room = Math.Clamp((int)((height * MaxPanelHeight - y0 - 2 * pad) / lineHeight) - 8, 5, MaxRows);
        int first = ScrollTo(room, lines.Count);
        int count = Math.Min(room, lines.Count - first);
        string crumbLine = CrumbLine(first, count, room, lines.Count);

        float bar = 8 * scale;
        float w = PanelChars(title, hint, crumbLine, lines, first, count) * o.CharWidth * scale + 2 * pad + bar;
        float h = (count + (_status.Length > 0 ? 8 : 7)) * lineHeight + 2 * pad;
        float x0 = atRight ? Math.Max(8 * scale, width - w - 16 * scale) : 16 * scale;
        o.Rect(x0, y0, x0 + w, y0 + h, DebugOverlay.PanelColour);
        _panelRect = new Vector4(x0, y0, x0 + w, y0 + h);

        float y = y0 + pad;
        o.TextScaled(title, x0 + pad, y, DebugOverlay.TextColour, scale);
        y += 1.5f * lineHeight;
        o.TextScaled(crumbLine, x0 + pad, y, Dim, scale);
        y += 1.5f * lineHeight;
        _drawnLines.Clear();
        DrawLines(o, x0, y, w - bar, first, count, lines, scale);
        DrawScrollBar(o, x0 + w - pad / 2 - 4 * scale, y, count * lineHeight, first, count, lines.Count, scale);
        y += (count + 0.5f) * lineHeight;
        _backButton = DebugMenu.DrawBackButton(o, x0 + pad, y, scale);
        y += lineHeight;
        o.TextScaled(hint, x0 + pad, y, Dim, scale);
        if (_status.Length > 0) o.TextScaled(_status, x0 + pad, y + lineHeight, new Vector4(1f, 0.9f, 0.4f, 1), scale);
        o.Flush(width, height);
    }

    /// <summary>
    /// The heading's second line: where the sub-view is, and the visible range of the list (or its length when it all shows).
    /// </summary>
    string CrumbLine(int first, int count, int room, int total)
    {
        string crumb = _step == Step.Root ? "Character" : $"Character > {Categories[_category].Title}";
        string range = total > room ? $"{first + 1}-{first + count} of {total}" : total.ToString(CultureInfo.InvariantCulture);
        return $"{crumb}   ({range})";
    }

    /// <summary>
    /// The panel's width in characters: the widest of the heading, the hint, the visible lines and the status, 48 at least.
    /// </summary>
    int PanelChars(string title, string hint, string crumbLine, List<(string Text, bool Title)> lines, int first, int count)
    {
        int chars = Math.Max(Math.Max(title.Length, hint.Length), Math.Max(crumbLine.Length, 48));
        for (int i = first; i < first + count; i++) chars = Math.Max(chars, lines[i].Text.Length + 2);
        return Math.Max(chars, _status.Length);
    }

    /// <summary>
    /// The current step's lines: the root's separators and rows, or the category's stats with their values.
    /// </summary>
    List<(string Text, bool Title)> DisplayLines()
    {
        var lines = new List<(string Text, bool Title)>();
        if (_step == Step.Root)
        {
            foreach (var line in RootLines) lines.Add((line.Title ? "-- " + line.Text + " --" : line.Text, line.Title));
            return lines;
        }

        var stats = Categories[_category].Stats;
        for (int i = 0; i < stats.Length; i++)
        {
            string field = _fields[i] + (i == _categoryCursor ? "_" : "");
            lines.Add(($"{StatsWindow.StatLabel(stats[i]),-20} {field}", false));
        }

        return lines;
    }

    /// <summary>
    /// The first line to show of a window of <paramref name="room"/> lines: the one kept from before, moved just far enough to hold the highlight.
    /// </summary>
    int ScrollTo(int room, int total)
    {
        int cursor = _step == Step.Root ? _rootCursor : _categoryCursor;
        int first = Math.Clamp(_scroll, 0, Math.Max(total - room, 0));
        if (cursor < first) first = cursor;
        else if (cursor >= first + room) first = cursor - room + 1;
        return _scroll = first;
    }

    /// <summary>
    /// The <paramref name="count"/> lines from <paramref name="first"/>, the highlighted one marked.
    /// </summary>
    void DrawLines(DebugOverlay o, float x0, float y, float w, int first, int count, List<(string Text, bool Title)> lines, float scale)
    {
        float lineHeight = o.LineHeight * scale;
        float pad = 10 * scale;
        int cursor = _step == Step.Root ? _rootCursor : _categoryCursor;
        for (int i = first; i < first + count; i++, y += lineHeight)
        {
            bool current = i == cursor;
            if (current) o.Rect(x0 + 4 * scale, y, x0 + w - 4 * scale, y + lineHeight, new Vector4(0.25f, 0.4f, 0.65f, 0.6f));
            var (text, title) = lines[i];
            _drawnLines.Add((i, new Vector4(x0 + 4 * scale, y, x0 + w - 4 * scale, y + lineHeight)));
            o.TextScaled((current ? "> " : "  ") + text, x0 + pad, y, title ? Dim : DebugOverlay.TextColour, scale);
        }
    }

    /// <summary>
    /// A bar at <paramref name="x"/> beside the window (top <paramref name="y"/>, <paramref name="length"/> high): a track, and a thumb as long as the
    /// window is of the list, as far down as the window is; nothing when the whole list shows.
    /// </summary>
    void DrawScrollBar(DebugOverlay o, float x, float y, float length, int first, int count, int total, float scale)
    {
        if (total <= count) return;

        float width = 4 * scale;
        float thumb = Math.Max(length * count / total, 12 * scale);
        float at = y + (length - thumb) * first / (total - count);
        o.Rect(x, y, x + width, y + length, new Vector4(1, 1, 1, 0.12f));
        o.Rect(x, at, x + width, at + thumb, new Vector4(0.55f, 0.7f, 0.95f, 0.9f));
    }

    #endregion
}
