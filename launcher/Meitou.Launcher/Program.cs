using Avalonia;

namespace Meitou.Launcher;

/// <summary>
/// The launcher's entry point.
/// </summary>
static class Program
{
    /// <summary>
    /// Runs Velopack's hooks, loads the native libraries and starts the Avalonia app.
    /// </summary>
    [STAThread]
    public static void Main(string[] args)
    {
        // First: Velopack runs its install, update and uninstall hooks here and may exit (docs/launcher.md, "Self-update").
        Velopack.VelopackApp.Build().Run();
        NativeLibraries.Load();
        AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace().StartWithClassicDesktopLifetime(args);
    }
}
