// The game-free decisions of upstream/ProvingGrounds/plugin/src/ArenaMedical.cpp (Proving Grounds by Shutori, GPLv3), task 4.7 (PA §2.11,
// CAP 12): SetProtocol's range and lock rule, the treatment block (BlockFor's player-arena half) over the published match view, the aid need
// and stabilise order (served to M6 prisoners and M8 aftercare through IMedicalWorld), the TEST-ONLY `pg medical` verb's decision, and (task
// 8.4c) the Scratch medic restock's decision and log line (RestockMedicIfDepleted over Core MedicSupplyPolicy).
// ShouldBlockTreatment / ShouldStabilizePrisoner / GetProtocolName / NextProtocol are Core/ArenaMedicalPolicy.cs (M1), reused, not re-ported.
// src/Game/ArenaMedical.cs binds them to the engine; Tests/Glue/ArenaMedicalLogicTests.cs runs them over fakes (E3).
using static ProvingGrounds.Core.ArenaMedicalPolicy;

namespace ProvingGrounds.Core.Glue;

/// <summary>The engine reads and the one write <c>ArenaMedical::ActionableAidNeed</c>, <c>NeedsStabilization</c> and <c>IssueStabilizeOrder</c>
/// make (ArenaMedical.cpp:227-267). Sim thread, serial steps.</summary>
public interface IMedicalWorld<TId>
{
    /// <summary>C++ <c>c &amp;&amp; c-&gt;isValid()</c>.</summary>
    bool IsValid(TId c);
    /// <summary>C++ <c>isDead()</c>.</summary>
    bool IsDead(TId c);
    /// <summary>C++ <c>getMedical() != NULL</c>.</summary>
    bool HasMedical(TId c);
    /// <summary>C++ <c>medical-&gt;wounds.size()</c> (asked only when <see cref="HasMedical"/>).</summary>
    int WoundCount(TId c);
    /// <summary>C++ <c>medical-&gt;currentBleedRate</c> (asked only when <see cref="HasMedical"/>).</summary>
    float BleedRate(TId c);
    /// <summary>C++ <c>medical-&gt;scoreFirstAidNeed(robot)</c> (asked only when <see cref="HasMedical"/>).</summary>
    float AidNeed(TId c, bool robot);
    /// <summary>C++ <c>medic-&gt;addOrder(NULL, robotAid ? FIRST_AID_ROBOT : FIRST_AID_ORDER, target, false, true, target-&gt;getPosition())</c>.
    /// False when the engine refused the order.</summary>
    bool GiveTreatOrder(TId medic, TId target, bool robotAid);
}

/// <summary>ArenaMedical.cpp's pure steps (namespace ArenaMedical).</summary>
public static class ArenaMedicalLogic
{
    // C++: ArenaMedical.cpp:214-220
    /// <summary><c>SetProtocol</c>: an out-of-range protocol becomes Ringside Aid; while a match runs the protocol is left as it is (locked).
    /// Returns the protocol after the call.</summary>
    public static Protocol SetProtocol(Protocol current, Protocol requested, bool matchActive)
    {
        if (requested != Protocol.RingsideAid && requested != Protocol.Bloodsport)
            requested = Protocol.RingsideAid;
        if (!matchActive)
            return requested;
        return current;
    }

    // C++: ArenaMedical.cpp:157-165 (BlockFor)
    /// <summary><c>BlockFor(medical, actor)</c>: an actor that is a town bystander for the patient (<see cref="CombatMatchView{TId}.BlocksTownBystander"/>,
    /// ArenaMedical.cpp:50-55, task 7.4; <paramref name="hasActor"/> = the C++'s <c>actor</c> not NULL, <paramref name="actorIsTownMedic"/> =
    /// IsScratchMedicActor, read by the glue only when the patient is a town fighter), or <see cref="BlocksTreatment"/>.</summary>
    public static bool BlockFor<TId>(CombatMatchView<TId> view, Protocol protocol, bool hasActor, TId patient, bool actorIsTownMedic)
        where TId : IEquatable<TId> =>
        (hasActor && view.BlocksTownBystander(patient, actorIsTownMedic)) || BlocksTreatment(view, protocol, patient);

    // C++: ArenaMedical.cpp:161-164, BlockFor's player-arena half: ShouldBlockTreatment(g_protocol, SparSession::IsActive(),
    // SparSession::IsParticipant(target), SparSession::IsEliminated(target)), read off the published view.
    /// <summary>Whether treatment of <paramref name="patient"/> is blocked now by the protocol.</summary>
    public static bool BlocksTreatment<TId>(CombatMatchView<TId> view, Protocol protocol, TId patient) where TId : IEquatable<TId>
    {
        view.ParticipantState(patient, out bool participant, out bool eliminated);   // one scan of the fighters
        return ShouldBlockTreatment(protocol, view.Active, participant, eliminated);
    }

    // C++: ArenaMedical.cpp:227-243
    /// <summary><c>ActionableAidNeed</c>: the first-aid need score, 0 for a missing, dead or medical-less character, 0 when there is no wound
    /// record and no bleeding above 0.0001 (a stale cached score), 0 unless the score is above 0.</summary>
    /// <remarks>Generic over the world type so a struct world is passed without boxing.</remarks>
    public static float ActionableAidNeed<TWorld, TId>(TWorld w, TId target, bool robotAid) where TWorld : IMedicalWorld<TId>
    {
        if (!w.IsValid(target) || w.IsDead(target))
            return 0.0f;
        if (!w.HasMedical(target))
            return 0.0f;
        // A non-zero cached score with no wound records and no bleeding cannot drive applyFirstAid/applyDoctoring. (C++ :237-239)
        if (w.WoundCount(target) == 0 && w.BleedRate(target) <= 0.0001f)
            return 0.0f;
        float need = w.AidNeed(target, robotAid);
        return need > 0.0f ? need : 0.0f;
    }

    // C++: ArenaMedical.cpp:245-249
    public static bool NeedsStabilization<TWorld, TId>(TWorld w, TId target) where TWorld : IMedicalWorld<TId> =>
        ActionableAidNeed<TWorld, TId>(w, target, false) > 0.01f || ActionableAidNeed<TWorld, TId>(w, target, true) > 0.01f;

    // C++: ArenaMedical.cpp:251-267
    /// <summary><c>IssueStabilizeOrder</c>: nothing for a missing or dead medic or patient; else a first-aid order, the robot one when the
    /// patient's robot need score is above its organic one. Returns whether an order was given (the C++ returns nothing).</summary>
    public static bool IssueStabilizeOrder<TWorld, TId>(TWorld w, TId medic, TId target) where TWorld : IMedicalWorld<TId>
    {
        if (!w.IsValid(medic) || w.IsDead(medic) || !w.IsValid(target) || w.IsDead(target))
            return false;
        bool robotAid = w.HasMedical(target) && w.AidNeed(target, true) > w.AidNeed(target, false);
        return w.GiveTreatOrder(medic, target, robotAid);
    }

    /// <summary>What <c>pg medical</c> did: whether it succeeded, the protocol after it and the refusal (null on success).</summary>
    public readonly record struct CommandResult(bool Ok, Protocol Protocol, string? Error);

    /// <summary>TEST ONLY <c>pg medical &lt;ringside|bloodsport|status&gt;</c>: stands in for the Arena window's protocol toggle (task 4.5,
    /// ArenaUI.cpp:1532-1537, refused while an arena start is pending or a match runs) followed by <see cref="SetProtocol"/>. The refusal texts
    /// are the port's (the C++ toggle returns silently).</summary>
    public static CommandResult Command(string word, Protocol current, bool matchActive, bool ingressPending)
    {
        Protocol wanted;
        switch (word)
        {
            case "status": return new(true, current, null);
            case "ringside": wanted = Protocol.RingsideAid; break;
            case "bloodsport": wanted = Protocol.Bloodsport; break;
            default: return new(false, current, "usage pg medical <ringside|bloodsport|status>");
        }
        if (ingressPending) return new(false, current, "locked: an arena start is pending");
        if (matchActive) return new(false, current, "locked: a match is running");
        return new(true, SetProtocol(current, wanted, matchActive), null);
    }
    /// <summary>Task 8.4c: the C++ <c>chargesBefore</c> of the kit a treatment tick used, as the port reads it: the prefix's read when the kit is the
    /// same item (<paramref name="sameKit"/>), else a kit the tick took (FirstAidSystem.Fewest takes only one with charges above 0), read as
    /// <see cref="float.Epsilon"/>: the restock rule asks only "above 0 before".</summary>
    public static float KitChargesBefore(bool sameKit, float prefixCharges) => sameKit ? prefixCharges : float.Epsilon;

    /// <summary>C++: ArenaMedical.cpp:135-155 <c>RestockMedicIfDepleted</c>, after a first-aid or robot-repair treatment tick that was not blocked.
    /// <paramref name="isKit"/>: the used equipment is a first-aid or robot-repair kit (the C++ <c>itemFunction</c> test; false: no equipment or
    /// another function, nothing happens). The policy (Core MedicSupplyPolicy.RestockAfterUse) asks <paramref name="access"/> over the medic's
    /// inventories. Returns the C++ PGLog line (null: no restock); <paramref name="error"/> is true when the C++ also logs it as an error (the
    /// used kit recharged because no replacement could be given).</summary>
    public static string? RestockMedicIfDepleted<TAccess>(bool isKit, bool robotRepair, float chargesBefore, float chargesAfter,
        ref TAccess access, string medicName, out bool error) where TAccess : MedicSupplyPolicy.IAccess
    {
        error = false;
        if (!isKit) return null;
        var supply = robotRepair ? MedicSupplyPolicy.Supply.RobotRepair : MedicSupplyPolicy.Supply.FirstAid;
        var result = MedicSupplyPolicy.RestockAfterUse(chargesBefore, chargesAfter, supply, ref access);
        if (result == MedicSupplyPolicy.RestockResult.NoRestock) return null;
        error = result == MedicSupplyPolicy.RestockResult.UsedKitRecharged;
        return "Proving Grounds: Scratch medic supply restored actor=" + medicName +
            " type=" + (supply == MedicSupplyPolicy.Supply.RobotRepair ? "skeleton repair" : "advanced first aid") +
            " method=" + (result == MedicSupplyPolicy.RestockResult.ReplacementSpawned ? "replacement item" : "recharged spent kit fallback");
    }
}
