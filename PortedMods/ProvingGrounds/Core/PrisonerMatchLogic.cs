// Port of upstream/ProvingGrounds/plugin/src/PrisonerMatchLogic.h and PrisonerMatchLogic.cpp (Proving Grounds by Shutori, GPLv3).
using System.Globalization;

namespace ProvingGrounds.Core;

/// <summary>Prisoners in a match: handler count, the cage a prisoner returns to, how they return, and the cage-lock decision.
/// C++: PrisonerMatchLogic.h, PrisonerMatchLogic.cpp (PA §2.19).</summary>
public static class PrisonerMatchLogic
{
    /// <summary>C++: PrisonerMatchLogic.h:6-9</summary>
    public struct CagePick
    {
        public int Index; // -1 = none
    }

    /// <summary>C++: PrisonerMatchLogic.h:11-17</summary>
    public enum ReturnAction { Skip = 0, InstantCage = 1, EscortWalk = 2, Carry = 3 }

    /// <summary>C++: PrisonerMatchLogic.h:19-25</summary>
    public enum CageLockAction
    {
        Secured, IssueOrder, Wait,
        MissingDestination, WrongOccupant, MissingLock,
        Broken, NoVerifier, NoHandler,
        OrderFailed, TimedOut
    }

    /// <summary>C++: PrisonerMatchLogic.h:27-39 (passed by const reference, read only).</summary>
    public struct CageLockObservation
    {
        public bool DestinationValid;
        public bool CorrectOccupant;
        public bool HasLock;
        public bool Broken;
        public bool VerifierAvailable;
        public bool Locked;
        public bool HandlerAvailable;
        public bool OrderIssued;
        public bool OrderRejected;
        public bool TimedOut;
    }

    // C++: PrisonerMatchLogic.cpp:8-20
    public static CageLockAction ChooseCageLockAction(in CageLockObservation observation)
    {
        if (!observation.DestinationValid) return CageLockAction.MissingDestination;
        if (observation.Broken) return CageLockAction.Broken;
        if (!observation.CorrectOccupant) return CageLockAction.WrongOccupant;
        if (!observation.HasLock) return CageLockAction.MissingLock;
        if (!observation.VerifierAvailable) return CageLockAction.NoVerifier;
        if (observation.Locked) return CageLockAction.Secured;
        if (!observation.HandlerAvailable) return CageLockAction.NoHandler;
        if (observation.OrderRejected) return CageLockAction.OrderFailed;
        if (observation.TimedOut) return CageLockAction.TimedOut;
        return observation.OrderIssued ? CageLockAction.Wait : CageLockAction.IssueOrder;
    }

    // C++: PrisonerMatchLogic.cpp:22-25
    public static bool HasEnoughHandlers(int prisonersInMatch, int availableHandlers)
    {
        return availableHandlers >= prisonersInMatch;
    }

    // C++: PrisonerMatchLogic.cpp:27-37 (sprintf_s into char[128]; the longest possible text, three INT_MIN values, is 75 chars, under 127, so never cut)
    public static string FormatHandlerShortage(int prisonersInMatch, int availableHandlers)
    {
        return string.Create(CultureInfo.InvariantCulture,
            $"Need {prisonersInMatch} handlers for {prisonersInMatch} prisoners ({availableHandlers} available)");
    }

    // originalIndex may be -1 (no original). Arrays length == count.
    // C++: PrisonerMatchLogic.cpp:39-73
    public static CagePick ChooseReturnCage(
        int originalIndex,
        bool[] free,
        float[] distToPrisonerSq,
        bool[] inRegistryRange,
        bool[] alreadyReserved,
        int count)
    {
        if (originalIndex >= 0 && originalIndex < count &&
            free[originalIndex] &&
            inRegistryRange[originalIndex] &&
            !alreadyReserved[originalIndex])
        {
            CagePick pick;
            pick.Index = originalIndex;
            return pick;
        }

        int bestIndex = -1;
        float bestDist = float.MaxValue;
        for (int i = 0; i < count; ++i)
        {
            if (!free[i] || !inRegistryRange[i] || alreadyReserved[i])
                continue;
            if (distToPrisonerSq[i] < bestDist)
            {
                bestDist = distToPrisonerSq[i];
                bestIndex = i;
            }
        }

        CagePick result;
        result.Index = bestIndex;
        return result;
    }

    // When true, CombatHooks must not treat the pair as natural enemies.
    // Sparring opponents are never suppressed (spar hook forces enmity).
    // C++: PrisonerMatchLogic.cpp:76-84
    public static bool ShouldSuppressNaturalEnemy(
        bool sparringOpponents,
        bool aIsMatchPrisoner,
        bool bIsMatchPrisoner)
    {
        if (sparringOpponents)
            return false;
        return aIsMatchPrisoner || bIsMatchPrisoner;
    }

    // C++: PrisonerMatchLogic.cpp:86-93
    public static ReturnAction ChooseReturnAction(bool conscious, bool dead)
    {
        if (dead)
            return ReturnAction.Skip;
        if (conscious)
            return ReturnAction.EscortWalk;
        return ReturnAction.Carry;
    }
}
