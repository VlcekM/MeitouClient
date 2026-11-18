// Port of upstream/ProvingGrounds/plugin/src/TownChallengeBuyInPolicy.h (Proving Grounds by Shutori, GPLv3).
namespace ProvingGrounds.Core;

/// <summary>C++: <c>namespace TownChallengeBuyInPolicy</c> (TownChallengeBuyInPolicy.h:1-56; TW §2.5). Pure: the wallet and the booking
/// state are plain structs the glue owns; the base buy-in per division is read live from <see cref="PGConfig.ChallengeBase"/>.</summary>
public static class TownChallengeBuyInPolicy
{
    /// <summary>C++: TownChallengeBuyInPolicy.h:6-10. Build with <c>new Quote()</c> (all zero, as the C++ constructor).</summary>
    public struct Quote
    {
        public int BuyIn, Payout;

        // C++: TownChallengeBuyInPolicy.h:9
        public readonly bool Valid() { return BuyIn >= 100 && BuyIn <= 5000 && Payout >= BuyIn && Payout <= 20000; }
    }

    /// <summary>C++: TownChallengeBuyInPolicy.h:11-14.</summary>
    public struct Account
    {
        public int Credit, Escrow;
    }

    /// <summary>C++: TownChallengeBuyInPolicy.h:15-19.</summary>
    public struct Ticket
    {
        public Quote Quote;
        public bool Pending;
    }

    // The base buy-in for the division, in Cats, is a configured price (shipped
    // 500 / 1000 / 2500); the rest of the quote is the model around it -- the
    // 0.5-2.0 scale on the power ratio, and the 1.1-5.0 odds band. Valid() bounds
    // what the wallet will accept, so a base outside 200-2500 prices challenges
    // the scale cannot keep inside those bounds.
    // C++: TownChallengeBuyInPolicy.h:25-37
    public static Quote Price(int division, double own, double enemy)
    {
        Quote result = new Quote();
        if (division < 0 || division > 2 || !TownMatchmakingPolicy.Finite(own) ||
            !TownMatchmakingPolicy.Finite(enemy) || own <= 0 || enemy <= 0) return result;
        ReadOnlySpan<double> target = stackalloc double[] { .70, 1.00, 1.30 };
        double ratio = enemy / own;
        double scale = Math.Max(.5, Math.Min(2.0, ratio / target[division]));
        int @base = PGConfig.ChallengeBase(division);
        result.BuyIn = (int)CMath.RoundHalfUp(@base * scale / 10.0) * 10;
        double odds = Math.Max(1.1, Math.Min(5.0, .9 * (1.0 + ratio)));
        result.Payout = (int)Math.Floor(result.BuyIn * odds + 1e-8);
        return result;
    }

    // C++: TownChallengeBuyInPolicy.h:38-40
    public static bool CanAccept(in Account a, in Ticket t, in Quote q, int balance)
    {
        return !t.Pending && a.Credit == 0 && a.Escrow == 0 && q.Valid() && balance >= q.BuyIn;
    }

    // C++: TownChallengeBuyInPolicy.h:41-45
    public static bool Accept(ref Account a, ref Ticket t, in Quote q, int balance)
    {
        if (!CanAccept(a, t, q, balance)) return false;
        t.Quote = q; t.Pending = true; a.Escrow = q.BuyIn;
        return true;
    }

    // Outcome: 1 = player victory, 0 = loss, -1 = cancellation/draw.
    // C++: TownChallengeBuyInPolicy.h:47-52
    public static int Settle(ref Account a, ref Ticket t, int outcome)
    {
        if (!t.Pending) return 0;
        int credit = outcome < 0 ? t.Quote.BuyIn : outcome == 1 ? t.Quote.Payout : 0;
        t.Pending = false; a.Escrow = 0; a.Credit += credit;
        return credit;
    }

    // Bookings do not resume on load. Store their deducted stake as a refund
    // alongside deferred winnings, without changing the running session.
    // C++: TownChallengeBuyInPolicy.h:55
    public static int SaveCredit(in Account a) { return a.Credit + a.Escrow; }
}
