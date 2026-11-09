using Meitou.Data.World;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Meitou.Game.Rendering.Foliage;
using Meitou.Game.Rendering.Frame;

namespace Meitou.Tests.Rendering;

/// <summary>
/// The foliage cull's CPU reference (step A1 of docs/renderer-native.md 5.6): the record layout a GPU cull reads, and how far each
/// reformulation may move a result against the per-frame code it replaced (reimplemented here as <see cref="Old"/>).
/// </summary>
public class FoliageCullTests
{
    /// <summary>
    /// The cull as it was before A1 (FoliageRenderer.CullZones, master cec04c8), for comparisons.
    /// </summary>
    static class Old
    {
        public static float Distance(Vector2 p, Vector2 eye) => Vector2.Distance(p, eye);
        public static float Fade(float range, float band, float d) => Math.Clamp((range - d) / band, 0, 1);
        public static bool SphereVisible(Vector4[] planes, Vector3 centre, float radius)
        {
            foreach (var p in planes)
                if (p.X * centre.X + p.Y * centre.Y + p.Z * centre.Z + p.W < -radius * MathF.Sqrt(p.X * p.X + p.Y * p.Y + p.Z * p.Z))
                    return false;
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
            var p = new Vector3(around.X + (float)(random.NextDouble() * 2 - 1) * spread, (float)(random.NextDouble() * 500),
                    around.Y + (float)(random.NextDouble() * 2 - 1) * spread);
            float scale = (float)(0.4 + random.NextDouble() * 2);
            var t = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromYawPitchRoll((float)random.NextDouble() * 6.28f, 0.1f,
                    0) * Matrix4x4.CreateTranslation(p);
            records[i] = new FoliageInstanceRecord { Transform = t, Ground = new Vector4(p.X, p.Z, scale, 0) };
        }
        return records;
    }
}
