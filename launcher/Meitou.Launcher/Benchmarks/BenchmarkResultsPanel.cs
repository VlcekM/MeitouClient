using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace Meitou.Launcher.Benchmarks;

/// <summary>
/// The Benchmark page's lower half: scenario and tier series tabs over a single results table, a single Submit button that submits all ready
/// sessions, and the machine record and handle results are submitted with.
/// </summary>
sealed class BenchmarkResultsPanel(BenchmarkService service, LauncherConfig config, Func<string> resolution) : StackPanel
{
    #region Showing

    /// <summary>
    /// Brushes for subtle status indicators on tabs and summary lines.
    /// </summary>
    static readonly IBrush PaperLight = Brush.Parse("#d9ceb8"), BrassLight = Brush.Parse("#c4a062"), QuietBrush = Brush.Parse("#8a8270");

    /// <summary>
    /// What was last shown: the build's version, the platform's wire name, whether the build is a target, and whether a run is active.
    /// </summary>
    (string Version, string Platform, bool Target, bool Running)? _shown;

    /// <summary>
    /// The selected scenario tab, null until chosen or resolved from the shown sessions.
    /// </summary>
    string? _selectedScenario;

    /// <summary>
    /// The selected tier tab within <see cref="_selectedScenario"/>; null shows the All-tiers overview when the scenario has multiple tiers.
    /// </summary>
    string? _selectedTier;

    /// <summary>
    /// Each session's last submit refusal or error (by folder); cleared on success, re-run, or switching target.
    /// </summary>
    readonly Dictionary<string, string> _answers = [];

    /// <summary>
    /// Whether a batch submission is running now.
    /// </summary>
    bool _submitting;

    /// <summary>
    /// Progress line while submitting (e.g. Submitting 2 of 5...), null when idle.
    /// </summary>
    string? _submitProgress;

    /// <summary>
    /// Summary of the last batch submit on the currently shown build and platform, null until one runs.
    /// </summary>
    string? _submitSummary;

    /// <summary>
    /// Updates the single Submit button and its status line as the machine id is edited, without rebuilding the panel.
    /// </summary>
    Action? _updateSubmitState;

    /// <summary>
    /// Whether a submission is in progress.
    /// </summary>
    public bool Submitting => _submitting;

    /// <summary>
    /// Fills the panel for build <paramref name="version"/> on <paramref name="platform"/>; <paramref name="target"/> says whether it may be
    /// submitted and <paramref name="running"/> disables manual submission while a benchmark is active. Null shows nothing (no build chosen).
    /// </summary>
    public void Show(string? version, string platform, bool target, bool running)
    {
        bool changed = _shown is not var (oldVersion, oldPlatform, _, _) || oldVersion != version || oldPlatform != platform;
        _shown = version is null ? null : (version, platform, target, running);
        if (changed)
        {
            _submitSummary = null;
            _selectedTier = null;
        }
        Refill();
    }

    /// <summary>
    /// Drops cached submit answers and batch summary for <paramref name="version"/> on <paramref name="platform"/> before a new run starts, so
    /// re-run sessions never keep an older run's status.
    /// </summary>
    public void ClearAnswers(string version, string platform)
    {
        _submitSummary = null;
        string root = BenchmarkFolders.Scenarios(version, platform);
        foreach (string key in _answers.Keys.Where(folder => folder.StartsWith(root, StringComparison.OrdinalIgnoreCase)).ToList())
            _answers.Remove(key);
    }

    /// <summary>
    /// Ensures a deterministic per-PC machine id exists from the probed hardware (or <paramref name="fallbackHardware"/> from a saved session)
    /// and persists it when newly generated.
    /// </summary>
    public void EnsureMachineId(System.Text.Json.Nodes.JsonObject? fallbackHardware = null)
    {
        var settings = config.Benchmark;
        if (settings.MachineId.Length > 0) return;

        string generated = BenchmarkMachineId.Generate(settings.HardwareRecord() ?? fallbackHardware, settings);
        if (generated.Length == 0) return;

        settings.MachineId = generated;
        config.Save();
    }

    /// <summary>
    /// <paramref name="text"/> in the launcher's language, its holes filled.
    /// </summary>
    static string Tr(string text, params object?[] args) => LauncherText.Translate(text, args);

    /// <summary>
    /// Builds the panel again from <see cref="_shown"/> and the session folders.
    /// </summary>
    void Refill()
    {
        Children.Clear();
        _updateSubmitState = null;
        if (_shown is not var (version, platform, target, running)) return;

        var sessions = BenchmarkSession.List(version, platform);
        Children.Add(ResultsHeader(sessions, version, platform));
        if (sessions.Count == 0)
        {
            Children.Add(BenchmarkLayout.Description(Tr("No results for this build and platform yet.")));
            return;
        }

        var sessionHardware = sessions.Select(s => s.Runs[^1].Hardware).FirstOrDefault(h => h is not null);
        EnsureMachineId(sessionHardware);
        var scenarios = sessions.Select(s => s.Scenario).Distinct(StringComparer.Ordinal).ToList();
        if (_selectedScenario is null || !scenarios.Contains(_selectedScenario)) _selectedScenario = scenarios[0];

        var inScenario = sessions.Where(s => s.Scenario == _selectedScenario).ToList();
        if (inScenario.Count == 1) _selectedTier = inScenario[0].Tier;
        else if (_selectedTier is not null && !inScenario.Any(s => s.Tier == _selectedTier)) _selectedTier = null;

        Children.Add(SeriesBar(scenarios, inScenario, version, target));
        Children.Add(ResultsTable(inScenario, version, target));
        Children.Add(SubmissionCard(sessions, version, platform, target, running, sessionHardware));
    }

    /// <summary>
    /// The Results heading with the session counts and an Open folder button.
    /// </summary>
    Border ResultsHeader(IReadOnlyList<BenchmarkSession> sessions, string version, string platform)
    {
        var dock = new DockPanel();
        if (sessions.Count > 0)
        {
            var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
            right.Children.Add(new TextBlock
            {
                Classes = { "aside" }, Text = SummaryAside(sessions), VerticalAlignment = VerticalAlignment.Center,
            });
            var open = new Button { Classes = { "aside" }, Content = Tr("Open folder") };
            ToolTip.SetTip(open, Tr("Open this build's benchmark results folder"));
            open.Click += async (_, _) => await OpenFolderAsync(BenchmarkFolders.Scenarios(version, platform));
            right.Children.Add(open);
            DockPanel.SetDock(right, Dock.Right);
            dock.Children.Add(right);
        }
        dock.Children.Add(new TextBlock { Text = Tr("Results") });
        return new Border { Classes = { "header" }, Margin = new(0, 18, 0, 12), Child = dock };
    }

    /// <summary>
    /// Concise counts for the Results header's right side.
    /// </summary>
    static string SummaryAside(IReadOnlyList<BenchmarkSession> sessions)
    {
        int submitted = sessions.Count(s => s.SubmittedId is not null);
        if (submitted == 0) return sessions.Count == 1 ? Tr("1 session") : Tr("{1} sessions", sessions.Count);
        if (submitted == sessions.Count)
            return sessions.Count == 1 ? Tr("1 session · submitted") : Tr("{1} sessions · all submitted", sessions.Count);
        return Tr("{1} of {2} submitted", submitted, sessions.Count);
    }

    /// <summary>
    /// Opens <paramref name="folder"/> in the system file manager when it exists.
    /// </summary>
    async Task OpenFolderAsync(string folder)
    {
        if (!Directory.Exists(folder) || TopLevel.GetTopLevel(this) is not { } top) return;
        await top.Launcher.LaunchUriAsync(new Uri(folder + Path.DirectorySeparatorChar));
    }

    #endregion

    #region Series tabs and table

    /// <summary>
    /// Scenario tabs (when present) and tier tabs over the single results table, plus the active session's status on the right.
    /// </summary>
    StackPanel SeriesBar(IReadOnlyList<string> scenarios, IReadOnlyList<BenchmarkSession> inScenario, string version, bool target)
    {
        var bar = new StackPanel { Spacing = 8, Margin = new(0, 0, 0, 10) };
        if (scenarios.Count > 1)
        {
            var scenarioTabs = new WrapPanel { ItemSpacing = 6, LineSpacing = 6 };
            foreach (string id in scenarios) scenarioTabs.Children.Add(ScenarioTab(id));
            bar.Children.Add(scenarioTabs);
        }
        else
        {
            string desc = BenchmarkScenario.Description(scenarios[0]);
            var heading = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            heading.Children.Add(new TextBlock
            {
                Classes = { "name" }, Text = BenchmarkScenario.ShortLabel(scenarios[0]), VerticalAlignment = VerticalAlignment.Center,
            });
            if (desc.Length > 0)
            {
                heading.Children.Add(new TextBlock
                {
                    Classes = { "desc" }, Text = desc, VerticalAlignment = VerticalAlignment.Center,
                });
            }
            bar.Children.Add(heading);
        }

        var tierDock = new DockPanel();
        if (_selectedTier is not null && inScenario.FirstOrDefault(s => s.Tier == _selectedTier) is { } selected)
        {
            var (status, warn) = SessionStatus(selected, version, target);
            var badge = new TextBlock
            {
                Text = status, FontSize = 12, FontWeight = FontWeight.Medium,
                Foreground = warn ? BrassLight : QuietBrush, VerticalAlignment = VerticalAlignment.Center,
                Margin = new(12, 0, 0, 0),
            };
            DockPanel.SetDock(badge, Dock.Right);
            tierDock.Children.Add(badge);
        }

        var tierTabs = new WrapPanel { ItemSpacing = 6, LineSpacing = 6 };
        if (inScenario.Count > 1) tierTabs.Children.Add(TierTab(Tr("All tiers"), null, null, null));
        foreach (var session in inScenario)
        {
            var (tip, warn) = SessionStatus(session, version, target);
            string? mark = session.SubmittedId is not null ? "✓" : warn ? "!" : null;
            tierTabs.Children.Add(TierTab(BenchmarkTiers.Label(session.Tier), session.Tier, mark, tip));
        }
        tierDock.Children.Add(tierTabs);
        bar.Children.Add(tierDock);
        return bar;
    }

    /// <summary>
    /// One scenario tab button; switching scenarios keeps the same tier when available, else returns to All tiers.
    /// </summary>
    ToggleButton ScenarioTab(string id)
    {
        var tab = new ToggleButton { Classes = { "tab" }, Content = BenchmarkScenario.ShortLabel(id), IsChecked = id == _selectedScenario };
        string desc = BenchmarkScenario.Description(id);
        if (desc.Length > 0) ToolTip.SetTip(tab, desc);
        tab.Click += (_, _) =>
        {
            _selectedScenario = id;
            Refill();
        };
        return tab;
    }

    /// <summary>
    /// One tier tab button (or All tiers when <paramref name="tier"/> is null) with an optional compact status mark.
    /// </summary>
    ToggleButton TierTab(string label, string? tier, string? mark, string? tip)
    {
        Control content = mark is null
            ? new TextBlock { Text = label }
            : new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 6,
                Children =
                {
                    new TextBlock { Text = label },
                    new TextBlock { Text = mark, FontSize = 11, Foreground = BrassLight, VerticalAlignment = VerticalAlignment.Center },
                },
            };
        var tab = new ToggleButton { Classes = { "tab" }, Content = content, IsChecked = tier == _selectedTier };
        if (tip is not null) ToolTip.SetTip(tab, tip);
        tab.Click += (_, _) =>
        {
            _selectedTier = tier;
            Refill();
        };
        return tab;
    }

    /// <summary>
    /// The single results table: either the All-tiers overview or the selected tier's per-group breakdown.
    /// </summary>
    Border ResultsTable(IReadOnlyList<BenchmarkSession> inScenario, string version, bool target)
    {
        if (_selectedTier is null && inScenario.Count > 1)
        {
            return BenchmarkTable.ForOverview(inScenario, session => SessionStatus(session, version, target), tier =>
            {
                _selectedTier = tier;
                Refill();
            });
        }
        var active = inScenario.FirstOrDefault(s => s.Tier == _selectedTier) ?? inScenario[0];
        return BenchmarkTable.ForSession(active);
    }

    /// <summary>
    /// A concise status label for <paramref name="session"/> (for overview rows, tab tooltips and the active session badge).
    /// </summary>
    (string Text, bool Warn) SessionStatus(BenchmarkSession session, string version, bool target)
    {
        if (_answers.TryGetValue(session.Folder, out string? error)) return (error, true);
        if (session.SubmittedId is { } id) return (Tr("Submitted (#{1})", id), false);
        if (!session.Complete) return (Tr("Incomplete ({1} of {2} runs)", session.Runs.Count, BenchmarkTiers.RunsPerTier), true);
        if (session.Runs.FirstOrDefault(run => run.GameVersion != version) is { } other)
            return (Tr("Built by {1}, not {2}", other.GameVersion, version), true);
        if (!target) return (Tr("Not a target build"), false);
        return (Tr("Ready to submit"), false);
    }

    #endregion

    #region Submit and machine card

    /// <summary>
    /// The single Submit action bar above the machine record and Submitted by handle, framed as one card under the table.
    /// </summary>
    Border SubmissionCard(IReadOnlyList<BenchmarkSession> sessions, string version, string platform, bool target, bool running,
        System.Text.Json.Nodes.JsonObject? fallbackHardware)
    {
        var card = new StackPanel { Spacing = 14 };
        card.Children.Add(SubmitBar(sessions, version, platform, target, running));
        card.Children.Add(new Border { Classes = { "rule" } });
        card.Children.Add(ProfileRow(fallbackHardware));
        return new Border { Classes = { "post" }, Margin = new(0, 14, 0, 0), Child = card };
    }

    /// <summary>
    /// The single Submit button (submitting every ready session for this build and platform) beside its status line.
    /// </summary>
    Grid SubmitBar(IReadOnlyList<BenchmarkSession> sessions, string version, string platform, bool target, bool running)
    {
        var statusLine = BenchmarkLayout.Line();
        var button = new Button { MinWidth = 170, VerticalAlignment = VerticalAlignment.Center };
        button.Click += async (_, _) => await SubmitAllAsync(sessions, version, platform, target);

        void Update()
        {
            var (submittable, pending, blocked) = SubmitPartition(sessions, version, target);
            button.Content = SubmitButtonText(submittable.Count, pending.Count);
            bool canSubmit = !_submitting && !running && blocked is null && submittable.Count > 0;
            button.IsEnabled = canSubmit;
            button.Classes.Set("update", canSubmit && pending.Count > 0);

            string? text = _submitting ? _submitProgress
                : _submitSummary is not null ? _submitSummary
                : blocked is not null ? blocked
                : SubmitReadyText(sessions.Count, submittable, pending);
            BenchmarkLayout.SetLine(statusLine, text);
        }

        Update();
        _updateSubmitState = Update;

        var left = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 16, 0) };
        left.Children.Add(new TextBlock { Classes = { "name" }, Text = Tr("Submit results") });
        left.Children.Add(statusLine);

        var grid = new Grid { ColumnDefinitions = [new ColumnDefinition(1, GridUnitType.Star), new ColumnDefinition(GridLength.Auto)] };
        grid.Children.Add(left);
        Grid.SetColumn(button, 1);
        grid.Children.Add(button);
        return grid;
    }

    /// <summary>
    /// Splits <paramref name="sessions"/> into submittable sessions, unsubmitted submittable sessions, and any global blocker.
    /// </summary>
    (List<BenchmarkSession> Submittable, List<BenchmarkSession> Pending, string? Blocked) SubmitPartition(
        IReadOnlyList<BenchmarkSession> sessions, string version, bool target)
    {
        if (!target) return ([], [], Tr("Not a target build: its results can be seen here but not submitted."));

        var submittable = sessions.Where(s => SessionBlockReason(s, version) is null).ToList();
        if (submittable.Count == 0)
        {
            string reason = sessions.Select(s => SessionBlockReason(s, version)).FirstOrDefault(r => r is not null)
                ?? Tr("Incomplete: both runs are needed to submit.");
            return ([], [], reason);
        }

        var pending = submittable.Where(s => s.SubmittedId is null).ToList();
        if (config.Benchmark.MachineId.Trim().Length == 0)
            return (submittable, pending, Tr("Hardware must be probed before submitting."));
        return (submittable, pending, null);
    }

    /// <summary>
    /// Why <paramref name="session"/> itself cannot be submitted, null when it is valid for <paramref name="version"/>.
    /// </summary>
    static string? SessionBlockReason(BenchmarkSession session, string version)
    {
        if (!session.Complete) return Tr("Incomplete: both runs are needed to submit.");
        if (session.Runs.FirstOrDefault(run => run.GameVersion != version) is { } other)
            return Tr("The runs were made by {1}, not {2}: they cannot be submitted.", other.GameVersion, version);
        return null;
    }

    /// <summary>
    /// Caption for the single Submit button.
    /// </summary>
    string SubmitButtonText(int submittableCount, int pendingCount)
    {
        if (_submitting) return Tr("Submitting...");
        if (pendingCount == 1) return Tr("Submit");
        if (pendingCount > 1) return Tr("Submit all ({1})", pendingCount);
        if (submittableCount > 1) return Tr("Submit all again");
        if (submittableCount == 1) return Tr("Submit again");
        return Tr("Submit");
    }

    /// <summary>
    /// Idle status text beside the Submit button when submission is allowed.
    /// </summary>
    static string SubmitReadyText(int total, IReadOnlyList<BenchmarkSession> submittable, IReadOnlyList<BenchmarkSession> pending)
    {
        int skipped = total - submittable.Count;
        if (pending.Count == 0)
        {
            return submittable.Count == 1
                ? Tr("Submitted (#{1}).", submittable[0].SubmittedId)
                : Tr("All {1} sessions submitted.", submittable.Count);
        }
        if (skipped > 0)
        {
            return pending.Count == 1
                ? Tr("1 session ready to submit ({1} incomplete skipped).", skipped)
                : Tr("{1} sessions ready to submit ({2} incomplete skipped).", pending.Count, skipped);
        }
        if (pending.Count < submittable.Count)
            return Tr("{1} of {2} sessions not yet submitted.", pending.Count, submittable.Count);
        return pending.Count == 1 ? Tr("1 session ready to submit.") : Tr("{1} sessions ready to submit.", pending.Count);
    }

    /// <summary>
    /// Submits all unsubmitted submittable sessions (or all submittable sessions when every one is already submitted).
    /// </summary>
    async Task SubmitAllAsync(IReadOnlyList<BenchmarkSession> sessions, string version, string platform, bool target)
    {
        var (submittable, pending, blocked) = SubmitPartition(sessions, version, target);
        if (_submitting || blocked is not null || submittable.Count == 0) return;

        var queue = pending.Count > 0 ? pending : submittable;
        await SubmitQueueAsync(queue, version, platform, progress: null, CancellationToken.None);
    }

    /// <summary>
    /// Submits every unsubmitted complete session for <paramref name="version"/> on <paramref name="platform"/> (used by auto-submit after a
    /// benchmark finishes) and returns a short translated status summary.
    /// </summary>
    public async Task<string?> SubmitPendingAsync(string version, string platform, bool target, Action<string>? progress, CancellationToken ct)
    {
        var sessions = BenchmarkSession.List(version, platform);
        EnsureMachineId(sessions.Select(s => s.Runs[^1].Hardware).FirstOrDefault(h => h is not null));
        var (_, pending, blocked) = SubmitPartition(sessions, version, target);
        if (blocked is not null) return blocked;
        if (pending.Count == 0) return null;

        return await SubmitQueueAsync(pending, version, platform, progress, ct);
    }

    /// <summary>
    /// Posts each session in <paramref name="queue"/> in turn, locking the machine record on the first accepted submission.
    /// </summary>
    async Task<string> SubmitQueueAsync(IReadOnlyList<BenchmarkSession> queue, string version, string platform, Action<string>? progress,
        CancellationToken ct)
    {
        _submitting = true;
        _submitSummary = null;
        int succeeded = 0, failed = 0;
        string? firstError = null;
        try
        {
            config.Save();
            for (int i = 0; i < queue.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                _submitProgress = queue.Count == 1 ? Tr("Submitting...") : Tr("Submitting {1} of {2}...", i + 1, queue.Count);
                progress?.Invoke(_submitProgress);
                Refill();

                var session = queue[i];
                var (stored, error) = await PostOneAsync(session, version, platform, ct);
                if (stored)
                {
                    _answers.Remove(session.Folder);
                    succeeded++;
                }
                else
                {
                    _answers[session.Folder] = error!;
                    firstError ??= error;
                    failed++;
                }
            }
            _submitSummary = BatchSummary(succeeded, failed, queue.Count, firstError);
            return _submitSummary;
        }
        finally
        {
            _submitting = false;
            _submitProgress = null;
            Refill();
        }
    }

    /// <summary>
    /// Posts one session; on success marks it submitted and locks the machine record.
    /// </summary>
    async Task<(bool Stored, string? Error)> PostOneAsync(BenchmarkSession session, string version, string platform, CancellationToken ct)
    {
        try
        {
            var body = BenchmarkSubmission.Body(session, config.Benchmark, platform, version, resolution());
            var answer = await service.SubmitAsync(body, ct);
            if (answer.Id is { } id)
            {
                Stored(session, id);
                return (true, null);
            }
            return (false, Tr("The service refused it: {1}", answer.Error));
        }
        catch (Exception e) when (e is HttpRequestException or IOException or UnauthorizedAccessException)
        {
            return (false, Tr("Could not reach the reports service: {1}", e.Message));
        }
    }

    /// <summary>
    /// One-line summary of a batch submission.
    /// </summary>
    static string BatchSummary(int succeeded, int failed, int total, string? firstError)
    {
        if (failed == 0) return succeeded == 1 ? Tr("Submitted 1 session.") : Tr("Submitted {1} sessions.", succeeded);
        if (succeeded == 0) return firstError ?? Tr("Submission failed.");
        return Tr("Submitted {1} of {2} sessions; {3} failed: {4}", succeeded, total, failed, firstError);
    }

    /// <summary>
    /// The service stored <paramref name="session"/> as <paramref name="id"/>: marked so, and the machine record locked.
    /// </summary>
    void Stored(BenchmarkSession session, string id)
    {
        session.MarkSubmitted(id);
        config.Benchmark.MachineLocked = true;
        config.Save();
    }

    #endregion

    #region Machine record and handle

    /// <summary>
    /// Two-column profile under the Submit bar: auto-generated non-editable machine id, hardware line and notes left; editable Submitted by
    /// handle right.
    /// </summary>
    Grid ProfileRow(System.Text.Json.Nodes.JsonObject? fallbackHardware)
    {
        var settings = config.Benchmark;
        var machine = new StackPanel { Spacing = 6, Margin = new(0, 0, 20, 0) };
        machine.Children.Add(new TextBlock { Classes = { "caps" }, Text = Tr("Machine").ToUpper(CultureInfo.CurrentCulture) });

        string idText = settings.MachineId.Length > 0 ? settings.MachineId : Tr("Generated when hardware is probed.");
        machine.Children.Add(new TextBlock
        {
            Text = idText, FontSize = 14, Foreground = settings.MachineId.Length > 0 ? PaperLight : QuietBrush,
            TextWrapping = TextWrapping.Wrap,
        });
        machine.Children.Add(BenchmarkLayout.Description(HardwareText(fallbackHardware)));
        if (settings.MachineLocked)
        {
            if (settings.MachineNotes.Length > 0) machine.Children.Add(BenchmarkLayout.Description(settings.MachineNotes));
            machine.Children.Add(BenchmarkLayout.Description(Tr("Kept as it was first submitted, so the site keeps your results together.")));
        }
        else
        {
            machine.Children.Add(Box(settings.MachineNotes, Tr("Notes (cooling, power plan, ...)"), text => settings.MachineNotes = text));
            machine.Children.Add(BenchmarkLayout.Description(
                Tr("Generated for this computer so the site keeps your results together.")));
        }

        var grid = new Grid { ColumnDefinitions = [new ColumnDefinition(1, GridUnitType.Star), new ColumnDefinition(320, GridUnitType.Pixel)] };
        grid.Children.Add(machine);
        var handle = HandleField();
        Grid.SetColumn(handle, 1);
        grid.Children.Add(handle);
        return grid;
    }

    /// <summary>
    /// The Submitted by column: label, editable text box and caption.
    /// </summary>
    StackPanel HandleField()
    {
        var settings = config.Benchmark;
        return new StackPanel
        {
            Spacing = 6,
            Children =
            {
                new TextBlock { Classes = { "caps" }, Text = Tr("Submitted by").ToUpper(CultureInfo.CurrentCulture) },
                Box(settings.Handle, Tr("Your name on the benchmarks site (optional)"), text => settings.Handle = text),
                BenchmarkLayout.Description(Tr("Shown beside your results.")),
            },
        };
    }

    /// <summary>
    /// A text box that hands its text to <paramref name="store"/> as it is typed (updating the Submit bar) and saves <c>launcher.json</c>
    /// when it loses focus.
    /// </summary>
    TextBox Box(string text, string placeholder, Action<string> store)
    {
        var box = new TextBox { Text = text, PlaceholderText = placeholder };
        box.TextChanged += (_, _) =>
        {
            store(box.Text?.Trim() ?? "");
            _updateSubmitState?.Invoke();
        };
        box.LostFocus += (_, _) => config.Save();
        return box;
    }

    /// <summary>
    /// The last probed hardware (or a session's hardware) in one line, or that it is probed at the first Start.
    /// </summary>
    string HardwareText(System.Text.Json.Nodes.JsonObject? fallbackHardware)
    {
        if ((config.Benchmark.HardwareRecord() ?? fallbackHardware) is not { } hardware)
            return Tr("The hardware is probed when a benchmark starts.");

        string cpu = BenchmarkResult.Text(hardware, "cpu") ?? "?";
        string gpu = BenchmarkResult.Text(hardware, "gpu") ?? "?";
        string ram = hardware["ramGb"]?.ToJsonString() ?? "?";
        string os = BenchmarkResult.Text(hardware, "os") ?? "?";
        return Tr("{1}, {2}, {3} GB, {4}", cpu, gpu, ram, os);
    }

    #endregion
}
