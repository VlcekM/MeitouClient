using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Rendering;
using Meitou.Rendering.Gpu;
using Meitou.Rendering.Vulkan;
using Meitou.Rendering.Vulkan.Core;
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
        catch (Exception e) when (e is VulkanException or DllNotFoundException or EntryPointNotFoundException or Silk.NET.Core.Loader.SymbolLoadingException)
        {
            return null;
        }
    }

    static void ExpectClean(VulkanDevice d) =>
        Assert.True(d.ValidationErrors == 0, "Validation errors:\n" + string.Join("\n", d.ValidationLog));

    const string SqrtCompute = """
        #version 450
        layout(local_size_x = 256) in;
        layout(std430, set = 0, binding = 0) readonly buffer In { float values[]; };
        layout(std430, set = 0, binding = 1) writeonly buffer Out { float roots[]; };
        layout(push_constant) uniform Push { uint count; } pc;
        """ + FoliageShaders.CrSqrt + """
        void main()
        {
            uint i = gl_GlobalInvocationID.x;
            if (i >= pc.count) return;
            roots[2u * i] = sqrt(values[i]);
            roots[2u * i + 1u] = CrSqrt(values[i]);
        }
        """;

    /// <summary>Squared ground distances as the cull meets them: random magnitudes up to (60000 units)², exact squares and their
    /// neighbours (the hard cases of rounding a root), powers of two and their neighbours, 0.</summary>
    static float[] SqrtInputs(int count)
    {
        var random = new Random(11);
        var v = new float[count];
        for (int i = 0; i < count; i++)
        {
            switch (i % 4)
            {
                case 0: v[i] = (float)Math.Pow(10, random.NextDouble() * 9.6); break;
                case 1:
                {
                    float r = (float)(random.NextDouble() * 60000);
                    float s = r * r;
                    int bits = BitConverter.SingleToInt32Bits(s) + random.Next(-2, 3);
                    v[i] = BitConverter.Int32BitsToSingle(bits);
                    break;
                }
                case 2:
                {
                    // Midpoint squares: (r + ulp/2)² rounded, a step either side.
                    float r = (float)(random.NextDouble() * 60000 + 1);
                    double mid = r + (MathF.BitIncrement(r) - (double)r) / 2;
                    int bits = BitConverter.SingleToInt32Bits((float)(mid * mid)) + random.Next(-1, 2);
                    v[i] = BitConverter.Int32BitsToSingle(bits);
                    break;
                }
                default:
                {
                    int bits = BitConverter.SingleToInt32Bits(MathF.Pow(2, random.Next(-20, 32))) + random.Next(-2, 3);
                    v[i] = BitConverter.Int32BitsToSingle(bits);
                    break;
                }
            }
        }
        v[0] = 0;
        return v;
    }

    [Fact]
    [Slow]
    public unsafe void CrSqrt_is_the_correctly_rounded_root()
    {
        using var d = TryCreate();
        Assert.SkipWhen(d is null, "No Vulkan 1.3 device");
        const int N = 1 << 20;
        var inputs = SqrtInputs(N);
        int raw = 0, rawFar = 0;
        using (var gl = new VkGl(d!))
        {
            var ctx = gl.Context;
            using var program = ctx.Shaders.Compute(SqrtCompute, "sqrt");
            var pipe = ctx.Pipelines.Get(new ComputePipelineDesc(program, "sqrt"));
            using var input = DeviceBuffer.Create(ctx, N * 4, BufferUse.Storage | BufferUse.TransferDst, "sqrt in");
            using var output = DeviceBuffer.Create(ctx, N * 8, BufferUse.Storage | BufferUse.TransferSrc, "sqrt out");
            using var readback = ReadbackBuffer.Create(ctx, N * 8, "sqrt readback");
            gl.BeginFrame(4, 4);
            ctx.Uploads.Write(input, 0, MemoryMarshal.AsBytes(inputs.AsSpan()));
            var cmd = ctx.Frame.PreFrame;
            cmd.Barrier(Full());
            cmd.BindPipeline(pipe);
            var infos = stackalloc DescriptorBufferInfo[] { new(input.Handle, 0, N * 4), new(output.Handle, 0, N * 8) };
            var writes = new WriteDescriptorSet[2];
            for (int i = 0; i < 2; i++)
                writes[i] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstBinding = (uint)i, DescriptorCount = 1, DescriptorType = DescriptorType.StorageBuffer, PBufferInfo = &infos[i] };
            cmd.PushDescriptors(program.Layout, 0, writes, PipelineBindPoint.Compute);
            cmd.PushConstants(program.Layout, ShaderStageFlags.ComputeBit, (uint)N);
            cmd.Dispatch(N / 256);
            cmd.Barrier(Full());
            cmd.CopyBuffer(output.Handle, readback.Handle, new BufferCopy(0, 0, N * 8));
            gl.EndFrame();
            d!.Frames.WaitAll();
            var roots = MemoryMarshal.Cast<byte, float>(readback.Read(0, N * 8));
            for (int i = 0; i < N; i++)
            {
                int expected = BitConverter.SingleToInt32Bits(MathF.Sqrt(inputs[i]));
                int driver = BitConverter.SingleToInt32Bits(roots[2 * i]);
                if (driver != expected) { raw++; if (Math.Abs(driver - expected) > 1) rawFar++; }
                Assert.True(expected == BitConverter.SingleToInt32Bits(roots[2 * i + 1]),
                    $"CrSqrt({inputs[i]:R}) = {roots[2 * i + 1]:R}, MathF.Sqrt {MathF.Sqrt(inputs[i]):R} (driver sqrt {roots[2 * i]:R})");
            }
        }
        TestContext.Current.TestOutputHelper?.WriteLine($"driver sqrt: {raw} of {N} differ from the correctly rounded root ({rawFar} by more than 1 ulp)");
        ExpectClean(d!);
    }

    static BarrierBatch Full()
    {
        var b = new BarrierBatch();
        b.Add(PipelineStageFlags2.AllCommandsBit, AccessFlags2.MemoryWriteBit, PipelineStageFlags2.AllCommandsBit, AccessFlags2.MemoryReadBit | AccessFlags2.MemoryWriteBit);
        return b;
    }

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
                var t = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromYawPitchRoll((float)random.NextDouble() * 6.28f, 0.05f, 0) * Matrix4x4.CreateTranslation(p);
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

    [Fact]
    [Slow]
    public unsafe void Gpu_cull_matches_FoliageCull_on_synthetic_data()
    {
        using var d = TryCreate();
        Assert.SkipWhen(d is null, "No Vulkan 1.3 device");
        var random = new Random(21);
        using (var gl = new VkGl(d!))
        {
            var ctx = gl.Context;
            using var cull = new FoliageGpuCull(ctx, arenaBytes: 1 << 20);   // small: the views below make it grow
            long totalVisible = 0, totalCandidates = 0;
            for (int viewNumber = 0; viewNumber < 6; viewNumber++)
            {
                var eye = new Vector3(random.Next(-200000, 200000), 300 + random.Next(0, 2000), random.Next(-200000, 200000));
                var planes = Frustum(eye, (float)(random.NextDouble() * 6.28), (float)(random.NextDouble() * 0.8 - 0.6));
                if (viewNumber == 5) planes = planes[..4];   // a shadow cascade's frustum may have fewer planes
                const int Batches = 5;
                var groups = Synthetic(random, eye, planes, 40, Batches);
                var view = new FoliageCullView().Set(planes);
                var eyeXz = new Vector2(eye.X, eye.Z);

                gl.BeginFrame(4, 4);
                // Place every group (a grown arena moves them all: place again), then chunks grouped by batch, groups in order.
                bool placed;
                do
                {
                    placed = true;
                    foreach (var g in groups)
                        if (!cull.Place(ref g.Arena, ref g.Generation, g.Records)) { placed = false; break; }
                } while (!placed);
                var chunks = new List<FoliageCullChunk>();
                var starts = new int[Batches + 1];
                for (int b = 0; b < Batches; b++)
                {
                    starts[b] = chunks.Count;
                    foreach (var g in groups.Where(g => g.Batch == b))
                        for (int at = 0; at < g.Records.Length; at += FoliageShaders.CullChunk)
                            chunks.Add(new FoliageCullChunk
                            {
                                First = FoliageGpuCull.FirstOf(g.Arena) + (uint)at, Count = (uint)Math.Min(FoliageShaders.CullChunk, g.Records.Length - at),
                                Range = g.Range.Range, RangeSquared = g.Range.RangeSquared, InverseBand = g.Range.InverseBand,
                            });
                }
                starts[Batches] = chunks.Count;
                // Two draws per batch (a mesh and its leaves), each with its own index count.
                var draws = new List<FoliageCullDraw>();
                for (int b = 0; b < Batches; b++)
                    for (int part = 0; part < 2; part++)
                        draws.Add(new FoliageCullDraw { IndexCount = (uint)(100 * b + part + 3), ChunkStart = (uint)starts[b], ChunkEnd = (uint)starts[b + 1] });
                var work = cull.Prepare(chunks.ToArray(), draws.ToArray());
                var result = cull.Dispatch(work, view, eyeXz);
                using var readback = ReadbackBuffer.Create(ctx, FoliageGpuCull.ReadbackBytes(result), "cull readback");
                cull.CopyForReadback(result, readback);
                gl.EndFrame();
                d!.Frames.WaitAll();

                // The CPU reference, batch by batch, group by group.
                var expected = new List<Matrix4x4>[Batches];
                var output = new FoliageCullOutput();
                for (int b = 0; b < Batches; b++)
                {
                    expected[b] = [];
                    foreach (var g in groups.Where(g => g.Batch == b))
                    {
                        FoliageCull.CullGroup(g.Records, g.Range, eyeXz, view, record: false, output);
                        expected[b].AddRange(output.Visible.AsSpan(0, output.Count));
                    }
                }
                int n = chunks.Count;
                var offsets = MemoryMarshal.Cast<byte, uint>(readback.Read(0, (ulong)(n + 1) * 4)).ToArray();
                ulong argsAt = FoliageGpuCull.Align16((ulong)(n + 1) * 4);
                var args = MemoryMarshal.Cast<byte, uint>(readback.Read(argsAt, (ulong)draws.Count * 20)).ToArray();
                ulong rowsAt = argsAt + FoliageGpuCull.Align16((ulong)draws.Count * 20);
                var rows = MemoryMarshal.Cast<byte, Matrix4x4>(readback.Read(rowsAt, (ulong)offsets[n] * 64)).ToArray();
                Assert.Equal(expected.Sum(e => e.Count), (int)offsets[n]);
                for (int b = 0; b < Batches; b++)
                {
                    uint first = offsets[starts[b]], count = offsets[starts[b + 1]] - first;
                    Assert.Equal(expected[b].Count, (int)count);
                    for (int j = 0; j < count; j++)
                    {
                        var e = expected[b][j];
                        var got = rows[first + j];
                        // Bit for bit, the fade (row 0 w) included.
                        Assert.True(MemoryMarshal.AsBytes(new ReadOnlySpan<Matrix4x4>(in e)).SequenceEqual(MemoryMarshal.AsBytes(new ReadOnlySpan<Matrix4x4>(in got))),
                            $"view {viewNumber}, batch {b}, instance {j}: GPU fade {got.M14:R}, CPU {e.M14:R}; translation {got.Translation} against {e.Translation}");
                    }
                    for (int part = 0; part < 2; part++)
                    {
                        int at = (b * 2 + part) * 5;
                        Assert.Equal([draws[b * 2 + part].IndexCount, count, 0u, 0u, first], args[at..(at + 5)]);
                    }
                }
                totalVisible += offsets[n];
                totalCandidates += groups.Sum(g => g.Records.Length);
            }
            Assert.True(cull.Grows > 0, "the arena should have grown");
            Assert.True(totalVisible > 1000 && totalVisible < totalCandidates / 2, $"{totalVisible} of {totalCandidates} visible: the views should cut the sets");
        }
        ExpectClean(d!);
    }

    /// <summary>
    /// TERRAIN-mode rocks (5.6.1) in the same dispatch as ordinary foliage batches: per rock batch (a mesh's plain or mirroring placements)
    /// the placements the CPU path hands <see cref="TerrainRenderer.DrawMeshes"/> (the A1 cull, then <c>FoliageRenderer.EmitAll</c>: a fade
    /// of at least 0.5, M14 = 0) as it groups and writes them (by mirroring, in order; row 0 w the biome row when resident, else -1, in
    /// colour; 0 in depth), bit for bit. Groups mix both kinds of placement, a third of the fades sit within a few ulps of 0.5, biome rows
    /// include none (-1) and rows beyond the first word of the residency bits.
    /// </summary>
    [Fact]
    [Slow]
    public unsafe void Gpu_cull_matches_the_terrain_mesh_path_for_rocks()
    {
        using var d = TryCreate();
        Assert.SkipWhen(d is null, "No Vulkan 1.3 device");
        var random = new Random(31);
        using (var gl = new VkGl(d!))
        {
            var ctx = gl.Context;
            using var cull = new FoliageGpuCull(ctx, arenaBytes: 4 << 20);
            long totalRocks = 0, totalMirrored = 0, nearThreshold = 0;
            for (int viewNumber = 0; viewNumber < 6; viewNumber++)
            {
                var eye = new Vector3(random.Next(-200000, 200000), 300 + random.Next(0, 2000), random.Next(-200000, 200000));
                var planes = Frustum(eye, (float)(random.NextDouble() * 6.28), (float)(random.NextDouble() * 0.8 - 0.6));
                if (viewNumber == 4) planes = planes[..4];
                bool biomes = viewNumber % 2 == 0;
                var view = new FoliageCullView().Set(planes);
                var eyeXz = new Vector2(eye.X, eye.Z);
                var rockView = new FoliageRockView { BiomeRows = biomes };
                for (int w = 0; w < 8; w++) rockView.Resident[w] = (uint)random.Next() ^ ((uint)random.Next() << 16);
                bool Resident(int row) => row >= 0 && ((rockView.Resident[row >> 5] >> (row & 31)) & 1) != 0;

                // Two ordinary batches and three rock meshes (batches: mesh 0, mesh 1, then each rock mesh's plain and mirroring placements).
                const int MeshBatches = 2, RockMeshes = 3;
                var meshGroups = Synthetic(random, eye, planes, 8, MeshBatches);
                var rockGroups = new List<(int Mesh, SyntheticGroup Group)>();
                for (int g = 0; g < 18; g++)
                {
                    int count = random.Next(1, 700);
                    float range = (float)(random.NextDouble() * 15000 + 300), band = MathF.Max(10, range * 0.1f);
                    var fr = FoliageGroupRange.Of(range, band);
                    bool mixed = g % 3 != 2, allMirrored = g % 6 == 2;
                    var records = new FoliageInstanceRecord[count];
                    for (int i = 0; i < count; i++)
                    {
                        float angle = (float)(random.NextDouble() * Math.PI * 2);
                        // A third at the middle of the fade (w = 0.5 within a few ulps), the rest anywhere up to beyond the range.
                        float dist = i % 3 == 0 ? (range - 0.5f * band) * (1 + (float)(random.NextDouble() - 0.5) * 6e-7f) : range * (float)random.NextDouble() * 1.1f;
                        var p = new Vector3(eye.X + MathF.Cos(angle) * dist, (float)(random.NextDouble() * 300), eye.Z + MathF.Sin(angle) * dist);
                        float scale = (float)(0.5 + random.NextDouble() * 1.5);
                        bool mirror = allMirrored || (mixed && random.Next(3) == 0);
                        var t = Matrix4x4.CreateScale(mirror ? -scale : scale, scale, scale) * Matrix4x4.CreateFromYawPitchRoll((float)random.NextDouble() * 6.28f, 0.05f, 0) * Matrix4x4.CreateTranslation(p);
                        int row = random.Next(8) == 0 ? -1 : random.Next(0, 250);
                        records[i] = new FoliageInstanceRecord { Transform = t, Ground = new Vector4(p.X, p.Z, scale, FoliageCull.RockBits(t, row)) };
                    }
                    FoliageCull.FillSpheres(records, new Vector3(0.5f, 2, 0.25f), 6);
                    rockGroups.Add((random.Next(RockMeshes), new SyntheticGroup(-1, records, fr)));
                }

                gl.BeginFrame(4, 4);
                bool placed;
                do
                {
                    placed = true;
                    foreach (var g in meshGroups.Concat(rockGroups.Select(r => r.Group)))
                        if (!cull.Place(ref g.Arena, ref g.Generation, g.Records)) { placed = false; break; }
                } while (!placed);
                var chunks = new List<FoliageCullChunk>();
                const int Batches = MeshBatches + 2 * RockMeshes;
                var starts = new int[Batches + 1];
                void Add(SyntheticGroup g, uint flags)
                {
                    for (int at = 0; at < g.Records.Length; at += FoliageShaders.CullChunk)
                        chunks.Add(new FoliageCullChunk
                        {
                            First = FoliageGpuCull.FirstOf(g.Arena) + (uint)at, Count = (uint)Math.Min(FoliageShaders.CullChunk, g.Records.Length - at),
                            Range = g.Range.Range, RangeSquared = g.Range.RangeSquared, InverseBand = g.Range.InverseBand, Flags = flags,
                        });
                }
                for (int b = 0; b < Batches; b++)
                {
                    starts[b] = chunks.Count;
                    if (b < MeshBatches)
                        foreach (var g in meshGroups.Where(g => g.Batch == b)) Add(g, 0);
                    else
                    {
                        int mesh = (b - MeshBatches) / 2;
                        bool mirrored = (b - MeshBatches) % 2 == 1;
                        foreach (var (m, g) in rockGroups)
                            if (m == mesh) Add(g, FoliageCullChunk.Rock | (mirrored ? FoliageCullChunk.Mirrored : 0));
                    }
                }
                starts[Batches] = chunks.Count;
                var draws = new List<FoliageCullDraw>();
                for (int b = 0; b < Batches; b++)
                    draws.Add(new FoliageCullDraw { IndexCount = (uint)(30 * b + 7), ChunkStart = (uint)starts[b], ChunkEnd = (uint)starts[b + 1] });
                var work = cull.Prepare(chunks.ToArray(), draws.ToArray());
                var result = cull.Dispatch(work, view, eyeXz, in rockView);
                using var readback = ReadbackBuffer.Create(ctx, FoliageGpuCull.ReadbackBytes(result), "cull readback");
                cull.CopyForReadback(result, readback);
                gl.EndFrame();
                d!.Frames.WaitAll();

                // The CPU reference: the ordinary batches as FoliageCull gives them; the rocks as the terrain's mesh path gets them.
                var expected = new List<Matrix4x4>[Batches];
                var output = new FoliageCullOutput();
                for (int b = 0; b < Batches; b++)
                {
                    expected[b] = [];
                    if (b < MeshBatches)
                    {
                        foreach (var g in meshGroups.Where(g => g.Batch == b))
                        {
                            FoliageCull.CullGroup(g.Records, g.Range, eyeXz, view, record: false, output);
                            expected[b].AddRange(output.Visible.AsSpan(0, output.Count));
                        }
                        continue;
                    }
                    int mesh = (b - MeshBatches) / 2;
                    bool mirrored = (b - MeshBatches) % 2 == 1;
                    foreach (var (m, g) in rockGroups)
                    {
                        if (m != mesh) continue;
                        FoliageCull.CullGroup(g.Records, g.Range, eyeXz, view, record: false, output);
                        foreach (var v in output.Visible.AsSpan(0, output.Count))
                        {
                            var t = v;
                            float w = t.M14 >= 2 ? 1 : t.M14;   // FoliageRenderer.EmitAll and Emit
                            t.M14 = 0;
                            if (w < FoliageCull.RockThreshold) continue;
                            if (t.GetDeterminant() < 0 != mirrored) continue;   // TerrainRenderer.GroupMeshes' key
                            int row = biomes ? -1 : 0;
                            if (biomes)
                            {
                                // The row the record was given (RockBits), as FeatureBiomeRow answers with this residency.
                                int index = Array.FindIndex(g.Records, r => r.Transform.Translation == t.Translation && r.Transform.M11 == t.M11);
                                int any = ((int)g.Records[index].Ground.W & 1023) - 1;
                                row = Resident(any) ? any : -1;
                            }
                            t.M14 = row;
                            expected[b].Add(t);
                            if (MathF.Abs(w - 0.5f) < 1e-5f) nearThreshold++;
                        }
                    }
                }
                int n = chunks.Count;
                var offsets = MemoryMarshal.Cast<byte, uint>(readback.Read(0, (ulong)(n + 1) * 4)).ToArray();
                ulong argsAt = FoliageGpuCull.Align16((ulong)(n + 1) * 4);
                var args = MemoryMarshal.Cast<byte, uint>(readback.Read(argsAt, (ulong)draws.Count * 20)).ToArray();
                ulong rowsAt = argsAt + FoliageGpuCull.Align16((ulong)draws.Count * 20);
                var rows = MemoryMarshal.Cast<byte, Matrix4x4>(readback.Read(rowsAt, (ulong)offsets[n] * 64)).ToArray();
                Assert.Equal(expected.Sum(e => e.Count), (int)offsets[n]);
                for (int b = 0; b < Batches; b++)
                {
                    uint first = offsets[starts[b]], count = offsets[starts[b + 1]] - first;
                    Assert.True(expected[b].Count == (int)count, $"view {viewNumber}, batch {b}: {count} on the GPU, {expected[b].Count} on the CPU");
                    for (int j = 0; j < count; j++)
                    {
                        var e = expected[b][j];
                        var got = rows[first + j];
                        Assert.True(MemoryMarshal.AsBytes(new ReadOnlySpan<Matrix4x4>(in e)).SequenceEqual(MemoryMarshal.AsBytes(new ReadOnlySpan<Matrix4x4>(in got))),
                            $"view {viewNumber}, batch {b}, instance {j}: GPU row 0 w {got.M14:R}, CPU {e.M14:R}; translation {got.Translation} against {e.Translation}");
                    }
                    Assert.Equal([draws[b].IndexCount, count, 0u, 0u, first], args[(b * 5)..(b * 5 + 5)]);
                    if (b >= MeshBatches)
                    {
                        totalRocks += count;
                        if ((b - MeshBatches) % 2 == 1) totalMirrored += count;
                    }
                }
            }
            Assert.True(totalRocks > 500, $"{totalRocks} rock placements drawn: the views should show some");
            Assert.True(totalMirrored > 100, $"{totalMirrored} mirroring rock placements drawn");
            Assert.True(nearThreshold > 20, $"{nearThreshold} rocks drawn at a fade within 1e-5 of 0.5: the threshold should be exercised");
        }
        ExpectClean(d!);
    }
}
