using Meitou.Data.World;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Meitou.Rendering;

namespace Meitou.Tests.Rendering;

/// <summary>
/// The foliage cull's CPU reference (step A1 of docs/renderer-native.md 5.6): the record layout a GPU cull reads, and how far each
/// reformulation may move a result against the per-frame code it replaced (reimplemented here as <see cref="Old"/>).
/// </summary>
public class FoliageCullTests
{
    /// <summary>The cull as it was before A1 (FoliageRenderer.CullZones, master cec04c8), for comparisons.</summary>
    static class Old
    {
        public static float Distance(Vector2 p, Vector2 eye) => Vector2.Distance(p, eye);
        public static float Fade(float range, float band, float d) => Math.Clamp((range - d) / band, 0, 1);
        public static bool SphereVisible(Vector4[] planes, Vector3 centre, float radius)
        {
            foreach (var p in planes)
                if (p.X * centre.X + p.Y * centre.Y + p.Z * centre.Z + p.W < -radius * MathF.Sqrt(p.X * p.X + p.Y * p.Y + p.Z * p.Z)) return false;
            return true;
        }
    }

    static int UlpDistance(float a, float b)
    {
        int ia = BitConverter.SingleToInt32Bits(a), ib = BitConverter.SingleToInt32Bits(b);
        if (ia < 0) ia = int.MinValue - ia;
        if (ib < 0) ib = int.MinValue - ib;
        return Math.Abs(ia - ib);
    }

    static Vector4[] Frustum(Vector3 eye, float yaw, float pitch)
    {
        var forward = new Vector3(MathF.Sin(yaw) * MathF.Cos(pitch), MathF.Sin(pitch), MathF.Cos(yaw) * MathF.Cos(pitch));
        var view = Matrix4x4.CreateLookAt(eye, eye + forward, Vector3.UnitY);
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(1.0f, 16 / 9f, 1, 40000);
        return WorldCamera.FrustumPlanes(view * projection);
    }

    [Fact]
    public void Instance_record_is_std430_96_bytes()
    {
        Assert.Equal(FoliageInstanceRecord.Size, Marshal.SizeOf<FoliageInstanceRecord>());
        Assert.Equal(96, Unsafe.SizeOf<FoliageInstanceRecord>());
        Assert.Equal(0, (int)Marshal.OffsetOf<FoliageInstanceRecord>(nameof(FoliageInstanceRecord.Transform)));
        Assert.Equal(64, (int)Marshal.OffsetOf<FoliageInstanceRecord>(nameof(FoliageInstanceRecord.Sphere)));
        Assert.Equal(80, (int)Marshal.OffsetOf<FoliageInstanceRecord>(nameof(FoliageInstanceRecord.Ground)));
    }

    [Fact]
    public void Arena_packing_is_68_bytes_and_lossless_for_placements()
    {
        Assert.Equal(68, FoliageInstanceRecord.GpuSize);
        var rng = new Random(7);
        var packed = new float[17];
        for (int i = 0; i < 1000; i++)
        {
            var t = Matrix4x4.CreateScale(0.5f + (float)rng.NextDouble()) * Matrix4x4.CreateFromYawPitchRoll((float)rng.NextDouble() * 6, (float)rng.NextDouble(), (float)rng.NextDouble())
                    * Matrix4x4.CreateTranslation(rng.Next(-90000, 90000) + (float)rng.NextDouble(), (float)rng.NextDouble() * 900, rng.Next(-90000, 90000) + (float)rng.NextDouble());
            var r = new FoliageInstanceRecord { Transform = t, Sphere = new Vector4(t.M41, t.M42, t.M43, 3), Ground = new Vector4(t.M41, t.M43, 1, i % 1024) };
            Assert.True(FoliageInstanceRecord.Pack(in r, packed));
            Assert.Equal(t.M11, packed[0]); Assert.Equal(t.M33, packed[8]); Assert.Equal(t.M41, packed[9]); Assert.Equal(t.M43, packed[11]);
            Assert.Equal(r.Ground.W, packed[16]);
        }
        // A fourth column that is not 0, 0, 0, 1, or a ground position that is not the translation, is reported.
        var odd = new FoliageInstanceRecord { Transform = Matrix4x4.Identity, Ground = new Vector4(1, 0, 1, 0) };
        Assert.False(FoliageInstanceRecord.Pack(in odd, packed));
    }

    [Fact]
    [Slow]
    public void Scalar_ground_distance_is_bit_identical_to_Vector2_Distance()
    {
        var random = new Random(1);
        for (int i = 0; i < 2_000_000; i++)
        {
            var p = new Vector2((float)(random.NextDouble() * 900000 - 450000), (float)(random.NextDouble() * 900000 - 450000));
            var eye = p + new Vector2((float)(random.NextDouble() * 80000 - 40000), (float)(random.NextDouble() * 80000 - 40000)) * (float)random.NextDouble();
            float dx = p.X - eye.X, dz = p.Y - eye.Y;
            Assert.Equal(BitConverter.SingleToInt32Bits(Old.Distance(p, eye)), BitConverter.SingleToInt32Bits(MathF.Sqrt(dx * dx + dz * dz)));
        }
    }

    [Fact]
    [Slow]
    public void Squared_range_test_differs_only_within_an_ulp_of_the_range()
    {
        var random = new Random(2);
        int flips = 0;
        for (int i = 0; i < 2_000_000; i++)
        {
            float range = (float)(random.NextDouble() * 40000 + 100);
            // Points on and near the range circle, where a flip can happen at all.
            float angle = (float)(random.NextDouble() * Math.PI * 2);
            float d = range * (1 + (float)(random.NextDouble() - 0.5) * 1e-6f);
            var eye = new Vector2((float)(random.NextDouble() * 600000 - 300000), (float)(random.NextDouble() * 600000 - 300000));
            var p = eye + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * d;
            float dx = p.X - eye.X, dz = p.Y - eye.Y;
            bool oldOut = Old.Distance(p, eye) >= range;
            bool newOut = dx * dx + dz * dz >= FoliageGroupRange.Of(range, 1).RangeSquared;
            if (oldOut == newOut) continue;
            flips++;
            Assert.True(UlpDistance(Old.Distance(p, eye), range) <= 1, $"flip at {UlpDistance(Old.Distance(p, eye), range)} ulp from the range");
        }
        Assert.True(flips > 0 && flips < 2_000_000 / 10, $"{flips} flips");   // the points sit within a few ulp of the range: a few percent flip, each by one ulp
    }

    [Fact]
    [Slow]
    public void Reciprocal_band_fade_is_within_one_ulp_of_the_division()
    {
        var random = new Random(3);
        int differ = 0;
        for (int i = 0; i < 2_000_000; i++)
        {
            float range = (float)(random.NextDouble() * 40000 + 100);
            float band = MathF.Max(10, range * 0.1f);
            if (random.Next(4) == 0) band = MathF.Min(band, range * 0.25f);
            float d = range - band * (float)random.NextDouble() * 1.2f;
            float a = Old.Fade(range, band, d), b = FoliageCull.Fade(FoliageGroupRange.Of(range, band), d);
            if (a != b) differ++;
            Assert.True(UlpDistance(a, b) <= 1, $"fade {a} against {b}");
        }
        Assert.True(differ > 0, "the reciprocal is expected to round differently sometimes");
    }

    [Fact]
    public void Fade_packing_marks_full_visibility_with_2()
    {
        Assert.Equal(2, FoliageCull.Pack(1));
        Assert.Equal(2, FoliageCull.Pack(0.999f));
        Assert.Equal(0.9989f, FoliageCull.Pack(0.9989f));
        Assert.Equal(0, FoliageCull.Pack(0));
        var m = FoliageCull.Packed(Matrix4x4.CreateTranslation(1, 2, 3), 0.5f);
        Assert.Equal(0.5f, m.M14);
        Assert.Equal(new Vector3(1, 2, 3), m.Translation);
    }

    [Fact]
    [Slow]
    public void Precomputed_spheres_and_plane_lengths_reproduce_the_per_frame_test_exactly()
    {
        var random = new Random(4);
        var centre = new Vector3(3.5f, 120.25f, -7);
        const float radius = 151.7f;
        var records = RandomRecords(random, 20000, new Vector2(-50000, -12000), 6000);
        FoliageCull.FillSpheres(records, centre, radius);
        var view = new FoliageCullView();
        for (int f = 0; f < 20; f++)
        {
            var eye = new Vector3(-50000 + random.Next(-3000, 3000), 400 + random.Next(0, 3000), -12000 + random.Next(-3000, 3000));
            var planes = Frustum(eye, (float)(random.NextDouble() * 6.28), (float)(random.NextDouble() - 0.7));
            view.Set(planes);
            foreach (var r in records)
            {
                var c = Vector3.Transform(centre, r.Transform);   // what the cull computed every frame before
                float rad = radius * r.Ground.Z;
                Assert.Equal(c, new Vector3(r.Sphere.X, r.Sphere.Y, r.Sphere.Z));
                Assert.Equal(BitConverter.SingleToInt32Bits(rad), BitConverter.SingleToInt32Bits(r.Sphere.W));
                Assert.Equal(Old.SphereVisible(planes, c, rad), FoliageCull.SphereVisible(view, r.Sphere));
            }
        }
    }

    [Fact]
    [Slow]
    public void Cull_keeps_index_order_and_matches_the_old_decisions()
    {
        var random = new Random(5);
        var centre = new Vector3(0, 80, 0);
        const float radius = 90;
        var records = RandomRecords(random, 30000, new Vector2(1000, 2000), 9000);
        FoliageCull.FillSpheres(records, centre, radius);
        var output = new FoliageCullOutput();
        var view = new FoliageCullView();
        for (int f = 0; f < 10; f++)
        {
            var eye = new Vector3(1000 + random.Next(-2000, 2000), 600, 2000 + random.Next(-2000, 2000));
            var planes = Frustum(eye, (float)(random.NextDouble() * 6.28), -0.2f);
            float range = 4000 + f * 300, band = range < 5000 ? Math.Min(range * 0.1f, range * 0.25f) : range * 0.1f;
            var groupRange = FoliageGroupRange.Of(range, band);
            FoliageCull.CullGroup(records, groupRange, new Vector2(eye.X, eye.Z), view.Set(planes), record: true, output);
            // The old loop, instance by instance, in order.
            int visible = 0, inRange = 0;
            for (int i = 0; i < records.Length; i++)
            {
                var r = records[i];
                float d = Old.Distance(new Vector2(r.Ground.X, r.Ground.Y), new Vector2(eye.X, eye.Z));
                if (d >= range) continue;
                float w = Old.Fade(range, band, d);
                Assert.Equal(i, output.InRange[inRange]);
                Assert.True(UlpDistance(w, output.InRangeFade[inRange]) <= 1);
                inRange++;
                var c = Vector3.Transform(centre, r.Transform);
                if (!Old.SphereVisible(planes, c, radius * r.Ground.Z)) continue;
                var expected = r.Transform;
                var got = output.Visible[visible++];
                Assert.Equal(expected.Translation, got.Translation);
                Assert.True(UlpDistance(w >= 0.999f ? 2 : w, got.M14) <= 1);
                got.M14 = 0;
                Assert.Equal(expected, got);
            }
            Assert.Equal(inRange, output.InRangeCount);
            Assert.Equal(visible, output.Count);
            Assert.True(visible > 0 && visible < inRange, $"the view should cut the set: {visible} of {inRange}");
        }
    }

    /// <summary>
    /// The impostor split (docs/impostors.md "Drawing"): with a transition T and band B, an instance nearer than T − B is a mesh only, one
    /// from T on an impostor only (both with the range fade), and one in [T − B, T) both, the mesh with the transition fade m and the impostor
    /// with −m (the complementary dither); the same set as without a transition, and a part not asked for is never emitted.
    /// </summary>
    [Fact]
    public void Impostor_split_is_complementary()
    {
        var random = new Random(7);
        var eye = new Vector2(1000, -2000);
        var all = new FoliageCullView().Set([]);
        var output = new FoliageCullOutput();
        for (int round = 0; round < 20; round++)
        {
            float range = 2000 + (float)random.NextDouble() * 10000, band = range * 0.1f;
            float t = (range - band) * (0.3f + 0.7f * (float)random.NextDouble()), b = t * 0.1f;
            var plain = FoliageGroupRange.Of(range, band);
            var split = plain.WithTransition(t, b);
            var records = RandomRecords(random, 3000, eye, range * 1.1f);
            FoliageCull.CullGroup(records, plain, eye, all, record: false, output);
            var whole = output.Visible.AsSpan(0, output.Count).ToArray();
            FoliageCull.CullGroup(records, split, eye, all, record: true, output, FoliageCull.MeshPart | FoliageCull.ImpostorPart);
            var meshes = output.Visible.AsSpan(0, output.Count).ToArray();
            var impostors = output.ImpostorVisible.AsSpan(0, output.ImpostorCount).ToArray();
            int mi = 0, ii = 0, both = 0;
            foreach (var w in whole)
            {
                float d = Vector2.Distance(new Vector2(w.M41, w.M43), eye);
                bool mesh = mi < meshes.Length && meshes[mi].Translation == w.Translation;
                bool impostor = ii < impostors.Length && impostors[ii].Translation == w.Translation;
                Assert.True(mesh || impostor, $"an instance at {d} is in neither part");
                if (mesh && impostor)
                {
                    both++;
                    Assert.True(meshes[mi].M14 > 0 && meshes[mi].M14 < 1, $"in the band at {d}: mesh fade {meshes[mi].M14}");
                    Assert.Equal(-meshes[mi].M14, impostors[ii].M14);
                }
                else if (mesh) Assert.Equal(w.M14, meshes[mi].M14);
                else Assert.Equal(w.M14, impostors[ii].M14);
                if (d < t - b - 0.01f) Assert.False(impostor, $"an impostor at {d}, before the band {t - b}");
                if (d > t + 0.01f) Assert.False(mesh, $"a mesh at {d}, beyond the transition {t}");
                if (mesh) mi++;
                if (impostor) ii++;
            }
            Assert.Equal(meshes.Length, mi);
            Assert.Equal(impostors.Length, ii);
            Assert.True(both > 0, "some instances should be in the band");
            // One part only: the other is not emitted, the asked one unchanged.
            FoliageCull.CullGroup(records, split, eye, all, record: false, output, FoliageCull.ImpostorPart);
            Assert.Equal(0, output.Count);
            Assert.Equal(impostors.Length, output.ImpostorCount);
        }
    }

    static FoliageInstanceRecord[] RandomRecords(Random random, int count, Vector2 around, float spread)
    {
        var records = new FoliageInstanceRecord[count];
        for (int i = 0; i < count; i++)
        {
            var p = new Vector3(around.X + (float)(random.NextDouble() * 2 - 1) * spread, (float)(random.NextDouble() * 500), around.Y + (float)(random.NextDouble() * 2 - 1) * spread);
            float scale = (float)(0.4 + random.NextDouble() * 2);
            var t = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromYawPitchRoll((float)random.NextDouble() * 6.28f, 0.1f, 0) * Matrix4x4.CreateTranslation(p);
            records[i] = new FoliageInstanceRecord { Transform = t, Ground = new Vector4(p.X, p.Z, scale, 0) };
        }
        return records;
    }
}
