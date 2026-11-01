using Meitou.Rendering.Gpu.Shaders;
namespace Meitou.Rendering.Gpu;

/// <summary>
/// The seam between the renderers and a graphics API (docs/resource-layer.md): what the renderers
/// may ask of a backend. The Vulkan context implements it as it is; the Metal one grows a pass at
/// a time, and an operation it does not have yet throws.
/// </summary>
public interface IGpuContext
{
    // ---- the resources ----

    /// <summary>A new texture: the backend makes its own image and its own layout rules.</summary>
    GpuTexture CreateTexture(TextureDesc desc);

    /// <summary>
    /// A new texture made on any thread, with nothing recorded: it is not used before <see cref="IGpuCommandList.InitTexture"/> records its
    /// layout on the render thread. Null where the backend has no such path (the caller makes it with <see cref="IGpuCommandList.CreateTexture"/>).
    /// </summary>
    GpuTexture? CreateTextureOffThread(TextureDesc desc) => null;

    /// <summary>
    /// A view of a texture's level and layer range: the backend cuts it (or its own equivalent) and
    /// keeps it.
    /// </summary>
    GpuView View(GpuTexture texture, int baseLevel, int levels, int baseLayer, int layers, GpuViewType type, GpuSwizzle swizzle);

    /// <summary>Releases a texture the backend made (and the views cut on it).</summary>
    void ReleaseTexture(GpuTexture texture);

    /// <summary>A new device-local buffer of <paramref name="size"/> bytes.</summary>
    GpuBuffer CreateBuffer(ulong size, BufferUse use, string name);

    /// <summary>Releases a device-local buffer the backend made.</summary>
    void ReleaseBuffer(GpuBuffer buffer);

    // ---- the services ----

    /// <summary>The frame being recorded (its number, its resource states, its command list).</summary>
    IGpuFrame Frame { get; }

    /// <summary>The pipelines by description (the engine's own).</summary>
    IGpuPipelines Pipelines { get; }

    /// <summary>The programs the engine's shaders compile to.</summary>
    IGpuShaders Shaders { get; }

    /// <summary>The samplers by description (the backend's objects; the renderers hold the handles).</summary>
    IGpuSamplers Samplers { get; }

    /// <summary>The uploads (the staging copies into the textures and the buffers).</summary>
    IGpuUploads Uploads { get; }

    /// <summary>The stand-in a sampler reads when nothing is bound (GL's default texture, by the sampler's declaration).</summary>
    SampledTexture Dummy(SamplerInfo sampler);

    /// <summary>The stand-in's index in the bindless 2D float array (what a 2D sampler with nothing bound reads, <see cref="Dummy"/>), registered once.</summary>
    uint StandIn2D { get; }

    /// <summary>The generic attribute value a disabled vertex input reads (GL's constant (0, 0, 0, 1), bound with stride 0).</summary>
    GpuBuffer DummyVertex { get; }

    /// <summary>Whether a disabled attribute may take GL's stride-0 constant binding (Metal's validator refuses it).</summary>
    bool ConstantVertexAttributes { get; }

    /// <summary>
    /// Whether a texture written by a render pass has its row 0 at the bottom (Vulkan's backbuffer and targets) rather than
    /// at the top (Metal's). UI images made by the renderer (portraits, icons, the editor stage) flip their V coordinate on
    /// a bottom-up target (<c>ImageFlipV</c>), but must be drawn as stored on Metal.
    /// </summary>
    bool RenderTargetsBottomUp { get; }

    /// <summary>The buffer a disabled attribute of <paramref name="kind"/> reads where a stride-0 binding is impossible (Metal: the value repeated at every 16-byte stride).</summary>
    GpuBuffer DummyVertexArray(ScalarKind kind);

    /// <summary>Frame-global textures, blocks and uniform values by GLSL name (docs/renderer-native.md 4.3).</summary>
    FrameGlobals Globals { get; }

    /// <summary>The bindless table (the arrays of textures the shaders index by number).</summary>
    IGpuBindlessTable Bindless { get; }

    /// <summary>The device limit a uniform buffer slice's offset must align to (the default blocks' slices).</summary>
    ulong UniformBufferAlign { get; }

    /// <summary>The device traces ray queries (an optional buffer use, never required).</summary>
    bool HasRayQuery { get; }

    /// <summary>The device has tessellation shaders (the ground relief and the smoothed rocks use them where they do).</summary>
    bool HasTessellation { get; }

    /// <summary>The largest multisample count a colour and depth target may use together (1: no MSAA).</summary>
    int MaxSampleCount { get; }

    /// <summary>A frame is open for recording (the upload paths write into it; Metal's batches need nothing).</summary>
    void EnsureFrame();

    /// <summary>The frames in flight (the slots a resource freed now may still be used by).</summary>
    int FramesInFlight { get; }

    /// <summary>The device's video memory budget in bytes (the impostor and streaming budgets).</summary>
    ulong VideoMemoryBudget { get; }

    /// <summary>Runs <paramref name="release"/> once the frames in flight that may use the resource have finished.</summary>
    void DeferDelete(Action release);

    /// <summary>Submits what is recorded and waits for the GPU (readbacks); a frame is open again afterwards when one was before.</summary>
    void Finish();

    /// <summary>A host-readable buffer the GPU writes into (the bakes' and the culls' readbacks).</summary>
    IGpuReadback CreateReadback(ulong size, string name);

    /// <summary>Whether frame <paramref name="frame"/> (a <see cref="IGpuFrame.Number"/>) has completed on the GPU.</summary>
    bool FrameCompleted(long frame);

    /// <summary>
    /// A set that lives as long as the context, its slots written once from the bindings (the layout says what each slot holds and
    /// whether its offset comes at bind time — then the binding's offset is ignored and the bind's dynamic offset is used).
    /// </summary>
    GpuSet CreateSet(GpuSetLayout layout, ReadOnlySpan<GpuBinding> bindings);

    // ---- the host's recording model ----

    /// <summary>
    /// A guest's segment in the open host pass: draws only (no rendering, barriers, copies or dispatches), with the host's targets and
    /// state (<see cref="CurrentTargets"/>, <see cref="CurrentState"/>). Inside a rendering with secondaries it is a secondary of its own,
    /// executed in its place. Without a host pass it throws. Closed by <see cref="EndGuest"/>.
    /// </summary>
    IGpuCommandList BeginGuest(string label);

    /// <summary>
    /// The caller has begun a rendering instance on <paramref name="cmd"/> (inside its own native segment) into
    /// <paramref name="targets"/>: until <see cref="EndHostPass"/> guests draw into it with <paramref name="state"/>.
    /// </summary>
    void BeginHostPass(IGpuCommandList cmd, PassTargets targets, DrawState state);

    /// <summary>
    /// As <see cref="BeginHostPass"/>, for a host whose rendering may record its guests on worker threads (Vulkan's secondaries):
    /// <paramref name="stage"/> is the <see cref="StageClock"/> stage its jobs count towards. A backend without secondaries ignores it.
    /// </summary>
    void BeginHostPassParallel(IGpuCommandList cmd, PassTargets targets, DrawState state, int stage);

    /// <summary>The viewport and scissor (null: the whole viewport) the guests draw with from now on (a cascade's tile).</summary>
    void SetPassViewport(PixelViewport viewport, PixelRect? scissor = null);

    /// <summary>The <see cref="StageClock"/> stage the open host pass's guests count towards from now on (no-op where guests record inline).</summary>
    void SetPassStage(int stage);

    /// <summary>Clears an area of the open host pass's depth attachment in its place among the guests.</summary>
    void ClearDepth(IGpuCommandList host, float value, PixelRect rect);

    /// <summary>
    /// Ends a parallel host pass's queued guests (Vulkan's secondaries): call before the rendering ends. Inline backends do nothing.
    /// </summary>
    void EndHostPassParallel(IGpuCommandList cmd);

    /// <summary>Closes the host's pass (a guest's draws end with it).</summary>
    void EndHostPass(IGpuCommandList cmd);

    /// <summary>The device clamps depth instead of clipping it (the shadow casters' state).</summary>
    bool DepthClamp { get; }

    /// <summary>
    /// Whether window depth equals clip depth (0..1, Metal) rather than the −1..1 clip-depth mapping the Vulkan path uses. A
    /// shader that recovers a cascade matrix's depth from <c>gl_FragCoord.z</c> must undo the mapping: 2·z−1 on Vulkan (its
    /// depth-clip-control path and the shader remap fallback both expose that window mapping), z itself on Metal.
    /// </summary>
    bool ClipDepthZeroToOne { get; }

    /// <summary>Hosts may open their rendering with secondaries (Vulkan's parallel recording; Metal records one encoder).</summary>
    bool RecordsSecondaries { get; }

    /// <summary>Closes a guest segment (<see cref="BeginGuest"/>).</summary>
    void EndGuest(IGpuCommandList cmd);

    /// <summary>
    /// A native segment of its own outside any pass (a full barrier before and after; nothing is assumed bound): copies, dispatches and
    /// the renderers' own renderings. Closed by <see cref="EndNative"/>.
    /// </summary>
    IGpuCommandList BeginNative(string label);

    /// <summary>Closes a native segment (<see cref="BeginNative"/>).</summary>
    void EndNative(IGpuCommandList cmd);

    /// <summary>Whether a host pass or a native segment is open (the guests check it before they draw).</summary>
    bool PassOpen { get; }

    /// <summary>What the open pass draws into (its host's targets, viewport and scissor); throws when no pass is open.</summary>
    PassTargets CurrentTargets();

    /// <summary>The state the open pass's host hands its guests; throws when no pass is open.</summary>
    DrawState CurrentState();

    /// <summary>Records something that does not disturb a pass (a timestamp, a label) where the frame's commands go now.</summary>
    void Interleave(Action<IGpuCommandList> record);

    /// <summary>
    /// Records a prepared guest segment into the pass being drawn now (docs/renderer-native.md 6.2): into the host's open pass as a guest
    /// segment, or (the backend's own choosing) queued and recorded elsewhere; the job goes back to its pool afterwards.
    /// </summary>
    void Record(string label, RecordJob job);

    /// <summary>
    /// A set with one uniform buffer in it, whose offset into the buffer is given when the set is bound (the constants block: the engine's
    /// per-frame constants at a chosen offset).
    /// </summary>
    GpuSet AllocateConstantSet(GpuSetLayout layout, GpuBuffer buffer, ulong size);

    /// <summary>Whether the device is an integrated GPU (its budgets and star layers differ).</summary>
    bool IsIntegrated { get; }

    /// <summary>The upscaler's texture LOD bias: added to the mip level of every mipmapped fetch.</summary>
    float LodBias { get; set; }
}
