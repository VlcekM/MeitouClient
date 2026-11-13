using System.Diagnostics;
using System.Runtime.InteropServices;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Gpu.Shaders;
using Meitou.Game.Rendering.Profiling;
using SharpMetal.Foundation;
using SharpMetal.Metal;
using SharpMetal.ObjectiveCCore;
using SharpMetal.QuartzCore;

namespace Meitou.Game.Rendering.Metal;

/// <summary>
/// The Metal device as the renderers see it (docs/resource-layer.md): the second implementation of
/// <see cref="IGpuContext"/>, sized to the sky pass — every operation a later pass needs first throws. The
/// frame loop is the context's own (<see cref="BeginFrame"/>, <see cref="EndFrame"/>), as the Vulkan
/// context's is; the host's pass model (<see cref="BeginHostPass"/>) mirrors <c>GpuContext</c>'s. The
/// renderers draw into an offscreen <see cref="Backbuffer"/> (the engine's Rgba8, as the Vulkan path's
/// <c>VulkanPresenter</c> has); the present is a flipped copy into the drawable — Metal's blit can do
/// neither the flip nor the format, so a five-line MSL pass does. Render thread only.
/// </summary>
public sealed unsafe class MetalContext : IGpuContext, IDisposable
{
    /// <summary>
    /// The frames in flight: the drawable ring's depth (three, as the layer's own).
    /// </summary>
    public const int FrameRingDepth = 3;

    /// <summary>
    /// The present pass's shaders: a full-screen triangle that copies the backbuffer into the drawable.
    /// </summary>
    const string PresentMsl = """
        #include <metal_stdlib>
        using namespace metal;

        vertex float4 present_vertex(uint id [[vertex_id]])
        {
            float2 xy = float2((id << 1) & 2, id & 2);
            return float4(xy * 2.0 - 1.0, 0.0, 1.0);
        }

        fragment float4 present_fragment(float4 position [[position]], constant float2& targetSize [[buffer(1)]],
            texture2d<float> source, sampler source_sampler)
        {
            // No flip: the engine's GL-style shaders land right side up in the backbuffer on Metal (the NDC points up as GL's
            // does), so row for row is the picture (the Vulkan path's blit flips instead - its backbuffer is upside down).
            // Divide by the drawable, not the source: on a scaled display the drawable is larger than the framebuffer the
            // scene and HUD are drawn into, and dividing by the source there showed only its top-left part.
            float2 uv = position.xy / targetSize;
            return source.sample(source_sampler, uv);
        }
        """;

    /// <summary>
    /// A scaled texture copy (the impostor bake's halving): a fullscreen sampling pass, as Metal's blit cannot scale.
    /// </summary>
    const string BlitMsl = """
        #include <metal_stdlib>
        using namespace metal;

        struct blit_out
        {
            float4 position [[position]];
        };

        vertex blit_out blit_vertex(uint id [[vertex_id]])
        {
            float2 xy = float2((id << 1) & 2, id & 2);
            blit_out o;
            o.position = float4(xy * 2.0 - 1.0, 0.0, 1.0);
            return o;
        }

        fragment float4 blit_fragment(blit_out in [[stage_in]], constant float2& targetSize [[buffer(1)]],
            texture2d<float> source [[texture(0)]], sampler sourceSampler [[sampler(0)]])
        {
            return source.sample(sourceSampler, in.position.xy / targetSize);
        }
        """;

    /// <summary>
    /// The attachments' partial clear (Vulkan's <c>vkCmdClearAttachments</c>): a fullscreen triangle under the clear rect's scissor.
    /// </summary>
    const string ClearMsl = """
        #include <metal_stdlib>
        using namespace metal;

        struct clear_out { float4 position [[position]]; };
        struct clear_depth_out { float depth [[depth(any)]]; };
        struct clear_both_out { float4 colour [[color(0)]]; float depth [[depth(any)]]; };

        vertex clear_out clear_vertex(uint id [[vertex_id]])
        {
            float2 xy = float2((id << 1) & 2, id & 2);
            clear_out o;
            o.position = float4(xy * 2.0 - 1.0, 0.0, 1.0);
            return o;
        }

        fragment float4 clear_colour_fragment(clear_out in [[stage_in]], constant float4& value [[buffer(0)]])
        {
            return value;
        }

        fragment clear_depth_out clear_depth_fragment(clear_out in [[stage_in]], constant float& value [[buffer(1)]])
        {
            clear_depth_out o;
            o.depth = value;
            return o;
        }

        fragment clear_both_out clear_both_fragment(clear_out in [[stage_in]], constant float4& colour [[buffer(0)]],
            constant float& value [[buffer(1)]])
        {
            clear_both_out o;
            o.colour = colour;
            o.depth = value;
            return o;
        }
        """;

    /// <summary>
    /// The window this context presents to; null for the headless (test) context.
    /// </summary>
    readonly MetalDisplay? _display;
    /// <summary>
    /// The Metal device every resource is made on.
    /// </summary>
    readonly MTLDevice _device;
    /// <summary>
    /// The queue the frames and upload batches are committed to.
    /// </summary>
    readonly MTLCommandQueue _queue;
    /// <summary>
    /// The drawable's pixel format (the present pipeline's colour attachment).
    /// </summary>
    readonly MTLPixelFormat _presentFormat;
    /// <summary>
    /// The largest MSAA sample count the device supports (from the constructor's probe).
    /// </summary>
    readonly int _maxSampleCount;
    /// <summary>
    /// The frame-slot wait mode from <c>MEITOU_METAL_SLOT_WAIT</c>: wait for the reused slot's command buffer (the default, as Vulkan's fence), and
    /// with "serial" for the previous frame instead; "0"/"false" turns the wait off for A/B diagnostics.
    /// </summary>
    readonly bool _waitForSlot, _waitOneFrame;

    /// <summary>
    /// Lossless target compression / base-view reuse, on by default (validated in the 2026-10-24 Metal run); <c>MEITOU_METAL_NO_COMPRESSION=1</c>
    /// disables it for an A/B. It is independent of the experimental GPU cull.
    /// </summary>
    static readonly bool LosslessCompression = Environment.GetEnvironmentVariable("MEITOU_METAL_NO_COMPRESSION") != "1";
    /// <summary>
    /// The frame ring's slots (<see cref="FrameRingDepth"/>), indexed by <see cref="_slot"/>.
    /// </summary>
    readonly MetalFrame[] _frames;
    /// <summary>
    /// The frame's GPU timestamps (<see cref="Timestamps"/>).
    /// </summary>
    readonly MetalTimestamps _timestamps = new();
    /// <summary>
    /// The bindless argument-buffer table behind <see cref="Bindless"/>.
    /// </summary>
    readonly MetalBindlessTable _bindless;
    /// <summary>
    /// Live textures by <see cref="GpuTexture"/> index; slot 0 is the null handle and a released texture leaves null.
    /// </summary>
    readonly List<MetalTexture?> _textures = [null];
    /// <summary>
    /// Live texture views by <see cref="GpuView"/> index; slot 0 is the null handle.
    /// </summary>
    readonly List<MetalTextureView?> _views = [null];
    /// <summary>
    /// Live buffers by <see cref="GpuBuffer"/> index; slot 0 is the null handle and a released buffer leaves null.
    /// </summary>
    readonly List<MetalBuffer?> _buffers = [null];
    /// <summary>
    /// Binding sets by <see cref="GpuSet"/> index; slot 0 is the null handle (sets are never released).
    /// </summary>
    readonly List<MetalSet?> _sets = [null];
    /// <summary>
    /// The stand-in textures a missing binding reads, by sampler slot, scalar kind and depth (made once by <see cref="Dummy"/>).
    /// </summary>
    readonly Dictionary<(int Slot, ScalarKind Kind, bool Depth), SampledTexture> _dummies = [];
    /// <summary>
    /// The textures behind <see cref="_dummies"/>, disposed at teardown.
    /// </summary>
    readonly List<GpuTexture> _dummyTextures = [];
    /// <summary>
    /// Depth states by test, write and compare op (<see cref="DepthState"/>).
    /// </summary>
    readonly Dictionary<(bool Test, bool Write, DepthFunction Op), MTLDepthStencilState> _depthStates = [];
    /// <summary>
    /// The dummy vertex buffer (<see cref="DummyVertex"/>): GL's current generic value for a disabled attribute.
    /// </summary>
    GpuBuffer _dummyVertex;
    /// <summary>
    /// The frame's backbuffer (window size) and the scene's depth and GI albedo (render size); made and resized by <see cref="EnsureTargets"/>.
    /// </summary>
    GpuTexture? _backbuffer, _depth, _albedo;
    /// <summary>
    /// The pipeline that copies the backbuffer into the drawable (<see cref="PresentMsl"/>).
    /// </summary>
    MTLRenderPipelineState _presentPipeline;
    /// <summary>
    /// The number of the frame being recorded, counted up by each frame begin; retirement and deferred deletes compare against it.
    /// </summary>
    long _frameNumber;
    /// <summary>
    /// The frame ring slot being recorded; -1 before the first frame.
    /// </summary>
    int _slot = -1;

    /// <summary>
    /// The autorelease pool around a frame. Objective-C objects returned by the frame's APIs (nextDrawable, commandBuffer, descriptors) are
    /// autoreleased; without a per-frame pool they accumulate for the whole process: 16k CAMetalLayer drawables after eight minutes.
    /// </summary>
    NSAutoreleasePool _framePool;
    /// <summary>
    /// Whether <see cref="_framePool"/> is open (drained at the frame's end or at teardown).
    /// </summary>
    bool _framePoolOpen;

    /// <summary>
    /// The host pass open now (the guests' targets and state); null outside a pass.
    /// </summary>
    PassTargets? _passTargets;
    /// <summary>
    /// The draw state of the open host pass; null outside a pass.
    /// </summary>
    DrawState? _passState;
    /// <summary>
    /// The command lists of the recording model: the open host pass, the open native segment and the open guest segment (null when closed).
    /// </summary>
    MetalCommandList? _passList, _segment, _guest;

    /// <summary>
    /// A context presenting to a window: the display's device, queue and drawable format.
    /// </summary>
    public MetalContext(MetalDisplay display) : this(display.Device, display.Queue, display.Layer.PixelFormat)
    {
        this._display = display;
    }

    /// <summary>
    /// A context without a window or drawable: the deterministic tests' entry point (uploads, resource lifetime, readbacks).
    /// </summary>
    internal MetalContext(MTLDevice device, MTLCommandQueue queue, MTLPixelFormat presentFormat)
    {
        this._device = device;
        this._queue = queue;
        this._presentFormat = presentFormat;
        Meitou.Game.Rendering.Frame.DepthConvention.Reversed = false;   // Metal keeps the conventional scene depth (docs/renderer-native.md)

        // Metal supports 2x/4x (and 8x on some discrete Macs) for the colour and depth targets the reflection uses. Ask the
        // device rather than assuming 4: the same reflection pass must fall back cleanly on a Mac without MSAA support.
        int supportedSamples = 1;
        for (int count = 2; count <= 8; count *= 2)
            if (device.SupportsTextureSampleCount((ulong)count)) supportedSamples = count;

        _maxSampleCount = supportedSamples;
        string slotWait = Environment.GetEnvironmentVariable("MEITOU_METAL_SLOT_WAIT") ?? "";

        // Vulkan waits for the slot's fence before reusing it. That rule is now the Metal default; "0"/"false" restores
        // the old frame-number-only behavior for A/B diagnostics, and "serial" waits for the immediately previous frame.
        _waitForSlot = slotWait is not ("0" or "false");
        _waitOneFrame = slotWait is "serial";

        if (_waitForSlot)
        {
            Console.WriteLine(_waitOneFrame
                ? "metal     slot wait enabled: one frame in flight (serial diagnostic)"
                : "metal     slot wait enabled: wait for the reused slot (Vulkan-style fence)");
        }
        else Console.WriteLine("metal     slot wait disabled (MEITOU_METAL_SLOT_WAIT=0)");

        _frames = new MetalFrame[FrameRingDepth];
        for (int i = 0; i < FrameRingDepth; i++) _frames[i] = new MetalFrame(this, i);

        Shaders = new MetalShaders(Device);
        Pipelines = new MetalPipelines(Device, Shaders);
        Samplers = new MetalSamplers(Device);
        Uploads = new MetalUploads(this);
        _bindless = new MetalBindlessTable(this);
        var shared = NewSharedBuffer(16, "dummy vertex");
        new Span<float>(shared.Pointer, 4)[3] = 1;   // GL's disabled-attribute constant (0, 0, 0, 1)
        _dummyVertex = RegisterBuffer(shared.Buffer, "dummy vertex");
        _presentPipeline = MakePresentPipeline();
    }

    /// <summary>
    /// The Metal device every resource is made on.
    /// </summary>
    public MTLDevice Device => _device;

    /// <summary>
    /// The GPU time of the last command buffer the three-frame ring found completed (ms); 0 until one is.
    /// </summary>
    public double LastGpuMilliseconds { get; private set; }

    /// <summary>
    /// Called with a frame's number and its command buffer's GPU milliseconds once it has completed, when its ring slot is taken by a later frame
    /// (the play benchmark's per-frame GPU time); null by default. Only the part after the previous frame's end counts: consecutive command
    /// buffers overlap on the GPU, so their whole spans add up to more than the GPU's busy time. The upload batches and the GPU cull's pre-frame
    /// are command buffers of their own and are not in it.
    /// </summary>
    public Action<long, double>? OnGpuFrame { get; set; }

    /// <summary>
    /// The GPU end time (seconds) of the last frame reported to <see cref="OnGpuFrame"/>.
    /// </summary>
    double _reportedGpuEnd;

    /// <summary>
    /// <c>objc_msgSend</c> for a selector that returns a double (a command buffer's GPU start and end times).
    /// </summary>
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    static extern double Time_objc_msgSend(IntPtr receiver, IntPtr selector);

    /// <summary>
    /// <c>objc_retain</c>: keeps a command buffer alive while retired textures wait for it.
    /// </summary>
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_retain")]
    static extern IntPtr Retain_objc_retain(IntPtr obj);

    /// <summary>
    /// The command buffer's <c>GPUStartTime</c> selector (seconds).
    /// </summary>
    static readonly Selector GpuStartTime = new("GPUStartTime");
    /// <summary>
    /// The command buffer's <c>GPUEndTime</c> selector (seconds).
    /// </summary>
    static readonly Selector GpuEndTime = new("GPUEndTime");

    /// <summary>
    /// The GPU time of a slot's command buffer in ms, or 0 when it has none, has not completed or reported no time.
    /// </summary>
    double CompletedGpuMs(MetalFrame frame) => TryGpuSpan(frame, out double start, out double end) ? (end - start) * 1000 : 0;

    /// <summary>
    /// The GPU span of a completed frame in seconds: the scene command buffer's start to the last command buffer's end (the present's, when there is
    /// one).
    /// </summary>
    static bool TryGpuSpan(MetalFrame frame, out double start, out double end)
    {
        (start, end) = (0, 0);
        var scene = frame.SceneCommand;
        var last = frame.Command;
        if (scene.NativePtr == IntPtr.Zero || last.Status != MTLCommandBufferStatus.Completed) return false;

        start = Time_objc_msgSend(scene.NativePtr, GpuStartTime.SelPtr);
        end = Time_objc_msgSend(last.NativePtr, GpuEndTime.SelPtr);
        return end > start;
    }

    /// <summary>
    /// Tells <see cref="OnGpuFrame"/> the GPU time of the frame a slot held, before the slot takes the next frame (the slot wait has seen its
    /// command complete).
    /// </summary>
    void ReportGpuFrame(MetalFrame frame)
    {
        if (OnGpuFrame is not { } report || frame.Number == 0) return;

        if (!TryGpuSpan(frame, out double start, out double end)) return;

        double own = end - Math.Max(start, Math.Min(_reportedGpuEnd, end));
        _reportedGpuEnd = Math.Max(_reportedGpuEnd, end);
        report(frame.Number, own * 1000);
    }

    /// <summary>
    /// Debug trace: resource-table and frame-chunk counts (a per-frame leak shows as a count that keeps rising).
    /// </summary>
    public string ResourceSummary()
    {
        int tex = 0, liveViews = 0, liveBuffers = 0, retired = 0, level = 0;
        foreach (var t in _textures) if (t is not null) tex++;
        foreach (var v in _views) if (v is not null) liveViews++;
        foreach (var b in _buffers) if (b is not null) liveBuffers++;
        foreach (var r in _retiredTextures) retired += r.Views.Length;
        foreach (var r in _retiredLevelViews) level += r.Views.Length;

        int deferred = _deferredDeletes.Count;
        long chunkBytes = 0, chunkUsed = 0;
        int chunkCount = 0;

        foreach (var f in _frames)
        {
            chunkBytes += f.ChunkBytes;
            chunkUsed += f.ChunkUsedBytes;
            chunkCount += f.ChunkCount;
        }

        var managed = GC.GetGCMemoryInfo();
        long loh = managed.GenerationInfo.Length > 3 ? managed.GenerationInfo[3].SizeAfterBytes : 0;
        long rss = Process.GetCurrentProcess().WorkingSet64;
        long oldest = _retiredTextures.Count > 0 ? _frameNumber - _retiredTextures[0].Frame : 0;
        return $"textures {tex}/{_textures.Count} views {liveViews}/{_views.Count} retiredViews {retired} retiredTextures {_retiredTextures.Count} " +
            $"oldest {oldest} levelViews {level} deferredDeletes {deferred} buffers {liveBuffers}/{_buffers.Count} " +
            $"sets {_sets.Count} chunks {chunkCount} ({chunkBytes / 1048576} MB, {chunkUsed / 1048576} MB used); " +
            $"managed {managed.HeapSizeBytes / 1048576} MB (LOH {loh / 1048576} MB), RSS {rss / 1048576} MB, " +
            $"GPU {Device.CurrentAllocatedSize / 1048576} MB";
    }

    /// <summary>
    /// The queue the frames are committed to (an upload batch's command buffer comes from it too: the queue executes its command buffers in commit
    /// order).
    /// </summary>
    public MTLCommandQueue Queue => _queue;

    #region Seam services

    /// <summary>
    /// Upload bytes counted before the first frame, added to the first frame's statistics.
    /// </summary>
    long _pendingUploadBytes;

    /// <summary>
    /// The Metal shader library (MSL translation and compiled functions).
    /// </summary>
    public MetalShaders Shaders { get; }
    /// <summary>
    /// The Metal pipeline cache.
    /// </summary>
    public MetalPipelines Pipelines { get; }
    /// <summary>
    /// The Metal sampler cache.
    /// </summary>
    public MetalSamplers Samplers { get; }
    /// <summary>
    /// The Metal upload batches (staging copies into textures and buffers).
    /// </summary>
    public MetalUploads Uploads { get; }
    /// <summary>
    /// The frame ring slot being recorded.
    /// </summary>
    public MetalFrame Frame => _frames[_slot];
    /// <summary>
    /// The per-frame constants the renderers fill (<see cref="FrameGlobals"/>).
    /// </summary>
    public FrameGlobals Globals { get; } = new();
    /// <summary>
    /// The frame as the seam's interface (<see cref="IGpuContext.Frame"/>).
    /// </summary>
    IGpuFrame IGpuContext.Frame => Frame;

    /// <inheritdoc/>
    IGpuShaders IGpuContext.Shaders => Shaders;
    /// <inheritdoc/>
    IGpuPipelines IGpuContext.Pipelines => Pipelines;
    /// <inheritdoc/>
    IGpuSamplers IGpuContext.Samplers => Samplers;
    /// <inheritdoc/>
    IGpuUploads IGpuContext.Uploads => Uploads;

    /// <summary>
    /// The bindless table (docs/metal-backend.md, "The bindless table"): the bindless set as a Metal 3 tier-2 argument buffer, one per frame slot.
    /// </summary>
    public IGpuBindlessTable Bindless => _bindless;

    /// <summary>
    /// The frame's number (the bindless table releases freed entries once the frames that could read them are done).
    /// </summary>
    internal long FrameNumber => _frameNumber;

    /// <summary>
    /// The device limit a uniform buffer slice's offset must align to (Metal's constant buffer alignment).
    /// </summary>
    public ulong UniformBufferAlign => 256;

    /// <summary>
    /// The device is an integrated GPU: Apple silicon's memory is unified (its budgets and star layers differ).
    /// </summary>
    public bool IsIntegrated => true;

    /// <summary>
    /// The frame's timestamps (<see cref="MetalTimestamps"/>; nothing is read yet).
    /// </summary>
    internal MetalTimestamps Timestamps => _timestamps;

    /// <summary>
    /// The upscaler's texture LOD bias: added to the mip level of every mipmapped fetch (set by the post-processing chain).
    /// </summary>
    public float LodBias { get; set; }

    /// <summary>
    /// Upload bytes into the frame's statistics; a batch before the first frame (the sky's textures at boot) counts into the next one.
    /// </summary>
    internal void CountUpload(int bytes)
    {
        if (_slot >= 0) Frame.Stats.UploadBytes += bytes;
        else _pendingUploadBytes += bytes;
    }

    /// <summary>
    /// Whether the open rendering has a depth attachment (<see cref="MetalCommandList.SetDepth"/>; Metal's validation objects to setting the state
    /// without one).
    /// </summary>
    internal bool HasDepthAttachment { get; private set; }

    /// <summary>
    /// The backend's depth state for a test/write/compare (made once, cached by value).
    /// </summary>
    internal MTLDepthStencilState DepthState(bool test, bool write, DepthFunction op)
    {
        var key = (test, write, op);
        if (_depthStates.TryGetValue(key, out var made)) return made;

        var d = new MTLDepthStencilDescriptor();
        d.DepthCompareFunction = test ? MetalConventions.Compare(op) : MTLCompareFunction.Always;
        d.IsDepthWriteEnabled = test && write;
        return _depthStates[key] = Device.NewDepthStencilState(d);
    }

    #endregion

    #region Resources

    /// <summary>
    /// Released textures kept until the command that could name them has completed; see <see cref="ReleaseRetired"/>.
    /// </summary>
    readonly List<(long Frame, MTLCommandBuffer Command, MTLTexture[] Views, MetalTexture Texture)> _retiredTextures = [];
    /// <summary>
    /// Mip-bake level views retained by their encoding command (the impostor bakes create these).
    /// </summary>
    readonly List<(long Frame, MTLCommandBuffer Command, MTLTexture[] Views)> _retiredLevelViews = [];
    /// <summary>
    /// Upload batches' staging buffers, kept until the command buffer that reads them has completed.
    /// </summary>
    readonly List<(long Frame, MTLCommandBuffer Command, MTLBuffer Buffer, string Label)> _retiredUploads = [];
    /// <summary>
    /// Whether a failed upload command buffer has been logged (only the first is).
    /// </summary>
    bool _uploadErrorReported;
    /// <summary>
    /// Actions from <see cref="DeferDelete"/>, released after the frame ring has passed.
    /// </summary>
    readonly DeferredReleaseQueue _deferredDeletes = new();

    /// <inheritdoc/>
    public GpuTexture CreateTexture(TextureDesc desc)
    {
        var d = new MTLTextureDescriptor();
        int samples = Math.Max(1, desc.Samples);
        d.TextureType = desc.Kind switch
        {
            TextureKind.Texture2D => samples > 1 ? MTLTextureType.Type2DMultisample : MTLTextureType.Type2D,
            TextureKind.Texture2DArray => samples > 1 ? MTLTextureType.Type2DMultisampleArray : MTLTextureType.Type2DArray,
            TextureKind.Cube => MTLTextureType.Cube,
            _ => throw new NotSupportedException($"no Metal texture type for {desc.Kind}"),
        };
        if (samples > 1 && desc.Kind == TextureKind.Cube)
            throw new NotSupportedException("Metal has no multisampled cube textures");

        d.PixelFormat = MetalConventions.Pixel(desc.Format);
        d.Width = (ulong)desc.Width;
        d.Height = (ulong)desc.Height;
        d.MipmapLevelCount = (ulong)(samples > 1 ? 1 : desc.Levels);

        // A cube's six faces are the type's own (arrayLength 1); only a cube *array* counts layers.
        d.ArrayLength = (ulong)(desc.Kind == TextureKind.Cube ? 1 : desc.Layers);
        d.SampleCount = (ulong)samples;

        // A render target nothing writes as a storage image: the GPU alone touches it (the CPU reads pictures back through buffers), so it can be
        // private and stay eligible for lossless compression, which a shaderWrite or pixelFormatView usage (or, in effect, shared storage) rules out.
        bool target = desc.Use.HasFlag(TextureUse.ColourTarget) || desc.Use.HasFlag(TextureUse.DepthTarget);
        bool compressible = LosslessCompression && target && !desc.Use.HasFlag(TextureUse.Storage);

        // Unified memory: shared where the CPU writes or reads, private where the GPU alone does (a compressible target) or an external producer
        // (MetalFX) requires it.
        bool gpuOnly = desc.Private || compressible;
        d.StorageMode = gpuOnly ? MTLStorageMode.Private : MTLStorageMode.Shared;
        MTLTextureUsage usage = 0;
        if (desc.Use.HasFlag(TextureUse.Sampled)) usage |= MTLTextureUsage.ShaderRead;
        if (desc.Use.HasFlag(TextureUse.Storage)) usage |= MTLTextureUsage.ShaderWrite;
        if (target) usage |= MTLTextureUsage.RenderTarget;

        // A blit destination needs no shaderWrite; it is kept where the texture is not a target (the uploads' own textures).
        if (desc.Use.HasFlag(TextureUse.TransferDst) && !compressible) usage |= MTLTextureUsage.ShaderWrite;

        // A plain 2D target can be attached and sampled through its base image (see <c>View</c>); only sub-ranges, layers and swizzles
        // need MTLTexture views. Omitting the flag on the plain targets lets Metal use lossless compression.
        bool plain = desc.Levels <= 1 && desc.Layers <= 1 && desc.Kind == TextureKind.Texture2D;
        bool needsViews = !LosslessCompression || !compressible && desc.Use.HasFlag(TextureUse.Sampled) || desc.Use.HasFlag(TextureUse.Storage) || !plain;
        if (needsViews) usage |= MTLTextureUsage.PixelFormatView;

        d.Usage = usage;
        d.ResourceOptions = gpuOnly ? MTLResourceOptions.ResourceStorageModePrivate : MTLResourceOptions.ResourceStorageModeShared;
        var image = Device.NewTexture(d);
        if (desc.Name.Length > 0) image.Label = (NSString)desc.Name;

        var texture = new MetalTexture { Image = image, Desc = desc };
        _textures.Add(texture);
        return new GpuTexture(this, desc, _textures.Count - 1);
    }

    /// <summary>
    /// The store action of a render pass attachment. Always <see cref="MTLStoreAction.Store"/> but where nothing reads the result: the GI albedo
    /// (the lit programs write it, but the GI resolve is not on Metal) and a multisampled depth (the reflection's: cleared by its one pass and never
    /// resolved). Those stay in tile memory.
    /// </summary>
    internal MTLStoreAction StoreActionFor(GpuView view, bool depth)
    {
        var of = _views[view.Index]!.Of;
        bool albedo = _albedo is { } a && ReferenceEquals(of, _textures[a.Index]);
        bool unresolvedDepth = depth && of.Desc.Samples > 1;
        return albedo || unresolvedDepth ? MTLStoreAction.DontCare : MTLStoreAction.Store;
    }

    /// <inheritdoc/>
    public GpuView View(GpuTexture texture, int baseLevel, int levels, int baseLayer, int layers, GpuViewType type, GpuSwizzle swizzle)
    {
        var of = _textures[texture.Index]!;
        var key = new MetalViewKey(baseLevel, levels, baseLayer, layers, type, swizzle);
        if (of.ViewCache.TryGetValue(key, out var cached)) return new GpuView(cached.ViewIndex);

        var format = MetalConventions.Pixel(of.Desc.Format);
        var viewType = type switch
        {
            GpuViewType.Texture2D => MTLTextureType.Type2D,
            GpuViewType.Texture2DArray => MTLTextureType.Type2DArray,
            GpuViewType.Cube => MTLTextureType.Cube,
            _ => throw new NotSupportedException($"no Metal view type for {type}"),
        };
        var levelRange = new NSRange { location = (ulong)baseLevel, length = (ulong)levels };
        var layerRange = new NSRange { location = (ulong)baseLayer, length = (ulong)layers };

        // A full-range identity view is the texture itself. Keeping the base image here is what lets a plain render
        // target omit PixelFormatView and stay eligible for Metal's lossless compression; only a real sub-range or
        // swizzled view creates a child texture. A multisampled texture always keeps its own image: its base type is
        // Type2DMultisample, and a child Type2D view would lose the samples (the reflection's MSAA targets depend on this).
        int totalLayers = of.Desc.Kind == TextureKind.Cube ? 6 * Math.Max(of.Desc.Layers, 1) : of.Desc.Layers;
        bool baseView = (LosslessCompression || of.Desc.Samples > 1) && baseLevel == 0 && levels == of.Desc.Levels && baseLayer == 0
            && layers == totalLayers && swizzle.Equals(GpuSwizzle.Identity)
            && viewType == (of.Desc.Kind switch
            {
                TextureKind.Cube => MTLTextureType.Cube,
                TextureKind.Texture2DArray => MTLTextureType.Type2DArray,
                _ => MTLTextureType.Type2D,
            });
        var view = new MetalTextureView
        {
            // A swizzled view (the BC4 masks' red in all three, alpha one) is Metal's own texture swizzle: the same format, the
            // sample reads the channels the engine's GpuSwizzle names.
            Image = baseView ? of.Image : swizzle.Equals(GpuSwizzle.Identity)
                ? of.Image.NewTextureView(format, viewType, levelRange, layerRange)
                : of.Image.NewTextureView(format, viewType, levelRange, layerRange, Swizzle(swizzle)),
            Of = of,
            ViewIndex = _views.Count,
            OwnsImage = !baseView,
        };
        of.Views.Add(view);
        of.ViewCache[key] = view;
        _views.Add(view);
        return new GpuView(view.ViewIndex);
    }

    /// <summary>
    /// The Metal swizzle of a view's (each identity component keeps its own channel).
    /// </summary>
    static MTLTextureSwizzleChannels Swizzle(GpuSwizzle swizzle) => new()
    {
        red = Swizzle(swizzle.R, MTLTextureSwizzle.Red),
        green = Swizzle(swizzle.G, MTLTextureSwizzle.Green),
        blue = Swizzle(swizzle.B, MTLTextureSwizzle.Blue),
        alpha = Swizzle(swizzle.A, MTLTextureSwizzle.Alpha),
    };

    /// <summary>
    /// The Metal swizzle source of one component; identity reads the component's own channel.
    /// </summary>
    static MTLTextureSwizzle Swizzle(GpuSwizzleComponent component, MTLTextureSwizzle identity) => component switch
    {
        GpuSwizzleComponent.Identity => identity,
        GpuSwizzleComponent.R => MTLTextureSwizzle.Red,
        GpuSwizzleComponent.G => MTLTextureSwizzle.Green,
        GpuSwizzleComponent.B => MTLTextureSwizzle.Blue,
        GpuSwizzleComponent.A => MTLTextureSwizzle.Alpha,
        GpuSwizzleComponent.One => MTLTextureSwizzle.One,
        GpuSwizzleComponent.Zero => MTLTextureSwizzle.Zero,
        _ => throw new NotSupportedException($"no Metal swizzle for {component}"),
    };

    /// <summary>
    /// Releases a texture and its views: their handles are cleared now and the Metal objects released once the command buffer that could name them
    /// has completed (<see cref="ReleaseRetired"/>); before the first frame they are released at once.
    /// </summary>
    public void ReleaseTexture(GpuTexture texture)
    {
        var of = _textures[texture.Index]!;
        var images = new List<MTLTexture>(of.Views.Count);

        for (int i = 0; i < of.Views.Count; i++)
        {
            _views[of.Views[i].ViewIndex] = null;
            if (of.Views[i].OwnsImage) images.Add(of.Views[i].Image);
        }

        of.Views.Clear();
        of.ViewCache.Clear();
        _textures[texture.Index] = null;

        // A texture view retains its parent. Releasing both at once while an earlier command buffer still names either can
        // fault the driver; retain the command buffer that could name them and retire the texture once it completes.
        if (_slot < 0) ReleaseTextureNow(images, of);
        else
        {
            var command = _frames[_slot].Command;

            if (command.NativePtr != IntPtr.Zero &&
                command.Status is not (MTLCommandBufferStatus.Completed or MTLCommandBufferStatus.Error))
            {
                Retain_objc_retain(command.NativePtr);
            }
            else
                command = default;   // already complete (or forgotten by Finish): no command check is needed

            _retiredTextures.Add((_frameNumber, command, images.ToArray(), of));
        }
    }

    /// <summary>
    /// Releases a texture's own image views and the texture.
    /// </summary>
    void ReleaseTextureNow(IReadOnlyList<MTLTexture> images, MetalTexture texture)
    {
        foreach (var image in images) image.Dispose();

        texture.Release();
    }

    /// <summary>
    /// Releases retired textures whose command buffer has completed or failed, at most 64 per call unless <paramref name="force"/> (teardown)
    /// releases them all.
    /// </summary>
    void ReleaseRetired(bool force = false)
    {
        if (_retiredTextures.Count == 0) return;

        int budget = force ? int.MaxValue : 64;

        for (int i = 0; i < _retiredTextures.Count && budget > 0;)
        {
            var retired = _retiredTextures[i];

            if (!force)
            {
                // Keep the views through the bindless table's own three-frame release (it still holds the view until its
                // index is handed back), then release once the command buffer that recorded them has completed.
                if (_frameNumber - retired.Frame < FrameRingDepth)
                {
                    i++;
                    continue;
                }

                if (retired.Command.NativePtr != IntPtr.Zero)
                {
                    var status = retired.Command.Status;

                    if (status is not (MTLCommandBufferStatus.Completed or MTLCommandBufferStatus.Error))
                    {
                        i++;
                        continue;
                    }
                }
            }

            ReleaseTextureNow(retired.Views, retired.Texture);
            if (retired.Command.NativePtr != IntPtr.Zero) retired.Command.Dispose();

            _retiredTextures.RemoveAt(i);
            budget -= Math.Max(1, retired.Views.Length);
        }
    }

    /// <summary>
    /// Keeps a mip-bake view alive until the command that encoded it has completed.
    /// </summary>
    void RetireLevelViews(MTLCommandBuffer command, List<MTLTexture> images)
    {
        if (images.Count == 0) return;

        if (command.NativePtr == IntPtr.Zero)
        {
            foreach (var image in images) image.Dispose();

            return;
        }

        Retain_objc_retain(command.NativePtr);
        _retiredLevelViews.Add((_frameNumber, command, images.ToArray()));
    }

    /// <summary>
    /// Releases retired mip-bake level views whose command buffer has completed or failed, at most 64 per call unless <paramref name="force"/>
    /// releases them all.
    /// </summary>
    void ReleaseRetiredLevelViews(bool force = false)
    {
        if (_retiredLevelViews.Count == 0) return;

        int budget = force ? int.MaxValue : 64;

        for (int i = 0; i < _retiredLevelViews.Count && budget > 0;)
        {
            var retired = _retiredLevelViews[i];

            if (!force && retired.Command.Status is not (MTLCommandBufferStatus.Completed or MTLCommandBufferStatus.Error))
            {
                i++;
                continue;
            }

            foreach (var image in retired.Views) image.Dispose();

            retired.Command.Dispose();
            _retiredLevelViews.RemoveAt(i);
            budget -= retired.Views.Length;
        }
    }

    /// <summary>
    /// Keeps an upload batch's staging buffer alive until its command buffer has completed (and reports the first upload failure).
    /// </summary>
    internal void RetireUpload(MTLCommandBuffer command, MTLBuffer buffer, string label = "")
    {
        if (buffer.NativePtr == IntPtr.Zero) return;

        if (command.NativePtr == IntPtr.Zero)
        {
            buffer.Dispose();
            return;
        }

        Retain_objc_retain(command.NativePtr);
        _retiredUploads.Add((_frameNumber, command, buffer, label));
    }

    /// <summary>
    /// Releases upload staging buffers whose command buffer has completed or failed (all with <paramref name="force"/>); logs the first failed upload
    /// with its label.
    /// </summary>
    void ReleaseRetiredUploads(bool force = false)
    {
        if (_retiredUploads.Count == 0) return;

        for (int i = 0; i < _retiredUploads.Count;)
        {
            var retired = _retiredUploads[i];
            var status = retired.Command.Status;

            if (!force && status is not (MTLCommandBufferStatus.Completed or MTLCommandBufferStatus.Error))
            {
                i++;
                continue;
            }

            if (status == MTLCommandBufferStatus.Error && !_uploadErrorReported)
            {
                _uploadErrorReported = true;
                string where = retired.Label.Length > 500 ? retired.Label[..500] + "..." : retired.Label;
                Console.WriteLine($"metal     upload command buffer failed{(where.Length > 0 ? $" ({where})" : "")}: " +
                    $"{retired.Command.Error.LocalizedDescription}");
            }

            retired.Buffer.Dispose();
            retired.Command.Dispose();
            _retiredUploads.RemoveAt(i);
        }
    }

    /// <summary>
    /// Runs <see cref="DeferDelete"/> actions whose frame the ring has passed, at most 64 per call unless <paramref name="force"/> runs them all.
    /// </summary>
    void ReleaseDeferred(bool force = false) => _deferredDeletes.ReleaseDue(_frameNumber, FrameRingDepth, 64, force);

    /// <summary>
    /// A buffer: GPU-only buffers are private storage (written only through the staging blit); the rest are shared, the unified memory the CPU writes
    /// directly.
    /// </summary>
    public GpuBuffer CreateBuffer(ulong size, BufferUse use, string name)
    {
        if (use.HasFlag(BufferUse.GpuOnly))
        {
            // A static vertex/index/texture-upload target is written through the staging blit and never mapped on the CPU:
            // private lets Metal put it in the GPU's own allocation and its compression/cache path.
            var buffer = Device.NewBuffer(Math.Max(size, 8ul), MTLResourceOptions.ResourceStorageModePrivate);
            return RegisterBuffer(buffer, name);
        }

        // Device-local on Apple silicon is the unified memory itself; shared mode makes the CPU's writes (the dummy vertex,
        // the frame's constants ring) the same object the GPU reads.
        var shared = NewSharedBuffer(size, name);
        return RegisterBuffer(shared.Buffer, name);
    }

    /// <summary>
    /// Releases a buffer: its handle is cleared now and the Metal buffer released once the frame ring has passed (at once before the first frame).
    /// </summary>
    public void ReleaseBuffer(GpuBuffer buffer)
    {
        // A mesh/index/instance buffer can be named by a frame still in flight when streaming unloads it; releasing it
        // immediately let the GPU read freed memory (the far-travel command-buffer faults). Keep the Metal buffer until
        // the frame ring has passed, as the texture path does.
        var of = _buffers[buffer.Index];
        if (of is null) return;

        _buffers[buffer.Index] = null;
        if (_slot < 0) of.Release();
        else DeferDelete(of.Release);
    }

    /// <inheritdoc/>
    public GpuSet CreateSet(GpuSetLayout layout, ReadOnlySpan<GpuBinding> bindings)
    {
        _sets.Add(new MetalSet { Layout = layout, Bindings = [.. bindings] });
        return new GpuSet(_sets.Count - 1);
    }

    /// <summary>
    /// The constants block (the seam's <see cref="IGpuContext.AllocateConstantSet"/>): on Metal the set binds straight onto the encoder and reads the
    /// offset at the bind, so it is an ordinary set holding the buffer.
    /// </summary>
    public GpuSet AllocateConstantSet(GpuSetLayout layout, GpuBuffer buffer, ulong size) =>
        CreateSet(layout, [new GpuBinding(0, GpuBindingKind.UniformBuffer, Buffer: new GpuBufferBinding(buffer, 0, size))]);

    /// <summary>
    /// Records a prepared guest segment at once, as a guest of the host's open pass (Metal records in order on the calling thread).
    /// </summary>
    public void Record(string label, RecordJob job)
    {
        var cmd = BeginGuest(label);

        try
        {
            job.Record(cmd);
        }
        finally
        {
            EndGuest(cmd);
            job.Release();
        }
    }

    #endregion

    #region Recording model

    // The host's recording model.

    /// <inheritdoc/>
    public IGpuCommandList BeginGuest(string label)
    {
        if (_guest is not null) throw new InvalidOperationException("BeginGuest inside a guest segment");
        if (_passList is null) throw new InvalidOperationException("BeginGuest without a host pass");

        _passList.Invalidate();
        _passList.BeginLabel(label);
        Frame.Stats.NativeSegments++;
        return _guest = _passList;
    }

    /// <inheritdoc/>
    public void EndGuest(IGpuCommandList cmd)
    {
        if (_guest is null || !ReferenceEquals(cmd, _guest))
            throw new InvalidOperationException("EndGuest without a matching BeginGuest");

        _guest = null;
        cmd.EndLabel();
    }

    /// <inheritdoc/>
    public IGpuCommandList BeginNative(string label)
    {
        if (_segment is not null) throw new InvalidOperationException("BeginNative inside a native segment");

        EnsureFrame();
        var list = Frame.Commands;
        list.Invalidate();
        Frame.States.AssumeFullBarrier();
        Frame.Stats.NativeSegments++;
        list.BeginLabel(label);
        return _segment = list;
    }

    /// <inheritdoc/>
    public void EndNative(IGpuCommandList cmd)
    {
        if (_segment is null || !ReferenceEquals(cmd, _segment))
            throw new InvalidOperationException("EndNative without a matching BeginNative");
        if (PassOpen) throw new InvalidOperationException("EndNative with a host pass open");

        cmd.EndLabel();
        _segment = null;
    }

    /// <inheritdoc/>
    public bool PassOpen => _passList is not null;

    /// <inheritdoc/>
    public PassTargets CurrentTargets() => _passTargets ?? throw new InvalidOperationException("no pass is open");
    /// <inheritdoc/>
    public DrawState CurrentState() => _passState ?? throw new InvalidOperationException("no pass is open");

    /// <summary>
    /// Records something that does not disturb a pass (a timestamp, a label) where the frame's commands go now.
    /// </summary>
    public void Interleave(Action<IGpuCommandList> record)
    {
        if (_passList is { } host && _guest is null)
        {
            record(host);
            return;
        }

        EnsureFrame();
        record(Frame.Commands);
    }

    #endregion

    #region Host pass

    // The host's pass (the twin of GpuContext's BeginHostPass; the Metal host drives it directly).

    /// <summary>
    /// The caller has begun a rendering instance on <paramref name="cmd"/> (inside its own native segment) into
    /// <paramref name="targets"/>: until <see cref="EndHostPass"/> guests draw into it with <paramref name="state"/>.
    /// </summary>
    public void BeginHostPass(MetalCommandList cmd, PassTargets targets, DrawState state)
    {
        if (_passList is not null) throw new InvalidOperationException("a host pass is already open");

        (_passList, _passTargets, _passState) = (cmd, targets, state);
        HasDepthAttachment = !targets.Depth.View.IsNull;
    }

    /// <inheritdoc/>
    void IGpuContext.BeginHostPass(IGpuCommandList cmd, PassTargets targets, DrawState state) => BeginHostPass((MetalCommandList)cmd, targets, state);

    /// <summary>
    /// Metal records inline: the stage is only a Vulkan secondary-pass concept, and the depth clear goes straight to the open list.
    /// </summary>
    public void BeginHostPassParallel(IGpuCommandList cmd, PassTargets targets, DrawState state, int stage) =>
        BeginHostPass((MetalCommandList)cmd, targets, state);

    /// <summary>
    /// Nothing to do: Metal records inline (the stage is a Vulkan secondary-pass concept).
    /// </summary>
    public void SetPassStage(int stage) { }

    /// <summary>
    /// Nothing to close: Metal records inline, without secondary passes.
    /// </summary>
    public void EndHostPassParallel(IGpuCommandList cmd) { }

    /// <inheritdoc/>
    public void ClearDepth(IGpuCommandList host, float value, PixelRect rect)
    {
        if (_passList is null || !ReferenceEquals(host, _passList))
            throw new InvalidOperationException("ClearDepth without the open host pass");

        host.ClearDepth(value, rect);
    }

    /// <summary>
    /// The viewport and scissor (null: the whole viewport) the guests draw with from now on (a cascade's tile).
    /// </summary>
    public void SetPassViewport(PixelViewport viewport, PixelRect? scissor = null)
    {
        if (_passTargets is null) throw new InvalidOperationException("no host pass is open");

        _passTargets = _passTargets.At(viewport, scissor);
    }

    /// <inheritdoc/>
    public bool DepthClamp => true;

    /// <inheritdoc/>
    public bool RecordsSecondaries => false;

    /// <inheritdoc/>
    public void EndHostPass(IGpuCommandList cmd)
    {
        if (_passList is null || !ReferenceEquals(cmd, _passList))
            throw new InvalidOperationException("EndHostPass without a matching BeginHostPass");

        (_passList, _passTargets, _passState) = (null, null, null);
        HasDepthAttachment = false;
    }

    #endregion

    #region Frame loop

    /// <summary>
    /// Begins a frame: its command buffer, and the frame's backbuffer (the offscreen target the renderers
    /// draw into, re-made when the window's size changed). The drawable is taken at the end (<see cref="EndFrame"/>).
    /// False when the window has no pixels: the frame is skipped.
    /// </summary>
    public bool BeginFrame()
    {
        var display = this._display ?? throw new InvalidOperationException("a headless MetalContext has no frame loop");

        // Every frame needs its own pool: CAMetalLayer's nextDrawable and the command buffers are autoreleased, and a
        // pool held until quit keeps every drawable and its IOSurface alive (the travel footprint grew to 15 GB).
        if (_framePoolOpen)
        {
            _framePool.Dispose();
            _framePoolOpen = false;
        }

        _framePool = new NSAutoreleasePool();
        _framePoolOpen = true;

        // Vulkan's FrameRing waits on the slot's fence before reusing per-frame memory or running deletions. The normal
        // diagnostic waits for the slot that this frame will reuse; "serial" also waits for the immediately previous
        // frame, making the whole backend one-frame-in-flight.
        WaitForSlot();

        // Free what the previous rings no longer name before the frame is recorded; a frame's worth at a time.
        ReleaseRetired();
        ReleaseRetiredLevelViews();
        ReleaseRetiredUploads();
        ReleaseDeferred();

        if (!display.TryBeginFrame(out var command))
        {
            _framePool.Dispose();
            _framePoolOpen = false;
            return false;
        }

        ReadLastGpuTime();
        var size = display.Window.FramebufferSize;
        EnsureTargets(size.X, size.Y);
        StartSlot(command);
        return true;
    }

    /// <summary>
    /// Waits for the slot this frame reuses (and, serial, for the previous frame) when the diagnostic asks for it.
    /// </summary>
    void WaitForSlot()
    {
        if (!_waitForSlot || _slot < 0) return;

        if (_waitOneFrame) _frames[_slot].WaitForCompletion();
        else _frames[(_slot + 1) % FrameRingDepth].WaitForCompletion();
    }

    /// <summary>
    /// The GPU time of the frame the slot held last, once it has completed.
    /// </summary>
    void ReadLastGpuTime()
    {
        if (_slot < 0) return;

        double gpu = CompletedGpuMs(_frames[_slot]);
        if (gpu > 0) LastGpuMilliseconds = gpu;
    }

    /// <summary>
    /// The size the scene renders at in a <paramref name="width"/> x <paramref name="height"/> pixel backbuffer (the window's backing pixels,
    /// Retina included) at <paramref name="renderScale"/>.
    /// </summary>
    public static (int Width, int Height) SceneSize(int width, int height, float renderScale)
    {
        float scale = Math.Clamp(renderScale, 0.25f, 1f);
        return (Math.Max((int)MathF.Round(width * scale), 1), Math.Max((int)MathF.Round(height * scale), 1));
    }

    /// <summary>
    /// The backbuffer at the window's size, and the scene's depth and GI albedo at the render size (re-made when either changed).
    /// </summary>
    void EnsureTargets(int width, int height)
    {
        if (_backbuffer is not { } target || target.Desc.Width != width || target.Desc.Height != height)
        {
            Console.WriteLine($"metal     backbuffer {width}x{height}");
            _backbuffer?.Dispose();
            _backbuffer = CreateTexture(new TextureDesc(InternalFormat.Rgba8, width, height,
                Use: TextureUse.ColourTarget | TextureUse.Sampled, Name: "backbuffer"));
        }

        float scale = Math.Clamp(RenderScale, 0.25f, 1f);
        (SceneWidth, SceneHeight) = SceneSize(width, height, scale);

        if (_depth is not { } currentDepth || currentDepth.Desc.Width != SceneWidth || currentDepth.Desc.Height != SceneHeight)
        {
            Console.WriteLine($"metal     scene {SceneWidth}x{SceneHeight} (scale {scale:0.##})");
            _depth?.Dispose();
            _depth = CreateTexture(new TextureDesc(InternalFormat.DepthComponent24, SceneWidth, SceneHeight,
                Use: TextureUse.DepthTarget | TextureUse.Sampled, Name: "depth"));
            _albedo?.Dispose();
            _albedo = CreateTexture(new TextureDesc(InternalFormat.Rgba8Ui, SceneWidth, SceneHeight, Use: TextureUse.ColourTarget, Name: "gi albedo"));
        }
    }

    /// <summary>
    /// The next slot of the ring begins the frame: its command buffer, the bindless table's and the timestamps' slot.
    /// </summary>
    void StartSlot(MTLCommandBuffer command)
    {
        _frameNumber++;
        _slot = (_slot + 1) % FrameRingDepth;
        ReportGpuFrame(_frames[_slot]);
        Retain_objc_retain(command.NativePtr);   // the frame pool drains at the end of this frame
        _frames[_slot].Begin(_frameNumber, command);
        _bindless.BeginFrame(_slot);

        if (_pendingUploadBytes > 0)
        {
            _frames[_slot].Stats.UploadBytes = _pendingUploadBytes;
            _pendingUploadBytes = 0;
        }

        _timestamps.Begin(_slot, _frameNumber);
    }

    /// <summary>
    /// The offscreen target the renderers draw into (the engine's Rgba8, as the Vulkan path's backbuffer is).
    /// </summary>
    public GpuTexture Backbuffer => _backbuffer ?? throw new InvalidOperationException("no frame is open");

    /// <summary>
    /// The scene's depth attachment (the scene's render size, re-made with it): the host pass tests and writes it.
    /// </summary>
    public GpuTexture Depth => _depth ?? throw new InvalidOperationException("no frame is open");

    /// <summary>
    /// The scene's second colour target (the lit fragments' GI albedo; the GI resolve is not on Metal yet, but the lit programs write it, so the pass
    /// has the attachment they expect).
    /// </summary>
    public GpuTexture Albedo => _albedo ?? throw new InvalidOperationException("no frame is open");

    /// <summary>
    /// The scene's render scale (the Vulkan path's TAA/upscaler scale): 1 = native, 0.67 = the Vulkan path's Meitou default.
    /// </summary>
    public float RenderScale { get; set; } = 1f;
    /// <summary>
    /// The width of the scene's render targets (the render size the frame was begun with), in pixels.
    /// </summary>
    public int SceneWidth { get; private set; }
    /// <summary>
    /// The height of the scene's render targets, in pixels.
    /// </summary>
    public int SceneHeight { get; private set; }

    /// <summary>
    /// Opens a frame without a display or drawable: the deterministic tests' counterpart of <see cref="BeginFrame"/>
    /// (the queues, the frame ring and the command lists are the real ones; only the present is omitted).
    /// </summary>
    internal void BeginHeadlessFrame()
    {
        if (_slot >= 0 && Frame.Open) throw new InvalidOperationException("a frame is already open");

        _frameNumber++;
        _slot = (_slot + 1) % FrameRingDepth;
        var command = Queue.CommandBuffer();
        Retain_objc_retain(command.NativePtr);
        _frames[_slot].Begin(_frameNumber, command);
        _bindless.BeginFrame(_slot);
        _timestamps.Begin(_slot, _frameNumber);
    }

    /// <summary>
    /// Closes the frame opened by <see cref="BeginHeadlessFrame"/> and waits for both command buffers.
    /// </summary>
    internal void EndHeadlessFrame()
    {
        if (_slot < 0 || !Frame.Open) throw new InvalidOperationException("no frame is open");

        var frame = _frames[_slot];
        _bindless.EndFrame();
        frame.End();
        _timestamps.End();
        frame.CommitPreFrame(wait: true);
        frame.Commands.Finish();
        frame.Command.Commit();
        frame.Command.WaitUntilCompleted();
        frame.ReleasePreFrame();
        if (frame.Command.Status != MTLCommandBufferStatus.Completed)
            Console.WriteLine($"metal     headless frame command failed: {frame.Command.Status} {frame.Command.Error.LocalizedDescription}");
    }

    /// <summary>
    /// Ends the frame: the scene's commands are committed, then the drawable is taken and the backbuffer is copied into it (flipped) by a
    /// small command buffer of its own that presents it.
    /// </summary>
    public void EndFrame()
    {
        var frame = _frames[_slot];
        _bindless.EndFrame();
        frame.End();
        _timestamps.End();
        Present(frame);

        if (_framePoolOpen)
        {
            _framePool.Dispose();
            _framePoolOpen = false;
        }
    }

    /// <summary>
    /// The present: the scene's work is committed first, so the GPU starts on it while the drawable is still being waited for (the layer hands
    /// one out only when the display has released an earlier one, and a wait for it before the commit would idle the GPU and push the whole
    /// frame behind the refresh). The backbuffer's picture then goes into the drawable, y flipped (a five-line MSL pass; Metal's blit can do
    /// neither the flip nor the format) in a command buffer that follows the scene's on the queue. Without a drawable the frame is not shown.
    /// </summary>
    void Present(MetalFrame frame)
    {
        // The GPU cull's dispatches can leave a compute encoder open (there is no rendering to close it); the
        // present encoder cannot be created beside it.
        frame.Commands.Finish();
        StageClock.BeginSubmission();
        frame.CommitPreFrame();
        frame.SceneCommand.Commit();
        StageClock.Lap(StageClock.Submit, gpuTimestamp: false);

        var display = _display ?? throw new InvalidOperationException("a headless MetalContext has no present");
        bool shown = display.TryNextDrawable(out var drawable);
        StageClock.Lap(StageClock.DriverWait, gpuTimestamp: false);
        if (!shown) return;

        var command = Queue.CommandBuffer();
        frame.AdoptPresent(command);
        frame.Drawable = drawable;
        command.Label = (NSString)$"Meitou present {frame.Number}";
        EncodePresent(command, drawable);
        command.PresentDrawable(drawable);
        command.Commit();
    }

    /// <summary>
    /// The present pass in <paramref name="command"/>: the backbuffer drawn into <paramref name="drawable"/>.
    /// </summary>
    void EncodePresent(MTLCommandBuffer command, CAMetalDrawable drawable)
    {
        var descriptor = new MTLRenderPassDescriptor(
            ObjectiveC.IntPtr_objc_msgSend(ObjectiveC.objc_getClass("MTLRenderPassDescriptor"), "renderPassDescriptor"));
        var attachments = descriptor.ColorAttachments;
        var attachment = attachments.Object(0);
        attachment.Texture = drawable.Texture;
        attachment.LoadAction = MTLLoadAction.DontCare;
        attachment.StoreAction = MTLStoreAction.Store;
        attachments.SetObject(attachment, 0);
        var encoder = command.RenderCommandEncoder(descriptor);
        encoder.SetRenderPipelineState(_presentPipeline);
        encoder.SetFragmentTexture(TextureAt(_backbuffer!), 0);
        encoder.SetFragmentSamplerState(Samplers.Backend(Samplers.Get(SamplerDesc.Clamped(TextureMinFilter.Nearest, TextureMagFilter.Nearest))), 0);
        var targetSize = stackalloc float[2] { (float)drawable.Texture.Width, (float)drawable.Texture.Height };
        encoder.SetFragmentBytes((nint)targetSize, 8, 1);
        encoder.DrawPrimitives(MTLPrimitiveType.Triangle, 0, 3);
        encoder.EndEncoding();
    }

    /// <inheritdoc/>
    public void EnsureFrame()
    {
        if (_slot < 0)
            throw new InvalidOperationException($"no frame slot (slot {_slot}, frame {_frameNumber}, thread {Environment.CurrentManagedThreadId})");
        if (!Frame.Open)
        {
            throw new InvalidOperationException(
                $"frame {Frame.Number} closed (slot {_slot}, frame {_frameNumber}, thread {Environment.CurrentManagedThreadId})");
        }
    }

    #endregion

    #region Backend lookups

    // The backend's own lookups (the command list resolves the handles through these).

    /// <summary>
    /// The Metal texture of a texture handle.
    /// </summary>
    internal MTLTexture TextureAt(GpuTexture texture) => _textures[texture.Index]!.Image;

    /// <summary>
    /// The Metal texture of a view handle.
    /// </summary>
    internal MTLTexture ViewTexture(GpuView view) => _views[view.Index]!.Image;

    /// <summary>
    /// The binding set of a set handle.
    /// </summary>
    internal MetalSet SetAt(GpuSet set) => _sets[set.Index]!;

    /// <summary>
    /// The Metal buffer and offset a buffer binding names.
    /// </summary>
    internal (MTLBuffer Buffer, ulong Offset) Buffer(GpuBufferBinding binding) => (_buffers[binding.Buffer.Index]!.Buffer, binding.Offset);

    /// <summary>
    /// The bindless table's own object (its resources are told to the encoder at the bind).
    /// </summary>
    internal MetalBindlessTable BindlessTable => _bindless;

    /// <summary>
    /// The Metal buffer of a buffer handle.
    /// </summary>
    internal MTLBuffer BufferAt(GpuBuffer buffer) => _buffers[buffer.Index]!.Buffer;

    /// <summary>
    /// A new shared-storage buffer (at least 16 bytes) with its CPU address.
    /// </summary>
    internal SharedBuffer NewSharedBuffer(ulong size, string name)
    {
        var buffer = Device.NewBuffer(Math.Max(size, 16ul), MTLResourceOptions.ResourceStorageModeShared);
        return new SharedBuffer(buffer, (byte*)buffer.Contents, buffer.Length);
    }

    /// <summary>
    /// A shared-mode buffer, its CPU address and its size (a record would carry the pointer as a generated field, which C# refuses).
    /// </summary>
    internal readonly struct SharedBuffer
    {
        /// <summary>
        /// The Metal buffer.
        /// </summary>
        public readonly MTLBuffer Buffer;
        /// <summary>
        /// Its contents' CPU address (shared storage).
        /// </summary>
        public readonly byte* Pointer;
        /// <summary>
        /// Its length in bytes.
        /// </summary>
        public readonly ulong Size;

        /// <summary>
        /// Wraps a shared buffer with its CPU address and length.
        /// </summary>
        public SharedBuffer(MTLBuffer buffer, byte* pointer, ulong size)
        {
            Buffer = buffer;
            Pointer = pointer;
            Size = size;
        }
    }

    /// <summary>
    /// Labels a Metal buffer with the name (when there is one) and registers it under a new handle.
    /// </summary>
    internal GpuBuffer RegisterBuffer(MTLBuffer buffer, string name)
    {
        if (name.Length > 0) buffer.Label = (NSString)name;

        _buffers.Add(new MetalBuffer { Buffer = buffer, Name = name });
        return new GpuBuffer(_buffers.Count - 1, buffer.Length);
    }

    /// <summary>
    /// Points an existing handle at another Metal buffer, labelled "frame constants" (nothing calls it now).
    /// </summary>
    internal void RebindBuffer(GpuBuffer handle, MTLBuffer buffer) =>
        _buffers[handle.Index] = new MetalBuffer { Buffer = buffer, Name = "frame constants" };

    /// <summary>
    /// The texture and sampler a combined binding reads.
    /// </summary>
    internal (MTLTexture Texture, MTLSamplerState Sampler) Sampled(SampledTexture sampled) =>
        (ViewTexture(sampled.View), Samplers.Backend(sampled.Sampler));

    /// <summary>
    /// The present pass's pipeline: the drawable's pixel format is the layer's (the only place the surface's format is named).
    /// </summary>
    MTLRenderPipelineState MakePresentPipeline()
    {
        NSError error = default;
        var library = Device.NewLibrary((NSString)PresentMsl, new MTLCompileOptions(), ref error);
        if (error.NativePtr != IntPtr.Zero)
            throw new InvalidOperationException($"the present pass's MSL did not compile: {error.LocalizedDescription}");

        var d = new MTLRenderPipelineDescriptor(new ObjectiveCClass(ObjectiveC.objc_getClass("MTLRenderPipelineDescriptor")).AllocInit());
        d.VertexFunction = library.NewFunction((NSString)"present_vertex");
        d.FragmentFunction = library.NewFunction((NSString)"present_fragment");
        var attachment = d.ColorAttachments.Object(0);
        attachment.PixelFormat = _presentFormat;
        d.ColorAttachments.SetObject(attachment, 0);
        var pipeline = Device.NewRenderPipelineState(d, ref error);
        if (error.NativePtr != IntPtr.Zero)
            throw new InvalidOperationException($"the present pass's pipeline failed: {error.LocalizedDescription}");

        return pipeline;
    }

    #endregion

    #region Stand-ins

    /// <summary>
    /// The bindless index of the 2D stand-in texture (<see cref="StandIn2D"/>), registered on first use.
    /// </summary>
    uint? _standIn2D;

    /// <summary>
    /// The vertices a disabled attribute may index: the generic value repeated at every 16-byte stride (4 MB an array).
    /// </summary>
    const int DummyVertexCapacity = 1 << 18;

    /// <summary>
    /// The dummy vertex arrays for float and integer attributes (<see cref="DummyVertexArray"/>), made on first use.
    /// </summary>
    GpuBuffer? _dummyFloatVertex, _dummyIntVertex;

    /// <inheritdoc/>
    public uint StandIn2D => _standIn2D ??= Bindless.Register(BindlessKind.Texture2D,
        Dummy(new SamplerInfo("", 0, 0, SamplerDimension.Dim2D, false, false, false, ScalarKind.Float, 0)));

    /// <inheritdoc/>
    public GpuBuffer DummyVertexArray(ScalarKind kind)
    {
        if (kind == ScalarKind.Float)
            return _dummyFloatVertex ??= MakeDummyVertexArray(0, 0, 0, 1, "dummy float vertices");

        return _dummyIntVertex ??= MakeDummyVertexArray(0, 0, 0, 1, "dummy int vertices");
    }

    /// <summary>
    /// A shared buffer of <see cref="DummyVertexCapacity"/> copies of one 16-byte generic vertex value.
    /// </summary>
    GpuBuffer MakeDummyVertexArray(int x, int y, int z, int w, string name)
    {
        var shared = NewSharedBuffer((ulong)DummyVertexCapacity * 16, name);
        var values = (int*)shared.Pointer;
        for (int i = 0; i < DummyVertexCapacity; i++)
            (values[i * 4], values[i * 4 + 1], values[i * 4 + 2], values[i * 4 + 3]) = (x, y, z, w);

        return RegisterBuffer(shared.Buffer, name);
    }

    /// <summary>
    /// The stand-in a sampler reads when nothing is bound: (0, 0, 0, 1), or depth 1 for a shadow sampler.
    /// </summary>
    public SampledTexture Dummy(SamplerInfo sampler)
    {
        int slot = sampler.Dimension == SamplerDimension.Cube ? 2 : sampler.Arrayed ? 1 : 0;
        var key = (slot, sampler.SampledKind, sampler.Depth);
        if (_dummies.TryGetValue(key, out var made)) return made;

        var format = sampler.Depth ? InternalFormat.DepthComponent24 : sampler.SampledKind switch
        {
            ScalarKind.UInt => InternalFormat.R32Ui,
            ScalarKind.Int => InternalFormat.R32I,
            _ => InternalFormat.Rgba8,
        };

        // A cube's six faces are its type's slices, not array layers: Layers is 1 for a plain cube (CreateTexture sets
        // ArrayLength 1), and GpuTexture.View asks for all six slices.
        int layers = 1;
        var texture = CreateTexture(new TextureDesc(format, 1, 1, Layers: layers, Kind: slot == 2 ? TextureKind.Cube : TextureKind.Texture2D,
            Use: TextureUse.Sampled | TextureUse.TransferDst, Name: "dummy texture"));

        // Shared storage: the CPU writes the texel where it is (a blit would need a command buffer of its own).
        var image = _textures[texture.Index]!.Image;
        var region = new MTLRegion { origin = new MTLOrigin(), size = new MTLSize { width = 1, height = 1, depth = 1 } };

        if (sampler.Depth)
        {
            float one = 1f;
            image.ReplaceRegion(region, 0, (nint)(&one), 4);
        }
        else
        {
            // The texel value is the R component (the other channels come from the format): 0, as GL's default texture is (0, 0, 0, 1).
            Span<byte> texel = [0, 0, 0, 255];
            if (sampler.SampledKind is ScalarKind.UInt or ScalarKind.Int) texel = new byte[4];
            fixed (byte* p = texel) image.ReplaceRegion(region, 0, (nint)p, 4);
        }

        var view = texture.View();
        var sampledTexture = new SampledTexture(Samplers.Get(SamplerDesc.FromGl(TextureMinFilter.Nearest, TextureMagFilter.Nearest,
            TextureWrapMode.Repeat, TextureWrapMode.Repeat, TextureWrapMode.Repeat, sampler.Depth, DepthFunction.Lequal, false, 1, false, 0)), view);
        _dummyTextures.Add(texture);
        _dummies[key] = sampledTexture;
        return sampledTexture;
    }

    /// <summary>
    /// The generic attribute value a disabled vertex input reads (GL's constant (0, 0, 0, 1), bound with stride 0).
    /// </summary>
    public GpuBuffer DummyVertex => _dummyVertex;

    #endregion

    #region Capabilities and the frame ring

    /// <inheritdoc/>
    public bool HasRayQuery => false;
    /// <summary>
    /// No tessellation stages on Metal.
    /// </summary>
    public bool HasTessellation => false;
    /// <inheritdoc/>
    public int MaxSampleCount => _maxSampleCount;

    /// <inheritdoc/>
    public bool ConstantVertexAttributes => false;
    /// <summary>
    /// False: row 0 of a Metal render target is at the top.
    /// </summary>
    public bool RenderTargetsBottomUp => false;

    /// <inheritdoc/>
    public int FramesInFlight => FrameRingDepth;

    /// <inheritdoc/>
    public ulong VideoMemoryBudget => Device.RecommendedMaxWorkingSetSize;

    /// <inheritdoc/>
    public void DeferDelete(Action release)
    {
        if (_slot < 0) release();
        else _deferredDeletes.Add(_frameNumber, release);
    }

    /// <inheritdoc/>
    public void Finish()
    {
        // A still that never opened a frame (no panel matched, or preparation failed) still has to shut down cleanly.
        if (_slot >= 0 && Frame.Open) EndFrame();

        var marker = Queue.CommandBuffer();
        marker.Commit();
        marker.WaitUntilCompleted();

        // The frame buffers are complete and are not referenced again; a later release must not retain a stale pointer.
        foreach (var frame in _frames) frame.ForgetCommand();

        // Do not force the texture queue here: Finish is also used for mid-run readbacks, and the bindless table may still
        // hold an index for another ring. Dispose forces the remainder once no frame can run again. Level views and
        // deferred actions have no bindless entries; the marker has completed every command that can use them.
        ReleaseRetiredLevelViews(force: true);
        ReleaseRetiredUploads(force: true);
        ReleaseDeferred(force: true);
    }

    /// <inheritdoc/>
    public IGpuReadback CreateReadback(ulong size, string name) => new MetalReadback(this, size, name);

    /// <inheritdoc/>
    public bool FrameCompleted(long frame) => _frameNumber - frame >= FrameRingDepth;

    #endregion

    #region Blits and clears

    /// <summary>
    /// Blit pipelines by target format (<see cref="BlitScaled"/>), made on first use.
    /// </summary>
    readonly Dictionary<MTLPixelFormat, MTLRenderPipelineState> _blitPipelines = [];
    /// <summary>
    /// Clear pipelines by attachment formats, sample count and what they write (<see cref="MakeClearPipeline"/>), made on first use.
    /// </summary>
    readonly Dictionary<(MTLPixelFormat Colour, MTLPixelFormat Extra, MTLPixelFormat Depth, ulong Samples, bool WritesColour, bool WritesDepth),
        MTLRenderPipelineState> _clearPipelines = [];
    /// <summary>
    /// The compiled clear shaders (<c>ClearMsl</c>), compiled on first use.
    /// </summary>
    MTLLibrary? _clearLibrary;

    /// <summary>
    /// A texture copied into another: depth by a native copy (same size, format and kind only), colour by a sampling pass, which
    /// can scale (the impostor bake's supersampled row halved).
    /// </summary>
    internal void Blit(MTLCommandBuffer command, GpuTexture src, GpuTexture dst, TextureMinFilter filter)
    {
        if (src.Desc.IsDepth || dst.Desc.IsDepth)
        {
            if (src.Desc.Format != dst.Desc.Format || src.Desc.Width != dst.Desc.Width || src.Desc.Height != dst.Desc.Height
                || src.Desc.Kind != TextureKind.Texture2D || dst.Desc.Kind != TextureKind.Texture2D
                || src.Desc.Samples != 1 || dst.Desc.Samples != 1)
            {
                throw new NotSupportedException("Metal depth blits require matching single-sample 2D textures of the same size and format");
            }

            // The sampling pass below writes a colour attachment; depth formats cannot be bound there.
            var blit = command.BlitCommandEncoder();
            blit.CopyFromTexture(TextureAt(src), 0, 0, new MTLOrigin(),
                new MTLSize { width = (ulong)src.Desc.Width, height = (ulong)src.Desc.Height, depth = 1 },
                TextureAt(dst), 0, 0, new MTLOrigin());
            blit.EndEncoding();
            return;
        }

        BlitScaled(command, TextureAt(src), TextureAt(dst), MetalConventions.Pixel(dst.Desc.Format), dst.Desc.Width, dst.Desc.Height, filter);
    }

    /// <summary>
    /// A texture's level over another level of the same texture (the mip chain's step): a blit when the two levels are the same
    /// size, a sampling pass over level views when they are not (Metal's blit cannot scale). All slices: a cube's six faces,
    /// an array's layers, per-slice 2D views for the scaled case.
    /// </summary>
    internal void BlitLevel(MTLCommandBuffer command, GpuTexture texture, int srcLevel, int dstLevel, TextureMinFilter filter)
    {
        var desc = texture.Desc;
        var image = TextureAt(texture);
        int srcWidth = Math.Max(desc.Width >> srcLevel, 1), srcHeight = Math.Max(desc.Height >> srcLevel, 1);
        int dstWidth = Math.Max(desc.Width >> dstLevel, 1), dstHeight = Math.Max(desc.Height >> dstLevel, 1);
        int layers = desc.Kind == TextureKind.Cube ? 6 * Math.Max(desc.Layers, 1) : desc.Layers;
        var format = MetalConventions.Pixel(desc.Format);

        if (srcWidth == dstWidth && srcHeight == dstHeight)
        {
            var blit = command.BlitCommandEncoder();

            for (uint layer = 0; layer < (uint)layers; layer++)
            {
                blit.CopyFromTexture(image, layer, (ulong)srcLevel, new MTLOrigin(),
                    new MTLSize { width = (ulong)srcWidth, height = (ulong)srcHeight, depth = 1 },
                    image, layer, (ulong)dstLevel, new MTLOrigin());
            }

            blit.EndEncoding();
            return;
        }

        var made = new List<MTLTexture>(layers * 2);

        for (uint layer = 0; layer < (uint)layers; layer++)
        {
            var source = image.NewTextureView(format, MTLTextureType.Type2D,
                new NSRange { location = (ulong)srcLevel, length = 1 }, new NSRange { location = layer, length = 1 });
            var target = image.NewTextureView(format, MTLTextureType.Type2D,
                new NSRange { location = (ulong)dstLevel, length = 1 }, new NSRange { location = layer, length = 1 });
            BlitScaled(command, source, target, format, dstWidth, dstHeight, filter);
            made.Add(source);
            made.Add(target);
        }

        RetireLevelViews(command, made);
    }

    /// <summary>
    /// Draws <paramref name="source"/> into <paramref name="target"/> with a full-screen sampling pass at the target's size and the filter.
    /// </summary>
    unsafe void BlitScaled(MTLCommandBuffer command, MTLTexture source, MTLTexture target, MTLPixelFormat format, int width, int height,
        TextureMinFilter filter)
    {
        if (!_blitPipelines.TryGetValue(format, out var pipeline))
            _blitPipelines[format] = pipeline = MakeBlitPipeline(format);

        var descriptor = new MTLRenderPassDescriptor(
            ObjectiveC.IntPtr_objc_msgSend(ObjectiveC.objc_getClass("MTLRenderPassDescriptor"), "renderPassDescriptor"));
        var attachments = descriptor.ColorAttachments;
        var attachment = attachments.Object(0);
        attachment.Texture = target;
        attachment.LoadAction = MTLLoadAction.DontCare;
        attachment.StoreAction = MTLStoreAction.Store;
        attachments.SetObject(attachment, 0);
        var encoder = command.RenderCommandEncoder(descriptor);
        encoder.SetRenderPipelineState(pipeline);
        encoder.SetFragmentTexture(source, 0);
        var mag = filter == TextureMinFilter.Linear ? TextureMagFilter.Linear : TextureMagFilter.Nearest;
        var sampler = Samplers.Backend(Samplers.Get(SamplerDesc.Clamped(filter, mag)));
        encoder.SetFragmentSamplerState(sampler, 0);
        var size = stackalloc float[2] { width, height };
        encoder.SetFragmentBytes((nint)size, 8, 1);
        encoder.DrawPrimitives(MTLPrimitiveType.Triangle, 0, 3);
        encoder.EndEncoding();
    }

    /// <summary>
    /// Clears the open pass's colour and / or depth over <paramref name="rect"/> (Vulkan's <c>vkCmdClearAttachments</c>): a fullscreen
    /// triangle under the rect's scissor, the colour and depth as the fragment's outputs. Metal's render passes clear whole attachments
    /// at their load only, so a partial clear (a shadow tile, a slice's depth) is a draw.
    /// </summary>
    internal unsafe void ClearAttachments(MTLRenderCommandEncoder encoder, in RenderingDesc desc, bool colour, ClearColour colourValue,
        bool depth, float depthValue, PixelRect rect)
    {
        var colourTexture = desc.Colour.IsNull ? default : ViewTexture(desc.Colour.View);
        var depthTexture = desc.Depth.IsNull ? default : ViewTexture(desc.Depth.View);
        colour &= colourTexture.NativePtr != IntPtr.Zero;
        depth &= depthTexture.NativePtr != IntPtr.Zero;
        if (!colour && !depth) return;

        var colourFormat = colourTexture.NativePtr != IntPtr.Zero ? colourTexture.PixelFormat : MTLPixelFormat.Invalid;
        var extraFormat = desc.Extra.IsNull ? MTLPixelFormat.Invalid : ViewTexture(desc.Extra.View).PixelFormat;
        var depthFormat = depthTexture.NativePtr != IntPtr.Zero ? depthTexture.PixelFormat : MTLPixelFormat.Invalid;
        ulong samples = Math.Max(1ul, colourTexture.NativePtr != IntPtr.Zero ? colourTexture.SampleCount : depthTexture.SampleCount);
        var key = (colourFormat, extraFormat, depthFormat, samples, colour, depth);
        if (!_clearPipelines.TryGetValue(key, out var pipeline))
            _clearPipelines[key] = pipeline = MakeClearPipeline(colourFormat, extraFormat, depthFormat, samples, colour, depth);

        encoder.SetRenderPipelineState(pipeline);
        if (depth) encoder.SetDepthStencilState(DepthState(true, true, DepthFunction.Always));
        else if (depthTexture.NativePtr != IntPtr.Zero)
            encoder.SetDepthStencilState(DepthState(false, false, DepthFunction.Always));

        encoder.SetViewport(new MTLViewport
        {
            originX = 0, originY = 0,
            width = colourTexture.NativePtr != IntPtr.Zero ? colourTexture.Width : depthTexture.Width,
            height = colourTexture.NativePtr != IntPtr.Zero ? colourTexture.Height : depthTexture.Height,
            znear = 0, zfar = 1,
        });
        encoder.SetScissorRect(new MTLScissorRect { x = (ulong)rect.X, y = (ulong)rect.Y, width = rect.Width, height = rect.Height });

        if (colour)
        {
            var value = stackalloc float[4] { colourValue.R, colourValue.G, colourValue.B, colourValue.A };
            encoder.SetFragmentBytes((nint)value, 16, 0);
        }

        if (depth)
        {
            float value = depthValue;
            encoder.SetFragmentBytes((nint)(&value), 4, 1);
        }

        encoder.DrawPrimitives(MTLPrimitiveType.Triangle, 0, 3);
    }

    /// <summary>
    /// A pipeline drawing a clear rectangle into the given attachment formats, writing colour, depth or both.
    /// </summary>
    MTLRenderPipelineState MakeClearPipeline(MTLPixelFormat colour, MTLPixelFormat extra, MTLPixelFormat depth, ulong samples, bool writesColour,
        bool writesDepth)
    {
        NSError error = default;
        var library = _clearLibrary ??= Device.NewLibrary((NSString)ClearMsl, new MTLCompileOptions(), ref error);
        if (error.NativePtr != IntPtr.Zero)
            throw new InvalidOperationException($"the clear pass's MSL did not compile: {error.LocalizedDescription}");

        var d = new MTLRenderPipelineDescriptor(new ObjectiveCClass(ObjectiveC.objc_getClass("MTLRenderPipelineDescriptor")).AllocInit());
        d.VertexFunction = library.NewFunction((NSString)"clear_vertex");
        d.FragmentFunction = library.NewFunction((NSString)(writesColour && writesDepth ? "clear_both_fragment"
            : writesDepth ? "clear_depth_fragment" : "clear_colour_fragment"));
        d.RasterSampleCount = samples;

        if (colour != MTLPixelFormat.Invalid)
        {
            // A depth-only clear still names the pass's colour attachment: the mask off, so the pass's picture is untouched.
            var attachment = d.ColorAttachments.Object(0);
            attachment.PixelFormat = colour;
            attachment.WriteMask = writesColour ? MTLColorWriteMask.All : MTLColorWriteMask.None;
            d.ColorAttachments.SetObject(attachment, 0);
        }

        if (extra != MTLPixelFormat.Invalid)
        {
            // The second colour target (the GI mask): the clear never writes it, but the pass's pipeline must name it.
            var attachment = d.ColorAttachments.Object(1);
            attachment.PixelFormat = extra;
            attachment.WriteMask = MTLColorWriteMask.None;
            d.ColorAttachments.SetObject(attachment, 1);
        }

        if (depth != MTLPixelFormat.Invalid)
        {
            d.DepthAttachmentPixelFormat = depth;
            if (depth is MTLPixelFormat.Depth32FloatStencil8) d.StencilAttachmentPixelFormat = depth;
        }

        var pipeline = Device.NewRenderPipelineState(d, ref error);
        if (error.NativePtr != IntPtr.Zero)
            throw new InvalidOperationException($"the clear pass's pipeline failed: {error.LocalizedDescription}");

        return pipeline;
    }

    /// <summary>
    /// A pipeline that copies a texture into a target of the format by sampling (<c>BlitMsl</c>).
    /// </summary>
    MTLRenderPipelineState MakeBlitPipeline(MTLPixelFormat format)
    {
        NSError error = default;
        var library = Device.NewLibrary((NSString)BlitMsl, new MTLCompileOptions(), ref error);
        if (error.NativePtr != IntPtr.Zero)
            throw new InvalidOperationException($"the blit pass's MSL did not compile: {error.LocalizedDescription}");

        var d = new MTLRenderPipelineDescriptor(new ObjectiveCClass(ObjectiveC.objc_getClass("MTLRenderPipelineDescriptor")).AllocInit());
        d.VertexFunction = library.NewFunction((NSString)"blit_vertex");
        d.FragmentFunction = library.NewFunction((NSString)"blit_fragment");
        var attachment = d.ColorAttachments.Object(0);
        attachment.PixelFormat = format;
        d.ColorAttachments.SetObject(attachment, 0);
        var pipeline = Device.NewRenderPipelineState(d, ref error);
        if (error.NativePtr != IntPtr.Zero)
            throw new InvalidOperationException($"the blit pass's pipeline failed: {error.LocalizedDescription}");

        return pipeline;
    }

    #endregion

    #region Readback and teardown

    /// <summary>
    /// The live entries in the three resource tables (the deterministic leak tests' count; the table indices themselves are never reused).
    /// </summary>
    internal (int Textures, int Views, int Buffers) LiveResourceCounts()
    {
        int tex = 0, liveViews = 0, liveBuffers = 0;
        foreach (var t in _textures) if (t is not null) tex++;
        foreach (var v in _views) if (v is not null) liveViews++;
        foreach (var b in _buffers) if (b is not null) liveBuffers++;

        return (tex, liveViews, liveBuffers);
    }

    /// <summary>
    /// A texture's level 0 as raw bytes, read back through a blit. <paramref name="texelBytes"/> is the engine format's
    /// texel size (RGBA8 = 4, R8 = 1, R16 = 2); rows are returned tightly packed even when the Metal blit needs a
    /// 4-byte-aligned source row. The display and the upload tests both use this.
    /// </summary>
    internal byte[] Readback(GpuTexture texture, int texelBytes, int level = 0, int layer = 0)
    {
        var desc = _textures[texture.Index]!.Desc;
        int layers = desc.Kind == TextureKind.Cube ? 6 * Math.Max(desc.Layers, 1) : desc.Layers;
        if (level < 0 || level >= desc.Levels) throw new ArgumentOutOfRangeException(nameof(level));
        if (layer < 0 || layer >= layers) throw new ArgumentOutOfRangeException(nameof(layer));

        int width = Math.Max(desc.Width >> level, 1), height = Math.Max(desc.Height >> level, 1);
        int rowBytes = checked(width * texelBytes);
        int stride = (rowBytes + 3) & ~3;
        var command = Queue.CommandBuffer();
        var blit = command.BlitCommandEncoder();
        var buffer = Device.NewBuffer((ulong)(stride * height), MTLResourceOptions.ResourceStorageModeShared);
        blit.CopyFromTexture(TextureAt(texture), (ulong)layer, (ulong)level, new MTLOrigin(),
            new MTLSize { width = (ulong)width, height = (ulong)height, depth = 1 }, buffer, 0, (ulong)stride, (ulong)(stride * height));
        blit.EndEncoding();
        command.Commit();
        command.WaitUntilCompleted();
        var pixels = new byte[rowBytes * height];
        for (int y = 0; y < height; y++)
            Marshal.Copy(IntPtr.Add(buffer.Contents, y * stride), pixels, y * rowBytes, rowBytes);

        buffer.Dispose();
        return pixels;
    }

    /// <summary>
    /// Waits for the queue's earlier command buffers (the deterministic tests' readback barrier).
    /// </summary>
    internal void WaitForGpu()
    {
        var marker = Queue.CommandBuffer();
        marker.Commit();
        marker.WaitUntilCompleted();
    }

    /// <summary>
    /// The contents of a shared buffer after the queue's earlier commands have completed (test/diagnostic helper).
    /// </summary>
    internal byte[] ReadBuffer(GpuBuffer buffer)
    {
        WaitForGpu();
        var of = BufferAt(buffer);
        var bytes = new byte[of.Length];
        if (bytes.Length > 0) Marshal.Copy(of.Contents, bytes, 0, bytes.Length);

        return bytes;
    }

    /// <summary>
    /// A texture's level 0 as RGBA bytes, read back through a blit (a texture is not CPU-mappable, even in shared storage): the
    /// display's and tests' picture check. Rows in the texture's own order.
    /// </summary>
    public byte[] Readback(GpuTexture texture) => Readback(texture, 4);

    /// <summary>
    /// Closes the frame's autorelease pool and releases the blit and clear pipelines, every retired and deferred resource, the bindless table,
    /// the stand-in textures and the buffers.
    /// </summary>
    public void Dispose()
    {
        if (_framePoolOpen)
        {
            _framePool.Dispose();
            _framePoolOpen = false;
        }

        foreach (var p in _blitPipelines.Values) p.Dispose();

        _blitPipelines.Clear();
        foreach (var p in _clearPipelines.Values) p.Dispose();

        _clearPipelines.Clear();
        _clearLibrary?.Dispose();
        ReleaseRetired(force: true);
        ReleaseRetiredLevelViews(force: true);
        ReleaseRetiredUploads(force: true);
        ReleaseDeferred(force: true);
        _bindless.Dispose();
        foreach (var t in _dummyTextures) t.Dispose();

        _dummyTextures.Clear();
        foreach (var b in _buffers) b?.Release();

        _buffers.Clear();
    }

    #endregion
}
