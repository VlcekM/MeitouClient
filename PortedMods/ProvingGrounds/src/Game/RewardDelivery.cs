using System.Globalization;
using Meitou.Simulation;
using Meitou.Simulation.Population;
using ProvingGrounds.Core;
using ProvingGrounds.Core.Glue;
using ProvingGrounds.Engine;

namespace ProvingGrounds.Game;

// Port of upstream/ProvingGrounds/plugin/src/RewardDelivery.h/.cpp (Proving Grounds by Shutori, GPLv3), task 5.2b (PA §2.17-§2.18, CAP 10).
// Every public function and every helper that decides something is Core/Glue/RewardDeliveryLogic.cs (fake-tested,
// Tests/Glue/RewardDeliveryLogicTests.cs; its header lists each C++ helper and where it went); every engine call is src/Engine/Items.cs (task
// 5.2a, its E1 list). This file binds the logic to a world and to CharacterIds and is the purchase command's sim half. Meitou 0.17.3 (tag
// v0.17.3, fa34a1b). Sim thread, serial steps only (the RewardPurchase command and the test verb run in Inputs).
//
// Engine calls of the C++ and what replaces them (E1; IRewardDeliveryHost member -> adapter method):
//   character (non-null), character->isValid() ............... IsNull = CharacterId.IsNone; Valid = PgEngine.Exists (TryResolveNext)
//   IsPlayerSquadFighter: SquadUtil::CollectPlayerSquad (:96-106) .. SquadUtil.CollectPlayerSquad (task 4.2). A prisoner (M6) is not in the
//       player squad, so it keeps its Marks and cannot claim (:773-777), as the C++: no separate prisoner source is asked
//   ou, ou->theFactory (:778, :1031) ......................... Items.CanStart (task 5.2a), asked ONCE per purchase before anything is reserved or
//       taken out; with the rewards members missing (or a refresh that threw) the part is off, one log line, every claim and general item refused (the licences make no item and are not, as the C++)
//   character->getInventory() ................................ Items.HasInventory
//   character->getName() ...................................... PgEngine.Name
//   LeaderboardStore::GetMarks / GetRewardTier / HasFactionUnlock / ReserveProgression ... src/Game/LeaderboardStore.cs (task 5.1)
//   hand actor; actor.getCharacter() == character; FighterIdentity::Find (PurchaseAccounting::Matches :447-452) .. PgEngine.Exists (the id's
//       generation is the handle) + FighterIdentity.Find (task 2.3)
//   ou->gamedata.getData (records) ............................ Items.RecordExists / BlueprintRecordsExist
//   inventory->hasRoomForItem(itemData / deliveryData) (:851, :1070) .. Items.HasRoomForRecord / HasRoomForBlueprint (before the item is made)
//   the item finders, CreateRewardItem, CreateBlueprintReward, chargesLeft, removeItemDontDestroy_returnsItem, giveItem, ou->destroy ..
//       Items.Copies / CreateRewardItem / CreateBlueprintReward / SetCharges / TakeOut / Give / Destroy (task 5.2a; each refreshes the fighter)
//   RewardDiag -> PGLog::Error + PGLog::Debug (:42-46) ......... PGLog.Debug only (divergences.md "RewardDelivery")
//
// Callers (C++ -> port): RewardsUI.cpp:369-417 (ResolveRewardItemData, CreateRewardItem: the preview), :499 ClaimNext, :533 PurchaseGeneral,
// :548 PurchaseWeaponLicence, :558 PurchaseLicence, :1493 ResolveOffer (task 5.3, the Rewards window: it sends RewardPurchase commands and
// reads ResolveOffer in its sim half). Until then the TEST-ONLY `pg reward` sends the same command.

/// <summary>RewardDelivery of one world (task 5.2b): one object, owned by ProvingGroundsSystem. Each call binds the world and runs the C++
/// function in <see cref="RewardDeliveryLogic{TId, TItem}"/>.</summary>
sealed class RewardDelivery
{
    readonly Host host;
    readonly RewardDeliveryLogic<CharacterId, RewardItem> logic;
    readonly LeaderboardStore store;
    readonly PopulationData data;

    internal RewardDelivery(LeaderboardStore store, FighterIdentity identity, PopulationData data)
    {
        this.store = store;
        this.data = data;
        host = new Host(store, identity, data);
        logic = new RewardDeliveryLogic<CharacterId, RewardItem>(host);
    }

    RewardDeliveryLogic<CharacterId, RewardItem> L(World world) { host.World = world; return logic; }

    /// <summary>The "rewards off" line is written once per process (the part stays off while a member is missing or a refresh threw).</summary>
    static bool offLogged;

    /// <summary>IRewardDeliveryHost bound to the world of the current call (every call binds it first and finishes inside that serial step).</summary>
    sealed class Host(LeaderboardStore store, FighterIdentity identity, PopulationData data) : IRewardDeliveryHost<CharacterId, RewardItem>
    {
        internal World? World;
        World W => World ?? throw new InvalidOperationException("RewardDelivery used before a world was bound");

        public bool IsNull(CharacterId c) => c.IsNone;
        public bool Valid(CharacterId c) => !c.IsNone && PgEngine.Exists(W, c);
        public bool IsPlayerSquadFighter(CharacterId c) => SquadUtil.CollectPlayerSquad(W).Contains(c);
        public bool ItemsCanStart()
        {
            if (Items.CanStart) return true;
            if (!offLogged)
            {
                offLogged = true;
                string line = $"rewards off: {string.Join(", ", Items.Missing())} not found";
                ProvingGroundsMod.Ctx?.Log(line);
                PGLog.Debug("Proving Grounds: " + line);
            }
            return false;
        }
        public bool HasInventory(CharacterId c) => Items.HasInventory(W, c);
        public string Name(CharacterId c) => PgEngine.Name(W, c) ?? "";
        public int GetMarks(CharacterId c) => store.GetMarks(W, c);
        public int GetRewardTier(CharacterId c, string rewardId) => store.GetRewardTier(W, c, rewardId);
        public bool HasFactionUnlock(string unlockId) => store.HasFactionUnlock(unlockId);
        public bool ReserveProgression(CharacterId c, ArenaLedger.Purchase purchase, RewardTransaction.ReservedAccounting accounting) =>
            store.ReserveProgression(W, c, purchase, accounting);
        public bool IdentityMatches(CharacterId c, string token) =>
            !c.IsNone && PgEngine.Exists(W, c) && identity.Find(W, c, out string current) && current == token;
        public bool RecordExists(string recordId) => Items.RecordExists(data, recordId);
        public bool BlueprintRecordsExist(string researchId) => Items.BlueprintRecordsExist(data, researchId);
        public IReadOnlyList<RewardItem> Copies(CharacterId c, string recordId) => Items.Copies(W, c, recordId);
        public int Level(RewardItem item) => item.Level;
        public bool HasRoomForRecord(CharacterId c, string recordId) => Items.HasRoomForRecord(W, data, c, recordId);
        public bool HasRoomForBlueprint(CharacterId c, string researchId) => Items.HasRoomForBlueprint(W, data, c, researchId);
        public RewardItem? CreateRewardItem(string recordId, int qualityLevel, string? modelStringId, string? manufacturerStringId) =>
            Items.CreateRewardItem(data, recordId, qualityLevel, modelStringId, manufacturerStringId, Diag);
        public RewardItem? CreateBlueprintReward(string researchId) => Items.CreateBlueprintReward(data, researchId, Diag);
        public void SetCharges(RewardItem item, int charges) => Items.SetCharges(item, charges);
        public RewardItem? TakeOut(CharacterId c, RewardItem held) => Items.TakeOut(W, data, c, held);
        public bool Give(CharacterId c, RewardItem item, bool destroyOnFail) => Items.Give(W, data, c, item, destroyOnFail);
        public void Destroy(CharacterId c, RewardItem item) => _ = Items.Destroy(W, data, c, item);
        public void Diag(string line) => PGLog.Debug(line);
        public void Error(string line) => ModLog.Error(line);
    }

    // ---------- the public functions (RewardDelivery.h:16-50) ----------

    // C++: RewardDelivery.cpp:711-723
    internal string? ResolveRewardItemData(World world, ArenaRewards.Piece piece) => L(world).ResolveRewardItemData(piece);
    // C++: RewardDelivery.cpp:613-709 (the preview item of the Rewards window, task 5.3: made, never given)
    internal RewardItem? CreateRewardItem(World world, string recordId, int qualityLevel, string? model = null, string? maker = null) =>
        L(world).CreateRewardItem(recordId, qualityLevel, model, maker);
    // C++: RewardDelivery.cpp:725-760
    internal ArenaRewards.Offer ResolveOffer(World world, CharacterId c, ArenaRewards.Piece piece) => L(world).ResolveOffer(c, piece);
    // C++: RewardDelivery.cpp:762-919
    internal bool ClaimNext(World world, CharacterId c, ArenaRewards.Piece piece, out string status) => L(world).ClaimNext(c, piece, out status);
    // C++: RewardDelivery.cpp:921-973
    internal bool PurchaseLicence(World world, CharacterId c, ArenaRewards.ArmourSet set, out string status) => L(world).PurchaseLicence(c, set, out status);
    // C++: RewardDelivery.cpp:975-1017
    internal bool PurchaseWeaponLicence(World world, CharacterId c, out string status) => L(world).PurchaseWeaponLicence(c, out status);
    // C++: RewardDelivery.cpp:1019-1101
    internal bool PurchaseGeneral(World world, CharacterId c, ArenaRewards.GeneralItem item, out string status) => L(world).PurchaseGeneral(c, item, out status);

    // ---------- the purchase command (the Rewards window's buttons, task 5.3; the test verb until then) ----------

    /// <summary>The result of each sender's last <see cref="RewardPurchase"/> applied in this world (published in PgSnapshot after Inputs).</summary>
    internal RewardPurchaseResults<PgPurchaseResult> LastPurchases { get; private set; } = RewardPurchaseResults<PgPurchaseResult>.Empty;

    /// <summary>The last result of <paramref name="sender"/>, or null.</summary>
    internal PgPurchaseResult? LastPurchaseOf(RewardPurchaseSender sender) => LastPurchases.Of(sender);

    /// <summary>One <see cref="RewardPurchase"/> (sim thread, serial Inputs): <see cref="RewardDeliveryLogic{TId, TItem}.Purchase"/> (the function its
    /// kind names; a Claim whose shown offer changed is not run). Returns whether it succeeded and the status text the window shows
    /// (RewardsUI.cpp:500-501), kept as the sender's entry of <see cref="LastPurchases"/>. While the test verbs are armed (pg spikes on) one <c>PG-PURCHASE {json}</c>
    /// line is logged with the fighter's Marks before and after.</summary>
    internal (bool Ok, string Status) Apply(World world, RewardPurchase command)
    {
        CharacterId c = command.Fighter;
        bool armed = Spikes.SpikePatches.Armed;
        int before = 0;
        try { if (armed) before = store.GetMarks(world, c); } catch (Exception) { }   // test output only
        RewardPurchaseResult result;
        try { result = L(world).Purchase(c, command.Kind, command.Item, command.Shown); }   // Purchase itself never throws (Core guard)
        catch (Exception e)   // a backstop: the binding above, never reached from Purchase's own work
        {
            ModLog.Error($"Proving Grounds: reward purchase {command.Kind} threw {e.GetType().Name}: {e.Message}");
            result = new RewardPurchaseResult(false, RewardDeliveryLogic<CharacterId, RewardItem>.ExceptionText, false);
        }
        LastPurchases = LastPurchases.With(command.Sender,
            new PgPurchaseResult(command.Seq, c, command.Kind, result.Ok, result.Status, result.OfferChanged, command.Sender));
        if (armed)
        {
            string what = command.Kind switch
            {
                RewardPurchaseKind.Claim => ((ArenaRewards.Piece)command.Item).ToString(),
                RewardPurchaseKind.Licence => ((ArenaRewards.ArmourSet)command.Item).ToString(),
                RewardPurchaseKind.General => ((ArenaRewards.GeneralItem)command.Item).ToString(),
                _ => "",
            };
            ProvingGroundsMod.Ctx?.Log("PG-PURCHASE {\"event\":\"purchase\",\"id\":" + PGLog.Quote(CharacterIdText.Format(c)) + ",\"tick\":" + Num(world.Tick)
                + ",\"seq\":" + Num(command.Seq) + ",\"kind\":" + PGLog.Quote(command.Kind.ToString()) + ",\"what\":" + PGLog.Quote(what)
                + ",\"ok\":" + (result.Ok ? "true" : "false") + ",\"offerChanged\":" + (result.OfferChanged ? "true" : "false")
                + ",\"status\":" + PGLog.Quote(result.Status) + ",\"marksBefore\":" + Num(before) + ",\"marksAfter\":" + Num(MarksOrZero(world, c)) + "}");
        }
        return (result.Ok, result.Status);
    }

    int MarksOrZero(World world, CharacterId c) { try { return store.GetMarks(world, c); } catch (Exception) { return 0; } }

    static string Num(long n) => n.ToString(CultureInfo.InvariantCulture);

    /// <summary>TEST ONLY <c>pg reward &lt;id&gt; offer &lt;piece&gt;</c> / <c>drop &lt;piece&gt;</c> (the purchases themselves are RewardPurchase
    /// commands, PgConsole): <c>offer</c> = ResolveOffer as the Rewards window reads it, with the reward tier and the Marks; <c>drop</c> = every copy
    /// of the piece's record (and the cloak's other record) the fighter holds is destroyed (Items.Destroy: taken out, gone), so the next offer is a
    /// half-cost replacement. One <c>PG-PURCHASE {json}</c> line.</summary>
    internal string Command(World world, IReadOnlyList<string> args)
    {
        if (args.Count != 3 || !CharacterIdText.TryParse(args[0], out var c)
            || !RewardPurchaseArgs.TryName(args[2], ArenaRewards.Piece.Count, out ArenaRewards.Piece piece))
            return RewardPurchaseArgs.Usage;
        string head = "{\"id\":" + PGLog.Quote(CharacterIdText.Format(c)) + ",\"tick\":" + Num(world.Tick) + ",\"piece\":" + PGLog.Quote(piece.ToString());
        string line;
        switch (args[1].ToLowerInvariant())
        {
            case "offer":
                var offer = ResolveOffer(world, c, piece);
                line = "{\"event\":\"offer\"," + head[1..] + ",\"kind\":" + PGLog.Quote(offer.Kind.ToString()) + ",\"tier\":" + PGLog.Quote(offer.Tier.ToString())
                    + ",\"cost\":" + Num(offer.Cost) + ",\"rewardTier\":" + Num(store.GetRewardTier(world, c, ArenaRewards.StableId(piece)))
                    + ",\"marks\":" + Num(store.GetMarks(world, c)) + "}";
                break;
            case "drop":   // units, not items: Destroy takes one unit of a held stack at a time, so re-read until none is left
                string record = RewardGrades.ItemRecord(piece);
                string? alternate = RewardGrades.AlternateRecord(piece, record);
                List<RewardItem> Held()
                {
                    var copies = Items.Copies(world, c, record);
                    if (alternate is not null) copies.AddRange(Items.Copies(world, c, alternate));
                    return copies;
                }
                int held = Held().Sum(i => i.Quantity), dropped = 0;
                for (List<RewardItem> left = Held(); left.Count > 0 && dropped < held; left = Held())
                {
                    if (!Items.Destroy(world, data, c, left[0])) break;
                    ++dropped;
                }
                line = "{\"event\":\"drop\"," + head[1..] + ",\"held\":" + Num(held) + ",\"dropped\":" + Num(dropped) + "}";
                break;
            default:
                return RewardPurchaseArgs.Usage;
        }
        ProvingGroundsMod.Ctx?.Log("PG-PURCHASE " + line);
        return args[1].ToLowerInvariant() + " logged";
    }
}
