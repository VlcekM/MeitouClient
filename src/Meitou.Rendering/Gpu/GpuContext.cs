using Meitou.Rendering.Gpu.Core;
using Meitou.Rendering.Gpu.Shaders;
using Silk.NET.Vulkan;

namespace Meitou.Rendering.Gpu;

/// <summary>The device doesn't have what the native renderer requires (owner decision 1).</summary>
public sealed class GpuFeatureException(string message) : Exception(message);

/// <summary>What the native API requires and what it may use (docs/renderer-native.md 1, owner decision 1).</summary>
public sealed record GpuFeatures(bool Bindless, bool MultiDrawIndirect, bool DrawIndirectFirstInstance, bool DrawIndirectCount, bool ShaderDrawParameters,
    bool PushDescriptors, bool DebugLabels)
{
    public static GpuFeatures Of(VulkanDevice d) => new(d.HasBindless, d.MultiDrawIndirect, d.DrawIndirectFirstInstance, d.DrawIndirectCount,
        d.ShaderDrawParameters, d.HasPushDescriptor, d.DebugUtils is not null);

    /// <summary>The required features the device lacks (empty when it has them all).</summary>
    public IReadOnlyList<string> Missing()
    {
        var m = new List<string>();
        if (!Bindless) m.Add("descriptor indexing (runtime descriptor arrays, partially bound, update after bind for sampled images, non-uniform indexing)");
        if (!MultiDrawIndirect) m.Add("multiDrawIndirect");
        if (!DrawIndirectFirstInstance) m.Add("drawIndirectFirstInstance");
        return m;
    }

    /// <summary>Throws <see cref="GpuFeatureException"/> naming what is missing.</summary>
    public void Require(string deviceName)
    {
        var missing = Missing();
        if (missing.Count > 0)
            throw new GpuFeatureException($"The GPU '{deviceName}' lacks Vulkan features Meitou requires: {string.Join("; ", missing)}. " +
                "Update the graphics driver; GPUs from before NVIDIA Maxwell, AMD GCN or Intel Xe are not supported.");
    }
}

/// <summary>
/// The native renderer API for one device (docs/renderer-native.md 2.2): what every renderer shares. The display (or a test) makes it
/// from the device and drives its frames (<see cref="BeginFrame"/>, <see cref="EndFrame"/>).
/// </summary>
public sealed unsafe partial class GpuContext : IDisposable
{
    readonly List<DescriptorPool> persistentPools = [];

    public GpuContext(VulkanDevice device)
    {
        Device = device;
        Features = GpuFeatures.Of(device);
        Features.Require(device.DeviceName);
        HostMemory = new HostMemory();
        Defaults = new GpuDefaults(device);
        Samplers = new SamplerCache(device);
        Shaders = new ShaderLibrary(device);
        Pipelines = new PipelineLibrary(device);
        Bindless = new BindlessTable(device);
        Shaders.BindlessLayout = Bindless.Layout;
        Uploads = new Uploader(this);
        Frame = new GpuFrame(this);
    }

    public VulkanDevice Device { get; }
    public GpuFeatures Features { get; }
    /// <summary>Frame-global textures, blocks and uniform values by GLSL name (docs/renderer-native.md 4.3).</summary>
    public FrameGlobals Globals { get; } = new();
    public GpuDefaults Defaults { get; }
    public SamplerCache Samplers { get; }
    public ShaderLibrary Shaders { get; }
    public PipelineLibrary Pipelines { get; }
    public BindlessTable Bindless { get; }
    public Uploader Uploads { get; }
    public GpuFrame Frame { get; }
    /// <summary>Per-frame host-visible chunks by buffer handle (for the draw log).</summary>
    public HostMemory HostMemory { get; }
    /// <summary>The draw log of the frame being recorded, when one is asked for (<see cref="DrawLog.RequestedPath"/>).</summary>
    public DrawLog? Log { get; internal set; }
    /// <summary>The upscaler's texture LOD bias: added to the mip level of every mipmapped fetch (set by the post-processing chain).</summary>
    public float LodBias { get; set; }

    /// <summary>
    /// Records a prepared guest segment into the pass being drawn now (docs/renderer-native.md 6.2, wave 4): inside a host's rendering with
    /// secondaries (<see cref="GpuFrame.Parallel"/>) it is queued and recorded when the host ends it, in this order, possibly on a job thread;
    /// otherwise at once into the host's open pass, as a guest segment (<see cref="BeginGuest"/>). Either way
    /// <see cref="RecordJob.Record"/> runs with <see cref="RenderJobs.InJob"/> set, so a job that reaches for render-thread state throws.
    /// </summary>
    public void Record(string label, RecordJob job)
    {
        if (Frame.Parallel.Open) { Frame.Parallel.Add(label, job); return; }
        var cmd = BeginGuest(label);
        RenderJobs.SetInJob(true);
        try { job.Record(cmd); }
        finally
        {
            RenderJobs.SetInJob(false);
            EndGuest(cmd);
        }
        job.Release();
    }

    /// <summary>
    /// A host's partial depth clear (<see cref="CommandList.ClearDepth"/>: a shadow tile, the depth between slices) in its place among the
    /// guests: into <paramref name="host"/>'s rendering, or, while it takes secondaries, as a segment of its own (a primary may record nothing
    /// else inside such a rendering).
    /// </summary>
    public void ClearDepth(CommandList host, float value, Rect2D rect) => Clear(host, false, default, true, value, rect);

    /// <summary>As <see cref="ClearDepth"/> for the colour and / or depth attachment (<see cref="CommandList.Clear"/>).</summary>
    public void Clear(CommandList host, bool colour, ClearColorValue colourValue, bool depth, float depthValue, Rect2D rect)
    {
        if (!Frame.Parallel.Open) { host.Clear(colour, colourValue, depth, depthValue, rect); return; }
        var job = clearJobs.Rent();
        (job.Owner, job.Colour, job.ColourValue, job.Depth, job.DepthValue, job.Rect) = (this, colour, colourValue, depth, depthValue, rect);
        Frame.Parallel.Add(depth && !colour ? "clear depth" : "clear", job);
    }

    readonly JobPool<ClearJob> clearJobs = new();

    sealed class ClearJob : RecordJob
    {
        public GpuContext Owner = null!;
        public bool Colour, Depth;
        public ClearColorValue ColourValue;
        public float DepthValue;
        public Rect2D Rect;
        public override void Record(CommandList cmd) => cmd.Clear(Colour, ColourValue, Depth, DepthValue, Rect);
        public override void Release() => Owner.clearJobs.Return(this);
    }

    uint? standIn2D;

    /// <summary>The stand-in's index in the bindless 2D float array (what a 2D sampler with nothing bound reads, <see cref="Dummy"/>), registered once.</summary>
    public uint StandIn2D => standIn2D ??= Bindless.Register(BindlessKind.Texture2D,
        Dummy(new SamplerInfo("", 0, 0, SamplerDimension.Dim2D, false, false, false, ScalarKind.Float, 0)));

    readonly Dictionary<(int, ScalarKind, bool), SampledTexture> dummies = [];

    /// <summary>The stand-in a sampler reads when nothing is bound: (0, 0, 0, 1), or depth 1 for a shadow sampler.</summary>
    public SampledTexture Dummy(SamplerInfo sampler)
    {
        int slot = GlConventions.SamplerSlot(sampler);
        var key = (slot, sampler.SampledKind, sampler.Depth);
        if (dummies.TryGetValue(key, out var s)) return s;
        var d = Defaults.Texture(slot, sampler.SampledKind, sampler.Depth);
        var desc = new TextureDesc(d.Format, 1, 1, 1, slot == 2 ? 1 : d.Layers, Kind: slot == 2 ? TextureKind.Cube : slot == 1 ? TextureKind.Texture2DArray : TextureKind.Texture2D);
        var t = Texture.Borrow(Device, desc, d.Image.Image);
        var sampled = new SampledTexture(Samplers.Get(SamplerDesc.FromGl(TextureMinFilter.Nearest, TextureMagFilter.Nearest, TextureWrapMode.Repeat, TextureWrapMode.Repeat,
            TextureWrapMode.Repeat, false, DepthFunction.Lequal, false, 1, GlConventions.IsIntegerFormat(d.Format) || d.Format == Format.R32Sint, 0)), t.View(), d.Image.Image);
        dummies[key] = sampled;
        return sampled;
    }

    /// <summary>A descriptor set that lives as long as the context (set 1 of legacy programs: one per pair of constant chunks).</summary>
    public DescriptorSet AllocatePersistentSet(DescriptorSetLayout layout)
    {
        var sizes = stackalloc DescriptorPoolSize[] { new(DescriptorType.UniformBufferDynamic, 2048), new(DescriptorType.StorageBufferDynamic, 512) };
        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (persistentPools.Count > 0)
            {
                var ai = new DescriptorSetAllocateInfo { SType = StructureType.DescriptorSetAllocateInfo, DescriptorPool = persistentPools[^1], DescriptorSetCount = 1, PSetLayouts = &layout };
                DescriptorSet set;
                if (Device.Vk.AllocateDescriptorSets(Device.Device, &ai, &set) == Result.Success) return set;
            }
            var pi = new DescriptorPoolCreateInfo { SType = StructureType.DescriptorPoolCreateInfo, MaxSets = 1024, PoolSizeCount = 2, PPoolSizes = sizes };
            VulkanException.Check(Device.Vk.CreateDescriptorPool(Device.Device, &pi, null, out var fresh), "vkCreateDescriptorPool");
            persistentPools.Add(fresh);
        }
        throw new InvalidOperationException("descriptor set allocation failed");
    }

    /// <summary>Waits for the GPU, then copies level 0 / layer 0 of a colour texture into host memory (tests and tools; never inside a frame).</summary>
    public byte[] ReadBack(Texture texture, int bytesPerTexel)
    {
        Device.Frames.WaitAll();
        int w = texture.Desc.Width, h = texture.Desc.Height;
        ulong size = (ulong)(w * h * bytesPerTexel);
        var buffer = Device.Allocator.CreateBuffer(size, BufferUsageFlags.TransferDstBit, MemoryKind.Readback, "readback");
        try
        {
            var cb = Device.BeginImmediate();
            var region = new BufferImageCopy
            {
                ImageSubresource = new ImageSubresourceLayers(texture.Desc.Aspect, 0, 0, 1),
                ImageExtent = new Extent3D((uint)w, (uint)h, 1),
            };
            Device.Vk.CmdCopyImageToBuffer(cb, texture.Image, ImageLayout.General, buffer.Buffer, 1, &region);
            Device.EndImmediate(cb);
            Device.Allocator.Invalidate(buffer.Allocation);
            return new ReadOnlySpan<byte>(buffer.Mapped, (int)size).ToArray();
        }
        finally { Device.Allocator.Free(buffer); }
    }

    public void Dispose()
    {
        Device.WaitIdle();
        Frame.Dispose();
        DisposeFrameLoop();
        Pipelines.Dispose();
        Bindless.Dispose();
        foreach (var p in persistentPools) Device.Vk.DestroyDescriptorPool(Device.Device, p, null);
        persistentPools.Clear();
        Samplers.Dispose();
        Defaults.Dispose();
        Log?.Dispose();
    }
}
