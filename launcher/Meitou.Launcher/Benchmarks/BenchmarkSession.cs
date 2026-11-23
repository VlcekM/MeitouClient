namespace Meitou.Launcher.Benchmarks;

/// <summary>
/// One scenario at one tier benchmarked on this machine with one build and platform, as its folder holds it
/// (<see cref="BenchmarkFolders.Session"/>): the runs that wrote a <c>result.json</c>, and whether it was submitted.
/// </summary>
sealed record BenchmarkSession(string Folder, string Scenario, string Tier, IReadOnlyList<BenchmarkResult> Runs)
{
    /// <summary>
    /// The file a submitted session keeps the service's id in.
    /// </summary>
    const string SubmittedFile = "submitted.txt";

    /// <summary>
    /// The sessions of build <paramref name="version"/> on <paramref name="platform"/> that have at least one run: by scenario, then cheapest
    /// tier first.
    /// </summary>
    public static List<BenchmarkSession> List(string version, string platform)
    {
        string root = BenchmarkFolders.Scenarios(version, platform);
        string[] scenarios;
        try
        {
            scenarios = Directory.Exists(root) ? [.. Directory.GetDirectories(root).Select(Path.GetFileName).OfType<string>()] : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            scenarios = [];
        }
        return [.. scenarios
            .Order(StringComparer.Ordinal)
            .SelectMany(scenario => BenchmarkTiers.All.Select(tier => Read(version, platform, scenario, tier)))
            .OfType<BenchmarkSession>()];
    }

    /// <summary>
    /// The session of <paramref name="scenario"/> at <paramref name="tier"/> of build <paramref name="version"/> on <paramref name="platform"/>.
    /// </summary>
    static BenchmarkSession? Read(string version, string platform, string scenario, string tier) =>
        Read(BenchmarkFolders.Session(version, platform, scenario, tier), scenario, tier);

    /// <summary>
    /// The session in <paramref name="folder"/>: its runs in order, up to the first without a result; null when there is none.
    /// </summary>
    public static BenchmarkSession? Read(string folder, string scenario, string tier)
    {
        var runs = new List<BenchmarkResult>();
        for (int run = 1; run <= BenchmarkTiers.RunsPerTier; run++)
        {
            if (BenchmarkResult.Read(BenchmarkFolders.Run(folder, run)) is not { } result) break;
            runs.Add(result);
        }
        return runs.Count == 0 ? null : new BenchmarkSession(folder, scenario, tier, runs);
    }

    /// <summary>
    /// Whether both runs finished and agree on the scenario, the tier, the benchmark version and the fingerprint, as the service requires.
    /// </summary>
    public bool Complete => Runs.Count == BenchmarkTiers.RunsPerTier && Runs.All(SameAsFirst);

    /// <summary>
    /// Whether <paramref name="run"/> is of this session's scenario and tier, with the first run's version and fingerprint.
    /// </summary>
    bool SameAsFirst(BenchmarkResult run) =>
        run.Scenario == Scenario && run.Tier == Tier && run.Version == Runs[0].Version && run.Fingerprint == Runs[0].Fingerprint;

    /// <summary>
    /// The last run's <c>summary.md</c>, null when it is missing or cannot be read.
    /// </summary>
    public string? Summary
    {
        get
        {
            string file = Path.Combine(BenchmarkFolders.Run(Folder, Runs.Count), "summary.md");
            try
            {
                return File.Exists(file) ? File.ReadAllText(file) : null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// The id the reports service gave the session, null while it is not submitted.
    /// </summary>
    public string? SubmittedId
    {
        get
        {
            string file = Path.Combine(Folder, SubmittedFile);
            try
            {
                return File.Exists(file) ? File.ReadAllText(file).Trim() : null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Records that the service stored the session under <paramref name="id"/>.
    /// </summary>
    public void MarkSubmitted(string id) => File.WriteAllText(Path.Combine(Folder, SubmittedFile), id);
}
