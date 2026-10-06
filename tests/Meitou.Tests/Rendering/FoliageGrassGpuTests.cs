using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Data.World;
using Meitou.Rendering;
using Meitou.Rendering.Gpu;
using Meitou.Rendering.Vulkan;
using Meitou.Rendering.Vulkan.Core;
using Silk.NET.Vulkan;

namespace Meitou.Tests.Rendering;

/// <summary>
/// The GPU grass cull (docs/renderer-native.md 5.6.2) against the CPU's decisions of <c>FoliageRenderer.PrepareGrass</c> on synthetic data: the
/// draws that exist, their blade counts (the density's prefix), their vertex counts and first blades, in nearest-first order.
/// </summary>
public class FoliageGrassGpuTests
{
    const float PageSize = 576;

    static VulkanDevice? TryCreate()
    {
        try { return VulkanDevice.Create(new VulkanDeviceOptions { Validation = true, SyncValidation = true }); }
        catch (Exception e) when (e is VulkanException or DllNotFoundException or EntryPointNotFoundException or Silk.NET.Core.Loader.SymbolLoadingException) { return null; }
    }

    /// <summary>BoxDistance of <c>FoliageRenderer</c> (static, private there).</summary>
    static float BoxDistance(float x0, float z0, float size, float ex, float ez)
    {
        float dx = Math.Max(Math.Max(x0 - ex, ex - (x0 + size)), 0);
        float dz = Math.Max(Math.Max(z0 - ez, ez - (z0 + size)), 0);
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    sealed record Page(GrassSlot Slot, int[] Prefix, float Range, bool Active, uint Vertices);

    static Page[] MakePages(int count, Random random, float ex, float ez)
    {
        var pages = new Page[count];
        for (int i = 0; i < count; i++)
        {
            float x0 = (float)(ex + (random.NextDouble() - 0.5) * 30000), z0 = (float)(ez + (random.NextDouble() - 0.5) * 30000);
            float y = (float)(random.NextDouble() * 3000 - 500);
            var prefix = new int[FoliageGrassField.PrefixSteps + 1];
            int total = random.Next(1, 4000);
            for (int s = 1; s < prefix.Length; s++) prefix[s] = prefix[s - 1] + random.Next(0, total / 40 + 2);
            float d = BoxDistance(x0, z0, PageSize, ex, ez);
            // A third of the ranges sit on the page's distance or a step beside it.
            float range = (i % 3) switch { 0 => d, 1 => MathF.BitIncrement(d), _ => d + (float)(random.NextDouble() * 4000 - 2000) };
            pages[i] = new Page(new GrassSlot { X0 = x0, Z0 = z0, Y = y, FirstBlade = (uint)(i * 7), Count = (uint)prefix[^1] + (i % 11 == 0 ? 0u : 1u), Zone = (uint)i, Local = 0, Tie = (uint)(random.Next(1 << 20) << 8) },
                prefix, range, i % 13 != 0, i % 5 == 0 ? 12u : 6u);
        }
        return pages;
    }

    static (uint First, uint Shown, uint Vertices, uint Row, float Distance, uint Tie)[] Reference(Page[] pages, Vector4[] planes, float ex, float ez, float fraction)
    {
        var (index, frac) = FoliageGrassGpu.DensityStep(fraction);
        var list = new List<(uint, uint, uint, uint, float, uint)>();
        for (int i = 0; i < pages.Length; i++)
        {
            var p = pages[i];
            var s = p.Slot;
            if (s.Count == 0 || !p.Active) continue;
            float d = BoxDistance(s.X0, s.Z0, PageSize, ex, ez);
            if (!WorldCamera.Intersects(planes, new Vector3(s.X0, s.Y - 2000, s.Z0), new Vector3(s.X0 + PageSize, s.Y + 2000, s.Z0 + PageSize))) continue;
            if (d >= p.Range) continue;
            int shown = FoliageGrassField.PrefixCount(p.Prefix, fraction);
            if (shown == 0) continue;
            list.Add((s.FirstBlade, (uint)shown, p.Vertices, (uint)i, d, s.Tie));
        }
        _ = (index, frac);
        return [.. list.OrderBy(e => e.Item5).ThenBy(e => e.Item6)];
    }

    [Fact]
    [Slow]
    public unsafe void Gpu_grass_cull_matches_the_CPU_decisions_on_synthetic_data()
    {
        using var d = TryCreate();
        Assert.SkipWhen(d is null, "No Vulkan 1.3 device");
        var random = new Random(5);
        const float ex = -37000, ez = -80000;
        int views = 0, draws = 0;
        using var gl = new VkGl(d!);
        var ctx = gl.Context;
        Assert.SkipUnless(FoliageGrassGpu.Supported(ctx), "no indirect count or push descriptors");
        using var store = new FoliageGrassGpu(ctx, 1 << 20, 2048);
        var pages = MakePages(1500, random, ex, ez);
        var densities = new[] { 0f, 0.0001f, 0.25f, 0.5f, 0.5078125f, 0.73f, 1f };
        var planeSets = new List<Vector4[]>();
        for (int v = 0; v < 6; v++)
        {
            var planes = new Vector4[v == 5 ? 4 : 6];
            for (int i = 0; i < planes.Length; i++)
            {
                var n = Vector3.Normalize(new Vector3((float)random.NextDouble() - 0.5f, (float)random.NextDouble() * 0.2f - 0.1f, (float)random.NextDouble() - 0.5f));
                var through = new Vector3(ex + (float)(random.NextDouble() - 0.5) * 20000 * (v + 1) / 3, 0, ez + (float)(random.NextDouble() - 0.5) * 20000 * (v + 1) / 3);
                planes[i] = new Vector4(n, -Vector3.Dot(n, through) - 3000);
            }
            planeSets.Add(planes);
        }
        gl.BeginFrame(4, 4);
        var zoneRows = new GrassZoneRow[pages.Length];
        var patchRows = new GrassPatchRow[pages.Length];
        for (int i = 0; i < pages.Length; i++)
        {
            int slot = store.AllocateSlot();
            Assert.Equal(i, slot);
            store.WriteSlot(slot, pages[i].Slot, pages[i].Prefix);
            zoneRows[i] = new GrassZoneRow { PatchBase = (uint)i, PatchCount = 1 };
            patchRows[i] = new GrassPatchRow { Range = pages[i].Range, VertexCount = pages[i].Vertices, Flags = pages[i].Active ? FoliageGrassShaders.FlagActive : 0 };
        }
        gl.EndFrame();
        d!.Frames.WaitAll();
        foreach (var fraction in densities)
            foreach (var planes in planeSets)
            {
                gl.BeginFrame(4, 4);
                var tables = store.Prepare(zoneRows, patchRows);
                var (index, frac) = FoliageGrassGpu.DensityStep(fraction);
                var result = store.Dispatch(planes, new Vector2(ex, ez), PageSize, index, frac, in tables);
                using var readback = ReadbackBuffer.Create(ctx, FoliageGrassGpu.ReadbackBytes(result), "grass test");
                store.CopyForReadback(result, readback);
                gl.EndFrame();
                d.Frames.WaitAll();
                var all = MemoryMarshal.Cast<byte, uint>(readback.Read(0, FoliageGrassGpu.ReadbackBytes(result)));
                var expected = Reference(pages, planes, ex, ez, fraction);
                Assert.True(expected.Length == all[0], $"fraction {fraction}, view {views}: {all[0]} draws on the GPU, {expected.Length} on the CPU");
                for (int k = 0; k < expected.Length; k++)
                {
                    var e = expected[k];
                    uint[] got = [all[4 + k * 4], all[4 + k * 4 + 1], all[4 + k * 4 + 2], all[4 + k * 4 + 3]];
                    Assert.True(got[0] == e.Vertices && got[1] == e.Shown && got[2] == e.Row * 16 && got[3] == e.First,
                        $"fraction {fraction}, view {views}, draw {k}: GPU ({got[0]}, {got[1]}, {got[2]}, {got[3]}), CPU ({e.Vertices}, {e.Shown}, {e.Row * 16}, {e.First})");
                }
                views++;
                draws += expected.Length;
            }
        TestContext.Current.TestOutputHelper?.WriteLine($"{views} views, {draws} draws equal");
        Assert.True(draws > 100, "the synthetic views draw something");
        Assert.True(d.ValidationErrors == 0, "Validation errors:\n" + string.Join("\n", d.ValidationLog));
    }
}
