using System.Runtime.InteropServices;

namespace Meitou.Game.Rendering.Display;

/// <summary>
/// What Windows says of its HDR displays (docs/render-post.md "HDR output"): the brightness it shows SDR white at, which the HDR picture's white follows so the game is as
/// bright as the desktop beside it.
/// </summary>
static unsafe partial class WindowsHdr
{
    const uint ActivePaths = 2;   // QDC_ONLY_ACTIVE_PATHS
    const int PathSize = 72, ModeSize = 64, TargetOffset = 20;   // DISPLAYCONFIG_PATH_INFO, DISPLAYCONFIG_MODE_INFO, the path's targetInfo (adapter LUID, then id)
    const int AdvancedColourInfo = 9, SdrWhiteLevel = 11;        // DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO, ..._GET_SDR_WHITE_LEVEL

    [LibraryImport("user32.dll")] private static partial int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
    [LibraryImport("user32.dll")] private static partial int QueryDisplayConfig(uint flags, ref uint paths, byte* pathArray, ref uint modes, byte* modeArray, nint topology);
    [LibraryImport("user32.dll")] private static partial int DisplayConfigGetDeviceInfo(byte* packet);

    static long s_readAt = long.MinValue;
    static float? s_white;

    /// <summary>
    /// The brightness, in nits, Windows shows SDR white at on its first display with HDR turned on (Settings, Display, HDR, "SDR content brightness"); null off Windows, with
    /// no such display or when the query fails. Asked of Windows at most once a second, so the slider there is followed while the game runs.
    /// </summary>
    public static float? SdrWhiteNits()
    {
        if (!OperatingSystem.IsWindows()) return null;

        long now = Environment.TickCount64;
        if (now - s_readAt < 1000) return s_white;

        s_readAt = now;
        return s_white = Query();
    }

    static float? Query()
    {
        try
        {
            if (GetDisplayConfigBufferSizes(ActivePaths, out uint pathCount, out uint modeCount) != 0 || pathCount == 0) return null;

            byte[] paths = new byte[pathCount * PathSize], modes = new byte[Math.Max(modeCount, 1) * ModeSize];
            fixed (byte* p = paths, m = modes)
            {
                if (QueryDisplayConfig(ActivePaths, ref pathCount, p, ref modeCount, m, 0) != 0) return null;

                for (int i = 0; i < pathCount; i++)
                {
                    byte* target = p + i * PathSize + TargetOffset;
                    // Header: type, size, the adapter's LUID, the target's id; then the answer.
                    byte* colour = stackalloc byte[32];
                    Header(colour, AdvancedColourInfo, 32, target);
                    if (DisplayConfigGetDeviceInfo(colour) != 0 || (*(uint*)(colour + 20) & 2) == 0) continue;   // bit 1: advanced colour (HDR) enabled

                    byte* white = stackalloc byte[24];
                    Header(white, SdrWhiteLevel, 24, target);
                    if (DisplayConfigGetDeviceInfo(white) != 0) continue;

                    return *(uint*)(white + 20) / 1000f * 80;   // in thousandths of 80 nits
                }
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
        }

        return null;
    }

    static void Header(byte* packet, int type, int size, byte* target)
    {
        new Span<byte>(packet, size).Clear();
        *(int*)packet = type;
        *(int*)(packet + 4) = size;
        *(long*)(packet + 8) = *(long*)target;
        *(uint*)(packet + 16) = *(uint*)(target + 8);
    }
}
