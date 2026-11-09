using System.Numerics;
using Meitou.Simulation;
using Meitou.Simulation.Combat;

namespace Meitou.Game.Scenarios;

/// <summary>
/// The duel's lap (<see cref="DuelScenario"/>): measured once on a world of its own, it is the world's ticks from <see cref="SkipTicks"/> (run
/// headless at each restart) to <see cref="EndTick"/>, and every lap that runs to its end must reach the measured run's state.
/// </summary>
sealed class DuelLap
{
    /// <summary>
    /// Two Samurai of the same record are evenly matched: in the built-in duel the first one goes down after 165 s (28 hits, 61 blocks). A lap is the
    /// last <see cref="DuelLeadTicks"/> (40 s) before that and <see cref="DuelHoldTicks"/> (4 s) after; the fight before the lap is run headless at
    /// each restart (about 0.06 ms a tick). A duel with nobody down in <see cref="DuelMaxTicks"/> (5 min) loops its first <see cref="DuelOpenTicks"/>
    /// (60 s).
    /// </summary>
    internal const int DuelLeadTicks = 1200, DuelHoldTicks = 120, DuelMaxTicks = 9000, DuelOpenTicks = 1800;

    /// <summary>
    /// The tick the lap starts at: the fight before it is run headless at the restart.
    /// </summary>
    internal long SkipTicks;

    /// <summary>
    /// The tick the lap ends at.
    /// </summary>
    internal long EndTick = DuelOpenTicks;

    /// <summary>
    /// The measured run's state hash at the lap's end.
    /// </summary>
    ulong _duelEndHash;

    /// <summary>
    /// Runs the set-up world headless to find when the first fighter is down, which places the lap round it; returns that tick (-1: nobody), who
    /// won, and the pair's mean middle over the lap (<paramref name="fallback"/> for an empty lap).
    /// </summary>
    internal (long Down, string Winner, Vector2 Centre) Measure(World world, CombatSystem combat, CharacterId duelA, CharacterId duelB,
        Vector2 fallback)
    {
        long down = -1;
        var middles = new List<Vector2>();   // the pair's middle after each tick
        string winner = "";
        ulong openHash = 0;

        // On past the knockout to the lap's end, for the state hash every lap must reach.
        while (world.Tick < DuelMaxTicks && (down < 0 || world.Tick < down + DuelHoldTicks))
        {
            world.RunTick();
            var (pa, pb) = (world.Characters.Previous[duelA.Slot].Position, world.Characters.Previous[duelB.Slot].Position);
            middles.Add(new Vector2(pa.X + pb.X, pa.Z + pb.Z) / 2);
            bool a = combat.StateOf(duelA).Down, b = combat.StateOf(duelB).Down;
            if (world.Tick == DuelOpenTicks) openHash = world.StateHash();
            if (down < 0 && (a || b))
                (down, winner) = (world.Tick, a && b ? "both down" : a ? "the one that started at +Z wins" : "the one that started at -Z wins");
        }

        (SkipTicks, EndTick) = down < 0 ? (0, DuelOpenTicks) : (Math.Max(down - DuelLeadTicks, 0), down + DuelHoldTicks);
        var lap = middles.Skip((int)SkipTicks).Take((int)(Math.Min(EndTick, world.Tick) - SkipTicks)).ToList();
        var centre = lap.Count == 0 ? fallback : lap.Aggregate(Vector2.Zero, (s, p) => s + p) / lap.Count;
        _duelEndHash = down < 0 ? openHash : world.StateHash();
        return (down, winner, centre);
    }

    /// <summary>
    /// Prints whether lap <paramref name="laps"/> ended in the measured run's state, by its state hash.
    /// </summary>
    internal void ReportLapEnd(int laps, ulong hash)
    {
        bool same = hash == _duelEndHash;
        Console.WriteLine(same
            ? $"duel      lap {laps} ended as measured (state hash {hash:x16})"
            : $"duel      lap {laps} DIFFERS from the measured run: state hash {hash:x16}, measured {_duelEndHash:x16}");
    }
}
