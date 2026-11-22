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
    /// Where the held blade of the character <paramref name="key"/> was last drawn (<see cref="BladeSpan.Frame"/>): its base, the turn that takes
    /// +Y along it and its length; null when the character was not drawn in the world in the last update or holds no blade drawn. Render thread,
    /// after <see cref="Update"/> (a mod's weapon glow particles, ModEffectPlayer).
    /// </summary>
    public BladeFrame? HeldBladeFrame(long key)
    {
        if (!_states.TryGetValue(key, out var s) || s.Seen != _updateStamp || !s.InWorld || s.Asset is not { IsIcon: false } asset || s.Posed < 0)
            return null;

        foreach (var part in asset.Parts)
        {
            if (!HeldBlade(part) || part.PickBoxes.Length == 0 || part.AttachIndex < 0 || part.AttachIndex >= s.Attach.Length) continue;
            if (!part.ShowsWith(s.WeaponDrawn, s.RangedDrawn, back: s.BackDrawn, weaponsHidden: s.WeaponsHidden)) continue;

            var m = part.Material;
            return BladeSpan.Frame(part.PickBoxes, m.BladeAxis, m.BladeLow, m.BladeScale, s.Attach[part.AttachIndex] * s.World);
        }
        return null;
    }
}

/// <summary>
/// A held blade in the world: its base (the hilt end of the glowing span), the turn that takes a system's local +Y along it (towards the tip, its
/// X across the blade's width) and its length in world units.
/// </summary>
internal readonly record struct BladeFrame(Vector3 Base, Quaternion Turn, float Length)
{
    /// <summary>
    /// The length a blade particle system is authored to span along its +Y (docs/modding.md "Weapon glow").
    /// </summary>
    public const float AuthoredLength = 7;

    /// <summary>
    /// The scale that makes <see cref="AuthoredLength"/> span the blade.
    /// </summary>
    public float Stretch => Length / AuthoredLength;
}

/// <summary>
/// A weapon mesh's blade in its own space, as the gear blood maps it (<see cref="PartMaterial.BladeAxis"/>): a line through the middle of its pick
/// boxes along the long axis, from the mesh's hilt end (value <c>low</c> on the axis) over <c>1 / scale</c> to the tip; the glowing blade is the
/// part from <see cref="Start"/> of it.
/// </summary>
static class BladeSpan
{
    /// <summary>
    /// Where along the mesh the blade starts (the glow comes up from 0.25 to 0.4, CharacterShaders): the hilt and guard are below.
    /// </summary>
    public const float Start = 0.3f;

    /// <summary>
    /// The point at <paramref name="along"/> of the line (0 the hilt end, 1 the tip).
    /// </summary>
    public static Vector3 Point(ReadOnlySpan<PickBox> boxes, Vector3 axis, float low, float scale, float along)
    {
        var (min, max) = Bounds(boxes);
        var middle = (min + max) / 2;
        return middle + axis * (low + along / scale - Vector3.Dot(middle, axis));
    }

    /// <summary>
    /// The blade placed by <paramref name="world"/> (the mesh's own space to the world): the base at <see cref="Start"/>, +Y towards the tip and
    /// X along the boxes' wider cross axis (the blade's width), and the length from the base to the tip.
    /// </summary>
    public static BladeFrame Frame(ReadOnlySpan<PickBox> boxes, Vector3 axis, float low, float scale, Matrix4x4 world)
    {
        var at = Vector3.Transform(Point(boxes, axis, low, scale, Start), world);
        var tip = Vector3.Transform(Point(boxes, axis, low, scale, 1), world);
        var up = Vector3.Normalize(tip - at);
        var across = Vector3.TransformNormal(Across(boxes, axis), world);
        across = Vector3.Normalize(across - up * Vector3.Dot(across, up));
        var forward = Vector3.Cross(across, up);
        var turn = new Matrix4x4(across.X, across.Y, across.Z, 0, up.X, up.Y, up.Z, 0, forward.X, forward.Y, forward.Z, 0, 0, 0, 0, 1);
        return new BladeFrame(at, Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(turn)), Vector3.Distance(at, tip));
    }

    /// <summary>
    /// The cross axis (of the mesh's two other than <paramref name="axis"/>) along which the boxes are wider.
    /// </summary>
    static Vector3 Across(ReadOnlySpan<PickBox> boxes, Vector3 axis)
    {
        var (min, max) = Bounds(boxes);
        var size = max - min;
        var a = MathF.Abs(axis.X) > 0.5f ? Vector3.UnitY : Vector3.UnitX;
        var b = MathF.Abs(axis.Z) > 0.5f ? Vector3.UnitY : Vector3.UnitZ;
        return Vector3.Dot(size, a) >= Vector3.Dot(size, b) ? a : b;
    }

    /// <summary>
    /// The union of the boxes.
    /// </summary>
    static (Vector3 Min, Vector3 Max) Bounds(ReadOnlySpan<PickBox> boxes)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var box in boxes)
        {
            min = Vector3.Min(min, box.Min);
            max = Vector3.Max(max, box.Max);
        }
        return (min, max);
    }
}
