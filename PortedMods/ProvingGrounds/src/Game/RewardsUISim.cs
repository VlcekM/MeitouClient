using System.Globalization;
using System.Text;
using System.Text.Json;
using Meitou.Simulation;
using Meitou.Simulation.Population;
using ProvingGrounds.Core;
using ProvingGrounds.Core.Glue;
using ProvingGrounds.Engine;

namespace ProvingGrounds.Game;

// Port of the rules half of upstream/ProvingGrounds/plugin/src/RewardsUI.h/.cpp (Proving Grounds by Shutori, GPLv3), task 5.3a (PA §2.17;
// CAP 7.0-7.4, 10). Every decision and text is Core/Glue/RewardsUILogic.cs (tested in Tests/Glue/RewardsUILogicTests.cs); the window half
// (layouts from EnsureWindow's createWidget code, the icons, the portraits, the scroll canvases, the purchase gate on the window thread) is task
// 5.3b in src/Ui/RewardsUI.cs. This file is the sim half: Show / Close / IsVisible / AbandonWorldState (RewardsUI.h:8-12), the window's
// commands (src/Engine/PgCommand.cs "task 5.3a"; purchases are 5.2b's RewardPurchase, run by src/Game/RewardDelivery.cs, after which
// ProvingGroundsSystem calls AfterPurchase) and the published RewardsUiModel. Meitou 0.17.3 (tag v0.17.3, fa34a1b). Sim thread, serial steps.
//
// Engine calls of the C++ and what replaces them (E1; IRewardsUIHost member -> adapter method, bound in Host below):
//   SquadUtil::CollectPlayerSquad (:1235) ............ SquadUtil.CollectPlayerSquad (task 4.2: squad members that exist and are not dead)
//   c->isValid(), isDead(), getName() ................ PgEngine.Exists / IsDead / Name
//   LeaderboardStore::GetMarks / HasFactionUnlock ..... src/Game/LeaderboardStore.cs (task 5.1)
//   RewardDelivery::ResolveOffer (:1493) ............. src/Game/RewardDelivery.cs ResolveOffer (task 5.2b)
//   RewardDelivery::ClaimNext / PurchaseLicence / PurchaseWeaponLicence / PurchaseGeneral (:499, :533, :548, :558) .. the RewardPurchase
//       command (task 5.2b, Sender = Window), RewardDelivery.Apply; the status line from PgSnapshot.LastPurchaseOf(Window) (Core
//       RewardsPurchaseGate; the window half resets its gate on a world change: FollowWorld(ProvingGroundsSystem.Current))
//   RewardDelivery::ResolveRewardItemData (BindRewardIcon :366-374), ou->gamedata.getData (BindGeneralIcon :410-416) ... here, on the sim thread:
//       RewardDelivery.ResolveRewardItemData / Items.RecordExists, published as each card's Icon (record, quality, model, maker of the preview
//       item); CreateRewardItem of the preview + InventoryIcon::createIconImage (the texture) ... the window half (5.3b)
//   PortraitManager::setImageWidget (:347-359), every MyGUI call ... the window half (5.3b), from the published model
//   PGLog::Error ..................................... ModLog.Error
// Callers (C++ -> port): ContextMenuHooks.cpp:322 Show (Null's trade, src/Game/ContextMenuHooks.cs TickPendingTrade); ArenaUI.cpp:3170 Tick
// (the frame-loop slot, kept empty: its only work is the window's resize); ArenaUI.cpp:3201 AbandonWorldState (through PgArenaUIHost);
// ArenaUI.cpp:3014-3016 Escape -> Close (the window framework's Closed report); the TEST-ONLY `pg rewards`.

/// <summary>The window model of the Rewards window <c>pg.rewards</c> (task 5.3a). A new object only when the view changed.</summary>
sealed record RewardsUiModel(RewardsUiView<CharacterId> View) : PgUiModel;

/// <summary>The sim half of the Rewards window (task 5.3a). One per world, owned by ProvingGroundsSystem.</summary>
sealed class RewardsUISim
{
    internal const string ScreenId = "pg.rewards";

    /// <summary>C++ g_catalogTab (RewardsUI.cpp:89) lives for the process: the next world's window starts on the tab the last one showed.</summary>
    static RewardsCatalogTab lastTab = RewardsCatalogTab.General;

    readonly LeaderboardStore store;
    readonly RewardDelivery delivery;
    readonly PopulationData data;
    readonly RewardsUILogic<CharacterId> logic = new(lastTab);
    readonly Host host;
    RewardsUiModel model;

    internal RewardsUISim(LeaderboardStore store, RewardDelivery delivery, PopulationData data)
    {
        this.store = store;
        this.delivery = delivery;
        this.data = data;
        host = new Host(this);
        model = new RewardsUiModel(logic.BuildView());
    }

    /// <summary>IRewardsUIHost bound to the world of the current call.</summary>
    sealed class Host(RewardsUISim sim) : IRewardsUIHost<CharacterId>
    {
        internal World? World;
        World W => World ?? throw new InvalidOperationException("RewardsUI used before a world was bound");
        public IReadOnlyList<CharacterId> PlayerSquad() => SquadUtil.CollectPlayerSquad(W);
        public bool Valid(CharacterId c) => !c.IsNone && PgEngine.Exists(W, c);
        public bool IsDead(CharacterId c) => PgEngine.IsDead(W, c);
        public string Name(CharacterId c) => PgEngine.Name(W, c) ?? "";
        public int GetMarks(CharacterId c) => sim.store.GetMarks(W, c);
        public bool HasFactionUnlock(string id) => sim.store.HasFactionUnlock(id);
        public ArenaRewards.Offer ResolveOffer(CharacterId c, ArenaRewards.Piece piece) => sim.delivery.ResolveOffer(W, c, piece);
        public void Error(string line) => ModLog.Error(line);
        public string? RewardIconRecord(ArenaRewards.Piece piece) => sim.delivery.ResolveRewardItemData(W, piece);
        public bool RecordExists(string recordId) => Items.RecordExists(sim.data, recordId);
    }

    Host H(World world) { host.World = world; return host; }

    internal RewardsUiModel Model => model;
    internal RewardsUILogic<CharacterId> Logic => logic;

    /// <summary>The F10 phase row.</summary>
    internal string Phase => logic.IsVisible ? "open: " + model.View.Status : "closed";

    void Publish()
    {
        lastTab = logic.Tab;
        var view = logic.BuildView();
        if (view.Equals(model.View)) return;
        model = new RewardsUiModel(view) { RequestSerial = view.RequestSerial, RequestOpen = view.RequestOpen };
    }

    /// <summary>C++: RewardsUI.cpp:1592-1617 Show(preferred) (<see cref="CharacterId.None"/> = NULL).</summary>
    internal void Show(World world, CharacterId preferred)
    {
        logic.Show(H(world), !preferred.IsNone, preferred);
        Publish();
    }

    /// <summary>C++: RewardsUI.cpp:1618 Close.</summary>
    internal void Close()
    {
        logic.Close();
        Publish();
    }

    /// <summary>C++: RewardsUI.cpp:1619 IsVisible.</summary>
    internal bool IsVisible => logic.IsVisible;

    /// <summary>C++: RewardsUI.cpp:1620-1633, the frame-loop slot (ArenaUI.cpp:3170): only the window's resize, which is the window side's.</summary>
    internal void Tick(World world) { }   // the C++ slot, kept empty on purpose: Marks shown are refreshed by clicks and purchases only, as the C++

    /// <summary>C++: RewardsUI.cpp:1635-1641 AbandonWorldState.</summary>
    internal void AbandonWorldState(World world)
    {
        logic.AbandonWorldState();
        Publish();
    }

    /// <summary>After a RewardPurchase ran (serial Inputs, any sender): the C++ RefreshDetails of the purchase handlers (:502, :537, :550, :559).</summary>
    internal void AfterPurchase(World world)
    {
        logic.AfterPurchase(H(world));
        Publish();
    }

    /// <summary>One Rewards window command (serial Inputs). A command that arrives while the window is closed (sent just before it closed) is
    /// ignored: the C++ handlers are reachable only through the visible window.</summary>
    internal (string Done, bool Ignored) Apply(World world, RewardsUiCommand command)
    {
        string done = command.GetType().Name;
        if (!logic.IsVisible) return (done + ": ignored (window closed)", true);
        switch (command)
        {
            case RewardsSelectFighter f:
                if (!logic.OnFighterClicked(H(world), f.Fighter)) return (done + ": no row shows " + CharacterIdText.Format(f.Fighter), true);
                break;
            case RewardsSelectTab t:
                if (!logic.OnCatalogTabClicked(H(world), t.Tab)) return (done + ": " + t.Tab + " already shown", false);
                break;
            case RewardsClose:
                logic.Close();
                break;
            default:
                return ("unknown rewards command " + done, true);
        }
        Publish();
        return (done + ": " + model.View.Status, false);
    }

    /// <summary>A generic widget event of the window (task 4.4's UiClick): only the framework's Closed report (Escape, the title bar's close
    /// button: OnWindowButtonPressed :563-567, ArenaUI.cpp:3014-3016) is ours; the buttons send RewardsUiCommands / RewardPurchase.</summary>
    internal (string Done, bool Ignored) Apply(World world, UiClickData c)
    {
        if (c.Kind != UiEventKind.Closed) return ($"ignored {c.Kind} on {c.Widget} (the Rewards window sends its own commands)", true);
        if (!logic.OnClosedFromWindow(c.Value)) return ("closed (already, or for an older request)", true);
        Publish();
        return ("closed from the window", false);
    }

    // ---------- TEST ONLY: pg rewards (task 5.3a, until the window half 5.3b) ----------

    const string Usage = "usage pg rewards <open [c:<slot>.<gen>] | close | model | click <fighter c:<slot>.<gen> | tab general|armour|weapons"
        + " | claim <piece> | licence pitfighter|retainer|enforcer|warlord | weapon-licence | general <item> | close>>";

    /// <summary>The test verb's own purchase sequence (sender TestGate), as the window's (5.3b keeps one on the window thread, sender Window).
    /// Per world by construction (one sim half per world).</summary>
    readonly RewardsPurchaseGate testGate = new();

    /// <summary>TEST ONLY <c>pg rewards ...</c>: <c>open [id]</c> = Show(id) (as Null's trade opens it, ContextMenuHooks.cpp:322); <c>close</c> = the
    /// sim's Close; <c>model</c> = the model; <c>click ...</c> = what the window's button sends: a RewardsUiCommand, or for a purchase button a
    /// RewardPurchase with the next number of this verb's RewardsPurchaseGate and the card's shown offer, run at once through RewardDelivery (the
    /// window's command path) and then AfterPurchase. A hidden or disabled button answers ok:false (the game cannot press it). One PG-REWARDS line.</summary>
    internal string Command(World world, IReadOnlyList<string> args)
    {
        try
        {
            string verb = args.Count > 0 ? args[0].ToLowerInvariant() : "";
            switch (verb)
            {
                case "open":
                    var who = CharacterId.None;
                    if (args.Count > 1 && !CharacterIdText.TryParse(args[1], out who)) return Answer(world, verb, false, Usage);
                    Show(world, who);
                    return Answer(world, verb, true, null);
                case "close":
                    Close();
                    return Answer(world, verb, true, null);
                case "model":
                    return Answer(world, verb, true, null);
                case "click" when args.Count > 1:
                    string? error = Click(world, args.Skip(1).Select(a => a.ToLowerInvariant()).ToArray(), args.Count > 2 ? args[2] : "");
                    return Answer(world, "click " + string.Join(' ', args.Skip(1)), error is null, error);
                default:
                    return Answer(world, verb.Length > 0 ? verb : "rewards", false, Usage);
            }
        }
        catch (Exception e) { return Answer(world, "rewards", false, $"{e.GetType().Name}: {e.Message}"); }
    }

    /// <summary>One window button, as the window sends it; null = done, else why the game could not press it.</summary>
    string? Click(World world, string[] w, string raw)
    {
        var v = model.View;
        if (!v.RequestOpen) return "ignored: the Rewards window is closed (pg rewards open first)";
        switch (w[0])
        {
            case "fighter" when w.Length == 2 && CharacterIdText.TryParse(raw, out var id):
                return Apply(world, new RewardsSelectFighter(id) { Tick = world.Tick }) is var (d1, i1) && i1 ? d1 : null;
            case "tab" when w.Length == 2 && Enum.TryParse(w[1], true, out RewardsCatalogTab tab) && Enum.IsDefined(tab) && !char.IsAsciiDigit(w[1][0]):
                if (!v.TabsVisible) return "the tabs are hidden";
                return Apply(world, new RewardsSelectTab(tab) { Tick = world.Tick }) is var (d2, i2) && i2 ? d2 : null;
            case "close" when w.Length == 1:
                return Apply(world, new RewardsClose() { Tick = world.Tick }) is var (d3, i3) && i3 ? d3 : null;
            case "claim" when w.Length == 2 && RewardPurchaseArgs.TryName(w[1], ArenaRewards.Piece.Count, out ArenaRewards.Piece piece):
            {
                var card = v.Cards[(int)piece];
                if (!card.Visible) return $"the {piece} card is hidden on this tab";
                if (!card.ClaimEnabled) return $"the {piece} {card.ClaimCaption} button is disabled";
                return Purchase(world, RewardPurchaseKind.Claim, (int)piece, card.HasOffer ? card.Offer : null);
            }
            case "licence" when w.Length == 2 && Enum.TryParse(w[1], true, out ArenaRewards.ArmourRow row) && row < ArenaRewards.ArmourRow.Count
                && !char.IsAsciiDigit(w[1][0]):
            {
                var l = v.Licences[(int)row];
                if (!l.BuyVisible) return $"the {row} licence button is hidden";
                if (!l.BuyEnabled) return $"the {row} {l.BuyCaption} button is disabled";
                return Purchase(world, RewardPurchaseKind.Licence, (int)l.Set, null);
            }
            case "weapon-licence" when w.Length == 1:
                if (!v.WeaponLicence.BuyVisible) return "the weapons licence button is hidden";
                if (!v.WeaponLicence.BuyEnabled) return "the weapons UNLOCK LICENCE button is disabled";
                return Purchase(world, RewardPurchaseKind.WeaponLicence, 0, null);
            case "general" when w.Length == 2 && RewardPurchaseArgs.TryName(w[1], ArenaRewards.GeneralItem.Count, out ArenaRewards.GeneralItem item):
            {
                var g = v.General[(int)item];
                if (!g.Visible) return $"the {item} card is hidden on this tab";
                if (!g.BuyEnabled) return $"the {item} {g.BuyCaption} button is disabled";
                return Purchase(world, RewardPurchaseKind.General, (int)item, null);
            }
            default:
                return Usage;
        }
    }

    /// <summary>A purchase button: the RewardPurchase the window sends, run here at once (sim thread) through RewardDelivery.Apply, then the refresh
    /// ProvingGroundsSystem runs after any RewardPurchase.</summary>
    string? Purchase(World world, RewardPurchaseKind kind, int item, ArenaRewards.Offer? shown)
    {
        var v = model.View;
        if (!v.HasSelected) return "no fighter is selected";
        long published = delivery.LastPurchaseOf(RewardPurchaseSender.TestGate)?.Seq ?? 0;
        long seq = testGate.Send(published, v.Shows);
        if (seq == 0) return "a purchase still waits for its result";
        var command = new RewardPurchase(v.Selected, kind, item, seq, shown, RewardPurchaseSender.TestGate) { Tick = world.Tick };
        var (_, status) = delivery.Apply(world, command);
        ProvingGroundsMod.Ctx?.Log($"reward purchase {kind} for {CharacterIdText.Format(v.Selected)}: {status}");
        AfterPurchase(world);
        return null;
    }

    string ShownStatus()
    {
        var last = delivery.LastPurchaseOf(RewardPurchaseSender.TestGate);
        testGate.Observe(last?.Seq ?? 0, last is not null, last?.Status ?? "", last?.OfferChanged == true, model.View.Shows);
        return testGate.Status(model.View.Status);
    }

    string Answer(World world, string verb, bool ok, string? error)
    {
        var sb = new StringBuilder();
        sb.Append("{\"event\":").Append(PGLog.Quote(verb)).Append(",\"ok\":").Append(ok ? "true" : "false");
        if (error is not null) sb.Append(",\"error\":").Append(PGLog.Quote(error));
        sb.Append(",\"tick\":").Append(world.Tick.ToString(CultureInfo.InvariantCulture))
          .Append(",\"model\":").Append(ModelJson(model.View, ShownStatus(), delivery.LastPurchaseOf(RewardPurchaseSender.TestGate))).Append('}');
        string json = sb.ToString();
        ProvingGroundsMod.Ctx?.Log("PG-REWARDS " + json);
        return json;
    }

    /// <summary>TEST ONLY: the published view, compactly (only the cards, General items and licences the current tab shows).</summary>
    internal static string ModelJson(RewardsUiView<CharacterId> v, string shownStatus, PgPurchaseResult? last) => JsonSerializer.Serialize(new
    {
        open = v.RequestOpen, serial = v.RequestSerial, shows = v.Shows, tab = v.Tab.ToString(),
        selected = v.HasSelected ? CharacterIdText.Format(v.Selected) : null,
        status = shownStatus, modelStatus = v.Status, career = v.Career, caption = v.Caption, subtitle = v.Subtitle,
        rows = v.Rows.Select(r => new { id = CharacterIdText.Format(r.Character), name = r.Name, marks = r.Marks, selected = r.Selected }),
        cards = v.Cards.Where(c => c.Visible).Select(c => new
        {
            piece = c.Piece.ToString(), name = c.Name, progress = c.Progress, button = c.ClaimCaption, enabled = c.ClaimEnabled, rarity = c.Rarity,
            offer = c.HasOffer ? $"{c.Offer.Kind} {c.Offer.Tier} {c.Offer.Cost.ToString(CultureInfo.InvariantCulture)}" : null,
            icon = c.Icon.Record,
        }),
        general = v.General.Where(g => g.Visible).Select(g => new { item = g.Item.ToString(), name = g.Name, details = g.Details, button = g.BuyCaption, enabled = g.BuyEnabled, icon = g.Icon.Record }),
        licences = v.Licences.Where(l => l.Visible).Select(l => new
        {
            row = l.Row.ToString(), heading = l.Heading, details = l.Details, button = l.BuyCaption, buttonVisible = l.BuyVisible, enabled = l.BuyEnabled,
            unlocked = l.Unlocked,
        }),
        weaponLicence = v.WeaponLicence.Visible ? new
        {
            row = "Weapons", heading = v.WeaponLicence.Heading, details = v.WeaponLicence.Details, button = v.WeaponLicence.BuyCaption,
            buttonVisible = v.WeaponLicence.BuyVisible, enabled = v.WeaponLicence.BuyEnabled, unlocked = v.WeaponLicence.Unlocked,
        } : null,
        lastPurchase = last is null ? null : new { seq = last.Seq, ok = last.Ok, status = last.Status, offerChanged = last.OfferChanged },
    });
}
