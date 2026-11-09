using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace Meitou.Launcher;

/// <summary>
/// Dragging a mod row's handle moves the row live under the pointer; the list holds the pointer capture, so rows can be
/// re-created underneath without ending the drag.
/// </summary>
sealed class ModListDrag(ListBox list, ObservableCollection<ModRow> mods)
{
    /// <summary>
    /// The row being dragged, null between drags.
    /// </summary>
    ModRow? _dragged;

    /// <summary>
    /// A press on a row's handle starts dragging that row, selected, with the list holding the pointer.
    /// </summary>
    public void Press(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: ModRow row } || !e.GetCurrentPoint(list).Properties.IsLeftButtonPressed)
            return;
        _dragged = row;
        list.SelectedItem = row;
        e.Pointer.Capture(list);
        MarkDragged(true);
        e.Handled = true;
    }

    /// <summary>
    /// Moves the dragged row to the row under the pointer.
    /// </summary>
    public void Moved(object? sender, PointerEventArgs e)
    {
        if (_dragged is null) return;
        double y = e.GetPosition(list).Y;
        int from = mods.IndexOf(_dragged), to = from;
        for (int i = 0; i < mods.Count; i++)
        {
            if (list.ContainerFromIndex(i) is not Control item || item.TranslatePoint(new Point(0, 0), list) is not { } top)
                continue;

            if (y >= top.Y && y < top.Y + item.Bounds.Height)
            {
                to = i;
                break;
            }
        }
        if (to == from) return;
        MarkDragged(false);
        mods.Move(from, to);
        list.SelectedItem = _dragged;
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
    /// Ends the drag, if one runs: the row loses its dragging look.
    /// </summary>
    public void End()
    {
        if (_dragged is null) return;
        MarkDragged(false);
        _dragged = null;
    }

    /// <summary>
    /// Sets or clears the dragged row's <c>dragging</c> class.
    /// </summary>
    void MarkDragged(bool on)
    {
        if (_dragged is not null && list.ContainerFromItem(_dragged) is { } item)
            item.Classes.Set("dragging", on);
    }
}
