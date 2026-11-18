// Port of upstream/ProvingGrounds/plugin/src/MarksHUD.h/.cpp (Proving Grounds by Shutori, GPLv3), task 4.6b (PA §2.23; CAP 7.1, 7.9). The sim half
// (the 250 ms cadence, the save and Scratch checks, the caption) is src/Game/MarksHUDSim.cs, which publishes the MarksHudModel; this file is the
// label: the C++'s root widget and label (Refresh :49-60) as an AddUi overlay panel and label (CAP 7.9's first route, "Ready"), placed just above
// the money display from the money widgets' arranged rectangles (:64-80, Core/Glue/MarksHUDLogic.Place). Window thread only.
//
// Engine calls of the C++ and what replaces them:
//   gui->mainbar->moneyText / moneyLabel, getInheritedVisible,   -> the HUD's arranged controls "MoneyAmountText" / "MoneyLabelText"
//     getAbsoluteLeft / Top, getWidth                                 (PlayerInterface._hudControls, PlayerInterface.Ui.cs:420, :587-596: hidden
//                                                                     widgets and their children are not arranged, UiDocument.cs:114-117), their
//                                                                     Bounds (screen pixels)
//   createWidget<Widget>("WhiteSkin", ..., "Main") + setColour     -> an AddUi Panel node in the overlay slot (drawn with the HUD, after its
//     (0.12, 0.10, 0.08)                                              controls, under every faithful window: PlayerInterface.Ui.cs:504-517) with
//                                                                     that background
//   NativeUI::Label (Kenshi_TextboxStandardText) + setTextAlign    -> an AddUi Label node: the standard font, the label's ink (MyGuiColours.Ink,
//     (Center), setNeedMouseFocus(false)                              the palette's Secondary as Kenshi_TextboxStandardText's TextColour), centred,
//                                                                     not pickable
//   getTextSize / getFontHeight (NativeUI::Spacing / RowHeight)    -> TextMeasure (src/Ui/TextMeasure.cs) / ScreenHost.FontHeight
//   RenderManager::getViewSize                                     -> the frame's viewport
//   panel->setVisible(false) (Hide)                                -> the overlay returns a hidden node (the overlay itself does not run while the
//                                                                     HUD is hidden: title, character editor, a conversation)
//   PGLog::Error on a throw, failed = true                         -> ModLog.Error once, nothing drawn until the next AbandonWorldState (epoch)
using System.Linq.Expressions;
using System.Numerics;
using Meitou.Data.Ui;
using Meitou.Game.Ui;
using Meitou.Modding;
using Meitou.Simulation;
using ProvingGrounds.Core;
using ProvingGrounds.Core.Glue;
using ProvingGrounds.Engine;
using ProvingGrounds.Game;

namespace ProvingGrounds.Ui;

/// <summary>The glue of the original MarksHUD.h entry points the system calls (sim thread). The label itself is <see cref="MarksHudOverlay"/>.</summary>
sealed class MarksHUD(MarksHUDSim sim)
{
    internal MarksHUDSim Sim => sim;

    // C++: MarksHUD.cpp:87, called from ArenaUI.cpp:3165 (ForgottenGUI_update_hook)
    internal void Tick(World world) => sim.Tick(world);

    // C++: MarksHUD.cpp:100-104; ArenaUI::AbandonWorldState calls it first (task 4.5a, ArenaUI.cpp:3199)
    internal void AbandonWorldState(World world) => sim.AbandonWorldState();
}

/// <summary>
/// The Marks HUD's label (task 4.6b): an AddUi extension contributing to the overlay slot. Each frame (the overlay factory runs whenever the
/// interface arranges its base with the HUD shown) it reads the published <see cref="MarksHudModel"/> and the HUD's money widgets and returns the
/// panel, or a hidden node. The panel's rectangle is computed again only when the caption, the money widgets, the view or the font changed.
/// </summary>
sealed class MarksHudOverlay : IUiExtension
{
    static readonly UiNode Hidden = new("proving-grounds.marks-hud.hidden") { Visible = false, Pickable = false };
    static ModContext? ctx;
    static Func<object, IReadOnlyList<UiControl>?>? hudControls;
    // one Meitou-log line for the first overlay failure: the sim half's status (F10 phase) cannot see a failure of the window side
    static bool failureLogged;

    string panelStyle = "", textStyle = "";
    readonly TextMeasure text = new();
    (string Text, LayoutRect Money, LayoutRect? Label, int ViewW, int Font, int Epoch)? builtFor;
    UiNode built = Hidden;
    int failedEpoch = -1;

    public string Id => "proving-grounds.marks-hud";

    /// <summary>Adds the overlay contribution (from the mod's Initialize). Without the HUD-controls member the HUD is off (one log line).</summary>
    internal static void Install(ModContext c)
    {
        ctx = c;
        try
        {
            var missing = Members.Missing(Part.MarksHud).ToList();
            if (missing.Count > 0) { c.Log($"Marks HUD off: {string.Join(", ", missing)} not found"); return; }
            c.AddUi(new MarksHudOverlay());
        }
        catch (Exception e) { c.Log($"Marks HUD off: {e.GetType().Name}: {e.Message}"); }
    }

    public void Register(UiRegistration registration)
    {
        // C++ :53 setColour(0.12, 0.10, 0.08) on WhiteSkin; :56-59 Kenshi_TextboxStandardText (Kenshi_StandardFont_Medium, its TextColour), centred
        panelStyle = registration.RegisterStyle("panel", new UiStyle { Background = new Vector4(0.12f, 0.10f, 0.08f, 1f) });
        textStyle = registration.RegisterStyle("text", new UiStyle
        {
            Background = Vector4.Zero, Text = MyGuiColours.Ink, TextAlign = "Center", FontName = TextMeasure.StandardFont,
        });
        builtFor = null;
        built = Hidden;
        registration.Contribute(UiSlots.Overlay, Frame);
    }

    /// <summary>The HUD's arranged controls (PlayerInterface._hudControls), read through a compiled field access; null without an interface.</summary>
    static IReadOnlyList<UiControl>? HudControls()
    {
        if (ScreenHost.Interface is not { } ui || Members.HudControls is not { } f) return null;
        if (hudControls is null)
        {
            var p = Expression.Parameter(typeof(object), "ui");
            hudControls = Expression.Lambda<Func<object, IReadOnlyList<UiControl>?>>(
                Expression.Convert(Expression.Field(Expression.Convert(p, f.DeclaringType!), f), typeof(IReadOnlyList<UiControl>)), p).Compile();
        }
        return hudControls(ui);
    }

    static LayoutRect Pixels(UiRect r) =>
        new((int)MathF.Floor(r.X), (int)MathF.Floor(r.Y), (int)MathF.Floor(r.Width), (int)MathF.Floor(r.Height));

    // C++: MarksHUD.cpp:40-82 Refresh, the window half (the sim half decided Show and the caption)
    UiNode Frame(UiFrameContext frame)
    {
        if (ProvingGroundsSystem.Current?.Snapshot.Model(MarksHUDSim.ModelId) is not MarksHudModel model || !model.Show) return Hidden;
        if (model.Epoch == failedEpoch) return Hidden;
        try
        {
            var controls = HudControls();
            if (controls is null) return Hidden;
            UiControl? money = null, moneyLabel = null;
            for (int i = 0; i < controls.Count; i++)
            {
                var c = controls[i];
                if (c.Name == "MoneyAmountText") money = c;
                else if (c.Name == "MoneyLabelText") moneyLabel = c;
            }
            if (money is null) return Hidden;   // C++ :45 !money || !money->getInheritedVisible()
            var key = (model.Text, Pixels(money.Bounds), moneyLabel is null ? (LayoutRect?)null : Pixels(moneyLabel.Bounds),
                (int)frame.Viewport.Width, ScreenHost.FontHeight, model.Epoch);
            if (builtFor == key) return built;
            text.UseCurrent();
            int font = ScreenHost.FontHeight;
            var size = text.Size(TextMeasure.StandardFont, model.Text, 0);   // a TextBox: one line, no wrapping
            var (panel, label) = MarksHudLogic.Place(key.Item2, key.Item3, key.Item4, size.Width, size.Height, font);
            var node = new UiNode("proving-grounds.marks-hud.PG_MarksHUD")
            {
                Placement = new UiPlacement(panel.W, panel.H, panel.X, panel.Y), Style = panelStyle, Pickable = false,
            };
            node.Children.Add(new UiNode("proving-grounds.marks-hud.PG_MarksHUDText", UiNodeKind.Label)
            {
                Placement = new UiPlacement(label.W, label.H, label.X, label.Y), Style = textStyle, Text = model.Text, Pickable = false,
            });
            built = node;
            builtFor = key;
            // TEST ONLY: one line per new placement while the test verbs are armed (the marks-debug scenario reads it; the label is not pickable,
            // so the agent's ui-tree does not list it)
            if (Spikes.SpikePatches.Armed)
                ctx?.Log(System.FormattableString.Invariant(
                    $"PG-MARKS {{\"event\":\"drawn\",\"text\":{PGLog.Quote(model.Text)},\"panel\":[{panel.X},{panel.Y},{panel.W},{panel.H}],\"money\":[{key.Item2.X},{key.Item2.Y},{key.Item2.W},{key.Item2.H}]}}"));
            return node;
        }
        catch (Exception e)
        {
            failedEpoch = model.Epoch;   // C++ :94-96: Hide, failed, one error line
            builtFor = null;
            built = Hidden;
            ModLog.Error(MarksHudTexts.Unavailable + e.Message);
            if (!failureLogged) { failureLogged = true; ctx?.Log($"Marks HUD failed: {e.GetType().Name}: {e.Message}"); }
            return Hidden;
        }
    }
}
