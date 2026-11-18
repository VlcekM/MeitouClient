// Port of upstream/ProvingGrounds/plugin/src/TownAftercarePolicy.h (Proving Grounds by Shutori, GPLv3).
namespace ProvingGrounds.Core;

/// <summary>C++: TownAftercarePolicy.h (header-only, namespace <c>TownAftercarePolicy</c>): the aftercare rules around the medic
/// planner (TW §2.9): who needs the hospital, the bed-wait and bed-stay budgets, the 1 s polls and the stalled-order tracker.
/// <c>unsigned long</c> is 32 bits on MSVC (the shipped build), so the two polls take <c>uint</c>: a 32-bit clock; a wrapped
/// clock (now &lt; last) is due.</summary>
public static class TownAftercarePolicy
{
    /// <summary>C++: TownAftercarePolicy.h:3. The name clashes with <c>System.Action</c> (ImplicitUsings are on): code outside this class
    /// that writes <c>using static TownAftercarePolicy</c> must alias it (<c>using Act = TownAftercarePolicy.Action;</c>) or qualify it;
    /// <c>TownAftercarePolicy.Action</c> always compiles.</summary>
    public enum Action { Done = 0, Treat = 1, WaitForBed = 2, PickUp = 3, Deliver = 4, Gather = 5, Hold = 6, ReturnHospital = 7,
        ApproachAid = 8, ApproachPickup = 9, WalkToBed = 10 }

    // C++: TownAftercarePolicy.h:4-6
    public static bool NeedsHospital(bool winner, bool unconscious, bool needsRecovery)
    {
        return unconscious || (!winner && needsRecovery);
    }

    // C++: TownAftercarePolicy.h:7-9
    public static bool StandbyOrderLost(bool moving, float destinationErrorSquared)
    {
        return moving && destinationErrorSquared > 18.0f * 18.0f;
    }

    // A patient who owes a hospital transfer but has no bed resolved holds a
    // medic (Intent Wait) for as long as the bed stays unavailable. Bound that
    // wait: past the budget the patient is handed back to the town AI instead of
    // pinning the bout, the medics and the autosave hold open indefinitely.
    // Sized above the observed legitimate bed waits (<= ~16s) and far below the
    // 300s bout cap in TownArenaRuntimePolicy::AftercareExpired.
    // C++: TownAftercarePolicy.h:16
    public static float TransferWaitBudget() { return 45.0f; }

    // C++: TownAftercarePolicy.h:17
    public static bool TransferWaitExpired(float age) { return age >= TransferWaitBudget(); }

    // The plugin is what puts arena patients into the hospital's beds, so the
    // plugin has to take them out again: the engine leaves a fighter lying there
    // once healed, and the per-bout patient list is gone by then. A bed is only
    // released for a healed patient, or for one that is awake and has outlasted
    // this stay limit (a destroyed limb can never reach the healed band).
    // C++: TownAftercarePolicy.h:23
    public static float BedStayLimit() { return 600.0f; }

    // C++: TownAftercarePolicy.h:24
    public static bool BedStayExpired(float age) { return age >= BedStayLimit(); }

    // A release that the engine refuses is retried, but not every tick: retries
    // are diagnostic, and one log line per 30s per stuck occupant is enough.
    // C++: TownAftercarePolicy.h:27
    public static float BedReleaseRetry() { return 30.0f; }

    // Bed recovery is a slow background sweep, not a frame-locked one.
    /// <summary><c>unsigned long</c> (32 bits on MSVC) -&gt; <c>uint</c>: a 32-bit clock; a wrapped clock (now &lt; last) is due.</summary>
    // C++: TownAftercarePolicy.h:29-31
    public static bool BedPollDue(uint lastPoll, uint now)
    {
        return lastPoll == 0 || now < lastPoll || unchecked(now - lastPoll) >= 1000;
    }

    // C++: TownAftercarePolicy.h:32
    public static int RequiredMedics(int fighters) { return fighters < 0 ? 0 : fighters > 10 ? 10 : fighters; }

    // C++: TownAftercarePolicy.h:33-35
    public static bool NeedsRecovery(bool unconscious, float minimumHealth, float blood)
    {
        return unconscious || minimumHealth < .99f || blood < .99f;
    }

    /// <summary>C++: TownAftercarePolicy.h:36-61. A value type (a member of the glue's <c>Medic</c>, copied with it). The C++
    /// constructor runs <see cref="Reset"/>, which zeroes every field (action <see cref="Action.Done"/> = 0), so here
    /// <c>default(CommandProgress)</c> and <c>new CommandProgress()</c> both equal the C++ default.
    /// <para><see cref="Reset"/> and <see cref="Update"/> mutate it: call them through a variable, an array element or a ref (e.g.
    /// <c>ref var m = ref CollectionsMarshal.AsSpan(medics)[i];</c> then <c>m.Command.Update(...)</c>), never through a
    /// <c>List&lt;T&gt;</c> indexer or a readonly field, which silently update a copy. The glue keeps it inside each element of
    /// <c>std::vector&lt;Medic&gt;</c> and updates it in place (TownAftercare.cpp:131, 217, 458, 469).</para></summary>
    public struct CommandProgress
    {
        public Action Action;
        public bool Issued;
        public float BestDistance, BestAid, LastProgress, LastObserved;
        public int StalledRetries;

        // C++: TownAftercarePolicy.h:41
        public CommandProgress() { Reset(); }

        // C++: TownAftercarePolicy.h:42-45
        public void Reset()
        {
            Action = TownAftercarePolicy.Action.Done; Issued = false;
            BestDistance = BestAid = LastProgress = LastObserved = 0; StalledRetries = 0;
        }

        // C++: TownAftercarePolicy.h:46-60
        public bool Update(Action next, float elapsed, float distance, float aid)
        {
            bool changed = !Issued || Action != next || elapsed < LastObserved;
            LastObserved = elapsed;
            if (!changed && (distance < BestDistance - 1.0f || aid < BestAid - .0001f))
            {
                if (distance < BestDistance) BestDistance = distance;
                if (aid < BestAid) BestAid = aid;
                LastProgress = elapsed; StalledRetries = 0;
            }
            if (!changed && (next == TownAftercarePolicy.Action.Hold || next == TownAftercarePolicy.Action.WaitForBed ||
                next == TownAftercarePolicy.Action.Done ||
                elapsed - LastProgress < (next == TownAftercarePolicy.Action.Gather ? 30.0f : 10.0f))) return false;
            if (changed) StalledRetries = 0; else ++StalledRetries;
            Action = next; Issued = true;
            BestDistance = distance; BestAid = aid; LastProgress = elapsed;
            return true;
        }
    }

    /// <summary><c>unsigned long</c> (32 bits on MSVC) -&gt; <c>uint</c>: a 32-bit clock; a wrapped clock (now &lt; last) is due.</summary>
    // C++: TownAftercarePolicy.h:62-64
    public static bool MedicPollDue(uint lastTick, uint now)
    {
        return lastTick == 0 || now < lastTick || unchecked(now - lastTick) >= 1000;
    }

    // C++: TownAftercarePolicy.h:65
    public static bool ClearExternalReservations(bool worldTeardown) { return worldTeardown; }
}
