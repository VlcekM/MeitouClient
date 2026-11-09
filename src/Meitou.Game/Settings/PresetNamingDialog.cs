using System.Numerics;
using Meitou.Game.Host.Input;

namespace Meitou.Game.Settings;

/// <summary>
/// The dialog that names a new look preset (the look bar's Save as).
/// </summary>
sealed class PresetNamingDialog(SettingsMenu menu)
{
    /// <summary>
    /// Whether the dialog is open; it takes the keys and the text typed while the menu shows.
    /// </summary>
    internal bool Open;

    /// <summary>
    /// The name typed.
    /// </summary>
    string _nameText = "";

    /// <summary>
    /// A key while the dialog is open: Esc closes it, Enter saves, Backspace drops the last character; every key is taken.
    /// </summary>
    internal bool OnKey(Key key)
    {
        if (key == Key.Escape) Open = false;
        else if (key == Key.Enter) Finish();
        else if (key == Key.Backspace && _nameText.Length > 0) _nameText = _nameText[..^1];
        return true;
    }

    /// <summary>
    /// A character typed: added unless it is a control character, the name is full, or a file name cannot hold it.
    /// </summary>
    internal void OnText(char value)
    {
        if (char.IsControl(value)) return;
        if (_nameText.Length >= global::Meitou.Settings.LookPresetFile.MaxNameLength) return;
        if (Path.GetInvalidFileNameChars().Contains(value)) return;

        _nameText += value;
    }

    /// <summary>
    /// The dialog over the menu: the name box, what the look holds, Cancel and Save preset.
    /// </summary>
    internal void Draw()
    {
        menu.Hits.Clear();
        menu.Canvas.Rect(menu.Bounds, new(8 / 255f, 6 / 255f, 5 / 255f, 0.8f));
        menu.Canvas.Surface(menu.Canvas.R(290, 230, 540, 236), "window");
        menu.Canvas.Text("Save as a new preset", menu.Canvas.R(316, 244, 488, 30), 20, SettingsCanvas.LightInk);
        menu.Canvas.Text("Name", menu.Canvas.R(316, 282, 488, 18), 12, SettingsCanvas.MainInk);
        var box = menu.Canvas.R(316, 302, 488, 32);
        menu.Canvas.Rect(box, new(0.06f, 0.05f, 0.04f, 0.95f));
        menu.Canvas.Rect(box with { Y = box.Y + box.Height - menu.Scale, Height = menu.Scale }, SettingsCanvas.Brass with { W = 0.6f });
        var editAt = box with { X = box.X + 8 * menu.Scale, Width = box.Width - 16 * menu.Scale };
        menu.Controls.Add(new("settings-name", "EditBox", editAt, _nameText, "", "", true,
            new Dictionary<string, string>
            {
                ["Focused"] = "true",
                ["TextAlign"] = "Left VCenter",
                ["FontPixels"] = ((int)MathF.Round(15 * menu.Scale)).ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["CaretIndex"] = _nameText.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["SelectionStart"] = "0",
                ["SelectionLength"] = "0",
            }, Vector4.Zero, SettingsCanvas.LightInk, Pickable: false));
        menu.Canvas.Text(LookSummary(), menu.Canvas.R(316, 342, 488, 40), 12, SettingsCanvas.MutedInk, "Left Top", wrap: true);
        menu.Canvas.Button("Cancel", menu.Canvas.R(500, 418, 138, 32), () => Open = false);
        menu.Canvas.Button("Save preset", menu.Canvas.R(650, 418, 154, 32), Finish, primary: true);
    }

    /// <summary>
    /// Opens the dialog with a free name, closing any drop-down and dropping the focus.
    /// </summary>
    internal void Begin()
    {
        Open = true;
        _nameText = menu.LookPresets.FreeName("My look");
        menu.ActiveChoice = null;
        menu.Focus = -1;
    }

    /// <summary>
    /// Saves the look under the name typed, unless it is empty or a built-in preset's.
    /// </summary>
    void Finish()
    {
        string name = _nameText.Trim();
        if (name.Length == 0)
        {
            menu.Notice = "Type a name, or press Esc to cancel.";
            return;
        }

        if (IsBuiltInName(name))
        {
            menu.Notice = $"{name} is a built-in preset: choose another name.";
            return;
        }

        Open = false;
        if (menu.LookPresets.SaveLook(name) is { } saved) menu.Notice = $"Saved as {saved.Name}.";
    }

    /// <summary>
    /// Whether a name is Meitou's look or one of the example looks (case ignored).
    /// </summary>
    static bool IsBuiltInName(string name) =>
        name.Equals(LookPresets.MeitouLook.Name, StringComparison.OrdinalIgnoreCase)
        || LookPresets.ExampleLooks.Any(l => l.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The line naming the active look and how many of its settings were changed.
    /// </summary>
    string LookSummary()
    {
        int changes = menu.LookPresets.LookChanges().Count;
        string suffix = changes > 0 ? $" with {changes} change{(changes == 1 ? "" : "s")}" : "";
        return $"Your look as it is now: {menu.LookPresets.ActiveLook.Name}{suffix}. What it does not set keeps following Meitou.";
    }
}
