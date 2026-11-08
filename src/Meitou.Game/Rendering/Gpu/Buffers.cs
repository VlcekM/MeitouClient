using System.Collections.Concurrent;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Gpu.Core;
using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace Meitou.Game.Rendering.Gpu;

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
    readonly IGpuContext _ctx;
    readonly bool _owned;

    internal DeviceBuffer(IGpuContext ctx, GpuBuffer buffer, bool owned, string name)
    {
        this._ctx = ctx;
        Handle = buffer;
        this._owned = owned;
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
        if (_owned) _ctx.ReleaseBuffer(Handle);
    }
}

/// <summary>
/// (Added for the GPU cull's verify mode, docs/renderer-native.md 5.6.) A host-visible, cached buffer the GPU copies into (transfer
/// destination) and the CPU reads once the frame that wrote it has completed (<see cref="Completed"/>), never waiting for it.
/// </summary>
public sealed unsafe class ReadbackBuffer : IGpuReadback
{
    readonly VulkanDevice _device;
    readonly BufferAllocation _buffer;

    ReadbackBuffer(VulkanDevice device, BufferAllocation buffer)
    {
        this._device = device;
        this._buffer = buffer;
    }

    public static ReadbackBuffer Create(GpuContext ctx, ulong size, string name) =>
        new(ctx.Device, ctx.Device.Allocator.CreateBuffer(Math.Max(size, 4), BufferUsageFlags.TransferDstBit, MemoryKind.Readback, name));

    public GpuBuffer Handle => _buffer.Handle;
    public ulong Size => _buffer.Size;

    /// <summary>Whether frame <paramref name="frame"/> (a <see cref="GpuFrame.Number"/>) has completed on the GPU, so what it copied can be read.</summary>
    public static bool Completed(GpuContext ctx, long frame) => ctx.Device.Frames.CompletedFrame >= frame;

    /// <summary>The bytes [<paramref name="offset"/>, +<paramref name="size"/>), made visible to the host first (invalidated when not coherent).</summary>
    public ReadOnlySpan<byte> Read(ulong offset, ulong size)
    {
        if (offset + size > _buffer.Size) throw new ArgumentOutOfRangeException(nameof(size));
        _device.Allocator.Invalidate(_buffer.Allocation);
        return new ReadOnlySpan<byte>((byte*)_buffer.Mapped + offset, (int)size);
    }

    public void Dispose() => _device.DeferFree(_buffer);
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
    readonly VulkanDevice _device;
    readonly string _name;
    readonly List<BufferAllocation> _chunks = [];
    int _current = -1;
    ulong _offset;

    public LinearAllocator(VulkanDevice device, string name)
    {
        this._device = device;
        this._name = name;
    }

    /// <summary>Bytes handed out since the last reset.</summary>
    public ulong Used { get; private set; }

    /// <summary>
    /// Starts the slot over. With <paramref name="keepChunks"/>, regular chunks beyond that many and beyond those the last cycle used are
    /// freed too: only for an allocator no descriptor set refers to (upload staging), so a loading peak does not stay allocated.
    /// </summary>
    public void Reset(int keepChunks = int.MaxValue)
    {
        foreach (var c in _large) _device.Allocator.Free(c);
        _large.Clear();
        int keep = Math.Max(keepChunks, _current + 1);
        for (int i = _chunks.Count - 1; i >= keep; i--)
        {
            _device.Allocator.Free(_chunks[i]);
            _chunks.RemoveAt(i);
        }
        _current = _chunks.Count > 0 ? 0 : -1;
        _offset = 0;
        Used = 0;
    }

    public Transient Allocate(ulong size, ulong alignment)
    {
        RenderJobs.AssertNotInJob();   // one allocator per frame, used in Prepare only (docs/renderer-native.md 6.5)
        if (size == 0) size = 4;
        if (alignment == 0) alignment = 1;
        Used += size;
        if (size > ChunkSize) return Slice(NewChunk(size, _large), 0, size);
        if (_current >= 0)
        {
            ulong aligned = (_offset + alignment - 1) / alignment * alignment;
            if (aligned + size <= _chunks[_current].Size)
            {
                _offset = aligned + size;
                return Slice(_chunks[_current], aligned, size);
            }
            if (++_current < _chunks.Count) { _offset = size; return Slice(_chunks[_current], 0, size); }
        }
        var chunk = NewChunk(ChunkSize, _chunks);
        _current = _chunks.Count - 1;
        _offset = size;
        return Slice(chunk, 0, size);
    }

    // A request larger than a regular chunk gets one of its own, kept apart until the reset: in the regular list it moved the bump cursor past the
    // regular chunks after the current one, and the next small request then added a regular chunk the reset keeps (one more per frame while such requests come).
    readonly List<BufferAllocation> _large = [];

    BufferAllocation NewChunk(ulong size, List<BufferAllocation> into)
    {
        long profileStart = UploadProfile.Begin();
        var chunk = _device.Allocator.CreateBuffer(size, Usage, MemoryKind.Upload, _name);
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
        foreach (var c in _chunks)
        {
            _device.DeferFree(c);
        }
        _chunks.Clear();
        foreach (var c in _large) _device.DeferFree(c);
        _large.Clear();
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
    readonly IGpuContext _ctx;
    readonly List<(ulong Offset, ulong Size)> _free = [];
    readonly object _gate = new();

    public BufferArena(IGpuContext ctx, ulong size, BufferUse use, string name)
    {
        this._ctx = ctx;
        Buffer = DeviceBuffer.Create(ctx, size, use, name);
        _free.Add((0, Buffer.Size));
    }

    public DeviceBuffer Buffer { get; }
    /// <summary>Bytes not handed out (freed ranges count once their frames have finished).</summary>
    public ulong FreeBytes { get { lock (_gate) return _free.Aggregate(0ul, (s, r) => s + r.Size); } }

    /// <summary>A range of <paramref name="size"/> bytes at a multiple of <paramref name="alignment"/>; empty when the arena is full.</summary>
    public ArenaRange Allocate(ulong size, ulong alignment)
    {
        if (size == 0) return default;
        if (alignment == 0) alignment = 1;
        lock (_gate)
            for (int i = 0; i < _free.Count; i++)
            {
                var (o, s) = _free[i];
                ulong aligned = (o + alignment - 1) / alignment * alignment;
                if (aligned + size > o + s) continue;
                _free.RemoveAt(i);
                // Keep the alignment gap and the tail as free ranges.
                if (aligned > o) _free.Insert(i++, (o, aligned - o));
                if (aligned + size < o + s) _free.Insert(i, (aligned + size, o + s - aligned - size));
                return new ArenaRange(aligned, size);
            }
        return default;
    }

    /// <summary>Returns <paramref name="range"/> once every frame in flight has finished with it.</summary>
    public void Free(ArenaRange range)
    {
        if (range.IsEmpty) return;
        _ctx.DeferDelete(() => Release(range));
    }

    void Release(ArenaRange range)
    {
        lock (_gate)
        {
            int i = 0;
            while (i < _free.Count && _free[i].Offset < range.Offset) i++;
            _free.Insert(i, (range.Offset, range.Size));
            // Coalesce with the neighbours.
            if (i + 1 < _free.Count && _free[i].Offset + _free[i].Size == _free[i + 1].Offset)
            {
                _free[i] = (_free[i].Offset, _free[i].Size + _free[i + 1].Size);
                _free.RemoveAt(i + 1);
            }
            if (i > 0 && _free[i - 1].Offset + _free[i - 1].Size == _free[i].Offset)
            {
                _free[i - 1] = (_free[i - 1].Offset, _free[i - 1].Size + _free[i].Size);
                _free.RemoveAt(i);
            }
        }
    }

    public void Dispose() => Buffer.Dispose();
}