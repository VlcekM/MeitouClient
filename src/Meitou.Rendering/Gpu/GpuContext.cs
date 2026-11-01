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
public sealed unsafe partial class GpuContext : IDisposable, IGpuContext
{
    readonly List<DescriptorPool> persistentPools = [];

    /// <summary>Whether the device is an integrated GPU (<see cref="VulkanDevice.IsIntegrated"/>: its budgets and star layers differ).</summary>
    public bool IsIntegrated => Device.IsIntegrated;

    /// <summary>The device limit a uniform buffer slice's offset must align to (the default blocks' slices).</summary>
    public ulong UniformBufferAlign => Device.Limits.MinUniformBufferOffsetAlignment;

    /// <summary>
    /// A set that lives as long as the context, its slots written once from the bindings (the layout says what each slot holds and
    /// whether its offset comes at bind time — then the binding's offset is ignored and the bind's dynamic offset is used).
    /// </summary>
    public GpuSet CreateSet(GpuSetLayout layout, ReadOnlySpan<GpuBinding> bindings)
    {
        var set = AllocatePersistentSet(layout);
        var vkSet = Device.SetAt(set.Index);
        var writes = stackalloc WriteDescriptorSet[bindings.Length];
        var buffers = stackalloc DescriptorBufferInfo[bindings.Length];
        var images = stackalloc DescriptorImageInfo[bindings.Length];
        for (int i = 0; i < bindings.Length; i++)
        {
            var b = bindings[i];
            var slot = layout.Slots.First(s => s.Binding == b.Slot);
            writes[i] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstSet = vkSet, DstBinding = b.Slot, DescriptorCount = 1 };
            var at = slot.OffsetAtBind ? 0UL : b.Buffer.Offset;
            switch (slot.Kind)
            {
                case GpuSlotKind.UniformBuffer:
                    buffers[i] = new DescriptorBufferInfo(Device.BufferAt(b.Buffer.Buffer.Index), at, b.Buffer.Size);
                    writes[i].DescriptorType = slot.OffsetAtBind ? DescriptorType.UniformBufferDynamic : DescriptorType.UniformBuffer;
                    writes[i].PBufferInfo = &buffers[i];
                    break;
                case GpuSlotKind.StorageBuffer:
                    buffers[i] = new DescriptorBufferInfo(Device.BufferAt(b.Buffer.Buffer.Index), at, b.Buffer.Size);
                    writes[i].DescriptorType = slot.OffsetAtBind ? DescriptorType.StorageBufferDynamic : DescriptorType.StorageBuffer;
                    writes[i].PBufferInfo = &buffers[i];
                    break;
                case GpuSlotKind.CombinedSampler:
                    images[i] = new DescriptorImageInfo(Device.SamplerAt(b.Texture.Sampler.Index), Device.ViewAt(b.Texture.View.Index), ImageLayout.General);
                    writes[i].DescriptorType = DescriptorType.CombinedImageSampler;
                    writes[i].PImageInfo = &images[i];
                    break;
                case GpuSlotKind.StorageImage:
                    images[i] = new DescriptorImageInfo(default, Device.ViewAt(b.Texture.View.Index), ImageLayout.General);
                    writes[i].DescriptorType = DescriptorType.StorageImage;
                    writes[i].PImageInfo = &images[i];
                    break;
            }
        }
        Device.Vk.UpdateDescriptorSets(Device.Device, (uint)bindings.Length, writes, 0, null);
        return set;
    }


    public GpuContext(VulkanDevice device)
    {
        Device = device;
        Features = GpuFeatures.Of(device);
        Features.Require(device.DeviceName);
        Defaults = new GpuDefaults(device);
        Samplers = new SamplerCache(device);
        Shaders = new ShaderLibrary(device);
        Pipelines = new PipelineLibrary(device, Shaders);
        var bindless = new BindlessTable(device);
        Bindless = bindless;
        Shaders.BindlessLayout = bindless.Layout;
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
    public IGpuBindlessTable Bindless { get; }
    public Uploader Uploads { get; }
    public GpuFrame Frame { get; }
    /// <summary>The frame as the seam's interface (<see cref="IGpuContext.Frame"/>).</summary>
    IGpuFrame IGpuContext.Frame => Frame;
    /// <summary>The services as the seam's interfaces (<see cref="IGpuContext"/>; the renderers on the concrete context keep the backend's classes).</summary>
    IGpuShaders IGpuContext.Shaders => Shaders;
    IGpuPipelines IGpuContext.Pipelines => Pipelines;
    IGpuSamplers IGpuContext.Samplers => Samplers;
    IGpuUploads IGpuContext.Uploads => Uploads;
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
    public void ClearDepth(IGpuCommandList host, float value, PixelRect rect) => Clear(host, false, default, true, value, rect);

    /// <summary>As <see cref="ClearDepth"/> for the colour and / or depth attachment (<see cref="CommandList.Clear"/>).</summary>
    public void Clear(IGpuCommandList host, bool colour, ClearColour colourValue, bool depth, float depthValue, PixelRect rect)
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
        public ClearColour ColourValue;
        public float DepthValue;
        public PixelRect Rect;
        public override void Record(IGpuCommandList cmd) => cmd.Clear(Colour, ColourValue, Depth, DepthValue, Rect);
        public override void Release() => Owner.clearJobs.Return(this);
    }

    uint? standIn2D;

    /// <summary>The stand-in's index in the bindless 2D float array (what a 2D sampler with nothing bound reads, <see cref="Dummy"/>), registered once.</summary>
    public uint StandIn2D => standIn2D ??= Bindless.Register(BindlessKind.Texture2D,
        Dummy(new SamplerInfo("", 0, 0, SamplerDimension.Dim2D, false, false, false, ScalarKind.Float, 0)));

    readonly Dictionary<(int, ScalarKind, bool), SampledTexture> dummies = [];
    readonly List<GpuTexture> dummyTextures = [];

    /// <summary>The generic attribute value a disabled vertex input reads (<see cref="GpuDefaults.DummyVertex"/>).</summary>
    public GpuBuffer DummyVertex => Defaults.DummyVertex.Handle;

    /// <summary>The stand-in a sampler reads when nothing is bound: (0, 0, 0, 1), or depth 1 for a shadow sampler.</summary>
    public SampledTexture Dummy(SamplerInfo sampler)
    {
        int slot = GlConventions.SamplerSlot(sampler);
        var key = (slot, sampler.SampledKind, sampler.Depth);
        if (dummies.TryGetValue(key, out var s)) return s;
        var d = Defaults.Texture(slot, sampler.SampledKind, sampler.Depth);
        var desc = new TextureDesc(d.Format, 1, 1, 1, slot == 2 ? 1 : d.Layers, Kind: slot == 2 ? TextureKind.Cube : slot == 1 ? TextureKind.Texture2DArray : TextureKind.Texture2D);
        var t = BorrowTexture(desc, d.Image.Image);
        var sampled = new SampledTexture(Samplers.Get(SamplerDesc.FromGl(TextureMinFilter.Nearest, TextureMagFilter.Nearest, TextureWrapMode.Repeat, TextureWrapMode.Repeat,
            TextureWrapMode.Repeat, false, DepthFunction.Lequal, false, 1, GlConventions.IsIntegerFormat(d.Format) || d.Format == InternalFormat.R32i, 0)), t.View());
        dummies[key] = sampled;
        dummyTextures.Add(t);   // borrowed image, but the view cut on it is ours to destroy
        return sampled;
    }

    /// <summary>A new texture: the Vulkan image and its transition into GENERAL layout (submitted at once).</summary>
    public GpuTexture CreateTexture(TextureDesc desc) => CreateTexture(desc, default);

    /// <inheritdoc/>
    public bool HasRayQuery => Device.HasRayQuery;
    public bool HasTessellation => Device.HasTessellation;

    /// <inheritdoc/>
    public int MaxSampleCount
    {
        get
        {
            var l = Device.Limits;
            uint both = (uint)(l.FramebufferColorSampleCounts & l.FramebufferDepthSampleCounts);
            int max = 1;
            while (max < 64 && (both & (uint)(max * 2)) != 0) max *= 2;
            return max;
        }
    }

    /// <inheritdoc/>
    public bool ConstantVertexAttributes => true;
    public bool RenderTargetsBottomUp => true;

    /// <inheritdoc/>
    public GpuBuffer DummyVertexArray(ScalarKind kind) => DummyVertex;

    /// <inheritdoc/>
    public int FramesInFlight => Device.Frames.Count;

    /// <inheritdoc/>
    public ulong VideoMemoryBudget => Device.VideoMemory().Budget;

    /// <inheritdoc/>
    public void DeferDelete(Action release) => Device.Frames.DeferDelete(release);

    /// <inheritdoc/>
    public IGpuReadback CreateReadback(ulong size, string name) => ReadbackBuffer.Create(this, size, name);

    /// <inheritdoc/>
    public bool FrameCompleted(long frame) => Device.Frames.CompletedFrame >= frame;

    /// <summary>A texture over someone else's image (the interop's export of a GL texture): the image is not ours to free.</summary>
    internal GpuTexture BorrowTexture(TextureDesc desc, Image image) => new(this, desc, Device.AddTexture(image, null, owned: false, desc));

    /// <summary>A view of a texture's level and layer range (the device cuts it and keeps it).</summary>
    public GpuView View(GpuTexture texture, int baseLevel, int levels, int baseLayer, int layers, GpuViewType type, GpuSwizzle swizzle) =>
        Device.View(texture, baseLevel, levels, baseLayer, layers, type, swizzle);

    /// <summary>Releases a texture: the views cut on it, then the image.</summary>
    public void ReleaseTexture(GpuTexture texture) => Device.ReleaseTexture(texture.Index);

    /// <summary>A new device-local buffer.</summary>
    public GpuBuffer CreateBuffer(ulong size, BufferUse use, string name)
    {
        var buffer = Device.Allocator.CreateBuffer(Math.Max(size, 4), DeviceBuffer.Usage(use) | BufferUsageFlags.TransferDstBit, MemoryKind.DeviceLocal, name);
        return buffer.Handle;
    }

    /// <summary>Releases a device-local buffer (once every frame that could use it has finished).</summary>
    public void ReleaseBuffer(GpuBuffer buffer)
    {
        var entry = Device.BufferEntryAt(buffer.Index);
        if (entry.Allocation is { } allocation) Device.DeferFree(allocation);
    }

    /// <summary>A new texture whose transition is recorded into <paramref name="cmd"/> (the frame's own command buffer) instead of submitted at once.</summary>
    internal GpuTexture CreateTexture(TextureDesc desc, CommandBuffer cmd)
    {
        var info = ImageInfo(desc);
        var image = Device.Allocator.CreateImage(in info, MemoryKind.DeviceLocal, desc.Name.Length > 0 ? desc.Name : "native texture", desc.Streamed);
        bool immediate = cmd.Handle == 0;
        var cb = immediate ? Device.BeginImmediate() : cmd;
        ToGeneral(Device.Vk, cb, image.Image, GlConventions.VkAspect(desc));
        if (immediate) Device.EndImmediate(cb);
        return new GpuTexture(this, desc, Device.AddTexture(image.Image, image, owned: true, desc));
    }

    /// <inheritdoc/>
    /// <remarks>A streamed texture of <see cref="GpuAllocator.OwnMemoryBytes"/> or more gets memory of its own here (the slow vkAllocateMemory is the worker's).</remarks>
    public GpuTexture? CreateTextureOffThread(TextureDesc desc)
    {
        var info = ImageInfo(desc);
        var image = Device.Allocator.CreateImage(in info, MemoryKind.DeviceLocal, desc.Name.Length > 0 ? desc.Name : "native texture", desc.Streamed, ownMemory: true);
        return new GpuTexture(this, desc, Device.AddTexture(image.Image, image, owned: true, desc));
    }

    /// <summary>Records the transition of a texture made by <see cref="CreateTextureOffThread"/> into the layout every texture is used in.</summary>
    internal void InitTexture(GpuTexture texture, CommandBuffer cmd) => ToGeneral(Device.Vk, cmd, Device.TextureAt(texture.Index).Image, GlConventions.VkAspect(texture.Desc));

    static ImageCreateInfo ImageInfo(TextureDesc desc)
    {
        var usage = (ImageUsageFlags)0;
        if (desc.Use.HasFlag(TextureUse.Sampled)) usage |= ImageUsageFlags.SampledBit;
        if (desc.Use.HasFlag(TextureUse.Storage)) usage |= ImageUsageFlags.StorageBit;
        if (desc.Use.HasFlag(TextureUse.ColourTarget)) usage |= ImageUsageFlags.ColorAttachmentBit;
        if (desc.Use.HasFlag(TextureUse.DepthTarget)) usage |= ImageUsageFlags.DepthStencilAttachmentBit;
        if (desc.Use.HasFlag(TextureUse.TransferSrc)) usage |= ImageUsageFlags.TransferSrcBit;
        if (desc.Use.HasFlag(TextureUse.TransferDst)) usage |= ImageUsageFlags.TransferDstBit;
        if (desc.Use.HasFlag(TextureUse.ShadingRate)) usage |= ImageUsageFlags.FragmentShadingRateAttachmentBitKhr;
        int layers = desc.Kind == TextureKind.Cube ? 6 * Math.Max(desc.Layers, 1) : desc.Layers;
        return new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo, ImageType = ImageType.Type2D, Format = GlConventions.VkFormat(desc.Format),
            Extent = new Extent3D((uint)desc.Width, (uint)desc.Height, 1), MipLevels = (uint)desc.Levels, ArrayLayers = (uint)layers,
            Samples = (SampleCountFlags)desc.Samples, Tiling = ImageTiling.Optimal, Usage = usage, SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined, Flags = desc.Kind == TextureKind.Cube ? ImageCreateFlags.CreateCubeCompatibleBit : 0,
        };
    }

    internal static void ToGeneral(Vk vk, CommandBuffer cb, Image image, ImageAspectFlags aspect)
    {
        var barrier = new ImageMemoryBarrier2
        {
            SType = StructureType.ImageMemoryBarrier2,
            SrcStageMask = PipelineStageFlags2.None, SrcAccessMask = AccessFlags2.None,
            DstStageMask = PipelineStageFlags2.AllCommandsBit, DstAccessMask = AccessFlags2.MemoryReadBit | AccessFlags2.MemoryWriteBit,
            OldLayout = ImageLayout.Undefined, NewLayout = ImageLayout.General,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored, DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = image, SubresourceRange = new ImageSubresourceRange(aspect, 0, Vk.RemainingMipLevels, 0, Vk.RemainingArrayLayers),
        };
        var info = new DependencyInfo { SType = StructureType.DependencyInfo, ImageMemoryBarrierCount = 1, PImageMemoryBarriers = &barrier };
        vk.CmdPipelineBarrier2(cb, &info);
    }

    /// <summary>A set that lives as long as the context (set 1 of legacy programs: one per pair of constant chunks).</summary>
    public GpuSet AllocatePersistentSet(GpuSetLayout layout)
    {
        var vkLayout = Shaders.VkLayout(layout);
        var sizes = stackalloc DescriptorPoolSize[] { new(DescriptorType.UniformBufferDynamic, 2048), new(DescriptorType.StorageBufferDynamic, 512) };
        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (persistentPools.Count > 0)
            {
                var ai = new DescriptorSetAllocateInfo { SType = StructureType.DescriptorSetAllocateInfo, DescriptorPool = persistentPools[^1], DescriptorSetCount = 1, PSetLayouts = &vkLayout };
                DescriptorSet set;
                if (Device.Vk.AllocateDescriptorSets(Device.Device, &ai, &set) == Result.Success) return new GpuSet(Device.AddSet(set));
            }
            var pi = new DescriptorPoolCreateInfo { SType = StructureType.DescriptorPoolCreateInfo, MaxSets = 1024, PoolSizeCount = 2, PPoolSizes = sizes };
            VulkanException.Check(Device.Vk.CreateDescriptorPool(Device.Device, &pi, null, out var fresh), "vkCreateDescriptorPool");
            persistentPools.Add(fresh);
        }
        throw new InvalidOperationException("descriptor set allocation failed");
    }

    /// <summary>
    /// A set with one uniform buffer in it, whose offset into the buffer is given when the set is bound
    /// (the constants block: the engine's per-frame constants at a chosen offset).
    /// </summary>
    public GpuSet AllocateConstantSet(GpuSetLayout layout, GpuBuffer buffer, ulong size)
    {
        var set = AllocatePersistentSet(layout);
        var info = new DescriptorBufferInfo(Device.BufferAt(buffer.Index), 0, size);
        var write = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet, DstSet = Device.SetAt(set.Index), DstBinding = 0, DescriptorCount = 1,
            DescriptorType = DescriptorType.UniformBufferDynamic, PBufferInfo = &info,
        };
        Device.Vk.UpdateDescriptorSets(Device.Device, 1, &write, 0, null);
        return set;
    }

    /// <summary>Waits for the GPU, then copies level 0 / layer <paramref name="layer"/> of a colour texture into host memory (tests and tools; never inside a frame).</summary>
    public byte[] ReadBack(GpuTexture texture, int bytesPerTexel, int layer = 0)
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
                ImageSubresource = new ImageSubresourceLayers(GlConventions.VkAspect(texture.Desc), 0, (uint)layer, 1),
                ImageExtent = new Extent3D((uint)w, (uint)h, 1),
            };
            Device.Vk.CmdCopyImageToBuffer(cb, Device.NativeImage(texture), ImageLayout.General, buffer.Buffer, 1, &region);
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
        Shaders.Dispose();
        foreach (var p in persistentPools) Device.Vk.DestroyDescriptorPool(Device.Device, p, null);
        persistentPools.Clear();
        foreach (var t in dummyTextures) t.Dispose();   // the stand-ins' views, before their images (Defaults) go
        dummyTextures.Clear();
        Samplers.Dispose();
        Defaults.Dispose();
    }
}
