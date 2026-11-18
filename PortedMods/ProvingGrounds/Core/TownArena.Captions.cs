// Pure parts of upstream/ProvingGrounds/plugin/src/TownArena.cpp (Proving Grounds by Shutori, GPLv3): the card captions.
namespace ProvingGrounds.Core;

public static partial class TownArena
{
    /// <summary>The TownArena.cpp module globals the card captions read (TownArena.cpp:60-61, 68, 98-105): the queued / active challenge
    /// slot, the pending booking, the reservation, the five previews, the direct (1v1) preview, the six unavailable reasons and the buy-in
    /// quotes and ticket. The glue owns one and passes it; every Offer field starts as <c>new Offer()</c>, like the C++ default constructor.
    /// <see cref="UnavailableReasons"/> entries are never null; the C++ std::string cannot be null.</summary>
    public sealed class DisplayState
    {
        public int QueuedChallenge = -1, ActiveChallenge = -1;
        public bool BookingPending;
        public TownChallengePolicy.Offer ChallengeReservation = new TownChallengePolicy.Offer();
        public TownChallengePolicy.Offer DirectPreview = new TownChallengePolicy.Offer();
        public readonly TownChallengePolicy.Offer[] Previews =
            { new TownChallengePolicy.Offer(), new TownChallengePolicy.Offer(), new TownChallengePolicy.Offer(),
              new TownChallengePolicy.Offer(), new TownChallengePolicy.Offer() };
        public readonly string[] UnavailableReasons = { "", "", "", "", "", "" };
        public TownChallengeBuyInPolicy.Ticket BuyInTicket;
        public readonly TownChallengeBuyInPolicy.Quote[] BuyInQuotes = new TownChallengeBuyInPolicy.Quote[6];
    }

    /// <summary>The C++ <c>GetNextChallengeRefreshCaption</c>. <paramref name="hasGame"/> = <c>ou</c> is set; <paramref name="totalHours"/> = <c>getTimeStamp_inGameHours().getTotalHours()</c>;
    /// <paramref name="refreshHours"/> = <c>PGConfig::ChallengeValues().refreshHours</c>.</summary>
    // C++: TownArena.cpp:467-476
    public static string NextChallengeRefreshCaption(bool hasGame, double totalHours, int refreshHours)
    {
        if (!hasGame) return Text.RefreshUnavailableCaption;
        double hours = TownArenaPolicy.HoursUntilChallengeRefresh(totalHours, refreshHours);
        if (!(hours >= 0.0)) return Text.RefreshUnavailableCaption;
        int totalMinutes = (int)Math.Ceiling(hours * 60.0);
        // "Next Refresh: %dh %02dm" (totalMinutes >= 0 here)
        int minutes = totalMinutes % 60;
        return "Next Refresh: " + I(totalMinutes / 60) + "h " + (minutes < 10 ? "0" : "") + I(minutes) + "m";
    }

    /// <summary>The C++ <c>GetChallengeBuyIn</c>. The buy-in shown on a card: the booked ticket's quote for the queued or active slot, the slot's live quote otherwise.
    /// <paramref name="card"/> is <c>LeaderboardStore::GetTownChallenges()</c>.</summary>
    // C++: TownArena.cpp:924-930
    public static TownChallengeBuyInPolicy.Quote ChallengeBuyIn(TownChallengePolicy.Card card, DisplayState s, int slot)
    {
        if (slot < 0 || slot >= 6 || !TownChallengePolicy.Visible(card, slot)) return new TownChallengeBuyInPolicy.Quote();
        if ((slot == s.QueuedChallenge || slot == s.ActiveChallenge) && s.BuyInTicket.Quote.Valid()) return s.BuyInTicket.Quote;
        if (card.Offers[slot].State != TownArenaPolicy.OfferState.Available) return new TownChallengeBuyInPolicy.Quote();
        return s.BuyInQuotes[slot];
    }

    /// <summary>The offer a card shows. <paramref name="slot"/> is 0..4.</summary>
    // C++: TownArena.cpp:951-958
    public static TownChallengePolicy.Offer DisplayedOffer(TownChallengePolicy.Card card, DisplayState s, int slot)
    {
        TownChallengePolicy.Offer offer = slot == s.QueuedChallenge || slot == s.ActiveChallenge ?
            s.ChallengeReservation : card.Offers[slot].State == TownArenaPolicy.OfferState.Available ? s.Previews[slot] :
            TownChallengePolicy.PreviewSource(card, slot);
        offer.State = card.Offers[slot].State;
        return offer;
    }

    // C++: TownArena.cpp:959-970
    public static string StoredPortraitName(in TownChallengePolicy.Offer offer, int member)
    {
        string name = TownChallengePolicy.OpponentNameConst(offer, member);
        // Older generated previews stored the catalog description with the name.
        // Remove only that exact known suffix, preserving parentheses in names.
        TownFighterCatalog.Entry? entry = TownFighterCatalog.Find(offer.Roles[member]);
        if (entry != null)
        {
            string suffix = " (" + TownChallengePolicy.DivisionName(entry.Tier) + " " + entry.Type + ")";
            if (name.EndsWith(suffix, StringComparison.Ordinal))
                name = name.Substring(0, name.Length - suffix.Length);
        }
        return name.Length == 0 ? "Unavailable" : name;
    }

    /// <summary>The C++ <c>GetChallengePortraits</c>. The portrait cards: per slot (5 = Skarn) the members' matched actor (an index into <paramref name="actors"/>, -1 = NULL) and
    /// their names. <paramref name="card"/> is <c>Challenges()</c>; <paramref name="actorsAvailable"/> = <c>ou</c>, the registry valid and in a
    /// town (TownArena.cpp:1004-1005); <paramref name="actors"/> = the valid, distinct characters within 2500 of the registry in its town
    /// (TownArena.cpp:1006-1014), which the glue gathers.</summary>
    // C++: TownArena.cpp:971-1031
    public static void ChallengePortraits(TownChallengePolicy.Card card, DisplayState s, bool actorsAvailable, IReadOnlyList<Actor> actors,
        out List<int>[] opponents, out List<string>[] names)
    {
        opponents = new List<int>[6];
        names = new List<string>[6];
        var offers = new TownChallengePolicy.Offer[6];
        bool any = false;
        for (int slot = 0; slot < 6; ++slot)
        {
            opponents[slot] = new List<int>();
            names[slot] = new List<string>();
            offers[slot] = new TownChallengePolicy.Offer();
            if (slot == 5)
            {
                offers[slot] = new TownChallengePolicy.Offer();
                offers[slot].Encounter = (int)TownChallengePolicy.Encounter.Skarn;
                offers[slot].Division = 2;
                opponents[slot].Add(-1);
                names[slot].Add(TownChallengePolicy.Title((int)TownChallengePolicy.Encounter.Skarn));
                any = true;
                continue;
            }
            if (!TownChallengePolicy.Visible(card, slot)) continue;
            offers[slot] = DisplayedOffer(card, s, slot);
            if (TownChallengePolicy.Resolved(offers[slot]))
            {
                int count = TownChallengePolicy.Opponents(offers[slot]);
                for (int member = 0; member < count; ++member) opponents[slot].Add(-1);
                for (int member = 0; member < count; ++member)
                    names[slot].Add(StoredPortraitName(offers[slot], member));
            }
            else if (offers[slot].Generated &&
                TownChallengePolicy.OfferRarity(offers[slot], offers[slot].Division) == 3 &&
                TownChallengePolicy.NamedForDivision(offers[slot].Encounter, offers[slot].Division))
            {
                // A named Legendary anchor remains the intended opponent even
                // while recovering, so the card can show a faded live portrait.
                opponents[slot].Add(-1);
                names[slot].Add(TownChallengePolicy.Title(offers[slot].Encounter));
            }
            else continue;
            any = true;
        }
        if (!any || !actorsAvailable) return;
        // Match the booking identity, including legacy keys. An ambiguous key
        // gets no portrait, even when only one of its namesakes is fight-ready.
        for (int slot = 0; slot < 6; ++slot)
            for (int member = 0; member < opponents[slot].Count; ++member)
            {
                int match = -1;
                string opponentId = TownChallengePolicy.OpponentIdConst(offers[slot], member);
                string intendedRole = opponentId.Length == 0 && member == 0 ? TownChallengePolicy.Role(offers[slot].Encounter) : "";
                for (int i = 0; i < actors.Count; ++i)
                    if ((opponentId.Length != 0 && MatchesOpponent(actors[i], opponentId)) ||
                        (opponentId.Length == 0 && intendedRole.Length != 0 && TownArenaRuntimePolicy.IdEquals(Id(actors[i]), intendedRole)))
                    {
                        if (match >= 0) { match = -1; break; }
                        match = i;
                    }
                opponents[slot][member] = match;
                if (match >= 0) names[slot][member] = actors[match].Name;
            }
    }

    /// <summary>The C++ <c>GetChallengeRarity</c>. <paramref name="card"/> is <c>Challenges()</c> (the glue runs it first: it refreshes and binds the card).</summary>
    // C++: TownArena.cpp:1032-1039
    public static int ChallengeRarity(TownChallengePolicy.Card card, DisplayState s, int slot)
    {
        if (slot < 0 || slot >= 5) return -1;
        if (!TownChallengePolicy.Visible(card, slot)) return -1;
        TownChallengePolicy.Offer offer = DisplayedOffer(card, s, slot);
        return TownChallengePolicy.OfferRarity(offer, card.Division);
    }

    // C++: TownArena.cpp:1040-1048
    public static bool IsUniqueLegendaryChallenge(TownChallengePolicy.Card card, DisplayState s, int slot)
    {
        if (slot < 0 || slot >= 5) return false;
        if (!TownChallengePolicy.Visible(card, slot)) return false;
        TownChallengePolicy.Offer offer = DisplayedOffer(card, s, slot);
        return TownChallengePolicy.OfferRarity(offer, card.Division) == 3 &&
            TownChallengePolicy.NamedForDivision(offer.Encounter, card.Division);
    }

    /// <summary>The C++ <c>GetChallengeUnavailableReason</c>.</summary>
    // C++: TownArena.cpp:1049-1055
    public static string ChallengeUnavailableReason(TownChallengePolicy.Card card, DisplayState s, int slot)
    {
        if (slot < 0 || slot >= 5) return "";
        TownChallengePolicy.Offer offer = DisplayedOffer(card, s, slot);
        return offer.State == TownArenaPolicy.OfferState.Available ? s.UnavailableReasons[slot] : "";
    }

    /// <summary>The C++ <c>GetDirectCaption</c>. <paramref name="division"/> = <c>GetDivision()</c>, the stored card's division (TownArena.cpp:466).</summary>
    // C++: TownArena.cpp:1056-1061
    public static string DirectCaption(DisplayState s, int division)
    {
        if (s.BookingPending) return "Cancel booking";
        if (!TownChallengePolicy.Resolved(s.DirectPreview)) return "1v1: select one fighter with suitable recovered opposition";
        // " | Marks: %.2fx" (the float promoted to double)
        string marks = " | Marks: " + CMath.FormatF(ArenaMarks.DivisionMultiplier(division), 2) + "x";
        return "Book " + TownChallengePolicy.DivisionName(division) + " 1v1 vs " + s.DirectPreview.Name + marks;
    }

    /// <summary>The C++ <c>GetProgressCaption</c>.</summary>
    // C++: TownArena.cpp:1062-1072
    public static string ProgressCaption(TownChallengePolicy.Card card)
    {
        uint completed = 0;
        for (uint bit = 1; bit <= (uint)TownChallengePolicy.UniqueVictory.Veyr; bit <<= 1)
            if ((card.UniqueWins & bit) != 0) ++completed;
        return "Challenge wins: " + U(card.ChallengeWins) + " | Skarn unlock: " + U(completed) + " / 5 unique fighters defeated";
    }

    /// <summary>The C++ <c>GetChallengeCaption</c>. <paramref name="card"/> is <c>Challenges()</c>.</summary>
    // C++: TownArena.cpp:1150-1182
    public static string ChallengeCaption(TownChallengePolicy.Card card, DisplayState s, int slot, bool includeLineup = true)
    {
        if (slot < 0 || slot >= 5) return "Unavailable";
        if (!TownChallengePolicy.Visible(card, slot)) return "";
        TownChallengePolicy.Offer offer = DisplayedOffer(card, s, slot);
        int rarity = TownChallengePolicy.OfferRarity(offer, card.Division);
        string challengeLabel = rarity == 3 &&
            TownChallengePolicy.NamedForDivision(offer.Encounter, card.Division) ?
            "Unique" : TownChallengePolicy.RarityName(rarity);
        if (offer.Generated && !TownChallengePolicy.Resolved(offer) && offer.State == TownArenaPolicy.OfferState.Consumed)
            return challengeLabel +
                " | " + TownChallengePolicy.DivisionName(card.Division) + " | " +
                TownChallengePolicy.ModeName(offer.Mode) + "\nAttempt used";
        string format = I(TownChallengePolicy.Players(offer)) + "v" + I(TownChallengePolicy.Opponents(offer));   // "%dv%d"
        string lineup = offer.Name.Length == 0 ? TownChallengePolicy.Title(offer.Encounter) : offer.Name;
        for (int i = 1; i < TownChallengePolicy.Opponents(offer); ++i)
            lineup += " + " + (TownChallengePolicy.OpponentNameConst(offer, i).Length == 0 ?
                TownChallengePolicy.Title(TownChallengePolicy.OpponentRole(offer, i)) : TownChallengePolicy.OpponentNameConst(offer, i));
        bool resolved = TownChallengePolicy.Resolved(offer);
        string text = challengeLabel +
            " | " + TownChallengePolicy.DivisionName(card.Division) + " | " +
            TownChallengePolicy.ModeName(offer.Mode) + " | " + format + "\n" +
            (includeLineup ? lineup + " | " : "");
        if (!offer.Generated) text = "Legacy | " + text;
        if (offer.Generated && !resolved && offer.State == TownArenaPolicy.OfferState.Available)
            return challengeLabel +
                " | " + TownChallengePolicy.DivisionName(card.Division) + " | " +
                TownChallengePolicy.ModeName(offer.Mode) + "\n" + s.UnavailableReasons[slot];
        return text + (offer.State == TownArenaPolicy.OfferState.Consumed ? "Attempt used" :
            offer.State == TownArenaPolicy.OfferState.Booked ? "Reserved" : !resolved ? "Opponent unavailable" : "Book");
    }
}
