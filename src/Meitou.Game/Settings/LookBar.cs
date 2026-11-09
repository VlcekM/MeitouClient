namespace Meitou.Game.Settings;

/// <summary>
/// The Look and Quality rows above the Graphics tabs; Save as opens the <see cref="PresetNamingDialog"/>.
/// </summary>
sealed class LookBar(SettingsMenu menu)
{
    /// <summary>
    /// For the screenshot scenes: the look drop-down opened.
    /// </summary>
    internal void ShowcaseOpenLooks() => menu.OpenChoice(menu.LookPresets.LookChoice, menu.Canvas.R(474, 99, 200, 28));

    /// <summary>
    /// For the screenshot scenes: the naming dialog opened.
    /// </summary>
    internal void ShowcaseNaming() => menu.PresetNaming.Begin();

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

        menu.Canvas.Button("Save as", menu.Canvas.R(x, 100, 72, 26), menu.PresetNaming.Begin);
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
}
