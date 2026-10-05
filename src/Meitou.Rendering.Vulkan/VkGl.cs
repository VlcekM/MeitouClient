using Meitou.Rendering.Gpu;
using Meitou.Rendering.Vulkan.Core;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;
using VkSemaphore = Silk.NET.Vulkan.Semaphore;

namespace Meitou.Rendering.Vulkan;

/// <summary>
/// <see cref="IGl"/> on Vulkan 1.3: the renderers' GL calls translated as they come (docs/engine.md "Backend interface").
/// <list type="bullet">
/// <item>GL objects are ids into tables of Vulkan resources; state (bindings, enables, the bound program's uniforms) is tracked
/// as GL defines it and turned into Vulkan objects at the draw: a pipeline per state combination (cached), the program's
/// resources pushed as one descriptor set (VK_KHR_push_descriptor), loose uniforms copied into a per-frame ring.</item>
/// <item>Coordinates: nothing is flipped. Vulkan's framebuffer row 0 is where GL's row 0 is (NDC y = -1), so render targets,
/// <c>gl_FragCoord</c> and texture coordinates mean the same in both; GL's counter-clockwise front faces are clockwise in
/// Vulkan's framebuffer space; clip depth stays −1..1 (VK_EXT_depth_clip_control, else a remap compiled into the vertex shaders).
/// The picture is flipped once, when it goes to the window.</item>
/// <item>Every image lives in <c>GENERAL</c> layout; a full memory barrier before each render pass and after the uploads orders
/// attachment writes, uploads and sampling (no per-image tracking).</item>
/// <item>Uploads (buffer and texture data) are recorded into a separate command buffer submitted before the frame's own, so they
/// precede the frame's draws. A buffer the current frame has already drawn from is renamed (fresh memory) instead of written,
/// so earlier draws keep the old data; textures are written in place (a strip rewritten mid-frame shows for the whole frame).
/// Stream/dynamic buffers live in host-visible per-frame memory and are written directly.</item>
/// </list>
/// Not in this model: bindless descriptors and multithreaded command recording (DECISIONS.md 7).
/// </summary>
public sealed unsafe partial class VkGl : IGl, ITextureLodBias, IDisposable
{
    readonly VulkanDevice device;
    readonly Vk vk;
    readonly Device dev;
    readonly Dictionary<uint, GlBufferObj> buffers = [];
    readonly Dictionary<uint, GlTextureObj> textures = [];
    readonly Dictionary<uint, GlVertexArray> vertexArrays = [];
    readonly Dictionary<uint, GlShaderObj> shaders = [];
    readonly Dictionary<uint, GlProgramObj> programs = [];
    readonly Dictionary<uint, GlFramebufferObj> framebuffers = [];
    readonly Dictionary<uint, GlRenderbufferObj> renderbuffers = [];
    readonly Dictionary<uint, GlQueryObj> queries = [];
    uint nextId = 1;

    CommandBuffer cmd, uploadCmd;
    bool frameOpen;
    readonly CommandPool[] uploadPools;
    readonly CommandBuffer[] uploadBuffers;

    /// <summary>The host's backbuffer (framebuffer 0): what the window shows, flipped, at <see cref="Present"/>.</summary>
    GlTextureObj? backbuffer;
    GlTextureObj? backbufferDepth;

    public VkGl(VulkanDevice device)
    {
        this.device = device;
        vk = device.Vk;
        dev = device.Device;
        int n = device.Frames.Count;
        uploadPools = new CommandPool[n];
        uploadBuffers = new CommandBuffer[n];
        for (int i = 0; i < n; i++)
        {
            var poolInfo = new CommandPoolCreateInfo { SType = StructureType.CommandPoolCreateInfo, QueueFamilyIndex = device.GraphicsFamily, Flags = CommandPoolCreateFlags.TransientBit };
            Check(vk.CreateCommandPool(dev, &poolInfo, null, out uploadPools[i]));
            var alloc = new CommandBufferAllocateInfo { SType = StructureType.CommandBufferAllocateInfo, CommandPool = uploadPools[i], Level = CommandBufferLevel.Primary, CommandBufferCount = 1 };
            CommandBuffer cb;
            Check(vk.AllocateCommandBuffers(dev, &alloc, &cb));
            uploadBuffers[i] = cb;
        }
        uniformRings = new FrameRings[n];
        for (int i = 0; i < n; i++) uniformRings[i] = new FrameRings(this);
        rings = new FrameRings[n];
        for (int i = 0; i < n; i++) rings[i] = new FrameRings(this);
        Context = new GpuContext(device);
        Context.Interop = this;
        InitState();
        InitDummies();
    }

    /// <summary>The native renderer API on the same device (docs/renderer-native.md); its frame is driven by <see cref="BeginFrame"/> and
    /// <see cref="EndFrame"/>, and records into this frame's command buffer.</summary>
    public GpuContext Context { get; }

    /// <summary>The Vulkan device under the translation.</summary>
    public VulkanDevice Device => device;

    /// <summary>Counters for the frame (draws, pipelines created, render passes, renamed buffers), reset by <see cref="BeginFrame"/>.</summary>
    public VkGlStats Stats { get; } = new();
    /// <summary>Added to the mip level of every mipmapped fetch (ITextureLodBias; set by the post-processing chain for an upscaler).</summary>
    public float TextureLodBias { get; set; }

    internal static void Check(Result r)
    {
        if (r != Result.Success) throw new InvalidOperationException($"Vulkan call failed: {r}");
    }

    uint NewId() => nextId++;

    // ----- Frames -----

    /// <summary>Starts a frame whose framebuffer 0 is <paramref name="width"/> × <paramref name="height"/> (RGBA8 + depth).</summary>
    public void BeginFrame(int width, int height)
    {
        GuardNative();
        if (frameOpen)
        {
            // Opened lazily (uploads before the host began the frame): keep it if the size matches, else submit it and start afresh.
            if (backbuffer is { } bb && bb.Width == Math.Max(width, 1) && bb.Height == Math.Max(height, 1)) return;
            EndFrame();
        }
        EnsureBackbuffer(width, height);
        cmd = device.Frames.BeginFrame();
        int slot = device.Frames.Slot;
        Check(vk.ResetCommandPool(dev, uploadPools[slot], 0));
        uploadCmd = uploadBuffers[slot];
        var begin = new CommandBufferBeginInfo { SType = StructureType.CommandBufferBeginInfo, Flags = CommandBufferUsageFlags.OneTimeSubmitBit };
        Check(vk.BeginCommandBuffer(uploadCmd, &begin));
        // Uploads may overwrite what earlier frames still read (in-place texture strips): wait for everything before.
        FullBarrier(uploadCmd);
        CarryDynamicBuffers(slot, rings[slot].Reset);
        uniformRings[slot].Reset();
        ResetFramePools(slot);
        RecycleQueries(slot);
        TimeFrame(start: true);
        frameOpen = true;
        Stats.BeginFrame();
        ResetFrameState();
        Context.Frame.Begin(cmd, uploadCmd);
    }

    /// <summary>Ends the frame and submits it (uploads first). <paramref name="signal"/>/<paramref name="wait"/>: swapchain semaphores.</summary>
    public void EndFrame(ReadOnlySpan<VkSemaphore> wait = default, ReadOnlySpan<PipelineStageFlags> waitStages = default, ReadOnlySpan<VkSemaphore> signal = default)
    {
        GuardNative();
        if (!frameOpen) return;
        EndPass();
        Context.Frame.End();
        TimeFrame(start: false);
        FullBarrier(uploadCmd);
        Check(vk.EndCommandBuffer(uploadCmd));
        device.Frames.EndFrame(wait, waitStages, signal, before: uploadCmd);
        frameOpen = false;
        PollQueries();
    }

    /// <summary>Records <paramref name="record"/> into the frame's command buffer outside any render pass, after everything recorded so far
    /// (presenting, upscalers). Images are in GENERAL layout.</summary>
    /// <summary>Starts recording outside the GL calls in the frame (the open render pass ended, a full barrier); <see cref="EndExternal"/> follows. For callers that hand the command buffer to native code.</summary>
    public CommandBuffer BeginExternal()
    {
        EndPass();
        FullBarrier(Cmd);
        return Cmd;
    }

    /// <summary>Ends what <see cref="BeginExternal"/> started (a full barrier).</summary>
    public void EndExternal() => FullBarrier(Cmd);

    public void RecordInFrame(Action<CommandBuffer> record)
    {
        EndPass();
        var cb = Cmd;
        FullBarrier(cb);
        record(cb);
        FullBarrier(cb);
    }

    /// <summary>Submits what is recorded and waits for it (readbacks, <c>glFinish</c>, waiting query results); the frame goes on after.</summary>
    void Flush()
    {
        GuardNative();
        if (!frameOpen) { device.Frames.WaitAll(); return; }
        var (w, h) = (backbuffer!.Width, backbuffer.Height);
        EndFrame();
        device.Frames.WaitAll();
        BeginFrame(w, h);
    }

    /// <summary>Records into the frame, opening one at the backbuffer's size if the host has not (offscreen tools).</summary>
    CommandBuffer Cmd
    {
        get
        {
            GuardNative();
            if (!frameOpen) BeginFrame(backbuffer?.Width ?? 1, backbuffer?.Height ?? 1);
            return cmd;
        }
    }

    CommandBuffer UploadCmd
    {
        get
        {
            GuardNative();
            if (!frameOpen) BeginFrame(backbuffer?.Width ?? 1, backbuffer?.Height ?? 1);
            return uploadCmd;
        }
    }

    void FullBarrier(CommandBuffer cb)
    {
        var barrier = new MemoryBarrier2
        {
            SType = StructureType.MemoryBarrier2,
            SrcStageMask = PipelineStageFlags2.AllCommandsBit,
            SrcAccessMask = AccessFlags2.MemoryWriteBit,
            DstStageMask = PipelineStageFlags2.AllCommandsBit,
            DstAccessMask = AccessFlags2.MemoryReadBit | AccessFlags2.MemoryWriteBit,
        };
        var info = new DependencyInfo { SType = StructureType.DependencyInfo, MemoryBarrierCount = 1, PMemoryBarriers = &barrier };
        vk.CmdPipelineBarrier2(cb, &info);
    }

    void EnsureBackbuffer(int width, int height)
    {
        width = Math.Max(width, 1);
        height = Math.Max(height, 1);
        if (backbuffer is { } b && b.Width == width && b.Height == height) return;
        if (backbuffer is not null) { DestroyTexture(backbuffer); DestroyTexture(backbufferDepth!); }
        backbuffer = NewRenderTexture(Format.R8G8B8A8Unorm, width, height, 1, "backbuffer");
        backbufferDepth = NewRenderTexture(Format.D32Sfloat, width, height, 1, "backbuffer depth");
    }

    /// <summary>The backbuffer image (framebuffer 0's colour), for presenting and screenshots.</summary>
    public (Image Image, int Width, int Height) Backbuffer => (backbuffer!.Image!.Image, backbuffer.Width, backbuffer.Height);

    public void Dispose()
    {
        device.WaitIdle();
        foreach (var t in textures.Values.ToList()) DestroyTexture(t);
        foreach (var r in renderbuffers.Values) if (r.Texture is not null) DestroyTexture(r.Texture);
        if (backbuffer is not null) { DestroyTexture(backbuffer); DestroyTexture(backbufferDepth!); }
        foreach (var b in buffers.Values.ToList()) DestroyBuffer(b);
        foreach (var p in programs.Values.ToList()) DestroyProgram(p);
        foreach (var q in queries.Values) q.Dispose(this);
        DestroyDummies();
        DestroyPipelines();
        DestroySamplers();
        foreach (var r in rings) r.Dispose();
        foreach (var r in uniformRings) r.Dispose();
        for (int i = 0; i < uploadPools.Length; i++) vk.DestroyCommandPool(dev, uploadPools[i], null);
        Context.Dispose();
        device.Frames.WaitAll();
    }
}

/// <summary>Per-frame counters of the translation.</summary>
public sealed class VkGlStats
{
    public int Draws, RenderPasses, PipelinesCreated, BuffersRenamed, Uploads, Flushes;
    public long UploadBytes;
    public int DescriptorPushes;
    /// <summary>The last completed frame's GPU time, start of its uploads to the end of its last pass (not reset per frame).</summary>
    public double GpuFrameMs;
    public long DrawTicks;   // Stopwatch ticks spent preparing draws (pipelines, state, descriptors, vertex buffers)
    public long UniformBytes;
    internal void BeginFrame() { Draws = RenderPasses = PipelinesCreated = BuffersRenamed = Uploads = Flushes = DescriptorPushes = 0; UploadBytes = UniformBytes = DrawTicks = 0; }
    public override string ToString() => $"{Draws} draws, {RenderPasses} passes, {PipelinesCreated} new pipelines, {Uploads} uploads ({UploadBytes / 1024} KB), {BuffersRenamed} renames, {UniformBytes / 1024} KB uniforms, {Flushes} flushes, {DescriptorPushes} pushes, draw prep {DrawTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency:0.00} ms, gpu frame {GpuFrameMs:0.00} ms";
}
