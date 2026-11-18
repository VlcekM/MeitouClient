namespace ProvingGrounds.Core.Glue;

// Port of the Rewards window's pixel arithmetic, upstream/ProvingGrounds/plugin/src/RewardsUI.cpp (Proving Grounds by Shutori, GPLv3), task 5.3b:
// BodyHeight / WrappedHeight (:169-180), LayoutRewardAccent (:277-311), LayoutIcon (:312-326), LayoutGeneralIcon (:327-346), LayoutFighters (:595-629),
// LayoutRewardCard (:630-680), LayoutGeneralCard (:681-738), LayoutDetails (:739-920), LayoutProfile (:921-958), FitWindowToScreen (:959-970) and
// LayoutWindow (:971-1050), with Core RewardsLayout. Pure; the window side (src/Ui/RewardsUI.cs) writes the rectangles into the layout XML. The
// widgets' visibility is UpdateCatalogVisibility's (the view, Core/Glue/RewardsUILogic.cs) plus what the layout passes hide.
//
// MyGUI reads and what replaces them here (as the Results / Leaderboard windows, Core/Glue/ResultsUILayout.cs, LeaderboardUILayout.cs):
//   getFontHeight / getTextSize (labels, edit boxes, buttons) .. IRewardsTextMeasure (the window side measures through src/Ui/TextMeasure.cs)
//   NativeUI::Spacing / RowHeight ............................... Core/Glue/NativeUI.cs (RowHeight with the caption's standard text height, as the
//                                                                  Leaderboard window's buttons)
//   getHeight() - getTextRegion().height of a Kenshi_WordWrap .... PgTemplates.WordWrapInset* (30 px; the edit box at least that tall)
//   ScrollView::getViewCoord (the vertical bar shown when the     .. RewardsScroll below: the Kenshi_ScrollView insets (ArenaUILayout.ScrollInset*,
//     canvas is taller than the view), setCanvasSize, setCoord       ScrollBarW); the bar follows the canvas at each setCanvasSize / resize
//   the window's client size (getClientWidget()->getSize()) ..... the window rectangle less the Kenshi_WindowCX chrome (ArenaUILayout.WindowClientMargin*)
//   InventoryIcon::createIconImage's texture size ............... RewardsLayoutInput.PieceIcons / GeneralIcons (the window side's icon sizes)

/// <summary>The text sizes the Rewards window's arithmetic asks for (MyGUI <c>getTextSize</c>): the standard text and Kenshi_Button1 captions
/// (<see cref="IPgTextMeasure"/>), plus the painted text of the fighter rows (<c>Kenshi_PaintedWordWrapEmpty</c>, <c>Kenshi_TextboxPaintedText</c>).</summary>
public interface IRewardsTextMeasure : IPgTextMeasure
{
    /// <summary>The size of <paramref name="text"/> in the painted font word-wrapped at <paramref name="wrapWidth"/> (0: one line).</summary>
    (int Width, int Height) Painted(string text, int wrapWidth);
}

/// <summary>What the Rewards window's arithmetic reads besides the view.</summary>
public sealed record RewardsLayoutInput
{
    public int ViewWidth { get; init; } = 1920;
    public int ViewHeight { get; init; } = 1080;
    /// <summary>The standard text's font height (<c>g_title->getFontHeight()</c>, NativeUI::Spacing).</summary>
    public int FontHeight { get; init; } = NativeUI.DefaultFontHeight;
    /// <summary>The status line the window shows (Show's, or a purchase's: RewardsPurchaseGate).</summary>
    public string Status { get; init; } = RewardsUiTexts.StatusInitial;
    /// <summary>Per piece (ArenaRewards.Piece order): the icon's texture size, or null when the card has no icon (C++ <c>iconLoaded</c> false).</summary>
    public IReadOnlyList<(int W, int H)?> PieceIcons { get; init; } = [];
    /// <summary>Per General item (ArenaRewards.GeneralItem order): as <see cref="PieceIcons"/>.</summary>
    public IReadOnlyList<(int W, int H)?> GeneralIcons { get; init; } = [];
    /// <summary>Whether each scroll view showed its vertical bar after the previous layout (MyGUI keeps it until the next setCanvasSize; it decides
    /// the first pass's view width).</summary>
    public bool FighterBarBefore { get; init; }
    public bool ProfileBarBefore { get; init; }
    public bool DetailBarBefore { get; init; }
}

/// <summary>One scroll view's state after a layout: its canvas height, its view height (the scroll offset clamps to content - view) and its bar.</summary>
public readonly record struct RewardsCanvasInfo(int Content, int View, bool Bar);

/// <summary>The Rewards window's layout: every widget's rectangle (relative to its parent, as MyGUI's setCoord), the widgets the layout hides, the
/// three canvases, the window rectangle.</summary>
public sealed class RewardsWindowLayout
{
    public Dictionary<string, LayoutRect> Rects { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, (int W, int H)> Canvas { get; } = new(StringComparer.Ordinal);
    public HashSet<string> Hidden { get; } = new(StringComparer.Ordinal);
    public LayoutRect Window { get; set; }
    public int ClientWidth { get; set; }
    public int ClientHeight { get; set; }
    public int Gap { get; set; }
    public int Body { get; set; }
    public RewardsCanvasInfo Fighter { get; set; }
    public RewardsCanvasInfo Profile { get; set; }
    public RewardsCanvasInfo Detail { get; set; }
    /// <summary>The icons the layout shows when their texture is ready (a card's, or a locked licence row's manifest).</summary>
    public HashSet<string> IconsShown { get; } = new(StringComparer.Ordinal);
    public bool Compact { get; set; }
    public bool Stacked { get; set; }
}

/// <summary>The Rewards window's widget names: the C++'s own (EnsureWindow :1051-1227, RebuildRows :1228-1305).</summary>
public static class RewardsUINames
{
    public const string Window = "ProvingGroundsRewardsWindow";
    public const string ProfileScroll = "PG_RewardsProfileScroll", FighterScroll = "PG_RewardsFighterScroll", DetailScroll = "PG_RewardsDetailScroll";
    public const string Title = "PG_RewardsTitle", FightersHeading = "PG_RewardsFightersHeading", SelectedHeading = "PG_RewardsSelectedHeading";
    public const string Subtitle = "PG_RewardsSubtitle", Status = "PG_RewardsStatus", Selected = "PG_RewardsSelected", Career = "PG_RewardsCareer";
    public const string GeneralTab = "PG_RewardsGeneralTab", ArmourTab = "PG_RewardsArmourTab", WeaponsTab = "PG_RewardsWeaponsTab";
    public const string BlueprintsHeading = "PG_GeneralBlueprintsHeading", SuppliesHeading = "PG_GeneralSuppliesHeading";
    public const string WeaponHeading = "PG_WeaponLicence_Heading", WeaponDetails = "PG_WeaponLicence_Details", WeaponBuy = "PG_WeaponLicence_Buy";
    public const string Close = "PG_RewardsClose";

    static string I(int v) => v.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The three tabs in RewardsCatalogTab order (:1134-1136).</summary>
    public static string Tab(RewardsCatalogTab tab) => tab switch
    {
        RewardsCatalogTab.Armour => ArmourTab,
        RewardsCatalogTab.Weapons => WeaponsTab,
        _ => GeneralTab,
    };

    /// <summary>C++ :1111 <c>PG_Reward_%d</c> and its parts (:1112-1123, MakeRewardAccent :237-276).</summary>
    public static string Piece(int i) => "PG_Reward_" + I(i);
    public static string Accent(int i) => Piece(i) + "_Accent";
    public static string Fade(int i, int k) => Piece(i) + "_Fade" + I(k);
    public static string Border(int i, int k) => Piece(i) + "_Border" + I(k);
    public static string Icon(int i) => Piece(i) + "_Icon";
    public static string Name(int i) => Piece(i) + "_Name";
    public static string Progress(int i) => Piece(i) + "_Progress";
    public static string Claim(int i) => Piece(i) + "_Claim";

    /// <summary>C++ :1140 <c>PG_General_%d</c> and its parts.</summary>
    public static string General(int i) => "PG_General_" + I(i);
    public static string GeneralIcon(int i) => General(i) + "_Icon";
    public static string GeneralName(int i) => General(i) + "_Name";
    public static string GeneralDetails(int i) => General(i) + "_Details";
    public static string GeneralBuy(int i) => General(i) + "_Buy";

    /// <summary>C++ :1180 <c>PG_Licence_%d</c> and its parts.</summary>
    public static string Licence(int i) => "PG_Licence_" + I(i);
    public static string LicenceHeading(int i) => Licence(i) + "_Heading";
    public static string LicenceDetails(int i) => Licence(i) + "_Details";
    public static string LicenceBuy(int i) => Licence(i) + "_Buy";

    /// <summary>C++ RebuildRows :1241-1290, <c>%d</c> = the fighter's index in the squad list: the row button, its portrait, name, Marks and rail
    /// (the rail's name is the Marks label's name + <c>_Rail</c>, :1276-1280).</summary>
    public static string Row(int squadIndex) => "PG_RewardFighter_" + I(squadIndex);
    public static string RowPortrait(int squadIndex) => "PG_RewardPortrait_" + I(squadIndex);
    public static string RowName(int squadIndex) => "PG_RewardFighterName_" + I(squadIndex);
    public static string RowMarks(int squadIndex) => "PG_RewardMarks_" + I(squadIndex);
    public static string RowRail(int squadIndex) => RowMarks(squadIndex) + "_Rail";

    /// <summary>The MyGUI event RewardsUI.cpp binds each button to (task 4.4b): every button (the tabs :1094, the claims :1124, the General BUY
    /// :1155, the licences :1189 / :1206, Close :1214, the fighter rows :1245) to eventMouseButtonClick; no button is bound to a press.</summary>
    public static UiButtonEvent ButtonEvent(string widget) => UiButtonEvent.Click;

    static bool Suffixed(string widget, string prefix, string suffix, out int index)
    {
        index = -1;
        if (!widget.StartsWith(prefix, StringComparison.Ordinal) || !widget.EndsWith(suffix, StringComparison.Ordinal)) return false;
        var digits = widget.AsSpan(prefix.Length, widget.Length - prefix.Length - suffix.Length);
        if (digits.Length == 0 || digits.Length > 6) return false;
        foreach (char ch in digits) if (!char.IsAsciiDigit(ch)) return false;
        index = int.Parse(digits, System.Globalization.CultureInfo.InvariantCulture);
        return true;
    }

    /// <summary>A claim button's piece index (<c>PG_Reward_%d_Claim</c>).</summary>
    public static bool TryClaim(string widget, out int piece) => Suffixed(widget, "PG_Reward_", "_Claim", out piece);
    /// <summary>A General BUY button's item index (<c>PG_General_%d_Buy</c>).</summary>
    public static bool TryGeneralBuy(string widget, out int item) => Suffixed(widget, "PG_General_", "_Buy", out item);
    /// <summary>An armour licence's button row index (<c>PG_Licence_%d_Buy</c>).</summary>
    public static bool TryLicenceBuy(string widget, out int row) => Suffixed(widget, "PG_Licence_", "_Buy", out row);
    /// <summary>A fighter row button's squad index (<c>PG_RewardFighter_%d</c>).</summary>
    public static bool TryRow(string widget, out int squadIndex) => Suffixed(widget, "PG_RewardFighter_", "", out squadIndex);

    /// <summary>The canvas of a widget the C++ binds the wheel to (BindWheel :196-206): the tabs and the profile labels (:1095-1099), every card's
    /// icon / name / progress / claim (:1125-1128), the General cards and headings (:1157-1170), the licences (:1191-1193, :1208-1210), the rows'
    /// button / portrait / name / Marks (:1246-1274); false for any other widget (the accents, fades, borders and rails: the C++ OnWheel is not
    /// called; MyGUI's own ScrollView wheel scrolls). In the port the rows' portrait, name and Marks let the pointer through to the row button
    /// (NeedMouse false, src/Ui/RewardsUI.cs), so a wheel there picks the row: those branches are unreachable but kept for fidelity.</summary>
    public static bool WheelCanvas(string widget, out RewardsScrollModel.Canvas canvas)
    {
        canvas = RewardsScrollModel.Canvas.Detail;
        if (widget is GeneralTab or ArmourTab or WeaponsTab or SelectedHeading or Selected or Career)
        {
            canvas = RewardsScrollModel.Canvas.Profile;
            return true;
        }
        if (TryRow(widget, out _) || Suffixed(widget, "PG_RewardPortrait_", "", out _) || Suffixed(widget, "PG_RewardFighterName_", "", out _)
            || Suffixed(widget, "PG_RewardMarks_", "", out _))
        {
            canvas = RewardsScrollModel.Canvas.Fighter;
            return true;
        }
        if (widget is BlueprintsHeading or SuppliesHeading or WeaponHeading or WeaponDetails or WeaponBuy) return true;
        return Suffixed(widget, "PG_Reward_", "_Icon", out _) || Suffixed(widget, "PG_Reward_", "_Name", out _)
            || Suffixed(widget, "PG_Reward_", "_Progress", out _) || TryClaim(widget, out _)
            || Suffixed(widget, "PG_General_", "_Icon", out _) || Suffixed(widget, "PG_General_", "_Name", out _)
            || Suffixed(widget, "PG_General_", "_Details", out _) || TryGeneralBuy(widget, out _)
            || Suffixed(widget, "PG_Licence_", "_Heading", out _) || Suffixed(widget, "PG_Licence_", "_Details", out _) || TryLicenceBuy(widget, out _);
    }
}

/// <summary>The Rewards window's pixel arithmetic (task 5.3b), C++ function by function. Pure.</summary>
public sealed class RewardsUILayout<TId>
{
    /// <summary>One MyGUI ScrollView (Kenshi_ScrollView, NativeUI::Scroll): its rectangle, its canvas and whether its vertical bar shows. MyGUI
    /// re-decides the bar (canvas taller than the view) at each setCanvasSize and each resize.</summary>
    sealed class Scroll(string name, bool bar)
    {
        public readonly string Name = name;
        public LayoutRect Rect = new(0, 0, 300, 200);
        public bool Bar = bar;
        public int CanvasW = -1, CanvasH = -1;
        int ViewHOf(LayoutRect r) => Math.Max(0, r.H - ArenaUILayout.ScrollInsetH);
        public int ViewW => Math.Max(0, Rect.W - ArenaUILayout.ScrollInsetW - (Bar ? ArenaUILayout.ScrollBarW : 0));
        public int ViewH => ViewHOf(Rect);
        public void SetRect(LayoutRect r)
        {
            Rect = r;
            if (CanvasH >= 0) Bar = CanvasH > ViewH;
        }
        public void SetCanvas(int w, int h)
        {
            CanvasW = w;
            CanvasH = h;
            Bar = h > ViewH;
        }
    }

    readonly RewardsUiView<TId> v;
    readonly RewardsLayoutInput input;
    readonly IRewardsTextMeasure measure;
    readonly RewardsWindowLayout result = new();
    readonly int gap, body;
    readonly Scroll fighter, profile, detail;
    int fighterContent, profileContent, detailContent;

    RewardsUILayout(RewardsUiView<TId> view, RewardsLayoutInput input, IRewardsTextMeasure measure)
    {
        v = view;
        this.input = input;
        this.measure = measure;
        gap = NativeUI.Spacing(input.FontHeight);
        // C++ :178 BodyHeight: g_title ("ARENA REWARDS")'s font height or text height
        body = Math.Max(1, Math.Max(input.FontHeight, measure.Standard(v.Title, 0).Height));
        result.Gap = gap;
        result.Body = body;
        fighter = new Scroll(RewardsUINames.FighterScroll, input.FighterBarBefore);
        profile = new Scroll(RewardsUINames.ProfileScroll, input.ProfileBarBefore);
        detail = new Scroll(RewardsUINames.DetailScroll, input.DetailBarBefore);
    }

    /// <summary>The window and every widget's rectangle for the view (RefreshDetails :1553-1562: UpdateDetails, then FitWindowToScreen, LayoutWindow).</summary>
    public static RewardsWindowLayout Compute(RewardsUiView<TId> view, RewardsLayoutInput input, IRewardsTextMeasure measure)
    {
        var l = new RewardsUILayout<TId>(view, input, measure);
        l.Creation();
        l.FitWindowToScreen();
        l.LayoutWindow();
        l.result.Fighter = new RewardsCanvasInfo(l.fighterContent, l.fighter.ViewH, l.fighter.Bar);
        l.result.Profile = new RewardsCanvasInfo(l.profileContent, l.profile.ViewH, l.profile.Bar);
        l.result.Detail = new RewardsCanvasInfo(l.detailContent, l.detail.ViewH, l.detail.Bar);
        return l.result;
    }

    void Set(string widget, int x, int y, int w, int h) => result.Rects[widget] = new LayoutRect(x, y, w, h);

    void Show(string widget, bool visible)
    {
        if (visible) result.Hidden.Remove(widget); else result.Hidden.Add(widget);
    }

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

    (int W, int H)? PieceIcon(int i) => i < input.PieceIcons.Count ? input.PieceIcons[i] : null;
    (int W, int H)? GeneralIcon(int i) => i < input.GeneralIcons.Count ? input.GeneralIcons[i] : null;

    // C++: EnsureWindow :1051-1227 + RebuildRows :1228-1305: every widget at (0, 0, 100, 24), the scroll views at (0, 0, 300, 200), the accents' parts
    // at (0, 0, 1, 1); the title, the selected heading and name are made hidden (:1071, :1074, :1082); UpdateCatalogVisibility's visibility (:1306-1371)
    void Creation()
    {
        foreach (var n in new[] { RewardsUINames.Title, RewardsUINames.FightersHeading, RewardsUINames.SelectedHeading, RewardsUINames.Subtitle,
                     RewardsUINames.Status, RewardsUINames.Selected, RewardsUINames.Career, RewardsUINames.GeneralTab, RewardsUINames.ArmourTab,
                     RewardsUINames.WeaponsTab, RewardsUINames.BlueprintsHeading, RewardsUINames.SuppliesHeading, RewardsUINames.WeaponHeading,
                     RewardsUINames.WeaponDetails, RewardsUINames.WeaponBuy, RewardsUINames.Close })
            Set(n, 0, 0, 100, 24);
        foreach (var s in new[] { profile, fighter, detail }) result.Rects[s.Name] = s.Rect;
        Show(RewardsUINames.Title, false);
        Show(RewardsUINames.SelectedHeading, false);
        Show(RewardsUINames.Selected, false);
        foreach (var t in new[] { RewardsUINames.GeneralTab, RewardsUINames.ArmourTab, RewardsUINames.WeaponsTab }) Show(t, v.TabsVisible);
        for (int i = 0; i < v.Cards.Count; ++i)
        {
            var c = v.Cards[i];
            Set(RewardsUINames.Accent(i), 0, 0, 1, 1);
            for (int k = 0; k < 16; ++k) Set(RewardsUINames.Fade(i, k), 0, 0, 1, 1);
            for (int k = 0; k < 4; ++k) Set(RewardsUINames.Border(i, k), 0, 0, 1, 1);
            foreach (var n in new[] { RewardsUINames.Icon(i), RewardsUINames.Name(i), RewardsUINames.Progress(i), RewardsUINames.Claim(i) }) Set(n, 0, 0, 100, 24);
            Show(RewardsUINames.Accent(i), c.Visible);
            Show(RewardsUINames.Name(i), c.Visible);
            Show(RewardsUINames.Progress(i), c.Visible);
            Show(RewardsUINames.Claim(i), c.Visible);
            IconShown(RewardsUINames.Icon(i), c.Visible && PieceIcon(i) is not null);
        }
        for (int i = 0; i < v.General.Count; ++i)
        {
            var g = v.General[i];
            foreach (var n in new[] { RewardsUINames.GeneralIcon(i), RewardsUINames.GeneralName(i), RewardsUINames.GeneralDetails(i), RewardsUINames.GeneralBuy(i) })
                Set(n, 0, 0, 100, 24);
            Show(RewardsUINames.GeneralName(i), g.Visible);
            Show(RewardsUINames.GeneralDetails(i), g.Visible);
            Show(RewardsUINames.GeneralBuy(i), g.Visible);
            IconShown(RewardsUINames.GeneralIcon(i), g.Visible && GeneralIcon(i) is not null);
        }
        Show(RewardsUINames.BlueprintsHeading, v.HeadingsVisible);
        Show(RewardsUINames.SuppliesHeading, v.HeadingsVisible);
        for (int i = 0; i < v.Licences.Count; ++i)
        {
            var l = v.Licences[i];
            foreach (var n in new[] { RewardsUINames.LicenceHeading(i), RewardsUINames.LicenceDetails(i), RewardsUINames.LicenceBuy(i) }) Set(n, 0, 0, 100, 24);
            Show(RewardsUINames.LicenceHeading(i), l.Visible);
            Show(RewardsUINames.LicenceDetails(i), l.Visible);
            Show(RewardsUINames.LicenceBuy(i), l.BuyVisible);
        }
        Show(RewardsUINames.WeaponHeading, v.WeaponLicence.Visible);
        Show(RewardsUINames.WeaponDetails, v.WeaponLicence.Visible);
        Show(RewardsUINames.WeaponBuy, v.WeaponLicence.BuyVisible);
        foreach (var r in v.Rows)
        {
            int s = r.SquadIndex;
            foreach (var n in new[] { RewardsUINames.Row(s), RewardsUINames.RowPortrait(s), RewardsUINames.RowName(s), RewardsUINames.RowMarks(s) }) Set(n, 0, 0, 100, 24);
            Set(RewardsUINames.RowRail(s), 0, 0, 3, 24);
            Show(RewardsUINames.RowRail(s), r.RailVisible);
            Show(RewardsUINames.RowPortrait(s), r.PortraitVisible);
        }
    }

    void IconShown(string icon, bool shown)
    {
        Show(icon, shown);
        if (shown) result.IconsShown.Add(icon); else result.IconsShown.Remove(icon);
    }

    // C++: RewardsUI.cpp:959-970 FitWindowToScreen (90 % of the view, centred)
    void FitWindowToScreen()
    {
        int w = input.ViewWidth * 90 / 100, h = input.ViewHeight * 90 / 100;
        result.Window = new LayoutRect((input.ViewWidth - w) / 2, (input.ViewHeight - h) / 2, w, h);
        result.ClientWidth = Math.Max(0, w - ArenaUILayout.WindowClientMarginW);
        result.ClientHeight = Math.Max(0, h - ArenaUILayout.WindowClientMarginH);
    }

    // C++: RewardsUI.cpp:173-180 WrappedHeight of a Kenshi_WordWrapEmpty (no insets) in the standard font
    int WrappedHeight(string label, string text, int x, int y, int width)
    {
        int w = Math.Max(1, width);
        int height = Math.Max(body, measure.Standard(text, w).Height);
        Set(label, x, y, w, height);
        return height;
    }

    // C++: the same for the fighter row's name (Kenshi_PaintedWordWrapEmpty: the painted font, no insets)
    int WrappedPaintedHeight(string label, string text, int x, int y, int width)
    {
        int w = Math.Max(1, width);
        int height = Math.Max(body, measure.Painted(text, w).Height);
        Set(label, x, y, w, height);
        return height;
    }

    // C++: NativeUI::RowHeight(button, 0)
    int ButtonRowHeight(string caption) => NativeUI.RowHeight(measure.Standard(caption, 0).Height, input.FontHeight, 0);

    // A Kenshi_WordWrap edit box (the subtitle and the status line) laid at (gap, gap, width, body * 2 + 2 gaps) to measure: its insets and the
    // height of its text in the text region (the width less the insets), :981-989
    (int Insets, int TextHeight) WordWrapMeasure(string text, int width)
    {
        int probe = body * 2 + 2 * gap;
        int insets = Math.Min(PgTemplates.WordWrapInsetH, probe);
        int textHeight = measure.Standard(text, Math.Max(1, width - PgTemplates.WordWrapInsetW)).Height;
        return (insets, textHeight);
    }

    // C++: RewardsUI.cpp:971-1050 LayoutWindow
    void LayoutWindow()
    {
        int sizeW = result.ClientWidth, sizeH = result.ClientHeight;
        int width = Math.Max(1, sizeW - 2 * gap);
        int closeHeight = ButtonRowHeight(v.CloseCaption);
        int closeWidth = Math.Min(width, measure.ButtonWidth(v.CloseCaption) + 4 * gap);
        int closeTop = Math.Max(gap, sizeH - gap - closeHeight);
        Set(RewardsUINames.Close, (sizeW - closeWidth) / 2, closeTop, closeWidth, closeHeight);
        var (statusInsets, statusText) = WordWrapMeasure(input.Status, width);
        int statusHeight = Math.Min(statusText + statusInsets, body * 2 + statusInsets);
        int statusTop = Math.Max(gap, closeTop - gap - statusHeight);
        var (subtitleInsets, subtitleText) = WordWrapMeasure(v.Subtitle, width);
        int subtitleHeight = Math.Min(subtitleText + subtitleInsets, body * 2 + subtitleInsets);
        Set(RewardsUINames.Subtitle, gap, gap, width, subtitleHeight);
        int top = gap + subtitleHeight + gap;
        int bottom = statusTop - gap;
        bool stacked = RewardsLayout.ShouldStack(width, body);
        result.Stacked = stacked;
        const int fighterInsets = ArenaUILayout.ScrollInsetH, profileInsets = ArenaUILayout.ScrollInsetH, detailInsets = ArenaUILayout.ScrollInsetH;
        int minimumFighter = body + fighterInsets;
        int minimumProfile = body + profileInsets;
        int minimumDetail = body + detailInsets;
        bool compactHeight = stacked && bottom - top < 6 * body + fighterInsets + profileInsets + detailInsets + 5 * gap;
        result.Compact = compactHeight;
        Show(RewardsUINames.Subtitle, !compactHeight);
        Show(RewardsUINames.FightersHeading, !compactHeight);
        if (compactHeight)
        {
            statusHeight = Math.Min(statusHeight, body + statusInsets);
            statusTop = Math.Max(gap, closeTop - gap - statusHeight);
            top = gap;
            bottom = statusTop - gap;
        }
        Set(RewardsUINames.Status, gap, statusTop, width, statusHeight);
        int fighterWidth = stacked ? width : RewardsLayout.FighterWidth(width, body);
        int detailLeft = stacked ? gap : gap + fighterWidth + 2 * gap;
        int detailWidth = stacked ? width : Math.Max(1, width - fighterWidth - 2 * gap);
        Set(RewardsUINames.FightersHeading, gap, top, fighterWidth, body);
        int detailTop = top;
        if (stacked)
        {
            int headingHeight = compactHeight ? 0 : body + gap;
            int maximumFighter = bottom - top - headingHeight - minimumProfile - minimumDetail - 2 * gap;
            int fighterHeight = Math.Max(minimumFighter, Math.Min(Math.Max(body * 2, (bottom - top) / 4), maximumFighter));
            SetScroll(fighter, gap, top + headingHeight, fighterWidth, fighterHeight);
            detailTop = top + headingHeight + gap + fighterHeight;
        }
        else
            SetScroll(fighter, gap, top + body + gap, fighterWidth, Math.Max(1, bottom - top - body - gap));
        int detailBottom = bottom;
        int profileLimit = Math.Max(minimumProfile, Math.Min((detailBottom - detailTop - gap) / 2, detailBottom - detailTop - gap - minimumDetail));
        SetScroll(profile, detailLeft, detailTop, detailWidth, profileLimit);
        LayoutProfile();
        int profileHeight = Math.Min(profileLimit, profileContent + Math.Max(0, profile.Rect.H - profile.ViewH));
        SetScroll(profile, profile.Rect.X, profile.Rect.Y, detailWidth, Math.Max(1, profileHeight));
        LayoutProfile();
        detailTop += Math.Max(1, profileHeight) + gap;
        SetScroll(detail, detailLeft, detailTop, detailWidth, Math.Max(1, detailBottom - detailTop));
        LayoutFighters();
        LayoutDetails();
    }

    // C++: RewardsUI.cpp:921-958 LayoutProfile
    void LayoutProfile()
    {
        for (int pass = 0; pass < 2; ++pass)
        {
            int viewW = profile.ViewW, viewH = profile.ViewH;
            int width = Math.Max(1, viewW - 2 * gap);
            int y = gap;
            y += WrappedHeight(RewardsUINames.Career, v.Career, gap, y, width) + gap;
            if (v.TabsVisible)
            {
                string[] captions = [RewardsUiTexts.TabGeneral, RewardsUiTexts.TabArmour, RewardsUiTexts.TabWeapons];
                int tabWidth = 0, tabHeight = 0;
                foreach (var caption in captions)
                {
                    tabWidth = Math.Max(tabWidth, measure.ButtonWidth(caption) + 4 * gap);
                    tabHeight = Math.Max(tabHeight, ButtonRowHeight(caption));
                }
                int columns = Math.Max(1, Math.Min(3, (width + gap) / (tabWidth + gap)));
                int cellWidth = Math.Max(1, (width - (columns - 1) * gap) / columns);
                for (int i = 0; i < 3; ++i)
                    Set(RewardsUINames.Tab((RewardsCatalogTab)i), gap + (i % columns) * (cellWidth + gap), y + (i / columns) * (tabHeight + gap), cellWidth, tabHeight);
                y += ((3 + columns - 1) / columns) * (tabHeight + gap);
            }
            profileContent = y;
            SetCanvas(profile, Math.Max(1, viewW), Math.Max(y, viewH));
            if (profile.ViewW == viewW) break;
        }
    }

    // C++: RewardsUI.cpp:595-629 LayoutFighters (coordinates of a row's parts are relative to the row button)
    void LayoutFighters()
    {
        int image = Math.Max(40, body * 3);
        for (int pass = 0; pass < 2; ++pass)
        {
            int viewW = fighter.ViewW, viewH = fighter.ViewH;
            int width = Math.Max(1, viewW - 2 * gap);
            int y = gap;
            foreach (var row in v.Rows)
            {
                int s = row.SquadIndex;
                int textX = image + 2 * gap;
                int textWidth = Math.Max(1, width - textX - gap);
                int nameHeight = WrappedPaintedHeight(RewardsUINames.RowName(s), row.Name, textX, gap, textWidth);
                int marksHeight = Math.Max(body, measure.Painted(row.Marks, 0).Height);
                Set(RewardsUINames.RowMarks(s), textX, gap + nameHeight + gap, textWidth, marksHeight);
                int height = Math.Max(image, nameHeight + gap + marksHeight) + 2 * gap;
                Set(RewardsUINames.Row(s), gap, y, width, height);
                Set(RewardsUINames.RowPortrait(s), gap, gap, image, image);
                Set(RewardsUINames.RowRail(s), 0, 0, Math.Max(2, gap / 2), height);
                y += height + gap;
            }
            fighterContent = y;
            SetCanvas(fighter, Math.Max(1, viewW), Math.Max(y, viewH));
            if (fighter.ViewW == viewW) break;
        }
    }

    static bool IsChest(ArenaRewards.Piece p) => p is ArenaRewards.Piece.PitfighterHarness or ArenaRewards.Piece.RetainerJacket
        or ArenaRewards.Piece.EnforcerChest or ArenaRewards.Piece.PlateArmour;

    static bool IsBoots(ArenaRewards.Piece p) => p is ArenaRewards.Piece.RetainerBoots or ArenaRewards.Piece.EnforcerBoots or ArenaRewards.Piece.PlateBoots;

    // C++: RewardsUI.cpp:312-326 LayoutIcon: the area; a weapon's texture fitted into it keeping its aspect
    void LayoutIcon(int i, LayoutRect area)
    {
        var piece = (ArenaRewards.Piece)i;
        var r = area;
        if (ArenaRewards.IsWeapon(piece) && PieceIcon(i) is { W: > 0, H: > 0 } t)
            r = Fit(area, t);
        result.Rects[RewardsUINames.Icon(i)] = r;
    }

    // C++ :316-325 / :331-345: the texture scaled by the smaller of the two scales, rounded, centred
    static LayoutRect Fit(LayoutRect area, (int W, int H) t)
    {
        float widthScale = (float)area.W / t.W;
        float heightScale = (float)area.H / t.H;
        float scale = widthScale < heightScale ? widthScale : heightScale;
        int width = Math.Max(1, (int)(t.W * scale + .5f));
        int height = Math.Max(1, (int)(t.H * scale + .5f));
        return new LayoutRect(area.X + (area.W - width) / 2, area.Y + (area.H - height) / 2, width, height);
    }

    // C++: RewardsUI.cpp:327-346 LayoutGeneralIcon
    void LayoutGeneralIcon(int i, LayoutRect area) =>
        result.Rects[RewardsUINames.GeneralIcon(i)] = GeneralIcon(i) is { W: > 0, H: > 0 } t ? Fit(area, t) : area;

    // C++: RewardsUI.cpp:277-311 LayoutRewardAccent (the fades and borders relative to the accent)
    void LayoutRewardAccent(int i, bool visible, int left, int top, int width, int height)
    {
        Show(RewardsUINames.Accent(i), visible);
        if (!visible) return;
        Set(RewardsUINames.Accent(i), left, top, width, height);
        for (int k = 0; k < 16; ++k)
        {
            int start = width * k / 16;
            int end = width * (k + 1) / 16;
            Set(RewardsUINames.Fade(i, k), start, 0, Math.Max(1, end - start), height);
        }
        int rail = Math.Max(3, gap / 2);
        Set(RewardsUINames.Border(i, 0), 0, 0, rail, height);
        Set(RewardsUINames.Border(i, 1), rail, 0, Math.Max(1, width - rail), 1);
        Set(RewardsUINames.Border(i, 2), rail, height - 1, Math.Max(1, width - rail), 1);
        Set(RewardsUINames.Border(i, 3), width - 1, 0, 1, height);
    }

    // C++: RewardsUI.cpp:630-680 LayoutRewardCard
    int LayoutRewardCard(int i, int left, int top, int width, int preferredHeight)
    {
        var card = v.Cards[i];
        var piece = card.Piece;
        int actionHeight = ButtonRowHeight(card.ClaimCaption);
        int minimumHeight = Math.Max(body * 7, actionHeight + body * 3 + 4 * gap);
        int height = Math.Max(minimumHeight, preferredHeight);
        bool weapon = ArenaRewards.IsWeapon(piece);
        int imageHeight = Math.Min(height - actionHeight - 3 * gap, Math.Max(body * 5, weapon ? height * 3 / 4 : height / 2));
        bool chest = IsChest(piece);
        bool boots = IsBoots(piece);
        int armourIconSize = chest ? imageHeight * 5 / 4 : (boots ? imageHeight * 7 / 8 : imageHeight);
        int imageWidth = weapon ? Math.Min(width * 45 / 100, imageHeight * 7 / 2) : armourIconSize;
        int actionWidth = Math.Min(width, measure.ButtonWidth(card.ClaimCaption) + 4 * gap);
        int contentLeft = left + 2 * gap;
        int contentTop = top + gap;
        int textX = contentLeft + (weapon ? imageWidth : Math.Max(imageHeight, imageWidth)) + gap;
        int textWidth = Math.Max(1, left + width - gap - textX);
        int textBottom = contentTop + WrappedHeight(RewardsUINames.Name(i), card.Name, textX, contentTop, textWidth);
        textBottom += gap + WrappedHeight(RewardsUINames.Progress(i), card.Progress, textX, textBottom + gap, textWidth);
        var iconArea = new LayoutRect(contentLeft + (boots ? (imageHeight - imageWidth) / 2 : 0),
            contentTop + (boots ? (imageHeight - armourIconSize) / 2 : 0), imageWidth, Math.Max(1, weapon ? imageHeight : armourIconSize));
        LayoutIcon(i, iconArea);
        Set(RewardsUINames.Claim(i), left + width - actionWidth - gap, top + height - actionHeight - gap, actionWidth, actionHeight);
        LayoutRewardAccent(i, card.Visible, left, top, width, height);
        return top + height + gap;
    }

    // C++: RewardsUI.cpp:681-738 LayoutGeneralCard
    int LayoutGeneralCard(int i, int left, int top, int width, int preferredHeight)
    {
        var card = v.General[i];
        int actionHeight = ButtonRowHeight(card.BuyCaption);
        int actionWidth = Math.Min(width, measure.ButtonWidth(card.BuyCaption) + 4 * gap);
        int iconSize = Math.Max(48, body * 4);
        int rowTop = top + gap;
        int textX = left + gap + iconSize + gap;
        bool fixedHeight = preferredHeight > 0;
        bool stackAction = !fixedHeight && width < iconSize + actionWidth + body * 14 + 3 * gap;
        int textWidth = Math.Max(1, width - iconSize - 2 * gap - (stackAction ? 0 : actionWidth + gap));
        int nameHeight = WrappedHeight(RewardsUINames.GeneralName(i), card.Name, textX, rowTop, textWidth);
        int detailsTop = rowTop + nameHeight + gap;
        int detailsHeight = WrappedHeight(RewardsUINames.GeneralDetails(i), card.Details, textX, detailsTop, textWidth);
        int contentHeight = nameHeight + gap + detailsHeight;
        int height = fixedHeight ? preferredHeight : Math.Max(iconSize, contentHeight) + 2 * gap;
        LayoutGeneralIcon(i, new LayoutRect(left + gap, rowTop, iconSize, iconSize));
        if (fixedHeight)
        {
            height = Math.Max(height, actionHeight + 3 * gap);
            Set(RewardsUINames.GeneralBuy(i), left + width - actionWidth - gap, top + height - actionHeight - gap, actionWidth, actionHeight);
        }
        else if (stackAction)
        {
            Set(RewardsUINames.GeneralBuy(i), textX, rowTop + Math.Max(iconSize, contentHeight) + gap, actionWidth, actionHeight);
            height += gap + actionHeight;
        }
        else
        {
            height = Math.Max(height, actionHeight + 2 * gap);
            Set(RewardsUINames.GeneralBuy(i), left + width - actionWidth - gap, top + (height - actionHeight) / 2, actionWidth, actionHeight);
        }
        return top + height + gap;
    }

    // C++: RewardsUI.cpp:739-920 LayoutDetails
    void LayoutDetails()
    {
        for (int pass = 0; pass < 2; ++pass)
        {
            int viewW = detail.ViewW, viewH = detail.ViewH;
            int width = Math.Max(1, viewW - 2 * gap);
            int y = gap;

            if (v.Tab == RewardsCatalogTab.General)
            {
                y += WrappedHeight(RewardsUINames.BlueprintsHeading, RewardsUiTexts.BlueprintsHeading, gap, y, width) + gap;
                for (int i = (int)ArenaRewards.GeneralItem.ArenaBuildingsI; i <= (int)ArenaRewards.GeneralItem.ArenaBuildingsII; ++i)
                {
                    if (i >= v.General.Count || !v.General[i].Visible) continue;
                    y = LayoutGeneralCard(i, gap, y, width, 0);
                }
                y += WrappedHeight(RewardsUINames.SuppliesHeading, RewardsUiTexts.SuppliesHeading, gap, y, width) + gap;
                var order = RewardsUILogic<TId>.SupplyDisplayOrder;
                int supplyCount = 0;
                foreach (var item in order)
                    if ((int)item < v.General.Count && v.General[(int)item].Visible) ++supplyCount;
                int columns = RewardsLayout.FightSupplyColumns(supplyCount);
                int cellWidth = Math.Max(1, (width - (columns - 1) * gap) / columns);
                int cardHeight = Math.Max(body * 8, 120);
                int visibleIndex = 0;
                foreach (var item in order)
                {
                    int i = (int)item;
                    if (i >= v.General.Count || !v.General[i].Visible) continue;
                    int column = visibleIndex % columns, row = visibleIndex / columns;
                    LayoutGeneralCard(i, gap + column * (cellWidth + gap), y + row * (cardHeight + gap), cellWidth, cardHeight);
                    ++visibleIndex;
                }
                if (visibleIndex > 0)
                {
                    int rows = (visibleIndex + columns - 1) / columns;
                    y += rows * (cardHeight + gap);
                }
            }
            else if (v.Tab == RewardsCatalogTab.Armour)
            {
                for (int r = 0; r < (int)ArenaRewards.ArmourRow.Count && r < v.Licences.Count; ++r)
                {
                    var armourRow = (ArenaRewards.ArmourRow)r;
                    var licence = v.Licences[r];
                    y += WrappedHeight(RewardsUINames.LicenceHeading(r), licence.Heading, gap, y, width) + gap;
                    y += WrappedHeight(RewardsUINames.LicenceDetails(r), licence.Details, gap, y, width) + gap;
                    if (!licence.Unlocked)
                    {
                        int actionHeight = ButtonRowHeight(licence.BuyCaption);
                        int actionWidth = Math.Min(width, measure.ButtonWidth(licence.BuyCaption) + 4 * gap);
                        Set(RewardsUINames.LicenceBuy(r), gap, y, actionWidth, actionHeight);
                        y += actionHeight + 2 * gap;
                        int iconSize = Math.Max(32, body * 2);
                        int iconX = gap;
                        foreach (var p in RewardsUILogic<TId>.ArmourDisplayOrder)
                        {
                            if (ArenaRewards.IsWeapon(p) || ArenaRewards.RowFor(p) != armourRow) continue;
                            int i = (int)p;
                            LayoutIcon(i, new LayoutRect(iconX, y, iconSize, iconSize));
                            IconShown(RewardsUINames.Icon(i), PieceIcon(i) is not null);   // :828 manifest.icon->setVisible(manifest.iconLoaded)
                            iconX += iconSize + gap;
                        }
                        y += iconSize + 2 * gap;
                    }
                    int columns = RewardsLayout.GridColumns(width, body * 15, gap, 4);
                    int cellWidth = Math.Max(1, (width - (columns - 1) * gap) / columns);
                    int cardHeight = Math.Max(body * 8, 120);
                    int visibleIndex = 0;
                    foreach (var p in RewardsUILogic<TId>.ArmourDisplayOrder)
                    {
                        int i = (int)p;
                        if (i >= v.Cards.Count || !v.Cards[i].Visible || ArenaRewards.RowFor(p) != armourRow) continue;
                        int column = visibleIndex % columns, row = visibleIndex / columns;
                        LayoutRewardCard(i, gap + column * (cellWidth + gap), y + row * (cardHeight + gap), cellWidth, cardHeight);
                        ++visibleIndex;
                    }
                    if (visibleIndex > 0)
                    {
                        int rows = (visibleIndex + columns - 1) / columns;
                        y += rows * (cardHeight + gap);
                    }
                    y += gap;
                }
            }
            else
            {
                var wl = v.WeaponLicence;
                y += WrappedHeight(RewardsUINames.WeaponHeading, wl.Heading, gap, y, width) + gap;
                y += WrappedHeight(RewardsUINames.WeaponDetails, wl.Details, gap, y, width) + gap;
                if (!wl.Unlocked)
                {
                    int actionHeight = ButtonRowHeight(wl.BuyCaption);
                    int actionWidth = Math.Min(width, measure.ButtonWidth(wl.BuyCaption) + 4 * gap);
                    Set(RewardsUINames.WeaponBuy, gap, y, actionWidth, actionHeight);
                    y += actionHeight + 2 * gap;
                }
                int columns = RewardsLayout.GridColumns(width, body * 20, gap, 2);
                int cellWidth = Math.Max(1, (width - (columns - 1) * gap) / columns);
                int minimumCardHeight = Math.Max(body * 7, ButtonRowHeight(v.Cards.Count > 0 ? v.Cards[0].ClaimCaption : RewardsUiTexts.Claim) + body * 3 + 4 * gap);
                int cardHeight = columns == 2
                    ? Math.Max(minimumCardHeight, RewardsLayout.CompactRowHeight(RewardsLayout.FillRowHeight(viewH, 2, gap, body * 9), body * 9) * 3 / 4)
                    : Math.Max(body * 9, 140);
                int visibleIndex = 0;
                for (int i = 0; i < v.Cards.Count; ++i)
                {
                    if (!v.Cards[i].Visible) continue;
                    int column = visibleIndex % columns, row = visibleIndex / columns;
                    LayoutRewardCard(i, gap + column * (cellWidth + gap), y + row * (cardHeight + gap), cellWidth, cardHeight);
                    ++visibleIndex;
                }
                int rowCount = visibleIndex > 0 ? (visibleIndex + columns - 1) / columns : 0;
                y += rowCount * (cardHeight + gap);
            }

            detailContent = y;
            SetCanvas(detail, Math.Max(1, viewW), Math.Max(y, viewH));
            if (detail.ViewW == viewW) break;
        }
    }
}
