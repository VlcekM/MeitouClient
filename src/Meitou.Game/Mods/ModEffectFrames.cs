using System.Numerics;
using Meitou.Simulation;

namespace Meitou.Modding;

/// <summary>
/// Where an anchored effect plays this frame: the system's origin, the turn of its axes and the scale of its emitters.
/// </summary>
internal readonly record struct EffectFrame(Vector3 At, Quaternion Turn, float Stretch);

/// <summary>
/// What locates the anchors that follow something drawn: the character renderer and the world's buildings in the game, a stand-in in tests. Each
/// is null when it is not there this frame (not drawn, no weapon drawn, no such bone or building).
/// </summary>
internal interface IEffectFrameSource
{
    /// <summary>
    /// The character's bone as drawn, moved by <paramref name="offset"/> and turned by <paramref name="turn"/> in its frame; stretch 1.
    /// </summary>
    EffectFrame? Bone(CharacterId on, string bone, Vector3 offset, Quaternion turn);

    /// <summary>
    /// The melee weapon the character holds drawn: its hilt end, +Y along the blade, its own stretch.
    /// </summary>
    EffectFrame? Weapon(CharacterId on);

    /// <summary>
    /// The building's origin and turn; stretch 1.
    /// </summary>
    EffectFrame? Building(string id);

    /// <summary>
    /// The bolt as drawn: its nose, +Y along its flight; stretch 1 (none by default: a source without bolts).
    /// </summary>
    EffectFrame? Projectile(int id) => null;
}

/// <summary>
/// An <see cref="EffectAnchor"/> to its frame (docs/modding.md "Visual effects").
/// </summary>
internal static class EffectFrames
{
    /// <summary>
    /// The frame <paramref name="anchor"/> stands for now, its stretch overridden by the anchor's and times its size (pulsing by
    /// <paramref name="age"/>, the seconds since the effect or trail started); null when <paramref name="source"/> cannot locate it.
    /// </summary>
    public static EffectFrame? Locate(in EffectAnchor anchor, IEffectFrameSource source, double age = 0)
    {
        var turn = anchor.Turn is { } t ? Quaternion.Normalize(t) : Quaternion.Identity;
        EffectFrame? frame = anchor.Kind switch
        {
            EffectAnchorKind.Point => new EffectFrame(anchor.Offset, turn, 1),
            EffectAnchorKind.Bone => source.Bone(anchor.Character, anchor.Name!, anchor.Offset, turn),
            EffectAnchorKind.Weapon => OnBlade(source.Weapon(anchor.Character), anchor),
            EffectAnchorKind.Building => Within(source.Building(anchor.Name!), anchor.Offset, turn),
            EffectAnchorKind.Projectile => Within(source.Projectile(anchor.ProjectileId), anchor.Offset, turn),
            _ => null,
        };
        if (frame is not { } f) return null;

        return f with { Stretch = (anchor.Stretch ?? f.Stretch) * anchor.SizeAt(age) };
    }

    /// <summary>
    /// The turn that takes +Y along <paramref name="direction"/> (a unit vector), the least turn from +Y.
    /// </summary>
    public static Quaternion AlongY(Vector3 direction)
    {
        float d = Vector3.Dot(Vector3.UnitY, direction);
        if (d > 0.99999f) return Quaternion.Identity;
        if (d < -0.99999f) return Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI);

        var axis = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, direction));
        return Quaternion.CreateFromAxisAngle(axis, MathF.Acos(d));
    }

    /// <summary>
    /// A weapon's <paramref name="frame"/> moved by the anchor's offset, scaled by the blade's stretch (so 8.5 along +Y stays the tip), and
    /// turned by its turn; the frame unchanged for an anchor with neither.
    /// </summary>
    static EffectFrame? OnBlade(EffectFrame? frame, in EffectAnchor anchor)
    {
        if (frame is not { } f || anchor.Offset == Vector3.Zero && anchor.Turn is null) return frame;

        var turn = anchor.Turn is { } t ? Quaternion.Normalize(t) : Quaternion.Identity;
        return Within(f, anchor.Offset * f.Stretch, turn);
    }

    /// <summary>
    /// <paramref name="frame"/> moved by <paramref name="offset"/> and turned by <paramref name="turn"/> in its own axes.
    /// </summary>
    static EffectFrame? Within(EffectFrame? frame, Vector3 offset, Quaternion turn) =>
        frame is { } f ? new EffectFrame(f.At + Vector3.Transform(offset, f.Turn), Quaternion.Normalize(f.Turn * turn), f.Stretch) : null;
}
