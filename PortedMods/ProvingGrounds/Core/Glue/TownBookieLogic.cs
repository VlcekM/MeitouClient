using System.Numerics;
using System.Text;

namespace ProvingGrounds.Core.Glue;

// Port of upstream/ProvingGrounds/plugin/src/TownBookie.h/.cpp (Proving Grounds by Shutori, GPLv3), M8 task 8.1a (TW §2.6, CAP 7, 10, 16, spike S4):
// every function of TownBookie.cpp that is not a widget call, one copy each, over ITownBookieHost (fake-tested in Tests/Glue/TownBookieLogicTests.cs).
// The texts and views are Core/TownBookie.cs (MakeView, MakeResultView, Capture, the accepted text and fields, the settle messages), the odds and
// stake rules Core/TownBettingPolicy.cs and PGConfig.ClampStake: called, not re-ported. The state (C++ file-level globals, TownBookie.cpp:27-43) is
// this object's, one per world (src/Town/TownBookie.cs owns it; TownArena's AbandonWorldState chain clears it, TownArena.cpp:1805).
// The two windows (TownBookieUI / TownBookieResultUI, task 8.1b) draw the views this logic publishes; their C++ callbacks (SelectSide, ChangeStake,
// PlaceBet, Arrange, Close, TownBookie.cpp:232-271) run here when the window's command arrives in the sim, re-checked on arrival with the C++
// button's enabled rule (MakeView's canSelect / canStake / canBet / canArrange, :131-198; a stake button also by the window's own rule,
// TownBookieUI.cpp:404-407) and a request number (one at or below LastSeq is dropped, as tasks 6.4a / 7.5).

/// <summary>What TownBookie.cpp reads and writes outside its own globals.</summary>
public interface ITownBookieHost<TId>
{
    /// <summary>The C++ NULL character (an unset <c>hand</c>).</summary>
    TId None { get; }
    bool IsNull(TId c);
    /// <summary><c>Character::isValid()</c> (a live id of this world).</summary>
    bool Valid(TId c);
    bool IsDead(TId c);
    bool IsUnconscious(TId c);
    /// <summary>Whether <paramref name="c"/> is one of <c>ou->player->getAllPlayerCharacters()</c>.</summary>
    bool IsPlayer(TId c);
    /// <summary><c>ou->player->getAllPlayerCharacters()</c>.</summary>
    IReadOnlyList<TId> PlayerCharacters();
    /// <summary><c>getCurrentTownLocation()</c> as a town key (two "no town" keys compare equal, as the C++ NULL pointers).</summary>
    long CurrentTown(TId c);
    /// <summary><c>getPosition()</c>; false for a character whose position cannot be read.</summary>
    bool Position(TId c, out Vector3 position);
    /// <summary><c>IsBookie(object)</c> (TownBookie.cpp:274-277): valid and the record is the Scratch Bookie 111.</summary>
    bool IsBookieRecord(TId c);
    /// <summary>The player's money (<c>Wallet()->getMoney()</c>, the long purse; Core/Glue/TownChallengeWalletRule reads it as the C++ int).</summary>
    long Money { get; }
    /// <summary><c>addMoney</c> / <c>takeMoney</c> (the rules are checked before the call).</summary>
    void AddMoney(long delta);
    /// <summary><c>LeaderboardStore::HasActiveSave()</c>.</summary>
    bool HasActiveSave { get; }
    /// <summary><c>LeaderboardStore::GetBookieCredit()</c> (an <c>int&amp;</c>: the saved credit).</summary>
    int BookieCredit { get; set; }
    /// <summary><c>LeaderboardStore::GetStandings(Town)</c>.</summary>
    IReadOnlyList<LeaderboardData.Standing> TownStandings();
    /// <summary><c>getName()</c>.</summary>
    string Name(TId c);
    /// <summary><c>ArenaCombatProfile::Read(c)</c>.</summary>
    ArenaCombatProfile.Profile Profile(TId c);
    /// <summary><c>TownMatchmakingRuntime::Identity(c)</c>.</summary>
    string Identity(TId c);
    /// <summary><c>TownMatchmakingRuntime::SelectionIdentity(c)</c> (the window's portrait key, TownBookieUI.cpp:363; "" for an invalid one).</summary>
    string SelectionIdentity(TId c);
    /// <summary><c>TownMatchmakingRuntime::Ability(c)</c>.</summary>
    double Ability(TId c);
    /// <summary><c>TownArena::IsNpcReadyForPreparation(c)</c>.</summary>
    bool IsNpcReadyForPreparation(TId c);
    /// <summary><c>TownArena::HasPlannedNpcBout() &amp;&amp; ou</c>.</summary>
    bool HasPlannedNpcBout { get; }
    /// <summary><c>TownArena::GetPlannedNpcStartHours()</c>.</summary>
    double PlannedNpcStartHours { get; }
    /// <summary><c>ou->getTimeStamp_inGameHours().getTotalHours()</c>.</summary>
    double NowHours { get; }
    /// <summary><c>TownArena::BindRegistryNear(c)</c>.</summary>
    bool BindRegistryNear(TId c);
    /// <summary><c>TownArena::BeginTrial()</c>.</summary>
    void BeginTrial();
    /// <summary><c>TownArena::GetStatus()</c>.</summary>
    string TownStatus();
    /// <summary><c>TownArenaUI::Close()</c>.</summary>
    void CloseTownRegistryWindow();
    /// <summary><c>GetTickCount()</c> (the sim clock: it does not advance while the game is paused).</summary>
    uint NowMs { get; }
    /// <summary><c>CombatBalanceLog::OpenMarket(fighters, teams, count, scoreA, scoreB, eligible)</c> (the scheduled hours and the standings are read by
    /// the host, as the C++ reads them inside OpenMarket).</summary>
    void LogOpenMarket(TId[] fighters, MatchRules.MatchTeam[] teams, int count, double scoreA, double scoreB, bool eligible);
    /// <summary><c>CombatBalanceLog::MarketEvent(ev, fields)</c>.</summary>
    void LogMarketEvent(string ev, string fields);
    /// <summary><c>CombatBalanceLog::CloseMarket(winner, side, stake, quotedReturn, credit, hadTicket)</c>.</summary>
    void LogCloseMarket(int winner, int side, int stake, int quotedReturn, int credit, bool hadTicket);
    /// <summary><c>CombatBalanceLog::Abandon()</c>.</summary>
    void LogAbandon();
    /// <summary><c>PGLog::Debug</c>.</summary>
    void Debug(string line);
    /// <summary><c>PGLog::Error</c>.</summary>
    void Error(string line);
    /// <summary><c>ou->showPlayerAMessage(text, true)</c>.</summary>
    void Message(string text);
}


/// <summary>The TownBookie.cpp globals whose value carries over a load in the same process: AbandonWorldState (TownBookie.cpp:405-418) leaves
/// <c>stake</c>, <c>elapsed</c> and <c>ability</c> alone and sets <c>message</c> to "NPC bouts only. Choose a side before combat." (:417), which
/// the loaded world then starts with (a fresh process starts with :43's text). The port builds a TownBookieLogic per world; these live here,
/// one object per process (<see cref="Process"/>), so a load keeps them as the C++ does. Sim thread only (every world's serial steps).</summary>
public sealed class TownBookieProcessState
{
    /// <summary>The process's state (the engine half's); tests give each logic its own.</summary>
    public static readonly TownBookieProcessState Process = new();
    public int Stake = 100;          // C++ stake = 100 (:41)
    public float Elapsed;            // C++ elapsed = 0 (:35)
    public readonly double[] Ability = [1, 1];   // C++ ability[2] = {1, 1} (:33)
    public string Message = TownBookieLogic<int>.Text.Idle;   // C++ message (:43)
}

/// <summary>TownBookie.cpp's state and functions for one world (sim thread, serial steps).</summary>
public sealed partial class TownBookieLogic<TId>
{
    /// <summary>C++ <c>kMaxTeam</c> (TownBookie.cpp:28).</summary>
    public const int MaxTeam = 4;
    /// <summary>C++ <c>kAdjustments</c> (TownBookieUI.cpp:24): the six stake buttons, left to right.</summary>
    public static readonly int[] Adjustments = [-1000, -100, -10, 10, 100, 1000];
    /// <summary>The ingress hold's window (C++ <c>elapsed &lt; 20.0f</c>, :376; NpcAssembling's <c>elapsed = 20.0f</c>, :366).</summary>
    public const float HoldSeconds = 20.0f;
    /// <summary>Tick's result delay (C++ <c>now - resultScheduledAt &gt;= 1000</c>, :424).</summary>
    public const uint ResultDelayMs = 1000;
    /// <summary>Tick's window refresh (C++ <c>now - lastUiTick &lt; 250</c>, :435).</summary>
    public const uint RefreshMs = 250;

    // ---- the texts of TownBookie.cpp not in Core/TownBookie.cs (byte for byte) ----
    public static class Text
    {
        public const string Idle = "NPC bouts only. Arrange a bout to open betting.";                                   // :43
        public const string IdleAfterTeardown = "NPC bouts only. Choose a side before combat.";                         // :417
        public const string WagerNotNear = "Bring a squad member close to this town's bookie.";                         // :99
        public const string WagerUnavailable = "Bet unavailable: check Cats, existing wager, and that combat has not started.";   // :102
        public const string WagerNotAccepted = "Could not accept the Cats wager.";                                      // :105
        public const string ArrangeNotNear = "Move a squad member close to the bookie.";                                // :252
        public const string ArrangeBusy = "Finish the current bout first.";                                             // :253
        public const string ArrangeNoRegistry = "No finished Town Registry found near this bookie.";                    // :255
        public const string MarketOpen = "Lineup announced. Betting open until combat begins. One wager per bout.";     // :317
        public const string MarketIncomplete = "Team bout unavailable for betting: fighter data is incomplete.";         // :318
        public const string WagerRestored = "Accepted wager restored with the announced lineup and quoted return.";     // :347
        public const string Assembling = "Scheduled bout assembling. Betting closes when combat begins.";               // :368
        public const string ShowNotNear = "Move a squad member closer to the Scratch bookie.";                          // :442
        public const string ResultDismissed = "Proving Grounds: bookie wager result dismissed";                         // :286
        public const string ResultScheduled = "Proving Grounds: bookie wager result scheduled";                         // :397
        public const string ResultShown = "Proving Grounds: bookie wager result shown";                                 // :430
        public const string ResultShowFailed = "Proving Grounds: bookie wager result show failed";                      // :431
        public const string MarketCombatStarted = "market_combat_started";                                              // :361
        public const string MarketCancelled = "market_cancelled";                                                       // :371
        public const string WagerAccepted = "wager_accepted";                                                           // :110
    }

    readonly TId none;
    // C++ :29-43
    readonly List<TId>[] teams = [[], []];                                   // teams[2][kMaxTeam] + teamCounts
    TId bookie;
    readonly List<TownBookie.FighterView>[] fighterViews = [[], []];
    readonly List<(TId Actor, string Key)>[] fighterActors = [[], []];      // the views' hand actor, its portrait key read once per lineup
    readonly List<TownBookie.FighterView>[] resultFighterViews = [[], []];
    readonly List<(TId Actor, string Key)>[] resultFighterActors = [[], []];
    bool market, fighting, completed;
    readonly TownBookieProcessState process;
    double[] ability => process.Ability;
    float elapsed { get => process.Elapsed; set => process.Elapsed = value; }
    int stake { get => process.Stake; set => process.Stake = value; }
    uint lastTick, lastUiTick;
    TownBettingPolicy.Ticket ticket = new();
    TownBettingPolicy.Result result = new();
    bool resultScheduled, resultReady;
    uint resultScheduledAt;
    int selectedSide = -1;
    string actionMessage = "";
    string message { get => process.Message; set => process.Message = value; }

    // ---- port only: the two windows (TownBookieUI::IsVisible / TownBookieResultUI::IsVisible) and their published views ----
    bool betVisible, resultVisible;
    int requestSerial;   // the models' RequestSerial (an int, as PgUiModel's: one per Show / Close)
    long lastSeq;
    TownBookieUiView<TId> view = TownBookieUiView<TId>.Empty;
    TownBookieResultUiView<TId> resultView = TownBookieResultUiView<TId>.Empty;
    readonly TownBookie.ViewInputs inputs = new();
    readonly double[,] memberAbility = new double[2, MaxTeam];
    readonly double[] teamBuffer = new double[MaxTeam];
    readonly StringBuilder keyBuilder = new();

    /// <summary>Port only (the 8.1a quality rule): a fighter whose card or ability read threw in BeginNpcBout (its card shows "Unavailable fighter",
    /// its ability 0: no market), counted and logged once per world.</summary>
    public int FighterReadFailures { get; private set; }
    bool fighterReadLogged;

    /// <param name="process">The globals that outlive a world (default: the process's own, <see cref="TownBookieProcessState.Process"/>).</param>
    public TownBookieLogic(TId none, TownBookieProcessState? process = null)
    {
        this.none = none; bookie = none; this.process = process ?? TownBookieProcessState.Process;
    }

    // ---------- read-only (the engine half's model and test verb) ----------
    public bool IsMarketOpen => market;
    public bool IsFighting => fighting;
    public bool IsCompleted => completed;
    public float Elapsed => elapsed;
    public TownBettingPolicy.Ticket Ticket => ticket;
    public TownBettingPolicy.Result Result => result;
    public bool ResultScheduledNow => resultScheduled;
    public bool ResultReadyNow => resultReady;
    /// <summary>When the result was scheduled (Settle) and, port only for the test verb, when Tick showed it (sim ms).</summary>
    public uint ResultScheduledAtMs => resultScheduledAt;
    public uint ResultShownAtMs { get; private set; }
    public int Stake => stake;
    public int SelectedSide => selectedSide;
    public string Message => message;
    public string ActionMessage => actionMessage;
    public TId BookieCharacter => bookie;
    public IReadOnlyList<TId> Team(int side) => teams[side];
    public double AbilityOf(int side) => ability[side];
    public bool BetWindowVisible => betVisible;
    public bool ResultWindowVisible => resultVisible;
    public TownBookieUiView<TId> View => view;
    public TownBookieResultUiView<TId> ResultView => resultView;
    public long LastSeq => lastSeq;
    public int RequestSerial => requestSerial;

    // C++: TownBookie.cpp:48-53 (no character counts as a player's: the C++ <c>!c</c> answers true)
    bool Player(ITownBookieHost<TId> h, TId c) => h.IsNull(c) || h.IsPlayer(c);

    // C++: TownBookie.cpp:54-63
    bool Eligible(ITownBookieHost<TId> h)
    {
        if (teams[0].Count <= 0 || teams[1].Count <= 0) return false;
        for (int side = 0; side < 2; ++side)
            for (int i = 0; i < teams[side].Count; ++i)
            {
                TId c = teams[side][i];
                if (h.IsNull(c) || !h.Valid(c) || Player(h, c)) return false;
            }
        return true;
    }

    // C++: TownBookie.cpp:64-75 (squaredLength <= 40000: within 200 units)
    bool AtBookie(ITownBookieHost<TId> h)
    {
        TId b = bookie;
        if (h.IsNull(b) || !h.Valid(b) || h.IsDead(b) || h.IsUnconscious(b) || Player(h, b)) return false;
        if (!h.Position(b, out Vector3 bp)) return false;
        long town = h.CurrentTown(b);
        IReadOnlyList<TId> all = h.PlayerCharacters();
        for (int i = 0; i < all.Count; ++i)
        {
            TId c = all[i];
            if (!h.IsNull(c) && h.Valid(c) && !h.IsDead(c) && !h.IsUnconscious(c) &&
                h.CurrentTown(c) == town && h.Position(c, out Vector3 cp) && Vector3.DistanceSquared(cp, bp) <= 40000.0f) return true;
        }
        return false;
    }

    /// <summary>Team A's first fighter and the bookie both live and in the same town (Wager :97-99, MakeView :139-142: <c>hand::getCharacter()</c> is
    /// NULL for a character that is gone, so both C++ tests ask for two valid characters).</summary>
    bool FirstFighterSameTown(ITownBookieHost<TId> h)
    {
        TId a = teams[0].Count > 0 ? teams[0][0] : none, b = bookie;
        if (h.IsNull(a) || h.IsNull(b) || !h.Valid(a) || !h.Valid(b)) return false;
        return h.CurrentTown(a) == h.CurrentTown(b);
    }

    // C++: TownBookie.cpp:79-85
    void ClearTeams()
    {
        for (int side = 0; side < 2; ++side)
        {
            teams[side].Clear();
            fighterViews[side].Clear();
            fighterActors[side].Clear();
        }
    }

    // C++: TownBookie.cpp:86-93 (the INT_MAX guard = TownChallengeWalletRule.CanCredit: credit > 0, money >= 0, money <= INT_MAX - credit)
    void PayCredit(ITownBookieHost<TId> h)
    {
        if (!h.HasActiveSave) return;
        int credit = h.BookieCredit;
        if (!TownChallengeWalletRule.CanCredit(h.Money, credit)) return;
        h.AddMoney(credit);
        h.BookieCredit = 0;
    }

    // C++: TownBookie.cpp:94-111
    void Wager(ITownBookieHost<TId> h, int side)
    {
        // Wallet() is never NULL here (Meitou's player always has a purse); takeMoney succeeds when the money covers the stake
        if (!h.HasActiveSave || !AtBookie(h) || !FirstFighterSameTown(h)) { message = Text.WagerNotNear; return; }
        int money = TownChallengeWalletRule.Balance(h.Money);
        if (h.BookieCredit != 0 ||
            !TownBettingPolicy.CanAccept(ticket, market && Eligible(h), !fighting, side, stake, money))
        {
            message = Text.WagerUnavailable; return;
        }
        int payout = TownBettingPolicy.ReturnCats(stake, ability[side], ability[1 - side]);
        if (payout == 0 || !TownChallengeWalletRule.CanTake(h.Money, stake)) { message = Text.WagerNotAccepted; return; }
        h.AddMoney(-stake);
        ticket.Side = side; ticket.Stake = stake; ticket.Payout = payout; ticket.Pending = true;
        message = TownBookie.AcceptedMessage(stake, payout);
        h.LogMarketEvent(Text.WagerAccepted, TownBookie.WagerFields(side, stake, payout));
    }

    // C++: TownBookie.cpp:113-130 Capture (the text is Core's); a throwing read gives the unavailable card (port only, counted)
    TownBookie.FighterView Capture(ITownBookieHost<TId> h, TId c, IReadOnlyList<LeaderboardData.Standing> standings, out double fighterAbility)
    {
        try
        {
            bool valid = !h.IsNull(c) && h.Valid(c);
            var profile = h.Profile(c);
            var card = TownBookie.Capture(valid, valid ? h.Name(c) : null, profile, h.Identity(c), standings);
            fighterAbility = h.Ability(c);
            return card;
        }
        catch (Exception e)
        {
            ++FighterReadFailures;
            if (!fighterReadLogged)
            {
                fighterReadLogged = true;
                h.Error("Proving Grounds: bookie fighter read failed: " + e.GetType().Name + ": " + e.Message);
            }
            fighterAbility = 0;
            return TownBookie.Capture(false, null, new ArenaCombatProfile.Profile(), null, null);
        }
    }

    // C++: TownBookie.cpp:131-198 MakeView (Core's) with the module's globals; writes the clamped stake back (:146)
    TownBookie.View MakeView(ITownBookieHost<TId> h, bool writeStake = true)
    {
        var g = inputs;
        g.HasWallet = true;
        g.Money = TownChallengeWalletRule.Balance(h.Money);
        g.SelectedSide = selectedSide;
        g.Market = market; g.Fighting = fighting; g.Completed = completed;
        g.ResultScheduled = resultScheduled; g.ResultReady = resultReady;
        g.Eligible = Eligible(h);
        g.Nearby = AtBookie(h);
        g.SameTown = FirstFighterSameTown(h);
        g.HasActiveSave = h.HasActiveSave;
        g.BookieCredit = h.BookieCredit;
        g.Stake = stake;
        g.Ticket = ticket;
        g.TeamCounts[0] = teams[0].Count; g.TeamCounts[1] = teams[1].Count;
        g.Ability[0] = ability[0]; g.Ability[1] = ability[1];
        g.FighterViews[0] = fighterViews[0]; g.FighterViews[1] = fighterViews[1];
        g.PlannedBout = h.HasPlannedNpcBout;
        g.PlannedStartHours = g.PlannedBout ? h.PlannedNpcStartHours : 0;
        g.NowHours = g.PlannedBout ? h.NowHours : 0;
        g.ActionMessage = actionMessage;
        g.Message = message;
        var v = TownBookie.MakeView(g, out int clamped);
        if (writeStake) stake = clamped;   // port only: a command's enabled check (writeStake false) changes nothing when it is refused
        return v;
    }

    // C++: TownBookie.cpp:199-230 MakeResultView (Core's)
    TownBookie.ResultView MakeResultView() => TownBookie.MakeResultView(result, resultFighterViews);

    // C++: TownBookie.cpp:231 RefreshUi (TownBookieUI::Refresh(MakeView())): the view is published (the window draws it while open)
    void RefreshUi(ITownBookieHost<TId> h) => Publish(h, MakeView(h));

    // C++: TownBookie.cpp:232-237
    void SelectSide(ITownBookieHost<TId> h, int side)
    {
        if (side >= 0 && side <= 1 && market && !fighting && !ticket.Pending && !completed)
        {
            selectedSide = side; actionMessage = "";
        }
        RefreshUi(h);
    }

    // C++: TownBookie.cpp:238-245
    void ChangeStake(ITownBookieHost<TId> h, int delta)
    {
        if (market && !fighting && !ticket.Pending && !completed &&
            (delta == -1000 || delta == -100 || delta == -10 ||
             delta == 10 || delta == 100 || delta == 1000))
        {
            stake = TownBettingPolicy.AdjustStake(stake, delta); actionMessage = "";
        }
        RefreshUi(h);
    }

    // C++: TownBookie.cpp:246-250
    void PlaceBet(ITownBookieHost<TId> h)
    {
        if (selectedSide == 0 || selectedSide == 1) Wager(h, selectedSide);
        actionMessage = ticket.Pending ? "" : message;
        RefreshUi(h);
    }

    // C++: TownBookie.cpp:251-260
    void Arrange(ITownBookieHost<TId> h)
    {
        if (!AtBookie(h)) { actionMessage = Text.ArrangeNotNear; RefreshUi(h); return; }
        if (market || fighting || ticket.Pending) { actionMessage = Text.ArrangeBusy; RefreshUi(h); return; }
        if (!h.BindRegistryNear(bookie)) { actionMessage = Text.ArrangeNoRegistry; RefreshUi(h); return; }
        h.BeginTrial();
        actionMessage = h.TownStatus();
        RefreshUi(h);
    }

    // C++: TownBookie.cpp:278
    public bool IsVisible => betVisible || resultVisible;

    // C++: TownBookie.cpp:279-288 (TownBookieUI::Close + TownBookieResultUI::Close: the windows hidden)
    public void Close(ITownBookieHost<TId> h)
    {
        betVisible = false;
        resultVisible = false;
        if (resultReady)
        {
            result = new TownBettingPolicy.Result();
            resultFighterViews[0].Clear(); resultFighterViews[1].Clear();
            resultFighterActors[0].Clear(); resultFighterActors[1].Clear();
            resultReady = false;
            h.Debug(Text.ResultDismissed);
        }
        ++requestSerial;
        Publish(h, view.View);
        PublishResult(h, resultView.View);
    }

    // C++: TownBookie.cpp:289-319
    public void BeginNpcBout(ITownBookieHost<TId> h, TId[] fighters, MatchRules.MatchTeam[] matchTeams, int count, double scoreA, double scoreB)
    {
        Settle(h, -1);
        ClearTeams();
        completed = false;
        IReadOnlyList<LeaderboardData.Standing> standings = h.TownStandings();
        Array.Clear(memberAbility);
        for (int i = 0; fighters != null && matchTeams != null && i < count; ++i)
        {
            int side = matchTeams[i] == MatchRules.MatchTeam.TeamA ? 0 : matchTeams[i] == MatchRules.MatchTeam.TeamB ? 1 : -1;
            if (side < 0 || teams[side].Count >= MaxTeam) continue;
            int slot = teams[side].Count;
            teams[side].Add(fighters[i]);
            fighterViews[side].Add(Capture(h, fighters[i], standings, out double a));
            fighterActors[side].Add((fighters[i], PortraitKey(h, fighters[i])));
            memberAbility[side, slot] = a;
        }
        ability[0] = scoreA; ability[1] = scoreB;
        market = Eligible(h) && ability[0] > 0 && ability[1] > 0 &&
            TeamAbility(0) > 0 && TeamAbility(1) > 0;
        h.LogOpenMarket(fighters ?? [], matchTeams ?? [], count, ability[0], ability[1], market);
        fighting = false; elapsed = 0; lastTick = h.NowMs;
        ticket = new TownBettingPolicy.Ticket();
        if (!resultScheduled && !resultReady)
        {
            result = new TownBettingPolicy.Result();
            resultFighterViews[0].Clear(); resultFighterViews[1].Clear();
            resultFighterActors[0].Clear(); resultFighterActors[1].Clear();
        }
        selectedSide = -1;
        actionMessage = "";
        message = market ? Text.MarketOpen : Text.MarketIncomplete;
    }

    // TownBettingPolicy::TeamAbility(memberAbility[side], teamCounts[side]) over the side's row (no allocation)
    double TeamAbility(int side)
    {
        for (int i = 0; i < MaxTeam; ++i) teamBuffer[i] = memberAbility[side, i];
        return TownBettingPolicy.TeamAbility(teamBuffer, teams[side].Count);
    }

    // C++: TownBookie.cpp:320-328
    public bool PreparationValid(ITownBookieHost<TId> h)
    {
        if (!Eligible(h)) return false;
        for (int side = 0; side < 2; ++side)
            for (int i = 0; i < teams[side].Count; ++i)
                if (!h.IsNpcReadyForPreparation(teams[side][i])) return false;
        return true;
    }

    // C++: TownBookie.cpp:329
    public bool HasPendingWager() => ticket.Pending;

    // C++: TownBookie.cpp:330-336
    public void CapturePlannedNpcBout(ArenaPersistence.PlannedNpcBout output)
    {
        output.Market = market;
        output.WagerPending = ticket.Pending;
        output.WagerSide = ticket.Side;
        output.Stake = ticket.Stake;
        output.Payout = ticket.Payout;
    }

    // C++: TownBookie.cpp:337-350
    public bool RestorePlannedNpcBout(ArenaPersistence.PlannedNpcBout saved)
    {
        // BeginNpcBout clears the previous ticket. Only call after world teardown
        // and restore the frozen ticket after the exact saved lineup is installed.
        if (ticket.Pending || (saved.WagerPending && (!market || !saved.Market))) return false;
        if (saved.WagerPending)
        {
            ticket.Side = saved.WagerSide;
            ticket.Stake = saved.Stake;
            ticket.Payout = saved.Payout;
            ticket.Pending = true;
            selectedSide = ticket.Side;
            message = Text.WagerRestored;
        }
        return true;
    }

    // C++: TownBookie.cpp:351-356
    public void RefundUnrestoredWager(ITownBookieHost<TId> h, ArenaPersistence.PlannedNpcBout saved)
    {
        if (!saved.WagerPending || !h.HasActiveSave) return;
        int credit = h.BookieCredit;
        if (saved.Stake <= unchecked(int.MaxValue - credit)) h.BookieCredit = unchecked(credit + saved.Stake);
        PayCredit(h);
    }

    // C++: TownBookie.cpp:357-364
    public void CombatStarted(ITownBookieHost<TId> h)
    {
        // TownArena also signals player challenges. Only an open NPC market
        // belongs to this controller; player bouts must not lock its UI forever.
        if (!market) return;
        if (!fighting) h.LogMarketEvent(Text.MarketCombatStarted, "");
        fighting = true;
        if (market && !Eligible(h)) Settle(h, -1);
    }

    // C++: TownBookie.cpp:365-369
    public void NpcAssembling()
    {
        elapsed = HoldSeconds; // The scheduled market already provided its betting window.
        actionMessage = "";
        message = Text.Assembling;
    }

    // C++: TownBookie.cpp:370-375
    public void CancelNpcBout(ITownBookieHost<TId> h, string reason)
    {
        h.LogMarketEvent(Text.MarketCancelled, ",\"reason\":" + PGLog.Quote(reason));
        Settle(h, -1);
        message = reason + ". " + message;
        actionMessage = "";
    }

    // C++: TownBookie.cpp:376
    public bool HoldForBets() => market && !fighting && elapsed < HoldSeconds;

    // C++: TownBookie.cpp:377-404
    public void Settle(ITownBookieHost<TId> h, int winner)
    {
        bool hadBout = market || fighting || ticket.Pending;
        if (!hadBout) return;
        if (!Eligible(h)) winner = -1;
        h.LogCloseMarket(winner, ticket.Side, ticket.Stake, ticket.Payout,
            ticket.Pending ? (winner < 0 ? ticket.Stake : winner == ticket.Side ? ticket.Payout : 0) : 0, ticket.Pending);
        bool pending = ticket.Pending;
        int side = ticket.Side;
        if (pending)
        {
            result = TownBettingPolicy.Resolve(ticket, winner);
            CopyCards(fighterViews, fighterActors, resultFighterViews, resultFighterActors);
            int credit = TownBettingPolicy.Settle(ref ticket, winner);
            // the C++ int += has no guard and wraps (MSVC, two's complement): kept (docs/divergences.md "Kept bugs" K8)
            h.BookieCredit = unchecked(h.BookieCredit + credit);
            message = TownBookie.SettleMessage(true, winner, side, credit);
            PayCredit(h);
            resultScheduled = true;
            resultReady = false;
            resultScheduledAt = h.NowMs;
            h.Debug(Text.ResultScheduled);
        }
        else message = TownBookie.SettleMessage(false, winner, side, 0);
        actionMessage = ""; completed = true;
        market = false; fighting = false;
    }

    static void CopyCards(List<TownBookie.FighterView>[] from, List<(TId, string)>[] fromActors, List<TownBookie.FighterView>[] to, List<(TId, string)>[] toActors)
    {
        for (int side = 0; side < 2; ++side)
        {
            to[side].Clear(); to[side].AddRange(from[side]);
            toActors[side].Clear(); toActors[side].AddRange(fromActors[side]);
        }
    }

    // C++: TownBookie.cpp:405-418
    public void AbandonWorldState(ITownBookieHost<TId> h)
    {
        // Never move money during world teardown; the loaded native save and
        // sidecar replace both balances and pending credits together.
        h.LogAbandon();
        // TownBookieUI::Destroy(); TownBookieResultUI::Destroy();
        betVisible = resultVisible = false; ++requestSerial;
        ClearTeams(); bookie = none;
        completed = false;
        ticket = new TownBettingPolicy.Ticket(); market = false; fighting = false; lastTick = 0;
        result = new TownBettingPolicy.Result(); resultScheduled = resultReady = false; resultScheduledAt = 0;
        resultFighterViews[0].Clear(); resultFighterViews[1].Clear();
        resultFighterActors[0].Clear(); resultFighterActors[1].Clear();
        selectedSide = -1;
        actionMessage = "";
        lastUiTick = 0;
        message = Text.IdleAfterTeardown;
        view = new TownBookieUiView<TId>(false, requestSerial, lastSeq, new TownBookie.View(), TownBookieUiView<TId>.NoCards, new bool[Adjustments.Length], "");
        resultView = new TownBookieResultUiView<TId>(false, requestSerial, lastSeq, new TownBookie.ResultView(), TownBookieUiView<TId>.NoCards, "");
    }

    // C++: TownBookie.cpp:419-438
    public void Tick(ITownBookieHost<TId> h)
    {
        uint now = h.NowMs;
        // ou->isPaused(): the sim clock stands still while the game is paused, so a paused frame adds nothing
        if (lastTick != 0 && market && !fighting) elapsed += unchecked(now - lastTick) / 1000.0f;
        lastTick = now;
        PayCredit(h);
        if (resultScheduled && unchecked(now - resultScheduledAt) >= ResultDelayMs)
        {
            resultScheduled = false;
            resultReady = true;
            h.CloseTownRegistryWindow();
            betVisible = false;                      // TownBookieUI::Close()
            resultVisible = true; ++requestSerial;   // TownBookieResultUI::Show(MakeResultView(), ResultCallbacks()): the window half draws it
            PublishResult(h, MakeResultView());
            ResultShownAtMs = now;
            h.Debug(Text.ResultShown);
            lastUiTick = 0;
            Publish(h, MakeView(h));                 // the betting window's model, closed
        }
        if (!betVisible) return;
        if (lastUiTick != 0 && unchecked(now - lastUiTick) < RefreshMs) return;
        lastUiTick = now;
        RefreshUi(h);
    }

    // C++: TownBookie.cpp:439-448
    public void Show(ITownBookieHost<TId> h, TId bookieCharacter)
    {
        if (h.IsNull(bookieCharacter) || !h.IsBookieRecord(bookieCharacter)) return;
        bookie = bookieCharacter;
        if (!AtBookie(h)) { h.Message(Text.ShowNotNear); return; }
        h.BindRegistryNear(bookie);
        h.CloseTownRegistryWindow();
        betVisible = true; ++requestSerial;   // TownBookieUI::Show(MakeView(), Callbacks()): true once the window half can draw it
        RefreshUi(h);
        lastUiTick = 0; Tick(h);
    }

    // ---------- the window commands (port only: the C++ callbacks re-checked on arrival) ----------

    /// <summary>Port only (tasks 6.4a / 7.5's rule): a request is recorded (LastSeq) when it arrives; one at or below LastSeq is dropped.</summary>
    bool Accept(long seq)
    {
        if (seq <= lastSeq) return false;
        lastSeq = seq;
        return true;
    }

    /// <summary>The Select Team button (C++ Select -> SelectSide, TownBookieUI.cpp:147-149), run only while the window is open and canSelect holds.</summary>
    public bool OnSelectSide(ITownBookieHost<TId> h, long seq, int side)
    {
        if (!betVisible || !Accept(seq)) return Rejected(h);
        if (!MakeView(h, writeStake: false).CanSelect || side < 0 || side > 1) return Rejected(h);
        SelectSide(h, side);
        return true;
    }

    /// <summary>A stake button (C++ Stake -> ChangeStake, :150-152), run only while it is enabled as the window draws it (canStake and the stake
    /// moves, TownBookieUI.cpp:404-407) and the stake is still the drawn one.</summary>
    public bool OnChangeStake(ITownBookieHost<TId> h, long seq, int delta, int shownStake)
    {
        if (!betVisible || !Accept(seq)) return Rejected(h);
        var v = MakeView(h, writeStake: false);
        if (Array.IndexOf(Adjustments, delta) < 0 || !v.CanStake || v.Stake != shownStake ||
            TownBettingPolicy.AdjustStake(v.Stake, delta) == v.Stake) return Rejected(h);
        ChangeStake(h, delta);
        return true;
    }

    /// <summary>The Place bet button (C++ Bet -> PlaceBet, :153), run only while canBet holds and the side and stake are still the drawn ones.</summary>
    public bool OnPlaceBet(ITownBookieHost<TId> h, long seq, int shownSide, int shownStake)
    {
        if (!betVisible || !Accept(seq)) return Rejected(h);
        var v = MakeView(h, writeStake: false);
        if (!v.CanBet || v.SelectedSide != shownSide || v.Stake != shownStake) return Rejected(h);
        PlaceBet(h);
        return true;
    }

    /// <summary>The Arrange NPC bout button (C++ Arrange, :154), run only while canArrange holds.</summary>
    public bool OnArrange(ITownBookieHost<TId> h, long seq)
    {
        if (!betVisible || !Accept(seq)) return Rejected(h);
        if (!MakeView(h, writeStake: false).CanArrange) return Rejected(h);
        Arrange(h);
        return true;
    }

    /// <summary>A window's close button or the framework's Closed report (C++ WindowButton / CloseClicked -> TownBookie::Close, :155-157;
    /// TownBookieResultUI.cpp:89-93), only for the window drawn under the current <paramref name="shownSerial"/> (an old window's close never
    /// closes a newer one).</summary>
    public bool OnClose(ITownBookieHost<TId> h, int shownSerial)
    {
        if (!IsVisible || shownSerial != requestSerial) return false;
        Close(h);
        return true;
    }

    bool Rejected(ITownBookieHost<TId> h) { Publish(h, view.View); return false; }   // a new view with the new LastSeq

}
