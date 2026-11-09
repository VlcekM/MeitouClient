using Meitou.Data.Textures;
using Meitou.Game.Rendering.Ui;
using Meitou.Game.Ui;

namespace Meitou.Game.Settings;

/// <summary>
/// Continuous window surfaces and reusable button slices from the Meitou UI kit.
/// </summary>
sealed class SettingsMenuArt
{
    /// <summary>
    /// The texture name prefix of the settings window's art.
    /// </summary>
    public const string Prefix = "meitou:settings:";

    /// <summary>
    /// The texture name prefix of the surfaces (versioned).
    /// </summary>
    public static string SurfacePrefix => Prefix + "v3:";

    /// <summary>
    /// The window plate's pixels per UI unit; it is drawn nine-sliced, <see cref="WindowBorder"/> pixels per edge (shadow margin + corner radius).
    /// </summary>
    public const float WindowDensity = 2;

    /// <summary>
    /// The window plate's nine-slice border in pixels.
    /// </summary>
    public const int WindowBorder = (int)((16 + 10) * WindowDensity);

    /// <summary>
    /// The picture under the luckyren Mode setting: <c>assets/luckyren-mode.png</c>, embedded; <c>ImageTexture</c> name.
    /// </summary>
    public const string LuckyrenImage = Prefix + "luckyren-mode";

    /// <summary>
    /// Bakes the Meitou surfaces (the buttons, paper and window) from the UI atlas once; nothing in the Faithful mode.
    /// </summary>
    public void Ensure(UiPainter painter, bool meitou, Func<RgbaImage?> uiAtlas)
    {
        if (!meitou || painter.HasTexture(SurfacePrefix + "button")) return;

        var atlas = uiAtlas() ?? throw new FileNotFoundException("Kenshi_UI.png not found or unreadable.");
        painter.SetImage(SurfacePrefix + "button", MenuArt.Plate(atlas, new(612, 766, 118, 45), 160, 40, 1, 0.7f), srgb: false);
        painter.SetImage(SurfacePrefix + "button-hover", MenuArt.Plate(atlas, new(612, 766, 118, 45), 160, 40, 1, 0.85f), srgb: false);
        painter.SetImage(SurfacePrefix + "button-selected", MenuArt.Plate(atlas, new(741, 766, 118, 45), 160, 40, 1, 0.85f), srgb: false);
        painter.SetImage(SurfacePrefix + "paper", MenuArt.Card(atlas, new(352, 457, 164, 81), 160, 40, 1, 0, 1), srgb: false);
        painter.SetImage(SurfacePrefix + "paper-hover", MenuArt.Card(atlas, new(352, 457, 164, 81), 160, 40, 1, 0, 1.1f), srgb: false);

        // A fifth of the 1120x720 window, same aspect, so the stretched rust keeps its proportions.
        painter.SetImage(
            SurfacePrefix + "window",
            MenuArt.WindowSurface(atlas, (int)(224 * WindowDensity), (int)(144 * WindowDensity), WindowDensity),
            srgb: false);
    }

    /// <summary>
    /// Uploads the luckyren Mode picture once (nothing when the resource is missing).
    /// </summary>
    public void EnsureLuckyrenImage(UiPainter painter)
    {
        if (painter.HasTexture(LuckyrenImage)) return;

        using var stream = typeof(SettingsMenuArt).Assembly.GetManifestResourceStream("luckyren-mode.png");
        if (stream is null) return;

        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        painter.SetImage(LuckyrenImage, TextureLoader.LoadImage(bytes.ToArray()));
    }
}
