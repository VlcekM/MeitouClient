using System.Runtime.InteropServices;
using System.Text;

namespace Meitou.Game;

/// <summary>
/// MeitouClient.exe is a windowed (GUI subsystem) program, so starting it opens no console window. Its output always goes to log.txt
/// next to the exe (<see cref="GameLog"/>); run from a terminal it also writes into that terminal. The one question it may ask
/// (the Kenshi folder) opens a console of its own for as long as it takes.
/// </summary>
static partial class GameConsole
{
    const uint AttachParentProcess = unchecked((uint)-1);

    /// <summary>Joins the console of the terminal that started the game, if any. Call before anything is written.</summary>
    public static void AttachToParent()
    {
        if (!OperatingSystem.IsWindows() || OutputRedirected() || GetConsoleWindow() != 0) return;
        if (AttachConsole(AttachParentProcess) != 0) Rebind();
    }

    // A parent that hands the game a pipe or a file (the Debug launcher's capture) keeps it; attaching would rebind past it.
    // Console.IsOutputRedirected is no test here: it also says true when a GUI program got no handle at all.
    static bool OutputRedirected()
    {
        nint handle = GetStdHandle(StdOutputHandle);
        if (handle is 0 or -1) return false;
        uint type = GetFileType(handle);
        return type is FileTypeDisk or FileTypePipe;
    }

    const int StdOutputHandle = -11;
    const uint FileTypeDisk = 1, FileTypePipe = 3;

    static TextWriter? log;

    /// <summary>Starts log.txt (after <see cref="AttachToParent"/>): output and error go to it as well, and so does an exception that ends the game.</summary>
    public static void StartLog()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ExplainBlocked(e.ExceptionObject as Exception);
        if (GameLog.Open() is not { } file) return;
        var shared = TextWriter.Synchronized(file);   // one lock for output and error
        log = shared;
        Bind(Console.Out, Console.Error);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => shared.WriteLine($"unhandled {e.ExceptionObject}");
    }

    // ERROR_SYSTEM_INTEGRITY_POLICY_VIOLATION: Smart App Control or an App Control (WDAC) policy refused to load a file (issue #127).
    const int PolicyBlocked = unchecked((int)0x800711C7);

    /// <summary>
    /// When Windows refused one of our files, says so in a message box before the game closes: our binaries are not code-signed, and Smart App
    /// Control blocks unsigned files it has no reputation for. Anything else is left to the log.
    /// </summary>
    static void ExplainBlocked(Exception? e)
    {
        if (!OperatingSystem.IsWindows()) return;
        for (; e is not null; e = e.InnerException)
        {
            if (e is not FileLoadException { HResult: PolicyBlocked } blocked) continue;
            string file = Path.GetFileName(blocked.FileName ?? "") is { Length: > 0 } name ? name : "a game file";
            string text = $"Windows blocked {file}, so Meitou has to close.\n\n" +
                "Meitou's files are not code-signed yet, and Smart App Control blocks unsigned files that Microsoft does not know. To play, turn it off in " +
                "Windows Security > App & browser control > Smart App Control settings. On some Windows versions it cannot be turned back on " +
                "without reinstalling Windows.\n\nIf Smart App Control is already off, an App Control policy set by your organisation blocked the file.";
            Console.Error.WriteLine($"blocked   {text.Replace("\n\n", " ")}");
            MessageBoxW(0, text, "Meitou", MessageBoxIconError | MessageBoxTopmost | MessageBoxSetForeground);
            return;
        }
    }

    const uint MessageBoxIconError = 0x10, MessageBoxSetForeground = 0x10000, MessageBoxTopmost = 0x40000;

    static void Bind(TextWriter output, TextWriter error)
    {
        Console.SetOut(log is null ? output : new TeeWriter(log, output));
        Console.SetError(log is null ? error : new TeeWriter(log, error));
    }

    /// <summary>A console to ask a question on: the attached one, else a new window that closes again when this is disposed.</summary>
    public static IDisposable ForPrompt()
    {
        if (!OperatingSystem.IsWindows() || GetConsoleWindow() != 0 || AllocConsole() == 0) return new Release(false);
        Rebind();
        return new Release(true);
    }

    // Console's readers and writers are made on first use from the handles the process had then; point them at the new console.
    static void Rebind()
    {
        // Console's own writers never write a byte-order mark; a StreamWriter over UTF-8 would.
        var encoding = Console.OutputEncoding.CodePage == 65001 ? new UTF8Encoding(false) : Console.OutputEncoding;
        Bind(new StreamWriter(Console.OpenStandardOutput(), encoding) { AutoFlush = true }, new StreamWriter(Console.OpenStandardError(), encoding) { AutoFlush = true });
        Console.SetIn(new StreamReader(Console.OpenStandardInput(), Console.InputEncoding));
    }

    sealed class Release(bool free) : IDisposable
    {
        public void Dispose()
        {
            if (!free) return;
            free = false;
            Bind(TextWriter.Null, TextWriter.Null);
            Console.SetIn(TextReader.Null);
            FreeConsole();
        }
    }

    [LibraryImport("kernel32.dll")] private static partial nint GetConsoleWindow();
    [LibraryImport("kernel32.dll")] private static partial int AttachConsole(uint processId);
    [LibraryImport("kernel32.dll")] private static partial int AllocConsole();
    [LibraryImport("kernel32.dll")] private static partial int FreeConsole();
    [LibraryImport("kernel32.dll")] private static partial nint GetStdHandle(int handle);
    [LibraryImport("kernel32.dll")] private static partial uint GetFileType(nint file);
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial int MessageBoxW(nint owner, string text, string caption, uint type);
}
