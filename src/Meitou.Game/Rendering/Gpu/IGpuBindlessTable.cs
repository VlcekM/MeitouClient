namespace Meitou.Rendering.Gpu;

/// <summary>
/// The bindless table as the renderers see it (docs/resource-layer.md): the arrays of textures a shader
/// indexes by number, and the engine's own bookkeeping of what each index holds. The table's storage is
/// the backend's: Vulkan's is a descriptor set per frame slot, Metal's an argument buffer with a texture
/// array and a sampler array.
/// </summary>
public interface IGpuBindlessTable : IDisposable
{
    /// <summary>Entries per array (<see cref="BindlessKind"/> order).</summary>
    uint[] Capacity { get; }

    /// <summary>How many entries are in use (the arrays' high-water mark minus the free ones).</summary>
    int Registered { get; }

    /// <summary>
    /// Adds a texture to the array its kind names. Its index is valid in the frame that is open (when one
    /// is) and in every frame that begins after the call.
    /// </summary>
    uint Register(BindlessKind kind, in SampledTexture texture);

    /// <summary>Adds a texture with its sampler to the array its format and kind select.</summary>
    BindlessHandle Register(GpuTexture texture, GpuSampler sampler, bool shadow = false);

    /// <summary>
    /// Replaces an entry's sampler or view (the upscaler's LOD bias changed, a texture was re-created).
    /// Takes effect in the frame that is open and in every frame that begins after the call.
    /// </summary>
    void Update(BindlessKind kind, uint index, in SampledTexture texture);

    /// <summary>Releases an index once the frames in flight are done with it.</summary>
    void Free(BindlessKind kind, uint index);

    /// <summary>As <see cref="Free(BindlessKind, uint)"/>.</summary>
    void Free(BindlessHandle handle);

    /// <summary>As <see cref="Update(BindlessKind, uint, in SampledTexture)"/>.</summary>
    void Update(BindlessHandle handle, in SampledTexture texture);

    /// <summary>The index into the backend's sets of the set the table is bound as (the renderers bind the table, not a set).</summary>
    int SetIndex { get; }

    /// <summary>At the start of a frame in the slot: the journal's new entries are written into the slot's table.</summary>
    void BeginFrame(int slot);

    /// <summary>At the end of the open frame, before it is submitted: what was registered during the frame is written in.</summary>
    void EndFrame();
}
