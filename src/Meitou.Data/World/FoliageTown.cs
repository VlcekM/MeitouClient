using System.Numerics;

namespace Meitou.Data.World;

/// <summary>
/// A town that keeps foliage away: X/Z, its <c>no-foliage range</c> and whether the player's faction owns it (resource meshes then grow inside the
/// range).
/// </summary>
public readonly record struct FoliageTown(Vector2 At, float Range, bool Owned = false)
{
    /// <summary>
    /// The town's X/Z and range.
    /// </summary>
    public void Deconstruct(out Vector2 at, out float range) => (at, range) = (At, Range);
}