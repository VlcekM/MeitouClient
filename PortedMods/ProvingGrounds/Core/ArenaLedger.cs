// Port of upstream/ProvingGrounds/plugin/src/ArenaLedger.h (Proving Grounds by Shutori, GPLv3).
namespace ProvingGrounds.Core;

/// <summary>C++ namespace <c>ArenaLedger</c> (header-only, ArenaLedger.h:1-124): the active world's snapshot with the purchase
/// reservation (reserve / cancel / accept) the reward and recruitment transactions use (RewardTransaction.cs). PA §2.18 / §2.20.
/// <para>The C++ copy assignments <c>snapshot = value</c> (Activate), <c>snapshot = Snapshot()</c> (Block) and <c>out = snapshot</c>
/// (Capture) are deep <see cref="ArenaPersistence.Snapshot.AssignFrom"/>s into the existing objects, so references to the held snapshot
/// and its parts (LeaderboardStore.cpp:30-34 keeps <c>State().fighters</c>, <c>.challenges</c>, ...) stay valid. <c>State()</c> and <c>Find()</c> return references in the C++ and return the held objects here,
/// so writes through them reach the ledger.</para>
/// <para><c>friend class Ledger</c> (Reservation's private members): the token's fields are <c>internal</c>, so only Core (and the
/// tests, InternalsVisibleTo) can read or forge them; callers outside Core hold the token opaquely, as in the C++.</para></summary>
public static class ArenaLedger
{
    /// <summary>C++: ArenaLedger.h:6-11. A value type (the C++ copies it: <c>change.purchase=purchase</c>; its strings are immutable
    /// in C#). The C++ constructor <c>Purchase(int amount = 0)</c> is not <c>explicit</c>, but no caller converts an int implicitly
    /// (all write <c>Purchase(cost)</c>), so no implicit operator is added.
    /// <para>WARNING: <c>default(Purchase)</c> is NOT the C++ default: it has tiers 0 and null strings. Use <c>new Purchase()</c>
    /// (tiers -1, empty strings) or <c>new Purchase(cost)</c>.</para></summary>
    public struct Purchase
    {
        public int Cost, PreviousTier, NextTier;
        public string Reward, Unlock;
        public bool RequireNewUnlock;

        // C++: ArenaLedger.h:10 (amount = 0)
        public Purchase() : this(0) { }

        // C++: ArenaLedger.h:10
        public Purchase(int amount)
        {
            Cost = amount; PreviousTier = -1; NextTier = -1;
            Reward = ""; Unlock = "";
            RequireNewUnlock = false;
        }
    }

    /// <summary>C++: ArenaLedger.h:12-18. A value type copied like the C++ token (all-zero = the C++ default: no owner, epoch 0,
    /// sequence 0, so <c>default(Reservation)</c> equals the C++ default). Fields <c>internal</c> for <c>friend class Ledger</c>.</summary>
    public struct Reservation
    {
        internal Ledger? Owner;
        internal ulong Epoch, Sequence;
    }

    /// <summary>C++: ArenaLedger.h:19-123. One active world owns this value. No filesystem or native identity fallback belongs
    /// here: callers supply a currently verified FighterIdentity token. Never copied in the C++ (one global in LeaderboardStore.cpp),
    /// so a class without Clone; a token's owner is compared by reference (<c>token.owner==this</c>).</summary>
    public sealed class Ledger
    {
        ArenaPersistence.Snapshot snapshot;
        bool ready;
        ulong epoch, sequence;
        bool reserved;

        /// <summary>C++: ArenaLedger.h:26-31. Assigned field by field (<c>change.id=id; change.purchase=purchase;</c>).</summary>
        struct Change
        {
            public string Id;
            public Purchase Purchase;
            public bool UnlockAdded;

            // C++: ArenaLedger.h:30
            public Change() { Id = ""; Purchase = new Purchase(); UnlockAdded = false; }
        }
        Change change;

        // C++: ArenaLedger.h:32
        void EndWorld() { reserved = false; if (epoch != ~0UL) ++epoch; }

        // C++: ArenaLedger.h:34 (snapshot default-constructed, change default-constructed)
        public Ledger()
        {
            snapshot = new ArenaPersistence.Snapshot();
            ready = false; epoch = 0; sequence = 0; reserved = false;
            change = new Change();
        }

        // C++: ArenaLedger.h:35 (snapshot = value: a deep copy INTO the held snapshot, which keeps its identity)
        public void Activate(ArenaPersistence.Snapshot value) { EndWorld(); snapshot.AssignFrom(value); ready = epoch != ~0UL; }

        // C++: ArenaLedger.h:36 (snapshot = Snapshot(): the held snapshot reset in place)
        public void Block() { EndWorld(); snapshot.AssignFrom(new ArenaPersistence.Snapshot()); ready = false; }

        // C++: ArenaLedger.h:37
        public bool Ready() { return ready; }

        /// <summary>C++: ArenaLedger.h:38. The held snapshot (a reference in the C++): writes through it change the ledger.</summary>
        public ArenaPersistence.Snapshot State() { return snapshot; }

        /// <summary>C++: ArenaLedger.h:39. <c>out = snapshot</c> deep-copies INTO the caller's object (it keeps its identity); on
        /// failure <paramref name="output"/> is left untouched.</summary>
        public bool Capture(ArenaPersistence.Snapshot output)
        {
            if (!ready || reserved) return false;
            output.AssignFrom(snapshot); return true;
        }

        /// <summary>C++: ArenaLedger.h:40-45. The row itself (a pointer in the C++), or null.</summary>
        public LeaderboardData.Progression? Find(string id)
        {
            if (!ready || id.Length == 0) return null;
            for (int i = 0; i < snapshot.Fighters.Progression.Count; ++i)
                if (snapshot.Fighters.Progression[i].Id == id) return snapshot.Fighters.Progression[i];
            return null;
        }

        // C++: ArenaLedger.h:46
        public int Marks(string id) { LeaderboardData.Progression? row = Find(id); return row != null ? row.Marks : 0; }

        // C++: ArenaLedger.h:47-50
        public int Tier(string id, string reward)
        {
            LeaderboardData.Progression? row = Find(id);
            return row != null ? RewardProgression.HighestTier(row.RewardTiers, reward) : -1;
        }

        // C++: ArenaLedger.h:51-55
        public bool Spend(string id, string name, int cost)
        {
            if (!ready || id.Length == 0 || cost < 0 || Marks(id) < cost) return false;
            LeaderboardData.EnsureProgression(snapshot.Fighters, id, name).Marks -= cost;
            return true;
        }

        // C++: ArenaLedger.h:56-60 (amount >= 0 is checked first, so INT_MAX - amount cannot overflow)
        public bool Refund(string id, string name, int amount)
        {
            if (!ready || id.Length == 0 || amount < 0 || Marks(id) > int.MaxValue - amount) return false;
            LeaderboardData.EnsureProgression(snapshot.Fighters, id, name).Marks += amount;
            return true;
        }

        // C++: ArenaLedger.h:61-67 (the row is created before SetHighestTier can refuse, as in the C++)
        public bool BeginTier(string id, string name, int cost, string reward, int previous, int next)
        {
            if (!ready || id.Length == 0 || cost < 0 || reward.Length == 0 || Marks(id) < cost) return false;
            LeaderboardData.Progression row = LeaderboardData.EnsureProgression(snapshot.Fighters, id, name);
            if (!RewardProgression.SetHighestTier(row.RewardTiers, reward, previous, next)) return false;
            row.Marks -= cost;
            return true;
        }

        // C++: ArenaLedger.h:68-75
        public bool RollbackTier(string id, int cost, string reward, int previous)
        {
            LeaderboardData.Progression? row = Find(id);
            if (row == null || cost < 0 || row.Marks > int.MaxValue - cost ||
                RewardProgression.HighestTier(row.RewardTiers, reward) <= previous) return false;
            RewardProgression.RestoreHighestTier(row.RewardTiers, reward, previous);
            row.Marks += cost;
            return true;
        }

        // C++: ArenaLedger.h:76-80
        public bool BeginUnlock(string id, string name, int cost, string unlock)
        {
            if (unlock.Length == 0 || RewardProgression.HasUnlock(snapshot.Fighters.FactionUnlocks, unlock) || !Spend(id, name, cost)) return false;
            RewardProgression.AddUnlock(snapshot.Fighters.FactionUnlocks, unlock);
            return true;
        }

        // C++: ArenaLedger.h:81-85
        public bool RollbackUnlock(string id, string name, int cost, string unlock)
        {
            if (!ready || id.Length == 0 || !RewardProgression.HasUnlock(snapshot.Fighters.FactionUnlocks, unlock) || !Refund(id, name, cost)) return false;
            RewardProgression.RemoveUnlock(snapshot.Fighters.FactionUnlocks, unlock);
            return true;
        }

        /// <summary>C++: ArenaLedger.h:86-100. <paramref name="token"/> is written only on success (a C++ reference parameter).</summary>
        public bool Reserve(string id, string name, Purchase purchase, ref Reservation token)
        {
            // Reward callbacks are synchronous. Refuse nested reservations instead
            // of letting two operations own the same fighter tier or global unlock.
            if (!ready || reserved || id.Length == 0 || sequence == ~0UL) return false;
            bool hasUnlock = RewardProgression.HasUnlock(snapshot.Fighters.FactionUnlocks, purchase.Unlock);
            if (purchase.RequireNewUnlock && (purchase.Unlock.Length == 0 || hasUnlock)) return false;
            bool paid = purchase.Reward.Length == 0 ? Spend(id, name, purchase.Cost) :
                BeginTier(id, name, purchase.Cost, purchase.Reward, purchase.PreviousTier, purchase.NextTier);
            if (!paid) return false;
            change.Id = id; change.Purchase = purchase;
            change.UnlockAdded = purchase.Unlock.Length != 0 && !hasUnlock;
            if (change.UnlockAdded) RewardProgression.AddUnlock(snapshot.Fighters.FactionUnlocks, purchase.Unlock);
            reserved = true; token.Owner = this; token.Epoch = epoch; token.Sequence = ++sequence;
            return true;
        }

        // C++: ArenaLedger.h:101-103
        public bool Pending(Reservation token)
        {
            return ready && reserved && ReferenceEquals(token.Owner, this) && token.Epoch == epoch && token.Sequence == sequence;
        }

        /// <summary>C++: ArenaLedger.h:104-117. A refused validation leaves the reservation pending, as in the C++.</summary>
        public bool Cancel(Reservation token)
        {
            if (!Pending(token)) return false;
            LeaderboardData.Progression? row = Find(change.Id);
            Purchase purchase = change.Purchase;
            // Validate all components before undoing any of them. Never overwrite
            // a later unrelated edit or overflow a payment restoration.
            if (row == null || row.Marks > int.MaxValue - purchase.Cost ||
                (purchase.Reward.Length != 0 && Tier(change.Id, purchase.Reward) != purchase.NextTier) ||
                (change.UnlockAdded && !RewardProgression.HasUnlock(snapshot.Fighters.FactionUnlocks, purchase.Unlock))) return false;
            row.Marks += purchase.Cost;
            if (purchase.Reward.Length != 0) RewardProgression.RestoreHighestTier(row.RewardTiers, purchase.Reward, purchase.PreviousTier);
            if (change.UnlockAdded) RewardProgression.RemoveUnlock(snapshot.Fighters.FactionUnlocks, purchase.Unlock);
            reserved = false; return true;
        }

        // C++: ArenaLedger.h:118-121
        public bool Accept(Reservation token)
        {
            if (!Pending(token)) return false;
            reserved = false; return true;
        }

        // C++: ArenaLedger.h:122
        public bool Complete() { return ready; }

        // ---- test seams (the C++ fields are private; the golden reads them with #define private public) ----
        internal bool ReservedForTest => reserved;
        internal ulong EpochForTest => epoch;
        internal ulong SequenceForTest => sequence;
        /// <summary>Puts the counters near their ~0ULL limits (unreachable otherwise: 2^64 calls).</summary>
        internal void SetCountersForTest(ulong epochValue, ulong sequenceValue) { epoch = epochValue; sequence = sequenceValue; }
    }
}
