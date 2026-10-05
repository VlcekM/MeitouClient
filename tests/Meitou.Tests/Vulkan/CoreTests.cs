using Meitou.Rendering.Vulkan.Core;
using Silk.NET.Vulkan;
using Xunit.Sdk;

namespace Meitou.Tests.Vulkan;

public unsafe class CoreTests(ITestOutputHelper output)
{
    static VulkanDevice? TryCreate(bool? validation = true, int frames = 2)
    {
        try
        {
            return VulkanDevice.Create(new VulkanDeviceOptions { Validation = validation, FramesInFlight = frames });
        }
        catch (Exception e) when (e is VulkanException or DllNotFoundException or EntryPointNotFoundException or Silk.NET.Core.Loader.SymbolLoadingException)
        {
            return null;
        }
    }

    static void ExpectClean(VulkanDevice d)
    {
        Assert.True(d.ValidationErrors == 0, "Validation errors:\n" + string.Join("\n", d.ValidationLog));
    }

    [Fact]
    public void Device_reports_features()
    {
        using var d = TryCreate();
        Assert.SkipWhen(d is null, "No Vulkan 1.3 device");
        output.WriteLine(d!.DescribeFeatures());
        Assert.True(d.HasDynamicRendering);
        Assert.True(d.HasSynchronization2);
        Assert.True(d.HasTimelineSemaphore);
        Assert.NotEmpty(d.DeviceName);
        Assert.NotEqual(0u, d.Properties.Limits.MinUniformBufferOffsetAlignment);
        Assert.False(d.HasSwapchain);
        if (d.HasPushDescriptor)
        {
            Assert.True(d.MaxPushDescriptors > 0);
        }
        ExpectClean(d);
    }

    [Fact]
    public void Allocator_mixed_sizes_coalesce_on_free()
    {
        using var d = TryCreate();
        Assert.SkipWhen(d is null, "No Vulkan 1.3 device");
        var rng = new Random(1234);
        var buffers = new List<GpuBuffer>();
        var images = new List<GpuImage>();
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
                    Format = Format.R8G8B8A8Unorm,
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
        output.WriteLine($"{d.Allocator.BlockCount} blocks, {d.Allocator.TotalAllocatedBytes >> 20} MB held, {d.Allocator.TotalUsedBytes >> 10} KB used");

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

    [Fact]
    public void Clear_image_and_read_back()
    {
        using var d = TryCreate();
        Assert.SkipWhen(d is null, "No Vulkan 1.3 device");
        var info = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = Format.R8G8B8A8Unorm,
            Extent = new Extent3D(16, 16, 1),
            MipLevels = 1,
            ArrayLayers = 1,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = ImageUsageFlags.TransferDstBit | ImageUsageFlags.TransferSrcBit,
            InitialLayout = ImageLayout.Undefined,
        };
        var image = d!.Allocator.CreateImage(in info, MemoryKind.DeviceLocal, "clear target");
        var readback = d.Allocator.CreateBuffer(16 * 16 * 4, BufferUsageFlags.TransferDstBit, MemoryKind.Readback, "readback");
        Assert.NotEqual(IntPtr.Zero, readback.Allocation.MappedPointer);
        new Span<byte>(readback.Mapped, 16 * 16 * 4).Fill(0xCD);

        var vk = d.Vk;
        var cb = d.BeginImmediate();
        var range = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1);
        void Barrier(ImageLayout from, ImageLayout to, PipelineStageFlags2 srcStage, AccessFlags2 srcAccess, PipelineStageFlags2 dstStage, AccessFlags2 dstAccess)
        {
            var b = new ImageMemoryBarrier2
            {
                SType = StructureType.ImageMemoryBarrier2,
                SrcStageMask = srcStage,
                SrcAccessMask = srcAccess,
                DstStageMask = dstStage,
                DstAccessMask = dstAccess,
                OldLayout = from,
                NewLayout = to,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = image.Image,
                SubresourceRange = range,
            };
            var dep = new DependencyInfo { SType = StructureType.DependencyInfo, ImageMemoryBarrierCount = 1, PImageMemoryBarriers = &b };
            vk.CmdPipelineBarrier2(cb, in dep);
        }
        Barrier(ImageLayout.Undefined, ImageLayout.TransferDstOptimal, PipelineStageFlags2.None, AccessFlags2.None,
            PipelineStageFlags2.ClearBit, AccessFlags2.TransferWriteBit);
        var color = new ClearColorValue { Float32_0 = 0.25f, Float32_1 = 0.5f, Float32_2 = 0.75f, Float32_3 = 1f };
        var clearRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1);
        vk.CmdClearColorImage(cb, image.Image, ImageLayout.TransferDstOptimal, &color, 1, &clearRange);
        Barrier(ImageLayout.TransferDstOptimal, ImageLayout.TransferSrcOptimal, PipelineStageFlags2.ClearBit, AccessFlags2.TransferWriteBit,
            PipelineStageFlags2.CopyBit, AccessFlags2.TransferReadBit);
        var region = new BufferImageCopy
        {
            ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            ImageExtent = new Extent3D(16, 16, 1),
        };
        vk.CmdCopyImageToBuffer(cb, image.Image, ImageLayout.TransferSrcOptimal, readback.Buffer, 1, &region);
        d.EndImmediate(cb);
        d.Allocator.Invalidate(readback.Allocation);

        var bytes = new ReadOnlySpan<byte>(readback.Mapped, 16 * 16 * 4);
        for (int p = 0; p < 16 * 16; p++)
        {
            Assert.InRange(bytes[p * 4 + 0], 63, 65);
            Assert.InRange(bytes[p * 4 + 1], 127, 129);
            Assert.InRange(bytes[p * 4 + 2], 190, 192);
            Assert.Equal(255, bytes[p * 4 + 3]);
        }
        d.Allocator.Free(image);
        d.Allocator.Free(readback);
        ExpectClean(d);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void Frame_ring_runs_deletions_after_their_fence(int inFlight)
    {
        using var d = TryCreate(true, inFlight);
        Assert.SkipWhen(d is null, "No Vulkan 1.3 device");
        var frames = d!.Frames;
        var ran = new List<long>();
        var queued = new Dictionary<long, long>();
        Assert.Equal(0, frames.FrameNumber);
        for (int i = 0; i < 10; i++)
        {
            var cb = frames.BeginFrame();
            long frame = frames.FrameNumber;
            Assert.Equal(i + 1, frame);
            Assert.Equal(i % inFlight, frames.Slot);
            Assert.NotEqual(0u, (uint)cb.Handle);
            // Everything queued at least `inFlight` frames ago has run by now, and nothing newer.
            foreach (var (q, _) in queued)
            {
                Assert.Equal(q <= frame - inFlight, ran.Contains(q));
            }
            long f = frame;
            frames.DeferDelete(() => ran.Add(f));
            queued[f] = f;
            // The deletion queued in a frame has not run before that frame's fence signals.
            Assert.DoesNotContain(f, ran);
            frames.EndFrame();
        }
        // Deleting outside a frame goes on the last submitted frame's list.
        long tag = 1000;
        frames.DeferDelete(() => ran.Add(tag));
        Assert.DoesNotContain(tag, ran);
        frames.WaitAll();
        Assert.Equal(11, ran.Count);
        Assert.Contains(tag, ran);
        Assert.Equal(10, frames.CompletedFrame);
        // With nothing in flight it runs at once.
        long now = 2000;
        frames.DeferDelete(() => ran.Add(now));
        Assert.Contains(now, ran);
        ExpectClean(d);
    }

    [Fact]
    public void Timeline_semaphore_signals_and_waits()
    {
        using var d = TryCreate();
        Assert.SkipWhen(d is null, "No Vulkan 1.3 device");
        using var sem = new TimelineSemaphore(d!, 0, "test timeline");
        Assert.Equal(0UL, sem.Value);
        Assert.False(sem.Wait(1, 1_000_000));
        sem.Signal(5);
        Assert.Equal(5UL, sem.Value);
        Assert.True(sem.Wait(5, 1_000_000));
        ExpectClean(d!);
    }

    [Fact]
    public void Pipeline_cache_round_trips_and_a_bad_file_is_ignored()
    {
        var path = Path.Combine(Path.GetTempPath(), "meitou-vk-test-" + Guid.NewGuid().ToString("N") + ".cache");
        try
        {
            File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33 });
            VulkanDevice? d;
            try
            {
                d = VulkanDevice.Create(new VulkanDeviceOptions { PipelineCachePath = path });
            }
            catch (VulkanException)
            {
                d = null;
            }
            Assert.SkipWhen(d is null, "No Vulkan 1.3 device");
            Assert.NotEqual(0UL, d!.PipelineCache.Handle);
            ExpectClean(d);
            d.Dispose();
            var saved = File.ReadAllBytes(path);
            Assert.True(saved.Length >= 32);
            Assert.NotEqual(1, saved[0]);
            // Loads again.
            using var d2 = VulkanDevice.Create(new VulkanDeviceOptions { PipelineCachePath = path });
            ExpectClean(d2);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Dispose_leaves_no_validation_errors()
    {
        var d = TryCreate();
        Assert.SkipWhen(d is null, "No Vulkan 1.3 device");
        d!.Allocator.CreateBuffer(1024, BufferUsageFlags.UniformBufferBit, MemoryKind.Upload, "leaked on purpose");
        d.Frames.BeginFrame();
        d.Frames.EndFrame();
        d.Frames.DeferDelete(() => { });
        d.Dispose();
        Assert.True(d.ValidationErrors == 0, string.Join("\n", d.ValidationLog));
    }
}
