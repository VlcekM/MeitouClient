using Meitou.Rendering.Vulkan.Core;
using Silk.NET.Vulkan;

namespace Meitou.Rendering.Gpu;

/// <summary>
/// The frame being recorded (docs/renderer-native.md 2.2): its number and slot, the primary command list, per-frame constants, timestamps,
/// resource states and statistics. Begun and ended by the host: while VkGl exists, <c>VkGl.BeginFrame</c> / <c>EndFrame</c> drive it, and its
/// command list records into VkGl's command buffer (the same buffer, the same order).
/// </summary>
public sealed unsafe class GpuFrame : IDisposable
{
    readonly VulkanDevice device;
    readonly LinearAllocator[] constants;
    readonly List<DescriptorPool>[] pools;
    readonly GpuContext ctx;

    internal GpuFrame(GpuContext ctx)
    {
        this.ctx = ctx;
        device = ctx.Device;
        int n = device.Frames.Count;
        constants = new LinearAllocator[n];
        pools = new List<DescriptorPool>[n];
        for (int i = 0; i < n; i++)
        {
            constants[i] = new LinearAllocator(device, $"frame constants {i}", ctx.HostMemory);
            pools[i] = [];
        }
        Stats = new GpuStats();
        Commands = new CommandList(device, Stats);
        Timestamps = new QueryArena(device);
        States = new ResourceStates();
    }

    /// <summary>The frame number (FrameRing.FrameNumber); 0 before the first.</summary>
    public long Number { get; private set; }
    public int Slot { get; private set; }
    public bool Open { get; private set; }
    public CommandList Commands { get; }
    /// <summary>Host-visible memory for this frame (constants, CPU-written instances and indirect arguments, upload staging).</summary>
    public LinearAllocator Constants => constants[Slot];
    public QueryArena Timestamps { get; }
    public ResourceStates States { get; }
    public GpuStats Stats { get; }
    /// <summary>The command buffer the host submits ahead of this frame's own (uploads).</summary>
    internal CommandBuffer UploadCommands { get; private set; }

    /// <summary>Called by the host after <c>FrameRing.BeginFrame</c>: the slot's fence has passed, so its memory, pools and queries are free.</summary>
    public void Begin(CommandBuffer commands, CommandBuffer uploads)
    {
        Number = device.Frames.FrameNumber;
        Slot = device.Frames.Slot;
        Commands.Handle = commands;
        Commands.Invalidate();
        UploadCommands = uploads;
        constants[Slot].Reset();
        foreach (var p in pools[Slot]) device.Vk.ResetDescriptorPool(device.Device, p, 0);
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
        foreach (var list in pools)
            foreach (var p in list) device.Vk.DestroyDescriptorPool(device.Device, p, null);
        Timestamps.Dispose();
    }
}
