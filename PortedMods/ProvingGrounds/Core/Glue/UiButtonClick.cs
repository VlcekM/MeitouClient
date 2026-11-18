namespace ProvingGrounds.Core.Glue;

// Task 4.4b: the original binds each button to one MyGUI event (the binding tables are in the windows: src/Ui/ArenaUI.cs, ResultsUI.cs,
// LeaderboardUI.cs), mostly eventMouseButtonClick, which MyGUI raises on a LEFT RELEASE (MyGUI 3.2.2 MyGUI_InputManager.cpp injectMouseRelease
// :283-333), and a few eventMouseButtonPressed, raised on the press (injectMousePress :218-281). Meitou's faithful screens activate every button on
// the press (MyGuiScreenHost.PointerDown -> Activated, MyGuiScreenHost.cs:504-508); the window framework (src/Ui/ScreenHost.cs) holds a Click
// button's press here and sends it at the release the PlayerInterface.MouseUp postfix sees.

/// <summary>The MyGUI event a screen's button is bound to in the original (task 4.4b).</summary>
public enum UiButtonEvent
{
    /// <summary><c>eventMouseButtonClick</c>: acts on a left release over the widget it was pressed on (the default).</summary>
    Click,
    /// <summary><c>eventMouseButtonPressed</c>: acts on the press.</summary>
    Press,
    /// <summary>The screen receives the press and copies MyGUI's release / click / double-click itself (the Arena window's rows,
    /// <see cref="ArenaRowPointer{TId}"/>): the framework sends the press as <see cref="Press"/> does.</summary>
    Pointer,
}

/// <summary>
/// MyGUI's click for the framework's <see cref="UiButtonEvent.Click"/> buttons (task 4.4b, window thread). A left press that activates a Click
/// button is held (nothing sent); the left release then clicks it when (MyGUI 3.2.2 MyGUI_InputManager.cpp):
/// <list type="bullet">
/// <item>the pressed widget is still shown and enabled (a widget hidden or disabled since the press is unlinked by MyGUI and loses the mouse focus
/// and capture: Widget::_updateVisible MyGUI_Widget.cpp:986-987, Widget::_updateEnabled :1013-1014, InputManager::_unlinkWidget :474-475; see
/// <see cref="Release"/>);</item>
/// <item>within <see cref="DoubleClickSeconds"/> of the last click: wherever the pointer is, and the timer is not restarted (:303-309);</item>
/// <item>otherwise only when the release is over the pressed widget (:312-316); the timer restarts either way (:317).</item>
/// </list>
/// The timer is MyGUI's one double-click timer and expires whenever the widget under the pointer changes, except while a button is held (the
/// pressed widget keeps the focus during a capture, injectMouseMove :99-117, :208-213). A right release does nothing to it (the click is the left
/// button's only). The double-click event that follows a quick second click is not modelled: no original button binds eventMouseButtonDoubleClick
/// (only the Arena rows, which have their own pointer). An activation that does not come from a left mouse press (Meitou's Enter / focused-button
/// activation) acts at once: no release follows it.
/// </summary>
public sealed class UiButtonClick
{
    /// <summary>MyGUI's double-click time: <c>INPUT_TIME_DOUBLE_CLICK = 0.25f</c> (MyGUI_InputManager.cpp:19).</summary>
    public const double DoubleClickSeconds = 0.25;

    (string Screen, string Widget)? pressed;
    double? clickAt;            // mTimerDoubleClick: when the last left release over a pressed widget came; null = expired
    (string, string)? hover;    // mWidgetMouseFocus (ours: screen and widget), null = none of ours

    /// <summary>A Click button's press is waiting for its release.</summary>
    public bool Waiting => pressed is not null;

    /// <summary>The double-click timer is still inside <see cref="DoubleClickSeconds"/> at <paramref name="now"/> (the framework follows the widget
    /// under the pointer only then: after that time the timer cannot shortcut a click, a change of widget no longer matters).</summary>
    public bool TimerLive(double now) => clickAt is { } t && now - t < DoubleClickSeconds;

    /// <summary>A left press begins (before Meitou handles it): a press still waiting from an earlier press whose release was never seen is
    /// forgotten (MyGUI's capture and focus belong to the new press now, injectMousePress :220-241); <see cref="Activated"/> sets the new one.</summary>
    public void BeginLeftPress() => pressed = null;

    /// <summary>The framework's per-frame check: with the left button no longer held (<paramref name="leftHeld"/>, PointerButtons after its
    /// Reconcile) and not inside a left press (<paramref name="leftPressing"/>: Meitou is still handling the press that activated the button), a
    /// waiting press is forgotten: its release was never passed on, so MyGUI never got one either. Only the waiting press: the double-click timer
    /// is kept (no release reached MyGUI, so its timer was not restarted or expired by one). True when a press was forgotten.</summary>
    public bool FrameCheck(bool leftHeld, bool leftPressing)
    {
        if (pressed is null || leftHeld || leftPressing) return false;
        pressed = null;
        return true;
    }

    /// <summary>Meitou activated button <paramref name="widget"/> of <paramref name="screen"/>, bound to <paramref name="kind"/>;
    /// <paramref name="fromLeftPress"/>: during a left PlayerInterface.MouseDown. True: send its command now; false: wait for the release.</summary>
    public bool Activated(string screen, string widget, UiButtonEvent kind, bool fromLeftPress, double now)
    {
        if (!fromLeftPress) return true;
        Hover(screen, widget, held: false);   // injectMousePress moves the pointer there first (:220)
        if (kind != UiButtonEvent.Click)
        {
            pressed = null;
            return true;
        }
        pressed = (screen, widget);
        return false;
    }

    /// <summary>A pointer release (<paramref name="left"/>: the left button) over <paramref name="under"/> (one of our screens' widgets, or null),
    /// <paramref name="now"/> seconds. <paramref name="shownAndEnabled"/> tells whether a widget is still shown and enabled. Answers the widget to
    /// click, or null.</summary>
    public (string Screen, string Widget)? Release(bool left, (string Screen, string Widget)? under, Func<string, string, bool> shownAndEnabled, double now)
    {
        if (!left || pressed is not { } p) return null;
        pressed = null;
        if (!shownAndEnabled(p.Screen, p.Widget))
        {
            // hidden or disabled since the press: MyGUI unlinks it either way (setVisible -> Widget::_updateVisible :986-987; setEnabled ->
            // Widget::_updateEnabled :1013-1014 -> InputManager::_unlinkWidget :474-475 -> _resetMouseFocusWidget :436-457: focus and capture
            // gone), so the release finds no focus; the next move picks another widget and the timer expires (:208-213). The early return for a
            // disabled focus widget in injectMouseRelease (:286-287) is reached only through setEnabledSilent, which the original never calls.
            clickAt = null;
            return null;
        }
        if (clickAt is { } t && now - t < DoubleClickSeconds) return p;
        clickAt = now;
        return under is { } u && u.Screen == p.Screen && u.Widget == p.Widget ? p : null;
    }

    /// <summary>The widget under the pointer (null: none of ours) with <paramref name="held"/> = any button down: a change expires the timer,
    /// not during a capture.</summary>
    public void Hover(string? screen, string? widget, bool held)
    {
        if (held) return;
        (string, string)? now = screen is null || widget is null ? null : (screen, widget);
        if (now == hover) return;
        hover = now;
        clickAt = null;
    }

    /// <summary>Screen <paramref name="id"/> is closed: its widget lost the focus, a waiting press is forgotten.</summary>
    public void ForgetScreen(string id)
    {
        if (pressed is { } p && p.Screen == id) pressed = null;
    }

    /// <summary>A new interface or world, or the left button up without a release seen: everything is forgotten.</summary>
    public void Forget()
    {
        pressed = null;
        clickAt = null;
        hover = null;
    }
}
