namespace ProvingGrounds.Core.Glue;

// Port of upstream/ProvingGrounds/plugin/src/RewardDelivery.h/.cpp (Proving Grounds by Shutori, GPLv3), task 5.2b (PA §2.17-§2.18): the
// purchase rules and the two-phase transaction of every public function and of the anonymous-namespace helpers that decide something, over
// IRewardDeliveryHost (the character, the store and the item reads and moves). The engine half is src/Engine/Items.cs (task 5.2a), the grade
// rules Core/Glue/RewardGrades.cs (task 5.2a); the glue that binds the host to a world is src/Game/RewardDelivery.cs. Core never sees an engine
// type: characters are TId, items an opaque TItem handle the host made or found.
//
// The C++ anonymous namespace (RewardDelivery.cpp:35-611), helper by helper, and where it lives in the port:
//   kWarlordManufacturerId / kNeckCloakId / kBeltCloakId (:38-40) ...... PgRecords (NullForged, WarlordCloakNeck, WarlordCloak)
//   RewardDiag (:42-46) .................................................. IRewardDeliveryHost.Diag (DEBUG only; divergences.md "RewardDelivery")
//   NormaliseModName, HasExtraInventorySections (:48-85) ................. dropped: the cloak is always record 89 (spike S13, RewardGrades.ItemRecord)
//   ResolveAlternateCloakData (:87-94) ................................... RewardGrades.AlternateRecord
//   IsPlayerSquadFighter (:96-106) ....................................... IRewardDeliveryHost.IsPlayerSquadFighter (SquadUtil.CollectPlayerSquad)
//   ResolveWeaponManufacturer, EnsureWeaponListed, FindWeaponModel, ResolveItemMaterial (:108-167) .. Items.CreateRewardItem (task 5.2a)
//   LevelsMatch (:169-196) ............................................... RewardGrades.LevelsMatch
//   FindInInventorySections, FindExactGrade, HasAnyGrade, FindPreviousGrade (:198-357) .. Items.Copies + RewardGrades finders
//   FindExactRewardGrade, HasAnyRewardGrade, FindPreviousRewardGrade (:359-394) .. this file (over RewardGrades' *Reward finders)
//   LogGearQuality, NormalizeGearQuality (:396-431) ...................... Items.CreateRewardItem (task 5.2a)
//   DestroyUndelivered (:433-437) ........................................ this file -> IRewardDeliveryHost.Destroy
//   PurchaseAccounting (:439-463), ItemDelivery (:465-502), DeliverPurchase (:504-520) .. this file
//   TryCreate, CreateBlueprintReward, TryCreateSword (:522-611) .......... Items.CreateRewardItem / CreateBlueprintReward (task 5.2a)

/// <summary>What RewardDelivery.cpp reads and does through the engine and the store (the glue implements it over PgEngine, SquadUtil,
/// LeaderboardStore, FighterIdentity and src/Engine/Items.cs). <typeparamref name="TItem"/> is the adapter's item handle (a reward item made,
/// found or taken out); the logic only passes it back.</summary>
public interface IRewardDeliveryHost<TId, TItem> where TItem : class
{
    /// <summary><c>!character</c>: no character at all.</summary>
    bool IsNull(TId c);
    /// <summary><c>character &amp;&amp; character-&gt;isValid()</c>.</summary>
    bool Valid(TId c);
    /// <summary><c>IsPlayerSquadFighter(character)</c> (:96-106): the character is one of <c>SquadUtil::CollectPlayerSquad</c>.</summary>
    bool IsPlayerSquadFighter(TId c);
    /// <summary><c>ou &amp;&amp; ou-&gt;theFactory</c> (:778, :1031): the reward-item adapter may START a purchase (task 5.2a's
    /// <c>Items.CanStart</c>; asked once per purchase, never between its moves).</summary>
    bool ItemsCanStart();
    /// <summary><c>character-&gt;getInventory()</c> is non-null.</summary>
    bool HasInventory(TId c);
    /// <summary><c>character-&gt;getName()</c>.</summary>
    string Name(TId c);

    // ---- the store (LeaderboardStore, task 5.1) ----
    /// <summary><c>LeaderboardStore::GetMarks</c>.</summary>
    int GetMarks(TId c);
    /// <summary><c>LeaderboardStore::GetRewardTier</c>.</summary>
    int GetRewardTier(TId c, string rewardId);
    /// <summary><c>LeaderboardStore::HasFactionUnlock</c>.</summary>
    bool HasFactionUnlock(string unlockId);
    /// <summary><c>LeaderboardStore::ReserveProgression(character, purchase, accounting)</c>.</summary>
    bool ReserveProgression(TId c, ArenaLedger.Purchase purchase, RewardTransaction.ReservedAccounting accounting);
    /// <summary>PurchaseAccounting::Matches (:447-452): <c>actor.getCharacter() == character &amp;&amp; FighterIdentity::Find(character, current)
    /// &amp;&amp; current == identity</c> (the handle still names the same character and its identity token is <paramref name="identity"/>).</summary>
    bool IdentityMatches(TId c, string identity);

    // ---- the items (src/Engine/Items.cs, task 5.2a) ----
    /// <summary><c>ou-&gt;gamedata.getData(sid)</c> is non-null.</summary>
    bool RecordExists(string recordId);
    /// <summary>The research record and the blueprint item that wraps it both exist (:1053-1060: <c>researchData</c> and
    /// <c>deliveryData</c>).</summary>
    bool BlueprintRecordsExist(string researchId);
    /// <summary>Every copy of the record the character holds, in the C++ search order (task 5.2a <c>Items.Copies</c>).</summary>
    IReadOnlyList<TItem> Copies(TId c, string recordId);
    /// <summary><c>Item::getLevel()</c>.</summary>
    int Level(TItem item);
    /// <summary><c>inventory-&gt;hasRoomForItem(itemData)</c> (:851, :1070) for the record, asked before the item is made.</summary>
    bool HasRoomForRecord(TId c, string recordId);
    /// <summary><c>inventory-&gt;hasRoomForItem(deliveryData)</c> (:1070) for the blueprint of a research, asked before it is made.</summary>
    bool HasRoomForBlueprint(TId c, string researchId);
    /// <summary><c>CreateRewardItem</c> (:613-709): a new loose item, or null.</summary>
    TItem? CreateRewardItem(string recordId, int qualityLevel, string? modelStringId, string? manufacturerStringId);
    /// <summary><c>CreateBlueprintReward</c> (:551-583): a new loose blueprint, or null.</summary>
    TItem? CreateBlueprintReward(string researchId);
    /// <summary><c>newItem-&gt;chargesLeft = charges</c> (:1084-1085).</summary>
    void SetCharges(TItem item, int charges);
    /// <summary><c>inventory-&gt;removeItemDontDestroy_returnsItem(item, 1, false)</c> (:478, :484): one unit of a held item taken out, kept
    /// alive by the returned handle; null on a failure.</summary>
    TItem? TakeOut(TId c, TItem held);
    /// <summary><c>character-&gt;giveItem(item, false, destroyOnFail)</c> (:481, :492).</summary>
    bool Give(TId c, TItem item, bool destroyOnFail);
    /// <summary><c>ou-&gt;destroy(item, false, reason)</c> (DestroyUndelivered :433-437).</summary>
    void Destroy(TId c, TItem item);
    /// <summary><c>RewardDiag</c> (:42-46): the C++ wrote each line at ERROR and DEBUG; the port at DEBUG only.</summary>
    void Diag(string line);
    /// <summary>A Meitou error line (port only): a purchase whose rollback failed (Marks or equipment left wrong), with the record and level.</summary>
    void Error(string line);
}

/// <summary>What one purchase command did (task 5.2b review): <see cref="OfferChanged"/> = not run at all, because the offer the window showed is
/// no longer the offer (the status is then empty: the window keeps its status line).</summary>
public readonly record struct RewardPurchaseResult(bool Ok, string Status, bool OfferChanged);

/// <summary>RewardDelivery.cpp's rules over a host (task 5.2b): offers, claims, upgrades, half-cost replacements, licences and general items,
/// each a reservation in the ledger (Core <see cref="ArenaLedger"/>) and, for an item, the two-phase <see cref="RewardTransaction.Deliver"/>.
/// Every status text is the C++'s, byte for byte.</summary>
public sealed class RewardDeliveryLogic<TId, TItem> where TItem : class
{
    readonly IRewardDeliveryHost<TId, TItem> h;

    public RewardDeliveryLogic(IRewardDeliveryHost<TId, TItem> host) => h = host ?? throw new ArgumentNullException(nameof(host));

    // ---------- the anonymous-namespace helpers that decide something ----------

    IReadOnlyList<TItem> AlternateCopies(TId c, ArenaRewards.Piece piece, string primary) =>
        RewardGrades.AlternateRecord(piece, primary) is { } alternate ? h.Copies(c, alternate) : [];

    int[] Levels(IReadOnlyList<TItem> items)
    {
        var levels = new int[items.Count];
        for (int i = 0; i < items.Count; ++i) levels[i] = h.Level(items[i]);
        return levels;
    }

    // C++: RewardDelivery.cpp:359-370
    TItem? FindExactRewardGrade(TId c, ArenaRewards.Piece piece, string primary, int qualityLevel)
    {
        var mine = h.Copies(c, primary);
        var other = AlternateCopies(c, piece, primary);
        var (alternate, index) = RewardGrades.FindExactReward(piece, Levels(mine), Levels(other), qualityLevel);
        return index < 0 ? null : (alternate ? other : mine)[index];
    }

    // C++: RewardDelivery.cpp:372-381
    bool HasAnyRewardGrade(TId c, ArenaRewards.Piece piece, string primary) =>
        RewardGrades.HasAnyReward(piece, h.Copies(c, primary).Count, AlternateCopies(c, piece, primary).Count);

    // C++: RewardDelivery.cpp:383-394
    TItem? FindPreviousRewardGrade(TId c, ArenaRewards.Piece piece, string primary, int qualityLevel)
    {
        var mine = h.Copies(c, primary);
        var other = AlternateCopies(c, piece, primary);
        var (alternate, index) = RewardGrades.FindPreviousReward(piece, Levels(mine), Levels(other), qualityLevel);
        return index < 0 ? null : (alternate ? other : mine)[index];
    }

    // C++: RewardDelivery.cpp:433-437 (the reason text is not kept: Meitou's destroy takes none)
    void DestroyUndelivered(TId c, TItem? item)
    {
        if (item is not null) h.Destroy(c, item);
    }

    /// <summary>C++: RewardDelivery.cpp:439-463 (<c>PurchaseAccounting</c>): the binding (the same character, the same identity token) and
    /// the ledger reservation through LeaderboardStore::ReserveProgression.</summary>
    sealed class PurchaseAccounting : RewardTransaction.SelfBoundReservedAccounting
    {
        readonly IRewardDeliveryHost<TId, TItem> h;
        readonly TId character;

        // C++: RewardDelivery.cpp:445-446
        public PurchaseAccounting(IRewardDeliveryHost<TId, TItem> host, TId c) { h = host; character = c; }

        // C++: RewardDelivery.cpp:447-452
        public override bool Matches(string identity) => h.IdentityMatches(character, identity);

        /// <summary>Port only (review I1): the transaction reached the completion phase / the reservation was accepted, so a recovery after
        /// an exception knows which failure it was and that an accepted purchase must not be rolled back.</summary>
        public bool CompleteStarted { get; private set; }
        public bool Accepted { get; private set; }
        public override bool Complete() { CompleteStarted = true; return base.Complete(); }
        public override bool Accept() { bool ok = base.Accept(); Accepted |= ok; return ok; }

        // C++: RewardDelivery.cpp:453-462
        public bool ReservePurchase(int cost, string? reward = null, int previous = -1, int next = -1, string? unlock = null,
            bool requireNewUnlock = false)
        {
            var purchase = new ArenaLedger.Purchase(cost);
            if (reward is not null) purchase.Reward = reward;
            purchase.PreviousTier = previous; purchase.NextTier = next;
            if (unlock is not null) purchase.Unlock = unlock;
            purchase.RequireNewUnlock = requireNewUnlock;
            return h.ReserveProgression(character, purchase, this);
        }
    }

    /// <summary>C++: RewardDelivery.cpp:465-502 (<c>ItemDelivery</c>): the inventory half of the transaction. The previous grade is kept
    /// alive (<c>retained</c>) until both delivery and accounting succeed.</summary>
    sealed class ItemDelivery : RewardTransaction.IInventory
    {
        readonly IRewardDeliveryHost<TId, TItem> h;
        readonly TId character;
        TItem? replacement;
        readonly TItem? previous;
        TItem? retained;

        // C++: RewardDelivery.cpp:473-474
        public ItemDelivery(IRewardDeliveryHost<TId, TItem> host, TId c, TItem item, TItem? old = null)
        {
            h = host; character = c; replacement = item; previous = old; retained = null;
        }

        // C++: RewardDelivery.cpp:475-480
        public bool RemovePrevious()
        {
            if (previous is null) return true;
            retained = h.TakeOut(character, previous);
            return retained is not null;
        }

        /// <summary>Port only (review I1): DeliverNew was called / succeeded (the recovery after an exception reads them).</summary>
        public bool DeliverStarted { get; private set; }
        public bool Delivered { get; private set; }

        // C++: RewardDelivery.cpp:481
        public bool DeliverNew()
        {
            DeliverStarted = true;
            Delivered = replacement is not null && h.Give(character, replacement, true);
            return Delivered;
        }

        /// <summary>Port only (review I1): after an exception, the new item out of the inventory (if it was delivered) and destroyed; true when
        /// it is not left in the inventory.</summary>
        public bool UndoNew()
        {
            if (Delivered && !RemoveNew()) return false;
            DestroyNew();
            return true;
        }

        // C++: RewardDelivery.cpp:482-488
        public bool RemoveNew()
        {
            if (replacement is null) return false;
            TItem? removed = h.TakeOut(character, replacement);
            if (removed is null) return false;
            replacement = removed;
            return true;
        }

        // C++: RewardDelivery.cpp:489-499
        public bool RestorePrevious()
        {
            if (retained is null) return true;
            if (!h.Give(character, retained, false))
            {
                h.Diag("Proving Grounds: inventory rollback could not restore retained previous reward item");
                return false;
            }
            retained = null;
            return true;
        }

        // C++: RewardDelivery.cpp:500
        public void DestroyNew()
        {
            if (replacement is not null) h.Destroy(character, replacement);
            replacement = null;
        }

        // C++: RewardDelivery.cpp:501
        public void DestroyPrevious()
        {
            if (retained is not null) h.Destroy(character, retained);
            retained = null;
        }
    }

    /// <summary>A step of the recovery after an exception: false when it fails or throws (logged).</summary>
    bool Safe(Func<bool> step)
    {
        try { return step(); }
        catch (Exception e) { h.Diag("Proving Grounds: reward rollback step threw " + e.GetType().Name + ": " + e.Message); return false; }
    }

    /// <summary>Port only (review I1): an exception inside the transaction (after the reservation) is answered like the failure of the phase
    /// it came from, with the same undo as RewardTransaction.Deliver (the new item out and destroyed, the previous grade given back, the
    /// reservation cancelled), each step best effort, so the ledger is never left reserved. An accepted purchase stays a success.</summary>
    RewardTransaction.Result Recover(ItemDelivery delivery, PurchaseAccounting accounting, Exception e)
    {
        h.Diag("Proving Grounds: reward purchase threw " + e.GetType().Name + ": " + e.Message);
        if (accounting.Accepted) return RewardTransaction.Result.Success;
        var failure = accounting.CompleteStarted ? RewardTransaction.Result.CompletionFailed
            : delivery.DeliverStarted ? RewardTransaction.Result.DeliveryFailed : RewardTransaction.Result.RemovalFailed;
        bool removed = Safe(delivery.UndoNew);
        bool restored = Safe(delivery.RestorePrevious);
        bool refunded = Safe(accounting.Rollback);
        if (!(removed && restored) && !refunded) return RewardTransaction.Result.RollbackFailed;
        if (!refunded) return RewardTransaction.Result.AccountingRollbackFailed;
        return removed && restored ? failure : RewardTransaction.Result.InventoryRollbackFailed;
    }

    /// <summary>C++: RewardDelivery.cpp:504-520 (<c>DeliverPurchase</c>): the transaction, and its failure text. Port: an exception is recovered
    /// (<see cref="Recover"/>); a failed rollback is also a Meitou error line naming the record and level.</summary>
    bool DeliverPurchase(ItemDelivery delivery, PurchaseAccounting accounting, string record, int level, out string status)
    {
        status = "";
        RewardTransaction.Result result;
        try { result = RewardTransaction.Deliver(delivery, accounting); }
        catch (Exception e) { result = Recover(delivery, accounting, e); }
        if (result == RewardTransaction.Result.Success) return true;
        if (result == RewardTransaction.Result.AccountingRollbackFailed || result == RewardTransaction.Result.RollbackFailed)
            status = "The purchase failed and accounting could not be restored. Check Arena Marks and equipment before saving.";
        else if (result == RewardTransaction.Result.InventoryRollbackFailed)
            status = "The purchase failed and inventory restoration failed. Check the fighter's equipment before saving.";
        else if (result == RewardTransaction.Result.RemovalFailed)
            status = "The previous grade could not be removed; the purchase was cancelled.";
        else if (result == RewardTransaction.Result.DeliveryFailed)
            status = "The reward would not fit; the purchase was rolled back.";
        else
            status = "Arena progression became unavailable; the purchase was cancelled.";
        h.Diag(status);
        if (result is RewardTransaction.Result.RollbackFailed or RewardTransaction.Result.AccountingRollbackFailed
            or RewardTransaction.Result.InventoryRollbackFailed)
            h.Error("Proving Grounds: " + status + " (record " + record + " level " + Num(level) + ")");
        return false;
    }

    /// <summary>The licences' completion (RewardDelivery.cpp:962-968, :1007-1013): <c>Complete() &amp;&amp; Accept()</c>, else a rollback. Port
    /// (review I1): an exception counts as a failed completion and is rolled back too; a failed rollback is also a Meitou error line.</summary>
    bool CompleteLicence(PurchaseAccounting accounting, string unlock, string cancelled, string notRestored, out string status)
    {
        status = "";
        bool completed;
        try { completed = accounting.Complete() && accounting.Accept(); }
        catch (Exception e)
        {
            h.Diag("Proving Grounds: reward purchase threw " + e.GetType().Name + ": " + e.Message);
            completed = accounting.Accepted;
        }
        if (completed) return true;
        bool rolledBack = Safe(accounting.Rollback);
        status = rolledBack ? cancelled : notRestored;
        if (!rolledBack) h.Error("Proving Grounds: " + status + " (unlock " + unlock + ")");
        return false;
    }

    static string Num(int n) => n.ToString(System.Globalization.CultureInfo.InvariantCulture);

    // ---------- the public functions (RewardDelivery.h:16-50) ----------

    /// <summary>C++: RewardDelivery.cpp:613-709 (<c>CreateRewardItem</c>): the adapter's (task 5.2a); the Rewards window's preview calls it.</summary>
    public TItem? CreateRewardItem(string recordId, int qualityLevel, string? weaponModelStringId = null, string? weaponManufacturerStringId = null) =>
        h.CreateRewardItem(recordId, qualityLevel, weaponModelStringId, weaponManufacturerStringId);

    /// <summary>C++: RewardDelivery.cpp:711-723 (<c>ResolveRewardItemData</c>): the record the piece is delivered as (the cloak always 89, spike
    /// S13: <see cref="RewardGrades.ItemRecord"/>), or null when the game data lacks it.</summary>
    public string? ResolveRewardItemData(ArenaRewards.Piece piece)
    {
        string record = RewardGrades.ItemRecord(piece);
        return h.RecordExists(record) ? record : null;
    }

    /// <summary>C++: RewardDelivery.cpp:725-760 (<c>ResolveOffer</c>): claim / half-cost replace / complete for the Rewards window.</summary>
    public ArenaRewards.Offer ResolveOffer(TId character, ArenaRewards.Piece piece)
    {
        ArenaRewards.Offer offer;
        offer.Kind = ArenaRewards.OfferKind.Complete;
        offer.Tier = ArenaRewards.Tier.MaxCount;
        offer.Cost = 0;
        if (h.IsNull(character) || !h.Valid(character))
            return offer;

        int highestTier = h.GetRewardTier(character, ArenaRewards.StableId(piece));
        string? itemData = ResolveRewardItemData(piece);
        bool inventory = h.HasInventory(character);

        bool hasExactPrevious = false;
        bool hasAny = false;
        ArenaRewards.Tier next = ArenaRewards.NextTier(highestTier, piece);
        if (inventory && itemData is not null)
        {
            hasAny = HasAnyRewardGrade(character, piece, itemData);
            if (next != ArenaRewards.Tier.MaxCount && next > ArenaRewards.FirstTier(piece))
            {
                ArenaRewards.Reward previousReward = ArenaRewards.Get(piece, next - 1);
                hasExactPrevious = FindExactRewardGrade(character, piece, itemData, previousReward.QualityLevel) is not null;
            }
        }

        return ArenaRewards.ResolveOffer(highestTier, piece, hasExactPrevious, hasAny);
    }

    /// <summary>C++: RewardDelivery.cpp:762-919 (<c>ClaimNext</c>): claims the next tier (an upgrade takes the previous grade out, delivers the
    /// new one and destroys the old one only when both delivery and accounting succeed), or replaces a lost claimed grade at half cost.</summary>
    public bool ClaimNext(TId character, ArenaRewards.Piece piece, out string status)
    {
        status = "";
        if (h.IsNull(character) || !h.Valid(character))
        {
            status = "Select a valid squad fighter.";
            return false;
        }
        if (!h.IsPlayerSquadFighter(character))
        {
            status = "Prisoners retain Marks but cannot claim rewards.";
            return false;
        }
        if (!h.ItemsCanStart())
        {
            status = "The world item factory is not ready.";
            return false;
        }

        if (!h.HasInventory(character))
        {
            status = "The selected fighter has no accessible inventory.";
            return false;
        }

        ArenaRewards.ArmourSet armourSet = ArenaRewards.SetFor(piece);
        if (ArenaRewards.IsWeapon(piece) && !h.HasFactionUnlock(ArenaRewards.GetWeaponLicence().StableId))
        {
            status = "Unlock the weapons licence before purchasing weapons.";
            return false;
        }
        if (armourSet != ArenaRewards.ArmourSet.None && !h.HasFactionUnlock(ArenaRewards.GetLicence(armourSet).StableId))
        {
            status = "Unlock this armour licence before purchasing pieces.";
            return false;
        }

        ArenaRewards.Offer offer = ResolveOffer(character, piece);
        if (offer.Kind == ArenaRewards.OfferKind.Complete)
        {
            status = "All grades of this reward are already claimed.";
            return false;
        }

        ArenaRewards.Reward reward = ArenaRewards.Get(piece, offer.Tier);
        int marks = h.GetMarks(character);
        if (marks < offer.Cost)
        {
            status = "This fighter does not have enough Arena Marks.";
            return false;
        }

        string? itemData = ResolveRewardItemData(piece);
        if (itemData is null)
        {
            status = "The Warlord reward record could not be found.";
            h.Diag("Proving Grounds: missing reward FCS record " + reward.StringId);
            return false;
        }

        TItem? previous = null;
        int previousLevel = -1;
        if (offer.Kind == ArenaRewards.OfferKind.Claim && offer.Tier > ArenaRewards.FirstTier(piece))
        {
            ArenaRewards.Reward previousReward = ArenaRewards.Get(piece, offer.Tier - 1);
            previous = FindExactRewardGrade(character, piece, itemData, previousReward.QualityLevel);
            if (previous is null)
                previous = FindPreviousRewardGrade(character, piece, itemData, previousReward.QualityLevel);
            if (previous is null)
            {
                status = "Place the previous grade in this fighter's inventory to upgrade it.";
                return false;
            }
            previousLevel = h.Level(previous);
        }
        else if (!h.HasRoomForRecord(character, itemData))
        {
            status = "This fighter needs more inventory space for the reward.";
            return false;
        }

        TItem? newItem = CreateRewardItem(itemData, reward.QualityLevel, reward.ModelStringId, reward.ManufacturerStringId);
        if (newItem is null)
        {
            status = "The reward item could not be created.";
            h.Diag("Proving Grounds: createItem failed for " + itemData + " level " + Num(reward.QualityLevel));
            return false;
        }

        h.Diag("Proving Grounds: " + (offer.Kind == ArenaRewards.OfferKind.Replace ? "replace" : "claim") + " prev=" + Num(previousLevel)
            + " -> created=" + Num(h.Level(newItem)) + " (want " + Num(reward.QualityLevel) + ") for " + itemData + " cost=" + Num(offer.Cost));

        if (offer.Kind == ArenaRewards.OfferKind.Replace)
        {
            var replaceAccounting = new PurchaseAccounting(h, character);
            if (!replaceAccounting.ReservePurchase(offer.Cost))
            {
                DestroyUndelivered(character, newItem);   // "Proving Grounds replace payment failed"
                status = "The replacement purchase could not be reserved.";
                return false;
            }
            var replaceDelivery = new ItemDelivery(h, character, newItem);
            if (!DeliverPurchase(replaceDelivery, replaceAccounting, itemData, reward.QualityLevel, out status)) return false;
            status = reward.PieceName + " - " + reward.TierName + " replaced for " + h.Name(character) + ".";
            h.Diag("Proving Grounds: reward replaced: " + itemData + " / " + reward.TierName);
            return true;
        }

        int previousTier = h.GetRewardTier(character, ArenaRewards.StableId(piece));
        var accounting = new PurchaseAccounting(h, character);
        if (!accounting.ReservePurchase(offer.Cost, ArenaRewards.StableId(piece), previousTier, (int)offer.Tier))
        {
            DestroyUndelivered(character, newItem);   // "Proving Grounds reward reservation failed"
            status = "The reward purchase could not be reserved.";
            return false;
        }

        var delivery = new ItemDelivery(h, character, newItem, previous);
        if (!DeliverPurchase(delivery, accounting, itemData, reward.QualityLevel, out status)) return false;

        status = reward.PieceName + " - " + reward.TierName + " claimed for " + h.Name(character) + ".";
        h.Diag("Proving Grounds: reward delivered: " + itemData + " / " + reward.TierName);
        return true;
    }

    /// <summary>C++: RewardDelivery.cpp:921-973 (<c>PurchaseLicence</c>): faction-wide access to one armour set; no item is delivered.</summary>
    public bool PurchaseLicence(TId character, ArenaRewards.ArmourSet set, out string status)
    {
        status = "";
        if (h.IsNull(character) || !h.Valid(character) || !h.IsPlayerSquadFighter(character))
        {
            status = "Select an eligible squad fighter.";
            return false;
        }
        ArenaRewards.Licence licence = ArenaRewards.GetLicence(set);
        bool owned = h.HasFactionUnlock(licence.StableId);
        bool prerequisite = licence.PrerequisiteId is null || h.HasFactionUnlock(licence.PrerequisiteId);
        int marks = h.GetMarks(character);
        if (owned)
        {
            status = "This armour licence is already unlocked.";
            return false;
        }
        if (!prerequisite)
        {
            status = "The previous armour licence is required.";
            return false;
        }
        if (!ArenaRewards.CanPurchaseLicence(set, owned, prerequisite, marks))
        {
            status = "This fighter does not have enough Arena Marks.";
            return false;
        }
        var accounting = new PurchaseAccounting(h, character);
        if (!accounting.ReservePurchase(licence.Cost, null, -1, -1, licence.StableId, true))
        {
            status = "The armour licence purchase could not be reserved.";
            return false;
        }
        if (!CompleteLicence(accounting, licence.StableId,
                "Arena progression became unavailable; the armour licence purchase was cancelled.",
                "The armour licence purchase failed and accounting could not be restored.", out status))
            return false;

        status = licence.Name + " licence unlocked for the faction.";
        return true;
    }

    /// <summary>C++: RewardDelivery.cpp:975-1017 (<c>PurchaseWeaponLicence</c>): faction-wide access to every weapon reward.</summary>
    public bool PurchaseWeaponLicence(TId character, out string status)
    {
        status = "";
        if (h.IsNull(character) || !h.Valid(character) || !h.IsPlayerSquadFighter(character))
        {
            status = "Select an eligible squad fighter.";
            return false;
        }
        ArenaRewards.Licence licence = ArenaRewards.GetWeaponLicence();
        bool owned = h.HasFactionUnlock(licence.StableId);
        int marks = h.GetMarks(character);
        if (owned)
        {
            status = "The weapons licence is already unlocked.";
            return false;
        }
        if (!ArenaRewards.CanPurchaseWeaponLicence(owned, marks))
        {
            status = "This fighter does not have enough Arena Marks.";
            return false;
        }
        var accounting = new PurchaseAccounting(h, character);
        if (!accounting.ReservePurchase(licence.Cost, null, -1, -1, licence.StableId, true))
        {
            status = "The weapons licence purchase could not be reserved.";
            return false;
        }
        if (!CompleteLicence(accounting, licence.StableId,
                "Arena progression became unavailable; the weapons licence purchase was cancelled.",
                "The weapons licence purchase failed and accounting could not be restored.", out status))
            return false;

        status = "Weapons licence unlocked for the faction.";
        return true;
    }

    /// <summary>C++: RewardDelivery.cpp:1019-1101 (<c>PurchaseGeneral</c>): purchases and delivers one repeatable blueprint book or supply item.</summary>
    public bool PurchaseGeneral(TId character, ArenaRewards.GeneralItem item, out string status)
    {
        status = "";
        if (h.IsNull(character) || !h.Valid(character) || !h.IsPlayerSquadFighter(character))
        {
            status = "Select an eligible squad fighter.";
            return false;
        }
        if (!h.ItemsCanStart())
        {
            status = "The world item factory is not ready.";
            return false;
        }

        ArenaRewards.GeneralReward reward = ArenaRewards.GetGeneral(item);
        if (reward.PrerequisiteId is not null && !h.HasFactionUnlock(reward.PrerequisiteId))
        {
            status = "Purchase Arena Buildings I first.";
            return false;
        }
        if (h.GetMarks(character) < reward.Cost)
        {
            status = "This fighter does not have enough Arena Marks.";
            return false;
        }

        bool inventory = h.HasInventory(character);
        bool itemData = h.RecordExists(reward.StringId);
        string? research = reward.BlueprintResearchStringId;
        bool researchAndDelivery = research is null || h.BlueprintRecordsExist(research);
        if (!inventory || !itemData || !researchAndDelivery)
        {
            if (!inventory)
                status = "The selected fighter has no accessible inventory.";
            else if (!itemData)
                status = "The reward item record could not be found.";
            else
                status = "The blueprint research record could not be found.";
            return false;
        }
        if (research is not null ? !h.HasRoomForBlueprint(character, research) : !h.HasRoomForRecord(character, reward.StringId))
        {
            status = "This fighter needs more inventory space for the reward.";
            return false;
        }

        TItem? newItem = research is not null
            ? h.CreateBlueprintReward(research)
            : CreateRewardItem(reward.StringId, -1);
        if (newItem is null)
        {
            status = "The reward item could not be created.";
            return false;
        }
        if (reward.Charges > 0)
            h.SetCharges(newItem, reward.Charges);
        var accounting = new PurchaseAccounting(h, character);
        if (!accounting.ReservePurchase(reward.Cost, null, -1, -1, reward.GrantsUnlockId))
        {
            DestroyUndelivered(character, newItem);   // "Proving Grounds general reward payment failed"
            status = "The reward purchase could not be reserved.";
            return false;
        }

        var delivery = new ItemDelivery(h, character, newItem);
        if (!DeliverPurchase(delivery, accounting, research ?? reward.StringId, h.Level(newItem), out status)) return false;

        status = reward.Name + " purchased for " + h.Name(character) + ".";
        return true;
    }

    // ---------- the purchase command (port only, task 5.2b review I2) ----------

    static bool Known<T>(int value, T sentinel) where T : struct, Enum
    {
        T v = (T)(object)value;
        return Enum.IsDefined(v) && !EqualityComparer<T>.Default.Equals(v, sentinel);
    }

    /// <summary>The Rewards window's button as one command (RewardsUI.cpp:489-561 called the function in the click): the purchase its kind
    /// names. <paramref name="shown"/> = the offer the window showed for a Claim (null: no check, the test verb): the C++ ran the purchase in the
    /// click, against the offer on screen; a command is applied later, so one whose offer changed since it was shown (a second click before the
    /// window saw the first one's result) is NOT run: not ok, an empty status (no invented text; the window keeps its line), a debug line. An
    /// unknown kind or item (a bad command, never a button) is refused with the C++ text "The reward item record could not be found.".
    /// An exception the transaction's own recovery did not take (one before the reservation, or in a read) is a failure with the C++ text
    /// <see cref="ExceptionText"/> and a Meitou error line; it never escapes, so the window always gets a result for its sequence.</summary>
    public RewardPurchaseResult Purchase(TId character, RewardPurchaseKind kind, int item, ArenaRewards.Offer? shown)
    {
        try { return PurchaseUnguarded(character, kind, item, shown); }
        catch (Exception e)
        {
            h.Error("Proving Grounds: reward purchase " + kind + " " + ItemName(kind, item) + " threw " + e.GetType().Name + ": " + e.Message);
            return new RewardPurchaseResult(false, ExceptionText, false);
        }
    }

    static string ItemName(RewardPurchaseKind kind, int item) => kind switch
    {
        RewardPurchaseKind.Claim when Known(item, ArenaRewards.Piece.Count) => ((ArenaRewards.Piece)item).ToString(),
        RewardPurchaseKind.Licence when Known(item, ArenaRewards.ArmourSet.None) => ((ArenaRewards.ArmourSet)item).ToString(),
        RewardPurchaseKind.General when Known(item, ArenaRewards.GeneralItem.Count) => ((ArenaRewards.GeneralItem)item).ToString(),
        _ => Num(item),
    };

    /// <summary>The status of a purchase that threw (port only): the C++ text of a purchase whose state could not be restored
    /// (RewardDelivery.cpp:509), as the safe advice when it is not known what the exception left behind.</summary>
    public const string ExceptionText = "The purchase failed and accounting could not be restored. Check Arena Marks and equipment before saving.";

    RewardPurchaseResult PurchaseUnguarded(TId character, RewardPurchaseKind kind, int item, ArenaRewards.Offer? shown)
    {
        const string unknown = "The reward item record could not be found.";
        bool ok;
        string status;
        switch (kind)
        {
            case RewardPurchaseKind.Claim when Known(item, ArenaRewards.Piece.Count):
                var piece = (ArenaRewards.Piece)item;
                if (shown is { } was)
                {
                    ArenaRewards.Offer now = ResolveOffer(character, piece);
                    if (now.Kind != was.Kind || now.Tier != was.Tier || now.Cost != was.Cost)
                    {
                        h.Diag("Proving Grounds: reward purchase skipped: the offer of " + piece + " changed since it was shown (" + was.Kind + " "
                            + was.Tier + " " + Num(was.Cost) + " -> " + now.Kind + " " + now.Tier + " " + Num(now.Cost) + ")");
                        return new RewardPurchaseResult(false, "", true);
                    }
                }
                ok = ClaimNext(character, piece, out status);
                break;
            case RewardPurchaseKind.Licence when Known(item, ArenaRewards.ArmourSet.None):
                ok = PurchaseLicence(character, (ArenaRewards.ArmourSet)item, out status);
                break;
            case RewardPurchaseKind.WeaponLicence:
                ok = PurchaseWeaponLicence(character, out status);
                break;
            case RewardPurchaseKind.General when Known(item, ArenaRewards.GeneralItem.Count):
                ok = PurchaseGeneral(character, (ArenaRewards.GeneralItem)item, out status);
                break;
            default:
                return new RewardPurchaseResult(false, unknown, false);
        }
        return new RewardPurchaseResult(ok, status, false);
    }
}
