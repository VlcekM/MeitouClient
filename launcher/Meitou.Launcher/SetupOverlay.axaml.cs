using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Meitou.Content;

namespace Meitou.Launcher;

/// <summary>
/// The first-start dialog over the main window: the install folder and the Kenshi folder, prefilled with the current (or detected)
/// ones, and on Windows whether to launch as Kenshi on Steam.
/// </summary>
public sealed partial class SetupOverlay : UserControl
{
    /// <summary>
    /// The launcher's settings the dialog writes.
    /// </summary>
    readonly LauncherConfig _config = new();

    /// <summary>
    /// The Kenshi install shown, null when the folder is not one.
    /// </summary>
    GameInstall? _install;

    /// <summary>
    /// Raised once Continue saved the folders, with the Kenshi install chosen (null when none was).
    /// </summary>
    public event Action<GameInstall?>? Confirmed;

    /// <summary>
    /// For the designer.
    /// </summary>
    public SetupOverlay() => InitializeComponent();

    /// <summary>
    /// The dialog prefilled from <paramref name="config"/> and the detected install; the Steam row on Windows, the renderer row on macOS.
    /// </summary>
    public SetupOverlay(LauncherConfig config, GameInstall? found)
    {
        this._config = config;
        InitializeComponent();
        InstallBox.Text = LauncherConfig.InstallDirectory;
        ShowKenshi(found, found?.Root ?? config.KenshiPath);
        SteamRow.IsVisible = OperatingSystem.IsWindows();
        SteamBox.IsChecked = config.LaunchAsSteamKenshi;
        RendererRow.IsVisible = OperatingSystem.IsMacOS();
        RendererMetalItem.IsVisible = MacSupport.MetalAvailable;
        if (!MacSupport.MetalAvailable && config.Renderer == "metal") config.Renderer = "auto";
        RendererBox.SelectedIndex = config.Renderer switch { "metal" => 1, "vulkan" => 2, _ => 0 };
        RendererBox.SelectionChanged += Renderer_Changed;
        ShowRendererHint();
    }

    /// <summary>
    /// Shows the Kenshi folder <paramref name="text"/> and whether it is one (<paramref name="found"/>).
    /// </summary>
    void ShowKenshi(GameInstall? found, string? text)
    {
        _install = found;
        KenshiBox.Text = text ?? "";
        KenshiStatus.Text = found is not null ? "The folder with data/gamedata.base."
            : string.IsNullOrWhiteSpace(text) ? "Not found. Browse to your Kenshi folder (it contains data/gamedata.base)."
            : "Not a Kenshi folder: data/gamedata.base not found.";
    }

    /// <summary>
    /// Uses the typed or picked folder (quotes trimmed).
    /// </summary>
    void SetKenshi(string path)
    {
        path = path.Trim().Trim('"');
        ShowKenshi(GameInstall.IsValid(path) ? GameInstall.Open(path) : null, path);
    }

    /// <summary>
    /// Find: searches for the Kenshi install again.
    /// </summary>
    void FindKenshi_Click(object? sender, RoutedEventArgs e)
    {
        var found = GameInstall.Locate();
        ShowKenshi(found, found?.Root ?? KenshiBox.Text);
    }

    /// <summary>
    /// Opens the guide to running Kenshi's data on macOS.
    /// </summary>
    async void KenshiHelp_Click(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.Launcher is { } launcher)
            await launcher.LaunchUriAsync(new Uri("https://github.com/VlcekM/MeitouClient/blob/master/docs/kenshi-on-macos.md"));
    }

    /// <summary>
    /// macOS renderer picked.
    /// </summary>
    void Renderer_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (RendererBox.SelectedItem is ComboBoxItem { Tag: string renderer } && renderer is "auto" or "metal" or "vulkan")
            _config.Renderer = renderer;
        ShowRendererHint();
    }

    /// <summary>
    /// The macOS renderer hint: which renderer the choice comes to here, and why.
    /// </summary>
    void ShowRendererHint()
    {
        if (RendererHint is null) return;
        bool vulkan = MacSupport.VulkanAvailable;
        string effective = MacSupport.Resolve(_config.Renderer) == "metal" ? "Metal" : "Vulkan";
        string hint = _config.Renderer switch
        {
            "auto" => vulkan ? "Auto: {1} (Vulkan loader found)." : MacSupport.MetalAvailable ? "Auto: {1} (no Vulkan loader)." : "Auto: {1}.",
            "metal" => MacSupport.MetalAvailable ? "Metal (native)." : "Metal is not available on this Mac; Auto uses Vulkan.",
            _ => vulkan ? "Vulkan (MoltenVK loader found)." : "Vulkan loader not found; choose Auto/Metal, or install it later in Settings.",
        };

        // Inlines, not Text: a translation pass would put back the first hint it saw.
        RendererHint.Inlines = [new Run(LauncherText.Translate(hint, effective))];
    }

    /// <summary>
    /// Browse for the Kenshi folder.
    /// </summary>
    async void BrowseKenshi_Click(object? sender, RoutedEventArgs e)
    {
        if (await PickFolder("Kenshi folder") is { } path) SetKenshi(path);
    }

    /// <summary>
    /// The typed Kenshi folder, when it changed, is used.
    /// </summary>
    void KenshiBox_LostFocus(object? sender, RoutedEventArgs e)
    {
        if (KenshiBox.Text is { } path && path != _install?.Root) SetKenshi(path);
    }

    /// <summary>
    /// Browse for the install folder.
    /// </summary>
    async void BrowseInstall_Click(object? sender, RoutedEventArgs e)
    {
        if (await PickFolder("Install folder") is { } path) InstallBox.Text = path;
    }

    /// <summary>
    /// A folder the user picks, null when cancelled.
    /// </summary>
    async Task<string?> PickFolder(string title)
    {
        if (TopLevel.GetTopLevel(this) is not { } top) return null;
        var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title });
        return folders is [var folder] ? folder.TryGetLocalPath() : null;
    }

    /// <summary>
    /// Continue: checks the install folder, saves both folders and the choices and raises <see cref="Confirmed"/>.
    /// </summary>
    async void Continue_Click(object? sender, RoutedEventArgs e)
    {
        if (KenshiBox.Text is { } typed && typed != _install?.Root) SetKenshi(typed);
        var folder = (InstallBox.Text ?? "").Trim().Trim('"');
        if (folder.Length == 0 || !Path.IsPathFullyQualified(folder))
        {
            Problem.Text = LauncherText.Translate("Choose a full path for the install folder.");
            return;
        }
        var previous = LauncherConfig.InstallDirectory;
        IsEnabled = false;
        try
        {
            Directory.CreateDirectory(folder);
            Problem.Text = LauncherText.Translate("Moving installed versions…");
            await Task.Run(() => Releases.MoveInstalled(previous, folder));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Problem.Text = LauncherText.Translate("Could not use {1}: {2}", folder, ex.Message);
            IsEnabled = true;
            return;
        }
        _config.InstallFolder = Path.GetFullPath(folder) == Path.GetFullPath(LauncherConfig.DataDirectory) ? null : folder;
        if (_install is not null) _config.KenshiPath = _install.Root;
        if (RendererBox.SelectedItem is ComboBoxItem { Tag: string renderer } && renderer is "auto" or "metal" or "vulkan")
            _config.Renderer = renderer;
        if (OperatingSystem.IsWindows()) _config.LaunchAsSteamKenshi = SteamBox.IsChecked == true;
        _config.FoldersConfirmed = true;
        _config.Save();
        Confirmed?.Invoke(_install);
    }
}
