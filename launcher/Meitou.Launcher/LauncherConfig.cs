using System.Text.Json;

namespace Meitou.Launcher;

/// <summary>
/// The launcher's settings and mod presets, in <c>launcher.json</c> in <see cref="DataDirectory"/>.
/// </summary>
public sealed class LauncherConfig
{
    /// <summary>
    /// The launcher's own folder: <c>%LocalAppData%/Meitou</c> on Windows, <c>~/.local/share/Meitou</c> on Linux,
    /// <c>~/Library/Application Support/Meitou</c> on macOS. Holds <c>launcher.json</c> and is the game's working directory, so its
    /// <c>meitou.user.json</c> is shared by every version.
    /// </summary>
    public static string DataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create), "Meitou");

    /// <summary>
    /// The launcher's settings file, <c>launcher.json</c> in the data folder.
    /// </summary>
    static string FilePath => Path.Combine(DataDirectory, "launcher.json");

    /// <summary>
    /// Where <c>versions/</c> and <c>downloads/</c> go: <see cref="InstallFolder"/>, else <see cref="DataDirectory"/>.
    /// </summary>
    public static string InstallDirectory { get; private set; } = DataDirectory;

    /// <summary>
    /// The install folder the user chose; null for the data folder.
    /// </summary>
    string? _installFolder;

    /// <summary>
    /// The folder the user chose for the installed versions; null (or blank) for the data folder. Setting it moves <see cref="InstallDirectory"/>.
    /// </summary>
    public string? InstallFolder
    {
        get => _installFolder;
        set
        {
            _installFolder = string.IsNullOrWhiteSpace(value) ? null : value;
            InstallDirectory = _installFolder ?? DataDirectory;
        }
    }

    /// <summary>
    /// The user confirmed the install and Kenshi folders in the first-start dialog.
    /// </summary>
    public bool FoldersConfirmed { get; set; }

    /// <summary>
    /// The graphics preset was chosen for the hardware once (the first version installed whose game can detect it), or did not need to be (the game's
    /// settings were already saved); the Graphics page's Detect button runs it again.
    /// </summary>
    public bool PresetDetected { get; set; }

    /// <summary>
    /// The Kenshi folder the user chose; null to search for one.
    /// </summary>
    public string? KenshiPath { get; set; }

    /// <summary>
    /// Nightly builds are offered among the versions.
    /// </summary>
    public bool IncludePrereleases { get; set; }

    /// <summary>
    /// macOS only: <c>auto</c> (Vulkan when its loader exists, else Metal), <c>metal</c> or <c>vulkan</c>. Other platforms always use Vulkan.
    /// </summary>
    public string Renderer
    {
        get => _renderer;
        set => _renderer = value is "auto" or "metal" or "vulkan" ? value : "auto";
    }

    /// <summary>
    /// The stored <see cref="Renderer"/>.
    /// </summary>
    string _renderer = "auto";

    /// <summary>
    /// macOS Metal: the soft GPU allocation budget in MB (5120 safe, 6144 balanced, 8120 high). It is a VramGuard ceiling, not a hard reservation;
    /// Vulkan ignores it.
    /// </summary>
    public int MetalMemoryBudget
    {
        get => _metalMemoryBudget;
        set
        {
            if (value is not (5120 or 6144 or 8120))
            {
                _metalMemoryBudget = 5120;
                return;
            }
            _metalMemoryBudget = value == 8120 && !MacSupport.CanUseHighBudget ? 5120 : value;
        }
    }

    /// <summary>
    /// The stored <see cref="MetalMemoryBudget"/>.
    /// </summary>
    int _metalMemoryBudget = 5120;

    /// <summary>
    /// The version Play starts; null for the newest installed.
    /// </summary>
    public string? SelectedVersion { get; set; }

    /// <summary>
    /// A game release the user chose not to be offered as an update; a newer one is offered again.
    /// </summary>
    public string? SkippedVersion { get; set; }

    /// <summary>
    /// Extra command-line arguments for the game (split as a shell would).
    /// </summary>
    public string LaunchArguments { get; set; } = "";

    /// <summary>
    /// Windows: start the game with Kenshi's Steam App ID (steam_appid.txt and SteamAppId), so Steam sees it as Kenshi.
    /// </summary>
    public bool LaunchAsSteamKenshi { get; set; } = true;

    /// <summary>
    /// The locale code of the launcher and the game (also written to the Kenshi folder's settings.cfg <c>language</c>); the settings.cfg wins when it
    /// has one.
    /// </summary>
    public string Language { get; set; } = "en_GB";

    /// <summary>
    /// An installed launcher applies its own update at startup and restarts into it.
    /// </summary>
    public bool AutoUpdateLauncher { get; set; } = true;

    /// <summary>
    /// The saved mod presets.
    /// </summary>
    public List<ModPreset> Presets { get; set; } = [];

    /// <summary>
    /// Reads <c>launcher.json</c>; defaults when it is missing or cannot be read.
    /// </summary>
    public static LauncherConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize(File.ReadAllText(FilePath), LauncherJson.Default.LauncherConfig) ?? new();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
        }
        return new();
    }

    /// <summary>
    /// Writes <c>launcher.json</c> (the data folder made when missing).
    /// </summary>
    public void Save()
    {
        Directory.CreateDirectory(DataDirectory);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, LauncherJson.Default.LauncherConfig));
    }
}

/// <summary>
/// A named mod list: mod names in load order, each enabled or not.
/// </summary>
public sealed class ModPreset
{
    /// <summary>
    /// The preset's name.
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// The mods in load order.
    /// </summary>
    public List<PresetMod> Mods { get; set; } = [];
    public override string ToString() => Name;
}

/// <summary>
/// A mod of a preset: its name and whether it is enabled.
/// </summary>
public sealed class PresetMod
{
    /// <summary>
    /// The mod's name (its folder).
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// The mod is enabled.
    /// </summary>
    public bool Enabled { get; set; }
}

/// <summary>
/// Source-generated JSON for the launcher's files, so it works trimmed.
/// </summary>
[System.Text.Json.Serialization.JsonSourceGenerationOptions(WriteIndented = true)]
[System.Text.Json.Serialization.JsonSerializable(typeof(LauncherConfig))]
[System.Text.Json.Serialization.JsonSerializable(typeof(ReleaseCache.Cache))]
sealed partial class LauncherJson : System.Text.Json.Serialization.JsonSerializerContext;
