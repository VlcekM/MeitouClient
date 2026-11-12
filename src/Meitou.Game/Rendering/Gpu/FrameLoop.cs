using System.Diagnostics;
using Meitou.Game.Rendering.Gpu.Core;
using Meitou.Game.Rendering.Profiling;
using Silk.NET.Vulkan;
using VkSemaphore = Silk.NET.Vulkan.Semaphore;

namespace Meitou.Game.Rendering.Gpu;

/// <summary>
/// The frame loop (phase 8 stage 3, docs/renderer-native.md 8.9): the context begins and submits frames itself. A frame is two primary command
/// buffers of the slot: the uploads (<see cref="GpuFrame.PreFrame"/>, submitted first, a full barrier at each end) and the frame's own
/// (<see cref="GpuFrame.Commands"/>). The order of commands is VkGl's (which drove the frame before): begin, uploads' barrier, the frame; at the end
/// the uploads' closing barrier and one submission.
/// </summary>
public sealed unsafe partial class GpuContext
{
    /// <summary>
    /// The upload command pool of each frame slot (reset when the slot's frame begins).
    /// </summary>
    CommandPool[]? _uploadPools;
    /// <summary>
    /// The upload command buffer of each frame slot (the frame's pre-frame commands).
    /// </summary>
    CommandBuffer[]? _uploadBuffers;

    /// <summary>
    /// Per slot: the upload buffers a <see cref="SubmitAndWait"/> went on in, and how many this frame used.
    /// </summary>
    List<CommandBuffer>[]? _uploadExtras;
    /// <summary>
    /// Per slot: how many of <see cref="_uploadExtras"/> this frame used.
    /// </summary>
    int[]? _uploadExtrasUsed;
    /// <summary>
    /// Per slot: the frame's first and last timestamps.
    /// </summary>
    (QuerySlot Begin, QuerySlot End)[]? _frameStamps;
    /// <summary>
    /// Per slot: the pre-frame buffer's end timestamp (only while <see cref="PreFrameStamps"/>).
    /// </summary>
    QuerySlot[]? _preFrameEnd;
    /// <summary>
    /// The pass marker of the upload buffer being recorded (<see cref="PassMarkers"/>; 0 when not tracking).
    /// </summary>
    uint _uploadMarker;

    /// <summary>
    /// The last completed frame's GPU time: from the start of its uploads to the end of its last command (a frame ring late).
    /// </summary>
    public double GpuFrameMs { get; private set; }

    /// <summary>
    /// <c>MEITOU_PASS_STATS=1</c> only (else 0): the last completed frame's pre-frame GPU time, from the start of its uploads to the end of the
    /// pre-frame command buffer (uploads, compute culls, grass kernels, impostor bakes), a frame ring late.
    /// </summary>
    public double PreFrameGpuMs { get; private set; }

    /// <summary>
    /// Set by the first <see cref="FrameProfiler"/>: the pre-frame buffer gets its end stamp, which the profiler needs to time what the buffer holds.
    /// </summary>
    internal static bool ProfilePreFrame;

    /// <summary>
    /// <c>MEITOU_PASS_STATS=1</c>: the pre-frame GPU time is measured.
    /// </summary>
    static readonly bool PassStats = Environment.GetEnvironmentVariable("MEITOU_PASS_STATS") == "1";

    /// <summary>
    /// Diagnostic (issue #108), <c>MEITOU_DIAG_SUBMIT=1</c>: the host calls <see cref="SubmitAndWait"/> after the world and before the present.
    /// </summary>
    public static readonly bool DiagSubmit = Environment.GetEnvironmentVariable("MEITOU_DIAG_SUBMIT") == "1";

    /// <summary>
    /// Diagnostic (issue #108), <c>MEITOU_DIAG_IDLE=1</c>: every frame waits for all frames in flight before it begins (the CPU never records while
    /// the GPU runs an earlier frame).
    /// </summary>
    public static readonly bool DiagIdle = Environment.GetEnvironmentVariable("MEITOU_DIAG_IDLE") == "1";

    /// <summary>
    /// Diagnostic (issue #319), <c>MEITOU_DIAG_NO_MATERIAL_CACHE=1</c>: the terrain material cache starts off (every pixel shades the layers).
    /// </summary>
    public static readonly bool DiagNoMaterialCache = Environment.GetEnvironmentVariable("MEITOU_DIAG_NO_MATERIAL_CACHE") == "1";

    /// <summary>
    /// Diagnostic (issue #319), <c>MEITOU_DIAG_NO_GROW=1</c>: the terrain near layer arrays and the grass store keep the size they were made with (no
    /// growth for kept places or a longer grass distance).
    /// </summary>
    public static readonly bool DiagNoGrow = Environment.GetEnvironmentVariable("MEITOU_DIAG_NO_GROW") == "1";

    /// <summary>
    /// The first and the pre-frame-end timestamps of the frame submitted last (--log-spikes or MEITOU_PASS_STATS=1 only; the profiler keeps them to
    /// read the pre-frame GPU time when the frame has completed).
    /// </summary>
    internal (QuerySlot Begin, QuerySlot PreEnd) LastFrameStamps { get; private set; }

    /// <summary>
    /// The current (or last submitted) frame's first and last timestamps: its own GPU time, readable once its slot has come round; End is set by
    /// <see cref="EndFrame"/>.
    /// </summary>
    internal (QuerySlot Begin, QuerySlot End) CurrentFrameStamps => _frameStamps is null ? default : _frameStamps[Device.Frames.Slot];

    /// <summary>
    /// Stopwatch ticks spent waiting for a free frame slot, since the context was made.
    /// </summary>
    public long FenceWaitTicks { get; private set; }

    /// <summary>
    /// Stopwatch ticks spent submitting frames, since the context was made.
    /// </summary>
    public long SubmitTicks { get; private set; }

    /// <summary>
    /// Whether the pre-frame buffer gets its end timestamp (pass stats, the spike log or the profiler).
    /// </summary>
    static bool PreFrameStamps => PassStats || SpikeLog.Enabled || ProfilePreFrame;

    /// <summary>
    /// Begins a frame (waits for its slot's fence); nothing when one is open.
    /// </summary>
    public void BeginFrame()
    {
        if (Frame.Open) return;

        long profileBegin = UploadProfile.Begin();
        EnsureUploadBuffers();
        long t0 = Stopwatch.GetTimestamp();
        if (DiagIdle) Device.Frames.WaitAll();

        var cmd = Device.Frames.BeginFrame();
        FenceWaitTicks += Stopwatch.GetTimestamp() - t0;
        UploadProfile.End(UploadProfile.Part.FrameWait, t0);
        int slot = Device.Frames.Slot;
        VulkanException.Check(Device.Vk.ResetCommandPool(Device.Device, _uploadPools![slot], 0), "vkResetCommandPool");
        _uploadExtrasUsed![slot] = 0;
        var upload = _uploadBuffers![slot];
        Device.Faults.Markers?.BeginFrame();
        BeginUploads(upload);
        Frame.Begin(cmd, upload);
        ReadPreviousFrameTimes(slot);
        StartFrameStamps(slot);
        UploadProfile.End(UploadProfile.Part.FrameBegin, profileBegin);
    }

    /// <summary>
    /// The slot's previous frame has completed and its timestamps were collected (Frame.Begin): its GPU time.
    /// </summary>
    void ReadPreviousFrameTimes(int slot)
    {
        ref var stamps = ref _frameStamps![slot];
        if (Frame.Timestamps.TryRead(stamps.Begin, out ulong b) && Frame.Timestamps.TryRead(stamps.End, out ulong e) && e >= b)
            GpuFrameMs = (e - b) / 1e6;

        if (PreFrameStamps && _preFrameEnd is not null && Frame.Timestamps.TryRead(stamps.Begin, out ulong pb)
            && Frame.Timestamps.TryRead(_preFrameEnd[slot], out ulong pe) && pe >= pb)
            PreFrameGpuMs = (pe - pb) / 1e6;
    }

    /// <summary>
    /// Allocates the frame's first timestamp and writes it at the start of the pre-frame buffer.
    /// </summary>
    void StartFrameStamps(int slot)
    {
        ref var stamps = ref _frameStamps![slot];
        stamps = (Frame.Timestamps.Allocate(), default);
        if (stamps.Begin.IsValid) Frame.PreFrame.Timestamp(Frame.Timestamps, stamps.Begin, PipelineStageFlags2.TopOfPipeBit);
    }

    /// <summary>
    /// Opens a frame where something must record and none is open (loading, uploads before the host began one, offscreen tools).
    /// </summary>
    public void EnsureFrame()
    {
        if (!Frame.Open) BeginFrame();
    }

    /// <summary>
    /// Ends the frame and submits it (the uploads first). <paramref name="wait"/> / <paramref name="signal"/>: the swapchain's semaphores.
    /// </summary>
    public void EndFrame(ReadOnlySpan<VkSemaphore> wait = default, ReadOnlySpan<PipelineStageFlags> waitStages = default,
        ReadOnlySpan<VkSemaphore> signal = default)
    {
        if (!Frame.Open) return;
        if (PassOpen) throw new InvalidOperationException("EndFrame with a host pass open");

        StageClock.BeginSubmission();
        int slot = Device.Frames.Slot;
        WriteEndStamps(slot);
        Frame.End();
        var upload = Frame.UploadCommands;
        EndUploads(upload);
        long t0 = Stopwatch.GetTimestamp();
        Device.Frames.EndFrame(wait, waitStages, signal, before: upload);
        SubmitTicks += Stopwatch.GetTimestamp() - t0;
        StageClock.Lap(StageClock.Submit, gpuTimestamp: false);
    }

    /// <summary>
    /// Writes the frame's last timestamp, and the pre-frame buffer's end timestamp while <see cref="PreFrameStamps"/>.
    /// </summary>
    void WriteEndStamps(int slot)
    {
        var end = Frame.Timestamps.Allocate();
        if (end.IsValid)
        {
            Frame.Commands.Timestamp(Frame.Timestamps, end, PipelineStageFlags2.BottomOfPipeBit);
            _frameStamps![slot].End = end;
        }

        if (PreFrameStamps)
        {
            // MEITOU_PASS_STATS=1: the pre-frame's own GPU time (uploads, the culls' and grass kernels, impostor bakes), read a frame ring later.
            _preFrameEnd ??= new QuerySlot[Device.Frames.Count];
            _preFrameEnd[slot] = Frame.Timestamps.Allocate();
            if (_preFrameEnd[slot].IsValid) Frame.PreFrame.Timestamp(Frame.Timestamps, _preFrameEnd[slot], PipelineStageFlags2.BottomOfPipeBit);

            LastFrameStamps = (_frameStamps![slot].Begin, _preFrameEnd[slot]);
        }
    }

    /// <summary>
    /// Begins recording an upload command buffer, with a full barrier first.
    /// </summary>
    void BeginUploads(CommandBuffer upload)
    {
        var begin = new CommandBufferBeginInfo { SType = StructureType.CommandBufferBeginInfo, Flags = CommandBufferUsageFlags.OneTimeSubmitBit };
        VulkanException.Check(Device.Vk.BeginCommandBuffer(upload, &begin), "vkBeginCommandBuffer");

        // Uploads may overwrite what earlier frames still read (in-place texture strips): wait for everything before.
        FullBarrier(upload);
        _uploadMarker = Device.Faults.Markers?.Begin(upload, "pre-frame") ?? 0;
    }

    /// <summary>
    /// Ends an upload command buffer, with a full barrier last.
    /// </summary>
    void EndUploads(CommandBuffer upload)
    {
        Device.Faults.Markers?.End(upload, _uploadMarker);
        FullBarrier(upload);
        VulkanException.Check(Device.Vk.EndCommandBuffer(upload), "vkEndCommandBuffer");
    }

    /// <summary>
    /// Diagnostic (<see cref="DiagSubmit"/>, issue #108): submits the open frame's uploads and commands so far and waits for the GPU to finish them;
    /// the frame goes on in fresh command buffers of the same slot (same constants, same swapchain image). Nothing outside a frame or inside a pass
    /// or segment.
    /// </summary>
    public void SubmitAndWait()
    {
        if (!Frame.Open || PassOpen || SegmentOpen) return;

        int slot = Device.Frames.Slot;
        var upload = Frame.UploadCommands;
        EndUploads(upload);
        Bindless?.EndFrame();   // what the frame registered so far, into its set before the submit
        var commands = Device.Frames.SubmitAndWait(before: upload);
        var extras = _uploadExtras![slot];
        if (_uploadExtrasUsed![slot] == extras.Count) extras.Add(AllocateUploadBuffer(_uploadPools![slot]));

        var next = extras[_uploadExtrasUsed[slot]++];
        BeginUploads(next);
        Frame.Continue(commands, next);
    }

    /// <summary>
    /// Submits what is recorded and waits for the GPU (readbacks); a frame is open again afterwards when one was before.
    /// </summary>
    public void Finish()
    {
        if (!Frame.Open)
        {
            Device.Frames.WaitAll();
            return;
        }

        EndFrame();
        Device.Frames.WaitAll();
        BeginFrame();
    }

    /// <summary>
    /// A full memory barrier (all commands, all writes before visible to all reads and writes after), placed around native segments.
    /// </summary>
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

    /// <summary>
    /// Makes each frame slot's upload pool and buffer and the per-slot tables, on the first frame.
    /// </summary>
    void EnsureUploadBuffers()
    {
        if (_uploadPools is not null) return;

        int n = Device.Frames.Count;
        _uploadPools = new CommandPool[n];
        _uploadBuffers = new CommandBuffer[n];
        _uploadExtras = [.. Enumerable.Range(0, n).Select(_ => new List<CommandBuffer>())];
        _uploadExtrasUsed = new int[n];
        _frameStamps = new (QuerySlot, QuerySlot)[n];
        for (int i = 0; i < n; i++)
        {
            var pi = new CommandPoolCreateInfo
            {
                SType = StructureType.CommandPoolCreateInfo,
                QueueFamilyIndex = Device.GraphicsFamily,
                Flags = CommandPoolCreateFlags.TransientBit,
            };
            VulkanException.Check(Device.Vk.CreateCommandPool(Device.Device, &pi, null, out _uploadPools[i]), "vkCreateCommandPool");
            _uploadBuffers[i] = AllocateUploadBuffer(_uploadPools[i]);
        }
    }

    /// <summary>
    /// A new primary command buffer from <paramref name="pool"/>.
    /// </summary>
    CommandBuffer AllocateUploadBuffer(CommandPool pool)
    {
        var ai = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = pool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = 1,
        };
        CommandBuffer cb;
        VulkanException.Check(Device.Vk.AllocateCommandBuffers(Device.Device, &ai, &cb), "vkAllocateCommandBuffers");
        return cb;
    }

    /// <summary>
    /// Destroys the upload command pools (and with them their buffers).
    /// </summary>
    void DisposeFrameLoop()
    {
        if (_uploadPools is null) return;

        foreach (var p in _uploadPools) Device.Vk.DestroyCommandPool(Device.Device, p, null);
        _uploadPools = null;
    }
}
