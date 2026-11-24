using Meitou.Data.Ui;
using Meitou.Game.Rendering.Characters;
using Meitou.Simulation;

namespace Meitou.Game.Ui;

/// <summary>
/// The portraits of CHARACTER records that code mods show (<see cref="UiNode.Portrait"/>; docs/native-ui.md "Layout, input and lifetime"): each
/// record on the interface gets a cell of the portrait atlas after the world's characters' (<see cref="CharacterRenderer.LivePortraitCells"/>),
/// which the host has the renderer draw from a hidden instance of the record's look. Live characters' portraits (<see cref="UiNode.PortraitOf"/>)
/// take the world characters' cells.
/// </summary>
sealed class RecordPortraits(PlayerInterface player)
{
    /// <summary>
    /// The property of an arranged image control that names the record whose portrait it shows.
    /// </summary>
    public const string Property = "Portrait";

    /// <summary>
    /// The property of an arranged image control that names the live character (<see cref="CharacterId.ToString"/>) whose portrait it shows
    /// (<see cref="UiNode.PortraitOf"/>).
    /// </summary>
    public const string LiveProperty = "PortraitOf";

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
    /// The live characters of the last arrangement that have no cell of the squad bar's, the squads screen's or the character trade's, with the
    /// cell each was given after those, for the host to draw.
    /// </summary>
    readonly List<(CharacterId Character, int Cell)> _live = [];

    /// <summary>
    /// The live characters the interface shows in cells of their own (<see cref="_live"/>), for the host to draw.
    /// </summary>
    public IReadOnlyList<(CharacterId Character, int Cell)> Live => _live;

    /// <summary>
    /// The atlas cell each live character of the last arrangement shows, by its id's text.
    /// </summary>
    readonly Dictionary<string, int> _liveCells = new(StringComparer.Ordinal);

    /// <summary>
    /// <paramref name="controls"/> with every record portrait given its cell's picture; these records are then the <see cref="Wanted"/> ones.
    /// </summary>
    internal IReadOnlyList<UiControl> Resolve(IReadOnlyList<UiControl> controls)
    {
        _wanted.Clear();
        _live.Clear();
        _liveCells.Clear();
        bool records = controls.Any(c => c.Properties.ContainsKey(Property));
        bool live = controls.Any(c => c.Properties.ContainsKey(LiveProperty));
        if (!records && !live) return controls;

        if (records) ResolveRecords(controls);
        if (live) ResolveLive(controls);
        return [.. controls.Select(Shown)];
    }

    /// <summary>
    /// Gives each record portrait of <paramref name="controls"/> its cell (<see cref="Wanted"/>).
    /// </summary>
    void ResolveRecords(IReadOnlyList<UiControl> controls)
    {
        var records = controls.Select(c => c.Properties.GetValueOrDefault(Property)).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        foreach (string record in records)
        {
            if (CellOf(record, records) is { } cell) _wanted.Add((record, cell));
        }
    }

    /// <summary>
    /// Gives each live portrait of <paramref name="controls"/> a cell: the one the host already draws the character into (the squad bar's, the
    /// squads screen's, a character trade row's), else the next free one after those and before the conversation's; none for a character not in
    /// the snapshot or past the free cells.
    /// </summary>
    void ResolveLive(IReadOnlyList<UiControl> controls)
    {
        var snapshot = player.Session.CurrentSnapshot;
        var drawn = new Dictionary<string, int>(StringComparer.Ordinal);
        var members = player.PortraitBar.PortraitMembers();
        for (int i = 0; i < members.Count; i++) drawn[members[i].Id.ToString()] = i;

        var trade = player.TradeWindows.CharacterTradePortraits();
        foreach (var (id, cell) in trade) drawn.TryAdd(id.ToString(), cell);

        int next = members.Count + trade.Count;
        int end = snapshot.Conversation is null ? PortraitBar.PortraitCells : PortraitBar.DialogueNpcCell;
        var ids = controls.Select(c => c.Properties.GetValueOrDefault(LiveProperty)).OfType<string>().ToHashSet(StringComparer.Ordinal);
        foreach (var c in snapshot.Characters)
        {
            string key = c.Id.ToString();
            if (!ids.Contains(key)) continue;

            if (drawn.TryGetValue(key, out int cell)) _liveCells[key] = cell;
            else if (next < end)
            {
                _liveCells[key] = next;
                _live.Add((c.Id, next++));
            }
        }
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
        if (control.Properties.TryGetValue(LiveProperty, out string? live))
            return _liveCells.TryGetValue(live, out int liveCell) ? Showing(control, liveCell) : control;
        if (!control.Properties.TryGetValue(Property, out string? record) || !_cells.TryGetValue(record, out int cell)) return control;
        if (!_wanted.Contains((record, cell))) return control;

        return Showing(control, cell);
    }

    /// <summary>
    /// <paramref name="control"/> showing atlas cell <paramref name="cell"/>.
    /// </summary>
    UiControl Showing(UiControl control, int cell)
    {
        var properties = new Dictionary<string, string>(control.Properties)
        {
            ["ImageTexture"] = PortraitBar.PortraitTexture,
            ["ImageFlipV"] = "true",
            ["ImageRect"] = player.PortraitBar.PortraitRect(cell),
        };
        return control with { Properties = properties };
    }
}
