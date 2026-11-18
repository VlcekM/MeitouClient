namespace ProvingGrounds.Core.Glue;

// Port of the Arena window's row pointer handling, upstream/ProvingGrounds/plugin/src/ArenaUI.cpp (Proving Grounds by Shutori, GPLv3), task
// 4.5b: MakeRow's delegates (:640-646: click, double click, wheel, drag, release on every row button), OnPortraitRowDrag (:1221-1238),
// OnPortraitRowRelease (:1240-1300, its MouseButton::Right branch), RefreshPointerHighlights (:1201-1219), ApplyDragHighlights (:1171-1191),
// ResetDropZoneVisuals (:1159-1169), OnPresetMousePressed (:2201-2217). MyGUI raises a row's events from the input manager (InputManager::
// injectMouseRelease: the pressed widget's eventMouseButtonReleased, then eventMouseButtonClick, then, inside the double-click time,
// eventMouseButtonDoubleClick). Meitou's faithful screens raise only a press (MyGuiScreenHost.PointerDown -> Activated, MyGuiScreenHost.cs:466),
// so the window side (src/Ui/ArenaUI.cs) feeds this state machine with the press and with the game's own pointer calls (PlayerInterface.MouseDown /
// MouseMove / MouseUp, PlayerInterface.cs:779, :1009, :1023), and turns its actions into the ArenaUiCommands of src/Engine/PgCommand.cs. Every
// action carries the CharacterId the window drew in the row (never a row index).

/// <summary>What a row pointer event becomes (the C++ handler it reaches).</summary>
public enum ArenaRowActionKind { Release, Click, DoubleClick }

/// <summary>One row handler to call: <see cref="ArenaRowActionKind.Release"/> = OnPortraitRowRelease (button, the list and the prisoner row under
/// the release point), Click = OnPortraitRowClick, DoubleClick = OnPortraitRowDoubleClick.</summary>
public readonly record struct ArenaRowAction<TId>(ArenaRowActionKind Kind, ArenaList List, TId Id, bool Right, ArenaDropTarget Target, TId Prisoner);

/// <summary>
/// The Arena window's row press / drag / release state (task 4.5b, window thread). A left press on a row (Meitou's Activated) starts it; any
/// pointer move while the button is held makes it a drag (MyGUI raises eventMouseDrag on every move of a pressed widget, OnPortraitRowDrag
/// :1221-1238); the left release gives the pressed row's Release with the drop list and prisoner row under the release point, then MyGUI's click
/// logic (MyGUI 3.2.2 MyGUI_InputManager.cpp injectMouseRelease :287-333): within <see cref="DoubleClickSeconds"/> of the last click, Click then
/// DoubleClick on the pressed row (wherever the pointer is, the timer not restarted); otherwise Click only when the pointer is still over the
/// pressed row, and the timer restarts. The timer is one for all widgets but expires whenever the widget under the pointer changes (:211-214,
/// "double clicks should only work when clicking on the *same* item twice"; the pressed widget keeps the focus while the button is held).
/// A right press on a row and its release give that row's Release with <c>Right</c> (the C++ removes an assigned fighter, :1259-1264): MyGUI
/// captures the pointer for every button (injectMousePress :238-241), so the release goes to the right-pressed row, not the row under it.
/// </summary>
public sealed class ArenaRowPointer<TId>
{
    /// <summary>MyGUI's double-click time: <c>INPUT_TIME_DOUBLE_CLICK = 0.25f</c> (MyGUI 3.2.2 MyGUI_InputManager.cpp:19).</summary>
    public const double DoubleClickSeconds = 0.25;

    (ArenaList List, int Index, TId Id, float X, float Y)? left;
    (ArenaList List, int Index, TId Id)? right;
    double? clickAt;   // mTimerDoubleClick: when the last click came; null = expired (INPUT_TIME_DOUBLE_CLICK or more)
    string? hover;     // mWidgetMouseFocus: the widget under the pointer

    /// <summary>A row is pressed with the left button and the pointer moved while it was held (C++ g_rowDragActive + g_dragCharacter).</summary>
    public bool DragActive { get; private set; }

    /// <summary>The fighter being dragged (valid while <see cref="DragActive"/>).</summary>
    public TId DragFighter => left is { } l ? l.Id : default!;

    /// <summary>The list the dragged row is in.</summary>
    public ArenaList DragList => left is { } l ? l.List : ArenaList.Roster;

    /// <summary>A left press on row <paramref name="index"/> of <paramref name="list"/>, which shows <paramref name="id"/>, at the point.</summary>
    public void LeftPress(ArenaList list, int index, TId id, float x, float y)
    {
        Hover(ArenaUINames.Row(list, index), held: false);   // injectMousePress moves the pointer there first (:230)
        left = (list, index, id, x, y);
        DragActive = false;
    }

    /// <summary>The pointer at (<paramref name="x"/>, <paramref name="y"/>) with the left button <paramref name="held"/>: a pressed row becomes a
    /// drag on its first move; the button up without a release seen (RefreshPointerHighlights :1206-1211: the drag ends) forgets the press.</summary>
    public void Move(float x, float y, bool held)
    {
        if (left is not { } l) return;
        if (!held)
        {
            left = null;
            DragActive = false;
            return;
        }
        if (x != l.X || y != l.Y) DragActive = true;
    }

    /// <summary>The widget under the pointer (null: none of this window's): a change expires the double-click timer (:211-214); not while a button
    /// is held (the pressed widget keeps the mouse focus during a capture, injectMouseMove :99-117).</summary>
    public void Hover(string? widget, bool held)
    {
        if (held || string.Equals(widget, hover, StringComparison.Ordinal)) return;
        hover = widget;
        clickAt = null;
    }

    /// <summary>The left button released at a point over row <paramref name="under"/> (null: not over a row of this window), list
    /// <paramref name="target"/> and prisoner row <paramref name="prisoner"/> (none: <paramref name="none"/>), <paramref name="now"/> seconds.
    /// Nothing when no row was pressed.</summary>
    public IReadOnlyList<ArenaRowAction<TId>> LeftRelease((ArenaList List, int Index)? under, ArenaDropTarget target, TId prisoner, TId none, double now)
    {
        if (left is not { } l) return [];
        left = null;
        DragActive = false;
        var actions = new List<ArenaRowAction<TId>>(3) { new(ArenaRowActionKind.Release, l.List, l.Id, false, target, prisoner) };
        if (clickAt is { } t && now - t < DoubleClickSeconds)
        {
            actions.Add(new(ArenaRowActionKind.Click, l.List, l.Id, false, ArenaDropTarget.None, none));
            actions.Add(new(ArenaRowActionKind.DoubleClick, l.List, l.Id, false, ArenaDropTarget.None, none));
            return actions;   // MyGUI leaves the timer running: a third quick click is a double click again
        }
        if (under is { } u && u.List == l.List && u.Index == l.Index)   // the click only over the pressed widget
            actions.Add(new(ArenaRowActionKind.Click, l.List, l.Id, false, ArenaDropTarget.None, none));
        clickAt = now;
        return actions;
    }

    /// <summary>A right press on a row.</summary>
    public void RightPress(ArenaList list, int index, TId id) => right = (list, index, id);

    /// <summary>The right button released: the right-pressed row's Release (Right), or nothing.</summary>
    public ArenaRowAction<TId>? RightRelease(ArenaDropTarget target, TId prisoner)
    {
        if (right is not { } r) return null;
        right = null;
        return new ArenaRowAction<TId>(ArenaRowActionKind.Release, r.List, r.Id, true, target, prisoner);
    }

    /// <summary>A new layout or a closed window: every press is forgotten (the C++ DestroyPanelWidgets clears g_dragCharacter / g_rowDragActive).</summary>
    public void Reset()
    {
        left = null;
        right = null;
        clickAt = null;
        hover = null;
        DragActive = false;
    }
}

/// <summary>What the window shows while a row is dragged (C++ ApplyDragHighlights :1171-1191 after ResetDropZoneVisuals and RefreshRowStates).</summary>
/// <param name="TeamALabel">The heading captions (the model's, plus a suffix on the one under the pointer).</param>
/// <param name="PrisonerSelected">The selected look of the assigned prisoner row under the pointer (handler drop), when there is one.</param>
/// <param name="Status">The status line's text over a prisoner row; null: the model's.</param>
public readonly record struct ArenaDragLook(string TeamALabel, string TeamBLabel, string PoolLabel, bool PrisonerSelected, string? Status);

/// <summary>C++ ApplyDragHighlights (:1171-1191) over the view's per-row flags: <c>valid</c> = the dragged row's <see cref="ArenaRowView{TId}.DragValid"/>
/// (IsAssignable, no match, no pending start, no prisoner return), the handler test = its <see cref="ArenaRowView{TId}.HandlerCandidate"/>.</summary>
public static class ArenaDragHighlight
{
    /// <param name="dragValid">The dragged row's DragValid.</param>
    /// <param name="handlerCandidate">The dragged row's HandlerCandidate.</param>
    /// <param name="overPrisonerRow">An assigned prisoner row (team / pool row of a roster prisoner) is under the pointer.</param>
    /// <param name="heading">The visible drop list under the pointer (team A / B in team mode, the pool otherwise; None: none).</param>
    public static ArenaDragLook Look(bool dragValid, bool handlerCandidate, bool overPrisonerRow, ArenaDropTarget heading, bool teamMode,
        string teamA, string teamB, string pool)
    {
        if (overPrisonerRow)
        {
            bool handler = dragValid && handlerCandidate;
            return new(teamA, teamB, pool, handler, handler ? ArenaUiTexts.DragHandlerOk : ArenaUiTexts.DragHandlerNo);
        }
        string suffix = dragValid ? ArenaUiTexts.HeadingDropOk : ArenaUiTexts.HeadingDropNo;
        if (teamMode && heading == ArenaDropTarget.TeamA) teamA += suffix;
        else if (teamMode && heading == ArenaDropTarget.TeamB) teamB += suffix;
        else if (!teamMode && heading == ArenaDropTarget.Pool) pool += suffix;
        return new(teamA, teamB, pool, false, null);
    }
}

/// <summary>
/// MyGUI's tooltip timing for one widget (MyGUI 3.2.2 MyGUI_ToolTipManager.cpp notifyEventFrameStart :60-135; task 4.5b, the Arena window's
/// settings tip): entering the widget starts the wait at the pointer's point; while the tip is hidden, a move restarts the wait and the tip shows
/// once the pointer rested <see cref="DelaySeconds"/> (mDelayVisible, :20); a held button (a capture) hides it without touching the wait, so after
/// the release with the pointer where it was the tip shows again at once; leaving the widget hides it.
/// </summary>
public sealed class TooltipTimer
{
    /// <summary>MyGUI's mDelayVisible (MyGUI_ToolTipManager.cpp:20, :35).</summary>
    public const double DelaySeconds = 0.5;

    bool inside, visible;
    double waited, last;
    float px, py;

    /// <summary>One frame at <paramref name="now"/> seconds: whether the tip shows.</summary>
    public bool Update(bool over, bool held, float x, float y, double now)
    {
        double dt = inside ? Math.Max(0, now - last) : 0;
        last = now;
        if (!over)   // the mouse focus left the widget (:63-79)
        {
            inside = false;
            visible = false;
            return false;
        }
        if (!inside)   // entered: the wait starts here (:72-77)
        {
            inside = true;
            visible = false;
            waited = 0;
            (px, py) = (x, y);
            return false;
        }
        if (held)   // a capture hides the tip; the wait and the point are left as they are (:83-91)
        {
            visible = false;
            return false;
        }
        if (!visible && (x != px || y != py))   // a move before it shows restarts the wait (:95-106)
        {
            waited = 0;
            (px, py) = (x, y);
            return false;
        }
        if (!visible)
        {
            waited += dt;
            if (waited >= DelaySeconds) visible = true;   // :122-128
        }
        // shown: it follows the pointer (moveToolTip, :130-133) without moving the rest point (mOldMousePoint stays): after a capture the tip
        // comes back at once only where the pointer rested
        return visible;
    }

    /// <summary>Hidden and forgotten (a new world).</summary>
    public void Reset()
    {
        inside = false;
        visible = false;
        waited = 0;
    }
}
