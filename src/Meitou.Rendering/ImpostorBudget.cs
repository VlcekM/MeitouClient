using System.Globalization;

namespace Meitou.Rendering;

/// <summary>
/// How much video memory the impostor atlases may hold (docs/impostors.md section 10, renderer-native.md 8.16). The default follows the card:
/// a share of the device-local budget the driver reports (<c>VulkanDevice.VideoMemory</c>, the same figure <see cref="VramGuard"/> works from, so
/// the atlases and everything else give way together), clamped between a floor and a cap, never more than <see cref="MaxShare"/> of the budget,
/// and three quarters of that while the guard is under pressure. An explicit <c>--impostor-budget</c> / <c>MEITOU_IMPOSTOR_BUDGET_MB</c> replaces
/// the rule (the pressure factor still applies).
/// <para>
/// <b>Integrated GPUs</b> share the system's memory: the "device-local" heap is system RAM (Intel) or a small carve-out (some AMD APUs), and the
/// budget the driver reports is what <i>every</i> allocation of this process may use, so there the share and the cap are smaller
/// (<see cref="IntegratedShare"/>, <see cref="IntegratedCapMb"/>): the atlases never take a large part of a small or shared budget. The atlases
/// themselves stretch further than their size suggests because each holds only the mip levels its nearest instance can sample (far mips).
/// </para>
/// </summary>
public static class ImpostorBudget
{
    /// <summary>The share of the device-local budget, for a discrete and an integrated GPU.</summary>
    public const double Share = 0.08, IntegratedShare = 0.05;
    /// <summary>The most the default may be (MB), for a discrete and an integrated GPU: all 267 base-game atlases at full detail are 1637 MB; far mips need far less.</summary>
    public const double CapMb = 1024, IntegratedCapMb = 256;
    /// <summary>The least the default may be (MB), unless that is more than <see cref="MaxShare"/> of the budget.</summary>
    public const double FloorMb = 48;
    /// <summary>The default never exceeds this share of the budget, whatever the floor says.</summary>
    public const double MaxShare = 0.12;
    /// <summary>The budget when the card's is not known (before the guard's first sample, or no VK_EXT_memory_budget): the fixed 192 MB of before.</summary>
    public const double UnknownMb = 192;
    /// <summary>The factor on the limit while the guard is under pressure.</summary>
    public const double PressureFactor = 0.75;

    /// <summary>The default budget (MB) for a device-local budget of <paramref name="deviceBudgetBytes"/> (0: unknown).</summary>
    public static double DefaultMb(long deviceBudgetBytes, bool integrated)
    {
        if (deviceBudgetBytes <= 0) return UnknownMb;
        double mb = deviceBudgetBytes / 1048576.0;
        double value = Math.Clamp(mb * (integrated ? IntegratedShare : Share), FloorMb, integrated ? IntegratedCapMb : CapMb);
        return Math.Min(value, mb * MaxShare);
    }

    /// <summary>The limit in force (MB): <paramref name="overrideMb"/> or the default, three quarters of it under pressure.</summary>
    public static double LimitMb(double? overrideMb, long deviceBudgetBytes, bool integrated, bool pressure) =>
        (overrideMb is > 0 ? overrideMb.Value : DefaultMb(deviceBudgetBytes, integrated)) * (pressure ? PressureFactor : 1);

    /// <summary><c>MEITOU_IMPOSTOR_BUDGET_MB</c>, or null.</summary>
    public static double? FromEnvironment() =>
        double.TryParse(Environment.GetEnvironmentVariable("MEITOU_IMPOSTOR_BUDGET_MB"), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && v > 0 ? v : null;
}
