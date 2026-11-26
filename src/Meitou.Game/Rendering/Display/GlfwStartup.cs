using Silk.NET.GLFW;

namespace Meitou.Game.Rendering.Display;

/// <summary>
/// GLFW's settings that must be made before it starts (before the first window).
/// </summary>
static class GlfwStartup
{
    /// <summary>
    /// Keeps the working directory: on macOS, GLFW's start otherwise changes it to <c>Contents/Resources</c> when the program runs from an app
    /// bundle (GLFW_COCOA_CHDIR_RESOURCES, on by default), and the game's settings and saves are found from the working directory the launcher
    /// gives it (docs/macos-port.md "App bundle"). Does nothing once GLFW has started.
    /// </summary>
    public static void KeepWorkingDirectory()
    {
        if (!OperatingSystem.IsMacOS() || GlfwProvider.GLFW.IsValueCreated) return;

        GlfwProvider.UninitializedGLFW.Value.InitHint(InitHint.CocoaChdirResources, false);
    }
}
