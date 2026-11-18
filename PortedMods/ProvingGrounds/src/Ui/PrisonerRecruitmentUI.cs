// Port of upstream/ProvingGrounds/plugin/src/PrisonerRecruitmentUI.h/.cpp (Proving Grounds by Shutori, GPLv3). Task 6.4a: the rules half
// (Core/Glue/PrisonerRecruitmentUILogic.cs) and the sim half (src/Game/PrisonerRecruitmentUISim.cs) behind the frame-loop object
// PrisonerRecruitmentUI below. Task 6.4b (PA §2.19; CAP 7.0-7.4; spikes S6, S7): the window half PrisonerRecruitmentScreen: the layout built call by
// call from EnsureWindow (:486-559) and RebuildRows (:174-210) with LayoutBuilder / NativeUI (same widget types, skins, names, captions and
// alignments), every rectangle from FitWindowToScreen / LayoutWindow / LayoutRows / LayoutDetails (Core/Glue/PrisonerRecruitmentUILayout.cs,
// tested), the portraits, the scroll canvases and the clicks. Window thread only: the window reads the published PrisonerRecruitmentUiModel and
// sends PgCommands. Meitou 0.17.3 (tag v0.17.3, fa34a1b).
//
// Engine calls of the C++ window and what replaces them:
//   createWidgetReal / createWidget (EnsureWindow, RebuildRows)        -> LayoutBuilder XML (Build) -> ScreenHost (Register / Replace). The shape key
//                                                                         is the row count (the rows are named by their index); everything else is
//                                                                         bound per frame from a plan made once per layout
//   setCoord / setSize (LayoutWindow, LayoutRows, LayoutDetails,        -> PrisonerRecruitmentUILayout's rectangles, written into the XML and bound as
//     WrappedHeight)                                                      fractions of their parent (UiBindings.RelativePositions)
//   g_window->setCoord (FitWindowToScreen, each Tick)                   -> the root's bound screen rectangle (88 % of the view, centred)
//   setCaption (UpdateDetails, the rows' names and Marks)               -> UiBindings Text from the model (the sim half's captions)
//   setEnabled (g_recruit) / setStateSelected (the rows)                -> UiBindings Disabled / NativeUI.SelectedState
//   setVisible (g_metrics :509, g_portrait :281, BindPortrait :115/121) -> Visible false in the XML (the metrics label); the detail portrait hidden
//                                                                         while the model has none; a portrait with no free cell hidden
//   PortraitManager::setImageWidget (BindPortrait :109-122)             -> Portraits.Bind (spike S7)
//   ScrollView::setCanvasSize / setViewOffset (Show :597-598, the       -> UiBindings.Canvas; MyGuiScreenHost.SetScroll / GetScroll: 0 at each Show,
//     layouts' clamps :333-336, :361-364)                                 then clamped to the content at each new layout
//   setNeedMouseFocus / setNeedKeyFocus(false) (Passive :80-91)         -> NeedMouse false on the rows' portrait, name and Marks and the detail
//                                                                         portrait (a press there reaches the row button, as MyGUI passes it on)
//   OnRowClicked / OnCloseClicked / OnRecruitClicked                    -> RecruitmentSelect / RecruitmentClose / RecruitPrisoner (task 6.4a) with
//                                                                         the shown price and a sequence above the model's LastSeq
//                                                                         (RecruitmentRequestGate); none while the last one is unanswered
//   eventWindowButtonPressed "close" (:480-484), Escape                  -> the framework's Closed report (the sim half's OnClosedFromWindow)
//     (ArenaUI.cpp:2994-2997)
//   eventMouseButtonClick (every button)                                 -> UiClick Click (left release, task 4.4b; PrisonerRecruitmentUINames.ButtonEvent)
//   getTextSize / getFontHeight                                          -> RewardsTextMeasure over src/Ui/TextMeasure.cs / ScreenHost.FontHeight
//   the catch blocks of EnsureWindow (:545-558) and RefreshAll (:581-585) -> PgScreen.Failed: the C++ error line; the framework reports the
//                                                                         window closed when it was asked open (ScreenFrame) and tries again at the
//                                                                         next open request (the C++ next Show)
using System.Globalization;
using Meitou.Data.Ui;
using Meitou.Simulation;
using ProvingGrounds.Core.Glue;
using ProvingGrounds.Engine;
using ProvingGrounds.Game;
using N = ProvingGrounds.Core.Glue.PrisonerRecruitmentUINames;

namespace ProvingGrounds.Ui;

/// <summary>The recruitment window's frame-loop object (PrisonerRecruitmentUI.h:5-9: Show / Close / Tick / IsVisible / AbandonWorldState) over its
/// sim half. The window itself is <see cref="PrisonerRecruitmentScreen"/>.</summary>
sealed class PrisonerRecruitmentUI(PrisonerRecruitmentUISim sim)
{
    internal PrisonerRecruitmentUISim Sim => sim;

    // C++: PrisonerRecruitmentUI.cpp:622-636, called from ArenaUI.cpp:3171 (ForgottenGUI_update_hook)
    internal void Tick(World world) => sim.Tick(world);

    // C++: PrisonerRecruitmentUI.cpp:591-606, called from the Arena window's "Recruit Prisoners" (ArenaUI.cpp:1550, task 4.5a)
    internal void Show(World world) => sim.Show(world);

    // C++: PrisonerRecruitmentUI.cpp:608-615
    internal void Close(World world) => sim.Close(world);

    // C++: PrisonerRecruitmentUI.cpp:617-620
    internal bool IsVisible => sim.IsVisible;

    // C++: PrisonerRecruitmentUI.cpp:638-647, called by ArenaUI::AbandonWorldState and ShowFromRegistry (ArenaUI.cpp:3202, :3254, task 4.5a)
    internal void AbandonWorldState(World world) => sim.AbandonWorldState(world);
}

/// <summary>
/// The recruitment window <c>pg.recruitment</c> (task 6.4b), declared to the window framework (src/Ui/ScreenHost.cs). EnsureWindow's widgets and
/// RebuildRows' rows are one XML layout; the shape key is the row count (the framework adds the font height), so only a new number of prisoners
/// rebuilds and Replaces it. Everything else the C++ changes within one shape (captions, every rectangle, the detail portrait's visibility, the
/// button's enabled state, the selected row, the canvases, the window rectangle) is bound per frame from a plan made once per layout. The layout
/// follows the C++'s LayoutWindow rule: the status line is sized for the text it had at the last LayoutWindow (a Show, a new candidate list, a new
/// view or font), not for each new status text (RefreshAll :571-579).
/// </summary>
static class PrisonerRecruitmentScreen
{
    static readonly TextMeasure text = new();
    static readonly RewardsTextMeasure measure = new(text);   // the standard text, Kenshi_Button1 captions, the rows' painted text
    static bool wasOpen;

    static PgUiModel? liveModel;
    // the status text at the last LayoutWindow of the live model, and what decided it (Show count, row rebuild count, view, font)
    static string? windowStatus;
    static (int Shows, int RowBuilds, (int W, int H) View, int Font)? windowKey;
    // the scroll views' bars after the last layout (MyGUI keeps them until the next setCanvasSize)
    static bool rosterBar, detailBar;
    static readonly RecruitmentRequestGate gate = new();

    // the canvases' offsets: put to 0 at each Show (:597-598), clamped after each new layout (:333-336, :361-364)
    static int lastShows = -1;
    static RecruitmentWindowLayout? clampedFor;

    static PrisonerRecruitmentUiView<CharacterId>? Of(PgUiModel? m) => m is PrisonerRecruitmentUiModel r ? r.View : null;

    /// <summary>The creation-state view (EnsureWindow's captions) for a layout before any model.</summary>
    static readonly PrisonerRecruitmentUiView<CharacterId> Created = new PrisonerRecruitmentUILogic<CharacterId>().BuildView();

    internal static readonly PgScreen Declaration = new()
    {
        Id = PrisonerRecruitmentUISim.ScreenId,
        ButtonEvent = N.ButtonEvent,   // task 4.4b: PrisonerRecruitmentUI.cpp's bindings (every button eventMouseButtonClick)
        ShapeKey = ShapeKey,
        BeforeFrame = BeforeFrame,
        Layout = Build,
        Bind = Bind,
        Click = Click,
        WhileClosed = () => { if (wasOpen) ResetWindowState(); },
        NewWorld = _ => { ResetWindowState(); liveModel = null; windowStatus = null; windowKey = null; },   // a new world: no LayoutWindow state carries over
        Portraits = m => Of(m) is { } v ? Faces(v) : [],
        Failed = Failed,
        CheckModels = CheckModels,
    };

    /// <summary>Window-side state that must not outlive one opening of the window (called on close, a new world and the first bind after opening):
    /// the bind plan and the events the scroll offsets follow. The bars and the window status follow the window as MyGUI keeps them.</summary>
    static void ResetWindowState()
    {
        wasOpen = false;
        plan = null;
        planFor = null;
        lastShows = -1;
        clampedFor = null;
        current = default;
    }

    static IReadOnlyList<CharacterId> Faces(PrisonerRecruitmentUiView<CharacterId> v)
    {
        var list = new List<CharacterId>(v.Rows.Count + 1);
        foreach (var r in v.Rows) list.Add(r.Character);   // RebuildRows :194 BindPortrait(row.portrait, character)
        if (v.PortraitVisible && v.HasSelected) list.Add(v.Selected);   // UpdateDetails :273 BindPortrait(g_portrait, g_selected)
        return list;
    }

    // ---------- shape ----------

    /// <summary>The widget set: RebuildRows' rows (named by their index, :184-206); every other widget exists in every shape.</summary>
    static string ShapeKey(PgUiModel? m) => Of(m) is { } v ? "recruitment/rows " + v.Rows.Count.ToString(CultureInfo.InvariantCulture) : "recruitment/none";

    static (int W, int H) View()
    {
        var (w, h) = ScreenHost.ViewSize;
        return w <= 1 || h <= 1 ? (1920, 1080) : (w, h);
    }

    // the live model, and the status text LayoutWindow last used for it
    static void BeforeFrame()
    {
        var snap = ProvingGroundsSystem.Current?.Snapshot;
        gate.FollowWorld(ProvingGroundsSystem.Current);   // a new world starts the window's request numbers over
        liveModel = snap?.Model(PrisonerRecruitmentUISim.ScreenId);
        if (Of(liveModel) is not { } v) return;
        var key = (v.Shows, v.RowBuilds, View(), ScreenHost.FontHeight);
        if (windowKey == key) return;
        windowKey = key;          // Show (:602-603), a new candidate list (:571), a new client size or body height (:572-573): LayoutWindow
        windowStatus = v.Status;
    }

    /// <summary>The status text the layout sizes the status line for: the live model's at its last LayoutWindow, any other model's (PgCheck) its own.</summary>
    static string WindowStatusOf(PgUiModel? m, PrisonerRecruitmentUiView<CharacterId> v) =>
        ReferenceEquals(m, liveModel) && windowStatus is { } s ? s : v.Status;

    static RecruitmentWindowLayout Compute(PrisonerRecruitmentUiView<CharacterId> v, int font, string windowStatusText)
    {
        text.UseCurrent();
        var (w, h) = View();
        var input = new RecruitmentLayoutInput
        {
            ViewWidth = w, ViewHeight = h, FontHeight = font, WindowStatus = windowStatusText,
            RosterBarBefore = rosterBar, DetailBarBefore = detailBar,
        };
        return PrisonerRecruitmentUILayout<CharacterId>.Compute(v, input, measure);   // the bars it ends with are kept only by LayoutFor (the live window)
    }

    /// <summary>The C++ <c>resource</c> EnsureWindow was making when it threw (its catch blocks name it, :545-558); null while the rows are made
    /// (RebuildRows runs inside RefreshAll, whose catch says "refresh failed", :581-585).</summary>
    static string? resource = "Kenshi_WindowCX";

    // C++: PrisonerRecruitmentUI.cpp:486-559 EnsureWindow and :174-210 RebuildRows, in creation order; positions from PrisonerRecruitmentUILayout,
    // the captions of this model (UpdateDetails wrote them before the layout pass)
    static LayoutBuilder Build(PgUiModel? m, int font)
    {
        var v = Of(m) ?? Created;
        var l = Compute(v, font, WindowStatusOf(m, v));
        current = default;
        var b = new LayoutBuilder();
        LayoutRect R(string name) => l.Rects.TryGetValue(name, out var r) ? r : new LayoutRect(0, 0, 100, 24);
        LayoutBuilder Open(string type, string skin, string name)
        {
            var r = R(name);
            b.Open(type, skin, r.X, r.Y, r.W, r.H, name);
            return b;
        }
        LayoutBuilder Label(string name, string caption) => Open("TextBox", "Kenshi_TextboxStandardText", name).Prop("Caption", caption);   // NativeUI::Label
        LayoutBuilder Wrapped(string name, string caption) => Open("EditBox", "Kenshi_WordWrapEmpty", name).Prop("Caption", caption);     // NativeUI::WrappedLabel
        LayoutBuilder Button(string name, string caption) => Open("Button", "Kenshi_Button1", name).Prop("Caption", caption);             // NativeUI::Button
        void Scroll(string name)   // NativeUI::Scroll
        {
            var r = R(name);
            NativeUI.Scroll(b, r.X, r.Y, r.W, r.H, name);
        }

        resource = "Kenshi_WindowCX";   // :493
        var w = l.Window;
        // :496-500 createWidgetReal<Window>(resource, .06f, .06f, .88f, .88f, Align::Center, "Window", name); setCaption; setVisible(false) (the
        // framework opens it when the sim half asks); FitWindowToScreen's rectangle is bound (Bind)
        b.OpenReal("Window", resource, .06f, .06f, .88f, .88f, N.Window, "Center", "Window").Prop("Caption", v.Caption);
        Label(N.Metrics, v.Metrics).Prop("Visible", "false").Close();   // :507-509 (resource still Kenshi_WindowCX)
        resource = "Kenshi_WordWrap";   // :510
        Open("EditBox", resource, N.Subtitle).Prop("Caption", v.Subtitle).Close();   // :511-514
        Open("EditBox", resource, N.Status).Prop("Caption", v.Status).Close();       // :515-516 (UpdateDetails :294-296 writes its caption)
        Label(N.RosterHeading, v.RosterHeading).Close();   // :517-518
        Scroll(N.RosterScroll);   // :519-520
        string? created = resource;
        resource = null;   // RebuildRows :174-210 (each Show's RefreshCandidates when the candidates changed), inside the roster scroll
        foreach (var row in v.Rows)
        {
            int i = row.Index;
            Button(N.Row(i), "");   // :187 NativeUI::Button(g_rosterScroll, initial, name, "")
            // :191-207 the row's parts, made Passive (no mouse or key focus: MyGUI passes a press on them to the row)
            Open("ImageBox", "ImageBox", N.RowPortrait(i)).Prop("NeedMouse", "false").Close();
            Open("EditBox", "Kenshi_PaintedWordWrapEmpty", N.RowName(i)).Prop("Caption", row.Name).Prop("NeedMouse", "false").Close();
            Open("TextBox", "Kenshi_TextboxPaintedText", N.RowMarks(i)).Prop("Caption", row.Marks).Prop("NeedMouse", "false").Close();
            b.Close();   // row
        }
        resource = created;
        b.Close();   // roster scroll
        Scroll(N.DetailScroll);   // :521-522
        resource = "ImageBox";   // :523
        Open("ImageBox", resource, N.Portrait).Prop("NeedMouse", "false").Close();   // :524-527 (Passive)
        resource = "Kenshi_WordWrapEmpty";   // :528
        Wrapped(N.Name, v.SelectedName).Close();      // :529-530
        Wrapped(N.Stats, v.Stats).Close();            // :531-532
        Wrapped(N.Explanation, v.Explanation).Close();   // :533-534
        b.Close();   // detail scroll
        resource = "Kenshi_Button1";   // :535
        Button(N.Close, v.CloseCaption).Close();      // :536-539
        Button(N.Action, v.ActionCaption).Close();    // :540-543
        b.Close();   // window
        return b;
    }

    // ---------- the C++ catch blocks ----------

    /// <summary>The framework could not register (EnsureWindow) or Replace (RebuildRows inside RefreshAll) the layout: the C++ error line. The
    /// framework itself reports the window closed to the sim half when it was asked open (ScreenFrame.Run) and tries again at the next open
    /// request. The line is deliberately not throttled: the framework tries at most once per open request, so it is one line per Show, as the
    /// C++ logs one per failed EnsureWindow.</summary>
    static void Failed(string what, Exception e)
    {
        if (what == "register" && resource is { } r)
            ModLog.Error("Proving Grounds: prisoner recruitment UI creation failed at " + r + ": " + e.Message);   // :547-549 (AbandonWindow :550)
        else
            ModLog.Error("Proving Grounds: prisoner recruitment UI refresh failed");   // :583 (AbandonWindow :584)
    }

    // ---------- bind ----------

    /// <summary>What one bind writes for a layout: made when the model, view, font or window status changes, applied every frame (no allocation in
    /// the steady state).</summary>
    sealed record Plan(UiRect Root, (string Name, (float W, float H) Canvas)[] Canvas, (string Name, UiRect Fraction)[] Sizes,
        (string Name, string Text)[] Texts, string[] Hidden, string[] Disabled, (string Widget, bool Selected)[] Selected,
        (string Image, CharacterId Who)[] Faces);

    static Plan? plan;
    static RecruitmentWindowLayout? planFor;
    /// <summary>The layout of the model shown now, and what it was computed for.</summary>
    static (PgUiModel? Model, (int W, int H) View, int Font, string? Status, RecruitmentWindowLayout? Layout) current;

    /// <summary>C++ RefreshAll / Tick (:561-586, :622-636): FitWindowToScreen and the layout for the model shown now; the last one when it is the same
    /// computation.</summary>
    static RecruitmentWindowLayout LayoutFor(PgUiModel? m, PrisonerRecruitmentUiView<CharacterId> v)
    {
        string status = WindowStatusOf(m, v);
        var view = View();
        int font = ScreenHost.FontHeight;
        if (ReferenceEquals(current.Model, m) && current.View == view && current.Font == font && current.Status == status && current.Layout is { } l)
            return l;
        l = Compute(v, font, status);
        current = (m, view, font, status, l);
        rosterBar = l.Roster.Bar;   // the bars MyGUI keeps for the next layout pass of the shown window (a Build for PgCheck or a Replace does not feed them)
        detailBar = l.Detail.Bar;
        return l;
    }

    /// <summary>The size of a widget's parent as Meitou arranges it: the window's client, a scroll view's canvas, a row button.</summary>
    static (int W, int H) ParentSize(RecruitmentWindowLayout l, string parent)
    {
        if (parent.Length == 0) return (l.ClientWidth, l.ClientHeight);
        if (l.Canvas.TryGetValue(parent, out var c)) return c;
        return l.Rects.TryGetValue(parent, out var r) ? (r.W, r.H) : (1, 1);
    }

    static UiRect Fraction(LayoutRect r, (int W, int H) parent)
    {
        float w = Math.Max(1, parent.W), h = Math.Max(1, parent.H);
        return new UiRect(r.X / w, r.Y / h, r.W / w, r.H / h);
    }

    static readonly string[] ClientChildren = [N.Metrics, N.Subtitle, N.Status, N.RosterHeading, N.RosterScroll, N.DetailScroll, N.Close, N.Action];
    static readonly string[] DetailChildren = [N.Portrait, N.Name, N.Stats, N.Explanation];

    static Plan MakePlan(PrisonerRecruitmentUiView<CharacterId> v, RecruitmentWindowLayout l)
    {
        var sizes = new List<(string, UiRect)>();
        void Size(string name, string parent) { if (l.Rects.TryGetValue(name, out var r)) sizes.Add((name, Fraction(r, ParentSize(l, parent)))); }
        foreach (var n in ClientChildren) Size(n, "");
        foreach (var n in DetailChildren) Size(n, N.DetailScroll);
        foreach (var r in v.Rows)
        {
            Size(N.Row(r.Index), N.RosterScroll);
            foreach (var part in new[] { N.RowPortrait(r.Index), N.RowName(r.Index), N.RowMarks(r.Index) }) Size(part, N.Row(r.Index));
        }
        // the captions UpdateDetails writes (setCaption, no new widget) and the ones EnsureWindow / RebuildRows gave
        var texts = new List<(string, string)>
        {
            (N.Metrics, v.Metrics), (N.Subtitle, v.Subtitle), (N.Status, v.Status), (N.RosterHeading, v.RosterHeading),
            (N.Name, v.SelectedName), (N.Stats, v.Stats), (N.Explanation, v.Explanation), (N.Close, v.CloseCaption), (N.Action, v.ActionCaption),
        };
        foreach (var r in v.Rows) { texts.Add((N.RowName(r.Index), r.Name)); texts.Add((N.RowMarks(r.Index), r.Marks)); }
        var hidden = new List<string>(l.Hidden);
        if (!(v.PortraitVisible && v.HasSelected)) hidden.Add(N.Portrait);   // UpdateDetails :281 setVisible(false); BindPortrait(NULL) :113-116 hides too
        string[] disabled = v.ActionEnabled ? [] : [N.Action];   // :258 g_recruit->setEnabled(state.enabled)
        var selected = v.Rows.Select(r => (N.Row(r.Index), r.Selected)).ToArray();   // :292 setStateSelected(row.character == g_selected)
        var faces = v.Rows.Select(r => (N.RowPortrait(r.Index), r.Character)).ToList();
        if (v.PortraitVisible && v.HasSelected) faces.Add((N.Portrait, v.Selected));
        return new Plan(new UiRect(l.Window.X, l.Window.Y, l.Window.W, l.Window.H),
            [.. l.Canvas.Select(c => (c.Key, ((float)c.Value.W, (float)c.Value.H)))], [.. sizes], [.. texts], [.. hidden], disabled, selected, [.. faces]);
    }

    /// <summary>The canvases' offsets as the C++ moves them: 0 at each Show (:597-598); after each layout the offset (the player's, read back) clamped
    /// to the content (:333-336, :361-364).</summary>
    static void FollowScroll(PrisonerRecruitmentUiView<CharacterId> v, RecruitmentWindowLayout l)
    {
        if (ScreenHost.Host is not { } host || !host.IsRegistered(PrisonerRecruitmentUISim.ScreenId)) return;
        string id = PrisonerRecruitmentUISim.ScreenId;
        bool show = v.Shows != lastShows;
        if (!show && ReferenceEquals(clampedFor, l)) return;
        int roster = show ? 0 : (int)host.GetScroll(id, N.RosterScroll);
        int detail = show ? 0 : (int)host.GetScroll(id, N.DetailScroll);
        lastShows = v.Shows;
        clampedFor = l;
        host.SetScroll(id, N.RosterScroll, l.Roster.Clamp(roster));
        host.SetScroll(id, N.DetailScroll, l.Detail.Clamp(detail));
    }

    static void Bind(PgUiModel? m, ScreenBinding b)
    {
        if (!wasOpen) { ResetWindowState(); wasOpen = true; }
        if (Of(m) is not { } v) return;
        var size = ScreenHost.ViewSize;
        if (size.Width <= 1 || size.Height <= 1) return;   // no view yet (a UI reload): the XML's own positions stand
        var l = LayoutFor(m, v);
        FollowScroll(v, l);
        if (plan is null || !ReferenceEquals(planFor, l))
        {
            plan = MakePlan(v, l);
            planFor = l;
        }
        foreach (var (n, t) in plan.Texts) b.Text(n, t);
        foreach (var (n, f) in plan.Sizes) b.Size(n, f);
        foreach (var n in plan.Hidden) b.Hide(n);
        foreach (var n in plan.Disabled) b.Disable(n);
        foreach (var (n, on) in plan.Selected) b.Props(n, NativeUI.SelectedState(on));
        foreach (var (image, who) in plan.Faces)
            if (!b.Portrait(image, who)) b.Hide(image);   // BindPortrait :115-121: no portrait manager (here: no free cell) = hidden
        foreach (var (n, c) in plan.Canvas) b.Bindings.Canvas[n] = c;
        b.Place(N.Window, plan.Root);
    }

    /// <summary>PgCheck ui only (task 6.4b quality review): the plan's portrait decision for a model (the offline check has no portrait cells, so the
    /// bound Hide cannot tell a plan that hides it from one with no cell): whether the detail portrait is hidden and whether it is given a face.</summary>
    internal static (bool PortraitHidden, bool PortraitFaced) PortraitPlanForCheck(PgUiModel? m)
    {
        var v = Of(m) ?? Created;
        var p = MakePlan(v, Compute(v, NativeUI.DefaultFontHeight, v.Status));
        return (p.Hidden.Contains(N.Portrait), p.Faces.Any(f => f.Image == N.Portrait));
    }

    /// <summary>PgCheck ui only: every window-side state back to a fresh process's (no layout built yet).</summary>
    internal static void ResetForCheck()
    {
        ResetWindowState();
        liveModel = null;
        windowStatus = null;
        windowKey = null;
        rosterBar = detailBar = false;
    }

    // ---------- clicks ----------

    static PgCommand? Click(PgUiModel? m, UiClickData c)
    {
        if (c.Kind != UiEventKind.Click || Of(m) is not { } v) return null;
        string w = c.Widget;
        if (w == N.Close) return new RecruitmentClose { Tick = 0 };   // OnCloseClicked :475-478
        if (N.TryRow(w, out int index))   // OnRowClicked :162-172: the row's prisoner, never its index
        {
            foreach (var r in v.Rows)
                if (r.Index == index) return new RecruitmentSelect(r.Character) { Tick = 0 };
            return null;
        }
        if (w == N.Action)   // OnRecruitClicked :465-473
        {
            if (!v.HasSelected) return null;   // :467-468
            var system = ProvingGroundsSystem.Current;
            gate.FollowWorld(system);
            // the protocol counter from the NEWEST published model (as the Rewards purchase gate); the prisoner and price from the drawn one (m)
            var newest = system?.Snapshot?.Model(PrisonerRecruitmentUISim.ScreenId);
            gate.FollowSerial(newest?.RequestSerial ?? m!.RequestSerial);   // task 7.6 quality review: a new Show / Close starts over
            long published = Of(newest)?.LastSeq ?? v.LastSeq;
            long seq = gate.Send(published);   // above the newest LastSeq (one at or below it is dropped); none while the last one waits
            if (seq == 0) return null;
            return new RecruitPrisoner(v.Selected, seq, v.ShownPrice) { Tick = 0 };
        }
        return null;
    }

    // ---------- PgCheck ui: one model per shape ----------

    /// <summary>A world-free host for the check's models (the real rules half builds them).</summary>
    sealed class CheckHost(int prisoners, string name, int marks, string recruitStatus = "") : IPrisonerRecruitmentUIHost<CharacterId>
    {
        public IReadOnlyList<CharacterId> RosterPrisoners() => [.. Enumerable.Range(1, prisoners).Select(i => new CharacterId(i, 0))];
        public bool Valid(CharacterId c) => !c.IsNone;
        public bool IsDead(CharacterId c) => false;
        public bool IsUnconscious(CharacterId c) => false;
        public bool IsRosterPrisoner(CharacterId c) => true;
        public bool IngressPending() => false;
        public bool SparActive() => false;
        public bool PrisonerRuntimeActivity() => false;
        public bool PlayerReady() => true;
        public bool RecruitAvailable() => true;
        public int GetMarks(CharacterId c) => marks * c.Slot;
        public float AverageCombatStat(CharacterId c) => 30 + c.Slot;
        public int RequiredMarks(CharacterId c) => 40 + 5 * c.Slot;
        public string Name(CharacterId c) => name + " " + c.Slot.ToString(CultureInfo.InvariantCulture);
        public bool Recruit(CharacterId c, out string status) { status = recruitStatus; return false; }
        public bool BoundRegistryValid() => false;
        public void ShowArenaFromBoundRegistry() { }
        public void Debug(string line) { }
        public void Error(string line) { }
    }

    static IReadOnlyList<PgUiModel?> CheckModels()
    {
        static PrisonerRecruitmentUiModel Model(int prisoners, string name, int marks, int select = 1, string recruitStatus = "")
        {
            var host = new CheckHost(prisoners, name, marks, recruitStatus);
            var logic = new PrisonerRecruitmentUILogic<CharacterId>();
            logic.Show(host);
            if (select != 1) logic.OnRowClicked(host, new CharacterId(select, 0));
            if (recruitStatus.Length > 0) logic.OnRecruitCommand(host, logic.Selected, host.RequiredMarks(logic.Selected), 1);
            var v = logic.BuildView();
            return new PrisonerRecruitmentUiModel(v) { RequestSerial = v.RequestSerial, RequestOpen = v.RequestOpen };
        }
        string longName = string.Concat(Enumerable.Repeat("Shutori of the Long Name ", 6));
        string longStatus = string.Concat(Enumerable.Repeat("Kenshi could not add this prisoner to the current squad; the payment was cancelled. ", 4));
        return
        [
            null,
            Model(0, "Nobody", 0),                              // no prisoner: No Prisoner Selected
            Model(1, "Dust Bandit", 100),                       // enough Marks: Grant Freedom - 45 Marks
            Model(1, "Dust Bandit", 10),                        // too few: More Marks Required (same shape, button disabled)
            Model(1, longName, 10, 1, longStatus),              // a long name and a long status line
            Model(3, "Prisoner", 30),
            Model(3, "Prisoner", 30, 3),                        // another selection, same shape
            Model(12, "Prisoner", 30),                          // the roster canvas scrolls
        ];
    }
}
