using System.Globalization;
using System.Runtime.InteropServices;
using System.Xml;
using System.Xml.Linq;

namespace Meitou.Game.Host;

/// <summary>
/// A crash outside .NET's reach (an access violation in a driver or a native library, a stack overflow, a fail-fast) leaves nothing in log.txt:
/// the log just stops (#1619). Windows Error Reporting records it in the Application event log, so at the next start the previous run's crash is
/// read from there into the log as <c>crash</c> lines: the exception code, the faulting module with its version and offset, and the text the .NET
/// runtime logged with it (an unhandled exception's or a fail-fast's stack). Windows only; nothing is written when the event log holds no such
/// event (a clean exit, error reporting turned off, Wine's event log).
/// </summary>
static unsafe partial class PreviousCrash
{
    /// <summary>
    /// The log category of the lines written.
    /// </summary>
    const string Prefix = "crash     ";
    /// <summary>
    /// <c>EvtQueryChannelPath</c>: the query's path names a channel.
    /// </summary>
    const int ChannelPath = 1;
    /// <summary>
    /// <c>EvtRenderEventXml</c>: an event rendered as its XML.
    /// </summary>
    const int RenderXml = 1;
    /// <summary>
    /// <c>ERROR_INSUFFICIENT_BUFFER</c>: the size query's answer of <c>EvtRender</c>.
    /// </summary>
    const int InsufficientBuffer = 122;
    /// <summary>
    /// At most this many lines of the runtime's text are written per event.
    /// </summary>
    const int MaxRuntimeLines = 40;
    /// <summary>
    /// The namespace of a rendered event's XML.
    /// </summary>
    static readonly XNamespace EventsXml = "http://schemas.microsoft.com/win/2004/08/events/event";
    /// <summary>
    /// The data of an Application Error event (1000) in order, for a Windows that writes them without names.
    /// </summary>
    static readonly string[] FaultFieldOrder =
    [
        "AppName", "AppVersion", "AppTimeStamp", "ModuleName", "ModuleVersion", "ModuleTimeStamp", "ExceptionCode", "FaultingOffset",
        "ProcessId", "ProcessCreationTime", "AppPath", "ModulePath",
    ];

    /// <summary>
    /// Writes the crashes of this executable recorded since <paramref name="previousStart"/> (the previous run's log header, local time) to the
    /// log, on a thread of its own so the start does not wait for the event log.
    /// </summary>
    public static void ReportSince(DateTime previousStart)
    {
        if (!OperatingSystem.IsWindows()) return;

        DateTime since = previousStart.ToUniversalTime();
        new Thread(() => Report(since)) { IsBackground = true, Name = "previous crash" }.Start();
    }

    /// <summary>
    /// The Application Error events, then the runtime's; an event log that cannot be read writes nothing.
    /// </summary>
    static void Report(DateTime since)
    {
        try
        {
            ReportFaults(since);
            ReportRuntime(since);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or XmlException)
        {
        }
    }

    /// <summary>
    /// One line per Application Error event (1000) of this executable: when, the exception code and where it was raised.
    /// </summary>
    static void ReportFaults(DateTime since)
    {
        foreach (var e in Query("Application Error", "EventID=1000", since))
        {
            var fields = FaultFields(e);
            if (!IsThisExecutable(fields)) continue;

            Console.Error.WriteLine(Prefix + DescribeFault(fields, LocalTime(e)));
        }
    }

    /// <summary>
    /// The text of each .NET Runtime event (1023 fatal error, 1025 fail-fast, 1026 unhandled exception) of this executable, a line at a time.
    /// </summary>
    static void ReportRuntime(DateTime since)
    {
        string application = "Application: " + Path.GetFileName(Environment.ProcessPath);
        foreach (var e in Query(".NET Runtime", "EventID=1023 or EventID=1025 or EventID=1026", since))
        {
            string text = e.Element(EventsXml + "EventData")?.Value ?? "";
            if (!text.StartsWith(application, StringComparison.OrdinalIgnoreCase)) continue;

            string id = e.Element(EventsXml + "System")?.Element(EventsXml + "EventID")?.Value ?? "?";
            Console.Error.WriteLine($"{Prefix}.NET runtime event {id} at {LocalTime(e)}:");
            WriteRuntimeLines(text);
        }
    }

    /// <summary>
    /// The runtime's text indented under its heading, cut after <see cref="MaxRuntimeLines"/> lines.
    /// </summary>
    static void WriteRuntimeLines(string text)
    {
        string[] lines = text.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n');
        foreach (string line in lines.Take(MaxRuntimeLines)) Console.Error.WriteLine($"{Prefix}  {line}");
        if (lines.Length > MaxRuntimeLines) Console.Error.WriteLine($"{Prefix}  ... {lines.Length - MaxRuntimeLines} more lines");
    }

    /// <summary>
    /// An Application Error event's data by name: by their names when Windows wrote them, else by <see cref="FaultFieldOrder"/>' order.
    /// </summary>
    static Dictionary<string, string> FaultFields(XElement e)
    {
        var data = e.Element(EventsXml + "EventData")?.Elements(EventsXml + "Data").ToList() ?? [];
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < data.Count; i++)
        {
            string? name = (string?)data[i].Attribute("Name") ?? (i < FaultFieldOrder.Length ? FaultFieldOrder[i] : null);
            if (name is not null) fields[name] = data[i].Value;
        }
        return fields;
    }

    /// <summary>
    /// Whether the event is this executable's: its path when the event has one (each version's folder keeps its own log), else its file name.
    /// </summary>
    static bool IsThisExecutable(Dictionary<string, string> fields)
    {
        string exe = Environment.ProcessPath ?? "";
        if (fields.GetValueOrDefault("AppPath") is { Length: > 0 } path) return string.Equals(path, exe, StringComparison.OrdinalIgnoreCase);

        return string.Equals(fields.GetValueOrDefault("AppName"), Path.GetFileName(exe), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The fault's line: when, the exception code (named when known) and the module with its version, offset and path.
    /// </summary>
    static string DescribeFault(Dictionary<string, string> fields, string time)
    {
        string code = fields.GetValueOrDefault("ExceptionCode", "?").ToLowerInvariant();
        if (code.StartsWith("0x", StringComparison.Ordinal)) code = code[2..];

        string module = fields.GetValueOrDefault("ModuleName", "?");
        string version = fields.GetValueOrDefault("ModuleVersion", "?");
        string offset = fields.GetValueOrDefault("FaultingOffset", "?");
        string path = fields.GetValueOrDefault("ModulePath", "");
        return $"the previous run crashed at {time}: exception 0x{code}{CodeName(code)} in {module} {version} at offset {offset}"
            + (path.Length > 0 ? $" ({path})" : "");
    }

    /// <summary>
    /// What the commonest exception codes mean.
    /// </summary>
    static string CodeName(string code) => code switch
    {
        "c0000005" => " (access violation)",
        "c00000fd" => " (stack overflow)",
        "c0000409" => " (fail-fast)",
        "c0000374" => " (heap corruption)",
        "e0434352" => " (.NET exception)",
        _ => "",
    };

    /// <summary>
    /// When the event was recorded, in local time as the log's header.
    /// </summary>
    static string LocalTime(XElement e)
    {
        string? stamp = (string?)e.Element(EventsXml + "System")?.Element(EventsXml + "TimeCreated")?.Attribute("SystemTime");
        if (!DateTime.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var time)) return "?";

        return time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The Application log's events of <paramref name="provider"/> matching <paramref name="ids"/> (an XPath test) recorded since
    /// <paramref name="since"/> (UTC), oldest first.
    /// </summary>
    static List<XElement> Query(string provider, string ids, DateTime since)
    {
        string time = since.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        string xpath = $"*[System[Provider[@Name='{provider}'] and ({ids}) and TimeCreated[@SystemTime>='{time}']]]";
        var events = new List<XElement>();
        nint results = EvtQuery(0, "Application", xpath, ChannelPath);
        if (results == 0) return events;

        try
        {
            ReadAll(results, events);
        }
        finally
        {
            EvtClose(results);
        }
        return events;
    }

    /// <summary>
    /// Every event of the query's results, each rendered and closed.
    /// </summary>
    static void ReadAll(nint results, List<XElement> events)
    {
        const int batch = 16;
        nint* handles = stackalloc nint[batch];
        while (EvtNext(results, batch, handles, uint.MaxValue, 0, out int count) != 0)
        {
            for (int i = 0; i < count; i++)
            {
                try
                {
                    if (Render(handles[i]) is { } xml) events.Add(XElement.Parse(xml));
                }
                finally
                {
                    EvtClose(handles[i]);
                }
            }
        }
    }

    /// <summary>
    /// The event's XML, or null when it cannot be rendered: a size query first, then the rendering.
    /// </summary>
    static string? Render(nint evt)
    {
        EvtRender(0, evt, RenderXml, 0, null, out int used, out _);
        if (Marshal.GetLastPInvokeError() != InsufficientBuffer || used <= 0) return null;

        var buffer = new char[(used + 1) / 2];
        fixed (char* text = buffer)
        {
            if (EvtRender(0, evt, RenderXml, buffer.Length * 2, text, out _, out _) == 0) return null;
            return new string(text);
        }
    }

    /// <summary>
    /// wevtapi <c>EvtQuery</c>: a query's result set over a channel; 0 on failure.
    /// </summary>
    [LibraryImport("wevtapi.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial nint EvtQuery(nint session, string path,
        string query, int flags);
    /// <summary>
    /// wevtapi <c>EvtNext</c>: the next events of a result set; 0 when there are none left.
    /// </summary>
    [LibraryImport("wevtapi.dll")] private static partial int EvtNext(nint results, int size, nint* events, uint timeout, int flags, out int returned);
    /// <summary>
    /// wevtapi <c>EvtRender</c>: an event as XML into a buffer of <paramref name="size"/> bytes; 0 on failure (the last error says why).
    /// </summary>
    [LibraryImport("wevtapi.dll", SetLastError = true)] private static partial int EvtRender(nint context, nint fragment, int flags, int size,
        char* buffer, out int used, out int properties);
    /// <summary>
    /// wevtapi <c>EvtClose</c>: closes a query or event handle.
    /// </summary>
    [LibraryImport("wevtapi.dll")] private static partial int EvtClose(nint handle);
}
