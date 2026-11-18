namespace ProvingGrounds.Core.Glue;

// Port of the Arena window's pixel arithmetic, upstream/ProvingGrounds/plugin/src/ArenaUI.cpp (Proving Grounds by Shutori, GPLv3), task 4.5b:
// RefreshResponsiveMode (:2512-2530, the compact test), PanelLabel / PanelButton (:2532-2554, their creation sizes), PlaceLabel (:2580-2587),
// PlaceButtons (:2594-2615), LayoutColumn (:2617-2703), LayoutPanel (:2705-2756), RefreshCompactDrawers (:2480-2490, which columns show),
// LayoutRows (:687-728) and RebuildList's empty label (:730-753). The C++ creates the widgets once (CreatePanel :2814-2926) and positions them
// on every refresh from the window's client size and the measured font; the port computes the same rectangles here, purely, from the client
// size, the font height and the texts (measured through IArenaTextMeasure: MyGUI's getTextSize, done by the window side with Meitou's text
// measurer), and the window side (src/Ui/ArenaUI.cs) writes them into the layout XML and the bindings.

/// <summary>A widget rectangle in pixels, relative to its parent (MyGUI <c>IntCoord</c>).</summary>
public readonly record struct LayoutRect(int X, int Y, int W, int H)
{
    public int Right => X + W;
    public int Bottom => Y + H;
    public bool Contains(int x, int y) => x >= X && x < X + W && y >= Y && y < Y + H;
}

/// <summary>The text sizes the C++ asks MyGUI for (<c>getTextSize</c>), measured by the window side.</summary>
public interface IArenaTextMeasure
{
    /// <summary>The height of <paramref name="text"/> in a PanelLabel (<c>Kenshi_WordWrapEmpty</c>: the standard font, word-wrapped) of
    /// <paramref name="width"/> pixels (<c>TextBox::getTextSize().height</c> after <c>setCoord</c>, PlaceLabel :2583-2585).</summary>
    int LabelHeight(string text, int width);

    /// <summary>The height of <paramref name="text"/> in a <c>Kenshi_PaintedWordWrapEmpty</c> edit box (the painted font, word-wrapped) of
    /// <paramref name="width"/> pixels: a button's presentation text (PlaceButtons :2606-2607), a row label or a handler label (LayoutRows :698-713).</summary>
    int PaintedHeight(string text, int width);

    /// <summary>The width of <paramref name="caption"/> on a <c>Kenshi_Button1</c> (<c>Button::getTextSize().width</c>, the button's
    /// "naturalWidth", PanelButton :2547, SyncButtonText :2570).</summary>
    int ButtonTextWidth(string caption);
}

/// <summary>One portrait row as the layout needs it (MakeRow :632-686): its label caption, whether it has the health bar (roster rows) and the
/// handler line (a team / pool prisoner row with a handler).</summary>
public readonly record struct ArenaLayoutRow(string Caption, bool Status, bool Handler, string HandlerCaption);

/// <summary>What the Arena window's pixel arithmetic reads.</summary>
public sealed record ArenaLayoutInput
{
    /// <summary>The game window's size (<c>RenderManager::getViewSize</c>, RefreshResponsiveMode :2516-2521: the window takes 23/25 of it).</summary>
    public int ViewWidth { get; init; } = 1920;
    public int ViewHeight { get; init; } = 1080;
    /// <summary><c>g_metrics->getFontHeight()</c> (the standard text's font height) and the sample's one-line text height
    /// (<c>getTextSize().height</c> of "Arena", NativeUI::RowHeight).</summary>
    public int FontHeight { get; init; } = NativeUI.DefaultFontHeight;
    public int SampleTextHeight { get; init; } = NativeUI.DefaultFontHeight;
    /// <summary>The window-side state (RefreshResponsiveMode, the drawer toggles).</summary>
    public bool Compact { get; init; }
    public bool RosterDrawerOpen { get; init; }
    public bool SettingsDrawerOpen { get; init; }
    /// <summary>From the model: team mode (IsTeamUiMode) and the balance menu.</summary>
    public bool TeamMode { get; init; } = true;
    public bool BalanceMenuOpen { get; init; }
    /// <summary>Captions the layout measures, by widget name (labels and the buttons' captions; a missing one is the C++'s creation caption).</summary>
    public IReadOnlyDictionary<string, string> Captions { get; init; } = new Dictionary<string, string>();
    public IReadOnlyList<ArenaLayoutRow> Roster { get; init; } = [];
    public IReadOnlyList<ArenaLayoutRow> TeamA { get; init; } = [];
    public IReadOnlyList<ArenaLayoutRow> TeamB { get; init; } = [];
    public IReadOnlyList<ArenaLayoutRow> Pool { get; init; } = [];
}

/// <summary>The rectangles of one layout pass: each widget's place in its parent (<see cref="Rects"/>), each scroll view's canvas
/// (<see cref="Canvas"/>, <c>setCanvasSize</c>), the columns that show, and the window's own rectangle and client size.</summary>
public sealed class ArenaLayout
{
    public Dictionary<string, LayoutRect> Rects { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, (int W, int H)> Canvas { get; } = new(StringComparer.Ordinal);
    /// <summary>The window (RefreshResponsiveMode :2519-2520, screen pixels) and its client widget's size.</summary>
    public LayoutRect Window { get; set; }
    public int ClientWidth { get; set; }
    public int ClientHeight { get; set; }
    /// <summary>RefreshCompactDrawers :2484-2489.</summary>
    public bool[] ColumnVisible { get; } = [true, true, true];
    public bool HeroVisible { get; set; } = true;
    public bool DrawerButtonsVisible { get; set; }
    /// <summary>Whether each list scroll and column shows its vertical bar (the canvas is taller than the view).</summary>
    public HashSet<string> VerticalBar { get; } = new(StringComparer.Ordinal);
}

/// <summary>
/// The Arena window's widget names and positions (task 4.5b): the C++'s own names (CreatePanel :2814-2926, MakeRow :640) for every named widget;
/// the C++'s anonymous widgets get a name derived from their owner (a button's presentation text <c>&lt;button&gt;Text</c>, a column's content
/// <c>PG_Column&lt;i&gt;Content</c>, a row's parts <c>PG_Row_&lt;kind&gt;_&lt;index&gt;Img</c> ...). The window side keeps every name the same across
/// shapes (the search edit box keeps its focus across a Replace).
/// </summary>
public static class ArenaUINames
{
    public const string Window = "ProvingGroundsArenaWindow", Metrics = "PG_Metrics", Hero = "PG_Hero", Recruit = "PG_PrisonerRecruitment";
    public const string LblRoster = "PG_LblRoster", RosterCount = "PG_RosterCount", Search = "PG_RosterSearch", RosterHint = "PG_RosterHint";
    public const string Roster = "PG_Roster", LblMode = "PG_LblMode", ModeAvB = "PG_ModeAvB", ModeTeams1v1 = "PG_ModeTeams1v1", ModeLast = "PG_ModeLast";
    public const string LblDest = "PG_LblDest", DestA = "PG_DestA", DestB = "PG_DestB", LblTeamA = "PG_LblTeamA", LblTeamB = "PG_LblTeamB";
    public const string LblPool = "PG_LblPool", TeamA = "PG_TeamA", TeamB = "PG_TeamB", Pool = "PG_Pool", AutoBalance = "PG_AutoBalance";
    public const string BalanceSkill = "PG_BalanceSkill", BalanceRating = "PG_BalanceRating", Swap = "PG_SwapTeams", Clear = "PG_ClearTeams";
    public const string BuilderHint = "PG_BuilderHint", LblPreview = "PG_LblPreview", PreviewImg = "PG_PreviewImg", PreviewName = "PG_PreviewName";
    public const string PreviewMeta = "PG_PreviewMeta", PreviewStats = "PG_PreviewStats", Remove = "PG_Remove", LblSettings = "PG_LblSettings";
    public const string LblLoc = "PG_LblLoc", LocArena = "PG_LocArena", LocSmall = "PG_LocSmallArena", LocBanner = "PG_LocBanner";
    public const string Medical = "PG_MedicalProtocol", LblSummary = "PG_LblSummary", Summary = "PG_Summary", Status = "PG_Status";
    public const string Start = "PG_Start", Stop = "PG_Stop", RosterDrawer = "PG_RosterDrawer", SettingsDrawer = "PG_SettingsDrawer";
    public const string SettingsTip = "PG_SettingsTip";

    /// <summary>C++ :2875-2878 PG_Preset1..3.</summary>
    public static string Preset(int index) => "PG_Preset" + (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
    /// <summary>C++ :2830 PG_Column0..2 and their content widget (anonymous in the C++).</summary>
    public static string Column(int i) => "PG_Column" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public static string ColumnContent(int i) => Column(i) + "Content";
    /// <summary>The presentation text of a PanelButton (:2541-2542, anonymous in the C++).</summary>
    public static string TextOf(string button) => button + "Text";
    /// <summary>C++ :640 <c>PG_Row_&lt;kind&gt;_&lt;index&gt;</c> and its parts (anonymous in the C++).</summary>
    public static string Row(ArenaList list, int index) => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"PG_Row_{(int)list}_{index}");
    public static string RowImage(string row) => row + "Img";
    public static string RowLabel(string row) => row + "Label";
    public static string RowStatusBack(string row) => row + "StatusBack";
    public static string RowStatusFill(string row) => row + "StatusFill";
    public static string RowHandlerImage(string row) => row + "HandlerImg";
    public static string RowHandlerLabel(string row) => row + "HandlerLabel";
    /// <summary>C++ :745 "PG_EmptyList" (the same name in every list; the port adds the list kind, the layout needs unique names).</summary>
    public static string EmptyList(ArenaList list) => "PG_EmptyList_" + ((int)list).ToString(System.Globalization.CultureInfo.InvariantCulture);
    /// <summary>The scroll view of a list (g_rosterScroll ...).</summary>
    public static string ScrollOf(ArenaList list) => list switch { ArenaList.Roster => Roster, ArenaList.TeamA => TeamA, ArenaList.TeamB => TeamB, _ => Pool };

    /// <summary>The MyGUI event ArenaUI.cpp binds each button to (task 4.4b): every PanelButton to eventMouseButtonClick (:2541); the presets
    /// also to eventMouseButtonPressed (:2881, after the Click delegate, whose handler OnPresetButtonClick :2196-2199 does nothing: the press
    /// loads or saves, OnPresetMousePressed :2201-2217); the rows to eventMouseButtonClick / DoubleClick / Released (:641-645), copied by
    /// <see cref="ArenaRowPointer{TId}"/> from the press.</summary>
    public static UiButtonEvent ButtonEvent(string widget)
    {
        if (TryParseRow(widget, out _, out _)) return UiButtonEvent.Pointer;
        for (int i = 0; i < 3; i++) if (widget == Preset(i)) return UiButtonEvent.Press;
        return UiButtonEvent.Click;
    }

    /// <summary>Whether <paramref name="name"/> is a row's root button; its list and index.</summary>
    public static bool TryParseRow(string name, out ArenaList list, out int index)
    {
        list = ArenaList.Roster;
        index = -1;
        if (!name.StartsWith("PG_Row_", StringComparison.Ordinal)) return false;
        var parts = name.AsSpan(7);
        int sep = parts.IndexOf('_');
        if (sep <= 0) return false;
        if (!int.TryParse(parts[..sep], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int kind)
            || kind < 0 || kind > 3) return false;
        if (!int.TryParse(parts[(sep + 1)..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out index))
            return false;
        list = (ArenaList)kind;
        return true;
    }

    /// <summary>The C++ creation captions (CreatePanel :2836-2911): what a widget says until the model changes it.</summary>
    public static readonly IReadOnlyDictionary<string, string> CreationCaptions = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [Hero] = "Arena Setup", [Recruit] = "Recruit Prisoners", [LblRoster] = "Squad Roster", [RosterCount] = "0 fighters",
        [RosterHint] = "Double-click or drag to assign. Right-click to remove.", [LblMode] = "Match Mode", [ModeAvB] = "Teams",
        [ModeTeams1v1] = "Teams 1v1", [ModeLast] = "Last Man Standing", [LblDest] = "Double-click assigns to Team A", [DestA] = "Assign to Team A",
        [DestB] = "Assign to Team B", [LblTeamA] = "Team A", [LblTeamB] = "Team B", [LblPool] = "Fighter pool", [AutoBalance] = "Balance",
        [BalanceSkill] = "By Skill", [BalanceRating] = "By Rating", [Swap] = "Swap Teams", [Clear] = "Clear Teams",
        [BuilderHint] = "Presets: left-click to load, right-click to save.", ["PG_Preset1"] = "Preset 1", ["PG_Preset2"] = "Preset 2",
        ["PG_Preset3"] = "Preset 3", [LblPreview] = "Selected Fighter", [PreviewName] = "No fighter selected",
        [PreviewMeta] = "Select a roster or team row", [PreviewStats] = "Choose a fighter to review readiness.", [Remove] = "Remove Fighter",
        [LblSettings] = "Match Settings", [LblLoc] = "Location", [LocArena] = "Arena", [LocSmall] = "Small", [LocBanner] = "Banner",
        [Medical] = "Medical: Ringside Aid", [LblSummary] = "Match Summary", [Summary] = "Team A vs Team B", [Start] = "Start Fight",
        [Stop] = "Stop Fight", [RosterDrawer] = "Squad Roster", [SettingsDrawer] = "Match Settings",
    };
}

/// <summary>
/// The Arena window's pixel arithmetic (task 4.5b), C++ function by function. Pure: <see cref="Compute"/> returns every widget's rectangle for a
/// client size, the font and the texts. Kenshi template numbers it needs (the window's client margins, the scroll view's client and bar) are
/// <see cref="WindowClientMarginW"/> ... (kenshi_templates.xml Kenshi_WindowCX :348-367, Kenshi_ScrollView :253-260; Meitou's faithful screens
/// arrange templates at TemplateScale 1, ScreenRouter.BindFaithfulScreen).
/// </summary>
public sealed class ArenaUILayout
{
    /// <summary>C++ :232-235 kRowH, kPortraitSize, kHandlerPortraitSize, kStatusBarHeight.</summary>
    public const int RowH = 56, PortraitSize = 48, HandlerPortraitSize = 22, StatusBarHeight = 6;

    /// <summary>Kenshi_WindowCX (kenshi_templates.xml:348-367): the body starts 38 px down and ends 1 px above the bottom (360 x 320: body 0 38 360
    /// 281), the client sits 3 px inside it (3 3 354 275): the client is the window's width - 6 and height - 45.</summary>
    public const int WindowClientMarginW = 6, WindowClientMarginH = 45, WindowClientX = 3, WindowClientY = 41;

    /// <summary>Kenshi_ScrollView (kenshi_templates.xml:253-260, 190 x 185): the client 10 10 155 150 with both bars, the vertical bar 20 wide,
    /// the horizontal bar 20 high (hidden: NativeUI.Scroll sets VisibleHScroll false, its height goes to the client): the view is the width - 15
    /// (- 20 with the vertical bar) and the height - 15.</summary>
    public const int ScrollInsetW = 15, ScrollInsetH = 15, ScrollBarW = 20, ScrollClientX = 10, ScrollClientY = 10;

    readonly ArenaLayoutInput input;
    readonly IArenaTextMeasure measure;
    readonly ArenaLayout result = new();
    readonly int gap, body, buttonH;

    ArenaUILayout(ArenaLayoutInput input, IArenaTextMeasure measure)
    {
        this.input = input;
        this.measure = measure;
        gap = NativeUI.Spacing(input.FontHeight);
        body = Math.Max(1, input.FontHeight);
        buttonH = NativeUI.RowHeight(input.SampleTextHeight, input.FontHeight, 0);
    }

    string Caption(string widget) => input.Captions.TryGetValue(widget, out var c) ? c
        : ArenaUINames.CreationCaptions.TryGetValue(widget, out var d) ? d : "";

    void Set(string widget, int x, int y, int w, int h) => result.Rects[widget] = new LayoutRect(x, y, w, h);

    /// <summary>The window, its client and every widget's rectangle.</summary>
    public static ArenaLayout Compute(ArenaLayoutInput input, IArenaTextMeasure measure)
    {
        var l = new ArenaUILayout(input, measure);
        l.Creation();
        l.Responsive();
        l.LayoutPanel();
        return l.result;
    }

    // C++: ArenaUI.cpp:2512-2530 RefreshResponsiveMode: the window at 1/25 of the view, 23/25 of its size; compact when the client is narrower
    // than 76 font heights (the window side decides the flag, CompactFor, and passes it in).
    void Responsive()
    {
        result.Window = WindowFor(input.ViewWidth, input.ViewHeight);
        result.ClientWidth = Math.Max(0, result.Window.W - WindowClientMarginW);
        result.ClientHeight = Math.Max(0, result.Window.H - WindowClientMarginH);
    }

    /// <summary>C++ :2519-2520 <c>g_window->setCoord(w / 25, h / 25, w * 23 / 25, h * 23 / 25)</c> (integer arithmetic).</summary>
    public static LayoutRect WindowFor(int viewWidth, int viewHeight) => new(viewWidth / 25, viewHeight / 25, viewWidth * 23 / 25, viewHeight * 23 / 25);

    /// <summary>C++ :2522 compact = the client narrower than 76 font heights.</summary>
    public static bool CompactFor(int viewWidth, int viewHeight, int fontHeight) =>
        Math.Max(0, WindowFor(viewWidth, viewHeight).W - WindowClientMarginW) < fontHeight * 76;

    // C++: ArenaUI.cpp:2814-2926 CreatePanel: the creation sizes of the widgets LayoutPanel never moves (the hidden metrics label, the settings tip)
    // and the defaults of those it moves only when they show (a hidden button keeps its creation coordinate, PanelButton :2537 0 0 200 32 and its
    // text :2542 0 0 200 32).
    void Creation()
    {
        Set(ArenaUINames.Metrics, 0, 0, 100, 24);
        foreach (var b in AllButtons()) { Set(b, 0, 0, 200, 32); Set(ArenaUINames.TextOf(b), 0, 0, 200, 32); }
        Set(ArenaUINames.SettingsTip, 0, 0, 280, 80);
        Set(ArenaUINames.PreviewImg, 0, 0, PortraitSize, PortraitSize);
        foreach (var n in new[] { ArenaUINames.LblTeamA, ArenaUINames.LblTeamB, ArenaUINames.LblPool, ArenaUINames.Hero }) Set(n, 0, 0, 200, 24);
        foreach (var n in new[] { ArenaUINames.TeamA, ArenaUINames.TeamB, ArenaUINames.Pool }) Set(n, 0, 0, 200, 300);
        for (int i = 0; i < 3; i++) { Set(ArenaUINames.Column(i), 0, 0, 300, 300); Set(ArenaUINames.ColumnContent(i), 0, 0, 300, 300); }
    }

    /// <summary>Every PanelButton of the window, in creation order (CreatePanel).</summary>
    public static IReadOnlyList<string> AllButtons() =>
    [
        ArenaUINames.Recruit, ArenaUINames.ModeAvB, ArenaUINames.ModeTeams1v1, ArenaUINames.ModeLast, ArenaUINames.DestA, ArenaUINames.DestB,
        ArenaUINames.AutoBalance, ArenaUINames.BalanceSkill, ArenaUINames.BalanceRating, ArenaUINames.Swap, ArenaUINames.Clear,
        ArenaUINames.Preset(0), ArenaUINames.Preset(1), ArenaUINames.Preset(2), ArenaUINames.Remove, ArenaUINames.LocArena, ArenaUINames.LocSmall,
        ArenaUINames.LocBanner, ArenaUINames.Medical, ArenaUINames.Start, ArenaUINames.Stop, ArenaUINames.RosterDrawer, ArenaUINames.SettingsDrawer,
    ];

    // C++: ArenaUI.cpp:2580-2587 PlaceLabel (a PanelLabel: the standard font)
    int PlaceLabel(string label, int x, int y, int width)
    {
        int w = Math.Max(1, width);
        int height = Math.Max(body, measure.LabelHeight(Caption(label), w));
        Set(label, x, y, w, height);
        return height;
    }

    // The same for a painted-font label (a button's presentation text): PlaceLabel's arithmetic, the label's own font.
    int PlacePaintedLabel(string label, string caption, int x, int y, int width)
    {
        int w = Math.Max(1, width);
        int height = Math.Max(body, measure.PaintedHeight(caption, w));
        Set(label, x, y, w, height);
        return height;
    }

    int NaturalWidth(string button) => measure.ButtonTextWidth(Caption(button));

    // C++: ArenaUI.cpp:2594-2615 PlaceButtons
    int PlaceButtons(IReadOnlyList<string> buttons, int count, int x, int y, int width)
    {
        int minWidth = 0;
        for (int i = 0; i < count; ++i) minWidth = Math.Max(minWidth, NaturalWidth(buttons[i]) + 4 * gap);
        int columns = Math.Max(1, Math.Min(count, (width + gap) / (minWidth + gap)));
        int cell = Math.Max(1, (width - (columns - 1) * gap) / columns);
        int bottom = y;
        for (int row = 0; row < count; row += columns)
        {
            int height = buttonH;
            for (int i = row; i < Math.Min(row + columns, count); ++i)
                height = Math.Max(height, PlacePaintedLabel(ArenaUINames.TextOf(buttons[i]), Caption(buttons[i]), gap, gap, cell - 2 * gap) + 2 * gap);
            for (int i = row; i < Math.Min(row + columns, count); ++i)
                Set(buttons[i], x + (i - row) * (cell + gap), bottom, cell, height);
            bottom += height + gap;
        }
        return bottom - y - gap;
    }

    /// <summary>A scroll view's view size (<c>getViewCoord</c>) for its rectangle, with or without the vertical bar.</summary>
    static (int W, int H) ViewOf(LayoutRect scroll, bool bar) =>
        (Math.Max(0, scroll.W - ScrollInsetW - (bar ? ScrollBarW : 0)), Math.Max(0, scroll.H - ScrollInsetH));

    // C++: ArenaUI.cpp:2617-2703 LayoutColumn. The C++ lays the column out twice: the first pass's setCanvasSize shows or hides the scroll bar
    // (the view gets narrower or wider), the second pass lays out with the view that results. The port starts with the bar hidden (a new view)
    // and decides it from the first pass's canvas, as MyGUI does.
    void LayoutColumn(int column)
    {
        string scrollName = ArenaUINames.Column(column);
        var scroll = result.Rects[scrollName];
        bool bar = false;
        for (int pass = 0; pass < 2; ++pass)
        {
            var view = ViewOf(scroll, bar);
            int width = Math.Max(1, view.W - 2 * gap);
            int viewH = view.H;
            int y = gap;
            if (column == 0)
            {
                y += PlaceLabel(ArenaUINames.LblRoster, gap, y, width) + gap;
                y += PlaceLabel(ArenaUINames.RosterCount, gap, y, width) + gap;
                Set(ArenaUINames.Search, gap, y, width, buttonH + gap); y += buttonH + 2 * gap;
                y += PlaceLabel(ArenaUINames.RosterHint, gap, y, width) + gap;
                int listH = Math.Max(body * 9, viewH - y - gap);
                Set(ArenaUINames.Roster, gap, y, width, listH); y += listH + gap;
                LayoutRows(ArenaList.Roster, input.Roster);
            }
            else if (column == 1)
            {
                y += PlaceLabel(ArenaUINames.LblMode, gap, y, width) + gap;
                string[] modes = [ArenaUINames.ModeAvB, ArenaUINames.ModeTeams1v1, ArenaUINames.ModeLast];
                y += PlaceButtons(modes, 3, gap, y, width) + gap;
                y += PlaceLabel(ArenaUINames.LblDest, gap, y, width) + gap;
                string[] assign = [ArenaUINames.DestA, ArenaUINames.DestB];
                y += PlaceButtons(assign, input.TeamMode ? 2 : 1, gap, y, width) + gap;
                int listH = Math.Max(body * 9, viewH - y - body * 12);
                if (input.TeamMode)
                {
                    bool stacked = width < body * 32;
                    int half = stacked ? width : (width - gap) / 2;
                    int hA = PlaceLabel(ArenaUINames.LblTeamA, gap, y, half);
                    if (stacked)
                    {
                        y += hA + gap;
                        Set(ArenaUINames.TeamA, gap, y, half, listH); y += listH + gap;
                        y += PlaceLabel(ArenaUINames.LblTeamB, gap, y, half) + gap;
                        Set(ArenaUINames.TeamB, gap, y, half, listH); y += listH + gap;
                    }
                    else
                    {
                        int hB = PlaceLabel(ArenaUINames.LblTeamB, 2 * gap + half, y, half);
                        y += Math.Max(hA, hB) + gap;
                        Set(ArenaUINames.TeamA, gap, y, half, listH);
                        Set(ArenaUINames.TeamB, 2 * gap + half, y, half, listH); y += listH + gap;
                    }
                    LayoutRows(ArenaList.TeamA, input.TeamA);
                    LayoutRows(ArenaList.TeamB, input.TeamB);
                }
                else
                {
                    y += PlaceLabel(ArenaUINames.LblPool, gap, y, width) + gap;
                    Set(ArenaUINames.Pool, gap, y, width, listH); y += listH + gap;
                    LayoutRows(ArenaList.Pool, input.Pool);
                }
                string[] balance = [ArenaUINames.AutoBalance, ArenaUINames.BalanceSkill, ArenaUINames.BalanceRating];
                string[] edit = [ArenaUINames.AutoBalance, ArenaUINames.Swap, ArenaUINames.Clear];
                string[] poolEdit = [ArenaUINames.AutoBalance, ArenaUINames.Clear];
                y += PlaceButtons(input.BalanceMenuOpen ? balance : input.TeamMode ? edit : poolEdit,
                    input.BalanceMenuOpen || input.TeamMode ? 3 : 2, gap, y, width) + gap;
                y += PlaceLabel(ArenaUINames.BuilderHint, gap, y, width) + gap;
                string[] presets = [ArenaUINames.Preset(0), ArenaUINames.Preset(1), ArenaUINames.Preset(2)];
                y += PlaceButtons(presets, 3, gap, y, width) + gap;
            }
            else
            {
                y += PlaceLabel(ArenaUINames.LblPreview, gap, y, width) + gap;
                Set(ArenaUINames.PreviewImg, gap, y, PortraitSize, PortraitSize);
                int textX = 2 * gap + PortraitSize;
                int textY = y;
                textY += PlaceLabel(ArenaUINames.PreviewName, textX, textY, width - PortraitSize - gap) + gap;
                textY += PlaceLabel(ArenaUINames.PreviewMeta, textX, textY, width - PortraitSize - gap) + gap;
                y = Math.Max(textY, y + PortraitSize + gap);
                y += PlaceLabel(ArenaUINames.PreviewStats, gap, y, width) + gap;
                y += PlaceButtons([ArenaUINames.Remove], 1, gap, y, width) + gap;
                y += PlaceLabel(ArenaUINames.LblSettings, gap, y, width) + gap;
                y += PlaceLabel(ArenaUINames.LblLoc, gap, y, width) + gap;
                string[] locations = [ArenaUINames.LocArena, ArenaUINames.LocSmall, ArenaUINames.LocBanner];
                y += PlaceButtons(locations, 3, gap, y, width) + gap;
                y += PlaceButtons([ArenaUINames.Medical], 1, gap, y, width) + gap;
                y += PlaceLabel(ArenaUINames.LblSummary, gap, y, width) + gap;
                y += PlaceLabel(ArenaUINames.Summary, gap, y, width) + gap;
            }
            Set(ArenaUINames.ColumnContent(column), 0, 0, width + 2 * gap, y);
            int canvasH = Math.Max(y, viewH);
            result.Canvas[scrollName] = (width + 2 * gap, canvasH);
            if (pass == 0) bar = canvasH > viewH;
        }
        if (result.Canvas[scrollName].H > ViewOf(scroll, bar).H) result.VerticalBar.Add(scrollName); else result.VerticalBar.Remove(scrollName);
    }

    // C++: ArenaUI.cpp:687-728 LayoutRows, with RebuildList's empty label (:740-747) when the list is empty. Two passes as LayoutColumn (the
    // canvas shows or hides the bar).
    void LayoutRows(ArenaList list, IReadOnlyList<ArenaLayoutRow> rows)
    {
        string scrollName = ArenaUINames.ScrollOf(list);
        var scroll = result.Rects[scrollName];
        bool bar = false;
        for (int pass = 0; pass < 2; ++pass)
        {
            var view = ViewOf(scroll, bar);
            int width = Math.Max(1, view.W);
            int y = 0;
            for (int i = 0; i < rows.Count; ++i)
            {
                var row = rows[i];
                string name = ArenaUINames.Row(list, i);
                int textX = PortraitSize + 2 * gap;
                int labelW = Math.Max(1, width - textX - gap);
                int labelH = Math.Max(body, measure.PaintedHeight(row.Caption, labelW));
                Set(ArenaUINames.RowLabel(name), textX, gap, labelW, labelH);
                Set(ArenaUINames.RowImage(name), gap, gap, PortraitSize, PortraitSize);
                int textH = labelH;
                if (row.Status)
                {
                    Set(ArenaUINames.RowStatusBack(name), textX, gap + labelH + gap, Math.Max(1, width - textX - gap), StatusBarHeight);
                    Set(ArenaUINames.RowStatusFill(name), 0, 0, Math.Max(1, width - textX - gap), StatusBarHeight);   // cut by the bind (RefreshStatusBar)
                    textH += gap + StatusBarHeight;
                }
                int height = Math.Max(PortraitSize, textH) + 2 * gap;
                if (row.Handler)
                {
                    int hx = HandlerPortraitSize + 2 * gap;
                    int handlerW = Math.Max(1, width - hx - gap);
                    int handlerH = Math.Max(body, measure.PaintedHeight(row.HandlerCaption, handlerW));
                    Set(ArenaUINames.RowHandlerLabel(name), hx, height, handlerW, handlerH);
                    Set(ArenaUINames.RowHandlerImage(name), gap, height, HandlerPortraitSize, HandlerPortraitSize);
                    height += Math.Max(HandlerPortraitSize, handlerH) + gap;
                }
                Set(name, 0, y, width, height);
                y += height + gap;
            }
            if (rows.Count == 0)
            {
                // RebuildList :743-747: WrappedLabel (8, 8, width - 16, 80), then its height = max(24, text height)
                int ew = Math.Max(1, width - 16);
                Set(ArenaUINames.EmptyList(list), 8, 8, ew, Math.Max(24, measure.LabelHeight(list == ArenaList.Roster
                    ? ArenaUiTexts.EmptyRoster : ArenaUiTexts.EmptyAssign, ew)));
            }
            int canvasH = Math.Max(y, view.H);
            result.Canvas[scrollName] = (width, canvasH);
            if (pass == 0) bar = canvasH > view.H;
        }
        if (result.Canvas[scrollName].H > ViewOf(scroll, bar).H) result.VerticalBar.Add(scrollName); else result.VerticalBar.Remove(scrollName);
    }

    // C++: ArenaUI.cpp:2705-2756 LayoutPanel, with RefreshCompactDrawers' visibility (:2484-2489)
    void LayoutPanel()
    {
        result.DrawerButtonsVisible = input.Compact;
        result.ColumnVisible[0] = !input.Compact || input.RosterDrawerOpen;
        result.ColumnVisible[2] = !input.Compact || input.SettingsDrawerOpen;
        int clientW = result.ClientWidth, clientH = result.ClientHeight;
        int width = Math.Max(1, clientW - 2 * gap);
        int headerH = buttonH + 2 * gap;
        int footerH = Math.Min(clientH / 3, body * 4 + 2 * gap);
        int footerY = Math.Max(headerH, clientH - footerH - gap);
        int contentH = Math.Max(1, footerY - headerH - gap);
        int actionW = Math.Min(width / 2, Math.Max(NaturalWidth(ArenaUINames.Stop), NaturalWidth(ArenaUINames.Start)) + 4 * gap);
        Set(ArenaUINames.Status, gap, footerY, Math.Max(1, width - actionW - gap), footerH);
        PlaceButtons([ArenaUINames.Start], 1, gap + width - actionW, footerY, actionW);
        PlaceButtons([ArenaUINames.Stop], 1, gap + width - actionW, footerY, actionW);
        if (input.Compact)
        {
            result.HeroVisible = false;
            string[] header = [ArenaUINames.RosterDrawer, ArenaUINames.SettingsDrawer, ArenaUINames.Recruit];
            int actualHeader = PlaceButtons(header, 3, gap, gap, width);
            int top = Math.Max(headerH, actualHeader + 2 * gap);
            bool drawer = input.RosterDrawerOpen || input.SettingsDrawerOpen;
            int sideW = drawer ? Math.Max(1, width * 2 / 5) : 0;
            Set(ArenaUINames.Column(1), gap + (drawer ? sideW + gap : 0), top, Math.Max(1, width - (drawer ? sideW + gap : 0)), Math.Max(1, footerY - top - gap));
            Set(ArenaUINames.Column(0), gap, top, Math.Max(1, sideW), Math.Max(1, footerY - top - gap));
            Set(ArenaUINames.Column(2), gap, top, Math.Max(1, sideW), Math.Max(1, footerY - top - gap));
        }
        else
        {
            result.HeroVisible = true;
            int recruitWidth = Math.Min(width / 2, NaturalWidth(ArenaUINames.Recruit) + 4 * gap);
            PlaceLabel(ArenaUINames.Hero, gap, gap, Math.Max(1, width - recruitWidth - gap));
            PlaceButtons([ArenaUINames.Recruit], 1, gap + width - recruitWidth, gap, recruitWidth);
            int sideW = (width - 2 * gap) / 4;
            Set(ArenaUINames.Column(0), gap, headerH, sideW, contentH);
            Set(ArenaUINames.Column(1), 2 * gap + sideW, headerH, width - 2 * sideW - 2 * gap, contentH);
            Set(ArenaUINames.Column(2), gap + width - sideW, headerH, sideW, contentH);
        }
        for (int i = 0; i < 3; ++i)
            if (result.ColumnVisible[i]) LayoutColumn(i);
    }

    // C++: ArenaUI.cpp:1405-1450 ShowSettingsTip / PlaceSettingsTip (the tip's size and place, in the client's coordinates; the pointer in the
    // client's coordinates too)
    /// <summary>The settings tip's rectangle in the client for a pointer at (<paramref name="px"/>, <paramref name="py"/>) relative to the client
    /// (ShowSettingsTip: width min(client - 2 gap, 28 font heights), height min(client - 2 gap, text height + 3 gap); PlaceSettingsTip: 14 / 18 px
    /// right of and below the pointer, flipped to the other side near the right / bottom edge, never closer than 8 px to the left / top).</summary>
    public static LayoutRect SettingsTip(int clientW, int clientH, int fontHeight, int px, int py, Func<int, int> textHeightAt)
    {
        int gap = NativeUI.Spacing(fontHeight);
        int width = Math.Max(1, Math.Min(clientW - 2 * gap, fontHeight * 28));
        int height = Math.Min(clientH - 2 * gap, textHeightAt(width) + 3 * gap);
        int left = px + 14, top = py + 18;
        if (left + width > clientW - 8) left = px - width - 8;
        if (top + height > clientH - 8) top = py - height - 8;
        if (left < 8) left = 8;
        if (top < 8) top = 8;
        return new LayoutRect(left, top, width, height);
    }

    /// <summary>C++ :270-281 RefreshStatusBar: the fill's width <c>(int)(back width * recovery + .5)</c>, shown when above 0.</summary>
    public static int StatusFillWidth(int backWidth, float recovery) => (int)(backWidth * recovery + .5f);
}
