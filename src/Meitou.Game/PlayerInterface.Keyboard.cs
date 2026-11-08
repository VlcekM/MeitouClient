using Meitou.Game.Ui;
using EngineKey = Meitou.Game.Host.Input.Key;

namespace Meitou.Game;

/// <summary>Keys in the faithful windows' edit boxes (MyGUI 3.2.2 <c>EditBox</c>, docs/game/ui-input.md "Edit boxes") and their clipboard.</summary>
sealed partial class PlayerInterface
{
    /// <summary>The system clipboard's text and where copied text goes (the window's keyboard); null: the boxes keep a clipboard of their own.</summary>
    public Func<string?>? ClipboardRead { get; set; }
    public Action<string>? ClipboardWrite { get; set; }

    /// <summary>An edit box has the keyboard: Ctrl+C and Ctrl+V are its, not the free camera's.</summary>
    public bool UiEditing => UiVisible && faithfulScreens.HasKeyboardFocus;

    /// <summary>
    /// The edit command of a key (MyGUI's key table for Windows and Linux; Meitou uses Ctrl on every system): Ctrl+C / X / V / A / Z / Y, Ctrl+Insert copy, Shift+Insert paste, Shift+Delete cut,
    /// Ctrl+Home / End to the ends of the text. Shift is not looked at for Z, so Ctrl+Shift+Z undoes as in MyGUI.
    /// </summary>
    static UiEditKey? EditKeyFor(EngineKey key, bool ctrl, bool shift) => key switch
    {
        EngineKey.Left => UiEditKey.Left, EngineKey.Right => UiEditKey.Right, EngineKey.Up => UiEditKey.Up, EngineKey.Down => UiEditKey.Down,
        EngineKey.PageUp => UiEditKey.PageUp, EngineKey.PageDown => UiEditKey.PageDown,
        EngineKey.Home => ctrl ? UiEditKey.DocumentStart : UiEditKey.Home, EngineKey.End => ctrl ? UiEditKey.DocumentEnd : UiEditKey.End,
        EngineKey.Backspace => UiEditKey.Backspace, EngineKey.Delete => shift ? UiEditKey.Cut : UiEditKey.Delete,
        EngineKey.Insert => shift ? UiEditKey.Paste : ctrl ? UiEditKey.Copy : null,
        EngineKey.A when ctrl => UiEditKey.SelectAll, EngineKey.C when ctrl => UiEditKey.Copy, EngineKey.X when ctrl => UiEditKey.Cut, EngineKey.V when ctrl => UiEditKey.Paste,
        EngineKey.Z when ctrl => UiEditKey.Undo, EngineKey.Y when ctrl => UiEditKey.Redo, _ => null,
    };

    /// <summary>A key with an edit box focused: it takes every key, except that a read-only box takes only the edit keys, so Tab, Enter and the hotkeys go on as without a focus.</summary>
    bool EditFocusKey(EngineKey key, bool ctrl, bool shift)
    {
        var editing = EditKeyFor(key, ctrl, shift);
        if (editing is { } editKey) faithfulScreens.EditKey(editKey, shift);
        if (faithfulScreens.FocusReadOnly) return editing is not null;
        if (key == EngineKey.Tab) faithfulScreens.FocusNext(shift);
        if (key == EngineKey.Enter)
        {
            faithfulScreens.EditEnter(ctrl);
            if (faithfulScreens.Focus is { Screen: "overview", Widget: FactionWindow.FactionNameBox }) faithfulScreens.Blur();   // Enter accepts the faction name
            if (faithfulScreens.Focus is { Screen: TownPanel.TownScreen, Widget: TownPanel.TownNameBox }) faithfulScreens.Blur();   // and the outpost name (FUN_140722d60)
        }
        return true;
    }
}
