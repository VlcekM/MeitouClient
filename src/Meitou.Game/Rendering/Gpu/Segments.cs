namespace Meitou.Game.Rendering.Gpu;

/// <summary>
/// Segments (docs/renderer-native.md 8.9): the frame's command list handed to a renderer, with full barriers around its native segments.
/// <see cref="BeginNative"/> opens one outside any pass (a full barrier before and after); <see cref="BeginGuest"/> draws into the open host pass
/// (<see cref="BeginHostPass"/>): its rendering, or with secondaries one of its own in its place.
/// </summary>
public sealed unsafe partial class GpuContext
{
    /// <summary>
    /// The native segment open now (<see cref="BeginNative"/>): the frame's list.
    /// </summary>
    CommandList? _segment;

    /// <summary>
    /// The pass marker of the open native segment (<see cref="Core.PassMarkers"/>; 0 when not tracking).
    /// </summary>
    uint _segmentMarker;

    /// <summary>
    /// The guest segment open now: the host's list or an inline secondary.
    /// </summary>
    CommandList? _guest;

    /// <summary>
    /// Where the open guest segment records.
    /// </summary>
    enum GuestKind
    {
        /// <summary>
        /// Into the host pass's own list.
        /// </summary>
        Host,
        /// <summary>
        /// Into an inline secondary of the host's rendering.
        /// </summary>
        Inline
    }

    /// <summary>
    /// Where the open guest segment records.
    /// </summary>
    GuestKind _guestKind;

    /// <summary>
    /// A native segment is open (<see cref="BeginNative"/> or <see cref="BeginGuest"/>).
    /// </summary>
    public bool SegmentOpen => _segment is not null || _guest is not null;

    /// <summary>
    /// The frame's command list for a segment of the caller's own (renderings, copies, dispatches): what was recorded before is complete and visible
    /// (a full barrier), nothing is assumed bound. <see cref="EndNative"/> closes it with another full barrier.
    /// </summary>
    public IGpuCommandList BeginNative(string label)
    {
        if (SegmentOpen) throw new InvalidOperationException("BeginNative inside a native segment");

        EnsureFrame();
        var list = Frame.Commands;
        FullBarrier(list.Handle);
        list.Invalidate();
        Frame.States.AssumeFullBarrier();
        Frame.Stats.NativeSegments++;
        list.BeginLabel(label);
        _segmentMarker = Device.Faults.Markers?.Begin(list.Handle, label) ?? 0;
        _segment = list;
        return list;
    }

    /// <summary>
    /// Closes the native segment <paramref name="cmd"/> (it must be the open one, with no host pass open) with a full barrier.
    /// </summary>
    public void EndNative(IGpuCommandList cmd)
    {
        if (_segment is null || !ReferenceEquals(cmd, _segment))
            throw new InvalidOperationException("EndNative without a matching BeginNative");

        if (PassOpen) throw new InvalidOperationException("EndNative with a host pass open");

        cmd.EndLabel();
        Device.Faults.Markers?.End(_segment.Handle, _segmentMarker);
        FullBarrier(_segment.Handle);
        _segment = null;
    }

    /// <summary>
    /// A guest's segment in the open host pass: draws only (no rendering, barriers, copies or dispatches), with the host's targets and state
    /// (<see cref="CurrentTargets"/>, <see cref="CurrentState"/>). Inside a rendering with secondaries it is a secondary of its own, executed in its
    /// place. Without a host pass it throws. Closed by <see cref="EndGuest"/>.
    /// </summary>
    public CommandList BeginGuest(string label)
    {
        if (_guest is not null) throw new InvalidOperationException("BeginGuest inside a guest segment");

        if (_passList is { } host)
        {
            if (Frame.Parallel.Open)
            {
                _guestKind = GuestKind.Inline;
                return _guest = Frame.Parallel.BeginInline(label);
            }

            host.Invalidate();
            Frame.Stats.NativeSegments++;
            host.BeginLabel(label);
            _guestKind = GuestKind.Host;
            return _guest = host;
        }

        throw new InvalidOperationException("BeginGuest without a host pass");
    }

    /// <inheritdoc/>
    IGpuCommandList IGpuContext.BeginGuest(string label) => BeginGuest(label);

    /// <summary>
    /// Closes the guest segment <paramref name="cmd"/> (it must be the open one): ends its inline secondary or its label.
    /// </summary>
    public void EndGuest(IGpuCommandList cmd)
    {
        if (_guest is null || !ReferenceEquals(cmd, _guest))
            throw new InvalidOperationException("EndGuest without a matching BeginGuest");

        _guest = null;
        if (_guestKind == GuestKind.Inline) Frame.Parallel.EndInline((CommandList)cmd);
        else cmd.EndLabel();
    }

    /// <summary>
    /// Records something that does not disturb a pass (a timestamp, a label) where the frame's commands go now: inside a host's rendering into its
    /// list (with secondaries, a secondary of its own in its place), else into the frame's list.
    /// </summary>
    public void Interleave(Action<IGpuCommandList> record)
    {
        if (_passList is { } host && _guest is null)
        {
            if (Frame.Parallel.Open)
            {
                var list = Frame.Parallel.BeginInline("interleave");
                record(list);
                Frame.Parallel.EndInline(list);
            }
            else record(host);

            return;
        }

        EnsureFrame();
        record(Frame.Commands);
    }
}
