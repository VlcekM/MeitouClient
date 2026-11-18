// Port only (task 3.3, moved to Core in the task 9.3e review): the PG-SPAR "result" line (Meitou's log, test output the scripted live runs read;
// the C++ writes no such line). Written where the C++ schedules the Results window (ResultsUI::ScheduleShow, src/Game/PgSparHost.cs) from a copy
// of the frozen snapshot. Its numbers go through TestJson: a non-finite one is written null (msvcr's 1.#QNAN would make the line unparseable).
using System.Globalization;
using System.Text;

namespace ProvingGrounds.Core.Glue;

/// <summary>The PG-SPAR result line's JSON (outcome, podium, per fighter the stats, rating and Marks), pure so the tests pin it.</summary>
public static class SparResultLine
{
    /// <summary>The line's JSON for <paramref name="snap"/> at sim tick <paramref name="tick"/>, the Results window due in
    /// <paramref name="delay"/> s; <paramref name="idText"/> gives a fighter slot's character id text ("" for none).</summary>
    public static string Json(SparPodium.Snapshot snap, long tick, float delay, Func<int, string> idText)
    {
        var b = new StringBuilder();
        string Outcome(SparPodium.OutcomeKind o) => o switch
        {
            SparPodium.OutcomeKind.TeamAWins => "team_a_wins",
            SparPodium.OutcomeKind.TeamBWins => "team_b_wins",
            SparPodium.OutcomeKind.LastStanding => "last_standing",
            SparPodium.OutcomeKind.Draw => "draw",
            _ => "stopped",
        };
        b.Append("{\"event\":\"result\",\"tick\":").Append(tick.ToString(CultureInfo.InvariantCulture))
            .Append(",\"outcome\":").Append(PGLog.Quote(Outcome(snap.Outcome)))
            .Append(",\"mode\":").Append(((int)snap.Mode).ToString(CultureInfo.InvariantCulture))
            .Append(",\"showIn\":").Append(TestJson.G(delay, 9))
            .Append(",\"lastStandingSlot\":").Append(snap.LastStandingId.ToString(CultureInfo.InvariantCulture))
            .Append(",\"fighters\":[");
        for (int i = 0; i < snap.FighterCount; i++)
        {
            var r = snap.Fighters[i];
            if (i > 0) b.Append(',');
            b.Append("{\"slot\":").Append(r.Id.ToString(CultureInfo.InvariantCulture))
                .Append(",\"id\":").Append(PGLog.Quote(idText(r.Id)))
                .Append(",\"name\":").Append(PGLog.Quote(r.Name ?? ""))
                .Append(",\"team\":").Append(((int)r.Team).ToString(CultureInfo.InvariantCulture))
                .Append(",\"eliminationIndex\":").Append(r.EliminationIndex.ToString(CultureInfo.InvariantCulture))
                .Append(",\"hits\":").Append(r.HitsLanded.ToString(CultureInfo.InvariantCulture))
                // the per-blow stats CombatHooks feeds (task 3.2b): blocks, misses (its blows the opponent dodged), dodges, damage
                .Append(",\"blocks\":").Append(r.Blocks.ToString(CultureInfo.InvariantCulture))
                .Append(",\"misses\":").Append(r.Misses.ToString(CultureInfo.InvariantCulture))
                .Append(",\"dodges\":").Append(r.Dodges.ToString(CultureInfo.InvariantCulture))
                .Append(",\"dealt\":").Append(TestJson.G(r.DamageDealt, 9))
                .Append(",\"taken\":").Append(TestJson.G(r.DamageTaken, 9))
                .Append(",\"mitigated\":").Append(TestJson.G(r.DamageMitigated, 9))
                .Append(",\"ratingUpdated\":").Append(r.RatingUpdated ? "true" : "false")
                .Append(",\"ratingBefore\":").Append(TestJson.G(r.RatingBefore, 9))
                .Append(",\"ratingAfter\":").Append(TestJson.G(r.RatingAfter, 9))
                .Append(",\"ratingDelta\":").Append(TestJson.G(r.RatingDelta, 9))
                .Append(",\"marksBefore\":").Append(r.MarksBefore.ToString(CultureInfo.InvariantCulture))
                .Append(",\"marksEarned\":").Append(r.MarksEarned.ToString(CultureInfo.InvariantCulture))
                .Append(",\"marksAfter\":").Append(r.MarksAfter.ToString(CultureInfo.InvariantCulture))
                .Append(",\"marksUpdated\":").Append(r.MarksUpdated ? "true" : "false").Append('}');
        }
        b.Append("],\"podium\":[");
        for (int p = 0; p < snap.PodiumCount; p++)
        {
            if (p > 0) b.Append(',');
            b.Append("{\"place\":").Append(snap.Podium[p].Place.ToString(CultureInfo.InvariantCulture))
                .Append(",\"slot\":").Append(snap.Podium[p].Fighter.Id.ToString(CultureInfo.InvariantCulture))
                .Append(",\"name\":").Append(PGLog.Quote(snap.Podium[p].Fighter.Name ?? "")).Append('}');
        }
        return b.Append("]}").ToString();
    }
}
