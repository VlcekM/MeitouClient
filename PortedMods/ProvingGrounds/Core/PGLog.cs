// Port of upstream/ProvingGrounds/plugin/src/PGLog.h/.cpp (Proving Grounds by Shutori, GPLv3).
using System.Globalization;

namespace ProvingGrounds.Core;

/// <summary>C <c>SYSTEMTIME</c> fields <c>GetSystemTime</c> fills (WORDs), the UTC wall time a log line is stamped with.</summary>
public readonly struct PgSystemTime
{
    public readonly ushort Year, Month, Day, Hour, Minute, Second, Milliseconds;
    public PgSystemTime(ushort year, ushort month, ushort day, ushort hour, ushort minute, ushort second, ushort milliseconds)
    {
        Year = year; Month = month; Day = day; Hour = hour; Minute = minute; Second = second; Milliseconds = milliseconds;
    }
}

/// <summary>What the C++ <c>Sink</c> took from Windows (PGLog.cpp:9-52), implemented by the glue (M2 task 2.7): the folder, the
/// clock and the file append. Core builds every byte of every line; the sink only writes them.
/// <para>Every member is called while PGLog holds its lock: a member must NOT call back into <see cref="PGLog"/> (the lock is
/// reentrant, so a call would recurse into the line being written). A member that throws is contained by PGLog: the throw counts as
/// a failed write (warned once, never rethrown); a throwing <see cref="UtcNow"/> skips the line.</para></summary>
public interface IPgLogSink
{
    /// <summary>The plugin's own folder WITH a trailing separator (PGLog.cpp:16-23: <c>GetModuleFileNameW</c> of the module, cut after
    /// the last <c>\</c> or <c>/</c>). Empty when it could not be resolved, which is also when nothing can be written. The glue
    /// returns the mod folder. Constant for the process (the C++ resolves it once, in the static-init constructor).</summary>
    string Directory { get; }
    /// <summary><c>GetSystemTime</c> (UTC, milliseconds).</summary>
    PgSystemTime UtcNow();
    /// <summary>PGLog.cpp:43-46: open <paramref name="path"/> for binary append (<c>_wfopen_s(.., L"ab")</c>), write
    /// <paramref name="line"/> as its UTF-8 bytes, close (flushing each event). True only when the open succeeded, every byte was
    /// written and the close succeeded; false otherwise (never throws).</summary>
    bool Append(string path, string line);
    /// <summary><c>OutputDebugStringA(text)</c>: the one-time warning that a log could not be written (Meitou: its own log).</summary>
    void Warn(string text);
}

/// <summary>The original's three log files (PGLog.h): <c>pg_debug.log</c> (DEBUG / ERROR text lines) and the JSON-lines channels
/// <c>pg_combat_debug.log</c> (ambient NPC bouts, markets, the diagnostic run) and <c>pg_challenge_debug.log</c> (player
/// challenges). Every line is stamped with the UTC time and the process session id; the two JSON channels share the session id.
/// <see cref="NextId"/> is the process-wide id source for <c>match_id</c> / <c>market_id</c> / <c>challenge_id</c>; the per-line
/// <c>record</c> field is CombatBalanceLog's own counter. The C++ <c>Sink</c> is a namespace-scope object built during static init;
/// here the glue calls <see cref="Install"/> once at startup. Until then nothing is written (no line, no warning). Safe to call from
/// any thread (the C++ guards every call with a critical section).</summary>
public static class PGLog
{
    /// <summary>The C++ anonymous-namespace <c>Sink sink</c> (PGLog.cpp:9-30), minus what the glue owns.</summary>
    internal sealed class State
    {
        // CRITICAL_SECTION lock -> a monitor on this object.
        public readonly object Lock = new object();
        public IPgLogSink? Sink;
        public string Session = "";
        public uint Sequence;
        public bool Warned;

        // C++: PGLog.cpp:14 (sequence 0, warned false; directory and session filled by the constructor = Install here)
        public static State Shipped() => new State();

        public State Clone() => new State { Sink = Sink, Session = Session, Sequence = Sequence, Warned = Warned };
    }

    static readonly State g_state = State.Shipped();
    // Test seam (no C++ counterpart): a state visible only to the flow that set it, so parallel tests never share a sink.
    static readonly AsyncLocal<State?> s_test = new AsyncLocal<State?>();
    internal static State Current => s_test.Value ?? g_state;

    /// <summary>Test hook: <see cref="Current"/> is <paramref name="state"/> in the calling flow until disposed.</summary>
    internal static IDisposable UseForTest(State state)
    {
        State? previous = s_test.Value;
        s_test.Value = state;
        return new Restore(previous);
    }

    sealed class Restore : IDisposable
    {
        readonly State? _previous;
        bool _done;
        public Restore(State? previous) { _previous = previous; }
        public void Dispose()
        {
            if (_done) return;
            _done = true;
            s_test.Value = _previous;
        }
    }

    /// <summary>The <c>Sink()</c> constructor's session id (PGLog.cpp:24-25): <c>sprintf_s(id, "%08lx%08lx-%lu",
    /// time.dwHighDateTime, time.dwLowDateTime, GetCurrentProcessId())</c>, where <c>time</c> is <c>GetSystemTimeAsFileTime</c>
    /// (100 ns ticks since 1601-01-01 UTC, split into two DWORDs) taken once at startup. The glue passes those three values.</summary>
    // C++: PGLog.cpp:24-25
    public static string SessionId(uint fileTimeHigh, uint fileTimeLow, uint processId) =>
        fileTimeHigh.ToString("x8", CultureInfo.InvariantCulture) + fileTimeLow.ToString("x8", CultureInfo.InvariantCulture) + "-" +
        processId.ToString(CultureInfo.InvariantCulture);

    /// <summary>The glue's stand-in for the C++ static-init <c>Sink()</c> constructor: the sink (folder, clock, append) and the
    /// three values of <see cref="SessionId"/>. Call once at startup, before anything logs; a second call throws
    /// <see cref="InvalidOperationException"/> (the session id must not change mid-process). Tests call <see cref="UseForTest"/> with
    /// a fresh <see cref="State"/> BEFORE Install, so the process-wide state is never installed by a test.</summary>
    // C++: PGLog.cpp:14-26
    public static void Install(IPgLogSink sink, uint fileTimeHigh, uint fileTimeLow, uint processId)
    {
        ArgumentNullException.ThrowIfNull(sink);
        State s = Current;
        lock (s.Lock)
        {
            if (s.Sink != null) throw new InvalidOperationException("PGLog.Install was already called; the session id is fixed for the process.");
            s.Sink = sink;
            s.Session = SessionId(fileTimeHigh, fileTimeLow, processId);
        }
    }

    // C++: PGLog.cpp:33-38 (a throwing clock returns null: the caller skips the line, PGLog.cs containment)
    static string? Timestamp(IPgLogSink sink)
    {
        PgSystemTime t;
        try { t = sink.UtcNow(); }
        catch (Exception) { return null; }
        CultureInfo ci = CultureInfo.InvariantCulture;
        // "%04u-%02u-%02uT%02u:%02u:%02u.%03uZ"
        return t.Year.ToString("D4", ci) + "-" + t.Month.ToString("D2", ci) + "-" + t.Day.ToString("D2", ci) + "T" +
            t.Hour.ToString("D2", ci) + ":" + t.Minute.ToString("D2", ci) + ":" + t.Second.ToString("D2", ci) + "." +
            t.Milliseconds.ToString("D3", ci) + "Z";
    }

    // C++: PGLog.cpp:39-51. A sink member that throws counts as a failed write (the C++ calls cannot throw); line null = the
    // clock failed, nothing to write.
    static void Append(State s, string name, string? line)
    {
        IPgLogSink sink = s.Sink!;
        if (line != null)
        {
            try
            {
                string directory = sink.Directory;
                if (!string.IsNullOrEmpty(directory) && sink.Append(directory + name, line)) return;
            }
            catch (Exception) { }
        }
        if (!s.Warned)
        {
            s.Warned = true;
            try { sink.Warn("Proving Grounds: could not write pg_debug.log, pg_combat_debug.log or pg_challenge_debug.log beside ProvingGrounds.dll in the mod folder\n"); }
            catch (Exception) { }
        }
    }

    /// <summary>JSON string quoting: <c>"</c> and <c>\</c> get a backslash, a byte below 32 becomes <c>\u%04x</c> (lowercase hex),
    /// everything else is copied as is (DEL and non-ASCII included, unescaped; the sink writes them as UTF-8, the bytes the C++
    /// copied from a UTF-8 std::string).</summary>
    // C++: PGLog.cpp:54-63
    public static string Quote(string? value)
    {
        value ??= "";
        var out_ = new System.Text.StringBuilder(value.Length + 2);
        out_.Append('"');
        for (int i = 0; i < value.Length; ++i)
        {
            char c = value[i];
            if (c == '"' || c == '\\') { out_.Append('\\'); out_.Append(c); }
            else if (c < 32) out_.Append("\\u").Append(((uint)c).ToString("x4", CultureInfo.InvariantCulture));
            else out_.Append(c);
        }
        return out_.Append('"').ToString();
    }

    // C++: PGLog.cpp:64-67
    public static void Debug(string message)
    {
        State s = Current;
        lock (s.Lock)
        {
            if (s.Sink == null) return; // not installed yet (the C++ sink always exists)
            string? time = Timestamp(s.Sink);
            Append(s, "pg_debug.log", time == null ? null : time + " [" + s.Session + "] DEBUG " + message + "\r\n");
        }
    }

    // C++: PGLog.cpp:68-71
    public static void Error(string message)
    {
        State s = Current;
        lock (s.Lock)
        {
            if (s.Sink == null) return;
            string? time = Timestamp(s.Sink);
            Append(s, "pg_debug.log", time == null ? null : time + " [" + s.Session + "] ERROR " + message + "\r\n");
        }
    }

    // The JSON envelope is identical on every channel; only the destination
    // differs. The session id is shared, so lines from the two files can be
    // ordered against each other on (session, record).
    // C++: PGLog.cpp:75-77
    static string? Envelope(State s, string fields)
    {
        string? time = Timestamp(s.Sink!);
        return time == null ? null : "{\"utc\":" + Quote(time) + ",\"session\":" + Quote(s.Session) + "," + fields + "}\r\n";
    }

    /// <summary>Ambient NPC bouts, their markets, and the controlled diagnostic run (<c>pg_combat_debug.log</c>).</summary>
    // C++: PGLog.cpp:78-81
    public static void Combat(string fields)
    {
        State s = Current;
        lock (s.Lock)
        {
            if (s.Sink == null) return;
            Append(s, "pg_combat_debug.log", Envelope(s, fields));
        }
    }

    /// <summary>Player-vs-NPC challenges, in their own file (<c>pg_challenge_debug.log</c>).</summary>
    // C++: PGLog.cpp:82-85
    public static void Challenge(string fields)
    {
        State s = Current;
        lock (s.Lock)
        {
            if (s.Sink == null) return;
            Append(s, "pg_challenge_debug.log", Envelope(s, fields));
        }
    }

    /// <summary>The process-wide monotonic id source (unsigned, wraps) for <c>match_id</c> / <c>market_id</c> / <c>challenge_id</c>
    /// on both channels (the per-line <c>record</c> field is CombatBalanceLog's own counter). Counts even before
    /// <see cref="Install"/>, like the C++ sink which always exists.</summary>
    // C++: PGLog.cpp:86
    public static uint NextId()
    {
        State s = Current;
        lock (s.Lock) { return unchecked(++s.Sequence); }
    }

    /// <summary>The plugin folder with a trailing separator (the sink's), "" before <see cref="Install"/>.</summary>
    // C++: PGLog.cpp:87
    public static string Directory
    {
        get
        {
            State s = Current;
            lock (s.Lock)
            {
                try { return s.Sink?.Directory ?? ""; }
                catch (Exception) { return ""; }
            }
        }
    }

    /// <summary>PGConfig's <see cref="IConfigLog"/> routed to <see cref="Debug"/> / <see cref="Error"/> unchanged: PGConfig.cpp calls
    /// <c>PGLog::Debug</c> / <c>PGLog::Error</c> directly. The glue passes this to <c>PGConfig.Load</c> and
    /// <c>PGConfig.SaveEditableSettings</c>.</summary>
    public static readonly IConfigLog ConfigLog = new ConfigLogRoute();

    // No C++ counterpart: PGConfig.cpp calls PGLog::Debug / PGLog::Error (PGLog.cpp:64-71) directly.
    sealed class ConfigLogRoute : IConfigLog
    {
        public void Debug(string message) => PGLog.Debug(message);
        public void Error(string message) => PGLog.Error(message);
    }
}
