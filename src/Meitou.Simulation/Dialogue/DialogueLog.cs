using System.Globalization;
using Meitou.Data.Gameplay.Dialogue;

namespace Meitou.Simulation.Dialogue;

/// <summary>
/// The dialogue log (<b>engine</b>, a debugging aid; the host writes it to log_dialogue.txt beside log.txt): every line said, as a speech bubble or in
/// the player's conversation, and every reply clicked, with its speaker, target, event, line and effects; conversations opening, finding nothing to
/// say and ending; pending <c>interrupt</c> dialogues stored and used; and NPCs walking up to talk (started, arrived, given up, dropped). Each line is
/// stamped with the game day, hour and tick. Serial phases only; the host takes the lines between ticks (<see cref="Take"/>). Not saved or hashed:
/// nothing reads them back.
/// </summary>
public sealed class DialogueLog
{
    /// <summary>
    /// The lines not taken yet.
    /// </summary>
    readonly LogLines _lines = new();

    /// <summary>
    /// Adds a line: the category padded to a column, the stamp, then the text.
    /// </summary>
    public void Line(World world, string category, string text) => _lines.Line(world, category, text);

    /// <summary>
    /// Takes the lines added since the last call (the host, between ticks).
    /// </summary>
    public IReadOnlyList<string> Take() => _lines.Take();

    /// <summary>
    /// A node as the lines name it: a root by its name and id, a line by its id.
    /// </summary>
    public static string Node(DialogueNode? node) =>
        node is null ? "(none)"
            : node.IsRoot ? $"'{node.Record.Name}' ({node.StringId})"
            : node.StringId;

    /// <summary>
    /// A line's effects as " effects DA2=0, DA12=10" (its pending interrupt and locks too), or empty when it has none.
    /// </summary>
    public static string Effects(DialogueNode line)
    {
        var parts = new List<string>();
        foreach (var e in line.Effects) parts.Add(string.Create(CultureInfo.InvariantCulture, $"DA{e.Action}={e.Value}"));
        if (line.Interrupt is { } interrupt) parts.Add("interrupt " + interrupt);
        return parts.Count == 0 ? "" : " effects " + string.Join(", ", parts);
    }

    /// <summary>
    /// The event's number as the lines show it.
    /// </summary>
    public static string Event(int ev) => "event " + ev.ToString(CultureInfo.InvariantCulture);
}
