// Port of upstream/ProvingGrounds/plugin/src/TownArenaUI.h/.cpp (Proving Grounds by Shutori, GPLv3). Task 7.5: the sim half (Core/Glue/
// TownArenaUILogic.cs, src/Town/TownArenaUISim.cs) behind the frame-loop object TownArenaUI below. Task 7.6 (TW §2.5; CAP 7.0-7.4; spikes S6, S7):
// the window half TownArenaScreen: the layout built call by call from EnsureWindow (:806-891), MakeRow (:383-403) and MakeAccent (:454-475) with
// LayoutBuilder / NativeUI (same widget types, skins, names where the C++ names them, captions, alignments, colours and alphas), every rectangle from
// LayoutTownPanel and its helpers (Core/Glue/TownArenaUILayout.cs, tested), the portraits, the tips, the scroll canvases and the clicks. Window thread
// only: the window reads the published TownArenaUiModel and sends PgCommands. Meitou 0.17.3 (tag v0.17.3, fa34a1b).
//
// Engine calls of the C++ window and what replaces them:
//   createWidgetReal / createWidget (EnsureWindow, MakeRow, MakeAccent) -> LayoutBuilder XML (Build) -> ScreenHost (Register / Replace). The shape key
//                                                                          is the row count (the rows are named by their index; the C++ rebuilds them
//                                                                          when the identities change, here a row's face and caption are bound per frame)
//   setCoord / setSize / setPosition (LayoutTownPanel and helpers,     -> TownArenaUILayout's rectangles, written into the XML and bound as fractions
//     Wrap, RefreshStatusBar, LayoutAccent)                               of their parent (UiBindings.RelativePositions)
//   window->setCoord (LayoutTownPanel :676-681)                         -> the root's bound screen rectangle (1/25 of the view in, 23/25 of it in size)
//   setCaption / SetText (Tick, RefreshRoster, RefreshCard, ...)        -> UiBindings Text from the model (the sim half's captions)
//   setEnabled (the buttons, Tick :922-948) / setStateSelected          -> UiBindings Disabled / NativeUI.SelectedState
//     (the rows :256, the divisions :930, the card books :315, :947)
//   setVisible (the cards, members, rows' borders and bars, Skarn)      -> Hide from the layout's visibility (TownArenaUILayout.Visibility); a portrait
//                                                                          with no free cell hidden (BindPortrait's PortraitManager)
//   setColour / setTextColour / setAlpha (StatusColour, LayoutAccent,   -> Properties Colour / TextColour / Alpha: static ones in the XML, the ones the
//     RefreshSkarn, RefreshCard's portrait alpha)                         model decides bound per plan
//   metrics->getTextColour() (SelectionVisuals :262)                    -> the palette's ink (MyGuiColours.InkText), as the Leaderboard window
//   PortraitManager::setImageWidget (BindPortrait :337-341)             -> Portraits.Bind (spike S7)
//   ScrollView::setCanvasSize / setViewOffset (the layouts' clamps)     -> UiBindings.Canvas; MyGuiScreenHost.SetScroll / GetScroll (vertical and the
//                                                                          challenges view's horizontal "@h" key) clamped to the content at each new
//                                                                          layout; 0 again at a new world (AbandonWindow destroys the window)
//   eventMouseWheel / ScrollBy (:121-136, Body * 3 a notch)             -> Meitou's scroll view wheel (50 px a notch, as every window; divergences.md)
//   setNeedMouseFocus / setNeedKeyFocus(false) (Passive :114-121)       -> NeedMouse false (the row's parts, the accent, the tip)
//   RowReleased (eventMouseButtonReleased + Inside, :280-297)           -> TownSelectFighter (a left release over the row it was pressed on = the
//                                                                          framework's Click, task 4.4b) with the row's identity, numbered by
//                                                                          RecruitmentRequestGate.Next
//   Challenge / Book / BuyRefresh / Division (:312-333)                 -> TownBookChallenge (the card's OfferKey and quote as drawn) / TownBookDirect
//                                                                          (the caption as drawn) / TownBuyRefresh (the cost as drawn) / TownSetDivision
//                                                                          (task 7.5), the booking ones numbered by RecruitmentRequestGate.Send
//   SelectOffer (eventMouseButtonClick on a card's text parts, :298-311) -> a left release over the part (WantsReleases: Meitou activates only buttons,
//                                                                          so a non-button's click is its release; divergences.md); window-side state
//   eventToolTip (PortraitTip :184-221) + MyGUI's ToolTipManager        -> the pointer over a tip owner, polled each bind (TooltipTimer: MyGUI's 0.5 s
//                                                                          delay, as the Arena window's settings tip; Meitou has no per-widget tooltip
//                                                                          event); the tip sized by TownArenaUILayout.FighterTip / SkarnTip, placed by
//                                                                          PlaceTip
//   WidgetStyle::Overlapped / upLayerItem (the tip background :877-878) -> created last among the client's children, so drawn above the cards
//   eventWindowButtonPressed "close" (WindowButton :334-336), Escape    -> the framework's Closed report (the sim half's ClosedFromWindow)
//   getTextSize / getFontHeight                                         -> RewardsTextMeasure over src/Ui/TextMeasure.cs / ScreenHost.FontHeight
//   RenderManager::getViewSize                                          -> ScreenHost.ViewSize
//   InputManager::getMousePositionByLayer (Inside, PortraitTip)         -> ScreenHost.Pointer (MyGuiScreenHost.HitTest for the widget under it)
//   the catch blocks of EnsureWindow (:888-890) and Tick (:955-956)     -> PgScreen.Failed: the C++ error line; the framework reports the window closed
//                                                                          when it was asked open and tries again at the next open request
using System.Globalization;
using System.Numerics;
using Meitou.Data.Ui;
using Meitou.Simulation;
using ProvingGrounds.Core;
using ProvingGrounds.Core.Glue;
using ProvingGrounds.Engine;
using ProvingGrounds.Town;
using static ProvingGrounds.Ui.TownWindowParts;
using N = ProvingGrounds.Core.Glue.TownArenaUINames;

namespace ProvingGrounds.Ui;

/// <summary>TownArenaUI.h:5-9 (Show / Close / AbandonWorldState / IsVisible / Tick) over its sim half (src/Town/TownArenaUISim.cs, task 7.5). The
/// window itself is <see cref="TownArenaScreen"/> (task 7.6).</summary>
sealed class TownArenaUI(Town.TownArenaUISim sim)
{
    internal Town.TownArenaUISim Sim => sim;

    // C++: TownArenaUI.cpp:909: called from ArenaUI.cpp ForgottenGUI_update_hook (:3131-3186)
    internal void Tick(World world) => sim.Tick(world);

    // ArenaUI::ShowFromRegistry / Close / AbandonWorldState call these (task 4.5a)
    internal void Show(World world, Core.Glue.ArenaBuilding registry, bool bindRegistry) => sim.Show(world, registry, bindRegistry);   // C++ TownArenaUI.cpp:957-965
    internal void Close(World world) => sim.Close(world);                                                                             // C++ TownArenaUI.cpp:904
    internal void AbandonWorldState(World world) => sim.AbandonWorldState(world);                                                     // C++ TownArenaUI.cpp:905-908

    // C++: TownArenaUI.cpp:903
    internal bool IsVisible => sim.IsVisible;
}

/// <summary>
/// The town registry window <c>pg.town</c> (task 7.6), declared to the window framework (src/Ui/ScreenHost.cs). EnsureWindow's widgets, the five cards
/// and the fighter rows are one XML layout; the shape key is the row count (the framework adds the font height), so only a new number of squad fighters
/// rebuilds and Replaces it. Everything else the C++ changes within one shape (captions, every rectangle, visibility, enabled and selected states,
/// colours, alphas, portraits, the canvases, the window rectangle) is bound per frame from a plan made once per layout; the tip follows the pointer, so
/// it is bound every frame outside the plan.
/// </summary>
static class TownArenaScreen
{
    static readonly TextMeasure text = new();
    static readonly RewardsTextMeasure measure = new(text);
    static bool wasOpen;
    static PgUiModel? liveModel;
    static readonly RecruitmentRequestGate gate = new();   // shared with the recruitment window (Core/Glue/RecruitmentRequestGate.cs)

    // window-side state of the C++ (selectedOffer, displayedDivision :43): SelectOffer's highlighted Book button, reset by a division change, the
    // selection prompt, a card that disappears and Close
    static int selectedOffer = -1, displayedDivision = -1;
    // the scroll views' bars after the last layout (MyGUI keeps them until the next setCanvasSize)
    static bool sidebarBar, rosterBar, challengesVBar, challengesHBar;
    static TownWindowLayout? clampedFor;
    static readonly WindowTip tip = new(TownArenaUISim.ScreenId, N.Window, N.TipBackground, N.FighterTip, text, measure);   // src/Ui/TownWindowParts.cs

    static TownArenaUiView<CharacterId>? Of(PgUiModel? m) => m is TownArenaUiModel t ? t.View : null;

    /// <summary>EnsureWindow's captions (:810-887) for a layout before any model.</summary>
    static readonly TownArenaUiView<CharacterId> Created = new()
    {
        Squad = "Selected team: 0 / 3", NextRefresh = "Next Refresh: calculating...", RefreshButton = "Buy Refresh (500 Cats)",
        BookingButton = "Book direct 1v1", SkarnPlaceholder = "SKARN\nLOCKED", SkarnState = "LOCKED",
        Cards = [.. Enumerable.Range(0, 5).Select(s => new TownArenaCardView<CharacterId>(s, false, "", "", false, false, "", false, false, -1, false, 1, [],
            new TownChallengeBuyInPolicy.Quote()))],
    };

    internal static readonly PgScreen Declaration = new()
    {
        Id = TownArenaUISim.ScreenId,
        ButtonEvent = N.ButtonEvent,   // task 4.4b: TownArenaUI.cpp's bindings (every button eventMouseButtonClick; the rows' release acts as a click)
        ShapeKey = ShapeKey,
        BeforeFrame = BeforeFrame,
        Layout = Build,
        Bind = Bind,
        Click = Click,
        WantsReleases = true,   // SelectOffer: a card's text parts are not buttons
        WhileClosed = () => { if (wasOpen) ResetWindowState(); selectedOffer = -1; },   // Close :904 selectedOffer = -1
        NewWorld = NewWorld,
        Portraits = m => Of(m) is { } v ? FacesOf(m!, v) : [],
        Failed = Failed,
        CheckModels = CheckModels,
    };

    /// <summary>Window-side state that must not outlive one opening of the window (close, a new world, the first bind after opening): the plan and
    /// the tip's wait (HideTip at Close :904).</summary>
    static void ResetWindowState()
    {
        wasOpen = false;
        plan = null;
        planKey = default;
        current = default;
        tip.Reset();
    }

    // C++: TownArenaUI.cpp:905-908 AbandonWorldState -> AbandonWindow (:784-805): the window destroyed, so its scroll offsets, bars and the selected
    // offer start over
    static void NewWorld(Meitou.Game.Ui.MyGuiScreenHost host)
    {
        string id = TownArenaUISim.ScreenId;
        if (host.IsRegistered(id))
        {
            host.SetScroll(id, N.Sidebar, 0);
            host.SetScroll(id, N.Roster, 0);
            host.SetScroll(id, N.Challenges, 0);
            host.SetScroll(id, N.Challenges + "@h", 0);
        }
        ResetWindowState();
        liveModel = null;
        selectedOffer = displayedDivision = -1;
        sidebarBar = rosterBar = challengesVBar = challengesHBar = false;
        clampedFor = null; tip.ForgetOrigin(); facesFor = default; skarnTipSize = null;
    }

    static (PgUiModel? Model, IReadOnlyList<CharacterId> Faces) facesFor;

    /// <summary>The portraits a model asks for, made once per model (the framework asks every frame).</summary>
    static IReadOnlyList<CharacterId> FacesOf(PgUiModel m, TownArenaUiView<CharacterId> v)
    {
        if (!ReferenceEquals(facesFor.Model, m)) facesFor = (m, Faces(v));
        return facesFor.Faces;
    }

    static IReadOnlyList<CharacterId> Faces(TownArenaUiView<CharacterId> v)
    {
        var list = new List<CharacterId>();
        foreach (var r in v.Rows) list.Add(r.Character);   // MakeRow :390 BindPortrait(row.portrait, c)
        foreach (var c in v.Cards)                          // RefreshCard :531-533 BindPortrait(card.portraits[member], c)
            if (c.Visible)
                foreach (var p in c.Portraits)
                    if (p.PortraitVisible) list.Add(p.Character);
        if (v.SkarnKey.Length > 0) list.Add(v.Skarn);      // RefreshSkarn :345 BindPortrait(skarnPortrait, skarn)
        return list;
    }

    // ---------- shape ----------

    /// <summary>The widget set: the fighter rows (named by their index, MakeRow :383-403); every other widget exists in every shape.</summary>
    static string ShapeKey(PgUiModel? m) => Of(m) is { } v ? "town/rows " + v.Rows.Count.ToString(CultureInfo.InvariantCulture) : "town/none";

    static (int W, int H) View()
    {
        var (w, h) = ScreenHost.ViewSize;
        return w <= 1 || h <= 1 ? (1920, 1080) : (w, h);
    }

    // the live model; Tick's window-side rules for the selected offer (:926, :939, :945)
    static void BeforeFrame()
    {
        var system = ProvingGroundsSystem.Current;
        gate.FollowWorld(system);   // a new world starts the window's request numbers over
        liveModel = system?.Snapshot?.Model(TownArenaUISim.ScreenId);
        if (Of(liveModel) is not { Open: true } v) return;
        if (displayedDivision != v.Division) selectedOffer = -1;
        displayedDivision = v.Division;
        if (v.SelectionPrompt) selectedOffer = -1;
        else if (selectedOffer >= 0 && (selectedOffer >= v.Cards.Count || !v.Cards[selectedOffer].Visible)) selectedOffer = -1;
    }

    static TownWindowLayout Compute(TownArenaUiView<CharacterId> v, int font)
    {
        text.UseCurrent();
        var (w, h) = View();
        var input = new TownLayoutInput
        {
            ViewWidth = w, ViewHeight = h, FontHeight = font, SidebarBarBefore = sidebarBar, RosterBarBefore = rosterBar,
            ChallengesVBarBefore = challengesVBar, ChallengesHBarBefore = challengesHBar,
        };
        return TownArenaUILayout<CharacterId>.Compute(v, input, measure);   // the bars it ends with are kept only by LayoutFor (the live window)
    }

    /// <summary>The C++ <c>EnsureWindow</c> stage that threw (its catch blocks log "creation failed", :888-890); null while Tick's refresh rebuilds
    /// rows (its catch logs "refresh failed", :955-956).</summary>
    static string? resource = "creation";

    static string F(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);
    static string C(float r, float g, float b) => F(r) + " " + F(g) + " " + F(b);

    // C++: TownArenaUI.cpp:806-891 EnsureWindow (+ MakeRow :383-403 for each row, made by RefreshRoster :441-444, and MakeAccent :454-475), in creation
    // order; positions from TownArenaUILayout, the captions of this model
    static LayoutBuilder Build(PgUiModel? m, int font)
    {
        var v = Of(m) ?? Created;
        var l = Compute(v, font);
        current = default;
        var b = new LayoutBuilder();
        LayoutRect R(string name) => l.Rects.TryGetValue(name, out var r) ? r : new LayoutRect(0, 0, 100, 24);
        LayoutBuilder Open(string type, string skin, string name)
        {
            var r = R(name);
            return b.Open(type, skin, r.X, r.Y, r.W, r.H, name);
        }
        // :791-797 Text: an EditBox, Kenshi_WordWrap when bounded, else Kenshi_WordWrapEmpty, with its caption
        LayoutBuilder Text(string name, string caption, bool bounded = false) =>
            Open("EditBox", bounded ? "Kenshi_WordWrap" : "Kenshi_WordWrapEmpty", name).Prop("Caption", caption);
        // :798-801 Button: NativeUI::Button
        LayoutBuilder Button(string name, string caption) => Open("Button", "Kenshi_Button1", name).Prop("Caption", caption);
        void Scroll(string name, bool hScroll = false)   // NativeUI::Scroll (+ setVisibleHScroll(true) for the challenges :818)
        {
            var r = R(name);
            if (!hScroll) { NativeUI.Scroll(b, r.X, r.Y, r.W, r.H, name); return; }
            b.Open("ScrollView", "Kenshi_ScrollView", r.X, r.Y, r.W, r.H, name)
                .Prop("VisibleHScroll", "true").Prop("VisibleVScroll", "true").Prop("CanvasAlign", "Left Top");
        }
        LayoutBuilder White(string name) => Open("Widget", "WhiteSkin", name).Prop("NeedMouse", "false");

        resource = "creation";
        var w = l.Window;
        // :812-815 createWidgetReal<Window>("Kenshi_WindowCX", .04f, .04f, .92f, .92f, Align::Center, "Window", "PG_TownRegistry"); setCaption;
        // setVisible(false) (the framework opens it when the sim half asks); LayoutTownPanel's rectangle is bound (Bind)
        b.OpenReal("Window", "Kenshi_WindowCX", .04f, .04f, .92f, .92f, N.Window, "Center", "Window").Prop("Caption", "Town Arena Registry - Rust Crucible");
        NativeUI.Label(b, 0, 0, 100, 24, N.Metrics, "Team").Prop("Visible", "false").Close();   // :820
        Scroll(N.Sidebar);                                                                    // :821
        Text(N.Team, v.Squad).Close();                                                        // :824
        Text(N.RosterHint, "Click a fighter to select. Click again to deselect.").Close();    // :825
        Scroll(N.Roster);                                                                     // :826
        Text(N.EmptyRoster, "No squad fighters available.").Close();                          // :827
        resource = null;   // the rows (RefreshRoster :441-444 inside Tick)
        for (int i = 0; i < v.Rows.Count; ++i)
        {
            var row = v.Rows[i];
            Button(N.Row(i), "");                                                             // :385 NativeUI::Button(parent, (0, 0, 200, 64), "", "")
            Open("ImageBox", "ImageBox", N.RowPortrait(i)).Prop("NeedMouse", "false").Close();   // :389-390 (Passive)
            Open("EditBox", "Kenshi_PaintedWordWrapEmpty", N.RowText(i)).Prop("Caption", row.Caption).Prop("NeedMouse", "false").Close();   // :391-393
            White(N.RowStatusBack(i)).Prop("Colour", C(.12f, .12f, .12f));                   // :394-396
            White(N.RowStatusFill(i)).Close();                                                // :397-398 (inside the back)
            b.Close();
            for (int e = 0; e < 4; ++e) White(N.RowBorder(i, e)).Prop("Colour", MyGuiColours.InkText).Close();   // :401-404 (SelectionVisuals :262)
            b.Close();   // row
        }
        resource = "creation";
        b.Close();   // roster
        b.Close();   // sidebar
        Scroll(N.Challenges, hScroll: true);                                                  // :822, :818
        Text(N.Direct, v.Direct, bounded: true).Close();                                      // :828
        Text(N.NextRefresh, v.NextRefresh, bounded: true).Close();                            // :829
        Button(N.BuyRefresh, v.RefreshButton).Close();                                        // :830
        Text(N.Progress, v.Progress, bounded: true).Close();                                  // :831
        Open("Widget", "PanelEmpty", N.SkarnPanel);                                           // :832
        Open("ImageBox", "ImageBox", N.SkarnPortrait).Close();                                // :833
        Text(N.SkarnPlaceholder, v.SkarnPlaceholder).Prop("TextAlign", "Center").Close();     // :834-835
        Text(N.SkarnState, v.SkarnState).Prop("TextAlign", "HCenter").Close();                // :836-837
        b.Close();   // Skarn panel
        string[] divisions = ["Easy", "Medium", "Hard"];                                      // :844
        for (int i = 0; i < 3; ++i) Button(N.Division(i), divisions[i]).Close();              // :845-847
        for (int s = 0; s < 5; ++s)                                                           // :849-872
        {
            var card = s < v.Cards.Count ? v.Cards[s] : null;
            Open("Widget", "PanelEmpty", N.Card(s));                                          // :852
            Open("Widget", "PanelEmpty", N.Accent(s)).Prop("NeedMouse", "false");             // MakeAccent :458-459
            for (int i = 0; i < 16; ++i) White(N.Fade(s, i)).Prop("Alpha", F(.12f * (16 - i) / 16f)).Close();   // :460-463
            for (int i = 0; i < 4; ++i) White(N.Border(s, i)).Prop("Alpha", i == 0 ? "0.9" : "0.35").Close();   // :464-467
            b.Close();   // accent
            Text(N.Heading(s), card?.Heading ?? "").Close();                                  // :854
            for (int m2 = 0; m2 < 4; ++m2)
            {
                var p = card is not null && m2 < card.Portraits.Count ? card.Portraits[m2] : null;
                Open("ImageBox", "ImageBox", N.Portrait(s, m2)).Close();                     // :857-860 (hidden until RefreshCard; the bind decides)
                Text(N.Placeholder(s, m2), p?.Placeholder ?? "").Prop("TextAlign", "Center").Close();   // :861-863
                Text(N.Name(s, m2), p?.Name ?? "").Prop("TextAlign", "HCenter").Prop("TextColour", C(.94f, .80f, .48f)).Close();   // :864-867
                Text(N.Stats(s, m2), p?.Stats ?? "").Prop("TextAlign", "HCenter").Prop("TextColour", C(.78f, .80f, .82f)).Close();   // :868-871
            }
            Text(N.Terms(s), card?.Terms ?? "").Close();                                      // :873
            Text(N.Text(s), card?.Text ?? "").Close();                                        // :874
            Button(N.CardBook(s), "Book challenge").UserString("slot", s.ToString(CultureInfo.InvariantCulture)).Close();   // :875
            b.Close();   // card
        }
        b.Close();   // challenges
        Text(N.Status, v.Feedback, bounded: true).Close();                                    // :842
        Button(N.Book, v.BookingButton).Close();                                              // :848
        // :877-881 the tip background (an overlapped node of the client, made last: drawn above the cards), dark and opaque, hidden; its text and the
        // seven Skarn lines (bounded, Passive, hidden)
        White(N.TipBackground).Prop("Colour", C(.06f, .06f, .06f)).Prop("Alpha", "1").Prop("Visible", "false");
        Text(N.FighterTip, "", bounded: true).Prop("NeedMouse", "false").Close();
        for (int i = 0; i < 7; ++i) Text(N.SkarnTip(i), "", bounded: true).Prop("Visible", "false").Prop("NeedMouse", "false").Close();
        b.Close();   // tip background
        b.Close();   // window
        return b;
    }

    // ---------- the C++ catch blocks ----------

    /// <summary>The framework could not register (EnsureWindow) or Replace (the rows' rebuild inside Tick) the layout: the C++ error line. The framework
    /// reports the window closed to the sim half when it was asked open and tries again at the next open request.</summary>
    static void Failed(string what, Exception e)
    {
        if (what == "register" && resource is not null)
            ModLog.Error("Proving Grounds: town registry creation failed: " + e.Message);   // :888 (AbandonWindow :890)
        else
            ModLog.Error("Proving Grounds: town registry refresh failed: " + e.Message);    // :955 (AbandonWindow)
    }

    // ---------- bind ----------

    /// <summary>What one bind writes for a layout: made when the model, view, font or selected offer changes, applied every frame (no allocation in the
    /// steady state).</summary>
    sealed class Plan
    {
        public UiRect Root;
        public readonly List<(string Name, (float W, float H) Canvas)> Canvas = [];
        public readonly List<(string Name, UiRect Fraction)> Sizes = [];
        public readonly List<(string Name, string Text)> Texts = [];
        public readonly List<string> Hidden = [], Disabled = [];
        public readonly List<(string Name, IReadOnlyDictionary<string, string> Props)> Props = [], EditColours = [];
        public readonly List<(string Image, CharacterId Who, IReadOnlyDictionary<string, string>? Alpha, string? Placeholder)> Faces = [];
        public TownWindowLayout Layout = null!;
    }

    static Plan? plan;
    static (PgUiModel? Model, TownWindowLayout? Layout, int Selected) planKey;
    /// <summary>The layout of the model shown now, and what it was computed for.</summary>
    static (PgUiModel? Model, (int W, int H) View, int Font, TownWindowLayout? Layout) current;

    /// <summary>C++ Tick's LayoutTownPanel (:952) for the model shown now; the last one when it is the same computation.</summary>
    static TownWindowLayout LayoutFor(PgUiModel? m, TownArenaUiView<CharacterId> v)
    {
        var view = View();
        int font = ScreenHost.FontHeight;
        if (ReferenceEquals(current.Model, m) && current.View == view && current.Font == font && current.Layout is { } l) return l;
        l = Compute(v, font);
        current = (m, view, font, l);
        sidebarBar = l.Sidebar.VBar;   // the bars MyGUI keeps for the next layout pass of the shown window
        rosterBar = l.Roster.VBar;
        challengesVBar = l.Challenges.VBar;
        challengesHBar = l.Challenges.HBar;
        return l;
    }

    // the creation tree: each widget's parent ("" = the window's client; a scroll view = its canvas)
    static string ParentOf(string name, TownArenaUiView<CharacterId> v)
    {
        if (name is N.Metrics or N.Sidebar or N.Challenges or N.Status or N.Book or N.TipBackground) return "";
        if (name is N.Team or N.RosterHint or N.Roster) return N.Sidebar;
        if (name == N.EmptyRoster || N.TryRow(name, out _)) return N.Roster;
        if (name is N.Direct or N.NextRefresh or N.BuyRefresh or N.Progress or N.SkarnPanel || N.TryDivision(name, out _)) return N.Challenges;
        if (name is N.SkarnPortrait or N.SkarnPlaceholder or N.SkarnState) return N.SkarnPanel;
        if (name == N.FighterTip || name.StartsWith("PG_TownSkarnTip", StringComparison.Ordinal)) return N.TipBackground;
        for (int i = 0; i < v.Rows.Count; ++i)
        {
            if (name == N.RowStatusFill(i)) return N.RowStatusBack(i);
            if (name == N.RowPortrait(i) || name == N.RowText(i) || name == N.RowStatusBack(i)) return N.Row(i);
            for (int e = 0; e < 4; ++e) if (name == N.RowBorder(i, e)) return N.Row(i);
        }
        for (int s = 0; s < 5; ++s)
        {
            string card = N.Card(s);
            if (name == card) return N.Challenges;
            if (!name.StartsWith(card + "_", StringComparison.Ordinal)) continue;
            return name.StartsWith(card + "_Fade", StringComparison.Ordinal) || name.StartsWith(card + "_Border", StringComparison.Ordinal) ? N.Accent(s) : card;
        }
        return "";
    }

    // C++ StatusColour (:265-269), the rarity colours of LayoutAccent (:482-485) and the unique-legendary border (:486), RefreshSkarn's green / red
    static readonly IReadOnlyDictionary<string, string> Green = Prop("Colour", C(.18f, .72f, .28f)), Orange = Prop("Colour", C(.95f, .52f, .12f)),
        Red = Prop("Colour", C(.78f, .16f, .12f));
    static readonly IReadOnlyDictionary<string, string>[] Rarity =
        [Prop("Colour", C(.22f, .52f, .95f)), Prop("Colour", C(.22f, .76f, .36f)), Prop("Colour", C(.65f, .34f, .92f)), Prop("Colour", C(1f, .49f, .12f))];
    static readonly IReadOnlyDictionary<string, string> Legendary = Prop("Colour", C(1f, .78f, .20f));
    internal static readonly string GoodText = C(.25f, .82f, .36f), BadText = C(.88f, .20f, .16f);
    static readonly IReadOnlyDictionary<string, string> GoodState = Prop("TextColour", GoodText), BadState = Prop("TextColour", BadText);
    static readonly IReadOnlyDictionary<string, string> NameColour = Prop("TextColour", C(.94f, .80f, .48f)), StatsColour = Prop("TextColour", C(.78f, .80f, .82f));
    static readonly Dictionary<float, IReadOnlyDictionary<string, string>> alphas = [];

    static IReadOnlyDictionary<string, string> Prop(string key, string value) => new Dictionary<string, string>(StringComparer.Ordinal) { [key] = value };

    static readonly IReadOnlyDictionary<string, string>[] bars =
        [.. new[] { (false, false), (true, false), (false, true), (true, true) }.Select(b => (IReadOnlyDictionary<string, string>)new Dictionary<string, string>(StringComparer.Ordinal)
            { ["VisibleVScroll"] = b.Item1 ? "true" : "false", ["VisibleHScroll"] = b.Item2 ? "true" : "false" })];
    static IReadOnlyDictionary<string, string> Bars(bool v, bool h) => bars[(v ? 1 : 0) + (h ? 2 : 0)];

    static IReadOnlyDictionary<string, string> Alpha(float a)
    {
        if (!alphas.TryGetValue(a, out var d)) alphas[a] = d = Prop("Alpha", F(a));
        return d;
    }

    static Plan MakePlan(TownArenaUiView<CharacterId> v, TownWindowLayout l)
    {
        var p = new Plan { Layout = l, Root = new UiRect(l.Window.X, l.Window.Y, l.Window.W, l.Window.H) };
        foreach (var (name, rect) in l.Rects)
        {
            if (name == N.TipBackground || name == N.FighterTip || name.StartsWith("PG_TownSkarnTip", StringComparison.Ordinal)) continue;   // the tip: per frame
            p.Sizes.Add((name, Fraction(rect, ParentSize(l, ParentOf(name, v)))));
        }
        foreach (var (name, c) in l.Canvas) p.Canvas.Add((name, (c.W, c.H)));
        // task 7.6 review: the bars MyGUI shows after the C++'s layout (TownWindowLayout's settled bars: a canvas set to the view needs none). Meitou
        // starts a scroll view's fit with both bars shown and drops one only when its canvas fits the client with both bars (UiDocument.Widgets.cs
        // FitScrollBars :559-587), so a canvas exactly the size of the bar-less view keeps both; the flags say which bars the layout settled on
        p.Props.Add((N.Sidebar, Bars(l.Sidebar.VBar, false)));
        p.Props.Add((N.Roster, Bars(l.Roster.VBar, false)));
        p.Props.Add((N.Challenges, Bars(l.Challenges.VBar, l.Challenges.HBar)));
        foreach (var n in l.Hidden)
            if (n != N.TipBackground && !n.StartsWith("PG_TownSkarnTip", StringComparison.Ordinal)) p.Hidden.Add(n);   // the tip's visibility is per frame

        // captions (Tick :917-933, RefreshRoster :445-451, RefreshSkarn :347-348, RefreshCard / RefreshSelectionPrompt, Feedback)
        p.Texts.Add((N.Team, v.Squad));
        p.Texts.Add((N.Direct, v.Direct));
        p.Texts.Add((N.NextRefresh, v.NextRefresh));
        p.Texts.Add((N.BuyRefresh, v.RefreshButton));
        p.Texts.Add((N.Progress, v.Progress));
        p.Texts.Add((N.Status, v.Feedback));
        p.Texts.Add((N.Book, v.BookingButton));
        p.Texts.Add((N.SkarnPlaceholder, v.SkarnPlaceholder));
        p.Texts.Add((N.SkarnState, v.SkarnState));
        p.EditColours.Add((N.SkarnState, v.SkarnUnlocked ? GoodState : BadState));   // :349 (an EditBox: ScreenBinding.EditTextColour)
        for (int i = 0; i < v.Rows.Count; ++i)
        {
            var r = v.Rows[i];
            p.Texts.Add((N.RowText(i), r.Caption));
            p.Props.Add((N.Row(i), NativeUI.SelectedState(r.Selected)));   // SelectionVisuals :256
            p.Faces.Add((N.RowPortrait(i), r.Character, null, null));
            if (r.StatusVisible)   // RefreshStatusBar :278 setColour(StatusColour(band))
                p.Props.Add((N.RowStatusFill(i), r.Band == RosterStatusPolicy.Band.Green ? Green : r.Band == RosterStatusPolicy.Band.Orange ? Orange : Red));
        }

        // buttons (Tick :922-948)
        if (!v.RefreshEnabled) p.Disabled.Add(N.BuyRefresh);
        if (!v.BookingEnabled) p.Disabled.Add(N.Book);
        for (int i = 0; i < 3; ++i)
        {
            p.Props.Add((N.Division(i), NativeUI.SelectedState(v.Division == i)));
            if (!v.DivisionsEnabled) p.Disabled.Add(N.Division(i));
        }

        // the cards
        for (int s = 0; s < 5 && s < v.Cards.Count; ++s)
        {
            var c = v.Cards[s];
            if (!c.Visible) continue;   // a hidden card keeps what it showed (:942-943)
            p.Texts.Add((N.Heading(s), c.Heading));
            p.Texts.Add((N.Text(s), c.Text));
            p.Texts.Add((N.Terms(s), c.Terms));
            p.Props.Add((N.CardBook(s), NativeUI.SelectedState(!v.SelectionPrompt && selectedOffer == s)));   // :315 / :947, :941
            if (!c.BookEnabled && !v.SelectionPrompt) p.Disabled.Add(N.CardBook(s));                         // :948
            for (int m = 0; m < c.Portraits.Count && m < 4; ++m)
            {
                var pv = c.Portraits[m];
                p.Texts.Add((N.Placeholder(s, m), pv.Placeholder));
                p.Texts.Add((N.Name(s, m), pv.Name));
                p.Texts.Add((N.Stats(s, m), pv.Stats));
                p.EditColours.Add((N.Name(s, m), NameColour));     // :866 setTextColour(.94, .80, .48) (the XML has it; Meitou draws it on the part)
                p.EditColours.Add((N.Stats(s, m), StatsColour));   // :870 setTextColour(.78, .80, .82)
                if (pv.PortraitVisible) p.Faces.Add((N.Portrait(s, m), pv.Character, Alpha(pv.Alpha), N.Placeholder(s, m)));   // :537-538 setVisible, setAlpha
            }
            if (!v.SelectionPrompt && c.Rarity >= 0 && !l.Hidden.Contains(N.Accent(s)))   // LayoutAccent :484-492
            {
                var colour = Rarity[Math.Min(3, c.Rarity)];
                for (int i = 0; i < 16; ++i) p.Props.Add((N.Fade(s, i), colour));
                for (int i = 0; i < 4; ++i) p.Props.Add((N.Border(s, i), c.UniqueLegendary ? Legendary : colour));
            }
        }
        if (v.SkarnKey.Length > 0) p.Faces.Add((N.SkarnPortrait, v.Skarn, Alpha(v.SkarnUnlocked ? 1f : .28f), N.SkarnPlaceholder));   // RefreshSkarn :344-347
        return p;
    }

    /// <summary>The canvases' offsets as the C++ moves them: after each new layout the offset (the player's, read back) clamped to the content
    /// (LayoutRoster :655-656, LayoutSidebar :664-665, LayoutTownPanel :779-781). Show keeps them (the C++ window is only hidden at Close).</summary>
    static void FollowScroll(TownWindowLayout l)
    {
        if (ReferenceEquals(clampedFor, l) || ScreenHost.Host is not { } host || !host.IsRegistered(TownArenaUISim.ScreenId)) return;
        string id = TownArenaUISim.ScreenId;
        clampedFor = l;
        host.SetScroll(id, N.Sidebar, l.Sidebar.ClampY((int)host.GetScroll(id, N.Sidebar)));
        host.SetScroll(id, N.Roster, l.Roster.ClampY((int)host.GetScroll(id, N.Roster)));
        host.SetScroll(id, N.Challenges, l.Challenges.ClampY((int)host.GetScroll(id, N.Challenges)));
        host.SetScroll(id, N.Challenges + "@h", l.Challenges.ClampX((int)host.GetScroll(id, N.Challenges + "@h")));
    }

    static void Bind(PgUiModel? m, ScreenBinding b)
    {
        if (!wasOpen) { ResetWindowState(); wasOpen = true; }
        if (Of(m) is not { } v) return;
        var size = ScreenHost.ViewSize;
        if (size.Width <= 1 || size.Height <= 1) return;   // no view yet (a UI reload): the XML's own positions stand
        var l = LayoutFor(m, v);
        FollowScroll(l);
        var key = (m, (TownWindowLayout?)l, selectedOffer);
        if (plan is null || !planKey.Equals(key))
        {
            plan = MakePlan(v, l);
            planKey = key;
        }
        Apply(plan, b);
        BindTip(v, l, b);
    }

    static void Apply(Plan p, ScreenBinding b)
    {
        foreach (var (n, t) in p.Texts) b.Text(n, t);
        foreach (var (n, f) in p.Sizes) b.Size(n, f);
        foreach (var n in p.Hidden) b.Hide(n);
        foreach (var n in p.Disabled) b.Disable(n);
        foreach (var (n, props) in p.Props) b.Props(n, props);
        foreach (var (n, props) in p.EditColours) b.EditTextColour(n, props);
        foreach (var (image, who, alpha, placeholder) in p.Faces)
        {
            if (!b.Portrait(image, who))   // BindPortrait :337-341: no portrait (here: no free cell) = hidden
            {
                b.Hide(image);
                // task 7.6 review: the member's placeholder ("Portrait unavailable" / Skarn's) shows instead, as the C++ shows it when it has no
                // portrait (:539 placeholders->setVisible(member < portraitCount && !portraitVisible), RefreshSkarn :348)
                if (placeholder is not null) { b.Bindings.Hidden.Remove(placeholder); b.Props(placeholder, Shown); }
                continue;
            }
            if (alpha is not null) b.Props(image, alpha);
        }
        foreach (var (n, c) in p.Canvas) b.Bindings.Canvas[n] = c;
        b.Place(N.Window, p.Root);
    }

    // ---------- the tip (PortraitTip :184-221, PlaceTip :171-183, the Tick's HideTip :953-954) ----------

    static readonly IReadOnlyDictionary<string, string> ShownGood = new Dictionary<string, string>(StringComparer.Ordinal) { ["Visible"] = "true", ["TextColour"] = GoodText };
    static readonly IReadOnlyDictionary<string, string> ShownBad = new Dictionary<string, string>(StringComparer.Ordinal) { ["Visible"] = "true", ["TextColour"] = BadText };
    static (int W, int H, LayoutRect[] Lines, string[] Key, int ClientW, int ClientH, int Font)? skarnTipSize;   // keyed on the model's array (never changed once published)

    /// <summary>The tip's owner under the pointer and its text: the Skarn panel's tip (skarnTip "1", :838-840), a card member's fighter tip (SetTip
    /// :519-520, empty = none: HideTip :216), else null.</summary>
    static (string Owner, string? Fighter)? TipAt(TownArenaUiView<CharacterId> v, string? widget)
    {
        if (widget is null) return null;
        if (N.IsSkarnTipOwner(widget)) return (widget, null);
        if (!N.TryTipOwner(widget, out int s, out int m) || s >= v.Cards.Count) return null;
        var c = v.Cards[s];
        if (!c.Visible || m >= c.Portraits.Count || c.Portraits[m].Tip.Length == 0) return null;
        return (widget, c.Portraits[m].Tip);
    }

    static void BindTip(TownArenaUiView<CharacterId> v, TownWindowLayout l, ScreenBinding b)
    {
        if (ScreenHost.Host is not { } host) return;
        var at = ScreenHost.Pointer;
        var owner = TipAt(v, tip.Hover(host, at));
        if (!tip.Follow(owner?.Owner, at) || owner is not { } o) return;
        var origin = tip.ClientOrigin(l);
        int font = ScreenHost.FontHeight;
        int w, h;
        if (o.Fighter is { } fighter)
            (w, h) = tip.BindFighterTip(b, fighter, l.ClientWidth, l.ClientHeight, font);   // Wrap(fighterTip, 0, 0, width, ...); the background its size (:218)
        else
        {
            var k = v.SkarnTip;
            if (skarnTipSize is not { } st || !ReferenceEquals(st.Key, k) || st.ClientW != l.ClientWidth || st.ClientH != l.ClientHeight || st.Font != font)
            {
                text.UseCurrent();
                var (sw, sh, lines) = TownArenaUILayout<CharacterId>.SkarnTip(l.ClientWidth, l.ClientHeight, font, [.. v.SkarnTip.Select(t => t ?? "")], measure);
                skarnTipSize = st = (sw, sh, lines, k, l.ClientWidth, l.ClientHeight, font);
            }
            (w, h) = (st.W, st.H);
            b.Hide(N.FighterTip);   // :192 fighterTip->setVisible(false)
            for (int i = 0; i < 7; ++i)
            {
                b.Text(N.SkarnTip(i), v.SkarnTip[i] ?? "");
                b.EditTextColour(N.SkarnTip(i), v.SkarnTipGood[i] ? ShownGood : ShownBad);
                b.Size(N.SkarnTip(i), Fraction(st.Lines[i], (w, h)));
            }
        }
        tip.Place(b, l.ClientWidth, l.ClientHeight, font, at, origin, w, h);
    }

    // ---------- clicks ----------

    static PgCommand? Click(PgUiModel? m, UiClickData c)
    {
        if (Of(m) is not { } v) return null;
        string w = c.Widget;
        if (c.Kind == UiEventKind.Release)   // SelectOffer :308-311: a card part's click (not a button: its left release over it)
        {
            if (!v.SelectionPrompt && N.TryOfferPart(w, out int slot) && slot < v.Cards.Count && v.Cards[slot].Visible)
                selectedOffer = slot;
            return null;
        }
        if (c.Kind != UiEventKind.Click) return null;
        var system = ProvingGroundsSystem.Current;
        gate.FollowWorld(system);
        // the protocol counter from the NEWEST published model (as the recruitment gate); what the button showed from the drawn one (m)
        var newest = system?.Snapshot?.Model(TownArenaUISim.ScreenId);
        gate.FollowSerial(newest?.RequestSerial ?? m!.RequestSerial);   // a new Show / Close starts over (a lost answer recovers by reopening)
        long published = Of(newest)?.LastSeq ?? v.LastSeq;
        if (N.TryRow(w, out int index))   // RowReleased :280-297: the row's fighter (its identity), never its index
            return index < v.Rows.Count ? new TownSelectFighter(v.Rows[index].Id, gate.Next(published)) { Tick = 0 } : null;
        if (N.TryDivision(w, out int division)) return new TownSetDivision(division) { Tick = 0 };   // Division :331-333
        if (N.TryCardBook(w, out int card))   // Challenge :321-326
        {
            if (card >= v.Cards.Count) return null;
            var cv = v.Cards[card];
            long seq = gate.Send(published);
            return seq == 0 ? null : new TownBookChallenge(card, seq, cv.OfferKey, cv.Quote.BuyIn, cv.Quote.Payout) { Tick = 0 };
        }
        if (w == N.Book)   // Book :312-320
        {
            long seq = gate.Send(published);
            return seq == 0 ? null : new TownBookDirect(seq, v.BookingButton) { Tick = 0 };
        }
        if (w == N.BuyRefresh)   // BuyRefresh :327-330
        {
            long seq = gate.Send(published);
            return seq == 0 ? null : new TownBuyRefresh(seq, v.RefreshCost) { Tick = 0 };
        }
        return null;
    }

    // ---------- PgCheck ui ----------

    /// <summary>PgCheck ui only: every window-side state back to a fresh process's (no layout built yet).</summary>
    internal static void ResetForCheck()
    {
        ResetWindowState();
        liveModel = null;
        selectedOffer = displayedDivision = -1;
        sidebarBar = rosterBar = challengesVBar = challengesHBar = false;
        clampedFor = null; tip.ForgetOrigin(); facesFor = default; skarnTipSize = null;
    }

    /// <summary>PgCheck ui only (as the recruitment window's): the plan's face decisions for a model (the offline check has no portrait cells, so the
    /// bound Hide cannot tell a plan that hides a portrait from one with no cell): each portrait widget given a face, with its alpha.</summary>
    internal static IReadOnlyList<(string Image, string? Alpha)> FacesForCheck(PgUiModel? m)
    {
        var v = Of(m) ?? Created;
        var p = MakePlan(v, Compute(v, NativeUI.DefaultFontHeight));
        return [.. p.Faces.Select(f => (f.Image, f.Alpha?.GetValueOrDefault("Alpha")))];
    }

    /// <summary>PgCheck ui only: the selected offer the next plans use (SelectOffer's state).</summary>
    internal static void SelectOfferForCheck(int slot) => selectedOffer = slot;

    static TownArenaPortraitView<CharacterId> CheckMember(int slot, string name, bool portrait, bool unavailable, bool martial) =>
        new(new CharacterId(slot, 0), portrait ? "key" + slot.ToString(CultureInfo.InvariantCulture) : "", portrait, !portrait,
            "Portrait unavailable", unavailable ? .32f : 1f, true, name, true,
            martial ? "ATK 41 | DOD 33 | STR 28\nTOU 30 | DEX 25" : "ATK 41 | DEF 33 | STR 28\nTOU 30 | DEX 25 | WPN 47",
            name + "\nBrawler\n\nAttack 41 | Defence 33");

    static TownArenaCardView<CharacterId> CheckCard(int slot, bool visible, int rarity, bool legendary, string heading, params TownArenaPortraitView<CharacterId>[] members) =>
        new(slot, visible, heading, rarity < 0 ? "Looking for a new fighter...\n" : "", rarity < 0, true,
            "Buy-in: 250 Cats\nOdds: 1.20x\nWin returns: 300 Cats\nMarks: 0.75x (division 0.75x / rarity 1.00x / config 1.00x)", true, rarity >= 0,
            Math.Max(rarity, 0), legendary, Math.Max(1, Math.Min(4, members.Length)), members,
            new TownChallengeBuyInPolicy.Quote { BuyIn = 250, Payout = 300 }, "offer" + slot.ToString(CultureInfo.InvariantCulture));

    static TownArenaUiModel CheckModel(int rows, bool prompt, int division = 0, bool skarn = false, bool unlocked = false, string feedback = "",
        int selected = 0, bool locked = false)
    {
        var v = new TownArenaUiView<CharacterId>
        {
            Open = true, RequestSerial = 1, Status = feedback, Feedback = feedback,
            Squad = "Selected team: " + (prompt ? 0 : selected).ToString(CultureInfo.InvariantCulture) + " / 3" + (locked ? " - locked" : ""),
            Direct = "Direct 1v1: an Easy town fighter for your selected fighter.", NextRefresh = "Next Refresh: 06:00 (in 4h 12m)",
            RefreshCost = 1000, RefreshButton = "Buy Refresh (1000 Cats)", RefreshEnabled = !locked, BookingButton = locked ? "Cancel booking" : "Book direct 1v1",
            BookingEnabled = locked || selected == 1, DivisionsEnabled = !locked, Division = division, SelectionPrompt = prompt,
            Progress = "Challenge wins: 3 | Skarn unlock: 2 / 5 unique fighters defeated", SkarnUnlocked = unlocked,
            SkarnState = unlocked ? "UNLOCKED" : "LOCKED", SkarnPlaceholder = unlocked ? "SKARN\nPORTRAIT UNAVAILABLE" : "SKARN\nLOCKED",
            Skarn = skarn ? new CharacterId(40, 0) : CharacterId.None, SkarnKey = skarn ? "skarn" : "",
        };
        string[] names = ["Ressa Vane", "Bum-Per", "Senn", "Torka", "Veyr the Gilded"];
        v.SkarnTip[0] = unlocked ? "Skarn unlocked" : "Skarn locked"; v.SkarnTipGood[0] = unlocked;
        for (int i = 0; i < 5; ++i) { v.SkarnTip[i + 1] = (i < 2 ? "Completed - " : "Incomplete - ") + names[i]; v.SkarnTipGood[i + 1] = i < 2; }
        v.SkarnTip[6] = unlocked ? "Skarn may now join the fighter pool." : "Defeat every unique fighter to unlock Skarn."; v.SkarnTipGood[6] = unlocked;
        for (int i = 0; i < rows; ++i)
            v.Rows.Add(new TownArenaRowView<CharacterId>("c" + i.ToString(CultureInfo.InvariantCulture), new CharacterId(i + 1, 0),
                "Fighter " + (i + 1).ToString(CultureInfo.InvariantCulture) + (i < selected ? "\nSelected" : ""), i < selected, i % 3 != 2,
                (i % 4 + 1) / 4f, i % 3 == 0 ? RosterStatusPolicy.Band.Green : i % 3 == 1 ? RosterStatusPolicy.Band.Orange : RosterStatusPolicy.Band.Red));
        if (prompt)
        {
            v.Cards.Add(new TownArenaCardView<CharacterId>(0, true, "Select a recovered fighter",
                "Choose 1-3 recovered squad fighters to reveal the current challenge cards.", true, false, "", false, false, -1, false, 0, [],
                new TownChallengeBuyInPolicy.Quote()));
            for (int s = 1; s < 5; ++s) v.Cards.Add(CheckCard(s, false, 0, false, ""));
        }
        else
        {
            v.Cards.Add(CheckCard(0, true, 0, false, "Common | Easy | Duel 1v1", CheckMember(10, "Dust Raider", true, false, false)));
            v.Cards.Add(CheckCard(1, true, 1, false, "Uncommon | Easy | Teams 2v2", CheckMember(11, "Hacker", true, false, true), CheckMember(12, "Polearm", false, false, false)));
            v.Cards.Add(CheckCard(2, true, 2, false, "Rare | Easy | Teams 1v3", CheckMember(13, "A", true, false, false), CheckMember(14, "B", true, false, false), CheckMember(15, "C", false, false, true)));
            v.Cards.Add(CheckCard(3, division != 1, 3, true, "Legendary | Easy | Duel 1v1", CheckMember(16, "Veyr the Gilded", true, false, false)));
            v.Cards.Add(CheckCard(4, true, -1, false, "Searching", CheckMember(17, "Unavailable", false, true, false)));
        }
        return new TownArenaUiModel(v) { RequestSerial = 1, RequestOpen = true };
    }

    static IReadOnlyList<PgUiModel?> CheckModels()
    {
        string longFeedback = string.Concat(Enumerable.Repeat("Assign 1-3 available squad fighters before booking. ", 6));
        return
        [
            null,
            CheckModel(0, prompt: true),                                       // no squad fighter: the empty label and the prompt card
            CheckModel(3, prompt: true),                                       // three fighters, none selected: the prompt
            CheckModel(3, prompt: false, selected: 1),                         // the five cards, Easy (Fit grid), one fighter: Book direct 1v1 enabled
            CheckModel(3, prompt: false, division: 1, selected: 2, skarn: true),   // Medium, a card hidden, two selected, Skarn's portrait
            CheckModel(3, prompt: false, division: 2, selected: 3, locked: true, skarn: true, unlocked: true, feedback: longFeedback),   // Hard: one row, locked
            CheckModel(8, prompt: false, selected: 1),                         // eight fighters: the roster canvas scrolls
        ];
    }
}
