using System.Diagnostics;

namespace Meitou.Rendering;

/// <summary>
/// <c>%TEMP%\meitou-gpu.lock</c>: bench runs hold it while they measure (not while they load) so concurrent runs queue instead of disturbing each
/// other's numbers. The holder keeps the file open without write sharing and writes its PID in it, so a crashed holder frees it by itself (the
/// OS closes its handle); a PID left in the file by one is reported as a stale lock when the next run takes it over.
/// </summary>
public sealed class GpuLock : IDisposable
{
    FileStream? stream;

    /// <summary>Tests point the lock at a file of their own so they never touch a real run's.</summary>
    internal static string? PathOverride;

    public static string FilePath => PathOverride ?? Path.Combine(Path.GetTempPath(), "meitou-gpu.lock");

    /// <summary>How long <see cref="Acquire"/> waited.</summary>
    public TimeSpan Waited { get; private set; }

    /// <summary>The lock was left behind by a process that no longer exists (its PID).</summary>
    public int? StaleHolder { get; private set; }

    /// <summary>Waits for the lock (printing who holds it) and takes it.</summary>
    public static GpuLock Acquire(TextWriter log)
    {
        var watch = Stopwatch.StartNew();
        bool told = false;
        var lastTold = TimeSpan.Zero;
        while (true)
        {
            try
            {
                var fs = new FileStream(FilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
                int? stale = null;
                string previous = new StreamReader(fs, leaveOpen: true).ReadToEnd().Trim();
                if (int.TryParse(previous, out int pid) && pid != Environment.ProcessId && !Alive(pid)) stale = pid;
                fs.SetLength(0);
                fs.Position = 0;
                using (var w = new StreamWriter(fs, leaveOpen: true)) w.Write(Environment.ProcessId.ToString());
                fs.Flush();
                return new GpuLock { stream = fs, Waited = watch.Elapsed, StaleHolder = stale };
            }
            catch (IOException)
            {
                if (!told || watch.Elapsed - lastTold > TimeSpan.FromSeconds(30))
                {
                    log.WriteLine($"gpu-lock  waiting for the GPU lock held by PID {Holder()?.ToString() ?? "?"} ({FilePath})");
                    told = true;
                    lastTold = watch.Elapsed;
                }
                Thread.Sleep(250);
            }
        }
    }

    static int? Holder()
    {
        try
        {
            using var fs = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return int.TryParse(new StreamReader(fs).ReadToEnd().Trim(), out int pid) ? pid : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    static bool Alive(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    public void Dispose()
    {
        if (stream is null) return;
        stream.Dispose();
        stream = null;
        try { File.Delete(FilePath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
