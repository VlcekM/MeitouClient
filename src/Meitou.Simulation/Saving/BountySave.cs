using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Simulation.Crime;

namespace Meitou.Simulation.Saving;

/// <summary>
/// A character's bounty ledger in its GAMESTATE_CHARACTER record (docs/formats/save.md, docs/game/economy.md 11.1): for each entry k from 0 the string
/// <c>bountyfac&lt;k&gt;</c> (the FACTION's string id) and the ints <c>bountyexp&lt;k&gt;</c> (the game hour of the last raise, truncated), <c>amount&lt;k&gt;</c>,
/// <c>crimes&lt;k&gt;</c> (the crime mask) and <c>claim&lt;k&gt;</c> (<b>Verified</b>: the writer <c>FUN_1406262b0</c> and the three sample saves agree on the
/// names and types). The game hour is read as day x 24 + hour of the save's clock (<b>Observed</b>: every <c>bountyexp</c> in the three sample saves is at
/// most that, and the largest is one hour below it); the world keeps ticks, converted here with the save's clock. The save's clock is floored to the minute,
/// so the hour is written with a minute and a tick of slack (<b>engine</b>: a raise up to a minute before the hour is written as that hour); without it each
/// save and load would truncate an entry read at a whole hour one hour lower and bounties would expire early.
/// </summary>
public static class BountySave
{
    /// <summary>
    /// The game hour of a save's clock: day x 24 + hour + minute / 60.
    /// </summary>
    public static double HoursOf(SaveClock clock) => clock.Day * 24.0 + clock.HourOfDay;

    /// <summary>
    /// The ledger a character's state record holds (null when it holds none or none of its factions is known), with the raise ticks relative to
    /// <paramref name="tick"/> at <paramref name="clock"/>.
    /// </summary>
    public static BountyLedger? Read(FcsRecord? state, FactionRelations relations, SaveClock clock, long tick, float tickSeconds)
    {
        if (state is null) return null;

        double hoursPerTick = tickSeconds * 11.0 / 1200.0;
        double now = HoursOf(clock);
        BountyLedger? ledger = null;
        for (int k = 0; state.Strings.TryGetValue($"bountyfac{k}", out var factionId); k++)
        {
            int f = relations.IndexOf(factionId);
            if (f < 0) continue;

            var entry = (ledger ??= new BountyLedger()).Get(f, relations.Factions[f].Name);
            entry.Amount = state.Ints.GetValueOrDefault($"amount{k}");
            entry.Crimes = state.Ints.GetValueOrDefault($"crimes{k}");
            entry.Claimed = state.Ints.GetValueOrDefault($"claim{k}") != 0;
            double raised = state.Ints.GetValueOrDefault($"bountyexp{k}");
            entry.RaisedTick = tick - (long)Math.Floor((now - raised) / hoursPerTick);
        }

        return ledger;
    }

    /// <summary>
    /// Writes the ledger into a character's state record, replacing the entries it had (none when <paramref name="ledger"/> is null or empty).
    /// </summary>
    public static void Write(FcsRecord state, BountyLedger? ledger, FactionRelations relations, SaveClock clock, long tick, float tickSeconds)
    {
        for (int k = 0; state.Strings.ContainsKey($"bountyfac{k}"); k++)
        {
            state.Strings.Remove($"bountyfac{k}");
            foreach (var key in new[] { "bountyexp", "amount", "crimes", "claim" })
                state.Ints.Remove($"{key}{k}");
        }

        if (ledger is null) return;

        double hoursPerTick = tickSeconds * 11.0 / 1200.0;
        double now = HoursOf(clock);
        int n = 0;
        foreach (var e in ledger.Entries)
        {
            if (!relations.Contains(e.Faction)) continue;

            state.Strings[$"bountyfac{n}"] = relations.Factions[e.Faction].Id;
            state.Ints[$"bountyexp{n}"] = (int)Math.Floor(now - (tick - e.RaisedTick) * hoursPerTick + 1.0 / 60 + hoursPerTick);
            state.Ints[$"amount{n}"] = e.Amount;
            state.Ints[$"crimes{n}"] = e.Crimes;
            state.Ints[$"claim{n}"] = e.Claimed ? 1 : 0;
            n++;
        }
    }
}
