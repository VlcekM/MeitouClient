using System.Text;

namespace Meitou.Simulation;

/// <summary>
/// The lines of a world's debugging log (<see cref="EventLog"/>, <see cref="Dialogue.DialogueLog"/>) not taken yet: each stamped with the game day,
/// hour and tick, kept until the host takes them between ticks. Not saved or hashed: nothing reads them back.
/// </summary>
public sealed class LogLines
{
    /// <summary>
    /// The most lines kept untaken (a world without a host taking them, as in the tests): older ones are dropped past it.
    /// </summary>
    const int MaxLines = 4096;

    /// <summary>
    /// The lines not taken yet.
    /// </summary>
    readonly List<string> _lines = [];

    /// <summary>
    /// Adds a line: the category padded to the log's column (as log.txt's other lines), the stamp, then the text.
    /// </summary>
    public void Line(World world, string category, string text)
    {
        var line = new StringBuilder(category.PadRight(10)).Append(EventLog.Stamp(world)).Append(text).ToString();
        lock (_lines)
        {
            if (_lines.Count >= MaxLines) _lines.RemoveRange(0, MaxLines / 4);
            _lines.Add(line);
        }
    }

    /// <summary>
    /// Takes the lines added since the last call (the host, between ticks).
    /// </summary>
    public IReadOnlyList<string> Take()
    {
        lock (_lines)
        {
            if (_lines.Count == 0) return [];
            string[] all = [.. _lines];
            _lines.Clear();
            return all;
        }
    }
}
