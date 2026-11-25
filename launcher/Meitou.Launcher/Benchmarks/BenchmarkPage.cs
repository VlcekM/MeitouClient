using System.Diagnostics;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Meitou.Content;

namespace Meitou.Launcher.Benchmarks;

/// <summary>
/// What the Benchmark page takes from the window: the shared HTTP client, installer and settings, the release list, the Kenshi folder, the
/// game Play would start with its preset detection flag (for the recommended tier), whether that game runs, and the modal choice dialog.
/// </summary>
sealed record BenchmarkHost(HttpClient Http, ReleaseInstaller Installer, LauncherConfig Config, Func<IReadOnlyList<Release>> Releases,
    Func<GameInstall?> Kenshi, Func<(string Exe, string Flag)?> DetectGame, Func<bool> GameRunning,
    Func<string, IReadOnlyList<Control>, IReadOnlyList<(string Label, bool? Value)>, Task<bool?>> Choose);

/// <summary>
/// The Benchmark page (docs/launcher.md "Benchmark"): choose a build, benchmarks, tiers and platform, run them (two launches per session) and
/// submit the sessions to the reports service. Built in code and in the launcher's language whenever it is built, so the window's
/// translation walk leaves it alone (<see cref="LauncherText"/>).
/// </summary>
public sealed class BenchmarkPage : UserControl, IBenchmarkView
{
    #region Page

    /// <summary>
    /// Width of the right-hand control column on configuration rows (matching Settings).
    /// </summary>
    const int ControlWidth = 460;

    /// <summary>
    /// Public benchmarks site URL.
    /// </summary>
    const string BenchmarksSiteUrl = "https://meitou.kenshi.wiki/benchmarks";

    /// <summary>
    /// The window's services, null until <see cref="Attach"/>.
    /// </summary>
    BenchmarkHost? _host;

    /// <summary>
    /// The reports service's benchmark endpoints.
    /// </summary>
    BenchmarkService? _service;

    /// <summary>
    /// Runs a Start or an Install.
    /// </summary>
    BenchmarkRunner? _runner;

    /// <summary>
    /// The results tabs, single table and Submit all card.
    /// </summary>
    BenchmarkResultsPanel? _results;

    /// <summary>
    /// Whether the page was shown once (the targets and the recommendation are loaded then).
    /// </summary>
    bool _loaded;

    /// <summary>
    /// Connects the page to the window; called once.
    /// </summary>
    internal void Attach(BenchmarkHost host)
    {
        _host = host;
        _service = new BenchmarkService(host.Http);
        _runner = new BenchmarkRunner(host.Installer, new BenchmarkSave(host.Http));
        _results = new BenchmarkResultsPanel(_service, host.Config, ScreenResolution);
        _platforms = BenchmarkPlatform.Available();
        _timer.Tick += (_, _) => ShowElapsed();
    }

    /// <summary>
    /// The page became visible: the release list taken again, the targets and the recommended tier loaded the first time.
    /// </summary>
    public void Shown()
    {
        if (_host is null) return;
        if (!Running) TakeReleases();
        Rebuild();
        if (_loaded) return;
        _loaded = true;
        _ = LoadTargetsAsync();
        _ = RecommendAsync();
    }

    /// <summary>
    /// The language changed: everything is built again in it.
    /// </summary>
    public void LanguageChanged()
    {
        if (_host is not null) Rebuild();
    }

    /// <summary>
    /// <paramref name="text"/> in the launcher's language, its holes filled.
    /// </summary>
    static string Tr(string text, params object?[] args) => LauncherText.Translate(text, args);

    /// <summary>
    /// The page's whole content: the header, the choices, the run controls, live progress and the results.
    /// </summary>
    void Rebuild()
    {
        var rows = new StackPanel { Margin = new(0, 0, 14, 0) };
        rows.Children.Add(Header());
        rows.Children.Add(BuildRow());
        if (_platforms.Count > 1) rows.Children.Add(PlatformRow());
        rows.Children.Add(ScenarioRow());
        rows.Children.Add(TierRow());
        rows.Children.Add(RunRow());
        rows.Children.Add(ProgressBlock());

        // The results panel keeps its selected tabs and submit state across rebuilds, so it moves from the old content to the new.
        if (_results!.Parent is Panel old) old.Children.Remove(_results);
        rows.Children.Add(_results);
        Content = new ScrollViewer { Classes = { "page" }, Content = rows };
        ShowResults();
    }

    /// <summary>
    /// The page's title bar, with the targets' count, a hover tooltip listing the fixed benchmark settings, and a link to the benchmarks site.
    /// </summary>
    Border Header()
    {
        string aside = _targets is null ? _targetsProblem ?? Tr("Loading the target builds...") : Tr("{1} target builds", _targets.Count);
        var settingsInfo = SettingsInfoBadge();
        var site = new Button { Classes = { "aside" }, Content = Tr("Benchmarks site") };
        ToolTip.SetTip(site, BenchmarksSiteUrl);
        site.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(this) is { } top) await top.Launcher.LaunchUriAsync(new Uri(BenchmarksSiteUrl));
        };

        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
        right.Children.Add(new TextBlock { Classes = { "aside" }, Text = aside, VerticalAlignment = VerticalAlignment.Center });
        right.Children.Add(settingsInfo);
        right.Children.Add(site);

        var dock = new DockPanel();
        DockPanel.SetDock(right, Dock.Right);
        dock.Children.Add(right);
        dock.Children.Add(new TextBlock { Text = Tr("Benchmark") });
        return new Border { Classes = { "header" }, Margin = new(0, 0, 0, 10), Child = dock };
    }

    /// <summary>
    /// A compact hover target in the header explaining the fixed display, graphics and isolation settings used during every benchmark run.
    /// </summary>
    static Border SettingsInfoBadge()
    {
        var icon = new Avalonia.Controls.Shapes.Path
        {
            Data = Geometry.Parse("M8,1 A7,7 0 1 0 8,15 A7,7 0 1 0 8,1 Z M7.2,4.2 H8.8 V5.8 H7.2 Z M7.2,7 H8.8 V12 H7.2 Z"),
            Fill = Brush.Parse("#c4a062"), Width = 14, Height = 14, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center,
        };
        var label = new TextBlock
        {
            Classes = { "aside" }, Text = Tr("Benchmark settings"), VerticalAlignment = VerticalAlignment.Center,
            TextDecorations = TextDecorations.Underline,
        };
        var badge = new Border
        {
            Background = Brushes.Transparent, Padding = new(4, 2), VerticalAlignment = VerticalAlignment.Center,
            Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Children = { icon, label } },
        };
        ToolTip.SetTip(badge, BenchmarkSettingsTip());
        ToolTip.SetShowDelay(badge, 150);
        return badge;
    }

    /// <summary>
    /// Multi-line tooltip detailing the fixed settings the game enforces during <c>--benchmark</c>.
    /// </summary>
    static string BenchmarkSettingsTip() => string.Join("\n", new[]
    {
        Tr("Benchmarks always use fixed settings so results stay comparable:"),
        Tr("• Windowed 1920×1080, Vulkan, VSync & frame limit off, HDR off"),
        Tr("• Chosen quality tier (Meitou look), TAA at 100% scale (Low: no AA at 83%), no upscaler"),
        Tr("• Ray tracing only on Ultra and Extreme"),
        Tr("• Your saved graphics settings and mods are ignored and never modified"),
    });

    /// <summary>
    /// The primary screen's size in pixels, for the machine record; empty when unknown.
    /// </summary>
    string ScreenResolution() =>
        TopLevel.GetTopLevel(this)?.Screens?.Primary?.Bounds is { } bounds ? $"{bounds.Width}x{bounds.Height}" : "";

    #endregion

    #region Build and platform

    /// <summary>
    /// The game releases GitHub lists, newest first.
    /// </summary>
    List<Release> _releases = [];

    /// <summary>
    /// The tags of the target builds; null until loaded (or when the service could not be reached).
    /// </summary>
    HashSet<string>? _targets;

    /// <summary>
    /// Why the targets are not known, null while loading or once loaded.
    /// </summary>
    string? _targetsProblem;

    /// <summary>
    /// The chosen build, null when none is listed.
    /// </summary>
    Release? _release;

    /// <summary>
    /// The ways to run the benchmark here.
    /// </summary>
    List<BenchmarkPlatform> _platforms = [];

    /// <summary>
    /// The chosen platform.
    /// </summary>
    int _platformIndex;

    /// <summary>
    /// The chosen platform.
    /// </summary>
    BenchmarkPlatform Platform => _platforms[_platformIndex];

    /// <summary>
    /// Whether the chosen build is a target, so its sessions may be submitted.
    /// </summary>
    bool IsTarget => _release is { } release && _targets?.Contains(release.Tag) == true;

    /// <summary>
    /// The window's game releases; the chosen one kept, else the newest target (the newest release while the targets are unknown).
    /// </summary>
    void TakeReleases()
    {
        _releases = [.. _host!.Releases().Where(release => release.Kind == ReleaseKind.Game)];
        _release = _releases.FirstOrDefault(release => release.Tag == _release?.Tag)
            ?? _releases.FirstOrDefault(release => _targets?.Contains(release.Tag) == true)
            ?? _releases.FirstOrDefault();
    }

    /// <summary>
    /// Asks the service for the targets, then selects the newest when no target is chosen.
    /// </summary>
    async Task LoadTargetsAsync()
    {
        try
        {
            var targets = await _service!.TargetsAsync(CancellationToken.None);
            _targets = [.. targets.Select(target => target.Tag)];
            _targetsProblem = null;
            if (!IsTarget && !Running) _release = null;
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
        {
            _targetsProblem = Tr("Target builds unknown: {1}", e.Message);
        }
        if (!Running) TakeReleases();
        Rebuild();
    }

    /// <summary>
    /// The build choice: each game release, marked target or not; Install is shown beside it only when the chosen build is not installed yet.
    /// </summary>
    Border BuildRow()
    {
        var box = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, IsEnabled = !Running };
        foreach (var release in _releases) box.Items.Add(new ComboBoxItem { Content = BuildLabel(release), Tag = release });
        box.SelectedIndex = _release is null ? -1 : _releases.IndexOf(_release);
        box.SelectionChanged += (_, _) =>
        {
            if (box.SelectedItem is not ComboBoxItem { Tag: Release release } || release == _release) return;
            _release = release;
            Dispatcher.UIThread.Post(Rebuild);
        };

        Control control = box;
        if (_release is not null && Platform.FindGame(_release.Version) is null)
        {
            var install = BenchmarkLayout.Button(Tr("Install"), () => _ = InstallAsync());
            install.IsEnabled = !Running;
            ToolTip.SetTip(install, Tr("Download the build into the benchmark's own folder and list its benchmarks"));
            var dock = new DockPanel();
            DockPanel.SetDock(install, Dock.Right);
            dock.Children.Add(install);
            dock.Children.Add(box);
            control = dock;
        }
        string description = Tr("Target builds (full releases and the daily nightlies) count for comparisons; others can be run but not submitted.");
        return BenchmarkLayout.Row(Tr("Build"), description, control, ControlWidth);
    }

    /// <summary>
    /// A release in the build choice.
    /// </summary>
    string BuildLabel(Release release)
    {
        string kind = _targets is null ? "" : _targets.Contains(release.Tag) ? Tr("target") : Tr("not a target");
        string installed = _platforms.Count > 0 && Platform.FindGame(release.Version) is not null ? Tr("installed") : "";
        string notes = string.Join(", ", new[] { kind, installed }.Where(part => part.Length > 0));
        return notes.Length == 0 ? release.Title : Tr("{1} ({2})", release.Title, notes);
    }

    /// <summary>
    /// The platform choice (Linux with a Proton found): the native build or the Windows build under a Proton.
    /// </summary>
    Border PlatformRow()
    {
        var box = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, IsEnabled = !Running };
        foreach (var platform in _platforms) box.Items.Add(new ComboBoxItem { Content = platform.Label });
        box.SelectedIndex = _platformIndex;
        box.SelectionChanged += (_, _) =>
        {
            if (box.SelectedIndex < 0 || box.SelectedIndex == _platformIndex) return;
            _platformIndex = box.SelectedIndex;
            Dispatcher.UIThread.Post(Rebuild);
        };
        string description = Tr("Proton runs the Windows build, with its own prefix in the launcher's folder.");
        return BenchmarkLayout.Row(Tr("Platform"), description, box, ControlWidth);
    }

    #endregion

    #region Scenarios and tiers

    /// <summary>
    /// The ticked scenarios, by id.
    /// </summary>
    readonly HashSet<string> _scenarios = [BenchmarkScenario.Default];

    /// <summary>
    /// The ticked tiers.
    /// </summary>
    readonly HashSet<string> _tiers = [BenchmarkTiers.Default];

    /// <summary>
    /// Whether the player changed the tiers, so a late recommendation does not tick one.
    /// </summary>
    bool _tiersChanged;

    /// <summary>
    /// The tier the hardware detection recommends, null before it answered (or without a game to ask).
    /// </summary>
    string? _recommended;

    /// <summary>
    /// The chosen build's benchmarks, null while it is not installed (or did not list them).
    /// </summary>
    List<BenchmarkScenario>? Listed => _release is null ? null : BenchmarkScenario.ReadList(Platform.BuildFolder(_release.Version));

    /// <summary>
    /// A check box per benchmark the build lists (the ones this launcher knows before it listed them), with its short name, estimate and subtitle.
    /// </summary>
    Border ScenarioRow()
    {
        var listed = Listed;
        var ids = listed?.Select(scenario => scenario.Id).ToList() ?? [.. BenchmarkScenario.Known];
        var boxes = new StackPanel { Spacing = 6 };
        foreach (string id in ids)
        {
            var listedScenario = listed?.FirstOrDefault(s => s.Id == id);
            boxes.Children.Add(ScenarioOption(id, listedScenario));
        }
        return BenchmarkLayout.Row(Tr("Benchmarks"), Tr("Each ticked benchmark is run at each ticked tier."), boxes, ControlWidth);
    }

    /// <summary>
    /// One scenario checkbox showing its short name, optional duration per tier and description.
    /// </summary>
    CheckBox ScenarioOption(string id, BenchmarkScenario? listed)
    {
        var heading = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        heading.Children.Add(new TextBlock { Text = BenchmarkScenario.ShortLabel(id), VerticalAlignment = VerticalAlignment.Center });
        if (listed is not null)
        {
            int mins = (int)Math.Ceiling(listed.RunMinutes * BenchmarkTiers.RunsPerTier);
            heading.Children.Add(new TextBlock
            {
                Classes = { "desc" }, Text = Tr("~{1} min per tier", mins), VerticalAlignment = VerticalAlignment.Center,
            });
        }

        var content = new StackPanel { Spacing = 1 };
        content.Children.Add(heading);
        string desc = BenchmarkScenario.Description(id);
        if (desc.Length > 0) content.Children.Add(BenchmarkLayout.Description(desc));

        var box = new CheckBox { Content = content, IsChecked = _scenarios.Contains(id), IsEnabled = !Running };
        box.IsCheckedChanged += (_, _) => Toggle(_scenarios, id, box.IsChecked == true);
        return box;
    }

    /// <summary>
    /// A check box per tier (Ultra and Extreme greyed when the last probe found no ray queries) plus quick Recommended / All links.
    /// </summary>
    Border TierRow()
    {
        bool noRayQueries = _host!.Config.Benchmark.RayQueries() == false;
        var boxes = new WrapPanel { ItemSpacing = 12, LineSpacing = 6 };
        foreach (string tier in BenchmarkTiers.All)
        {
            bool blocked = noRayQueries && BenchmarkTiers.NeedsRayQueries(tier);
            var box = Tick(BenchmarkTiers.Label(tier), _tiers.Contains(tier) && !blocked, on =>
            {
                _tiersChanged = true;
                Toggle(_tiers, tier, on);
            });
            box.IsEnabled = !blocked && !Running;
            if (blocked) ToolTip.SetTip(box, Tr("Needs ray tracing"));
            else if (tier == _recommended) ToolTip.SetTip(box, Tr("Recommended for this computer"));
            boxes.Children.Add(box);
        }

        var quick = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new(0, 4, 0, 0) };
        quick.Children.Add(QuickLink(Tr("Recommended"), () => SelectTiers(all: false, noRayQueries)));
        quick.Children.Add(QuickLink(Tr("All"), () => SelectTiers(all: true, noRayQueries)));

        var right = new StackPanel { Children = { boxes, quick } };
        string description = _recommended is { } recommended
            ? Tr("Medium and {1}, recommended for this computer, are ticked first.", BenchmarkTiers.Label(recommended))
            : Tr("The game's quality presets.");
        return BenchmarkLayout.Row(Tr("Tiers"), description, right, ControlWidth);
    }

    /// <summary>
    /// A subtle text link button disabled while running.
    /// </summary>
    Button QuickLink(string caption, Action click)
    {
        var link = new Button { Classes = { "link" }, Content = new TextBlock { Text = caption }, IsEnabled = !Running };
        link.Click += (_, _) => click();
        return link;
    }

    /// <summary>
    /// Ticks either every available tier or the recommended defaults (Medium + recommended).
    /// </summary>
    void SelectTiers(bool all, bool noRayQueries)
    {
        _tiersChanged = true;
        _tiers.Clear();
        if (all)
        {
            foreach (string tier in BenchmarkTiers.All)
                if (!noRayQueries || !BenchmarkTiers.NeedsRayQueries(tier)) _tiers.Add(tier);
        }
        else
        {
            _tiers.Add(BenchmarkTiers.Default);
            if (_recommended is { } tier && (!noRayQueries || !BenchmarkTiers.NeedsRayQueries(tier))) _tiers.Add(tier);
        }
        Rebuild();
    }

    /// <summary>
    /// A check box captioned <paramref name="caption"/> that reports its state to <paramref name="changed"/>.
    /// </summary>
    CheckBox Tick(string caption, bool ticked, Action<bool> changed)
    {
        var box = new CheckBox { Content = caption, IsChecked = ticked, IsEnabled = !Running };
        box.IsCheckedChanged += (_, _) => changed(box.IsChecked == true);
        return box;
    }

    /// <summary>
    /// Adds or removes <paramref name="item"/>, then shows the new estimate.
    /// </summary>
    void Toggle(HashSet<string> set, string item, bool on)
    {
        if (on) set.Add(item);
        else set.Remove(item);
        BenchmarkLayout.SetLine(_estimate, EstimateText());
    }

    /// <summary>
    /// Asks the game Play starts which preset suits this computer (<c>--recommend-preset</c>, as the Graphics page's Detect, without saving
    /// it) and ticks that tier unless the player changed the tiers.
    /// </summary>
    async Task RecommendAsync()
    {
        if (_host!.DetectGame() is not var (exe, flag) || _host.GameRunning()) return;

        Directory.CreateDirectory(BenchmarkFolders.Root);
        string file = Path.Combine(BenchmarkFolders.Root, "recommend.json");
        var start = new ProcessStartInfo(exe)
        {
            WorkingDirectory = LauncherConfig.DataDirectory, UseShellExecute = false, CreateNoWindow = true, ArgumentList = { flag, file },
        };
        try
        {
            File.Delete(file);
            await GameProbe.RunAsync(start, TimeSpan.FromSeconds(60));
            _recommended = BenchmarkTiers.FromPreset((await GameProbe.ReadPresetAsync(file)).Preset);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException
            or InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception)
        {
            return;
        }
        if (_recommended is { } tier && !_tiersChanged && !Running) _tiers.Add(tier);
        Rebuild();
    }

    #endregion

    #region Running

    /// <summary>
    /// Ticks once a second while a game runs, for the elapsed time.
    /// </summary>
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

    /// <summary>
    /// Cancels the running Start or Install, null when none runs.
    /// </summary>
    CancellationTokenSource? _cancel;

    /// <summary>
    /// The step now running, the run's line and when the run started (null between runs).
    /// </summary>
    string? _step, _runText;

    /// <summary>
    /// When the running launch started, null between launches.
    /// </summary>
    DateTime? _runStarted;

    /// <summary>
    /// The current progress bar value (0..1) for either a download or launches in progress; null hides the bar.
    /// </summary>
    double? _progressValue;

    /// <summary>
    /// The notices and the result of the last Start, newest last.
    /// </summary>
    readonly List<string> _notices = [];

    /// <summary>
    /// The estimate line, the step line, the run line, the notices, the progress card and the progress bar of the page as last built.
    /// </summary>
    TextBlock _estimate = new(), _stepLine = new(), _runLine = new();

    /// <summary>
    /// The notices' panel of the page as last built.
    /// </summary>
    StackPanel _noticePanel = new();

    /// <summary>
    /// The framed card holding progress and notices, hidden while idle with no notices.
    /// </summary>
    Border _progressCard = new();

    /// <summary>
    /// The download / launch progress bar of the page as last built.
    /// </summary>
    ProgressBar _progress = new();

    /// <summary>
    /// Whether a Start or an Install runs.
    /// </summary>
    public bool Running => _cancel is not null;

    /// <summary>
    /// The window is closing: the running game is killed and the Start cancelled.
    /// </summary>
    public void Stop()
    {
        _cancel?.Cancel();
        _runner?.Kill();
    }

    /// <summary>
    /// Start (or Cancel while running) beside the estimate, warning and auto-submit preference.
    /// </summary>
    Border RunRow()
    {
        var button = new Button { Content = Running ? Tr("Cancel") : Tr("Start"), Classes = { "paper" }, MinWidth = 200 };
        button.Click += (_, _) =>
        {
            if (_cancel is { } running) running.Cancel();
            else _ = StartAsync();
        };
        _estimate = BenchmarkLayout.Description(EstimateText());

        var autoSubmit = new CheckBox
        {
            Content = new TextBlock { Classes = { "desc" }, Text = Tr("Submit results automatically when done") },
            IsChecked = _host!.Config.Benchmark.AutoSubmit ?? true,
            IsEnabled = !Running,
            Margin = new(0, 4, 0, 0),
        };
        if (!IsTarget) ToolTip.SetTip(autoSubmit, Tr("Applies to target builds only."));
        autoSubmit.IsCheckedChanged += (_, _) =>
        {
            _host.Config.Benchmark.AutoSubmit = autoSubmit.IsChecked == true;
            _host.Config.Save();
        };

        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 16, 0) };
        texts.Children.Add(new TextBlock { Classes = { "name" }, Text = Tr("Run") });
        texts.Children.Add(_estimate);
        texts.Children.Add(new TextBlock { Classes = { "warn" }, Text = Tr("Do not use the computer while it runs."), Margin = new(0, 4, 0, 0) });
        texts.Children.Add(autoSubmit);

        var grid = new Grid { ColumnDefinitions = [new ColumnDefinition(1, GridUnitType.Star), new ColumnDefinition(GridLength.Auto)] };
        grid.Children.Add(texts);
        Grid.SetColumn(button, 1);
        button.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(button);
        return new Border { Classes = { "row" }, Child = grid };
    }

    /// <summary>
    /// About how long the ticked sessions take: two runs each, the build's seconds per benchmark (eight minutes a run while unknown).
    /// </summary>
    string EstimateText()
    {
        int sessions = _scenarios.Count * _tiers.Count;
        if (sessions == 0) return Tr("Tick at least one benchmark and one tier.");

        var listed = Listed;
        double perTier = _scenarios.Sum(id => listed?.FirstOrDefault(scenario => scenario.Id == id)?.RunMinutes ?? BenchmarkTiers.MinutesPerRun);
        int minutes = (int)Math.Ceiling(perTier * _tiers.Count * BenchmarkTiers.RunsPerTier);
        return Tr("Sessions: {1}, two runs each, about {2} minutes.", sessions, minutes);
    }

    /// <summary>
    /// The framed status card: current step, running launch with elapsed time, progress bar and notices (hidden when empty).
    /// </summary>
    Border ProgressBlock()
    {
        _stepLine = BenchmarkLayout.Line("name");
        _runLine = BenchmarkLayout.Line();
        _progress = new ProgressBar { Minimum = 0, Maximum = 1, HorizontalAlignment = HorizontalAlignment.Stretch };
        _noticePanel = new StackPanel { Spacing = 4 };
        BenchmarkLayout.SetLine(_stepLine, _step);
        ShowElapsed();
        ShowProgress();
        foreach (string notice in _notices) _noticePanel.Children.Add(NoticeLine(notice));

        var stack = new StackPanel { Spacing = 6, Children = { _stepLine, _runLine, _progress, _noticePanel } };
        _progressCard = new Border { Classes = { "post" }, Margin = new(0, 12, 0, 0), Padding = new(16, 12), Child = stack };
        UpdateProgressVisibility();
        return _progressCard;
    }

    /// <summary>
    /// Shows the progress card only while a task runs or notices are displayed.
    /// </summary>
    void UpdateProgressVisibility() =>
        _progressCard.IsVisible = Running || _step is not null || _runText is not null || _notices.Count > 0;

    /// <summary>
    /// A notice as a line of the notices' panel.
    /// </summary>
    static TextBlock NoticeLine(string notice)
    {
        var line = BenchmarkLayout.Line("warn");
        BenchmarkLayout.SetLine(line, notice);
        return line;
    }

    /// <summary>
    /// The run line with the running launch's elapsed time.
    /// </summary>
    void ShowElapsed()
    {
        string? text = _runText is null ? null
            : _runStarted is { } started ? Tr("{1} ({2} elapsed)", _runText, (DateTime.UtcNow - started).ToString(@"m\:ss"))
            : _runText;
        BenchmarkLayout.SetLine(_runLine, text);
    }

    /// <summary>
    /// The progress bar, hidden when no fraction is reported.
    /// </summary>
    void ShowProgress()
    {
        _progress.IsVisible = _progressValue is not null;
        _progress.Value = _progressValue ?? 0;
    }

    /// <summary>
    /// Shows the sessions of the chosen build and platform.
    /// </summary>
    void ShowResults() => _results!.Show(_release?.Version, _platforms.Count > 0 ? Platform.Wire : "", IsTarget, Running);

    /// <summary>
    /// Runs the ticked sessions; asks once before the first target benchmark whether to submit results automatically on completion.
    /// </summary>
    async Task StartAsync()
    {
        if (Problem(requireSelection: true) is { } problem)
        {
            Say(problem);
            return;
        }
        if (_host!.Config.Benchmark.AutoSubmit is null && IsTarget && !await AskAutoSubmitAsync()) return;

        var job = Job();
        var scenarios = _scenarios.ToList();
        bool target = IsTarget;
        _results!.ClearAnswers(job.Release.Version, job.Platform.Wire);

        await GuardAsync(async ct =>
        {
            await _runner!.RunAsync(job with { Scenarios = scenarios, Tiers = OrderedTiers() }, this, ct);
            if ((_host.Config.Benchmark.AutoSubmit ?? true) && target)
            {
                string? summary = await _results.SubmitPendingAsync(job.Release.Version, job.Platform.Wire, target,
                    step => ((IBenchmarkView)this).Step(step), ct);
                if (summary is not null) ((IBenchmarkView)this).Notice(summary);
            }
        }, Tr("Benchmark finished."));
    }

    /// <summary>
    /// First-time prompt before starting a target benchmark: asks whether to submit results automatically when finished and lets the player set
    /// an optional handle. Returns false when cancelled so Start does not run.
    /// </summary>
    async Task<bool> AskAutoSubmitAsync()
    {
        var settings = _host!.Config.Benchmark;
        var handleBox = new TextBox
        {
            Text = settings.Handle, PlaceholderText = Tr("Your name on the benchmarks site (optional)"),
        };
        Control[] body =
        [
            new TextBlock
            {
                Text = Tr("Submit your results to the Meitou benchmarks site automatically when the benchmark finishes? Hardware specs, timings and your optional handle are sent; your computer name is not."),
                TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.Medium,
            },
            new StackPanel
            {
                Spacing = 4, Margin = new(0, 12, 0, 0),
                Children =
                {
                    new TextBlock { Classes = { "desc" }, Text = Tr("Submitted by") },
                    handleBox,
                },
            },
            new TextBlock
            {
                Classes = { "desc" }, Margin = new(0, 8, 0, 0), Text = Tr("You can change this anytime on the Benchmark page."),
            },
        ];

        var choices = new (string Label, bool? Value)[]
        {
            (Tr("Cancel"), null), (Tr("Don't submit"), false), (Tr("Submit"), true),
        };
        bool? answer = await _host.Choose(Tr("Submit benchmark results?"), body, choices);
        if (answer is null) return false;

        settings.AutoSubmit = answer.Value;
        string typedHandle = handleBox.Text?.Trim() ?? "";
        if (typedHandle.Length > 0 || settings.Handle.Length > 0) settings.Handle = typedHandle;
        _host.Config.Save();
        return true;
    }

    /// <summary>
    /// Installs the chosen build and lists its benchmarks.
    /// </summary>
    async Task InstallAsync()
    {
        if (Problem(requireSelection: false) is { } problem)
        {
            Say(problem);
            return;
        }
        var job = Job();
        await GuardAsync(ct => _runner!.PrepareBuildAsync(job, this, ct), Tr("The build is installed."));
    }

    /// <summary>
    /// The job for the chosen build and platform, without scenarios or tiers.
    /// </summary>
    BenchmarkJob Job() => new(_release!, Platform, [], [], _host!.Kenshi()!);

    /// <summary>
    /// The ticked tiers, cheapest first.
    /// </summary>
    List<string> OrderedTiers() => [.. BenchmarkTiers.All.Where(_tiers.Contains)];

    /// <summary>
    /// Why nothing can start now, null when it can. <paramref name="requireSelection"/> is false for Install, which lists the build's
    /// benchmarks before the player has to tick any.
    /// </summary>
    string? Problem(bool requireSelection)
    {
        if (Running) return Tr("A benchmark is already running.");
        if (_results?.Submitting == true) return Tr("Wait for the current submission to finish.");
        if (_host!.Kenshi() is null) return Tr("Set the Kenshi folder first (Settings).");
        if (_host.GameRunning()) return Tr("Meitou is running: close it before benchmarking.");
        if (_release is null) return Tr("Choose a build first.");
        if (requireSelection && (_scenarios.Count == 0 || _tiers.Count == 0)) return Tr("Tick at least one benchmark and one tier.");
        return null;
    }

    /// <summary>
    /// Runs <paramref name="work"/> as the page's one running task: Cancel offered, its failure or <paramref name="done"/> said at the end.
    /// </summary>
    async Task GuardAsync(Func<CancellationToken, Task> work, string done)
    {
        _cancel = new CancellationTokenSource();
        _notices.Clear();
        Rebuild();
        // Unexpected errors still end the task (finally), so the page and Play are never left waiting on it.
        string last = Tr("The benchmark stopped.");
        try
        {
            await work(_cancel.Token);
            last = done;
        }
        catch (OperationCanceledException)
        {
            last = Tr("Cancelled.");
        }
        catch (BenchmarkStoppedException e)
        {
            last = e.Message;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or UnauthorizedAccessException or InvalidDataException
            or InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception)
        {
            last = Tr("The benchmark stopped: {1}", e.Message);
        }
        finally
        {
            Finish(last);
        }
    }

    /// <summary>
    /// The running task ended with <paramref name="last"/>: the controls back, the results shown.
    /// </summary>
    void Finish(string last)
    {
        _cancel?.Dispose();
        _cancel = null;
        _timer.Stop();
        (_step, _runText, _runStarted, _progressValue) = (null, null, null, null);
        _notices.Add(last);
        Rebuild();
    }

    /// <summary>
    /// Shows <paramref name="text"/> as the only notice.
    /// </summary>
    void Say(string text)
    {
        _notices.Clear();
        _notices.Add(text);
        Rebuild();
    }

    #endregion

    #region Runner reports

    /// <summary>
    /// A step began: shown, the progress bar hidden until a download or launch reports progress.
    /// </summary>
    void IBenchmarkView.Step(string text)
    {
        (_step, _runText, _runStarted, _progressValue) = (text, null, null, null);
        _timer.Stop();
        BenchmarkLayout.SetLine(_stepLine, text);
        ShowElapsed();
        ShowProgress();
        UpdateProgressVisibility();
    }

    /// <summary>
    /// A download's progress on the bar.
    /// </summary>
    void IBenchmarkView.Download(long done, long total)
    {
        _progressValue = total > 0 ? (double)done / total : 0;
        ShowProgress();
    }

    /// <summary>
    /// The build listed its benchmarks: the page shows them.
    /// </summary>
    void IBenchmarkView.ScenariosListed(IReadOnlyList<BenchmarkScenario> scenarios) => Rebuild();

    /// <summary>
    /// A launch started: its line, launch progress bar and elapsed timer ticking.
    /// </summary>
    void IBenchmarkView.RunStarted(BenchmarkScenario scenario, string tier, int run, int index, int count)
    {
        string name = BenchmarkScenario.ShortLabel(scenario.Id), tierName = BenchmarkTiers.Label(tier);
        _step = Tr("Benchmarking: the game window opens and closes by itself.");
        _runText = Tr("{1} at {2}: run {3} of {4}, launch {5} of {6}", name, tierName, run, BenchmarkTiers.RunsPerTier, index, count);
        _runStarted = DateTime.UtcNow;
        _progressValue = count > 0 ? (double)(index - 1) / count : 0;
        BenchmarkLayout.SetLine(_stepLine, _step);
        ShowElapsed();
        ShowProgress();
        UpdateProgressVisibility();
        _timer.Start();
    }

    /// <summary>
    /// A notice added under the progress.
    /// </summary>
    void IBenchmarkView.Notice(string text)
    {
        _notices.Add(text);
        _noticePanel.Children.Add(NoticeLine(text));
        UpdateProgressVisibility();
    }

    /// <summary>
    /// The probed hardware kept in <c>launcher.json</c> (the ray tiers are greyed and the default machine id filled from it).
    /// </summary>
    void IBenchmarkView.HardwareProbed(JsonObject hardware)
    {
        _host!.Config.Benchmark.Hardware = hardware.ToJsonString();
        _results!.EnsureMachineId();
        _host.Config.Save();
    }

    /// <summary>
    /// A session ended: the results shown again.
    /// </summary>
    void IBenchmarkView.SessionDone() => ShowResults();

    #endregion
}
