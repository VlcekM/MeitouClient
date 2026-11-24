using System.Globalization;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Meitou.Launcher.Benchmarks;

/// <summary>
/// The Benchmark page's lower half: the machine record and handle results are submitted with, then each session of the chosen build and
/// platform with its runs' numbers and a Submit button.
/// </summary>
sealed class BenchmarkResultsPanel(BenchmarkService service, LauncherConfig config, Func<string> resolution) : StackPanel
{
    #region Showing

    /// <summary>
    /// What was last shown: the build's version, the platform's wire name, and whether the build is a target.
    /// </summary>
    (string Version, string Platform, bool Target)? _shown;

    /// <summary>
    /// Each session's last submit answer (by folder), for the line under it.
    /// </summary>
    readonly Dictionary<string, string> _answers = [];

    /// <summary>
    /// The sessions (by folder) being submitted now.
    /// </summary>
    readonly HashSet<string> _submitting = [];

    /// <summary>
    /// Updates each shown Submit button and its line from the machine record as typed, so filling it in enables them without a rebuild.
    /// </summary>
    readonly List<Action> _submitStates = [];

    /// <summary>
    /// Fills the panel for build <paramref name="version"/> on <paramref name="platform"/>; <paramref name="target"/> says whether it may be
    /// submitted. Null shows nothing (no build chosen).
    /// </summary>
    public void Show(string? version, string platform, bool target)
    {
        _shown = version is null ? null : (version, platform, target);
        Refill();
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
        _submitStates.Clear();
        if (_shown is not var (version, platform, target)) return;

        var sessions = BenchmarkSession.List(version, platform);
        Children.Add(new Border { Classes = { "header" }, Margin = new(0, 16, 0, 10), Child = new TextBlock { Text = Tr("Results") } });
        if (sessions.Count == 0)
        {
            Children.Add(BenchmarkLayout.Description(Tr("No results for this build and platform yet.")));
            return;
        }
        Children.Add(MachineRow());
        Children.Add(HandleRow());
        foreach (var session in sessions) Children.Add(SessionBlock(session, version, platform, target));
    }

    #endregion

    #region Machine record

    /// <summary>
    /// The machine record's id, name and notes; editable until a session is submitted with it.
    /// </summary>
    Border MachineRow()
    {
        var settings = config.Benchmark;
        if (settings.MachineId.Length == 0) settings.MachineId = DefaultMachineId(settings.HardwareRecord());

        var boxes = new StackPanel { Spacing = 6 };
        boxes.Children.Add(Box(settings.MachineId, Tr("Machine id (letters, digits, dashes)"), text => settings.MachineId = text));
        boxes.Children.Add(Box(settings.MachineName, Tr("Machine name (optional)"), text => settings.MachineName = text));
        boxes.Children.Add(Box(settings.MachineNotes, Tr("Notes (cooling, power plan, ...)"), text => settings.MachineNotes = text));
        foreach (var box in boxes.Children.OfType<TextBox>()) box.IsReadOnly = settings.MachineLocked;

        string description = settings.MachineLocked
            ? Tr("Kept as it was first submitted, so the site keeps your results together.")
            : Tr("Can be edited until the first session is submitted; then it is kept, so the site keeps your results together.");
        return BenchmarkLayout.Row(Tr("Machine"), description + "\n" + HardwareText(), boxes, 420);
    }

    /// <summary>
    /// The handle results are submitted under, remembered.
    /// </summary>
    Border HandleRow()
    {
        var settings = config.Benchmark;
        var box = Box(settings.Handle, Tr("Your name on the benchmarks site (optional)"), text => settings.Handle = text);
        return BenchmarkLayout.Row(Tr("Submitted by"), Tr("Shown beside your results."), box, 420);
    }

    /// <summary>
    /// A text box that hands its text to <paramref name="store"/> as it is typed (updating the Submit buttons) and saves
    /// <c>launcher.json</c> when it loses focus.
    /// </summary>
    TextBox Box(string text, string placeholder, Action<string> store)
    {
        var box = new TextBox { Text = text, PlaceholderText = placeholder };
        box.TextChanged += (_, _) =>
        {
            store(box.Text?.Trim() ?? "");
            foreach (var update in _submitStates) update();
        };
        box.LostFocus += (_, _) => config.Save();
        return box;
    }

    /// <summary>
    /// A machine id from the probed hardware (GPU and OS family, e.g. <c>nvidia-geforce-rtx-4070-linux</c>), never the computer's name, which
    /// would be published; empty before a probe.
    /// </summary>
    static string DefaultMachineId(System.Text.Json.Nodes.JsonObject? hardware)
    {
        if (hardware is null) return "";

        string text = $"{hardware["gpu"]?.ToString()} {hardware["osFamily"]?.ToString()}".ToLowerInvariant();
        var slug = new string([.. text.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-')]);
        return string.Join('-', slug.Split('-', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// The last probed hardware in one line, or that it is probed at the first Start.
    /// </summary>
    string HardwareText()
    {
        if (config.Benchmark.HardwareRecord() is not { } hardware) return Tr("The hardware is probed when a benchmark starts.");

        string cpu = BenchmarkResult.Text(hardware, "cpu") ?? "?";
        string gpu = BenchmarkResult.Text(hardware, "gpu") ?? "?";
        string ram = hardware["ramGb"]?.ToJsonString() ?? "?";
        string os = BenchmarkResult.Text(hardware, "os") ?? "?";
        return Tr("{1}, {2}, {3} GB, {4}", cpu, gpu, ram, os);
    }

    #endregion

    #region Sessions

    /// <summary>
    /// One session: its title, the numbers of its runs and its Submit line.
    /// </summary>
    StackPanel SessionBlock(BenchmarkSession session, string version, string platform, bool target)
    {
        string title = Tr("{1} at {2}", BenchmarkScenario.Label(session.Scenario), BenchmarkTiers.Label(session.Tier));
        var block = new StackPanel { Spacing = 6, Margin = new(0, 14, 0, 0) };
        block.Children.Add(new TextBlock { Classes = { "name" }, Text = title });
        block.Children.Add(Table(session));
        block.Children.Add(SubmitLine(session, version, platform, target));
        return block;
    }

    /// <summary>
    /// A row per scenario group (as the last run lists them) with each run's average FPS, 99th percentile frame time and frames over 33 ms.
    /// </summary>
    static Grid Table(BenchmarkSession session)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("170" + string.Concat(session.Runs.Select(_ => ",110,110,110"))) };
        AddRow(grid, [Tr("Group"), .. session.Runs.SelectMany((_, i) => RunHeaders(i + 1))], header: true);
        foreach (var (group, _) in session.Runs[^1].Groups)
        {
            var cells = session.Runs.SelectMany(run => Cells(run.Groups.FirstOrDefault(g => g.Name == group).Metrics));
            AddRow(grid, [BenchmarkTiers.GroupLabel(group), .. cells], header: false);
        }
        return grid;
    }

    /// <summary>
    /// The column titles of run <paramref name="run"/>.
    /// </summary>
    static string[] RunHeaders(int run) =>
        [Tr("Run {1}: FPS", run), Tr("Run {1}: 99% ms", run), Tr("Run {1}: over 33 ms", run)];

    /// <summary>
    /// A group's three numbers; dashes where the run lacks the group.
    /// </summary>
    static string[] Cells(GroupMetrics? metrics) => metrics is null
        ? ["-", "-", "-"]
        : [Number(metrics.AvgFps, "0.0"), Number(metrics.P99Ms, "0.0"), Number(metrics.Over33Pct, "0.0") + " %"];

    /// <summary>
    /// <paramref name="value"/> in the current culture, a dash when missing.
    /// </summary>
    static string Number(double value, string format) => double.IsNaN(value) ? "-" : value.ToString(format, CultureInfo.CurrentCulture);

    /// <summary>
    /// Appends one line of <paramref name="cells"/> to <paramref name="grid"/>.
    /// </summary>
    static void AddRow(Grid grid, string[] cells, bool header)
    {
        int row = grid.RowDefinitions.Count;
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        for (int column = 0; column < cells.Length; column++)
        {
            var text = new TextBlock { Classes = { header ? "desc" : "name" }, Text = cells[column], Margin = new(0, 2, 8, 2) };
            if (!header) text.FontSize = 13;
            Grid.SetRow(text, row);
            Grid.SetColumn(text, column);
            grid.Children.Add(text);
        }
    }

    #endregion

    #region Submit

    /// <summary>
    /// The Submit button and the line beside it: the service's answer, or why the session cannot be submitted.
    /// </summary>
    StackPanel SubmitLine(BenchmarkSession session, string version, string platform, bool target)
    {
        string? submitted = session.SubmittedId;
        var line = BenchmarkLayout.Line();
        var button = new Button
        {
            Content = _submitting.Contains(session.Folder) ? Tr("Submitting...") : submitted is null ? Tr("Submit") : Tr("Submit again"),
        };
        button.Click += async (_, _) => await SubmitAsync(session, version, platform);
        line.VerticalAlignment = VerticalAlignment.Center;

        void Update()
        {
            string? blocked = WhyNotSubmittable(session, version, target);
            BenchmarkLayout.SetLine(line, _answers.GetValueOrDefault(session.Folder)
                ?? (submitted is not null ? Tr("Submitted (#{1}).", submitted) : blocked));
            button.IsEnabled = blocked is null && !_submitting.Contains(session.Folder);
        }

        Update();
        _submitStates.Add(Update);
        return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { button, line } };
    }

    /// <summary>
    /// Why <paramref name="session"/> cannot be submitted, null when it can.
    /// </summary>
    string? WhyNotSubmittable(BenchmarkSession session, string version, bool target)
    {
        if (!session.Complete) return Tr("Incomplete: both runs are needed to submit.");
        if (!target) return Tr("Not a target build: its results can be seen here but not submitted.");
        if (session.Runs.FirstOrDefault(run => run.GameVersion != version) is { } other)
            return Tr("The runs were made by {1}, not {2}: they cannot be submitted.", other.GameVersion, version);
        if (config.Benchmark.MachineId.Trim().Length == 0) return Tr("Fill in the machine's id to submit.");
        return null;
    }

    /// <summary>
    /// Posts the session; on success the machine record is kept from then on.
    /// </summary>
    async Task SubmitAsync(BenchmarkSession session, string version, string platform)
    {
        _submitting.Add(session.Folder);
        Refill();
        try
        {
            var body = BenchmarkSubmission.Body(session, config.Benchmark, platform, version, resolution());
            config.Save();
            var answer = await service.SubmitAsync(body, CancellationToken.None);
            _answers[session.Folder] = answer.Id is { } id ? Tr("Submitted (#{1}).", id) : Tr("The service refused it: {1}", answer.Error);
            if (answer.Id is { } stored) Stored(session, stored);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException or UnauthorizedAccessException)
        {
            _answers[session.Folder] = Tr("Could not reach the reports service: {1}", e.Message);
        }
        finally
        {
            _submitting.Remove(session.Folder);
            Refill();
        }
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
}
