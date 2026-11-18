using System.Numerics;

namespace ProvingGrounds.Core.Glue;

// Task 8.4b (TW §2.9 "After the fight" steps 6-7): the bed half of the treatment. The bed itself is always the coordinator's choice (Core
// TownMedicCoordinator FindBed: the nearest compatible free bed of the hospital's beds; robots only in skeleton beds, humans only in normal ones, the
// C++'s Compatible :332-335), handed to the put-in-bed trip as its bed: the engine's own bed search (FixtureRegistry.FindBed) is never asked (it
// skips skeleton beds and chooses by itself). The C++ learned that a patient lay down through its setBedMode hook (RecordBedEntry :75-84: the
// delivery receipt, and the bed registry entry for a hospital bed); Meitou has no such hook, so the port reads the patients it sent to a bed (the
// deliveries it watches) at each 1 s poll of Treat (docs/divergences.md "TownAftercare (task 8.4b)"). TraceBeds :778-812.

public sealed partial class TownAftercareLogic<TId>
{
    /// <summary>One object per bed id, so Core's reference-compared <see cref="TownMedicAIPolicy.Deliveries"/> can hold a bed as the C++ holds a
    /// <c>UseableStuff*</c>. Cleared with the world (ResetState(true)).</summary>
    readonly Dictionary<string, object> bedKeys = new(StringComparer.Ordinal);

    /// <summary>Port: the deliveries' watches (patient, bed) as TIds and bed ids, so the poll can ask the host whether each watched patient lies in its
    /// bed. Kept in step with <see cref="deliveries"/>' Watch (same 16 slots, same "a complete receipt keeps its bed" rule); cleared with it.</summary>
    readonly List<(TId Patient, string Bed)> watched = [];

    // TraceBeds' change cache (C++ :783-785)
    readonly TId[] bedOccupantTrace = new TId[TownMedicCoordinator.MaxBeds];
    readonly bool[] bedAvailableTrace = new bool[TownMedicCoordinator.MaxBeds];
    float lastBedTrace = -1000;

    object BedKey(string bed)
    {
        if (!bedKeys.TryGetValue(bed, out var k)) bedKeys[bed] = k = new object();
        return k;
    }

    /// <summary>C++ <c>deliveries.Watch(p, targetBed)</c> (Execute :1015-1018), and the port's copy of the watch for the poll.</summary>
    void Watch(ITownAftercareHost<TId> h, TId patient, string bed)
    {
        object? receiver = Receiver(h, patient);
        if (receiver == null) return;
        bool complete = deliveries.Completed(receiver);
        deliveries.Watch(receiver, BedKey(bed));
        var eq = EqualityComparer<TId>.Default;
        for (int i = 0; i < watched.Count; ++i)
            if (eq.Equals(watched[i].Patient, patient))
            {
                if (!complete) watched[i] = (patient, bed);
                return;
            }
        if (watched.Count < TownMedicAIPolicy.Deliveries.Slots) watched.Add((patient, bed));
    }

    /// <summary>Port of the C++ setBedMode hook (RecordBedEntry, TownAftercare.cpp:75-84) by polling: each watched patient now lying in its watched
    /// bed counts as entered (<c>deliveries.Entered</c>), and a hospital bed's occupant goes into the bed registry (<c>RememberBedOccupant</c>). The
    /// entry is seen at Treat's next 1 s poll, not at the moment the patient lies down.</summary>
    void PollBedEntries(ITownAftercareHost<TId> h)
    {
        for (int i = 0; i < watched.Count; ++i)
        {
            var (patient, bed) = watched[i];
            if (h.IsNull(patient) || !h.Valid(patient) || !h.LiesIn(patient, bed)) continue;
            object? receiver = ExistingReceiver(patient);
            if (receiver == null || deliveries.Completed(receiver)) continue;
            deliveries.Entered(receiver, BedKey(bed));
            // Only the arena hospital's beds are ours to give back; beds elsewhere in
            // the world (inns, a player's own bed) stay the engine's business.
            if (IsArenaHospitalBed(h, bed)) RememberBedOccupant(h, patient, bed);
        }
    }

    // C++: TownAftercare.cpp:786-812. Port: the bed's operator count (currentOperators.size()) is 1 while its occupant holds it, else 0 (a Meitou bed
    // holds one occupant and no operator set).
    void TraceBeds(ITownAftercareHost<TId> h, in TownMedicCoordinator.Observation o, float elapsed, bool force)
    {
        bool dumpAll = force || elapsed < lastBedTrace || elapsed - lastBedTrace >= 10.0f;
        if (dumpAll) lastBedTrace = elapsed;
        var eq = EqualityComparer<TId>.Default;
        for (int i = 0; i < o.BedCount && i < TownMedicCoordinator.MaxBeds; ++i)
        {
            if (HospitalBed(h, beds[i]) is not { } bed) continue;
            TId occupantHand = bed.Occupant;
            bool resolved = !h.IsNull(occupantHand) && h.Valid(occupantHand);
            TId occupant = resolved ? occupantHand : none;
            bool available = o.Beds[i].Available;
            if (!dumpAll && eq.Equals(bedOccupantTrace[i], occupant) && bedAvailableTrace[i] == available) continue;
            bedOccupantTrace[i] = occupant; bedAvailableTrace[i] = available;
            uint reservedBy = 0;
            for (int m = 0; m < o.MedicCount; ++m)
                if (coordinator.ReservedBed(m) == i) { reservedBy = (uint)(m + 1); break; }
            Vector3 pos = bed.Position;
            h.Debug("Proving Grounds: aftercare bed care=" + N(careId) + " index=" + N((uint)(i + 1)) + " skeleton=" + B(o.Beds[i].Robot) +
                " available=" + B(available) + " occupied=" + B(Occupied(h, bed)) + " handleValid=" + B(!h.IsNull(occupantHand)) +
                " resolved=" + B(resolved) + " occupant=" + (resolved ? h.Name(occupant) : "none") + " inBed=" + B(resolved && h.InBed(occupant)) +
                " dead=" + B(resolved && h.IsDead(occupant)) + " ko=" + B(resolved && h.IsUnconscious(occupant)) +
                " operators=" + N(bed.Occupied ? 1u : 0u) + " reservedBy=" + N(reservedBy) + " position=" + V(pos));
        }
    }
}
