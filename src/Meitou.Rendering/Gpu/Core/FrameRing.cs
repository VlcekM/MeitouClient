using Silk.NET.Vulkan;

namespace Meitou.Rendering.Gpu.Core;

/// <summary>
/// N frames in flight. Each slot has a transient command pool, one primary command buffer, a fence and a deletion
/// list. Frame numbers start at 1 (0 = no frame yet). Call <see cref="BeginFrame"/> / <see cref="EndFrame"/> from one
/// thread; <see cref="DeferDelete"/> may be called from any thread.
/// </summary>
public sealed unsafe class FrameRing : IDisposable
{
    sealed class FrameSlot
    {
        public CommandPool Pool;
        public CommandBuffer Cmd;
        public Fence Fence;
        public List<Action> Deletions = new();
        public bool InFlight;
        public long Frame;
    }

    readonly VulkanDevice dev;
    readonly Vk vk;
    readonly FrameSlot[] slots;
    readonly object gate = new();
    int lastSubmitted = -1;
    bool inFrame;
    bool disposed;

    internal FrameRing(VulkanDevice dev, int count)
    {
        this.dev = dev;
        vk = dev.Vk;
        slots = new FrameSlot[count];
        for (int i = 0; i < count; i++)
        {
            var s = new FrameSlot();
            var pi = new CommandPoolCreateInfo
            {
                SType = StructureType.CommandPoolCreateInfo,
                Flags = CommandPoolCreateFlags.TransientBit,
                QueueFamilyIndex = dev.GraphicsFamily,
            };
            VulkanException.Check(vk.CreateCommandPool(dev.Device, in pi, null, out s.Pool), "vkCreateCommandPool");
            var ai = new CommandBufferAllocateInfo
            {
                SType = StructureType.CommandBufferAllocateInfo,
                CommandPool = s.Pool,
                Level = CommandBufferLevel.Primary,
                CommandBufferCount = 1,
            };
            VulkanException.Check(vk.AllocateCommandBuffers(dev.Device, in ai, out s.Cmd), "vkAllocateCommandBuffers");
            var fi = new FenceCreateInfo { SType = StructureType.FenceCreateInfo };
            VulkanException.Check(vk.CreateFence(dev.Device, in fi, null, out s.Fence), "vkCreateFence");
            slots[i] = s;
        }
    }

    public int Count => slots.Length;
    /// <summary>The current (or last begun) frame; 0 before the first.</summary>
    public long FrameNumber { get; private set; }
    /// <summary>The slot of the current frame (FrameNumber-1 modulo the count).</summary>
    public int Slot { get; private set; }
    /// <summary>The newest frame whose fence has been seen signalled (all its deletions have run).</summary>
    public long CompletedFrame { get; private set; }
    public bool InFrame => inFrame;
    public CommandBuffer Current => slots[Slot].Cmd;

    /// <summary>Waits for this slot's frame from N frames ago, runs its deletions, resets and begins the command buffer.</summary>
    public CommandBuffer BeginFrame()
    {
        if (inFrame)
        {
            throw new InvalidOperationException("BeginFrame called twice without EndFrame");
        }
        long frame = FrameNumber + 1;
        int index = (int)((frame - 1) % slots.Length);
        var s = slots[index];
        if (s.InFlight)
        {
            var fence = s.Fence;
            VulkanException.Check(vk.WaitForFences(dev.Device, 1, in fence, true, ulong.MaxValue), "vkWaitForFences");
            Retire(s);
        }
        else
        {
            RunDeletions(s);
        }
        VulkanException.Check(vk.ResetCommandPool(dev.Device, s.Pool, 0), "vkResetCommandPool");
        var bi = new CommandBufferBeginInfo { SType = StructureType.CommandBufferBeginInfo, Flags = CommandBufferUsageFlags.OneTimeSubmitBit };
        VulkanException.Check(vk.BeginCommandBuffer(s.Cmd, in bi), "vkBeginCommandBuffer");
        FrameNumber = frame;
        Slot = index;
        s.Frame = frame;
        inFrame = true;
        return s.Cmd;
    }

    /// <summary>Ends the command buffer and submits it (graphics queue) with this slot's fence. <paramref name="before"/>, when given, is an
    /// already ended command buffer submitted ahead of the frame's own in the same submission (uploads).</summary>
    public void EndFrame(ReadOnlySpan<Silk.NET.Vulkan.Semaphore> wait = default, ReadOnlySpan<PipelineStageFlags> waitStages = default,
        ReadOnlySpan<Silk.NET.Vulkan.Semaphore> signal = default, CommandBuffer before = default)
    {
        if (!inFrame)
        {
            throw new InvalidOperationException("EndFrame without BeginFrame");
        }
        if (wait.Length != waitStages.Length)
        {
            throw new ArgumentException("wait and waitStages must have the same length");
        }
        var s = slots[Slot];
        VulkanException.Check(vk.EndCommandBuffer(s.Cmd), "vkEndCommandBuffer");
        var fence = s.Fence;
        VulkanException.Check(vk.ResetFences(dev.Device, 1, in fence), "vkResetFences");
        var cmds = stackalloc CommandBuffer[2];
        uint cmdCount = 0;
        if (before.Handle != 0) cmds[cmdCount++] = before;
        cmds[cmdCount++] = s.Cmd;
        fixed (Silk.NET.Vulkan.Semaphore* pw = wait)
        fixed (PipelineStageFlags* pws = waitStages)
        fixed (Silk.NET.Vulkan.Semaphore* ps = signal)
        {
            var si = new SubmitInfo
            {
                SType = StructureType.SubmitInfo,
                WaitSemaphoreCount = (uint)wait.Length,
                PWaitSemaphores = pw,
                PWaitDstStageMask = pws,
                CommandBufferCount = cmdCount,
                PCommandBuffers = cmds,
                SignalSemaphoreCount = (uint)signal.Length,
                PSignalSemaphores = ps,
            };
            lock (dev.QueueLock)
            {
                VulkanException.Check(vk.QueueSubmit(dev.GraphicsQueue, 1, in si, fence), "vkQueueSubmit");
            }
        }
        lock (gate)
        {
            s.InFlight = true;
            lastSubmitted = Slot;
            inFrame = false;
        }
    }

    /// <summary>
    /// Runs <paramref name="destroy"/> once every frame that could use the resource has finished: on the current
    /// slot's list inside a frame, else on the most recently submitted slot's list, else at once.
    /// </summary>
    public void DeferDelete(Action destroy)
    {
        lock (gate)
        {
            if (!disposed)
            {
                if (inFrame)
                {
                    slots[Slot].Deletions.Add(destroy);
                    return;
                }
                if (lastSubmitted >= 0 && slots[lastSubmitted].InFlight)
                {
                    slots[lastSubmitted].Deletions.Add(destroy);
                    return;
                }
            }
        }
        // Nothing in flight (or the last frame was already retired by WaitAll): safe now.
        destroy();
    }

    /// <summary>Waits for all submitted frames and runs all pending deletions.</summary>
    public void WaitAll()
    {
        foreach (var s in slots)
        {
            if (s.InFlight)
            {
                var fence = s.Fence;
                VulkanException.Check(vk.WaitForFences(dev.Device, 1, in fence, true, ulong.MaxValue), "vkWaitForFences");
                Retire(s);
            }
            else
            {
                RunDeletions(s);
            }
        }
    }

    void Retire(FrameSlot s)
    {
        lock (gate)
        {
            s.InFlight = false;
            if (s.Frame > CompletedFrame)
            {
                CompletedFrame = s.Frame;
            }
        }
        RunDeletions(s);
    }

    void RunDeletions(FrameSlot s)
    {
        while (true)
        {
            List<Action> list;
            lock (gate)
            {
                if (s.Deletions.Count == 0)
                {
                    return;
                }
                list = s.Deletions;
                s.Deletions = new List<Action>();
            }
            foreach (var a in list)
            {
                a();
            }
        }
    }

    /// <summary>The device must be idle. Runs the remaining deletions and destroys the per-frame objects.</summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        if (inFrame)
        {
            // Drop the half-recorded frame; the pool reset below discards it.
            inFrame = false;
        }
        foreach (var s in slots)
        {
            s.InFlight = false;
            RunDeletions(s);
        }
        lock (gate)
        {
            disposed = true;
        }
        foreach (var s in slots)
        {
            vk.DestroyFence(dev.Device, s.Fence, null);
            vk.DestroyCommandPool(dev.Device, s.Pool, null);
        }
    }
}
