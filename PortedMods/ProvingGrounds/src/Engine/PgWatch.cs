using System.Globalization;
using Meitou.Simulation;
using Meitou.Simulation.Combat;
using ProvingGrounds.Core;

namespace ProvingGrounds.Engine;

/// <summary>
/// TEST ONLY <c>pg watch &lt;id...&gt;</c> / <c>pg watch off</c> (task 3.5; refused until <c>pg spikes on</c>, divergences.md "Test-only
/// commands"): the gate scenarios time what fighters do by sim ticks, not by the wall clock of their shell. While on, once per tick (the end
/// of the system's serial step) each watched character's fight state is read: its attack target (<see cref="PgEngine.AttackTarget(World,
/// CombatSystem?, CharacterId)"/>, CombatSystem.TargetOf as of the last tick), down / dead, in combat, and its running task (row, source,
/// start tick, CharacterCold.Running). A <c>PG-WATCH {json}</c> line is written only when that state CHANGED for a character, never per tick:
/// a bout gives a few lines per target switch. Up to <see cref="MaxWatched"/> ids; a new world forgets the list (one object per world).
/// No allocation on a tick without a change. Sim thread, serial step.
/// </summary>
sealed class PgWatch
{
    /// <summary>The most characters one watch follows.</summary>
    public const int MaxWatched = 8;

    readonly CharacterId[] ids = new CharacterId[MaxWatched];
    readonly State[] last = new State[MaxWatched];
    int count;
    CombatSystem? combat;
    World? combatWorld;

    record struct State(bool Known, bool Exists, CharacterId Target, bool Down, bool Dead, bool InCombat, int Row, int Source, long TaskTick);

    /// <summary>Whether a watch runs (the F10 row has no line for it: test only).</summary>
    internal bool Active => count > 0;

    /// <summary><c>pg watch &lt;id...&gt;</c> starts a watch of those characters (the first line of each comes in the next tick: its state
    /// now); <c>pg watch off</c> ends it. The answer is one PG-WATCH line with the list.</summary>
    internal string Command(World world, IReadOnlyList<string> args)
    {
        if (args.Count == 1 && args[0] == "off")
        {
            count = 0;
            return Line("{\"event\":\"off\",\"tick\":" + world.Tick.ToString(CultureInfo.InvariantCulture) + "}");
        }
        if (args.Count < 1 || args.Count > MaxWatched) return $"usage pg watch <id...> (1 to {MaxWatched}) | pg watch off";
        var parsed = new CharacterId[args.Count];
        for (int i = 0; i < args.Count; i++)
            if (!CharacterIdText.TryParse(args[i], out parsed[i])) return "usage pg watch c:<slot>.<generation>...";
        count = parsed.Length;
        for (int i = 0; i < count; i++) { ids[i] = parsed[i]; last[i] = default; }
        return Line("{\"event\":\"on\",\"tick\":" + world.Tick.ToString(CultureInfo.InvariantCulture) + ",\"ids\":["
            + string.Join(",", parsed.Select(p => PGLog.Quote(CharacterIdText.Format(p)))) + "]}");
    }

    /// <summary>Once per tick: a line per watched character whose state changed.</summary>
    internal void Tick(World world)
    {
        if (count == 0) return;
        if (!ReferenceEquals(combatWorld, world)) { combat = PgEngine.FindCombat(world); combatWorld = world; }
        for (int i = 0; i < count; i++)
        {
            State now = Read(world, ids[i]);
            if (now == last[i]) continue;
            last[i] = now;
            Line("{\"event\":\"state\",\"tick\":" + world.Tick.ToString(CultureInfo.InvariantCulture)
                + ",\"id\":" + PGLog.Quote(CharacterIdText.Format(ids[i]))
                + ",\"exists\":" + B(now.Exists)
                + ",\"target\":" + (now.Target.IsNone ? "null" : PGLog.Quote(CharacterIdText.Format(now.Target)))
                + ",\"down\":" + B(now.Down) + ",\"dead\":" + B(now.Dead) + ",\"inCombat\":" + B(now.InCombat)
                + ",\"row\":" + (now.Row < 0 ? "null" : PGLog.Quote(((Meitou.Data.Gameplay.Ai.TaskType)now.Row).ToString()))
                + ",\"source\":" + (now.Source < 0 ? "null" : PGLog.Quote(((Meitou.Simulation.Ai.TaskSource)now.Source).ToString()))
                + ",\"taskTick\":" + now.TaskTick.ToString(CultureInfo.InvariantCulture) + "}");
        }
    }

    State Read(World world, CharacterId id)
    {
        if (!PgEngine.Exists(world, id)) return new State(true, false, CharacterId.None, false, false, false, -1, -1, -1);
        var run = PgEngine.Running(world, id);
        return new State(true, true, PgEngine.AttackTarget(world, combat, id), PgEngine.IsUnconscious(world, id), PgEngine.IsDead(world, id),
            PgEngine.InCombat(world, id), run is null ? -1 : (int)run.Row, run is null ? -1 : (int)run.Source, run?.Tick ?? -1);
    }

    static string B(bool b) => b ? "true" : "false";

    static string Line(string json)
    {
        ProvingGroundsMod.Ctx?.Log("PG-WATCH " + json);
        return json;
    }
}
