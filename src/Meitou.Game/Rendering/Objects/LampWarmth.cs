using System.Numerics;

namespace Meitou.Game.Rendering.Objects;

/// <summary>
/// The <c>lampwarmth</c> switch (Meitou, on): a yellow lamp with more green than the game's torches (the Ceiling Lamp's E9FF9B, the Searchlight's
/// F1FFC4, the Spotlight's F1E8B4) is drawn amber, at the brightness it had; docs/render-lights.md "What is drawn".
/// </summary>
public static class LampWarmth
{
    /// <summary>
    /// Whether the switch is on.
    /// </summary>
    public static bool Enabled { get; set; } = true;

    /// <summary>
    /// The green a warmed lamp keeps per unit of red: that of the game's own fire lights (<c>torch_light</c>, FFCE5E), which stay as they are.
    /// </summary>
    const float TorchGreen = 0xCE / 255f;

    /// <summary>
    /// How yellow a lamp is (red and green over blue, as a share of its strongest channel) for the whole shift; a paler one takes a share of it.
    /// </summary>
    const float FullAt = 0.25f;

    /// <summary>
    /// A lamp's colour as drawn: <paramref name="colour"/> itself with the switch off.
    /// </summary>
    public static Vector3 Of(Vector3 colour) => Enabled ? Warm(colour) : colour;

    /// <summary>
    /// <paramref name="colour"/> with its green lowered towards <see cref="TorchGreen"/> of its red, and its blue by as much, by how yellow it is,
    /// then scaled back to its luminance. A lamp whose blue is not its weakest channel (white, cyan, blue) or that is already as warm is unchanged.
    /// </summary>
    public static Vector3 Warm(Vector3 colour)
    {
        float strongest = MathF.Max(colour.X, MathF.Max(colour.Y, colour.Z));
        float target = colour.X * TorchGreen;
        if (strongest <= 0 || colour.Y <= target) return colour;

        float yellow = (MathF.Min(colour.X, colour.Y) - colour.Z) / strongest;
        if (yellow <= 0) return colour;

        float weight = MathF.Min(yellow / FullAt, 1);
        float cut = float.Lerp(colour.Y, target, weight) / colour.Y;
        var warm = new Vector3(colour.X, colour.Y * cut, colour.Z * cut);
        return warm * (Luminance(colour) / Luminance(warm));
    }

    /// <summary>
    /// The Rec. 709 luminance of a linear colour.
    /// </summary>
    static float Luminance(Vector3 colour) => Vector3.Dot(colour, new Vector3(0.2126f, 0.7152f, 0.0722f));
}
