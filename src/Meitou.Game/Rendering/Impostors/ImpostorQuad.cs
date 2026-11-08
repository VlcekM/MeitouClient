using System.Numerics;

namespace Meitou.Rendering.Impostors;

/// <summary>
/// The tight billboard quad (docs/render-foliage.md "Foliage cost in the orbit"): a billboard spans the rectangle that holds the outline of the mesh's bounding box
/// seen from the eye (the vertex stage works it out from the box's half extents) instead of the square of the bounding sphere's silhouette, so a tall tree or a flat
/// bush has fewer transparent corners to sample and shade. The A/B <c>impostor-quad</c>. The half extents travel in the
/// push constants' spare word, three 10-bit numbers (thousandths of the radius, rounded up) and the on bit.
/// </summary>
public static class ImpostorQuad
{
    public static bool Enabled { get; set; } = true;

    /// <summary>The spare word for a mesh whose bounding box has <paramref name="halfExtent"/> (object units) about the atlas's sphere of <paramref name="radius"/>; 0 (the sphere) when the box is not inside the sphere.</summary>
    public static uint Pack(Vector3 halfExtent, float radius)
    {
        if (!(radius > 0)) return 0;
        uint x = Step(halfExtent.X, radius), y = Step(halfExtent.Y, radius), z = Step(halfExtent.Z, radius);
        return x > 1023 || y > 1023 || z > 1023 ? 0 : x | y << 10 | z << 20 | 1u << 30;
    }

    static uint Step(float half, float radius) => (uint)MathF.Ceiling(Math.Max(half, 0) / radius * 1023f);
}
