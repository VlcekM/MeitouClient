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

// The Graphics page: the settings the version Play starts describes in its settings-schema.json, written to the game's
// meitou.user.json (GameSettings). Nothing about the settings is known here: renamed or new ones come with the release.
public sealed partial class MainWindow
{
    GameSettings game = GameSettings.Load();
    Process? running;
    DispatcherTimer? graphicsSave;
    int schemaVersion;
    /// <summary>The Graphics tab shown (a section's name), the schema's presets and their row, and its look.</summary>
    string graphicsTab = "";
    SettingsPresets? presets;
    SettingsLooks? looks;
    /// <summary>Every setting the schema describes, by key (the look row counts and saves through them).</summary>
    Dictionary<string, SettingDescription> settingByKey = [];
    /// <summary>The name a preset is being saved under (Save as), null when not naming.</summary>
    string? namingLook;
    ComboBox? presetBox;
    /// <summary>The features the player opened (by title), kept while the page is shown again.</summary>
    readonly HashSet<string> openGroups = [];
    /// <summary>The shown tab's rows: what marks each one changed, and what puts each control back to the saved value (after a feature's Reset).</summary>
    readonly List<Action> rowMarks = [], rowShows = [];
    /// <summary>A feature header's key to the settings folded under it: its row is marked, and resets, for them too.</summary>
    readonly Dictionary<string, List<SettingDescription>> featureMembers = [];
    /// <summary>The last hardware detection's result line, and whether one is running.</summary>
    string? detected;
    bool detecting;

    // The schema of the version Play starts, and why there is none.
    (SettingsSchema? Schema, string? Problem) PlaySchema()
    {
        if (PlayVersion is not { } version) return (null, "Install a version first (Versions): its settings are shown here.");
        if (Releases.FindGame(Releases.VersionDirectory(version)) is not { } exe) return (null, $"Meitou {version} is not installed any more.");
        var path = Path.Combine(Path.GetDirectoryName(exe)!, SettingsSchema.FileName);
        if (SettingsSchema.Read(path) is { } schema) return (schema, null);
        return (null, File.Exists(path)
            ? $"Meitou {version} describes its settings in a newer way than this launcher reads: update the launcher (Settings)."
            : $"Meitou {version} is older than the Graphics page: change its settings in the game's Tab panel, or play a newer version.");
    }

    void ShowGraphics()
    {
        if (graphicsSave?.IsEnabled == true) SaveGame();
        game = GameSettings.Load();
        GraphicsPanel.Children.Clear();
        GraphicsFooter.Child = null;
        GraphicsFooter.IsVisible = false;
        bool found = game.Version is not null;
        GraphicsFileText.Text = found ? GameSettings.FileName : Loc.Format(Tr("No {1} found"), GameSettings.FileName);
        ToolTip.SetTip(GraphicsFileText, GameSettings.FilePath);
        if (!found)
            GraphicsPanel.Children.Add(new TextBlock
            {
                Classes = { "desc" }, Margin = new(0, 0, 0, 8),
                Text = $"No {GameSettings.FileName} in {LauncherConfig.DataDirectory} yet: the game writes it when it first saves its settings "
                    + "(started with Play), and a change here creates it. Until then the game uses its defaults.",
            });
        var (schema, missing) = PlaySchema();
        if (schema is null)
        {
            GraphicsPanel.Children.Add(new TextBlock { Classes = { "desc" }, Text = missing });
            LauncherText.TranslateTree(GraphicsPanel, originalUiText);
            return;
        }
        schemaVersion = schema.SettingsVersion;
        var notes = new List<string>();
        string? locked = Locked(schema);
        if (locked is not null) notes.Add(locked);
        if (running is { HasExited: false }) notes.Add("The game is running: it saves its settings when it closes, over changes made here.");
        if (SplitArguments(config.LaunchArguments).Contains("--low-end")) notes.Add("The launch arguments have --low-end: the game ignores the graphics sliders while it is set.");
        foreach (var note in notes) GraphicsPanel.Children.Add(new TextBlock { Classes = { "warn" }, Text = note, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new(0, 0, 0, 6) });

        var rows = new StackPanel { IsEnabled = locked is null };
        presets = schema.Presets is { Names.Count: > 0, Key.Length: > 0 } p ? p : null;
        presetBox = null;
        looks = schema.Looks is { Presets.Count: > 0, FaithfulKey.Length: > 0, PresetKey.Length: > 0 } l ? l : null;
        settingByKey = schema.Sections.SelectMany(x => x.Settings).GroupBy(x => x.Key).ToDictionary(g => g.Key, g => g.First());
        if (looks is not null) rows.Children.Add(LookRow(looks));
        if (presets is not null) rows.Children.Add(PresetRow(presets));
        // One tab per section (the game's pages); the chosen one stays while the page is shown again.
        var sections = schema.Sections.Where(section => section.Settings.Any(Shown)).ToList();
        if (sections.Count > 0)
        {
            var tabs = new WrapPanel { Margin = new(0, 14, 0, 4) };
            var content = new StackPanel();
            var section = sections.FirstOrDefault(x => x.Name == graphicsTab) ?? sections[0];
            foreach (var each in sections)
            {
                var tab = new ToggleButton { Content = each.Name, IsChecked = each == section, Margin = new(0, 0, 6, 6) };
                tab.Click += (_, _) =>
                {
                    graphicsTab = each.Name;
                    foreach (var other in tabs.Children.OfType<ToggleButton>()) other.IsChecked = other == tab;
                    FillSection(content, each);
                };
                tabs.Children.Add(tab);
            }
            rows.Children.Add(tabs);
            rows.Children.Add(content);
            FillSection(content, section);
        }
        var reset = new Button { Content = "Reset", HorizontalAlignment = HorizontalAlignment.Stretch };
        reset.Click += (_, _) =>
        {
            // Two clicks: the sliders tuned in the game's Tab panel are not undone by a stray one.
            if (reset.Tag is null) { reset.Tag = true; reset.Content = Tr("Click again"); return; }
            game.ResetGraphics(presets?.Key, looks?.FaithfulKey, looks?.PresetKey);
            SaveGame();
            ShowGraphics();
        };
        var footer = Row("Reset graphics", "Every graphics setting back to the game's defaults, also those only in the game's Tab panel.", reset, 150);
        footer.BorderThickness = new(0);
        GraphicsFooter.Child = footer;
        GraphicsFooter.IsEnabled = locked is null;
        GraphicsFooter.IsVisible = true;
        GraphicsPanel.Children.Add(rows);
        LauncherText.TranslateTree(GraphicsPanel, originalUiText);
        LauncherText.TranslateTree(GraphicsFooter, originalUiText);
    }

    // Why the file may not be changed for this schema (null: it may).
    string? Locked(SettingsSchema schema) =>
        game.Problem is { } problem ? problem + " Start the game once to write it again; nothing is changed here until then."
        // The keys of another layout may be gone or mean something else; the game converts the file to its own when it next starts.
        : game.Version is { } v && v > schema.SettingsVersion ? $"The settings were saved by a newer Meitou than v{PlayVersion}: play that version, or change them in the game."
        : game.Version is { } w && w < schema.SettingsVersion ? $"The settings are from an older Meitou: start v{PlayVersion} once to update them, then change them here."
        : null;

    static bool Shown(SettingDescription s) => s.Kind == SettingKind.Toggle || s.Kind == SettingKind.Slider && s.Max > s.Min;

    void FillSection(StackPanel content, SettingsSection section)
    {
        content.Children.Clear();
        rowMarks.Clear();
        rowShows.Clear();
        featureMembers.Clear();
        var groups = section.Groups ?? [];
        foreach (var g in groups.Where(g => g.Header is not null))
            featureMembers[g.Header!] = [.. g.Members.Select(key => section.Settings.FirstOrDefault(x => x.Key == key)).OfType<SettingDescription>().Where(Shown)];
        var done = new HashSet<SettingsGroup>();
        foreach (var s in section.Settings.Where(Shown))
        {
            // A feature shows where its first setting is: its header, then its members folded under it. Every row of every tab keeps the arrow's gutter,
            // so the names line up from tab to tab.
            if (groups.FirstOrDefault(g => g.Header == s.Key || g.Members.Contains(s.Key)) is not { } group)
                content.Children.Add(new DockPanel { Children = { Gutter(null), SettingRow(s) } });
            else if (done.Add(group)) content.Children.Add(GroupRows(group, section));
        }
        LauncherText.TranslateTree(content, originalUiText);
    }

    Border SettingRow(SettingDescription s) => s.Kind == SettingKind.Toggle ? s.Graphics ? SwitchRow(s) : ToggleRow(s)
        : s.Labels is { Length: 2 } ? CheckRow(s, s.Labels[0], s.Labels[1], v => { if (s.Graphics) game.SetGraphics(s.Key, s.Min + v); else game.Set(s.Key, s.Min + v); })
        : s.Labels is { Length: > 0 } ? ChoiceRow(s) : SliderRow(s);


    const double GutterWidth = 22;

    /// <summary>The column left of a row: a feature's fold arrow (a chevron, turned down while open), else empty.</summary>
    static Border Gutter(Avalonia.Controls.Shapes.Path? chevron)
    {
        var gutter = new Border { Width = GutterWidth, Background = Avalonia.Media.Brushes.Transparent, Child = chevron };
        DockPanel.SetDock(gutter, Dock.Left);
        return gutter;
    }

    /// <summary>A feature, as the game's menu shows it: one row (its switch, or its title) with a fold arrow, and its settings in a band under it
    /// while open, named without the feature's name they repeat ("Light shafts strength" is "Strength"). The arrow or the name opens it.</summary>
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
        bool open = openGroups.Contains(group.Title);
        var brass = Avalonia.Application.Current?.FindResource("BrassLight") as Avalonia.Media.IBrush;
        var chevron = new Avalonia.Controls.Shapes.Path
        {
            Data = Avalonia.Media.Geometry.Parse("M 1,1 L 5,5 L 1,9"), Stroke = brass, StrokeThickness = 1.8, Width = 7, Height = 10,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            RenderTransform = new Avalonia.Media.RotateTransform(open ? 90 : 0),
        };
        var under = new Border { Classes = { "features" }, IsVisible = open, Margin = new(GutterWidth, 0, 0, 0) };
        var rows = new StackPanel();
        rows.Children.AddRange(members.Select(SettingRow));
        under.Child = rows;
        void Fold()
        {
            bool nowOpen = !under.IsVisible;
            under.IsVisible = nowOpen;
            chevron.RenderTransform = new Avalonia.Media.RotateTransform(nowOpen ? 90 : 0);
            if (nowOpen) openGroups.Add(group.Title);
            else openGroups.Remove(group.Title);
        }
        var gutter = Gutter(chevron);
        // The row's name and description (left of its control) open it too.
        var targets = new List<Control> { gutter };
        if (headerRow.Child is Grid { Children: [StackPanel text, ..] }) { text.Background = Avalonia.Media.Brushes.Transparent; targets.Add(text); }
        foreach (var target in targets)
        {
            target.Cursor = new Cursor(StandardCursorType.Hand);
            ToolTip.SetTip(target, Loc.Format(Tr("{1}: {2} settings"), name, members.Count));
            target.PointerPressed += (_, e) => { if (e.GetCurrentPoint(target).Properties.IsLeftButtonPressed) { Fold(); e.Handled = true; } };
        }
        panel.Children.Add(new DockPanel { Children = { gutter, headerRow } });
        panel.Children.Add(under);
        return panel;
    }

    static string WithinGroup(string label, string group) =>
        label.Length > group.Length + 1 && label.StartsWith(group + " ", StringComparison.OrdinalIgnoreCase)
            ? char.ToUpperInvariant(label[group.Length + 1]) + label[(group.Length + 2)..] : label;

    TextBlock Section(string text) => new() { Classes = { "caps" }, Text = Tr(text).ToUpperInvariant(), Margin = new(0, 14, 0, 2) };

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

    /// <summary>A look the row offers: the schema's (Meitou first, then the examples) or one of the player's (its file).</summary>
    sealed record LookChoice(SettingsLook Look, string? File, string Source);

    /// <summary>
    /// The look, as the game's settings menu shows it (docs/native-ui.md "Settings"): Faithful | Meitou, the Meitou preset, its changes from the look
    /// (Revert; Save for the player's own), Save as, Share (a code on the clipboard), Paste, Delete. The presets are the game's files
    /// (<see cref="LookPresetFile"/>); choosing a look writes its values, which the game reads as that look with no change.
    /// </summary>
    Border LookRow(SettingsLooks l)
    {
        var all = AllLooks(l);
        bool faithful = IsFaithful(l);
        var active = all.FirstOrDefault(x => x.Look.Name == (game.GetString(l.PresetKey) ?? l.Presets[0].Name)) ?? all[0];
        int changes = l.Keys.Count(key => settingByKey.TryGetValue(key, out var s) && Changed(s));
        string folder = Path.Combine(LauncherConfig.DataDirectory, l.Folder);

        var controls = new WrapPanel { Margin = new(0, 6, 0, 0), ItemSpacing = 6, LineSpacing = 6 };
        foreach (var (text, side) in new[] { ("Faithful", true), ("Meitou", false) })
        {
            var button = new ToggleButton { Content = Tr(text), IsChecked = faithful == side, MinWidth = 90 };
            button.Click += (_, _) => ApplyLook(l, side, active);
            controls.Children.Add(button);
        }
        if (!faithful)
        {
            var box = new ComboBox
            {
                MinWidth = 220, ItemsSource = all.Select(x => x.Source == "built in" ? Tr(x.Look.Name) : x.Look.Name).ToList(), SelectedIndex = all.IndexOf(active),
            };
            box.SelectionChanged += (_, _) => { if (box.SelectedIndex >= 0 && all[box.SelectedIndex] != active) ApplyLook(l, false, all[box.SelectedIndex]); };
            controls.Children.Add(box);
        }
        string status = faithful ? changes == 0 ? "As the game ships" : changes == 1 ? "1 change from the game" : $"{changes} changes from the game"
            : changes > 0 ? changes == 1 ? "1 unsaved change" : $"{changes} unsaved changes"
            : active.Source == "mine" ? $"Mine · Meitou {MadeOf(active)}" : active.Source == "built in" ? "Built in" : "Example";
        var statusText = new TextBlock { Classes = { "desc" }, Text = Tr(status), VerticalAlignment = VerticalAlignment.Center, Margin = new(6, 0, 6, 0) };
        if (changes > 0 && Avalonia.Application.Current?.FindResource("BrassLight") is Avalonia.Media.IBrush brass) statusText.Foreground = brass;
        controls.Children.Add(statusText);
        Button Action(string text, Action run)
        {
            var button = new Button { Content = Tr(text) };
            button.Click += (_, _) => run();
            controls.Children.Add(button);
            return button;
        }
        if (changes > 0) Action("Revert", () => ApplyLook(l, faithful, active));
        if (!faithful)
        {
            if (active.Source == "mine" && changes > 0) Action("Save", () => SaveLook(l, active.Look.Name, folder));
            else if (active.Source == "mine")
            {
                var delete = Action("Delete", () => { });
                delete.Click += (_, _) =>
                {
                    // Two clicks: a preset file is not removed by a stray one.
                    if (delete.Tag is null) { delete.Tag = true; delete.Content = Tr("Click again"); return; }
                    try { if (active.File is { } file) File.Delete(file); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Status($"Could not delete the preset: {e.Message}"); }
                    ApplyLook(l, false, all[0]);
                    Status(Loc.Format(Tr("{1} deleted; the look is Meitou."), active.Look.Name));
                };
            }
            Action("Save as", () => { namingLook = LookPresetFile.FreeName("My look", all.Select(x => x.Look.Name)); ShowGraphics(); });
            Action("Share", async () =>
            {
                if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
                await clipboard.SetValueAsync(Avalonia.Input.DataFormat.Text, LookPresetFile.ShareCode(new(active.Look.Name, MadeOf(active), active.Look.Values)));
                Status(Loc.Format(Tr("{1}'s code is on the clipboard: paste it on Discord, or with Paste."), active.Look.Name));
            });
            Action("Paste", async () =>
            {
                string? code = TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard ? await clipboard.TryGetTextAsync() : null;
                if (LookPresetFile.FromShareCode(code) is not { } pasted)
                {
                    Status("The clipboard holds no preset code (MT1:...). Copy one with Share first.");
                    return;
                }
                var saved = pasted with { Name = LookPresetFile.FreeName(pasted.Name, all.Select(x => x.Look.Name)) };
                try { LookPresetFile.Save(folder, saved); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Status($"Could not write the preset: {e.Message}"); return; }
                ApplyLook(l, false, new LookChoice(new SettingsLook { Name = saved.Name, Values = saved.Values }, null, "mine"));
                Status(Loc.Format(Tr("{1} pasted and loaded (made on Meitou {2})."), saved.Name, saved.Made));
            });
        }
        var panel = new StackPanel
        {
            Children =
            {
                new TextBlock { Classes = { "name" }, Text = Tr(l.Name) },
                new TextBlock { Classes = { "desc" }, Text = Tr(l.Description) },
                controls,
            },
        };
        if (namingLook is { } naming)
        {
            // Save as: the name, then Save or Cancel (Enter / Esc).
            var name = new TextBox { Text = naming, Width = 260, MaxLength = LookPresetFile.MaxNameLength };
            var save = new Button { Content = Tr("Save") };
            var cancel = new Button { Content = Tr("Cancel") };
            void Done(bool keep)
            {
                string chosen = (name.Text ?? "").Trim();
                namingLook = null;
                if (keep && chosen.Length > 0) SaveLook(l, chosen, folder);
                else ShowGraphics();
            }
            save.Click += (_, _) => Done(true);
            cancel.Click += (_, _) => Done(false);
            name.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter) { Done(true); e.Handled = true; }
                else if (e.Key == Key.Escape) { Done(false); e.Handled = true; }
            };
            panel.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 6, Margin = new(0, 6, 0, 0),
                Children = { new TextBlock { Classes = { "desc" }, Text = Tr("Preset name"), VerticalAlignment = VerticalAlignment.Center }, name, save, cancel },
            });
            Dispatcher.UIThread.Post(() => { name.Focus(); name.SelectAll(); });
        }
        return new Border { Classes = { "row" }, Child = panel };
    }

    /// <summary>The Meitou version a look was made on: its file's, else this release's.</summary>
    string MadeOf(LookChoice look) => look.File is { } file && File.Exists(file) && LookPresetFile.Read(File.ReadAllText(file)) is { } read ? read.Made : looks?.Made ?? "";

    /// <summary>Makes a look the active one and writes its values: Faithful's, or Meitou's with the preset's own over them (Faithful keeps the preset to go back to).</summary>
    void ApplyLook(SettingsLooks l, bool faithful, LookChoice look)
    {
        game.Set(l.FaithfulKey, faithful);
        if (!faithful)
        {
            if (look.Source == "built in") game.Remove(l.PresetKey);
            else game.Set(l.PresetKey, look.Look.Name);
        }
        foreach (var (key, value) in faithful ? l.Faithful : LookValues(l, look.Look)) game.SetGraphics(key, value);
        SaveGame();
        Dispatcher.UIThread.Post(ShowGraphics);
    }

    /// <summary>
    /// Saves the look settings as the player's preset <paramref name="name"/> (what differs from Meitou's, as the game saves it) and makes it the active look.
    /// </summary>
    void SaveLook(SettingsLooks l, string name, string folder)
    {
        var values = new Dictionary<string, float>();
        foreach (var key in l.Keys)
            if (settingByKey.TryGetValue(key, out var s) && l.Meitou.TryGetValue(key, out float meitou))
            {
                float value = CurrentValue(s);
                bool toggle = s.Kind == SettingKind.Toggle;
                if (MathF.Abs(value - meitou) > (toggle ? 0.5f : MathF.Max(s.Step, 1e-4f) / 2)) values[key] = toggle ? value >= 0.5f ? 1 : 0 : MathF.Round(value, 3);
            }
        try { LookPresetFile.Save(folder, new(name, l.Made, values)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Status($"Could not write the preset: {e.Message}"); return; }
        game.Set(l.FaithfulKey, false);
        game.Set(l.PresetKey, name);
        SaveGame();
        Status(Loc.Format(Tr("{1} saved."), name));
        Dispatcher.UIThread.Post(ShowGraphics);
    }

    /// <summary>Whether the look is Faithful: saved, else when every switch Faithful turns off and Meitou turns on (not the tier's, not bloom) is saved off, as the game reads it.</summary>
    bool IsFaithful(SettingsLooks l) => game.GetBool(l.FaithfulKey)
        ?? l.Faithful.Keys.Where(key => key.StartsWith("Meitou: ", StringComparison.Ordinal) && l.Meitou.TryGetValue(key, out float meitou) && meitou >= 0.5f).All(key => game.GetGraphics(key) is { } v && v < 0.5f);

    /// <summary>A Meitou look's values: Meitou's, with the preset's own over them.</summary>
    static Dictionary<string, float> LookValues(SettingsLooks l, SettingsLook look)
    {
        var values = new Dictionary<string, float>(l.Meitou);
        foreach (var (key, value) in look.Values) values[key] = value;
        return values;
    }

    /// <summary>The value the current look gives a setting (Reset goes back to it); null when the look does not set it.</summary>
    float? LookValue(string key)
    {
        if (looks is not { } l) return null;
        if (IsFaithful(l)) return l.Faithful.TryGetValue(key, out float faithful) ? faithful : null;
        string name = game.GetString(l.PresetKey) ?? l.Presets[0].Name;
        var look = AllLooks(l).FirstOrDefault(x => x.Look.Name == name)?.Look ?? l.Presets[0];
        return look.Values.TryGetValue(key, out float own) ? own : l.Meitou.TryGetValue(key, out float meitou) ? meitou : null;
    }

    /// <summary>The schema's looks (Meitou built in, then the examples), then the player's files in the look folder beside the game's settings file.</summary>
    static List<LookChoice> AllLooks(SettingsLooks l)
    {
        var all = l.Presets.Select((look, i) => new LookChoice(look, null, i == 0 ? "built in" : "example")).ToList();
        if (l.Folder.Length == 0) return all;
        foreach (var (file, read) in LookPresetFile.ReadFolder(Path.Combine(LauncherConfig.DataDirectory, l.Folder)))
            if (!all.Any(x => x.Look.Name == read.Name)) all.Add(new(new SettingsLook { Name = read.Name, Values = read.Values }, file, "mine"));
        return all;
    }

    /// <summary>The quality preset: the one the game saved its settings at, or one chosen here, which the game applies when it next starts.</summary>
    Border PresetRow(SettingsPresets p)
    {
        string current = game.GetString(p.Key) is { } saved && p.Names.Contains(saved) ? saved : game.HasGraphics ? p.Custom : p.Default;
        var box = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, ItemsSource = p.Names.Select(Tr).ToList(), SelectedIndex = p.Names.IndexOf(current) };
        box.SelectionChanged += (_, _) =>
        {
            if (box.Tag is not null || box.SelectedIndex < 0) return;
            game.Set(p.Key, p.Names[box.SelectedIndex]);
            SaveGame();
        };
        presetBox = box;
        var detect = new Button { Content = detecting ? "Detecting" : "Detect", Margin = new(6, 0, 0, 0), IsEnabled = !detecting, IsVisible = p.Detect is { Length: > 0 } };
        ToolTip.SetTip(detect, "Choose the preset for this computer's graphics card, memory and processor.");
        detect.Click += async (_, _) => await DetectPresetAsync();
        var dock = new DockPanel();
        DockPanel.SetDock(detect, Dock.Right);
        dock.Children.Add(detect);
        dock.Children.Add(box);
        string description = p.Description;
        return Row(p.Name, detected is { } line ? description + "\n" + line : description, dock, 330);
    }

    /// <summary>
    /// Runs the game's hardware probe (<see cref="SettingsPresets.Detect"/>) and saves the preset it picks; false when it could not (no version that
    /// detects, the settings locked or the game running, a failed probe), with the reason in <see cref="detected"/>.
    /// </summary>
    async Task<bool> DetectPresetAsync()
    {
        if (detecting) return false;
        if (graphicsSave?.IsEnabled == true) SaveGame();
        var (schema, _) = PlaySchema();
        if (schema?.Presets is not { Detect: { Length: > 0 } flag } p || PlayVersion is not { } version || Releases.FindGame(Releases.VersionDirectory(version)) is not { } exe)
            return false;
        game = GameSettings.Load();
        if ((Locked(schema) ?? (running is { HasExited: false } ? "The game is running: close it before detecting the preset." : null)) is { } locked)
        {
            Status(locked);
            return false;
        }
        detecting = true;
        if (GraphicsPage.IsVisible) ShowGraphics();
        string file = Path.Combine(Path.GetTempPath(), $"meitou-hardware-{Environment.ProcessId}.json");
        bool done = false;
        try
        {
            var start = new ProcessStartInfo(exe) { WorkingDirectory = LauncherConfig.DataDirectory, UseShellExecute = false, CreateNoWindow = true, ArgumentList = { flag, file } };
            using var probe = Process.Start(start) ?? throw new InvalidOperationException("the game did not start");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try { await probe.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                probe.Kill();
                throw new TimeoutException("the game took over a minute");
            }
            var report = JsonNode.Parse(await File.ReadAllTextAsync(file)) as JsonObject;
            string? preset = report?["preset"] is JsonValue pv && pv.TryGetValue(out string? name) ? name : null;
            string summary = report?["summary"] is JsonValue sv && sv.TryGetValue(out string? line) ? line : preset ?? "";
            if (preset is null || !p.Names.Contains(preset)) throw new InvalidDataException("the game reported no preset");
            // Read again: the file may have changed while the probe ran.
            game = GameSettings.Load();
            schemaVersion = schema.SettingsVersion;
            game.Set(p.Key, preset);
            SaveGame();
            detected = Loc.Format(Tr("Detected: {1}"), summary);
            Status(detected);
            done = true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or InvalidDataException
            or TimeoutException or System.ComponentModel.Win32Exception)
        {
            detected = Loc.Format(Tr("Could not detect the hardware: {1}"), e.Message);
            Status(detected);
        }
        finally
        {
            detecting = false;
            try { File.Delete(file); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            if (GraphicsPage.IsVisible) ShowGraphics();
        }
        return done;
    }

    /// <summary>
    /// Once (<see cref="LauncherConfig.PresetDetected"/>): picks the preset for the hardware when a version that can detect it is installed and the
    /// game has no graphics settings saved yet; a player who has some keeps them.
    /// </summary>
    async Task AutoDetectPresetAsync()
    {
        if (config.PresetDetected || detecting || PlaySchema().Schema?.Presets is not { Detect.Length: > 0 } p) return;
        var saved = GameSettings.Load();
        if (saved.Problem is null && !saved.HasGraphics && saved.GetString(p.Key) is null && !await DetectPresetAsync()) return;
        config.PresetDetected = true;
        config.Save();
    }

    /// <summary>A setting a preset sets was changed here: the saved preset becomes Custom, so the game keeps the change.</summary>
    void PresetBroken(string key)
    {
        if (presets is not { } p || !p.Keys.Contains(key) || game.GetString(p.Key) == p.Custom) return;
        game.Set(p.Key, p.Custom);
        if (presetBox is { } box)
        {
            box.Tag = true;
            box.SelectedIndex = p.Names.IndexOf(p.Custom);
            box.Tag = null;
        }
    }

    /// <summary>A setting's value: saved, else the game's start value (a top-level one: the schema's default).</summary>
    float CurrentValue(SettingDescription s) =>
        (s.Graphics ? game.GetGraphics(s.Key) : s.Kind == SettingKind.Toggle ? game.GetBool(s.Key) is { } on ? on ? 1 : 0 : null : game.GetNumber(s.Key))
        ?? (s.Graphics ? s.Shown : s.Default ?? s.Shown);

    /// <summary>Whether a look setting differs from the look's value; a quality or display setting is never marked (the Quality row reads Custom).</summary>
    bool Changed(SettingDescription s) => s.Graphics && looks?.Keys.Contains(s.Key) == true && LookValue(s.Key) is { } look
        && MathF.Abs(CurrentValue(s) - look) > (s.Kind == SettingKind.Toggle ? 0.5f : MathF.Max(s.Step, 1e-4f) / 2);

    /// <summary>Puts a look setting back to the look's value.</summary>
    void ResetSetting(SettingDescription s)
    {
        game.SetGraphics(s.Key, LookValue(s.Key));
        PresetBroken(s.Key);
    }

    /// <summary>After a value changed: every row of the tab marked again (a feature header stands for its settings too).</summary>
    void RefreshMarks()
    {
        foreach (var mark in rowMarks) mark();
    }

    /// <summary>
    /// A row, the same for every control: its name, and while one of <paramref name="settings"/> (a look setting) differs from the look
    /// (<see cref="Changed"/>) the name in brass with a Reset link beside it that puts them all back, as in the game's menu.
    /// </summary>
    Border Frame(string text, string description, IReadOnlyList<SettingDescription> settings, Control control, int width, string? impact = null)
    {
        var name = new TextBlock { Classes = { "name" }, Text = text };
        var reset = new TextBlock { Classes = { "reset" }, Text = Tr("Reset"), Margin = new(10, 0, 0, 2), VerticalAlignment = VerticalAlignment.Bottom, Cursor = new Cursor(StandardCursorType.Hand) };
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
            foreach (var show in rowShows) show();
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
        rowMarks.Add(Mark);
        Mark();
        return new Border { Classes = { "row" }, Child = grid };
    }

    /// <summary>A setting's performance impact, as the game shows it (High in brass), in its own column centred on the row; null when not ranked.</summary>
    static TextBlock? ImpactText(string? impact)
    {
        if (impact is not { Length: > 0 }) return null;
        var text = new TextBlock { Classes = { "desc" }, Text = Tr(impact), Margin = new(12, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        if (impact.StartsWith("High", StringComparison.Ordinal) && Avalonia.Application.Current?.FindResource("BrassLight") is Avalonia.Media.IBrush brass) text.Foreground = brass;
        return text;
    }

    /// <summary>A setting's row (<see cref="Frame"/>); a feature's header stands for the settings under it too. <paramref name="show"/> puts the control back after a Reset.</summary>
    Border SettingFrame(SettingDescription s, Control control, int width, Action show)
    {
        rowShows.Add(show);
        return Frame(s.Name, s.Description, [s, .. featureMembers.GetValueOrDefault(s.Key) ?? []], control, width, s.Impact);
    }

    /// <summary>A drop-down over a setting's values (<paramref name="options"/>, the first one at <paramref name="first"/>), framed as every row is.</summary>
    Border DropDownRow(SettingDescription s, IReadOnlyList<string> options, float first, Action<float> write)
    {
        var box = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, ItemsSource = options.Select(Tr).ToList() };
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
    /// An on / off setting, as the game shows it: two halves, the active one lit. A Faithful / Meitou effect reads Faithful | Meitou ("Faithful (off) |
    /// Meitou (on)" where the game's side is off), what each side does in its tooltip; an optional effect or a plain setting reads Off | On.
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
        var pair = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { halves[0], halves[1] } };
        return SettingFrame(s, pair, 260, Show);
    }

    /// <summary>A top-level on / off setting (full screen, VSync).</summary>
    Border ToggleRow(SettingDescription s) => CheckRow(s, s.Off, s.On, v => game.Set(s.Key, v >= 0.5f));

    /// <summary>A Tab panel switch: Faithful (off, saved 0) or Meitou (on, saved 1).</summary>
    Border SwitchRow(SettingDescription s) => CheckRow(s, s.Off, s.On, v => game.SetGraphics(s.Key, v));

    /// <summary>A setting with named values (anti-aliasing, texture quality, water reflection), in the slider's order from its left end.</summary>
    Border ChoiceRow(SettingDescription s) => DropDownRow(s, s.Labels!, s.Min, v => { if (s.Graphics) game.SetGraphics(s.Key, v); else game.Set(s.Key, v); });

    /// <summary>A continuous setting: a slider and its value, which can be typed (Enter or leaving the box; Esc puts it back).</summary>
    Border SliderRow(SettingDescription s)
    {
        bool log = s.Logarithmic && s.Min > 0;
        float step = s.Step > 0 ? s.Step : 1;
        // A reversed setting's value is mirrored onto the slider (Min + Max - value; on a logarithmic one Min x Max / value), so right is always the better end.
        float Mirror(float v) => !s.Reversed ? v : log ? s.Min * s.Max / v : s.Min + s.Max - v;
        float ToSlider(float v) => log ? MathF.Log(Mirror(v)) : Mirror(v);
        float Snap(float v) => Math.Clamp(MathF.Round(v / step) * step, s.Min, s.Max);
        float FromSlider(double x) => Snap(Mirror(log ? MathF.Exp((float)x) : (float)x));
        // The range itself is not mirrored: Min..Max either way.
        var slider = new Slider { Minimum = log ? MathF.Log(s.Min) : s.Min, Maximum = log ? MathF.Log(s.Max) : s.Max, Margin = new(0, 0, 10, 0) };
        var text = new TextBox { Width = 120, VerticalAlignment = VerticalAlignment.Center, TextWrapping = Avalonia.Media.TextWrapping.NoWrap };
        bool loading = false;
        void Show()
        {
            loading = true;
            float v = CurrentValue(s);
            slider.Value = ToSlider(Math.Clamp(v, s.Min, s.Max));
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
            if (s.Graphics) game.SetGraphics(s.Key, v);
            else game.Set(s.Key, v);
            PresetBroken(s.Key);
            RefreshMarks();
        }
        slider.ValueChanged += (_, e) =>
        {
            if (loading) return;
            float v = FromSlider(e.NewValue);
            text.Text = Tr(s.Text(v));
            Write(v);
            SaveGameSoon();
        };
        // A value typed as the row shows it: the suffix may stay, the off text is the left end, a comma counts as a decimal point.
        float? Parse(string input)
        {
            if (s.OffBelow is not null && (input.Equals(s.OffText, StringComparison.OrdinalIgnoreCase) || input.Equals(Tr(s.OffText), StringComparison.OrdinalIgnoreCase)))
                return s.Min;
            string suffix = s.Suffix.Trim();
            if (suffix.Length > 0 && input.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) input = input[..^suffix.Length].Trim();
            return float.TryParse(input.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out float n) && float.IsFinite(n) ? Snap(n / s.Scale) : null;
        }
        void Commit()
        {
            // Anything unreadable, or the shown text left as it was, puts the row back.
            if (Parse((text.Text ?? "").Trim()) is { } v && MathF.Abs(v - CurrentValue(s)) > step / 2)
            {
                Write(v);
                SaveGame();
            }
            Show();
        }
        text.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { Commit(); e.Handled = true; }
            else if (e.Key == Key.Escape) { Show(); e.Handled = true; }
        };
        text.LostFocus += (_, _) => Commit();
        return row;
    }

    // Dragging a slider writes the file once it rests, not on every step.
    void SaveGameSoon()
    {
        graphicsSave ??= new DispatcherTimer(TimeSpan.FromMilliseconds(300), DispatcherPriority.Background, (_, _) => SaveGame());
        graphicsSave.Stop();
        graphicsSave.Start();
    }

    void SaveGame()
    {
        graphicsSave?.Stop();
        try
        {
            if (game.Save(schemaVersion)) Status("Graphics settings saved; the game reads them when it starts.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Status($"Could not write {GameSettings.FileName}: {e.Message}");
        }
    }
}
