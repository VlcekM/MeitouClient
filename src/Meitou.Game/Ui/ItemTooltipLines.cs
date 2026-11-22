namespace Meitou.Game.Ui;

/// <summary>
/// A line an extension adds to an item's tooltip (<see cref="UiRegistration.AddItemTooltipLines"/>): <paramref name="Label"/> in the tooltip's label
/// column, <paramref name="Value"/> (null or blank: none) in its value column.
/// </summary>
public sealed record UiItemTooltipLine(string Label, string? Value = null);

/// <summary>
/// What an extension's item tooltip lines get: the item's record string id, its name, its code-mod tag (<c>ItemInstance.ModTag</c>, "" none; a
/// trader's stock item has none), its quantity, level and quality.
/// </summary>
public sealed record UiItemTooltipContext(string Record, string Name, string ModTag, int Quantity, int Level, float Quality);

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
        new(Text(line.Label), string.IsNullOrWhiteSpace(line.Value) ? null : Text(line.Value));

    /// <summary>
    /// <paramref name="text"/> cut to <see cref="MaxText"/> characters and put on one line.
    /// </summary>
    static string Text(string text)
    {
        string cut = text.Length > MaxText ? text[..MaxText] : text;
        return cut.ReplaceLineEndings(" ").Trim();
    }
}
