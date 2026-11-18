namespace ProvingGrounds.Core.Glue;

// Port of the debug menu's pixel arithmetic, upstream/ProvingGrounds/plugin/src/DebugMenu.cpp (Proving Grounds by Shutori, GPLv3), task 4.6b:
// PlaceText (:143-149) and Layout (:151-230), plus the window's own rectangle (createWidgetReal(0.12, 0.09, 0.76, 0.82), :546-548). Pure: every
// widget's rectangle for a view size, the font and the texts (measured through IDebugMenuTextMeasure: MyGUI's getTextSize, done by the window
// side with Meitou's text measurer); the window side (src/Ui/DebugMenu.cs) writes them into the layout XML.

/// <summary>The text sizes the debug menu's Layout asks MyGUI for (<c>getTextSize</c>), measured by the window side.</summary>
public interface IDebugMenuTextMeasure
{
    /// <summary>The height of <paramref name="text"/> in the standard font (the client of Kenshi_WordWrapEmpty: no insets) word-wrapped at
    /// <paramref name="width"/> pixels.</summary>
    int StandardHeight(string text, int width);

    /// <summary>The height of <paramref name="text"/> in the painted font (the client of Kenshi_PaintedWordWrapEmpty: no insets; a Kenshi_Button1
    /// caption) word-wrapped at <paramref name="width"/> pixels (0: one line).</summary>
    int PaintedHeight(string text, int width);

    /// <summary>The width of <paramref name="caption"/> on a <c>Kenshi_Button1</c> (<c>Button::getTextSize().width</c>, one line).</summary>
    int ButtonWidth(string caption);
}

/// <summary>What the debug menu's Layout reads: the view (the window's size comes from it), the font and the texts of the visible widgets.</summary>
public sealed record DebugMenuLayoutInput
{
    public int ViewWidth { get; init; } = 1920;
    public int ViewHeight { get; init; } = 1080;
    /// <summary>The standard text's font height (<c>getFontHeight</c>: NativeUI::Spacing / RowHeight).</summary>
    public int FontHeight { get; init; } = NativeUI.DefaultFontHeight;
    public bool Fighters { get; init; } = true;
    public string Heading { get; init; } = DebugMenuTexts.HeadingFighters;
    /// <summary>The eight row buttons: visible and their text (hidden rows are not laid out, C++ :191).</summary>
    public IReadOnlyList<DebugRowView> Rows { get; init; } = DebugMenuLogic<int>.HiddenRows;
    public string SelectedName { get; init; } = DebugMenuTexts.SelectCharacter;
    public string Rating { get; init; } = DebugMenuTexts.RatingNone;
    public string Marks { get; init; } = DebugMenuTexts.MarksNone;
    public string DiagnosticSummary { get; init; } = DebugMenuTexts.DiagnosticOff;
    public IReadOnlyList<string> DiagnosticLabels { get; init; } = DebugMenuLogic<int>.EmptyDiagnosticLabels;
    /// <summary>Whether the scroll view showed its vertical bar after the previous Layout (its view width, :182).</summary>
    public bool ScrollBarBefore { get; init; }
}

/// <summary>The debug menu's pixel arithmetic (task 4.6b), C++ function by function. Pure.</summary>
public sealed class DebugMenuLayout
{
    readonly DebugMenuLayoutInput input;
    readonly IDebugMenuTextMeasure measure;
    readonly PgWindowLayout result = new();
    readonly int gap, body;
    bool bar;

    DebugMenuLayout(DebugMenuLayoutInput input, IDebugMenuTextMeasure measure)
    {
        this.input = input;
        this.measure = measure;
        // C++ :156-157 NativeUI::Spacing(g_prev); Max(1, Max(g_prev->getFontHeight(), g_prev->getTextSize().height)) (the button's caption, painted)
        gap = NativeUI.Spacing(input.FontHeight);
        body = Math.Max(1, Math.Max(input.FontHeight, measure.PaintedHeight(DebugMenuTexts.Prev, 0)));
        bar = input.ScrollBarBefore;
        result.Gap = gap;
        result.Body = body;
    }

    void Set(string widget, int x, int y, int w, int h) => result.Rects[widget] = new LayoutRect(x, y, w, h);

    /// <summary>The window, its client and every widget's rectangle (relative to its parent; the scroll view's children to its canvas).</summary>
    public static PgWindowLayout Compute(DebugMenuLayoutInput input, IDebugMenuTextMeasure measure)
    {
        var l = new DebugMenuLayout(input, measure);
        l.Creation();
        l.Window();
        l.Layout();
        return l.result;
    }

    // C++ :552-620: every widget created at IntCoord(0, 0, 100, 24) (the scroll view 300 x 300, the status 300 x 100); Layout moves the visible ones
    void Creation()
    {
        foreach (var n in new[] { DebugMenuNames.FightersTab, DebugMenuNames.DiagnosticsTab, DebugMenuNames.Heading, DebugMenuNames.Prev, DebugMenuNames.Next,
                     DebugMenuNames.Page, DebugMenuNames.Selected, DebugMenuNames.Rating, DebugMenuNames.Marks, DebugMenuNames.DiagnosticSummary })
            Set(n, 0, 0, 100, 24);
        Set(DebugMenuNames.Scroll, 0, 0, 300, 300);
        Set(DebugMenuNames.Status, 0, 0, 300, 100);
        for (int i = 0; i < DebugMenuLogic<int>.RowsPerPage; ++i) { Set(DebugMenuNames.Row(i), 0, 0, 100, 24); Set(DebugMenuNames.RowText(i), 0, 0, 100, 24); }
        foreach (var a in DebugMenuNames.Actions) { Set(a, 0, 0, 100, 24); Set(DebugMenuNames.LabelOf(a), 0, 0, 100, 24); }
        foreach (var a in DebugMenuNames.Diagnostics) { Set(a, 0, 0, 100, 24); Set(DebugMenuNames.LabelOf(a), 0, 0, 100, 24); }
    }

    // C++ :546-548 createWidgetReal(resource, 0.12f, 0.09f, 0.76f, 0.82f, Align::Center, "Window", ...): MyGUI turns the fractions of the view into
    // pixels with an int cast of each float product (Gui::createWidgetRealT); the client is Kenshi_WindowCX's (ArenaUILayout's margins)
    void Window()
    {
        int x = (int)(0.12f * input.ViewWidth), y = (int)(0.09f * input.ViewHeight);
        int w = (int)(0.76f * input.ViewWidth), h = (int)(0.82f * input.ViewHeight);
        result.Window = new LayoutRect(x, y, w, h);
        result.ClientWidth = Math.Max(0, w - ArenaUILayout.WindowClientMarginW);
        result.ClientHeight = Math.Max(0, h - ArenaUILayout.WindowClientMarginH);
    }

    // C++: DebugMenu.cpp:143-149 PlaceText: the label at (x, y, max(1, width), body), then as tall as its wrapped text (at least body)
    int PlaceText(string label, string text, bool painted, int x, int y, int width, int body)
    {
        int w = Math.Max(1, width);
        int textHeight = painted ? measure.PaintedHeight(text, w) : measure.StandardHeight(text, w);
        int height = Math.Max(body, textHeight);
        Set(label, x, y, w, height);
        return height;
    }

    static (int W, int H) ViewOf(LayoutRect scroll, bool bar) =>
        (Math.Max(0, scroll.W - ArenaUILayout.ScrollInsetW - (bar ? ArenaUILayout.ScrollBarW : 0)), Math.Max(0, scroll.H - ArenaUILayout.ScrollInsetH));

    // C++: DebugMenu.cpp:151-230 Layout
    void Layout()
    {
        int sizeW = result.ClientWidth, sizeH = result.ClientHeight;
        int width = Math.Max(1, sizeW - 2 * gap);
        int pageHeight = NativeUI.RowHeight(measure.PaintedHeight(DebugMenuTexts.Prev, 0), input.FontHeight, 0);
        int tabWidth = Math.Max(1, (width - gap) / 2);
        Set(DebugMenuNames.FightersTab, gap, gap, tabWidth, pageHeight);
        Set(DebugMenuNames.DiagnosticsTab, gap + tabWidth + gap, gap, Math.Max(1, width - tabWidth - gap), pageHeight);
        int headingTop = gap + pageHeight + gap;
        int headingHeight = PlaceText(DebugMenuNames.Heading, input.Heading, false, gap, headingTop, width, body);
        int pageTop = headingTop + headingHeight + gap;
        int pageWidth = Math.Max(measure.ButtonWidth(DebugMenuTexts.Prev), measure.ButtonWidth(DebugMenuTexts.Next)) + 4 * gap;
        bool fighters = input.Fighters;
        if (fighters)
        {
            Set(DebugMenuNames.Prev, gap, pageTop, pageWidth, pageHeight);
            Set(DebugMenuNames.Next, Math.Max(gap, sizeW - gap - pageWidth), pageTop, pageWidth, pageHeight);
            Set(DebugMenuNames.Page, gap + pageWidth + gap, pageTop, Math.Max(1, width - 2 * pageWidth - 2 * gap), pageHeight);
        }
        // Native scrollable status remains outside growing fighter/action content.
        int statusHeight = body * 3 + 2 * gap;
        int statusTop = Math.Max(0, sizeH - gap - statusHeight);
        Set(DebugMenuNames.Status, gap, statusTop, width, Math.Max(0, sizeH - gap - statusTop));
        int scrollTop = pageTop + (fighters ? pageHeight + gap : 0);
        int scrollHeight = Math.Max(0, statusTop - gap - scrollTop);
        Set(DebugMenuNames.Scroll, gap, scrollTop, width, scrollHeight);
        var scroll = result.Rects[DebugMenuNames.Scroll];
        for (int pass = 0; pass < 2; ++pass)
        {
            var view = ViewOf(scroll, bar);
            int contentWidth = Math.Max(1, view.W - 2 * gap);
            bool stacked = contentWidth < body * 36 + 3 * gap;
            int listWidth = stacked ? contentWidth : (contentWidth - gap) / 2;
            int detailX = stacked ? gap : gap + listWidth + gap;
            int detailWidth = stacked ? contentWidth : contentWidth - listWidth - gap;
            int listBottom = gap;
            for (int i = 0; fighters && i < input.Rows.Count; ++i)
            {
                if (!input.Rows[i].Visible) continue;
                int textHeight = PlaceText(DebugMenuNames.RowText(i), input.Rows[i].Text, true, gap, gap, listWidth - 2 * gap, body);
                Set(DebugMenuNames.Row(i), gap, listBottom, listWidth, textHeight + 2 * gap);
                listBottom += textHeight + 3 * gap;
            }
            int y = stacked ? listBottom + gap : gap;
            if (fighters)
            {
                y += PlaceText(DebugMenuNames.Selected, input.SelectedName, false, detailX, y, detailWidth, body) + gap;
                y += PlaceText(DebugMenuNames.Rating, input.Rating, false, detailX, y, detailWidth, body) + gap;
                y += PlaceText(DebugMenuNames.Marks, input.Marks, false, detailX, y, detailWidth, body) + gap;
                for (int i = 0; i < DebugMenuNames.Actions.Count; ++i)
                {
                    string a = DebugMenuNames.Actions[i];
                    int height = PlaceText(DebugMenuNames.LabelOf(a), DebugMenuTexts.ActionCaptions[i], true, gap, gap, detailWidth - 2 * gap, body) + 2 * gap;
                    Set(a, detailX, y, detailWidth, height);
                    y += height + gap;
                }
            }
            else
            {
                y = gap;
                y += PlaceText(DebugMenuNames.DiagnosticSummary, input.DiagnosticSummary, false, gap, y, contentWidth, body) + 2 * gap;
                int buttonWidth = stacked ? contentWidth : (contentWidth - gap) / 2;
                int actionHeight = body * 2 + 2 * gap;
                int visibleIndex = 0;
                for (int i = 0; i < DebugMenuNames.Diagnostics.Count; ++i)   // every diagnostic action is visible on this tab (Refresh :297)
                {
                    int column = stacked ? 0 : visibleIndex % 2;
                    int x = gap + column * (buttonWidth + gap);
                    string a = DebugMenuNames.Diagnostics[i];
                    Set(a, x, y, buttonWidth, actionHeight);
                    PlaceText(DebugMenuNames.LabelOf(a), i < input.DiagnosticLabels.Count ? input.DiagnosticLabels[i] : "", true, gap, gap, buttonWidth - 2 * gap, body);
                    ++visibleIndex;
                    if (stacked || visibleIndex % 2 == 0) y += actionHeight + gap;
                }
                if (!stacked && visibleIndex % 2 != 0) y += actionHeight + gap;
                listBottom = 0;
            }
            int canvasH = Math.Max(view.H, Math.Max(y, listBottom) + gap);
            result.Canvas[DebugMenuNames.Scroll] = (Math.Max(1, view.W), canvasH);
            result.ContentHeight = Math.Max(y, listBottom) + gap;
            bool barNow = canvasH > view.H;
            bool changed = barNow != bar;
            bar = barNow;
            if (!changed) break;   // view.width == g_scroll->getViewCoord().width
        }
        result.ScrollBar = bar;
    }
}
