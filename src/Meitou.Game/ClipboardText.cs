using Silk.NET.GLFW;
using Silk.NET.Input;

namespace Meitou.Game;

/// <summary>The clipboard's text, null when it holds none: GLFW throws when the clipboard holds something else (an image, files).</summary>
static class ClipboardText
{
    public static string? Read(IKeyboard keyboard)
    {
        try { return keyboard.ClipboardText; }
        catch (GlfwException) { return null; }
    }
}
