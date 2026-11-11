using System.Globalization;
using Meitou.Data.Gameplay;

namespace Meitou.Simulation;

/// <summary>
/// The world's event lines for the game's log (<b>engine</b>, a debugging aid; docs/simulation.md "Event log"): the rare events that explain what
/// characters and factions did (a fight started and why, a relation changed, a crime, an arrest, a death), each stamped with the game day, hour and
/// tick. Serial phases only; the host takes them between ticks (<see cref="Take"/>) and writes them to log.txt. Not saved or hashed: nothing reads
/// them back.
/// </summary>
public sealed class EventLog
{
    #region Lines

    /// <summary>
    /// The lines not taken yet.
    /// </summary>
    readonly LogLines _lines = new();
    /// <summary>
    /// The fight starts, written as one line per attacking faction, target and cause at the tick's end.
    /// </summary>
    readonly Combat.FightLog _fights = new();
    /// <summary>
    /// The deaths and knock-outs, found by comparing each tick boundary with the last.
    /// </summary>
    readonly Bodies.DownLog _downs = new();

    /// <summary>
    /// Adds a line: the category padded to the log's column (as log.txt's other lines), the stamp, then the text.
    /// </summary>
    public void Line(World world, string category, string text) => _lines.Line(world, category, text);

    /// <summary>
    /// Notes a fight <paramref name="attacker"/> started on <paramref name="target"/> for <paramref name="cause"/>, written at the tick's end
    /// (<see cref="EndTick"/>).
    /// </summary>
    public void Fight(World world, int attacker, int target, string cause) => _fights.Note(world, attacker, target, cause);

    /// <summary>
    /// At the world's tick boundary: the deaths and knock-outs since the last one, then the fights started since, are written.
    /// </summary>
    public void EndTick(World world)
    {
        _downs.Observe(world, this);
        _fights.Write(world, this);
    }

    /// <summary>
    /// Takes the lines added since the last call (the host, between ticks).
    /// </summary>
    public IReadOnlyList<string> Take() => _lines.Take();

    #endregion

    #region Naming

    /// <summary>
    /// "Name #slot [Faction]": a character as the lines name it (its faction left out for the player's).
    /// </summary>
    public static string Who(World world, int slot)
    {
        if (world.Characters.Cold(slot) is not { } cold) return "#" + slot.ToString(CultureInfo.InvariantCulture);
        string name = (cold.Name.Length > 0 ? cold.Name : "(unnamed)") + " #" + slot.ToString(CultureInfo.InvariantCulture);
        return cold.IsPlayer ? name : $"{name} [{FactionName(world, cold.Faction)}]";
    }

    /// <summary>
    /// A character by id as the lines name it (<see cref="Who(World, int)"/>); "(none)" for none, "(gone)" for one no longer in the world.
    /// </summary>
    public static string Who(World world, CharacterId id)
    {
        if (id.IsNone) return "(none)";
        return world.Characters.IsAlive(id) ? Who(world, id.Slot) : "(gone)";
    }

    /// <summary>
    /// The faction's name, else its index.
    /// </summary>
    public static string FactionName(World world, int faction) =>
        Relations(world) is { } relations && relations.Contains(faction)
            ? relations.Factions[faction].Name
            : "faction " + faction.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// "day 3 14:05 tick 48213  ": the game day and hour of the world, and its tick.
    /// </summary>
    public static string Stamp(World world)
    {
        double hours = world.GameHours;
        long day = (long)Math.Floor(hours / 24);
        double inDay = hours - day * 24;
        int hour = (int)inDay, minute = Math.Min(59, (int)((inDay - hour) * 60));
        return string.Create(CultureInfo.InvariantCulture, $"day {day} {hour:00}:{minute:00} tick {world.Tick}  ");
    }

    /// <summary>
    /// The relation table of the world (its aggro system's), or null.
    /// </summary>
    internal static FactionRelations? Relations(World world) => world.SystemOf<Aggro.AggroSystem>()?.Relations;

    #endregion
}
