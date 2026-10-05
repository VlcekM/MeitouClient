using Meitou.Rendering.Vulkan.Core;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace Meitou.Rendering.Vulkan;

public sealed unsafe partial class VkGl
{
    readonly FrameRings[] rings;

    FrameRings Ring => rings[device.Frames.Slot];

    /// <summary>A slice of host-visible per-frame memory: valid until this frame slot comes round again.</summary>
    internal readonly struct RingSlice(VkBuffer buffer, ulong offset, byte* pointer, ulong size)
    {
        public readonly VkBuffer Buffer = buffer;
        public readonly ulong Offset = offset;
        public readonly byte* Pointer = pointer;
        public readonly ulong Size = size;
    }

    /// <summary>
    /// Host-visible memory used and thrown away per frame: the loose uniforms of each draw, staging data for uploads, and the
    /// versions of stream/dynamic buffers. Linear allocation in 8 MB chunks (bigger requests get a chunk of their own); a frame
    /// slot's chunks are reused when the slot's fence has passed (<see cref="Reset"/>).
    /// </summary>
    internal sealed class FrameRings(VkGl owner)
    {
        const ulong ChunkSize = 8 << 20;
        const BufferUsageFlags Usage = BufferUsageFlags.UniformBufferBit | BufferUsageFlags.VertexBufferBit | BufferUsageFlags.IndexBufferBit |
            BufferUsageFlags.TransferSrcBit | BufferUsageFlags.TransferDstBit;
        readonly List<GpuBuffer> chunks = [];
        int current = -1;
        ulong offset;

        public void Reset()
        {
            // Keep the regular chunks for reuse, drop the oversized ones.
            for (int i = chunks.Count - 1; i >= 0; i--)
                if (chunks[i].Size > ChunkSize) { owner.device.Allocator.Free(chunks[i]); chunks.RemoveAt(i); }
            current = chunks.Count > 0 ? 0 : -1;
            offset = 0;
        }

        public RingSlice Allocate(ulong size, ulong alignment)
        {
            if (size == 0) size = 4;
            if (current >= 0)
            {
                ulong aligned = (offset + alignment - 1) / alignment * alignment;
                if (aligned + size <= chunks[current].Size)
                {
                    offset = aligned + size;
                    return Slice(chunks[current], aligned, size);
                }
                // Next existing chunk, if any.
                while (++current < chunks.Count)
                    if (size <= chunks[current].Size) { offset = size; return Slice(chunks[current], 0, size); }
            }
            var chunk = owner.device.Allocator.CreateBuffer(Math.Max(ChunkSize, size), Usage, MemoryKind.Upload, "frame ring");
            chunks.Add(chunk);
            current = chunks.Count - 1;
            offset = size;
            return Slice(chunk, 0, size);
        }

        static RingSlice Slice(GpuBuffer chunk, ulong at, ulong size) => new(chunk.Buffer, at, (byte*)chunk.Mapped + at, size);

        public void Dispose()
        {
            foreach (var c in chunks) owner.device.Allocator.Free(c);
            chunks.Clear();
        }
    }
}
