// Pure parts of upstream/ProvingGrounds/plugin/src/TownArena.cpp (Proving Grounds by Shutori, GPLv3): small rules over plain values.
using System.Text;

namespace ProvingGrounds.Core;

public static partial class TownArena
{
    /// <summary>std::string's ordering: bytewise (unsigned) over the UTF-8 bytes, shorter first on a common prefix.</summary>
    sealed class Utf8Ordinal : IComparer<string>
    {
        public static readonly Utf8Ordinal Instance = new();
        public int Compare(string? x, string? y)
        {
            byte[] a = Encoding.UTF8.GetBytes(x ?? ""), b = Encoding.UTF8.GetBytes(y ?? "");
            int n = Math.Min(a.Length, b.Length);
            for (int i = 0; i < n; ++i)
                if (a[i] != b[i]) return a[i] < b[i] ? -1 : 1;
            return a.Length.CompareTo(b.Length);
        }
    }

    /// <summary>The lineup key of the selected squad: <paramref name="selectionIdentities"/> are the players'
    /// <c>TownMatchmakingRuntime::SelectionIdentity</c>, sorted as std::string sorts, each written <c>'|' size ':' id</c> (size = UTF-8 bytes).</summary>
    // C++: TownArena.cpp:157-166
    public static string PlayerLineupKey(IEnumerable<string> selectionIdentities)
    {
        var ids = new List<string>(selectionIdentities);
        ids.Sort(Utf8Ordinal.Instance);   // std::sort: equal strings are interchangeable, so instability cannot show
        var key = new StringBuilder();
        for (int i = 0; i < ids.Count; ++i) key.Append('|').Append(U((uint)Encoding.UTF8.GetByteCount(ids[i]))).Append(':').Append(ids[i]);
        return key.ToString();
    }

    /// <summary>A save's first card is seeded from its save key (<c>LeaderboardStore::GetActiveSaveKey()</c>, its UTF-8 bytes) while the
    /// first offer still has the never-generated epoch -2.</summary>
    // C++: TownArena.cpp:244-248
    public static void FoldSaveKey(TownChallengePolicy.Card card, string saveKey)
    {
        if (card.Offers[0].Epoch == -2)
        {
            byte[] key = Encoding.UTF8.GetBytes(saveKey);
            unchecked
            {
                for (int i = 0; i < key.Length; ++i) card.Seed = card.Seed * 33u + key[i];
            }
        }
    }

    /// <summary>The refresh button's state. <paramref name="phase"/>: the C++ <c>enum Phase { Idle, Preparing, Fighting, Aftercare }</c>
    /// (0..3); <paramref name="cost"/> = <c>GetChallengeRefreshCost()</c>; <paramref name="balance"/> = <c>TownChallengeWallet::Balance()</c>
    /// (read only when the rest allows it; any value then).</summary>
    // C++: TownArena.cpp:483-490
    public static bool CanBuyChallengeRefresh(bool hasActiveSave, bool hasGame, bool registryValid, DisplayState s, int phase,
        int cost, int balance)
    {
        const int Preparing = 1, Fighting = 2;
        if (!hasActiveSave || !hasGame || !registryValid ||
            s.BookingPending || s.QueuedChallenge >= 0 || s.ActiveChallenge >= 0 ||
            phase == Preparing || phase == Fighting) return false;
        return cost > 0 && balance >= cost;
    }

    /// <summary>A paid refresh replaces the five offers (the card's own seed and draws are not advanced: the new offers come from a copy
    /// seeded with the refresh nonce) and counts it. <paramref name="epoch"/> = <c>ChallengeCardEpoch</c> of the game hours.</summary>
    // C++: TownArena.cpp:527-536
    public static void ApplyPaidRefresh(TownChallengePolicy.Card card, int epoch)
    {
        TownChallengePolicy.Card refreshed = card.Clone();
        refreshed.Seed = unchecked(card.Seed ^ (0x9e3779b9u * card.PaidRefreshNonce));
        if (refreshed.Seed == 0) refreshed.Seed = 0x6d2b79f5u;
        for (int slot = 0; slot < 5; ++slot)
            refreshed.Offers[slot] = new TownChallengePolicy.Offer();
        TownChallengePolicy.RefreshGenerated(refreshed, epoch, -1);
        for (int slot = 0; slot < 5; ++slot)
            card.Offers[slot] = refreshed.Offers[slot];
        if (card.PaidRefreshCount < int.MaxValue) ++card.PaidRefreshCount;
        if (card.PaidRefreshNonce < uint.MaxValue) ++card.PaidRefreshNonce;
    }

    /// <summary>How many fighters an ambient bout may use: the fit medics, plus the fighters in aftercare (their medics are expected back).</summary>
    // C++: TownArena.cpp:657-658
    public static int AmbientCapacity(int medics, bool aftercare, int fighterCount)
    {
        return Math.Min(8, medics + (aftercare ? fighterCount : 0));
    }

    /// <summary>The announced start of the next ambient bout, on a whole game minute. <paramref name="totalHours"/> = the game's hours now;
    /// lead time from <see cref="BalanceTuning"/> (defaults reproduce the shipped 1.0-2.0 h window).</summary>
    // C++: TownArena.cpp:682-685
    public static double NextNpcStartHours(TownChallengePolicy.Card card, double totalHours)
    {
        BalanceTuning.Values tuning = BalanceTuning.Get();
        uint delay = TownMatchmakingPolicy.Detail.Hash(card.AmbientSeed, "next NPC start") % 61;
        return Math.Ceiling((totalHours +
            tuning.NpcLeadHoursMin + (delay / 60.0) * tuning.NpcLeadHoursSpread) * 60.0) / 60.0;
    }

    /// <summary>The card key the preview search is keyed on (the glue compares it with the previous one).</summary>
    // C++: TownArena.cpp:755-759
    public static string PreviewKey(string lineupKey, TownChallengePolicy.Card card)
    {
        var key = new StringBuilder();
        key.Append(lineupKey).Append(':').Append(I(card.Division));
        for (int i = 0; i < 5; ++i)
            key.Append(':').Append(I(card.Offers[i].Epoch)).Append(':').Append(U(card.Offers[i].Draw)).
                Append(':').Append(I(card.Offers[i].Encounter)).Append(':').Append(I(card.Offers[i].Mode));
        return key.ToString();
    }

    /// <summary>The reason a finished search found no lineup (<paramref name="match"/> invalid): too weak, the named anchor missing, or no
    /// opponents. <paramref name="snapshot"/> is the frozen preview snapshot.</summary>
    // C++: TownArena.cpp:815-829
    public static string SearchFailureReason(TownMatchmakingPolicy.Match match, in TownChallengePolicy.Offer offer,
        MatchmakingSnapshot snapshot, TownChallengePolicy.Card card, bool playerInterface)
    {
        if (match.Failure == TownMatchmakingPolicy.Match.FailureCode.PlayersTooWeak)
            return "Selected fighter is too weak for this challenge.";
        int rarity = TownChallengePolicy.OfferRarity(offer, card.Division);
        string anchor = rarity == 3 &&
            TownChallengePolicy.NamedForDivision(offer.Encounter, card.Division) ?
            TownChallengePolicy.Role(offer.Encounter) : "";
        bool anchorReady = anchor.Length == 0;
        for (int i = 0; i < snapshot.Npcs.Count; ++i)
            if (snapshot.Npcs[i].Role == anchor) anchorReady = true;
        return anchorReady ?
            "No suitable recovered opponents for this challenge." :
            NamedOpponentUnavailableReason(offer, snapshot, playerInterface);
    }

    // C++: TownArena.cpp:870-871
    public static string SearchingReason(MatchmakingSnapshot snapshot)
    {
        return snapshot.PlayersReady ?
            "Searching eligible opponents..." : snapshot.PlayerBlocker;
    }

    /// <summary>The search request's seed, and for a named Legendary its required role and whether it must be solo (the anchor not yet
    /// beaten). <paramref name="requiredRole"/> is "" and <paramref name="forceSoloNamed"/> false otherwise (the Request's defaults).</summary>
    // C++: TownArena.cpp:874, 881-882, 889-895
    public static uint SearchRequestSeed(in TownChallengePolicy.Offer offer, int slot, TownChallengePolicy.Card card,
        out string requiredRole, out bool forceSoloNamed)
    {
        int rarity = TownChallengePolicy.OfferRarity(offer, card.Division);
        requiredRole = ""; forceSoloNamed = false;
        uint seed = unchecked(offer.Draw ^ ((uint)offer.Epoch * 1664525u) ^ ((uint)slot * 1013904223u));
        if (rarity == 3 && TownChallengePolicy.NamedForDivision(offer.Encounter, card.Division))
        {
            requiredRole = TownChallengePolicy.Role(offer.Encounter);
            uint victory = TownChallengePolicy.UniqueVictoryForEncounter(offer.Encounter);
            bool defeated = offer.Encounter == (int)TownChallengePolicy.Encounter.Skarn ?
                card.SkarnWon : victory != 0 && (card.UniqueWins & victory) != 0;
            forceSoloNamed = !defeated;
        }
        return seed;
    }

    /// <summary>The direct (1v1) preview's matchmaking seed.</summary>
    // C++: TownArena.cpp:915
    public static uint DirectSearchSeed(TownChallengePolicy.Card card) => card.Seed ^ 0x12345678u;

    /// <summary>Whether a fighter on <paramref name="team"/> is marked a winner for aftercare.</summary>
    // C++: TownArena.cpp:1082-1083
    public static bool MarksWinner(SparPodium.OutcomeKind outcome, MatchRules.MatchTeam team)
    {
        return (outcome == SparPodium.OutcomeKind.TeamAWins && team == MatchRules.MatchTeam.TeamA) ||
            (outcome == SparPodium.OutcomeKind.TeamBWins && team == MatchRules.MatchTeam.TeamB);
    }

    /// <summary>The side the bookie settles an ambient bout on (-1 = refund).</summary>
    // C++: TownArena.cpp:1094-1095
    public static int BookieSide(SparPodium.OutcomeKind outcome)
    {
        return outcome == SparPodium.OutcomeKind.TeamAWins ? 0 :
            outcome == SparPodium.OutcomeKind.TeamBWins ? 1 : -1;
    }

    /// <summary>The challenge outcome a player bout settles with (1 won, 0 lost, -1 refund).</summary>
    // C++: TownArena.cpp:1099-1100
    public static int ChallengeOutcome(SparPodium.OutcomeKind outcome)
    {
        bool playerWon = outcome == SparPodium.OutcomeKind.TeamAWins;
        return playerWon ? 1 : outcome == SparPodium.OutcomeKind.TeamBWins ? 0 : -1;
    }

    /// <summary>The unique victories a won challenge records: from the reservation's roles first, then from the live opponents'
    /// <c>Id(opponent)</c> (<paramref name="liveOpponentIds"/>, the fighters after the players; null for an invalid handle, skipped).</summary>
    // C++: TownArena.cpp:1114-1133
    public static uint DefeatedUniques(in TownChallengePolicy.Offer reservation, IReadOnlyList<string?> liveOpponentIds)
    {
        uint defeated = 0;
        // Use the reserved lineup first: defeated/removed actors can already
        // have an invalid live handle by the time the podium result arrives.
        for (int member = 0; member < TownChallengePolicy.Opponents(reservation); ++member)
            for (int encounter = (int)TownChallengePolicy.Encounter.Senn;
                encounter <= (int)TownChallengePolicy.Encounter.Veyr; ++encounter)
                if (encounter != (int)TownChallengePolicy.Encounter.Skarn &&
                    TownArenaRuntimePolicy.IdEquals(reservation.Roles[member], TownChallengePolicy.Role(encounter)))
                    defeated |= TownChallengePolicy.UniqueVictoryForEncounter(encounter);
        for (int i = 0; i < liveOpponentIds.Count; ++i)
        {
            if (liveOpponentIds[i] == null) continue;
            for (int encounter = (int)TownChallengePolicy.Encounter.Senn;
                encounter <= (int)TownChallengePolicy.Encounter.Veyr; ++encounter)
                if (encounter != (int)TownChallengePolicy.Encounter.Skarn &&
                    TownArenaRuntimePolicy.IdEquals(liveOpponentIds[i], TownChallengePolicy.Role(encounter)))
                    defeated |= TownChallengePolicy.UniqueVictoryForEncounter(encounter);
        }
        return defeated;
    }

    /// <summary>The marks multiplier and unique bonus a bout captures when it starts. <paramref name="player"/> = a player booking;
    /// <paramref name="division"/> = <c>GetDivision()</c>; <paramref name="activeRarity"/> = <c>GetChallengeRarity(activeChallenge)</c>
    /// (read only when <paramref name="activeChallenge"/> &gt;= 0); <paramref name="reservationEncounter"/> = the reservation's encounter.</summary>
    // C++: TownArena.cpp:1725-1733
    public static void ActiveMarks(bool player, int division, int activeChallenge, int activeRarity, int reservationEncounter,
        out float marksMultiplier, out int marksBonus)
    {
        marksMultiplier = player ? ArenaMarks.ChallengeMultiplier(division,
            activeChallenge >= 0 ? activeRarity : 0) *
            (float)PGConfig.ChallengeValues().WinMarksMultiplier : 1.0f;
        int activeEncounter = activeChallenge >= 0 ? reservationEncounter : -1;
        bool skarnChallenge = activeEncounter == (int)TownChallengePolicy.Encounter.Skarn;
        bool uniqueChallenge = skarnChallenge ||
            TownChallengePolicy.UniqueVictoryForEncounter(activeEncounter) != 0;
        marksBonus = player && uniqueChallenge ?
            ArenaMarks.UniqueChallengeBonus(division, skarnChallenge) : 0;
    }
}
