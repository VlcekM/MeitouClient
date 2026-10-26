using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
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

    /// <summary>
    /// The look: Faithful, or a Meitou look preset (the schema's, then the player's files). Choosing one writes its values, as the game's menu sets them;
    /// the game counts no change from the look it reads.
    /// </summary>
    Border LookRow(SettingsLooks l)
    {
        var all = AllLooks(l);
        var box = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = all.Select(look => look.Name == l.Presets[0].Name ? Tr(look.Name) : $"{Tr(l.Presets[0].Name)}: {look.Name}").Prepend(Tr("Faithful")).ToList(),
            SelectedIndex = IsFaithful(l) ? 0 : 1 + Math.Max(0, all.FindIndex(look => look.Name == (game.GetString(l.PresetKey) ?? l.Presets[0].Name))),
        };
        box.SelectionChanged += (_, _) =>
        {
            if (box.SelectedIndex < 0) return;
            bool faithful = box.SelectedIndex == 0;
            game.Set(l.FaithfulKey, faithful);
            // Faithful keeps the preset the game goes back to.
            if (box.SelectedIndex == 1) game.Remove(l.PresetKey);
            else if (box.SelectedIndex > 1) game.Set(l.PresetKey, all[box.SelectedIndex - 1].Name);
            foreach (var (key, value) in faithful ? l.Faithful : LookValues(l, all[box.SelectedIndex - 1])) game.SetGraphics(key, value);
            SaveGame();
            Dispatcher.UIThread.Post(ShowGraphics);
        };
        return Row(l.Name, l.Description, box, 330);
    }

    /// <summary>Whether the look is Faithful: saved, else when every switch Faithful turns off is saved off (as the game reads it).</summary>
    bool IsFaithful(SettingsLooks l) => game.GetBool(l.FaithfulKey)
        ?? l.Faithful.Keys.Where(key => key.StartsWith("Meitou: ", StringComparison.Ordinal)).All(key => game.GetGraphics(key) is { } v && v < 0.5f);

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
        var look = AllLooks(l).FirstOrDefault(x => x.Name == name) ?? l.Presets[0];
        return look.Values.TryGetValue(key, out float own) ? own : l.Meitou.TryGetValue(key, out float meitou) ? meitou : null;
    }

    /// <summary>The schema's looks, then the player's: JSON files in the look folder beside the game's settings file, each with a <c>name</c> and <c>values</c>.</summary>
    static List<SettingsLook> AllLooks(SettingsLooks l)
    {
        var all = new List<SettingsLook>(l.Presets);
        string dir = Path.Combine(LauncherConfig.DataDirectory, l.Folder);
        if (l.Folder.Length == 0 || !Directory.Exists(dir)) return all;
        foreach (var file in Directory.GetFiles(dir, "*.json").Order(StringComparer.OrdinalIgnoreCase))
            try
            {
                if (JsonNode.Parse(File.ReadAllText(file)) is not JsonObject json || json["name"] is not JsonValue n || !n.TryGetValue(out string? name) || name.Length == 0
                    || all.Any(x => x.Name == name)) continue;
                var look = new SettingsLook { Name = name };
                if (json["values"] is JsonObject values)
                    foreach (var (key, value) in values)
                        if (value is JsonValue v && v.TryGetValue(out double d)) look.Values[key] = (float)d;
                all.Add(look);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
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
    bool Changed(SettingDescription s) => s.Graphics && LookValue(s.Key) is { } look
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
    Border Frame(string text, string description, IReadOnlyList<SettingDescription> settings, Control control, int width)
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
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions($"*,{width}") };
        grid.Children.Add(new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new StackPanel { Orientation = Orientation.Horizontal, Children = { name, reset } },
                new TextBlock { Classes = { "desc" }, Text = description },
            },
        });
        Grid.SetColumn(control, 1);
        control.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(control);
        rowMarks.Add(Mark);
        Mark();
        return new Border { Classes = { "row" }, Child = grid };
    }

    /// <summary>A setting's row (<see cref="Frame"/>); a feature's header stands for the settings under it too. <paramref name="show"/> puts the control back after a Reset.</summary>
    Border SettingFrame(SettingDescription s, Control control, int width, Action show)
    {
        rowShows.Add(show);
        return Frame(s.Name, s.Description, [s, .. featureMembers.GetValueOrDefault(s.Key) ?? []], control, width);
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

    /// <summary>A top-level on / off setting (full screen, VSync).</summary>
    Border ToggleRow(SettingDescription s) => DropDownRow(s, [s.Off, s.On], 0, v => game.Set(s.Key, v >= 0.5f));

    /// <summary>A Tab panel switch: Faithful (off, saved 0) or Meitou (on, saved 1).</summary>
    Border SwitchRow(SettingDescription s) => DropDownRow(s, [s.Off, s.On], 0, v => game.SetGraphics(s.Key, v));

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
