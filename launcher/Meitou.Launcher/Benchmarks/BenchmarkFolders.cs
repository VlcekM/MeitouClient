namespace Meitou.Launcher.Benchmarks;

/// <summary>
/// The Benchmark page's own folders, all under <c>benchmark/</c> in the launcher's data folder: never the player's installed versions or
/// Kenshi's save folder.
/// </summary>
static class BenchmarkFolders
{
    /// <summary>
    /// <c>benchmark/</c> in the data folder.
    /// </summary>
    public static string Root => Path.Combine(LauncherConfig.DataDirectory, "benchmark");

    /// <summary>
    /// Downloads in progress and the cached save zip.
    /// </summary>
    public static string Downloads => Path.Combine(Root, "downloads");

    /// <summary>
    /// The installed benchmark builds, one folder per version and runtime.
    /// </summary>
    public static string Builds => Path.Combine(Root, "builds");

    /// <summary>
    /// The unpacked, checked save; runs load a copy of it.
    /// </summary>
    public static string Save => Path.Combine(Root, "save");

    /// <summary>
    /// The copy of the save a run loads, made fresh before each run.
    /// </summary>
    public static string SaveCopy => Path.Combine(Root, "save-copy");

    /// <summary>
    /// Proton's compatibility prefix (its own Wine prefix, not Kenshi's).
    /// </summary>
    public static string ProtonPrefix => Path.Combine(Root, "proton-prefix");

    /// <summary>
    /// The sessions' outputs: <c>sessions/&lt;version&gt;/&lt;platform&gt;/&lt;scenario&gt;/&lt;tier&gt;/run1</c> and <c>run2</c>.
    /// </summary>
    public static string Sessions => Path.Combine(Root, "sessions");

    /// <summary>
    /// The sessions of build <paramref name="version"/> on <paramref name="platform"/>, one folder per scenario.
    /// </summary>
    public static string Scenarios(string version, string platform) => Path.Combine(Sessions, version, platform);

    /// <summary>
    /// The folder of one session: the runs of <paramref name="scenario"/> at <paramref name="tier"/> of build <paramref name="version"/> on
    /// <paramref name="platform"/>.
    /// </summary>
    public static string Session(string version, string platform, string scenario, string tier) =>
        Path.Combine(Scenarios(version, platform), scenario, tier);

    /// <summary>
    /// The output folder of run <paramref name="run"/> (1-based) of a session.
    /// </summary>
    public static string Run(string session, int run) => Path.Combine(session, $"run{run}");
}
