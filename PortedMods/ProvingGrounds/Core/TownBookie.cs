// Pure parts of upstream/ProvingGrounds/plugin/src/TownBookie.cpp (Proving Grounds by Shutori, GPLv3).
using System.Globalization;

namespace ProvingGrounds.Core;

/// <summary>The bookie window's text and button states (TownBookie.cpp; TW §2.6). The C++ keeps the bout, ticket and messages in module
/// globals and reads the wallet, the save, the bookie's distance and the planned bout itself; here the glue keeps those and passes them
/// (<see cref="ViewInputs"/>). Odds and stake rules are <see cref="TownBettingPolicy"/> (task 1.9b) and <see cref="PGConfig.ClampStake"/>,
/// called, not re-ported; fighter stats are <see cref="ArenaCombatProfile.StatText"/>.</summary>
public static class TownBookie
{
    /// <summary>C++: TownBookieUI.h:8-11 (copied by value) without its <c>hand actor</c>, which the glue keeps beside it.</summary>
    public struct FighterView
    {
        public string Name, Style, Record, Stats;

        public FighterView() { Name = ""; Style = ""; Record = ""; Stats = ""; }
    }

    /// <summary>C++: TownBookieUI.h:12-19; the constructor sets the C++ constructor's values (selected side -1, stake 100, every flag false).</summary>
    public sealed class View
    {
        public List<FighterView>[] Fighters = { new List<FighterView>(), new List<FighterView>() };
        public string Header = "", Timer = "", Status = "", Slip = "";
        public string[] Odds = { "", "" };
        public int SelectedSide, Stake;
        public bool CanSelect, CanStake, CanBet, CanArrange;

        // C++: TownBookieUI.h:17-18
        public View()
        {
            SelectedSide = -1; Stake = 100; CanSelect = false; CanStake = false;
            CanBet = false; CanArrange = false;
        }
    }

    /// <summary>C++: TownBookieResultUI.h:8-14; the constructor sets the C++ constructor's values (both sides -1, tone 0).</summary>
    public sealed class ResultView
    {
        public List<FighterView>[] Fighters = { new List<FighterView>(), new List<FighterView>() };
        public string Title = "", Context = "", Payout = "";
        public int WinningSide, SelectedSide, Tone;

        // C++: TownBookieResultUI.h:13
        public ResultView() { WinningSide = -1; SelectedSide = -1; Tone = 0; }
    }

    /// <summary>The fighter card. <paramref name="characterValid"/> = <c>character &amp;&amp; isValid()</c>, <paramref name="characterName"/>
    /// = <c>getName()</c> (read only when valid); <paramref name="profile"/> = <c>ArenaCombatProfile::Read(character)</c>;
    /// <paramref name="identity"/> = <c>TownMatchmakingRuntime::Identity(character)</c>; <paramref name="standings"/> =
    /// <c>LeaderboardStore::GetStandings(Town)</c>. The record is the first standing with that id and at least one match.</summary>
    // C++: TownBookie.cpp:113-130
    public static FighterView Capture(bool characterValid, string? characterName, in ArenaCombatProfile.Profile profile,
        string? identity, IReadOnlyList<LeaderboardData.Standing>? standings)
    {
        FighterView result = new FighterView();
        result.Name = characterValid ? characterName ?? "" : "Unavailable fighter";
        result.Style = profile.Style ?? "";
        result.Stats = ArenaCombatProfile.StatText(profile);
        result.Record = "Unrated | 0 W / 0 L";
        string id = identity ?? "";
        int count = standings?.Count ?? 0;
        for (int i = 0; id.Length != 0 && i < count; ++i)
        {
            LeaderboardData.Standing s = standings![i];
            if (s.Id != id || s.Matches < 1) continue;
            // "MMR %.0f | %d W / %d L" (the float mmr promoted to double)
            result.Record = "MMR " + CMath.FormatF(s.Mmr, 0) + " | " + I(s.Wins) + " W / " + I(s.Losses) + " L";
            break;
        }
        return result;
    }

    /// <summary>What <c>MakeView</c> reads: the TownBookie.cpp globals (<c>selectedSide</c>, <c>market</c>, <c>fighting</c>,
    /// <c>completed</c>, <c>resultScheduled</c>, <c>resultReady</c>, <c>stake</c>, <c>ticket</c>, <c>teamCounts</c>, <c>ability</c>,
    /// <c>fighterViews</c>, <c>actionMessage</c>, <c>message</c>) and the engine reads.</summary>
    public sealed class ViewInputs
    {
        /// <summary><c>Wallet()</c> is not null, and its <c>getMoney()</c> (money is 0 without a wallet).</summary>
        public bool HasWallet;
        public int Money;
        public int SelectedSide = -1;
        public bool Market, Fighting, Completed, ResultScheduled, ResultReady;
        /// <summary><c>Eligible()</c>: both teams non-empty and every fighter valid and not the player's.</summary>
        public bool Eligible;
        /// <summary><c>AtBookie()</c>.</summary>
        public bool Nearby;
        /// <summary>Team A's first fighter and the bookie are both valid and in the same town.</summary>
        public bool SameTown;
        /// <summary><c>LeaderboardStore::HasActiveSave()</c> and <c>GetBookieCredit()</c>.</summary>
        public bool HasActiveSave;
        public int BookieCredit;
        /// <summary>The module's <c>stake</c>; <c>MakeView</c> clamps it and the glue keeps <see cref="MakeView"/>'s <c>stake</c> out value.</summary>
        public int Stake = 100;
        public TownBettingPolicy.Ticket Ticket = new TownBettingPolicy.Ticket();
        /// <summary>Length 2 (team A, team B), like the three arrays below.</summary>
        public int[] TeamCounts = { 0, 0 };
        /// <summary>Length 2: the module's <c>ability[2]</c>.</summary>
        public double[] Ability = { 1, 1 };
        /// <summary>Length 2: the module's <c>fighterViews[2]</c>.</summary>
        public IReadOnlyList<FighterView>[] FighterViews = { new List<FighterView>(), new List<FighterView>() };
        /// <summary><c>TownArena::HasPlannedNpcBout() &amp;&amp; ou</c>, its <c>GetPlannedNpcStartHours()</c> and the game's total hours.</summary>
        public bool PlannedBout;
        public double PlannedStartHours, NowHours;
        public string ActionMessage = "";
        public string Message = "NPC bouts only. Arrange a bout to open betting.";
    }

    static string I(int value) => value.ToString(CultureInfo.InvariantCulture);
    // printf "%02d": zero-padded to two characters; a negative number already has two (the sign is counted)
    static string D2(int value) => value >= 0 && value < 10 ? "0" + I(value) : I(value);

    /// <summary>The betting window. <paramref name="stake"/> is the module's stake after <c>stake = PGConfig::ClampStake(stake)</c>
    /// (TownBookie.cpp:146), which the glue writes back.</summary>
    // C++: TownBookie.cpp:131-198
    public static View MakeView(ViewInputs g, out int stake)
    {
        var view = new View();
        int money = g.HasWallet ? g.Money : 0;
        int selectedSide = g.SelectedSide;
        bool market = g.Market, fighting = g.Fighting, completed = g.Completed;
        TownBettingPolicy.Ticket ticket = g.Ticket;
        int[] teamCounts = g.TeamCounts;
        double[] ability = g.Ability;
        bool chosen = selectedSide == 0 || selectedSide == 1;
        bool valid = market && g.Eligible;
        bool resultBlocking = g.ResultScheduled || g.ResultReady;
        bool nearby = g.Nearby;
        bool sameTown = g.SameTown;
        bool ready = g.HasActiveSave && nearby && sameTown;
        // A configured stake ceiling can be lower than the stake the window
        // opened with, and the bet button is gated on this exact value.
        stake = PGConfig.ClampStake(g.Stake);
        view.SelectedSide = selectedSide;
        view.Stake = ticket.Stake > 0 ? ticket.Stake : stake;
        view.CanSelect = valid && !fighting && !ticket.Pending && !completed && !resultBlocking;
        view.CanStake = view.CanSelect;
        view.CanBet = !resultBlocking && ready && g.BookieCredit == 0 &&
            TownBettingPolicy.CanAccept(ticket, valid && !completed, !fighting,
                selectedSide, stake, money);
        view.CanArrange = !resultBlocking && nearby && g.HasActiveSave && !market && !fighting && !ticket.Pending;
        string state = fighting ? "COMBAT - BETS CLOSED" : market ? "BETTING OPEN" : completed ? "BOUT FINISHED" : "NO BOUT OPEN";
        view.Header = state + " | " + I(money) + " Cats | NPC bout " + I(teamCounts[0]) + "v" + I(teamCounts[1]);
        if (g.PlannedBout)
        {
            double start = g.PlannedStartHours;
            double now = g.NowHours;
            // static_cast<int>(ceil(..)): the glue's hours are finite (a NaN or out-of-range cast is undefined in C++)
            int minuteOfDay = (int)Math.Ceiling(start * 60.0) % (24 * 60);
            double late = start - now;
            // (std::max)(0.0, x) = (0.0 < x) ? x : 0.0 (a NaN gives 0.0, unlike Math.Max)
            int remaining = (int)Math.Ceiling((0.0 < late ? late : 0.0) * 60.0);
            if (remaining > 0)
                view.Timer = "NEXT BOUT  " + D2(minuteOfDay / 60) + ":" + D2(minuteOfDay % 60) + "  |  STARTS IN " +
                    I(remaining / 60) + "h " + D2(remaining % 60) + "m";
            else
                view.Timer = "NEXT BOUT  " + D2(minuteOfDay / 60) + ":" + D2(minuteOfDay % 60) +
                    "  |  START TIME REACHED - WAITING FOR ARENA / MEDICS";
        }
        for (int side = 0; side < 2; ++side)
        {
            view.Fighters[side] = new List<FighterView>(g.FighterViews[side]);
            int payout = TownBettingPolicy.ReturnCats(1000, ability[side], ability[1 - side]);
            if (teamCounts[side] > 0 && payout > 0)
                view.Odds[side] = CMath.FormatF(payout / 1000.0, 2) + "x return";
            else view.Odds[side] = "Awaiting fighters";
        }
        view.Status = g.ActionMessage.Length == 0 ? g.Message : g.ActionMessage;
        if (ticket.Stake > 0 && ticket.Side >= 0 && ticket.Side <= 1)
        {
            view.Slip = (ticket.Pending ? "ACCEPTED" : "SETTLED") + ": Team " + (ticket.Side == 0 ? "A" : "B") + " | Stake " +
                I(ticket.Stake) + " Cats | Quoted return " + I(ticket.Payout) + " Cats | Potential profit +" +
                I(unchecked(ticket.Payout - ticket.Stake)) + " Cats";
        }
        else if (chosen && market && !fighting)
        {
            int payout = TownBettingPolicy.ReturnCats(stake, ability[selectedSide], ability[1 - selectedSide]);
            view.Slip = "YOUR BET: Team " + (selectedSide == 0 ? "A" : "B") + " | Stake " + I(stake) + " Cats | Total return " +
                I(payout) + " Cats | Profit +" + I(unchecked(payout - stake)) + " Cats";
        }
        else view.Slip = fighting ? "Bets closed. No ticket placed." : completed ? "No ticket placed for this bout." :
            "YOUR BET: Choose a team and stake. Place bet confirms your wager.";
        if (!ticket.Pending && market && !fighting)
        {
            if (!ready) view.Status = "Bring a squad member close to this town's bookie; an active save is required.";
            else if (!valid) view.Status = "Betting unavailable: fighter data is incomplete.";
            else if (g.BookieCredit != 0) view.Status = "Pending Cats return must be collected before betting.";
            else if (money < stake) view.Status = "Not enough Cats for this stake.";
            else if (!chosen) view.Status = "Select a team, then choose your stake. One wager per bout.";
        }
        return view;
    }

    /// <summary>The settled-wager window. <paramref name="resultFighterViews"/> = the module's two <c>resultFighterViews</c> lists (length 2).</summary>
    // C++: TownBookie.cpp:199-230
    public static ResultView MakeResultView(in TownBettingPolicy.Result result, IReadOnlyList<FighterView>[] resultFighterViews)
    {
        var view = new ResultView();
        view.Fighters[0] = new List<FighterView>(resultFighterViews[0]);
        view.Fighters[1] = new List<FighterView>(resultFighterViews[1]);
        view.WinningSide = result.WinningSide;
        view.SelectedSide = result.SelectedSide;
        view.Tone = result.Outcome == TownBettingPolicy.ResultOutcome.Won ? 1 :
            result.Outcome == TownBettingPolicy.ResultOutcome.Lost ? 2 : 3;
        string text;
        if (result.Outcome == TownBettingPolicy.ResultOutcome.Won)
        {
            view.Title = "WAGER WON";
            view.Context = "Team " + (result.WinningSide == 0 ? "A" : "B") + " claimed the arena. Your pick delivered.";
            text = "STAKE  " + I(result.Stake) + " CATS     RETURNED  " + I(result.Returned) + " CATS\nNET WIN  +" + I(result.Net) + " CATS";
        }
        else if (result.Outcome == TownBettingPolicy.ResultOutcome.Lost)
        {
            view.Title = "WAGER LOST";
            view.Context = "Team " + (result.WinningSide == 0 ? "A" : "B") + " claimed the arena. You backed Team " +
                (result.SelectedSide == 0 ? "A" : "B") + ".";
            text = "STAKE  " + I(result.Stake) + " CATS     RETURNED  0 CATS\nNET LOSS  -" + I(unchecked(-result.Net)) + " CATS";
        }
        else
        {
            view.Title = "WAGER REFUNDED";
            view.Context = "No victor was declared. The bookie returns your stake.";
            text = "STAKE  " + I(result.Stake) + " CATS     RETURNED  " + I(result.Returned) + " CATS\nNET  0 CATS";
        }
        view.Payout = text;
        return view;
    }

    /// <summary><c>Wager</c>'s accepted message (TownBookie.cpp:107-108).</summary>
    // C++: TownBookie.cpp:107-108
    public static string AcceptedMessage(int stake, int payout) =>
        "Accepted: " + I(stake) + " Cats. Winning total return: " + I(payout) + " Cats.";

    /// <summary><c>Wager</c>'s <c>CombatBalanceLog::MarketEvent("wager_accepted", fields)</c> fields (TownBookie.cpp:109-110).</summary>
    // C++: TownBookie.cpp:109
    public static string WagerFields(int side, int stake, int payout) =>
        ",\"bet_side\":" + I(side) + ",\"stake\":" + I(stake) + ",\"quoted_return\":" + I(payout);

    /// <summary><c>Settle</c>'s message: <paramref name="pending"/> = <c>ticket.pending</c> before settling, <paramref name="side"/> =
    /// <c>ticket.side</c>, <paramref name="credit"/> = <c>TownBettingPolicy::Settle(ticket, winner)</c> (read only when pending).
    /// <paramref name="winner"/> is already -1 when <c>Eligible()</c> was false (:380).</summary>
    // C++: TownBookie.cpp:383-401
    public static string SettleMessage(bool pending, int winner, int side, int credit)
    {
        if (pending)
        {
            if (winner < 0) return "Bout cancelled/drawn: " + I(credit) + " Cats refunded.";
            return "Team " + (winner == 0 ? "A" : "B") + " wins. Bet " + (winner == side ? "won" : "lost") + ": " + I(credit) + " Cats returned.";
        }
        return winner < 0 ? "Bout cancelled or drawn. No wager placed." :
            winner == 0 ? "Team A wins. No wager placed." : "Team B wins. No wager placed.";
    }
}
