namespace ProvingGrounds.Core;

/// <summary>C++: SparHitLogic.h, SparHitLogic.cpp (PA §2.8 row 4).</summary>
public static class SparHitLogic
{
    /// <summary>Soft-miss safety net for same-faction spars. Never force-apply a successful dodge (defenderIsDodging).</summary>
    // C++: SparHitLogic.cpp:3-13
    public static bool ShouldForceApplySparMiss(bool resultIsMiss, float damageTotal, bool defenderIsDodging)
    {
        if (!resultIsMiss || damageTotal <= 0.01f)
            return false;
        if (defenderIsDodging)
            return false;
        return true;
    }
}
