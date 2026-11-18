// Port of the window half of upstream/ProvingGrounds/plugin/src/DebugMenu.h/.cpp (Proving Grounds by Shutori, GPLv3), task 4.6b (PA §2.24;
// CAP 7.0-7.2, 7.10). The rules half (Toggle, every handler, every caption) is Core/Glue/DebugMenuLogic.cs + src/Game/DebugMenuSim.cs, which
// publishes the immutable DebugMenuUiModel. This file is the window: the layout built call by call from Create (DebugMenu.cpp:535-640, with
// MakeLabel :88-107, MakeButton :133-141, NativeUI) with LayoutBuilder (same widget types, skins, names, captions, alignments), every rectangle
// from Layout (Core/Glue/DebugMenuLayout.cs, tested), the bindings and the clicks; and the debug menu key (a PlayerInterface.UiKey postfix,
// QuestLog's hotkey recipe, CAP 7.10). Window thread only.
//
// Engine calls of the C++ window and what replaces them:
//   createWidgetReal<Window>("Kenshi_WindowCX", 0.12, 0.09, 0.76, 0.82,  -> LayoutBuilder XML (Build) -> ScreenHost (Register / Replace); the root's
//     Align::Center, "Window", ...) + createWidget (Create)                  screen rectangle from DebugMenuLayout (int casts as MyGUI's)
//   setCoord / setSize (Layout, PlaceText; run by Refresh and the           -> DebugMenuLayout's rectangles in the XML: the model's Revision (a Layout
//     client's eventChangeCoord)                                              pass after a laid-out text changed), the view size and the font in the
//                                                                             shape key
//   setCaption (Refresh, the handlers, AbandonWorldState)                   -> UiBindings.Text from the model each frame
//   setVisible (Refresh: the two tabs' widgets, the rows)                   -> UiBindings.Hidden from the model
//   setEnabled (Refresh: Prev / Next, the diagnostic actions)               -> UiBindings.Disabled from the model
//   setStateSelected (Refresh: the tabs, the selected row)                  -> Properties StateSelected + Checked (NativeUI.SelectedState)
//   setNeedMouseFocus(false) / setNeedKeyFocus(false) (MakeLabel)           -> Property NeedMouse false on the labels inside buttons and the page label
//   ScrollView::setCanvasSize / setViewOffset (Layout)                      -> UiBindings.Canvas; Meitou keeps the offset by name and clamps it
//   eventMouseButtonClick (every MakeButton: tabs, Prev, Next, rows,        -> DebugMenuPress (the button's name + what the drawn model showed for
//     actions, diagnostic actions)                                            it, task 8.7b) -> DebugMenuSim.ApplyPress (a stale click refused)
//   eventWindowButtonPressed "close" (OnWindowButtonPressed)                -> the framework's Closed report
//   eventMouseWheel (OnWheel / BindWheel :109-131, 2 rows a notch)          -> Meitou's own ScrollView wheel (divergences.md, as the Arena window)
//   getTextSize / getFontHeight                                             -> TextMeasure / ScreenHost.FontHeight
//   GetAsyncKeyState(VK_F8) (ArenaUI.cpp:2969, PollF8Toggle)                -> the PlayerInterface.UiKey postfix below: the "Debug menu key" (Off / O /
//                                                                             N / V, task 2.6; never F8, Meitou's screenshot key) sends DebugMenuKeyPressed
using System.Globalization;
using Meitou.Data.Ui;
using Meitou.Game.Host.Input;
using Meitou.Modding;
using Meitou.Simulation;
using ProvingGrounds.Core;
using ProvingGrounds.Core.Glue;
using ProvingGrounds.Engine;
using ProvingGrounds.Game;
using N = ProvingGrounds.Core.Glue.DebugMenuNames;
using T = ProvingGrounds.Core.Glue.DebugMenuTexts;

namespace ProvingGrounds.Ui;

/// <summary>The glue of the original DebugMenu.h entry points the system calls (sim thread). The window itself is <see cref="DebugMenuScreen"/>.</summary>
sealed class DebugMenu(DebugMenuSim sim)
{
    internal DebugMenuSim Sim => sim;

    // C++: DebugMenu.cpp:662, called from ArenaUI.cpp:3154 (ForgottenGUI_update_hook)
    internal void Tick(World world) => sim.Tick(world);

    // C++: ArenaUI.cpp:2967-2976 PollF8Toggle (the frame loop's ArenaUI.PollDebugMenuKey slot, :3153)
    internal void PollKey(World world) => sim.PollKey(world);

    // Save safety (task 2.5, WorldLifecycle.cpp:70, :261) calls it.
    internal void AbandonWorldState(World world) => sim.AbandonWorldState(world);   // C++ DebugMenu.cpp:520-533
}

/// <summary>The text sizes the debug menu's Layout asks (MyGUI <c>getTextSize</c>), through the screen's <see cref="TextMeasure"/>: the
/// Kenshi_WordWrapEmpty labels in the standard font, the Kenshi_PaintedWordWrapEmpty labels and the buttons' captions in the painted font.</summary>
sealed class DebugMenuTextMeasure(TextMeasure text) : IDebugMenuTextMeasure
{
    public int StandardHeight(string s, int width) => text.Height(TextMeasure.StandardFont, s, width);
    public int PaintedHeight(string s, int width) => width <= 0 ? text.Size(TextMeasure.PaintedFont, s, 0).Height : text.Height(TextMeasure.PaintedFont, s, width);
    public int ButtonWidth(string caption) => text.Width(TextMeasure.PaintedFont, caption);
}

/// <summary>
/// The debug menu window <c>pg.debug</c> (task 4.6b), declared to the window framework (src/Ui/ScreenHost.cs). Its shape key is the C++'s Layout
/// passes (the model's Revision) and the game window's size; every caption, visibility, enabled and selected state is bound from the model.
/// </summary>
static class DebugMenuScreen
{
    static readonly TextMeasure text = new();
    static readonly DebugMenuTextMeasure measure = new(text);
    static (int W, int H) viewFor;
    static int keyVersion;
    static bool wasOpen;
    static bool scrollBar;   // the scroll view's vertical bar after the last Layout (its view width, DebugMenu.cpp:182)

    static DebugMenuView? Of(PgUiModel? m) => m is DebugMenuUiModel d ? d.View : null;

    internal static readonly PgScreen Declaration = new()
    {
        Id = DebugMenuSim.ScreenId,
        ShapeKey = m => Of(m) is { } v ? "debug/" + v.Revision.ToString(CultureInfo.InvariantCulture) : "debug/none",
        BeforeFrame = ViewChanged,
        KeyVersion = () => keyVersion,
        Layout = Build,
        Bind = Bind,
        WhileClosed = () => { if (wasOpen) ResetWindowState(closing: true); },
        NewWorld = _ => ResetWindowState(closing: true),
        CheckModels = CheckModels,
        Click = Click,
    };

    /// <summary>Task 8.7b (the window framework's drawn-model clicks, 6.4b): a button click sends <see cref="DebugMenuPress"/> with what the
    /// model the window last DREW showed for that widget (<see cref="DebugMenuDrawn.Of"/>); the sim refuses it when its state differs. No drawn
    /// model, no press. Other event kinds go as before (UiClick: the sim ignores them).</summary>
    static PgCommand? Click(PgUiModel? m, UiClickData c)
    {
        if (c.Kind != UiEventKind.Click) return new UiClick(c) { Tick = 0 };
        return Of(m) is { } v ? new DebugMenuPress(c.Widget, DebugMenuDrawn.Of(v, c.Widget)) { Tick = 0 } : null;
    }

    /// <summary>Window-side state that must not outlive one opening of the window: the bind plan (called on close, a new world and the first bind
    /// after opening).</summary>
    static void ResetWindowState(bool closing)
    {
        wasOpen = false;
        plan = null;
        if (closing) built = default;   // kept at the first bind after opening: that bind places the layout the open request's Replace built
    }

    // the window's size follows the view (MyGUI made it from the view's fractions at creation): a new view size lays it out again
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

    static PgWindowLayout Compute(DebugMenuView? v, int font)
    {
        text.UseCurrent();
        var (w, h) = View();
        v ??= new DebugMenuView();
        var l = DebugMenuLayout.Compute(new DebugMenuLayoutInput
        {
            ViewWidth = w, ViewHeight = h, FontHeight = font, Fighters = v.Fighters, Heading = v.Heading, Rows = v.Rows,
            SelectedName = v.SelectedName, Rating = v.Rating, Marks = v.Marks, DiagnosticSummary = v.DiagnosticSummary,
            DiagnosticLabels = v.DiagnosticLabels, ScrollBarBefore = scrollBar,
        }, measure);
        scrollBar = l.ScrollBar;
        return l;
    }

    // C++: DebugMenu.cpp:535-640 Create, in creation order (with MakeLabel :88-107, MakeButton :133-141)
    static LayoutBuilder Build(PgUiModel? m, int font)
    {
        var v = Of(m);
        var l = Compute(v, font);
        built = (m, View(), font, l);   // reused by the bind (one computation per layout)
        var b = new LayoutBuilder();
        LayoutRect R(string name) => l.Rects.TryGetValue(name, out var r) ? r : new LayoutRect(0, 0, 100, 24);
        void Button(string name, string caption) { var r = R(name); NativeUI.Button(b, r.X, r.Y, r.W, r.H, name, caption); }
        // MakeLabel(painted = false): NativeUI::WrappedLabel; mouse and key focus off (BindWheel turns the mouse back on for the scroll's labels)
        void Wrapped(string name, string caption, bool mouse)
        {
            var r = R(name);
            NativeUI.WrappedLabel(b, r.X, r.Y, r.W, r.H, name, caption);
            if (!mouse) b.Prop("NeedMouse", "false");
            b.Close();
        }
        // MakeLabel(painted = true): an EditBox "Kenshi_PaintedWordWrapEmpty" with the caption, mouse and key focus off (the button stays the target)
        void Painted(string name, string caption)
        {
            var r = R(name);
            b.Open("EditBox", "Kenshi_PaintedWordWrapEmpty", r.X, r.Y, r.W, r.H, name).Prop("Caption", caption).Prop("NeedMouse", "false").Close();
        }

        b.OpenReal("Window", "Kenshi_WindowCX", .12f, .09f, .76f, .82f, N.Window, "Center", "Window").Prop("Caption", T.Caption);
        Button(N.FightersTab, T.FightersTab); b.Close();
        Button(N.DiagnosticsTab, T.DiagnosticsTab); b.Close();
        Wrapped(N.Heading, T.HeadingFighters, mouse: false);
        Button(N.Prev, T.Prev); b.Close();
        Button(N.Next, T.Next); b.Close();
        var pr = R(N.Page);
        NativeUI.Label(b, pr.X, pr.Y, pr.W, pr.H, N.Page, T.PageOne).Prop("TextAlign", "Center").Prop("NeedMouse", "false").Close();
        var sr = R(N.Scroll);
        NativeUI.Scroll(b, sr.X, sr.Y, sr.W, sr.H, N.Scroll);
        Wrapped(N.Selected, T.SelectCharacter, mouse: true);
        Wrapped(N.Rating, T.RatingNone, mouse: true);
        Wrapped(N.Marks, T.MarksNone, mouse: true);
        for (int i = 0; i < DebugMenuLogic<CharacterId>.RowsPerPage; ++i)
        {
            Button(N.Row(i), "");
            Painted(N.RowText(i), "");
            b.Close();
        }
        for (int i = 0; i < N.Actions.Count; ++i)
        {
            Button(N.Actions[i], "");
            Painted(N.LabelOf(N.Actions[i]), T.ActionCaptions[i]);
            b.Close();
        }
        Wrapped(N.DiagnosticSummary, T.DiagnosticOff, mouse: true);
        for (int i = 0; i < N.Diagnostics.Count; ++i)
        {
            Button(N.Diagnostics[i], "");
            Painted(N.LabelOf(N.Diagnostics[i]), "");
            b.Close();
        }
        b.Close();   // scroll
        var st = R(N.Status);
        b.Open("EditBox", "Kenshi_WordWrap", st.X, st.Y, st.W, st.H, N.Status).Prop("Caption", T.StatusStart).Close();
        b.Close();   // window
        return b;
    }

    // ---------- bind ----------

    // setStateSelected binds StateSelected AND Checked (Meitou paints the selected look from Checked): NativeUI.SelectedState
    static readonly IReadOnlyDictionary<string, string> Selected = NativeUI.SelectedState(true);
    static readonly IReadOnlyDictionary<string, string> NotSelected = NativeUI.SelectedState(false);

    sealed record Plan(UiRect Root, (float W, float H) Canvas);

    static Plan? plan;
    static (PgUiModel? Model, int W, int H, int Font) planKey;
    static (PgUiModel? Model, (int W, int H) View, int Font, PgWindowLayout? Layout) built;

    static void Bind(PgUiModel? m, ScreenBinding b)
    {
        if (!wasOpen) { ResetWindowState(closing: false); wasOpen = true; }
        if (Of(m) is not { } v) return;
        var (w, h) = View();
        var key = (m, w, h, ScreenHost.FontHeight);
        if (plan is null || planKey != key)
        {
            var l = ReferenceEquals(built.Model, m) && built.View == (w, h) && built.Font == ScreenHost.FontHeight && built.Layout is { } bl
                ? bl : Compute(v, ScreenHost.FontHeight);
            var c = l.Canvas.TryGetValue(N.Scroll, out var cv) ? cv : (1, 1);
            plan = new Plan(new UiRect(l.Window.X, l.Window.Y, l.Window.W, l.Window.H), (c.Item1, c.Item2));
            planKey = key;
        }
        // C++ Refresh :290-297 (tabs, heading, the two tabs' widgets) and :332-385 (rows, page, details); the status line (every handler)
        b.Props(N.FightersTab, v.Fighters ? Selected : NotSelected);
        b.Props(N.DiagnosticsTab, v.Fighters ? NotSelected : Selected);
        b.Text(N.Heading, v.Heading);
        b.Text(N.Status, v.Status);
        if (v.Fighters)
        {
            b.Text(N.Page, v.PageLabel);
            if (!v.PrevEnabled) b.Disable(N.Prev);
            if (!v.NextEnabled) b.Disable(N.Next);
            b.Text(N.Selected, v.SelectedName);
            b.Text(N.Rating, v.Rating);
            b.Text(N.Marks, v.Marks);
            b.Hide(N.DiagnosticSummary);
            foreach (var d in N.Diagnostics) b.Hide(d);
        }
        else
        {
            b.Hide(N.Prev);
            b.Hide(N.Next);
            b.Hide(N.Page);
            b.Hide(N.Selected);
            b.Hide(N.Rating);
            b.Hide(N.Marks);
            foreach (var a in N.Actions) b.Hide(a);
            b.Text(N.DiagnosticSummary, v.DiagnosticSummary);
            for (int i = 0; i < N.Diagnostics.Count; ++i)
            {
                b.Text(N.LabelOf(N.Diagnostics[i]), v.DiagnosticLabels[i]);
                if (!v.DiagnosticEnabled[i]) b.Disable(N.Diagnostics[i]);
            }
        }
        for (int i = 0; i < v.Rows.Count; ++i)
        {
            var row = v.Rows[i];
            if (!row.Visible) { b.Hide(N.Row(i)); continue; }
            b.Text(N.RowText(i), row.Text);
            b.Props(N.Row(i), row.Selected ? Selected : NotSelected);
        }
        b.Bindings.Canvas[N.Scroll] = plan.Canvas;
        b.Place(N.Window, plan.Root);
    }

    // ---------- PgCheck ui: one model per shape ----------

    static IReadOnlyList<PgUiModel?> CheckModels()
    {
        static DebugMenuUiModel Model(int revision, int rows, bool fighters, bool selection)
        {
            var list = Enumerable.Range(0, 8).Select(i => i < rows
                ? new DebugRowView(true, T.RowText("Fighter " + i, i % 3 == 2, 150f + i, i), selection && i == 0, "id" + i)
                : new DebugRowView(false, "", false));
            var d = new TownDiagnosticData.State();
            return new DebugMenuUiModel(new DebugMenuView
            {
                Revision = revision, Fighters = fighters, Heading = fighters ? T.HeadingFighters : T.HeadingDiagnostics,
                PageLabel = T.Page(1, 1), PrevEnabled = false, NextEnabled = rows == 8, Rows = new(list),
                SelectedName = selection ? "Fighter 0" : T.SelectCharacter, Rating = selection ? T.Rating(150f) : T.RatingNone,
                Marks = selection ? T.Marks(0) : T.MarksNone, SelectedKey = selection ? "id0" : "", DiagnosticSummary = Core.TownDiagnosticRun.StatusText(d, ""),
                DiagnosticLabels = new(T.DiagnosticLabels(d, fighters ? 0 : 1)), DiagnosticEnabled = new(T.DiagnosticEnabled(d)),
                RequestOpen = true, RequestSerial = 1,
            });
        }
        return
        [
            null,
            new DebugMenuUiModel(new DebugMenuView()),
            Model(1, 0, true, false),
            Model(2, 3, true, true),
            Model(3, 8, true, false),
            Model(4, 0, false, false),
        ];
    }
}

/// <summary>
/// The debug menu key (task 4.6b; CAP 7.10, QuestLog's Ui/Hotkey.cs recipe): a postfix on <c>PlayerInterface.UiKey</c> that, for the key the
/// "Debug menu key" setting names (Off / O / N / V, task 2.6, published at once by the settings page), takes the press where the game left it
/// unused (<see cref="DebugMenuKeyRule"/>) and sends <see cref="DebugMenuKeyPressed"/>; the sim half toggles the menu in the frame loop's
/// PollDebugMenuKey slot. Window thread.
/// </summary>
static class DebugMenuKey
{
    static ModContext? ctx;
    static bool failedLogged;

    internal static IEnumerable<PgPatch> Patches() =>
    [
        new(Part.DebugMenuKey, "PlayerInterface.UiKey postfix (the debug menu key)", Members.UiKey, null, PgPatches.Method(typeof(DebugMenuKey), nameof(AfterUiKey))),
    ];

    /// <summary>Applies the patch (a missing member turns the key off with one log line; the window and pg debug stay).</summary>
    internal static void Install(ModContext c)
    {
        ctx = c;
        try
        {
            var missing = Members.Missing(Part.DebugMenuKey).ToList();
            if (missing.Count > 0) { c.Log($"debug menu key off: {string.Join(", ", missing)} not found"); return; }
            foreach (var p in Patches()) PgPatches.Apply(c.Harmony, p);
        }
        catch (Exception e) { c.Log($"debug menu key off: {e.GetType().Name}: {e.Message}"); }
    }

    static (string Setting, Key? Key) bound = ("", null);

    /// <summary>The game key the setting names (DebugMenuKeyRule.KeyName parsed once per setting).</summary>
    static Key? KeyOf(string setting)
    {
        if (setting == bound.Setting) return bound.Key;
        Key? k = DebugMenuKeyRule.KeyName(setting) is { } name && Enum.TryParse<Key>(name, out var parsed) ? parsed : null;
        bound = (setting, k);
        return k;
    }

    // C++: ArenaUI.cpp:2967-2976 PollF8Toggle's key read (the toggle itself runs in the sim's slot)
    static void AfterUiKey(object __instance, Key key, bool ctrl, ref bool __result)
    {
        using var perfScope = PgPerf.Measure(PgPerf.UiKey);   // task 9.2a: the cost instrument (off: one volatile read)
        try
        {
            string setting = PGSettingsUI.DebugMenuKeyPublished;
            if (KeyOf(setting) is not { } bound || key != bound || __result) return;
            bool reserved = Members.ReservedKeys?.GetValue(__instance) is HashSet<Key> keys && keys.Contains(key)
                || (Members.GameHotkeysProp?.GetValue(__instance) is { } hotkeys && Members.IsHotkey?.Invoke(hotkeys, [key]) is true);
            if (!DebugMenuKeyRule.Takes(setting, key.ToString(), ctrl, __result, Members.UiVisible?.GetValue(__instance) is true,
                    Members.TitleOpen?.GetValue(__instance) is true, reserved, Members.UiEditing?.GetValue(__instance) is true))
                return;
            ScreenHost.Send(new DebugMenuKeyPressed { Tick = 0 });   // sent first: a throw leaves the key to the game
            __result = true;
            if (Spikes.SpikePatches.Armed) ctx?.Log("PG-DEBUG {\"event\":\"key\",\"key\":" + PGLog.Quote(setting) + "}");
        }
        catch (Exception e)
        {
            if (!failedLogged) { failedLogged = true; ctx?.Log($"debug menu key failed: {e.GetType().Name}: {e.Message}"); }
        }
    }

    /// <summary>TEST ONLY <c>pg window key &lt;key&gt;</c> (task 4.6b, frame thread): the game's own key call (PlayerInterface.UiKey, as GameLoop.cs:873
    /// runs it for a key press; the agent channel has no key command). Null on success (with the call's result), else why not.</summary>
    internal static string? TestKey(string name, out bool taken)
    {
        taken = false;
        if (!Enum.TryParse<Key>(name, ignoreCase: true, out var key)) return $"no key named '{name}'";
        if (ScreenHost.Interface is not { } ui || Members.UiKey is not { } call) return "no interface yet, or PlayerInterface.UiKey is missing";
        try
        {
            taken = call.Invoke(ui, [key, false, false]) is true;
            return null;
        }
        catch (Exception e) { return $"{e.GetType().Name}: {(e.InnerException ?? e).Message}"; }
    }
}
