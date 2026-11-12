using Avalonia.Media;
using Avalonia.Media.Fonts;
using Meitou.Content;

namespace Meitou.Launcher;

/// <summary>
/// Exo 2, the face of the game's Meitou UI (docs/meitou-ui.md), read from the Kenshi install's <c>data/gui/fonts</c> at runtime as the
/// game does, so the launcher ships no font. Without an install the theme falls back to the system font.
/// </summary>
sealed class KenshiFonts : FontCollectionBase
{
    /// <summary>
    /// The collection's key in font URIs (<c>fonts:Kenshi</c>).
    /// </summary>
    public static readonly Uri CollectionKey = new("fonts:Kenshi");

    /// <summary>
    /// The family to use in styles: Exo 2 from this collection, then the system font.
    /// </summary>
    public static readonly FontFamily Family = new("fonts:Kenshi#Exo 2, $Default");

    /// <inheritdoc/>
    public override Uri Key => CollectionKey;

    /// <summary>
    /// Adds the collection once at startup; every face found under <paramref name="install"/> is loaded.
    /// </summary>
    public static void Register(GameInstall? install)
    {
        var fonts = new KenshiFonts();
        if (install is not null)
        {
            var dir = Path.Combine(install.DataDirectory, "gui", "fonts");
            foreach (var name in new[] { "Exo2-Medium.ttf", "Exo2-SemiBold.ttf", "Exo2-Bold.ttf" })
            {
                var path = Path.Combine(dir, name);
                if (!File.Exists(path)) continue;

                // A memory copy: the collection may read the face after this returns.
                try
                {
                    fonts.TryAddGlyphTypeface(new MemoryStream(File.ReadAllBytes(path)), out _);
                }
                catch (IOException)
                {
                }
            }
        }
        FontManager.Current.AddFontCollection(fonts);
    }
}
