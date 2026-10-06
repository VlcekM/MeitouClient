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
/// GL's fixed-function state as VkGl would turn it into a pipeline and dynamic state for a draw into <see cref="IGlInterop.CurrentTargets"/>
/// now (<see cref="IGlInterop.CurrentState"/>): depth test and write only with a depth attachment (write only while testing), blend only
/// with a colour attachment, alpha-to-coverage only with more than one sample, depth clamp only where the device has it.
/// </summary>
public sealed record DrawState(CullModeFlags Cull, FrontFace Front, bool DepthTest, bool DepthWrite, CompareOp Compare,
    bool BiasEnable, float BiasConstant, float BiasSlope, BlendState Blend, ColorComponentFlags ColourMask, Silk.NET.Vulkan.PolygonMode Polygon,
    bool AlphaToCoverage, bool DepthClamp)
{
    /// <summary>The pipeline VkGl would make for <paramref name="program"/> with this state.</summary>
    public GraphicsPipelineDesc Pipeline(ShaderProgram program, VertexLayout vertex, PrimitiveTopology topology, AttachmentFormats targets, string name = "") =>
        new(program, vertex, topology, targets, Blend, ColourMask, Polygon, AlphaToCoverage, DepthClamp, name);

    /// <summary>Records the dynamic state: <paramref name="t"/>'s viewport and scissor, and this cull, front face, depth and bias
    /// (<paramref name="front"/> overrides the front face, for a draw that turns the winding round).</summary>
    public void Record(CommandList cmd, PassTargets t, FrontFace? front = null)
    {
        cmd.SetViewport(t.Viewport);
        cmd.SetScissor(t.Scissor);
        cmd.SetRaster(Cull, front ?? Front);
        cmd.SetDepth(DepthTest, DepthWrite, Compare);
        cmd.SetDepthBias(BiasEnable, BiasConstant, BiasSlope);
    }
}

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

    /// <summary>GL's fixed-function state as VkGl would apply it to a draw into <see cref="CurrentTargets"/> now.</summary>
    DrawState CurrentState();

    /// <summary>A GL vertex array's attributes and element buffer, marked used by this frame (as a VkGl draw with it would).</summary>
    VertexArrayBindings VertexArray(uint glVertexArray);

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
