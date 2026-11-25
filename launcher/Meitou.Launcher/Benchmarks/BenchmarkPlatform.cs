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
    /// The renderer the benchmark runs on for the build of <paramref name="version"/>: on macOS <c>metal</c> on Apple silicon when the build's list
    /// says it runs the benchmark on Metal, else <c>vulkan</c> (MoltenVK); null elsewhere (the game's own, Vulkan).
    /// </summary>
    public string? Renderer(string version)
    {
        if (!OperatingSystem.IsMacOS() || Proton is not null) return null;

        bool metal = MacSupport.MetalAvailable && BenchmarkScenario.ReadRenderers(BuildFolder(version)).Contains("metal");
        return metal ? "metal" : "vulkan";
    }

    /// <summary>
    /// The start of <paramref name="exe"/> with <paramref name="arguments"/> (paths already <see cref="GamePath"/>), the Kenshi folder in
    /// <c>KENSHI_PATH</c>, no Steam app id and none of the game's Metal variables (the result must not depend on them); under Proton through
    /// <c>python3 proton run</c> with the benchmark's own prefix. On macOS <paramref name="renderer"/> <c>vulkan</c> is MoltenVK through the
    /// loader the launcher links in, <c>metal</c> needs nothing, null (a start that draws nothing: the list) either. Null when it cannot start
    /// here (macOS Vulkan without a loader).
    /// </summary>
    public ProcessStartInfo? Start(string exe, GameInstall kenshi, IEnumerable<string> arguments, string? renderer)
    {
        var start = Proton is { } proton ? ProtonStart(proton, exe) : new ProcessStartInfo(exe) { WorkingDirectory = LauncherConfig.DataDirectory };
        start.UseShellExecute = false;
        foreach (string argument in arguments) start.ArgumentList.Add(argument);

        start.Environment[GameInstall.EnvironmentVariable] = GamePath(kenshi.Root);
        foreach (string name in start.Environment.Keys.Where(name => name.StartsWith("MEITOU_METAL_", StringComparison.Ordinal)).ToList())
            start.Environment.Remove(name);

        foreach (string name in new[] { "SteamAppId", "SteamGameId" }) start.Environment.Remove(name);

        if (OperatingSystem.IsMacOS() && renderer == "metal") start.Environment.Remove("DYLD_LIBRARY_PATH");
        if (OperatingSystem.IsMacOS() && renderer == "vulkan" && !MacSupport.AddVulkanEnvironment(start.Environment)) return null;
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
