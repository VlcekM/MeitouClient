namespace ProvingGrounds.Core.Glue;

// Port of the bookie betting window's pixel arithmetic, upstream/ProvingGrounds/plugin/src/TownBookieUI.cpp (Proving Grounds by Shutori, GPLv3),
// task 8.1b (TW §2.6; CAP 7.0-7.4, 7.7): Body (:50), Wrap (:54-63), PlaceTip (:75-86) and PortraitTip's tip size (:87-110, through the town window's
// identical port), LayoutCard (:254-273), Layout (:274-356), with the visibility Refresh (:393-431) and BindCard (:357-376) decide, over the published
// view (Core/Glue/TownBookieLogic.Publish.cs, task 8.1a). Pure; the window side (src/Ui/TownBookieUI.cs) writes the rectangles into the layout XML and
// binds them. Line numbers are TownBookieUI.cpp's.
//
// MyGUI reads and what replaces them here (as the town registry window, Core/Glue/TownArenaUILayout.cs):
//   bet->getFontHeight() (Body :50), NativeUI::Spacing(bet) .... BookieLayoutInput.FontHeight: the standard text's measured font height (the button's
//                                                                font is read as the standard one, as every window since 4.5b)
//   NativeUI::RowHeight(bet / arrange, 0) ...................... RowHeight with the caption's standard-text height and the standard font height
//   bet / arrange ->getTextSize().width (:295) .................. IPgTextMeasure.ButtonWidth (a Kenshi_Button1 caption on one line)
//   getHeight() - getTextRegion().height (Wrap :57) ............. a Kenshi_WordWrap ("BoundedText" :163-168): PgTemplates.WordWrapInset* (30 px; the
//                                                                edit box is measured at least 100 high); a Kenshi_WordWrapEmpty ("Text" :158-162) none
//   ScrollView::getViewCoord / setCanvasSize (:325-354) ......... the Kenshi_ScrollView insets (ArenaUILayout.ScrollInset*, ScrollBarW); the vertical bar
//                                                                shows while the canvas overflows the view; it settles as Meitou's arrange settles it
//   window->getClientWidget()->getSize() ........................ the window rectangle less the Kenshi_WindowCX chrome (ArenaUILayout.WindowClientMargin*)
//   RenderManager::getViewSize (Layout :275-281) ................ BookieLayoutInput.ViewWidth / ViewHeight

/// <summary>What the betting window's arithmetic reads besides the view (the published model).</summary>
public sealed record BookieLayoutInput
{
    public int ViewWidth { get; init; } = 1920;
    public int ViewHeight { get; init; } = 1080;
    /// <summary>The standard text's font height (<c>bet->getFontHeight()</c>, Body :50; NativeUI::Spacing).</summary>
    public int FontHeight { get; init; } = NativeUI.DefaultFontHeight;
    /// <summary>Whether the roster view showed its vertical bar after the previous layout (MyGUI keeps it until the next setCanvasSize; it decides
    /// the first pass's view width).</summary>
    public bool RosterBarBefore { get; init; }
}

/// <summary>The betting window's widget names: the C++'s own (EnsureWindow :193-252, CreateCard :169-192); every widget is named there.</summary>
public static class TownBookieUINames
{
    public const string Window = "PG_Bookie";                                                                          // :199
    public const string Header = "PG_BookieHeader", Timer = "PG_BookieTimer", Roster = "PG_BookieRoster";             // :205-209
    public const string Status = "PG_BookieStatus", Slip = "PG_BookieSlip", StakeValue = "PG_BookieStakeValue";       // :210-212
    public const string Versus = "PG_BookieVS", Bet = "PG_BookieBet", Arrange = "PG_BookieArrange";                    // :215-219
    public const string TipBackground = "PG_BookieFighterTipBackground", FighterTip = "PG_BookieFighterTip";           // :240-244

    static string I(int v) => v.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary><c>PG_BookieStakeAdjust%d</c> (:223), the six stake buttons left to right (kAdjustments).</summary>
    public static string StakeAdjust(int i) => "PG_BookieStakeAdjust" + I(i);
    /// <summary><c>PG_BookieTeam%d</c> (:231), a side's heading in the roster.</summary>
    public static string Team(int side) => "PG_BookieTeam" + I(side);
    /// <summary><c>PG_BookieSide%d</c> (:233), a side's Select Team button.</summary>
    public static string Side(int side) => "PG_BookieSide" + I(side);
    /// <summary><c>PG_BookieCard%d_%d</c> (:171), a card's root (side, slot).</summary>
    public static string Card(int side, int slot) => "PG_BookieCard" + I(side) + "_" + I(slot);
    public static string Portrait(int side, int slot) => Card(side, slot) + "Portrait";         // :176
    public static string Name(int side, int slot) => Card(side, slot) + "Name";                 // :179
    public static string Record(int side, int slot) => Card(side, slot) + "Record";             // :180
    public static string Placeholder(int side, int slot) => Card(side, slot) + "Placeholder";   // :181
    public static string Accent(int side, int slot) => Card(side, slot) + "Accent";             // :187

    /// <summary>The side of a Select Team button (0 or 1), or false.</summary>
    public static bool TrySide(string widget, out int side)
    {
        side = widget == Side(0) ? 0 : widget == Side(1) ? 1 : -1;
        return side >= 0;
    }

    /// <summary>The index 0-5 of a stake button, or false.</summary>
    public static bool TryStakeAdjust(string widget, out int index)
    {
        for (index = 0; index < 6; ++index) if (widget == StakeAdjust(index)) return true;
        index = -1;
        return false;
    }

    /// <summary>The card of a fighter tip's owner (its portrait or placeholder: BindTip :178, :182), or false.</summary>
    public static bool TryTipOwner(string widget, out int side, out int slot)
    {
        for (side = 0; side < 2; ++side)
            for (slot = 0; slot < TownBookieUILayout<int>.Members; ++slot)
                if (widget == Portrait(side, slot) || widget == Placeholder(side, slot)) return true;
        side = slot = -1;
        return false;
    }

    /// <summary>The MyGUI event TownBookieUI.cpp binds each button to (task 4.4b): every button to eventMouseButtonClick (:218, :220, :227, :236).</summary>
    public static UiButtonEvent ButtonEvent(string widget) => UiButtonEvent.Click;
}

/// <summary>The betting window's pixel arithmetic (task 8.1b), C++ function by function. Pure.</summary>
public sealed class TownBookieUILayout<TId>
{
    /// <summary>C++ <c>kMembers</c> (:23): the cards per side.</summary>
    public const int Members = 4;

    readonly TownBookieUiView<TId> v;
    readonly BookieLayoutInput input;
    readonly IPgTextMeasure measure;
    readonly PgWindowLayout result = new();
    readonly int gap, body;
    readonly int[] counts = new int[2];
    LayoutRect roster = new(0, 0, 100, 24);
    bool rosterBar;

    TownBookieUILayout(TownBookieUiView<TId> view, BookieLayoutInput input, IPgTextMeasure measure)
    {
        v = view;
        this.input = input;
        this.measure = measure;
        body = Math.Max(1, input.FontHeight);       // Body :50
        gap = NativeUI.Spacing(input.FontHeight);   // NativeUI::Spacing(bet)
        rosterBar = input.RosterBarBefore;
        for (int side = 0; side < 2; ++side) counts[side] = Math.Min(Members, view.Cards[side].Count);   // Refresh :411
        result.Gap = gap;
        result.Body = body;
        result.Edge = Math.Max(1, gap / 3);
    }

    /// <summary>The window and every widget's rectangle (relative to its parent; the roster's children to its canvas) for the view: Refresh's
    /// visibility then Layout (:427).</summary>
    public static PgWindowLayout Compute(TownBookieUiView<TId> view, BookieLayoutInput input, IPgTextMeasure measure)
    {
        var l = new TownBookieUILayout<TId>(view, input, measure);
        l.Creation();
        l.Visibility();
        l.Layout();
        l.result.ScrollBar = l.rosterBar;
        return l.result;
    }

    void Set(string widget, int x, int y, int w, int h) => result.Rects[widget] = new LayoutRect(x, y, w, h);
    void Move(string widget, int x, int y) { var r = result.Rects[widget]; result.Rects[widget] = r with { X = x, Y = y }; }

    // C++: EnsureWindow :193-252 + CreateCard :169-192: every widget at its `initial` IntCoord(0, 0, 100, 24) (:173, :203)
    void Creation()
    {
        foreach (var n in new[] { TownBookieUINames.Header, TownBookieUINames.Timer, TownBookieUINames.Roster, TownBookieUINames.Status,
                     TownBookieUINames.Slip, TownBookieUINames.StakeValue, TownBookieUINames.Versus, TownBookieUINames.Bet, TownBookieUINames.Arrange,
                     TownBookieUINames.TipBackground, TownBookieUINames.FighterTip })
            Set(n, 0, 0, 100, 24);
        for (int i = 0; i < 6; ++i) Set(TownBookieUINames.StakeAdjust(i), 0, 0, 100, 24);
        for (int side = 0; side < 2; ++side)
        {
            Set(TownBookieUINames.Team(side), 0, 0, 100, 24);
            Set(TownBookieUINames.Side(side), 0, 0, 100, 24);
            for (int i = 0; i < Members; ++i)
                foreach (var n in new[] { TownBookieUINames.Card(side, i), TownBookieUINames.Portrait(side, i), TownBookieUINames.Name(side, i),
                             TownBookieUINames.Record(side, i), TownBookieUINames.Placeholder(side, i), TownBookieUINames.Accent(side, i) })
                    Set(n, 0, 0, 100, 24);
        }
    }

    // The visibility Refresh (:393-431) and BindCard (:357-376) give before Layout reads it: the tip background hidden (:245), the timer while its
    // text is empty (:398), the cards past the side's count (:423), each card's portrait or its placeholder (:374-375: the portrait while the card
    // has a key; here the window side may still hide a portrait it has no cell for, which changes no rectangle)
    void Visibility()
    {
        result.Hidden.Add(TownBookieUINames.TipBackground);
        if (v.View.Timer.Length == 0) result.Hidden.Add(TownBookieUINames.Timer);
        for (int side = 0; side < 2; ++side)
            for (int i = 0; i < Members; ++i)
            {
                if (i >= counts[side]) { result.Hidden.Add(TownBookieUINames.Card(side, i)); continue; }
                bool face = v.Cards[side][i].PortraitKey.Length > 0;
                if (face) result.Hidden.Add(TownBookieUINames.Placeholder(side, i));
                else result.Hidden.Add(TownBookieUINames.Portrait(side, i));
            }
    }

    bool Visible(string widget) => !result.Hidden.Contains(widget);

    // C++: TownBookieUI.cpp:54-63 Wrap: the edit box at (x, y, max(1, width), max(100, Body * 4)); its insets (the box less its text region); its text's
    // height word-wrapped in the text region, at least Body, plus the insets; with a maximum, at most max(Body + insets, maximum). A Kenshi_WordWrap
    // ("bounded") has 30 px of insets each way, a Kenshi_WordWrapEmpty none.
    int Wrap(string widget, string text, int x, int y, int width, int maximum = 0, bool bounded = false)
    {
        int w = Math.Max(1, width);
        int insets = bounded ? Math.Min(PgTemplates.WordWrapInsetH, Math.Max(100, body * 4)) : 0;
        int region = bounded ? Math.Max(1, w - PgTemplates.WordWrapInsetW) : w;
        int height = Math.Max(body, measure.Standard(text, region).Height) + insets;
        if (maximum > 0) height = Math.Min(height, Math.Max(body + insets, maximum));
        Set(widget, x, y, w, height);
        return height;
    }

    int RowHeight(string caption) => NativeUI.RowHeight(measure.Standard(caption, 0).Height, input.FontHeight, 0);

    /// <summary>The caption a card's name label carries (BindCard :358: name + "\n" + style).</summary>
    public static string NameCaption(TownBookie.FighterView f) => f.Name + "\n" + f.Style;

    /// <summary>The heading of a side (Refresh :412-413).</summary>
    public static string Heading(TownBookie.View v, int side) =>
        (side == 0 ? "TEAM A" : "TEAM B") + (v.SelectedSide == side ? " [SELECTED]" : "") + "  |  " + v.Odds[side];

    // C++: TownBookieUI.cpp:254-273 LayoutCard (the card root relative to the roster canvas; its parts relative to the root): its height
    int LayoutCard(int side, int slot, int x, int y, int width, bool solo)
    {
        var f = v.Cards[side][slot].View;
        int inner = Math.Max(1, width - 2 * gap);
        Set(TownBookieUINames.Card(side, slot), x, y, Math.Max(1, width), 1000);
        int top = gap;
        Set(TownBookieUINames.Accent(side, slot), gap, 0, inner, Math.Max(1, gap / 3));   // :258 (the accent is optional in the C++; always made here)
        top += Wrap(TownBookieUINames.Name(side, slot), NameCaption(f), gap, top, inner) + gap;
        bool beside = inner >= body * 27;
        int portrait = Math.Max(1, Math.Min(solo ? body * 13 : body * 8, beside ? inner * 45 / 100 : Math.Min(inner, body * 10)));
        int px = beside ? gap : gap + (inner - portrait) / 2;
        Set(TownBookieUINames.Portrait(side, slot), px, top, portrait, portrait);
        Set(TownBookieUINames.Placeholder(side, slot), px, top, portrait, portrait);
        int detailX = beside ? gap + portrait + gap : gap;
        int detailWidth = beside ? Math.Max(1, inner - portrait - gap) : inner;
        int detailY = beside ? top : top + portrait + gap;
        detailY += Wrap(TownBookieUINames.Record(side, slot), f.Record, detailX, detailY, detailWidth) + gap;
        int height = Math.Max(top + portrait, detailY) + 2 * gap;
        Set(TownBookieUINames.Card(side, slot), x, y, Math.Max(1, width), height);
        return height;
    }

    int ViewW => Math.Max(0, roster.W - ArenaUILayout.ScrollInsetW - (rosterBar ? ArenaUILayout.ScrollBarW : 0));
    int ViewH => Math.Max(0, roster.H - ArenaUILayout.ScrollInsetH);

    // the vertical bar as MyGUI settles it for a canvas (Kenshi_ScrollView: shown while the canvas overflows the view on that axis)
    void SetCanvas(int w, int h)
    {
        result.Canvas[TownBookieUINames.Roster] = (w, h);
        result.ContentHeight = h;
        rosterBar = h > ViewH;   // one evaluation settles it: the view's height does not depend on the vertical bar (the roster has no horizontal one)
    }

    // C++: TownBookieUI.cpp:274-356 Layout
    void Layout()
    {
        // :275-281 the window: 88% x 90% of the view, centred (when the view size changed: here every layout)
        int vw = input.ViewWidth, vh = input.ViewHeight;
        int ww = vw * 88 / 100, wh = vh * 90 / 100;
        result.Window = new LayoutRect((vw - ww) / 2, (vh - wh) / 2, ww, wh);
        result.ClientWidth = Math.Max(0, ww - ArenaUILayout.WindowClientMarginW);
        result.ClientHeight = Math.Max(0, wh - ArenaUILayout.WindowClientMarginH);
        int sizeW = result.ClientWidth, sizeH = result.ClientHeight;
        int width = Math.Max(1, sizeW - 2 * gap);
        int row = Math.Max(RowHeight("Place bet"), RowHeight("Arrange next NPC bout"));
        int infoHalf = Math.Max(1, (width - gap) / 2);
        bool timer = Visible(TownBookieUINames.Timer);
        int headerWidth = timer ? infoHalf : width;
        int headerH = Wrap(TownBookieUINames.Header, v.View.Header, gap, gap, headerWidth, Math.Max(body * 2, sizeH / 9), bounded: true);
        int topH = headerH;
        if (timer)
        {
            int timerH = Wrap(TownBookieUINames.Timer, v.View.Timer, gap * 2 + infoHalf, gap, Math.Max(1, width - infoHalf - gap),
                Math.Max(body * 2, sizeH / 9), bounded: true);
            topH = Math.Max(topH, timerH);
        }
        int top = gap + topH + gap;
        int actionMin = Math.Max(measure.ButtonWidth("Place bet"), measure.ButtonWidth("Arrange next NPC bout")) + 4 * gap;
        int actionColumns = width >= 2 * actionMin + gap ? 2 : 1;
        int actionHeight = actionColumns == 2 ? row : 2 * row + gap;
        int bottom = sizeH - gap - actionHeight;
        int half = Math.Max(1, (width - gap) / 2);
        Set(TownBookieUINames.Bet, gap, bottom, actionColumns == 2 ? half : width, row);
        Set(TownBookieUINames.Arrange, actionColumns == 2 ? gap * 2 + half : gap, actionColumns == 2 ? bottom : bottom + row + gap,
            actionColumns == 2 ? Math.Max(1, width - half - gap) : width, row);
        int slipH = Wrap(TownBookieUINames.Slip, v.View.Slip, gap, 0, infoHalf, Math.Max(body * 3, sizeH / 8), bounded: true);
        int statusH = Wrap(TownBookieUINames.Status, v.View.Status, gap * 2 + infoHalf, 0, Math.Max(1, width - infoHalf - gap),
            Math.Max(body * 3, sizeH / 8), bounded: true);
        int infoH = Math.Max(slipH, statusH);
        bottom -= infoH + gap;
        Move(TownBookieUINames.Slip, gap, bottom);
        Move(TownBookieUINames.Status, gap * 2 + infoHalf, bottom);
        int stakeColumn = Math.Max(1, (width - 6 * gap) / 7);
        bottom -= row + gap;
        for (int i = 0; i < 3; ++i) Set(TownBookieUINames.StakeAdjust(i), gap + i * (stakeColumn + gap), bottom, stakeColumn, row);
        Set(TownBookieUINames.StakeValue, gap + 3 * (stakeColumn + gap), bottom, stakeColumn, row);
        for (int i = 3; i < 6; ++i)
            Set(TownBookieUINames.StakeAdjust(i), gap + (i + 1) * (stakeColumn + gap), bottom,
                i == 5 ? Math.Max(1, sizeW - gap - (gap + (i + 1) * (stakeColumn + gap))) : stakeColumn, row);
        // Both sides remain selectable even when one team's cards are offscreen.
        bottom -= row + gap;
        Set(TownBookieUINames.Side(0), gap, bottom, half, row);
        Set(TownBookieUINames.Side(1), gap * 2 + half, bottom, Math.Max(1, width - half - gap), row);
        int rosterTop = top;
        roster = new LayoutRect(gap, rosterTop, width, Math.Max(1, bottom - rosterTop - gap));
        result.Rects[TownBookieUINames.Roster] = roster;
        for (int pass = 0; pass < 2; ++pass)
        {
            int viewW = ViewW, viewH = ViewH;
            int usable = Math.Max(1, viewW - 2 * gap);
            bool columns = usable >= body * 58;
            int divider = columns ? body * 3 : 0;
            int teamWidth = columns ? Math.Max(1, (usable - divider - 2 * gap) / 2) : usable;
            if (columns)
            {
                result.Hidden.Remove(TownBookieUINames.Versus);
                Set(TownBookieUINames.Versus, gap * 2 + teamWidth, gap + body * 7, divider, body * 3);
            }
            else result.Hidden.Add(TownBookieUINames.Versus);
            int teamTop = gap, maximum = gap;
            for (int side = 0; side < 2; ++side)
            {
                int x = columns ? gap + side * (teamWidth + divider + 2 * gap) : gap;
                int y = teamTop + Wrap(TownBookieUINames.Team(side), Heading(v.View, side), x, teamTop, teamWidth) + gap;
                int cardColumns = counts[side] > 1 && teamWidth >= body * 35 ? 2 : 1;
                int cardWidth = Math.Max(1, (teamWidth - (cardColumns - 1) * gap) / cardColumns);
                for (int i = 0; i < counts[side]; i += cardColumns)
                {
                    int height = 0;
                    for (int j = 0; j < cardColumns && i + j < counts[side]; ++j)
                        height = Math.Max(height, LayoutCard(side, i + j, x + j * (cardWidth + gap), y, cardWidth, counts[side] == 1));
                    y += height + gap;
                }
                y += 2 * gap;
                maximum = Math.Max(maximum, y);
                if (!columns) teamTop = y;
            }
            SetCanvas(Math.Max(1, viewW), Math.Max(viewH, maximum));
            result.ContentHeight = maximum;   // contentHeight = maximum (:351)
            if (viewW == ViewW) break;
        }
    }

    /// <summary>C++ Layout :355 (and Wheel :131-136): the roster's vertical offset (positive, from the top) clamped to [0, content - view].</summary>
    public static int ClampOffset(PgWindowLayout l, int offset)
    {
        int view = Math.Max(0, l.Rects[TownBookieUINames.Roster].H - ArenaUILayout.ScrollInsetH);
        return Math.Max(0, Math.Min(offset, l.ContentHeight - view));
    }
}
