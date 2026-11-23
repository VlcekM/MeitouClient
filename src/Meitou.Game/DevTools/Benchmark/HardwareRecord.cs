using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Meitou.Game.Rendering.Gpu.Core;

namespace Meitou.Game.DevTools.Benchmark;

/// <summary>
/// The launcher benchmark's hardware record (docs/bench.md "Launcher benchmark"): the CPU, its threads, the memory, the GPU the game picks, its
/// driver and video memory, whether it offers ray queries, and the OS. <c>--benchmark-probe</c> writes it alone; result.json carries it.
/// </summary>
sealed record HardwareRecord(string Cpu, int Threads, int RamGb, string Gpu, string Driver, long VramMb, bool RayQueries, string Os,
    string Kernel, string OsFamily)
{
    /// <summary>
    /// The probe's option: <c>--benchmark-probe &lt;file.json&gt;</c>, the only arguments.
    /// </summary>
    public const string ProbeFlag = "--benchmark-probe";

    /// <summary>
    /// The record of this computer with <paramref name="device"/>'s GPU (null: none found).
    /// </summary>
    public static HardwareRecord Of(VulkanDevice? device)
    {
        var (os, kernel, family) = OperatingSystemInfo();
        int ramGb = (int)Math.Round(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (double)(1L << 30));
        return new(CpuName(), Environment.ProcessorCount, ramGb, device?.DeviceName ?? "", device?.DriverName ?? "",
            device is null ? 0 : (long)(device.DeviceLocalBytes() >> 20), device?.OffersRayQuery ?? false, os, kernel, family);
    }

    /// <summary>
    /// <c>--benchmark-probe</c>: makes the Vulkan device the game would pick (headless, ray queries asked for), writes the record to
    /// <paramref name="path"/> and returns the exit code (0; 1 when the file cannot be written). Without a device the GPU fields stay empty.
    /// </summary>
    public static int WriteProbe(string path)
    {
        HardwareRecord record;
        try
        {
            using var device = VulkanDevice.Create(new VulkanDeviceOptions { Validation = false, RayTracing = true });
            record = Of(device);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"--benchmark-probe: no Vulkan device ({e.Message})");
            record = Of(null);
        }

        try
        {
            File.WriteAllText(path, record.ToJson().ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"--benchmark-probe: could not write {path}: {e.Message}");
            return 1;
        }

        Console.WriteLine($"hardware  {record.Gpu}, {record.Cpu}, {record.RamGb} GB, {record.Os}");
        return 0;
    }

    /// <summary>
    /// The record as result.json's <c>hardware</c> object (camelCase).
    /// </summary>
    public JsonObject ToJson() => new()
    {
        ["cpu"] = Cpu,
        ["threads"] = Threads,
        ["ramGb"] = RamGb,
        ["gpu"] = Gpu,
        ["driver"] = Driver,
        ["vramMb"] = VramMb,
        ["rayQueries"] = RayQueries,
        ["os"] = Os,
        ["kernel"] = Kernel,
        ["osFamily"] = OsFamily,
    };

    /// <summary>
    /// The CPU's brand name: Linux's /proc/cpuinfo, Windows' registry, macOS's sysctl; the architecture when none says.
    /// </summary>
    static string CpuName()
    {
        string? name = null;
        try
        {
            if (OperatingSystem.IsLinux()) name = CpuInfoName();
            else if (OperatingSystem.IsWindows()) name = RegistryCpuName();
            else if (OperatingSystem.IsMacOS()) name = Run("sysctl", "-n machdep.cpu.brand_string");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
        }

        return string.IsNullOrWhiteSpace(name) ? RuntimeInformation.ProcessArchitecture.ToString() : name.Trim();
    }

    /// <summary>
    /// The first <c>model name</c> of /proc/cpuinfo (x86), else its <c>Model</c> line (ARM boards); null without either.
    /// </summary>
    static string? CpuInfoName()
    {
        var lines = File.ReadAllLines("/proc/cpuinfo");
        string? Field(string key) => lines.Where(l => l.StartsWith(key, StringComparison.Ordinal) && l.Contains(':'))
            .Select(l => l[(l.IndexOf(':') + 1)..].Trim())
            .FirstOrDefault();

        return Field("model name") ?? Field("Model");
    }

    /// <summary>
    /// The first processor's <c>ProcessorNameString</c> in the registry.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    static string? RegistryCpuName()
    {
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        return key?.GetValue("ProcessorNameString") as string;
    }

    /// <summary>
    /// The OS's name, kernel version and family (linux, windows, macos).
    /// </summary>
    static (string Os, string Kernel, string Family) OperatingSystemInfo()
    {
        var version = Environment.OSVersion.Version;
        if (OperatingSystem.IsWindows())
            return (version.Build >= 22000 ? "Windows 11" : "Windows 10", version.ToString(), "windows");

        if (OperatingSystem.IsMacOS())
        {
            string darwin = RuntimeInformation.OSDescription.Split(' ', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1) ?? "";
            return ("macOS " + version.ToString(version.Build > 0 ? 3 : 2), darwin, "macos");
        }

        return (LinuxDistribution(), ReadOrEmpty("/proc/sys/kernel/osrelease"), "linux");
    }

    /// <summary>
    /// The distribution's name from /etc/os-release (<c>PRETTY_NAME</c>, else <c>NAME</c>), else "Linux".
    /// </summary>
    static string LinuxDistribution()
    {
        var lines = ReadOrEmpty("/etc/os-release").Split('\n');
        string? Value(string key) => lines.Where(l => l.StartsWith(key + "=", StringComparison.Ordinal))
            .Select(l => l[(key.Length + 1)..].Trim().Trim('"'))
            .FirstOrDefault(v => v.Length > 0);

        return Value("PRETTY_NAME") ?? Value("NAME") ?? "Linux";
    }

    /// <summary>
    /// The text of <paramref name="path"/>, trimmed; empty when it cannot be read.
    /// </summary>
    static string ReadOrEmpty(string path)
    {
        try
        {
            return File.ReadAllText(path).Trim();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    /// <summary>
    /// What <paramref name="program"/> prints with <paramref name="arguments"/>.
    /// </summary>
    static string Run(string program, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo(program, arguments) { RedirectStandardOutput = true, UseShellExecute = false });
        if (process is null) return "";

        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output;
    }
}
