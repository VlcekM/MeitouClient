using System.Globalization;
using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Game.Rendering.Overlays;
using Meitou.Simulation;
using EngineKey = Meitou.Game.Host.Input.Key;

namespace Meitou.Game.DevTools;

/// <summary>
/// The debug menu's "Faction relations" sub-view (docs/game/ui-input.md "Debug menu"): first the faction whose relations are edited (the player's
/// first), then "All factions" and every other faction with the live relation both ways. A value typed in a row and Enter sets that pair both ways
/// (<see cref="DebugSetRelation"/>). Keys only: Up and Down pick, Left and Right page, Enter chooses or sets, Backspace edits, Esc steps back.
/// </summary>
sealed class DebugRelationsEditor(GameHost host)
{
    #region State

    /// <summary>
    /// The most lines the list shows at once; a longer list scrolls.
    /// </summary>
    const int MaxRows = 18;

    /// <summary>
    /// The share of the screen's height the panel may reach, so it stays clear of the game's own interface below.
    /// </summary>
    const float MaxPanelHeight = 0.7f;

    /// <summary>
    /// How far Left and Right move.
    /// </summary>
    const int Page = 10;

    /// <summary>
    /// The longest value the keys can type.
    /// </summary>
    const int MaxField = 6;

    /// <summary>
    /// The width of the faction name column.
    /// </summary>
    const int NameWidth = 32;

    /// <summary>
    /// The colour of the heading's crumb and the hint.
    /// </summary>
    static readonly Vector4 Dim = new(0.7f, 0.75f, 0.8f, 1);

    /// <summary>
    /// The highlight's colour.
    /// </summary>
    static readonly Vector4 Highlight = new(0.25f, 0.4f, 0.65f, 0.6f);

    /// <summary>
    /// Whether the list of other factions is shown (else the faction to edit is picked).
    /// </summary>
    bool _editing;

    /// <summary>
    /// The factions the first step lists, as table positions: the player's first, then the rest by name.
    /// </summary>
    int[] _subjects = [];

    /// <summary>
    /// The second step's rows: -1 for "All factions", then the other factions by name.
    /// </summary>
    int[] _others = [];

    /// <summary>
    /// The highlighted line of the first step.
    /// </summary>
    int _subjectCursor;

    /// <summary>
    /// The highlighted line of the second step.
    /// </summary>
    int _otherCursor;

    /// <summary>
    /// The first line shown, kept while the highlight moves.
    /// </summary>
    int _scroll;

    /// <summary>
    /// The second step's typed values, one per row.
    /// </summary>
    string[] _fields = [];

    /// <summary>
    /// The last action's result, shown in the sub-view; empty before one.
    /// </summary>
    string _status = "";

    /// <summary>
    /// The list's lines as last drawn, with their rectangles; a click hit-tests them.
    /// </summary>
    readonly List<(int Index, Vector4 Rect)> _drawnLines = [];

    /// <summary>
    /// The "back" button's rectangle as last drawn; null before the first draw.
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
    /// Whether the sub-view takes typed text: the second step's values (the first only picks, so the rest of the keys are the game's).
    /// </summary>
    internal bool TakesText => _editing;

    /// <summary>
    /// The live relations table; null before a world is loaded.
    /// </summary>
    FactionRelations? Relations => host.PopData?.Relations;

    #endregion

    #region Keys

    /// <summary>
    /// Opens the sub-view at the faction list; nothing (the reason in the returned status) without a world.
    /// </summary>
    internal string Begin()
    {
        if (Relations is not { } relations) return "no world loaded";

        _subjects = ListedFactions(relations, host.Session.World.Player.Faction);
        (_editing, _subjectCursor, _otherCursor, _scroll, _status) = (false, 0, 0, 0, "");
        Open = true;
        return "";
    }

    /// <summary>
    /// A key in the sub-view: Up and Down pick, Left and Right page, Enter chooses or sets, Backspace edits, Esc steps back.
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
            case EngineKey.Left:
                Move(-Page, wrap: false);
                break;
            case EngineKey.Right:
                Move(Page, wrap: false);
                break;
            case EngineKey.Backspace when _editing:
                if (_fields[_otherCursor].Length > 0) _fields[_otherCursor] = _fields[_otherCursor][..^1];
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
    /// A left click: the "back" button steps back as Esc does; a faction of the first step is picked and opened, a row of the second only picked.
    /// </summary>
    internal void Click(Vector2 at)
    {
        if (_backButton is { } back && DebugMenu.Inside(back, at))
        {
            StepBack();
            return;
        }

        foreach (var (index, rect) in _drawnLines)
        {
            if (!DebugMenu.Inside(rect, at)) continue;
            if (_editing)
            {
                _otherCursor = index;
                return;
            }

            _subjectCursor = index;
            Choose();
            return;
        }
    }

    /// <summary>
    /// A typed character: the highlighted row's value.
    /// </summary>
    internal void Type(char c)
    {
        if (!_editing || char.IsControl(c) || _fields[_otherCursor].Length >= MaxField) return;
        _fields[_otherCursor] += c;
    }

    /// <summary>
    /// The mouse wheel: moves the highlight by <paramref name="lines"/> rows (stopping at the ends), so the list scrolls with it.
    /// </summary>
    internal void Scroll(int lines) => Move(lines, wrap: false);

    /// <summary>
    /// Moves the highlight by <paramref name="delta"/> lines, round the ends or stopping at them.
    /// </summary>
    void Move(int delta, bool wrap = true)
    {
        int count = _editing ? _others.Length : _subjects.Length;
        if (count == 0) return;

        ref int cursor = ref _editing ? ref _otherCursor : ref _subjectCursor;
        int at = cursor + delta;
        cursor = wrap ? (at % count + count) % count : Math.Clamp(at, 0, count - 1);
    }

    /// <summary>
    /// Enter: opens the highlighted faction's relations, or sets the highlighted row to its typed value.
    /// </summary>
    void Choose()
    {
        if (_editing) Apply();
        else if (_subjects.Length > 0 && Relations is { } relations) OpenSubject(relations);
    }

    /// <summary>
    /// Esc: from the relations back to the faction list, from it out of the sub-view.
    /// </summary>
    void StepBack()
    {
        if (!_editing)
        {
            Open = false;
            return;
        }

        _editing = false;
        _scroll = 0;
        _status = "";
    }

    #endregion

    #region Actions

    /// <summary>
    /// Opens the highlighted faction's rows: "All factions", then every other listed faction, each with an empty value.
    /// </summary>
    void OpenSubject(FactionRelations relations)
    {
        int subject = _subjects[_subjectCursor];
        _others = [-1, .. ListedFactions(relations, -1).Where(f => f != subject)];
        _fields = new string[_others.Length];
        Array.Fill(_fields, "");
        (_editing, _otherCursor, _scroll, _status) = (true, 0, 0, "");
    }

    /// <summary>
    /// Sets the highlighted pair (or the faction and every other one) to the typed value, both ways.
    /// </summary>
    void Apply()
    {
        if (Relations is not { } relations) return;

        string field = _fields[_otherCursor];
        if (!float.TryParse(field, NumberStyles.Float, CultureInfo.InvariantCulture, out float value) || !float.IsFinite(value)
            || value < -100 || value > 100)
        {
            _status = "type a relation from -100 to 100";
            return;
        }

        int subject = _subjects[_subjectCursor];
        int other = _others[_otherCursor];
        var world = host.Session.World;
        world.Commands.Enqueue(new DebugSetRelation(subject, other < 0 ? null : other, value) { Tick = world.Tick });
        _fields[_otherCursor] = "";
        string whom = other < 0 ? "every faction" : relations.Factions[other].Name;
        _status = $"{relations.Factions[subject].Name} and {whom}: {Format(value)}";
        host.DebugMenu.ApplyWhilePaused();
    }

    /// <summary>
    /// The factions worth listing (not <c>not real</c>, not the generation template), <paramref name="first"/> at the head and the rest by name.
    /// </summary>
    static int[] ListedFactions(FactionRelations relations, int first)
    {
        return [.. Enumerable.Range(0, relations.Count)
            .Where(i => i == first || (!relations.Factions[i].NotReal && relations.Factions[i].Id != "GENERATION_TEMPLATE_FACTION"))
            .OrderBy(i => i != first)
            .ThenBy(i => relations.Factions[i].Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// A relation as a row shows it.
    /// </summary>
    static string Format(float value) => value.ToString("0.#", CultureInfo.InvariantCulture);

    #endregion

    #region Drawing

    /// <summary>
    /// Draws the sub-view in the debug menu's place: the heading, the list in a window of at most <see cref="MaxRows"/> lines that scrolls with the
    /// highlight, the keys and the last action. All sizes are multiplied by <paramref name="scale"/> (the framebuffer's pixels per point).
    /// </summary>
    internal void Draw(DebugOverlay o, int width, int height, float top, float scale, string close, bool atRight,
        DebugOverlay.OverlayPass? pass = null)
    {
        if (Relations is not { } relations) return;

        float lineHeight = o.LineHeight * scale;
        float pad = 10 * scale;
        float y0 = top + 16 * scale;
        string title = $"Faction relations   ({close} closes)";
        string hint = _editing
            ? "Up/Down or click pick   type -100 to 100   Enter set both ways   Esc back"
            : "Up/Down or click pick   Enter choose   Esc back";

        var lines = DisplayLines(relations);
        int room = Math.Clamp((int)((height * MaxPanelHeight - y0 - 2 * pad) / lineHeight) - 8, 5, MaxRows);
        int first = ScrollTo(room, lines.Count);
        int count = Math.Min(room, lines.Count - first);
        string crumbLine = CrumbLine(relations, first, count, room, lines.Count);

        int chars = new[] { title.Length, hint.Length, crumbLine.Length, _status.Length, 48 }.Max();
        for (int i = first; i < first + count; i++) chars = Math.Max(chars, lines[i].Length + 2);
        float w = chars * o.CharWidth * scale + 2 * pad;
        float h = (count + (_status.Length > 0 ? 8 : 7)) * lineHeight + 2 * pad;
        float x0 = atRight ? Math.Max(8 * scale, width - w - 16 * scale) : 16 * scale;
        o.Rect(x0, y0, x0 + w, y0 + h, DebugOverlay.PanelColour);
        _panelRect = new Vector4(x0, y0, x0 + w, y0 + h);

        float y = y0 + pad;
        o.TextScaled(title, x0 + pad, y, DebugOverlay.TextColour, scale);
        y += 1.5f * lineHeight;
        o.TextScaled(crumbLine, x0 + pad, y, Dim, scale);
        y += 1.5f * lineHeight;
        DrawLines(o, x0, y, w, first, count, lines, scale);
        y += (count + 0.5f) * lineHeight;
        _backButton = DebugMenu.DrawBackButton(o, x0 + pad, y, scale, host.Player.Mouse);
        y += lineHeight;
        o.TextScaled(hint, x0 + pad, y, Dim, scale);
        if (_status.Length > 0) o.TextScaled(_status, x0 + pad, y + lineHeight, new Vector4(1f, 0.9f, 0.4f, 1), scale);
        o.Flush(width, height, null, pass);
    }

    /// <summary>
    /// The heading's second line: where the sub-view is, and the visible range of the list (or its length when it all shows).
    /// </summary>
    string CrumbLine(FactionRelations relations, int first, int count, int room, int total)
    {
        string crumb = _editing ? $"Relations > {relations.Factions[_subjects[_subjectCursor]].Name}" : "Relations > pick a faction";
        string range = total > room ? $"{first + 1}-{first + count} of {total}" : total.ToString(CultureInfo.InvariantCulture);
        return $"{crumb}   ({range})";
    }

    /// <summary>
    /// The current step's lines: the factions to pick, or each other faction with the relation towards it, its relation back, and the typed value.
    /// </summary>
    List<string> DisplayLines(FactionRelations relations)
    {
        int player = host.Session.World.Player.Faction;
        if (!_editing)
            return [.. _subjects.Select(f => relations.Factions[f].Name + (f == player ? "   (yours)" : ""))];

        int subject = _subjects[_subjectCursor];
        var lines = new List<string>(_others.Length);
        for (int i = 0; i < _others.Length; i++)
        {
            int other = _others[i];
            string field = "set " + _fields[i] + (i == _otherCursor ? "_" : "");
            string name = other < 0 ? "All factions" : relations.Factions[other].Name;
            if (name.Length > NameWidth) name = name[..NameWidth];
            string now = other < 0 ? "" : $"{Format(relations.Get(subject, other)),6} / {Format(relations.Get(other, subject)),-6}";
            lines.Add($"{name.PadRight(NameWidth)} {now,-15} {field}");
        }

        return lines;
    }

    /// <summary>
    /// The first line to show of a window of <paramref name="room"/> lines: the one kept from before, moved just far enough to hold the highlight.
    /// </summary>
    int ScrollTo(int room, int total)
    {
        int cursor = _editing ? _otherCursor : _subjectCursor;
        int first = Math.Clamp(_scroll, 0, Math.Max(total - room, 0));
        if (cursor < first) first = cursor;
        else if (cursor >= first + room) first = cursor - room + 1;
        return _scroll = first;
    }

    /// <summary>
    /// The <paramref name="count"/> lines from <paramref name="first"/>, the highlighted one marked.
    /// </summary>
    void DrawLines(DebugOverlay o, float x0, float y, float w, int first, int count, List<string> lines, float scale)
    {
        float lineHeight = o.LineHeight * scale;
        float pad = 10 * scale;
        int cursor = _editing ? _otherCursor : _subjectCursor;
        _drawnLines.Clear();
        for (int i = first; i < first + count; i++, y += lineHeight)
        {
            var rect = new Vector4(x0 + 4 * scale, y, x0 + w - 4 * scale, y + lineHeight);
            bool current = i == cursor;
            if (current) o.Rect(rect.X, rect.Y, rect.Z, rect.W, Highlight);
            DebugMenu.DrawHover(o, rect, host.Player.Mouse);
            _drawnLines.Add((i, rect));
            o.TextScaled((current ? "> " : "  ") + lines[i], x0 + pad, y, DebugOverlay.TextColour, scale);
        }
    }

    #endregion
}
