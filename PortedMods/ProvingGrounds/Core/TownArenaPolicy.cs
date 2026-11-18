namespace ProvingGrounds.Core;

// Pure decisions only. Runtime adapters own persistence, health assessment and NPC control.
/// <summary>C++: TownArenaPolicy.h (header-only, namespace <c>TownArenaPolicy</c>).
///
/// Not ported (legacy save support dropped, owner decision D7: no old-save import): <c>enum Milestone</c>, <c>enum SkarnEncounter</c>,
/// <c>AddMilestones</c>, <c>SkarnUnlocked(unsigned)</c>, <c>SkarnFormat</c> and <c>CanBookSkarn</c> (TownArenaPolicy.h:53-67). None has a
/// caller in the shipped plugin (TW §2.5 "Legacy offers"; checked by grep over plugin/src/*.cpp and *.h: the only <c>SkarnUnlocked</c>
/// calls are the <c>TownChallengePolicy::SkarnUnlocked(const Card&amp;)</c> overload, and <c>CanBookSkarn</c> is the only caller of
/// this file's one).</summary>
public static class TownArenaPolicy
{
    // C++: TownArenaPolicy.h:7
    public const int OffersPerCard = 5;

    // C++: TownArenaPolicy.h:8
    public enum EventKind { None, Player, Npc }

    // Signed epoch: initial night is -1; invalid clock is -2.
    // C++: TownArenaPolicy.h:10-13
    public static int CardEpoch(double hours)
    {
        if (!(hours >= 0.0) || hours > 24000000.0) return -2;
        return (int)Math.Floor((hours - 6.0) / 12.0);
    }

    // C++: TownArenaPolicy.h:14-17
    public static int ChallengeCardEpoch(double hours, int refreshHours)
    {
        if (!(hours >= 0.0) || hours > 24000000.0 || refreshHours < 1 || refreshHours > 168) return -2;
        return (int)Math.Floor((hours - 6.0) / refreshHours);
    }

    // C++: TownArenaPolicy.h:18-22
    public static double HoursUntilChallengeRefresh(double hours, int refreshHours)
    {
        if (!(hours >= 0.0) || hours > 24000000.0 || refreshHours < 1 || refreshHours > 168) return -1.0;
        double epoch = Math.Floor((hours - 6.0) / refreshHours);
        return 6.0 + (epoch + 1.0) * refreshHours - hours;
    }

    // C++: TownArenaPolicy.h:23-30
    public static int PaidRefreshCost(int baseCats, double multiplier, int paidRefreshCount)
    {
        if (baseCats < 1 || !(multiplier >= 1.0) || !(multiplier <= 10.0) || paidRefreshCount < 0)
            return 0;
        double cost = baseCats * Math.Pow(multiplier, (double)paidRefreshCount);
        if (!(cost < (double)int.MaxValue)) return int.MaxValue;
        double rounded = CMath.RoundHalfUp(cost);
        return rounded < (double)int.MaxValue ? (int)rounded : int.MaxValue;
    }

    // C++: TownArenaPolicy.h:31-35
    public static EventKind SelectNext(bool busy, bool playerPending, bool npcReady)
    {
        if (busy) return EventKind.None;
        if (playerPending) return EventKind.Player;
        return npcReady ? EventKind.Npc : EventKind.None;
    }

    // C++: TownArenaPolicy.h:36-42
    public static EventKind AmbientNext(bool paused, bool registryReady, bool busy,
        bool playerPending, double now, double due)
    {
        if (!(now >= 0.0) || now > double.MaxValue || !(due >= 0.0) || due > double.MaxValue)
            return EventKind.None;
        return SelectNext(busy, playerPending,
            !paused && registryReady && now >= due);
    }

    // C++: TownArenaPolicy.h:43-46
    public static bool ShouldDiscoverRegistry(bool paused, bool registryReady,
        bool playerBookingPending)
    {
        return !paused && !registryReady && !playerBookingPending;
    }

    // C++: TownArenaPolicy.h:47-52
    public static int SelectNpcTeamSize(int recoveredFighters, uint roll)
    {
        int maximum = recoveredFighters / 2;
        if (maximum < 1) return 0;
        int capped = maximum > 4 ? 4 : maximum;
        return 1 + (int)(roll % (uint)capped);
    }

    // C++: TownArenaPolicy.h:69
    public enum OfferState { Available, Booked, Consumed }

    // C++: TownArenaPolicy.h:70-74
    public static bool BookOffer(ref OfferState state)
    {
        if (state != OfferState.Available) return false;
        state = OfferState.Booked;
        return true;
    }

    // C++: TownArenaPolicy.h:75-79
    public static bool BeginOffer(ref OfferState state)
    {
        if (state != OfferState.Booked) return false;
        state = OfferState.Consumed;
        return true;
    }

    // C++: TownArenaPolicy.h:80-82
    public static void CancelBooking(ref OfferState state)
    {
        if (state == OfferState.Booked) state = OfferState.Available;
    }
}
