using System.Numerics;
using Meitou.Game.Rendering.Characters;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Gpu.Core;

namespace Meitou.Tests.Rendering;

/// <summary>
/// <see cref="MorphArena.Scratch"/>, the base of the character editor's blended morph (docs/characters.md "Character editor", Preview): three regions
/// used in turn, so the frames in flight never read what the next one writes, and the arena does not grow however many frames are drawn.
/// </summary>
public class MorphScratchTests
{
    static VulkanDevice? TryCreate()
    {
        try
        {
            return VulkanDevice.Create(new VulkanDeviceOptions());
        }
        catch (Exception e)
            when (e is VulkanException or DllNotFoundException or EntryPointNotFoundException or Silk.NET.Core.Loader.SymbolLoadingException)
        {
            return null;
        }
    }

    [Fact]
    [Slow]
    public void Scratch_rotates_over_three_regions_and_does_not_grow_the_arena()
    {
        using var d = TryCreate();

        Assert.SkipWhen(d is null, "No Vulkan 1.3 device");

        using var ctx = new GpuContext(d!);
        using var arena = new MorphArena(ctx);
        var deltas = new Vector4[40];
        uint a = arena.Scratch(deltas), b = arena.Scratch(deltas), c = arena.Scratch(deltas);
        int entries = arena.Entries;

        Assert.Equal(3, new[] { a, b, c }.Distinct().Count());
        Assert.True(b - a >= deltas.Length && c - b >= deltas.Length, "the regions overlap");

        for (int i = 0; i < 30; i++) Assert.Equal(new[] { a, b, c }[i % 3], arena.Scratch(deltas));

        Assert.Equal(entries, arena.Entries);
    }
}
