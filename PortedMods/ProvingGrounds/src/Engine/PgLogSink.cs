using System.Reflection;
using System.Text;
using Meitou.Modding;
using ProvingGrounds.Core;
using ProvingGrounds.Core.Glue;

namespace ProvingGrounds.Engine;

// Port of the Windows half of upstream/ProvingGrounds/plugin/src/PGLog.cpp (task M2.7, spike S14). Core/PGLog.cs builds every byte of every
// line; this is the IPgLogSink it writes through. Engine calls of the C++ and what replaces them:
//   GetModuleFileNameW(__ImageBase) cut after the last \ or / (PGLog.cpp:16-23) ... ModContext.Folder (the mod folder, which holds
//       ProvingGroundsMeitou.dll) with a trailing separator (PgLogGlue.Folder)
//   GetSystemTimeAsFileTime + GetCurrentProcessId in the static-init Sink() (PGLog.cpp:24-25) ... DateTime.UtcNow at Install (the moment
//       the mod's Initialize runs, as the C++ took it when its DLL was loaded) + Environment.ProcessId
//   GetSystemTime (PGLog.cpp:34) ... DateTime.UtcNow
//   _wfopen_s(.., L"ab"), fwrite, fclose per line (PGLog.cpp:42-46) ... File.Open(Append) + Write + close per line, never throws
//   OutputDebugStringA (PGLog.cpp:49) ... ModContext.Log (Meitou's log.txt)
//   (no C++ counterpart) ... every error also goes to ModContext.LogError (task 2.7), so errors show in Meitou's log: glue errors through
//       ModLog.Error and PGConfig's through ModLog.Config (Core/Glue/PgLogRoute), also when pg_debug.log cannot be written
// Writes happen on whatever thread logs (PGLog holds its lock); no caller logs every tick.
// NOTE before M3/M7 add CombatBalanceLog callers: Append opens, writes and closes the file once per LINE, under PGLog's lock. That is fine
// for rare lines (start, config, lifecycle, persistence); a per-hit caller (a combat line per blow) would hold the lock through an open and
// a close every time: give it a kept-open stream (opened once, flushed per line, closed at unload) instead.

/// <summary>The real <see cref="IPgLogSink"/>: the three log files beside the mod's DLL, as the original wrote beside ProvingGrounds.dll.</summary>
sealed class PgLogSink(ModContext c, string directory) : IPgLogSink
{
    static bool s_installed;   // PGLog.Install throws on a second call: once per process (a second Initialize must not reach it)

    public string Directory { get; } = directory;

    public PgSystemTime UtcNow() => PgLogGlue.SystemTime(DateTime.UtcNow);

    // C++: PGLog.cpp:39-46
    public bool Append(string path, string line)
    {
        long perfStart = PgPerf.Begin();   // task 9.2b: the write's time while pg perf is on
        try
        {
            byte[] bytes = Encoding.UTF8.GetBytes(line);
            using var file = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            file.Write(bytes, 0, bytes.Length);
            return true;   // the using closes (and flushes) the file; a throwing close is caught below
        }
        catch (Exception) { return false; }
        finally { PgPerf.FileLineDone(perfStart); }
    }

    // C++: PGLog.cpp:49 (OutputDebugStringA)
    public void Warn(string text)
    {
        try { c.Log(text.TrimEnd('\n')); } catch (Exception) { }
    }

    /// <summary>From the mod's Initialize, first thing: the sink and the session id, then the two startPlugin lines (Plugin.cpp:15-16), once per
    /// process (as the C++ sink is built once, at DLL load). A failure is one log line; the mod runs on with nothing written.</summary>
    internal static void InstallOnce(ModContext c)
    {
        if (s_installed) return;
        s_installed = true;
        ModLog.Route = new PgLogRoute(c.LogError);   // before Install: an error reaches Meitou's log even when Install fails
        try
        {
            var (high, low) = PgLogGlue.FileTime(DateTime.UtcNow);
            PGLog.Install(new PgLogSink(c, PgLogGlue.Folder(c.Folder, Path.DirectorySeparatorChar)), high, low, (uint)Environment.ProcessId);
            PgLogGlue.StartPlugin(BuildStamp());
        }
        catch (Exception e) { c.LogError($"pg_debug.log off: {e.GetType().Name}: {e.Message}"); }
    }

    /// <summary>The build's local date and time (ProvingGroundsMeitou.csproj stamps it), as <c>__DATE__ " " __TIME__</c>; the DLL's write time if
    /// the stamp is missing.</summary>
    static string BuildStamp()
    {
        var asm = typeof(PgLogSink).Assembly;
        string? stamp = asm.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "BuildStamp")?.Value;
        if (stamp is not null && DateTime.TryParse(stamp, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var t)) return PgLogGlue.BuildStamp(t);
        try { return PgLogGlue.BuildStamp(File.GetLastWriteTime(asm.Location)); } catch (Exception) { return "unknown"; }
    }
}

/// <summary>The glue's way to PGLog (task 2.7): <see cref="Debug"/> = PGLog::Debug; <see cref="Error"/> = PGLog::Error and Meitou's error log;
/// <see cref="Config"/> = the same for PGConfig. Any thread.</summary>
static class ModLog
{
    internal static volatile PgLogRoute? Route;   // set by PgLogSink.InstallOnce, first thing in Initialize

    internal static void Debug(string message) => PGLog.Debug(message);

    internal static void Error(string message)
    {
        if (Route is { } r) { r.Error(message); return; }
        PGLog.Error(message);
        try { ProvingGroundsMod.Ctx?.LogError(message); } catch (Exception) { }
    }

    /// <summary>PGConfig's log (PGConfig.cpp calls PGLog::Debug / PGLog::Error).</summary>
    internal static IConfigLog Config => Route ?? (IConfigLog)PGLog.ConfigLog;
}
