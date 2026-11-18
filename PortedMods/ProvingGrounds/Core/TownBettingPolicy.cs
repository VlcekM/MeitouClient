// Port of upstream/ProvingGrounds/plugin/src/TownBettingPolicy.h (Proving Grounds by Shutori, GPLv3).
namespace ProvingGrounds.Core;

/// <summary>C++: <c>namespace TownBettingPolicy</c> (TownBettingPolicy.h:1-116; TW §2.6). Pure: the bookie range is read live from
/// <see cref="PGConfig.BookieValues"/>, the pricing levers live from <see cref="BalanceTuning.Get"/>.</summary>
public static class TownBettingPolicy
{
    /// <summary>C++: TownBettingPolicy.h:6-11. The C++ constructor sets side -1; build with <c>new Ticket()</c> (a <c>default</c> would
    /// leave side 0).</summary>
    public struct Ticket
    {
        public int Side, Stake, Payout;
        public bool Pending;

        public Ticket() { Side = -1; Stake = 0; Payout = 0; Pending = false; }
    }

    /// <summary>C++: TownBettingPolicy.h:12 (<c>ResultNone</c> .. <c>ResultRefunded</c>, values 0..3).</summary>
    public enum ResultOutcome { None, Won, Lost, Refunded }

    /// <summary>C++: TownBettingPolicy.h:13-19. Build with <c>new Result()</c> (outcome None, sides -1, rest 0).</summary>
    public struct Result
    {
        public ResultOutcome Outcome;
        public int WinningSide, SelectedSide, Stake, Returned, Net;

        public Result() { Outcome = ResultOutcome.None; WinningSide = -1; SelectedSide = -1; Stake = 0; Returned = 0; Net = 0; }
    }

    // C++: TownBettingPolicy.h:20-37
    public static Result Resolve(in Ticket ticket, int winningSide)
    {
        Result result = new Result();
        if (!ticket.Pending) return result;
        result.WinningSide = winningSide;
        result.SelectedSide = ticket.Side;
        result.Stake = ticket.Stake;
        if (winningSide < 0)
        {
            result.Outcome = ResultOutcome.Refunded;
            result.Returned = ticket.Stake;
        }
        else if (winningSide == ticket.Side)
        {
            result.Outcome = ResultOutcome.Won;
            result.Returned = ticket.Payout;
        }
        else
        {
            result.Outcome = ResultOutcome.Lost;
        }
        result.Net = result.Returned - result.Stake;
        return result;
    }

    // Wagers are held between the configured Cats range, in whole stake steps;
    // the shipped 10 to 10,000 is what pg_config.json's "bookie" section edits.
    // C++: TownBettingPolicy.h:40-48
    public static int AdjustStake(int current, int delta)
    {
        PGConfig.Bookie bookie = PGConfig.BookieValues();
        if (current < bookie.Minimum) current = bookie.Minimum;
        if (current > bookie.Maximum) current = bookie.Maximum;
        int adjusted = current + delta;
        if (adjusted < bookie.Minimum) return bookie.Minimum;
        if (adjusted > bookie.Maximum) return bookie.Maximum;
        return adjusted;
    }

    // Raw model probability. This is the value the combat log records as
    // predicted_p_a and what the diagnostic fields compare, so it must stay the
    // untransformed model even when the pricing below is tuned -- otherwise we
    // lose the ability to measure the model against its own capture.
    // C++: TownBettingPolicy.h:53-58
    public static double WinProbability(double ownAbility, double otherAbility)
    {
        if (!(ownAbility > 0) || !(otherAbility > 0) || !TownMatchmakingPolicy.Finite(ownAbility) || !TownMatchmakingPolicy.Finite(otherAbility))
            return 0;
        double ratio = otherAbility / ownAbility;
        return 1.0 / (1.0 + ratio * ratio);
    }

    // Priced probability -- what the book actually pays against. Shipped this is
    // identical to WinProbability (knee 0, exponent 2). The transform is
    // p = r^k / (1 + r^k) on r = max(score)/min(score), with k = 2 below
    // pricingKnee and k = pricingExponent above it.
    //
    // It is computed from the UNORDERED pair, never from own/other: with a knee
    // the map is not symmetric, so pricing by own/other would apply the wide-ratio
    // exponent to the favourite and leave the underdog at the shipped price,
    // pricing the two sides of one market inconsistently.
    // Port note: the upstream "shipped = WinProbability" is stale; BalanceTuning ships knee 1.10 / exponent 6.65 (BalanceTuning.cpp:18-19), so only
    // ratios <= 1.10 take the square (no pow), and even those skip the WinProbability shortcut.
    // C++: TownBettingPolicy.h:68-87
    public static double PricedProbability(double ownAbility, double otherAbility)
    {
        if (!(ownAbility > 0) || !(otherAbility > 0) || !TownMatchmakingPolicy.Finite(ownAbility) || !TownMatchmakingPolicy.Finite(otherAbility))
            return 0;
        BalanceTuning.Values tuning = BalanceTuning.Get();
        // Shipped settings: take the original path, bit-for-bit, so a capture on
        // defaults is byte-identical to the shipped build.
        if (!(tuning.PricingKnee > 0) && tuning.PricingExponent == 2.0)
            return WinProbability(ownAbility, otherAbility);
        bool ownIsHigher = ownAbility >= otherAbility;
        double high = ownIsHigher ? ownAbility : otherAbility;
        double low = ownIsHigher ? otherAbility : ownAbility;
        double ratio = high / low;
        double exponent = (tuning.PricingKnee > 0 && ratio <= tuning.PricingKnee)
            ? 2.0 : tuning.PricingExponent;
        double powered = exponent == 2.0 ? ratio * ratio : Math.Pow(ratio, exponent);
        double favourite = powered / (1.0 + powered);
        if (!(favourite > 0) || !(favourite < 1) || !TownMatchmakingPolicy.Finite(favourite))
            return 0;
        return ownIsHigher ? favourite : 1.0 - favourite;
    }

    // C++: TownBettingPolicy.h:88-99. stake * odds is at most 10000 * 5.0 with the shipped bookie range; a configured maximum above
    // about 429 million Cats would overflow the C++ int cast (undefined there; .NET saturates). Unreachable through config: ReadBookie caps the
    // maximum at MaxEditableCost (1,000,000), times 5.0 is 5,000,000.
    public static int ReturnCats(int stake, double ownAbility, double otherAbility)
    {
        PGConfig.Bookie bookie = PGConfig.BookieValues();
        if (stake < bookie.Minimum || stake > bookie.Maximum ||
            stake % PGConfig.StakeStep != 0 || !(ownAbility > 0) || !(otherAbility > 0) ||
            !TownMatchmakingPolicy.Finite(ownAbility) || !TownMatchmakingPolicy.Finite(otherAbility)) return 0;
        double probability = PricedProbability(ownAbility, otherAbility);
        if (!(probability > 0)) return 0;
        double odds = .9 / probability;
        if (odds < 1.1) odds = 1.1;
        if (odds > 5.0) odds = 5.0;
        return (int)(stake * odds);
    }

    /// <summary>A null array is the C++ null pointer; the array must hold at least <paramref name="count"/> values.</summary>
    // C++: TownBettingPolicy.h:100-104
    public static double TeamAbility(double[]? members, int count)
    {
        if (members == null || count <= 0 || count > 4) return 0;
        double total = TownMatchmakingPolicy.TeamScore(members, count);
        return total > 0 && TownMatchmakingPolicy.Finite(total) ? total : 0;
    }

    // C++: TownBettingPolicy.h:105-110
    public static bool CanAccept(in Ticket ticket, bool npcOnly, bool beforeCombat, int side, int stake, int balance)
    {
        PGConfig.Bookie bookie = PGConfig.BookieValues();
        return !ticket.Pending && npcOnly && beforeCombat && side >= 0 && side <= 1 &&
            stake >= bookie.Minimum && stake <= bookie.Maximum &&
            stake % PGConfig.StakeStep == 0 && balance >= stake;
    }

    // Call Resolve first: Settle clears Pending, and Resolve of a non-pending ticket returns nothing.
    // C++: TownBettingPolicy.h:111-115
    public static int Settle(ref Ticket ticket, int winningSide)
    {
        if (!ticket.Pending) return 0;
        ticket.Pending = false;
        return winningSide < 0 ? ticket.Stake : winningSide == ticket.Side ? ticket.Payout : 0;
    }
}
