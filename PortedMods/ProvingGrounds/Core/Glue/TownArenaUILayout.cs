namespace ProvingGrounds.Core.Glue;

// Port of the town registry window's pixel arithmetic, upstream/ProvingGrounds/plugin/src/TownArenaUI.cpp (Proving Grounds by Shutori, GPLv3),
// task 7.6 (TW §2.5; CAP 7.0-7.4): Body / Gap (:105-106), Wrap (:162-170), PlaceTip (:171-183) and PortraitTip's tip sizes (:184-221), LayoutRow
// (:404-420), LayoutCardContent (:578-623), Buttons (:624-634), LayoutSkarn (:635-645), LayoutRoster (:646-657), LayoutSidebar (:658-674),
// LayoutTownPanel (:675-783) with LayoutAccent (:476-493), over the published view (Core/Glue/TownArenaUILogic.cs, task 7.5) and Core's
// TownCardLayout / LeaderboardLayout. Pure; the window side (src/Ui/TownArenaUI.cs) writes the rectangles into the layout XML and binds them.
// Widget names: EnsureWindow's (:810-887) where the C++ names them; the parts it leaves unnamed (the rows of MakeRow :383-403, the card parts,
// accent strips and borders :467-475, :862-883) get port names (the framework binds by name; docs/divergences.md "TownArenaUI").
//
// MyGUI reads and what replaces them here (as the recruitment window, Core/Glue/PrisonerRecruitmentUILayout.cs):
//   metrics->getFontHeight() (Body, :105) ...................... TownLayoutInput.FontHeight (the standard text's measured font height)
//   getTextSize (edit boxes, the buttons) ...................... IRewardsTextMeasure: the standard text (Kenshi_WordWrap / Kenshi_WordWrapEmpty), the
//                                                                  painted text (the rows' Kenshi_PaintedWordWrapEmpty), a Kenshi_Button1 caption
//   NativeUI::Spacing / RowHeight(booking, 0) .................. Core/Glue/NativeUI.cs (RowHeight with the caption's standard text height, as every
//                                                                  window since 4.5b)
//   getHeight() - getTextRegion().height (Wrap :165) ............ a Kenshi_WordWrap (bounded): PgTemplates.WordWrapInset* (30 px, the edit box is at
//                                                                  least 100 high when measured); the Empty skins have none
//   ScrollView::getViewCoord / setCanvasSize .................... the Kenshi_ScrollView insets (ArenaUILayout.ScrollInset*, ScrollBarW); a bar shows
//                                                                  while the canvas overflows the view on its axis (the challenges view allows the
//                                                                  horizontal one, setVisibleHScroll(true) :818); the bars settle as Meitou's arrange
//                                                                  settles them (UiDocument.Widgets.cs FitScrollBars, to its fixed point)
//   window->getClientWidget()->getSize() ........................ the window rectangle less the Kenshi_WindowCX chrome (ArenaUILayout.WindowClientMargin*)
//   RenderManager::getViewSize (LayoutTownPanel :676-681) ....... TownLayoutInput.ViewWidth / ViewHeight

/// <summary>What the town window's arithmetic reads besides the view (the published model).</summary>
public sealed record TownLayoutInput
{
    public int ViewWidth { get; init; } = 1920;
    public int ViewHeight { get; init; } = 1080;
    /// <summary>The standard text's font height (<c>metrics->getFontHeight()</c>, Body :105; NativeUI::Spacing).</summary>
    public int FontHeight { get; init; } = NativeUI.DefaultFontHeight;
    /// <summary>Whether each scroll view showed its bars after the previous layout (MyGUI keeps them until the next setCanvasSize; they decide the
    /// first pass's view size).</summary>
    public bool SidebarBarBefore { get; init; }
    public bool RosterBarBefore { get; init; }
    public bool ChallengesVBarBefore { get; init; }
    public bool ChallengesHBarBefore { get; init; }
}

/// <summary>One scroll view after a layout: its canvas, its view (the offsets are clamped to canvas - view) and its bars.</summary>
public readonly record struct TownCanvasInfo(int ContentW, int ContentH, int ViewW, int ViewH, bool VBar, bool HBar)
{
    /// <summary>C++ LayoutRoster :655-656 / LayoutSidebar :664-665 (LeaderboardLayout::ClampOffset) and LayoutTownPanel :779-781
    /// (TownCardLayout::ClampViewOffset): a vertical offset (positive, from the top) clamped to [0, canvas - view].</summary>
    public int ClampY(int offset) => LeaderboardLayout.ClampOffset(offset, ContentH, ViewH);

    /// <summary>C++ LayoutTownPanel :779-780 the horizontal offset of the challenges view, clamped the same way.</summary>
    public int ClampX(int offset) => -TownCardLayout.ClampViewOffset(-offset, ContentW, ViewW);
}

/// <summary>The town window's layout: every widget's rectangle (relative to its parent, as MyGUI's setCoord), the hidden widgets, the canvases,
/// the window rectangle.</summary>
public sealed class TownWindowLayout
{
    public Dictionary<string, LayoutRect> Rects { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, (int W, int H)> Canvas { get; } = new(StringComparer.Ordinal);
    public HashSet<string> Hidden { get; } = new(StringComparer.Ordinal);
    public LayoutRect Window { get; set; }
    public int ClientWidth { get; set; }
    public int ClientHeight { get; set; }
    public int Gap { get; set; }
    public int Body { get; set; }
    public int ButtonHeight { get; set; }
    public bool Stacked { get; set; }
    /// <summary>The card grid of the last pass (TownCardLayout::Fit / SingleRow, or the prompt's one column) and whether it was a single row.</summary>
    public TownCardLayout.Grid Grid { get; set; }
    public bool SingleRow { get; set; }
    public TownCanvasInfo Sidebar { get; set; }
    public TownCanvasInfo Roster { get; set; }
    public TownCanvasInfo Challenges { get; set; }
}

/// <summary>The town window's widget names: the C++'s own (EnsureWindow :810-887) and port names for the parts it leaves unnamed.</summary>
public static class TownArenaUINames
{
    public const string Window = "PG_TownRegistry";                                                     // :814
    public const string Metrics = "PG_TownMetrics", Sidebar = "PG_TownSidebar", Challenges = "PG_TownChallenges";   // :820-822
    public const string Team = "PG_TownTeam", RosterHint = "PG_TownRosterHint", Roster = "PG_TownRoster";           // :824-826
    public const string EmptyRoster = "PG_TownEmptyRoster", Direct = "PG_TownDirect", NextRefresh = "PG_TownNextRefresh";   // :827-829
    public const string BuyRefresh = "PG_TownBuyRefresh", Progress = "PG_TownProgress";                            // :830-831
    public const string SkarnPanel = "PG_TownSkarnStatus", SkarnPortrait = "PG_TownSkarnPortrait";                 // :832-833
    public const string SkarnPlaceholder = "PG_TownSkarnPlaceholder", SkarnState = "PG_TownSkarnState";            // :834-837
    public const string Status = "PG_TownStatus", Book = "PG_TownBook";                                            // :842, :848
    public const string TipBackground = "PG_TownFighterTipBackground", FighterTip = "PG_TownFighterTip";          // :878-883

    static string I(int v) => v.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary><c>PG_TownDivision%d</c> (:845-846).</summary>
    public static string Division(int i) => "PG_TownDivision" + I(i);
    /// <summary><c>PG_TownSkarnTip%d</c> (:884-886).</summary>
    public static string SkarnTip(int i) => "PG_TownSkarnTip" + I(i);
    /// <summary><c>PG_TownCard%d</c> (:851-852), the card's root.</summary>
    public static string Card(int slot) => "PG_TownCard" + I(slot);
    // port names: the card's parts (unnamed in the C++, :467-475, :854-871)
    public static string Accent(int slot) => Card(slot) + "_Accent";
    public static string Fade(int slot, int i) => Card(slot) + "_Fade" + I(i);
    public static string Border(int slot, int i) => Card(slot) + "_Border" + I(i);
    public static string Heading(int slot) => Card(slot) + "_Heading";
    public static string Portrait(int slot, int m) => Card(slot) + "_Portrait" + I(m);
    public static string Placeholder(int slot, int m) => Card(slot) + "_Placeholder" + I(m);
    public static string Name(int slot, int m) => Card(slot) + "_Name" + I(m);
    public static string Stats(int slot, int m) => Card(slot) + "_Stats" + I(m);
    public static string Terms(int slot) => Card(slot) + "_Terms";
    public static string Text(int slot) => Card(slot) + "_Text";
    public static string CardBook(int slot) => Card(slot) + "_Book";
    // port names: a fighter row (MakeRow :383-403, unnamed in the C++), %d = the row's place in the roster
    public static string Row(int i) => "PG_TownRow_" + I(i);
    public static string RowPortrait(int i) => "PG_TownRowPortrait_" + I(i);
    public static string RowText(int i) => "PG_TownRowText_" + I(i);
    public static string RowStatusBack(int i) => "PG_TownRowStatusBack_" + I(i);
    public static string RowStatusFill(int i) => "PG_TownRowStatusFill_" + I(i);
    public static string RowBorder(int i, int edge) => "PG_TownRowBorder_" + I(i) + "_" + I(edge);

    /// <summary>A name's index after <paramref name="prefix"/> (1-6 digits, the whole rest), or false.</summary>
    static bool TryIndex(string widget, string prefix, out int index)
    {
        index = -1;
        if (!widget.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var digits = widget.AsSpan(prefix.Length);
        if (digits.Length == 0 || digits.Length > 6) return false;
        foreach (char ch in digits) if (!char.IsAsciiDigit(ch)) return false;
        index = int.Parse(digits, System.Globalization.CultureInfo.InvariantCulture);
        return true;
    }

    /// <summary>A row button's index (<c>PG_TownRow_%d</c>).</summary>
    public static bool TryRow(string widget, out int index) => TryIndex(widget, "PG_TownRow_", out index);

    /// <summary>A division button's index 0-2 (<c>PG_TownDivision%d</c>).</summary>
    public static bool TryDivision(string widget, out int division) => TryIndex(widget, "PG_TownDivision", out division) && division is >= 0 and < 3;

    /// <summary>A card's Book button slot 0-4.</summary>
    public static bool TryCardBook(string widget, out int slot)
    {
        slot = -1;
        for (int s = 0; s < 5; ++s) if (widget == CardBook(s)) { slot = s; return true; }
        return false;
    }

    /// <summary>The slot of a card part bound to SelectOffer (BindOfferText :298-307: the heading, each member's portrait, placeholder, name and
    /// stats, the terms and the text), or false.</summary>
    public static bool TryOfferPart(string widget, out int slot)
    {
        slot = -1;
        for (int s = 0; s < 5; ++s)
        {
            if (widget == Heading(s) || widget == Terms(s) || widget == Text(s)) { slot = s; return true; }
            for (int m = 0; m < 4; ++m)
                if (widget == Portrait(s, m) || widget == Placeholder(s, m) || widget == Name(s, m) || widget == Stats(s, m)) { slot = s; return true; }
        }
        return false;
    }

    /// <summary>The card and member of a portrait or placeholder (the fighter tip's owners, BindTip :863, :867), or false.</summary>
    public static bool TryTipOwner(string widget, out int slot, out int member)
    {
        for (int s = 0; s < 5; ++s)
            for (int m = 0; m < 4; ++m)
                if (widget == Portrait(s, m) || widget == Placeholder(s, m)) { slot = s; member = m; return true; }
        slot = member = -1;
        return false;
    }

    /// <summary>Whether the widget carries the Skarn tip (the panel, its portrait, its placeholder: setUserString("skarnTip", "1") :838-840).</summary>
    public static bool IsSkarnTipOwner(string widget) => widget is SkarnPanel or SkarnPortrait or SkarnPlaceholder;

    /// <summary>The MyGUI event TownArenaUI.cpp binds each button to (task 4.4b): the rows to eventMouseButtonReleased with an inside test (RowReleased
    /// :280-283, which acts as a click: a left release over the row it was pressed on), every other button to eventMouseButtonClick (:808); nothing
    /// to a press.</summary>
    public static UiButtonEvent ButtonEvent(string widget) => UiButtonEvent.Click;
}

/// <summary>The town window's pixel arithmetic (task 7.6), C++ function by function. Pure.</summary>
public sealed class TownArenaUILayout<TId>
{
    /// <summary>The horizontal bar of a Kenshi_ScrollView is as thick as the vertical one (kenshi_templates.xml:253-260).</summary>
    public const int ScrollBarH = ArenaUILayout.ScrollBarW;

    /// <summary>One MyGUI ScrollView (Kenshi_ScrollView): its rectangle, its canvas and whether each bar shows.</summary>
    sealed class Scroll(string name, bool vbar, bool hbar, bool hAllowed)
    {
        public readonly string Name = name;
        public LayoutRect Rect = new(0, 0, 300, 300);   // EnsureWindow's `initial` (:816)
        public bool VBar = vbar, HBar = hbar && hAllowed;
        public int CanvasW = -1, CanvasH = -1;
        public int ViewW => Math.Max(0, Rect.W - ArenaUILayout.ScrollInsetW - (VBar ? ArenaUILayout.ScrollBarW : 0));
        public int ViewH => Math.Max(0, Rect.H - ArenaUILayout.ScrollInsetH - (HBar ? ScrollBarH : 0));
        void Fit()
        {
            if (CanvasH < 0) return;
            for (int i = 0; i < 4; ++i)
            {
                bool v = CanvasH > ViewH, h = hAllowed && CanvasW > ViewW;
                if (v == VBar && h == HBar) return;
                VBar = v; HBar = h;
            }
        }
        public void SetRect(LayoutRect r) { Rect = r; Fit(); }
        public void SetCanvas(int w, int h) { CanvasW = w; CanvasH = h; Fit(); }
        public TownCanvasInfo Info => new(Math.Max(0, CanvasW), Math.Max(0, CanvasH), ViewW, ViewH, VBar, HBar);
    }

    readonly TownArenaUiView<TId> v;
    readonly TownLayoutInput input;
    readonly IRewardsTextMeasure measure;
    readonly TownWindowLayout result = new();
    readonly int gap, body, buttonHeight;
    readonly Scroll sidebar, roster, challenges;

    TownArenaUILayout(TownArenaUiView<TId> view, TownLayoutInput input, IRewardsTextMeasure measure)
    {
        v = view;
        this.input = input;
        this.measure = measure;
        gap = NativeUI.Spacing(input.FontHeight);   // Gap :106 NativeUI::Spacing(metrics)
        body = Math.Max(1, input.FontHeight);        // Body :105
        buttonHeight = NativeUI.RowHeight(measure.Standard(view.BookingButton, 0).Height, input.FontHeight, 0);   // NativeUI::RowHeight(booking, 0)
        result.Gap = gap;
        result.Body = body;
        result.ButtonHeight = buttonHeight;
        sidebar = new Scroll(TownArenaUINames.Sidebar, input.SidebarBarBefore, false, false);
        roster = new Scroll(TownArenaUINames.Roster, input.RosterBarBefore, false, false);
        challenges = new Scroll(TownArenaUINames.Challenges, input.ChallengesVBarBefore, input.ChallengesHBarBefore, true);
    }

    /// <summary>The window and every widget's rectangle for the view: LayoutTownPanel (Tick :952), with the visibility the refresh gave.</summary>
    public static TownWindowLayout Compute(TownArenaUiView<TId> view, TownLayoutInput input, IRewardsTextMeasure measure)
    {
        var l = new TownArenaUILayout<TId>(view, input, measure);
        l.Creation();
        l.Visibility();
        l.LayoutTownPanel();
        l.result.Sidebar = l.sidebar.Info;
        l.result.Roster = l.roster.Info;
        l.result.Challenges = l.challenges.Info;
        return l.result;
    }

    void Set(string widget, int x, int y, int w, int h) => result.Rects[widget] = new LayoutRect(x, y, w, h);
    void Move(string widget, int x, int y) { var r = result.Rects[widget]; result.Rects[widget] = r with { X = x, Y = y }; }
    void Size(string widget, int w, int h) { var r = result.Rects[widget]; result.Rects[widget] = r with { W = w, H = h }; }
    bool Visible(string widget) => !result.Hidden.Contains(widget);
    void Hide(string widget, bool hidden) { if (hidden) result.Hidden.Add(widget); }

    void SetScroll(Scroll s, int x, int y, int w, int h)
    {
        s.SetRect(new LayoutRect(x, y, w, h));
        result.Rects[s.Name] = s.Rect;
    }

    void SetCanvas(Scroll s, int w, int h)
    {
        s.SetCanvas(w, h);
        result.Canvas[s.Name] = (w, h);
    }

    // C++: EnsureWindow :810-887 + MakeRow :383-403 + MakeAccent :454-475: every widget where it was created
    void Creation()
    {
        Set(TownArenaUINames.Metrics, 0, 0, 100, 24);   // :820 NativeUI::Label(client, IntCoord(0,0,100,24), ...)
        foreach (var s in new[] { sidebar, roster, challenges }) result.Rects[s.Name] = s.Rect;   // :821-826 NativeUI::Scroll(.., initial, ..)
        foreach (var n in new[] { TownArenaUINames.Team, TownArenaUINames.RosterHint, TownArenaUINames.EmptyRoster, TownArenaUINames.Direct,
                     TownArenaUINames.NextRefresh, TownArenaUINames.Progress, TownArenaUINames.SkarnPlaceholder, TownArenaUINames.SkarnState,
                     TownArenaUINames.Status, TownArenaUINames.FighterTip })
            Set(n, 0, 0, 100, 100);   // Text :791-797 IntCoord(0, 0, 100, 100)
        for (int i = 0; i < 7; ++i) Set(TownArenaUINames.SkarnTip(i), 0, 0, 100, 100);
        Set(TownArenaUINames.BuyRefresh, 0, 0, 100, 24);   // Button :798-801 IntCoord(0, 0, 100, 24)
        Set(TownArenaUINames.Book, 0, 0, 100, 24);
        for (int i = 0; i < 3; ++i) Set(TownArenaUINames.Division(i), 0, 0, 100, 24);
        Set(TownArenaUINames.SkarnPanel, 0, 0, 300, 300);       // :832 initial
        Set(TownArenaUINames.SkarnPortrait, 0, 0, 300, 300);    // :833 initial
        Set(TownArenaUINames.TipBackground, 0, 0, 300, 300);    // :877-878 initial
        for (int s = 0; s < 5; ++s)
        {
            Set(TownArenaUINames.Card(s), 0, 0, 300, 300);      // :852 initial
            Set(TownArenaUINames.Accent(s), 0, 0, 1, 1);        // :458 IntCoord(0, 0, 1, 1)
            for (int i = 0; i < 16; ++i) Set(TownArenaUINames.Fade(s, i), 0, 0, 1, 1);
            for (int i = 0; i < 4; ++i) Set(TownArenaUINames.Border(s, i), 0, 0, 1, 1);
            Set(TownArenaUINames.Heading(s), 0, 0, 100, 100);
            for (int m = 0; m < 4; ++m)
            {
                Set(TownArenaUINames.Portrait(s, m), 0, 0, 96, 96);   // :859-860 IntCoord(0, 0, 96, 96)
                Set(TownArenaUINames.Placeholder(s, m), 0, 0, 100, 100);
                Set(TownArenaUINames.Name(s, m), 0, 0, 100, 100);
                Set(TownArenaUINames.Stats(s, m), 0, 0, 100, 100);
            }
            Set(TownArenaUINames.Terms(s), 0, 0, 100, 100);
            Set(TownArenaUINames.Text(s), 0, 0, 100, 100);
            Set(TownArenaUINames.CardBook(s), 0, 0, 100, 24);
        }
        for (int i = 0; i < v.Rows.Count; ++i)
        {
            Set(TownArenaUINames.Row(i), 0, 0, 200, 64);              // :385 IntCoord(0, 0, 200, 64)
            Set(TownArenaUINames.RowPortrait(i), 0, 0, 48, 48);       // :389 IntCoord(0, 0, 48, 48)
            Set(TownArenaUINames.RowText(i), 0, 0, 100, 24);          // :392 IntCoord(0, 0, 100, 24)
            Set(TownArenaUINames.RowStatusBack(i), 0, 0, 100, 6);     // :394-395 IntCoord(0, 0, 100, 6)
            Set(TownArenaUINames.RowStatusFill(i), 0, 0, 1, 6);       // :397-398 IntCoord(0, 0, 1, 6)
            for (int e = 0; e < 4; ++e) Set(TownArenaUINames.RowBorder(i, e), 0, 0, 1, 1);   // :402 IntCoord(0, 0, 1, 1)
        }
    }

    // The visibility Tick's refresh gave before LayoutTownPanel (the layout reads getVisible): EnsureWindow's hidden widgets (the metrics label
    // :820, the tip :888 and its Skarn lines :886), RefreshRoster's empty label :447 and rows (SelectionVisuals :255-264, RefreshStatusBar
    // :270-279), the cards (Tick :937-950, RefreshCard :494-561 / RefreshSelectionPrompt :562-577) and RefreshSkarn (:342-350)
    void Visibility()
    {
        result.Hidden.Add(TownArenaUINames.Metrics);
        result.Hidden.Add(TownArenaUINames.TipBackground);
        for (int i = 0; i < 7; ++i) result.Hidden.Add(TownArenaUINames.SkarnTip(i));
        Hide(TownArenaUINames.EmptyRoster, v.Rows.Count != 0);
        for (int i = 0; i < v.Rows.Count; ++i)
        {
            var r = v.Rows[i];
            for (int e = 0; e < 4; ++e) Hide(TownArenaUINames.RowBorder(i, e), !r.Selected);
            Hide(TownArenaUINames.RowStatusBack(i), !r.StatusVisible);
        }
        for (int s = 0; s < 5; ++s)
        {
            var c = s < v.Cards.Count ? v.Cards[s] : null;
            if (c is null || !c.Visible) { result.Hidden.Add(TownArenaUINames.Card(s)); continue; }
            Hide(TownArenaUINames.Text(s), !c.TextVisible);
            Hide(TownArenaUINames.Terms(s), !c.TermsVisible);
            Hide(TownArenaUINames.CardBook(s), !c.BookVisible);
            for (int m = 0; m < 4; ++m)
            {
                var p = m < c.Portraits.Count ? c.Portraits[m] : null;
                Hide(TownArenaUINames.Portrait(s, m), p is null || !p.PortraitVisible);
                Hide(TownArenaUINames.Placeholder(s, m), p is null || !p.PlaceholderVisible);
                Hide(TownArenaUINames.Name(s, m), p is null || !p.NameVisible);
                Hide(TownArenaUINames.Stats(s, m), p is null || !p.StatsVisible);
            }
        }
        Hide(TownArenaUINames.SkarnPortrait, v.SkarnKey.Length == 0);
        Hide(TownArenaUINames.SkarnPlaceholder, v.SkarnKey.Length != 0);
    }

    // C++: TownArenaUI.cpp:162-170 Wrap: the edit box at (x, y, max(1, width), max(100, Body * 4)), its insets (the box less its text region), its
    // text's height word-wrapped in the text region, at least Body; with a limit, at most max(Body + insets, limit). A Kenshi_WordWrap ("bounded")
    // has 30 px of insets each way (the box is at least 100 high, so all of them), the Empty skins none; the rows' text is the painted font.
    int Wrap(string widget, string text, int x, int y, int width, int limit = 0, bool bounded = false, bool painted = false)
    {
        int w = Math.Max(1, width);
        int insets = bounded ? Math.Min(PgTemplates.WordWrapInsetH, Math.Max(100, body * 4)) : 0;
        int region = bounded ? Math.Max(1, w - PgTemplates.WordWrapInsetW) : w;
        int textHeight = painted ? measure.Painted(text, region).Height : measure.Standard(text, region).Height;
        int height = Math.Max(body, textHeight) + insets;
        if (limit > 0) height = Math.Min(height, Math.Max(body + insets, limit));
        Set(widget, x, y, w, height);
        return height;
    }

    string RowCaption(int i) => v.Rows[i].Caption;

    // C++: TownArenaUI.cpp:404-420 LayoutRow (the row button relative to the roster canvas; its parts relative to the button)
    int LayoutRow(int i, int x, int y, int width)
    {
        int textX = 48 + 2 * gap;
        int textHeight = Wrap(TownArenaUINames.RowText(i), RowCaption(i), textX, gap, width - textX - gap, painted: true);
        Set(TownArenaUINames.RowStatusBack(i), textX, gap + textHeight + gap, Math.Max(1, width - textX - gap), 6);
        RefreshStatusBar(i);
        int height = Math.Max(48, textHeight + gap + 6) + 2 * gap;
        Move(TownArenaUINames.RowPortrait(i), gap, gap);
        Set(TownArenaUINames.Row(i), x, y, Math.Max(1, width), height);
        int edge = Math.Min(Math.Max(2, gap / 3), Math.Max(1, width / 2));
        Set(TownArenaUINames.RowBorder(i, 0), 0, 0, Math.Max(1, width), edge);
        Set(TownArenaUINames.RowBorder(i, 1), 0, height - edge, Math.Max(1, width), edge);
        Set(TownArenaUINames.RowBorder(i, 2), 0, 0, edge, height);
        Set(TownArenaUINames.RowBorder(i, 3), Math.Max(0, width - edge), 0, edge, height);
        return height;
    }

    // C++: TownArenaUI.cpp:270-279 RefreshStatusBar: the fill (relative to the back) as wide as the recovery fraction of the back, rounded; hidden at 0
    void RefreshStatusBar(int i)
    {
        var r = v.Rows[i];
        if (!r.StatusVisible) return;
        var back = result.Rects[TownArenaUINames.RowStatusBack(i)];
        int width = (int)(back.W * r.Recovery + .5f);
        if (width <= 0) result.Hidden.Add(TownArenaUINames.RowStatusFill(i));
        else result.Hidden.Remove(TownArenaUINames.RowStatusFill(i));
        Set(TownArenaUINames.RowStatusFill(i), 0, 0, width, back.H);
    }

    TownArenaCardView<TId>? CardOf(int slot) => slot < v.Cards.Count ? v.Cards[slot] : null;

    static string PlaceholderOf(TownArenaCardView<TId> c, int m) => m < c.Portraits.Count ? c.Portraits[m].Placeholder : "";
    static string NameOf(TownArenaCardView<TId> c, int m) => m < c.Portraits.Count ? c.Portraits[m].Name : "";
    static string StatsOf(TownArenaCardView<TId> c, int m) => m < c.Portraits.Count ? c.Portraits[m].Stats : "";

    // C++: TownArenaUI.cpp:578-623 LayoutCardContent (the card's parts relative to its root): the card's natural height at that width
    int LayoutCardContent(int slot, int width)
    {
        var c = CardOf(slot)!;
        int pad = gap + Math.Max(3, gap / 2);
        int inner = Math.Max(1, width - pad - gap);
        int y = gap;
        y += Wrap(TownArenaUINames.Heading(slot), c.Heading, pad, y, inner) + gap;
        // A large solo portrait; pairs share a row and trios use two rows.
        // Missing members keep their position so portrait order matches names.
        int count = c.PortraitCount;
        int columns = Math.Max(1, Math.Min(2, count)), portraitRows = count > 0 ? (count + columns - 1) / columns : 0;
        int cell = Math.Max(1, (inner - (columns - 1) * gap) / columns);
        int portraitSize = Math.Max(1, Math.Min(count == 1 ? 192 : 112, cell));
        for (int row = 0; row < portraitRows; ++row)
        {
            int rowCount = Math.Min(columns, count - row * columns);
            int rowWidth = rowCount * cell + (rowCount - 1) * gap;
            int memberDetailsHeight = 0, imageHeight = portraitSize;
            for (int column = 0; column < rowCount; ++column)
            {
                int member = row * columns + column;
                int x = pad + (inner - rowWidth) / 2 + column * (cell + gap);
                Set(TownArenaUINames.Portrait(slot, member), x + (cell - portraitSize) / 2, y, portraitSize, portraitSize);
                int placeholderHeight = Wrap(TownArenaUINames.Placeholder(slot, member), PlaceholderOf(c, member), x, y, cell);
                if (Visible(TownArenaUINames.Placeholder(slot, member))) imageHeight = Math.Max(imageHeight, placeholderHeight);
                Move(TownArenaUINames.Placeholder(slot, member), x, y + Math.Max(0, (portraitSize - placeholderHeight) / 2));
            }
            for (int column = 0; column < rowCount; ++column)
            {
                int member = row * columns + column;
                int x = pad + (inner - rowWidth) / 2 + column * (cell + gap);
                if (Visible(TownArenaUINames.Name(slot, member)))
                {
                    int detailY = y + imageHeight + gap / 2;
                    detailY += Wrap(TownArenaUINames.Name(slot, member), NameOf(c, member), x, detailY, cell) + gap / 3;
                    int statsHeight = Wrap(TownArenaUINames.Stats(slot, member), StatsOf(c, member), x, detailY, cell);
                    memberDetailsHeight = Math.Max(memberDetailsHeight, detailY + statsHeight - (y + imageHeight + gap / 2));
                }
            }
            y += imageHeight + gap / 2 + memberDetailsHeight + gap;
        }
        if (Visible(TownArenaUINames.Text(slot))) y += Wrap(TownArenaUINames.Text(slot), c.Text, pad, y, inner) + gap;
        if (Visible(TownArenaUINames.Terms(slot))) y += Wrap(TownArenaUINames.Terms(slot), c.Terms, pad, y, inner) + gap;
        return Visible(TownArenaUINames.CardBook(slot)) ? Math.Max(body * 28, y + buttonHeight + gap) : y + gap;
    }

    static readonly string[] DivisionCaptions = ["Easy", "Medium", "Hard"];   // :844

    // C++: TownArenaUI.cpp:624-634 Buttons (the division buttons, relative to the challenges canvas)
    int Buttons(int x, int y, int width)
    {
        int count = 3, height = buttonHeight;
        int natural = 0;
        for (int i = 0; i < count; ++i) natural = Math.Max(natural, measure.ButtonWidth(DivisionCaptions[i]) + 4 * gap);
        int columns = Math.Max(1, Math.Min(count, (width + gap) / (natural + gap)));
        int cell = Math.Max(1, (width - (columns - 1) * gap) / columns);
        for (int i = 0; i < count; ++i)
            Set(TownArenaUINames.Division(i), x + (i % columns) * (cell + gap), y + (i / columns) * (height + gap), cell, height);
        return ((count + columns - 1) / columns) * (height + gap) - gap;
    }

    // C++: TownArenaUI.cpp:635-645 LayoutSkarn (the panel relative to the challenges canvas, its parts relative to the panel)
    void LayoutSkarn(int x, int y, int width, int height)
    {
        Set(TownArenaUINames.SkarnPanel, x, y, width, height);
        int labelHeight = body + gap;
        int portraitSize = Math.Max(1, Math.Min(width - 2 * gap, height - labelHeight - 2 * gap));
        int portraitX = (width - portraitSize) / 2;
        Set(TownArenaUINames.SkarnPortrait, portraitX, gap, portraitSize, portraitSize);
        Set(TownArenaUINames.SkarnPlaceholder, gap, gap, Math.Max(1, width - 2 * gap), portraitSize);
        Set(TownArenaUINames.SkarnState, gap, gap + portraitSize, Math.Max(1, width - 2 * gap), labelHeight);
    }

    // C++: TownArenaUI.cpp:646-657 LayoutRoster (the rows relative to the roster canvas)
    void LayoutRoster()
    {
        for (int pass = 0; pass < 2; ++pass)
        {
            int width = Math.Max(1, roster.ViewW);
            int y = 0;
            for (int i = 0; i < v.Rows.Count; ++i) y += LayoutRow(i, 0, y, width) + gap;
            if (v.Rows.Count == 0) y = Wrap(TownArenaUINames.EmptyRoster, "No squad fighters available.", gap, gap, width - 2 * gap) + 2 * gap;
            SetCanvas(roster, width, Math.Max(y, roster.ViewH));
        }
    }

    // C++: TownArenaUI.cpp:658-674 LayoutSidebar (relative to the sidebar canvas)
    void LayoutSidebar()
    {
        for (int pass = 0; pass < 2; ++pass)
        {
            int width = Math.Max(1, sidebar.ViewW - 2 * gap);
            int y = gap;
            y += Wrap(TownArenaUINames.Team, v.Squad, gap, y, width) + gap;
            y += Wrap(TownArenaUINames.RosterHint, "Click a fighter to select. Click again to deselect.", gap, y, width) + gap;
            int listH = Math.Max(body * 8, sidebar.ViewH - y - gap);
            SetScroll(roster, gap, y, width, listH); y += listH + gap;
            LayoutRoster();
            SetCanvas(sidebar, width + 2 * gap, Math.Max(y, sidebar.ViewH));
        }
    }

    // C++: TownArenaUI.cpp:476-493 LayoutAccent (the accent relative to the card root, its strips and borders relative to the accent; the colours
    // are the window side's, from the card's rarity)
    void LayoutAccent(int slot, int width, int height, int rarity)
    {
        Hide(TownArenaUINames.Accent(slot), rarity < 0);
        if (rarity < 0) return;
        Size(TownArenaUINames.Accent(slot), width, height);
        for (int i = 0; i < 16; ++i)
        {
            int left = width * i / 16, right = width * (i + 1) / 16;
            Set(TownArenaUINames.Fade(slot, i), left, 0, Math.Max(1, right - left), height);
        }
        int rail = Math.Max(3, gap / 2);
        Set(TownArenaUINames.Border(slot, 0), 0, 0, rail, height);
        Set(TownArenaUINames.Border(slot, 1), rail, 0, Math.Max(1, width - rail), 1);
        Set(TownArenaUINames.Border(slot, 2), rail, height - 1, Math.Max(1, width - rail), 1);
        Set(TownArenaUINames.Border(slot, 3), width - 1, 0, 1, height);
    }

    // C++: TownArenaUI.cpp:675-783 LayoutTownPanel
    void LayoutTownPanel()
    {
        // :676-681 the window at 1/25 of the view from its top left, 23/25 of it in size (when the view size changed: here every layout)
        int vw = input.ViewWidth, vh = input.ViewHeight;
        result.Window = new LayoutRect(vw / 25, vh / 25, vw * 23 / 25, vh * 23 / 25);
        result.ClientWidth = Math.Max(0, result.Window.W - ArenaUILayout.WindowClientMarginW);
        result.ClientHeight = Math.Max(0, result.Window.H - ArenaUILayout.WindowClientMarginH);
        int sizeW = result.ClientWidth, sizeH = result.ClientHeight;
        int width = Math.Max(1, sizeW - 2 * gap);
        int buttonTop = sizeH - gap - buttonHeight;
        Set(TownArenaUINames.Book, gap, buttonTop, width, buttonHeight);
        int statusHeight = Wrap(TownArenaUINames.Status, v.Feedback, gap, 0, width, Math.Max(body, sizeH / 9), bounded: true);
        int statusTop = buttonTop - gap - statusHeight;
        Move(TownArenaUINames.Status, gap, statusTop);
        int available = Math.Max(1, statusTop - 2 * gap);
        bool stacked = width < body * 42;
        result.Stacked = stacked;
        if (stacked)
        {
            int sideHeight = Math.Max(1, (available - gap) * 2 / 5);
            SetScroll(sidebar, gap, gap, width, sideHeight);
            SetScroll(challenges, gap, sideHeight + 2 * gap, width, Math.Max(1, available - sideHeight - gap));
        }
        else
        {
            int sideWidth = Math.Min(width * 2 / 5, Math.Max(body * 14, width / 5));
            SetScroll(sidebar, gap, gap, sideWidth, available);
            SetScroll(challenges, sideWidth + 2 * gap, gap, Math.Max(1, width - sideWidth - gap), available);
        }
        LayoutSidebar();
        for (int pass = 0; pass < 2; ++pass) ChallengesPass();
    }

    // C++: TownArenaUI.cpp:694-778 (one pass of the challenges view, relative to its canvas)
    void ChallengesPass()
    {
        int rowWidth = Math.Max(1, challenges.ViewW - 2 * gap);
        int y = gap;
        int headerTop = y;
        int skarnWidth = Math.Min(Math.Max(body * 8, 96), Math.Max(1, rowWidth / 3));
        int headerWidth = Math.Max(1, rowWidth - skarnWidth - gap);
        y += Buttons(gap, y, headerWidth) + gap;
        int actionWidth = Math.Min(headerWidth / 3, Math.Max(body * 13, headerWidth / 5));
        int timerWidth = Math.Min(headerWidth / 3, Math.Max(body * 13, headerWidth / 5));
        int directWidth = headerWidth - actionWidth - timerWidth - 2 * gap;
        if (directWidth >= body * 16)
        {
            int directHeight = Wrap(TownArenaUINames.Direct, v.Direct, gap, y, directWidth, bounded: true);
            int timerHeight = Wrap(TownArenaUINames.NextRefresh, v.NextRefresh, gap + directWidth + gap, y, timerWidth, bounded: true);
            Set(TownArenaUINames.BuyRefresh, gap + directWidth + gap + timerWidth + gap, y, actionWidth, buttonHeight);
            y += Math.Max(buttonHeight, Math.Max(directHeight, timerHeight)) + gap;
        }
        else
        {
            y += Wrap(TownArenaUINames.Direct, v.Direct, gap, y, headerWidth, bounded: true) + gap;
            int stackedActionWidth = Math.Min(headerWidth, Math.Max(body * 13, headerWidth / 3));
            int stackedTimerWidth = headerWidth - stackedActionWidth - gap;
            if (stackedTimerWidth >= body * 8)
            {
                int timerHeight = Wrap(TownArenaUINames.NextRefresh, v.NextRefresh, gap, y, stackedTimerWidth, bounded: true);
                Set(TownArenaUINames.BuyRefresh, gap + stackedTimerWidth + gap, y, stackedActionWidth, buttonHeight);
                y += Math.Max(buttonHeight, timerHeight) + gap;
            }
            else
            {
                y += Wrap(TownArenaUINames.NextRefresh, v.NextRefresh, gap, y, headerWidth, bounded: true) + gap;
                Set(TownArenaUINames.BuyRefresh, gap, y, headerWidth, buttonHeight);
                y += buttonHeight + gap;
            }
        }
        int skarnHeight = Math.Max(body * 6, y - headerTop - gap);
        LayoutSkarn(gap + headerWidth + gap, headerTop, skarnWidth, skarnHeight);
        y = Math.Max(y, headerTop + skarnHeight + gap);
        var visible = new List<int>();
        int minimum = body * 13;
        int pad = gap + Math.Max(3, gap / 2);
        for (int i = 0; i < 5; ++i)
            if (Visible(TownArenaUINames.Card(i)))
            {
                visible.Add(i);
                minimum = Math.Max(minimum, measure.ButtonWidth("Book challenge") + 5 * gap + pad);
            }
        bool prompt = v.SelectionPrompt;
        bool singleRow = v.Division == 2 && !prompt;   // displayedDivision == 2 && !showingSelectionPrompt
        TownCardLayout.Grid grid;
        if (prompt) grid = new TownCardLayout.Grid { Columns = 1, Width = rowWidth, Gap = gap };
        else grid = singleRow ? TownCardLayout.SingleRow(rowWidth, minimum, gap, visible.Count) : TownCardLayout.Fit(rowWidth, minimum, gap);
        result.Grid = grid;
        result.SingleRow = singleRow;
        int columns = Math.Max(1, grid.Columns);
        int rowCount = (visible.Count + columns - 1) / columns;
        int[] rowHeights = new int[5];
        for (int row = 0; row < rowCount; ++row)
        {
            int first = row * columns;
            int count = Math.Min(columns, visible.Count - first);
            for (int column = 0; column < count; ++column)
                rowHeights[row] = Math.Max(rowHeights[row], LayoutCardContent(visible[first + column], grid.Width));
        }
        int baseProgressionTop = y;
        for (int row = 0; row < rowCount; ++row) baseProgressionTop += rowHeights[row] + gap;
        int progressionHeight = Wrap(TownArenaUINames.Progress, v.Progress, gap, baseProgressionTop, rowWidth, bounded: true);
        int spare = challenges.ViewH - (baseProgressionTop + progressionHeight + gap);
        if (spare > 0 && rowCount > 0 && !prompt)
            for (int row = 0; row < rowCount; ++row)
                rowHeights[row] += spare / rowCount + (row < spare % rowCount ? 1 : 0);
        for (int row = 0; row < rowCount; ++row)
        {
            int first = row * columns;
            int count = Math.Min(columns, visible.Count - first);
            int height = rowHeights[row];
            for (int column = 0; column < count; ++column)
            {
                int slot = visible[first + column];
                Set(TownArenaUINames.Card(slot), gap + grid.Left(column), y, grid.Width, height);
                Set(TownArenaUINames.CardBook(slot), pad, height - buttonHeight - gap, Math.Max(1, grid.Width - pad - gap), buttonHeight);
                LayoutAccent(slot, grid.Width, height, prompt ? -1 : CardOf(slot)!.Rarity);
            }
            y += height + gap;
        }
        y += Wrap(TownArenaUINames.Progress, v.Progress, gap, y, rowWidth, bounded: true) + gap;
        int contentWidth = singleRow ? Math.Max(rowWidth, grid.ContentWidth(visible.Count)) : rowWidth;
        SetCanvas(challenges, contentWidth + 2 * gap, Math.Max(y, challenges.ViewH));
    }

    // ---------- the tips (PortraitTip :184-221, PlaceTip :171-183), sized when one shows ----------

    static int TipWrap(IPgTextMeasure m, string text, int width, int limit, int body)
    {
        int w = Math.Max(1, width);
        int insets = Math.Min(PgTemplates.WordWrapInsetH, Math.Max(100, body * 4));   // a Kenshi_WordWrap measured at least 100 high
        int height = Math.Max(body, m.Standard(text, Math.Max(1, w - PgTemplates.WordWrapInsetW)).Height) + insets;
        if (limit > 0) height = Math.Min(height, Math.Max(body + insets, limit));
        return height;
    }

    /// <summary>C++ PortraitTip :207-218 (a fighter tip): the tip's size in a client of <paramref name="clientW"/> x <paramref name="clientH"/>: as wide
    /// as its text's widest line plus the edit box's insets and two gaps (at most the client less two gaps), as high as Wrap gives at that width (at
    /// most the client less two gaps). The text fills the background (its rectangle (0, 0, W, H)).</summary>
    public static (int W, int H) FighterTip(int clientW, int clientH, int fontHeight, string text, IPgTextMeasure m)
    {
        int gap = NativeUI.Spacing(fontHeight), body = Math.Max(1, fontHeight);
        int maxWidth = Math.Max(1, clientW - 2 * gap);
        // :213 fighterTip->setSize(maxWidth, max(100, Body * 4)); :214 the inset = the box less its text region (Kenshi_WordWrap)
        int inset = Math.Min(PgTemplates.WordWrapInsetW, maxWidth);
        int textWidth = m.Standard(text, Math.Max(1, maxWidth - PgTemplates.WordWrapInsetW)).Width;
        int width = Math.Min(maxWidth, textWidth + inset + 2 * gap);
        int height = TipWrap(m, text, width, clientH - 2 * gap, body);
        return (Math.Max(1, width), height);
    }

    /// <summary>C++ PortraitTip :193-206 (the Skarn tip): the seven lines' rectangles inside the background and the background's size.</summary>
    public static (int W, int H, LayoutRect[] Lines) SkarnTip(int clientW, int clientH, int fontHeight, IReadOnlyList<string> lines, IPgTextMeasure m)
    {
        int gap = NativeUI.Spacing(fontHeight), body = Math.Max(1, fontHeight);
        int width = Math.Max(body * 22, 180), y = gap;
        for (int i = 0; i < 7; ++i)   // :196-198 each line at (size.width, Body * 2): its text's width in that box's text region
            width = Math.Max(width, m.Standard(i < lines.Count ? lines[i] : "", Math.Max(1, clientW - PgTemplates.WordWrapInsetW)).Width + 3 * gap);
        width = Math.Min(width, clientW - 2 * gap);
        var rects = new LayoutRect[7];
        for (int i = 0; i < 7; ++i)
        {
            int w = Math.Max(1, width - 2 * gap);
            int h = TipWrap(m, i < lines.Count ? lines[i] : "", w, 0, body);
            rects[i] = new LayoutRect(gap, y, w, h);
            y += h + (i == 0 || i == 5 ? gap : gap / 2);
        }
        return (width, y + gap / 2, rects);
    }

    /// <summary>C++ PlaceTip :171-183: the tip's position in the client for the pointer at (<paramref name="px"/>, <paramref name="py"/>) relative
    /// to the client's top left: 2 gaps right and 3 gaps below it, flipped to the other side when it would cross the client's edge less a gap, at
    /// least a gap from the top left.</summary>
    public static (int X, int Y) PlaceTip(int clientW, int clientH, int fontHeight, int px, int py, int tipW, int tipH)
    {
        int gap = NativeUI.Spacing(fontHeight);
        int x = px + 2 * gap;
        int y = py + 3 * gap;
        if (x + tipW > clientW - gap) x = px - tipW - gap;
        if (y + tipH > clientH - gap) y = py - tipH - gap;
        return (Math.Max(gap, x), Math.Max(gap, y));
    }
}
