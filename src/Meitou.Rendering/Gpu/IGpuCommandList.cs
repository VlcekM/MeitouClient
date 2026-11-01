namespace Meitou.Rendering.Gpu;

/// <summary>
/// A command list being recorded (docs/renderer-native.md 2.7): the commands the renderers state, in
/// the engine's words. The Vulkan list implements it; a second backend has its own (Metal: a command
/// encoder). The Vulkan-only commands (the raw descriptor writes, the acceleration structure's) stay
/// on the Vulkan class.
/// </summary>
public interface IGpuCommandList
{
    /// <summary>Forgets everything the redundancy filters and callers believed was bound (after another API recorded into the same buffer).</summary>
    void Invalidate();

    /// <summary>The epoch of the command list: a seam crossing starts a new one, and what a caller cached is valid only within one.</summary>
    long Epoch { get; }

    /// <summary>The program whose sets are bound in this epoch (the legacy program's bookkeeping).</summary>
    object? BoundProgram { get; set; }

    // ---- rendering ----

    /// <summary>Begins dynamic rendering on <paramref name="d"/>: one colour and one depth attachment over an area.</summary>
    void BeginRendering(in RenderingDesc d, bool secondaries = false);

    /// <summary>Ends the dynamic rendering.</summary>
    void EndRendering();

    /// <summary>Clears a depth area (a shadow tile, the depth between slices).</summary>
    void ClearDepth(float value, PixelRect rect);

    /// <summary>Clears the colour and / or the depth attachment over an area.</summary>
    void Clear(bool colour, ClearColour colourValue, bool depth, float depthValue, PixelRect rect);

    // ---- state ----

    /// <summary>Binds a graphics pipeline.</summary>
    void BindPipeline(GpuPipeline pipeline);

    /// <summary>Binds a compute pipeline.</summary>
    void BindPipeline(GpuComputePipeline pipeline);

    /// <summary>Sets the viewport.</summary>
    void SetViewport(in PixelViewport v);

    /// <summary>Sets the scissor.</summary>
    void SetScissor(in PixelRect r);

    /// <summary>Sets the cull mode and the front face.</summary>
    void SetRaster(CullMode cull, FrontFaceDirection front);

    /// <summary>Sets the front face.</summary>
    void SetFrontFace(FrontFaceDirection front);

    /// <summary>Sets the depth test and write, and the comparison.</summary>
    void SetDepth(bool test, bool write, DepthFunction op);

    /// <summary>Sets the depth bias.</summary>
    void SetDepthBias(bool enable, float constant, float slope);

    // ---- resources ----

    /// <summary>Binds the vertex buffers of the locations from <paramref name="first"/>.</summary>
    void BindVertexBuffers(uint first, ReadOnlySpan<GpuBufferBinding> bindings);

    /// <summary>Binds the index buffer.</summary>
    void BindIndexBuffer(GpuBufferBinding binding, IndexKind type);

    /// <summary>Binds a program's resources: the slots of a set and what each holds (docs/resource-layer.md).</summary>
    void BindProgramResources(GpuProgram program, uint set, ReadOnlySpan<GpuBinding> bindings);

    /// <summary>Binds sets to a program (the layout comes from the program).</summary>
    void BindSets(GpuProgram program, uint first, ReadOnlySpan<GpuSet> sets, ReadOnlySpan<uint> dynamicOffsets, GpuBindPoint point = GpuBindPoint.Graphics);

    /// <summary>Binds the bindless table to a program.</summary>
    void BindBindlessTable(GpuProgram program, uint set, GpuBindPoint point = GpuBindPoint.Graphics);

    /// <summary>Pushes a block of constants to a program (the layout comes from the program).</summary>
    void PushConstants<T>(GpuProgram program, GpuStage stages, in T value, uint offset = 0) where T : unmanaged;

    /// <summary>Writes a timestamp into a query slot (the frame's timings).</summary>
    void Timestamp(IGpuTimestamps arena, in QuerySlot slot, GpuStage stage = GpuStage.All);

    // ---- draws ----

    /// <summary>Draws vertices.</summary>
    void Draw(uint vertices, uint instances = 1, uint firstVertex = 0, uint firstInstance = 0);

    /// <summary>Draws indexed vertices.</summary>
    void DrawIndexed(uint indices, uint instances = 1, uint firstIndex = 0, int vertexOffset = 0, uint firstInstance = 0);

    /// <summary>Draws indexed vertices, the arguments in a buffer.</summary>
    void DrawIndexedIndirect(GpuBuffer args, ulong offset, uint count, uint stride = 20);

    /// <summary>Draws indexed vertices, the arguments in a buffer and the count in another.</summary>
    void DrawIndexedIndirectCount(GpuBuffer args, ulong offset, GpuBuffer count, ulong countOffset, uint maxCount, uint stride = 20);

    /// <summary>Draws indirect, the count in a buffer (the count of the draw calls is the buffer's).</summary>
    void DrawIndirectCount(GpuBuffer args, ulong offset, GpuBuffer countBuffer, ulong countOffset, uint maxCount, uint stride = 16);

    /// <summary>Dispatches a compute pass.</summary>
    void Dispatch(uint x, uint y = 1, uint z = 1);

    /// <summary>A new texture: the backend's own image, and its transition recorded into this command list.</summary>
    GpuTexture CreateTexture(TextureDesc desc);

    /// <summary>Records the transition of a texture made by <see cref="IGpuContext.CreateTextureOffThread"/> (nothing where the backend has none).</summary>
    void InitTexture(GpuTexture texture) { }

    // ---- transfers ----

    /// <summary>Fills a range of a buffer with a value.</summary>
    void FillBuffer(GpuBuffer b, ulong offset, ulong size, uint value);

    /// <summary>Copies a range of a buffer into another.</summary>
    void CopyBuffer(GpuBuffer src, GpuBuffer dst, in GpuBufferCopy region);

    /// <summary>Copies a range of a buffer into a texture's level and layer.</summary>
    void CopyBufferToImage(GpuBuffer src, ulong offset, GpuTexture dst, int level, int layer);

    /// <summary>Copies a tightly packed region of a texture's level and layer into a buffer (the bakes' readback).</summary>
    void CopyTextureToBuffer(GpuTexture src, int level, int layer, in PixelRect region, GpuBuffer dst, ulong offset);

    /// <summary>Copies the first <paramref name="layers"/> layers of every level of <paramref name="src"/> into the same layers and levels of <paramref name="dst"/>
    /// (same format and level sizes; <paramref name="src"/> made with <see cref="TextureUse.TransferSrc"/>). Ordered by full barriers around it.</summary>
    void CopyLayers(GpuTexture src, GpuTexture dst, int layers);

    /// <summary>Resolves a multisample texture into a single-sample one.</summary>
    void Resolve(GpuTexture src, GpuTexture dst);

    /// <summary>Copies a texture into another (a filter says how).</summary>
    void Blit(GpuTexture src, GpuTexture dst, TextureMinFilter filter);

    /// <summary>Blits one level of a texture into another (the mip chain's step).</summary>
    void BlitLevel(GpuTexture texture, int srcLevel, int dstLevel, TextureMinFilter filter);

    /// <summary>Generates a texture's mip chain.</summary>
    void GenerateMips(GpuTexture t);

    // ---- sync ----

    /// <summary>Records the dependencies the engine's words describe (the backend's masks).</summary>
    void Barrier(ReadOnlySpan<GpuDependency> dependencies);

    /// <summary>Orders everything recorded before against everything after it (the seam between two passes).</summary>
    void Order();

    // ---- labels ----

    /// <summary>Opens a debug label (the name of the pass).</summary>
    void BeginLabel(string name);

    /// <summary>Closes the debug label.</summary>
    void EndLabel();
}
