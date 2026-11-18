// Port of the window half of upstream/ProvingGrounds/plugin/src/ResultsUI.h/.cpp (Proving Grounds by Shutori, GPLv3), task 4.6a (PA §2.12;
// CAP 7.0-7.4; spikes S6, S7). The rules half (the delayed show, every text) is Core/Glue/ResultsUILogic.cs + src/Game/ResultsUISim.cs, which
// publishes the immutable ResultsUiModel. This file is the window: the layout built call by call from EnsureWindow (ResultsUI.cpp:366-458) with
// LayoutBuilder / NativeUI (same widget types, skins, names, captions, alignments, colours and alphas), every rectangle from FitWindowToContent /
// LayoutWindow / LayoutResultCard (Core/Glue/ResultsUILayout.cs, tested), the portraits and the clicks. Window thread only.
//
// Engine calls of the C++ window and what replaces them:
//   createWidgetReal / createWidget (EnsureWindow)                   -> LayoutBuilder XML (Build) -> ScreenHost (Register / Replace)
//   setCoord / setSize / setPosition (LayoutWindow, LayoutResultCard,  -> ResultsUILayout's rectangles, written into the XML: each show is a new
//     FitWindowToContent, the per-frame resize in Tick :638-651)          layout (the model's Revision is in the shape key), a new view size too
//   g_window->setCoord (FitWindowToContent)                          -> the root's bound screen rectangle (MyGuiScreenHost.PlaceRoots keeps a
//                                                                       dragged offset)
//   ScrollView::setCanvasSize / setViewOffset                         -> UiBindings.Canvas; the offset: 0 on a new show (SetScroll), else kept
//   setCaption / setTextColour / setTextAlign / setColour / setAlpha  -> Property Caption / TextColour / TextAlign / Colour / Alpha in the XML
//   setVisible                                                        -> Property Visible false in the XML (the layout's Hidden set)
//   PortraitManager::setImageWidget (BindPortrait :97-108)            -> Portraits.Bind (spike S7); no free cell = the placeholder shown
//   eventMouseButtonClick (Close results), eventWindowButtonPressed   -> UiClick Click on PG_ResultsClose (left release over it, task 4.4b); the framework's Closed report
//   eventMouseWheel (BindWheel / OnWheel :71-88)                      -> Meitou's own ScrollView wheel (divergences.md, as the Arena window)
//   RenderManager::getViewSize                                        -> ScreenHost.ViewSize
//   getTextSize / getFontHeight                                       -> PgTextMeasure (the faithful painter) / ScreenHost.FontHeight
using System.Globalization;
using Meitou.Data.Ui;
using Meitou.Simulation;
using ProvingGrounds.Core;
using ProvingGrounds.Core.Glue;
using ProvingGrounds.Engine;
using ProvingGrounds.Game;
using N = ProvingGrounds.Core.Glue.ResultsUINames;

namespace ProvingGrounds.Ui;

/// <summary>The glue of the original ResultsUI.h entry points the system calls (sim thread): the frame-loop slot and the calls of SparSession,
/// ArenaIngress and the save safety, passed to the sim half. The window itself is <see cref="ResultsScreen"/>.</summary>
sealed class ResultsUI(ResultsUISim sim)
{
    internal ResultsUISim Sim => sim;

    // C++: ResultsUI.cpp:636, called from ArenaUI.cpp:3168 (ForgottenGUI_update_hook)
    internal void Tick(World world) => sim.Tick(world);

    // C++ ResultsUI.h:7 (WorldLifecycle.cpp:129, SparSession.cpp:660, ArenaIngress.cpp:1324)
    internal void Close(World world) => sim.Close();

    // C++ ResultsUI.h:5 (SparSession.cpp:719)
    internal void ScheduleShow(World world, float delaySec) => sim.ScheduleShow(world, delaySec);

    // C++ ResultsUI.h:6 (SparSession.cpp:659, ArenaIngress.cpp:1323)
    internal void Cancel(World world) => sim.Cancel();

    // C++ ResultsUI.h:9
    internal bool IsVisible => sim.IsVisible;
}

/// <summary>
/// The Results window <c>pg.results</c> (task 4.6a), the window half of ResultsUI.cpp, declared to the window framework (src/Ui/ScreenHost.cs).
/// Its shape key is the show (the model's Revision: each show lays the window out again in the C++) and the game window's size (the C++ refits on
/// a resize); everything else is in the XML of that layout. Per frame the bind places the window, gives the canvas and the portraits.
/// </summary>
static class ResultsScreen
{
    static readonly TextMeasure text = new();
    static readonly PgTextMeasure measure = new(text);
    // the coordinates the C++ widgets keep between shows (FitWindowToContent measures the wrapped labels at them; :240-245, :251-252)
    static int headerWidth = 100, contextWidth = 100, emptyWidth = 100;
    static bool scrollBar;
    static (int W, int H) viewFor;
    static int keyVersion;
    static bool wasOpen;

    static ResultsUiView<CharacterId>? Of(PgUiModel? m) => m is ResultsUiModel r ? r.View : null;

    internal static readonly PgScreen Declaration = new()
    {
        Id = ResultsUISim.ScreenId,
        ButtonEvent = N.ButtonEvent,   // task 4.4b: ResultsUI.cpp's bindings
        ShapeKey = m => "results/" + (Of(m)?.Revision ?? 0).ToString(CultureInfo.InvariantCulture),
        BeforeFrame = ViewChanged,
        KeyVersion = () => keyVersion,
        Layout = Build,
        Bind = Bind,
        WhileClosed = () => { if (wasOpen) ResetWindowState(closing: true); },
        NewWorld = _ => ResetWindowState(closing: true),
        Portraits = m => Of(m) is { } v ? [.. v.Slots.Where(s => s.HasCharacter).Select(s => s.Character)] : [],
        CheckModels = CheckModels,
    };

    /// <summary>Window-side state that must not outlive one opening of the window: the bind plan (called on close, a new world and the first bind
    /// after opening). The widgets' coordinates (the widths above) are not: the C++ window keeps them for the process.</summary>
    static void ResetWindowState(bool closing)
    {
        wasOpen = false;
        plan = null;
        scrolledFor = -1;
        // the layout last built: forgotten on close and on a new world, NOT at the first bind after opening (the layout of this opening was built
        // just before it, at the Replace that came with the open request, and the bind must place the window that layout was made for)
        if (closing) built = default;
    }

    // C++: ResultsUI.cpp:225-233, :641-644: a new view size refits the window (the per-frame Tick); here a new layout
    static void ViewChanged()
    {
        var size = ScreenHost.ViewSize;
        if (size.Width <= 1 || size.Height <= 1 || size == viewFor) return;
        viewFor = size;
        keyVersion++;
    }

    static (int W, int H) View()
    {
        var (w, h) = ScreenHost.ViewSize;
        return w <= 1 || h <= 1 ? (1920, 1080) : (w, h);
    }

    static PgWindowLayout Compute(ResultsUiView<CharacterId>? v, int font, bool keep)
    {
        text.UseCurrent();
        var (w, h) = View();
        var input = new ResultsLayoutInput
        {
            ViewWidth = w, ViewHeight = h, FontHeight = font, Header = v?.Header ?? ResultsUiTexts.Measure, Context = v?.Context ?? "",
            EmptyVisible = v?.EmptyVisible ?? false,
            Slots = v is null ? [] : [.. v.Slots.Select(s => (s.RankCaption, s.NameLine, s.Stats))],
            HeaderWidthBefore = headerWidth, ContextWidthBefore = contextWidth, EmptyWidthBefore = emptyWidth, ScrollBarBefore = scrollBar,
        };
        var l = ResultsUILayout.Compute(input, measure);
        if (keep)
        {
            headerWidth = l.HeaderWidth;
            contextWidth = l.ContextWidth;
            emptyWidth = l.EmptyWidth;
            scrollBar = l.ScrollBar;
        }
        return l;
    }

    /// <summary>C++ RankColour (:109-115): gold, silver, bronze; another place the measure label's text colour (the palette's ink in Meitou).</summary>
    internal static string RankColour(int place) => place switch
    {
        1 => "0.82 0.63 0.26",
        2 => "0.66 0.69 0.72",
        3 => "0.62 0.37 0.2",
        _ => MyGuiColours.InkText,
    };

    static string F(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    // C++: ResultsUI.cpp:366-458 EnsureWindow, the creation order; positions, visibility and the texts of the show from ResultsUILayout / the model.
    static LayoutBuilder Build(PgUiModel? m, int font)
    {
        var v = Of(m);
        var l = Compute(v, font, v is { Revision: > 0 });
        built = (m, View(), font, l);   // the bind places the window this layout was built for (Compute moved the kept widths on)
        var b = new LayoutBuilder();
        LayoutRect R(string name) => l.Rects.TryGetValue(name, out var r) ? r : new LayoutRect(0, 0, 100, 24);
        LayoutBuilder Open(string type, string skin, string name)
        {
            var r = R(name);
            b.Open(type, skin, r.X, r.Y, r.W, r.H, name);
            if (l.Hidden.Contains(name)) b.Prop("Visible", "false");
            return b;
        }
        void Accent(string name, float alpha, string colour) =>   // OptionalAccent (:116-135) + SetAccent (:136-146)
            Open("Widget", "WhiteSkin", name).Prop("Alpha", F(alpha)).Prop("Colour", colour).Close();

        b.OpenReal("Window", "Kenshi_WindowCX", .08f, .09f, .84f, .82f, N.Window, "Center", "Window").Prop("Caption", ResultsUiTexts.Caption);
        Open("TextBox", "Kenshi_TextboxStandardText", N.Measure).Prop("Caption", v?.Header ?? ResultsUiTexts.Measure).Close();
        var sr = R(N.Scroll);
        NativeUI.Scroll(b, sr.X, sr.Y, sr.W, sr.H, N.Scroll);
        Open("EditBox", "Kenshi_WordWrapEmpty", N.Header).Prop("Caption", v?.Header ?? "").Prop("TextAlign", "Center")
            .Prop("TextColour", RankColour(1)).Close();
        Open("EditBox", "Kenshi_WordWrapEmpty", N.Context).Prop("Caption", v?.Context ?? "").Prop("TextAlign", "HCenter Top").Close();
        Open("EditBox", "Kenshi_WordWrapEmpty", N.Empty).Prop("Caption", ResultsUiTexts.Empty).Prop("TextAlign", "HCenter Top").Close();
        Accent(N.RailLeft, .42f, RankColour(1));
        Accent(N.RailRight, .42f, RankColour(1));
        for (int i = 0; i < 3; ++i)
        {
            string s = N.Slot(i);
            bool filled = v is not null && i < v.Slots.Count;
            var slot = filled ? v!.Slots[i] : default;
            string colour = RankColour(filled ? slot.Place : 0);
            Open("Widget", "PanelEmpty", s);
            Open("ImageBox", "ImageBox", N.Portrait(s));
            if (!filled || !slot.HasCharacter) b.Prop("Visible", "false");   // BindPortrait: hidden without a character; LayoutPortrait :156
            b.Close();
            Open("TextBox", "Kenshi_TextboxStandardText", N.Placeholder(s)).Prop("Caption", filled ? slot.Initial : ResultsUiTexts.Placeholder)
                .Prop("TextAlign", "Center");
            if (filled && slot.HasCharacter) b.Prop("Visible", "false");   // LayoutPortrait :158
            b.Close();
            Open("TextBox", "Kenshi_TextboxStandardText", N.Rank(s)).Prop("Caption", filled ? slot.RankCaption : "").Prop("TextAlign", "Center")
                .Prop("TextColour", colour).Close();
            Open("EditBox", "Kenshi_WordWrapEmpty", N.Name(s)).Prop("Caption", filled ? slot.NameLine : "").Prop("TextAlign", "HCenter Top").Close();
            Open("EditBox", "Kenshi_WordWrapEmpty", N.Stats(s)).Prop("Caption", filled ? slot.Stats : "").Prop("TextAlign", "HCenter Top").Close();
            for (int e = 0; e < 4; ++e)
            {
                Accent(N.PortraitBorder(s, e), .82f, colour);
                Accent(N.CardBorder(s, e), .28f, colour);
            }
            b.Close();   // slot
        }
        b.Close();   // scroll
        var cr = R(N.Close);
        NativeUI.Button(b, cr.X, cr.Y, cr.W, cr.H, N.Close, ResultsUiTexts.Close).Close();
        b.Close();   // window
        return b;
    }

    // ---------- bind ----------

    static readonly IReadOnlyDictionary<string, string> Shown = new Dictionary<string, string>(StringComparer.Ordinal) { ["Visible"] = "true" };

    /// <summary>g_header->setTextColour(RankColour(1)) (ResultsUI.cpp:394): the header is an EditBox, and Meitou ignores an EditBox's XML TextColour
    /// (it draws the caption on the template's client part), so it is bound at runtime on the widget and its caption part (ScreenBinding.EditTextColour).
    /// The other EditBoxes (context, empty, names, stats) set no colour in the C++.</summary>
    internal static readonly IReadOnlyDictionary<string, string> HeaderText = new Dictionary<string, string>(StringComparer.Ordinal) { ["TextColour"] = RankColour(1) };

    internal static void ResetForCheck() => ResetWindowState(closing: true);

    /// <summary>What one bind writes for a model, view size and font: made when one of them changes, applied every frame.</summary>
    sealed record Plan(UiRect Root, (string Name, (float W, float H) Canvas)[] Canvas, (string Image, string Placeholder, CharacterId Who)[] Faces);

    static Plan? plan;
    static (PgUiModel? Model, int W, int H, int Font) planKey;
    static (PgUiModel? Model, (int W, int H) View, int Font, PgWindowLayout? Layout) built;

    /// <summary>The canvas offset: the C++ puts it at the top on a new show (ResultsUI.cpp:597) and keeps it, clamped, through every other layout pass
    /// (LayoutWindow :287, :361-362). Meitou's screen host keeps a scroll view's offset by its name across a Replace and clamps it when it arranges, so only a new show
    /// sets it to 0 here (MyGuiScreenHost.SetScroll, MyGuiScreenHost.Widgets.cs:181; the clamp UiDocument.Widgets.cs:512).</summary>
    static int scrolledFor = -1;

    static void TopOnNewShow(int show)
    {
        if (show == scrolledFor) return;
        scrolledFor = show;
        if (ScreenHost.Host is { } host && host.IsRegistered(ResultsUISim.ScreenId)) host.SetScroll(ResultsUISim.ScreenId, N.Scroll, 0);
    }

    static void Bind(PgUiModel? m, ScreenBinding b)
    {
        if (!wasOpen) { ResetWindowState(closing: false); wasOpen = true; }
        if (Of(m) is not { } v) return;
        TopOnNewShow(v.Revision);
        var (w, h) = View();
        var key = (m, w, h, ScreenHost.FontHeight);
        if (plan is null || planKey != key)
        {
            var l = ReferenceEquals(built.Model, m) && built.View == (w, h) && built.Font == ScreenHost.FontHeight && built.Layout is { } bl
                // the fallback (no layout built for this model, view and font, e.g. the font measured again before the Replace): computed from
                // the kept label widths as they are NOW, i.e. after the last layout, where the C++ measuring pass of a show used the widths of the
                // layout before; it does not move them (keep: false), and the next Replace rebuilds the layout itself
                ? bl : Compute(v, ScreenHost.FontHeight, false);
            var faces = new List<(string, string, CharacterId)>();
            for (int i = 0; i < v.Slots.Count && i < 3; i++)
                if (v.Slots[i].HasCharacter) faces.Add((N.Portrait(N.Slot(i)), N.Placeholder(N.Slot(i)), v.Slots[i].Character));
            plan = new Plan(new UiRect(l.Window.X, l.Window.Y, l.Window.W, l.Window.H),
                [.. l.Canvas.Select(c => (c.Key, ((float)c.Value.W, (float)c.Value.H)))], [.. faces]);
            planKey = key;
        }
        foreach (var (n, c) in plan.Canvas) b.Bindings.Canvas[n] = c;
        foreach (var (image, placeholder, who) in plan.Faces)
            if (!b.Portrait(image, who)) b.Props(placeholder, Shown);   // no free portrait cell: the initial, as the C++ without a portrait
        b.EditTextColour(N.Header, HeaderText);
        b.Place(N.Window, plan.Root);
    }

    // ---------- PgCheck ui: one model per shape ----------

    static IReadOnlyList<PgUiModel?> CheckModels()
    {
        static ResultsSlotView<CharacterId> Slot(int place, bool team, bool rated, bool face)
        {
            var row = new SparPodium.FighterRow { Name = "Fighter " + place, DamageDealt = 104.6f, HitsLanded = 11, Blocks = 6, Dodges = 3, RatingUpdated = rated,
                RatingAfter = 120f, RatingDelta = 20f, MarksAfter = 58, MarksEarned = 10 };
            return new(place, ResultsUILogic<CharacterId>.PlaceCaption(team ? MatchRules.MatchMode.Teams1v1 : MatchRules.MatchMode.LastStanding, place),
                row.Name + (team ? "  -  Team A" : ""), "F", ResultsUILogic<CharacterId>.StatsLine(row), face ? new CharacterId(place, 0) : CharacterId.None, face);
        }
        static ResultsUiModel Model(int revision, int count, bool team, bool rated, string header)
        {
            var v = new ResultsUiView<CharacterId>
            {
                Revision = revision, Header = header, Context = (team ? "Teams 1v1" : "Last man standing") + "  -  " + (count + 1) + " fighters",
                Slots = new(Enumerable.Range(1, count).Select(p => Slot(p, team, rated, p != 2))), EmptyVisible = count == 0,
            };
            return new ResultsUiModel(v);
        }
        return
        [
            null,
            Model(1, 1, false, false, "Kang last standing"),
            Model(2, 2, false, true, "Kang last standing"),
            Model(3, 3, false, true, "Stopped"),
            Model(4, 3, true, true, "Team A wins"),
            Model(5, 2, true, false, "Team B wins"),
            Model(6, 0, true, false, "Draw"),
        ];
    }
}
