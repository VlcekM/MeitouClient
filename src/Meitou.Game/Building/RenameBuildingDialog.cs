using System.Globalization;
using System.Numerics;
using Meitou.Data.Ui;
using Meitou.Game.Title;
using Meitou.Game.Ui;
using Meitou.Simulation.Construction;

namespace Meitou.Game.Building;

/// <summary>
/// Meitou's rename dialog for a building the player built (docs/game/ui-screens.md "Selected building"): the panel's Rename opens the message box
/// with a text box holding the building's name; Rename sends <see cref="RenameSite"/> with the text typed (empty gives back the record's name),
/// Cancel or Esc changes nothing.
/// </summary>
sealed class RenameBuildingDialog(PlayerInterface player)
{
    /// <summary>
    /// The text box's widget name.
    /// </summary>
    internal const string Box = "rename-box";

    /// <summary>
    /// The message screen the dialog shows in.
    /// </summary>
    const string Screen = "message";

    /// <summary>
    /// The name typed so far.
    /// </summary>
    string _name = "";

    /// <summary>
    /// Whether the text box still has to take the typing focus (the first frame after opening).
    /// </summary>
    bool _focusPending;

    /// <summary>
    /// Opens the dialog for site <paramref name="site"/> with <paramref name="current"/> in the box.
    /// </summary>
    internal void Ask(string site, string current)
    {
        _name = current;
        _focusPending = true;
        var caption = Loc.Tr("Rename").ToUpperInvariant();
        player.Frontend.PendingConfirmation = new(caption, "", () => Rename(site), caption, Cancel: TitleMenu.UpperTr("CANCEL")) { NameEntry = true };
        player.FaithfulScreens.Open(Screen);
        player.FaithfulScreens.SetText(Screen, Box, current);
        player.UpdateMenuPause();
    }

    /// <summary>
    /// An event of the text box (its typed text is kept); false for any other event (a button).
    /// </summary>
    internal bool NameAction(MyGuiEvent e)
    {
        if (e.Widget != Box) return false;

        if (e.Action == "text") _name = e.Text;
        return true;
    }

    /// <summary>
    /// The dialog's content in the message text's area: the hint in its upper half, the text box in its lower half.
    /// </summary>
    internal void AddNameRow(List<UiControl> controls)
    {
        if (controls.FirstOrDefault(c => c.Name == "MessageText") is not { } text) return;

        var area = text.Bounds;
        float half = area.Height / 2;
        AddHint(controls, new(area.X, area.Y, area.Width, half));
        AddBox(controls, new(area.X, area.Y + half + (half - 30 * player.UiScale) / 2, area.Width, 30 * player.UiScale));
    }

    /// <summary>
    /// The hint over the box, centred in the Main colour.
    /// </summary>
    static void AddHint(List<UiControl> controls, UiRect at)
    {
        var properties = new Dictionary<string, string>
        {
            ["FontName"] = "Kenshi_StandardFont_Medium",
            ["TextColour"] = PlayerInterface.MainColour,
            ["TextAlign"] = "Center",
            ["WordWrap"] = "true",
        };
        controls.Add(new("rename-hint", "TextBox", at, Loc.Tr("Leave it empty for the building's own name."), "", "", true, properties,
            Vector4.Zero, Pickable: false));
    }

    /// <summary>
    /// The text box with the name typed, focused on the first frame, with the caret while it has the focus.
    /// </summary>
    void AddBox(List<UiControl> controls, UiRect at)
    {
        var properties = new Dictionary<string, string>
        {
            ["FontName"] = "Kenshi_StandardFont_Medium",
            ["TextColour"] = PlayerInterface.MainColour,
            ["TextAlign"] = "Left VCenter",
            ["MaxTextLength"] = RenameSite.MaxLength.ToString(CultureInfo.InvariantCulture),
        };
        var box = new UiControl(Box, "EditBox", at, _name, "", "Kenshi_EditBox", true, properties, Vector4.Zero);
        if (_focusPending)
        {
            _focusPending = false;
            player.FaithfulScreens.FocusDecoratedEdit(Screen, box);
        }

        controls.Add(WithCaret(box));
    }

    /// <summary>
    /// <paramref name="box"/> drawn focused with the caret and selection while it has the typing focus.
    /// </summary>
    UiControl WithCaret(UiControl box)
    {
        if (player.FaithfulScreens.Focus is not { Screen: Screen, Widget: Box } focus) return box;

        var properties = new Dictionary<string, string>(box.Properties, StringComparer.Ordinal)
        {
            ["Focused"] = "true",
            ["CaretIndex"] = focus.Caret.ToString(CultureInfo.InvariantCulture),
            ["SelectionStart"] = focus.SelectionStart.ToString(CultureInfo.InvariantCulture),
            ["SelectionLength"] = focus.SelectionLength.ToString(CultureInfo.InvariantCulture),
        };
        return box with { Properties = properties };
    }

    /// <summary>
    /// Rename: the site takes the name typed, with the click sound.
    /// </summary>
    void Rename(string site)
    {
        player.InterfaceSounds.Sound("General_Click");
        player.Say(new RenameSite(site, _name) { Tick = player.Session.World.Tick });
    }
}
