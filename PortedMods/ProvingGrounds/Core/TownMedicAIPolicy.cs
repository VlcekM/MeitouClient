// Port of upstream/ProvingGrounds/plugin/src/TownMedicAIPolicy.h (Proving Grounds by Shutori, GPLv3).
namespace ProvingGrounds.Core;

/// <summary>C++: TownMedicAIPolicy.h (header-only, namespace <c>TownMedicAIPolicy</c>): how the town takes an NPC's AI over and
/// hands it back, the bed-delivery receipts and the order-receiver reservation tables (TW §2.9, "AI control mechanism").
/// <para>The <c>const void*</c> handles (characters, beds, order receivers) are <c>object?</c> compared by reference, as the C++
/// compares pointers; <c>null</c> is the C++ null pointer. No engine type enters Core: the glue passes its own objects.</para>
/// <para>The two templates over <c>Native</c> are generics over <see cref="INative"/> (the members they call), taken by
/// <c>ref</c> as the C++ takes <c>Native&amp;</c>. <c>ReceiverReservations&lt;Capacity&gt;</c> is one class whose capacity is a
/// constructor argument; the three C++ typedefs are <see cref="NewReservations"/> (10), <see cref="NewPatientReservations"/>
/// (16) and <see cref="NewExternalReservations"/> (4).</para>
/// <para>None of these classes is copied in the C++ (single globals in TownAftercare.cpp:29-33), so they have no Clone.</para></summary>
public static class TownMedicAIPolicy
{
    /// <summary>What the two templates call on their <c>Native</c> argument.</summary>
    public interface INative
    {
        void RestoreGoals();
        void ClearOrders();
        void Rethink();
        void ChooseGoal();
        void Schedule();
    }

    // C++: TownMedicAIPolicy.h:4-9
    public static void PrepareOwnedOrder<TNative>(ref TNative native) where TNative : INative
    {
        // addOrder(clearOld=true) performs native order replacement. Rethink
        // additionally queues body completion for a later update, which can
        // remove the replacement order after its first treatment frame.
        native.RestoreGoals();
    }

    // C++: TownMedicAIPolicy.h:10-16
    public static void ResumeNative<TNative>(ref TNative native) where TNative : INative
    {
        native.ClearOrders();
        native.Rethink();
        native.RestoreGoals();
        native.ChooseGoal();
        native.Schedule();
    }

    /// <summary>C++: TownMedicAIPolicy.h:17-46</summary>
    public sealed class Deliveries
    {
        /// <summary>C++: TownMedicAIPolicy.h:18-23 (the C++ default is all null / false, as <c>default(Receipt)</c> is).</summary>
        internal struct Receipt
        {
            public object? Patient;
            public object? Bed;
            public bool Complete;
        }
        /// <summary>C++: TownMedicAIPolicy.h:23 (<c>receipts[16]</c>, the literal 16 repeated in every loop)</summary>
        public const int Slots = 16;
        internal readonly Receipt[] receipts = new Receipt[Slots];

        // C++: TownMedicAIPolicy.h:25
        public void Clear() { for (int i = 0; i < Slots; ++i) receipts[i] = new Receipt(); }

        // C++: TownMedicAIPolicy.h:26-35
        public void Watch(object? patient, object? bed)
        {
            if (patient == null || bed == null) return;
            for (int i = 0; i < Slots; ++i) if (ReferenceEquals(receipts[i].Patient, patient))
            {
                if (!receipts[i].Complete) receipts[i].Bed = bed;
                return;
            }
            for (int i = 0; i < Slots; ++i) if (receipts[i].Patient == null)
            {
                receipts[i].Patient = patient; receipts[i].Bed = bed; return;
            }
        }

        // C++: TownMedicAIPolicy.h:36-40
        public void Entered(object? patient, object? bed)
        {
            if (patient == null || bed == null) return;
            for (int i = 0; i < Slots; ++i)
                if (ReferenceEquals(receipts[i].Patient, patient) && ReferenceEquals(receipts[i].Bed, bed)) receipts[i].Complete = true;
        }

        // C++: TownMedicAIPolicy.h:41-45
        public bool Completed(object? patient)
        {
            for (int i = 0; i < Slots; ++i)
                if (ReferenceEquals(receipts[i].Patient, patient) && receipts[i].Complete) return true;
            return false;
        }
    }

    // Native checkOrders still executes first. Reporting the reserved receiver
    // as handled prevents chooseGoal from falling through into autonomous jobs.
    // C++: TownMedicAIPolicy.h:49-51
    public static bool OrdersHandled(bool reserved, bool nativeResult)
    {
        return reserved || nativeResult;
    }

    // C++: TownMedicAIPolicy.h:52-55
    public static bool OrdersHandled(bool medicReserved, bool patientReserved,
        bool externalReserved, bool nativeResult)
    {
        return medicReserved || patientReserved || externalReserved || nativeResult;
    }

    // C++: TownMedicAIPolicy.h:56-59
    public static bool CanReserveExternal(bool medicReserved, bool patientReserved,
        bool externalReserved)
    {
        return !medicReserved && !patientReserved && !externalReserved;
    }

    /// <summary>C++: TownMedicAIPolicy.h:60-79, <c>template&lt;int Capacity&gt;</c>; the capacity is a constructor argument. A capacity
    /// below 1 throws (the C++ cannot be instantiated with one: a zero-length array does not compile).</summary>
    public sealed class ReceiverReservations
    {
        internal readonly object?[] receivers;

        public int Capacity => receivers.Length;

        // C++: TownMedicAIPolicy.h:63
        public ReceiverReservations(int capacity)
        {
            if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity), "ReceiverReservations needs a capacity of at least 1");
            receivers = new object?[capacity];
            Clear();
        }

        // C++: TownMedicAIPolicy.h:64
        public void Clear() { for (int i = 0; i < receivers.Length; ++i) receivers[i] = null; }

        // C++: TownMedicAIPolicy.h:65-69
        public bool Contains(object? receiver)
        {
            if (receiver == null) return false;
            for (int i = 0; i < receivers.Length; ++i) if (ReferenceEquals(receivers[i], receiver)) return true;
            return false;
        }

        // C++: TownMedicAIPolicy.h:70-75
        public bool Add(object? receiver)
        {
            if (receiver == null) return false;
            if (Contains(receiver)) return true;
            for (int i = 0; i < receivers.Length; ++i) if (receivers[i] == null) { receivers[i] = receiver; return true; }
            return false;
        }

        // C++: TownMedicAIPolicy.h:76-78
        public void Remove(object? receiver)
        {
            for (int i = 0; i < receivers.Length; ++i) if (ReferenceEquals(receivers[i], receiver)) receivers[i] = null;
        }
    }

    /// <summary>C++: TownMedicAIPolicy.h:80-82 (the three typedefs' capacities).</summary>
    public const int ReservationsCapacity = 10, PatientReservationsCapacity = 16, ExternalReservationsCapacity = 4;

    /// <summary>C++: TownMedicAIPolicy.h:80 <c>typedef ReceiverReservations&lt;10&gt; Reservations</c></summary>
    public static ReceiverReservations NewReservations() => new(ReservationsCapacity);
    /// <summary>C++: TownMedicAIPolicy.h:81 <c>typedef ReceiverReservations&lt;16&gt; PatientReservations</c></summary>
    public static ReceiverReservations NewPatientReservations() => new(PatientReservationsCapacity);
    /// <summary>C++: TownMedicAIPolicy.h:82 <c>typedef ReceiverReservations&lt;4&gt; ExternalReservations</c></summary>
    public static ReceiverReservations NewExternalReservations() => new(ExternalReservationsCapacity);
}
