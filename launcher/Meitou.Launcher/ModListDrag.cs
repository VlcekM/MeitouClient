using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace Meitou.Launcher;

/// <summary>
/// Moves the selected mod rows together: dragging a row's handle moves them live under the pointer, Alt+Up / Alt+Down one step. The
/// list holds the pointer capture, so rows can be re-created underneath without ending the drag.
/// </summary>
sealed class ModListDrag(ListBox list, ObservableCollection<ModRow> mods)
{
    #region Dragging

    /// <summary>
    /// The row whose handle is held, null between drags; the other selected rows move with it.
    /// </summary>
    ModRow? _dragged;

    /// <summary>
    /// A press on a row's handle starts dragging the selection, with the list holding the pointer. A handle outside the selection
    /// selects its row alone first.
    /// </summary>
    public void Press(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: ModRow row } || !e.GetCurrentPoint(list).Properties.IsLeftButtonPressed)
            return;

        if (list.SelectedItems?.Contains(row) != true) list.SelectedItem = row;
        _dragged = row;
        e.Pointer.Capture(list);
        MarkDragged(true);
        e.Handled = true;
    }

    /// <summary>
    /// Moves the selection so the held row sits on the row under the pointer.
    /// </summary>
    public void Moved(object? sender, PointerEventArgs e)
    {
        if (_dragged is null) return;

        int from = mods.IndexOf(_dragged);
        int to = RowAt(e.GetPosition(list).Y) ?? from;
        if (to == from) return;

        MarkDragged(false);
        MoveSelection(to - from);
        list.UpdateLayout(); // the next hit test needs the rows where they are now
        MarkDragged(true);
    }

    /// <summary>
    /// Releasing the button ends the drag and frees the pointer.
    /// </summary>
    public void Released(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragged is null) return;

        e.Pointer.Capture(null);
        End();
    }

    /// <summary>
    /// Ends the drag, if one runs: the rows lose their dragging look.
    /// </summary>
    public void End()
    {
        if (_dragged is null) return;

        MarkDragged(false);
        _dragged = null;
    }

    /// <summary>
    /// The index of the row whose container spans height <paramref name="y"/> in the list, null over none.
    /// </summary>
    int? RowAt(double y)
    {
        for (int i = 0; i < mods.Count; i++)
        {
            if (list.ContainerFromIndex(i) is not Control item || item.TranslatePoint(new Point(0, 0), list) is not { } top)
                continue;

            if (y >= top.Y && y < top.Y + item.Bounds.Height) return i;
        }
        return null;
    }

    /// <summary>
    /// Sets or clears the selected rows' <c>dragging</c> class.
    /// </summary>
    void MarkDragged(bool on)
    {
        foreach (var row in SelectedInOrder())
        {
            if (list.ContainerFromItem(row) is { } item) item.Classes.Set("dragging", on);
        }
    }

    #endregion

    #region Moving the selection

    /// <summary>
    /// Moves every selected row <paramref name="delta"/> places (negative is up), keeping the gaps between them; the move stops at the
    /// list's ends. The rows stay selected. Returns whether anything moved.
    /// </summary>
    public bool MoveSelection(int delta)
    {
        var rows = SelectedInOrder();
        if (rows.Count == 0) return false;

        delta = Math.Clamp(delta, -mods.IndexOf(rows[0]), mods.Count - 1 - mods.IndexOf(rows[^1]));
        if (delta == 0) return false;

        var primary = list.SelectedItem;
        Shift(rows, delta);
        Reselect(rows, primary);
        return true;
    }

    /// <summary>
    /// The selected rows in list order.
    /// </summary>
    List<ModRow> SelectedInOrder() =>
        [.. mods.Where(m => list.SelectedItems?.Contains(m) == true)];

    /// <summary>
    /// Moves each row <paramref name="delta"/> places; the leading row moves first, so no row passes another.
    /// </summary>
    void Shift(List<ModRow> rows, int delta)
    {
        IEnumerable<ModRow> order = delta < 0 ? rows : Enumerable.Reverse(rows);
        foreach (var row in order)
        {
            int i = mods.IndexOf(row);
            mods.Move(i, i + delta);
        }
    }

    /// <summary>
    /// Selects <paramref name="rows"/> again after a move dropped them, with <paramref name="primary"/> as the selected item.
    /// </summary>
    void Reselect(List<ModRow> rows, object? primary)
    {
        list.SelectedItem = primary;
        foreach (var row in rows)
        {
            if (row != primary) list.SelectedItems?.Add(row);
        }
    }

    #endregion
}
