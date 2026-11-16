namespace Meitou.Data.Fcs;

/// <summary>
/// The game loader's binary input stream over a whole file, with the end-of-stream rules of the original's C++ stream (docs/formats/fcs-mod.md,
/// "Damaged files"): a read at the end copies the bytes that are left and fails the stream; once failed, every later read, skip and seek does
/// nothing; a skip that reaches the end only marks it, which fails the next read.
/// </summary>
internal sealed class LoaderStream(byte[] data)
{
    /// <summary>
    /// The file's bytes.
    /// </summary>
    readonly byte[] _data = data;

    /// <summary>
    /// The read position; a relative seek may put it past the end, where reads find nothing.
    /// </summary>
    long _position;

    /// <summary>
    /// Whether a skip reached the end: the next read fails without reading.
    /// </summary>
    bool _atEnd;

    /// <summary>
    /// Whether a read came up short or a seek went before the start: nothing is read from then on.
    /// </summary>
    public bool Failed { get; private set; }

    /// <summary>
    /// The read position.
    /// </summary>
    public long Position => _position;

    /// <summary>
    /// Copies up to <paramref name="into"/>'s length into it and returns how many bytes it copied; fewer than asked fails the stream. A failed stream
    /// copies nothing.
    /// </summary>
    public int Read(Span<byte> into)
    {
        if (Failed || _atEnd)
        {
            Failed = true;
            return 0;
        }

        long left = Math.Max(0, _data.Length - _position);
        int n = (int)Math.Min(into.Length, left);
        if (n > 0) _data.AsSpan((int)_position, n).CopyTo(into);
        _position += n;
        if (n < into.Length) Failed = true;
        return n;
    }

    /// <summary>
    /// Skips <paramref name="count"/> bytes (none for a count of 0 or less; <see cref="int.MaxValue"/> skips to the end); reaching the end marks it.
    /// </summary>
    public void Skip(long count)
    {
        if (Failed || _atEnd)
        {
            Failed = true;
            return;
        }

        if (count <= 0) return;

        long left = Math.Max(0, _data.Length - _position);
        if (count == int.MaxValue || count > left)
        {
            _position += left;
            _atEnd = true;
            return;
        }

        _position += count;
    }

    /// <summary>
    /// Moves the position by <paramref name="offset"/> unless the stream failed; a position before the start fails it.
    /// </summary>
    public void Seek(long offset)
    {
        if (Failed) return;

        long target = _position + offset;
        if (target < 0)
        {
            Failed = true;
            return;
        }

        _position = target;
    }
}