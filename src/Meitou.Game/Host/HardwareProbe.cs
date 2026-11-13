using System.Runtime.InteropServices;
using System.Text.Json;
using Meitou.Game.Rendering.Gpu.Core;
using Meitou.Game.Settings;

namespace Meitou.Game.Host;

/// <summary>
/// <c>--recommend-preset &lt;file&gt;</c> (docs/launcher.md "Graphics"): looks at the GPU the game would pick (a headless Vulkan device), the memory
/// and the cores, and writes the quality preset that suits them as JSON, for the launcher's Detect button. No log, window or game files.
/// </summary>
static class HardwareProbe
{
    /// <summary>
    /// What was found and the preset for it; <see cref="Summary"/> is the line the launcher shows.
    /// </summary>
    public sealed record Report(string Preset, string Summary, string Gpu, bool Integrated, long VideoMemoryMb,
        long MemoryMb, int Cores, string? Problem);

    /// <summary>
    /// The preset for the hardware. A judgement, not measured, but for the classes the tiers are meant for (<see cref="QualityTiers.LowPreset"/>):
    /// an integrated GPU (but Apple's with 16 GB or more), under 3 GB of video memory, under 8 GB of memory or under 4 cores is Low; a GeForce by
    /// its model (<see cref="GeForceTier"/>); any other card by its video memory (under 6 GB Medium, under 12 GB High, under 24 GB Ultra, else
    /// Extreme); under 12 GB of memory or 6 cores is Medium at most.
    /// </summary>
    public static string Recommend(bool integrated, bool apple, long videoMemoryMb, long memoryMb, int cores, string gpu = "")
    {
        var names = QualityTiers.PresetChoices;   // Low, Medium, High, Ultra, Extreme
        if (apple) return names[memoryMb >= 16 * 1024 ? 1 : 0];
        if (integrated || videoMemoryMb < 3 * 1024 || memoryMb < 8 * 1024 || cores < 4) return names[QualityTiers.LowPreset];

        int tier = GeForceTier(gpu) ?? (videoMemoryMb < 6 * 1024 ? QualityTiers.MediumPreset
            : videoMemoryMb < 12 * 1024 ? QualityTiers.HighPreset
            : videoMemoryMb < 24 * 1024 ? QualityTiers.UltraPreset
            : QualityTiers.ExtremePreset);
        if (memoryMb < 12 * 1024 || cores < 6) tier = Math.Min(tier, QualityTiers.MediumPreset);
        return names[tier];
    }

    /// <summary>
    /// A GeForce's tier by its model ("NVIDIA GeForce RTX 4070 Ti SUPER"), from its place against the classes the tiers are meant for; null for
    /// any other name or a series not listed (the video memory decides). A GTX has no ray tracing; a laptop GPU sits a class under its name.
    /// </summary>
    internal static int? GeForceTier(string gpu)
    {
        var m = System.Text.RegularExpressions.Regex.Match(gpu, @"(RTX|GTX)\s*(\d\d)(\d)0\s*(Ti)?\s*(SUPER)?",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!m.Success) return null;

        int series = int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
        int model = m.Groups[3].Value[0] - '0';
        bool ti = m.Groups[4].Success;
        bool super = m.Groups[5].Success;
        const int low = QualityTiers.LowPreset;
        const int medium = QualityTiers.MediumPreset;
        const int high = QualityTiers.HighPreset;
        const int ultra = QualityTiers.UltraPreset;
        const int extreme = QualityTiers.ExtremePreset;
        int? tier = m.Groups[1].Value.Equals("GTX", StringComparison.OrdinalIgnoreCase)
            ? (series == 10 && model == 8 && ti ? medium : low)
            : (series, model) switch
            {
                (20, 6 or 7) => medium,
                (20, 8) => ti || super ? high : medium,
                (30, 5) => low,
                (30, 6) => medium,
                (30, 7) => high,
                (30, 8) => ti ? ultra : high,
                (30, 9) => ultra,
                (40, 5) => low,
                (40, 6) => ti ? high : medium,
                (40, 7) => ti ? ultra : high,
                (40, 8) => super ? extreme : ultra,
                (40, 9) => extreme,
                (50, 5) => medium,
                (50, 6) => high,
                (50, 7) => ti ? ultra : high,
                (50, 8 or 9) => extreme,
                _ => null,
            };

        return tier is { } t && gpu.Contains("Laptop", StringComparison.OrdinalIgnoreCase) ? Math.Max(t - 1, low) : tier;
    }

    /// <summary>
    /// Looks at the GPU the game would pick, the memory and the cores, and recommends a preset (<see cref="Recommend"/>).
    /// </summary>
    public static Report Probe()
    {
        long memoryMb = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes >> 20;
        int cores = Environment.ProcessorCount;
        bool appleSilicon = OperatingSystem.IsMacOS() && RuntimeInformation.OSArchitecture == Architecture.Arm64;
        string gpu = "";
        bool integrated = appleSilicon;
        long videoMemoryMb = appleSilicon ? memoryMb : 0;
        string? problem = null;
        try
        {
            using var device = VulkanDevice.Create(new VulkanDeviceOptions { Validation = false });
            gpu = device.DeviceName;
            integrated = device.IsIntegrated;
            videoMemoryMb = (long)(device.DeviceLocalBytes() >> 20);
        }
        catch (Exception e)
        {
            // No Vulkan (Metal-only Mac, a missing driver): the memory and the cores decide alone.
            problem = e.Message;
        }
        bool apple = appleSilicon || gpu.StartsWith("Apple", StringComparison.Ordinal);
        string preset = Recommend(integrated, apple, videoMemoryMb, memoryMb, cores, gpu);
        // An integrated GPU's device-local heap is a share of the system's memory, not memory of its own.
        string what = gpu.Length == 0 ? "GPU not found" : integrated ? $"{gpu} (integrated)"
            : $"{gpu} ({videoMemoryMb / 1024.0:0.#} GB video memory)";
        string summary = $"{what}, {memoryMb / 1024.0:0.#} GB memory, {cores} threads: {preset}";
        return new(preset, summary, gpu, integrated, videoMemoryMb, memoryMb, cores, problem);
    }

    /// <summary>
    /// Writes the probe's report as JSON to <paramref name="path"/> and logs its summary; returns the exit code, 0.
    /// </summary>
    public static int Write(string path)
    {
        var report = Probe();
        File.WriteAllText(path,
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        Console.WriteLine($"hardware  {report.Summary}");
        return 0;
    }
}