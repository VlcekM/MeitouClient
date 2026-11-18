using System.Numerics;

namespace Meitou.Game.Rendering.Post;

/// <summary>
/// A look photo mode's filter starts from (docs/viewer.md "Photo mode", Look tab).
/// </summary>
public enum FilterLook
{
    /// <summary>
    /// The picture as it is.
    /// </summary>
    None,

    /// <summary>
    /// Grey by luminance.
    /// </summary>
    BlackAndWhite,

    /// <summary>
    /// A hard black and white: grey with a steep contrast.
    /// </summary>
    Noir,

    /// <summary>
    /// Brown old prints.
    /// </summary>
    Sepia,

    /// <summary>
    /// Warmer light: more red, less blue.
    /// </summary>
    Warm,

    /// <summary>
    /// Colder light: less red, more blue.
    /// </summary>
    Cool,

    /// <summary>
    /// Washed out: less colour, lifted blacks, a softer contrast.
    /// </summary>
    Faded,

    /// <summary>
    /// Richer colour and a firmer contrast.
    /// </summary>
    Vivid,

    /// <summary>
    /// Half the colour kept, a firmer contrast (a bleach bypass).
    /// </summary>
    Bleach,
}

/// <summary>
/// A colour filter on the display picture, after the grade (<see cref="PostOptions.PhotoFilter"/>): each output channel is a weighted sum of the
/// input's plus an offset (<see cref="Red"/>, <see cref="Green"/>, <see cref="Blue"/>: the weights in X Y Z, the offset in W), then a contrast about
/// mid-grey.
/// </summary>
public readonly record struct ColourFilter(Vector4 Red, Vector4 Green, Vector4 Blue, float Contrast)
{
    /// <summary>
    /// The luminance weights the grade uses (BT.601).
    /// </summary>
    static readonly Vector3 Luma = new(0.299f, 0.587f, 0.114f);

    /// <summary>
    /// The filter of <paramref name="look"/> at <paramref name="strength"/> (0: none of it, 1: all), then a saturation (1 unchanged), a temperature
    /// and a tint (-1 to 1, 0 unchanged: warmer is redder and less blue, a positive tint is more magenta) and a contrast factor (1 unchanged); null
    /// when it changes nothing.
    /// </summary>
    public static ColourFilter? Of(FilterLook look, float strength, float saturation, float temperature, float tint, float contrast)
    {
        var (matrix, offset, lookContrast) = Look(look);
        strength = Math.Clamp(strength, 0, 1);
        var p = Matrix4x4.Identity + (matrix - Matrix4x4.Identity) * strength;
        float k = (1 + (lookContrast - 1) * strength) * contrast;
        offset *= strength;

        // The saturation about the luminance, then the white balance.
        var grey = Rows(Luma, Luma, Luma);
        var s = grey * (1 - saturation) + Matrix4x4.Identity * saturation;
        var w = Diagonal(new Vector3(1 + 0.1f * temperature, 1 - 0.08f * tint, 1 - 0.1f * temperature));
        var m = w * s * p;

        if (IsIdentity(m) && offset == Vector3.Zero && MathF.Abs(k - 1) < 1e-4f) return null;

        return new ColourFilter(new Vector4(m.M11, m.M12, m.M13, offset.X), new Vector4(m.M21, m.M22, m.M23, offset.Y),
            new Vector4(m.M31, m.M32, m.M33, offset.Z), MathF.Max(k, 0.01f));
    }

    /// <summary>
    /// A look's matrix (rows: output channels), offset and contrast.
    /// </summary>
    static (Matrix4x4 Matrix, Vector3 Offset, float Contrast) Look(FilterLook look)
    {
        var grey = Rows(Luma, Luma, Luma);
        return look switch
        {
            FilterLook.BlackAndWhite => (grey, Vector3.Zero, 1),
            FilterLook.Noir => (grey, new Vector3(-0.02f), 1.35f),
            FilterLook.Sepia => (Rows(new(0.393f, 0.769f, 0.189f), new(0.349f, 0.686f, 0.168f), new(0.272f, 0.534f, 0.131f)), Vector3.Zero, 1),
            FilterLook.Warm => (Diagonal(new Vector3(1.08f, 1.0f, 0.86f)), Vector3.Zero, 1),
            FilterLook.Cool => (Diagonal(new Vector3(0.9f, 0.98f, 1.1f)), Vector3.Zero, 1),
            FilterLook.Faded => (grey * 0.25f + Matrix4x4.Identity * 0.75f, new Vector3(0.05f), 0.85f),
            FilterLook.Vivid => (grey * -0.35f + Matrix4x4.Identity * 1.35f, Vector3.Zero, 1.1f),
            FilterLook.Bleach => (grey * 0.5f + Matrix4x4.Identity * 0.5f, Vector3.Zero, 1.2f),
            _ => (Matrix4x4.Identity, Vector3.Zero, 1),
        };
    }

    /// <summary>
    /// A colour matrix from its three rows (the fourth row and column as the identity's).
    /// </summary>
    static Matrix4x4 Rows(Vector3 r, Vector3 g, Vector3 b) => new(r.X, r.Y, r.Z, 0, g.X, g.Y, g.Z, 0, b.X, b.Y, b.Z, 0, 0, 0, 0, 1);

    /// <summary>
    /// A colour matrix scaling each channel.
    /// </summary>
    static Matrix4x4 Diagonal(Vector3 d) => Rows(new(d.X, 0, 0), new(0, d.Y, 0), new(0, 0, d.Z));

    /// <summary>
    /// The colour part of <paramref name="m"/> is the identity, within a ten-thousandth.
    /// </summary>
    static bool IsIdentity(Matrix4x4 m)
    {
        var d = m - Matrix4x4.Identity;
        return MathF.Abs(d.M11) + MathF.Abs(d.M12) + MathF.Abs(d.M13) + MathF.Abs(d.M21) + MathF.Abs(d.M22) + MathF.Abs(d.M23)
            + MathF.Abs(d.M31) + MathF.Abs(d.M32) + MathF.Abs(d.M33) < 1e-4f;
    }
}
