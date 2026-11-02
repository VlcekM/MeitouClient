namespace Meitou.Data.Ui;

public sealed partial class UiBindings
{
    /// <summary>
    /// Whether <paramref name="other"/> binds the same data, so that <see cref="MyGuiPresentation.Arrange"/> makes the same controls from it (a host reuses the last arrangement then).
    /// Every member counts; a member added to the bindings must be compared here too. <see cref="TextHeight"/> is compared by identity.
    /// </summary>
    public bool ContentEquals(UiBindings other) =>
        TemplateScale == other.TemplateScale && OpenCombo == other.OpenCombo && TextHeight == other.TextHeight
        && Same(Text, other.Text) && Same(Actions, other.Actions) && Hidden.SetEquals(other.Hidden) && Disabled.SetEquals(other.Disabled)
        && HiddenWidgets.SetEquals(other.HiddenWidgets) && Same(Positions, other.Positions) && Same(RelativePositions, other.RelativePositions)
        && Same(SelectedTabs, other.SelectedTabs) && Same(Progress, other.Progress) && Same(States, other.States)
        && Same(SelectedItems, other.SelectedItems) && Same(ScrollOffsets, other.ScrollOffsets) && Same(Sorts, other.Sorts) && Same(Canvas, other.Canvas)
        && NativeScroll.SetEquals(other.NativeScroll)
        && Same(TabCaptions, other.TabCaptions, (a, b) => a.SequenceEqual(b))
        && Same(Columns, other.Columns, (a, b) => a.SequenceEqual(b))
        && Same(Properties, other.Properties, (a, b) => a.Count == b.Count && a.All(p => b.TryGetValue(p.Key, out var v) && v == p.Value))
        && Same(Lists, other.Lists, (a, b) => a.Count == b.Count && a.Zip(b).All(p => SameItem(p.First, p.Second)));

    static bool SameItem(UiListItem a, UiListItem b) => a.Id == b.Id && a.Caption == b.Caption
        && (a.Cells == b.Cells || a.Cells is not null && b.Cells is not null && a.Cells.SequenceEqual(b.Cells))
        && (a.SortKeys == b.SortKeys || a.SortKeys is not null && b.SortKeys is not null && a.SortKeys.SequenceEqual(b.SortKeys));

    static bool Same<TKey, TValue>(Dictionary<TKey, TValue> a, Dictionary<TKey, TValue> b) where TKey : notnull =>
        Same(a, b, EqualityComparer<TValue>.Default.Equals);

    static bool Same<TKey, TValue>(Dictionary<TKey, TValue> a, Dictionary<TKey, TValue> b, Func<TValue, TValue, bool> equal) where TKey : notnull
    {
        if (a.Count != b.Count) return false;
        foreach (var (key, value) in a)
            if (!b.TryGetValue(key, out var theirs) || !equal(value, theirs)) return false;
        return true;
    }
}
