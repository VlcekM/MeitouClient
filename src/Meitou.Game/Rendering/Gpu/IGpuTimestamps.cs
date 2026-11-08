namespace Meitou.Rendering.Gpu;

/// <summary>
/// The frame's timestamp queries as the seam sees them (docs/resource-layer.md): slots are handed out while
/// the frame is recorded, and a pair's span is read once its frame's slot has come round. The backend's own
/// storage is its own (Vulkan: the query pools of <see cref="QueryArena"/>; Metal: no timestamps yet — slots
/// are handed out, nothing is read).
/// </summary>
public interface IGpuTimestamps
{
    /// <summary>Timestamps taken this frame.</summary>
    int Count { get; }

    /// <summary>A timestamp index in this frame's pool; invalid (Frame 0) when the pool is full or no frame is open.</summary>
    QuerySlot Allocate();

    /// <summary>The timestamp in nanoseconds once its frame has completed and been collected (a frame ring later), else false.</summary>
    bool TryRead(in QuerySlot q, out ulong nanoseconds);
}
