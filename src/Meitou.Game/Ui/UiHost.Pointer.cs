using System.Globalization;
using System.Numerics;
using Meitou.Data.Ui;

namespace Meitou.Game.Ui;

/// <summary>
/// The overlay's node looks, scroll views and pointer events (docs/modding.md "Interface").
/// </summary>
public sealed partial class UiHost
{
    #region Node looks

    /// <summary>
    /// The largest <see cref="UiNode.TextScale"/> a node may ask for.
    /// </summary>
    public const float MaxTextScale = 8;

    /// <summary>
    /// Throws for a node whose own looks are not finite or out of range: its opacity, text scale, text colour, image crop or scroll step.
    /// </summary>
    static void ValidateLooks(UiNode node)
    {
        bool colour = node.TextColour is not { } c || float.IsFinite(c.X + c.Y + c.Z + c.W);
        bool crop = node.ImageCrop is not { } r
            || float.IsFinite(r.Rect.X + r.Rect.Y + r.Rect.Width + r.Rect.Height) && r.Rect.Width > 0 && r.Rect.Height > 0;
        bool scale = node.TextScale is null or (> 0 and <= MaxTextScale);
        bool step = float.IsFinite(node.ScrollStep) && node.ScrollStep > 0;
        bool tint = node.Tint is not { } t || InUnit(t);
        bool layer = Enum.IsDefined(node.Layer);
        if (!float.IsFinite(node.Opacity) || !colour || !crop || !scale || !step || !tint || !layer)
            throw new InvalidOperationException($"Invalid UI node looks: {node.Id}");
    }

    /// <summary>
    /// Whether each channel of <paramref name="c"/> is finite and within 0 to 1.
    /// </summary>
    static bool InUnit(Vector4 c) => float.IsFinite(c.X + c.Y + c.Z + c.W) && Vector4.Clamp(c, Vector4.Zero, Vector4.One) == c;

    /// <summary>
    /// The properties of <paramref name="node"/>'s own looks: its inherited <paramref name="opacity"/> (<c>Alpha</c>, only below 1) and an image's
    /// crop (<c>ImageRect</c> in pixels, Meitou's <c>ImageUv</c> in fractions). Nothing for a node without them, so its control stays as before.
    /// </summary>
    static void AddLooks(UiNode node, float opacity, Dictionary<string, string> properties)
    {
        if (opacity < 1) properties["Alpha"] = opacity.ToString("R", CultureInfo.InvariantCulture);
        if (node.Tint is { } t)
            properties["Colour"] = Format(t);

        bool portrait = node.Portrait.Length > 0 || node.PortraitOf is not null;
        if (node.Kind == UiNodeKind.Image && !portrait && node.ImageCrop is { } crop)
            properties[crop.Normalized ? "ImageUv" : "ImageRect"] = Format(crop.Rect);
    }

    /// <summary>
    /// <paramref name="r"/> as four numbers (<see cref="UiRect.Parse"/>'s form).
    /// </summary>
    static string Format(UiRect r) => string.Create(CultureInfo.InvariantCulture, $"{r.X} {r.Y} {r.Width} {r.Height}");

    /// <summary>
    /// <paramref name="c"/> as four round-trip numbers (x y z w), the form a control's colour properties take.
    /// </summary>
    internal static string Format(Vector4 c) => string.Create(CultureInfo.InvariantCulture, $"{c.X:R} {c.Y:R} {c.Z:R} {c.W:R}");

    #endregion

    #region Arrangement

    /// <summary>
    /// What the last <see cref="Arrange"/> laid out, for its input.
    /// </summary>
    UiArrangement _last = new();

    /// <summary>
    /// Whether a node shown in the last arrangement asks to be laid out every frame (<see cref="UiNode.Animated"/>).
    /// </summary>
    public bool Animating => _last.Animated;

    /// <summary>
    /// Whether the last arrangement laid out any overlay node (else the pointer events have nothing to go to).
    /// </summary>
    public bool HasOverlayNodes => _last.Nodes.Count > 0;

    /// <summary>
    /// A part of a scroll view's bar, or none for a node's own control.
    /// </summary>
    enum ScrollPart
    {
        None,
        Track,
        Up,
        Down,
        PageUp,
        PageDown,
        Thumb,
    }

    /// <summary>
    /// A laid-out control: its node, its parent's index (-1: a root), its contribution's owner and the scroll bar part it is (none for a node's).
    /// </summary>
    readonly record struct ArrangedNode(UiNode Node, UiControl Control, int Parent, string Owner, ScrollPart Part);

    /// <summary>
    /// A laid-out scroll view: its offset's key, its children's view, its greatest offset, its step, and its bar's track and thumb height.
    /// </summary>
    readonly record struct ScrollState(string Key, UiRect View, float Maximum, float Step, UiRect Track, float Thumb);

    /// <summary>
    /// The nodes of one arrangement in drawing order, found again by their controls.
    /// </summary>
    sealed class UiArrangement
    {
        /// <summary>
        /// The laid-out controls with their nodes, in drawing order.
        /// </summary>
        internal readonly List<ArrangedNode> Nodes = [];

        /// <summary>
        /// The controls, in drawing order (for the hit test).
        /// </summary>
        internal readonly List<UiControl> Controls = [];

        /// <summary>
        /// The last index of each control name.
        /// </summary>
        internal readonly Dictionary<string, int> ByName = new(StringComparer.Ordinal);

        /// <summary>
        /// The text boxes by name (<see cref="DispatchText"/>).
        /// </summary>
        internal readonly Dictionary<string, UiControl> TextBoxes = new(StringComparer.Ordinal);

        /// <summary>
        /// The scroll views by their index.
        /// </summary>
        internal readonly Dictionary<int, ScrollState> ScrollViews = [];

        /// <summary>
        /// The owner of the contribution being laid out.
        /// </summary>
        internal string Owner = "";

        /// <summary>
        /// A shown node asks to be laid out every frame.
        /// </summary>
        internal bool Animated;

        /// <summary>
        /// Records <paramref name="control"/> of <paramref name="node"/> under <paramref name="parent"/>; returns its index.
        /// </summary>
        internal int Add(UiNode node, UiControl control, int parent, ScrollPart part = ScrollPart.None)
        {
            Nodes.Add(new(node, control, parent, Owner, part));
            Controls.Add(control);
            Index(Nodes.Count - 1);
            return Nodes.Count - 1;
        }

        /// <summary>
        /// Puts node <paramref name="i"/> in the lookups.
        /// </summary>
        void Index(int i)
        {
            var entry = Nodes[i];
            ByName[entry.Control.Name] = i;
            if (entry.Part != ScrollPart.None) return;

            if (entry.Node.Kind == UiNodeKind.TextBox) TextBoxes[entry.Control.Name] = entry.Control;
            Animated |= entry.Node.Animated;
        }

        /// <summary>
        /// The index of <paramref name="control"/> (or the copy of it the interface drew: the same name, bounds and action); null for another's.
        /// </summary>
        internal int? Find(UiControl control) =>
            ByName.TryGetValue(control.Name, out int i) && Nodes[i].Control.Bounds == control.Bounds && Nodes[i].Control.Action == control.Action
                ? i : null;

        /// <summary>
        /// Drops the nodes from <paramref name="count"/> on (a contribution that failed).
        /// </summary>
        internal void Truncate(int count)
        {
            if (Nodes.Count == count) return;

            Nodes.RemoveRange(count, Nodes.Count - count);
            Controls.RemoveRange(count, Controls.Count - count);
            foreach (int view in ScrollViews.Keys.Where(k => k >= count).ToArray()) ScrollViews.Remove(view);
            ByName.Clear();
            TextBoxes.Clear();
            Animated = false;
            for (int i = 0; i < Nodes.Count; i++) Index(i);
        }
    }

    #endregion

    #region Scroll views

    /// <summary>
    /// A scroll view's bar's width, pixels.
    /// </summary>
    const float ScrollBarWidth = 14;

    /// <summary>
    /// The scroll views' offsets, pixels, by owner and node id (<see cref="ScrollKey"/>).
    /// </summary>
    readonly Dictionary<string, float> _scrollOffsets = new(StringComparer.Ordinal);

    /// <summary>
    /// The key of <paramref name="owner"/>'s scroll view <paramref name="id"/>.
    /// </summary>
    static string ScrollKey(string owner, string id) => owner + "\n" + id;

    /// <summary>
    /// Lays out a scroll view's children in <paramref name="content"/> (less its bar when they overflow) shifted by its kept offset, clipped to it,
    /// then its bar.
    /// </summary>
    void ArrangeScrollView(UiNode node, UiRect content, NodeScope scope, UiArrangement into, List<UiControl> result)
    {
        string key = ScrollKey(into.Owner, node.Id);
        float extent = ContentExtent(node, content);
        bool bar = node.ScrollBar && extent > content.Height && content.Width > ScrollBarWidth;
        var view = bar ? content with { Width = content.Width - ScrollBarWidth } : content;
        if (bar) extent = ContentExtent(node, view);

        float maximum = Math.Max(0, extent - view.Height);
        float offset = Math.Clamp(_scrollOffsets.GetValueOrDefault(key), 0, maximum);
        if (_scrollOffsets.ContainsKey(key)) _scrollOffsets[key] = offset;

        var shifted = view with { Y = view.Y - offset };
        var inner = scope with { Clip = scope.Clip.Intersect(view) };
        foreach (var (child, rect) in ChildRects(node, shifted)) ArrangeNode(child, shifted, inner, into, result, rect);

        var state = new ScrollState(key, view, maximum, node.ScrollStep, default, 0);
        if (bar && maximum > 0)
        {
            var barRect = new UiRect(view.X + view.Width, content.Y, ScrollBarWidth, content.Height);
            state = AddScrollBar(node, barRect, extent, offset, scope, into, result, state);
        }

        into.ScrollViews[scope.Parent] = state;
    }

    /// <summary>
    /// How far down <paramref name="node"/>'s children reach below the top of <paramref name="content"/>, pixels.
    /// </summary>
    static float ContentExtent(UiNode node, UiRect content)
    {
        float bottom = 0;
        foreach (var (child, rect) in ChildRects(node, content))
        {
            var r = rect ?? child.Placement.Resolve(content);
            bottom = Math.Max(bottom, r.Y + r.Height - content.Y);
        }

        return bottom;
    }

    /// <summary>
    /// A scroll view's bar in <paramref name="bar"/> as <c>Kenshi_ScrollBarV</c> draws one (<see cref="ScrollBars.AddScrollBar"/>'s parts at
    /// scale 1): arrows at both ends, the track between them and a thumb 10 or more pixels tall, sized to the visible share. Returns the state with
    /// its track and thumb.
    /// </summary>
    static ScrollState AddScrollBar(UiNode node, UiRect bar, float extent, float offset, NodeScope scope, UiArrangement into,
        List<UiControl> result, ScrollState state)
    {
        var track = new UiRect(bar.X, bar.Y + 14, bar.Width, Math.Max(0, bar.Height - 28));
        float thumb = Math.Clamp(track.Height * state.View.Height / Math.Max(1, extent), Math.Min(10, track.Height), track.Height);
        float top = track.Y + (track.Height - thumb) * offset / state.Maximum;

        void Part(ScrollPart part, string suffix, UiRect bounds, string skin)
        {
            var control = ScrollPartControl(node.Id + suffix, bounds, skin, scope);
            result.Add(control);
            into.Add(node, control, scope.Parent, part);
        }

        Part(ScrollPart.Track, "-scrollbar", bar, "Kenshi_TextBoxEmptySkin");
        Part(ScrollPart.Up, "-scrollbar-up", new(bar.X, bar.Y + 2, bar.Width, 12), "Kenshi_SmallArrowUpSkin");
        Part(ScrollPart.Down, "-scrollbar-down", new(bar.X, bar.Y + bar.Height - 14, bar.Width, 12), "Kenshi_SmallArrowDownSkin");
        Part(ScrollPart.PageUp, "-scrollbar-page-up", new(track.X, track.Y, track.Width, top - track.Y), "");
        Part(ScrollPart.PageDown, "-scrollbar-page-down", new(track.X, top + thumb, track.Width, track.Y + track.Height - top - thumb), "");
        Part(ScrollPart.Thumb, "-scrollbar-thumb", new(track.X, top, track.Width, thumb), "Kenshi_SmallBarSkin");
        return state with { Track = track, Thumb = thumb };
    }

    /// <summary>
    /// One part of a scroll view's bar: pickable, clipped and faded as the view.
    /// </summary>
    static UiControl ScrollPartControl(string name, UiRect bounds, string skin, NodeScope scope)
    {
        var properties = new Dictionary<string, string>();
        if (scope.Opacity < 1) properties["Alpha"] = scope.Opacity.ToString("R", CultureInfo.InvariantCulture);

        return new(name, skin.Length > 0 ? "Button" : "Widget", bounds, "", "", skin, scope.Enabled, properties, Vector4.Zero, Pickable: true,
            Clip: scope.Clip);
    }

    /// <summary>
    /// A press on a part of a scroll view's bar: an arrow scrolls a step, the track beside the thumb a page, the thumb is taken.
    /// </summary>
    void PressScrollPart(ArrangedNode entry, Vector2 p)
    {
        if (!entry.Control.Enabled || !_last.ScrollViews.TryGetValue(entry.Parent, out var view)) return;

        if (entry.Part == ScrollPart.Up) ScrollBy(view, -view.Step);
        else if (entry.Part == ScrollPart.Down) ScrollBy(view, view.Step);
        else if (entry.Part == ScrollPart.PageUp) ScrollBy(view, -view.View.Height);
        else if (entry.Part == ScrollPart.PageDown) ScrollBy(view, view.View.Height);
        else if (entry.Part == ScrollPart.Thumb) _thumb = (view.Key, p.Y - entry.Control.Bounds.Y);
    }

    /// <summary>
    /// <paramref name="owner"/>'s scroll view <paramref name="id"/>'s offset, pixels, as last laid out or set; 0 for one never scrolled.
    /// </summary>
    internal float ScrollOffset(string owner, string id) => _scrollOffsets.GetValueOrDefault(ScrollKey(owner, id));

    /// <summary>
    /// Sets <paramref name="owner"/>'s scroll view <paramref name="id"/>'s offset to <paramref name="offset"/> pixels (at least 0); the next layout
    /// clamps it to the view's content.
    /// </summary>
    internal void ScrollTo(string owner, string id, float offset) => _scrollOffsets[ScrollKey(owner, id)] = Math.Max(0, offset);

    /// <summary>
    /// Moves a scroll view's offset by <paramref name="delta"/> pixels, within its range.
    /// </summary>
    void ScrollBy(ScrollState view, float delta) =>
        _scrollOffsets[view.Key] = Math.Clamp(_scrollOffsets.GetValueOrDefault(view.Key) + delta, 0, view.Maximum);

    /// <summary>
    /// The taken thumb follows the pointer.
    /// </summary>
    void MoveThumb((string Key, float Grab) thumb, Vector2 p)
    {
        foreach (var view in _last.ScrollViews.Values)
        {
            if (view.Key != thumb.Key || view.Track.Height <= view.Thumb) continue;

            float share = Math.Clamp((p.Y - thumb.Grab - view.Track.Y) / (view.Track.Height - view.Thumb), 0, 1);
            _scrollOffsets[view.Key] = share * view.Maximum;
            return;
        }
    }

    #endregion

    #region Pointer events

    /// <summary>
    /// A press held by a node that asked for its release or drags (the node's action and argument when pressed, where it was pressed and where
    /// the pointer last was).
    /// </summary>
    readonly record struct PointerCapture(string Owner, string Action, string Argument, string Node, UiPointerEvents Events, Vector2 Start,
        Vector2 Last);

    /// <summary>
    /// The node under the pointer that asked for hover events.
    /// </summary>
    readonly record struct Hovered(string Owner, string Node, string Action, string Argument);

    /// <summary>
    /// The held press; null: none.
    /// </summary>
    PointerCapture? _capture;

    /// <summary>
    /// A scroll view's taken thumb (its key and where on the thumb it was taken); null: none.
    /// </summary>
    (string Key, float Grab)? _thumb;

    /// <summary>
    /// The hovered node; null: none.
    /// </summary>
    Hovered? _hovered;

    /// <summary>
    /// Whether an overlay node holds the pointer (a press asking for its release or drags, a scroll view's thumb): the world does not see it.
    /// </summary>
    public bool CapturesPointer => _capture is not null || _thumb is not null;

    /// <summary>
    /// A left press on <paramref name="control"/> at <paramref name="p"/>, the topmost control there: an overlay node's action runs with the press
    /// (<see cref="UiEventKind.Press"/>) and the nearest node asking for the release or drags holds the press; a scroll bar's part scrolls; any
    /// other control is <see cref="Dispatch(UiControl)"/>ed. As Dispatch, false for an unregistered action.
    /// </summary>
    public bool Press(UiControl control, Vector2 p)
    {
        if (_last.Find(control) is not { } index) return Dispatch(control);

        var entry = _last.Nodes[index];
        if (entry.Part != ScrollPart.None)
        {
            PressScrollPart(entry, p);
            return true;
        }

        bool ran = Run(control.Enabled, control.Action, f => Event(f, entry.Node.Id, control.Argument, UiEventKind.Press, p) with { Start = p });
        Capture(index, p);
        return ran;
    }

    /// <summary>
    /// A press of <paramref name="button"/> (not the left) on <paramref name="control"/>, the topmost overlay control there: the nearest node from
    /// it up asking for <see cref="UiPointerEvents.Buttons"/> gets it as <see cref="UiEventKind.Press"/>. False when none asks (another's control).
    /// </summary>
    public bool ButtonPress(UiControl control, Vector2 p, Host.Input.MouseButton button)
    {
        if (_last.Find(control) is not { } index) return false;

        for (int at = index; at >= 0; at = _last.Nodes[at].Parent)
        {
            var e = _last.Nodes[at];
            if (e.Part != ScrollPart.None || (e.Node.Pointer & UiPointerEvents.Buttons) == 0) continue;

            return Run(e.Control.Enabled, e.Control.Action,
                f => Event(f, e.Node.Id, e.Control.Argument, UiEventKind.Press, p) with { Start = p, Button = button });
        }

        return false;
    }

    /// <summary>
    /// A press on <paramref name="widget"/>, an extension's control on <paramref name="screen"/> (docs/modding.md "Screen controls"): with the
    /// left button, when it or a parent asks for the release or drags, its action gets the press and that node holds it, as on the overlay; with
    /// another button, the nearest asking for <see cref="UiPointerEvents.Buttons"/> gets it. False otherwise (the screen takes the press).
    /// </summary>
    public bool PressScreen(string screen, string widget, Vector2 p, Host.Input.MouseButton button)
    {
        if (button != Host.Input.MouseButton.Left)
        {
            return Screens.Nearest(screen, widget, UiPointerEvents.Buttons) is { } b
                && Run(b.Enabled, b.Action, f => Event(f, b.Node, b.Argument, UiEventKind.Press, p) with { Start = p, Button = button });
        }

        if (Screens.Nearest(screen, widget, UiPointerEvents.Release | UiPointerEvents.Drag) is not { } holder) return false;

        if (Screens.Node(screen, widget) is { } pressed)
            Run(pressed.Enabled, pressed.Action, f => Event(f, pressed.Node, pressed.Argument, UiEventKind.Press, p) with { Start = p });
        if (holder.Enabled) _capture = new(holder.Owner, holder.Action, holder.Argument, holder.Node, holder.Pointer, p, p);
        return true;
    }

    /// <summary>
    /// The nearest enabled node from <paramref name="index"/> up asking for the release or drags holds the press.
    /// </summary>
    void Capture(int index, Vector2 p)
    {
        for (int at = index; at >= 0; at = _last.Nodes[at].Parent)
        {
            var e = _last.Nodes[at];
            if (e.Part != ScrollPart.None || (e.Node.Pointer & (UiPointerEvents.Release | UiPointerEvents.Drag)) == 0) continue;

            if (e.Control.Enabled) _capture = new(e.Owner, e.Control.Action, e.Control.Argument, e.Node.Id, e.Node.Pointer, p, p);
            return;
        }
    }

    /// <summary>
    /// A pointer move: the taken thumb follows it, a held press asking for drags gets one (<see cref="UiEventKind.Drag"/>). True while the overlay
    /// holds the pointer.
    /// </summary>
    public bool PointerMove(Vector2 p)
    {
        if (_thumb is { } thumb)
        {
            MoveThumb(thumb, p);
            return true;
        }
        if (_capture is not { } c) return false;

        _capture = c with { Last = p };
        if ((c.Events & UiPointerEvents.Drag) != 0 && p != c.Last)
        {
            Run(true, c.Action,
                f => Event(f, c.Node, c.Argument, UiEventKind.Drag, p) with { Start = c.Start, Delta = p - c.Last, Over = OverAt(p) });
        }

        return true;
    }

    /// <summary>
    /// The left button's release: the thumb is let go, a held press asking for it gets the release (<see cref="UiEventKind.Release"/>). True when
    /// the overlay held the pointer.
    /// </summary>
    public bool PointerUp(Vector2 p)
    {
        bool held = _thumb is not null || _capture is not null;
        _thumb = null;
        if (_capture is not { } c) return held;

        _capture = null;
        if ((c.Events & UiPointerEvents.Release) != 0)
            Run(true, c.Action, f => Event(f, c.Node, c.Argument, UiEventKind.Release, p) with { Start = c.Start, Over = OverAt(p) });
        return true;
    }

    /// <summary>
    /// A wheel step over <paramref name="control"/> (the topmost control there): to the nearest node from it up that asks for the wheel
    /// (<see cref="UiEventKind.Wheel"/>) or is a scroll view that can scroll. False when none takes it (another's control, or none asks).
    /// </summary>
    public bool Wheel(UiControl? control, Vector2 p, float steps)
    {
        if (control is null || _last.Find(control) is not { } index) return false;

        for (int at = index; at >= 0; at = _last.Nodes[at].Parent)
        {
            var e = _last.Nodes[at];
            if (e.Part == ScrollPart.None && (e.Node.Pointer & UiPointerEvents.Wheel) != 0)
            {
                return Run(e.Control.Enabled, e.Control.Action,
                    f => Event(f, e.Node.Id, e.Control.Argument, UiEventKind.Wheel, p) with { Wheel = steps });
            }

            if (_last.ScrollViews.TryGetValue(at, out var view) && view.Maximum > 0)
            {
                ScrollBy(view, -steps * view.Step);
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The pointer is at <paramref name="p"/> over <paramref name="top"/> (the topmost control there, null for none): when the nearest enabled
    /// node from it up asking for hover events changes, the last one gets <see cref="UiEventKind.Leave"/> and the new one
    /// <see cref="UiEventKind.Enter"/>.
    /// </summary>
    public void Hover(UiControl? top, Vector2 p)
    {
        var now = top is null ? null : HoverTarget(top);
        if (now?.Owner == _hovered?.Owner && now?.Node == _hovered?.Node) return;

        var old = _hovered;
        _hovered = now;
        if (old is { } o) Run(true, o.Action, f => Event(f, o.Node, o.Argument, UiEventKind.Leave, p));
        if (now is { } n) Run(true, n.Action, f => Event(f, n.Node, n.Argument, UiEventKind.Enter, p));
    }

    /// <summary>
    /// The nearest enabled node from <paramref name="top"/> up asking for hover events; null: none (or another's control).
    /// </summary>
    Hovered? HoverTarget(UiControl top)
    {
        if (_last.Find(top) is not { } index) return null;

        for (int at = index; at >= 0; at = _last.Nodes[at].Parent)
        {
            var e = _last.Nodes[at];
            if (e.Part == ScrollPart.None && e.Control.Enabled && (e.Node.Pointer & UiPointerEvents.Hover) != 0)
                return new(e.Owner, e.Node.Id, e.Control.Action, e.Control.Argument);
        }

        return null;
    }

    /// <summary>
    /// The id of the topmost overlay node under <paramref name="p"/> (a scroll bar's part: its view's); empty: none.
    /// </summary>
    string OverAt(Vector2 p) =>
        MyGuiPresentation.HitTest(_last.Controls, p.X, p.Y) is { } hit && _last.Find(hit) is { } i ? _last.Nodes[i].Node.Id : "";

    /// <summary>
    /// A pointer event's context: its kind, node, argument and the pointer.
    /// </summary>
    static UiActionContext Event(UiFrameContext frame, string node, string argument, UiEventKind kind, Vector2 p) =>
        new(frame, argument) { Kind = kind, Node = node, Position = p };

    /// <summary>
    /// Forgets <paramref name="owner"/>'s held press, hover and scroll offsets.
    /// </summary>
    void RemovePointerState(string owner)
    {
        if (_capture?.Owner == owner) _capture = null;
        if (_hovered?.Owner == owner) _hovered = null;
        if (_thumb is { } thumb && thumb.Key.StartsWith(owner + "\n", StringComparison.Ordinal)) _thumb = null;
        foreach (string key in _scrollOffsets.Keys.Where(k => k.StartsWith(owner + "\n", StringComparison.Ordinal)).ToArray())
            _scrollOffsets.Remove(key);
    }

    #endregion
}
