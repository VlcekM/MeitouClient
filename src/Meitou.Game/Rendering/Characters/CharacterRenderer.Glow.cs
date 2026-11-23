using System.Numerics;
using Meitou.Data.Characters;
using Meitou.Modding;

namespace Meitou.Game.Rendering.Characters;

/// <summary>
/// The code mods' glows on one drawn character (docs/modding.md "Glow"), each packed for the instances (<see cref="Pack"/>; 0: none): the whole
/// character's, its held blade's and its slots'.
/// </summary>
internal sealed class CharacterGlow
{
    /// <summary>
    /// The whole character's glow.
    /// </summary>
    public uint Whole;
    /// <summary>
    /// The held blade's glow (blade-masked).
    /// </summary>
    public uint Weapon;
    /// <summary>
    /// The glows of its slots' parts.
    /// </summary>
    public Dictionary<AttachSlot, uint>? Slots;

    /// <summary>
    /// Sets <paramref name="target"/>'s glow.
    /// </summary>
    public void Set(in GlowTarget target, Vector3 colour, float strength) => Set(target, colour, strength, GlowMode.Light);

    /// <summary>
    /// Sets <paramref name="target"/>'s glow or tint (<paramref name="mode"/>).
    /// </summary>
    public void Set(in GlowTarget target, Vector3 colour, float strength, GlowMode mode)
    {
        switch (target.Kind)
        {
            case GlowTargetKind.Weapon:
                Weapon = Pack(colour, strength, bladeMasked: true, mode);
                break;
            case GlowTargetKind.Worn:
                (Slots ??= [])[target.Slot] = Pack(colour, strength, bladeMasked: false, mode);
                break;
            default:
                Whole = Pack(colour, strength, bladeMasked: false, mode);
                break;
        }
    }

    /// <summary>
    /// The glow a part takes: a held blade the weapon's, a part in a glowing slot (<paramref name="slot"/>, null for the body) its slot's, else
    /// the whole character's.
    /// </summary>
    public uint For(bool heldBlade, AttachSlot? slot)
    {
        if (heldBlade && Weapon != 0) return Weapon;
        if (slot is { } s && Slots is not null && Slots.TryGetValue(s, out uint glow)) return glow;
        return Whole;
    }

    /// <summary>
    /// A glow as four bytes from the top: the colour over its brightest channel (r, g, b), then its level (that channel times
    /// <paramref name="strength"/>, in eighths, at most 127) with bit 7 set for the blade mask; 0 for none.
    /// </summary>
    public static uint Pack(Vector3 colour, float strength, bool bladeMasked, GlowMode mode = GlowMode.Light)
    {
        if (mode == GlowMode.Tint) return PackTint(colour, strength, bladeMasked);

        float top = MathF.Max(colour.X, MathF.Max(colour.Y, colour.Z));
        uint level = (uint)Math.Clamp(MathF.Round(top * strength * 8), 0, 127);
        if (level == 0) return 0;

        static uint Byte(float c) => (uint)Math.Clamp(MathF.Round(c * 255), 0, 255);
        var unit = colour / top;
        return Byte(unit.X) << 24 | Byte(unit.Y) << 16 | Byte(unit.Z) << 8 | level | (bladeMasked ? 128u : 0);
    }

    /// <summary>
    /// A tint as four bytes from the top: its colour (each channel 0 to 1 as 0 to 254, so no byte is 255 as a glow's brightest is), then its share
    /// (0 to 1 as 0 to 127) with bit 7 set for the blade mask; 0 for none.
    /// </summary>
    public static uint PackTint(Vector3 colour, float amount, bool bladeMasked)
    {
        uint share = (uint)Math.Clamp(MathF.Round(amount * 127), 0, 127);
        if (share == 0) return 0;

        static uint Byte(float c) => (uint)Math.Clamp(MathF.Round(c * 254), 0, 254);
        return Byte(colour.X) << 24 | Byte(colour.Y) << 16 | Byte(colour.Z) << 8 | share | (bladeMasked ? 128u : 0);
    }
}

internal sealed partial class CharacterRenderer
{
    /// <summary>
    /// The code mods' glows by draw key, set by the host each frame before the draw (<see cref="SetGlow"/>). Empty without a mod.
    /// </summary>
    public Dictionary<long, CharacterGlow> Glows { get; } = [];

    /// <summary>
    /// Sets <paramref name="target"/>'s glow on the character <paramref name="key"/> for this frame.
    /// </summary>
    public void SetGlow(long key, in GlowTarget target, Vector3 colour, float strength, GlowMode mode = GlowMode.Light)
    {
        if (!Glows.TryGetValue(key, out var glow)) Glows[key] = glow = new CharacterGlow();
        glow.Set(target, colour, strength, mode);
    }

    /// <summary>
    /// The character's glows for its instances this frame.
    /// </summary>
    void GlowOf(ref Live l) => l.Glow = Glows.Count > 0 ? Glows.GetValueOrDefault(l.Key) : null;

    /// <summary>
    /// A part's glow (<see cref="CharacterGlow.For"/>) in the high byte (bits 16-23) of the w of the matrix's four rows, whose low 16 bits keep the
    /// blood (<see cref="CharacterShaders.Glow"/>).
    /// </summary>
    static Matrix4x4 GlowModel(AssetPart part, in Live l, Matrix4x4 model, ref uint flags)
    {
        if (l.Glow is not { } glow) return model;

        uint packed = glow.For(HeldBlade(part), part.IsBody ? null : part.Slot);
        if (packed == 0) return model;

        flags |= CharacterShaders.Glow;
        model.M14 = WithHigh(model.M14, packed >> 24);
        model.M24 = WithHigh(model.M24, (packed >> 16) & 255);
        model.M34 = WithHigh(model.M34, (packed >> 8) & 255);
        model.M44 = WithHigh(model.M44, packed & 255);
        return model;
    }

    /// <summary>
    /// <paramref name="w"/>'s low 16 bits (an exact integer) with <paramref name="high"/> in bits 16-23.
    /// </summary>
    static float WithHigh(float w, uint high) => ((uint)MathF.Round(w) & 0xFFFF) | high << 16;

    /// <summary>
    /// A melee weapon's blade as held (the gear blood's mapping): not worn, sheathed or ranged.
    /// </summary>
    static bool HeldBlade(AssetPart part) =>
        !part.Shared && part.Material.GearBlood && part.Shown is not (WeaponShown.Sheathed or WeaponShown.BackSheathed or WeaponShown.RangedSheathed);

    /// <summary>
    /// Where the held blade of the character <paramref name="key"/> was last drawn (<see cref="BladeSpan.Frame"/>): its base, the turn that takes
    /// +Y along it and its length; null when the character was not drawn in the world in the last update or holds no blade drawn. Render thread,
    /// after <see cref="Update"/> (a mod's effect on EffectAnchor.Weapon, ModEffectPlayer).
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
/// A held weapon in the world: its base (the mesh's hilt end), the turn that takes a system's local +Y along its blade (towards the tip, its X
/// across the blade's width) and its length from the hilt end to the tip in world units.
/// </summary>
internal readonly record struct BladeFrame(Vector3 Base, Quaternion Turn, float Length)
{
    /// <summary>
    /// The length along its +Y that a blade particle system authored with its origin at the weapon's base reaches to the tip (its box from about
    /// 1.5 to 8.5; docs/modding.md "Blade effect").
    /// </summary>
    public const float AuthoredLength = 8.5f;

    /// <summary>
    /// The scale that makes <see cref="AuthoredLength"/> span the weapon.
    /// </summary>
    public float Stretch => Length / AuthoredLength;
}

/// <summary>
/// A weapon mesh's blade in its own space, as the gear blood maps it (<see cref="PartMaterial.BladeAxis"/>): a line through the middle of its pick
/// boxes along the long axis, from the mesh's hilt end (value <c>low</c> on the axis) over <c>1 / scale</c> to the tip.
/// </summary>
static class BladeSpan
{
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
    /// The weapon placed by <paramref name="world"/> (the mesh's own space to the world): the base at the hilt end, +Y towards the tip and X along
    /// the boxes' wider cross axis (the blade's width), and the length from the base to the tip.
    /// </summary>
    public static BladeFrame Frame(ReadOnlySpan<PickBox> boxes, Vector3 axis, float low, float scale, Matrix4x4 world)
    {
        var at = Vector3.Transform(Point(boxes, axis, low, scale, 0), world);
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
