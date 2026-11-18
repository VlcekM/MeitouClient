namespace ProvingGrounds.Core.Glue;

// Port of the Leaderboard window's pixel arithmetic, upstream/ProvingGrounds/plugin/src/LeaderboardUI.cpp (Proving Grounds by Shutori, GPLv3),
// task 4.6a: WrappedHeight (:164-171), LayoutPortrait (:215-242), LayoutFeatured (:244-289), LayoutRows (:291-457), FitWindowToScreen
// (:459-469) and LayoutWindow (:471-512), with Core LeaderboardLayout. Pure; the window side (src/Ui/LeaderboardUI.cs) writes the rectangles into
// the layout XML.

/// <summary>One row as the arithmetic needs it: its texts (RebuildRows :593-634).</summary>
public readonly record struct LeaderboardLayoutRow(string Rank, string Name, string Rating, string Record, string Matches, string Details);

/// <summary>What the Leaderboard window's arithmetic reads.</summary>
public sealed record LeaderboardLayoutInput
{
    public int ViewWidth { get; init; } = 1920;
    public int ViewHeight { get; init; } = 1080;
    public int FontHeight { get; init; } = NativeUI.DefaultFontHeight;
    public string Subtitle { get; init; } = LeaderboardUiTexts.Subtitle;
    public string Empty { get; init; } = LeaderboardUiTexts.Empty;
    public bool ResetVisible { get; init; }
    public string ResetCaption { get; init; } = LeaderboardUiTexts.Reset;
    public IReadOnlyList<LeaderboardLayoutRow> Rows { get; init; } = [];
}

/// <summary>The Leaderboard window's widget names: the C++'s own (EnsureWindow :522-567, RebuildRows :597-642).</summary>
public static class LeaderboardUINames
{
    public const string Window = "ProvingGroundsLeaderboardWindow", Subtitle = "PG_LeaderboardSubtitle", Empty = "PG_LeaderboardEmpty";
    public const string Scroll = "PG_LeaderboardScroll", Champions = "PG_LeaderboardChampions";
    public const string RailLeft = "PG_LeaderboardChampionRailLeft", RailRight = "PG_LeaderboardChampionRailRight";
    public const string Underline = "PG_LeaderboardHeaderUnderline", Close = "PG_LeaderboardClose", Reset = "PG_LeaderboardReset";

    /// <summary>The MyGUI event LeaderboardUI.cpp binds each button to (task 4.4b): Close and Reset to eventMouseButtonClick (:565, :567); no
    /// other button is bound.</summary>
    public static UiButtonEvent ButtonEvent(string widget) => UiButtonEvent.Click;

    /// <summary>C++ :557 <c>PG_LeaderboardHeader_&lt;heading&gt;</c>.</summary>
    public static string Header(int column) => "PG_LeaderboardHeader_" + LeaderboardUiTexts.Headings[column];
    /// <summary>C++ :597 <c>PG_Lb_%u</c> and its parts (:610-642).</summary>
    public static string Row(int i) => "PG_Lb_" + ((uint)i).ToString(System.Globalization.CultureInfo.InvariantCulture);
    public static string Portrait(string row) => row + "_Portrait";
    public static string Placeholder(string row) => row + "_PortraitPlaceholder";
    public static string PortraitBorder(string row, int edge) => row + "_PortraitBorder" + (char)('0' + edge);
    public static string CardBorder(string row, int edge) => row + "_CardBorder" + (char)('0' + edge);
    public static string Separator(string row) => row + "_Separator";
    public static string Details(string row) => row + "_Details";
    /// <summary>C++ :624-628 the cells: <c>_Rank</c>, <c>_Name</c>, <c>_Rating</c>, <c>_Record</c>, <c>_Matches</c>.</summary>
    public static string Cell(string row, int column) => row + column switch { 0 => "_Rank", 1 => "_Name", 2 => "_Rating", 3 => "_Record", _ => "_Matches" };
}

/// <summary>The Leaderboard window's pixel arithmetic (task 4.6a), C++ function by function. Pure.</summary>
public sealed class LeaderboardUILayout
{
    const int Rank = (int)LeaderboardLayout.Column.Rank, Fighter = (int)LeaderboardLayout.Column.Fighter;
    const int ColumnCount = (int)LeaderboardLayout.Column.ColumnCount;

    readonly LeaderboardLayoutInput input;
    readonly IPgTextMeasure measure;
    readonly PgWindowLayout result = new();
    readonly int gap, body;
    bool bar;

    LeaderboardUILayout(LeaderboardLayoutInput input, IPgTextMeasure measure)
    {
        this.input = input;
        this.measure = measure;
        gap = NativeUI.Spacing(input.FontHeight);
        // C++ :63-66 BodyHeight: g_headers[0] ("Rank")'s font height or text height
        body = Math.Max(1, Math.Max(input.FontHeight, measure.Standard(LeaderboardUiTexts.Headings[0], 0).Height));
        result.Gap = gap;
        result.Body = body;
    }

    /// <summary>The window, its client and every widget's rectangle (RefreshContent: FitWindowToScreen, LayoutWindow :687-688).</summary>
    public static PgWindowLayout Compute(LeaderboardLayoutInput input, IPgTextMeasure measure)
    {
        var l = new LeaderboardUILayout(input, measure);
        l.Creation();
        l.FitWindowToScreen();
        l.LayoutWindow();
        return l.result;
    }

    void Set(string widget, int x, int y, int w, int h) => result.Rects[widget] = new LayoutRect(x, y, w, h);

    void Show(string widget, bool visible)
    {
        if (visible) result.Hidden.Remove(widget); else result.Hidden.Add(widget);
    }

    void SetAccent(string accent, int x, int y, int w, int h, bool visible)
    {
        Set(accent, x, y, w, h);
        Show(accent, visible);
    }

    // C++: LeaderboardUI.cpp:514-583 EnsureWindow + RebuildRows :606-642 (every widget at 0 0 100 24, the subtitle 0 0 100 100, the scroll view
    // 0 0 300 200; the reset button hidden unless offered, :568, :678)
    void Creation()
    {
        Set(LeaderboardUINames.Subtitle, 0, 0, 100, 100);
        Set(LeaderboardUINames.Scroll, 0, 0, 300, 200);
        foreach (var n in new[] { LeaderboardUINames.Empty, LeaderboardUINames.Champions, LeaderboardUINames.RailLeft, LeaderboardUINames.RailRight,
                     LeaderboardUINames.Underline, LeaderboardUINames.Close, LeaderboardUINames.Reset })
            Set(n, 0, 0, 100, 24);
        for (int c = 0; c < ColumnCount; ++c) Set(LeaderboardUINames.Header(c), 0, 0, 100, 24);
        Show(LeaderboardUINames.Reset, input.ResetVisible);
        for (int i = 0; i < input.Rows.Count; ++i)
        {
            string row = LeaderboardUINames.Row(i);
            foreach (var n in new[] { row, LeaderboardUINames.Portrait(row), LeaderboardUINames.Placeholder(row), LeaderboardUINames.Details(row),
                         LeaderboardUINames.Separator(row) })
                Set(n, 0, 0, 100, 24);
            for (int c = 0; c < ColumnCount; ++c) Set(LeaderboardUINames.Cell(row, c), 0, 0, 100, 24);
            for (int e = 0; e < 4; ++e) { Set(LeaderboardUINames.PortraitBorder(row, e), 0, 0, 100, 24); Set(LeaderboardUINames.CardBorder(row, e), 0, 0, 100, 24); }
        }
    }

    // C++: LeaderboardUI.cpp:459-469 FitWindowToScreen
    void FitWindowToScreen()
    {
        int w = input.ViewWidth * 88 / 100, h = input.ViewHeight * 82 / 100;
        result.Window = new LayoutRect((input.ViewWidth - w) / 2, (input.ViewHeight - h) / 2, w, h);
        result.ClientWidth = Math.Max(0, w - ArenaUILayout.WindowClientMarginW);
        result.ClientHeight = Math.Max(0, h - ArenaUILayout.WindowClientMarginH);
    }

    // C++: LeaderboardUI.cpp:164-171 WrappedHeight (no insets: Kenshi_WordWrapEmpty)
    int WrappedHeight(string label, string text, int x, int y, int width)
    {
        int w = Math.Max(1, width);
        int height = Math.Max(body, measure.Standard(text, w).Height);
        Set(label, x, y, w, height);
        return height;
    }

    int CloseHeight => NativeUI.RowHeight(measure.Standard(LeaderboardUiTexts.Close, 0).Height, input.FontHeight, 0);

    // C++: LeaderboardUI.cpp:471-512 LayoutWindow
    void LayoutWindow()
    {
        int sizeW = result.ClientWidth, sizeH = result.ClientHeight;
        int width = Math.Max(1, sizeW - 2 * gap);
        // A long save key scrolls inside a native text panel; include the skin insets (Kenshi_WordWrap: 30 px). The C++ first sets the subtitle to
        // max(100, 4 body) high to measure it (:480); its text region is then that height less the insets, its wrap width the width less them.
        int contextHeight = measure.Standard(input.Subtitle, Math.Max(1, width - PgTemplates.WordWrapInsetW)).Height + PgTemplates.WordWrapInsetH;
        var vertical = LeaderboardLayout.FitVertical(sizeH, gap, body, contextHeight, CloseHeight);
        Set(LeaderboardUINames.Subtitle, gap, gap, width, vertical.ContextHeight);
        Show(LeaderboardUINames.Subtitle, vertical.ContextHeight > 0);
        int scrollTop = vertical.HeadingTop;
        int scrollHeight = Math.Max(0, vertical.CloseTop - gap - scrollTop);
        Set(LeaderboardUINames.Scroll, gap, scrollTop, width, scrollHeight);
        Show(LeaderboardUINames.Scroll, input.Rows.Count > 0 && scrollHeight > 0);
        int closeWidth = Math.Max(measure.ButtonWidth(LeaderboardUiTexts.Close) + 4 * gap, body * 6);
        if (input.ResetVisible)
        {
            int resetWidth = Math.Max(measure.ButtonWidth(input.ResetCaption) + 4 * gap, body * 11);
            int buttonWidth = Math.Min(width / 2 - gap, Math.Max(closeWidth, resetWidth));
            int resetX = (sizeW - 2 * buttonWidth - gap) / 2;
            Set(LeaderboardUINames.Reset, resetX, vertical.CloseTop, buttonWidth, vertical.CloseHeight);
            Set(LeaderboardUINames.Close, resetX + buttonWidth + gap, vertical.CloseTop, buttonWidth, vertical.CloseHeight);
        }
        else Set(LeaderboardUINames.Close, Math.Max(gap, (sizeW - closeWidth) / 2), vertical.CloseTop, closeWidth < width ? closeWidth : width,
            vertical.CloseHeight);
        int emptyHeight = WrappedHeight(LeaderboardUINames.Empty, input.Empty, 2 * gap, scrollTop, Math.Max(1, width - 2 * gap));
        var er = result.Rects[LeaderboardUINames.Empty];
        result.Rects[LeaderboardUINames.Empty] = er with { W = Math.Max(1, width - 2 * gap), H = emptyHeight < scrollHeight ? emptyHeight : scrollHeight };
        Show(LeaderboardUINames.Empty, input.Rows.Count == 0 && scrollHeight > 0);
        LayoutRows();
    }

    static (int W, int H) ViewOf(LayoutRect scroll, bool bar) =>
        (Math.Max(0, scroll.W - ArenaUILayout.ScrollInsetW - (bar ? ArenaUILayout.ScrollBarW : 0)), Math.Max(0, scroll.H - ArenaUILayout.ScrollInsetH));

    // C++: LeaderboardUI.cpp:215-242 LayoutPortrait (the portrait / placeholder split is the window side's: it knows the portrait; here both are
    // placed and hidden together when the portrait is not shown)
    void LayoutPortrait(string row, int x, int y, int size, bool show, int edge)
    {
        Set(LeaderboardUINames.Portrait(row), x, y, size, size);
        Set(LeaderboardUINames.Placeholder(row), x, y, size, size);
        Show(LeaderboardUINames.Portrait(row), show);
        Show(LeaderboardUINames.Placeholder(row), show);
        SetAccent(LeaderboardUINames.PortraitBorder(row, 0), x, y, edge, size, show);
        SetAccent(LeaderboardUINames.PortraitBorder(row, 1), x, y, size, edge, show);
        SetAccent(LeaderboardUINames.PortraitBorder(row, 2), x, y + size - edge, size, edge, show);
        SetAccent(LeaderboardUINames.PortraitBorder(row, 3), x + size - edge, y, edge, size, show);
    }

    // C++: LeaderboardUI.cpp:244-289 LayoutFeatured
    int LayoutFeatured(int index, int rank, int left, int top, int width, int portraitSize, int edge)
    {
        string row = LeaderboardUINames.Row(index);
        var texts = input.Rows[index];
        Show(row, true);
        for (int c = 0; c < ColumnCount; ++c)
            Show(LeaderboardUINames.Cell(row, c), c == Rank || c == Fighter);
        Show(LeaderboardUINames.Details(row), true);
        Set(LeaderboardUINames.Cell(row, Rank), gap, gap, Math.Max(1, width - 2 * gap), body);
        int portraitTop = body + 2 * gap;
        LayoutPortrait(row, Math.Max(0, (width - portraitSize) / 2), portraitTop, portraitSize, true, edge);
        int nameTop = portraitTop + portraitSize + gap;
        int nameHeight = WrappedHeight(LeaderboardUINames.Cell(row, Fighter), texts.Name, gap, nameTop, Math.Max(1, width - 2 * gap));
        int detailsTop = nameTop + nameHeight + gap;
        int detailsHeight = WrappedHeight(LeaderboardUINames.Details(row), texts.Details, gap, detailsTop, Math.Max(1, width - 2 * gap));
        int height = detailsTop + detailsHeight + 2 * gap;
        Set(row, left, top, width, height);
        result.FeaturedRank[row] = rank;
        SetAccent(LeaderboardUINames.CardBorder(row, 0), 0, 0, edge, height, true);
        SetAccent(LeaderboardUINames.CardBorder(row, 1), 0, 0, width, edge, true);
        SetAccent(LeaderboardUINames.CardBorder(row, 2), 0, height - edge, width, edge, true);
        SetAccent(LeaderboardUINames.CardBorder(row, 3), width - edge, 0, edge, height, true);
        Show(LeaderboardUINames.Separator(row), false);
        return height;
    }

    string Cell(int row, int column)
    {
        var t = input.Rows[row];
        return column switch { 0 => t.Rank, 1 => t.Name, 2 => t.Rating, 3 => t.Record, _ => t.Matches };
    }

    // C++: LeaderboardUI.cpp:291-457 LayoutRows
    void LayoutRows()
    {
        var ceremony = LeaderboardLayout.FitCeremonialMetrics(body, gap);
        int rowPortrait = ceremony.RowPortrait;
        int rowGap = ceremony.RowGap;
        result.Edge = ceremony.Edge;
        var scroll = result.Rects[LeaderboardUINames.Scroll];
        int count = input.Rows.Count;
        Span<int> minimum = stackalloc int[ColumnCount];
        // Scrollbar appearance can change the viewport width; use its actual geometry, including a second pass after the canvas height is set.
        for (int pass = 0; pass < 2; ++pass)
        {
            var view = ViewOf(scroll, bar);
            int width = Math.Max(1, view.W - 2 * gap);
            int featuredCount = Math.Min(3, count);
            int y = gap;
            Show(LeaderboardUINames.Champions, featuredCount > 0);
            if (featuredCount > 0)
            {
                var rails = LeaderboardLayout.FitTitleRails(width, gap, measure.Standard(LeaderboardUiTexts.Champions, 0).Width + 4 * gap);
                Set(LeaderboardUINames.Champions, gap + rails.TitleLeft, y, rails.TitleWidth, body);
                int railTop = y + (body - ceremony.Edge) / 2;
                SetAccent(LeaderboardUINames.RailLeft, gap, railTop, rails.LeftWidth, ceremony.Edge, rails.LeftWidth > 0);
                SetAccent(LeaderboardUINames.RailRight, gap + rails.RightLeft, railTop, rails.RightWidth, ceremony.Edge, rails.RightWidth > 0);
                y += body + gap;
                var podium = LeaderboardLayout.FitPodium(width, gap, featuredCount, ceremony.ChampionPortrait, ceremony.OtherPortrait);
                var heights = new int[3];
                int maximumHeight = 0;
                if (podium.Stacked)
                {
                    for (int p = 0; p < podium.Count; ++p)
                    {
                        var slot = podium.Slots[p];
                        heights[p] = LayoutFeatured(slot.SourceIndex, slot.Rank, gap + slot.Left, y, slot.Width, slot.Portrait, ceremony.Edge);
                        y += heights[p] + gap;
                    }
                }
                else
                {
                    for (int p = 0; p < podium.Count; ++p)
                    {
                        var slot = podium.Slots[p];
                        heights[p] = LayoutFeatured(slot.SourceIndex, slot.Rank, gap + slot.Left, y, slot.Width, slot.Portrait, ceremony.Edge);
                        maximumHeight = Math.Max(maximumHeight, heights[p]);
                    }
                    for (int p = 0; p < podium.Count; ++p)
                    {
                        var slot = podium.Slots[p];
                        string name = LeaderboardUINames.Row(slot.SourceIndex);
                        result.Rects[name] = result.Rects[name] with { X = gap + slot.Left, Y = y + maximumHeight - heights[p] };
                    }
                    y += maximumHeight + gap;
                }
            }
            else
            {
                Show(LeaderboardUINames.RailLeft, false);
                Show(LeaderboardUINames.RailRight, false);
            }

            for (int c = 0; c < ColumnCount; ++c)
            {
                minimum[c] = measure.Standard(LeaderboardUiTexts.Headings[c], 0).Width + gap;
                if (c == Fighter) continue;
                for (int r = 3; r < count; ++r)
                    minimum[c] = Math.Max(minimum[c], measure.Standard(Cell(r, c), 0).Width + gap);
            }
            minimum[Fighter] = Math.Max(minimum[Fighter], rowPortrait + gap + body * 8);
            var columns = LeaderboardLayout.FitColumns(width, gap, minimum);
            bool hasRows = count > 3;
            for (int c = 0; c < ColumnCount; ++c)
            {
                Show(LeaderboardUINames.Header(c), hasRows && !columns.Stacked);
                if (hasRows && !columns.Stacked)
                    Set(LeaderboardUINames.Header(c), gap + columns.Left[c], y, columns.Width[c], body);
            }
            if (hasRows && columns.Stacked)
            {
                Show(LeaderboardUINames.Header(Fighter), true);
                Set(LeaderboardUINames.Header(Fighter), gap, y, width, body);
            }
            SetAccent(LeaderboardUINames.Underline, gap, y + body, width, ceremony.Edge, hasRows);
            if (hasRows) y += body + ceremony.Edge + rowGap;

            for (int r = 3; r < count; ++r)
            {
                string row = LeaderboardUINames.Row(r);
                Show(row, true);
                Show(LeaderboardUINames.Details(row), columns.Stacked);
                for (int c = 0; c < ColumnCount; ++c)
                    Show(LeaderboardUINames.Cell(row, c), !columns.Stacked || c == Rank || c == Fighter);
                int height;
                if (columns.Stacked)
                {
                    bool showImage = width > rowPortrait + gap + body * 4;
                    int nameX = showImage ? rowPortrait + gap : 0;
                    int nameY = showImage ? rowGap : body + 2 * rowGap;
                    Set(LeaderboardUINames.Cell(row, Rank), 0, rowGap, showImage ? rowPortrait : width, body);
                    LayoutPortrait(row, 0, body + 2 * rowGap, rowPortrait, showImage, ceremony.Edge);
                    int nameHeight = WrappedHeight(LeaderboardUINames.Cell(row, Fighter), input.Rows[r].Name, nameX, nameY, width - nameX);
                    int topHeight = Math.Max(nameY + nameHeight, showImage ? body + 2 * rowGap + rowPortrait : 0);
                    height = topHeight + rowGap + WrappedHeight(LeaderboardUINames.Details(row), input.Rows[r].Details, 0, topHeight + rowGap, width) + rowGap;
                }
                else
                {
                    int nameX = columns.Left[Fighter] + rowPortrait + gap;
                    int nameHeight = WrappedHeight(LeaderboardUINames.Cell(row, Fighter), input.Rows[r].Name, nameX, rowGap,
                        columns.Width[Fighter] - rowPortrait - gap);
                    height = Math.Max(nameHeight, rowPortrait) + 2 * rowGap;
                    LayoutPortrait(row, columns.Left[Fighter], rowGap, rowPortrait, true, ceremony.Edge);
                    for (int c = 0; c < ColumnCount; ++c)
                        if (c != Fighter)
                            Set(LeaderboardUINames.Cell(row, c), columns.Left[c], rowGap, columns.Width[c], height - 2 * rowGap);
                }
                Set(row, gap, y, width, height);
                for (int e = 0; e < 4; ++e) Show(LeaderboardUINames.CardBorder(row, e), false);
                SetAccent(LeaderboardUINames.Separator(row), 0, height - ceremony.Edge, width, ceremony.Edge, true);
                y += height + rowGap;
            }
            result.ContentHeight = y;
            int canvasH = Math.Max(y, view.H);
            result.Canvas[LeaderboardUINames.Scroll] = (Math.Max(1, view.W), canvasH);
            bool barNow = canvasH > view.H;
            bool changed = barNow != bar;
            bar = barNow;
            if (!changed) break;   // getViewCoord().width == view.width
        }
        result.ScrollBar = bar;
    }
}
