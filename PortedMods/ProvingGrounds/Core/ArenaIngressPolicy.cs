// Port of upstream/ProvingGrounds/plugin/src/ArenaIngressPolicy.h (Proving Grounds by Shutori, GPLv3).
namespace ProvingGrounds.Core;

/// <summary>When ingress may advance, open the approach UI and start the countdown. C++: ArenaIngressPolicy.h (PA §2.5).</summary>
public static class ArenaIngressPolicy
{
    // C++: ArenaIngressPolicy.h:5-13
    public static bool ShouldAdvancePrisonerRelease(
        bool inPrisonerReleasePhase,
        bool matchIncludesPrisoner,
        bool allPrisonersReleased)
    {
        return inPrisonerReleasePhase &&
            matchIncludesPrisoner &&
            allPrisonersReleased;
    }

    // C++: ArenaIngressPolicy.h:15-21
    public static bool ShouldOpenInteractUiImmediately(
        bool matchActive,
        bool fightIngressPending,
        bool walkInPending)
    {
        return matchActive || fightIngressPending || walkInPending;
    }

    // C++: ArenaIngressPolicy.h:23-26
    public static bool ShouldBindInteractSource(bool openImmediately)
    {
        return !openImmediately;
    }

    /// <summary>C++: ArenaIngressPolicy.h:28-32</summary>
    public struct FightReadiness
    {
        public bool AllPrisonersReleased;
        public bool AllFightersAtFormation;
    }

    /// <summary>C++: ArenaIngressPolicy.h:34-42</summary>
    public struct IngressObservation
    {
        public bool AllPrisonersReleased;
        public bool AllFightersValid;
        public bool AllAtArrivalRadius;
        public bool AllAtSettleRadius;
        public bool AllAtFormationRadius;
        public bool AllSettled;
    }

    // C++: ArenaIngressPolicy.h:44-49
    public static bool CanOpenApproachUi(in IngressObservation observation)
    {
        return observation.AllFightersValid &&
            observation.AllPrisonersReleased &&
            observation.AllAtArrivalRadius;
    }

    // C++: ArenaIngressPolicy.h:51-56
    public static bool CanBeginCountdown(in IngressObservation observation)
    {
        return observation.AllFightersValid &&
            observation.AllPrisonersReleased &&
            observation.AllAtFormationRadius;
    }

    // C++: ArenaIngressPolicy.h:58-62
    public static bool CanBeginCountdown(in FightReadiness readiness)
    {
        return readiness.AllPrisonersReleased &&
            readiness.AllFightersAtFormation;
    }
}
