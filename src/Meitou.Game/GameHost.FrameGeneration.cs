using Meitou.Rendering;
using Meitou.Rendering.Display;
using Meitou.Rendering.Upscalers;
using Silk.NET.Input;

namespace Meitou.Game;

sealed partial class GameHost
{
    /// <summary>The frame generator for the display's swapchain (the Frame generation setting, where it could be honoured), or null; made on the first frame.</summary>
    static IFrameGenerator? CreateFrameGenerator(VulkanDisplay display)
    {
        string? why = null;
        IFrameGenerator? generator = null;
        if (display.DlssFrameGeneration) generator = DlssFrameGeneration.TryCreate(display.Context, display.Streamline, out why);
        else if (display.FrameGenerationLayer is { } layer) generator = FrameGeneration.TryCreate(display.Context, layer, out why);
        if (generator is null && why is not null) Console.WriteLine($"frame generation  unavailable: {why}");
        return generator;
    }

    /// <summary>The settings' line on frame generation: the loaded one's state, or the setting's change that waits for a restart.</summary>
    static string FrameGenerationState(VulkanDisplay display, FrameGenerationMethod loaded, FrameGenerationMethod now)
    {
        bool available = loaded == FrameGenerationMethod.Dlss ? display.DlssFrameGeneration : display.FrameGeneration;
        if (now == FrameGenerationMethod.Off) return "off";
        if (now != loaded) return $"{FrameGenerationName(now)} after a restart";
        return available ? "active" : "unavailable (see the log), plain swapchain in use";
    }

    /// <summary>The stats panel's note on the frames shown: DLSS reports what it presented, FSR 3 shows two per rendered frame.</summary>
    static string FrameGenerationShown(IFrameGenerator? generator) => generator switch
    {
        DlssFrameGeneration { Enabled: true, PresentedPerSecond: > 0 } dlss => $" rendered, {dlss.PresentedPerSecond:0} shown",
        { Enabled: true } => " (x2 shown with frame generation)",
        _ => "",
    };

    internal static string FrameGenerationName(FrameGenerationMethod method) => method switch
    {
        FrameGenerationMethod.Fsr => "FSR 3",
        FrameGenerationMethod.Dlss => "DLSS",
        _ => "off",
    };

    /// <summary>
    /// The game's pointer as the window's cursor (a custom cursor image of the pointer at its drawn size, its hot spot on the mouse), set again only when the pointer
    /// changes. While frame generation runs the window system draws it: in a frame it would reach the screen a held-back frame late and, as part of the
    /// interface, be interpolated between frames.
    /// </summary>
    sealed class SystemPointer
    {
        string? shown;

        public void Show(ICursor cursor, PlayerInterface player, string name)
        {
            if (name == shown) return;
            shown = name;
            if (player.PointerImage(name) is not { } pointer)
            {
                cursor.Type = CursorType.Standard;
                return;
            }
            cursor.Type = CursorType.Custom;
            cursor.HotspotX = pointer.HotX;
            cursor.HotspotY = pointer.HotY;
            cursor.Image = new Silk.NET.Core.RawImage(pointer.Image.Width, pointer.Image.Height, pointer.Image.Pixels);
        }

        /// <summary>Back to the system's arrow (Hardware mouse, or frame generation off).</summary>
        public void Standard(ICursor cursor)
        {
            if (shown is null) return;
            shown = null;
            cursor.Type = CursorType.Standard;
            cursor.StandardCursor = StandardCursor.Default;
        }
    }
}
