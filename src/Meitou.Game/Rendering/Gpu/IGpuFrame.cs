namespace Meitou.Rendering.Gpu;

/// <summary>
/// The frame's neutral surface (docs/resource-layer.md): what a renderer may ask of the frame regardless of
/// the backend. The Vulkan frame implements it; the Metal one has its own (a slot ring over shared buffers).
/// The backend-only parts (the descriptor pools, the parallel secondaries, the host-visible allocators)
/// stay on the concrete frame.
/// </summary>
public interface IGpuFrame
{
    /// <summary>The frame number (FrameRing.FrameNumber); 0 before the first.</summary>
    long Number { get; }

    /// <summary>The slot the frame rotates through (the frames-in-flight index).</summary>
    int Slot { get; }

    /// <summary>Whether the frame is open for recording (between the context's BeginFrame and EndFrame).</summary>
    bool Open { get; }

    /// <summary>The frame's primary command list.</summary>
    IGpuCommandList Commands { get; }

    /// <summary>The commands recorded ahead of the frame's passes (uploads, bakes, GPU culls); Metal records them into the frame's own list before its passes.</summary>
    IGpuCommandList PreFrame { get; }

    /// <summary>The frame's resource states (the neutral sync model's bookkeeping).</summary>
    ResourceStates States { get; }

    /// <summary>The frame's statistics (the passes', draws' and dispatches' counters).</summary>
    GpuStats Stats { get; }

    /// <summary>Writes bytes into the frame's constant ring (the default blocks' slices), aligned; the slice lives for the frame.</summary>
    Transient WriteConstants(ReadOnlySpan<byte> data, ulong alignment = 16);

    /// <summary>Reserves bytes in the frame's constant ring without a source copy: the caller fills the slice (matrices,
    /// instances). The same lifetime and alignment rules as <see cref="WriteConstants"/>; the contents are undefined.</summary>
    Transient AllocateConstants(ulong size, ulong alignment = 16);

    /// <summary>The frame's timestamp queries (a pass's two stamps, read when the frame's slot comes round).</summary>
    IGpuTimestamps Timestamps { get; }
}
