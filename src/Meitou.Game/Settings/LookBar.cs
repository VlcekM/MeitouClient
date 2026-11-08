using System.Numerics;
using Meitou.Game.Host.Input;

namespace Meitou.Game.Settings;

/// <summary>The Look and Quality rows above the Graphics tabs, and the dialog that names a new look preset (Save as).</summary>
sealed class LookBar(SettingsMenu menu)
{
    internal void ShowcaseOpenLooks() => menu.OpenChoice(menu.lookPresets.LookChoice, menu.canvas.R(474, 99, 200, 28));
    internal void ShowcaseNaming() => BeginNaming();

    // ---- naming a preset (Save as) ----

    internal bool naming;
    string nameText = "";

    void BeginNaming() { naming = true; nameText = menu.lookPresets.FreeName("My look"); menu.openChoice = null; menu.focus = -1; }

    void FinishNaming()
    {
        string name = nameText.Trim();
        if (name.Length == 0) { menu.notice = "Type a name, or press Esc to cancel."; return; }
        if (name.Equals(LookPresets.MeitouLook.Name, StringComparison.OrdinalIgnoreCase) || LookPresets.ExampleLooks.Any(l => l.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            menu.notice = $"{name} is a built-in preset: choose another name.";
            return;
        }
        naming = false;
        if (menu.lookPresets.SaveLook(name) is { } saved) menu.notice = $"Saved as {saved.Name}.";
    }

    internal bool NameKey(Key key)
    {
        if (key == Key.Escape) naming = false;
        else if (key == Key.Enter) FinishNaming();
        else if (key == Key.Backspace && nameText.Length > 0) nameText = nameText[..^1];
        return true;
    }

    internal void NameText(char value)
    {
        if (!char.IsControl(value) && nameText.Length < global::Meitou.Settings.LookPresetFile.MaxNameLength && !Path.GetInvalidFileNameChars().Contains(value)) nameText += value;
    }

    internal void DrawNameDialog()
    {
        menu.hits.Clear();
        menu.canvas.Rect(menu.bounds, new(8 / 255f, 6 / 255f, 5 / 255f, 0.8f));
        menu.canvas.Surface(menu.canvas.R(290, 230, 540, 236), "window");
        menu.canvas.Text("Save as a new preset", menu.canvas.R(316, 244, 488, 30), 20, SettingsCanvas.LightInk);
        menu.canvas.Text("Name", menu.canvas.R(316, 282, 488, 18), 12, SettingsCanvas.MainInk);
        var box = menu.canvas.R(316, 302, 488, 32);
        menu.canvas.Rect(box, new(0.06f, 0.05f, 0.04f, 0.95f));
        menu.canvas.Rect(box with { Y = box.Y + box.Height - menu.scale, Height = menu.scale }, SettingsCanvas.Brass with { W = 0.6f });
        var editAt = box with { X = box.X + 8 * menu.scale, Width = box.Width - 16 * menu.scale };
        menu.controls.Add(new("settings-name", "EditBox", editAt, nameText, "", "", true,
            new Dictionary<string, string>
            {
                ["Focused"] = "true", ["TextAlign"] = "Left VCenter",
                ["FontPixels"] = ((int)MathF.Round(15 * menu.scale)).ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["CaretIndex"] = nameText.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), ["SelectionStart"] = "0", ["SelectionLength"] = "0",
            }, Vector4.Zero, SettingsCanvas.LightInk, Pickable: false));
        int changes = menu.lookPresets.LookChanges().Count;
        menu.canvas.Text($"Your look as it is now: {menu.lookPresets.ActiveLook.Name}{(changes > 0 ? $" with {changes} change{(changes == 1 ? "" : "s")}" : "")}. What it does not set keeps following Meitou.",
            menu.canvas.R(316, 342, 488, 40), 12, SettingsCanvas.MutedInk, "Left Top", wrap: true);
        menu.canvas.Button("Cancel", menu.canvas.R(500, 418, 138, 32), () => naming = false);
        menu.canvas.Button("Save preset", menu.canvas.R(650, 418, 154, 32), FinishNaming, primary: true);
    }

    // ---- the Look and Quality rows ----

    /// <summary>The Look and Quality rows above the Graphics tabs.</summary>
    internal void DrawLookBar()
    {
        var keep = menu.viewport;
        menu.viewport = menu.canvas.R(212, 96, 888, 72);
        menu.tiers.SyncTier();
        bool faithful = menu.lookPresets.FaithfulLook;
        menu.canvas.Text("Look", menu.canvas.R(212, 98, 70, 30), 15, SettingsCanvas.LightInk);
        menu.canvas.Segmented(menu.canvas.R(286, 101, 172, 24), faithful ? 0 : 1, i => menu.lookPresets.SetFaithful(i == 0));
        if (faithful)
        {
            // Faithful is a starting point, not a lock: every option stays free, and what differs from the game is counted and can be reverted.
            int changes = menu.lookPresets.LookChanges().Count;
            if (changes > 0)
            {
                string count = changes == 1 ? "1 change from the game" : $"{changes} changes from the game";
                menu.canvas.Text(count, menu.canvas.R(474, 98, 150, 30), 12, SettingsCanvas.Brass);
                menu.canvas.Button("Revert", menu.canvas.R(628, 100, 66, 26), () => menu.lookPresets.SetFaithful(true));
            }
            else menu.canvas.Text("As the game ships", menu.canvas.R(474, 98, 200, 30), 12, SettingsCanvas.MutedInk);
        }
        else
        {
            var look = menu.lookPresets.ActiveLook;
            var at = menu.canvas.R(474, 99, 200, 28);
            menu.canvas.DropDown(look.Name, at, () => menu.OpenChoice(menu.lookPresets.LookChoice, at));
            int changes = menu.lookPresets.LookChanges().Count;
            bool mine = look.Source == "mine";
            if (changes > 0)
            {
                string count = changes == 1 ? "1 unsaved change" : $"{changes} unsaved changes";
                menu.canvas.Text(count, menu.canvas.R(684, 98, 120, 30), 12, SettingsCanvas.Brass);
                menu.canvas.Button("Revert", menu.canvas.R(800, 100, 66, 26), () =>
                {
                    menu.lookPresets.ApplyLook(look);
                    menu.notice = $"Back to {look.Name}.";
                });
            }
            else menu.canvas.Text(look.Source == "built in" ? "Built in" : look.Source == "example" ? "Example" : $"Mine · Meitou {look.Made}", menu.canvas.R(684, 98, 120, 30), 12, SettingsCanvas.MutedInk);
            const float x = 872;
            if (mine && changes > 0) menu.canvas.Button("Save", menu.canvas.R(x - 64, 100, 58, 26), () => { if (menu.lookPresets.SaveLook(look.Name) is not null) menu.notice = $"{look.Name} saved."; });
            else if (mine)
                menu.canvas.Button("Delete", menu.canvas.R(x - 70, 100, 64, 26), () =>
                {
                    menu.pendingAction = () => { menu.lookPresets.DeleteLook(look); return $"{look.Name} deleted; the look is Meitou."; };
                    menu.confirmTitle = "Delete preset?";
                    menu.confirmation = $"Delete {look.Name}? Its file goes; the look goes back to Meitou.";
                    menu.focus = -1;
                });
            menu.canvas.Button("Save as", menu.canvas.R(x, 100, 72, 26), BeginNaming);
            menu.canvas.Button("Share", menu.canvas.R(x + 78, 100, 70, 26), () => { menu.player.ClipboardWrite?.Invoke(LookPresets.ShareCode(look)); menu.notice = $"{look.Name}'s code is on the clipboard: paste it on Discord, or in Paste."; });
            menu.canvas.Button("Paste", menu.canvas.R(x + 154, 100, 74, 26), menu.lookPresets.PasteLook);
        }
        menu.canvas.Text("Quality", menu.canvas.R(212, 134, 70, 30), 15, SettingsCanvas.LightInk);
        var q = menu.canvas.R(286, 136, 172, 28);
        menu.canvas.DropDown(QualityTiers.PresetNames[menu.tiers.CurrentPreset()], q, () => menu.OpenChoice(menu.lookPresets.QualityChoice, q));
        string cut = menu.lookPresets.LowCutList();
        if (cut.Length > 0) menu.canvas.Text(Loc.Tr("Low holds off: {1}.", cut), menu.canvas.R(474, 134, 626, 30), 12, SettingsCanvas.Brass);
        menu.viewport = keep;
    }
}
