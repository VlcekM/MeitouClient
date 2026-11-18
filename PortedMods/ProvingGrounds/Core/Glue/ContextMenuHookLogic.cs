namespace ProvingGrounds.Core.Glue;

// The decisions of upstream/ProvingGrounds/plugin/src/ContextMenuHooks.cpp (Proving Grounds by Shutori, GPLv3), task 4.2 (PA §2.2; CAP 5, 6;
// spike S3), without the game, so they are tested with plain values (Tests/Glue/ContextMenuHookLogicTests.cs). src/Game/ContextMenuHooks.cs binds
// them to Meitou: the right press (a prefix on PlayerInterface.PickedRightDown), the pointer (a postfix on PointerView.PickWithSelection), the
// orders (a prefix on PlayerOrders.Give).

/// <summary>What a right press on the world does (the S3 hook's decision).</summary>
public enum InteractPress
{
    /// <summary>Not ours: the game's own click runs.</summary>
    Vanilla,
    /// <summary>A finished Registry / Banner / Leaderboard: the click is swallowed and the approach starts (C++ showContextMenu_hook :140-150,
    /// addOrder_hook :236-241), or, with nobody selected while a match / ingress / walk-in runs, the window opens at once (BeginApproachForUI
    /// :1182-1186 comes before the selection test :1197-1203).</summary>
    Approach,
    /// <summary>One of ours, not finished: the game's press runs (its held menu still opens, C++ :150-157), only its quick order is dropped
    /// (C++ addOrder_hook :243-247, "interact use ignored (not finished)"; <see cref="ContextMenuHookLogic.DropsQuickOrder"/>).</summary>
    Unfinished,
}

/// <summary>The pure rules of ContextMenuHooks.cpp.</summary>
public static class ContextMenuHookLogic
{
    /// <summary>Meitou's TaskType rows of Kenshi's USE_TRAINING_DUMMY / OPERATE_MACHINERY / OPERATE_AUTOMATIC_MACHINERY /
    /// PRETEND_TO_OPERATE_MACHINERY (Meitou.Data.Gameplay.Ai.TaskType 97, 87, 152, 221: the original's enum values).</summary>
    public const int UseTrainingDummy = 97, OperateMachinery = 87, OperateAutomaticMachinery = 152, PretendToOperateMachinery = 221;

    // C++: ContextMenuHooks.cpp:125-131
    public static bool IsRegistryUseTask(int t) =>
        t == UseTrainingDummy
        || t == OperateMachinery
        || t == OperateAutomaticMachinery
        || t == PretendToOperateMachinery;

    /// <summary>
    /// The right press on the world (S3: <c>Build.PickSite(p)</c> names the site and its record whatever <c>Picking.PickAt(p).Kind</c> says).
    /// <paramref name="pickIsSiteOrNone"/>: the nearest pick is a site or nothing (PlayerInterface.cs:885, the only kinds vanilla gives to
    /// <c>Build.SitePress</c>; a character, item, container, door, bed, seat, cage, dummy or rock in front keeps its own click, as in vanilla).
    /// <paramref name="siteRecord"/>: the picked site's BUILDING record (null: no site); <paramref name="levelPlacement"/> / <paramref name="destroyed"/>:
    /// a town house under repair (M7's town buildings) or a ruin is left to the game. <paramref name="interactRecords"/>: the records ArenaIdentity
    /// reads as a Registry, Banner or Leaderboard (C++ <c>IsInteractUiBuilding</c>, :112-115), published by the simulation.
    /// <paramref name="arenaBusy"/>: a match, a staged fight or a walk-in runs (C++ ShouldOpenInteractUiImmediately's inputs, published): with
    /// nobody selected a finished one is then still ours (BeginApproachForUI opens the window at once before it asks for a selection); otherwise
    /// with nobody selected the press is the game's (vanilla <c>SitePress</c> needs a selection, BuildMode.cs:1250; the C++ approach would answer
    /// "No characters selected").
    /// </summary>
    public static InteractPress DecidePress(bool pickIsSiteOrNone, string? siteRecord, bool complete, bool levelPlacement, bool destroyed,
        int selectedCount, IReadOnlySet<string> interactRecords, bool arenaBusy = false)
    {
        if (!pickIsSiteOrNone || siteRecord is null || levelPlacement || destroyed) return InteractPress.Vanilla;
        if (!interactRecords.Contains(siteRecord)) return InteractPress.Vanilla;
        if (selectedCount <= 0) return complete && arenaBusy ? InteractPress.Approach : InteractPress.Vanilla;
        return complete ? InteractPress.Approach : InteractPress.Unfinished;
    }

    /// <summary>Task 7.6: the right press on a level building (a town's static placement) when no site of the player's took it. The C++
    /// showContextMenu_hook (:133-158) asks IsInteractUiBuilding of whatever Kenshi building is under the cursor, so the Town Registry (record 195,
    /// ArenaIdentity::IsTownRegistry) is approached and opens the town window as a player Registry opens the Arena window (ArenaUI::ShowFromRegistry
    /// routes it), and the town Leaderboard (272, ArenaIdentity::IsLeaderboard) opens the town board (ArenaIngress.cpp:966-980, :997-1004). Meitou picks a level building only through BuildingSelection.PickBuilding, and a plain building takes the right press only when
    /// nothing is in front (<paramref name="nothingInFront"/>, PlayerInterface.cs:885; <see cref="NothingInFront"/>). A destroyed one (a ruin) is left to the game; town buildings
    /// are finished unless under repair (a town building Meitou keeps as a level-placement site: unfinished = Unfinished); with nobody selected the press is ours only while the arena is busy, as a player site's.</summary>
    public static InteractPress DecideTownPress(bool nothingInFront, string? levelRecord, bool isLevel, bool destroyed, bool complete, int selectedCount,
        bool arenaBusy = false)
    {
        bool ours = TownArenaRuntimePolicy.IsTownRegistry(levelRecord)
            || (LeaderboardData.IsLeaderboardId(levelRecord) && LeaderboardData.KindForId(levelRecord) == LeaderboardData.Kind.Town);   // IsInteractUiBuilding :103-106 (the town board)
        if (!nothingInFront || !isLevel || destroyed || levelRecord is null || !ours) return InteractPress.Vanilla;
        if (selectedCount <= 0) return complete && arenaBusy ? InteractPress.Approach : InteractPress.Vanilla;
        return complete ? InteractPress.Approach : InteractPress.Unfinished;   // :127-140: an unfinished one logs "ignored (not finished)", the game's menu opens
    }

    /// <summary>Task 7.6 review: whether the pick in front of a town building leaves the press to the building: nothing (<c>Picked.None</c>), or a seat
    /// that belongs to that building (its operator seat: the town Leaderboard is a machine; <c>SeatSpot.Building</c> = the building's key). Any other
    /// seat (a bench in front of it) is the seat's own click.</summary>
    public static bool NothingInFront(bool pickIsNone, bool pickIsSeat, string? seatBuilding, string? buildingKey) =>
        pickIsNone || (pickIsSeat && seatBuilding is not null && buildingKey is not null && seatBuilding == buildingKey);

    /// <summary>Port only: whether the right press or the pointer must pick the world for the player's sites. With no interact record published (no
    /// Registry / Banner / Leaderboard of the player) and no building suppressed for the pointer, the site pick and the pointer's pick are skipped;
    /// the right press still picks for the town's Registry and Leaderboard (task 7.6, ContextMenuHooks.TownPress).</summary>
    public static bool NeedsWorldPick(int interactRecordCount, string? suppressedKey) => interactRecordCount > 0 || suppressedKey is not null;

    /// <summary>The quick order of a site press released before its menu opened (BuildMode.SiteRelease): dropped for an unfinished one of ours
    /// (C++ addOrder_hook :243-247: Kenshi's quick order on our buildings is a "use" row, getDefaultTask_hook :158-162, swallowed while
    /// unfinished; Meitou's is Build).</summary>
    public static bool DropsQuickOrder(string? siteRecord, bool complete, bool levelPlacement, bool destroyed, IReadOnlySet<string> interactRecords) =>
        siteRecord is not null && !complete && !levelPlacement && !destroyed && interactRecords.Contains(siteRecord);
}

/// <summary>
/// The "use" pointer over our buildings and its reset after the click (C++ getMouseCursor_hook :151-156 / changeMouseCursorTarget_hook :165-171:
/// USE over an interact building, finished or not; Tick :280-288 + RefreshArenaCursor :327-345: after a right press that started an approach,
/// the building is "suppressed": the pointer over it is the game's own until the pointer leaves it, the 1.3 "mouse stuck on gears" fix).
/// Window thread only.
/// </summary>
public sealed class ArenaCursorState
{
    string? suppressed;

    /// <summary>The suppressed building's key (null: none).</summary>
    public string? Suppressed => suppressed;

    /// <summary>C++ showContextMenu_hook :145-146: <c>g_suppressedInteractCursorTarget = building; g_resetInteractCursor = true</c>.</summary>
    public void Suppress(string key) => suppressed = key;

    /// <summary>C++ AbandonWorldState :265-266.</summary>
    public void Reset() => suppressed = null;

    /// <summary>
    /// Whether the pointer shows "use" (C++ RefreshArenaCursor :327-345): over the suppressed building, no (and it stays suppressed); anywhere
    /// else the suppression ends, and over an interact building with a selected character, yes.
    /// </summary>
    /// <param name="keyUnder">The key of the site under the pointer (null: none).</param>
    /// <param name="interact">That site is a Registry, Banner or Leaderboard.</param>
    /// <param name="hasSelection">A character is selected (C++ <c>gui->selectedPlayerCharacter.isValid()</c>).</param>
    public bool WantsUse(string? keyUnder, bool interact, bool hasSelection)
    {
        if (keyUnder is not null && keyUnder == suppressed)
            return false;

        if (suppressed is not null)
            suppressed = null;

        if (!interact)
            return false;
        return hasSelection;
    }
}

// ---- task 5.3a: the money trade with Null (C++ showTradeWindow_hook :173-226, the pending trade UI of Tick :290-325) ----

/// <summary>What a dialogue's trade does (the S4 prefix's decision, C++ showTradeWindow_hook :198-225 after the bookie test).</summary>
public enum TradeCatch
{
    /// <summary>Not Null's: the game's trade window opens.</summary>
    PassThrough,
    /// <summary>Null's, the first participant is the player character: the Rewards window opens for it once the dialogue closes.</summary>
    OpenForFirst,
    /// <summary>Null's, the second participant is the player character.</summary>
    OpenForSecond,
    /// <summary>Null's, but the other participant is no valid player character: the C++ passes through with a debug line (:211-216).</summary>
    NoPlayer,
}

/// <summary>What one pending trade tick does (C++ Tick :290-325).</summary>
public enum PendingTradeStep { None, Wait, Discard, OpenRewards, OpenBookie }

/// <summary>The trade hook's decision (C++ showTradeWindow_hook).</summary>
public static class PendingTradeUi
{
    /// <summary>C++ showTradeWindow_hook :198-216 for a money trade (Meitou's dialogue TRADE opens only the money trade window: divergences.md):
    /// <c>RewardsDialoguePolicy::ResolveTrade</c>, then the participant must be a valid player character.</summary>
    /// <param name="windowOpens">Meitou would open a trade window for this effect (DialogueSystem.Effects.cs:158: only for a speaker with a squad,
    /// <c>mc is { SquadId: >= 0 }</c>). The C++ hook sat in showTradeWindow, so a trade that opens no window is never caught: PassThrough.</param>
    public static TradeCatch DecideTrade(string? firstStringId, string? secondStringId, bool firstIsPlayer, bool secondIsPlayer, bool windowOpens = true)
    {
        if (!windowOpens)
            return TradeCatch.PassThrough;
        var route = RewardsDialoguePolicy.ResolveTrade(firstStringId, secondStringId, moneyTrade: true);
        if (route == RewardsDialoguePolicy.TradeRoute.PassThrough)
            return TradeCatch.PassThrough;
        bool player = route == RewardsDialoguePolicy.TradeRoute.OpenForFirst ? firstIsPlayer : secondIsPlayer;
        if (!player)
            return TradeCatch.NoPlayer;
        return route == RewardsDialoguePolicy.TradeRoute.OpenForFirst ? TradeCatch.OpenForFirst : TradeCatch.OpenForSecond;
    }

}

/// <summary>What a dialogue's trade with the Scratch Bookie does (C++ showTradeWindow_hook :180-196, before Null's test).</summary>
public enum BookieTradeCatch
{
    /// <summary>Neither or both participants are the bookie (or no window would open): Null's test runs next.</summary>
    NotBookie,
    /// <summary>The bookie's money trade with a valid player character first: the betting window opens for it once the dialogue closes.</summary>
    OpenForFirst,
    /// <summary>The same with the player character second.</summary>
    OpenForSecond,
    /// <summary>The bookie's money trade with no valid player character: the C++ runs the game's trade window (:193-194), no line.</summary>
    PassThrough,
}

public static class BookieTradeRule
{
    /// <summary>C++ showTradeWindow_hook :180-196 for a money trade: <paramref name="firstIsBookie"/> = <c>first.isValid() &amp;&amp;
    /// TownBookie::IsBookie(first)</c>; the participant (the other side) must be a valid player character.</summary>
    /// <param name="windowOpens">As <see cref="PendingTradeUi.DecideTrade"/>: Meitou opens no trade window for a speaker without a squad, so the C++
    /// hook (in showTradeWindow) is never reached.</param>
    // C++: ContextMenuHooks.cpp:180-196
    public static BookieTradeCatch Decide(bool firstIsBookie, bool secondIsBookie, bool firstIsPlayer, bool secondIsPlayer, bool windowOpens = true)
    {
        if (!windowOpens || firstIsBookie == secondIsBookie) return BookieTradeCatch.NotBookie;
        bool player = firstIsBookie ? secondIsPlayer : firstIsPlayer;
        if (!player) return BookieTradeCatch.PassThrough;
        return firstIsBookie ? BookieTradeCatch.OpenForSecond : BookieTradeCatch.OpenForFirst;
    }
}

/// <summary>The pending trade UI of ContextMenuHooks.cpp (C++ g_pendingTradeUi, g_pendingTradeCharacter, g_pendingBookie :48-51): queued by the
/// trade hook, opened by Tick once the dialogue is gone (task 5.3a Null's Rewards, task 8.1a the bookie's betting window). Sim thread (the S4 prefix
/// and the frame loop both run in serial steps).</summary>
public sealed class PendingTradeUi<TId>
{
    enum Kind { None, Rewards, Bookie }
    Kind kind;
    TId character = default!;
    TId bookie = default!;

    /// <summary>A trade waits to open its window.</summary>
    public bool Pending => kind != Kind.None;

    /// <summary>C++ :222-224: <c>g_pendingTradeCharacter = participant; g_pendingBookie.setNull(); g_pendingTradeUi = RewardsTradeUi</c>.</summary>
    public void QueueRewards(TId participant)
    {
        character = participant;
        bookie = default!;
        kind = Kind.Rewards;
    }

    /// <summary>C++ :189-191: <c>g_pendingTradeCharacter = participant; g_pendingBookie = the bookie; g_pendingTradeUi = BookieTradeUi</c>.</summary>
    public void QueueBookie(TId participant, TId bookieCharacter)
    {
        character = participant;
        bookie = bookieCharacter;
        kind = Kind.Bookie;
    }

    /// <summary>C++ Tick :290-325 (Null's half; no bookie test).</summary>
    public PendingTradeStep Tick(bool dialogueActive, Func<TId, bool> participantValid, out TId participant) =>
        Tick(dialogueActive, participantValid, _ => false, out participant, out _);

    /// <summary>C++ Tick :290-325. <paramref name="participantValid"/> = <c>preferred &amp;&amp; isValid() &amp;&amp; isPlayerCharacter()</c>;
    /// <paramref name="isBookie"/> = <c>TownBookie::IsBookie(bookie)</c>, asked only for a bookie trade (:302). Wait = nothing yet; Discard / Open*
    /// clear the pending trade first, as the C++.</summary>
    public PendingTradeStep Tick(bool dialogueActive, Func<TId, bool> participantValid, Func<TId, bool> isBookie, out TId participant, out TId bookieCharacter)
    {
        participant = character;
        bookieCharacter = bookie;
        if (kind == Kind.None)
            return PendingTradeStep.None;
        bool openBookie = kind == Kind.Bookie;
        var decision = RewardsDialoguePolicy.ResolvePending(dialogueActive, participantValid(character) && (!openBookie || isBookie(bookie)));
        if (decision == RewardsDialoguePolicy.PendingDecision.WaitForDialogue)
            return PendingTradeStep.Wait;
        Reset();
        if (decision == RewardsDialoguePolicy.PendingDecision.DiscardPending)
            return PendingTradeStep.Discard;
        return openBookie ? PendingTradeStep.OpenBookie : PendingTradeStep.OpenRewards;
    }

    /// <summary>C++ AbandonWorldState :264-266.</summary>
    public void Reset()
    {
        kind = Kind.None;
        character = default!;
        bookie = default!;
    }
}
