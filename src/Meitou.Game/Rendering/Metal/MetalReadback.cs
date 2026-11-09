using Meitou.Game.Rendering.Gpu;

namespace Meitou.Game.Rendering.Metal;

/// <summary>
/// A host-readable Metal buffer (shared storage): the GPU writes it with a blit, the CPU reads it once the frame has completed.
/// </summary>
sealed unsafe class MetalReadback : IGpuReadback
{
    readonly MetalContext _ctx;

    public MetalReadback(MetalContext ctx, ulong size, string name)
    {
        this._ctx = ctx;
        Handle = ctx.CreateBuffer(Math.Max(size, 4), BufferUse.TransferDst, name);
    }

    public GpuBuffer Handle { get; }
    public ulong Size => Handle.Size;

    public ReadOnlySpan<byte> Read(ulong offset, ulong size)
    {
        if (offset + size > Size) throw new ArgumentOutOfRangeException(nameof(size));
        return new ReadOnlySpan<byte>((byte*)_ctx.BufferAt(Handle).Contents + offset, (int)size);
    }

    public void Dispose() => _ctx.ReleaseBuffer(Handle);
}
