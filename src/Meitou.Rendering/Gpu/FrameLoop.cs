using System.Diagnostics;
using Meitou.Rendering.Gpu.Core;
using Silk.NET.Vulkan;
using VkSemaphore = Silk.NET.Vulkan.Semaphore;

namespace Meitou.Rendering.Gpu;

/// <summary>
/// The frame loop (phase 8 stage 3, docs/renderer-native.md 8.9): the context begins and submits frames itself. A frame is two primary
/// command buffers of the slot: the uploads (<see cref="GpuFrame.PreFrame"/>, submitted first, a full barrier at each end) and the frame's own
/// (<see cref="GpuFrame.Commands"/>). The order of commands is VkGl's (which drove the frame before): begin, uploads' barrier, the frame; at the
/// end the uploads' closing barrier and one submission.
/// </summary>
public sealed unsafe partial class GpuContext
{
    CommandPool[]? uploadPools;
    CommandBuffer[]? uploadBuffers;
    (QuerySlot Begin, QuerySlot End)[]? frameStamps;

    /// <summary>The last completed frame's GPU time: from the start of its uploads to the end of its last command (a frame ring late).</summary>
    public double GpuFrameMs { get; private set; }
    /// <summary><c>MEITOU_PASS_STATS=1</c> only (else 0): the last completed frame's pre-frame GPU time, from the start of its uploads to the end of
    /// the pre-frame command buffer (uploads, compute culls, grass kernels, impostor bakes), a frame ring late.</summary>
    public double PreFrameGpuMs { get; private set; }
    static bool PreFrameStamps => PassStats || SpikeLog.Enabled;
    static readonly bool PassStats = Environment.GetEnvironmentVariable("MEITOU_PASS_STATS") == "1";

    /// <summary>The first and the pre-frame-end timestamps of the frame submitted last (--log-spikes or MEITOU_PASS_STATS=1 only; the profiler keeps them to read the pre-frame GPU time when the frame has completed).</summary>
    internal (QuerySlot Begin, QuerySlot PreEnd) LastFrameStamps { get; private set; }
    QuerySlot[]? preFrameEnd;
    /// <summary>Stopwatch ticks spent waiting for a free frame slot and submitting, since the context was made.</summary>
    public long FenceWaitTicks { get; private set; }
    public long SubmitTicks { get; private set; }

    void EnsureUploadBuffers()
    {
        if (uploadPools is not null) return;
        int n = Device.Frames.Count;
        uploadPools = new CommandPool[n];
        uploadBuffers = new CommandBuffer[n];
        frameStamps = new (QuerySlot, QuerySlot)[n];
        for (int i = 0; i < n; i++)
        {
            var pi = new CommandPoolCreateInfo { SType = StructureType.CommandPoolCreateInfo, QueueFamilyIndex = Device.GraphicsFamily, Flags = CommandPoolCreateFlags.TransientBit };
            VulkanException.Check(Device.Vk.CreateCommandPool(Device.Device, &pi, null, out uploadPools[i]), "vkCreateCommandPool");
            var ai = new CommandBufferAllocateInfo { SType = StructureType.CommandBufferAllocateInfo, CommandPool = uploadPools[i], Level = CommandBufferLevel.Primary, CommandBufferCount = 1 };
            CommandBuffer cb;
            VulkanException.Check(Device.Vk.AllocateCommandBuffers(Device.Device, &ai, &cb), "vkAllocateCommandBuffers");
            uploadBuffers[i] = cb;
        }
    }

    /// <summary>Begins a frame (waits for its slot's fence); nothing when one is open.</summary>
    public void BeginFrame()
    {
        if (Frame.Open) return;
        EnsureUploadBuffers();
        long t0 = Stopwatch.GetTimestamp();
        var cmd = Device.Frames.BeginFrame();
        FenceWaitTicks += Stopwatch.GetTimestamp() - t0;
        int slot = Device.Frames.Slot;
        VulkanException.Check(Device.Vk.ResetCommandPool(Device.Device, uploadPools![slot], 0), "vkResetCommandPool");
        var upload = uploadBuffers![slot];
        var begin = new CommandBufferBeginInfo { SType = StructureType.CommandBufferBeginInfo, Flags = CommandBufferUsageFlags.OneTimeSubmitBit };
        VulkanException.Check(Device.Vk.BeginCommandBuffer(upload, &begin), "vkBeginCommandBuffer");
        // Uploads may overwrite what earlier frames still read (in-place texture strips): wait for everything before.
        FullBarrier(upload);
        Frame.Begin(cmd, upload);
        // The slot's previous frame has completed and its timestamps were collected (Frame.Begin): its GPU time.
        ref var stamps = ref frameStamps![slot];
        if (Frame.Timestamps.TryRead(stamps.Begin, out ulong b) && Frame.Timestamps.TryRead(stamps.End, out ulong e) && e >= b) GpuFrameMs = (e - b) / 1e6;
        if (PreFrameStamps && preFrameEnd is not null && Frame.Timestamps.TryRead(stamps.Begin, out ulong pb) && Frame.Timestamps.TryRead(preFrameEnd[slot], out ulong pe) && pe >= pb) PreFrameGpuMs = (pe - pb) / 1e6;
        stamps = (Frame.Timestamps.Allocate(), default);
        if (stamps.Begin.IsValid) Frame.PreFrame.Timestamp(Frame.Timestamps, stamps.Begin, PipelineStageFlags2.TopOfPipeBit);
    }

    /// <summary>Opens a frame where something must record and none is open (loading, uploads before the host began one, offscreen tools).</summary>
    public void EnsureFrame()
    {
        if (!Frame.Open) BeginFrame();
    }

    /// <summary>Ends the frame and submits it (the uploads first). <paramref name="wait"/> / <paramref name="signal"/>: the swapchain's semaphores.</summary>
    public void EndFrame(ReadOnlySpan<VkSemaphore> wait = default, ReadOnlySpan<PipelineStageFlags> waitStages = default, ReadOnlySpan<VkSemaphore> signal = default)
    {
        if (!Frame.Open) return;
        if (PassOpen) throw new InvalidOperationException("EndFrame with a host pass open");
        int slot = Device.Frames.Slot;
        var end = Frame.Timestamps.Allocate();
        if (end.IsValid)
        {
            Frame.Commands.Timestamp(Frame.Timestamps, end, PipelineStageFlags2.BottomOfPipeBit);
            frameStamps![slot].End = end;
        }
        if (PreFrameStamps)
        {
            // MEITOU_PASS_STATS=1: the pre-frame's own GPU time (uploads, the culls' and grass kernels, impostor bakes), read a frame ring later.
            preFrameEnd ??= new QuerySlot[Device.Frames.Count];
            preFrameEnd[slot] = Frame.Timestamps.Allocate();
            if (preFrameEnd[slot].IsValid) Frame.PreFrame.Timestamp(Frame.Timestamps, preFrameEnd[slot], PipelineStageFlags2.BottomOfPipeBit);
        }
        if (PreFrameStamps) LastFrameStamps = (frameStamps![slot].Begin, preFrameEnd![slot]);
        Frame.End();
        var upload = uploadBuffers![slot];
        FullBarrier(upload);
        VulkanException.Check(Device.Vk.EndCommandBuffer(upload), "vkEndCommandBuffer");
        long t0 = Stopwatch.GetTimestamp();
        Device.Frames.EndFrame(wait, waitStages, signal, before: upload);
        SubmitTicks += Stopwatch.GetTimestamp() - t0;
    }

    /// <summary>Submits what is recorded and waits for the GPU (readbacks); a frame is open again afterwards when one was before.</summary>
    public void Finish()
    {
        if (!Frame.Open) { Device.Frames.WaitAll(); return; }
        EndFrame();
        Device.Frames.WaitAll();
        BeginFrame();
    }

    /// <summary>A full memory barrier (all commands, all writes before visible to all reads and writes after), placed around native segments.</summary>
    public void FullBarrier(CommandBuffer cb)
    {
        var barrier = new MemoryBarrier2
        {
            SType = StructureType.MemoryBarrier2,
            SrcStageMask = PipelineStageFlags2.AllCommandsBit,
            SrcAccessMask = AccessFlags2.MemoryWriteBit,
            DstStageMask = PipelineStageFlags2.AllCommandsBit,
            DstAccessMask = AccessFlags2.MemoryReadBit | AccessFlags2.MemoryWriteBit,
        };
        var info = new DependencyInfo { SType = StructureType.DependencyInfo, MemoryBarrierCount = 1, PMemoryBarriers = &barrier };
        Device.Vk.CmdPipelineBarrier2(cb, &info);
    }

    void DisposeFrameLoop()
    {
        if (uploadPools is null) return;
        foreach (var p in uploadPools) Device.Vk.DestroyCommandPool(Device.Device, p, null);
        uploadPools = null;
    }
}
