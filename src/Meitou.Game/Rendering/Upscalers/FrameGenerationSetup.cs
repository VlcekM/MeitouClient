using Meitou.Game.Rendering.Display;
using Meitou.Game.Rendering.Post;
using Silk.NET.Input;

namespace Meitou.Game.Rendering.Upscalers;

/// <summary>
/// The host's side of frame generation: the generator for the swapchain, the settings' and fps counter's lines on it, and the system pointer it
/// needs.
/// </summary>
static class FrameGenerationSetup
{
    #region Generator

    /// <summary>
    /// The frame generator for the display's swapchain (the Frame generation setting, where it could be honoured), or null; made on the first frame.
    /// </summary>
    internal static IFrameGenerator? CreateFrameGenerator(VulkanDisplay display)
    {
        string? why = null;
        IFrameGenerator? generator = null;
        if (display.DlssFrameGeneration) generator = DlssFrameGeneration.TryCreate(display.Context, display.Streamline, out why);
        else if (display.FrameGenerationLayer is { } layer) generator = FrameGeneration.TryCreate(display.Context, layer, out why);

        if (generator is null && why is not null) Console.WriteLine($"frame generation  unavailable: {why}");
        return generator;
    }

    /// <summary>
    /// The settings' line on frame generation: the loaded one's state, or the setting's change that waits for a restart.
    /// </summary>
    internal static string FrameGenerationState(VulkanDisplay display, FrameGenerationMethod loaded, FrameGenerationMethod now)
    {
        bool available = loaded == FrameGenerationMethod.Dlss ? display.DlssFrameGeneration : display.FrameGeneration;
        if (now != loaded) return $"{FrameGenerationName(now)} after a restart";
        if (loaded == FrameGenerationMethod.Off) return "off";

        return available ? "active" : "unavailable (see the log), plain swapchain in use";
    }

    /// <summary>
    /// Frames shown per rendered one: what DLSS reports it presented, two for FSR 3, one without frame generation.
    /// </summary>
    static uint FramesShownPerRendered(IFrameGenerator? generator) => generator switch
    {
        DlssFrameGeneration { Enabled: true, PresentedPerRendered: > 0 } dlss => dlss.PresentedPerRendered,
        { Enabled: true } => 2,
        _ => 1,
    };

    #endregion

    #region Text

    /// <summary>
    /// The stats panel's note on the frames shown per rendered one.
    /// </summary>
    internal static string FrameGenerationShown(IFrameGenerator? generator) =>
        generator is { Enabled: true } ? $" rendered, x{FramesShownPerRendered(generator)} shown" : "";

    /// <summary>
    /// The fps counter (Ctrl+F9): the frames shown per second, with frame generation also the rendered ones; and a rendered frame's time.
    /// </summary>
    internal static string FpsCounterText(int frames, double seconds, IFrameGenerator? generator)
    {
        double rendered = frames / seconds;
        double ms = seconds * 1000 / frames;
        uint shown = FramesShownPerRendered(generator);
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        if (shown <= 1) return string.Create(inv, $"{rendered:0} fps  ({ms:0.0} ms)");

        return string.Create(inv, $"{rendered * shown:0} fps  ({rendered:0} rendered, {ms:0.0} ms)");
    }

    /// <summary>
    /// The method's name as the settings and the log show it: "FSR 3", "DLSS" or "off".
    /// </summary>
    internal static string FrameGenerationName(FrameGenerationMethod method) => method switch
    {
        FrameGenerationMethod.Fsr => "FSR 3",
        FrameGenerationMethod.Dlss => "DLSS",
        _ => "off",
    };

    #endregion

    /// <summary>
    /// The game's pointer as the window's cursor (a custom cursor image of the pointer at its drawn size, its hot spot on the mouse), set again only
    /// when the pointer changes, or a system cursor. While frame generation runs the window system draws it: in a frame it would reach the screen a
    /// held-back frame late and, as part of the interface, be interpolated between frames.
    /// </summary>
    internal sealed class SystemPointer
    {
        /// <summary>
        /// The pointer image last shown (null: a system cursor).
        /// </summary>
        string? _shown;

        /// <summary>
        /// The system cursor last set.
        /// </summary>
        StandardCursor _standard = StandardCursor.Default;

        /// <summary>
        /// Shows the pointer <paramref name="name"/> as the window's cursor: its image and hot spot, or the system arrow when the interface has no
        /// image for it; nothing when it is already shown.
        /// </summary>
        public void Show(ICursor cursor, PlayerInterface player, string name)
        {
            if (name == _shown) return;

            _shown = name;
            if (player.PointerPanel.PointerImage(name) is not { } pointer)
            {
                cursor.Type = CursorType.Standard;
                cursor.StandardCursor = _standard = StandardCursor.Default;
                return;
            }

            cursor.Type = CursorType.Custom;
            cursor.HotspotX = pointer.HotX;
            cursor.HotspotY = pointer.HotY;
            cursor.Image = new Silk.NET.Core.RawImage(pointer.Image.Width, pointer.Image.Height, pointer.Image.Pixels);
        }

        /// <summary>
        /// Back to a system cursor (Hardware mouse, or frame generation off): the arrow, or the enhanced Hardware mouse's hand.
        /// </summary>
        public void Standard(ICursor cursor, StandardCursor kind = StandardCursor.Default)
        {
            if (_shown is null && kind == _standard) return;

            _shown = null;
            _standard = kind;
            cursor.Type = CursorType.Standard;
            cursor.StandardCursor = kind;
        }
    }
}
