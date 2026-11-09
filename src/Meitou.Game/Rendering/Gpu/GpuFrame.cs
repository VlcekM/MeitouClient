using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Gpu.Core;
using Silk.NET.Vulkan;

namespace Meitou.Game.Rendering.Gpu;

/// <summary>
/// The frame being recorded (docs/renderer-native.md 2.2): its number and slot, the primary command list, per-frame constants, timestamps, resource
/// states and statistics. Begun and ended by the context's frame loop (<see cref="GpuContext.BeginFrame"/> / <see cref="GpuContext.EndFrame"/>),
/// driven by the display or a test.
/// </summary>
public sealed unsafe class GpuFrame : IDisposable, IGpuFrame
{
    #region Frame

    /// <summary>
    /// Regular staging chunks a slot keeps beyond what its last cycle used (32 MB): a loading peak's are freed once it has passed.
    /// </summary>
    const int StagingKeep = 4;

    /// <summary>
    /// The device the frame's memory, pools and command buffers are on.
    /// </summary>
    readonly VulkanDevice _device;
    /// <summary>
    /// Each slot's constants memory (<see cref="Constants"/>).
    /// </summary>
    readonly LinearAllocator[] _constants;
    /// <summary>
    /// Each slot's upload staging memory (<see cref="Staging"/>).
    /// </summary>
    readonly LinearAllocator[] _staging;

    /// <summary>
    /// The context whose pipelines and services the frame's lists use.
    /// </summary>
    readonly GpuContext _ctx;

    /// <summary>
    /// The frame of the context: per slot a constants and a staging allocator and descriptor pools; the primary and pre-frame lists, the timestamps,
    /// the state tracking and the counters.
    /// </summary>
    internal GpuFrame(GpuContext ctx)
    {
        this._ctx = ctx;
        _device = ctx.Device;
        int n = _device.Frames.Count;
        _constants = new LinearAllocator[n];
        _staging = new LinearAllocator[n];
        _pools = new List<DescriptorPool>[n];
        for (int i = 0; i < n; i++)
        {
            _constants[i] = new LinearAllocator(_device, $"frame constants {i}");
            _staging[i] = new LinearAllocator(_device, $"upload staging {i}");
            _pools[i] = [];
        }

        Stats = new GpuStats();
        Commands = new CommandList(ctx, Stats);
        PreFrame = new CommandList(ctx, Stats);
        Timestamps = new QueryArena(_device);
        States = new ResourceStates();
        Parallel = new ParallelPass(ctx);
        _threads = new ThreadPools?[RenderJobs.Threads];
    }

    /// <summary>
    /// The host's rendering with secondary command buffers, when one is open (docs/renderer-native.md 6, wave 4).
    /// </summary>
    public ParallelPass Parallel { get; }

    /// <summary>
    /// The frame number (FrameRing.FrameNumber); 0 before the first.
    /// </summary>
    public long Number { get; private set; }

    /// <summary>
    /// The frame ring slot (FrameRing.Slot).
    /// </summary>
    public int Slot { get; private set; }

    /// <summary>
    /// Between the frame loop's begin and end.
    /// </summary>
    public bool Open { get; private set; }

    /// <summary>
    /// The frame's primary command list.
    /// </summary>
    public CommandList Commands { get; }

    /// <summary>
    /// The frame's primary command list, as the seam's interface (<see cref="IGpuFrame.Commands"/>).
    /// </summary>
    IGpuCommandList IGpuFrame.Commands => Commands;

    /// <summary>
    /// The upload commands, as the seam's interface (<see cref="IGpuFrame.PreFrame"/>).
    /// </summary>
    IGpuCommandList IGpuFrame.PreFrame => PreFrame;

    /// <summary>
    /// Host-visible memory for this frame (constants, CPU-written instances and indirect arguments).
    /// </summary>
    public LinearAllocator Constants => _constants[Slot];

    /// <inheritdoc cref="IGpuFrame.WriteConstants"/>
    public Transient WriteConstants(ReadOnlySpan<byte> data, ulong alignment = 16) => Constants.Write(data, alignment);

    /// <inheritdoc cref="IGpuFrame.AllocateConstants"/>
    public Transient AllocateConstants(ulong size, ulong alignment = 16) => Constants.Allocate(size, alignment);

    /// <summary>
    /// Host-visible staging for this frame's uploads (<see cref="Uploader"/>): copy sources only, so its reset trims it.
    /// </summary>
    public LinearAllocator Staging => _staging[Slot];

    /// <summary>
    /// The frame's GPU timestamps.
    /// </summary>
    public QueryArena Timestamps { get; }

    /// <summary>
    /// The frame's resource state tracking.
    /// </summary>
    public ResourceStates States { get; }

    /// <summary>
    /// The frame's counters, shared by its primary and pre-frame lists.
    /// </summary>
    public GpuStats Stats { get; }

    /// <summary>
    /// The frame's timestamps as the seam's interface (<see cref="IGpuFrame.Timestamps"/>).
    /// </summary>
    IGpuTimestamps IGpuFrame.Timestamps => Timestamps;

    /// <summary>
    /// The command buffer the host submits ahead of this frame's own (uploads).
    /// </summary>
    internal CommandBuffer UploadCommands { get; private set; }

    /// <summary>
    /// (Added for GPU-driven foliage, docs/renderer-native.md 5.3 as built.) A command list recording into <see cref="UploadCommands"/>: the buffer
    /// the host submits ahead of the frame's own, in the same submission, with a full barrier at its end (VkGl). Whatever is recorded here, at any
    /// point of the frame's recording (inside a native segment or a host pass too), executes before every command of the frame's own list, after the
    /// uploads recorded before it. For transfers and compute whose results the frame's passes read (a cull per view that writes instance lists and
    /// indirect arguments): no rendering, and the caller places its own barriers between its commands (uploads place transfer-to-transfer ones only).
    /// Valid while the frame is open; render thread only.
    /// </summary>
    public CommandList PreFrame { get; }

    /// <summary>
    /// Called by the host after <c>FrameRing.BeginFrame</c>: the slot's fence has passed, so its memory, pools and queries are free.
    /// </summary>
    public void Begin(CommandBuffer commands, CommandBuffer uploads)
    {
        Number = _device.Frames.FrameNumber;
        Slot = _device.Frames.Slot;
        Commands.Handle = commands;
        Commands.Invalidate();
        UploadCommands = uploads;
        PreFrame.Handle = uploads;
        PreFrame.Invalidate();
        _constants[Slot].Reset();
        _staging[Slot].Reset(StagingKeep);
        foreach (var p in _pools[Slot]) _device.Vk.ResetDescriptorPool(_device.Device, p, 0);

        foreach (var t in _threads) t?.Reset(Slot);

        Timestamps.Begin(Slot, Number);
        States.AssumeFullBarrier();
        Stats.Reset();
        _ctx.Bindless?.BeginFrame(Slot);
        Open = true;
    }

    /// <summary>
    /// After <see cref="GpuContext.SubmitAndWait"/>: the frame goes on in <paramref name="commands"/> and <paramref name="uploads"/> (begun), all
    /// before done.
    /// </summary>
    internal void Continue(CommandBuffer commands, CommandBuffer uploads)
    {
        Commands.Handle = commands;
        Commands.Invalidate();
        UploadCommands = uploads;
        PreFrame.Handle = uploads;
        PreFrame.Invalidate();
        States.AssumeFullBarrier();
        _ctx.Bindless?.BeginFrame(Slot);
    }

    /// <summary>
    /// Called by the host before it submits the frame.
    /// </summary>
    public void End()
    {
        Timestamps.End();
        _ctx.Bindless?.EndFrame();   // what was registered during the frame, into its set before the submit
        Open = false;
    }

    /// <summary>
    /// Releases the constants and staging memory, the descriptor pools, the timestamps and the threads' command pools.
    /// </summary>
    public void Dispose()
    {
        foreach (var c in _constants) c.Dispose();
        foreach (var c in _staging) c.Dispose();
        foreach (var list in _pools)
            foreach (var p in list) _device.Vk.DestroyDescriptorPool(_device.Device, p, null);

        Timestamps.Dispose();
        foreach (var t in _threads) t?.Dispose();
    }

    #endregion

    #region Secondary command buffers

    /// <summary>
    /// Each recording thread's secondary command pools, made on the thread's first secondary.
    /// </summary>
    readonly ThreadPools?[] _threads;

    /// <summary>
    /// Per recording thread (<see cref="RenderJobs.ThreadIndex"/>): a command pool per frame slot and its secondaries, reused when the slot comes
    /// round (docs/renderer-native.md 6.3). Each thread only touches its own entry; the render thread resets them at the frame's begin.
    /// </summary>
    sealed class ThreadPools(GpuContext ctx, int slots)
    {
        /// <summary>
        /// The device the pools are made on.
        /// </summary>
        readonly VulkanDevice _device = ctx.Device;

        /// <summary>
        /// One command pool per frame slot; 0 until the thread's first secondary in that slot.
        /// </summary>
        public readonly CommandPool[] Pools = new CommandPool[slots];

        /// <summary>
        /// Each slot's secondaries, reused once the slot's pool is reset.
        /// </summary>
        public readonly List<CommandList>[] Lists = [.. Enumerable.Range(0, slots).Select(_ => new List<CommandList>())];

        /// <summary>
        /// Each slot's secondaries handed out this frame.
        /// </summary>
        public readonly int[] Used = new int[slots];

        /// <summary>
        /// The next secondary of the slot (making the pool and the list when needed).
        /// </summary>
        public CommandList Next(int slot)
        {
            var vk = _device.Vk;
            if (Pools[slot].Handle == 0) MakePool(vk, slot);

            var lists = Lists[slot];
            if (Used[slot] == lists.Count) lists.Add(MakeSecondary(vk, slot));

            return lists[Used[slot]++];
        }

        /// <summary>
        /// Makes the slot's transient command pool.
        /// </summary>
        void MakePool(Vk vk, int slot)
        {
            var pi = new CommandPoolCreateInfo
            {
                SType = StructureType.CommandPoolCreateInfo,
                Flags = CommandPoolCreateFlags.TransientBit,
                QueueFamilyIndex = _device.GraphicsFamily,
            };
            VulkanException.Check(vk.CreateCommandPool(_device.Device, in pi, null, out Pools[slot]), "vkCreateCommandPool");
        }

        /// <summary>
        /// A new secondary command buffer of the slot's pool, wrapped in a list with its own counters.
        /// </summary>
        CommandList MakeSecondary(Vk vk, int slot)
        {
            var ai = new CommandBufferAllocateInfo
            {
                SType = StructureType.CommandBufferAllocateInfo,
                CommandPool = Pools[slot],
                Level = CommandBufferLevel.Secondary,
                CommandBufferCount = 1,
            };
            VulkanException.Check(vk.AllocateCommandBuffers(_device.Device, in ai, out var cb), "vkAllocateCommandBuffers");
            return new CommandList(ctx, new GpuStats()) { Handle = cb };
        }

        /// <summary>
        /// Resets the slot's pool (its secondaries go back to initial) and hands them out from the start.
        /// </summary>
        public void Reset(int slot)
        {
            if (Pools[slot].Handle == 0) return;

            VulkanException.Check(_device.Vk.ResetCommandPool(_device.Device, Pools[slot], 0), "vkResetCommandPool");
            Used[slot] = 0;
        }

        /// <summary>
        /// Destroys the pools (their command buffers go with them).
        /// </summary>
        public void Dispose()
        {
            foreach (var p in Pools)
                if (p.Handle != 0) _device.Vk.DestroyCommandPool(_device.Device, p, null);
        }
    }

    /// <summary>
    /// A secondary command buffer of the calling thread's pool for this slot, begun to continue a rendering of <paramref name="formats"/>.
    /// </summary>
    internal CommandList BeginSecondary(in AttachmentFormats formats)
    {
        int t = RenderJobs.ThreadIndex;
        var pools = _threads[t] ??= new ThreadPools(_ctx, _device.Frames.Count);
        var list = pools.Next(Slot);
        list.Stats.Reset();
        list.BeginSecondary(formats);
        return list;
    }

    #endregion

    #region Descriptor sets

    /// <summary>
    /// Each slot's descriptor pools for the frame's sets, reset when the slot comes round.
    /// </summary>
    readonly List<DescriptorPool>[] _pools;

    /// <summary>
    /// Guards the slot's descriptor pools (the recording threads allocate sets too).
    /// </summary>
    readonly object _setGate = new();

    /// <summary>
    /// A descriptor set from this frame's pools (sets that cannot be pushed); freed when the slot comes round.
    /// </summary>
    public DescriptorSet AllocateSet(GpuSetLayout layout) => AllocateSet(_ctx.Shaders.VkLayout(layout));

    /// <summary>
    /// As <see cref="AllocateSet(GpuSetLayout)"/>, with the backend's own layout.
    /// </summary>
    public DescriptorSet AllocateSet(DescriptorSetLayout layout)
    {
        RenderJobs.AssertNotInJob();
        return AllocateSetAnyThread(layout);
    }

    /// <summary>
    /// As <see cref="AllocateSet(DescriptorSetLayout)"/> from any thread: a recording job writes a set it cannot push
    /// (<see cref="CommandList.BindProgramResources"/> on a device or layout without push descriptors). Serialized: the pools are shared.
    /// </summary>
    internal DescriptorSet AllocateSetAnyThread(DescriptorSetLayout layout)
    {
        lock (_setGate)
        {
            var list = _pools[Slot];
            var sizes = stackalloc DescriptorPoolSize[]
            {
                new(DescriptorType.UniformBuffer, 4096),
                new(DescriptorType.CombinedImageSampler, 8192),
                new(DescriptorType.StorageBuffer, 2048),
                new(DescriptorType.StorageImage, 1024),
            };
            for (int attempt = 0; attempt < 2; attempt++)
            {
                if (list.Count > 0)
                {
                    var ai = new DescriptorSetAllocateInfo
                    {
                        SType = StructureType.DescriptorSetAllocateInfo, DescriptorPool = list[^1], DescriptorSetCount = 1, PSetLayouts = &layout,
                    };
                    DescriptorSet set;
                    if (_device.Vk.AllocateDescriptorSets(_device.Device, &ai, &set) == Result.Success) return set;
                }

                var pi = new DescriptorPoolCreateInfo
                {
                    SType = StructureType.DescriptorPoolCreateInfo, MaxSets = 1024, PoolSizeCount = 4, PPoolSizes = sizes,
                };
                VulkanException.Check(_device.Vk.CreateDescriptorPool(_device.Device, &pi, null, out var fresh), "vkCreateDescriptorPool");
                list.Add(fresh);
            }
        }

        throw new InvalidOperationException("descriptor set allocation failed");
    }

    #endregion
}
