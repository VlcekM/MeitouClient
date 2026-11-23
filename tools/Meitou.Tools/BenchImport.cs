using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>
/// <c>meitou-tools bench-import</c> (docs/feedback.md "Reports service"): reads the old Python kit's results from git history (the machine files
/// <c>bench/data/machines/*.json</c> and the CSVs <c>bench/data/results/&lt;machine&gt;/*.csv</c> of a commit), groups their rows into legacy
/// sessions (one per machine, commit and scenario) and posts them to the reports service's <c>POST /admin/bench/legacy</c>, which replaces every
/// legacy session; <c>--dry</c> prints them instead.
/// </summary>
static class BenchImport
{
    #region Command

    /// <summary>
    /// The command line: the commit the data is read from, the service's address, its admin token and whether to print instead of posting.
    /// </summary>
    sealed record Options(string Commit, string? Url, string? Token, bool Dry);

    /// <summary>
    /// The commit before the bench data was deleted.
    /// </summary>
    const string DefaultCommit = "1e317e343~1";

    /// <summary>
    /// <c>meitou-tools bench-import</c>: builds the sessions and posts or prints them. Returns 0 on success, 1 when git or the service fails, 2 for
    /// a bad command line.
    /// </summary>
    public static int Run(string[] args)
    {
        if (ParseOptions(args) is not { } options) return Usage();

        var machines = ReadMachines(options.Commit);
        var sessions = BuildSessions(options.Commit, machines);
        if (options.Dry) return PrintDry(sessions);

        return Post(sessions, options.Url!, options.Token!);
    }

    /// <summary>
    /// The options, null when the command line is wrong (posting needs <c>--url</c> and <c>--token</c>).
    /// </summary>
    static Options? ParseOptions(string[] args)
    {
        string commit = DefaultCommit;
        string? url = null, token = null;
        bool dry = false;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--dry")
            {
                dry = true;
                continue;
            }

            if (i + 1 >= args.Length) return null;

            string value = args[++i];
            switch (args[i - 1])
            {
                case "--commit":
                    commit = value;
                    break;
                case "--url":
                    url = value.TrimEnd('/');
                    break;
                case "--token":
                    token = value;
                    break;
                default:
                    return null;
            }
        }

        if (!dry && (url is null || token is null)) return null;

        return new Options(commit, url, token, dry);
    }

    /// <summary>
    /// The command's usage, on stderr; returns 2.
    /// </summary>
    static int Usage()
    {
        Console.Error.WriteLine($"meitou-tools bench-import [--commit {DefaultCommit}] (--url <reports service> --token <admin token> | --dry)");
        return 2;
    }

    /// <summary>
    /// Prints the session count, how many rows the sessions hold and the first session; returns 0.
    /// </summary>
    static int PrintDry(List<JsonObject> sessions)
    {
        Console.WriteLine($"{sessions.Count} legacy sessions");
        var byRows = sessions.GroupBy(RowCount).OrderBy(g => g.Key);
        foreach (var group in byRows) Console.WriteLine($"  {group.Count()} with {group.Key} row(s)");
        foreach (var group in sessions.GroupBy(s => (string)s["machine"]!["id"]!)) Console.WriteLine($"  {group.Key}: {group.Count()}");

        if (sessions.Count > 0) Console.WriteLine(sessions[0].ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        int bytes = Encoding.UTF8.GetByteCount(Body(sessions));
        Console.WriteLine($"request body: {bytes / 1000} kB");
        return 0;
    }

    /// <summary>
    /// How many CSV rows a session holds.
    /// </summary>
    static int RowCount(JsonObject session) => session["data"]!["rows"]!.AsArray().Count;

    /// <summary>
    /// Posts the sessions to the service's legacy import; returns 0 when it took them.
    /// </summary>
    static int Post(List<JsonObject> sessions, string url, string token)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{url}/admin/bench/legacy");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = new StringContent(Body(sessions), Encoding.UTF8, "application/json");
        using var response = http.Send(request);
        string answer = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

        Console.WriteLine($"{(int)response.StatusCode} {answer}");
        return response.IsSuccessStatusCode ? 0 : 1;
    }

    /// <summary>
    /// The legacy import's request body: <c>{sessions: [...]}</c>.
    /// </summary>
    static string Body(List<JsonObject> sessions)
    {
        var array = new JsonArray([.. sessions.Select(s => (JsonNode)s.DeepClone())]);
        return new JsonObject { ["sessions"] = array }.ToJsonString();
    }

    #endregion

    #region Sessions

    /// <summary>
    /// The old kit's two scenarios by their <c>segments</c> value: the play benchmark and the base switch.
    /// </summary>
    static readonly Dictionary<string, string> Scenarios = new() { ["idle,orbit,zoom,pan"] = "play", ["bases:idle,return"] = "bases" };

    /// <summary>
    /// The sessions of every results CSV at <paramref name="commit"/>: the rows of one machine, commit and scenario make one, oldest run first.
    /// </summary>
    static List<JsonObject> BuildSessions(string commit, Dictionary<string, JsonObject> machines)
    {
        var rows = new List<(string Machine, JsonObject Row)>();
        foreach (string path in Files(commit, "bench/data/results/").Where(p => p.EndsWith(".csv", StringComparison.Ordinal)))
        {
            string machine = path.Split('/')[^2];
            rows.AddRange(LegacyBenchCsv.Rows(Git("show", $"{commit}:{path}")).Select(r => (machine, r)));
        }

        var groups = rows
            .GroupBy(r => (r.Machine, Commit: Commit(r.Row), Segments: (string)r.Row["segments"]!))
            .OrderBy(g => g.Min(r => StartedAt(r.Row)))
            .ThenBy(g => g.Key.Machine);
        return [.. groups.Select(g => Session(machines[g.Key.Machine], g.Key.Commit, [.. g.Select(r => r.Row)]))];
    }

    /// <summary>
    /// One legacy session: scenario <c>play</c> or <c>bases</c> (from the rows' segments, also added to each row), tier <c>baseline</c>,
    /// benchmark version 0, the 8-digit commit as its target, the rows in run order.
    /// </summary>
    static JsonObject Session(JsonObject machine, string commit, List<JsonObject> rows)
    {
        rows.Sort((a, b) => string.CompareOrdinal(StartedAt(a), StartedAt(b)));
        string scenario = Scenarios[(string)rows[0]["segments"]!];
        foreach (var row in rows) row["scenario"] = scenario;
        string platform = (string)rows[0]["platform"]!;

        return new JsonObject
        {
            ["at"] = StartedAt(rows[0]),
            ["handle"] = (string?)machine["github"] ?? "",
            ["machine"] = MachineRecord(machine, platform),
            ["platform"] = platform,
            ["scenario"] = scenario,
            ["target"] = commit,
            ["tier"] = "baseline",
            ["version"] = 0,
            ["fingerprint"] = "",
            ["legacy"] = true,
            ["data"] = new JsonObject { ["rows"] = new JsonArray([.. rows]) },
        };
    }

    /// <summary>
    /// A machine file as the sessions' machine record; the OS family, when the file has none, from the rows' platform.
    /// </summary>
    static JsonObject MachineRecord(JsonObject m, string platform)
    {
        string family = (string?)m["os_family"] ?? (platform is "native" or "proton" ? "linux" : platform);
        return new JsonObject
        {
            ["id"] = (string?)m["id"],
            ["name"] = (string?)m["id"],
            ["cpu"] = (string?)m["cpu"],
            ["gpu"] = (string?)m["gpu"],
            ["ramGb"] = m["ram_gb"]?.DeepClone(),
            ["os"] = (string?)m["os"],
            ["osFamily"] = family,
            ["resolution"] = (string?)m["resolution"],
            ["notes"] = (string?)m["notes"],
        };
    }

    /// <summary>
    /// When a row's run started, as UTC ISO 8601: the kit wrote the machine's local time without a zone, so it is taken as UTC.
    /// </summary>
    static string StartedAt(JsonObject row) => $"{(string)row["date"]!}T{(string)row["time"]!}Z";

    /// <summary>
    /// A row's commit, its first 8 hex digits.
    /// </summary>
    static string Commit(JsonObject row)
    {
        string commit = (string)row["commit"]!;
        return commit.Length > 8 ? commit[..8] : commit;
    }

    /// <summary>
    /// Every machine file at <paramref name="commit"/> by its id (the file name).
    /// </summary>
    static Dictionary<string, JsonObject> ReadMachines(string commit)
    {
        var machines = new Dictionary<string, JsonObject>();
        foreach (string path in Files(commit, "bench/data/machines/").Where(p => p.EndsWith(".json", StringComparison.Ordinal)))
        {
            machines[Path.GetFileNameWithoutExtension(path)] = JsonNode.Parse(Git("show", $"{commit}:{path}"))!.AsObject();
        }

        return machines;
    }

    #endregion

    #region Git

    /// <summary>
    /// The files under <paramref name="folder"/> at <paramref name="commit"/>.
    /// </summary>
    static IEnumerable<string> Files(string commit, string folder)
    {
        return Git("ls-tree", "-r", "--name-only", commit, folder).Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>
    /// Runs git in the current folder and returns its output (UTF-8); throws when it fails.
    /// </summary>
    static string Git(params string[] args)
    {
        var start = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (string arg in args) start.ArgumentList.Add(arg);
        using var git = Process.Start(start)!;
        string output = git.StandardOutput.ReadToEnd();
        string error = git.StandardError.ReadToEnd();
        git.WaitForExit();
        if (git.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', args)}: {error.Trim()}");

        return output;
    }

    #endregion
}
