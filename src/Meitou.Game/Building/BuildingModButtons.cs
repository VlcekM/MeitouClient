using System.Globalization;
using Meitou.Game.Ui;

namespace Meitou.Game.Building;

/// <summary>
/// The code mods' buttons on the selected building's panel (docs/modding.md "Building panel buttons",
/// <see cref="UiRegistration.AddBuildingActions"/>): one button line each, after the game's own stats lines, laid out with the panel; a press runs
/// the button shown for the building it was shown for.
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
    /// The button lines of the mods' buttons for <paramref name="focus"/> (none without a mod that adds any): the caption across the line, its
    /// tooltip on the button.
    /// </summary>
    internal PlayerInterface.HudLine[] Lines(BuildingSelection.SelectedBuilding focus)
    {
        if (!player.HasModBuildingActions)
        {
            _shown = [];
            return [];
        }

        var building = new UiSelectedBuilding(focus.Instance, focus.Record, focus.SiteId, focus.Name, Owned(focus));
        _shown = player.ModBuildingActions(building, player.Session.CurrentSnapshot);
        _shownFor = focus.Instance;

        var lines = new PlayerInterface.HudLine[_shown.Count];
        for (int k = 0; k < lines.Length; k++)
        {
            var button = _shown[k];
            string argument = k.ToString(CultureInfo.InvariantCulture);
            lines[k] = new PlayerInterface.HudLine("", button.Caption, Tooltip: button.Tooltip, Action: LineAction) { Argument = argument };
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
