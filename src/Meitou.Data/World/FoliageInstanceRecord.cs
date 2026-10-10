using System.Numerics;
using System.Runtime.InteropServices;

namespace Meitou.Data.World;

/// <summary>
/// One placed foliage mesh as the cull reads it, in the layout a GPU cull will read it from a storage buffer (std430, 96 bytes;
/// docs/renderer-native.md 5.2, "InstanceRecord"). Built on the layout worker (<see cref="FoliageGrouping"/>, <see cref="FoliageLayoutCache.TryLoadGrouped"/>);
/// <see cref="Sphere"/> is filled once the mesh's bounds are known (<c>FoliageCull.FillSpheres</c> in the renderer), with the same call the cull made per frame
/// before, so it is bit-identical. (In Meitou.Rendering until the layout cache built these records itself.)
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct FoliageInstanceRecord
{
    /// <summary>The instance's transform (row-vector convention, translation in row 4; <c>M14</c> is 0 here and carries the fade once packed).</summary>
    public Matrix4x4 Transform;
    /// <summary>Bounding sphere in the world: xyz = <c>Vector3.Transform(mesh centre, Transform)</c>, w = mesh radius × scale.</summary>
    public Vector4 Sphere;
    /// <summary>x, z of the position (the range is measured along the ground), the scale, and the group's index within its zone; for a TERRAIN-mode rock group on the GPU cull, <c>FoliageCull.RockBits</c> instead (nothing else reads w).</summary>
    public Vector4 Ground;

    public const int Size = 96;

    /// <summary>Bytes a record takes in the GPU's instance arena (<see cref="Pack"/>): 17 floats, 71% of <see cref="Size"/>.</summary>
    public const int GpuSize = 68;

    /// <summary>
    /// The arena's layout, lossless for what the kernels read: the transform's 12 rotation and translation floats (rows 1 to 4, xyz; the fourth
    /// column is 0, 0, 0, 1 for a placement and the kernels write those back), the sphere (4), and <c>Ground.W</c> (a rock's bits). <c>Ground.X</c> and
    /// <c>Ground.Y</c> are the translation's x and z (the position the range is measured from, the very same floats), and <c>Ground.Z</c> (the scale)
    /// only feeds the sphere's radius on the CPU, so none of the three is stored. Returns false when a record breaks that (a fourth column that is
    /// not exactly 0, 0, 0, 1, or a ground position that is not the translation's): the arena would then not hold what the CPU cull reads.
    /// </summary>
    public static bool Pack(in FoliageInstanceRecord r, Span<float> d)
    {
        var t = r.Transform;
        d[0] = t.M11; d[1] = t.M12; d[2] = t.M13;
        d[3] = t.M21; d[4] = t.M22; d[5] = t.M23;
        d[6] = t.M31; d[7] = t.M32; d[8] = t.M33;
        d[9] = t.M41; d[10] = t.M42; d[11] = t.M43;
        d[12] = r.Sphere.X; d[13] = r.Sphere.Y; d[14] = r.Sphere.Z; d[15] = r.Sphere.W;
        d[16] = r.Ground.W;
        static int Bits(float f) => BitConverter.SingleToInt32Bits(f);
        return Bits(t.M14) == 0 && Bits(t.M24) == 0 && Bits(t.M34) == 0 && t.M44 == 1f && Bits(r.Ground.X) == Bits(t.M41) && Bits(r.Ground.Y) == Bits(t.M43);
    }
}
