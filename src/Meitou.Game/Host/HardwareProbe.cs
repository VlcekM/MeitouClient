using System.Runtime.InteropServices;
using System.Text.Json;
using Meitou.Rendering.Gpu.Core;

namespace Meitou.Game;

/// <summary>
/// <c>--recommend-preset &lt;file&gt;</c> (docs/launcher.md "Graphics"): looks at the GPU the game would pick (a headless Vulkan device), the memory and
/// the cores, and writes the quality preset that suits them as JSON, for the launcher's Detect button. No log, window or game files.
/// </summary>
static class HardwareProbe
{
    /// <summary>What was found and the preset for it; <see cref="Summary"/> is the line the launcher shows.</summary>
    public sealed record Report(string Preset, string Summary, string Gpu, bool Integrated, long VideoMemoryMb, long MemoryMb, int Cores, string? Problem);

    /// <summary>
    /// The preset for the hardware. The thresholds are a judgement, not measured: an integrated GPU (but Apple's with 16 GB or more), under 3 GB
    /// of video memory, under 8 GB of memory or under 4 cores is Low; under 6 GB of video memory, 12 GB of memory or 6 cores Medium; under
    /// 10 GB of video memory High; else Ultra.
    /// </summary>
    public static string Recommend(bool integrated, bool apple, long videoMemoryMb, long memoryMb, int cores)
    {
        var names = SettingsMenu.PresetChoices;   // Low, Medium, High, Ultra
        if (apple) return names[memoryMb >= 16 * 1024 ? 1 : 0];
        if (integrated || videoMemoryMb < 3 * 1024 || memoryMb < 8 * 1024 || cores < 4) return names[0];
        if (videoMemoryMb < 6 * 1024 || memoryMb < 12 * 1024 || cores < 6) return names[1];
        return names[videoMemoryMb < 10 * 1024 ? 2 : 3];
    }

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
        string preset = Recommend(integrated, apple, videoMemoryMb, memoryMb, cores);
        // An integrated GPU's device-local heap is a share of the system's memory, not memory of its own.
        string what = gpu.Length == 0 ? "GPU not found" : integrated ? $"{gpu} (integrated)" : $"{gpu} ({videoMemoryMb / 1024.0:0.#} GB video memory)";
        string summary = $"{what}, {memoryMb / 1024.0:0.#} GB memory, {cores} threads: {preset}";
        return new(preset, summary, gpu, integrated, videoMemoryMb, memoryMb, cores, problem);
    }

    public static int Write(string path)
    {
        var report = Probe();
        File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        Console.WriteLine($"hardware  {report.Summary}");
        return 0;
    }
}
