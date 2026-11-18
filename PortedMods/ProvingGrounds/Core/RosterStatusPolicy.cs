// Port of upstream/ProvingGrounds/plugin/src/RosterStatusPolicy.h (Proving Grounds by Shutori, GPLv3).
namespace ProvingGrounds.Core;

/// <summary>The roster's health colour band and inline status. C++: RosterStatusPolicy.h.</summary>
public static class RosterStatusPolicy
{
    /// <summary>C++: RosterStatusPolicy.h:7-12</summary>
    public enum Band
    {
        Red,
        Orange,
        Green
    }

    // C++: RosterStatusPolicy.h:14-19
    public static float Clamp(float value)
    {
        if (value < 0.0f) return 0.0f;
        if (value > 1.0f) return 1.0f;
        return value;
    }

    // C++: RosterStatusPolicy.h:21-24
    public static float Recovery(float lowestLimb, float blood)
    {
        return Clamp(lowestLimb < blood ? lowestLimb : blood);
    }

    // C++: RosterStatusPolicy.h:26-29
    public static float LowestLimb(float current, float candidate)
    {
        return candidate < current ? candidate : current;
    }

    // C++: RosterStatusPolicy.h:31-36
    public static Band BandFor(float recovery)
    {
        if (recovery >= 0.9f) return Band.Green;
        if (recovery >= 0.5f) return Band.Orange;
        return Band.Red;
    }

    // C++: RosterStatusPolicy.h:38-41 (`reason.compare(0, 10, "recovering") == 0`: the first 10 characters are exactly "recovering")
    public static bool IsRecoveryReason(string reason)
    {
        return reason.StartsWith("recovering", StringComparison.Ordinal);
    }

    // C++: RosterStatusPolicy.h:43-48
    public static string InlineStatus(bool ready, string reason)
    {
        if (ready) return "Ready";
        if (IsRecoveryReason(reason)) return "Recovering";
        return string.Empty;
    }
}
