using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Gpu.Core;
using Silk.NET.Vulkan;

namespace Meitou.Tests.Vulkan;

public unsafe class CoreTests(ITestOutputHelper output)
{
    static VulkanDevice? TryCreate(bool? validation = true, int frames = 2)
    {
        try
        {
            return VulkanDevice.Create(new VulkanDeviceOptions { Validation = validation, FramesInFlight = frames });
        }
        catch (Exception e) when (e is VulkanException or DllNotFoundException or EntryPointNotFoundException
            or Silk.NET.Core.Loader.SymbolLoadingException)
        {
            return null;
        }
    }

    static void ExpectClean(VulkanDevice d)
    {
        Assert.True(d.ValidationErrors == 0, "Validation errors:\n" + string.Join("\n", d.ValidationLog));
    }

    [Fact]
    [Slow]
    public void Allocator_mixed_sizes_coalesce_on_free()
    {
        using var d = TryCreate();
        Assert.SkipWhen(d is null, "No Vulkan 1.3 device");
        var rng = new Random(1234);
        var buffers = new List<BufferAllocation>();
        var images = new List<ImageAllocation>();
        const BufferUsageFlags usage = BufferUsageFlags.TransferSrcBit | BufferUsageFlags.TransferDstBit | BufferUsageFlags.UniformBufferBit;

        for (int i = 0; i < 300; i++)
        {
            ulong size = (ulong)rng.Next(1, 3) switch
            {
                1 => (ulong)rng.Next(16, 70_000),
                _ => (ulong)rng.Next(70_000, 3_000_000),
            };
            var kind = (i % 5) switch { 0 => MemoryKind.Upload, 1 => MemoryKind.Readback, _ => MemoryKind.DeviceLocal };
            var b = d!.Allocator.CreateBuffer(size, usage, kind, "test buffer " + i);
            Assert.Equal(kind != MemoryKind.DeviceLocal, b.Mapped != null || b.Allocation.MappedPointer != 0);
            buffers.Add(b);

            if (i % 3 == 0)
            {
                int w = rng.Next(4, 600), h = rng.Next(4, 600);
                var info = new ImageCreateInfo
                {
                    SType = StructureType.ImageCreateInfo,
                    ImageType = ImageType.Type2D,
                    Format = GlConventions.VkFormat(InternalFormat.Rgba8),
                    Extent = new Extent3D((uint)w, (uint)h, 1),
                    MipLevels = 1,
                    ArrayLayers = 1,
                    Samples = SampleCountFlags.Count1Bit,
                    Tiling = ImageTiling.Optimal,
                    Usage = ImageUsageFlags.TransferDstBit | ImageUsageFlags.SampledBit,
                    InitialLayout = ImageLayout.Undefined,
                };
                images.Add(d.Allocator.CreateImage(in info, MemoryKind.DeviceLocal, "test image " + i));
            }

            // free some along the way, in scattered order
            if (i % 7 == 6)
            {
                int k = rng.Next(buffers.Count);
                d.Allocator.Free(buffers[k]);
                buffers.RemoveAt(k);
            }
        }

        // One request above half a block: its own dedicated block, gone again on free.
        var big = d!.Allocator.CreateBuffer(d.Allocator.BlockSize / 2 + 1, BufferUsageFlags.StorageBufferBit, MemoryKind.DeviceLocal, "big");
        Assert.Contains(d.Allocator.GetBlocks(), b => b.Dedicated);
        d.Allocator.Free(big);
        Assert.DoesNotContain(d.Allocator.GetBlocks(), b => b.Dedicated);

        Assert.True(d.Allocator.BlockCount > 0);
        Assert.True(d.Allocator.TotalUsedBytes > 0 && d.Allocator.TotalAllocatedBytes >= d.Allocator.TotalUsedBytes);
        output.WriteLine($"{d.Allocator.BlockCount} blocks, {d.Allocator.TotalAllocatedBytes >> 20} MB held, "
            + $"{d.Allocator.TotalUsedBytes >> 10} KB used");

        foreach (var b in buffers.OrderBy(_ => rng.Next())) d.Allocator.Free(b);
        foreach (var i in images.OrderBy(_ => rng.Next())) d.Allocator.Free(i);

        Assert.Equal(0, d.Allocator.AllocationCount);
        Assert.Equal(0UL, d.Allocator.TotalUsedBytes);
        var blocks = d.Allocator.GetBlocks();
        output.WriteLine(string.Join("\n", blocks));

        foreach (var group in blocks.GroupBy(b => (b.MemoryTypeIndex, b.Optimal)))
        {
            var one = Assert.Single(group);
            Assert.Equal(0UL, one.Used);
            Assert.Equal(1, one.FreeRanges);
        }

        ExpectClean(d);
    }
}
