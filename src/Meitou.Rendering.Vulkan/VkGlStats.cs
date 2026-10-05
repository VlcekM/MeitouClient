namespace Meitou.Rendering.Vulkan;

/// <summary>
/// Counters of the translation. They only ever count up (a mid-frame flush starts a new frame, so the per-frame values are differences to
/// the values at <see cref="BeginFrame"/>); <see cref="Snapshot"/> copies them for the viewer's pass meter (<c>MEITOU_PASS_STATS=1</c>,
/// docs/engine.md "Frame cost breakdown"). Plain increments: the cost is not measurable next to a draw.
/// </summary>
public sealed class VkGlStats
{
    // What the frame did.
    public long Draws, RenderPasses, PipelinesCreated, BuffersRenamed, Uploads, Flushes, UploadBytes, DescriptorPushes, UniformBytes;
    // What it took to record it (the Vulkan side).
    public long PipelineBinds, DynamicStateCalls, Set1Binds, UniformCopies, DescriptorWrites, PushedTextures, PushSkips, VertexBufferBinds, IndexBufferBinds, Barriers;
    // The GL calls the renderers made.
    public long StateCalls, UniformCalls, TextureBinds, BindCalls, AttribCalls;
    // Stopwatch ticks: preparing draws (pipelines, state, descriptors, vertex buffers), creating pipelines, waiting for a free frame, submitting, acquiring, presenting.
    public long DrawTicks, PipelineCreateTicks, FenceWaitTicks, SubmitTicks, AcquireTicks, QueuePresentTicks;
    /// <summary>With <c>MEITOU_VKGL_PHASES=1</c> (<see cref="VkGl.Phases"/>): ticks of the parts of a draw's preparation, by <see cref="PhaseNames"/>.</summary>
    public readonly long[] PhaseTicks = new long[PhaseNames.Length];
    /// <summary>With the same switch: the part of each phase spent inside the <c>vkCmd*</c> calls (a subset of <see cref="PhaseTicks"/>).</summary>
    public readonly long[] NativeTicks = new long[PhaseNames.Length];
    public static readonly string[] PhaseNames =
        ["layout + key", "pipeline lookup", "bind pipeline", "dynamic state", "set 1 (loose uniforms)", "set 0 build + compare", "push descriptors", "vertex buffers", "index bind + draw call"];
    /// <summary>The last completed frame's GPU time, start of its uploads to the end of its last pass (not reset per frame).</summary>
    public double GpuFrameMs;

    public static readonly string[] CounterNames =
    [
        "draws", "passes", "new pipelines", "buffer renames", "uploads", "flushes", "upload bytes", "descriptor pushes", "uniform bytes",
        "pipeline binds", "dynamic state cmds", "set 1 binds", "uniform ring copies", "descriptor writes", "pushed textures", "push skipped", "vertex buffer binds", "index buffer binds", "barriers",
        "GL state calls", "GL uniform calls", "GL texture binds", "GL bind calls", "GL attrib calls",
        "draw prep ticks", "pipeline create ticks", "fence wait ticks", "submit ticks", "acquire ticks", "present ticks",
    ];
    public const int CounterCount = 30;

    /// <summary>All counters, in <see cref="CounterNames"/> order.</summary>
    public void Snapshot(Span<long> into)
    {
        ReadOnlySpan<long> all =
        [
            Draws, RenderPasses, PipelinesCreated, BuffersRenamed, Uploads, Flushes, UploadBytes, DescriptorPushes, UniformBytes,
            PipelineBinds, DynamicStateCalls, Set1Binds, UniformCopies, DescriptorWrites, PushedTextures, PushSkips, VertexBufferBinds, IndexBufferBinds, Barriers,
            StateCalls, UniformCalls, TextureBinds, BindCalls, AttribCalls,
            DrawTicks, PipelineCreateTicks, FenceWaitTicks, SubmitTicks, AcquireTicks, QueuePresentTicks,
        ];
        all.CopyTo(into);
    }

    readonly long[] frameBase = new long[CounterCount];
    internal void BeginFrame() => Snapshot(frameBase);

    public override string ToString()
    {
        var now = new long[CounterCount];
        Snapshot(now);
        long D(int i) => now[i] - frameBase[i];
        static double Ms(long ticks) => ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        return $"{D(0)} draws, {D(1)} passes, {D(2)} new pipelines, {D(4)} uploads ({D(6) / 1024} KB), {D(3)} renames, {D(8) / 1024} KB uniforms, {D(5)} flushes, {D(7)} pushes, draw prep {Ms(D(24)):0.00} ms, gpu frame {GpuFrameMs:0.00} ms";
    }
}
