// Port of upstream/ProvingGrounds/plugin/src/RewardTransaction.h (Proving Grounds by Shutori, GPLv3).
namespace ProvingGrounds.Core;

/// <summary>C++ namespace <c>RewardTransaction</c> (header-only, RewardTransaction.h:1-77): the two-phase purchase (accounting +
/// inventory, or accounting + one irreversible action) with rollback. PA §2.18.
/// <para>The C++ interfaces: <c>Accounting</c> has a default <c>Accept</c> and a concrete subclass, so it is an abstract class;
/// <c>Binding</c>, <c>Inventory</c> and <c>Action</c> are pure, so they are interfaces (<see cref="IBinding"/>, <see cref="IInventory"/>,
/// <see cref="IAction"/>, the I prefix like 1.7a's IStorage; <c>IAction</c> also keeps clear of System.Action). The glue classes
/// derive from BOTH Binding and ReservedAccounting (RewardDelivery.cpp:439-446, PrisonerRecruitment.cpp:34-38), which C# allows
/// only with Binding as an interface.</para></summary>
public static class RewardTransaction
{
    /// <summary>C++: RewardTransaction.h:4</summary>
    public enum Result
    {
        Success,
        RemovalFailed,
        DeliveryFailed,
        CompletionFailed,
        InventoryRollbackFailed,
        AccountingRollbackFailed,
        RollbackFailed,
    }

    /// <summary>C++: RewardTransaction.h:5-10</summary>
    public abstract class Accounting
    {
        public abstract bool Complete();
        public abstract bool Rollback();
        // C++: RewardTransaction.h:9
        public virtual bool Accept() { return true; }
    }

    /// <summary>C++: RewardTransaction.h:11-14</summary>
    public interface IBinding
    {
        bool Matches(string id);
    }

    /// <summary>C++: RewardTransaction.h:17-35 (comment 15-16). Shared by native purchases and recruitment; cancellation owns the exact reserved
    /// change even if the live binding has since disappeared. Not copyable in the C++ (private copy constructor and assignment);
    /// a C# class is never copied implicitly.
    /// <para>The C++ glue passes <c>static_cast&lt;Binding&amp;&gt;(*this)</c> to the base constructor; C# cannot pass <c>this</c>
    /// to a base constructor, so a glue class that is itself the binding derives from <see cref="SelfBoundReservedAccounting"/>, whose
    /// constructor binds to <c>this</c> (the same object the C++ binds to).</para></summary>
    public class ReservedAccounting : Accounting
    {
        readonly IBinding binding;
        ArenaLedger.Ledger? ledger;
        ArenaLedger.Reservation reservation;
        string identity;

        // C++: RewardTransaction.h:25
        public ReservedAccounting(IBinding source)
        {
            binding = source ?? throw new ArgumentNullException(nameof(source));
            ledger = null; reservation = default; identity = "";
        }

        /// <summary>For <see cref="SelfBoundReservedAccounting"/> only: binds to the object itself (the C++ glue's
        /// <c>ReservedAccounting(static_cast&lt;Binding&amp;&gt;(*this))</c>).</summary>
        private protected ReservedAccounting()
        {
            binding = this as IBinding ?? throw new InvalidOperationException(GetType().FullName + " is bound to itself but does not implement IBinding");
            ledger = null; reservation = default; identity = "";
        }

        // C++: RewardTransaction.h:26-31
        public bool Reserve(ArenaLedger.Ledger owner, string id,
            string name, ArenaLedger.Purchase purchase)
        {
            if (ledger != null && ledger.Pending(reservation)) return false;
            if (!binding.Matches(id) || !owner.Reserve(id, name, purchase, ref reservation)) return false;
            ledger = owner; identity = id; return true;
        }

        // C++: RewardTransaction.h:32
        public override bool Complete() { return ledger != null && ledger.Pending(reservation) && binding.Matches(identity); }

        // C++: RewardTransaction.h:33
        public override bool Rollback() { return ledger != null && ledger.Cancel(reservation); }

        // C++: RewardTransaction.h:34
        public override bool Accept() { return ledger != null && ledger.Accept(reservation); }
    }

    /// <summary>The glue shape of RewardDelivery.cpp:439-446 / PrisonerRecruitment.cpp:34-38: a class that is both the Binding and the
    /// ReservedAccounting, the C++ passing <c>static_cast&lt;Binding&amp;&gt;(*this)</c> to the base. Deriving from this class makes the
    /// binding a compile-time obligation (abstract <see cref="Matches"/>); its constructor binds the accounting to <c>this</c>.</summary>
    public abstract class SelfBoundReservedAccounting : ReservedAccounting, IBinding
    {
        protected SelfBoundReservedAccounting() : base() { }
        public abstract bool Matches(string id);
    }

    /// <summary>C++: RewardTransaction.h:36-44</summary>
    public interface IInventory
    {
        bool RemovePrevious();
        bool DeliverNew();
        bool RemoveNew();
        bool RestorePrevious();
        void DestroyNew();
        void DestroyPrevious();
    }

    // C++: RewardTransaction.h:45-50
    public static Result Cancel(Accounting accounting, bool inventoryRestored, Result failure)
    {
        bool refunded = accounting.Rollback();
        if (!inventoryRestored && !refunded) return Result.RollbackFailed;
        if (!refunded) return Result.AccountingRollbackFailed;
        return inventoryRestored ? failure : Result.InventoryRollbackFailed;
    }

    /// <summary>C++: RewardTransaction.h:53-68 (comment 51-52). Accounting has already been reserved. Keep the previous item alive until both
    /// delivery and the memory-only completion succeed.</summary>
    public static Result Deliver(IInventory inventory, Accounting accounting)
    {
        if (!inventory.RemovePrevious())
        {
            inventory.DestroyNew(); return Cancel(accounting, true, Result.RemovalFailed);
        }
        if (!inventory.DeliverNew())
        {
            bool restored = inventory.RestorePrevious();
            inventory.DestroyNew(); return Cancel(accounting, restored, Result.DeliveryFailed);
        }
        if (!accounting.Complete() || !accounting.Accept())
        {
            bool removed = inventory.RemoveNew();
            if (removed) inventory.DestroyNew();
            bool restored = inventory.RestorePrevious();
            return Cancel(accounting, removed && restored, Result.CompletionFailed);
        }
        inventory.DestroyPrevious(); return Result.Success;
    }

    /// <summary>C++: RewardTransaction.h:69</summary>
    public interface IAction
    {
        bool Apply();
    }

    /// <summary>C++: RewardTransaction.h:72-76 (comment 70-71). Recruitment cannot be reversed safely after native success. Check memory readiness
    /// first; failed native recruitment still cancels its reservation.</summary>
    public static Result CompleteAction(IAction action, Accounting accounting)
    {
        if (!accounting.Complete()) return Cancel(accounting, true, Result.CompletionFailed);
        if (!action.Apply()) return Cancel(accounting, true, Result.DeliveryFailed);
        return accounting.Accept() ? Result.Success : Result.AccountingRollbackFailed;
    }
}
