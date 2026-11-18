// Port of upstream/ProvingGrounds/plugin/src/TownBookieUI.h/.cpp (Proving Grounds by Shutori, GPLv3). Task 8.1a: the sim half (Core/Glue/TownBookieLogic.cs,
// src/Town/TownBookieUISim.cs: the model pg.bookie and the commands). Task 8.1b (TW §2.6; CAP 7.0-7.4, 7.7): the window half TownBookieScreen: the
// layout built call by call from EnsureWindow (:193-253) and CreateCard (:169-192) with LayoutBuilder / NativeUI (same widget types, skins, names,
// captions, alignments, colours and alphas), every rectangle from Layout / LayoutCard / Wrap (Core/Glue/TownBookieUILayout.cs, tested), the
// portraits, the tip, the roster's canvas and the clicks. Window thread only: the window reads the published TownBookieUiModel and sends PgCommands.
// Line numbers are TownBookieUI.cpp's. Meitou 0.17.3 (tag v0.17.3, fa34a1b).
//
// Engine calls of the C++ window and what replaces them:
//   createWidgetReal / createWidget (EnsureWindow, CreateCard,          -> LayoutBuilder XML (Build) -> ScreenHost (Register once). Every widget exists in
//     Text :158-162, BoundedText :163-168)                                 every shape (the eight cards are made once and hidden past the counts), so the
//                                                                        shape never changes and the layout is never Replaced
//   setCoord / setSize / setPosition (Layout, LayoutCard, Wrap)        -> TownBookieUILayout's rectangles, written into the XML and bound as fractions of
//                                                                        their parent (UiBindings.RelativePositions)
//   window->setCoord (Layout :276-281)                                  -> the root's bound screen rectangle (88% x 90% of the view, centred)
//   setCaption (Caption :51-53, Refresh :396-416, BindCard :358-359)    -> UiBindings Text from the model (the sim half's texts; the window's own
//                                                                        captions "STAKE: %d CATS", the headings and the side buttons made once per model)
//   setEnabled (Refresh :401-417) / setStateSelected (:414)             -> UiBindings Disabled / NativeUI.SelectedState
//   setVisible (the timer :398, the cards :423, BindCard :374-375)     -> Hide from TownBookieUILayout's visibility; a portrait with no free cell hidden and
//                                                                        its placeholder shown (BindPortrait's PortraitManager, 7.6's path)
//   setColour / setTextColour / setAlpha (the timer :208, the stake    -> Properties Colour / TextColour / Alpha in the XML; an EditBox's text colour also
//     value :214, the accents :188-189, the tip background :242-243)     bound on its template part (ScreenBinding.EditTextColour)
//   PortraitManager::setImageWidget (BindCard :368-372)                 -> Portraits.Bind (spike S7)
//   ScrollView::setCanvasSize / setViewOffset (Layout :352-355)          -> UiBindings.Canvas; MyGuiScreenHost.SetScroll / GetScroll clamped to the content at
//                                                                        each new layout; 0 again at a new world (AbandonWorldState destroys the window)
//   eventMouseWheel / Wheel (:131-146, Body * 3 a notch)                 -> Meitou's scroll view wheel (50 px a notch, as every window; divergences.md)
//   setNeedMouseFocus / setNeedKeyFocus(false) (Passive :68-74, the     -> NeedMouse false (the tip background and its text, the accents)
//     accents :190)
//   Select / Stake / Bet / Arrange (:147-154)                            -> BookieSelectSide (the side) / BookieChangeStake (the button's delta and the drawn
//                                                                        stake) / BookiePlaceBet (the drawn side and stake) / BookieArrange (task 8.1a), the
//                                                                        drawn model's, numbered by RecruitmentRequestGate.Send
//   eventWindowButtonPressed "close" (WindowButton :155-157), Escape    -> the framework's Closed report with the serial the window was drawn under (the sim
//                                                                        half closes only that window, TownBookieUISim.Apply)
//   eventToolTip (PortraitTip :87-110) + MyGUI's ToolTipManager          -> the pointer over a card's portrait or placeholder, polled each bind (TooltipTimer:
//                                                                        MyGUI's 0.5 s delay, as the town window); sized and placed by the town window's
//                                                                        ports of the same arithmetic (TownArenaUILayout.FighterTip / PlaceTip)
//   WidgetStyle::Overlapped / upLayerItem (the tip background :240-241) -> created last among the client's children, so drawn above the cards
//   getTextSize / getFontHeight                                         -> RewardsTextMeasure over src/Ui/TextMeasure.cs / ScreenHost.FontHeight
//   RenderManager::getViewSize                                          -> ScreenHost.ViewSize
//   the catch blocks of EnsureWindow (:248-252) and Refresh (:428-430)  -> PgScreen.Failed: the C++ error line; the framework reports the window closed
//                                                                        when it was asked open and tries again at the next open request
using System.Globalization;
using System.Numerics;
using Meitou.Data.Ui;
using Meitou.Simulation;
using ProvingGrounds.Core;
using ProvingGrounds.Core.Glue;
using ProvingGrounds.Engine;
using ProvingGrounds.Town;
using static ProvingGrounds.Ui.TownWindowParts;
using N = ProvingGrounds.Core.Glue.TownBookieUINames;

namespace ProvingGrounds.Ui;

/// <summary>
/// The bookie's betting window <c>pg.bookie</c> (task 8.1b), declared to the window framework (src/Ui/ScreenHost.cs). EnsureWindow's widgets and the
/// eight cards are one XML layout of one shape. Everything Refresh / Layout change (captions, every rectangle, visibility, enabled and selected
/// states, portraits, the canvas, the window rectangle) is bound per frame from a plan made once per model, view and font; the tip follows the
/// pointer, so it is bound every frame outside the plan.
/// </summary>
static class TownBookieScreen
{
    static readonly TextMeasure text = new();
    static readonly RewardsTextMeasure measure = new(text);
    static bool wasOpen;
    static readonly RecruitmentRequestGate gate = new();   // the shared request numbering (Core/Glue/RecruitmentRequestGate.cs)
    static bool rosterBar;                                  // the roster's bar after the last layout (MyGUI keeps it until the next setCanvasSize)
    static PgWindowLayout? clampedFor;
    static readonly WindowTip tip = new(TownBookieUISim.ScreenId, N.Window, N.TipBackground, N.FighterTip, text, measure);   // src/Ui/TownWindowParts.cs

    static TownBookieUiView<CharacterId>? Of(PgUiModel? m) => m is TownBookieUiModel t ? t.View : null;

    internal static readonly PgScreen Declaration = new()
    {
        Id = TownBookieUISim.ScreenId,
        ButtonEvent = N.ButtonEvent,   // task 4.4b: every button eventMouseButtonClick (:218, :220, :227, :236)
        ShapeKey = m => Of(m) is null ? "bookie/none" : "bookie",
        BeforeFrame = BeforeFrame,
        Layout = Build,
        Bind = Bind,
        Click = Click,
        WhileClosed = () => { if (wasOpen) ResetWindowState(); },   // Close :380 HideTip
        NewWorld = NewWorld,
        Portraits = m => Of(m) is { } v ? FacesOf(m!, v) : [],
        Failed = Failed,
        CheckModels = CheckModels,
    };

    /// <summary>Window-side state that must not outlive one opening of the window (close, a new world, the first bind after opening): the plan and
    /// the tip's wait (HideTip at Close :380).</summary>
    static void ResetWindowState()
    {
        wasOpen = false;
        plan = null;
        planKey = default;
        current = default;
        tip.Reset();
    }

    // C++: TownBookie.cpp:409 AbandonWorldState -> TownBookieUI::Destroy (:381-392): the window destroyed, so its scroll offset and bar start over
    static void NewWorld(Meitou.Game.Ui.MyGuiScreenHost host)
    {
        if (host.IsRegistered(TownBookieUISim.ScreenId)) host.SetScroll(TownBookieUISim.ScreenId, N.Roster, 0);
        ResetWindowState();
        rosterBar = false;
        clampedFor = null; tip.ForgetOrigin(); facesFor = default;
    }

    static (PgUiModel? Model, IReadOnlyList<CharacterId> Faces) facesFor;

    /// <summary>The portraits a model asks for (BindCard :368-372: each shown card with a key), made once per model.</summary>
    static IReadOnlyList<CharacterId> FacesOf(PgUiModel m, TownBookieUiView<CharacterId> v)
    {
        if (ReferenceEquals(facesFor.Model, m)) return facesFor.Faces;
        var list = new List<CharacterId>();
        for (int side = 0; side < 2; ++side)
            for (int i = 0; i < v.Cards[side].Count && i < TownBookieUILayout<CharacterId>.Members; ++i)
                if (v.Cards[side][i].PortraitKey.Length > 0) list.Add(v.Cards[side][i].Actor);
        facesFor = (m, list);
        return list;
    }

    static (int W, int H) View()
    {
        var (w, h) = ScreenHost.ViewSize;
        return w <= 1 || h <= 1 ? (1920, 1080) : (w, h);
    }

    static void BeforeFrame() => gate.FollowWorld(ProvingGroundsSystem.Current);   // a new world starts the window's request numbers over

    static PgWindowLayout Compute(TownBookieUiView<CharacterId> v, int font)
    {
        text.UseCurrent();
        var (w, h) = View();
        return TownBookieUILayout<CharacterId>.Compute(v, new BookieLayoutInput { ViewWidth = w, ViewHeight = h, FontHeight = font, RosterBarBefore = rosterBar },
            measure);
    }

    /// <summary>The C++ <c>EnsureWindow</c> stage that threw (its catch logs "creation failed", :248-250); null while Refresh runs (its catch logs
    /// "refresh failed", :428-430).</summary>
    static string? resource = "creation";

    static string F(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);
    static string C(float r, float g, float b) => F(r) + " " + F(g) + " " + F(b);

    /// <summary>The timer's and the stake value's text colour (:208, :214).</summary>
    internal static readonly string Gold = C(.95f, .72f, .22f);
    /// <summary>The accents' colours (CreateCard :188: Team A red, Team B blue).</summary>
    internal static readonly string[] AccentColour = [C(.65f, .30f, .23f), C(.30f, .48f, .63f)];

    // C++: TownBookieUI.cpp:193-253 EnsureWindow (+ CreateCard :169-192 for each card), in creation order (each widget under its parent); positions from
    // TownBookieUILayout, the captions of this model
    static LayoutBuilder Build(PgUiModel? m, int font)
    {
        var v = Of(m) ?? TownBookieUiView<CharacterId>.Empty;
        var l = Compute(v, font);
        current = default;
        var b = new LayoutBuilder();
        LayoutRect R(string name) => l.Rects.TryGetValue(name, out var r) ? r : new LayoutRect(0, 0, 100, 24);
        LayoutBuilder Open(string type, string skin, string name)
        {
            var r = R(name);
            return b.Open(type, skin, r.X, r.Y, r.W, r.H, name);
        }
        // :158-162 Text: NativeUI::WrappedLabel (a Kenshi_WordWrapEmpty EditBox, caption ""); :163-168 BoundedText: a Kenshi_WordWrap EditBox
        LayoutBuilder Text(string name, string caption) => Open("EditBox", "Kenshi_WordWrapEmpty", name).Prop("Caption", caption);
        LayoutBuilder Bounded(string name, string caption) => Open("EditBox", "Kenshi_WordWrap", name).Prop("Caption", caption);
        LayoutBuilder Button(string name, string caption) => Open("Button", "Kenshi_Button1", name).Prop("Caption", caption);   // NativeUI::Button

        resource = "creation";
        var view = v.View;
        // :198-202 createWidgetReal<Window>("Kenshi_WindowCX", .06f, .05f, .88f, .90f, Align::Center, "Window", "PG_Bookie"); setCaption; setVisible(false)
        // (the framework opens it when the sim half asks); Layout's rectangle is bound (Bind)
        b.OpenReal("Window", "Kenshi_WindowCX", .06f, .05f, .88f, .90f, N.Window, "Center", "Window").Prop("Caption", "Scratch Bookie - Proving Grounds");
        Bounded(N.Header, view.Header).Close();                                                           // :205
        Bounded(N.Timer, view.Timer).Prop("TextAlign", "Center").Prop("TextColour", Gold).Close();       // :206-208 (Align::Center | VCenter)
        var rr = R(N.Roster);
        NativeUI.Scroll(b, rr.X, rr.Y, rr.W, rr.H, N.Roster);                                             // :209
        Text(N.Versus, "VS").Prop("TextAlign", "Center").Close();                                         // :215-216 (inside the roster)
        for (int side = 0; side < 2; ++side)
        {
            Text(N.Team(side), TownBookieUILayout<CharacterId>.Heading(view, side)).Close();              // :231-232 heading[side] (inside the roster)
            for (int i = 0; i < TownBookieUILayout<CharacterId>.Members; ++i)                             // :237 CreateCard(side, i)
            {
                var card = i < v.Cards[side].Count ? v.Cards[side][i] : null;
                Open("Widget", "PanelEmpty", N.Card(side, i));                                            // :174
                Open("ImageBox", "ImageBox", N.Portrait(side, i)).Close();                                // :176
                Text(N.Name(side, i), card is null ? "" : TownBookieUILayout<CharacterId>.NameCaption(card.View)).Close();   // :179
                Text(N.Record(side, i), card?.View.Record ?? "").Close();                                 // :180
                Text(N.Placeholder(side, i), "Portrait unavailable").Prop("TextAlign", "Center").Close(); // :181-184
                // :187-190 the accent: WhiteSkin, the side's colour, alpha .65, no mouse focus
                Open("Widget", "WhiteSkin", N.Accent(side, i)).Prop("Colour", AccentColour[side]).Prop("Alpha", "0.65").Prop("NeedMouse", "false").Close();
                b.Close();   // card
            }
        }
        b.Close();   // roster
        Bounded(N.Status, view.Status).Close();                                                           // :210
        Bounded(N.Slip, view.Slip).Close();                                                               // :211
        Text(N.StakeValue, StakeCaption(view.Stake)).Prop("TextAlign", "Center").Prop("TextColour", Gold).Close();   // :212-214
        Button(N.Bet, "Place bet").Close();                                                               // :217
        Button(N.Arrange, "Arrange next NPC bout").Close();                                               // :219
        for (int i = 0; i < 6; ++i)                                                                       // :221-228
        {
            int d = TownBookieLogic<CharacterId>.Adjustments[i];
            Button(N.StakeAdjust(i), d.ToString("+0;-0", CultureInfo.InvariantCulture)).UserString("stake", d.ToString(CultureInfo.InvariantCulture)).Close();
        }
        for (int side = 0; side < 2; ++side)                                                              // :233-236
            Button(N.Side(side), SideCaption(side, view.SelectedSide)).UserString("side", side.ToString(CultureInfo.InvariantCulture)).Close();
        // :240-246 the tip background (an overlapped node of the client, made last: drawn above the cards), dark and opaque, hidden, Passive; its text
        Open("Widget", "WhiteSkin", N.TipBackground).Prop("Colour", C(.06f, .06f, .06f)).Prop("Alpha", "1").Prop("Visible", "false").Prop("NeedMouse", "false");
        Bounded(N.FighterTip, "").Prop("NeedMouse", "false").Close();
        b.Close();   // tip background
        b.Close();   // window
        resource = null;
        return b;
    }

    // C++: Refresh :402-403 sprintf_s("STAKE: %d CATS", stake)
    static string StakeCaption(int stake) => "STAKE: " + stake.ToString(CultureInfo.InvariantCulture) + " CATS";

    // C++: Refresh :415-416
    static string SideCaption(int side, int selected) =>
        side == 0 ? (selected == side ? "Team A selected" : "Select Team A") : (selected == side ? "Team B selected" : "Select Team B");

    // ---------- the C++ catch blocks ----------

    /// <summary>The framework could not register (EnsureWindow) or bind (Refresh) the layout: the C++ error line (:249-250, :429-430). The framework
    /// reports the window closed to the sim half when it was asked open and tries again at the next open request.</summary>
    static void Failed(string what, Exception e)
    {
        if (what == "register" && resource is not null) ModLog.Error("Proving Grounds: bookie UI creation failed: " + e.Message);
        else ModLog.Error("Proving Grounds: bookie UI refresh failed: " + e.Message);
    }

    // ---------- bind ----------

    /// <summary>What one bind writes: made when the model, view or font changes, applied every frame (no allocation in the steady state).</summary>
    sealed class Plan
    {
        public UiRect Root;
        public (string Name, (float W, float H) Canvas) Canvas;
        public readonly List<(string Name, UiRect Fraction)> Sizes = [];
        public readonly List<(string Name, string Text)> Texts = [];
        public readonly List<string> Hidden = [], Disabled = [];
        public readonly List<(string Name, IReadOnlyDictionary<string, string> Props)> Props = [], EditColours = [];
        public readonly List<(string Image, CharacterId Who, string Placeholder)> Faces = [];
        public readonly string[][] Tips = [new string[TownBookieUILayout<CharacterId>.Members], new string[TownBookieUILayout<CharacterId>.Members]];
        public PgWindowLayout Layout = null!;
    }

    static Plan? plan;
    static (PgUiModel? Model, PgWindowLayout? Layout) planKey;
    /// <summary>The layout of the model shown now, and what it was computed for.</summary>
    static (PgUiModel? Model, (int W, int H) View, int Font, PgWindowLayout? Layout) current;

    /// <summary>C++ Refresh's Layout (:427) for the model shown now; the last one when it is the same computation.</summary>
    static PgWindowLayout LayoutFor(PgUiModel? m, TownBookieUiView<CharacterId> v)
    {
        var view = View();
        int font = ScreenHost.FontHeight;
        if (ReferenceEquals(current.Model, m) && current.View == view && current.Font == font && current.Layout is { } l) return l;
        l = Compute(v, font);
        current = (m, view, font, l);
        rosterBar = l.ScrollBar;   // the bar MyGUI keeps for the next layout pass of the shown window
        return l;
    }

    // the creation tree: each widget's parent ("" = the window's client; the roster = its canvas)
    static string ParentOf(string name)
    {
        if (name == N.Versus || name.StartsWith("PG_BookieTeam", StringComparison.Ordinal)) return N.Roster;
        if (name == N.FighterTip) return N.TipBackground;
        for (int side = 0; side < 2; ++side)
            for (int i = 0; i < TownBookieUILayout<CharacterId>.Members; ++i)
            {
                string card = N.Card(side, i);
                if (name == card) return N.Roster;
                if (name.StartsWith(card, StringComparison.Ordinal)) return card;
            }
        return "";
    }

    static IReadOnlyDictionary<string, string> Prop(string key, string value) => new Dictionary<string, string>(StringComparer.Ordinal) { [key] = value };

    static readonly IReadOnlyDictionary<string, string> GoldText = Prop("TextColour", Gold);
    static readonly IReadOnlyDictionary<string, string> BarOn = new Dictionary<string, string>(StringComparer.Ordinal) { ["VisibleVScroll"] = "true", ["VisibleHScroll"] = "false" };
    static readonly IReadOnlyDictionary<string, string> BarOff = new Dictionary<string, string>(StringComparer.Ordinal) { ["VisibleVScroll"] = "false", ["VisibleHScroll"] = "false" };

    static Plan MakePlan(TownBookieUiView<CharacterId> v, PgWindowLayout l)
    {
        var view = v.View;
        var p = new Plan { Layout = l, Root = new UiRect(l.Window.X, l.Window.Y, l.Window.W, l.Window.H) };
        foreach (var (name, rect) in l.Rects)
        {
            if (name == N.TipBackground || name == N.FighterTip) continue;   // the tip: per frame
            p.Sizes.Add((name, Fraction(rect, ParentSize(l, ParentOf(name)))));
        }
        var canvas = l.Canvas[N.Roster];
        p.Canvas = (N.Roster, (canvas.W, canvas.H));
        // the bar MyGUI shows after the C++'s layout (as the town window, task 7.6 review: Meitou keeps both bars for a canvas the size of the bar-less view)
        p.Props.Add((N.Roster, l.ScrollBar ? BarOn : BarOff));
        foreach (var n in l.Hidden) if (n != N.TipBackground) p.Hidden.Add(n);

        // Refresh :396-416 and BindCard :358-359
        p.Texts.Add((N.Header, view.Header));
        p.Texts.Add((N.Timer, view.Timer));
        p.Texts.Add((N.Status, view.Status));
        p.Texts.Add((N.Slip, view.Slip));
        p.Texts.Add((N.StakeValue, StakeCaption(view.Stake)));
        p.EditColours.Add((N.Timer, GoldText));        // :208 (an EditBox: ScreenBinding.EditTextColour)
        p.EditColours.Add((N.StakeValue, GoldText));   // :214
        if (!view.CanBet) p.Disabled.Add(N.Bet);             // :401
        if (!view.CanArrange) p.Disabled.Add(N.Arrange);     // :401
        for (int i = 0; i < 6; ++i)                          // :404-407 (the sim half's rule, published)
            if (i >= v.AdjustmentEnabled.Count || !v.AdjustmentEnabled[i]) p.Disabled.Add(N.StakeAdjust(i));
        for (int side = 0; side < 2; ++side)
        {
            p.Texts.Add((N.Team(side), TownBookieUILayout<CharacterId>.Heading(view, side)));
            p.Texts.Add((N.Side(side), SideCaption(side, view.SelectedSide)));
            p.Props.Add((N.Side(side), NativeUI.SelectedState(view.SelectedSide == side)));   // :414
            if (!view.CanSelect) p.Disabled.Add(N.Side(side));                               // :417
            for (int i = 0; i < TownBookieUILayout<CharacterId>.Members; ++i)
            {
                p.Tips[side][i] = "";   // :419-421 SetTip(.., "") past the count
                if (i >= v.Cards[side].Count) continue;
                var c = v.Cards[side][i];
                p.Texts.Add((N.Name(side, i), TownBookieUILayout<CharacterId>.NameCaption(c.View)));
                p.Texts.Add((N.Record(side, i), c.View.Record));
                p.Tips[side][i] = c.View.Name + "\n" + c.View.Style + "\n\n" + c.View.Stats;   // :360
                if (c.PortraitKey.Length > 0) p.Faces.Add((N.Portrait(side, i), c.Actor, N.Placeholder(side, i)));
            }
        }
        return p;
    }

    /// <summary>The roster's offset as the C++ moves it: after each new layout the offset (the player's, read back) clamped to the content (:355).
    /// Show keeps it (the C++ window is only hidden at Close).</summary>
    static void FollowScroll(PgWindowLayout l)
    {
        if (ReferenceEquals(clampedFor, l) || ScreenHost.Host is not { } host || !host.IsRegistered(TownBookieUISim.ScreenId)) return;
        clampedFor = l;
        host.SetScroll(TownBookieUISim.ScreenId, N.Roster, TownBookieUILayout<CharacterId>.ClampOffset(l, (int)host.GetScroll(TownBookieUISim.ScreenId, N.Roster)));
    }

    static void Bind(PgUiModel? m, ScreenBinding b)
    {
        if (!wasOpen) { ResetWindowState(); wasOpen = true; }
        if (Of(m) is not { } v) return;
        var size = ScreenHost.ViewSize;
        if (size.Width <= 1 || size.Height <= 1) return;   // no view yet (a UI reload): the XML's own positions stand
        var l = LayoutFor(m, v);
        FollowScroll(l);
        var key = (m, (PgWindowLayout?)l);
        if (plan is null || !planKey.Equals(key))
        {
            plan = MakePlan(v, l);
            planKey = key;
        }
        Apply(plan, b);
        BindTip(plan, l, b);
    }

    static void Apply(Plan p, ScreenBinding b)
    {
        foreach (var (n, t) in p.Texts) b.Text(n, t);
        foreach (var (n, f) in p.Sizes) b.Size(n, f);
        foreach (var n in p.Hidden) b.Hide(n);
        foreach (var n in p.Disabled) b.Disable(n);
        foreach (var (n, props) in p.Props) b.Props(n, props);
        foreach (var (n, props) in p.EditColours) b.EditTextColour(n, props);
        foreach (var (image, who, placeholder) in p.Faces)
            if (!b.Portrait(image, who))   // BindCard :368-375: no portrait (here: no free cell) = the placeholder instead, as 7.6
            {
                b.Hide(image);
                b.Bindings.Hidden.Remove(placeholder);
                b.Props(placeholder, Shown);
            }
        b.Bindings.Canvas[p.Canvas.Name] = p.Canvas.Canvas;
        b.Place(N.Window, p.Root);
    }

    // ---------- the tip (PortraitTip :87-110, PlaceTip :75-86, HideTip :64-67) ----------

    static void BindTip(Plan p, PgWindowLayout l, ScreenBinding b)
    {
        if (ScreenHost.Host is not { } host) return;
        var at = ScreenHost.Pointer;
        string? widget = tip.Hover(host, at);
        string? owner = null, tipText = null;
        if (widget is not null && N.TryTipOwner(widget, out int side, out int slot) && p.Tips[side][slot].Length > 0) { owner = widget; tipText = p.Tips[side][slot]; }
        if (!tip.Follow(owner, at) || tipText is null) return;
        var origin = tip.ClientOrigin(l);
        int font = ScreenHost.FontHeight;
        var (w, h) = tip.BindFighterTip(b, tipText, l.ClientWidth, l.ClientHeight, font);   // :97-103 (the arena's arithmetic); Wrap(fighterTip, ...) (:103)
        tip.Place(b, l.ClientWidth, l.ClientHeight, font, at, origin, w, h);
    }

    // ---------- clicks ----------

    // C++ Select / Stake / Bet / Arrange (:147-154): each the command of what the window drew (m), numbered above the newest published LastSeq
    static PgCommand? Click(PgUiModel? m, UiClickData c)
    {
        if (c.Kind != UiEventKind.Click || Of(m) is not { } v) return null;
        var system = ProvingGroundsSystem.Current;
        gate.FollowWorld(system);
        var newest = system?.Snapshot?.Model(TownBookieUISim.ScreenId);
        gate.FollowSerial(newest?.RequestSerial ?? m!.RequestSerial);   // a new Show / Close starts over
        long published = Of(newest)?.LastSeq ?? v.LastSeq;
        string w = c.Widget;
        var view = v.View;
        if (N.TrySide(w, out int side))
        {
            long seq = gate.Send(published);
            return seq == 0 ? null : new BookieSelectSide(side, seq) { Tick = 0 };
        }
        if (N.TryStakeAdjust(w, out int index))   // the button's own delta (its "stake" user string :226), never a computed stake
        {
            long seq = gate.Send(published);
            return seq == 0 ? null : new BookieChangeStake(TownBookieLogic<CharacterId>.Adjustments[index], seq, view.Stake) { Tick = 0 };
        }
        if (w == N.Bet)
        {
            long seq = gate.Send(published);
            return seq == 0 ? null : new BookiePlaceBet(seq, view.SelectedSide, view.Stake) { Tick = 0 };
        }
        if (w == N.Arrange)
        {
            long seq = gate.Send(published);
            return seq == 0 ? null : new BookieArrange(seq) { Tick = 0 };
        }
        return null;
    }

    // ---------- PgCheck ui ----------

    /// <summary>PgCheck ui only: every window-side state back to a fresh process's.</summary>
    internal static void ResetForCheck()
    {
        ResetWindowState();
        rosterBar = false;
        clampedFor = null; tip.ForgetOrigin(); facesFor = default;
    }

    /// <summary>PgCheck ui only: the plan's face decisions for a model (the offline check has no portrait cells): each portrait widget given a face.</summary>
    internal static IReadOnlyList<string> FacesForCheck(PgUiModel? m)
    {
        var v = Of(m) ?? TownBookieUiView<CharacterId>.Empty;
        return [.. MakePlan(v, Compute(v, NativeUI.DefaultFontHeight)).Faces.Select(f => f.Image)];
    }

    static BookieFighterCard<CharacterId> CheckCard(int id, string name, string style, bool face) =>
        new(new CharacterId(id, 0), face ? "key" + id.ToString(CultureInfo.InvariantCulture) : "", new Core.TownBookie.FighterView
        {
            Name = name, Style = style, Record = id % 2 == 0 ? "MMR 1210 | 3 W / 1 L" : "Unrated | 0 W / 0 L",
            Stats = "Attack 41 | Defence 33 | Strength 28\nToughness 30 | Dexterity 25",
        });

    static TownBookieUiModel CheckModel(int a, int b, int selected, int stake, bool market, bool placed = false, string timer = "NEXT BOUT 14:48 | STARTS IN 0h 53m")
    {
        var view = new Core.TownBookie.View
        {
            Header = market ? "BETTING OPEN | 5000 Cats | NPC bout " + a.ToString(CultureInfo.InvariantCulture) + "v" + b.ToString(CultureInfo.InvariantCulture)
                : "BETTING CLOSED | 5000 Cats | No NPC bout",
            Timer = market ? timer : "",
            Status = placed ? "Accepted: 500 Cats. Winning total return: 875 Cats." : market ? "Select a team, then choose your stake. One wager per bout."
                : "NPC bouts only. Arrange a bout to open betting.",
            Slip = placed ? "ACCEPTED: Team A | Stake 500 Cats | Quoted return 875 Cats | Potential profit +375 Cats"
                : "YOUR BET: Choose a team and stake. Place bet confirms your wager.",
            SelectedSide = selected, Stake = stake, CanSelect = market && !placed, CanStake = market && !placed,
            CanBet = market && !placed && selected >= 0, CanArrange = !market,
        };
        view.Odds[0] = market ? "1.75x return" : ""; view.Odds[1] = market ? "1.85x return" : "";
        string[] names = ["Soo", "Masaru", "Skinner", "Ressa Vane", "Bum-Per", "Hacker", "Polearm", "Dust Raider"];
        var cards = new IReadOnlyList<BookieFighterCard<CharacterId>>[2];
        cards[0] = [.. Enumerable.Range(0, a).Select(i => CheckCard(10 + i, names[i % 8], "Sabre", i != 1))];
        cards[1] = [.. Enumerable.Range(0, b).Select(i => CheckCard(20 + i, names[(i + 3) % 8], "Katana", i != 2))];
        var adjust = new bool[6];
        for (int i = 0; i < 6; ++i) adjust[i] = view.CanStake && TownBettingPolicy.AdjustStake(stake, TownBookieLogic<CharacterId>.Adjustments[i]) != stake;
        var v = new TownBookieUiView<CharacterId>(true, 1, 0, view, cards, adjust, "check");
        return new TownBookieUiModel(v) { RequestSerial = 1, RequestOpen = true };
    }

    static IReadOnlyList<PgUiModel?> CheckModels() =>
    [
        null,
        CheckModel(0, 0, -1, 100, market: false),                       // no bout: no fighters, Arrange enabled, no timer
        CheckModel(1, 2, 0, 500, market: true),                         // ws-13's 1v2, Team A selected, stake 500 (a card without a portrait key)
        CheckModel(1, 2, 0, 500, market: true, placed: true),           // after Place bet: nothing to select or stake
        CheckModel(4, 4, 1, 10, market: true),                          // 4v4, Team B, the minimum stake: the minus buttons disabled; the roster scrolls
        CheckModel(3, 1, -1, 10000, market: true, timer: ""),          // the maximum stake, no timer text
    ];
}
