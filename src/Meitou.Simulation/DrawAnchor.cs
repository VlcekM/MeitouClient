using System.Numerics;

namespace Meitou.Simulation;

/// <summary>
/// Draws a character on another's bone (a rider on its mount; docs/modding.md "Bone anchors"): its node sits at <paramref name="Bone"/> of
/// <paramref name="Carrier"/> as the renderer poses that one in the drawn frame, moved by <paramref name="Offset"/> and turned by
/// <paramref name="Rotation"/> in the bone's frame. The offset scales with the carrier's size; the character keeps its own size, pose and gear.
/// Display only: the simulation reads none of it.
/// </summary>
public readonly record struct DrawAnchor(CharacterId Carrier, string Bone, Vector3 Offset, Quaternion Rotation);
