using System.Globalization;
using Meitou.Rendering.Gpu.Core;

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
    static readonly Lock gate = new();
    static ulong peakUsed, budget;
    readonly CancellationTokenSource stop = new();
    readonly Thread thread;

    VramWatch(VulkanDevice device, double limit)
    {
        thread = new Thread(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                var (used, total) = device.VideoMemory();
                lock (gate) { peakUsed = Math.Max(peakUsed, used); budget = total; }
                if (limit > 0 && total > 0 && used > limit * total)
                {
                    Console.WriteLine($"vram      ABORT: {used / 1048576.0:0} MB in use of a {total / 1048576.0:0} MB budget ({100.0 * used / total:0.0}%, limit {limit * 100:0}%)");
                    Console.Out.Flush();
                    Console.Error.Flush();
                    Terminate(9);
                }
                stop.Token.WaitHandle.WaitOne(100);
            }
        }) { IsBackground = true, Name = "vram watch", Priority = ThreadPriority.AboveNormal };
        thread.Start();
    }

    /// <summary>Ends the process at once. <c>Environment.Exit</c> unloads the driver's DLLs, and with the render thread inside the driver near the
    /// budget that unload can deadlock: the process then stays alive holding its video memory, and the next runs abort too. The OS frees all of
    /// it when the process ends; the GPU objects can't be released here anyway, since the render thread is still using them.</summary>
    static void Terminate(int code)
    {
        if (OperatingSystem.IsWindows()) TerminateProcess(GetCurrentProcess(), (uint)code);
        Environment.Exit(code);
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    static extern nint GetCurrentProcess();

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    static extern bool TerminateProcess(nint process, uint exitCode);

    public static VramWatch Start(VulkanDevice device)
    {
        double limit = double.TryParse(Environment.GetEnvironmentVariable("MEITOU_VRAM_KILL"), CultureInfo.InvariantCulture, out var v) ? v : 0.95;
        return new VramWatch(device, limit);
    }

    /// <summary>The peak device-local use seen so far and the budget.</summary>
    public static string Describe()
    {
        lock (gate) return $"peak {peakUsed / 1048576.0:0} MB in use of a {budget / 1048576.0:0} MB budget ({(budget > 0 ? 100.0 * peakUsed / budget : 0):0.0}%)";
    }

    public void Dispose()
    {
        stop.Cancel();
        thread.Join();
        stop.Dispose();
    }
}
