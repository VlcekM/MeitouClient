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
    readonly LauncherConfig _config;
    static readonly HttpClient Http = Releases.CreateClient();
    readonly Releases _releases = new(Http);
    readonly NewsFeed _news = new(Http);
    readonly LauncherUpdater _updater;
    readonly ObservableCollection<ModRow> _mods = [];
    List<Release> _allReleases = [];
    List<string> _installedVersions = [];
    readonly MenuFlyout _versionMenu = new() { Placement = PlacementMode.TopEdgeAlignedRight };
    GameInstall? _install;
    CancellationTokenSource? _download;
    ModRow? _dragged;
    readonly Dictionary<Control, string> _originalUiText = [];
    bool _languageReady, _populatingLanguages;
    int _modsLoad;
    bool _modsReady;
    bool _toggleSavePending;
    // While ApplyOrder rebuilds the list: the mod count and the dependency check wait for the end.
    bool _checksSuspended;
    string? _launcherUpdateVersion;   // the launcher release the update button offers, for a language switch
    static string Tr(string text, params object?[] args) => LauncherText.Translate(text, args);

    const uint WsCaption = 0x00C00000, WsSysmenu = 0x00080000, WsMinimizebox = 0x00020000, WsMaximizebox = 0x00010000;

    static string LauncherVersion => typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public MainWindow() : this(LauncherConfig.Load(), GameInstall.Locate()) { }

    public MainWindow(LauncherConfig config, GameInstall? found)
    {
        this._config = config;
        _updater = new LauncherUpdater(_releases, config.IncludePrereleases);
        InitializeComponent();
        LauncherText.Use(StartLanguage(found), found);
        PopulateLanguageOptions(found);
        LauncherText.TranslateTree(this, _originalUiText);
        _languageReady = true;
        // Windows animates minimise, maximise and restore only for windows with a caption style. Avalonia sizes the client area of an
        // undecorated window to the whole window whatever its styles, so the caption bits add the animations and no frame.
        if (OperatingSystem.IsWindows())
            Win32Properties.AddWindowStylesCallback(this, (style, exStyle) => (style | WsCaption | WsSysmenu | WsMinimizebox | WsMaximizebox, exStyle));
        PlayVersionButton.Flyout = _versionMenu;
        TrackPointer();
        ModListBox.ItemsSource = _mods;
        _mods.CollectionChanged += (_, _) => { if (_modsReady) ModListChanged(); };   // a drag or Alt+arrow move changes what loads first
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
        MetalBudgetRow.IsVisible = OperatingSystem.IsMacOS() && MacSupport.MetalAvailable;
        MetalBudgetHighItem.IsVisible = MacSupport.CanUseHighBudget;
        if (!MacSupport.CanUseHighBudget && config.MetalMemoryBudget == 8120) config.MetalMemoryBudget = 5120;
        MetalBudgetBox.SelectedIndex = config.MetalMemoryBudget switch { 8120 => 2, 6144 => 1, _ => 0 };
        MetalBudgetBox.SelectionChanged += MetalBudget_Changed;
        NightlyBox.IsChecked = config.IncludePrereleases;
        AutoUpdateBox.IsChecked = config.AutoUpdateLauncher;
        AutoUpdateBox.IsEnabled = _updater.IsInstalled;
        ShowReportButton();
        ShowBuiltTexts();
        InstallPathBox.Text = LauncherConfig.InstallDirectory;
        LauncherVersionText.Text = $"v{LauncherVersion} · {Releases.Rid}";
        RefreshPresets();
        RefreshInstalled();
        UseKenshi(found);
        if (!config.FoldersConfirmed) ShowSetup();
        Closing += (_, _) => { if (_graphicsSave?.IsEnabled == true) SaveGame(); };
        // A save made in the game since: Continue names it.
        Activated += (_, _) => _ = RefreshContinueAsync();
        Opened += async (_, _) =>
        {
            // Apply after Avalonia has attached control templates; the constructor pass runs before some text presenters exist.
            LauncherText.TranslateTree(this, _originalUiText);
            await Task.WhenAll(LoadNewsAsync(), RefreshReleasesAsync(updateLauncher: true));
        };
    }

    void Status(string text) => SetText(StatusText, text);

    /// <summary>
    /// The labels whose text holds a value (the data folder, the version, the launcher update) and so cannot be looked up whole:
    /// they are built again for a language switch, which only walks the layout's own texts otherwise.
    /// </summary>
    void ShowBuiltTexts()
    {
        DataFolderText.Text = Tr("The launcher's and the game's settings: {1}", LauncherConfig.DataDirectory);
        LauncherAbout.Text = Tr(_updater.IsInstalled
            ? "Version {1} for {2}. Updates itself from GitHub."
            : "Version {1} for {2}. Not installed by its Setup, so updates open the release page.", LauncherVersion, Releases.Rid);
        if (_launcherUpdateVersion is { } version)
        {
            LauncherUpdateButton.Content = Tr(_updater.IsInstalled ? "Update to {1}" : "Get launcher {1}", version);
            TitleLauncherUpdateText.Text = Tr(_updater.IsInstalled ? "Update launcher to {1}" : "Get launcher {1}", version);
        }
        UpdateRendererHint();
        UpdateMetalBudgetHint();
    }

    // ---- Language: one choice for the game (Kenshi's settings.cfg) and the launcher ----

    /// <summary>The language to start in: the Kenshi folder's settings.cfg choice, else the launcher's last one.</summary>
    string StartLanguage(GameInstall? found) =>
        (found is null ? null : KenshiLanguageSettings.GetLanguage(found)) is { Length: > 0 } game ? game : _config.Language;

    void Language_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_languageReady || _populatingLanguages) return;
        if (LanguageBox.SelectedItem is not ComboBoxItem item || item.Tag is not string code || code == LauncherText.Code) return;
        _config.Language = code;
        _config.Save();
        LauncherText.Use(code, _install);
        if (_install is not null)
        {
            try
            {
                KenshiLanguageSettings.SetLanguage(_install, code);
                Status(Tr("Game language set to {1}. It applies when the game starts.", code));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Status(Tr("Could not update Kenshi's language setting: {1}", error.Message));
            }
        }
        LauncherText.TranslateTree(this, _originalUiText);
        ShowBuiltTexts();
        foreach (var row in _mods) row.LanguageChanged();
        if (_modsReady) ShowDependencyProblems();
        ShowReleases();
        PopulateLanguageOptions(_install);
    }

    // Opening the list reads the catalogs again, so a meitou.po added or edited meanwhile shows up.
    void Language_DropDownOpened(object? sender, EventArgs e)
    {
        LauncherText.Use(LauncherText.Code, _install);
        PopulateLanguageOptions(_install);
        LauncherText.TranslateTree(this, _originalUiText);
        ShowBuiltTexts();
    }

    /// <summary>English, Kenshi's own languages, the locale folders of the install and the languages Meitou has texts for; the current one selected.</summary>
    void PopulateLanguageOptions(GameInstall? gameInstall)
    {
        var codes = new List<string> { GameLanguage.DefaultCode };
        foreach (string code in GameLanguage.SteamLanguages.Select(l => l.Code).Concat(MeitouTranslations.EmbeddedCodes).Concat(InstalledLocaleCodes(gameInstall)))
            if (!codes.Any(known => SameLanguage(known, code))) codes.Add(code);
        _populatingLanguages = true;
        LanguageBox.Items.Clear();
        foreach (string code in codes)
        {
            bool translated = code == GameLanguage.DefaultCode || MeitouTranslations.Has(code, LauncherText.LocaleFolder(gameInstall, code));
            string name = translated ? GameLanguageName(code) : $"{GameLanguageName(code)} ({Tr("Meitou's texts not yet translated")})";
            LanguageBox.Items.Add(new ComboBoxItem { Content = name, Tag = code });
        }
        LanguageBox.SelectedItem = LanguageBox.Items.OfType<ComboBoxItem>().FirstOrDefault(item => item.Tag is string code && SameLanguage(code, LauncherText.Code))
            ?? LanguageBox.Items.OfType<ComboBoxItem>().First();
        _populatingLanguages = false;
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
                LauncherText.TranslateTree(selectedPage, _originalUiText);
            };
            selectedPage.LayoutUpdated += applyAfterLayout;
        }
    }

    // ---- Home: news ----

    async Task LoadNewsAsync()
    {
        NewsCount.Text = Tr("Loading");
        var (posts, problem) = await _news.FetchAsync();
        NewsPanel.Children.Clear();
        foreach (var post in posts) NewsPanel.Children.Add(PostCard(post));
        if (posts.Count == 0) NewsPanel.Children.Add(new TextBlock { Classes = { "desc" }, Text = problem ?? "No news yet." });
        NewsCount.Text = NewsFeed.LocalFolder is not null ? $"Debug: local news/, {posts.Count} posts"
            : posts.Count == 0 ? "" : problem is not null ? Tr("Offline: saved posts") : posts.Count == 1 ? Tr("1 post") : Tr("{1} posts", posts.Count);
    }

    Control PostCard(NewsPost post)
    {
        var card = new StackPanel { Spacing = 8 };
        if (post.Image is { } cover) card.Children.Add(MarkdownView.Image(NewsFeed.Resolve(post, cover), _news, post.Video is { } video ? (NewsFeed.Resolve(post, video), OpenLink) : null));
        // The title, with "19 October 2026 · author" (either part when only it is given) on its right.
        var heading = new DockPanel();
        var byline = string.Join(" · ", new[] { post.Date?.ToString("d MMMM yyyy", System.Globalization.CultureInfo.InvariantCulture), post.Author }.Where(s => !string.IsNullOrEmpty(s)));
        if (byline.Length > 0)
        {
            var aside = new TextBlock { Classes = { "desc" }, Text = byline, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };
            DockPanel.SetDock(aside, Dock.Right);
            heading.Children.Add(aside);
        }
        heading.Children.Add(new TextBlock { Text = post.Title, FontSize = 22, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        card.Children.Add(heading);
        card.Children.Add(MarkdownView.Render(post, _news, OpenLink));
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
        _install = found;
        KenshiPathBox.Text = found?.Root ?? _config.KenshiPath ?? "";
        _ = RefreshContinueAsync();
        KenshiStatus.Text = found is null ? "Not found: browse to the folder with data/gamedata.base."
            : found.WorkshopDirectory is null ? "The folder with data/gamedata.base." : "The folder with data/gamedata.base; Steam workshop mods found.";
        KenshiBadge.Text = found is null ? "Kenshi not found: set its folder in Settings" : found.Root;
        if (found is not null && _config.KenshiPath != found.Root)
        {
            _config.KenshiPath = found.Root;
            _config.Save();
        }
        if (_languageReady)
        {
            LauncherText.Use(StartLanguage(found), found);
            PopulateLanguageOptions(found);
            LauncherText.TranslateTree(this, _originalUiText);
            ShowBuiltTexts();
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
        if (KenshiPathBox.Text is { } path && path != _install?.Root)
            SetKenshiPath(path.Trim().Trim('"'));
    }

    void SetKenshiPath(string path)
    {
        if (GameInstall.IsValid(path)) UseKenshi(GameInstall.Open(path));
        else KenshiStatus.Text = Tr("'{1}' is not a Kenshi folder (data/gamedata.base not found).", path);
    }

    void Arguments_LostFocus(object? sender, RoutedEventArgs e)
    {
        _config.LaunchArguments = ArgumentsBox.Text ?? "";
        _config.Save();
    }

    void Renderer_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (RendererBox.SelectedItem is ComboBoxItem { Tag: string renderer } && renderer is "auto" or "metal" or "vulkan")
        {
            _config.Renderer = renderer;
            _config.Save();
            UpdateRendererHint();
            Status(Tr("Renderer: {1}; Play starts the game with it.", RendererLabel(MacSupport.Resolve(renderer))));
        }
    }

    void UpdateRendererHint()
    {
        bool vulkan = MacSupport.VulkanAvailable;
        string effective = RendererLabel(MacSupport.Resolve(_config.Renderer));
        string hint = _config.Renderer switch
        {
            "auto" => vulkan ? "Auto: {1} (Vulkan loader found)." : MacSupport.MetalAvailable ? "Auto: {1} (no Vulkan loader)." : "Auto: {1}.",
            "metal" => MacSupport.MetalAvailable ? "Metal (native)." : "Metal is not available on this Mac; Auto uses Vulkan.",
            _ => vulkan ? "Vulkan (MoltenVK loader found)." : "Vulkan loader not found. Use Auto/Metal or install it with Setup.",
        };
        SetText(RendererHint, Tr(hint, effective));
    }

    void MetalBudget_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (MetalBudgetBox.SelectedItem is ComboBoxItem { Tag: string tag } && int.TryParse(tag, out int mb))
        {
            _config.MetalMemoryBudget = mb;
            UpdateMetalBudgetHint();
            Status(Tr("Metal memory budget: {1} MB.", _config.MetalMemoryBudget));
        }
    }

    void UpdateMetalBudgetHint()
    {
        SetText(MetalBudgetHint, _config.MetalMemoryBudget switch
        {
            8120 => "High: keeps the most textures, terrain and objects resident. Best on a 16 GB-or-larger Mac with other memory-heavy apps closed.",
            6144 => "Balanced: more resident detail than Safe, with some headroom for macOS and other apps.",
            _ => "Safe: the conservative default; the guard shortens ranges and evicts caches sooner.",
        });
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
        if (GetTopLevel(this)?.Clipboard is { } clipboard)
            await clipboard.SetValueAsync(DataFormat.Text, MacSupport.HomebrewVulkanCommand);
        await this.Launcher.LaunchUriAsync(new Uri("https://brew.sh"));
        Status(Tr("Copied '{1}' and opened brew.sh; install Homebrew, run it in Terminal, then press Check.", MacSupport.HomebrewVulkanCommand));
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
        if (NightlyBox.IsChecked == true && !_config.IncludePrereleases)
        {
            bool sure = await AskAsync("Nightly builds",
                "Nightly builds are made twice a day from the newest code and are not tested. They may be unstable, crash, and corrupt your save files: "
                + "back up your saves before playing one.\n\nContinue with nightly builds?", "Yes", "No");
            if (!sure) { NightlyBox.IsChecked = false; return; }
        }
        _config.IncludePrereleases = NightlyBox.IsChecked == true;
        _config.Save();
        ShowReleases();
    }

    string Latest => _config.IncludePrereleases ? "latest nightly" : "latest";

    async void OpenDataFolder_Click(object? sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(LauncherConfig.DataDirectory);
        await this.Launcher.LaunchUriAsync(new Uri(LauncherConfig.DataDirectory + Path.DirectorySeparatorChar));
    }

    async void BrowseInstall_Click(object? sender, RoutedEventArgs e)
    {
        if (_download is not null) { InstallStatus.Text = Tr("Wait for the download to finish first."); return; }
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Install folder" });
        if (folders is not [var folder] || folder.TryGetLocalPath() is not { } path || path == LauncherConfig.InstallDirectory) return;
        var previous = LauncherConfig.InstallDirectory;
        InstallStatus.Text = Tr("Moving installed versions…");
        try { await Task.Run(() => Releases.MoveInstalled(previous, path)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            InstallStatus.Text = Tr("Could not move the versions to {1}: {2}", path, ex.Message);
            RefreshInstalled();
            return;
        }
        _config.InstallFolder = Path.GetFullPath(path) == Path.GetFullPath(LauncherConfig.DataDirectory) ? null : path;
        _config.Save();
        InstallPathBox.Text = LauncherConfig.InstallDirectory;
        InstallStatus.Text = Tr("Where versions are installed; changing it moves them.");
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
        var setup = new SetupOverlay(_config, _install);
        setup.Confirmed += chosen =>
        {
            SetupHost.IsVisible = false;
            SetupHost.Child = null;
            InstallPathBox.Text = LauncherConfig.InstallDirectory;
            SteamBox.IsChecked = _config.LaunchAsSteamKenshi;
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
        int load = ++_modsLoad;
        _modsReady = false;
        _mods.Clear();
        ModsEmpty.IsVisible = false;
        var gameInstall = _install;
        List<(ModEntry Entry, CodeModEntry Code)> entries;
        try
        {
            entries = gameInstall is null ? [] : await Task.Run(() =>
            {
                var order = ModList.LauncherOrder(gameInstall, ModList.Discover(gameInstall));
                // The rows show each mod's author, plugin note and code-mod dependencies: read them here too, not on the UI thread.
                foreach (var entry in order) { _ = entry.Mod.Header; _ = entry.Mod.HasNativePlugin; }
                return order.Select(e => (e, CodeModDependencies.ReadEntry(e.Mod, e.Enabled))).ToList();
            });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            if (load != _modsLoad) return;
            ModsEmpty.IsVisible = true;
            ModsEmpty.Text = Tr("Could not read the mods: {1}", e.Message);
            return;
        }
        if (load != _modsLoad) return;
        foreach (var (entry, code) in entries) _mods.Add(new ModRow(entry.Mod, entry.Enabled, code));
        // Only a toggle changes the checks; the checks' own Notes changes must not start them again.
        foreach (var row in _mods) row.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ModRow.Enabled)) ModListChanged(); };
        _modsReady = true;
        ShowDependencyProblems();
        ModsEmpty.IsVisible = _mods.Count == 0;
        ModsEmpty.Text = Tr(gameInstall is null ? "Set the Kenshi folder in Settings to list its mods."
            : "No mods found in Kenshi's mods folder or among its Steam workshop items.");
    }

    // The code-mod rules the game applies at start that need no code (docs/modding-dependencies.md "Launcher"), shown on each row as the list
    // changes. A preliminary check: DLLs and initialization are tried only by the game. Mods set to data only cannot serve as dependencies.
    void ShowDependencyProblems()
    {
        if (_checksSuspended) return;
        var problems = CodeModChecks.Check(DependencyEntries());
        foreach (var row in _mods) row.DependencyProblems = problems.TryGetValue(row.Name, out var lines) ? lines : [];
    }

    List<CodeModEntry> DependencyEntries() =>
        [.. _mods.Select(m => m.Code with { Enabled = m.Enabled, HeaderDependencies = m.Mod.Header?.Dependencies })];

    // One graph of the enabled mods' code and .mod header dependencies; the proposed order is shown and applied only when accepted, and is
    // saved once accepted. A contradiction is shown and changes nothing.
    async void ArrangeMods_Click(object? sender, RoutedEventArgs e)
    {
        if (!ModsReady()) return;
        var result = CodeModDependencies.Arrange(DependencyEntries());
        if (result.HasCycle)
        {
            ShowArrangeCycle(result.Cycle);
            return;
        }
        var moves = ArrangeMoves(result.Order);
        if (moves.Count == 0)
        {
            Status(Tr("The mod order already meets every dependency."));
            return;
        }
        if (!await AskAsync(Tr("Arrange mods"), Tr("Proposed moves:") + "\n" + string.Join("\n", moves), Tr("Apply"), Tr("Cancel"))) return;
        var enabled = _mods.ToDictionary(m => m.Name, m => m.Enabled, StringComparer.Ordinal);
        ApplyOrder(result.Order.Select(name => (name, enabled[name])));
        int left = _mods.Count(m => m.DependencyProblems.Count > 0);
        Status(left == 0 ? Tr("Mods arranged by their dependencies.")
            : Tr("Mods arranged by their dependencies; {1} still need attention.", left));
    }

    void ShowArrangeCycle(IReadOnlyList<OrderConstraint> cycle)
    {
        string lines = string.Join("\n", cycle.Select(c => Tr("{1} after {2} ({3})", c.After, c.Before, c.Why)));
        string text = Tr("These dependencies contradict each other, so nothing was moved:") + "\n" + lines;
        _ = ChooseAsync(Tr("Arrange mods"), [Wrapped(text)], [(Tr("OK"), true)], cancel: false);
    }

    // One line per mod whose position changes, in the new order: "3 → 1  Framework"; long lists are cut.
    List<string> ArrangeMoves(IReadOnlyList<string> order)
    {
        const int shown = 12;
        var before = _mods.Select((m, i) => (m.Name, i)).ToDictionary(p => p.Name, p => p.i, StringComparer.Ordinal);
        var moves = order.Select((name, i) => (name, from: before[name], to: i)).Where(m => m.from != m.to).ToList();
        var lines = moves.Take(shown).Select(m => $"{m.from + 1} → {m.to + 1}  {m.name}").ToList();
        if (moves.Count > shown) lines.Add(Tr("and {1} more", moves.Count - shown));
        return lines;
    }

    // False while the list is loading or after it could not be read; Status says why.
    bool ModsReady()
    {
        if (!_modsReady) Status(ModsEmpty.IsVisible ? ModsEmpty.Text ?? "" : "The mod list is still loading; try again in a moment.");
        return _modsReady;
    }

    bool SaveMods()
    {
        if (_install is null) { Status("Set the Kenshi folder first (Settings)."); return false; }
        if (!ModsReady()) return false;
        try
        {
            ModList.Save(_install, _mods.Select(m => new ModEntry(m.Mod, m.Enabled)));
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Status(Tr("Could not save the mod list: {1}", e.Message));
            return false;
        }
    }

    // A tick, drag or move saves mods.cfg at once, so the game reads what the list shows (docs/launcher.md "Mod list"). Select all / none
    // change many rows: one save after them. ApplyOrder (presets, Arrange, From save) holds this and saves once itself.
    void ModListChanged()
    {
        ShowDependencyProblems();
        if (_checksSuspended || !_modsReady || _toggleSavePending) return;

        _toggleSavePending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _toggleSavePending = false;
            if (_modsReady && SaveMods()) Status(Tr("Mod list saved: {1} enabled.", _mods.Count(m => m.Enabled)));
        });
    }

    void RefreshMods_Click(object? sender, RoutedEventArgs e) => LoadMods();

    void SelectAll_Click(object? sender, RoutedEventArgs e) { foreach (var m in _mods) m.Enabled = true; }

    void SelectNone_Click(object? sender, RoutedEventArgs e) { foreach (var m in _mods) m.Enabled = false; }

    // Alt+Up / Alt+Down move the selected mod, as dragging does.
    void ModListBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.Alt || e.Key is not (Key.Up or Key.Down)) return;
        int i = ModListBox.SelectedIndex, j = i + (e.Key == Key.Up ? -1 : 1);
        if (i >= 0 && j >= 0 && j < _mods.Count)
        {
            _mods.Move(i, j);
            ModListBox.SelectedIndex = j;
        }
        e.Handled = true;
    }

    // Dragging a row's handle moves the row live under the pointer; the list holds the pointer capture, so rows can be
    // re-created underneath without ending the drag.
    void DragHandle_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: ModRow row } || !e.GetCurrentPoint(ModListBox).Properties.IsLeftButtonPressed) return;
        _dragged = row;
        ModListBox.SelectedItem = row;
        e.Pointer.Capture(ModListBox);
        MarkDragged(true);
        e.Handled = true;
    }

    void ModList_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragged is null) return;
        double y = e.GetPosition(ModListBox).Y;
        int from = _mods.IndexOf(_dragged), to = from;
        for (int i = 0; i < _mods.Count; i++)
        {
            if (ModListBox.ContainerFromIndex(i) is not Control item || item.TranslatePoint(new Point(0, 0), ModListBox) is not { } top) continue;
            if (y >= top.Y && y < top.Y + item.Bounds.Height) { to = i; break; }
        }
        if (to == from) return;
        MarkDragged(false);
        _mods.Move(from, to);
        ModListBox.SelectedItem = _dragged;
        ModListBox.UpdateLayout(); // the next hit test needs the rows where they are now
        MarkDragged(true);
    }

    void ModList_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragged is null) return;
        e.Pointer.Capture(null);
        EndDrag();
    }

    void EndDrag()
    {
        if (_dragged is null) return;
        MarkDragged(false);
        _dragged = null;
    }

    void MarkDragged(bool on)
    {
        if (_dragged is not null && ModListBox.ContainerFromItem(_dragged) is { } item)
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
            parts.Add(Tr("Needs {1}", string.Join(", ", header.Dependencies)) + (missing.Count > 0 ? Tr(" (not enabled above it: {1})", string.Join(", ", missing)) : ""));
        }
        parts.Add(row.Mod.Folder);
        ModDetails.Text = string.Join(" · ", parts);
    }

    // A dependency is met by a core file or by an enabled mod earlier in the list.
    bool IsAvailableBefore(string dependency, ModRow row)
    {
        if (dependency is "gamedata.base" or "Newwworld.mod" or "Dialogue.mod" or "rebirth.mod") return true;
        foreach (var m in _mods)
        {
            if (m == row) return false;
            if (m.Enabled && m.Name + ".mod" == dependency) return true;
        }
        return false;
    }

    // ---- Presets ----

    void RefreshPresets(string? select = null)
    {
        PresetBox.ItemsSource = _config.Presets.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        PresetBox.SelectedItem = _config.Presets.FirstOrDefault(p => p.Name == select);
    }

    void SavePreset_Click(object? sender, RoutedEventArgs e)
    {
        var name = PresetName.Text?.Trim();
        if (string.IsNullOrEmpty(name)) { Status("Type a name for the preset."); return; }
        if (!ModsReady()) return;
        _config.Presets.RemoveAll(p => p.Name == name);
        _config.Presets.Add(new ModPreset { Name = name, Mods = [.. _mods.Select(m => new PresetMod { Name = m.Name, Enabled = m.Enabled })] });
        _config.Save();
        PresetName.Text = "";
        RefreshPresets(name);
        Status(Tr("Preset '{1}' saved.", name));
    }

    void ApplyPreset_Click(object? sender, RoutedEventArgs e)
    {
        if (PresetBox.SelectedItem is not ModPreset preset || !ModsReady()) return;
        var missing = ApplyOrder(preset.Mods.Select(p => (p.Name, p.Enabled)));
        Status(Tr("Preset '{1}' applied.", preset.Name) + NotOnDisk(missing));
    }

    /// <summary>
    /// Puts the named mods first, in this order and with these flags, and the others after them, disabled; answers the enabled names not on disk.
    /// </summary>
    /// <remarks>
    /// The dependency check is held while rows move and runs once at the end, synchronously (Arrange reads the result).
    /// </remarks>
    List<string> ApplyOrder(IEnumerable<(string Name, bool Enabled)> order)
    {
        var byName = _mods.ToDictionary(m => m.Name, StringComparer.Ordinal);
        var ordered = new List<ModRow>();
        var missing = new List<string>();
        _checksSuspended = true;
        try
        {
            foreach (var (name, enabled) in order)
            {
                if (byName.Remove(name, out var row)) { row.Enabled = enabled; ordered.Add(row); }
                else if (enabled) missing.Add(name);
            }
            foreach (var row in _mods.Where(m => byName.ContainsKey(m.Name))) { row.Enabled = false; ordered.Add(row); }
            _mods.Clear();
            foreach (var row in ordered) _mods.Add(row);
        }
        finally
        {
            _checksSuspended = false;
        }
        ShowDependencyProblems();
        SaveMods();
        return missing;
    }

    static string NotOnDisk(List<string> missing) => missing.Count > 0 ? Tr(" Not on disk: {1}.", string.Join(", ", missing)) : "";

    void DeletePreset_Click(object? sender, RoutedEventArgs e)
    {
        if (PresetBox.SelectedItem is not ModPreset preset) return;
        _config.Presets.Remove(preset);
        _config.Save();
        RefreshPresets();
        Status(Tr("Preset '{1}' deleted.", preset.Name));
    }

    // ---- Versions ----

    void RefreshInstalled()
    {
        _installedVersions = Releases.Installed();
        ShowPlay();
        ShowFooter();
        _ = AutoDetectPresetAsync();
    }

    // The version picked to play (config.SelectedVersion) while it is installed; null plays the latest.
    string? PickedVersion => _installedVersions.Contains(_config.SelectedVersion ?? "") ? _config.SelectedVersion : null;

    // The version Play starts: the picked one, else the newest installed.
    string? PlayVersion => PickedVersion ?? _installedVersions.FirstOrDefault();

    // The latest release when it is newer than every installed version and not skipped.
    Release? NewRelease => _installedVersions.Count > 0 && LatestRelease is { } latest
        && Releases.CompareVersions(latest.Version, _installedVersions[0]) > 0
        && latest.Version != _config.SkippedVersion ? latest : null;

    // Playing the latest, with a new release: Play is the button that installs it.
    bool UpdatePending => PickedVersion is null && NewRelease is not null;

    // "Play latest" or "Play v0.13.0" (or "Update to v0.14.1" while UpdatePending), and the arrow's menu: Latest, then each
    // installed version. During a download Play keeps its look and shows the progress.
    void ShowPlay()
    {
        var picked = PickedVersion;
        if (_download is null) ShowPlayText();
        _versionMenu.Items.Clear();
        _versionMenu.Items.Add(VersionItem(UpdatePending ? Tr("Latest (update to {1})", Releases.Display(LatestRelease!.Version))
            : _installedVersions.FirstOrDefault() is { } newest ? Tr("Latest ({1})", Releases.Display(newest)) : Tr("Latest"), null, picked));
        foreach (var version in _installedVersions) _versionMenu.Items.Add(VersionItem($"v{Releases.ParseVersion(version).Number}", version, picked));
        // Its settings are the played version's.
        if (GraphicsPage.IsVisible) ShowGraphics();
        // A skipped release stays installable from here.
        if (LatestRelease is { } latest && latest.Version == _config.SkippedVersion && !_installedVersions.Contains(latest.Version))
        {
            var item = new MenuItem { Header = Tr("Install v{1} (skipped)", latest.Version) };
            item.Click += async (_, _) =>
            {
                _config.SkippedVersion = null;
                _config.Save();
                if (_download is null) await InstallAsync(latest);
            };
            _versionMenu.Items.Add(new Separator());
            _versionMenu.Items.Add(item);
        }
    }

    static readonly Avalonia.Media.Geometry StartIcon = Avalonia.Media.Geometry.Parse("M0,0 L0,18 L15,9 Z"),
        MenuIcon = Avalonia.Media.Geometry.Parse("M0,0 H4 V18 H0 Z M7,0 L7,18 L20,9 Z");

    // Beside Continue, Play is "Main menu" over the version it starts.
    void ShowPlayText()
    {
        var picked = PickedVersion;
        bool pending = UpdatePending, menu = ContinueButton.IsVisible && !pending;
        string played = picked is null ? Tr(Latest) : Releases.Display(picked);
        PlayButton.Classes.Set("pending", pending);
        PlayVersionButton.Classes.Set("pending", pending);
        PlayIcon.IsVisible = !pending;
        PlayIcon.Data = ContinueButton.IsVisible ? MenuIcon : StartIcon;
        UpdateIcon.IsVisible = pending;
        PlayText.FontSize = ContinueButton.IsVisible ? 17 : 21;
        // Two lines in the button's one-line height.
        PlayText.LineHeight = menu ? 20 : double.NaN;
        PlaySubText.IsVisible = menu;
        PlaySubText.Text = played;
        SetText(PlayText, pending ? Tr("Update to {1}", Releases.Display(LatestRelease!.Version)) : menu ? Tr("Main menu") : Tr("Play {1}", played));
        ToolTip.SetTip(PlayButton, pending ? Tr("Download and install {1}; the arrow plays an installed version", LatestRelease!.Title)
            : PlayVersion is not { } version ? null
            : menu ? Tr("Start Meitou {1} at the main menu", version)
            : Tr("Start Meitou {1}", version));
    }

    // Skip: the new release is no longer offered, until a newer one comes out.
    void SkipVersion_Click(object? sender, RoutedEventArgs e)
    {
        if (_download is not null || NewRelease is not { } release) return;
        _config.SkippedVersion = release.Version;
        _config.Save();
        ShowFooter();
        Status(Tr("Skipped {1}: the arrow's menu can still install it.", release.Title));
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
        _config.SelectedVersion = version;
        _config.Save();
        ShowPlay();
        ShowReleases();
        Status(version is null ? Tr("Play starts the latest installed version.") : Tr("Play starts Meitou {1}.", version));
    }

    void SelectVersion_Click(object? sender, RoutedEventArgs e)
    {
        if (ReleaseListBox.SelectedItem is ReleaseRow { Installed: true, Release: var release })
            PickVersion(release.Version == _installedVersions.FirstOrDefault() ? null : release.Version);
    }

    // The newest game release with a build for this platform, honouring the pre-release setting.
    Release? LatestRelease => _allReleases.FirstOrDefault(r => r.Kind == ReleaseKind.Game && (_config.IncludePrereleases || !r.Prerelease) && r.Asset is not null);

    // With no version installed the footer offers Install latest instead of the version box and Play; with versions installed
    // but not the latest, an update button beside the version box installs it and selects it.
    void ShowFooter()
    {
        if (_download is not null) return;
        var installed = _installedVersions;
        var latest = LatestRelease;
        bool none = installed.Count == 0;
        PlayPanel.IsVisible = !none;
        InstallLatestButton.IsVisible = none;
        SetText(InstallLatestText, Tr("Install {1}", Tr(Latest)));
        InstallLatestButton.IsEnabled = latest is not null;
        ToolTip.SetTip(InstallLatestButton, latest is not null ? Tr("Download and install {1}", latest.Title) : Tr("No release with a {1} build found yet", Releases.Rid));
        // Playing the latest, Play itself is the update (ShowPlay); playing a picked version, this button beside it.
        UpdateButton.IsVisible = PickedVersion is not null && NewRelease is not null;
        SkipButton.IsVisible = NewRelease is not null;
        SkipText.Text = Tr("Skip v{1}", NewRelease?.Version);
        UpdateTitle.Text = Tr("New version {1}", latest?.Version);
        SetText(UpdateHint, "Click to install");
        ToolTip.SetTip(UpdateButton, Tr("Download and install {1}", latest?.Title));
        ShowPlay();
    }

    // Install shows Installed for an installed release and Cancel during a download; Uninstall and Open folder need it installed.
    void ShowVersionButtons()
    {
        var row = ReleaseListBox.SelectedItem as ReleaseRow;
        bool installed = row?.Installed == true;
        InstallButton.Content = Tr(_download is not null ? "Cancel" : installed ? "Installed" : "Install");
        InstallButton.IsEnabled = _download is not null || (row?.Release.Asset is not null && !installed);
        UninstallButton.IsEnabled = installed && _download is null;
        OpenFolderButton.IsEnabled = installed;
        SelectVersionButton.Content = Tr(row?.Played == true ? "Selected" : "Select");
        SelectVersionButton.IsEnabled = installed && row?.Played != true;
    }

    // At startup (updateLauncher) an installed launcher also applies its own update and restarts into it, unless turned off in Settings.
    async Task RefreshReleasesAsync(bool updateLauncher = false)
    {
        Status("Checking GitHub for releases...");
        var (list, problem) = await _releases.FetchAsync();
        _allReleases = list;
        ShowReleases();
        Status(problem ?? "");
        await ShowLauncherUpdateAsync();   // after: its "Launcher … is available" line must not be cleared
        if (updateLauncher && _config.AutoUpdateLauncher && _updater.IsInstalled && LauncherUpdateButton.IsVisible) await UpdateLauncherAsync();
    }

    void ShowReleases()
    {
        var installed = Releases.Installed();
        var selected = (ReleaseListBox.SelectedItem as ReleaseRow)?.Release.Tag;
        var rows = _allReleases
            .Where(r => r.Kind == ReleaseKind.Game && (_config.IncludePrereleases || !r.Prerelease || installed.Contains(r.Version)))
            .Select(r => new ReleaseRow(r, installed.Contains(r.Version), r.Version == PlayVersion))
            .ToList();
        ReleaseListBox.ItemsSource = rows;
        ReleaseListBox.SelectedItem = rows.FirstOrDefault(r => r.Release.Tag == selected);
        NotesEmpty.IsVisible = ReleaseListBox.SelectedItem is null;
        ReleasesEmpty.IsVisible = rows.Count == 0;
        ReleasesEmpty.Text = Tr(_allReleases.Any(r => r.Kind == ReleaseKind.Game) ? "Only nightly builds so far: tick Nightly builds beside Play to list them."
            : "No releases listed yet. Check for releases below.");
        VersionCount.Text = Tr("{1} releases · {2} installed", rows.Count, installed.Count);
        ShowVersionButtons();
        ShowFooter();
    }

    async void RefreshReleases_Click(object? sender, RoutedEventArgs e) => await RefreshReleasesAsync();

    void ReleaseListBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var release = (ReleaseListBox.SelectedItem as ReleaseRow)?.Release;
        ReleaseNotes.Content = release is null ? null
            : MarkdownView.Render(new NewsPost("", release.Title, null, null, null, ReleaseNotesMarkdown(release.Notes), release.PageUrl), _news, OpenLink);
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
        if (_download is not null) { _download.Cancel(); return; }
        if (ReleaseListBox.SelectedItem is ReleaseRow { Release: var release }) await InstallAsync(release);
    }

    // The footer's Install latest, or Cancel while a download runs.
    async void InstallLatest_Click(object? sender, RoutedEventArgs e)
    {
        if (_download is not null) { _download.Cancel(); return; }
        if (LatestRelease is { } release) await InstallAsync(release);
    }

    static readonly Avalonia.Media.FontFamily Mono = new("Cascadia Mono, Consolas, DejaVu Sans Mono, Menlo, monospace");

    // The download percentage in a mono font, padded to "100%", so the footer buttons keep their width while it counts.
    void ShowProgress(double p)
    {
        var percent = $"{p,4:0%}";
        InstallLatestText.Inlines = [new Run(Tr("Installing") + " "), new Run(percent) { FontFamily = Mono }];
        UpdateHint.Inlines = [new Run(Tr("Installing") + " "), new Run(percent) { FontFamily = Mono }, new Run(" · " + Tr("click to cancel"))];
        if (PlayButton.Classes.Contains("pending"))
            PlayText.Inlines = [new Run(Tr("Installing") + " "), new Run(percent) { FontFamily = Mono }];
    }

    // "Downloading <title>  12.3 / 456.7 MB · 85.2 Mbps" (MB = 10^6 bytes, Mbps = 10^6 bits a second), the figures in mono and
    // padded so the line keeps its width; "Unpacking <title>..." once every byte is in.
    void ShowDownloadStatus(Release release, long done, long total, double bitsPerSecond)
    {
        if (total > 0 && done >= total) { Status(Tr("Unpacking {1}...", release.Title)); return; }
        var size = total > 0 ? $"{done / 1e6,6:0.0} / {total / 1e6:0.0} MB" : $"{done / 1e6:0.0} MB";
        var speed = bitsPerSecond > 0 ? $" · {bitsPerSecond / 1e6,5:0.0} Mbps" : "";
        StatusText.Inlines = [new Run(Tr("Downloading {1}", release.Title) + "  "), new Run(size + speed) { FontFamily = Mono }];
    }

    void SetText(TextBlock block, string text) => block.Inlines = [new Run(Tr(text))];

    async Task InstallAsync(Release release)
    {
        using var cts = _download = new CancellationTokenSource();
        ShowVersionButtons();
        ShowProgress(0);
        SkipButton.IsVisible = false;
        ToolTip.SetTip(InstallLatestButton, Tr("Cancel the download of {1}", release.Title));
        ToolTip.SetTip(UpdateButton, Tr("Cancel the download of {1}", release.Title));
        if (PlayButton.Classes.Contains("pending")) ToolTip.SetTip(PlayButton, Tr("Cancel the download of {1}", release.Title));
        DownloadProgress.Value = StatusProgress.Value = 0;
        DownloadProgress.IsVisible = StatusProgress.IsVisible = true;
        Status(Tr("Downloading {1}...", release.Title));
        // The speed is smoothed over half-second samples, so the shown figure doesn't flicker with every 100 ms report.
        var clock = Stopwatch.StartNew();
        double sampleTime = 0, bitsPerSecond = 0;
        long sampleBytes = 0;
        try
        {
            await _releases.InstallAsync(release, new Progress<(long Done, long Total)>(p =>
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
            _config.SelectedVersion = release.Version == Releases.Installed().FirstOrDefault() ? null : release.Version;
            _config.Save();
            RefreshInstalled();
            ShowReleases();
            Status(Tr("{1} installed.", release.Title));
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            Status(Tr("Download of {1} cancelled.", release.Title));
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
        {
            Status(Tr("Install failed: {1}", ex.Message));
        }
        finally
        {
            _download = null;
            DownloadProgress.IsVisible = StatusProgress.IsVisible = false;
            ShowVersionButtons();
            ShowFooter();
        }
    }

    void Uninstall_Click(object? sender, RoutedEventArgs e)
    {
        if (_download is not null || ReleaseListBox.SelectedItem is not ReleaseRow { Release: var release }) return;
        var dir = Releases.VersionDirectory(release.Version);
        if (!Directory.Exists(dir)) { Status(Tr("{1} is not installed.", release.Title)); return; }
        try
        {
            Directory.Delete(dir, true);
            RefreshInstalled();
            ShowReleases();
            Status(Tr("{1} uninstalled.", release.Title));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status(Tr("Could not remove {1}: {2}", dir, ex.Message));
        }
    }

    async void OpenVersionFolder_Click(object? sender, RoutedEventArgs e)
    {
        if (ReleaseListBox.SelectedItem is not ReleaseRow { Release: var release }) return;
        var dir = Releases.VersionDirectory(release.Version);
        if (!Directory.Exists(dir)) { Status(Tr("{1} is not installed.", release.Title)); return; }
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
        if (_updater.IsInstalled)
        {
            try { version = await _updater.CheckAsync(); }
            catch (Exception ex) { LauncherAbout.Text = Tr("Version {1}. The update check failed: {2}", LauncherVersion, ex.Message); return; }
            LauncherUpdateButton.Tag = null;
        }
        else
        {
            var page = _allReleases.Where(r => r.Kind == ReleaseKind.Launcher && (_config.IncludePrereleases || !r.Prerelease))
                .FirstOrDefault(r => Version.TryParse(r.Version.Split('-')[0], out var v) && v > Version.Parse(LauncherVersion));
            version = page?.Version;
            LauncherUpdateButton.Tag = page?.PageUrl;
        }
        LauncherUpdateButton.IsVisible = version is not null;
        _launcherUpdateVersion = version;
        LauncherUpdateButton.Content = Tr(_updater.IsInstalled ? "Update to {1}" : "Get launcher {1}", version);
        TitleLauncherUpdateButton.IsVisible = version is not null;
        TitleLauncherUpdateText.Text = Tr(_updater.IsInstalled ? "Update launcher to {1}" : "Get launcher {1}", version);
        ToolTip.SetTip(TitleLauncherUpdateButton, _updater.IsInstalled ? "Download the new launcher and restart into it" : "Open the launcher's release page");
        if (version is not null) Status(Tr("Launcher {1} is available.", version));
    }

    void Steam_Click(object? sender, RoutedEventArgs e)
    {
        _config.LaunchAsSteamKenshi = SteamBox.IsChecked == true;
        _config.Save();
    }

    void AutoUpdate_Click(object? sender, RoutedEventArgs e)
    {
        _config.AutoUpdateLauncher = AutoUpdateBox.IsChecked == true;
        _config.Save();
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
            await _updater.UpdateAndRestartAsync(p => Dispatcher.UIThread.Post(() =>
            {
                Status(Tr("Updating the launcher: {1}%", p));
                StatusProgress.Value = p / 100.0;
            }), CancellationToken.None);
        }
        catch (Exception ex)
        {
            StatusProgress.IsVisible = false;
            Status(Tr("Launcher update failed: {1}", ex.Message));
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
    WindowEdge? _resizeEdge;
    PixelPoint _resizeStart;
    Win32Window.Rect _resizeRect;

    void Grip_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { Tag: string tag } grip || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var edge = Enum.Parse<WindowEdge>(tag);
        if (!OperatingSystem.IsWindows() || TryGetPlatformHandle()?.Handle is not { } hwnd || !Win32Window.GetWindowRect(hwnd, out _resizeRect))
        {
            BeginResizeDrag(edge, e);
            return;
        }
        _resizeEdge = edge;
        _resizeStart = this.PointToScreen(e.GetPosition(this));
        e.Pointer.Capture(grip);
        e.Handled = true;
    }

    void Grip_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_resizeEdge is not { } edge || TryGetPlatformHandle()?.Handle is not { } hwnd) return;
        var now = this.PointToScreen(e.GetPosition(this));
        int dx = now.X - _resizeStart.X, dy = now.Y - _resizeStart.Y;
        int minWidth = (int)Math.Ceiling(MinWidth * DesktopScaling), minHeight = (int)Math.Ceiling(MinHeight * DesktopScaling);
        var (left, top, right, bottom) = (_resizeRect.Left, _resizeRect.Top, _resizeRect.Right, _resizeRect.Bottom);
        if (edge is WindowEdge.West or WindowEdge.NorthWest or WindowEdge.SouthWest) left = Math.Min(left + dx, right - minWidth);
        if (edge is WindowEdge.East or WindowEdge.NorthEast or WindowEdge.SouthEast) right = Math.Max(right + dx, left + minWidth);
        if (edge is WindowEdge.North or WindowEdge.NorthWest or WindowEdge.NorthEast) top = Math.Min(top + dy, bottom - minHeight);
        if (edge is WindowEdge.South or WindowEdge.SouthWest or WindowEdge.SouthEast) bottom = Math.Max(bottom + dy, top + minHeight);
        Win32Window.SetWindowPos(hwnd, IntPtr.Zero, left, top, right - left, bottom - top, Win32Window.NoZOrder | Win32Window.NoActivate);
    }

    void Grip_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_resizeEdge is null) return;
        _resizeEdge = null;
        e.Pointer.Capture(null);
    }

    void Grip_PointerCaptureLost(object? sender, PointerCaptureLostEventArgs e) => _resizeEdge = null;

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

    async void Play_Click(object? sender, RoutedEventArgs e)
    {
        // As the update button: install the latest, or cancel its download.
        if (PlayButton.Classes.Contains("pending")) { InstallLatest_Click(sender, e); return; }
        await StartGame(continueSave: false);
    }

    // Starts the played version; with continueSave, in the newest save (ContinueSave.cs), not at all when its mods are other ones.
    async Task StartGame(bool continueSave)
    {
        if (_install is null) { Status("Set the Kenshi folder first (Settings)."); return; }
        if (FindPlayedGame() is not (string exe, string version)) return;
        // A second game would share meitou.user.json and the mod list with the first; whichever closes last overwrites the other's.
        if (_running is { HasExited: false }) { Status("Meitou is already running: close it before starting it again."); return; }

        SaveModSlot? save = null;
        if (continueSave && (save = await ContinueTarget(_install)) is null) return;
        if (!SaveMods()) return;
        if (_graphicsSave?.IsEnabled == true) SaveGame();
        if (_running is { HasExited: false }) return;   // a second click while the save was checked started the game already

        var start = GameStartInfo(exe, _install);
        var steamAppIdFiles = SetUpSteamAppId(start, exe, _install);
        if (!SetUpRenderer(start, steamAppIdFiles, out string? renderer, out bool metalFallback)) return;
        AddArguments(start, renderer, save);

        string started = renderer is null ? Tr("Meitou {1} started.", version)
            : Tr("Meitou {1} started with {2}{3}.", version, RendererLabel(renderer), metalFallback ? " (Vulkan loader missing)" : "");
        if (save is not null) started += Tr(" Continuing '{1}'.", save.Name);
        Launch(start, exe, version, started, steamAppIdFiles);
    }

    // The game Play starts and its version; null, with the reason in the status, when there is none.
    (string Exe, string Version)? FindPlayedGame()
    {
#if DEBUG
        if (FindDevelopmentGame() is { } development) return (development, "Debug build");
#endif
        if (PlayVersion is not { } version) { Status("Install a version first (Versions)."); return null; }
        if (Releases.FindGame(Releases.VersionDirectory(version)) is { } exe) return (exe, version);
        Status(Tr("Meitou {1} is not installed any more.", version));
        return null;
    }

    static ProcessStartInfo GameStartInfo(string exe, GameInstall gameInstall)
    {
        var start = new ProcessStartInfo(exe)
        {
            WorkingDirectory = LauncherConfig.DataDirectory,
            UseShellExecute = false,
#if DEBUG
            RedirectStandardOutput = true,
            RedirectStandardError = true,
#endif
        };
        start.Environment[GameInstall.EnvironmentVariable] = gameInstall.Root;
        // The Metal GPU cull is experimental and has caused GPU progress timeouts; it is not a user setting.
        start.Environment.Remove("MEITOU_METAL_GPU_CULL");
        start.Environment.Remove("MEITOU_METAL_GPU_BUDGET_MB");
        return start;
    }

    // Steamworks reads steam_appid.txt from the working directory when the game is started outside Steam. Keep it only for the lifetime of
    // this launch; never ship it or overwrite a user's file. A file that already holds Kenshi's ID is one an earlier launch left (the
    // launcher closed before the game did), so this launch takes it over and removes it. Answers the files written.
    List<string> SetUpSteamAppId(ProcessStartInfo start, string exe, GameInstall gameInstall)
    {
        var written = new List<string>();
        string[] appIdFiles = [.. new[] { Path.Combine(start.WorkingDirectory, "steam_appid.txt"), Path.Combine(Path.GetDirectoryName(exe)!, "steam_appid.txt") }
            .Distinct(StringComparer.OrdinalIgnoreCase)];
        if (!_config.LaunchAsSteamKenshi)
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
        else if (OperatingSystem.IsWindows() && File.Exists(Path.Combine(gameInstall.Root, "steam_api64.dll")) && !SteamAppIdConflict(appIdFiles))
        {
            foreach (string file in appIdFiles)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                    File.WriteAllText(file, KenshiAppId);
                    written.Add(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            start.Environment["SteamAppId"] = KenshiAppId;
            start.Environment["SteamGameId"] = KenshiAppId;
        }
        return written;
    }

    // Any other ID in either place is the user's: write nothing then, so both places keep agreeing.
    static bool SteamAppIdConflict(string[] appIdFiles)
    {
        bool conflict = false;
        foreach (string file in appIdFiles)
        {
            try { if (File.Exists(file) && File.ReadAllText(file).Trim() != KenshiAppId) conflict = true; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { conflict = true; }
        }
        return conflict;
    }

    static void RemoveSteamAppIds(List<string> files) { foreach (string file in files) TryDeleteSteamAppId(file); }

    // macOS: the renderer to pass and its environment; false (the status says why, the app ID files removed) when it cannot start.
    bool SetUpRenderer(ProcessStartInfo start, List<string> steamAppIdFiles, out string? renderer, out bool metalFallback)
    {
        renderer = null;
        metalFallback = false;
        if (!OperatingSystem.IsMacOS()) return true;

        // A release advertises its renderers in settings-schema.json; an old release without the key is Vulkan-only.
        bool metalSupported = PlaySchema().Schema?.Renderers.Contains("metal", StringComparer.OrdinalIgnoreCase) == true;
        string requested = _config.Renderer;
        const string noMetal = "This Meitou version does not include Metal; choose Vulkan or install a newer version.";
        string? problem = requested == "metal" && !MacSupport.MetalAvailable ? "Metal needs an Apple-silicon Mac; choose Auto or Vulkan."
            : requested == "metal" && !metalSupported ? noMetal
            : null;
        if (problem is null)
        {
            if (requested == "auto" && !metalSupported) requested = "vulkan";
            renderer = MacSupport.Resolve(requested);
            metalFallback = requested == "vulkan" && renderer == "metal";
            problem = renderer == "metal" ? (metalSupported ? null : noMetal)
                : MacSupport.AddVulkanEnvironment(start.Environment) ? null
                : VulkanLoaderProblem(metalSupported);
        }
        if (problem is not null)
        {
            RemoveSteamAppIds(steamAppIdFiles);
            Status(problem);
            return false;
        }

        if (renderer == "metal")
        {
            start.Environment.Remove("DYLD_LIBRARY_PATH");
            start.Environment["MEITOU_METAL_GPU_BUDGET_MB"] = _config.MetalMemoryBudget.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        return true;
    }

    static string VulkanLoaderProblem(bool metalSupported) => MacSupport.MetalAvailable && !metalSupported
        ? "This Meitou version has no Metal renderer and no Vulkan loader; install a newer version or MoltenVK."
        : MacSupport.MetalAvailable
            ? "The Vulkan loader is not installed: use Auto/Metal, or install MoltenVK from Settings > Setup."
            : "The Vulkan loader is not installed: run 'brew install molten-vk vulkan-loader' in a terminal, then Play again.";

    void AddArguments(ProcessStartInfo start, string? renderer, SaveModSlot? save)
    {
        foreach (var arg in SplitArguments(_config.LaunchArguments)) start.ArgumentList.Add(arg);
        if (renderer is not null)
        {
            // Appended last so the selector wins over a hand-written --renderer in Launch arguments.
            start.ArgumentList.Add("--renderer");
            start.ArgumentList.Add(renderer);
        }
        if (save is not null)
        {
            start.ArgumentList.Add("--load");
            start.ArgumentList.Add(save.Folder);
        }
    }

    void Launch(ProcessStartInfo start, string exe, string version, string started, List<string> steamAppIdFiles)
    {
        try
        {
            var game = Process.Start(start);
            _running = game;
            Status(started);
            if (game is null) { RemoveSteamAppIds(steamAppIdFiles); return; }
#if DEBUG
            ForwardOutput(game);
#endif
            WatchExit(game, version, Path.Combine(Path.GetDirectoryName(exe)!, "log.txt"), steamAppIdFiles);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            RemoveSteamAppIds(steamAppIdFiles);
            Status(Tr("Could not start Meitou {1}: {2}", version, ex.Message));
        }
    }

    // The game's window is all a player sees of it, so a crash (above all one before the window opens) is reported here.
    void WatchExit(Process game, string version, string log, List<string> steamAppIdFiles)
    {
        game.EnableRaisingEvents = true;
        game.Exited += (_, _) =>
        {
            int code = game.ExitCode;
            Dispatcher.UIThread.Post(() =>
            {
                // On the UI thread, as Play: a game started since then has taken these files over and removes them itself.
                if (_running == game) RemoveSteamAppIds(steamAppIdFiles);
                if (code != 0) Status(Tr("Meitou {1} stopped with an error (exit code {2}); its log is {3}", version, code, log));
                _ = RefreshContinueAsync();
            });
        };
    }

#if DEBUG
    static void ForwardOutput(Process game)
    {
        game.OutputDataReceived += (_, data) => { if (data.Data is { } line) { Console.WriteLine($"[Meitou] {line}"); Debug.WriteLine($"[Meitou] {line}"); } };
        game.ErrorDataReceived += (_, data) => { if (data.Data is { } line) { Console.Error.WriteLine($"[Meitou] {line}"); Debug.WriteLine($"[Meitou] {line}"); } };
        game.BeginOutputReadLine();
        game.BeginErrorReadLine();
    }
#endif

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
