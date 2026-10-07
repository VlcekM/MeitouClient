using Meitou.Rendering.Gpu.Core;
using Silk.NET.Vulkan;

namespace Meitou.Rendering.Gpu;

/// <summary>
/// The frame being recorded (docs/renderer-native.md 2.2): its number and slot, the primary command list, per-frame constants, timestamps,
/// resource states and statistics. Begun and ended by the context's frame loop (<see cref="GpuContext.BeginFrame"/> /
/// <see cref="GpuContext.EndFrame"/>), driven by the display or a test.
/// </summary>
public sealed unsafe class GpuFrame : IDisposable
{
    readonly VulkanDevice device;
    readonly LinearAllocator[] constants, staging;
    /// <summary>Regular staging chunks a slot keeps beyond what its last cycle used (32 MB): a loading peak's are freed once it has passed.</summary>
    const int StagingKeep = 4;
    readonly List<DescriptorPool>[] pools;
    readonly GpuContext ctx;

    internal GpuFrame(GpuContext ctx)
    {
        this.ctx = ctx;
        device = ctx.Device;
        int n = device.Frames.Count;
        constants = new LinearAllocator[n];
        staging = new LinearAllocator[n];
        pools = new List<DescriptorPool>[n];
        for (int i = 0; i < n; i++)
        {
            constants[i] = new LinearAllocator(device, $"frame constants {i}", ctx.HostMemory);
            staging[i] = new LinearAllocator(device, $"upload staging {i}", ctx.HostMemory);
            pools[i] = [];
        }
        Stats = new GpuStats();
        Commands = new CommandList(device, Stats);
        PreFrame = new CommandList(device, Stats);
        Timestamps = new QueryArena(device);
        States = new ResourceStates();
        Parallel = new ParallelPass(ctx);
        threads = new ThreadPools?[RenderJobs.Threads];
    }

    /// <summary>The host's rendering with secondary command buffers, when one is open (docs/renderer-native.md 6, wave 4).</summary>
    public ParallelPass Parallel { get; }

    /// <summary>Per recording thread (<see cref="RenderJobs.ThreadIndex"/>): a command pool per frame slot and its secondaries, reused when the
    /// slot comes round (docs/renderer-native.md 6.3). Each thread only touches its own entry; the render thread resets them at the frame's begin.</summary>
    sealed class ThreadPools(VulkanDevice device, int slots)
    {
        public readonly CommandPool[] Pools = new CommandPool[slots];
        public readonly List<CommandList>[] Lists = [.. Enumerable.Range(0, slots).Select(_ => new List<CommandList>())];
        public readonly int[] Used = new int[slots];

        public CommandList Next(int slot)
        {
            var vk = device.Vk;
            if (Pools[slot].Handle == 0)
            {
                var pi = new CommandPoolCreateInfo { SType = StructureType.CommandPoolCreateInfo, Flags = CommandPoolCreateFlags.TransientBit, QueueFamilyIndex = device.GraphicsFamily };
                VulkanException.Check(vk.CreateCommandPool(device.Device, in pi, null, out Pools[slot]), "vkCreateCommandPool");
            }
            var lists = Lists[slot];
            if (Used[slot] == lists.Count)
            {
                var ai = new CommandBufferAllocateInfo { SType = StructureType.CommandBufferAllocateInfo, CommandPool = Pools[slot], Level = CommandBufferLevel.Secondary, CommandBufferCount = 1 };
                VulkanException.Check(vk.AllocateCommandBuffers(device.Device, in ai, out var cb), "vkAllocateCommandBuffers");
                lists.Add(new CommandList(device, new GpuStats()) { Handle = cb });
            }
            return lists[Used[slot]++];
        }

        public void Reset(int slot)
        {
            if (Pools[slot].Handle == 0) return;
            VulkanException.Check(device.Vk.ResetCommandPool(device.Device, Pools[slot], 0), "vkResetCommandPool");
            Used[slot] = 0;
        }

        public void Dispose()
        {
            foreach (var p in Pools) if (p.Handle != 0) device.Vk.DestroyCommandPool(device.Device, p, null);
        }
    }

    readonly ThreadPools?[] threads;

    /// <summary>A secondary command buffer of the calling thread's pool for this slot, begun to continue a rendering of <paramref name="formats"/>.</summary>
    internal CommandList BeginSecondary(in AttachmentFormats formats)
    {
        int t = RenderJobs.ThreadIndex;
        var pools = threads[t] ??= new ThreadPools(device, device.Frames.Count);
        var list = pools.Next(Slot);
        list.Stats.Reset();
        list.BeginSecondary(formats);
        return list;
    }

    /// <summary>The frame number (FrameRing.FrameNumber); 0 before the first.</summary>
    public long Number { get; private set; }
    public int Slot { get; private set; }
    public bool Open { get; private set; }
    public CommandList Commands { get; }
    /// <summary>Host-visible memory for this frame (constants, CPU-written instances and indirect arguments).</summary>
    public LinearAllocator Constants => constants[Slot];
    /// <summary>Host-visible staging for this frame's uploads (<see cref="Uploader"/>): copy sources only, so its reset trims it.</summary>
    public LinearAllocator Staging => staging[Slot];
    public QueryArena Timestamps { get; }
    public ResourceStates States { get; }
    public GpuStats Stats { get; }
    /// <summary>The command buffer the host submits ahead of this frame's own (uploads).</summary>
    internal CommandBuffer UploadCommands { get; private set; }
    /// <summary>
    /// (Added for GPU-driven foliage, docs/renderer-native.md 5.3 as built.) A command list recording into <see cref="UploadCommands"/>: the
    /// buffer the host submits ahead of the frame's own, in the same submission, with a full barrier at its end (VkGl). Whatever is recorded
    /// here, at any point of the frame's recording (inside a native segment or a host pass too), executes before every command of the frame's
    /// own list, after the uploads recorded before it. For transfers and compute whose results the frame's passes read (a cull per view that
    /// writes instance lists and indirect arguments): no rendering, and the caller places its own barriers between its commands (uploads
    /// place transfer-to-transfer ones only). Valid while the frame is open; render thread only.
    /// </summary>
    public CommandList PreFrame { get; }

    /// <summary>Called by the host after <c>FrameRing.BeginFrame</c>: the slot's fence has passed, so its memory, pools and queries are free.</summary>
    public void Begin(CommandBuffer commands, CommandBuffer uploads)
    {
        Number = device.Frames.FrameNumber;
        Slot = device.Frames.Slot;
        Commands.Handle = commands;
        Commands.Invalidate();
        UploadCommands = uploads;
        PreFrame.Handle = uploads;
        PreFrame.Invalidate();
        constants[Slot].Reset();
        staging[Slot].Reset(StagingKeep);
        foreach (var p in pools[Slot]) device.Vk.ResetDescriptorPool(device.Device, p, 0);
        foreach (var t in threads) t?.Reset(Slot);
        Timestamps.Begin(Slot, Number);
        States.AssumeFullBarrier();
        Stats.Reset();
        ctx.Bindless?.BeginFrame(Slot);
        // The draw log records one chosen frame.
        if (DrawLog.RequestedPath(Number) is { } path && ctx.Log is null)
        {
            ctx.Log = new DrawLog(new StreamWriter(path), ctx.HostMemory);
            ctx.Log.Note($"frame {Number}");
        }
        Commands.Log = ctx.Log;
        Open = true;
    }

    /// <summary>Called by the host before it submits the frame.</summary>
    public void End()
    {
        Timestamps.End();
        ctx.Bindless?.EndFrame();   // what was registered during the frame, into its set before the submit
        Open = false;
        if (ctx.Log is { } log)
        {
            log.Note($"end of frame {Number}: {log.Draws} draws");
            log.Dispose();
            ctx.Log = null;
            Commands.Log = null;
        }
    }

    /// <summary>A descriptor set from this frame's pools (sets that cannot be pushed); freed when the slot comes round.</summary>
    public DescriptorSet AllocateSet(DescriptorSetLayout layout)
    {
        RenderJobs.AssertNotInJob();
        var list = pools[Slot];
        var sizes = stackalloc DescriptorPoolSize[] { new(DescriptorType.UniformBuffer, 4096), new(DescriptorType.CombinedImageSampler, 8192), new(DescriptorType.StorageBuffer, 2048) };
        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (list.Count > 0)
            {
                var ai = new DescriptorSetAllocateInfo { SType = StructureType.DescriptorSetAllocateInfo, DescriptorPool = list[^1], DescriptorSetCount = 1, PSetLayouts = &layout };
                DescriptorSet set;
                if (device.Vk.AllocateDescriptorSets(device.Device, &ai, &set) == Result.Success) return set;
            }
            var pi = new DescriptorPoolCreateInfo { SType = StructureType.DescriptorPoolCreateInfo, MaxSets = 1024, PoolSizeCount = 3, PPoolSizes = sizes };
            VulkanException.Check(device.Vk.CreateDescriptorPool(device.Device, &pi, null, out var fresh), "vkCreateDescriptorPool");
            list.Add(fresh);
        }
        throw new InvalidOperationException("descriptor set allocation failed");
    }

    public void Dispose()
    {
        foreach (var c in constants) c.Dispose();
        foreach (var c in staging) c.Dispose();
        foreach (var list in pools)
            foreach (var p in list) device.Vk.DestroyDescriptorPool(device.Device, p, null);
        Timestamps.Dispose();
        foreach (var t in threads) t?.Dispose();
    }
}
