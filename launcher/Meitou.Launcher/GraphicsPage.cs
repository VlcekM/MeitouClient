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
        looks = schema.Looks is { Names.Count: > 0, FaithfulKey.Length: > 0, PresetKey.Length: > 0 } l ? l : null;
        if (looks is not null) rows.Children.Add(LookRow(looks, schema));
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
        rows.Children.Add(Section("Reset"));
        rows.Children.Add(Row("Reset graphics", "Every graphics setting back to the game's defaults, also those only in the game's Tab panel.", reset, 150));
        GraphicsPanel.Children.Add(rows);
        LauncherText.TranslateTree(GraphicsPanel, originalUiText);
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
        foreach (var s in section.Settings.Where(Shown))
            content.Children.Add(s.Kind == SettingKind.Toggle ? s.Graphics ? SwitchRow(s) : ToggleRow(s) : SliderRow(s));
        LauncherText.TranslateTree(content, originalUiText);
    }

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
    /// The look: Faithful, or a Meitou look preset (the schema's, then the player's files). Choosing one removes the settings a look sets, which the
    /// game sets from the look when it next starts; until then the rows read "From the look".
    /// </summary>
    Border LookRow(SettingsLooks l, SettingsSchema schema)
    {
        var names = l.Names.Concat(PlayerLooks(l.Folder)).Distinct().ToList();
        string preset = game.GetString(l.PresetKey) ?? l.Names[0];
        if (!names.Contains(preset)) names.Add(preset);
        // No saved choice: Faithful when every look switch is saved off, as the game reads it.
        var switches = schema.Sections.SelectMany(x => x.Settings).Where(x => x.Graphics && x.Kind == SettingKind.Toggle && l.Keys.Contains(x.Key)).Select(x => x.Key).ToList();
        bool faithful = game.GetBool(l.FaithfulKey) ?? (switches.Count > 0 && switches.All(key => game.GetGraphics(key) is { } v && v < 0.5f));
        var box = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = names.Select(name => name == l.Names[0] ? Tr(name) : $"{Tr(l.Names[0])}: {name}").Prepend(Tr("Faithful")).ToList(),
            SelectedIndex = faithful ? 0 : 1 + names.IndexOf(preset),
        };
        box.SelectionChanged += (_, _) =>
        {
            if (box.SelectedIndex < 0) return;
            game.Set(l.FaithfulKey, box.SelectedIndex == 0);
            // Faithful keeps the preset the game goes back to.
            if (box.SelectedIndex == 1) game.Remove(l.PresetKey);
            else if (box.SelectedIndex > 1) game.Set(l.PresetKey, names[box.SelectedIndex - 1]);
            foreach (var key in l.Keys) game.SetGraphics(key, null);
            SaveGame();
            Dispatcher.UIThread.Post(ShowGraphics);
        };
        return Row(l.Name, l.Description + " A look chosen here is applied when the game next starts: the settings it sets take its values.", box, 330);
    }

    /// <summary>The names of the player's look presets: JSON files in <paramref name="folder"/> beside the game's settings file.</summary>
    static List<string> PlayerLooks(string folder)
    {
        var names = new List<string>();
        string dir = Path.Combine(LauncherConfig.DataDirectory, folder);
        if (folder.Length == 0 || !Directory.Exists(dir)) return names;
        foreach (var file in Directory.GetFiles(dir, "*.json").Order(StringComparer.OrdinalIgnoreCase))
            try
            {
                if (JsonNode.Parse(File.ReadAllText(file))?["name"] is JsonValue v && v.TryGetValue(out string? name) && name.Length > 0) names.Add(name);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
        return names;
    }

    /// <summary>Whether the look sets this setting: with no saved value it takes the look's, not the game's default.</summary>
    bool FromLook(SettingDescription s) => looks?.Keys.Contains(s.Key) == true;

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
        string description = p.Description + " A preset chosen here is applied when the game next starts.";
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

    Border ToggleRow(SettingDescription s)
    {
        bool value = game.GetBool(s.Key) ?? s.Default >= 0.5f;
        var toggle = new ToggleButton { IsChecked = value, Content = Tr(value ? s.On : s.Off), HorizontalAlignment = HorizontalAlignment.Stretch };
        toggle.Click += (_, _) =>
        {
            bool v = toggle.IsChecked == true;
            toggle.Content = Tr(v ? s.On : s.Off);
            game.Set(s.Key, v);
            SaveGame();
        };
        return Row(s.Name, s.Description, toggle, 150);
    }

    /// <summary>A Tab panel switch: the game's default (no key: the mode and the computer decide; a look switch: the look), or saved off (0) or on (1).</summary>
    Border SwitchRow(SettingDescription s)
    {
        var box = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = new[] { FromLook(s) ? Tr("From the look") : Loc.Format(Tr("Game default ({1})"), Tr(s.Shown >= 0.5f ? s.On : s.Off)), Tr(s.Off), Tr(s.On) },
            SelectedIndex = game.GetGraphics(s.Key) is { } v ? v >= 0.5f ? 2 : 1 : 0,
        };
        box.SelectionChanged += (_, _) =>
        {
            if (box.SelectedIndex < 0) return;
            game.SetGraphics(s.Key, box.SelectedIndex switch { 1 => 0, 2 => 1, _ => null });
            PresetBroken(s.Key);
            SaveGame();
        };
        return Row(s.Name, s.Description, box, 260);
    }

    /// <summary>A slider with its value and a Default button: a graphics slider's key is removed (the game's default for the mode),
    /// a top-level one gets the schema's default. A slider without labels also takes a typed value (Enter or leaving the box).</summary>
    Border SliderRow(SettingDescription s)
    {
        bool log = s.Logarithmic && s.Min > 0;
        float step = s.Step > 0 ? s.Step : 1;
        // A reversed setting's value is mirrored onto the slider (Min + Max - value), so right is always the better end.
        float Mirror(float v) => s.Reversed ? s.Min + s.Max - v : v;
        float ToSlider(float v) => log ? MathF.Log(Mirror(v)) : Mirror(v);
        float Snap(float v) => Math.Clamp(MathF.Round(v / step) * step, s.Min, s.Max);
        float FromSlider(double x) => Snap(Mirror(log ? MathF.Exp((float)x) : (float)x));
        float? Get() => s.Graphics ? game.GetGraphics(s.Key) : game.GetNumber(s.Key) ?? s.Default;
        // The range itself is not mirrored: Min..Max either way.
        var slider = new Slider { Minimum = log ? MathF.Log(s.Min) : s.Min, Maximum = log ? MathF.Log(s.Max) : s.Max, Margin = new(0, 0, 10, 0) };
        bool typed = s.Labels is not { Length: > 0 };
        var text = new TextBox { Width = 120, IsVisible = typed, VerticalAlignment = VerticalAlignment.Center, TextWrapping = Avalonia.Media.TextWrapping.NoWrap };
        var value = new TextBlock { Classes = { "desc" }, Width = 120, IsVisible = !typed, VerticalAlignment = VerticalAlignment.Center, TextWrapping = Avalonia.Media.TextWrapping.NoWrap };
        var reset = new Button { Content = Tr("Default"), Margin = new(6, 0, 0, 0), IsEnabled = s.Graphics || s.Default is not null };
        bool loading = false;
        void ShowValue(float? v) => text.Text = value.Text = Tr(v is { } x ? s.Text(x) : FromLook(s) ? "From the look" : "Game default");
        void Show()
        {
            loading = true;
            var v = Get();
            slider.Value = ToSlider(Math.Clamp(v ?? s.Shown, s.Min, s.Max));
            ShowValue(v);
            loading = false;
        }
        void Write(float v)
        {
            if (s.Graphics) game.SetGraphics(s.Key, v);
            else game.Set(s.Key, v);
            PresetBroken(s.Key);
        }
        slider.ValueChanged += (_, e) =>
        {
            if (loading) return;
            float v = FromSlider(e.NewValue);
            ShowValue(v);
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
            if (Parse((text.Text ?? "").Trim()) is { } v && v != Get())
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
        reset.Click += (_, _) =>
        {
            if (s.Graphics) game.SetGraphics(s.Key, null);
            else if (s.Default is { } d) game.Set(s.Key, d);
            PresetBroken(s.Key);
            SaveGame();
            Show();
        };
        Show();
        var dock = new DockPanel();
        DockPanel.SetDock(reset, Dock.Right);
        DockPanel.SetDock(text, Dock.Right);
        DockPanel.SetDock(value, Dock.Right);
        dock.Children.Add(reset);
        dock.Children.Add(text);
        dock.Children.Add(value);
        dock.Children.Add(slider);
        return Row(s.Name, s.Description, dock, 460);
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
