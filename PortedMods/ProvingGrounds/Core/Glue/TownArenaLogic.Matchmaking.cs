using System.Globalization;
using System.Text;

namespace ProvingGrounds.Core.Glue;

// Task 7.2 (TownArena.cpp:120-125, 157-173, 297-346, 574-590, 746-793; TW §2.2 steps 2-3, §2.4 "Search worker"; census docs/census.md): who may fight
// (Ready), the roster snapshot the town works from (SnapshotFor over TownMatchmakingRuntimeLogic.Collect), and the matchmaking preview's throttle and
// frozen roster (the first part of UpdateMatchmakingPreview (:746-793); its card half, from :795 on, is task 7.5's and calls these). The background search is
// the host's (src/Town/TownMatchmakingSearch.cs over Core/Glue/TownMatchmakingWorker.cs).

public sealed partial class TownArenaLogic<TId>
{
    /// <summary>C++ <c>now - g_previewTick &lt; 1000</c> (TownArena.cpp:752): the preview polls the search and the world at most once a second.</summary>
    public const uint PreviewPollMs = 1000;
    /// <summary>C++ <c>now - g_previewSnapshotTick &gt;= 10000</c> (TownArena.cpp:770): an unfrozen roster is read again after 10 s.</summary>
    public const uint PreviewRefreshMs = 10000;
    /// <summary>C++ <c>now - g_matchmakingDiagnosticTick &gt;= 30000</c> (TownArena.cpp:585): the discovery line at most every 30 s.</summary>
    public const uint DiagnosticLogMs = 30000;
    /// <summary>Port only (census, docs/census.md: at 3 s after arrival the town held 2 characters, filled within ~10 s): how long the registry's
    /// town must have had a player before a roster is read for a search (<see cref="RosterSettled"/>).</summary>
    public const uint RosterSettleMs = 10000;
    /// <summary>Port only: the settle clock's check period.</summary>
    public const uint RosterObserveMs = 1000;

    // C++: TownArena.cpp:120-125
    bool IngressQuarantined(TId c)
    {
        for (int i = 0; i < s.IngressQuarantine.Count; ++i)
            if (Eq.Equals(s.IngressQuarantine[i], c)) return true;
        return false;
    }

    // C++: TownArena.cpp:157-166
    public static string PlayerLineupKey(ITownRosterHost<TId> h, IReadOnlyList<TId> players)
    {
        var ids = new List<string>();
        for (int i = 0; i < players.Count; ++i)
            ids.Add(TownMatchmakingRuntimeLogic.SelectionIdentity(h, players[i]));
        ids.Sort(string.CompareOrdinal);
        var key = new StringBuilder();
        for (int i = 0; i < ids.Count; ++i)
            key.Append('|').Append(Encoding.UTF8.GetByteCount(ids[i]).ToString(CultureInfo.InvariantCulture)).Append(':').Append(ids[i]);
        return key.ToString();
    }

    // C++: TownArena.cpp:168-173
    public static bool EligibleOpponent(TownRosterSnapshot<TId> snapshot, string id)
    {
        for (int i = 0; i < snapshot.Npcs.Count; ++i)
            if (snapshot.Npcs[i].Id == id) return true;
        return false;
    }

    /// <summary>C++: TownArena.cpp:297-346 <c>Ready(c, doctor, bookedPlayer, reason)</c> (TW §2.2 step 3): not a player character (a booked player
    /// must be one, then judged by the same thresholds), not dead, unconscious or in combat, no first-aid need, lowest limb and blood both at least
    /// 0.90 (a medic: native medical capacity instead), not quarantined for missing the formation. <paramref name="reason"/> as the C++ writes it.
    /// The candidate line goes to the debug log unless discovery is quiet (<c>g_quietDiscovery</c>).</summary>
    public bool Ready(ITownHost<TId> h, TId c, bool doctor, bool bookedPlayer, out string reason)
    {
        reason = "health data unavailable";
        if (h.IsNull(c) || !h.Valid(c)) return false;
        if (!doctor && IngressQuarantined(c))
        {
            reason = "could not reach arena formation";
            return false;
        }
        var r = new TownArenaRuntimePolicy.Readiness();
        r.Valid = true;
        if (!h.HasWorld || h.PlayerCharacters() is not { } playerCharacters) return false;
        for (int i = 0; i < playerCharacters.Count; ++i)
            if (Eq.Equals(playerCharacters[i], c)) r.Player = true;
        if (bookedPlayer)
        {
            if (!r.Player) return false;
            r.Player = false; // Same recovery thresholds, explicitly player-owned.
        }
        r.Dead = h.IsDead(c);
        r.Unconscious = h.IsUnconscious(c);
        r.InCombat = h.InCombatMode(c);
        r.NeedsAid = h.NeedsStabilization(c);
        if (!h.ReadHealth(c, out RosterStatus.Snapshot health, out string error)) { reason = error; return false; }
        r.Blood = health.Blood;
        r.MinimumHealth = health.LowestLimb;
        bool medicallyAble = h.MedicallyAble(c);
        bool ready = doctor ? TownArenaRuntimePolicy.CanProvideAid(r, medicallyAble) :
            TownArenaRuntimePolicy.CanParticipate(r);
        if (ready) reason = "";
        else if (r.Player) reason = "belongs to the player squad";
        else if (r.Dead) reason = "dead";
        else if (r.Unconscious) reason = "unconscious";
        else if (r.InCombat) reason = "already in combat";
        else if (r.NeedsAid) reason = "needs first aid";
        else if (doctor && !medicallyAble) reason = "native medical capacity check failed";
        else reason = "recovering";
        // sprintf_s "Town candidate %s: ready=%d health=%.3f blood=%.3f player=%d dead=%d ko=%d combat=%d aid=%d" (%.3f: msvcr100, CMath.FormatF)
        string role = h.Role(c);
        static string I(bool b) => b ? "1" : "0";
        string detail = "Town candidate " + (role.Length != 0 ? role : "unknown") + ": ready=" + I(ready) +
            " health=" + CMath.FormatF(r.MinimumHealth, 3) + " blood=" + CMath.FormatF(r.Blood, 3) +
            " player=" + I(r.Player) + " dead=" + I(r.Dead) + " ko=" + I(r.Unconscious) + " combat=" + I(r.InCombat) + " aid=" + I(r.NeedsAid);
        if (!s.QuietDiscovery) h.Debug(detail);
        return ready;
    }

    // C++: TownArena.cpp:574-578
    public void RefreshMatchmakingRoster()
    {
        s.PreviewSnapshotValid = false;
        s.PreviewSnapshotTick = 0;
        s.PreviewTick = 0;
    }

    /// <summary>C++: TownArena.cpp:579-590 <c>SnapshotFor(players)</c>: Collect around the bound Town Registry with this file's readiness and the
    /// named legendaries, discovery quiet; the discovery line at most every 30 s.</summary>
    public TownRosterSnapshot<TId> SnapshotFor(ITownHost<TId> h, IReadOnlyList<TId> players)
    {
        bool previousQuiet = s.QuietDiscovery; s.QuietDiscovery = true;
        PlacedBuilding? registry = s.TownRegistry is { } r ? h.Resolve(r) : null;
        TownRosterSnapshot<TId> result;
        try
        {
            result = TownMatchmakingRuntimeLogic.Collect(h, registry, players,
                (TId c, bool doctor, bool booked, out string reason) => Ready(h, c, doctor, booked, out reason), true);
        }
        finally { s.QuietDiscovery = previousQuiet; }   // port only: a host throw never leaves discovery quiet
        uint now = h.NowMs;
        if (s.MatchmakingDiagnosticTick == 0 || now - s.MatchmakingDiagnosticTick >= DiagnosticLogMs)
        {
            h.Debug("Proving Grounds: matchmaking discovery - " + result.Diagnostics);
            s.MatchmakingDiagnosticTick = now;
        }
        return result;
    }

    /// <summary>The preview's card key (TownArena.cpp:755-759): the lineup key, the division, and each offer's epoch, draw, encounter and mode.</summary>
    // C++: TownArena.cpp:755-759
    public static string PreviewCardKey(string lineupKey, TownChallengePolicy.Card card)
    {
        var key = new StringBuilder();
        key.Append(lineupKey).Append(':').Append(card.Division.ToString(CultureInfo.InvariantCulture));
        for (int i = 0; i < 5; ++i)
            key.Append(':').Append(card.Offers[i].Epoch.ToString(CultureInfo.InvariantCulture)).Append(':')
                .Append(card.Offers[i].Draw.ToString(CultureInfo.InvariantCulture)).Append(':')
                .Append(card.Offers[i].Encounter.ToString(CultureInfo.InvariantCulture)).Append(':')
                .Append(card.Offers[i].Mode.ToString(CultureInfo.InvariantCulture));
        return key.ToString();
    }

    /// <summary>C++: TownArena.cpp:746-775, the first part of <c>UpdateMatchmakingPreview(players)</c>: nothing without an active save or while the
    /// lineup is locked; at most once a second; the card key; and the roster: read again when there is none, when the cards or the lineup changed,
    /// or every 10 s while no search of this generation is queued, otherwise frozen for the whole queued generation ("rebuilding them during a
    /// search starves the worker by cancelling all of its results"). False where the C++ returns. <paramref name="sameCards"/> and
    /// <paramref name="lineupKey"/> are the C++ locals the rest of the function (task 7.5) reads; the roster is <see cref="TownArenaState{TId}.PreviewSnapshot"/>.
    /// <para>Port only (census): false, before the throttle, until <see cref="RosterSettled"/>: the town fills about 10 s after the player arrives,
    /// and a search run on the half-empty roster would answer every card "No suitable recovered opponents" until the next refresh.</para></summary>
    public bool UpdatePreviewRoster(ITownHost<TId> h, IReadOnlyList<TId> players, TownChallengePolicy.Card card, out bool sameCards, out string lineupKey)
    {
        sameCards = false;
        lineupKey = "";
        if (!h.HasActiveSave || IsLineupLocked()) return false;
        if (!RosterSettled(h)) return false;   // port only (census)
        uint now = h.NowMs;
        // Polling and world discovery are intentionally throttled. The worker
        // keeps searching between UI refreshes without touching Kenshi state.
        if (s.PreviewTick != 0 && now - s.PreviewTick < PreviewPollMs) return false;
        s.PreviewTick = now;
        lineupKey = PlayerLineupKey(h, players);
        string key = PreviewCardKey(lineupKey, card);
        sameCards = key == s.PreviewKey;
        s.PreviewKey = key;
        bool searchPending = false;
        for (int slot = 0; slot < 5; ++slot)
            searchPending = searchPending || s.SearchQueued[slot];
        // Freeze one immutable roster for the whole queued generation. Exact
        // live-stat fingerprints can change every frame while actors recover;
        // rebuilding them during a search starves the worker by cancelling all
        // of its results. Once the queue drains, refresh health periodically.
        if (!s.PreviewSnapshotValid || !sameCards ||
            (!searchPending && (s.PreviewSnapshotTick == 0 || now - s.PreviewSnapshotTick >= PreviewRefreshMs)))
        {
            s.PreviewSnapshot = SnapshotFor(h, players);
            s.PreviewSnapshotValid = true;
            s.PreviewSnapshotTick = now;
        }
        return true;
    }

    /// <summary>C++: TownArena.cpp:780-793: when the search context (the card key and the roster's digest) changed, a new search generation
    /// (never 0), the worker's queue replaced, and every slot's queued / completed / searching flag cleared. True when it began one.</summary>
    public bool RestartSearchIfContextChanged(ITownHost<TId> h, string searchContext)
    {
        if (searchContext == s.RetryContext) return false;
        s.RetryContext = searchContext;
        BeginSearchGeneration(h);
        h.Debug("Proving Grounds: started lazy challenge search generation");
        for (int slot = 0; slot < 5; ++slot)
        {
            s.SearchQueued[slot] = false;
            s.SearchCompleted[slot] = false;
            s.Searching[slot] = false;
        }
        return true;
    }

    /// <summary>C++ <c>++g_searchGeneration; if (!g_searchGeneration) ++g_searchGeneration; TownMatchmakingSearch::BeginGeneration(g_searchGeneration)</c>
    /// (TownArena.cpp:543-545, :784-786): the next generation, skipping 0.</summary>
    public uint BeginSearchGeneration(ITownHost<TId> h)
    {
        unchecked { ++s.SearchGeneration; }
        if (s.SearchGeneration == 0) ++s.SearchGeneration;
        h.MatchmakingBeginGeneration(s.SearchGeneration);
        return s.SearchGeneration;
    }

    /// <summary>Port only (census): the settle clock, once a second from <see cref="Tick"/>: started at the first check that finds a player
    /// character in the bound Town Registry's town, stopped when none is.</summary>
    void ObserveRoster(ITownHost<TId> h, uint now)
    {
        if (s.RosterObserveCheck != 0 && now - s.RosterObserveCheck < RosterObserveMs) return;
        s.RosterObserveCheck = now == 0 ? 1u : now;
        if (!HasPlayerInRegistryTown(h, s.TownRegistry)) { s.RosterObservedSince = 0; return; }
        if (s.RosterObservedSince == 0) s.RosterObservedSince = now == 0 ? 1u : now;
    }

    /// <summary>Port only (census, docs/divergences.md "Matchmaking snapshot and search worker"): the registry's town has had a player character for
    /// at least 10 s, so the town's people have loaded and a roster read for a search sees them. Task 7.3's planning asks it too.</summary>
    public bool RosterSettled(ITownHost<TId> h) =>
        s.RosterObservedSince != 0 && h.NowMs - s.RosterObservedSince >= RosterSettleMs;
}
