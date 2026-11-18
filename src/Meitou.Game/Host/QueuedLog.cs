using System.Text;

namespace Meitou.Game.Host;

/// <summary>
/// A debug log written off the thread that logs (log_actions.txt from the tick, log_dialogue.txt from the frame): a line only joins a queue, and at
/// <see cref="Flush"/> a thread of its own writes the queue to the file in one write. The file is a <see cref="GameLog"/>, kept the same way (cut
/// past 10 MiB, each write forced to the disk); written a line at a time from the tick, a slow write held the tick.
/// </summary>
sealed class QueuedLog : TextWriter
{
    /// <summary>
    /// The file the queue is written to, by <see cref="Drain"/> (or, as the game ends on an exception, by <see cref="WriteOnCrash"/>).
    /// </summary>
    readonly GameLog _file;
    /// <summary>
    /// Guards <see cref="_pending"/> and <see cref="_disposed"/>.
    /// </summary>
    readonly object _gate = new();
    /// <summary>
    /// Held while a batch is taken and written, so the writing thread and <see cref="WriteOnCrash"/> keep the lines in order.
    /// </summary>
    readonly object _fileGate = new();
    /// <summary>
    /// The text logged and not yet handed to <see cref="_file"/>.
    /// </summary>
    readonly StringBuilder _pending = new();
    /// <summary>
    /// Set at <see cref="Flush"/> and on dispose, waking <see cref="Drain"/>; never disposed, so a flush after the log's dispose does nothing.
    /// </summary>
    readonly AutoResetEvent _flushed = new(false);
    /// <summary>
    /// The thread that writes the queue.
    /// </summary>
    readonly Thread _thread;
    /// <summary>
    /// Set on dispose: the writing thread writes what is left and ends; text logged later is dropped.
    /// </summary>
    bool _disposed;

    /// <summary>
    /// A queue in front of <paramref name="file"/>, with its writing thread started.
    /// </summary>
    QueuedLog(GameLog file, string name)
    {
        _file = file;
        _thread = new Thread(Drain) { IsBackground = true, Name = "log " + name };
        _thread.Start();
        AppDomain.CurrentDomain.UnhandledException += WriteOnCrash;
    }

    /// <summary>
    /// The queued log of <paramref name="name"/> (a path relative to the executable's folder, as <see cref="GameLog.Open"/>), or null when the file
    /// cannot be opened.
    /// </summary>
    public static QueuedLog? Open(string name) => GameLog.Open(name) is { } file ? new QueuedLog(file, name) : null;

    /// <inheritdoc/>
    public override Encoding Encoding => _file.Encoding;

    /// <inheritdoc/>
    public override void Write(char value)
    {
        lock (_gate)
        {
            if (!_disposed) _pending.Append(value);
        }
    }

    /// <inheritdoc/>
    public override void Write(string? value)
    {
        lock (_gate)
        {
            if (!_disposed) _pending.Append(value);
        }
    }

    /// <inheritdoc/>
    public override void Write(char[] buffer, int index, int count)
    {
        lock (_gate)
        {
            if (!_disposed) _pending.Append(buffer, index, count);
        }
    }

    /// <inheritdoc/>
    public override void WriteLine(string? value)
    {
        lock (_gate)
        {
            if (!_disposed) _pending.Append(value).Append(CoreNewLine);
        }
    }

    /// <summary>
    /// Hands what was logged to the writing thread, without waiting for it.
    /// </summary>
    public override void Flush() => _flushed.Set();

    /// <summary>
    /// The writing thread: at each flush, takes the queue and writes it to the file in one write; on dispose writes what is left and ends.
    /// </summary>
    void Drain()
    {
        while (true)
        {
            _flushed.WaitOne();
            if (WritePending()) return;
        }
    }

    /// <summary>
    /// As the game ends on an exception (on any thread, the tick's rethrown on the main one), writes what is queued at once: the last lines
    /// before a crash are the ones looked for.
    /// </summary>
    void WriteOnCrash(object? sender, UnhandledExceptionEventArgs e) => WritePending();

    /// <summary>
    /// Takes the queue and writes it to the file in one write; a failed write (a full disk) drops it rather than end the game. Returns whether the
    /// log was disposed by then (nothing more will be queued).
    /// </summary>
    bool WritePending()
    {
        lock (_fileGate)
        {
            string text;
            bool last;
            lock (_gate)
            {
                text = _pending.ToString();
                _pending.Clear();
                last = _disposed;
            }

            if (text.Length == 0) return last;

            try
            {
                _file.Write(text);
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException)
            {
            }
            return last;
        }
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
            }

            AppDomain.CurrentDomain.UnhandledException -= WriteOnCrash;
            _flushed.Set();
            _thread.Join();
            _file.Dispose();
        }
        base.Dispose(disposing);
    }
}
