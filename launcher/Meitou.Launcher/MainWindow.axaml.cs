using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Meitou.Content;
using Meitou.Localisation;

namespace Meitou.Launcher;

public sealed partial class MainWindow : Window
{
    readonly LauncherConfig config;
    static readonly HttpClient Http = Releases.CreateClient();
    readonly Releases releases = new(Http);
    readonly NewsFeed news = new(Http);
    readonly LauncherUpdater updater;
    readonly ObservableCollection<ModRow> mods = [];
    List<Release> allReleases = [];
    List<string> installedVersions = [];
    readonly MenuFlyout versionMenu = new() { Placement = PlacementMode.TopEdgeAlignedRight };
    GameInstall? install;
    CancellationTokenSource? download;
    ModRow? dragged;
    readonly Dictionary<Control, string> originalUiText = [];
    bool languageReady, populatingLanguages;
    int modsLoad;
    bool modsReady;
    static string Tr(string text) => LauncherText.Translate(text);

    const uint WS_CAPTION = 0x00C00000, WS_SYSMENU = 0x00080000, WS_MINIMIZEBOX = 0x00020000, WS_MAXIMIZEBOX = 0x00010000;

    static string LauncherVersion => typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public MainWindow() : this(LauncherConfig.Load(), GameInstall.Locate()) { }

    public MainWindow(LauncherConfig config, GameInstall? found)
    {
        this.config = config;
        updater = new LauncherUpdater(releases, config.IncludePrereleases);
        InitializeComponent();
        LauncherText.Use(StartLanguage(found), found);
        PopulateLanguageOptions(found);
        LauncherText.TranslateTree(this, originalUiText);
        languageReady = true;
        // Windows animates minimise, maximise and restore only for windows with a caption style. Avalonia sizes the client area of an
        // undecorated window to the whole window whatever its styles, so the caption bits add the animations and no frame.
        if (OperatingSystem.IsWindows())
            Win32Properties.AddWindowStylesCallback(this, (style, exStyle) => (style | WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX, exStyle));
        PlayVersionButton.Flyout = versionMenu;
        TrackPointer();
        ModListBox.ItemsSource = mods;
        ModListBox.AddHandler(PointerMovedEvent, ModList_PointerMoved, RoutingStrategies.Bubble, handledEventsToo: true);
        ModListBox.AddHandler(PointerReleasedEvent, ModList_PointerReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        ModListBox.AddHandler(PointerCaptureLostEvent, (_, _) => EndDrag(), RoutingStrategies.Direct | RoutingStrategies.Bubble, handledEventsToo: true);
        ArgumentsBox.Text = config.LaunchArguments;
        SteamRow.IsVisible = OperatingSystem.IsWindows();
        SteamBox.IsChecked = config.LaunchAsSteamKenshi;
        RendererRow.IsVisible = OperatingSystem.IsMacOS();
        RendererMetalItem.IsVisible = MacSupport.MetalAvailable;
        if (!MacSupport.MetalAvailable && config.Renderer == "metal") config.Renderer = "auto";
        RendererBox.SelectedIndex = config.Renderer switch { "metal" => 1, "vulkan" => 2, _ => 0 };
        RendererBox.SelectionChanged += Renderer_Changed;
        UpdateRendererHint();
        MetalBudgetRow.IsVisible = OperatingSystem.IsMacOS() && MacSupport.MetalAvailable;
        MetalBudgetHighItem.IsVisible = MacSupport.CanUseHighBudget;
        if (!MacSupport.CanUseHighBudget && config.MetalMemoryBudget == 8120) config.MetalMemoryBudget = 5120;
        MetalBudgetBox.SelectedIndex = config.MetalMemoryBudget switch { 8120 => 2, 6144 => 1, _ => 0 };
        MetalBudgetBox.SelectionChanged += MetalBudget_Changed;
        UpdateMetalBudgetHint();
        NightlyBox.IsChecked = config.IncludePrereleases;
        AutoUpdateBox.IsChecked = config.AutoUpdateLauncher;
        AutoUpdateBox.IsEnabled = updater.IsInstalled;
        ShowReportButton();
        DataFolderText.Text = $"The launcher's and the game's settings: {LauncherConfig.DataDirectory}";
        InstallPathBox.Text = LauncherConfig.InstallDirectory;
        LauncherVersionText.Text = $"v{LauncherVersion} · {Releases.Rid}";
        LauncherAbout.Text = $"Version {LauncherVersion} for {Releases.Rid}. "
            + (updater.IsInstalled ? "Updates itself from GitHub." : "Not installed by its Setup, so updates open the release page.");
        RefreshPresets();
        RefreshInstalled();
        UseKenshi(found);
        if (!config.FoldersConfirmed) ShowSetup();
        Closing += (_, _) => { if (graphicsSave?.IsEnabled == true) SaveGame(); };
        Opened += async (_, _) =>
        {
            // Apply after Avalonia has attached control templates; the constructor pass runs before some text presenters exist.
            LauncherText.TranslateTree(this, originalUiText);
            await Task.WhenAll(LoadNewsAsync(), RefreshReleasesAsync(updateLauncher: true));
        };
    }

    void Status(string text) => SetText(StatusText, text);

    // ---- Language: one choice for the game (Kenshi's settings.cfg) and the launcher ----

    /// <summary>The language to start in: the Kenshi folder's settings.cfg choice, else the launcher's last one.</summary>
    string StartLanguage(GameInstall? found) =>
        (found is null ? null : KenshiLanguageSettings.GetLanguage(found)) is { Length: > 0 } game ? game : config.Language;

    void Language_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!languageReady || populatingLanguages) return;
        if (LanguageBox.SelectedItem is not ComboBoxItem item || item.Tag is not string code || code == LauncherText.Code) return;
        config.Language = code;
        config.Save();
        LauncherText.Use(code, install);
        if (install is not null)
        {
            try
            {
                KenshiLanguageSettings.SetLanguage(install, code);
                Status(Loc.Format(Tr("Game language set to {1}. It applies when the game starts."), code));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Status(Loc.Format(Tr("Could not update Kenshi's language setting: {1}"), error.Message));
            }
        }
        LauncherText.TranslateTree(this, originalUiText);
        foreach (var row in mods) row.LanguageChanged();
        if (modsReady) ShowModCount();
        ShowReleases();
        PopulateLanguageOptions(install);
    }

    // Opening the list reads the catalogs again, so a meitou.po added or edited meanwhile shows up.
    void Language_DropDownOpened(object? sender, EventArgs e)
    {
        LauncherText.Use(LauncherText.Code, install);
        PopulateLanguageOptions(install);
        LauncherText.TranslateTree(this, originalUiText);
    }

    /// <summary>English, Kenshi's own languages, the locale folders of the install and the languages Meitou has texts for; the current one selected.</summary>
    void PopulateLanguageOptions(GameInstall? gameInstall)
    {
        var codes = new List<string> { GameLanguage.DefaultCode };
        foreach (string code in GameLanguage.SteamLanguages.Select(l => l.Code).Concat(MeitouTranslations.EmbeddedCodes).Concat(InstalledLocaleCodes(gameInstall)))
            if (!codes.Any(known => SameLanguage(known, code))) codes.Add(code);
        populatingLanguages = true;
        LanguageBox.Items.Clear();
        foreach (string code in codes)
        {
            bool translated = code == GameLanguage.DefaultCode || MeitouTranslations.Has(code, LauncherText.LocaleFolder(gameInstall, code));
            string name = translated ? GameLanguageName(code) : $"{GameLanguageName(code)} ({Tr("Meitou's texts not yet translated")})";
            LanguageBox.Items.Add(new ComboBoxItem { Content = name, Tag = code });
        }
        LanguageBox.SelectedItem = LanguageBox.Items.OfType<ComboBoxItem>().FirstOrDefault(item => item.Tag is string code && SameLanguage(code, LauncherText.Code))
            ?? LanguageBox.Items.OfType<ComboBoxItem>().First();
        populatingLanguages = false;
    }

    static bool SameLanguage(string left, string right) => left.Replace('_', '-').Equals(right.Replace('_', '-'), StringComparison.OrdinalIgnoreCase);

    static IEnumerable<string> InstalledLocaleCodes(GameInstall? gameInstall)
    {
        if (gameInstall is null) return [];
        try
        {
            string root = Path.Combine(gameInstall.Root, "locale");
            return Directory.Exists(root)
                ? Directory.EnumerateDirectories(root).Select(Path.GetFileName).OfType<string>().Where(code => !code.StartsWith('_')).ToArray()
                : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }

    static string GameLanguageName(string code) => code.Replace('_', '-').ToLowerInvariant() switch
    {
        "en-gb" => "English",
        "de-de" => "German",
        "fr-fr" => "French",
        "ru-ru" => "Russian",
        "it-it" => "Italian",
        "pt-br" => "Portuguese (Brazil)",
        "es-es" => "Spanish",
        "zh-cn" => "Chinese",
        "ja-jp" => "Japanese",
        "ko-kr" => "Korean",
        _ => code,
    };

    // ---- Pages ----

    void Nav_Click(object? sender, RoutedEventArgs e)
    {
        foreach (var (nav, page) in new (ToggleButton, Control)[] { (NavHome, HomePage), (NavMods, ModsPage), (NavVersions, VersionsPage), (NavGraphics, GraphicsPage), (NavSettings, SettingsPage) })
        {
            nav.IsChecked = nav == sender;
            page.IsVisible = nav == sender;
        }
        if (sender == NavGraphics) ShowGraphics();
        Control? selectedPage = sender == NavHome ? HomePage
            : sender == NavMods ? ModsPage
            : sender == NavVersions ? VersionsPage
            : sender == NavGraphics ? GraphicsPage
            : sender == NavSettings ? SettingsPage : null;
        if (selectedPage is not null)
        {
            EventHandler? applyAfterLayout = null;
            applyAfterLayout = (_, _) =>
            {
                selectedPage.LayoutUpdated -= applyAfterLayout;
                LauncherText.TranslateTree(selectedPage, originalUiText);
            };
            selectedPage.LayoutUpdated += applyAfterLayout;
        }
    }

    // ---- Home: news ----

    async Task LoadNewsAsync()
    {
        NewsCount.Text = "Loading";
        var (posts, problem) = await news.FetchAsync();
        NewsPanel.Children.Clear();
        foreach (var post in posts) NewsPanel.Children.Add(PostCard(post));
        if (posts.Count == 0) NewsPanel.Children.Add(new TextBlock { Classes = { "desc" }, Text = problem ?? "No news yet." });
        NewsCount.Text = NewsFeed.LocalFolder is not null ? $"Debug: local news/, {posts.Count} posts"
            : posts.Count == 0 ? "" : problem is not null ? "Offline: saved posts" : posts.Count == 1 ? "1 post" : $"{posts.Count} posts";
    }

    Control PostCard(NewsPost post)
    {
        var card = new StackPanel { Spacing = 8 };
        if (post.Image is { } cover) card.Children.Add(MarkdownView.Image(NewsFeed.Resolve(post, cover), news, post.Video is { } video ? (NewsFeed.Resolve(post, video), OpenLink) : null));
        // The title, with "19 October 2026 · author" (either part when only it is given) on its right.
        var heading = new DockPanel();
        var byline = string.Join(" · ", new[] { post.Date?.ToString("d MMMM yyyy", System.Globalization.CultureInfo.InvariantCulture), post.Author }.Where(s => !string.IsNullOrEmpty(s)));
        if (byline.Length > 0)
        {
            var aside = new TextBlock { Classes = { "desc" }, Text = byline, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, Margin = new Avalonia.Thickness(16, 0, 0, 0) };
            DockPanel.SetDock(aside, Dock.Right);
            heading.Children.Add(aside);
        }
        heading.Children.Add(new TextBlock { Text = post.Title, FontSize = 22, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        card.Children.Add(heading);
        card.Children.Add(MarkdownView.Render(post, news, OpenLink));
        return new Border { Classes = { "post" }, Child = card };
    }

    // Links in posts open in the browser; only web addresses.
    async void OpenLink(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http")
            await this.Launcher.LaunchUriAsync(uri);
    }

    void Kofi_Click(object? sender, RoutedEventArgs e) => OpenLink("https://ko-fi.com/martinvlcek");

    // ---- Kenshi folder ----

    void UseKenshi(GameInstall? found)
    {
        install = found;
        KenshiPathBox.Text = found?.Root ?? config.KenshiPath ?? "";
        KenshiStatus.Text = found is null ? "Not found: browse to the folder with data/gamedata.base."
            : found.WorkshopDirectory is null ? "The folder with data/gamedata.base." : "The folder with data/gamedata.base; Steam workshop mods found.";
        KenshiBadge.Text = found is null ? "Kenshi not found: set its folder in Settings" : found.Root;
        if (found is not null && config.KenshiPath != found.Root)
        {
            config.KenshiPath = found.Root;
            config.Save();
        }
        if (languageReady)
        {
            LauncherText.Use(StartLanguage(found), found);
            PopulateLanguageOptions(found);
            LauncherText.TranslateTree(this, originalUiText);
        }
        LoadMods();
    }

    void DetectKenshi_Click(object? sender, RoutedEventArgs e) => UseKenshi(GameInstall.Locate());

    async void BrowseKenshi_Click(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Kenshi folder" });
        if (folders is [var folder] && folder.TryGetLocalPath() is { } path)
            SetKenshiPath(path);
    }

    void KenshiPath_LostFocus(object? sender, RoutedEventArgs e)
    {
        if (KenshiPathBox.Text is { } path && path != install?.Root)
            SetKenshiPath(path.Trim().Trim('"'));
    }

    void SetKenshiPath(string path)
    {
        if (GameInstall.IsValid(path)) UseKenshi(GameInstall.Open(path));
        else KenshiStatus.Text = $"'{path}' is not a Kenshi folder (data/gamedata.base not found).";
    }

    void Arguments_LostFocus(object? sender, RoutedEventArgs e)
    {
        config.LaunchArguments = ArgumentsBox.Text ?? "";
        config.Save();
    }

    void Renderer_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (RendererBox.SelectedItem is ComboBoxItem { Tag: string renderer } && renderer is "auto" or "metal" or "vulkan")
        {
            config.Renderer = renderer;
            config.Save();
            UpdateRendererHint();
            Status($"Renderer: {RendererLabel(MacSupport.Resolve(renderer))}; Play starts the game with it.");
        }
    }

    void UpdateRendererHint()
    {
        bool vulkan = MacSupport.VulkanAvailable;
        string effective = RendererLabel(MacSupport.Resolve(config.Renderer));
        RendererHint.Text = config.Renderer switch
        {
            "auto" => $"Auto: {effective}" + (vulkan ? " (Vulkan loader found)." : MacSupport.MetalAvailable ? " (no Vulkan loader)." : "."),
            "metal" => MacSupport.MetalAvailable ? "Metal (native)." : "Metal is not available on this Mac; Auto uses Vulkan.",
            _ => vulkan ? "Vulkan (MoltenVK loader found)." : "Vulkan loader not found. Use Auto/Metal or install it with Setup.",
        };
    }

    void MetalBudget_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (MetalBudgetBox.SelectedItem is ComboBoxItem { Tag: string tag } && int.TryParse(tag, out int mb))
        {
            config.MetalMemoryBudget = mb;
            UpdateMetalBudgetHint();
            Status($"Metal memory budget: {config.MetalMemoryBudget} MB.");
        }
    }

    void UpdateMetalBudgetHint()
    {
        MetalBudgetHint.Text = config.MetalMemoryBudget switch
        {
            8120 => "High: keeps the most textures, terrain and objects resident. Best on a 16 GB-or-larger Mac with other memory-heavy apps closed.",
            6144 => "Balanced: more resident detail than Safe, with some headroom for macOS and other apps.",
            _ => "Safe: the conservative default; the guard shortens ranges and evicts caches sooner.",
        };
    }

    static string RendererLabel(string renderer) => renderer == "metal" ? "Metal" : "Vulkan";

    async void VulkanSetup_Click(object? sender, RoutedEventArgs e)
    {
        if (MacSupport.VulkanAvailable)
        {
            UpdateRendererHint();
            Status("The Vulkan loader is already installed. Select Vulkan to use it.");
            return;
        }
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            await clipboard.SetValueAsync(DataFormat.Text, MacSupport.HomebrewVulkanCommand);
        await this.Launcher.LaunchUriAsync(new Uri("https://brew.sh"));
        Status($"Copied '{MacSupport.HomebrewVulkanCommand}' and opened brew.sh; install Homebrew, run it in Terminal, then press Check.");
    }

    void VulkanCheck_Click(object? sender, RoutedEventArgs e)
    {
        UpdateRendererHint();
        Status(MacSupport.VulkanAvailable
            ? "Vulkan loader found. Select Vulkan to use it."
            : "Vulkan loader not found. Install Homebrew and MoltenVK, or use Auto/Metal.");
    }

    async void KenshiHelp_Click(object? sender, RoutedEventArgs e) =>
        await this.Launcher.LaunchUriAsync(new Uri("https://github.com/VlcekM/MeitouClient/blob/master/docs/kenshi-on-macos.md"));

    async void Nightly_Click(object? sender, RoutedEventArgs e)
    {
        if (NightlyBox.IsChecked == true && !config.IncludePrereleases)
        {
            bool sure = await AskAsync("Nightly builds",
                "Nightly builds are made twice a day from the newest code and are not tested. They may be unstable, crash, and corrupt your save files: "
                + "back up your saves before playing one.\n\nContinue with nightly builds?", "Yes", "No");
            if (!sure) { NightlyBox.IsChecked = false; return; }
        }
        config.IncludePrereleases = NightlyBox.IsChecked == true;
        config.Save();
        ShowReleases();
    }

    string Latest => config.IncludePrereleases ? "latest nightly" : "latest";

    async void OpenDataFolder_Click(object? sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(LauncherConfig.DataDirectory);
        await this.Launcher.LaunchUriAsync(new Uri(LauncherConfig.DataDirectory + Path.DirectorySeparatorChar));
    }

    async void BrowseInstall_Click(object? sender, RoutedEventArgs e)
    {
        if (download is not null) { InstallStatus.Text = "Wait for the download to finish first."; return; }
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Install folder" });
        if (folders is not [var folder] || folder.TryGetLocalPath() is not { } path || path == LauncherConfig.InstallDirectory) return;
        var previous = LauncherConfig.InstallDirectory;
        InstallStatus.Text = "Moving installed versions…";
        try { await Task.Run(() => Releases.MoveInstalled(previous, path)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            InstallStatus.Text = $"Could not move the versions to {path}: {ex.Message}";
            RefreshInstalled();
            return;
        }
        config.InstallFolder = Path.GetFullPath(path) == Path.GetFullPath(LauncherConfig.DataDirectory) ? null : path;
        config.Save();
        InstallPathBox.Text = LauncherConfig.InstallDirectory;
        InstallStatus.Text = "Where versions are installed; changing it moves them.";
        RefreshInstalled();
        ShowReleases();
    }

    async void OpenInstallFolder_Click(object? sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(LauncherConfig.InstallDirectory);
        await this.Launcher.LaunchUriAsync(new Uri(LauncherConfig.InstallDirectory + Path.DirectorySeparatorChar));
    }

    void ShowSetup()
    {
        var setup = new SetupOverlay(config, install);
        setup.Confirmed += chosen =>
        {
            SetupHost.IsVisible = false;
            SetupHost.Child = null;
            InstallPathBox.Text = LauncherConfig.InstallDirectory;
            SteamBox.IsChecked = config.LaunchAsSteamKenshi;
            RefreshInstalled();
            ShowReleases();
            UseKenshi(chosen);
        };
        SetupHost.Child = setup;
        SetupHost.IsVisible = true;
    }

    // ---- Mods ----

    // Off the UI thread, so a large workshop folder does not hold up the window: listing the folders and reading every header (for the
    // dependency order and the authors) is file work per mod. Until it is done the list is not saved (Play, presets), so a click
    // meanwhile cannot write an empty mods.cfg. A later load (another Kenshi folder, Refresh) supersedes an unfinished one.
    async void LoadMods()
    {
        int load = ++modsLoad;
        modsReady = false;
        mods.Clear();
        ModsEmpty.IsVisible = false;
        ModCount.Text = Tr("Loading");
        var gameInstall = install;
        List<ModEntry> entries;
        try
        {
            entries = gameInstall is null ? [] : await Task.Run(() =>
            {
                var order = ModList.LauncherOrder(gameInstall, ModList.Discover(gameInstall));
                // The rows show each mod's author and plugin note: read them here too, not on the UI thread.
                foreach (var entry in order) { _ = entry.Mod.Header; _ = entry.Mod.HasNativePlugin; }
                return order;
            });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            if (load != modsLoad) return;
            ModCount.Text = "";
            ModsEmpty.IsVisible = true;
            ModsEmpty.Text = Loc.Format(Tr("Could not read the mods: {1}"), e.Message);
            return;
        }
        if (load != modsLoad) return;
        foreach (var entry in entries) mods.Add(new ModRow(entry.Mod, entry.Enabled));
        foreach (var row in mods) row.PropertyChanged += (_, _) => ShowModCount();
        modsReady = true;
        ShowModCount();
        ModsEmpty.IsVisible = mods.Count == 0;
        ModsEmpty.Text = Tr(gameInstall is null ? "Set the Kenshi folder in Settings to list its mods."
            : "No mods found in Kenshi's mods folder or among its Steam workshop items.");
    }

    void ShowModCount() => ModCount.Text = mods.Count == 0 ? "No mods found" : $"{mods.Count(m => m.Enabled)} of {mods.Count} enabled";

    // False while the list is loading or after it could not be read; Status says why.
    bool ModsReady()
    {
        if (!modsReady) Status(ModsEmpty.IsVisible ? ModsEmpty.Text ?? "" : "The mod list is still loading; try again in a moment.");
        return modsReady;
    }

    bool SaveMods()
    {
        if (install is null) { Status("Set the Kenshi folder first (Settings)."); return false; }
        if (!ModsReady()) return false;
        try
        {
            ModList.Save(install, mods.Select(m => new ModEntry(m.Mod, m.Enabled)));
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Status($"Could not save the mod list: {e.Message}");
            return false;
        }
    }

    void SaveMods_Click(object? sender, RoutedEventArgs e)
    {
        if (SaveMods()) Status($"Mod list saved: {mods.Count(m => m.Enabled)} enabled.");
    }

    void RefreshMods_Click(object? sender, RoutedEventArgs e) => LoadMods();

    void SelectAll_Click(object? sender, RoutedEventArgs e) { foreach (var m in mods) m.Enabled = true; }

    void SelectNone_Click(object? sender, RoutedEventArgs e) { foreach (var m in mods) m.Enabled = false; }

    // Alt+Up / Alt+Down move the selected mod, as dragging does.
    void ModListBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.Alt || e.Key is not (Key.Up or Key.Down)) return;
        int i = ModListBox.SelectedIndex, j = i + (e.Key == Key.Up ? -1 : 1);
        if (i >= 0 && j >= 0 && j < mods.Count)
        {
            mods.Move(i, j);
            ModListBox.SelectedIndex = j;
        }
        e.Handled = true;
    }

    // Dragging a row's handle moves the row live under the pointer; the list holds the pointer capture, so rows can be
    // re-created underneath without ending the drag.
    void DragHandle_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: ModRow row } || !e.GetCurrentPoint(ModListBox).Properties.IsLeftButtonPressed) return;
        dragged = row;
        ModListBox.SelectedItem = row;
        e.Pointer.Capture(ModListBox);
        MarkDragged(true);
        e.Handled = true;
    }

    void ModList_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (dragged is null) return;
        double y = e.GetPosition(ModListBox).Y;
        int from = mods.IndexOf(dragged), to = from;
        for (int i = 0; i < mods.Count; i++)
        {
            if (ModListBox.ContainerFromIndex(i) is not Control item || item.TranslatePoint(new Point(0, 0), ModListBox) is not { } top) continue;
            if (y >= top.Y && y < top.Y + item.Bounds.Height) { to = i; break; }
        }
        if (to == from) return;
        MarkDragged(false);
        mods.Move(from, to);
        ModListBox.SelectedItem = dragged;
        ModListBox.UpdateLayout(); // the next hit test needs the rows where they are now
        MarkDragged(true);
    }

    void ModList_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (dragged is null) return;
        e.Pointer.Capture(null);
        EndDrag();
    }

    void EndDrag()
    {
        if (dragged is null) return;
        MarkDragged(false);
        dragged = null;
    }

    void MarkDragged(bool on)
    {
        if (dragged is not null && ModListBox.ContainerFromItem(dragged) is { } item)
            item.Classes.Set("dragging", on);
    }

    void ModListBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ModListBox.SelectedItem is not ModRow row) { ModDetails.Text = ""; return; }
        var header = row.Mod.Header;
        var parts = new List<string>();
        if (header is { Description.Length: > 0 }) parts.Add(header.Description.ReplaceLineEndings(" "));
        if (header is { Dependencies.Count: > 0 })
        {
            var missing = header.Dependencies.Where(d => !IsAvailableBefore(d, row)).ToList();
            parts.Add($"Needs {string.Join(", ", header.Dependencies)}" + (missing.Count > 0 ? $" (not enabled above it: {string.Join(", ", missing)})" : ""));
        }
        parts.Add(row.Mod.Folder);
        ModDetails.Text = string.Join(" · ", parts);
    }

    // A dependency is met by a core file or by an enabled mod earlier in the list.
    bool IsAvailableBefore(string dependency, ModRow row)
    {
        if (dependency is "gamedata.base" or "Newwworld.mod" or "Dialogue.mod" or "rebirth.mod") return true;
        foreach (var m in mods)
        {
            if (m == row) return false;
            if (m.Enabled && m.Name + ".mod" == dependency) return true;
        }
        return false;
    }

    // ---- Presets ----

    void RefreshPresets(string? select = null)
    {
        PresetBox.ItemsSource = config.Presets.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        PresetBox.SelectedItem = config.Presets.FirstOrDefault(p => p.Name == select);
    }

    void SavePreset_Click(object? sender, RoutedEventArgs e)
    {
        var name = PresetName.Text?.Trim();
        if (string.IsNullOrEmpty(name)) { Status("Type a name for the preset."); return; }
        if (!ModsReady()) return;
        config.Presets.RemoveAll(p => p.Name == name);
        config.Presets.Add(new ModPreset { Name = name, Mods = [.. mods.Select(m => new PresetMod { Name = m.Name, Enabled = m.Enabled })] });
        config.Save();
        PresetName.Text = "";
        RefreshPresets(name);
        Status($"Preset '{name}' saved.");
    }

    void ApplyPreset_Click(object? sender, RoutedEventArgs e)
    {
        if (PresetBox.SelectedItem is not ModPreset preset || !ModsReady()) return;
        var byName = mods.ToDictionary(m => m.Name, StringComparer.Ordinal);
        var ordered = new List<ModRow>();
        var missing = new List<string>();
        foreach (var p in preset.Mods)
        {
            if (byName.Remove(p.Name, out var row)) { row.Enabled = p.Enabled; ordered.Add(row); }
            else if (p.Enabled) missing.Add(p.Name);
        }
        foreach (var row in mods.Where(m => byName.ContainsKey(m.Name))) { row.Enabled = false; ordered.Add(row); }
        mods.Clear();
        foreach (var row in ordered) mods.Add(row);
        ShowModCount();
        Status($"Preset '{preset.Name}' applied (not saved yet)." + (missing.Count > 0 ? $" Not on disk: {string.Join(", ", missing)}." : ""));
    }

    void DeletePreset_Click(object? sender, RoutedEventArgs e)
    {
        if (PresetBox.SelectedItem is not ModPreset preset) return;
        config.Presets.Remove(preset);
        config.Save();
        RefreshPresets();
        Status($"Preset '{preset.Name}' deleted.");
    }

    // ---- Versions ----

    void RefreshInstalled()
    {
        installedVersions = Releases.Installed();
        ShowPlay();
        ShowFooter();
        _ = AutoDetectPresetAsync();
    }

    // The version picked to play (config.SelectedVersion) while it is installed; null plays the latest.
    string? PickedVersion => installedVersions.Contains(config.SelectedVersion ?? "") ? config.SelectedVersion : null;

    // The version Play starts: the picked one, else the newest installed.
    string? PlayVersion => PickedVersion ?? installedVersions.FirstOrDefault();

    // The latest release when it is newer than every installed version and not skipped.
    Release? NewRelease => installedVersions.Count > 0 && LatestRelease is { } latest
        && Releases.CompareVersions(latest.Version, installedVersions[0]) > 0
        && latest.Version != config.SkippedVersion ? latest : null;

    // Playing the latest, with a new release: Play is the button that installs it.
    bool UpdatePending => PickedVersion is null && NewRelease is not null;

    // "Play latest" or "Play v0.13.0" (or "Update to v0.14.1" while UpdatePending), and the arrow's menu: Latest, then each
    // installed version. During a download Play keeps its look and shows the progress.
    void ShowPlay()
    {
        var picked = PickedVersion;
        if (download is null)
        {
            bool pending = UpdatePending;
            PlayButton.Classes.Set("pending", pending);
            PlayVersionButton.Classes.Set("pending", pending);
            PlayIcon.IsVisible = !pending;
            UpdateIcon.IsVisible = pending;
            SetText(PlayText, pending ? $"Update to {Releases.Display(LatestRelease!.Version)}" : picked is null ? $"Play {Latest}" : $"Play {Releases.Display(picked)}");
            ToolTip.SetTip(PlayButton, pending ? $"Download and install {LatestRelease!.Title}; the arrow plays an installed version"
                : PlayVersion is { } played ? $"Start Meitou {played}" : null);
        }
        versionMenu.Items.Clear();
        versionMenu.Items.Add(VersionItem(UpdatePending ? $"Latest (update to {Releases.Display(LatestRelease!.Version)})"
            : installedVersions.FirstOrDefault() is { } newest ? $"Latest ({Releases.Display(newest)})" : "Latest", null, picked));
        foreach (var version in installedVersions) versionMenu.Items.Add(VersionItem($"v{Releases.ParseVersion(version).Number}", version, picked));
        // Its settings are the played version's.
        if (GraphicsPage.IsVisible) ShowGraphics();
        // A skipped release stays installable from here.
        if (LatestRelease is { } latest && latest.Version == config.SkippedVersion && !installedVersions.Contains(latest.Version))
        {
            var item = new MenuItem { Header = $"Install v{latest.Version} (skipped)" };
            item.Click += async (_, _) =>
            {
                config.SkippedVersion = null;
                config.Save();
                if (download is null) await InstallAsync(latest);
            };
            versionMenu.Items.Add(new Separator());
            versionMenu.Items.Add(item);
        }
    }

    // Skip: the new release is no longer offered, until a newer one comes out.
    void SkipVersion_Click(object? sender, RoutedEventArgs e)
    {
        if (download is not null || NewRelease is not { } release) return;
        config.SkippedVersion = release.Version;
        config.Save();
        ShowFooter();
        Status($"Skipped {release.Title}: the arrow's menu can still install it.");
    }

    MenuItem VersionItem(string header, string? version, string? picked)
    {
        object content = header;
        // A nightly wears a brass tag with its build date, and a same-day second build its number: "v0.15.1  NIGHTLY 20 OCT #2".
        if (version is not null && version.Contains("-nightly.", StringComparison.Ordinal))
        {
            var parsed = Releases.ParseVersion(version);
            var date = parsed.Nightly?.ToString(" d MMM", System.Globalization.CultureInfo.InvariantCulture) + (parsed.Build > 1 ? $" #{parsed.Build}" : "");
            content = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 10,
                Children =
                {
                    new TextBlock { Text = header },
                    new TextBlock { Classes = { "warn" }, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, Text = ("nightly" + date).ToUpperInvariant() },
                },
            };
        }
        var item = new MenuItem { Header = content, ToggleType = MenuItemToggleType.Radio, GroupName = "play", IsChecked = version == picked };
        item.Click += (_, _) => PickVersion(version);
        return item;
    }

    // Play starts this version from now on; null: the latest installed.
    void PickVersion(string? version)
    {
        config.SelectedVersion = version;
        config.Save();
        ShowPlay();
        ShowReleases();
        Status(version is null ? "Play starts the latest installed version." : $"Play starts Meitou {version}.");
    }

    void SelectVersion_Click(object? sender, RoutedEventArgs e)
    {
        if (ReleaseListBox.SelectedItem is ReleaseRow { Installed: true, Release: var release })
            PickVersion(release.Version == installedVersions.FirstOrDefault() ? null : release.Version);
    }

    // The newest game release with a build for this platform, honouring the pre-release setting.
    Release? LatestRelease => allReleases.FirstOrDefault(r => r.Kind == ReleaseKind.Game && (config.IncludePrereleases || !r.Prerelease) && r.Asset is not null);

    // With no version installed the footer offers Install latest instead of the version box and Play; with versions installed
    // but not the latest, an update button beside the version box installs it and selects it.
    void ShowFooter()
    {
        if (download is not null) return;
        var installed = installedVersions;
        var latest = LatestRelease;
        bool none = installed.Count == 0;
        PlayPanel.IsVisible = !none;
        InstallLatestButton.IsVisible = none;
        SetText(InstallLatestText, $"Install {Latest}");
        InstallLatestButton.IsEnabled = latest is not null;
        ToolTip.SetTip(InstallLatestButton, latest is not null ? $"Download and install {latest.Title}" : $"No release with a {Releases.Rid} build found yet");
        // Playing the latest, Play itself is the update (ShowPlay); playing a picked version, this button beside it.
        UpdateButton.IsVisible = PickedVersion is not null && NewRelease is not null;
        SkipButton.IsVisible = NewRelease is not null;
        SkipText.Text = Loc.Format(Tr("Skip v{1}"), NewRelease?.Version);
        UpdateTitle.Text = Loc.Format(Tr("New version {1}"), latest?.Version);
        SetText(UpdateHint, "Click to install");
        ToolTip.SetTip(UpdateButton, $"Download and install {latest?.Title}");
        ShowPlay();
    }

    // Install shows Installed for an installed release and Cancel during a download; Uninstall and Open folder need it installed.
    void ShowVersionButtons()
    {
        var row = ReleaseListBox.SelectedItem as ReleaseRow;
        bool installed = row?.Installed == true;
        InstallButton.Content = Tr(download is not null ? "Cancel" : installed ? "Installed" : "Install");
        InstallButton.IsEnabled = download is not null || (row?.Release.Asset is not null && !installed);
        UninstallButton.IsEnabled = installed && download is null;
        OpenFolderButton.IsEnabled = installed;
        SelectVersionButton.Content = Tr(row?.Played == true ? "Selected" : "Select");
        SelectVersionButton.IsEnabled = installed && row?.Played != true;
    }

    // At startup (updateLauncher) an installed launcher also applies its own update and restarts into it, unless turned off in Settings.
    async Task RefreshReleasesAsync(bool updateLauncher = false)
    {
        Status("Checking GitHub for releases...");
        var (list, problem) = await releases.FetchAsync();
        allReleases = list;
        ShowReleases();
        Status(problem ?? "");
        await ShowLauncherUpdateAsync();   // after: its "Launcher … is available" line must not be cleared
        if (updateLauncher && config.AutoUpdateLauncher && updater.IsInstalled && LauncherUpdateButton.IsVisible) await UpdateLauncherAsync();
    }

    void ShowReleases()
    {
        var installed = Releases.Installed();
        var selected = (ReleaseListBox.SelectedItem as ReleaseRow)?.Release.Tag;
        var rows = allReleases
            .Where(r => r.Kind == ReleaseKind.Game && (config.IncludePrereleases || !r.Prerelease || installed.Contains(r.Version)))
            .Select(r => new ReleaseRow(r, installed.Contains(r.Version), r.Version == PlayVersion))
            .ToList();
        ReleaseListBox.ItemsSource = rows;
        ReleaseListBox.SelectedItem = rows.FirstOrDefault(r => r.Release.Tag == selected);
        NotesEmpty.IsVisible = ReleaseListBox.SelectedItem is null;
        ReleasesEmpty.IsVisible = rows.Count == 0;
        ReleasesEmpty.Text = Tr(allReleases.Any(r => r.Kind == ReleaseKind.Game) ? "Only nightly builds so far: tick Nightly builds beside Play to list them."
            : "No releases listed yet. Check for releases below.");
        VersionCount.Text = $"{rows.Count} releases · {installed.Count} installed";
        ShowVersionButtons();
        ShowFooter();
    }

    async void RefreshReleases_Click(object? sender, RoutedEventArgs e) => await RefreshReleasesAsync();

    void ReleaseListBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var release = (ReleaseListBox.SelectedItem as ReleaseRow)?.Release;
        ReleaseNotes.Content = release is null ? null
            : MarkdownView.Render(new NewsPost("", release.Title, null, null, null, ReleaseNotesMarkdown(release.Notes), release.PageUrl), news, OpenLink);
        NotesEmpty.IsVisible = release is null;
        ShowVersionButtons();
    }

    // Release notes are Markdown, shown without what is only for the release page: its "## Download" section (how to install by hand)
    // and GitHub's "**Full Changelog**" link.
    static string ReleaseNotesMarkdown(string markdown)
    {
        var lines = new List<string>();
        bool download = false;
        foreach (var line in markdown.ReplaceLineEndings("\n").Split('\n'))
        {
            if (line.StartsWith("## ", StringComparison.Ordinal)) download = line.Trim() == "## Download";
            if (download || line.StartsWith("**Full Changelog**", StringComparison.Ordinal)) continue;
            lines.Add(line);
        }
        return string.Join("\n", lines).Trim();
    }

    // Install, or Cancel while a download runs.
    async void Install_Click(object? sender, RoutedEventArgs e)
    {
        if (download is not null) { download.Cancel(); return; }
        if (ReleaseListBox.SelectedItem is ReleaseRow { Release: var release }) await InstallAsync(release);
    }

    // The footer's Install latest, or Cancel while a download runs.
    async void InstallLatest_Click(object? sender, RoutedEventArgs e)
    {
        if (download is not null) { download.Cancel(); return; }
        if (LatestRelease is { } release) await InstallAsync(release);
    }

    static readonly Avalonia.Media.FontFamily Mono = new("Cascadia Mono, Consolas, DejaVu Sans Mono, Menlo, monospace");

    // The download percentage in a mono font, padded to "100%", so the footer buttons keep their width while it counts.
    void ShowProgress(double p)
    {
        var percent = $"{p,4:0%}";
        InstallLatestText.Inlines = [new Run("Installing "), new Run(percent) { FontFamily = Mono }];
        UpdateHint.Inlines = [new Run("Installing "), new Run(percent) { FontFamily = Mono }, new Run(" · click to cancel")];
        if (PlayButton.Classes.Contains("pending"))
            PlayText.Inlines = [new Run("Installing "), new Run(percent) { FontFamily = Mono }];
    }

    // "Downloading <title>  12.3 / 456.7 MB · 85.2 Mbps" (MB = 10^6 bytes, Mbps = 10^6 bits a second), the figures in mono and
    // padded so the line keeps its width; "Unpacking <title>..." once every byte is in.
    void ShowDownloadStatus(Release release, long done, long total, double bitsPerSecond)
    {
        if (total > 0 && done >= total) { Status($"Unpacking {release.Title}..."); return; }
        var size = total > 0 ? $"{done / 1e6,6:0.0} / {total / 1e6:0.0} MB" : $"{done / 1e6:0.0} MB";
        var speed = bitsPerSecond > 0 ? $" · {bitsPerSecond / 1e6,5:0.0} Mbps" : "";
        StatusText.Inlines = [new Run($"Downloading {release.Title}  "), new Run(size + speed) { FontFamily = Mono }];
    }

    void SetText(TextBlock block, string text) => block.Inlines = [new Run(Tr(text))];

    async Task InstallAsync(Release release)
    {
        using var cts = download = new CancellationTokenSource();
        ShowVersionButtons();
        ShowProgress(0);
        SkipButton.IsVisible = false;
        ToolTip.SetTip(InstallLatestButton, $"Cancel the download of {release.Title}");
        ToolTip.SetTip(UpdateButton, $"Cancel the download of {release.Title}");
        if (PlayButton.Classes.Contains("pending")) ToolTip.SetTip(PlayButton, $"Cancel the download of {release.Title}");
        DownloadProgress.Value = StatusProgress.Value = 0;
        DownloadProgress.IsVisible = StatusProgress.IsVisible = true;
        Status($"Downloading {release.Title}...");
        // The speed is smoothed over half-second samples, so the shown figure doesn't flicker with every 100 ms report.
        var clock = System.Diagnostics.Stopwatch.StartNew();
        double sampleTime = 0, bitsPerSecond = 0;
        long sampleBytes = 0;
        try
        {
            await releases.InstallAsync(release, new Progress<(long Done, long Total)>(p =>
            {
                double now = clock.Elapsed.TotalSeconds;
                if (now - sampleTime >= 0.5)
                {
                    double rate = (p.Done - sampleBytes) * 8 / (now - sampleTime);
                    bitsPerSecond = bitsPerSecond == 0 ? rate : 0.7 * bitsPerSecond + 0.3 * rate;
                    (sampleTime, sampleBytes) = (now, p.Done);
                }
                double fraction = p.Total > 0 ? (double)p.Done / p.Total : 0;
                DownloadProgress.Value = StatusProgress.Value = fraction;
                ShowProgress(fraction);
                ShowDownloadStatus(release, p.Done, p.Total, bitsPerSecond);
            }), cts.Token);
            // Play starts what was just installed: as the latest, or picked when older than an installed one.
            config.SelectedVersion = release.Version == Releases.Installed().FirstOrDefault() ? null : release.Version;
            config.Save();
            RefreshInstalled();
            ShowReleases();
            Status($"{release.Title} installed.");
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            Status($"Download of {release.Title} cancelled.");
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
        {
            Status($"Install failed: {ex.Message}");
        }
        finally
        {
            download = null;
            DownloadProgress.IsVisible = StatusProgress.IsVisible = false;
            ShowVersionButtons();
            ShowFooter();
        }
    }

    void Uninstall_Click(object? sender, RoutedEventArgs e)
    {
        if (download is not null || ReleaseListBox.SelectedItem is not ReleaseRow { Release: var release }) return;
        var dir = Releases.VersionDirectory(release.Version);
        if (!Directory.Exists(dir)) { Status($"{release.Title} is not installed."); return; }
        try
        {
            Directory.Delete(dir, true);
            RefreshInstalled();
            ShowReleases();
            Status($"{release.Title} uninstalled.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status($"Could not remove {dir}: {ex.Message}");
        }
    }

    async void OpenVersionFolder_Click(object? sender, RoutedEventArgs e)
    {
        if (ReleaseListBox.SelectedItem is not ReleaseRow { Release: var release }) return;
        var dir = Releases.VersionDirectory(release.Version);
        if (!Directory.Exists(dir)) { Status($"{release.Title} is not installed."); return; }
        await this.Launcher.LaunchUriAsync(new Uri(dir + Path.DirectorySeparatorChar));
    }

    async void OpenReleasePage_Click(object? sender, RoutedEventArgs e)
    {
        if (ReleaseListBox.SelectedItem is ReleaseRow row)
            await this.Launcher.LaunchUriAsync(new Uri(row.Release.PageUrl));
    }

    // ---- Launcher updates ----

    // Installed by its Setup or AppImage: Velopack finds, downloads and applies the update. Run from anywhere else (a build, a copied
    // folder): the newest launcher-v* release's page.
    async Task ShowLauncherUpdateAsync()
    {
        string? version;
        if (updater.IsInstalled)
        {
            try { version = await updater.CheckAsync(); }
            catch (Exception ex) { LauncherAbout.Text = Loc.Format(Tr("Version {1}. The update check failed: {2}"), LauncherVersion, ex.Message); return; }
            LauncherUpdateButton.Tag = null;
        }
        else
        {
            var page = allReleases.Where(r => r.Kind == ReleaseKind.Launcher && (config.IncludePrereleases || !r.Prerelease))
                .FirstOrDefault(r => System.Version.TryParse(r.Version.Split('-')[0], out var v) && v > System.Version.Parse(LauncherVersion));
            version = page?.Version;
            LauncherUpdateButton.Tag = page?.PageUrl;
        }
        LauncherUpdateButton.IsVisible = version is not null;
        LauncherUpdateButton.Content = Loc.Format(Tr(updater.IsInstalled ? "Update to {1}" : "Get launcher {1}"), version);
        TitleLauncherUpdateButton.IsVisible = version is not null;
        TitleLauncherUpdateText.Text = Loc.Format(Tr(updater.IsInstalled ? "Update launcher to {1}" : "Get launcher {1}"), version);
        ToolTip.SetTip(TitleLauncherUpdateButton, updater.IsInstalled ? "Download the new launcher and restart into it" : "Open the launcher's release page");
        if (version is not null) Status($"Launcher {version} is available.");
    }

    void Steam_Click(object? sender, RoutedEventArgs e)
    {
        config.LaunchAsSteamKenshi = SteamBox.IsChecked == true;
        config.Save();
    }

    void AutoUpdate_Click(object? sender, RoutedEventArgs e)
    {
        config.AutoUpdateLauncher = AutoUpdateBox.IsChecked == true;
        config.Save();
    }

    async void LauncherUpdate_Click(object? sender, RoutedEventArgs e)
    {
        if (LauncherUpdateButton.Tag is string url) { await this.Launcher.LaunchUriAsync(new Uri(url)); return; }
        await UpdateLauncherAsync();
    }

    async Task UpdateLauncherAsync()
    {
        LauncherUpdateButton.IsEnabled = TitleLauncherUpdateButton.IsEnabled = false;
        try
        {
            StatusProgress.Value = 0;
            StatusProgress.IsVisible = true;
            await updater.UpdateAndRestartAsync(p => Dispatcher.UIThread.Post(() =>
            {
                Status($"Updating the launcher: {p}%");
                StatusProgress.Value = p / 100.0;
            }), CancellationToken.None);
        }
        catch (Exception ex)
        {
            StatusProgress.IsVisible = false;
            Status($"Launcher update failed: {ex.Message}");
            LauncherUpdateButton.IsEnabled = TitleLauncherUpdateButton.IsEnabled = true;
        }
    }

    // ---- Window: own title bar and resize grips ----

    void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.ClickCount == 2) ToggleMaximize();
        else BeginMoveDrag(e);
    }

    void Minimize_Click(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    void Maximize_Click(object? sender, RoutedEventArgs e) => ToggleMaximize();

    void Close_Click(object? sender, RoutedEventArgs e) => Close();

    void ToggleMaximize() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    // Windows does not start a resize drag (BeginResizeDrag) on a window without its frame, so there the grips size the window themselves:
    // the pixel rectangle at the press, its dragged edges following the pointer they capture (Avalonia's Width / Height on this frameless
    // window add a title bar's height). Elsewhere the platform does it (Wayland lets no window place itself).
    WindowEdge? resizeEdge;
    PixelPoint resizeStart;
    Win32Window.Rect resizeRect;

    void Grip_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { Tag: string tag } grip || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var edge = Enum.Parse<WindowEdge>(tag);
        if (!OperatingSystem.IsWindows() || TryGetPlatformHandle()?.Handle is not { } hwnd || !Win32Window.GetWindowRect(hwnd, out resizeRect))
        {
            BeginResizeDrag(edge, e);
            return;
        }
        resizeEdge = edge;
        resizeStart = this.PointToScreen(e.GetPosition(this));
        e.Pointer.Capture(grip);
        e.Handled = true;
    }

    void Grip_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (resizeEdge is not { } edge || TryGetPlatformHandle()?.Handle is not { } hwnd) return;
        var now = this.PointToScreen(e.GetPosition(this));
        int dx = now.X - resizeStart.X, dy = now.Y - resizeStart.Y;
        int minWidth = (int)Math.Ceiling(MinWidth * DesktopScaling), minHeight = (int)Math.Ceiling(MinHeight * DesktopScaling);
        var (left, top, right, bottom) = (resizeRect.Left, resizeRect.Top, resizeRect.Right, resizeRect.Bottom);
        if (edge is WindowEdge.West or WindowEdge.NorthWest or WindowEdge.SouthWest) left = Math.Min(left + dx, right - minWidth);
        if (edge is WindowEdge.East or WindowEdge.NorthEast or WindowEdge.SouthEast) right = Math.Max(right + dx, left + minWidth);
        if (edge is WindowEdge.North or WindowEdge.NorthWest or WindowEdge.NorthEast) top = Math.Min(top + dy, bottom - minHeight);
        if (edge is WindowEdge.South or WindowEdge.SouthWest or WindowEdge.SouthEast) bottom = Math.Max(bottom + dy, top + minHeight);
        Win32Window.SetWindowPos(hwnd, IntPtr.Zero, left, top, right - left, bottom - top, Win32Window.NoZOrder | Win32Window.NoActivate);
    }

    void Grip_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (resizeEdge is null) return;
        resizeEdge = null;
        e.Pointer.Capture(null);
    }

    void Grip_PointerCaptureLost(object? sender, PointerCaptureLostEventArgs e) => resizeEdge = null;

    // Maximised, the plate fills the screen: no shadow margin, square corners, no grips.
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != WindowStateProperty || Plate is null) return;
        bool max = WindowState == WindowState.Maximized;
        Plate.Margin = new Thickness(max ? 0 : 8);
        Plate.CornerRadius = new CornerRadius(max ? 0 : 10);
        SetupHost.Margin = max ? new Thickness(0, 46, 0, 0) : new Thickness(8, 54, 8, 8);
        SetupHost.CornerRadius = new CornerRadius(0, 0, max ? 0 : 10, max ? 0 : 10);
        Grips.IsVisible = !max;
        MaximizeIcon.Data = Avalonia.Media.Geometry.Parse(max ? "M2,0 H10 V8 M0,2 H8 V10 H0 Z" : "M0,0 H10 V10 H0 Z");
    }

    // ---- Play ----

    void Play_Click(object? sender, RoutedEventArgs e)
    {
        // As the update button: install the latest, or cancel its download.
        if (PlayButton.Classes.Contains("pending")) { InstallLatest_Click(sender, e); return; }
        if (install is null) { Status("Set the Kenshi folder first (Settings)."); return; }
        string version;
        string? exe = null;
#if DEBUG
        exe = FindDevelopmentGame();
#endif
        if (exe is null)
        {
            if (PlayVersion is not { } selected) { Status("Install a version first (Versions)."); return; }
            version = selected;
            exe = Releases.FindGame(Releases.VersionDirectory(version));
            if (exe is null) { Status($"Meitou {version} is not installed any more."); return; }
        }
        else version = "Debug build";
        // A second game would share meitou.user.json and the mod list with the first; whichever closes last overwrites the other's.
        if (running is { HasExited: false }) { Status("Meitou is already running: close it before starting it again."); return; }
        if (!SaveMods()) return;
        if (graphicsSave?.IsEnabled == true) SaveGame();
        var start = new ProcessStartInfo(exe)
        {
            WorkingDirectory = LauncherConfig.DataDirectory,
            UseShellExecute = false,
#if DEBUG
            RedirectStandardOutput = true,
            RedirectStandardError = true,
#endif
        };
        start.Environment[GameInstall.EnvironmentVariable] = install.Root;
        // The Metal GPU cull is experimental and has caused GPU progress timeouts; it is not a user setting.
        start.Environment.Remove("MEITOU_METAL_GPU_CULL");
        start.Environment.Remove("MEITOU_METAL_GPU_BUDGET_MB");
        string? renderer = null;
        bool metalFallback = false;
        // Steamworks reads this file from the working directory when the game is started outside Steam.
        // Keep it only for the lifetime of this launch; never ship it or overwrite a user's file. A file that already holds Kenshi's
        // ID is one an earlier launch left (the launcher closed before the game did), so this launch takes it over and removes it.
        var steamAppIdFiles = new List<string>();
        void RemoveSteamAppIds() { foreach (string file in steamAppIdFiles) TryDeleteSteamAppId(file); }
        string[] appIdFiles = [.. new[] { Path.Combine(start.WorkingDirectory, "steam_appid.txt"), Path.Combine(Path.GetDirectoryName(exe)!, "steam_appid.txt") }
            .Distinct(StringComparer.OrdinalIgnoreCase)];
        if (!config.LaunchAsSteamKenshi)
        {
            // Off: no Kenshi ID anywhere (not one inherited from Steam, nor a file an earlier launch left), so the game's Steamworks init skips.
            start.Environment.Remove("SteamAppId");
            start.Environment.Remove("SteamGameId");
            foreach (string file in appIdFiles)
            {
                try { if (File.Exists(file) && File.ReadAllText(file).Trim() == KenshiAppId) TryDeleteSteamAppId(file); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        else if (OperatingSystem.IsWindows() && File.Exists(Path.Combine(install.Root, "steam_api64.dll")))
        {
            // Any other ID in either place is the user's: write nothing then, so both places keep agreeing.
            bool appIdConflict = false;
            foreach (string file in appIdFiles)
            {
                try { if (File.Exists(file) && File.ReadAllText(file).Trim() != KenshiAppId) appIdConflict = true; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { appIdConflict = true; }
            }
            if (!appIdConflict)
            {
                foreach (string file in appIdFiles)
                {
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                        File.WriteAllText(file, KenshiAppId);
                        steamAppIdFiles.Add(file);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
                start.Environment["SteamAppId"] = KenshiAppId;
                start.Environment["SteamGameId"] = KenshiAppId;
            }
        }
        if (OperatingSystem.IsMacOS())
        {
            // A release advertises its renderers in settings-schema.json; an old release without the key is Vulkan-only.
            bool metalSupported = PlaySchema().Schema?.Renderers.Contains("metal", StringComparer.OrdinalIgnoreCase) == true;
            string requested = config.Renderer;
            if (requested == "metal" && !MacSupport.MetalAvailable)
            {
                RemoveSteamAppIds();
                Status("Metal needs an Apple-silicon Mac; choose Auto or Vulkan.");
                return;
            }
            if (requested == "metal" && !metalSupported)
            {
                RemoveSteamAppIds();
                Status("This Meitou version does not include Metal; choose Vulkan or install a newer version.");
                return;
            }
            if (requested == "auto" && !metalSupported) requested = "vulkan";
            renderer = MacSupport.Resolve(requested);
            if (renderer == "metal" && !metalSupported)
            {
                RemoveSteamAppIds();
                Status("This Meitou version does not include Metal; choose Vulkan or install a newer version.");
                return;
            }
            metalFallback = requested == "vulkan" && renderer == "metal";
            if (renderer == "metal")
            {
                start.Environment.Remove("DYLD_LIBRARY_PATH");
                start.Environment["MEITOU_METAL_GPU_BUDGET_MB"] = config.MetalMemoryBudget.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            else if (!MacSupport.AddVulkanEnvironment(start.Environment))
            {
                RemoveSteamAppIds();
                Status(MacSupport.MetalAvailable && !metalSupported
                    ? "This Meitou version has no Metal renderer and no Vulkan loader; install a newer version or MoltenVK."
                    : MacSupport.MetalAvailable
                        ? "The Vulkan loader is not installed: use Auto/Metal, or install MoltenVK from Settings > Setup."
                        : "The Vulkan loader is not installed: run 'brew install molten-vk vulkan-loader' in a terminal, then Play again.");
                return;
            }
        }
        foreach (var arg in SplitArguments(config.LaunchArguments)) start.ArgumentList.Add(arg);
        if (renderer is not null)
        {
            // Appended last so the selector wins over a hand-written --renderer in Launch arguments.
            start.ArgumentList.Add("--renderer");
            start.ArgumentList.Add(renderer);
        }
        try
        {
            var game = Process.Start(start);
            running = game;
            string suffix = metalFallback ? " (Vulkan loader missing)" : "";
            Status(renderer is null ? $"Meitou {version} started." : $"Meitou {version} started with {RendererLabel(renderer)}{suffix}.");
            if (game is null) { RemoveSteamAppIds(); return; }
            // The game's window is all a player sees of it, so a crash (above all one before the window opens) is reported here.
            var log = Path.Combine(Path.GetDirectoryName(exe)!, "log.txt");
#if DEBUG
            game.OutputDataReceived += (_, data) => { if (data.Data is { } line) { Console.WriteLine($"[Meitou] {line}"); System.Diagnostics.Debug.WriteLine($"[Meitou] {line}"); } };
            game.ErrorDataReceived += (_, data) => { if (data.Data is { } line) { Console.Error.WriteLine($"[Meitou] {line}"); System.Diagnostics.Debug.WriteLine($"[Meitou] {line}"); } };
            game.BeginOutputReadLine();
            game.BeginErrorReadLine();
#endif
            game.EnableRaisingEvents = true;
            game.Exited += (_, _) =>
            {
                int code = game.ExitCode;
                Dispatcher.UIThread.Post(() =>
                {
                    // On the UI thread, as Play: a game started since then has taken these files over and removes them itself.
                    if (running == game) RemoveSteamAppIds();
                    if (code != 0) Status($"Meitou {version} stopped with an error (exit code {code}); its log is {log}");
                });
            };
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            RemoveSteamAppIds();
            Status($"Could not start Meitou {version}: {ex.Message}");
        }
    }

#if DEBUG
    static string? FindDevelopmentGame()
    {
        foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(Path.GetFullPath(start));
            for (; directory is not null; directory = directory.Parent)
            {
                string candidate = Path.Combine(directory.FullName, "src", "Meitou.Game", "bin", "Debug", "net10.0", "MeitouClient.exe");
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }
#endif

    const string KenshiAppId = "233860";

    static void TryDeleteSteamAppId(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // Space-separated, with double quotes around arguments that contain spaces.
    static IEnumerable<string> SplitArguments(string text)
    {
        var current = new System.Text.StringBuilder();
        bool quoted = false, any = false;
        foreach (char c in text)
        {
            if (c == '"') { quoted = !quoted; any = true; }
            else if (char.IsWhiteSpace(c) && !quoted) { if (any) yield return current.ToString(); current.Clear(); any = false; }
            else { current.Append(c); any = true; }
        }
        if (any) yield return current.ToString();
    }
}

/// <summary>The Win32 calls the resize grips use on Windows (MainWindow.Grip_PointerPressed).</summary>
static partial class Win32Window
{
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    internal struct Rect { public int Left, Top, Right, Bottom; }

    internal const uint NoZOrder = 0x0004, NoActivate = 0x0010;

    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    internal static partial bool GetWindowRect(IntPtr window, out Rect rect);

    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    internal static partial bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
}
