namespace Meitou.Game.Rendering.Gpu;

/// <summary>
/// Diagnostic (issue #319, <c>MEITOU_DIAG_FAULT=1</c>): the bindless indices freed and not registered again, with the frame each was freed in, so a
/// renderer can tell when it draws with an index freed in an earlier frame (its texture may be destroyed while that draw runs). Any thread.
/// </summary>
public sealed class FreedIndices
{
    /// <summary>
    /// The frame each freed index was freed in (the ring's frame number then: the open frame, or the last submitted between frames).
    /// </summary>
    readonly Dictionary<(BindlessKind, uint), long> _freedIn = [];

    /// <summary>
    /// <paramref name="index"/> of <paramref name="kind"/> was freed in <paramref name="frame"/>.
    /// </summary>
    public void Freed(BindlessKind kind, uint index, long frame)
    {
        lock (_freedIn) _freedIn[(kind, index)] = frame;
    }

    /// <summary>
    /// <paramref name="index"/> of <paramref name="kind"/> holds a texture again.
    /// </summary>
    public void Taken(BindlessKind kind, uint index)
    {
        lock (_freedIn) _freedIn.Remove((kind, index));
    }

    /// <summary>
    /// The frame <paramref name="index"/> of <paramref name="kind"/> was freed in when that is before <paramref name="frame"/> (a draw recorded in
    /// <paramref name="frame"/> must not read it); 0 when it is live, or was freed in <paramref name="frame"/> itself.
    /// </summary>
    public long FreedBefore(BindlessKind kind, uint index, long frame)
    {
        lock (_freedIn) return _freedIn.TryGetValue((kind, index), out long freed) && freed < frame ? freed : 0;
    }
}
