using System.Numerics;
using Meitou.Data.Ui;
using Meitou.Game.Title;
using Meitou.Simulation.Items;

namespace Meitou.Game.Ui;

/// <summary>
/// Meitou's split stack dialog (docs/game/ui-screens.md "Holding an item"): Shift + Alt on a stack with stack splitting on opens the message box
/// with a slider from one unit to the whole stack; Take lifts that many onto the mouse, Cancel or Esc lifts nothing.
/// </summary>
sealed class StackSplitDialog(PlayerInterface player)
{
    /// <summary>
    /// The units the dialog lifts, from 1 to <see cref="_max"/>.
    /// </summary>
    internal int Amount;

    /// <summary>
    /// The stack's quantity: the slider's top.
    /// </summary>
    int _max;

    /// <summary>
    /// Opens the dialog for <paramref name="stack"/> at half of it (rounded up); Take runs <paramref name="take"/> with the chosen units.
    /// </summary>
    internal void Ask(HeldItemView stack, Action<int> take)
    {
        _max = Math.Max(1, stack.Quantity);
        Amount = (_max + 1) / 2;
        player.Frontend.PendingConfirmation = new(Loc.Tr("Split stack").ToUpperInvariant(), "", () => take(Amount),
            Loc.Tr("Take").ToUpperInvariant(), Cancel: TitleMenu.UpperTr("CANCEL")) { SplitAmount = true };
        player.FaithfulScreens.Open("message");
        player.UpdateMenuPause();
    }

    /// <summary>
    /// A slider event of the open dialog: the track sets the share of the stack, the arrows step one unit; false for any other event (a button).
    /// </summary>
    internal bool AmountAction(MyGuiEvent e)
    {
        int amount = e.Action switch
        {
            "split-slider" => 1 + (int)MathF.Round(Math.Clamp(e.Value, 0, 1) * (_max - 1)),
            "split-less" => Amount - 1,
            "split-more" => Amount + 1,
            _ => -1,
        };
        if (amount < 0) return false;

        Amount = Math.Clamp(amount, 1, _max);
        return true;
    }

    /// <summary>
    /// The dialog's content in the message text's area: "Take N of M" in its upper half, the left arrow, the slider and the right arrow in its
    /// lower half (the layout's Kenshi_MessageBox gives the text about 330 x 83 px at 1080p).
    /// </summary>
    internal void AddAmountRow(List<UiControl> controls)
    {
        if (controls.FirstOrDefault(c => c.Name == "MessageText") is not { } text) return;

        float s = player.UiScale;
        var area = text.Bounds;
        float half = area.Height / 2;
        AddLabel(controls, new(area.X, area.Y, area.Width, half));
        AddSlider(controls, new(area.X, area.Y + half, area.Width, half), s);
    }

    /// <summary>
    /// "Take N of M", centred in the Main colour.
    /// </summary>
    void AddLabel(List<UiControl> controls, UiRect at)
    {
        var properties = new Dictionary<string, string>
        {
            ["FontName"] = "Kenshi_StandardFont_Medium",
            ["TextColour"] = PlayerInterface.MainColour,
            ["TextAlign"] = "Center",
        };
        controls.Add(new("split-label", "TextBox", at, Loc.Tr("Take {1} of {2}", Amount, _max), "", "", true, properties, Vector4.Zero,
            Pickable: false));
    }

    /// <summary>
    /// The arrows and the track with its knob at the chosen share, as the advanced options' sliders draw them.
    /// </summary>
    void AddSlider(List<UiControl> controls, UiRect row, float s)
    {
        float arrow = 24 * s;
        float y = row.Y + (row.Height - arrow) / 2;
        var none = new Dictionary<string, string>();
        controls.Add(new("split-less", "Button", new(row.X, y, arrow, arrow), "", "split-less", "Kenshi_SpeedArrowLeftSkin", true, none,
            Vector4.Zero));
        controls.Add(new("split-more", "Button", new(row.X + row.Width - arrow, y, arrow, arrow), "", "split-more", "Kenshi_SpeedArrowRightSkin",
            true, none, Vector4.Zero));

        var track = new UiRect(row.X + arrow + 6 * s, row.Y + (row.Height - 18 * s) / 2, row.Width - 2 * arrow - 12 * s, 18 * s);
        controls.Add(new("split-track", "Widget", track, "", "split-slider", "Kenshi_SliderSkin", true,
            new Dictionary<string, string> { ["Slider"] = "true" }, Vector4.Zero, Pickable: true));

        float t = _max > 1 ? (Amount - 1f) / (_max - 1) : 1;
        var knob = new UiRect(track.X + t * track.Width - 6 * s, row.Y + (row.Height - 28 * s) / 2, 12 * s, 28 * s);
        controls.Add(new("split-knob", "Button", knob, "", "", "Kenshi_SliderButtonSkin", true, none, Vector4.Zero, Pickable: false));
    }
}
