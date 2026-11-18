namespace ProvingGrounds.Core.Glue;

// Port of the recruitment window's pixel arithmetic, upstream/ProvingGrounds/plugin/src/PrisonerRecruitmentUI.cpp (Proving Grounds by Shutori,
// GPLv3), task 6.4b: BodyHeight (:93-96), WrappedHeight (:98-107), LayoutRows (:299-337), LayoutDetails (:339-365), FitWindowToScreen (:367-380) and
// LayoutWindow (:382-444), with Core/Glue/NativeUI.cs. Pure; the window side (src/Ui/PrisonerRecruitmentUI.cs) writes the rectangles into the layout
// XML and binds them. The widget names are EnsureWindow's (:486-559) and RebuildRows' (:174-210).
//
// MyGUI reads and what replaces them here (as the Rewards window, Core/Glue/RewardsUILayout.cs):
//   g_metrics->getFontHeight() (BodyHeight) ....................... RecruitmentLayoutInput.FontHeight (the standard text's measured font height)
//   getTextSize (edit boxes, the close button) .................... IRewardsTextMeasure (standard text, Kenshi_Button1 captions, the painted text of
//                                                                    the rows' names)
//   NativeUI::Spacing / RowHeight ................................. Core/Glue/NativeUI.cs (RowHeight with the caption's standard text height, as
//                                                                    every window since 4.5b)
//   getHeight() - getTextRegion().height of a Kenshi_WordWrap ...... PgTemplates.WordWrapInset* (30 px; at most the edit box's own height);
//                                                                    a Kenshi_WordWrapEmpty / Kenshi_PaintedWordWrapEmpty has none
//   ScrollView::getViewCoord / setCanvasSize ....................... the Kenshi_ScrollView insets (ArenaUILayout.ScrollInset*, ScrollBarW); the
//                                                                    vertical bar shows while the canvas is taller than the view
//   g_window->getClientWidget()->getSize() ......................... the window rectangle less the Kenshi_WindowCX chrome (ArenaUILayout.WindowClientMargin*)

/// <summary>What the recruitment window's arithmetic reads besides the view (the published model).</summary>
public sealed record RecruitmentLayoutInput
{
    public int ViewWidth { get; init; } = 1920;
    public int ViewHeight { get; init; } = 1080;
    /// <summary>The standard text's font height (<c>g_metrics->getFontHeight()</c>, BodyHeight :95; NativeUI::Spacing).</summary>
    public int FontHeight { get; init; } = NativeUI.DefaultFontHeight;
    /// <summary>The status line's text at the last <c>LayoutWindow</c> (it sizes the status line and so the two scroll views). The C++ calls
    /// LayoutWindow only at Show, when the candidates change, or when the client size or the body height changes (RefreshAll :571-579, Show
    /// :602-603); otherwise a new status text keeps the old rectangles. Null = the view's own <see cref="PrisonerRecruitmentUiView{TId}.Status"/>.</summary>
    public string? WindowStatus { get; init; }
    /// <summary>Whether each scroll view showed its vertical bar after the previous layout pass (MyGUI keeps it until the next setCanvasSize; it
    /// decides the first pass's view width).</summary>
    public bool RosterBarBefore { get; init; }
    public bool DetailBarBefore { get; init; }
}

/// <summary>One scroll view after a layout: its canvas height, its view height (the offset is clamped to content - view) and its bar.</summary>
public readonly record struct RecruitmentCanvasInfo(int Content, int View, bool Bar)
{
    /// <summary>C++ LayoutRows :333-336 / LayoutDetails :361-364: an offset (positive, from the top) clamped to [0, content - view].</summary>
    public int Clamp(int offset) => Math.Min(Math.Max(0, Content - View), Math.Max(0, offset));
}

/// <summary>The recruitment window's layout: every widget's rectangle (relative to its parent, as MyGUI's setCoord), the hidden widgets, the two
/// canvases, the window rectangle.</summary>
public sealed class RecruitmentWindowLayout
{
    public Dictionary<string, LayoutRect> Rects { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, (int W, int H)> Canvas { get; } = new(StringComparer.Ordinal);
    public HashSet<string> Hidden { get; } = new(StringComparer.Ordinal);
    public LayoutRect Window { get; set; }
    public int ClientWidth { get; set; }
    public int ClientHeight { get; set; }
    public int Gap { get; set; }
    public int Body { get; set; }
    public bool Stacked { get; set; }
    public RecruitmentCanvasInfo Roster { get; set; }
    public RecruitmentCanvasInfo Detail { get; set; }
}

/// <summary>The recruitment window's widget names: the C++'s own (EnsureWindow :496-543, RebuildRows :184-206).</summary>
public static class PrisonerRecruitmentUINames
{
    public const string Window = "ProvingGroundsRecruitmentWindow";                                  // :498
    public const string Metrics = "PG_RecruitmentMetrics", Subtitle = "PG_RecruitmentSubtitle";   // :508, :512
    public const string Status = "PG_RecruitmentStatus", RosterHeading = "PG_RecruitmentRosterHeading";   // :516, :518
    public const string RosterScroll = "PG_RecruitmentRoster", DetailScroll = "PG_RecruitmentDetails";   // :520, :522
    public const string Portrait = "PG_RecruitmentSelectedPortrait", Name = "PG_RecruitmentSelectedName";   // :526, :530
    public const string Stats = "PG_RecruitmentSelectedStats", Explanation = "PG_RecruitmentExplanation";   // :532, :534
    public const string Close = "PG_RecruitmentClose", Action = "PG_RecruitmentAction";   // :537, :541

    static string I(int v) => v.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>C++ RebuildRows :184-206, <c>%d</c> = the row's place in the candidates: the row button, its portrait, name and Marks.</summary>
    public static string Row(int index) => "PG_RecruitmentRow_" + I(index);
    public static string RowPortrait(int index) => "PG_RecruitmentPortrait_" + I(index);
    public static string RowName(int index) => "PG_RecruitmentName_" + I(index);
    public static string RowMarks(int index) => "PG_RecruitmentMarks_" + I(index);

    /// <summary>A row button's index (<c>PG_RecruitmentRow_%d</c>).</summary>
    public static bool TryRow(string widget, out int index)
    {
        index = -1;
        const string prefix = "PG_RecruitmentRow_";
        if (!widget.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var digits = widget.AsSpan(prefix.Length);
        if (digits.Length == 0 || digits.Length > 6) return false;
        foreach (char ch in digits) if (!char.IsAsciiDigit(ch)) return false;
        index = int.Parse(digits, System.Globalization.CultureInfo.InvariantCulture);
        return true;
    }

    /// <summary>The MyGUI event PrisonerRecruitmentUI.cpp binds each button to (task 4.4b): the rows (:188), Back to Arena (:538) and the action
    /// button (:542) to eventMouseButtonClick; nothing is bound to a press.</summary>
    public static UiButtonEvent ButtonEvent(string widget) => UiButtonEvent.Click;
}

/// <summary>The recruitment window's pixel arithmetic (task 6.4b), C++ function by function. Pure.</summary>
public sealed class PrisonerRecruitmentUILayout<TId>
{
    /// <summary>One MyGUI ScrollView (Kenshi_ScrollView, NativeUI::Scroll): its rectangle, its canvas and whether its vertical bar shows (MyGUI
    /// re-decides the bar at each setCanvasSize and each resize).</summary>
    sealed class Scroll(string name, bool bar)
    {
        public readonly string Name = name;
        public LayoutRect Rect = new(0, 0, 100, 24);
        public bool Bar = bar;
        public int CanvasH = -1;
        public int ViewW => Math.Max(0, Rect.W - ArenaUILayout.ScrollInsetW - (Bar ? ArenaUILayout.ScrollBarW : 0));
        public int ViewH => Math.Max(0, Rect.H - ArenaUILayout.ScrollInsetH);
        public void SetRect(LayoutRect r)
        {
            Rect = r;
            if (CanvasH >= 0) Bar = CanvasH > ViewH;
        }
        public void SetCanvas(int h)
        {
            CanvasH = h;
            Bar = h > ViewH;
        }
    }

    readonly PrisonerRecruitmentUiView<TId> v;
    readonly RecruitmentLayoutInput input;
    readonly IRewardsTextMeasure measure;
    readonly RecruitmentWindowLayout result = new();
    readonly int gap, body;
    readonly Scroll roster, detail;
    int rosterContent, detailContent;

    PrisonerRecruitmentUILayout(PrisonerRecruitmentUiView<TId> view, RecruitmentLayoutInput input, IRewardsTextMeasure measure)
    {
        v = view;
        this.input = input;
        this.measure = measure;
        gap = NativeUI.Spacing(input.FontHeight);   // NativeUI::Spacing(g_metrics)
        body = Math.Max(1, input.FontHeight);        // C++ :93-96 BodyHeight: g_metrics's font height
        result.Gap = gap;
        result.Body = body;
        roster = new Scroll(PrisonerRecruitmentUINames.RosterScroll, input.RosterBarBefore);
        detail = new Scroll(PrisonerRecruitmentUINames.DetailScroll, input.DetailBarBefore);
    }

    /// <summary>The window and every widget's rectangle for the view: FitWindowToScreen then LayoutWindow (Show :602-603; RefreshAll :571-574).</summary>
    public static RecruitmentWindowLayout Compute(PrisonerRecruitmentUiView<TId> view, RecruitmentLayoutInput input, IRewardsTextMeasure measure)
    {
        var l = new PrisonerRecruitmentUILayout<TId>(view, input, measure);
        l.Creation();
        l.FitWindowToScreen();
        l.LayoutWindow();
        l.result.Roster = new RecruitmentCanvasInfo(l.rosterContent, l.roster.ViewH, l.roster.Bar);
        l.result.Detail = new RecruitmentCanvasInfo(l.detailContent, l.detail.ViewH, l.detail.Bar);
        return l.result;
    }

    void Set(string widget, int x, int y, int w, int h) => result.Rects[widget] = new LayoutRect(x, y, w, h);

    void SetScroll(Scroll s, int x, int y, int w, int h)
    {
        s.SetRect(new LayoutRect(x, y, w, h));
        result.Rects[s.Name] = s.Rect;
    }

    void SetCanvas(Scroll s, int w, int h)
    {
        s.SetCanvas(h);
        result.Canvas[s.Name] = (w, h);
    }

    // C++: EnsureWindow :486-559 + RebuildRows :174-210: every widget at (0, 0, 100, 24) (`initial`); the metrics label is made hidden (:509)
    void Creation()
    {
        foreach (var n in new[] { PrisonerRecruitmentUINames.Metrics, PrisonerRecruitmentUINames.Subtitle, PrisonerRecruitmentUINames.Status,
                     PrisonerRecruitmentUINames.RosterHeading, PrisonerRecruitmentUINames.Portrait, PrisonerRecruitmentUINames.Name,
                     PrisonerRecruitmentUINames.Stats, PrisonerRecruitmentUINames.Explanation, PrisonerRecruitmentUINames.Close,
                     PrisonerRecruitmentUINames.Action })
            Set(n, 0, 0, 100, 24);
        foreach (var s in new[] { roster, detail }) result.Rects[s.Name] = s.Rect;
        result.Hidden.Add(PrisonerRecruitmentUINames.Metrics);
        foreach (var r in v.Rows)
            foreach (var n in new[] { PrisonerRecruitmentUINames.Row(r.Index), PrisonerRecruitmentUINames.RowPortrait(r.Index),
                         PrisonerRecruitmentUINames.RowName(r.Index), PrisonerRecruitmentUINames.RowMarks(r.Index) })
                Set(n, 0, 0, 100, 24);
    }

    // C++: PrisonerRecruitmentUI.cpp:367-380 FitWindowToScreen (88 % of the view, centred)
    void FitWindowToScreen()
    {
        int w = input.ViewWidth * 88 / 100, h = input.ViewHeight * 88 / 100;
        result.Window = new LayoutRect((input.ViewWidth - w) / 2, (input.ViewHeight - h) / 2, w, h);
        result.ClientWidth = Math.Max(0, w - ArenaUILayout.WindowClientMarginW);
        result.ClientHeight = Math.Max(0, h - ArenaUILayout.WindowClientMarginH);
    }

    // C++: PrisonerRecruitmentUI.cpp:98-107 WrappedHeight of a Kenshi_WordWrapEmpty (no insets) in the standard font
    int WrappedHeight(string label, string text, int x, int y, int width)
    {
        int w = Math.Max(1, width);
        int height = Math.Max(body, measure.Standard(text, w).Height);
        Set(label, x, y, w, height);
        return height;
    }

    // C++: NativeUI::RowHeight(sample, 0) for a one-line caption
    int RowHeight(string caption) => NativeUI.RowHeight(measure.Standard(caption, 0).Height, input.FontHeight, 0);

    // A Kenshi_WordWrap edit box laid at (gap, gap, width, body * 2 + 2 gaps) to measure (:400-402, :409-411): its insets (the edit box less its text
    // region, never more than the box) and the height of its text word-wrapped in the text region (the width less the insets)
    (int Insets, int TextHeight) WordWrapMeasure(string text, int width)
    {
        int probe = body * 2 + 2 * gap;
        int insets = Math.Min(PgTemplates.WordWrapInsetH, probe);
        int textHeight = measure.Standard(text, Math.Max(1, width - PgTemplates.WordWrapInsetW)).Height;
        return (insets, textHeight);
    }

    // C++: PrisonerRecruitmentUI.cpp:382-444 LayoutWindow
    void LayoutWindow()
    {
        int sizeW = result.ClientWidth, sizeH = result.ClientHeight;
        int width = Math.Max(1, sizeW - 2 * gap);
        int buttonHeight = Math.Max(
            RowHeight(v.Metrics),         // NativeUI::RowHeight(g_metrics, 0)
            RowHeight(v.ActionCaption));  // NativeUI::RowHeight(g_recruit, 0)
        int closeWidth = Math.Min(width / 3,
            measure.ButtonWidth(v.CloseCaption) + 4 * gap);
        int buttonTop = Math.Max(gap, sizeH - gap - buttonHeight);
        Set(PrisonerRecruitmentUINames.Close, gap, buttonTop, Math.Max(1, closeWidth), buttonHeight);
        Set(PrisonerRecruitmentUINames.Action, gap + closeWidth + gap, buttonTop,
            Math.Max(1, width - closeWidth - gap), buttonHeight);

        var (statusInsets, statusText) = WordWrapMeasure(input.WindowStatus ?? v.Status, width);
        int statusHeight = Math.Min(body * 2 + statusInsets,
            Math.Max(body, statusText + statusInsets));
        int statusTop = Math.Max(gap,
            buttonTop - gap - statusHeight);
        Set(PrisonerRecruitmentUINames.Status, gap, statusTop, width, statusHeight);

        var (subtitleInsets, subtitleText) = WordWrapMeasure(v.Subtitle, width);
        int subtitleHeight = Math.Max(body,
            subtitleText + subtitleInsets);
        Set(PrisonerRecruitmentUINames.Subtitle, gap, gap, width, subtitleHeight);
        int contentTop = gap + subtitleHeight + gap;
        int contentHeight = Math.Max(1, statusTop - gap - contentTop);
        bool stacked = width < body * 55;
        result.Stacked = stacked;

        if (stacked)
        {
            Set(PrisonerRecruitmentUINames.RosterHeading, gap, contentTop, width, body);
            int splitHeight = Math.Max(1,
                contentHeight - body - 2 * gap);
            int rosterHeight = Math.Max(1, splitHeight * 2 / 5);
            SetScroll(roster, gap, contentTop + body + gap,
                width, rosterHeight);
            int detailTop = contentTop + body + 2 * gap + rosterHeight;
            SetScroll(detail, gap, detailTop, width,
                Math.Max(1, contentTop + contentHeight - detailTop));
        }
        else
        {
            int rosterWidth = Math.Max(body * 15, width * 32 / 100);
            Set(PrisonerRecruitmentUINames.RosterHeading, gap, contentTop, rosterWidth, body);
            SetScroll(roster, gap, contentTop + body + gap,
                rosterWidth, Math.Max(1, contentHeight - body - gap));
            SetScroll(detail, gap + rosterWidth + gap, contentTop,
                Math.Max(1, width - rosterWidth - gap), contentHeight);
        }
        LayoutRows();
        LayoutDetails();
    }

    // C++: PrisonerRecruitmentUI.cpp:299-337 LayoutRows (a row's parts relative to the row button; rows relative to the roster canvas)
    void LayoutRows()
    {
        int image = Math.Max(48, body * 3);
        for (int pass = 0; pass < 2; ++pass)
        {
            int viewW = roster.ViewW, viewH = roster.ViewH;
            int width = Math.Max(1, viewW);
            int y = 0;
            foreach (var row in v.Rows)
            {
                int i = row.Index;
                int textX = image + 2 * gap;
                int textWidth = Math.Max(1, width - textX - gap);
                // name: setCoord(textX, gap, textWidth, body), then its height from its word-wrapped painted text (a Kenshi_PaintedWordWrapEmpty)
                int nameHeight = Math.Max(body,
                    measure.Painted(row.Name, textWidth).Height);
                Set(PrisonerRecruitmentUINames.RowName(i), textX, gap, textWidth, nameHeight);
                Set(PrisonerRecruitmentUINames.RowMarks(i), textX, gap + nameHeight + gap,
                    textWidth, body);
                int height = Math.Max(image,
                    nameHeight + gap + body) + 2 * gap;
                Set(PrisonerRecruitmentUINames.Row(i), 0, y, width, height);
                Set(PrisonerRecruitmentUINames.RowPortrait(i), gap, gap, image, image);
                y += height + gap;
            }
            rosterContent = y;
            SetCanvas(roster, Math.Max(1, viewW),
                Math.Max(y, viewH));
            if (roster.ViewW == viewW)
                break;
        }
    }

    // C++: PrisonerRecruitmentUI.cpp:339-365 LayoutDetails (relative to the detail canvas)
    void LayoutDetails()
    {
        for (int pass = 0; pass < 2; ++pass)
        {
            int viewW = detail.ViewW, viewH = detail.ViewH;
            int width = Math.Max(1, viewW - 2 * gap);
            int portrait = Math.Min(Math.Max(80, body * 7), width);
            int y = gap;
            Set(PrisonerRecruitmentUINames.Portrait, gap, y, portrait, portrait);
            y += portrait + gap;
            y += WrappedHeight(PrisonerRecruitmentUINames.Name, v.SelectedName, gap, y, width) + gap;
            y += WrappedHeight(PrisonerRecruitmentUINames.Stats, v.Stats, gap, y, width) + gap;
            y += WrappedHeight(PrisonerRecruitmentUINames.Explanation, v.Explanation, gap, y, width) + gap;
            detailContent = y;
            SetCanvas(detail, Math.Max(1, viewW),
                Math.Max(y, viewH));
            if (detail.ViewW == viewW)
                break;
        }
    }
}
