using System.Numerics;
using Meitou.Game.Host.Input;

namespace Meitou.Game.Settings;

/// <summary>
/// The Look and Quality rows above the Graphics tabs, and the dialog that names a new look preset (Save as).
/// </summary>
sealed class LookBar(SettingsMenu menu)
{
    internal void ShowcaseOpenLooks() => menu.OpenChoice(menu.LookPresets.LookChoice, menu.Canvas.R(474, 99, 200, 28));

    internal void ShowcaseNaming() => BeginNaming();

    #region Naming

    internal bool Naming;
    string _nameText = "";

    internal bool NameKey(Key key)
    {
        if (key == Key.Escape) Naming = false;
        else if (key == Key.Enter) FinishNaming();
        else if (key == Key.Backspace && _nameText.Length > 0) _nameText = _nameText[..^1];
        return true;
    }

    internal void NameText(char value)
    {
        if (char.IsControl(value)) return;
        if (_nameText.Length >= global::Meitou.Settings.LookPresetFile.MaxNameLength) return;
        if (Path.GetInvalidFileNameChars().Contains(value)) return;

        _nameText += value;
    }

    internal void DrawNameDialog()
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
        menu.Canvas.Button("Cancel", menu.Canvas.R(500, 418, 138, 32), () => Naming = false);
        menu.Canvas.Button("Save preset", menu.Canvas.R(650, 418, 154, 32), FinishNaming, primary: true);
    }

    void BeginNaming()
    {
        Naming = true;
        _nameText = menu.LookPresets.FreeName("My look");
        menu.ActiveChoice = null;
        menu.Focus = -1;
    }

    void FinishNaming()
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

        Naming = false;
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

    #endregion

    #region Look bar

    /// <summary>
    /// The Look and Quality rows above the Graphics tabs.
    /// </summary>
    internal void DrawLookBar()
    {
        var keep = menu.Viewport;
        menu.Viewport = menu.Canvas.R(212, 96, 888, 72);
        menu.Tiers.SyncTier();
        DrawLookRow();
        DrawQualityRow();
        menu.Viewport = keep;
    }

    /// <summary>
    /// The Look row: Faithful with its count of changes from the game, or the named look with its save, delete, share and paste buttons.
    /// </summary>
    void DrawLookRow()
    {
        bool faithful = menu.LookPresets.FaithfulLook;
        menu.Canvas.Text("Look", menu.Canvas.R(212, 98, 70, 30), 15, SettingsCanvas.LightInk);
        menu.Canvas.Segmented(menu.Canvas.R(286, 101, 172, 24), faithful ? 0 : 1, i => menu.LookPresets.SetFaithful(i == 0));
        if (faithful)
        {
            DrawFaithfulState();
            return;
        }

        DrawNamedLook();
    }

    /// <summary>
    /// Faithful is a starting point, not a lock: every option stays free, and what differs from the game is counted and can be reverted.
    /// </summary>
    void DrawFaithfulState()
    {
        int changes = menu.LookPresets.LookChanges().Count;
        if (changes > 0)
        {
            string count = changes == 1 ? "1 change from the game" : $"{changes} changes from the game";
            menu.Canvas.Text(count, menu.Canvas.R(474, 98, 150, 30), 12, SettingsCanvas.Brass);
            menu.Canvas.Button("Revert", menu.Canvas.R(628, 100, 66, 26), () => menu.LookPresets.SetFaithful(true));
        }
        else
        {
            menu.Canvas.Text("As the game ships", menu.Canvas.R(474, 98, 200, 30), 12, SettingsCanvas.MutedInk);
        }
    }

    /// <summary>
    /// The active look's drop-down and beside it its unsaved changes with Revert, or where the look comes from.
    /// </summary>
    void DrawNamedLook()
    {
        var look = menu.LookPresets.ActiveLook;
        var at = menu.Canvas.R(474, 99, 200, 28);
        menu.Canvas.DropDown(look.Name, at, () => menu.OpenChoice(menu.LookPresets.LookChoice, at));
        int changes = menu.LookPresets.LookChanges().Count;
        bool mine = look.Source == "mine";
        if (changes > 0)
        {
            string count = changes == 1 ? "1 unsaved change" : $"{changes} unsaved changes";
            menu.Canvas.Text(count, menu.Canvas.R(684, 98, 120, 30), 12, SettingsCanvas.Brass);
            menu.Canvas.Button("Revert", menu.Canvas.R(800, 100, 66, 26), () =>
            {
                menu.LookPresets.ApplyLook(look);
                menu.Notice = $"Back to {look.Name}.";
            });
        }
        else
        {
            string source = look.Source == "built in" ? "Built in" : look.Source == "example" ? "Example" : $"Mine · Meitou {look.Made}";
            menu.Canvas.Text(source, menu.Canvas.R(684, 98, 120, 30), 12, SettingsCanvas.MutedInk);
        }

        const float x = 872;
        if (mine && changes > 0)
        {
            menu.Canvas.Button("Save", menu.Canvas.R(x - 64, 100, 58, 26), () =>
            {
                if (menu.LookPresets.SaveLook(look.Name) is not null) menu.Notice = $"{look.Name} saved.";
            });
        }
        else if (mine)
        {
            menu.Canvas.Button("Delete", menu.Canvas.R(x - 70, 100, 64, 26), () =>
            {
                menu.PendingAction = () =>
                {
                    menu.LookPresets.DeleteLook(look);
                    return $"{look.Name} deleted; the look is Meitou.";
                };
                menu.ConfirmTitle = "Delete preset?";
                menu.Confirmation = $"Delete {look.Name}? Its file goes; the look goes back to Meitou.";
                menu.Focus = -1;
            });
        }

        menu.Canvas.Button("Save as", menu.Canvas.R(x, 100, 72, 26), BeginNaming);
        menu.Canvas.Button("Share", menu.Canvas.R(x + 78, 100, 70, 26), () =>
        {
            menu.Player.ClipboardWrite?.Invoke(LookPresets.ShareCode(look));
            menu.Notice = $"{look.Name}'s code is on the clipboard: paste it on Discord, or in Paste.";
        });
        menu.Canvas.Button("Paste", menu.Canvas.R(x + 154, 100, 74, 26), menu.LookPresets.PasteLook);
    }

    /// <summary>
    /// The Quality drop-down and, beside it, what the Low preset holds off.
    /// </summary>
    void DrawQualityRow()
    {
        menu.Canvas.Text("Quality", menu.Canvas.R(212, 134, 70, 30), 15, SettingsCanvas.LightInk);
        var q = menu.Canvas.R(286, 136, 172, 28);
        menu.Canvas.DropDown(QualityTiers.PresetNames[menu.Tiers.CurrentPreset()], q, () => menu.OpenChoice(menu.LookPresets.QualityChoice, q));
        string cut = menu.LookPresets.LowCutList();
        if (cut.Length > 0) menu.Canvas.Text(Loc.Tr("Low holds off: {1}.", cut), menu.Canvas.R(474, 134, 626, 30), 12, SettingsCanvas.Brass);
    }

    #endregion
}
