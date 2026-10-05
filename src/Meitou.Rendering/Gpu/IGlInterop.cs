using Meitou.Rendering.Vulkan.Shaders;
using Silk.NET.Vulkan;

namespace Meitou.Rendering.Gpu;

/// <summary>
/// What VkGl is drawing into (<see cref="IGlInterop.CurrentTargets"/>): the bound draw framebuffer's attachments, their formats and size, and
/// GL's viewport and scissor as VkGl would set them for a draw (viewport 0 × 0 means the whole target; the scissor clipped to the target).
/// A native renderer drawing into the current pass begins its own rendering on <see cref="Rendering"/> (LOAD / STORE) and sets these.
/// </summary>
public sealed record PassTargets(RenderTarget Colour, RenderTarget Depth, AttachmentFormats Formats, int Width, int Height, Viewport Viewport, Rect2D Scissor)
{
    public RenderingDesc Rendering => new(Colour, Depth, Width, Height);
}

/// <summary>
/// The coexistence seam (docs/renderer-native.md 4.2). Implemented by VkGl; renderers reach it as <see cref="GpuContext.Interop"/> while VkGl
/// exists (null afterwards). Native segments record into VkGl's command buffer of the same frame, in call order.
/// </summary>
public interface IGlInterop
{
    GpuContext Context { get; }

    /// <summary>
    /// Ends VkGl's open pass, places a full barrier and returns the frame's command list (invalidated: nothing is assumed bound). Until
    /// <see cref="EndNative"/>, IGl calls that record or flush throw: they could end the frame under the native code (VkGl.Flush).
    /// </summary>
    CommandList BeginNative(string label);

    /// <summary>A full barrier, then every VkGl cache of command-buffer state is invalidated (pipeline, dynamic state, descriptors, pass).</summary>
    void EndNative(CommandList cmd);

    /// <summary>Records something that does not disturb a render pass (a timestamp, a debug label) into the frame without ending VkGl's pass.</summary>
    void Interleave(Action<CommandList> record);

    /// <summary>The bound draw framebuffer's attachments and GL's viewport and scissor (the GL binding is the truth while VkGl code remains).</summary>
    PassTargets CurrentTargets();

    /// <summary>The image behind a GL texture (non-owning; valid while the GL texture keeps its storage).</summary>
    Texture Texture(uint glTexture);

    /// <summary>The sampler and view VkGl itself would bind for this texture now (its own cache: GL parameters, defined levels, base and
    /// max level, swizzle, the LOD bias). 0 gives the stand-in VkGl binds for a sampler of <paramref name="sampler"/>'s type.</summary>
    SampledTexture Sampled(uint glTexture, SamplerInfo sampler);

    /// <summary>As <see cref="Sampled(uint, SamplerInfo)"/> for a plain 2D sampler, or a shadow sampler.</summary>
    SampledTexture Sampled(uint glTexture, bool shadowSampler);

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
