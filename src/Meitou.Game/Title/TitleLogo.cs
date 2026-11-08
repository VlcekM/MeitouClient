using Meitou.Data.Textures;

namespace Meitou.Game;

/// <summary>
/// The brush-stroke Kenshi wordmark for the Meitou title: <c>assets/branding/kenshi-logo.png</c>, embedded in the game. The game ships no
/// logo file of its own (docs/game/ui-screens.md, "Title artwork and location"); the image is Lo-Fi Games' artwork and trademark, not
/// GPL (THIRD_PARTY_NOTICES.md).
/// </summary>
static class TitleLogo
{
    const string Resource = "kenshi-logo.png";

    /// <summary>The wordmark as straight-alpha RGBA, rows top to bottom, or null when the resource is missing.</summary>
    public static RgbaImage? Load()
    {
        using var stream = typeof(TitleLogo).Assembly.GetManifestResourceStream(Resource);
        if (stream is null) return null;
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        return TextureLoader.LoadImage(bytes.ToArray());
    }
}
