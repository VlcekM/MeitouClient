using Meitou.Rendering.Gpu;

namespace Meitou.Metal;

/// <summary>A host-readable Metal buffer (shared storage): the GPU writes it with a blit, the CPU reads it once the frame has completed.</summary>
sealed unsafe class MetalReadback : IGpuReadback
{
    readonly MetalContext ctx;

    public MetalReadback(MetalContext ctx, ulong size, string name)
    {
        this.ctx = ctx;
        Handle = ctx.CreateBuffer(Math.Max(size, 4), BufferUse.TransferDst, name);
    }

    public GpuBuffer Handle { get; }
    public ulong Size => Handle.Size;

    public ReadOnlySpan<byte> Read(ulong offset, ulong size)
    {
        if (offset + size > Size) throw new ArgumentOutOfRangeException(nameof(size));
        return new ReadOnlySpan<byte>((byte*)ctx.BufferAt(Handle).Contents + offset, (int)size);
    }

    public void Dispose() => ctx.ReleaseBuffer(Handle);
}
