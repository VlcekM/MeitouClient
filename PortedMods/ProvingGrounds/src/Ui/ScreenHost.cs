// The window framework (task 4.4). C++: every UI file's window creation (createWidget/createWidgetReal under MyGUI::Gui, e.g. ArenaUI.cpp:2814-2926,
// ResultsUI.cpp:366-460) and its event delegates. Meitou: faithful MyGUI screens (CAP 7.2, the GuildQuests / Recap pattern with 0.17.3 names),
// spikes S6 (runtime XML layouts, Replace) and S7 (portraits), both VERIFIED live 2026-11-11.
//
// Engine calls of the original and what replaces them:
//   MyGUI::Gui::createWidgetReal / Widget::createWidget  -> LayoutBuilder (Core/Glue) -> MyGuiReader.ParseLayout -> MyGuiScreenHost.Register
//                                                           (non-modal, centred root) in a PlayerInterface.LoadUi postfix
//   Widget::setCaption / setEnabled / setVisible /       -> UiBindings Text / Disabled / Hidden / Properties in a ScreenRouter.BindFaithfulScreen
//   ImageBox textures                                       postfix (ScreenBinding below)
//   destroy + create again for a new row count           -> MyGuiScreenHost.Replace when the screen's shape key changes (S6: keeps open, offset,
//                                                           texts, focus while the focused widget name exists)
//   Widget::setVisible(true/false) of the window         -> MyGuiScreenHost.Open / Close, asked by the sim half through its model (ScreenRequests)
//   eventMouseButtonClick (a button)                     -> MyGuiScreenHost.Activated on the press, held (UiButtonClick) and sent at the left release
//                                                           over the same widget the PlayerInterface.MouseUp postfix sees (task 4.4b; MyGUI 3.2.2
//                                                           injectMouseRelease); each screen declares its buttons' C++ event (PgScreen.ButtonEvent)
//   eventMouseButtonPressed (a left press) / eventListSelectAccept / ... -> MyGuiScreenHost.Activated -> UiClickPolicy -> a PgCommand (UiClick)
//   eventMouseButtonPressed / Released (right, releases)  -> PlayerInterface.MouseDown / MouseUp postfixes hit-testing our screens (task 4.5b):
//                                                           UiEventKind.RightPress / Release / RightRelease (MyGuiScreenHost has no right press)
//   PortraitManager::setImageWidget                      -> Portraits.cs (TradeWindows.CharacterTradePortraits postfix, PortraitBar.PortraitRect)
//   getFontHeight / getTextSize (NativeUI::Spacing)       -> NativeUIMeasure.FontHeight (UiBindings.TextHeight) or NativeUI.DefaultFontHeight;
//                                                           a window's own text sizes through TextMeasure (src/Ui/TextMeasure.cs)
//   GetAsyncKeyState(VK_LBUTTON) (ArenaUI.cpp:1205)       -> the MouseDown / MouseUp postfixes, reconciled each frame with the system's buttons
//                                                           (SystemMouse below, Core/Glue/PointerButtons.cs)
// Window thread only: nothing here reads or writes the world; the windows read the published PgSnapshot and send PgCommands.
using System.Linq.Expressions;
using System.Numerics;
using HarmonyLib;
using Meitou.Game.Host.Input;
using Meitou.Data.Ui;
using Meitou.Game.Ui;
using Meitou.Modding;
using Meitou.Simulation;
using ProvingGrounds.Core.Glue;
using ProvingGrounds.Engine;

namespace ProvingGrounds.Ui;

/// <summary>
/// One Proving Grounds window, declared once (task 4.4; the Arena, Results, Leaderboard and Debug windows add theirs in 4.5 / 4.6):
/// <list type="bullet">
/// <item><see cref="Id"/>: the screen id (unique among every faithful screen).</item>
/// <item><see cref="ShapeKey"/>: what makes two layouts differ (the number of rows or cards), from the model; the framework adds the measured font
/// height. The layout is rebuilt and <c>Replace</c>d only when it changes.</item>
/// <item><see cref="Layout"/>: the layout for a model and a font height, built call by call from the original's createWidget code with
/// <see cref="LayoutBuilder"/> / <see cref="NativeUI"/>. Keep the names of focused edit boxes the same across shapes (S6).</item>
/// <item><see cref="Bind"/>: fills the texts, enabled state, sizes and pictures from the model (every button of the layout is enabled with the
/// action <c>pg:&lt;name&gt;</c> before; disable or hide here). Runs whenever the interface arranges (every tick while the game runs): cheap, no
/// formatting and no allocation in the steady state (the model carries formatted text; pass cached dictionaries to Props).</item>
/// <item><see cref="Click"/>: a widget event -> the command to send (null: none); default <see cref="UiClick"/>. A window closed from the window
/// side (Escape, its close button) reaches the sim half as a <see cref="UiClick"/> of kind <see cref="UiEventKind.Closed"/> (widget "") without
/// going through Click: the sim half then stops saying open, keeping its request serial (see TestScreenSim).</item>
/// <item><see cref="Portraits"/>: the characters whose portraits the open window shows (Portraits.cs gives each a cell).</item>
/// <item><see cref="CheckModels"/>: one model per shape, for <c>PgCheck ui</c> (offline: every layout built, parsed and arranged).</item>
/// </list>
/// <para>Patterns every window follows (task 4.5b review):</para>
/// <list type="bullet">
/// <item>Text sizes (MyGUI getTextSize): the screen owns one <see cref="TextMeasure"/> and calls its <c>UseCurrent</c> at the start of its Layout and
/// Bind; never measure another way.</item>
/// <item>Window-side state (a press or drag in flight, a double-click timer, a tooltip's wait, anything else that must not outlive one showing of the
/// window): one <c>ResetWindowState()</c> in the screen, called from its <see cref="WhileClosed"/> (once, on the first frame after a showing), its
/// <see cref="NewWorld"/> and its first <see cref="Bind"/> after opening (see ArenaScreen).</item>
/// <item>Window-side state that changes the shape (the Arena window's compact mode) is updated in <see cref="BeforeFrame"/>; <see cref="KeyVersion"/>
/// only reads it.</item>
/// </list>
/// </summary>
sealed class PgScreen
{
    public required string Id { get; init; }
    public required Func<PgUiModel?, string> ShapeKey { get; init; }
    public required Func<PgUiModel?, int, LayoutBuilder> Layout { get; init; }
    public required Action<PgUiModel?, ScreenBinding> Bind { get; init; }
    public Func<PgUiModel?, UiClickData, PgCommand?> Click { get; init; } = (_, c) => new UiClick(c) { Tick = 0 };   // the tick is set when it is sent (OnActivated)
    public Func<PgUiModel?, IReadOnlyList<CharacterId>> Portraits { get; init; } = _ => [];
    /// <summary>The MyGUI event the original binds button <c>widget</c> to (task 4.4b): <see cref="UiButtonEvent.Click"/> (eventMouseButtonClick,
    /// the default: the command is sent at the left release over the pressed button, as a <see cref="UiEventKind.Click"/>),
    /// <see cref="UiButtonEvent.Press"/> (eventMouseButtonPressed: sent on the press, <see cref="UiEventKind.Press"/>) or
    /// <see cref="UiButtonEvent.Pointer"/> (the screen copies MyGUI's release / click itself from the press: the Arena rows). Each window writes the
    /// table from the C++'s bindings.</summary>
    public Func<string, UiButtonEvent> ButtonEvent { get; init; } = _ => UiButtonEvent.Click;
    /// <summary>Whether the screen hears every pointer release while it is open (<see cref="UiEventKind.Release"/> /
    /// <see cref="UiEventKind.RightRelease"/>, the widget under the pointer when it is one of the screen's, else ""): the original's
    /// eventMouseButtonReleased of a pressed widget fires wherever the button comes up (MyGUI captures the pointer). A right press over one of the
    /// screen's enabled widgets reaches <see cref="Click"/> as <see cref="UiEventKind.RightPress"/> for every screen.</summary>
    public bool WantsReleases { get; init; }
    /// <summary>Called first in every frame of the framework (before the shape keys are asked, open or not): window-side state the shape depends on
    /// is brought up to date here (the Arena window's compact mode from the view size). Cheap, no allocation.</summary>
    public Action BeforeFrame { get; init; } = () => { };
    /// <summary>A number the window side bumps when its own state changes the shape (the Arena window's compact mode): the shape key is asked
    /// again (it is cached per model and font height otherwise). A plain read (no side effect; update the state in <see cref="BeforeFrame"/>),
    /// called every frame.</summary>
    public Func<int> KeyVersion { get; init; } = () => 0;
    /// <summary>TEST ONLY: the pointer moved outside an arrange (<c>pg window drag</c>, whose moves happen in one call): a screen that follows the
    /// pointer (a row drag) reads it here as its bind would.</summary>
    public Action TestPointerMoved { get; init; } = () => { };
    /// <summary>A new world was published (the C++ AbandonWorldState's window-side resets: scroll offsets, window-side flags). Called once per world
    /// change, before the framework closes our windows, with the screen host (the screen may not be registered yet).</summary>
    public Action<MyGuiScreenHost> NewWorld { get; init; } = _ => { };
    /// <summary>Called every frame while the screen is not open (window-side state that must not outlive the window: a press in flight; reset it
    /// once, on the first such frame, through the screen's ResetWindowState).</summary>
    public Action WhileClosed { get; init; } = () => { };
    /// <summary>Called after the layout the last <see cref="Layout"/> call built was registered or Replaced on the host without an error (task 5.4):
    /// window-side state that describes the layout shown (the Rewards window's built layout) is taken from that call only here, so a failed
    /// Register / Replace (the old layout stays on screen) leaves it describing the old one.</summary>
    public Action Registered { get; init; } = () => { };
    /// <summary>Called when the framework could not register (<c>what</c> = "register") or Replace ("layout") the screen's layout (task 6.4b):
    /// the window's own reaction (the C++ creation / refresh error line; the recruitment window reports itself closed). The failure itself is
    /// logged by the framework first.</summary>
    public Action<string, Exception> Failed { get; init; } = (_, _) => { };
    public required Func<IReadOnlyList<PgUiModel?>> CheckModels { get; init; }
}

/// <summary>What a screen's <see cref="PgScreen.Bind"/> fills (Meitou's <see cref="UiBindings"/>, src/Meitou.Data/Ui/UiDocument.cs:24-96). One
/// object, reused for every bind.</summary>
sealed class ScreenBinding
{
    UiBindings b = new();
    MyGuiLayout? layout;

    internal void Reset(UiBindings bindings, MyGuiLayout current)
    {
        b = bindings;
        layout = current;
    }

    /// <summary>The raw bindings, for what the helpers below do not cover (Lists, SelectedItems, ScrollOffsets, States, ...).</summary>
    public UiBindings Bindings => b;
    /// <summary><c>setCaption</c>.</summary>
    public void Text(string widget, string text) => b.Text[widget] = text;
    /// <summary><c>setVisible(false)</c> (the widget and everything under it).</summary>
    public void Hide(string widget) => b.Hidden.Add(widget);
    /// <summary><c>setEnabled(false)</c>.</summary>
    public void Disable(string widget) => b.Disabled.Add(widget);

    /// <summary>Properties set over the layout's (TextColour, TextAlign, tooltip_*, ...), MERGED with what is already bound for the widget (a
    /// portrait set before stays). Pass a cached dictionary: with nothing bound yet it is stored as it is (no copy).</summary>
    public void Props(string widget, IReadOnlyDictionary<string, string> props) => Merge(b, widget, props);

    // the merged property sets by widget, kept while both inputs are the same objects (Props and Portrait on one widget: no copy per frame)
    static readonly Dictionary<string, (IReadOnlyDictionary<string, string> A, IReadOnlyDictionary<string, string> B, IReadOnlyDictionary<string, string> Merged)> merges
        = new(StringComparer.Ordinal);

    /// <summary>Merges <paramref name="props"/> into the widget's bound properties: stored as is when the widget has none; else the merge, made once
    /// per pair of input objects (callers pass cached dictionaries, so the steady state allocates nothing).</summary>
    internal static void Merge(UiBindings b, string widget, IReadOnlyDictionary<string, string> props)
    {
        if (!b.Properties.TryGetValue(widget, out var current) || current.Count == 0) { b.Properties[widget] = props; return; }
        if (merges.TryGetValue(widget, out var m) && ReferenceEquals(m.A, current) && ReferenceEquals(m.B, props)) { b.Properties[widget] = m.Merged; return; }
        var merged = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in current) merged[k] = v;
        foreach (var (k, v) in props) merged[k] = v;
        merges[widget] = (current, props, merged);
        b.Properties[widget] = merged;
    }

    /// <summary>A new interface: the merge cache is emptied.</summary>
    internal static void ClearMerges() => merges.Clear();

    /// <summary><c>setCoord</c> in the widget's parent: <paramref name="rect"/> as FRACTIONS of the parent (MyGUI's real coordinates; Meitou's
    /// <c>UiBindings.RelativePositions</c>, UiDocument.cs:63, read by WidgetBounds as <c>RelativeTo(parent, true)</c>). False when the layout has no
    /// such widget.</summary>
    public bool Size(string widget, UiRect rect)
    {
        if (layout?.Find(widget) is not { } w) return false;
        b.RelativePositions[w] = rect;
        return true;
    }

    /// <summary>A widget at a SCREEN rectangle (pixels from the top left of the game window; <c>UiBindings.Positions</c>, UiDocument.cs:59).</summary>
    public void Place(string widget, UiRect screenRect) => b.Positions[widget] = screenRect;

    /// <summary>A bar filled to <paramref name="fraction"/> (0..1): a ProgressBar template's fill, any other widget cut to that width
    /// (<c>UiBindings.Progress</c>, UiDocument.cs:76, cut in Arrange :125-127) — the health bars' resize.</summary>
    public void Progress(string widget, float fraction) => b.Progress[widget] = fraction;

    /// <summary><c>EditBox::setTextColour</c> (task 7.6): Meitou draws an EditBox's caption on its template's client part (the part marked
    /// CaptionOwner), which takes the owner's WordWrap and TextAlign but not its TextColour (Meitou v0.17.3 src/Meitou.Data/Ui/UiDocument.cs:201-209,
    /// MyGuiColours.Text reads the drawn control's own TextColour, MyGuiColours.cs:167-178): the properties are merged on that part too. Pass a
    /// cached dictionary (no allocation in the steady state: the parts are found once per layout).</summary>
    public void EditTextColour(string widget, IReadOnlyDictionary<string, string> props)
    {
        Merge(b, widget, props);
        foreach (var part in CaptionParts(widget)) Merge(b, part, props);
    }

    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<MyGuiLayout, Dictionary<string, string[]>> captionParts = new();

    string[] CaptionParts(string owner)
    {
        if (layout is null) return [];
        var map = captionParts.GetValue(layout, _ => new(StringComparer.Ordinal));
        if (!map.TryGetValue(owner, out var parts))
            map[owner] = parts = [.. layout.Widgets.Where(w => w.Properties.TryGetValue("CaptionOwner", out var o) && o == owner).Select(w => w.Name)];
        return parts;
    }

    /// <summary>The portrait of <paramref name="character"/> in ImageBox <paramref name="widget"/>; false (the ImageBox hidden, the name alone left)
    /// when the character has no portrait cell (none free: a full squad).</summary>
    public bool Portrait(string widget, CharacterId character) => Portraits.Bind(b, widget, character);
}

/// <summary>
/// The window framework (task 4.4): registers every declared <see cref="PgScreen"/> on each new faithful screen host (a postfix on
/// <c>PlayerInterface.LoadUi</c>), fills their bindings (a postfix on <c>ScreenRouter.BindFaithfulScreen</c>), replaces a layout when its shape key
/// changes, opens / closes windows the sim halves ask for, reports windows closed from the window side, and turns widget events into
/// <see cref="PgCommand"/>s. The decisions are Core/Glue/ScreenHostLogic.cs's ScreenFrame (tested); this class adapts it to MyGuiScreenHost.
/// Window thread only. Assumes no hot mod reload: its static state lives for the process (Meitou loads a mod once, as the other parts assume).
/// </summary>
static class ScreenHost
{
    static ModContext? ctx;

    /// <summary>Every declared window, in order. 4.5 / 4.6 add theirs here (Arena, Results, Leaderboard, Debug).</summary>
    static readonly PgScreen[] screens = [TestScreen.Declaration, ArenaScreen.Declaration,
        ResultsScreen.Declaration, LeaderboardScreen.Declaration /* task 4.6a */, DebugMenuScreen.Declaration /* task 4.6b */,
        RewardsScreen.Declaration /* task 5.3b */, PrisonerRecruitmentScreen.Declaration /* task 6.4b */,
        TownArenaScreen.Declaration /* task 7.6 */, TownBookieScreen.Declaration, TownBookieResultScreen.Declaration /* task 8.1b */];
    static readonly Dictionary<string, PgScreen> byId = screens.ToDictionary(s => s.Id, StringComparer.Ordinal);
    static readonly HashSet<string> ids = new(byId.Keys, StringComparer.Ordinal);

    /// <summary>The newest PlayerInterface and its screen host (set by the LoadUi postfix).</summary>
    internal static object? Interface;
    internal static MyGuiScreenHost? Host;

    static readonly ScreenFrame frame = new();
    static object? lastSystem;
    static readonly Surface surface = new();
    static readonly List<ScreenState> states = new(screens.Length);
    static readonly Dictionary<string, (PgUiModel? Model, int Font, int Version, string Key)> keyCache = new(StringComparer.Ordinal);
    static readonly Dictionary<string, (MyGuiLayout Layout, (string Name, string Action)[] Buttons)> buttons = new(StringComparer.Ordinal);
    static readonly ScreenBinding binding = new();
    static readonly HashSet<string> logged = new(StringComparer.Ordinal);

    /// <summary>The standard text's font height the layouts are built with (NativeUI.Spacing / RowHeight): measured at bind, else Kenshi's at 1080 lines.</summary>
    internal static int FontHeight = NativeUI.DefaultFontHeight;
    static bool fontLogged;
    static string CMathText(float v) => float.IsFinite(v) ? Core.CMath.FormatF(v, 3) : "null";

    internal static IReadOnlyList<PgScreen> Screens => screens;

    /// <summary>A window's own failure, logged once per screen and step (task 5.3b: the Rewards window's wheel and icons).</summary>
    internal static void LogScreenError(string screen, string what, Exception e) => LogOnce(screen, what, e);

    /// <summary>One log line per screen and exception type (binds run every frame: a failing one must not flood the log).</summary>
    static void LogOnce(string screen, string what, Exception e)
    {
        if (logged.Add(screen + "|" + what + "|" + e.GetType().FullName)) ctx?.Log($"window {screen} {what} failed: {e.GetType().Name}: {e.Message}");
    }

    /// <summary>The window framework's Harmony patches (applied by <see cref="Install"/> and by PgCheck hooks, through PgPatches.Apply).</summary>
    internal static IEnumerable<PgPatch> Patches() =>
    [
        new(Part.Windows, "PlayerInterface.LoadUi postfix (register the windows)", Members.LoadUi, null, PgPatches.Method(typeof(ScreenHost), nameof(AfterLoadUi))),
        new(Part.Windows, "ScreenRouter.BindFaithfulScreen postfix (fill the windows)", Members.BindFaithfulScreen, null, PgPatches.Method(typeof(ScreenHost), nameof(AfterBind))),
        new(Part.Windows, "PlayerInterface.MouseDown prefix + postfix (right press, held buttons)", Members.MouseDown, PgPatches.Method(typeof(ScreenHost), nameof(BeforeMouseDown)), PgPatches.Method(typeof(ScreenHost), nameof(AfterMouseDown))),
        new(Part.Windows, "PlayerInterface.MouseUp prefix + postfix (releases, button clicks)", Members.MouseUp, PgPatches.Method(typeof(ScreenHost), nameof(BeforeMouseUp)), PgPatches.Method(typeof(ScreenHost), nameof(AfterMouseUp))),
        new(Part.WindowsPointerMove, "PlayerInterface.MouseMove postfix (the widget under the pointer, MyGUI's double-click timer)", Members.MouseMove, null, PgPatches.Method(typeof(ScreenHost), nameof(AfterMouseMove))),
        new(Part.WindowsLoopPass, "GameLoop.Frame prefix (count the loop passes for the held buttons)", Members.GameLoopFrame, PgPatches.Method(typeof(ScreenHost), nameof(BeforeLoopFrame)), null),
        new(Part.WindowsWheel, "MyGuiScreenHost.Wheel prefix (the Rewards window's per-widget wheel, task 5.3b)", Members.ScreenHostWheel, PgPatches.Method(typeof(RewardsScreen), nameof(RewardsScreen.BeforeWheel)), null),
        new(Part.Portraits, "TradeWindows.CharacterTradePortraits postfix (portrait cells)", Members.CharacterTradePortraits, null, PgPatches.Method(typeof(Portraits), nameof(Portraits.AfterTradePortraits))),
    ];

    /// <summary>Applies the patches part by part (a missing member turns its part off with one log line) and adds the per-frame hook.</summary>
    internal static void Install(ModContext c)
    {
        ctx = c;
        try
        {
            foreach (var part in new[] { Part.Windows, Part.WindowsLoopPass, Part.WindowsPointerMove, Part.WindowsWheel, Part.Portraits })
            {
                var missing = Members.Missing(part).ToList();
                if (part == Part.WindowsWheel)   // optional (task 5.3b): without it the host's own wheel scrolls the Rewards canvases
                {
                    if (missing.Count > 0) { c.Log($"Rewards window wheel: {string.Join(", ", missing)} not found; the screen host's own wheel scrolls its canvases"); continue; }
                    try { foreach (var p in Patches().Where(p => p.Part == part)) PgPatches.Apply(c.Harmony, p); }
                    catch (Exception e) { c.Log($"Rewards window wheel: the patch is off ({e.GetType().Name}: {e.Message}); the screen host's own wheel scrolls its canvases"); }
                    continue;
                }
                if (part == Part.WindowsPointerMove)   // optional: without it the widget under the pointer is read at each arrange only
                {
                    if (missing.Count > 0) { c.Log($"window buttons: {string.Join(", ", missing)} not found; the double-click timer follows the pointer at each arrange only"); continue; }
                    try { foreach (var p in Patches().Where(p => p.Part == part)) PgPatches.Apply(c.Harmony, p); }
                    catch (Exception e) { c.Log($"window buttons: the pointer-move patch is off ({e.GetType().Name}: {e.Message}); the double-click timer follows the pointer at each arrange only"); }
                    continue;
                }
                if (missing.Count > 0 && part == Part.WindowsLoopPass)
                {
                    c.Log($"window buttons: {string.Join(", ", missing)} not found; a release Meitou does not pass on is not noticed");
                    continue;
                }
                if (missing.Count > 0)
                {
                    c.Log($"{(part == Part.Windows ? "Proving Grounds windows" : "window portraits")} off: {string.Join(", ", missing)} not found");
                    if (part == Part.Windows) return;
                    continue;
                }
                if (part == Part.Portraits && Portraits.Prepare() is { } why) { c.Log("window portraits off: " + why); continue; }
                if (part == Part.Windows && PreparePressTaken() is { } off)   // optional: right presses then reach the windows as before
                    c.Log("window right presses the game takes first are not recognised: " + off);
                if (part == Part.WindowsLoopPass)   // optional: without it the held buttons are not reconciled, the windows stay on
                {
                    try
                    {
                        foreach (var p in Patches().Where(p => p.Part == part)) PgPatches.Apply(c.Harmony, p);
                        loopCounted = true;
                    }
                    catch (Exception e) { c.Log($"window buttons: the loop pass count is off ({e.GetType().Name}: {e.Message}); a release Meitou does not pass on is not noticed"); }
                    continue;
                }
                foreach (var p in Patches().Where(p => p.Part == part)) PgPatches.Apply(c.Harmony, p);
            }
            c.AddUi(new FrameHook());
        }
        catch (Exception e) { c.Log($"Proving Grounds windows off: {e.GetType().Name}: {e.Message}"); }
    }

    static PgSnapshot? Snapshot => ProvingGroundsSystem.Current?.Snapshot;

    /// <summary>The model a click on screen <paramref name="id"/> is judged against: the one its window was last bound (drawn) with, else the
    /// newest published one (task 6.4b review; ScreenFrame.ClickModel).</summary>
    static PgUiModel? ClickModel(string id) => frame.ClickModel(id, Snapshot?.Model(id)) as PgUiModel;

    /// <summary>The screen's shape key with the font height (cached per model object and font height: no string per frame).</summary>
    static string Key(PgScreen s, PgUiModel? model)
    {
        int version = s.KeyVersion();
        if (keyCache.TryGetValue(s.Id, out var c) && ReferenceEquals(c.Model, model) && c.Font == FontHeight && c.Version == version) return c.Key;
        string key = s.ShapeKey(model) + "|font " + FontHeight.ToString(System.Globalization.CultureInfo.InvariantCulture);
        keyCache[s.Id] = (model, FontHeight, version, key);
        return key;
    }

    /// <summary>The parsed layout of a screen for a model (a parse error throws: PgCheck ui catches it offline).</summary>
    internal static MyGuiLayout Layout(PgScreen s, PgUiModel? model, int fontHeight) => MyGuiReader.ParseLayout(s.Layout(model, fontHeight).Xml());

    /// <summary>ScreenFrame's view of the MyGuiScreenHost: builds a screen's layout for the model of this frame.</summary>
    sealed class Surface : IScreenSurface
    {
        public MyGuiScreenHost Host = null!;
        public PgSnapshot? Snap;

        public bool IsRegistered(string id) => Host.IsRegistered(id);
        public bool IsOpen(string id) => Host.IsOpen(id);
        public bool TryRegister(string id)
        {
            bool ok = Try(id, "register", () => Host.Register(id, Build(id), modal: false, centreRoot: true));
            if (ok) { Registered(id); Note(id, "registered"); }
            return ok;
        }
        public bool TryReplace(string id)
        {
            bool ok = Try(id, "layout", () => Host.Replace(id, Build(id)));   // Replace keeps open, offset, texts, focus (S6)
            if (ok) { Registered(id); Note(id, "replaced"); }
            return ok;
        }
        public void Open(string id) { Host.Open(id); Note(id, "opened"); }
        public void Close(string id) { Host.Close(id); Note(id, "closed"); }

        // One line per registration / Replace / open / close (rare events, never per frame): the state the window framework's live check reads
        // (open, offset, the focused edit box, the font height the layout was built with).
        void Note(string id, string what)
        {
            try
            {
                var f = Host.Focus;
                ctx?.Log($"PG-WINDOW {{\"screen\":\"{id}\",\"event\":\"{what}\",\"open\":{(Host.IsOpen(id) ? "true" : "false")},"
                    + $"\"offset\":\"{Host.GetOffset(id).X.ToString(System.Globalization.CultureInfo.InvariantCulture)} {Host.GetOffset(id).Y.ToString(System.Globalization.CultureInfo.InvariantCulture)}\","
                    + $"\"focus\":{(f is { } x ? "\"" + x.Screen + "/" + x.Widget + "\"" : "null")},\"font\":{FontHeight.ToString(System.Globalization.CultureInfo.InvariantCulture)},"
                    + $"\"key\":{System.Text.Json.JsonSerializer.Serialize(Key(byId[id], Snap?.Model(id)))}}}");
            }
            catch (Exception e) { LogOnce(id, "note", e); }
        }
        // Value = the request serial of the model the window closed under: the sim half ignores a Closed for another serial
        public void ReportClosed(string id, int serial) => Send(new UiClick(new UiClickData(id, "", UiEventKind.Closed, "", serial, false)) { Tick = 0 });

        MyGuiLayout Build(string id) => Layout(byId[id], Snap?.Model(id), FontHeight);
        static void Registered(string id) { try { byId[id].Registered(); } catch (Exception e) { LogOnce(id, "registered", e); } }

        static bool Try(string id, string what, Action act)
        {
            try { act(); return true; }
            catch (Exception e)
            {
                LogOnce(id, what, e);
                try { byId[id].Failed(what, e); }   // task 6.4b: the window's own reaction
                catch (Exception x) { LogOnce(id, "failed", x); }
                return false;
            }
        }
    }

    /// <summary>Enqueues a command for the next tick (window thread; the console does the same).</summary>
    internal static void Send(PgCommand command)
    {
        if (ctx is null || ProvingGroundsSystem.Current is null) return;
        ctx.Enqueue(command with { Tick = ctx.Tick + 1 });
    }

    // C++: the windows' CreatePanel / EnsureWindow (e.g. ArenaUI.cpp:2814-2926): built once; here registered once on each new screen host.
    static void AfterLoadUi(object __instance)
    {
        using var perfScope = PgPerf.Measure(PgPerf.LoadUi);   // task 9.2a: the cost instrument (off: one volatile read)
        try
        {
            if (Members.FaithfulScreens?.GetValue(__instance) is not MyGuiScreenHost host) return;
            Interface = __instance;
            Host = host;
            frame.NewHost();   // register everything again; re-apply the latest open request (a window open before the reload opens again)
            ClearButtons();    // a new interface: no press survives it
            keyCache.Clear();
            buttons.Clear();
            logged.Clear();
            Portraits.NewHost();
            ScreenBinding.ClearMerges();
            host.Activated -= OnActivated;
            host.Activated += OnActivated;
            Frame();
        }
        catch (Exception e) { ctx?.Log($"Proving Grounds windows not registered: {e.GetType().Name}: {e.Message}"); }
    }

    /// <summary>Once per interface arrange (the overlay hook below, and before every bind of ours): ScreenFrame's rules (Core/Glue/ScreenHostLogic.cs):
    /// a new world closes our windows, a changed shape Replaces the layout, a window closed from the window side is reported, the sim halves'
    /// open / close requests are applied.</summary>
    internal static void Frame()
    {
        if (Host is not { } host) return;
        try
        {
            if (loopCounted) pressed.Reconcile(SystemMouse.AnyDown(), pass);   // forget a release Meitou never passed on (up since 2 loop passes)
            clicks.FrameCheck(LeftHeld, leftPressing);   // the left button came up without a release seen: MyGUI never got one either
            if (clicks.TimerLive(Now))   // MyGUI's double-click timer expires when the widget under the pointer changes (not during a capture)
            {
                var under = OursAt(host, Pointer);
                clicks.Hover(under?.Screen, under?.Widget, AnyHeld);
            }
            foreach (var s in screens)
            {
                try { s.BeforeFrame(); }
                catch (Exception e) { LogOnce(s.Id, "before frame", e); }
            }
            var system = ProvingGroundsSystem.Current;
            var snap = system?.Snapshot;
            states.Clear();
            foreach (var s in screens)
            {
                var model = snap?.Model(s.Id);
                states.Add(new ScreenState(s.Id, Key(s, model), model is not null, model?.RequestSerial ?? 0, model?.RequestOpen ?? false));
            }
            surface.Host = host;
            surface.Snap = snap;
            if (!ReferenceEquals(lastSystem, system))
            {
                lastSystem = system;
                ClearButtons();   // a world change: no press survives it (the fallback for a release the system cannot report)
                foreach (var s in screens)
                {
                    try { s.NewWorld(host); }
                    catch (Exception e) { LogOnce(s.Id, "new world", e); }
                }
            }
            frame.Run(surface, system, states);
            Game.ContextMenuHooks.WindowFrame();   // task 4.2: a right-click menu that opened cancels a building approach (its own try/catch)
            foreach (var s in screens)
            {
                if (host.IsRegistered(s.Id) && host.IsOpen(s.Id)) continue;
                clicks.ForgetScreen(s.Id);   // a hidden widget loses the mouse focus (MyGUI Widget::_updateVisible): no click at the release
                try { s.WhileClosed(); }
                catch (Exception e) { LogOnce(s.Id, "while closed", e); }
            }
        }
        catch (Exception e) { LogOnce("*", "frame", e); }
    }

    /// <summary>Whether <paramref name="p"/> is over one of our screens (task 4.2: the building right-click leaves such a press to the window).</summary>
    internal static bool OverOurScreen(Vector2 p) => Host is { } h && OursAt(h, p) is not null;

    /// <summary>Whether a game right-click menu is open (ContextMenuWindow.OpenMenu, the WindowsPressTaken reads); null when those reads are off
    /// (task 4.2's menu watch).</summary>
    internal static bool? MenuOpenNow() => Interface is { } ui && menuOpen is { } read ? read(ui) : null;

    /// <summary>Whether one of our screens is open (Portraits asks).</summary>
    internal static bool IsOpen(string id) => Host is { } h && h.IsRegistered(id) && h.IsOpen(id);

    // ---------- the pointer (task 4.5b): what MyGUI's InputManager gave the original's widgets beyond a press ----------

    static Func<object, Vector2>? pointer;
    static Func<object, int>? viewWidth, viewHeight;

    /// <summary>Compiles the pointer and view-size reads once (no boxing per frame); false when a member is missing.</summary>
    static bool PreparePointer()
    {
        if (pointer is not null) return true;
        if (Members.PointerField is not { } m || Members.UiWidth is not { } w || Members.UiHeight is not { } h) return false;
        var ui = Expression.Parameter(typeof(object), "ui");
        var typed = Expression.Convert(ui, m.DeclaringType!);
        pointer = Expression.Lambda<Func<object, Vector2>>(Expression.Field(typed, m), ui).Compile();
        viewWidth = Expression.Lambda<Func<object, int>>(Expression.Field(typed, w), ui).Compile();
        viewHeight = Expression.Lambda<Func<object, int>>(Expression.Field(typed, h), ui).Compile();
        return true;
    }

    static Func<object, float>? standardEm;

    /// <summary>The standard text's em pixels (PlayerInterface.StandardPixels); null when it cannot be read.</summary>
    internal static float? StandardEm
    {
        get
        {
            if (Interface is not { } ui || Members.StandardPixels is not { } prop) return null;
            try
            {
                if (standardEm is null)
                {
                    var p = Expression.Parameter(typeof(object), "ui");
                    standardEm = Expression.Lambda<Func<object, float>>(Expression.Property(Expression.Convert(p, prop.DeclaringType!), prop), p).Compile();
                }
                float em = standardEm(ui);
                return float.IsFinite(em) ? em : null;
            }
            catch (Exception) { return null; }
        }
    }

    static Func<object, object?>? painter;

    /// <summary>The faithful painter (PlayerInterface.UiPainter), to measure text as the faithful screens draw it; null without one.</summary>
    internal static Meitou.Game.Rendering.Ui.UiPainter? Painter
    {
        get
        {
            if (Interface is not { } ui || Members.UiPainterField is not { } f) return null;
            if (painter is null)
            {
                var p = Expression.Parameter(typeof(object), "ui");
                painter = Expression.Lambda<Func<object, object?>>(Expression.Convert(Expression.Field(Expression.Convert(p, f.DeclaringType!), f), typeof(object)), p).Compile();
            }
            return painter(ui) as Meitou.Game.Rendering.Ui.UiPainter;
        }
    }

    /// <summary>The pointer in screen pixels as the interface last saw it (MyGUI getMousePositionByLayer).</summary>
    internal static Vector2 Pointer => Interface is { } ui && PreparePointer() ? pointer!(ui) : new Vector2(float.NaN);

    /// <summary>The game window's size (MyGUI RenderManager::getViewSize); (0, 0) before an interface exists.</summary>
    internal static (int Width, int Height) ViewSize => ViewForCheck ?? (Interface is { } ui && PreparePointer() ? (viewWidth!(ui), viewHeight!(ui)) : (0, 0));
    /// <summary>PgCheck ui only (task 5.4): the view size a world-free check binds at; null in the game.</summary>
    internal static (int Width, int Height)? ViewForCheck;

    /// <summary>Whether the left button is down (pressed through PlayerInterface.MouseDown, not yet released or reconciled up; the original polls
    /// GetAsyncKeyState(VK_LBUTTON), ArenaUI.cpp:1205).</summary>
    internal static bool LeftHeld => pressed.IsDown((int)MouseButton.Left);

    // the buttons pressed through PlayerInterface.MouseDown and not yet released; Frame() forgets those the system says are up (Meitou does not
    // always call MouseUp: GameLoop.PointerUp skips it under a loading screen, during a cinema or settings-panel drag and while the settings
    // panel shows; a lost focus releases only the InputState)
    static readonly PointerButtons pressed = new();

    // task 4.4b: MyGUI's click for the Click buttons (a press held until its left release); leftPressing = inside a left PlayerInterface.MouseDown
    // (Meitou activates a button from it; an Activated outside one is Meitou's keyboard activation)
    static readonly UiButtonClick clicks = new();
    static bool leftPressing;

    /// <summary>The window side's one clock (real time, seconds): MyGUI's double-click timer for the buttons (UiButtonClick) and the Arena rows
    /// (ArenaRowPointer), and the Arena window's tooltip wait.</summary>
    internal static readonly System.Diagnostics.Stopwatch UiClock = System.Diagnostics.Stopwatch.StartNew();
    static double Now => UiClock.Elapsed.TotalSeconds;

    // loop passes for PointerButtons.Reconcile: bumped by a prefix on GameLoop.Frame (GameLoop.cs:1231), which runs exactly once per loop pass
    // before its DoEvents (:1235; the interface also arranges inside DoEvents' handlers, so an arrange is not a pass). Without that patch
    // (loopCounted false: Part.WindowsLoopPass missing, one log line) nothing is reconciled; a new interface or world still clears. MetalGameLoop
    // (macOS only, GameHost.Metal.cs) is not counted: the system's buttons are read on Windows only.
    static long pass;
    static bool loopCounted;

    static void BeforeLoopFrame()
    {
        using var perfScope = PgPerf.Measure(PgPerf.LoopFrame);   // task 9.2a: the cost instrument (off: one volatile read)
        pass++;
        leftPressing = false;   // a loop pass starts outside any MouseDown (a MouseDown that threw skipped its postfix)
    }

    /// <summary>Whether any pointer button is down: MyGUI captures the pointer (and keeps its mouse focus) for every held button
    /// (MyGUI 3.2.2 MyGUI_InputManager.cpp injectMousePress :238-241, injectMouseMove :99-117).</summary>
    internal static bool AnyHeld => pressed.Any;

    static void ClearButtons()
    {
        pressed.Clear();
        clicks.Forget();
    }

    /// <summary>Whether screen <paramref name="id"/>'s widget <paramref name="widget"/> is open, shown and enabled as the last arrange laid it out (a
    /// hidden widget is not laid out).</summary>
    static bool ShownAndEnabled(string id, string widget)
    {
        if (!IsOpen(id) || ControlsOf(id) is not { } controls) return false;
        for (int i = controls.Count - 1; i >= 0; i--) if (controls[i].Name == widget) return controls[i].Enabled;
        return false;
    }

    /// <summary>Our screen's widget under <paramref name="p"/> (null: none of ours).</summary>
    static (string Screen, string Widget)? OursAt(MyGuiScreenHost host, Vector2 p) =>
        host.HitTest(p) is { } h && byId.ContainsKey(h.Screen) ? (h.Screen, h.Control.Name) : null;

    /// <summary>
    /// The operating system's mouse buttons, as the original read them (GetAsyncKeyState, ArenaUI.cpp:1205). Meitou keeps no state that says a
    /// button is physically down: <c>WorldSession.Input</c> (InputState) records a press only when the interface did NOT take it (GameLoop.cs
    /// PointerDown: <c>if (host.Player.MouseDown(...)) return;</c> before <c>SetMouseButton</c>; PlayerInterface.MouseDown takes every left press,
    /// LeftDown always returns true, and every press over a window), so it never sees a press on our windows; the Silk mice it reads are a private
    /// field of the internal GameLoop with no path from the interface. Windows only: elsewhere (or if the call fails) <see cref="AnyDown"/> answers
    /// null and nothing is reconciled.
    /// </summary>
    static class SystemMouse
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern short GetAsyncKeyState(int virtualKey);

        static readonly int[] Keys = [0x01, 0x02, 0x04, 0x05, 0x06];   // VK_LBUTTON, VK_RBUTTON, VK_MBUTTON, VK_XBUTTON1, VK_XBUTTON2
        static bool unavailable = !OperatingSystem.IsWindows();

        /// <summary>Whether any mouse button is physically down now (the physical buttons: any, so a swapped or rebound button does not matter);
        /// null when the system cannot be asked.</summary>
        internal static bool? AnyDown()
        {
            if (unavailable) return null;
            try
            {
                foreach (int k in Keys) if ((GetAsyncKeyState(k) & 0x8000) != 0) return true;
                return false;
            }
            catch (Exception e)
            {
                unavailable = true;
                ctx?.Log($"window buttons: the system's mouse buttons cannot be read ({e.GetType().Name}); a release Meitou does not pass on is no longer noticed");
                return null;
            }
        }
    }

    /// <summary>TEST ONLY <c>pg window drag x1 y1 x2 y2</c> (task 4.5b, frame thread, console): the left button pressed at the first point, the pointer
    /// moved to the second in ten steps and the button released there, through the game's own calls (PlayerInterface.MouseDown / MouseMove / MouseUp,
    /// as the agent channel's click does, GameLoop.cs StepControlPointer). Null on success, else why not.</summary>
    internal static string? TestDrag(string x1, string y1, string x2, string y2)
    {
        static bool F(string s, out float v) => float.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v);
        if (!F(x1, out float ax) || !F(y1, out float ay) || !F(x2, out float bx) || !F(y2, out float by)) return "usage pg window drag <x1> <y1> <x2> <y2>";
        if (Interface is not { } ui || Members.MouseDown is not { } down || Members.MouseMove is not { } move || Members.MouseUp is not { } up)
            return "no interface yet, or a pointer member is missing";
        try
        {
            var a = new Vector2(ax, ay);
            move.Invoke(ui, [a]);
            down.Invoke(ui, [MouseButton.Left, a, false, false, false]);
            for (int i = 1; i <= 10; i++)
            {
                move.Invoke(ui, [Vector2.Lerp(a, new Vector2(bx, by), i / 10f)]);
                Frame();
                if (Host is { } h)
                    foreach (var s in screens)
                    {
                        if (!h.IsRegistered(s.Id) || !h.IsOpen(s.Id)) continue;
                        try { s.TestPointerMoved(); }
                        catch (Exception e) { LogOnce(s.Id, "pointer moved", e); }
                    }
            }
            up.Invoke(ui, [MouseButton.Left, new Vector2(bx, by), false]);
            return null;
        }
        catch (Exception e) { return $"{e.GetType().Name}: {(e.InnerException ?? e).Message}"; }
    }

    /// <summary>TEST ONLY <c>pg window escape</c> (task 4.5b, frame thread): the game's own Esc handler (PlayerInterface.EscapeKey), as the Esc key
    /// would run it. Null on success, else why not.</summary>
    internal static string? TestEscape()
    {
        if (Interface is not { } ui || Members.EscapeKey is not { } esc) return "no interface yet, or PlayerInterface.EscapeKey is missing";
        try { esc.Invoke(ui, []); return null; }
        catch (Exception e) { return $"{e.GetType().Name}: {(e.InnerException ?? e).Message}"; }
    }

    /// <summary>TEST ONLY <c>pg window caption &lt;screen&gt; &lt;widget&gt;</c> (task 5.3b): the caption the last arrange drew for the widget (a template
    /// part's when the widget itself carries none; ui-tree lists only the pickable widgets), or null with <paramref name="error"/> set.</summary>
    internal static string? TestCaption(string screen, string widget, out string? error) => TestCaption(screen, widget, out error, out _, out _);

    /// <summary>As above, with the widget's drawn rectangle (screen pixels; empty for a template part's caption alone) and enabled state (task 6.4b
    /// review: the recruitment scenario presses a disabled button, which ui-tree does not list, at its rectangle).</summary>
    internal static string? TestCaption(string screen, string widget, out string? error, out string rect, out bool enabled)
    {
        error = null;
        rect = "";
        enabled = false;
        if (ControlsOf(screen) is not { } controls) { error = $"screen {screen} is not laid out (closed?)"; return null; }
        UiControl? own = null, part = null;
        foreach (var c in controls)
        {
            if (c.Name == widget) own ??= c;
            else if (part is null && c.Caption.Length > 0
                && (c.Properties.TryGetValue("TemplateOwner", out var o) && o == widget || c.Name.StartsWith(widget + "@", StringComparison.Ordinal))) part = c;
        }
        if (own is null && part is null) { error = $"no widget {widget} on {screen} (hidden, or scrolled out of view)"; return null; }
        if (own is { } drawn)
        {
            var b = drawn.Bounds;
            rect = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{b.X:0} {b.Y:0} {b.Width:0} {b.Height:0}");
            enabled = drawn.Enabled;
        }
        return own is { Caption.Length: > 0 } ? own.Caption : part?.Caption ?? own!.Caption;
    }

    /// <summary>TEST ONLY <c>pg window wheel &lt;x&gt; &lt;y&gt; &lt;steps&gt;</c> (task 5.3b): the faithful screens' wheel call at that screen point
    /// (MyGuiScreenHost.Wheel, the call PlayerInterface's wheel makes, PlayerInterface.Ui.cs:1201; the agent channel has no wheel command).</summary>
    internal static string? TestWheel(string x, string y, string steps, out bool taken)
    {
        taken = false;
        static bool F(string s, out float v) => float.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v);
        if (!F(x, out float px) || !F(y, out float py) || !F(steps, out float n)) return "usage pg window wheel <x> <y> <steps>";
        if (Host is not { } h) return "no interface yet";
        try { taken = h.Wheel(new Vector2(px, py), n); return null; }
        catch (Exception e) { return $"{e.GetType().Name}: {e.Message}"; }
    }

    /// <summary>Screen <paramref name="id"/>'s controls as the last arrange laid them out (null: not laid out).</summary>
    internal static IReadOnlyList<UiControl>? ControlsOf(string id)
    {
        if (Host is not { } h) return null;
        foreach (var f in h.LastFrame) if (f.Id == id) return f.Controls;
        return null;
    }

    // A right press a game handler takes before the windows (PlayerInterface.MouseDown, PlayerInterface.cs:779-800): MouseDown's own result cannot
    // tell (it is true for every press over a window: OverHud, :797), so the states that make it take one are read just before it runs. Of the right
    // press branches ahead of the windows, two can fire over a window: :786 CancelInventoryDrag (InventoryWindows.cs:738-748: no modal window, and a
    // held item, a scrollbar drag or a lift press) and :792 CloseContextMenu (ContextMenuWindow.cs:764-770: a menu open). :787-789 need !OverHud;
    // :785 CharacterEditor.EditorPointerDown, :791 WorldMap.InventoryRightClick, :793 WorldMap.MapRightDown and :797 the squad bar are not read
    // (divergences.md).
    static Func<object, bool>? menuOpen, inventoryCapture;
    static bool pressTaken;

    /// <summary>Compiles the two state reads (once, at install); null on success, else what is missing.</summary>
    internal static string? PreparePressTaken()
    {
        var missing = Members.Missing(Part.WindowsPressTaken).ToList();
        if (missing.Count > 0) return string.Join(", ", missing) + " not found";
        try
        {
            var ui = Expression.Parameter(typeof(object), "ui");
            var typed = Expression.Convert(ui, Members.ContextMenuWindowProp!.DeclaringType!);
            Expression NotNull(Expression e) => Nullable.GetUnderlyingType(e.Type) is not null
                ? Expression.Property(e, "HasValue") : Expression.NotEqual(e, Expression.Constant(null, e.Type));
            var menu = Expression.Field(Expression.Property(typed, Members.ContextMenuWindowProp), Members.OpenMenu!);
            var inv = Expression.Property(typed, Members.InventoryWindowsProp!);
            var capture = Expression.OrElse(Expression.OrElse(NotNull(Expression.Field(inv, Members.HeldItem!)),
                NotNull(Expression.Field(Expression.Property(typed, Members.ScrollBarsProp!), Members.ScrollDrag!))), NotNull(Expression.Field(inv, Members.LiftPress!)));
            menuOpen = Expression.Lambda<Func<object, bool>>(NotNull(menu), ui).Compile();
            inventoryCapture = Expression.Lambda<Func<object, bool>>(capture, ui).Compile();
            return null;
        }
        catch (Exception e) { menuOpen = inventoryCapture = null; return $"{e.GetType().Name}: {e.Message}"; }
    }

    /// <summary>Whether PlayerInterface.MouseDown will take a right press before the windows, read before it runs (CancelInventoryDrag's and
    /// CloseContextMenu's conditions); false when the reads are off.</summary>
    internal static bool GameTakesRightPress(object ui)
    {
        if (menuOpen is null || inventoryCapture is null) return false;
        if (inventoryCapture(ui) && Host is { HasModal: false }) return true;
        return menuOpen(ui);
    }

    static void BeforeMouseDown(object __instance, MouseButton button)
    {
        using var perfScope = PgPerf.Measure(PgPerf.MouseDownPrefix);   // task 9.2a: the cost instrument (off: one volatile read)
        pressTaken = false;
        leftPressing = button == MouseButton.Left && ReferenceEquals(__instance, Interface);
        if (leftPressing) clicks.BeginLeftPress();   // a stale waiting press (its MouseUp skipped) never clicks on this press
        if (button != MouseButton.Right || !ReferenceEquals(__instance, Interface)) return;
        try { pressTaken = GameTakesRightPress(__instance); }
        catch (Exception e) { LogOnce("*", "press taken", e); }
    }

    // A press anywhere: the held buttons; a right press over one of our screens' enabled widgets reaches the screen (C++
    // eventMouseButtonPressed / eventMouseButtonReleased with MouseButton::Right; MyGuiScreenHost has no right press), unless a game handler took
    // the press first (an open context menu closed, an inventory drag cancelled, read by BeforeMouseDown: MyGUI raises no widget press then).
    // Window thread.
    static void AfterMouseDown(object __instance, MouseButton button, Vector2 p)
    {
        using var perfScope = PgPerf.Measure(PgPerf.MouseDownPostfix);   // task 9.2a: the cost instrument (off: one volatile read)
        bool taken = pressTaken;
        pressTaken = false;
        leftPressing = false;
        try
        {
            if (!ReferenceEquals(__instance, Interface) || Host is not { } host) return;
            pressed.Press((int)button, SystemMouse.AnyDown());
            if (button == MouseButton.Left) return;
            if (button != MouseButton.Right || taken) return;
            if (host.HitTest(p) is not { } hit || !byId.TryGetValue(hit.Screen, out var s) || !hit.Control.Enabled || hit.Control.Name.Length == 0) return;
            var click = new UiClickData(hit.Screen, hit.Control.Name, UiEventKind.RightPress, "", 0, false, p.X, p.Y);
            if (s.Click(ClickModel(hit.Screen), click) is { } command) Send(command);
        }
        catch (Exception e) { LogOnce("*", "right press", e); }
    }

    // A MouseDown that threw skipped its postfix: no press is in progress by the time a release comes.
    static void BeforeMouseUp()
    {
        using var perfScope = PgPerf.Measure(PgPerf.MouseUpPrefix);   // task 9.2a: the cost instrument (off: one volatile read)
        leftPressing = false;
    }

    // The pointer moved (with no button held, MyGUI's mouse focus follows it): a change of widget ends the double-click timer.
    static void AfterMouseMove(object __instance, Vector2 p)
    {
        using var perfScope = PgPerf.Measure(PgPerf.MouseMove);   // task 9.2a: the cost instrument (off: one volatile read)
        try
        {
            if (!clicks.TimerLive(Now) || !ReferenceEquals(__instance, Interface) || Host is not { } host) return;
            var under = OursAt(host, p);
            clicks.Hover(under?.Screen, under?.Widget, AnyHeld);
        }
        catch (Exception e) { LogOnce("*", "pointer move", e); }
    }

    // A release anywhere: every open screen of ours that wants releases hears it, with its own widget under the pointer (or "").
    static void AfterMouseUp(object __instance, MouseButton button, Vector2 p)
    {
        using var perfScope = PgPerf.Measure(PgPerf.MouseUpPostfix);   // task 9.2a: the cost instrument (off: one volatile read)
        try
        {
            if (!ReferenceEquals(__instance, Interface) || Host is not { } host) return;
            pressed.Release((int)button);
            if (button != MouseButton.Left && button != MouseButton.Right) return;
            var kind = button == MouseButton.Left ? UiEventKind.Release : UiEventKind.RightRelease;
            var hit = host.HitTest(p);
            foreach (var s in screens)
            {
                if (!s.WantsReleases || !host.IsRegistered(s.Id) || !host.IsOpen(s.Id)) continue;
                try
                {
                    string widget = hit is { } h && h.Screen == s.Id ? h.Control.Name : "";
                    var click = new UiClickData(s.Id, widget, kind, "", 0, false, p.X, p.Y);
                    if (s.Click(ClickModel(s.Id), click) is { } command) Send(command);
                }
                catch (Exception e) { LogOnce(s.Id, "release", e); }   // one screen's failure does not lose another's release or the click
            }
            // then the click (MyGUI: eventMouseButtonReleased, then eventMouseButtonClick, injectMouseRelease :295-317)
            (string Screen, string Widget)? under = hit is { } u && byId.ContainsKey(u.Screen) ? (u.Screen, u.Control.Name) : null;
            if (clicks.Release(button == MouseButton.Left, under, ShownAndEnabled, Now) is { } clicked
                && byId.TryGetValue(clicked.Screen, out var cs))
            {
                try
                {
                    bool ctrl = Members.CtrlHeld?.GetValue(__instance) is true;
                    var click = new UiClickData(clicked.Screen, clicked.Widget, UiEventKind.Click, "", 0, ctrl, p.X, p.Y);
                    if (cs.Click(ClickModel(clicked.Screen), click) is { } command) Send(command);
                }
                catch (Exception e) { LogOnce(clicked.Screen, "click", e); }
            }
            clicks.Hover(under?.Screen, under?.Widget, pressed.Any);   // MyGUI moves the focus after the release (:323)
        }
        catch (Exception e) { LogOnce("*", "release", e); }
    }

    /// <summary>The screen's own buttons and their actions, made once per layout object (template parts keep the actions their template gives).</summary>
    static (string Name, string Action)[] Buttons(string id, MyGuiLayout layout)
    {
        if (buttons.TryGetValue(id, out var c) && ReferenceEquals(c.Layout, layout)) return c.Buttons;
        var list = layout.Widgets
            .Where(w => w.Type == "Button" && w.Name.Length > 0 && !w.Properties.ContainsKey("TemplateEvent") && !w.Properties.ContainsKey("TemplatePart"))
            .Select(w => (w.Name, UiClickPolicy.ActionFor(w.Name))).ToArray();
        buttons[id] = (layout, list);
        return list;
    }

    // Every frame the interface rebuilds its base, ScreenRouter.BindFaithfulScreen(id) runs for each open screen; MyGuiScreenHost.Arrange reads the
    // screen's layout AFTER this returns (MyGuiScreenHost.cs:366-377), so a Replace made in Frame() here shows in the same frame.
    static void AfterBind(string id, UiBindings __result)
    {
        using var perfScope = PgPerf.Measure(PgPerf.BindScreen);   // task 9.2a: the cost instrument (off: one volatile read)
        try
        {
            if (!byId.TryGetValue(id, out var s) || Host is not { } host) return;
            int font = NativeUIMeasure.FontHeight(__result, FontHeight);
            if (font != FontHeight || !fontLogged)
            {
                fontLogged = true;
                ctx?.Log($"PG-WINDOW {{\"event\":\"font\",\"lineHeight\":{CMathText(NativeUIMeasure.Raw(__result))},\"em\":{CMathText(StandardEm ?? float.NaN)},"
                    + $"\"height\":{font.ToString(System.Globalization.CultureInfo.InvariantCulture)},\"view\":\"{ViewSize.Width.ToString(System.Globalization.CultureInfo.InvariantCulture)}x{ViewSize.Height.ToString(System.Globalization.CultureInfo.InvariantCulture)}\"}}");
            }
            FontHeight = font;
            Frame();
            var model = Snapshot?.Model(id);
            var layout = host.Layout(id);
            var own = Buttons(id, layout);
            if (!frame.Bindable(id, Key(s, model))) return;   // its new layout failed to build: ScreenRouter's default (buttons disabled) stays
            // ScreenRouter disabled every button of an unknown screen (ScreenRouter.cs:84-85): ours are live, each with its own action.
            foreach (var (name, action) in own)
            {
                __result.Disabled.Remove(name);
                __result.Actions[name] = action;
            }
            binding.Reset(__result, layout);
            s.Bind(model, binding);
            frame.Bound(id, model);   // task 6.4b review: clicks are judged against the model the player saw
        }
        catch (Exception e) { LogOnce(id, "bind", e); }
    }

    // C++: each window's widget delegates (eventMouseButtonClick, eventListSelectAccept, eventEditTextChange, ...); here one Activated event.
    static void OnActivated(MyGuiEvent e)
    {
        try
        {
            if (!byId.TryGetValue(e.Screen, out var s)) return;
            bool ctrl = Interface is { } ui && Members.CtrlHeld?.GetValue(ui) is true;
            if (UiClickPolicy.Map(ids, e.Screen, e.Widget, e.Action, e.Text, e.Value, ctrl) is not { } mapped) return;
            if (mapped.Kind == UiEventKind.Press)   // a button (task 4.4b): its C++ event decides when it acts
            {
                var bound = s.ButtonEvent(e.Widget);
                if (!clicks.Activated(e.Screen, e.Widget, bound, leftPressing, Now)) return;   // a Click: at the release
                if (bound == UiButtonEvent.Click) mapped = mapped with { Kind = UiEventKind.Click };   // Meitou's keyboard activation: at once
            }
            var at = Pointer;   // MouseDown set it just before the press reached the screen (PlayerInterface.cs:781)
            var click = mapped with { X = at.X, Y = at.Y };
            if (s.Click(ClickModel(e.Screen), click) is { } command) Send(command);
        }
        catch (Exception ex) { LogOnce(e.Screen, "click", ex); }
    }

    /// <summary>
    /// The per-frame hook: an overlay contribution that draws nothing and runs <see cref="Frame"/> (the overlay factory runs whenever the interface
    /// arranges its base with the HUD shown, just before the faithful screens, PlayerInterface.Ui.cs:513, :521; CAP 7.1). With the HUD hidden
    /// (title, character editor, a conversation) requests wait; an open window still applies them at its bind.
    /// </summary>
    sealed class FrameHook : IUiExtension
    {
        static readonly UiNode Nothing = new("proving-grounds.windows.frame") { Visible = false, Pickable = false };
        public string Id => "proving-grounds.windows";
        static bool ran;
        public void Register(UiRegistration registration) => registration.Contribute(UiSlots.Overlay, _ =>
        {
            if (!ran) { ran = true; ctx?.Log("PG-WINDOW {\"event\":\"frame hook running\"}"); }
            Frame();
            return Nothing;
        });
    }
}
