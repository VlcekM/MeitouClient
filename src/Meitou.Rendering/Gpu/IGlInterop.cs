using Meitou.Rendering.Vulkan.Shaders;
using Silk.NET.Vulkan;

namespace Meitou.Rendering.Gpu;

/// <summary>A GL vertex array as VkGl would feed it (<see cref="IGlInterop.VertexArray"/>): per location the attribute (null when disabled)
/// and the element buffer (null binding when none).</summary>
public sealed record VertexArrayBindings(LegacyProgram.Attribute?[] Attributes, BufferBinding Elements);

/// <summary>
/// The coexistence seam (docs/renderer-native.md 4.2). Implemented by VkGl; renderers reach it as <see cref="GpuContext.Interop"/> while VkGl
/// exists (null afterwards). Native segments record into VkGl's command buffer of the same frame, in call order.
/// </summary>
public interface IGlInterop
{
    GpuContext Context { get; }

    /// <summary>(Phase 8 stage 3.) The context began a frame in <paramref name="slot"/> (<see cref="GpuContext.BeginFrame"/>): VkGl resets its
    /// per-frame state (rings, descriptor pools, queries, caches).</summary>
    void FrameBegun(int slot);

    /// <summary>The context is about to end the frame: VkGl ends its open pass.</summary>
    void FrameEnding();

    /// <summary>A full barrier was recorded (VkGl's counter).</summary>
    void CountBarrier();

    /// <summary>The context opens a native segment on <paramref name="list"/> (<see cref="GpuContext.BeginNative"/>): VkGl ends its pass, and
    /// until <see cref="SegmentClosed"/> IGl calls that record or flush throw.</summary>
    void SegmentOpening(CommandList list);

    /// <summary>The segment is closed (its closing barrier recorded): VkGl assumes nothing bound and no pass open.</summary>
    void SegmentClosed(CommandList list);

    /// <summary>
    /// Ends VkGl's open pass, places a full barrier and returns the frame's command list (invalidated: nothing is assumed bound). Until
    /// <see cref="EndNative"/>, IGl calls that record or flush throw: they could end the frame under the native code (VkGl.Flush).
    /// </summary>
    CommandList BeginNative(string label);

    /// <summary>
    /// A native segment that draws into the pass VkGl would draw into now (<see cref="CurrentTargets"/>), inside VkGl's own rendering instance:
    /// the pass is kept open (or begun as a VkGl draw would begin it), and stays open after <see cref="EndNative"/>, so neither side ends it and
    /// no barrier is placed (draws in one rendering instance are ordered). The native code records draws only: no BeginRendering,
    /// EndRendering, barriers, copies or dispatches. Same guard as <see cref="BeginNative"/>.
    /// </summary>
    CommandList BeginNativeInPass(string label);

    /// <summary>
    /// (Added for the native hosts, wave 3 step H.) Announces that the caller, inside its own <see cref="BeginNative"/> segment, has begun a rendering
    /// instance of its own on <paramref name="cmd"/> (<see cref="CommandList.BeginRendering"/>, into the targets of the GL framebuffer it keeps bound
    /// with the GL viewport, scissor and fixed-function state its guests expect). Until <see cref="EndHostPass"/> that rendering is "the pass": a guest's
    /// <see cref="BeginNativeInPass"/> returns <paramref name="cmd"/> without touching any pass, and <see cref="CurrentTargets"/>,
    /// <see cref="CurrentState"/> and <c>GetInteger(Samples)</c> answer as they do for VkGl's own pass (they read the GL state). Uploads and timestamps
    /// are still allowed through IGl (they go into the upload command buffer or write a timestamp); anything that would touch a pass (a clear, a draw, a blit, a
    /// flush) still throws. The host clears with load ops and <see cref="CommandList.ClearDepth"/> inside its rendering.
    /// </summary>
    void BeginHostPass(CommandList cmd);

    /// <summary>Ends the announcement; call after <see cref="CommandList.EndRendering"/> and before <see cref="EndNative"/> of the host's own segment.</summary>
    void EndHostPass(CommandList cmd);

    /// <summary>Every VkGl cache of command-buffer state is invalidated (pipeline, dynamic state, descriptors). After <see cref="BeginNative"/>
    /// a full barrier is placed and VkGl's pass is forgotten; after <see cref="BeginNativeInPass"/> the pass stays open.</summary>
    void EndNative(CommandList cmd);

    /// <summary>Records something that does not disturb a render pass (a timestamp, a debug label) into the frame without ending VkGl's pass.</summary>
    void Interleave(Action<CommandList> record);

    /// <summary>The bound draw framebuffer's attachments and GL's viewport and scissor (the GL binding is the truth while VkGl code remains).</summary>
    PassTargets CurrentTargets();

    /// <summary>GL's fixed-function state as VkGl would apply it to a draw into <see cref="CurrentTargets"/> now.</summary>
    DrawState CurrentState();

    /// <summary>
    /// A GL vertex array's attributes and element buffer, marked used by this frame (as a VkGl draw with it would). While neither the vertex
    /// array nor the storage of a buffer it names has changed, the same object is returned (no allocation): a caller may keep what it
    /// derived from it (vertex layout, pipeline, bindings) for as long as the result is <see cref="object.ReferenceEquals"/> to the last one.
    /// </summary>
    VertexArrayBindings VertexArray(uint glVertexArray);

    /// <summary>
    /// (Added for wave 3b, the per-draw export cost.) Moves whenever a <see cref="VertexArray"/> export could have become stale (an exported
    /// vertex array changed or was deleted; the storage of a buffer an export names changed: re-specified, renamed, deleted). While it equals
    /// the value read right after a <see cref="VertexArray"/> call for a vertex array, that export is current and its buffers are already
    /// marked used by this frame, so the call may be skipped: keep the stamp with what was derived from the export and compare one number
    /// per draw. It does not move for buffers no export names (other renderers' per-draw uniform buffers), and not at frame begin (since the
    /// step-O hot-path work, docs/renderer-native.md 7.5): every buffer an export has named counts as used by each new frame, so an export
    /// stays current across frames until one of the changes above.
    /// </summary>
    long VertexArrayStamp { get; }

    /// <summary>The image behind a GL texture (non-owning; valid while the GL texture keeps its storage).</summary>
    Texture Texture(uint glTexture);

    /// <summary>The sampler and view VkGl itself would bind for this texture now (its own cache: GL parameters, defined levels, base and
    /// max level, swizzle, the LOD bias). 0 gives the stand-in VkGl binds for a sampler of <paramref name="sampler"/>'s type.</summary>
    SampledTexture Sampled(uint glTexture, SamplerInfo sampler);

    /// <summary>As <see cref="Sampled(uint, SamplerInfo)"/> for a plain 2D sampler, or a shadow sampler.</summary>
    SampledTexture Sampled(uint glTexture, bool shadowSampler);

    /// <summary>
    /// The bindless entry of a GL texture with the sampler and view VkGl would bind now (<see cref="Sampled(uint, bool)"/>), registered in
    /// the array its format selects (<see cref="BindlessTable.KindFor"/>). Usable in the current frame. While the texture's view, sampler
    /// and the LOD bias are unchanged the same handle is returned; after a change (mip streaming, a parameter, the upscaler's bias) a new
    /// index is registered and the old one freed after the frames in flight, so a draw recorded earlier keeps what it was given, as in GL.
    /// Look it up per segment or per draw, not once per texture. 0 or a texture without storage gives the stand-in's entry. Freed with the
    /// texture's storage.
    /// </summary>
    BindlessHandle Bindless(uint glTexture, bool shadowSampler = false);

    /// <summary>The GL texture bound to <paramref name="unit"/> for <paramref name="sampler"/>'s type, as VkGl would sample it (the stand-in when none).</summary>
    SampledTexture SampledUnit(int unit, SamplerInfo sampler);

    /// <summary>The buffer's current memory (dynamic buffers move every frame; a static one is renamed after a write), marked used by this frame.</summary>
    BufferBinding Buffer(uint glBuffer);

    /// <summary>The buffer bound to uniform binding point <paramref name="index"/> (BindBufferBase), as VkGl would push it.</summary>
    BufferBinding UniformBinding(uint index);

    /// <summary>A GL texture name for a native texture (borrowed: DeleteTexture forgets it, the image stays the native owner's).</summary>
    uint Import(Texture texture);

    /// <summary>A GL buffer name for a native buffer (static; borrowed like <see cref="Import"/>).</summary>
    uint ImportBuffer(DeviceBuffer buffer);
}
