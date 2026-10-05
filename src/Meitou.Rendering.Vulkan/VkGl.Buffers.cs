using Meitou.Rendering.Vulkan.Core;
using Meitou.Rendering.Gpu;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace Meitou.Rendering.Vulkan;

public sealed unsafe partial class VkGl
{
    /// <summary>
    /// A GL buffer. Static ones (STATIC_DRAW) are device-local and written through the upload command buffer; stream/dynamic
    /// ones live in the per-frame rings and are written by the CPU. Either way a buffer the frame has already drawn from gets new
    /// memory on its next write (rename), so the earlier draws see the old contents as GL promises.
    /// </summary>
    internal sealed class GlBufferObj(uint id)
    {
        public readonly uint Id = id;
        public long Size;
        public bool Dynamic;
        public GpuBuffer? Device;             // static
        public long UsedFrame = -1;           // static: last frame a draw read it
        public RingSlice Version;             // dynamic: current contents
        public long VersionFrame = -1;
        public int VersionSlot = -1;
        public bool UsedSinceWrite;           // dynamic: a draw of this frame read the current version
        public bool Borrowed;                 // imported native buffer (ImportBuffer): never freed or written here
        public bool Defined => Dynamic ? VersionFrame >= 0 : Device is not null;
    }

    readonly List<GlBufferObj> dynamicBuffers = [];
    uint boundArrayBuffer, boundUniformBuffer, boundCopyBuffer;
    readonly uint[] uniformBindings = new uint[64];
    readonly (long Offset, long Size)[] uniformRanges = new (long, long)[64];

    public uint GenBuffer()
    {
        uint id = NewId();
        buffers[id] = new GlBufferObj(id);
        return id;
    }

    public void DeleteBuffer(uint buffer)
    {
        if (!buffers.Remove(buffer, out var b)) return;
        DestroyBuffer(b);
        if (boundArrayBuffer == buffer) boundArrayBuffer = 0;
        if (boundUniformBuffer == buffer) boundUniformBuffer = 0;
        for (int i = 0; i < uniformBindings.Length; i++) if (uniformBindings[i] == buffer) uniformBindings[i] = 0;
    }

    void DestroyBuffer(GlBufferObj b)
    {
        if (b.Device is { } d && !b.Borrowed) device.Frames.DeferDelete(() => device.Allocator.Free(d));
        b.Device = null;
        dynamicBuffers.Remove(b);
    }

    static void NotImported(GlBufferObj b)
    {
        if (b.Borrowed) throw new InvalidOperationException($"buffer {b.Id} is an imported native buffer: write it through the native API");
    }

    public void BindBuffer(BufferTargetARB target, uint buffer)
    {
        Stats.BindCalls++;
        switch (target)
        {
            case BufferTargetARB.ArrayBuffer: boundArrayBuffer = buffer; break;
            case BufferTargetARB.ElementArrayBuffer: CurrentVao.ElementBuffer = buffer; break;
            case BufferTargetARB.UniformBuffer: boundUniformBuffer = buffer; break;
            default: boundCopyBuffer = buffer; break;
        }
    }

    public void BindBufferBase(BufferTargetARB target, uint index, uint buffer)
    {
        if (target != BufferTargetARB.UniformBuffer) throw new NotSupportedException($"BindBufferBase {target}");
        Stats.BindCalls++;
        uniformBindings[index] = buffer;
        uniformRanges[index] = (0, -1);
        boundUniformBuffer = buffer;
    }

    GlBufferObj Bound(BufferTargetARB target)
    {
        uint id = target switch
        {
            BufferTargetARB.ArrayBuffer => boundArrayBuffer,
            BufferTargetARB.ElementArrayBuffer => CurrentVao.ElementBuffer,
            BufferTargetARB.UniformBuffer => boundUniformBuffer,
            _ => boundCopyBuffer,
        };
        return buffers.TryGetValue(id, out var b) ? b : throw new InvalidOperationException($"no buffer bound to {target}");
    }

    public void BufferData(BufferTargetARB target, nuint size, void* data, BufferUsageARB usage) => Specify(Bound(target), (long)size, data, usage);

    public void BufferData<T>(BufferTargetARB target, ReadOnlySpan<T> data, BufferUsageARB usage) where T : unmanaged
    {
        fixed (T* p = data) Specify(Bound(target), (long)data.Length * sizeof(T), p, usage);
    }

    public void BufferSubData(BufferTargetARB target, nint offset, nuint size, void* data) => Write(Bound(target), offset, (long)size, data);

    void Specify(GlBufferObj b, long size, void* data, BufferUsageARB usage)
    {
        NotImported(b);
        bool dynamic = usage is not (BufferUsageARB.StaticDraw or BufferUsageARB.StaticRead or BufferUsageARB.StaticCopy);
        if (b.Device is { } old) { b.Device = null; device.Frames.DeferDelete(() => device.Allocator.Free(old)); }
        b.Size = Math.Max(size, 4);
        b.Dynamic = dynamic;
        if (dynamic)
        {
            if (!dynamicBuffers.Contains(b)) dynamicBuffers.Add(b);
            NewVersion(b);
            if (data is not null) System.Buffer.MemoryCopy(data, b.Version.Pointer, b.Size, size);
            return;
        }
        dynamicBuffers.Remove(b);
        b.VersionFrame = -1;
        b.Device = device.Allocator.CreateBuffer((ulong)b.Size, BufferUsageFlags.VertexBufferBit | BufferUsageFlags.IndexBufferBit | BufferUsageFlags.UniformBufferBit |
            BufferUsageFlags.TransferDstBit | BufferUsageFlags.TransferSrcBit, MemoryKind.DeviceLocal, $"gl buffer {b.Id}");
        b.UsedFrame = -1;
        if (data is not null) UploadBuffer(b.Device, 0, size, data);
    }

    void NewVersion(GlBufferObj b)
    {
        b.Version = Ring.Allocate((ulong)b.Size, 256);
        b.VersionFrame = device.Frames.FrameNumber;
        b.VersionSlot = device.Frames.Slot;
        b.UsedSinceWrite = false;
    }

    void Write(GlBufferObj b, long offset, long size, void* data)
    {
        if (size <= 0) return;
        NotImported(b);
        if (offset + size > b.Size) throw new ArgumentOutOfRangeException(nameof(size), $"buffer {b.Id}: {offset}+{size} > {b.Size}");
        if (b.Dynamic)
        {
            if (b.VersionFrame != device.Frames.FrameNumber || b.UsedSinceWrite)
            {
                var old = b.Version;
                NewVersion(b);
                System.Buffer.MemoryCopy(old.Pointer, b.Version.Pointer, b.Size, b.Size);
                Stats.BuffersRenamed++;
            }
            System.Buffer.MemoryCopy(data, b.Version.Pointer + offset, size, size);
            return;
        }
        var target = b.Device ?? throw new InvalidOperationException($"buffer {b.Id} has no storage");
        if (b.UsedFrame == device.Frames.FrameNumber)
        {
            // Drawn from earlier this frame: new memory, the old contents copied over first (in the upload command buffer,
            // which runs before this frame's draws; the old buffer keeps what those draws need).
            var fresh = device.Allocator.CreateBuffer((ulong)b.Size, BufferUsageFlags.VertexBufferBit | BufferUsageFlags.IndexBufferBit | BufferUsageFlags.UniformBufferBit |
                BufferUsageFlags.TransferDstBit | BufferUsageFlags.TransferSrcBit, MemoryKind.DeviceLocal, $"gl buffer {b.Id}");
            var copy = new BufferCopy(0, 0, (ulong)b.Size);
            vk.CmdCopyBuffer(UploadCmd, target.Buffer, fresh.Buffer, 1, &copy);
            BufferWriteBarrier(UploadCmd);
            var old = target;
            device.Frames.DeferDelete(() => device.Allocator.Free(old));
            b.Device = target = fresh;
            b.UsedFrame = -1;
            Stats.BuffersRenamed++;
        }
        UploadBuffer(target, offset, size, data);
    }

    void BufferWriteBarrier(CommandBuffer cb)
    {
        var barrier = new MemoryBarrier2
        {
            SType = StructureType.MemoryBarrier2,
            SrcStageMask = PipelineStageFlags2.TransferBit, SrcAccessMask = AccessFlags2.TransferWriteBit,
            DstStageMask = PipelineStageFlags2.TransferBit, DstAccessMask = AccessFlags2.TransferReadBit | AccessFlags2.TransferWriteBit,
        };
        var info = new DependencyInfo { SType = StructureType.DependencyInfo, MemoryBarrierCount = 1, PMemoryBarriers = &barrier };
        vk.CmdPipelineBarrier2(cb, &info);
    }

    void UploadBuffer(GpuBuffer target, long offset, long size, void* data)
    {
        var staging = Ring.Allocate((ulong)size, 16);
        System.Buffer.MemoryCopy(data, staging.Pointer, size, size);
        var copy = new BufferCopy(staging.Offset, (ulong)offset, (ulong)size);
        vk.CmdCopyBuffer(UploadCmd, staging.Buffer, target.Buffer, 1, &copy);
        Stats.Uploads++;
        Stats.UploadBytes += size;
    }

    /// <summary>The memory a draw reads for <paramref name="b"/>, marking it used by this frame.</summary>
    (VkBuffer Buffer, ulong Offset) Use(GlBufferObj b)
    {
        if (b.Dynamic)
        {
            b.UsedSinceWrite = true;
            return (b.Version.Buffer, b.Version.Offset);
        }
        b.UsedFrame = device.Frames.FrameNumber;
        return (b.Device!.Buffer, 0);
    }

    /// <summary>
    /// At the start of a frame, before its slot's ring is reused: dynamic buffers whose contents still live in that ring (not
    /// rewritten since) are copied into the new frame's memory, so data written once stays valid however long it is used.
    /// </summary>
    void CarryDynamicBuffers(int slot, Action resetRing)
    {
        List<(GlBufferObj, byte[])>? carried = null;
        foreach (var b in dynamicBuffers)
            if (b.VersionSlot == slot && b.VersionFrame >= 0)
            {
                var copy = new byte[b.Size];
                fixed (byte* p = copy) System.Buffer.MemoryCopy(b.Version.Pointer, p, b.Size, b.Size);
                (carried ??= []).Add((b, copy));
            }
        resetRing();
        if (carried is null) return;
        foreach (var (b, data) in carried)
        {
            NewVersion(b);
            fixed (byte* p = data) System.Buffer.MemoryCopy(p, b.Version.Pointer, b.Size, b.Size);
        }
    }

    // ----- Vertex arrays -----

    internal struct VertexAttrib
    {
        public bool Enabled, Integer, Normalized;
        public uint Buffer;
        public int Size;
        public GLEnum Type;
        public uint Stride, Divisor;
        public long Offset;
    }

    internal sealed class GlVertexArray
    {
        public readonly VertexAttrib[] Attribs = new VertexAttrib[16];
        public uint ElementBuffer;
    }

    readonly GlVertexArray defaultVao = new();
    uint boundVao;
    GlVertexArray CurrentVao => boundVao != 0 && vertexArrays.TryGetValue(boundVao, out var v) ? v : defaultVao;

    public uint GenVertexArray()
    {
        uint id = NewId();
        vertexArrays[id] = new GlVertexArray();
        return id;
    }

    public void DeleteVertexArray(uint array)
    {
        vertexArrays.Remove(array);
        if (boundVao == array) boundVao = 0;
    }

    public void BindVertexArray(uint array) { Stats.BindCalls++; boundVao = array; }

    public void EnableVertexAttribArray(uint index) { Stats.AttribCalls++; CurrentVao.Attribs[index].Enabled = true; }

    public void VertexAttribPointer(uint index, int size, VertexAttribPointerType type, bool normalized, uint stride, void* pointer)
    {
        Stats.AttribCalls++;
        ref var a = ref CurrentVao.Attribs[index];
        a.Buffer = boundArrayBuffer;
        a.Size = size;
        a.Type = (GLEnum)type;
        a.Normalized = normalized;
        a.Integer = false;
        a.Stride = stride;
        a.Offset = (long)pointer;
    }

    public void VertexAttribIPointer(uint index, int size, VertexAttribIType type, uint stride, void* pointer)
    {
        Stats.AttribCalls++;
        ref var a = ref CurrentVao.Attribs[index];
        a.Buffer = boundArrayBuffer;
        a.Size = size;
        a.Type = (GLEnum)type;
        a.Normalized = false;
        a.Integer = true;
        a.Stride = stride;
        a.Offset = (long)pointer;
    }

    public void VertexAttribDivisor(uint index, uint divisor)
    {
        Stats.AttribCalls++;
        if (divisor > 1) throw new NotSupportedException("vertex attribute divisors above 1");
        CurrentVao.Attribs[index].Divisor = divisor;
    }

    static Format AttribFormat(in VertexAttrib a) => GlConventions.VertexFormat(a.Type, a.Size, a.Normalized, a.Integer);

    static uint AttribBytes(in VertexAttrib a) => GlConventions.VertexBytes(a.Type, a.Size);
}
