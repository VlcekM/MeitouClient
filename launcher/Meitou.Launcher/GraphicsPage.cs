using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Threading;
using Meitou.Localisation;
using Meitou.Settings;

namespace Meitou.Launcher;

/// <summary>
/// The Graphics page: the settings the version Play starts describes in its settings-schema.json, written to the game's meitou.user.json
/// (GameSettings). Nothing about the settings is known here: renamed or new ones come with the release.
/// </summary>
public sealed partial class MainWindow
{
    #region Page

    /// <summary>
    /// The width of a row's gutter (the feature chevron's column), in pixels.
    /// </summary>
    const double GutterWidth = 22;

    /// <summary>
    /// The game's settings file as last loaded (<see cref="ShowGraphics"/> loads it again).
    /// </summary>
    GameSettings _game = GameSettings.Load();

    /// <summary>
    /// The game process Play started, null before; the page is locked while it has not exited.
    /// </summary>
    Process? _running;

    /// <summary>
    /// The schema version of the shown page, written with the settings.
    /// </summary>
    int _schemaVersion;

    /// <summary>
    /// The Graphics tab shown (a section's name).
    /// </summary>
    string _graphicsTab = "";

    /// <summary>
    /// Every setting the schema describes, by key (the look row counts and saves through them).
    /// </summary>
    Dictionary<string, SettingDescription> _settingByKey = [];

    /// <summary>
    /// The features the player opened (by title), kept while the page is shown again.
    /// </summary>
    readonly HashSet<string> _openGroups = [];

    /// <summary>
    /// The shown tab's rows: what marks each one changed, and what puts each control back to the saved value (after a feature's Reset).
    /// </summary>
    readonly List<Action> _rowMarks = [], _rowShows = [];

    /// <summary>
    /// A feature header's key to the settings folded under it: its row is marked, and resets, for them too.
    /// </summary>
    readonly Dictionary<string, List<SettingDescription>> _featureMembers = [];

    /// <summary>
    /// The schema of the version Play starts, and why there is none.
    /// </summary>
    (SettingsSchema? Schema, string? Problem) PlaySchema()
    {
        if (PlayVersion is not { } version)
            return (null, "Install a version first (Versions): its settings are shown here.");
        if (Releases.FindGame(Releases.VersionDirectory(version)) is not { } exe)
            return (null, $"Meitou {version} is not installed any more.");
        var path = Path.Combine(Path.GetDirectoryName(exe)!, SettingsSchema.FileName);
        if (SettingsSchema.Read(path) is { } schema) return (schema, null);
        return (null, File.Exists(path)
            ? $"Meitou {version} describes its settings in a newer way than this launcher reads: update the launcher (Settings)."
            : $"Meitou {version} is older than the Graphics page: change its settings in the game's Tab panel, or play a newer version.");
    }

    /// <summary>
    /// The Graphics page made again from the settings file and the schema of the version Play starts (a pending save written first).
    /// </summary>
    void ShowGraphics()
    {
        if (_graphicsSave?.IsEnabled == true) SaveGame();
        _game = GameSettings.Load();
        GraphicsPanel.Children.Clear();
        GraphicsFooter.Child = null;
        GraphicsFooter.IsVisible = false;
        ShowSettingsFile();
        var (schema, missing) = PlaySchema();
        if (schema is null)
        {
            GraphicsPanel.Children.Add(new TextBlock { Classes = { "desc" }, Text = missing });
            LauncherText.TranslateTree(GraphicsPanel, _originalUiText);
            return;
        }
        _schemaVersion = schema.SettingsVersion;
        string? locked = Locked(schema);
        ShowGraphicsNotes(locked);
        var rows = GraphicsRows(schema, locked);
        ShowGraphicsFooter(locked);
        GraphicsPanel.Children.Add(rows);
        LauncherText.TranslateTree(GraphicsPanel, _originalUiText);
        LauncherText.TranslateTree(GraphicsFooter, _originalUiText);
    }

    /// <summary>
    /// The settings file's name over the page (its path in the tooltip), and a note while the game has not written it yet.
    /// </summary>
    void ShowSettingsFile()
    {
        bool found = _game.Version is not null;
        GraphicsFileText.Text = found ? GameSettings.FileName : Tr("No {1} found", GameSettings.FileName);
        ToolTip.SetTip(GraphicsFileText, GameSettings.FilePath);
        if (!found)
        {
            GraphicsPanel.Children.Add(new TextBlock
            {
                Classes = { "desc" }, Margin = new(0, 0, 0, 8),
                Text = $"No {GameSettings.FileName} in {LauncherConfig.DataDirectory} yet: the game writes it when it first saves its settings "
                    + "(started with Play), and a change here creates it. Until then the game uses its defaults.",
            });
        }
    }

    /// <summary>
    /// The warnings over the settings: why they are <paramref name="locked"/>, the game running, launch arguments that override them.
    /// </summary>
    void ShowGraphicsNotes(string? locked)
    {
        var notes = new List<string>();
        if (locked is not null) notes.Add(locked);
        if (_running is { HasExited: false })
            notes.Add("The game is running: it saves its settings when it closes, over changes made here.");
        if (SplitArguments(_config.LaunchArguments).Contains("--low-end"))
            notes.Add("The launch arguments have --low-end: the game ignores the graphics sliders while it is set.");
        if (SplitArguments(_config.LaunchArguments).Contains("--benchmark-baseline"))
            notes.Add("The launch arguments have --benchmark-baseline: the game ignores the graphics sliders while it is set.");
        foreach (var note in notes)
        {
            var warning = new TextBlock
            {
                Classes = { "warn" }, Text = note, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new(0, 0, 0, 6),
            };
            GraphicsPanel.Children.Add(warning);
        }
    }

    /// <summary>
    /// The settings' rows (off while <paramref name="locked"/>): the look, the preset, then the sections' tabs and the chosen one's settings.
    /// </summary>
    StackPanel GraphicsRows(SettingsSchema schema, string? locked)
    {
        var rows = new StackPanel { IsEnabled = locked is null };
        _presets = schema.Presets is { Names.Count: > 0, Key.Length: > 0 } p ? p : null;
        _presetBox = null;
        _looks = schema.Looks is { Presets.Count: > 0, FaithfulKey.Length: > 0, PresetKey.Length: > 0 } l ? l : null;
        _settingByKey = schema.Sections.SelectMany(x => x.Settings).GroupBy(x => x.Key).ToDictionary(g => g.Key, g => g.First());
        if (_looks is not null) rows.Children.Add(LookRow(_looks));
        if (_presets is not null) rows.Children.Add(PresetRow(_presets));
        AddSectionTabs(rows, schema);
        return rows;
    }

    /// <summary>
    /// One tab per section (the game's pages); the chosen one stays while the page is shown again.
    /// </summary>
    void AddSectionTabs(StackPanel rows, SettingsSchema schema)
    {
        var sections = schema.Sections.Where(section => section.Settings.Any(Shown)).ToList();
        if (sections.Count > 0)
        {
            var tabs = new WrapPanel { Margin = new(0, 14, 0, 4) };
            var content = new StackPanel();
            var section = sections.FirstOrDefault(x => x.Name == _graphicsTab) ?? sections[0];
            foreach (var each in sections)
            {
                var tab = new ToggleButton { Content = each.Name, IsChecked = each == section, Margin = new(0, 0, 6, 6) };
                tab.Click += (_, _) =>
                {
                    _graphicsTab = each.Name;
                    foreach (var other in tabs.Children.OfType<ToggleButton>()) other.IsChecked = other == tab;
                    FillSection(content, each);
                };
                tabs.Children.Add(tab);
            }
            rows.Children.Add(tabs);
            rows.Children.Add(content);
            FillSection(content, section);
        }
    }

    /// <summary>
    /// The footer: Reset graphics, every graphics setting back to the game's defaults (off while <paramref name="locked"/>).
    /// </summary>
    void ShowGraphicsFooter(string? locked)
    {
        var reset = new Button { Content = "Reset", HorizontalAlignment = HorizontalAlignment.Stretch };
        reset.Click += (_, _) =>
        {
            // Two clicks: the sliders tuned in the game's Tab panel are not undone by a stray one.
            if (reset.Tag is null)
            {
                reset.Tag = true;
                reset.Content = Tr("Click again");
                return;
            }
            _game.ResetGraphics(_presets?.Key, _looks?.FaithfulKey, _looks?.PresetKey);
            SaveGame();
            ShowGraphics();
        };
        var footer = Row("Reset graphics", "Every graphics setting back to the game's defaults, also those only in the game's Tab panel.",
            reset, 150);
        footer.BorderThickness = new(0);
        GraphicsFooter.Child = footer;
        GraphicsFooter.IsEnabled = locked is null;
        GraphicsFooter.IsVisible = true;
    }

    /// <summary>
    /// Why the file may not be changed for this schema (null: it may).
    /// </summary>
    string? Locked(SettingsSchema schema) =>
        _game.Problem is { } problem ? problem + " Start the game once to write it again; nothing is changed here until then."
        // The keys of another layout may be gone or mean something else; the game converts the file to its own when it next starts.
        : _game.Version is { } v && v > schema.SettingsVersion
            ? $"The settings were saved by a newer Meitou than v{PlayVersion}: play that version, or change them in the game."
        : _game.Version is { } w && w < schema.SettingsVersion
            ? $"The settings are from an older Meitou: start v{PlayVersion} once to update them, then change them here."
        : null;

    /// <summary>
    /// Whether a setting gets a row: every toggle, and a slider with a range.
    /// </summary>
    static bool Shown(SettingDescription s) => s.Kind == SettingKind.Toggle || s.Kind == SettingKind.Slider && s.Max > s.Min;

    /// <summary>
    /// Fills the content with one tab's rows: its plain settings, then its feature groups (headers that fold their settings), with their marks and
    /// resets.
    /// </summary>
    void FillSection(StackPanel content, SettingsSection section)
    {
        content.Children.Clear();
        _rowMarks.Clear();
        _rowShows.Clear();
        _featureMembers.Clear();
        var groups = section.Groups ?? [];
        foreach (var g in groups.Where(g => g.Header is not null))
        {
            var candidates = g.Members.Select(key => section.Settings.FirstOrDefault(x => x.Key == key));
            _featureMembers[g.Header!] = [.. candidates.OfType<SettingDescription>().Where(Shown)];
        }

        var done = new HashSet<SettingsGroup>();
        foreach (var s in section.Settings.Where(Shown))
        {
            // A feature shows where its first setting is: its header, then its members folded under it. Every row of every tab keeps the arrow's
            // gutter, so the names line up from tab to tab.
            if (groups.FirstOrDefault(g => g.Header == s.Key || g.Members.Contains(s.Key)) is not { } group)
                content.Children.Add(new DockPanel { Children = { Gutter(null), SettingRow(s) } });
            else if (done.Add(group)) content.Children.Add(GroupRows(group, section));
        }
        LauncherText.TranslateTree(content, _originalUiText);
    }

    /// <summary>
    /// A setting's row by its kind: a switch or toggle, a two-label check, a choice, a modes row or a slider; shown only while its condition holds.
    /// </summary>
    Border SettingRow(SettingDescription s)
    {
        var row = s.Kind == SettingKind.Toggle ? s.Graphics ? SwitchRow(s) : ToggleRow(s)
            : s.Labels is { Length: 2 } ? CheckRow(s, s.Labels[0], s.Labels[1], v => WriteCheck(s, v))
            : s.Labels is { Length: > 0 } ? ChoiceRow(s)
            : s.Modes is { Count: > 0 } ? ModesRow(s)
            : SliderRow(s);
        if (s.ShownWhen is { } when) ShowWhen(row, when);
        return row;
    }

    /// <summary>
    /// Writes a two-label setting's checked value (0 or 1) above its minimum: into the graphics block or at the top level.
    /// </summary>
    void WriteCheck(SettingDescription s, float v)
    {
        if (s.Graphics) _game.SetGraphics(s.Key, s.Min + v);
        else _game.Set(s.Key, s.Min + v);
    }

    /// <summary>
    /// Shows the row only while the setting it follows is at its value (<see cref="SettingDescription.ShownWhen"/>); checked again after each change.
    /// </summary>
    void ShowWhen(Control row, SettingCondition when)
    {
        void Check() => row.IsVisible = !_settingByKey.TryGetValue(when.Key, out var other) || when.Matches(CurrentValue(other));
        _rowMarks.Add(Check);
        Check();
    }

    /// <summary>
    /// The column left of a row: a feature's fold arrow (a chevron, turned down while open), else empty.
    /// </summary>
    static Border Gutter(Avalonia.Controls.Shapes.Path? chevron)
    {
        var gutter = new Border { Width = GutterWidth, Background = Avalonia.Media.Brushes.Transparent, Child = chevron };
        DockPanel.SetDock(gutter, Dock.Left);
        return gutter;
    }

    /// <summary>
    /// A feature, as the game's menu shows it: one row (its switch, or its title) with a fold arrow, and its settings in a band under it while open,
    /// named without the feature's name they repeat ("Light shafts strength" is "Strength"). The arrow or the name opens it.
    /// </summary>
    StackPanel GroupRows(SettingsGroup group, SettingsSection section)
    {
        var header = section.Settings.FirstOrDefault(x => x.Key == group.Header && Shown(x));
        string name = header?.Name ?? group.Title;
        var members = group.Members.Select(key => section.Settings.FirstOrDefault(x => x.Key == key)).OfType<SettingDescription>().Where(Shown)
            .Select(m => m with { Name = WithinGroup(m.Name, name) }).ToList();
        var headerRow = header is not null ? SettingRow(header) : Frame(group.Title, group.Description, members, new Panel(), 150);
        var panel = new StackPanel();
        if (members.Count == 0)
        {
            panel.Children.Add(new DockPanel { Children = { Gutter(null), headerRow } });
            return panel;
        }
        bool open = _openGroups.Contains(group.Title);
        var chevron = Chevron(open);
        var under = new Border { Classes = { "features" }, IsVisible = open, Margin = new(GutterWidth, 0, 0, 0) };
        var rows = new StackPanel();
        rows.Children.AddRange(members.Select(SettingRow));
        under.Child = rows;
        void Fold()
        {
            bool nowOpen = !under.IsVisible;
            under.IsVisible = nowOpen;
            chevron.RenderTransform = new Avalonia.Media.RotateTransform(nowOpen ? 90 : 0);
            if (nowOpen) _openGroups.Add(group.Title);
            else _openGroups.Remove(group.Title);
        }
        var gutter = Gutter(chevron);

        // The row's name and description (left of its control) open it too.
        var targets = new List<Control> { gutter };
        if (headerRow.Child is Grid { Children: [StackPanel text, ..] })
        {
            text.Background = Avalonia.Media.Brushes.Transparent;
            targets.Add(text);
        }
        foreach (var target in targets)
        {
            target.Cursor = new Cursor(StandardCursorType.Hand);
            ToolTip.SetTip(target, Tr("{1}: {2} settings", Tr(name), members.Count));
            target.PointerPressed += (_, e) =>
            {
                if (e.GetCurrentPoint(target).Properties.IsLeftButtonPressed)
                {
                    Fold();
                    e.Handled = true;
                }
            };
        }
        panel.Children.Add(new DockPanel { Children = { gutter, headerRow } });
        panel.Children.Add(under);
        return panel;
    }

    /// <summary>
    /// The fold arrow of a feature: pointing right when shut, down when open.
    /// </summary>
    static Avalonia.Controls.Shapes.Path Chevron(bool open)
    {
        var brass = Avalonia.Application.Current?.FindResource("BrassLight") as Avalonia.Media.IBrush;
        return new Avalonia.Controls.Shapes.Path
        {
            Data = Avalonia.Media.Geometry.Parse("M 1,1 L 5,5 L 1,9"), Stroke = brass, StrokeThickness = 1.8, Width = 7, Height = 10,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            RenderTransform = new Avalonia.Media.RotateTransform(open ? 90 : 0),
        };
    }

    /// <summary>
    /// <paramref name="label"/> without the group's name in front ("Shadows quality" in Shadows: "Quality"); as it is otherwise.
    /// </summary>
    static string WithinGroup(string label, string group) =>
        label.Length > group.Length + 1 && label.StartsWith(group + " ", StringComparison.OrdinalIgnoreCase)
            ? char.ToUpperInvariant(label[group.Length + 1]) + label[(group.Length + 2)..] : label;

    /// <summary>
    /// A section heading in capitals.
    /// </summary>
    TextBlock Section(string text) => new() { Classes = { "caps" }, Text = Tr(text).ToUpperInvariant(), Margin = new(0, 14, 0, 2) };

    /// <summary>
    /// A row: the name and description on the left, the control in a column <paramref name="width"/> pixels wide on the right.
    /// </summary>
    static Border Row(string name, string description, Control control, int width)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions($"*,{width}") };
        grid.Children.Add(new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Children = { new TextBlock { Classes = { "name" }, Text = name }, new TextBlock { Classes = { "desc" }, Text = description } },
        });
        Grid.SetColumn(control, 1);
        control.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(control);
        return new Border { Classes = { "row" }, Child = grid };
    }

    #endregion

    #region Looks

    /// <summary>
    /// The schema's look (the named sets of look settings), null when it has none.
    /// </summary>
    SettingsLooks? _looks;

    /// <summary>
    /// The name a preset is being saved under (Save as), null when not naming.
    /// </summary>
    string? _namingLook;

    /// <summary>
    /// A look the row offers: the schema's (Meitou first, then the examples) or one of the player's (its file).
    /// </summary>
    sealed record LookChoice(SettingsLook Look, string? File, string Source);

    /// <summary>
    /// What the look row's actions read: the looks, every look offered (<see cref="AllLooks"/>), whether the look is Faithful, the active one,
    /// how many look settings differ from it, and the folder of the player's presets.
    /// </summary>
    readonly record struct LookRowState(SettingsLooks Looks, List<LookChoice> All, bool Faithful, LookChoice Active, int Changes, string Folder);

    /// <summary>
    /// The look, as the game's settings menu shows it (docs/native-ui.md "Settings"): Faithful | Meitou, the Meitou preset, its changes from the look
    /// (Revert; Save for the player's own), Save as, Share (a code on the clipboard), Paste, Delete. The presets are the game's files
    /// (<see cref="LookPresetFile"/>); choosing a look writes its values, which the game reads as that look with no change.
    /// </summary>
    Border LookRow(SettingsLooks l)
    {
        var all = AllLooks(l);
        bool faithful = IsFaithful(l);
        var active = all.FirstOrDefault(x => x.Look.Name == (_game.GetString(l.PresetKey) ?? l.Presets[0].Name)) ?? all[0];
        int changes = l.Keys.Count(key => _settingByKey.TryGetValue(key, out var s) && Changed(s));
        string folder = Path.Combine(LauncherConfig.DataDirectory, l.Folder);

        var controls = new WrapPanel { Margin = new(0, 6, 0, 0), ItemSpacing = 6, LineSpacing = 6 };
        AddLookSides(controls, l, faithful, active);
        if (!faithful) controls.Children.Add(LookPicker(l, all, active));
        controls.Children.Add(LookStatus(faithful, changes, active));
        AddLookActions(controls, new LookRowState(l, all, faithful, active, changes, folder));
        var panel = new StackPanel
        {
            Children =
            {
                new TextBlock { Classes = { "name" }, Text = Tr(l.Name) },
                new TextBlock { Classes = { "desc" }, Text = Tr(l.Description) },
                controls,
            },
        };
        if (_namingLook is { } naming) AddLookNaming(panel, l, naming, folder);
        return new Border { Classes = { "row" }, Child = panel };
    }

    /// <summary>
    /// Faithful | Meitou, the active side lit.
    /// </summary>
    void AddLookSides(WrapPanel controls, SettingsLooks l, bool faithful, LookChoice active)
    {
        foreach (var (text, side) in new[] { ("Faithful", true), ("Meitou", false) })
        {
            var button = new ToggleButton { Content = Tr(text), IsChecked = faithful == side, MinWidth = 90 };
            button.Click += (_, _) => ApplyLook(l, side, active);
            controls.Children.Add(button);
        }
    }

    /// <summary>
    /// The Meitou preset: the built-in, the examples and the player's own; choosing one applies it.
    /// </summary>
    ComboBox LookPicker(SettingsLooks l, List<LookChoice> all, LookChoice active)
    {
        var names = all.Select(x => x.Source == "built in" ? Tr(x.Look.Name) : x.Look.Name).ToList();
        var box = new ComboBox { MinWidth = 220, ItemsSource = names, SelectedIndex = all.IndexOf(active) };
        box.SelectionChanged += (_, _) =>
        {
            if (box.SelectedIndex >= 0 && all[box.SelectedIndex] != active)
                ApplyLook(l, false, all[box.SelectedIndex]);
        };
        return box;
    }

    /// <summary>
    /// The look's state: its changes from the game (Faithful) or from the preset (in brass while there are some), else where the preset is
    /// from.
    /// </summary>
    TextBlock LookStatus(bool faithful, int changes, LookChoice active)
    {
        string status = faithful
            ? changes == 0 ? Tr("As the game ships") : changes == 1 ? Tr("1 change from the game") : Tr("{1} changes from the game", changes)
            : changes > 0 ? changes == 1 ? Tr("1 unsaved change") : Tr("{1} unsaved changes", changes)
            : active.Source == "mine" ? Tr("Mine · Meitou {1}", MadeOf(active))
                : active.Source == "built in" ? Tr("Built in") : Tr("Example");
        var statusText = new TextBlock
        {
            Classes = { "desc" }, Text = status, VerticalAlignment = VerticalAlignment.Center, Margin = new(6, 0, 6, 0),
        };
        if (changes > 0 && Avalonia.Application.Current?.FindResource("BrassLight") is Avalonia.Media.IBrush brass)
            statusText.Foreground = brass;
        return statusText;
    }

    /// <summary>
    /// The look's buttons: Revert while it has changes; on Meitou, Save (the player's own, changed) or Delete (the player's own), Save as,
    /// Share and Paste.
    /// </summary>
    void AddLookActions(WrapPanel controls, LookRowState look)
    {
        var (l, all, faithful, active, changes, folder) = look;
        Button Action(string text, Action run)
        {
            var button = new Button { Content = Tr(text) };
            button.Click += (_, _) => run();
            controls.Children.Add(button);
            return button;
        }
        if (changes > 0) Action("Revert", () => ApplyLook(l, faithful, active));
        if (faithful) return;
        if (active.Source == "mine" && changes > 0) Action("Save", () => SaveLook(l, active.Look.Name, folder));
        else if (active.Source == "mine")
        {
            var delete = Action("Delete", () => { });
            delete.Click += (_, _) => DeleteLook(delete, l, all, active);
        }
        Action("Save as", () =>
        {
            _namingLook = LookPresetFile.FreeName("My look", all.Select(x => x.Look.Name));
            ShowGraphics();
        });
        Action("Share", async () =>
        {
            if (GetTopLevel(this)?.Clipboard is not { } clipboard) return;
            await clipboard.SetValueAsync(DataFormat.Text, LookPresetFile.ShareCode(new(active.Look.Name, MadeOf(active), active.Look.Values)));
            Status(Tr("{1}'s code is on the clipboard: paste it on Discord, or with Paste.", active.Look.Name));
        });
        Action("Paste", () => PasteLook(l, all, folder));
    }

    /// <summary>
    /// Delete, clicked: the first click asks again, the second removes the player's preset file and goes back to Meitou.
    /// </summary>
    void DeleteLook(Button delete, SettingsLooks l, List<LookChoice> all, LookChoice active)
    {
        // Two clicks: a preset file is not removed by a stray one.
        if (delete.Tag is null)
        {
            delete.Tag = true;
            delete.Content = Tr("Click again");
            return;
        }
        try
        {
            if (active.File is { } file) File.Delete(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Status(Tr("Could not delete the preset: {1}", e.Message));
        }

        ApplyLook(l, false, all[0]);
        Status(Tr("{1} deleted; the look is Meitou.", active.Look.Name));
    }

    /// <summary>
    /// Paste: the preset code on the clipboard saved as the player's own (under a free name) and applied.
    /// </summary>
    async void PasteLook(SettingsLooks l, List<LookChoice> all, string folder)
    {
        string? code = GetTopLevel(this)?.Clipboard is { } clipboard ? await clipboard.TryGetTextAsync() : null;
        if (LookPresetFile.FromShareCode(code) is not { } pasted)
        {
            Status("The clipboard holds no preset code (MT1:...). Copy one with Share first.");
            return;
        }
        var saved = pasted with { Name = LookPresetFile.FreeName(pasted.Name, all.Select(x => x.Look.Name)) };
        try
        {
            LookPresetFile.Save(folder, saved);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Status(Tr("Could not write the preset: {1}", e.Message));
            return;
        }
        ApplyLook(l, false, new LookChoice(new SettingsLook { Name = saved.Name, Values = saved.Values }, null, "mine"));
        Status(Tr("{1} pasted and loaded (made on Meitou {2}).", saved.Name, saved.Made));
    }

    /// <summary>
    /// Save as: the name, then Save or Cancel (Enter / Esc).
    /// </summary>
    void AddLookNaming(StackPanel panel, SettingsLooks l, string naming, string folder)
    {
        var name = new TextBox { Text = naming, Width = 260, MaxLength = LookPresetFile.MaxNameLength };
        var save = new Button { Content = Tr("Save") };
        var cancel = new Button { Content = Tr("Cancel") };
        void Done(bool keep)
        {
            string chosen = (name.Text ?? "").Trim();
            _namingLook = null;
            if (keep && chosen.Length > 0) SaveLook(l, chosen, folder);
            else ShowGraphics();
        }
        save.Click += (_, _) => Done(true);
        cancel.Click += (_, _) => Done(false);
        name.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Done(true);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                Done(false);
                e.Handled = true;
            }
        };
        var label = new TextBlock { Classes = { "desc" }, Text = Tr("Preset name"), VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 6, Margin = new(0, 6, 0, 0), Children = { label, name, save, cancel },
        });
        Dispatcher.UIThread.Post(() =>
        {
            name.Focus();
            name.SelectAll();
        });
    }

    /// <summary>
    /// The Meitou version a look was made on: its file's, else this release's.
    /// </summary>
    string MadeOf(LookChoice look) =>
        look.File is { } file && File.Exists(file) && LookPresetFile.Read(File.ReadAllText(file)) is { } read ? read.Made : _looks?.Made ?? "";

    /// <summary>
    /// Makes a look the active one and writes its values: Faithful's, or Meitou's with the preset's own over them (Faithful keeps the preset to go
    /// back to).
    /// </summary>
    void ApplyLook(SettingsLooks l, bool faithful, LookChoice look)
    {
        _game.Set(l.FaithfulKey, faithful);
        if (!faithful)
        {
            if (look.Source == "built in") _game.Remove(l.PresetKey);
            else _game.Set(l.PresetKey, look.Look.Name);
        }
        foreach (var (key, value) in faithful ? l.Faithful : LookValues(l, look.Look)) _game.SetGraphics(key, value);
        SaveGame();
        Dispatcher.UIThread.Post(ShowGraphics);
    }

    /// <summary>
    /// Saves the look settings as the player's preset <paramref name="name"/> (what differs from Meitou's, as the game saves it) and makes it the
    /// active look.
    /// </summary>
    void SaveLook(SettingsLooks l, string name, string folder)
    {
        var values = new Dictionary<string, float>();
        foreach (var key in l.Keys)
        {
            if (_settingByKey.TryGetValue(key, out var s) && l.Meitou.TryGetValue(key, out float meitou))
            {
                float value = CurrentValue(s);
                bool toggle = s.Kind == SettingKind.Toggle;
                if (MathF.Abs(value - meitou) > (toggle ? 0.5f : MathF.Max(s.Step, 1e-4f) / 2))
                    values[key] = toggle ? value >= 0.5f ? 1 : 0 : MathF.Round(value, 3);
            }
        }
                try
        {
            LookPresetFile.Save(folder, new(name, l.Made, values));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Status(Tr("Could not write the preset: {1}", e.Message));
            return;
        }
        _game.Set(l.FaithfulKey, false);
        _game.Set(l.PresetKey, name);
        SaveGame();
        Status(Tr("{1} saved.", name));
        Dispatcher.UIThread.Post(ShowGraphics);
    }

    /// <summary>
    /// Whether the look is Faithful: saved, else when every switch Faithful turns off and Meitou turns on (not the tier's, not bloom) is saved off,
    /// as the game reads it.
    /// </summary>
    bool IsFaithful(SettingsLooks l) => _game.GetBool(l.FaithfulKey)
        ?? l.Faithful.Keys
            .Where(key => key.StartsWith("Meitou: ", StringComparison.Ordinal) && l.Meitou.TryGetValue(key, out float meitou) && meitou >= 0.5f)
            .All(key => _game.GetGraphics(key) is { } v && v < 0.5f);

    /// <summary>
    /// A Meitou look's values: Meitou's, with the preset's own over them.
    /// </summary>
    static Dictionary<string, float> LookValues(SettingsLooks l, SettingsLook look)
    {
        var values = new Dictionary<string, float>(l.Meitou);
        foreach (var (key, value) in look.Values) values[key] = value;
        return values;
    }

    /// <summary>
    /// The value the current look gives a setting (Reset goes back to it); null when the look does not set it.
    /// </summary>
    float? LookValue(string key)
    {
        if (_looks is not { } l) return null;
        if (IsFaithful(l)) return l.Faithful.TryGetValue(key, out float faithful) ? faithful : null;
        string name = _game.GetString(l.PresetKey) ?? l.Presets[0].Name;
        var look = AllLooks(l).FirstOrDefault(x => x.Look.Name == name)?.Look ?? l.Presets[0];
        return look.Values.TryGetValue(key, out float own) ? own : l.Meitou.TryGetValue(key, out float meitou) ? meitou : null;
    }

    /// <summary>
    /// The schema's looks (Meitou built in, then the examples), then the player's files in the look folder beside the game's settings file.
    /// </summary>
    static List<LookChoice> AllLooks(SettingsLooks l)
    {
        var all = l.Presets.Select((look, i) => new LookChoice(look, null, i == 0 ? "built in" : "example")).ToList();
        if (l.Folder.Length == 0) return all;
        foreach (var (file, read) in LookPresetFile.ReadFolder(Path.Combine(LauncherConfig.DataDirectory, l.Folder)))
            if (!all.Any(x => x.Look.Name == read.Name))
                all.Add(new(new SettingsLook { Name = read.Name, Values = read.Values }, file, "mine"));
        return all;
    }

    #endregion

    #region Presets and detection

    /// <summary>
    /// The schema's quality presets, null when it has none.
    /// </summary>
    SettingsPresets? _presets;

    /// <summary>
    /// The preset row's combo box, null while the page has none.
    /// </summary>
    ComboBox? _presetBox;

    /// <summary>
    /// The last hardware detection's result line, null before one.
    /// </summary>
    string? _detected;

    /// <summary>
    /// A hardware detection is running.
    /// </summary>
    bool _detecting;

    /// <summary>
    /// The quality preset: the one the game saved its settings at, or one chosen here, which the game applies when it next starts.
    /// </summary>
    Border PresetRow(SettingsPresets p)
    {
        string current = _game.GetString(p.Key) is { } saved && p.Names.Contains(saved) ? saved : _game.HasGraphics ? p.Custom : p.Default;
        var names = p.Names.Select(name => Tr(name)).ToList();
        var box = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, ItemsSource = names, SelectedIndex = p.Names.IndexOf(current) };
        box.SelectionChanged += (_, _) =>
        {
            if (box.Tag is not null || box.SelectedIndex < 0) return;
            _game.Set(p.Key, p.Names[box.SelectedIndex]);
            SaveGame();
        };
        _presetBox = box;
        var detect = new Button
        {
            Content = _detecting ? "Detecting" : "Detect", Margin = new(6, 0, 0, 0), IsEnabled = !_detecting, IsVisible = p.Detect is { Length: > 0 },
        };
        ToolTip.SetTip(detect, "Choose the preset for this computer's graphics card, memory and processor.");
        detect.Click += async (_, _) => await DetectPresetAsync();
        var dock = new DockPanel();
        DockPanel.SetDock(detect, Dock.Right);
        dock.Children.Add(detect);
        dock.Children.Add(box);

        // The Detect line under the description: a text the walk cannot look up whole once it is appended, so the description is translated first.
        string description = _detected is { } line ? Tr(p.Description) + "\n" + line : p.Description;
        return Row(p.Name, description, dock, 330);
    }

    /// <summary>
    /// Runs the game's hardware probe (<see cref="SettingsPresets.Detect"/>) and saves the preset it picks; false when it could not (no version that
    /// detects, the settings locked or the game running, a failed probe), with the reason in <see cref="_detected"/>.
    /// </summary>
    async Task<bool> DetectPresetAsync()
    {
        if (_detecting) return false;
        if (_graphicsSave?.IsEnabled == true) SaveGame();
        var (schema, _) = PlaySchema();
        if (schema?.Presets is not { Detect: { Length: > 0 } flag } p || PlayVersion is not { } version
                || Releases.FindGame(Releases.VersionDirectory(version)) is not { } exe)
            return false;
        _game = GameSettings.Load();
        string? locked = Locked(schema) ?? (_running is { HasExited: false } ? "The game is running: close it before detecting the preset." : null);
        if (locked is not null)
        {
            Status(locked);
            return false;
        }
        _detecting = true;
        if (GraphicsPage.IsVisible) ShowGraphics();
        string file = Path.Combine(Path.GetTempPath(), $"meitou-hardware-{Environment.ProcessId}.json");
        bool done = false;
        try
        {
            await RunProbeAsync(exe, flag, file);
            var (preset, summary) = await ReadReportAsync(file);
            if (preset is null || !p.Names.Contains(preset))
                throw new InvalidDataException("the game reported no preset");
            // Read again: the file may have changed while the probe ran.
            _game = GameSettings.Load();
            _schemaVersion = schema.SettingsVersion;
            _game.Set(p.Key, preset);
            SaveGame();
            _detected = Tr("Detected: {1}", summary);
            Status(_detected);
            done = true;
        }
        catch (Exception e)
            when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or InvalidDataException
                or TimeoutException or System.ComponentModel.Win32Exception)
        {
            _detected = Tr("Could not detect the hardware: {1}", e.Message);
            Status(_detected);
        }
        finally
        {
            _detecting = false;
            try
            {
                File.Delete(file);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
            if (GraphicsPage.IsVisible) ShowGraphics();
        }
        return done;
    }

    /// <summary>
    /// Runs the game's probe with the detect flag and the report path, and waits up to a minute for it to finish.
    /// </summary>
    async Task RunProbeAsync(string exe, string flag, string file)
    {
        var start = new ProcessStartInfo(exe)
        {
            WorkingDirectory = LauncherConfig.DataDirectory, UseShellExecute = false, CreateNoWindow = true, ArgumentList = { flag, file },
        };
        using var probe = Process.Start(start) ?? throw new InvalidOperationException("the game did not start");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            await probe.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            probe.Kill();
            throw new TimeoutException("the game took over a minute");
        }
    }

    /// <summary>
    /// The preset and the summary line the probe wrote to <paramref name="file"/>; the preset is null when the report names none.
    /// </summary>
    static async Task<(string? Preset, string Summary)> ReadReportAsync(string file)
    {
        var report = JsonNode.Parse(await File.ReadAllTextAsync(file)) as JsonObject;
        string? preset = report?["preset"] is JsonValue pv && pv.TryGetValue(out string? name) ? name : null;
        string summary = report?["summary"] is JsonValue sv && sv.TryGetValue(out string? line) ? line : preset ?? "";
        return (preset, summary);
    }

    /// <summary>
    /// Once (<see cref="LauncherConfig.PresetDetected"/>): picks the preset for the hardware when a version that can detect it is installed and the
    /// game has no graphics settings saved yet; a player who has some keeps them.
    /// </summary>
    async Task AutoDetectPresetAsync()
    {
        if (_config.PresetDetected || _detecting || PlaySchema().Schema?.Presets is not { Detect.Length: > 0 } p)
            return;
        var saved = GameSettings.Load();
        if (saved.Problem is null && !saved.HasGraphics && saved.GetString(p.Key) is null && !await DetectPresetAsync())
            return;
        _config.PresetDetected = true;
        _config.Save();
    }

    /// <summary>
    /// A setting a preset sets was changed here: the saved preset becomes Custom, so the game keeps the change.
    /// </summary>
    void PresetBroken(string key)
    {
        if (_presets is not { } p || !p.Keys.Contains(key) || _game.GetString(p.Key) == p.Custom) return;
        _game.Set(p.Key, p.Custom);
        if (_presetBox is { } box)
        {
            box.Tag = true;
            box.SelectedIndex = p.Names.IndexOf(p.Custom);
            box.Tag = null;
        }
    }

    #endregion

    #region Setting rows

    /// <summary>
    /// A setting's value: saved, else the game's start value (a top-level one: the schema's default).
    /// </summary>
    float CurrentValue(SettingDescription s) =>
        (s.Graphics ? _game.GetGraphics(s.Key)
            : s.Kind == SettingKind.Toggle ? _game.GetBool(s.Key) is { } on ? on ? 1 : 0 : null
            : _game.GetNumber(s.Key))
        ?? (s.Graphics ? s.Shown : s.Default ?? s.Shown);

    /// <summary>
    /// Whether a look setting differs from the look's value; a quality or display setting is never marked (the Quality row reads Custom).
    /// </summary>
    bool Changed(SettingDescription s) => s.Graphics && _looks?.Keys.Contains(s.Key) == true && LookValue(s.Key) is { } look
        && MathF.Abs(CurrentValue(s) - look) > (s.Kind == SettingKind.Toggle ? 0.5f : MathF.Max(s.Step, 1e-4f) / 2);

    /// <summary>
    /// Puts a look setting back to the look's value.
    /// </summary>
    void ResetSetting(SettingDescription s)
    {
        _game.SetGraphics(s.Key, LookValue(s.Key));
        PresetBroken(s.Key);
    }

    /// <summary>
    /// After a value changed: every row of the tab marked again (a feature header stands for its settings too).
    /// </summary>
    void RefreshMarks()
    {
        foreach (var mark in _rowMarks) mark();
    }

    /// <summary>
    /// A row, the same for every control: its name, and while one of <paramref name="settings"/> (a look setting) differs from the look
    /// (<see cref="Changed"/>) the name in brass with a Reset link beside it that puts them all back, as in the game's menu.
    /// </summary>
    Border Frame(string text, string description, IReadOnlyList<SettingDescription> settings, Control control, int width, string? impact = null)
    {
        var name = new TextBlock { Classes = { "name" }, Text = text };
        var reset = new TextBlock
        {
            Classes = { "reset" }, Text = Tr("Reset"), Margin = new(10, 0, 0, 2), VerticalAlignment = VerticalAlignment.Bottom,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        ToolTip.SetTip(reset, Tr("Back to the look's value"));
        void Mark()
        {
            bool changed = settings.Any(Changed);
            name.Classes.Set("changed", changed);
            reset.IsVisible = changed;
        }
        reset.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(reset).Properties.IsLeftButtonPressed) return;
            foreach (var s in settings.Where(Changed).ToList()) ResetSetting(s);
            SaveGame();
            foreach (var show in _rowShows) show();
            RefreshMarks();
            e.Handled = true;
        };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions($"*,Auto,{width}") };
        grid.Children.Add(new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new StackPanel { Orientation = Orientation.Horizontal, Children = { name, reset } },
                new TextBlock { Classes = { "desc" }, Text = description },
            },
        });
        if (ImpactText(impact) is { } impactText)
        {
            Grid.SetColumn(impactText, 1);
            grid.Children.Add(impactText);
        }
        Grid.SetColumn(control, 2);
        control.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(control);
        _rowMarks.Add(Mark);
        Mark();
        return new Border { Classes = { "row" }, Child = grid };
    }

    /// <summary>
    /// A setting's performance impact, as the game shows it (High in brass), in its own column centred on the row; null when not ranked.
    /// </summary>
    static TextBlock? ImpactText(string? impact)
    {
        if (impact is not { Length: > 0 }) return null;
        var text = new TextBlock
        {
            Classes = { "desc" }, Text = Tr(impact), Margin = new(12, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center,
        };
        if (impact.StartsWith("High", StringComparison.Ordinal)
                && Avalonia.Application.Current?.FindResource("BrassLight") is Avalonia.Media.IBrush brass)
            text.Foreground = brass;
        return text;
    }

    /// <summary>
    /// A setting's row (<see cref="Frame"/>); a feature's header stands for the settings under it too. <paramref name="show"/> puts the control back
    /// after a Reset.
    /// </summary>
    Border SettingFrame(SettingDescription s, Control control, int width, Action show)
    {
        _rowShows.Add(show);
        return Frame(s.Name, s.Description, [s, .. _featureMembers.GetValueOrDefault(s.Key) ?? []], control, width, s.Impact);
    }

    /// <summary>
    /// A drop-down over a setting's values (<paramref name="options"/>, the first one at <paramref name="first"/>), framed as every row is.
    /// </summary>
    Border DropDownRow(SettingDescription s, IReadOnlyList<string> options, float first, Action<float> write)
    {
        var box = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, ItemsSource = options.Select(text => Tr(text)).ToList() };
        bool loading = false;
        void Show()
        {
            loading = true;
            box.SelectedIndex = Math.Clamp((int)MathF.Round(CurrentValue(s) - first), 0, options.Count - 1);
            loading = false;
        }
        Show();
        var row = SettingFrame(s, box, 260, Show);
        box.SelectionChanged += (_, _) =>
        {
            if (loading || box.SelectedIndex < 0) return;
            write(first + box.SelectedIndex);
            PresetBroken(s.Key);
            SaveGame();
            RefreshMarks();
        };
        return row;
    }

    /// <summary>
    /// An on / off setting, as the game shows it: two halves, the active one lit. A Faithful / Meitou effect reads Faithful | Meitou ("Faithful (off)
    /// | Meitou (on)" where the game's side is off), what each side does in its tooltip; an optional effect or a plain setting reads Off | On.
    /// <paramref name="write"/> takes 1 for on (Meitou), 0 for off.
    /// </summary>
    Border CheckRow(SettingDescription s, string off, string on, Action<float> write)
    {
        bool effect = s.Graphics && off.StartsWith("Faithful", StringComparison.Ordinal) || on.StartsWith("Meitou", StringComparison.Ordinal);
        bool offSide = off == "Off";
        string left = !effect ? off : offSide ? "Faithful (off)" : "Faithful", right = !effect ? on : offSide ? "Meitou (on)" : "Meitou";
        var halves = new[]
        {
            new ToggleButton { Content = Tr(left), CornerRadius = new(7, 0, 0, 7), Classes = { "half", "faithful" } },
            new ToggleButton { Content = Tr(right), CornerRadius = new(0, 7, 7, 0), Classes = { "half" } },
        };
        foreach (var half in halves) half.MinWidth = effect && offSide ? 120 : 90;
        if (effect)
        {
            ToolTip.SetTip(halves[0], Tr(off));
            ToolTip.SetTip(halves[1], Tr(on));
        }
        bool loading = false;
        void Show()
        {
            loading = true;
            bool lit = CurrentValue(s) - (s.Kind == SettingKind.Toggle ? 0 : s.Min) >= 0.5f;
            halves[0].IsChecked = !lit;
            halves[1].IsChecked = lit;
            loading = false;
        }
        Show();
        for (int i = 0; i < 2; i++)
        {
            int side = i;
            halves[i].Click += (_, _) =>
            {
                if (loading) return;
                write(side);
                PresetBroken(s.Key);
                SaveGame();
                Show();
                RefreshMarks();
            };
        }
        var pair = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { halves[0], halves[1] },
        };
        return SettingFrame(s, pair, 260, Show);
    }

    /// <summary>
    /// A top-level on / off setting (full screen, VSync).
    /// </summary>
    Border ToggleRow(SettingDescription s) => CheckRow(s, s.Off, s.On, v => _game.Set(s.Key, v >= 0.5f));

    /// <summary>
    /// A Tab panel switch: Faithful (off, saved 0) or Meitou (on, saved 1).
    /// </summary>
    Border SwitchRow(SettingDescription s) => CheckRow(s, s.Off, s.On, v => _game.SetGraphics(s.Key, v));

    /// <summary>
    /// A setting with named values (anti-aliasing, texture quality, water reflection), in the slider's order from its left end.
    /// </summary>
        Border ChoiceRow(SettingDescription s) => DropDownRow(s, s.Labels!, s.Min, v =>
    {
        if (s.Graphics) _game.SetGraphics(s.Key, v);
        else _game.Set(s.Key, v);
    });

    /// <summary>
    /// A slider with named values (<see cref="SettingDescription.Modes"/>, render scale's quality modes): a drop-down of them and Custom, as the
    /// game's menu shows it; the slider under it shows the value, locked but while Custom is chosen (or the value is none of the modes).
    /// </summary>
    Border ModesRow(SettingDescription s)
    {
        var modes = s.Modes!;
        var names = modes.Select(m => Tr(m.Name)).Append(Tr("Custom")).ToList();
        var box = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, ItemsSource = names };
        var custom = SliderRow(s with { Name = "Custom value", Description = "", Impact = null });
        custom.Margin = new(GutterWidth, 0, 0, 0);
        bool loading = false, customChosen = false;
        int Index() => customChosen ? modes.Count : modes.FindIndex(m => m.Matches(CurrentValue(s), s.Step)) is var i and >= 0 ? i : modes.Count;
        void Show()
        {
            loading = true;
            box.SelectedIndex = Index();
            custom.IsEnabled = box.SelectedIndex == modes.Count;
            loading = false;
        }
        Show();
        var row = SettingFrame(s, box, 260, Show);
        box.SelectionChanged += (_, _) =>
        {
            if (loading || box.SelectedIndex < 0) return;
            customChosen = box.SelectedIndex == modes.Count;
            if (!customChosen) WriteSetting(s, modes[box.SelectedIndex].Value);
            PresetBroken(s.Key);
            SaveGame();
            foreach (var show in _rowShows) show();
            RefreshMarks();
        };
        return new Border { Child = new StackPanel { Children = { row, custom } } };
    }

    /// <summary>
    /// Writes a setting's value: into <c>graphics</c> for a graphics setting, else as a top-level key.
    /// </summary>
    void WriteSetting(SettingDescription s, float value)
    {
        if (s.Graphics) _game.SetGraphics(s.Key, value);
        else _game.Set(s.Key, value);
    }

    /// <summary>
    /// A continuous setting: a slider and its value, which can be typed (Enter or leaving the box; Esc puts it back).
    /// </summary>
    Border SliderRow(SettingDescription s)
    {
        var scale = new SliderScale(s);

        // The range itself is not mirrored: Min..Max either way.
        var slider = new Slider
        {
            Minimum = scale.Log ? MathF.Log(s.Min) : s.Min, Maximum = scale.Log ? MathF.Log(s.Max) : s.Max, Margin = new(0, 0, 10, 0),
        };
        var text = new TextBox { Width = 120, VerticalAlignment = VerticalAlignment.Center, TextWrapping = Avalonia.Media.TextWrapping.NoWrap };
        bool loading = false;
        void Show()
        {
            loading = true;
            float v = CurrentValue(s);
            slider.Value = scale.ToSlider(Math.Clamp(v, s.Min, s.Max));
            text.Text = Tr(s.Text(v));
            loading = false;
        }
        Show();
        var dock = new DockPanel();
        DockPanel.SetDock(text, Dock.Right);
        dock.Children.Add(text);
        dock.Children.Add(slider);
        var row = SettingFrame(s, dock, 380, Show);
        void Write(float v)
        {
            if (s.Graphics) _game.SetGraphics(s.Key, v);
            else _game.Set(s.Key, v);
            PresetBroken(s.Key);
            RefreshMarks();
        }
        slider.ValueChanged += (_, e) =>
        {
            if (loading) return;
            float v = scale.FromSlider(e.NewValue);
            text.Text = Tr(s.Text(v));
            Write(v);
            SaveGameSoon();
        };
        WireTypedValue(s, scale, text, Show, Write);
        return row;
    }

    /// <summary>
    /// The slider row's value box: a value typed into it is written on Enter or when it loses focus; Esc puts the row back.
    /// </summary>
    void WireTypedValue(SettingDescription s, SliderScale scale, TextBox text, Action show, Action<float> write)
    {
        // A value typed as the row shows it: the suffix may stay, the off text is the left end, a comma counts as a decimal point.
        float? Parse(string input)
        {
            if (s.OffBelow is not null
                    && (input.Equals(s.OffText, StringComparison.OrdinalIgnoreCase)
                        || input.Equals(Tr(s.OffText), StringComparison.OrdinalIgnoreCase)))
            {
                return s.Min;
            }
            string suffix = s.Suffix.Trim();
            if (suffix.Length > 0 && input.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                input = input[..^suffix.Length].Trim();
            bool parsed = float.TryParse(input.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out float n);
            return parsed && float.IsFinite(n) ? scale.Snap(n / s.Scale) : null;
        }
        void Commit()
        {
            // Anything unreadable, or the shown text left as it was, puts the row back.
            if (Parse((text.Text ?? "").Trim()) is { } v && MathF.Abs(v - CurrentValue(s)) > scale.Step / 2)
            {
                write(v);
                SaveGame();
            }
            show();
        }
        text.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Commit();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                show();
                e.Handled = true;
            }
        };
        text.LostFocus += (_, _) => Commit();
    }

    #endregion

    #region Saving

    /// <summary>
    /// The timer that writes the settings 300 ms after the last change; null before the first.
    /// </summary>
    DispatcherTimer? _graphicsSave;

    /// <summary>
    /// Dragging a slider writes the file once it rests, not on every step.
    /// </summary>
    void SaveGameSoon()
    {
        _graphicsSave ??= new DispatcherTimer(TimeSpan.FromMilliseconds(300), DispatcherPriority.Background, (_, _) => SaveGame());
        _graphicsSave.Stop();
        _graphicsSave.Start();
    }

    /// <summary>
    /// Writes the settings file now (a pending timed save stopped), saying so in the status line.
    /// </summary>
    void SaveGame()
    {
        _graphicsSave?.Stop();
        try
        {
            if (_game.Save(_schemaVersion)) Status("Graphics settings saved; the game reads them when it starts.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Status(Tr("Could not write {1}: {2}", GameSettings.FileName, e.Message));
        }
    }

    #endregion
}
