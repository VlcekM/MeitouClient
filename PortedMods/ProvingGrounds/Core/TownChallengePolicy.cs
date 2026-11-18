using System.Runtime.CompilerServices;
using static ProvingGrounds.Core.TownArenaPolicy;

namespace ProvingGrounds.Core;

/// <summary>C++: TownChallengePolicy.h (header-only, namespace <c>TownChallengePolicy</c>): the challenge card, its offers and their rules.
/// The compact text codec is in TownChallengePolicy.Codec.cs.
///
/// Types: <see cref="Offer"/> is a struct (the C++ copies it by value everywhere: <c>o = Offer()</c>, <c>source = preview</c>,
/// <c>Offer source = card.offers[slot]</c>) and is mutated through <c>ref</c> (C++ <c>Offer&amp;</c>); <see cref="Card"/> is a sealed
/// class mutated in place (C++ <c>Card&amp;</c>) with a deep <see cref="Card.Clone"/> at every C++ by-value copy. C++ functions that take
/// or return <c>int</c> for an enum keep <c>int</c>, so out-of-range values behave the same.
///
/// Not ported (legacy save support dropped, owner decision D7: no old-save import): <c>EnsureSkarn</c> (:164-171), <c>BeginSkarn</c>
/// (:172-175), <c>EndSkarn</c> (:176-184) and <c>Refresh</c> (:185-208). None has a caller in the shipped plugin (TW §2.5 "Legacy
/// offers", checked by grep over plugin/src/*.cpp and *.h). Their Card fields (<c>skarnGenerated</c>, <c>skarnInProgress</c>, ...) stay:
/// the codec and the save snapshot carry them.</summary>
public static partial class TownChallengePolicy
{
    // C++: TownChallengePolicy.h:11
    public enum ChallengeMode { Teams, Teams1v1 }

    // C++: TownChallengePolicy.h:12-16
    public enum Encounter
    {
        Sabre, Katana, Champion, Senn,
        Torka, Skarn, RessaVane, BumPer,
        Veyr
    }

    /// <summary>Unsigned bits (the C++ uses them as <c>unsigned</c>). <see cref="AllUniqueVictories"/> keeps its C++ name (it has no
    /// <c>Unique</c> prefix to drop).</summary>
    // C++: TownChallengePolicy.h:17-20
    public enum UniqueVictory : uint
    {
        RessaVane = 1, BumPer = 2, Senn = 4,
        Torka = 8, Veyr = 16, AllUniqueVictories = 31
    }

    const uint AllUniqueVictoriesBits = (uint)UniqueVictory.AllUniqueVictories;

    // C++: TownChallengePolicy.h:21-29
    public static uint UniqueVictoryForEncounter(int encounter)
    {
        if (encounter == (int)Encounter.RessaVane) return (uint)UniqueVictory.RessaVane;
        if (encounter == (int)Encounter.BumPer) return (uint)UniqueVictory.BumPer;
        if (encounter == (int)Encounter.Senn) return (uint)UniqueVictory.Senn;
        if (encounter == (int)Encounter.Torka) return (uint)UniqueVictory.Torka;
        if (encounter == (int)Encounter.Veyr) return (uint)UniqueVictory.Veyr;
        return 0;
    }

    // C++: TownChallengePolicy.h:31-38
    public static int NamedDivision(int encounter)
    {
        if (encounter == (int)Encounter.RessaVane) return 0;
        if (encounter == (int)Encounter.BumPer) return 1;
        if (encounter == (int)Encounter.Senn || encounter == (int)Encounter.Torka ||
            encounter == (int)Encounter.Skarn || encounter == (int)Encounter.Veyr) return 2;
        return -1;
    }

    // C++: TownChallengePolicy.h:39-41
    public static bool NamedForDivision(int encounter, int division)
    {
        return NamedDivision(encounter) == division;
    }

    // C++: TownChallengePolicy.h:42-49
    public static int NamedRoleDivision(string role)
    {
        if (role == "616-Proving Grounds.mod") return 0;
        if (role == "619-Proving Grounds.mod") return 1;
        if (role == "169-Proving Grounds.mod" || role == "168-Proving Grounds.mod" ||
            role == "167-Proving Grounds.mod" || role == "623-Proving Grounds.mod") return 2;
        return -1;
    }

    // C++: TownChallengePolicy.h:51-55
    public static string OpponentKey(string uid, string role, string name)
    {
        if (uid.Length != 0) return uid;
        return role.Length == 0 || name.Length == 0 ? "" : "name:" + role + ":" + name;
    }

    // C++: TownChallengePolicy.h:56-59
    public static int RarityForRoll(uint roll)
    {
        return roll < 40 ? 0 : roll < 70 ? 1 : roll < 90 ? 2 : 3;
    }

    /// <summary>The C++ <c>std::string roles[4]</c> inside <see cref="Offer"/>: a fixed array copied with the struct.</summary>
    [InlineArray(4)]
    public struct RoleIds
    {
        string _element0;
    }

    /// <summary>C++: TownChallengePolicy.h:60-74. A struct because the C++ copies offers by value everywhere. The C++ constructor's
    /// defaults (epoch -2, mode Teams, state Available, every string "") are in the explicit parameterless constructor: ALWAYS create one
    /// with <c>new Offer()</c>. Every way that does not run it gives epoch 0 and null strings and must never be used: <c>default(Offer)</c>,
    /// <c>new Offer[n]</c> and <c>Array.Resize</c> (new elements), an Offer field of a class or struct with no initializer, a Dictionary
    /// <c>TryGetValue</c> miss / <c>GetValueOrDefault</c> / LINQ <c>FirstOrDefault</c> on an empty sequence, and an <c>out</c> parameter
    /// assigned <c>default</c>. (<see cref="Card"/> fills its array and its Skarn offer with <c>new Offer()</c>.) Strings are never null,
    /// like std::string.
    /// Do not call Equals/GetHashCode/Assert.Equal on it (.NET throws NotSupportedException for a struct holding an InlineArray, and the
    /// C++ has no operator==): compare fields.</summary>
    public struct Offer
    {
        public int Epoch, Encounter, Format, Mode;
        public OfferState State;
        public string Opponent, Name;
        public string SecondOpponent, SecondName;
        public string ThirdOpponent, ThirdName;
        public string FourthOpponent, FourthName;
        public bool Generated;
        public uint Draw;
        public int Division, PlayerCount, EnemyCount;
        public string Context;
        public RoleIds Roles;

        // C++: TownChallengePolicy.h:72-73
        public Offer()
        {
            Epoch = -2; Encounter = 0; Format = 0; Mode = (int)ChallengeMode.Teams; State = OfferState.Available;
            Opponent = ""; Name = ""; SecondOpponent = ""; SecondName = ""; ThirdOpponent = ""; ThirdName = "";
            FourthOpponent = ""; FourthName = "";
            Generated = false; Draw = 0; Division = 0; PlayerCount = 0; EnemyCount = 0;
            Context = "";
            Roles = default;
            for (int i = 0; i < 4; ++i) Roles[i] = "";
        }
    }

    // C++: TownChallengePolicy.h:75-77
    public static string ModeName(int mode)
    {
        return mode == (int)ChallengeMode.Teams1v1 ? "Teams 1v1" : "Teams";
    }

    // C++: TownChallengePolicy.h:78
    public static int Players(in Offer o) { return o.Generated ? o.PlayerCount : o.Format == 6 ? 6 : o.Format == 0 || o.Format == 3 ? 1 : o.Format == 1 || o.Format == 2 ? 2 : 3; }
    // C++: TownChallengePolicy.h:79
    public static int Opponents(in Offer o) { return o.Generated ? o.EnemyCount : o.Format == 6 ? 3 : o.Format >= 2 && o.Format <= 4 ? 2 : 1; }

    // C++: TownChallengePolicy.h:80-88
    public static int SecondRole(in Offer o)
    {
        return o.Encounter == (int)Encounter.Sabre ? (int)Encounter.Katana :
            o.Encounter == (int)Encounter.Katana ? (int)Encounter.Sabre :
            o.Encounter == (int)Encounter.Champion ? (int)Encounter.Katana :
            o.Encounter == (int)Encounter.Senn ? (int)Encounter.Torka :
            o.Encounter == (int)Encounter.Torka || o.Encounter == (int)Encounter.Skarn ? (int)Encounter.Senn :
            (int)Encounter.Champion;
    }

    /// <summary>The C++ non-const overload (returns <c>std::string&amp;</c>). C# cannot overload on <c>ref</c> vs <c>in</c>, so the const
    /// overload is <see cref="OpponentIdConst"/>.</summary>
    // C++: TownChallengePolicy.h:89
    public static ref string OpponentId(ref Offer o, int member) { if (member == 0) return ref o.Opponent; if (member == 1) return ref o.SecondOpponent; if (member == 2) return ref o.ThirdOpponent; return ref o.FourthOpponent; }
    // C++: TownChallengePolicy.h:90
    public static string OpponentIdConst(in Offer o, int member) { return member == 0 ? o.Opponent : member == 1 ? o.SecondOpponent : member == 2 ? o.ThirdOpponent : o.FourthOpponent; }
    /// <summary>Non-const overload; the const one is <see cref="OpponentNameConst"/>.</summary>
    // C++: TownChallengePolicy.h:91
    public static ref string OpponentName(ref Offer o, int member) { if (member == 0) return ref o.Name; if (member == 1) return ref o.SecondName; if (member == 2) return ref o.ThirdName; return ref o.FourthName; }
    // C++: TownChallengePolicy.h:92
    public static string OpponentNameConst(in Offer o, int member) { return member == 0 ? o.Name : member == 1 ? o.SecondName : member == 2 ? o.ThirdName : o.FourthName; }

    // C++: TownChallengePolicy.h:93
    public static int OpponentRole(in Offer o, int member) { return member == 0 ? o.Encounter : member == 1 ? SecondRole(o) : 4; }

    // C++: TownChallengePolicy.h:94-98
    public static bool Resolved(in Offer o)
    {
        if (Players(o) < 1 || Opponents(o) < 1) return false;
        for (int i = 0; i < Opponents(o); ++i) if (OpponentIdConst(o, i).Length == 0) return false;
        return true;
    }

    // The reservation is session-owned. A migrated named source must survive
    // booking its regular counterpart; both views share the source attempt.
    // C++: TownChallengePolicy.h:101-109
    public static bool ReserveOffer(ref Offer source, in Offer preview, ref Offer reservation)
    {
        if (source.State != OfferState.Available || source.Epoch != preview.Epoch ||
            !Resolved(preview)) return false;
        reservation = preview;
        if (source.Generated || !preview.Generated) source = preview;
        BookOffer(ref source.State);
        reservation.State = source.State;
        return true;
    }

    // C++: TownChallengePolicy.h:110-116
    public static bool SameEncounter(in Offer a, in Offer b)
    {
        if (Players(a) != Players(b) || Opponents(a) != Opponents(b)) return false;
        if (Opponents(a) == 1) return a.Encounter == b.Encounter;
        return (a.Encounter == b.Encounter && SecondRole(a) == SecondRole(b)) ||
            (a.Encounter == SecondRole(b) && SecondRole(a) == b.Encounter);
    }

    /// <summary>C++: TownChallengePolicy.h:117-136. Mutated in place where the C++ takes <c>Card&amp;</c>; every C++ by-value copy
    /// (<c>Card carrier = card;</c>, <c>card = parsed;</c>, a snapshot holding one) is <see cref="Clone"/> or <see cref="AssignFrom"/>.
    /// <see cref="Seed"/> is a FIELD so <see cref="Draw"/> can pass it by <c>ref</c>.</summary>
    public sealed class Card
    {
        public uint Seed;
        public uint AmbientSeed;
        public int Division;
        // readonly like Offers: the C++ vectors are members, so `card = parsed` copies contents into the same vectors
        public readonly List<List<string>> PlayerHistory = new(), AmbientHistory = new();
        public uint Milestones, UniqueWins;
        public uint ChallengeWins;
        public readonly Offer[] Offers = { new Offer(), new Offer(), new Offer(), new Offer(), new Offer() };
        public Offer SkarnOffer = new Offer();
        public bool SkarnGenerated, SkarnWon, SkarnAttempted, SkarnInProgress;
        public double SkarnLastEnd;
        public double LastChallengeEndHours;
        public bool HasChallengeEnd;
        public int PaidRefreshCount;
        // Kept separate from seed, which also drives scheduled offers and Skarn.
        public uint PaidRefreshNonce;

        // C++: TownChallengePolicy.h:134-135
        public Card()
        {
            Seed = 1; AmbientSeed = 2463534242u; Division = 0; Milestones = 0; UniqueWins = 0; ChallengeWins = 0;
            SkarnGenerated = false; SkarnWon = false; SkarnAttempted = false; SkarnInProgress = false; SkarnLastEnd = 0.0;
            LastChallengeEndHours = 0.0; HasChallengeEnd = false; PaidRefreshCount = 0; PaidRefreshNonce = 1;
        }

        /// <summary>The C++ implicit copy constructor: every field, the histories and offers copied (an Offer copy is complete: its
        /// strings are immutable and its roles are an inline array).</summary>
        public Card Clone()
        {
            Card c = new Card();
            c.AssignFrom(this);
            return c;
        }

        /// <summary>The C++ implicit copy assignment <c>*this = other</c>.</summary>
        public void AssignFrom(Card other)
        {
            if (ReferenceEquals(this, other)) return;
            Seed = other.Seed; AmbientSeed = other.AmbientSeed; Division = other.Division;
            CopyHistory(other.PlayerHistory, PlayerHistory); CopyHistory(other.AmbientHistory, AmbientHistory);
            Milestones = other.Milestones; UniqueWins = other.UniqueWins; ChallengeWins = other.ChallengeWins;
            for (int i = 0; i < 5; ++i) Offers[i] = other.Offers[i];
            SkarnOffer = other.SkarnOffer;
            SkarnGenerated = other.SkarnGenerated; SkarnWon = other.SkarnWon; SkarnAttempted = other.SkarnAttempted;
            SkarnInProgress = other.SkarnInProgress; SkarnLastEnd = other.SkarnLastEnd;
            LastChallengeEndHours = other.LastChallengeEndHours; HasChallengeEnd = other.HasChallengeEnd;
            PaidRefreshCount = other.PaidRefreshCount; PaidRefreshNonce = other.PaidRefreshNonce;
        }

        // std::vector copy assignment: the target keeps its identity, its contents become a copy of the source's (a new List per bout)
        static void CopyHistory(List<List<string>> from, List<List<string>> to)
        {
            to.Clear();
            foreach (var bout in from) to.Add(new List<string>(bout));
        }
    }

    // C++: TownChallengePolicy.h:137-142
    public static bool ChallengeCooldownActive(Card card, in Offer offer, double now, int cooldownHours)
    {
        return offer.Encounter == (int)Encounter.Skarn && cooldownHours > 0 && card.SkarnAttempted &&
            now >= card.SkarnLastEnd &&
            now - card.SkarnLastEnd < cooldownHours;
    }

    // C++: TownChallengePolicy.h:143-148
    public static void RecordSkarnChallengeEnd(Card card, in Offer offer, double now)
    {
        if (offer.Encounter != (int)Encounter.Skarn || !(now >= 0.0) || now > 24000000.0) return;
        card.SkarnAttempted = true;
        card.SkarnLastEnd = now;
    }

    /// <summary>Slot 5 is the Skarn offer; 0-4 the card's offers. Another slot is undefined in the C++ (an out-of-bounds index) and
    /// throws here.</summary>
    // C++: TownChallengePolicy.h:149
    public static ref Offer At(Card card, int slot) { if (slot == 5) return ref card.SkarnOffer; return ref card.Offers[slot]; }

    // C++: TownChallengePolicy.h:150-155
    public static void RecordVictory(Card card, bool challenge, bool won, uint defeated)
    {
        if (!won) return;
        card.UniqueWins |= defeated & AllUniqueVictoriesBits;
        if (challenge && card.ChallengeWins < 1000000000u) ++card.ChallengeWins;
    }

    // C++: TownChallengePolicy.h:156-158
    public static bool SkarnUnlocked(Card card)
    {
        return (card.UniqueWins & AllUniqueVictoriesBits) == AllUniqueVictoriesBits;
    }

    // C++: TownChallengePolicy.h:159-163
    public static uint Draw(Card card)
    {
        return Hashing.LcgNext(ref card.Seed);
    }

    // C++: TownChallengePolicy.h:209-216
    public static string Role(int encounter)
    {
        string[] ids = { "96-Proving Grounds.mod", "97-Proving Grounds.mod",
            "98-Proving Grounds.mod", "169-Proving Grounds.mod", "168-Proving Grounds.mod",
            "167-Proving Grounds.mod", "616-Proving Grounds.mod", "619-Proving Grounds.mod",
            "623-Proving Grounds.mod" };
        return encounter >= 0 && encounter <= (int)Encounter.Veyr ? ids[encounter] : "";
    }

    // C++: TownChallengePolicy.h:217-221
    public static string Rarity(int encounter)
    {
        return encounter == 0 ? "Common" : encounter == 1 ? "Uncommon" :
            encounter == 2 ? "Rare" : "Legendary";
    }

    // C++: TownChallengePolicy.h:222-227
    public static string Title(int encounter)
    {
        string[] names = { "Sabre fighter", "Katana fighter", "Champion", "Senn", "Torka", "Skarn",
            "Ressa Vane", "Bum-Per", "Veyr the Gilded" };
        return encounter >= 0 && encounter <= (int)Encounter.Veyr ? names[encounter] : "Unavailable";
    }

    // C++: TownChallengePolicy.h:319-325
    public static void ClearPreview(ref Offer o)
    {
        o.Context = ""; o.PlayerCount = 0; o.EnemyCount = 0;
        for (int i = 0; i < 4; ++i)
        {
            OpponentId(ref o, i) = ""; OpponentName(ref o, i) = ""; o.Roles[i] = "";
        }
    }

    // C++: TownChallengePolicy.h:326-350
    public static Offer PreviewSource(Card card, int slot)
    {
        if (slot < 0 || slot > 5) return new Offer();
        Offer source = slot == 5 ? card.SkarnOffer : card.Offers[slot];
        // A completed lineup was balanced for its original division. Keep the
        // saved source for that view, but never advertise its actors elsewhere.
        if (slot < 5 && source.Generated && source.State == OfferState.Consumed &&
            source.Division != card.Division)
        {
            ClearPreview(ref source);
            source.Division = card.Division;
        }
        if (slot < 5 && !source.Generated && NamedDivision(source.Encounter) >= 0 &&
            !NamedForDivision(source.Encounter, card.Division))
        {
            ClearPreview(ref source);
            source.Generated = true;
            source.Division = card.Division;
            // Derive the counterpart without advancing or changing the saved card.
            uint seed = unchecked(card.Seed ^ (0x9e3779b9u * (uint)(slot + 1)));
            seed = Hashing.ChallengeMix(seed);   // seed ^= seed >> 16; seed *= 0x85ebca6bu; seed ^= seed >> 13;
            source.Draw = seed % 10000;
        }
        return source;
    }

    // C++: TownChallengePolicy.h:351-356
    public static int OfferRarity(in Offer o, int division)
    {
        if (!o.Generated && o.Encounter < 3) return o.Encounter;
        if (!o.Generated && division == 2) return 3;
        return o.Draw < 4000 ? 0 : o.Draw < 6500 ? 1 : o.Draw < 8500 ? 2 : 3;
    }

    // C++: TownChallengePolicy.h:357-359
    public static string RarityName(int rarity)
    {
        return rarity == 0 ? "Common" : rarity == 1 ? "Uncommon" : rarity == 2 ? "Rare" : "Legendary";
    }

    // C++: TownChallengePolicy.h:360-362
    public static string DivisionName(int division)
    {
        return division == 0 ? "Easy" : division == 1 ? "Medium" : "Hard";
    }

    // C++: TownChallengePolicy.h:363-377
    public static int LegendaryEncounter(int division, uint namedRoll, uint repeatRoll,
        uint regularRoll, uint uniqueWins, bool skarnUnlocked, bool skarnWon)
    {
        namedRoll %= 8;
        if (namedRoll >= 4) return (int)(namedRoll % 3);
        int encounter = division == 0 ? (int)Encounter.RessaVane :
            division == 1 ? (int)Encounter.BumPer :
            namedRoll == 0 ? (int)Encounter.Senn :
            namedRoll == 1 ? (int)Encounter.Torka :
            namedRoll == 2 || !skarnUnlocked ? (int)Encounter.Veyr : (int)Encounter.Skarn;
        uint victory = UniqueVictoryForEncounter(encounter);
        bool defeated = encounter == (int)Encounter.Skarn ? skarnWon :
            victory != 0 && (uniqueWins & victory) != 0;
        return defeated && (repeatRoll & 1u) != 0 ? (int)(regularRoll % 3) : encounter;
    }

    // C++: TownChallengePolicy.h:378-381
    public static bool UniqueEncounter(int encounter)
    {
        return UniqueVictoryForEncounter(encounter) != 0;
    }

    /// <summary>The C++ takes <c>const Offer (&amp;offers)[5]</c>: pass a card's <see cref="Card.Offers"/> (exactly 5 elements).</summary>
    // C++: TownChallengePolicy.h:382-390
    public static int UniqueEncountersInOffers(Offer[] offers, int division, int exceptSlot)
    {
        int count = 0;
        for (int i = 0; i < 5; ++i)
            if (i != exceptSlot && offers[i].Generated &&
                OfferRarity(offers[i], division) == 3 && UniqueEncounter(offers[i].Encounter))
                ++count;
        return count;
    }

    // C++: TownChallengePolicy.h:391-395
    public static int LegendaryEncounter(int division, uint namedRoll, bool skarnUnlocked)
    {
        return LegendaryEncounter(division, namedRoll, 0, namedRoll, 0,
            skarnUnlocked, false);
    }

    // C++: TownChallengePolicy.h:396-406
    public static int EncounterForDivision(in Offer offer, int slot, int division,
        uint uniqueWins, bool skarnUnlocked, bool skarnWon)
    {
        int rarity = OfferRarity(offer, division);
        if (rarity < 3) return rarity;
        uint seed = unchecked(offer.Draw ^ ((uint)offer.Epoch * 1664525u) ^
            ((uint)(slot + 1) * 1013904223u));
        seed = Hashing.ChallengeMix(seed);   // seed ^= seed >> 16; seed *= 0x85ebca6bu; seed ^= seed >> 13;
        return LegendaryEncounter(division, seed % 8, seed >> 16, seed >> 8,
            uniqueWins, skarnUnlocked, skarnWon);
    }

    // C++: TownChallengePolicy.h:409-419
    public static string UnavailableReason(in Offer offer, bool playersReady,
        bool anchorReady, bool fitsWithoutMedics)
    {
        bool named = OfferRarity(offer, offer.Division) == 3 &&
            NamedForDivision(offer.Encounter, offer.Division);
        string prefix = named ? Title(offer.Encounter) + " | " : "";
        if (!playersReady) return prefix + "Select 1-3 recovered fighters";
        if (named && !anchorReady) return prefix + "Named opponent unavailable or recovering";
        if (fitsWithoutMedics) return prefix + "Waiting for medical capacity";
        if (named) return prefix + "Out of range: no matching team in " + DivisionName(offer.Division);
        return "No suitable recovered opponents for this team";
    }

    // C++: TownChallengePolicy.h:420-422
    public static bool Visible(Card card, int slot)
    {
        return slot >= 0 && slot < 5;
    }

    // C++: TownChallengePolicy.h:423-434
    public static bool CanBookDivision(Card card, int slot)
    {
        if (!Visible(card, slot)) return false;
        ref readonly Offer o = ref (slot == 5 ? ref card.SkarnOffer : ref card.Offers[slot]);
        if (o.Encounter == (int)Encounter.Skarn && !SkarnUnlocked(card)) return false;
        if (!o.Generated) return NamedDivision(o.Encounter) < 0 || NamedForDivision(o.Encounter, card.Division);
        if (o.Division != card.Division) return false;
        for (int i = 0; i < Opponents(o); ++i)
        {
            int namedDivision = NamedRoleDivision(o.Roles[i]);
            if (namedDivision >= 0 && namedDivision != card.Division) return false;
        }
        return true;
    }

    // C++: TownChallengePolicy.h:435-450
    public static bool SetDivision(Card card, int division, bool locked)
    {
        if (locked || division < 0 || division > 2) return false;
        if (card.Division == division) return true;
        card.Division = division;
        for (int i = 0; i < 5; ++i)
            if (card.Offers[i].Generated && card.Offers[i].State == OfferState.Available)
            {
                ClearPreview(ref card.Offers[i]);
                card.Offers[i].Division = division;
                card.Offers[i].Encounter = EncounterForDivision(card.Offers[i], i, division,
                    card.UniqueWins, SkarnUnlocked(card), card.SkarnWon);
                if (OfferRarity(card.Offers[i], division) == 3 && UniqueEncounter(card.Offers[i].Encounter) &&
                    UniqueEncountersInOffers(card.Offers, division, i) > 0)
                    card.Offers[i].Encounter = (int)Encounter.Champion;
            }
        return true;
    }

    // C++: TownChallengePolicy.h:451-475
    public static void RefreshGenerated(Card card, int epoch, int pinned)
    {
        if (epoch < -1 || epoch > 1999999) return;
        int lastRefreshed = -1;
        for (int i = 0; i < 5; ++i)
        {
            ref Offer o = ref card.Offers[i];
            if (i == pinned || o.Epoch == epoch) continue;
            o = new Offer(); o.Epoch = epoch; o.Generated = true;
            o.Draw = Draw(card) % 10000;
            o.Mode = (int)((Draw(card) >> 8) % 2);
            o.Division = card.Division;
            o.Encounter = EncounterForDivision(o, i, o.Division,
                card.UniqueWins, SkarnUnlocked(card), card.SkarnWon);
            if (OfferRarity(o, o.Division) == 3 && UniqueEncounter(o.Encounter) &&
                UniqueEncountersInOffers(card.Offers, o.Division, i) > 0)
                o.Encounter = (int)Encounter.Champion;
            lastRefreshed = i;
        }
        bool hasTeams = false, hasTeams1v1 = false;
        for (int i = 0; i < 5; ++i)
        {
            hasTeams = hasTeams || card.Offers[i].Mode == (int)ChallengeMode.Teams;
            hasTeams1v1 = hasTeams1v1 || card.Offers[i].Mode == (int)ChallengeMode.Teams1v1;
        }
        if (lastRefreshed >= 0 && (!hasTeams || !hasTeams1v1))
            card.Offers[lastRefreshed].Mode = hasTeams ? (int)ChallengeMode.Teams1v1 : (int)ChallengeMode.Teams;
    }
}
