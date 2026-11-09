#if MEITOU_METAL
using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Data.World;
using Meitou.Game.Rendering.Foliage;
using Meitou.Game.Rendering.Gpu;
using SharpMetal.Metal;

namespace Meitou.Tests.MetalBackend;

/// <summary>
/// The GPU foliage cull's compute path on Metal: the shaders compile through spirv-cross, the sets bind as argument
/// buffers, and the three dispatches write the same compacted rows and indirect arguments as the CPU reference. The
/// cull is recorded before any rendering, as the live Metal path must do (a Metal rendering cannot record compute).
/// </summary>
[Collection("Metal GPU")]
public sealed class MetalFoliageCullTests
{
    [Fact]
    [Slow]
    public void Gpu_cull_matches_FoliageCull_on_Metal()
    {
        using var gpu = MetalTestGpu.Create();
        Assert.SkipWhen(gpu is null, "No Metal device");
        var ctx = gpu!.Context;
        using var cull = new FoliageGpuCull(ctx, arenaBytes: 1 << 20, scratchCap: 16 << 20);

        var random = new Random(31);
        const int groupCount = 6;
        const int recordCount = 257;   // two chunks for each group
        var groups = new (FoliageInstanceRecord[] Records, ArenaRange Arena, FoliageGroupRange Range)[groupCount];
        for (int g = 0; g < groupCount; g++)
        {
            var records = new FoliageInstanceRecord[recordCount];
            var centre = new Vector3(g * 50, 10, 0);
            for (int i = 0; i < records.Length; i++)
            {
                var offset = new Vector3(
                    (float)(random.NextDouble() * 20 - 10),
                    (float)(random.NextDouble() * 20 - 10),
                    (float)(random.NextDouble() * 20 - 10));
                var p = centre + offset;
                var transform = Matrix4x4.CreateScale(0.5f + (float)random.NextDouble()) * Matrix4x4.CreateTranslation(p);
                records[i] = new FoliageInstanceRecord { Transform = transform, Ground = new Vector4(p.X, p.Z, 1, 0) };
            }
            FoliageCull.FillSpheres(records, new Vector3(0, 10, 0), 30);
            groups[g] = (records, default, FoliageGroupRange.Of(1e6f, 1e5f));
        }

        ctx.BeginHeadlessFrame();
        for (int g = 0; g < groupCount; g++)
        {
            var (records, arena, range) = groups[g];
            var generation = 0;
            Assert.True(cull.Place(ref arena, ref generation, records));
            groups[g] = (records, arena, range);
        }

        const int batchCount = 2;
        var chunks = new List<FoliageCullChunk>();
        var starts = new int[batchCount + 1];
        for (int b = 0; b < batchCount; b++)
        {
            starts[b] = chunks.Count;
            for (int g = b; g < groupCount; g += batchCount)
            {
                for (int at = 0; at < groups[g].Records.Length; at += FoliageShaders.CullChunk)
                {
                    chunks.Add(new FoliageCullChunk
                    {
                        First = FoliageGpuCull.FirstOf(groups[g].Arena) + (uint)at,
                        Count = (uint)Math.Min(FoliageShaders.CullChunk, groups[g].Records.Length - at),
                        Range = groups[g].Range.Range,
                        RangeSquared = groups[g].Range.RangeSquared,
                        InverseBand = groups[g].Range.InverseBand,
                    });
                }
            }
        }

        starts[batchCount] = chunks.Count;
        var draws = new List<FoliageCullDraw>();
        for (int b = 0; b < batchCount; b++)
        {
            for (int part = 0; part < 3; part++)
            {
                draws.Add(new FoliageCullDraw
                {
                    IndexCount = (uint)(30 + b * 10 + part),
                    FirstIndex = (uint)(b * 100 + part * 10),
                    ChunkStart = (uint)starts[b],
                    ChunkEnd = (uint)starts[b + 1],
                });
            }
        }

        var work = cull.Prepare(chunks.ToArray(), draws.ToArray());
        var result = cull.Dispatch(work, new FoliageCullView().Set([]), Vector2.Zero);
        Assert.False(result.IsEmpty);
        ctx.EndHeadlessFrame();

        int n = chunks.Count;
        var offsets = MemoryMarshal.Cast<byte, uint>(ctx.ReadBuffer(result.Offsets).AsSpan((int)result.OffsetsOffset)).ToArray()[..(n + 1)];
        var args = MemoryMarshal.Cast<byte, uint>(ctx.ReadBuffer(result.Args).AsSpan((int)result.ArgsOffset)).ToArray()[..(draws.Count * 20)];
        var rows = MemoryMarshal.Cast<byte, Matrix4x4>(ctx.ReadBuffer(result.Rows).AsSpan((int)result.RowsOffset)).ToArray()[..(int)offsets[n]];

        var expected = new List<Matrix4x4>[batchCount];
        var output = new FoliageCullOutput();
        var view = new FoliageCullView().Set([]);
        for (int b = 0; b < batchCount; b++)
        {
            expected[b] = [];
            for (int g = b; g < groupCount; g += batchCount)
            {
                FoliageCull.CullGroup(groups[g].Records, groups[g].Range, Vector2.Zero, view, record: false, output);
                expected[b].AddRange(output.Visible.AsSpan(0, output.Count));
            }
        }

        Assert.Equal(expected.Sum(e => e.Count), (int)offsets[n]);
        for (int b = 0; b < batchCount; b++)
        {
            uint first = offsets[starts[b]], count = offsets[starts[b + 1]] - first;
            Assert.Equal((uint)expected[b].Count, count);
            for (int i = 0; i < count; i++)
            {
                var want = expected[b][i];
                var got = rows[first + i];
                Assert.True(
                    MemoryMarshal.AsBytes(new ReadOnlySpan<Matrix4x4>(in want))
                        .SequenceEqual(MemoryMarshal.AsBytes(new ReadOnlySpan<Matrix4x4>(in got))),
                    $"batch {b}, row {i}");
            }

            for (int part = 0; part < 3; part++)
            {
                int at = (b * 3 + part) * 5;
                var draw = draws[b * 3 + part];
                Assert.Equal([draw.IndexCount, count, draw.FirstIndex, 0u, first], args[at..(at + 5)]);
            }
        }
    }
}
#endif
