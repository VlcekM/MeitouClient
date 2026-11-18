// Pure parts of upstream/ProvingGrounds/plugin/src/TownArena.cpp (Proving Grounds by Shutori, GPLv3): the status texts.
namespace ProvingGrounds.Core;

public static partial class TownArena
{
    /// <summary>The C++ <c>Status(message)</c> (TownArena.cpp:127-134): a message equal to the current status does nothing; otherwise it
    /// becomes the status and the returned line is logged. Its player pop-up is dead code (<c>kShowTownStatusPopups</c> is false,
    /// TownArena.cpp:118), so the glue shows nothing. Returns null when unchanged.</summary>
    // C++: TownArena.cpp:127-134
    public static string? UpdateStatus(ref string status, string message)
    {
        if (status == message) return null;
        status = message;
        return "Proving Grounds: town trial - " + message;
    }

    /// <summary>Every fixed status, Finish and planned-bout-cancel text (the reasons given to <c>CancelPlannedNpcBout</c> go on to
    /// <c>TownBookie::CancelNpcBout</c>), and the fixed log lines, with their TownArena.cpp line. The texts built from values are the
    /// functions below.</summary>
    public static class Text
    {
        public const string Idle = "Town trial idle";                                                                          // :97, :1839
        public const string RefreshUnavailableCaption = "Next Refresh: unavailable";                                           // :468, :471
        public const string RefreshNoSave = "Load or start a game before refreshing challenge offers";                         // :493
        public const string RefreshNoRegistry = "Open the town registry before refreshing challenge offers";                   // :498
        public const string RefreshBooked = "Finish or cancel the current booking before refreshing challenge offers";         // :503
        public const string RefreshBadClock = "Challenge refresh is unavailable because the in-game clock is invalid";        // :511
        public const string RefreshCollectFailed = "Could not collect the challenge refresh cost";                             // :524
        public const string SavedLineupWaiting = "Saved NPC lineup and wager waiting for the original fighters to load";       // :618
        public const string NpcLineupAnnounced = "Next NPC lineup announced; betting open for the scheduled bout";             // :688
        public const string PlayerTookSlot = "Player booking took the next slot";                                              // :692
        public const string UpcomingCancelledForPlayer = "Upcoming NPC bout cancelled for player booking; wagers refunded";    // :695
        public const string SearchStartedLog = "Proving Grounds: started lazy challenge search generation";                    // :787
        public const string MatchmakingUnavailable = "Background matchmaking is unavailable.";                                 // :901
        public const string BookNoSave = "Load or start a game before booking challenges";                                     // :1187
        public const string BookBusy = "Finish the current challenge or cancel the pending booking first";                     // :1192
        public const string BookWrongDivision = "This encounter is not bookable in the selected division";                     // :1201
        public const string BookReservedOrUnavailable = "Challenge is reserved, used, or an opponent is unavailable";          // :1208
        public const string BookFightersNotReady = "Every selected fighter must be recovered and in town";                     // :1235
        public const string BookLineupChanged = "Selected lineup changed; review the refreshed cards before booking";          // :1245
        public const string BookReviewBuyIn = "Review the refreshed buy-in and odds before booking this challenge";            // :1254
        public const string BookCollectFailed = "Could not collect the challenge buy-in";                                      // :1271
        public const string BookingCancelled = "Town booking cancelled";                                                       // :1347
        public const string SchedulingPaused = "NPC scheduling paused";                                                        // :1355
        public const string SlotTaken = "A town booking already holds the next slot";                                          // :1364
        public const string ReserveNeedsRecovered = "Booking needs a recovered squad member at Scratch's Registry";            // :1371
        public const string ProgressUnavailable = "Arena progress is unavailable; resolve the persistence error and reload";   // :1384, :1487
        public const string PlayerBookingBusy = "Finish or cancel the current player booking first";                          // :1385
        public const string DirectLineupChanged = "Selected lineup changed; review the current 1v1 opponent before booking";   // :1391
        public const string DirectUnavailable = "The displayed 1v1 lineup is currently unavailable";                           // :1396
        public const string WagerStarting = "Your wagered fight is starting.";                                                 // :1479 (showPlayerAMessage)
        public const string ArenaBusy = "Arena is busy; finish the current operation first";                                   // :1490
        public const string NeedRegistry = "Interact with Scratch's Registry before starting the town trial";                  // :1496
        public const string NoCrucible = "No finished Rust Crucible found in this Registry's town";                            // :1518
        public const string ReservationInvalid = "The reserved lineup or division is no longer valid; cancel the booking";     // :1535
        public const string ReservedRecovering = "Reserved opponent is still recovering";                                      // :1602
        public const string AnnouncedUnavailable = "An announced fighter or arena is unavailable";                             // :1622, :1941
        public const string AnnouncedIneligible = "An announced fighter is no longer eligible";                                // :1632
        public const string WaitingOpponents = "Town booking waiting for recovered opponents";                                 // :1650
        public const string NoMedic = "Pit fighters are ready, but no Scratch medic is fit and available";                     // :1655
        public const string WaitingSquad = "Town booking waiting for every selected fighter to recover and return";            // :1667
        public const string MedicControlUnavailable = "Town booking unavailable: medic AI control could not be installed";     // :1686
        public const string PlayerHoldsSlot = "A player booking holds the next town slot";                                     // :1770
        public const string BoutUnderway = "Current bout is already underway";                                                 // :1774
        public const string WaitingToAnnounce = "Waiting for recovered NPC fighters and medical capacity to announce the next bout"; // :1777
        public const string ScheduledCancelled = "Scheduled bout cancelled";                                                   // :1783
        public const string TrialCancelled = "Town trial cancelled; town AI released";                                         // :1794
        public const string EventStopped = "Town event stopped: no player characters remain in Scratch";                       // :1870
        public const string ScheduledNoPlayers = "Scheduled bout cancelled: no player characters remain in Scratch";           // :1879
        public const string SavedLineupRestored = "Saved NPC lineup and wager restored";                                       // :1933
        public const string SavedLineupNoMarket = "Saved NPC lineup could not reopen its market";                              // :1934
        public const string SavedLineupExpired = "Saved NPC lineup unavailable after one game hour in Scratch";                // :1936
        public const string QueueReturnToRegistry = "Town queue waiting: return to the booked Registry";                       // :1978
        public const string BookingWaitingFighter = "Town booking waiting for its fighter to recover and return; cancel to release slot"; // :1986
        public const string FightFinished = "Town fight finished; medics treating and transferring fighters independently";   // :2045
    }

    // C++: TownArena.cpp:289
    public static string OpponentResolvedLog(string boundName, string uid) =>
        "Proving Grounds: challenge opponent resolved: " + boundName + " key=" + uid;

    /// <summary>The settlement status (shown to the player: <c>Status(message, true)</c>).</summary>
    // C++: TownArena.cpp:362-364
    public static string SettleChallengeMessage(int outcome, int credit) =>
        outcome < 0 ? "Challenge cancelled/drawn: " + I(credit) + " Cats refunded." :
        outcome == 1 ? "Challenge won: " + I(credit) + " Cats total return." : "Challenge lost: buy-in consumed.";

    // C++: TownArena.cpp:445-446
    public static string QuarantineLog(string name, string identity) =>
        "Proving Grounds: ingress fighter quarantined for this world session name=" + name + " identity=" + identity;

    /// <summary>The Finish text when a locked NPC preparation is cancelled. <paramref name="wagerPending"/> =
    /// <c>TownBookie::HasPendingWager()</c>, <paramref name="ingressStatus"/> / <paramref name="ingressPending"/> = <c>ArenaIngress</c>'s,
    /// all read BEFORE the ingress is cancelled.</summary>
    // C++: TownArena.cpp:451-459
    public static string PreparationCancelMessage(bool wagerPending, string ingressStatus, bool ingressPending)
    {
        bool formationFailure = !ingressPending &&
            ingressStatus.StartsWith("NPC formation ", StringComparison.Ordinal);
        return (formationFailure ? ingressStatus :
            "NPC bout cancelled: locked fighter unavailable or recovering") +
            (wagerPending && !formationFailure ? "; wager refunded" : "");
    }

    // C++: TownArena.cpp:518-519
    public static string RefreshCostMessage(int cost, int balance) =>
        "Refresh costs " + I(cost) + " Cats; faction balance: " + I(balance) + " Cats";

    // C++: TownArena.cpp:557-561
    public static string RefreshedMessage(int cost, int nextCost) =>
        "Challenge offers refreshed for " + I(cost) + " Cats; next refresh costs " + I(nextCost) + " Cats";

    /// <summary><paramref name="diagnostics"/> = the matchmaking snapshot's <c>diagnostics</c> (logged at most every 30 s; the throttle
    /// stays in the glue).</summary>
    // C++: TownArena.cpp:586
    public static string MatchmakingDiscoveryLog(string diagnostics) => "Proving Grounds: matchmaking discovery - " + diagnostics;

    /// <summary><paramref name="slot"/> 0..4 (the C++ writes one character, <c>'0' + slot</c>).</summary>
    // C++: TownArena.cpp:812-813
    public static string SearchResolvedLog(int slot, bool bestAvailable) =>
        "Proving Grounds: challenge search resolved slot=" + (char)('0' + slot) + (bestAvailable ? " best-available" : " in-band");

    /// <summary><paramref name="progressCaption"/> = <see cref="ProgressCaption"/>.</summary>
    // C++: TownArena.cpp:1136
    public static string ProgressionLog(string progressCaption) =>
        "Proving Grounds: shared town progression updated - " + progressCaption;

    /// <summary>BookChallenge's refusal when the offer is not bookable: the slot's own search reason (shown to the player,
    /// <paramref name="force"/>) for an unresolved generated offer, else the generic text. <paramref name="state"/> = the stored offer's
    /// state, <paramref name="offer"/> = the preview, <paramref name="reason"/> = the slot's unavailable reason.</summary>
    // C++: TownArena.cpp:1205-1208
    public static string BookUnavailableMessage(int slot, TownArenaPolicy.OfferState state, in TownChallengePolicy.Offer offer, string reason,
        out bool force)
    {
        force = false;
        if (slot < 5 && state == TownArenaPolicy.OfferState.Available && offer.Generated &&
            !TownChallengePolicy.Resolved(offer) && reason.Length != 0)
        { force = true; return reason; }
        return Text.BookReservedOrUnavailable;
    }

    /// <summary><paramref name="cooldownHours"/> = <c>PGConfig::ChallengeValues().cooldownHours</c>; "%.1f".</summary>
    // C++: TownArena.cpp:1216-1219
    public static string SkarnCooldownMessage(int cooldownHours, double gameHours, double skarnLastEnd)
    {
        double remaining = cooldownHours -
            (gameHours - skarnLastEnd);
        return "Skarn cooldown: " + CMath.FormatF(remaining, 1) + " in-game hours remaining";
    }

    // C++: TownArena.cpp:1225-1226
    public static string SelectExactlyMessage(int required) =>
        "Select exactly " + I(required) + " squad fighter" + (required == 1 ? "" : "s") + " for this challenge";

    // C++: TownArena.cpp:1260
    public static string ChallengeRequiresMessage(int buyIn, int balance) =>
        "Challenge requires " + I(buyIn) + " Cats; balance: " + I(balance) + " Cats. Collect any pending return first.";

    /// <summary><paramref name="caption"/> = <see cref="ChallengeCaption"/> of the slot (with its lineup).</summary>
    // C++: TownArena.cpp:1292-1293
    public static string ChallengeReservedMessage(string caption, int buyIn, int payout) =>
        "Challenge reserved: " + caption + " | Buy-in: " + I(buyIn) + " Cats | Winning total return: " + I(payout) + " Cats";

    // C++: TownArena.cpp:1357-1358
    public static string ScheduleMessage(bool enabled) => enabled ? "Ambient NPC bouts resumed" :
        "Ambient NPC bouts paused; current fight continues";

    // C++: TownArena.cpp:1380
    public static string SlotReservedMessage(string playerName) => "Next town 1v1 slot reserved for " + playerName;

    // C++: TownArena.cpp:1402
    public static string DirectReservedMessage(string opponentName) => "1v1 reserved vs " + opponentName;

    // C++: TownArena.cpp:1481
    public static string UnderwayMessage(bool playerMatch) => playerMatch ? "Booked town fight underway" : "Town 1v1 underway";

    /// <summary><paramref name="matches"/>: how many candidates matched the reserved opponent (the C++ calls this only when it is not 1).</summary>
    // C++: TownArena.cpp:1593
    public static string ChallengeWaitingMessage(string name, int matches) =>
        "Challenge waiting for " + name + (matches == 0 ? ": not found near town" : ": ambiguous identity");

    // C++: TownArena.cpp:1598
    public static string ChallengeWaitingMessage(string name, string challengeBlocker) =>
        "Challenge waiting for " + name + ": " + challengeBlocker;

    /// <summary><paramref name="availability"/>, <paramref name="npcs"/>, <paramref name="medics"/>: the snapshot's availability text and
    /// its npc and medic counts.</summary>
    // C++: TownArena.cpp:1615-1618
    public static string BoutWaitingMessage(string availability, int npcs, int medics) =>
        "Town bout waiting: " + availability +
        (npcs < 2 ? "; need at least 2 eligible opponents" :
        medics < 2 ? "; need at least 2 available medics" :
        "; no balanced lineup within medical capacity");

    /// <summary>"%uv%u scores %.2f / %.2f" (the team sizes cast to unsigned).</summary>
    // C++: TownArena.cpp:1643-1645
    public static string AmbientMatchupLog(int teamA, int teamB, double scoreA, double scoreB) =>
        "Proving Grounds: ambient matchup " + U((uint)teamA) + "v" + U((uint)teamB) + " scores " + CMath.FormatF(scoreA, 2) + " / " +
        CMath.FormatF(scoreB, 2);

    // C++: TownArena.cpp:1692-1694
    public static string MedicsWaitingMessage(int doctors, int requiredMedics) =>
        "Town booking waiting for medics: " + U((uint)doctors) + "/" + I(requiredMedics) + " fit and available";

    /// <summary><paramref name="ingressStatus"/> = <c>ArenaIngress::GetStatus()</c>.</summary>
    // C++: TownArena.cpp:1754
    public static string StagingFailedMessage(string ingressStatus) => "Town staging failed: " + ingressStatus;

    // C++: TownArena.cpp:2019
    public static string StagingEndedMessage(string ingressStatus) => "Town staging ended: " + ingressStatus;

    /// <summary>The staging status (shown to the player). <paramref name="player"/> = a player booking.</summary>
    // C++: TownArena.cpp:1758-1761
    public static string AssemblingMessage(MatchRules.MatchMode matchMode, bool player, int playerCount, int npcTeamSize, int neededEnemies) =>
        "Town " + (matchMode == MatchRules.MatchMode.Teams1v1 ? "Teams 1v1" : "Teams") + " assembling: " +
        I(player ? playerCount : npcTeamSize) + " vs " + I(player ? neededEnemies : neededEnemies - npcTeamSize);

    /// <summary><paramref name="medicalStatus"/> = <c>TownAftercare::GetStatus()</c> before Release; <paramref name="limbShopActive"/> =
    /// <c>TownLimbShop::IsActive()</c>.</summary>
    // C++: TownArena.cpp:2064-2066
    public static string AftercareDoneMessage(string medicalStatus, bool limbShopActive) =>
        medicalStatus + (limbShopActive ? "; replacement-limb visits pending" : "");
}
