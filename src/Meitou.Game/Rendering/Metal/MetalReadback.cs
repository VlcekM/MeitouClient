using Meitou.Game.Rendering.Gpu;

namespace Meitou.Game.Rendering.Metal;

/// <summary>
/// A host-readable Metal buffer (shared storage): the GPU writes it with a blit, the CPU reads it once the frame has completed.
/// </summary>
sealed unsafe class MetalReadback : IGpuReadback
{
    /// <summary>
    /// The context the buffer is made and read through.
    /// </summary>
    readonly MetalContext _ctx;

    /// <summary>
    /// A shared buffer of <paramref name="size"/> bytes (4 at least) the GPU copies into.
    /// </summary>
    public MetalReadback(MetalContext ctx, ulong size, string name)
    {
        this._ctx = ctx;
        Handle = ctx.CreateBuffer(Math.Max(size, 4), BufferUse.TransferDst, name);
    }

    /// <summary>
    /// The buffer the GPU writes.
    /// </summary>
    public GpuBuffer Handle { get; }
    /// <summary>
    /// The buffer's size in bytes.
    /// </summary>
    public ulong Size => Handle.Size;

    /// <summary>
    /// The buffer's bytes from <paramref name="offset"/>, valid once the frame that wrote them has completed; throws past the end.
    /// </summary>
    public ReadOnlySpan<byte> Read(ulong offset, ulong size)
    {
        if (offset + size > Size) throw new ArgumentOutOfRangeException(nameof(size));
        return new ReadOnlySpan<byte>((byte*)_ctx.BufferAt(Handle).Contents + offset, (int)size);
    }

    /// <summary>
    /// Releases the buffer.
    /// </summary>
    public void Dispose() => _ctx.ReleaseBuffer(Handle);
}
