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
    /// <summary>x, z of the position (the range is measured along the ground), the scale, and the group's index within its zone.</summary>
    public Vector4 Ground;

    public const int Size = 96;
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
