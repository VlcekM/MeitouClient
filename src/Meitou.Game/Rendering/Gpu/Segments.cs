namespace Meitou.Rendering.Gpu;

/// <summary>
/// Segments (docs/renderer-native.md 8.9): the frame's command list handed to a renderer, with full barriers around its native
/// segments. <see cref="BeginNative"/> opens one outside any pass (a full barrier before and after); <see cref="BeginGuest"/>
/// draws into the open host pass (<see cref="BeginHostPass"/>): its rendering, or with secondaries one of its own in its place.
/// </summary>
public sealed unsafe partial class GpuContext
{
    CommandList? segment;
    // The guest segment open now: the host's list or an inline secondary.
    CommandList? guest;
    enum GuestKind { Host, Inline }
    GuestKind guestKind;

    /// <summary>A native segment is open (<see cref="BeginNative"/> or <see cref="BeginGuest"/>).</summary>
    public bool SegmentOpen => segment is not null || guest is not null;

    /// <summary>
    /// The frame's command list for a segment of the caller's own (renderings, copies, dispatches): what was recorded before is complete and
    /// visible (a full barrier), nothing is assumed bound. <see cref="EndNative"/> closes it with another full barrier.
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
        segment = list;
        return list;
    }

    public void EndNative(IGpuCommandList cmd)
    {
        if (segment is null || !ReferenceEquals(cmd, segment)) throw new InvalidOperationException("EndNative without a matching BeginNative");
        if (PassOpen) throw new InvalidOperationException("EndNative with a host pass open");
        cmd.EndLabel();
        FullBarrier(segment.Handle);
        segment = null;
    }

    /// <summary>
    /// A guest's segment in the open host pass: draws only (no rendering, barriers, copies or dispatches), with the host's targets and state
    /// (<see cref="CurrentTargets"/>, <see cref="CurrentState"/>). Inside a rendering with secondaries it is a secondary of its own, executed in
    /// its place. Without a host pass it throws. Closed by <see cref="EndGuest"/>.
    /// </summary>
    public CommandList BeginGuest(string label)
    {
        if (guest is not null) throw new InvalidOperationException("BeginGuest inside a guest segment");
        if (passList is { } host)
        {
            if (Frame.Parallel.Open)
            {
                guestKind = GuestKind.Inline;
                return guest = Frame.Parallel.BeginInline(label);
            }
            host.Invalidate();
            Frame.Stats.NativeSegments++;
            host.BeginLabel(label);
            guestKind = GuestKind.Host;
            return guest = host;
        }
        throw new InvalidOperationException("BeginGuest without a host pass");
    }

    IGpuCommandList IGpuContext.BeginGuest(string label) => BeginGuest(label);

    public void EndGuest(IGpuCommandList cmd)
    {
        if (guest is null || !ReferenceEquals(cmd, guest)) throw new InvalidOperationException("EndGuest without a matching BeginGuest");
        guest = null;
        if (guestKind == GuestKind.Inline) Frame.Parallel.EndInline((CommandList)cmd);
        else cmd.EndLabel();
    }

    /// <summary>
    /// Records something that does not disturb a pass (a timestamp, a label) where the frame's commands go now: inside a host's rendering into
    /// its list (with secondaries, a secondary of its own in its place), else into the frame's list.
    /// </summary>
    public void Interleave(Action<IGpuCommandList> record)
    {
        if (passList is { } host && guest is null)
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
