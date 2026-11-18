namespace ProvingGrounds.Core.Glue;

// Port of the bookie wager results window's pixel arithmetic, upstream/ProvingGrounds/plugin/src/TownBookieResultUI.cpp (Proving Grounds by Shutori,
// GPLv3), task 8.1b (TW §2.6; CAP 7.0-7.4): Body (:53), the colours (:54-62), Wrap (:81-88), LayoutCard (:171-189), Layout (:190-277), with what Show
// (:292-321) decides first (the display sides, the headings, the card counts, BindCard's portrait or placeholder :161-170). Pure; the window side
// (src/Ui/TownBookieResultUI.cs) writes the rectangles into the layout XML and binds them. Line numbers are TownBookieResultUI.cpp's.
//
// MyGUI reads and what replaces them here:
//   measure->getFontHeight() / getTextSize() (Body :53) ......... the standard text's font height (BookieResultLayoutInput.FontHeight) and the one-line
//                                                                 standard size of "WAGER RESULT" (a Kenshi_TextboxStandardText, :128)
//   NativeUI::Spacing(measure), RowHeight(closeButton, 0) ........ Core/Glue/NativeUI.cs (RowHeight with the caption's standard-text height, as every
//                                                                 window since 4.5b)
//   title / payout ->getTextSize().width (Layout :206-207) ....... their text word-wrapped at the width the label had before this layout (MyGUI keeps
//                                                                 it: 100 at creation :127-132, then the previous layout's), as the Results window
//                                                                 (Core/Glue/ResultsUILayout.cs FitWindowToContent)
//   title->getTextSize().width after Wrap (:236) ................. its text wrapped at the width Wrap just gave it
//   closeButton->getTextSize().width (:275) ...................... IPgTextMeasure.ButtonWidth
//   getHeight() - getTextRegion().height (Wrap :83) .............. 0: every wrapped label is a Kenshi_WordWrapEmpty (NativeUI::WrappedLabel)
//   window->getSize() - getClientWidget()->getSize() (:208-211) .. the Kenshi_WindowCX chrome (ArenaUILayout.WindowClientMargin*)
//   RenderManager::getViewSize (:193-196) ........................ BookieResultLayoutInput.ViewWidth / ViewHeight

/// <summary>What the result window's arithmetic reads besides the view.</summary>
public sealed record BookieResultLayoutInput
{
    public int ViewWidth { get; init; } = 1920;
    public int ViewHeight { get; init; } = 1080;
    /// <summary>The standard text's font height (<c>measure->getFontHeight()</c>, Body :53; NativeUI::Spacing).</summary>
    public int FontHeight { get; init; } = NativeUI.DefaultFontHeight;
    /// <summary>The widths the title and payout labels had when Layout measured their text (their coordinates of the previous layout: 100 at
    /// creation).</summary>
    public int TitleWidthBefore { get; init; } = 100;
    public int PayoutWidthBefore { get; init; } = 100;
}

/// <summary>One layout of the result window: the rectangles, and the widths the wrapped title and payout labels were left with (the next layout
/// measures their text there, Layout :206-207).</summary>
public sealed record BookieResultLayout(PgWindowLayout Layout, int TitleWidth, int PayoutWidth);

/// <summary>The result window's widget names: the C++'s own (EnsureWindow :113-160, CreateCard :94-112).</summary>
public static class TownBookieResultUINames
{
    public const string Window = "PG_BookieWagerResults";                                                               // :122
    public const string Measure = "PG_BookieResultMeasure", Title = "PG_BookieResultTitle";                           // :128, :130
    public const string Context = "PG_BookieResultContext", Payout = "PG_BookieResultPayout";                         // :131-132
    public const string RailLeft = "PG_BookieResultTitleRailLeft", RailRight = "PG_BookieResultTitleRailRight";      // :136-137
    public const string Close = "PG_BookieResultClose";                                                                // :138

    static string I(int v) => v.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary><c>PG_BookieResultColumn%d</c> (:142), a column's root (0 = the victor's or Team A's).</summary>
    public static string Column(int column) => "PG_BookieResultColumn" + I(column);
    public static string Heading(int column) => Column(column) + "Heading";   // :146
    public static string Rail(int column) => Column(column) + "Rail";         // :148
    /// <summary><c>PG_BookieResult_%d_%d</c> (:96), a card's root (display column, slot).</summary>
    public static string Card(int column, int slot) => "PG_BookieResult_" + I(column) + "_" + I(slot);
    public static string Portrait(int column, int slot) => Card(column, slot) + "Portrait";         // :102
    public static string Placeholder(int column, int slot) => Card(column, slot) + "Placeholder";   // :104
    public static string Name(int column, int slot) => Card(column, slot) + "Name";                 // :107
    /// <summary><c>...Border%c</c> (:110-111), edge 0-3.</summary>
    public static string Border(int column, int slot, int edge) => Card(column, slot) + "Border" + I(edge);

    /// <summary>Every button to eventMouseButtonClick (:139).</summary>
    public static UiButtonEvent ButtonEvent(string widget) => UiButtonEvent.Click;
}

/// <summary>The result window's pixel arithmetic and Show's decisions (task 8.1b), C++ function by function. Pure.</summary>
public sealed class TownBookieResultUILayout<TId>
{
    /// <summary>C++ <c>kMembers</c> (:19).</summary>
    public const int Members = 4;

    /// <summary>C++ Gold / Winner / Loser / Neutral (:54-57), red, green, blue.</summary>
    public static readonly (float R, float G, float B) Gold = (.86f, .66f, .22f), Winner = (.92f, .72f, .24f), Loser = (.58f, .24f, .20f),
        Neutral = (.64f, .67f, .70f);

    /// <summary>C++ OutcomeColour (:58-62): tone 1 green, 2 red, else gold.</summary>
    public static (float R, float G, float B) OutcomeColour(int tone) => tone == 1 ? (.28f, .82f, .38f) : tone == 2 ? (.90f, .27f, .20f) : Gold;

    /// <summary>C++ Show :298-299: the side drawn in each column (the winner first, else Team A first).</summary>
    public static int DisplaySide(int winningSide, int column) =>
        column == 0 ? (winningSide >= 0 ? winningSide : 0) : (winningSide >= 0 ? 1 - winningSide : 1);

    /// <summary>C++ Show :305-309: a column's heading.</summary>
    public static string Heading(TownBookie.ResultView v, int column)
    {
        int side = DisplaySide(v.WinningSide, column);
        string heading = v.WinningSide < 0 ? (side == 0 ? "TEAM A" : "TEAM B")
            : (column == 0 ? "VICTOR  -  TEAM " : "DEFEATED  -  TEAM ") + (side == 0 ? "A" : "B");
        if (side == v.SelectedSide) heading += "  [YOUR PICK]";
        return heading;
    }

    /// <summary>C++ Layout :249-250: a column's colour (neutral without a winner, else victor gold / defeated red).</summary>
    public static (float R, float G, float B) ColumnColour(int winningSide, int column) => winningSide < 0 ? Neutral : column == 0 ? Winner : Loser;

    /// <summary>The caption a card's name label carries (BindCard :163).</summary>
    public static string NameCaption(TownBookie.FighterView f) => f.Name + "\n" + f.Style;

    readonly TownBookieResultUiView<TId> v;
    readonly BookieResultLayoutInput input;
    readonly IPgTextMeasure measure;
    readonly PgWindowLayout result = new();
    readonly int gap, body, edge;
    readonly int[] counts = new int[2];

    TownBookieResultUILayout(TownBookieResultUiView<TId> view, BookieResultLayoutInput input, IPgTextMeasure measure)
    {
        v = view;
        this.input = input;
        this.measure = measure;
        body = Math.Max(1, Math.Max(input.FontHeight, measure.Standard("WAGER RESULT", 0).Height));   // Body :53
        gap = NativeUI.Spacing(input.FontHeight);                                                   // :198 NativeUI::Spacing(measure)
        edge = Math.Max(1, gap / 3);                                                                // :199
        for (int column = 0; column < 2; ++column)                                                  // Show :304
            counts[column] = Math.Min(Members, view.Cards[DisplaySide(view.View.WinningSide, column)].Count);
        result.Gap = gap;
        result.Body = body;
        result.Edge = edge;
    }

    /// <summary>The window and every widget's rectangle (relative to its parent) for the view: Show's decisions then Layout (:317).</summary>
    public static BookieResultLayout Compute(TownBookieResultUiView<TId> view, BookieResultLayoutInput input, IPgTextMeasure measure)
    {
        var l = new TownBookieResultUILayout<TId>(view, input, measure);
        l.Creation();
        l.Visibility();
        l.Layout();
        return new BookieResultLayout(l.result, l.titleWidth, l.payoutWidth);
    }

    int titleWidth, payoutWidth;

    void Set(string widget, int x, int y, int w, int h) => result.Rects[widget] = new LayoutRect(x, y, w, h);

    /// <summary>The card count of a display column.</summary>
    public int Count(int column) => counts[column];

    // C++: EnsureWindow :113-160 + CreateCard :94-112: every widget at `initial` IntCoord(0, 0, 100, 24) (:98, :127), the accents at (0, 0, 1, 1) (:68)
    void Creation()
    {
        foreach (var n in new[] { TownBookieResultUINames.Measure, TownBookieResultUINames.Title, TownBookieResultUINames.Context,
                     TownBookieResultUINames.Payout, TownBookieResultUINames.Close })
            Set(n, 0, 0, 100, 24);
        Set(TownBookieResultUINames.RailLeft, 0, 0, 1, 1);
        Set(TownBookieResultUINames.RailRight, 0, 0, 1, 1);
        for (int column = 0; column < 2; ++column)
        {
            Set(TownBookieResultUINames.Column(column), 0, 0, 100, 24);
            Set(TownBookieResultUINames.Heading(column), 0, 0, 100, 24);
            Set(TownBookieResultUINames.Rail(column), 0, 0, 1, 1);
            for (int slot = 0; slot < Members; ++slot)
            {
                foreach (var n in new[] { TownBookieResultUINames.Card(column, slot), TownBookieResultUINames.Portrait(column, slot),
                             TownBookieResultUINames.Placeholder(column, slot), TownBookieResultUINames.Name(column, slot) })
                    Set(n, 0, 0, 100, 24);
                for (int e = 0; e < 4; ++e) Set(TownBookieResultUINames.Border(column, slot, e), 0, 0, 1, 1);
            }
        }
    }

    // Show :311-315 and BindCard :161-170: the cards past the column's count hidden, each card's portrait or its "?" placeholder (a valid actor: one
    // with a portrait key); the measure label hidden (:129)
    void Visibility()
    {
        result.Hidden.Add(TownBookieResultUINames.Measure);
        for (int column = 0; column < 2; ++column)
        {
            int side = DisplaySide(v.View.WinningSide, column);
            for (int slot = 0; slot < Members; ++slot)
            {
                if (slot >= counts[column]) { result.Hidden.Add(TownBookieResultUINames.Card(column, slot)); continue; }
                bool face = v.Cards[side][slot].PortraitKey.Length > 0;
                result.Hidden.Add(face ? TownBookieResultUINames.Placeholder(column, slot) : TownBookieResultUINames.Portrait(column, slot));
            }
        }
    }

    // C++: TownBookieResultUI.cpp:81-88 Wrap: the label at (x, y, max(1, width), Body); its text's height word-wrapped at that width (no insets: a
    // Kenshi_WordWrapEmpty), at least Body
    int Wrap(string widget, string text, int x, int y, int width)
    {
        int w = Math.Max(1, width);
        int height = Math.Max(body, measure.Standard(text, w).Height);
        Set(widget, x, y, w, height);
        return height;
    }

    // C++: TownBookieResultUI.cpp:171-189 LayoutCard (the card root relative to its column, its parts relative to the root): its height
    int LayoutCard(int column, int slot, string name, int x, int y, int width, int portrait)
    {
        int portraitX = (width - portrait) / 2;
        Set(TownBookieResultUINames.Portrait(column, slot), portraitX, gap, portrait, portrait);
        Set(TownBookieResultUINames.Placeholder(column, slot), portraitX, gap, portrait, portrait);
        int nameTop = gap + portrait + gap;
        int nameHeight = Wrap(TownBookieResultUINames.Name(column, slot), name, gap, nameTop, Math.Max(1, width - 2 * gap));
        int height = nameTop + nameHeight + gap;
        Set(TownBookieResultUINames.Card(column, slot), x, y, width, height);
        Set(TownBookieResultUINames.Border(column, slot, 0), portraitX, gap, edge, portrait);
        Set(TownBookieResultUINames.Border(column, slot, 1), portraitX, gap, portrait, edge);
        Set(TownBookieResultUINames.Border(column, slot, 2), portraitX, gap + portrait - edge, portrait, edge);
        Set(TownBookieResultUINames.Border(column, slot, 3), portraitX + portrait - edge, gap, edge, portrait);
        return height;
    }

    // C++: TownBookieResultUI.cpp:190-277 Layout
    void Layout()
    {
        int sw = input.ViewWidth, sh = input.ViewHeight;
        int maximumCount = Math.Max(counts[0], counts[1]);
        int cardColumns = maximumCount > 1 ? 2 : 1;
        int cardRows = Math.Max(1, (maximumCount + cardColumns - 1) / cardColumns);
        int naturalCardWidth = body * 12;
        int naturalColumnWidth = cardColumns * naturalCardWidth + (cardColumns + 1) * gap;
        int contentWidth = 2 * naturalColumnWidth + 3 * gap;
        contentWidth = Math.Max(contentWidth, measure.Standard(v.View.Title, Math.Max(1, input.TitleWidthBefore)).Width + 10 * gap);
        contentWidth = Math.Max(contentWidth, measure.Standard(v.View.Payout, Math.Max(1, input.PayoutWidthBefore)).Width + 8 * gap);
        int chromeWidth = ArenaUILayout.WindowClientMarginW, chromeHeight = ArenaUILayout.WindowClientMarginH;
        int outerWidth = Math.Min(sw * 90 / 100, Math.Max(520, contentWidth + chromeWidth));
        int clientWidth = Math.Max(1, outerWidth - chromeWidth);
        int innerWidth = Math.Max(1, clientWidth - 3 * gap);
        int columnWidth = Math.Max(1, (innerWidth - gap) / 2);
        int cardWidth = Math.Max(1, (columnWidth - (cardColumns + 1) * gap) / cardColumns);
        int portrait = Math.Min(body * 9, Math.Max(body * 5, cardWidth - 2 * gap));
        int cardHeight = portrait + 4 * gap + 2 * body;
        int headingHeight = 2 * body;
        int teamsHeight = headingHeight + 2 * gap + cardRows * cardHeight + (cardRows - 1) * gap;
        int titleHeight = 2 * body, contextHeight = 2 * body, payoutHeight = 5 * body;
        int closeHeight = NativeUI.RowHeight(measure.Standard("Close", 0).Height, input.FontHeight, 0);
        int desiredClientHeight = titleHeight + contextHeight + teamsHeight + payoutHeight + closeHeight + 10 * gap;
        int outerHeight = Math.Min(sh * 90 / 100, Math.Max(360, desiredClientHeight + chromeHeight));
        result.Window = new LayoutRect((sw - outerWidth) / 2, (sh - outerHeight) / 2, outerWidth, outerHeight);
        result.ClientWidth = Math.Max(0, outerWidth - chromeWidth);
        result.ClientHeight = Math.Max(0, outerHeight - chromeHeight);

        int sizeW = result.ClientWidth;
        int width = Math.Max(1, sizeW - 2 * gap);
        int y = gap;
        int actualTitleHeight = Wrap(TownBookieResultUINames.Title, v.View.Title, 4 * gap, y, Math.Max(1, sizeW - 8 * gap));
        titleWidth = Math.Max(1, sizeW - 8 * gap);   // the title's width now (the next layout measures it there)
        int titleTextWidth = measure.Standard(v.View.Title, Math.Max(1, sizeW - 8 * gap)).Width;
        int railWidth = Math.Max(0, (sizeW - titleTextWidth - 12 * gap) / 2);
        int railTop = y + actualTitleHeight / 2;
        Set(TownBookieResultUINames.RailLeft, gap, railTop, railWidth, edge);
        Set(TownBookieResultUINames.RailRight, sizeW - gap - railWidth, railTop, railWidth, edge);
        y += actualTitleHeight + gap;
        y += Wrap(TownBookieResultUINames.Context, v.View.Context, gap, y, width) + 2 * gap;
        int actualColumnWidth = Math.Max(1, (width - gap) / 2);
        int columnBottom = y;
        for (int column = 0; column < 2; ++column)
        {
            int side = DisplaySide(v.View.WinningSide, column);
            int left = gap + column * (actualColumnWidth + gap);
            int headingH = Wrap(TownBookieResultUINames.Heading(column), Heading(v.View, column), gap, gap, Math.Max(1, actualColumnWidth - 2 * gap));
            Set(TownBookieResultUINames.Rail(column), gap, gap + headingH + gap / 2, Math.Max(1, actualColumnWidth - 2 * gap), edge);
            int gridTop = gap + headingH + 2 * gap;
            int localCardWidth = Math.Max(1, (actualColumnWidth - (cardColumns + 1) * gap) / cardColumns);
            int localPortrait = Math.Min(body * 9, Math.Max(body * 5, localCardWidth - 2 * gap));
            int tallestBottom = gridTop;
            for (int slot = 0; slot < counts[column]; ++slot)
            {
                int cardX = gap + (slot % cardColumns) * (localCardWidth + gap);
                int cardY = gridTop + (slot / cardColumns) * (cardHeight + gap);
                int height = LayoutCard(column, slot, NameCaption(v.Cards[side][slot].View), cardX, cardY, localCardWidth, localPortrait);
                tallestBottom = Math.Max(tallestBottom, cardY + height);
            }
            Set(TownBookieResultUINames.Column(column), left, y, actualColumnWidth, tallestBottom + gap);   // :248 then :269 setSize
            columnBottom = Math.Max(columnBottom, y + tallestBottom + gap);
        }
        y = columnBottom + gap;
        y += Wrap(TownBookieResultUINames.Payout, v.View.Payout, gap, y, width) + 2 * gap;
        payoutWidth = width;   // the payout's width now
        int buttonWidth = Math.Min(width, Math.Max(body * 12, measure.ButtonWidth("Close") + 6 * gap));
        Set(TownBookieResultUINames.Close, (sizeW - buttonWidth) / 2, y, buttonWidth, closeHeight);
    }
}
