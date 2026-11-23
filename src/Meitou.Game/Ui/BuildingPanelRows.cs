namespace Meitou.Game.Ui;

/// <summary>
/// A bar an extension adds under a selected building's Condition (<see cref="UiRegistration.AddBuildingRows"/>): <paramref name="Name"/> on the bar,
/// <paramref name="Fraction"/> (0 to 1) how full it is, <paramref name="Value"/> in the number column ("" none) and <paramref name="Tooltip"/> (null
/// or blank: none).
/// </summary>
public sealed record UiBuildingRow(string Name, float Fraction, string Value = "", string? Tooltip = null);

/// <summary>
/// The extensions' building panel bar sources (docs/modding.md "Building panel bars"), asked whenever the selected building's bars are bound.
/// </summary>
sealed class BuildingPanelRows
{
    /// <summary>
    /// The most bars the extensions together add to one building.
    /// </summary>
    public const int MaxRows = 6;

    /// <summary>
    /// The most characters a bar's name, number or tooltip keeps.
    /// </summary>
    public const int MaxText = 80;

    /// <summary>
    /// The sources, with their owners, in registration order.
    /// </summary>
    readonly List<(string Owner, Func<UiBuildingContext, IEnumerable<UiBuildingRow>> Rows)> _sources = [];

    /// <summary>
    /// Whether any extension adds bars.
    /// </summary>
    public bool Any => _sources.Count > 0;

    /// <summary>
    /// Adds <paramref name="owner"/>'s source.
    /// </summary>
    public void Add(string owner, Func<UiBuildingContext, IEnumerable<UiBuildingRow>> rows) => _sources.Add((owner, rows));

    /// <summary>
    /// Removes <paramref name="owner"/>'s sources.
    /// </summary>
    public void Remove(string owner) => _sources.RemoveAll(s => s.Owner == owner);

    /// <summary>
    /// The bars for the building in <paramref name="context"/>, in registration order, at most <see cref="MaxRows"/> of all sources together (a copy
    /// of the sources is walked). Fractions are clamped to 0 to 1 (NaN is 0), texts put on one line and cut to <see cref="MaxText"/>. A source that
    /// throws, or names a bar without a name, is reported once through <paramref name="report"/> and dropped.
    /// </summary>
    public IReadOnlyList<UiBuildingRow> For(UiBuildingContext context, Action<UiDiagnostic>? report)
    {
        var rows = new List<UiBuildingRow>();
        foreach (var source in _sources.ToArray())
        {
            if (rows.Count >= MaxRows) break;

            try
            {
                var added = source.Rows(context)
                    .Take(MaxRows - rows.Count)
                    .ToList();
                if (added.Any(r => r is null || string.IsNullOrWhiteSpace(r.Name)))
                    throw new InvalidOperationException("A building panel bar needs a name.");

                rows.AddRange(added.Select(Shown));
            }
            catch (Exception e)
            {
                _sources.Remove(source);
                report?.Invoke(new UiDiagnostic(source.Owner, "building panel bars (dropped)", e));
            }
        }

        return rows;
    }

    /// <summary>
    /// A bar as the panel shows it: the fraction clamped, the texts on one line and cut, a blank tooltip none.
    /// </summary>
    static UiBuildingRow Shown(UiBuildingRow row)
    {
        float fraction = float.IsNaN(row.Fraction) ? 0 : Math.Clamp(row.Fraction, 0, 1);
        string? tooltip = string.IsNullOrWhiteSpace(row.Tooltip) ? null : Text(row.Tooltip);
        return new UiBuildingRow(Text(row.Name), fraction, Text(row.Value ?? ""), tooltip);
    }

    /// <summary>
    /// <paramref name="text"/> cut to <see cref="MaxText"/> characters and put on one line.
    /// </summary>
    static string Text(string text)
    {
        string cut = text.Length > MaxText ? text[..MaxText] : text;
        return cut.ReplaceLineEndings(" ").Trim();
    }
}
