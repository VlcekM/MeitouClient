// Port of upstream/ProvingGrounds/plugin/src/WorldLifecyclePolicy.h (Proving Grounds by Shutori, GPLv3).
namespace ProvingGrounds.Core;

/// <summary>When an autosave waits for the arena to be idle. C++: WorldLifecyclePolicy.h (PA §2.20, save safety).</summary>
public static class WorldLifecyclePolicy
{
    /// <summary>C++: WorldLifecyclePolicy.h:5-12</summary>
    public struct ArenaActivity
    {
        public bool IngressPending;
        public bool WalkInPending;
        public bool MatchActive;
        public bool PrisonerActivity;
        public bool TownActivity;
    }

    // C++: WorldLifecyclePolicy.h:14-20
    public static bool ShouldPostponeAutosave(in ArenaActivity activity)
    {
        return activity.IngressPending ||
            activity.WalkInPending ||
            activity.MatchActive ||
            activity.PrisonerActivity || activity.TownActivity;
    }
}
