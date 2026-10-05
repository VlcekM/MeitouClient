using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using Silk.NET.Vulkan.Extensions.KHR;

namespace Meitou.Rendering.Vulkan.Core;

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
    const string ValidationLayer = "VK_LAYER_KHRONOS_validation";

    readonly VulkanDeviceOptions options;
    readonly ConcurrentDictionary<ulong, CommandPool> immediatePools = new();
    readonly object logGate = new();
    readonly List<string> validationLog = new();
    GCHandle selfHandle;
    DebugUtilsMessengerCallbackFunctionEXT? callbackDelegate;
    ExtDebugUtils? debugUtils;
    DebugUtilsMessengerEXT messenger;
    KhrSurface? khrSurface;
    string? pipelineCachePath;
    int validationErrors;
    int validationWarnings;
    bool disposed;

    VulkanDevice(VulkanDeviceOptions options)
    {
        this.options = options;
        Vk = Vk.GetApi();
    }

    // ---- public state --------------------------------------------------------------------------------------

    public Vk Vk { get; }
    public Instance Instance { get; private set; }
    public PhysicalDevice PhysicalDevice { get; private set; }
    public Device Device { get; private set; }
    public PhysicalDeviceProperties Properties { get; private set; }
    public string DeviceName { get; private set; } = "";
    public uint ApiVersion => Properties.ApiVersion;
    /// <summary>The surface from <see cref="VulkanDeviceOptions.CreateSurface"/> (default when headless); destroyed on dispose.</summary>
    public SurfaceKHR Surface { get; private set; }
    public bool HasSurface => Surface.Handle != 0;
    /// <summary>The KHR_surface instance extension functions (null when headless).</summary>
    public KhrSurface? SurfaceExtension => khrSurface;

    public Queue GraphicsQueue { get; private set; }
    public uint GraphicsFamily { get; private set; }
    public Queue TransferQueue { get; private set; }
    public uint TransferFamily { get; private set; }
    public bool HasDedicatedTransfer { get; private set; }
    /// <summary>Held around every queue submission this device makes; take it when submitting yourself.</summary>
    public object QueueLock { get; } = new();

    public GpuAllocator Allocator { get; private set; } = null!;
    public PipelineCache PipelineCache { get; private set; }
    public FrameRing Frames { get; private set; } = null!;
    public int FramesInFlight => options.FramesInFlight;

    public bool ValidationEnabled { get; private set; }
    public int ValidationErrors => Volatile.Read(ref validationErrors);
    public int ValidationWarnings => Volatile.Read(ref validationWarnings);
    /// <summary>The last ~50 validation messages (errors and warnings), oldest first.</summary>
    public string[] ValidationLog
    {
        get
        {
            lock (logGate)
            {
                return validationLog.ToArray();
            }
        }
    }

    // Features and extensions (true = enabled on the device).
    public bool HasDynamicRendering { get; private set; }
    public bool HasSynchronization2 { get; private set; }
    public bool HasTimelineSemaphore { get; private set; }
    /// <summary>Core in Vulkan 1.3: always available.</summary>
    public bool HasExtendedDynamicState { get; private set; }
    public bool HasExtendedDynamicState2 { get; private set; }
    public bool HasExtendedDynamicState2LogicOp { get; private set; }
    public bool HasExtendedDynamicState2PatchControlPoints { get; private set; }
    public bool FillModeNonSolid { get; private set; }
    public bool DepthClamp { get; private set; }
    public bool SamplerAnisotropy { get; private set; }
    public bool TextureCompressionBC { get; private set; }
    public bool IndependentBlend { get; private set; }
    public bool ImageCubeArray { get; private set; }
    public bool HasPushDescriptor { get; private set; }
    public uint MaxPushDescriptors { get; private set; }
    public bool HasDepthClipControl { get; private set; }
    /// <summary>Extension present and all of the five sub-features below supported.</summary>
    public bool HasExtendedDynamicState3 { get; private set; }
    public bool Eds3ColorBlendEnable { get; private set; }
    public bool Eds3ColorWriteMask { get; private set; }
    public bool Eds3AlphaToCoverageEnable { get; private set; }
    public bool Eds3PolygonMode { get; private set; }
    public bool Eds3DepthClampEnable { get; private set; }
    public bool HasVertexInputDynamicState { get; private set; }
    public bool HasSwapchain { get; private set; }

    public PhysicalDeviceLimits Limits => Properties.Limits;

    /// <summary>One line per feature, for logs and test output.</summary>
    public string DescribeFeatures()
    {
        var sb = new StringBuilder();
        var v = Properties.ApiVersion;
        sb.AppendLine($"Device: {DeviceName}, Vulkan {v >> 22}.{(v >> 12) & 0x3FF}.{v & 0xFFF}, type {Properties.DeviceType}, validation {(ValidationEnabled ? "on" : "off")}");
        sb.AppendLine($"Queues: graphics family {GraphicsFamily}, transfer family {TransferFamily}{(HasDedicatedTransfer ? " (dedicated)" : " (shared with graphics)")}");
        sb.AppendLine($"dynamicRendering={HasDynamicRendering} synchronization2={HasSynchronization2} timelineSemaphore={HasTimelineSemaphore} extendedDynamicState={HasExtendedDynamicState}");
        sb.AppendLine($"extendedDynamicState2={HasExtendedDynamicState2} (logicOp={HasExtendedDynamicState2LogicOp}, patchControlPoints={HasExtendedDynamicState2PatchControlPoints})");
        sb.AppendLine($"fillModeNonSolid={FillModeNonSolid} depthClamp={DepthClamp} samplerAnisotropy={SamplerAnisotropy} textureCompressionBC={TextureCompressionBC} independentBlend={IndependentBlend} imageCubeArray={ImageCubeArray}");
        sb.AppendLine($"pushDescriptor={HasPushDescriptor} (max {MaxPushDescriptors}) depthClipControl={HasDepthClipControl} vertexInputDynamicState={HasVertexInputDynamicState} swapchain={HasSwapchain}");
        sb.AppendLine($"extendedDynamicState3={HasExtendedDynamicState3} (colorBlendEnable={Eds3ColorBlendEnable} colorWriteMask={Eds3ColorWriteMask} alphaToCoverage={Eds3AlphaToCoverageEnable} polygonMode={Eds3PolygonMode} depthClamp={Eds3DepthClampEnable})");
        sb.Append($"Limits: minUniformBufferOffsetAlignment={Limits.MinUniformBufferOffsetAlignment} timestampPeriod={Limits.TimestampPeriod}ns bufferImageGranularity={Limits.BufferImageGranularity}");
        return sb.ToString();
    }

    // ---- creation ------------------------------------------------------------------------------------------

    /// <summary>Creates the device. Throws <see cref="VulkanException"/> when no suitable Vulkan 1.3 device exists.</summary>
    public static VulkanDevice Create(VulkanDeviceOptions o)
    {
        if (o.FramesInFlight is < 2 or > 3)
        {
            throw new ArgumentOutOfRangeException(nameof(o), "FramesInFlight must be 2..3");
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

    void Initialize()
    {
        CreateInstance();
        if (options.CreateSurface != null)
        {
            Surface = options.CreateSurface(Instance);
            Vk.TryGetInstanceExtension(Instance, out khrSurface);
        }
        PickAndCreateDevice();
        Allocator = new GpuAllocator(this);
        CreatePipelineCache();
        Frames = new FrameRing(this, options.FramesInFlight);
        options.DeviceCreated?.Invoke(this);
    }

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

    bool InstanceExtensionAvailable(string name)
    {
        uint n = 0;
        Vk.EnumerateInstanceExtensionProperties((byte*)null, &n, null);
        var exts = new ExtensionProperties[n];
        fixed (ExtensionProperties* p = exts)
        {
            Vk.EnumerateInstanceExtensionProperties((byte*)null, &n, p);
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

    void CreateInstance()
    {
        bool wantValidation = options.Validation ?? true;
        bool layer = wantValidation && LayerAvailable();
        if (options.Validation == true && !layer)
        {
            throw new VulkanException($"{ValidationLayer} is not installed");
        }
        bool debugExt = layer && InstanceExtensionAvailable("VK_EXT_debug_utils");
        ValidationEnabled = layer && debugExt;

        var extNames = new List<string>();
        if (ValidationEnabled)
        {
            extNames.Add("VK_EXT_debug_utils");
        }
        if (options.InstanceExtensions != null)
        {
            foreach (var e in options.InstanceExtensions)
            {
                if (!extNames.Contains(e))
                {
                    extNames.Add(e);
                }
            }
        }
        if (options.CreateSurface != null && !extNames.Contains("VK_KHR_surface"))
        {
            extNames.Add("VK_KHR_surface");
        }

        var appName = (byte*)SilkMarshal.StringToPtr("Meitou");
        var layerPtrs = ValidationEnabled ? (byte**)SilkMarshal.StringArrayToPtr(new[] { ValidationLayer }) : null;
        var extPtrs = (byte**)SilkMarshal.StringArrayToPtr(extNames.ToArray());
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
            var ci = new InstanceCreateInfo
            {
                SType = StructureType.InstanceCreateInfo,
                PApplicationInfo = &app,
                EnabledLayerCount = ValidationEnabled ? 1u : 0,
                PpEnabledLayerNames = layerPtrs,
                EnabledExtensionCount = (uint)extNames.Count,
                PpEnabledExtensionNames = extPtrs,
            };
            VulkanException.Check(Vk.CreateInstance(in ci, null, out var instance), "vkCreateInstance");
            Instance = instance;
        }
        finally
        {
            SilkMarshal.Free((nint)appName);
            if (layerPtrs != null)
            {
                SilkMarshal.Free((nint)layerPtrs);
            }
            SilkMarshal.Free((nint)extPtrs);
        }

        if (ValidationEnabled)
        {
            Vk.TryGetInstanceExtension(Instance, out debugUtils);
            selfHandle = GCHandle.Alloc(this);
            callbackDelegate = DebugCallback;
            var mi = new DebugUtilsMessengerCreateInfoEXT
            {
                SType = StructureType.DebugUtilsMessengerCreateInfoExt,
                MessageSeverity = DebugUtilsMessageSeverityFlagsEXT.WarningBitExt | DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt,
                MessageType = DebugUtilsMessageTypeFlagsEXT.GeneralBitExt | DebugUtilsMessageTypeFlagsEXT.ValidationBitExt | DebugUtilsMessageTypeFlagsEXT.PerformanceBitExt,
                PfnUserCallback = new PfnDebugUtilsMessengerCallbackEXT(callbackDelegate),
                PUserData = (void*)GCHandle.ToIntPtr(selfHandle),
            };
            VulkanException.Check(debugUtils!.CreateDebugUtilsMessenger(Instance, in mi, null, out messenger), "vkCreateDebugUtilsMessengerEXT");
        }
    }

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
            Interlocked.Increment(ref self.validationErrors);
            Console.Error.WriteLine("[vulkan error] " + text);
        }
        else if (!perfOnly)
        {
            Interlocked.Increment(ref self.validationWarnings);
        }
        lock (self.logGate)
        {
            self.validationLog.Add((isError ? "ERROR " : perfOnly ? "PERF " : "WARN ") + text);
            if (self.validationLog.Count > 50)
            {
                self.validationLog.RemoveAt(0);
            }
        }
        return 0;
    }

    static string Name(byte* p) => Marshal.PtrToStringAnsi((nint)p) ?? "";

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
        CreateLogicalDevice();
    }

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

    /// <summary>Negative = unusable.</summary>
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
            score += options.PreferDiscrete ? 1_000_000 : 1_000;
        }
        else if (props.DeviceType == PhysicalDeviceType.IntegratedGpu)
        {
            score += options.PreferDiscrete ? 1_000 : 1_000_000;
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
                khrSurface!.GetPhysicalDeviceSurfaceSupport(d, (uint)i, Surface, out var ok);
                if (!ok)
                {
                    continue;
                }
            }
            return i;
        }
        return -1;
    }

    bool Wants12(string feature) => options.Features12?.Contains(feature) == true;

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
        var clip = new PhysicalDeviceDepthClipControlFeaturesEXT { SType = StructureType.PhysicalDeviceDepthClipControlFeaturesExt };
        var vid = new PhysicalDeviceVertexInputDynamicStateFeaturesEXT { SType = StructureType.PhysicalDeviceVertexInputDynamicStateFeaturesExt };

        bool extEds2 = have.Contains("VK_EXT_extended_dynamic_state2");
        bool extEds3 = have.Contains("VK_EXT_extended_dynamic_state3");
        bool extClip = have.Contains("VK_EXT_depth_clip_control");
        bool extVid = have.Contains("VK_EXT_vertex_input_dynamic_state");
        bool extPush = have.Contains("VK_KHR_push_descriptor");

        void* chain = null;
        void Link<T>(ref T s, bool use) where T : unmanaged
        {
            if (!use)
            {
                return;
            }
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
        Link(ref clip, extClip);
        Link(ref vid, extVid);

        var f2 = new PhysicalDeviceFeatures2 { SType = StructureType.PhysicalDeviceFeatures2, PNext = chain };
        Vk.GetPhysicalDeviceFeatures2(pd, &f2);
        var core = f2.Features;

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
        TextureCompressionBC = core.TextureCompressionBC;
        IndependentBlend = core.IndependentBlend;
        ImageCubeArray = core.ImageCubeArray;
        HasPushDescriptor = extPush;
        HasDepthClipControl = extClip && clip.DepthClipControl;
        Eds3ColorBlendEnable = extEds3 && eds3.ExtendedDynamicState3ColorBlendEnable;
        Eds3ColorWriteMask = extEds3 && eds3.ExtendedDynamicState3ColorWriteMask;
        Eds3AlphaToCoverageEnable = extEds3 && eds3.ExtendedDynamicState3AlphaToCoverageEnable;
        Eds3PolygonMode = extEds3 && eds3.ExtendedDynamicState3PolygonMode;
        Eds3DepthClampEnable = extEds3 && eds3.ExtendedDynamicState3DepthClampEnable;
        HasExtendedDynamicState3 = Eds3ColorBlendEnable && Eds3ColorWriteMask && Eds3AlphaToCoverageEnable && Eds3PolygonMode && Eds3DepthClampEnable;
        HasVertexInputDynamicState = extVid && vid.VertexInputDynamicState;
        HasSwapchain = Surface.Handle != 0;

        // Turn off everything we did not ask for (the queried struct becomes the create struct).
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
            DescriptorIndexing = v12.DescriptorIndexing && Wants12("descriptorIndexing"),
            BufferDeviceAddress = v12.BufferDeviceAddress && Wants12("bufferDeviceAddress"),
        };
        v11 = new PhysicalDeviceVulkan11Features
        {
            SType = StructureType.PhysicalDeviceVulkan11Features,
            PNext = v11.PNext,
            StorageBuffer16BitAccess = v11.StorageBuffer16BitAccess,
            UniformAndStorageBuffer16BitAccess = v11.UniformAndStorageBuffer16BitAccess,
        };
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
        clip = new PhysicalDeviceDepthClipControlFeaturesEXT
        {
            SType = StructureType.PhysicalDeviceDepthClipControlFeaturesExt,
            PNext = clip.PNext,
            DepthClipControl = HasDepthClipControl,
        };
        vid = new PhysicalDeviceVertexInputDynamicStateFeaturesEXT
        {
            SType = StructureType.PhysicalDeviceVertexInputDynamicStateFeaturesExt,
            PNext = vid.PNext,
            VertexInputDynamicState = HasVertexInputDynamicState,
        };
        f2.Features = new PhysicalDeviceFeatures
        {
            FillModeNonSolid = FillModeNonSolid,
            DepthClamp = DepthClamp,
            SamplerAnisotropy = SamplerAnisotropy,
            TextureCompressionBC = TextureCompressionBC,
            IndependentBlend = IndependentBlend,
            ImageCubeArray = ImageCubeArray,
            ShaderStorageImageReadWithoutFormat = core.ShaderStorageImageReadWithoutFormat,
            ShaderStorageImageWriteWithoutFormat = core.ShaderStorageImageWriteWithoutFormat,
            ShaderInt16 = core.ShaderInt16,
        };

        var names = new List<string>();
        if (HasSwapchain) names.Add("VK_KHR_swapchain");
        if (extEds2) names.Add("VK_EXT_extended_dynamic_state2");
        if (extEds3) names.Add("VK_EXT_extended_dynamic_state3");
        if (extClip) names.Add("VK_EXT_depth_clip_control");
        if (extVid) names.Add("VK_EXT_vertex_input_dynamic_state");
        if (extPush) names.Add("VK_KHR_push_descriptor");
        // Core since 1.1, but AMD's FidelityFX DLL calls the KHR-named entry points when the GPU lists the extensions, and those are
        // null unless the extensions are enabled (DECISIONS 16).
        foreach (var e in (string[])["VK_KHR_get_memory_requirements2", "VK_KHR_dedicated_allocation"])
            if (have.Contains(e)) names.Add(e);
        foreach (var e in options.DeviceExtensions ?? [])
            // Buffer device address is core in 1.2 (enabled as a feature): its extensions may not be enabled with it.
            if (have.Contains(e) && !names.Contains(e) && e is not ("VK_EXT_buffer_device_address" or "VK_KHR_buffer_device_address")) names.Add(e);

        // Queues
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
        float prio = 1f;
        var qcis = stackalloc DeviceQueueCreateInfo[2];
        uint qn = 0;
        qcis[qn++] = new DeviceQueueCreateInfo { SType = StructureType.DeviceQueueCreateInfo, QueueFamilyIndex = GraphicsFamily, QueueCount = 1, PQueuePriorities = &prio };
        if (HasDedicatedTransfer)
        {
            qcis[qn++] = new DeviceQueueCreateInfo { SType = StructureType.DeviceQueueCreateInfo, QueueFamilyIndex = TransferFamily, QueueCount = 1, PQueuePriorities = &prio };
        }

        var extPtrs = (byte**)SilkMarshal.StringArrayToPtr(names.ToArray());
        try
        {
            f2.PNext = chain;
            var dci = new DeviceCreateInfo
            {
                SType = StructureType.DeviceCreateInfo,
                PNext = &f2,
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

        Vk.GetDeviceQueue(Device, GraphicsFamily, 0, out var gq);
        GraphicsQueue = gq;
        if (HasDedicatedTransfer)
        {
            Vk.GetDeviceQueue(Device, TransferFamily, 0, out var tq);
            TransferQueue = tq;
        }
        else
        {
            TransferQueue = gq;
        }

        if (HasPushDescriptor)
        {
            var pushProps = new PhysicalDevicePushDescriptorPropertiesKHR { SType = StructureType.PhysicalDevicePushDescriptorPropertiesKhr };
            var p2 = new PhysicalDeviceProperties2 { SType = StructureType.PhysicalDeviceProperties2, PNext = &pushProps };
            Vk.GetPhysicalDeviceProperties2(pd, &p2);
            MaxPushDescriptors = pushProps.MaxPushDescriptors;
        }
        if (ValidationEnabled)
        {
            SetName(ObjectType.Device, (ulong)Device.Handle, "Meitou device");
            SetName(ObjectType.Queue, (ulong)GraphicsQueue.Handle, "graphics queue");
            if (HasDedicatedTransfer)
            {
                SetName(ObjectType.Queue, (ulong)TransferQueue.Handle, "transfer queue");
            }
        }
    }

    // ---- pipeline cache ------------------------------------------------------------------------------------

    void CreatePipelineCache()
    {
        pipelineCachePath = options.PipelineCachePath;
        byte[]? data = null;
        if (pipelineCachePath != null)
        {
            try
            {
                if (File.Exists(pipelineCachePath))
                {
                    data = File.ReadAllBytes(pipelineCachePath);
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

    void SavePipelineCache()
    {
        if (pipelineCachePath == null || PipelineCache.Handle == 0)
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
            var dir = Path.GetDirectoryName(Path.GetFullPath(pipelineCachePath));
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            var tmp = pipelineCachePath + ".tmp";
            File.WriteAllBytes(tmp, bytes.AsSpan(0, (int)size).ToArray());
            File.Move(tmp, pipelineCachePath, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A cache that cannot be saved only costs the next start some compile time.
        }
    }

    // ---- helpers -------------------------------------------------------------------------------------------

    /// <summary>Names an object for validation messages and capture tools (no-op without validation).</summary>
    public void SetName(ObjectType type, ulong handle, string name)
    {
        if (debugUtils == null || Device.Handle == 0)
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
            debugUtils.SetDebugUtilsObjectName(Device, in info);
        }
        finally
        {
            SilkMarshal.Free((nint)p);
        }
    }

    /// <summary>Frees the buffer once every frame that could use it has finished.</summary>
    public void DeferFree(GpuBuffer b) => Frames.DeferDelete(() => Allocator.Free(b));

    /// <summary>Frees the image once every frame that could use it has finished.</summary>
    public void DeferFree(GpuImage i) => Frames.DeferDelete(() => Allocator.Free(i));

    public void WaitIdle()
    {
        lock (QueueLock)
        {
            Vk.DeviceWaitIdle(Device);
        }
    }

    /// <summary>A one-shot command buffer (own transient pool) for readbacks and startup uploads. Finish with <see cref="EndImmediate"/>.</summary>
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
        immediatePools[(ulong)cb.Handle] = pool;
        return cb;
    }

    /// <summary>Ends, submits on the graphics queue and waits for completion.</summary>
    public void EndImmediate(CommandBuffer cb)
    {
        if (!immediatePools.TryRemove((ulong)cb.Handle, out var pool))
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
                    VulkanException.Check(Vk.QueueSubmit(GraphicsQueue, 1, in si, fence), "vkQueueSubmit");
                }
                VulkanException.Check(Vk.WaitForFences(Device, 1, in fence, true, ulong.MaxValue), "vkWaitForFences");
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

    // ---- teardown ------------------------------------------------------------------------------------------

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        if (Device.Handle != 0)
        {
            Vk.DeviceWaitIdle(Device);
            foreach (var pool in immediatePools.Values)
            {
                Vk.DestroyCommandPool(Device, pool, null);
            }
            immediatePools.Clear();
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
        if (Surface.Handle != 0 && khrSurface != null)
        {
            khrSurface.DestroySurface(Instance, Surface, null);
            Surface = default;
        }
        if (messenger.Handle != 0)
        {
            debugUtils!.DestroyDebugUtilsMessenger(Instance, messenger, null);
            messenger = default;
        }
        if (Instance.Handle != 0)
        {
            Vk.DestroyInstance(Instance, null);
            Instance = default;
        }
        if (selfHandle.IsAllocated)
        {
            selfHandle.Free();
        }
        Vk.Dispose();
    }
}
