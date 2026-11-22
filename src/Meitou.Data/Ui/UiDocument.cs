using System.Globalization;
using System.Numerics;

namespace Meitou.Data.Ui;

/// <summary>
/// A list row; <paramref name="Cells"/> are its per-column texts in a MultiListBox (null: the caption is the first column);
/// <paramref name="SortKeys"/>, per column, what a MultiListBox sorts it by instead of its shown cells (a date by its time, as the save list's
/// comparator). <paramref name="Colour"/> is the row's own text colour (a <c>TextColour</c> value, "r g b" or "#rrggbb"); null: the list's.
/// </summary>
public sealed record UiListItem(string Id, string Caption, IReadOnlyList<string>? Cells = null, IReadOnlyList<string>? SortKeys = null,
    string? Colour = null);

/// <summary>
/// One arranged widget as both frontends paint and hit-test it: its screen rectangle, caption, action, skin, state and properties.
/// </summary>
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

    /// <summary>
    /// Shrink a window to the viewport. Inventory frames retain the size required by their cells, including oversized mod inventories.
    /// </summary>
    public bool FitWindowToViewport { get; set; } = true;

    /// <summary>
    /// A window's base top-left before its kept drag offset; null centres the window. Inventory windows use the original's opening margin.
    /// </summary>
    public Vector2? WindowPosition { get; set; }
    /// <summary>
    /// Captions the code sets, by widget name; <see cref="Coded"/> translates a message of the game's catalog.
    /// </summary>
    public Dictionary<string, string> Text { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// The text the code set for <paramref name="key"/>, in the active language when it is a message of the game's catalog (the original sets such
    /// captions through gettext; a layout's own captions stay as written). Null when the code set nothing.
    /// </summary>
    public string? Coded(string key) => Text.TryGetValue(key, out var text) ? Loc.Tr(text) : null;
    /// <summary>
    /// Each widget's action, by widget name, or by <c>owner@event</c> for a template part's event.
    /// </summary>
    public Dictionary<string, string> Actions { get; } = new(StringComparer.Ordinal);
    /// <summary>
    /// Widgets left out by name, with everything under them.
    /// </summary>
    public HashSet<string> Hidden { get; } = new(StringComparer.Ordinal);
    /// <summary>
    /// Widgets shown disabled by name; their children are disabled too.
    /// </summary>
    public HashSet<string> Disabled { get; } = new(StringComparer.Ordinal);
    /// <summary>
    /// Widgets left out by instance (one of several of a name), with everything under them.
    /// </summary>
    public HashSet<MyGuiWidget> HiddenWidgets { get; } = [];
    /// <summary>
    /// Screen rectangles that replace a widget's layout position, by widget name.
    /// </summary>
    public Dictionary<string, UiRect> Positions { get; } = new(StringComparer.Ordinal);
    /// <summary>
    /// The size a widget of <see cref="Positions"/> was made at, by widget name, when the code sized it since (a window's resize grip; MyGUI's
    /// <c>setSize</c> / <c>setCoord</c>): its children are aligned from that size. Without one, the bound position is where the widget was made, and
    /// its children are made in it.
    /// </summary>
    public Dictionary<string, (float Width, float Height)> MadeSizes { get; } = new(StringComparer.Ordinal);
    /// <summary>
    /// Parent-relative runtime geometry, including anonymous layout panels.
    /// </summary>
    public Dictionary<MyGuiWidget, UiRect> RelativePositions { get; } = [];
    /// <summary>
    /// The selected tab of each TabControl, by widget name; 0 when unset.
    /// </summary>
    public Dictionary<string, int> SelectedTabs { get; } = new(StringComparer.Ordinal);
    /// <summary>
    /// A TabControl whose buttons are made at run time (one per player squad): their captions; the selected button shows the first page (MyGUI adds
    /// TabItems so).
    /// </summary>
    public Dictionary<string, IReadOnlyList<string>> TabCaptions { get; } = new(StringComparer.Ordinal);
    /// <summary>
    /// The filled fraction (0..1) of each bound progress widget, by widget name: a ProgressBar template's fill, else the widget cut to it.
    /// </summary>
    public Dictionary<string, float> Progress { get; } = new(StringComparer.Ordinal);
    /// <summary>
    /// The skin state each widget shows (its <c>State</c> property), by widget name.
    /// </summary>
    public Dictionary<string, string> States { get; } = new(StringComparer.Ordinal);
    /// <summary>
    /// Properties set over each widget's layout ones, by widget name.
    /// </summary>
    public Dictionary<string, IReadOnlyDictionary<string, string>> Properties { get; } = new(StringComparer.Ordinal);
    /// <summary>
    /// The rows of each list widget, by widget name (or by the name its <c>ListSource</c> property gives).
    /// </summary>
    public Dictionary<string, IReadOnlyList<UiListItem>> Lists { get; } = new(StringComparer.Ordinal);
    /// <summary>
    /// The selected row of each list, by name; -1 when unset.
    /// </summary>
    public Dictionary<string, int> SelectedItems { get; } = new(StringComparer.Ordinal);
    /// <summary>
    /// How far each list or scroll bar is scrolled, by widget name.
    /// </summary>
    public Dictionary<string, float> ScrollOffsets { get; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Arranges MyGUI layouts into the <see cref="UiControl"/> lists both frontends paint and hit-test.
/// </summary>
public static partial class MyGuiPresentation
{
    /// <summary>
    /// The control property of a combo box's popup list and its parts: the host draws them over the controls a screen adds after arranging (the Items
    /// window's item cells), which would otherwise cover the list.
    /// </summary>
    public const string PopupProperty = "PopupControl";

    /// <summary>
    /// The controls of <paramref name="layout"/> on a <paramref name="width"/> x <paramref name="height"/> screen, roots in the order of their
    /// <paramref name="layers"/>, with the data and visibility of <paramref name="bindings"/>.
    /// </summary>
    public static IReadOnlyList<UiControl> Arrange(MyGuiLayout layout, int width, int height, UiBindings bindings, IReadOnlyList<string> layers)
    {
        var result = new List<UiControl>();
        var deferred = new List<Action>();   // popups (combo lists), visited after every root so they draw last
        void Visit(MyGuiWidget w, UiParent parent, UiRect clip, bool parentEnabled = true, float parentAlpha = 1)
        {
            if (bindings.Hidden.Contains(w.Name) || bindings.HiddenWidgets.Contains(w)) return;
            // A MultiListItem is a column of its MultiListBox, which lays it out (MultiColumns).
            if (w.Type == "MultiListItem") return;
            var props = new Dictionary<string, string>(w.Properties, StringComparer.Ordinal);
            if (bindings.Properties.TryGetValue(w.Name, out var overrides)) foreach (var p in overrides) props[p.Key] = p.Value;
            if (props.GetValueOrDefault("Visible") == "false") return;

            float alpha = WidgetAlpha(props, parentAlpha);
            if (alpha < 1) props["Alpha"] = alpha.ToString(Inv);
            var placed = Place(bindings, w, props, parent);
            var bounds = placed.Bounds;

            // A ProgressBar template (TrackSkin) keeps its frame; its fill is added in TrackPlace (AddTrackFill). Any other bound widget is cut to
            // the fraction.
            bool progress = bindings.Progress.TryGetValue(w.Name, out float fraction);
            string? trackSkin = progress ? w.UserStrings.GetValueOrDefault("TrackSkin") : null;
            if (progress && trackSkin is null)
            {
                bounds = bounds with { Width = bounds.Width * Math.Clamp(fraction, 0, 1) };
                placed = MadeAt(placed, bounds, null);
            }
            var self = AsParent(w, props, placed, bounds);

            string caption = WidgetCaption(layout, bindings, w, props);
            string action = WidgetAction(bindings, w, props);
            if (bindings.States.TryGetValue(w.Name, out var state)) props["State"] = state;
            bool enabled = parentEnabled && !bindings.Disabled.Contains(w.Name) && props.GetValueOrDefault("Enabled") != "false";
            bool pickable = IsPickable(w, props, action);
            result.Add(new(w.Name, w.Type, bounds, caption, action, w.Skin, enabled, props, default, Pickable: pickable, Clip: clip));

            var childClip = props.GetValueOrDefault("Clipped") == "false" ? clip : bounds.Intersect(clip);
            if (w.Type == "TabControl")
            {
                var pages = w.Children.Where(c => c.Type == "TabItem" && !bindings.Hidden.Contains(c.Name) && !bindings.HiddenWidgets.Contains(c))
                    .ToArray();
                var captions = bindings.TabCaptions.GetValueOrDefault(w.Name);
                int buttons = captions?.Count ?? pages.Length;
                int selected = Math.Clamp(bindings.SelectedTabs.GetValueOrDefault(w.Name), 0, Math.Max(0, buttons - 1));
                foreach (var child in w.Children.Where(c => c.Type != "TabItem")) Visit(child, self, childClip, enabled, alpha);
                AddTabButtons(result, bindings, w, props, bounds, clip, pages, captions, buttons, selected, enabled);
                if (pages.Length > 0) Visit(pages[captions is null ? selected : 0], bounds, childClip, enabled, alpha);
            }
            // An ItemBox scrolls like a ScrollView (MyGUI 3.2 ItemBox derives ScrollViewBase): its bars show only when a bound canvas overflows the
            // client.
            else if (w.Type is "ScrollView" or "ItemBox") VisitScrollView(result, bindings, w, props, self, childClip, enabled, alpha, Visit);
            else foreach (var child in w.Children) Visit(child, self, childClip, enabled, alpha);
            if (trackSkin is not null) AddTrackFill(result, w, trackSkin, fraction, enabled);
            AddWidgetContent(result, bindings, w, props, bounds, clip, enabled, alpha, new(0, 0, width, height), Visit, deferred);
        }

        var screen = new UiRect(0, 0, width, height);
        foreach (var root in RootsInOrder(layout, layers)) Visit(root, screen, screen);
        for (int i = 0; i < deferred.Count; i++) deferred[i]();
        return result;
    }

    /// <summary>
    /// The layout's roots in paint order (by <see cref="LayerOrder"/>, stable); a single root needs no sorting.
    /// </summary>
    static IEnumerable<MyGuiWidget> RootsInOrder(MyGuiLayout layout, IReadOnlyList<string> layers) =>
        layout.Roots.Count <= 1 ? layout.Roots : layout.Roots.OrderBy(w => LayerOrder(layers, w));

    /// <summary>
    /// A root's place in the paint order: its layer's index, 0 for an unknown layer.
    /// </summary>
    static int LayerOrder(IReadOnlyList<string> layers, MyGuiWidget root)
    {
        int i = layers.IndexOfLayer(root.Layer);
        return i < 0 ? 0 : i;
    }

    /// <summary>
    /// MyGUI multiplies a widget's Alpha into its children unless they set InheritsAlpha false (an EditBox's text is its template client's).
    /// </summary>
    static float WidgetAlpha(Dictionary<string, string> props, float parentAlpha) =>
        (props.GetValueOrDefault("InheritsAlpha") == "false" ? 1 : parentAlpha)
        * (float.TryParse(props.GetValueOrDefault("Alpha"), NumberStyles.Float, Inv, out float own) ? Math.Clamp(own, 0, 1) : 1);

    /// <summary>
    /// A parent as MyGUI's children see it: a child's position becomes pixels once, against the parent's size when the child is made, and is aligned
    /// again only when the parent is sized (docs/formats/mygui.md "Align and pixel rounding"). <paramref name="Now"/> is where the children go;
    /// <paramref name="Name"/> (null for a bare rectangle) and <paramref name="Owner"/> (its template owner; null when it is no template part) tell
    /// its template parts from its layout children; <paramref name="Made"/> is its size when it was made (at the origin) and <paramref name="Sized"/> whether it was sized since.
    /// A template part also has <paramref name="AtOwner"/>, its rectangle once its owner took its own size (the size the owner's layout children are
    /// made in), <paramref name="AtOwnerSized"/> whether it was sized getting there, and <paramref name="Later"/> whether it was sized after that;
    /// any other widget has its made size and <paramref name="Sized"/> there.
    /// </summary>
    readonly record struct UiParent(UiRect Now, string? Name, string? Owner, UiRect Made, bool Sized, UiRect AtOwner, bool AtOwnerSized, bool Later)
    {
        /// <summary>
        /// A parent that is only a rectangle (the screen, a popup's viewport, a run-time column's place): made at its size, never sized since.
        /// </summary>
        public static implicit operator UiParent(UiRect r) =>
            new(r, null, null, new(0, 0, r.Width, r.Height), false, new(0, 0, r.Width, r.Height), false, false);
    }

    /// <summary>
    /// How a widget is placed (<see cref="ChainOf"/>): whether it is a template part of its parent's template, the parent's rectangle now, the
    /// parent's size the widget was made in and whether the parent was sized since; for a part also the parent's rectangle once the owner took its
    /// own size, whether the parent was sized getting there, and whether it was sized after that.
    /// </summary>
    readonly record struct UiChain(bool Part, UiRect Parent, float MadeWidth, float MadeHeight, bool Sized, UiRect OwnerParent, bool OwnerSized,
        bool Later);

    /// <summary>
    /// A placed widget: its screen rectangle, how it was placed, its rectangle in its parent when made and, for a template part, its rectangle once
    /// the owner took its own size; whether the code placed it (<paramref name="Coded"/>) and sized it since it was made (<paramref name="Resized"/>).
    /// </summary>
    readonly record struct UiPlaced(UiRect Bounds, UiChain Chain, UiRect Made, UiRect AtOwner, bool Coded, bool Resized);

    /// <summary>
    /// A widget's screen rectangle as MyGUI's history of it gives (docs/formats/mygui.md "Align and pixel rounding"): its rectangle when made
    /// (<see cref="MadeRect"/>) aligned by its parent's sizes since, in whole pixels when the parents are, unless the code placed it (bound
    /// <see cref="UiBindings.Positions"/> as given, or <see cref="UiBindings.RelativePositions"/> in the parent's size now as
    /// <c>Widget::setRealCoord</c> converts them; <see cref="MadeAt"/>).
    /// </summary>
    static UiPlaced Place(UiBindings bindings, MyGuiWidget w, Dictionary<string, string> props, UiParent parent)
    {
        var chain = ChainOf(props, parent, bindings.TemplateScale);
        var made = MadeRect(w, props, chain, bindings.TemplateScale);
        var aligned = Follow(made, chain.Parent, chain.MadeWidth, chain.MadeHeight, w.Align, chain.Sized);
        var atOwner = chain.Part ? Follow(made, chain.OwnerParent, chain.MadeWidth, chain.MadeHeight, w.Align, chain.OwnerSized) : made;
        var placed = new UiPlaced(aligned, chain, made, atOwner, false, false);

        if (bindings.Positions.TryGetValue(w.Name, out var positioned))
            return MadeAt(placed, positioned, bindings.MadeSizes.TryGetValue(w.Name, out var size) ? size : null);
        if (bindings.RelativePositions.TryGetValue(w, out var relative)) return MadeAt(placed, relative.RelativeTo(chain.Parent, true), null);

        return placed;
    }

    /// <summary>
    /// <paramref name="placed"/> where the code put it, <paramref name="bounds"/>: made there, so its children are made in that size, unless the code
    /// says it was made at another <paramref name="madeSize"/> and sized since (<see cref="UiBindings.MadeSizes"/>), which aligns them.
    /// </summary>
    static UiPlaced MadeAt(UiPlaced placed, UiRect bounds, (float Width, float Height)? madeSize)
    {
        var (width, height) = madeSize ?? (bounds.Width, bounds.Height);
        var made = new UiRect(0, 0, width, height);
        bool resized = width != bounds.Width || height != bounds.Height;
        return placed with { Bounds = bounds, Made = made, AtOwner = made, Coded = true, Resized = resized };
    }

    /// <summary>
    /// How a widget is placed in <paramref name="parent"/>. A template part of it (its <c>TemplateOwner</c> is the parent) is made in the template's
    /// root size (<c>TemplateReferenceWidth</c> / <c>Height</c> scaled as Kenshi's MyGUI scales a <c>ResourceLayout</c>) and aligned at once to the
    /// owner's own size; a part of the same template as the parent is made in the parent's size as made; a layout widget in the size its parent had
    /// when the layout's widgets were made.
    /// </summary>
    static UiChain ChainOf(IReadOnlyDictionary<string, string> props, UiParent parent, float scale)
    {
        // An unnamed owner's parts have an empty TemplateOwner: the key, not its text, marks a part.
        if (!props.TryGetValue("TemplateOwner", out var owner)) return LayoutChain(parent);

        if (owner == parent.Name)
        {
            float rootWidth = MyGuiScale.Size(Num(props, "TemplateReferenceWidth", parent.Made.Width), scale);
            float rootHeight = MyGuiScale.Size(Num(props, "TemplateReferenceHeight", parent.Made.Height), scale);
            return new(true, parent.Now, rootWidth, rootHeight, true, parent.Made, true, parent.Sized);
        }

        if (owner == parent.Owner)
            return new(true, parent.Now, parent.Made.Width, parent.Made.Height, parent.Sized, parent.AtOwner, parent.AtOwnerSized, parent.Later);

        return LayoutChain(parent);
    }

    /// <summary>
    /// A layout widget's chain: made in the size its parent had when the layout's widgets were made, aligned when the parent was sized after that.
    /// </summary>
    static UiChain LayoutChain(UiParent parent) =>
        new(false, parent.Now, parent.AtOwner.Width, parent.AtOwner.Height, parent.Later, default, false, false);

    /// <summary>
    /// A widget's rectangle in its parent when it was made, in whole pixels: a template part's <c>position</c> scaled (<see cref="MyGuiScale"/>), a
    /// layout widget's as written, and either's <c>position_real</c> as <c>int(fraction x the parent's size then)</c>
    /// (<c>CoordConverter::convertFromRelative</c>; Kenshi scales no fraction).
    /// </summary>
    static UiRect MadeRect(MyGuiWidget w, IReadOnlyDictionary<string, string> props, UiChain chain, float scale)
    {
        var parent = new UiRect(0, 0, chain.MadeWidth, chain.MadeHeight);
        if (!chain.Part) return w.RealPosition ? w.Position.RelativeTo(parent, true) : w.Position;

        return props.TryGetValue("TemplateRealPosition", out var real)
            ? UiRect.Parse(real).RelativeTo(parent, true)
            : MyGuiScale.Coord(w.Position, scale);
    }

    /// <summary>
    /// <paramref name="made"/> in <paramref name="parent"/>: aligned from the parent's made size (<c>Widget::_setAlign</c>) when the parent was
    /// <paramref name="sized"/> since, else only moved with it.
    /// </summary>
    static UiRect Follow(UiRect made, UiRect parent, float madeWidth, float madeHeight, string align, bool sized) => sized
        ? made.AlignTo(parent, madeWidth, madeHeight, align)
        : made with { X = parent.X + made.X, Y = parent.Y + made.Y };

    /// <summary>
    /// The widget at <paramref name="bounds"/> as its children see it. It was sized since they were made when it stretches on an axis and its own
    /// parent was sized (<c>_setAlign</c> calls <c>setSize</c> then, even at an unchanged size; one it only moves gets <c>setPosition</c>), or, when
    /// the code placed it, when the code says it sized it since. A ScrollView client's canvas (<c>CanvasOrigin</c>) is a parent made at its size.
    /// </summary>
    static UiParent AsParent(MyGuiWidget w, IReadOnlyDictionary<string, string> props, UiPlaced placed, UiRect bounds)
    {
        if (props.TryGetValue("CanvasOrigin", out var canvas)) return UiRect.Parse(canvas);

        bool stretches = UiRect.Stretches(w.Align);
        bool sized = placed.Coded ? placed.Resized : placed.Chain.Sized && stretches;
        var made = new UiRect(0, 0, placed.Made.Width, placed.Made.Height);
        string? owner = placed.Chain.Part ? props.GetValueOrDefault("TemplateOwner") : null;
        if (!placed.Chain.Part || placed.Coded) return new(bounds, w.Name, owner, made, sized, made, false, sized);

        bool later = placed.Chain.Later && stretches;
        return new(bounds, w.Name, owner, made, sized, placed.AtOwner, placed.Chain.OwnerSized && stretches, later);
    }

    /// <summary>
    /// A widget's caption, in the player's language. A caption written in the layout is the source string of the player's language (the layouts
    /// say "SKILLS", the catalog key is "Skills"). A template part's caption is its owner's, whose WordWrap and TextAlign it takes too.
    /// </summary>
    static string WidgetCaption(MyGuiLayout layout, UiBindings bindings, MyGuiWidget w, Dictionary<string, string> props)
    {
        // MyGUI's plain Widget has no text: it ignores a layout Caption (the character trading window's TradePanel says "Character Selection").
        string layoutCaption = w.Type == "Widget" ? "" : props.GetValueOrDefault("Caption") ?? "";
        string caption = bindings.Coded(w.Name) ?? PartText(bindings, props) ?? Loc.TrCaption(layoutCaption);
        if (props.TryGetValue("CaptionOwner", out string? owner))
        {
            caption = bindings.Coded(owner) ?? Loc.TrCaption(layout.Find(owner)?.Properties.GetValueOrDefault("Caption") ?? "");
            // An EditBox's WordWrap is its text's, which the template's client draws.
            if (!props.ContainsKey("WordWrap") && OwnerProperty(layout, bindings, owner, "WordWrap") is { } wrap)
                props["WordWrap"] = wrap;
            // So is its TextAlign (MyGUI EditBox::setTextAlign aligns the client's text): the editor's NameText is centred.
            if (OwnerProperty(layout, bindings, owner, "TextAlign") is { } textAlign && layout.Find(owner)?.Type == "EditBox")
                props["TextAlign"] = textAlign;
        }

        // A TabItem's caption is its tab button's, never drawn on the page.
        if (props.GetValueOrDefault("SkipCaption") == "true" || w.Type == "TabItem") caption = "";
        return caption;
    }

    /// <summary>
    /// A property of the template owner <paramref name="owner"/>: bound, else from the layout; null when it has none.
    /// </summary>
    static string? OwnerProperty(MyGuiLayout layout, UiBindings bindings, string owner, string key) =>
        (bindings.Properties.GetValueOrDefault(owner)?.GetValueOrDefault(key)) ?? layout.Find(owner)?.Properties.GetValueOrDefault(key);

    /// <summary>
    /// A widget's bound action; a template part's event goes to its owner (<c>owner@event</c>, else <c>mygui:event:owner</c>).
    /// </summary>
    static string WidgetAction(UiBindings bindings, MyGuiWidget w, Dictionary<string, string> props)
    {
        string action = bindings.Actions.GetValueOrDefault(w.Name) ?? "";
        if (props.TryGetValue("TemplateEvent", out string? ev) && props.TryGetValue("TemplateOwner", out var target))
            action = bindings.Actions.GetValueOrDefault(target + "@" + ev) ?? $"mygui:{ev}:{target}";
        return action;
    }

    /// <summary>
    /// Whether a widget takes the mouse: an action, an interactive type, an editable EditBox, or a drawn skin.
    /// </summary>
    static bool IsPickable(MyGuiWidget w, Dictionary<string, string> props, string action) =>
        action.Length > 0 || w.Type is "Button" or "Window" or "ListBox" or "MultiListBox" or "ScrollBar" or "ComboBox" or "ScrollView"
        || w.Type == "EditBox" && props.GetValueOrDefault("Static") != "true" || w.Skin.Length > 0 && w.Skin != "PanelEmpty" && w.Type != "TextBox";

    /// <summary>
    /// Whether <paramref name="c"/> is the part <paramref name="part"/> of the template expanded for <paramref name="owner"/>.
    /// </summary>
    static bool IsTemplatePart(UiControl c, string owner, string part) =>
        c.Properties.GetValueOrDefault("TemplateOwner") == owner && c.Properties.GetValueOrDefault("TemplatePart") == part;

    /// <summary>
    /// A TabControl's buttons: in the skin's HeaderPlace (MyGUI's TabControl part), ButtonWidth (at the template scale) wide unless
    /// ButtonAutoWidth. The selected tab is Checked, not a fixed State, so the painter shows the skin's hover and pushed states (amber caption
    /// under the mouse).
    /// </summary>
    static void AddTabButtons(List<UiControl> result, UiBindings bindings, MyGuiWidget w, Dictionary<string, string> props, UiRect bounds,
        UiRect clip, MyGuiWidget[] pages, IReadOnlyList<string>? captions, int buttons, int selected, bool enabled)
    {
        var header = result.LastOrDefault(c => IsTemplatePart(c, w.Name, "HeaderPlace"))?.Bounds
            ?? new UiRect(bounds.X, bounds.Y, bounds.Width, 30 * bindings.TemplateScale);
        float tabWidth = buttons == 0 ? 0 : header.Width / buttons;
        if (buttons > 0 && props.GetValueOrDefault("ButtonAutoWidth") == "false"
            && float.TryParse(props.GetValueOrDefault("ButtonWidth"), NumberStyles.Float, Inv, out float fixedWidth))
            tabWidth = Math.Min(fixedWidth * bindings.TemplateScale, tabWidth);

        for (int i = 0; i < buttons; i++)
        {
            result.Add(new(w.Name + "@tab" + i, "Button", new(header.X + i * tabWidth, header.Y, tabWidth, header.Height),
                captions?[i] ?? bindings.Coded(w.Name + "@tab" + i)
                    ?? (pages[i].Properties.GetValueOrDefault("Caption") is { Length: > 0 } tabText ? Loc.TrCaption(tabText) : null)
                    ?? pages[i].Name, $"mygui:tab:{w.Name}:{i}", "Kenshi_SquadTabButton1Skin", enabled,
                new Dictionary<string, string>
                {
                    ["Checked"] = i == selected ? "true" : "false",
                    ["FontName"] = "Kenshi_PaintedTextFont_Medium",
                    ["TextAlign"] = "Center"
                },
                default, Clip: clip));
        }
    }

    /// <summary>
    /// TrackFill (MyGUI ProgressBar): the track runs from the start of TrackPlace to the position, under the frame that follows it.
    /// </summary>
    static void AddTrackFill(List<UiControl> result, MyGuiWidget w, string trackSkin, float fraction, bool enabled)
    {
        int place = result.FindLastIndex(c => IsTemplatePart(c, w.Name, "TrackPlace"));
        if (place < 0) return;

        var track = result[place].Bounds;
        result.Insert(place + 1, new(w.Name + "@track", "Widget", track with { Width = MathF.Round(track.Width * Math.Clamp(fraction, 0, 1)) },
            "", "", trackSkin, enabled, new Dictionary<string, string>(StringComparer.Ordinal), default, Pickable: false, Clip: result[place].Clip));
    }

    /// <summary>
    /// The index of <paramref name="layer"/> in <paramref name="layers"/>; -1 when absent.
    /// </summary>
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
