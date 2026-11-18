// Port of upstream/ProvingGrounds/plugin/src/TownCorpseCleanupPolicy.h (Proving Grounds by Shutori, GPLv3).
namespace ProvingGrounds.Core;

/// <summary>C++: TownCorpseCleanupPolicy.h (header-only, namespace <c>TownCorpseCleanupPolicy</c>): the rules of the after-bout
/// corpse sweep (TW §2.11): which bodies count, when the cage handler and the furnace are usable, the handler's next order, the
/// 1 s-capped batch clock and its timeout, and what a released handler does.</summary>
public static class TownCorpseCleanupPolicy
{
    /// <summary>C++: TownCorpseCleanupPolicy.h:4. The name clashes with <c>System.Action</c> (ImplicitUsings are on): outside this
    /// class write <c>TownCorpseCleanupPolicy.Action</c> or alias it.</summary>
    public enum Action { Wait, PickUp, Feed }

    /// <summary>C++: TownCorpseCleanupPolicy.h:5</summary>
    public enum ReleaseDisposition { ForgetRelease, DeferRelease, ResumeNative }

    // C++: TownCorpseCleanupPolicy.h:7-9
    public static bool EligibleCorpse(bool registeredFighter, bool valid, bool dead, bool consumed)
    {
        return registeredFighter && valid && dead && !consumed;
    }

    // C++: TownCorpseCleanupPolicy.h:11-14
    public static bool HandlerReady(bool valid, bool player, bool dead, bool unconscious,
        bool inCombat, bool carryingSomething)
    {
        return valid && !player && !dead && !unconscious && !inCombat && !carryingSomething;
    }

    // C++: TownCorpseCleanupPolicy.h:16-19
    public static bool FurnaceReady(bool valid, bool sameTown, bool complete, bool disabled,
        bool corpseDisposalFunction)
    {
        return valid && sameTown && complete && !disabled && corpseDisposalFunction;
    }

    // C++: TownCorpseCleanupPolicy.h:21-25
    public static Action NextAction(bool handlerReady, bool furnaceReady,
        bool carryingTarget, bool carryingOther)
    {
        if (!furnaceReady || carryingOther || (!handlerReady && !carryingTarget)) return Action.Wait;
        return carryingTarget ? Action.Feed : Action.PickUp;
    }

    // C++: TownCorpseCleanupPolicy.h:27-29
    public static bool TimedOut(float elapsed, float limit)
    {
        return limit >= 0.0f && elapsed >= limit;
    }

    /// <summary>The C++ takes two <c>const bool*</c> and a count; here two arrays (null = a NULL pointer). As in the C++, a count
    /// longer than an array is not checked (the C++ would read past it; here it throws).</summary>
    // C++: TownCorpseCleanupPolicy.h:31-36
    public static int SelectCorpse(bool[]? eligible, bool[]? resolved, int count, int current)
    {
        if (eligible == null || resolved == null || count <= 0) return -1;
        if (current >= 0 && current < count && eligible[current] && !resolved[current]) return current;
        for (int i = 0; i < count; ++i) if (eligible[i] && !resolved[i]) return i;
        return -1;
    }

    // C++: TownCorpseCleanupPolicy.h:38-41
    public static float AdvanceElapsed(float elapsed, float delta, bool paused)
    {
        if (paused || delta <= 0.0f) return elapsed;
        return elapsed + (delta > 1.0f ? 1.0f : delta);
    }

    // C++: TownCorpseCleanupPolicy.h:43-45
    public static bool BatchTimedOut(bool started, float elapsed, float limit)
    {
        return started && TimedOut(elapsed, limit);
    }

    // C++: TownCorpseCleanupPolicy.h:47-54
    public static ReleaseDisposition ReleaseFor(bool valid, bool player, bool dead,
        bool unconscious, bool inCombat, bool beingCarried, bool needsAid,
        bool carryingSomething)
    {
        if (!valid || player || dead) return ReleaseDisposition.ForgetRelease;
        if (unconscious || inCombat || beingCarried || needsAid || carryingSomething)
            return ReleaseDisposition.DeferRelease;
        return ReleaseDisposition.ResumeNative;
    }
}
