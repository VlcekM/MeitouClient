using System.Globalization;

namespace Meitou.Data.Ui;

/// <summary>
/// A MultiListBox column (MyGUI <c>addColumn</c>): its caption and width in layout pixels; a Fill column takes the width the others leave (MyGUI's
/// Fill resizing policy), the rest are Fixed.
/// </summary>
public sealed record UiColumn(string Caption, float Width, bool Fill = false);

/// <summary>
/// The bindings the run-time widgets read: list columns and sorting, scroll canvases and native scrolling, text measuring, the open combo box.
/// </summary>
public sealed partial class UiBindings
{
    /// <summary>
    /// The columns of a MultiListBox by widget name; its rows are <see cref="Lists"/> items whose <see cref="UiListItem.Cells"/> hold one text per
    /// column.
    /// </summary>
    public Dictionary<string, IReadOnlyList<UiColumn>> Columns { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// The sorted column and direction of a MultiListBox (the host overrides it once the user clicks a header).
    /// </summary>
    public Dictionary<string, (int Column, bool Ascending)> Sorts { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// The canvas of a ScrollView in pixels, for content sized at run time (else its <c>CanvasSize</c> property).
    /// </summary>
    public Dictionary<string, (float Width, float Height)> Canvas { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// ScrollViews whose scrolling the screen implements itself (the map): the generic canvas, bars and offsets leave them alone.
    /// </summary>
    public HashSet<string> NativeScroll { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// The height of a control's text as the host's painter would draw it (wrapped to its width when it has <c>WordWrap</c>); null when the host
    /// measures no text, so an EditBox never shows its vertical bar.
    /// </summary>
    public Func<UiControl, float>? TextHeight { get; set; }

    /// <summary>
    /// The ComboBox whose popup list is open (set by the host).
    /// </summary>
    public string? OpenCombo { get; set; }
}

/// <summary>
/// ScrollBar, ListBox, ScrollView, MultiListBox and ComboBox layout (docs/formats/mygui.md "Widget runtime"). The geometry is made here from state
/// the host pushes into <see cref="UiBindings"/>; the controls carry markers (<c>Bar*</c>, <c>Scroll*</c>, <c>List*</c>, <c>Combo*</c>) that the
/// screen host reads for input.
/// </summary>
public static partial class MyGuiPresentation
{
    /// <summary>
    /// A ScrollView's wheel step and bar page in template pixels, and a list line's height when the skin gives none.
    /// </summary>
    const float ScrollViewWheel = 50, ScrollViewPage = 16, DefaultLine = 22;

    /// <summary>
    /// The culture of every number the layouts and markers hold.
    /// </summary>
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>
    /// Arranges one widget under <paramref name="parent"/> (the visit of <see cref="Arrange"/>).
    /// </summary>
    delegate void VisitWidget(MyGuiWidget widget, UiRect parent, UiRect clip, bool enabled, float alpha);

    /// <summary>
    /// A number as a marker property (round-trip).
    /// </summary>
    static string N(float v) => v.ToString("R", Inv);

    /// <summary>
    /// The number property <paramref name="key"/>, or <paramref name="fallback"/> when it is missing or not a number.
    /// </summary>
    public static float Num(IReadOnlyDictionary<string, string> p, string key, float fallback = 0) =>
        p.TryGetValue(key, out var s) && float.TryParse(s, NumberStyles.Float, Inv, out float v) ? v : fallback;

    /// <summary>
    /// A ScrollBar, or a widget the layout editor marks as one.
    /// </summary>
    static bool IsBar(MyGuiWidget w) => w.Type == "ScrollBar" || w.UserStrings.GetValueOrDefault("LE_TargetWidgetType") == "ScrollBar";

    /// <summary>
    /// The text the code set for a template part (<c>owner.part</c>); null when it set none.
    /// </summary>
    static string? PartText(UiBindings b, IReadOnlyDictionary<string, string> props) =>
        b.Text.Count > 0 && props.TryGetValue("TemplateOwner", out var o) && props.TryGetValue("TemplatePart", out var part) && part.Length > 0
            ? b.Coded(o + "." + part)
            : null;

    /// <summary>
    /// Where the children of a ScrollView's client are placed: the scrolled canvas rather than the client.
    /// </summary>
    static UiRect ChildParent(IReadOnlyDictionary<string, string> props, UiRect bounds) =>
        props.TryGetValue("CanvasOrigin", out var s) ? UiRect.Parse(s) : bounds;

    /// <summary>
    /// The part <paramref name="part"/> of the template expanded for <paramref name="owner"/>; null when it has none.
    /// </summary>
    static MyGuiWidget? PartOf(MyGuiWidget owner, string part)
    {
        foreach (var n in owner.Children) if (FindPart(n, owner.Name, part) is { } found) return found;
        return null;
    }

    /// <summary>
    /// <paramref name="n"/> or its first descendant that is the part <paramref name="part"/> of <paramref name="owner"/>'s template.
    /// </summary>
    static MyGuiWidget? FindPart(MyGuiWidget n, string owner, string part) =>
        n.Properties.GetValueOrDefault("TemplateOwner") == owner && n.Properties.GetValueOrDefault("TemplatePart") == part ? n
        : n.Children.Select(c => FindPart(c, owner, part)).FirstOrDefault(f => f is not null);

    /// <summary>
    /// The index in <paramref name="r"/> of the last control of a template part; -1 when there is none.
    /// </summary>
    static int IndexOfPart(List<UiControl> r, MyGuiWidget owner, string part) =>
        PartOf(owner, part) is { } p ? r.FindLastIndex(c => c.Name == p.Name) : -1;

    /// <summary>
    /// A row's text in <paramref name="column"/>: its cell, or its caption in the first column when it has no cells.
    /// </summary>
    static string CellOf(UiListItem item, int column) => item.Cells is { } cells
        ? column >= 0 && column < cells.Count ? cells[column] : ""
        : column <= 0 ? item.Caption : "";

    /// <summary>
    /// A list line's height from the skin's <c>HeightLine</c> at the template scale, else <see cref="DefaultLine"/>.
    /// </summary>
    static float HeightLine(MyGuiWidget w, UiBindings b)
    {
        if (!w.UserStrings.TryGetValue("HeightLine", out var heightLine)) return DefaultLine;

        return float.TryParse(heightLine, NumberStyles.Float, Inv, out float height) && height >= 1 ? height * b.TemplateScale : DefaultLine;
    }

    /// <summary>
    /// A list's client-set <c>LineHeight</c> in pixels; null when it has none (or less than a pixel).
    /// </summary>
    static float? LineHeight(Dictionary<string, string> props)
    {
        if (!props.TryGetValue("LineHeight", out var lineHeight)) return null;

        return float.TryParse(lineHeight, NumberStyles.Float, Inv, out float pixels) && pixels >= 1 ? pixels : null;
    }

    /// <summary>
    /// The source indices of a list's rows in display order: as bound, or by the MultiListBox's sort column (MyGUI's default comparator is a plain
    /// string comparison of the cells).
    /// </summary>
    public static int[] ListOrder(UiBindings b, string name)
    {
        if (!b.Lists.TryGetValue(name, out var items)) return [];
        var order = Enumerable.Range(0, items.Count);
        if (b.Columns.ContainsKey(name) && b.Sorts.TryGetValue(name, out var sort))
        {
            string Key(int i) =>
                items[i].SortKeys is { } keys && sort.Column >= 0 && sort.Column < keys.Count ? keys[sort.Column] : CellOf(items[i], sort.Column);
            order = sort.Ascending ? order.OrderBy(Key, StringComparer.Ordinal) : order.OrderByDescending(Key, StringComparer.Ordinal);
        }
        return order.ToArray();
    }

    /// <summary>
    /// After a widget's children: the run-time content and geometry of the widgets that have it.
    /// </summary>
    static void AddWidgetContent(List<UiControl> result, UiBindings b, MyGuiWidget w, Dictionary<string, string> props, UiRect bounds, UiRect clip,
        bool enabled, float alpha, UiRect viewport, VisitWidget visit, List<Action> deferred)
    {
        if (w.UserStrings.ContainsKey("Scale") || w.UserStrings.ContainsKey("Action"))
        {
            // A window grip (MyGUI Window: user strings Scale, Action): the host moves or resizes the window by it.
            if (w.UserStrings.TryGetValue("Scale", out var scale)) props["WindowScale"] = scale;
            if (w.UserStrings.TryGetValue("Action", out var action)) props["WindowAction"] = action;
            int grip = result.FindLastIndex(c => c.Name == w.Name);
            if (grip >= 0) result[grip] = result[grip] with { Pickable = true };
        }

        bool bar = IsBar(w);
        if (bar)
        {
            if (props.ContainsKey("Range")) FreeBar(result, b, w, props);
        }
        else
        {
            foreach (var child in w.Children)
            {
                if (child.Properties.GetValueOrDefault("TemplateOwner") != w.Name
                    || child.Properties.GetValueOrDefault("TemplatePart") is not { } part || part is not ("Start" or "End"))
                    continue;
                int i = result.FindLastIndex(c => c.Name == child.Name);
                if (i >= 0)
                    result[i] = result[i] with { Action = $"mygui:step:{w.Name}:{part.ToLowerInvariant()}", Pickable = true, Enabled = enabled };
            }
        }

        if (w.Type == "EditBox") EditBoxScroll(result, b, w, props);
        if (w.Type == "ComboBox") Combo(result, b, w, props, bounds, enabled, viewport, visit, deferred);
        else if (w.Type == "MultiListBox" && b.Columns.ContainsKey(w.Name)) MultiColumns(result, b, w, props, enabled, alpha, visit);
        else if (props.TryGetValue("ListSource", out var source) && b.Lists.TryGetValue(source, out var sourceItems))
        {
            ListRows(result, b, w, props, bounds, clip, enabled, props["ListSel"], props["ListScroll"], sourceItems, ListOrder(b, source),
                (int)Num(props, "ListColumn"), props.GetValueOrDefault("ListBar") != "false", props.GetValueOrDefault("RowAction") ?? "mygui:list");
        }
        else if (b.Lists.TryGetValue(w.Name, out var items))
            ListRows(result, b, w, props, bounds, clip, enabled, w.Name, w.Name, items, ListOrder(b, w.Name), 0, true, "mygui:list");
    }

    /// <summary>
    /// An EditBox's vertical bar (MyGUI 3.2 EditBox and ScrollViewBase::updateScrollSize): it shows only when <c>VisibleVScroll</c> is not false and
    /// the text is taller than the client without it, and scrolls the text by its position (the client's <c>TextScroll</c>, page 16, wheel 50 pixels
    /// at the template's scale). The client is the template's plus the bar's width while the bar is hidden, less it while shown (Observed in the
    /// editor's race description: a Hive Prince's wraps 6 px inside the frame, a skeleton's 14 px clear of the bar).
    /// </summary>
    static void EditBoxScroll(List<UiControl> result, UiBindings b, MyGuiWidget w, Dictionary<string, string> props)
    {
        if (PartOf(w, "VScroll") is not { } barWidget || result.FindLastIndex(c => c.Name == barWidget.Name) is not (var bi and >= 0)) return;
        int ci = IndexOfPart(result, w, "Client");
        float widen = result[bi].Bounds.Width;
        if (ci >= 0 && props.GetValueOrDefault("VisibleVScroll") != "false" && b.TextHeight is { } measure)
        {
            var client = result[ci];
            float view = client.Bounds.Height;
            if (measure(client with { Bounds = client.Bounds with { Width = client.Bounds.Width + widen } }) > view + 0.5f)
            {
                // Shown, the bar's width comes off the template's client (Observed: the skeleton's description wraps 14 px clear of the bar, where
                // the template's client reaches under it).
                client = client with { Bounds = client.Bounds with { Width = Math.Max(0, client.Bounds.Width - widen) } };
                float content = measure(client), max = Math.Max(0, content - view);
                float offset = Math.Clamp(b.ScrollOffsets.GetValueOrDefault(w.Name), 0, max);
                result[ci] = client with
                {
                    Properties = new Dictionary<string, string>(client.Properties, StringComparer.Ordinal) { ["TextScroll"] = N(offset) }
                };
                LayoutBar(result, b, barWidget, true, max, offset, view / content, ScrollViewPage * b.TemplateScale, view,
                    ScrollViewWheel * b.TemplateScale, true, w.Name, w.Name, false, true);

                props["ScrollKey"] = w.Name;
                props["ScrollMax"] = N(max);
                props["ScrollWheel"] = N(ScrollViewWheel * b.TemplateScale);
                return;
            }
        }

        RemoveBar(result, bi);
        if ((ci = IndexOfPart(result, w, "Client")) >= 0)
            result[ci] = result[ci] with { Bounds = result[ci].Bounds with { Width = result[ci].Bounds.Width + widen } };
    }

    /// <summary>
    /// A ListBox's rows (Verified against MyGUI 3.2.2 ListBox.cpp): <c>HeightLine</c> tall, scrolled in pixels; the VScroll shows when the lines
    /// overflow the client by a pixel or more (hidden: the client takes its width), its thumb is the visible share of the track, its page and view
    /// page one line.
    /// </summary>
    static void ListRows(List<UiControl> result, UiBindings b, MyGuiWidget w, Dictionary<string, string> props, UiRect bounds, UiRect clip,
        bool enabled, string sel, string scrollKey, IReadOnlyList<UiListItem> items, int[] order, int column, bool showBar, string rowAction)
    {
        int ci = IndexOfPart(result, w, "Client");
        var area = ci >= 0 ? result[ci].Bounds : bounds;
        // A client-set LineHeight (pixels) wins over the skin's HeightLine (an ItemBox's cells are the client's size, the jobs list's
        // int(24 x height / 1080)).
        float line = LineHeight(props) ?? HeightLine(w, b);
        float content = items.Count * line, view = area.Height, max = content - view >= 1 ? content - view : 0;
        var barWidget = PartOf(w, "VScroll");
        int bi = barWidget is null ? -1 : result.FindLastIndex(c => c.Name == barWidget.Name);
        float offset = Math.Clamp(b.ScrollOffsets.GetValueOrDefault(scrollKey), 0, max);
        if (bi >= 0)
        {
            if (max > 0 && showBar)
            {
                LayoutBar(result, b, barWidget!, true, max, offset, view / content, line, line, line, true, scrollKey, scrollKey, false, true);
            }
            else
            {
                float widen = result[bi].Bounds.Width;
                RemoveBar(result, bi);
                if ((ci = IndexOfPart(result, w, "Client")) >= 0)
                {
                    area = result[ci].Bounds with { Width = result[ci].Bounds.Width + widen };
                    result[ci] = result[ci] with { Bounds = area };
                }
            }
        }

        var rowClip = area.Intersect(clip);
        int selected = b.SelectedItems.GetValueOrDefault(sel, -1);
        string ink = props.GetValueOrDefault("TextColour") ?? MyGuiColours.InkText, count = items.Count.ToString(Inv);
        // Lines of the list's SkinLine (MyGuiTemplates: LineSkin, LineFont) draw as that skin, the selected one in its checked state; without one, a
        // bar marks the selection.
        string? lineSkin = props.GetValueOrDefault("LineSkin");
        for (int d = 0; d < order.Length; d++)
        {
            int source = order[d];
            // A line of the list's SkinLine spans the client and its skin places the text (MyGUI ListBox; the base game's save list: 6 px in at 1440
            // lines); without one, Meitou's 3 px inset and text padding.
            var row = lineSkin is null
                ? new UiRect(area.X + 3, area.Y + d * line - offset, Math.Max(0, area.Width - 6), line)
                : new UiRect(area.X, area.Y + d * line - offset, area.Width, line);
            if (row.Intersect(rowClip).Height <= 0) continue;
            var rowProperties = new Dictionary<string, string>
            {
                ["ListOwner"] = scrollKey,
                ["ListCount"] = count,
                ["FontName"] = props.GetValueOrDefault("LineFont") ?? "Kenshi_StandardFont_Medium",
                ["TextAlign"] = "Left VCenter",
            };
            if (lineSkin is null) rowProperties["TextPadding"] = "4 2 4 2";
            if (lineSkin is null || props.ContainsKey("TextColour")) rowProperties["TextColour"] = ink;
            if (lineSkin is not null && selected == source) rowProperties["Checked"] = "true";
            result.Add(new(w.Name + "@row" + source, "Button", row, CellOf(items[source], column), $"{rowAction}:{sel}:{source}", lineSkin ?? "",
                enabled, rowProperties, lineSkin is null && selected == source ? new(0.25f, 0.24f, 0.16f, 0.95f) : default, Argument: items[source].Id,
                Clip: rowClip));
        }

        props["ListKey"] = sel;
        props["ScrollKey"] = scrollKey;
        props["ScrollMax"] = N(max);
        props["ScrollWheel"] = N(line);
        props["ListCount"] = count;
        props["ListLine"] = N(line);
        props["ListView"] = N(view);
    }

    /// <summary>
    /// A ScrollBar's parts (Verified against MyGUI 3.2.2 ScrollBar.cpp): the thumb (<c>Track</c>) is <paramref name="fraction"/> of the
    /// <c>TrackPlace</c> (the skin's size without one), at least <c>MinTrackSize</c>, and sits at <c>(line - thumb) * position / max</c>; with no
    /// range, or no room, it is hidden and <c>FirstPart</c> / <c>SecondPart</c> take half of the line each. Start, End, the thumb and the two parts
    /// get <c>mygui:bar:name:part</c> actions; the bar's control carries the <c>Bar*</c> markers the host drives it by. A null <paramref name="pos"/>
    /// is the position of the skin's own thumb.
    /// </summary>
    static void LayoutBar(List<UiControl> result, UiBindings b, MyGuiWidget bar, bool vertical, float max, float? pos, float? fraction, float page,
        float viewPage, float wheel, bool owned, string key, string logical, bool moveToClick, bool repeat)
    {
        int bi = result.FindLastIndex(c => c.Name == bar.Name);
        if (bi < 0) return;

        bool on = result[bi].Enabled;   // the template's part buttons are disabled by the screens' default; the bar's own state rules them
        var (place, track, first, second) = BarParts(result, bi, bar, on);
        max = Math.Max(0, max);
        float position = pos is { } given ? Math.Clamp(given, 0, max) : 0;
        if (place >= 0)
        {
            var pl = result[place].Bounds;
            float length = vertical ? pl.Height : pl.Width;
            float skin = track >= 0 ? vertical ? result[track].Bounds.Height : result[track].Bounds.Width : 0;
            float minimum = (PartOf(bar, "Track") is { } trackWidget && trackWidget.UserStrings.TryGetValue("MinTrackSize", out _)
                ? Num(trackWidget.UserStrings, "MinTrackSize")
                : Num(bar.UserStrings, "MinTrackSize")) * b.TemplateScale;
            float thumb = Math.Max(fraction is { } f ? length * Math.Clamp(f, 0, 1) : skin, minimum);
            if (pos is null && track >= 0 && length - thumb > 0.5f && max > 0)
            {
                float at = vertical ? result[track].Bounds.Y - pl.Y : result[track].Bounds.X - pl.X;
                position = MathF.Round(Math.Clamp(at / (length - thumb), 0, 1) * max);
            }
            bool has = track >= 0 && max > 0 && length - thumb > 0.5f;
            float off = has ? (length - thumb) * position / max : 0;
            UiRect Slice(float from, float size) =>
                vertical ? new(pl.X, pl.Y + from, pl.Width, Math.Max(0, size)) : new(pl.X + from, pl.Y, Math.Max(0, size), pl.Height);
            if (first >= 0)
            {
                result[first] = result[first] with
                {
                    Bounds = has ? Slice(0, off) : Slice(0, length / 2),
                    Action = $"mygui:bar:{bar.Name}:first",
                    Pickable = true,
                    Enabled = on
                };
            }
            if (second >= 0)
            {
                result[second] = result[second] with
                {
                    Bounds = has ? Slice(off + thumb, length - off - thumb) : Slice(length / 2, length - length / 2),
                    Action = $"mygui:bar:{bar.Name}:second",
                    Pickable = true,
                    Enabled = on
                };
            }
            if (track >= 0)
            {
                var t = result[track].Bounds;
                result[track] = result[track] with
                {
                    Bounds = !has ? default
                        : vertical ? new(t.X, pl.Y + off, t.Width, thumb)
                        : new(pl.X + off, t.Y, thumb, t.Height),
                    Action = $"mygui:bar:{bar.Name}:track",
                    Pickable = true,
                    Enabled = on
                };
            }
        }
        if (result[bi].Properties is not Dictionary<string, string> markers) return;

        markers["BarKey"] = key;
        markers["BarName"] = logical;
        markers["BarMax"] = N(max);
        markers["BarPos"] = N(position);
        markers["BarPage"] = N(page);
        markers["BarViewPage"] = N(viewPage);
        markers["BarWheel"] = N(wheel);
        markers["BarVertical"] = vertical ? "true" : "false";
        markers["BarOwned"] = owned ? "true" : "false";
        markers["BarMove"] = moveToClick ? "true" : "false";
        markers["BarRepeat"] = repeat ? "true" : "false";
    }

    /// <summary>
    /// The indices of a bar's TrackPlace, Track, FirstPart and SecondPart among the controls after it (-1 for a part it lacks); its Start and End
    /// get their step actions on the way.
    /// </summary>
    static (int Place, int Track, int First, int Second) BarParts(List<UiControl> result, int bi, MyGuiWidget bar, bool on)
    {
        string prefix = bar.Name + "@";
        int place = -1, track = -1, first = -1, second = -1;
        for (int j = bi + 1; j < result.Count && result[j].Name.StartsWith(prefix, StringComparison.Ordinal); j++)
        {
            if (result[j].Properties.GetValueOrDefault("TemplateOwner") != bar.Name) continue;
            switch (result[j].Properties.GetValueOrDefault("TemplatePart"))
            {
                case "TrackPlace":
                    place = j;
                    break;
                case "Track":
                    track = j;
                    break;
                case "FirstPart":
                    first = j;
                    break;
                case "SecondPart":
                    second = j;
                    break;
                case "Start":
                    result[j] = result[j] with { Action = $"mygui:bar:{bar.Name}:start", Pickable = true, Enabled = on };
                    break;
                case "End":
                    result[j] = result[j] with { Action = $"mygui:bar:{bar.Name}:end", Pickable = true, Enabled = on };
                    break;
            }
        }
        return (place, track, first, second);
    }

    /// <summary>
    /// Removes the bar at <paramref name="bi"/> and its parts (the controls after it named <c>bar@...</c>).
    /// </summary>
    static void RemoveBar(List<UiControl> result, int bi)
    {
        string prefix = result[bi].Name + "@";
        int n = 1;
        while (bi + n < result.Count && result[bi + n].Name.StartsWith(prefix, StringComparison.Ordinal)) n++;
        result.RemoveRange(bi, n);
    }

    /// <summary>
    /// A ScrollBar that no widget drives (a slider): positions run 0 to <c>Range</c> - 1, the host keeps the position
    /// (<see cref="UiBindings.ScrollOffsets"/>, else <c>RangePosition</c>, else the skin's thumb).
    /// </summary>
    static void FreeBar(List<UiControl> result, UiBindings b, MyGuiWidget w, Dictionary<string, string> props)
    {
        float? pos = b.ScrollOffsets.TryGetValue(w.Name, out float stored) ? stored
            : props.ContainsKey("RangePosition") ? Num(props, "RangePosition")
            : null;
        string logical = w.Properties.GetValueOrDefault("TemplatePart") == "Slider" && w.Properties.TryGetValue("TemplateOwner", out var owner)
            ? owner
            : w.Name;
        bool repeat = (props.GetValueOrDefault("Repeat") ?? props.GetValueOrDefault("RepeatEnabled")) != "false";
        LayoutBar(result, b, w, props.GetValueOrDefault("VerticalAlignment") != "false", Num(props, "Range") - 1, pos, null, Num(props, "Page", 1),
            Num(props, "ViewPage", 1), Num(props, "WheelPage", 1), false, w.Name, logical, props.GetValueOrDefault("MoveToClick") == "true", repeat);
    }

    /// <summary>
    /// A ScrollView (Verified against MyGUI 3.2.2 ScrollView.cpp / ScrollViewBase.cpp): the canvas (<c>CanvasSize</c>, or
    /// <see cref="UiBindings.Canvas"/>) scrolls inside the template's <c>Client</c>; a bar shows when the canvas overflows the client on its axis and
    /// the <c>VisibleVScroll</c> / <c>VisibleHScroll</c> flag allows, each shown bar takes its thickness from the client (the template is laid out
    /// with both bars shown), the range is canvas minus client, a smaller canvas sits by <c>CanvasAlign</c> (centred by default), the bar page is 16
    /// px, the wheel 50 px. MyGUI re-reads the sizes only once per update; Meitou iterates the rule to its fixed point.
    /// </summary>
    static void VisitScrollView(List<UiControl> result, UiBindings b, MyGuiWidget w, Dictionary<string, string> props, UiRect bounds,
        UiRect childClip, bool enabled, float alpha, VisitWidget visit)
    {
        int mark = result.Count;
        foreach (var child in w.Children) visit(child, bounds, childClip, enabled, alpha);
        var clientWidget = PartOf(w, "Client");
        if (clientWidget is null) return;
        // A host-scrolled view (NativeScroll) lays out as one with nothing to scroll: no bars, the client in their place.
        bool native = b.NativeScroll.Contains(w.Name);
        int ci = result.FindLastIndex(c => c.Name == clientWidget.Name);
        if (ci < 0) return;

        var vWidget = PartOf(w, "VScroll");
        var hWidget = PartOf(w, "HScroll");
        int vi = vWidget is null ? -1 : result.FindLastIndex(c => c.Name == vWidget.Name);
        int hi = hWidget is null ? -1 : result.FindLastIndex(c => c.Name == hWidget.Name);
        float scale = b.TemplateScale;
        var (cw, ch) = CanvasSize(b, w, props, scale);
        if (native) (cw, ch) = (0, 0);
        UiRect c0 = result[ci].Bounds, v0 = vi >= 0 ? result[vi].Bounds : default, h0 = hi >= 0 ? result[hi].Bounds : default;
        bool flagV = props.GetValueOrDefault("VisibleVScroll") != "false", flagH = props.GetValueOrDefault("VisibleHScroll") != "false";
        var fit = FitScrollBars(c0, v0, h0, vi >= 0, hi >= 0, flagV, flagH, cw, ch);
        var client = fit.Client;

        float canvasW = cw > 0 ? cw : client.Width, canvasH = ch > 0 ? ch : client.Height;
        float vRange = Math.Max(0, canvasH - client.Height), hRange = Math.Max(0, canvasW - client.Width);
        string align = props.GetValueOrDefault("CanvasAlign") ?? "Center";
        bool left = align.Contains("Left") || align.Contains("Default"), right = align.Contains("Right");
        bool top = align.Contains("Top") || align.Contains("Default"), bottom = align.Contains("Bottom");
        static float Aligned(float content, float view, bool near, bool far) => near ? 0 : far ? content - view : (content - view) / 2;
        float sy = vRange > 0 ? Math.Clamp(b.ScrollOffsets.GetValueOrDefault(w.Name), 0, vRange) : Aligned(canvasH, client.Height, top, bottom);
        float sx = hRange > 0 ? Math.Clamp(b.ScrollOffsets.GetValueOrDefault(w.Name + "@h"), 0, hRange) : Aligned(canvasW, client.Width, left, right);
        var origin = new UiRect(client.X - sx, client.Y - sy, canvasW, canvasH);
        if (client != c0 || fit.V != v0 || fit.H != h0 || origin != client)
            Relayout(result, b, w, mark, clientWidget, vWidget, hWidget, fit, origin, bounds, childClip, enabled, alpha, visit);
        if (native) return;

        props["ScrollKey"] = w.Name;
        props["ScrollMax"] = N(vRange);
        props["ScrollWheel"] = N(ScrollViewWheel * scale);
        props["ScrollKeyH"] = w.Name + "@h";
        props["ScrollMaxH"] = N(hRange);
        props["ScrollWheelH"] = N(ScrollViewWheel * scale);
        props["CanvasX"] = N(origin.X);
        props["CanvasY"] = N(origin.Y);

        float page = ScrollViewPage * scale;
        if (vWidget is not null && fit.VShown)
        {
            LayoutBar(result, b, vWidget, true, vRange, vRange > 0 ? sy : 0, ch > 0 ? client.Height / ch : null, page, Math.Max(client.Height, page),
                ScrollViewWheel * scale, true, w.Name, w.Name, false, true);
        }
        if (hWidget is not null && fit.HShown)
        {
            LayoutBar(result, b, hWidget, false, hRange, hRange > 0 ? sx : 0, cw > 0 ? client.Width / cw : null, page, Math.Max(client.Width, page),
                ScrollViewWheel * scale, true, w.Name + "@h", w.Name, false, true);
        }
    }

    /// <summary>
    /// A ScrollView's canvas in pixels: bound, else its <c>CanvasSize</c> at the template scale, else none (0 x 0).
    /// </summary>
    static (float Width, float Height) CanvasSize(UiBindings b, MyGuiWidget w, Dictionary<string, string> props, float scale)
    {
        float cw = 0, ch = 0;
        if (b.Canvas.TryGetValue(w.Name, out var canvas)) (cw, ch) = canvas;
        else if (props.TryGetValue("CanvasSize", out var size) && size.Split(' ', StringSplitOptions.RemoveEmptyEntries) is { Length: 2 } xy
            && float.TryParse(xy[0], NumberStyles.Float, Inv, out float sw) && float.TryParse(xy[1], NumberStyles.Float, Inv, out float sh))
            (cw, ch) = (sw * scale, sh * scale);
        return (cw, ch);
    }

    /// <summary>
    /// The client and bar rectangles once each bar shows exactly when the canvas overflows the client on its axis: a shown bar's thickness comes off
    /// the client and the other bar's length (from the template's layout, <paramref name="c0"/>, <paramref name="v0"/>, <paramref name="h0"/>, with
    /// both bars shown).
    /// </summary>
    static (UiRect Client, UiRect V, UiRect H, bool VShown, bool HShown) FitScrollBars(UiRect c0, UiRect v0, UiRect h0, bool vBar, bool hBar,
        bool flagV, bool flagH, float cw, float ch)
    {
        bool vShown = vBar, hShown = hBar;
        float clientW = c0.Width, clientH = c0.Height, vHeight = v0.Height, hWidth = h0.Width;
        for (int pass = 0; pass < 4; pass++)
        {
            bool wantV = vBar && flagV && ch > 0 && ch > clientH + 0.01f, wantH = hBar && flagH && cw > 0 && cw > clientW + 0.01f;
            if (wantV == vShown && wantH == hShown) break;
            if (wantV != vShown)
            {
                vShown = wantV;
                float d = vShown ? -v0.Width : v0.Width;
                clientW += d;
                hWidth += d;
            }
            if (wantH != hShown)
            {
                hShown = wantH;
                float d = hShown ? -h0.Height : h0.Height;
                clientH += d;
                vHeight += d;
            }
        }
        var client = new UiRect(c0.X, c0.Y, Math.Max(0, clientW), Math.Max(0, clientH));
        var vRect = new UiRect(v0.X, v0.Y, v0.Width, Math.Max(0, vHeight));
        var hRect = new UiRect(h0.X, h0.Y, Math.Max(0, hWidth), h0.Height);
        return (client, vRect, hRect, vShown, hShown);
    }

    /// <summary>
    /// Lays a ScrollView's children out again from <paramref name="mark"/> with the fitted client and bars and the canvas at
    /// <paramref name="origin"/>: the parts are positioned or hidden through the bindings for the visit, which are restored after it.
    /// </summary>
    static void Relayout(List<UiControl> result, UiBindings b, MyGuiWidget w, int mark, MyGuiWidget clientWidget, MyGuiWidget? vWidget,
        MyGuiWidget? hWidget, (UiRect Client, UiRect V, UiRect H, bool VShown, bool HShown) fit, UiRect origin, UiRect bounds, UiRect childClip,
        bool enabled, float alpha, VisitWidget visit)
    {
        result.RemoveRange(mark, result.Count - mark);
        var undo = new List<Action>();
        void Place(MyGuiWidget? part, UiRect rect, bool shown)
        {
            if (part is null) return;
            if (shown)
            {
                bool had = b.Positions.ContainsKey(part.Name);
                b.Positions[part.Name] = rect;
                if (!had) undo.Add(() => b.Positions.Remove(part.Name));
            }
            else if (b.Hidden.Add(part.Name)) undo.Add(() => b.Hidden.Remove(part.Name));
        }
        Place(clientWidget, fit.Client, true);
        Place(vWidget, fit.V, fit.VShown);
        Place(hWidget, fit.H, fit.HShown);

        var clientProps = new Dictionary<string, string>(b.Properties.GetValueOrDefault(clientWidget.Name) ?? new Dictionary<string, string>(),
            StringComparer.Ordinal)
        {
            ["CanvasOrigin"] = $"{N(origin.X)} {N(origin.Y)} {N(origin.Width)} {N(origin.Height)}",
        };
        var previous = b.Properties.GetValueOrDefault(clientWidget.Name);
        b.Properties[clientWidget.Name] = clientProps;
        foreach (var child in w.Children) visit(child, bounds, childClip, enabled, alpha);
        if (previous is null) b.Properties.Remove(clientWidget.Name);
        else b.Properties[clientWidget.Name] = previous;
        foreach (var action in undo) action();
    }

    /// <summary>
    /// A ComboBox (Verified against MyGUI 3.2.2 ComboBox.cpp): the arrow and the client toggle the popup; the popup is the template's <c>List</c>, as
    /// wide as the combo, as tall as its lines (capped by <c>MaxListLength</c>), below the combo or above when it does not fit; the client shows the
    /// selected item. Items are <see cref="UiBindings.Lists"/>[name], the selection <see cref="UiBindings.SelectedItems"/>.
    /// </summary>
    static void Combo(List<UiControl> result, UiBindings b, MyGuiWidget w, Dictionary<string, string> props, UiRect bounds, bool enabled,
        UiRect viewport, VisitWidget visit, List<Action> deferred)
    {
        var items = b.Lists.GetValueOrDefault(w.Name) ?? [];
        int selected = b.SelectedItems.GetValueOrDefault(w.Name, -1);
        string text = selected >= 0 && selected < items.Count ? items[selected].Caption : b.Coded(w.Name) ?? "";
        foreach (string part in new[] { "Button", "Client" })
        {
            if (IndexOfPart(result, w, part) is var i and >= 0)
            {
                result[i] = result[i] with
                {
                    Action = $"mygui:combo:{w.Name}:toggle", Pickable = true, Enabled = enabled, Caption = part == "Client" ? text : result[i].Caption
                };
            }
        }
        props["ListKey"] = w.Name;
        props["ComboCount"] = items.Count.ToString(Inv);
        props["ComboSelected"] = selected.ToString(Inv);

        var list = w.Children.FirstOrDefault(c => c.Properties.GetValueOrDefault("TemplatePart") == "List");
        if (b.OpenCombo != w.Name || items.Count == 0 || list is null) return;
        float scale = b.TemplateScale;
        float line = HeightLine(list, b);
        var client = PartOf(list, "Client");
        float frame = client is null ? 0 : Math.Max(0, Num(client.Properties, "TemplateReferenceHeight") - client.Position.Height) * scale;
        float length = items.Count * line + frame, cap = Num(props, "MaxListLength", -1) * scale;
        if (cap > 0) length = Math.Min(length, cap);
        length = Math.Min(length, viewport.Height);
        float y = bounds.Y + bounds.Height;
        if (y + length > viewport.Height) y = bounds.Y - length >= 0 ? bounds.Y - length : Math.Max(0, viewport.Height - length);
        var properties = new Dictionary<string, string>(list.Properties, StringComparer.Ordinal)
        {
            ["ListSource"] = w.Name,
            ["ListSel"] = w.Name,
            ["ListScroll"] = w.Name + "@popup",
            ["RowAction"] = "mygui:combo",
            ["ListBar"] = "true",
        };
        properties.Remove("Visible");
        properties.Remove("TemplateReferenceWidth");
        properties.Remove("TemplateReferenceHeight");
        var popup = list with { Position = new(bounds.X, y, bounds.Width, length), RealPosition = false, Properties = properties };
        deferred.Add(() => visit(popup, viewport, viewport, enabled, 1));
    }

    /// <summary>
    /// A MultiListBox with bound columns (Verified against MyGUI 3.2.2 MultiListBox.cpp): per column a header button (<c>SkinButton</c>, in
    /// <c>HeaderPlace</c>, caption, sort arrow image Up / Down / None) and a sub-list (<c>SkinList</c>, in <c>Client</c>); Fixed columns keep their
    /// width, Fill columns share the rest; the sub-lists share one selection and scroll position, and only the last shows its bar.
    /// </summary>
    static void MultiColumns(List<UiControl> result, UiBindings b, MyGuiWidget w, Dictionary<string, string> props, bool enabled, float alpha,
        VisitWidget visit)
    {
        var columns = b.Columns[w.Name];
        int hi = IndexOfPart(result, w, "HeaderPlace"), ci = IndexOfPart(result, w, "Client");
        var buttonPrototype = w.Children.FirstOrDefault(c => c.Properties.GetValueOrDefault("ColumnPrototype") == "button");
        var listPrototype = w.Children.FirstOrDefault(c => c.Properties.GetValueOrDefault("ColumnPrototype") == "list");
        if (hi < 0 || ci < 0 || buttonPrototype is null || listPrototype is null) return;

        var header = result[hi];
        var client = result[ci];
        var headerClip = header.Bounds.Intersect(header.Clip ?? header.Bounds);
        var clientClip = client.Bounds.Intersect(client.Clip ?? client.Bounds);
        float scale = b.TemplateScale, used = columns.Where(c => !c.Fill).Sum(c => c.Width * scale);
        int fills = columns.Count(c => c.Fill), lastFill = -1;
        for (int i = 0; i < columns.Count; i++) if (columns[i].Fill) lastFill = i;
        float share = fills > 0 ? MathF.Floor(Math.Max(0, client.Bounds.Width - used) / fills) : 0;
        float rest = fills > 0 ? Math.Max(0, client.Bounds.Width - used) - share * fills : 0;
        var (sortColumn, ascending) = b.Sorts.TryGetValue(w.Name, out var sort) ? sort : (-1, true);
        float x = 0;
        for (int i = 0; i < columns.Count; i++)
        {
            float width = columns[i].Fill ? share + (i == lastFill ? rest : 0) : columns[i].Width * scale;
            string name = $"{w.Name}@column{i}";
            var button = CloneAs(buttonPrototype, name + "-header", new(x, 0, width, header.Bounds.Height), null);
            b.Text[button.Name] = columns[i].Caption;
            b.Actions[button.Name] = $"mygui:sort:{w.Name}:{i}";
            if (button.DescendantsAndSelf().FirstOrDefault(n => n.Properties.GetValueOrDefault("TemplatePart") == "Image") is { } image)
            {
                b.Properties[image.Name] = new Dictionary<string, string>(
                    b.Properties.GetValueOrDefault(image.Name) ?? new Dictionary<string, string>(), StringComparer.Ordinal)
                {
                    ["ImageName"] = i == sortColumn ? ascending ? "Up" : "Down" : "None"
                };
            }
            visit(button, header.Bounds, headerClip, enabled, alpha);
            var list = CloneAs(listPrototype, name + "-list", new(x, 0, width, client.Bounds.Height), new Dictionary<string, string>
            {
                ["ListSource"] = w.Name,
                ["ListSel"] = w.Name,
                ["ListScroll"] = w.Name,
                ["ListColumn"] = i.ToString(Inv),
                ["ListBar"] = i == columns.Count - 1 ? "true" : "false",
            });
            visit(list, client.Bounds, clientClip, enabled, alpha);
            x += width;
        }
        props["ListKey"] = w.Name;
    }

    /// <summary>
    /// A prototype widget (and its expanded parts) renamed to <paramref name="name"/> and placed at <paramref name="position"/> (pixels in its
    /// parent), with extra properties on the root.
    /// </summary>
    static MyGuiWidget CloneAs(MyGuiWidget prototype, string name, UiRect position, IReadOnlyDictionary<string, string>? extra)
    {
        string from = prototype.Name;
        string Rename(string s) => s.StartsWith(from, StringComparison.Ordinal) ? name + s[from.Length..] : s;
        MyGuiWidget Go(MyGuiWidget n, bool root)
        {
            var p = new Dictionary<string, string>(n.Properties, StringComparer.Ordinal);
            foreach (string key in new[] { "TemplateOwner", "CaptionOwner" }) if (p.TryGetValue(key, out var v)) p[key] = Rename(v);
            if (root)
            {
                p.Remove("Visible");
                p.Remove("ColumnPrototype");
                if (extra is not null) foreach (var e in extra) p[e.Key] = e.Value;
            }
            var cloned = n with { Name = Rename(n.Name), Properties = p, Children = n.Children.Select(c => Go(c, false)).ToArray() };
            return root ? cloned with { Position = position, RealPosition = false } : cloned;
        }
        return Go(prototype, true);
    }
}