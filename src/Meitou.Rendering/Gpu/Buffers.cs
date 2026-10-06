using System.Collections.Concurrent;
using Meitou.Rendering.Vulkan.Core;
using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace Meitou.Rendering.Gpu;

/// <summary>What a buffer is used for (docs/renderer-native.md 2.3).</summary>
[Flags]
public enum BufferUse { Vertex = 1, Index = 2, Uniform = 4, Storage = 8, Indirect = 16, TransferSrc = 32, TransferDst = 64 }

/// <summary>A range of a buffer as a draw or a descriptor reads it.</summary>
public readonly record struct BufferBinding(Buffer Buffer, ulong Offset, ulong Size = Vk.WholeSize)
{
    public bool IsNull => Buffer.Handle == 0;

    // By handle: Silk.NET's handle structs are not IEquatable, so the generated equality boxed the buffer.
    public bool Equals(BufferBinding other) => Buffer.Handle == other.Buffer.Handle && Offset == other.Offset && Size == other.Size;
    public override int GetHashCode() => HashCode.Combine(Buffer.Handle, Offset, Size);
}

/// <summary>
/// A device-local buffer, written through the <see cref="Uploader"/> (static meshes, persistent instance stores, indirect arguments).
/// Writing one the current frame has already used is a bug in the native API (VkGl renamed such buffers; the native API asserts).
/// </summary>
public sealed class DeviceBuffer : IDisposable
{
    readonly VulkanDevice device;
    readonly bool owned;

    internal DeviceBuffer(VulkanDevice device, GpuBuffer buffer, bool owned)
    {
        this.device = device;
        Underlying = buffer;
        this.owned = owned;
    }

    /// <summary>A new device-local buffer of <paramref name="size"/> bytes.</summary>
    public static DeviceBuffer Create(GpuContext ctx, ulong size, BufferUse use, string name)
    {
        var buffer = ctx.Device.Allocator.CreateBuffer(Math.Max(size, 4), Usage(use) | BufferUsageFlags.TransferDstBit, MemoryKind.DeviceLocal, name);
        return new DeviceBuffer(ctx.Device, buffer, owned: true);
    }

    internal static BufferUsageFlags Usage(BufferUse use)
    {
        BufferUsageFlags f = 0;
        if (use.HasFlag(BufferUse.Vertex)) f |= BufferUsageFlags.VertexBufferBit;
        if (use.HasFlag(BufferUse.Index)) f |= BufferUsageFlags.IndexBufferBit;
        if (use.HasFlag(BufferUse.Uniform)) f |= BufferUsageFlags.UniformBufferBit;
        if (use.HasFlag(BufferUse.Storage)) f |= BufferUsageFlags.StorageBufferBit;
        if (use.HasFlag(BufferUse.Indirect)) f |= BufferUsageFlags.IndirectBufferBit;
        if (use.HasFlag(BufferUse.TransferSrc)) f |= BufferUsageFlags.TransferSrcBit;
        if (use.HasFlag(BufferUse.TransferDst)) f |= BufferUsageFlags.TransferDstBit;
        return f;
    }

    public GpuBuffer Underlying { get; }
    public Buffer Handle => Underlying.Buffer;
    public ulong Size => Underlying.Size;
    /// <summary>The last frame that bound it (<see cref="Binding"/>); 0 when never.</summary>
    public long UsedFrame { get; internal set; }

    /// <summary>A binding of the range from <paramref name="offset"/>, marking the buffer used by <paramref name="frame"/> (the Uploader
    /// refuses to write it again in that frame).</summary>
    public BufferBinding Binding(GpuFrame frame, ulong offset = 0, ulong size = Vk.WholeSize)
    {
        UsedFrame = frame.Number;
        return new BufferBinding(Handle, offset, size);
    }

    public void Dispose()
    {
        if (owned) device.DeferFree(Underlying);
    }
}

/// <summary>
/// (Added for the GPU cull's verify mode, docs/renderer-native.md 5.6.) A host-visible, cached buffer the GPU copies into (transfer
/// destination) and the CPU reads once the frame that wrote it has completed (<see cref="Completed"/>), never waiting for it.
/// </summary>
public sealed unsafe class ReadbackBuffer : IDisposable
{
    readonly VulkanDevice device;
    readonly GpuBuffer buffer;

    ReadbackBuffer(VulkanDevice device, GpuBuffer buffer)
    {
        this.device = device;
        this.buffer = buffer;
    }

    public static ReadbackBuffer Create(GpuContext ctx, ulong size, string name) =>
        new(ctx.Device, ctx.Device.Allocator.CreateBuffer(Math.Max(size, 4), BufferUsageFlags.TransferDstBit, MemoryKind.Readback, name));

    public Buffer Handle => buffer.Buffer;
    public ulong Size => buffer.Size;

    /// <summary>Whether frame <paramref name="frame"/> (a <see cref="GpuFrame.Number"/>) has completed on the GPU, so what it copied can be read.</summary>
    public static bool Completed(GpuContext ctx, long frame) => ctx.Device.Frames.CompletedFrame >= frame;

    /// <summary>The bytes [<paramref name="offset"/>, +<paramref name="size"/>), made visible to the host first (invalidated when not coherent).</summary>
    public ReadOnlySpan<byte> Read(ulong offset, ulong size)
    {
        if (offset + size > buffer.Size) throw new ArgumentOutOfRangeException(nameof(size));
        device.Allocator.Invalidate(buffer.Allocation);
        return new ReadOnlySpan<byte>((byte*)buffer.Mapped + offset, (int)size);
    }

    public void Dispose() => device.DeferFree(buffer);
}

/// <summary>A slice of host-visible per-frame memory, valid until the frame slot comes round again.</summary>
public readonly unsafe struct Transient(Buffer handle, ulong offset, byte* pointer, ulong size)
{
    public Buffer Handle { get; } = handle;
    public ulong Offset { get; } = offset;
    public byte* Pointer { get; } = pointer;
    public ulong Size { get; } = size;
    public BufferBinding Binding => new(Handle, Offset, Size);
    public Span<byte> Bytes => new(Pointer, (int)Size);
}

/// <summary>
/// Host-visible mapped buffers by handle, so a draw log can hash what a draw reads from per-frame memory (VkGl's rings and the native
/// allocators register their chunks). Thread-safe.
/// </summary>
public sealed unsafe class HostMemory
{
    readonly ConcurrentDictionary<ulong, (nint Pointer, ulong Size)> chunks = new();

    public void Register(Buffer buffer, void* mapped, ulong size) => chunks[buffer.Handle] = ((nint)mapped, size);
    public void Forget(Buffer buffer) => chunks.TryRemove(buffer.Handle, out _);

    /// <summary>The mapped bytes of [<paramref name="offset"/>, +<paramref name="size"/>) of a registered buffer, or empty.</summary>
    public ReadOnlySpan<byte> Read(Buffer buffer, ulong offset, ulong size)
    {
        if (!chunks.TryGetValue(buffer.Handle, out var c) || offset >= c.Size) return default;
        ulong n = Math.Min(size, c.Size - offset);
        return new ReadOnlySpan<byte>((byte*)c.Pointer + offset, (int)n);
    }

    public bool Contains(Buffer buffer) => chunks.ContainsKey(buffer.Handle);
}

/// <summary>
/// Per-frame host-visible memory: a pointer bump in 8 MB chunks (larger requests get a chunk of their own, freed at the next reset).
/// Regular chunks live as long as the allocator, so descriptor sets made for them stay valid. One per frame slot (and, in wave 4, per
/// recording thread); <see cref="Reset"/> when the slot comes round. Not thread-safe.
/// </summary>
public sealed unsafe class LinearAllocator : IDisposable
{
    public const ulong ChunkSize = 8 << 20;
    const BufferUsageFlags Usage = BufferUsageFlags.UniformBufferBit | BufferUsageFlags.StorageBufferBit | BufferUsageFlags.VertexBufferBit |
        BufferUsageFlags.IndexBufferBit | BufferUsageFlags.IndirectBufferBit | BufferUsageFlags.TransferSrcBit | BufferUsageFlags.TransferDstBit;
    readonly VulkanDevice device;
    readonly HostMemory? registry;
    readonly string name;
    readonly List<GpuBuffer> chunks = [];
    int current = -1;
    ulong offset;

    public LinearAllocator(VulkanDevice device, string name, HostMemory? registry = null)
    {
        this.device = device;
        this.name = name;
        this.registry = registry;
    }

    /// <summary>Bytes handed out since the last reset.</summary>
    public ulong Used { get; private set; }

    public void Reset()
    {
        for (int i = chunks.Count - 1; i >= 0; i--)
            if (chunks[i].Size > ChunkSize)
            {
                registry?.Forget(chunks[i].Buffer);
                device.Allocator.Free(chunks[i]);
                chunks.RemoveAt(i);
            }
        current = chunks.Count > 0 ? 0 : -1;
        offset = 0;
        Used = 0;
    }

    public Transient Allocate(ulong size, ulong alignment)
    {
        if (size == 0) size = 4;
        if (alignment == 0) alignment = 1;
        Used += size;
        if (current >= 0)
        {
            ulong aligned = (offset + alignment - 1) / alignment * alignment;
            if (aligned + size <= chunks[current].Size)
            {
                offset = aligned + size;
                return Slice(chunks[current], aligned, size);
            }
            while (++current < chunks.Count)
                if (size <= chunks[current].Size) { offset = size; return Slice(chunks[current], 0, size); }
        }
        var chunk = device.Allocator.CreateBuffer(Math.Max(ChunkSize, size), Usage, MemoryKind.Upload, name);
        registry?.Register(chunk.Buffer, chunk.Mapped, chunk.Size);
        chunks.Add(chunk);
        current = chunks.Count - 1;
        offset = size;
        return Slice(chunk, 0, size);
    }

    /// <summary>Copies <paramref name="data"/> into a fresh slice aligned to 16 bytes (or <paramref name="alignment"/>).</summary>
    public Transient Write<T>(ReadOnlySpan<T> data, ulong alignment = 16) where T : unmanaged
    {
        ulong bytes = (ulong)(data.Length * sizeof(T));
        var t = Allocate(bytes, alignment);
        fixed (T* p = data) System.Buffer.MemoryCopy(p, t.Pointer, bytes, bytes);
        return t;
    }

    static Transient Slice(GpuBuffer chunk, ulong at, ulong size) => new(chunk.Buffer, at, (byte*)chunk.Mapped + at, size);

    public void Dispose()
    {
        foreach (var c in chunks)
        {
            registry?.Forget(c.Buffer);
            device.DeferFree(c);
        }
        chunks.Clear();
    }
}

/// <summary>A range of a <see cref="BufferArena"/>.</summary>
public readonly record struct ArenaRange(ulong Offset, ulong Size)
{
    public bool IsEmpty => Size == 0;
}

/// <summary>
/// A device-local buffer suballocated in ranges that live as long as their owner (a foliage zone's instances, an object page): first fit
/// with coalescing; a freed range becomes reusable once the frames that could read it have finished. Render thread only.
/// </summary>
public sealed class BufferArena : IDisposable
{
    readonly VulkanDevice device;
    readonly List<(ulong Offset, ulong Size)> free = [];
    readonly object gate = new();

    public BufferArena(GpuContext ctx, ulong size, BufferUse use, string name)
    {
        device = ctx.Device;
        Buffer = DeviceBuffer.Create(ctx, size, use, name);
        free.Add((0, Buffer.Size));
    }

    public DeviceBuffer Buffer { get; }
    /// <summary>Bytes not handed out (freed ranges count once their frames have finished).</summary>
    public ulong FreeBytes { get { lock (gate) return free.Aggregate(0ul, (s, r) => s + r.Size); } }

    /// <summary>A range of <paramref name="size"/> bytes at a multiple of <paramref name="alignment"/>; empty when the arena is full.</summary>
    public ArenaRange Allocate(ulong size, ulong alignment)
    {
        if (size == 0) return default;
        if (alignment == 0) alignment = 1;
        lock (gate)
            for (int i = 0; i < free.Count; i++)
            {
                var (o, s) = free[i];
                ulong aligned = (o + alignment - 1) / alignment * alignment;
                if (aligned + size > o + s) continue;
                free.RemoveAt(i);
                // Keep the alignment gap and the tail as free ranges.
                if (aligned > o) free.Insert(i++, (o, aligned - o));
                if (aligned + size < o + s) free.Insert(i, (aligned + size, o + s - aligned - size));
                return new ArenaRange(aligned, size);
            }
        return default;
    }

    /// <summary>Returns <paramref name="range"/> once every frame in flight has finished with it.</summary>
    public void Free(ArenaRange range)
    {
        if (range.IsEmpty) return;
        device.Frames.DeferDelete(() => Release(range));
    }

    void Release(ArenaRange range)
    {
        lock (gate)
        {
            int i = 0;
            while (i < free.Count && free[i].Offset < range.Offset) i++;
            free.Insert(i, (range.Offset, range.Size));
            // Coalesce with the neighbours.
            if (i + 1 < free.Count && free[i].Offset + free[i].Size == free[i + 1].Offset)
            {
                free[i] = (free[i].Offset, free[i].Size + free[i + 1].Size);
                free.RemoveAt(i + 1);
            }
            if (i > 0 && free[i - 1].Offset + free[i - 1].Size == free[i].Offset)
            {
                free[i - 1] = (free[i - 1].Offset, free[i - 1].Size + free[i].Size);
                free.RemoveAt(i);
            }
        }
    }

    public void Dispose() => Buffer.Dispose();
}
