// Port of the window half of upstream/ProvingGrounds/plugin/src/ArenaUI.h/.cpp (Proving Grounds by Shutori, GPLv3), task 4.5b (PA §2.3, §2.4;
// CAP 7.0-7.7; spikes S6, S7). The rules half (every decision, state and text) is task 4.5a: Core/Glue/ArenaUILogic.cs, src/Game/ArenaUISim.cs,
// which publishes the immutable ArenaUiModel and takes the ArenaUiCommands of src/Engine/PgCommand.cs. This file is the window: the layout built
// call by call from CreatePanel (ArenaUI.cpp:2814-2926) and MakeRow (:632-686) with LayoutBuilder / NativeUI (same widget types, skins, names and
// captions), the pixel arithmetic of LayoutPanel / LayoutColumn / LayoutRows / PlaceButtons (Core/Glue/ArenaUILayout.cs, tested), the binding of
// the model, the clicks, compact mode and its drawers (RefreshResponsiveMode / RefreshCompactDrawers :2480-2530), the row press / drag / release
// (Core/Glue/ArenaUIPointer.cs), the drag highlights (:1159-1238), the search box's placeholder (:1685-1735) and the settings tooltip
// (:1405-1516). Window thread only: it reads the published model and sends commands (M4 brief, UI rules).
//
// Engine calls of the C++ window and what replaces them:
//   createWidgetReal / createWidget (CreatePanel, MakeRow, RebuildList)      -> LayoutBuilder XML (Layout below) -> ScreenHost (Register / Replace)
//   setCoord / setSize / setPosition (LayoutPanel, LayoutColumn, LayoutRows)  -> ArenaUILayout's rectangles: in the XML, and bound
//                                                                              (UiBindings.RelativePositions) when they change within one shape
//   ScrollView::setCanvasSize                                                 -> UiBindings.Canvas
//   g_window->setCoord(view / 25, ...) (RefreshResponsiveMode)                -> the root's bound size (MyGuiScreenHost.PlaceRoots centres it)
//   setCaption / setVisible / setEnabled / setStateSelected                   -> UiBindings Text / Hidden / Disabled / Properties (StateSelected + Checked, NativeUI.SelectedState)
//   setColour (StatusColour), fill setCoord (RefreshStatusBar)                -> Properties Colour, UiBindings.Progress
//   PortraitManager::setImageWidget (BindPortrait)                            -> Portraits.Bind (spike S7)
//   eventMouseButtonClick (PanelButton)                                       -> UiEventKind.Click: the framework sends it at the left release over
//                                                                              the pressed button (task 4.4b, ArenaUINames.ButtonEvent) -> commands
//   eventMouseButtonPressed (presets, left)                                   -> MyGuiScreenHost.Activated (a press) -> UiEventKind.Press
//   eventMouseButtonPressed / Released (MouseButton::Right, presets, rows)    -> ScreenHost's PlayerInterface.MouseDown / MouseUp postfixes
//   eventMouseButtonDoubleClick, eventMouseDrag                               -> ArenaRowPointer (MyGUI's 0.25 s double-click time; drag from the
//                                                                              pointer while the left button is held)
//   eventMouseWheel / RouteArenaWheelFallback / RouteColumnWheelFallback      -> Meitou's own ScrollView wheel (MyGuiScreenHost.WheelWidget: the
//                                                                              innermost scroll view under the pointer, 50 px; divergences.md)
//   eventEditTextChange / KeySetFocus / KeyLostFocus (search box)             -> Activated "text"; the focus read from MyGuiScreenHost.Focus each bind
//   eventToolTip (BindSettingsToolTip)                                        -> the pointer over the medical button, read each bind (Meitou has no
//                                                                              per-widget tooltip event; divergences.md)
//   MyGUI::InputManager::getMousePositionByLayer, GetAsyncKeyState(VK_LBUTTON) -> ScreenHost.Pointer / LeftHeld
//   RenderManager::getViewSize                                                -> ScreenHost.ViewSize (PlayerInterface.Width / Height)
//   getTextSize / getFontHeight                                               -> TextMeasure (src/Ui/TextMeasure.cs: the faithful painter's measurer)
using System.Globalization;
using System.Numerics;
using Meitou.Data.Ui;
using Meitou.Simulation;
using ProvingGrounds.Core;
using ProvingGrounds.Core.Glue;
using ProvingGrounds.Engine;
using ProvingGrounds.Game;
using N = ProvingGrounds.Core.Glue.ArenaUINames;

namespace ProvingGrounds.Ui;

/// <summary>The glue of the original ArenaUI.cpp entry points the system calls (sim thread): the debug key poll (task 4.6) and the save safety's
/// AbandonWorldState (the rules half, task 4.5a). The window itself is <see cref="ArenaScreen"/>.</summary>
sealed class ArenaUI(Game.ArenaUISim sim)
{
    // C++: ArenaUI.cpp:2967-2976 (PollF8Toggle), called at :3153 between ContextMenuHooks::RefreshArenaCursor and DebugMenu::Tick.
    // F8 becomes the "Debug menu key" setting (Off / O / N / V; overview §1, divergences.md). Task 4.6b: the debug menu's PollKey (the system sets it).
    internal Action<World>? PollDebugMenuKeyStep { get; set; }
    internal void PollDebugMenuKey(World world) => PollDebugMenuKeyStep?.Invoke(world);

    // Save safety (task 2.5, WorldLifecycle.cpp:69, :263) calls it: the rules half's AbandonWorldState (task 4.5a).
    internal void AbandonWorldState(World world) => sim.AbandonWorldState(world);   // C++ ArenaUI.h:13
}

/// <summary>The text sizes the Arena window's arithmetic asks (MyGUI <c>getTextSize</c>), through the screen's <see cref="TextMeasure"/>: a label
/// (PanelLabel) in the standard font word-wrapped to its width, a button's presentation text in the painted font, a button's natural width its
/// caption on one line. Window thread.</summary>
sealed class ArenaTextMeasure(TextMeasure text) : IArenaTextMeasure
{
    public int LabelHeight(string caption, int width) => text.Height(TextMeasure.StandardFont, caption, width);
    public int PaintedHeight(string caption, int width) => text.Height(TextMeasure.PaintedFont, caption, width);
    public int ButtonTextWidth(string caption) => text.Width(TextMeasure.PaintedFont, caption);
}

/// <summary>
/// The Arena window <c>pg.arena</c> (task 4.5b), the window half of ArenaUI.cpp: declared to the window framework (src/Ui/ScreenHost.cs) like the
/// test window. Its shape key is the sim half's (row counts, team mode, balance menu, ArenaUiView.ShapeKey) plus the handler lines of the team / pool
/// rows (the C++ recreates every row on each RebuildList, so a row gaining its handler line is a new layout) plus compact mode, decided here from
/// the client width (RefreshResponsiveMode). The drawers, the client size, the texts' sizes and the drag look change only positions, captions and
/// visibility: bound each frame (the C++ LayoutPanel on every refresh).
/// </summary>
static class ArenaScreen
{
    static readonly TextMeasure text = new();
    static readonly ArenaTextMeasure measure = new(text);
    static readonly ArenaRowPointer<CharacterId> pointer = new();
    static bool compact, rosterDrawerOpen, settingsDrawerOpen, searchPlaceholderActive = true, searchFocused, wasOpen;
    static (int W, int H, int Font) responsiveFor;
    static int keyVersion;

    // the layout the registered XML was built with (its rectangles never need binding) and its parents
    static ArenaLayout? built;
    static Dictionary<string, string> builtParents = new(StringComparer.Ordinal);

    static ArenaUiView<CharacterId>? Of(PgUiModel? m) => m is ArenaUiModel a ? a.View : null;

    internal static readonly PgScreen Declaration = new()
    {
        Id = ArenaUISim.ScreenId,
        ShapeKey = ShapeKey,
        BeforeFrame = Responsive,
        KeyVersion = () => keyVersion,
        Layout = Build,
        Bind = Bind,
        Click = Click,
        ButtonEvent = N.ButtonEvent,   // task 4.4b: ArenaUI.cpp's bindings (PanelButton Click, presets Press, rows their own pointer)
        WantsReleases = true,
        WhileClosed = Closed,
        NewWorld = NewWorld,
        TestPointerMoved = () => { var at = ScreenHost.Pointer; pointer.Move(at.X, at.Y, ScreenHost.LeftHeld); },
        Portraits = Portraits,
        CheckModels = CheckModels,
    };

    // ---------- shape ----------

    static string ShapeKey(PgUiModel? m)
    {
        var v = Of(m);
        var sb = new System.Text.StringBuilder(v?.ShapeKey ?? "arena:none");
        if (v is not null)
        {
            AppendHandlers(sb, 'a', v.TeamA);
            AppendHandlers(sb, 'b', v.TeamB);
            AppendHandlers(sb, 'p', v.Pool);
        }
        sb.Append(compact ? "/compact" : "/wide");
        return sb.ToString();
    }

    static void AppendHandlers(System.Text.StringBuilder sb, char list, IReadOnlyList<ArenaRowView<CharacterId>> rows)
    {
        for (int i = 0; i < rows.Count; i++)
            if (rows[i].HasHandler) sb.Append('/').Append(list).Append('h').Append(i.ToString(CultureInfo.InvariantCulture));
    }

    // C++: ArenaUI.cpp:2512-2530 RefreshResponsiveMode: compact when the client is narrower than 76 font heights; a change closes both drawers.
    // Decided on the window thread from the view size (RenderManager::getViewSize) and the measured font; a change bumps the key version (a Replace).
    // The framework calls it before every frame (PgScreen.BeforeFrame), so KeyVersion is a plain read.
    static void Responsive()
    {
        var (w, h) = ScreenHost.ViewSize;
        if (w <= 1 || h <= 1) return;
        int font = ScreenHost.FontHeight;
        if (responsiveFor == (w, h, font)) return;
        responsiveFor = (w, h, font);
        bool now = ArenaUILayout.CompactFor(w, h, font);
        if (now == compact) return;
        compact = now;
        rosterDrawerOpen = false;
        settingsDrawerOpen = false;
        keyVersion++;
    }

    // ---------- layout ----------

    /// <summary>The text measure follows the current painter, window height and font (window thread; nothing before an interface exists).</summary>
    static void UseMeasurer() => text.UseCurrent();

    static IReadOnlyList<ArenaLayoutRow> LayoutRows(IReadOnlyList<ArenaRowView<CharacterId>> rows, bool roster)
    {
        var list = new ArenaLayoutRow[rows.Count];
        for (int i = 0; i < rows.Count; i++) list[i] = new(rows[i].Caption, roster, rows[i].HasHandler, rows[i].HandlerLabel);
        return list;
    }

    /// <summary>The layout input for a view, the window state and (during a drag) the heading captions the drag look shows.</summary>
    static ArenaLayoutInput InputFor(ArenaUiView<CharacterId>? v, int font, ArenaDragLook? drag)
    {
        var (w, h) = ScreenHost.ViewSize;
        if (w <= 1 || h <= 1) (w, h) = (1920, 1080);
        var captions = new Dictionary<string, string>(StringComparer.Ordinal);
        if (v is not null)
        {
            captions[N.RosterCount] = v.RosterCount;
            captions[N.LblDest] = v.DestLabel;
            captions[N.LblTeamA] = drag?.TeamALabel ?? v.TeamALabel;
            captions[N.LblTeamB] = drag?.TeamBLabel ?? v.TeamBLabel;
            captions[N.LblPool] = drag?.PoolLabel ?? v.PoolLabel;
            captions[N.PreviewName] = v.PreviewName;
            captions[N.PreviewMeta] = v.PreviewMeta;
            captions[N.PreviewStats] = v.PreviewStats;
            captions[N.Summary] = v.Summary;
            foreach (var (name, b) in Buttons(v)) captions[name] = b.Caption ?? N.CreationCaptions.GetValueOrDefault(name, "");
        }
        return new ArenaLayoutInput
        {
            ViewWidth = w, ViewHeight = h, FontHeight = font, SampleTextHeight = measure.LabelHeight("Arena", 100),
            Compact = compact, RosterDrawerOpen = rosterDrawerOpen, SettingsDrawerOpen = settingsDrawerOpen,
            TeamMode = v?.TeamMode ?? true, BalanceMenuOpen = v?.BalanceMenuOpen ?? false, Captions = captions,
            Roster = v is null ? [] : LayoutRows(v.Roster, true), TeamA = v is null ? [] : LayoutRows(v.TeamA, false),
            TeamB = v is null ? [] : LayoutRows(v.TeamB, false), Pool = v is null ? [] : LayoutRows(v.Pool, false),
        };
    }

    /// <summary>Every PanelButton with its view state, in creation order.</summary>
    static IEnumerable<(string Name, ArenaButtonView View)> Buttons(ArenaUiView<CharacterId> v)
    {
        yield return (N.Recruit, v.Recruit);
        yield return (N.ModeAvB, v.ModeAvB);
        yield return (N.ModeTeams1v1, v.ModeTeams1v1);
        yield return (N.ModeLast, v.ModeLast);
        yield return (N.DestA, v.DestA);
        yield return (N.DestB, v.DestB);
        yield return (N.AutoBalance, v.Balance);
        yield return (N.BalanceSkill, v.BalanceSkill);
        yield return (N.BalanceRating, v.BalanceRating);
        yield return (N.Swap, v.Swap);
        yield return (N.Clear, v.Clear);
        for (int i = 0; i < 3 && i < v.Presets.Count; i++) yield return (N.Preset(i), v.Presets[i]);
        yield return (N.Remove, v.Remove);
        yield return (N.LocArena, v.LocArena);
        yield return (N.LocSmall, v.LocSmall);
        yield return (N.LocBanner, v.LocBanner);
        yield return (N.Medical, v.Medical);
        yield return (N.Start, v.Start);
        yield return (N.Stop, v.Stop);
    }

    // C++: ArenaUI.cpp:2814-2926 CreatePanel, :632-686 MakeRow, :730-753 RebuildList (the empty label), in creation order; positions from
    // ArenaUILayout. The C++ hides g_metrics (:2828) and g_settingsTip (:2914) at creation; the bind does the rest of the visibility.
    static LayoutBuilder Build(PgUiModel? m, int font)
    {
        UseMeasurer();
        var v = Of(m);
        var l = ArenaUILayout.Compute(InputFor(v, font, null), measure);
        var parents = new Dictionary<string, string>(StringComparer.Ordinal);
        var b = new LayoutBuilder();
        LayoutRect R(string name) => l.Rects.TryGetValue(name, out var r) ? r : new LayoutRect(0, 0, 200, 32);
        LayoutBuilder Open(string type, string skin, string name, string parent)
        {
            parents[name] = parent;
            var r = R(name);
            return b.Open(type, skin, r.X, r.Y, r.W, r.H, name);
        }
        void Label(string name, string parent) => Open("EditBox", "Kenshi_WordWrapEmpty", name, parent)   // PanelLabel = NativeUI::WrappedLabel
            .Prop("Caption", N.CreationCaptions.GetValueOrDefault(name, "")).Close();
        void Button(string name, string parent)   // PanelButton :2537-2554: the caption on a painted edit box over the button, the button's own ""
        {
            Open("Button", "Kenshi_Button1", name, parent).Prop("Caption", "");
            Open("EditBox", "Kenshi_PaintedWordWrapEmpty", N.TextOf(name), name)
                .Prop("Caption", N.CreationCaptions.GetValueOrDefault(name, "")).Prop("NeedMouse", "false").Close();
            b.Close();
        }
        void Scroll(string name, string parent)
        {
            parents[name] = parent;
            var r = R(name);
            NativeUI.Scroll(b, r.X, r.Y, r.W, r.H, name);
        }

        b.OpenReal("Window", "Kenshi_WindowCX", .04f, .04f, .92f, .92f, N.Window, "Center", "Window").Prop("Caption", "Proving Grounds - Arena");
        parents[N.Metrics] = "";
        NativeUI.Label(b, 0, 0, 100, 24, N.Metrics, "Arena").Prop("Visible", "false").Close();
        for (int i = 0; i < 3; i++)
        {
            Scroll(N.Column(i), "");
            Open("Widget", "PanelEmpty", N.ColumnContent(i), N.Column(i));
            string c = N.ColumnContent(i);
            if (i == 0)
            {
                Label(N.LblRoster, c);
                Label(N.RosterCount, c);
                Open("EditBox", "Kenshi_EditBox", N.Search, c).Prop("MaxTextLength", "40").Prop("Caption", ArenaUiTexts.SearchPlaceholder).Close();
                Label(N.RosterHint, c);
                Scroll(N.Roster, c);
                Rows(v?.Roster ?? ValueList<ArenaRowView<CharacterId>>.Empty, ArenaList.Roster);
                b.Close();
            }
            else if (i == 1)
            {
                Label(N.LblMode, c);
                Button(N.ModeAvB, c); Button(N.ModeTeams1v1, c); Button(N.ModeLast, c);
                Label(N.LblDest, c);
                Button(N.DestA, c); Button(N.DestB, c);
                Label(N.LblTeamA, c); Label(N.LblTeamB, c); Label(N.LblPool, c);
                foreach (var (list, rows) in new[] { (ArenaList.TeamA, v?.TeamA), (ArenaList.TeamB, v?.TeamB), (ArenaList.Pool, v?.Pool) })
                {
                    Scroll(N.ScrollOf(list), c);
                    Rows(rows ?? ValueList<ArenaRowView<CharacterId>>.Empty, list);
                    b.Close();
                }
                Button(N.AutoBalance, c); Button(N.BalanceSkill, c); Button(N.BalanceRating, c); Button(N.Swap, c); Button(N.Clear, c);
                Label(N.BuilderHint, c);
                for (int p = 0; p < 3; p++) Button(N.Preset(p), c);
            }
            else
            {
                Label(N.LblPreview, c);
                Open("ImageBox", "ImageBox", N.PreviewImg, c).Close();
                Label(N.PreviewName, c); Label(N.PreviewMeta, c); Label(N.PreviewStats, c);
                Button(N.Remove, c);
                Label(N.LblSettings, c); Label(N.LblLoc, c);
                Button(N.LocArena, c); Button(N.LocSmall, c); Button(N.LocBanner, c);
                Button(N.Medical, c);
                Label(N.LblSummary, c); Label(N.Summary, c);
            }
            b.Close();   // content
            b.Close();   // column scroll
        }
        Label(N.Hero, "");
        Button(N.Recruit, "");
        Open("EditBox", "Kenshi_WordWrap", N.Status, "").Close();
        Button(N.Start, ""); Button(N.Stop, ""); Button(N.RosterDrawer, ""); Button(N.SettingsDrawer, "");
        Open("EditBox", "Kenshi_WordWrap", N.SettingsTip, "").Prop("Visible", "false").Prop("NeedMouse", "false").Close();
        b.Close();   // window

        // MakeRow :632-686 (children of the list's scroll view; the C++ makes the parts passive, Passive :251-260: NeedMouse false)
        void Rows(IReadOnlyList<ArenaRowView<CharacterId>> rows, ArenaList list)
        {
            string scroll = N.ScrollOf(list);
            if (rows.Count == 0)
            {
                Open("EditBox", "Kenshi_WordWrapEmpty", N.EmptyList(list), scroll)
                    .Prop("Caption", list == ArenaList.Roster ? ArenaUiTexts.EmptyRoster : ArenaUiTexts.EmptyAssign).Prop("NeedMouse", "false").Close();
                return;
            }
            for (int i = 0; i < rows.Count; i++)
            {
                string row = N.Row(list, i);
                Open("Button", "Kenshi_Button1", row, scroll).Prop("Caption", "");
                Open("ImageBox", "ImageBox", N.RowImage(row), row).Prop("NeedMouse", "false").Close();
                Open("EditBox", "Kenshi_PaintedWordWrapEmpty", N.RowLabel(row), row).Prop("Caption", rows[i].Caption).Prop("NeedMouse", "false").Close();
                if (list == ArenaList.Roster)
                {
                    Open("Widget", "WhiteSkin", N.RowStatusBack(row), row).Prop("Colour", "0.12 0.12 0.12").Prop("NeedMouse", "false");
                    Open("Widget", "WhiteSkin", N.RowStatusFill(row), N.RowStatusBack(row)).Prop("NeedMouse", "false").Close();
                    b.Close();
                }
                if (rows[i].HasHandler)
                {
                    Open("ImageBox", "ImageBox", N.RowHandlerImage(row), row).Prop("NeedMouse", "false").Close();
                    Open("EditBox", "Kenshi_PaintedWordWrapEmpty", N.RowHandlerLabel(row), row).Prop("Caption", rows[i].HandlerLabel).Prop("NeedMouse", "false").Close();
                }
                b.Close();
            }
        }

        built = l;
        builtParents = parents;
        // a press in flight survives the new layout (it holds the fighter, not the row widget): the C++ RebuildList keeps g_dragCharacter too
        return b;
    }

    // ---------- bind ----------

    static readonly IReadOnlyDictionary<string, string> Selected = NativeUI.SelectedState(true);   // StateSelected + Checked
    // C++ :263-268 StatusColour
    static readonly IReadOnlyDictionary<string, string> Green = new Dictionary<string, string>(StringComparer.Ordinal) { ["Colour"] = "0.18 0.72 0.28" };
    static readonly IReadOnlyDictionary<string, string> Orange = new Dictionary<string, string>(StringComparer.Ordinal) { ["Colour"] = "0.95 0.52 0.12" };
    static readonly IReadOnlyDictionary<string, string> Red = new Dictionary<string, string>(StringComparer.Ordinal) { ["Colour"] = "0.78 0.16 0.12" };

    /// <summary>What one bind writes for a model and the window state, made when one of them changes and applied every frame.</summary>
    sealed class Plan
    {
        public readonly List<(string Name, string Text)> Texts = [];
        public readonly List<string> Hidden = [];
        public readonly List<string> Disabled = [];
        public readonly List<(string Name, IReadOnlyDictionary<string, string> Props)> Props = [];
        public readonly List<(string Name, UiRect Fraction)> Sizes = [];
        public readonly List<(string Name, float Fraction)> Progress = [];
        public readonly List<(string Name, (float W, float H) Canvas)> Canvas = [];
        public readonly List<(string Name, CharacterId Who)> Faces = [];
        public UiRect Root;
        public ArenaLayout Layout = null!;
    }

    readonly record struct PlanKey(PgUiModel? Model, ArenaLayout? Built, int ViewW, int ViewH, int Font, bool Compact, bool RosterDrawer,
        bool SettingsDrawer, ArenaDragLook? Drag, bool DragOverPrisonerRow, int PrisonerList, int PrisonerIndex);

    static Plan? plan;
    static PlanKey planKey;

    static void Bind(PgUiModel? m, ScreenBinding b)
    {
        var host = ScreenHost.Host;
        UseMeasurer();
        if (Of(m) is not { } v || host is null)
        {
            b.Text(N.Status, "Idle");
            foreach (var n in ArenaUILayout.AllButtons()) b.Disable(n);
            return;
        }
        if (!wasOpen) { wasOpen = true; ResetWindowState(); }   // the first bind after opening
        Search(host);

        // RefreshPointerHighlights :1201-1219: a drag follows the pointer while the left button is held
        var at = ScreenHost.Pointer;
        pointer.Hover(HoverWidget(host, at), ScreenHost.AnyHeld);   // MyGUI's mouse focus, kept during a capture by any button (the double-click timer expires on a change)
        pointer.Move(at.X, at.Y, ScreenHost.LeftHeld);
        ArenaDragLook? drag = null;
        (ArenaList List, int Index)? prisonerRow = null;
        if (pointer.DragActive && Find(v, pointer.DragList, pointer.DragFighter) is { } dragged)
        {
            prisonerRow = PrisonerRowAt(v, at);
            drag = ArenaDragHighlight.Look(dragged.DragValid, dragged.HandlerCandidate, prisonerRow is not null, TargetAt(at),
                v.TeamMode, v.TeamALabel, v.TeamBLabel, v.PoolLabel);
        }

        var (vw, vh) = ScreenHost.ViewSize;
        var key = new PlanKey(m, built, vw, vh, ScreenHost.FontHeight, compact, rosterDrawerOpen, settingsDrawerOpen, drag,
            prisonerRow is not null, (int)(prisonerRow?.List ?? 0), prisonerRow?.Index ?? -1);
        if (plan is null || !planKey.Equals(key))
        {
            plan = MakePlan(v, drag, prisonerRow);
            planKey = key;
        }
        Apply(plan, b);

        // the settings tip (BindSettingsToolTip :1491-1497; ShowSettingsTip / PlaceSettingsTip :1438-1450): shown while the pointer rests on the
        // medical button, it follows the pointer, so it is bound every frame here, outside the plan (no plan rebuild per pointer move)
        if (Tip(Over(N.Medical, at) && v.MedicalTip.Length > 0, at))
        {
            var l = plan.Layout;
            var client = ClientOrigin(l);
            tipText = v.MedicalTip;
            var r = ArenaUILayout.SettingsTip(l.ClientWidth, l.ClientHeight, ScreenHost.FontHeight, (int)(at.X - client.X), (int)(at.Y - client.Y), TipHeight);
            b.Text(N.SettingsTip, tipText);
            b.Props(N.SettingsTip, Shown);
            b.Size(N.SettingsTip, Fraction(r, l.ClientWidth, l.ClientHeight));
        }
    }

    // the tip text's wrapped height at a width (one cached delegate: no closure per frame)
    static string tipText = "";
    static readonly Func<int, int> TipHeight = width => measure.LabelHeight(tipText, width);

    /// <summary>The bind of a view: texts, visibility, enabled state, selected looks, every changed position, canvases, health bars, portraits.</summary>
    static Plan MakePlan(ArenaUiView<CharacterId> v, ArenaDragLook? drag, (ArenaList List, int Index)? prisonerRow)
    {
        var p = new Plan();
        var l = ArenaUILayout.Compute(InputFor(v, ScreenHost.FontHeight, drag), measure);
        p.Layout = l;
        p.Root = new UiRect(l.Window.X, l.Window.Y, l.Window.W, l.Window.H);

        // captions (the model's; a drag shows the heading suffix and, over a prisoner row, the status text: ApplyDragHighlights)
        p.Texts.Add((N.RosterCount, v.RosterCount));
        p.Texts.Add((N.LblDest, v.DestLabel));
        p.Texts.Add((N.LblTeamA, drag?.TeamALabel ?? v.TeamALabel));
        p.Texts.Add((N.LblTeamB, drag?.TeamBLabel ?? v.TeamBLabel));
        p.Texts.Add((N.LblPool, drag?.PoolLabel ?? v.PoolLabel));
        p.Texts.Add((N.PreviewName, v.PreviewName));
        p.Texts.Add((N.PreviewMeta, v.PreviewMeta));
        p.Texts.Add((N.PreviewStats, v.PreviewStats));
        p.Texts.Add((N.Summary, v.Summary));
        p.Texts.Add((N.Status, drag?.Status ?? v.Status));

        // buttons (UpdateModeVisibility, UpdateBalanceMenuVisibility, RefreshStatusAndButtons, the highlights)
        foreach (var (name, bv) in Buttons(v))
        {
            p.Texts.Add((N.TextOf(name), bv.Caption ?? ""));
            if (!bv.Visible) p.Hidden.Add(name);
            if (!bv.Enabled) p.Disabled.Add(name);
            if (bv.Selected) p.Props.Add((name, Selected));
        }
        // compact mode (RefreshCompactDrawers, LayoutPanel :2717-2731): the drawer buttons and their toggled look; the side columns; the title
        if (!l.DrawerButtonsVisible) { p.Hidden.Add(N.RosterDrawer); p.Hidden.Add(N.SettingsDrawer); }
        else
        {
            if (rosterDrawerOpen) p.Props.Add((N.RosterDrawer, Selected));
            if (settingsDrawerOpen) p.Props.Add((N.SettingsDrawer, Selected));
        }
        if (!l.HeroVisible) p.Hidden.Add(N.Hero);
        for (int i = 0; i < 3; i++) if (!l.ColumnVisible[i]) p.Hidden.Add(N.Column(i));
        // team mode (UpdateModeVisibility :1316-1353)
        if (v.TeamMode) { p.Hidden.Add(N.Pool); p.Hidden.Add(N.LblPool); }
        else { p.Hidden.Add(N.TeamA); p.Hidden.Add(N.TeamB); p.Hidden.Add(N.LblTeamA); p.Hidden.Add(N.LblTeamB); }

        // rows (MakeRow, RefreshRowLabel, ApplyRowState, RefreshStatusBar; the drag's prisoner-row look)
        foreach (var (list, rows) in new[] { (ArenaList.Roster, v.Roster), (ArenaList.TeamA, v.TeamA), (ArenaList.TeamB, v.TeamB), (ArenaList.Pool, v.Pool) })
            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                string row = N.Row(list, i);
                p.Texts.Add((N.RowLabel(row), r.Caption));
                bool selected = r.Selected;
                if (prisonerRow is { } pr && pr.List == list && pr.Index == i && drag is { } d) selected = d.PrisonerSelected;
                if (selected) p.Props.Add((row, Selected));
                p.Faces.Add((N.RowImage(row), r.Id));
                if (list == ArenaList.Roster)
                {
                    string back = N.RowStatusBack(row), fill = N.RowStatusFill(row);
                    if (!r.HealthVisible) p.Hidden.Add(back);
                    else if (l.Rects.TryGetValue(back, out var br))
                    {
                        int w = ArenaUILayout.StatusFillWidth(br.W, r.HealthRatio);
                        if (w <= 0) p.Hidden.Add(fill);
                        else
                        {
                            p.Progress.Add((fill, br.W > 0 ? (float)w / br.W : 0));
                            p.Props.Add((fill, r.HealthBand == RosterStatusPolicy.Band.Green ? Green : r.HealthBand == RosterStatusPolicy.Band.Orange ? Orange : Red));
                        }
                    }
                }
                if (r.HasHandler)
                {
                    p.Texts.Add((N.RowHandlerLabel(row), r.HandlerLabel));
                    p.Faces.Add((N.RowHandlerImage(row), r.Handler));
                }
            }

        // the selected fighter card (SetPreview :537-594)
        if (v.PreviewImageVisible && v.HasPreview) p.Faces.Add((N.PreviewImg, v.Preview));
        else p.Hidden.Add(N.PreviewImg);

        // positions that changed since the XML was built (a pixel position in the XML stays at its offset from its parent's origin), canvases
        if (built is not null)
            foreach (var (name, rect) in l.Rects)
            {
                if (built.Rects.TryGetValue(name, out var was) && was == rect) continue;
                if (!builtParents.TryGetValue(name, out var parent)) continue;   // not in this layout (another shape)
                var (pw, ph) = ParentSize(l, parent);
                p.Sizes.Add((name, Fraction(rect, pw, ph)));
            }
        foreach (var (name, c) in l.Canvas) p.Canvas.Add((name, (c.W, c.H)));
        return p;
    }

    /// <summary>The widget of this window under the pointer (MyGUI's mouse focus widget), or null.</summary>
    static string? HoverWidget(Meitou.Game.Ui.MyGuiScreenHost host, Vector2 at)
    {
        if (!float.IsFinite(at.X)) return null;
        return host.HitTest(at) is { } hit && hit.Screen == ArenaUISim.ScreenId ? hit.Control.Name : null;
    }

    // C++: the settings tip is shown on MyGUI's ToolTipInfo::Show (OnSettingsToolTip :1468-1489), which MyGUI's ToolTipManager raises when the
    // pointer has rested on the widget for its delay (MyGUI 3.2.2 MyGUI_ToolTipManager.cpp notifyEventFrameStart :60-135: mDelayVisible = 0.5 s,
    // :20; a move before that restarts the wait, a move after it moves the tip, a held button or leaving the widget hides it; Kenshi's data sets
    // no other delay). Meitou raises no tooltip event for a faithful screen's widget: the same rule over the polled pointer, in real time.
    static readonly TooltipTimer tip = new();
    static bool Tip(bool over, Vector2 at) => tip.Update(over, ScreenHost.AnyHeld, at.X, at.Y, ScreenHost.UiClock.Elapsed.TotalSeconds);

    static readonly IReadOnlyDictionary<string, string> Shown = new Dictionary<string, string>(StringComparer.Ordinal) { ["Visible"] = "true" };

    /// <summary>The size of a widget's parent as Meitou arranges it: the window's client (""), a scroll view's canvas, else the parent's rectangle.</summary>
    static (int W, int H) ParentSize(ArenaLayout l, string parent)
    {
        if (parent.Length == 0) return (l.ClientWidth, l.ClientHeight);
        if (l.Canvas.TryGetValue(parent, out var c)) return c;
        return l.Rects.TryGetValue(parent, out var r) ? (r.W, r.H) : (1, 1);
    }

    static UiRect Fraction(LayoutRect r, int pw, int ph)
    {
        float w = Math.Max(1, pw), h = Math.Max(1, ph);
        return new UiRect(r.X / w, r.Y / h, r.W / w, r.H / h);
    }

    /// <summary>The client's top left on the screen (the arranged window's client part, else the layout's window + the template's client offset).</summary>
    static Vector2 ClientOrigin(ArenaLayout l)
    {
        if (ScreenHost.ControlsOf(ArenaUISim.ScreenId) is { } controls)
            foreach (var c in controls)
                if (c.Properties.GetValueOrDefault("TemplateOwner") == N.Window && c.Properties.GetValueOrDefault("TemplatePart") == "Client")
                    return new Vector2(c.Bounds.X, c.Bounds.Y);
        return new Vector2(l.Window.X + ArenaUILayout.WindowClientX, l.Window.Y + ArenaUILayout.WindowClientY);
    }

    static void Apply(Plan p, ScreenBinding b)
    {
        foreach (var (n, t) in p.Texts) b.Text(n, t);
        foreach (var n in p.Hidden) b.Hide(n);
        foreach (var n in p.Disabled) b.Disable(n);
        foreach (var (n, props) in p.Props) b.Props(n, props);
        foreach (var (n, f) in p.Sizes) b.Size(n, f);
        foreach (var (n, f) in p.Progress) b.Progress(n, f);
        foreach (var (n, c) in p.Canvas) b.Bindings.Canvas[n] = c;
        foreach (var (n, who) in p.Faces) b.Portrait(n, who);
        b.Place(N.Window, p.Root);   // RefreshResponsiveMode's size (MyGuiScreenHost.PlaceRoots centres the window and keeps its offset)
    }

    // C++: ArenaUI.cpp:3222-3236 AbandonWorldState, its window-side part: the four lists and three columns back to the top, the search placeholder
    // flag set (the C++ leaves the box's text as it is), and the window state (the drag forgotten, the tip hidden). The rest is the sim half's (4.5a).
    static void NewWorld(Meitou.Game.Ui.MyGuiScreenHost host)
    {
        string id = ArenaUISim.ScreenId;
        if (host.IsRegistered(id))
        {
            foreach (var list in new[] { ArenaList.Roster, ArenaList.TeamA, ArenaList.TeamB, ArenaList.Pool }) host.SetScroll(id, N.ScrollOf(list), 0);
            for (int i = 0; i < 3; i++) host.SetScroll(id, N.Column(i), 0);
        }
        searchPlaceholderActive = true;
        searchFocused = false;
        ResetWindowState();
    }

    /// <summary>The window-side state that must not outlive one showing of the window (the pattern of PgScreen's docs, src/Ui/ScreenHost.cs): a press,
    /// drag or double-click timer in flight (the C++ DestroyPanelWidgets clears g_dragCharacter / g_rowDragActive) and the settings tip's wait (a
    /// window closed with the pointer on the medical button reopens without the tip). Called by <see cref="Closed"/>, <see cref="NewWorld"/> and the
    /// first bind after opening.</summary>
    static void ResetWindowState()
    {
        pointer.Reset();
        tip.Reset();
    }

    // ---------- the search box (C++ :1685-1735) ----------

    // OnRosterSearchFocus: the first focus clears the placeholder; OnRosterSearchBlur: an empty box shows the placeholder again and clears the
    // query (sent as ArenaSearch("")). The box's text is never bound from the model (it lags a tick): MyGuiScreenHost keeps what is typed.
    static void Search(Meitou.Game.Ui.MyGuiScreenHost host)
    {
        string id = ArenaUISim.ScreenId;
        bool focused = host.Focus is { } f && f.Screen == id && f.Widget == N.Search;
        if (focused && !searchFocused && searchPlaceholderActive)
        {
            searchPlaceholderActive = false;
            host.SetText(id, N.Search, "");
        }
        else if (!focused && searchFocused && !searchPlaceholderActive && host.GetText(id, N.Search).Length == 0)
        {
            searchPlaceholderActive = true;
            host.SetScroll(id, N.Roster, 0);   // OnRosterSearchBlur :1730: g_rosterScroll->setViewOffset(IntPoint())
            host.SetText(id, N.Search, ArenaUiTexts.SearchPlaceholder);
            ScreenHost.Send(new ArenaSearch("") { Tick = 0 });
        }
        searchFocused = focused;
    }

    // ---------- clicks ----------

    static PgCommand? Click(PgUiModel? m, UiClickData c)
    {
        if (Of(m) is not { } v) return null;
        switch (c.Kind)
        {
            case UiEventKind.Press:   // the buttons bound to a press (ArenaUINames.ButtonEvent): the rows' pointer, the presets
                if (N.TryParseRow(c.Widget, out var list, out int index))
                {
                    if (RowAt(v, list, index) is { } r) pointer.LeftPress(list, index, r.Id, c.X, c.Y);
                    return null;
                }
                for (int i = 0; i < 3; i++)   // OnPresetMousePressed :2215: the left button loads
                    if (c.Widget == N.Preset(i)) return new ArenaPreset(i, false) { Tick = 0 };
                return null;
            case UiEventKind.Click:   // eventMouseButtonClick (PanelButton :2541)
                return Clicked(c.Widget);
            case UiEventKind.RightPress:
                if (N.TryParseRow(c.Widget, out var rl, out int ri))
                {
                    if (RowAt(v, rl, ri) is { } r) pointer.RightPress(rl, ri, r.Id);
                    return null;
                }
                for (int i = 0; i < 3; i++)   // OnPresetMousePressed :2213: the right button saves
                    if (c.Widget == N.Preset(i)) return new ArenaPreset(i, true) { Tick = 0 };
                return null;
            case UiEventKind.Text when c.Widget == N.Search:   // OnRosterSearchChanged :1685-1692
                if (searchPlaceholderActive) return null;
                ScreenHost.Host?.SetScroll(ArenaUISim.ScreenId, N.Roster, 0);   // :1690 g_rosterScroll->setViewOffset(IntPoint())
                return new ArenaSearch(c.Text) { Tick = 0 };
            case UiEventKind.Release:
            {
                var at = new Vector2(c.X, c.Y);
                (ArenaList, int)? under = N.TryParseRow(c.Widget, out var ul, out int ui) ? (ul, ui) : null;
                var prisoner = PrisonerRowAt(v, at) is { } pr && RowAt(v, pr.List, pr.Index) is { } prow ? prow.Id : CharacterId.None;
                foreach (var a in pointer.LeftRelease(under, TargetAt(at), prisoner, CharacterId.None, ScreenHost.UiClock.Elapsed.TotalSeconds))
                    ScreenHost.Send(Command(a));
                return null;
            }
            case UiEventKind.RightRelease:
            {
                var at = new Vector2(c.X, c.Y);
                var prisoner = PrisonerRowAt(v, at) is { } pr && RowAt(v, pr.List, pr.Index) is { } prow ? prow.Id : CharacterId.None;
                return pointer.RightRelease(TargetAt(at), prisoner) is { } a ? Command(a) : null;
            }
            default:
                return null;
        }
    }

    static PgCommand Command(ArenaRowAction<CharacterId> a) => a.Kind switch
    {
        ArenaRowActionKind.Click => new ArenaRowClick(a.List, a.Id) { Tick = 0 },
        ArenaRowActionKind.DoubleClick => new ArenaRowDoubleClick(a.List, a.Id) { Tick = 0 },
        _ => new ArenaRowRelease(a.List, a.Id, a.Right, a.Target, a.Prisoner) { Tick = 0 },
    };

    /// <summary>A button click -> its C++ handler's command (CreatePanel's eventMouseButtonClick delegates); the drawer toggles are window-side
    /// (OnToggleRosterDrawer / OnToggleSettingsDrawer :2492-2510 touch no sim state); the presets' click handler does nothing
    /// (OnPresetButtonClick :2196-2199: they act on the press).</summary>
    static PgCommand? Clicked(string widget)
    {
        switch (widget)
        {
            case N.ModeAvB: return new ArenaSetMode(ArenaUiMode.AvB) { Tick = 0 };
            case N.ModeTeams1v1: return new ArenaSetMode(ArenaUiMode.Teams1v1) { Tick = 0 };
            case N.ModeLast: return new ArenaSetMode(ArenaUiMode.LastStanding) { Tick = 0 };
            case N.DestA: return new ArenaSetDest(ArenaDest.TeamA) { Tick = 0 };
            case N.DestB: return new ArenaSetDest(ArenaDest.TeamB) { Tick = 0 };
            case N.Remove: return new ArenaRemoveSelected { Tick = 0 };
            case N.AutoBalance: return new ArenaToggleBalanceMenu { Tick = 0 };
            case N.BalanceSkill: return new ArenaBalance(false) { Tick = 0 };
            case N.BalanceRating: return new ArenaBalance(true) { Tick = 0 };
            case N.Swap: return new ArenaSwap { Tick = 0 };
            case N.Clear: return new ArenaClear { Tick = 0 };
            case N.LocArena: return new ArenaLocation(ArenaLocationMode.Arena) { Tick = 0 };
            case N.LocSmall: return new ArenaLocation(ArenaLocationMode.SmallArena) { Tick = 0 };
            case N.LocBanner: return new ArenaLocation(ArenaLocationMode.Banner) { Tick = 0 };
            case N.Medical: return new ArenaMedicalToggle { Tick = 0 };
            case N.Recruit: return new ArenaRecruit { Tick = 0 };
            case N.Start: return new ArenaStart { Tick = 0 };
            case N.Stop: return new ArenaStop { Tick = 0 };
            case N.RosterDrawer:   // OnToggleRosterDrawer :2492-2500
                if (!compact) return null;
                rosterDrawerOpen = !rosterDrawerOpen;
                if (rosterDrawerOpen) settingsDrawerOpen = false;
                return null;
            case N.SettingsDrawer:   // OnToggleSettingsDrawer :2502-2510
                if (!compact) return null;
                settingsDrawerOpen = !settingsDrawerOpen;
                if (settingsDrawerOpen) rosterDrawerOpen = false;
                return null;
        }
        return null;
    }

    // ---------- hit tests (C++ PointInside :776-789: every ancestor intersected; Meitou gives each control its clip) ----------

    static bool Inside(UiControl c, Vector2 p) => c.Bounds.Contains(p.X, p.Y) && (c.Clip is not { } clip || clip.Contains(p.X, p.Y));

    static UiControl? Control(string name)
    {
        if (ScreenHost.ControlsOf(ArenaUISim.ScreenId) is not { } controls) return null;
        for (int i = controls.Count - 1; i >= 0; i--) if (controls[i].Name == name) return controls[i];
        return null;
    }

    static bool Over(string name, Vector2 p) => float.IsFinite(p.X) && Control(name) is { } c && Inside(c, p);

    /// <summary>The visible drop list under the point (OnPortraitRowRelease :1277-1294 / ApplyDragHighlights :1186-1188: team A, team B, the pool,
    /// the roster; a hidden list is not arranged, so it is never hit).</summary>
    static ArenaDropTarget TargetAt(Vector2 p)
    {
        if (Over(N.TeamA, p)) return ArenaDropTarget.TeamA;
        if (Over(N.TeamB, p)) return ArenaDropTarget.TeamB;
        if (Over(N.Pool, p)) return ArenaDropTarget.Pool;
        if (Over(N.Roster, p)) return ArenaDropTarget.Roster;
        return ArenaDropTarget.None;
    }

    /// <summary>FindAssignedPrisonerRowAtPoint (:988-996): a team / pool row of a roster prisoner under the point.</summary>
    static (ArenaList List, int Index)? PrisonerRowAt(ArenaUiView<CharacterId> v, Vector2 p)
    {
        if (!float.IsFinite(p.X) || ScreenHost.ControlsOf(ArenaUISim.ScreenId) is not { } controls) return null;
        for (int i = controls.Count - 1; i >= 0; i--)
        {
            var c = controls[i];
            if (!N.TryParseRow(c.Name, out var list, out int index) || list == ArenaList.Roster || !Inside(c, p)) continue;
            return RowAt(v, list, index) is { Prisoner: true } ? (list, index) : null;
        }
        return null;
    }

    static IReadOnlyList<ArenaRowView<CharacterId>> RowsOf(ArenaUiView<CharacterId> v, ArenaList list) =>
        list switch { ArenaList.Roster => v.Roster, ArenaList.TeamA => v.TeamA, ArenaList.TeamB => v.TeamB, _ => v.Pool };

    static ArenaRowView<CharacterId>? RowAt(ArenaUiView<CharacterId> v, ArenaList list, int index)
    {
        var rows = RowsOf(v, list);
        return index >= 0 && index < rows.Count ? rows[index] : null;
    }

    static ArenaRowView<CharacterId>? Find(ArenaUiView<CharacterId> v, ArenaList list, CharacterId id)
    {
        foreach (var r in RowsOf(v, list)) if (r.Id == id) return r;
        return null;
    }

    // ---------- portraits ----------

    static (PgUiModel? Model, IReadOnlyList<CharacterId> List) portraitCache = (null, []);

    static IReadOnlyList<CharacterId> Portraits(PgUiModel? m)
    {
        if (ReferenceEquals(portraitCache.Model, m)) return portraitCache.List;
        if (Of(m) is not { } v) return [];
        var list = new List<CharacterId>();
        if (v.PreviewImageVisible && v.HasPreview) list.Add(v.Preview);
        foreach (var rows in new[] { v.Roster, v.TeamA, v.TeamB, v.Pool })
            foreach (var r in rows)
            {
                list.Add(r.Id);
                if (r.HasHandler) list.Add(r.Handler);
            }
        portraitCache = (m, list);
        return list;
    }

    // ---------- PgCheck ui: one model per shape ----------

    static IReadOnlyList<PgUiModel?> CheckModels()
    {
        static ArenaRowView<CharacterId> Row(int slot, ArenaList list, bool handler = false) => new(new CharacterId(slot, 0), list, "Fighter " + slot,
            "Fighter " + slot + (list == ArenaList.Roster ? "" : "\nUnavailable - Too far away"), slot == 0, list == ArenaList.Roster, .6f,
            RosterStatusPolicy.Band.Orange, handler, handler ? new CharacterId(slot + 20, 0) : CharacterId.None, handler ? "Handler: Ann" : "",
            true, false, handler);
        static ValueList<ArenaRowView<CharacterId>> Rows(int n, ArenaList list, int handlerAt = -1) =>
            new(Enumerable.Range(0, n).Select(i => Row(i + (int)list * 6, list, i == handlerAt)));
        static ArenaUiModel Model(int roster, int a, int b, int pool, bool team, bool balance, int handlerAt = -1)
        {
            var v = new ArenaUiView<CharacterId>
            {
                TeamMode = team, BalanceMenuOpen = balance, Mode = team ? ArenaUiMode.AvB : ArenaUiMode.LastStanding, Preview = CharacterId.None,
                Roster = Rows(roster, ArenaList.Roster), TeamA = Rows(a, ArenaList.TeamA, handlerAt), TeamB = Rows(b, ArenaList.TeamB),
                Pool = Rows(pool, ArenaList.Pool, handlerAt), Presets = new([new(true, true, false, "Preset 1"), new(true, true, false, "Preset 2"), new(true, true, false, "Preset 3")]),
                ShapeKey = ArenaUiView<CharacterId>.ShapeOf(roster, a, b, pool, team, balance),
            };
            return new ArenaUiModel(v);
        }
        return
        [
            null,
            Model(0, 0, 0, 0, true, false),
            Model(6, 2, 2, 0, true, false),
            Model(6, 2, 2, 0, true, true),
            Model(12, 3, 1, 0, true, false, handlerAt: 1),
            Model(6, 0, 0, 4, false, false),
            Model(6, 0, 0, 4, false, true, handlerAt: 0),
            Model(1, 0, 0, 0, false, false),
        ];
    }

    /// <summary>PgCheck ui: the compact shape (the window-side flag) for the next layouts built.</summary>
    internal static void SetCompactForCheck(bool value, bool rosterDrawer = false, bool settingsDrawer = false)
    {
        compact = value;
        rosterDrawerOpen = rosterDrawer;
        settingsDrawerOpen = settingsDrawer;
    }

    /// <summary>The window is not open (the framework calls it every frame while it is not): on the first such frame after a showing, the window
    /// state is reset.</summary>
    internal static void Closed()
    {
        if (!wasOpen) return;
        wasOpen = false;
        ResetWindowState();
    }
}
