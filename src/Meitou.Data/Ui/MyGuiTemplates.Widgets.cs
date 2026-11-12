namespace Meitou.Data.Ui;

/// <summary>
/// The run-time parts of templated widgets: a MultiListBox's column prototypes.
/// </summary>
public static partial class MyGuiTemplates
{
    /// <summary>
    /// A MultiListBox makes its columns at run time from the user strings <c>SkinButton</c> (header) and <c>SkinList</c> (sub-list): the expanded
    /// skins are kept as hidden prototype children (<c>ColumnPrototype</c> button / list) that <see cref="MyGuiPresentation"/> clones per bound
    /// column.
    /// </summary>
    static IEnumerable<MyGuiWidget> ColumnPrototypes(string owner, IReadOnlyDictionary<string, string> strings, MyGuiResources resources,
        HashSet<string> chain)
    {
        foreach (var (kind, key, type) in new[] { ("button", "SkinButton", "Button"), ("list", "SkinList", "ListBox") })
        {
            if (strings.TryGetValue(key, out string? skin) && skin.Length > 0)
            {
                yield return ExpandWidget(new(type, owner + "@column-" + kind, skin, "", "", default, false,
                    new Dictionary<string, string>(StringComparer.Ordinal) { ["Visible"] = "false", ["ColumnPrototype"] = kind },
                    new Dictionary<string, string>(StringComparer.Ordinal), []), resources, new(chain));
            }
        }
    }
}