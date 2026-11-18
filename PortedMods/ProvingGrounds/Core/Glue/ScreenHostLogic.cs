namespace ProvingGrounds.Core.Glue;

/// <summary>
/// The shape key each Proving Grounds screen is registered with on the current <c>MyGuiScreenHost</c> (task 4.4, spike S6): each screen is
/// <c>Register</c>ed once on each host (a second Register of the same id throws, MyGuiScreenHost.cs:188-189; <c>PlayerInterface.LoadUi</c> makes a new
/// host each time, PlayerInterface.Ui.cs:166), then <c>Replace</c>d only when its shape key changes (a different number of rows or cards): Replace
/// keeps the window open, at its offset, with its texts and focus (VERIFIED live, S6). A key is committed only after its Register / Replace
/// succeeded. Window thread only.
/// </summary>
public sealed class ScreenShapes
{
    readonly Dictionary<string, string> keys = new(StringComparer.Ordinal);

    /// <summary>The shape key registered for <paramref name="id"/> on the current host; null before its registration.</summary>
    public string? Current(string id) => keys.GetValueOrDefault(id);

    /// <summary>Whether <paramref name="id"/>'s registered layout has shape <paramref name="key"/>.</summary>
    public bool Matches(string id, string key) => keys.TryGetValue(id, out var k) && string.Equals(k, key, StringComparison.Ordinal);

    /// <summary>Records a successful Register / Replace with <paramref name="key"/>.</summary>
    public void Commit(string id, string key) => keys[id] = key;

    /// <summary>A new screen host: every screen is registered again.</summary>
    public void Reset() => keys.Clear();
}

/// <summary>
/// The open / close requests the simulation publishes for a window (task 4.4): the sim half bumps a serial and says open or close; the window
/// side acts once per new serial (the newest wins when two came between frames). Each world's system starts at serial 0, so the window side
/// starts again from 0 when the publishing system (its world) changes; a new screen host (<see cref="Reset"/>) applies the latest request again
/// (a window open before a UI reload opens again). Window thread only.
/// </summary>
public sealed class ScreenRequests
{
    public enum Act { None, Open, Close }

    readonly Dictionary<string, (object? Owner, int Serial)> seen = new(StringComparer.Ordinal);

    /// <summary>Screen <paramref name="id"/>'s request as <paramref name="owner"/> (the publishing system) published it.</summary>
    public Act Observe(string id, object? owner, int serial, bool open)
    {
        var (lastOwner, lastSerial) = seen.GetValueOrDefault(id);
        if (!ReferenceEquals(lastOwner, owner)) lastSerial = 0;
        seen[id] = (owner, serial);
        if (serial == lastSerial) return Act.None;
        return open ? Act.Open : Act.Close;
    }

    /// <summary>A new screen host: the next <see cref="Observe"/> applies the latest request again.</summary>
    public void Reset() => seen.Clear();
}

/// <summary>One screen as <see cref="ScreenFrame.Run"/> sees it this frame: its id, its shape key for the published model, whether a model
/// exists and the model's open / close request.</summary>
public readonly record struct ScreenState(string Id, string Key, bool HasModel, int RequestSerial, bool RequestOpen);

/// <summary>What <see cref="ScreenFrame"/> does to the screen host (the glue implements it over <c>MyGuiScreenHost</c>; tests fake it).</summary>
public interface IScreenSurface
{
    bool IsRegistered(string id);
    bool IsOpen(string id);
    /// <summary>Builds the layout for the screen's current model and registers it (non-modal); false (logged) when the build or Register failed.</summary>
    bool TryRegister(string id);
    /// <summary>Builds the layout for the screen's current model and Replaces the registered one; false (logged) on failure.</summary>
    bool TryReplace(string id);
    void Open(string id);
    void Close(string id);
    /// <summary>The window closed without the sim half asking (Escape, CloseAll, its close button, a failed layout): tell the sim half, with the
    /// request serial of the model it closed (the sim ignores a close for an older or newer serial).</summary>
    void ReportClosed(string id, int serial);
}

/// <summary>
/// The window framework's per-frame decisions (task 4.4), pure over <see cref="IScreenSurface"/>:
/// <list type="number">
/// <item>A new world (the publishing system changed) closes every open screen of ours (C++ AbandonWorldState hid them, ArenaUI.h:13).</item>
/// <item>An unregistered screen is registered; a registered one whose shape key changed is Replaced. A key is committed only after success; a
/// failed key is remembered and tried again only when the key changes or a NEW open request comes (no rebuild every frame; task 6.4b review:
/// every C++ window creates its widgets again at its next show when the last creation failed: ArenaUI ShowWindow :2927-2929, ResultsUI :565,
/// LeaderboardUI :656, DebugMenu Toggle :644, RewardsUI :1587, PrisonerRecruitmentUI Show :593); a window whose new layout failed is closed (an
/// old layout is never bound to a model of another shape: <see cref="Bindable"/> is false).</item>
/// <item>A screen that was open and is now closed without the framework closing it (MyGuiScreenHost.Escape :613, CloseAll :286, the window's own
/// close button) is reported to the sim half (<see cref="IScreenSurface.ReportClosed"/>: a <see cref="UiEventKind.Closed"/> click), so its model
/// no longer says open.</item>
/// <item>The sim half's open / close request is applied once per serial (<see cref="ScreenRequests"/>).</item>
/// </list>
/// <see cref="NewHost"/> (a UI reload) forgets the registrations and requests, so every screen is registered again and the latest request
/// (a window that was open) is applied again. Window thread only.
/// </summary>
public sealed class ScreenFrame
{
    readonly ScreenShapes shapes = new();
    readonly ScreenRequests requests = new();
    readonly Dictionary<string, bool> wasOpen = new(StringComparer.Ordinal);
    readonly Dictionary<string, (string Key, int Serial)> failedKey = new(StringComparer.Ordinal);   // the key that failed and the request serial then
    readonly Dictionary<string, int> closedSerial = new(StringComparer.Ordinal);   // reported closes the sim half has not acknowledged yet
    readonly Dictionary<string, object?> bound = new(StringComparer.Ordinal);   // the model each screen was last bound with (task 6.4b review)
    object? owner;

    /// <summary>The shape key <paramref name="id"/> is registered with; null before.</summary>
    public string? Current(string id) => shapes.Current(id);

    /// <summary>Whether the registered layout of <paramref name="id"/> has the shape of the current model (bind it); false while its layout
    /// failed to build.</summary>
    public bool Bindable(string id, string key) => shapes.Matches(id, key);

    /// <summary>A new screen host (LoadUi): register everything again and apply the latest requests again (except an open request whose window
    /// was reported closed and the sim half has not acknowledged yet: it stays closed).</summary>
    public void NewHost()
    {
        shapes.Reset();
        requests.Reset();
        wasOpen.Clear();
        failedKey.Clear();
        bound.Clear();
    }

    /// <summary>Records that <paramref name="id"/> was just bound (drawn) with <paramref name="model"/> (task 6.4b review).</summary>
    public void Bound(string id, object? model) => bound[id] = model;

    /// <summary>The model a click on <paramref name="id"/> is judged against: the one the window was last bound with, i.e. what the player saw
    /// (task 6.4b review: the newest published model may already differ: another prisoner at that row, another price); <paramref name="newest"/>
    /// only before the first bind on this host and in this world. The sim halves re-check what a command names against their state now.</summary>
    public object? ClickModel(string id, object? newest) => bound.TryGetValue(id, out var m) ? m : newest;

    /// <summary>One frame for every screen, with <paramref name="system"/> the publishing system (null: no world).</summary>
    public void Run(IScreenSurface surface, object? system, IReadOnlyList<ScreenState> screens)
    {
        bool newWorld = !ReferenceEquals(owner, system);
        owner = system;
        if (newWorld) { closedSerial.Clear(); bound.Clear(); failedKey.Clear(); }   // a new world restarts the request serials (6.4b re-review)
        for (int i = 0; i < screens.Count; i++)
        {
            var st = screens[i];
            string id = st.Id;
            // a reported close is acknowledged once the model no longer says open for that serial (or a newer request came)
            if (closedSerial.TryGetValue(id, out int reported) && (!st.HasModel || !st.RequestOpen || st.RequestSerial != reported)) closedSerial.Remove(id);

            // a failed key stays failed until the key changes or a new open request comes (the C++ creates the window again at its next show)
            bool failed = failedKey.TryGetValue(id, out var bad) && string.Equals(bad.Key, st.Key, StringComparison.Ordinal)
                && !(st.HasModel && st.RequestOpen && st.RequestSerial != bad.Serial);
            if (!surface.IsRegistered(id))
            {
                if (failed) continue;
                if (!surface.TryRegister(id))
                {
                    failedKey[id] = (st.Key, st.RequestSerial);
                    // the sim half wants it open but there is no window: it hears it closed (task 6.4b review; the C++ Show found no g_window)
                    if (st.HasModel && st.RequestOpen) Report(surface, st);
                    continue;
                }
                shapes.Commit(id, st.Key);
                failedKey.Remove(id);
            }
            else if (!shapes.Matches(id, st.Key) && !failed)
            {
                if (surface.TryReplace(id))
                {
                    shapes.Commit(id, st.Key);
                    failedKey.Remove(id);
                }
                else
                {
                    failedKey[id] = (st.Key, st.RequestSerial);
                    if (surface.IsOpen(id))   // never an old layout bound to a model of another shape; the sim half hears the window closed
                    {
                        surface.Close(id);
                        Report(surface, st);
                    }
                    else if (st.HasModel && st.RequestOpen) Report(surface, st);   // asked open, never shown (task 6.4b review)
                    wasOpen[id] = false;
                    continue;
                }
            }
            else if (failed) continue;

            bool open = surface.IsOpen(id);
            if (newWorld)
            {
                if (open) surface.Close(id);
                open = false;
            }
            else if (wasOpen.GetValueOrDefault(id) && !open) Report(surface, st);

            if (st.HasModel)
            {
                switch (requests.Observe(id, system, st.RequestSerial, st.RequestOpen))
                {
                    case ScreenRequests.Act.Open when !(closedSerial.TryGetValue(id, out int c) && c == st.RequestSerial):
                        surface.Open(id);
                        open = true;
                        break;
                    case ScreenRequests.Act.Close:
                        if (open) surface.Close(id);
                        open = false;
                        break;
                }
            }
            wasOpen[id] = open;
        }
    }

    void Report(IScreenSurface surface, ScreenState st)
    {
        surface.ReportClosed(st.Id, st.RequestSerial);
        if (st.HasModel && st.RequestOpen) closedSerial[st.Id] = st.RequestSerial;
    }
}

/// <summary>
/// The portrait ImageRect strings and bound property sets cached per atlas cell (task 4.4), valid for one portrait size: the game's
/// <c>PortraitBar.PortraitRect(cell)</c> includes <c>PortraitSize</c> (PortraitBar.cs:80-85), which the host resets every frame from the look
/// (128 faithful / 256 enhanced, GameHost.cs:1473-1479, MetalGameLoop.cs:2581) without a new interface, so a size change empties the cache.
/// Window thread only.
/// </summary>
public sealed class PortraitRectCache<TValue> where TValue : class
{
    readonly Dictionary<(string Widget, int Cell), TValue> values = [];
    int size = -1;

    /// <summary>The cached value for (<paramref name="widget"/>, <paramref name="cell"/>) at portrait size <paramref name="portraitSize"/>; a
    /// different size than the cache holds empties it first.</summary>
    public bool TryGet(string widget, int cell, int portraitSize, out TValue value)
    {
        if (portraitSize != size)
        {
            values.Clear();
            size = portraitSize;
        }
        return values.TryGetValue((widget, cell), out value!);
    }

    /// <summary>Keeps <paramref name="value"/> for (<paramref name="widget"/>, <paramref name="cell"/>) at the size of the last
    /// <see cref="TryGet"/>.</summary>
    public void Set(string widget, int cell, TValue value) => values[(widget, cell)] = value;

    /// <summary>Empties the cache (a new interface).</summary>
    public void Clear()
    {
        values.Clear();
        size = -1;
    }

    public int Count => values.Count;
}

/// <summary>What a widget event of a faithful screen was (MyGuiEvent's action, MyGuiScreenHost.cs:9);
/// <see cref="Closed"/> is the framework's own: the window was closed from the window side (Escape, its close button) and the sim half must stop
/// saying open (<see cref="ScreenFrame"/>). <see cref="RightPress"/>, <see cref="Release"/> and <see cref="RightRelease"/> are the framework's
/// too (task 4.5b): MyGuiScreenHost raises nothing for them, the window side reads the game's own pointer calls
/// (<c>PlayerInterface.MouseDown</c> / <c>MouseUp</c>, PlayerInterface.cs:779, :1023) and hit-tests the screen (MyGuiScreenHost.HitTest).
/// <see cref="Press"/> is a button bound to eventMouseButtonPressed (or an Arena row, whose pointer copies MyGUI's click), sent on the left press;
/// <see cref="Click"/> a button bound to eventMouseButtonClick, sent at the left release over the button it was pressed on (task 4.4b,
/// <see cref="UiButtonClick"/>; or at once for Meitou's keyboard activation).</summary>
public enum UiEventKind { Press, Select, Accept, Text, Closed, RightPress, Release, RightRelease, Click }

/// <summary>A widget event of one of the mod's screens, as the window side sends it to the simulation (the <c>UiClick</c> command): the screen,
/// the widget, the kind, the list row's argument or the edit box's text, the row index, whether Ctrl was held, and for the pointer events the
/// point in screen pixels.</summary>
public readonly record struct UiClickData(string Screen, string Widget, UiEventKind Kind, string Text, int Value, bool Ctrl, float X = 0, float Y = 0);

/// <summary>
/// Maps a faithful screen event to a click of the mod (task 4.4; CAP 7.0: every UI callback runs on the window thread and becomes a command).
/// MyGuiScreenHost raises <c>Activated</c> for a button press or Enter on it (its bound action, MyGuiScreenHost.cs:506, :682,
/// MyGuiScreenHost.Keyboard.cs:266), a list line selected or accepted (MyGuiScreenHost.cs:536, :543; MyGuiScreenHost.Widgets.cs:517, :580,
/// :597), an edit box's text (MyGuiScreenHost.cs:645) and scroll bar steps (MyGuiScreenHost.Widgets.cs:258, :301, :387; not mapped) and never
/// for a right press (it only handles <c>PointerDown</c>; PlayerInterface.MouseDown sends right presses elsewhere, PlayerInterface.cs:779-800).
/// Right presses and the releases reach the screens as <see cref="UiEventKind.RightPress"/> / <see cref="UiEventKind.RightRelease"/> /
/// <see cref="UiEventKind.Release"/> through the window side's PlayerInterface.MouseDown / MouseUp postfixes (task 4.5b, docs/divergences.md
/// "Window framework"), not through this map; the Ctrl modifier read at a press is only a flag the test window logs.
/// </summary>
public static class UiClickPolicy
{
    /// <summary>The prefix of the actions the window side binds to its buttons.</summary>
    public const string ActionPrefix = "pg:";

    /// <summary>The action bound to button <paramref name="widget"/>.</summary>
    public static string ActionFor(string widget) => ActionPrefix + widget;

    /// <summary>The click of an event of screen <paramref name="screen"/>: a press of a button bound with <see cref="ActionFor"/>, a list select or
    /// accept, an edit box's text; null for another screen's event, a widget without a name or any other action (the chrome's <c>mygui:</c>,
    /// scroll bars).</summary>
    public static UiClickData? Map(IReadOnlySet<string> ours, string screen, string widget, string action, string text, float value, bool ctrl)
    {
        if (!ours.Contains(screen) || widget.Length == 0) return null;
        UiEventKind kind;
        if (action == "select") kind = UiEventKind.Select;
        else if (action == "accept") kind = UiEventKind.Accept;
        else if (action == "text") kind = UiEventKind.Text;
        else if (action.StartsWith(ActionPrefix, StringComparison.Ordinal)) kind = UiEventKind.Press;
        else return null;
        int row = float.IsFinite(value) ? (int)value : 0;
        return new UiClickData(screen, widget, kind, text, row, ctrl && kind == UiEventKind.Press);   // Ctrl matters on a press only
    }
}

/// <summary>
/// The character portrait cells the mod's open windows use (task 4.4, spike S7, CAP 7.4): the renderer's atlas has 32 cells; the squad bar (and the
/// squads screen) take the first ones, the conversation the last two while one is open, and the character trading window takes cells after the
/// squad bar too (TradeWindows.cs:594-610). The mod takes the free cells after those: a character keeps its cell while it stays wanted and the cell
/// stays free; a new one takes the lowest free cell in request order; a character no window wants (a closed window) gives its cell back; with no
/// cell left it gets none (the window shows the name only). Window thread only.
/// </summary>
public sealed class PortraitCells<TKey> where TKey : notnull
{
    Dictionary<TKey, int> cells = [], shown = [];
    Dictionary<TKey, int> nextCells = [], nextShown = [];   // scratch, swapped in when something changed (no allocation per frame)
    readonly List<TKey> order = [];
    readonly HashSet<int> otherCells = [], used = [];
    readonly Dictionary<TKey, int> otherOf = [];
    List<(TKey Key, int Cell)> pairs = [];

    /// <summary>The cells the mod allocated (its own: appended to the host's list, given back when no window wants them).</summary>
    public IReadOnlyDictionary<TKey, int> Cells => cells;

    /// <summary>Every wanted character's cell as the windows show it: the mod's own, or the cell another window (the character trading window)
    /// already gave that character (never allocated a second time, never given back by the mod).</summary>
    public IReadOnlyDictionary<TKey, int> Shown => shown;

    /// <summary>The character's shown cell, if it has one.</summary>
    public bool TryCell(TKey key, out int cell) => shown.TryGetValue(key, out cell);

    /// <summary>The mod's own (character, cell) pairs in request order: what to append to the host's list. Each character at most once, never one
    /// another window already lists (TradeWindows.cs:507 turns the list into a dictionary by character: a repeat would throw there). The same
    /// list object until an <see cref="Update"/> changes something.</summary>
    public IReadOnlyList<(TKey Key, int Cell)> Pairs() => pairs;

    /// <summary>Assigns cells for <paramref name="wanted"/> in [<paramref name="firstFree"/>, <paramref name="end"/>) around the cells
    /// <paramref name="others"/> (pairs another window already listed) hold; a wanted character <paramref name="others"/> already lists is shown in
    /// its cell and gets none of the mod's. True when the cells or the shown cells changed.</summary>
    public bool Update(IReadOnlyList<TKey> wanted, int firstFree, int end, IReadOnlyList<(TKey Key, int Cell)>? others = null)
    {
        int start = Math.Max(0, firstFree);
        otherCells.Clear();
        otherOf.Clear();
        if (others is not null)
            for (int i = 0; i < others.Count; i++)
            {
                otherCells.Add(others[i].Cell);
                otherOf.TryAdd(others[i].Key, others[i].Cell);
            }
        bool Usable(int c) => c >= start && c < end && !otherCells.Contains(c);

        nextCells.Clear();
        nextShown.Clear();
        used.Clear();
        order.Clear();
        for (int i = 0; i < wanted.Count; i++)
        {
            var key = wanted[i];
            if (order.Contains(key) || nextShown.ContainsKey(key)) continue;
            if (otherOf.TryGetValue(key, out int theirs)) { nextShown[key] = theirs; continue; }
            order.Add(key);
            if (cells.TryGetValue(key, out int cell) && Usable(cell) && used.Add(cell)) nextCells[key] = cell;
        }

        int next = start;
        foreach (var key in order)
        {
            if (nextCells.ContainsKey(key)) continue;
            while (next < end && (!Usable(next) || used.Contains(next))) next++;
            if (next >= end) break;
            nextCells[key] = next;
            used.Add(next);
        }
        foreach (var (k, c) in nextCells) nextShown[k] = c;

        bool changed = !Same(cells, nextCells) || !Same(shown, nextShown);
        if (!changed) return false;
        (cells, nextCells) = (nextCells, cells);
        (shown, nextShown) = (nextShown, shown);
        pairs = [.. order.Where(cells.ContainsKey).Select(k => (k, cells[k]))];
        return true;
    }

    static bool Same(Dictionary<TKey, int> a, Dictionary<TKey, int> b)
    {
        if (a.Count != b.Count) return false;
        foreach (var (k, v) in a) if (!b.TryGetValue(k, out int w) || w != v) return false;
        return true;
    }
}

/// <summary>Task 4.8: whether the portrait list Portraits.AfterTradePortraits built last frame can be returned again: the same input pairs (the
/// trading window's) and our pairs equal to the tail of the last output. Counts are compared before any index (a new character asked for makes
/// our pairs longer than the last output's tail).</summary>
public static class PortraitOutput
{
    public static bool SameAsLast<TKey>(IReadOnlyList<(TKey, int)> input, IReadOnlyList<(TKey, int)> lastInput,
        IReadOnlyList<(TKey, int)>? lastOutput, IReadOnlyList<(TKey, int)> mine)
    {
        if (lastOutput is null || input.Count != lastInput.Count || lastOutput.Count != input.Count + mine.Count) return false;
        var eq = EqualityComparer<(TKey, int)>.Default;
        for (int i = 0; i < input.Count; i++) if (!eq.Equals(input[i], lastInput[i])) return false;
        for (int i = 0; i < mine.Count; i++) if (!eq.Equals(lastOutput[input.Count + i], mine[i])) return false;
        return true;
    }
}
