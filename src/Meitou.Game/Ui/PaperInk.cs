using System.Numerics;
using Meitou.Data.Textures;
using Meitou.Data.Ui;

namespace Meitou.Game.Ui;

/// <summary>
/// The ink of Meitou's text on its baked paper (the Continue card, the settings page title, primary buttons): the paper skin's text colour when it
/// reads on the paper, else whichever of dark or light ink does. A UI mod that repaints <c>Kenshi_UI.png</c> darker without recolouring the text
/// would otherwise leave dark ink on dark paper.
/// </summary>
static class PaperInk
{
    /// <summary>
    /// The game's dark ink (its Secondary colour, #140806).
    /// </summary>
    static readonly Vector4 Dark = new(20 / 255f, 8 / 255f, 6 / 255f, 1);

    /// <summary>
    /// The Meitou kit's light ink (#d9ceb8).
    /// </summary>
    static readonly Vector4 Light = new(217 / 255f, 206 / 255f, 184 / 255f, 1);

    /// <summary>
    /// The least luminance difference (sRGB-encoded, 0..1) between ink and paper that still reads. The game's paper is 0.44 and its ink 0.04;
    /// Dark UI's paper is 0.11 and its grey Secondary 0.46.
    /// </summary>
    const float MinContrast = 0.25f;

    /// <summary>
    /// The atlas and resources <see cref="s_ink"/> was picked for; other ones pick again.
    /// </summary>
    static (RgbaImage? Atlas, MyGuiResources? Resources) s_pickedFor;

    /// <summary>
    /// The ink picked for <see cref="s_pickedFor"/>.
    /// </summary>
    static Vector4 s_ink = Dark;

    /// <summary>
    /// The ink for text on the paper baked from <paramref name="player"/>'s UI atlas; the dark ink while the atlas is missing.
    /// </summary>
    public static Vector4 On(PlayerInterface player)
    {
        var atlas = player.UiAtlas();
        if (atlas is null) return Dark;
        if (ReferenceEquals(s_pickedFor.Atlas, atlas) && ReferenceEquals(s_pickedFor.Resources, player.UiResources)) return s_ink;

        s_ink = Pick(MenuArt.MeanLuminance(atlas, MenuArt.PaperCrop), MyGuiColours.TemplateInk(player.UiResources, "Kenshi_GenericTextBoxSkin"));
        s_pickedFor = (atlas, player.UiResources);
        return s_ink;
    }

    /// <summary>
    /// The skin's ink when it contrasts with paper of luminance <paramref name="paper"/>, else the dark or light ink, whichever contrasts more.
    /// </summary>
    static Vector4 Pick(float paper, string skinInk)
    {
        var skin = MyGuiColours.Parse(skinInk, Dark);
        if (MathF.Abs(Luminance(skin) - paper) >= MinContrast) return skin;

        return MathF.Abs(Luminance(Dark) - paper) >= MathF.Abs(Luminance(Light) - paper) ? Dark : Light;
    }

    /// <summary>
    /// The Rec. 709 luminance of an sRGB-encoded colour, without decoding it.
    /// </summary>
    static float Luminance(Vector4 c) => 0.2126f * c.X + 0.7152f * c.Y + 0.0722f * c.Z;
}
