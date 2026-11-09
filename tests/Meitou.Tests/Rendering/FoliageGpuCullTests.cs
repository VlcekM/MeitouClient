using Meitou.Data.World;
using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Game.Rendering.Foliage;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Gpu.Core;
using Silk.NET.Vulkan;

namespace Meitou.Tests.Rendering;

/// <summary>
/// The GPU foliage cull (step A2 of docs/renderer-native.md 5.6) against its CPU reference <see cref="FoliageCull"/> on synthetic data:
/// the same visible instances, in the same order, with the same fade bits, and the indirect arguments of each batch.
/// </summary>
public class FoliageGpuCullTests
{
    static VulkanDevice? TryCreate()
    {
        try
        {
            return VulkanDevice.Create(new VulkanDeviceOptions { Validation = true, SyncValidation = true });
        }
        catch (Exception e)
            when (e is VulkanException or DllNotFoundException or EntryPointNotFoundException or Silk.NET.Core.Loader.SymbolLoadingException)
        {
            return null;
        }
    }

    static void ExpectClean(VulkanDevice d) =>

        Assert.True(d.ValidationErrors == 0, "Validation errors:\n" + string.Join("\n", d.ValidationLog));

    sealed record SyntheticGroup(int Batch, FoliageInstanceRecord[] Records, FoliageGroupRange Range)
    {
        public ArenaRange Arena;
        public int Generation;
    }

    static Vector4[] Frustum(Vector3 eye, float yaw, float pitch)
    {
        var forward = new Vector3(MathF.Sin(yaw) * MathF.Cos(pitch), MathF.Sin(pitch), MathF.Cos(yaw) * MathF.Cos(pitch));
        var view = Matrix4x4.CreateLookAt(eye, eye + forward, Vector3.UnitY);
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(1.0f, 16 / 9f, 1, 40000);
        return WorldCamera.FrustumPlanes(view * projection);
    }

    /// <summary>Groups of instances spread around the eye, a third of them within a few ulps of their range (where the range test and the
    /// fade's root are decided in the last bit) and a fifth of the spheres moved onto a frustum plane's limit.</summary>
    static List<SyntheticGroup> Synthetic(Random random, Vector3 eye, Vector4[] planes, int groups, int batches)
    {
        var view = new FoliageCullView().Set(planes);
        var list = new List<SyntheticGroup>();
        var centre = new Vector3(0.25f, 3.5f, -0.75f);
        for (int g = 0; g < groups; g++)
        {
            int count = random.Next(1, 900);
            float range = (float)(random.NextDouble() * 20000 + 300), band = MathF.Max(10, range * 0.1f);
            if (g % 5 == 4) band = MathF.Min(band, range * 0.25f);
            var records = new FoliageInstanceRecord[count];
            for (int i = 0; i < count; i++)
            {
                float angle = (float)(random.NextDouble() * Math.PI * 2);
                float dist = i % 3 == 0 ? range * (1 + (float)(random.NextDouble() - 0.5) * 4e-7f) : range * (float)random.NextDouble() * 1.1f;
                var p = new Vector3(eye.X + MathF.Cos(angle) * dist, (float)(random.NextDouble() * 300), eye.Z + MathF.Sin(angle) * dist);
                float scale = (float)(0.5 + random.NextDouble() * 1.5);
                var t = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromYawPitchRoll((float)random.NextDouble() * 6.28f, 0.05f,
                        0) * Matrix4x4.CreateTranslation(p);
                records[i] = new FoliageInstanceRecord { Transform = t, Ground = new Vector4(p.X, p.Z, scale, g) };
            }
            FoliageCull.FillSpheres(records, centre, 4);
            for (int i = 1; i < count; i += 5)
            {
                // Onto plane k's limit: n·c + w = -r |n| (up to rounding), so the test is decided in the last bits.
                int k = random.Next(planes.Length);
                var n = new Vector3(planes[k].X, planes[k].Y, planes[k].Z);
                ref var s = ref records[i].Sphere;
                var c = new Vector3(s.X, s.Y, s.Z);
                float side = Vector3.Dot(n, c) + planes[k].W, target = -s.W * view.NormalLengths[k];
                c -= n * ((side - target) / n.LengthSquared());
                s = new Vector4(c, s.W);
            }
            list.Add(new SyntheticGroup(random.Next(batches), records, FoliageGroupRange.Of(range, band)));
        }
        return list;
    }

    /// <summary>
    /// One dispatch over plain batches (two draws each) and the impostor split (docs/impostors.md "Drawing"): every split group is culled
    /// twice, its mesh chunks (flag <see cref="FoliageCullChunk.ImpostorMesh"/>) in one batch and its impostor chunks
    /// (<see cref="FoliageCullChunk.Impostor"/>, the same instances) in another. Each batch's list against <see cref="FoliageCull.CullGroup"/>'s
    /// mesh and impostor outputs, bit for bit, and each draw's indirect arguments. A third of the split instances sit within a few ulps of the
    /// transition or of the band's start.
    /// </summary>
    [Fact]
    [Slow]
    public unsafe void Gpu_cull_matches_FoliageCull_on_synthetic_data()
    {
        using var d = TryCreate();

        Assert.SkipWhen(d is null, "No Vulkan 1.3 device");

        var random = new Random(21);
        using (var ctx = new GpuContext(d!))
        {
            using var cull = new FoliageGpuCull(ctx, arenaBytes: 1 << 20);   // small: the views below make it grow
            long totalVisible = 0, totalCandidates = 0, inBand = 0;
            for (int viewNumber = 0; viewNumber < 6; viewNumber++)
            {
                var eye = new Vector3(random.Next(-200000, 200000), 300 + random.Next(0, 2000), random.Next(-200000, 200000));
                var planes = Frustum(eye, (float)(random.NextDouble() * 6.28), (float)(random.NextDouble() * 0.8 - 0.6));
                if (viewNumber == 5) planes = planes[..4];   // a shadow cascade's frustum may have fewer planes
                const int plainBatches = 5, splitBatches = 4;
                var groups = Synthetic(random, eye, planes, 40, plainBatches);
                var splitGroups = Synthetic(random, eye, planes, 30, splitBatches);
                var centre = new Vector3(0.25f, 3.5f, -0.75f);
                for (int k = 0; k < splitGroups.Count; k++)
                {
                    var g = splitGroups[k];
                    float band = 1 / g.Range.InverseBand, t = (g.Range.Range - band) * (0.3f + 0.6f * (float)random.NextDouble()), b = t * 0.1f;
                    for (int i = 1; i < g.Records.Length; i += 3)
                    {
                        float at = (i % 2 == 0 ? t : t - b) * (1 + (float)(random.NextDouble() - 0.5) * 4e-7f),
                                angle = (float)(random.NextDouble() * Math.PI * 2);
                        ref var r = ref g.Records[i];
                        var p = new Vector3(eye.X + MathF.Cos(angle) * at, r.Transform.M42, eye.Z + MathF.Sin(angle) * at);
                        r.Transform.Translation = p;
                        r.Ground = r.Ground with { X = p.X, Y = p.Z };
                    }
                    FoliageCull.FillSpheres(g.Records, centre, 4);
                    splitGroups[k] = g with { Range = g.Range.WithTransition(t, b) };
                }
                var view = new FoliageCullView().Set(planes);
                var eyeXz = new Vector2(eye.X, eye.Z);

                ctx.BeginFrame();

                // Place every group (a grown arena moves them all: place again), then chunks grouped by batch, groups in order.
                bool placed;
                do
                {
                    placed = true;
                    foreach (var g in groups.Concat(splitGroups))
                        if (!cull.Place(ref g.Arena, ref g.Generation, g.Records))
                        {
                            placed = false;
                            break;
                        }
                } while (!placed);

                // Batches 0..4 the plain ones, then the split groups' meshes and then their impostors.
                var batches = new List<(List<SyntheticGroup> Groups, uint Flags, uint[] IndexCounts)>();
                for (int b = 0; b < plainBatches; b++)
                    batches.Add(([.. groups.Where(g => g.Batch == b)], 0, [(uint)(100 * b + 3), (uint)(100 * b + 4)]));   // a mesh and its leaves
                for (int b = 0; b < splitBatches; b++)
                    batches.Add(([.. splitGroups.Where(g => g.Batch == b)], FoliageCullChunk.ImpostorMesh, [(uint)(30 + b)]));
                for (int b = 0; b < splitBatches; b++)
                    batches.Add(([.. splitGroups.Where(g => g.Batch == b)], FoliageCullChunk.Impostor, [6]));
                var chunks = new List<FoliageCullChunk>();
                var draws = new List<FoliageCullDraw>();
                var starts = new int[batches.Count + 1];
                for (int b = 0; b < batches.Count; b++)
                {
                    starts[b] = chunks.Count;
                    foreach (var g in batches[b].Groups)
                        for (int at = 0; at < g.Records.Length; at += FoliageShaders.CullChunk)
                            chunks.Add(new FoliageCullChunk
                            {
                                First = FoliageGpuCull.FirstOf(g.Arena) + (uint)at,
                                        Count = (uint)Math.Min(FoliageShaders.CullChunk, g.Records.Length - at),
                                Range = g.Range.Range, RangeSquared = g.Range.RangeSquared, InverseBand = g.Range.InverseBand,
                                        Flags = batches[b].Flags,
                                Transition = batches[b].Flags == 0 ? 0 : g.Range.Transition,
                                        InverseTransitionBand = batches[b].Flags == 0 ? 0 : g.Range.InverseTransitionBand,
                            });
                    foreach (var indexCount in batches[b].IndexCounts)
                        draws.Add(new FoliageCullDraw { IndexCount = indexCount, ChunkStart = (uint)starts[b], ChunkEnd = (uint)chunks.Count });
                }
                starts[batches.Count] = chunks.Count;
                var work = cull.Prepare(chunks.ToArray(), draws.ToArray());
                var result = cull.Dispatch(work, view, eyeXz);
                using var readback = ReadbackBuffer.Create(ctx, FoliageGpuCull.ReadbackBytes(result), "cull readback");
                cull.CopyForReadback(result, readback);
                ctx.EndFrame();
                d!.Frames.WaitAll();

                // The CPU reference, batch by batch, group by group.
                var expected = new List<Matrix4x4>[batches.Count];
                for (int b = 0; b < batches.Count; b++) expected[b] = [];
                var output = new FoliageCullOutput();
                for (int b = 0; b < plainBatches; b++)
                    foreach (var g in batches[b].Groups)
                    {
                        FoliageCull.CullGroup(g.Records, g.Range, eyeXz, view, record: false, output);
                        expected[b].AddRange(output.Visible.AsSpan(0, output.Count));
                    }
                for (int b = 0; b < splitBatches; b++)
                    foreach (var g in batches[plainBatches + b].Groups)
                    {
                        FoliageCull.CullGroup(g.Records, g.Range, eyeXz, view, record: false, output,
                                FoliageCull.MeshPart | FoliageCull.ImpostorPart);
                        expected[plainBatches + b].AddRange(output.Visible.AsSpan(0, output.Count));
                        expected[plainBatches + splitBatches + b].AddRange(output.ImpostorVisible.AsSpan(0, output.ImpostorCount));
                        foreach (var m in output.ImpostorVisible.AsSpan(0, output.ImpostorCount)) if (m.M14 < 0) inBand++;
                    }
                int n = chunks.Count;
                var offsets = MemoryMarshal.Cast<byte, uint>(readback.Read(0, (ulong)(n + 1) * 4)).ToArray();
                ulong argsAt = FoliageGpuCull.Align16((ulong)(n + 1) * 4);
                var args = MemoryMarshal.Cast<byte, uint>(readback.Read(argsAt, (ulong)draws.Count * 20)).ToArray();
                ulong rowsAt = argsAt + FoliageGpuCull.Align16((ulong)draws.Count * 20);
                var rows = MemoryMarshal.Cast<byte, Matrix4x4>(readback.Read(rowsAt, (ulong)offsets[n] * 64)).ToArray();

                Assert.Equal(expected.Sum(e => e.Count), (int)offsets[n]);

                int draw = 0;
                for (int b = 0; b < batches.Count; b++)
                {
                    uint first = offsets[starts[b]], count = offsets[starts[b + 1]] - first;

                    Assert.Equal(expected[b].Count, (int)count);

                    for (int j = 0; j < count; j++)
                    {
                        var e = expected[b][j];
                        var got = rows[first + j];

                        // Bit for bit, the fade (row 0 w) included.
                        Assert.True(MemoryMarshal.AsBytes(new ReadOnlySpan<Matrix4x4>(in e))
                                .SequenceEqual(MemoryMarshal.AsBytes(new ReadOnlySpan<Matrix4x4>(in got))),

                            $"view {viewNumber}, batch {b}, instance {j}: GPU fade {got.M14:R}, CPU {e.M14:R}; translation {got.Translation} against {e.Translation}");
                    }
                    foreach (var indexCount in batches[b].IndexCounts)
                    {
                        Assert.Equal([indexCount, count, 0u, 0u, first], args[(draw * 5)..(draw * 5 + 5)]);

                        draw++;
                    }
                }
                totalVisible += expected.Take(plainBatches).Sum(e => e.Count);
                totalCandidates += groups.Sum(g => g.Records.Length);
            }

            Assert.True(cull.Grows > 0, "the arena should have grown");
            Assert.True(totalVisible > 1000 && totalVisible < totalCandidates / 2,
                    $"{totalVisible} of {totalCandidates} visible: the views should cut the sets");
            Assert.True(inBand > 100, $"{inBand} instances in the crossfade band: the band should be exercised");
        }
        ExpectClean(d!);
    }

    /// <summary>
    /// The occlusion cull in the kernel (<c>HizOccluded</c>) against the CPU rule of <see cref="OcclusionCullTests"/>: a pyramid drawn from a
    /// synthetic scene (spheres and a ground plane), the instances behind it in random places and the eye moved by 0 to 40 units; the instances the
    /// kernel keeps are the ones the rule does not hide, and the count it left out (the offsets' third extra entry) equals the rule's.
    /// </summary>
    [Fact]
    [Slow]
    public unsafe void Gpu_cull_leaves_out_what_the_depth_pyramid_hides_as_the_rule_does()
    {
        using var d = TryCreate();

        Assert.SkipWhen(d is null, "No Vulkan 1.3 device");

        var random = new Random(5);
        long hiddenTotal = 0, keptTotal = 0;
        using (var ctx = new GpuContext(d!))
        {
            using var cull = new FoliageGpuCull(ctx, arenaBytes: 4 << 20);
            for (int viewNumber = 0; viewNumber < 8; viewNumber++)
            {
                var scene = new List<OcclusionCullTests.Occluder>();
                for (int i = random.Next(2, 9); i > 0; i--)
                    scene.Add(new OcclusionCullTests.Occluder(new Vector3(random.Next(-600, 600), random.Next(20, 400), random.Next(150, 1500)),
                            random.Next(40, 500)));
                var eye = new Vector3(random.Next(-5000, 5000), random.Next(20, 120), random.Next(-5000, 5000));
                var local = OcclusionCullTests.Camera.Look(Vector3.Zero, (float)(random.NextDouble() * 0.2 - 0.1),
                        (float)(random.NextDouble() * 0.2 - 0.1));
                var cam = local with { Eye = eye };
                const int w = OcclusionCullTests.W, h = OcclusionCullTests.H;
                var z = new float[h, w];

                // The scene lies round the origin; the camera is offset to a far position by moving the scene with it.
                var moved = scene.Select(o => o with { Centre = o.Centre + eye }).ToList();
                float jx = (float)random.NextDouble() - 0.5f, jy = (float)random.NextDouble() - 0.5f;
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                        z[y, x] = OcclusionCullTests.DepthAt(moved, cam, x, y, jx, jy);
                var pyramid = new OcclusionCullTests.Pyramid(z);
                var sizes = pyramid.Sizes;
                var offsets = new uint[HizPyramid.MaxLevels];
                uint at = 0;
                var data = new List<float>();
                for (int l = 0; l < sizes.Count; l++)
                {
                    offsets[l] = at;
                    at += (uint)(sizes[l].W * sizes[l].H);
                    foreach (var (least, most) in pyramid.Levels[l]) { data.Add(least); data.Add(most); }
                }
                float delta = new[] { 0f, 0.5f, 3f, 12f, 40f }[random.Next(5)];
                var current = eye + OcclusionCullTests.RandomUnit(random) * delta;

                // Instances: behind the blockers mostly (in the camera's frame), some anywhere.
                var records = new List<FoliageInstanceRecord>();
                var spheres = new List<Vector4>();
                for (int i = 0; i < 700; i++)
                {
                    var c = eye + cam.Right * random.Next(-900, 900) + cam.Up * random.Next(-40, 500) - cam.Back * random.Next(60, 5000);
                    float r = random.Next(2, 80);
                    var t = Matrix4x4.CreateTranslation(c);
                    records.Add(new FoliageInstanceRecord { Transform = t, Ground = new Vector4(c.X, c.Z, 1, 0) });
                    spheres.Add(new Vector4(c, r));
                }
                var recordArray = records.ToArray();
                for (int i = 0; i < recordArray.Length; i++) recordArray[i].Sphere = spheres[i];
                var group = new SyntheticGroup(0, recordArray, FoliageGroupRange.Of(1e6f, 1e5f));

                ctx.BeginFrame();
                while (!cull.Place(ref group.Arena, ref group.Generation, group.Records)) { }
                var chunks = new List<FoliageCullChunk>();
                for (int at2 = 0; at2 < group.Records.Length; at2 += FoliageShaders.CullChunk)
                    chunks.Add(new FoliageCullChunk
                    {
                        First = FoliageGpuCull.FirstOf(group.Arena) + (uint)at2,
                                Count = (uint)Math.Min(FoliageShaders.CullChunk, group.Records.Length - at2),
                        Range = group.Range.Range, RangeSquared = group.Range.RangeSquared, InverseBand = group.Range.InverseBand,
                    });
                var draws = new[] { new FoliageCullDraw { IndexCount = 3, ChunkStart = 0, ChunkEnd = (uint)chunks.Count } };
                var work = cull.Prepare(chunks.ToArray(), draws);
                using var pyramidBuffer = DeviceBuffer.Create(ctx, (ulong)data.Count * 4, BufferUse.Storage | BufferUse.TransferDst, "pyramid test");
                ctx.Uploads.Write(pyramidBuffer, 0, MemoryMarshal.AsBytes(data.ToArray().AsSpan()));
                var vectors = HizPyramid.Vectors(eye, Vector3.Distance(current, eye), cam.Right, cam.Up, cam.Back, OcclusionCullTests.TanX,
                        OcclusionCullTests.TanY, w, h, sizes.Count, sizes[0].W, sizes[0].H, offsets);
                var occlusion = new OcclusionView(new GpuBufferBinding(pyramidBuffer.Handle, 0, pyramidBuffer.Size), vectors);
                var view = new FoliageCullView().Set([]);
                var result = cull.Dispatch(work, view, new Vector2(current.X, current.Z), default, 0.999f, default, occlusion);
                int n = chunks.Count;
                using var readback = ReadbackBuffer.Create(ctx, FoliageGpuCull.ReadbackBytes(result), "cull readback");
                cull.CopyForReadback(result, readback);
                using var total = ReadbackBuffer.Create(ctx, 12, "occlusion total");
                ctx.Frame.PreFrame.CopyBuffer(result.Offsets, total.Handle, new GpuBufferCopy(result.OffsetsOffset + (ulong)n * 4, 0, 12));
                ctx.EndFrame();
                d!.Frames.WaitAll();

                var expected = new HashSet<Vector3>();
                int expectedHidden = 0;
                for (int i = 0; i < recordArray.Length; i++)
                {
                    var s = spheres[i];
                    if (pyramid.Occluded(cam, new Vector3(s.X, s.Y, s.Z), s.W, Vector3.Distance(current, eye)))
                        expectedHidden++;
                    else expected.Add(new Vector3(s.X, s.Y, s.Z));
                }
                var counts = MemoryMarshal.Cast<byte, uint>(readback.Read(0, (ulong)(n + 1) * 4)).ToArray();
                var tail = MemoryMarshal.Cast<byte, uint>(total.Read(0, 12)).ToArray();
                ulong rowsAt = FoliageGpuCull.Align16((ulong)(n + 1) * 4) + FoliageGpuCull.Align16(20);
                var rows = MemoryMarshal.Cast<byte, Matrix4x4>(readback.Read(rowsAt, (ulong)counts[n] * 64)).ToArray();

                Assert.Equal(expected.Count, (int)counts[n]);
                Assert.Equal(expectedHidden, (int)tail[2]);

                foreach (var row in rows) Assert.Contains(row.Translation, expected);
                hiddenTotal += expectedHidden;
                keptTotal += expected.Count;
            }
        }

        Assert.True(hiddenTotal > 100 && keptTotal > 100, $"{hiddenTotal} hidden, {keptTotal} kept: the views should have both");

        ExpectClean(d!);
    }
}
