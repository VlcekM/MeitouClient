namespace Meitou.Game.Ui;

/// <summary>
/// A line an extension adds to an item's tooltip (<see cref="UiRegistration.AddItemTooltipLines"/>): <paramref name="Label"/> in the tooltip's label
/// column, <paramref name="Value"/> (null or blank: none) in its value column.
/// </summary>
public sealed record UiItemTooltipLine(string Label, string? Value = null)
{
    /// <summary>
    /// The line's colours (<see cref="UiLineStyle"/>; its icon is not drawn in tooltips); null: the tooltip's own.
    /// </summary>
    public UiLineStyle? Style { get; init; }

    /// <summary>
    /// The label of the game's row the line goes right after (its own English label, untranslated, as <c>Sell value</c> or <c>Weight</c>; the last
    /// such row when several have it), after the lines placed there before it; null, or a label the tooltip has no row of: after the game's rows,
    /// before the record's description, as without it.
    /// </summary>
    public string? After { get; init; }
}

/// <summary>
/// What an extension's item tooltip lines get: the item's record string id, its name, its code-mod tag (<c>ItemInstance.ModTag</c>, "" none; a
/// trader's stock item has none), its quantity, level and quality; who holds it and its index there, whether this is its tooltip's first build,
/// the tick and the command queue.
/// </summary>
public sealed record UiItemTooltipContext(string Record, string Name, string ModTag, int Quantity, int Level, float Quality)
{
    /// <summary>
    /// Who holds the item (a character's inventory, a machine's or storage's site), which a command names it by with <see cref="Index"/>; null
    /// for an item no command can name (a trader's stock, a crafted product's preview).
    /// </summary>
    public Simulation.Items.ItemOwner? Owner { get; init; }

    /// <summary>
    /// The item's index among <see cref="Owner"/>'s items (<c>InventoryItemView.Index</c>); -1 without an owner. It names the item only until the inventory changes, so a command checks the record (and tag) it expects there.
    /// </summary>
    public int Index { get; init; } = -1;

    /// <summary>
    /// Whether this is the first build of the tooltip for this item as it is now: true when the tooltip last showed another item (or the item
    /// changed, its tag included), false while the same tooltip is built again each frame it shows.
    /// </summary>
    public bool First { get; init; }

    /// <summary>
    /// The tick source and command queue (none in a context made without them: <see cref="Tick"/> is 0 and <see cref="Enqueue"/> does nothing).
    /// </summary>
    internal (Func<long> Tick, Action<Simulation.SimCommand> Enqueue)? World { get; init; }

    /// <summary>
    /// The world's tick now, for a command's <see cref="Simulation.SimCommand.Tick"/>.
    /// </summary>
    public long Tick => World?.Tick() ?? 0;

    /// <summary>
    /// Queues a command for the world (the only way a source changes it, e.g. to tag an item the first time its tooltip shows).
    /// </summary>
    public void Enqueue(Simulation.SimCommand command) => World?.Enqueue(command);
}

/// <summary>
/// The extensions' item tooltip line sources (docs/modding.md "Item tooltip lines"), asked whenever an item's tooltip is built.
/// </summary>
sealed class ItemTooltipLines
{
    /// <summary>
    /// The most lines the extensions together add to one tooltip.
    /// </summary>
    public const int MaxLines = 8;

    /// <summary>
    /// The most characters a line's label or value keeps.
    /// </summary>
    public const int MaxText = 80;

    /// <summary>
    /// The sources, with their owners, in registration order.
    /// </summary>
    readonly List<(string Owner, Func<UiItemTooltipContext, IEnumerable<UiItemTooltipLine>> Lines)> _sources = [];

    /// <summary>
    /// Whether any extension adds lines.
    /// </summary>
    public bool Any => _sources.Count > 0;

    /// <summary>
    /// Adds <paramref name="owner"/>'s source.
    /// </summary>
    public void Add(string owner, Func<UiItemTooltipContext, IEnumerable<UiItemTooltipLine>> lines) => _sources.Add((owner, lines));

    /// <summary>
    /// Removes <paramref name="owner"/>'s sources.
    /// </summary>
    public void Remove(string owner) => _sources.RemoveAll(s => s.Owner == owner);

    /// <summary>
    /// The lines for <paramref name="item"/>, in registration order, at most <see cref="MaxLines"/> of all sources together (a copy of the sources is
    /// walked). Labels and values are put on one line and cut to <see cref="MaxText"/>; a blank value is none. A source that throws, or names a line
    /// without a label, is reported once through <paramref name="report"/> and dropped.
    /// </summary>
    public IReadOnlyList<UiItemTooltipLine> For(UiItemTooltipContext item, Action<UiDiagnostic>? report)
    {
        var lines = new List<UiItemTooltipLine>();
        foreach (var source in _sources.ToArray())
        {
            if (lines.Count >= MaxLines) break;

            try
            {
                var added = source.Lines(item)
                    .Take(MaxLines - lines.Count)
                    .ToList();
                if (added.Any(l => l is null || string.IsNullOrWhiteSpace(l.Label)))
                    throw new InvalidOperationException("An item tooltip line needs a label.");

                lines.AddRange(added.Select(Shown));
            }
            catch (Exception e)
            {
                _sources.Remove(source);
                report?.Invoke(new UiDiagnostic(source.Owner, "item tooltip lines (dropped)", e));
            }
        }

        return lines;
    }

    /// <summary>
    /// A line as the tooltip shows it: label and value on one line and cut, a blank value none.
    /// </summary>
    static UiItemTooltipLine Shown(UiItemTooltipLine line) =>
        new(Text(line.Label), string.IsNullOrWhiteSpace(line.Value) ? null : Text(line.Value))
        {
            Style = UiLineStyle.Shown(line.Style),
            After = string.IsNullOrWhiteSpace(line.After) ? null : line.After.Trim(),
        };

    /// <summary>
    /// <paramref name="text"/> cut to <see cref="MaxText"/> characters and put on one line.
    /// </summary>
    static string Text(string text)
    {
        string cut = text.Length > MaxText ? text[..MaxText] : text;
        return cut.ReplaceLineEndings(" ").Trim();
    }
}
