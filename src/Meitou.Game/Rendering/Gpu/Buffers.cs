using System.Collections.Concurrent;
using Meitou.Rendering.Gpu.Core;
using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace Meitou.Rendering.Gpu;

/// <summary>What a buffer is used for (docs/renderer-native.md 2.3).</summary>
[Flags]
public enum BufferUse
{
    Vertex = 1, Index = 2, Uniform = 4, Storage = 8, Indirect = 16, TransferSrc = 32, TransferDst = 64,
    /// <summary>Read by acceleration structure builds and by shaders through its device address (needs <see cref="Core.VulkanDevice.HasRayQuery"/>).</summary>
    RayInput = 128,
    /// <summary>Static GPU-only storage: Metal may choose private storage; Vulkan ignores this hint.</summary>
    GpuOnly = 256,
}

/// <summary>A range of a buffer as a draw or a descriptor reads it.</summary>
public readonly record struct GpuBufferBinding(GpuBuffer Buffer, ulong Offset, ulong Size = GpuBufferBinding.WholeSize)
{
    /// <summary>The whole of the rest of the buffer (what a draw or a descriptor reads).</summary>
    public const ulong WholeSize = ulong.MaxValue;

    public bool IsNull => Buffer.IsNull;

    // By handle: the generated equality boxed the handle (on every Bindless call).
    public bool Equals(GpuBufferBinding other) => Buffer == other.Buffer && Offset == other.Offset && Size == other.Size;
    public override int GetHashCode() => HashCode.Combine(Buffer, Offset, Size);
}

/// <summary>
/// A device-local buffer, written through the <see cref="Uploader"/> (static meshes, persistent instance stores, indirect arguments).
/// Writing one the current frame has already used is a bug in the native API (VkGl renamed such buffers; the native API asserts).
/// </summary>
public sealed class DeviceBuffer : IDisposable
{
    readonly IGpuContext ctx;
    readonly bool owned;

    internal DeviceBuffer(IGpuContext ctx, GpuBuffer buffer, bool owned, string name)
    {
        this.ctx = ctx;
        Handle = buffer;
        this.owned = owned;
        Name = name;
    }
    /// <summary>A new device-local buffer of <paramref name="size"/> bytes.</summary>
    public static DeviceBuffer Create(IGpuContext ctx, ulong size, BufferUse use, string name) =>
        new(ctx, ctx.CreateBuffer(size, use, name), owned: true, name);

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
        if (use.HasFlag(BufferUse.RayInput)) f |= BufferUsageFlags.ShaderDeviceAddressBit | BufferUsageFlags.AccelerationStructureBuildInputReadOnlyBitKhr;
        return f;
    }

    /// <summary>The handle the renderers hold: the backend's identity and the size.</summary>
    public GpuBuffer Handle { get; }
    /// <summary>The name the buffer was made with (diagnostics).</summary>
    public string Name { get; }
    public ulong Size => Handle.Size;
    /// <summary>The last frame that bound it (<see cref="Binding"/>); 0 when never.</summary>
    public long UsedFrame { get; internal set; }

    /// <summary>A binding of the range from <paramref name="offset"/>, marking the buffer used by <paramref name="frame"/> (the Uploader
    /// refuses to write it again in that frame).</summary>
    public GpuBufferBinding Binding(IGpuFrame frame, ulong offset = 0, ulong size = GpuBufferBinding.WholeSize)
    {
        UsedFrame = frame.Number;
        return new GpuBufferBinding(Handle, offset, size);
    }

    public void Dispose()
    {
        if (owned) ctx.ReleaseBuffer(Handle);
    }
}

/// <summary>
/// (Added for the GPU cull's verify mode, docs/renderer-native.md 5.6.) A host-visible, cached buffer the GPU copies into (transfer
/// destination) and the CPU reads once the frame that wrote it has completed (<see cref="Completed"/>), never waiting for it.
/// </summary>
public sealed unsafe class ReadbackBuffer : IGpuReadback
{
    readonly VulkanDevice device;
    readonly BufferAllocation buffer;

    ReadbackBuffer(VulkanDevice device, BufferAllocation buffer)
    {
        this.device = device;
        this.buffer = buffer;
    }

    public static ReadbackBuffer Create(GpuContext ctx, ulong size, string name) =>
        new(ctx.Device, ctx.Device.Allocator.CreateBuffer(Math.Max(size, 4), BufferUsageFlags.TransferDstBit, MemoryKind.Readback, name));

    public GpuBuffer Handle => buffer.Handle;
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
public readonly unsafe struct Transient(GpuBuffer handle, ulong offset, byte* pointer, ulong size)
{
    public GpuBuffer Handle { get; } = handle;
    public ulong Offset { get; } = offset;
    public byte* Pointer { get; } = pointer;
    public ulong Size { get; } = size;
    public GpuBufferBinding Binding => new(Handle, Offset, Size);
    public Span<byte> Bytes => new(Pointer, (int)Size);
}

/// <summary>
/// Per-frame host-visible memory: a pointer bump in 8 MB chunks (larger requests get a chunk of their own, freed at the next reset).
/// Regular chunks live as long as the allocator (unless <see cref="Reset"/> trims them), so descriptor sets made for them stay valid. One per frame slot (and, in wave 4, per
/// recording thread); <see cref="Reset"/> when the slot comes round. Not thread-safe.
/// </summary>
public sealed unsafe class LinearAllocator : IDisposable
{
    public const ulong ChunkSize = 8 << 20;
    const BufferUsageFlags Usage = BufferUsageFlags.UniformBufferBit | BufferUsageFlags.StorageBufferBit | BufferUsageFlags.VertexBufferBit |
        BufferUsageFlags.IndexBufferBit | BufferUsageFlags.IndirectBufferBit | BufferUsageFlags.TransferSrcBit | BufferUsageFlags.TransferDstBit;
    readonly VulkanDevice device;
    readonly string name;
    readonly List<BufferAllocation> chunks = [];
    int current = -1;
    ulong offset;

    public LinearAllocator(VulkanDevice device, string name)
    {
        this.device = device;
        this.name = name;
    }

    /// <summary>Bytes handed out since the last reset.</summary>
    public ulong Used { get; private set; }

    /// <summary>
    /// Starts the slot over. With <paramref name="keepChunks"/>, regular chunks beyond that many and beyond those the last cycle used are
    /// freed too: only for an allocator no descriptor set refers to (upload staging), so a loading peak does not stay allocated.
    /// </summary>
    public void Reset(int keepChunks = int.MaxValue)
    {
        foreach (var c in large) device.Allocator.Free(c);
        large.Clear();
        int keep = Math.Max(keepChunks, current + 1);
        for (int i = chunks.Count - 1; i >= keep; i--)
        {
            device.Allocator.Free(chunks[i]);
            chunks.RemoveAt(i);
        }
        current = chunks.Count > 0 ? 0 : -1;
        offset = 0;
        Used = 0;
    }

    public Transient Allocate(ulong size, ulong alignment)
    {
        RenderJobs.AssertNotInJob();   // one allocator per frame, used in Prepare only (docs/renderer-native.md 6.5)
        if (size == 0) size = 4;
        if (alignment == 0) alignment = 1;
        Used += size;
        if (size > ChunkSize) return Slice(NewChunk(size, large), 0, size);
        if (current >= 0)
        {
            ulong aligned = (offset + alignment - 1) / alignment * alignment;
            if (aligned + size <= chunks[current].Size)
            {
                offset = aligned + size;
                return Slice(chunks[current], aligned, size);
            }
            if (++current < chunks.Count) { offset = size; return Slice(chunks[current], 0, size); }
        }
        var chunk = NewChunk(ChunkSize, chunks);
        current = chunks.Count - 1;
        offset = size;
        return Slice(chunk, 0, size);
    }

    // A request larger than a regular chunk gets one of its own, kept apart until the reset: in the regular list it moved the bump cursor past the
    // regular chunks after the current one, and the next small request then added a regular chunk the reset keeps (one more per frame while such requests come).
    readonly List<BufferAllocation> large = [];

    BufferAllocation NewChunk(ulong size, List<BufferAllocation> into)
    {
        long profileStart = UploadProfile.Begin();
        var chunk = device.Allocator.CreateBuffer(size, Usage, MemoryKind.Upload, name);
        UploadProfile.End(UploadProfile.Part.StagingChunk, profileStart);
        into.Add(chunk);
        return chunk;
    }

    /// <summary>Copies <paramref name="data"/> into a fresh slice aligned to 16 bytes (or <paramref name="alignment"/>).</summary>
    public Transient Write<T>(ReadOnlySpan<T> data, ulong alignment = 16) where T : unmanaged
    {
        ulong bytes = (ulong)(data.Length * sizeof(T));
        var t = Allocate(bytes, alignment);
        long profileStart = UploadProfile.Begin();
        fixed (T* p = data) System.Buffer.MemoryCopy(p, t.Pointer, bytes, bytes);
        UploadProfile.End(UploadProfile.Part.StagingCopy, profileStart, (long)bytes);
        return t;
    }

    static Transient Slice(BufferAllocation chunk, ulong at, ulong size) => new(chunk.Handle, at, (byte*)chunk.Mapped + at, size);

    public void Dispose()
    {
        foreach (var c in chunks)
        {
            device.DeferFree(c);
        }
        chunks.Clear();
        foreach (var c in large) device.DeferFree(c);
        large.Clear();
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
    readonly IGpuContext ctx;
    readonly List<(ulong Offset, ulong Size)> free = [];
    readonly object gate = new();

    public BufferArena(IGpuContext ctx, ulong size, BufferUse use, string name)
    {
        this.ctx = ctx;
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
        ctx.DeferDelete(() => Release(range));
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
