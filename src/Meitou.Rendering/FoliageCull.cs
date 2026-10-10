using Meitou.Data.World;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Meitou.Rendering;

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

/// <summary>
/// A group's (one mesh of one layer in one zone) range for one view: where instances end, and the fade band as a reciprocal. With an impostor
/// (docs/impostors.md "Drawing"): <see cref="Transition"/>, the ground distance from which the instance is its impostor, and the crossfade band
/// before it as a reciprocal (<see cref="FoliageCull.TransitionFade"/>); infinite without one.
/// </summary>
public readonly record struct FoliageGroupRange(float Range, float RangeSquared, float InverseBand, float Transition = float.PositiveInfinity, float InverseTransitionBand = 0)
{
    public static FoliageGroupRange Of(float range, float band) => new(range, range * range, 1 / band);

    /// <summary>This range with an impostor from <paramref name="transition"/> on, crossfaded over <paramref name="band"/> before it.</summary>
    public FoliageGroupRange WithTransition(float transition, float band) => this with { Transition = transition, InverseTransitionBand = 1 / band };

    public bool HasImpostor => Transition < float.PositiveInfinity;
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
    /// <summary>The visible impostors (<see cref="FoliageCull.CullGroup"/> with <see cref="FoliageCull.ImpostorPart"/>), fade in <c>M14</c>
    /// (negative in the crossfade band: the complement of the mesh's dither).</summary>
    public Matrix4x4[] ImpostorVisible = new Matrix4x4[64];
    public int ImpostorCount;
    /// <summary>Instances the fog cull left out (<see cref="FoliageCull.CullGroup"/> with a fog).</summary>
    public int FogCulled;
    /// <summary>With record: each in-range instance's packed mesh and impostor values (<see cref="FoliageCull.Hidden"/> where that part is not drawn).</summary>
    public float[] InRangeMesh = new float[64], InRangeImpostor = new float[64];
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

    /// <summary>The parts of a group a view draws (<see cref="CullGroup"/>): its meshes, its impostors (both for a group straddling the transition).</summary>
    public const int MeshPart = 1, ImpostorPart = 2;

    /// <summary>The packed value of a part not drawn (the kernels' sentinel: visible values are above −1.5).</summary>
    public const float Hidden = -2;

    /// <summary>The share of the mesh at <paramref name="distance"/>: 1 before the crossfade band, 0 from the transition on.</summary>
    public static float TransitionFade(in FoliageGroupRange range, float distance) => Math.Clamp((range.Transition - distance) * range.InverseTransitionBand, 0, 1);

    /// <summary>The mesh's packed value with an impostor: <paramref name="m"/> in the band (the mesh's dither keeps that share), else the range fade's.</summary>
    public static float PackMesh(float m, float w) => m > 0 ? m < 1 ? m : Pack(w) : Hidden;

    /// <summary>The impostor's packed value: −<paramref name="m"/> in the band (the complement of the mesh's dither), else the range fade's.</summary>
    public static float PackImpostor(float m, float w) => m < 1 ? m > 0 ? -m : Pack(w) : Hidden;

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

    /// <summary>Culls one group's instances for one view. With <paramref name="record"/>, every instance in range is also listed (index and fade) for later views that share the range (the shadow cascades).
    /// <paramref name="parts"/>: <see cref="MeshPart"/> and / or <see cref="ImpostorPart"/>; with a transition (<see cref="FoliageGroupRange.HasImpostor"/>) the
    /// meshes keep only the instances before it (<see cref="PackMesh"/>) and the impostors only those from its band on (<see cref="PackImpostor"/>).</summary>
    internal static void CullGroup(ReadOnlySpan<FoliageInstanceRecord> instances, in FoliageGroupRange range, Vector2 eye, FoliageCullView view, bool record, FoliageCullOutput output, int parts = MeshPart, FogVolumes? fog = null)
    {
        output.Count = output.InRangeCount = output.ImpostorCount = output.FogCulled = 0;
        bool split = range.HasImpostor;
        for (int i = 0; i < instances.Length; i++)
        {
            ref readonly var r = ref instances[i];
            float dx = r.Ground.X - eye.X, dz = r.Ground.Y - eye.Y;
            float d2 = dx * dx + dz * dz;
            if (d2 >= range.RangeSquared) continue;
            float d = MathF.Sqrt(d2);
            float w = Fade(range, d);
            float mesh = Pack(w), impostor = Hidden;
            if (split)
            {
                float m = TransitionFade(range, d);
                mesh = (parts & MeshPart) != 0 ? PackMesh(m, w) : Hidden;
                impostor = (parts & ImpostorPart) != 0 ? PackImpostor(m, w) : Hidden;
            }
            if (record)
            {
                if (output.InRangeCount == output.InRange.Length)
                {
                    Array.Resize(ref output.InRange, output.InRangeCount * 2);
                    Array.Resize(ref output.InRangeFade, output.InRangeCount * 2);
                    Array.Resize(ref output.InRangeMesh, output.InRangeCount * 2);
                    Array.Resize(ref output.InRangeImpostor, output.InRangeCount * 2);
                }
                output.InRange[output.InRangeCount] = i;
                output.InRangeMesh[output.InRangeCount] = mesh;
                output.InRangeImpostor[output.InRangeCount] = impostor;
                output.InRangeFade[output.InRangeCount++] = w;
            }
            if (!SphereVisible(view, r.Sphere)) continue;
            // The fog cull (the main colour pass only): the sphere's box wholly inside the eye's drawn-last fog block and beyond the hide distance.
            if (fog is not null && (mesh > Hidden || impostor > Hidden))
            {
                var s = r.Sphere;
                if (fog.Covers(new Vector3(s.X - s.W, s.Y - s.W, s.Z - s.W), new Vector3(s.X + s.W, s.Y + s.W, s.Z + s.W))) { output.FogCulled++; continue; }
            }
            if (mesh > Hidden)
            {
                if (output.Count == output.Visible.Length) Array.Resize(ref output.Visible, output.Count * 2);
                var t = r.Transform;
                t.M14 = mesh;
                output.Visible[output.Count++] = t;
            }
            if (impostor > Hidden)
            {
                if (output.ImpostorCount == output.ImpostorVisible.Length) Array.Resize(ref output.ImpostorVisible, output.ImpostorCount * 2);
                var t = r.Transform;
                t.M14 = impostor;
                output.ImpostorVisible[output.ImpostorCount++] = t;
            }
        }
    }
}
