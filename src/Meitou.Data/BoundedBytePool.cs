using System.Buffers;
using System.Numerics;

namespace Meitou.Data;

/// <summary>
/// A byte array pool that keeps at most a set number of idle bytes: arrays come in power-of-two sizes, a returned array waits for the next rent of
/// its size while the idle total stays within the budget and goes to the GC otherwise. Streaming's large transient buffers (texture file bytes,
/// terrain levels) use <see cref="Streaming"/> instead of <see cref="ArrayPool{T}.Shared"/>, whose per-core partitions kept hundreds of megabytes
/// of them idle for the whole session (docs/engine.md, "Pooled buffers"). Same contract as any pool: a rented array may be longer than asked and
/// holds old bytes; it is returned once, after its last reader.
/// </summary>
public sealed class BoundedBytePool : ArrayPool<byte>
{
    /// <summary>
    /// The smallest array handed out (a smaller request gets one of this size).
    /// </summary>
    const int MinLength = 4096;

    /// <summary>
    /// The pool of streaming's buffers: texture file bytes, terrain levels and the impostor reader's scratch.
    /// </summary>
    public static BoundedBytePool Streaming { get; } = new(256L << 20);

    /// <summary>
    /// The idle arrays by size class (index i holds arrays of <see cref="MinLength"/> &lt;&lt; i bytes), newest on top; guarded by itself.
    /// </summary>
    readonly Stack<byte[]>[] _idle = [.. Enumerable.Range(0, 31 - BitOperations.Log2(MinLength)).Select(_ => new Stack<byte[]>())];

    /// <summary>
    /// The most idle bytes kept.
    /// </summary>
    readonly long _budget;

    /// <summary>
    /// The bytes of the idle arrays; guarded by <see cref="_idle"/>.
    /// </summary>
    long _idleBytes;

    /// <summary>
    /// A pool keeping at most <paramref name="budget"/> idle bytes.
    /// </summary>
    public BoundedBytePool(long budget) => _budget = budget;

    /// <summary>
    /// The bytes of the arrays waiting in the pool.
    /// </summary>
    public long IdleBytes
    {
        get
        {
            lock (_idle) return _idleBytes;
        }
    }

    /// <summary>
    /// An array of at least <paramref name="minimumLength"/> bytes: an idle one of its size class, or a new one (not cleared).
    /// </summary>
    public override byte[] Rent(int minimumLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(minimumLength);

        int size = SizeClass(minimumLength);
        if (size >= _idle.Length) return GC.AllocateUninitializedArray<byte>(minimumLength);

        lock (_idle)
        {
            if (_idle[size].TryPop(out var array))
            {
                _idleBytes -= array.Length;
                return array;
            }
        }

        return GC.AllocateUninitializedArray<byte>(MinLength << size);
    }

    /// <summary>
    /// Takes <paramref name="array"/> back for a later rent while the idle bytes stay within the budget; an array of another length than a size
    /// class (not from this pool) or one over the budget is left to the GC.
    /// </summary>
    public override void Return(byte[] array, bool clearArray = false)
    {
        ArgumentNullException.ThrowIfNull(array);

        int size = SizeClass(array.Length);
        if (size >= _idle.Length || MinLength << size != array.Length) return;

        if (clearArray) Array.Clear(array);

        lock (_idle)
        {
            if (_idleBytes + array.Length > _budget) return;

            _idle[size].Push(array);
            _idleBytes += array.Length;
        }
    }

    /// <summary>
    /// The size class of <paramref name="length"/> bytes: the index of the smallest power of two, from <see cref="MinLength"/>, that holds it.
    /// </summary>
    static int SizeClass(int length) => length <= MinLength ? 0 : BitOperations.Log2((uint)length - 1) + 1 - BitOperations.Log2(MinLength);
}
