using System.Numerics;

namespace Meitou.Data.Ui;

/// <summary>
/// A list row; <paramref name="Cells"/> are its per-column texts in a MultiListBox (null: the caption is the first column).
/// </summary>
/// <summary>
/// A list row; <paramref name="SortKeys"/>, per column, what a MultiListBox sorts it by instead of its shown cells (a date by its time, as the save
/// list's comparator).
/// </summary>
public sealed record UiListItem(string Id, string Caption, IReadOnlyList<string>? Cells = null, IReadOnlyList<string>? SortKeys = null);

public sealed record UiControl(string Name, string Type, UiRect Bounds, string Caption, string Action,
    string Skin, bool Enabled, IReadOnlyDictionary<string, string> Properties, Vector4 Background,
    Vector4? TextColour = null, Vector4? HoverColour = null, Vector4? ActiveColour = null, Vector4? DisabledColour = null,
    bool? Pickable = null, string Argument = "", float? TextScale = null, UiRect? Clip = null);

/// <summary>
/// A frame shared by both frontends; bindings supply data and visibility without changing source documents.
/// </summary>
public sealed partial class UiBindings
{
    /// <summary>
    /// Scale pixel-based template chrome along with viewport-relative layouts.
    /// </summary>
    public float TemplateScale { get; set; } = 1;
    public Dictionary<string, string> Text { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// The text the code set for <paramref name="key"/>, in the active language when it is a message of the game's catalog (the original sets such
    /// captions through gettext; a layout's own captions stay as written). Null when the code set nothing.
    /// </summary>
    public string? Coded(string key) => Text.TryGetValue(key, out var text) ? Loc.Tr(text) : null;
    public Dictionary<string, string> Actions { get; } = new(StringComparer.Ordinal);
    public HashSet<string> Hidden { get; } = new(StringComparer.Ordinal);
    public HashSet<string> Disabled { get; } = new(StringComparer.Ordinal);
    public HashSet<MyGuiWidget> HiddenWidgets { get; } = [];
    public Dictionary<string, UiRect> Positions { get; } = new(StringComparer.Ordinal);
    /// <summary>
    /// Parent-relative runtime geometry, including anonymous layout panels.
    /// </summary>
    public Dictionary<MyGuiWidget, UiRect> RelativePositions { get; } = [];
    public Dictionary<string, int> SelectedTabs { get; } = new(StringComparer.Ordinal);
    /// <summary>
    /// A TabControl whose buttons are made at run time (one per player squad): their captions; the selected button shows the first page (MyGUI adds
    /// TabItems so).
    /// </summary>
    public Dictionary<string, IReadOnlyList<string>> TabCaptions { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, float> Progress { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> States { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, IReadOnlyDictionary<string, string>> Properties { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, IReadOnlyList<UiListItem>> Lists { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> SelectedItems { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, float> ScrollOffsets { get; } = new(StringComparer.Ordinal);
}

public static partial class MyGuiPresentation
{
    public static IReadOnlyList<UiControl> Arrange(MyGuiLayout layout, int width, int height, UiBindings bindings, IReadOnlyList<string> layers)
    {
        var result = new List<UiControl>();
        var deferred = new List<Action>();   // popups (combo lists), visited after every root so they draw last
        void Visit(MyGuiWidget w, UiRect parent, UiRect clip, bool parentEnabled = true, float parentAlpha = 1)
        {
            if (bindings.Hidden.Contains(w.Name) || bindings.HiddenWidgets.Contains(w)) return;
            var props = new Dictionary<string, string>(w.Properties, StringComparer.Ordinal);
            if (bindings.Properties.TryGetValue(w.Name, out var overrides))
                foreach (var p in overrides) props[p.Key] = p.Value;
            if (props.GetValueOrDefault("Visible") == "false") return;
            // MyGUI multiplies a widget's Alpha into its children unless they set InheritsAlpha false (an EditBox's text is its template client's).
            float alpha = (props.GetValueOrDefault("InheritsAlpha") == "false" ? 1 : parentAlpha)
                * (float.TryParse(
                    props.GetValueOrDefault("Alpha"),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out float own
                ) ? Math.Clamp(own, 0, 1) : 1);
            if (alpha < 1) props["Alpha"] = alpha.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var bounds = bindings.Positions.TryGetValue(w.Name, out var positioned) ? positioned
                : bindings.RelativePositions.TryGetValue(
                    w,
                    out var relative
                ) ? relative.RelativeTo(parent, true) : w.RealPosition ? w.Position.RelativeTo(parent, true)
                : props.TryGetValue("TemplateReferenceWidth", out var rw) && props.TryGetValue("TemplateReferenceHeight", out var rh)
                    ? new UiRect(
                        w.Position.X * bindings.TemplateScale,
                        w.Position.Y * bindings.TemplateScale,
                        w.Position.Width * bindings.TemplateScale,
                        w.Position.Height * bindings.TemplateScale
                    )
                        .AlignTo(
                            parent,
                            float.Parse(rw, System.Globalization.CultureInfo.InvariantCulture) * bindings.TemplateScale,
                            float.Parse(rh, System.Globalization.CultureInfo.InvariantCulture) * bindings.TemplateScale,
                            w.Align
                        )
                    : w.Position.RelativeTo(parent, false);
            // A ProgressBar template (TrackSkin) keeps its frame; its fill is added in TrackPlace below. Any other bound widget is cut to the
            // fraction.
            bool progress = bindings.Progress.TryGetValue(w.Name, out float fraction);
            string? trackSkin = progress ? w.UserStrings.GetValueOrDefault("TrackSkin") : null;
            if (progress && trackSkin is null) bounds = bounds with { Width = bounds.Width * Math.Clamp(fraction, 0, 1) };
            // A caption written in the layout is the source string of the player's language (the layouts say "SKILLS", the catalog key is "Skills").
            // MyGUI's plain Widget has no text: it ignores a layout Caption (the character trading window's TradePanel says "Character Selection").
            string layoutCaption = w.Type == "Widget" ? "" : props.GetValueOrDefault("Caption") ?? "";
            string caption = bindings.Coded(w.Name) ?? PartText(bindings, props) ?? Loc.TrCaption(layoutCaption);
            if (props.TryGetValue("CaptionOwner", out string? owner))
            {
                caption = bindings.Coded(owner) ?? Loc.TrCaption(layout.Find(owner)?.Properties.GetValueOrDefault("Caption") ?? "");
                // An EditBox's WordWrap is its text's, which the template's client draws.
                if (
                    !props.ContainsKey("WordWrap")
                    && ((bindings.Properties.GetValueOrDefault(owner)?.GetValueOrDefault("WordWrap"))
                            ?? layout.Find(owner)?.Properties.GetValueOrDefault("WordWrap")) is { } wrap
                )
                    props["WordWrap"] = wrap;
                // So is its TextAlign (MyGUI EditBox::setTextAlign aligns the client's text): the editor's NameText is centred.
                if (((bindings.Properties.GetValueOrDefault(owner)?.GetValueOrDefault("TextAlign"))
                        ?? layout.Find(owner)?.Properties.GetValueOrDefault("TextAlign")) is { } textAlign
                    && layout.Find(owner)?.Type == "EditBox")
                    props["TextAlign"] = textAlign;
            }
            // A TabItem's caption is its tab button's, never drawn on the page.
            if (props.GetValueOrDefault("SkipCaption") == "true" || w.Type == "TabItem") caption = "";
            string action = bindings.Actions.GetValueOrDefault(w.Name) ?? "";
            if (props.TryGetValue("TemplateEvent", out string? ev) && props.TryGetValue("TemplateOwner", out var target))
                action = bindings.Actions.GetValueOrDefault(target + "@" + ev) ?? $"mygui:{ev}:{target}";
            if (bindings.States.TryGetValue(w.Name, out var state)) props["State"] = state;
            bool enabled = parentEnabled && !bindings.Disabled.Contains(w.Name) && props.GetValueOrDefault("Enabled") != "false";
            bool pickable = action.Length > 0 || w.Type is "Button" or "Window" or "ListBox" or "MultiListBox" or "ScrollBar" or "ComboBox"
                    or "ScrollView" || w.Type == "EditBox" && props.GetValueOrDefault("Static") != "true" || w.Skin.Length > 0
                            && w.Skin != "PanelEmpty" && w.Type != "TextBox";
            result.Add(new(w.Name, w.Type, bounds, caption, action, w.Skin, enabled, props, default, Pickable: pickable, Clip: clip));
            var childClip = props.GetValueOrDefault("Clipped") == "false" ? clip : bounds.Intersect(clip);
            if (w.Type == "TabControl")
            {
                var pages = w.Children.Where(
                    c => c.Type == "TabItem"
                    && !bindings.Hidden.Contains(c.Name)
                    && !bindings.HiddenWidgets.Contains(c)
                ).ToArray();
                var captions = bindings.TabCaptions.GetValueOrDefault(w.Name);
                int buttons = captions?.Count ?? pages.Length;
                int selected = Math.Clamp(bindings.SelectedTabs.GetValueOrDefault(w.Name), 0, Math.Max(0, buttons - 1));
                foreach (var child in w.Children.Where(c => c.Type != "TabItem")) Visit(child, bounds, childClip, enabled, alpha);
                // The buttons go in the skin's HeaderPlace (MyGUI's TabControl part), ButtonWidth (at the template scale) wide unless
                // ButtonAutoWidth.
                var header = result.LastOrDefault(
                    c => c.Properties.GetValueOrDefault("TemplateOwner") == w.Name
                    && c.Properties.GetValueOrDefault("TemplatePart") == "HeaderPlace"
                )?.Bounds
                    ?? new UiRect(bounds.X, bounds.Y, bounds.Width, 30 * bindings.TemplateScale);
                float tabWidth = buttons == 0 ? 0 : header.Width / buttons;
                if (buttons > 0 && props.GetValueOrDefault("ButtonAutoWidth") == "false"
                    && float.TryParse(
                        props.GetValueOrDefault("ButtonWidth"),
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out float fixedWidth
                    ))
                    tabWidth = Math.Min(fixedWidth * bindings.TemplateScale, tabWidth);
                // The selected tab is Checked, not a fixed State, so the painter shows the skin's hover and pushed states (amber caption under the
                // mouse).
                for (int i = 0; i < buttons; i++)
                    result.Add(new(w.Name + "@tab" + i, "Button", new(header.X + i * tabWidth, header.Y, tabWidth, header.Height),
                        captions?[i] ?? bindings.Coded(w.Name + "@tab" + i)
                            ?? (pages[i].Properties.GetValueOrDefault("Caption") is { Length: > 0 } tabText ? Loc.TrCaption(tabText) : null)
                            ?? pages[i].Name, $"mygui:tab:{w.Name}:{i}", "Kenshi_SquadTabButton1Skin", enabled,
                        new Dictionary<string, string> { ["Checked"] = i == selected ? "true" : "false",
                        ["FontName"] = "Kenshi_PaintedTextFont_Medium", ["TextAlign"] = "Center" }, default, Clip: clip));
                if (pages.Length > 0) Visit(pages[captions is null ? selected : 0], bounds, childClip, enabled, alpha);
            }
            // An ItemBox scrolls like a ScrollView (MyGUI 3.2 ItemBox derives ScrollViewBase): its bars show only when a bound canvas overflows the
            // client.
            else if (w.Type is "ScrollView" or "ItemBox")
                VisitScrollView(result, bindings, w, props, bounds, childClip, enabled, alpha, Visit);
            else foreach (var child in w.Children) Visit(child, ChildParent(props, bounds), childClip, enabled, alpha);
            if (
                trackSkin is not null
                && result.FindLastIndex(
                    c => c.Properties.GetValueOrDefault("TemplateOwner") == w.Name
                    && c.Properties.GetValueOrDefault("TemplatePart") == "TrackPlace"
                ) is var place and >= 0
            )
            {
                // TrackFill (MyGUI ProgressBar): the track runs from the start of TrackPlace to the position, under the frame that follows it.
                var track = result[place].Bounds;
                result.Insert(place + 1, new(w.Name + "@track", "Widget", track with { Width = MathF.Round(track.Width * Math.Clamp(
                    fraction,
                    0,
                    1
                )) }, "", "", trackSkin, enabled,
                    new Dictionary<string, string>(StringComparer.Ordinal), default, Pickable: false, Clip: result[place].Clip));
            }
            AddWidgetContent(result, bindings, w, props, bounds, clip, enabled, alpha, new(0, 0, width, height), Visit, deferred);
        }
        foreach (var root in layout.Roots.OrderBy(w => { int i = layers.IndexOfLayer(w.Layer); return i < 0 ? 0 : i; }))
            Visit(root, new(0, 0, width, height), new(0, 0, width, height));
        for (int i = 0; i < deferred.Count; i++) deferred[i]();
        return result;
    }

    static int IndexOfLayer(this IReadOnlyList<string> layers, string layer)
    {
        for (int i = 0; i < layers.Count; i++) if (layers[i] == layer) return i;
        return -1;
    }

    /// <summary>
    /// Transparent full-screen roots never swallow world input. Reverse order matches painting.
    /// </summary>
    public static UiControl? HitTest(IReadOnlyList<UiControl> controls, float x, float y) => controls.Reverse().FirstOrDefault(c =>
        c.Bounds.Contains(x, y) && (c.Clip is null || c.Clip.Value.Contains(x, y)) && c.Properties.GetValueOrDefault("NeedMouse") != "false" &&
        (c.Pickable ?? (c.Action.Length > 0 || c.Type == "Button" || c.Skin.Length > 0 && c.Skin != "PanelEmpty" || c.Background.W > 0)));
}