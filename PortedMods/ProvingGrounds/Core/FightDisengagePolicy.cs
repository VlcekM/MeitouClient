// Port of upstream/ProvingGrounds/plugin/src/FightDisengagePolicy.h (Proving Grounds by Shutori, GPLv3).
namespace ProvingGrounds.Core;

/// <summary>Which cleanup steps a fighter's disengage runs. C++: FightDisengagePolicy.h (PA §2.7).</summary>
public static class FightDisengagePolicy
{
    // Ordered cleanup steps for FightStarter::DisengageFighter.
    // Keep engage/disengage symmetric so mid-fight saves do not serialize
    // leftover attack orders / AI combat goals.
    /// <summary>C++: FightDisengagePolicy.h:8-17</summary>
    public enum Step
    {
        ClearTempEnemies = 0,
        RemoveMeleeJobs,
        ClearPlayerOrders,
        EndCombatMode,
        ClearAiGoals,
        RethinkAi,
        RestoreOrders
    }

    /// <summary>C++: FightDisengagePolicy.h:19-28</summary>
    public struct Plan
    {
        public bool ClearTempEnemies;
        public bool RemoveFocusedMeleeJobs;
        public bool ClearPlayerAttackOrders;
        public bool EndCombatMode;
        public bool ClearAiGoals;
        public bool RethinkAi;
        public bool RestoreStandingOrders;
    }

    // C++: FightDisengagePolicy.h:30-41
    public static Plan BuildFullDisengagePlan()
    {
        Plan plan = default;
        plan.ClearTempEnemies = true;
        plan.RemoveFocusedMeleeJobs = true;
        plan.ClearPlayerAttackOrders = true;
        plan.EndCombatMode = true;
        plan.ClearAiGoals = true;
        plan.RethinkAi = true;
        plan.RestoreStandingOrders = true;
        return plan;
    }

    // A KO in a continuing match only needs stale combat targets removed.
    // Restoring standing orders (including Jobs via the UI command) here would
    // tear down and immediately rebuild the match state for every survivor.
    // C++: FightDisengagePolicy.h:46-53
    public static Plan BuildInMatchRetargetPlan()
    {
        Plan plan = default;
        plan.ClearTempEnemies = true;
        plan.RemoveFocusedMeleeJobs = true;
        plan.ClearPlayerAttackOrders = true;
        return plan;
    }
}
