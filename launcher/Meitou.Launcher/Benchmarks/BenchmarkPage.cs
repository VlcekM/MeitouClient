using System.Diagnostics;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using Meitou.Content;

namespace Meitou.Launcher.Benchmarks;

/// <summary>
/// What the Benchmark page takes from the window: the shared HTTP client, installer and settings, the release list, the Kenshi folder, the
/// game Play would start with its preset detection flag (for the recommended tier), and whether that game runs.
/// </summary>
sealed record BenchmarkHost(HttpClient Http, ReleaseInstaller Installer, LauncherConfig Config, Func<IReadOnlyList<Release>> Releases,
    Func<GameInstall?> Kenshi, Func<(string Exe, string Flag)?> DetectGame, Func<bool> GameRunning);

/// <summary>
/// The Benchmark page (docs/launcher.md "Benchmark"): choose a build, benchmarks, tiers and platform, run them (two launches per session) and
/// submit the sessions to the reports service. Built in code and in the launcher's language whenever it is built, so the window's
/// translation walk leaves it alone (<see cref="LauncherText"/>).
/// </summary>
public sealed class BenchmarkPage : UserControl, IBenchmarkView
{
    #region Page

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
    /// The sessions and the Submit buttons.
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
    /// The page's whole content: the header, the choices, the run controls and the results.
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

        // The results panel keeps its answers across rebuilds, so it moves from the old content to the new.
        if (_results!.Parent is Panel old) old.Children.Remove(_results);
        rows.Children.Add(_results);
        Content = new ScrollViewer { Classes = { "page" }, Content = rows };
        ShowResults();
    }

    /// <summary>
    /// The page's title bar, with the targets' state at its right.
    /// </summary>
    Border Header()
    {
        string aside = _targets is null ? _targetsProblem ?? Tr("Loading the target builds...") : Tr("{1} target builds", _targets.Count);
        var dock = new DockPanel();
        var right = new TextBlock { Classes = { "aside" }, Text = aside };
        DockPanel.SetDock(right, Dock.Right);
        dock.Children.Add(right);
        dock.Children.Add(new TextBlock { Text = Tr("Benchmark") });
        return new Border { Classes = { "header" }, Margin = new(0, 0, 0, 10), Child = dock };
    }

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
    /// The build choice: each game release, marked target or not; Install fetches the chosen one and its benchmark list.
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

        var install = BenchmarkLayout.Button(Tr("Install"), () => _ = InstallAsync());
        install.IsEnabled = !Running && _release is not null && Platform.FindGame(_release.Version) is null;
        ToolTip.SetTip(install, Tr("Download the build into the benchmark's own folder and list its benchmarks"));
        var dock = new DockPanel();
        DockPanel.SetDock(install, Dock.Right);
        dock.Children.Add(install);
        dock.Children.Add(box);
        string description = Tr("Target builds (full releases and the daily nightlies) count for comparisons; others can be run but not submitted.");
        return BenchmarkLayout.Row(Tr("Build"), description, dock, 420);
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
        return BenchmarkLayout.Row(Tr("Platform"), description, box, 420);
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
    /// A check box per benchmark the build lists (only the default one before it is installed).
    /// </summary>
    Border ScenarioRow()
    {
        var listed = Listed;
        var ids = listed?.Select(scenario => scenario.Id).ToList() ?? [BenchmarkScenario.Default];
        var boxes = new WrapPanel { ItemSpacing = 12, LineSpacing = 6 };
        foreach (string id in ids) boxes.Children.Add(Tick(BenchmarkScenario.Label(id), _scenarios.Contains(id), on => Toggle(_scenarios, id, on)));
        string description = listed is null
            ? Tr("Install the build to see all the benchmarks it has.")
            : Tr("Each ticked benchmark is run at each ticked tier.");
        return BenchmarkLayout.Row(Tr("Benchmarks"), description, boxes, 420);
    }

    /// <summary>
    /// A check box per tier; Ultra and Extreme greyed when the last probe found no ray queries.
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
            boxes.Children.Add(box);
        }
        string description = _recommended is { } recommended
            ? Tr("Medium and {1}, recommended for this computer, are ticked first.", BenchmarkTiers.Label(recommended))
            : Tr("The game's quality presets.");
        return BenchmarkLayout.Row(Tr("Tiers"), description, boxes, 420);
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
    /// The download's share done, null when none runs.
    /// </summary>
    double? _downloaded;

    /// <summary>
    /// The notices and the result of the last Start, newest last.
    /// </summary>
    readonly List<string> _notices = [];

    /// <summary>
    /// The estimate line, the step line, the run line, the notices and the progress bar of the page as last built.
    /// </summary>
    TextBlock _estimate = new(), _stepLine = new(), _runLine = new();

    /// <summary>
    /// The notices' panel of the page as last built.
    /// </summary>
    StackPanel _noticePanel = new();

    /// <summary>
    /// The download's progress bar of the page as last built.
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
    /// Start (or Cancel while running) with the estimate and the warning beside it.
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
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 16, 0) };
        texts.Children.Add(new TextBlock { Classes = { "name" }, Text = Tr("Run") });
        texts.Children.Add(_estimate);
        texts.Children.Add(new TextBlock { Classes = { "warn" }, Text = Tr("Do not use the computer while it runs."), Margin = new(0, 4, 0, 0) });
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
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
        var listed = Listed;
        double perTier = _scenarios.Sum(id => listed?.FirstOrDefault(scenario => scenario.Id == id)?.RunMinutes ?? BenchmarkTiers.MinutesPerRun);
        int minutes = (int)Math.Ceiling(perTier * _tiers.Count * BenchmarkTiers.RunsPerTier);
        int sessions = _scenarios.Count * _tiers.Count;
        return Tr("Sessions: {1}, two runs each, about {2} minutes.", sessions, minutes);
    }

    /// <summary>
    /// The step, the running launch with its elapsed time, the download's progress and the notices.
    /// </summary>
    StackPanel ProgressBlock()
    {
        _stepLine = BenchmarkLayout.Line("name");
        _runLine = BenchmarkLayout.Line();
        _progress = new ProgressBar { Minimum = 0, Maximum = 1, MaxWidth = 420, HorizontalAlignment = HorizontalAlignment.Left };
        _noticePanel = new StackPanel { Spacing = 4 };
        BenchmarkLayout.SetLine(_stepLine, _step);
        ShowElapsed();
        ShowDownload();
        foreach (string notice in _notices) _noticePanel.Children.Add(NoticeLine(notice));
        return new StackPanel { Spacing = 6, Margin = new(0, 10, 0, 0), Children = { _stepLine, _runLine, _progress, _noticePanel } };
    }

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
    /// The download's progress bar, hidden while none runs.
    /// </summary>
    void ShowDownload()
    {
        _progress.IsVisible = _downloaded is not null;
        _progress.Value = _downloaded ?? 0;
    }

    /// <summary>
    /// Shows the sessions of the chosen build and platform.
    /// </summary>
    void ShowResults() => _results!.Show(_release?.Version, _platforms.Count > 0 ? Platform.Wire : "", IsTarget);

    /// <summary>
    /// Runs the ticked sessions.
    /// </summary>
    async Task StartAsync()
    {
        if (Problem() is { } problem)
        {
            Say(problem);
            return;
        }
        var job = Job();
        var scenarios = _scenarios.ToList();
        await GuardAsync(ct => _runner!.RunAsync(job with { Scenarios = scenarios, Tiers = OrderedTiers() }, this, ct), Tr("Benchmark finished."));
    }

    /// <summary>
    /// Installs the chosen build and lists its benchmarks.
    /// </summary>
    async Task InstallAsync()
    {
        if (Problem() is { } problem)
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
    /// Why nothing can start now, null when it can.
    /// </summary>
    string? Problem()
    {
        if (Running) return Tr("A benchmark is already running.");
        if (_host!.Kenshi() is null) return Tr("Set the Kenshi folder first (Settings).");
        if (_host.GameRunning()) return Tr("Meitou is running: close it before benchmarking.");
        if (_release is null) return Tr("Choose a build first.");
        if (_scenarios.Count == 0 || _tiers.Count == 0) return Tr("Tick at least one benchmark and one tier.");
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
        (_step, _runText, _runStarted, _downloaded) = (null, null, null, null);
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
    /// A step began: shown, the download bar hidden until it reports.
    /// </summary>
    void IBenchmarkView.Step(string text)
    {
        (_step, _runText, _runStarted, _downloaded) = (text, null, null, null);
        _timer.Stop();
        BenchmarkLayout.SetLine(_stepLine, text);
        ShowElapsed();
        ShowDownload();
    }

    /// <summary>
    /// A download's progress on the bar.
    /// </summary>
    void IBenchmarkView.Download(long done, long total)
    {
        _downloaded = total > 0 ? (double)done / total : 0;
        ShowDownload();
    }

    /// <summary>
    /// The build listed its benchmarks: the page shows them.
    /// </summary>
    void IBenchmarkView.ScenariosListed(IReadOnlyList<BenchmarkScenario> scenarios) => Rebuild();

    /// <summary>
    /// A launch started: its line, with the elapsed time ticking.
    /// </summary>
    void IBenchmarkView.RunStarted(BenchmarkScenario scenario, string tier, int run, int index, int count)
    {
        string name = BenchmarkScenario.Label(scenario.Id), tierName = BenchmarkTiers.Label(tier);
        _step = Tr("Benchmarking: the game window opens and closes by itself.");
        _runText = Tr("{1} at {2}: run {3} of {4}, launch {5} of {6}", name, tierName, run, BenchmarkTiers.RunsPerTier, index, count);
        _runStarted = DateTime.UtcNow;
        _downloaded = null;
        BenchmarkLayout.SetLine(_stepLine, _step);
        ShowElapsed();
        ShowDownload();
        _timer.Start();
    }

    /// <summary>
    /// A notice added under the progress.
    /// </summary>
    void IBenchmarkView.Notice(string text)
    {
        _notices.Add(text);
        _noticePanel.Children.Add(NoticeLine(text));
    }

    /// <summary>
    /// The probed hardware kept in <c>launcher.json</c> (the ray tiers are greyed from it on).
    /// </summary>
    void IBenchmarkView.HardwareProbed(JsonObject hardware)
    {
        _host!.Config.Benchmark.Hardware = hardware.ToJsonString();
        _host.Config.Save();
    }

    /// <summary>
    /// A session ended: the results shown again.
    /// </summary>
    void IBenchmarkView.SessionDone() => ShowResults();

    #endregion
}
