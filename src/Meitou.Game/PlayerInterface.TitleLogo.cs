using Meitou.Data.Textures;

namespace Meitou.Game;

sealed partial class PlayerInterface
{
    const string TitleLogoTexture = "meitou:title-logo";
    /// <summary>Makes the Meitou title's wordmark (<see cref="TitleLogo"/>); asked once, null keeps the plain lettering.</summary>
    public Func<RgbaImage?>? TitleLogoSource { get; set; }
    Lazy<RgbaImage?>? titleLogo;

    /// <summary>Whether the wordmark can be drawn; the first ask makes it and hands it to the painter.</summary>
    bool TitleLogoReady()
    {
        if (uiPainter is not { } painter || TitleLogoSource is not { } source) return false;
        if (painter.HasTexture(TitleLogoTexture)) return true;
        titleLogo ??= new(source);
        if (titleLogo.Value is not { } image) return false;
        painter.SetImage(TitleLogoTexture, image);
        return true;
    }

    /// <summary>The wordmark's texture goes while the title is closed (its image stays, so it is uploaded again when the title opens).</summary>
    void ReleaseTitleLogo()
    {
        if (uiPainter is { } painter && painter.HasTexture(TitleLogoTexture)) painter.Forget(TitleLogoTexture);
    }
}
