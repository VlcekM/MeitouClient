namespace ProvingGrounds.Core.Glue;

/// <summary>
/// The pointer buttons the window framework believes are down (src/Ui/ScreenHost.cs, task 4.5b review): a press is recorded by the
/// PlayerInterface.MouseDown postfix and forgotten by the MouseUp postfix. Meitou does not always call MouseUp for a press it called MouseDown for
/// (GameLoop.PointerUp skips it while a loading screen covers the game, during a cinema or settings-panel drag and while the settings panel is
/// shown; a lost window focus releases its InputState, which never reaches the interface), so each frame the framework also asks the operating
/// system whether any mouse button is physically down (the original polls GetAsyncKeyState(VK_LBUTTON), ArenaUI.cpp:1205) and
/// <see cref="Reconcile"/> forgets the presses the system says ended.
/// <para>A whole DoEvents after the mark: Meitou hands the window's mouse events to the interface only in <c>DoEvents</c> near the start of each
/// loop pass (GameLoop.cs:1235), while the system's state goes up at the physical release. A press is only marked on the first "up" (in pass
/// <c>m</c>; <c>pass</c>: a number the caller bumps once per loop pass, before its DoEvents) and forgotten when pass <c>m + 2</c> or later still
/// says up: the mark may have been taken inside pass m's own DoEvents (an event handler arranging the interface) with the MouseUp queued behind
/// it, so only pass m + 1's DoEvents is sure to have run after the mark; a real MouseUp has arrived by then. A paused game with a still pointer
/// may run passes more slowly: the press is then forgotten later (safe).</para>
/// <para>Only a press the system saw down when it was made is reconciled: the agent channel's click and the TEST ONLY <c>pg window drag</c> call
/// the game's own MouseDown / MouseUp without a physical button (the system says up throughout), and such a press must live until its own
/// release. So is a fast tap whose physical button was already up when its press was dispatched (the system said up at the press): it also waits
/// for its own release, or for <see cref="Clear"/>. The buttons are the engine's <c>MouseButton</c> values as integers (left 0, right 1, ...).
/// Window thread only.</para>
/// </summary>
public sealed class PointerButtons
{
    const int Count = 8;
    readonly bool[] down = new bool[Count];
    readonly bool[] physical = new bool[Count];   // the system saw a button down when the press came
    readonly long?[] upSince = new long?[Count];  // the pass the system first said "up" for a physical press (a suspect), else null

    /// <summary>Button <paramref name="button"/> went down; <paramref name="systemDown"/>: the operating system reported a mouse button down at that
    /// moment (null: it cannot be asked).</summary>
    public void Press(int button, bool? systemDown)
    {
        if ((uint)button >= Count) return;
        down[button] = true;
        physical[button] = systemDown == true;
        upSince[button] = null;
    }

    /// <summary>Button <paramref name="button"/> came up (the interface's release).</summary>
    public void Release(int button)
    {
        if ((uint)button >= Count) return;
        down[button] = false;
        physical[button] = false;
        upSince[button] = null;
    }

    /// <summary>The operating system's word on the buttons in loop pass <paramref name="pass"/> (<paramref name="systemAnyDown"/>: any mouse button
    /// physically down; null: cannot be asked). When none is down, a press it had seen down is marked; one marked two or more passes earlier is
    /// forgotten. A button down again clears the marks. True when a press was forgotten.</summary>
    public bool Reconcile(bool? systemAnyDown, long pass)
    {
        bool changed = false;
        for (int i = 0; i < Count; i++)
        {
            if (!down[i] || !physical[i]) continue;
            if (systemAnyDown != false) { upSince[i] = null; continue; }
            if (upSince[i] is not { } first) { upSince[i] = pass; continue; }
            if (pass - first < 2) continue;   // no whole DoEvents since the mark yet: its MouseUp may still be queued
            down[i] = false;
            physical[i] = false;
            upSince[i] = null;
            changed = true;
        }
        return changed;
    }

    /// <summary>Every press forgotten (a new interface or a new world).</summary>
    public void Clear()
    {
        Array.Clear(down);
        Array.Clear(physical);
        Array.Clear(upSince);
    }

    /// <summary>Whether button <paramref name="button"/> is believed down.</summary>
    public bool IsDown(int button) => (uint)button < Count && down[button];

    /// <summary>Whether any button is believed down (MyGUI captures the pointer while one is).</summary>
    public bool Any
    {
        get
        {
            foreach (var d in down) if (d) return true;
            return false;
        }
    }
}
