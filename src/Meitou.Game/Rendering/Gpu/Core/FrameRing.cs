using Silk.NET.Vulkan;

namespace Meitou.Game.Rendering.Gpu.Core;

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
        /// <summary>
        /// The primary being recorded: <see cref="Cmd"/>, or after a <see cref="SubmitAndWait"/> one of <see cref="Extra"/>.
        /// </summary>
        public CommandBuffer Recording;
        public readonly List<CommandBuffer> Extra = new();
        public int ExtraUsed;
    }

    #region State

    readonly VulkanDevice _dev;
    readonly Vk _vk;
    readonly FrameSlot[] _slots;
    readonly object _gate = new();
    int _lastSubmitted = -1;
    Fence _splitFence;
    bool _inFrame;
    bool _disposed;

    #endregion

    #region Construction

    internal FrameRing(VulkanDevice dev, int count)
    {
        this._dev = dev;
        _vk = dev.Vk;
        _slots = new FrameSlot[count];
        for (int i = 0; i < count; i++)
        {
            var s = new FrameSlot();
            var pi = new CommandPoolCreateInfo
            {
                SType = StructureType.CommandPoolCreateInfo,
                Flags = CommandPoolCreateFlags.TransientBit,
                QueueFamilyIndex = dev.GraphicsFamily,
            };
            VulkanException.Check(_vk.CreateCommandPool(dev.Device, in pi, null, out s.Pool), "vkCreateCommandPool");
            var ai = new CommandBufferAllocateInfo
            {
                SType = StructureType.CommandBufferAllocateInfo,
                CommandPool = s.Pool,
                Level = CommandBufferLevel.Primary,
                CommandBufferCount = 1,
            };
            VulkanException.Check(_vk.AllocateCommandBuffers(dev.Device, in ai, out s.Cmd), "vkAllocateCommandBuffers");
            var fi = new FenceCreateInfo { SType = StructureType.FenceCreateInfo };
            VulkanException.Check(_vk.CreateFence(dev.Device, in fi, null, out s.Fence), "vkCreateFence");
            _slots[i] = s;
        }
    }

    #endregion

    #region Properties

    public int Count => _slots.Length;

    /// <summary>
    /// The current (or last begun) frame; 0 before the first.
    /// </summary>
    public long FrameNumber { get; private set; }

    /// <summary>
    /// The slot of the current frame (FrameNumber-1 modulo the count).
    /// </summary>
    public int Slot { get; private set; }

    /// <summary>
    /// The newest frame whose fence has been seen signalled (all its deletions have run).
    /// </summary>
    public long CompletedFrame { get; private set; }
    public bool InFrame => _inFrame;
    public CommandBuffer Current => _slots[Slot].Recording;

    #endregion

    #region Frames

    /// <summary>
    /// Waits for this slot's frame from N frames ago, runs its deletions, resets and begins the command buffer.
    /// </summary>
    public CommandBuffer BeginFrame()
    {
        if (_inFrame)
        {
            throw new InvalidOperationException("BeginFrame called twice without EndFrame");
        }
        long frame = FrameNumber + 1;
        int index = (int)((frame - 1) % _slots.Length);
        var s = _slots[index];
        if (s.InFlight)
        {
            var fence = s.Fence;
            VulkanException.Check(_vk.WaitForFences(_dev.Device, 1, in fence, true, ulong.MaxValue), "vkWaitForFences");
            Retire(s);
        }
        else
        {
            RunDeletions(s);
        }
        VulkanException.Check(_vk.ResetCommandPool(_dev.Device, s.Pool, 0), "vkResetCommandPool");
        Begin(s.Cmd);
        (s.Recording, s.ExtraUsed) = (s.Cmd, 0);
        FrameNumber = frame;
        Slot = index;
        s.Frame = frame;
        _inFrame = true;
        return s.Cmd;
    }

    #endregion

    #region Submission

    /// <summary>
    /// Ends the command buffer and submits it (graphics queue) with this slot's fence. <paramref name="before"/>, when given, is an
    /// already ended command buffer submitted ahead of the frame's own in the same submission (uploads).
    /// </summary>
    public void EndFrame(ReadOnlySpan<Silk.NET.Vulkan.Semaphore> wait = default, ReadOnlySpan<PipelineStageFlags> waitStages = default,
        ReadOnlySpan<Silk.NET.Vulkan.Semaphore> signal = default, CommandBuffer before = default)
    {
        if (!_inFrame)
        {
            throw new InvalidOperationException("EndFrame without BeginFrame");
        }
        if (wait.Length != waitStages.Length)
        {
            throw new ArgumentException("wait and waitStages must have the same length");
        }
        var s = _slots[Slot];
        VulkanException.Check(_vk.EndCommandBuffer(s.Recording), "vkEndCommandBuffer");
        var fence = s.Fence;
        VulkanException.Check(_vk.ResetFences(_dev.Device, 1, in fence), "vkResetFences");
        var cmds = stackalloc CommandBuffer[2];
        uint cmdCount = 0;
        if (before.Handle != 0) cmds[cmdCount++] = before;
        cmds[cmdCount++] = s.Recording;
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
            lock (_dev.QueueLock)
            {
                VulkanException.Check(_vk.QueueSubmit(_dev.GraphicsQueue, 1, in si, fence), "vkQueueSubmit");
            }
        }
        lock (_gate)
        {
            s.InFlight = true;
            _lastSubmitted = Slot;
            _inFrame = false;
        }
    }

    /// <summary>
    /// (Diagnostic, <c>MEITOU_DIAG_SUBMIT</c>, issue #108.) Ends the frame's command buffer so far, submits it after
    /// <paramref name="before"/> (an ended buffer) and waits for both; the frame goes on in another primary of the slot's pool, returned (begun).
    /// </summary>
    public CommandBuffer SubmitAndWait(CommandBuffer before)
    {
        if (!_inFrame) throw new InvalidOperationException("SubmitAndWait without BeginFrame");
        var s = _slots[Slot];
        VulkanException.Check(_vk.EndCommandBuffer(s.Recording), "vkEndCommandBuffer");
        SubmitWaited(before, s.Recording);
        if (s.ExtraUsed == s.Extra.Count) s.Extra.Add(Allocate(s.Pool));
        s.Recording = s.Extra[s.ExtraUsed++];
        Begin(s.Recording);
        return s.Recording;
    }

    void SubmitWaited(CommandBuffer first, CommandBuffer second)
    {
        if (_splitFence.Handle == 0)
        {
            var fi = new FenceCreateInfo { SType = StructureType.FenceCreateInfo };
            VulkanException.Check(_vk.CreateFence(_dev.Device, in fi, null, out _splitFence), "vkCreateFence");
        }
        var cmds = stackalloc CommandBuffer[] { first, second };
        var si = new SubmitInfo { SType = StructureType.SubmitInfo, CommandBufferCount = 2, PCommandBuffers = cmds };
        lock (_dev.QueueLock)
        {
            VulkanException.Check(_vk.QueueSubmit(_dev.GraphicsQueue, 1, in si, _splitFence), "vkQueueSubmit");
        }
        var fence = _splitFence;
        VulkanException.Check(_vk.WaitForFences(_dev.Device, 1, in fence, true, ulong.MaxValue), "vkWaitForFences");
        VulkanException.Check(_vk.ResetFences(_dev.Device, 1, in fence), "vkResetFences");
    }

    CommandBuffer Allocate(CommandPool pool)
    {
        var ai = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = pool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = 1,
        };
        VulkanException.Check(_vk.AllocateCommandBuffers(_dev.Device, in ai, out var cb), "vkAllocateCommandBuffers");
        return cb;
    }

    void Begin(CommandBuffer cb)
    {
        var bi = new CommandBufferBeginInfo { SType = StructureType.CommandBufferBeginInfo, Flags = CommandBufferUsageFlags.OneTimeSubmitBit };
        VulkanException.Check(_vk.BeginCommandBuffer(cb, in bi), "vkBeginCommandBuffer");
    }

    #endregion

    #region Deletion

    /// <summary>
    /// Runs <paramref name="destroy"/> once every frame that could use the resource has finished: on the current
    /// slot's list inside a frame, else on the most recently submitted slot's list, else at once.
    /// </summary>
    public void DeferDelete(Action destroy)
    {
        lock (_gate)
        {
            if (!_disposed)
            {
                if (_inFrame)
                {
                    _slots[Slot].Deletions.Add(destroy);
                    return;
                }
                if (_lastSubmitted >= 0 && _slots[_lastSubmitted].InFlight)
                {
                    _slots[_lastSubmitted].Deletions.Add(destroy);
                    return;
                }
            }
        }

        // Nothing in flight (or the last frame was already retired by WaitAll): safe now.
        destroy();
    }

    /// <summary>
    /// Out of memory: waits for the frames already submitted (not the one being recorded) and runs their deletions, so what they released
    /// is freed now rather than frames later. Render thread only. False when none was in flight.
    /// </summary>
    public bool RetireSubmitted()
    {
        bool any = false;
        for (int i = 0; i < _slots.Length; i++)
        {
            var s = _slots[i];
            if (_inFrame && i == Slot || !s.InFlight) continue;
            var fence = s.Fence;
            VulkanException.Check(_vk.WaitForFences(_dev.Device, 1, in fence, true, ulong.MaxValue), "vkWaitForFences");
            Retire(s);
            any = true;
        }
        return any;
    }

    /// <summary>
    /// Waits for all submitted frames and runs all pending deletions.
    /// </summary>
    public void WaitAll()
    {
        foreach (var s in _slots)
        {
            if (s.InFlight)
            {
                var fence = s.Fence;
                VulkanException.Check(_vk.WaitForFences(_dev.Device, 1, in fence, true, ulong.MaxValue), "vkWaitForFences");
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
        lock (_gate)
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
            lock (_gate)
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

    #endregion

    #region Lifetime

    /// <summary>
    /// The device must be idle. Runs the remaining deletions and destroys the per-frame objects.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        if (_inFrame)
        {
            // Drop the half-recorded frame; the pool reset below discards it.
            _inFrame = false;
        }
        foreach (var s in _slots)
        {
            s.InFlight = false;
            RunDeletions(s);
        }
        lock (_gate)
        {
            _disposed = true;
        }
        if (_splitFence.Handle != 0) _vk.DestroyFence(_dev.Device, _splitFence, null);
        foreach (var s in _slots)
        {
            _vk.DestroyFence(_dev.Device, s.Fence, null);
            _vk.DestroyCommandPool(_dev.Device, s.Pool, null);
        }
    }

    #endregion
}