using Meitou.Data.World;
using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Game.Rendering.Foliage;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Gpu.Core;

namespace Meitou.Tests.Rendering;

/// <summary>
/// Generated foliage levels (docs/formats/foliage.md, "Generated levels"): the builder and its deviation measure, the disk cache, and the cull
/// kernel's choice of level.
/// </summary>
public class FoliageLodTests
{
    /// <summary>
    /// A UV sphere of the radius as a one-part model.
    /// </summary>
    static Model Sphere(int rings, int segments, float radius, float spikes = 0)
    {
        var vertices = new List<Vertex>();
        for (int r = 0; r <= rings; r++)
            for (int s = 0; s <= segments; s++)
            {
                float phi = MathF.PI * r / rings, theta = 2 * MathF.PI * (s % segments) / segments;
                var n = new Vector3(MathF.Sin(phi) * MathF.Cos(theta), MathF.Cos(phi), MathF.Sin(phi) * MathF.Sin(theta));
                vertices.Add(new Vertex { Position = n * radius, Normal = n, Uv = new Vector2((float)s / segments, (float)r / rings),
                        Weights = new Vector4(1, 0, 0, 0) });
            }
        var indices = new List<uint>();
        for (int r = 0; r < rings; r++)
            for (int s = 0; s < segments; s++)
            {
                uint a = (uint)(r * (segments + 1) + s), b = a + 1, c = a + (uint)(segments + 1), d = c + 1;
                indices.AddRange([a, c, b, b, c, d]);
            }
        var model = new Model();
        model.Parts.Add(new ModelPart { SubMeshIndex = 0, MaterialName = "m", Vertices = [.. vertices], Indices = [.. indices] });
        return model;
    }

    /// <summary>
    /// A smooth sphere's levels: each deviates no less than the one before and has fewer triangles by at least the builder's gain.
    /// </summary>
    [Fact]
    public void A_smooth_mesh_gets_levels_with_a_small_never_decreasing_deviation()
    {
        var model = Sphere(40, 80, 100);
        var set = FoliageLodBuilder.Build(model, radius: 100);

        Assert.NotNull(set);
        Assert.True(set.Levels >= 3, $"{set.Levels} levels");
        Assert.Equal(0, set.Errors[0]);

        for (int i = 1; i < set.Levels; i++)
        {
            Assert.True(set.Errors[i] >= set.Errors[i - 1], $"deviation of level {i}");
            Assert.True(set.Triangles[i] <= set.Triangles[i - 1] * FoliageLodBuilder.MinGain + 1, $"triangles of level {i}");
        }

        Assert.Equal(model.Parts[0].Indices.Length / 3, set.Triangles[0]);
        Assert.True(set.Errors[1] < 0.02f * 100, $"half the triangles of a sphere deviate {set.Errors[1]} units");

        foreach (var level in set.Indices[0])
        {
            Assert.Equal(0, level.Length % 3);
            Assert.All(level, i => Assert.True(i < model.Parts[0].Vertices.Length));
        }
    }

    /// <summary>
    /// The deviation of a level that drops a thin spike is the spike's height.
    /// </summary>
    [Fact]
    public void Deviation_sees_a_thin_part_that_a_level_loses()
    {
        // A flat 20 x 20 grid with one thin spike (a sliver triangle 30 units high) in the middle; the level is the grid alone.
        var vertices = new List<Vertex>();
        for (int y = 0; y <= 20; y++)
            for (int x = 0; x <= 20; x++) vertices.Add(new Vertex { Position = new Vector3(x, 0, y) });
        var indices = new List<uint>();
        for (int y = 0; y < 20; y++)
            for (int x = 0; x < 20; x++)
            {
                uint a = (uint)(y * 21 + x), b = a + 1, c = a + 21, d = c + 1;
                indices.AddRange([a, c, b, b, c, d]);
            }
        var flat = indices.ToArray();
        vertices.Add(new Vertex { Position = new Vector3(10, 30, 10) });
        uint tip = (uint)vertices.Count - 1, p0 = 10 * 21 + 10, p1 = p0 + 1;
        indices.AddRange([p0, p1, tip]);
        float deviation = FoliageLodBuilder.Deviation([.. vertices], [.. indices], flat);

        Assert.InRange(deviation, 29.9f, 30.1f);
        Assert.Equal(0, FoliageLodBuilder.Deviation([.. vertices], flat, flat), 4);
    }

    // ---- the cull kernel ----

    /// <summary>
    /// A Vulkan 1.3 device with validation, or null where there is none.
    /// </summary>
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

    /// <summary>
    /// Three batches (levels 0 to 2) hold the same instances in chunks flagged <see cref="FoliageCullChunk.Lod"/>: every instance the plain cull
    /// keeps lands in exactly one of them, the one the rule picks (the deviation in pixels, or in texels for an orthographic view, at most the
    /// tolerance while the next level's is not), and the draws carry their first index.
    /// </summary>
    [Fact]
    [Slow]
    public unsafe void The_cull_puts_each_instance_in_the_level_its_size_picks()
    {
        using var d = TryCreate();

        Assert.SkipWhen(d is null, "No Vulkan 1.3 device");

        var random = new Random(77);
        float[] relative = [0, 0.004f, 0.02f];   // the levels' deviation in radii of the mesh
        using (var ctx = new GpuContext(d!))
        {
            using var cull = new FoliageGpuCull(ctx, arenaBytes: 4 << 20);
            for (int viewNumber = 0; viewNumber < 4; viewNumber++)
                CullOneLodView(d!, ctx, cull, random, relative, viewNumber);
        }
        ExpectClean(d!);
    }

    /// <summary>
    /// One random view (orthographic from the third on): the count of each level's draw within the range the rule allows.
    /// </summary>
    static void CullOneLodView(VulkanDevice d, GpuContext ctx, FoliageGpuCull cull, Random random, float[] relative, int viewNumber)
    {
        bool ortho = viewNumber >= 2;
        var eye = new Vector3(random.Next(-5000, 5000), 200 + random.Next(0, 1500), random.Next(-5000, 5000));
        var planes = Frustum(eye, (float)(random.NextDouble() * 6.28), -0.1f);
        var view = new FoliageCullView().Set(planes);
        var eyeXz = new Vector2(eye.X, eye.Z);
        var lod = ortho ? new FoliageLodView(0, 1 / 3f, 2f, true) : new FoliageLodView(eye.Y, 900f, 3f, false);

        var records = RandomInstances(random, eye);
        var range = FoliageGroupRange.Of(12000, 1200);

        var arena = default(ArenaRange);
        int generation = 0;
        ctx.BeginFrame();
        while (!cull.Place(ref arena, ref generation, records)) { }
        var chunks = new List<FoliageCullChunk>();
        var starts = new int[4];
        AddLevelChunks(chunks, starts, arena, records, range, relative);
        var draws = LevelDraws(starts);
        var work = cull.Prepare(chunks.ToArray(), draws.ToArray());
        var result = cull.Dispatch(work, view, eyeXz, default, 0.999f, default, default, lod);
        using var readback = ReadbackBuffer.Create(ctx, FoliageGpuCull.ReadbackBytes(result), "lod readback");
        cull.CopyForReadback(result, readback);
        ctx.EndFrame();
        d.Frames.WaitAll();

        int[] low = new int[3], high = new int[3];
        int total = RuleLevels(records, range, eyeXz, view, eye, ortho, lod, relative, low, high);
        int sum = AssertLevelCounts(readback, chunks.Count, low, high);

        Assert.Equal(total, sum);
        Assert.True(total > 100, $"view {viewNumber}: {total} instances survive");
        Assert.True(high[1] > 0 || high[2] > 0, "the views should pick some coarser levels");
    }

    /// <summary>
    /// 1500 instances of random scale round the eye, most of them near it.
    /// </summary>
    static FoliageInstanceRecord[] RandomInstances(Random random, Vector3 eye)
    {
        var records = new FoliageInstanceRecord[1500];
        for (int i = 0; i < records.Length; i++)
        {
            float angle = (float)(random.NextDouble() * Math.PI * 2), dist = (float)(random.NextDouble() * random.NextDouble() * 9000);
            var p = new Vector3(eye.X + MathF.Cos(angle) * dist, (float)random.NextDouble() * 300, eye.Z + MathF.Sin(angle) * dist);
            float scale = (float)(0.3 + random.NextDouble() * 3);
            records[i] = new FoliageInstanceRecord { Transform = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateTranslation(p),
                    Ground = new Vector4(p.X, p.Z, scale, 0) };
        }
        FoliageCull.FillSpheres(records, new Vector3(0, 10, 0), 80);
        return records;
    }

    /// <summary>
    /// The same instances in chunks flagged <see cref="FoliageCullChunk.Lod"/> once per level; fills each level's first chunk into the starts
    /// (and the chunk count last).
    /// </summary>
    static void AddLevelChunks(List<FoliageCullChunk> chunks, int[] starts, ArenaRange arena, FoliageInstanceRecord[] records,
            FoliageGroupRange range, float[] relative)
    {
        for (int level = 0; level < 3; level++)
        {
            starts[level] = chunks.Count;
            for (int at = 0; at < records.Length; at += FoliageShaders.CullChunk)
                chunks.Add(new FoliageCullChunk
                {
                    First = FoliageGpuCull.FirstOf(arena) + (uint)at, Count = (uint)Math.Min(FoliageShaders.CullChunk, records.Length - at),
                    Range = range.Range, RangeSquared = range.RangeSquared, InverseBand = range.InverseBand, Flags = FoliageCullChunk.Lod,
                    LodError = relative[level], LodNextError = level + 1 < 3 ? relative[level + 1] : float.PositiveInfinity,
                });
        }
        starts[3] = chunks.Count;
    }

    /// <summary>
    /// One draw per level, each with its own index count and first index.
    /// </summary>
    static List<FoliageCullDraw> LevelDraws(int[] starts)
    {
        var draws = new List<FoliageCullDraw>();
        for (int level = 0; level < 3; level++)
            draws.Add(new FoliageCullDraw { IndexCount = (uint)(30 - level * 8), FirstIndex = (uint)(level * 100),
                    ChunkStart = (uint)starts[level], ChunkEnd = (uint)starts[level + 1] });
        return draws;
    }

    /// <summary>
    /// The plain cull's survivors, and the level the rule gives each (a range of counts where an instance sits within a hair of a boundary):
    /// fills the least and greatest count of each level and returns how many survive.
    /// </summary>
    static int RuleLevels(FoliageInstanceRecord[] records, FoliageGroupRange range, Vector2 eyeXz, FoliageCullView view, Vector3 eye, bool ortho,
            FoliageLodView lod, float[] relative, int[] low, int[] high)
    {
        var output = new FoliageCullOutput();
        FoliageCull.CullGroup(records, range, eyeXz, view, record: false, output);
        var survivors = new HashSet<(float, float, float)>();
        int total = 0;
        for (int i = 0; i < records.Length; i++)
        {
            var r = records[i];
            var t = r.Transform;
            if (!output.Visible.AsSpan(0, output.Count).ToArray().Any(m => m.M41 == t.M41 && m.M42 == t.M42 && m.M43 == t.M43))
                continue;
            total++;
            double radius = r.Sphere.W;
            double s = ortho ? radius * lod.Scale
                : radius * lod.Scale / Math.Max(Vector3.Distance(new Vector3(r.Sphere.X, r.Sphere.Y, r.Sphere.Z), eye) - radius, 1);

            // Level k is picked when relative[k] s
            // <= tolerance < relative[k+1] s; the candidates within 0.1 % of a boundary are either neighbour.
            int lo = 0, hi = 0;
            for (int k = 1; k < 3; k++)
            {
                double v = relative[k] * s / lod.Tolerance;
                if (v <= 1 - 1e-3)
                {
                    lo = k;
                    hi = k;
                }
                else if (v <= 1 + 1e-3) hi = k;
            }
            for (int k = lo; k <= hi; k++) high[k]++;
            if (lo == hi) low[lo]++;
        }
        return total;
    }

    /// <summary>
    /// Each level's draw read back: its count within the rule's range, its index count and first index; returns the counts' sum.
    /// </summary>
    static int AssertLevelCounts(ReadbackBuffer readback, int chunkCount, int[] low, int[] high)
    {
        var offsets = MemoryMarshal.Cast<byte, uint>(readback.Read(0, (ulong)(chunkCount + 1) * 4)).ToArray();
        ulong argsAt = FoliageGpuCull.Align16((ulong)(chunkCount + 1) * 4);
        var args = MemoryMarshal.Cast<byte, uint>(readback.Read(argsAt, 3 * 20)).ToArray();
        int sum = 0;
        for (int level = 0; level < 3; level++)
        {
            uint count = args[level * 5 + 1];
            sum += (int)count;

            Assert.InRange((int)count, low[level], high[level]);
            Assert.Equal((uint)(30 - level * 8), args[level * 5]);
            Assert.Equal((uint)(level * 100), args[level * 5 + 2]);
        }
        return sum;
    }

    /// <summary>
    /// Fails with the validation log when the device reported validation errors.
    /// </summary>
    static void ExpectClean(VulkanDevice d) =>

        Assert.True(d.ValidationErrors == 0, "Validation errors:\n" + string.Join("\n", d.ValidationLog));

    /// <summary>
    /// The frustum planes of a 16:9 camera at the eye looking along the yaw and pitch.
    /// </summary>
    static Vector4[] Frustum(Vector3 eye, float yaw, float pitch)
    {
        var forward = new Vector3(MathF.Sin(yaw) * MathF.Cos(pitch), MathF.Sin(pitch), MathF.Cos(yaw) * MathF.Cos(pitch));
        var view = Matrix4x4.CreateLookAt(eye, eye + forward, Vector3.UnitY);
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(1.2f, 16 / 9f, 1, 40000);
        return WorldCamera.FrustumPlanes(view * projection);
    }
}
