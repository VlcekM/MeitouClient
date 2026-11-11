using System.Globalization;

namespace Meitou.Simulation.Combat;

/// <summary>
/// The event log's fight starts (<see cref="EventLog.Fight"/>): those of one tick are written at its end as one line per attacking faction, target
/// and cause, with how the two factions stand; a fight started again on the same target for the same cause within <see cref="RepeatSeconds"/> is left
/// out (a guard turning on the same culprit again after each lost sight of it). Serial.
/// </summary>
sealed class FightLog
{
    /// <summary>
    /// A fight started again on the same target for the same cause within this many game seconds is not written again.
    /// </summary>
    const float RepeatSeconds = 60;
    /// <summary>
    /// Past this many remembered starts, the ones older than <see cref="RepeatSeconds"/> are forgotten.
    /// </summary>
    const int MaxRemembered = 4096;

    /// <summary>
    /// The fights started this tick, grouped by attacking faction, target and cause, with their attackers' slots.
    /// </summary>
    readonly Dictionary<(int Faction, int Target, string Cause), List<int>> _fights = [];
    /// <summary>
    /// The tick each attacker last started a fight on each target for each cause (<see cref="Repeated"/>).
    /// </summary>
    readonly Dictionary<(CharacterId Attacker, CharacterId Target, string Cause), long> _last = [];

    /// <summary>
    /// Notes a fight <paramref name="attacker"/> started on <paramref name="target"/> for <paramref name="cause"/>.
    /// </summary>
    public void Note(World world, int attacker, int target, string cause)
    {
        if (Repeated(world, attacker, target, cause)) return;

        int faction = world.Characters.Cold(attacker)?.Faction ?? -1;
        var key = (faction, target, cause);
        if (!_fights.TryGetValue(key, out var attackers)) _fights[key] = attackers = [];
        attackers.Add(attacker);
    }

    /// <summary>
    /// Writes the fights noted since the last call to <paramref name="log"/>.
    /// </summary>
    public void Write(World world, EventLog log)
    {
        foreach (var ((faction, target, cause), attackers) in _fights)
        {
            string who = Names(world, attackers);
            string relation = Relation(world, faction, world.Characters.Cold(target)?.Faction ?? -1);
            string verb = attackers.Count == 1 ? "attacks" : "attack";
            log.Line(world, "fight", $"{who} [{EventLog.FactionName(world, faction)}] {verb} {EventLog.Who(world, target)}: {cause}{relation}");
        }

        _fights.Clear();
    }

    /// <summary>
    /// Whether the attacker started a fight on the same target for the same cause within <see cref="RepeatSeconds"/>; remembers this one.
    /// </summary>
    bool Repeated(World world, int attacker, int target, string cause)
    {
        var key = (world.Characters.IdOf(attacker), world.Characters.IdOf(target), cause);
        long window = (long)(RepeatSeconds / world.TickSeconds);
        bool repeated = _last.TryGetValue(key, out long last) && world.Tick - last < window;
        _last[key] = world.Tick;
        if (_last.Count > MaxRemembered) Forget(world.Tick - window);
        return repeated;
    }

    /// <summary>
    /// Forgets the fights started before <paramref name="before"/>.
    /// </summary>
    void Forget(long before)
    {
        foreach (var (key, tick) in _last)
            if (tick < before) _last.Remove(key);
    }

    /// <summary>
    /// The attackers' names (their faction left out: the line names it once): up to three, then how many more.
    /// </summary>
    static string Names(World world, List<int> slots)
    {
        var names = slots
            .Take(3)
            .Select(s => Name(world, s));
        string list = string.Join(", ", names);
        return slots.Count > 3 ? $"{list} and {slots.Count - 3} more" : list;
    }

    /// <summary>
    /// "Name #slot", without the faction.
    /// </summary>
    static string Name(World world, int slot)
    {
        string number = "#" + slot.ToString(CultureInfo.InvariantCulture);
        return world.Characters.Cold(slot) is { Name.Length: > 0 } c ? c.Name + " " + number : number;
    }

    /// <summary>
    /// "; relation -45 (hostile)": how the attackers' faction stands with the target's, when they differ.
    /// </summary>
    static string Relation(World world, int faction, int targetFaction)
    {
        if (faction == targetFaction || EventLog.Relations(world) is not { } relations) return "";
        if (!relations.Contains(faction) || !relations.Contains(targetFaction)) return "";

        float value = relations.Get(faction, targetFaction);
        string standing = "";
        if (relations.IsHostile(faction, targetFaction)) standing = " (hostile)";
        else if (relations.IsAlly(faction, targetFaction)) standing = " (allied)";
        return string.Create(CultureInfo.InvariantCulture, $"; relation {value:0.#}{standing}");
    }
}
