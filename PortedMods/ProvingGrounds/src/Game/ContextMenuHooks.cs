using System.Collections.Frozen;
using System.Linq.Expressions;
using System.Numerics;
using Meitou.Data.Gameplay.Dialogue;
using Meitou.Modding;
using Meitou.Simulation;
using Meitou.Simulation.Construction;
using Meitou.Simulation.Player;
using Meitou.Simulation.Population;
using ProvingGrounds.Core;
using ProvingGrounds.Core.Glue;
using ProvingGrounds.Engine;

namespace ProvingGrounds.Game;

// Port of upstream/ProvingGrounds/plugin/src/ContextMenuHooks.h/.cpp (Proving Grounds by Shutori, GPLv3), task 4.2 (PA §2.2; CAP 5, 6; spike S3,
// VERIFIED live 2026-11-11). Meitou 0.17.3 (tag v0.17.3, fa34a1b). The decisions are in Core/Glue/ContextMenuHookLogic.cs (tested,
// Tests/Glue/ContextMenuHookLogicTests.cs); the approach itself is ArenaIngress's (Core/Glue/ArenaIngressLogic.cs BeginApproachForUI,
// NotifyExternalOrder). This file has a window half (static: the right press, the pointer, the menu watch; window thread, never the world) and a
// sim half (one object per world: the records the window recognises, the commands the window sends, the order hook; serial steps).
//
// The C++ functions, in order, and what replaces each:
//   StringId / IsMatchUiOpenerHand / IsLeaderboardHand / IsInteractUiHand / SameRootObject / IsSuppressedInteractCursorTarget (:58-110) ... the
//       window half compares the picked site's key (BuildingSnapshot.Id) and record with the published InteractRecords; the sim half asks
//       ArenaIdentityLogic.IsMatchUiOpener / IsLeaderboard of the resolved building
//   IsInteractUiBuilding (:112-115), IsInteractUiFinished (:117-121) ... InteractBuilding / ArenaIdentityLogic.IsMatchUiOpenerFinished ||
//       IsLeaderboardFinished, below
//   IsRegistryUseTask (:125-131) ... ContextMenuHookLogic.IsRegistryUseTask (Meitou's TaskType rows 97, 87, 152, 221)
//   showContextMenu_hook (:133-158), the right-click menu of Kenshi ... spike S3: a prefix on private PlayerInterface.PickedRightDown(Vector2, bool,
//       bool) (PlayerInterface.cs:870) keyed on Build.PickSite(p) (BuildMode.cs:1014: the site, its record, Complete): a finished Registry /
//       Banner / Leaderboard is swallowed (return false: no SitePress, so no move order, no Operate, no menu) and sends ArenaInteract (the site's
//       ArenaBuildingRef, task 4.1: player BuildSite.Id, and the selection); the sim logs LogInteractIdentity "RMB" and starts
//       BeginApproachForUI next tick (CAP 7.0). The cursor is suppressed as :145-146. With nobody selected the press is still ours while a match,
//       ingress or walk-in runs (the published InteractBusy: BeginApproachForUI opens the window at once before it asks for a selection,
//       :1182-1186). An unfinished one of ours keeps the game's press (C++ :150-157: Kenshi's held menu still opens; Meitou's held menu is
//       BuildMode.UpdateSite, :1358-1366); only its quick order is dropped, by a prefix on BuildMode.SiteRelease (:1266; C++ addOrder_hook
//       :243-247, "interact use ignored (not finished)"). A right press on anything else is the game's; when it opens a menu (any
//       ContextMenuWindow.OpenMenu: Meitou opens a site's, a character's, a bed's ... menu on a held press), the window half's menu watch sends
//       ArenaExternalMenu = NotifyExternalOrder(NULL) (:154-155), after an ArenaInteractIgnored when the press was on an unfinished one of ours
//       (the C++'s identity line and "interact RMB ignored (not finished)", :135-136, :150-151; never an approach).
//       Order of the two right-press handlers: PlayerInterface.MouseDown (PlayerInterface.cs:779-800) reaches PickedRightDown only through
//       WorldRightDown, only when !OverHud(p) (:797), and OverHud = ContainsUi = FaithfulScreens.Contains (PlayerInterface.Ui.cs:646-651), which
//       holds our windows; ScreenHost's MouseDown postfix acts only on a press over one of our windows. The prefix also leaves a press over one
//       of our windows to the windows (the interface hidden: OverHud false), so one press never reaches both.
//   getMouseCursor_hook (:151-156), changeMouseCursorTarget_hook (:160-166), RefreshArenaCursor (:327-345), the reset in Tick (:280-288) ...
//       CAP 6: a postfix on private PointerView.PickWithSelection() (PointerView.cs:175, the pointer with characters selected): "use" over an
//       interact building (finished or not), the game's own pointer over the building just clicked until the pointer leaves it
//       (Core/Glue/ArenaCursorState). Window thread.
//   getDefaultTask_hook (:158-162: USE_TRAINING_DUMMY for our buildings) ... none: Meitou has no default task of a building; its right press is
//       the S3 prefix above (the quick order and the menu are both behind it)
//   changeMouseCursor_hook (:168-171) ... none (a plain pass-through in the C++)
//   showTradeWindow_hook (:173-226), ForgottenGUI::showTradeWindow of Kenshi ... spike S4 (VERIFIED live 2026-11-11, task 5.3a): a prefix on
//       private DialogueSystem.ConversationEffect(World, Conversation, DialogueEffect, CharacterId me, CharacterCold? mc, CharacterId target)
//       (DialogueSystem.Effects.cs:150; Conversation taken as object), the only place a dialogue's TRADE (DialogueActions.Trade) opens the trade
//       window: it ends the conversation and defers OpenTrade(target, me) (:155-159), so first = target (the player side), second = me (the
//       speaker). For Null's trade (RewardsDialoguePolicy.ResolveTrade on the two record ids, Core PendingTradeUi.DecideTrade) with a valid player
//       character on the other side the prefix sets the nested Conversation.Ended (DialogueSystem.cs:338) by reflection, queues the pending
//       Rewards UI, answers true (the line ends) and skips the original, so no trade window is deferred. Everything else (another speaker, no
//       player participant: the C++ debug line) runs the game's trade. Meitou's dialogue TRADE opens only the money trade window (divergences.md),
//       so the C++ TW_MONEY_TRADING test is always true here. The bookie half (:180-196, task 8.1a): the Scratch Bookie's 111 trade (spike S4: its
//       TRADE is directly on the choice "Show me the board before somebody dies.") with a valid player character on the other side ends the
//       conversation the same way and queues the betting window (Core BookieTradeRule; with no player character the game's trade runs, as the C++).
//   the pending trade UI of Tick (:290-325) ... TickPendingTrade below (Core PendingTradeUi; ForgottenGUI::inDialogue -> PgEngine.InDialogue,
//       the world's DialogueSystem still publishing a conversation): RewardsUI::Show(preferred) / TownBookie::Show(bookie) (src/Town/TownBookie.cs,
//       task 8.1a; IsBookie = PgRecords.Bookie on a live character) once the conversation is gone
//   addOrder_hook (:228-258), Character::addOrder of Kenshi ... CAP 5: a prefix on public static PlayerOrders.Give(World, int, OrderEntry, bool,
//       bool) (OrderQueue.cs:224), the one entry every player order reaches (MoveOrder through MovementSystem.GiveMove :357-391, Operate,
//       Attack, Build, ...): a "use" row (IsRegistryUseTask) whose Site is a player site ArenaIdentity reads as a finished Registry / Banner /
//       Leaderboard is swallowed (Give answers OrderCheck.Gone, nothing queued) and starts BeginApproachForUI; an unfinished one is swallowed;
//       any other order to a character calls NotifyExternalOrder(that character) first, except the approach's own move (the C++
//       g_issuingManagedMove: Meitou applies a MoveOrder a tick later, so ArenaIngress.ConsumeOwnApproachMove recognises it by its walker and
//       target; a leftover of an approach that ended in between is dropped)
//   AbandonWorldState (:262-269) ... the window half resets its cursor state when the world (the system) changes; the pending trade is this
//       object's (a new world builds a new one)
//   Tick (:271-325) ... the sim slot ContextMenuHooks.Tick keeps the frame-loop order and publishes InteractRecords (port only: the window must
//       know a record without reading the world); the cursor reset is the window half's
//   Install (:347-418) ... Install below (PgPatches; the C++ failure texts for the hooks that exist here)
//   PGLog::Debug ... PGLog.Debug, the C++ texts byte for byte

/// <summary>The glue of the original ContextMenuHooks.cpp: the sim half (one per world, owned by ProvingGroundsSystem) and, as statics, the window
/// half and the patches.</summary>
sealed class ContextMenuHooks(ArenaIngress ingress, PopulationData data, Ui.RewardsUI rewards, Town.TownBookie bookie)
{
    // ==================== sim half (serial steps) ====================

    readonly PendingTradeUi<CharacterId> pendingTrade = new();   // C++ g_pendingTradeUi / g_pendingTradeCharacter / g_pendingBookie :48-51

    readonly Dictionary<string, bool> classified = new(StringComparer.Ordinal);   // record -> ArenaIdentity reads it as an interact building
    FrozenSet<string> interactRecords = FrozenSet<string>.Empty;
    int lastSiteCount = -1;
    long lastScanTick = -1000;

    /// <summary>The BUILDING records of the player's sites that ArenaIdentity reads as a Registry, Banner or Leaderboard (C++ IsInteractUiBuilding),
    /// published in the snapshot for the window half (a new set only when one was added).</summary>
    internal IReadOnlySet<string> InteractRecords => interactRecords;

    /// <summary>The F10 phase row: whether the right-press part is on and how many interact records the window knows.</summary>
    internal string Phase => (RightPressOn ? "right-click on" : "right-click off") + (TradeOn ? ", Null's trade on" : ", Null's trade off")
        + (pendingTrade.Pending ? " (a trade waits for the dialogue)" : "") + ", "
        + interactRecords.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + " interact building records";

    // C++: ContextMenuHooks.cpp:112-115
    static bool IsInteractUiBuilding(ArenaBuilding? obj) =>
        obj != null && (ArenaIdentityLogic.IsMatchUiOpener(obj) || ArenaIdentityLogic.IsLeaderboard(obj));

    // C++: ContextMenuHooks.cpp:117-121
    static bool IsInteractUiFinished(ArenaBuilding building) =>
        ArenaIdentityLogic.IsMatchUiOpenerFinished(building)
        || ArenaIdentityLogic.IsLeaderboardFinished(building);

    /// <summary>C++: ArenaUI.cpp:3151 ContextMenuHooks::Tick (ContextMenuHooks.cpp:271-325). Port only: classifies each new record among the
    /// player's sites (once per record per world; scanned when the site count changes, else once a second) and publishes InteractRecords. The C++
    /// Tick's cursor reset runs on the window thread (AfterPickWithSelection); its trade part is <see cref="TickPendingTrade"/>.</summary>
    internal void Tick(World world)
    {
        TickPendingTrade(world);   // C++ :290-325 (the cursor reset before it, :280-288, is the window half's)
        var sites = world.Buildings.All;
        bool due = sites.Count != lastSiteCount || world.Tick < lastScanTick || world.Tick - lastScanTick >= 30;   // a load restarts the tick
        if (!due) return;
        lastSiteCount = sites.Count;
        lastScanTick = world.Tick;
        bool added = false;
        Meitou.Simulation.Production.ProductionSystem? production = null;
        for (int i = 0; i < sites.Count; i++)
        {
            string record = sites[i].Record;
            if (classified.ContainsKey(record)) continue;
            production ??= PgEngine.Production(world);
            bool interact = IsInteractUiBuilding(ArenaIdentity.From(PgEngine.FromPlayer(sites[i]), production));
            classified[record] = interact;
            added |= interact;
        }
        if (added) interactRecords = classified.Where(kv => kv.Value).Select(kv => kv.Key).ToFrozenSet(StringComparer.Ordinal);
    }

    /// <summary>C++: ContextMenuHooks.cpp:290-325, the pending trade UI of Tick: once the conversation view is gone, the Rewards window opens for the
    /// player character that traded with Null (or the stale request is dropped); the betting window for the bookie (task 8.1a).</summary>
    void TickPendingTrade(World world)
    {
        if (!pendingTrade.Pending) return;
        var step = pendingTrade.Tick(PgEngine.InDialogue(world),
            c => !c.IsNone && PgEngine.Exists(world, c) && PgEngine.IsPlayer(world, c), c => Town.TownBookie.IsBookie(world, c),
            out CharacterId preferred, out CharacterId pendingBookie);
        switch (step)
        {
            case PendingTradeStep.Discard:
                PGLog.Debug("Proving Grounds: discarded stale trade UI request");
                break;
            case PendingTradeStep.OpenRewards:
                rewards.Show(world, preferred);
                PGLog.Debug("Proving Grounds: rewards UI opened from dialogue");
                break;
            case PendingTradeStep.OpenBookie:   // C++ :315-319 (task 8.1a)
                bookie.Show(world, pendingBookie);
                PGLog.Debug("Proving Grounds: bookie UI requested from dialogue");
                break;
        }
    }

    /// <summary>C++ showTradeWindow_hook (:173-226) for one dialogue TRADE (the S4 prefix, serial): true = the original runs (the game's trade
    /// window), false = caught (the conversation ended, the Rewards window queued).</summary>
    internal bool BeforeTrade(World world, object conversation, CharacterId me, CharacterCold? mc, CharacterId target)
    {
        string? firstId = target.IsNone ? null : PgEngine.RecordId(world, target);   // first = the player side of OpenTrade(target, me)
        string? secondId = mc?.RecordId;
        bool firstPlayer = !target.IsNone && PgEngine.Exists(world, target) && PgEngine.IsPlayer(world, target);
        bool secondPlayer = !me.IsNone && PgEngine.Exists(world, me) && mc?.IsPlayer == true;
        // C++ :178-196 the bookie's money trade (task 8.1a): first.isValid() && IsBookie(first), the same for second
        bool firstBookie = !target.IsNone && PgEngine.Exists(world, target) && firstId == PgRecords.Bookie;
        bool secondBookie = !me.IsNone && PgEngine.Exists(world, me) && secondId == PgRecords.Bookie;
        var bookieDecision = BookieTradeRule.Decide(firstBookie, secondBookie, firstPlayer, secondPlayer, windowOpens: mc is { SquadId: >= 0 });
        if (bookieDecision == BookieTradeCatch.PassThrough) return true;   // :193-194: the game's trade window, no line
        if (bookieDecision != BookieTradeCatch.NotBookie)
        {
            if (Members.ConversationEnded is not { } bookieEnded) return true;
            bookieEnded.SetValue(conversation, true);   // end first, queue last (as Null's below)
            if (bookieDecision == BookieTradeCatch.OpenForFirst) pendingTrade.QueueBookie(target, me);
            else pendingTrade.QueueBookie(me, target);
            PGLog.Debug("Proving Grounds: bookie trade intercepted; queued until dialogue closes");
            return false;
        }
        // only when Meitou would open a trade window (DialogueSystem.Effects.cs:158 `if (mc is { SquadId: >= 0 })`): the C++ hook is showTradeWindow
        var decision = PendingTradeUi.DecideTrade(firstId, secondId, firstPlayer, secondPlayer, windowOpens: mc is { SquadId: >= 0 });
        if (decision == TradeCatch.PassThrough) return true;
        if (decision == TradeCatch.NoPlayer)
        {
            PGLog.Debug("Proving Grounds: rewards dialogue trade had no player participant; passing through");
            return true;
        }
        // Never null here: InstallTrade applies this prefix only when every RewardsTrade member resolved; the check keeps a broken install harmless
        if (Members.ConversationEnded is not { } ended) return true;
        // Order: end the conversation first, queue last. A throw while ending (the prefix then passes through) leaves nothing queued, so the game's
        // trade window opens alone; queueing first and failing to end would open the game's trade window AND ours.
        ended.SetValue(conversation, true);   // what the original's Trade case does first (DialogueSystem.Effects.cs:157), without OpenTrade
        pendingTrade.QueueRewards(decision == TradeCatch.OpenForFirst ? target : me);   // cannot throw (two field writes); nothing follows it
        PGLog.Debug("Proving Grounds: rewards dialogue trade intercepted; queued until GUI focus is clear");
        return false;
    }

    /// <summary>TEST ONLY (the scenario's check): a trade waits for the dialogue to close.</summary>
    internal bool TradePending => pendingTrade.Pending;

    /// <summary>C++: ArenaUI.cpp:3152 ContextMenuHooks::RefreshArenaCursor. Kept as the frame-loop slot; the cursor is the window's
    /// (AfterPickWithSelection: Meitou's pointer is chosen on the window thread, PointerView.cs:124-151).</summary>
    internal void RefreshArenaCursor(World world) { }

    /// <summary>The right press the window half swallowed (sim, serial Inputs): C++ showContextMenu_hook :133-150 with the building as it is now.
    /// The window decided from its own snapshot of the site; here the building is resolved again and the C++ predicates decide.</summary>
    internal string ApplyInteract(World world, ArenaInteract command)
    {
        ArenaBuilding? building = ArenaIdentity.Resolve(world, data, command.Site);
        if (building is { } b0)
            ArenaIdentity.LogInteractIdentity(b0, "RMB");

        if (building is { } b && IsInteractUiBuilding(b))
        {
            if (IsInteractUiFinished(b))
            {
                PGLog.Debug("Proving Grounds: interact RMB → approach for UI (no context menu)");
                string walkers = WalkersText(world, b, command.Selection);   // port only: who starts where, and the walk-up point
                bool ok = ingress.BeginApproachForUI(world, b, command.Selection);
                return (ok ? "approach: " : "approach refused: ") + ingress.GetStatus() + "; " + walkers;
            }
            PGLog.Debug("Proving Grounds: interact RMB ignored (not finished)");
            return "ignored (not finished)";
        }
        return "not an interact building";
    }

    /// <summary>Port only (the PG-INTERACT line): each selected character's position, its running task and its walk-up point at the press.</summary>
    string WalkersText(World world, ArenaBuilding b, CharacterId[] selection)
    {
        var sb = new System.Text.StringBuilder("walkers");
        foreach (var id in selection)
        {
            sb.Append(' ').Append(CharacterIdText.Format(id));
            if (PgEngine.Position(world, id) is not { } at) { sb.Append(" gone"); continue; }
            var t = ingress.ApproachPointFor(world, b, id);
            sb.Append(" at ").Append(CMath.FormatF(at.X, 1)).Append(',').Append(CMath.FormatF(at.Z, 1))
              .Append(" task ").Append(PgEngine.TaskText(world, id))
              .Append(" target ").Append(CMath.FormatF(t.X, 1)).Append(',').Append(CMath.FormatF(t.Z, 1))
              .Append(" dist ").Append(CMath.FormatF(MathF.Sqrt(ArenaFormation.HorizontalDistSq(new Vec3(at.X, at.Y, at.Z), t)), 1));
        }
        return sb.ToString();
    }

    /// <summary>A right press on an unfinished one of ours whose held menu opened (the window half's menu watch): C++ showContextMenu_hook
    /// :135-136, :150-151 logs the building's identity and "ignored (not finished)". Only those lines: never an approach, even when the building
    /// finished in between (the press the C++ judged was on an unfinished building).</summary>
    internal string ApplyInteractIgnored(World world, ArenaInteractIgnored command)
    {
        if (ArenaIdentity.Resolve(world, data, command.Site) is { } b)
            ArenaIdentity.LogInteractIdentity(b, "RMB");
        PGLog.Debug("Proving Grounds: interact RMB ignored (not finished)");
        return "ignored (not finished)";
    }

    /// <summary>The quick order of an unfinished one of ours was dropped (the window half's SiteRelease prefix): C++ addOrder_hook :243-247.</summary>
    internal string ApplyUseIgnored()
    {
        PGLog.Debug("Proving Grounds: interact use ignored (not finished)");
        return "use ignored (not finished)";
    }

    /// <summary>Another right-click menu opened (the window half's menu watch): C++ showContextMenu_hook :154-155 NotifyExternalOrder(NULL).</summary>
    internal string ApplyExternalMenu(World world)
    {
        ingress.NotifyExternalOrder(world, CharacterId.None);
        return "menu opened: " + ingress.GetStatus();
    }

    /// <summary>C++ addOrder_hook (:228-258) for one order given to <paramref name="slot"/> (PlayerOrders.Give prefix, serial). False = swallow
    /// the order.</summary>
    internal bool BeforeOrder(World world, int slot, OrderEntry order)
    {
        ArenaBuilding? building = null;
        if (order.Site is { } siteId && world.Buildings.Find(siteId) is { } site && PgEngine.IsListed(site)
            && classified.TryGetValue(site.Record, out bool interact) && interact)
            building = ArenaIdentity.From(PgEngine.FromPlayer(site), PgEngine.Production(world));

        var character = new CharacterId(slot, world.Characters.Next[slot].Generation);
        if (building is { } b)
        {
            // Same Registry pattern: swallow use/operate, walk-then-open when finished.
            if (IsInteractUiFinished(b) && ContextMenuHookLogic.IsRegistryUseTask((int)order.Row))
            {
                PGLog.Debug("Proving Grounds: interact use → approach for UI");
                ingress.BeginApproachForUI(world, b);
                return false;
            }

            if (ContextMenuHookLogic.IsRegistryUseTask((int)order.Row))
            {
                PGLog.Debug("Proving Grounds: interact use ignored (not finished)");
                return false;
            }
        }

        // the approach's own move (the C++ g_issuingManagedMove guard, see the header): never a cancel; a leftover of an approach that ended in
        // the tick between is dropped (port only: it would undo the order that cancelled the approach)
        if (order.IsMove && ingress.ConsumeOwnApproachMove(character, order.Point, out bool stale))
            return !stale;
        ingress.NotifyExternalOrder(world, character);
        return true;
    }

    // ==================== patches ====================

    static ModContext? ctx;
    static readonly HashSet<string> logged = new(StringComparer.Ordinal);

    static void LogOnce(string what, Exception e)
    {
        lock (logged)
            if (logged.Add(what + "|" + e.GetType().FullName)) ctx?.Log($"interact {what} failed: {e.GetType().Name}: {e.Message}");
    }

    /// <summary>The patches (applied by <see cref="Install"/> and by PgCheck hooks, through PgPatches.Apply).</summary>
    internal static IEnumerable<PgPatch> Patches() =>
    [
        new(Part.Interact, "PlayerInterface.PickedRightDown prefix (a right press on a Registry / Banner / Leaderboard)", Members.PickedRightDown,
            PgPatches.Method(typeof(ContextMenuHooks), nameof(BeforePickedRightDown)), null),
        new(Part.Interact, "BuildMode.SiteRelease prefix (no quick order on an unfinished Registry / Banner / Leaderboard)", Members.SiteRelease,
            PgPatches.Method(typeof(ContextMenuHooks), nameof(BeforeSiteRelease)), null),
        new(Part.Interact, "PlayerOrders.Give prefix (use orders on our buildings; a new order cancels an approach)", Members.OrdersGive,
            PgPatches.Method(typeof(ContextMenuHooks), nameof(BeforeGive)), null),
        new(Part.InteractCursor, "PointerView.PickWithSelection postfix (the use pointer over our buildings)", Members.PickWithSelection,
            null, PgPatches.Method(typeof(ContextMenuHooks), nameof(AfterPickWithSelection))),
        new(Part.RewardsTrade, "DialogueSystem.ConversationEffect prefix (Null's trade opens the Rewards window; spike S4)", Members.ConversationEffect,
            PgPatches.Method(typeof(ContextMenuHooks), nameof(BeforeConversationEffect)), null),
    ];

    /// <summary>C++: ContextMenuHooks.cpp:347-418 Install. A missing member turns its part off with one line; without the right-press part the
    /// buildings answer as without the mod; without the pointer part the pointer stays the game's.</summary>
    internal static void Install(ModContext c)
    {
        ctx = c;
        bool ok = true;
        foreach (var part in new[] { Part.Interact, Part.InteractCursor })
        {
            try
            {
                var missing = Members.Missing(part).ToList();
                string? why = missing.Count > 0 ? string.Join(", ", missing) + " not found" : Prepare(part);
                if (why is null)
                {
                    // all of a part's patches or none: a patch that throws after others went on takes those off again, so the log line that
                    // says the part is off is true (the flag is set only after every patch applied)
                    var applied = new List<PgPatch>();
                    try
                    {
                        foreach (var p in Patches().Where(p => p.Part == part)) { PgPatches.Apply(c.Harmony, p); applied.Add(p); }
                    }
                    catch
                    {
                        foreach (var p in applied)
                        {
                            try { PgPatches.Remove(c.Harmony, p); }
                            catch (Exception e) { c.Log($"building right-click part {part}: could not take off {p.What}: {e.GetType().Name}: {e.Message}"); }
                        }
                        throw;
                    }
                    if (part == Part.Interact) RightPressOn = true;
                    continue;
                }
                c.Log((part == Part.Interact ? "building right-click off: " : "building use pointer off: ") + why);
                if (part == Part.Interact)
                {
                    ok = false;
                    PGLog.Error("Proving Grounds: failed to hook ContextMenu::showContextMenu");
                    PGLog.Error("Proving Grounds: failed to hook Character::addOrder");
                }
                else
                {
                    PGLog.Error("Proving Grounds: failed to hook Building::getMouseCursor");
                    PGLog.Error("Proving Grounds: failed to hook ForgottenGUI::changeMouseCursor(target)");
                }
            }
            catch (Exception e)
            {
                c.Log($"building right-click part {part} off: {e.GetType().Name}: {e.Message}");
                if (part == Part.Interact) ok = false;
            }
        }
        InstallTrade(c);
        PGLog.Debug("Proving Grounds: registry/banner/leaderboard interact hooks install done");
        if (!ok) c.Log("building right-click: the Registry / Banner / Leaderboard answer the right click as without the mod");
    }

    /// <summary>C++ Install :399-406 (showTradeWindow): the S4 prefix, or one line and the C++ failure text (Null's trade then opens the game's trade
    /// window).</summary>
    static void InstallTrade(ModContext c)
    {
        try
        {
            var missing = Members.Missing(Part.RewardsTrade).ToList();
            if (missing.Count == 0)
            {
                foreach (var p in Patches().Where(p => p.Part == Part.RewardsTrade)) PgPatches.Apply(c.Harmony, p);
                TradeOn = true;
                return;
            }
            c.Log($"Null's trade -> Rewards off: {string.Join(", ", missing)} not found");
        }
        catch (Exception e) { c.Log($"Null's trade -> Rewards off: {e.GetType().Name}: {e.Message}"); }
        PGLog.Error("Proving Grounds: failed to hook ForgottenGUI::showTradeWindow");
    }

    /// <summary>The trade part is applied (the F10 phase row).</summary>
    internal static volatile bool TradeOn;

    /// <summary>Prefix on private DialogueSystem.ConversationEffect(World world, Conversation c, DialogueEffect effect, CharacterId me,
    /// CharacterCold? mc, CharacterId target) (DialogueSystem.Effects.cs:150; spike S4), serial phases: Null's TRADE (see the header). False = the
    /// original is skipped with <paramref name="__result"/> true (the line ends; no OpenTrade deferred). Respects another prefix's skip; never
    /// throws into the game. Fires only on a TRADE line (a leave line logs no TRADE effect, S4 run 1).</summary>
    static bool BeforeConversationEffect(World world, object c, DialogueEffect effect, CharacterId me, CharacterCold? mc, CharacterId target,
        ref bool __result, bool __runOriginal)
    {
        using var perfScope = PgPerf.Measure(PgPerf.ConversationEffect);   // task 9.2a: the cost instrument (off: one volatile read)
        if (!__runOriginal || effect.Action != DialogueActions.Trade) return true;
        try
        {
            if (ProvingGroundsSystem.InteractFor(world) is not { } hooks) return true;
            if (hooks.BeforeTrade(world, c, me, mc, target)) return true;
            __result = true;
            return false;
        }
        catch (Exception e) { LogOnce("trade", e); return true; }
    }

    /// <summary>The right-press part is applied (the F10 row; the scenario reads it).</summary>
    internal static volatile bool RightPressOn;

    // compiled reads of the interface (window thread; no boxing per press beyond the snapshot record)
    static Func<object, Vector2, BuildingSnapshot?>? pickSite;
    static Func<object, Vector2, int>? pickKind;
    static Func<object, int>? selectedCount;
    // task 7.6: the level building under the pixel (BuildingSelection.PickBuilding): its placement key, BUILDING record, site id (null for a level
    // building) and Destroyed; null when the reads did not compile (the town route is off, the player sites' route stays)
    static Func<object, Vector2, object?>? pickBuilding;
    static Func<object, string?>? buildingInstance, buildingRecord, buildingSiteId;
    static Func<object, bool>? buildingDestroyed;
    static Func<object, Vector2, Meitou.Simulation.Locks.SeatSpot?>? pickSeat;   // task 7.6 review: the seat in front and its building
    static Func<object, IReadOnlyList<CharacterId>>? selectedIds;
    static int kindNone, kindSite, kindSeat = -1;
    static Func<object, BuildingSnapshot?>? pendingSite;   // build -> build._sitePending?.Site (the BuildMode SiteRelease runs on)
    static Action<object>? clearPending;                  // build._sitePending = null

    /// <summary>Every compiled read dropped (a failed Prepare leaves none half-made: the patches test them for null).</summary>
    static void ResetReads()
    {
        pickSite = null; pickKind = null; selectedCount = null; selectedIds = null; pendingSite = null; clearPending = null;
        pickBuilding = null; buildingInstance = buildingRecord = buildingSiteId = null; buildingDestroyed = null; pickSeat = null;
    }

    /// <summary>Compiles the reads a part needs; null on success, else what is missing.</summary>
    internal static string? Prepare(Part part)
    {
        try
        {
            if (part == Part.InteractCursor) return pickSite is null ? "the right-press reads did not compile" : null;
            var ui = Expression.Parameter(typeof(object), "ui");
            var p = Expression.Parameter(typeof(Vector2), "p");
            var typed = Expression.Convert(ui, Members.PickedRightDown!.DeclaringType!);
            var build = Expression.Property(typed, Members.BuildProp!);
            var site = Expression.Call(build, Members.PickSite!, p);
            pickSite = Expression.Lambda<Func<object, Vector2, BuildingSnapshot?>>(Expression.TypeAs(site, typeof(BuildingSnapshot)), ui, p).Compile();
            var picking = Expression.Property(typed, Members.PickingProp!);
            var pick = Expression.Call(picking, Members.PickAt!, p);
            var kind = Expression.Field(pick, "Item1");
            pickKind = Expression.Lambda<Func<object, Vector2, int>>(Expression.Convert(kind, typeof(int)), ui, p).Compile();
            var ids = Expression.Call(typed, Members.SelectedIds!);
            var idsOf = Expression.Lambda<Func<object, IReadOnlyList<CharacterId>>>(ids, ui).Compile();
            selectedIds = idsOf;
            selectedCount = o => idsOf(o).Count;
            var bm = Expression.Parameter(typeof(object), "build");
            var pend = Expression.Field(Expression.Convert(bm, Members.BuildProp!.PropertyType), Members.SitePending!);
            if (Nullable.GetUnderlyingType(pend.Type) is null) { ResetReads(); return "BuildMode._sitePending is not nullable"; }
            var pendSite = Expression.Condition(Expression.Property(pend, "HasValue"),
                Expression.TypeAs(Expression.Field(Expression.Property(pend, "Value"), "Item1"), typeof(BuildingSnapshot)),
                Expression.Constant(null, typeof(BuildingSnapshot)));
            pendingSite = Expression.Lambda<Func<object, BuildingSnapshot?>>(pendSite, bm).Compile();
            clearPending = Expression.Lambda<Action<object>>(Expression.Assign(pend, Expression.Default(pend.Type)), bm).Compile();
            var picked = kind.Type;
            if (!picked.IsEnum || !Enum.IsDefined(picked, "None") || !Enum.IsDefined(picked, "Site")) { ResetReads(); return "WorldPicking.Picked has no None / Site"; }
            kindNone = Convert.ToInt32(Enum.Parse(picked, "None"));
            kindSite = Convert.ToInt32(Enum.Parse(picked, "Site"));
            kindSeat = Enum.IsDefined(picked, "Seat") ? Convert.ToInt32(Enum.Parse(picked, "Seat")) : -1;   // task 7.6 review: a machine's own seat (below)
            PrepareTownPick(typed, ui, p);
            return null;
        }
        catch (Exception e) { ResetReads(); return $"{e.GetType().Name}: {e.Message}"; }
    }

    /// <summary>Task 7.6: compiles BuildingSelection.PickBuilding and the SelectedBuilding reads (the Town Registry's right press). A failure leaves
    /// them null (one log line): the player sites' route is not affected.</summary>
    static void PrepareTownPick(Expression typed, ParameterExpression ui, ParameterExpression p)
    {
        try
        {
            if (Members.BuildingSelectionProp is null || Members.PickBuilding is null || Members.SelectedBuildingInstance is null
                || Members.SelectedBuildingRecord is null || Members.SelectedBuildingSiteId is null || Members.SelectedBuildingDestroyed is null)
            { ProvingGroundsMod.Ctx?.Log("Proving Grounds: the Town Registry right-click is off (BuildingSelection.PickBuilding not found)"); return; }
            var selection = Expression.Property(typed, Members.BuildingSelectionProp);
            var picked = Expression.Call(selection, Members.PickBuilding, p);
            pickBuilding = Expression.Lambda<Func<object, Vector2, object?>>(Expression.Convert(picked, typeof(object)), ui, p).Compile();
            var b = Expression.Parameter(typeof(object), "b");
            var typedB = Expression.Convert(b, Members.PickBuilding.ReturnType);
            Func<object, string?> Str(System.Reflection.PropertyInfo prop) =>
                Expression.Lambda<Func<object, string?>>(Expression.Convert(Expression.Property(typedB, prop), typeof(string)), b).Compile();
            buildingInstance = Str(Members.SelectedBuildingInstance);
            buildingRecord = Str(Members.SelectedBuildingRecord);
            buildingSiteId = Str(Members.SelectedBuildingSiteId);
            buildingDestroyed = Expression.Lambda<Func<object, bool>>(Expression.Convert(Expression.Property(typedB, Members.SelectedBuildingDestroyed), typeof(bool)), b).Compile();
            if (Members.FurnitureProp is not null && Members.PickSeat is not null)
                pickSeat = Expression.Lambda<Func<object, Vector2, Meitou.Simulation.Locks.SeatSpot?>>(Expression.TypeAs(Expression.Call(
                    Expression.Property(typed, Members.FurnitureProp), Members.PickSeat, p), typeof(Meitou.Simulation.Locks.SeatSpot)), ui, p).Compile();
        }
        catch (Exception e)
        {
            pickBuilding = null; buildingInstance = buildingRecord = buildingSiteId = null; buildingDestroyed = null; pickSeat = null;
            ProvingGroundsMod.Ctx?.Log($"Proving Grounds: the Town Registry right-click is off ({e.GetType().Name}: {e.Message})");
        }
    }

    /// <summary>PgCheck only: whether the town pick reads compiled.</summary>
    internal static bool TownPickReady => pickBuilding is not null;

    // ==================== window half (window thread; never the world) ====================

    static readonly ArenaCursorState cursor = new();
    static object? cursorSystem;
    static bool menuWasOpen;

    /// <summary>C++ AbandonWorldState (:262-269) on the window side: a new world (a new system) forgets the suppressed building.</summary>
    static void FollowWorld()
    {
        var system = ProvingGroundsSystem.Current;
        if (ReferenceEquals(system, cursorSystem)) return;
        cursorSystem = system;
        cursor.Reset();
        menuWasOpen = false;
    }

    static IReadOnlySet<string> Records => ProvingGroundsSystem.Current?.Snapshot.InteractRecords ?? FrozenSet<string>.Empty;

    /// <summary>Prefix on private PlayerInterface.PickedRightDown(Vector2 p, bool shift, bool alt) (PlayerInterface.cs:870; spike S3): C++
    /// showContextMenu_hook :133-158 for a Registry / Banner / Leaderboard (see the header). False = the game's own right press is skipped.</summary>
    static bool BeforePickedRightDown(object __instance, Vector2 p, bool __runOriginal)
    {
        using var perfScope = PgPerf.Measure(PgPerf.PickedRightDown);   // task 9.2a: the cost instrument (off: one volatile read)
        if (!__runOriginal || pickSite is null || pickKind is null || selectedCount is null || selectedIds is null) return true;
        try
        {
            FollowWorld();
            pressedUnfinished = null;
            if (Ui.ScreenHost.OverOurScreen(p)) return true;   // the window's press (ScreenHost's MouseDown postfix), never also ours
            if (!ContextMenuHookLogic.NeedsWorldPick(Records.Count, cursor.Suppressed)) return !TownPress(__instance, p, null, pickSite(__instance, p));   // no site of ours: only the town's pick (task 7.6)
            int kind = pickKind(__instance, p);
            var site = pickSite(__instance, p);
            var snap = ProvingGroundsSystem.Current?.Snapshot;
            var decision = ContextMenuHookLogic.DecidePress(kind == kindNone || kind == kindSite, site?.Record, site?.Complete == true,
                site?.LevelPlacement is not null, site?.Destroyed == true, selectedCount(__instance), Records, snap?.InteractBusy == true);
            if (decision == InteractPress.Vanilla || site is null) return !TownPress(__instance, p, kind, site);
            var siteRef = new ArenaBuildingRef(ArenaBuildingSource.Player, site.Id, site.Record);
            if (decision == InteractPress.Unfinished)
            {
                // C++ :150-157: the held menu opens as without the mod (the menu watch then logs and cancels); the quick order is dropped in
                // BeforeSiteRelease
                pressedUnfinished = siteRef;
                return true;
            }
            CharacterId[] selection = [.. selectedIds(__instance)];
            Ui.ScreenHost.Send(new ArenaInteract(siteRef, selection) { Tick = 0 });   // Send sets the tick
            cursor.Suppress(site.Id);   // C++ :145-146
            return false;
        }
        catch (Exception e) { LogOnce("right press", e); return true; }
    }

    /// <summary>Task 7.6 (C++ showContextMenu_hook :133-158 for the Town Registry, a Kenshi building like any other there): a right press with
    /// nothing in front (<c>Picked.None</c>) on a level building whose record is the Town Registry (195) or the town Leaderboard (272, review) sends ArenaInteract with the town building
    /// (its placement key, Town source), and the sim starts the same approach as for a player Registry (ApplyInteract, BeginApproachForUI;
    /// ArenaUI::ShowFromRegistry opens the town window for it). True = the press was ours (the game's move order is skipped). The "use" pointer
    /// over it is not given (divergences.md "TownArenaUI").</summary>
    static bool TownPress(object ui, Vector2 p, int? pickedKind = null, BuildingSnapshot? site = null)
    {
        if (pickKind is null || selectedCount is null || selectedIds is null) return false;
        int kind = pickedKind ?? pickKind(ui, p);
        // a town building Meitou keeps as a site (a level placement: the town Leaderboard is a machine, FixtureRegistry.Property.cs:322-340, its Id the
        // town building's key): picked as a site object (Picked.Site) or with nothing in front
        if (site is { LevelPlacement: not null } level && (kind == kindSite || ContextMenuHookLogic.NothingInFront(kind == kindNone, kind == kindSeat,
                kind == kindSeat ? pickSeat?.Invoke(ui, p)?.Building : null, level.Id)))
        {
            var d = ContextMenuHookLogic.DecideTownPress(true, level.Record, true, level.Destroyed, level.Complete, selectedCount(ui),
                ProvingGroundsSystem.Current?.Snapshot?.InteractBusy == true);
            if (d == InteractPress.Unfinished)   // C++ :140: "interact RMB ignored (not finished)", the game's own press (its menu) runs
                Ui.ScreenHost.Send(new ArenaInteractIgnored(new ArenaBuildingRef(ArenaBuildingSource.Town, level.Id, level.Record)) { Tick = 0 });
            if (d != InteractPress.Approach) return false;
            CharacterId[] who = [.. selectedIds(ui)];
            Ui.ScreenHost.Send(new ArenaInteract(new ArenaBuildingRef(ArenaBuildingSource.Town, level.Id, level.Record), who) { Tick = 0 });
            return true;
        }
        if (pickBuilding is null || buildingInstance is null || buildingRecord is null || buildingSiteId is null || buildingDestroyed is null) return false;
        var b = pickBuilding(ui, p);
        string? instance = b is null ? null : buildingInstance(b), record = b is null ? null : buildingRecord(b);
        // nothing in front (Picked.None), or the building's own seat (SeatSpot.Building = its key): the town Leaderboard is a machine whose operator
        // seat Meitou picks in front of it (Picked.Seat; live: the press gave "Leaderboards Town is already in use", the machine's use); another seat
        // (a bench in front), a character, item, container, door, bed, cage, dummy or rock keeps its own click. A level building PickBuilding names is
        // finished (a building under repair is a site, the branch above)
        if (b is null || !ContextMenuHookLogic.NothingInFront(kind == kindNone, kind == kindSeat, kind == kindSeat ? pickSeat?.Invoke(ui, p)?.Building : null,
                instance)) return false;
        var decision = ContextMenuHookLogic.DecideTownPress(true, record, buildingSiteId(b) is null && instance is not null, buildingDestroyed(b), true,
            selectedCount(ui), ProvingGroundsSystem.Current?.Snapshot?.InteractBusy == true);
        if (decision != InteractPress.Approach) return false;
        CharacterId[] selection = [.. selectedIds(ui)];
        Ui.ScreenHost.Send(new ArenaInteract(new ArenaBuildingRef(ArenaBuildingSource.Town, instance!, record!), selection) { Tick = 0 });
        return true;
    }

    /// <summary>The last right press on an unfinished one of ours (window thread), for the menu watch: C++ showContextMenu_hook on such a
    /// building logs its identity and "ignored (not finished)" when the menu opens (:135-136, :150-151).</summary>
    static ArenaBuildingRef? pressedUnfinished;

    /// <summary>Prefix on internal BuildMode.SiteRelease() (BuildMode.cs:1266; window thread, the right release): a press on an unfinished one of
    /// ours released before its menu opened gives no quick order (C++ addOrder_hook :243-247: Kenshi's quick order on our buildings is a "use"
    /// row, swallowed while unfinished; Meitou's would be Build, or the Engineer job with Shift). The press is forgotten as SiteRelease forgets
    /// it (:1269) and the sim logs "interact use ignored (not finished)".</summary>
    static bool BeforeSiteRelease(object __instance, bool __runOriginal)
    {
        using var perfScope = PgPerf.Measure(PgPerf.SiteRelease);   // task 9.2a: the cost instrument (off: one volatile read)
        if (!__runOriginal || pendingSite is null || clearPending is null || __instance is null) return true;
        try
        {
            if (pendingSite(__instance) is not { } site) return true;
            if (!ContextMenuHookLogic.DropsQuickOrder(site.Record, site.Complete, site.LevelPlacement is not null, site.Destroyed, Records)) return true;
            clearPending(__instance);
            pressedUnfinished = null;
            Ui.ScreenHost.Send(new ArenaUseIgnored(new ArenaBuildingRef(ArenaBuildingSource.Player, site.Id, site.Record)) { Tick = 0 });
            return false;
        }
        catch (Exception e) { LogOnce("site release", e); return true; }
    }

    /// <summary>Postfix on private PointerView.PickWithSelection() (PointerView.cs:175; CAP 6), called with characters selected when the pointer is
    /// picked again: "use" over an interact building (C++ getMouseCursor_hook / changeMouseCursorTarget_hook), the game's pointer over the building
    /// just clicked until the pointer leaves it (C++ Tick :280-288, RefreshArenaCursor :327-345).</summary>
    static void AfterPickWithSelection(ref ValueTuple<string, CharacterId?> __result)
    {
        using var perfScope = PgPerf.Measure(PgPerf.PickWithSelection);   // task 9.2a: the cost instrument (off: one volatile read)
        if (pickSite is null || pickKind is null || selectedCount is null || Ui.ScreenHost.Interface is not { } ui) return;
        try
        {
            FollowWorld();
            if (!ContextMenuHookLogic.NeedsWorldPick(Records.Count, cursor.Suppressed)) return;   // nothing of ours: no world pick
            Vector2 m = Ui.ScreenHost.Pointer;
            if (!float.IsFinite(m.X)) return;
            int kind = pickKind(ui, m);
            var site = kind == kindNone || kind == kindSite ? pickSite(ui, m) : null;
            bool interact = site is { LevelPlacement: null, Destroyed: false } && Records.Contains(site.Record);
            if (cursor.WantsUse(site?.Id, interact, selectedCount(ui) > 0))
                __result = ("use", __result.Item2);
        }
        catch (Exception e) { LogOnce("pointer", e); }
    }

    /// <summary>Once per interface arrange (ScreenHost.Frame): a right-click menu that opened since the last arrange is "another right-click menu"
    /// (C++ showContextMenu_hook :154-155): ArenaExternalMenu, which cancels a running approach (NotifyExternalOrder(NULL)). Our own right press
    /// never opens one (it skips the game's press).</summary>
    internal static void WindowFrame()
    {
        if (!RightPressOn) return;
        try
        {
            FollowWorld();
            if (Ui.ScreenHost.MenuOpenNow() is not { } open) return;
            if (open && !menuWasOpen)
            {
                // C++ showContextMenu_hook (:133-158) on an unfinished one of ours: its identity and "ignored (not finished)", then the cancel
                if (pressedUnfinished is { } u) Ui.ScreenHost.Send(new ArenaInteractIgnored(u) { Tick = 0 });
                pressedUnfinished = null;
                Ui.ScreenHost.Send(new ArenaExternalMenu { Tick = 0 });
            }
            menuWasOpen = open;
        }
        catch (Exception e) { LogOnce("menu watch", e); }
    }

    /// <summary>Prefix on public static PlayerOrders.Give(World world, int slot, OrderEntry order, bool queued, bool show) (OrderQueue.cs:224; CAP 5),
    /// serial phases only: C++ addOrder_hook (:228-258). A swallowed order queues nothing and answers OrderCheck.Gone. Respects another prefix's
    /// skip; never throws into the game.</summary>
    static bool BeforeGive(World world, int slot, OrderEntry order, ref OrderCheck __result, bool __runOriginal)
    {
        using var perfScope = PgPerf.Measure(PgPerf.OrdersGive);   // task 9.2a: the cost instrument (off: one volatile read)
        if (!__runOriginal) return true;
        try
        {
            if (ProvingGroundsSystem.InteractFor(world) is not { } hooks) return true;
            if (hooks.BeforeOrder(world, slot, order)) return true;
            __result = OrderCheck.Gone;
            return false;
        }
        catch (Exception e) { LogOnce("order", e); return true; }
    }
}
