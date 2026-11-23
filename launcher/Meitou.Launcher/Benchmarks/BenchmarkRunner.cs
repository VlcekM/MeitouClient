using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Meitou.Content;

namespace Meitou.Launcher.Benchmarks;

/// <summary>
/// What one Start runs: the build, where, which scenarios at which tiers, with which Kenshi folder.
/// </summary>
sealed record BenchmarkJob(Release Release, BenchmarkPlatform Platform, IReadOnlyList<string> Scenarios, IReadOnlyList<string> Tiers,
    GameInstall Kenshi);

/// <summary>
/// Where the runner reports what it does (the Benchmark page).
/// </summary>
interface IBenchmarkView
{
    /// <summary>
    /// A step began; <paramref name="text"/> is translated.
    /// </summary>
    void Step(string text);

    /// <summary>
    /// A download's bytes done and total (0 when unknown).
    /// </summary>
    void Download(long done, long total);

    /// <summary>
    /// The build listed its scenarios (<c>--benchmark-list</c>).
    /// </summary>
    void ScenariosListed(IReadOnlyList<BenchmarkScenario> scenarios);

    /// <summary>
    /// Run <paramref name="run"/> of <paramref name="scenario"/> at <paramref name="tier"/> started, launch <paramref name="index"/> of
    /// <paramref name="count"/> (1-based).
    /// </summary>
    void RunStarted(BenchmarkScenario scenario, string tier, int run, int index, int count);

    /// <summary>
    /// Something the player should read that did not stop the Start (a skipped session); translated.
    /// </summary>
    void Notice(string text);

    /// <summary>
    /// The game probed the hardware: its record.
    /// </summary>
    void HardwareProbed(JsonObject hardware);

    /// <summary>
    /// A session's runs finished (or it was skipped).
    /// </summary>
    void SessionDone();
}

/// <summary>
/// A Start stopped for a reason the player is told (translated), not a fault of the launcher.
/// </summary>
sealed class BenchmarkStoppedException(string message) : Exception(message);

/// <summary>
/// Runs a Start: installs the build into the benchmark's own folder, asks it for its scenarios, prepares the save when a ticked scenario loads
/// it, probes the hardware, then launches the game twice per session (scenario and tier) with <c>--benchmark &lt;tier&gt;
/// --benchmark-scenario &lt;id&gt; --benchmark-out &lt;dir&gt;</c> and, for a scenario with a save, <c>--load &lt;save copy&gt;</c>.
/// </summary>
sealed class BenchmarkRunner(ReleaseInstaller installer, BenchmarkSave save)
{
    #region Start

    /// <summary>
    /// The game's exit code for a tier that needs ray queries on a device without them.
    /// </summary>
    const int NoRayQueries = 5;

    /// <summary>
    /// The game's exit code for a save whose hash is not the benchmark's.
    /// </summary>
    const int SaveMismatch = 6;

    /// <summary>
    /// The game's exit code for a scenario it does not know.
    /// </summary>
    const int UnknownScenario = 7;

    /// <summary>
    /// The longest one run may take before it is taken as hung.
    /// </summary>
    static readonly TimeSpan RunLimit = TimeSpan.FromMinutes(30);

    /// <summary>
    /// The longest a probe or the scenario list may take (Proton makes its prefix on the first start).
    /// </summary>
    static readonly TimeSpan ProbeLimit = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The game process now running, null between launches.
    /// </summary>
    Process? _game;

    /// <summary>
    /// Runs <paramref name="job"/>, reporting to <paramref name="view"/>. Throws <see cref="BenchmarkStoppedException"/> with the reason when a
    /// step fails, <see cref="OperationCanceledException"/> when cancelled (the game killed).
    /// </summary>
    public async Task RunAsync(BenchmarkJob job, IBenchmarkView view, CancellationToken ct)
    {
        var (exe, listed) = await PrepareBuildAsync(job, view, ct);
        var scenarios = Runnable(job, listed, view);
        if (scenarios.Any(scenario => scenario.Save is not null)) await PrepareSaveAsync(view, ct);
        var hardware = await ProbeAsync(job, exe, view, ct);
        bool rayQueries = hardware["rayQueries"] is JsonValue value && value.TryGetValue(out bool has) && has;
        int count = scenarios.Count * job.Tiers.Count * BenchmarkTiers.RunsPerTier, index = 0;
        foreach (var scenario in scenarios)
        {
            foreach (string tier in job.Tiers)
            {
                if (BenchmarkTiers.NeedsRayQueries(tier) && !rayQueries)
                    view.Notice(Tr("{1} skipped: it needs ray tracing, which this graphics card does not have.", BenchmarkTiers.Label(tier)));
                else
                    await RunSessionAsync(job, exe, scenario, tier, view, index, count, ct);
                index += BenchmarkTiers.RunsPerTier;
                view.SessionDone();
            }
        }
    }

    /// <summary>
    /// The build installed (when it is not yet) and its scenarios listed (when they are not yet), for Start or for the page's Install button.
    /// </summary>
    public async Task<(string Exe, List<BenchmarkScenario> Scenarios)> PrepareBuildAsync(BenchmarkJob job, IBenchmarkView view,
        CancellationToken ct)
    {
        string exe = await InstallAsync(job, view, ct);
        var scenarios = await ListScenariosAsync(job, exe, view, ct);
        view.ScenariosListed(scenarios);
        return (exe, scenarios);
    }

    /// <summary>
    /// Kills the running game and what it started at once (the window closing).
    /// </summary>
    public void Kill()
    {
        try
        {
            if (_game is { HasExited: false } game) game.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }

    /// <summary>
    /// <paramref name="text"/> in the launcher's language, its holes filled.
    /// </summary>
    static string Tr(string text, params object?[] args) => LauncherText.Translate(text, args);

    #endregion

    #region Preparing

    /// <summary>
    /// The build's game, installed into the benchmark's builds folder first when it is not there.
    /// </summary>
    async Task<string> InstallAsync(BenchmarkJob job, IBenchmarkView view, CancellationToken ct)
    {
        var (release, platform) = (job.Release, job.Platform);
        if (platform.FindGame(release.Version) is { } installed) return installed;

        var asset = release.GameAsset(platform.Channel)
            ?? throw new BenchmarkStoppedException(Tr("{1} has no download for {2}.", release.Title, platform.Channel));
        view.Step(Tr("Downloading {1}...", release.Title));
        var progress = new Progress<(long Done, long Total)>(p => view.Download(p.Done, p.Total));
        string folder = platform.BuildFolder(release.Version);
        await installer.InstallAsync(asset, BenchmarkFolders.Downloads, folder, platform.Executables, progress, ct);
        return platform.FindGame(release.Version) ?? throw new BenchmarkStoppedException(Tr("{1} has no game in its download.", release.Title));
    }

    /// <summary>
    /// The build's scenarios: its kept list, else what <c>--benchmark-list</c> writes (kept in the build's folder).
    /// </summary>
    async Task<List<BenchmarkScenario>> ListScenariosAsync(BenchmarkJob job, string exe, IBenchmarkView view, CancellationToken ct)
    {
        string folder = job.Platform.BuildFolder(job.Release.Version);
        if (BenchmarkScenario.ReadList(folder) is { } kept) return kept;

        view.Step(Tr("Asking the build for its benchmarks..."));
        string file = BenchmarkScenario.ListPath(folder);
        var start = Start(job, exe, ["--benchmark-list", job.Platform.GamePath(file)]);
        int code = await RunGameAsync(start, ProbeLimit, ct);
        if (code == 0 && BenchmarkScenario.ReadList(folder) is { } listed) return listed;

        File.Delete(file);
        throw new BenchmarkStoppedException(
            Tr("This build did not list its benchmarks (exit code {1}): it may be older than launcher benchmarks.", code));
    }

    /// <summary>
    /// The ticked scenarios the build has and the launcher can give the save of, in the build's order; the others are said and skipped.
    /// </summary>
    static List<BenchmarkScenario> Runnable(BenchmarkJob job, List<BenchmarkScenario> listed, IBenchmarkView view)
    {
        foreach (string id in job.Scenarios.Where(id => !listed.Any(scenario => scenario.Id == id)))
            view.Notice(Tr("{1} skipped: this build has no such benchmark.", BenchmarkScenario.Label(id)));

        var ticked = listed.Where(scenario => job.Scenarios.Contains(scenario.Id)).ToList();
        foreach (var scenario in ticked.Where(scenario => !HasSave(scenario)))
        {
            string name = BenchmarkScenario.Label(scenario.Id);
            view.Notice(Tr("{1} skipped: it needs the save {2}, which this launcher cannot download.", name, scenario.Save));
        }
        return [.. ticked.Where(HasSave)];
    }

    /// <summary>
    /// Whether the launcher can give <paramref name="scenario"/> its save: it needs none, or the one <see cref="BenchmarkSave"/> downloads.
    /// </summary>
    static bool HasSave(BenchmarkScenario scenario) => scenario.Save is null or BenchmarkSave.Name;

    /// <summary>
    /// The save downloaded (once), unpacked and checked.
    /// </summary>
    async Task PrepareSaveAsync(IBenchmarkView view, CancellationToken ct)
    {
        view.Step(Tr("Checking the benchmark save..."));
        var progress = new Progress<(long Done, long Total)>(p => view.Download(p.Done, p.Total));
        try
        {
            await save.EnsureAsync(progress, ct);
        }
        catch (InvalidDataException)
        {
            throw new BenchmarkStoppedException(Tr("The benchmark save is damaged: its download does not match the expected hash."));
        }
    }

    /// <summary>
    /// The hardware record <c>--benchmark-probe</c> writes, handed to the view.
    /// </summary>
    async Task<JsonObject> ProbeAsync(BenchmarkJob job, string exe, IBenchmarkView view, CancellationToken ct)
    {
        view.Step(Tr("Probing the hardware..."));
        string file = Path.Combine(BenchmarkFolders.Root, "hardware.json");
        Directory.CreateDirectory(BenchmarkFolders.Root);
        File.Delete(file);
        var start = Start(job, exe, ["--benchmark-probe", job.Platform.GamePath(file)]);
        int code = await RunGameAsync(start, ProbeLimit, ct);
        if (code != 0 || ReadObject(file) is not { } hardware)
        {
            throw new BenchmarkStoppedException(
                Tr("This build did not report the hardware (exit code {1}): it may be older than launcher benchmarks.", code));
        }
        view.HardwareProbed(hardware);
        return hardware;
    }

    /// <summary>
    /// <paramref name="file"/> as a JSON object; null when it is missing or not one.
    /// </summary>
    static JsonObject? ReadObject(string file)
    {
        try
        {
            return File.Exists(file) ? JsonNode.Parse(File.ReadAllText(file)) as JsonObject : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    #endregion

    #region Runs

    /// <summary>
    /// The runs of one session into its folder (emptied first); stops the whole Start on a failed run.
    /// </summary>
    async Task RunSessionAsync(BenchmarkJob job, string exe, BenchmarkScenario scenario, string tier, IBenchmarkView view, int index, int count,
        CancellationToken ct)
    {
        string session = BenchmarkFolders.Session(job.Release.Version, job.Platform.Wire, scenario.Id, tier);
        if (Directory.Exists(session)) Directory.Delete(session, recursive: true);
        for (int run = 1; run <= BenchmarkTiers.RunsPerTier; run++)
        {
            string output = BenchmarkFolders.Run(session, run);
            Directory.CreateDirectory(output);
            if (scenario.Save is not null)
            {
                view.Step(Tr("Copying the save..."));
                await BenchmarkSave.CopyForRunAsync(ct);
            }

            view.RunStarted(scenario, tier, run, index + run, count);
            int code = await RunGameAsync(Start(job, exe, RunArguments(job.Platform, scenario, tier, output)), RunLimit, ct);
            if (Skipped(code, scenario, tier, view)) return;
            CheckRun(code, output, exe, tier, run);
        }
    }

    /// <summary>
    /// Whether the game refused the session in a way that only skips it (exit code 5: no ray queries; 7: an unknown scenario), said in the view.
    /// </summary>
    static bool Skipped(int code, BenchmarkScenario scenario, string tier, IBenchmarkView view)
    {
        if (code == NoRayQueries)
        {
            string tierName = BenchmarkTiers.Label(tier);
            view.Notice(Tr("{1} skipped: the game says it needs ray tracing, which this graphics card does not have.", tierName));
        }
        else if (code == UnknownScenario)
            view.Notice(Tr("{1} skipped: the game does not know this benchmark (exit code 7).", BenchmarkScenario.Label(scenario.Id)));
        return code is NoRayQueries or UnknownScenario;
    }

    /// <summary>
    /// The game's arguments for one run (paths as the game sees them); <c>--load</c> only for a scenario with a save.
    /// </summary>
    static List<string> RunArguments(BenchmarkPlatform platform, BenchmarkScenario scenario, string tier, string output)
    {
        List<string> arguments = ["--benchmark", tier, "--benchmark-scenario", scenario.Id, "--benchmark-out", platform.GamePath(output)];
        if (scenario.Save is not null) arguments.AddRange(["--load", platform.GamePath(BenchmarkSave.Copy)]);
        return arguments;
    }

    /// <summary>
    /// Stops the Start with the reason when a run failed: a save the game refused, another non-zero exit, no <c>result.json</c>.
    /// </summary>
    static void CheckRun(int code, string output, string exe, string tier, int run)
    {
        if (code == SaveMismatch)
            throw new BenchmarkStoppedException(Tr("The game refused the save: its hash is not the benchmark's (exit code 6)."));

        string log = Path.Combine(Path.GetDirectoryName(exe)!, "log.txt");
        if (code != 0)
        {
            string tierName = BenchmarkTiers.Label(tier);
            throw new BenchmarkStoppedException(Tr("The game stopped with exit code {1} in {2}, run {3}; its log is {4}", code, tierName, run, log));
        }
        if (BenchmarkResult.Read(output) is null)
            throw new BenchmarkStoppedException(Tr("The game wrote no result.json into {1}; its log is {2}", output, log));
    }

    /// <summary>
    /// The start of the game for <paramref name="job"/>'s platform; stops the Start when it cannot start here.
    /// </summary>
    static ProcessStartInfo Start(BenchmarkJob job, string exe, IEnumerable<string> arguments) =>
        job.Platform.Start(exe, job.Kenshi, arguments)
        ?? throw new BenchmarkStoppedException(
            Tr("The Vulkan loader is not installed: run 'brew install molten-vk vulkan-loader' in a terminal, then try again."));

    /// <summary>
    /// Runs one launch to its end; kills it (and what it started) when cancelled or when it runs past <paramref name="most"/>.
    /// </summary>
    async Task<int> RunGameAsync(ProcessStartInfo start, TimeSpan most, CancellationToken ct)
    {
        using var game = Process.Start(start) ?? throw new BenchmarkStoppedException(Tr("The game did not start."));
        _game = game;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(most);
        try
        {
            await game.WaitForExitAsync(limit.Token);
            return game.ExitCode;
        }
        catch (OperationCanceledException)
        {
            Kill();
            ct.ThrowIfCancellationRequested();
            throw new BenchmarkStoppedException(Tr("The game ran over {1} minutes and was stopped.", (int)most.TotalMinutes));
        }
        finally
        {
            _game = null;
        }
    }

    #endregion
}
