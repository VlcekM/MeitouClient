using System.Text;

namespace ProvingGrounds.Core.Glue;

// Port of upstream/ProvingGrounds/plugin/src/LeaderboardStore.h/.cpp (Proving Grounds by Shutori, GPLv3), task 5.1 (PA §2.13-§2.16): every
// function of the C++ namespace in its order, over the world's ArenaWorldState (the C++ g_ledger and the buy-in account, LeaderboardStore.cpp:29-34)
// and ILeaderboardStoreHost (the character reads). The glue (src/Game/LeaderboardStore.cs) binds the host to a world and to CharacterIds; Core never
// sees an engine type. The persistence functions (BlockPersistence, ActivateSnapshot, CaptureSnapshot, CompleteWorldTransition) are
// ArenaWorldState's (task 2.4), routed here. Took over Core/Glue/MatchAwards.cs (task 3.3's rating half of ApplyFromSnapshot) and the store
// members task 3.3 / 4.6 kept in ProvingGroundsSystem, LeaderboardUILogic (GetStandings, StandingsLess) and CombatLogFighter (GetRating).

/// <summary>What LeaderboardStore.cpp reads of a character (the glue implements it over PgEngine, FighterIdentity, PrisonerUtil and SparSession).
/// "Valid" is the C++ <c>c &amp;&amp; c->isValid()</c>.</summary>
public interface ILeaderboardStoreHost<TId>
{
    /// <summary><c>!c</c>: no character at all (a null <c>Character*</c>).</summary>
    bool IsNull(TId c);
    /// <summary><c>c &amp;&amp; c->isValid()</c>.</summary>
    bool Valid(TId c);
    /// <summary><c>FighterIdentity::GetOrCreate(c, id)</c> when <paramref name="create"/>, else <c>FighterIdentity::Find(c, id)</c>.</summary>
    bool TryIdentity(TId c, bool create, out string token);
    /// <summary><c>c->getName()</c> ("" for none).</summary>
    string Name(TId c);
    /// <summary><c>c->getStats()</c> is non-null, and then <c>getOverallSkillLevel_0_100()</c> (spike S12 stand-in).</summary>
    bool TryOverallSkill(TId c, out float skill);
    /// <summary><c>c->isPlayerCharacter()</c>.</summary>
    bool IsPlayerCharacter(TId c);
    /// <summary><c>c->isChainedMode()</c> (task 6.1: LockSystem.Chained).</summary>
    bool IsChainedMode(TId c);
    /// <summary><c>c->isDead()</c>.</summary>
    bool IsDead(TId c);
    /// <summary><c>PrisonerUtil::IsRosterPrisoner(c)</c>.</summary>
    bool IsRosterPrisoner(TId c);
    /// <summary><c>PrisonerUtil::IsMatchPrisoner(c)</c>.</summary>
    bool IsMatchPrisoner(TId c);
    /// <summary><c>SparSession::GetParticipant(id)</c>: the character of a result row (none = <c>NULL</c>).</summary>
    TId Participant(int participantId);
    /// <summary><c>SquadUtil::CollectPlayerSquad</c>, in its order.</summary>
    IEnumerable<TId> PlayerSquad();
    /// <summary><c>PrisonerUtil::GetRosterPrisoners()</c>.</summary>
    IEnumerable<TId> RosterPrisoners();
    /// <summary><c>PrisonerUtil::GetMatchPrisoners()</c>.</summary>
    IEnumerable<TId> MatchPrisoners();
    /// <summary><c>PGLog::Debug</c>.</summary>
    void Debug(string line);
    /// <summary><c>PGLog::Error</c>.</summary>
    void Error(string line);
}

/// <summary>The rules of LeaderboardStore.cpp that need no world: StandingsLess and the body of GetStandings (one copy, read by the store and by
/// LeaderboardUILogic's window host).</summary>
public static class LeaderboardStoreRules
{
    /// <summary>C++ <c>StandingsLess</c> (LeaderboardStore.cpp:83-86) as a comparison: rating descending, then <c>std::string operator&lt;</c> (the
    /// UTF-8 bytes, unsigned).</summary>
    public static int CompareStandings(LeaderboardData.Standing a, LeaderboardData.Standing b)
    {
        if (a.Mmr != b.Mmr) return a.Mmr > b.Mmr ? -1 : 1;
        byte[] x = Encoding.UTF8.GetBytes(a.Name), y = Encoding.UTF8.GetBytes(b.Name);
        return ((ReadOnlySpan<byte>)x).SequenceCompareTo(y);
    }

    /// <summary>The body of GetStandings over a table and a live-name read ("" = none found, or an empty name): shared with LeaderboardUILogic's
    /// host-based form, one copy of the rule.</summary>
    public static List<LeaderboardData.Standing> SortedStandings(IReadOnlyList<LeaderboardData.Standing> standings, Func<string, string> liveName)
    {
        var output = new List<LeaderboardData.Standing>();
        for (int i = 0; i < standings.Count; ++i)
        {
            if (standings[i].Matches < 1)
                continue;
            LeaderboardData.Standing copy = standings[i].Clone();
            string name = liveName(copy.Id);
            if (name.Length != 0)
                copy.Name = name;
            output.Add(copy);
        }
        return [.. output.OrderBy(r => r, Comparer<LeaderboardData.Standing>.Create(CompareStandings))];
    }
}

/// <summary>The C++ namespace <c>LeaderboardStore</c> for one world. Sim thread only. <typeparamref name="TId"/> is the character handle (the glue's
/// CharacterId; an int in the tests); the C++ <c>!character</c> checks are <see cref="ILeaderboardStoreHost{TId}.IsNull"/>, <c>isValid()</c> is
/// <see cref="ILeaderboardStoreHost{TId}.Valid"/>.</summary>
public sealed class LeaderboardStoreLogic<TId> where TId : notnull
{
    /// <summary>C++ LeaderboardStore.cpp:221.</summary>
    public const string NotReadyText = "Proving Grounds: rating skip - arena ledger unavailable";
    /// <summary>C++ LeaderboardStore.cpp:366.</summary>
    public const string RatingsUpdatedText = "Proving Grounds: ratings updated in active world snapshot";
    /// <summary>C++ LeaderboardStore.cpp:653-655: the town board's search around its building: 2500 units, at most 512 characters.</summary>
    public const float TownSearchRadius = 2500.0f;
    public const int TownSearchMax = 512;

    readonly ArenaWorldState state;
    readonly ILeaderboardStoreHost<TId> h;
    // C++ g_missingPersistentIdentityLogged (LeaderboardStore.cpp:36): a vector of Character*, cleared by BlockPersistence / ActivateSnapshot
    readonly HashSet<TId> missingLogged = [];
    int missingLoggedSwitch;

    public LeaderboardStoreLogic(ArenaWorldState state, ILeaderboardStoreHost<TId> host)
    {
        this.state = state;
        h = host;
        missingLoggedSwitch = state.LedgerSwitches;
    }

    ArenaLedger.Ledger Ledger => state.Ledger;

    /// <summary>C++ <c>g_data</c> (LeaderboardStore.cpp:30): the standings and progression tables of the world.</summary>
    public LeaderboardData.Collections Data => state.Data;

    /// <summary>The world's state (the ledger with its save decisions, task 2.4).</summary>
    public ArenaWorldState State => state;

    // ---------- the anonymous namespace (LeaderboardStore.cpp:27-87) ----------

    /// <summary>C++: LeaderboardStore.cpp:38-45 <c>CharacterKey(character, create)</c>: "" when the ledger is not ready, the character is not valid
    /// or it has no identity.</summary>
    public string CharacterKey(TId c, bool create = false)
    {
        if (!Ledger.Ready() || !h.Valid(c)) return "";
        return h.TryIdentity(c, create, out string id) ? id : "";
    }

    /// <summary>C++: LeaderboardStore.cpp:47-53 <c>LogMissingPersistentIdentity</c>: once per character (until the ledger is next activated or
    /// blocked), PGLog::Error; nothing for no character.</summary>
    public void LogMissingPersistentIdentity(TId c, string operation)
    {
        if (h.IsNull(c)) return;
        if (missingLoggedSwitch != state.LedgerSwitches)
        {
            missingLogged.Clear();
            missingLoggedSwitch = state.LedgerSwitches;
        }
        if (!missingLogged.Add(c)) return;
        h.Error("Proving Grounds: unavailable or conflicting fighter identity during " + operation);
    }

    // C++: LeaderboardStore.cpp:55-61
    LeaderboardData.Standing? FindStanding(LeaderboardData.Kind kind, string id)
    {
        if (!Ledger.Ready() || id.Length == 0) return null;
        List<LeaderboardData.Standing> rows = LeaderboardData.Standings(Data, kind);
        for (int i = 0; i < rows.Count; ++i) if (rows[i].Id == id) return rows[i];
        return null;
    }

    // C++: LeaderboardStore.cpp:63-66
    LeaderboardData.Standing? FindStandingForCharacter(TId c, LeaderboardData.Kind kind) => FindStanding(kind, CharacterKey(c));

    // C++: LeaderboardStore.cpp:68-71
    LeaderboardData.Standing EnsureStandingForCharacter(TId c, LeaderboardData.Kind kind) =>
        LeaderboardData.EnsureStanding(Data, kind, CharacterKey(c, true), h.Name(c));

    // C++: LeaderboardStore.cpp:73-76
    LeaderboardData.Progression? FindProgressionForCharacter(TId c) => Ledger.Find(CharacterKey(c));

    // C++: LeaderboardStore.cpp:78-81
    LeaderboardData.Progression EnsureProgressionForCharacter(TId c) =>
        LeaderboardData.EnsureProgression(Data, CharacterKey(c, true), h.Name(c));

    // ---------- town-only accessors (LeaderboardStore.cpp:91-156): what the snapshot holds; the town behaviour is M7 / M8 ----------

    /// <summary>C++: LeaderboardStore.cpp:91-97 <c>GetTownDiagnostic</c>: the held state while Ready, else a scratch default (writes are lost).</summary>
    public ref TownDiagnosticData.State GetTownDiagnostic()
    {
        if (Ledger.Ready()) return ref Ledger.State().Diagnostic;
        unavailableDiagnostic = new TownDiagnosticData.State();
        return ref unavailableDiagnostic;
    }
    TownDiagnosticData.State unavailableDiagnostic;

    // C++: LeaderboardStore.cpp:99-102
    public string GetDiagnosticError() => Ledger.Ready() ? Ledger.State().DiagnosticError : "";

    // C++: LeaderboardStore.cpp:104-107
    public void ClearDiagnosticError()
    {
        if (Ledger.Ready()) Ledger.State().DiagnosticError = "";
    }

    // C++: LeaderboardStore.cpp:110-115
    public bool ResetTownStandings()
    {
        if (!Ledger.Ready()) return false;
        Data.TownStandings.Clear();
        return true;
    }

    /// <summary>C++: LeaderboardStore.cpp:117-134 <c>RemoveTownNpcProgression</c>: false (nothing removed) for a character that is not valid, a
    /// player character, a chained one or a prisoner of the roster or the match; else every progression row with its key goes and it answers true
    /// (also when it has no key).</summary>
    public bool RemoveTownNpcProgression(TId c)
    {
        if (!h.Valid(c) || h.IsPlayerCharacter(c) || h.IsChainedMode(c) || h.IsRosterPrisoner(c) || h.IsMatchPrisoner(c)) return false;

        string id = CharacterKey(c);
        if (id.Length != 0)
        {
            for (int i = 0; i < Data.Progression.Count;)
            {
                if (Data.Progression[i].Id == id)
                    Data.Progression.RemoveAt(i);
                else ++i;
            }
        }
        return true;
    }

    /// <summary>C++: LeaderboardStore.cpp:136-142 <c>GetTownChallenges</c>: the held card while Ready, else a new default card (writes are lost).</summary>
    public TownChallengePolicy.Card GetTownChallenges() => Ledger.Ready() ? Ledger.State().Challenges : new TownChallengePolicy.Card();

    /// <summary>C++: LeaderboardStore.cpp:143-149 <c>GetBookieCredit</c> (an <c>int&amp;</c>): the held credit while Ready, else 0; a write while not
    /// Ready is lost, as a write to the C++ static.</summary>
    public int BookieCredit
    {
        get => Ledger.Ready() ? Ledger.State().BookieCredit : 0;
        set { if (Ledger.Ready()) Ledger.State().BookieCredit = value; }
    }

    /// <summary>C++: LeaderboardStore.cpp:150-156 <c>GetChallengeAccount</c> (held by ArenaWorldState).</summary>
    public ref TownChallengeBuyInPolicy.Account GetChallengeAccount() => ref state.ChallengeAccount();

    // ---------- the world lifecycle (LeaderboardStore.cpp:158-208), routed to ArenaWorldState ----------

    /// <summary>C++: LeaderboardStore.cpp:158-162: a no-op ("activation is an explicit native lifecycle event").</summary>
    public void EnsureLoaded() { }

    // C++: LeaderboardStore.cpp:164-170
    public void BlockPersistence(string reason) => state.BlockPersistence(reason);

    // C++: LeaderboardStore.cpp:172
    public string GetPersistenceError() => state.PersistenceError;

    // C++: LeaderboardStore.cpp:173
    public void Unload() => BlockPersistence("Arena world state is unloaded.");

    // C++: LeaderboardStore.cpp:175-182
    public void ActivateSnapshot(ArenaPersistence.Snapshot snapshot) => state.ActivateSnapshot(snapshot);

    // C++: LeaderboardStore.cpp:184-192
    public bool CaptureSnapshot(ArenaPersistence.Snapshot output) => state.CaptureSnapshot(output);

    /// <summary>C++: LeaderboardStore.cpp:194-200 <c>CompleteWorldTransition</c>. Meitou's form is ArenaWorldState.Resolve (the first serial step
    /// of a world: a new game gets the fresh snapshot with the <c>GetTickCount() | 1u</c> seed); this is the C++ call for a caller that has the
    /// fact itself.</summary>
    public void CompleteWorldTransition(bool newGame, uint tickCount)
    {
        if (!newGame) { BlockPersistence("Waiting for native arena snapshot ownership metadata."); return; }
        ActivateSnapshot(ArenaSaveData.FreshSnapshot(tickCount));
    }

    /// <summary>C++: LeaderboardStore.cpp:202-208: the legacy sidecar publication, disabled (logs the C++ error, false).</summary>
    public bool CommitSaveSnapshot(string sourceSaveName, string targetSaveName)
    {
        h.Error("Proving Grounds: legacy arena sidecar publication is disabled.");
        return false;
    }

    // ---------- ApplyFromSnapshot (LeaderboardStore.cpp:210-367) ----------

    /// <summary>
    /// C++: LeaderboardStore.cpp:210-367. A rated outcome only (no-op for Stop / Draw), the ledger Ready, 1..16 rows: each row's character (its
    /// participant) gets its identity created; one without a valid character or an identity is skipped (logged once); the bindings are checked
    /// again (a later row may have revealed a duplicate token); ratings by MmrRating, Marks by ArenaMarks; the rated rows' rating and Marks fields
    /// (and the podium copies) are written; the standings of <paramref name="kind"/> get the result and one match; players and prisoners (every
    /// row on the player board, a town row only when <see cref="RemoveTownNpcProgression"/> refused) get their Marks in the shared progression
    /// table; a town NPC gets none and loses its progression row. Returns whether any row was rated (the C++ returns nothing).
    /// </summary>
    public bool ApplyFromSnapshot(SparPodium.Snapshot snap, LeaderboardData.Kind kind, float marksMultiplier = 1.0f, int marksTeamBonus = 0)
    {
        if (!MmrRating.ShouldRate(snap.Outcome))
            return false;

        EnsureLoaded();
        if (!Ledger.Ready())
        {
            h.Debug(NotReadyText);
            return false;
        }

        int count = snap.FighterCount;
        if (count <= 0 || count > 16)
            return false;

        var mmrBefore = new float[16];
        var matchesBefore = new int[16];
        var rateMask = new bool[16];
        var marksMask = new bool[16];
        var combatSkill = new float[16];
        var ids = new string[16];
        var names = new string[16];
        var characters = new TId[16];

        for (int i = 0; i < count; ++i)
        {
            TId c = h.Participant(snap.Fighters[i].Id);
            characters[i] = c;
            ids[i] = "";
            names[i] = snap.Fighters[i].Name ?? "";
            rateMask[i] = false;
            marksMask[i] = false;
            mmrBefore[i] = MmrRating.DefaultMmr;
            matchesBefore[i] = 0;
            combatSkill[i] = 0.0f;

            if (!h.Valid(c))
                continue;

            ids[i] = CharacterKey(c, true);
            if (ids[i].Length == 0)
            {
                LogMissingPersistentIdentity(c, "match award");
                continue;
            }

            if (h.TryOverallSkill(c, out float skill))
                combatSkill[i] = skill;

            LeaderboardData.Standing? rec = FindStanding(kind, ids[i]);
            if (rec is not null) { mmrBefore[i] = rec.Mmr; matchesBefore[i] = rec.Matches; }
            rateMask[i] = true;
            marksMask[i] = kind != LeaderboardData.Kind.Town || !RemoveTownNpcProgression(c);
        }

        // A later participant may reveal a duplicate of an earlier token.
        // Revalidate all bindings before any career mutation.
        for (int i = 0; i < count; ++i)
            if (rateMask[i] && CharacterKey(characters[i]) != ids[i]) rateMask[i] = false;
        bool any = false;
        for (int i = 0; i < count; ++i)
        {
            if (rateMask[i])
            {
                any = true;
                break;
            }
        }
        if (!any)
            return false;

        var mmrAfter = new float[16];
        var winsDelta = new int[16];
        var lossesDelta = new int[16];
        var marksEarned = new int[16];
        MmrRating.ApplyMatch(
            snap,
            mmrBefore,
            matchesBefore,
            rateMask,
            mmrAfter,
            winsDelta,
            lossesDelta);
        ArenaMarks.ComputeAwards(snap, combatSkill, marksEarned, marksMultiplier);
        ArenaMarks.ApplyWinningTeamBonus(snap, marksMask, marksEarned, marksTeamBonus);
        for (int i = 0; i < count; ++i)
        {
            if (!rateMask[i])
                continue;

            ref SparPodium.FighterRow fighter = ref snap.Fighters[i];
            fighter.RatingBefore = mmrBefore[i];
            fighter.RatingAfter = mmrAfter[i];
            fighter.RatingDelta = mmrAfter[i] - mmrBefore[i];
            fighter.RatingUpdated = true;
            fighter.MarksBefore = fighter.MarksAfter = fighter.MarksEarned = 0;
            fighter.MarksUpdated = marksMask[i];
            if (marksMask[i])
            {
                LeaderboardData.Progression progress =
                    EnsureProgressionForCharacter(characters[i]);
                fighter.MarksBefore = progress.Marks;
                fighter.MarksAfter = LeaderboardData.AwardedTotal(progress.Marks, marksEarned[i]);
                fighter.MarksEarned = fighter.MarksAfter - fighter.MarksBefore;
            }

            // The podium stores FighterRow copies built before ratings apply.
            for (int p = 0; p < snap.PodiumCount; ++p)
            {
                if (snap.Podium[p].Fighter.Id != fighter.Id)
                    continue;
                ref SparPodium.FighterRow podium = ref snap.Podium[p].Fighter;
                podium.RatingBefore = fighter.RatingBefore;
                podium.RatingAfter = fighter.RatingAfter;
                podium.RatingDelta = fighter.RatingDelta;
                podium.RatingUpdated = true;
                podium.MarksBefore = fighter.MarksBefore;
                podium.MarksAfter = fighter.MarksAfter;
                podium.MarksEarned = fighter.MarksEarned;
                podium.MarksUpdated = true;
                break;
            }
        }

        for (int i = 0; i < count; ++i)
        {
            if (!rateMask[i])
                continue;

            LeaderboardData.ApplyCompetitiveResult(
                Data, kind, ids[i], names[i], mmrAfter[i],
                winsDelta[i], lossesDelta[i]);
            if (!marksMask[i]) continue;
            LeaderboardData.AddMarks(
                Data, ids[i], names[i], marksEarned[i]);
            LeaderboardData.Progression progress =
                LeaderboardData.EnsureProgression(
                    Data, ids[i], names[i]);
            TId character = h.Participant(snap.Fighters[i].Id);
            if (h.IsRosterPrisoner(character))
            {
                // C++ sprintf_s(line, "... name=%s key=%s earned=%d total=%d") into char[512]
                h.Debug("Proving Grounds: prisoner Marks awarded name=" + h.Name(character) + " key=" + ids[i]
                    + " earned=" + marksEarned[i].ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " total=" + progress.Marks.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        h.Debug(RatingsUpdatedText);
        return true;
    }

    // ---------- the debug setters, the reads (LeaderboardStore.cpp:369-491) ----------

    /// <summary>C++: LeaderboardStore.cpp:369-395 <c>SetRating</c>: false for a character that is not valid, then ArenaWorldState.SetRating (refused
    /// while not Ready, before any identity is made; an identity created, else the miss logged once; clamped to 0..9999; the player board).</summary>
    public bool SetRating(TId c, float rating)
    {
        if (!h.Valid(c))
            return false;
        EnsureLoaded();
        return state.SetRating(KeyForSetter(c, "rating update"), h.Name(c), rating) == ArenaWorldState.SetResult.Updated;
    }

    /// <summary>The C++ <c>CharacterKey(character, true)</c> of a setter: null (and the once-per-character log) when there is none.</summary>
    Func<string?> KeyForSetter(TId c, string operation) => () =>
    {
        string id = CharacterKey(c, true);
        if (id.Length != 0) return id;
        LogMissingPersistentIdentity(c, operation);
        return null;
    };

    /// <summary>C++: LeaderboardStore.cpp:397-423 <c>GetStandings</c>: copies of the rows with at least one match, each renamed to its live
    /// character's name (FindRatedCharacter with no building: the squad and the prisoners) when that is not empty, sorted by StandingsLess. The
    /// C++ std::sort is not stable; this sort is (two rows with the same rating and name keep their table order: docs/divergences.md).</summary>
    public List<LeaderboardData.Standing> GetStandings(LeaderboardData.Kind kind)
    {
        EnsureLoaded();
        var index = NewRatedCharacterIndex(null);
        return LeaderboardStoreRules.SortedStandings(LeaderboardData.Standings(Data, kind), id => index.TryFind(id, false, out TId c) ? h.Name(c) : "");
    }

    /// <summary>C++: LeaderboardStore.cpp:425-439 <c>GetRating</c>: the default for no character; a character
    /// without a key (not Ready, not valid, no identity) logs once ("rating lookup") and gets the default; else its standing's rating or the
    /// default.</summary>
    public float GetRating(TId c, LeaderboardData.Kind kind = LeaderboardData.Kind.Player)
    {
        if (h.IsNull(c))
            return MmrRating.DefaultMmr;

        EnsureLoaded();
        string id = CharacterKey(c);
        if (id.Length == 0)
        {
            LogMissingPersistentIdentity(c, "rating lookup");
            return MmrRating.DefaultMmr;
        }
        LeaderboardData.Standing? record = FindStandingForCharacter(c, kind);
        return record is not null ? record.Mmr : MmrRating.DefaultMmr;
    }

    /// <summary>C++: LeaderboardStore.cpp:441-455 <c>GetMatchCount</c>: as GetRating, 0 by default ("match count lookup").</summary>
    public int GetMatchCount(TId c, LeaderboardData.Kind kind = LeaderboardData.Kind.Player)
    {
        if (h.IsNull(c))
            return 0;

        EnsureLoaded();
        string id = CharacterKey(c);
        if (id.Length == 0)
        {
            LogMissingPersistentIdentity(c, "match count lookup");
            return 0;
        }
        LeaderboardData.Standing? record = FindStandingForCharacter(c, kind);
        return record is not null ? record.Matches : 0;
    }

    /// <summary>C++: LeaderboardStore.cpp:457-473 <c>GetMarks</c>: as GetRating over the progression row, 0 by default ("Marks lookup").</summary>
    public int GetMarks(TId c)
    {
        if (h.IsNull(c))
            return 0;

        EnsureLoaded();
        string id = CharacterKey(c);
        if (id.Length == 0)
        {
            LogMissingPersistentIdentity(c, "Marks lookup");
            return 0;
        }
        LeaderboardData.Progression? record = FindProgressionForCharacter(c);
        int marks = record is not null ? record.Marks : 0;
        return marks;
    }

    /// <summary>C++: LeaderboardStore.cpp:475-491 <c>GetRewardTier</c>: -1 (unclaimed) for no character or an empty reward id; no key logs once
    /// ("reward lookup"); else the highest purchased tier of the reward.</summary>
    public int GetRewardTier(TId c, string? rewardId)
    {
        if (h.IsNull(c) || string.IsNullOrEmpty(rewardId))
            return -1;

        EnsureLoaded();
        string id = CharacterKey(c);
        if (id.Length == 0)
        {
            LogMissingPersistentIdentity(c, "reward lookup");
            return -1;
        }
        LeaderboardData.Progression? record = FindProgressionForCharacter(c);
        return record is not null ? RewardProgression.HighestTier(record.RewardTiers, rewardId) : -1;
    }

    // ---------- purchases (LeaderboardStore.cpp:493-613) ----------

    /// <summary>C++: LeaderboardStore.cpp:493-504 <c>BeginRewardTierPurchase</c>: the Marks paid and the tier set together, or nothing.</summary>
    public bool BeginRewardTierPurchase(TId c, int cost, string? rewardId, int expectedPreviousTier, int nextTier)
    {
        string id = CharacterKey(c, true);
        if (id.Length == 0) return false;
        return rewardId is not null && Ledger.BeginTier(id, h.Name(c), cost, rewardId, expectedPreviousTier, nextTier);
    }

    /// <summary>C++: LeaderboardStore.cpp:506-532 <c>SetMarks</c>: as SetRating ("Marks update"), ArenaWorldState.SetMarks (0..9999).</summary>
    public bool SetMarks(TId c, int marks)
    {
        if (!h.Valid(c))
            return false;
        EnsureLoaded();
        return state.SetMarks(KeyForSetter(c, "Marks update"), h.Name(c), marks) == ArenaWorldState.SetResult.Updated;
    }

    /// <summary>C++: LeaderboardStore.cpp:534-541 <c>RollbackRewardTierPurchase</c>: the tier back to <paramref name="previousTier"/> and the Marks
    /// refunded, only when the tier is still above it.</summary>
    public void RollbackRewardTierPurchase(TId c, int cost, string? rewardId, int previousTier)
    {
        if (rewardId is not null) Ledger.RollbackTier(CharacterKey(c, true), cost, rewardId, previousTier);
    }

    // C++: LeaderboardStore.cpp:543-550
    public bool HasFactionUnlock(string? unlockId)
    {
        if (string.IsNullOrEmpty(unlockId))
            return false;
        EnsureLoaded();
        return Ledger.Ready() && RewardProgression.HasUnlock(Data.FactionUnlocks, unlockId);
    }

    // C++: LeaderboardStore.cpp:552-559
    public bool AddFactionUnlock(string? unlockId)
    {
        if (string.IsNullOrEmpty(unlockId))
            return false;
        EnsureLoaded();
        return Ledger.Ready() && RewardProgression.AddUnlock(Data.FactionUnlocks, unlockId);
    }

    // C++: LeaderboardStore.cpp:561-568
    public void RemoveFactionUnlock(string? unlockId)
    {
        if (string.IsNullOrEmpty(unlockId))
            return;
        EnsureLoaded();
        if (Ledger.Ready())
            RewardProgression.RemoveUnlock(Data.FactionUnlocks, unlockId);
    }

    /// <summary>C++: LeaderboardStore.cpp:570-578 <c>BeginFactionUnlockPurchase</c>: the Marks paid and the faction-wide unlock added, or nothing
    /// (refused when the unlock is already owned).</summary>
    public bool BeginFactionUnlockPurchase(TId c, int cost, string? unlockId)
    {
        string id = CharacterKey(c, true);
        if (id.Length == 0) return false;
        return unlockId is not null && Ledger.BeginUnlock(id, h.Name(c), cost, unlockId);
    }

    // C++: LeaderboardStore.cpp:580-587
    public void RollbackFactionUnlockPurchase(TId c, int cost, string? unlockId)
    {
        string id = CharacterKey(c, true);
        if (id.Length != 0 && unlockId is not null) Ledger.RollbackUnlock(id, h.Name(c), cost, unlockId);
    }

    /// <summary>C++: LeaderboardStore.cpp:589-594 <c>ReserveProgression</c>: the identity resolved once, the reservation then owned by the token.</summary>
    public bool ReserveProgression(TId c, ArenaLedger.Purchase purchase, RewardTransaction.ReservedAccounting accounting)
    {
        string id = CharacterKey(c, true);
        return id.Length != 0 && accounting.Reserve(Ledger, id, h.Name(c), purchase);
    }

    // C++: LeaderboardStore.cpp:596-599
    public bool CompleteProgressionTransaction() => Ledger.Complete();

    // C++: LeaderboardStore.cpp:601-606
    public bool SpendMarks(TId c, int cost)
    {
        string id = CharacterKey(c, true);
        if (id.Length == 0) return false;
        return Ledger.Spend(id, h.Name(c), cost);
    }

    // C++: LeaderboardStore.cpp:608-613
    public void RefundMarks(TId c, int amount)
    {
        string id = CharacterKey(c, true);
        if (id.Length == 0) return;
        Ledger.Refund(id, h.Name(c), amount);
    }

    // ---------- FindRatedCharacter (LeaderboardStore.cpp:615-667) ----------

    /// <summary>C++: LeaderboardStore.cpp:615-667 <c>FindRatedCharacter</c>: the first character with key <paramref name="id"/> among the player's
    /// squad, the roster prisoners, the match prisoners (each valid), then, for the town board with a building, the living characters within 2500 of
    /// it (<paramref name="townSearch"/>: the glue's sphere read, null = no building). False for "".</summary>
    public bool FindRatedCharacter(LeaderboardData.Kind kind, string id, Func<IEnumerable<TId>>? townSearch, out TId character) =>
        NewRatedCharacterIndex(kind == LeaderboardData.Kind.Town ? townSearch : null).TryFind(id, kind == LeaderboardData.Kind.Town, out character);

    /// <summary>FindRatedCharacter for many ids at once (task 4.6a review): each candidate's key read once, in the C++ order, the first match kept;
    /// the town search only when a lookup needs it. One index per window refresh (the world may change between). <paramref name="beforeWalk"/>
    /// runs before each lazy walk (the glue binds its host to the index's own world there, so a later lookup never reads another call's world).</summary>
    public RatedCharacterIndex<TId> NewRatedCharacterIndex(Func<IEnumerable<TId>>? townSearch, Action? beforeWalk = null) =>
        new(() => { beforeWalk?.Invoke(); return PrimaryCandidates(); },
            townSearch is null ? null : () => { beforeWalk?.Invoke(); return TownCandidates(townSearch()); });

    // C++: LeaderboardStore.cpp:623-648 (each valid, with its key)
    IEnumerable<(TId Id, string Key)> PrimaryCandidates()
    {
        foreach (TId c in h.PlayerSquad())
            if (h.Valid(c)) yield return (c, CharacterKey(c));
        foreach (TId c in h.RosterPrisoners())
            if (h.Valid(c)) yield return (c, CharacterKey(c));
        foreach (TId c in h.MatchPrisoners())
            if (h.Valid(c)) yield return (c, CharacterKey(c));
    }

    // C++: LeaderboardStore.cpp:650-664 (valid, not dead)
    IEnumerable<(TId Id, string Key)> TownCandidates(IEnumerable<TId> nearby)
    {
        foreach (TId c in nearby)
            if (h.Valid(c) && !h.IsDead(c)) yield return (c, CharacterKey(c));
    }

    // C++: LeaderboardStore.cpp:669-673
    public string GetActiveSaveKey()
    {
        EnsureLoaded();
        return Ledger.State().SaveKey;
    }

    /// <summary>Port only (task 7.5): the active snapshot's <c>generation</c>, the key the town's first challenge card is seeded from (Meitou's save
    /// key is the fixed "meitou", docs/divergences.md "TownArena challenge cards"); "" in a game never saved, as the C++'s empty save key.</summary>
    public string GetActiveSaveGeneration()
    {
        EnsureLoaded();
        return Ledger.State().Generation;
    }

    // C++: LeaderboardStore.cpp:675-679
    public bool HasActiveSave()
    {
        EnsureLoaded();
        return Ledger.Ready();
    }
}
