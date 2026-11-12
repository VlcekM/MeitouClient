using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using Silk.NET.Vulkan.Extensions.KHR;

namespace Meitou.Game.Rendering.Gpu.Core;

/// <summary>
/// A buffer the backend made: its buffer, and the allocation behind it (null when the buffer is not the allocator's).
/// </summary>
public readonly record struct BufferEntry(Silk.NET.Vulkan.Buffer Buffer, BufferAllocation? Allocation);

/// <summary>
/// A texture the backend made: its image, the allocation behind it (null when borrowed) and its description.
/// </summary>
public readonly record struct TextureEntry(Image Image, ImageAllocation? Allocation, bool Owned, TextureDesc Desc);

/// <summary>
/// Instance, physical and logical device, queues, memory allocator, frame ring and pipeline cache. Headless unless
/// <see cref="VulkanDeviceOptions.CreateSurface"/> is given.
/// <para>
/// Threading: queue submission must come from one thread at a time. The device serialises the submissions it makes
/// itself (<see cref="FrameRing.EndFrame"/>, <see cref="EndImmediate"/>) on <see cref="QueueLock"/>; code that submits
/// on <see cref="GraphicsQueue"/> or <see cref="TransferQueue"/> on its own must take the same lock.
/// </para>
/// </summary>
public sealed unsafe class VulkanDevice : IDisposable
{
    /// <summary>
    /// The Khronos validation layer's name, enabled when the options ask for validation and the loader has it.
    /// </summary>
    const string ValidationLayer = "VK_LAYER_KHRONOS_validation";

    /// <summary>
    /// What the device was created with.
    /// </summary>
    readonly VulkanDeviceOptions _options;
    /// <summary>
    /// The transient pools of the open one-shot command buffers, by command buffer handle (<see cref="BeginImmediate"/>, <see cref="EndImmediate"/>).
    /// </summary>
    readonly ConcurrentDictionary<ulong, CommandPool> _immediatePools = new();
    /// <summary>
    /// Guards <see cref="_validationLog"/> (the debug callback runs on the driver's threads).
    /// </summary>
    readonly object _logGate = new();
    /// <summary>
    /// The last 50 validation messages, oldest first (<see cref="ValidationLog"/>).
    /// </summary>
    readonly List<string> _validationLog = new();
    /// <summary>
    /// The handle to this device the debug callback receives as its user data.
    /// </summary>
    GCHandle _selfHandle;
    /// <summary>
    /// The debug callback delegate, kept alive as long as the messenger.
    /// </summary>
    DebugUtilsMessengerCallbackFunctionEXT? _callbackDelegate;
    /// <summary>
    /// VK_EXT_debug_utils functions; null when the instance does not have the extension.
    /// </summary>
    ExtDebugUtils? _debugUtils;
    /// <summary>
    /// The validation message callback's messenger.
    /// </summary>
    DebugUtilsMessengerEXT _messenger;
    /// <summary>
    /// The KHR_surface functions; null when headless.
    /// </summary>
    KhrSurface? _khrSurface;
    /// <summary>
    /// Where the pipeline cache is loaded from and saved to; null for no file.
    /// </summary>
    string? _pipelineCachePath;
    /// <summary>
    /// Validation errors counted by the debug callback.
    /// </summary>
    int _validationErrors;
    /// <summary>
    /// Validation warnings counted by the debug callback (performance messages not counted).
    /// </summary>
    int _validationWarnings;
    /// <summary>
    /// Set by the first <see cref="Dispose"/>; a second does nothing.
    /// </summary>
    bool _disposed;

    /// <summary>
    /// Stores the options and loads the Vulkan API; the instance and device are made by <see cref="Initialize"/>.
    /// </summary>
    VulkanDevice(VulkanDeviceOptions options)
    {
        this._options = options;
        Vk = Vk.GetApi();
    }

    #region Public state

    /// <summary>
    /// The Vulkan API entry points.
    /// </summary>
    public Vk Vk { get; }
    /// <summary>
    /// The Vulkan instance.
    /// </summary>
    public Instance Instance { get; private set; }
    /// <summary>
    /// The physical device picked by <see cref="PickAndCreateDevice"/> (the best-scoring Vulkan 1.3 device).
    /// </summary>
    public PhysicalDevice PhysicalDevice { get; private set; }
    /// <summary>
    /// The logical device.
    /// </summary>
    public Device Device { get; private set; }
    /// <summary>
    /// The physical device's properties (limits, type, API version).
    /// </summary>
    public PhysicalDeviceProperties Properties { get; private set; }
    /// <summary>
    /// The physical device's name.
    /// </summary>
    public string DeviceName { get; private set; } = "";
    /// <summary>
    /// The driver's name and version as the driver reports them ("NVIDIA 581.80"); empty when it reports neither.
    /// </summary>
    public string DriverName { get; private set; } = "";
    /// <summary>
    /// The physical device's Vulkan API version.
    /// </summary>
    public uint ApiVersion => Properties.ApiVersion;
    /// <summary>
    /// The surface from <see cref="VulkanDeviceOptions.CreateSurface"/> (default when headless); destroyed on dispose.
    /// </summary>
    public SurfaceKHR Surface { get; private set; }
    /// <summary>
    /// Whether there is a surface (false when headless).
    /// </summary>
    public bool HasSurface => Surface.Handle != 0;

    /// <summary>
    /// Puts <paramref name="surface"/> (the same window's, made another way) in place of the surface, which is destroyed. Before any swapchain
    /// exists.
    /// </summary>
    public void ReplaceSurface(SurfaceKHR surface)
    {
        if (Surface.Handle != 0 && _khrSurface != null) _khrSurface.DestroySurface(Instance, Surface, null);

        Surface = surface;
    }

    /// <summary>
    /// The KHR_surface instance extension functions (null when headless).
    /// </summary>
    public KhrSurface? SurfaceExtension => _khrSurface;

    /// <summary>
    /// The graphics and compute queue (index 0 of <see cref="GraphicsFamily"/>); the frames are submitted to it.
    /// </summary>
    public Queue GraphicsQueue { get; private set; }
    /// <summary>
    /// The queue family of <see cref="GraphicsQueue"/>: the first with graphics and compute that can present to the surface.
    /// </summary>
    public uint GraphicsFamily { get; private set; }
    /// <summary>
    /// Further queues of the graphics family (index 1.. of the family), as many as <see cref="VulkanDeviceOptions.ExtraGraphicsQueues"/> asked for
    /// and the family offers; empty by default. Only their users submit on them (frame generation's swapchain, through its own locks), not the
    /// device.
    /// </summary>
    public Queue[] ExtraQueues { get; private set; } = [];
    /// <summary>
    /// The transfer queue: a dedicated transfer family's when the device has one (<see cref="HasDedicatedTransfer"/>).
    /// </summary>
    public Queue TransferQueue { get; private set; }
    /// <summary>
    /// The queue family of <see cref="TransferQueue"/>.
    /// </summary>
    public uint TransferFamily { get; private set; }
    /// <summary>
    /// Whether <see cref="TransferQueue"/> is on a family of its own rather than the graphics family.
    /// </summary>
    public bool HasDedicatedTransfer { get; private set; }
    /// <summary>
    /// Held around every queue submission this device makes; take it when submitting yourself.
    /// </summary>
    public object QueueLock { get; } = new();

    /// <summary>
    /// The device memory allocator.
    /// </summary>
    public GpuAllocator Allocator { get; private set; } = null!;
    /// <summary>
    /// The pipeline cache, loaded from and saved to <see cref="VulkanDeviceOptions.PipelineCachePath"/>.
    /// </summary>
    public PipelineCache PipelineCache { get; private set; }
    /// <summary>
    /// The frame ring: frames in flight, their fences and deferred deletes.
    /// </summary>
    public FrameRing Frames { get; private set; } = null!;
    /// <summary>
    /// The report a lost device writes (<see cref="Check"/>), with the pass markers when tracking.
    /// </summary>
    public DeviceFault Faults { get; private set; } = null!;
    /// <summary>
    /// What is and was at each GPU address (<c>MEITOU_DIAG_FAULT=1</c>, <see cref="DeviceFault.Tracking"/>); null when not tracked.
    /// </summary>
    public GpuAddressLog? AddressLog { get; private set; }
    /// <summary>
    /// Whether VK_EXT_device_fault is enabled (a lost device then tells its fault).
    /// </summary>
    public bool HasDeviceFault { get; private set; }
    /// <summary>
    /// Whether VK_EXT_device_address_binding_report is enabled (only when tracking: the driver then reports each range it binds).
    /// </summary>
    public bool HasAddressBindingReport { get; private set; }

    /// <summary>
    /// Guards the handle tables below. They are added to from any thread (the allocator's callers, the recording jobs' views) under this lock and
    /// read without it (an index never moves). Slots are not reused: the renderers' caches compare views and buffers by index, so a reused slot would
    /// pass for the old resource. A released one is cleared once no frame in flight can read it, so what it held goes to the GC.
    /// </summary>
    readonly object _tableGate = new();

    /// <summary>
    /// The samplers the renderers hold by index (index 0 is the null sampler).
    /// </summary>
    readonly List<Sampler> _samplerTable = [default];
    /// <summary>
    /// The sampler at an index. Any thread.
    /// </summary>
    public Sampler SamplerAt(int index) => _samplerTable[index];

    /// <summary>
    /// Adds a sampler to the table and returns its index. Any thread.
    /// </summary>
    public int AddSampler(Sampler sampler)
    {
        lock (_tableGate)
        {
            _samplerTable.Add(sampler);
            return _samplerTable.Count - 1;
        }
    }

    /// <summary>
    /// The views the renderers hold by index (index 0 is the null view).
    /// </summary>
    readonly List<ImageView> _viewTable = [default];
    /// <summary>
    /// The view at an index. Any thread.
    /// </summary>
    public ImageView ViewAt(int index) => _viewTable[index];

    /// <summary>
    /// Adds a view to the table and returns its index. Any thread.
    /// </summary>
    public int AddView(ImageView view)
    {
        lock (_tableGate)
        {
            _viewTable.Add(view);
            return _viewTable.Count - 1;
        }
    }

    /// <summary>
    /// The sets the renderers bind by index (index 0 is the null set): the persistent ones (per-draw sets are bound by their own handle).
    /// </summary>
    readonly List<DescriptorSet> _setTable = [default];
    /// <summary>
    /// The set at an index. Any thread.
    /// </summary>
    public DescriptorSet SetAt(int index) => _setTable[index];

    /// <summary>
    /// Adds a set to the table and returns its index. Any thread.
    /// </summary>
    public int AddSet(DescriptorSet set)
    {
        lock (_tableGate)
        {
            _setTable.Add(set);
            return _setTable.Count - 1;
        }
    }

    /// <summary>
    /// The textures the renderers hold by index (index 0 is the null texture).
    /// </summary>
    readonly List<TextureEntry> _textureTable = [default];
    /// <summary>
    /// The textures already passed to <see cref="ReleaseTexture"/> whose deferred release has not run yet (a second release is ignored).
    /// </summary>
    readonly HashSet<int> _releasedTextures = [];
    /// <summary>
    /// The texture at an index. Any thread.
    /// </summary>
    public TextureEntry TextureAt(int index) => _textureTable[index];

    /// <summary>
    /// Adds a texture to the table and returns its index. Any thread.
    /// </summary>
    public int AddTexture(Image image, ImageAllocation? allocation, bool owned, TextureDesc desc)
    {
        lock (_tableGate)
        {
            _textureTable.Add(new TextureEntry(image, allocation, owned, desc));
            return _textureTable.Count - 1;
        }
    }

    /// <summary>
    /// Releases a texture: the views cut on it, then the image, and clears their slots (once every frame that could use them has finished). A second
    /// release of the same texture does nothing.
    /// </summary>
    public void ReleaseTexture(int index)
    {
        TextureEntry entry;

        lock (_tableGate)
        {
            entry = _textureTable[index];

            // The entry and its views stay until the frames in flight are done (a pass recorded later this frame may still name them).
            if (entry.Image.Handle == 0 || !_releasedTextures.Add(index)) return;
        }

        var alloc = entry.Allocation;
        Frames.DeferDelete(() =>
        {
            lock (_tableGate)
            {
                if (_textureViews.Remove(index, out var cut))
                {
                    foreach (var v in cut)
                    {
                        Vk.DestroyImageView(Device, _viewTable[v], null);
                        _viewCache.TryRemove(_viewKeys[v], out _);
                        _viewKeys.Remove(v);
                        _viewTable[v] = default;
                    }
                }

                _textureTable[index] = default;
                _releasedTextures.Remove(index);
            }

            if (alloc is not null) Allocator.Free(alloc);
        });
    }

    /// <summary>
    /// The views cut on a texture, by the range they cover (the backend's own cache), and per texture the views cut on it (its release takes those,
    /// not a walk over every view). The cache is read without the lock: every draw asks for its views, and a lock there was contended by the
    /// streaming threads' table adds (6% of the render thread's samples on the hub view).
    /// </summary>
    readonly ConcurrentDictionary<(int Texture, int BaseLevel, int Levels, int BaseLayer, int Layers, GpuViewType Type, int Swizzle), int> _viewCache =
        [];
    /// <summary>
    /// The cache key of each view in <see cref="_viewCache"/>, by view index (a texture's release removes them from the cache).
    /// </summary>
    readonly Dictionary<int, (int Texture, int BaseLevel, int Levels, int BaseLayer, int Layers, GpuViewType Type, int Swizzle)> _viewKeys = [];
    /// <summary>
    /// The views cut on each texture, by texture index.
    /// </summary>
    readonly Dictionary<int, List<int>> _textureViews = [];

    /// <summary>
    /// A view of a texture's level and layer range: cut on the first ask and kept until the texture is released. Any thread.
    /// </summary>
    public unsafe GpuView View(GpuTexture texture, int baseLevel, int levels, int baseLayer, int layers, GpuViewType type, GpuSwizzle swizzle)
    {
        var key = (texture.Index, baseLevel, levels, baseLayer, layers, type,
            (int)swizzle.R | (int)swizzle.G << 4 | (int)swizzle.B << 8 | (int)swizzle.A << 12);
        if (_viewCache.TryGetValue(key, out var index)) return new GpuView(index);

        lock (_tableGate)
        {
            if (_viewCache.TryGetValue(key, out index)) return new GpuView(index);

            var info = new ImageViewCreateInfo
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = _textureTable[texture.Index].Image,
                ViewType = VkViewType(type),
                Format = GlConventions.VkFormat(texture.Desc.Format),
                Components = VkSwizzle(swizzle),
                SubresourceRange = new ImageSubresourceRange(GlConventions.VkAspect(texture.Desc), (uint)baseLevel, (uint)levels, (uint)baseLayer,
                    (uint)layers),
            };
            VulkanException.Check(Vk.CreateImageView(Device, &info, null, out var view), "vkCreateImageView");
            _viewTable.Add(view);
            index = _viewTable.Count - 1;
            _viewCache[key] = index;
            _viewKeys[index] = key;
            if (!_textureViews.TryGetValue(texture.Index, out var list)) _textureViews[texture.Index] = list = [];

            list.Add(index);
            return new GpuView(index);
        }
    }

    /// <summary>
    /// The buffers the renderers hold by index (index 0 is the null buffer; a freed one is cleared, <see cref="RemoveBuffer"/>).
    /// </summary>
    readonly List<BufferEntry> _bufferTable = [default];
    /// <summary>
    /// The buffer at an index. Any thread.
    /// </summary>
    public Silk.NET.Vulkan.Buffer BufferAt(int index) => _bufferTable[index].Buffer;
    /// <summary>
    /// The buffer and its allocation at an index. Any thread.
    /// </summary>
    public BufferEntry BufferEntryAt(int index) => _bufferTable[index];

    /// <summary>
    /// Adds a buffer to the table and returns its index. Any thread.
    /// </summary>
    public int AddBuffer(BufferEntry buffer)
    {
        lock (_tableGate)
        {
            _bufferTable.Add(buffer);
            return _bufferTable.Count - 1;
        }
    }

    /// <summary>
    /// Clears a destroyed buffer's slot (the allocator's free, which runs once no frame in flight can use it).
    /// </summary>
    internal void RemoveBuffer(int index)
    {
        if (index <= 0) return;

        lock (_tableGate)
        {
            if (_bufferTable[index].Buffer.Handle == 0) return;

            _bufferTable[index] = default;
        }
    }

    /// <summary>
    /// The buffer's device address (it must have been made with the device address flag).
    /// </summary>
    public ulong Address(GpuBuffer buffer)
    {
        var info = new BufferDeviceAddressInfo { SType = StructureType.BufferDeviceAddressInfo, Buffer = BufferAt(buffer.Index) };
        return Vk.GetBufferDeviceAddress(Device, in info);
    }

    /// <summary>
    /// The Vulkan view type of a view.
    /// </summary>
    static ImageViewType VkViewType(GpuViewType t) => t switch
    {
        GpuViewType.Texture2DArray => ImageViewType.Type2DArray,
        GpuViewType.Cube => ImageViewType.TypeCube,
        _ => ImageViewType.Type2D,
    };

    /// <summary>
    /// The Vulkan component mapping of a view's swizzle.
    /// </summary>
    static ComponentMapping VkSwizzle(GpuSwizzle s) => new()
    {
        R = VkSwizzleComponent(s.R),
        G = VkSwizzleComponent(s.G),
        B = VkSwizzleComponent(s.B),
        A = VkSwizzleComponent(s.A),
    };

    /// <summary>
    /// The Vulkan swizzle of one component.
    /// </summary>
    static ComponentSwizzle VkSwizzleComponent(GpuSwizzleComponent c) => c switch
    {
        GpuSwizzleComponent.Identity => ComponentSwizzle.Identity,
        GpuSwizzleComponent.R => ComponentSwizzle.R,
        GpuSwizzleComponent.G => ComponentSwizzle.G,
        GpuSwizzleComponent.B => ComponentSwizzle.B,
        GpuSwizzleComponent.A => ComponentSwizzle.A,
        GpuSwizzleComponent.One => ComponentSwizzle.One,
        _ => ComponentSwizzle.Zero,
    };

    /// <summary>
    /// The native image (Vulkan's): the paths that copy or clear an image themselves need the handle.
    /// </summary>
    public Image NativeImage(GpuTexture texture) => TextureAt(texture.Index).Image;

    /// <summary>
    /// The native image's usage flags (Vulkan's): the vendor upscalers ask for them.
    /// </summary>
    public ImageUsageFlags NativeUsage(GpuTexture texture) => TextureAt(texture.Index).Allocation?.Usage ?? 0;

    /// <summary>
    /// The native view (Vulkan's): the DLSS paths (Streamline, NGX) need the handle itself, and those paths are NVIDIA's.
    /// </summary>
    public ImageView NativeView(GpuTexture texture, int level = 0, int layer = 0) =>
        ViewAt(View(texture, level, 1, layer, 1, GpuViewType.Texture2D, default).Index);
    /// <summary>
    /// The frames in flight the device was created with.
    /// </summary>
    public int FramesInFlight => _options.FramesInFlight;

    /// <summary>
    /// The validation layer and VK_EXT_debug_utils are both on.
    /// </summary>
    public bool ValidationEnabled { get; private set; }
    /// <summary>
    /// Synchronisation validation was asked for and the layer took the setting.
    /// </summary>
    public bool SyncValidationEnabled { get; private set; }
    /// <summary>
    /// VK_EXT_debug_utils (labels and names), null when the instance does not have it.
    /// </summary>
    public ExtDebugUtils? DebugUtils => _debugUtils;
    /// <summary>
    /// Validation errors so far.
    /// </summary>
    public int ValidationErrors => Volatile.Read(ref _validationErrors);
    /// <summary>
    /// Validation warnings so far (performance messages not counted).
    /// </summary>
    public int ValidationWarnings => Volatile.Read(ref _validationWarnings);
    /// <summary>
    /// The last ~50 validation messages (errors and warnings), oldest first.
    /// </summary>
    public string[] ValidationLog
    {
        get
        {
            lock (_logGate)
            {
                return _validationLog.ToArray();
            }
        }
    }

    // Features and extensions (true = enabled on the device).
    /// <summary>
    /// Dynamic rendering (core in Vulkan 1.3; a device without it is not picked).
    /// </summary>
    public bool HasDynamicRendering { get; private set; }
    /// <summary>
    /// Synchronization2 (core in Vulkan 1.3; a device without it is not picked).
    /// </summary>
    public bool HasSynchronization2 { get; private set; }
    /// <summary>
    /// Timeline semaphores (core in Vulkan 1.2; a device without them is not picked).
    /// </summary>
    public bool HasTimelineSemaphore { get; private set; }
    /// <summary>
    /// Core in Vulkan 1.3: always available.
    /// </summary>
    public bool HasExtendedDynamicState { get; private set; }
    /// <summary>
    /// VK_EXT_extended_dynamic_state2 is on with its core features.
    /// </summary>
    public bool HasExtendedDynamicState2 { get; private set; }
    /// <summary>
    /// <see cref="HasExtendedDynamicState2"/> with dynamic logic op.
    /// </summary>
    public bool HasExtendedDynamicState2LogicOp { get; private set; }
    /// <summary>
    /// <see cref="HasExtendedDynamicState2"/> with dynamic patch control points.
    /// </summary>
    public bool HasExtendedDynamicState2PatchControlPoints { get; private set; }
    /// <summary>
    /// Line and point polygon modes (the core <c>fillModeNonSolid</c> feature).
    /// </summary>
    public bool FillModeNonSolid { get; private set; }
    /// <summary>
    /// Depth clamp (the core <c>depthClamp</c> feature).
    /// </summary>
    public bool DepthClamp { get; private set; }
    /// <summary>
    /// Anisotropic filtering (the core <c>samplerAnisotropy</c> feature).
    /// </summary>
    public bool SamplerAnisotropy { get; private set; }
    /// <summary>
    /// Tessellation shaders are on (the core feature; the ground displacement uses them, GroundRelief).
    /// </summary>
    public bool HasTessellation { get; private set; }
    /// <summary>
    /// Pipeline statistics queries are on (<see cref="VulkanDeviceOptions.TriangleMeasurements"/> and the device has them).
    /// </summary>
    public bool HasPipelineStatistics { get; private set; }
    /// <summary>
    /// VK_KHR_fragment_shader_barycentric is on (<see cref="VulkanDeviceOptions.TriangleMeasurements"/> and the device has it).
    /// </summary>
    public bool HasFragmentBarycentric { get; private set; }
    /// <summary>
    /// Buffer device address was asked for by <see cref="VulkanDeviceOptions.TriangleMeasurements"/> or <see cref="VulkanDeviceOptions.RayTracing"/>
    /// and the device has it.
    /// </summary>
    public bool HasBufferDeviceAddress { get; private set; }
    /// <summary>
    /// Ray queries against acceleration structures (VK_KHR_acceleration_structure, VK_KHR_ray_query, VK_KHR_deferred_host_operations):
    /// <see cref="VulkanDeviceOptions.RayTracing"/> was asked for and the device has them with buffer device address. Device-local memory is then
    /// allocated with the device address flag (<see cref="GpuAllocator"/>).
    /// </summary>
    public bool HasRayQuery { get; private set; }
    /// <summary>
    /// With <see cref="HasRayQuery"/>: the acceleration structure entry points.
    /// </summary>
    public KhrAccelerationStructure? AccelerationStructures { get; private set; }
    /// <summary>
    /// With <see cref="HasRayQuery"/>: the scratch buffer alignment of acceleration structure builds.
    /// </summary>
    public uint ScratchAlignment { get; private set; } = 256;
    /// <summary>
    /// VK_KHR_fragment_shading_rate is on with the attachment rate (variable-rate shading from an image) and the device can use an R8_UINT image as
    /// that attachment. False without the extension (an integrated GPU may lack it): the fog shading rate (<c>fog-vrs</c>) then does nothing.
    /// </summary>
    public bool HasFragmentShadingRate { get; private set; }
    /// <summary>
    /// The shading rate attachment's texel size to use (one rate per this many pixels on a side; 16 where the device allows it, else its smallest).
    /// </summary>
    public int ShadingRateTexel { get; private set; }
    /// <summary>
    /// The largest square fragment size (pixels on a side) the device lists as a shading rate: 2 or 4.
    /// </summary>
    public int MaxShadingRate { get; private set; } = 1;
    /// <summary>
    /// Draws whose shader writes the fragment depth stay at one invocation per pixel on this device (the property is false).
    /// </summary>
    public bool ShadingRateForcedByDepthWrite { get; private set; }
    /// <summary>
    /// BC texture compression (the core <c>textureCompressionBC</c> feature).
    /// </summary>
    public bool TextureCompressionBc { get; private set; }
    /// <summary>
    /// Per-attachment blend state (the core <c>independentBlend</c> feature).
    /// </summary>
    public bool IndependentBlend { get; private set; }
    /// <summary>
    /// Cube map arrays (the core <c>imageCubeArray</c> feature).
    /// </summary>
    public bool ImageCubeArray { get; private set; }
    /// <summary>
    /// VK_KHR_push_descriptor is on.
    /// </summary>
    public bool HasPushDescriptor { get; private set; }
    /// <summary>
    /// The most descriptors one push may write (the device's limit; at most 16 on macOS, where MoltenVK binds pushed sets through MSL attributes).
    /// </summary>
    public uint MaxPushDescriptors { get; private set; }
    /// <summary>
    /// Extension present and all of the five sub-features below supported.
    /// </summary>
    public bool HasExtendedDynamicState3 { get; private set; }
    /// <summary>
    /// VK_EXT_extended_dynamic_state3 with dynamic colour blend enable.
    /// </summary>
    public bool Eds3ColorBlendEnable { get; private set; }
    /// <summary>
    /// VK_EXT_extended_dynamic_state3 with dynamic colour write mask.
    /// </summary>
    public bool Eds3ColorWriteMask { get; private set; }
    /// <summary>
    /// VK_EXT_extended_dynamic_state3 with dynamic alpha-to-coverage.
    /// </summary>
    public bool Eds3AlphaToCoverageEnable { get; private set; }
    /// <summary>
    /// VK_EXT_extended_dynamic_state3 with dynamic polygon mode.
    /// </summary>
    public bool Eds3PolygonMode { get; private set; }
    /// <summary>
    /// VK_EXT_extended_dynamic_state3 with dynamic depth clamp enable.
    /// </summary>
    public bool Eds3DepthClampEnable { get; private set; }
    /// <summary>
    /// VK_EXT_vertex_input_dynamic_state is on.
    /// </summary>
    public bool HasVertexInputDynamicState { get; private set; }
    /// <summary>
    /// VK_KHR_swapchain is on (the device was made with a surface).
    /// </summary>
    public bool HasSwapchain { get; private set; }
    // What the native renderer API needs (docs/renderer-native.md 1, owner decision 1): enabled when present; GpuFeatures refuses to
    // start without the required ones.
    /// <summary>
    /// Descriptor indexing with every sub-feature the bindless table uses (runtime arrays, partially bound, variable count, sampled-image update
    /// after bind, update unused while pending, non-uniform sampled-image indexing).
    /// </summary>
    public bool HasBindless { get; private set; }
    /// <summary>
    /// Multi-draw indirect (the core <c>multiDrawIndirect</c> feature).
    /// </summary>
    public bool MultiDrawIndirect { get; private set; }
    /// <summary>
    /// Indirect draws with a first instance other than 0 (the core <c>drawIndirectFirstInstance</c> feature).
    /// </summary>
    public bool DrawIndirectFirstInstance { get; private set; }
    /// <summary>
    /// Indirect draws with a GPU-written count (Vulkan 1.2 <c>drawIndirectCount</c>).
    /// </summary>
    public bool DrawIndirectCount { get; private set; }
    /// <summary>
    /// <c>gl_DrawID</c> / base vertex and instance in shaders (Vulkan 1.1 <c>shaderDrawParameters</c>).
    /// </summary>
    public bool ShaderDrawParameters { get; private set; }

    /// <summary>
    /// The physical device's limits.
    /// </summary>
    public PhysicalDeviceLimits Limits => Properties.Limits;

    /// <summary>
    /// An integrated GPU (shares the system's memory): the budgets that scale with video memory take a smaller share of it (<c>ImpostorBudget</c>).
    /// </summary>
    public bool IsIntegrated => Properties.DeviceType == PhysicalDeviceType.IntegratedGpu || ForceIntegrated;

    /// <summary>
    /// <c>MEITOU_FORCE_INTEGRATED=1</c> makes <see cref="IsIntegrated"/> true on any GPU (to see the integrated-GPU budgets on a discrete card, with
    /// <c>MEITOU_VRAM_BUDGET_MB</c>).
    /// </summary>
    static readonly bool ForceIntegrated = Environment.GetEnvironmentVariable("MEITOU_FORCE_INTEGRATED") == "1";

    /// <summary>
    /// Whether VK_EXT_memory_budget is enabled (<see cref="VideoMemory"/> then reports the driver's figures).
    /// </summary>
    public bool HasMemoryBudget { get; private set; }

    /// <summary>
    /// Device-local memory summed over the device-local heaps: this process's usage and the budget the driver allows it (VK_EXT_memory_budget;
    /// the usage includes the driver's own allocations). Without the extension, the allocator's bytes and the heaps' size.
    /// </summary>
    public (ulong Used, ulong Budget) VideoMemory()
    {
        var budget = new PhysicalDeviceMemoryBudgetPropertiesEXT { SType = StructureType.PhysicalDeviceMemoryBudgetPropertiesExt };
        var props = new PhysicalDeviceMemoryProperties2
        {
            SType = StructureType.PhysicalDeviceMemoryProperties2, PNext = HasMemoryBudget ? &budget : null,
        };
        Vk.GetPhysicalDeviceMemoryProperties2(PhysicalDevice, &props);
        ulong used = 0, total = 0;

        for (int i = 0; i < props.MemoryProperties.MemoryHeapCount; i++)
        {
            var heap = props.MemoryProperties.MemoryHeaps[i];
            if ((heap.Flags & MemoryHeapFlags.DeviceLocalBit) == 0) continue;

            used += HasMemoryBudget ? budget.HeapUsage[i] : 0;
            total += HasMemoryBudget ? budget.HeapBudget[i] : heap.Size;
        }

        return (HasMemoryBudget ? used : Allocator.TotalAllocatedBytes, Math.Min(total, BudgetOverride));
    }

    /// <summary>
    /// Every memory heap: its flags, size, and this process's usage and budget (VK_EXT_memory_budget; 0 without it). For the VRAM dump.
    /// </summary>
    public List<(MemoryHeapFlags Flags, ulong Size, ulong Usage, ulong Budget)> Heaps()
    {
        var budget = new PhysicalDeviceMemoryBudgetPropertiesEXT { SType = StructureType.PhysicalDeviceMemoryBudgetPropertiesExt };
        var props = new PhysicalDeviceMemoryProperties2
        {
            SType = StructureType.PhysicalDeviceMemoryProperties2, PNext = HasMemoryBudget ? &budget : null,
        };
        Vk.GetPhysicalDeviceMemoryProperties2(PhysicalDevice, &props);
        var heaps = new List<(MemoryHeapFlags, ulong, ulong, ulong)>();

        for (int i = 0; i < props.MemoryProperties.MemoryHeapCount; i++)
        {
            var heap = props.MemoryProperties.MemoryHeaps[i];
            heaps.Add((heap.Flags, heap.Size, HasMemoryBudget ? budget.HeapUsage[i] : 0, HasMemoryBudget ? budget.HeapBudget[i] : 0));
        }

        return heaps;
    }

    /// <summary>
    /// The device-local heaps' size: the card's video memory (an integrated GPU's share of the system's).
    /// </summary>
    public ulong DeviceLocalBytes()
    {
        PhysicalDeviceMemoryProperties props;
        Vk.GetPhysicalDeviceMemoryProperties(PhysicalDevice, &props);
        ulong total = 0;
        for (int i = 0; i < props.MemoryHeapCount; i++)
            if ((props.MemoryHeaps[i].Flags & MemoryHeapFlags.DeviceLocalBit) != 0) total += props.MemoryHeaps[i].Size;

        return total;
    }

    /// <summary>
    /// <c>MEITOU_VRAM_BUDGET_MB</c> makes <see cref="VideoMemory"/> report at most that budget (a card with less memory, for testing the
    /// memory-pressure guard, <c>VramGuard</c>, and the viewer's watch on a bigger one).
    /// </summary>
    static readonly ulong BudgetOverride =
        ulong.TryParse(Environment.GetEnvironmentVariable("MEITOU_VRAM_BUDGET_MB"), out var mb) && mb > 0 ? mb << 20 : ulong.MaxValue;

    /// <summary>
    /// One line per feature, for logs and test output.
    /// </summary>
    public string DescribeFeatures()
    {
        var sb = new StringBuilder();
        var v = Properties.ApiVersion;
        sb.AppendLine($"Device: {DeviceName}, Vulkan {v >> 22}.{(v >> 12) & 0x3FF}.{v & 0xFFF}, type {Properties.DeviceType}, " +
            $"validation {(ValidationEnabled ? SyncValidationEnabled ? "on (sync)" : _options.GpuValidation ? "on (gpu)" : "on" : "off")}");
        sb.AppendLine($"Queues: graphics family {GraphicsFamily}, transfer family {TransferFamily}" +
            $"{(HasDedicatedTransfer ? " (dedicated)" : " (shared with graphics)")}");
        sb.AppendLine($"dynamicRendering={HasDynamicRendering} synchronization2={HasSynchronization2} timelineSemaphore={HasTimelineSemaphore} " +
            $"extendedDynamicState={HasExtendedDynamicState}");
        sb.AppendLine($"extendedDynamicState2={HasExtendedDynamicState2} (logicOp={HasExtendedDynamicState2LogicOp}, " +
            $"patchControlPoints={HasExtendedDynamicState2PatchControlPoints})");
        sb.AppendLine($"fillModeNonSolid={FillModeNonSolid} depthClamp={DepthClamp} samplerAnisotropy={SamplerAnisotropy} " +
            $"tessellationShader={HasTessellation} textureCompressionBC={TextureCompressionBc} independentBlend={IndependentBlend} " +
            $"imageCubeArray={ImageCubeArray}");
        sb.AppendLine($"bindless={HasBindless} multiDrawIndirect={MultiDrawIndirect} drawIndirectFirstInstance={DrawIndirectFirstInstance} " +
            $"drawIndirectCount={DrawIndirectCount} shaderDrawParameters={ShaderDrawParameters}");
        sb.AppendLine($"pushDescriptor={HasPushDescriptor} (max {MaxPushDescriptors}) vertexInputDynamicState={HasVertexInputDynamicState} " +
            $"swapchain={HasSwapchain}");
        sb.AppendLine($"rayQuery={HasRayQuery} bufferDeviceAddress={HasBufferDeviceAddress}");
        sb.AppendLine($"fragmentShadingRate={HasFragmentShadingRate} (attachment texel {ShadingRateTexel}, largest square rate " +
            $"{MaxShadingRate}x{MaxShadingRate}, forced to 1x1 by depth writes {ShadingRateForcedByDepthWrite})");
        sb.AppendLine($"extendedDynamicState3={HasExtendedDynamicState3} (colorBlendEnable={Eds3ColorBlendEnable} " +
            $"colorWriteMask={Eds3ColorWriteMask} alphaToCoverage={Eds3AlphaToCoverageEnable} polygonMode={Eds3PolygonMode} " +
            $"depthClamp={Eds3DepthClampEnable})");
        sb.Append($"Limits: minUniformBufferOffsetAlignment={Limits.MinUniformBufferOffsetAlignment} timestampPeriod={Limits.TimestampPeriod}ns " +
            $"bufferImageGranularity={Limits.BufferImageGranularity}");
        return sb.ToString();
    }

    #endregion

    #region Creation

    /// <summary>
    /// Creates the device. Throws <see cref="VulkanException"/> when no suitable Vulkan 1.3 device exists.
    /// </summary>
    public static VulkanDevice Create(VulkanDeviceOptions o)
    {
        if (o.FramesInFlight is < 2 or > 3)
        {
            throw new ArgumentOutOfRangeException(nameof(o), "FramesInFlight must be 2..3");
        }

        if (OperatingSystem.IsMacOS())
        {
            // macOS finds the Vulkan loader through DYLD_LIBRARY_PATH, which SIP-restricted launchers
            // (nohup among them) silently strip; what breaks then reads like a missing OS feature -
            // GLFW "doesn't support Vulkan", or a FileNotFoundException deep inside Silk.NET. Name
            // the real cause here, at first touch, while the launch environment is still in question.
            if (!NativeLibrary.TryLoad("libvulkan.1.dylib", out _))
            {
                throw new InvalidOperationException(
                    $"the Vulkan loader (libvulkan.1.dylib) is not findable on this macOS: " +
                    $"DYLD_LIBRARY_PATH='{Environment.GetEnvironmentVariable("DYLD_LIBRARY_PATH")}' - the variable is absent from the launch " +
                    $"environment, or a restricted launcher stripped it (nohup does); export DYLD_LIBRARY_PATH=/opt/homebrew/lib and launch " +
                    $"plainly (a script, a shell function or 'cmd &', not 'nohup cmd &')");
            }
        }

        var d = new VulkanDevice(o);

        try
        {
            d.Initialize();
            return d;
        }
        catch
        {
            d.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Creates the instance, the surface (when the options make one), the device, the allocator, the pipeline cache and the frame ring; the allocator
    /// may reclaim memory by retiring submitted frames, but only on the thread that created the device.
    /// </summary>
    void Initialize()
    {
        CreateInstance();

        if (_options.CreateSurface != null)
        {
            Surface = _options.CreateSurface(Instance);
            Vk.TryGetInstanceExtension(Instance, out _khrSurface);
        }

        PickAndCreateDevice();

        // Before the allocator: the log hears of every range from the first.
        if (HasAddressBindingReport) AddressLog = GpuAddressLog.Create(this, _debugUtils!);
        Allocator = new GpuAllocator(this);
        CreatePipelineCache();
        Frames = new FrameRing(this, _options.FramesInFlight);
        Faults = new DeviceFault(this, AddressLog);
        int renderThread = Environment.CurrentManagedThreadId;
        Allocator.Reclaim = () =>
        {
            if (Environment.CurrentManagedThreadId != renderThread) return false;

            bool retired = Frames.RetireSubmitted();
            return Allocator.Trim() > 0 || retired;
        };
        _options.DeviceCreated?.Invoke(this);
    }

    /// <summary>
    /// Whether the loader lists the validation layer.
    /// </summary>
    bool LayerAvailable()
    {
        uint n = 0;

        if (Vk.EnumerateInstanceLayerProperties(&n, null) != Result.Success || n == 0)
        {
            return false;
        }

        var layers = new LayerProperties[n];

        fixed (LayerProperties* p = layers)
        {
            Vk.EnumerateInstanceLayerProperties(&n, p);

            for (int i = 0; i < n; i++)
            {
                if (Marshal.PtrToStringAnsi((nint)p[i].LayerName) == ValidationLayer)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Whether the loader (or <paramref name="layer"/>, for an extension a layer provides) has the instance extension.
    /// </summary>
    bool InstanceExtensionAvailable(string name, string? layer = null)
    {
        uint n = 0;
        var layerName = layer is null ? null : (byte*)SilkMarshal.StringToPtr(layer);

        try
        {
            Vk.EnumerateInstanceExtensionProperties(layerName, &n, null);
            var exts = new ExtensionProperties[n];

            fixed (ExtensionProperties* p = exts)
            {
                Vk.EnumerateInstanceExtensionProperties(layerName, &n, p);

                for (int i = 0; i < n; i++)
                {
                    if (Marshal.PtrToStringAnsi((nint)p[i].ExtensionName) == name)
                    {
                        return true;
                    }
                }
            }

            return false;
        }
        finally
        {
            if (layerName != null) SilkMarshal.Free((nint)layerName);
        }
    }

    /// <summary>
    /// The instance, with the validation layer and debug utils where available, and the validation messenger.
    /// </summary>
    void CreateInstance()
    {
        bool wantValidation = _options.Validation ?? true;
        bool layer = wantValidation && LayerAvailable();

        if (_options.Validation == true && !layer)
        {
            throw new VulkanException($"{ValidationLayer} is not installed");
        }

        // Debug utils (object names, command labels for capture tools and the native API's passes) whenever the loader has it, with or
        // without validation.
        bool debugExt = InstanceExtensionAvailable("VK_EXT_debug_utils");
        ValidationEnabled = layer && debugExt;
        bool layerSettings = ValidationEnabled && (_options.SyncValidation || _options.GpuValidation)
            && InstanceExtensionAvailable("VK_EXT_layer_settings", ValidationLayer);

        // macOS: the loader hides portability drivers (MoltenVK) from instances that do not opt in
        // (VK_INSTANCE_CREATE_ENUMERATE_PORTABILITY_BIT_KHR).
        bool portability = OperatingSystem.IsMacOS();
        var extNames = InstanceExtensionNames(debugExt, layerSettings, portability);
        CreateVkInstance(extNames, layerSettings, portability);

        if (debugExt)
        {
            Vk.TryGetInstanceExtension(Instance, out _debugUtils);
        }

        if (ValidationEnabled) CreateDebugMessenger();
    }

    /// <summary>
    /// The instance extensions to enable: debug utils, layer settings, the caller's, the surface's and (macOS) portability enumeration.
    /// </summary>
    List<string> InstanceExtensionNames(bool debugExt, bool layerSettings, bool portability)
    {
        var extNames = new List<string>();

        if (debugExt)
        {
            extNames.Add("VK_EXT_debug_utils");
        }

        if (layerSettings)
        {
            extNames.Add("VK_EXT_layer_settings");
        }

        if (_options.InstanceExtensions != null)
        {
            foreach (var e in _options.InstanceExtensions)
            {
                if (!extNames.Contains(e))
                {
                    extNames.Add(e);
                }
            }
        }

        if (_options.CreateSurface != null && !extNames.Contains("VK_KHR_surface"))
        {
            extNames.Add("VK_KHR_surface");
        }

        if (portability && !extNames.Contains("VK_KHR_portability_enumeration"))
        {
            extNames.Add("VK_KHR_portability_enumeration");
        }

        return extNames;
    }

    /// <summary>
    /// Creates the Vulkan 1.3 instance with <paramref name="extNames"/>, the validation layer when enabled and its settings.
    /// </summary>
    void CreateVkInstance(List<string> extNames, bool layerSettings, bool portability)
    {
        var appName = (byte*)SilkMarshal.StringToPtr("Meitou");
        var layerPtrs = ValidationEnabled ? (byte**)SilkMarshal.StringArrayToPtr(new[] { ValidationLayer }) : null;
        var extPtrs = (byte**)SilkMarshal.StringArrayToPtr(extNames.ToArray());
        var layerName = (byte*)SilkMarshal.StringToPtr(ValidationLayer);
        var syncName = (byte*)SilkMarshal.StringToPtr("validate_sync");
        var gpuName = (byte*)SilkMarshal.StringToPtr("gpuav_enable");

        try
        {
            var app = new ApplicationInfo
            {
                SType = StructureType.ApplicationInfo,
                PApplicationName = appName,
                ApplicationVersion = 1,
                PEngineName = appName,
                EngineVersion = 1,
                ApiVersion = Vk.Version13,
            };

            // MEITOU_VK_VALIDATION=sync / =gpu: the validation layer's settings (VK_EXT_layer_settings), not its environment variables.
            uint on = 1;
            var settings = stackalloc LayerSettingEXT[2];
            uint settingCount = 0;

            if (layerSettings)
            {
                if (_options.SyncValidation)
                {
                    settings[settingCount++] = new LayerSettingEXT
                    {
                        PLayerName = layerName, PSettingName = syncName, Type = LayerSettingTypeEXT.Bool32Ext, ValueCount = 1, PValues = &on,
                    };
                }

                if (_options.GpuValidation)
                {
                    settings[settingCount++] = new LayerSettingEXT
                    {
                        PLayerName = layerName, PSettingName = gpuName, Type = LayerSettingTypeEXT.Bool32Ext, ValueCount = 1, PValues = &on,
                    };
                }
            }

            var layerInfo = new LayerSettingsCreateInfoEXT
            {
                SType = StructureType.LayerSettingsCreateInfoExt, SettingCount = settingCount, PSettings = settings,
            };
            var ci = new InstanceCreateInfo
            {
                SType = StructureType.InstanceCreateInfo,
                Flags = portability ? InstanceCreateFlags.EnumeratePortabilityBitKhr : 0,
                PNext = settingCount > 0 ? &layerInfo : null,
                PApplicationInfo = &app,
                EnabledLayerCount = ValidationEnabled ? 1u : 0,
                PpEnabledLayerNames = layerPtrs,
                EnabledExtensionCount = (uint)extNames.Count,
                PpEnabledExtensionNames = extPtrs,
            };
            VulkanException.Check(Vk.CreateInstance(in ci, null, out var instance), "vkCreateInstance");
            Instance = instance;
            SyncValidationEnabled = settingCount > 0 && _options.SyncValidation;
        }
        finally
        {
            SilkMarshal.Free((nint)layerName);
            SilkMarshal.Free((nint)syncName);
            SilkMarshal.Free((nint)gpuName);
            SilkMarshal.Free((nint)appName);

            if (layerPtrs != null)
            {
                SilkMarshal.Free((nint)layerPtrs);
            }

            SilkMarshal.Free((nint)extPtrs);
        }
    }

    /// <summary>
    /// The validation messenger: warnings and errors reach <see cref="DebugCallback"/>.
    /// </summary>
    void CreateDebugMessenger()
    {
        _selfHandle = GCHandle.Alloc(this);
        _callbackDelegate = DebugCallback;
        var mi = new DebugUtilsMessengerCreateInfoEXT
        {
            SType = StructureType.DebugUtilsMessengerCreateInfoExt,
            MessageSeverity = DebugUtilsMessageSeverityFlagsEXT.WarningBitExt | DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt,
            MessageType = DebugUtilsMessageTypeFlagsEXT.GeneralBitExt | DebugUtilsMessageTypeFlagsEXT.ValidationBitExt
                | DebugUtilsMessageTypeFlagsEXT.PerformanceBitExt,
            PfnUserCallback = new PfnDebugUtilsMessengerCallbackEXT(_callbackDelegate),
            PUserData = (void*)GCHandle.ToIntPtr(_selfHandle),
        };
        VulkanException.Check(_debugUtils!.CreateDebugUtilsMessenger(Instance, in mi, null, out _messenger), "vkCreateDebugUtilsMessengerEXT");
    }

    /// <summary>
    /// The validation message callback: counts errors (also written to stderr) and warnings and keeps the last 50 messages.
    /// </summary>
    static uint DebugCallback(DebugUtilsMessageSeverityFlagsEXT severity, DebugUtilsMessageTypeFlagsEXT type,
        DebugUtilsMessengerCallbackDataEXT* data, void* user)
    {
        var self = (VulkanDevice?)GCHandle.FromIntPtr((IntPtr)user).Target;

        if (self == null)
        {
            return 0;
        }

        string text = Marshal.PtrToStringAnsi((nint)data->PMessage) ?? "";
        bool isError = (severity & DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt) != 0;
        bool perfOnly = type == DebugUtilsMessageTypeFlagsEXT.PerformanceBitExt;

        if (isError)
        {
            Interlocked.Increment(ref self._validationErrors);
            Console.Error.WriteLine("[vulkan error] " + text);
        }
        else if (!perfOnly)
        {
            Interlocked.Increment(ref self._validationWarnings);
        }

        lock (self._logGate)
        {
            self._validationLog.Add((isError ? "ERROR " : perfOnly ? "PERF " : "WARN ") + text);

            if (self._validationLog.Count > 50)
            {
                self._validationLog.RemoveAt(0);
            }
        }

        return 0;
    }

    /// <summary>
    /// A NUL-terminated ANSI string from a Vulkan name array.
    /// </summary>
    static string Name(byte* p) => Marshal.PtrToStringAnsi((nint)p) ?? "";

    /// <summary>
    /// Picks the best-scoring physical device (<see cref="ScoreDevice"/>; throws when none is usable) and creates the logical device on it.
    /// </summary>
    void PickAndCreateDevice()
    {
        uint n = 0;
        Vk.EnumeratePhysicalDevices(Instance, &n, null);

        if (n == 0)
        {
            throw new VulkanException("No Vulkan physical devices");
        }

        var devices = new PhysicalDevice[n];

        fixed (PhysicalDevice* pd = devices)
        {
            Vk.EnumeratePhysicalDevices(Instance, &n, pd);
        }

        PhysicalDevice best = default;
        long bestScore = -1;

        foreach (var d in devices)
        {
            long score = ScoreDevice(d);

            if (score > bestScore)
            {
                bestScore = score;
                best = d;
            }
        }

        if (bestScore < 0)
        {
            throw new VulkanException("No Vulkan 1.3 device with dynamic rendering, synchronization2 and timeline semaphores");
        }

        PhysicalDevice = best;
        Vk.GetPhysicalDeviceProperties(best, out var props);
        Properties = props;
        DeviceName = Name(props.DeviceName);
        DriverName = ReadDriverName(best);
        CreateLogicalDevice();
    }

    /// <summary>
    /// The driver's name and version from Vulkan 1.2's driver properties (<see cref="DriverName"/>).
    /// </summary>
    string ReadDriverName(PhysicalDevice d)
    {
        var driver = new PhysicalDeviceDriverProperties { SType = StructureType.PhysicalDeviceDriverProperties };
        var properties = new PhysicalDeviceProperties2 { SType = StructureType.PhysicalDeviceProperties2, PNext = &driver };
        Vk.GetPhysicalDeviceProperties2(d, &properties);

        return $"{Name(driver.DriverName)} {Name(driver.DriverInfo)}".Trim();
    }

    /// <summary>
    /// The extensions a physical device offers.
    /// </summary>
    HashSet<string> DeviceExtensions(PhysicalDevice d)
    {
        uint n = 0;
        Vk.EnumerateDeviceExtensionProperties(d, (byte*)null, &n, null);
        var exts = new ExtensionProperties[n];
        var set = new HashSet<string>();

        fixed (ExtensionProperties* p = exts)
        {
            Vk.EnumerateDeviceExtensionProperties(d, (byte*)null, &n, p);

            for (int i = 0; i < n; i++)
            {
                set.Add(Name(p[i].ExtensionName));
            }
        }

        return set;
    }

    /// <summary>
    /// Negative = unusable.
    /// </summary>
    long ScoreDevice(PhysicalDevice d)
    {
        Vk.GetPhysicalDeviceProperties(d, out var props);

        if (props.ApiVersion < Vk.Version13)
        {
            return -1;
        }

        var v13 = new PhysicalDeviceVulkan13Features { SType = StructureType.PhysicalDeviceVulkan13Features };
        var v12 = new PhysicalDeviceVulkan12Features { SType = StructureType.PhysicalDeviceVulkan12Features, PNext = &v13 };
        var f2 = new PhysicalDeviceFeatures2 { SType = StructureType.PhysicalDeviceFeatures2, PNext = &v12 };
        Vk.GetPhysicalDeviceFeatures2(d, &f2);

        if (!v13.DynamicRendering || !v13.Synchronization2 || !v12.TimelineSemaphore)
        {
            return -1;
        }

        if (FindGraphicsFamily(d) < 0)
        {
            return -1;
        }

        if (Surface.Handle != 0 && !DeviceExtensions(d).Contains("VK_KHR_swapchain"))
        {
            return -1;
        }

        long score = 1;

        if (props.DeviceType == PhysicalDeviceType.DiscreteGpu)
        {
            score += _options.PreferDiscrete ? 1_000_000 : 1_000;
        }
        else if (props.DeviceType == PhysicalDeviceType.IntegratedGpu)
        {
            score += _options.PreferDiscrete ? 1_000 : 1_000_000;
        }

        Vk.GetPhysicalDeviceMemoryProperties(d, out var mp);

        for (int i = 0; i < mp.MemoryHeapCount; i++)
        {
            if ((mp.MemoryHeaps[i].Flags & MemoryHeapFlags.DeviceLocalBit) != 0)
            {
                score += (long)(mp.MemoryHeaps[i].Size >> 20);
            }
        }

        return score;
    }

    /// <summary>
    /// The queue families of a physical device.
    /// </summary>
    QueueFamilyProperties[] QueueFamilies(PhysicalDevice d)
    {
        uint n = 0;
        Vk.GetPhysicalDeviceQueueFamilyProperties(d, &n, null);
        var fams = new QueueFamilyProperties[n];

        fixed (QueueFamilyProperties* p = fams)
        {
            Vk.GetPhysicalDeviceQueueFamilyProperties(d, &n, p);
        }

        return fams;
    }

    /// <summary>
    /// The first queue family with graphics and compute that can present to the surface (any when headless); -1 when there is none.
    /// </summary>
    int FindGraphicsFamily(PhysicalDevice d)
    {
        var fams = QueueFamilies(d);

        for (int i = 0; i < fams.Length; i++)
        {
            const QueueFlags need = QueueFlags.GraphicsBit | QueueFlags.ComputeBit;

            if ((fams[i].QueueFlags & need) != need)
            {
                continue;
            }

            if (Surface.Handle != 0)
            {
                _khrSurface!.GetPhysicalDeviceSurfaceSupport(d, (uint)i, Surface, out var ok);

                if (!ok)
                {
                    continue;
                }
            }

            return i;
        }

        return -1;
    }

    /// <summary>
    /// Whether <see cref="VulkanDeviceOptions.Features12"/> asks for a Vulkan 1.2 feature by name.
    /// </summary>
    bool Wants12(string feature) => _options.Features12?.Contains(feature) == true;

    /// <summary>
    /// The logical device on <see cref="PhysicalDevice"/>: what it has is queried in one feature chain, only what the renderer uses is enabled, then
    /// its queues and the limits read once it exists. The feature structs stay locals of this method: the chain points at them.
    /// </summary>
    void CreateLogicalDevice()
    {
        var pd = PhysicalDevice;
        var have = DeviceExtensions(pd);

        // Query everything that might exist, in one chain (only structs of extensions the device has).
        var v13 = new PhysicalDeviceVulkan13Features { SType = StructureType.PhysicalDeviceVulkan13Features };
        var v12 = new PhysicalDeviceVulkan12Features { SType = StructureType.PhysicalDeviceVulkan12Features };
        var v11 = new PhysicalDeviceVulkan11Features { SType = StructureType.PhysicalDeviceVulkan11Features };
        var eds2 = new PhysicalDeviceExtendedDynamicState2FeaturesEXT { SType = StructureType.PhysicalDeviceExtendedDynamicState2FeaturesExt };
        var eds3 = new PhysicalDeviceExtendedDynamicState3FeaturesEXT { SType = StructureType.PhysicalDeviceExtendedDynamicState3FeaturesExt };
        var vid = new PhysicalDeviceVertexInputDynamicStateFeaturesEXT { SType = StructureType.PhysicalDeviceVertexInputDynamicStateFeaturesExt };
        var bary = new PhysicalDeviceFragmentShaderBarycentricFeaturesKHR { SType = StructureType.PhysicalDeviceFragmentShaderBarycentricFeaturesKhr };
        var accel = new PhysicalDeviceAccelerationStructureFeaturesKHR { SType = StructureType.PhysicalDeviceAccelerationStructureFeaturesKhr };
        var rayQuery = new PhysicalDeviceRayQueryFeaturesKHR { SType = StructureType.PhysicalDeviceRayQueryFeaturesKhr };
        var vrs = new PhysicalDeviceFragmentShadingRateFeaturesKHR { SType = StructureType.PhysicalDeviceFragmentShadingRateFeaturesKhr };
        var presentId = new PhysicalDevicePresentIdFeaturesKHR { SType = StructureType.PhysicalDevicePresentIDFeaturesKhr };
        var fault = new PhysicalDeviceFaultFeaturesEXT { SType = StructureType.PhysicalDeviceFaultFeaturesExt };
        var binding = new PhysicalDeviceAddressBindingReportFeaturesEXT { SType = StructureType.PhysicalDeviceAddressBindingReportFeaturesExt };

        bool extEds2 = have.Contains("VK_EXT_extended_dynamic_state2");
        bool extEds3 = have.Contains("VK_EXT_extended_dynamic_state3");
        bool extVid = have.Contains("VK_EXT_vertex_input_dynamic_state");
        bool extPush = have.Contains("VK_KHR_push_descriptor");
        bool extBary = _options.TriangleMeasurements && have.Contains("VK_KHR_fragment_shader_barycentric");
        bool extRay = _options.RayTracing && have.Contains("VK_KHR_acceleration_structure") && have.Contains("VK_KHR_ray_query")
            && have.Contains("VK_KHR_deferred_host_operations");
        bool extVrs = have.Contains("VK_KHR_fragment_shading_rate");

        // VK_NV_low_latency2 (Reflex, which DLSS frame generation asks for) requires present ids, which Streamline's list leaves out (validation,
        // 2026-11-08).
        bool extPresentId = _options.CreateSurface != null && _options.DeviceExtensions?.Contains("VK_NV_low_latency2") == true
            && have.Contains("VK_KHR_present_id");

        // A lost device's own account of its fault (issue #319); the address reports go to a debug messenger and only when tracking.
        bool extFault = have.Contains("VK_EXT_device_fault");
        bool extBinding = DeviceFault.Tracking && _debugUtils is not null && have.Contains("VK_EXT_device_address_binding_report");

        void* chain = null;

        void Link<T>(ref T s, bool use) where T : unmanaged
        {
            if (!use) return;

            // Every feature struct starts with sType, pNext; write pNext in place.
            fixed (T* p = &s)
            {
                ((BaseOutStructure*)p)->PNext = (BaseOutStructure*)chain;
                chain = p;
            }
        }

        // The structs are locals of this method and never move (no GC relocation of stack locals).
        Link(ref v13, true);
        Link(ref v12, true);
        Link(ref v11, true);
        Link(ref eds2, extEds2);
        Link(ref eds3, extEds3);
        Link(ref vid, extVid);
        Link(ref bary, extBary);
        Link(ref accel, extRay);
        Link(ref rayQuery, extRay);
        Link(ref vrs, extVrs);
        Link(ref presentId, extPresentId);
        Link(ref fault, extFault);
        Link(ref binding, extBinding);

        var f2 = new PhysicalDeviceFeatures2 { SType = StructureType.PhysicalDeviceFeatures2, PNext = chain };
        Vk.GetPhysicalDeviceFeatures2(pd, &f2);
        var core = f2.Features;

        ReadFeatures(in v13, in v12, in v11, in eds2, in eds3, in vid, in bary, in accel, in rayQuery, in core, extEds2, extEds3, extVid, extPush,
            extBary, extRay);
        extVrs = extVrs && vrs.AttachmentFragmentShadingRate && ShadingRateAttachmentFormatOk(pd, Format.R8Uint);

        // Turn off everything we did not ask for (the queried structs become the create structs).
        EnableVersionFeatures(ref v13, ref v12, ref v11);
        EnableExtensionFeatures(ref eds2, ref eds3, ref vid, ref bary, ref accel, ref rayQuery, ref vrs, extVrs);
        EnableFaultFeatures(ref fault, ref binding, extFault, extBinding);
        f2.Features = EnabledCoreFeatures(in core);
        var names = DeviceExtensionNames(have, extEds2, extEds3, extVid, extPush, extBary, extVrs, extPresentId);
        if (HasDeviceFault) names.Add("VK_EXT_device_fault");
        if (HasAddressBindingReport) names.Add("VK_EXT_device_address_binding_report");
        var fams = PickQueueFamilies(pd);
        f2.PNext = chain;
        uint graphicsQueues = CreateDevice(pd, &f2, names, fams);
        GetQueues(graphicsQueues);
        QueryDeviceProperties(pd, extVrs);
        if (ValidationEnabled) NameDeviceObjects();
    }

    /// <summary>
    /// Creates <see cref="Device"/> with the enabled features <paramref name="f2"/> (its chain still on the caller's stack), the extensions
    /// <paramref name="names"/>, the graphics queues and the dedicated transfer queue; returns the graphics queue count.
    /// </summary>
    uint CreateDevice(PhysicalDevice pd, PhysicalDeviceFeatures2* f2, List<string> names, QueueFamilyProperties[] fams)
    {
        float prio = 1f;

        // 1 + the extra queues the family offers (priorities all 1).
        uint graphicsQueues = 1 + (uint)Math.Min(Math.Max(_options.ExtraGraphicsQueues, 0), (int)fams[GraphicsFamily].QueueCount - 1);
        var prios = stackalloc float[(int)graphicsQueues];
        for (int i = 0; i < graphicsQueues; i++) prios[i] = 1f;

        var qcis = stackalloc DeviceQueueCreateInfo[2];
        uint qn = 0;
        qcis[qn++] = new DeviceQueueCreateInfo
        {
            SType = StructureType.DeviceQueueCreateInfo, QueueFamilyIndex = GraphicsFamily, QueueCount = graphicsQueues, PQueuePriorities = prios,
        };

        if (HasDedicatedTransfer)
        {
            qcis[qn++] = new DeviceQueueCreateInfo
            {
                SType = StructureType.DeviceQueueCreateInfo, QueueFamilyIndex = TransferFamily, QueueCount = 1, PQueuePriorities = &prio,
            };
        }

        var extPtrs = (byte**)SilkMarshal.StringArrayToPtr(names.ToArray());

        try
        {
            var dci = new DeviceCreateInfo
            {
                SType = StructureType.DeviceCreateInfo,
                PNext = f2,
                QueueCreateInfoCount = qn,
                PQueueCreateInfos = qcis,
                EnabledExtensionCount = (uint)names.Count,
                PpEnabledExtensionNames = extPtrs,
            };
            VulkanException.Check(Vk.CreateDevice(pd, in dci, null, out var device), "vkCreateDevice");
            Device = device;
        }
        finally
        {
            SilkMarshal.Free((nint)extPtrs);
        }

        return graphicsQueues;
    }

    /// <summary>
    /// What the device has, from the queried features and the extensions it lists (true = enabled on the device).
    /// </summary>
    void ReadFeatures(in PhysicalDeviceVulkan13Features v13, in PhysicalDeviceVulkan12Features v12, in PhysicalDeviceVulkan11Features v11,
        in PhysicalDeviceExtendedDynamicState2FeaturesEXT eds2, in PhysicalDeviceExtendedDynamicState3FeaturesEXT eds3,
        in PhysicalDeviceVertexInputDynamicStateFeaturesEXT vid, in PhysicalDeviceFragmentShaderBarycentricFeaturesKHR bary,
        in PhysicalDeviceAccelerationStructureFeaturesKHR accel, in PhysicalDeviceRayQueryFeaturesKHR rayQuery, in PhysicalDeviceFeatures core,
        bool extEds2, bool extEds3, bool extVid, bool extPush, bool extBary, bool extRay)
    {
        HasDynamicRendering = v13.DynamicRendering;
        HasSynchronization2 = v13.Synchronization2;
        HasTimelineSemaphore = v12.TimelineSemaphore;
        HasExtendedDynamicState = true;
        HasExtendedDynamicState2 = extEds2 && eds2.ExtendedDynamicState2;
        HasExtendedDynamicState2LogicOp = HasExtendedDynamicState2 && eds2.ExtendedDynamicState2LogicOp;
        HasExtendedDynamicState2PatchControlPoints = HasExtendedDynamicState2 && eds2.ExtendedDynamicState2PatchControlPoints;
        FillModeNonSolid = core.FillModeNonSolid;
        DepthClamp = core.DepthClamp;
        SamplerAnisotropy = core.SamplerAnisotropy;
        HasTessellation = core.TessellationShader;
        TextureCompressionBc = core.TextureCompressionBC;
        IndependentBlend = core.IndependentBlend;
        ImageCubeArray = core.ImageCubeArray;
        HasPushDescriptor = extPush;
        HasPipelineStatistics = _options.TriangleMeasurements && core.PipelineStatisticsQuery;
        HasFragmentBarycentric = extBary && bary.FragmentShaderBarycentric;
        HasRayQuery = extRay && accel.AccelerationStructure && rayQuery.RayQuery && v12.BufferDeviceAddress;
        HasBufferDeviceAddress = (_options.TriangleMeasurements || HasRayQuery) && v12.BufferDeviceAddress;
        Eds3ColorBlendEnable = extEds3 && eds3.ExtendedDynamicState3ColorBlendEnable;
        Eds3ColorWriteMask = extEds3 && eds3.ExtendedDynamicState3ColorWriteMask;
        Eds3AlphaToCoverageEnable = extEds3 && eds3.ExtendedDynamicState3AlphaToCoverageEnable;
        Eds3PolygonMode = extEds3 && eds3.ExtendedDynamicState3PolygonMode;
        Eds3DepthClampEnable = extEds3 && eds3.ExtendedDynamicState3DepthClampEnable;
        HasExtendedDynamicState3 = Eds3ColorBlendEnable && Eds3ColorWriteMask && Eds3AlphaToCoverageEnable && Eds3PolygonMode && Eds3DepthClampEnable;
        HasVertexInputDynamicState = extVid && vid.VertexInputDynamicState;
        HasSwapchain = Surface.Handle != 0;
        HasBindless = v12.DescriptorIndexing && v12.RuntimeDescriptorArray && v12.DescriptorBindingPartiallyBound
            && v12.DescriptorBindingVariableDescriptorCount && v12.DescriptorBindingSampledImageUpdateAfterBind
            && v12.DescriptorBindingUpdateUnusedWhilePending && v12.ShaderSampledImageArrayNonUniformIndexing;
        MultiDrawIndirect = core.MultiDrawIndirect;
        DrawIndirectFirstInstance = core.DrawIndirectFirstInstance;
        DrawIndirectCount = v12.DrawIndirectCount;
        ShaderDrawParameters = v11.ShaderDrawParameters;
    }

    /// <summary>
    /// Turns off every Vulkan 1.1 to 1.3 feature not asked for: the queried structs become the create structs, in place (their chain stays).
    /// </summary>
    void EnableVersionFeatures(ref PhysicalDeviceVulkan13Features v13, ref PhysicalDeviceVulkan12Features v12, ref PhysicalDeviceVulkan11Features v11)
    {
        v13 = new PhysicalDeviceVulkan13Features
        {
            SType = StructureType.PhysicalDeviceVulkan13Features,
            PNext = v13.PNext,
            DynamicRendering = true,
            Synchronization2 = true,
            SubgroupSizeControl = v13.SubgroupSizeControl,
            ComputeFullSubgroups = v13.ComputeFullSubgroups,
            PrivateData = v13.PrivateData,   // NVIDIA's NGX (DLSS) creates private data slots
        };
        v12 = new PhysicalDeviceVulkan12Features
        {
            SType = StructureType.PhysicalDeviceVulkan12Features,
            PNext = v12.PNext,
            TimelineSemaphore = true,
            // For the GL translation: host query reset (GL queries) and tightly laid out uniform blocks (glslang's loose uniforms).
            HostQueryReset = v12.HostQueryReset,
            ScalarBlockLayout = v12.ScalarBlockLayout,
            UniformBufferStandardLayout = v12.UniformBufferStandardLayout,
            // What the vendor upscalers' compute shaders may use: AMD's FSR picks its FP16 shaders from what the GPU supports, not
            // from what is enabled (docs/engine.md "Upscaling"), so everything it can probe is turned on where supported.
            ShaderFloat16 = v12.ShaderFloat16,
            DescriptorIndexing = HasBindless || (v12.DescriptorIndexing && Wants12("descriptorIndexing")),
            RuntimeDescriptorArray = HasBindless,
            DescriptorBindingPartiallyBound = HasBindless,
            DescriptorBindingVariableDescriptorCount = HasBindless,
            DescriptorBindingSampledImageUpdateAfterBind = HasBindless,
            DescriptorBindingUpdateUnusedWhilePending = HasBindless,
            ShaderSampledImageArrayNonUniformIndexing = HasBindless,
            DrawIndirectCount = DrawIndirectCount,
            BufferDeviceAddress = v12.BufferDeviceAddress && (Wants12("bufferDeviceAddress") || HasBufferDeviceAddress),
        };
        v11 = new PhysicalDeviceVulkan11Features
        {
            SType = StructureType.PhysicalDeviceVulkan11Features,
            PNext = v11.PNext,
            StorageBuffer16BitAccess = v11.StorageBuffer16BitAccess,
            UniformAndStorageBuffer16BitAccess = v11.UniformAndStorageBuffer16BitAccess,
            ShaderDrawParameters = ShaderDrawParameters,
        };
    }

    /// <summary>
    /// The extension feature structs as create structs, each with only what the device gets enabled.
    /// </summary>
    void EnableExtensionFeatures(ref PhysicalDeviceExtendedDynamicState2FeaturesEXT eds2, ref PhysicalDeviceExtendedDynamicState3FeaturesEXT eds3,
        ref PhysicalDeviceVertexInputDynamicStateFeaturesEXT vid, ref PhysicalDeviceFragmentShaderBarycentricFeaturesKHR bary,
        ref PhysicalDeviceAccelerationStructureFeaturesKHR accel, ref PhysicalDeviceRayQueryFeaturesKHR rayQuery,
        ref PhysicalDeviceFragmentShadingRateFeaturesKHR vrs, bool extVrs)
    {
        // The Link() calls stored pointers to the old locations, which are the same variables: still valid.
        eds2 = new PhysicalDeviceExtendedDynamicState2FeaturesEXT
        {
            SType = StructureType.PhysicalDeviceExtendedDynamicState2FeaturesExt,
            PNext = eds2.PNext,
            ExtendedDynamicState2 = HasExtendedDynamicState2,
            ExtendedDynamicState2LogicOp = HasExtendedDynamicState2LogicOp,
            ExtendedDynamicState2PatchControlPoints = HasExtendedDynamicState2PatchControlPoints,
        };
        eds3 = new PhysicalDeviceExtendedDynamicState3FeaturesEXT
        {
            SType = StructureType.PhysicalDeviceExtendedDynamicState3FeaturesExt,
            PNext = eds3.PNext,
            ExtendedDynamicState3ColorBlendEnable = Eds3ColorBlendEnable,
            ExtendedDynamicState3ColorWriteMask = Eds3ColorWriteMask,
            ExtendedDynamicState3AlphaToCoverageEnable = Eds3AlphaToCoverageEnable,
            ExtendedDynamicState3PolygonMode = Eds3PolygonMode,
            ExtendedDynamicState3DepthClampEnable = Eds3DepthClampEnable,
        };
        vid = new PhysicalDeviceVertexInputDynamicStateFeaturesEXT
        {
            SType = StructureType.PhysicalDeviceVertexInputDynamicStateFeaturesExt,
            PNext = vid.PNext,
            VertexInputDynamicState = HasVertexInputDynamicState,
        };
        bary = new PhysicalDeviceFragmentShaderBarycentricFeaturesKHR
        {
            SType = StructureType.PhysicalDeviceFragmentShaderBarycentricFeaturesKhr,
            PNext = bary.PNext,
            FragmentShaderBarycentric = HasFragmentBarycentric,
        };
        accel = new PhysicalDeviceAccelerationStructureFeaturesKHR
        {
            SType = StructureType.PhysicalDeviceAccelerationStructureFeaturesKhr,
            PNext = accel.PNext,
            AccelerationStructure = HasRayQuery,
        };
        rayQuery = new PhysicalDeviceRayQueryFeaturesKHR
        {
            SType = StructureType.PhysicalDeviceRayQueryFeaturesKhr,
            PNext = rayQuery.PNext,
            RayQuery = HasRayQuery,
        };
        vrs = new PhysicalDeviceFragmentShadingRateFeaturesKHR
        {
            SType = StructureType.PhysicalDeviceFragmentShadingRateFeaturesKhr,
            PNext = vrs.PNext,
            AttachmentFragmentShadingRate = extVrs,
        };
    }

    /// <summary>
    /// The fault feature structs as create structs: the fault information without the vendor's binary dump, and the address binding reports, each
    /// where the device has it (<see cref="HasDeviceFault"/>, <see cref="HasAddressBindingReport"/>).
    /// </summary>
    void EnableFaultFeatures(ref PhysicalDeviceFaultFeaturesEXT fault, ref PhysicalDeviceAddressBindingReportFeaturesEXT binding, bool extFault,
        bool extBinding)
    {
        HasDeviceFault = extFault && fault.DeviceFault;
        HasAddressBindingReport = extBinding && binding.ReportAddressBinding;
        fault = new PhysicalDeviceFaultFeaturesEXT
        {
            SType = StructureType.PhysicalDeviceFaultFeaturesExt,
            PNext = fault.PNext,
            DeviceFault = HasDeviceFault,
        };
        binding = new PhysicalDeviceAddressBindingReportFeaturesEXT
        {
            SType = StructureType.PhysicalDeviceAddressBindingReportFeaturesExt,
            PNext = binding.PNext,
            ReportAddressBinding = HasAddressBindingReport,
        };
    }

    /// <summary>
    /// The core features to enable, from the queried <paramref name="core"/>.
    /// </summary>
    PhysicalDeviceFeatures EnabledCoreFeatures(in PhysicalDeviceFeatures core) => new()
    {
        PipelineStatisticsQuery = HasPipelineStatistics,
        FillModeNonSolid = FillModeNonSolid,
        DepthClamp = DepthClamp,
        SamplerAnisotropy = SamplerAnisotropy,
        TessellationShader = HasTessellation,
        TextureCompressionBC = TextureCompressionBc,
        IndependentBlend = IndependentBlend,
        ImageCubeArray = ImageCubeArray,
        ShaderStorageImageReadWithoutFormat = core.ShaderStorageImageReadWithoutFormat,
        ShaderStorageImageWriteWithoutFormat = core.ShaderStorageImageWriteWithoutFormat,
        ShaderStorageImageExtendedFormats = HasRayQuery && core.ShaderStorageImageExtendedFormats,   // the probe GI's rg16f distance atlas
        ShaderInt16 = core.ShaderInt16,
        MultiDrawIndirect = MultiDrawIndirect,
        DrawIndirectFirstInstance = DrawIndirectFirstInstance,
    };

    /// <summary>
    /// The device extensions to enable (and whether the memory budget extension is among them).
    /// </summary>
    List<string> DeviceExtensionNames(HashSet<string> have, bool extEds2, bool extEds3, bool extVid, bool extPush, bool extBary, bool extVrs,
        bool extPresentId)
    {
        var names = new List<string>();
        if (HasSwapchain) names.Add("VK_KHR_swapchain");
        if (extEds2) names.Add("VK_EXT_extended_dynamic_state2");
        if (extEds3) names.Add("VK_EXT_extended_dynamic_state3");
        if (extVid) names.Add("VK_EXT_vertex_input_dynamic_state");
        if (extPush) names.Add("VK_KHR_push_descriptor");
        if (extBary) names.Add("VK_KHR_fragment_shader_barycentric");
        if (HasRayQuery)
            names.AddRange(["VK_KHR_acceleration_structure", "VK_KHR_ray_query", "VK_KHR_deferred_host_operations"]);
        if (extVrs) names.Add("VK_KHR_fragment_shading_rate");
        if (extPresentId) names.Add("VK_KHR_present_id");

        // Only read by VideoMemory (the viewer's statistics): the process's usage and budget per heap.
        HasMemoryBudget = have.Contains("VK_EXT_memory_budget");
        if (HasMemoryBudget) names.Add("VK_EXT_memory_budget");

        // Core since 1.1, but AMD's FidelityFX DLL calls the KHR-named entry points when the GPU lists the extensions, and those are
        // null unless the extensions are enabled (DECISIONS 16).
        foreach (var e in (string[])["VK_KHR_get_memory_requirements2", "VK_KHR_dedicated_allocation"])
            if (have.Contains(e)) names.Add(e);

        foreach (var e in _options.DeviceExtensions ?? [])
        {
            // Buffer device address is core in 1.2 (enabled as a feature): its extensions may not be enabled with it.
            if (have.Contains(e) && !names.Contains(e) && e is not ("VK_EXT_buffer_device_address" or "VK_KHR_buffer_device_address"))
                names.Add(e);
        }

        return names;
    }

    /// <summary>
    /// The graphics family and a transfer-only family when the device has one (else transfers share the graphics family); returns the families.
    /// </summary>
    QueueFamilyProperties[] PickQueueFamilies(PhysicalDevice pd)
    {
        var fams = QueueFamilies(pd);
        GraphicsFamily = (uint)FindGraphicsFamily(pd);
        TransferFamily = GraphicsFamily;

        for (int i = 0; i < fams.Length; i++)
        {
            var fl = fams[i].QueueFlags;

            if ((fl & QueueFlags.TransferBit) != 0 && (fl & (QueueFlags.GraphicsBit | QueueFlags.ComputeBit)) == 0)
            {
                TransferFamily = (uint)i;
                HasDedicatedTransfer = true;
                break;
            }
        }

        return fams;
    }

    /// <summary>
    /// The device's queues: the graphics queue, the extra graphics queues and the transfer queue (the graphics queue without a family of its own).
    /// </summary>
    void GetQueues(uint graphicsQueues)
    {
        Vk.GetDeviceQueue(Device, GraphicsFamily, 0, out var gq);
        GraphicsQueue = gq;

        if (graphicsQueues > 1)
        {
            ExtraQueues = new Queue[graphicsQueues - 1];

            for (uint i = 1; i < graphicsQueues; i++)
            {
                Vk.GetDeviceQueue(Device, GraphicsFamily, i, out var eq);
                ExtraQueues[i - 1] = eq;
            }
        }

        if (HasDedicatedTransfer)
        {
            Vk.GetDeviceQueue(Device, TransferFamily, 0, out var tq);
            TransferQueue = tq;
        }
        else
        {
            TransferQueue = gq;
        }
    }

    /// <summary>
    /// The limits read once the device exists: shading rates, push descriptors and acceleration structures.
    /// </summary>
    void QueryDeviceProperties(PhysicalDevice pd, bool extVrs)
    {
        // Unusable (no attachment feature, no R8_UINT attachment, no listed rate): the extension is not enabled and the feature stays off.
        if (extVrs) QueryShadingRate(pd);

        if (HasPushDescriptor)
        {
            var pushProps = new PhysicalDevicePushDescriptorPropertiesKHR { SType = StructureType.PhysicalDevicePushDescriptorPropertiesKhr };
            var p2 = new PhysicalDeviceProperties2 { SType = StructureType.PhysicalDeviceProperties2, PNext = &pushProps };
            Vk.GetPhysicalDeviceProperties2(pd, &p2);
            MaxPushDescriptors = pushProps.MaxPushDescriptors;

            // MoltenVK (macOS): push descriptor sets cannot go into Metal argument buffers, so they bind through MSL
            // attributes, where the per-function sampler cap is 16 — a set with more (water: ocean + shadows + sky) fails
            // to compile. Lower the budget so the existing regular descriptor set path (argument buffers) is used instead.
            if (OperatingSystem.IsMacOS() && MaxPushDescriptors > 16)
            {
                MaxPushDescriptors = 16;
            }
        }

        if (HasRayQuery)
        {
            if (!Vk.TryGetDeviceExtension(Instance, Device, out KhrAccelerationStructure accelerationStructures))
                throw new VulkanException("VK_KHR_acceleration_structure was enabled but its entry points did not load");

            AccelerationStructures = accelerationStructures;
            var asProps = new PhysicalDeviceAccelerationStructurePropertiesKHR
            {
                SType = StructureType.PhysicalDeviceAccelerationStructurePropertiesKhr,
            };
            var p2 = new PhysicalDeviceProperties2 { SType = StructureType.PhysicalDeviceProperties2, PNext = &asProps };
            Vk.GetPhysicalDeviceProperties2(pd, &p2);
            ScratchAlignment = Math.Max(asProps.MinAccelerationStructureScratchOffsetAlignment, 1);
        }
    }

    /// <summary>
    /// Names the device and its queues for validation messages and capture tools.
    /// </summary>
    void NameDeviceObjects()
    {
        SetName(ObjectType.Device, (ulong)Device.Handle, "Meitou device");
        SetName(ObjectType.Queue, (ulong)GraphicsQueue.Handle, "graphics queue");

        if (HasDedicatedTransfer)
        {
            SetName(ObjectType.Queue, (ulong)TransferQueue.Handle, "transfer queue");
        }
    }

    #endregion

    #region Fragment shading rate

    /// <summary>
    /// Whether a format supports being a fragment shading rate attachment and a colour attachment with optimal tiling.
    /// </summary>
    bool ShadingRateAttachmentFormatOk(PhysicalDevice pd, Format format)
    {
        Vk.GetPhysicalDeviceFormatProperties(pd, format, out var props);
        return (props.OptimalTilingFeatures & FormatFeatureFlags.FragmentShadingRateAttachmentBitKhr) != 0 &&
            (props.OptimalTilingFeatures & FormatFeatureFlags.ColorAttachmentBit) != 0;
    }

    /// <summary>
    /// Reads the attachment texel size limits and the listed rates once the device exists; the feature stays off when no usable texel size or square
    /// rate comes out.
    /// </summary>
    void QueryShadingRate(PhysicalDevice pd)
    {
        var rateProps = new PhysicalDeviceFragmentShadingRatePropertiesKHR { SType = StructureType.PhysicalDeviceFragmentShadingRatePropertiesKhr };
        var p2 = new PhysicalDeviceProperties2 { SType = StructureType.PhysicalDeviceProperties2, PNext = &rateProps };
        Vk.GetPhysicalDeviceProperties2(pd, &p2);
        var min = rateProps.MinFragmentShadingRateAttachmentTexelSize;
        var max = rateProps.MaxFragmentShadingRateAttachmentTexelSize;

        // Square texels only: 16 when the range allows it, else the smallest (8 on some GPUs).
        int texel = (int)Math.Max(min.Width, min.Height);
        if (texel < 16 && 16 <= Math.Min(max.Width, max.Height)) texel = 16;
        if (texel > Math.Min(max.Width, max.Height) || texel > 32 || (texel & (texel - 1)) != 0) return;

        ShadingRateForcedByDepthWrite = !rateProps.FragmentShadingRateWithShaderDepthStencilWrites;
        int largest = 1;   // the largest square rate (1, 2 or 4 pixels a side) the device lists for one sample
        var list = (delegate* unmanaged<PhysicalDevice, uint*, PhysicalDeviceFragmentShadingRateKHR*, Result>)Vk.GetInstanceProcAddr(Instance,
            "vkGetPhysicalDeviceFragmentShadingRatesKHR").Handle;

        if (list != null)
        {
            uint n = 0;
            list(pd, &n, null);

            if (n > 0)
            {
                var rates = new PhysicalDeviceFragmentShadingRateKHR[n];
                for (int i = 0; i < n; i++) rates[i].SType = StructureType.PhysicalDeviceFragmentShadingRateKhr;
                fixed (PhysicalDeviceFragmentShadingRateKHR* r = rates) list(pd, &n, r);

                for (int i = 0; i < n; i++)
                {
                    var size = rates[i].FragmentSize;
                    if (size.Width == size.Height && (rates[i].SampleCounts & SampleCountFlags.Count1Bit) != 0 && size.Width <= 4)
                        largest = Math.Max(largest, (int)size.Width);
                }
            }
        }

        if (largest < 2) return;

        (ShadingRateTexel, MaxShadingRate, HasFragmentShadingRate) = (texel, largest, true);
    }

    #endregion

    #region Pipeline cache

    /// <summary>
    /// Creates the pipeline cache from the saved file when it can be read and the device takes it; else an empty one (throws when even that fails).
    /// </summary>
    void CreatePipelineCache()
    {
        _pipelineCachePath = _options.PipelineCachePath;
        byte[]? data = null;

        if (_pipelineCachePath != null)
        {
            try
            {
                if (File.Exists(_pipelineCachePath))
                {
                    data = File.ReadAllBytes(_pipelineCachePath);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        if (data != null && data.Length >= 32 && TryCreateCache(data))
        {
            return;
        }

        TryCreateCache(null);

        if (PipelineCache.Handle == 0)
        {
            throw new VulkanException("vkCreatePipelineCache failed");
        }
    }

    /// <summary>
    /// Creates the pipeline cache from initial data (null for empty); false when the device refuses it.
    /// </summary>
    bool TryCreateCache(byte[]? data)
    {
        fixed (byte* p = data)
        {
            var ci = new PipelineCacheCreateInfo
            {
                SType = StructureType.PipelineCacheCreateInfo,
                InitialDataSize = (nuint)(data?.Length ?? 0),
                PInitialData = p,
            };
            var r = Vk.CreatePipelineCache(Device, in ci, null, out var cache);

            if (r != Result.Success)
            {
                return false;
            }

            PipelineCache = cache;
            return true;
        }
    }

    /// <summary>
    /// Writes the pipeline cache to its file (through a per-process temporary file, then a move); a cache that cannot be saved is skipped.
    /// </summary>
    void SavePipelineCache()
    {
        if (_pipelineCachePath == null || PipelineCache.Handle == 0)
        {
            return;
        }

        try
        {
            nuint size = 0;
            Vk.GetPipelineCacheData(Device, PipelineCache, &size, null);

            if (size == 0)
            {
                return;
            }

            var bytes = new byte[size];

            fixed (byte* p = bytes)
            {
                Vk.GetPipelineCacheData(Device, PipelineCache, &size, p);
            }

            var dir = Path.GetDirectoryName(Path.GetFullPath(_pipelineCachePath));

            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var tmp = $"{_pipelineCachePath}.{Environment.ProcessId}.tmp";   // processes saving at once each write their own, the last move wins
            File.WriteAllBytes(tmp, bytes.AsSpan(0, (int)size).ToArray());
            File.Move(tmp, _pipelineCachePath, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A cache that cannot be saved only costs the next start some compile time.
        }
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Names an object for validation messages and capture tools (no-op without validation).
    /// </summary>
    public void SetName(ObjectType type, ulong handle, string name)
    {
        if (_debugUtils == null || Device.Handle == 0)
        {
            return;
        }

        var p = (byte*)SilkMarshal.StringToPtr(name);

        try
        {
            var info = new DebugUtilsObjectNameInfoEXT
            {
                SType = StructureType.DebugUtilsObjectNameInfoExt,
                ObjectType = type,
                ObjectHandle = handle,
                PObjectName = p,
            };
            _debugUtils.SetDebugUtilsObjectName(Device, in info);
        }
        finally
        {
            SilkMarshal.Free((nint)p);
        }
    }

    /// <summary>
    /// <see cref="VulkanException.Check"/> for the calls that tell of a lost device (submissions, waits, the swapchain's): a lost device writes its
    /// report first (<see cref="DeviceFault.Report"/>).
    /// </summary>
    public void Check(Result r, string what)
    {
        if (r == Result.ErrorDeviceLost) Faults?.Report(what);
        VulkanException.Check(r, what);
    }

    /// <summary>
    /// Frees the buffer once every frame that could use it has finished.
    /// </summary>
    public void DeferFree(BufferAllocation b) => Frames.DeferDelete(() => Allocator.Free(b));

    /// <summary>
    /// Frees the image once every frame that could use it has finished.
    /// </summary>
    public void DeferFree(ImageAllocation i) => Frames.DeferDelete(() => Allocator.Free(i));

    /// <summary>
    /// Waits through this function instead of the driver's <c>vkDeviceWaitIdle</c> when set (Streamline's proxy with DLSS frame generation, whose
    /// present thread must be idle too).
    /// </summary>
    public delegate* unmanaged<Device, Result> WaitIdleFunction { get; set; }

    /// <summary>
    /// Waits for the device to go idle, under <see cref="QueueLock"/> (through <see cref="WaitIdleFunction"/> when set).
    /// </summary>
    public void WaitIdle()
    {
        lock (QueueLock)
        {
            if (WaitIdleFunction != null) WaitIdleFunction(Device);
            else Vk.DeviceWaitIdle(Device);
        }
    }

    /// <summary>
    /// A one-shot command buffer (own transient pool) for readbacks and startup uploads. Finish with <see cref="EndImmediate"/>.
    /// </summary>
    public CommandBuffer BeginImmediate()
    {
        var pi = new CommandPoolCreateInfo
        {
            SType = StructureType.CommandPoolCreateInfo,
            Flags = CommandPoolCreateFlags.TransientBit,
            QueueFamilyIndex = GraphicsFamily,
        };
        VulkanException.Check(Vk.CreateCommandPool(Device, in pi, null, out var pool), "vkCreateCommandPool");
        var ai = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = pool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = 1,
        };
        VulkanException.Check(Vk.AllocateCommandBuffers(Device, in ai, out var cb), "vkAllocateCommandBuffers");
        var bi = new CommandBufferBeginInfo { SType = StructureType.CommandBufferBeginInfo, Flags = CommandBufferUsageFlags.OneTimeSubmitBit };
        VulkanException.Check(Vk.BeginCommandBuffer(cb, in bi), "vkBeginCommandBuffer");
        _immediatePools[(ulong)cb.Handle] = pool;
        return cb;
    }

    /// <summary>
    /// Ends, submits on the graphics queue and waits for completion.
    /// </summary>
    public void EndImmediate(CommandBuffer cb)
    {
        if (!_immediatePools.TryRemove((ulong)cb.Handle, out var pool))
        {
            throw new InvalidOperationException("Not a command buffer from BeginImmediate");
        }

        try
        {
            VulkanException.Check(Vk.EndCommandBuffer(cb), "vkEndCommandBuffer");
            var fi = new FenceCreateInfo { SType = StructureType.FenceCreateInfo };
            VulkanException.Check(Vk.CreateFence(Device, in fi, null, out var fence), "vkCreateFence");

            try
            {
                var si = new SubmitInfo { SType = StructureType.SubmitInfo, CommandBufferCount = 1, PCommandBuffers = &cb };

                lock (QueueLock)
                {
                    Check(Vk.QueueSubmit(GraphicsQueue, 1, in si, fence), "vkQueueSubmit");
                }

                Check(Vk.WaitForFences(Device, 1, in fence, true, ulong.MaxValue), "vkWaitForFences");
            }
            finally
            {
                Vk.DestroyFence(Device, fence, null);
            }
        }
        finally
        {
            Vk.DestroyCommandPool(Device, pool, null);
        }
    }

    #endregion

    #region Teardown

    /// <summary>
    /// Waits for the device to go idle, then destroys what it made: immediate pools, the frame ring, the pipeline cache (saved first), the allocator,
    /// the device, the surface, the messenger and the instance.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (Device.Handle != 0)
        {
            Vk.DeviceWaitIdle(Device);

            foreach (var pool in _immediatePools.Values)
            {
                Vk.DestroyCommandPool(Device, pool, null);
            }

            _immediatePools.Clear();
            Frames?.Dispose();
            SavePipelineCache();

            if (PipelineCache.Handle != 0)
            {
                Vk.DestroyPipelineCache(Device, PipelineCache, null);
                PipelineCache = default;
            }

            Allocator?.Dispose();
            Vk.DestroyDevice(Device, null);
            Device = default;
        }

        if (Surface.Handle != 0 && _khrSurface != null)
        {
            _khrSurface.DestroySurface(Instance, Surface, null);
            Surface = default;
        }

        if (_messenger.Handle != 0)
        {
            _debugUtils!.DestroyDebugUtilsMessenger(Instance, _messenger, null);
            _messenger = default;
        }

        AddressLog?.Dispose();

        if (Instance.Handle != 0)
        {
            Vk.DestroyInstance(Instance, null);
            Instance = default;
        }

        if (_selfHandle.IsAllocated)
        {
            _selfHandle.Free();
        }

        Vk.Dispose();
    }

    #endregion
}