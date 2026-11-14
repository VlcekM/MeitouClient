using System.Numerics;
using Meitou.Data.Characters;
using Meitou.Game.Rendering.Frame;

namespace Meitou.Game.Rendering.Characters;

/// <summary>
/// Where a character anchored to another's bone (<see cref="CharacterInstance.Anchor"/>) is, from the carrier's palette as it is drawn this frame
/// (docs/character-renderer.md "Bone anchors").
/// </summary>
static class BoneAnchor
{
    /// <summary>
    /// The anchored character's node in the world: at <paramref name="anchor"/>'s bone as <paramref name="bones"/> (the carrier's skin palette on
    /// <paramref name="rig"/>) pose it under <paramref name="world"/>, moved by the anchor's offset (scaled with the carrier) and turned by its
    /// rotation, both in the bone's frame. The carrier's size moves the node and does not size it. Null without the bone.
    /// </summary>
    public static (Vector3 Position, Quaternion Rotation)? Node(SkeletonRig rig, Matrix4x4[] bones, in Matrix4x4 world, in CharacterAnchor anchor)
    {
        if (rig.Handle(anchor.Bone) is not int h || (uint)h >= (uint)bones.Length) return null;

        // The skin matrix is the bone's scale, then its turn from the binding pose, about the binding position (SkeletonRig's skinning transform):
        // so it carries the binding position to the posed one, and its rotation after the binding one is the bone's model-space rotation.
        if (!Matrix4x4.Decompose(bones[h], out _, out var fromBind, out _)) return null;
        if (!Matrix4x4.Decompose(world, out _, out var placed, out _)) return null;

        var at = Vector3.Transform(rig.BindPosition[h], bones[h] * world);
        var turn = Quaternion.Normalize(placed * fromBind * Quaternion.Conjugate(rig.BindRotationInverse[h]));

        var offset = Vector3.Transform(anchor.Offset * world.ScaleLength(), turn);
        return (at + offset, Quaternion.Normalize(turn * anchor.Rotation));
    }
}
