// Port of upstream/ProvingGrounds/plugin/src/PrisonerRecruitmentLogic.h and PrisonerRecruitmentLogic.cpp (Proving Grounds by
// Shutori, GPLv3).
namespace ProvingGrounds.Core;

/// <summary>The Arena Marks price of a prisoner's freedom. C++: PrisonerRecruitmentLogic.h, PrisonerRecruitmentLogic.cpp (PA §2.19).</summary>
public static class PrisonerRecruitmentLogic
{
    // Cost in Arena Marks to free one prisoner, from the bound average combat
    // stat of the prisoner. The model -- the marks per stat point, the 5-Mark
    // step, the bounds, and the marks multiplier a configured file may apply on
    // top -- lives in PGConfig, which reads pg_config.json; the shipped
    // numbers are 2.5 marks per point, 25 to 250.
    // C++: PrisonerRecruitmentLogic.cpp:8-27
    public static int RequiredMarks(float averageCombatStat)
    {
        if (averageCombatStat < 0.0f)
            averageCombatStat = 0.0f;
        if (averageCombatStat > 100.0f)
            averageCombatStat = 100.0f;

        PGConfig.Recruitment recruitment = PGConfig.RecruitmentValues();
        float scaled = averageCombatStat * (float)recruitment.CombatScale;
        // port note: (int) of ceilf is in range for every validated config (a zero step is refused by the loader, task 1.6). A NaN
        // stat (from the config's scale, refused by the loader, or from the game's average combat stat, which the clamps above let
        // through) gives MSVC INT_MIN * step and .NET 10 (saturating) 0 * step; both end at the minimum clamp below.
        int cost = unchecked((int)MathF.Ceiling(
            scaled / (float)recruitment.Step) * recruitment.Step);
        if (cost < recruitment.Minimum)
            cost = recruitment.Minimum;
        if (cost > recruitment.Maximum)
            cost = recruitment.Maximum;
        // The multiplier is applied to the bounded price, so it scales the
        // ceiling too: 2.0 doubles every prisoner's price, not only the cheap
        // ones. At the shipped 1.0 this returns the value above unchanged.
        return PGConfig.ScaleMarks(cost);
    }

    // C++: PrisonerRecruitmentLogic.cpp:29-32
    public static bool CanRecruit(float averageCombatStat, int currentMarks)
    {
        return currentMarks >= RequiredMarks(averageCombatStat);
    }
}
