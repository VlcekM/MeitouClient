// Port of the window half of upstream/ProvingGrounds/plugin/src/LeaderboardUI.h/.cpp (Proving Grounds by Shutori, GPLv3), task 4.6a (PA §2.15;
// CAP 7.0-7.4; spikes S6, S7). The rules half (Show, Close, the reset, every text, the standings) is Core/Glue/LeaderboardUILogic.cs +
// src/Game/LeaderboardUISim.cs, which publishes the immutable LeaderboardUiModel. This file is the window: the layout built call by call from
// EnsureWindow (LeaderboardUI.cpp:514-583) and RebuildRows (:585-652) with LayoutBuilder / NativeUI (same widget types, skins, names, captions,
// alignments, colours and alphas), every rectangle from FitWindowToScreen / LayoutWindow / LayoutRows / LayoutFeatured
// (Core/Glue/LeaderboardUILayout.cs, tested), the portraits and the clicks. Window thread only.
//
// Engine calls of the C++ window and what replaces them:
//   createWidgetReal / createWidget (EnsureWindow, RebuildRows)        -> LayoutBuilder XML (Build) -> ScreenHost (Register / Replace)
//   setCoord / setSize / setPosition / setVisible (LayoutWindow,        -> LeaderboardUILayout's rectangles and Hidden set in the XML: the model's
//     LayoutRows, the per-frame resize in Tick :721-736)                    Revision (a C++ layout pass) and the view size are in the shape key
//   g_window->setCoord (FitWindowToScreen)                              -> the root's bound screen rectangle
//   setCaption written without a layout pass (the reset's first click,   -> UiBindings.Text from the model each frame (no new layout, as the C++)
//     a failed reset, Close)
//   setTextColour / setTextAlign / setColour / setAlpha                 -> Property TextColour / TextAlign / Colour / Alpha in the XML
//   ScrollView::setCanvasSize / setViewOffset                           -> UiBindings.Canvas; the offset: 0 on a new show (SetScroll), else kept
//   PortraitManager::setImageWidget (BindPortrait :151-162)             -> Portraits.Bind (spike S7); no free cell = the placeholder shown
//   eventMouseButtonClick (Close, Reset), eventWindowButtonPressed      -> UiClick Click on PG_LeaderboardClose / PG_LeaderboardReset (left release, 4.4b); Closed
//   eventMouseWheel (OnLeaderboardWheel / BindWheel :124-149)           -> Meitou's own ScrollView wheel (divergences.md, as the Arena window)
//   getTextSize / getFontHeight / getTextColour                         -> PgTextMeasure / ScreenHost.FontHeight / the palette's ink (MyGuiColours)
using System.Globalization;
using Meitou.Data.Ui;
using Meitou.Simulation;
using ProvingGrounds.Core;
using ProvingGrounds.Core.Glue;
using ProvingGrounds.Engine;
using ProvingGrounds.Game;
using N = ProvingGrounds.Core.Glue.LeaderboardUINames;

namespace ProvingGrounds.Ui;

/// <summary>The glue of the original LeaderboardUI.h entry points the system calls (sim thread). The window itself is <see cref="LeaderboardScreen"/>.</summary>
sealed class LeaderboardUI(LeaderboardUISim sim)
{
    internal LeaderboardUISim Sim => sim;

    // C++: LeaderboardUI.cpp:721, called from ArenaUI.cpp:3169 (ForgottenGUI_update_hook): only the window's resize (window side)
    internal void Tick(World world) => sim.Tick(world);

    // C++ LeaderboardUI.h:10-12
    internal void Show(World world, LeaderboardData.Kind kind, ArenaBuilding? source) => sim.Show(world, kind, source);
    internal void Close(World world) => sim.Close();
    internal bool IsVisible => sim.IsVisible;
}

/// <summary>
/// The Leaderboard window <c>pg.leaderboard</c> (task 4.6a), declared to the window framework (src/Ui/ScreenHost.cs). Its shape key is the C++'s
/// layout passes (the model's Revision: Show, a completed reset), the row count and the game window's size; captions the C++ changes without a layout
/// pass are bound as texts.
/// </summary>
static class LeaderboardScreen
{
    static readonly TextMeasure text = new();
    static readonly PgTextMeasure measure = new(text);
    static (int W, int H) viewFor;
    static int keyVersion;
    static bool wasOpen;
    const string LeftV = "Left VCenter";   // MyGUI Align::Left | Align::VCenter

    static LeaderboardUiView<CharacterId>? Of(PgUiModel? m) => m is LeaderboardUiModel l ? l.View : null;

    internal static readonly PgScreen Declaration = new()
    {
        Id = LeaderboardUISim.ScreenId,
        ButtonEvent = N.ButtonEvent,   // task 4.4b: LeaderboardUI.cpp's bindings
        ShapeKey = m => Of(m) is { } v
            ? string.Create(CultureInfo.InvariantCulture, $"leaderboard/{v.Revision}/rows {v.Rows.Count}/reset {(v.ResetVisible ? 1 : 0)}")
            : "leaderboard/none",
        BeforeFrame = ViewChanged,
        KeyVersion = () => keyVersion,
        Layout = Build,
        Bind = Bind,
        WhileClosed = () => { if (wasOpen) ResetWindowState(closing: true); },
        NewWorld = _ => ResetWindowState(closing: true),
        Portraits = m => Of(m) is { } v ? [.. v.Rows.Where(r => r.HasCharacter).Select(r => r.Character)] : [],
        CheckModels = CheckModels,
    };

    /// <summary>Window-side state that must not outlive one opening of the window: the bind plan (called on close, a new world and the first bind
    /// after opening).</summary>
    static void ResetWindowState(bool closing)
    {
        wasOpen = false;
        plan = null;
        scrolledFor = -1;
        // the layout last built: forgotten on close and on a new world, NOT at the first bind after opening (the layout of this opening was built
        // just before it, at the Replace that came with the open request, and the bind must place the window that layout was made for)
        if (closing) built = default;
    }

    // C++: LeaderboardUI.cpp:459-469 (FitWindowToScreen on every Tick): a new view size lays the window out again; here a new layout
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

    static PgWindowLayout Compute(LeaderboardUiView<CharacterId>? v, int font)
    {
        text.UseCurrent();
        var (w, h) = View();
        return LeaderboardUILayout.Compute(new LeaderboardLayoutInput
        {
            ViewWidth = w, ViewHeight = h, FontHeight = font, Subtitle = v?.Subtitle ?? LeaderboardUiTexts.Subtitle, Empty = v?.Empty ?? LeaderboardUiTexts.Empty,
            ResetVisible = v?.ResetVisible ?? false, ResetCaption = v?.ResetCaption ?? LeaderboardUiTexts.Reset,
            Rows = v is null ? [] : [.. v.Rows.Select(r => new LeaderboardLayoutRow(r.Rank, r.Name, r.Rating, r.Record, r.Matches, r.Details))],
        }, measure);
    }

    static string F(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    // C++: LeaderboardUI.cpp:514-583 EnsureWindow and :585-652 RebuildRows, in creation order
    static LayoutBuilder Build(PgUiModel? m, int font)
    {
        var v = Of(m);
        var l = Compute(v, font);
        built = (m, View(), font, l);   // reused by the bind (one computation per layout)
        string ink = MyGuiColours.InkText;   // g_headers[0]->getTextColour(): the standard label's text colour (the palette's ink in Meitou)
        var b = new LayoutBuilder();
        LayoutRect R(string name) => l.Rects.TryGetValue(name, out var r) ? r : new LayoutRect(0, 0, 100, 24);
        LayoutBuilder Open(string type, string skin, string name)
        {
            var r = R(name);
            b.Open(type, skin, r.X, r.Y, r.W, r.H, name);
            if (l.Hidden.Contains(name)) b.Prop("Visible", "false");
            return b;
        }
        void Accent(string name, float alpha, string colour) =>   // OptionalAccent (:181-200) + SetAccent (:202-213)
            Open("Widget", "WhiteSkin", name).Prop("Alpha", F(alpha)).Prop("Colour", colour).Close();

        b.OpenReal("Window", "Kenshi_WindowCX", .06f, .09f, .88f, .82f, N.Window, "Center", "Window")
            .Prop("Caption", v?.Caption ?? LeaderboardUiTexts.Caption);
        Open("EditBox", "Kenshi_WordWrap", N.Subtitle).Prop("Caption", v?.Subtitle ?? LeaderboardUiTexts.Subtitle).Close();
        Open("EditBox", "Kenshi_WordWrapEmpty", N.Empty).Prop("Caption", v?.Empty ?? LeaderboardUiTexts.Empty).Prop("NeedMouse", "false").Close();
        var sr = R(N.Scroll);
        NativeUI.Scroll(b, sr.X, sr.Y, sr.W, sr.H, N.Scroll);
        if (l.Hidden.Contains(N.Scroll)) b.Prop("Visible", "false");
        Open("TextBox", "Kenshi_TextboxStandardText", N.Champions).Prop("Caption", LeaderboardUiTexts.Champions).Prop("TextAlign", "Center")
            .Prop("TextColour", ResultsScreen.RankColour(1)).Close();
        Accent(N.RailLeft, .42f, ResultsScreen.RankColour(1));
        Accent(N.RailRight, .42f, ResultsScreen.RankColour(1));
        for (int c = 0; c < LeaderboardUiTexts.Headings.Count; ++c)
            Open("TextBox", "Kenshi_TextboxStandardText", N.Header(c)).Prop("Caption", LeaderboardUiTexts.Headings[c]).Prop("TextAlign", LeftV).Close();
        Accent(N.Underline, .25f, ink);
        var rows = v?.Rows ?? ValueList<LeaderboardRowView<CharacterId>>.Empty;
        for (int i = 0; i < rows.Count; ++i)
        {
            var row = rows[i];
            string p = N.Row(i);
            bool featured = l.FeaturedRank.TryGetValue(p, out int rank);
            string border = featured ? ResultsScreen.RankColour(rank) : ink;   // LayoutFeatured :272 RankColour(rank), LayoutRows :419, :434 the ink
            Open("Widget", "PanelEmpty", p);
            Open("ImageBox", "ImageBox", N.Portrait(p));
            if (!row.HasCharacter && !l.Hidden.Contains(N.Portrait(p))) b.Prop("Visible", "false");   // LayoutPortrait :225: show && portraitAvailable
            b.Close();
            Open("TextBox", "Kenshi_TextboxStandardText", N.Placeholder(p)).Prop("Caption", row.Initial).Prop("TextAlign", "Center");
            if (row.HasCharacter && !l.Hidden.Contains(N.Placeholder(p))) b.Prop("Visible", "false");   // :227: show && !portraitAvailable
            b.Close();
            for (int e = 0; e < 4; ++e) Accent(N.PortraitBorder(p, e), .82f, border);
            string cellAlign = featured ? "Center" : LeftV;   // LayoutFeatured :261-264 / LayoutRows :407-408
            Open("TextBox", "Kenshi_TextboxStandardText", N.Cell(p, 0)).Prop("Caption", row.Rank).Prop("TextAlign", cellAlign);
            if (row.RankColour > 0) b.Prop("TextColour", ResultsScreen.RankColour(row.RankColour));   // :643-645
            b.Close();
            Open("EditBox", "Kenshi_WordWrapEmpty", N.Cell(p, 1)).Prop("Caption", row.Name).Prop("TextAlign", cellAlign).Close();
            Open("TextBox", "Kenshi_TextboxStandardText", N.Cell(p, 2)).Prop("Caption", row.Rating).Prop("TextAlign", LeftV).Close();
            Open("TextBox", "Kenshi_TextboxStandardText", N.Cell(p, 3)).Prop("Caption", row.Record).Prop("TextAlign", LeftV).Close();
            Open("TextBox", "Kenshi_TextboxStandardText", N.Cell(p, 4)).Prop("Caption", row.Matches).Prop("TextAlign", LeftV).Close();
            Open("EditBox", "Kenshi_WordWrapEmpty", N.Details(p)).Prop("Caption", row.Details);
            if (featured) b.Prop("TextAlign", "HCenter Top");   // :265-266
            b.Close();
            for (int e = 0; e < 4; ++e) Accent(N.CardBorder(p, e), .28f, featured ? ResultsScreen.RankColour(rank) : ink);
            Accent(N.Separator(p), .18f, ink);
            b.Close();   // row
        }
        b.Close();   // scroll
        var cr = R(N.Close);
        NativeUI.Button(b, cr.X, cr.Y, cr.W, cr.H, N.Close, LeaderboardUiTexts.Close).Close();
        var rr = R(N.Reset);
        NativeUI.Button(b, rr.X, rr.Y, rr.W, rr.H, N.Reset, v?.ResetCaption ?? LeaderboardUiTexts.Reset);
        if (l.Hidden.Contains(N.Reset)) b.Prop("Visible", "false");
        b.Close();
        b.Close();   // window
        return b;
    }

    // ---------- bind ----------

    static readonly IReadOnlyDictionary<string, string> Shown = new Dictionary<string, string>(StringComparer.Ordinal) { ["Visible"] = "true" };

    sealed record Plan(UiRect Root, (string Name, (float W, float H) Canvas)[] Canvas, (string Image, string Placeholder, CharacterId Who)[] Faces);

    static Plan? plan;
    static (PgUiModel? Model, int W, int H, int Font) planKey;
    static (PgUiModel? Model, (int W, int H) View, int Font, PgWindowLayout? Layout) built;

    /// <summary>The canvas offset: the C++ puts it at the top on a new show (RebuildRows :591) and keeps it, clamped, through every other layout pass
    /// (LayoutRows :294, :454-456). Meitou's screen host keeps a scroll view's offset by its name across a Replace and clamps it when it arranges, so only a new show
    /// sets it to 0 here (MyGuiScreenHost.SetScroll, MyGuiScreenHost.Widgets.cs:181; the clamp UiDocument.Widgets.cs:512).</summary>
    static int scrolledFor = -1;

    static void TopOnNewShow(int show)
    {
        if (show == scrolledFor) return;
        scrolledFor = show;
        if (ScreenHost.Host is { } host && host.IsRegistered(LeaderboardUISim.ScreenId)) host.SetScroll(LeaderboardUISim.ScreenId, N.Scroll, 0);
    }

    static void Bind(PgUiModel? m, ScreenBinding b)
    {
        if (!wasOpen) { ResetWindowState(closing: false); wasOpen = true; }
        if (Of(m) is not { } v) return;
        TopOnNewShow(v.Shows);
        var (w, h) = View();
        var key = (m, w, h, ScreenHost.FontHeight);
        if (plan is null || planKey != key)
        {
            var l = ReferenceEquals(built.Model, m) && built.View == (w, h) && built.Font == ScreenHost.FontHeight && built.Layout is { } bl
                ? bl : Compute(v, ScreenHost.FontHeight);   // the fallback gives the same layout (the Leaderboard keeps no widget state)
            var faces = new List<(string, string, CharacterId)>();
            for (int i = 0; i < v.Rows.Count; i++)
            {
                string p = N.Row(i);
                if (v.Rows[i].HasCharacter && !l.Hidden.Contains(N.Portrait(p))) faces.Add((N.Portrait(p), N.Placeholder(p), v.Rows[i].Character));
            }
            plan = new Plan(new UiRect(l.Window.X, l.Window.Y, l.Window.W, l.Window.H),
                [.. l.Canvas.Select(c => (c.Key, ((float)c.Value.W, (float)c.Value.H)))], [.. faces]);
            planKey = key;
        }
        // captions the C++ writes without a layout pass (OnResetClicked :73-85, Close :718)
        b.Text(N.Subtitle, v.Subtitle);
        b.Text(N.Empty, v.Empty);
        b.Text(N.Reset, v.ResetCaption);
        foreach (var (n, c) in plan.Canvas) b.Bindings.Canvas[n] = c;
        foreach (var (image, placeholder, who) in plan.Faces)
            if (!b.Portrait(image, who)) b.Props(placeholder, Shown);   // no free portrait cell: the initial
        b.Place(N.Window, plan.Root);
    }

    // ---------- PgCheck ui: one model per shape ----------

    static IReadOnlyList<PgUiModel?> CheckModels()
    {
        static LeaderboardUiModel Model(int revision, int rows, bool town, bool reset)
        {
            var list = Enumerable.Range(0, rows).Select(i => LeaderboardUILogic<CharacterId>.RowTexts(i,
                new LeaderboardData.Standing { Id = "t" + i, Name = "Fighter " + i, Mmr = 150f - i, Wins = 2, Losses = i % 2, Matches = 2 + i % 2 },
                i % 3 != 1, new CharacterId(i, 0)));
            return new LeaderboardUiModel(new LeaderboardUiView<CharacterId>
            {
                Revision = revision, Caption = town ? LeaderboardUiTexts.CaptionTown : LeaderboardUiTexts.CaptionPlayer,
                Subtitle = reset ? LeaderboardUiTexts.ResetSubtitle : town ? "Scratch circuit standings - meitou" : "Player arena standings - meitou",
                Empty = reset ? LeaderboardUiTexts.ResetEmpty : town ? LeaderboardUiTexts.EmptyTown : LeaderboardUiTexts.EmptyPlayer,
                ResetVisible = reset, Rows = new(list),
            });
        }
        return
        [
            null,
            Model(1, 0, true, false),
            Model(2, 0, false, true),
            Model(3, 1, false, false),
            Model(4, 2, false, false),
            Model(5, 3, true, false),
            Model(6, 12, true, false),
            Model(7, 40, false, false),
        ];
    }
}
