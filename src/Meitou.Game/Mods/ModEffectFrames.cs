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
}

/// <summary>
/// An <see cref="EffectAnchor"/> to its frame (docs/modding.md "Visual effects").
/// </summary>
internal static class EffectFrames
{
    /// <summary>
    /// The frame <paramref name="anchor"/> stands for now, its stretch overridden by the anchor's; null when <paramref name="source"/> cannot
    /// locate it.
    /// </summary>
    public static EffectFrame? Locate(in EffectAnchor anchor, IEffectFrameSource source)
    {
        var turn = anchor.Turn is { } t ? Quaternion.Normalize(t) : Quaternion.Identity;
        EffectFrame? frame = anchor.Kind switch
        {
            EffectAnchorKind.Point => new EffectFrame(anchor.Offset, turn, 1),
            EffectAnchorKind.Bone => source.Bone(anchor.Character, anchor.Name!, anchor.Offset, turn),
            EffectAnchorKind.Weapon => source.Weapon(anchor.Character),
            EffectAnchorKind.Building => Within(source.Building(anchor.Name!), anchor.Offset, turn),
            _ => null,
        };
        return frame is { } f && anchor.Stretch is { } s ? f with { Stretch = s } : frame;
    }

    /// <summary>
    /// <paramref name="frame"/> moved by <paramref name="offset"/> and turned by <paramref name="turn"/> in its own axes.
    /// </summary>
    static EffectFrame? Within(EffectFrame? frame, Vector3 offset, Quaternion turn) =>
        frame is { } f ? new EffectFrame(f.At + Vector3.Transform(offset, f.Turn), Quaternion.Normalize(f.Turn * turn), f.Stretch) : null;
}
