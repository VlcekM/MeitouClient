namespace Meitou.Game.Host;

/// <summary>
/// The Metal memory guard's default GPU allocation ceiling (docs/metal-performance-memory.md): a share of the Mac's unified memory, not one number
/// for every Mac. Used when neither <c>--metal-gpu-budget</c> nor the launcher's <c>MEITOU_METAL_GPU_BUDGET_MB</c> names a ceiling.
/// </summary>
static class MetalMemoryBudget
{
    /// <summary>
    /// The least the default is, in MB: what the guard has always defaulted to (sized for a 16 GB M1 Pro), unless the device recommends less.
    /// </summary>
    public const ulong FloorMb = 5120;

    /// <summary>
    /// The most the default is, in MB: the caches have high-water marks of their own, so a larger ceiling only keeps more of the world resident
    /// than a play session visits.
    /// </summary>
    public const ulong CeilingMb = 10240;

    /// <summary>
    /// The default ceiling in MB for a Mac with <paramref name="physicalBytes"/> of memory (0 when unknown) whose device recommends a working set of
    /// <paramref name="recommendedBytes"/> (0 when unknown): a third of the memory in steps of 256 MB, within <see cref="FloorMb"/> and
    /// <see cref="CeilingMb"/> and never above what the device recommends. 16 GB gives 5376, 24 GB 8192 and 32 GB or more 10240.
    /// </summary>
    public static ulong DefaultMb(ulong physicalBytes, ulong recommendedBytes)
    {
        ulong third = physicalBytes / 3 >> 20;
        ulong mb = Math.Clamp(third / 256 * 256, FloorMb, CeilingMb);
        return recommendedBytes > 0 ? Math.Min(mb, recommendedBytes >> 20) : mb;
    }
}
