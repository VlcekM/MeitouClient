using System.Buffers;
using System.Runtime.InteropServices;

namespace Meitou.Data;

/// <summary>
/// A block of native memory read through <see cref="Memory{T}"/>: outside the GC heap, so a large transient buffer (data on its way to the GPU) is
/// given back the moment its owner disposes it and leaves no Large Object Heap garbage or holes (docs/engine.md, "Native buffers"). There is no
/// finalizer (CA2015: it could free a block a <see cref="Span{T}"/> still reads): a block nobody disposes stays allocated, which
/// <see cref="LiveBytes"/> shows.
/// </summary>
public sealed unsafe class NativeBuffer : MemoryManager<byte>
{
    /// <summary>
    /// The bytes of every block not freed yet, over the process; changed atomically.
    /// </summary>
    static long s_liveBytes;

    /// <summary>
    /// The block's address; 0 once disposed.
    /// </summary>
    nint _pointer;

    /// <summary>
    /// The bytes of every block not freed yet, over the process.
    /// </summary>
    public static long LiveBytes => Interlocked.Read(ref s_liveBytes);

    /// <summary>
    /// Allocates <paramref name="length"/> bytes of native memory (at least one byte, so a zero length still has an address); the content is
    /// not cleared.
    /// </summary>
    public NativeBuffer(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);

        _pointer = (nint)NativeMemory.Alloc((nuint)Math.Max(length, 1));
        Length = length;
        Interlocked.Add(ref s_liveBytes, length);
    }

    /// <summary>
    /// The block's size in bytes.
    /// </summary>
    public int Length { get; }

    public override Span<byte> GetSpan()
    {
        nint p = _pointer;
        ObjectDisposedException.ThrowIf(p == 0, this);
        return new Span<byte>((void*)p, Length);
    }

    public override MemoryHandle Pin(int elementIndex = 0)
    {
        nint p = _pointer;
        ObjectDisposedException.ThrowIf(p == 0, this);
        ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)elementIndex, (uint)Length, nameof(elementIndex));
        return new MemoryHandle((byte*)p + elementIndex);   // native memory never moves: nothing to pin
    }

    public override void Unpin() { }

    protected override void Dispose(bool disposing)
    {
        nint p = Interlocked.Exchange(ref _pointer, 0);
        if (p == 0) return;

        NativeMemory.Free((void*)p);
        Interlocked.Add(ref s_liveBytes, -Length);
    }
}
