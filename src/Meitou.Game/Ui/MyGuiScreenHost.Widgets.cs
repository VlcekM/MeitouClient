using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Meitou.Data.Ui;

namespace Meitou.Game.Ui;

/// <summary>
/// A key a focused list (or combo box) handles: MyGUI ListBox / ComboBox <c>onKeyButtonPressed</c>.
/// </summary>
public enum UiListKey
{
    /// <summary>
    /// One row up (from no selection, the first).
    /// </summary>
    Up,

    /// <summary>
    /// One row down (from no selection, the first); a closed combo box opens.
    /// </summary>
    Down,

    /// <summary>
    /// A client's worth of rows up.
    /// </summary>
    PageUp,

    /// <summary>
    /// A client's worth of rows down.
    /// </summary>
    PageDown,

    /// <summary>
    /// The first row.
    /// </summary>
    Home,

    /// <summary>
    /// The last row.
    /// </summary>
    End,

    /// <summary>
    /// Return: accepts the selected row.
    /// </summary>
    Accept,
}

/// <summary>
/// Runtime of the scroll bars, sliders, lists, combo boxes and window geometry (docs/formats/mygui.md "Widget runtime"). The presentation lays the
/// widgets out from the state this pushes into the bindings and marks the controls (<c>Bar*</c>, <c>Scroll*</c>, <c>List*</c>, <c>Combo*</c>); the
/// pointer, wheel and key input below acts on those markers. Events: a free scroll bar or slider raises <c>scroll</c> (widget: the slider's or bar's
/// name, <c>Value</c>: its position, 0 to Range - 1), a list or combo item <c>select</c> (<c>Text</c>: the item id, <c>Value</c>: its row), Return on
/// a list <c>accept</c>.
/// </summary>
public sealed partial class MyGuiScreenHost
{
    /// <summary>
    /// The widget runtime's part of a screen's state: list sorts and the window geometry.
    /// </summary>
    sealed partial class Screen
    {
        /// <summary>
        /// The lists' sort column and direction by widget.
        /// </summary>
        public Dictionary<string, (int Column, bool Ascending)> Sorts { get; } = new(StringComparer.Ordinal);

        /// <summary>
        /// The viewport of the last arrangement and of the first (windows with Stretch alignment grow by the change from it).
        /// </summary>
        public Vector2 Viewport, FirstViewport;

        /// <summary>
        /// The first root's last placement (position, size), alignment, Snap and Movable.
        /// </summary>
        public Vector2 Placed, PlacedSize;

        /// <summary>
        /// The first root's alignment.
        /// </summary>
        public string Align = "";

        /// <summary>
        /// The first root's Snap (edges snap to the viewport's) and Movable.
        /// </summary>
        public bool Snap, Movable = true;

        /// <summary>
        /// The size a resize grip gave the window (null: the layout's).
        /// </summary>
        public Vector2? Size;

        /// <summary>
        /// Whether the player dragged the window (it may then leave the screen, <see cref="KeepVisible"/>); a placement by the host clears it.
        /// </summary>
        public bool Dragged;
    }

    /// <summary>
    /// A scroll bar part held down: a thumb drag, or an arrow / page part repeating its <paramref name="Delta"/> (MyGUI ControllerRepeatClick: after
    /// 0.5 s, then every 0.1 s).
    /// </summary>
    sealed record Grab(string Screen, string Bar, string Control, UiRect Place, float Thumb, bool Vertical, bool Thumbing, float Delta)
    {
        /// <summary>
        /// The thumb's grab point from its start (a thumb drag).
        /// </summary>
        public float Offset;

        /// <summary>
        /// When the held part steps next (Stopwatch).
        /// </summary>
        public long Next;
    }

    /// <summary>
    /// A legacy slider track (a decorating control with the property <c>Slider</c>): the drag raises its own action with the pointer's share of the
    /// track.
    /// </summary>
    sealed record LegacySlide(string Screen, string Widget, string Action, string Argument, UiRect Track);

    /// <summary>
    /// A resize grip held: the screen, where the press was, the window's rectangle then and the grip's four multipliers (left, top, width, height).
    /// </summary>
    sealed record WindowGrab(string Screen, Vector2 At, UiRect Start, Vector4 Scale);

    /// <summary>
    /// The scroll bar part held; null for none.
    /// </summary>
    Grab? _grab;

    /// <summary>
    /// The legacy slider being dragged; null for none.
    /// </summary>
    LegacySlide? _legacy;

    /// <summary>
    /// The resize grip held; null for none.
    /// </summary>
    WindowGrab? _windowGrab;

    /// <summary>
    /// The combo box whose list is open; null for none.
    /// </summary>
    (string Screen, string Combo)? _openCombo;

    /// <summary>
    /// The list or combo box that has the keyboard (the last one clicked): its screen and selection key.
    /// </summary>
    (string Screen, string Key)? _keyFocus;

    /// <summary>
    /// Each open screen's bindings of the last arrangement (the lists' items).
    /// </summary>
    readonly Dictionary<string, UiBindings> _lastBindings = new(StringComparer.Ordinal);

    /// <summary>
    /// The repeat delay and step, seconds, and the snap distance, pixels.
    /// </summary>
    const double RepeatDelay = 0.5, RepeatStep = 0.1, SnapDistance = 10;

    /// <summary>
    /// How much of a dragged window stays on screen: MyGUI lets a window go anywhere, Meitou keeps its top edge and this many pixels of it in view
    /// so it can be dragged back (#444).
    /// </summary>
    const float KeepVisible = 48;

    /// <summary>
    /// A scroll bar part, slider or resize grip is held.
    /// </summary>
    bool CapturesWidget => _grab is not null || _legacy is not null || _windowGrab is not null;

    /// <summary>
    /// Scroll offset <paramref name="key"/> of screen <paramref name="id"/>.
    /// </summary>
    public float GetScroll(string id, string key) => _screens[id].Scroll.GetValueOrDefault(key);

    /// <summary>
    /// Sets scroll offset <paramref name="key"/> of screen <paramref name="id"/>.
    /// </summary>
    public void SetScroll(string id, string key, float value) => _screens[id].Scroll[key] = value;

    /// <summary>
    /// Back to the top (a new content for the screen's lists and scroll views).
    /// </summary>
    public void ResetScroll(string id) => _screens[id].Scroll.Clear();

    /// <summary>
    /// Screen <paramref name="id"/>'s controls of the last arrangement; null when it was not laid out.
    /// </summary>
    IReadOnlyList<UiControl>? Controls(string id) => _frame.FirstOrDefault(f => f.Id == id)?.Controls;

    /// <summary>
    /// The last control named <paramref name="name"/> in screen <paramref name="id"/>.
    /// </summary>
    UiControl? Find(string id, string name) => Controls(id)?.LastOrDefault(c => c.Name == name);

    /// <summary>
    /// Number property <paramref name="key"/> of <paramref name="c"/>.
    /// </summary>
    static float Num(UiControl c, string key, float fallback = 0) => MyGuiPresentation.Num(c.Properties, key, fallback);

    /// <summary>
    /// Pushes the sorts, the open combo and the held part's pushed state into the bindings, and keeps the bindings.
    /// </summary>
    void PushWidgetState(string id, Screen s, UiBindings b)
    {
        foreach (var t in s.Sorts) b.Sorts[t.Key] = t.Value;
        if (_openCombo is { } popup && popup.Screen == id) b.OpenCombo = popup.Combo;
        if (_grab is { } g && g.Screen == id) b.States[g.Control] = "pushed";

        _lastBindings[id] = b;
    }

    /// <summary>
    /// Lets go the held parts, the open combo, the keyboard list and the bindings of screen <paramref name="id"/>.
    /// </summary>
    void ForgetWidgets(string id)
    {
        if (_grab?.Screen == id) _grab = null;
        if (_legacy?.Screen == id) _legacy = null;
        if (_windowGrab?.Screen == id) _windowGrab = null;
        if (_openCombo?.Screen == id) _openCombo = null;
        if (_keyFocus?.Screen == id) _keyFocus = null;

        _lastBindings.Remove(id);
    }

    // ---- pointer

    /// <summary>
    /// A press on a control of <paramref name="id"/>: the scroll bar parts, combo box, header buttons, slider tracks and window grips; false: not a
    /// widget action (the screen goes on).
    /// </summary>
    bool PressWidget(string id, UiControl c, Vector2 p)
    {
        PressOutside(c);
        var s = _screens[id];
        var parts = c.Action.Split(':');

        if (parts.Length >= 4 && parts[0] == "mygui")
        {
            switch (parts[1])
            {
                case "list": _keyFocus = (id, parts[2]); return false;
                case "bar": BarPress(id, c, parts[2], parts[3], p); return true;
                case "combo": ComboPress(id, parts[2], parts[3]); return true;
                case "sort":
                    if (_lastBindings.TryGetValue(id, out var b))
                    {
                        bool same = b.Sorts.TryGetValue(parts[2], out var now) && now.Column == int.Parse(parts[3], CultureInfo.InvariantCulture);
                        s.Sorts[parts[2]] = same ? (now.Column, !now.Ascending) : (int.Parse(parts[3], CultureInfo.InvariantCulture), true);
                    }

                    _keyFocus = (id, parts[2]);
                    return true;
                case "step":
                    Activated?.Invoke(new(id, parts[2], "step", parts[3], parts[3] == "start" ? -1 : 1));
                    return true;
            }
        }
        else _keyFocus = null;

        if (c.Properties.ContainsKey("WindowAction") || c.Properties.ContainsKey("WindowScale"))
            return BeginWindowAction(id, s, c, p);

        if (c.Properties.GetValueOrDefault("Slider") == "true")
        {
            Blur();
            _legacy = new(id, c.Name, c.Action, c.Argument, c.Bounds);
            Slide(p);
            return true;
        }

        return false;
    }

    /// <summary>
    /// A press that is not on a combo box's popup closes the popup (MyGUI: the list loses key focus); null: outside every window.
    /// </summary>
    void PressOutside(UiControl? c)
    {
        if (_openCombo is { } popup
            && (c is null || !(c.Name.StartsWith(popup.Combo + "@List#", StringComparison.Ordinal)
                || c.Action.StartsWith($"mygui:combo:{popup.Combo}:", StringComparison.Ordinal))))
        {
            _openCombo = null;
        }

        if (c is null) _keyFocus = null;
    }

    /// <summary>
    /// Raises the legacy slider's action with the pointer's share of its track.
    /// </summary>
    void Slide(Vector2 p)
    {
        if (_legacy is not { } s) return;

        float t = s.Track.Width > 0 ? Math.Clamp((p.X - s.Track.X) / s.Track.Width, 0, 1) : 0;
        Activated?.Invoke(new(s.Screen, s.Widget, s.Action, s.Argument, t));
    }

    /// <summary>
    /// A press on a scroll bar part (Verified against MyGUI 3.2.2 ScrollBar.cpp): Start / End step by <c>Page</c>, the track parts by <c>ViewPage</c>
    /// (with <c>MoveToClick</c> the thumb jumps to the click and follows the pointer), the thumb drags; the parts but the thumb repeat while held.
    /// </summary>
    void BarPress(string id, UiControl pressed, string bar, string part, Vector2 p)
    {
        if (Find(id, bar) is not { } root) return;

        static bool IsTemplatePart(UiControl c, string owner, string part) =>
            c.Properties.GetValueOrDefault("TemplateOwner") == owner && c.Properties.GetValueOrDefault("TemplatePart") == part;

        var pr = root.Properties;
        bool vertical = pr.GetValueOrDefault("BarVertical") != "false", repeat = pr.GetValueOrDefault("BarRepeat") != "false";
        float page = Num(root, "BarPage", 1), viewPage = Num(root, "BarViewPage", 1);
        var place = Controls(id)!.LastOrDefault(c => IsTemplatePart(c, bar, "TrackPlace"))?.Bounds ?? default;
        var track = Controls(id)!.LastOrDefault(c => IsTemplatePart(c, bar, "Track"))?.Bounds ?? default;
        float thumb = vertical ? track.Height : track.Width;
        float coord = vertical ? p.Y : p.X;
        _keyFocus = null;

        if (part == "track")
        {
            _grab = new(id, bar, pressed.Name, place, thumb, vertical, true, 0) { Offset = coord - (vertical ? track.Y : track.X) };
            return;
        }

        if (part is "first" or "second" && pr.GetValueOrDefault("BarMove") == "true")
        {
            if (thumb <= 0) return;

            _grab = new(id, bar, pressed.Name, place, thumb, vertical, true, 0) { Offset = thumb / 2 };
            DragThumb(_grab, p);
            return;
        }

        float delta = part switch { "start" => -page, "end" => page, "first" => -viewPage, _ => viewPage };
        StepBar(id, root, delta);
        _grab = new(id, bar, pressed.Name, place, thumb, vertical, false, repeat ? delta : 0)
        {
            Next = Stopwatch.GetTimestamp() + (long)(RepeatDelay * Stopwatch.Frequency),
        };
    }

    /// <summary>
    /// Moves the dragged thumb to the pointer: the bar's position by its share of the travel, whole steps.
    /// </summary>
    void DragThumb(Grab g, Vector2 p)
    {
        if (Find(g.Screen, g.Bar) is not { } root) return;

        float max = Num(root, "BarMax"), origin = g.Vertical ? g.Place.Y : g.Place.X, travel = (g.Vertical ? g.Place.Height : g.Place.Width) - g.Thumb;
        if (travel <= 0 || max <= 0) return;

        float start = Math.Clamp((g.Vertical ? p.Y : p.X) - g.Offset - origin, 0, travel);
        SetBar(g.Screen, root, MathF.Round(start * max / travel));
    }

    /// <summary>
    /// A scroll bar's position: the stored one, else its layout's, clamped to its maximum.
    /// </summary>
    float BarPosition(string id, UiControl root)
    {
        float position = _screens[id].Scroll.TryGetValue(root.Properties.GetValueOrDefault("BarKey") ?? "", out float stored)
            ? stored
            : Num(root, "BarPos");
        return Math.Clamp(position, 0, Num(root, "BarMax"));
    }

    /// <summary>
    /// Steps a scroll bar by <paramref name="delta"/>.
    /// </summary>
    void StepBar(string id, UiControl root, float delta) => SetBar(id, root, BarPosition(id, root) + delta);

    /// <summary>
    /// Moves a scroll bar to <paramref name="value"/> (clamped): the position is the host's; a bar no widget drives raises <c>scroll</c>.
    /// </summary>
    void SetBar(string id, UiControl root, float value)
    {
        float v = Math.Clamp(value, 0, Num(root, "BarMax"));
        if (v == BarPosition(id, root)) return;

        _screens[id].Scroll[root.Properties["BarKey"]] = v;
        if (root.Properties.GetValueOrDefault("BarOwned") != "true")
            Activated?.Invoke(new(id, root.Properties.GetValueOrDefault("BarName") ?? root.Name, "scroll", "", v));
    }

    /// <summary>
    /// Held arrows and track parts repeat on real time (the arrangement runs every frame).
    /// </summary>
    void RepeatTick()
    {
        if (_grab is not { } g || g.Delta == 0 || Stopwatch.GetTimestamp() < g.Next) return;
        if (Find(g.Screen, g.Bar) is { } root) StepBar(g.Screen, root, g.Delta);

        g.Next = Stopwatch.GetTimestamp() + (long)(RepeatStep * Stopwatch.Frequency);
    }

    /// <summary>
    /// A pointer move for a held part: the thumb drag, the legacy slider or the resize; false when none is held.
    /// </summary>
    bool MoveWidget(Vector2 p)
    {
        if (_grab is { Thumbing: true } g)
        {
            DragThumb(g, p);
            return true;
        }

        if (_grab is not null) return true;

        if (_legacy is not null)
        {
            Slide(p);
            return true;
        }

        if (_windowGrab is not null)
        {
            ResizeWindow(p);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Lets go the held part, slider and grip.
    /// </summary>
    void ReleaseWidget()
    {
        _grab = null;
        _legacy = null;
        _windowGrab = null;
    }

    // ---- wheel

    /// <summary>
    /// The wheel over <paramref name="p"/>: the innermost scroll bar, list or scroll view under it scrolls (a list one line, a scroll view 50 px,
    /// vertical first; a bar its <c>WheelPage</c>), a focused closed combo box changes its selection (MyGUI ComboBox.cpp, ListBox.cpp,
    /// ScrollView.cpp, ScrollBar.cpp: one step per wheel event, whatever its size).
    /// </summary>
    void WheelWidget(string id, Vector2 p, float steps)
    {
        if (Controls(id) is not { } controls || steps == 0) return;

        float sign = steps > 0 ? -1 : 1;
        var s = _screens[id];

        for (int i = controls.Count - 1; i >= 0; i--)
        {
            var c = controls[i];
            if (!c.Bounds.Contains(p.X, p.Y) || c.Clip is { } clip && !clip.Contains(p.X, p.Y)) continue;

            var pr = c.Properties;

            if (pr.ContainsKey("BarKey") && pr.ContainsKey("BarMax"))
            {
                if (Num(c, "BarMax") > 0)
                {
                    StepBar(id, c, sign * Num(c, "BarWheel", 1));
                    return;
                }

                continue;
            }

            if (pr.ContainsKey("ComboCount"))
            {
                string key = pr["ListKey"];

                if (_keyFocus == (id, key) && _openCombo != (id, key) && int.Parse(pr["ComboCount"], CultureInfo.InvariantCulture) is var count and > 0)
                {
                    int selected = s.Selected.TryGetValue(key, out int chosen) ? chosen : (int)Num(c, "ComboSelected", -1);
                    SelectItem(id, key, Math.Clamp(selected + (int)sign, 0, count - 1));
                    return;
                }

                continue;
            }

            if (!pr.ContainsKey("ScrollKey")) continue;

            if (Num(c, "ScrollMax") > 0)
            {
                Scroll(s, pr["ScrollKey"], Num(c, "ScrollMax"), sign * Num(c, "ScrollWheel", 1));
                return;
            }

            if (Num(c, "ScrollMaxH") > 0)
            {
                Scroll(s, pr["ScrollKeyH"], Num(c, "ScrollMaxH"), sign * Num(c, "ScrollWheelH", 1));
                return;
            }
        }
    }

    /// <summary>
    /// Scrolls offset <paramref name="key"/> by <paramref name="delta"/>, within 0 and <paramref name="max"/>.
    /// </summary>
    static void Scroll(Screen s, string key, float max, float delta) => s.Scroll[key] = Math.Clamp(s.Scroll.GetValueOrDefault(key) + delta, 0, max);

    // ---- lists and combo boxes

    /// <summary>
    /// Selects the row <paramref name="source"/> (a row of the bound list) and raises <c>select</c> with the item's id.
    /// </summary>
    void SelectItem(string id, string key, int source)
    {
        if (!_lastBindings.TryGetValue(id, out var b) || !b.Lists.TryGetValue(key, out var items) || source < 0 || source >= items.Count)
            return;

        _screens[id].Selected[key] = source;
        Activated?.Invoke(new(id, key, "select", items[source].Id, source));
    }

    /// <summary>
    /// A press on combo box <paramref name="combo"/>: its toggle opens or closes the list, an item selects it and closes the list.
    /// </summary>
    void ComboPress(string id, string combo, string what)
    {
        _keyFocus = (id, combo);

        if (what == "toggle")
        {
            _openCombo = _openCombo == (id, combo) ? null : (id, combo);
            return;
        }

        _openCombo = null;
        SelectItem(id, combo, int.Parse(what, CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Closes an open combo box popup (Esc); false: none was open.
    /// </summary>
    bool CloseCombo()
    {
        if (_openCombo is null) return false;

        _openCombo = null;
        return true;
    }

    /// <summary>
    /// A key for the focused list or combo box (the keyboard routing calls this before its own handling; true: used). Verified against MyGUI 3.2.2
    /// ListBox.cpp: Up / Down move one row (from no selection both pick the first), Home / End the first / last, PageUp / PageDown a client's worth
    /// of rows; the row is scrolled in when not fully visible; Return accepts. A closed combo box opens on Down.
    /// </summary>
    public bool ListKey(UiListKey key)
    {
        if (_keyFocus is not { } f || !_open.Contains(f.Screen) || !_lastBindings.TryGetValue(f.Screen, out var b)
            || Controls(f.Screen) is not { } controls)
        {
            return false;
        }

        var owner = controls.LastOrDefault(c => c.Properties.GetValueOrDefault("ListKey") == f.Key);
        if (owner is null || !b.Lists.TryGetValue(f.Key, out var items)) return false;

        var s = _screens[f.Screen];
        int[] order = MyGuiPresentation.ListOrder(b, f.Key);
        int source = s.Selected.TryGetValue(f.Key, out int chosen) ? chosen : b.SelectedItems.GetValueOrDefault(f.Key, -1);
        int old = source < 0 ? -1 : Array.IndexOf(order, source), sel = old, count = order.Length;
        bool combo = owner.Properties.ContainsKey("ComboCount");

        if (combo && _openCombo != f)
        {
            if (key == UiListKey.Down)
            {
                _openCombo = f;
                return true;
            }

            if (key == UiListKey.Accept && old >= 0)
            {
                Activated?.Invoke(new(f.Screen, f.Key, "accept", items[source].Id, source));
                return true;
            }

            return false;
        }

        if (count == 0 || !owner.Properties.ContainsKey("ScrollKey")) return false;

        float line = Math.Max(1, Num(owner, "ListLine", 1)), view = Num(owner, "ListView"), max = Num(owner, "ScrollMax");
        int page = Math.Max(1, (int)(view / line));

        switch (key)
        {
            case UiListKey.Accept:
                if (old < 0) return false;

                Activated?.Invoke(new(f.Screen, f.Key, "accept", items[source].Id, source));
                if (combo) _openCombo = null;

                return true;
            case UiListKey.Up: if (sel != 0) sel = sel < 0 ? 0 : sel - 1; break;
            case UiListKey.Down:
                sel = sel < 0 ? 0 : sel + 1;
                if (sel >= count) sel = old;
                break;
            case UiListKey.Home: if (sel != 0) sel = 0; break;
            case UiListKey.End: if (sel != count - 1) sel = count - 1; break;
            case UiListKey.PageUp: sel = sel < 0 ? 0 : sel <= page ? 0 : sel - page; break;
            case UiListKey.PageDown: sel = sel < 0 ? 0 : Math.Min(sel + page, count - 1); break;
        }

        if (sel == old) return true;

        string scroll = owner.Properties["ScrollKey"];
        float at = sel * line - Math.Clamp(s.Scroll.GetValueOrDefault(scroll), 0, max);
        if (at < 0 || at + line > view) s.Scroll[scroll] = Math.Min(sel * line, max);

        SelectItem(f.Screen, f.Key, order[sel]);
        return true;
    }

    // ---- windows

    /// <summary>
    /// The Interface scale setting (1 = 100%): a window as large as its layout says is this much larger (Meitou; the original sizes it by the
    /// screen alone).
    /// </summary>
    public float WindowScale { get; set; } = 1;

    /// <summary>
    /// Places the screen's window roots: centred plus the screen's offset, as large as the layout (or a resize grip) says within <c>MinSize</c> /
    /// <c>MaxSize</c> and the viewport, fully on screen (a dragged one partly, <see cref="OnScreen"/>). A viewport change moves a moved window by its alignment (Verified against MyGUI 3.2.2
    /// Window.cpp for the properties; Meitou's approximation for the move: the window keeps its distance from the edges its Right / Bottom / centre
    /// flags name, the left / top ones by default, a Stretch edge keeps both); Stretch roots with pixel positions grow by the viewport's change.
    /// </summary>
    void PlaceRoots(Screen s, UiBindings b, int width, int height)
    {
        var viewport = new Vector2(width, height);

        if (s.Viewport != default && s.Viewport != viewport && s.Offset != Vector2.Zero)
        {
            var d = viewport - s.Viewport;
            bool stretchX = s.Align.Contains("Stretch") && !s.Align.Contains("VStretch"), stretchY = s.Align.Contains("Stretch")
                && !s.Align.Contains("HStretch");
            float dx = stretchX || s.Align.Contains("HCenter") || s.Align.Contains("Center") ? 0 : s.Align.Contains("Right") ? d.X / 2 : -d.X / 2;
            float dy = stretchY || s.Align.Contains("VCenter") || s.Align.Contains("Center") ? 0 : s.Align.Contains("Bottom") ? d.Y / 2 : -d.Y / 2;
            s.Offset += new Vector2(dx, dy);
        }

        if (s.FirstViewport == default) s.FirstViewport = viewport;

        bool first = true;

        foreach (var root in s.Layout.Roots.Where(r => r.Type == "Window" || s.CentreRoot))
        {
            bool bound = b.Positions.ContainsKey(root.Name);
            var initial = b.Positions.GetValueOrDefault(root.Name, root.Position.RelativeTo(new(0, 0, width, height), root.RealPosition));
            float w = initial.Width * (bound ? 1 : WindowScale), h = initial.Height * (bound ? 1 : WindowScale);

            if (!bound && s.Size is { } size) (w, h) = (size.X, size.Y);
            else if (!bound && !root.RealPosition)
            {
                if (root.Align.Contains("Stretch") && !root.Align.Contains("VStretch")) w += width - s.FirstViewport.X;
                if (root.Align.Contains("Stretch") && !root.Align.Contains("HStretch")) h += height - s.FirstViewport.Y;
            }

            var (min, max) = SizeLimits(root);
            w = Math.Max(min.X, Math.Min(w, max.X));
            h = Math.Max(min.Y, Math.Min(h, max.Y));
            w = Math.Clamp(w, 0, Math.Max(0, width - 24));
            h = Math.Clamp(h, 0, Math.Max(0, height - 24));
            var (x, y) = OnScreen(s, viewport, new(w, h), (width - w) / 2 + s.Offset.X, (height - h) / 2 + s.Offset.Y);
            b.Positions[root.Name] = new(x, y, w, h);
            if (!first) continue;

            first = false;
            s.Placed = new(x, y);
            s.PlacedSize = new(w, h);
            s.Align = root.Align;
            s.Snap = root.Properties.GetValueOrDefault("Snap") == "true";
            s.Movable = root.Properties.GetValueOrDefault("Movable") != "false";
        }

        s.Viewport = viewport;
    }

    /// <summary>
    /// A window's <c>MinSize</c> and <c>MaxSize</c> (none: zero and unlimited).
    /// </summary>
    static (Vector2 Min, Vector2 Max) SizeLimits(MyGuiWidget root)
    {
        static Vector2? Pair(string? v)
        {
            if (v?.Split(' ', StringSplitOptions.RemoveEmptyEntries) is not { Length: 2 } xy) return null;
            if (!float.TryParse(xy[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float a)) return null;

            return float.TryParse(xy[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float c) ? new(a, c) : null;
        }

        return (Pair(root.Properties.GetValueOrDefault("MinSize")) ?? Vector2.Zero,
            Pair(root.Properties.GetValueOrDefault("MaxSize")) ?? new(float.MaxValue));
    }

    /// <summary>
    /// A window drag to the pointer <paramref name="p"/>: with <c>Snap</c> a left / top edge within 10 px of the viewport's snaps to it and a right /
    /// bottom edge within 10 px (strictly) likewise (Verified against MyGUI 3.2.2 Window.cpp getSnappedCoord), then the window may leave the
    /// screen but for <see cref="KeepVisible"/> (Meitou; MyGUI does not clamp).
    /// </summary>
    void DragWindow((string Screen, Vector2 At, Vector2 Offset) d, Vector2 p)
    {
        var was = _screens[d.Screen].Offset;
        MoveWindow(d, p);
        var moved = _screens[d.Screen].Offset - was;
        foreach (var (follower, leader) in _leaders)
        {
            if (leader != d.Screen) continue;

            _screens[follower].Offset += moved;
            _screens[follower].Dragged = true;
        }
    }

    /// <summary>
    /// A window's top-left kept on screen: fully, or for a dragged window with its top edge and <see cref="KeepVisible"/> pixels in view.
    /// </summary>
    static (float X, float Y) OnScreen(Screen s, Vector2 vp, Vector2 size, float x, float y)
    {
        if (!s.Dragged) return (Math.Clamp(x, 0, Math.Max(0, vp.X - size.X)), Math.Clamp(y, 0, Math.Max(0, vp.Y - size.Y)));

        float keepX = MathF.Min(KeepVisible, size.X), keepY = MathF.Min(KeepVisible, size.Y);
        return (Math.Clamp(x, keepX - size.X, Math.Max(0, vp.X - keepX)), Math.Clamp(y, 0, Math.Max(0, vp.Y - keepY)));
    }

    /// <summary>
    /// Moves the window by the pointer's travel from the drag's start: edges snapped with Snap, then kept partly on screen.
    /// </summary>
    void MoveWindow((string Screen, Vector2 At, Vector2 Offset) d, Vector2 p)
    {
        var s = _screens[d.Screen];
        var want = d.Offset + p - d.At;

        if (s.Viewport == default)
        {
            s.Offset = want;
            return;
        }

        var vp = s.Viewport;
        var size = s.PlacedSize;
        float x = (vp.X - size.X) / 2 + want.X, y = (vp.Y - size.Y) / 2 + want.Y;

        if (s.Snap)
        {
            if (Math.Abs(x) <= SnapDistance) x = 0;
            if (Math.Abs(y) <= SnapDistance) y = 0;
            if (Math.Abs(x + size.X - vp.X) < SnapDistance) x = vp.X - size.X;
            if (Math.Abs(y + size.Y - vp.Y) < SnapDistance) y = vp.Y - size.Y;
        }

        s.Dragged = true;
        (x, y) = OnScreen(s, vp, size, x, y);
        s.Offset = new(x - (vp.X - size.X) / 2, y - (vp.Y - size.Y) / 2);
    }

    /// <summary>
    /// A press on a grip: a widget with the user string <c>Scale</c> (four multipliers of the pointer's travel for left, top, width, height) or
    /// <c>Action</c> (Move; Left, Right, Top, Bottom or a pair). Moving grips drag the window; the rest resize it. Kenshi's own window templates have
    /// neither (Observed), so this serves mods that add them.
    /// </summary>
    bool BeginWindowAction(string id, Screen s, UiControl c, Vector2 p)
    {
        static float ParseOrZero(string v) => float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? f : 0;

        Vector4 scale;

        if (c.Properties.TryGetValue("WindowScale", out var text) && text.Split(' ', StringSplitOptions.RemoveEmptyEntries) is { Length: 4 } n
            && n.Select(ParseOrZero).ToArray() is [var l, var t, var w, var h])
        {
            scale = new(l, t, w, h);
        }
        else
        {
            string action = c.Properties.GetValueOrDefault("WindowAction") ?? "";
            scale = action == "Move" ? new(1, 1, 0, 0) : default;
            if (action.Contains("Left")) scale += new Vector4(1, 0, -1, 0);
            if (action.Contains("Right")) scale += new Vector4(0, 0, 1, 0);
            if (action.Contains("Top")) scale += new Vector4(0, 1, 0, -1);
            if (action.Contains("Bottom")) scale += new Vector4(0, 0, 0, 1);
        }

        bool resizes = scale.Z != 0 || scale.W != 0;
        if (!resizes && !s.Movable) return true;
        if (scale == default) return true;

        if (!resizes)
        {
            _drag = (id, p, s.Offset);
            return true;
        }

        _windowGrab = new(id, p, new(s.Placed.X, s.Placed.Y, s.PlacedSize.X, s.PlacedSize.Y), scale);
        return true;
    }

    /// <summary>
    /// A resize grip's drag (Verified against MyGUI 3.2.2 Window.cpp): the start rectangle plus the scale times the travel, the size within
    /// <c>MinSize</c> / <c>MaxSize</c> (a left / top edge then keeps the far edge).
    /// </summary>
    void ResizeWindow(Vector2 p)
    {
        if (_windowGrab is not { } g || !_open.Contains(g.Screen)) return;

        var s = _screens[g.Screen];
        var travel = p - g.At;
        var (min, max) = SizeLimits(s.Layout.Roots.FirstOrDefault(r => r.Type == "Window" || s.CentreRoot) ?? s.Layout.Roots[0]);
        float x = g.Start.X + g.Scale.X * travel.X, y = g.Start.Y + g.Scale.Y * travel.Y;
        float w = Math.Max(min.X, Math.Min(g.Start.Width + g.Scale.Z * travel.X, max.X)), h = Math.Max(min.Y,
            Math.Min(g.Start.Height + g.Scale.W * travel.Y, max.Y));
        if (g.Scale.X != 0) x = g.Start.X + g.Start.Width - w;
        if (g.Scale.Y != 0) y = g.Start.Y + g.Start.Height - h;

        var vp = s.Viewport;
        s.Size = new(w, h);
        s.Offset = new(x - (vp.X - w) / 2, y - (vp.Y - h) / 2);
    }
}