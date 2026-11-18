// Port of upstream/ProvingGrounds/plugin/src/TownBookieResultUI.h/.cpp (Proving Grounds by Shutori, GPLv3). Task 8.1a: the sim half (the model
// pg.bookie.result, src/Town/TownBookieUISim.cs over Core/Glue/TownBookieLogic.cs). Task 8.1b (TW §2.6; CAP 7.0-7.4): the window half
// TownBookieResultScreen: the layout built call by call from EnsureWindow (:113-160) and CreateCard (:94-112) with LayoutBuilder / NativeUI, every
// rectangle from Layout / LayoutCard / Wrap and Show's decisions (Core/Glue/TownBookieResultUILayout.cs, tested), the outcome colours, the portraits
// and the Close button. Window thread only. Line numbers are TownBookieResultUI.cpp's. Meitou 0.17.3 (tag v0.17.3, fa34a1b).
//
// Engine calls of the C++ window and what replaces them:
//   createWidgetReal / createWidget (EnsureWindow, CreateCard, Accent)  -> LayoutBuilder XML (Build) -> ScreenHost (Register once; one shape: the eight
//                                                                        cards are made once and hidden past the counts)
//   setCoord / setSize (Layout, LayoutCard, Wrap, SetAccent)            -> TownBookieResultUILayout's rectangles, bound as fractions of their parent
//   window->setCoord (Layout :229-230)                                  -> the root's bound screen rectangle (fitted to the content, centred)
//   setCaption (Show :296, :310, BindCard :163)                         -> UiBindings Text from the model (the headings made once per model)
//   setVisible (Show :311-315, BindCard :168-169, SetAccent :79)        -> Hide from TownBookieResultUILayout's visibility; a portrait with no free cell
//                                                                        hidden and its "?" shown (7.6's path)
//   setColour / setAlpha (SetAccent :74-80) / setTextColour (Show :297, -> Properties Colour / Alpha / TextColour bound per plan; an EditBox's text colour
//     Layout :251, :273)                                                 also on its template part (ScreenBinding.EditTextColour)
//   PortraitManager::setImageWidget (BindCard :167)                     -> Portraits.Bind (spike S7)
//   upLayerItem (Show :319)                                             -> the framework opens the window above the others
//   CloseClicked (:89) / eventWindowButtonPressed "close" (:90-93),     -> BookieClose with the serial the window was drawn under / the framework's Closed
//     Escape                                                             report (both close only that window, TownBookieUISim.Apply)
//   getTextSize / getFontHeight / NativeUI::Spacing                     -> RewardsTextMeasure over src/Ui/TextMeasure.cs / ScreenHost.FontHeight
//   RenderManager::getViewSize                                          -> ScreenHost.ViewSize
//   the catch block of EnsureWindow (:153-158)                          -> PgScreen.Failed: the C++ error line and TownBookie.cpp:431's "show failed"
using System.Globalization;
using Meitou.Data.Ui;
using Meitou.Simulation;
using ProvingGrounds.Core;
using ProvingGrounds.Core.Glue;
using ProvingGrounds.Engine;
using ProvingGrounds.Town;
using static ProvingGrounds.Ui.TownWindowParts;
using N = ProvingGrounds.Core.Glue.TownBookieResultUINames;
using L = ProvingGrounds.Core.Glue.TownBookieResultUILayout<Meitou.Simulation.CharacterId>;

namespace ProvingGrounds.Ui;

/// <summary>
/// The bookie's wager results window <c>pg.bookie.result</c> (task 8.1b), declared to the window framework. One shape; everything Show / Layout set
/// (captions, rectangles, visibility, colours, portraits, the window rectangle) is bound per frame from a plan made once per model, view and font.
/// </summary>
static class TownBookieResultScreen
{
    static readonly TextMeasure text = new();
    static readonly RewardsTextMeasure measure = new(text);
    // the widths the title and payout labels were left with by the last layout of the shown window (100 at creation; the C++ widget keeps them)
    static int titleWidth = 100, payoutWidth = 100;

    static TownBookieResultUiView<CharacterId>? Of(PgUiModel? m) => m is TownBookieResultUiModel t ? t.View : null;

    internal static readonly PgScreen Declaration = new()
    {
        Id = TownBookieUISim.ResultScreenId,
        ButtonEvent = N.ButtonEvent,   // task 4.4b: the Close button eventMouseButtonClick (:139)
        ShapeKey = m => Of(m) is null ? "bookie.result/none" : "bookie.result",
        Layout = Build,
        Bind = Bind,
        Click = Click,
        WhileClosed = ResetWindowState,
        NewWorld = _ => { ResetWindowState(); titleWidth = payoutWidth = 100; },   // TownBookie.cpp:409 Destroy (:284-291): the window made again
        Portraits = m => Of(m) is { } v ? FacesOf(m!, v) : [],
        Failed = Failed,
        CheckModels = CheckModels,
    };

    static void ResetWindowState()
    {
        plan = null;
        planKey = default;
        current = default;
    }

    static (PgUiModel? Model, IReadOnlyList<CharacterId> Faces) facesFor;

    /// <summary>The portraits a model asks for (BindCard :164-167: each shown card with a valid actor), made once per model.</summary>
    static IReadOnlyList<CharacterId> FacesOf(PgUiModel m, TownBookieResultUiView<CharacterId> v)
    {
        if (ReferenceEquals(facesFor.Model, m)) return facesFor.Faces;
        var list = new List<CharacterId>();
        for (int column = 0; column < 2; ++column)
        {
            var cards = v.Cards[L.DisplaySide(v.View.WinningSide, column)];
            for (int i = 0; i < cards.Count && i < L.Members; ++i) if (cards[i].PortraitKey.Length > 0) list.Add(cards[i].Actor);
        }
        facesFor = (m, list);
        return list;
    }

    static (int W, int H) View()
    {
        var (w, h) = ScreenHost.ViewSize;
        return w <= 1 || h <= 1 ? (1920, 1080) : (w, h);
    }

    static BookieResultLayout Compute(TownBookieResultUiView<CharacterId> v, int font)
    {
        text.UseCurrent();
        var (w, h) = View();
        return L.Compute(v, new BookieResultLayoutInput { ViewWidth = w, ViewHeight = h, FontHeight = font, TitleWidthBefore = titleWidth, PayoutWidthBefore = payoutWidth },
            measure);
    }

    static string F(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);
    static string C((float R, float G, float B) c) => F(c.R) + " " + F(c.G) + " " + F(c.B);

    // C++: TownBookieResultUI.cpp:113-160 EnsureWindow (+ CreateCard :94-112, Accent :63-73), in creation order; positions from the layout
    static LayoutBuilder Build(PgUiModel? m, int font)
    {
        var v = Of(m) ?? TownBookieResultUiView<CharacterId>.Empty;
        var l = Compute(v, font).Layout;
        current = default;
        var b = new LayoutBuilder();
        LayoutRect R(string name) => l.Rects.TryGetValue(name, out var r) ? r : new LayoutRect(0, 0, 100, 24);
        LayoutBuilder Open(string type, string skin, string name)
        {
            var r = R(name);
            return b.Open(type, skin, r.X, r.Y, r.W, r.H, name);
        }
        LayoutBuilder Wrapped(string name, string caption) => Open("EditBox", "Kenshi_WordWrapEmpty", name).Prop("Caption", caption);   // NativeUI::WrappedLabel
        LayoutBuilder Label(string name, string caption) => Open("TextBox", "Kenshi_TextboxStandardText", name).Prop("Caption", caption); // NativeUI::Label
        LayoutBuilder Accent(string name) => Open("Widget", "WhiteSkin", name).Prop("NeedMouse", "false");                               // :67-69

        // :120-125 createWidgetReal<Window>("Kenshi_WindowCX", .18f, .15f, .64f, .70f, Align::Center, "Window", "PG_BookieWagerResults"); setCaption
        b.OpenReal("Window", "Kenshi_WindowCX", .18f, .15f, .64f, .70f, N.Window, "Center", "Window").Prop("Caption", "Scratch Bookie - Wager Results");
        Label(N.Measure, "WAGER RESULT").Prop("Visible", "false").Close();                               // :128-129
        Wrapped(N.Title, v.View.Title).Prop("TextAlign", "Center").Close();                              // :130, :133 (Center | VCenter)
        Wrapped(N.Context, v.View.Context).Prop("TextAlign", "HCenter Top").Close();                     // :131, :134
        Wrapped(N.Payout, v.View.Payout).Prop("TextAlign", "HCenter Top").Close();                       // :132, :135
        Accent(N.RailLeft).Close();                                                                      // :136
        Accent(N.RailRight).Close();                                                                     // :137
        Open("Button", "Kenshi_Button1", N.Close).Prop("Caption", "Close").Close();                      // :138
        for (int column = 0; column < 2; ++column)                                                       // :140-150
        {
            int side = L.DisplaySide(v.View.WinningSide, column);
            Open("Widget", "PanelEmpty", N.Column(column));
            Wrapped(N.Heading(column), L.Heading(v.View, column)).Prop("TextAlign", "Center").Close();
            Accent(N.Rail(column)).Close();
            for (int slot = 0; slot < L.Members; ++slot)                                                 // CreateCard :94-112
            {
                var card = slot < v.Cards[side].Count ? v.Cards[side][slot] : null;
                Open("Widget", "PanelEmpty", N.Card(column, slot));
                Open("ImageBox", "ImageBox", N.Portrait(column, slot)).Close();
                Label(N.Placeholder(column, slot), "?").Prop("TextAlign", "Center").Close();
                Wrapped(N.Name(column, slot), card is null ? "" : L.NameCaption(card.View)).Prop("TextAlign", "HCenter Top").Close();
                for (int e = 0; e < 4; ++e) Accent(N.Border(column, slot, e)).Close();
                b.Close();   // card
            }
            b.Close();   // column
        }
        b.Close();   // window
        return b;
    }

    /// <summary>EnsureWindow failed (:153-158): the C++ error line, then TownBookie.cpp:431's debug line (its Show returned false).</summary>
    static void Failed(string what, Exception e)
    {
        ModLog.Error("Proving Grounds: wager results UI creation failed: " + e.Message);
        PGLog.Debug(TownBookieLogic<CharacterId>.Text.ResultShowFailed);
    }

    // ---------- bind ----------

    sealed class Plan
    {
        public UiRect Root;
        public readonly List<(string Name, UiRect Fraction)> Sizes = [];
        public readonly List<(string Name, string Text)> Texts = [];
        public readonly List<string> Hidden = [];
        public readonly List<(string Name, IReadOnlyDictionary<string, string> Props)> Props = [], EditColours = [];
        public readonly List<(string Image, CharacterId Who, string Placeholder)> Faces = [];
    }

    static Plan? plan;
    static (PgUiModel? Model, PgWindowLayout? Layout) planKey;
    static (PgUiModel? Model, (int W, int H) View, int Font, PgWindowLayout? Layout) current;

    /// <summary>C++ Show's Layout (:317) for the model shown now; the widths it leaves the title and payout with are the next layout's.</summary>
    static PgWindowLayout LayoutFor(PgUiModel? m, TownBookieResultUiView<CharacterId> v)
    {
        var view = View();
        int font = ScreenHost.FontHeight;
        if (ReferenceEquals(current.Model, m) && current.View == view && current.Font == font && current.Layout is { } l) return l;
        var r = Compute(v, font);
        l = r.Layout;
        current = (m, view, font, l);
        titleWidth = r.TitleWidth;
        payoutWidth = r.PayoutWidth;
        return l;
    }

    static string ParentOf(string name)
    {
        for (int column = 0; column < 2; ++column)
        {
            string col = N.Column(column);
            if (name == col) return "";
            for (int slot = 0; slot < L.Members; ++slot)
            {
                string card = N.Card(column, slot);
                if (name == card) return col;
                if (name.StartsWith(card, StringComparison.Ordinal)) return card;
            }
            if (name.StartsWith(col, StringComparison.Ordinal)) return col;
        }
        return "";
    }

    static readonly Dictionary<(string Colour, string Alpha), IReadOnlyDictionary<string, string>> accents = [];
    static readonly Dictionary<string, IReadOnlyDictionary<string, string>> textColours = [];

    // SetAccent :74-80: colour, alpha, visible
    static IReadOnlyDictionary<string, string> AccentProps((float R, float G, float B) c, float alpha)
    {
        var key = (C(c), F(alpha));
        if (!accents.TryGetValue(key, out var d))
            accents[key] = d = new Dictionary<string, string>(StringComparer.Ordinal) { ["Colour"] = key.Item1, ["Alpha"] = key.Item2, ["Visible"] = "true" };
        return d;
    }

    internal static IReadOnlyDictionary<string, string> TextColour((float R, float G, float B) c)
    {
        string k = C(c);
        if (!textColours.TryGetValue(k, out var d)) textColours[k] = d = new Dictionary<string, string>(StringComparer.Ordinal) { ["TextColour"] = k };
        return d;
    }

    static Plan MakePlan(TownBookieResultUiView<CharacterId> v, PgWindowLayout l)
    {
        var view = v.View;
        var p = new Plan { Root = new UiRect(l.Window.X, l.Window.Y, l.Window.W, l.Window.H) };
        foreach (var (name, rect) in l.Rects) p.Sizes.Add((name, Fraction(rect, ParentSize(l, ParentOf(name)))));
        foreach (var n in l.Hidden) p.Hidden.Add(n);
        var outcome = L.OutcomeColour(view.Tone);
        // Show :296-297, Layout :238-239, :273
        p.Texts.Add((N.Title, view.Title));
        p.Texts.Add((N.Context, view.Context));
        p.Texts.Add((N.Payout, view.Payout));
        p.EditColours.Add((N.Title, TextColour(outcome)));
        p.EditColours.Add((N.Payout, TextColour(outcome)));
        p.Props.Add((N.RailLeft, AccentProps(outcome, .85f)));
        p.Props.Add((N.RailRight, AccentProps(outcome, .85f)));
        for (int column = 0; column < 2; ++column)
        {
            int side = L.DisplaySide(view.WinningSide, column);
            var colour = L.ColumnColour(view.WinningSide, column);
            p.Texts.Add((N.Heading(column), L.Heading(view, column)));       // Show :305-310
            p.EditColours.Add((N.Heading(column), TextColour(colour)));     // Layout :251
            p.Props.Add((N.Rail(column), AccentProps(colour, .8f)));        // :253-254
            var cards = v.Cards[side];
            for (int slot = 0; slot < cards.Count && slot < L.Members; ++slot)
            {
                p.Texts.Add((N.Name(column, slot), L.NameCaption(cards[slot].View)));   // BindCard :163
                for (int e = 0; e < 4; ++e) p.Props.Add((N.Border(column, slot, e), AccentProps(colour, 1f)));   // LayoutCard :187
                if (cards[slot].PortraitKey.Length > 0) p.Faces.Add((N.Portrait(column, slot), cards[slot].Actor, N.Placeholder(column, slot)));
            }
        }
        return p;
    }

    static void Bind(PgUiModel? m, ScreenBinding b)
    {
        if (Of(m) is not { } v) return;
        var size = ScreenHost.ViewSize;
        if (size.Width <= 1 || size.Height <= 1) return;
        var l = LayoutFor(m, v);
        var key = (m, (PgWindowLayout?)l);
        if (plan is null || !planKey.Equals(key))
        {
            plan = MakePlan(v, l);
            planKey = key;
        }
        foreach (var (n, t) in plan.Texts) b.Text(n, t);
        foreach (var (n, f) in plan.Sizes) b.Size(n, f);
        foreach (var n in plan.Hidden) b.Hide(n);
        foreach (var (n, props) in plan.Props) b.Props(n, props);
        foreach (var (n, props) in plan.EditColours) b.EditTextColour(n, props);
        foreach (var (image, who, placeholder) in plan.Faces)
            if (!b.Portrait(image, who))   // no free portrait cell: the "?" instead (BindCard :168-169's rule, 7.6's path)
            {
                b.Hide(image);
                b.Bindings.Hidden.Remove(placeholder);
                b.Props(placeholder, Shown);
            }
        b.Place(N.Window, plan.Root);
    }

    // ---------- clicks ----------

    // C++ CloseClicked (:89): TownBookie::Close for the window drawn under this serial (a late click never closes a newer window)
    static PgCommand? Click(PgUiModel? m, UiClickData c) =>
        c.Kind == UiEventKind.Click && c.Widget == N.Close && Of(m) is not null ? new BookieClose(m!.RequestSerial) { Tick = 0 } : null;

    // ---------- TEST ONLY: pg window rect pg.bookie.result <widget> ----------

    /// <summary>TEST ONLY (task 8.1b review): a widget's rectangle in the layout the shown window was last bound with (relative to its parent; the
    /// window's direct children to the client) and the client's size, so a scenario can tell a Close the C++'s layout puts below the client (a
    /// two-row result at a small view) from a missing one. Null with an error when the window was not laid out or has no such widget.</summary>
    internal static (LayoutRect Rect, int ClientWidth, int ClientHeight)? LaidOutForTest(string widget, out string? error)
    {
        error = null;
        if (current.Layout is not { } l) { error = "the result window has not been laid out"; return null; }
        if (!l.Rects.TryGetValue(widget, out var r)) { error = "no widget " + widget + " in the result window's layout"; return null; }
        return (r, l.ClientWidth, l.ClientHeight);
    }

    // ---------- PgCheck ui ----------

    internal static void ResetForCheck()
    {
        ResetWindowState();
        titleWidth = payoutWidth = 100;
        facesFor = default;
    }

    internal static IReadOnlyList<string> FacesForCheck(PgUiModel? m)
    {
        var v = Of(m) ?? TownBookieResultUiView<CharacterId>.Empty;
        return [.. MakePlan(v, Compute(v, NativeUI.DefaultFontHeight).Layout).Faces.Select(f => f.Image)];
    }

    static TownBookieResultUiModel CheckModel(int a, int b, int winning, int selected, int tone, string title, string context, string payout)
    {
        var view = new Core.TownBookie.ResultView { Title = title, Context = context, Payout = payout, WinningSide = winning, SelectedSide = selected, Tone = tone };
        string[] names = ["Set", "Bark", "Zan", "Logan", "Sing", "Little", "Kat", "Rook"];
        string[] styles = ["Sabre", "Martial arts", "Sabre", "Sabre", "Sabre", "Katana", "Heavy weapon", "Polearm"];
        var cards = new IReadOnlyList<BookieFighterCard<CharacterId>>[2];
        cards[0] = [.. Enumerable.Range(0, a).Select(i => new BookieFighterCard<CharacterId>(new CharacterId(10 + i, 0), i == 1 ? "" : "k" + i,
            new Core.TownBookie.FighterView { Name = names[i], Style = styles[i] }))];
        cards[1] = [.. Enumerable.Range(0, b).Select(i => new BookieFighterCard<CharacterId>(new CharacterId(20 + i, 0), "k" + (20 + i),
            new Core.TownBookie.FighterView { Name = names[4 + i], Style = styles[4 + i] }))];
        return new TownBookieResultUiModel(new TownBookieResultUiView<CharacterId>(true, 2, 0, view, cards, "check")) { RequestSerial = 2, RequestOpen = true };
    }

    static IReadOnlyList<PgUiModel?> CheckModels() =>
    [
        null,
        CheckModel(4, 3, 1, 1, 1, "WAGER WON", "Team B claimed the arena. Your pick delivered.",
            "STAKE  4500 CATS    RETURNED  8974 CATS\nNET WIN  +4474 CATS"),                                // ws-07: Team B won, the player's pick
        CheckModel(1, 2, 0, 1, 2, "WAGER LOST", "Team A claimed the arena. Your pick fell.",
            "STAKE  500 CATS    RETURNED  0 CATS\nNET LOSS  -500 CATS"),                                     // a loss, 1v2 (a card without a key: "?")
        CheckModel(2, 2, -1, 0, 0, "WAGER REFUNDED", "Bout cancelled: nobody in town.",
            "STAKE  500 CATS    RETURNED  500 CATS\nNET  0 CATS"),                                           // a refund: no winner, neutral columns
        CheckModel(1, 1, 0, 0, 1, "WAGER WON", "", "STAKE  10 CATS    RETURNED  17 CATS\nNET WIN  +7 CATS"),   // 1v1: one card column
    ];
}
