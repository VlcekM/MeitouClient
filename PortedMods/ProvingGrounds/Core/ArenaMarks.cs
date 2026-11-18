// Port of upstream/ProvingGrounds/plugin/src/ArenaMarks.h and ArenaMarks.cpp (Proving Grounds by Shutori, GPLv3).
namespace ProvingGrounds.Core;

/// <summary>Pure per-fighter arena reward payouts (no Character / IO). C++: ArenaMarks.h:1-47, ArenaMarks.cpp:1-207 (PA §2.13,
/// TW §2.5 "Marks for a challenge win"). Reads <c>PGConfig.ChallengeValues()</c> (unique/Skarn bonus multipliers) only.
/// <para>The arrays stand for the C++ pointers, indexed 0..fighterCount-1.</para></summary>
public static class ArenaMarks
{
    // C++: ArenaMarks.h:8-14
    public const int CompletionMarks = 5;
    public const int VictoryMarks = 15;
    public const int MvpMarks = 5;

    public const float DifficultyDivisor = 60.0f;
    public const float DifficultyMin = 0.50f;
    public const float DifficultyMax = 1.50f;

    // C++: ArenaMarks.cpp:10-17
    static float Clamp(float v, float lo, float hi)
    {
        if (v < lo)
            return lo;
        if (v > hi)
            return hi;
        return v;
    }

    // C++: ArenaMarks.cpp:19-34
    static bool FighterWon(SparPodium.Snapshot snap, in SparPodium.FighterRow fighter)
    {
        switch (snap.Outcome)
        {
            case SparPodium.OutcomeKind.TeamAWins:
                return fighter.Team == MatchRules.MatchTeam.TeamA;
            case SparPodium.OutcomeKind.TeamBWins:
                return fighter.Team == MatchRules.MatchTeam.TeamB;
            case SparPodium.OutcomeKind.LastStanding:
                return fighter.Id == snap.LastStandingId;
            default:
                return false;
        }
    }

    // C++: ArenaMarks.cpp:36-77
    static float OpponentAverageCombat(SparPodium.Snapshot snap, int selfIndex, float[]? combatSkill)
    {
        if (combatSkill == null)
            return 0.0f;

        float sum = 0.0f;
        int n = 0;
        ref readonly SparPodium.FighterRow self = ref snap.Fighters[selfIndex];

        for (int i = 0; i < snap.FighterCount; ++i)
        {
            if (i == selfIndex)
                continue;

            ref readonly SparPodium.FighterRow other = ref snap.Fighters[i];
            bool isOpponent = false;
            if (snap.Outcome == SparPodium.OutcomeKind.LastStanding ||
                snap.Mode == MatchRules.MatchMode.LastStanding)
            {
                isOpponent = true;
            }
            else if (self.Team != MatchRules.MatchTeam.None &&
                other.Team != MatchRules.MatchTeam.None &&
                other.Team != self.Team)
            {
                isOpponent = true;
            }

            if (!isOpponent)
                continue;

            sum += combatSkill[i];
            ++n;
        }

        if (n <= 0)
            return combatSkill[selfIndex];
        return sum / (float)n;
    }

    // C++: ArenaMarks.cpp:80-85
    public static bool ShouldAward(SparPodium.OutcomeKind outcome)
    {
        return outcome == SparPodium.OutcomeKind.TeamAWins
            || outcome == SparPodium.OutcomeKind.TeamBWins
            || outcome == SparPodium.OutcomeKind.LastStanding;
    }

    /// <summary>Marks multiplier from self vs average opponent overall combat skill.</summary>
    // C++: ArenaMarks.cpp:87-91
    public static float DifficultyMultiplier(float selfCombat, float opponentAvgCombat)
    {
        float delta = opponentAvgCombat - selfCombat;
        return Clamp(1.0f + delta / DifficultyDivisor, DifficultyMin, DifficultyMax);
    }

    /// <summary>Scales a base payout and floors finished awards at one Mark.</summary>
    // C++: ArenaMarks.cpp:93-106
    public static int ApplyDifficulty(int baseMarks, float selfCombat, float opponentAvgCombat)
    {
        if (baseMarks <= 0)
            return 0;

        float mult = DifficultyMultiplier(selfCombat, opponentAvgCombat);
        float scaled = (float)baseMarks * mult;
        int earned = (int)CMath.RoundHalfAwayFromZero(scaled);   // scaled >= 0.0f ? floorf(scaled + 0.5f) : ceilf(scaled - 0.5f)
        if (earned < 1)
            earned = 1;
        return earned;
    }

    // C++: ArenaMarks.cpp:108-111
    public static float DivisionMultiplier(int division)
    {
        return division == 0 ? 0.75f : division == 2 ? 1.25f : 1.0f;
    }

    // C++: ArenaMarks.cpp:113-116
    public static float RarityMultiplier(int rarity)
    {
        return rarity == 1 ? 1.25f : rarity == 2 ? 1.5f : rarity == 3 ? 2.0f : 1.0f;
    }

    // C++: ArenaMarks.cpp:118-121
    public static float ChallengeMultiplier(int division, int rarity)
    {
        return DivisionMultiplier(division) * RarityMultiplier(rarity);
    }

    // C++: ArenaMarks.cpp:123-133
    public static int UniqueChallengeBonus(int division, bool skarn)
    {
        double multiplier = skarn ? PGConfig.ChallengeValues().SkarnBonusMultiplier :
            PGConfig.ChallengeValues().UniqueBonusMultiplier;
        if (multiplier == 1.0)
            return skarn ? 100 : division == 0 ? 20 : division == 1 ? 40 : division == 2 ? 50 : 0;
        if (skarn)
            return (int)CMath.RoundHalfUp(100.0 * multiplier);   // floor(100.0 * multiplier + 0.5)
        int @base = division == 0 ? 20 : division == 1 ? 40 : division == 2 ? 50 : 0;
        return (int)CMath.RoundHalfUp(@base * multiplier);       // floor(base * multiplier + 0.5)
    }

    /// <summary>Adds a flat bonus after normal multipliers. The bonus is divided among eligible Team A winners; any integer remainder
    /// goes to their MVP (the first such winner when the MVP is not one of them).</summary>
    // C++: ArenaMarks.cpp:135-167
    public static void ApplyWinningTeamBonus(SparPodium.Snapshot snap, bool[]? eligible, int[]? marksEarned, int teamBonus)
    {
        if (eligible == null || marksEarned == null || teamBonus <= 0 ||
            snap.Outcome != SparPodium.OutcomeKind.TeamAWins)
            return;

        int[] winners = new int[16];
        int winnerCount = 0;
        int remainderWinner = -1;
        int mvpId = snap.PodiumCount > 0 ? snap.Podium[0].Fighter.Id : -1;
        for (int i = 0; i < snap.FighterCount && i < 16; ++i)
        {
            if (!eligible[i] || snap.Fighters[i].Team != MatchRules.MatchTeam.TeamA)
                continue;
            winners[winnerCount++] = i;
            if (snap.Fighters[i].Id == mvpId)
                remainderWinner = i;
        }
        if (winnerCount == 0)
            return;
        if (remainderWinner < 0)
            remainderWinner = winners[0];

        int share = teamBonus / winnerCount;
        int remainder = teamBonus % winnerCount;
        for (int i = 0; i < winnerCount; ++i)
            marksEarned[winners[i]] += share;
        marksEarned[remainderWinner] += remainder;
    }

    /// <summary>Writes the Marks earned by each snapshot fighter. Stopped and drawn matches award zero. The visible first podium slot
    /// receives the MVP bonus. combatSkill[i] is overall combat 0-100 for fighters[i]; may be null to treat every matchup as even
    /// (multiplier 1.0). matchMultiplier combines booked division/rarity, applied before the single final rounding step.</summary>
    // C++: ArenaMarks.cpp:169-206 (default argument: ArenaMarks.h:46)
    public static void ComputeAwards(SparPodium.Snapshot snap, float[]? combatSkill, int[]? marksEarned, float matchMultiplier = 1.0f)
    {
        if (marksEarned == null)
            return;

        for (int i = 0; i < snap.FighterCount; ++i)
            marksEarned[i] = 0;

        if (!ShouldAward(snap.Outcome))
            return;

        int mvpId = snap.PodiumCount > 0
            ? snap.Podium[0].Fighter.Id
            : -1;

        for (int i = 0; i < snap.FighterCount; ++i)
        {
            int earned = CompletionMarks;
            if (FighterWon(snap, snap.Fighters[i]))
                earned += VictoryMarks;
            if (snap.Fighters[i].Id == mvpId)
                earned += MvpMarks;

            float multiplier = matchMultiplier;
            if (combatSkill != null)
            {
                float oppAvg = OpponentAverageCombat(snap, i, combatSkill);
                multiplier *= DifficultyMultiplier(combatSkill[i], oppAvg);
            }
            // Round once after all bonuses, preserving zero for stops/draws.
            earned = (int)CMath.RoundHalfUpF(earned * multiplier);   // floorf(earned * multiplier + 0.5f)
            marksEarned[i] = earned < 1 ? 1 : earned;
        }
    }
}
