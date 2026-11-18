using Meitou.Simulation;

namespace ProvingGrounds.Engine;

/// <summary>Something the frame thread (console, windows) asks the system to do. Applied in the serial <c>Inputs</c> of the tick it names:
/// send with <c>ProvingGroundsMod.Ctx!.Enqueue(cmd with { Tick = ctx.Tick + 1 })</c>. One record per UI action, added by the tasks
/// that need them.</summary>
abstract record PgCommand : SimCommand;

/// <summary>A <c>pg</c> console line that needs the world (census, the test-only spike verbs).</summary>
sealed record ConsoleLine(string Verb, IReadOnlyList<string> Args) : PgCommand;

// ---------- task 4.4: window clicks ----------

/// <summary>A widget event of one of the mod's windows (src/Ui/ScreenHost.cs): a button press (with Ctrl held: the test window's flag), a right
/// press, a pointer release (task 4.5b), a list select / accept or an edit box's text. Applied in Inputs by the screen's sim half (ProvingGroundsSystem.ApplyUiClick).
/// Mod commands wait while the game is paused (CAP 7.0, World.Interface.cs:22-40): a click made in a paused game applies when it resumes.</summary>
sealed record UiClick(ProvingGrounds.Core.Glue.UiClickData Click) : PgCommand;

// ---------- task 4.6b ----------

/// <summary>The "Debug menu key" was pressed and taken (src/Ui/DebugMenu.cs, the PlayerInterface.UiKey postfix; C++ PollF8Toggle's key read,
/// ArenaUI.cpp:2969): the debug menu's sim half toggles the menu in the frame loop's PollDebugMenuKey slot when the setting is on.</summary>
sealed record DebugMenuKeyPressed : PgCommand;
// ---------- end task 4.6b ----------

// ---------- task 8.7b ----------

/// <summary>A debug menu button press (src/Ui/DebugMenu.cs, DebugMenuScreen's Click; C++ each button's eventMouseButtonClick handler,
/// DebugMenu.cpp:387-509) with what the window last DREW for that widget (<paramref name="Drawn"/>: the row's character, the selection, the
/// label and enabled state, the page label, the tab). Applied in Inputs by the debug menu's sim half (DebugMenuSim.ApplyPress), which refuses it
/// when the drawn state is no longer its own (port only). The window's Closed report stays task 4.4's UiClick.</summary>
sealed record DebugMenuPress(string Widget, ProvingGrounds.Core.Glue.DebugMenuDrawn Drawn) : PgCommand;
// ---------- end task 8.7b ----------

// ---------- task 5.2b: a Rewards purchase (the Rewards window's buttons, task 5.3; the TEST-ONLY `pg reward` sends the same) ----------

/// <summary>One RewardDelivery.h purchase for <paramref name="Fighter"/> (C++ RewardsUI.cpp:489-561: OnClaimClicked -> ClaimNext, OnLicenceClicked
/// -> PurchaseLicence / PurchaseWeaponLicence, OnGeneralClicked -> PurchaseGeneral): <paramref name="Item"/> is the ArenaRewards.Piece,
/// ArmourSet or GeneralItem the kind names (0 for the weapons licence). Applied in Inputs by src/Game/RewardDelivery.cs Apply, which publishes
/// the result as <see cref="PgSnapshot.LastPurchaseOf"/>(<paramref name="Sender"/>) with <paramref name="Seq"/>.
/// <para><paramref name="Seq"/>: the sender's request number (the window's own counter; the test verb's). <paramref name="Shown"/>: for a Claim,
/// the offer the window showed on the clicked card; the sim refuses the command (not ok, empty status) when ResolveOffer now differs, so two
/// clicks before the window saw the first result never buy an offer nobody was shown. Null = no check (the test verb). For the other kinds the
/// sequence is the guard: the Rewards window sends no other purchase while its own last published Seq is not its last request's (Core/Glue/RewardsUILogic.cs
/// RewardsPurchaseGate, task 5.3a). <paramref name="Sender"/>: who sent it (task 5.3a quality review): each sender has its own sequence and reads only
/// its own last result.</para></summary>
sealed record RewardPurchase(CharacterId Fighter, ProvingGrounds.Core.Glue.RewardPurchaseKind Kind, int Item, long Seq = 0,
    ProvingGrounds.Core.ArenaRewards.Offer? Shown = null,
    ProvingGrounds.Core.Glue.RewardPurchaseSender Sender = ProvingGrounds.Core.Glue.RewardPurchaseSender.Window) : PgCommand;
// ---------- end task 5.2b ----------

// ---------- task 5.3a: the Rewards window's other buttons (the window half, 5.3b, sends them; the TEST-ONLY `pg rewards click` too) ----------
// The purchase buttons send RewardPurchase (task 5.2b) with the window's sequence number and, for a claim, the offer the card showed.

/// <summary>Any Rewards window command but a purchase (ProvingGroundsSystem hands each to the Rewards window's sim half, src/Game/RewardsUISim.cs).</summary>
abstract record RewardsUiCommand : PgCommand;

/// <summary>C++ OnFighterClicked (RewardsUI.cpp:463-487): the row that showed <paramref name="Fighter"/> (never a row index).</summary>
sealed record RewardsSelectFighter(CharacterId Fighter) : RewardsUiCommand;

/// <summary>C++ OnCatalogTabClicked (RewardsUI.cpp:507-521): GENERAL / ARMOUR / WEAPONS.</summary>
sealed record RewardsSelectTab(ProvingGrounds.Core.Glue.RewardsCatalogTab Tab) : RewardsUiCommand;

/// <summary>C++ OnCloseClicked (RewardsUI.cpp:561, "Close catalogue") asked by the sim side (the TEST-ONLY verb); the window's own close reaches
/// the sim as task 4.4's Closed click.</summary>
sealed record RewardsClose : RewardsUiCommand;
// ---------- end task 5.3a ----------

// ---------- task 6.4a: the recruitment window's commands (the window half, 6.4b, sends them; the TEST-ONLY `pg recruit` too) ----------

/// <summary>Any recruitment window command (ProvingGroundsSystem hands each to its sim half, src/Game/PrisonerRecruitmentUISim.cs).</summary>
abstract record RecruitmentUiCommand : PgCommand;

/// <summary>C++ OnRowClicked (PrisonerRecruitmentUI.cpp:162-172): the row that showed <paramref name="Prisoner"/> (never a row index).</summary>
sealed record RecruitmentSelect(CharacterId Prisoner) : RecruitmentUiCommand;

/// <summary>C++ OnCloseClicked (PrisonerRecruitmentUI.cpp:475-478, "Back to Arena"): Close, the Arena window back from the bound Registry. The
/// window's own close (Escape, the title bar) reaches the sim as task 4.4's Closed click instead.</summary>
sealed record RecruitmentClose : RecruitmentUiCommand;

/// <summary>C++ OnRecruitClicked (PrisonerRecruitmentUI.cpp:465-473, "Grant Freedom - N Marks"): PrisonerRecruitment::Recruit of the selection the
/// window showed. <paramref name="Seq"/> = the window's request number (published back as the model's LastSeq when applied, so the window sends no
/// second one while it waits); <paramref name="ShownPrice"/> = the price the button showed (the model's ShownPrice). The sim runs it only while the
/// window still selects <paramref name="Prisoner"/> at that price (Core/Glue/PrisonerRecruitmentUILogic.cs OnRecruitCommand); a Seq at or below the
/// model's LastSeq is dropped (the window numbers its requests above it).</summary>
sealed record RecruitPrisoner(CharacterId Prisoner, long Seq, int ShownPrice) : RecruitmentUiCommand;
// ---------- end task 6.4a ----------

// ---------- task 4.5a: the Arena window's commands (the window half, 4.5b, sends them; the TEST-ONLY `pg arena` sends the same) ----------
// Each is one C++ ArenaUI.cpp handler, applied in Inputs by src/Game/ArenaUISim.cs. A row command carries the character the window showed in
// that row (never a row index: the roster can change before the command lands); the sim ignores it when no row of that list shows the
// character any more. Fighters as CharacterId (resolved again on the sim thread).

/// <summary>Any Arena window command (ProvingGroundsSystem hands each to the Arena window's sim half).</summary>
abstract record ArenaUiCommand : PgCommand;

/// <summary>C++ OnModeAvB / OnModeTeams1v1 / OnModeLast (ArenaUI.cpp:2050-2075).</summary>
sealed record ArenaSetMode(ProvingGrounds.Core.Glue.ArenaUiMode Mode) : ArenaUiCommand;

/// <summary>C++ OnDestA / OnDestB (:2077-2099): "Assign to Team A" / "Add Selected Fighter" and "Assign to Team B".</summary>
sealed record ArenaSetDest(ProvingGrounds.Core.Glue.ArenaDest Dest) : ArenaUiCommand;

/// <summary>C++ OnPortraitRowClick (:1073-1089): select the row's fighter.</summary>
sealed record ArenaRowClick(ProvingGrounds.Core.Glue.ArenaList List, CharacterId Fighter) : ArenaUiCommand;

/// <summary>C++ OnPortraitRowDoubleClick (:1091-1103): assign a roster row's fighter.</summary>
sealed record ArenaRowDoubleClick(ProvingGrounds.Core.Glue.ArenaList List, CharacterId Fighter) : ArenaUiCommand;

/// <summary>C++ OnPortraitRowRelease (:1234-1295): the mouse released after a press on a row (every release, a plain click too): with
/// <paramref name="Right"/> the right button (remove; a real right-click, task 4.5b), else what the window side
/// hit-tested at the release point: <paramref name="Target"/> = the visible list under it, <paramref name="Prisoner"/> = the character of the
/// team / pool row under it (CharacterId.None when none). Both are sent: a prisoner row lies inside its list, and a fighter that cannot be its
/// handler falls through to the list (:1267-1294).</summary>
sealed record ArenaRowRelease(ProvingGrounds.Core.Glue.ArenaList List, CharacterId Fighter, bool Right, ProvingGrounds.Core.Glue.ArenaDropTarget Target,
    CharacterId Prisoner) : ArenaUiCommand;

/// <summary>C++ OnRemove (:2101-2111): "Remove Fighter" (the selected fighter).</summary>
sealed record ArenaRemoveSelected : ArenaUiCommand;

/// <summary>C++ OnRosterSearchChanged (:1685-1692) and the blur back to the placeholder (:1719-1734, text ""): the search text.</summary>
sealed record ArenaSearch(string Text) : ArenaUiCommand;

/// <summary>C++ OnBalanceMenu (:2219-2225): "Balance" / "Back".</summary>
sealed record ArenaToggleBalanceMenu : ArenaUiCommand;

/// <summary>C++ OnBalanceBySkill / OnBalanceByRating (:2305-2313).</summary>
sealed record ArenaBalance(bool ByRating) : ArenaUiCommand;

/// <summary>C++ OnClearTeams (:2315-2327).</summary>
sealed record ArenaClear : ArenaUiCommand;

/// <summary>C++ OnSwapTeams (:2329-2339).</summary>
sealed record ArenaSwap : ArenaUiCommand;

/// <summary>C++ OnPresetMousePressed (:2201-2217): preset 0-2; <paramref name="Save"/> = the right button (a real right-click, task 4.5b).</summary>
sealed record ArenaPreset(int Index, bool Save) : ArenaUiCommand;

/// <summary>C++ OnLocationArena / OnLocationSmallArena / OnLocationBanner (:1363-1391).</summary>
sealed record ArenaLocation(ProvingGrounds.Core.Glue.ArenaLocationMode Mode) : ArenaUiCommand;

/// <summary>C++ OnMedicalProtocolToggle (:1531-1541).</summary>
sealed record ArenaMedicalToggle : ArenaUiCommand;

/// <summary>C++ OnRecruitPrisoners (:1543-1551).</summary>
sealed record ArenaRecruit : ArenaUiCommand;

/// <summary>C++ OnStart (:2341-2464): "Start Fight".</summary>
sealed record ArenaStart : ArenaUiCommand;

/// <summary>C++ OnStop (:2466-2478): "Stop Fight" / "Cancel Match Start".</summary>
sealed record ArenaStop : ArenaUiCommand;

/// <summary>C++ HidePanel (:2938-2945) asked by the sim side (the TEST-ONLY verb); the window's own close (its button, Escape) reaches the sim as
/// task 4.4's Closed click instead.</summary>
sealed record ArenaClose : ArenaUiCommand;

// ---------- task 4.2: the right click on a Registry / Banner / Leaderboard (src/Game/ContextMenuHooks.cs, spike S3) ----------

/// <summary>A right press the window half swallowed on one of our buildings (C++ showContextMenu_hook, ContextMenuHooks.cpp:133-158):
/// <paramref name="Site"/> = the picked player site (task 4.1's ref: BuildSite.Id + record), <paramref name="Selection"/> = the characters selected
/// at the press. Applied in Inputs by ContextMenuHooks.ApplyInteract (the C++ predicates on the building as it is then, then BeginApproachForUI).
/// Like every mod command it waits while the game is paused (CAP 7.0): a click in a paused game starts the walk when it resumes.</summary>
sealed record ArenaInteract(ProvingGrounds.Core.Glue.ArenaBuildingRef Site, CharacterId[] Selection) : PgCommand;

/// <summary>A right press on an unfinished one of our buildings whose held menu opened (ContextMenuHooks' menu watch): C++ showContextMenu_hook
/// :135-136, :150-151, the building's identity and "interact RMB ignored (not finished)" only. Never starts an approach (even when the building
/// finished in between).</summary>
sealed record ArenaInteractIgnored(ProvingGrounds.Core.Glue.ArenaBuildingRef Site) : PgCommand;

/// <summary>The quick order of a press on an unfinished one of our buildings was dropped (ContextMenuHooks' BuildMode.SiteRelease prefix): C++
/// addOrder_hook :243-247, "interact use ignored (not finished)".</summary>
sealed record ArenaUseIgnored(ProvingGrounds.Core.Glue.ArenaBuildingRef Site) : PgCommand;

/// <summary>Another right-click menu opened (ContextMenuHooks' menu watch): C++ showContextMenu_hook :154-155, NotifyExternalOrder(NULL).</summary>
sealed record ArenaExternalMenu : PgCommand;

// ---------- task 7.5: the town registry window's commands (the window half, 7.6, sends them) ----------

/// <summary>Any town window command (ProvingGroundsSystem hands each to its sim half, src/Town/TownArenaUISim.cs).</summary>
abstract record TownUiCommand : PgCommand;

/// <summary>C++ RowReleased (TownArenaUI.cpp:280-297): the row of the squad member with selection identity <paramref name="Id"/> (never a row index).
/// A <paramref name="Seq"/> at or below the model's LastSeq is dropped (a repeated or late release never toggles twice).</summary>
sealed record TownSelectFighter(string Id, long Seq) : TownUiCommand;

/// <summary>C++ Challenge (TownArenaUI.cpp:321-326): a card's Book button. Runs only while the button is still enabled (the C++'s rule) and the
/// card still shows the drawn offer (<paramref name="ShownOffer"/> = the card view's OfferKey) and quote; a <paramref name="Seq"/> at or below the
/// model's LastSeq is dropped (the window numbers its requests above it).</summary>
sealed record TownBookChallenge(int Slot, long Seq, string ShownOffer, int ShownBuyIn, int ShownPayout) : TownUiCommand;

/// <summary>C++ Book (TownArenaUI.cpp:312-320): "Book direct 1v1" / "Cancel booking", run only while the button still has the drawn caption.</summary>
sealed record TownBookDirect(long Seq, string ShownCaption) : TownUiCommand;

/// <summary>C++ BuyRefresh (TownArenaUI.cpp:327-330): "Buy Refresh (N Cats)", run only while the cost is still the drawn one.</summary>
sealed record TownBuyRefresh(long Seq, int ShownCost) : TownUiCommand;

/// <summary>C++ Division (TownArenaUI.cpp:331-333): a division button (0 Easy, 1 Medium, 2 Hard).</summary>
sealed record TownSetDivision(int Division) : TownUiCommand;
// ---------- end task 7.5 ----------

// ---------- task 8.1a: the bookie windows' commands (the window half, 8.1b, sends them; src/Town/TownBookieUISim.cs applies them) ----------

/// <summary>Any bookie window command (ProvingGroundsSystem hands each to its sim half). Each runs only while the betting window is open, with a
/// <c>Seq</c> above the model's LastSeq, and while the C++ button's enabled rule still holds on arrival (TownBookie.cpp MakeView :131-198).</summary>
abstract record BookieUiCommand : PgCommand;

/// <summary>C++ Select -> SelectSide (TownBookieUI.cpp:147-149, TownBookie.cpp:232-237): Team A (0) or B (1), while canSelect.</summary>
sealed record BookieSelectSide(int Side, long Seq) : BookieUiCommand;

/// <summary>C++ Stake -> ChangeStake (TownBookieUI.cpp:150-152, TownBookie.cpp:238-245): one of the six stake buttons, while it is enabled as drawn
/// and the stake is still <paramref name="ShownStake"/>.</summary>
sealed record BookieChangeStake(int Delta, long Seq, int ShownStake) : BookieUiCommand;

/// <summary>C++ Bet -> PlaceBet (TownBookieUI.cpp:153, TownBookie.cpp:246-250): while canBet and the drawn side and stake are still the window's.</summary>
sealed record BookiePlaceBet(long Seq, int ShownSide, int ShownStake) : BookieUiCommand;

/// <summary>C++ Arrange (TownBookieUI.cpp:154, TownBookie.cpp:251-260): arrange the next NPC bout, while canArrange.</summary>
sealed record BookieArrange(long Seq) : BookieUiCommand;

/// <summary>C++ the close buttons (TownBookieUI.cpp:155-157, TownBookieResultUI.cpp:89-93) -> TownBookie::Close, for the window drawn under
/// <paramref name="ShownSerial"/> (the model's RequestSerial); an older window's close is dropped.</summary>
sealed record BookieClose(int ShownSerial) : BookieUiCommand;
// ---------- end task 8.1a ----------
