namespace Meitou.Game.Ui;

/// <summary>
/// What an extension's item name source gets (<see cref="UiRegistration.AddItemNames"/>): the item's record string id, the name it would show
/// (the game's, or an earlier source's), and its code-mod tag (<c>ItemInstance.ModTag</c>, "" none).
/// </summary>
public sealed record UiItemNameContext(string Record, string Name, string ModTag);

/// <summary>
/// The extensions' item name sources (docs/modding.md "Item names"), asked when the interface shows an item's name. Display only: the item, its
/// record and the simulation keep their names.
/// </summary>
sealed class ItemNames
{
    /// <summary>
    /// The most characters a shown name keeps.
    /// </summary>
    public const int MaxText = 80;

    /// <summary>
    /// The most answers kept; past it the cache starts over (item tags can make the set of names open-ended).
    /// </summary>
    const int MaxCached = 4096;

    /// <summary>
    /// The sources, with their owners, in registration order.
    /// </summary>
    readonly List<(string Owner, Func<UiItemNameContext, string?> Names)> _sources = [];

    /// <summary>
    /// The names shown so far, by record, name and tag; emptied when the sources change or one asks (<see cref="Refresh"/>).
    /// </summary>
    readonly Dictionary<(string Record, string Name, string ModTag), string> _shown = [];

    /// <summary>
    /// Whether any extension renames items.
    /// </summary>
    public bool Any => _sources.Count > 0;

    /// <summary>
    /// Adds <paramref name="owner"/>'s source after the others.
    /// </summary>
    public void Add(string owner, Func<UiItemNameContext, string?> names)
    {
        _sources.Add((owner, names));
        _shown.Clear();
    }

    /// <summary>
    /// Removes <paramref name="owner"/>'s sources.
    /// </summary>
    public void Remove(string owner)
    {
        if (_sources.RemoveAll(s => s.Owner == owner) > 0) _shown.Clear();
    }

    /// <summary>
    /// Forgets the names shown so far, so every source is asked again (a source whose answers changed).
    /// </summary>
    public void Refresh() => _shown.Clear();

    /// <summary>
    /// The name shown for an item of <paramref name="record"/> named <paramref name="name"/> with tag <paramref name="modTag"/>: each source in
    /// registration order gets the name so far and may replace it (null or blank: keep it); the result is put on one line and cut to
    /// <see cref="MaxText"/>, and kept until <see cref="Refresh"/>. A source that throws is reported once through <paramref name="report"/> and
    /// dropped. With no source, <paramref name="name"/> as it is.
    /// </summary>
    public string Shown(string record, string name, string modTag, Action<UiDiagnostic>? report)
    {
        if (_sources.Count == 0) return name;

        var key = (record, name, modTag);
        if (_shown.TryGetValue(key, out var known)) return known;

        string shown = name;
        foreach (var source in _sources.ToArray())
        {
            try
            {
                if (source.Names(new UiItemNameContext(record, shown, modTag)) is { } named && !string.IsNullOrWhiteSpace(named))
                    shown = Text(named);
            }
            catch (Exception e)
            {
                _sources.Remove(source);
                report?.Invoke(new UiDiagnostic(source.Owner, "item names (dropped)", e));
            }
        }

        if (_shown.Count >= MaxCached) _shown.Clear();
        _shown[key] = shown;
        return shown;
    }

    /// <summary>
    /// <paramref name="text"/> put on one line and cut to <see cref="MaxText"/> characters.
    /// </summary>
    static string Text(string text)
    {
        string line = text.ReplaceLineEndings(" ").Trim();
        return line.Length > MaxText ? line[..MaxText] : line;
    }
}
