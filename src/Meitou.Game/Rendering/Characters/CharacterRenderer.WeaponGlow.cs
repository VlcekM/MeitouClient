using System.Numerics;
using Meitou.Data.Characters;

namespace Meitou.Game.Rendering.Characters;

internal sealed partial class CharacterRenderer
{
    /// <summary>
    /// The code mods' weapon glows by draw key (docs/modding.md "Weapon glow"): the colour and its level as <see cref="PackGlow"/> makes them, set
    /// by the host each frame before the draw. Empty without a mod.
    /// </summary>
    public Dictionary<long, (float Colour, float Level)> WeaponGlows { get; } = [];

    /// <summary>
    /// A glow for the instances (<see cref="Live.GlowColour"/>, <see cref="Live.GlowLevel"/>): the colour over its brightest channel, 8 bits a
    /// channel as an exact integer, and that channel times <paramref name="strength"/>.
    /// </summary>
    public static (float Colour, float Level) PackGlow(Vector3 colour, float strength)
    {
        float top = MathF.Max(colour.X, MathF.Max(colour.Y, colour.Z));
        if (top <= 0 || strength <= 0) return (0, 0);

        static uint Byte(float c) => (uint)Math.Clamp(MathF.Round(c * 255), 0, 255);
        var unit = colour / top;
        return ((Byte(unit.X) << 16) | (Byte(unit.Y) << 8) | Byte(unit.Z), top * strength);
    }

    /// <summary>
    /// The character's weapon glow for its instances this frame.
    /// </summary>
    void WeaponGlowOf(ref Live l)
    {
        (l.GlowColour, l.GlowLevel) = WeaponGlows.Count > 0 && WeaponGlows.TryGetValue(l.Key, out var glow) ? glow : (0, 0);
    }

    /// <summary>
    /// A held melee weapon's blade (the gear blood's mapping, <see cref="PartMaterial.BladeAxis"/>) takes the character's glow in the w of the
    /// matrix's last two rows (<see cref="CharacterShaders.WeaponGlow"/>); a sheathed, worn or ranged part does not.
    /// </summary>
    static Matrix4x4 GlowModel(AssetPart part, in Live l, Matrix4x4 model, ref uint flags)
    {
        if (l.GlowLevel <= 0 || !HeldBlade(part)) return model;

        flags |= CharacterShaders.WeaponGlow;
        model.M34 = l.GlowColour;
        model.M44 = l.GlowLevel;
        return model;
    }

    /// <summary>
    /// A melee weapon's blade as held (the gear blood's mapping): not worn, sheathed or ranged.
    /// </summary>
    static bool HeldBlade(AssetPart part) =>
        !part.Shared && part.Material.GearBlood && part.Shown is not (WeaponShown.Sheathed or WeaponShown.BackSheathed or WeaponShown.RangedSheathed);

    /// <summary>
    /// Where the held blade of the character <paramref name="key"/> was last drawn at <paramref name="along"/> of its length (0 the hilt end of the
    /// mesh, 1 the tip; <see cref="BladeSpan"/>); null when it was not drawn in the world in the last update or holds no blade drawn. Render
    /// thread, after <see cref="Update"/> (a mod's weapon glow particles, ModEffectPlayer).
    /// </summary>
    public Vector3? BladePoint(long key, float along)
    {
        if (!_states.TryGetValue(key, out var s) || s.Seen != _updateStamp || !s.InWorld || s.Asset is not { IsIcon: false } asset || s.Posed < 0)
            return null;

        foreach (var part in asset.Parts)
        {
            if (!HeldBlade(part) || part.PickBoxes.Length == 0 || part.AttachIndex < 0 || part.AttachIndex >= s.Attach.Length) continue;
            if (!part.ShowsWith(s.WeaponDrawn, s.RangedDrawn, back: s.BackDrawn, weaponsHidden: s.WeaponsHidden)) continue;

            var local = BladeSpan.Point(part.PickBoxes, part.Material.BladeAxis, part.Material.BladeLow, part.Material.BladeScale, along);
            return Vector3.Transform(local, s.Attach[part.AttachIndex] * s.World);
        }
        return null;
    }
}

/// <summary>
/// A weapon mesh's blade line in its own space, as the gear blood maps it (<see cref="PartMaterial.BladeAxis"/>): through the middle of its pick
/// boxes, along the long axis from the hilt end (value <c>low</c> on the axis) over <c>1 / scale</c>.
/// </summary>
static class BladeSpan
{
    /// <summary>
    /// The point at <paramref name="along"/> of the blade line (0 the hilt end, 1 the tip).
    /// </summary>
    public static Vector3 Point(ReadOnlySpan<PickBox> boxes, Vector3 axis, float low, float scale, float along)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var box in boxes)
        {
            min = Vector3.Min(min, box.Min);
            max = Vector3.Max(max, box.Max);
        }

        var middle = (min + max) / 2;
        return middle + axis * (low + along / scale - Vector3.Dot(middle, axis));
    }
}
