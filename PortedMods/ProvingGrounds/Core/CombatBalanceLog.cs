// Port of the event text and state of upstream/ProvingGrounds/plugin/src/CombatBalanceLog.h/.cpp (Proving Grounds by Shutori, GPLv3).
using System.Runtime.CompilerServices;

namespace ProvingGrounds.Core;

/// <summary>The balance telemetry of C++ CombatBalanceLog (TW §2.17): JSON-lines events for match start / result / abort, the NPC
/// bout market and player challenges, written through <see cref="PGLog.Combat"/> (ambient bouts, markets, diagnostic run) and
/// <see cref="PGLog.Challenge"/> (player challenges). Event names, field order and number formats are the C++'s.
///
/// <para>The C++ reads the game itself (<c>Character*</c> arrays, <c>ou</c>, LeaderboardStore, TownArena, SparSession,
/// ArenaCombatProfile, RosterStatus, TownMatchmakingRuntime). Here the glue reads those and passes plain data: one
/// <see cref="FighterInfo"/> per fighter, one <see cref="ParticipantInfo"/> per result row, the standings list, the TownArena flags
/// as bools, and <see cref="IHost"/> for the three reads every event makes (game hours, active save key, the diagnostic run state).
/// <c>GetTickCount64()</c> is a <c>ulong nowMs</c> parameter. The module's globals are <see cref="State"/>.</para>
/// <para>Not thread-safe (neither is the C++, whose globals are unguarded): call it from the sim thread only. <see cref="PGLog"/>
/// itself is safe from any thread. Arrays: <c>fighters</c> and <c>teams</c> hold at least <c>count</c> elements, and
/// <c>participants</c> at least <c>snapshot.FighterCount</c> (the C++ reads past the end otherwise).</para></summary>
public static class CombatBalanceLog
{
    /// <summary>The reads the C++ makes on every event line and diagnostic block.</summary>
    public interface IHost
    {
        /// <summary><c>Hours()</c> (CombatBalanceLog.cpp:45): <c>ou ? ou->getTimeStamp_inGameHours().getTotalHours() : 0</c>.</summary>
        double GameHours();
        /// <summary><c>LeaderboardStore::GetActiveSaveKey()</c>.</summary>
        string ActiveSaveKey();
        /// <summary><c>TownDiagnosticRun::Get()</c> (= <c>LeaderboardStore::GetTownDiagnostic()</c>), read only.</summary>
        TownDiagnosticData.State Diagnostic();
    }

    /// <summary>What <c>Fighter()</c> and <c>StartAbility()</c> read from one live <c>Character*</c> (CombatBalanceLog.cpp:90-157).
    /// For a null character use <see cref="Unavailable"/>, NEVER <c>default(FighterInfo)</c>: the C++ gives a null character
    /// ability -1 and rating <c>MmrRating::kDefaultMmr</c> (100), where <c>default</c> holds 0 for both.</summary>
    public struct FighterInfo
    {
        /// <summary>What the C++ reads for a null <c>Character*</c>: <c>Identity</c> "" and <c>Role</c> ""
        /// (TownMatchmakingRuntime.cpp:21-29), not valid, a default <c>Profile</c> (valid false, style "", stats 0,
        /// ArenaCombatProfile.cpp:59-60 and Read's early returns :63-66) whose <c>Evaluate</c> is -1 (:89-91), <c>GetRating(NULL)</c> = <c>kDefaultMmr</c>
        /// (LeaderboardStore.cpp:427-428) and no health (<c>RosterStatus::Read</c> false, RosterStatus.cpp:16). A non-null but invalid
        /// character reads the same except its rating, which the glue takes from <c>GetRating</c>.</summary>
        public static FighterInfo Unavailable => new FighterInfo
        {
            Identity = "", Role = "", Valid = false, Name = "", ProfileValid = false, Style = "",
            Ability = -1, Rating = MmrRating.DefaultMmr, HasHealth = false,
        };

        /// <summary><c>TownMatchmakingRuntime::Identity(c)</c> ("" for none, as that function returns).</summary>
        public string? Identity;
        /// <summary><c>TownMatchmakingRuntime::Role(c)</c>: the "template" field and the catalog lookup key.</summary>
        public string? Role;
        /// <summary><c>c &amp;&amp; c->isValid()</c>.</summary>
        public bool Valid;
        /// <summary><c>c->getName()</c>; read only when <see cref="Valid"/> ("Unavailable" otherwise).</summary>
        public string? Name;
        /// <summary><c>ArenaCombatProfile::Read(c)</c>: <c>valid</c>, <c>style</c> and the six stats.</summary>
        public bool ProfileValid;
        public string? Style;
        public double Attack, Defence, Strength, Toughness, Dexterity, WeaponSkill;
        /// <summary><c>ArenaCombatProfile::Evaluate(profile)</c> (-1 when the profile is unusable).</summary>
        public double Ability;
        /// <summary><c>LeaderboardStore::GetRating(c, kind)</c>, read even when not <see cref="Valid"/> (StartAbility does). Kind:
        /// <see cref="StartMatch"/> = <c>LeaderboardData::ForMatch(town)</c>, <see cref="OpenMarket"/> = <c>Town</c> (StartAbility
        /// always reads <c>Town</c>, and runs only for a town match, where the two are the same).</summary>
        public float Rating;
        /// <summary><c>RosterStatus::Read(c, health)</c> returned true, and its three values.</summary>
        public bool HasHealth;
        public float LowestLimb, Blood, Recovery;
    }

    /// <summary>What <c>FinishMatch</c> reads from <c>SparSession::GetParticipant(row.id)</c> for one result row
    /// (CombatBalanceLog.cpp:210-220). <c>default(ParticipantInfo)</c> is what the C++ reads for a null participant (identity "",
    /// no health, not valid; a null <see cref="Identity"/> quotes as "").</summary>
    public struct ParticipantInfo
    {
        /// <summary><c>TownMatchmakingRuntime::Identity(c)</c> (also for a null participant).</summary>
        public string? Identity;
        /// <summary><c>RosterStatus::Read(c, health)</c> returned true, and its three values.</summary>
        public bool HasHealth;
        public float LowestLimb, Blood, Recovery;
        /// <summary><c>c &amp;&amp; c->isValid()</c>, and then <c>c->isDead()</c>, <c>c->isUnconcious()</c>.</summary>
        public bool Valid, Dead, Unconscious;
    }

    // Everything a booked challenge is offered under. Gathered at the booking
    // site because that is the only place the resolved offer, the priced quote and
    // the two team scores are in scope together; the bout it becomes is described
    // later by StartMatch, which carries the same challenge_id.
    /// <summary>The C++ <c>std::string opponents[4]</c> (and names, roles) of <see cref="ChallengeCard"/>: a fixed array of 4.</summary>
    [InlineArray(4)]
    public struct Strings4
    {
        string _element0;
    }

    /// <summary>C++: CombatBalanceLog.h:17-31 (built at the booking site, passed by const reference).</summary>
    public sealed class ChallengeCard
    {
        public int Slot = -1;          // which offer was booked, 0-4, or 5 for the Skarn slot
        public int Division;           // Easy / Medium / Hard
        public int Rarity;             // Common / Uncommon / Rare / Legendary
        public int Encounter;          // TownChallengePolicy::Encounter
        public int Format, Mode;
        public int Players;            // player fighters committed
        public int Enemies;            // opponents in the lineup
        public double OwnScore;        // TeamScore of the player's team
        public double EnemyScore;      // TeamScore of the opponents, i.e. what priced the quote
        public Strings4 Opponents, OpponentNames, OpponentRoles;
        public int Stake, Payout;

        // C++: CombatBalanceLog.h:29-30 (the std::string members default to "")
        public ChallengeCard()
        {
            for (int i = 0; i < 4; ++i) Opponents[i] = OpponentNames[i] = OpponentRoles[i] = "";
        }
    }

    /// <summary>The C++ anonymous-namespace globals (CombatBalanceLog.cpp:24-44).</summary>
    internal sealed class State
    {
        public uint MatchId, MarketId, ChallengeId;
        // Monotonic per-process record counter. Unlike the id families it is written
        // on EVERY event, so a reader can key a join on (session, record) and never
        // meet the cross-session id collisions described in
        // balancing/matchmaking-balance-insights.md.
        public uint Record;
        public bool Active;
        public double StartedHours;
        public ulong StartedWall;
        // Bout-start model scores. Written for a controlled diagnostic bout AND for a
        // player challenge, so it is no longer diagnostic-only even though both feed
        // the same start_* field names; match_result repeats it so a result line still
        // carries the chance its bout was measured against.
        public string StartFields = "";
        // Which channel the live match belongs to, and which challenge it came from.
        // matchChallengeId is deliberately held across ChallengeSettled, because that
        // call clears challengeId and match_result must still be able to name its
        // challenge whichever of the two fires first.
        public bool PlayerMatch;
        public uint MatchChallengeId;
        public int ChallengeStake, ChallengePayout;

        // C++: CombatBalanceLog.cpp:24-44
        public static State Shipped() => new State();

        public State Clone() => (State)MemberwiseClone();
    }

    static readonly State g_state = State.Shipped();
    // Test seam (no C++ counterpart): a state visible only to the flow that set it.
    static readonly AsyncLocal<State?> s_test = new AsyncLocal<State?>();
    internal static State Current => s_test.Value ?? g_state;

    /// <summary>Test hook: <see cref="Current"/> is <paramref name="state"/> in the calling flow until disposed.</summary>
    internal static IDisposable UseForTest(State state)
    {
        State? previous = s_test.Value;
        s_test.Value = state;
        return new Restore(previous);
    }

    sealed class Restore : IDisposable
    {
        readonly State? _previous;
        bool _done;
        public Restore(State? previous) { _previous = previous; }
        public void Dispose()
        {
            if (_done) return;
            _done = true;
            s_test.Value = _previous;
        }
    }

    /// <summary><c>std::ostringstream</c>, classic locale, <c>std::setprecision(10)</c>, default float format = C <c>%.10g</c>;
    /// a non-finite value is <c>null</c>.</summary>
    // C++: CombatBalanceLog.cpp:46-49
    internal static string Number(double value)
    {
        if (!double.IsFinite(value)) return "null";
        return CMath.FormatG(value, 10);
    }

    // The live balance levers, stamped onto the events a balance read depends on
    // so a captured log always describes the parameters that produced it.
    // C++: CombatBalanceLog.cpp:52-56
    static string TuningFields() => "," + BalanceTuning.WriteJson();

    // C++: CombatBalanceLog.cpp:57-61
    static string Line(State s, IHost host, string ev, string fields) =>
        "\"event\":" + PGLog.Quote(ev) + ",\"record\":" + Number(unchecked(++s.Record)) +
        ",\"game_hours\":" + Number(host.GameHours()) +
        ",\"save\":" + PGLog.Quote(host.ActiveSaveKey()) + fields;

    // NPC bouts, their markets and the diagnostic run: the ambient population.
    // C++: CombatBalanceLog.cpp:63
    static void Event(State s, IHost host, string ev, string fields) => PGLog.Combat(Line(s, host, ev, fields));

    // Player challenges, in their own file, so a balance read of either population
    // is never diluted by the other. Both channels share the session id and the
    // record counter and can be interleaved on (session, record).
    // C++: CombatBalanceLog.cpp:67
    static void ChallengeEvent(State s, IHost host, string ev, string fields) => PGLog.Challenge(Line(s, host, ev, fields));

    // C++: CombatBalanceLog.cpp:68
    internal static string Field(string name, double value) => "," + PGLog.Quote(name) + ":" + Number(value);
    // C++: CombatBalanceLog.cpp:68 (a bool argument promotes to 1.0 / 0.0)
    static string Field(string name, bool value) => Field(name, value ? 1.0 : 0.0);

    // C++: CombatBalanceLog.cpp:69-86
    static string DiagnosticFields(IHost host)
    {
        TownDiagnosticData.State d = host.Diagnostic();
        if (!d.Pending.Active) return "";
        TownDiagnosticData.Pending p = d.Pending;
        string out_ = Field("diagnostic_run_id", d.RunId) + Field("diagnostic_sequence", p.Sequence) +
            ",\"diagnostic_model\":" + PGLog.Quote(TownDiagnosticData.ModelName(p.Model)) +
            ",\"diagnostic_requested_coverage\":" + PGLog.Quote(TownDiagnosticData.CoverageName(p.RequestedCoverage)) +
            ",\"diagnostic_actual_coverage\":" + PGLog.Quote(TownDiagnosticData.CoverageName(p.ActualCoverage)) +
            Field("diagnostic_relaxation", p.Relaxation) + Field("diagnostic_underdog", p.Underdog) +
            Field("diagnostic_size_a", p.SizeA) + Field("diagnostic_size_b", p.SizeB);
        for (int i = 0; i < TownDiagnosticData.ModelCount; ++i)
        {
            string suffix = i == 0 ? "stats" : i == 1 ? "reduced" : "full";
            out_ += Field("score_a_" + suffix, p.ScoreA[i]);
            out_ += Field("score_b_" + suffix, p.ScoreB[i]);
            out_ += Field("predicted_p_a_" + suffix, p.ProbabilityA[i]);
        }
        return out_;
    }

    // Per-side ability and rating, read from the live fighters. Shared by the
    // diagnostic's three models and a player challenge's single one, so both quote
    // the same underlying quantity.
    // C++: CombatBalanceLog.cpp:90-102
    static bool StartAbility(FighterInfo[]? fighters, MatchRules.MatchTeam[]? teams, int count,
        List<double> abilityA, List<double> ratingA, List<double> abilityB, List<double> ratingB)
    {
        if (fighters == null || teams == null || count < 2) return false;
        for (int i = 0; i < count; ++i)
        {
            double ability = fighters[i].Ability;
            double rating = fighters[i].Rating;
            if (ability <= 0 || rating < 0) return false;
            if (teams[i] == MatchRules.MatchTeam.TeamA) { abilityA.Add(ability); ratingA.Add(rating); }
            else if (teams[i] == MatchRules.MatchTeam.TeamB) { abilityB.Add(ability); ratingB.Add(rating); }
        }
        return abilityA.Count != 0 && abilityB.Count != 0;
    }

    // A player challenge's model chance, under the same start_* names the
    // diagnostic uses for its full model, so one reader covers both populations.
    // The full model only: the reduced-influence variant is a property of that
    // run's config, and a booked challenge has no such config.
    // C++: CombatBalanceLog.cpp:107-116
    static string PlayerStartScores(FighterInfo[]? fighters, MatchRules.MatchTeam[]? teams, int count)
    {
        List<double> abilityA = new(), ratingA = new(), abilityB = new(), ratingB = new();
        if (!StartAbility(fighters, teams, count, abilityA, ratingA, abilityB, ratingB))
            return Field("start_scores_available", 0);
        double scoreA = TownMatchmakingPolicy.TeamScore(abilityA.ToArray(), ratingA.ToArray(), abilityA.Count, 1.0);
        double scoreB = TownMatchmakingPolicy.TeamScore(abilityB.ToArray(), ratingB.ToArray(), abilityB.Count, 1.0);
        if (scoreA <= 0 || scoreB <= 0) return Field("start_scores_available", 0);
        return Field("start_scores_available", 1) + Field("start_score_a_full", scoreA) + Field("start_score_b_full", scoreB) +
            Field("start_predicted_p_a_full", TownBettingPolicy.WinProbability(scoreA, scoreB));
    }

    // C++: CombatBalanceLog.cpp:117-135
    static string DiagnosticStartScores(IHost host, FighterInfo[]? fighters, MatchRules.MatchTeam[]? teams, int count)
    {
        TownDiagnosticData.State d = host.Diagnostic();
        if (!d.Pending.Active) return "";
        List<double> abilityA = new(), ratingA = new(), abilityB = new(), ratingB = new();
        if (!StartAbility(fighters, teams, count, abilityA, ratingA, abilityB, ratingB))
            return Field("start_scores_available", 0);
        string out_ = Field("start_scores_available", 1);
        double[] influences = { 0, d.Config.ReducedInfluence, 1 };
        for (int i = 0; i < TownDiagnosticData.ModelCount; ++i)
        {
            string suffix = i == 0 ? "stats" : i == 1 ? "reduced" : "full";
            double scoreA = TownMatchmakingPolicy.TeamScore(abilityA.ToArray(), ratingA.ToArray(), abilityA.Count, influences[i]);
            double scoreB = TownMatchmakingPolicy.TeamScore(abilityB.ToArray(), ratingB.ToArray(), abilityB.Count, influences[i]);
            if (scoreA <= 0 || scoreB <= 0) return Field("start_scores_available", 0);
            out_ += Field("start_score_a_" + suffix, scoreA);
            out_ += Field("start_score_b_" + suffix, scoreB);
            out_ += Field("start_predicted_p_a_" + suffix, TownBettingPolicy.WinProbability(scoreA, scoreB));
        }
        return out_;
    }

    // C++: CombatBalanceLog.cpp:136-157
    static string Fighter(in FighterInfo c, int slot, MatchRules.MatchTeam team, IReadOnlyList<LeaderboardData.Standing>? standings)
    {
        bool valid = c.Valid;
        string id = c.Identity ?? "";
        string out_ = "{\"id\":" + PGLog.Quote(id) + ",\"template\":" + PGLog.Quote(c.Role) +
            ",\"name\":" + PGLog.Quote(valid ? c.Name : "Unavailable") + Field("slot", slot) + Field("team", (int)team) +
            ",\"style\":" + PGLog.Quote(c.Style) + Field("profile_valid", c.ProfileValid) +
            Field("attack", c.Attack) + Field("defence", c.Defence) + Field("strength", c.Strength) +
            Field("toughness", c.Toughness) + Field("dexterity", c.Dexterity) + Field("weapon_skill", c.WeaponSkill) +
            Field("ability", c.Ability);
        TownFighterCatalog.Entry? catalog = TownFighterCatalog.Find(c.Role ?? "");
        out_ += ",\"catalog_type\":" + PGLog.Quote(catalog != null ? catalog.Type : "") + Field("catalog_tier", catalog != null ? catalog.Tier : -1);
        int wins = 0, losses = 0, matches = 0;
        if (standings != null)
            for (int i = 0; i < standings.Count; ++i) if (id.Length != 0 && standings[i].Id == id)
            {
                wins = standings[i].Wins; losses = standings[i].Losses; matches = standings[i].Matches; break;
            }
        out_ += Field("mmr", valid ? c.Rating : 0) + Field("wins", wins) + Field("losses", losses) + Field("matches", matches);
        if (c.HasHealth) out_ += Field("lowest_limb", c.LowestLimb) + Field("blood", c.Blood) + Field("recovery", c.Recovery);
        return out_ + "}";
    }

    // C++: CombatBalanceLog.cpp:158-162
    static string Fighters(FighterInfo[] fighters, MatchRules.MatchTeam[] teams, int count, IReadOnlyList<LeaderboardData.Standing>? standings)
    {
        string out_ = ",\"fighters\":[";
        for (int i = 0; i < count; ++i) { if (i != 0) out_ += ','; out_ += Fighter(fighters[i], i, teams[i], standings); }
        return out_ + "]";
    }

    /// <summary><c>town</c> = <c>TownArena::OwnsMatch()</c>, <c>player</c> = <c>TownArena::IsPlayerMatch()</c>; <c>standings</c> =
    /// <c>LeaderboardStore::GetStandings(ForMatch(town))</c>; <c>nowMs</c> = <c>GetTickCount64()</c>.</summary>
    // C++: CombatBalanceLog.cpp:165-181
    public static void StartMatch(IHost host, MatchRules.MatchMode mode, FighterInfo[] fighters, MatchRules.MatchTeam[] teams, int count,
        bool town, bool player, IReadOnlyList<LeaderboardData.Standing>? standings, ulong nowMs)
    {
        State s = Current;
        AbortMatch(host, "superseded");
        s.MatchId = PGLog.NextId(); s.Active = true; s.StartedHours = host.GameHours(); s.StartedWall = nowMs;
        // A player challenge is its own population: it goes to the challenge file,
        // and carries no market and no diagnostic block. It does now carry a model
        // chance, which before this only the ambient path had.
        s.PlayerMatch = town && player;
        s.MatchChallengeId = s.PlayerMatch ? s.ChallengeId : 0;
        s.StartFields = !town ? "" : player ? PlayerStartScores(fighters, teams, count) : DiagnosticStartScores(host, fighters, teams, count);
        string body = Field("match_id", s.MatchId) + Field("market_id", town && !player ? s.MarketId : 0) +
            Field("challenge_id", town && player ? s.ChallengeId : 0) + Field("mode", (int)mode) +
            ",\"kind\":" + PGLog.Quote(town ? player ? "town_player" : "town_npc" : "arena") + TuningFields() +
            (town && !player ? DiagnosticFields(host) : "") + s.StartFields +
            Fighters(fighters, teams, count, standings);
        if (s.PlayerMatch) ChallengeEvent(s, host, "match_start", body); else Event(s, host, "match_start", body);
    }

    /// <summary><c>participants[i]</c> is what <c>SparSession::GetParticipant(snapshot.fighters[i].id)</c> reads;
    /// <c>pendingEndKind</c> = <c>SparSession::GetPendingEndKind()</c>; <c>nowMs</c> = <c>GetTickCount64()</c>.</summary>
    // C++: CombatBalanceLog.cpp:182-226
    public static void FinishMatch(IHost host, SparPodium.Snapshot snapshot, int stopReason, ParticipantInfo[] participants,
        MatchRules.MatchEndKind pendingEndKind, ulong nowMs)
    {
        State s = Current;
        if (!s.Active) return;
        // Damage concentration: the single strongest predictor of the winner in
        // the recorded sample was the top damage dealer's side (27/36, p=0.002),
        // not the additive team score. Logged so it is measurable directly.
        float totalDamage = 0.0f, teamDamageA = 0.0f, teamDamageB = 0.0f, topDamage = 0.0f;
        for (int i = 0; i < snapshot.FighterCount; ++i)
        {
            float dealt = snapshot.Fighters[i].DamageDealt;
            totalDamage += dealt;
            if (dealt > topDamage) topDamage = dealt;
            if (snapshot.Fighters[i].Team == MatchRules.MatchTeam.TeamA) teamDamageA += dealt;
            else teamDamageB += dealt;
        }
        SparPodium.OutcomeKind o = snapshot.Outcome;
        // challenge_id leads, so a player result names its own challenge even when
        // ChallengeSettled has already cleared the live id.
        string fields = Field("match_id", s.MatchId) + Field("challenge_id", s.MatchChallengeId) + Field("outcome", (int)o) + Field("stop_reason", stopReason) +
            ",\"end_kind\":" + PGLog.Quote(MatchRules.EndKindName(pendingEndKind)) +
            Field("game_minutes", (host.GameHours() - s.StartedHours) * 60) + Field("wall_seconds", unchecked(nowMs - s.StartedWall) / 1000.0) +
            Field("last_standing_slot", snapshot.LastStandingId) + ",\"outcome_name\":" + PGLog.Quote(
                o == SparPodium.OutcomeKind.TeamAWins ? "team_a_wins" :
                o == SparPodium.OutcomeKind.TeamBWins ? "team_b_wins" :
                o == SparPodium.OutcomeKind.LastStanding ? "last_standing" :
                o == SparPodium.OutcomeKind.Draw ? "draw" : "stopped") +
            Field("team_damage_a", teamDamageA) + Field("team_damage_b", teamDamageB) +
            Field("top_damage_share", totalDamage > 0 ? topDamage / totalDamage : 0f) +
            Field("total_damage", totalDamage) + ",\"fighters\":[";
        for (int i = 0; i < snapshot.FighterCount; ++i)
        {
            SparPodium.FighterRow r = snapshot.Fighters[i];
            ParticipantInfo c = participants[i];
            if (i != 0) fields += ',';
            fields += "{\"id\":" + PGLog.Quote(c.Identity) + ",\"name\":" + PGLog.Quote(r.Name) + Field("slot", r.Id) + Field("team", (int)r.Team) +
                Field("damage_dealt", r.DamageDealt) + Field("damage_taken", r.DamageTaken) + Field("damage_mitigated", r.DamageMitigated) +
                Field("damage_share", totalDamage > 0 ? r.DamageDealt / totalDamage : 0f) +
                Field("hits", r.HitsLanded) + Field("blocks", r.Blocks) + Field("misses", r.Misses) + Field("dodges", r.Dodges) +
                Field("elimination_order", r.EliminationIndex) + Field("rating_updated", r.RatingUpdated) +
                Field("mmr_before", r.RatingBefore) + Field("mmr_after", r.RatingAfter);
            if (c.HasHealth) fields += Field("lowest_limb", c.LowestLimb) + Field("blood", c.Blood) + Field("recovery", c.Recovery);
            if (c.Valid) fields += Field("dead", c.Dead) + Field("unconscious", c.Unconscious);
            fields += "}";
        }
        string body = fields + "]" + Field("damage_mitigated_available", 0) + TuningFields() + DiagnosticFields(host) + s.StartFields;
        if (s.PlayerMatch) ChallengeEvent(s, host, "match_result", body); else Event(s, host, "match_result", body);
        s.Active = false; s.StartFields = "";
    }

    // C++: CombatBalanceLog.cpp:227-234
    public static void AbortMatch(IHost host, string reason)
    {
        State s = Current;
        if (!s.Active) return;
        // Teardown-safe: no Character or world dereferences, no fabricated result.
        string body = "\"event\":\"match_aborted\"" + Field("record", unchecked(++s.Record)) + Field("match_id", s.MatchId) +
            ",\"reason\":" + PGLog.Quote(reason) + DiagnosticFields(host) + s.StartFields;
        if (s.PlayerMatch) PGLog.Challenge(body); else PGLog.Combat(body);
        s.Active = false; s.StartFields = "";
    }

    /// <summary><c>scheduledGameHours</c> = <c>TownArena::GetPlannedNpcStartHours()</c>; <c>standings</c> =
    /// <c>LeaderboardStore::GetStandings(Town)</c>; each fighter's <see cref="FighterInfo.Rating"/> is its Town rating.</summary>
    // C++: CombatBalanceLog.cpp:235-242
    public static void OpenMarket(IHost host, FighterInfo[] fighters, MatchRules.MatchTeam[] teams, int count, double scoreA, double scoreB,
        bool eligible, double scheduledGameHours, IReadOnlyList<LeaderboardData.Standing>? standings)
    {
        State s = Current;
        s.MarketId = PGLog.NextId();
        Event(s, host, "market_open", Field("market_id", s.MarketId) + Field("eligible", eligible) + Field("ability_a", scoreA) + Field("ability_b", scoreB) + Field("scheduled_game_hours", scheduledGameHours) +
            Field("predicted_p_a", TownBettingPolicy.WinProbability(scoreA, scoreB)) +
            Field("return_per_1000_a", TownBettingPolicy.ReturnCats(1000, scoreA, scoreB)) +
            Field("return_per_1000_b", TownBettingPolicy.ReturnCats(1000, scoreB, scoreA)) + TuningFields() + DiagnosticFields(host) +
            Fighters(fighters, teams, count, standings));
    }

    // C++: CombatBalanceLog.cpp:243-245 (fields defaults to "", CombatBalanceLog.h:11)
    public static void MarketEvent(IHost host, string ev, string fields = "")
    {
        State s = Current;
        if (s.MarketId != 0) Event(s, host, ev, Field("market_id", s.MarketId) + fields + DiagnosticFields(host));
    }

    // C++: CombatBalanceLog.cpp:246-250
    public static void CloseMarket(IHost host, int winner, int side, int stake, int quotedReturn, int credit, bool hadTicket)
    {
        MarketEvent(host, "market_settled", Field("winner_side", winner) + Field("had_ticket", hadTicket) + Field("bet_side", side) +
            Field("stake", stake) + Field("quoted_return", quotedReturn) + Field("credited", credit) + Field("player_net", hadTicket ? unchecked(credit - stake) : 0));
        Current.MarketId = 0;
    }

    // C++: CombatBalanceLog.cpp:251-276
    public static void ChallengeAccepted(IHost host, ChallengeCard card)
    {
        State s = Current;
        s.ChallengeId = PGLog.NextId();
        s.ChallengeStake = card.Stake; s.ChallengePayout = card.Payout;
        // odds / implied_p are the book's own view of the bout (payout over
        // buy-in). model_p is the same model the ambient book prices from, on the
        // same TeamScore quantity, so the two chances sit on one line and can be
        // compared against each other and against the realised result.
        string fields = Field("challenge_id", s.ChallengeId) + Field("slot", card.Slot) +
            Field("division", card.Division) + ",\"division_name\":" + PGLog.Quote(TownChallengePolicy.DivisionName(card.Division)) +
            Field("rarity", card.Rarity) + ",\"rarity_name\":" + PGLog.Quote(TownChallengePolicy.RarityName(card.Rarity)) +
            Field("encounter", card.Encounter) + ",\"encounter_title\":" + PGLog.Quote(TownChallengePolicy.Title(card.Encounter)) +
            Field("format", card.Format) + Field("mode", card.Mode) + ",\"mode_name\":" + PGLog.Quote(TownChallengePolicy.ModeName(card.Mode)) +
            Field("players", card.Players) + Field("enemies", card.Enemies) +
            Field("own_score", card.OwnScore) + Field("enemy_score", card.EnemyScore) +
            Field("stake", card.Stake) + Field("quoted_return", card.Payout) +
            Field("odds", card.Stake > 0 ? (double)card.Payout / card.Stake : 0) +
            Field("implied_p", card.Payout > 0 ? (double)card.Stake / card.Payout : 0) +
            Field("model_p", TownBettingPolicy.WinProbability(card.OwnScore, card.EnemyScore)) + TuningFields() +
            ",\"opponents\":[";
        for (int i = 0; i < card.Enemies && i < 4; ++i)
        {
            if (i != 0) fields += ',';
            fields += "{\"id\":" + PGLog.Quote(card.Opponents[i]) + ",\"name\":" + PGLog.Quote(card.OpponentNames[i]) +
                ",\"role\":" + PGLog.Quote(card.OpponentRoles[i]) + Field("slot", i) + "}";
        }
        ChallengeEvent(s, host, "challenge_accepted", fields + "]");
    }

    // C++: CombatBalanceLog.cpp:277-285
    public static void ChallengeSettled(IHost host, int outcome, int credit)
    {
        State s = Current;
        // stake / quoted_return are repeated here so the challenge ledger is
        // self-contained: a reader never has to join back to the accept line to
        // work out what the bet was.
        if (s.ChallengeId != 0) ChallengeEvent(s, host, "challenge_settled", Field("challenge_id", s.ChallengeId) + Field("outcome", outcome) +
            Field("credited", credit) + Field("stake", s.ChallengeStake) + Field("quoted_return", s.ChallengePayout) +
            Field("player_net", unchecked(credit - s.ChallengeStake)));
        s.ChallengeId = 0; s.ChallengeStake = s.ChallengePayout = 0;
    }

    // C++: CombatBalanceLog.cpp:286 (fields defaults to "", CombatBalanceLog.h:34)
    public static void DiagnosticEvent(IHost host, string ev, string fields = "") => Event(Current, host, ev, fields);

    /// <summary>The <c>diagnosticEvent(name, fields)</c> sink of <see cref="TownDiagnosticRun"/>'s Start / Pause / Resume / End /
    /// RecoverPendingAfterLoad / CompletePending: the C++ calls <c>CombatBalanceLog::DiagnosticEvent</c> there.</summary>
    // No C++ counterpart: the glue adapter for TownDiagnosticRun's optional sink (the C++ calls :286 directly).
    public static Action<string, string> DiagnosticSink(IHost host) => (ev, fields) => DiagnosticEvent(host, ev, fields);

    // C++: CombatBalanceLog.cpp:287-292
    public static void Abandon(IHost host)
    {
        State s = Current;
        AbortMatch(host, "world_teardown");
        if (s.MarketId != 0) PGLog.Combat("\"event\":\"market_abandoned\"" + Field("record", unchecked(++s.Record)) + Field("market_id", s.MarketId) + DiagnosticFields(host));
        if (s.ChallengeId != 0) PGLog.Challenge("\"event\":\"challenge_abandoned\"" + Field("record", unchecked(++s.Record)) + Field("challenge_id", s.ChallengeId) + Field("stake", s.ChallengeStake));
        s.MarketId = s.ChallengeId = 0; s.ChallengeStake = s.ChallengePayout = 0;
    }
}
