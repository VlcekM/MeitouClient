using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Post;

namespace Meitou.Game.Rendering.Upscalers;

/// <summary>
/// The vendor upscalers on a <see cref="GpuContext"/>, for <see cref="PostProcess.UpscalerFactory"/>: null (and a note why) when one is not
/// available.
/// </summary>
public static class VendorUpscalers
{
    /// <summary>
    /// Meitou's own DLSS project id (any GUID; engine type custom) and engine version, the same for Streamline and NGX.
    /// </summary>
    internal const string DlssProjectId = "6e8a8f8c-6f1b-4b8e-9a51-6b2d5c9b4e21", DlssEngineVersion = "0.3";

    /// <summary>
    /// MEITOU_DLSS_PRESET: DLSS's render preset for every mode over the settings' (0 default; the letters from 1, e.g. 11 = K, 5 = E): Streamline and
    /// NGX number them alike.
    /// </summary>
    static readonly uint? DlssPresetOverride = uint.TryParse(Environment.GetEnvironmentVariable("MEITOU_DLSS_PRESET"), out var p) ? p : null;

    /// <summary>
    /// The preset DLSS runs: MEITOU_DLSS_PRESET when set, else the settings' <paramref name="chosen"/>.
    /// </summary>
    internal static uint DlssPreset(uint chosen) => DlssPresetOverride ?? chosen;

    /// <summary>
    /// The folder of the DLSS runtime (Streamline's DLLs on Windows, the DLSS model on Linux): <c>MEITOU_DLSS_PATH</c>, else the older
    /// <c>MEITOU_STREAMLINE_PATH</c>; null when neither is set (the executable's folder is searched either way).
    /// </summary>
    public static string? DlssPath =>
        Environment.GetEnvironmentVariable("MEITOU_DLSS_PATH") is { Length: > 0 } path
            ? path
            : Environment.GetEnvironmentVariable("MEITOU_STREAMLINE_PATH") is { Length: > 0 } old
                ? old
                : null;

    /// <summary>
    /// Makes the upscaler of a kind: FSR, or DLSS through NGX (Linux, or with <paramref name="ngx"/>) else Streamline; null, with the reason logged,
    /// when it cannot be made.
    /// </summary>
    /// <param name="streamline">Streamline, when it was initialised before the device (DLSS on Windows); null otherwise.</param>
    /// <param name="ngx">NGX, when it was initialised before the device (DLSS on Linux, and on Windows with frame generation); null
    /// otherwise.</param>
    public static Func<UpscalerKind, IUpscaler?> Factory(GpuContext ctx, Streamline? streamline, Ngx? ngx = null) => kind =>
    {
        IUpscaler? upscaler = null;
        string? reason = null;

        if (kind == UpscalerKind.Fsr)
            upscaler = FsrUpscaler.TryCreate(ctx, out reason);
        else if (kind == UpscalerKind.Dlss)
        {
            upscaler = OperatingSystem.IsLinux() || ngx is not null
                ? NgxDlssUpscaler.TryCreate(ngx, ctx, out reason)
                : DlssUpscaler.TryCreate(streamline, ctx, out reason);
        }

        if (upscaler is null) Console.WriteLine($"upscaler  {reason}");
        return upscaler;
    };
}
