namespace Meitou.Game.Rendering.Foliage;

/// <summary>
/// Whether the Metal path culls foliage on the GPU (docs/metal-gpu-cull-plan.md): on by default; <c>MEITOU_METAL_GPU_CULL=0</c> (or
/// <c>false</c>) or <c>MEITOU_METAL_CPU_CULL=1</c> keeps the CPU cull, the reference path. The one reading of both variables, so the foliage
/// renderer, the Metal frame's pre-frame command buffer and the host agree.
/// </summary>
static class MetalGpuCull
{
    /// <summary>
    /// The GPU cull is wanted on Metal (read once at start).
    /// </summary>
    public static bool Enabled { get; } = Environment.GetEnvironmentVariable("MEITOU_METAL_GPU_CULL") is not ("0" or "false")
        && Environment.GetEnvironmentVariable("MEITOU_METAL_CPU_CULL") != "1";
}
