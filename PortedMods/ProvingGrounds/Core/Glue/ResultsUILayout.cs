namespace ProvingGrounds.Core.Glue;

// Port of the Results window's pixel arithmetic, upstream/ProvingGrounds/plugin/src/ResultsUI.cpp (Proving Grounds by Shutori, GPLv3), task 4.6a:
// WrappedHeight (:89-96), LayoutPortrait (:147-167), LayoutResultCard (:168-201), FitWindowToContent (:225-273) and LayoutWindow (:274-365), with
// Core ResultsLayout / LeaderboardLayout for the ceremony. The C++ creates its widgets once (EnsureWindow :366-458) and positions them on each
// show and resize from the window's client and the measured texts; the port computes the same rectangles here, purely (the texts measured through
// IPgTextMeasure: MyGUI's getTextSize, done by the window side with Meitou's text measurer), and the window side (src/Ui/ResultsUI.cs) writes
// them into the layout XML.

/// <summary>The text sizes the windows of task 4.6a ask MyGUI for (<c>getTextSize</c>), measured by the window side.</summary>
public interface IPgTextMeasure
{
    /// <summary>The size of <paramref name="text"/> in the standard font (Kenshi_TextboxStandardText, the client of Kenshi_WordWrapEmpty and
    /// Kenshi_WordWrap) word-wrapped at <paramref name="wrapWidth"/> pixels (0: no wrapping, a TextBox): the widest line and the height of all
    /// lines.</summary>
    (int Width, int Height) Standard(string text, int wrapWidth);

    /// <summary>The width of <paramref name="caption"/> on a <c>Kenshi_Button1</c> (<c>Button::getTextSize().width</c>, one line).</summary>
    int ButtonWidth(string caption);
}

/// <summary>Kenshi template numbers both windows need (kenshi_templates.xml; Meitou arranges templates at TemplateScale 1).</summary>
public static class PgTemplates
{
    /// <summary>Kenshi_WordWrap (:543-554): its text client is "10 10 2 2" in a 32 x 32 root, stretched: 10 px left / top and 20 px right / bottom,
    /// 30 px of insets each way (<c>getHeight() - getTextRegion().height</c>, LeaderboardUI.cpp:481).</summary>
    public const int WordWrapInsetW = 30, WordWrapInsetH = 30;
}

/// <summary>What the Results window's arithmetic reads.</summary>
public sealed record ResultsLayoutInput
{
    public int ViewWidth { get; init; } = 1920;
    public int ViewHeight { get; init; } = 1080;
    /// <summary>The standard text's font height (<c>getFontHeight</c>, NativeUI::Spacing / RowHeight).</summary>
    public int FontHeight { get; init; } = NativeUI.DefaultFontHeight;
    public string Header { get; init; } = "";
    public string Context { get; init; } = "";
    public bool EmptyVisible { get; init; }
    /// <summary>The visible cards, podium order (slot 0 = place 1): rank caption, name line, stats line.</summary>
    public IReadOnlyList<(string Rank, string Name, string Stats)> Slots { get; init; } = [];
    /// <summary>The widths the wrapped header, context and empty labels had when FitWindowToContent measured them (their coordinates of the
    /// previous layout: 100 at creation, :382), and whether the scroll view showed its vertical bar then (its view width: :251-252).</summary>
    public int HeaderWidthBefore { get; init; } = 100;
    public int ContextWidthBefore { get; init; } = 100;
    public int EmptyWidthBefore { get; init; } = 100;
    public bool ScrollBarBefore { get; init; }
}

/// <summary>The rectangles of one layout pass, relative to each widget's parent (the scroll view's children to its canvas), the scroll view's
/// canvas, the window on the screen and its client, and the state the next show's measuring pass starts from.</summary>
public sealed class PgWindowLayout
{
    public Dictionary<string, LayoutRect> Rects { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, (int W, int H)> Canvas { get; } = new(StringComparer.Ordinal);
    /// <summary>Widgets a layout pass hides (setVisible(false) / SetAccent with visible false).</summary>
    public HashSet<string> Hidden { get; } = new(StringComparer.Ordinal);
    public LayoutRect Window { get; set; }
    public int ClientWidth { get; set; }
    public int ClientHeight { get; set; }
    public int ContentHeight { get; set; }
    public bool ScrollBar { get; set; }
    public int Gap { get; set; }
    public int Body { get; set; }
    public int Edge { get; set; }
    /// <summary>Results: the widths the wrapped header, context and empty labels were left with (the next show measures them there).</summary>
    public int HeaderWidth { get; set; }
    public int ContextWidth { get; set; }
    public int EmptyWidth { get; set; }
    /// <summary>Leaderboard: the podium slot's rank of each featured row (its accents' colour, LayoutFeatured :282).</summary>
    public Dictionary<string, int> FeaturedRank { get; } = new(StringComparer.Ordinal);
}

/// <summary>The Results window's widget names: the C++'s own (EnsureWindow :374-454).</summary>
public static class ResultsUINames
{
    public const string Window = "ProvingGroundsResultsWindow", Measure = "PG_ResultsMeasure", Scroll = "PG_ResultsScroll";
    public const string Header = "PG_ResultsHeader", Context = "PG_ResultsContext", Empty = "PG_ResultsEmpty", Close = "PG_ResultsClose";
    public const string RailLeft = "PG_ResultsHeaderRailLeft", RailRight = "PG_ResultsHeaderRailRight";

    /// <summary>The MyGUI event ResultsUI.cpp binds each button to (task 4.4b): Close results to eventMouseButtonClick (:445); no other button
    /// is bound.</summary>
    public static UiButtonEvent ButtonEvent(string widget) => UiButtonEvent.Click;

    /// <summary>C++ :407 <c>PG_ResultsSlot_%d</c>.</summary>
    public static string Slot(int i) => "PG_ResultsSlot_" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public static string Portrait(string slot) => slot + "_Portrait";
    public static string Placeholder(string slot) => slot + "_PortraitPlaceholder";
    public static string Rank(string slot) => slot + "_Rank";
    public static string Name(string slot) => slot + "_Name";
    public static string Stats(string slot) => slot + "_Stats";
    /// <summary>C++ :435-440 <c>_PortraitBorder0..3</c>, <c>_CardBorder0..3</c>.</summary>
    public static string PortraitBorder(string slot, int edge) => slot + "_PortraitBorder" + (char)('0' + edge);
    public static string CardBorder(string slot, int edge) => slot + "_CardBorder" + (char)('0' + edge);
}

/// <summary>The Results window's pixel arithmetic (task 4.6a), C++ function by function. Pure.</summary>
public sealed class ResultsUILayout
{
    readonly ResultsLayoutInput input;
    readonly IPgTextMeasure measure;
    readonly PgWindowLayout result = new();
    readonly int gap, body, closeHeight;
    int headerWidth, contextWidth, emptyWidth;
    bool bar;

    ResultsUILayout(ResultsLayoutInput input, IPgTextMeasure measure)
    {
        this.input = input;
        this.measure = measure;
        gap = NativeUI.Spacing(input.FontHeight);
        // C++ :64 BodyHeight: g_measure's font height or its text height (its caption is the header, :573)
        body = Math.Max(1, Math.Max(input.FontHeight, measure.Standard(input.Header, 0).Height));
        // NativeUI::RowHeight(g_closeBtn, 0): the standard sample's heights stand for the button's (as the Arena window, task 4.5b)
        closeHeight = NativeUI.RowHeight(measure.Standard(ResultsUiTexts.Close, 0).Height, input.FontHeight, 0);
        headerWidth = input.HeaderWidthBefore;
        contextWidth = input.ContextWidthBefore;
        emptyWidth = input.EmptyWidthBefore;
        bar = input.ScrollBarBefore;
        result.Gap = gap;
        result.Body = body;
    }

    /// <summary>The window, its client and every widget's rectangle after a show (FitWindowToContent(true) then LayoutWindow, :598-599).</summary>
    public static PgWindowLayout Compute(ResultsLayoutInput input, IPgTextMeasure measure)
    {
        var l = new ResultsUILayout(input, measure);
        l.Creation();
        l.FitWindowToContent();
        l.LayoutWindow();
        return l.result;
    }

    void Set(string widget, int x, int y, int w, int h) => result.Rects[widget] = new LayoutRect(x, y, w, h);

    void Show(string widget, bool visible)
    {
        if (visible) result.Hidden.Remove(widget); else result.Hidden.Add(widget);
    }

    // C++: ResultsUI.cpp:366-458 EnsureWindow: every widget at 0 0 100 24 (:382), the scroll view at 0 0 300 200; hidden: g_measure, the cards
    // (a card shows when filled), and the portrait ImageBoxes
    void Creation()
    {
        Set(ResultsUINames.Measure, 0, 0, 100, 24);
        Show(ResultsUINames.Measure, false);
        Set(ResultsUINames.Scroll, 0, 0, 300, 200);
        foreach (var n in new[] { ResultsUINames.Header, ResultsUINames.Context, ResultsUINames.Empty, ResultsUINames.RailLeft, ResultsUINames.RailRight,
                     ResultsUINames.Close })
            Set(n, 0, 0, 100, 24);
        for (int i = 0; i < 3; ++i)
        {
            string s = ResultsUINames.Slot(i);
            foreach (var n in new[] { s, ResultsUINames.Portrait(s), ResultsUINames.Placeholder(s), ResultsUINames.Rank(s), ResultsUINames.Name(s),
                         ResultsUINames.Stats(s) })
                Set(n, 0, 0, 100, 24);
            for (int e = 0; e < 4; ++e) { Set(ResultsUINames.PortraitBorder(s, e), 0, 0, 100, 24); Set(ResultsUINames.CardBorder(s, e), 0, 0, 100, 24); }
            Show(s, i < input.Slots.Count);   // ShowFromSnapshot :584-592
        }
        Show(ResultsUINames.Empty, input.EmptyVisible);   // :594-595
    }

    int Count => Math.Min(3, input.Slots.Count);

    /// <summary>A scroll view's view size (<c>getViewCoord</c>) for its rectangle (ArenaUILayout's Kenshi_ScrollView numbers).</summary>
    static (int W, int H) ViewOf(LayoutRect scroll, bool bar) =>
        (Math.Max(0, scroll.W - ArenaUILayout.ScrollInsetW - (bar ? ArenaUILayout.ScrollBarW : 0)), Math.Max(0, scroll.H - ArenaUILayout.ScrollInsetH));

    // C++: ResultsUI.cpp:225-273 FitWindowToContent (forced: the show)
    void FitWindowToContent()
    {
        int screenW = input.ViewWidth, screenH = input.ViewHeight;
        int count = Count;
        var metrics = LeaderboardLayout.FitCeremonialMetrics(body, gap);
        int naturalCell = metrics.ChampionPortrait + 20 * gap;
        int contentW = count > 0
            ? count * naturalCell + (count - 1) * gap
            : measure.Standard(ResultsUiTexts.Empty, emptyWidth).Width + 4 * gap;
        contentW = Math.Max(contentW, measure.Standard(input.Header, headerWidth).Width + 8 * gap);
        contentW = Math.Max(contentW, measure.Standard(input.Context, contextWidth).Width + 4 * gap);
        contentW = Math.Max(contentW, measure.ButtonWidth(ResultsUiTexts.Close) + 4 * gap);

        int chromeWidth = ArenaUILayout.WindowClientMarginW, chromeHeight = ArenaUILayout.WindowClientMarginH;
        int scrollInsetWidth = ArenaUILayout.ScrollInsetW + (bar ? ArenaUILayout.ScrollBarW : 0);
        int maximumWidth = screenW * 92 / 100;
        int maximumHeight = screenH * 90 / 100;
        int desiredClientWidth = contentW + 4 * gap + scrollInsetWidth;
        int outerWidth = Math.Min(maximumWidth, Math.Max(320, desiredClientWidth + chromeWidth));

        // Use the screen-height cap for the measuring pass, then collapse the window around the actual canvas and persistent close button.
        SetWindow((screenW - outerWidth) / 2, (screenH - maximumHeight) / 2, outerWidth, maximumHeight);
        LayoutWindow();
        int scrollInsetHeight = ArenaUILayout.ScrollInsetH;
        int desiredClientHeight = result.ContentHeight + scrollInsetHeight + closeHeight + 3 * gap;
        int outerHeight = Math.Min(maximumHeight, Math.Max(180, desiredClientHeight + chromeHeight));
        SetWindow((screenW - outerWidth) / 2, (screenH - outerHeight) / 2, outerWidth, outerHeight);
    }

    void SetWindow(int x, int y, int w, int h)
    {
        result.Window = new LayoutRect(x, y, w, h);
        result.ClientWidth = Math.Max(0, w - ArenaUILayout.WindowClientMarginW);
        result.ClientHeight = Math.Max(0, h - ArenaUILayout.WindowClientMarginH);
    }

    // C++: ResultsUI.cpp:89-96 WrappedHeight (the skin's insets: Kenshi_WordWrapEmpty has none)
    int WrappedHeight(string label, string text, int x, int y, int width)
    {
        int w = Math.Max(1, width);
        int height = Math.Max(body, measure.Standard(text, w).Height);
        Set(label, x, y, w, height);
        return height;
    }

    void SetAccent(string accent, int x, int y, int w, int h, bool visible)
    {
        Set(accent, x, y, w, h);
        Show(accent, visible);
    }

    // C++: ResultsUI.cpp:147-167 LayoutPortrait (the portrait / placeholder visibility is the window side's: it knows the portrait)
    void LayoutPortrait(string slot, int x, int y, int size, int edge)
    {
        Set(ResultsUINames.Portrait(slot), x, y, size, size);
        Set(ResultsUINames.Placeholder(slot), x, y, size, size);
        SetAccent(ResultsUINames.PortraitBorder(slot, 0), x, y, edge, size, true);
        SetAccent(ResultsUINames.PortraitBorder(slot, 1), x, y, size, edge, true);
        SetAccent(ResultsUINames.PortraitBorder(slot, 2), x, y + size - edge, size, edge, true);
        SetAccent(ResultsUINames.PortraitBorder(slot, 3), x + size - edge, y, edge, size, true);
    }

    // C++: ResultsUI.cpp:168-201 LayoutResultCard
    int LayoutResultCard(int slotIndex, int left, int top, int width, int portrait, int edge)
    {
        string slot = ResultsUINames.Slot(slotIndex);
        var texts = input.Slots[slotIndex];
        Show(slot, true);
        Set(ResultsUINames.Rank(slot), gap, gap, Math.Max(1, width - 2 * gap), body);
        int portraitTop = body + 2 * gap;
        LayoutPortrait(slot, Math.Max(0, (width - portrait) / 2), portraitTop, portrait, edge);
        int nameTop = portraitTop + portrait + gap;
        int innerWidth = Math.Max(1, width - 2 * gap);
        int nameHeight = WrappedHeight(ResultsUINames.Name(slot), texts.Name, gap, nameTop, innerWidth);
        int statsTop = nameTop + nameHeight + gap;
        int statsHeight = WrappedHeight(ResultsUINames.Stats(slot), texts.Stats, gap, statsTop, innerWidth);
        int height = statsTop + statsHeight + 2 * gap;
        Set(slot, left, top, width, height);
        SetAccent(ResultsUINames.CardBorder(slot, 0), 0, 0, edge, height, true);
        SetAccent(ResultsUINames.CardBorder(slot, 1), 0, 0, width, edge, true);
        SetAccent(ResultsUINames.CardBorder(slot, 2), 0, height - edge, width, edge, true);
        SetAccent(ResultsUINames.CardBorder(slot, 3), width - edge, 0, edge, height, true);
        return height;
    }

    // C++: ResultsUI.cpp:274-365 LayoutWindow
    void LayoutWindow()
    {
        int sizeW = result.ClientWidth, sizeH = result.ClientHeight;
        int width = Math.Max(1, sizeW - 2 * gap);
        int closeTop = Math.Max(gap, sizeH - gap - closeHeight);
        int closeWidth = Math.Min(width, measure.ButtonWidth(ResultsUiTexts.Close) + 4 * gap);
        Set(ResultsUINames.Close, (sizeW - closeWidth) / 2, closeTop, closeWidth, closeHeight);
        var scroll = new LayoutRect(gap, gap, width, Math.Max(1, closeTop - 2 * gap));
        result.Rects[ResultsUINames.Scroll] = scroll;
        for (int pass = 0; pass < 2; ++pass)
        {
            var view = ViewOf(scroll, bar);
            int contentWidth = Math.Max(1, view.W - 2 * gap);
            int resultCount = Count;
            var ceremony = ResultsLayout.FitCeremony(contentWidth, gap, body, resultCount, measure.Standard(input.Header, 0).Width + 4 * gap);
            result.Edge = ceremony.Metrics.Edge;
            int y = gap;
            int headerHeight = WrappedHeight(ResultsUINames.Header, input.Header, gap + ceremony.Title.TitleLeft, y, ceremony.Title.TitleWidth);
            headerWidth = Math.Max(1, ceremony.Title.TitleWidth);
            int railTop = y + (headerHeight - ceremony.Metrics.Edge) / 2;
            SetAccent(ResultsUINames.RailLeft, gap, railTop, ceremony.Title.LeftWidth, ceremony.Metrics.Edge, ceremony.Title.LeftWidth > 0);
            SetAccent(ResultsUINames.RailRight, gap + ceremony.Title.RightLeft, railTop, ceremony.Title.RightWidth, ceremony.Metrics.Edge,
                ceremony.Title.RightWidth > 0);
            y += headerHeight + gap;
            y += WrappedHeight(ResultsUINames.Context, input.Context, gap, y, contentWidth) + 2 * gap;
            contextWidth = Math.Max(1, contentWidth);
            if (resultCount > 0)
            {
                var heights = new int[3];
                int maximumHeight = 0;
                if (ceremony.Podium.Stacked)
                {
                    for (int p = 0; p < ceremony.Podium.Count; ++p)
                    {
                        var geometry = ceremony.Podium.Slots[p];
                        heights[p] = LayoutResultCard(geometry.SourceIndex, gap + geometry.Left, y, geometry.Width, geometry.Portrait, ceremony.Metrics.Edge);
                        y += heights[p] + gap;
                    }
                }
                else
                {
                    for (int p = 0; p < ceremony.Podium.Count; ++p)
                    {
                        var geometry = ceremony.Podium.Slots[p];
                        heights[p] = LayoutResultCard(geometry.SourceIndex, gap + geometry.Left, y, geometry.Width, geometry.Portrait, ceremony.Metrics.Edge);
                        maximumHeight = Math.Max(maximumHeight, heights[p]);
                    }
                    for (int p = 0; p < ceremony.Podium.Count; ++p)
                    {
                        var geometry = ceremony.Podium.Slots[p];
                        string s = ResultsUINames.Slot(geometry.SourceIndex);
                        var r = result.Rects[s];
                        result.Rects[s] = r with { X = gap + geometry.Left, Y = y + maximumHeight - heights[p] };
                    }
                    y += maximumHeight + gap;
                }
            }
            if (input.EmptyVisible)
            {
                y += WrappedHeight(ResultsUINames.Empty, ResultsUiTexts.Empty, gap, y, contentWidth) + gap;
                emptyWidth = Math.Max(1, contentWidth);
            }
            result.ContentHeight = y;
            int canvasH = Math.Max(y, view.H);
            result.Canvas[ResultsUINames.Scroll] = (Math.Max(1, view.W), canvasH);
            bool barNow = canvasH > Math.Max(0, scroll.H - ArenaUILayout.ScrollInsetH);
            bool changed = barNow != bar;
            bar = barNow;
            if (!changed) break;   // getViewCoord().width == view.width
        }
        result.ScrollBar = bar;
        result.HeaderWidth = headerWidth;
        result.ContextWidth = contextWidth;
        result.EmptyWidth = emptyWidth;
    }
}
