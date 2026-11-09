using System.Runtime.InteropServices;

namespace Meitou.Threading;

/// <summary>
/// A point where a long loop that only computes lets a collection in. Under Wine (Proton) the runtime cannot interrupt such a thread, so every
/// collection waits until the loop ends; a yield is a call into the OS where it can stop it. Elsewhere the runtime interrupts the thread itself and
/// this does nothing.
/// </summary>
public static class SafePoint
{
    /// <summary>
    /// Whether this process runs under Wine (its <c>ntdll</c> exports <c>wine_get_version</c>); looked up once.
    /// </summary>
    public static readonly bool Wine = OperatingSystem.IsWindows() && NativeLibrary.TryLoad("ntdll.dll", out var ntdll)
        && NativeLibrary.TryGetExport(ntdll, "wine_get_version", out _);

    /// <summary>
    /// Under Wine, yields on every 256th <paramref name="step"/> of a loop.
    /// </summary>
    public static void Every(int step)
    {
        if (Wine && (step & 255) == 0) Thread.Yield();
    }
}