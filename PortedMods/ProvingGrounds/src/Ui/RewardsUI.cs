// Port of upstream/ProvingGrounds/plugin/src/RewardsUI.h/.cpp (Proving Grounds by Shutori, GPLv3). Task 5.3a: the rules half
// (Core/Glue/RewardsUILogic.cs) and the sim half (src/Game/RewardsUISim.cs) behind the frame-loop object RewardsUI below. Task 5.3b (PA §2.17;
// CAP 7.0-7.5; spikes S6, S7): the window half RewardsScreen: the layout built call by call from EnsureWindow (:1051-1227) and RebuildRows
// (:1228-1305) with LayoutBuilder / NativeUI (same widget types, skins, names, captions, alignments, colours and alphas), every rectangle from
// FitWindowToScreen / LayoutWindow / LayoutFighters / LayoutProfile / LayoutDetails / LayoutRewardCard / LayoutGeneralCard / LayoutRewardAccent /
// LayoutIcon (Core/Glue/RewardsUILayout.cs, tested), the portraits, the icons, the scroll canvases, the wheel and the clicks. Window thread only:
// the window reads the published RewardsUiModel and PgSnapshot.LastPurchaseOf(Window) and sends PgCommands. Meitou 0.17.3 (tag v0.17.3, fa34a1b).
//
// Engine calls of the C++ window and what replaces them:
//   createWidgetReal / createWidget (EnsureWindow, RebuildRows)        -> LayoutBuilder XML (Build) -> ScreenHost (Register / Replace). A new
//                                                                         model (each C++ RefreshDetails: Show, a click, a purchase) or a new status
//                                                                         line lays the window out again (the shape key below)
//   setCoord / setSize / setVisible (LayoutWindow ... LayoutIcon, the   -> RewardsUILayout's rectangles and Hidden set in the XML
//     resize in Tick :1619-1634)
//   g_window->setCoord (FitWindowToScreen)                              -> the root's bound screen rectangle; a new view size is a new layout
//   setEnabled / setStateSelected (UpdateDetails, UpdateCatalogVisibility) -> UiBindings Disabled / NativeUI.SelectedState each frame
//   g_status->setCaption(status) (the purchase handlers :500-501, :519-520, ->  the purchase's result PgSnapshot.LastPurchaseOf(Window) read through
//     :537, :551; Show :1605-1614)                                         Core RewardsPurchaseGate (FollowWorld first): the status line
//   RewardDelivery::ClaimNext / PurchaseLicence / PurchaseWeaponLicence  -> the RewardPurchase command (task 5.2b) with the gate's sequence and,
//     / PurchaseGeneral (OnClaimClicked :490-506 ... :541-565)            for a claim, the offer the card shows; none sent while the last waits
//   OnFighterClicked / OnCatalogTabClicked / OnCloseClicked              -> RewardsSelectFighter / RewardsSelectTab / RewardsClose
//   eventWindowButtonPressed "close" (:568-572), Escape (ArenaUI.cpp:3014-3016) -> the framework's Closed report (the sim half's OnClosedFromWindow)
//   eventMouseButtonClick (every button)                                 -> UiClick Click (left release, task 4.4b; RewardsUINames.ButtonEvent)
//   ScrollView::setCanvasSize / setViewOffset (g_tabOffsets :77, Show,  -> UiBindings.Canvas; MyGuiScreenHost.SetScroll / GetScroll with Core
//     OnFighterClicked, OnCatalogTabClicked, the layouts' clamps)          RewardsScrollModel (tested)
//   eventMouseWheel (OnWheel / BindWheel :181-206)                       -> a prefix on MyGuiScreenHost.Wheel (Part.WindowsWheel): over a widget the
//                                                                         C++ binds, three body heights a notch on its canvas; elsewhere the host's own
//                                                                         ScrollView wheel (MyGUI ScrollView's own, 50 px)
//   PortraitManager::setImageWidget (BindPortrait :347-359)             -> Portraits.Bind (spike S7); no free cell = the portrait hidden (as no manager)
//   RewardDelivery::CreateRewardItem + InventoryIcon::createIconImage    -> no item made: Items.PreviewLook (the record and material that item would
//     + ou->destroy (BindRewardIcon :360-404, BindGeneralIcon :405-439)    have, from the read-only game data), then the inventory cells' own icon
//                                                                         source (PlayerInterface.ItemCells.ItemIcons.Choose, Part.RewardsIcons): a
//                                                                         PNG (UiPainter.IconReady / IconTexture, its size from the PNG header) or a
//                                                                         picture the renderer draws (RenderedIconReady / RenderedIconTexture, its size
//                                                                         ItemIconStyle.TextureSize); the icon shows once its texture is on the GPU
//   getTextSize / getFontHeight                                          -> RewardsTextMeasure over src/Ui/TextMeasure.cs / ScreenHost.FontHeight
using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Data.Ui;
using Meitou.Game.Rendering.Ui;
using Meitou.Game.Ui;
using Meitou.Simulation;
using ProvingGrounds.Core;
using ProvingGrounds.Core.Glue;
using ProvingGrounds.Engine;
using ProvingGrounds.Game;
using N = ProvingGrounds.Core.Glue.RewardsUINames;

namespace ProvingGrounds.Ui;

/// <summary>The Rewards window's frame-loop object (RewardsUI.h: Show / Close / Tick / IsVisible / AbandonWorldState) over its sim half. The window
/// itself is <see cref="RewardsScreen"/>.</summary>
sealed class RewardsUI(RewardsUISim sim)
{
    internal RewardsUISim Sim => sim;

    // C++: RewardsUI.cpp:1620, called from ArenaUI.cpp:3170 (ForgottenGUI_update_hook): only the window's resize (window side)
    internal void Tick(World world) => sim.Tick(world);

    // C++ RewardsUI.h:8-12
    internal void Show(World world, CharacterId preferred) => sim.Show(world, preferred);
    internal void Close(World world) => sim.Close();
    internal bool IsVisible => sim.IsVisible;

    // C++: RewardsUI.cpp:1635, called by ArenaUI::AbandonWorldState (ArenaUI.cpp:3201, task 4.5a)
    internal void AbandonWorldState(World world) => sim.AbandonWorldState(world);
}

/// <summary><see cref="IRewardsTextMeasure"/> over a screen's <see cref="TextMeasure"/>: the standard text, a Kenshi_Button1 caption, the painted
/// text of the fighter rows.</summary>
sealed class RewardsTextMeasure(TextMeasure text) : IRewardsTextMeasure
{
    public (int Width, int Height) Standard(string s, int wrapWidth) => text.Size(TextMeasure.StandardFont, s, wrapWidth);
    public int ButtonWidth(string caption) => text.Width(TextMeasure.PaintedFont, caption);
    public (int Width, int Height) Painted(string s, int wrapWidth) => text.Size(TextMeasure.PaintedFont, s, wrapWidth);
}

/// <summary>
/// The Rewards window <c>pg.rewards</c> (task 5.3b), declared to the window framework (src/Ui/ScreenHost.cs). EnsureWindow creates every card,
/// General card and licence once; RebuildRows (each Show) the fighter rows: the shape key is the rows' squad indexes (the framework adds the font
/// height), so only a new row set rebuilds and Replaces the layout. Everything the C++ RefreshDetails / LayoutWindow changes within one shape (captions,
/// rectangles, visibility, accent colours, the status line, the view size) is bound per frame from a plan made once per model, view, font, status line
/// and icon source (task 5.3b review I1, as the Arena window binds its positions): rectangles that differ from the XML's as fractions of their parent,
/// visibility as Hidden / Visible over the XML's. The bind also gives the canvases, enabled and selected states, portraits, the icons whose textures are
/// ready and keeps the scroll offsets as the C++ does.
/// </summary>
static class RewardsScreen
{
    static readonly TextMeasure text = new();
    static readonly RewardsTextMeasure measure = new(text);
    static bool wasOpen;

    // the purchase gate on the window thread (sender Window) and the status line it shows (BeforeFrame); the live model it was read for
    static readonly RewardsPurchaseGate gate = new();
    static string shownStatus = RewardsUiTexts.StatusInitial;
    static PgUiModel? liveModel;

    // C++ g_tabOffsets (:77) and the canvases' offsets: process-wide as the C++ statics (AbandonWindow resets them; AbandonWorldState does not)
    static readonly RewardsScrollModel scroll = new();
    static int lastShows = -1, lastDetailResets = -1;
    static RewardsCatalogTab lastTab;
    static RewardsWindowLayout? clampedFor;
    static bool fighterBar, profileBar, detailBar;   // the scroll views' bars after the last layout (MyGUI keeps them until the next setCanvasSize)

    static RewardsUiView<CharacterId>? Of(PgUiModel? m) => m is RewardsUiModel r ? r.View : null;

    /// <summary>The creation-state view (EnsureWindow's captions) for a layout before any model.</summary>
    static readonly RewardsUiView<CharacterId> Created = new RewardsUILogic<CharacterId>().BuildView();

    internal static readonly PgScreen Declaration = new()
    {
        Id = RewardsUISim.ScreenId,
        ButtonEvent = N.ButtonEvent,   // task 4.4b: RewardsUI.cpp's bindings (every button eventMouseButtonClick)
        ShapeKey = ShapeKey,
        BeforeFrame = BeforeFrame,
        Layout = Build,
        Bind = Bind,
        Click = Click,
        WhileClosed = () => { if (wasOpen) ResetWindowState(); },
        NewWorld = _ => ResetWindowState(),
        Registered = () => { built = pending; (fighterBar, profileBar, detailBar) = pendingBars; },   // task 5.4: only a layout the host took
        Portraits = m => Of(m) is { } v ? [.. v.Rows.Where(r => r.PortraitVisible).Select(r => r.Character)] : [],
        CheckModels = CheckModels,
    };

    /// <summary>Window-side state that must not outlive one opening of the window: the bind plan and the events the scroll offsets follow (called on
    /// close, a new world and the first bind after opening). The scroll model and the purchase gate are not: the C++ keeps its offsets for the
    /// process, the gate follows the world itself (FollowWorld).</summary>
    static void ResetWindowState()
    {
        wasOpen = false;
        plan = null;
        lastShows = -1;
        lastDetailResets = -1;
        clampedFor = null;
        current = default;
    }

    // ---------- shape ----------

    /// <summary>The widget set: RebuildRows' rows (their squad indexes name the widgets); every other widget exists in every shape.</summary>
    static string ShapeKey(PgUiModel? m) => Of(m) is { } v
        ? "rewards/rows " + string.Join(",", v.Rows.Select(r => r.SquadIndex.ToString(CultureInfo.InvariantCulture)))
        : "rewards/none";

    /// <summary>The status line of a model: the live model's is the gate's (a purchase's result over Show's line), any other (PgCheck) its own.</summary>
    static string StatusOf(PgUiModel? m) => ReferenceEquals(m, liveModel) ? shownStatus : Of(m)?.Status ?? RewardsUiTexts.StatusInitial;

    // the status line from the purchase result (one snapshot read per frame: the key, the layout and the bind use liveModel)
    static void BeforeFrame()
    {
        var system = ProvingGroundsSystem.Current;
        gate.FollowWorld(system);   // task 5.3a: a new world starts the window's numbers over
        var snap = system?.Snapshot;
        liveModel = snap?.Model(RewardsUISim.ScreenId);
        if (Of(liveModel) is { } v)
        {
            var last = snap!.LastPurchaseOf(RewardPurchaseSender.Window);
            gate.Observe(last?.Seq ?? 0, last is not null, last?.Status ?? "", last?.OfferChanged == true, v.Shows);
            string status = gate.Status(v.Status);
            shownStatus = status;   // C++: setCaption(status) then RefreshDetails -> LayoutWindow: a new plan (the status line's height)
        }
        RewardIcons.Refresh(ScreenHost.IsOpen(RewardsUISim.ScreenId));   // a new icon source: a new plan (RewardIcons.Version)
    }

    static (int W, int H) View()
    {
        var (w, h) = ScreenHost.ViewSize;
        return w <= 1 || h <= 1 ? (1920, 1080) : (w, h);
    }

    static RewardsWindowLayout Compute(RewardsUiView<CharacterId> v, int font, string status)
    {
        text.UseCurrent();
        var (w, h) = View();
        var input = new RewardsLayoutInput
        {
            ViewWidth = w, ViewHeight = h, FontHeight = font, Status = status,
            PieceIcons = [.. v.Cards.Select(c => RewardIcons.Size(c.Icon))],
            GeneralIcons = [.. v.General.Select(g => RewardIcons.Size(g.Icon))],
            FighterBarBefore = fighterBar, ProfileBarBefore = profileBar, DetailBarBefore = detailBar,
        };
        return RewardsUILayout<CharacterId>.Compute(v, input, measure);
    }

    static string F(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    static string Colour((float R, float G, float B) c) => F(c.R) + " " + F(c.G) + " " + F(c.B);

    // C++: RewardsUI.cpp:1051-1227 EnsureWindow and :1228-1305 RebuildRows, in creation order; positions and visibility from RewardsUILayout, the
    // captions of this model (UpdateDetails wrote them before the layout pass)
    static LayoutBuilder Build(PgUiModel? m, int font)
    {
        var v = Of(m) ?? Created;
        string status = StatusOf(m);
        var l = Compute(v, font, status);
        pendingBars = (l.Fighter.Bar, l.Profile.Bar, l.Detail.Bar);   // the bars MyGUI keeps once the host took this layout (Registered)
        var parents = new Dictionary<string, string>(StringComparer.Ordinal);
        string parent = "";   // the widget the next ones are made in ("" = the window's client)
        pending = (l, parents, [.. v.Cards.Select(c => c.Rarity)]);   // `built` once the host took it (Registered)
        current = default;
        var b = new LayoutBuilder();
        LayoutRect R(string name) => l.Rects.TryGetValue(name, out var r) ? r : new LayoutRect(0, 0, 100, 24);
        LayoutBuilder Open(string type, string skin, string name)
        {
            var r = R(name);
            parents[name] = parent;
            b.Open(type, skin, r.X, r.Y, r.W, r.H, name);
            if (l.Hidden.Contains(name)) b.Prop("Visible", "false");
            return b;
        }
        LayoutBuilder Label(string name, string caption) => Open("TextBox", "Kenshi_TextboxStandardText", name).Prop("Caption", caption);   // NativeUI::Label
        LayoutBuilder Wrapped(string name, string caption) => Open("EditBox", "Kenshi_WordWrapEmpty", name).Prop("Caption", caption);     // NativeUI::WrappedLabel
        LayoutBuilder Button(string name, string caption) => Open("Button", "Kenshi_Button1", name).Prop("Caption", caption);             // NativeUI::Button
        void Scroll(string name)   // NativeUI::Scroll
        {
            var r = R(name);
            parents[name] = parent;
            NativeUI.Scroll(b, r.X, r.Y, r.W, r.H, name);
            if (l.Hidden.Contains(name)) b.Prop("Visible", "false");
        }

        b.OpenReal("Window", "Kenshi_WindowCX", .05f, .05f, .90f, .90f, N.Window, "Center", "Window").Prop("Caption", v.Caption);   // :1059-1061
        Scroll(N.ProfileScroll);   // :1068
        parent = N.ProfileScroll;
        Label(N.SelectedHeading, "").Close();   // :1073 (hidden :1074)
        Wrapped(N.Selected, "").Close();   // :1081 (hidden :1082)
        Wrapped(N.Career, v.Career).Close();   // :1083
        Button(N.GeneralTab, RewardsUiTexts.TabGeneral).Close();   // :1088-1090
        Button(N.ArmourTab, RewardsUiTexts.TabArmour).Close();
        Button(N.WeaponsTab, RewardsUiTexts.TabWeapons).Close();
        b.Close();   // profile scroll
        parent = "";
        Label(N.Title, v.Title).Close();   // :1070 (hidden :1071)
        Label(N.FightersHeading, v.FightersHeading).Close();   // :1072
        Open("EditBox", "Kenshi_WordWrap", N.Subtitle).Prop("Caption", v.Subtitle).Close();   // :1076-1077
        Open("EditBox", "Kenshi_WordWrap", N.Status).Prop("Caption", status).Close();   // :1078-1079 (Show / the purchases overwrite it)
        Scroll(N.FighterScroll);   // :1085
        parent = N.FighterScroll;
        foreach (var row in v.Rows)   // RebuildRows :1234-1294
        {
            int s = row.SquadIndex;
            Button(N.Row(s), "");
            parent = N.Row(s);
            // the row's parts: the C++ binds their clicks to OnFighterClicked as the row's (:1245-1273); here they let the press through to the row
            // button (NeedMouse false), which MyGuiScreenHost activates (a press on a picked label activates nothing)
            Open("ImageBox", "ImageBox", N.RowPortrait(s)).Prop("NeedMouse", "false").Close();
            Open("EditBox", "Kenshi_PaintedWordWrapEmpty", N.RowName(s)).Prop("Caption", row.Name).Prop("NeedMouse", "false").Close();
            Open("TextBox", "Kenshi_TextboxPaintedText", N.RowMarks(s)).Prop("Caption", row.Marks).Prop("NeedMouse", "false").Close();
            Open("Widget", "WhiteSkin", N.RowRail(s)).Prop("Colour", Colour(RewardsUiColours.Rail)).Prop("NeedMouse", "false").Close();   // :1275-1284
            b.Close();   // row
            parent = N.FighterScroll;
        }
        b.Close();   // fighter scroll
        parent = "";
        Scroll(N.DetailScroll);   // :1086
        parent = N.DetailScroll;
        for (int i = 0; i < v.Cards.Count; ++i)   // :1100-1129
        {
            var card = v.Cards[i];
            string colour = Colour(RewardsUiColours.Grade(card.Rarity));   // LayoutRewardAccent :289-296
            Open("Widget", "PanelEmpty", N.Accent(i)).Prop("NeedMouse", "false");   // MakeRewardAccent :237-276
            parent = N.Accent(i);
            for (int k = 0; k < 16; ++k)
                Open("Widget", "WhiteSkin", N.Fade(i, k)).Prop("NeedMouse", "false").Prop("Alpha", F(RewardsUiColours.FadeAlpha(k))).Prop("Colour", colour).Close();
            for (int k = 0; k < 4; ++k)
                Open("Widget", "WhiteSkin", N.Border(i, k)).Prop("NeedMouse", "false").Prop("Alpha", F(RewardsUiColours.BorderAlpha(k))).Prop("Colour", colour).Close();
            b.Close();   // accent
            parent = N.DetailScroll;
            Open("ImageBox", "ImageBox", N.Icon(i)).Close();
            Wrapped(N.Name(i), card.Name).Close();
            Wrapped(N.Progress(i), card.Progress).Close();
            Button(N.Claim(i), card.ClaimCaption).Close();
        }
        for (int i = 0; i < v.General.Count; ++i)   // :1131-1161
        {
            var g = v.General[i];
            Open("ImageBox", "ImageBox", N.GeneralIcon(i)).Close();
            Wrapped(N.GeneralName(i), g.Name).Close();
            Wrapped(N.GeneralDetails(i), g.Details).Close();
            Button(N.GeneralBuy(i), g.BuyCaption).Close();
        }
        Wrapped(N.BlueprintsHeading, RewardsUiTexts.BlueprintsHeading).Close();   // :1163-1168
        Wrapped(N.SuppliesHeading, RewardsUiTexts.SuppliesHeading).Close();
        for (int i = 0; i < v.Licences.Count; ++i)   // :1172-1194
        {
            var lic = v.Licences[i];
            Wrapped(N.LicenceHeading(i), lic.Heading).Close();
            Wrapped(N.LicenceDetails(i), lic.Details).Close();
            Button(N.LicenceBuy(i), lic.BuyCaption).Close();
        }
        Wrapped(N.WeaponHeading, v.WeaponLicence.Heading).Close();   // :1196-1210
        Wrapped(N.WeaponDetails, v.WeaponLicence.Details).Close();
        Button(N.WeaponBuy, v.WeaponLicence.BuyCaption).Close();
        b.Close();   // detail scroll
        parent = "";
        Button(N.Close, v.CloseCaption).Close();   // :1213-1214
        b.Close();   // window
        return b;
    }

    // ---------- bind ----------

    /// <summary>What one bind writes for a layout: made when the model, view, font, status line or icon source changes, applied every frame (no
    /// allocation in the steady state).</summary>
    sealed record Plan(UiRect Root, (string Name, (float W, float H) Canvas)[] Canvas, string[] Disabled, (string Widget, bool Selected)[] Selected,
        (string Image, CharacterId Who)[] Faces, (string Icon, RewardsIconView Look)[] Icons, (string Name, string Text)[] Texts, string[] Hidden,
        string[] Shown, (string Name, UiRect Fraction)[] Sizes, (string Name, IReadOnlyDictionary<string, string> Props)[] Colours);

    static Plan? plan;
    static RewardsWindowLayout? planFor;
    /// <summary>The layout the registered XML was built from, each widget's parent in it, each card's accent grade in it.</summary>
    static (RewardsWindowLayout? Layout, Dictionary<string, string>? Parents, int[]? Rarity) built;
    /// <summary>What the last Build made, taken as <see cref="built"/> (and its scroll bars) only after the host registered or Replaced it.</summary>
    static (RewardsWindowLayout? Layout, Dictionary<string, string>? Parents, int[]? Rarity) pending;
    static (bool Fighter, bool Profile, bool Detail) pendingBars;
    /// <summary>The layout of the model shown now, and what it was computed for.</summary>
    static (PgUiModel? Model, (int W, int H) View, int Font, string? Status, int Icons, RewardsWindowLayout? Layout) current;

    static readonly IReadOnlyDictionary<string, string> Visible = new Dictionary<string, string>(StringComparer.Ordinal) { ["Visible"] = "true" };
    static readonly IReadOnlyDictionary<string, string>[] GradeColour = [.. Enumerable.Range(0, 4).Select(g =>
        (IReadOnlyDictionary<string, string>)new Dictionary<string, string>(StringComparer.Ordinal) { ["Colour"] = Colour(RewardsUiColours.Grade(g)) })];

    /// <summary>C++ RefreshDetails (:1553-1562): UpdateDetails, FitWindowToScreen, LayoutWindow for the model shown now; the built layout when it is the
    /// same computation.</summary>
    static RewardsWindowLayout LayoutFor(PgUiModel? m, RewardsUiView<CharacterId> v)
    {
        string status = StatusOf(m);
        var view = View();
        int font = ScreenHost.FontHeight, icons = RewardIcons.Version;
        if (ReferenceEquals(current.Model, m) && current.View == view && current.Font == font && current.Status == status && current.Icons == icons
            && current.Layout is { } l)
            return l;
        l = Compute(v, font, status);
        current = (m, view, font, status, icons, l);
        return l;
    }

    /// <summary>The size of a widget's parent as Meitou arranges it: the window's client (""), a scroll view's canvas, else the parent's rectangle.</summary>
    static (int W, int H) ParentSize(RewardsWindowLayout l, string parent)
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

    static Plan MakePlan(RewardsUiView<CharacterId> v, RewardsWindowLayout l)
    {
        var disabled = new List<string>();
        for (int i = 0; i < v.Cards.Count; ++i) if (!v.Cards[i].ClaimEnabled) disabled.Add(N.Claim(i));
        for (int i = 0; i < v.General.Count; ++i) if (!v.General[i].BuyEnabled) disabled.Add(N.GeneralBuy(i));
        for (int i = 0; i < v.Licences.Count; ++i) if (!v.Licences[i].BuyEnabled) disabled.Add(N.LicenceBuy(i));
        if (!v.WeaponLicence.BuyEnabled) disabled.Add(N.WeaponBuy);
        var selected = new List<(string, bool)>();
        foreach (var r in v.Rows) selected.Add((N.Row(r.SquadIndex), r.Selected));   // :1547 setStateSelected(row == g_selected)
        for (int t = 0; t < 3; ++t) selected.Add((N.Tab((RewardsCatalogTab)t), v.Tab == (RewardsCatalogTab)t));   // :1315-1316
        var faces = v.Rows.Where(r => r.PortraitVisible).Select(r => (N.RowPortrait(r.SquadIndex), r.Character)).ToArray();
        var icons = new List<(string, RewardsIconView)>();
        for (int i = 0; i < v.Cards.Count; ++i) if (l.IconsShown.Contains(N.Icon(i))) icons.Add((N.Icon(i), v.Cards[i].Icon));
        for (int i = 0; i < v.General.Count; ++i) if (l.IconsShown.Contains(N.GeneralIcon(i))) icons.Add((N.GeneralIcon(i), v.General[i].Icon));
        // the captions UpdateDetails writes (setCaption, no new widget)
        var texts = new List<(string, string)>
        {
            (N.Career, v.Career), (N.Title, v.Title), (N.Subtitle, v.Subtitle), (N.FightersHeading, v.FightersHeading), (N.Close, v.CloseCaption),
            (N.WeaponHeading, v.WeaponLicence.Heading), (N.WeaponDetails, v.WeaponLicence.Details), (N.WeaponBuy, v.WeaponLicence.BuyCaption),
        };
        foreach (var r in v.Rows) { texts.Add((N.RowName(r.SquadIndex), r.Name)); texts.Add((N.RowMarks(r.SquadIndex), r.Marks)); }
        for (int i = 0; i < v.Cards.Count; ++i)
        {
            texts.Add((N.Name(i), v.Cards[i].Name)); texts.Add((N.Progress(i), v.Cards[i].Progress)); texts.Add((N.Claim(i), v.Cards[i].ClaimCaption));
        }
        for (int i = 0; i < v.General.Count; ++i)
        {
            texts.Add((N.GeneralName(i), v.General[i].Name)); texts.Add((N.GeneralDetails(i), v.General[i].Details)); texts.Add((N.GeneralBuy(i), v.General[i].BuyCaption));
        }
        for (int i = 0; i < v.Licences.Count; ++i)
        {
            texts.Add((N.LicenceHeading(i), v.Licences[i].Heading)); texts.Add((N.LicenceDetails(i), v.Licences[i].Details));
            texts.Add((N.LicenceBuy(i), v.Licences[i].BuyCaption));
        }
        // visibility, rectangles and accent colours over the XML's (setVisible / setCoord / setColour of LayoutWindow ... LayoutRewardAccent)
        var hidden = new List<string>(l.Hidden);
        var shown = new List<string>();
        var sizes = new List<(string, UiRect)>();
        var colours = new List<(string, IReadOnlyDictionary<string, string>)>();
        if (built is { Layout: { } bl, Parents: { } parents, Rarity: { } rarity })
        {
            foreach (var name in bl.Hidden) if (!l.Hidden.Contains(name) && parents.ContainsKey(name) && !l.IconsShown.Contains(name)) shown.Add(name);
            foreach (var (name, rect) in l.Rects)
            {
                if (bl.Rects.TryGetValue(name, out var was) && was == rect) continue;
                if (!parents.TryGetValue(name, out var parent)) continue;   // not in this shape
                sizes.Add((name, Fraction(rect, ParentSize(l, parent))));
            }
            for (int i = 0; i < v.Cards.Count && i < rarity.Length; ++i)
            {
                if (v.Cards[i].Rarity == rarity[i]) continue;
                var c = GradeColour[RewardsLayout.Min(3, RewardsLayout.Max(0, v.Cards[i].Rarity))];
                for (int k = 0; k < 16; ++k) colours.Add((N.Fade(i, k), c));
                for (int k = 0; k < 4; ++k) colours.Add((N.Border(i, k), c));
            }
        }
        return new Plan(new UiRect(l.Window.X, l.Window.Y, l.Window.W, l.Window.H),
            [.. l.Canvas.Select(c => (c.Key, ((float)c.Value.W, (float)c.Value.H)))], [.. disabled], [.. selected], faces, [.. icons], [.. texts],
            [.. hidden], [.. shown], [.. sizes], [.. colours]);
    }

    /// <summary>The canvases' offsets as the C++ moves them: Show (:1595-1598), a new selection (:479-482), a tab change (:516-520), each layout's clamp
    /// (:626-628, :913-918, :956-957); the offsets the player scrolled to (the bar, the host's wheel) are read back first.</summary>
    static void FollowScroll(RewardsUiView<CharacterId> v, RewardsWindowLayout l)
    {
        if (ScreenHost.Host is not { } host || !host.IsRegistered(RewardsUISim.ScreenId)) return;
        string id = RewardsUISim.ScreenId;
        bool changed = false;
        if (v.Shows != lastShows)
        {
            scroll.OnShow(v.Tab);
            lastShows = v.Shows;
            lastDetailResets = v.DetailResets;
            lastTab = v.Tab;
            changed = true;
        }
        else
        {
            scroll.Observe(RewardsScrollModel.Canvas.Fighter, (int)host.GetScroll(id, N.FighterScroll));
            scroll.Observe(RewardsScrollModel.Canvas.Profile, (int)host.GetScroll(id, N.ProfileScroll));
            scroll.Observe(RewardsScrollModel.Canvas.Detail, (int)host.GetScroll(id, N.DetailScroll));
            if (v.DetailResets != lastDetailResets)
            {
                lastDetailResets = v.DetailResets;
                scroll.OnSelectionChanged();
                changed = true;
            }
            if (v.Tab != lastTab)
            {
                scroll.OnTabChanged(lastTab, v.Tab);
                lastTab = v.Tab;
                changed = true;
            }
        }
        if (!ReferenceEquals(clampedFor, l))
        {
            clampedFor = l;
            scroll.AfterFighterLayout(l.Fighter.Content, l.Fighter.View);
            scroll.AfterProfileLayout(l.Profile.Content, l.Profile.View);
            scroll.AfterDetailLayout(v.Tab, l.Detail.Content, l.Detail.View);
            changed = true;
        }
        if (!changed) return;
        host.SetScroll(id, N.FighterScroll, scroll.Fighter);
        host.SetScroll(id, N.ProfileScroll, scroll.Profile);
        host.SetScroll(id, N.DetailScroll, scroll.Detail);
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
        b.Text(N.Status, StatusOf(m));
        foreach (var n in plan.Hidden) b.Hide(n);
        foreach (var n in plan.Shown) b.Props(n, Visible);
        foreach (var (n, f) in plan.Sizes) b.Size(n, f);
        foreach (var (n, c) in plan.Colours) b.Props(n, c);
        foreach (var name in plan.Disabled) b.Disable(name);
        foreach (var (name, on) in plan.Selected) b.Props(name, NativeUI.SelectedState(on));
        foreach (var (image, who) in plan.Faces)
            if (!b.Portrait(image, who)) b.Hide(image);   // BindPortrait :355-358: no portrait manager (here: no free cell) = hidden
        foreach (var (icon, look) in plan.Icons)
        {
            if (RewardIcons.Ready(look) is { } props) b.Props(icon, props);   // its texture (and Visible: the XML may have it hidden)
            else b.Hide(icon);   // the texture is not on the GPU yet: hidden as an icon not loaded
        }
        foreach (var (n, c) in plan.Canvas) b.Bindings.Canvas[n] = c;
        b.Place(N.Window, plan.Root);
    }

    /// <summary>PgCheck ui (task 5.3b review I1): the average time of the window-side work a new model costs when the shape stays (the layout
    /// arithmetic and the bind plan), over <paramref name="runs"/> runs after a warm-up, in ms.</summary>
    internal static double TimePlanForCheck(PgUiModel? m, int runs)
    {
        var v = Of(m) ?? Created;
        MakePlan(v, Compute(v, NativeUI.DefaultFontHeight, StatusOf(m)));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < runs; ++i) MakePlan(v, Compute(v with { Shows = v.Shows + i + 1 }, NativeUI.DefaultFontHeight, StatusOf(m)));
        return sw.Elapsed.TotalMilliseconds / runs;
    }

    /// <summary>PgCheck ui only (task 5.4): every window-side state back to a fresh process's (no layout built or registered yet).</summary>
    internal static void ResetForCheck()
    {
        ResetWindowState();
        built = pending = default;
        pendingBars = default;
        fighterBar = profileBar = detailBar = false;
    }

    /// <summary>TEST ONLY (pg window wheel): the body height of the layout shown now (BodyHeight :169), 0 when none.</summary>
    internal static int BodyForTest => current.Layout?.Body ?? 0;

    // ---------- clicks ----------

    static PgCommand? Click(PgUiModel? m, UiClickData c)
    {
        if (c.Kind != UiEventKind.Click || Of(m) is not { } v) return null;
        string w = c.Widget;
        if (w == N.Close) return new RewardsClose { Tick = 0 };   // OnCloseClicked :566
        for (int t = 0; t < 3; ++t)   // OnCatalogTabClicked :507-523
            if (w == N.Tab((RewardsCatalogTab)t)) return new RewardsSelectTab((RewardsCatalogTab)t) { Tick = 0 };
        if (N.TryRow(w, out int squadIndex))   // OnFighterClicked :465-489: the row's fighter, never its index
        {
            foreach (var r in v.Rows)
                if (r.SquadIndex == squadIndex) return new RewardsSelectFighter(r.Character) { Tick = 0 };
            return null;
        }
        if (N.TryClaim(w, out int piece) && piece < v.Cards.Count)   // OnClaimClicked :490-506 (ClaimNext with the offer the card shows)
        {
            var card = v.Cards[piece];
            return Purchase(v, RewardPurchaseKind.Claim, piece, card.HasOffer ? card.Offer : null);
        }
        if (N.TryGeneralBuy(w, out int item) && item < v.General.Count)   // OnGeneralClicked :524-540
            return Purchase(v, RewardPurchaseKind.General, item, null);
        if (N.TryLicenceBuy(w, out int row) && row < v.Licences.Count)   // OnLicenceClicked :541-565
            return Purchase(v, RewardPurchaseKind.Licence, (int)v.Licences[row].Set, null);
        if (w == N.WeaponBuy)
            return Purchase(v, RewardPurchaseKind.WeaponLicence, 0, null);
        return null;
    }

    /// <summary>A purchase button: the C++ guard (a valid selection, :492-493), then the RewardPurchase with the gate's next number; none while the
    /// last one sent waits for its result (task 5.2b review).</summary>
    static PgCommand? Purchase(RewardsUiView<CharacterId> v, RewardPurchaseKind kind, int item, ArenaRewards.Offer? shown)
    {
        if (!v.HasSelected) return null;
        var system = ProvingGroundsSystem.Current;
        gate.FollowWorld(system);
        long published = system?.Snapshot?.LastPurchaseOf(RewardPurchaseSender.Window)?.Seq ?? 0;
        long seq = gate.Send(published, v.Shows);
        if (seq == 0) return null;
        return new RewardPurchase(v.Selected, kind, item, seq, shown, RewardPurchaseSender.Window) { Tick = 0 };
    }

    // ---------- the wheel (OnWheel / BindWheel :181-206) ----------

    /// <summary>Prefix on MyGuiScreenHost.Wheel (Part.WindowsWheel): the wheel over a Rewards widget the C++ binds moves its canvas three body
    /// heights a notch, clamped (OnWheel :181-195, RewardsScrollModel.Wheel); anything else runs the host's own wheel.</summary>
    internal static bool BeforeWheel(MyGuiScreenHost __instance, Vector2 p, float steps, ref bool __result)
    {
        using var perfScope = PgPerf.Measure(PgPerf.Wheel);   // task 9.2a: the cost instrument (off: one volatile read)
        try
        {
            if (steps == 0 || !ReferenceEquals(__instance, ScreenHost.Host) || current.Layout is not { } l) return true;
            if (__instance.HitTest(p) is not { } hit || hit.Screen != RewardsUISim.ScreenId) return true;
            var c = hit.Control;
            string name = c.Properties.TryGetValue("TemplateOwner", out var owner) && owner.Length > 0 ? owner : c.Name;
            if (name.IndexOf('@') is int at and > 0) name = name[..at];   // a template part (owner@Part#n) counts as its widget
            if (!N.WheelCanvas(name, out var canvas)) return true;
            var info = canvas switch
            {
                RewardsScrollModel.Canvas.Fighter => l.Fighter,
                RewardsScrollModel.Canvas.Profile => l.Profile,
                _ => l.Detail,
            };
            string scrollName = canvas switch
            {
                RewardsScrollModel.Canvas.Fighter => N.FighterScroll,
                RewardsScrollModel.Canvas.Profile => N.ProfileScroll,
                _ => N.DetailScroll,
            };
            string id = RewardsUISim.ScreenId;
            scroll.Observe(canvas, (int)__instance.GetScroll(id, scrollName));
            scroll.OnWheel(canvas, steps > 0 ? 1 : -1, l.Body, info.Content, info.View);   // MyGUI's relative: + up, Meitou's steps: + up
            int offset = canvas switch
            {
                RewardsScrollModel.Canvas.Fighter => scroll.Fighter,
                RewardsScrollModel.Canvas.Profile => scroll.Profile,
                _ => scroll.Detail,
            };
            __instance.SetScroll(id, scrollName, offset);
            __result = true;
            return false;
        }
        catch (Exception e)
        {
            ScreenHost.LogScreenError(RewardsUISim.ScreenId, "wheel", e);
            return true;
        }
    }

    // ---------- PgCheck ui: one model per shape ----------

    /// <summary>A world-free host for the check's models (the real rules half builds them).</summary>
    sealed class CheckHost(int fighters, string name) : IRewardsUIHost<CharacterId>
    {
        public readonly HashSet<string> Unlocks = new(StringComparer.Ordinal);
        public IReadOnlyList<CharacterId> PlayerSquad() => [.. Enumerable.Range(1, fighters).Select(i => new CharacterId(i, 0))];
        public bool Valid(CharacterId c) => !c.IsNone;
        public bool IsDead(CharacterId c) => false;
        public string Name(CharacterId c) => name + " " + c.Slot.ToString(CultureInfo.InvariantCulture);
        public int GetMarks(CharacterId c) => 40 * c.Slot;
        public bool HasFactionUnlock(string id) => Unlocks.Contains(id);
        public ArenaRewards.Offer ResolveOffer(CharacterId c, ArenaRewards.Piece piece)
        {
            var first = ArenaRewards.FirstTier(piece);
            if (c.Slot % 3 == 0 && (int)piece % 4 == 0) return new ArenaRewards.Offer { Kind = ArenaRewards.OfferKind.Complete, Tier = first };
            return new ArenaRewards.Offer { Kind = (int)piece % 5 == 1 ? ArenaRewards.OfferKind.Replace : ArenaRewards.OfferKind.Claim, Tier = first,
                Cost = ArenaRewards.Get(piece, first).Cost };
        }
        public void Error(string line) { }
        public string? RewardIconRecord(ArenaRewards.Piece piece) => "check-" + piece;
        public bool RecordExists(string recordId) => true;
    }

    static IReadOnlyList<PgUiModel?> CheckModels()
    {
        static RewardsUiModel Model(RewardsCatalogTab tab, int fighters, string name, params string[] unlocks) => Selecting(tab, fighters, 1, name, unlocks);
        static RewardsUiModel Selecting(RewardsCatalogTab tab, int fighters, int selected, string name, params string[] unlocks)
        {
            var host = new CheckHost(fighters, name);
            foreach (var u in unlocks) host.Unlocks.Add(u);
            var logic = new RewardsUILogic<CharacterId>(tab);
            logic.Show(host, fighters > 0, new CharacterId(selected, 0));
            var v = logic.BuildView();
            return new RewardsUiModel(v) { RequestSerial = v.RequestSerial, RequestOpen = v.RequestOpen };
        }
        string weapons = ArenaRewards.GetWeaponLicence().StableId, t1 = ArenaRewards.GetLicence(ArenaRewards.ArmourSet.T1).StableId,
            t2 = ArenaRewards.GetLicence(ArenaRewards.ArmourSet.T2).StableId;
        string longName = string.Concat(Enumerable.Repeat("Shutori of the Long Name ", 6));
        return
        [
            null,
            Model(RewardsCatalogTab.General, 1, "Fighter"),
            Model(RewardsCatalogTab.General, 6, "Fighter", "blueprint.arena1"),
            Model(RewardsCatalogTab.Armour, 6, "Fighter"),
            Model(RewardsCatalogTab.Armour, 6, "Fighter", t1, t2),
            Model(RewardsCatalogTab.Weapons, 3, "Fighter"),
            Model(RewardsCatalogTab.Weapons, 3, "Fighter", weapons),
            Model(RewardsCatalogTab.General, 0, "Nobody"),                     // no eligible fighter
            Model(RewardsCatalogTab.Armour, 2, longName, t1),                   // long names, long status line
            Model(RewardsCatalogTab.Weapons, 14, "Fighter", weapons),          // the fighter canvas scrolls
            Selecting(RewardsCatalogTab.Weapons, 3, 3, "Fighter", weapons),    // task 5.4: fighter 3 completed some pieces (other accent grades, same shape as 6)
        ];
    }
}

/// <summary>
/// The Rewards cards' icons on the window thread (task 5.3b): the C++ makes the reward item, asks InventoryIcon::createIconImage for its texture and
/// destroys the item (BindRewardIcon :360-404, BindGeneralIcon :405-439). Meitou draws an inventory icon from the item's record and material alone, so
/// no item is made: Items.PreviewLook gives the record and material CreateRewardItem would give it (read-only game data), and the inventory cells'
/// own icon source (PlayerInterface.ItemCells.ItemIcons, Part.RewardsIcons) chooses the PNG or the picture to draw (ItemIcons.Choose; the colour of
/// a new item is its record's, the "" of Choose). Sizes are known at once (the layout needs them); the texture shows when it is on the GPU.
/// </summary>
static class RewardIcons
{
    sealed record Entry((int W, int H) Size, string? File, string? Rendered, IReadOnlyDictionary<string, string> Props);

    static object? iface;
    static ItemIcons? source;
    static bool logged;
    static readonly Dictionary<RewardsIconView, Entry?> entries = [];

    /// <summary>Bumped when the icon source changes (a new interface, the icons appear): the shape key carries it.</summary>
    internal static int Version { get; private set; }

    /// <summary>Reads the icon source again on a new interface, or while the window is open without one; true when it changed.</summary>
    internal static bool Refresh(bool open)
    {
        var ui = ScreenHost.Interface;
        if (ReferenceEquals(ui, iface) && (source is not null || !open)) return false;
        iface = ui;
        ItemIcons? now = null;
        try
        {
            if (ui is not null && Members.ItemCellsProp is { } cellsProp && Members.ItemIconsField is { } iconsField)
                now = iconsField.GetValue(cellsProp.GetValue(ui)) as ItemIcons;
            else if (ui is not null && !logged)
            {
                logged = true;
                ProvingGroundsMod.Ctx?.Log("Rewards window icons off: " + string.Join(", ", Members.Missing(Part.RewardsIcons)) + " not found");
            }
        }
        catch (Exception e) { ScreenHost.LogScreenError(RewardsUISim.ScreenId, "icons", e); }
        if (ReferenceEquals(now, source)) return false;
        source = now;
        entries.Clear();
        Version++;
        return true;
    }

    static Entry? EntryOf(RewardsIconView look)
    {
        if (look.Record is null || source is not { } icons) return null;
        if (entries.TryGetValue(look, out var e)) return e;
        e = null;
        try
        {
            var db = icons.Database;
            if (Items.PreviewLook(db, look.Record, look.Quality, look.Model, look.Maker) is var (record, material))
            {
                var choice = icons.Choose(record, material, "");
                if (choice is { Source: ItemIconSource.File, File: { } file })
                    e = new Entry(PngSize(file), file, null, new Dictionary<string, string>(StringComparer.Ordinal) { ["ImageTexture"] = UiPainter.IconTexture(file), ["Visible"] = "true" });
                else if (choice.Source == ItemIconSource.Render && db.Find(record) is { } r)
                {
                    var spec = ItemIcons.MaterialOf(db, r, material);
                    string name = UiPainter.RenderedIconTexture(record, material, choice.Colour);
                    e = new Entry(ItemIconStyle.Of(r, spec).TextureSize, null, name,
                        new Dictionary<string, string>(StringComparer.Ordinal) { ["ImageTexture"] = name, ["ImageFlipV"] = "true", ["Visible"] = "true" });
                }
            }
        }
        catch (Exception ex) { ScreenHost.LogScreenError(RewardsUISim.ScreenId, "icon " + look.Record, ex); }
        if (entries.Count > 256) entries.Clear();
        entries[look] = e;
        return e;
    }

    /// <summary>The icon's texture size for the layout, or null when the card has no icon (C++ iconLoaded false: no item data, no texture).</summary>
    internal static (int W, int H)? Size(RewardsIconView look) => EntryOf(look)?.Size;

    /// <summary>The ImageBox properties when the texture can be drawn now (asking for it the first time), else null.</summary>
    internal static IReadOnlyDictionary<string, string>? Ready(RewardsIconView look)
    {
        if (EntryOf(look) is not { } e || ScreenHost.Painter is not { } painter) return null;
        bool ready = e.File is { } file ? painter.IconReady(file) : e.Rendered is { } name && painter.RenderedIconReady(name);
        return ready ? e.Props : null;
    }

    /// <summary>A PNG's width and height from its header (the saved and the static icons are PNGs); (0, 0) when it cannot be read. A 24-byte read
    /// of a game data file on the window thread, once per icon (the entry is cached): read-only game data, no world access.</summary>
    static (int W, int H) PngSize(string path)
    {
        try
        {
            Span<byte> head = stackalloc byte[24];
            using var f = File.OpenRead(path);
            if (f.Read(head) < 24 || head[1] != (byte)'P' || head[2] != (byte)'N' || head[3] != (byte)'G') return (0, 0);
            return ((int)BinaryPrimitives.ReadUInt32BigEndian(head[16..]), (int)BinaryPrimitives.ReadUInt32BigEndian(head[20..]));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return (0, 0); }
    }
}
