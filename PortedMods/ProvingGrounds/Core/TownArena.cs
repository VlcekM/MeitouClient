// Pure parts of upstream/ProvingGrounds/plugin/src/TownArena.cpp (Proving Grounds by Shutori, GPLv3).
using System.Globalization;

namespace ProvingGrounds.Core;

/// <summary>The pure parts of <c>TownArena.cpp</c> (TW §6.2): readiness reasons, the preview and named-opponent reasons, the buy-in quote,
/// the card captions (TownArena.Captions.cs), the small rules over plain values (TownArena.Rules.cs) and every status text
/// (TownArena.Messages.cs). The C++ keeps the booking, the phase, the previews and the fighters in module globals and reads characters,
/// buildings, the save and the clock itself; here the glue (<c>ProvingGrounds.Town.TownArena</c>, which refers to this class as
/// <c>Core.TownArena</c>) keeps those and passes plain values: a character is an <see cref="Actor"/>, the matchmaking snapshot a
/// <see cref="MatchmakingSnapshot"/>, the preview globals a <see cref="DisplayState"/>. Rules already in Core are called, not re-ported:
/// <see cref="TownChallengePolicy"/>, <see cref="TownArenaPolicy"/>, <see cref="TownArenaRuntimePolicy"/>,
/// <see cref="TownChallengeBuyInPolicy"/>, <see cref="TownMatchmakingPolicy"/>, <see cref="TownAftercarePolicy"/>, <see cref="ArenaMarks"/>,
/// <see cref="TownFighterCatalog"/>; numbers are formatted with <see cref="CMath.FormatF"/> (the C++'s <c>%.Nf</c>).</summary>
public static partial class TownArena
{
    /// <summary>What the copied code reads off one <c>Character*</c>. The glue builds ONE Actor per live character and reuses it, because
    /// the C++ compares characters by pointer (a map may hold the same character under two keys; <see cref="NamedOpponentUnavailableReason"/>
    /// and <see cref="QuoteFor"/> compare by reference). A null entry in a list stands for a NULL pointer.</summary>
    public sealed class Actor
    {
        /// <summary><c>c-&gt;isValid()</c>.</summary>
        public bool Valid = true;
        /// <summary><c>c-&gt;getInstanceID()-&gt;uid</c>, null when <c>getInstanceID()</c> is NULL.</summary>
        public string? InstanceUid;
        /// <summary><c>c-&gt;getGameData()-&gt;stringID</c>, null when <c>getGameData()</c> is NULL (the C++ <c>Id(c)</c>, TownArena.cpp:136-140).</summary>
        public string? StringId;
        /// <summary><c>c-&gt;getName()</c>.</summary>
        public string Name = "";
        /// <summary><c>TownMatchmakingRuntime::Identity(c)</c>.</summary>
        public string Identity = "";
        /// <summary><c>TownMatchmakingRuntime::SelectionIdentity(c)</c>.</summary>
        public string SelectionIdentity = "";
        /// <summary><c>IngressQuarantined(c)</c> (TownArena.cpp:120-125).</summary>
        public bool Quarantined;
        /// <summary><c>c</c> is in <c>ou-&gt;player-&gt;getAllPlayerCharacters()</c>.</summary>
        public bool PlayerCharacter;
        /// <summary><c>isDead()</c>, <c>isUnconcious()</c>, <c>isInCombatMode(true, true)</c>, <c>ArenaMedical::NeedsStabilization(c)</c>.</summary>
        public bool Dead, Unconscious, InCombat, NeedsAid;
        /// <summary><c>RosterStatus::Read(c, health, reason)</c>: its result, the snapshot and the text it left in <c>reason</c>
        /// (<see cref="RosterStatus.Read"/> gives all three).</summary>
        public bool RosterOk;
        public RosterStatus.Snapshot Health;
        public string RosterError = "health data unavailable";
        /// <summary>TownArena.cpp:325-326: <c>!isInBloodlossTrauma() &amp;&amp; !isProbablyDying() &amp;&amp; !isCrippled() &amp;&amp; hasAnArmToFightWith()</c>.</summary>
        public bool MedicallyAble;
        /// <summary><c>TownAftercare::IsManaged(c)</c>, <c>TownLimbShop::IsManaged(c)</c>, <c>TownArena::IsFighter(c)</c>.</summary>
        public bool InAftercare, InLimbShop, Fighter;
        /// <summary><c>TownMatchmakingRuntime::Ability(c)</c>.</summary>
        public double Ability;
        /// <summary><c>LeaderboardStore::GetRating(c, LeaderboardData::Town)</c>.</summary>
        public float TownRating = 100.0f;
        /// <summary>TownArena.cpp:939-940: the registry is valid and <c>c-&gt;getCurrentTownLocation() == registry-&gt;getTown()</c>.</summary>
        public bool InRegistryTown;
    }

    /// <summary>C++: TownMatchmakingRuntime.h:8-16 (<c>Snapshot</c>, copied by value) as these functions read it. <see cref="Characters"/> are
    /// the <c>std::map&lt;std::string, Character*&gt;</c>'s values in key order (std::string keys: ordinal byte order); only the medics'
    /// count is read. <see cref="Context"/> and <see cref="Availability"/> are not read here: they are for the glue (the offer context, :704, the preview search key,
    /// :780, and the bout-waiting status, :1615; all TownArena.cpp).</summary>
    public sealed class MatchmakingSnapshot
    {
        public List<TownMatchmakingPolicy.Fighter> Players = new(), Npcs = new();
        public List<Actor?> Characters = new();
        public int MedicCount;
        public string Context = "", Availability = "", PlayerBlocker = "";
        public bool PlayersReady;
    }

    static string I(int value) => value.ToString(CultureInfo.InvariantCulture);
    static string U(uint value) => value.ToString(CultureInfo.InvariantCulture);

    // C++: TownArena.cpp:136-140
    static string? Id(Actor? c) => c?.StringId;

    // C++: TownArena.cpp:141-145
    public static string LegacyOpponentId(Actor? c)
    {
        return c != null ? TownChallengePolicy.OpponentKey(c.InstanceUid ?? "", Id(c) ?? "", c.Name) : "";
    }

    // C++: TownArena.cpp:146-150
    public static string PersistentId(Actor c)
    {
        string id = c.Identity;
        return id.Length == 0 ? LegacyOpponentId(c) : id;
    }

    // C++: TownArena.cpp:151-155
    public static bool MatchesOpponent(Actor? c, string key)
    {
        return c != null && key.Length != 0 && (PersistentId(c) == key || LegacyOpponentId(c) == key ||
            TownChallengePolicy.OpponentKey("", Id(c) ?? "", c.Name) == key);
    }

    // C++: TownArena.cpp:168-173
    public static bool EligibleOpponent(MatchmakingSnapshot snapshot, string id)
    {
        for (int i = 0; i < snapshot.Npcs.Count; ++i)
            if (snapshot.Npcs[i].Id == id) return true;
        return false;
    }

    /// <summary>Whether <paramref name="c"/> may fight (or, as <paramref name="doctor"/>, give aid). <paramref name="playerInterface"/> =
    /// <c>ou &amp;&amp; ou-&gt;player</c>. <paramref name="reason"/> is what the C++ writes through its optional <c>reason</c> pointer (the glue
    /// drops it where the C++ passes NULL). <paramref name="detail"/> is the debug line the C++ logs when <c>g_quietDiscovery</c> is false,
    /// null when it returns before that line.</summary>
    // C++: TownArena.cpp:297-346
    public static bool Ready(Actor? c, bool doctor, bool bookedPlayer, bool playerInterface, out string reason, out string? detail)
    {
        detail = null;
        reason = "health data unavailable";
        if (c == null || !c.Valid) return false;
        if (!doctor && c.Quarantined)
        {
            reason = "could not reach arena formation";
            return false;
        }
        var r = new TownArenaRuntimePolicy.Readiness();
        r.Valid = true;
        if (!playerInterface) return false;
        if (c.PlayerCharacter) r.Player = true;
        if (bookedPlayer)
        {
            if (!r.Player) return false;
            r.Player = false; // Same recovery thresholds, explicitly player-owned.
        }
        r.Dead = c.Dead;
        r.Unconscious = c.Unconscious;
        r.InCombat = c.InCombat;
        r.NeedsAid = c.NeedsAid;
        // RosterStatus::Read writes its error through reason, or clears it on success
        reason = c.RosterError;
        if (!c.RosterOk) return false;
        RosterStatus.Snapshot health = c.Health;
        reason = "";
        r.Blood = health.Blood;
        r.MinimumHealth = health.LowestLimb;
        bool medicallyAble = c.MedicallyAble;
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
        // "Town candidate %s: ready=%d health=%.3f blood=%.3f player=%d dead=%d ko=%d combat=%d aid=%d" (the floats promoted to double)
        detail = "Town candidate " + (Id(c) ?? "unknown") + ": ready=" + (ready ? "1" : "0") + " health=" +
            CMath.FormatF(r.MinimumHealth, 3) + " blood=" + CMath.FormatF(r.Blood, 3) + " player=" + B(r.Player) + " dead=" + B(r.Dead) +
            " ko=" + B(r.Unconscious) + " combat=" + B(r.InCombat) + " aid=" + B(r.NeedsAid);
        return ready;
    }

    static string B(bool value) => value ? "1" : "0";

    /// <summary>Why the named opponent of <paramref name="offer"/> cannot be booked. The C++ calls <c>Ready</c> with
    /// <c>g_quietDiscovery</c> set, so nothing is logged.</summary>
    // C++: TownArena.cpp:177-208
    public static string NamedOpponentUnavailableReason(in TownChallengePolicy.Offer offer, MatchmakingSnapshot snapshot, bool playerInterface)
    {
        string label = TownChallengePolicy.Title(offer.Encounter);
        string role = TownChallengePolicy.Role(offer.Encounter);
        string opponentId = TownChallengePolicy.OpponentIdConst(offer, 0);
        Actor? match = null;
        bool ambiguous = false;
        foreach (Actor? candidate in snapshot.Characters)
        {
            if (candidate == null || !candidate.Valid) continue;
            if ((opponentId.Length != 0 && MatchesOpponent(candidate, opponentId)) ||
                (opponentId.Length == 0 && TownArenaRuntimePolicy.IdEquals(Id(candidate), role)))
            {
                if (match != null && !ReferenceEquals(match, candidate)) { ambiguous = true; break; }
                match = candidate;
            }
        }
        if (ambiguous) return label + ": identity is ambiguous";
        if (match == null) return label + ": not present in the registry town";
        if (match.InAftercare) return label + ": still in aftercare";
        if (match.InLimbShop) return label + ": waiting for replacement limbs";
        if (match.Fighter) return label + ": already assigned to an active bout";
        bool ready = Ready(match, false, false, playerInterface, out string reason, out _);
        if (!ready) return label + ": " + (reason.Length == 0 ? "not ready to fight" : reason);
        if (!(match.Ability > 0)) return label + ": combat stats unavailable";
        return label + ": unavailable for this matchup";
    }

    // C++: TownArena.cpp:210-232
    public static string PreviewUnavailableReason(in TownChallengePolicy.Offer offer, MatchmakingSnapshot snapshot, bool playerInterface)
    {
        if (!snapshot.PlayersReady) return snapshot.PlayerBlocker.Length == 0 ?
            "Selected fighters are unavailable or recovering" : snapshot.PlayerBlocker;
        if (!TownChallengePolicy.Resolved(offer))
        {
            if (TownChallengePolicy.NamedForDivision(offer.Encounter, offer.Division))
                return NamedOpponentUnavailableReason(offer, snapshot, playerInterface);
            return "No suitable recovered opponents for this team";
        }
        if (snapshot.Players.Count != TownChallengePolicy.Players(offer))
            return "Selected lineup no longer matches this challenge";
        for (int i = 0; i < TownChallengePolicy.Opponents(offer); ++i)
            if (!EligibleOpponent(snapshot, TownChallengePolicy.OpponentIdConst(offer, i)))
            {
                if (i == 0 && TownChallengePolicy.NamedForDivision(offer.Encounter, offer.Division))
                    return NamedOpponentUnavailableReason(offer, snapshot, playerInterface);
                return TownChallengePolicy.OpponentNameConst(offer, i) + " is unavailable or recovering";
            }
        int participants = TownChallengePolicy.Players(offer) + TownChallengePolicy.Opponents(offer);
        if (snapshot.MedicCount < TownAftercarePolicy.RequiredMedics(participants))
            return "Waiting for medical capacity";
        return "";
    }

    // C++: TownArena.cpp:234-238
    public static MatchRules.MatchMode MatchModeFor(in TownChallengePolicy.Offer offer)
    {
        return offer.Mode == (int)TownChallengePolicy.ChallengeMode.Teams1v1 ?
            MatchRules.MatchMode.Teams1v1 : MatchRules.MatchMode.TeamAvB;
    }

    /// <summary>The buy-in for <paramref name="offer"/>. <paramref name="ownScore"/> / <paramref name="enemyScore"/> are the two TeamScores
    /// the quote is derived from, written only when it gets that far (the C++ writes through optional pointers; callers start them at 0).</summary>
    // C++: TownArena.cpp:715-745
    public static TownChallengeBuyInPolicy.Quote QuoteFor(in TownChallengePolicy.Offer offer, MatchmakingSnapshot snapshot, int division,
        bool playerInterface, ref double ownScore, ref double enemyScore)
    {
        var unavailable = new TownChallengeBuyInPolicy.Quote();
        if (!snapshot.PlayersReady || !TownChallengePolicy.Resolved(offer) ||
            snapshot.Players.Count != TownChallengePolicy.Players(offer) ||
            (offer.Generated && PreviewUnavailableReason(offer, snapshot, playerInterface).Length != 0)) return unavailable;
        double[] own = new double[3], ownRatings = new double[3], enemy = new double[4], enemyRatings = new double[4];
        int count = TownChallengePolicy.Opponents(offer);
        if (count < 1 || count > 4 || snapshot.Players.Count > 3) return unavailable;
        for (int i = 0; i < snapshot.Players.Count; ++i)
        {
            own[i] = snapshot.Players[i].Ability;
            ownRatings[i] = snapshot.Players[i].Mmr;
        }
        for (int member = 0; member < count; ++member)
        {
            Actor? match = null;
            foreach (Actor? it in snapshot.Characters)
                if (it != null && MatchesOpponent(it, TownChallengePolicy.OpponentIdConst(offer, member)))
                {
                    if (match != null && !ReferenceEquals(match, it)) return unavailable;
                    match = it;
                }
            if (match == null) return unavailable;
            enemy[member] = match.Ability;
            enemyRatings[member] = match.TownRating;
        }
        double own_ = TownMatchmakingPolicy.TeamScore(own, ownRatings, snapshot.Players.Count);
        double enemy_ = TownMatchmakingPolicy.TeamScore(enemy, enemyRatings, count);
        ownScore = own_;
        enemyScore = enemy_;
        return TownChallengeBuyInPolicy.Price(division, own_, enemy_);
    }

    /// <summary>The C++ <c>GetPlayerReadiness</c>. Whether the selected squad member may book, and why not. The C++ runs it quiet (no <c>Ready</c> log line).</summary>
    // C++: TownArena.cpp:931-950
    public static bool PlayerReadiness(Actor? player, bool playerInterface, out string reason)
    {
        if (!Ready(player, false, true, playerInterface, out reason, out _)) return false;
        if (!player!.InRegistryTown)
        {
            reason = "not at this town registry";
            return false;
        }
        if (player.InAftercare) { reason = "still in aftercare"; return false; }
        if (player.SelectionIdentity.Length == 0) { reason = "live character handle unavailable"; return false; }
        if (!(player.Ability > 0)) { reason = "combat stats unavailable"; return false; }
        reason = "";
        return true;
    }
}
