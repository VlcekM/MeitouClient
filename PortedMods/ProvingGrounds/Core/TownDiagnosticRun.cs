// Port of the planning logic of upstream/ProvingGrounds/plugin/src/TownDiagnosticRun.h/.cpp (Proving Grounds by Shutori, GPLv3).
using System.Globalization;

namespace ProvingGrounds.Core;

/// <summary>The planning logic of C++ TownDiagnosticRun.cpp (TW §2.17). The C++ functions read and write the one saved
/// <c>State&amp;</c> that <c>LeaderboardStore::GetTownDiagnostic()</c> returns (TownDiagnosticRun.cpp:40); here the state is passed by
/// <c>ref</c> and the glue passes the store's. The calls into LeaderboardStore, TownArena and CombatBalanceLog stay in the glue: the
/// three checks <see cref="ValidationError(in TownDiagnosticData.State, bool, bool)"/> needs are bool parameters, or are asked through
/// <see cref="IHost"/> in the C++'s order (the other ValidationError overload, which <see cref="StartConfigured"/> and the glue use), the
/// diagnostic error text is a <see cref="StatusText"/> parameter, <c>GetTickCount()</c> is <c>nowMs</c>, and every
/// <c>CombatBalanceLog::DiagnosticEvent(name, fields)</c> goes to an optional <c>diagnosticEvent(name, fields)</c> sink with the C++'s exact
/// field text. Numbers in the texts are formatted invariantly.</summary>
public static class TownDiagnosticRun
{
    /// <summary>The glue calls of <see cref="StartConfigured"/>, in the order the C++ makes them.</summary>
    public interface IHost
    {
        /// <summary><c>LeaderboardStore::HasActiveSave()</c></summary>
        bool HasActiveSave();
        /// <summary><c>TownArena::IsBusy()</c></summary>
        bool IsBusy();
        /// <summary><c>TownArena::HasBooking()</c></summary>
        bool HasBooking();
        /// <summary><c>TownArena::HasPlannedNpcBout()</c></summary>
        bool HasPlannedNpcBout();
        /// <summary><c>TownArena::CancelPlannedNpcBout(reason)</c></summary>
        void CancelPlannedNpcBout(string reason);
        /// <summary><c>LeaderboardStore::ResetTownStandings()</c> (it does not touch the diagnostic state, LeaderboardStore.cpp:110-115)</summary>
        bool ResetTownStandings();
        /// <summary><c>LeaderboardStore::ClearDiagnosticError()</c></summary>
        void ClearDiagnosticError();
    }

    // C++: TownDiagnosticRun.cpp:10-12
    public static bool Mutable(in TownDiagnosticData.State s) =>
        s.Status != (int)TownDiagnosticData.RunStatus.Active && s.Status != (int)TownDiagnosticData.RunStatus.Paused;

    // C++: TownDiagnosticRun.cpp:13-15
    public static int EnabledModels(in TownDiagnosticData.Config c)
    {
        int n = 0; for (int i = 0; i < TownDiagnosticData.ModelCount; ++i) if (c.Models[i]) ++n; return n;
    }

    // C++: TownDiagnosticRun.cpp:16-18
    public static int EnabledCoverage(in TownDiagnosticData.Config c)
    {
        int n = 0; for (int i = 0; i < TownDiagnosticData.CoverageCount; ++i) if (c.Coverage[i]) ++n; return n;
    }

    // C++: TownDiagnosticRun.cpp:19-21
    public static int ModelMask(in TownDiagnosticData.Config c)
    {
        int mask = 0; for (int i = 0; i < TownDiagnosticData.ModelCount; ++i) if (c.Models[i]) mask |= 1 << i; return mask;
    }

    // C++: TownDiagnosticRun.cpp:22-24
    public static int CoverageMask(in TownDiagnosticData.Config c)
    {
        int mask = 0; for (int i = 0; i < TownDiagnosticData.CoverageCount; ++i) if (c.Coverage[i]) mask |= 1 << i; return mask;
    }

    // C++: TownDiagnosticRun.cpp:25-30
    public static int LeastModel(in TownDiagnosticData.State s)
    {
        int best = -1;
        for (int i = 0; i < TownDiagnosticData.ModelCount; ++i) if (s.Config.Models[i] &&
            (best < 0 || s.Counters.ModelCompleted[i] < s.Counters.ModelCompleted[best])) best = i;
        return best;
    }

    // C++: TownDiagnosticRun.cpp:31-36
    public static int LeastCoverage(in TownDiagnosticData.State s)
    {
        int best = -1;
        for (int i = 0; i < TownDiagnosticData.CoverageCount; ++i) if (s.Config.Coverage[i] &&
            (best < 0 || s.Counters.CoverageCompleted[i] < s.Counters.CoverageCompleted[best])) best = i;
        return best;
    }

    // C++: TownDiagnosticRun.cpp:41 (IsActive)
    public static bool IsActive(in TownDiagnosticData.State s) => s.Status == (int)TownDiagnosticData.RunStatus.Active;

    /// <summary><paramref name="hasActiveSave"/> = <c>LeaderboardStore::HasActiveSave()</c>, <paramref name="busyOrBooked"/> =
    /// <c>TownArena::IsBusy() || TownArena::HasBooking()</c>.</summary>
    // C++: TownDiagnosticRun.cpp:42-50
    public static string ValidationError(in TownDiagnosticData.State s, bool hasActiveSave, bool busyOrBooked)
    {
        if (!hasActiveSave) return "Load or start a game first.";
        if (busyOrBooked) return "Wait for the active bout to finish or cancel the player booking.";
        if (s.Config.Target < 1 || s.Config.Target > 10000) return "Target must be between 1 and 10000.";
        if (EnabledModels(s.Config) == 0) return "Enable at least one MMR model.";
        if (EnabledCoverage(s.Config) == 0) return "Enable at least one roster bucket.";
        return "";
    }

    /// <summary>The C++ <c>ValidationError()</c> with its three glue checks made through <paramref name="host"/> (the glue's ValidationError, task
    /// 8.7a, and <see cref="StartConfigured"/>).</summary>
    // C++: TownDiagnosticRun.cpp:42-50 with the three glue checks made through the host in the C++'s order (IsBusy and HasBooking
    // only when there is a save, HasBooking only when not busy).
    public static string ValidationError(in TownDiagnosticData.State s, IHost host)
    {
        bool hasActiveSave = host.HasActiveSave();
        bool busyOrBooked = hasActiveSave && (host.IsBusy() || host.HasBooking());
        return ValidationError(s, hasActiveSave, busyOrBooked);
    }

    /// <summary>The run id: <c>(GetTickCount() ^ (prior*1664525u+1013904223u))|1u</c>.</summary>
    // C++: TownDiagnosticRun.cpp:65
    public static uint RunId(uint nowMs, uint prior) => unchecked((nowMs ^ Hashing.Lcg(prior)) | 1u);

    // C++: TownDiagnosticRun.cpp:51-70
    public static bool StartConfigured(ref TownDiagnosticData.State s, bool resetTownRecords, uint nowMs, IHost host,
        Action<string, string>? diagnosticEvent = null)
    {
        if (!Mutable(s) || ValidationError(s, host).Length != 0) return false;
        // Idle ambient markets are created continuously by the normal scheduler.
        // Replace one atomically so it cannot race the debug action; active fights
        // and player-owned bookings remain protected by ValidationError above.
        if (host.HasPlannedNpcBout())
            host.CancelPlannedNpcBout("Replaced by controlled diagnostic run");
        if (host.HasPlannedNpcBout()) return false;
        TownDiagnosticData.Config config = s.Config;
        uint prior = s.RunId;
        if (resetTownRecords && !host.ResetTownStandings()) return false;
        s = new TownDiagnosticData.State(); s.Config = config; s.Status = (int)TownDiagnosticData.RunStatus.Active;
        host.ClearDiagnosticError();
        s.RunId = RunId(nowMs, prior);
        diagnosticEvent?.Invoke("diagnostic_run_started", StartFields(s, resetTownRecords));
        return true;
    }

    /// <summary>The fields text of "diagnostic_run_started".</summary>
    // C++: TownDiagnosticRun.cpp:66-67 (sprintf_s into char fields[512]; %.6f -> CMath.FormatF)
    public static string StartFields(in TownDiagnosticData.State s, bool resetTownRecords)
    {
        CultureInfo ci = CultureInfo.InvariantCulture;
        return ",\"diagnostic_run_id\":" + s.RunId.ToString(ci) + ",\"target\":" + s.Config.Target.ToString(ci) +
            ",\"reset_town_records\":" + (resetTownRecords ? 1 : 0).ToString(ci) + ",\"model_mask\":" + ModelMask(s.Config).ToString(ci) +
            ",\"coverage_mask\":" + CoverageMask(s.Config).ToString(ci) + ",\"reduced_influence\":" + CMath.FormatF(s.Config.ReducedInfluence, 6) +
            ",\"underdog_percent\":" + s.Config.UnderdogPercent.ToString(ci) + ",\"balance_style_tier\":" + (s.Config.BalanceStyleTier ? 1 : 0).ToString(ci);
    }

    // C++: TownDiagnosticRun.cpp:71-76
    public static bool StartPreset(ref TownDiagnosticData.State s, bool resetTownRecords, uint nowMs, IHost host,
        Action<string, string>? diagnosticEvent = null)
    {
        if (!Mutable(s)) return false;
        s.Config = new TownDiagnosticData.Config(); s.Config.Target = 150;
        return StartConfigured(ref s, resetTownRecords, nowMs, host, diagnosticEvent);
    }

    // C++: TownDiagnosticRun.cpp:77
    public static bool Pause(ref TownDiagnosticData.State s, Action<string, string>? diagnosticEvent = null)
    {
        if (s.Status != (int)TownDiagnosticData.RunStatus.Active || s.Pending.Active) return false;
        s.Status = (int)TownDiagnosticData.RunStatus.Paused;
        diagnosticEvent?.Invoke("diagnostic_run_paused", ",\"diagnostic_run_id\":" + s.RunId.ToString(CultureInfo.InvariantCulture));
        return true;
    }

    // C++: TownDiagnosticRun.cpp:78
    public static bool Resume(ref TownDiagnosticData.State s, Action<string, string>? diagnosticEvent = null)
    {
        if (s.Status != (int)TownDiagnosticData.RunStatus.Paused) return false;
        s.Status = (int)TownDiagnosticData.RunStatus.Active;
        diagnosticEvent?.Invoke("diagnostic_run_resumed", ",\"diagnostic_run_id\":" + s.RunId.ToString(CultureInfo.InvariantCulture));
        return true;
    }

    // C++: TownDiagnosticRun.cpp:79
    public static bool End(ref TownDiagnosticData.State s, Action<string, string>? diagnosticEvent = null)
    {
        if ((s.Status != (int)TownDiagnosticData.RunStatus.Active && s.Status != (int)TownDiagnosticData.RunStatus.Paused) || s.Pending.Active) return false;
        s.Status = (int)TownDiagnosticData.RunStatus.Ended;
        CultureInfo ci = CultureInfo.InvariantCulture;
        diagnosticEvent?.Invoke("diagnostic_run_ended", ",\"diagnostic_run_id\":" + s.RunId.ToString(ci) + ",\"completed\":" + s.Counters.Completed.ToString(ci));
        return true;
    }

    // C++: TownDiagnosticRun.cpp:80
    public static bool SetTarget(ref TownDiagnosticData.State s, int target)
    {
        if (!Mutable(s) || target < 1 || target > 10000) return false;
        s.Config.Target = target; return true;
    }

    // C++: TownDiagnosticRun.cpp:81
    public static bool ToggleModel(ref TownDiagnosticData.State s, int model)
    {
        if (!Mutable(s) || model < 0 || model >= TownDiagnosticData.ModelCount) return false;
        s.Config.Models[model] = !s.Config.Models[model];
        if (EnabledModels(s.Config) == 0) { s.Config.Models[model] = true; return false; }
        return true;
    }

    // C++: TownDiagnosticRun.cpp:82
    public static bool ToggleCoverage(ref TownDiagnosticData.State s, int coverage)
    {
        if (!Mutable(s) || coverage < 0 || coverage >= TownDiagnosticData.CoverageCount) return false;
        s.Config.Coverage[coverage] = !s.Config.Coverage[coverage];
        if (EnabledCoverage(s.Config) == 0) { s.Config.Coverage[coverage] = true; return false; }
        return true;
    }

    // C++: TownDiagnosticRun.cpp:83
    public static bool CycleReducedInfluence(ref TownDiagnosticData.State s)
    {
        if (!Mutable(s)) return false;
        double v = s.Config.ReducedInfluence;
        s.Config.ReducedInfluence = v < .24 ? .25 : v < .49 ? .50 : v < .74 ? .75 : 0;
        return true;
    }

    // C++: TownDiagnosticRun.cpp:84
    public static bool CycleUnderdogPercent(ref TownDiagnosticData.State s)
    {
        if (!Mutable(s)) return false;
        s.Config.UnderdogPercent = (s.Config.UnderdogPercent + 10) % 60;
        return true;
    }

    // C++: TownDiagnosticRun.cpp:85
    public static bool ToggleStyleTierBalance(ref TownDiagnosticData.State s)
    {
        if (!Mutable(s)) return false;
        s.Config.BalanceStyleTier = !s.Config.BalanceStyleTier;
        return true;
    }

    // C++: TownDiagnosticRun.cpp:86-98
    public static TownMatchmakingPolicy.DiagnosticRequest NextRequest(in TownDiagnosticData.State s)
    {
        var r = new TownMatchmakingPolicy.DiagnosticRequest();
        if (s.Status != (int)TownDiagnosticData.RunStatus.Active || s.Pending.Active || s.Counters.Completed >= s.Config.Target) return r;
        r.Active = true; r.Model = LeastModel(s); r.Coverage = LeastCoverage(s); r.ReducedInfluence = s.Config.ReducedInfluence;
        for (int i = 0; i < TownDiagnosticData.CoverageCount; ++i) r.EnabledCoverage[i] = s.Config.Coverage[i];
        r.Influence = r.Model == (int)TownDiagnosticData.Model.StatsOnly ? 0 : r.Model == (int)TownDiagnosticData.Model.ReducedMmr ? s.Config.ReducedInfluence : 1;
        unchecked
        {
            r.Underdog = ((int)(((s.NextSequence - 1) * 37u) % 100u) < s.Config.UnderdogPercent);
            // Step every catalog type (see TypeNames), so a type added to the catalog is
            // targeted by the run instead of being skipped as a relaxation.
            uint types = (uint)TownMatchmakingPolicy.Detail.TypeCount();
            r.BalanceStyleTier = s.Config.BalanceStyleTier; r.DesiredType = (int)((s.NextSequence - 1) % types);
            r.DesiredTier = (int)(((s.NextSequence - 1) / types) % 3); r.Seed = s.RunId ^ s.NextSequence;
        }
        return r;
    }

    // C++: TownDiagnosticRun.cpp:99-106
    public static void Assign(ref TownDiagnosticData.State s, TownMatchmakingPolicy.Match m, double hours)
    {
        if (s.Status != (int)TownDiagnosticData.RunStatus.Active || s.Pending.Active || !m.Valid || !m.Diagnostic) return;
        var p = new TownDiagnosticData.Pending();
        p.Active = true; p.Sequence = s.NextSequence++; p.Model = m.DiagnosticModel; p.RequestedCoverage = m.RequestedCoverage;
        p.ActualCoverage = m.ActualCoverage; p.Relaxation = m.Relaxation; p.Underdog = m.DiagnosticUnderdog; p.SizeA = m.A.Count; p.SizeB = m.B.Count;
        p.AnnouncedHours = hours;
        for (int i = 0; i < TownDiagnosticData.ModelCount; ++i) { p.ScoreA[i] = m.ModelScoreA[i]; p.ScoreB[i] = m.ModelScoreB[i]; p.ProbabilityA[i] = m.ModelProbabilityA[i]; }
        if (s.Counters.LastCompletedHours > 0 && hours >= s.Counters.LastCompletedHours)
        {
            s.Counters.SchedulingHours += hours - s.Counters.LastCompletedHours; ++s.Counters.SchedulingSamples;
        }
        s.Pending = p; ++s.Counters.Opened;
    }

    // C++: TownDiagnosticRun.cpp:107
    public static void CancelPending(ref TownDiagnosticData.State s)
    {
        if (!s.Pending.Active) return;
        ++s.Counters.Cancelled; s.Pending = new TownDiagnosticData.Pending();
    }

    // C++: TownDiagnosticRun.cpp:108-112
    public static void RetryPending(ref TownDiagnosticData.State s)
    {
        if (!s.Pending.Active) return;
        if (s.NextSequence == unchecked(s.Pending.Sequence + 1)) s.NextSequence = s.Pending.Sequence;
        ++s.Counters.Cancelled; s.Pending = new TownDiagnosticData.Pending();
    }

    // C++: TownDiagnosticRun.cpp:113-118
    public static void RecoverPendingAfterLoad(ref TownDiagnosticData.State s, Action<string, string>? diagnosticEvent = null)
    {
        if (!s.Pending.Active) return;
        uint sequence = s.Pending.Sequence; ++s.Counters.Cancelled; s.Pending = new TownDiagnosticData.Pending();
        CultureInfo ci = CultureInfo.InvariantCulture;
        diagnosticEvent?.Invoke("diagnostic_assignment_cancelled", ",\"diagnostic_run_id\":" + s.RunId.ToString(ci) + ",\"diagnostic_sequence\":" +
            sequence.ToString(ci) + ",\"reason\":\"reload_does_not_restore_announced_bout\"");
    }

    // C++: TownDiagnosticRun.cpp:119
    public static void AbortPending(ref TownDiagnosticData.State s)
    {
        if (!s.Pending.Active) return;
        ++s.Counters.Aborted; s.Pending = new TownDiagnosticData.Pending();
    }

    // C++: TownDiagnosticRun.cpp:120-136
    public static void CompletePending(ref TownDiagnosticData.State s, SparPodium.Snapshot result, double hours,
        Action<string, string>? diagnosticEvent = null)
    {
        if (!s.Pending.Active) return;
        TownDiagnosticData.Pending p = s.Pending; s.Pending = new TownDiagnosticData.Pending();
        if (p.Relaxation >= 3) return;
        bool a = result.Outcome == SparPodium.OutcomeKind.TeamAWins, b = result.Outcome == SparPodium.OutcomeKind.TeamBWins;
        if (!a && !b) return;
        ++s.Counters.Completed; ++s.Counters.ModelCompleted[p.Model]; ++s.Counters.CoverageCompleted[p.ActualCoverage];
        if (a) ++s.Counters.TeamAWins; else ++s.Counters.TeamBWins;
        if (p.SizeA == p.SizeB) ++s.Counters.EqualWins;
        else if ((a && p.SizeA > p.SizeB) || (b && p.SizeB > p.SizeA)) ++s.Counters.LargerWins;
        else ++s.Counters.SmallerWins;
        if ((p.ProbabilityA[p.Model] >= .5) == a) ++s.Counters.FavoriteWins;
        s.Counters.LastCompletedHours = hours;
        if (s.Counters.Completed >= s.Config.Target)
        {
            s.Status = (int)TownDiagnosticData.RunStatus.Complete;
            CultureInfo ci = CultureInfo.InvariantCulture;
            diagnosticEvent?.Invoke("diagnostic_run_completed",
                ",\"diagnostic_run_id\":" + s.RunId.ToString(ci) + ",\"completed\":" + s.Counters.Completed.ToString(ci) +
                ",\"cancelled\":" + s.Counters.Cancelled.ToString(ci) + ",\"aborted\":" + s.Counters.Aborted.ToString(ci) +
                ",\"model_stats\":" + s.Counters.ModelCompleted[(int)TownDiagnosticData.Model.StatsOnly].ToString(ci) +
                ",\"model_reduced_mmr\":" + s.Counters.ModelCompleted[(int)TownDiagnosticData.Model.ReducedMmr].ToString(ci) +
                ",\"model_full_mmr\":" + s.Counters.ModelCompleted[(int)TownDiagnosticData.Model.FullMmr].ToString(ci) +
                ",\"coverage_1v1\":" + s.Counters.CoverageCompleted[(int)TownDiagnosticData.Coverage.Duel].ToString(ci) +
                ",\"coverage_equal_group\":" + s.Counters.CoverageCompleted[(int)TownDiagnosticData.Coverage.EqualGroup].ToString(ci) +
                ",\"coverage_difference_one\":" + s.Counters.CoverageCompleted[(int)TownDiagnosticData.Coverage.DifferenceOne].ToString(ci) +
                ",\"coverage_difference_large\":" + s.Counters.CoverageCompleted[(int)TownDiagnosticData.Coverage.DifferenceLarge].ToString(ci));
        }
    }

    /// <summary><paramref name="diagnosticError"/> = <c>LeaderboardStore::GetDiagnosticError()</c>.</summary>
    // C++: TownDiagnosticRun.cpp:137-145 (sprintf_s into char text[768]; %.1f -> CMath.FormatF, %u/%d -> the number)
    public static string StatusText(in TownDiagnosticData.State s, string diagnosticError)
    {
        double downtime = s.Counters.SchedulingSamples != 0 ? s.Counters.SchedulingHours / s.Counters.SchedulingSamples : 0;
        if (diagnosticError.Length != 0)
            return "Diagnostic disabled: " + diagnosticError +
                "\nOrdinary arena matchmaking remains active. Configure and start a new run to replace the invalid diagnostic state.";
        CultureInfo ci = CultureInfo.InvariantCulture;
        TownDiagnosticData.Counters c = s.Counters;
        return string.Format(ci,
            "Diagnostic: {0} | Run {1} | {2}/{3} complete\nModels: stats {4}, reduced {5}, full {6} | Coverage: 1v1 {7}, equal {8}, diff-1 {9}, diff-2+ {10}" +
            "\nMarkets: {11} opened, {12} cancelled, {13} aborted | Wins A/B {14}/{15} | Larger/smaller {16}/{17} | Favorites {18} | Avg downtime {19} game hours",
            TownDiagnosticData.StatusName(s.Status), s.RunId, c.Completed, s.Config.Target, c.ModelCompleted[0], c.ModelCompleted[1], c.ModelCompleted[2],
            c.CoverageCompleted[0], c.CoverageCompleted[1], c.CoverageCompleted[2], c.CoverageCompleted[3], c.Opened, c.Cancelled, c.Aborted,
            c.TeamAWins, c.TeamBWins, c.LargerWins, c.SmallerWins, c.FavoriteWins, CMath.FormatF(downtime, 1));
    }
}
