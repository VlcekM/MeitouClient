using System.Numerics;
using System.Runtime.InteropServices;

namespace Meitou.Rendering;

/// <summary>
/// One placed foliage mesh as the cull reads it, in the layout a GPU cull will read it from a storage buffer (std430, 96 bytes;
/// docs/renderer-native.md 5.2, "InstanceRecord"). Built on the layout worker; <see cref="Sphere"/> is filled once the mesh's bounds
/// are known (<see cref="FoliageCull.FillSpheres"/>), with the same call the cull made per frame before, so it is bit-identical.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct FoliageInstanceRecord
{
    /// <summary>The instance's transform (row-vector convention, translation in row 4; <c>M14</c> is 0 here and carries the fade once packed).</summary>
    public Matrix4x4 Transform;
    /// <summary>Bounding sphere in the world: xyz = <c>Vector3.Transform(mesh centre, Transform)</c>, w = mesh radius × scale.</summary>
    public Vector4 Sphere;
    /// <summary>x, z of the position (the range is measured along the ground), the scale, and the group's index within its zone; for a TERRAIN-mode rock group on the GPU cull, <see cref="FoliageCull.RockBits"/> instead (nothing else reads w).</summary>
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

/// <summary>A view the cull tests spheres against: its frustum planes (xyz normal, w offset) and their normals' lengths, computed once per view.</summary>
public sealed class FoliageCullView
{
    public Vector4[] Planes { get; private set; } = [];
    public float[] NormalLengths { get; private set; } = [];

    public FoliageCullView Set(Vector4[] planes)
    {
        if (NormalLengths.Length != planes.Length) NormalLengths = new float[planes.Length];
        Planes = planes;
        for (int i = 0; i < planes.Length; i++)
        {
            var p = planes[i];
            NormalLengths[i] = MathF.Sqrt(p.X * p.X + p.Y * p.Y + p.Z * p.Z);
        }
        return this;
    }
}

/// <summary>A group's (one mesh of one layer in one zone) range for one view: where instances end, and the fade band as a reciprocal.</summary>
public readonly record struct FoliageGroupRange(float Range, float RangeSquared, float InverseBand)
{
    public static FoliageGroupRange Of(float range, float band) => new(range, range * range, 1 / band);
}

/// <summary>The output of culling one group: the visible instances as batch matrices (fade in <c>M14</c>), and, when recording for the
/// shadow cascades, every instance in range with its fade.</summary>
public sealed class FoliageCullOutput
{
    public Matrix4x4[] Visible = new Matrix4x4[64];
    public int Count;
    public int[] InRange = new int[64];
    public float[] InRangeFade = new float[64];
    public int InRangeCount;
}

/// <summary>
/// The per-instance foliage cull (docs/renderer-native.md 5.6, step A1): the CPU reference a GPU compute cull reproduces. Every float
/// operation is a plain IEEE single add, subtract, multiply, divide or square root in a fixed order (no FMA: RyuJIT does not contract
/// scalar arithmetic), so a <c>precise</c> GLSL kernel computes the same values, except the square root of the fade (Vulkan does not
/// require a correctly rounded <c>sqrt</c>), which only feeds the fade, never the decision.
/// <list type="number">
/// <item>Range: <c>dx² + dz² ≥ range²</c> (along the ground from the eye) is out.</item>
/// <item>Fade: <c>w = clamp((range − sqrt(dx² + dz²)) × (1 / band), 0, 1)</c>.</item>
/// <item>Frustum: the precomputed sphere against each plane, <c>n·c + d &lt; −r |n|</c> with <c>|n|</c> precomputed per view, is out.</item>
/// <item>Packing: <c>M14 = w ≥ 0.999 ? 2 : w</c> (2 = fully visible, no dither).</item>
/// </list>
/// Visible instances keep their index order (a stable compaction).
/// </summary>
public static class FoliageCull
{
    /// <summary>Fills the bounding spheres from the mesh's bounds (centre and radius before scaling).</summary>
    public static void FillSpheres(Span<FoliageInstanceRecord> instances, Vector3 centre, float radius)
    {
        foreach (ref var r in instances)
        {
            var c = Vector3.Transform(centre, r.Transform);
            r.Sphere = new Vector4(c, radius * r.Ground.Z);
        }
    }

    /// <summary>A placement turns the winding round (<see cref="TerrainRenderer.DrawMeshes"/>' test, on the matrix it is given: M14 = 0).</summary>
    public static bool Mirrors(in Matrix4x4 transform) => transform.GetDeterminant() < 0;

    /// <summary>A TERRAIN-mode rock's <c>Ground.W</c> for the GPU cull (<see cref="FoliageShaders.CullCompute"/>): 1024 when the placement
    /// mirrors, plus its biome map row + 1 (<paramref name="biomeRow"/> -1: none; at most 1022). Whole numbers, exact in a float.</summary>
    public static float RockBits(in Matrix4x4 transform, int biomeRow) => (Mirrors(transform) ? 1024 : 0) + Math.Clamp(biomeRow, -1, 1022) + 1;

    /// <summary>The fade a TERRAIN-mode rock needs to be drawn at all: the terrain shader has no dither, so it goes at the middle of the fade.</summary>
    public const float RockThreshold = 0.5f;

    public static float Fade(in FoliageGroupRange range, float distance) => Math.Clamp((range.Range - distance) * range.InverseBand, 0, 1);

    /// <summary>The fade as the mesh shader reads it from row 0 w (<see cref="FoliageShaders.MeshVertex"/>).</summary>
    public static float Pack(float w) => w >= 0.999f ? 2 : w;

    public static Matrix4x4 Packed(in Matrix4x4 t, float w) { var m = t; m.M14 = Pack(w); return m; }

    public static bool SphereVisible(FoliageCullView view, Vector4 sphere)
    {
        var planes = view.Planes;
        var lengths = view.NormalLengths;
        for (int i = 0; i < planes.Length; i++)
        {
            var p = planes[i];
            if (p.X * sphere.X + p.Y * sphere.Y + p.Z * sphere.Z + p.W < -sphere.W * lengths[i]) return false;
        }
        return true;
    }

    /// <summary>Culls one group's instances for one view. With <paramref name="record"/>, every instance in range is also listed (index and fade) for later views that share the range (the shadow cascades).</summary>
    public static void CullGroup(ReadOnlySpan<FoliageInstanceRecord> instances, in FoliageGroupRange range, Vector2 eye, FoliageCullView view, bool record, FoliageCullOutput output)
    {
        output.Count = output.InRangeCount = 0;
        for (int i = 0; i < instances.Length; i++)
        {
            ref readonly var r = ref instances[i];
            float dx = r.Ground.X - eye.X, dz = r.Ground.Y - eye.Y;
            float d2 = dx * dx + dz * dz;
            if (d2 >= range.RangeSquared) continue;
            float w = Fade(range, MathF.Sqrt(d2));
            if (record)
            {
                if (output.InRangeCount == output.InRange.Length)
                {
                    Array.Resize(ref output.InRange, output.InRangeCount * 2);
                    Array.Resize(ref output.InRangeFade, output.InRangeCount * 2);
                }
                output.InRange[output.InRangeCount] = i;
                output.InRangeFade[output.InRangeCount++] = w;
            }
            if (!SphereVisible(view, r.Sphere)) continue;
            if (output.Count == output.Visible.Length) Array.Resize(ref output.Visible, output.Count * 2);
            output.Visible[output.Count++] = Packed(r.Transform, w);
        }
    }
}
