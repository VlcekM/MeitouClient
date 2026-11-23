using System.Diagnostics;
using Meitou.Content;

namespace Meitou.Launcher.Benchmarks;

/// <summary>
/// A way to run the benchmark on this machine: the host's own build, or on Linux the Windows build under a Proton. <see cref="Wire"/> is the
/// <c>platform</c> the reports service stores: <c>native</c> (Linux), <c>proton</c>, <c>windows</c> or <c>macos</c>.
/// </summary>
sealed record BenchmarkPlatform(string Wire, ProtonInstall? Proton)
{
    /// <summary>
    /// The game's executables in the Windows build, newest name first.
    /// </summary>
    static readonly string[] WindowsExecutables = ["MeitouClient.exe", "meitou.exe"];

    /// <summary>
    /// The host's own platform, then on Linux each Proton found.
    /// </summary>
    public static List<BenchmarkPlatform> Available()
    {
        string host = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "native";
        return [new BenchmarkPlatform(host, null), .. ProtonInstall.Find().Select(proton => new BenchmarkPlatform("proton", proton))];
    }

    /// <summary>
    /// The runtime part of the game zip this platform runs: <c>win-x64</c> under Proton, else the launcher's own.
    /// </summary>
    public string Channel => Proton is null ? Releases.Channel : "win-x64";

    /// <summary>
    /// The game's executable names in this platform's build.
    /// </summary>
    public IReadOnlyList<string> Executables => Proton is null ? Releases.GameExecutables : WindowsExecutables;

    /// <summary>
    /// The name shown in the platform choice.
    /// </summary>
    public string Label => Proton is { } proton ? LauncherText.Translate("Proton: {1}", proton.Name)
        : Wire == "native" ? LauncherText.Translate("Native Linux build")
        : LauncherText.Translate("This computer's build");

    /// <summary>
    /// The folder the build of <paramref name="version"/> for this platform is installed in.
    /// </summary>
    public string BuildFolder(string version) => Path.Combine(BenchmarkFolders.Builds, $"{version}-{Channel}");

    /// <summary>
    /// The installed game of <paramref name="version"/> for this platform, or null.
    /// </summary>
    public string? FindGame(string version) =>
        Executables.Select(name => Path.Combine(BuildFolder(version), name)).FirstOrDefault(File.Exists);

    /// <summary>
    /// <paramref name="path"/> as the game sees it: unchanged, or under Proton on Wine's <c>Z:</c> drive.
    /// </summary>
    public string GamePath(string path) => Proton is null ? path : ProtonInstall.WindowsPath(path);

    /// <summary>
    /// The start of <paramref name="exe"/> with <paramref name="arguments"/> (paths already <see cref="GamePath"/>), the Kenshi folder in
    /// <c>KENSHI_PATH</c>, no Steam app id; under Proton through <c>python3 proton run</c> with the benchmark's own prefix. Null when it cannot
    /// start here (macOS without a Vulkan loader).
    /// </summary>
    public ProcessStartInfo? Start(string exe, GameInstall kenshi, IEnumerable<string> arguments)
    {
        var start = Proton is { } proton ? ProtonStart(proton, exe) : new ProcessStartInfo(exe) { WorkingDirectory = LauncherConfig.DataDirectory };
        start.UseShellExecute = false;
        foreach (string argument in arguments) start.ArgumentList.Add(argument);

        start.Environment[GameInstall.EnvironmentVariable] = GamePath(kenshi.Root);
        foreach (string name in new[] { "SteamAppId", "SteamGameId", "MEITOU_METAL_GPU_CULL", "MEITOU_METAL_GPU_BUDGET_MB" })
            start.Environment.Remove(name);

        // The benchmark is Vulkan only; on macOS that is MoltenVK through the loader the launcher links in.
        if (OperatingSystem.IsMacOS() && !MacSupport.AddVulkanEnvironment(start.Environment)) return null;
        return start;
    }

    /// <summary>
    /// <c>python3 &lt;proton&gt; run ./MeitouClient.exe</c> from the build's folder, with the compatibility prefix (made first: Proton refuses
    /// one that does not exist) and the Steam folder Proton needs.
    /// </summary>
    static ProcessStartInfo ProtonStart(ProtonInstall proton, string exe)
    {
        Directory.CreateDirectory(BenchmarkFolders.ProtonPrefix);
        var start = new ProcessStartInfo("python3") { WorkingDirectory = Path.GetDirectoryName(exe)! };
        start.ArgumentList.Add(proton.Script);
        start.ArgumentList.Add("run");
        start.ArgumentList.Add("./" + Path.GetFileName(exe));
        start.Environment["STEAM_COMPAT_DATA_PATH"] = BenchmarkFolders.ProtonPrefix;
        start.Environment["STEAM_COMPAT_CLIENT_INSTALL_PATH"] = proton.SteamRoot;
        return start;
    }
}
