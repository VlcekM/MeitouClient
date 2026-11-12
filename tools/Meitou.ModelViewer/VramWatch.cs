using System.Globalization;
using Meitou.Game.Rendering.Gpu.Core;

namespace Meitou.ModelViewer;

/// <summary>
/// Headless runs (<c>--screenshot</c>, <c>--fly-benchmark</c>) watch the device-local memory in use of the driver's budget
/// (<see cref="VulkanDevice.VideoMemory"/>, VK_EXT_memory_budget) on a background thread and end the process when it passes a limit, so a large
/// range cannot take the driver down (a device-lost reset) while measuring; the exit code is then 9. Interactive runs have no watch (the
/// memory-pressure guard, <c>VramGuard</c>, shortens the ranges instead). <c>MEITOU_VRAM_KILL</c> is the limit as a fraction of the budget
/// (default 0.95, 0 turns it off). It also keeps the peak, which the benchmark prints on its <c>vram</c> line.
/// </summary>
sealed class VramWatch : IDisposable
{
    /// <summary>
    /// Guards the peak and the budget, written by the watch thread.
    /// </summary>
    static readonly Lock Gate = new();

    /// <summary>
    /// The highest device-local use seen and the last budget, bytes.
    /// </summary>
    static ulong s_peakUsed, s_budget;

    /// <summary>
    /// Stops the watch thread.
    /// </summary>
    readonly CancellationTokenSource _stop = new();

    /// <summary>
    /// The thread that reads the video memory every 100 ms.
    /// </summary>
    readonly Thread _thread;

    /// <summary>
    /// Starts the watch thread: it keeps the peak and ends the process (exit code 9) when the use passes <paramref name="limit"/> times the budget
    /// (0: never).
    /// </summary>
    VramWatch(VulkanDevice device, double limit)
    {
        _thread = new Thread(() =>
        {
            while (!_stop.IsCancellationRequested)
            {
                var (used, total) = device.VideoMemory();
                lock (Gate)
                {
                    s_peakUsed = Math.Max(s_peakUsed, used);
                    s_budget = total;
                }
                if (limit > 0 && total > 0 && used > limit * total)
                {
                    Console.WriteLine($"vram      ABORT: {used / 1048576.0:0} MB in use of a {total / 1048576.0:0} MB budget "
                        + $"({100.0 * used / total:0.0}%, limit {limit * 100:0}%)");
                    Console.Out.Flush();
                    Console.Error.Flush();
                    Terminate(9);
                }
                _stop.Token.WaitHandle.WaitOne(100);
            }
        }) { IsBackground = true, Name = "vram watch", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    /// <summary>
    /// Ends the process at once. <c>Environment.Exit</c> unloads the driver's DLLs, and with the render thread inside the driver near the budget that
    /// unload can deadlock: the process then stays alive holding its video memory, and the next runs abort too. The OS frees all of it when the
    /// process ends; the GPU objects can't be released here anyway, since the render thread is still using them.
    /// </summary>
    static void Terminate(int code)
    {
        if (OperatingSystem.IsWindows()) TerminateProcess(GetCurrentProcess(), (uint)code);
        Environment.Exit(code);
    }

    /// <summary>
    /// The current process's pseudo handle (Windows).
    /// </summary>
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    static extern nint GetCurrentProcess();

    /// <summary>
    /// Ends a process at once (Windows).
    /// </summary>
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    static extern bool TerminateProcess(nint process, uint exitCode);

    /// <summary>
    /// Starts a watch with the limit of <c>MEITOU_VRAM_KILL</c> (default 0.95).
    /// </summary>
    public static VramWatch Start(VulkanDevice device)
    {
        double limit = double.TryParse(Environment.GetEnvironmentVariable("MEITOU_VRAM_KILL"), CultureInfo.InvariantCulture, out var v) ? v : 0.95;
        return new VramWatch(device, limit);
    }

    /// <summary>
    /// The peak device-local use seen so far and the budget.
    /// </summary>
    public static string Describe()
    {
        lock (Gate)
        {
            return $"peak {s_peakUsed / 1048576.0:0} MB in use of a {s_budget / 1048576.0:0} MB budget "
                + $"({(s_budget > 0 ? 100.0 * s_peakUsed / s_budget : 0):0.0}%)";
        }
    }

    /// <summary>
    /// Stops the watch thread and waits for it.
    /// </summary>
    public void Dispose()
    {
        _stop.Cancel();
        _thread.Join();
        _stop.Dispose();
    }
}
