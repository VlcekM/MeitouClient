using Meitou.Data.Ui;
using Meitou.Game.Rendering.Characters;

namespace Meitou.Game.Ui;

/// <summary>
/// The portraits of CHARACTER records that code mods show (<see cref="UiNode.Portrait"/>; docs/native-ui.md "Layout, input and lifetime"): each
/// record on the interface gets a cell of the portrait atlas after the world's characters' (<see cref="CharacterRenderer.LivePortraitCells"/>),
/// which the host has the renderer draw from a hidden instance of the record's look.
/// </summary>
sealed class RecordPortraits(PlayerInterface player)
{
    /// <summary>
    /// The property of an arranged image control that names the record whose portrait it shows.
    /// </summary>
    public const string Property = "Portrait";

    /// <summary>
    /// How many records have a portrait at once; the image of one past them shows nothing.
    /// </summary>
    public const int Cells = CharacterRenderer.PortraitColumns * CharacterRenderer.PortraitRows - CharacterRenderer.LivePortraitCells;

    /// <summary>
    /// The atlas cell of each record that has one. A record no longer shown keeps its cell (and its picture) until another record needs it.
    /// </summary>
    readonly Dictionary<string, int> _cells = new(StringComparer.Ordinal);

    /// <summary>
    /// The records of the last arrangement, in its order, with their cells.
    /// </summary>
    readonly List<(string Record, int Cell)> _wanted = [];

    /// <summary>
    /// The records the interface shows and the atlas cell of each, for the host to draw.
    /// </summary>
    public IReadOnlyList<(string Record, int Cell)> Wanted => _wanted;

    /// <summary>
    /// <paramref name="controls"/> with every record portrait given its cell's picture; these records are then the <see cref="Wanted"/> ones.
    /// </summary>
    internal IReadOnlyList<UiControl> Resolve(IReadOnlyList<UiControl> controls)
    {
        _wanted.Clear();
        if (!controls.Any(c => c.Properties.ContainsKey(Property))) return controls;

        var records = controls.Select(c => c.Properties.GetValueOrDefault(Property)).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        foreach (string record in records)
        {
            if (CellOf(record, records) is { } cell) _wanted.Add((record, cell));
        }

        return [.. controls.Select(Shown)];
    }

    /// <summary>
    /// <paramref name="record"/>'s cell: its own, else a free one, else that of a record not among <paramref name="shown"/>; null when every cell
    /// is taken by one of them.
    /// </summary>
    int? CellOf(string record, List<string> shown)
    {
        if (_cells.TryGetValue(record, out int cell)) return cell;

        var free = Enumerable.Range(CharacterRenderer.LivePortraitCells, Cells).Except(_cells.Values).ToList();
        if (free.Count > 0) return _cells[record] = free[0];
        if (_cells.Keys.FirstOrDefault(r => !shown.Contains(r)) is not { } gone) return null;

        _cells.Remove(gone, out cell);
        return _cells[record] = cell;
    }

    /// <summary>
    /// A record portrait's control showing its cell of the atlas (nothing without a cell); any other control as it is.
    /// </summary>
    UiControl Shown(UiControl control)
    {
        if (!control.Properties.TryGetValue(Property, out string? record) || !_cells.TryGetValue(record, out int cell)) return control;
        if (!_wanted.Contains((record, cell))) return control;

        var properties = new Dictionary<string, string>(control.Properties)
        {
            ["ImageTexture"] = PortraitBar.PortraitTexture,
            ["ImageFlipV"] = "true",
            ["ImageRect"] = player.PortraitBar.PortraitRect(cell),
        };
        return control with { Properties = properties };
    }
}
