using System.Runtime.InteropServices;

namespace Meitou.Launcher;

/// <summary>
/// What the launcher can give the game on macOS: native Metal on Apple silicon, and Vulkan/MoltenVK when Homebrew (or a
/// user-supplied <c>DYLD_LIBRARY_PATH</c>) has the loader. The game itself owns the actual renderers; this only decides which
/// one to start and keeps the Vulkan loader out of the game's broad Homebrew dyld path.
/// </summary>
static class MacSupport
{
    /// <summary>
    /// The command a user with Homebrew needs for Vulkan; shown, never run by the launcher.
    /// </summary>
    public const string HomebrewVulkanCommand = "brew install molten-vk vulkan-loader";

    /// <summary>
    /// Whether Metal can be used: macOS on Apple silicon (arm64).
    /// </summary>
    public static bool MetalAvailable =>
        OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64;

    /// <summary>
    /// libc's <c>sysctlbyname</c>, for the Mac's memory size.
    /// </summary>
    [DllImport("libc", SetLastError = true)]
    static extern int sysctlbyname(string name, out ulong oldp, ref ulong oldlenp, IntPtr newp, ulong newlen);

    /// <summary>
    /// The Mac's physical memory in bytes, or 0 when it cannot be read.
    /// </summary>
    public static ulong PhysicalMemoryBytes
    {
        get
        {
            try
            {
                ulong value = 0, length = sizeof(ulong);
                return sysctlbyname("hw.memsize", out value, ref length, IntPtr.Zero, 0) == 0 ? value : 0;
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
            {
                return 0;
            }
        }
    }

    /// <summary>
    /// The 8 GB Metal budget preset is offered on 16 GB machines and larger; an unknown size stays conservative.
    /// </summary>
    public static bool CanUseHighBudget => PhysicalMemoryBytes is 0 or >= 16UL << 30;

    /// <summary>
    /// The folder holding <c>libvulkan.1.dylib</c>, or null when Vulkan is not installed.
    /// </summary>
    public static string? VulkanLoaderDirectory()
    {
        if (!OperatingSystem.IsMacOS()) return null;
        var existing = Environment.GetEnvironmentVariable("DYLD_LIBRARY_PATH");
        return Candidates(existing).FirstOrDefault(dir => File.Exists(Path.Combine(dir, "libvulkan.1.dylib")));
    }

    /// <summary>
    /// Whether the Vulkan loader (Homebrew's) is installed.
    /// </summary>
    public static bool VulkanAvailable => VulkanLoaderDirectory() is not null;

    /// <summary>
    /// The renderer to pass to the game. Auto prefers Vulkan when its loader exists and falls back to Metal.
    /// </summary>
    public static string Resolve(string requested) => requested switch
    {
        "metal" when MetalAvailable => "metal",
        "vulkan" or "auto" when VulkanAvailable => "vulkan",
        "vulkan" when MetalAvailable => "metal",   // explicit Vulkan became unavailable: keep the game startable
        _ when MetalAvailable => "metal",
        _ => "vulkan",
    };

    /// <summary>
    /// Points the game at a narrow folder holding the loader and MoltenVK, and sets the argument-buffer switch.
    /// </summary>
    public static bool AddVulkanEnvironment(IDictionary<string, string?> environment)
    {
        if (VulkanLoaderDirectory() is not { } source) return false;
        var molten = Candidates(environment.TryGetValue("DYLD_LIBRARY_PATH", out var existing) ? existing : null)
            .FirstOrDefault(dir => File.Exists(Path.Combine(dir, "libMoltenVK.dylib")));
        environment["DYLD_LIBRARY_PATH"] = LinkLibraries(source, molten) ?? source;
        if (!environment.TryGetValue("MVK_CONFIG_USE_METAL_ARGUMENT_BUFFERS", out var mvk) || string.IsNullOrEmpty(mvk))
            environment["MVK_CONFIG_USE_METAL_ARGUMENT_BUFFERS"] = "1";
        return true;
    }

    /// <summary>
    /// The folders to look for MoltenVK in: those of <paramref name="existing"/> (a DYLD_LIBRARY_PATH), then Homebrew's two prefixes.
    /// </summary>
    static string[] Candidates(string? existing) =>
        (existing ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries)
            .Concat(["/opt/homebrew/lib", "/usr/local/lib"])
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// The loader is looked up by name from DYLD_LIBRARY_PATH; the ICD JSON still points MoltenVK at its absolute Homebrew path. Links the loader
    /// (and MoltenVK when found) into the data folder's <c>vulkan</c> folder and returns it; null when that fails.
    /// </summary>
    static string? LinkLibraries(string source, string? molten)
    {
        try
        {
            var directory = Path.GetFullPath(Path.Combine(LauncherConfig.DataDirectory, "vulkan"));
            if (Path.GetFullPath(source) == directory) return directory;
            Directory.CreateDirectory(directory);
            Link(Path.Combine(directory, "libvulkan.1.dylib"), Path.Combine(source, "libvulkan.1.dylib"));
            if (molten is not null)
                Link(Path.Combine(directory, "libMoltenVK.dylib"), Path.Combine(molten, "libMoltenVK.dylib"));
            return directory;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Makes <paramref name="path"/> a symbolic link to <paramref name="target"/>, replacing what is there (nothing when they are the same file).
    /// </summary>
    static void Link(string path, string target)
    {
        if (Path.GetFullPath(path) == Path.GetFullPath(target)) return;
        try
        {
            File.Delete(path);
        }
        catch (FileNotFoundException)
        {
        }
        File.CreateSymbolicLink(path, target);
    }
}
