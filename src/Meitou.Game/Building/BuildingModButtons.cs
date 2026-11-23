using System.Globalization;
using Meitou.Game.Ui;

namespace Meitou.Game.Building;

/// <summary>
/// The code mods' buttons and bars on the selected building's panel (docs/modding.md "Building panel buttons" and "Building panel bars",
/// <see cref="UiRegistration.AddBuildingActions"/>, <see cref="UiRegistration.AddBuildingRows"/>): one button line each, after the game's own stats
/// lines, laid out with the panel; a press runs the button shown for the building it was shown for. The bars go under Condition.
/// </summary>
sealed class BuildingModButtons(PlayerInterface player)
{
    /// <summary>
    /// The interface action a mod's button runs (registered by Meitou's own extension); its argument is the button's index in <see cref="_shown"/>.
    /// </summary>
    internal const string Action = "building-mod";

    /// <summary>
    /// The registered key of <see cref="Action"/>, which the button line names.
    /// </summary>
    const string LineAction = "meitou:" + Action;

    /// <summary>
    /// The buttons the panel last showed.
    /// </summary>
    IReadOnlyList<UiBuildingAction> _shown = [];

    /// <summary>
    /// The building <see cref="_shown"/> was laid out for (its instance key), so a press after the selection changed does nothing.
    /// </summary>
    string? _shownFor;

    /// <summary>
    /// The button lines of the mods' buttons for <paramref name="focus"/> (none without a mod that adds any): the caption across the line in the
    /// panel's main colour, its tooltip on the button. Lines past the stats panel's lower edge are clipped by the panel (not drawn, not clickable).
    /// </summary>
    internal PlayerInterface.HudLine[] Lines(BuildingSelection.SelectedBuilding focus)
    {
        if (!player.HasModBuildingActions)
        {
            _shown = [];
            return [];
        }

        _shown = player.ModBuildingActions(Selected(focus), player.Session.CurrentSnapshot);
        _shownFor = focus.Instance;

        var lines = new PlayerInterface.HudLine[_shown.Count];
        for (int k = 0; k < lines.Length; k++)
        {
            var button = _shown[k];
            string argument = k.ToString(CultureInfo.InvariantCulture);
            // The caption in the panel's main colour, as the game's own neutral action lines (PAUSE CONSTRUCTION, the downgrade): a line without
            // a colour falls back to the dark ink of parchment buttons, unreadable on this panel.
            lines[k] = new PlayerInterface.HudLine("", button.Caption, ValueColour: PlayerInterface.MainColour, Tooltip: button.Tooltip,
                Action: LineAction) { Argument = argument };
        }

        return lines;
    }

    /// <summary>
    /// A press of the mod button <paramref name="argument"/> (its index): runs it with the click sound while its building is still the one selected
    /// and still exists, by the check the game's own panel buttons make (<see cref="BuildingSelection.BuildingFocus"/>).
    /// </summary>
    internal void Press(string argument)
    {
        if (!int.TryParse(argument, NumberStyles.Integer, CultureInfo.InvariantCulture, out int k) || (uint)k >= (uint)_shown.Count) return;
        if (player.BuildingSelection.BuildingFocus()?.Instance != _shownFor) return;

        player.InterfaceSounds.Sound("General_Click");
        _shown[k].Press();
    }

    /// <summary>
    /// The mods' bars for <paramref name="focus"/> (none without a mod that adds any), as medical panel rows: the bar in the healed (yellow) layer, as
    /// the game's own Condition bar.
    /// </summary>
    internal IEnumerable<PlayerInterface.MedicalRow> Rows(BuildingSelection.SelectedBuilding focus)
    {
        if (!player.HasModBuildingRows) return [];

        return player.ModBuildingRows(Selected(focus), player.Session.CurrentSnapshot)
            .Select(r => new PlayerInterface.MedicalRow(r.Name, 0, r.Fraction, 0, r.Value, "", false, r.Tooltip));
    }

    /// <summary>
    /// <paramref name="focus"/> as the mods' sources see it.
    /// </summary>
    UiSelectedBuilding Selected(BuildingSelection.SelectedBuilding focus) =>
        new(focus.Instance, focus.Record, focus.SiteId, focus.Name, Owned(focus));

    /// <summary>
    /// Whether the player's faction owns <paramref name="focus"/>: a building of its own (not a rock's or a town machine's site), or a town building
    /// the published detail calls owned, as the panel decides when it shows the game's own buttons.
    /// </summary>
    bool Owned(BuildingSelection.SelectedBuilding focus)
    {
        if (focus.SiteId is { } id)
        {
            bool natural = Simulation.Production.ProductionSystem.IsNatural(id);
            if (!natural && !Simulation.Production.ProductionSystem.IsTownWork(id)) return true;
        }

        return player.Session.CurrentSnapshot.InspectedBuilding is { Owned: true } detail && detail.Instance == focus.Instance;
    }
}
