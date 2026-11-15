using System.Numerics;

namespace Meitou.Simulation;

/// <summary>
/// A rigid mesh a code mod hangs on one of a character's bones (a saddle on a mount; docs/modding.md "Bone meshes"): <paramref name="Mesh"/>'s
/// origin sits at <paramref name="Bone"/> as the renderer poses the character in the drawn frame, moved by <paramref name="Offset"/> and turned by
/// <paramref name="Rotation"/> in the bone's frame; the offset and the mesh scale with the character's size. <paramref name="Key"/> names it among
/// the character's bone meshes (<see cref="CharacterCold.SetBoneMesh"/>). Display only: the simulation reads none of it.
/// </summary>
public readonly record struct BoneMesh(string Key, string Mesh, string Bone, Vector3 Offset, Quaternion Rotation);
