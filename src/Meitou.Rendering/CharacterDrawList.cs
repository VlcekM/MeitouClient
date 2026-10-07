using System.Numerics;
using Meitou.Data.Characters;

namespace Meitou.Rendering;

/// <summary>One animation to pose a character with: the skeleton animation's name, its time in seconds and its blend weight.</summary>
public readonly record struct CharacterPose(string Animation, float Time, float Weight);

/// <summary>
/// A character to draw this frame, already interpolated by the host. <see cref="Key"/> is stable while the character exists, so
/// the renderer can keep its GPU resources and last frame's bones (motion vectors) by it; <see cref="Appearance"/> is compared by
/// reference to find the meshes and materials.
/// </summary>
public readonly record struct CharacterInstance(long Key, CharacterAppearance Appearance, Vector3 Position, float Yaw, IReadOnlyList<CharacterPose> Poses);

/// <summary>
/// The characters of one frame. The renderers know nothing of the simulation (docs/engine.md): the host fills this list from the
/// simulation's snapshots each frame, as it copies the camera into <see cref="WorldCamera"/>.
/// </summary>
public sealed class CharacterDrawList
{
    public List<CharacterInstance> Items { get; } = [];
    public void Clear() => Items.Clear();
    public void Add(in CharacterInstance instance) => Items.Add(instance);
}
