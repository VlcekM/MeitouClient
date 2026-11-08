using System.Numerics;
using Meitou.Data.Textures;
using Meitou.Game.Rendering.Ui;

namespace Meitou.Game;

/// <summary>Continuous window surfaces and reusable button slices from the Meitou UI kit.</summary>
sealed class SettingsMenuArt
{
    public const string Prefix = "meitou:settings:";
    public static string SurfacePrefix => Prefix + "v3:";
    /// <summary>The window plate's pixels per UI unit; it is drawn nine-sliced, <see cref="WindowBorder"/> pixels per edge (shadow margin + corner radius).</summary>
    public const float WindowDensity = 2;
    public const int WindowBorder = (int)((16 + 10) * WindowDensity);

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
        painter.SetImage(SurfacePrefix + "window", MenuArt.WindowSurface(atlas, (int)(224 * WindowDensity), (int)(144 * WindowDensity), WindowDensity), srgb: false);
    }

    /// <summary>The picture under the luckyren Mode setting: <c>assets/luckyren-mode.png</c>, embedded; <c>ImageTexture</c> name.</summary>
    public const string LuckyrenImage = Prefix + "luckyren-mode";

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

static partial class MenuArt
{
    /// <summary>Rust under the kit's dark tint, with a rounded inner rim and a soft drop shadow.</summary>
    public static RgbaImage Window(RgbaImage atlas) => WindowSurface(atlas, 128, 128, 1);

    public static RgbaImage WindowSurface(RgbaImage atlas, int width, int height, float s)
    {
        int margin = (int)(16 * s);
        var image = new RgbaImage(width + margin * 2, height + margin * 2);
        for (int y = 0; y < image.Height; y++)
            for (int x = 0; x < image.Width; x++)
            {
                float px = x + 0.5f - margin, py = y + 0.5f - margin;
                float distance = RoundedDistance(px, py, width, height, 10 * s);
                float shadow = 0.55f * Smooth(1 - Math.Clamp((RoundedDistance(px, py - 6 * s, width, height, 10 * s) + 3 * s) / (14 * s), 0, 1));
                // The full rust crop contains a welded T joint. Use the uninterrupted sheet to its right.
                var texture = Sample(atlas, new(1620, 136, 200, 510), px / width, py / height);
                var colour = Vector3.Lerp(texture, new Vector3(14, 11, 9) / 255, 0.86f);
                if (distance > -s) colour = Vector3.Lerp(colour, new Vector3(175, 166, 139) / 255, 0.12f);
                Put(image, x, y, Over(new(colour, Math.Clamp(0.5f - distance, 0, 1)), new(0, 0, 0, shadow)));
            }
        return image;
    }
}
