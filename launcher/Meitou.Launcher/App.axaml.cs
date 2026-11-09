using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Meitou.Content;

namespace Meitou.Launcher;

/// <summary>
/// The launcher application: loads its config, finds the Kenshi install (the saved folder, else a search), registers the game's fonts and opens the
/// main window.
/// </summary>
public sealed class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        var config = LauncherConfig.Load();
        var install = config.KenshiPath is { } saved && GameInstall.IsValid(saved) ? GameInstall.Open(saved) : GameInstall.Locate();
        KenshiFonts.Register(install);
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow(config, install);

        base.OnFrameworkInitializationCompleted();
    }
}
